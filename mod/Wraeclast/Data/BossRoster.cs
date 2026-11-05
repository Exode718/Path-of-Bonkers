using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Actors.Enemies;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Managers;

namespace Wraeclast.Data;

/// <summary>
/// S2 — the Atlas boss roster (<c>mod/data/bosses.json</c>).
///
/// Megabonk has twelve boss-tier <see cref="EEnemy"/> slots and one final-boss
/// slot, chosen by the stage data; our run structure needs three named floor
/// roles plus two minibosses. The roster maps a role to a boss, not a boss to a
/// slot, so the same asset can wear the floor-1 boss on one stage and the
/// floor-2 boss on the next:
///
///   floor 1 -> Searing Exarch, floor 2 -> Eater of Worlds,
///   floor 3 / FinalBoss flag -> Maven, miniboss waves -> Elder / Shaper.
///
/// The model is a field because the two new bosses have no client export yet:
/// they borrow the Shaper/Elder meshes until the extraction pass lands
/// (<c>modelPending</c>). The name and the model therefore both come from the
/// table, and <see cref="DisplayName"/> is what the boss bar reads through the
/// <c>EnemyData.GetName</c> prefix.
///
/// Resolution needs the spawn flag and, for a stage boss, the floor:
/// <see cref="Begin"/> runs on <c>Enemy.InitEnemy</c>'s prefix (where the flag
/// is still an argument), tags the shared <c>EnemyData</c> with the answer and
/// keeps it for <see cref="End"/>, which the postfix hands to the mesh attach.
/// </summary>
internal static class BossRoster
{
    internal sealed class Boss
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Model { get; set; }
        public bool ModelPending { get; set; }
    }

    private sealed class FloorRow
    {
        public int Floor { get; set; }
        public string Boss { get; set; }
    }

    private sealed class Sheet
    {
        public List<FloorRow> Floors { get; set; }
        public List<string> Minibosses { get; set; }
        public List<Boss> Bosses { get; set; }
    }

    private static readonly List<Boss> Bosses = new List<Boss>();
    private static readonly Dictionary<string, Boss> ById =
        new Dictionary<string, Boss>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, string> Floors = new Dictionary<int, string>();
    private static readonly List<string> Minibosses = new List<string>();

    /// <summary>Boss id per <c>EnemyData</c> instance, so the name prefix can
    /// answer for the row that spawned. The rows are shared assets, so the last
    /// spawn owns the answer (two live bosses only share a row when they are the
    /// same role).</summary>
    private static readonly Dictionary<int, string> Tagged = new Dictionary<int, string>();

    /// <summary>Prefix -> postfix handoff, keyed by the <c>EnemyData</c> id.</summary>
    private static readonly Dictionary<int, string> Pending = new Dictionary<int, string>();

    private static string forced;
    private static int minibossIndex;
    private static bool loaded;

    internal static IReadOnlyList<Boss> All => Bosses;

    internal static bool Load(string path)
    {
        Bosses.Clear();
        ById.Clear();
        Floors.Clear();
        Minibosses.Clear();
        Tagged.Clear();
        Pending.Clear();
        forced = null;
        minibossIndex = 0;
        loaded = false;

        if (!File.Exists(path))
        {
            Plugin.Logger.LogError($"Boss roster missing: {path} (run mod/tools/prepare-data.ps1)");
            return false;
        }

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var sheet = JsonSerializer.Deserialize<Sheet>(File.ReadAllText(path), options);
            if (sheet == null)
            {
                return false;
            }

            var skipped = 0;
            foreach (var boss in sheet.Bosses ?? new List<Boss>())
            {
                if (boss == null || string.IsNullOrEmpty(boss.Id) || string.IsNullOrEmpty(boss.Name))
                {
                    skipped++;
                    continue;
                }

                if (string.IsNullOrEmpty(boss.Model))
                {
                    boss.Model = boss.Id;
                }

                if (!File.Exists(ModPaths.Creature(boss.Model)))
                {
                    Plugin.Logger.LogWarning(
                        $"boss {boss.Id}: model {boss.Model} has no GLB (the native renderer stays)");
                }

                Bosses.Add(boss);
                ById[boss.Id] = boss;
            }

            foreach (var row in sheet.Floors ?? new List<FloorRow>())
            {
                if (row == null || row.Floor < 1 || row.Floor > 3)
                {
                    skipped++;
                    continue;
                }

                if (!ById.ContainsKey(row.Boss ?? string.Empty))
                {
                    Plugin.Logger.LogWarning(
                        $"boss roster: floor {row.Floor} names {row.Boss ?? "null"}, which is not a boss");
                    skipped++;
                    continue;
                }

                Floors[row.Floor] = row.Boss;
            }

            foreach (var id in sheet.Minibosses ?? new List<string>())
            {
                if (!ById.ContainsKey(id ?? string.Empty))
                {
                    Plugin.Logger.LogWarning($"boss roster: miniboss {id ?? "null"} is not a boss");
                    skipped++;
                    continue;
                }

                Minibosses.Add(id);
            }

            loaded = Bosses.Count > 0 && Floors.Count > 0 && Minibosses.Count > 0;

            var floors = new List<string>();
            foreach (var floor in new[] { 1, 2, 3 })
            {
                floors.Add($"floor {floor} {Name(Floors.TryGetValue(floor, out var id) ? id : null)}");
            }

            var minis = new List<string>();
            foreach (var id in Minibosses)
            {
                minis.Add(Name(id));
            }

            Plugin.Logger.LogInfo(
                $"boss roster: {string.Join("; ", floors)}; minibosses {string.Join(" / ", minis)}" +
                $" ({Bosses.Count} row(s), {skipped} skipped)");

            if (!loaded)
            {
                Plugin.Logger.LogWarning(
                    "boss roster: incomplete role set (a floor and a miniboss are required), " +
                    "bosses keep Megabonk's names and the native slot mapping");
            }

            foreach (var boss in Bosses)
            {
                if (!boss.ModelPending)
                {
                    continue;
                }

                Plugin.Logger.LogInfo(
                    $"boss roster: {boss.Name} borrows the {boss.Model} mesh until its own export");
            }

            return loaded;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Boss roster failed to parse: {error}");
            return false;
        }
    }

    internal static bool IsLoaded => loaded;

    /// <summary>Resolves the boss for this spawn and tags the row. Called on the
    /// <c>InitEnemy</c> prefix, before the attack controller snapshots the data.
    /// Returns null for a non-boss enemy or before the roster loaded.</summary>
    internal static string Begin(EnemyData data, EEnemyFlag flag)
    {
        if (data == null || !PoeEnemies.IsBossTier(data.enemyName))
        {
            return null;
        }

        var boss = Resolve(data.enemyName, flag);
        if (boss == null)
        {
            return null;
        }

        var key = data.GetInstanceID();
        Tagged[key] = boss;
        Pending[key] = boss;
        return boss;
    }

    /// <summary>Hands the prefix's answer to the mesh attach (postfix) and clears
    /// the handoff, so a failed prefix cannot leak a stale role into the next
    /// spawn of the same row.</summary>
    internal static string End(EnemyData data)
    {
        if (data == null)
        {
            return null;
        }

        var key = data.GetInstanceID();
        if (!Pending.TryGetValue(key, out var boss))
        {
            return null;
        }

        Pending.Remove(key);
        return boss;
    }

    /// <summary>Dev-only preview (the F5/F6/F7 keys, the F4 trio and the
    /// gallery): the next boss spawn answers this id instead of its role.</summary>
    internal static void ForceNext(string bossId)
    {
        forced = bossId;
    }

    internal static void ClearForce()
    {
        forced = null;
    }

    private static string Resolve(EEnemy kind, EEnemyFlag flag)
    {
        if (!loaded)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(forced))
        {
            var forcedId = forced;
            forced = null;
            if (ById.ContainsKey(forcedId))
            {
                return forcedId;
            }
        }

        // The arena boss is the one spawn that names its tier in the flag, so
        // Maven is the floor-3 boss whatever slot the final stage uses.
        if ((flag & EEnemyFlag.FinalBoss) != 0)
        {
            return Floor(3);
        }

        if ((flag & EEnemyFlag.SummonerMiniboss) != 0 || IsMinibossKind(kind))
        {
            return NextMiniboss();
        }

        // The final arena's fight spawns extra boss-tier enemies (the log shows
        // two `Ent1` rows after the Maven). They are not floor bosses: resolving
        // them by the stage index gave two more of the floor's role, and the
        // floor-1 Searing Exarch borrows the Shaper mesh until its export, so
        // the arena read as a second and a third Shaper. The roster's other
        // tier is the miniboss pair, so an arena extra takes the next of
        // Elder/Shaper — one event's two extras differ, as on a floor.
        if (InFinalArena())
        {
            return NextMiniboss();
        }

        return Floor(FloorOfStage());
    }

    private static bool InFinalArena()
    {
        try
        {
            return MapController.IsFinalBossStage();
        }
        catch (Exception)
        {
            // No MapController (menu, data load): not the arena.
            return false;
        }
    }

    private static string NextMiniboss()
    {
        if (Minibosses.Count == 0)
        {
            return null;
        }

        var boss = Minibosses[minibossIndex % Minibosses.Count];
        minibossIndex++;
        return boss;
    }

    private static string Floor(int floor) =>
        Floors.TryGetValue(floor, out var boss) ? boss : null;

    private static int FloorOfStage()
    {
        try
        {
            var floor = MapController.GetStageIndex() + 1;
            return floor < 1 ? 1 : floor > 3 ? 3 : floor;
        }
        catch (Exception)
        {
            // No MapController (menu, data load): floor 1 is the safe answer.
            return 1;
        }
    }

    private static bool IsMinibossKind(EEnemy kind)
    {
        switch (kind)
        {
            case EEnemy.MinibossPig:
            case EEnemy.MinibossGolem:
            case EEnemy.MinibossGolemSand:
            case EEnemy.MinibossScorpion:
            case EEnemy.Ghostham:
            case EEnemy.CalciumDad:
                return true;

            default:
                return false;
        }
    }

    internal static string Model(string bossId) =>
        bossId != null && ById.TryGetValue(bossId, out var boss) ? boss.Model : null;

    internal static string Name(string bossId) =>
        bossId != null && ById.TryGetValue(bossId, out var boss) ? boss.Name : null;

    internal static bool ModelPending(string bossId) =>
        bossId != null && ById.TryGetValue(bossId, out var boss) && boss.ModelPending;

    /// <summary>The boss bar reads <c>EnemyData.GetName</c>; a tagged row answers
    /// our name instead of Megabonk's localized one ("Lil Bark", "Chunkham the
    /// Terrible"). Null means "not a boss row", and the game answers.</summary>
    internal static string DisplayName(EnemyData data)
    {
        if (data == null)
        {
            return null;
        }

        return Tagged.TryGetValue(data.GetInstanceID(), out var boss) ? Name(boss) : null;
    }
}
