using System;
using System.Collections.Generic;
using Assets.Scripts.Inventory__Items__Pickups.Weapons.Attacks;
using UnityEngine;

namespace Wraeclast.Skills;

/// <summary>
/// SND (interim) — a cast sound per class skill, taken from the **host's own
/// weapon sounds**.
///
/// A custom clip cannot be built in this interop (see the SND checklist entry:
/// `SetData`, the PCM callback, `UnityWebRequest` and `AudioClip.Create` all
/// throw, and the native injection crashed the game). What is safe is **playing
/// clips that already exist**: every Megabonk weapon carries its attack prefab
/// chain, and those prefabs hold `RandomSfx` pools with the weapon's own sounds.
///
/// The operator's mapping (2026-11-03):
///   Witch → Fox (`WeaponFireStaff`), Marauder → Megachad (`WeaponAura`),
///   Ranger → Robinette (`WeaponBow`), Scion → Vlad (`WeaponBloodMagic`),
///   Shadow → Amog (`WeaponPoisonFlask`), Duelist → Birdo (`WeaponTornado`),
///   Templar → Noelle (`WeaponFrostWalker`).
///
/// The resolution goes `DataManager.GetCharacterData(ECharacter)` →
/// `CharacterData.weapon` → `WeaponData.attack` → the `WeaponAttack` component's
/// `prefabProjectile` / `prefabMuzzle` / `prefabHit`; every `RandomSfx` and
/// `AudioSource` in that chain is collected, and one clip is picked per cast. A
/// skill whose weapon prefab cannot be resolved falls back to the scene's
/// `RandomSfx` catalogue, so a cast is never silent. Nothing is created or
/// written, and every entry point is exception-safe.
/// </summary>
internal static class SkillAudio
{
    private static readonly Dictionary<string, ECharacter> Characters =
        new Dictionary<string, ECharacter>(StringComparer.OrdinalIgnoreCase)
        {
            { "fireball", ECharacter.Fox },
            { "righteous-fire", ECharacter.Megachad },
            { "ice-shot", ECharacter.Robinette },
            { "arc", ECharacter.Vlad },
            { "viper-strike", ECharacter.Amog },
            { "cyclone", ECharacter.Birdo },
            { "ice-nova", ECharacter.Noelle },
        };

    private sealed class Set
    {
        internal AudioClip[] Clips;
        internal string Source;
    }

    private static readonly Dictionary<string, Set> Resolved =
        new Dictionary<string, Set>(StringComparer.OrdinalIgnoreCase);

    private static readonly List<AudioClip> Fallback = new List<AudioClip>();

    private static AudioSource source;
    private static bool fallbackLogged;

    /// <summary>Resolves this skill's clip set ahead of the first cast. Never throws.</summary>
    internal static void Warmup(string skillId)
    {
        if (!Plugin.SkillSounds.Value)
        {
            return;
        }

        try
        {
            Resolve(skillId);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"skill audio: {skillId} warmup failed: {error}");
        }
    }

    /// <summary>Plays one clip from the skill's set. Never throws.</summary>
    internal static void PlayCast(string skillId)
    {
        if (!Plugin.SkillSounds.Value || string.IsNullOrEmpty(skillId))
        {
            return;
        }

        try
        {
            var set = Resolve(skillId);
            if (set == null || set.Clips == null || set.Clips.Length == 0)
            {
                return;
            }

            var clip = set.Clips[UnityEngine.Random.Range(0, set.Clips.Length)];
            if (clip == null)
            {
                return;
            }

            // Play through the game's own AudioManager: its source sits on the
            // SFX mixer group, so the volume setting in the menus (and the pause
            // behaviour) applies. Our own source ignored both — the operator's
            // "beaucoup trop fort, ça change rien".
            var manager = AudioManager.Instance;
            if (manager != null)
            {
                manager.PlaySfx(clip);
                return;
            }

            EnsureSource();
            source.pitch = UnityEngine.Random.Range(0.96f, 1.04f);
            source.PlayOneShot(clip, UnityEngine.Random.Range(0.35f, 0.45f));
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"skill audio: {skillId} playback failed: {error}");
        }
    }

    /// <summary>The run reset: stop the one-shot and forget every clip, which
    /// belongs to the floor that is being left.</summary>
    internal static void Reset()
    {
        try
        {
            if (source != null)
            {
                source.Stop();
                source.pitch = 1f;
            }
        }
        catch (Exception)
        {
            source = null;
        }

        Resolved.Clear();
        Fallback.Clear();
        fallbackLogged = false;
    }

    private static Set Resolve(string skillId)
    {
        if (string.IsNullOrEmpty(skillId))
        {
            return null;
        }

        if (Resolved.TryGetValue(skillId, out var cached) && Alive(cached.Clips))
        {
            return cached;
        }

        Resolved.Remove(skillId);
        var clips = new List<AudioClip>();
        var origin = "";

        try
        {
            if (Characters.TryGetValue(skillId, out var character))
            {
                origin = ResolveWeapon(character, clips);
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"skill audio: {skillId} weapon resolution failed: {error}");
        }

        if (clips.Count == 0)
        {
            CatalogFallback(clips);
            origin = "scene RandomSfx fallback";
        }

        var deduped = Dedupe(clips);
        if (deduped.Length == 0)
        {
            return null;
        }

        var set = new Set { Clips = deduped, Source = origin };
        Resolved[skillId] = set;
        Plugin.Logger.LogInfo(
            $"skill audio: {skillId} <- {origin}: {deduped.Length} clip(s) " +
            $"[{string.Join(", ", Names(deduped))}]");
        return set;
    }

    /// <summary>Character -> weapon -> attack prefabs -> their sound pools.</summary>
    private static string ResolveWeapon(ECharacter character, List<AudioClip> clips)
    {
        var manager = DataManager.Instance;
        if (manager == null)
        {
            return $"{character} (no DataManager)";
        }

        var data = manager.GetCharacterData(character);
        if (data == null)
        {
            return $"{character} (no CharacterData)";
        }

        var weapon = data.weapon;
        if (weapon == null)
        {
            return $"{character} (no weapon)";
        }

        var attack = weapon.attack;
        var label = $"{character}/{weapon.eWeapon}";
        if (attack == null)
        {
            return $"{label} (no attack prefab)";
        }

        // The cast sound lives in the attack/projectile/muzzle prefabs; the hit
        // prefab is an impact and is only used when nothing else resolves.
        Collect(attack, clips);

        var attackComponent = attack.GetComponent<WeaponAttack>();
        if (attackComponent != null)
        {
            Collect(attackComponent.prefabProjectile, clips);
            Collect(attackComponent.prefabMuzzle, clips);
            if (clips.Count == 0)
            {
                Collect(attackComponent.prefabHit, clips);
            }
        }

        return label;
    }

    private static void Collect(GameObject root, List<AudioClip> clips)
    {
        if (root == null)
        {
            return;
        }

        var pools = root.GetComponentsInChildren<RandomSfx>(true);
        if (pools != null)
        {
            foreach (var pool in pools)
            {
                if (pool == null || pool.sounds == null)
                {
                    continue;
                }

                for (var index = 0; index < pool.sounds.Length; index++)
                {
                    var clip = pool.sounds[index];
                    if (Wanted(clip))
                    {
                        clips.Add(clip);
                    }
                }
            }
        }
    }

    /// <summary>Only the weapon's short one-shot sounds: the attack prefab
    /// pools also hold loops and ambiences, and those read as "the wrong sound"
    /// (or a sound that never ends).</summary>
    private static bool Wanted(AudioClip clip)
    {
        if (clip == null)
        {
            return false;
        }

        var name = clip.name ?? "";
        if (name.IndexOf("loop", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("ambien", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("music", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("theme", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        return clip.length <= 3.5f;
    }

    /// <summary>Last resort: the scene's own `RandomSfx` pools, by name (the
    /// 0.9.198 behaviour), so a cast still makes a sound if a weapon prefab is
    /// not loaded.</summary>
    private static void CatalogFallback(List<AudioClip> clips)
    {
        if (Fallback.Count > 0 && Alive(Fallback.ToArray()))
        {
            clips.AddRange(Fallback);
            return;
        }

        Fallback.Clear();
        var pools = UnityEngine.Object.FindObjectsOfType<RandomSfx>(true);
        if (pools == null)
        {
            return;
        }

        for (var poolIndex = 0; poolIndex < pools.Length; poolIndex++)
        {
            var pool = pools[poolIndex];
            if (pool == null || pool.sounds == null)
            {
                continue;
            }

            for (var index = 0; index < pool.sounds.Length; index++)
            {
                var clip = pool.sounds[index];
                if (Wanted(clip))
                {
                    Fallback.Add(clip);
                }
            }
        }

        if (!fallbackLogged && Fallback.Count > 0)
        {
            fallbackLogged = true;
            Plugin.Logger.LogInfo(
                $"skill audio: {Fallback.Count} scene clip(s) in the fallback pool");
        }

        clips.AddRange(Fallback);
    }

    private static AudioClip[] Dedupe(List<AudioClip> clips)
    {
        var seen = new HashSet<int>();
        var unique = new List<AudioClip>();
        foreach (var clip in clips)
        {
            if (clip != null && seen.Add(clip.GetInstanceID()))
            {
                unique.Add(clip);
            }
        }

        return unique.ToArray();
    }

    private static bool Alive(AudioClip[] clips)
    {
        if (clips == null || clips.Length == 0)
        {
            return false;
        }

        foreach (var clip in clips)
        {
            if (clip == null)
            {
                return false;
            }
        }

        return true;
    }

    private static string[] Names(AudioClip[] clips)
    {
        var names = new string[clips.Length];
        for (var index = 0; index < clips.Length; index++)
        {
            names[index] = clips[index].name;
        }

        return names;
    }

    private static void EnsureSource()
    {
        if (source != null)
        {
            return;
        }

        var holder = new GameObject("WraeclastAudio");
        UnityEngine.Object.DontDestroyOnLoad(holder);
        source = holder.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0f;
        source.volume = 0.8f;
        source.loop = false;
    }
}
