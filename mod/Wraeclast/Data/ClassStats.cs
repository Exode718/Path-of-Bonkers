using System;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Menu.Shop;
using Il2CppSystem.Collections.Generic;

namespace Wraeclast.Data;

/// <summary>
/// Translates our class sheet (<c>classes.json</c>) into the game's own
/// <see cref="StatModifier"/> list, so HP, damage, crit and movement keep
/// flowing through Megabonk's stat pipeline (and its UI) instead of a parallel
/// system.
///
/// The baselines below are OUR design units, taken from the old Godot reference
/// (now in git history) and <c>mod/design/arena/classes.json</c>: 100 life, 100
/// move speed, 100% damage, 90 pickup radius. They are not Megabonk values.
/// Every Megabonk side number (base HP, armour meaning) stays untouched: our
/// sheet is applied as a ratio on top of it.
/// </summary>
internal static class ClassStats
{
    private const float LifeBaseline = 100f;
    private const float MoveSpeedBaseline = 100f;
    private const float PickupBaseline = 90f;

    internal static void Apply(CharacterData data, ClassDefinition definition)
    {
        var modifiers = data.statModifiers;
        if (modifiers == null)
        {
            modifiers = new List<StatModifier>();
            data.statModifiers = modifiers;
        }
        else
        {
            modifiers.Clear();
        }

        Add(modifiers, EStat.MaxHealth, EStatModifyType.Multiplication, definition.Life / LifeBaseline);
        Add(modifiers, EStat.HealthRegen, EStatModifyType.Flat, definition.Regen);
        Add(modifiers, EStat.Armor, EStatModifyType.Flat, definition.Armour / 100f);
        Add(modifiers, EStat.MoveSpeedMultiplier, EStatModifyType.Multiplication, definition.MoveSpeed / MoveSpeedBaseline);
        Add(modifiers, EStat.DamageMultiplier, EStatModifyType.Addition, definition.DamagePct / 100f);
        Add(modifiers, EStat.CritChance, EStatModifyType.Flat, definition.CritChance / 100f);
        Add(modifiers, EStat.PickupRange, EStatModifyType.Multiplication, definition.PickupRadius / PickupBaseline);
        ApplyPassive(modifiers, definition.Passive);
        ApplyPerLevel(modifiers, definition.Passive, level: 1);
        ApplyWeapon(modifiers, definition.StarterWeapon, definition.Id);
        ApplyWeapon(modifiers, definition.Offhand, definition.Id);
        ApplyWeaponTraits(definition);
    }

    /// <summary>The starter unique's own <c>bonus</c> block, which the sheet
    /// carried but nothing read until 2026-11-05. It goes through the same
    /// <see cref="StatModifier"/> pipeline as the class sheet, so the weapon
    /// bonus and the class bonus are additive where the game says so
    /// (<c>damagePct</c> + <c>damagePct</c>) and multiplicative only for
    /// <c>maxLifePct</c>.</summary>
    private static void ApplyWeapon(
        List<StatModifier> modifiers,
        ClassDefinition.WeaponDefinition weapon,
        string className)
    {
        if (weapon == null || weapon.Bonus == null)
        {
            return;
        }

        foreach (var pair in weapon.Bonus)
        {
            switch (pair.Key)
            {
                case "damagePct":
                    Add(modifiers, EStat.DamageMultiplier, EStatModifyType.Addition, pair.Value / 100f);
                    break;
                case "areaPct":
                    Add(modifiers, EStat.SizeMultiplier, EStatModifyType.Addition, pair.Value / 100f);
                    break;
                case "armour":
                    Add(modifiers, EStat.Armor, EStatModifyType.Flat, pair.Value / 100f);
                    break;
                case "moveSpeedPct":
                    Add(modifiers, EStat.MoveSpeedMultiplier, EStatModifyType.Addition, pair.Value / 100f);
                    break;
                case "critChance":
                    Add(modifiers, EStat.CritChance, EStatModifyType.Flat, pair.Value / 100f);
                    break;
                case "maxLifePct":
                    Add(modifiers, EStat.MaxHealth, EStatModifyType.Multiplication, 1f + pair.Value / 100f);
                    break;
                case "regen":
                    Add(modifiers, EStat.HealthRegen, EStatModifyType.Flat, pair.Value);
                    break;
                case "projectileSpeedPct":
                    Add(modifiers, EStat.ProjectileSpeedMultiplier, EStatModifyType.Addition, pair.Value / 100f);
                    break;
                default:
                    Plugin.Logger.LogWarning(
                        $"{className}: weapon {weapon.Id} has an unmapped bonus \"{pair.Key}\" (skipped)");
                    break;
            }
        }
    }

    /// <summary>The starter weapon's ailment/chain traits, merged once into the
    /// class skill's own <see cref="SkillTraits"/>. The chances add, the
    /// durations/jumps use the larger value, and a poison radius uses the
    /// larger one (the skill's 3.5 m already covers Bino's 3.2 m).</summary>
    private static void ApplyWeaponTraits(ClassDefinition definition)
    {
        var weapon = definition.StarterWeapon;
        if (weapon == null || weapon.Traits == null || weapon.Traits.Count == 0)
        {
            return;
        }

        var key = definition.Id + "/" + definition.SkillId + "/" + weapon.Id;
        if (!WeaponTraitsApplied.Add(key))
        {
            return;
        }

        if (!SkillDatabase.TryGet(definition.SkillId, out var skill))
        {
            Plugin.Logger.LogWarning($"{definition.Id}: skill {definition.SkillId} is missing for weapon traits");
            return;
        }

        var traits = skill.Traits;
        if (traits == null)
        {
            traits = new SkillTraits();
            skill.Traits = traits;
        }

        foreach (var pair in weapon.Traits)
        {
            switch (pair.Key)
            {
                case "ignitePct":
                    traits.IgnitePct += pair.Value;
                    break;
                case "igniteSeconds":
                    traits.IgniteSeconds = Math.Max(traits.IgniteSeconds, pair.Value);
                    break;
                case "chainChance":
                    // The weapon and the skill both state a chain chance; the
                    // larger one is the trait, not 30 + 30 = 60.
                    traits.ChainChance = Math.Max(traits.ChainChance, pair.Value);
                    break;
                case "chainJumps":
                    traits.ChainJumps = Math.Max(traits.ChainJumps, pair.Value);
                    break;
                case "poisonSpread":
                    traits.PoisonSpread = Math.Max(traits.PoisonSpread, pair.Value);
                    break;
                default:
                    Plugin.Logger.LogWarning(
                        $"{definition.Id}: weapon {weapon.Id} has an unmapped trait \"{pair.Key}\" (skipped)");
                    break;
            }
        }

        Plugin.Logger.LogInfo(
            $"{definition.Id}: weapon {weapon.Id} traits merged into {definition.SkillId} " +
            $"(ignite {traits.IgnitePct:0.##}/{traits.IgniteSeconds:0.#} s, " +
            $"chain {traits.ChainChance:0.#}%/{traits.ChainJumps:0.#}, poison spread {traits.PoisonSpread:0.#} m)");
    }

    private static readonly System.Collections.Generic.HashSet<string> WeaponTraitsApplied =
        new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

    /// <summary>The class passive is stat-based, so it goes into the same list the
    /// sheet writes: the game applies it once per run with the rest of the class
    /// stats, and there is no separate engine to keep in sync. A name the game
    /// does not know is reported and skipped, never thrown at spawn.</summary>
    private static void ApplyPassive(
        List<StatModifier> modifiers,
        ClassDefinition.PassiveDefinition passive)
    {
        if (passive == null || passive.Stats == null)
        {
            return;
        }

        for (var index = 0; index < passive.Stats.Count; index++)
        {
            if (TryBuild(passive.Stats[index], passive.Name, out var modifier))
            {
                modifiers.Add(modifier);
            }
        }
    }

    /// <summary>The level-1 increment of a per-level passive goes into the
    /// character's own modifier list, exactly like the static passive; the
    /// level-up hook (<see cref="ClassPassives"/>) applies the next ones live.</summary>
    private static void ApplyPerLevel(
        List<StatModifier> modifiers,
        ClassDefinition.PassiveDefinition passive,
        int level)
    {
        if (passive == null || passive.PerLevel == null)
        {
            return;
        }

        foreach (var entry in passive.PerLevel)
        {
            if (entry == null)
            {
                continue;
            }

            var every = Math.Max(1, entry.Every);
            if (level % every != 0)
            {
                continue;
            }

            if (TryBuild(entry, passive.Name, out var modifier))
            {
                modifiers.Add(modifier);
            }
        }
    }

    /// <summary>Turns one sheet row into the game's own modifier, reporting a
    /// name the build does not know instead of throwing at spawn.</summary>
    internal static bool TryBuild(
        ClassDefinition.StatEntry entry,
        string passiveName,
        out StatModifier modifier)
    {
        modifier = null;
        if (entry == null)
        {
            return false;
        }

        if (!Enum.TryParse(entry.Stat, out EStat stat) ||
            !Enum.TryParse(entry.Type, out EStatModifyType type))
        {
            Plugin.Logger.LogWarning(
                $"class passive {passiveName}: \"{entry.Stat}\"/\"{entry.Type}\" is not an " +
                "EStat/EStatModifyType member; entry skipped");
            return false;
        }

        modifier = new StatModifier();
        modifier.stat = stat;
        modifier.modifyType = type;
        modifier.modification = entry.Value;
        return true;
    }

    private static void Add(List<StatModifier> modifiers, EStat stat, EStatModifyType type, float value)
    {
        var modifier = new StatModifier();
        modifier.stat = stat;
        modifier.modifyType = type;
        modifier.modification = value;
        modifiers.Add(modifier);
    }

    internal static string Describe(CharacterData data)
    {
        var modifiers = data.statModifiers;
        if (modifiers == null)
        {
            return "none";
        }

        var text = "";
        for (var i = 0; i < modifiers.Count; i++)
        {
            var modifier = modifiers[i];
            if (i > 0)
            {
                text += ", ";
            }

            text += $"{modifier.stat} {modifier.modifyType} {modifier.modification:0.###}";
        }

        return text;
    }
}

/// <summary>
/// The per-level half of a class passive (2026-11-05). Level 1 is already in
/// the character's own modifier list (<see cref="ClassStats.Apply"/>); this hook
/// applies one increment per level to the live stat pipeline, so the run's
/// stats grow with the character exactly like the relic engine's writes.
/// </summary>
internal static class ClassPassives
{
    private static int lastLevel = 1;
    private static bool warnedNoClass;
    private static bool warnedNoStats;

    private static readonly System.Collections.Generic.Dictionary<string, int> Applied =
        new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);

    internal static void Reset()
    {
        lastLevel = 1;
        warnedNoClass = false;
        warnedNoStats = false;
        Applied.Clear();
    }

    internal static void OnLevelUp(PlayerInventory inventory)
    {
        if (inventory == null)
        {
            return;
        }

        var player = MyPlayer.Instance;
        if (player == null)
        {
            return;
        }

        int level;
        try
        {
            level = inventory.GetCharacterLevel();
        }
        catch (Exception)
        {
            return;
        }

        if (level <= lastLevel)
        {
            return;
        }

        if (!ClassSwap.TryGetDefinition(player.character, out var definition))
        {
            if (!warnedNoClass)
            {
                warnedNoClass = true;
                Plugin.Logger.LogWarning(
                    $"class passive: no definition for {player.character}; per-level stats wait");
            }

            return;
        }

        var passive = definition.Passive;
        if (passive == null || passive.PerLevel == null)
        {
            lastLevel = level;
            return;
        }

        var statInventory = inventory.statInventory;
        if (statInventory == null)
        {
            if (!warnedNoStats)
            {
                warnedNoStats = true;
                Plugin.Logger.LogWarning("class passive: stat inventory is not ready; per-level stats wait");
            }

            // Do not move lastLevel: the next frame retries the whole delta.
            return;
        }

        var written = 0;
        for (var l = lastLevel + 1; l <= level; l++)
        {
            foreach (var entry in passive.PerLevel)
            {
                if (entry == null)
                {
                    continue;
                }

                var every = Math.Max(1, entry.Every);
                if (l % every != 0)
                {
                    continue;
                }

                var key = entry.Stat + "/" + entry.Type;
                Applied.TryGetValue(key, out var count);
                if (entry.Max > 0 && count >= entry.Max)
                {
                    continue;
                }

                if (!ClassStats.TryBuild(entry, passive.Name, out var modifier))
                {
                    continue;
                }

                statInventory.ChangeStat(modifier, permanent: true, timeout: 0f, addToShrineLog: false);
                Applied[key] = count + 1;
                written++;
            }
        }

        lastLevel = level;
        if (written > 0)
        {
            inventory.playerStats?.ForceUpdateStats();
            Plugin.Logger.LogInfo(
                $"class passive: {definition.Name} level {level}, {written} per-level stat(s)");
        }
    }
}
