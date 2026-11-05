using System;
using System.Collections.Generic;
using UnityEngine;

namespace Wraeclast.Data;

/// <summary>
/// A5.2 — the microwave and the three shrine kinds A3b left out wear client art.
///
/// The A5.0 register named the elements no earlier pass covered: the
/// <c>Microwave</c> (the item-cooking device the placer rolls), and the
/// <c>CursedShrine</c>, <c>ChallengeShrine</c> and <c>MagnetShrine</c>. Its live
/// lines also named the one number a swap needs: each class's **body** box,
/// distinct from the 20 m encounter/charge zone its union reports.
///
/// The rule is the A3/A5 rule unchanged: **only what is drawn changes**. The
/// client mesh is fitted uniformly inside the body renderer's own box, its base
/// on that box's floor, at the body's yaw; the native body is disabled and the
/// icons, minimap/alert markers, effects, interaction volume and collider stay
/// Megabonk's. Idempotent on a <see cref="ChildName"/> child, so the spawn hooks
/// and the once-a-second net can walk the same floor.
/// </summary>
internal static class ElementSwap
{
    private const string ChildName = "WraeclastElement";

    /// <summary>A5.2: the microwave draws the client's Expedition plinth, a
    /// metal device with a socketed top — the "put an item in, take one out"
    /// read, one material.</summary>
    private const string MicrowaveModel = "poe-microwave";

    /// <summary>A5.2: the cursed shrine (the boss curse) draws the client's Vaal
    /// soul vessel — a sealed 2.2 m jar, the curse read.</summary>
    private const string CursedModel = "poe-cursed-shrine";

    /// <summary>A5.2: the challenge shrine draws the Labyrinth's Izaro pedestal
    /// (`labyrinth_shrine_crown`), the shrine a trial places its reward on.</summary>
    private const string ChallengeModel = "poe-challenge-shrine";

    /// <summary>A5.2: the magnet shrine draws the client's Act 10 Kitava horn
    /// arch — two horn arms rising from a flared base with the middle open, the
    /// native `MagnetShrine`'s own silhouette (a huge horseshoe magnet). The
    /// first pick, a low crystal cluster, had no relation to that shape (the
    /// operator).</summary>
    private const string MagnetModel = "poe-magnet-shrine";

    /// <summary>A body renderer larger than this is the shrine's encounter zone,
    /// never its altar (A3b's `chargeShrine` sphere and the 20 m unions the A5.0
    /// register reported).</summary>
    private const float MaxBodySpan = 12f;

    /// <summary>How often the net looks for an element a spawn hook missed.
    /// A microwave is spawned by a placer row and a shrine by the floor build,
    /// so a late or pooled instance is caught within a second.</summary>
    private const float SweepSeconds = 1f;

    /// <summary>Our design, not Megabonk's: a shrine's altar is small next to its
    /// 20 m encounter ring, so a model fitted to a 2–3 m body would read as a
    /// speck inside its own zone. These are the minimum drawn heights (the A3b
    /// `MinShrineHeight` rule). The microwave keeps its fitted size: its own
    /// body is the appliance.</summary>
    private const float MinShrineHeight = 5f;

    private static float nextSweep;

    internal static bool Active => Plugin.ReplaceElements.Value;

    /// <summary>Logged once at load, like every pass that hangs off one hook: a
    /// floor where the hook never fires must not read like a working feature.</summary>
    internal static void Mount()
    {
        if (!Active)
        {
            Plugin.Logger.LogInfo("elements: element pass off ([Props] ReplaceElements), Megabonk's elements stay");
            return;
        }

        Plugin.Logger.LogInfo(
            $"elements: pass mounted on RandomObjectPlacer.GenerateInteractables and " +
            $"SpawnInteractables.SpawnShrines (postfixes) plus InteractableMicrowave.Start, with a " +
            $"once-a-second sweep; the microwave draws {MicrowaveModel}, the cursed shrine {CursedModel}, " +
            $"the challenge shrine {ChallengeModel} and the magnet shrine {MagnetModel} — icons, effects, " +
            "encounter zones, interaction and colliders stay Megabonk's");
    }

    /// <summary>The safety net, from <c>MyPlayer.Update</c>: a shrine or microwave
    /// created after its spawn hook (a pooled reuse, a late activation) is caught
    /// within a second. Idempotent through the child marker.</summary>
    internal static void Tick()
    {
        if (!Active || Time.realtimeSinceStartup < nextSweep)
        {
            return;
        }

        nextSweep = Time.realtimeSinceStartup + SweepSeconds;
        ApplyAll();
    }

    internal static void ApplyAll()
    {
        if (!Active)
        {
            return;
        }

        try
        {
            var microwaves = UnityEngine.Object.FindObjectsOfType<InteractableMicrowave>(true);
            if (microwaves != null)
            {
                for (var index = 0; index < microwaves.Length; index++)
                {
                    ApplyMicrowave(microwaves[index]);
                }
            }

            var cursed = UnityEngine.Object.FindObjectsOfType<InteractableShrineCursed>(true);
            if (cursed != null)
            {
                for (var index = 0; index < cursed.Length; index++)
                {
                    ApplyCursed(cursed[index]);
                }
            }

            var challenges = UnityEngine.Object.FindObjectsOfType<InteractableShrineChallenge>(true);
            if (challenges != null)
            {
                for (var index = 0; index < challenges.Length; index++)
                {
                    ApplyChallenge(challenges[index]);
                }
            }

            var magnets = UnityEngine.Object.FindObjectsOfType<InteractableShrineMagnet>(true);
            if (magnets != null)
            {
                for (var index = 0; index < magnets.Length; index++)
                {
                    ApplyMagnet(magnets[index]);
                }
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"elements: the element pass failed: {error}");
        }
    }

    /// <summary>A5.2: the microwave is a placer row (`Microwave`, 1..2 per floor)
    /// whose body is a `SkinnedMeshRenderer` in `meshRenderer`. The component's
    /// own <c>Start</c> mounts it the frame it exists; the sweep is the net.</summary>
    internal static bool ApplyMicrowave(InteractableMicrowave microwave)
    {
        if (microwave == null)
        {
            return false;
        }

        try
        {
            var body = microwave.meshRenderer;
            if (body == null)
            {
                return false;
            }

            return Place(microwave.transform, body, MicrowaveModel, "microwave", 0f);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"elements: the microwave could not take the client model: {error}");
            return false;
        }
    }

    internal static bool ApplyCursed(InteractableShrineCursed shrine) =>
        PlaceShrine(shrine != null ? shrine.transform : null, "cursed", CursedModel);

    internal static bool ApplyChallenge(InteractableShrineChallenge shrine) =>
        PlaceShrine(shrine != null ? shrine.transform : null, "challenge", ChallengeModel);

    internal static bool ApplyMagnet(InteractableShrineMagnet shrine) =>
        PlaceShrine(shrine != null ? shrine.transform : null, "magnet", MagnetModel);

    /// <summary>The three shrines expose no body field on the interop, so the
    /// body is the renderer carrying the prefab's own name (`CursedShrine` under
    /// `CursedShrine(Clone)`), else the largest renderer small enough to be an
    /// altar — the A5.0 `GuessBody` rule, and the reason the 20 m zone never
    /// sizes them.</summary>
    private static bool PlaceShrine(Transform node, string label, string modelId)
    {
        if (node == null)
        {
            return false;
        }

        try
        {
            var body = Body(node);
            if (body == null)
            {
                return false;
            }

            return Place(node, body, modelId, $"{label} shrine", MinShrineHeight);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"elements: the {label} shrine could not take the client model: {error}");
            return false;
        }
    }

    private static Renderer Body(Transform node)
    {
        var renderers = node.GetComponentsInChildren<Renderer>(true);
        if (renderers == null)
        {
            return null;
        }

        Renderer best = null;
        var bestVolume = 0f;
        for (var index = 0; index < renderers.Length; index++)
        {
            var renderer = renderers[index];
            if (renderer == null || Icon(renderer.transform))
            {
                continue;
            }

            // The prefab's own name is the body: the register shows every kind
            // drawing its altar under that name and its icons under another.
            if (renderer.name == node.name)
            {
                return renderer;
            }

            var box = BodyBox(renderer);
            if (!box.HasValue || Span(box.Value) > MaxBodySpan)
            {
                continue;
            }

            var volume = box.Value.size.x * box.Value.size.y * box.Value.size.z;
            if (volume > bestVolume)
            {
                bestVolume = volume;
                best = renderer;
            }
        }

        return best;
    }

    /// <summary>
    /// The swap itself: the client model fitted **uniformly inside** the native
    /// body's own world box, at the body's yaw, its base on the box floor — the
    /// A3a/A3b rule, so an element cannot grow outside the shape the game placed.
    /// </summary>
    private static bool Place(Transform node, Renderer body, string modelId, string label, float minHeight)
    {
        if (node == null || body == null || node.Find(ChildName) != null)
        {
            return false;
        }

        var box = BodyBox(body);
        if (!box.HasValue)
        {
            return false;
        }

        var model = PoeAssets.GetProp(modelId);
        if (model == null || model.Mesh == null)
        {
            NoteMissing(modelId);
            return false;
        }

        var rotation = model.RootRotation;
        PropSwap.RotatedBounds(model.Mesh.bounds, rotation, out var min, out var max);
        var size = max - min;
        if (size.x <= 0f || size.y <= 0f || size.z <= 0f)
        {
            return false;
        }

        var fit = Mathf.Min(
            box.Value.size.x / size.x,
            Mathf.Min(box.Value.size.y / size.y, box.Value.size.z / size.z));

        var lifted = false;
        if (minHeight > 0f && size.y * fit < minHeight)
        {
            fit = minHeight / size.y;
            lifted = true;
        }

        var child = new GameObject(ChildName);
        child.transform.SetParent(node, false);

        var parentScale = node.lossyScale;
        child.transform.localScale = new Vector3(
            fit / Safe(parentScale.x),
            fit / Safe(parentScale.y),
            fit / Safe(parentScale.z));

        var frame = body.transform;
        var facing = Quaternion.Euler(0f, frame.rotation.eulerAngles.y, 0f);
        child.transform.rotation = facing * rotation;
        child.transform.position =
            new Vector3(box.Value.center.x, box.Value.min.y, box.Value.center.z)
            - Vector3.up * (min.y * fit)
            - Vector3.up * Mathf.Max(Plugin.PropSink.Value, 0f);

        child.AddComponent<MeshFilter>().sharedMesh = model.Mesh;
        var renderer = child.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = Materials(body.sharedMaterial, model);

        // Only the body goes: icons, effects, the encounter/charge zone, the
        // interaction volume and the collider all keep their renderers.
        body.enabled = false;

        if (Logged.Add(label))
        {
            Plugin.Logger.LogInfo(
                $"elements: the {label} draws {modelId} on '{body.name}' inside a " +
                $"{box.Value.size.x:0.00} x {box.Value.size.y:0.00} x {box.Value.size.z:0.00} m body box " +
                $"(scale {fit:0.00}{(lifted ? $", lifted to {minHeight:0.0} m" : string.Empty)}); " +
                "icons, effects, zone, interaction and collider untouched");
        }

        return true;
    }

    /// <summary>
    /// The body's world box. A <c>MeshRenderer</c> is measured from its mesh
    /// through its own transform, which is valid before the first draw (the A3b
    /// lesson: an undrawn renderer answers a zero box). A
    /// <c>SkinnedMeshRenderer</c> — the microwave's body — is **not**: its
    /// `sharedMesh` box is the bind pose, and at `Start` the A5.1 merchant
    /// answered a 7 × 1.72 × 7 m bind box instead of his drawn body. A skinned
    /// body therefore waits for the live `bounds` the first draw produces, and
    /// the once-a-second sweep mounts it then.
    /// </summary>
    private static Bounds? BodyBox(Renderer body)
    {
        if (body is SkinnedMeshRenderer)
        {
            var drawn = body.bounds;
            return drawn.size.x > 0.01f && drawn.size.y > 0.01f && drawn.size.z > 0.01f
                ? drawn
                : (Bounds?)null;
        }

        Mesh mesh = null;
        var filter = body.GetComponent<MeshFilter>();
        if (filter != null)
        {
            mesh = filter.sharedMesh;
        }

        if (mesh == null)
        {
            return null;
        }

        var local = mesh.bounds;
        var matrix = body.transform.localToWorldMatrix;
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (var corner = 0; corner < 8; corner++)
        {
            var offset = new Vector3(
                (corner & 1) == 0 ? -local.extents.x : local.extents.x,
                (corner & 2) == 0 ? -local.extents.y : local.extents.y,
                (corner & 4) == 0 ? -local.extents.z : local.extents.z);
            var world = matrix.MultiplyPoint3x4(local.center + offset);
            min = Vector3.Min(min, world);
            max = Vector3.Max(max, world);
        }

        var box = new Bounds((min + max) * 0.5f, max - min);
        return box.size.x > 0.01f && box.size.y > 0.01f && box.size.z > 0.01f
            ? box
            : (Bounds?)null;
    }

    /// <summary>An icon, minimap billboard or effect is not the body: it is named
    /// like one, or it is a flat card with no depth.</summary>
    private static bool Icon(Transform node)
    {
        var current = node;
        var depth = 0;
        while (current != null && depth++ < 4)
        {
            var name = current.name.ToLowerInvariant();
            if (name.Contains("icon") || name.Contains("minimap") || name.Contains("billboard") ||
                name.Contains("particle") || name.Contains("fx") || name.Contains("quad"))
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    private static float Span(Bounds box) =>
        Mathf.Max(box.size.x, Mathf.Max(box.size.y, box.size.z));

    /// <summary>One opaque material per submesh, cloned from the native body's
    /// material (the frozen body-material path), the client colour map on the
    /// albedo aliases. A submesh whose client material has no map is painted
    /// black on purpose, like the chests and the shrines.</summary>
    private static Material[] Materials(Material source, GltfSkin.StaticModel model)
    {
        var submeshes = Mathf.Max(1, model.Mesh.subMeshCount);
        var materials = new Material[submeshes];
        for (var index = 0; index < submeshes; index++)
        {
            var texture = model.Textures != null && index < model.Textures.Length ? model.Textures[index] : null;
            var cut = model.AlphaCut != null && index < model.AlphaCut.Length && model.AlphaCut[index];
            var material = PoeCharacters.BuildMaterial(source, texture, cut);
            if (material == null)
            {
                material = source;
            }

            if (texture == null && material != null)
            {
                if (material.HasProperty("_Color"))
                {
                    material.SetColor("_Color", Color.black);
                }

                if (material.HasProperty("_AlbedoColor"))
                {
                    material.SetColor("_AlbedoColor", Color.black);
                }
            }

            materials[index] = material;
        }

        return materials;
    }

    /// <summary>What the A2c survey should say about an A5.2 element, since the
    /// element pass is not one of <see cref="PropSwap"/>'s name rules.</summary>
    internal static string Describe(Transform node)
    {
        if (node == null)
        {
            return null;
        }

        // The survey's sample can be a child of the element (the register showed
        // the microwave's component under `Microwave(Clone)/Microwave`), so the
        // lookup walks up from the sampled transform.
        if (node.GetComponentInParent<InteractableMicrowave>() != null)
        {
            return $"microwave ({MicrowaveModel})";
        }

        if (node.GetComponentInParent<InteractableShrineCursed>() != null)
        {
            return $"shrine cursed ({CursedModel})";
        }

        if (node.GetComponentInParent<InteractableShrineChallenge>() != null)
        {
            return $"shrine challenge ({ChallengeModel})";
        }

        if (node.GetComponentInParent<InteractableShrineMagnet>() != null)
        {
            return $"shrine magnet ({MagnetModel})";
        }

        return null;
    }

    private static readonly HashSet<string> Logged = new HashSet<string>(StringComparer.Ordinal);
    private static readonly HashSet<string> Missing = new HashSet<string>(StringComparer.Ordinal);

    private static void NoteMissing(string modelId)
    {
        if (Missing.Add(modelId))
        {
            Plugin.Logger.LogWarning(
                $"elements: {modelId} is not shipped, Megabonk's element body stays " +
                "(run mod/tools/prepare-data.ps1)");
        }
    }

    private static float Safe(float value) => Mathf.Abs(value) < 1e-4f ? 1e-4f : value;
}
