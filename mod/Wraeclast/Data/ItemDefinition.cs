using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Assets.Scripts.Inventory__Items__Pickups.Items;

namespace Wraeclast.Data;

/// <summary>
/// One PoE relic on a Megabonk <c>EItem</c> slot. Field names match
/// <c>assets/items.json</c> (authored in <c>mod/data/items.json</c>).
///
/// A relic comes in one of two kinds:
///
/// - **Donor-backed** (the 67 slots R3 mapped, retired by S5): item effects are
///   code-side (<c>ItemFactory.CreateItem</c> builds an
///   <c>ItemImplementations.Item*</c> per <c>EItem</c> that writes its numbers
///   through <c>ItemBase.SetStat</c>), so the slot kept the donor's effect and
///   <see cref="Text"/> stated it with the donor's own numbers.
///   <see cref="NativeStats"/> recorded the <c>SetStat</c> lines the text was
///   written against and <see cref="Native"/> was the card the slot had to
///   carry. A field the dump does not resolve was left out of the text instead
///   of being invented.
/// - **Engine-owned** (R4's 12 uniques, then every slot in S5): <see cref="Stats"/>
///   and <see cref="Triggers"/> are real, and <see cref="RelicEngine"/> applies
///   them and neutralises the donor class. The text states our numbers.
/// </summary>
internal sealed class ItemDefinition
{
    public string Slot { get; set; }
    public string Id { get; set; }
    public string Name { get; set; }
    public string NativeName { get; set; }
    public string Native { get; set; }
    public string NativeStats { get; set; }
    public string Text { get; set; }

    /// <summary>S5+: the client unique icon's file name under
    /// <c>assets/icons/items/</c>. Empty derives it from <see cref="Id"/>.</summary>
    public string Icon { get; set; }

    internal string IconId => string.IsNullOrEmpty(Icon) ? Id : Icon;

    /// <summary>Stat writes <see cref="RelicEngine"/> applies per stack, in the
    /// game's own units (fractions for percentages, <c>Armor</c> as a fraction
    /// of 100). Empty for a donor-backed relic.</summary>
    public List<RelicStat> Stats { get; set; }

    /// <summary>Event hooks (hit / kill / standstill) the engine owns.</summary>
    public List<RelicTrigger> Triggers { get; set; }

    internal int StatCount => Stats == null ? 0 : Stats.Count;

    internal int TriggerCount => Triggers == null ? 0 : Triggers.Count;

    /// <summary>True when the engine is this relic's effect: the donor class is
    /// then replaced with <c>ItemNoImplementation</c>, so nothing applies twice.</summary>
    internal bool EngineOwned => StatCount > 0 || TriggerCount > 0;
}

/// <summary>One <c>EStat</c> write. <see cref="Stat"/> and <see cref="Type"/>
/// are <c>EStat</c> / <c>EStatModifyType</c> member names, so the sheet says
/// exactly what the game's own stat pipeline receives.</summary>
internal sealed class RelicStat
{
    public string Stat { get; set; }
    public string Type { get; set; }
    public float Value { get; set; }
}

/// <summary>One event hook. <see cref="On"/> is <c>hit</c>, <c>kill</c> or
/// <c>standstill</c>; <see cref="Effect"/> names what the engine does
/// (<c>poison</c>, <c>burn</c>, <c>curse</c>, <c>explode</c>,
/// <c>damageRamp</c>). Unused numbers stay 0.</summary>
internal sealed class RelicTrigger
{
    public string On { get; set; }
    public string Effect { get; set; }
    public float Value { get; set; }
    public float Chance { get; set; }
    public float Radius { get; set; }
    public float Seconds { get; set; }
}

internal static class ItemDatabase
{
    private static readonly List<ItemDefinition> Entries = new List<ItemDefinition>();

    private static readonly Dictionary<int, ItemDefinition> ByItem =
        new Dictionary<int, ItemDefinition>();

    private static readonly Dictionary<string, ItemDefinition> ById =
        new Dictionary<string, ItemDefinition>(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<ItemDefinition> All => Entries;

    internal static bool TryGet(EItem item, out ItemDefinition definition) =>
        ByItem.TryGetValue((int)item, out definition);

    /// <summary>The reward table names a relic by id; the draw asks this to see
    /// whether the relic's stats can help the current class at all.</summary>
    internal static bool TryGet(string id, out ItemDefinition definition)
    {
        definition = null;
        return !string.IsNullOrEmpty(id) && ById.TryGetValue(id, out definition);
    }

    internal static bool Load(string path)
    {
        Entries.Clear();
        ByItem.Clear();
        ById.Clear();

        if (!File.Exists(path))
        {
            Plugin.Logger.LogError($"Item table missing: {path} (run mod/tools/prepare-data.ps1)");
            return false;
        }

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var items = JsonSerializer.Deserialize<List<ItemDefinition>>(File.ReadAllText(path), options);
            if (items == null)
            {
                return false;
            }

            foreach (var item in items)
            {
                if (item == null || string.IsNullOrEmpty(item.Slot))
                {
                    continue;
                }

                if (!Enum.TryParse(item.Slot, ignoreCase: false, out EItem slot) ||
                    !Enum.IsDefined(typeof(EItem), slot))
                {
                    Plugin.Logger.LogWarning($"Item slot {item.Slot} is not an EItem member in this build");
                    continue;
                }

                ByItem[(int)slot] = item;
                if (!string.IsNullOrEmpty(item.Id))
                {
                    ById[item.Id] = item;
                }

                Entries.Add(item);
            }

            Plugin.Logger.LogInfo($"Item table: {Entries.Count} relic(s) from {path}");
            return Entries.Count > 0;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Item table failed to parse: {error}");
            return false;
        }
    }
}
