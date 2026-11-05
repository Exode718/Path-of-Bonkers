using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Managers;
using HarmonyLib;
using UnityEngine;
using Wraeclast.Data;
using Wraeclast.Run;
using Wraeclast.Skills;

namespace Wraeclast.Patches;

/// <summary>
/// Enemy visuals and boss attacks. <c>Enemy.InitEnemy</c> is the spawn/repool
/// entry point: it is where Megabonk assigns the <c>EnemyData</c>, the animated
/// mesh, the renderer *and* builds the <c>SpecialAttackController</c> from
/// <c>EnemyData.specialAttacks</c>. The boss kit therefore goes in on a prefix
/// (before the controller snapshots the array) and the creature mesh on the
/// postfix (after the renderer and size are final). Gameplay (AI, collider,
/// damage, loot) is untouched.
///
/// Animation ticks from <c>AnimatedMesh.Update</c> because that is the one
/// component every visible enemy drives per frame; the Enemy object itself has
/// no Unity Update.
/// </summary>
[HarmonyPatch]
internal static class EnemyPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.InitEnemy))]
    private static void BeforeInitEnemy(EnemyData enemyData, EEnemyFlag flag)
    {
        try
        {
            // One resolution per spawn, on the prefix where the flag still
            // exists: the kit and the model both use this role.
            var boss = BossRoster.Begin(enemyData, flag);
            BossSkills.Apply(enemyData, boss);
        }
        catch (System.Exception error)
        {
            Plugin.Logger.LogError($"{enemyData?.enemyName}: boss skills failed: {error}");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.InitEnemy))]
    private static void AfterInitEnemy(Enemy __instance, EnemyData enemyData)
    {
        EnemyCap.Track(__instance);
        try
        {
            PoeEnemies.Attach(__instance, enemyData, BossRoster.End(enemyData));
        }
        catch (System.Exception error)
        {
            Plugin.Logger.LogError($"{enemyData?.enemyName}: enemy model swap failed: {error}");
        }
    }

    /// <summary>The soft cap: a floor already at
    /// <see cref="Plugin.MaxAliveEnemies"/> refuses new non-boss spawns until an
    /// enemy is released. See <see cref="EnemyCapPatches"/>.</summary>

    [HarmonyPostfix]
    [HarmonyPatch(typeof(AnimatedMesh), "Update")]
    private static void AfterAnimatedMeshUpdate(AnimatedMesh __instance)
    {
        PoeEnemies.Tick(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.EnemyDied), typeof(DamageContainer))]
    private static void AfterEnemyDied(Enemy __instance, DamageContainer __0)
    {
        PoeEnemies.BeginDeath(__instance);
        BossWindow.OnEnemyDied(__instance);
        PoeSkills.OnEnemyKilled(__instance, __0);
        RelicEngine.OnKill(__instance, __0);
        ChestSwap.ProbeAt(__instance.transform.position);
    }

    /// <summary>The game exposes two death entries (with and without the killing
    /// damage container); <see cref="PoeEnemies.BeginDeath"/> is idempotent, so
    /// both are hooked rather than relying on which one the kill path takes.
    /// The engine's kill trigger only needs the container, so the parameterless
    /// overload adds nothing to it.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.EnemyDied), new System.Type[0])]
    private static void AfterEnemyDiedNoDamage(Enemy __instance)
    {
        PoeEnemies.BeginDeath(__instance);
        BossWindow.OnEnemyDied(__instance);
    }

    /// <summary>The player's own damage entry points name the attacker, so they
    /// are the exact moment an enemy's swing lands: <c>DamagePlayer</c> for
    /// contact (melee) and <c>DamagePlayerExternal</c> for projectiles. The same
    /// two hooks record the killer for the death screen's damage-source row (V7).</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerHealth), "DamagePlayer")]
    private static void AfterDamagePlayer(Enemy enemy)
    {
        PoeEnemies.Attack(enemy);
        DeathScreenPatches.NoteAttacker(enemy);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerHealth), "DamagePlayerExternal")]
    private static void AfterDamagePlayerExternal(Enemy enemy)
    {
        PoeEnemies.Attack(enemy);
        DeathScreenPatches.NoteAttacker(enemy);
    }

    /// <summary>Boss kits (and any other cast the game starts) also swing the
    /// creature's attack clip, so a cast is not a statue in the middle of a
    /// telegraph.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.StartSpecialAttack))]
    private static void AfterStartSpecialAttack(Enemy __instance)
    {
        PoeEnemies.Attack(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.ReleaseToPool))]
    private static void AfterReleaseToPool(Enemy __instance)
    {
        EnemyCap.Release(__instance);
        PoeEnemies.Detach(__instance, true);
    }
}

/// <summary>
/// The soft cap's spawn gate. The type is known at compile time
/// (<c>typeof(EnemyManager)</c>); only the overload is resolved by shape (name +
/// first parameter <c>EnemyData</c>) because the interop parameter list is not
/// exposed in a stable form. Do **not** use <c>AccessTools.TypeByName</c> here:
/// it scans every loaded assembly, and Harmony then logs a wall of harmless
/// <c>ReflectionTypeLoadException</c> warnings for the Unity IL2CPP modules.
/// </summary>
[HarmonyPatch]
internal static class EnemyCapPatches
{
    private static System.Reflection.MethodBase TargetMethod()
    {
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(EnemyManager)))
        {
            if (method == null || method.Name != "SpawnEnemy")
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length > 0 && parameters[0].ParameterType.Name == "EnemyData")
            {
                return method;
            }
        }

        Plugin.Logger.LogWarning("enemy cap: EnemyManager.SpawnEnemy not found, the cap is off");
        return null;
    }

    [HarmonyPrefix]
    private static bool BeforeSpawnEnemy(object[] __args)
    {
        try
        {
            var data = __args != null && __args.Length > 0 ? __args[0] as EnemyData : null;
            return EnemyCap.Allows(data);
        }
        catch (System.Exception)
        {
            return true;
        }
    }
}

/// <summary>
/// The cap is per floor, not per run. The floor transition tears the previous
/// scene down, so the enemies still on it (the last ghost wave in particular)
/// are never released to the pool and their IDs would stay counted forever.
/// <c>ItemInventory.Cleanup</c> only fires at a run reset, so by floor 3 the set
/// was saturated and every regular spawn was refused while the boss tier kept
/// spawning: no mob on floor 3 except the timeline minibosses, with
/// <c>alive 254/250</c> from the first second. <c>LoadNextStage</c> and
/// <c>LoadFinalStage</c> are the loaders the portal, the boss-window transition
/// and the endless loop all take, so the reset sits on both.
/// </summary>
[HarmonyPatch]
internal static class EnemyCapFloorPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(MapController), nameof(MapController.LoadNextStage))]
    private static void AfterLoadNextStage()
    {
        EnemyCap.Reset();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MapController), nameof(MapController.LoadFinalStage))]
    private static void AfterLoadFinalStage()
    {
        EnemyCap.Reset();
    }
}
