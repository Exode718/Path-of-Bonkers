using System;
using System.Collections.Generic;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Game.MapGeneration;
using Assets.Scripts.Managers;
using Assets.Scripts.Utility;
using UnityEngine;
using Wraeclast.Data;

namespace Wraeclast.Dev;

/// <summary>
/// A2c — where does Megabonk really place its scenery?
///
/// A2 mounted the prop swap on <c>RandomObjectPlacer.Generate</c> and the live
/// run proved the hook never fires on a Forest floor (the patch is listed, the
/// terrain pass runs, and not one <c>props:</c> line exists). Rather than guess
/// a second method, this survey answers it from the game's own scene: once per
/// floor it logs the placer it can find (row list, shrine rows, children) and
/// every object named like a prop, with its renderer count, bounds and parent
/// path, plus what the swap rules would currently do with that name.
///
/// **A5.0 adds the map-element register.** The survey above answers where the
/// scenery comes from and what the rules do with a name; A5 has to replace
/// **every element a floor spawns**, and the ones it still owes (the shady-guy
/// merchant, the microwave, the cursed and challenge shrines) are not scenery
/// names — they are interactables. Every one of them derives from
/// <c>BaseInteractable</c>, so this sweep asks the game itself: once a second it
/// names each **kind** (component type + prefab name) the first time it appears,
/// with its renderer count, bounds, parent chain and, where the client class
/// declares one, its own body renderer. A merchant that only walks in at 8:00 is
/// therefore named when it appears, not missed by the once-per-floor survey.
///
/// It is a diagnostic, not a feature: it never changes what is drawn, it runs
/// behind <c>[Props] PropProbe</c>, and its output is what the A5 swaps are
/// chosen from.
/// </summary>
internal static class PropProbe
{
    /// <summary>How long after a stage starts the survey runs: the map is
    /// built before the clock starts, so a few seconds in everything the floor
    /// will ever place exists.</summary>
    private const float DelaySeconds = 3f;

    /// <summary>Row and name-group caps, so a Forest floor's report stays one
    /// readable block instead of a thousand lines.</summary>
    private const int RowShown = 24;
    private const int GroupShown = 40;
    private const int UnmappedShown = 40;

    /// <summary>Smallest span an unclaimed object needs to be worth naming: below
    /// it, the scene is grass, decals and debris.</summary>
    private const float UnmappedMinSize = 2f;

    /// <summary>A5.0: how often the live register looks for a new interactable
    /// kind. It is a component query over a floor's interactive objects (a few
    /// dozen), not a scene walk, so once a second is cheap and catches a merchant
    /// that appears mid-floor.</summary>
    private const float InteractableScanSeconds = 1f;

    /// <summary>Names that look like scenery. A prefab the rules miss is the
    /// whole point of the survey, so the list is broad on purpose.</summary>
    private static readonly string[] Keywords =
    {
        "tree", "rock", "log", "bush", "box", "shrine", "moai", "pot", "urn",
        "pillar", "wall", "tower", "ruin", "brick", "castle", "cube", "crate", "boulder",
    };

    private static int stage = -1;
    private static float stageClock;
    private static bool reported;

    /// <summary>A5.0: the kinds already named on this floor (`type|prefab[|variant]`),
    /// so the register prints one line per kind while the scene reshuffles.</summary>
    private static readonly HashSet<string> Interactables = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>A5.0: instances already named, so the typed scans and the
    /// catch-all walk cannot log the same merchant twice under two names.</summary>
    private static readonly HashSet<int> InteractableInstances = new HashSet<int>();

    private static float nextInteractableScan;
    private static bool interactableHeader;

    internal static void Tick(MyPlayer player)
    {
        if (!Plugin.PropProbe.Value || player == null)
        {
            return;
        }

        try
        {
            var index = MapController.GetStageIndex();

            // A floor is a new stage index, or the same index with the clock
            // rewound (a death and restart on floor 1 keeps index 0).
            if (index != stage || MyTime.stageTimer + 1f < stageClock)
            {
                stage = index;
                reported = false;
                Interactables.Clear();
                InteractableInstances.Clear();
                nextInteractableScan = 0f;
                interactableHeader = false;
            }

            stageClock = MyTime.stageTimer;

            // A5.0 runs from the first frame: a merchant or a microwave that
            // appears mid-floor would be missed by the one survey below.
            ScanInteractables();

            if (reported || MyTime.stageTimer < DelaySeconds)
            {
                return;
            }

            reported = true;
            Report();
        }
        catch (Exception error)
        {
            // One failed survey must not repeat every frame for the whole floor.
            reported = true;
            Plugin.Logger.LogError($"props probe: the floor survey failed: {error}");
        }
    }

    private static void Report()
    {
        var placer = UnityEngine.Object.FindObjectOfType<RandomObjectPlacer>();
        if (placer == null)
        {
            Plugin.Logger.LogWarning(
                "props probe: no RandomObjectPlacer in the scene, so this floor's scenery does not come from the placer " +
                "(the panel and prefab rows below are still listed)");
        }
        else
        {
            ReportPlacer(placer);
        }

        ReportScene();
    }

    private static void ReportPlacer(RandomObjectPlacer placer)
    {
        var rows = placer.randomObjects;
        Plugin.Logger.LogInfo(
            $"props probe: placer {placer.name} at {Path(placer.transform)} — centre {Vector(placer.center)}, " +
            $"size {Vector(placer.size)}, rows {(rows == null ? 0 : rows.Length)}, " +
            $"children {placer.transform.childCount}, charge shrine row {Row(placer.chargeShrineSpawns)}, " +
            $"greed shrine row {Row(placer.greedShrineSpawns)}");

        if (rows == null)
        {
            return;
        }

        var shown = Mathf.Min(rows.Length, RowShown);
        for (var index = 0; index < shown; index++)
        {
            var row = rows[index];
            if (row == null)
            {
                continue;
            }

            Plugin.Logger.LogInfo(
                $"props probe:   row {index} {Row(row)} — amount {row.amount}..{row.maxAmount}, " +
                $"scale {row.scaleMin:0.##}..{row.scaleMax:0.##}, " +
                $"normal {(row.alignWithNormal ? "aligned" : "upright")}");
        }

        if (rows.Length > shown)
        {
            Plugin.Logger.LogInfo($"props probe:   … {rows.Length - shown} more row(s)");
        }
    }

    /// <summary>The scene sweep: every active object whose name looks like a
    /// prop, grouped by name (instances of one prefab share it), with the
    /// sample's renderer count, bounds, parent path and rule verdict.</summary>
    private static void ReportScene()
    {
        var all = UnityEngine.Object.FindObjectsOfType<Transform>();
        if (all == null)
        {
            return;
        }

        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        for (var index = 0; index < all.Length; index++)
        {
            var node = all[index];
            if (node == null || Ours(node.name) || !HasKeyword(node.name))
            {
                continue;
            }

            if (!groups.TryGetValue(node.name, out var group))
            {
                group = new Group(node);
                groups[node.name] = group;
            }

            group.Count++;
        }

        if (groups.Count == 0)
        {
            Plugin.Logger.LogWarning(
                $"props probe: {all.Length} transform(s) in the scene, none named like a prop " +
                $"({string.Join("/", Keywords)}), so the scenery prefabs are named something else");
            return;
        }

        var ordered = new List<KeyValuePair<string, Group>>(groups);
        ordered.Sort((left, right) => right.Value.Count.CompareTo(left.Value.Count));

        Plugin.Logger.LogInfo(
            $"props probe: {ordered.Count} object kind(s) named like a prop among {all.Length} transform(s):");

        var shown = Mathf.Min(ordered.Count, GroupShown);
        for (var index = 0; index < shown; index++)
        {
            var pair = ordered[index];
            var sample = pair.Value.Sample;
            var parts = sample.GetComponentsInChildren<MeshRenderer>(true);
            var rule = ShrineSwap.Describe(sample) ?? ElementSwap.Describe(sample) ?? PropSwap.Describe(pair.Key);
            Plugin.Logger.LogInfo(
                $"props probe:   {pair.Key} x{pair.Value.Count} — renderers {(parts == null ? 0 : parts.Length)}, " +
                $"bounds {Size(parts)}, at {Vector(sample.position)}, " +
                $"rule {rule}, under {Path(sample.parent)}");
        }

        if (ordered.Count > shown)
        {
            Plugin.Logger.LogInfo($"props probe:   … {ordered.Count - shown} more kind(s)");
        }

        ReportUnmapped(all);
    }

    /// <summary>
    /// A2m: the discovery half. The keyword list can only find what it already
    /// expects, and the towers the operator pointed at match none of it. Every
    /// object kind with a renderer that is at least a couple of metres across and
    /// that no rule claims is listed here, name and size first — one run then says
    /// what the scenery is actually called instead of another guess.
    /// </summary>
    private static void ReportUnmapped(Transform[] all)
    {
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        for (var index = 0; index < all.Length; index++)
        {
            var node = all[index];

            // Our own child meshes are not scenery: they are what the pass drew.
            if (node == null || Ours(node.name) || HasKeyword(node.name))
            {
                continue;
            }

            var renderers = node.GetComponentsInChildren<MeshRenderer>(true);
            if (renderers == null || renderers.Length == 0)
            {
                continue;
            }

            var box = renderers[0].bounds;
            for (var part = 1; part < renderers.Length; part++)
            {
                if (renderers[part] != null)
                {
                    box.Encapsulate(renderers[part].bounds);
                }
            }

            var span = Mathf.Max(box.size.x, Mathf.Max(box.size.y, box.size.z));
            if (span < UnmappedMinSize)
            {
                continue;
            }

            if (!groups.TryGetValue(node.name, out var group))
            {
                group = new Group(node);
                groups[node.name] = group;
            }

            group.Count++;
        }

        if (groups.Count == 0)
        {
            return;
        }

        var ordered = new List<KeyValuePair<string, Group>>(groups);
        ordered.Sort((left, right) => right.Value.Count.CompareTo(left.Value.Count));

        Plugin.Logger.LogInfo(
            $"props probe: {ordered.Count} unmapped object kind(s) of at least {UnmappedMinSize:0} m " +
            "(what the rules do not know about yet):");

        var shown = Mathf.Min(ordered.Count, UnmappedShown);
        for (var index = 0; index < shown; index++)
        {
            var pair = ordered[index];
            var sample = pair.Value.Sample;
            var parts = sample.GetComponentsInChildren<MeshRenderer>(true);
            Plugin.Logger.LogInfo(
                $"props probe:   [unmapped] {pair.Key} x{pair.Value.Count} — renderers {(parts == null ? 0 : parts.Length)}, " +
                $"bounds {Size(parts)}, at {Vector(sample.position)}, under {Path(sample.parent)}");
        }

        if (ordered.Count > shown)
        {
            Plugin.Logger.LogInfo($"props probe:   … {ordered.Count - shown} more unmapped kind(s)");
        }
    }

    /// <summary>
    /// A5.0: every map element the player can use derives from
    /// <c>BaseInteractable</c>. The register names each **kind** once, on the
    /// first frame it exists, so an element spawned later in the floor is still
    /// named. It only reads.
    ///
    /// **The scan is typed on purpose.** A generic
    /// <c>FindObjectsOfType&lt;BaseInteractable&gt;</c> hands back proxies typed
    /// as the *base* class, so <c>GetType()</c> printed "BaseInteractable" and
    /// the merchant's <c>rarity</c>/<c>meshRenderer</c> were never readable (the
    /// first live run on 0.9.131). A typed query wraps each object as its real
    /// class, which is what names the kind and reads its fields. The catch-all
    /// walk after it takes anything else by prefab name.
    /// </summary>
    private static void ScanInteractables()
    {
        if (MyTime.stageTimer < nextInteractableScan)
        {
            return;
        }

        nextInteractableScan = MyTime.stageTimer + InteractableScanSeconds;

        ScanTyped<InteractableShadyGuy>("merchant", e => e.rarity.ToString(), e => e.meshRenderer);
        ScanTyped<InteractableMicrowave>("microwave", e => e.rarity.ToString(), e => e.meshRenderer);
        ScanTyped<InteractableShrineCursed>("cursed shrine");
        ScanTyped<InteractableShrineChallenge>("challenge shrine");
        ScanTyped<InteractableShrineMagnet>("magnet shrine");
        ScanTyped<InteractableShrineBalance>("balance shrine");

        ScanOtherElements();
    }

    /// <summary>One concrete interactable class. <paramref name="variant"/>
    /// splits a prefab into kinds (the merchant's rarity colours share one
    /// prefab), <paramref name="body"/> names the renderer a swap would repaint.
    /// Both are optional.</summary>
    private static void ScanTyped<T>(string label, Func<T, string> variant = null, Func<T, Renderer> body = null)
        where T : BaseInteractable
    {
        var found = UnityEngine.Object.FindObjectsOfType<T>(true);
        if (found == null)
        {
            return;
        }

        for (var index = 0; index < found.Length; index++)
        {
            var element = found[index];
            if (element == null || !InteractableInstances.Add(element.GetInstanceID()))
            {
                continue;
            }

            string variantText = null;
            Renderer bodyRenderer = null;
            try
            {
                if (variant != null)
                {
                    variantText = variant(element);
                }

                if (body != null)
                {
                    bodyRenderer = body(element);
                }
            }
            catch (Exception)
            {
                // A field the interop refuses leaves the kind unsplit.
            }

            var key = variantText == null
                ? $"{typeof(T).Name}|{element.name}"
                : $"{typeof(T).Name}|{element.name}|{variantText}";
            if (Interactables.Add(key))
            {
                ReportElement(typeof(T).Name, label, variantText, bodyRenderer, element);
            }
        }
    }

    /// <summary>The catch-all: an interactable of a class the table above does
    /// not name yet is still listed by its prefab name, so a new element is
    /// discovered rather than missed. Its concrete type cannot be read through
    /// a base proxy, so the kind is the prefab name.</summary>
    private static void ScanOtherElements()
    {
        var found = UnityEngine.Object.FindObjectsOfType<BaseInteractable>(true);
        if (found == null)
        {
            return;
        }

        for (var index = 0; index < found.Length; index++)
        {
            var element = found[index];
            if (element == null || !InteractableInstances.Add(element.GetInstanceID()))
            {
                continue;
            }

            if (Interactables.Add($"other|{element.name}"))
            {
                ReportElement("interactable", null, null, null, element);
            }
        }
    }

    /// <summary>One register line: the kind, which renderer is its body, how
    /// much geometry it carries, where it sits and which prefab/parent it came
    /// from. **Body and union are both stated**, because a shrine's union is
    /// dominated by its 20 m encounter/charge zone and would size a swap ten
    /// times too big (the A3b lesson); the body box is the one a fit uses.</summary>
    private static void ReportElement(string type, string label, string variant, Renderer body, BaseInteractable sample)
    {
        var meshes = sample.GetComponentsInChildren<MeshRenderer>(true);
        var skinned = sample.GetComponentsInChildren<SkinnedMeshRenderer>(true);

        Bounds? union = null;
        var names = new List<string>(4);
        Accumulate(meshes, false, ref union, names);
        Accumulate(skinned, true, ref union, names);

        if (body == null)
        {
            body = GuessBody(meshes, skinned, sample.name);
        }

        var bodyBox = body == null ? null : RendererBox(body);
        var meshCount = meshes == null ? 0 : meshes.Length;
        var skinnedCount = skinned == null ? 0 : skinned.Length;

        Header();
        Plugin.Logger.LogInfo(
            $"a5 probe:   {type}" + (label == null ? string.Empty : $" ({label})") +
            $" — '{sample.name}'" +
            (variant == null ? string.Empty : $", rarity {variant}") +
            $", renderers {meshCount + skinnedCount} ({meshCount} mesh, {skinnedCount} skinned)" +
            (names.Count > 0 ? $" [{string.Join(", ", names)}]" : string.Empty) +
            $", body {(body == null ? "none" : $"'{body.name}' {(bodyBox.HasValue ? Size(bodyBox.Value) : "no box")}")}" +
            $", union {(union.HasValue ? Size(union.Value) : "none")}" +
            $", at {Vector(sample.transform.position)}, under {Path(sample.transform.parent)}");
    }

    private static void Header()
    {
        if (interactableHeader)
        {
            return;
        }

        interactableHeader = true;
        Plugin.Logger.LogInfo(
            "a5 probe: map-element register (every BaseInteractable the floor spawns, one line per kind, " +
            "logged the first time it appears):");
    }

    private static void Accumulate(Renderer[] parts, bool skinned, ref Bounds? union, List<string> names)
    {
        if (parts == null)
        {
            return;
        }

        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part == null)
            {
                continue;
            }

            var box = RendererBox(part);
            if (box.HasValue)
            {
                if (union == null)
                {
                    union = box.Value;
                }
                else
                {
                    var grown = union.Value;
                    grown.Encapsulate(box.Value);
                    union = grown;
                }
            }

            if (names.Count < 4)
            {
                names.Add($"{(skinned ? "skinned" : "mesh")} '{part.name}'");
            }
        }
    }

    /// <summary>The body of a prefab whose class does not name one: the renderer
    /// that carries the prefab's own name (every interactable draws its body
    /// under that name — `CursedShrine(Clone)`, `MagnetShrine(Clone)`), else the
    /// largest volume, which still skips an icon quad.</summary>
    private static Renderer GuessBody(MeshRenderer[] meshes, SkinnedMeshRenderer[] skinned, string name)
    {
        Renderer best = null;
        var bestVolume = 0f;

        for (var index = 0; index < 2; index++)
        {
            var parts = index == 0 ? (Renderer[])meshes : skinned;
            if (parts == null)
            {
                continue;
            }

            for (var part = 0; part < parts.Length; part++)
            {
                var renderer = parts[part];
                if (renderer == null)
                {
                    continue;
                }

                if (renderer.name == name)
                {
                    return renderer;
                }

                var box = RendererBox(renderer);
                var volume = box.HasValue ? box.Value.size.x * box.Value.size.y * box.Value.size.z : 0f;
                if (volume > bestVolume)
                {
                    bestVolume = volume;
                    best = renderer;
                }
            }
        }

        return best;
    }

    /// <summary>A renderer's world box, measured from its own mesh when the
    /// renderer has not been drawn yet — a world <c>Renderer.bounds</c> is zero
    /// until the first draw, which is the A3b fault (a shrine read as "not
    /// there" until the player entered its zone).</summary>
    private static Bounds? RendererBox(Renderer renderer)
    {
        var drawn = renderer.bounds;
        if (drawn.size.sqrMagnitude > 0f)
        {
            return drawn;
        }

        Mesh mesh = null;
        var filter = renderer.GetComponent<MeshFilter>();
        if (filter != null)
        {
            mesh = filter.sharedMesh;
        }

        if (mesh == null && renderer is SkinnedMeshRenderer skinned)
        {
            mesh = skinned.sharedMesh;
        }

        if (mesh == null)
        {
            return null;
        }

        var frame = renderer.transform;
        var local = mesh.bounds;
        var centre = frame.TransformPoint(local.center);
        var scale = frame.lossyScale;
        var size = new Vector3(
            local.size.x * Mathf.Abs(scale.x),
            local.size.y * Mathf.Abs(scale.y),
            local.size.z * Mathf.Abs(scale.z));
        return new Bounds(centre, size);
    }

    private static string Size(Bounds box) =>
        $"{box.size.x:0.##} x {box.size.y:0.##} x {box.size.z:0.##} m, base y {box.min.y:0.##}";

    private static string Row(RandomMapObject row)
    {
        if (row == null)
        {
            return "none";
        }

        var prefabs = row.prefabs;
        if (prefabs == null || prefabs.Length == 0)
        {
            return "no prefab";
        }

        var names = new List<string>(prefabs.Length);
        for (var index = 0; index < prefabs.Length; index++)
        {
            var prefab = prefabs[index];
            names.Add(prefab == null ? "null" : prefab.name);
        }

        return $"[{string.Join(", ", names)}]";
    }

    private static string Size(MeshRenderer[] parts)
    {
        if (parts == null || parts.Length == 0)
        {
            return "none";
        }

        var box = parts[0].bounds;
        for (var index = 1; index < parts.Length; index++)
        {
            box.Encapsulate(parts[index].bounds);
        }

        return $"{box.size.x:0.##} x {box.size.y:0.##} x {box.size.z:0.##} m, base y {box.min.y:0.##}";
    }

    private static string Vector(Vector3 value) => $"({value.x:0.#}, {value.y:0.#}, {value.z:0.#})";

    private static string Path(Transform node)
    {
        if (node == null)
        {
            return "<scene>";
        }

        var parts = new List<string>(8);
        var current = node;
        var depth = 0;
        while (current != null && depth++ < 8)
        {
            parts.Add(current.name);
            current = current.parent;
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    /// <summary>Our own child meshes (`WraeclastProp`, `WraeclastChest`,
    /// `WraeclastShrine`, `WraeclastModel`) are what a pass drew, not scenery:
    /// the 0.9.131 run listed `WraeclastShrine x26` as an unmapped kind. Every
    /// one of them carries this prefix.</summary>
    private static bool Ours(string name) =>
        name != null && name.StartsWith("Wraeclast", StringComparison.Ordinal);

    private static bool HasKeyword(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var lower = name.ToLowerInvariant();
        for (var index = 0; index < Keywords.Length; index++)
        {
            if (lower.Contains(Keywords[index]))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class Group
    {
        internal Group(Transform sample)
        {
            Sample = sample;
        }

        internal Transform Sample { get; }

        internal int Count;
    }
}
