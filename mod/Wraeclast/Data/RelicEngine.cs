using System;
using System.Collections.Generic;
using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Game.Combat;
using Assets.Scripts.Game.Combat.EnemyDebuffs;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Menu.Shop;
using UnityEngine;

namespace Wraeclast.Data;

/// <summary>
/// R4: the relic effects that are ours, not the donor's.
///
/// Megabonk items are code-side: <c>ItemFactory.CreateItem</c> builds an
/// <c>ItemImplementations.Item*</c> per <c>EItem</c> and that class writes its
/// own modifiers. A relic in <c>mod/data/items.json</c> that carries
/// <c>stats</c> or <c>triggers</c> is therefore **engine-owned**: the factory is
/// answered with <c>ItemNoImplementation</c> (so nothing native applies) and
/// this class applies the sheet instead.
///
/// - Stats go through the game's own <c>StatInventory.ChangeStat</c>
///   (<c>permanent: true</c>), once per stack, so the stats, the UI and the run
///   reset stay Megabonk's.
/// - Triggers ride the game's own events: our skill hits (<c>OnHit</c> from
///   <see cref="Wraeclast.Skills.PoeSkills"/>), enemy deaths
///   (<c>OnKill</c> from <c>Enemy.EnemyDied</c>) and a per-frame
///   <c>Tick</c> for the stand-still ramp. Damage always goes through
///   <c>Enemy.DamageFromPlayerWeapon</c>, so XP, gold and the draft stay native.
///
/// The engine is generic: S5 swapped the 67 donor-backed relics onto it, one
/// sheet row at a time, without another code change. Every one of the 79 relics
/// in <c>mod/data/items.json</c> is engine-owned today.
/// </summary>
internal static class RelicEngine
{
    private static readonly Dictionary<int, ItemDefinition> Owned = new Dictionary<int, ItemDefinition>();

    /// <summary>Stacks already paid into the stat pipeline, per slot, so a
    /// second pickup (or the <c>amount</c> overload) only applies the delta.</summary>
    private static readonly Dictionary<int, int> Applied = new Dictionary<int, int>();

    /// <summary>Warned (stat, type) pairs, so a bad sheet row logs once.</summary>
    private static readonly HashSet<string> Warned = new HashSet<string>();

    private static ItemInventory inventory;
    private static float standstill;
    private static Vector3 lastPosition;
    private static bool tracking;
    private static bool exploding;

    internal static int Count => Owned.Count;

    internal static bool Active => Owned.Count > 0;

    /// <summary>Builds the engine-owned set from the sheet. Called at
    /// <c>DataManager.Load</c>, after <see cref="ItemSwap.Prime"/> audited the
    /// slots. Off with <c>[Items] ReplaceItems</c>, like the relic cards.</summary>
    internal static void Prime()
    {
        Owned.Clear();
        Applied.Clear();

        if (!Plugin.ReplaceItems.Value)
        {
            Plugin.Logger.LogInfo("relic engine off: item effects stay the donor class's own");
            return;
        }

        var names = new List<string>();
        foreach (var definition in ItemDatabase.All)
        {
            if (!definition.EngineOwned)
            {
                continue;
            }

            if (!Enum.TryParse(definition.Slot, ignoreCase: false, out EItem slot) ||
                !Enum.IsDefined(typeof(EItem), slot))
            {
                Plugin.Logger.LogWarning($"relic engine: {definition.Id} has no valid EItem slot");
                continue;
            }

            Owned[(int)slot] = definition;
            names.Add($"{definition.Name} ({definition.StatCount}s/{definition.TriggerCount}t)");
        }

        Plugin.Logger.LogInfo(Owned.Count == 0
            ? "relic engine: no engine-owned relic in the sheet"
            : $"relic engine: {Owned.Count} unique(s) own their effect, the donor class is neutralised: " +
              string.Join(", ", names));
    }

    /// <summary>A new run starts from zero: the game's own inventory cleanup
    /// resets the stats, so the stack bookkeeping and the ramp reset with it.</summary>
    internal static void Reset()
    {
        Applied.Clear();
        inventory = null;
        standstill = 0f;
        tracking = false;
        exploding = false;
    }

    /// <summary>True when this slot's effect is the engine's, without needing an
    /// inventory (patch filters, effect neutralisation).</summary>
    internal static bool OwnsSlot(EItem item) => Owned.ContainsKey((int)item);

    internal static bool OwnsEffect(EItem item, ItemInventory owner)
    {
        if (!Owned.ContainsKey((int)item))
        {
            return false;
        }

        if (owner != null)
        {
            inventory = owner;
        }

        return true;
    }

    // ------------------------------------------------------------------ stats

    /// <summary>Pickup hook (<c>ItemInventory.AddItem</c>). Applies only the new
    /// stacks: <c>GetAmount</c> is the game's own count, and a relic can be
    /// picked up again while it is already owned.</summary>
    internal static void OnItemAdded(EItem item)
    {
        if (!Owned.TryGetValue((int)item, out var definition))
        {
            return;
        }

        var amount = Amount(item);
        Applied.TryGetValue((int)item, out var applied);
        if (amount <= applied)
        {
            return;
        }

        Applied[(int)item] = amount;
        Apply(definition, amount - applied);
    }

    private static int Amount(EItem item)
    {
        if (inventory != null)
        {
            try
            {
                return inventory.GetAmount(item);
            }
            catch (Exception error)
            {
                Plugin.Logger.LogWarning($"relic engine: GetAmount({item}) failed ({error.Message})");
            }
        }

        return 1;
    }

    private static void Apply(ItemDefinition definition, int stacks)
    {
        if (stacks <= 0)
        {
            return;
        }

        var player = MyPlayer.Instance;
        var playerInventory = player == null ? null : player.inventory;
        var statInventory = playerInventory == null ? null : playerInventory.statInventory;
        if (statInventory == null)
        {
            Plugin.Logger.LogWarning($"relic engine: {definition.Name} taken with no live stat inventory");
            return;
        }

        var written = 0;
        if (definition.Stats != null)
        {
            foreach (var entry in definition.Stats)
            {
                if (!TryModifier(entry, out var stat, out var type))
                {
                    continue;
                }

                for (var stack = 0; stack < stacks; stack++)
                {
                    // A fresh container per write: ChangeStat keeps the modifier.
                    var modifier = new StatModifier
                    {
                        stat = stat,
                        modifyType = type,
                        modification = entry.Value
                    };

                    statInventory.ChangeStat(modifier, permanent: true, timeout: 0f, addToShrineLog: false);
                    written++;
                }
            }
        }

        if (playerInventory.playerStats != null)
        {
            playerInventory.playerStats.ForceUpdateStats();
        }

        Plugin.Logger.LogInfo(
            $"relic engine: {definition.Name} x{AmountOf(definition)} -> {written} modifier(s), " +
            $"{definition.TriggerCount} trigger(s)");
    }

    private static int AmountOf(ItemDefinition definition) =>
        Enum.TryParse(definition.Slot, out EItem slot) && Applied.TryGetValue((int)slot, out var amount)
            ? amount
            : 1;

    private static bool TryModifier(RelicStat entry, out EStat stat, out EStatModifyType type)
    {
        stat = EStat.MaxHealth;
        type = EStatModifyType.Flat;

        if (entry == null || string.IsNullOrEmpty(entry.Stat) || string.IsNullOrEmpty(entry.Type))
        {
            return false;
        }

        if (!Enum.TryParse(entry.Stat, ignoreCase: false, out stat) ||
            !Enum.IsDefined(typeof(EStat), stat))
        {
            Warn($"{entry.Stat}/{entry.Type}", $"relic engine: {entry.Stat} is not an EStat member");
            return false;
        }

        if (!Enum.TryParse(entry.Type, ignoreCase: false, out type) ||
            !Enum.IsDefined(typeof(EStatModifyType), type))
        {
            Warn($"{entry.Stat}/{entry.Type}", $"relic engine: {entry.Type} is not an EStatModifyType member");
            return false;
        }

        return true;
    }

    private static void Warn(string key, string message)
    {
        if (Warned.Add(key))
        {
            Plugin.Logger.LogWarning(message);
        }
    }

    // --------------------------------------------------------------- triggers

    /// <summary>Our skill damage landed on an enemy. <paramref name="container"/>
    /// is the hit the game's pipeline just resolved (damage, element, crit).</summary>
    internal static void OnHit(Enemy enemy, DamageContainer container)
    {
        if (!Active || enemy == null || container == null)
        {
            return;
        }

        foreach (var pair in Owned)
        {
            var definition = pair.Value;
            if (definition.TriggerCount == 0 || !Owns(pair.Key))
            {
                continue;
            }

            foreach (var trigger in definition.Triggers)
            {
                if (trigger == null || trigger.On != "hit" || !Roll(trigger.Chance))
                {
                    continue;
                }
                switch (trigger.Effect)
                {
                    case "poison":
                        // Maw of Conquest grants a poison trigger *and* the
                        // PoisonDamageMultiplier that must scale it; the hit
                        // container is already resolved, so clone it for the
                        // debuff (same pattern as the skill's own poison).
                        enemy.AddDebuff(EDebuff.Poison, PoisonScaled(container), trigger.Seconds);
                        break;

                    case "burn":
                        enemy.AddDebuff(EDebuff.Burn, container, trigger.Seconds);
                        break;

                    case "curse":
                        Burst(
                            enemy.transform.position,
                            Radius(trigger.Radius, 3f),
                            container.damage * Value(trigger.Value, 0.45f),
                            definition.Name);
                        break;
                }
            }
        }
    }

    /// <summary>An enemy died. Only the kill trigger needs the killing hit
    /// (Headhunter explodes for a share of it), and the parameterless
    /// <c>EnemyDied</c> overload reaches here with a null container.</summary>
    internal static void OnKill(Enemy enemy, DamageContainer container)
    {
        if (!Active || exploding || enemy == null || container == null || container.damage <= 0f)
        {
            return;
        }

        foreach (var pair in Owned)
        {
            var definition = pair.Value;
            if (definition.TriggerCount == 0 || !Owns(pair.Key))
            {
                continue;
            }

            foreach (var trigger in definition.Triggers)
            {
                if (trigger == null || trigger.On != "kill" || trigger.Effect != "explode")
                {
                    continue;
                }

                if (!Roll(trigger.Chance))
                {
                    continue;
                }

                // The explosion kills enemies of its own; those deaths must not
                // start another explosion (one detonation per death).
                exploding = true;
                try
                {
                    Burst(
                        enemy.transform.position,
                        Radius(trigger.Radius, 3f),
                        container.damage * Value(trigger.Value, 0.7f),
                        definition.Name);
                }
                finally
                {
                    exploding = false;
                }
            }
        }
    }

    /// <summary>Per-frame upkeep, ticked by our skill loop (the player's own
    /// clock, so the pause menu stops it too).</summary>
    internal static void Tick(MyPlayer player, float delta)
    {
        if (!Active || player == null)
        {
            return;
        }

        var position = player.transform.position;
        if (!tracking)
        {
            tracking = true;
            lastPosition = position;
            standstill = 0f;
            return;
        }

        if ((position - lastPosition).sqrMagnitude > 0.01f)
        {
            standstill = 0f;
        }
        else
        {
            standstill += delta;
        }

        lastPosition = position;
    }

    /// <summary>The stand-still ramp (Dying Sun), as a multiplier on our skill
    /// damage. Applied directly rather than through the stat pipeline because
    /// the value changes every frame, and only our skills deal the damage.</summary>
    internal static float CastMultiplier(MyPlayer player)
    {
        if (!Active || player == null || standstill <= 0f)
        {
            return 1f;
        }

        var bonus = 0f;
        foreach (var pair in Owned)
        {
            var definition = pair.Value;
            if (definition.TriggerCount == 0 || !Owns(pair.Key))
            {
                continue;
            }

            foreach (var trigger in definition.Triggers)
            {
                if (trigger == null || trigger.On != "standstill" || trigger.Effect != "damageRamp")
                {
                    continue;
                }

                var cap = Value(trigger.Value, 1f);
                var seconds = Mathf.Max(0.1f, trigger.Seconds);
                bonus += cap * Mathf.Clamp01(standstill / seconds);
            }
        }

        return 1f + bonus;
    }

    // ----------------------------------------------------------------- shared

    private static bool Owns(int slot)
    {
        if (inventory != null)
        {
            try
            {
                return inventory.GetAmount((EItem)slot) > 0;
            }
            catch (Exception)
            {
                // A destroyed inventory proxy (run over) falls back to the
                // stack bookkeeping below.
            }
        }

        return Applied.ContainsKey(slot);
    }

    /// <summary>The hit container, scaled by the player's own
    /// <c>PoisonDamageMultiplier</c> when a relic adds poison. Base-agnostic,
    /// like the skill's read: `current - base` is the bonus whatever the unit.</summary>
    private static DamageContainer PoisonScaled(DamageContainer source)
    {
        var player = MyPlayer.Instance;
        var stats = player == null || player.inventory == null ? null : player.inventory.playerStats;
        if (source == null || stats == null)
        {
            return source;
        }

        var bonus = stats.GetStat(EStat.PoisonDamageMultiplier) -
                    PlayerStatsNew.GetBaseValue(EStat.PoisonDamageMultiplier);
        if (bonus <= 0f)
        {
            return source;
        }

        var copy = new DamageContainer(1f, "Relic poison");
        copy.damage = source.damage * (1f + bonus);
        copy.direction = source.direction;
        copy.element = source.element;
        copy.damageEffect = source.damageEffect;
        copy.crit = source.crit;
        copy.knockback = source.knockback;
        return copy;
    }

    private static bool Roll(float chance) =>
        chance <= 0f || UnityEngine.Random.value < chance;

    private static float Value(float value, float fallback) => value > 0f ? value : fallback;

    private static float Radius(float radius, float fallback) => radius > 0.1f ? radius : fallback;

    /// <summary>Damage around a point through the game's own enemy entry point,
    /// so kills, XP, gold and the draft stay native.</summary>
    private static void Burst(Vector3 position, float radius, float damage, string source)
    {
        if (damage <= 0f)
        {
            return;
        }

        var colliders = Physics.OverlapSphere(position, radius);
        var seen = new HashSet<int>();

        for (var index = 0; index < colliders.Length; index++)
        {
            var collider = colliders[index];
            if (collider == null)
            {
                continue;
            }

            var enemy = collider.GetComponentInParent<Enemy>();
            if (enemy == null || !enemy.CanTakeDamage() || !seen.Add(enemy.GetInstanceID()))
            {
                continue;
            }

            var container = new DamageContainer(1f, source);
            container.damage = damage;
            container.direction = (enemy.transform.position - position).normalized;
            container.element = EElement.Neutral;
            container.damageEffect = EDamageEffect.None;
            container.crit = false;
            container.knockback = 0f;
            enemy.DamageFromPlayerWeapon(container);
        }
    }
}
