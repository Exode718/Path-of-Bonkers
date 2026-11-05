using System;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Saves___Serialization.Progression.Stats;
using HarmonyLib;
using Wraeclast.Data;

namespace Wraeclast.Patches;

/// <summary>
/// The death screen's two text faults, both on <c>DeathStatsUi</c>.
///
/// V7 — the damage-source row.
///
/// <c>GameOverDamageSourcesUi</c> fills one <c>DamageSourceEntry</c> per
/// <c>RunStats.damageSources</c> key, and the row's label is
/// <c>LocalizationUtility.GetLocalizedDamageSource(source)</c>. Contact and
/// projectile damage are recorded under the literal <c>"Enemy"</c>
/// (<c>DamageUtility.GetPlayerDamage</c>, and every enemy attack prefab), while
/// the client's <c>DamageSources</c> table only carries the passive and ailment
/// names (Poison, Burn, Thorns, Zap, …) — it has no <c>Enemy</c> key. The game
/// therefore prints its own missing-key fallback in the row,
/// “Missing localization for DamageSources.Enemy”.
///
/// The attacker is known at the hit: <c>PlayerHealth.DamagePlayer</c> and
/// <c>DamagePlayerExternal</c> both carry the enemy. Those two hooks record a
/// display name — our Atlas boss name through <see cref="BossRoster.DisplayName"/>
/// for a roster spawn, the game's own localized enemy name otherwise — and the
/// entry's postfix replaces the fallback text only. A row the game did localize
/// is untouched, and any other unresolved key reads as itself.
///
/// V8 — the character label. The panel names the run's character from the
/// Megabonk slot's internal name ("cl4nk"); the postfix on <c>DeathStatsUi.Start</c>
/// rewrites it from the class table, so the screen states the class that ran.
/// </summary>
[HarmonyPatch]
internal static class DeathScreenPatches
{
    private const string EnemySource = "Enemy";
    private const string MissingPrefix = "Missing localization";

    private static string killer;
    private static bool logged;
    private static bool loggedName;

    /// <summary>The last creature that landed a hit on the player; at the death
    /// screen that is the killing blow, which is the row the report is about.</summary>
    internal static void NoteAttacker(Enemy enemy)
    {
        try
        {
            var data = enemy == null ? null : enemy.enemyData;
            if (data == null)
            {
                return;
            }

            var name = BossRoster.DisplayName(data);
            if (string.IsNullOrEmpty(name))
            {
                name = data.GetName();
            }

            if (!string.IsNullOrEmpty(name))
            {
                killer = name;
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"death source: the attacker name is unreadable: {error.Message}");
        }
    }

    /// <summary>A new run starts from <c>ItemInventory.Cleanup</c>, like the
    /// relic and skill-mod layers, so a graveyard name cannot carry over.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ItemInventory), nameof(ItemInventory.Cleanup))]
    private static void AfterCleanup()
    {
        killer = null;
    }

    /// <summary>V8: the panel names the run's character from the Megabonk slot
    /// (its internal prefab name, "cl4nk"), not from our class. The label is the
    /// last word: the postfix rewrites it from the class table, so whatever
    /// source the game asked before, the screen states the class the player ran.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeathStatsUi), "Start")]
    private static void AfterDeathStatsStart(DeathStatsUi __instance)
    {
        if (!Plugin.DeathScreenName.Value || __instance == null)
        {
            return;
        }

        try
        {
            var label = __instance.t_characterName;
            if (label == null)
            {
                return;
            }

            var player = MyPlayer.Instance;
            var inventory = player == null ? null : player.inventory;
            var data = inventory == null ? null : inventory.characterData;
            if (data == null || !ClassSwap.TryGetDefinition(data.eCharacter, out var definition))
            {
                return;
            }

            var native = label.text;
            label.text = definition.Name;

            if (!loggedName)
            {
                loggedName = true;
                Plugin.Logger.LogInfo(
                    $"death screen: the character reads \"{definition.Name}\" (native \"{native}\")");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"death screen: the character name is unreadable: {error.Message}");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DamageSourceEntry), nameof(DamageSourceEntry.Set))]
    private static void AfterSet(DamageSourceEntry __instance, DamageSource dmgSource)
    {
        if (!Plugin.DeathScreenSource.Value || __instance == null)
        {
            return;
        }

        try
        {
            var label = __instance.t_sourceName;
            var text = label == null ? null : label.text;
            if (text == null || text.IndexOf(MissingPrefix, StringComparison.Ordinal) < 0)
            {
                return;
            }

            var source = dmgSource == null ? null : dmgSource.damageSource;
            var name = Answer(source);
            label.text = name;

            if (logged)
            {
                return;
            }

            logged = true;
            Plugin.Logger.LogInfo(
                $"death source: \"{source}\" reads \"{name}\" (the game's DamageSources table has no such key)");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"death source: the row is unreadable: {error.Message}");
        }
    }

    /// <summary><c>Enemy</c> is the one source that names a class of attacker, so
    /// it answers the killer. Any other unresolved key already reads as itself
    /// (a Cactus, a weapon id), which is better than the fallback text.</summary>
    private static string Answer(string source)
    {
        if (!string.IsNullOrEmpty(killer) && string.Equals(source, EnemySource, StringComparison.Ordinal))
        {
            return killer;
        }

        return string.IsNullOrEmpty(source) ? "Unknown" : source;
    }
}
