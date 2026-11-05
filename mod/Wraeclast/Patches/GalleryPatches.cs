using Assets.Scripts.Actors;
using Assets.Scripts.Inventory__Items__Pickups;
using HarmonyLib;
using Wraeclast.Dev;

namespace Wraeclast.Patches;

/// <summary>
/// Model-gallery switches (<c>Debug.GalleryKeys</c>). While the gallery is up the
/// player takes no damage and cannot be executed by the boss-window deadline, so
/// the models can be inspected; the boss window itself (countdown, ghost wave)
/// still runs, which is what the 10-minute beat is there to show.
/// </summary>
[HarmonyPatch]
internal static class GalleryPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.DamagePlayer))]
    private static bool BeforeDamagePlayer()
    {
        return !Gallery.Invincible && !TestMode.Invincible;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.DamagePlayerExternal))]
    private static bool BeforeDamagePlayerExternal()
    {
        return !Gallery.Invincible && !TestMode.Invincible;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.KillPlayer))]
    private static bool BeforeKillPlayer()
    {
        // The boss-window deadline sets BypassInvincibility: the run rule wins
        // over the F1 inspect mode, so the floor always resolves.
        return !Gallery.Invincible && (!TestMode.Invincible || TestMode.BypassInvincibility);
    }
}
