using System;
using System.Collections.Generic;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using HarmonyLib;
using Wraeclast.Data;
using Wraeclast.Run;

namespace Wraeclast.Patches;

/// <summary>
/// Read-only probes on <c>DataManager</c> (Assembly-CSharp line ~13048), the
/// game's data registry. These exist to confirm that the interop assembly
/// resolves the game types and that lookups pass through our hooks.
///
/// Nothing is replaced here yet. The next step (one class swap) rewrites a
/// <c>CharacterData</c> and hides the base entries; that needs the field list
/// of <c>CharacterData</c> verified in-game first.
/// </summary>
[HarmonyPatch(typeof(DataManager))]
internal static class DataManagerPatches
{
    // The selection UI re-queries every character several times per frame; log
    // each id once instead of thousands of identical lines.
    private static readonly HashSet<int> SeenCharacters = new HashSet<int>();
    private static readonly HashSet<int> SeenItems = new HashSet<int>();

    [HarmonyPostfix]
    [HarmonyPatch(nameof(DataManager.GetCharacterData))]
    private static void AfterGetCharacterData(ECharacter character, CharacterData __result)
    {
        if (!Plugin.VerboseLogging.Value || !SeenCharacters.Add((int)character))
        {
            return;
        }

        try
        {
            // CharacterData.name is the display name, not the enum name
            // (Cl4nk -> "Clank", SirOofie -> "Knight").
            var name = __result == null ? "null" : __result.name;
            Plugin.Logger.LogInfo($"GetCharacterData({character} = {(int)character}) -> {name}");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"GetCharacterData log failed: {error.Message}");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(DataManager.GetItem))]
    private static void AfterGetItem(EItem item, ItemData __result)
    {
        if (!Plugin.VerboseLogging.Value || !SeenItems.Add((int)item))
        {
            return;
        }

        try
        {
            var name = __result == null ? "null" : __result.name;
            Plugin.Logger.LogInfo($"GetItem({item}) -> {name}");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"GetItem log failed: {error.Message}");
        }
    }

    /// <summary>
    /// Fires once per data load, before any screen reads the lists. This is the
    /// moment our definitions get injected in the next milestone, so log the
    /// counts it will have to work with, plus the roster we remap onto.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(nameof(DataManager.Load))]
    private static void AfterLoad(DataManager __instance)
    {
        try
        {
            var characters = __instance.unsortedCharacterData;
            var weapons = __instance.unsortedWeapons;
            Plugin.Logger.LogInfo(
                "DataManager loaded: " +
                $"characters={characters.Count}, " +
                $"weapons={weapons.Count}, " +
                $"items={__instance.unsortedItems.Count}, " +
                $"unlockables={__instance.unsortedUnlockables.Count}");

            // List order (alphabetical by display name, not enum order) and the name,
            // so the remap table in CharacterSlots.cs can be checked against the build.
            for (var index = 0; index < characters.Count; index++)
            {
                var data = characters[index];
                if (data == null)
                {
                    continue;
                }

                Plugin.Logger.LogInfo($"  index {index}: {data.name}");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"DataManager.Load log failed: {error.Message}");
        }

        try
        {
            ClassSwap.Apply(__instance);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Class swap failed: {error}");
        }

        try
        {
            BossRoster.Load(ModPaths.Bosses);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Boss roster failed: {error}");
        }

        try
        {
            BossSkills.Prime(__instance);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Boss skill kits failed: {error}");
        }

        try
        {
            TomeSwap.Apply(__instance);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Tome swap failed: {error}");
        }

        try
        {
            ItemSwap.Prime(__instance);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Relic mapping failed: {error}");
        }

        try
        {
            RelicEngine.Prime();
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Relic engine failed: {error}");
        }

        try
        {
            ItemPool.Sweep(__instance);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Item pool sweep failed: {error}");
        }

        try
        {
            ForestOnly.Apply(__instance);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Forest only failed: {error}");
        }

        try
        {
            TerrainSwap.Apply(__instance);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Terrain swap failed: {error}");
        }

        try
        {
            WeaponLockPatches.Report();
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"Weapon lock report failed: {error.Message}");
        }

        try
        {
            BossWindow.Report();
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"Boss window report failed: {error.Message}");
        }

        try
        {
            GoldOnly.Report();
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"Gold only report failed: {error.Message}");
        }
    }
}
