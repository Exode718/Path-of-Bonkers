using System;
using System.Reflection;
using Actors.Enemies;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Game.Combat.EnemySpecialAttacks;
using Assets.Scripts.Managers;
using HarmonyLib;
using UnityEngine;
using Wraeclast.Data;

namespace Wraeclast.Dev;

/// <summary>
/// Debug harness for the live Atlas boss run (DOC.md: "boss run in the real
/// game"). One key per boss spawns it in front of the player through
/// <c>EnemyManager.SpawnBoss</c> — the game's own stage-boss entry point, the
/// same call <c>GameManager</c> makes for the final boss (summoner 0,
/// <c>EEnemyFlag.Boss</c>, explicit position). Scaling, pooling, HP, the boss
/// bar and the attack controller therefore stay the game's own.
///
/// The cast probe is installed by hand instead of with a [HarmonyPatch]
/// attribute: <c>UseSpecialAttack</c> is private, and a renamed method would
/// throw out of <c>CreateAndPatchAll</c> and take every other patch with it.
///
/// Both features are config-gated (<c>Debug.SpawnBossKeys</c> off by default,
/// <c>Debug.LogBossCasts</c> on) so a normal run is unaffected.
/// </summary>
internal static class BossRun
{
    private const string HarmonyId = "wraeclast.conversion.bossrun";
    private const float SpawnDistance = 20f;

    private readonly struct Spawn
    {
        internal Spawn(KeyCode key, EEnemy kind, string boss)
        {
            Key = key;
            Kind = kind;
            Boss = boss;
        }

        internal readonly KeyCode Key;
        internal readonly EEnemy Kind;
        internal readonly string Boss;
    }

    // One representative EEnemy per Atlas boss (see PoeEnemies.IsBossTier); the
    // spawn goes through the boss data row, so the kit and the model are the
    // ones a real stage boss gets. The role is forced, because the slot alone
    // resolves to the current floor's boss.
    //
    // 0.9.119: the plain F5/F6/F7 now teleport to floors 1/2/3
    // (<see cref="RunTest"/>), so the dev spawns take **Ctrl+F5/F6/F7** — the
    // same keys, shifted, instead of a second set the operator has to remember.
    private static readonly Spawn[] Spawns =
    {
        new Spawn(KeyCode.F5, EEnemy.Ent1, "elder"),
        new Spawn(KeyCode.F6, EEnemy.Pharaoh3, "shaper"),
        new Spawn(KeyCode.F7, EEnemy.Ghostham, "maven")
    };

    /// <summary>Patches the special-attack entry point so every boss cast is
    /// named in the log. Failure is a warning, never a boot failure.</summary>
    internal static void Install()
    {
        try
        {
            var target = AccessTools.Method(typeof(SpecialAttackController), "UseSpecialAttack");
            if (target == null)
            {
                Plugin.Logger.LogWarning(
                    "Boss cast probe: SpecialAttackController.UseSpecialAttack not found");
                return;
            }

            var postfix = new HarmonyMethod(typeof(BossRun).GetMethod(
                nameof(AfterUseSpecialAttack),
                BindingFlags.Static | BindingFlags.NonPublic));
            new Harmony(HarmonyId).Patch(target, postfix: postfix);
            Plugin.Logger.LogInfo("Boss cast probe: SpecialAttackController.UseSpecialAttack");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"Boss cast probe failed: {error.Message}");
        }
    }

    /// <summary>Per-frame key check, ticked from <c>MyPlayer.Update</c>.</summary>
    internal static void Tick(MyPlayer player)
    {
        if (!Plugin.SpawnBossKeys.Value || player == null || player.transform == null)
        {
            return;
        }

        if (Time.timeScale <= 0f)
        {
            return;
        }

        // 0.9.119: the plain F5/F6/F7 belong to the floor teleport.
        if (!Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl))
        {
            return;
        }

        var manager = EnemyManager.Instance;
        if (manager == null)
        {
            return;
        }

        for (var index = 0; index < Spawns.Length; index++)
        {
            if (Input.GetKeyDown(Spawns[index].Key))
            {
                SpawnOne(manager, player, Spawns[index]);
            }
        }
    }

    private static void SpawnOne(EnemyManager manager, MyPlayer player, Spawn spawn)
    {
        try
        {
            var forward = player.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f)
            {
                forward = Vector3.forward;
            }

            var position = player.transform.position + forward.normalized * SpawnDistance;

            // Ground the spawn: InitEnemy only stores the position it is given,
            // and a boss buried in a rock or a cliff can never be inspected.
            if (Physics.Raycast(position + Vector3.up * 60f, Vector3.down, out var ground, 200f))
            {
                position = ground.point;
            }

            BossRoster.ForceNext(spawn.Boss);
            var enemy = manager.SpawnBoss(spawn.Kind, 0, EEnemyFlag.Boss, position, 1f);
            BossRoster.ClearForce();
            if (enemy == null)
            {
                Plugin.Logger.LogWarning(
                    $"debug boss spawn {spawn.Boss} ({spawn.Kind}) returned null");
                return;
            }

            var body = enemy.transform.Find("WraeclastModel/Body");
            var bodyRenderer = body == null ? null : body.GetComponent<Renderer>();
            var bounds = bodyRenderer == null ? default : bodyRenderer.bounds;
            Plugin.Logger.LogInfo(
                $"debug boss spawn: {spawn.Boss} as {spawn.Kind}, " +
                $"{enemy.GetHeight():0.00} units tall, attacks [{Kit(enemy.enemyData)}], " +
                $"root {position}, mesh center {bounds.center} size {bounds.size}");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"debug boss spawn {spawn.Boss} failed: {error}");
        }
    }

    private static string Kit(EnemyData data)
    {
        var attacks = data == null ? null : data.specialAttacks;
        if (attacks == null || attacks.Length == 0)
        {
            return "none";
        }

        var names = new string[attacks.Length];
        for (var index = 0; index < attacks.Length; index++)
        {
            names[index] = attacks[index] == null ? "null" : attacks[index].attackName;
        }

        return string.Join(", ", names);
    }

    private static void AfterUseSpecialAttack(EnemySpecialAttack attack)
    {
        if (!Plugin.LogBossCasts.Value || attack == null)
        {
            return;
        }

        try
        {
            Plugin.Logger.LogInfo($"boss cast: {attack.attackName}");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"boss cast log failed: {error.Message}");
        }
    }
}
