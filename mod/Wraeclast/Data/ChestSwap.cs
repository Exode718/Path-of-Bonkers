using System;
using System.Collections.Generic;
using Assets.Scripts.Inventory__Items__Pickups.Chests;
using Assets.Scripts.Inventory__Items__Pickups.Interactables;
using Assets.Scripts.Managers;
using UnityEngine;

namespace Wraeclast.Data;

/// <summary>
/// A3a — the map's chests wear the client's own chest meshes.
///
/// <c>SpawnInteractables.SpawnChests</c> instantiates Megabonk's two chest
/// prefabs (<c>chest</c> and <c>chestFree</c>): one root <c>MeshRenderer</c> on
/// the built-in chest mesh, a minimap-icon quad child, and a sphere collider.
/// This pass runs on that method's postfix and draws a client chest over the
/// native renderer of every chest the floor spawned, with the A2 discipline:
/// the mesh is fitted **inside** the native renderer's own box, the chest keeps
/// its pivot, its yaw and its collider, and only what is drawn changes. The
/// minimap icon keeps its own renderer.
///
/// The mapping is by <see cref="EChest"/>, not by prefab name: the paid chest
/// draws the town chest the operator asked for — Lioneye's Watch's stash chest,
/// wood and metal — and the free — **gold** — chest draws a distinct golden
/// model (its own albedo is 70 % gold texels, which is why the operator calls
/// those "the golden chests"). The corrupt and ghost chests keep Megabonk's
/// model until their own exports land.
/// </summary>
internal static class ChestSwap
{
    /// <summary>Child carrying our mesh, so a second pass cannot stack a chest.</summary>
    private const string ChildName = "WraeclastChest";

    /// <summary>The paid (normal) map chest: the client's `chest31`
    /// (`Art/Models/Chests/Chests/chest31.sm`), the model the operator named.
    /// Also what an elite drop draws.</summary>
    private const string NormalModel = "chest-normal";


    /// <summary>Mesh a billboard icon is drawn with; it is never the chest body.</summary>
    private const string QuadMesh = "Quad";

    internal static bool Active => Plugin.ReplaceChests.Value;

    /// <summary>Logged at load: a pass reachable from one hook must say it is
    /// mounted, or a floor where the hook never fires reads like a feature.</summary>
    internal static void Mount()
    {
        if (!Active)
        {
            Plugin.Logger.LogInfo("chests: pass off ([Props] ReplaceChests), Megabonk's own chests stay");
            return;
        }

        Plugin.Logger.LogInfo(
            $"chests: pass mounted on SpawnInteractables.SpawnChests (postfix) and on InteractableChest.Start " +
            $"(a chest an elite kill drops or a wave leaves behind) — every mapped chest draws " +
            $"{NormalModel} (the client's chest31), the gold ones with a gold tint, and the opening shot " +
            "draws the same mesh; the ghost chests keep Megabonk's model (colliders, pivots and spawn " +
            "rules untouched, overlaid on the host's own chest box)");
    }

    /// <summary>
    /// 0.9.115: drop the caches keyed by Unity **InstanceIDs** at the run reset.
    /// Unity reuses those IDs after a scene reload, so a chest of a restarted run
    /// could be handed a tinted clone (or an opening model/scale) built for the
    /// previous run, whose texture was already destroyed — the black chests the
    /// operator saw after a retry. Nothing is destroyed here, so a renderer that
    /// still draws an entry keeps it; the new run builds from live objects.
    /// </summary>
    internal static void ClearCaches()
    {
        Tinted.Clear();
        OpeningWanted.Clear();
        OpeningBaseScale.Clear();
    }

    /// <summary>
    /// A3a: the chests that did not exist when the floor was built — an elite or
    /// a boss drops one, a wave leaves one behind, a powerup turns into one.
    /// Those never passed `SpawnChests`, and a chest the host creates while
    /// inactive never runs `Start` either, so both hooks can miss it (the
    /// operator's dropped chests still wore Megabonk's texture). Once a second
    /// every live chest is looked at, and the ones without our child are swapped
    /// like the floor's (gold ones tinted, so a dropped "powerup" chest keeps its
    /// yellow read).
    /// </summary>
    internal static void Tick()
    {
        if (!Active)
        {
            return;
        }

        var now = Time.time;
        if (now < nextSweep)
        {
            return;
        }

        nextSweep = now + 1f;
        Probe();

        var swapped = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var chests = UnityEngine.Object.FindObjectsOfType<InteractableChest>();
            if (chests == null)
            {
                return;
            }

            if (sweepStage != MapController.GetStageIndex())
            {
                sweepStage = MapController.GetStageIndex();
                floorPassed = false;
                ProbeLogged.Clear();
                Plugin.Logger.LogInfo($"chests: the sweep sees {chests.Length} chest(s) on the floor");
            }

            // Anything still drawn with a **host chest material** is a chest, no
            // matter which class owns the object: the dropped one kept Megabonk's
            // mesh through three different hooks, so the material is the reliable
            // signal (`m_chest`, `m_chestFree`, `m_chestFreeCrypt`, `m_evilChest`,
            // `m_chest_ghost` — all contain "chest").
            SweepHostChests();

            // The host's dropped chest is an `OpenChest`, and its `Awake` can run
            // before it has a renderer to measure — the sweep retries it here.
            var opens = UnityEngine.Object.FindObjectsOfType<OpenChest>();
            if (opens != null)
            {
                for (var index = 0; index < opens.Length; index++)
                {
                    ApplyOpenChest(opens[index]);
                }
            }

            for (var index = 0; index < chests.Length; index++)
            {
                var chest = chests[index];
                if (chest == null || chest.transform.Find(ChildName) != null)
                {
                    continue;
                }

                // Once the floor's own pass has run, anything the sweep still
                // finds without our child is a drop: gold, like the host's.
                var type = chest.chestType;
                var tint = floorPassed ? GoldTint : TintFor(type);
                if (Place(chest, NormalModel, tint, swapped) && LateLogged.Add(type))
                {
                    Plugin.Logger.LogInfo(
                        $"chests: a {type} chest was not in the floor pass — the sweep swapped it " +
                        $"({chest.name} under {ParentName(chest.transform)}), it draws {NormalModel}" +
                        (tint == GoldTint ? " with the gold tint (a drop)" : string.Empty));
                }
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"chests: the sweep failed: {error.Message}");
        }
    }

    private static float nextSweep;
    private static int sweepStage = int.MinValue;
    private static float nextProbe;
    private static readonly HashSet<string> ProbeLogged = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// A3a: what else in the scene looks like a chest. The operator still sees
    /// Megabonk's model on a dropped chest, and the sweep sees no new
    /// `InteractableChest`, so something else is drawing it. Every 10 s this
    /// names each object whose name contains “chest” and that is **not** an
    /// `InteractableChest`, once per floor, with its parent path and renderer
    /// count — that is the line that will say what the drop actually is.
    /// </summary>
    private static void Probe()
    {
        var now = Time.time;
        if (now < nextProbe)
        {
            return;
        }

        nextProbe = now + 10f;
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<Transform>();
            if (all == null)
            {
                return;
            }

            for (var index = 0; index < all.Length; index++)
            {
                var node = all[index];
                if (node == null || node.name.IndexOf("chest", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (node.GetComponentInParent<InteractableChest>() != null ||
                    node.Find(ChildName) != null || node.name == ChildName || !ProbeLogged.Add(node.name))
                {
                    continue;
                }

                var renderers = node.GetComponentsInChildren<MeshRenderer>(true);
                Plugin.Logger.LogInfo(
                    $"chests probe: '{node.name}' is not an InteractableChest (renderers " +
                    $"{(renderers == null ? 0 : renderers.Length)}) under {ParentName(node)}");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"chests probe failed: {error.Message}");
        }
    }

    /// <summary>Set once the floor's own `SpawnChests` pass has run: a chest the
    /// sweep meets after that is a drop and is drawn gold.</summary>
    private static bool floorPassed;

    private static string ParentName(Transform node) =>
        node != null && node.parent != null && node.parent.parent != null ? node.parent.parent.name : "<scene>";

    /// <summary>
    /// A3a: the chest an elite kill leaves on the ground is an <c>OpenChest</c>,
    /// not an <c>InteractableChest</c> — the 0.9.105 probe named it
    /// (`'OpenChest(Clone)' is not an InteractableChest (renderers 1)`), which is
    /// why the sweep and the spawn pass both walked past it and it kept
    /// Megabonk's mesh. It draws the same client chest, **gold tinted** (a drop),
    /// fitted into its own box exactly like the map's chests.
    /// </summary>
    internal static void ApplyOpenChest(OpenChest chest)
    {
        if (!Active || chest == null)
        {
            return;
        }

        try
        {
            if (chest.transform.Find(ChildName) != null)
            {
                return;
            }

            if (PlaceOn(chest.transform, NormalModel, GoldTint, "OpenChest", null))
            {
                if (LateLogged.Add(chest.chestType))
                {
                    Plugin.Logger.LogInfo(
                        $"chests: an OpenChest drop ({chest.chestType}) draws {NormalModel} with the gold tint");
                }

                return;
            }

            // A drop that could not be placed must say why: an `Awake` that runs
            // before the object has a renderer or a size would otherwise look
            // exactly like "the hook never fired".
            if (MissLogged.Add("OpenChest"))
            {
                var parts = chest.GetComponentsInChildren<Renderer>(true);
                var described = new List<string>();
                if (parts != null)
                {
                    for (var index = 0; index < parts.Length; index++)
                    {
                        var part = parts[index];
                        var mesh = part != null ? MeshOf(part) : null;
                        described.Add(
                            $"{(part == null ? "<null>" : part.GetType().Name + " '" + part.name + "'")}" +
                            $" mesh {(mesh == null ? "<none>" : mesh.name)} " +
                            $"bounds {(part == null ? "<none>" : part.bounds.size.ToString())} " +
                            $"material {(part == null || part.sharedMaterial == null ? "<none>" : part.sharedMaterial.name)}");
                    }
                }

                Plugin.Logger.LogWarning(
                    $"chests: an OpenChest could not take {NormalModel} ({(parts == null ? 0 : parts.Length)} " +
                    $"renderer(s): {string.Join("; ", described)}); the sweep retries every second");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"chests: the OpenChest swap failed: {error.Message}");
        }
    }

    private static readonly HashSet<string> MissLogged = new HashSet<string>(StringComparer.Ordinal);

    private static readonly HashSet<string> HostLogged = new HashSet<string>(StringComparer.Ordinal);

    private static float nextHostSweep;

    private static readonly HashSet<string> ProbeAtLogged = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// A3a diagnostic: what appears at a kill. The dropped chest has resisted
    /// four different guesses, so this lists every renderer within 3 m of the
    /// death point, once per object name and capped, with its type, mesh and
    /// material — the object is named by position instead of by a keyword.
    /// </summary>
    internal static void ProbeAt(Vector3 point)
    {
        if (!Active || ProbeAtLogged.Count > 40)
        {
            return;
        }

        try
        {
            var colliders = Physics.OverlapSphere(point, 3f, ~0, QueryTriggerInteraction.Collide);
            if (colliders == null)
            {
                return;
            }

            for (var index = 0; index < colliders.Length; index++)
            {
                var collider = colliders[index];
                if (collider == null || !ProbeAtLogged.Add(collider.name))
                {
                    continue;
                }

                var node = collider.transform;
                var parts = node.GetComponentsInChildren<Renderer>(true);
                var first = parts != null && parts.Length > 0 ? parts[0] : null;
                var mesh = first != null ? MeshOf(first) : null;
                Plugin.Logger.LogInfo(
                    $"chests probe at a kill: '{node.name}' under {ParentName(node)} " +
                    $"({(parts == null ? 0 : parts.Length)} renderer(s), " +
                    $"first {(first == null ? "<none>" : first.GetType().Name)}, " +
                    $"mesh {(mesh == null ? "<none>" : mesh.name)}, " +
                    $"material {(first == null || first.sharedMaterial == null ? "<none>" : first.sharedMaterial.name)})");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"chests probe at a kill failed: {error.Message}");
        }
    }

    /// <summary>
    /// The catch-all: every active `MeshRenderer` still carrying one of the
    /// host's chest materials gets the client chest. Three hooks were not enough
    /// (the dropped chest kept Megabonk's mesh), because the class that owns it
    /// is not one we know — but the material it draws with is always one of
    /// `m_chest*`. A renderer we already swapped carries our own material and a
    /// `WraeclastChest` child, so it is skipped; the size guard keeps a UI icon
    /// (a 16×16 `psd_Chest` sprite) out.
    /// </summary>
    private static void SweepHostChests()
    {
        // A full renderer scan is the heaviest thing in the sweep, so it runs on
        // its own slower clock; a drop that stays unswapped for three seconds is
        // the worst case.
        var now = Time.time;
        if (now < nextHostSweep)
        {
            return;
        }

        nextHostSweep = now + 3f;

        var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
        if (renderers == null)
        {
            return;
        }

        for (var index = 0; index < renderers.Length; index++)
        {
            var renderer = renderers[index];
            if (renderer == null || renderer.sharedMaterial == null)
            {
                continue;
            }

            var material = renderer.sharedMaterial.name;
            if (string.IsNullOrEmpty(material) ||
                material.IndexOf("chest", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var node = renderer.transform;
            if (node.Find(ChildName) != null || node.GetComponentInParent<InteractableChest>() != null)
            {
                continue;
            }

            if (renderer.bounds.size.magnitude < 0.5f)
            {
                continue;
            }

            if (PlaceOn(node, NormalModel, GoldTint, "host " + material, null) && HostLogged.Add(material))
            {
                Plugin.Logger.LogInfo(
                    $"chests: '{node.name}' under {ParentName(node)} was drawing the host's {material} — " +
                    $"it now draws {NormalModel} with the gold tint");
            }
        }
    }

    /// <summary>One chest, from its own <c>Start</c>: the safety net behind the
    /// spawn pass, for a chest the game activates after <c>SpawnChests</c> has
    /// returned. A prefab template is inactive (an inactive object never runs
    /// <c>Start</c>), so a clone cannot inherit our child mesh.</summary>
    internal static void Apply(InteractableChest chest)
    {
        if (!Active || chest == null)
        {
            return;
        }

        try
        {
            // A chest that reaches this hook was not on the floor when the spawn
            // pass ran: the game dropped it (an elite kill) or a wave activated
            // it. A drop reads **gold** whatever its type — the elite's chest
            // comes in as `Normal`, which is why 0.9.103 still showed a plain
            // wooden one — and the line below says what was swapped.
            var type = chest.chestType;
            if (Place(chest, NormalModel, GoldTint, null) && LateLogged.Add(type))
            {
                Plugin.Logger.LogInfo(
                    $"chests: a {type} chest spawned after the floor pass (an elite drop or a wave) and " +
                    $"draws {NormalModel} with the gold tint");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"chests: the swap failed on {chest?.chestType}: {error}");
        }
    }

    private static readonly HashSet<EChest> LateLogged = new HashSet<EChest>();

    /// <summary>Every chest the floor spawned, once per <c>SpawnChests</c> call.
    /// <c>FindObjectsOfType</c> skips the inactive prefab template, so only the
    /// placed chests are touched.</summary>
    internal static void ApplyAll()
    {
        if (!Active)
        {
            return;
        }

        var started = Time.realtimeSinceStartup;
        var swapped = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var chests = UnityEngine.Object.FindObjectsOfType<InteractableChest>();
            if (chests == null)
            {
                return;
            }

            for (var index = 0; index < chests.Length; index++)
            {
                var chest = chests[index];
                Place(chest, ModelFor(chest.chestType), TintFor(chest.chestType), swapped);
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"chests: the swap pass failed: {error}");
        }

        if (swapped.Count == 0)
        {
            return;
        }

        var counts = new List<string>(swapped.Count);
        foreach (var pair in swapped)
        {
            counts.Add($"{pair.Key} {pair.Value}");
        }

        counts.Sort(StringComparer.Ordinal);
        Plugin.Logger.LogInfo(
            $"chests: {string.Join(", ", counts)} " +
            $"(took {(Time.realtimeSinceStartup - started) * 1000f:0} ms)");
        floorPassed = true;
    }

    private static bool Place(InteractableChest chest, string modelId, Color? tint, Dictionary<string, int> swapped)
    {
        if (chest == null)
        {
            return false;
        }

        var type = chest.chestType;
        if (modelId == null || tint == null)
        {
            NoteKept(type);
            return false;
        }

        return PlaceOn(chest.transform, modelId, tint.Value, type.ToString(), swapped);
    }

    /// <summary>
    /// The swap itself, on any object that draws a chest: the map's
    /// <c>InteractableChest</c> and the host's dropped <c>OpenChest</c> (an elite
    /// kill leaves one, and it is not an InteractableChest — the 0.9.105 probe
    /// named it: `'OpenChest(Clone)' is not an InteractableChest (renderers 1)`).
    /// </summary>
    private static bool PlaceOn(Transform node, string modelId, Color tint, string label, Dictionary<string, int> swapped)
    {
        if (node == null || node.Find(ChildName) != null)
        {
            return false;
        }

        // A null material is not a reason to skip: an inactive instance may not
        // have it yet, and `ChestMaterial` falls back to the host body material
        // (or unlit) when the source is missing.
        var body = Body(node);
        if (body == null)
        {
            return false;
        }

        var box = body.bounds;
        if (box.size.x <= 0.01f || box.size.y <= 0.01f || box.size.z <= 0.01f)
        {
            return false;
        }

        var model = PoeAssets.GetProp(modelId);
        if (model == null || model.Mesh == null)
        {
            return false;
        }

        var rotation = model.RootRotation;
        PropSwap.RotatedBounds(model.Mesh.bounds, rotation, out var min, out var max);
        var size = max - min;
        if (size.x <= 0f || size.y <= 0f || size.z <= 0f)
        {
            return false;
        }

        // Inside the native box on every axis, proportions kept: a chest is a
        // chest, so it is fitted like the trees, not stretched like a rock (A2j).
        var fit = Mathf.Min(box.size.x / size.x, Mathf.Min(box.size.y / size.y, box.size.z / size.z));

        var child = new GameObject(ChildName);
        child.transform.SetParent(node, false);

        var parentScale = node.lossyScale;
        child.transform.localScale = new Vector3(
            fit / Safe(parentScale.x),
            fit / Safe(parentScale.y),
            fit / Safe(parentScale.z));

        // Upright, on the game's own facing. The prefab's root rotation is a
        // baked stand-up flip (Unity Euler `(0, 90, 180)` on `sharedassets2`
        // `Chest`/`ChestFree`, because the native mesh is authored Z-up), so its
        // **yaw is the chest's facing** and its pitch/roll are not: 0.9.95 threw
        // the whole rotation away and every chest faced one arbitrary way (the
        // operator saw their plain backs — "manque de texture" — and read them as
        // "encore plus inclinés"). The yaw is applied, the client mesh's own root
        // rotation stands it up, and nothing tilts it.
        var frame = body.transform;
        var facing = Quaternion.Euler(0f, frame.rotation.eulerAngles.y, 0f);
        child.transform.rotation = facing * rotation;

        // The chest sits exactly where the host draws its own: inside the native
        // renderer's box, centred on it, base on its floor. 0.9.95–0.9.98 tried
        // to be clever here — a terrain ray under the pivot, then the highest of
        // five samples — and both read on screen as a chest that floats on the
        // downhill side ("le côté droit s'enfonce bien plus que le côté gauche").
        // The host already placed its chest correctly, so its own box is the
        // answer: overlay it and change nothing about where it stands.
        child.transform.position =
            new Vector3(box.center.x, box.min.y, box.center.z)
            - Vector3.up * (min.y * fit)
            - Vector3.up * Mathf.Max(Plugin.PropSink.Value, 0f);

        child.AddComponent<MeshFilter>().sharedMesh = model.Mesh;
        var renderer = child.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = Materials(body, model, tint);

        // Only the chest body goes: the minimap icon child keeps its renderer.
        // Its material is stamped with ours as well, so the host-material sweep
        // recognises an already-swapped chest instead of adding a second child.
        body.enabled = false;
        if (renderer.sharedMaterials.Length > 0 && renderer.sharedMaterials[0] != null)
        {
            body.sharedMaterial = renderer.sharedMaterials[0];
        }

        NotePlaced(label, modelId, box, fit, parentScale);

        if (swapped != null)
        {
            swapped.TryGetValue(modelId, out var count);
            swapped[modelId] = count + 1;
        }

        return true;
    }

    /// <summary>
    /// A3a: the opening shot. `ChestOpening` feeds its own `SkinnedMeshRenderer`
    /// one of three meshes (`meshNormal` / `meshFree` / `meshEvil`) and the
    /// matching material, so the animation always showed Megabonk's chest. The
    /// pass runs after `SetRender` has picked one and swaps in the client chest:
    /// the mesh, one material per submesh off the renderer's own material (so
    /// the host shader and the shot's lighting stay), and a uniform scale that
    /// fits our chest into the box the game's own chest filled, which keeps the
    /// camera framing unchanged.
    ///
    /// The opening mesh is the **Y-up** build (`chest-normal-open`): the game's
    /// renderer has no root rotation of its own to stand a Z-up mesh up.
    /// </summary>
    internal static void ApplyOpening(ChestOpening opening, EChest type)
    {
        if (!Active || opening == null)
        {
            return;
        }

        var id = OpeningModelFor(type);
        if (id == null)
        {
            return;
        }

        try
        {
            // Remembered even when the renderer is not reachable yet: the
            // `Update` pass retries, which is what makes the shot keep the model
            // when the host does `SetRender` before binding its own renderer.
            var tint = TintFor(type) ?? Color.white;
            OpeningWanted[opening.GetInstanceID()] = (id, tint);
            SwapOpening(opening, id, tint);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"chests: the opening chest could not take the client model: {error.Message}");
        }
    }

    /// <summary>The retry behind <see cref="ApplyOpening"/>, from the component's
    /// own `Update`: cheap (one dictionary lookup, one mesh compare) and it is
    /// what puts the client chest back if the host re-assigns its own mesh.</summary>
    internal static void EnsureOpening(ChestOpening opening)
    {
        if (!Active || opening == null)
        {
            return;
        }

        if (!OpeningWanted.TryGetValue(opening.GetInstanceID(), out var wanted))
        {
            return;
        }

        var (id, tint) = wanted;
        var renderer = RendererOf(opening);
        var model = PoeAssets.GetProp(id);
        if (renderer == null || model == null || model.Mesh == null)
        {
            return;
        }

        // Only act while the shot is on screen; the mesh is ours when it is.
        if (renderer.sharedMesh == model.Mesh)
        {
            return;
        }

        try
        {
            SwapOpening(opening, id, tint);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"chests: the opening retro-fit failed: {error.Message}");
        }
    }

    private static readonly Dictionary<int, (string Id, Color Tint)> OpeningWanted =
        new Dictionary<int, (string, Color)>();

    /// <summary>The renderer the opening draws with. The serialized field is the
    /// normal one; the fallback covers a component whose field is still null
    /// when `SetRender` runs.</summary>
    private static SkinnedMeshRenderer RendererOf(ChestOpening opening)
    {
        var renderer = opening.chestRenderer;
        if (renderer != null)
        {
            return renderer;
        }

        return opening.GetComponentInChildren<SkinnedMeshRenderer>(true);
    }

    private static void SwapOpening(ChestOpening opening, string id, Color tint)
    {
        var renderer = RendererOf(opening);
        var model = PoeAssets.GetProp(id);
        if (renderer == null || model == null || model.Mesh == null)
        {
            if (OpeningLogged.Add("miss:" + id))
            {
                Plugin.Logger.LogWarning(
                    $"chests: the opening shot could not take {id} (renderer " +
                    $"{(renderer == null ? "<none>" : renderer.name)}, model " +
                    $"{(model == null ? "<not shipped>" : "ok")})");
            }

            return;
        }

        var target = model.Mesh;
        var native = renderer.sharedMesh;
        var nativeSize = native != null ? native.bounds.size : target.bounds.size;
        var ourSize = target.bounds.size;

        // Fit inside the box the host's chest filled, so the chest reads at the
        // same size in the shot. The first native mesh seen is the reference:
        // a later call must not shrink it against our own mesh.
        if (!OpeningFactor.TryGetValue(id, out var factor))
        {
            factor = Mathf.Min(
                nativeSize.x / Safe(ourSize.x),
                Mathf.Min(nativeSize.y / Safe(ourSize.y), nativeSize.z / Safe(ourSize.z)));
            OpeningFactor[id] = factor;
        }

        var source = renderer.sharedMaterial;
        renderer.sharedMesh = target;

        var submeshes = Mathf.Max(1, target.subMeshCount);
        var materials = new Material[submeshes];
        for (var index = 0; index < submeshes; index++)
        {
            var texture = model.Textures != null && index < model.Textures.Length ? model.Textures[index] : null;
            materials[index] = ChestMaterial(source, texture, tint) ?? source;
        }

        renderer.sharedMaterials = materials;
        renderer.localBounds = new Bounds(target.bounds.center, target.bounds.size * 1.5f);

        var key = renderer.GetInstanceID();
        if (!OpeningBaseScale.TryGetValue(key, out var baseScale))
        {
            baseScale = renderer.transform.localScale;
            OpeningBaseScale[key] = baseScale;
        }

        renderer.transform.localScale = baseScale * factor;

        if (OpeningLogged.Add(id))
        {
            Plugin.Logger.LogInfo(
                $"chests: the opening shot draws {id} at scale {factor:0.00} " +
                $"(the host's chest box is {nativeSize.x:0.00} x {nativeSize.y:0.00} x {nativeSize.z:0.00} m)");
        }
    }

    private static readonly Dictionary<string, float> OpeningFactor = new Dictionary<string, float>(StringComparer.Ordinal);

    private static readonly Dictionary<int, Vector3> OpeningBaseScale = new Dictionary<int, Vector3>();
    private static readonly HashSet<string> OpeningLogged = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>What the opening shot draws for a chest kind: the same client
    /// chest as the world, in its Y-up build, with the same tint.</summary>
    private static string OpeningModelFor(EChest type) => TintFor(type) != null ? NormalModel + "-open" : null;

    /// <summary>
    /// One opaque material per submesh, cloned from the native chest's own
    /// material so the host shader, ramp and outline stay (the same clone the
    /// bodies and props use), with the client chest's colour map on the albedo
    /// aliases.
    ///
    /// **No alpha cut**: a chest is a solid object, and going through the
    /// scenery pass's cut-card heuristic put `_AlphaClipping` 1 + a 0.35 cutoff
    /// and `_RenderFace` 0 on it — the log said `poe-stash submesh 0 is a cut
    /// card` for a wooden box. A chest atlas has empty corners, not leaf cards.
    /// A submesh whose client material has no colour map is the client's own
    /// black surface, so it is tinted out instead of drawn with the chest's
    /// atlas.
    /// </summary>
    private static Material[] Materials(Renderer native, GltfSkin.StaticModel model, Color tint)
    {
        var submeshes = Mathf.Max(1, model.Mesh.subMeshCount);
        var materials = new Material[submeshes];
        for (var index = 0; index < submeshes; index++)
        {
            var texture = model.Textures != null && index < model.Textures.Length ? model.Textures[index] : null;
            materials[index] = ChestMaterial(native.sharedMaterial, texture, tint) ?? native.sharedMaterial;
        }

        return materials;
    }

    /// <summary>
    /// One chest material: the body-path clone (host shader, ramp and outline
    /// kept, our texture on the albedo aliases), plus an optional tint. A tinted
    /// chest gets its **own** material instance, because the clone is shared
    /// with every other chest of that texture.
    /// </summary>
    private static Material ChestMaterial(Material source, Texture2D texture, Color tint)
    {
        var baseMaterial = PoeCharacters.BuildMaterial(source, texture, false);
        if (baseMaterial == null)
        {
            return null;
        }

        if (tint == Color.white)
        {
            if (texture == null)
            {
                return Tint(baseMaterial, Color.black, false);
            }

            return baseMaterial;
        }

        return Tint(baseMaterial, tint, texture != null);
    }

    private static readonly Dictionary<string, Material> Tinted = new Dictionary<string, Material>(StringComparer.Ordinal);

    /// <summary>A tinted copy of a chest material, cached per (material, tint) so
    /// a floor of gold chests shares one instance.</summary>
    private static Material Tint(Material baseMaterial, Color tint, bool keepAlbedo)
    {
        var key = baseMaterial.GetInstanceID() + "|" + tint.r + "," + tint.g + "," + tint.b + "|" + (keepAlbedo ? 1 : 0);
        if (Tinted.TryGetValue(key, out var cached) && cached != null)
        {
            return cached;
        }

        Material material;
        try
        {
            material = new Material(baseMaterial) { name = baseMaterial.name + "Gold" };
        }
        catch (Exception)
        {
            return baseMaterial;
        }

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

    /// <summary>
    /// The chest's own body renderer. Several host chests do **not** draw it
    /// from the prefab root: `OpenChest` is a root with an `Animator` and two
    /// children — `Cube` (the chest) and `MinimapIcon` (a billboard) — which is
    /// why 0.9.109 logged `an OpenChest could not take chest-normal (renderers
    /// 1, body <none>)` and the dropped chest kept the host's mesh. So a child
    /// renderer is accepted, the billboard is excluded by its mesh name, and a
    /// null material no longer disqualifies a candidate (an inactive instance has
    /// no bounds yet, and the material may arrive with `Start`).
    /// </summary>
    /// <summary>
    /// A chest's body renderer — any `Renderer`, not just `MeshRenderer`. The
    /// dropped chest is not a `MeshRenderer` at all (0.9.111 logged
    /// `renderers 1, body <none>`: the only `MeshRenderer` it found was the
    /// `MinimapIcon` `Quad`), so a `SkinnedMeshRenderer` — the host's rigged chest
    /// is one — has to be accepted the same way.
    /// </summary>
    private static Renderer Body(Transform node)
    {
        var own = node.GetComponent<Renderer>();
        if (own != null && !IsBillboard(own))
        {
            return own;
        }

        var parts = node.GetComponentsInChildren<Renderer>(true);
        if (parts == null)
        {
            return null;
        }

        Renderer best = null;
        var bestVolume = -1f;
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part == null || IsBillboard(part))
            {
                continue;
            }

            // `Renderer.bounds` is zero on an inactive instance; the mesh's own
            // box through the transform is the next best measure.
            var volume = Volume(part.bounds.size);
            if (volume <= 0f)
            {
                var mesh = MeshOf(part);
                if (mesh != null)
                {
                    var scale = part.transform.lossyScale;
                    volume = Volume(mesh.bounds.size) * Mathf.Abs(scale.x * scale.y * scale.z);
                }
            }

            if (volume > bestVolume)
            {
                bestVolume = volume;
                best = part;
            }
        }

        return best;
    }

    private static Mesh MeshOf(Renderer part)
    {
        if (part is SkinnedMeshRenderer skinned)
        {
            return skinned.sharedMesh;
        }

        var filter = part.GetComponent<MeshFilter>();
        return filter != null ? filter.sharedMesh : null;
    }

    private static bool IsBillboard(Renderer part)
    {
        var mesh = MeshOf(part);
        return mesh != null && string.Equals(mesh.name, QuadMesh, StringComparison.Ordinal);
    }

    /// <summary>What the **floor's** chests draw. Every mapped kind draws the
    /// same client chest; the gold ones are the same mesh **tinted** (the
    /// operator's "on peut pas juste recolorer le coffre en doré ?"). A chest
    /// that appears later (an elite drop, a wave) always draws the untinted
    /// normal chest — see <see cref="Apply"/>.</summary>
    private static string ModelFor(EChest type) => TintFor(type) != null ? NormalModel : null;

    /// <summary>The tint a chest kind wears: white (the plain client texture) for
    /// the normal chest, a gold tint for the host's free/corrupt ones, and null
    /// for a kind we do not map (the ghost chest).</summary>
    private static Color? TintFor(EChest type) => type switch
    {
        EChest.Normal => Color.white,
        EChest.Free => GoldTint,
        EChest.FreeCrypt => GoldTint,
        EChest.Corrupt => GoldTint,
        _ => null,
    };

    /// <summary>Gold, as the host's own gold chest reads: warm and bright but
    /// still shaded by the client texture's grain.</summary>
    private static readonly Color GoldTint = new Color(1f, 0.82f, 0.32f);

    private static readonly HashSet<EChest> KeptLogged = new HashSet<EChest>();

    /// <summary>A chest kind without a client model keeps Megabonk's own mesh;
    /// named once so the gap is in the log instead of a guess.</summary>
    private static void NoteKept(EChest type)
    {
        if (KeptLogged.Add(type))
        {
            Plugin.Logger.LogInfo(
                $"chests: {type} keeps Megabonk's own chest model (no client export for it yet)");
        }
    }

    private static readonly HashSet<string> PlacedLogged = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>One line per chest kind, with the numbers a wrong fit would be
    /// read from: the native box, the fit and the scale it applied.</summary>
    private static void NotePlaced(string label, string modelId, Bounds box, float fit, Vector3 parentScale)
    {
        if (!PlacedLogged.Add(label))
        {
            return;
        }

        // The parent's own scale is printed because a non-uniform one shears any
        // rotated child: it is the one number that would explain a chest that
        // still leans after the footprint grounding.
        Plugin.Logger.LogInfo(
            $"chests: {label} draws {modelId} inside a {box.size.x:0.00} x {box.size.y:0.00} x " +
            $"{box.size.z:0.00} m native box (scale {fit:0.00}, base y {box.min.y:0.00}, " +
            $"parent scale {parentScale.x:0.00}/{parentScale.y:0.00}/{parentScale.z:0.00})");
    }

    private static float Volume(Vector3 size) => Mathf.Max(size.x, 0f) * Mathf.Max(size.y, 0f) * Mathf.Max(size.z, 0f);

    private static float Safe(float value) => Mathf.Abs(value) < 1e-4f ? 1e-4f : value;
}
