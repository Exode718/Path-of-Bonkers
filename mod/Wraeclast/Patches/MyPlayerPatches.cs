using System;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Utility;
using HarmonyLib;
using Wraeclast.Data;
using Wraeclast.Run;
using Wraeclast.Skills;

namespace Wraeclast.Patches;

/// <summary>
/// Frame driver for our skills. <c>MyPlayer.Update</c> only runs on the live
/// player (not on previews), which is exactly the scope the controller wants.
/// </summary>
[HarmonyPatch(typeof(MyPlayer))]
internal static class MyPlayerPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(MyPlayer.Update))]
    private static void AfterUpdate(MyPlayer __instance)
    {
        // MyTime.Pause is the game's pause (it flips Physics to Script); nothing
        // of ours may deal damage, spawn or dissolve while it is set.
        if (MyTime.paused)
        {
            return;
        }

        PoeSkills.Tick(__instance);
        EnemyStatus.Tick();
        PropSwap.Tick();
        ChestSwap.Tick();
        ShrineSwap.Tick();
        MerchantSwap.Tick();
        ElementSwap.Tick();
        PoeEnemies.TickUnanimated();
        PoeEnemies.TickDissolves();
        BossWindow.Tick(__instance);
        Dev.BossRun.Tick(__instance);
        Dev.RunTest.Tick(__instance);
        Dev.Gallery.Tick(__instance);
        Dev.PerfProbe.Tick(__instance);
        Dev.PropProbe.Tick(__instance);
        SkillRowPatches.Tick();
    }
}
