using HarmonyLib;

namespace Wraeclast.Patches;

/// <summary>
/// The death handler uploads the run to Steam: <c>MyPlayer.OnPlayerDied</c> ->
/// <c>Leaderboards.UploadScore</c> -> <c>Leaderboards.GetTomes</c>, which builds
/// an array sized to the native tome list and indexes it by <c>ETome</c>. R3
/// enables the code-side slots (Balance, Gambler, Hoarder) that the native list
/// does not carry, so that array is too short and every death throws
/// <c>IndexOutOfRangeException</c> inside the death path (the run still ends,
/// but the exception is logged and the rest of OnPlayerDied is skipped).
///
/// The leaderboard is hidden by the main-menu patch and the run is local, so the
/// upload is skipped instead: nothing is lost and the death path stays clean.
/// </summary>
[HarmonyPatch(typeof(Assets.Scripts.Steam.Leaderboards))]
internal static class LeaderboardPatches
{
    private static bool logged;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(Assets.Scripts.Steam.Leaderboards.UploadScore))]
    private static bool BeforeUploadScore(int score)
    {
        if (!logged)
        {
            logged = true;
            Plugin.Logger.LogInfo(
                "leaderboards: run upload skipped (our tome slots are outside the native list; the board is hidden)");
        }

        return false;
    }
}
