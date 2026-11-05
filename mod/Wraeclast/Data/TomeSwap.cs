using System;
using System.Collections.Generic;
using Assets.Scripts._Data.Tomes;
using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Menu.Shop;

namespace Wraeclast.Data;

/// <summary>
/// The runtime tome swap: each mapped <c>ETome</c> slot keeps its enum value and
/// its own <c>statModifier</c>, and gets our name and card text. Tome effects
/// are data on <c>TomeData</c> (not code keyed by id, unlike items), so the
/// game's own <c>TomeInventory.AddTome</c> applies them unchanged: the level-up
/// draft, the shop and the chests need no hooks of their own.
///
/// Nothing is added to the enum and no asset is written: a restart restores the
/// base game.
/// </summary>
internal static class TomeSwap
{
    /// <summary>
    /// The highest level a tome reaches by default. The host's own
    /// <c>TomeData.GetMaxLevel</c> answers **5** (Megabonk's cap), which the
    /// operator's design does not want: “les tomes peuvent qu'être lvl 5 max ?
    /// c'est supposé être 99”. The reward table carries the same number as each
    /// tome's <c>maxStacks</c>, so the draft keeps offering a card until the cap
    /// and the game's own `IsMaxLevel` agrees.
    ///
    /// A flat count stat takes its own cap (<c>maxLevel</c> in the sheet):
    /// +1 projectile per stack to 99 would be 99 bolts, so Quantity and Swarm
    /// stop at 5.
    /// </summary>
    internal const int DefaultMaxLevel = 99;

    internal static int MaxLevelFor(TomeDefinition definition) =>
        definition != null && definition.MaxLevel > 0 ? definition.MaxLevel : DefaultMaxLevel;

    private static readonly Dictionary<int, TomeDefinition> BySlot =
        new Dictionary<int, TomeDefinition>();

    internal static bool TryGet(ETome tome, out TomeDefinition definition) =>
        BySlot.TryGetValue((int)tome, out definition);

    internal static void Apply(DataManager manager)
    {
        BySlot.Clear();
        if (manager == null || !Plugin.ReplaceTomes.Value)
        {
            Plugin.Logger.LogInfo("Tome swap off: the base game's tomes stay");
            return;
        }

        var applied = 0;
        TomePool.Clear();

        foreach (var definition in TomeDatabase.All)
        {
            if (!Enum.TryParse(definition.Slot, ignoreCase: false, out ETome tome) ||
                !Enum.IsDefined(typeof(ETome), tome))
            {
                Plugin.Logger.LogWarning($"Tome {definition.Id}: slot {definition.Slot} is not in this build");
                continue;
            }

            // 0.9.111: a **code-side** slot (Balance, Chaos, Gambler, Hoarder) is
            // not in the game's tome dictionary at all, so `GetTome` throws
            // `KeyNotFoundException` out of the IL2CPP trampoline — the managed
            // catch cannot silence it and it printed as an Error at every load
            // (the 0.9.26 loose end). The getter is therefore **not called** for
            // a code-side slot: its asset is enabled from the disabled copy
            // instead. A stat slot with no asset is still a fault.
            TomeData data;
            if (TomePool.CanEnable(definition))
            {
                data = TomePool.Enable(manager, tome);
                if (data == null)
                {
                    Plugin.Logger.LogWarning($"Tome {definition.Slot}: code-side slot could not be enabled");
                    continue;
                }
            }
            else
            {
                try
                {
                    data = manager.GetTome(tome);
                }
                catch (Exception)
                {
                    data = null;
                }

                if (data == null)
                {
                    Plugin.Logger.LogWarning(
                        $"Tome {definition.Slot} is not in the game's tome table; slot skipped");
                    continue;
                }
            }

            try
            {
                data.name = definition.Name;
            }
            catch (Exception error)
            {
                Plugin.Logger.LogWarning($"Tome {tome} could not be renamed: {error.Message}");
                continue;
            }

            var audit = Audit(data, definition);
            var rewrite = Rewrite(data, definition);
            var baseValue = Apply(data, definition);

            try
            {
                data.description = CardText(definition);
            }
            catch (Exception error)
            {
                Plugin.Logger.LogWarning($"Tome {tome} could not write its card: {error.Message}");
            }

            BySlot[(int)tome] = definition;
            applied++;

            Plugin.Logger.LogInfo(
                $"tome {tome} -> {definition.Name}: {audit}{rewrite}{baseValue}, max {MaxLevelFor(definition)}");
        }

        Plugin.Logger.LogInfo($"Tome swap active: {applied}/{TomeDatabase.All.Count} slot(s)");
        ReportMultipliers();
    }

    /// <summary>One line with the game's own rarity multipliers, so a run shows
    /// what a rare or legendary card is actually worth (S3).</summary>
    private static void ReportMultipliers()
    {
        var parts = new List<string>();
        foreach (ERarity rarity in Enum.GetValues(typeof(ERarity)))
        {
            parts.Add($"{rarity} x{Multiplier(rarity):0.##}");
        }

        Plugin.Logger.LogInfo($"tome rarity multipliers: {string.Join(", ", parts)}");
    }

    /// <summary>
    /// A slot whose original meaning is gone (Tome of Silver, with silver cut)
    /// names the stat its card is rewritten to. The native row is audited first
    /// and logged before the write, so the sheet still documents the reference
    /// build and the loader states the live change.
    /// </summary>
    private static string Rewrite(TomeData data, TomeDefinition definition)
    {
        if (definition.RewriteStat < 0)
        {
            return "";
        }

        try
        {
            data.statModifier = new StatModifier
            {
                stat = (EStat)definition.RewriteStat,
                modifyType = (EStatModifyType)definition.RewriteType,
                modification = definition.RewriteValue
            };
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"Tome {definition.Slot}: rewrite failed: {error.Message}");
            return " (rewrite failed)";
        }

        var stat = (EStat)definition.RewriteStat;
        return $", repurposed -> {stat}/{definition.RewriteType}/{definition.RewriteValue:0.###}";
    }

    /// <summary>
    /// S3: records the base value one stack applies and composes the common
    /// card from it. **Nothing is scaled here**: the game's own
    /// <c>TomeData.GetUpgradeOffer(rarity)</c> multiplies the modifier by
    /// <c>Rarity.GetMultiplier(rarity)</c> when the card is taken, so rarity
    /// changes the number per offer and the card has to state that offer's
    /// number (<see cref="CardText(TomeDefinition, ERarity)"/>). Writing a
    /// scaled value here as well multiplied it twice.
    /// </summary>
    private static string Apply(TomeData data, TomeDefinition definition)
    {
        var codeSide = definition.NativeStat < 0;
        var baseValue = codeSide
            ? RewardTable.Value(definition.Id, 0f)
            : definition.RewriteStat >= 0
                ? definition.RewriteValue
                : data.statModifier != null ? data.statModifier.modification : 0f;

        definition.BaseValue = baseValue;
        definition.Card = CardText(definition);

        if (definition.Description == null || !definition.Description.Contains("{value}"))
        {
            // A random-effect card states no number, so there is none to state.
            return $", \"{definition.Card}\"";
        }

        var shown = Number(baseValue * definition.DisplayScale);
        var percent = definition.DisplayScale == 100f ? "%" : "";
        return $", base {shown}{percent} (common) \"{definition.Card}\"";
    }

    /// <summary>The game's own rarity multiplier (1 for common), applied to a
    /// tome's per-stack value. The offer and the card must use the same call,
    /// which is why both go through <see cref="Rarity"/>; a failed lookup is 1.</summary>
    internal static float Multiplier(ERarity rarity)
    {
        try
        {
            return Rarity.GetMultiplier(rarity);
        }
        catch (Exception)
        {
            return 1f;
        }
    }

    /// <summary>The card at the rarity it is being offered at: the offer's own
    /// value (base x multiplier), in display units. A flat stat whose base is a
    /// whole number is rounded the way the stat ends up read (Projectiles,
    /// Life, Regen, Thorns, Shield); a fraction stays exact.</summary>
    internal static string CardText(TomeDefinition definition, ERarity rarity)
    {
        if (definition == null)
        {
            return null;
        }

        var value = definition.BaseValue * Multiplier(rarity);
        if (IsWholeFlat(definition))
        {
            value = (float)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        return Compose(definition.Description, value * definition.DisplayScale);
    }

    /// <summary>The card with no offer at hand (the tome book, the load log):
    /// the common number, which is the sheet's base value.</summary>
    internal static string CardText(TomeDefinition definition) =>
        CardText(definition, ERarity.Common);

    private static bool IsWholeFlat(TomeDefinition definition)
    {
        var type = definition.RewriteStat >= 0 ? definition.RewriteType : definition.NativeType;
        return type == (int)EStatModifyType.Flat &&
               Math.Abs(definition.BaseValue - Math.Round(definition.BaseValue)) < 0.0005f;
    }

    private static string Compose(string template, float shown) =>
        string.IsNullOrEmpty(template) || !template.Contains("{value}")
            ? template
            : template.Replace("{value}", Number(shown));

    private static string Number(float value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The native modifier is the effect the card promises, so log it next to
    /// the sheet's expectation: "ok" means our text states the number the game
    /// will apply, "MISMATCH" means the build retuned the row and the text has
    /// to be updated with it.
    /// </summary>
    private static string Audit(TomeData data, TomeDefinition definition)
    {
        var modifier = data.statModifier;
        var native = modifier == null
            ? "native null"
            : $"native {modifier.stat}/{modifier.modifyType}/{modifier.modification:0.###}";
        if (definition.NativeStat < 0)
        {
            return $"{native} (code-side)";
        }

        if (modifier == null)
        {
            return $"{native} (MISMATCH)";
        }

        var expected = $"{definition.NativeStat}/{definition.NativeType}/{definition.NativeValue:0.###}";
        var matches = (int)modifier.stat == definition.NativeStat &&
                      (int)modifier.modifyType == definition.NativeType &&
                      Math.Abs(modifier.modification - definition.NativeValue) < 0.0005f;

        if (!matches)
        {
            Plugin.Logger.LogWarning(
                $"tome {definition.Slot}: {native} does not match the sheet ({expected}); " +
                "the sheet's native row is stale (the card still states the applied value)");
        }

        return $"{native} ({(matches ? "ok" : "MISMATCH")})";
    }
}
