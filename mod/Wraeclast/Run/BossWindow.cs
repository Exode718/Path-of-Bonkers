using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Game.Combat;
using Assets.Scripts.Game.Spawning.New;
using Assets.Scripts.Managers;
using Assets.Scripts.Utility;
using HarmonyLib;
using TMPro;
using UnityEngine;
using Wraeclast.Data;

namespace Wraeclast.Run;

/// <summary>
/// W1 — the run's floor race (DOC.md "Run structure", floors 1–2 only).
///
/// Megabonk's own stage flow is a survival timer: at the stage time the wave
/// director stops the normal summoners, starts one swarm of ghosts and waits for
/// the player to walk to the boss altar and press the interact key. This class
/// turns that into the floor race the design asks for, without replacing any
/// native system:
///
/// - at the stage time the floor boss is summoned with the game's own
///   <c>SummonerController.SpawnStageBoss</c> (the call the boss altar makes)
///   in front of the player, and the spawned bosses are handed to the altar's
///   <c>bossEnemies</c> list, so the boss-tier spawn, the objective arrow and
///   the floor portal stay the game's;
/// - the one ghost wave is the game's own final swarm
///   (<c>SummonerController.StartFinalSwarm</c>, the same call the timeline
///   makes); the stage's own (shorter) timeline must not start its final swarm
///   before our beat, so the ghost wave is always ours and always single, and
///   every later timeline event is refused while the window is open;
/// - the deadline is absolute on the stage clock: <c>beat + BossWindowSeconds</c>
///   (10:00 + 60 s = 11:00), and a 60 s countdown is drawn under the stage timer
///   (the game has no screen-space boss bar; the boss health is the world-space
///   <c>EnemyHpBar</c>); at the deadline a boss still alive ends the run through
///   <c>PlayerHealth.KillPlayer</c>, i.e. the normal death screen and run end;
/// - the kill opens the floor portal, but the floor does not advance on the kill:
///   the portal is taken by us at the deadline (<c>InteractablePortal.Interact</c>
///   is the coroutine that teleports and loads the next stage), so a boss killed
///   before 11:00 leaves the rest of the floor to play. The player may take the
///   portal manually earlier.
///
/// R1: the beat is the design's <see cref="Plugin.FloorSeconds"/> (600 s) on
/// every floor, not the stage's own timeline length (Megabonk ships 600 / 540 /
/// 480 s for floors 1-3). A boss the dev keys (<c>BossRun</c>) spawned earlier
/// is honoured: the gate never spawns a second one while a boss is alive, and a
/// boss killed before the beat never counts as the beat's kill, so the 10:00
/// window still runs.
///
/// R10: an altar boss summoned before the beat consumes the floor's beat (no
/// second boss, no ghost wave, no countdown at 10:00). The early fight keeps the
/// floor's own deadline, and a kill opens the portal while the floor still runs
/// to 11:00.
///
/// Floor 3 (W2) has no floor boss at all: at the gate the class takes the
/// game's own <c>FINAL_BOSS</c> transition (<see cref="MyPlayer.TeleportPlayerNextStage"/>
/// then <c>MapController.LoadFinalStage</c>, the two calls
/// <c>InteractableBossSpawnerFinal</c> makes) with no ghost wave and no
/// countdown. On the final boss stage the arena's own <c>FinalFightController</c>
/// spawns and runs the boss. When it dies the run does not finish and the floor
/// is not reloaded: the final portal is refused and the arena turns into the
/// game's own endless phase — <c>SummonerController.StartFinalSwarm</c> keeps
/// spawning the final wave, whose ghost tiers (Ghost → Greater Ghost → Ghost
/// Purple → Ghost Red) and target count escalate on <c>MyTime.finalSwarmTimer</c>
/// exactly as in vanilla Megabonk's post-final-boss survival.
/// </summary>
internal static class BossWindow
{
    /// <summary>Distance in front of the player for the fallback spawn, the
    /// same value the dev harness uses.</summary>
    private const float SpawnDistance = 20f;

    /// <summary>The fallback (no altar in the scene) starts the next stage on
    /// the kill; the portal path is native and needs no timer.</summary>
    private const float PortalPollSeconds = 0.25f;

    /// <summary>Default floor length: the design's 10 minutes. The stage's own
    /// timeline is shorter on the later floors (Megabonk ships 600 / 540 / 480 s),
    /// so the beat is ours unless <see cref="Plugin.FloorSeconds"/> asks for the
    /// stage's own value (0).</summary>
    private const float DefaultFloorSeconds = 600f;

    /// <summary>How long the floor waits for the portal before the fallback
    /// teleport, and how long the teleport effect gets before the stage load.</summary>
    private const float PortalFallbackSeconds = 1.5f;

    /// <summary>How long the arena waits after the final boss dies, so the
    /// cinematic and the death dissolve finish before the endless ghost swarm
    /// starts.</summary>
    private const float EndlessGraceSeconds = 3f;

    /// <summary>A beat whose boss spawn failed retries at this interval, so the
    /// floor can never be left with no boss, no waves and no portal.</summary>
    private const float SpawnRetrySeconds = 5f;

    private static readonly List<Enemy> Bosses = new List<Enemy>();

    /// <summary><c>StartFinalSwarm</c> is private; the lookup is cached and a
    /// missing name only costs the ghost wave, never a run.</summary>
    private static readonly MethodInfo StartFinalSwarm =
        AccessTools.Method(typeof(SummonerController), "StartFinalSwarm");

    private static bool windowOpen;
    private static bool windowRan;
    private static bool bossKilled;
    private static bool stageBossSpawning;
    private static bool earlyAltarBoss;
    private static bool portalTaken;
    private static bool fallbackTeleported;
    private static bool finalRequested;
    private static bool finalKilled;
    private static bool endlessStarted;
    private static bool loggedFinal;
    private static bool loggedEndless;
    private static bool loggedSuppression;
    private static bool loggedSwarm;
    private static bool loggedAnchor;
    private static bool uiUnavailable;
    private static float deadline;
    private static float bossKilledAt;
    private static float nextPortalPoll;
    private static float nextSpawnAttempt;
    private static bool finalTeleported;
    private static float nextFinalLoadAttempt;
    private static float endlessAt;
    private static int lastStage = int.MinValue;
    private static int lastShownSecond = -1;
    private static float lastStageTimer;
    private static TextMeshProUGUI countdown;
    private static GameObject countdownRoot;

    /// <summary>The altar interaction is refused once the window owns the floor:
    /// the player cannot summon a second boss beside the one the race spawned.</summary>
    internal static bool BlocksAltar => windowOpen || windowRan;

    /// <summary>Timeline events (swarm and miniboss waves) are refused while the
    /// floor is a race: the ghost wave is the only wave after the boss. R1 adds
    /// the stage's own final swarm to that: the later floors ship 540 / 480 s
    /// timelines, and their native gate must not start a ghost wave before our
    /// 600 s beat, or the floor would get one early and a second one at 10:00.</summary>
    internal static bool BlocksTimelineEvent
    {
        get
        {
            if (!Plugin.BossWindowEnabled.Value)
            {
                return false;
            }

            if (windowOpen)
            {
                return true;
            }

            if (windowRan)
            {
                // The floor's waves end with the beat: from the boss spawn to the
                // stage change the screen holds only the boss and its ghost wave.
                return true;
            }

            try
            {
                if (MapController.IsFinalBossStage())
                {
                    return false;
                }

                var manager = EnemyManager.Instance;
                var summoner = manager == null ? null : manager.summonerController;
                if (summoner == null)
                {
                    return false;
                }

                return MyTime.stageTimer >= NativeStageTime(summoner);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// R12 (operator, 2026-11-01): killing the floor boss **before** the beat is
    /// a bonus, never a shortcut. The game's own reaction to a stage-boss death
    /// (<c>SummonerController.OnBossDied</c>) stops the summoners and rewrites
    /// the stage's final-swarm clock, which is what pulled the "10 seconds
    /// before the final wave" in three minutes early and blanked the rest of the
    /// floor. It is skipped while our beat owns the floor, so the waves keep
    /// flowing until 10:00 and the kill only removes the boss from the last
    /// minute.
    /// </summary>
    internal static bool BlocksBossDiedNotice
    {
        get
        {
            if (!Plugin.BossWindowEnabled.Value || windowRan)
            {
                return false;
            }

            try
            {
                if (MapController.IsFinalBossStage() || MapController.IsLastStage())
                {
                    return false;
                }

                return MyTime.stageTimer < GateSeconds();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Set around our own <c>StartFinalSwarm</c> call.</summary>
    internal static bool CallingGhostWave { get; private set; }

    private static bool loggedBossDiedSkip;

    /// <summary>One line per floor when the native boss-death flow is refused,
    /// so the log states why the waves did not stop (R12).</summary>
    internal static void NoteBossDiedSkipped()
    {
        if (loggedBossDiedSkip)
        {
            return;
        }

        loggedBossDiedSkip = true;
        Plugin.Logger.LogInfo(
            "boss window: the floor boss died before the beat — the game's own boss-death flow (stop the " +
            "waves, rewrite the final-swarm clock) is skipped; the waves run until 10:00 and the kill only " +
            "removes the boss from the last minute");
    }

    /// <summary>
    /// The one ghost wave of a floor is ours (R1), so the native timeline's own
    /// <c>StartFinalSwarm</c> is refused on floors 1–2. The final arena and
    /// floor 3 keep the game's own flow.
    /// </summary>
    internal static bool BlocksFinalSwarm
    {
        get
        {
            if (!Plugin.BossWindowEnabled.Value || CallingGhostWave)
            {
                return false;
            }

            try
            {
                return !MapController.IsFinalBossStage() && !MapController.IsLastStage();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>The final portal would run <c>DoFinishGame</c> (the stats screen).
    /// The design keeps the run going instead, so the portal is refused while the
    /// endless loop owns the final boss stage.</summary>
    internal static bool BlocksFinalPortal =>
        Plugin.BossWindowEnabled.Value && Plugin.AutoNextFloor.Value && MapController.IsFinalBossStage();

    /// <summary>
    /// The final boss stage is its own scene and <c>LoadFinalStage</c> keeps the
    /// previous stage's <c>StageData</c> (and its timeline). The arena's own
    /// <c>FinalFightController</c> and, after the kill, the endless final swarm
    /// own the spawning there, so the floor timeline must not also run: its
    /// events would add floor waves beside the swarm, and a final map without a
    /// timeline must not be dereferenced. <c>SummonerController.Tick</c> still
    /// ticks the swarm summoner after the skipped timeline.
    /// </summary>
    internal static bool BlocksArenaTimeline
    {
        get
        {
            if (!Plugin.BossWindowEnabled.Value)
            {
                return false;
            }

            try
            {
                return MapController.IsFinalBossStage();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>One load-time line so a run log states the window is in force.</summary>
    internal static void Report()
    {
        if (!Plugin.BossWindowEnabled.Value)
        {
            Plugin.Logger.LogInfo("boss window off: Megabonk's stage flow stays as it is");
            return;
        }

        var length = Plugin.FloorSeconds.Value > 1f
            ? $"{Plugin.FloorSeconds.Value:0} s on every floor"
            : "each stage's own length (600 / 540 / 480 s)";
        Plugin.Logger.LogInfo(
            $"boss window: {length} of waves, then the floor boss and the final wave " +
            $"({Plugin.BossWindowSeconds.Value:0} s to kill it or the run ends), one ghost wave; " +
            "the portal is taken at the same offset on the stage clock " +
            "(an altar boss summoned before the beat consumes the beat: no second boss, no ghost wave); " +
            "floor 3 has no floor boss (straight to the final boss, no countdown) and the final boss " +
            "kill loops into the endless cycle");
    }

    /// <summary>The whole window is covered by the game's own boss spawn entry
    /// point, so the altar, the dev keys and this class are all seen.</summary>
    internal static void OnBossSpawned(Enemy enemy, EEnemyFlag flag)
    {
        if (!Plugin.BossWindowEnabled.Value || enemy == null)
        {
            return;
        }

        // Flags are a bitmask: the final boss spawns as StageBoss | FinalBoss
        // (36), so equality against StageBoss would miss it.
        if ((flag & (EEnemyFlag.StageBoss | EEnemyFlag.Boss)) == 0)
        {
            return;
        }

        try
        {
            Bosses.Add(enemy);

            // A new floor boss is a live race again: an altar re-summon after an
            // early kill must not inherit the previous kill's latch.
            bossKilled = false;
            bossKilledAt = 0f;

            // R10: a stage boss the altar summoned before the beat consumes the
            // floor's beat. `SpawnStageBoss` is the altar's own call (the dev
            // keys go straight to `EnemyManager.SpawnBoss`), and our own gate
            // spawn is never before the gate.
            if (stageBossSpawning && MyTime.stageTimer < GateSeconds())
            {
                earlyAltarBoss = true;
                Plugin.Logger.LogInfo(
                    $"boss window: floor boss {Name(enemy)} summoned by the altar at {MyTime.stageTimer:0} s, " +
                    $"before the {GateSeconds():0} s beat: this kill consumes the beat");
            }

            Plugin.Logger.LogInfo(
                $"boss window: floor boss {Name(enemy)} spawned ({flag})" +
                (windowOpen ? ", window already open" : ""));
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"boss window: boss spawn bookkeeping failed: {error.Message}");
        }
    }

    /// <summary>Set around <c>SummonerController.SpawnStageBoss</c> by the patch,
    /// so a boss spawned inside it can be attributed to the altar (R10).</summary>
    internal static void NoteStageBossSpawning(bool spawning)
    {
        stageBossSpawning = spawning;
    }

    /// <summary>The two <c>Enemy.EnemyDied</c> overloads both land here.</summary>
    internal static void OnEnemyDied(Enemy enemy)
    {
        if (!Plugin.BossWindowEnabled.Value || enemy == null || !Remove(enemy))
        {
            return;
        }

        // The latch is only set when the floor has no boss left: a stage that
        // spawned several keeps a live race until the last one is down.
        bossKilled = !AnyLive();
        if (bossKilled)
        {
            bossKilledAt = MyTime.time;
            HideCountdown();
        }

        var final = false;
        try
        {
            final = MapController.IsFinalBossStage();
            if (final)
            {
                finalKilled = true;
            }
        }
        catch (Exception)
        {
            // A missing MapController only costs the endless signal.
        }

        // R5: the boss pays a card from the reward table (1 on a floor boss,
        // 2 on the final boss). The tier is the floor; the grant goes through
        // the game's own upgrade entry, so the stat pipeline stays native.
        try
        {
            var floor = final ? 3 : Mathf.Clamp(MapController.GetStageIndex() + 1, 1, 3);
            RewardPool.GrantBossReward(floor, final ? 2 : 1, $"boss floor {floor}");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"boss window: boss reward failed: {error.Message}");
        }

        Plugin.Logger.LogInfo($"boss window: floor boss {Name(enemy)} down");
    }

    /// <summary>Dev harness (<c>Debug.RunTestKeys</c>): probes every tracked boss
    /// so a live invisibility report can name the cause (renderer state, world
    /// position, bounds and scale of the attached model).</summary>
    internal static void DescribeBossesForTest()
    {
        for (var index = 0; index < Bosses.Count; index++)
        {
            var boss = Bosses[index];
            if (boss == null)
            {
                continue;
            }

            var root = boss.transform.Find("WraeclastModel");
            var body = root == null ? null : root.Find("Body");
            var renderer = body == null ? null : body.GetComponent<Renderer>();
            var skinned = renderer as SkinnedMeshRenderer;
            var mesh = skinned == null ? null : skinned.sharedMesh;

            Plugin.Logger.LogInfo(
                $"run test boss probe: {Name(boss)} root {boss.transform.position}, " +
                $"model {(root == null ? "none" : root.position.ToString())}, " +
                $"scale {(root == null ? "?" : root.lossyScale.ToString())}, " +
                $"body {(body == null ? "none" : body.position.ToString())}, " +
                $"enabled {(renderer == null ? "none" : renderer.enabled.ToString())}, " +
                $"visible {(renderer == null ? "?" : renderer.isVisible.ToString())}, " +
                $"layer {(body == null ? "?" : body.gameObject.layer.ToString())}, " +
                $"bounds {(renderer == null ? "none" : renderer.bounds.ToString())}, " +
                $"local {(skinned == null ? "none" : skinned.localBounds.ToString())}, " +
                $"mesh {(mesh == null ? "none" : mesh.bounds.ToString())}");
        }
    }

    /// <summary>Dev harness (<c>Debug.RunTestKeys</c>): finishes every boss this
    /// class tracks with one player-weapon hit, so the floor or final boss can be
    /// cleared without a fight. (<c>Enemy.Kill</c> only reports a death source —
    /// it builds a zero-damage container — so the lethal hit must be damage.)</summary>
    internal static void KillBossesForTest()
    {
        for (var index = Bosses.Count - 1; index >= 0; index--)
        {
            var boss = Bosses[index];
            if (boss == null)
            {
                continue;
            }

            var container = new DamageContainer(1f, "run test");
            container.damage = 1e9f;
            container.damageEffect = EDamageEffect.None;
            Plugin.Logger.LogInfo($"run test: finishing {Name(boss)}");
            boss.DamageFromPlayerWeapon(container);
        }
    }

    /// <summary>Ticked from <c>MyPlayer.Update</c>, i.e. only with a live player
    /// and never while the game is paused (<c>MyTime.paused</c> gates the
    /// caller).</summary>
    internal static void Tick(MyPlayer player)
    {
        if (!Plugin.BossWindowEnabled.Value || player == null || player.transform == null)
        {
            return;
        }

        try
        {
            var manager = GameManager.Instance;
            if (manager == null || !manager.isPlaying)
            {
                return;
            }

            var finalArena = MapController.IsFinalBossStage();
            var stage = MapController.GetStageIndex();

            // The final boss stage is its own scene: it keeps the last stage
            // index, and the arena rewinds the clock on the kill, so the stage
            // reset must not touch the endless state while it is loaded.
            if (!finalArena)
            {
                if (stage != lastStage || MyTime.stageTimer + 1f < lastStageTimer)
                {
                    ResetStage(stage);
                }

                lastStageTimer = MyTime.stageTimer;
            }

            if (finalArena)
            {
                TickFinalFight();
                return;
            }

            var summoner = EnemyManager.Instance == null
                ? null
                : EnemyManager.Instance.summonerController;
            if (summoner == null)
            {
                return;
            }

            // R12: the native final-swarm counter must not run before our beat.
            // The early boss kill used to start it — the "10 seconds before the
            // final wave" the operator reported — so it is held at 0 while the
            // floor is still on its normal waves. A plain static, value write.
            if (!windowRan && MyTime.stageTimer < GateSeconds(summoner))
            {
                MyTime.finalSwarmTimer = 0f;
            }

            // Floor 3 has no floor boss: at the gate it goes straight to the
            // final boss, with no ghost wave and no countdown.
            if (MapController.IsLastStage())
            {
                TickFinalFloor(player, summoner);
                return;
            }

            if (windowOpen)
            {
                UpdateWindow(player);
            }
            else if (!windowRan)
            {
                TryOpenWindow(player, summoner);
            }

            // R10: the kill opens the portal but the floor is not left on the
            // kill; the portal is taken at the deadline, so the rest of the
            // floor is the player's.
            if (bossKilled && !portalTaken && FloorEnded(summoner))
            {
                AdvanceFloor(player);
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"boss window tick failed: {error.Message}");
        }
    }

    private static void ResetStage(int stage)
    {
        lastStage = stage;
        lastStageTimer = -1f;
        windowOpen = false;
        windowRan = false;
        bossKilled = false;
        stageBossSpawning = false;
        earlyAltarBoss = false;
        portalTaken = false;
        fallbackTeleported = false;
        finalRequested = false;
        finalKilled = false;
        endlessStarted = false;
        finalTeleported = false;
        nextFinalLoadAttempt = 0f;
        endlessAt = 0f;
        deadline = 0f;
        bossKilledAt = 0f;
        nextSpawnAttempt = 0f;
        uiUnavailable = false;
        loggedBossDiedSkip = false;
        lastShownSecond = -1;
        Bosses.Clear();
        HideCountdown();
    }

    /// <summary>
    /// Floor 3 (W2). No floor boss, no ghost wave, no countdown: at the stage
    /// time the game's own FINAL_BOSS transition is taken — the player teleport
    /// effect, then <c>MapController.LoadFinalStage()</c>, exactly the two calls
    /// <c>InteractableBossSpawnerFinal</c> makes. The arena's
    /// <c>FinalFightController</c> then spawns and drives the final boss.
    ///
    /// R13: the load is taken in the same frame as the teleport, like
    /// <see cref="AdvanceFloor"/>'s no-portal fallback. Deferring it to a later
    /// tick left the operator on floor 3 with the teleport effect and no floor
    /// (invisible, standing still): the transition sets the cutscene flag, and
    /// from there the player tick this class runs on (and the clock it reads) is
    /// not something to rely on, so the deferred load was never reached and the
    /// fade stayed up. The vanilla altar can wait because its coroutine is not on
    /// the player's tick; ours is.
    /// </summary>
    private static void TickFinalFloor(MyPlayer player, SummonerController summoner)
    {
        var gate = GateSeconds(summoner);
        if (MyTime.stageTimer < gate)
        {
            return;
        }

        if (!finalRequested)
        {
            finalRequested = true;
            HideCountdown();
            Plugin.Logger.LogInfo(
                $"boss window: floor {lastStage + 1} has no floor boss at {MyTime.stageTimer:0.0} s, " +
                "straight to the final boss (no ghost wave, no countdown)");

            if (!Plugin.AutoNextFloor.Value)
            {
                Plugin.Logger.LogInfo(
                    "boss window: AutoNextFloor is off, the final stage is left to the FINAL_BOSS altar");
                return;
            }
        }
        else if (!Plugin.AutoNextFloor.Value)
        {
            return;
        }

        if (!finalTeleported)
        {
            try
            {
                var manager = GameManager.Instance;
                if (manager != null)
                {
                    manager.cutscene = true;
                }

                player.TeleportPlayerNextStage();
                finalTeleported = true;
                Plugin.Logger.LogInfo("boss window: final stage transition started");
            }
            catch (Exception error)
            {
                finalRequested = false;
                Plugin.Logger.LogWarning($"boss window: final stage transition failed: {error.Message}");
                return;
            }
        }

        // Real time, not the stage clock: the transition above freezes the clock
        // this class reads, so a retry paced on it would never come around.
        if (Time.realtimeSinceStartup < nextFinalLoadAttempt)
        {
            return;
        }

        nextFinalLoadAttempt = Time.realtimeSinceStartup + 1f;

        try
        {
            MapController.LoadFinalStage();
            Plugin.Logger.LogInfo("boss window: final boss stage loaded");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"boss window: final boss stage load failed: {error}");
        }
    }

    /// <summary>
    /// The final boss stage (W2). The arena owns the fight; this only watches
    /// for the kill. The run must not finish there: the run-ending portal is
    /// refused (<see cref="BlocksFinalPortal"/>) and the arena itself becomes
    /// the endless phase. After a short grace so the cinematic and the dissolve
    /// play, the game's own final swarm is started on the arena summoner
    /// (<c>SummonerController.StartFinalSwarm</c>, the call the stage timeline
    /// makes at its end) and never stopped: it keeps spawning ghosts and
    /// escalates on <c>MyTime.finalSwarmTimer</c> (Ghost → Greater Ghost → Ghost
    /// Purple → Ghost Red, with a growing target count), which is the vanilla
    /// post-final-boss survival. Nothing is reloaded, so the run's build and
    /// level stay.
    /// </summary>
    private static void TickFinalFight()
    {
        if (endlessStarted)
        {
            return;
        }

        if (endlessAt <= 0f)
        {
            if (!FinalBossDown())
            {
                return;
            }

            if (!Plugin.AutoNextFloor.Value)
            {
                if (!loggedEndless)
                {
                    loggedEndless = true;
                    Plugin.Logger.LogInfo(
                        "boss window: final boss down, AutoNextFloor is off -> the native final portal ends the run");
                }

                return;
            }

            endlessAt = MyTime.time + EndlessGraceSeconds;
            HideCountdown();
            if (!loggedFinal)
            {
                loggedFinal = true;
                Plugin.Logger.LogInfo(
                    $"boss window: final boss down, the endless ghost waves start in {EndlessGraceSeconds:0} s " +
                    "(the arena stays, no floor reload)");
            }

            return;
        }

        if (MyTime.time < endlessAt)
        {
            return;
        }

        endlessAt = 0f;
        StartEndlessSwarm();
    }

    /// <summary>
    /// Starts the arena's endless phase: the game's own final swarm
    /// (<c>StartFinalSwarm</c>) on the final stage's summoner. The swarm timer is
    /// reset first when no swarm is running, so the escalation starts at the
    /// base ghosts and climbs the native tiers instead of inheriting a timer
    /// from an earlier floor. A missing summoner retries on the next tick rather
    /// than latching an arena with no pressure.
    /// </summary>
    private static void StartEndlessSwarm()
    {
        if (StartFinalSwarm == null)
        {
            Plugin.Logger.LogWarning(
                "boss window: SummonerController.StartFinalSwarm not found, the endless ghost wave cannot start");
            return;
        }

        try
        {
            var manager = EnemyManager.Instance;
            if (manager == null)
            {
                endlessAt = MyTime.time + 1f;
                Plugin.Logger.LogWarning(
                    "boss window: no EnemyManager on the final boss stage, the endless ghost wave retries");
                return;
            }

            // `EnemyManager.FixedUpdate` only ticks the summoner while this flag
            // is on; the arena's own goons do not go through it, so the stage may
            // ship it off. The endless swarm is the summoner, so turn it back on.
            if (!manager.enabledWaves)
            {
                manager.enabledWaves = true;
                Plugin.Logger.LogInfo(
                    "boss window: wave spawning was off in the arena, enabled for the endless swarm");
            }

            var summoner = manager.summonerController;
            if (summoner == null)
            {
                endlessAt = MyTime.time + 1f;
                Plugin.Logger.LogWarning(
                    "boss window: no summoner on the final boss stage, the endless ghost wave retries");
                return;
            }

            if (!summoner.IsFinalSwarm())
            {
                MyTime.finalSwarmTimer = 0f;
            }

            StartGhostWave(summoner);
            endlessStarted = true;
            Plugin.Logger.LogInfo(
                "boss window: the arena is now the endless phase — the native final swarm keeps spawning " +
                "ghosts, harder with every tier (Ghost -> Greater Ghost -> Ghost Purple -> Ghost Red)");
        }
        catch (Exception error)
        {
            endlessAt = MyTime.time + 1f;
            Plugin.Logger.LogWarning($"boss window: the endless ghost wave failed to start: {error.Message}");
        }
    }

    /// <summary>The native final-boss signal, with our own death hook as the
    /// fallback (<c>GameManager.IsFinalBossDead</c> reads the arena summoner).</summary>
    private static bool FinalBossDown()
    {
        if (finalKilled)
        {
            return true;
        }

        try
        {
            var manager = GameManager.Instance;
            return manager != null && manager.IsFinalBossDead();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The gate itself: the floor's beat, on the stage clock. It is the
    /// design's fixed floor length, not the stage's own (shorter) timeline, so
    /// every floor runs 0:00 → 10:00. An altar boss summoned before the beat
    /// consumes it (R10); a dev-key boss killed before the beat does not, so the
    /// beat still summons the floor boss (or honours one still alive) and runs
    /// its own deadline.</summary>
    private static void TryOpenWindow(MyPlayer player, SummonerController summoner)
    {
        var gate = GateSeconds(summoner);
        if (MyTime.stageTimer < gate)
        {
            return;
        }

        // R10: an altar boss summoned before the beat consumes this floor's beat.
        // No second boss, no ghost wave. A boss still alive at the gate still has
        // the floor's deadline (11:00); the countdown only runs while one is live.
        if (earlyAltarBoss)
        {
            windowRan = true;
            deadline = gate + Plugin.BossWindowSeconds.Value;

            if (!AnyLive())
            {
                windowOpen = false;
                HideCountdown();
                Plugin.Logger.LogInfo(
                    $"boss window: the early altar boss cleared floor {lastStage + 1}, the beat summons nothing; " +
                    $"the portal is taken at {deadline:0} s on the stage clock");
                return;
            }

            windowOpen = true;
            nextSpawnAttempt = 0f;
            ShowCountdown();
            Plugin.Logger.LogInfo(
                $"boss window: the early altar boss is still alive at {MyTime.stageTimer:0.0} s, " +
                $"the deadline is {deadline:0} s on the stage clock, no second boss, no ghost wave");
            return;
        }

        if (bossKilled)
        {
            // R12 (operator, 2026-11-01): an early kill counts. The floor keeps
            // its full length, no second boss is summoned and the last minute is
            // just the ghost wave — killing the boss early is the reward for the
            // pressure the beat would otherwise apply.
            windowOpen = false;
            windowRan = true;
            deadline = gate + Plugin.BossWindowSeconds.Value;
            StartGhostWave(summoner);
            HideCountdown();
            Plugin.Logger.LogInfo(
                $"boss window: floor {lastStage + 1} — the boss died at {bossKilledAt:0} s, so the beat " +
                $"summons nothing and the ghost wave is the only pressure; the portal is taken at " +
                $"{deadline:0} s on the stage clock");
            return;
        }

        windowOpen = true;
        windowRan = true;
        bossKilled = false;
        bossKilledAt = 0f;
        // Absolute on the stage clock, so a late tick cannot stretch the race.
        deadline = gate + Plugin.BossWindowSeconds.Value;

        if (AnyLive())
        {
            Plugin.Logger.LogInfo("boss window: forced boss honoured, no second spawn");
        }
        else
        {
            SpawnFloorBoss(player, summoner);
        }

        StartGhostWave(summoner);
        ShowCountdown();

        Plugin.Logger.LogInfo(
            $"boss window: floor {lastStage + 1} at {MyTime.stageTimer:0.0} s, " +
            $"the boss must be down by {deadline:0} s on the stage clock, one ghost wave");
    }

    private static void SpawnFloorBoss(MyPlayer player, SummonerController summoner)
    {
        // The boss spawns in front of the player (the floor is a race, and the
        // altar can be anywhere on the map), but the altar must know the boss it
        // "summoned": its boss list is what opens the floor portal when the boss
        // is released to the pool. Both halves are the game's own data, only the
        // spawn position is ours.
        var spawned = SpawnNear(player, summoner);
        if (spawned == null || spawned.Count == 0)
        {
            return;
        }

        var altar = FindAltar(player);
        if (altar == null)
        {
            Plugin.Logger.LogWarning(
                "boss window: no boss altar in the scene, the portal will not open (next floor loaded on the kill)");
            return;
        }

        try
        {
            altar.bossEnemies = spawned;
            Plugin.Logger.LogInfo(
                $"boss window: {spawned.Count} boss(es) registered with the altar '{altar.name}'");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"boss window: altar registration failed: {error.Message}");
        }
    }

    private static Il2CppSystem.Collections.Generic.List<Enemy> SpawnNear(
        MyPlayer player,
        SummonerController summoner)
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
            if (Physics.Raycast(position + Vector3.up * 60f, Vector3.down, out var ground, 200f))
            {
                position = ground.point;
            }

            var spawned = summoner.SpawnStageBoss(position);
            Plugin.Logger.LogInfo(
                $"boss window: floor boss spawned in front of the player ({spawned?.Count ?? 0} boss(es)) at {position}");
            return spawned;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"boss window: floor boss spawn failed: {error}");
            return null;
        }
    }

    private static InteractableBossSpawner FindAltar(MyPlayer player)
    {
        try
        {
            var altars = UnityEngine.Object.FindObjectsOfType<InteractableBossSpawner>();
            InteractableBossSpawner nearest = null;
            var best = float.MaxValue;

            for (var index = 0; index < altars.Length; index++)
            {
                var altar = altars[index];
                if (altar == null || !altar.gameObject.activeInHierarchy)
                {
                    continue;
                }

                var distance = (altar.transform.position - player.transform.position).sqrMagnitude;
                if (distance >= best)
                {
                    continue;
                }

                best = distance;
                nearest = altar;
            }

            return nearest;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"boss window: altar lookup failed: {error.Message}");
            return null;
        }
    }

    /// <summary>The one ghost wave is the game's own final swarm, the same call
    /// the timeline makes at the stage time. It is idempotent per summoner, so a
    /// second window in a later floor starts its own wave.</summary>
    private static void StartGhostWave(SummonerController summoner)
    {
        if (StartFinalSwarm == null)
        {
            if (!loggedSwarm)
            {
                loggedSwarm = true;
                Plugin.Logger.LogWarning(
                    "boss window: SummonerController.StartFinalSwarm not found, the game's own timeline starts the swarm");
            }

            return;
        }

        try
        {
            // The guard in front of StartFinalSwarm refuses every call that is
            // not this one (R1: the floor's one ghost wave is ours).
            CallingGhostWave = true;
            StartFinalSwarm.Invoke(summoner, null);
            if (!loggedSwarm)
            {
                loggedSwarm = true;
                Plugin.Logger.LogInfo("boss window: one ghost wave started (final swarm)");
            }
        }
        catch (Exception error)
        {
            if (!loggedSwarm)
            {
                loggedSwarm = true;
                Plugin.Logger.LogWarning($"boss window: ghost wave failed: {error.Message}");
            }
        }
        finally
        {
            CallingGhostWave = false;
        }
    }

    private static void UpdateWindow(MyPlayer player)
    {
        var left = deadline - MyTime.stageTimer;

        if (bossKilled)
        {
            // R10: the kill does not end the floor. The window stays open with
            // the countdown hidden and the portal is taken at the deadline, so
            // the player keeps the rest of the clock.
            HideCountdown();
            return;
        }

        if (!AnyLive())
        {
            // The beat's spawn failed, or the boss left the pool without dying.
            // Retry while the clock runs: a floor must never be left with no
            // boss, no waves (the beat suppresses them) and no portal.
            if (left > 0f && MyTime.time >= nextSpawnAttempt)
            {
                nextSpawnAttempt = MyTime.time + SpawnRetrySeconds;
                var manager = EnemyManager.Instance;
                var summoner = manager == null ? null : manager.summonerController;
                if (summoner != null)
                {
                    Plugin.Logger.LogWarning("boss window: no live floor boss, summoning one again");
                    SpawnFloorBoss(player, summoner);
                }
            }
        }
        else
        {
            nextSpawnAttempt = 0f;
        }

        if (left <= 0f)
        {
            windowOpen = false;
            HideCountdown();

            // R10: only a living boss ends the run at the deadline. A boss that
            // left the pool without dying cannot be lost to a spawn fault, so
            // the floor is cleared instead.
            if (AnyLive())
            {
                KillPlayer(player);
                return;
            }

            Plugin.Logger.LogWarning("boss window: no live floor boss at the deadline, the floor is cleared");
            bossKilled = true;
            bossKilledAt = MyTime.time;
            return;
        }

        SetCountdown(left);
    }

    /// <summary>
    /// The floor's portal is opened by the boss spawner when the boss is
    /// released to the pool (i.e. after the death dissolve). R10: it is taken at
    /// the deadline (<see cref="FloorEnded"/>), not on the kill, so a boss killed
    /// early leaves the rest of the floor to play; the player can take the
    /// portal manually before. If no portal ever opens (a stage without the altar
    /// object), the same two calls the portal coroutine makes are used, teleport
    /// effect first, so the run still advances.
    /// </summary>
    private static void AdvanceFloor(MyPlayer player)
    {
        if (!Plugin.AutoNextFloor.Value || portalTaken || MyTime.time < nextPortalPoll)
        {
            return;
        }

        nextPortalPoll = MyTime.time + PortalPollSeconds;

        try
        {
            var portal = UnityEngine.Object.FindObjectOfType<InteractablePortal>();
            if (portal != null && portal.gameObject.activeInHierarchy && portal.Interact())
            {
                portalTaken = true;
                Plugin.Logger.LogInfo("boss window: floor cleared, portal taken -> next floor");
                return;
            }

            if (bossKilledAt <= 0f || MyTime.time - bossKilledAt < PortalFallbackSeconds)
            {
                return;
            }

            // No portal took the floor: a stage without the altar, or a
            // dev-spawned boss the altar never registered. The game's own
            // DoLoadNextStage coroutine sets the cutscene flag, plays the
            // teleport and then loads; with no portal to host that coroutine,
            // the same sequence is completed here, in this frame. A load
            // deferred to a later MyPlayer.Update never ran in the live dev
            // run (the log stopped at "teleporting to the next floor"), which
            // left the player with the effect and no floor.
            if (!fallbackTeleported)
            {
                fallbackTeleported = true;
                var manager = GameManager.Instance;
                if (manager != null)
                {
                    manager.cutscene = true;
                }

                player.TeleportPlayerNextStage();
            }

            MapController.LoadNextStage();
            portalTaken = true;
            Plugin.Logger.LogInfo("boss window: no portal, next floor loaded");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"boss window: advancing the floor failed: {error.Message}");
        }
    }

    private static void KillPlayer(MyPlayer player)
    {
        try
        {
            var inventory = player.inventory;
            var health = inventory == null ? null : inventory.playerHealth;
            if (health == null)
            {
                Plugin.Logger.LogWarning("boss window: no PlayerHealth, the floor deadline cannot end the run");
                return;
            }

            if (health.IsDead())
            {
                return;
            }

            var bypass = Dev.TestMode.Invincible;
            Dev.TestMode.BypassInvincibility = bypass;
            try
            {
                health.KillPlayer();
            }
            finally
            {
                Dev.TestMode.BypassInvincibility = false;
            }

            Plugin.Logger.LogInfo(
                $"boss window: the boss is still alive at {deadline:0} s on the stage clock " +
                $"({Plugin.BossWindowSeconds.Value:0} s after the beat) -> the run ends" +
                (bypass ? " (F1 invincibility does not hold the deadline)" : string.Empty));
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"boss window: ending the run failed: {error}");
        }
    }

    /// <summary>The floor's beat on the stage clock: the design's
    /// <see cref="DefaultFloorSeconds"/> (10 minutes) on every floor, or the
    /// stage's own timeline time when <see cref="Plugin.FloorSeconds"/> is 0.
    /// Megabonk ships 600 / 540 / 480 s, so the fixed value is what makes the
    /// three floors read the same.</summary>
    private static float GateSeconds(SummonerController summoner)
    {
        var configured = Plugin.FloorSeconds.Value;
        if (configured > 1f)
        {
            return configured;
        }

        return NativeStageTime(summoner);
    }

    /// <summary>The same beat without a summoner in hand (the boss spawn hook);
    /// falls back to the configured floor length when the summoner is missing.</summary>
    internal static float GateSeconds()
    {
        try
        {
            var manager = EnemyManager.Instance;
            var summoner = manager == null ? null : manager.summonerController;
            return summoner == null ? FloorLength() : GateSeconds(summoner);
        }
        catch (Exception)
        {
            return FloorLength();
        }
    }

    /// <summary>R10: the floor's end on the stage clock (beat + window, 11:00 by
    /// default). A dead boss does not end the floor early: the portal is taken
    /// here so a boss killed before the deadline leaves the rest of the floor to
    /// play.</summary>
    private static bool FloorEnded(SummonerController summoner)
    {
        return MyTime.stageTimer >= GateSeconds(summoner) + Plugin.BossWindowSeconds.Value;
    }

    /// <summary>The stage's own timeline length (<c>StageTimeline.GetStageTime</c>
    /// through <c>SummonerController.GetMaxTimeLength</c>). Used to refuse the
    /// native final swarm and as the fallback beat when the floor length is 0.</summary>
    private static float NativeStageTime(SummonerController summoner)
    {
        try
        {
            var value = summoner.GetMaxTimeLength();
            return value > 1f ? value : DefaultFloorSeconds;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"boss window: stage time unavailable ({error.Message}), using {DefaultFloorSeconds:0} s");
            return DefaultFloorSeconds;
        }
    }

    /// <summary>The switch's floor length (for the load-time report).</summary>
    private static float FloorLength()
    {
        var configured = Plugin.FloorSeconds.Value;
        return configured > 1f ? configured : DefaultFloorSeconds;
    }

    private static bool Remove(Enemy enemy)
    {
        var id = enemy.GetInstanceID();
        for (var index = Bosses.Count - 1; index >= 0; index--)
        {
            var boss = Bosses[index];
            if (boss == null || boss.GetInstanceID() == id)
            {
                Bosses.RemoveAt(index);
                if (boss != null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool AnyLive()
    {
        for (var index = Bosses.Count - 1; index >= 0; index--)
        {
            var boss = Bosses[index];
            if (boss == null || boss.gameObject == null || !boss.gameObject.activeInHierarchy)
            {
                Bosses.RemoveAt(index);
            }
        }

        return Bosses.Count > 0;
    }

    private static string Name(Enemy enemy)
    {
        try
        {
            var data = enemy.enemyData;
            return data == null ? enemy.name : BossRoster.DisplayName(data) ?? data.name;
        }
        catch (Exception)
        {
            return "?";
        }
    }

    /// <summary>Suppression is logged once per run so the log states why no wave
    /// followed the ghost wave.</summary>
    internal static void NoteSuppression()
    {
        if (loggedSuppression)
        {
            return;
        }

        loggedSuppression = true;
        Plugin.Logger.LogInfo("boss window: timeline waves suppressed for the rest of the floor");
    }

    private static void SetCountdown(float seconds)
    {
        if (countdown == null || countdownRoot == null)
        {
            BuildCountdown();
        }

        if (countdown == null || countdownRoot == null)
        {
            return;
        }

        var whole = Mathf.Max(0, Mathf.CeilToInt(seconds));
        if (whole != lastShownSecond)
        {
            lastShownSecond = whole;
            countdown.text = $"BOSS 0:{whole:00}";
        }

        if (!countdownRoot.activeSelf)
        {
            countdownRoot.SetActive(true);
        }
    }

    private static void ShowCountdown()
    {
        if (countdown == null || countdownRoot == null)
        {
            BuildCountdown();
        }

        if (countdownRoot != null && !countdownRoot.activeSelf)
        {
            countdownRoot.SetActive(true);
        }
    }

    private static void HideCountdown()
    {
        lastShownSecond = -1;
        if (countdownRoot != null)
        {
            countdownRoot.SetActive(false);
        }
    }

    /// <summary>The countdown is a copy of the stage timer's own TMP object, so it
    /// is drawn in the game's font, size, colour and outline without shipping a
    /// font, and the uniform layout offset places it under the timer.</summary>
    private static void BuildCountdown()
    {
        if (uiUnavailable)
        {
            return;
        }

        try
        {
            var template = GameObject.Find("t_Timer_Stage");
            if (template == null)
            {
                uiUnavailable = true;
                if (!loggedAnchor)
                {
                    loggedAnchor = true;
                    Plugin.Logger.LogWarning("boss window: t_Timer_Stage not found, the countdown has no anchor");
                }

                return;
            }

            countdownRoot = UnityEngine.Object.Instantiate(template, template.transform.parent);
            countdownRoot.name = "WraeclastBossWindow";

            var rect = countdownRoot.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.anchoredPosition = rect.anchoredPosition + new Vector2(0f, -96f);
                rect.sizeDelta = new Vector2(360f, 48f);
            }

            countdown = countdownRoot.GetComponent<TextMeshProUGUI>();
            if (countdown != null)
            {
                countdown.raycastTarget = false;
                countdown.alignment = TextAlignmentOptions.Center;
                countdown.text = "BOSS 1:00";
            }

            Plugin.Logger.LogInfo("boss window: countdown drawn under the stage timer");
        }
        catch (Exception error)
        {
            countdown = null;
            countdownRoot = null;
            Plugin.Logger.LogWarning($"boss window: countdown UI failed: {error.Message}");
        }
    }
}
