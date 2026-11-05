using System;
using System.Collections.Generic;
using System.IO;
using Actors.Enemies;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Utility;
using UnityEngine;

namespace Wraeclast.Data;

/// <summary>
/// Megabonk enemies wear PoE creature meshes. The AI, collider, rigidbody,
/// damage flow, debuff colours and spawner stay Megabonk's; only the visual is
/// replaced by a skinned creature from <c>assets/models/</c> (beasts, skeletons,
/// zombies, ghosts, goatman, stonegolem, fireelemental, tormentedspirit, spiders) or, for
/// boss-tier enemies, an Atlas boss (elder, shaper, maven).
///
/// The mapping from <see cref="EEnemy"/> to a creature is our design (Megabonk
/// ships no such table) and lives in <see cref="CreatureOf"/>. One template
/// per creature is loaded once; every live enemy gets a <see cref="GltfSkin.Clone"/>
/// because bones and vertex buffers are per instance.
///
/// Like the player body, this is a static system ticked from a Harmony postfix
/// (AnimatedMesh.Update), never a custom MonoBehaviour.
/// </summary>
internal static class PoeEnemies
{
    /// <summary>CPU skinning upload rate for enemies near the camera. Megabonk
    /// can hold dozens of enemies; 20 Hz is smooth for idle/walk and keeps the
    /// vertex upload bounded.</summary>
    private const float NearSkinInterval = 0.05f;

    /// <summary>Upload rate for enemies outside <see cref="NearRadius"/>: they
    /// are small on screen, so 8 Hz is indistinguishable and quarters the work.</summary>
    private const float FarSkinInterval = 0.125f;

    /// <summary>Distance from the camera where an enemy drops to the far rate.</summary>
    private const float NearRadius = 40f;

    /// <summary>Every MK Toon diffuse shader in the build exposes the dissolve
    /// funnel (measured: <c>Standard/Simple</c> 100 properties and its
    /// <c>+ Outline</c> twin 109, both with <c>_Dissolve</c> /
    /// <c>_DissolveAmount</c>), so the corpse keeps the body's exact shader and
    /// only gains the keyword. Which of the three dissolve variants is active
    /// is a material keyword (<c>_MK_DISSOLVE_DEFAULT</c>,
    /// <c>_..._BORDER_COLOR</c>, <c>_..._BORDER_RAMP</c>; harvested from the
    /// build's shader strings).</summary>
    private const string DissolveKeyword = "_MK_DISSOLVE_DEFAULT";

    /// <summary>Used only when the enemy prefab carries no
    /// <see cref="EnemyDissolve"/> (a dev spawn outside the pool). The real
    /// duration is read from the component.</summary>
    private const float FallbackDissolveSeconds = 0.6f;

    private sealed class Instance
    {
        internal Enemy Enemy;
        internal GltfSkin.Model Model;
        internal GameObject Root;
        internal Renderer Renderer;
        internal GltfSkin.Clip AttackClip;
        internal GltfSkin.Clip DeathClip;
        internal float IdleTime;
        internal float WalkTime;
        internal float Phase;
        internal float NextSkin;
        internal float AttackElapsed = -1f;
        internal float DeathElapsed = -1f;
        internal float DissolveSeconds;
        internal float DeathDuration;
        internal float Height;
        internal Vector3 DeathPosition;
        internal Material DeathMaterial;
        internal bool DissolveByShader;
        internal Vector3 LastPosition;
        internal bool HasPosition;

        /// <summary>The root's local scale from the attach fit and the enemy
        /// transform's scale it was computed against, so a later scale change on
        /// the enemy cannot drag the model with it.</summary>
        internal float LocalFit;
        internal float BaseParentScale;

        /// <summary>Model id and the frame the instance was attached, for the
        /// one-time "which clock drives the clips" line.</summary>
        internal string Creature;
        internal int AttachFrame;

        /// <summary>Frame this instance was last sampled and whether the game's
        /// own <see cref="AnimatedMesh"/> path ever drove it.</summary>
        internal int LastTickFrame = -1;
        internal bool MeshTicked;

        /// <summary>Role the roster gave this spawn (a boss only), kept for the
        /// one-shot render probe and the forced CPU skin choice.</summary>
        internal string BossRole;

        /// <summary>E1: one renderer probe per boss spawn, a second after the
        /// attach so the first render has already run. A culled or invisible mesh
        /// cannot be told apart from a mis-scaled one without this line.</summary>
        internal float ProbeAt = -1f;
        internal bool ProbeLogged;

        /// <summary>Whether the body is in the full-rate tier. A reduced body is
        /// still the PoE mesh with its own texture — only its clip sampling drops
        /// to 2 Hz and its shadow pass is switched off.</summary>
        internal bool FullDetail = true;
        internal bool Boss;

        /// <summary>Camera-distance squared, refreshed by the detail pass so
        /// the sort does not recompute it in the comparison.</summary>
        internal float Distance;
    }

    // Key: Enemy instance id. Pooled enemies keep the id, so a new spawn
    // replaces the previous instance in place.
    private static readonly Dictionary<int, Instance> Instances = new Dictionary<int, Instance>();

    // Corpses still dissolving. They are no longer driven by the enemy's own
    // AnimatedMesh (the game pauses/returns early on death), so they are ticked
    // from MyPlayer.Update instead.
    private static readonly List<Instance> Dying = new List<Instance>();

    private static readonly HashSet<string> LoggedDissolves = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>One placement line per boss path, so an invisible boss can be
    /// located from the log (model Y range, pivot, renderer state).</summary>
    private static readonly HashSet<string> LoggedPlacements = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Once per creature: which clip the attach found and whether the
    /// game's animation component is on the enemy.</summary>
    private static readonly HashSet<string> LoggedAnimation = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Creatures whose mesh carries single-layer cloth (The Shaper's
    /// robe, the Maven's dress, the Elder's mantle, the wraith's spectral robe) or
    /// whose winding is inverted (the Elder renders as nothing with back-face
    /// culling).
    /// Only these bodies are drawn two-sided: a closed body reads the same from
    /// outside, and MK Toon's outline pass shares `_RenderFace`, so the flag is
    /// not sprayed over the whole bestiary.</summary>
    private static readonly HashSet<string> ClothCreatures =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "shaper", "maven", "elder", "searing-exarch", "ghosts", "tormentedspirit" };

    /// <summary>Once per creature: the frame clock had to drive its clips,
    /// because <c>AnimatedMesh.Update</c> never reached the enemy.</summary>
    private static readonly HashSet<string> LoggedTickSources = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Snapshot for the frame fallback, so a Detach inside a tick
    /// cannot modify the dictionary it is iterating.</summary>
    private static readonly List<Instance> TickBuffer = new List<Instance>();

    /// <summary>How often the detail tiers are re-sorted. Half a second keeps a
    /// body switching tier from being read as a pop, and keeps the
    /// camera-distance sort off the frame path.</summary>
    private const float BudgetInterval = 0.5f;

    /// <summary>Clip rate for bodies past the full-rate tier. They stay the PoE
    /// model with their own texture everywhere; at 2 Hz a walk still reads at
    /// distance and the bone work drops to a quarter of the far rate.</summary>
    private const float LowSkinInterval = 0.5f;

    private static readonly List<Instance> BudgetBuffer = new List<Instance>();
    private static float nextBudget;
    private static bool budgetLogged;

    /// <summary>Beyond this distance a drawn body stops casting shadows: the
    /// shadow pass is another full skinned draw call per enemy, and a distant
    /// silhouette in a swarm reads the same without it.</summary>
    private const float ShadowRadius = 30f;

    internal static int LiveCount => Instances.Count;

    /// <summary>Bodies in the full-rate tier this pass. Read by the frame-time
    /// probe; the rest still draw the PoE mesh at 2 Hz.</summary>
    internal static int FullCount { get; private set; }

    /// <summary>V11: the fitted world height of the creature drawn for this
    /// enemy, so the status badges stand above the model (a boss is 16.78 units
    /// tall, not its 13.42 slot; a fitted creature is not its Megabonk height).</summary>
    internal static bool TryGetHeight(Enemy enemy, out float height)
    {
        height = 0f;
        if (enemy == null || !Instances.TryGetValue(enemy.GetInstanceID(), out var instance))
        {
            return false;
        }

        height = instance.Height;
        return height > 0.01f;
    }

    // AnimatedMesh instance id -> Enemy instance id. The animation component is
    // what ticks every frame, but it has no back-reference to the Enemy.
    private static readonly Dictionary<int, int> ByAnimation = new Dictionary<int, int>();

    private static readonly Dictionary<string, GltfSkin.Model> Templates =
        new Dictionary<string, GltfSkin.Model>(StringComparer.OrdinalIgnoreCase);

    // Camera lookup is cached per frame: Camera.main searches the scene, and
    // Tick runs once per live enemy, so it must not run per enemy.
    private static int CameraFrame = -1;
    private static Vector3 CameraPosition;
    private static bool CameraResolved;
    private static bool SkinPolicyLogged;
    private static readonly HashSet<string> GroundPolicyLogged = new HashSet<string>(StringComparer.Ordinal);
    private static bool IdleMissingLogged;
    private static bool TwoSidedLogged;

    private static bool TryCameraPosition(out Vector3 position)
    {
        if (CameraFrame != Time.frameCount)
        {
            CameraFrame = Time.frameCount;
            var camera = Camera.main;
            CameraResolved = camera != null;
            CameraPosition = camera != null ? camera.transform.position : Vector3.zero;
        }

        position = CameraPosition;
        return CameraResolved;
    }

    internal static void Attach(Enemy enemy, EnemyData data, string boss)
    {
        if (enemy == null || data == null)
        {
            return;
        }

        var key = enemy.GetInstanceID();
        Detach(enemy, false);

        if (!Plugin.ReplaceEnemyModels.Value)
        {
            return;
        }

        var creature = boss == null && data.isFlying
            ? FlyingCreatureOf(data.enemyName)
            : CreatureOf(data.enemyName, boss);
        if (creature == null)
        {
            return;
        }

        var baseRenderer = enemy.renderer;
        if (baseRenderer == null)
        {
            Plugin.Logger.LogWarning($"{data.enemyName}: no renderer to fit the creature against");
            return;
        }

        var template = LoadTemplate(creature);
        if (template == null)
        {
            return;
        }

        var model = GltfSkin.Clone(template);
        var root = GltfSkin.Instantiate(model, enemy.transform);
        var meshObject = root.transform.Find("Body");
        var bodyRenderer = meshObject != null ? meshObject.GetComponent<Renderer>() : null;
        if (bodyRenderer != null)
        {
            var enemyMaterial = PoeCharacters.BuildMaterial(baseRenderer, model.Texture);
            if (enemyMaterial != null)
            {
                // PoE cloth parts (The Shaper's robe, capes) are single-layer
                // sheets meant to be drawn two-sided; the host's material culls
                // back faces, which flattens the figure into a dark cutout.
                // MK Toon's control is `_RenderFace` (MK.Toon.RenderFace), not
                // `_Cull`: the shader dump lists 109 properties with no `_Cull`
                // and every pass reads its `culling` state from `_RenderFace`
                // (default 2 = Cull Back, 0 = Cull Off, the Unity order).
                // The Elder's atlas is mostly alpha below the 0.35 cut (a third of
                // its texels), and alpha clipping it makes the body vanish while
                // the renderer still reports enabled. No hair or cloth cards on
                // this model need the cut, so it is drawn opaque.
                if (string.Equals(creature, "elder", StringComparison.OrdinalIgnoreCase))
                {
                    if (enemyMaterial.HasProperty("_AlphaClipping"))
                    {
                        enemyMaterial.SetFloat("_AlphaClipping", 0f);
                    }

                    if (enemyMaterial.HasProperty("_AlphaCutoff"))
                    {
                        enemyMaterial.SetFloat("_AlphaCutoff", 0f);
                    }

                    Plugin.Logger.LogInfo("elder: alpha clipping off (the atlas is mostly transparent at the cut)");
                }


                if (ClothCreatures.Contains(creature) && enemyMaterial.HasProperty("_RenderFace"))
                {
                    enemyMaterial.SetFloat("_RenderFace", 0f);
                    if (!TwoSidedLogged)
                    {
                        TwoSidedLogged = true;
                        Plugin.Logger.LogInfo(
                            $"{creature}: cloth body is two-sided (_RenderFace 0), back faces are kept");
                    }
                }

                bodyRenderer.sharedMaterial = enemyMaterial;

                // A boss that is still fading in (the golem miniboss spawns
                // dissolved and blooms in) hands us a material whose dissolve
                // state rides along in the clone: the creature then draws fully
                // dissolved, which reads as an invisible enemy with a working
                // attack kit. Clear the value and the keyword on our clone.
                if (enemyMaterial.HasProperty("_DissolveAmount"))
                {
                    enemyMaterial.SetFloat("_Dissolve", 0f);
                    enemyMaterial.SetFloat("_DissolveAmount", 0f);
                    enemyMaterial.DisableKeyword(DissolveKeyword);
                    enemyMaterial.DisableKeyword("_MK_DISSOLVE_BORDER_COLOR");
                    enemyMaterial.DisableKeyword("_MK_DISSOLVE_BORDER_RAMP");
                }
            }
        }

        // Idle pose first: the pose bounds are the real size, the bind pose
        // ignores the skeleton's own scale. A model without an idle clip still
        // has to be fitted: leaving the bounds empty skipped the scale entirely
        // and left the mesh at its export size (the Elder then stood ~296 units
        // tall, so the camera sat inside it and the boss read as invisible).
        // One bounds pass, after the pose is current: the old call before the
        // sample was dead work for every model that shipped an idle clip, and a
        // swarm spawns hundreds of them in one frame.
        var idle = ClipOf(model, "idle");
        if (idle != null)
        {
            GltfSkin.Sample(model, idle, 0.4f, 1f);
        }
        else if (!IdleMissingLogged)
        {
            IdleMissingLogged = true;
            Plugin.Logger.LogWarning(
                $"{creature}: no idle clip, the fit uses the bind pose");
        }

        var local = GltfSkin.PoseBounds(model);

        if (local.size.y <= 0.01f)
        {
            // Last resort: the uploaded bind-pose bounds, before the node scales.
            local = model.Mesh.bounds;
        }

        // The GLB builder stands every model up with the scene root's Rx(+90),
        // which the runtime instantiates with the rest of the hierarchy; only
        // the yaw is ours. Inferring "up" from the longest axis laid the
        // quadrupeds (the blood ape) on their side.
        root.transform.localRotation = Quaternion.Euler(0f, Plugin.ModelYaw.Value, 0f);
        root.transform.localScale = Vector3.one;
        root.transform.localPosition = Vector3.zero;

        // Ground the model on the terrain under the enemy, not on the enemy's
        // pivot: the pivot is not guaranteed to be the body's contact (and the
        // pooled enemy's own GetFeetPosition answers with stale numbers). One
        // downward ray from above the enemy, skipping its own colliders, gives
        // the surface Megabonk's own enemies stand on. **A flyer is left to the
        // game**: its pivot hovers, and pinning the model to the floor (S1) put
        // a Bee under the map wherever the ray found the seabed or the void —
        // see the flying rule below.
        // S6: every boss reads at one size. The fit target was the native
        // renderer's `GetHeight()`, so the same Atlas boss spanned 23.78 units
        // on Ent1, 30.55 on the Shaper slot and 13.42 on the Maven slot; bosses
        // (minibosses included) now take `[Enemies] BossHeight` (the measured
        // Maven, 13.42). Normal creatures keep Megabonk's own height: only the
        // model changes, never the size.
        var bossTier = IsBossTier(data.enemyName);
        // S2c: BossScale multiplies the fixed boss height (default 1.25), so a
        // boss reads larger than the measured Maven without moving the knob that
        // keeps every slot at one size.
        var targetHeight = bossTier
            ? Plugin.BossHeight.Value * Mathf.Max(0.1f, Plugin.BossScale.Value)
            : enemy.GetHeight();
        if (targetHeight <= 0.01f)
        {
            // A disabled BossHeight falls back to the native height.
            targetHeight = enemy.GetHeight() > 0.01f ? enemy.GetHeight() : baseRenderer.bounds.size.y;
            bossTier = false;
        }

        var feet = enemy.transform.position;
        var anchor = "root";
        var pivotY = enemy.transform.position.y;
        if (data.isFlying)
        {
            // The flying rule (2026-11-01): a flyer keeps Megabonk's own
            // placement. The pivot hovers and the native renderer rides it, so
            // the model's lowest point is put on the pivot and nothing else
            // touches it — no terrain ray, no water clamp, no per-tick
            // re-anchor. S1 had grounded flyers because a black ape "flew" on
            // the Bee slot; that was a mis-skinned model, not a placement bug,
            // and the correction is what pushed bees under the map.
            anchor = "root (flying: the game's own hover)";
        }
        else if (TryGround(enemy, targetHeight, out var groundY))
        {
            var drop = pivotY - groundY;
            if (drop > targetHeight * 1.2f)
            {
                // The ray found something far under the spawn (the void, the
                // map's underside) instead of the floor the enemy stands on.
                // Trusting it buried a boss 30 units into the ground, which
                // reads as an invisible boss. Keep the game's own anchor: the
                // feet sit half a body under the pivot it spawned.
                feet.y = pivotY - targetHeight * 0.5f;
                anchor = $"pivot (terrain {groundY:0.##} is {drop:0.##} under, out of band)";
            }
            else
            {
                feet.y = groundY;
                anchor = "terrain";
            }
        }

        // A land creature must not stand under the water surface: the ray goes
        // through the water to the seabed, which read as a body sunk under the
        // map. The native pivot, AI and collider stay Megabonk's own; only the
        // mesh's vertical placement is corrected.
        if (!data.isFlying && TryWaterLevel(out var waterY) && feet.y < waterY)
        {
            anchor += $", clamped to the water surface {waterY:0.##}";
            feet.y = waterY;
        }

        if (GroundPolicyLogged.Add(creature))
        {
            Plugin.Logger.LogInfo(
                $"ground: root {enemy.transform.position.y:0.##} -> {feet.y:0.##} ({anchor}), " +
                $"delta {(feet.y - enemy.transform.position.y):0.##} units, enemy {targetHeight:0.##} tall, " +
                $"{(data.isFlying ? "flying" : "grounded")} slot");
        }

        var world = GltfSkin.ToWorldBounds(local, root.transform);
        if (world.size.y > 0.01f && targetHeight > 0.01f)
        {
            root.transform.localScale = Vector3.one * (targetHeight / world.size.y);
            world = GltfSkin.ToWorldBounds(local, root.transform);
        }

        root.transform.position += new Vector3(
            feet.x - world.center.x,
            feet.y - world.min.y,
            feet.z - world.center.z);
        if (!model.Gpu)
        {
            GltfSkin.ApplySkin(model);
        }

        if (bodyRenderer != null && local.size.y > 0.01f)
        {
            // Culling follows `localBounds`, and with updateWhenOffscreen off it
            // is never refreshed. A model whose idle clip scales the skeleton up
            // (the Elder: 2.9 units in the bind pose, 170 in idle) keeps the tiny
            // bind box, so Unity culls it while the real body fills the screen —
            // a live boss that renders nowhere. Cull on the posed box instead.
            bodyRenderer.localBounds = new Bounds(local.center, local.size * 1.2f);
        }

        if (LoggedAnimation.Add(creature))
        {
            var clip = idle != null
                ? $"{idle.Name} {idle.Duration:0.00}s, {idle.Channels.Count} channel(s)"
                : "missing (bind pose)";
            var animationSource = enemy.animatedMesh;
            Plugin.Logger.LogInfo(
                $"{creature}: idle clip {clip}, animatedMesh " +
                $"{(animationSource != null ? "present" : "missing")}, " +
                $"skin {(model.Gpu ? "GPU" : "CPU")}");
        }

        baseRenderer.enabled = false;

        var instance = new Instance
        {
            Enemy = enemy,
            Model = model,
            Root = root,
            Renderer = bodyRenderer,
            AttackClip = ClipOf(model, "attack"),
            DeathClip = ClipOf(model, "death"),
            Height = targetHeight,
            Phase = UnityEngine.Random.value * 2f,
            LastPosition = enemy.transform.position,
            HasPosition = true,
            LocalFit = root.transform.localScale.x,
            BaseParentScale = enemy.transform.lossyScale.x,
            Creature = creature,
            BossRole = boss,
            ProbeAt = bossTier || boss != null ? Time.time + 0.75f : -1f,
            AttachFrame = Time.frameCount,
            Boss = bossTier || boss != null
        };

        Instances[key] = instance;

        // A swarm spawns its whole pack in one frame: re-sort the budget on the
        // next update instead of drawing every new body until the 0.5 s pass.
        nextBudget = 0f;

        if (boss != null && LoggedPlacements.Add(boss))
        {
            var placed = GltfSkin.ToWorldBounds(local, root.transform);
            Plugin.Logger.LogInfo(
                $"boss {boss} ({creature}{(BossRoster.ModelPending(boss) ? ", model pending" : "")}) " +
                $"on {data.enemyName}: height {(bossTier ? "fixed" : "native")} {targetHeight:0.##} " +
                $"(slot {enemy.GetHeight():0.##}), " +
                $"model y {placed.min.y:0.##}..{placed.max.y:0.##} " +
                $"({placed.size.y:0.##} tall) at pivot y {enemy.transform.position.y:0.##}, " +
                $"renderer enabled {bodyRenderer.enabled}, dissolve cleared");
        }

        if (!SkinPolicyLogged)
        {
            SkinPolicyLogged = true;
            Plugin.Logger.LogInfo(
                $"skin budget: {1f / NearSkinInterval:0} Hz within {NearRadius:0} units, " +
                $"{1f / FarSkinInterval:0} Hz beyond, off-screen enemies keep their last pose");
        }

        var animation = enemy.animatedMesh;
        if (animation != null)
        {
            ByAnimation[animation.GetInstanceID()] = key;
        }

        Plugin.Logger.LogInfo(
            $"{data.enemyName} -> {creature}: {model.Mesh.vertexCount} vertices, " +
            $"{model.Joints.Length} bones, {targetHeight:0.00} units tall, " +
            $"fit {root.transform.localScale.x:0.000} (posed {local.size.y:0.00})");
    }

    /// <summary>Removes the creature. <paramref name="restoreBaseRenderer"/>
    /// re-arms the Megabonk renderer when an enemy returns to the pool, so a
    /// pooled object without a mapping is never invisible.</summary>
    internal static void Detach(Enemy enemy, bool restoreBaseRenderer)
    {
        if (enemy == null)
        {
            return;
        }

        var key = enemy.GetInstanceID();
        if (!Instances.TryGetValue(key, out var instance))
        {
            return;
        }

        Instances.Remove(key);
        Dying.Remove(instance);

        var animation = enemy.animatedMesh;
        if (animation != null)
        {
            ByAnimation.Remove(animation.GetInstanceID());
        }

        DestroyInstance(instance);

        if (restoreBaseRenderer && enemy.renderer != null)
        {
            enemy.renderer.enabled = true;
        }
    }

    private static void DestroyInstance(Instance instance)
    {
        if (instance.DeathMaterial != null)
        {
            UnityEngine.Object.Destroy(instance.DeathMaterial);
            instance.DeathMaterial = null;
        }

        if (instance.Root != null)
        {
            UnityEngine.Object.Destroy(instance.Root);
        }
    }

    /// <summary>The stage's <c>Water</c> component, found on first use and
    /// re-found after a scene change destroys it.</summary>
    private static Water water;
    private static bool waterLookupFailed;

    /// <summary>
    /// The water surface's world Y, when the stage has water. The water is a
    /// visual plane with no collider, so a downward ray goes straight through it
    /// to the seabed — that is why a land creature grounded on the ray stood
    /// under the water surface (the Slime's "sous la map"). Missing or
    /// inaccessible water simply leaves the ray's answer.
    /// </summary>
    private static bool TryWaterLevel(out float level)
    {
        level = 0f;
        if (water == null && !waterLookupFailed)
        {
            try
            {
                water = UnityEngine.Object.FindObjectOfType<Water>();
            }
            catch (Exception error)
            {
                waterLookupFailed = true;
                Plugin.Logger.LogWarning(
                    $"water: the surface is not readable ({error.Message}); creatures ground on the ray only");
            }
        }

        if (water == null || water.waterUpper == null)
        {
            return false;
        }

        level = water.waterUpper.bounds.max.y;
        return true;
    }

    /// <summary>
    /// Terrain height under the enemy: one downward ray from above it, skipping
    /// every collider that belongs to the enemy itself (the pooled collider and
    /// capsule match the hidden native mesh, not ours). The highest hit is the
    /// surface the native enemies stand on.
    /// </summary>
    private static bool TryGround(Enemy enemy, float targetHeight, out float groundY)
    {
        groundY = 0f;
        var origin = enemy.transform.position + Vector3.up * Mathf.Max(2f, targetHeight * 1.5f);

        // The reach is deliberately generous: a flyer's pivot hovers well above
        // the floor (the Bee slot sits at several units) and the old 8-unit ray
        // simply ran out of length, so the ape never grounded at all.
        var reach = Mathf.Max(60f, targetHeight * 6f);
        var hits = Physics.RaycastAll(origin, Vector3.down, reach);
        var ground = float.MinValue;
        var pivot = enemy.transform.position.y;
        foreach (var hit in hits)
        {
            if (hit.collider == null || hit.collider.transform.IsChildOf(enemy.transform))
            {
                continue;
            }

            // Only the floor **under** the enemy counts. Taking the highest hit
            // of the whole ray let a tree canopy, a bridge or a tall prop above
            // the pivot win, and the model rose into the air as the enemy walked
            // under it — the "monsters take off when they come close" report.
            // Walls are excluded by their normal.
            if (hit.point.y > pivot + 0.1f || hit.normal.y < 0.5f)
            {
                continue;
            }

            // Another enemy's capsule (its top normal points up) and the player
            // are not floor: a flyer inside a pack landed on a packmate's head
            // and climbed with the crowd.
            if (hit.collider.GetComponentInParent<Enemy>() != null ||
                hit.collider.GetComponentInParent<MyPlayer>() != null)
            {
                continue;
            }

            ground = Mathf.Max(ground, hit.point.y);
        }

        if (ground <= float.MinValue)
        {
            return false;
        }

        groundY = ground;
        return true;
    }

    /// <summary>Per-frame animation and player-facing updates. Called from the
    /// postfix on <c>AnimatedMesh.Update</c>, which runs for every active enemy.</summary>
    internal static void Tick(AnimatedMesh animation)
    {
        if (animation == null || animation.paused || MyTime.paused)
        {
            return;
        }

        var animationKey = animation.GetInstanceID();
        if (!ByAnimation.TryGetValue(animationKey, out var key))
        {
            return;
        }

        if (!Instances.TryGetValue(key, out var instance))
        {
            ByAnimation.Remove(animationKey);
            return;
        }

        // The component reached this enemy. Record it before the per-frame
        // guard, so the fallback never reports a mesh path that only ran after
        // the fallback had already sampled the same frame.
        instance.MeshTicked = true;

        if (instance.LastTickFrame == Time.frameCount)
        {
            return;
        }

        instance.LastTickFrame = Time.frameCount;
        TickInstance(instance, MyTime.deltaTime);
    }

    /// <summary>
    /// Frame fallback for enemies whose <c>AnimatedMesh.Update</c> never runs:
    /// the arena's final-boss prefab is the one spawn where the game's animation
    /// component may be disabled, and a model with no clip tick stands in its
    /// bind pose (the Maven's T-pose). Ticked from <c>MyPlayer.Update</c> after
    /// <see cref="Tick"/>, so an enemy the component does drive is only sampled
    /// once per frame.
    /// </summary>
    internal static void TickUnanimated()
    {
        if (MyTime.paused || Instances.Count == 0)
        {
            return;
        }

        if (Time.time >= nextBudget)
        {
            nextBudget = Time.time + BudgetInterval;
            ApplyBudget();
        }

        var delta = MyTime.deltaTime;
        if (delta <= 0f)
        {
            return;
        }

        TickBuffer.Clear();
        TickBuffer.AddRange(Instances.Values);
        for (var index = 0; index < TickBuffer.Count; index++)
        {
            var instance = TickBuffer[index];
            if (instance.LastTickFrame == Time.frameCount)
            {
                continue;
            }

            if (!instance.MeshTicked &&
                instance.AttachFrame >= 0 &&
                Time.frameCount - instance.AttachFrame > 6 &&
                LoggedTickSources.Add(instance.Creature ?? "?"))
            {
                Plugin.Logger.LogInfo(
                    $"{instance.Creature}: clips ticked from MyPlayer.Update, " +
                    "AnimatedMesh.Update never drove this enemy");
            }

            instance.LastTickFrame = Time.frameCount;
            TickInstance(instance, delta);
        }
    }

    /// <summary>
    /// Picks the full-rate tier for the bodies closest to the camera
    /// (<see cref="Plugin.MaxLiveModels"/>). Every enemy still draws the PoE
    /// mesh with its own texture — this never falls back to Megabonk's model.
    /// The tier only controls clip sampling (20/8 Hz vs 2 Hz) and the shadow
    /// pass (off past 30 units, off for the reduced tier), which is where a
    /// swarm of hundreds multiplied the frame cost. Bosses and dissolving
    /// corpses always stay in the full tier.
    /// </summary>
    private static void ApplyBudget()
    {
        if (Instances.Count == 0)
        {
            FullCount = 0;
            budgetLogged = false;
            return;
        }

        var cap = Plugin.MaxLiveModels.Value;
        if (cap <= 0 || Instances.Count <= cap)
        {
            SetAllFull();
            return;
        }

        if (!TryCameraPosition(out var camera))
        {
            SetAllFull();
            return;
        }

        BudgetBuffer.Clear();
        foreach (var instance in Instances.Values)
        {
            if (instance.Root == null || instance.Enemy == null)
            {
                continue;
            }

            instance.Distance = (instance.Enemy.transform.position - camera).sqrMagnitude;
            BudgetBuffer.Add(instance);
        }

        BudgetBuffer.Sort((left, right) => left.Distance.CompareTo(right.Distance));

        var full = 0;
        for (var index = 0; index < BudgetBuffer.Count; index++)
        {
            var instance = BudgetBuffer[index];
            var detailed = index < cap || instance.Boss || instance.DeathElapsed >= 0f;
            SetDetail(instance, detailed);
            if (detailed)
            {
                full++;
            }
        }

        FullCount = full;
        if (!budgetLogged)
        {
            budgetLogged = true;
            Plugin.Logger.LogInfo(
                $"model detail: {full} full-rate, {Instances.Count - full} at 2 Hz without shadows " +
                "(all still draw the PoE mesh; the closest bodies keep the full rate)");
        }
    }

    private static void SetAllFull()
    {
        foreach (var instance in Instances.Values)
        {
            SetDetail(instance, true);
        }

        FullCount = Instances.Count;
    }

    /// <summary>Switches one body between the full-rate and reduced tiers.
    /// Nothing is hidden and no native renderer is restored: the PoE mesh and
    /// its texture stay on screen at every distance.</summary>
    private static void SetDetail(Instance instance, bool full)
    {
        if (instance.FullDetail != full)
        {
            instance.FullDetail = full;
            instance.NextSkin = 0f;
        }

        if (instance.Renderer != null)
        {
            instance.Renderer.shadowCastingMode =
                full && instance.Distance <= ShadowRadius * ShadowRadius
                    ? UnityEngine.Rendering.ShadowCastingMode.On
                    : UnityEngine.Rendering.ShadowCastingMode.Off;
        }
    }

    /// <summary>
    /// E1: one line per boss spawn that says whether the attached mesh can be
    /// seen by a camera at all — <c>isVisible</c>, the renderer's world bounds
    /// (the culling box), the pose box, the bone and vertex count, the skin path
    /// and the distance to the camera. The Elder renders nowhere with every other
    /// cause closed (atlas, alpha cut, two-sided, culling box), so this is what
    /// tells the next live run whether the GPU skin path is the remaining fault:
    /// set <c>[Presentation] CpuSkinCreatures = elder</c> and the same line is
    /// printed with <c>skin CPU</c>.
    /// </summary>
    private static void LogRenderProbe(Instance instance)
    {
        var renderer = instance.Renderer;
        var root = instance.Root;
        if (renderer == null || root == null)
        {
            Plugin.Logger.LogInfo(
                $"boss {instance.BossRole ?? instance.Creature} render probe: no renderer " +
                $"(root {(root == null ? "gone" : "present")})");
            return;
        }

        var skinned = renderer as SkinnedMeshRenderer;
        var mesh = skinned != null ? skinned.sharedMesh : null;
        if (mesh == null)
        {
            var filter = renderer.GetComponent<MeshFilter>();
            mesh = filter != null ? filter.sharedMesh : null;
        }

        var bones = 0;
        try
        {
            bones = skinned != null ? skinned.bones.Length : instance.Model.Joints.Length;
        }
        catch (Exception)
        {
            bones = -1;
        }

        var distance = "?";
        if (TryCameraPosition(out var camera))
        {
            distance = Vector3.Distance(camera, root.transform.position).ToString("0.##");
        }

        Plugin.Logger.LogInfo(
            $"boss {instance.BossRole ?? instance.Creature} ({instance.Creature}) render probe: " +
            $"visible {renderer.isVisible}, enabled {renderer.enabled}, " +
            $"skin {(instance.Model.Gpu ? "GPU" : "CPU")}, bones {bones}, " +
            $"verts {(mesh != null ? mesh.vertexCount : 0)}, " +
            $"world bounds {renderer.bounds}, " +
            $"local bounds {(skinned != null ? skinned.localBounds.ToString() : "n/a")}, " +
            $"model {root.transform.position} scale {root.transform.lossyScale}, " +
            $"layer {renderer.gameObject.layer}, camera {distance} away, " +
            $"shader {(renderer.sharedMaterial != null ? renderer.sharedMaterial.shader.name : "none")}");
    }

    private static void TickInstance(Instance instance, float delta)
    {
        var enemy = instance.Enemy;
        if (enemy == null || instance.Root == null)
        {
            Detach(enemy, false);
            return;
        }

        if (enemy.IsDead())
        {
            // The corpse is driven by TickDissolves (the enemy's own animation
            // component stops being useful once death begins).
            return;
        }

        if (delta <= 0f)
        {
            return;
        }

        // E1: probe a boss once, after the attach has had a frame to render. It
        // must run before the isVisible gate below, because that gate is exactly
        // what a boss stuck in a bad culling box would hit.
        if (!instance.ProbeLogged && instance.ProbeAt > 0f && Time.time >= instance.ProbeAt)
        {
            instance.ProbeLogged = true;
            LogRenderProbe(instance);
        }

        var position = enemy.transform.position;
        var speed = 0f;
        if (instance.HasPosition)
        {
            var movement = position - instance.LastPosition;
            movement.y = 0f;
            speed = movement.magnitude / delta;
        }

        instance.LastPosition = position;
        instance.HasPosition = true;

        // A flyer is not corrected here any more: the model rides the enemy
        // pivot exactly as Megabonk's own renderer does, hover included (the
        // flying rule at attach). Grounded enemies are left alone as well —
        // their pivot is the contact, and re-anchoring the whole pack made it
        // climb as it clustered (0.9.14 revert).

        if (instance.Root != null)
        {
            // The game scales the enemy transform itself (rendererScale, elite
            // and spawn-in growth). The fit was computed against the scale live
            // at attach, so an enemy that grows afterwards would carry the model
            // with it — a boss that grows 5x puts the camera inside the body,
            // which reads as an invisible boss. Keep the attached world size.
            var parentScale = enemy.transform.lossyScale.x;
            if (parentScale > 0.0001f &&
                instance.BaseParentScale > 0.0001f &&
                Mathf.Abs(parentScale - instance.BaseParentScale) > 0.001f)
            {
                instance.Root.transform.localScale =
                    Vector3.one * (instance.LocalFit * (instance.BaseParentScale / parentScale));
            }
        }

        // A one-shot attack clip takes over locomotion while it runs; it is
        // sampled unlooped so it holds its last pose instead of snapping back
        // to frame 0.
        GltfSkin.Clip clip = null;
        var time = 0f;
        var loop = true;

        if (instance.AttackElapsed >= 0f)
        {
            instance.AttackElapsed += delta;
            if (instance.AttackClip == null || instance.AttackElapsed >= instance.AttackClip.Duration)
            {
                instance.AttackElapsed = -1f;
            }
            else
            {
                clip = instance.AttackClip;
                time = instance.AttackElapsed;
                loop = false;
            }
        }

        if (clip == null)
        {
            var idle = ClipOf(instance.Model, "idle");
            var walk = ClipOf(instance.Model, "walk");
            var moving = speed > 0.4f && walk != null;
            clip = moving ? walk : idle;
            if (clip == null)
            {
                return;
            }

            if (moving)
            {
                instance.WalkTime += delta * Mathf.Clamp(speed / 6f, 0.7f, 1.8f);
            }
            else
            {
                instance.IdleTime += delta;
            }

            time = (moving ? instance.WalkTime : instance.IdleTime) + instance.Phase;
        }

        // Sampling the clip and uploading the skinned mesh are both per-vertex /
        // per-bone work, so both are throttled together. An enemy outside every
        // camera keeps its last uploaded pose instead of paying for a refresh
        // the player cannot see; the pose resumes when it comes back on screen.
        if (instance.Renderer != null && !instance.Renderer.isVisible)
        {
            return;
        }

        var interval = instance.FullDetail ? NearSkinInterval : LowSkinInterval;
        if (instance.FullDetail &&
            TryCameraPosition(out var camera) &&
            (position - camera).sqrMagnitude > NearRadius * NearRadius)
        {
            interval = FarSkinInterval;
        }

        if (Time.time < instance.NextSkin)
        {
            return;
        }

        var blend = Mathf.Clamp01(1f - Mathf.Exp(-interval * 12f));
        var start = Dev.PerfProbe.BeginSample();
        GltfSkin.Sample(instance.Model, clip, time, blend, loop);
        Dev.PerfProbe.EndSample(start);

        if (!instance.Model.Gpu)
        {
            var skinStart = Dev.PerfProbe.BeginSkin();
            GltfSkin.ApplySkin(instance.Model);
            Dev.PerfProbe.EndSkin(skinStart);
        }

        instance.NextSkin = Time.time + interval;
    }

    /// <summary>
    /// Plays the creature's attack clip once. The event is the game's own hit:
    /// <c>PlayerHealth.DamagePlayer(Enemy, Vector3, DcFlags)</c> carries the
    /// attacking enemy, and <c>Enemy.StartSpecialAttack</c> covers the boss
    /// kits, so the swing is timed to the damage instead of guessed from
    /// distance.
    /// </summary>
    internal static void Attack(Enemy enemy)
    {
        if (enemy == null || !Instances.TryGetValue(enemy.GetInstanceID(), out var instance))
        {
            return;
        }

        // Do not restart a swing that is already playing (contact damage can be
        // reported on consecutive frames) and never animate a corpse.
        if (instance.AttackClip == null || instance.DeathElapsed >= 0f || instance.AttackElapsed >= 0f)
        {
            return;
        }

        instance.AttackElapsed = 0f;
    }

    /// <summary>
    /// Turns a dead enemy's creature into a dissolving corpse instead of making
    /// it vanish: Megabonk's own renderer fades through
    /// <see cref="EnemyDissolve"/> (duration read from the component), which
    /// only knows the hidden native renderer. The creature gets the same
    /// treatment through MK Toon's dissolve keyword plus the creature's own
    /// <c>death</c> clip.
    /// </summary>
    internal static void BeginDeath(Enemy enemy)
    {
        if (enemy == null || !Instances.TryGetValue(enemy.GetInstanceID(), out var instance) ||
            instance.DeathElapsed >= 0f)
        {
            return;
        }

        instance.DeathElapsed = 0f;
        instance.AttackElapsed = -1f;

        // A corpse always stays in the full tier: the dissolve samples its own
        // clip every frame and must not fall back to the 2 Hz rate.
        SetDetail(instance, true);
        instance.DeathPosition = instance.Root != null
            ? instance.Root.transform.position
            : enemy.transform.position;

        var dissolve = enemy.dissolve;
        instance.DissolveSeconds = dissolve != null && dissolve.dissolveDuration > 0.01f
            ? dissolve.dissolveDuration
            : FallbackDissolveSeconds;

        StartDissolve(instance);

        var clipSeconds = instance.DeathClip != null ? instance.DeathClip.Duration : 0f;
        instance.DeathDuration = Mathf.Max(instance.DissolveSeconds + 0.1f, clipSeconds);
        Dying.Add(instance);
    }

    /// <summary>Per-frame corpse update (ticked from <c>MyPlayer.Update</c>, so
    /// it does not depend on the enemy's own animation component still
    /// running).</summary>
    internal static void TickDissolves()
    {
        if (Dying.Count == 0)
        {
            return;
        }

        var delta = Time.deltaTime;
        if (delta <= 0f)
        {
            return;
        }

        for (var index = Dying.Count - 1; index >= 0; index--)
        {
            var instance = Dying[index];
            if (instance.Root == null)
            {
                Dying.RemoveAt(index);
                continue;
            }

            instance.DeathElapsed += delta;
            var progress = Mathf.Clamp01(instance.DeathElapsed / Mathf.Max(0.01f, instance.DissolveSeconds));

            if (instance.DissolveByShader && instance.DeathMaterial != null)
            {
                instance.DeathMaterial.SetFloat("_DissolveAmount", progress);
            }
            else
            {
                // No dissolve funnel on this shader: sink the body instead of
                // leaving it standing.
                instance.Root.transform.position =
                    instance.DeathPosition - Vector3.up * (instance.Height * progress);
            }

            if (instance.DeathClip != null)
            {
                GltfSkin.Sample(instance.Model, instance.DeathClip, instance.DeathElapsed, 1f, false);
                if (!instance.Model.Gpu)
                {
                    var skinStart = Dev.PerfProbe.BeginSkin();
                    GltfSkin.ApplySkin(instance.Model);
                    Dev.PerfProbe.EndSkin(skinStart);
                }
            }

            if (instance.DeathElapsed < instance.DeathDuration)
            {
                continue;
            }

            Dying.RemoveAt(index);

            var enemy = instance.Enemy;
            if (enemy != null)
            {
                Instances.Remove(enemy.GetInstanceID());
                var animation = enemy.animatedMesh;
                if (animation != null)
                {
                    ByAnimation.Remove(animation.GetInstanceID());
                }
            }

            DestroyInstance(instance);
        }
    }

    private static void StartDissolve(Instance instance)
    {
        var renderer = instance.Renderer;
        var source = renderer != null ? renderer.sharedMaterial : null;
        if (source == null)
        {
            return;
        }

        // One clone per corpse: the keyword is material state, so enabling it on
        // the shared body material would dissolve every living enemy too.
        var material = new Material(source) { name = "WraeclastDissolve" };
        if (!material.HasProperty("_DissolveAmount"))
        {
            UnityEngine.Object.Destroy(material);
            LogDissolve(false, source);
            return;
        }

        material.SetFloat("_Dissolve", 1f);
        material.SetFloat("_DissolveAmount", 0f);
        material.SetFloat("_DissolveBorderSize", 0.25f);
        material.EnableKeyword(DissolveKeyword);
        material.DisableKeyword("_MK_DISSOLVE_BORDER_COLOR");
        material.DisableKeyword("_MK_DISSOLVE_BORDER_RAMP");

        // The body style stays authoritative: no outline appears on death.
        material.DisableKeyword("_MK_OUTLINE_HULL_CLIP");
        if (material.HasProperty("_Outline"))
        {
            material.SetFloat("_Outline", 0f);
        }

        if (Plugin.BodyAlphaClipping.Value)
        {
            material.SetFloat("_AlphaClipping", 1f);
            material.SetFloat("_AlphaCutoff", PoeCharacters.BodyAlphaCutoff);
            material.EnableKeyword("_MK_ALPHA_CLIPPING");
        }

        renderer.sharedMaterial = material;
        instance.DeathMaterial = material;
        instance.DissolveByShader = true;
        LogDissolve(true, material);
    }

    private static void LogDissolve(bool byShader, Material material)
    {
        var shader = material != null ? material.shader.name : "?";
        var key = (byShader ? "shader|" : "sink|") + shader;
        if (!LoggedDissolves.Add(key))
        {
            return;
        }

        Plugin.Logger.LogInfo(byShader
            ? $"death dissolve: {shader} + {DissolveKeyword}, duration from EnemyDissolve"
            : $"death dissolve: {shader} has no _DissolveAmount, corpse sinks instead");
    }

    /// <summary>Is this creature named in <c>[Presentation] CpuSkinCreatures</c>
    /// (comma separated, case-insensitive)? An empty list answers false.</summary>
    private static bool ForcesCpuSkin(string creatureId)
    {
        var list = Plugin.CpuSkinCreatures.Value;
        if (string.IsNullOrWhiteSpace(list))
        {
            return false;
        }

        foreach (var part in list.Split(','))
        {
            if (string.Equals(part.Trim(), creatureId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static GltfSkin.Clip ClipOf(GltfSkin.Model model, string name) =>
        model.Clips.TryGetValue(name, out var clip) ? clip : null;

    private static GltfSkin.Model LoadTemplate(string creatureId)
    {
        if (Templates.TryGetValue(creatureId, out var cached) && cached.Mesh != null)
        {
            return cached;
        }

        Templates.Remove(creatureId);
        var path = ModPaths.Creature(creatureId);
        if (!File.Exists(path))
        {
            Plugin.Logger.LogWarning($"Missing creature model: {path} (run mod/tools/prepare-data.ps1)");
            return null;
        }

        try
        {
            // E1: a creature listed in [Presentation] CpuSkinCreatures skips the
            // GPU skin upload, so the same boss can be compared on both paths
            // without costing the bestiary its GPU skinning.
            var model = GltfSkin.Load(path, ForcesCpuSkin(creatureId));
            Templates[creatureId] = model;
            return model;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Creature model failed for {creatureId}: {error}");
            return null;
        }
    }

    /// <summary>
    /// Boss-tier <see cref="EEnemy"/> members, as configured in the game's own
    /// stage data (<c>StageData.asset</c> -> <c>StageTimeline.boss</c> and
    /// <c>StageTimeline.minibosses</c>): Pharaoh1-3 (Desert), Ent1-3
    /// (Forest/Graveyard/Hell) and the minibosses. Which Atlas boss a spawn
    /// wears is <see cref="BossRoster"/>'s job (role and floor); this only
    /// answers "is this slot boss-tier".
    /// </summary>
    internal static bool IsBossTier(EEnemy kind)
    {
        switch (kind)
        {
            case EEnemy.Ent1:
            case EEnemy.Ent2:
            case EEnemy.Ent3:
            case EEnemy.MinibossGolem:
            case EEnemy.MinibossGolemSand:
            case EEnemy.Pharaoh1:
            case EEnemy.Pharaoh2:
            case EEnemy.Pharaoh3:
            case EEnemy.CalciumDad:
            case EEnemy.MinibossPig:
            case EEnemy.MinibossScorpion:
            case EEnemy.Ghostham:
                return true;

            default:
                return false;
        }
    }

    /// <summary>Which family a flying slot wears (S7b). The spectral slots take
    /// the Tormented Spirit; the Bee is a small airborne pest and wears the
    /// client's bat instead.</summary>
    private static string FlyingCreatureOf(EEnemy kind)
    {
        return kind == EEnemy.Bee ? "bats" : "tormentedspirit";
    }

    /// <summary>
    /// Megabonk bestiary -> PoE creature family. This mapping is ours: Megabonk
    /// data carries no creature correspondence, only an enemy enum. The rule is
    /// silhouette and role, not exact PoE mob identity. A boss-tier spawn wears
    /// the mesh its <see cref="BossRoster"/> role names.
    /// </summary>
    private static string CreatureOf(EEnemy kind, string boss)
    {
        if (boss != null)
        {
            return BossRoster.Model(boss);
        }

        switch (kind)
        {
            // Skeletons, mummies and graveyard bones.
            case EEnemy.Skeleton:
            case EEnemy.GoldenSkeleton:
            case EEnemy.XpSkeleton:
            case EEnemy.ArmoredSkeleton:
            case EEnemy.SkeletonDusty:
            case EEnemy.ArmoredSkeletonDusty:
            case EEnemy.SkeletonMage:
            case EEnemy.Mummy:
            case EEnemy.MummyTank:
            case EEnemy.MummyAncient:
            case EEnemy.Tumblebone:
            case EEnemy.Head:
            case EEnemy.GhostGrave1:
            case EEnemy.GhostGrave2:
            case EEnemy.GhostGrave3:
            case EEnemy.GhostGrave4:
                return "skeletons";

            // Undead corpses: the zombie and ghoul slots, which wore the goatman
            // (a horned humanoid) until S7.
            case EEnemy.Ghoul:
            case EEnemy.Zombie:
                return "zombies";

            // Humanoids that are alive and human-sized: bandits and goblins, which
            // are PoE's own goatman archetype.
            case EEnemy.Bandit:
            case EEnemy.Goblin:
            case EEnemy.GoblinStrong:
            case EEnemy.GoblinTank:
                return "goatman";

            // Spectral bodies (S7): the wraith, not the walking bone cultist.
            case EEnemy.Ghost:
            case EEnemy.GreaterGhost:
            case EEnemy.GhostPurple:
            case EEnemy.GhostInvincible:
            case EEnemy.GhostKing:
                return "ghosts";

            // Burning spirits; the one family that reads as airborne. The Bee
            // slot is a small flying enemy too, so it wears the floating
            // elemental instead of the blood ape (the ape stands on all fours
            // and read as a bug the moment its pivot hovered).
            case EEnemy.Wisp:
            case EEnemy.GhostRed:
            case EEnemy.Bee:
                return "fireelemental";

            // The Slime: a hound (the client's Hound rig), because the blood ape
            // fitted to the Slime's 2.8-unit slot read as a tiny crouched blob.
            case EEnemy.Slime:
                return "wolves";

            // Low, four-legged and vermin-shaped enemies.
            case EEnemy.Scorpion:
            case EEnemy.ScorpionMedium:
            case EEnemy.CactusShooter:
            case EEnemy.FrogGreen:
            case EEnemy.FrogBlue:
            case EEnemy.FrogRed:
            case EEnemy.Bush:
                return "beasts";

            // The one spider: it explodes, so it must not look like the pack
            // around it (silhouette rule: spider -> spider).
            case EEnemy.BoomerSpider:
                return "spiders";

            // Golems and test dummies.
            case EEnemy.Golem:
            case EEnemy.Test:
            case EEnemy.Test2:
                return "stonegolem";

            default:
                return null;
        }
    }
}
