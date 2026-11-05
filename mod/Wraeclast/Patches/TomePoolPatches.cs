using Assets.Scripts._Data;
using Assets.Scripts._Data.Tomes;
using Assets.Scripts.Inventory__Items__Pickups;
using HarmonyLib;
using Wraeclast.Data;

namespace Wraeclast.Patches;

/// <summary>
/// R3, the tome half of "our pools only". The code-side tomes are enabled at
/// load (<see cref="TomePool"/>) but the game reads its private <c>ETome</c>
/// dictionary, so these two small patches answer it for a registered slot:
/// <c>GetTome</c> (level checks, <c>TomeInventory.IsMaxLevel</c>) and
/// <c>GetAllTomes</c> (the draft, <c>UnlockUtility.GetAvailableTomes</c>).
///
/// <c>AddTome</c> is where Balance's missing native effect is applied. Gambler
/// and Hoarder need no hook: the game's own
/// <c>TomeUtility.CheckSpecialTomes</c> already implements them.
/// </summary>
[HarmonyPatch]
internal static class TomePoolPatches
{
    private static bool loggedDraft;
    private static bool loggedSlots;

    internal static void ResetDraftLog() => loggedDraft = false;

    /// <summary>R11b: a skill-upgrade draft is a forced choice. Its refresh
    /// button would call <c>UpgradePicker.ShuffleUpgrades</c> and roll the
    /// game's own pool (tomes) over the three upgrade cards, so the refresh is
    /// refused while the draft on screen is a real skill draft. Refilling it
    /// with the skill's own upgrades would be the alternative; the operator
    /// asked for "one of the three" here.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(LevelupScreen), nameof(LevelupScreen.Refresh))]
    private static bool BeforeLevelupRefresh()
    {
        return !RefuseSkillUtility("refresh");
    }

    /// <summary>R11b: the skip and banish buttons are the other two ways out of
    /// a draft; both are refused while the three skill upgrades are on screen,
    /// so the pick is forced. The native banish path does not work with our
    /// cards anyway.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(LevelupScreen), nameof(LevelupScreen.Skip))]
    private static bool BeforeLevelupSkip()
    {
        return !RefuseSkillUtility("skip");
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LevelupScreen), nameof(LevelupScreen.StartBanish))]
    private static bool BeforeLevelupStartBanish()
    {
        return !RefuseSkillUtility("banish");
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LevelupScreen), nameof(LevelupScreen.Banish))]
    private static bool BeforeLevelupBanish()
    {
        return !RefuseSkillUtility("banish");
    }

    private static bool RefuseSkillUtility(string action)
    {
        if (!RewardPool.LastDraftWasSkill)
        {
            return false;
        }

        Plugin.Logger.LogInfo(
            $"tome pool: {action} refused on a skill-upgrade draft (pick one of the three)");
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DataManager), nameof(DataManager.GetTome))]
    private static bool BeforeGetTome(ETome eTome, ref TomeData __result)
    {
        if (!TomePool.TryGet(eTome, out var data))
        {
            return true;
        }

        __result = data;
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DataManager), nameof(DataManager.GetAllTomes))]
    private static void AfterGetAllTomes(ref Il2CppSystem.Collections.Generic.List<TomeData> __result)
    {
        if (__result == null || TomePool.Count == 0)
        {
            return;
        }

        for (var slot = 0; slot < 27; slot++)
        {
            if (!TomePool.TryGet((ETome)slot, out var data))
            {
                continue;
            }

            var present = false;
            for (var index = 0; index < __result.Count; index++)
            {
                if (__result[index] != null && __result[index].eTome == (ETome)slot)
                {
                    present = true;
                    break;
                }
            }

            if (!present)
            {
                __result.Add(data);
            }
        }
    }

    /// <summary>Balance has no native effect in this build; the card's +2% is
    /// applied through <c>StatInventory.ChangeStat</c> when it is taken.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TomeInventory), nameof(TomeInventory.AddTome))]
    private static void AfterAddTome(TomeData tomeData, ERarity rarity)
    {
        if (tomeData == null || tomeData.eTome != ETome.Balance)
        {
            return;
        }

        BalanceTome.ShowRoll(tomeData, BalanceTome.Grant(rarity));
    }

    /// <summary>R11b follow-up: Gambler and Chaos no longer use the game's random
    /// encounter offer, which can be a downside (the operator got −24% elite
    /// spawn chance from the “twice as hard” card). Gambler boosts one random
    /// beneficial stat at twice the Balance value, Chaos at the base value; the
    /// native effect is skipped for those two and kept for Hoarder.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(TomeUtility), nameof(TomeUtility.CheckSpecialTomes))]
    private static bool BeforeCheckSpecialTomes(TomeData tomeData, ERarity rarity)
    {
        if (tomeData == null)
        {
            return true;
        }

        if (tomeData.eTome == ETome.Gambler)
        {
            BalanceTome.ShowRoll(tomeData, BalanceTome.GrantGambler(rarity));
            return false;
        }

        if (tomeData.eTome == ETome.Chaos)
        {
            BalanceTome.ShowRoll(tomeData, BalanceTome.GrantChaos(rarity));
            return false;
        }

        // The native Hoarder effect spawns a free chest on the map; the
        // operator asked for the item directly (2026-11-05).
        if (tomeData.eTome == ETome.Hoarder)
        {
            RewardPool.GrantHoarderItem("Tome of Hoarding");
            return false;
        }

        return true;
    }

    /// <summary>V5: four tome slots from the first minute. The native count is
    /// <c>2 + ShopItemData.GetLevel(EShopItem 4)</c>, and GoldOnly zeroes every
    /// shop upgrade, so the game drew two usable tome rows and painted the lock
    /// overlay on rows 3 and 4. The design caps a run at four tomes, which is
    /// already <c>GetNumMaxTomeSlots()</c>, so the available count answers the
    /// max. Both the HUD and the pause inventory read this method, and the
    /// existing <c>GetTome</c> prefix already answers our code-side slots, so
    /// the row fills from <c>TomeInventory.tomeLevels</c> with no container
    /// surgery.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(InventoryUtility), nameof(InventoryUtility.GetNumAvailableTomeSlots))]
    private static void AfterGetNumAvailableTomeSlots(ref int __result)
    {
        if (!Plugin.ReplacePools.Value)
        {
            return;
        }

        var max = InventoryUtility.GetNumMaxTomeSlots();
        if (__result >= max)
        {
            return;
        }

        var native = __result;
        __result = max;

        if (!loggedSlots)
        {
            loggedSlots = true;
            Plugin.Logger.LogInfo(
                $"tome slots: {max} available from the start (native {native}, [Items] ReplacePools)");
        }
    }

    /// <summary>One line per run before the first draft: the drawn cards are
    /// tomes (R2 already proved the weapon half) and every one of them is our
    /// sheet. <c>GetRandomUpgrades</c> is the merged list the picker builds;
    /// R5 replaces its cards with the reward table's draw.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(InventoryUtility), nameof(InventoryUtility.GetRandomUpgrades))]
    private static void AfterGetRandomUpgrades(
        Il2CppSystem.Collections.Generic.List<IUpgradable> __result)
    {
        if (__result == null)
        {
            return;
        }

        if (RewardPool.FillDraft(__result, "draft") > 0)
        {
            return;
        }

        if (loggedDraft)
        {
            return;
        }

        loggedDraft = true;

        var ours = 0;
        var foreign = 0;
        for (var index = 0; index < __result.Count; index++)
        {
            var tome = __result[index] == null ? null : __result[index].TryCast<TomeData>();
            if (tome == null)
            {
                continue;
            }

            if (TomeSwap.TryGet(tome.eTome, out _))
            {
                ours++;
            }
            else
            {
                foreign++;
            }
        }

        Plugin.Logger.LogInfo(
            $"tome pool: draft drew {__result.Count} card(s), {ours} our tome(s), {foreign} native tome(s)");
        if (foreign > 0)
        {
            Plugin.Logger.LogWarning($"tome pool: {foreign} native tome(s) reached the draft");
        }
    }
}
