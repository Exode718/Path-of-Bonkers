using System;
using Assets.Scripts._Data;
using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Utility;
using HarmonyLib;
using Wraeclast.Data;
using Wraeclast.Skills;

namespace Wraeclast.Patches;

/// <summary>
/// R6: the hooks the skill-mod layer needs from the game.
///
/// - <c>PlayerInventory.AddUpgrade</c> is where a draft card is consumed. A mod
///   card (a <c>TomeData</c> clone registered by <see cref="SkillMods"/>) adds a
///   stack in the engine and returns false, so the native tome inventory and its
///   statModifier are never touched.
/// - <c>ItemInventory.Cleanup</c> is the run reset: stacks, the storm channel
///   and its root go with the rest of the run state.
/// - <c>UpgradeButton.SetUpgrade</c> paints the draft card; a mod card is
///   repainted Legendary there (R11b item 12).
/// </summary>
[HarmonyPatch]
internal static class SkillModPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.AddUpgrade))]
    private static bool BeforeAddUpgrade(IUpgradable upgradable)
    {
        if (!SkillMods.TryTake(upgradable, out var entry))
        {
            return true;
        }

        SkillMods.Add(entry);
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ItemInventory), nameof(ItemInventory.Cleanup))]
    private static void AfterCleanup()
    {
        SkillMods.Reset();
        PoeSkills.ResetModState();
        PoeSkills.ResetStorm();
    }

    /// <summary>R11b item 12: a skill-upgrade card always reads Legendary.
    /// <c>UpgradeButton.SetUpgrade</c> chooses the frame itself — a card with no
    /// stack stays <c>ERarity.New</c> and one already taken rolls
    /// <c>Rarity.GetUpgradeOfferRarity(luck)</c> — so the same upgrade could be
    /// New, Rare or Epic from one draft to the next. The operator does not want
    /// rarity scaling on these cards (their values are fixed), so a mod card is
    /// repainted after the button is filled: the same fields the method writes
    /// (the rarity label and its colour, the icon border/background, the card
    /// background) take <c>ERarity.Legendary</c>, and the button's own rarity is
    /// set to match what the pick applies.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(UpgradeButton), nameof(UpgradeButton.SetUpgrade))]
    private static void AfterSetUpgrade(UpgradeButton __instance, IUpgradable upgradable)
    {
        try
        {
            var card = upgradable == null ? null : upgradable.TryCast<TomeData>();
            if (card == null || !SkillMods.TryGetCard(card, out _))
            {
                return;
            }

            __instance.rarity = ERarity.Legendary;

            var color = MyColorUtility.RarityToColor(ERarity.Legendary);
            if (__instance.t_rarity != null)
            {
                __instance.t_rarity.text = ERarity.Legendary.ToString();
                __instance.t_rarity.color = color;
            }

            if (__instance.iconBorder != null)
            {
                __instance.iconBorder.color = color;
            }

            if (__instance.iconBackground != null)
            {
                __instance.iconBackground.color = color;
            }

            if (__instance.background != null)
            {
                __instance.background.color =
                    MyColorUtility.GetRarityColorBackground(ERarity.Legendary);
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"skill mod: legendary card repaint failed ({error.Message})");
        }
    }
}
