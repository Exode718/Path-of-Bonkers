using System;
using System.Collections.Generic;
using Assets.Scripts.Actors.Enemies;
using UnityEngine;

namespace Wraeclast.Data;

/// <summary>
/// A soft cap on simultaneously live enemies. Megabonk keeps spawning waves
/// even when the player cannot clear them, and every extra body costs the host
/// AI, physics, collisions and draw calls; a swarm that never dies is what
/// turns a good frame into a slide show. New non-boss spawns are refused while
/// the count is at <see cref="Plugin.MaxAliveEnemies"/>; existing enemies keep
/// fighting and the next spawn succeeds as soon as one is released to the pool.
///
/// The count is ours (<see cref="Enemy.InitEnemy"/> adds, <c>ReleaseToPool</c>
/// removes), not a scene sweep: it is O(1) per spawn and a swarm cannot make it
/// expensive. Boss tiers always spawn — a full floor must not eat the boss.
/// </summary>
internal static class EnemyCap
{
    private static readonly HashSet<int> Alive = new HashSet<int>();
    private static bool logged;

    internal static int Count => Alive.Count;

    internal static void Track(Enemy enemy)
    {
        if (enemy != null)
        {
            Alive.Add(enemy.GetInstanceID());
        }
    }

    internal static void Release(Enemy enemy)
    {
        if (enemy != null)
        {
            Alive.Remove(enemy.GetInstanceID());
        }
    }

    /// <summary>The run reset (<c>ItemInventory.Cleanup</c>): a new floor must
    /// not inherit the previous floor's live set.</summary>
    internal static void Reset()
    {
        Alive.Clear();
        logged = false;
    }

    /// <summary>Whether this spawn may run. A boss-tier enemy is always allowed;
    /// every other spawn is refused once the cap is reached.</summary>
    internal static bool Allows(EnemyData data)
    {
        var cap = Plugin.MaxAliveEnemies.Value;
        if (cap <= 0 || data == null)
        {
            return true;
        }

        // A boss must never be eaten by a floor full of trash.
        try
        {
            if (PoeEnemies.IsBossTier(data.enemyName))
            {
                return true;
            }
        }
        catch (Exception)
        {
            // An enemy row without a readable name: let it spawn rather than
            // starve the floor.
            return true;
        }

        if (Alive.Count < cap)
        {
            return true;
        }

        if (!logged)
        {
            logged = true;
            Plugin.Logger.LogInfo(
                $"enemy cap: {cap} alive, new spawns are refused until the count drops");
        }

        return false;
    }
}
