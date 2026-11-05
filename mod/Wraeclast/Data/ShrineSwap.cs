using System;
using System.Collections.Generic;
using UnityEngine;

namespace Wraeclast.Data;

/// <summary>
/// A3b — the map's shrines wear the client's own totem.
///
/// Megabonk places two kinds through <c>RandomObjectPlacer.chargeShrineSpawns</c>
/// and <c>greedShrineSpawns</c> (15 + 8 per floor on the Forest stage): the
/// charge shrine the player stands in to fill, and the greed shrine that grants
/// a blessing. **Only what is drawn changes** — the blessing, the trigger, the
/// charge zone, the minimap and alert icons, the particles and the collider are
/// all Megabonk's, exactly like A2's scenery and A3a's chests.
///
/// The body renderer is the one the game itself draws the shrine from:
/// <c>ChargeShrine.meshRenderer</c> for the charge shrine (the root sphere is
/// the 25 m charge zone, not the body, so an oversized renderer falls back to
/// the largest child) and the root <c>MeshRenderer</c> for the greed shrine.
/// The client mesh is fitted inside that renderer's own world box and the greed
/// shrine keeps a gold tint, so the two kinds stay readable apart.
/// </summary>
internal static class ShrineSwap
{
    private const string ChildName = "WraeclastShrine";

    /// <summary>A3b: the charge shrine keeps the client's Azmeri bear-head totem;
    /// a golden charge shrine keeps the same model gold tinted (the operator:
    /// “on peut garder ce modèle doré pour les shrines dorés”).</summary>
    private const string ChargeModel = "poe-shrine-totem";

    /// <summary>The greed shrine is the Forest bandit camp's **stick-skull**, a
    /// skull impaled on a palisade stake — the operator's “crâne au bout d'un
    /// bâton”. Built from `treewitch_stickskull_v01_07.fmt`.</summary>
    private const string GreedModel = "greed-stickskull";

    /// <summary>A3c: the moai (`ShrineMaoi`, `InteractableShrineMoai`) is the
    /// map's monument — a stone head 8.44 m tall on a solid `BoxCollider`. The
    /// client's shrine models were measured first and rejected (they are
    /// sacrificial altars with a body and candles); the Act 3 battlefield
    /// marble statue is the monument read, and its 2.46 m figure fits the
    /// moai's own height almost exactly.</summary>
    private const string MoaiModel = "poe-statue-moai";

    /// <summary>A3b: the greed shrine's encounter ring is about 12 m across and
    /// drowned the 2.87 m pole. The skull-on-a-stake is drawn at least this tall
    /// (our design) so it reads as a shrine next to its own red effect.</summary>
    private const float GreedMinHeight = 6f;

    /// <summary>The charge shrine's root draws its 10 m charge zone as a 25 m
    /// sphere; anything that large is the zone, never the altar body.</summary>
    private const float MaxBodySpan = 12f;

    /// <summary>A3b design (ours, not Megabonk's): the charge shrine's body is a
    /// 1.36 m rune stone whose 0.54 m footprint shrank the totem to 0.6 m — the
    /// operator could not see it. The charge totem is drawn at least this tall,
    /// so it reads as an altar at the zone's centre; the zone and its collider
    /// are untouched, so nothing about the blessing changes. The greed shrine's
    /// own body is a 2.87 m altar and keeps its fitted height.</summary>
    private const float MinShrineHeight = 5f;

    private static readonly Color GoldTint = new Color(1f, 0.82f, 0.32f);

    internal static bool Active => Plugin.ReplaceShrines.Value;

    /// <summary>Logged once at load, like every pass that hangs off one hook: a
    /// floor where the hook never fires must not read like a working feature.</summary>
    internal static void Mount()
    {
        if (!Active)
        {
            Plugin.Logger.LogInfo("shrines: shrine pass off ([Props] ReplaceShrines), Megabonk's shrines stay");
            return;
        }

        Plugin.Logger.LogInfo(
            $"shrines: pass mounted on RandomObjectPlacer.GenerateInteractables (postfix) with a once-a-second " +
            $"sweep as a safety net; the charge shrine draws {ChargeModel} at the root (gold when the shrine is " +
            $"golden), the greed shrine draws {GreedModel} in its body box, the moai draws {MoaiModel} — " +
            "blessings, zones, icons and interaction stay Megabonk's");
    }

    private static float nextSweep;

    /// <summary>The safety net, from <c>MyPlayer.Update</c>: a shrine created
    /// after the spawn hook (a pooled reuse, a late activation) is caught within
    /// a second. Idempotent through the child marker.</summary>
    internal static void Tick()
    {
        if (!Active || Time.realtimeSinceStartup < nextSweep)
        {
            return;
        }

        nextSweep = Time.realtimeSinceStartup + 1f;
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
            var charges = UnityEngine.Object.FindObjectsOfType<ChargeShrine>(true);
            if (charges != null)
            {
                for (var index = 0; index < charges.Length; index++)
                {
                    ApplyCharge(charges[index]);
                }
            }

            var greeds = UnityEngine.Object.FindObjectsOfType<InteractableShrineGreed>(true);
            if (greeds != null)
            {
                for (var index = 0; index < greeds.Length; index++)
                {
                    ApplyGreed(greeds[index]);
                }
            }

            var moais = UnityEngine.Object.FindObjectsOfType<InteractableShrineMoai>(true);
            if (moais != null)
            {
                for (var index = 0; index < moais.Length; index++)
                {
                    ApplyMoai(moais[index]);
                }
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"shrines: the shrine pass failed: {error}");
        }
    }

    internal static bool ApplyCharge(ChargeShrine shrine)
    {
        if (shrine == null)
        {
            return false;
        }

        var node = shrine.transform;
        var body = shrine.meshRenderer;

        // Once the totem exists, the native rune stone that the zone activates
        // must not draw over it. `ChargeShrine.Start` calls this again, so the
        // rune stone is hidden the frame it appears.
        if (node.Find(ChildName) != null)
        {
            Hide(body);
            return false;
        }

        var tint = Color.white;
        try
        {
            if (shrine.isGolden)
            {
                tint = GoldTint;
            }
        }
        catch (Exception)
        {
            // A build without the property keeps the natural totem colour.
        }

        // The charge body (`ShrineEnergy`) is dormant until its zone is entered:
        // it has no mesh and no bounds before then, which is why 0.9.124 could
        // only swap on approach. A shrine is an altar at the zone's centre, so
        // the totem is placed at the root with a fixed height and the native
        // body is hidden when it does appear.
        var source = PoeCharacters.HostBodyMaterial;
        if (source == null && body != null)
        {
            source = body.sharedMaterial;
        }

        var placed = PlaceAtRoot(node, ChargeModel, tint, MinShrineHeight, source, "charge");
        if (placed)
        {
            Hide(body);
        }

        return placed;
    }

    /// <summary>The charge shrine's native body is dormant until its zone is
    /// entered; the `Start` hook lands the hide on that frame.</summary>
    private static void Hide(Renderer body)
    {
        if (body != null && body.enabled)
        {
            body.enabled = false;
        }
    }

    private static bool ApplyGreed(InteractableShrineGreed shrine)
    {
        if (shrine == null)
        {
            return false;
        }

        var node = shrine.transform;
        if (node.Find(ChildName) != null)
        {
            // The red encounter/fx objects are Megabonk's pre-activation beacon
            // now: `Interact` turns them on natively, so the pass keeps them on
            // until the interaction then turns them off (see AfterGreedInteract).
            ActivateGreedFx(shrine);
            return false;
        }

        Renderer body = node.GetComponent<MeshRenderer>();
        if (body == null)
        {
            body = BestBody(node);
        }

        // The greed body is already a 2.87 m altar mesh; the stick-skull is
        // lifted above it so the red encounter ring cannot hide it. The gold
        // totem is the charge shrine's look, not the greed shrine's.
        var placed = PlaceOn(node, body, GreedModel, Color.white, "greed", GreedMinHeight);
        if (placed)
        {
            ActivateGreedFx(shrine);
        }

        return placed;
    }

    /// <summary>
    /// The greed shrine's red effect is born after activation in Megabonk: its
    /// `Interact` turns `fx` and `fxLoop` on. The operator asked for the inverse
    /// — the red animation marks the shrine **before** it is used and disappears
    /// once used — so the pass turns both on at spawn and off on interaction.
    /// </summary>
    private static void ActivateGreedFx(InteractableShrineGreed shrine)
    {
        try
        {
            if (shrine.done)
            {
                return;
            }

            if (shrine.fx != null)
            {
                shrine.fx.SetActive(true);
            }

            if (shrine.fxLoop != null)
            {
                shrine.fxLoop.SetActive(true);
            }
        }
        catch (Exception)
        {
            // A prefab without the fields keeps its own effect state.
        }
    }

    /// <summary>Postfix on <c>InteractableShrineGreed.Interact</c>: the native
    /// method turns the two effects on, and the operator wants them gone once
    /// the shrine is used.</summary>
    internal static void AfterGreedInteract(InteractableShrineGreed shrine)
    {
        if (shrine == null)
        {
            return;
        }

        try
        {
            if (shrine.fx != null)
            {
                shrine.fx.SetActive(false);
            }

            if (shrine.fxLoop != null)
            {
                shrine.fxLoop.SetActive(false);
            }
        }
        catch (Exception)
        {
            // Same guard: a prefab without the fields is left alone.
        }
    }

    /// <summary>
    /// A3c: the moai is the floor's monument — the `ShrineMaoi` root draws one
    /// 8.44 m stone head with a solid `BoxCollider` and carries the
    /// `InteractableShrineMoai` script. The client marble statue is fitted
    /// inside that head's own box (it comes out 8.34 m tall, so the monument
    /// keeps Megabonk's size), the native renderer is hidden, and the solid
    /// collider follows the drawn statue so the player cannot walk through an
    /// invisible column above a shorter mesh.
    /// </summary>
    private static bool ApplyMoai(InteractableShrineMoai shrine)
    {
        if (shrine == null)
        {
            return false;
        }

        var node = shrine.transform;
        if (node.Find(ChildName) != null)
        {
            return false;
        }

        Renderer body = node.GetComponent<MeshRenderer>();
        if (body == null)
        {
            body = BestBody(node);
        }

        var placed = PlaceOn(node, body, MoaiModel, Color.white, "moai", 0f);
        if (placed)
        {
            FitStatueCollider(node);
        }

        return placed;
    }

    /// <summary>
    /// A solid collider belongs to the mesh it blocks: the moai's collider is
    /// the 8.44 m head, and the statue stands inside it, so the box is rewritten
    /// from the drawn child's own bounds (in the collider's local space). A
    /// trigger is left alone — that is the interaction volume, not a wall.
    /// </summary>
    private static void FitStatueCollider(Transform node)
    {
        var child = node != null ? node.Find(ChildName) : null;
        if (child == null)
        {
            return;
        }

        var filter = child.GetComponent<MeshFilter>();
        if (filter == null || filter.sharedMesh == null)
        {
            return;
        }

        var colliders = node.GetComponentsInChildren<BoxCollider>(true);
        if (colliders == null || colliders.Length == 0)
        {
            return;
        }

        var mesh = filter.sharedMesh.bounds;
        for (var index = 0; index < colliders.Length; index++)
        {
            var collider = colliders[index];
            if (collider == null || collider.isTrigger)
            {
                continue;
            }

            var matrix = collider.transform.worldToLocalMatrix * child.localToWorldMatrix;
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (var corner = 0; corner < 8; corner++)
            {
                var offset = new Vector3(
                    (corner & 1) == 0 ? -mesh.extents.x : mesh.extents.x,
                    (corner & 2) == 0 ? -mesh.extents.y : mesh.extents.y,
                    (corner & 4) == 0 ? -mesh.extents.z : mesh.extents.z);
                var local = matrix.MultiplyPoint3x4(mesh.center + offset);
                min = Vector3.Min(min, local);
                max = Vector3.Max(max, local);
            }

            var size = max - min;
            if (size.x <= 0.01f || size.y <= 0.01f || size.z <= 0.01f)
            {
                continue;
            }

            collider.center = (min + max) * 0.5f;
            collider.size = size;

            if (Logged.Add("moai collider"))
            {
                Plugin.Logger.LogInfo(
                    $"shrines: the moai's collider follows the drawn statue " +
                    $"({size.x:0.00} x {size.y:0.00} x {size.z:0.00} m in its local space)");
            }
        }
    }

    /// <summary>
    /// The largest renderer small enough to be a body: the charge shrine's root
    /// sphere is the zone, its energy mesh is the altar, and icon quads have no
    /// volume. Name and size both filter, so a re-parented prefab still answers.
    /// </summary>
    private static Renderer BestBody(Transform node)
    {
        var renderers = node.GetComponentsInChildren<Renderer>(true);
        if (renderers == null)
        {
            return null;
        }

        Renderer best = null;
        var bestVolume = 0f;
        Renderer altar = null;
        var altarVolume = 0f;
        for (var index = 0; index < renderers.Length; index++)
        {
            var renderer = renderers[index];
            if (renderer == null || Icon(renderer.transform))
            {
                continue;
            }

            var box = BodyBox(renderer);
            var size = box.size;
            var span = Span(box);
            if (span < 0.05f || span > MaxBodySpan)
            {
                continue;
            }

            var volume = size.x * size.y * size.z;
            if (volume > bestVolume)
            {
                bestVolume = volume;
                best = renderer;
            }

            // The charge shrine's body lives under `B_EnergyAltar`; prefer that
            // subtree over a same-sized lamp or rail when `meshRenderer` fell
            // back because it named the zone sphere.
            if (volume > altarVolume && Altar(renderer.transform))
            {
                altarVolume = volume;
                altar = renderer;
            }
        }

        return altar != null ? altar : best;
    }

    /// <summary>Is this renderer inside the shrine's altar/energy subtree?</summary>
    private static bool Altar(Transform node)
    {
        var current = node;
        var depth = 0;
        while (current != null && depth++ < 4)
        {
            var name = current.name.ToLowerInvariant();
            if (name.Contains("altar") || name.Contains("energy"))
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    /// <summary>An icon, a minimap billboard or a flat circle is not the altar:
    /// it is either a quad (no depth) or named like one.</summary>
    private static bool Icon(Transform node)
    {
        var current = node;
        var depth = 0;
        while (current != null && depth++ < 4)
        {
            var name = current.name.ToLowerInvariant();
            if (name.Contains("icon") || name.Contains("circle") || name.Contains("minimap") ||
                name.Contains("billboard") || name.Contains("particle") || name.Contains("fx"))
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    private static float Span(Bounds box) =>
        Mathf.Max(box.size.x, Mathf.Max(box.size.y, box.size.z));

    /// <summary>
    /// The body's world box from its mesh, not <see cref="Renderer.bounds"/>: a
    /// renderer the game has not drawn yet answers a zero box, which is why the
    /// charge shrine was only swapped once the player entered its zone (and then
    /// at a stale size). The bind-pose mesh box through the renderer's transform
    /// is valid before the first frame, and the transform carries the prefab's
    /// stand-up rotation.
    /// </summary>
    private static Bounds BodyBox(Renderer body)
    {
        Mesh mesh = null;
        if (body is SkinnedMeshRenderer skinned)
        {
            mesh = skinned.sharedMesh;
        }
        else
        {
            var filter = body.GetComponent<MeshFilter>();
            if (filter != null)
            {
                mesh = filter.sharedMesh;
            }
        }

        if (mesh == null)
        {
            return body.bounds;
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
            : body.bounds;
    }

    /// <summary>
    /// The swap itself: the client totem fitted inside the native body's own
    /// world box, at its pivot's yaw, its base on the box floor — the A3a rule
    /// unchanged, so a shrine cannot grow outside the shape the game placed.
    /// </summary>
    private static bool PlaceOn(Transform node, Renderer body, string modelId, Color tint, string label, float minHeight)
    {
        if (node == null || body == null || node.Find(ChildName) != null)
        {
            return false;
        }

        var box = BodyBox(body);
        if (box.size.x <= 0.01f || box.size.y <= 0.01f || box.size.z <= 0.01f)
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

        var fit = Mathf.Min(box.size.x / size.x, Mathf.Min(box.size.y / size.y, box.size.z / size.z));

        // A shrine is an altar, not a small prop: the charge shrine's rune stone
        // is only 1.36 m tall with a 0.54 m footprint, which shrank the totem to
        // 0.6 m (the operator could not see it). Give it a readable height; the
        // interaction is a trigger zone, so a larger totem changes no collider.
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

        // The charge shrine's rune stone floats above the altar, so its box floor
        // is not the ground; the prefab root is where the game placed the shrine.
        // The greed shrine's own body box floor is the lower (correct) one.
        var baseY = Mathf.Min(box.min.y, node.position.y);
        child.transform.position =
            new Vector3(box.center.x, baseY, box.center.z)
            - Vector3.up * (min.y * fit)
            - Vector3.up * Mathf.Max(Plugin.PropSink.Value, 0f);

        child.AddComponent<MeshFilter>().sharedMesh = model.Mesh;
        var renderer = child.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = Materials(body.sharedMaterial, model, tint);

        // Only the body goes: the charge zone, icons, particles, minimap and the
        // collider all keep their renderers and their behaviour.
        body.enabled = false;

        if (Logged.Add(label))
        {
            Plugin.Logger.LogInfo(
                $"shrines: the {label} shrine draws {modelId} on '{body.name}' inside a " +
                $"{box.size.x:0.00} x {box.size.y:0.00} x {box.size.z:0.00} m body box " +
                $"(scale {fit:0.00}{(tint == GoldTint ? ", gold tinted" : string.Empty)}" +
                $"{(lifted ? $", lifted to {minHeight:0.0} m" : string.Empty)}); " +
                "blessing, zone, icons and collider untouched");
        }

        return true;
    }

    /// <summary>
    /// The charge shrine's totem at the shrine root, with no body box: its
    /// `ShrineEnergy` body is dormant (meshless, zero bounds) until the player
    /// enters the zone, so 0.9.123/0.9.124 could only swap on approach. The
    /// shrine root is where the game placed the shrine, so the totem stands at
    /// the zone's centre from the floor's build; the native body is hidden when
    /// it appears (`ChargeShrine.Start` calls back into the pass).
    /// </summary>
    private static bool PlaceAtRoot(
        Transform node,
        string modelId,
        Color tint,
        float height,
        Material source,
        string label)
    {
        if (node == null || node.Find(ChildName) != null)
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
        if (size.y <= 0f)
        {
            return false;
        }

        var fit = height / size.y;

        var child = new GameObject(ChildName);
        child.transform.SetParent(node, false);

        var parentScale = node.lossyScale;
        child.transform.localScale = new Vector3(
            fit / Safe(parentScale.x),
            fit / Safe(parentScale.y),
            fit / Safe(parentScale.z));

        var facing = Quaternion.Euler(0f, node.eulerAngles.y, 0f);
        child.transform.rotation = facing * rotation;

        // The root is the game's placement anchor; one ray down finds the floor
        // under it (the zone collider belongs to the shrine and is skipped).
        var floor = PropSwap.GroundAt(node, node.position, height + 2f);
        child.transform.position =
            new Vector3(node.position.x, floor, node.position.z)
            - Vector3.up * (min.y * fit)
            - Vector3.up * Mathf.Max(Plugin.PropSink.Value, 0f);

        child.AddComponent<MeshFilter>().sharedMesh = model.Mesh;
        var renderer = child.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = Materials(source, model, tint);

        if (Logged.Add(label))
        {
            Plugin.Logger.LogInfo(
                $"shrines: the {label} shrine draws {modelId} at the root " +
                $"({height:0.0} m tall, source {(source != null ? source.name : "<unlit>")}" +
                $"{(tint == GoldTint ? ", gold tinted" : string.Empty)}); blessing, zone, icons and " +
                "collider untouched");
        }

        return true;
    }

    private static Material[] Materials(Renderer body, GltfSkin.StaticModel model, Color tint) =>
        Materials(body != null ? body.sharedMaterial : null, model, tint);

    /// <summary>One opaque material per submesh, cloned from the given source
    /// material (the frozen body-material path), our colour map on the albedo
    /// aliases, and a separate instance when a tint is asked for, because the
    /// clone is shared with every other use of that texture.</summary>
    private static Material[] Materials(Material source, GltfSkin.StaticModel model, Color tint)
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

            if (material != null && tint != Color.white)
            {
                material = Tint(material, tint, texture != null);
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

    private static readonly Dictionary<string, Material> Tinted = new Dictionary<string, Material>(StringComparer.Ordinal);

    private static Material Tint(Material baseMaterial, Color tint, bool keepAlbedo)
    {
        var key = baseMaterial.GetInstanceID() + "|" + tint.r + "," + tint.g + "," + tint.b + "|" + (keepAlbedo ? 1 : 0);
        if (Tinted.TryGetValue(key, out var cached) && cached != null)
        {
            return cached;
        }

        var material = new Material(baseMaterial) { name = baseMaterial.name + "Shrine" };
        if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", tint);
        }

        if (material.HasProperty("_AlbedoColor"))
        {
            material.SetColor("_AlbedoColor", tint);
        }

        if (!keepAlbedo && material.HasProperty("_AlbedoMapIntensity"))
        {
            material.SetFloat("_AlbedoMapIntensity", 0f);
        }

        Tinted[key] = material;
        return material;
    }

    /// <summary>0.9.116's rule: a cache keyed by Unity InstanceIDs is dropped on
    /// the run reset, because those IDs are reused after a scene reload.</summary>
    internal static void ClearCaches()
    {
        if (Tinted.Count > 0)
        {
            Tinted.Clear();
        }
    }

    /// <summary>What the A2c survey should say about a shrine object, since the
    /// shrine pass is not one of <see cref="PropSwap"/>'s name rules.</summary>
    internal static string Describe(Transform node)
    {
        if (node == null)
        {
            return null;
        }

        if (node.GetComponent<ChargeShrine>() != null)
        {
            return $"shrine charge ({ChargeModel}, gold when golden)";
        }

        if (node.GetComponent<InteractableShrineGreed>() != null)
        {
            return $"shrine greed ({GreedModel})";
        }

        if (node.GetComponent<InteractableShrineMoai>() != null)
        {
            return $"moai ({MoaiModel}, collider follows the statue)";
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
                $"shrines: {modelId} is not shipped, Megabonk's shrine body stays " +
                "(run mod/tools/prepare-data.ps1)");
        }
    }

    private static float Safe(float value) => Mathf.Abs(value) < 1e-4f ? 1e-4f : value;
}
