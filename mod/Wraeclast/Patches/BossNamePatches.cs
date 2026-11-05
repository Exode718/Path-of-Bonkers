using Actors.Enemies;
using HarmonyLib;
using Wraeclast.Data;

namespace Wraeclast.Patches;

/// <summary>
/// S2 — the boss bar's name.
///
/// <c>EnemyData.GetName</c> resolves the enemy's Unity Localization entry
/// (the key is the <see cref="EEnemy"/> member name: <c>Ent1</c> -> "Lil Bark",
/// <c>MinibossPig</c> -> "Chunkham the Terrible"), so the live bar kept
/// Megabonk's name after the mesh swap. The row that a <see cref="BossRoster"/>
/// spawn tagged answers with our Atlas boss name instead; every other row falls
/// through to the game, and so does every boss while the model layer is off.
/// </summary>
[HarmonyPatch(typeof(EnemyData))]
internal static class BossNamePatches
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(EnemyData.GetName))]
    private static bool PrefixGetName(EnemyData __instance, ref string __result)
    {
        if (!Plugin.ReplaceEnemyModels.Value)
        {
            return true;
        }

        var name = BossRoster.DisplayName(__instance);
        if (name == null)
        {
            return true;
        }

        __result = name;
        return false;
    }
}
