using System;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Game.Spawning.New;
using Assets.Scripts.Managers;
using HarmonyLib;
using Wraeclast.Run;

namespace Wraeclast.Patches;

/// <summary>
/// W1 hooks for <see cref="BossWindow"/>. The boss spawn is observed at
/// <c>EnemyManager.SpawnBoss</c>, the single entry point every boss-tier spawn
/// goes through (the stage altar, the final fight and the dev keys), so the
/// window knows a boss exists no matter who summoned it.
///
/// The suppression hooks are prefixes that return false: no timeline event
/// after the boss (<c>SummonerController.StartEvent</c> is where a swarm or
/// miniboss wave is started), and no second boss from the altar once the window
/// owns the floor.
/// </summary>
[HarmonyPatch]
internal static class BossWindowPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(EnemyManager), nameof(EnemyManager.SpawnBoss))]
    private static void AfterSpawnBoss(Enemy __result, EEnemyFlag enemyFlag)
    {
        BossWindow.OnBossSpawned(__result, enemyFlag);
    }

    /// <summary>R10: <c>SpawnStageBoss</c> is the altar's own summon call (the
    /// dev keys go straight to <c>EnemyManager.SpawnBoss</c>), so a boss spawned
    /// inside it before the beat is the altar's and consumes the floor's beat.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SummonerController), nameof(SummonerController.SpawnStageBoss))]
    private static void BeforeSpawnStageBoss()
    {
        BossWindow.NoteStageBossSpawning(true);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SummonerController), nameof(SummonerController.SpawnStageBoss))]
    private static void AfterSpawnStageBoss()
    {
        BossWindow.NoteStageBossSpawning(false);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SummonerController), nameof(SummonerController.StartEvent))]
    private static bool BeforeStartEvent()
    {
        if (!BossWindow.BlocksTimelineEvent)
        {
            return true;
        }

        BossWindow.NoteSuppression();
        return false;
    }

    /// <summary>R12: a floor boss killed before the beat must not touch the
    /// native stage flow. <c>OnBossDied</c> stops the summoners and rewrites the
    /// stage's final-swarm clock, which pulled the final wave (and its 10 s
    /// warning) three minutes early.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SummonerController), "OnBossDied")]
    private static bool BeforeOnBossDied()
    {
        if (!BossWindow.BlocksBossDiedNotice)
        {
            return true;
        }

        BossWindow.NoteBossDiedSkipped();
        return false;
    }

    /// <summary>R1/R12: the floor's one ghost wave is ours, so the timeline's
    /// own final swarm is refused on floors 1-2 (the final arena and floor 3
    /// keep the game's flow).</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SummonerController), "StartFinalSwarm")]
    private static bool BeforeStartFinalSwarm()
    {
        return !BossWindow.BlocksFinalSwarm;
    }

    /// <summary>The final arena keeps the previous stage's timeline; its boss and
    /// the endless final swarm own the spawning there, so the floor timeline is
    /// skipped (the summoner's own summoners still tick).</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SummonerController), "TickTimeline")]
    private static bool BeforeTickTimeline()
    {
        return !BossWindow.BlocksArenaTimeline;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(InteractableBossSpawner), nameof(InteractableBossSpawner.Interact))]
    private static bool BeforeAltarInteract(ref bool __result)
    {
        if (!BossWindow.BlocksAltar)
        {
            return true;
        }

        __result = false;
        return false;
    }

    /// <summary>W2: the final portal runs <c>DoFinishGame</c> (the run's stats
    /// screen). The endless loop owns the final boss stage instead, so the
    /// portal answers false and the kill reloads stage 1.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(InteractablePortalFinal), nameof(InteractablePortalFinal.Interact))]
    private static bool BeforeFinalPortalInteract(ref bool __result)
    {
        if (!BossWindow.BlocksFinalPortal)
        {
            return true;
        }

        __result = false;
        return false;
    }
}
