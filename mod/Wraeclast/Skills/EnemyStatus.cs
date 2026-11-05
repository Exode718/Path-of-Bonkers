using System;
using System.Collections.Generic;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Game.Combat.EnemyDebuffs;
using UnityEngine;
using Wraeclast.Data;

namespace Wraeclast.Skills;

/// <summary>
/// V11 — the enemy status readout.
///
/// The game applies burn / freeze / poison / stun as native debuffs
/// (<see cref="Enemy.HasDebuff"/>) but gives them no visual: its
/// <c>EnemyStatusSymbols</c> only carries the elite / boss / challenge flags.
/// This pass therefore draws one small camera-facing badge above every live
/// enemy that carries one of the four states:
///
/// - burn: orange orb
/// - freeze: blue ring
/// - poison: green orb
/// - stun: yellow ring
///
/// The debuff query runs at 5 Hz (a component sweep, capped at
/// <see cref="MaxTracked"/> enemies); the badge holder is re-placed and
/// billboarded every frame so it follows a running enemy and the camera without
/// a visible lag. Nothing is parented to the enemy, so the game's own scale and
/// orientation cannot distort the badge, and every material comes from
/// <see cref="SkillVisuals"/>' cached procedural textures (no new asset).
/// </summary>
internal static class EnemyStatus
{
    /// <summary>How often the native debuffs are read. A status lasts at least
    /// a couple of seconds, so 5 Hz is enough and keeps the scene sweep cheap.</summary>
    private const float RefreshSeconds = 0.2f;

    /// <summary>Cap on simultaneously badged enemies. A pack is a few dozen at
    /// most; the rest share the same fight and do not need their own badge.</summary>
    private const int MaxTracked = 48;

    private const int KindCount = 4;

    private static readonly EDebuff[] Debuffs =
    {
        EDebuff.Burn, EDebuff.Freeze, EDebuff.Poison, EDebuff.Stun
    };

    private static readonly string[] Ids = { "burn", "freeze", "poison", "stun" };

    private static readonly Color[] Colours =
    {
        new Color(1f, 0.45f, 0.10f, 0.95f),
        new Color(0.35f, 0.75f, 1f, 0.95f),
        new Color(0.45f, 1f, 0.25f, 0.95f),
        new Color(1f, 0.90f, 0.20f, 0.95f)
    };

    private sealed class Entry
    {
        internal Enemy Enemy;
        internal GameObject Holder;
        internal readonly GameObject[] Icons = new GameObject[KindCount];
        internal float Height;
        internal float Size;
    }

    private static readonly Dictionary<int, Entry> Entries = new Dictionary<int, Entry>();
    private static readonly List<int> Stale = new List<int>();
    private static float nextRefresh;
    private static bool cappedLogged;
    private static int cameraFrame = -1;
    private static Quaternion cameraRotation = Quaternion.identity;

    internal static void Tick()
    {
        if (!Plugin.EnemyStatusIcons.Value)
        {
            if (Entries.Count > 0)
            {
                Reset();
            }

            return;
        }

        try
        {
            if (Time.time >= nextRefresh)
            {
                nextRefresh = Time.time + RefreshSeconds;
                Refresh();
            }

            Place();
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"enemy status: {error.Message}");
            nextRefresh = Time.time + 1f;
        }
    }

    /// <summary>The 5 Hz pass: keep a badged entry for every live enemy that
    /// carries one of the four states, drop the others.</summary>
    private static void Refresh()
    {
        var enemies = UnityEngine.Object.FindObjectsOfType<Enemy>(true);
        if (enemies == null || enemies.Length == 0)
        {
            Reset();
            return;
        }

        for (var index = 0; index < enemies.Length; index++)
        {
            var enemy = enemies[index];
            if (enemy == null)
            {
                continue;
            }

            var key = enemy.GetInstanceID();
            if (!enemy.gameObject.activeInHierarchy || Dead(enemy))
            {
                Remove(key);
                continue;
            }

            var mask = Mask(enemy);
            if (mask == 0)
            {
                Remove(key);
                continue;
            }

            if (!Entries.TryGetValue(key, out var entry))
            {
                if (Entries.Count >= MaxTracked)
                {
                    if (!cappedLogged)
                    {
                        cappedLogged = true;
                        Plugin.Logger.LogInfo(
                            $"enemy status: {MaxTracked} badge(s) tracked, further statuses stay hidden");
                    }

                    continue;
                }

                entry = Create(enemy);
            }

            Show(entry, mask);
        }

        // A destroyed enemy cannot appear in the sweep above: prune its badge.
        Stale.Clear();
        foreach (var pair in Entries)
        {
            if (pair.Value.Enemy == null)
            {
                Stale.Add(pair.Key);
            }
        }

        for (var index = 0; index < Stale.Count; index++)
        {
            Remove(Stale[index]);
        }
    }

    private static Entry Create(Enemy enemy)
    {
        var entry = new Entry
        {
            Enemy = enemy,
            Height = HeightOf(enemy),
            Holder = new GameObject("WraeclastStatus")
        };

        Entries[enemy.GetInstanceID()] = entry;
        return entry;
    }

    private static void Show(Entry entry, int mask)
    {
        entry.Size = Mathf.Clamp(entry.Height * 0.13f, 0.5f, 1.6f);

        var count = 0;
        for (var index = 0; index < KindCount; index++)
        {
            if ((mask & (1 << index)) != 0)
            {
                count++;
            }
        }

        var slot = 0;
        for (var index = 0; index < KindCount; index++)
        {
            var icon = entry.Icons[index];
            if ((mask & (1 << index)) == 0)
            {
                if (icon != null && icon.activeSelf)
                {
                    icon.SetActive(false);
                }

                continue;
            }

            if (icon == null)
            {
                icon = SkillVisuals.Sprite(
                    $"WraeclastStatus_{Ids[index]}",
                    BadgeTexture(index),
                    Colours[index],
                    Vector3.zero,
                    Quaternion.identity,
                    1f);
                icon.transform.SetParent(entry.Holder.transform, false);
                entry.Icons[index] = icon;
            }

            icon.SetActive(true);
            icon.transform.localRotation = Quaternion.identity;
            icon.transform.localPosition =
                new Vector3((slot - (count - 1) * 0.5f) * entry.Size * 1.15f, 0f, 0f);
            icon.transform.localScale = Vector3.one * entry.Size;
            slot++;
        }
    }

    /// <summary>Every frame: stand the badge just above the drawn model and face
    /// it to the camera, so it follows a moving enemy without the 5 Hz step.</summary>
    private static void Place()
    {
        if (Entries.Count == 0)
        {
            return;
        }

        var rotation = CameraRotation();
        foreach (var pair in Entries)
        {
            var entry = pair.Value;
            var enemy = entry.Enemy;
            if (enemy == null || entry.Holder == null)
            {
                continue;
            }

            entry.Holder.transform.position =
                enemy.transform.position + Vector3.up * (entry.Height + entry.Size * 0.75f);
            entry.Holder.transform.rotation = rotation;
        }
    }

    /// <summary>Burn and poison are orbs, freeze and stun are rings, so the four
    /// states read apart even in one colour-blind channel.</summary>
    private static Texture2D BadgeTexture(int kind) =>
        kind == 0 || kind == 2 ? SkillVisuals.Orb() : SkillVisuals.Ring();

    private static int Mask(Enemy enemy)
    {
        var mask = 0;
        for (var index = 0; index < KindCount; index++)
        {
            if (Has(enemy, Debuffs[index]))
            {
                mask |= 1 << index;
            }
        }

        return mask;
    }

    private static bool Has(Enemy enemy, EDebuff debuff)
    {
        try
        {
            return enemy.HasDebuff(debuff);
        }
        catch (Exception)
        {
            // A pooled or dying enemy can refuse the query; no badge then.
            return false;
        }
    }

    private static bool Dead(Enemy enemy)
    {
        try
        {
            return enemy.IsDead();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The fitted creature height when the enemy has one (a boss is
    /// 16.78 units, not the 13.42 slot), else Megabonk's own slot height.</summary>
    private static float HeightOf(Enemy enemy)
    {
        if (PoeEnemies.TryGetHeight(enemy, out var fitted) && fitted > 0.1f)
        {
            return fitted;
        }

        try
        {
            return Mathf.Max(1f, enemy.GetHeight());
        }
        catch (Exception)
        {
            return 3f;
        }
    }

    private static Quaternion CameraRotation()
    {
        if (cameraFrame != Time.frameCount)
        {
            cameraFrame = Time.frameCount;
            var camera = Camera.main;
            cameraRotation = camera != null ? camera.transform.rotation : Quaternion.identity;
        }

        return cameraRotation;
    }

    private static void Remove(int key)
    {
        if (!Entries.TryGetValue(key, out var entry))
        {
            return;
        }

        Entries.Remove(key);
        if (entry.Holder != null)
        {
            UnityEngine.Object.Destroy(entry.Holder);
            entry.Holder = null;
        }
    }

    /// <summary>The run reset (<c>ItemInventory.Cleanup</c>): a new floor must not
    /// inherit a badge from the previous one.</summary>
    internal static void Reset()
    {
        foreach (var entry in Entries.Values)
        {
            if (entry.Holder != null)
            {
                UnityEngine.Object.Destroy(entry.Holder);
            }
        }

        Entries.Clear();
        Stale.Clear();
        cappedLogged = false;
        nextRefresh = 0f;
    }
}
