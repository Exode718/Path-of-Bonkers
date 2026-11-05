using System.Collections.Generic;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations;
using HarmonyLib;
using Wraeclast.Data;

namespace Wraeclast.Patches;

/// <summary>
/// R4: the hooks the relic engine needs from the game's own item pipeline.
///
/// - <c>ItemFactory.CreateItem</c> is where an <c>EItem</c> becomes its effect
///   class. For an engine-owned relic the answer is <c>ItemNoImplementation</c>
///   (the same class Megabonk's own inert items use), so the donor's modifiers
///   never apply and <see cref="RelicEngine"/> owns the effect.
/// - <c>ItemInventory.AddItem</c> (both overloads) is the pickup: the engine
///   writes the new stacks into the game's stat pipeline there.
/// - <c>ItemInventory.Cleanup</c> is the run reset: the stack bookkeeping and
///   the stand-still ramp restart with the run.
///
/// The kill trigger lives in <c>EnemyPatches</c>, next to the creature hooks.
/// </summary>
[HarmonyPatch]
internal static class RelicPatches
{
    /// <summary>The slots the host casts back to their own class. Their donor
    /// stays; the effect is neutralised where it fires.</summary>
    private static readonly HashSet<EItem> CastByHost = new HashSet<EItem>
    {
        EItem.Key,           // (ItemKey) on the crypt/chest path
        EItem.GoldenGlove,   // (ItemGoldenGlove) in MoneyUtility.OnEnemyDied
        EItem.EchoShard,     // (ItemEchoShard) in PickupManager.OnEnemyDied
    };

    private static readonly HashSet<EItem> CastLogged = new HashSet<EItem>();

    /// <summary>The interop stub names the second argument <c>inventory</c>;
    /// Harmony resolves patch parameters by name, so the name is the stub's.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ItemFactory), nameof(ItemFactory.CreateItem))]
    private static bool BeforeCreateItem(EItem eItem, ItemInventory inventory, ref ItemBase __result)
    {
        if (!RelicEngine.OwnsEffect(eItem, inventory))
        {
            return true;
        }

        if (inventory == null)
        {
            // Card-preview builds (ItemData's dummy item) pass no inventory; the
            // donor class fills the numbers the game will not show anyway.
            return true;
        }

        if (CastByHost.Contains(eItem))
        {
            // 0.9.106: **a slot the host downcasts keeps its donor class.** The
            // host does `(ItemKey)item` on its key path,
            // `(ItemGoldenGlove)item` in `MoneyUtility.OnEnemyDied` and
            // `(ItemEchoShard)item` in `PickupManager.OnEnemyDied` for every
            // kill; `ItemNoImplementation` makes all three throw
            // `InvalidCastException`, and the golden-glove / echo-shard ones
            // abort the whole death pipeline (no XP, no gold, an error storm in
            // the log). The donor class stays and its **effect** is neutralised
            // at its own hook instead (see `ItemGoldenGlovePatches` and
            // `ItemEchoShardPatches`).
            if (CastLogged.Add(eItem))
            {
                Plugin.Logger.LogInfo(
                    $"relic engine: {eItem} keeps its donor class (the host casts it back); " +
                    "its own effect is neutralised at the hook, ours still applies");
            }

            return true;
        }

        try
        {
            // The same class Megabonk gives its inert items (Golden Ring).
            __result = new ItemNoImplementation(inventory);
            return false;
        }
        catch (System.Exception error)
        {
            Plugin.Logger.LogWarning(
                $"relic engine: {eItem} has no neutral item class ({error.Message}); the donor effect stays");
            return true;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ItemInventory), nameof(ItemInventory.AddItem), typeof(EItem))]
    private static void AfterAddItem(EItem eItem)
    {
        RelicEngine.OnItemAdded(eItem);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ItemInventory), nameof(ItemInventory.AddItem), typeof(EItem), typeof(int))]
    private static void AfterAddItemAmount(EItem eItem)
    {
        RelicEngine.OnItemAdded(eItem);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ItemInventory), nameof(ItemInventory.Cleanup))]
    private static void AfterCleanup()
    {
        RelicEngine.Reset();
    }
}

/// <summary>
/// 0.9.106: the golden glove's own bonus, neutralised. The slot keeps its donor
/// class (the host downcasts it on every kill, see `RelicPatches.CastByHost`), so
/// the extra gold it would add is answered as zero while our relic owns that
/// slot — the card's own gold comes from `RelicEngine`.
/// </summary>
[HarmonyPatch(typeof(ItemGoldenGlove), nameof(ItemGoldenGlove.GetExtraGold))]
internal static class ItemGoldenGlovePatches
{
    [HarmonyPrefix]
    private static bool BeforeGetExtraGold(ref int __result)
    {
        if (!RelicEngine.OwnsSlot(EItem.GoldenGlove))
        {
            return true;
        }

        __result = 0;
        return false;
    }
}

/// <summary>
/// 0.9.216: the echo shard's own bonus, neutralised. The slot keeps its donor
/// class because <c>PickupManager.OnEnemyDied</c> downcasts it on every kill
/// (see `RelicPatches.CastByHost`), and <c>ItemNoImplementation</c> there threw
/// 3,180 `InvalidCastException`s in one run and aborted that death's pickup
/// pipeline. Its only effect is <c>GetExtraShards</c>, answered as zero while
/// our relic owns the slot.
/// </summary>
[HarmonyPatch(typeof(ItemEchoShard), nameof(ItemEchoShard.GetExtraShards))]
internal static class ItemEchoShardPatches
{
    [HarmonyPrefix]
    private static bool BeforeGetExtraShards(ref int __result)
    {
        if (!RelicEngine.OwnsSlot(EItem.EchoShard))
        {
            return true;
        }

        __result = 0;
        return false;
    }
}
