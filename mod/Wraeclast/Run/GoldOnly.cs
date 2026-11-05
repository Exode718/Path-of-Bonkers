using System;
using System.Collections.Generic;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Inventory__Items__Pickups.GoldAndMoney;
using Assets.Scripts.Saves___Serialization.SaveFiles;
using Assets.Scripts._Data.ShopItems;
using HarmonyLib;
using UnityEngine;

namespace Wraeclast.Run;

/// <summary>
/// W3 — gold only. Megabonk carries two currencies: gold, which is the run's
/// chest money, and silver, which is the meta currency saved in
/// <c>ProgressionSaveFile.silver</c> and spent in the shop window (the Sanctum)
/// on permanent upgrades. The design has no meta progression, so silver is cut
/// from both ends and the Sanctum is closed:
///
/// - <b>Earning</b>: every silver source is refused at
///   <c>MoneyUtility.SpawnSilver</c> (enemy drops and tumbleweeds) and
///   <c>MoneyUtility.SpawnSilverNoTimerImpact</c> (silver pots), and
///   <c>PlayerInventory.AddSilver</c> refuses the run credit in case a coin from
///   an older pool is already flying.
/// - <b>Saving</b>: <c>ProgressionSaveFile.AddSilver</c> is refused, so no run,
///   achievement or challenge can grow the saved value.
/// - <b>Spending</b>: <c>ProgressionSaveFile.PurchaseShopItem</c> and
///   <c>RefundShopItem</c> answer false and <c>ShopWindow.Buy</c>/<c>Refund</c>
///   return early, so the Sanctum can never take silver. The saved balance is
///   zeroed when the save loads and every purchased level reads as 0 through
///   <c>ShopItemData.GetLevel</c>, so the old meta benefits (extra draft
///   rerolls, skip/banish charges, gold/silver multipliers) are gone without
///   touching the save's dictionary or risking a missing-key read in the UI.
/// - <b>Showing</b>: the silver counters (HUD, kill/gold counter, death screen,
///   final-swarm multiplier) are hidden and the menu's shop button and tab are
///   switched off, so nothing advertises a currency that no longer exists.
///
/// Gold is untouched: chests still cost gold, gold still drops and the chest
/// price still rises per purchase. <c>[Run] GoldOnly</c> is the switch.
/// </summary>
[HarmonyPatch]
internal static class GoldOnly
{
    private static readonly HashSet<int> RefusedShopItems = new HashSet<int>();
    private static bool loggedDrops;
    private static bool loggedRunSilver;
    private static bool loggedSavedSilver;
    private static bool loggedUi;
    private static bool loggedMenu;

    internal static bool Active => Plugin.GoldOnlyCurrency.Value;

    /// <summary>One load-time line so a run log states the economy in force.</summary>
    internal static void Report()
    {
        Plugin.Logger.LogInfo(Active
            ? "gold only: silver is cut (drops, run credit, achievements) and the Sanctum is closed; gold stays the chest currency"
            : "gold only off: Megabonk's silver and the Sanctum shop stay");
    }

    private static void LogOnce(ref bool flag, string message)
    {
        if (flag)
        {
            return;
        }

        flag = true;
        Plugin.Logger.LogInfo(message);
    }

    // ---- earning -----------------------------------------------------------

    /// <summary>Enemy silver drop. Private in the build, hence the string name.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(MoneyUtility), "SpawnSilver", new[] { typeof(Enemy) })]
    private static bool BeforeSpawnSilverFromEnemy()
    {
        if (!Active)
        {
            return true;
        }

        LogOnce(ref loggedDrops, "gold only: silver drops disabled (enemy, tumbleweed, pot)");
        return false;
    }

    /// <summary>Tumbleweed silver (the world interactable calls this overload).
    /// The parameters are named after the interop stub (<c>pos</c>), never from
    /// the design: Harmony resolves them by name and throws on a mismatch.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(MoneyUtility), nameof(MoneyUtility.SpawnSilver), new[] { typeof(Vector3) })]
    private static bool BeforeSpawnSilverAt(Vector3 pos)
    {
        return !Active;
    }

    /// <summary>Silver pot: the pot breaks, the silver does not.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(MoneyUtility), nameof(MoneyUtility.SpawnSilverNoTimerImpact))]
    private static bool BeforeSpawnSilverNoTimerImpact(int amount, Vector3 pos)
    {
        return !Active;
    }

    /// <summary>
    /// The run credit. A coin pooled before the drop blocks were installed can
    /// still reach the player, so the one entry point into the run's silver is
    /// closed as well.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.AddSilver))]
    private static bool BeforeAddSilver(int amount)
    {
        if (!Active)
        {
            return true;
        }

        LogOnce(ref loggedRunSilver, "gold only: run silver refused (no meta currency)");
        return false;
    }

    // ---- saving ------------------------------------------------------------

    /// <summary>The saved balance (achievements, challenges, legacy runs).</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ProgressionSaveFile), nameof(ProgressionSaveFile.AddSilver))]
    private static bool BeforeSaveSilver(int change)
    {
        if (!Active)
        {
            return true;
        }

        LogOnce(ref loggedSavedSilver, "gold only: saved silver refused (no meta currency)");
        return false;
    }

    /// <summary>
    /// Normalise a save when it loads: the economy is gold only, so a balance
    /// carried from an earlier build has no meaning. The Sanctum levels stay in
    /// the dictionary (a missing key would throw in the game's own
    /// <c>GetLevel</c> indexer); they read as 0 through the postfix below.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ProgressionSaveFile), nameof(ProgressionSaveFile.Init))]
    private static void AfterProgressionInit(ProgressionSaveFile __instance)
    {
        if (!Active || __instance == null)
        {
            return;
        }

        try
        {
            var silver = __instance.silver;
            if (silver != 0)
            {
                __instance.silver = 0;
                Plugin.Logger.LogInfo(
                    $"gold only: save normalised, silver {silver} -> 0 (no meta currency)");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"gold only: normalising the save failed: {error.Message}");
        }
    }

    // ---- spending ----------------------------------------------------------

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ProgressionSaveFile), nameof(ProgressionSaveFile.PurchaseShopItem))]
    private static bool BeforePurchaseShopItem(ShopItemData shopItemData)
    {
        if (!Active)
        {
            return true;
        }

        var slot = shopItemData == null ? -1 : (int)shopItemData.eShopItem;
        if (RefusedShopItems.Add(slot))
        {
            var name = shopItemData == null ? "?" : shopItemData.name;
            Plugin.Logger.LogInfo($"gold only: Sanctum purchase refused ({slot} {name})");
        }

        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ProgressionSaveFile), nameof(ProgressionSaveFile.RefundShopItem))]
    private static bool BeforeRefundShopItem(ShopItemData shopItemData)
    {
        return !Active;
    }

    /// <summary>The window's own buttons, so a click never reaches the model.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ShopWindow), nameof(ShopWindow.Buy))]
    private static bool BeforeShopBuy()
    {
        return !Active;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ShopWindow), nameof(ShopWindow.Refund))]
    private static bool BeforeShopRefund()
    {
        return !Active;
    }

    /// <summary>
    /// Every purchased Sanctum level reads from <see cref="Comfort"/>: the three
    /// comfort upgrades are maxed (silver cannot buy anything any more, so they
    /// are simply granted), everything else reads 0. The switch that consumes
    /// them — <c>InitSkipRefreshBanish</c> — reads
    /// <c>ProgressionSaveFile.GetShopItemLevel</c>, which indexes the save's
    /// dictionary directly and throws on a missing key, so that one is answered
    /// in a **prefix** and the dictionary is never touched.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ProgressionSaveFile), nameof(ProgressionSaveFile.GetShopItemLevel))]
    private static bool BeforeShopItemLevel(EShopItem eShopItem, ref int __result)
    {
        if (!Active)
        {
            return true;
        }

        __result = Comfort(eShopItem);
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ShopItemData), nameof(ShopItemData.GetLevel))]
    private static void AfterShopItemGetLevel(ShopItemData __instance, ref int __result)
    {
        if (Active)
        {
            __result = Comfort(__instance == null ? EShopItem.Weapons : __instance.eShopItem);
        }
    }

    /// <summary>The three upgrades that make a run less tedious: refresh, skip
    /// and banish. `ShopItemData` gives each of them `maxLevel` 6, so this is
    /// the game's own cap, not a number of ours.</summary>
    private const int ComfortLevel = 6;

    private static int Comfort(EShopItem item) =>
        item == EShopItem.Refresh || item == EShopItem.Skip || item == EShopItem.Banish
            ? ComfortLevel
            : 0;

    // ---- showing -----------------------------------------------------------

    /// <summary>The menu's shop button and tab open the Sanctum.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(MainMenu), "Start")]
    private static void AfterMainMenuStart(MainMenu __instance)
    {
        HideShopEntries(__instance);
    }

    /// <summary>The menu re-applies button visibility when it comes back from a screen.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(MainMenu), "RefreshButtons")]
    private static void AfterMainMenuRefresh(MainMenu __instance)
    {
        HideShopEntries(__instance);
    }

    private static void HideShopEntries(MainMenu menu)
    {
        if (!Active || menu == null)
        {
            return;
        }

        try
        {
            var hidden = 0;
            hidden += Hide(menu.btnShop) ? 1 : 0;
            hidden += Hide(menu.tabShop) ? 1 : 0;

            if (hidden > 0)
            {
                LogOnce(ref loggedMenu, $"gold only: {hidden} Sanctum menu entr(ies) hidden");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"gold only: hiding the Sanctum menu failed: {error.Message}");
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MainMenu), nameof(MainMenu.GoToShop))]
    private static bool BeforeGoToShop()
    {
        return !Active;
    }

    /// <summary>The in-run HUD's silver counter.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(KillsAndGoldCounter), "Start")]
    private static void AfterGoldCounterStart(KillsAndGoldCounter __instance)
    {
        if (!Active || __instance == null)
        {
            return;
        }

        try
        {
            if (Hide(__instance.t_silver))
            {
                LogOnce(ref loggedUi, "gold only: silver counter(s) hidden");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"gold only: hiding the kill counter silver failed: {error.Message}");
        }
    }

    /// <summary>The XP/gold HUD's silver entry.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(XpAndGoldHUD), "Awake")]
    private static void AfterXpAndGoldHudAwake(XpAndGoldHUD __instance)
    {
        if (!Active || __instance == null)
        {
            return;
        }

        try
        {
            var text = __instance.silverText;
            if (text != null)
            {
                Hide(text.gameObject);
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"gold only: hiding the HUD silver failed: {error.Message}");
        }
    }

    /// <summary>The death screen's silver line and the animated silver tally.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeathStatsUi), "Start")]
    private static void AfterDeathStatsStart(DeathStatsUi __instance)
    {
        if (!Active || __instance == null)
        {
            return;
        }

        try
        {
            Hide(__instance.t_silver);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"gold only: hiding the death screen silver failed: {error.Message}");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GoldCounter), "Start")]
    private static void AfterSilverCounterStart(GoldCounter __instance)
    {
        if (!Active || __instance == null)
        {
            return;
        }

        try
        {
            Hide(__instance.t_silver);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"gold only: hiding the menu silver failed: {error.Message}");
        }
    }

    /// <summary>The final swarm's "silver x1.2" badge; the wave itself stays.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(FinalSwarmSilverUi), "Start")]
    private static void AfterFinalSwarmStart(FinalSwarmSilverUi __instance)
    {
        if (!Active || __instance == null)
        {
            return;
        }

        try
        {
            Hide(__instance.coin);
            Hide(__instance.t_silverMultiplier);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"gold only: hiding the final swarm silver failed: {error.Message}");
        }
    }

    private static bool Hide(UnityEngine.Object target)
    {
        if (target == null)
        {
            return false;
        }

        try
        {
            if (target is Component component)
            {
                component.gameObject.SetActive(false);
            }
            else if (target is GameObject gameObject)
            {
                gameObject.SetActive(false);
            }
            else
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
