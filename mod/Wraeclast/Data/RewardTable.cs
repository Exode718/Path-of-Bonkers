using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Wraeclast.Data;

/// <summary>
/// One entry of the reward table (<c>mod/data/rewards.json</c>): one card the
/// draft, chests, the boss reward or the shop may offer. Field names match the
/// sheet; the numbers are ours (see <c>mod/tools/data/build-rewards.py</c> and the
/// design section of <c>DOC.md</c>).
///
/// <see cref="Effect"/> and <see cref="Value"/> are R3's fields and keep their
/// meaning: <c>native</c> = the game's own tome data applies it, anything else
/// = our code does (<see cref="TomePool"/> for Balance).
/// </summary>
internal sealed class RewardEntry
{
    public string Kind { get; set; }
    public string Id { get; set; }
    public string Slot { get; set; }
    public string Skill { get; set; }
    public string Name { get; set; }
    public string Text { get; set; }
    public int Tier { get; set; } = 1;
    public string Rarity { get; set; } = "common";
    public float Weight { get; set; } = 100f;
    public int MinLevel { get; set; } = 1;
    public int MaxStacks { get; set; } = 5;
    public string Requires { get; set; }
    /// <summary>R11b follow-up: a mod's role in the draft (offence / defence /
    /// utility, from <c>mods.json</c>). The skill draft guarantees at least one
    /// offence card so a full-DPS path is always available.</summary>
    public string Role { get; set; }
    public string Effect { get; set; }
    public float Value { get; set; }

    internal bool IsTome => string.Equals(Kind, RewardTable.Tome, StringComparison.OrdinalIgnoreCase);

    internal bool IsRelic => string.Equals(Kind, RewardTable.Relic, StringComparison.OrdinalIgnoreCase);

    internal bool IsMod => string.Equals(Kind, RewardTable.Mod, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The mod's own reward table: every tome and every relic, with the tier,
/// rarity, weight and gates the pool reads. One file, so balancing is one edit.
///
/// The load is also an audit. An id must resolve on its own sheet (a tome id in
/// <c>tomes.json</c>, a relic id in <c>items.json</c>), the slot must be a live
/// game enum member, the rarity and tier must be known, and a <c>requires</c>
/// branch must name another row. A bad row is reported and skipped, so a typo
/// costs one card instead of the pool.
/// </summary>
internal static class RewardTable
{
    internal const string Tome = "tome";
    internal const string Relic = "relic";
    internal const string Mod = "mod";

    internal const string Common = "common";
    internal const string Rare = "rare";
    internal const string Epic = "epic";
    internal const string Legendary = "legendary";

    private const string Native = "native";

    /// <summary>Rarity share, ours: the base weight is multiplied by this, so a
    /// legendary is rare without a second table. A run's luck is added on top
    /// (<see cref="EffectiveWeight"/>). The strength of a tome at a rarity is
    /// the game's own <c>Rarity.GetMultiplier</c> (S3), not a second table.</summary>
    private static readonly Dictionary<string, float> RarityScales =
        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            { Common, 1f },
            { Rare, 0.35f },
            { Epic, 0.15f },
            { Legendary, 0.08f }
        };

    private static readonly List<RewardEntry> Entries = new List<RewardEntry>();

    private static readonly Dictionary<string, RewardEntry> ById =
        new Dictionary<string, RewardEntry>(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<RewardEntry> All => Entries;

    internal static bool TryGet(string id, out RewardEntry entry)
    {
        entry = null;
        return !string.IsNullOrEmpty(id) && ById.TryGetValue(id, out entry);
    }

    /// <summary>True when this sheet entry is ours to implement in code.</summary>
    internal static bool IsCodeSide(string id) =>
        TryGet(id, out var entry) &&
        !string.Equals(entry.Effect, Native, StringComparison.OrdinalIgnoreCase);

    internal static float Value(string id, float fallback)
    {
        if (!TryGet(id, out var entry) || entry.Value <= 0f)
        {
            return fallback;
        }

        return entry.Value;
    }

    /// <summary>The draw weight after the rarity share and the run's luck
    /// (<c>effective = weight x rarityScale x (1 + luck)</c>).</summary>
    internal static float EffectiveWeight(RewardEntry entry, float luck)
    {
        if (entry.Weight <= 0f)
        {
            return 0f;
        }

        return entry.Weight * Scale(entry.Rarity) * (1f + Math.Max(0f, luck));
    }

    private static float Scale(string rarity) =>
        rarity != null && RarityScales.TryGetValue(rarity, out var scale) ? scale : 1f;

    internal static bool Load(string path)
    {
        Entries.Clear();
        ById.Clear();

        if (!File.Exists(path))
        {
            Plugin.Logger.LogError($"Reward table missing: {path} (run mod/tools/prepare-data.ps1)");
            return false;
        }

        var skipped = 0;
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var entries = JsonSerializer.Deserialize<List<RewardEntry>>(File.ReadAllText(path), options);
            if (entries == null)
            {
                return false;
            }

            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Id))
                {
                    continue;
                }

                if (!Audit(entry))
                {
                    skipped++;
                    continue;
                }

                Entries.Add(entry);
                ById[entry.Id] = entry;
            }

            var tomes = 0;
            var relics = 0;
            var mods = 0;
            foreach (var entry in Entries)
            {
                if (entry.IsTome)
                {
                    tomes++;
                }
                else if (entry.IsRelic)
                {
                    relics++;
                }
                else if (entry.IsMod)
                {
                    mods++;
                }
            }

            AuditBranches();

            Plugin.Logger.LogInfo(
                $"Reward table: {Entries.Count} entr(ies) from {path} " +
                $"({tomes} tome, {relics} relic, {mods} mod, {skipped} skipped)");
            return Entries.Count > 0;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Reward table failed to parse: {error}");
            return false;
        }
    }

    /// <summary>A branch named by <c>requires</c> must be a row too; checked
    /// after every row is known, so the order in the sheet does not matter.</summary>
    private static void AuditBranches()
    {
        foreach (var entry in Entries)
        {
            if (string.IsNullOrEmpty(entry.Requires))
            {
                continue;
            }

            if (!ById.ContainsKey(entry.Requires))
            {
                Plugin.Logger.LogWarning(
                    $"reward {entry.Id}: requires {entry.Requires}, which is not a row");
            }
        }
    }

    /// <summary>One row against the live sheets and the game's enums.</summary>
    private static bool Audit(RewardEntry entry)
    {
        if (!IsKnownKind(entry.Kind))
        {
            Plugin.Logger.LogWarning(
                $"reward {entry.Id}: kind {entry.Kind ?? "null"} is not tome/relic/mod");
            return false;
        }

        if (!RarityScales.ContainsKey(entry.Rarity ?? string.Empty))
        {
            Plugin.Logger.LogWarning(
                $"reward {entry.Id}: rarity {entry.Rarity ?? "null"} is not common/rare/epic/legendary");
            return false;
        }

        if (entry.Tier < 1 || entry.Tier > 3)
        {
            Plugin.Logger.LogWarning($"reward {entry.Id}: tier {entry.Tier} is outside 1-3");
            return false;
        }

        if (entry.Weight <= 0f)
        {
            Plugin.Logger.LogWarning($"reward {entry.Id}: weight {entry.Weight} is not positive");
            return false;
        }

        if (entry.MaxStacks < 1)
        {
            Plugin.Logger.LogWarning($"reward {entry.Id}: maxStacks {entry.MaxStacks} is not positive");
            return false;
        }

        // Everything else is ours; a tome/relic names its sheet and the sheet is
        // the only thing allowed to answer the slot. A mod names the skill it
        // modifies and carries its own card text, so there is no sheet row.
        if (entry.IsTome)
        {
            var found = false;
            foreach (var tome in TomeDatabase.All)
            {
                if (string.Equals(tome.Id, entry.Id, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                Plugin.Logger.LogWarning($"reward {entry.Id}: no tome row with that id in tomes.json");
                return false;
            }
        }
        else if (entry.IsRelic)
        {
            var found = false;
            foreach (var relic in ItemDatabase.All)
            {
                if (string.Equals(relic.Id, entry.Id, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                Plugin.Logger.LogWarning($"reward {entry.Id}: no relic row with that id in items.json");
                return false;
            }
        }
        else if (entry.IsMod)
        {
            if (string.IsNullOrEmpty(entry.Skill) || !SkillDatabase.TryGet(entry.Skill, out _))
            {
                Plugin.Logger.LogWarning(
                    $"reward {entry.Id}: skill {entry.Skill ?? "null"} is not in skills.json");
                return false;
            }

            if (string.IsNullOrEmpty(entry.Text))
            {
                Plugin.Logger.LogWarning($"reward {entry.Id}: mod has no card text");
                return false;
            }
        }

        return true;
    }

    private static bool IsKnownKind(string kind) =>
        string.Equals(kind, Tome, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, Relic, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, Mod, StringComparison.OrdinalIgnoreCase);
}
