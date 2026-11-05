using System;
using System.Collections.Generic;
using Assets.Scripts._Data;
using Assets.Scripts._Data.ShopItems;
using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Inventory__Items__Pickups.Progression;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using HarmonyLib;
using Wraeclast.Data;
using Wraeclast.Skills;

namespace Wraeclast.Patches;

/// <summary>
/// R2, the weapon lock: a class holds exactly one weapon, its starter, and that
/// starter is a PoE mesh on the hand bones (<c>WeaponModels</c>), not a
/// Megabonk weapon. Three native paths could still hand the player a second
/// weapon, so all three are closed:
///
/// - the level-up draft (<c>InventoryUtility.GetRandomUpgrades</c> merges
///   <c>UnlockUtility.GetAvailableWeapons</c> with the available tomes and draws
///   three cards): the weapon half is emptied, so the draft can only offer
///   tomes;
/// - the weapon inventory (<c>WeaponInventory.AddWeapon</c>): every add is
///   refused, so the native weapon slot stays empty and no chest, shop or
///   forced path can equip anything;
/// - the meta shop (<c>EShopItem.Weapons</c>, the extra weapon-slot purchase):
///   its card is hidden, because a slot nobody can fill is a dead reward.
///
/// The empty slot is not hidden any more: V3 puts the class's one skill in it
/// (see <see cref="SkillRowPatches"/>), so the HUD row states what the class
/// actually does instead of advertising a second weapon. Chests need no hook:
/// <c>ChestOpening.OpenChest</c> only ever takes an <c>ItemData</c>, and this
/// build's <c>ShopItemData</c> carries no weapon entry beyond the slot purchase
/// above.
/// </summary>
[HarmonyPatch]
internal static class WeaponLockPatches
{
    private static readonly HashSet<int> Refused = new HashSet<int>();
    private static bool loggedDraft;

    private static bool Active => Plugin.LockWeapons.Value;

    /// <summary>One load-time line so a run log states the lock is in force.</summary>
    internal static void Report()
    {
        Plugin.Logger.LogInfo(Active
            ? "weapon lock: one weapon per class (starter only); the draft, chests and the shop cannot offer another"
            : "weapon lock off: Megabonk's weapons and weapon slots stay available");
    }

    /// <summary>
    /// The draft pool is built from <c>GetAvailableWeapons()</c> +
    /// <c>GetAvailableTomes()</c>; emptying the weapon half removes every weapon
    /// unlock and every weapon level-up card at the source, without touching the
    /// draw itself.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UnlockUtility), nameof(UnlockUtility.GetAvailableWeapons))]
    private static bool BeforeGetAvailableWeapons(
        ref Il2CppSystem.Collections.Generic.List<WeaponData> __result)
    {
        if (!Active)
        {
            return true;
        }

        __result = new Il2CppSystem.Collections.Generic.List<WeaponData>();
        if (!loggedDraft)
        {
            loggedDraft = true;
            Plugin.Logger.LogInfo("weapon lock: the draft draws tomes only (GetAvailableWeapons -> 0)");
        }

        return false;
    }

    /// <summary>
    /// Defensive: a weapon that reached the draft by another route is dropped
    /// before the picker builds its cards.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(InventoryUtility), nameof(InventoryUtility.GetRandomUpgrades))]
    private static void AfterGetRandomUpgrades(Il2CppSystem.Collections.Generic.List<IUpgradable> __result)
    {
        if (!Active || __result == null)
        {
            return;
        }

        var removed = 0;
        for (var index = __result.Count - 1; index >= 0; index--)
        {
            var entry = __result[index];
            if (entry == null || entry.TryCast<WeaponData>() == null)
            {
                continue;
            }

            __result.RemoveAt(index);
            removed++;
        }

        if (removed > 0)
        {
            Plugin.Logger.LogWarning($"weapon lock: {removed} weapon card(s) removed from the draft");
        }
    }

    /// <summary>
    /// The native weapon inventory stays empty: the class starter is our mesh, so
    /// no Megabonk weapon may be held at all. <c>ChallengeModifierNoWeapons</c>
    /// proves a run with zero weapons is a supported state.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(WeaponInventory), nameof(WeaponInventory.AddWeapon))]
    private static bool BeforeAddWeapon(WeaponData weaponData)
    {
        if (!Active || weaponData == null)
        {
            return true;
        }

        var slot = (int)weaponData.eWeapon;
        if (Refused.Add(slot))
        {
            var asset = "?";
            try
            {
                asset = weaponData.name;
            }
            catch (Exception error)
            {
                Plugin.Logger.LogWarning($"weapon lock: refused weapon name unreadable ({error.Message})");
            }

            Plugin.Logger.LogInfo(
                $"weapon lock: refused native weapon {weaponData.eWeapon} ({asset}); the class holds its starter only");
        }

        return false;
    }

    /// <summary>
    /// The meta shop's "Weapons" card buys extra weapon slots. With the lock
    /// there is no weapon to put in them, so the card is switched off instead of
    /// selling a reward that does nothing.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ShopWindow), nameof(ShopWindow.Start))]
    private static void AfterShopStart(ShopWindow __instance)
    {
        if (!Active || __instance == null)
        {
            return;
        }

        try
        {
            var containers = __instance.shopContainers;
            if (containers == null)
            {
                return;
            }

            var hidden = 0;
            for (var index = 0; index < containers.Count; index++)
            {
                var container = containers[index];
                if (container == null)
                {
                    continue;
                }

                var data = container.data;
                if (data == null || data.eShopItem != EShopItem.Weapons)
                {
                    continue;
                }

                container.gameObject.SetActive(false);
                hidden++;
            }

            if (hidden > 0)
            {
                Plugin.Logger.LogInfo($"weapon lock: {hidden} weapon slot card(s) hidden in the shop");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"weapon lock: hiding the shop weapon card failed: {error.Message}");
        }
    }

}

/// <summary>
/// V3: the weapon row comes back and shows the class's one skill. With R2 there
/// is no native weapon to fill it, so the row is repurposed instead of hidden:
/// the slot's <c>InventoryItemPrefabUI</c> takes the PoE skill icon, no level
/// number, and the skill name as its tooltip — the row then states what the
/// class actually does. Both surfaces are patched (the HUD's
/// <c>InventoryHud.weaponParent</c> and the pause inventory's
/// <c>UpgradeInventoryUI.weaponParent</c>), on <c>Start</c>/<c>Refresh</c> and
/// <c>OnEnable</c>, because the game rebuilds or clears these rows on its own
/// schedule.
/// </summary>
[HarmonyPatch]
internal static class SkillRowPatches
{
    private static bool logged;
    private static bool loggedSlots;

    /// <summary>Rows currently showing the class skill, so the upgrade count
    /// can follow a pick without waiting for the HUD's own refresh.</summary>
    private static readonly List<InventoryItemPrefabUI> Rows = new List<InventoryItemPrefabUI>();
    private static int shownLevel = -1;

    internal static void Tick()
    {
        var level = SkillMods.TotalStacks;
        if (level == shownLevel)
        {
            return;
        }

        shownLevel = level;
        for (var index = Rows.Count - 1; index >= 0; index--)
        {
            var row = Rows[index];
            if (row == null)
            {
                Rows.RemoveAt(index);
                continue;
            }

            if (row.t_level != null)
            {
                row.t_level.text = level > 0 ? level.ToString() : string.Empty;
            }
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(InventoryHud), nameof(InventoryHud.Start))]
    private static void AfterHudStart(InventoryHud __instance)
    {
        Show(__instance == null ? null : __instance.weaponParent);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(InventoryHud), nameof(InventoryHud.Refresh))]
    private static void AfterHudRefresh(InventoryHud __instance)
    {
        Show(__instance == null ? null : __instance.weaponParent);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UpgradeInventoryUI), nameof(UpgradeInventoryUI.OnEnable))]
    private static void AfterInventoryEnable(UpgradeInventoryUI __instance)
    {
        Show(__instance == null ? null : __instance.weaponParent);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UpgradeInventoryUI), nameof(UpgradeInventoryUI.Refresh))]
    private static void AfterInventoryRefresh(UpgradeInventoryUI __instance)
    {
        Show(__instance == null ? null : __instance.weaponParent);
    }

    /// <summary>V10: the death screen's run recap has its own weapon row, filled
    /// from the (empty) native weapon inventory. The class skill takes that row
    /// too, so the recap shows the icon the run actually used; the panel's own
    /// item prefab is the row template when no weapon ever created one.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeathStatsUi), "Start")]
    private static void AfterDeathStatsStart(DeathStatsUi __instance)
    {
        if (__instance == null)
        {
            return;
        }

        Show(__instance.weaponsParent, __instance.inventoryItemPrefab);
    }

    private static void Show(UnityEngine.Transform parent, UnityEngine.GameObject prefab = null)
    {
        if (!Plugin.LockWeapons.Value || parent == null)
        {
            return;
        }

        try
        {
            // The skill comes from the live run; a HUD refreshed before the
            // player exists stays hidden until the next Refresh resolves it.
            var skillId = PoeSkills.ActiveSkillId;
            if (string.IsNullOrEmpty(skillId) || !SkillDatabase.TryGet(skillId, out var skill))
            {
                return;
            }

            var row = KeepFirstRow(parent, out var hidden);
            if (row == null && prefab != null)
            {
                var clone = UnityEngine.Object.Instantiate(prefab, parent);
                if (clone != null)
                {
                    row = clone.GetComponent<InventoryItemPrefabUI>();
                }
            }

            if (row == null)
            {
                if (!logged)
                {
                    logged = true;
                    Plugin.Logger.LogWarning(
                        $"skill row: no InventoryItemPrefabUI under {parent.name}; the row stays empty");
                }

                return;
            }

            parent.gameObject.SetActive(true);
            row.gameObject.SetActive(true);
            Write(row, skill);

            if (hidden > 0 && !loggedSlots)
            {
                loggedSlots = true;
                Plugin.Logger.LogInfo($"skill row: {hidden} extra weapon slot(s) hidden");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"skill row: showing the class skill failed: {error.Message}");
        }
    }

    /// <summary>The prefab ships one row per weapon slot and the game opens
    /// three by default. The class holds exactly one skill, so the first row is
    /// kept and every other weapon row is switched off; the tomes live under
    /// their own parent and are untouched. A row is reused rather than created:
    /// adding a managed component at runtime is not possible under IL2CPP.</summary>
    private static InventoryItemPrefabUI KeepFirstRow(UnityEngine.Transform parent, out int hidden)
    {
        hidden = 0;
        var rows = parent.GetComponentsInChildren<InventoryItemPrefabUI>(true);
        if (rows == null)
        {
            return null;
        }

        InventoryItemPrefabUI kept = null;
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            if (row == null)
            {
                continue;
            }

            if (kept == null)
            {
                kept = row;
                continue;
            }

            if (row.gameObject.activeSelf)
            {
                row.gameObject.SetActive(false);
                hidden++;
            }
        }

        return kept;
    }

    private static void Write(InventoryItemPrefabUI row, SkillDefinition skill)
    {
        var texture = PoeAssets.GetSkillIcon(skill.Id);
        if (row.icon != null && texture != null)
        {
            row.icon.texture = texture;
        }

        if (row.t_level != null)
        {
            // R11b: the skill's level is the number of upgrade picks taken
            // (40 max), shown on the icon exactly like a tome's level.
            row.t_level.text = SkillMods.TotalStacks > 0
                ? SkillMods.TotalStacks.ToString()
                : string.Empty;
        }

        if (!Rows.Contains(row))
        {
            Rows.Add(row);
        }

        if (row.toolTipObject != null)
        {
            // V11: the tooltip states the skill's damage type, the one thing the
            // HUD never said (the tag drives the ailment and the number colour).
            row.toolTipObject.text =
                (string.IsNullOrEmpty(skill.Name) ? skill.Id : skill.Name) +
                "\n" + skill.TypeName();
        }

        if (!logged)
        {
            logged = true;
            Plugin.Logger.LogInfo(
                $"skill row: the weapon slot shows {skill.Id} ({skill.Name}), " +
                $"icon {(texture == null ? "missing" : texture.width + "x" + texture.height)}");
        }
    }
}
