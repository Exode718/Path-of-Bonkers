using System;
using Actors.Enemies;
using Assets.Scripts._Data;
using Assets.Scripts._Data.Tomes;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Managers;
using Assets.Scripts.Utility;
using Inventory__Items__Pickups;
using UnityEngine;
using Wraeclast.Data;

namespace Wraeclast.Dev;

/// <summary>
/// Live check, gated by <c>Debug.RunTestKeys</c> (off by default):
/// F1 toggles invincibility + no clip (<see cref="TestMode"/>), F2 spawns the
/// five Atlas bosses (Elder/Shaper/Maven plus the Searing Exarch and the Eater
/// of Worlds) frozen for a size comparison, **F5/F6/F7 teleport to
/// floors 1/2/3 and F8 to the final arena** (0.9.119 — the game's own stage
/// loader, so the floor is a real floor), F9 jumps the stage clock to 6:00
/// remaining (the first swarm wave), F10 finishes every boss <see cref="Run.BossWindow"/> tracks,
/// F11 is the old alias of the arena load, `Ctrl+F8` probes the tracked bosses
/// (renderer, bounds, scale), F12 pauses one-way for a screenshot, F3 grants the
/// Quantity tome at two rarities (S3), **F4 grants 10 character levels one
/// level-up at a time (R11b item 7)** and **Ctrl+F4 force-spawns a wave of
/// twelve native enemies around the player**. The harness only calls the game's
/// own systems.
/// </summary>
internal static class RunTest
{
    /// <summary>R11b item 7: a debug upgrade pick is a full card choice, so the
    /// 10 levels are applied one per level-up (the game pauses on each draft)
    /// rather than jumped in one XP grant, which would fire the draft only at
    /// the final level.</summary>
    private const int LevelsPerPress = 10;

    /// <summary>Normal enemies, cycled around the player for the force-wave key;
    /// every row is a real <c>EEnemy</c> the game's own spawner knows.</summary>
    private static readonly EEnemy[] WaveKinds =
    {
        EEnemy.Skeleton, EEnemy.Ghoul, EEnemy.Slime, EEnemy.Goblin,
        EEnemy.GoblinStrong, EEnemy.GoblinTank, EEnemy.BoomerSpider, EEnemy.Golem,
        EEnemy.Bee, EEnemy.Scorpion, EEnemy.Zombie, EEnemy.Bandit
    };

    private static int levelsPending;
    private static int lastQueuedLevel = -1;

    internal static void Tick(MyPlayer player)
    {
        if (!Plugin.RunTestKeys.Value || player == null || player.transform == null)
        {
            return;
        }

        var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        if (Input.GetKeyDown(KeyCode.F5))
        {
            if (!ctrl)
            {
                TeleportFloor(1);
            }
        }
        else if (Input.GetKeyDown(KeyCode.F6))
        {
            if (!ctrl)
            {
                TeleportFloor(2);
            }
        }
        else if (Input.GetKeyDown(KeyCode.F7))
        {
            if (!ctrl)
            {
                TeleportFloor(3);
            }
        }
        else if (Input.GetKeyDown(KeyCode.F8))
        {
            if (ctrl)
            {
                DescribeBosses();
            }
            else
            {
                LoadFinalStage();
            }
        }
        else if (Input.GetKeyDown(KeyCode.F9))
        {
            SkipToGate();
        }
        else if (Input.GetKeyDown(KeyCode.F10))
        {
            KillBosses();
        }
        else if (Input.GetKeyDown(KeyCode.F11))
        {
            LoadFinalStage();
        }
        else if (Input.GetKeyDown(KeyCode.F1))
        {
            TestMode.Toggle(player);
        }
        else if (Input.GetKeyDown(KeyCode.F2))
        {
            SpawnLineup();
        }
        else if (Input.GetKeyDown(KeyCode.F12))
        {
            PauseForShot();
        }
        else if (Input.GetKeyDown(KeyCode.F3))
        {
            GrantTomes();
        }
        else if (Input.GetKeyDown(KeyCode.F4))
        {
            if (ctrl)
            {
                SpawnWave(player);
            }
            else
            {
                GrantLevels(player);
            }
        }

        AdvanceLevelGrant(player);
        TestMode.Sustain(player);
    }

    /// <summary>
    /// 0.9.119: F5/F6/F7 — load floor 1, 2 or 3 through the game's own stage
    /// loader (<c>MapController.LoadNextStage</c>), the same path the portal and
    /// the endless cycle use. It reads <c>stages[index + 1]</c>, hence
    /// <c>index = floor - 2</c>; the final-boss flag is cleared first, or a floor
    /// reached from the arena would keep being treated as the arena
    /// (<see cref="Run.BossWindow"/> does the same in its endless reload).
    /// </summary>
    private static void TeleportFloor(int floor)
    {
        try
        {
            if (MapController.IsMainMenu())
            {
                Plugin.Logger.LogInfo($"run test: floor {floor} — no run yet, the key does nothing");
                return;
            }

            var before = MapController.GetStageIndex();
            MapController.isFinalBossStage = false;
            MapController.index = floor - 2;
            MapController.LoadNextStage();
            Plugin.Logger.LogInfo(
                $"run test: floor {floor} requested (stage index {before} -> {MapController.GetStageIndex()})");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"run test: floor {floor} teleport failed: {error}");
        }
    }

    /// <summary>
    /// S3 probe: grants one stack of the Quantity tome at Rare, then another at
    /// Legendary, through the game's own upgrade entry
    /// (<c>PlayerInventory.AddUpgrade</c> with <c>TomeData.GetUpgradeOffer</c>,
    /// the call the level-up screen makes), logging each card text. The same
    /// tome must state two different numbers, the offer must carry the same
    /// rarity multiplier, and the next cast must log the extra projectile(s).
    /// </summary>
    private static void GrantTomes()
    {
        var player = MyPlayer.Instance;
        var inventory = player == null ? null : player.inventory;
        var manager = DataManager.Instance;
        if (inventory == null || manager == null)
        {
            Plugin.Logger.LogWarning("run test: no inventory for the tome grant");
            return;
        }

        Grant(inventory, manager, ETome.Quantity, ERarity.Rare);
        Grant(inventory, manager, ETome.Quantity, ERarity.Legendary);
    }

    private static void Grant(PlayerInventory inventory, DataManager manager, ETome tome, ERarity rarity)
    {
        try
        {
            var data = manager.GetTome(tome);
            var card = data == null ? null : data.TryCast<IUpgradable>();
            if (card == null)
            {
                Plugin.Logger.LogWarning($"run test: no upgrade card for {tome}");
                return;
            }

            inventory.AddUpgrade(card, data.GetUpgradeOffer(rarity), rarity);
            TomeSwap.TryGet(tome, out var definition);
            Plugin.Logger.LogInfo(
                $"run test: granted {tome} [{rarity} x{TomeSwap.Multiplier(rarity):0.##}] " +
                $"\"{TomeSwap.CardText(definition, rarity)}\"");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"run test: {tome} grant failed: {error.Message}");
        }
    }

    /// <summary>R11b item 7: F4 — queues ten character levels. They are applied
    /// one at a time by <see cref="AdvanceLevelGrant"/> through the game's own
    /// XP path (<c>PlayerInventory.AddXp</c> → <c>PlayerXp.AddXp</c>), so every
    /// level-up opens its own draft; granting all ten at once would only fire
    /// the final level's card.</summary>
    private static void GrantLevels(MyPlayer player)
    {
        try
        {
            var inventory = player == null ? null : player.inventory;
            var xp = inventory == null ? null : inventory.playerXp;
            if (xp == null)
            {
                Plugin.Logger.LogWarning("run test: no XP component for the level grant");
                return;
            }

            levelsPending = LevelsPerPress;
            lastQueuedLevel = -1;
            Plugin.Logger.LogInfo(
                $"run test: +{LevelsPerPress} levels queued from level {xp.level} (one draft per level-up)");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"run test: level grant failed: {error.Message}");
        }
    }

    /// <summary>Applies one queued level per tick (the game's own <c>AddXp</c>
    /// queues into <c>pendingXp</c>, which <c>LateUpdate</c> turns into a level
    /// and a draft). The tick driver is skipped while <c>MyTime.paused</c>, so
    /// the sequence naturally waits for the player to pick each card.</summary>
    private static void AdvanceLevelGrant(MyPlayer player)
    {
        if (levelsPending <= 0)
        {
            return;
        }

        try
        {
            var inventory = player == null ? null : player.inventory;
            var xp = inventory == null ? null : inventory.playerXp;
            if (xp == null)
            {
                levelsPending = 0;
                Plugin.Logger.LogWarning("run test: level grant stopped, no XP component");
                return;
            }

            // Wait until the level queued on the previous tick has landed (the
            // level-up screen pauses the tick driver meanwhile).
            if (xp.level == lastQueuedLevel)
            {
                return;
            }

            if (xp.level >= XpUtility.maxLevel - 1)
            {
                levelsPending = 0;
                Plugin.Logger.LogInfo($"run test: level grant stopped at the cap ({xp.level})");
                return;
            }

            var target = xp.level + 1;
            var needed = XpUtility.LevelToXp(target) - xp.xp;
            if (needed <= 0)
            {
                needed = 1;
            }

            lastQueuedLevel = xp.level;
            inventory.AddXp(needed);
            levelsPending--;
            Plugin.Logger.LogInfo(
                $"run test: level {target} queued (+{needed} xp, {levelsPending} left)");

            if (levelsPending == 0)
            {
                Plugin.Logger.LogInfo("run test: +10 levels queued, pick the draft to continue");
            }
        }
        catch (Exception error)
        {
            levelsPending = 0;
            Plugin.Logger.LogWarning($"run test: level grant failed: {error.Message}");
        }
    }

    /// <summary>R11b item 7: Ctrl+F4 — force-spawns a wave of twelve native
    /// enemies in a ring around the player through <c>EnemyManager.SpawnEnemy</c>
    /// (the same entry the gallery and the game's own summoners use), so
    /// pooling, HP, AI and our creature models are the normal ones. Repeatable:
    /// unlike the timeline's <c>StartFinalSwarm</c>, which is once per floor.</summary>
    private static void SpawnWave(MyPlayer player)
    {
        try
        {
            var manager = EnemyManager.Instance;
            var dataManager = DataManager.Instance;
            if (manager == null || dataManager == null || player == null)
            {
                return;
            }

            var spawned = 0;
            for (var index = 0; index < WaveKinds.Length; index++)
            {
                var data = dataManager.GetEnemyData(WaveKinds[index]);
                if (data == null)
                {
                    continue;
                }

                var angle = 360f * index / WaveKinds.Length + UnityEngine.Random.Range(-10f, 10f);
                var direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                var distance = UnityEngine.Random.Range(8f, 14f);
                var position = player.transform.position + direction * distance;
                if (Physics.Raycast(position + Vector3.up * 40f, Vector3.down, out var ground, 100f))
                {
                    position = ground.point;
                }
                else
                {
                    position.y = player.transform.position.y;
                }

                if (manager.SpawnEnemy(data, position, 1, true, EEnemyFlag.None, false, 1f) != null)
                {
                    spawned++;
                }
            }

            Plugin.Logger.LogInfo($"run test: force wave -> {spawned} enemies around the player");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"run test: force wave failed: {error.Message}");
        }
    }

    /// <summary>F2: the five Atlas bosses in two rows in front of the camera,
    /// with their speed at 0 and their attack kits off, so the models can be
    /// compared at the same size without the run ending. The Searing Exarch and
    /// the Eater of Worlds stand in the near row (the two S2c meshes), the
    /// Elder/Shaper/Maven trio in the far row. The player is not paused: F1
    /// gives invincibility and no clip for the walk-around.</summary>
    private static void SpawnLineup()
    {
        try
        {
            var manager = EnemyManager.Instance;
            var engine = GameManager.Instance;
            var player = engine == null ? null : engine.GetPlayer();
            if (manager == null || player == null)
            {
                return;
            }

            var camera = Camera.main;
            var forward = camera != null ? camera.transform.forward : player.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f)
            {
                forward = Vector3.forward;
            }

            forward.Normalize();
            var right = Vector3.Cross(Vector3.up, forward).normalized;
            var near = player.transform.position + forward * 16f;
            var far = player.transform.position + forward * 26f;

            SpawnOne(manager, player, EEnemy.Ent2, near - right * 7f, "searing-exarch");
            SpawnOne(manager, player, EEnemy.Ent3, near + right * 7f, "eater-of-worlds");
            SpawnOne(manager, player, EEnemy.Ent1, far - right * 14f, "elder");
            SpawnOne(manager, player, EEnemy.Pharaoh3, far, "shaper");
            SpawnOne(manager, player, EEnemy.Ghostham, far + right * 14f, "maven");

            Plugin.Logger.LogInfo("run test: lineup up (5 bosses), frozen (speed 0, attack kits off)");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"run test: lineup spawn failed: {error}");
        }
    }

    private static void SpawnOne(
        EnemyManager manager,
        MyPlayer player,
        EEnemy kind,
        Vector3 position,
        string label)
    {
        if (Physics.Raycast(position + Vector3.up * 80f, Vector3.down, out var ground, 300f))
        {
            position = ground.point;
        }

        // Anchor to the player's own floor level: a raycast over a forest can
        // hit a canopy or a prop and put the boss 20 units in the air or under
        // the terrain, which makes the model impossible to inspect.
        position.y = player.transform.position.y;

        BossRoster.ForceNext(label);
        var boss = manager.SpawnBoss(kind, 0, EEnemyFlag.Boss, position, 1f);
        BossRoster.ClearForce();
        if (boss == null)
        {
            Plugin.Logger.LogWarning($"run test: {label} ({kind}) returned null");
            return;
        }

        boss.SetSpeedMultiplier(0f);
        boss.DisableSpecialAttacks();
        var look = player.transform.position - boss.transform.position;
        look.y = 0f;
        if (look.sqrMagnitude > 0.01f)
        {
            boss.transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
        }

        Plugin.Logger.LogInfo(
            $"run test: {label} ({kind}) at {position}, player {player.transform.position}");
    }

    /// <summary>One-way: the pause is only ever entered from here, so a
    /// screenshot can be taken without the boss killing the player. Restart the
    /// game to resume.</summary>
    private static void PauseForShot()
    {
        try
        {
            MyTime.Pause();
            Plugin.Logger.LogInfo("run test: paused for a screenshot (restart to resume)");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"run test: pause failed: {error.Message}");
        }
    }

    private static void DescribeBosses()
    {
        try
        {
            Run.BossWindow.DescribeBossesForTest();
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"run test: boss probe failed: {error.Message}");
        }
    }

    private static void SkipToGate()
    {
        try
        {
            var manager = EnemyManager.Instance;
            var summoner = manager == null ? null : manager.summonerController;
            if (summoner == null)
            {
                return;
            }

            // The window's own gate (the stage's native length, or the configured
            // FloorSeconds), so the key lands exactly on the beat on every floor.
            var gate = Run.BossWindow.GateSeconds();
            if (gate <= 1f)
            {
                return;
            }

            // F9 lands 6:00 before the gate — the first swarm wave — so a swarm
            // FPS check does not need to play four minutes of floor first.
            const float remaining = 360f;
            var target = Mathf.Max(1f, gate - remaining);
            MyTime.stageTimer = target;
            Plugin.Logger.LogInfo(
                $"run test: stage clock -> {remaining:0} s remaining " +
                $"({target:0.0} s elapsed of a {gate:0} s floor, first swarm)");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"run test: skip to gate failed: {error.Message}");
        }
    }

    private static void KillBosses()
    {
        try
        {
            Run.BossWindow.KillBossesForTest();
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"run test: boss kill failed: {error.Message}");
        }
    }

    private static void LoadFinalStage()
    {
        try
        {
            Plugin.Logger.LogInfo("run test: loading the final boss stage directly");
            MapController.LoadFinalStage();
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"run test: final stage load failed: {error.Message}");
        }
    }
}
