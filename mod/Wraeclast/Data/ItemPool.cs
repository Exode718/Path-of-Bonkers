using System;
using System.Collections.Generic;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Wraeclast.Data;

/// <summary>
/// The item pool, R3: our relics only. Megabonk builds the run's item pool in
/// <c>RunUnlockables.OnNewRunStarted</c> from <c>DataManager.unsortedItems</c>,
/// keeping a row only when <c>ItemData.inItemPool</c> is set. The load pass
/// closes every row and opens back the audited relic slots, so the chest, the
/// shop and the Moai sanctuary can only draw a card from
/// <c>mod/data/items.json</c>.
///
/// The native pool still has two side doors — the legendary-luck roll can hand
/// back the unmapped Golden Ring (<c>ItemUtility.GetRandomItemFromRarity</c>),
/// and the sanctuary/shop paths build their own lists — so every draw entry
/// point is also guarded: a foreign result is replaced with one of ours and
/// logged, never shown.
/// </summary>
internal static class ItemPool
{
    private static readonly List<ItemData> Relics = new List<ItemData>();

    private static readonly HashSet<int> RelicSlots = new HashSet<int>();

    private static readonly HashSet<int> Refused = new HashSet<int>();

    internal static int Count => Relics.Count;

    /// <summary>True once a sweep actually replaced the pool; every guard and
    /// log below follows this instead of re-reading the config.</summary>
    internal static bool Active { get; private set; }

    internal static bool IsOurs(EItem item) => RelicSlots.Contains((int)item);

    internal static void Sweep(DataManager manager)
    {
        Relics.Clear();
        RelicSlots.Clear();
        Refused.Clear();
        Active = false;

        if (manager == null || !Plugin.ReplacePools.Value)
        {
            Plugin.Logger.LogInfo(Plugin.ReplacePools.Value
                ? "item pool: no DataManager, sweep skipped"
                : "item pool off: Megabonk's item cards stay in every pool");
            return;
        }

        if (!Plugin.ReplaceItems.Value)
        {
            Plugin.Logger.LogInfo("item pool: item swap is off, so the sweep is off too");
            return;
        }

        var items = manager.unsortedItems;
        if (items == null)
        {
            Plugin.Logger.LogWarning("item pool: DataManager has no item list, sweep skipped");
            return;
        }

        var original = new List<KeyValuePair<ItemData, bool>>();
        var closed = 0;
        for (var index = 0; index < items.Count; index++)
        {
            var data = items[index];
            if (data == null)
            {
                continue;
            }

            original.Add(new KeyValuePair<ItemData, bool>(data, data.inItemPool));
            if (data.inItemPool)
            {
                closed++;
            }

            data.inItemPool = false;
        }

        for (var index = 0; index < items.Count; index++)
        {
            var data = items[index];
            if (data == null || !ItemSwap.TryGet(data.eItem, out _))
            {
                continue;
            }

            data.inItemPool = true;
            MakeAvailable(data);
            Relics.Add(data);
            RelicSlots.Add((int)data.eItem);
        }

        if (Relics.Count == 0)
        {
            // Never leave a live game with an empty pool: an empty pool would
            // roll null cards in every chest and sanctuary.
            foreach (var pair in original)
            {
                pair.Key.inItemPool = pair.Value;
            }

            Plugin.Logger.LogError("item pool: no relic audited ok, sweep reverted to Megabonk's rows");
            return;
        }

        Plugin.Logger.LogInfo(
            $"item pool: {closed} native row(s) closed, {Relics.Count} relic(s) live (our cards only)");
        Active = true;
    }

    /// <summary>
    /// Our relics are not Megabonk unlocks: a native `price` puts the row behind
    /// the save's purchased set and an `unlockRequirement` behind an achievement,
    /// both of which hide it from <c>MyAchievements.IsAvailable</c> and shrink
    /// the pool (the first live run built 38 of 67). Clearing the gate, the
    /// achievement and the price is the item-side twin of what R1 does for the
    /// class cards; the design has no meta progression.
    /// </summary>
    private static void MakeAvailable(ItemData data)
    {
        try
        {
            data.isEnabled = true;
            data.price = 0;
            data.unlockRequirement = null;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning(
                $"item pool: could not unlock {data.eItem} ({error.Message})");
        }
    }

    /// <summary>A foreign card is swapped for one of ours and reported once per
    /// slot. The pool is never empty here (the sweep reverts instead), but a
    /// null pool still falls back to the original card rather than a crash.</summary>
    internal static ItemData Sanitize(ItemData item, string source)
    {
        if (item == null || !Active || IsOurs(item.eItem))
        {
            return item;
        }

        var replacement = Relics.Count == 0 ? null : Relics[Random.Range(0, Relics.Count)];
        if (Refused.Add((int)item.eItem))
        {
            Plugin.Logger.LogWarning(
                $"item pool: {source} drew native {item.eItem} ({AssetName(item)}); " +
                (replacement == null ? "no relic to swap in" : $"swapped for {Describe(replacement)}"));
        }

        return replacement ?? item;
    }

    internal static void SanitizeList(
        Il2CppSystem.Collections.Generic.List<ItemData> list,
        string source)
    {
        if (list == null || !Active)
        {
            return;
        }

        for (var index = 0; index < list.Count; index++)
        {
            list[index] = Sanitize(list[index], source);
        }

        Report(source, list);
    }

    internal static void Report(string source, ItemData item)
    {
        if (!Active || item == null)
        {
            return;
        }

        Plugin.Logger.LogInfo($"{source} draw -> {Describe(item)}");
    }

    private static void Report(
        string source,
        Il2CppSystem.Collections.Generic.List<ItemData> list)
    {
        var names = new List<string>();
        for (var index = 0; index < list.Count; index++)
        {
            if (list[index] != null)
            {
                names.Add(Describe(list[index]));
            }
        }

        Plugin.Logger.LogInfo($"{source} draw -> {names.Count} card(s): {string.Join(", ", names)}");
    }

    internal static string Describe(ItemData item)
    {
        if (item == null)
        {
            return "null";
        }

        return ItemSwap.TryGet(item.eItem, out var relic)
            ? $"{relic.Name} [{item.eItem}]"
            : $"native {item.eItem} ({AssetName(item)})";
    }

    private static string AssetName(ItemData item)
    {
        try
        {
            return string.IsNullOrEmpty(item.name) ? "?" : item.name;
        }
        catch (Exception)
        {
            return "?";
        }
    }
}
