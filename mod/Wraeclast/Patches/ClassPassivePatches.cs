using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using HarmonyLib;
using Wraeclast.Data;

namespace Wraeclast.Patches;

/// <summary>
/// 2026-11-05: a class passive is per level. Level 1 rides the character's own
/// modifier list (<see cref="ClassStats.Apply"/>); the live increments are
/// applied by <see cref="ClassPassives"/>.
///
/// The original hook was <c>PlayerInventory.AddLevel</c> only, and the 0.9.213
/// log shows it is never called on the normal level-up path. The level is
/// therefore watched from three places: <c>PlayerInventory.AddXp</c>,
/// <c>MyPlayer.OnLevelUp</c> and a per-frame <c>MyPlayer.Update</c> check (the
/// last one also catches a debug menu that sets the level directly). All three
/// are idempotent: <see cref="ClassPassives"/> applies only levels above its
/// last applied one.
/// </summary>
[HarmonyPatch]
internal static class ClassPassivePatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.AddXp))]
    private static void AfterAddXp(PlayerInventory __instance) => ClassPassives.OnLevelUp(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.AddLevel))]
    private static void AfterAddLevel(PlayerInventory __instance) => ClassPassives.OnLevelUp(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MyPlayer), nameof(MyPlayer.OnLevelUp))]
    private static void AfterOnLevelUp(MyPlayer __instance) =>
        ClassPassives.OnLevelUp(__instance == null ? null : __instance.inventory);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MyPlayer), nameof(MyPlayer.Update))]
    private static void AfterUpdate(MyPlayer __instance) =>
        ClassPassives.OnLevelUp(__instance == null ? null : __instance.inventory);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ItemInventory), nameof(ItemInventory.Cleanup))]
    private static void AfterCleanup() => ClassPassives.Reset();
}
