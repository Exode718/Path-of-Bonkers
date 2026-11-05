using System;
using System.Collections.Generic;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Managers;
using Assets.Scripts.Utility;
using UnityEngine;

namespace Wraeclast.Data;

/// <summary>
/// A2 — the stage's random scenery wears the client's own prop meshes.
///
/// Megabonk's map builder places its scenery from
/// <c>StageData.randomMapObjects</c>: <c>RandomObjectPlacer.Generate</c> walks
/// the rows (trees, rocks, bushes, logs, boulders, each with an amount, a scale
/// range and a prefab list) and instantiates one prefab per roll. This pass runs
/// after that, finds the placed objects by their prefab name, and draws a PoE
/// prop over each one.
///
/// **Nothing but what is drawn changes.** The native renderer is disabled and a
/// child object carries our mesh, so the GameObject, its transform (pivot, the
/// placer's random scale and yaw, its alignment to the terrain normal), its
/// collider and the spawn rule stay Megabonk's. The PoE mesh is fitted **inside**
/// the native renderer's own world box (the smallest of the three axis ratios),
/// which is the rule the checklist states: a prop that cannot keep the native
/// footprint is fitted to the footprint, never the height.
///
/// The mapping is by prefab name, first match wins, and it is a table on
/// purpose: the client has no equivalent for every Megabonk prop, and a row
/// with an empty list records what was decided instead of silently guessing.
/// </summary>
internal static class PropSwap
{
    /// <summary>Name of the child that carries our mesh, so a second pass over
    /// the same object (a relayout, a reload) cannot stack two props.</summary>
    private const string ChildName = "WraeclastProp";

    /// <summary>How many transforms one pass may survey. A Forest floor has a
    /// few hundred props among thousands of tile objects; the budget stops a
    /// pathological hierarchy from stalling the load.</summary>
    private const int NodeBudget = 20000;

    /// <summary>When the once-per-floor sweep runs, in stage seconds. The trees,
    /// bushes and rocks of a Forest floor are placed by the map generator itself
    /// (not by `RandomObjectSpawner`, whose rows are only the pots and the
    /// microwave), so the sweep is what mounts them — one fixed 3 s pass read as
    /// “the base textures, then the switch 3 s later”. 0.9.118 passes it three
    /// times instead: the first as early as the floor allows, the later ones as
    /// the safety net for anything the generator creates a moment later. Each
    /// pass is idempotent and cheap (~40 ms measured), and one that finds nothing
    /// is silent.</summary>
    private static readonly float[] SweepAtSeconds = { 0.5f, 1.5f, 3f };

    /// <summary>A collider whose footprint reaches this share of the renderer's
    /// is treated as the prop's real body (a rock); below it, it is a trunk
    /// inside a canopy and only centres the mesh.</summary>
    private const float HalfFootprint = 0.5f;

    /// <summary>Largest object the pass will treat as scenery, in world metres.
    /// It is a sanity guard against map furniture that happens to share a word
    /// with a rule (a world edge, a collider box), not a prop size limit: the
    /// landmark `Tree` is 52 m tall and was being refused at 30 m, which is why
    /// one Megabonk tree was still standing in the Forest (operator's
    /// screenshot, 2026-11-01). Its height is handled by
    /// `[Props] PropMaxHeight`, not by this guard.</summary>
    private const float MaxPropSize = 80f;

    /// <summary>Names already reported as too large, so the warning is one line
    /// per kind instead of one per instance.</summary>
    private static readonly HashSet<string> Rejected = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Prefab name (lower case substring) to the PoE props it draws,
    /// in rotation so a stand of trees is not one clone repeated.</summary>
    private static readonly PropRule[] Rules =
    {
        // "deadtree" is checked before "tree": its name contains both.
        new PropRule("deadtree", new[] { "forest-tree-dead" }),
        new PropRule("tree", new[] { "forest-tree-1", "forest-tree-2", "forest-tree-3" }),
        // A2k (operator, 2026-11-01: "toujours pas bon les rochers"). The client's
        // rock mesh (`kaomrock01`) is a tall slab — no fit, uniform or stretched,
        // turns it into a boulder, and the operator read the result as a menhir
        // next to the player. Megabonk's own rock IS a boulder and its collider is
        // already exact, so this family keeps the native mesh and takes only the
        // client's grey albedo: the hitbox cannot disagree with a mesh it already
        // owns.
        new PropRule("rock", Array.Empty<string>(), retexture: RockAlbedoId),
        new PropRule("boulder", Array.Empty<string>(), retexture: RockAlbedoId),
        // A2l (operator, 2026-11-01: "faudrait faire pareil pour les tours et
        // petite ruine… le reste on peut remplacer normalement comme les arbres").
        // Megabonk's brick blocks are its own ruin set, and their shape (a stack
        // of blocks, a wall slab) is the shape the collider describes; swapping in
        // the client's `fruinswall04` mesh would repeat the rock story. They keep
        // the native mesh and take the client's mossy stone albedo instead.
        new PropRule("wall", Array.Empty<string>(), retexture: RuinAlbedoId),
        new PropRule("tower", Array.Empty<string>(), retexture: RuinAlbedoId),
        new PropRule("pillar", Array.Empty<string>(), retexture: RuinAlbedoId),
        new PropRule("ruin", Array.Empty<string>(), retexture: RuinAlbedoId),
        // A2n: the `[unmapped]` block of 0.9.86 named the rest of the set. The
        // brick towers the operator pointed at are Megabonk's **Castle** family
        // (`Castle`, `Castle (1)`, `CastleLying`), the big stone blocks are
        // **Cube**, and the boxes are **Crate** — none of which any earlier rule
        // could see. Same treatment: native mesh, native collider, client albedo.
        new PropRule("castle", Array.Empty<string>(), retexture: RuinAlbedoId),
        new PropRule("cube", Array.Empty<string>(), retexture: RuinAlbedoId),
        new PropRule("crate", Array.Empty<string>(), retexture: CrateAlbedoId),
        // A2o: the two biggest remaining families — 201 bushes and 40 logs per
        // floor — are Megabonk's own models too. Their shape is a bush and a log;
        // they only need the client's foliage and bark.
        new PropRule("bush", Array.Empty<string>(), retexture: BushAlbedoId),
        new PropRule("log", Array.Empty<string>(), retexture: CrateAlbedoId),
        // A3 pots: the map's breakables (`PotSmall`, `PotBig` and the silver
        // pair, all spawned from the placer's pot rows). The client's encampment
        // ceramic pots replace the mesh, stretched to the native pot's own body
        // box (0.75 × 1.79 / 1.50 × 2.16) so the size is Megabonk's. Their
        // `SphereCollider` is the interaction trigger and the flat `Quad`
        // children are shadow/billboard cards: neither is the body, so neither
        // sizes the pot nor is switched off.
        new PropRule(
            "pot",
            new[] { "poe-pot-1", "poe-pot-2", "poe-pot-3" },
            stretch: true,
            fitColliders: false,
            ignoreBillboards: true,
            groundOnBox: true),
        // Everything else is still a mesh swap, like the trees.
        new PropRule("statue", new[] { "forest-statue" }),

        // The only row still deliberately native: a Forest floor has never shown
        // a torch prefab.
        new PropRule("torch", Array.Empty<string>()),
    };

    private sealed class PropRule
    {
        internal PropRule(
            string match,
            string[] props,
            bool stretch = false,
            string retexture = null,
            bool fitColliders = true,
            bool ignoreBillboards = false,
            bool groundOnBox = false)
        {
            Match = match;
            Props = props;
            Stretch = stretch;
            Retexture = retexture;
            FitColliders = fitColliders;
            IgnoreBillboards = ignoreBillboards;
            GroundOnBox = groundOnBox;
        }

        internal string Match { get; }

        internal string[] Props { get; }

        /// <summary>A2j: fit the mesh to the box on all three axes instead of
        /// keeping its proportions.</summary>
        internal bool Stretch { get; }

        /// <summary>A2k: keep Megabonk's own mesh (and collider) and only bind
        /// this terrain albedo over it.</summary>
        internal string Retexture { get; }

        /// <summary>A3 pots: keep the native collider as it is. A pot's
        /// `SphereCollider` is the interaction trigger, not a wall, so shrinking
        /// it to the drawn mesh would shrink the volume the player can interact
        /// in (the moai's A3c rule: a trigger is the interaction volume). A rule
        /// with this off also does not use the collider as a fit target.</summary>
        internal bool FitColliders { get; }

        /// <summary>A3 pots: the flat `Quad` children of a pot prefab (a ground
        /// shadow and an interact billboard) are not the body. They are left
        /// enabled and out of the fit, or a 6–9 m card would size the pot
        /// (0.9.128: every pot came out gigantic).</summary>
        internal bool IgnoreBillboards { get; }

        /// <summary>A3 pots: stand the mesh on the native renderer box's floor
        /// instead of a ground ray. Megabonk's pot meshes start **below** their
        /// pivot (-0.14 m small, -0.18 m big), so a base placed on the terrain
        /// stands that much taller than the pot it replaces — the operator's
        /// “les grands pots sont trop grand en hauteur” (0.9.129).</summary>
        internal bool GroundOnBox { get; }

        /// <summary>Rotation index, so one rule's props alternate.</summary>
        internal int Next;
    }

    internal static bool Active => Plugin.ReplaceProps.Value;

    /// <summary>
    /// Logged once at load. A pass that is reachable from one hook only must
    /// state that it is mounted: a floor where the hook never fires would
    /// otherwise read exactly like a working feature in the log (A2c).
    /// </summary>
    internal static void Mount()
    {
        if (!Active)
        {
            Plugin.Logger.LogInfo("props: scenery pass off ([Props] ReplaceProps), Megabonk's scenery stays");
            return;
        }

        Plugin.Logger.LogInfo(
            "props: scenery pass mounted on RandomObjectPlacer.RandomObjectSpawner (postfix, one row at " +
            "a time) with a once-per-floor sweep as a safety net; it only draws, the native collider, " +
            "pivot and spawn rule stay" +
            (Plugin.PropProbe.Value ? "; A2c discovery survey armed (one per floor)" : string.Empty));
    }

    /// <summary>What the rules do with a prefab name, for the A2c survey: the
    /// client props it draws, <c>native</c> for a row deliberately kept on
    /// Megabonk's model, or <c>unmapped</c> when no rule claims the name.</summary>
    internal static string Describe(string name)
    {
        var rule = RuleFor(name);
        if (rule == null)
        {
            return "unmapped";
        }

        if (rule.Retexture != null)
        {
            return $"retexture {rule.Retexture} (native mesh)";
        }

        return rule.Props.Length == 0 ? "native" : string.Join("/", rule.Props);
    }

    /// <summary>
    /// Runs from <c>RandomObjectPlacer.RandomObjectSpawner</c>'s postfix, ie
    /// after one row of scenery has been instantiated (A2c: this is the path
    /// <c>MapGenerationController.GenerateMap</c> really uses, once per row;
    /// <c>Generate</c> never runs on a floor). The pass is idempotent — an
    /// object that already carries <see cref="ChildName"/> is skipped — so the
    /// successive rows can walk the same subtree. Silent unless it swapped
    /// something: the A2c survey is what names the prefabs that matched nothing.
    /// </summary>
    internal static void Apply(RandomObjectPlacer placer)
    {
        if (!Active || placer == null)
        {
            return;
        }

        var swapped = new Dictionary<string, int>(StringComparer.Ordinal);
        var surveyed = new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            // The placer usually parents what it spawns under itself, so its own
            // subtree is surveyed first; a placer that hangs its props on the
            // object above it is covered by the second sweep.
            var visited = 0;
            var matched = Walk(placer.transform, swapped, surveyed, ref visited);
            if (matched == 0)
            {
                visited = 0;
                var above = placer.transform.parent;
                matched = Walk(above != null ? above : placer.transform.root, swapped, surveyed, ref visited);
            }

            // 0.9.117: on a Forest floor the placer owns no scenery at all — the
            // A2c probe prints `children 0` for it while every tree, bush and rock
            // is a child of the **scene root** (`under <scene>`), so the two walks
            // above found nothing and the whole floor waited for the 3 s safety
            // sweep. That is the "it re-applies everything a few seconds after a
            // restart" the operator reported; the row now falls back to the
            // scene's own roots so the props are mounted as the floor builds.
            if (matched == 0)
            {
                matched = WalkSceneRoots(placer, swapped, surveyed);
            }

            if (matched > 0)
            {
                Report(placer, matched, swapped, surveyed);
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"props: the scenery pass failed: {error}");
        }
        finally
        {
            foreach (var rule in Rules)
            {
                rule.Next = 0;
            }
        }
    }

    private static int sweepStage = -1;
    private static float sweepClock;
    private static int sweepCursor;

    /// <summary>
    /// Walks every root GameObject of the placer's scene (0.9.117). The Forest's
    /// scenery is parented straight to the scene, not to the placer, so this is
    /// the walk that actually reaches it from the per-row hook.
    /// </summary>
    private static int WalkSceneRoots(
        RandomObjectPlacer placer,
        Dictionary<string, int> swapped,
        Dictionary<string, int> surveyed)
    {
        var matched = 0;
        try
        {
            var scene = placer.gameObject.scene;
            if (!scene.IsValid())
            {
                return 0;
            }

            var roots = scene.GetRootGameObjects();
            if (roots == null)
            {
                return 0;
            }

            for (var index = 0; index < roots.Length; index++)
            {
                if (roots[index] == null)
                {
                    continue;
                }

                var visited = 0;
                matched += Walk(roots[index].transform, swapped, surveyed, ref visited);
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"props: the row's scene walk failed: {error.Message}");
        }

        return matched;
    }

    /// <summary>
    /// The whole-scene pass, run at the times in <see cref="SweepAtSeconds"/>.
    /// It exists because the row hook only sees what a `RandomObjectSpawner` row
    /// spawns, and the Forest's trees, bushes and rocks are placed by the map
    /// generator itself — without this pass they keep Megabonk's mesh. It is
    /// idempotent, so a later pass finds everything already swapped and stays
    /// silent.
    /// </summary>
    internal static void Tick()
    {
        if (!Active)
        {
            return;
        }

        try
        {
            var index = MapController.GetStageIndex();
            if (index != sweepStage || MyTime.stageTimer + 1f < sweepClock)
            {
                sweepStage = index;
                sweepCursor = 0;
            }

            sweepClock = MyTime.stageTimer;
            while (sweepCursor < SweepAtSeconds.Length && MyTime.stageTimer >= SweepAtSeconds[sweepCursor])
            {
                sweepCursor++;
                Sweep();
            }
        }
        catch (Exception error)
        {
            sweepCursor = SweepAtSeconds.Length;
            Plugin.Logger.LogError($"props: the floor sweep failed: {error}");
        }
    }

    private static void Sweep()
    {
        var started = Time.realtimeSinceStartup;
        var all = UnityEngine.Object.FindObjectsOfType<Transform>();
        if (all == null)
        {
            return;
        }

        var swapped = new Dictionary<string, int>(StringComparer.Ordinal);
        var surveyed = new Dictionary<string, int>(StringComparer.Ordinal);
        var matched = 0;

        try
        {
            for (var index = 0; index < all.Length; index++)
            {
                var node = all[index];
                if (node == null || RuleFor(node.name) == null || Nested(node))
                {
                    continue;
                }

                if (Place(node, swapped, surveyed))
                {
                    matched++;
                }
            }
        }
        finally
        {
            foreach (var rule in Rules)
            {
                rule.Next = 0;
            }
        }

        if (matched > 0)
        {
            Report(null, matched, swapped, surveyed, (Time.realtimeSinceStartup - started) * 1000f);
        }
    }

    /// <summary>An object nested inside another object the rules already claim
    /// (a tree inside a tree group) is left to the ancestor: one prop per
    /// placed prefab, never a stack of them.</summary>
    private static bool Nested(Transform node)
    {
        var current = node.parent;
        var depth = 0;
        while (current != null && depth++ < 8)
        {
            if (RuleFor(current.name) != null)
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    private static int Walk(
        Transform parent,
        Dictionary<string, int> swapped,
        Dictionary<string, int> surveyed,
        ref int visited)
    {
        if (parent == null || visited > NodeBudget)
        {
            return 0;
        }

        var matched = 0;
        for (var index = 0; index < parent.childCount && visited <= NodeBudget; index++)
        {
            var child = parent.GetChild(index);
            if (child == null || string.Equals(child.name, ChildName, StringComparison.Ordinal))
            {
                continue;
            }

            visited++;
            if (Place(child, swapped, surveyed))
            {
                matched++;
            }

            matched += Walk(child, swapped, surveyed, ref visited);
        }

        return matched;
    }

    /// <summary>One placed object: pick its prop, fit and mount it, and hide the
    /// native renderer. Answers false when the object is not a prop we map.</summary>
    private static bool Place(
        Transform node,
        Dictionary<string, int> swapped,
        Dictionary<string, int> surveyed)
    {
        if (node.Find(ChildName) != null)
        {
            return false;
        }

        var rule = RuleFor(node.name);
        if (rule == null)
        {
            return false;
        }

        surveyed.TryGetValue(rule.Match, out var seen);
        surveyed[rule.Match] = seen + 1;
        // A retextured family keeps Megabonk's own mesh — and therefore its own
        // collider — and only takes the client's albedo (A2k).
        if (rule.Retexture != null)
        {
            return Retexture(node, rule.Retexture, swapped);
        }

        if (rule.Props.Length == 0)
        {
            return false;
        }

        // A native prop may draw itself with several renderers (a trunk and its
        // leaves): the fit must cover the whole object, and every part has to go
        // once ours is drawn, or Megabonk's parts show through it. A pot is the
        // exception: its prefab also carries flat `Quad` children (a ground
        // shadow and an interact billboard, 6–9 m across) that are not geometry —
        // fitting to them made every pot gigantic (0.9.128, operator: “ils sont
        // gigantesque par rapport au model initial”).
        var parts = rule.IgnoreBillboards ? BodyParts(node) : node.GetComponentsInChildren<MeshRenderer>(true);
        if (parts == null || parts.Length == 0)
        {
            return false;
        }

        var native = parts[0];
        if (native == null || native.sharedMaterial == null)
        {
            return false;
        }

        var box = native.bounds;
        for (var index = 1; index < parts.Length; index++)
        {
            if (parts[index] != null && parts[index] != native)
            {
                box.Encapsulate(parts[index].bounds);
            }
        }

        // A scenery prop is a handful of metres. Anything larger is map furniture
        // (the world edge, the collider box) whose name happens to share a word
        // with a rule, and one prop fitted over it would cover half the floor.
        // It is named once in the log so the rule can be narrowed.
        var span = box.size;
        if (Mathf.Max(span.x, Mathf.Max(span.y, span.z)) > MaxPropSize)
        {
            if (Rejected.Add(node.name))
            {
                Plugin.Logger.LogWarning(
                    $"props: {node.name} spans {span.x:0} x {span.y:0} x {span.z:0} m, " +
                    $"left native (over the {MaxPropSize:0} m sanity guard — map furniture, not a prop)");
            }

            return false;
        }

        // Rotate the rotation index per rule so a stand of trees varies.
        var id = rule.Props[rule.Next++ % rule.Props.Length];
        var model = PoeAssets.GetProp(id);
        if (model == null || model.Mesh == null)
        {
            return false;
        }

        var rotation = model.RootRotation;
        RotatedBounds(model.Mesh.bounds, rotation, out var min, out var max);

        var size = max - min;

        // A2h (operator, 2026-11-01: "les hitbox sont trop vers le sud-ouest").
        // A prop's placement is its **pivot**, not the centre of a bounding box:
        // a tree's canopy makes its box centre drift away from the trunk, and
        // centring the mesh on the collider's box centre therefore pushed the
        // trunk to the north-east of the hitbox. The client models are authored
        // with their base at the origin, exactly like Megabonk's prefabs, so the
        // mesh's own origin takes the pivot and nothing is offset sideways.
        var target = box;
        // A rule that keeps its collider (a pot's trigger sphere) does not use it
        // as a fit target either: the trigger's own bounds are larger than the
        // drawn object and would inflate the mesh.
        var collider = rule.FitColliders ? ColliderBounds(node) : null;
        if (collider.HasValue &&
            collider.Value.size.x >= box.size.x * HalfFootprint &&
            collider.Value.size.z >= box.size.z * HalfFootprint)
        {
            target = collider.Value;
        }

        var world = target.size;
        if (size.x <= 0f || size.y <= 0f || size.z <= 0f || world.x <= 0f || world.y <= 0f || world.z <= 0f)
        {
            return false;
        }

        // Inside the target box on every axis: the collider keeps deciding where
        // the player can walk, so the mesh must not overhang it. A tree keeps its
        // proportions (uniform); a rock is stretched to the box on all three axes
        // (A2j), because the client's rock is a tall slab and the native boxes are
        // squat boulders — a uniform fit left every rock standing on end.
        var propScale = Mathf.Max(Plugin.PropScale.Value, 0.01f);
        var factor = rule.Stretch
            ? new Vector3(world.x / size.x, world.y / size.y, world.z / size.z) * propScale
            : Vector3.one * (Mathf.Min(world.x / size.x, Mathf.Min(world.y / size.y, world.z / size.z)) * propScale);

        // A2o (operator, 2026-11-01: "il reste toujours les arbres géants à
        // corriger"). Megabonk's landmark `Tree` prefab is 52 m tall, so a mesh
        // fitted to its box becomes a 52 m PoE tree. A swapped mesh is capped to
        // `[Props] PropMaxHeight` (0 disables): the tree keeps its own proportions
        // and simply stops growing at that height, while the native collider is
        // re-shaped to it by FitColliders. Retextured families are untouched —
        // they are Megabonk's own models.
        var maxHeight = Plugin.PropMaxHeight.Value;
        if (maxHeight > 0f && size.y * factor.y > maxHeight)
        {
            var capped = maxHeight / size.y;
            factor = Vector3.one * capped;
            NoteCapped(id, world.y, maxHeight);
        }

        var child = new GameObject(ChildName);
        child.transform.SetParent(node, false);

        var parentScale = node.lossyScale;
        child.transform.localScale = new Vector3(
            factor.x / Safe(parentScale.x),
            factor.y / Safe(parentScale.y),
            factor.z / Safe(parentScale.z));

        // A2i (operator, 2026-11-01: "les roches sont trop vers le nord-ouest
        // maintenant… faut trouver le juste milieu"). Two anchors were tried and
        // each was wrong in its own direction: the collider/box centre (a tree's
        // canopy drags it off the trunk, SW) and the placer node's pivot (the
        // prefab root, NW). The frame that is actually right is **the transform
        // the game itself draws the prop from** — the native renderer — because
        // that is the object being replaced: its origin is the visual's origin.
        // Its yaw is kept, its tilt onto the terrain normal is not (A2e).
        var frame = native.transform;
        var pivot = frame.position;
        var placement = Upright(frame.rotation);
        child.transform.rotation = placement * rotation;

        // A2h: the ground under the pivot, not a box floor: a native renderer box
        // may start below the visible base (a shadow plane, a root skirt) and a
        // collider box may start above it, and either reads as "the tree sticks
        // out of the ground". One ray down from above the pivot, skipping the
        // prop's own colliders, and the mesh's lowest vertex is placed on what
        // the game says the terrain is; the box floor is the fallback.
        //
        // A3 pots are the exception (A3d ter, 0.9.130): a pot's own mesh starts
        // *below* its pivot (-0.14 m small, -0.18 m big), so the native pot is
        // sunk into the floor. Standing our pot on the terrain instead made every
        // pot that much taller above ground — the big one read "trop grand en
        // hauteur". Anchoring on the native box floor reproduces the same top.
        var floor = rule.GroundOnBox
            ? box.min.y
            : GroundAt(node, new Vector3(pivot.x, box.min.y, pivot.z), box.size.y);
        var sink = rule.GroundOnBox ? 0f : Mathf.Max(Plugin.PropSink.Value, 0f);
        var grounded = new Vector3(pivot.x, floor, pivot.z) - Vector3.up * (min.y * factor.y);
        child.transform.position = grounded - Vector3.up * sink;

        child.AddComponent<MeshFilter>().sharedMesh = model.Mesh;
        var renderer = child.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = Materials(native, model, id);
        ReportFit(id, box, collider ?? box, target, pivot, size, factor);

        // A2g: the native collider is Megabonk's own shape for *its* prop — a
        // 7.4 m capsule around a 4.2 m tree, a 12 m sphere around a 21 m rock —
        // so the player hits air or walks into the mesh. It is re-shaped to the
        // mesh that is now drawn, which is what "the hitbox matches the visual"
        // means.
        if (Plugin.FitPropColliders.Value && rule.FitColliders)
        {
            FitColliders(node, renderer, id);
        }

        foreach (var part in parts)
        {
            if (part != null)
            {
                part.enabled = false;
            }
        }

        swapped.TryGetValue(id, out var count);
        swapped[id] = count + 1;
        return true;
    }

    /// <summary>One material per submesh, cloned from the native renderer's own
    /// material so the host's shader, keywords and lighting rules stay (the same
    /// rule the bodies and creatures follow), with our colour map on the albedo
    /// aliases. <see cref="PoeCharacters.BuildMaterial"/> caches by
    /// (source material, texture), so a floor of props shares a handful of
    /// materials.</summary>
    internal static Material[] Materials(MeshRenderer native, GltfSkin.StaticModel model, string id)
    {
        var submeshes = Mathf.Max(1, model.Mesh.subMeshCount);
        var materials = new Material[submeshes];
        for (var index = 0; index < submeshes; index++)
        {
            var texture = model.Textures != null && index < model.Textures.Length ? model.Textures[index] : null;
            var cut = model.AlphaCut != null && index < model.AlphaCut.Length && model.AlphaCut[index];

            // A2e/A2f: a foliage or branch card is alpha-tested by the client,
            // and drawn opaque its sheet shows the near-black surround as hard
            // squares. `CutMaterial` picks a source that really clips.
            materials[index] = cut
                ? CutMaterial(native, texture, id, index)
                : PoeCharacters.BuildMaterial(native, texture, false) ?? native.sharedMaterial;

            // A group with no colour map is the client's own black surface
            // (`blackc.mat`, the solid base under a tree's foliage). The clone
            // still carries the native prop's atlas, so it is tinted out rather
            // than left to draw Megabonk's bark there.
            var material = materials[index];
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

                if (material.HasProperty("_AlbedoMapIntensity"))
                {
                    material.SetFloat("_AlbedoMapIntensity", 0f);
                }
            }
        }

        return materials;
    }

    /// <summary>
    /// A2f: a cut card needs a shader that really alpha-tests. 0.9.78 wrote
    /// `_AlphaClipping` on the host prop's own material, which accepted the value
    /// and kept drawing the sheet opaque (the operator still saw the black
    /// squares), so the source is now chosen from what the material itself
    /// reports:
    ///   1. the native prop material when it **already** clips
    ///      (`_AlphaClipping` &gt; 0), which keeps its lighting and outline;
    ///   2. the host body material, the one shader this build has been shown to
    ///      clip with values only (the hair cards);
    ///   3. `Unlit/Transparent Cutout`, the built-in cutout, which always clips —
    ///      the guaranteed floor of the chain, so a card can never draw its black
    ///      surround.
    /// One line per (kind, submesh) states which source won.
    /// </summary>
    private static Material CutMaterial(MeshRenderer native, Texture2D texture, string id, int submesh)
    {
        var source = native.sharedMaterial;
        if (source != null && Clips(source))
        {
            NoteCut(id, submesh, "the native prop material (it already clips)");
            return PoeCharacters.BuildMaterial(source, texture, true) ?? source;
        }

        if (PoeCharacters.HostBodyMaterial != null)
        {
            var body = PoeCharacters.BuildMaterial(PoeCharacters.HostBodyMaterial, texture, true);
            if (body != null)
            {
                // The body shader carries an outline pass. A negative size pushes
                // that hull *inside* the card (a small positive one would fatten
                // every leaf, and 0 makes it coincide with the surface and
                // z-fight it black — the trap the body path documents).
                try
                {
                    if (body.HasProperty("_OutlineSize"))
                    {
                        body.SetFloat("_OutlineSize", -0.5f);
                    }
                }
                catch (Exception)
                {
                    // A shader without the property simply keeps its own hull.
                }

                NoteCut(id, submesh, "the host body material");
                return body;
            }
        }

        var unlit = Shader.Find("Unlit/Transparent Cutout");
        if (unlit != null)
        {
            var material = new Material(unlit) { name = "WraeclastCutout" };
            if (texture != null)
            {
                material.mainTexture = texture;
            }

            if (material.HasProperty("_Cutoff"))
            {
                material.SetFloat("_Cutoff", PoeCharacters.BodyAlphaCutoff);
            }

            NoteCut(id, submesh, "Unlit/Transparent Cutout");
            return material;
        }

        NoteCut(id, submesh, "the native prop material (no clip-capable source)");
        return PoeCharacters.BuildMaterial(source, texture, true) ?? source;
    }

    /// <summary>Does this material already clip its own cards? A value read, not
    /// a keyword call (this interop's keyword getters throw).</summary>
    private static bool Clips(Material material)
    {
        try
        {
            return material.HasProperty("_AlphaClipping") && material.GetFloat("_AlphaClipping") > 0.5f;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static readonly HashSet<string> CutLogged = new HashSet<string>(StringComparer.Ordinal);

    private static void NoteCut(string id, int submesh, string source)
    {
        if (CutLogged.Add($"{id}#{submesh}"))
        {
            Plugin.Logger.LogInfo($"props material: {id} submesh {submesh} is a cut card -> {source}");
        }
    }

    /// <summary>The client's grey cliff albedo, already shipped for the terrain
    /// (A1). `rock`/`boulder` bind it over Megabonk's own boulder mesh (A2k), so
    /// the hitbox stays the mesh's own and the rock matches the cliffs.</summary>
    private const string RockAlbedoId = "forest-rock";

    /// <summary>The client's mossy stone, for Megabonk's own ruin blocks (A2l).</summary>
    private const string RuinAlbedoId = "ruin-stone";

    /// <summary>The client's bark, for Megabonk's wooden crates and logs (A2n/A2o).</summary>
    private const string CrateAlbedoId = "crate-wood";

    /// <summary>The client's bush foliage, for Megabonk's own bushes (A2o).</summary>
    private const string BushAlbedoId = "bush-leaves";

    /// <summary>Kinds already reported, so the fit line is one per prop kind.</summary>
    private static readonly HashSet<string> Fitted = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The renderers that draw a prop's body: a flat one (a ground shadow, an
    /// icon billboard, the pot's `Quad`) is a card, not geometry, so it is left
    /// enabled and never drives the fit. The test uses the mesh's own bounds,
    /// which are valid before the object is first drawn (a world
    /// <c>Renderer.bounds</c> is zero until then).
    /// </summary>
    private static MeshRenderer[] BodyParts(Transform node)
    {
        var all = node.GetComponentsInChildren<MeshRenderer>(true);
        if (all == null || all.Length == 0)
        {
            return all;
        }

        var kept = new List<MeshRenderer>(all.Length);
        for (var index = 0; index < all.Length; index++)
        {
            var part = all[index];
            if (part == null)
            {
                continue;
            }

            var filter = part.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            var size = mesh != null ? mesh.bounds.size : part.bounds.size;
            var thin = Mathf.Min(size.x, Mathf.Min(size.y, size.z));
            var wide = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            if (wide > 0.0001f && thin <= wide * 0.02f)
            {
                continue;
            }

            kept.Add(part);
        }

        return kept.ToArray();
    }

    /// <summary>The collider a placed object carries, in world space: the box the
    /// player really hits. Null when the object has none (then the renderer box
    /// is the only reference).</summary>
    private static Bounds? ColliderBounds(Transform node)
    {
        var colliders = node.GetComponentsInChildren<Collider>(true);
        if (colliders == null || colliders.Length == 0)
        {
            return null;
        }

        Bounds? box = null;
        for (var index = 0; index < colliders.Length; index++)
        {
            var collider = colliders[index];
            if (collider == null)
            {
                continue;
            }

            if (box == null)
            {
                box = collider.bounds;
            }
            else
            {
                var union = box.Value;
                union.Encapsulate(collider.bounds);
                box = union;
            }
        }

        return box;
    }

    /// <summary>One line per prop kind: the renderer box, the collider box, the box
    /// the mesh was fitted to, the box it ends up as, and how far the two candidate
    /// anchors sit from the one used (the renderer's own transform). A prop that
    /// still reads offset can then be tuned from numbers instead of a photograph.
    /// </summary>
    private static void ReportFit(
        string id,
        Bounds renderer,
        Bounds collider,
        Bounds target,
        Vector3 pivot,
        Vector3 model,
        Vector3 factor)
    {
        if (!Fitted.Add(id))
        {
            return;
        }

        var fromBoxX = renderer.center.x - pivot.x;
        var fromBoxZ = renderer.center.z - pivot.z;
        var fromColliderX = collider.center.x - pivot.x;
        var fromColliderZ = collider.center.z - pivot.z;

        Plugin.Logger.LogInfo(
            $"props fit: {id} native box {renderer.size.x:0.00} x {renderer.size.y:0.00} x {renderer.size.z:0.00} m, " +
            $"collider {collider.size.x:0.00} x {collider.size.y:0.00} x {collider.size.z:0.00} m, " +
            $"fitted to {target.size.x:0.00} x {target.size.y:0.00} x {target.size.z:0.00} m -> " +
            $"ours {model.x * factor.x:0.00} x {model.y * factor.y:0.00} x {model.z * factor.z:0.00} m " +
            $"(scale {factor.x:0.00}/{factor.y:0.00}/{factor.z:0.00}); " +
            $"anchors: pivot ({pivot.x:0.00}, {pivot.z:0.00}), box centre +({fromBoxX:0.00}, {fromBoxZ:0.00}), " +
            $"collider centre +({fromColliderX:0.00}, {fromColliderZ:0.00})");
    }

    /// <summary>The placement rotation: the placer's yaw, without its tilt onto
    /// the terrain normal. `[Props] PropAlignToGround` restores the full native
    /// rotation.</summary>
    private static Quaternion Upright(Quaternion rotation)
    {
        if (Plugin.PropAlignToGround.Value)
        {
            return rotation;
        }

        return Quaternion.Euler(0f, rotation.eulerAngles.y, 0f);
    }

    /// <summary>
    /// A2h: the terrain under a prop, as the game itself sees it — one downward
    /// sweep that skips the prop's own colliders and the player, keeps only
    /// upward-facing hits, and answers the hit **closest to the expected floor**
    /// (`around.y`): the highest hit would be a neighbour's canopy, and that is
    /// how a tree ends up standing in the air. Falls back to the expected floor.
    /// </summary>
    internal static float GroundAt(Transform node, Vector3 around, float height)
    {
        try
        {
            var reach = Mathf.Max(60f, height * 4f);
            var origin = new Vector3(around.x, around.y + reach * 0.5f, around.z);
            var hits = Physics.RaycastAll(origin, Vector3.down, reach, ~0, QueryTriggerInteraction.Ignore);
            if (hits == null)
            {
                return around.y;
            }

            var best = around.y;
            var bestDistance = float.MaxValue;
            for (var index = 0; index < hits.Length; index++)
            {
                var hit = hits[index];
                var other = hit.collider;
                if (other == null || hit.normal.y < 0.5f)
                {
                    continue;
                }

                if (other.transform.IsChildOf(node) || node.IsChildOf(other.transform))
                {
                    continue;
                }

                if (other.GetComponentInParent<MyPlayer>() != null)
                {
                    continue;
                }

                var distance = Mathf.Abs(hit.point.y - around.y);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = hit.point.y;
                }
            }

            return best;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"props: the ground ray for {node.name} failed: {error.Message}");
            return around.y;
        }
    }

    /// <summary>
    /// A2k: keep the native prop and only paint it. Megabonk's rock is a boulder
    /// with an exact collider; the client's rock mesh is a slab that no fit can
    /// turn into one, so the family keeps its own mesh and takes the client's
    /// grey albedo through the same neutralised material clone the bodies use
    /// (host shader and keywords, our texture on the albedo aliases).
    /// </summary>
    private static bool Retexture(Transform node, string textureId, Dictionary<string, int> swapped)
    {
        var parts = node.GetComponentsInChildren<MeshRenderer>(true);
        if (parts == null || parts.Length == 0)
        {
            return false;
        }

        var texture = PoeAssets.GetTerrain(textureId);
        if (texture == null)
        {
            if (RetextureMissing.Add(textureId))
            {
                Plugin.Logger.LogWarning(
                    $"props: {textureId} is not shipped, {node.name} keeps Megabonk's own texture " +
                    "(run mod/tools/prepare-data.ps1)");
            }

            return false;
        }

        var painted = 0;
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part == null || part.sharedMaterial == null)
            {
                continue;
            }

            var material = PoeCharacters.BuildMaterial(part, texture);
            if (material == null)
            {
                continue;
            }

            part.sharedMaterial = material;
            painted++;
        }

        if (painted == 0)
        {
            return false;
        }

        // A2r: some props carry `CameraFadeObject` (they go transparent when the
        // player walks behind them). It caches its own `defaultMaterial` at Start
        // and swaps back to it on exit — which restored Megabonk's atlas, so a
        // faded rock showed the base texture. Both of its materials are painted
        // here; the fade variant only takes the texture, so its transparency is
        // untouched.
        RepaintFades(node, texture);

        // A marker only (no renderer): the later passes skip this object and the
        // sweep counts it as handled.
        new GameObject(ChildName).transform.SetParent(node, false);

        const string kind = "native mesh + client albedo";
        swapped.TryGetValue(kind, out var count);
        swapped[kind] = count + 1;

        if (RetextureLogged.Add(node.name))
        {
            Plugin.Logger.LogInfo(
                $"props: {node.name} keeps Megabonk's mesh and collider and wears the client's grey " +
                $"{textureId} albedo ({painted} part(s))");
        }

        return true;
    }

    /// <summary>Per fade component: the albedo it must wear, and the material
    /// instance it was last painted with, so the repaint is one per swap.</summary>
    private static readonly Dictionary<int, Texture2D> FadeAlbedo = new Dictionary<int, Texture2D>();
    private static readonly Dictionary<int, int> FadePainted = new Dictionary<int, int>();

    private static readonly HashSet<string> FadeLogged = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// A2r (operator, 2026-11-01: "certains rochers sont déplaçables, quand on
    /// s'en approche ils deviennent invisibles … et récupèrent leur texture de
    /// base"). Those props carry <c>CameraFadeObject</c>: at <c>Start</c> it
    /// stores `renderer.material` (an **instance copy** of the prop's material)
    /// as its own `defaultMaterial`, swaps the renderer to `fadeMaterial` near
    /// the camera and puts the stored instance back on exit — so painting the
    /// renderer once only lasted until the first fade, and the private field
    /// cannot be painted before `Start` has run.
    /// The component is registered here and `RepaintFade` then follows the
    /// renderer: whenever the material instance it carries changes (the fade and
    /// its restore), that instance is painted — the fade variant with the texture
    /// **only**, so its blend stays its own.
    /// </summary>
    private static void RepaintFades(Transform node, Texture2D texture)
    {
        if (node == null || texture == null)
        {
            return;
        }

        try
        {
            var fades = node.GetComponentsInChildren<CameraFadeObject>(true);
            if (fades == null || fades.Length == 0)
            {
                return;
            }

            var registered = 0;
            for (var index = 0; index < fades.Length; index++)
            {
                var fade = fades[index];
                if (fade == null)
                {
                    continue;
                }

                FadeAlbedo[fade.GetInstanceID()] = texture;
                registered++;
                RepaintFade(fade);
            }

            if (registered > 0 && FadeLogged.Add(node.name))
            {
                Plugin.Logger.LogInfo(
                    $"props: {node.name} carries a CameraFadeObject — its fade and restore materials " +
                    $"take the client albedo too ({registered} component(s))");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"props: {node.name} fade materials could not be painted: {error.Message}");
        }
    }

    /// <summary>Paint the material the fade component's renderer carries right
    /// now, once per material swap. Called from the component's own
    /// <c>Update</c>/<c>StopFade</c> postfixes, so a restore cannot win.</summary>
    internal static void RepaintFade(CameraFadeObject fade)
    {
        if (fade == null)
        {
            return;
        }

        var key = fade.GetInstanceID();
        if (!FadeAlbedo.TryGetValue(key, out var texture) || texture == null)
        {
            return;
        }

        try
        {
            var renderer = fade.renderer;
            var material = renderer == null ? null : renderer.sharedMaterial;
            if (material == null)
            {
                return;
            }

            var materialId = material.GetInstanceID();
            if (FadePainted.TryGetValue(key, out var last) && last == materialId)
            {
                return;
            }

            FadePainted[key] = materialId;

            var isFade = fade.fadeMaterial != null && fade.fadeMaterial.GetInstanceID() == materialId;
            if (isFade)
            {
                PaintTexture(material, texture);
            }
            else
            {
                PoeCharacters.ApplySurface(material, texture);
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"props: a fade material could not be painted: {error.Message}");
        }
    }

    /// <summary>The albedo aliases only: a fade material keeps its own blend
    /// state, or the prop would stop being see-through.</summary>
    private static void PaintTexture(Material material, Texture2D texture)
    {
        try
        {
            material.mainTexture = texture;
            if (material.HasProperty("_MainTex"))
            {
                material.SetTexture("_MainTex", texture);
            }

            if (material.HasProperty("_AlbedoMap"))
            {
                material.SetTexture("_AlbedoMap", texture);
            }

            if (material.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", texture);
            }
        }
        catch (Exception)
        {
            // A shader without those names simply keeps its own map.
        }
    }

    private static readonly HashSet<string> RetextureLogged = new HashSet<string>(StringComparer.Ordinal);
    private static readonly HashSet<string> RetextureMissing = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// A2g: re-shape the native colliders to the mesh that is now drawn. Megabonk
    /// ships its own shape for its own prop (a 7.4 m capsule around a 4.2 m tree,
    /// a 12 m sphere around a 21 m rock), so the player hits air beside the trunk
    /// or walks into the rock. Sizes are written in each collider's own local
    /// space (divided by its lossy scale) and the height is kept: the player
    /// walks around a prop, not over it. A box takes the mesh's footprint, a
    /// capsule the footprint's half-width as its radius, a sphere the mesh's
    /// horizontal bounding radius capped to half its height.
    /// </summary>
    private static void FitColliders(Transform node, MeshRenderer renderer, string id)
    {
        try
        {
            var colliders = node.GetComponentsInChildren<Collider>(true);
            if (colliders == null || colliders.Length == 0)
            {
                return;
            }

            var bounds = renderer.bounds;
            var note = new List<string>(colliders.Length);
            for (var index = 0; index < colliders.Length; index++)
            {
                var collider = colliders[index];
                if (collider == null)
                {
                    continue;
                }

                // Everything is logged, triggers included: 0.9.80 printed nothing
                // at all, which read as "the pass ran" when it had silently
                // skipped every collider.
                var scale = collider.transform.lossyScale;
                var width = bounds.size.x / Safe(scale.x);
                var depth = bounds.size.z / Safe(scale.z);
                var height = bounds.size.y / Safe(scale.y);

                var trigger = collider.isTrigger ? " (trigger)" : string.Empty;
                switch (collider)
                {
                    case BoxCollider box:
                        note.Add($"box {box.size.x:0.00}x{box.size.z:0.00} -> {width:0.00}x{depth:0.00}{trigger}");
                        box.size = new Vector3(width, box.size.y, depth);
                        break;

                    case CapsuleCollider capsule:
                    {
                        var radius = Mathf.Min(
                            0.5f * Mathf.Max(width, depth),
                            Mathf.Max(capsule.height * 0.5f, 0.05f));
                        note.Add($"capsule r {capsule.radius:0.00} -> {radius:0.00}{trigger}");
                        capsule.radius = radius;
                        if (capsule.height < radius * 2f + 0.05f)
                        {
                            capsule.height = radius * 2f + 0.05f;
                        }

                        break;
                    }

                    case SphereCollider sphere:
                    {
                        var diagonal = 0.5f * Mathf.Sqrt(width * width + depth * depth);
                        var radius = Mathf.Min(diagonal, height * 0.5f);
                        note.Add($"sphere r {sphere.radius:0.00} -> {radius:0.00}{trigger}");
                        sphere.radius = radius;
                        break;
                    }

                    case MeshCollider mesh:
                        // A mesh collider cannot be resized; it follows its own
                        // mesh, which is Megabonk's prop, not ours. It is named
                        // here and left alone: the operator's hitbox test then
                        // says whether it has to be replaced by a box.
                        note.Add($"mesh collider (mesh {mesh.sharedMesh?.name ?? "?"}) left as is{trigger}");
                        break;

                    default:
                        note.Add($"{collider.GetType().Name} not handled{trigger}");
                        break;
                }
            }

            if (note.Count > 0 && ColliderFitLogged.Add(id))
            {
                Plugin.Logger.LogInfo(
                    $"props collider: {id} ({note.Count} found) {string.Join(", ", note)}");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"props collider: {id} could not be re-shaped: {error.Message}");
        }
    }

    private static readonly HashSet<string> ColliderFitLogged = new HashSet<string>(StringComparer.Ordinal);
    private static readonly HashSet<string> CappedLogged = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>One line per prop kind whose native box asked for more height
    /// than <see cref="Plugin.PropMaxHeight"/> allows.</summary>
    private static void NoteCapped(string id, float nativeHeight, float maxHeight)
    {
        if (CappedLogged.Add(id))
        {
            Plugin.Logger.LogInfo(
                $"props: {id} capped to {maxHeight:0.0} m (its native box is {nativeHeight:0.0} m tall)");
        }
    }

    private static PropRule RuleFor(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var lower = name.ToLowerInvariant();
        foreach (var rule in Rules)
        {
            if (lower.Contains(rule.Match))
            {
                return rule;
            }
        }

        return null;
    }

    /// <summary>The model's own bounds after its root rotation, ie the box the
    /// child will really occupy (the client's meshes are authored Z-up).</summary>
    internal static void RotatedBounds(Bounds bounds, Quaternion rotation, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

        var centre = bounds.center;
        var extents = bounds.extents;
        for (var corner = 0; corner < 8; corner++)
        {
            var offset = new Vector3(
                (corner & 1) == 0 ? -extents.x : extents.x,
                (corner & 2) == 0 ? -extents.y : extents.y,
                (corner & 4) == 0 ? -extents.z : extents.z);
            var point = rotation * (centre + offset);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
    }

    private static float Safe(float value) => Mathf.Abs(value) < 1e-4f ? 1e-4f : value;

    private static void Report(
        RandomObjectPlacer placer,
        int matched,
        Dictionary<string, int> swapped,
        Dictionary<string, int> surveyed,
        float elapsedMs = -1f)
    {
        var counts = new List<string>();
        foreach (var pair in swapped)
        {
            counts.Add($"{pair.Key} {pair.Value}");
        }

        // Rules with no client model are recorded, so the log states what was
        // deliberately left as Megabonk's own scenery.
        var kept = new List<string>();
        foreach (var pair in surveyed)
        {
            var handled = false;
            foreach (var rule in Rules)
            {
                if (rule.Match == pair.Key && (rule.Props.Length > 0 || rule.Retexture != null))
                {
                    handled = true;
                    break;
                }
            }

            if (!handled)
            {
                kept.Add($"{pair.Key} {pair.Value}");
            }
        }

        counts.Sort(StringComparer.Ordinal);
        kept.Sort(StringComparer.Ordinal);
        Plugin.Logger.LogInfo(
            $"props: {matched} object(s) swapped [{string.Join(", ", counts)}]" +
            (kept.Count > 0 ? $", left native [{string.Join(", ", kept)}]" : string.Empty) +
            (placer == null ? " in the floor sweep" : $" over {placer.name}") +
            (elapsedMs >= 0f ? $", took {elapsedMs:0} ms" : string.Empty) +
            " (colliders, pivots and spawn rules untouched)");
    }
}
