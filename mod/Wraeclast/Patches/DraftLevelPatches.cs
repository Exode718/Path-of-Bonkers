using System;
using Assets.Scripts.UI.InGame.Rewards;
using HarmonyLib;
using Wraeclast.Data;

namespace Wraeclast.Patches;

/// <summary>
/// 2026-11-05: the level-up draft must use the level the screen is showing, not
/// the inventory's level. A multi-level XP burst can generate two drafts before
/// the inventory catches up, which produced two skill drafts at 110 and 111
/// instead of 110 then 111 (only 110 is a skill level). The interop exposes
/// <c>LevelupScreen.currentLevel</c> (the one being shown) and <c>level</c>
/// (the target) as properties.
/// </summary>
[HarmonyPatch]
internal static class DraftLevelPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UpgradePicker), nameof(UpgradePicker.ShuffleUpgrades))]
    private static void BeforeShuffle(UpgradePicker __instance, EEncounter encounterType)
    {
        try
        {
            // Only the level-up picker carries a level; a shady-guy/chest
            // picker on the same component must not leave a stale override.
            if (encounterType != EEncounter.Levelup)
            {
                return;
            }

            var screen = __instance == null ? null : __instance.levelupScreen;
            if (screen == null)
            {
                return;
            }

            var current = screen.currentLevel;
            var target = screen.level;
            var level = current > 0 ? current : target;
            if (level > 0)
            {
                RewardPool.SetDraftLevel(level);
                Plugin.Logger.LogInfo(
                    $"draft level: screen current {current}, target {target} -> {level} " +
                    $"(inventory {RewardPool.Level()})");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"draft level: level-up screen unreadable ({error.Message})");
        }
    }
}
