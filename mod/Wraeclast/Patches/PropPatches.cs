using Assets.Scripts.Inventory__Items__Pickups.Chests;
using Assets.Scripts.Inventory__Items__Pickups.Interactables;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using HarmonyLib;
using Wraeclast.Data;
using Wraeclast.Skills;

namespace Wraeclast.Patches;

/// <summary>
/// A2: the stage's random scenery wears the client's own prop meshes.
///
/// **The row path is the one that runs (A2c).** Megabonk builds its scenery
/// from <c>StageData.randomMapObjects</c>, but not through
/// <c>RandomObjectPlacer.Generate</c>: the <c>MapGenerationController</c>'s
/// <c>GenerateMap</c> coroutine reads the placer's own fields
/// (<c>chargeShrineSpawns</c>, <c>greedShrineSpawns</c>, <c>randomObjects</c>)
/// and calls the private <c>RandomObjectSpawner(RandomMapObject, float)</c>
/// once per row (ISIL of <c>GenerateMap</c>: calls at 859, 864, 880, 989).
/// A postfix on that method therefore runs exactly when a row has been
/// instantiated, before the first frame that shows it — no pop, and nothing
/// about the roll, the placement, the pivot or the collider is touched.
/// </summary>
[HarmonyPatch(typeof(RandomObjectPlacer), "RandomObjectSpawner")]
internal static class PropSpawnerPatches
{
    [HarmonyPostfix]
    private static void AfterRow(RandomObjectPlacer __instance)
    {
        PropSwap.Apply(__instance);
    }
}

/// <summary>
/// Kept for a build (or another caller) that rolls the whole list through the
/// public wrapper. On a floor it never fires — which is why the per-row postfix
/// above exists — and it is idempotent with it, since an object that already
/// carries our child mesh is skipped.
/// </summary>
[HarmonyPatch(typeof(RandomObjectPlacer), nameof(RandomObjectPlacer.Generate))]
internal static class PropPlacerPatches
{
    [HarmonyPostfix]
    private static void AfterGenerate(RandomObjectPlacer __instance)
    {
        PropSwap.Apply(__instance);
    }
}

/// <summary>
/// A3b: the shrine rows are spawned from <c>GenerateInteractables</c> through
/// the same <c>RandomObjectSpawner</c> (the two shrine rows call it directly,
/// then the random rows follow). The postfix runs once every shrine of the
/// floor exists, so the client totem is mounted on the frame the shrine is
/// placed; <c>ShrineSwap.Tick</c> stays the once-a-second net.
/// </summary>
[HarmonyPatch(typeof(RandomObjectPlacer), nameof(RandomObjectPlacer.GenerateInteractables))]
internal static class ShrineSpawnPatches
{
    [HarmonyPostfix]
    private static void AfterInteractables()
    {
        ShrineSwap.ApplyAll();
        ElementSwap.ApplyAll();
    }
}

/// <summary>
/// A3c: the moai (`ShrineMaoi` + `InteractableShrineMoai`) is placed by
/// <c>SpawnInteractables.SpawnShrines</c>, which builds the stage's shrine set
/// in one pass. The postfix mounts the client statue on the frame the shrine
/// exists; the once-a-second sweep in <c>ShrineSwap</c> remains the net for a
/// shrine a pool or a reload hands over later.
/// </summary>
[HarmonyPatch(typeof(SpawnInteractables), nameof(SpawnInteractables.SpawnShrines))]
internal static class ShrineSpawnSetPatches
{
    [HarmonyPostfix]
    private static void AfterSpawnShrines()
    {
        ShrineSwap.ApplyAll();
        ElementSwap.ApplyAll();
    }
}

/// <summary>
/// A5.2: the microwave comes from the placer's own `Microwave` row, and its body
/// is a `SkinnedMeshRenderer` — which answers its **bind pose** box until the
/// first draw (the A5.1 merchant lesson). The `Start` postfix is therefore a
/// nudge, not the mount: the pass only fits a body it can measure, and the
/// once-a-second sweep mounts it on the frame the drawn box exists.
/// </summary>
[HarmonyPatch(typeof(InteractableMicrowave), "Start")]
internal static class MicrowaveStartPatches
{
    [HarmonyPostfix]
    private static void AfterStart(InteractableMicrowave __instance)
    {
        ElementSwap.ApplyMicrowave(__instance);
    }
}

/// <summary>
/// A5.1: the shady-guy merchant's body is replaced on his own `Start` (the
/// interop confirms `Start` is present, unlike the `OpenChest` trap of 0.9.111),
/// so the client NPC exists the frame the merchant does; `MerchantSwap.Tick`'s
/// once-a-second sweep is the net for a merchant the shop chain instantiates
/// later or inactive.
/// </summary>
[HarmonyPatch(typeof(InteractableShadyGuy), "Start")]
internal static class MerchantStartPatches
{
    [HarmonyPostfix]
    private static void AfterStart(InteractableShadyGuy __instance)
    {
        MerchantSwap.Apply(__instance);
    }
}

/// <summary>
/// A3b: the charge shrine's native rune stone is dormant until its zone is
/// activated, so the totem is placed at the root from the start and this hook
/// hides the body the frame it appears. `Start` exists on the interop stub
/// (`Awake`, `Start`, `Update`, …), unlike the `OpenChest` trap of 0.9.111.
/// </summary>
[HarmonyPatch(typeof(ChargeShrine), "Start")]
internal static class ChargeShrineStartPatches
{
    [HarmonyPostfix]
    private static void AfterStart(ChargeShrine __instance)
    {
        ShrineSwap.ApplyCharge(__instance);
    }
}

/// <summary>
/// A3b: Megabonk's greed shrine turns its red `fx`/`fxLoop` **on** in
/// <c>Interact</c>. The operator wants the inverse (the red marks the shrine
/// before it is used), so the pass turns them on at spawn and this postfix turns
/// them off once the shrine is used — after the native method has enabled them.
/// </summary>
[HarmonyPatch(typeof(InteractableShrineGreed), nameof(InteractableShrineGreed.Interact))]
internal static class GreedShrineInteractPatches
{
    [HarmonyPostfix]
    private static void AfterInteract(InteractableShrineGreed __instance)
    {
        ShrineSwap.AfterGreedInteract(__instance);
    }
}

/// <summary>
/// A3a: the map's chests. <c>SpawnInteractables.SpawnChests</c> instantiates
/// Megabonk's <c>chest</c> and <c>chestFree</c> prefabs; the postfix runs after
/// the whole set exists, so the pass sees every chest with its final position
/// and never touches the inactive prefab template.
/// </summary>
[HarmonyPatch(typeof(SpawnInteractables), nameof(SpawnInteractables.SpawnChests))]
internal static class ChestSpawnPatches
{
    [HarmonyPostfix]
    private static void AfterSpawnChests()
    {
        ChestSwap.ApplyAll();
    }
}

/// <summary>A3a's safety net: a chest the game activates after
/// <c>SpawnChests</c> returned still gets its model on its own <c>Start</c>.
/// The pass is idempotent, so a chest the spawn pass already swapped is
/// skipped.</summary>
[HarmonyPatch(typeof(InteractableChest), "Start")]
internal static class ChestStartPatches
{
    [HarmonyPostfix]
    private static void AfterStart(InteractableChest __instance)
    {
        ChestSwap.Apply(__instance);
    }
}

/// <summary>
/// A3a: the chest-opening shot. <c>ChestOpening.SetRender</c> picks one of the
/// host's own chest meshes and its material for the animation; the postfix
/// swaps in the client chest, scaled into the same box, so the animation shows
/// the chest the player actually walked up to.
/// </summary>
[HarmonyPatch(typeof(ChestOpening))]
internal static class ChestOpeningPatches
{
    [HarmonyPostfix]
    [HarmonyPatch("SetRender")]
    private static void AfterSetRender(ChestOpening __instance, EChest chest)
    {
        ChestSwap.ApplyOpening(__instance, chest);
    }

    [HarmonyPostfix]
    [HarmonyPatch("SetChest")]
    private static void AfterSetChest(ChestOpening __instance, EChest chestType)
    {
        ChestSwap.ApplyOpening(__instance, chestType);
    }

    /// <summary>The net: the shot keeps the client chest even if the host
    /// assigns its own mesh after `SetRender` (the animation does that). One
    /// dictionary lookup and one mesh compare per frame.</summary>
    [HarmonyPostfix]
    [HarmonyPatch("Update")]
    private static void AfterUpdate(ChestOpening __instance)
    {
        ChestSwap.EnsureOpening(__instance);
    }
}

/// <summary>
/// A3a: the chest an elite kill leaves on the ground is an <c>OpenChest</c>, not
/// an <c>InteractableChest</c> — the 0.9.105 probe named it, which is why the
/// spawn pass and the sweep both walked past it. It gets the client chest too,
/// gold tinted, on its own <c>Awake</c>.
/// </summary>
[HarmonyPatch(typeof(OpenChest), "Awake")]
internal static class OpenChestPatches
{
    /// <summary>
    /// 0.9.111: **only `Awake`.** `OpenChest` has no `Start` (measured on the
    /// interop stub: `Awake`, `OnTriggerStay`, `CanPickup`), and patching a
    /// method that does not exist throws `Patching exception in method null`,
    /// which took the whole class down — so `Awake` was never patched either and
    /// the dropped chest kept its host mesh through 0.9.110. The instance is
    /// created inactive, so the per-second sweep finishes anything `Awake` could
    /// not measure yet.
    /// </summary>
    [HarmonyPostfix]
    private static void AfterAwake(OpenChest __instance)
    {
        ChestSwap.ApplyOpenChest(__instance);
    }
}

/// <summary>
/// A2r: a prop that fades near the camera carries <c>CameraFadeObject</c>, which
/// swaps the renderer to its own fade material and then puts back the instance
/// it cached at <c>Start</c> — Megabonk's atlas. Both of its entries are
/// postfixed so the material the renderer carries right now is repainted after
/// every swap (one write per material change, not per frame).
/// </summary>
[HarmonyPatch(typeof(CameraFadeObject))]
internal static class CameraFadePatches
{
    [HarmonyPostfix]
    [HarmonyPatch("Update")]
    private static void AfterUpdate(CameraFadeObject __instance)
    {
        PropSwap.RepaintFade(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch("StopFade")]
    private static void AfterStopFade(CameraFadeObject __instance)
    {
        PropSwap.RepaintFade(__instance);
    }
}

/// <summary>
/// 0.9.115: the run reset drops the material caches that are keyed by Unity
/// **InstanceIDs**. Unity reuses those IDs after a scene reload, so a cache
/// entry built for the previous run can be handed to the new run's object — the
/// black chests the operator saw after a retry, where the clone still pointed at
/// a destroyed texture. <c>ItemInventory.Cleanup</c> is the run reset the relic
/// and skill-mod layers already hook, so it is the one place this belongs.
/// </summary>
[HarmonyPatch(typeof(ItemInventory), nameof(ItemInventory.Cleanup))]
internal static class RunMaterialResetPatches
{
    [HarmonyPostfix]
    private static void AfterCleanup()
    {
        PoeCharacters.ClearMaterials();
        ChestSwap.ClearCaches();
        ShrineSwap.ClearCaches();
        EnemyStatus.Reset();
        EnemyCap.Reset();
        SkillAudio.Reset();
    }
}
