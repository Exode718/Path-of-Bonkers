using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Assets.Scripts._Data.Tomes;

namespace Wraeclast.Data;

/// <summary>
/// One Path of Exile tome on a Megabonk <c>ETome</c> slot. Field names match
/// <c>assets/tomes.json</c> (authored in <c>mod/data/tomes.json</c>).
///
/// The slot keeps the game's own <c>statModifier</c>: tome effects are plain
/// data on <c>TomeData</c>, already balanced and applied by
/// <c>TomeInventory.AddTome</c>, so there is nothing to re-implement. The
/// numeric fields below are the <c>statModifier</c> this sheet was written
/// against (read from <c>TomeData</c> in the reference install); at load time
/// they are compared with the live asset, so a game update that retunes a tome
/// is reported instead of silently making the card text lie. <c>-1</c> marks
/// the four special slots (Balance, Chaos, Gambler, Hoarder) whose effect is
/// code-side and has no stat to compare.
///
/// <see cref="RewriteStat"/> is -1 on every untouched slot. A slot that no
/// longer has a meaning of its own (Tome of Silver, with silver cut) names the
/// stat the card is rewritten to here; the load audit still checks the native
/// row first, then the pass writes the new modifier and logs it, so the sheet
/// keeps documenting the reference row while the card states what the run
/// actually applies.
/// </summary>
internal sealed class TomeDefinition
{
    public string Slot { get; set; }
    public string Id { get; set; }
    public string Name { get; set; }

    /// <summary>Card text template. <c>{value}</c> is replaced with the number
    /// the run actually applies (base x rarity, in display units), so the card
    /// cannot state a number the stat pipeline does not receive (S3). A row
    /// with no token (the random-effect tomes) is used as written.</summary>
    public string Description { get; set; }

    /// <summary>The number the live modifier is shown with: 100 for fractions
    /// written as percentages, 100 for Armour (a fraction of 100), 1 for flat
    /// counts and metres.</summary>
    public float DisplayScale { get; set; } = 1f;

    /// <summary>Skill shapes this tome helps (<c>projectile</c>, <c>aura</c>...).
    /// Empty means every shape; a list is a hard filter, so a card that does
    /// nothing for the class's skill never enters that class's draft (S3).</summary>
    public List<string> Shapes { get; set; }

    /// <summary>The stack cap of this card. 0 (or absent) means
    /// <see cref="TomeSwap.DefaultMaxLevel"/>. A flat count stat needs its own
    /// cap: +1 projectile per stack cannot run to 99 like a percentage can.</summary>
    public int MaxLevel { get; set; }

    public int NativeStat { get; set; } = -1;
    public int NativeType { get; set; } = -1;
    public float NativeValue { get; set; }
    public int RewriteStat { get; set; } = -1;
    public int RewriteType { get; set; } = -1;
    public float RewriteValue { get; set; }

    /// <summary>The composed card text, set by <see cref="TomeSwap"/> once the
    /// live value is known; null before the swap ran.</summary>
    public string Card { get; set; }

    /// <summary>The value one stack applies **before** the offer's rarity
    /// multiplier. The game's own <c>TomeData.GetUpgradeOffer(rarity)</c>
    /// multiplies it, so this stays the sheet's base and the card states the
    /// rare/legendary number only for the rarity it is shown at (S3).</summary>
    public float BaseValue { get; set; }
}

internal static class TomeDatabase
{
    /// <summary>The shapes <c>PoeSkills.Cast</c> handles. A tome's
    /// <c>shapes</c> list must name these, or the card would be filtered out
    /// for every class (a typo costs a card instead of a whole pool).</summary>
    internal static readonly HashSet<string> KnownShapes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "projectile", "melee", "nova", "chain", "aura", "cyclone", "ground", "orbit"
        };

    private static readonly List<TomeDefinition> Entries = new List<TomeDefinition>();

    private static readonly Dictionary<int, TomeDefinition> ByTome =
        new Dictionary<int, TomeDefinition>();

    internal static IReadOnlyList<TomeDefinition> All => Entries;

    internal static bool TryGet(ETome tome, out TomeDefinition definition) =>
        ByTome.TryGetValue((int)tome, out definition);

    internal static bool Load(string path)
    {
        Entries.Clear();
        ByTome.Clear();

        if (!File.Exists(path))
        {
            Plugin.Logger.LogError($"Tome table missing: {path} (run mod/tools/prepare-data.ps1)");
            return false;
        }

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var tomes = JsonSerializer.Deserialize<List<TomeDefinition>>(File.ReadAllText(path), options);
            if (tomes == null)
            {
                return false;
            }

            foreach (var tome in tomes)
            {
                if (tome == null || string.IsNullOrEmpty(tome.Slot))
                {
                    continue;
                }

                if (!Enum.TryParse(tome.Slot, ignoreCase: false, out ETome slot) ||
                    !Enum.IsDefined(typeof(ETome), slot))
                {
                    Plugin.Logger.LogWarning($"Tome slot {tome.Slot} is not an ETome member in this build");
                    continue;
                }

                ByTome[(int)slot] = tome;
                Entries.Add(tome);

                if (tome.Shapes == null)
                {
                    continue;
                }

                foreach (var shape in tome.Shapes)
                {
                    if (!KnownShapes.Contains(shape ?? string.Empty))
                    {
                        Plugin.Logger.LogWarning(
                            $"Tome {tome.Id}: shape {shape ?? "null"} is not a shape PoeSkills.Cast handles; the card will never be offered");
                    }
                }
            }

            Plugin.Logger.LogInfo($"Tome table: {Entries.Count} tome(s) from {path}");
            return Entries.Count > 0;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Tome table failed to parse: {error}");
            return false;
        }
    }
}
