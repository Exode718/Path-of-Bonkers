using System.Collections.Generic;
using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Inventory__Items__Pickups.Interactables;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using HarmonyLib;
using Wraeclast.Data;

namespace Wraeclast.Patches;

/// <summary>
/// R3, the item half of "our pools only": the run pool is built at
/// <c>RunUnlockables.OnNewRunStarted</c> from the rows <see cref="ItemPool"/>
/// left open, and every item draw entry point is guarded and logged, so a run
/// log states in one place what the draft, chests, the shop and the sanctuary
/// actually offered.
///
/// Draw points (from the IL2CPP dump): the chest and the chest utility both go
/// through <c>ItemUtility.GetRandomChestItem</c>; the in-run merchant uses
/// <c>InventoryUtility.GetRandomItemsShadyGuy</c>; the Moai sanctuary uses
/// <c>InventoryUtility.GetRandomItemsMoai</c>.
/// </summary>
[HarmonyPatch]
internal static class ItemPoolPatches
{
    private static int pooled;
    private static int foreign;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(RunUnlockables), nameof(RunUnlockables.OnNewRunStarted))]
    private static void BeforeRunStart()
    {
        pooled = 0;
        foreign = 0;
        TomePoolPatches.ResetDraftLog();
    }

    /// <summary>The game itself decides what enters the pool; this counts it
    /// and names any native row that survived the sweep.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(RunUnlockables), nameof(RunUnlockables.AddItemToPool))]
    private static void AfterAddItemToPool(ItemData item)
    {
        if (item == null)
        {
            return;
        }

        pooled++;
        if (ItemPool.IsOurs(item.eItem))
        {
            return;
        }

        foreign++;
        Plugin.Logger.LogWarning(
            $"item pool: native {item.eItem} ({ItemPool.Describe(item)}) entered the run pool");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(RunUnlockables), nameof(RunUnlockables.OnNewRunStarted))]
    private static void AfterRunStart()
    {
        if (!ItemPool.Active)
        {
            return;
        }

        Plugin.Logger.LogInfo(
            $"item pool: run pool built with {pooled} card(s), {foreign} native row(s)");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ItemUtility), nameof(ItemUtility.GetRandomChestItem))]
    private static void AfterGetRandomChestItem(EChest chestType, ref ItemData __result)
    {
        var tier = RewardPool.ChestTier(chestType);
        var drawn = RewardPool.DrawRelics(1, tier);
        if (drawn.Count > 0)
        {
            var card = RewardPool.ResolveRelic(drawn[0]);
            if (card != null)
            {
                __result = card;
                Plugin.Logger.LogInfo(
                    $"reward pool: chest {chestType} (tier {tier}) -> {drawn[0].Name} " +
                    $"[{drawn[0].Rarity}/t{drawn[0].Tier}]");
                return;
            }
        }

        __result = ItemPool.Sanitize(__result, "chest");
        ItemPool.Report("chest", __result);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(InventoryUtility), nameof(InventoryUtility.GetRandomItemsMoai))]
    private static void AfterGetRandomItemsMoai(
        Il2CppSystem.Collections.Generic.List<ItemData> __result)
    {
        ItemPool.SanitizeList(__result, "sanctuary");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(InventoryUtility), nameof(InventoryUtility.GetRandomItemsShadyGuy))]
    private static void AfterGetRandomItemsShadyGuy(
        EItemRarity itemRarity,
        Il2CppSystem.Collections.Generic.List<ItemData> __result)
    {
        if (__result == null)
        {
            return;
        }

        // 0.9.112: the merchant's rarity is matched against the **card's own
        // `ItemData.rarity`**, which is what the frame and the colour on screen
        // come from. 0.9.114 restores the host's four bands in our table (epic
        // is no longer folded into rare), so the table and the asset agree for
        // every relic, and the draw can ask for the rarity directly: a rarity
        // request ignores the tier band and the level floor (the host's own
        // draw does the same), which is what let a low-level run fall back to
        // commons on every merchant kind — the tier-3 epics and legendaries
        // were all below their `minLevel` before they could be matched.
        var tier = RewardPool.ShopTier(itemRarity);
        var wanted = RewardPool.ShadyRarity(itemRarity);
        var cards = new List<ItemData>();
        var names = new List<string>();

        if (wanted != null)
        {
            foreach (var entry in RewardPool.DrawRelics(RewardPool.ShopCards, tier, wanted))
            {
                var card = RewardPool.ResolveRelic(entry);
                if (card == null || card.rarity != itemRarity)
                {
                    continue;
                }

                cards.Add(card);
                names.Add($"{entry.Name} [{entry.Rarity}/t{entry.Tier}]");
            }

            if (cards.Count == 0)
            {
                Plugin.Logger.LogInfo(
                    $"reward pool: shop asked for {itemRarity} but no card's own rarity matches; " +
                    "falling back to the whole relic table");
            }
        }

        if (cards.Count == 0)
        {
            foreach (var entry in RewardPool.DrawRelics(RewardPool.ShopCards, tier))
            {
                var card = RewardPool.ResolveRelic(entry);
                if (card == null)
                {
                    continue;
                }

                cards.Add(card);
                names.Add($"{entry.Name} [{entry.Rarity}/t{entry.Tier}]");
            }
        }

        if (cards.Count > 0)
        {
            __result.Clear();
            foreach (var card in cards)
            {
                __result.Add(card);
            }

            Plugin.Logger.LogInfo(
                $"reward pool: shop {itemRarity} (tier {tier}" +
                (wanted != null ? $", {itemRarity} only" : string.Empty) +
                $") -> {cards.Count} card(s): {string.Join(", ", names)}");
            return;
        }

        ItemPool.SanitizeList(__result, "shop");
    }
}
