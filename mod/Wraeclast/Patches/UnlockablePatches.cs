using System;
using Assets.Scripts._Data.Tomes;
using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Saves___Serialization.Progression.Achievements;
using HarmonyLib;
using Wraeclast.Data;
using Wraeclast.Skills;

namespace Wraeclast.Patches;

/// <summary>
/// Card and lore text. <c>UnlockableBase.GetName/GetDescription</c> resolve a
/// Unity Localization key, which cannot be rewritten at runtime, so the mapped
/// slots are answered directly from our class and tome tables. Everything else
/// falls through to the game.
///
/// R6's mod cards (a <c>TomeData</c> clone registered by <see cref="SkillMods"/>)
/// are answered first: they reuse the clone's donor slot, so the slot lookup
/// below must not run for them.
/// </summary>
[HarmonyPatch(typeof(UnlockableBase))]
internal static class UnlockableTextPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(UnlockableBase.GetName))]
    private static bool PrefixGetName(UnlockableBase __instance, ref string __result)
    {
        if (__instance is TomeData card && SkillMods.TryGetCard(card, out var upgrade))
        {
            __result = upgrade.Name;
            return false;
        }

        if (__instance is TomeData tome && TomeSwap.TryGet(tome.eTome, out var tomeDefinition))
        {
            __result = tomeDefinition.Name;
            return false;
        }

        if (!(__instance is CharacterData data) ||
            !ClassSwap.TryGetDefinition(data.eCharacter, out var definition))
        {
            return true;
        }

        __result = definition.Name;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(UnlockableBase.GetDescription))]
    private static bool PrefixGetDescription(UnlockableBase __instance, ref string __result)
    {
        if (__instance is TomeData card && SkillMods.TryGetCard(card, out var upgrade))
        {
            __result = upgrade.Text;
            return false;
        }

        if (__instance is TomeData tome && TomeSwap.TryGet(tome.eTome, out var tomeDefinition))
        {
            __result = TomeSwap.CardText(tomeDefinition);
            return false;
        }

        if (!(__instance is CharacterData data) ||
            !ClassSwap.TryGetDefinition(data.eCharacter, out var definition))
        {
            return true;
        }

        __result = definition.Flavour;
        return false;
    }
}

/// <summary>
/// The level-up card's tooltip. <c>TomeData.GetUpgradeDescription</c> composes
/// the localized text with the computed stat line; the mapped slots answer with
/// our card text instead, which names the number the native statModifier
/// applies (the load-time audit checks they still agree).
///
/// A mod card states its own text and its stack count, and offers no stat lines
/// (the effect is applied by the skill layer, not the stat pipeline).
/// </summary>
[HarmonyPatch(typeof(TomeData))]
internal static class TomeCardPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(TomeData.GetUpgradeDescription))]
    private static bool PrefixGetUpgradeDescription(TomeData __instance, ERarity rarity, ref string __result)
    {
        if (SkillMods.TryGetCard(__instance, out var upgrade))
        {
            // R11b: “what this pick gives” — the sheet text plus the next
            // stack's value, composed from the same detail line the Échap list
            // prints. The base text explains the mod; the second line states
            // the value the player would have after taking it.
            var next = SkillMods.Preview(upgrade.Id);
            __result = string.IsNullOrEmpty(next)
                ? upgrade.Text
                : $"{upgrade.Text}\nIf taken: {next}";
            return false;
        }

        if (!TomeSwap.TryGet(__instance.eTome, out var definition))
        {
            return true;
        }

        // The card states the number the offer applies *at this rarity*: the
        // same Rarity.GetMultiplier the game's GetUpgradeOffer uses (S3).
        __result = TomeSwap.CardText(definition, rarity);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(TomeData.GetLevel))]
    private static bool PrefixGetLevel(TomeData __instance, ref int __result)
    {
        if (!SkillMods.TryGetCard(__instance, out var upgrade))
        {
            return true;
        }

        __result = SkillMods.Stacks(upgrade.Id);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(TomeData.GetMaxLevel))]
    private static bool PrefixGetMaxLevel(TomeData __instance, ref int __result)
    {
        if (SkillMods.TryGetCard(__instance, out var upgrade))
        {
            __result = upgrade.MaxStacks;
            return false;
        }

        // 0.9.102: our tomes go to 99, not the host's 5. `TomeInventory.IsMaxLevel`
        // and the card's level line both read this, so one answer covers the
        // draft, the chest, the shop and the pause inventory. A sheet row with
        // its own `maxLevel` (the flat projectile tomes: 5) answers that.
        if (Plugin.ReplaceTomes.Value && TomeSwap.TryGet(__instance.eTome, out var definition))
        {
            __result = TomeSwap.MaxLevelFor(definition);
            return false;
        }

        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(TomeData.GetUpgradeOffer))]
    private static bool PrefixGetUpgradeOffer(TomeData __instance,
        ref Il2CppSystem.Collections.Generic.List<StatModifier> __result)
    {
        if (!SkillMods.TryGetCard(__instance, out _))
        {
            return true;
        }

        __result = new Il2CppSystem.Collections.Generic.List<StatModifier>();
        return false;
    }
}

/// <summary>
/// Item cards. <c>ItemData</c> overrides <c>UnlockableBase.GetName</c> and
/// <c>GetDescription</c>, so the base-class prefix above never reaches an item:
/// these two answer from the relic sheet instead. <c>GetDescription</c> normally
/// delegates to the dummy item, which composes the native card with the donor's
/// own numbers; our text is already written against those numbers (the load-time
/// audit checks them), so the raw text replaces the composed one.
/// </summary>
[HarmonyPatch(typeof(ItemData))]
internal static class ItemCardPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(ItemData.GetName))]
    private static bool PrefixGetName(ItemData __instance, ref string __result)
    {
        if (!ItemSwap.TryGet(__instance.eItem, out var definition))
        {
            return true;
        }

        __result = definition.Name;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ItemData.GetDescription))]
    private static bool PrefixGetDescription(ItemData __instance, ref string __result)
    {
        if (!ItemSwap.TryGet(__instance.eItem, out var definition))
        {
            return true;
        }

        __result = definition.Text;
        return false;
    }
}

/// <summary>The swapped classes are always selectable: no quest gate, no
/// purchase gate. The base game's unlock state is irrelevant to ours.</summary>
[HarmonyPatch(typeof(CharacterData))]
internal static class CharacterDataPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(CharacterData.IsBlackedOutInCharacterSelectionScreen))]
    private static bool PrefixIsBlackedOut(CharacterData __instance, ref bool __result)
    {
        if (!ClassSwap.IsMapped(__instance.eCharacter))
        {
            return true;
        }

        __result = false;
        return false;
    }
}

/// <summary>Removes the "locked" and "buy" overlays from our class cards after
/// the button has been filled in. The button still exists for lookup and
/// navigation; only its purchase state changes.
///
/// The card's name label is set from the label the button built itself, so it
/// is written again here from the class sheet: the grid then shows our class
/// names even if the game's localization table answers first.</summary>
[HarmonyPatch(typeof(MyButtonCharacter))]
internal static class CharacterButtonPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(MyButtonCharacter.SetCharacter))]
    private static void AfterSetCharacter(MyButtonCharacter __instance, CharacterData data)
    {
        if (data == null || !ClassSwap.TryGetDefinition(data.eCharacter, out var definition))
        {
            return;
        }

        try
        {
            if (__instance.t_name != null)
            {
                __instance.t_name.text = definition.Name;
            }

            __instance.canUseCharacter = true;
            if (__instance.unavailableOverlay != null)
            {
                __instance.unavailableOverlay.SetActive(false);
            }

            if (__instance.requiresPurchaseOverlay != null)
            {
                __instance.requiresPurchaseOverlay.SetActive(false);
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"Class card unlock failed: {error.Message}");
        }
    }
}
