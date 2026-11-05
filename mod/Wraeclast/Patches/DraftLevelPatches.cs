using System;
using Assets.Scripts.UI.InGame.Rewards;
using HarmonyLib;
using Wraeclast.Data;

namespace Wraeclast.Patches;

/// <summary>
/// 2026-11-05: the level-up draft takes its own level from the screen
/// (<c>LevelupScreen.currentLevel</c>, falling back to the target <c>level</c>).
/// A multi-level XP burst can generate two drafts before the inventory catches
/// up, and the screen is the only thing that knows which of the two it is
/// drawing (110 then 111, not 110 twice).
///
/// The **skill palier gate** does not use this value any more: the screen's
/// counter can also run *ahead* of the character level when a pot's XP lands in
/// one chunk, which offered a weapon upgrade at level 3. <see cref="RewardPool"/>
/// gates on the character level instead; this patch keeps the screen level for
/// the draft's log line and tier.
///
/// The same component drives the sanctuary (Moai) and merchant pickers with a
/// different <c>EEncounter</c>; they draw their own cards without going through
/// <see cref="RewardPool.FillDraft"/>, so every non-level shuffle clears the
/// forced-pick flag and the level override instead of inheriting them.
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
            // A sanctuary (Moai) or merchant picker shares this component but
            // draws its own cards without going through RewardPool.FillDraft.
            // The level-up draft's forced-pick flag would otherwise survive into
            // that screen and lock its refresh/skip/banish buttons, so every
            // non-level shuffles states that the draft on screen is not a skill
            // draft and drops any stale level override.
            if (encounterType != EEncounter.Levelup)
            {
                RewardPool.ClearSkillDraft();
                RewardPool.ClearDraftLevel();
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
