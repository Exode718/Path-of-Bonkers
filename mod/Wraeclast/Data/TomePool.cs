using System;
using System.Collections.Generic;
using Assets.Scripts._Data.Tomes;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Menu.Shop;
using UnityEngine;

namespace Wraeclast.Data;

/// <summary>
/// The tome pool, R3. The 23 native slots resolve through <c>DataManager</c>,
/// but Balance, Gambler and Hoarder ship as disabled assets and never enter its
/// private <c>ETome</c> dictionary (Chaos is enabled and already resolves). The
/// load pass therefore flips those assets on and registers them here, and the
/// two small patches in <c>TomePoolPatches</c> answer <c>GetTome</c> /
/// <c>GetAllTomes</c> for a registered slot, so the level-up draft can offer it.
///
/// Gambler and Hoarder keep Megabonk's own code-side effect
/// (<c>TomeUtility.CheckSpecialTomes</c>: a random stat offer, a free chest).
/// Balance has no native effect in this build (its <c>statModifier</c> is
/// zero), so <see cref="BalanceTome"/> applies the card's +2% through
/// <c>StatInventory.ChangeStat</c> when the tome is taken.
/// </summary>
internal static class TomePool
{
    private static readonly Dictionary<int, TomeData> Injected =
        new Dictionary<int, TomeData>();

    internal static int Count => Injected.Count;

    internal static bool TryGet(ETome tome, out TomeData data) =>
        Injected.TryGetValue((int)tome, out data);

    internal static void Clear() => Injected.Clear();

    /// <summary>A slot this pass may enable: the reward table names it (its
    /// effect may still be the game's own) but the game's <c>TomeData</c>
    /// dictionary does not answer.</summary>
    internal static bool CanEnable(TomeDefinition definition)
    {
        if (definition == null)
        {
            return false;
        }

        if (RewardTable.All.Count == 0)
        {
            // The table is the R3 source of truth; without it fall back to the
            // sheet's own marker so a run still starts.
            return definition.NativeStat < 0;
        }

        return RewardTable.TryGet(definition.Id, out _);
    }

    /// <summary>
    /// Enables the disabled asset the game shipped for this slot, or creates a
    /// bare <c>TomeData</c> if the list does not carry one. Returns null when
    /// neither works (the caller then keeps the slot out of the pool).
    /// </summary>
    internal static TomeData Enable(DataManager manager, ETome tome)
    {
        var data = Find(manager, tome);
        if (data == null)
        {
            try
            {
                data = ScriptableObject.CreateInstance<TomeData>();
            }
            catch (Exception error)
            {
                Plugin.Logger.LogWarning($"Tome pool: could not create {tome} ({error.Message})");
                return null;
            }

            if (data == null)
            {
                return null;
            }

            data.eTome = tome;
            manager.unsortedTomes?.Add(data);
        }

        try
        {
            data.isEnabled = true;
            if (data.statModifier == null)
            {
                data.statModifier = new StatModifier();
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"Tome pool: could not enable {tome} ({error.Message})");
            return null;
        }

        Injected[(int)tome] = data;
        return data;
    }

    private static TomeData Find(DataManager manager, ETome tome)
    {
        var tomes = manager == null ? null : manager.unsortedTomes;
        if (tomes == null)
        {
            return null;
        }

        for (var index = 0; index < tomes.Count; index++)
        {
            var candidate = tomes[index];
            if (candidate != null && candidate.eTome == tome)
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>
/// Balance's effect, which this build does not implement (the asset is disabled
/// and its <c>statModifier</c> is zero). "+2% to every stat" is applied once per
/// stack through the game's own <c>StatInventory.ChangeStat</c>, so the
/// permanent-modifier pipeline, the stat refresh and the run reset stay native.
///
/// The stat list is ours: every <c>EStat</c> except the run/enemy counters and
/// the values where +2% would be a penalty (chest/shop price, damage taken,
/// fall damage, alignment, weapon cooldown). Those are excluded by
/// <see cref="Excluded"/> rather than by a hardcoded whitelist, so a stat the
/// game adds later is boosted by default.
/// </summary>
internal static class BalanceTome
{
    internal const float DefaultBonus = 0.02f;

    private static readonly HashSet<EStat> Excluded = new HashSet<EStat>
    {
        EStat.Unused0,
        EStat.DamageReductionMultiplier,
        EStat.FallDamageReduction,
        EStat.Difficulty,
        EStat.EliteSpawnIncrease,
        EStat.ChestPriceMultiplier,
        EStat.ShopPriceReduction,
        EStat.Holiness,
        EStat.Wickedness,
        EStat.WeaponBurstCooldown,
        EStat.SilverIncreaseMultiplier,
        EStat.EnemyAmountMultiplier,
        EStat.EnemySizeMultiplier,
        EStat.EnemySpeedMultiplier,
        EStat.EnemyHpMultiplier,
        EStat.EnemyDamageMultiplier,
        EStat.EnemyScalingMultiplier
    };

    /// <summary>Balance has no native effect in this build; the card's bonus is
    /// applied through <c>StatInventory.ChangeStat</c> when it is taken, at the
    /// same rarity multiplier the card states (S3). A <c>Multiplication</c>
    /// modifier is a **factor** (the sheet's 1.10 is +10%), so the applied value
    /// is <c>1 + bonus</c> — passing the raw bonus made every stat 2% of itself,
    /// which is the "all my stats at 2%" the operator saw.</summary>
    internal static string Grant(ERarity rarity)
    {
        try
        {
            var player = MyPlayer.Instance;
            var inventory = player == null ? null : player.inventory;
            var statInventory = inventory == null ? null : inventory.statInventory;
            if (statInventory == null)
            {
                Plugin.Logger.LogWarning("Tome pool: Balance taken with no live stat inventory");
                return null;
            }

            var bonus = RewardTable.Value("tome-balance", DefaultBonus) * TomeSwap.Multiplier(rarity);
            var applied = 0;
            foreach (EStat stat in Enum.GetValues(typeof(EStat)))
            {
                if (!Eligible(stat))
                {
                    continue;
                }

                var modifier = new StatModifier
                {
                    stat = stat,
                    modifyType = EStatModifyType.Multiplication,
                    modification = 1f + bonus
                };

                statInventory.ChangeStat(modifier, permanent: true, timeout: 0f, addToShrineLog: false);
                applied++;
            }

            if (inventory.playerStats != null)
            {
                inventory.playerStats.ForceUpdateStats();
            }

            Plugin.Logger.LogInfo(
                $"tome pool: Balance +{bonus * 100f:0.#}% applied to {applied} stat(s) ({rarity})");
            ShowScore($"+{bonus * 100f:0.#}% to every stat", "All stats", true);
            return $"+{bonus * 100f:0.#}% to every stat";
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"Tome pool: Balance failed: {error.Message}");
            return null;
        }
    }

    /// <summary>Gambler and Chaos, ours instead of the native random *offer*:
    /// the game's <c>EncounterUtility.GetRandomStatOffers</c> can hand out a
    /// downside (the operator got a −24% elite-spawn chance from the “twice as
    /// hard” card), so both now roll from the same beneficial set Balance uses.
    /// **Chaos is the safe read** (one beneficial stat, base value);
    /// **Gambler is the gamble** — a coin flip between a beneficial stat at
    /// twice the value and a malus at the base value. The roll is logged and
    /// written on the card (<see cref="ShowRoll"/>).</summary>
    internal static string GrantGambler(ERarity rarity) => GrantRandom(rarity, 2f, "Gambler", gamble: true);

    internal static string GrantChaos(ERarity rarity) => GrantRandom(rarity, 1f, "Chaos");

    private static string GrantRandom(ERarity rarity, float scale, string label, bool gamble = false)
    {
        try
        {
            var player = MyPlayer.Instance;
            var inventory = player == null ? null : player.inventory;
            var statInventory = inventory == null ? null : inventory.statInventory;
            if (statInventory == null)
            {
                Plugin.Logger.LogWarning($"Tome pool: {label} taken with no live stat inventory");
                return null;
            }

            var eligible = new List<EStat>();
            foreach (EStat stat in Enum.GetValues(typeof(EStat)))
            {
                if (Eligible(stat))
                {
                    eligible.Add(stat);
                }
            }

            if (eligible.Count == 0)
            {
                // A build with no localized stat at all must still roll: fall back
                // to the excluded-filtered set rather than doing nothing.
                foreach (EStat stat in Enum.GetValues(typeof(EStat)))
                {
                    if (!Excluded.Contains(stat))
                    {
                        eligible.Add(stat);
                    }
                }

                Plugin.Logger.LogWarning(
                    $"Tome pool: {label} found no localized stat; falling back to the unfiltered set");
            }

            if (eligible.Count == 0)
            {
                return null;
            }

            var chosen = eligible[UnityEngine.Random.Range(0, eligible.Count)];
            var malus = gamble && UnityEngine.Random.value < 0.5f;
            var bonus = DefaultBonus * (malus ? -1f : scale) * TomeSwap.Multiplier(rarity);

            // Multiplication is a factor (the sheet's 1.10 is +10%): the roll is
            // 1 + bonus, so a malus of 2% is 0.98, never 0.02.
            var modifier = new StatModifier
            {
                stat = chosen,
                modifyType = EStatModifyType.Multiplication,
                modification = 1f + bonus
            };
            statInventory.ChangeStat(
                modifier,
                permanent: true,
                timeout: 0f,
                addToShrineLog: true);

            if (inventory.playerStats != null)
            {
                inventory.playerStats.ForceUpdateStats();
            }

            // The native branch's own display: the score feed entry the game
            // shows for a stat gain. It takes an explicit description and header
            // (the StatModifier overload would print a Multiplication factor as
            // "1,10x" / "0,95x", which reads badly); state the real percentage
            // against the localized stat name instead.
            var amount = $"{bonus * 100f:+0.#;-0.#}%";
            ShowScore(amount, StatName(chosen), bonus > 0f);

            Plugin.Logger.LogInfo(
                $"tome pool: {label} {(bonus > 0f ? "+" : "")}{bonus * 100f:0.#}% {chosen} ({rarity}, " +
                $"{(malus ? "malus roll" : scale > 1f ? "twice as hard" : "one roll")})");

            var statName = StatName(chosen);
            return $"{(bonus > 0f ? "+" : "")}{bonus * 100f:0.#}% {statName}";
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"Tome pool: {label} failed: {error.Message}");
            return null;
        }
    }

    /// <summary>The game's stat-gain feed. The native
    /// <c>TomeUtility.CheckSpecialTomes</c> applies the roll through
    /// <c>StatInventory.ChangeStat</c> and then calls
    /// <c>UiManager.Instance.scoreUi.AddScore(modifier, isPositive, useSfx,
    /// sizeMultiplier)</c> — that is the "+1 Projectile Count" read the operator
    /// wants, not a world popup. Our prefix skips the native branch, so the roll
    /// has to add its own score entry.</summary>
    private static void ShowScore(StatModifier modifier, bool positive)
    {
        try
        {
            var ui = UiManager.Instance;
            var score = ui == null ? null : ui.scoreUi;
            if (score != null)
            {
                score.AddScore(modifier, positive, true, 1f);
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"Tome pool: stat score entry failed: {error.Message}");
        }
    }

    private static void ShowScore(string description, string header, bool positive)
    {
        try
        {
            var ui = UiManager.Instance;
            var score = ui == null ? null : ui.scoreUi;
            if (score != null)
            {
                score.AddScore(description, header, positive, true, 1f);
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"Tome pool: stat score entry failed: {error.Message}");
        }
    }

    /// <summary>A stat the roll may grant: not one of the excluded counters, and
    /// one the game can name. `GetStatName` answers with a
    /// "LOCALIZATION ERROR…" string (it does not throw) for a stat with no
    /// entry, and the score feed prints it as the header — the operator's
    /// "LOCALIZATION ERROR in LocalizationUtility.GetStatName".</summary>
    private static bool Eligible(EStat stat)
    {
        if (Excluded.Contains(stat))
        {
            return false;
        }

        var name = StatName(stat);
        return !string.IsNullOrEmpty(name) &&
               !name.StartsWith("LOCALIZATION ERROR", StringComparison.OrdinalIgnoreCase);
    }

    private static string StatName(EStat stat)
    {
        try
        {
            var name = Assets.Scripts.UI.Localization.LocalizationUtility.GetStatName(stat);
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }
        catch (Exception)
        {
        }

        return stat.ToString();
    }

    /// <summary>Writes the roll on the tome card so the player sees what the
    /// random tome actually did (the game keeps the card's description in the
    /// tome list). Any previous "Last roll:" suffix is replaced, not stacked.</summary>
    internal static void ShowRoll(TomeData data, string roll)
    {
        if (data == null || string.IsNullOrEmpty(roll))
        {
            return;
        }

        var text = data.description ?? string.Empty;
        var cut = text.IndexOf(" Last roll:", StringComparison.Ordinal);
        if (cut >= 0)
        {
            text = text.Substring(0, cut);
        }

        data.description = $"{text} Last roll: {roll}.";
    }
}
