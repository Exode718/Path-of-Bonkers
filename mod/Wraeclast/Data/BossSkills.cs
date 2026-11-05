using System;
using System.Collections.Generic;
using Actors.Enemies;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Game.Combat.EnemySpecialAttacks;

namespace Wraeclast.Data;

/// <summary>
/// Atlas boss skill kits.
///
/// Megabonk builds every enemy's attack list in <c>Enemy.InitEnemy</c>: it takes
/// <c>EnemyData.specialAttacks</c> and hands the array to a new
/// <c>SpecialAttackController</c>, which snapshots it in its constructor (ISIL
/// 0x180485650: the array is read at [enemyData+0x168], its length stored on
/// the controller). The array therefore has to be in place *before*
/// <c>InitEnemy</c> runs — a postfix is too late, which is why
/// <see cref="Patches.EnemyPatches"/> uses a prefix for this.
///
/// The twelve boss-tier <see cref="EEnemy"/> members (see
/// <see cref="PoeEnemies.IsBossTier"/>) keep stat blocks, HP, speed and melee
/// from the Megabonk enemy they replace, but cast a kit chosen per Atlas boss
/// role (see <see cref="BossRoster"/>) instead of that enemy's own
/// root/laser/slam set. Every attack is copied from an existing <c>EnemyData</c> row: the <c>attackPrefab</c> must stay a prefab the
/// <c>PoolManager</c> attack pool already knows, because
/// <c>SpecialAttackController.UseSpecialAttack</c> rents from that pool by
/// reference. That is why kits are assembled from donors rather than authored
/// as new assets.
///
/// Kit composition is ours (Megabonk ships no Atlas bosses); each entry names
/// its donor <see cref="EEnemy"/> and attack so the source is traceable.
/// </summary>
internal static class BossSkills
{
    private readonly struct Source
    {
        internal Source(EEnemy donor, string attack)
        {
            Donor = donor;
            Attack = attack;
        }

        /// <summary>EnemyData row the attack is copied from (EEnemy enum value).</summary>
        internal readonly EEnemy Donor;

        /// <summary><c>EnemySpecialAttack.attackName</c> on that row.</summary>
        internal readonly string Attack;
    }

    /// <summary>Atlas boss -> attack sources. Our design; donors are Megabonk's.</summary>
    private static readonly Dictionary<string, Source[]> Kits =
        new Dictionary<string, Source[]>(StringComparer.OrdinalIgnoreCase)
        {
            // Elder: corruption rooted in the ground — root spikes, a spike line,
            // predicted stone pillars and a stone pulse.
            ["elder"] = new[]
            {
                new Source(EEnemy.Ent1, "RootAttackContinous"),
                new Source(EEnemy.Ent3, "RootAttackLineSingle"),
                new Source(EEnemy.Pharaoh3, "DesertPillarsPredictiveMultiple"),
                new Source(EEnemy.MinibossGolem, "StoneAttackContinous")
            },

            // Shaper: beams — a tracking beam, a sweeping laser and a ring of
            // pillars closing on the arena.
            ["shaper"] = new[]
            {
                new Source(EEnemy.Pharaoh3, "LaserTargetAttack"),
                new Source(EEnemy.Pharaoh2, "LaserDiscoAttack"),
                new Source(EEnemy.Pharaoh3, "DesertPillarsCircles")
            },

            // Maven: floating spikes and a body slam where the player stands.
            ["maven"] = new[]
            {
                new Source(EEnemy.GhostKing, "GhostSpikesMultiple"),
                new Source(EEnemy.MinibossScorpion, "ScorpionSpikesMultiple"),
                new Source(EEnemy.MinibossPig, "SphereSlam")
            },

            // Searing Exarch: bone pillars that predict and ring the player,
            // with the desert pillar volley on top (fire/ash, no fire prefab
            // exists in the attack pool).
            ["searing-exarch"] = new[]
            {
                new Source(EEnemy.CalciumDad, "BonePillarsPredictiveMultiple"),
                new Source(EEnemy.CalciumDad, "BonePillarsCircles"),
                new Source(EEnemy.Pharaoh3, "DesertPillarsPredictiveMultiple")
            },

            // Eater of Worlds: a root line, the ghost spikes and the slam.
            ["eater-of-worlds"] = new[]
            {
                new Source(EEnemy.Ent2, "RootAttackLines3"),
                new Source(EEnemy.GhostKing, "GhostSpikesMultiple"),
                new Source(EEnemy.MinibossPig, "SphereSlam")
            }
        };

    // Built kits, keyed by Atlas boss: all tiers that share a boss share the kit
    // (and therefore the same attack instances the controller hashes on).
    private static readonly Dictionary<string, EnemySpecialAttack[]> Built =
        new Dictionary<string, EnemySpecialAttack[]>(StringComparer.OrdinalIgnoreCase);

    // One log line per boss; InitEnemy runs on every spawn and repool.
    private static readonly HashSet<string> Logged =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves every kit at data load so a missing donor or attack
    /// shows up in the log before any boss spawns. Purely diagnostic; the kits
    /// stay cached for <see cref="Apply"/>.</summary>
    internal static void Prime(DataManager manager)
    {
        if (!Plugin.ReplaceBossSkills.Value)
        {
            return;
        }

        foreach (var boss in Kits.Keys)
        {
            Resolve(manager, boss);
        }
    }

    /// <summary>Replaces <paramref name="data"/>'s attack array with the kit for
    /// the Atlas boss role resolved for this spawn. Non-boss enemies are
    /// untouched, and a kit that cannot be resolved falls back to the native
    /// attacks.</summary>
    internal static void Apply(EnemyData data, string boss)
    {
        if (data == null || string.IsNullOrEmpty(boss) || !Plugin.ReplaceBossSkills.Value)
        {
            return;
        }

        var kit = Resolve(DataManager.Instance, boss);
        if (kit == null)
        {
            return;
        }

        data.specialAttacks = kit;
    }

    private static EnemySpecialAttack[] Resolve(DataManager manager, string boss)
    {
        if (Built.TryGetValue(boss, out var cached))
        {
            return cached;
        }

        if (manager == null)
        {
            Plugin.Logger.LogWarning($"{boss}: no DataManager, keeping the native boss attacks");
            return null;
        }

        if (!Kits.TryGetValue(boss, out var sources))
        {
            return null;
        }

        var kit = new List<EnemySpecialAttack>(sources.Length);
        foreach (var source in sources)
        {
            var donor = manager.GetEnemyData(source.Donor);
            var attack = Find(donor, source.Attack);
            if (attack == null)
            {
                Plugin.Logger.LogWarning(
                    $"{boss}: {source.Donor} has no attack '{source.Attack}'");
                continue;
            }

            kit.Add(Copy(attack));
        }

        if (kit.Count == 0)
        {
            return null;
        }

        var result = kit.ToArray();
        Built[boss] = result;

        if (Logged.Add(boss))
        {
            var names = new string[result.Length];
            for (var index = 0; index < result.Length; index++)
            {
                names[index] = result[index].attackName;
            }

            Plugin.Logger.LogInfo($"{boss} skills: {string.Join(", ", names)}");
        }

        return result;
    }

    private static EnemySpecialAttack Find(EnemyData donor, string name)
    {
        var attacks = donor == null ? null : donor.specialAttacks;
        if (attacks == null)
        {
            return null;
        }

        for (var index = 0; index < attacks.Length; index++)
        {
            var attack = attacks[index];
            if (attack != null && string.Equals(attack.attackName, name, StringComparison.Ordinal))
            {
                return attack;
            }
        }

        return null;
    }

    /// <summary>Copies the tuning of a donor attack. The prefab is shared, never
    /// cloned: it is the pool's key.</summary>
    private static EnemySpecialAttack Copy(EnemySpecialAttack source) => new EnemySpecialAttack
    {
        isEnabled = true,
        priority = source.priority,
        attackName = source.attackName,
        attackPrefab = source.attackPrefab,
        attackChargeTime = source.attackChargeTime,
        attackRadius = source.attackRadius,
        attackCooldown = source.attackCooldown,
        attackCooldownMax = source.attackCooldownMax,
        initialCooldown = source.initialCooldown,
        duration = source.duration,
        nextSpecialAttackCooldown = source.nextSpecialAttackCooldown,
        triggerDistance = source.triggerDistance,
        endLag = source.endLag,
        damageMultiplier = source.damageMultiplier
    };
}
