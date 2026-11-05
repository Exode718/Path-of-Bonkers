using System;
using System.Collections.Generic;
using Actors.Enemies;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Managers;
using Assets.Scripts.Utility;
using Wraeclast.Data;
using UnityEngine;

namespace Wraeclast.Dev;

/// <summary>
/// Non-combat model gallery, gated by <c>Debug.GalleryKeys</c> (off by default):
/// F1 spawns every <see cref="EEnemy"/> the game knows in two rows in front of
/// the player (the boss tier on its own row), freezes them (speed 0, special
/// attacks off) and makes the player invincible through the damage prefixes in
/// <c>Patches/GalleryPatches.cs</c>. Thirty seconds after the build the stage
/// clock jumps to the stage gate, so the normal 10-minute beat can be watched
/// with the gallery still alive. F2 jumps to the gate immediately.
///
/// Every spawn goes through the game's own <c>EnemyManager</c>, so pooling, HP,
/// name, collider and our creature model attach are the normal ones.
/// </summary>
internal static class Gallery
{
    private const float BossSpacing = 18f;
    private const float CreatureSpacing = 6f;
    private const float BossDistance = 26f;
    private const float CreatureDistance = 40f;
    private const float RowDepth = 10f;
    private const int CreatureColumns = 14;
    private const float AccelerateAfterSeconds = 30f;

    private static readonly List<Enemy> Spawned = new List<Enemy>();

    private static bool built;
    private static bool accelerated;
    private static float accelerateAt;

    /// <summary>Read by the damage prefixes: no damage reaches the player while
    /// the gallery is up, so the models can be inspected at leisure.</summary>
    internal static bool Invincible => built;

    internal static void Tick(MyPlayer player)
    {
        if (!Plugin.GalleryKeys.Value || player == null || player.transform == null)
        {
            return;
        }

        if (Input.GetKeyDown(KeyCode.F1))
        {
            Build(player);
        }
        else if (Input.GetKeyDown(KeyCode.F2))
        {
            Accelerate("manual");
        }

        if (!built || accelerated || MyTime.time < accelerateAt)
        {
            return;
        }

        Accelerate("30 s timer");
    }

    private static void Build(MyPlayer player)
    {
        try
        {
            var manager = EnemyManager.Instance;
            if (manager == null)
            {
                return;
            }

            if (built)
            {
                Clear();
            }

            built = true;
            accelerated = false;
            accelerateAt = MyTime.time + AccelerateAfterSeconds;

            var camera = Camera.main;
            var forward = camera != null ? camera.transform.forward : player.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f)
            {
                forward = Vector3.forward;
            }

            forward.Normalize();
            var right = Vector3.Cross(Vector3.up, forward).normalized;
            var kinds = (EEnemy[])Enum.GetValues(typeof(EEnemy));

            var bosses = new List<EEnemy>();
            var creatures = new List<EEnemy>();
            for (var index = 0; index < kinds.Length; index++)
            {
                var kind = kinds[index];
                if (PoeEnemies.IsBossTier(kind))
                {
                    bosses.Add(kind);
                }
                else
                {
                    creatures.Add(kind);
                }
            }

            Plugin.Logger.LogInfo(
                $"gallery: spawning {bosses.Count} boss slot(s) and {creatures.Count} creature slot(s)");

            for (var index = 0; index < bosses.Count; index++)
            {
                var offset = (index - (bosses.Count - 1) * 0.5f) * BossSpacing;
                var position = Ground(player, forward * BossDistance + right * offset);
                var role = BossRoster.All.Count == 0
                    ? null
                    : BossRoster.All[index % BossRoster.All.Count].Id;
                SpawnBoss(manager, bosses[index], position, player, "boss", role);
            }

            for (var index = 0; index < creatures.Count; index++)
            {
                var row = index / CreatureColumns;
                var column = index % CreatureColumns;
                var count = Mathf.Min(CreatureColumns, creatures.Count - row * CreatureColumns);
                var offset = (column - (count - 1) * 0.5f) * CreatureSpacing;
                var position = Ground(
                    player,
                    forward * (CreatureDistance + row * RowDepth) + right * offset);
                SpawnCreature(manager, creatures[index], position, player);
            }

            Plugin.Logger.LogInfo(
                $"gallery: {Spawned.Count} model(s) up, frozen; player invincible; " +
                $"stage gate in {AccelerateAfterSeconds:0} s");
        }
        catch (Exception error)
        {
            built = false;
            Plugin.Logger.LogError($"gallery build failed: {error}");
        }
    }

    private static Vector3 Ground(MyPlayer player, Vector3 offset)
    {
        var position = player.transform.position + offset;
        if (Physics.Raycast(position + Vector3.up * 80f, Vector3.down, out var ground, 300f))
        {
            position = ground.point;
        }

        return position;
    }

    private static void SpawnBoss(
        EnemyManager manager,
        EEnemy kind,
        Vector3 position,
        MyPlayer player,
        string row,
        string role)
    {
        try
        {
            BossRoster.ForceNext(role);
            var boss = manager.SpawnBoss(kind, 0, EEnemyFlag.Boss, position, 1f);
            BossRoster.ClearForce();
            if (boss == null)
            {
                Plugin.Logger.LogWarning($"gallery: {kind} ({row}) returned null");
                return;
            }

            Freeze(boss, player);
            Spawned.Add(boss);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"gallery: {kind} ({row}) failed: {error.Message}");
        }
    }

    private static void SpawnCreature(
        EnemyManager manager,
        EEnemy kind,
        Vector3 position,
        MyPlayer player)
    {
        try
        {
            var data = DataManager.Instance == null
                ? null
                : DataManager.Instance.GetEnemyData(kind);
            if (data == null)
            {
                Plugin.Logger.LogWarning($"gallery: {kind} has no EnemyData");
                return;
            }

            var enemy = manager.SpawnEnemy(
                data, position, 1, true, EEnemyFlag.None, false, 1f);
            if (enemy == null)
            {
                Plugin.Logger.LogWarning($"gallery: {kind} spawn returned null");
                return;
            }

            Freeze(enemy, player);
            Spawned.Add(enemy);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"gallery: {kind} failed: {error.Message}");
        }
    }

    /// <summary>Stops movement and casts and turns the model toward the player so
    /// the camera-facing side is the front. AI state, collider and identity stay
    /// the game's own.</summary>
    private static void Freeze(Enemy enemy, MyPlayer player)
    {
        enemy.SetSpeedMultiplier(0f);
        enemy.DisableSpecialAttacks();

        var look = player.transform.position - enemy.transform.position;
        look.y = 0f;
        if (look.sqrMagnitude > 0.01f)
        {
            enemy.transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
        }
    }

    private static void Accelerate(string reason)
    {
        if (accelerated)
        {
            return;
        }

        try
        {
            var manager = EnemyManager.Instance;
            var summoner = manager == null ? null : manager.summonerController;
            if (summoner == null)
            {
                return;
            }

            var gate = summoner.GetMaxTimeLength();
            if (gate <= 1f)
            {
                return;
            }

            accelerated = true;
            MyTime.stageTimer = gate + 0.2f;
            Plugin.Logger.LogInfo(
                $"gallery: stage clock jumped to {MyTime.stageTimer:0.0} s ({reason})");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"gallery: accelerate failed: {error.Message}");
        }
    }

    private static void Clear()
    {
        for (var index = Spawned.Count - 1; index >= 0; index--)
        {
            var enemy = Spawned[index];
            if (enemy != null)
            {
                enemy.Kill("gallery");
            }
        }

        Spawned.Clear();
        Plugin.Logger.LogInfo("gallery: previous models cleared");
    }
}
