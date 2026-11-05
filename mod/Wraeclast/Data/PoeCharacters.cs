using System;
using System.Collections.Generic;
using System.IO;
using Assets.Scripts.Actors.Player;
using UnityEngine;
using Wraeclast.Skills;

namespace Wraeclast.Data;

/// <summary>
/// The PoE character body in game: a real skinned mesh with its own skeleton
/// and clips from the class GLB, driven by the player's speed (idle / walk).
///
/// The Megabonk body stays hidden underneath — animator, collider, hat socket
/// and damage flash keep working — while this model supplies the visuals.
/// Instances are keyed by <see cref="PlayerRenderer"/> so the menu previews and
/// the live player each animate on their own.
/// </summary>
internal static class PoeCharacters
{
    private const string ChildName = "WraeclastModel";

    private sealed class Instance
    {
        internal GltfSkin.Model Model;
        internal GameObject Root;
        internal string Label;
        internal float IdleTime;
        internal float WalkTime;
        internal Vector3 LastPosition;
        internal bool HasPosition;
        internal float Spin;
        internal float Crouch;
        internal float Height;
        internal bool CrouchLogged;

        /// <summary>R11b: the rising edge of a slide/dash, so Smouldering Step
        /// leaves one patch per dash instead of one per frame.</summary>
        internal bool WasDashing;
    }

    private static readonly Dictionary<int, Instance> Instances = new Dictionary<int, Instance>();
    private static readonly Dictionary<string, GltfSkin.Model> Cache =
        new Dictionary<string, GltfSkin.Model>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LoggedMaterials = new HashSet<string>();
    private static readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>();

    /// <summary>A2e: the host body's own material, kept the first time a body is
    /// swapped. It is the source <see cref="BuildMaterial"/> clones for hair and
    /// cloth cards, so it is also the one source known to alpha-test with values
    /// only — which is what a prop's foliage cards need.</summary>
    internal static Material HostBodyMaterial;

    /// <summary>Every exported body GLB declares <c>alphaMode: MASK</c> with
    /// <c>alphaCutoff: 0.35</c> (measured on all 16 files), which is how the PoE
    /// client cuts the hair and cloth cards out of the atlas.</summary>
    internal const float BodyAlphaCutoff = 0.35f;

    /// <summary>
    /// 0.9.115: drop every cached material at the run reset. The cache is keyed
    /// by Unity **InstanceIDs** and outlives the scene, and Unity reuses those
    /// IDs after a reload: a chest of a restarted run was handed the clone built
    /// for the previous run's chest, whose albedo texture no longer existed, so
    /// it drew black. Nothing is destroyed here — a renderer that still holds an
    /// entry keeps drawing it — the next build just starts from live sources.
    /// </summary>
    internal static void ClearMaterials()
    {
        if (Materials.Count == 0)
        {
            return;
        }

        var count = Materials.Count;
        Materials.Clear();
        Plugin.Logger.LogInfo($"materials: {count} cached material(s) dropped for the new run");
    }

    /// <summary>The slide pose bends the body around the right axis into a
    /// knee-slide: the thighs come forward just enough for the knees to lead,
    /// the shins fold fully back under the body and the torso reclines — the
    /// operator's "assis sur les genoux, penché vers l'arrière".</summary>
    private const float SlideHipDegrees = 10f;
    private const float SlideKneeDegrees = -110f;
    private const float SlideSpineDegrees = 55f;
    private const float SlideBlendRate = 7f;

    /// <summary>S10b: folding the shins back lifts the whole body, and the knees
    /// become its lowest point, so the root is lowered by this share of the
    /// fitted body height to put them on the floor. Measured offline on two
    /// rigs: 0.437 m of lift on a 1.568/1.581 m model (27.6 %) and 0.454 m on
    /// 1.683 m (27.0 %).</summary>
    private const float SlideDropShare = 0.273f;

    private static GltfSkin.Clip ClipOf(GltfSkin.Model model, string name) =>
        model.Clips.TryGetValue(name, out var clip) ? clip : null;

    internal static void Attach(PlayerRenderer renderer, ClassDefinition definition, CharacterData data)
    {
        var template = LoadTemplate(definition.Id);
        if (template == null)
        {
            Plugin.Logger.LogWarning($"{definition.Name}: no skinned model, keeping the Megabonk body");
            return;
        }

        var key = renderer.GetInstanceID();
        if (Instances.TryGetValue(key, out var existing) && existing.Root != null)
        {
            UnityEngine.Object.Destroy(existing.Root);
        }

        // One instance per renderer: the menu preview and the live player each
        // own their bones, so they animate independently.
        var model = GltfSkin.Clone(template);

        var body = renderer.renderer;
        var targetHeight = body != null ? body.bounds.size.y : data.colliderHeight;

        var root = GltfSkin.Instantiate(model, renderer.transform);
        var meshObject = root.transform.Find("Body");
        var bodyRenderer = meshObject != null ? meshObject.GetComponent<Renderer>() : null;
        if (bodyRenderer != null)
        {
            // A2e: the host body material is the one shader this build has been
            // shown to alpha-test with values only (`_AlphaClipping`/`_AlphaCutoff`,
            // the hair cards cut in 0.8.x). A terrain prop's shader usually does
            // not carry that property, so its foliage cards would draw opaque and
            // show their near-black surround as squares; PropSwap borrows this
            // material for those submeshes.
            if (body != null && body.sharedMaterial != null)
            {
                HostBodyMaterial = body.sharedMaterial;
            }

            var bodyMaterial = BuildMaterial(body, model.Texture);
            if (bodyMaterial != null)
            {
                bodyRenderer.sharedMaterial = bodyMaterial;
            }
        }

        // Idle pose first: the pose bounds are the real size (the GLB root
        // carries the skeleton's own scale), the raw bind pose is not.
        var local = new Bounds(Vector3.zero, Vector3.zero);
        var idle = ClipOf(model, "idle");
        if (idle != null)
        {
            GltfSkin.Sample(model, idle, 0.4f, 1f);
            local = GltfSkin.PoseBounds(model);
        }

        // The GLB builder already stands every model up with the scene root's
        // Rx(+90) (the runtime instantiates that node with the rest of the
        // hierarchy), so the pose bounds are Y-up and only the yaw is ours. The
        // old "longest axis is Z, so fix it" guess laid quadrupeds on their
        // side, because a crouched ape is deeper than it is tall.
        root.transform.localRotation = Quaternion.Euler(0f, Plugin.ModelYaw.Value, 0f);
        root.transform.localScale = Vector3.one;
        root.transform.localPosition = Vector3.zero;

        // Fit and ground the model from its measured pose bounds: no guessed
        // axis, pivot or scale.
        var bounds = GltfSkin.ToWorldBounds(local, root.transform);
        var scale = bounds.size.y > 0.01f && targetHeight > 0.1f ? targetHeight / bounds.size.y : 1f;
        root.transform.localScale = Vector3.one * scale;

        bounds = GltfSkin.ToWorldBounds(local, root.transform);
        root.transform.position += renderer.transform.position -
                                   new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);

        if (!model.Gpu)
        {
            GltfSkin.ApplySkin(model);
        }

        Instances[key] = new Instance { Model = model, Root = root, Label = definition.Name, Height = targetHeight };

        Plugin.Logger.LogInfo(
            $"{definition.Name}: skinned model {model.Mesh.vertexCount} vertices, " +
            $"{model.Joints.Length} bones, clips [{string.Join(", ", model.Clips.Keys)}], " +
            $"skin {(model.Gpu ? "GPU" : "CPU")}, " +
            $"height {targetHeight:0.00} units, fit {scale:0.000}, " +
            $"yaw {Plugin.ModelYaw.Value:0} (must be 180: the GLB faces -Z)");

        HideHostMeshes(renderer, definition.Name);
        WeaponModels.Attach(model, root, body, definition);
    }

    /// <summary>The instantiated body root of a renderer, so a caller can ask
    /// where a held weapon is (the Fireball is cast from the main hand).</summary>
    internal static GameObject BodyRoot(PlayerRenderer renderer)
    {
        if (renderer == null)
        {
            return null;
        }

        return Instances.TryGetValue(renderer.GetInstanceID(), out var instance) ? instance.Root : null;
    }

    internal static void Detach(PlayerRenderer renderer)
    {
        var key = renderer.GetInstanceID();
        if (!Instances.TryGetValue(key, out var instance))
        {
            return;
        }

        if (instance.Root != null)
        {
            WeaponModels.Forget(instance.Root);
            UnityEngine.Object.Destroy(instance.Root);
        }

        Instances.Remove(key);
    }

    /// <summary>
    /// Hide the host's own handheld meshes. Megabonk parents a character's
    /// weapons to the skeleton hand bones (<c>RobotCowboy/Armature/Hips/.../
    /// Hand.R/B_revolver</c>), as separate <see cref="MeshRenderer"/> objects —
    /// disabling the body's skinned renderer leaves them floating on the PoE
    /// skeleton. The scan is scoped to the character prefab, never the player
    /// root, so the minimap icon, aim arrows and particle systems are untouched.
    /// </summary>
    internal static void HideHostMeshes(PlayerRenderer renderer, string label)
    {
        var root = CharacterRoot(renderer);
        if (root == null)
        {
            return;
        }

        var hidden = new List<string>();
        HideHostMeshes(root, hidden);
        if (hidden.Count > 0)
        {
            Plugin.Logger.LogInfo(
                $"{label}: host meshes hidden: {hidden.Count} [{string.Join(", ", hidden)}]");
        }
    }

    /// <summary>The host reassigns hat and cosmetic meshes after the body swap
    /// (<c>PlayerRenderer.SetHat</c>), which re-enables them; hide them again so
    /// no Megabonk accessory is drawn over the PoE body.</summary>
    internal static void HideHostMeshesAfterHat(PlayerRenderer renderer)
    {
        if (renderer == null || !Instances.TryGetValue(renderer.GetInstanceID(), out var instance))
        {
            return;
        }

        HideHostMeshes(renderer, instance.Label);
    }

    /// <summary>The prefab object spawned by <c>PlayerRenderer</c>: the ancestor
    /// of the body renderer directly under the player transform.</summary>
    private static Transform CharacterRoot(PlayerRenderer renderer)
    {
        var body = renderer != null ? renderer.renderer : null;
        if (body == null)
        {
            return null;
        }

        var target = body.transform;
        for (var depth = 0; depth < 8 && target.parent != null && target.parent != renderer.transform; depth++)
        {
            target = target.parent;
        }

        return target.parent == renderer.transform ? target : null;
    }

    private static void HideHostMeshes(Transform parent, List<string> hidden)
    {
        for (var index = 0; index < parent.childCount; index++)
        {
            var child = parent.GetChild(index);
            if (child == null || string.Equals(child.name, ChildName, StringComparison.Ordinal))
            {
                continue;
            }

            var mesh = child.GetComponent<MeshRenderer>();
            if (mesh != null && mesh.enabled)
            {
                mesh.enabled = false;
                hidden.Add(child.name);
            }

            // R11b item 14: a host cosmetic can be a particle effect rather than
            // a mesh. Vlad's (BloodMage) prefab carries `ShadowMissileRed` and
            // `ShadowMissileRed (1)` — two ParticleSystemRenderer orbs on the
            // wrists — so the mesh-only scan left them orbiting the PoE body.
            // The renderer is a Renderer, not a MeshRenderer, and disabling it
            // is what hides the balls; the system is also stopped so it does not
            // keep simulating invisible particles.
            var particles = child.GetComponent<ParticleSystemRenderer>();
            if (particles != null && particles.enabled)
            {
                particles.enabled = false;
                StopParticles(child);
                hidden.Add($"{child.name} (particle)");
            }

            HideHostMeshes(child, hidden);
        }
    }

    /// <summary>Stops a hidden host effect, wrapped because a renderer-only
    /// effect may carry no ParticleSystem and the interop binding must never
    /// take the attach down.</summary>
    private static void StopParticles(Transform child)
    {
        try
        {
            var system = child.GetComponent<ParticleSystem>();
            if (system != null)
            {
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }
        catch (Exception)
        {
            // The renderer is already off; the effect stays invisible.
        }
    }

    internal static void SetSpin(PlayerRenderer renderer, float angle)
    {
        if (renderer == null || !Instances.TryGetValue(renderer.GetInstanceID(), out var instance) || instance.Root == null)
        {
            return;
        }

        instance.Spin = angle;
        instance.Root.transform.localRotation = Quaternion.Euler(0f, Plugin.ModelYaw.Value + angle, 0f);
    }

    /// <summary>The fitted body height (the host collider the model was scaled
    /// to) for a swapped character, 0 when it is not mounted. R11b: Twin Orbit
    /// orbits at the body's pivot, so it needs the real height, not the host
    /// <c>MyPlayer.height</c> value.</summary>
    internal static float HeightOf(PlayerRenderer renderer)
    {
        if (renderer == null || !Instances.TryGetValue(renderer.GetInstanceID(), out var instance))
        {
            return 0f;
        }

        return instance.Height;
    }

    /// <summary>How far the body is into the slide pose (0..1), read from the
    /// host's own slide and dash state so the rig follows the player's move
    /// instead of a guess at the speed.</summary>
    private static float UpdateCrouch(Instance instance, float delta)
    {
        var sliding = false;
        try
        {
            var movement = PlayerMovement.Instance;
            sliding = movement != null && (movement.IsSliding() || movement.isDashing);
        }
        catch (Exception error)
        {
            if (!instance.CrouchLogged)
            {
                instance.CrouchLogged = true;
                Plugin.Logger.LogWarning($"slide pose: the player movement state is not readable: {error.Message}");
            }
        }

        instance.Crouch = Mathf.MoveTowards(instance.Crouch, sliding ? 1f : 0f, delta * SlideBlendRate);

        if (sliding && !instance.WasDashing)
        {
            PoeSkills.DashTrail(MyPlayer.Instance);
        }

        instance.WasDashing = sliding;

        if (instance.Crouch <= 0.001f)
        {
            return 0f;
        }

        if (!instance.CrouchLogged)
        {
            instance.CrouchLogged = true;
            Plugin.Logger.LogInfo(
                $"slide pose: knee-slide crouch on {instance.Label} " +
                $"(thigh {SlideHipDegrees:0} deg, knee {SlideKneeDegrees:0} deg, spine {SlideSpineDegrees:0} deg, " +
                $"drop {instance.Height * SlideDropShare:0.00})");
        }

        return instance.Crouch;
    }

    private static void ApplySlide(GltfSkin.Model model, Vector3 axis, float weight)
    {
        var hip = SlideHipDegrees * weight;
        var knee = SlideKneeDegrees * weight;
        var spine = SlideSpineDegrees * weight;

        GltfSkin.BendBone(model, "L_Hip", axis, hip);
        GltfSkin.BendBone(model, "R_Hip", axis, hip);
        GltfSkin.BendBone(model, "L_Knee", axis, knee);
        GltfSkin.BendBone(model, "R_Knee", axis, knee);
        GltfSkin.BendBone(model, "Spine1", axis, spine * 0.4f);
        GltfSkin.BendBone(model, "Spine2", axis, spine * 0.4f);
        GltfSkin.BendBone(model, "Spine3", axis, spine * 0.2f);
    }

    /// <summary>Per-frame animation: walk when the character moves, idle when it
    /// stands still. The blend factor smooths clip switches.</summary>
    internal static void Tick(PlayerRenderer renderer, float delta)
    {
        if (renderer == null || delta <= 0f ||
            !Instances.TryGetValue(renderer.GetInstanceID(), out var instance) ||
            instance.Root == null)
        {
            return;
        }

        // S10b: undo the previous frame's slide bend before sampling, otherwise
        // each frame's bend lands on the last bent pose and the body rolls.
        GltfSkin.RestoreBends(instance.Model);

        var position = renderer.transform.position;
        var speed = 0f;
        if (instance.HasPosition)
        {
            var movement = position - instance.LastPosition;
            movement.y = 0f;
            speed = movement.magnitude / delta;
        }

        instance.LastPosition = position;
        instance.HasPosition = true;

        var walk = ClipOf(instance.Model, "walk");
        var idle = ClipOf(instance.Model, "idle");
        var moving = speed > 0.4f && walk != null;

        var crouch = UpdateCrouch(instance, delta);
        var sliding = crouch > 0.001f;

        // S10b: the crouch is a held pose, so its base clip must not advance.
        // A walk cycle whose legs swing once per stride under the bend is the
        // "roulades en boucle" the operator saw; the idle stance is symmetrical,
        // so both thighs bend into the same knee-slide.
        var clip = sliding ? (idle ?? walk) : (moving ? walk : idle);
        if (clip == null)
        {
            return;
        }

        if (!sliding)
        {
            if (clip == walk)
            {
                instance.WalkTime += delta * Mathf.Clamp(speed / 6f, 0.7f, 1.8f);
            }
            else
            {
                instance.IdleTime += delta;
            }
        }

        var blend = Mathf.Clamp01(1f - Mathf.Exp(-delta * 12f));
        var start = Dev.PerfProbe.BeginSample();
        GltfSkin.Sample(instance.Model, clip, clip == walk ? instance.WalkTime : instance.IdleTime, blend);
        Dev.PerfProbe.EndSample(start);

        if (sliding)
        {
            ApplySlide(instance.Model, instance.Root.transform.right, crouch);
        }

        instance.Root.transform.localPosition = new Vector3(0f, -instance.Height * SlideDropShare * crouch, 0f);

        if (!instance.Model.Gpu)
        {
            var skinStart = Dev.PerfProbe.BeginSkin();
            GltfSkin.ApplySkin(instance.Model);
            Dev.PerfProbe.EndSkin(skinStart);
        }

        // A weapon is posed in body space (classes.json `poses`), so it is
        // rewritten every frame, after the bones carry this frame's pose:
        // setting it only on the idle↔walk transition let the hand's bone carry
        // the weapon away as the new clip played, so stopping kept the run
        // angle instead of returning to the idle one.
        WeaponModels.SetPose(instance.Root, moving ? "walk" : "idle");

        if (instance.Spin != 0f)
        {
            instance.Root.transform.localRotation = Quaternion.Euler(0f, Plugin.ModelYaw.Value + instance.Spin, 0f);
        }
    }

    private static GltfSkin.Model LoadTemplate(string classId)
    {
        if (Cache.TryGetValue(classId, out var cached) && cached.Mesh != null)
        {
            return cached;
        }

        Cache.Remove(classId);
        var path = ModPaths.Model(classId);
        if (!File.Exists(path))
        {
            Plugin.Logger.LogWarning($"Missing model: {path} (run mod/tools/prepare-data.ps1)");
            return null;
        }

        try
        {
            var model = GltfSkin.Load(path);
            Cache[classId] = model;
            return model;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Skinned model failed for {classId}: {error}");
            return null;
        }
    }

    internal static Material BuildMaterial(Renderer body, Texture2D texture, bool alphaCut = false) =>
        BuildMaterial(body != null ? body.sharedMaterial : null, texture, alphaCut);

    /// <summary>The same recipe from a bare source material: used by the prop
    /// pass, which has no renderer to borrow from (a foliage submesh takes the
    /// host body's material, see <see cref="HostBodyMaterial"/>).</summary>
    internal static Material BuildMaterial(Material source, Texture2D texture, bool alphaCut = false)
    {
        // Shared material per (source material, texture, style): dozens of live
        // enemies drawn with one material instead of one clone each. The style
        // flags are part of the key so a config change never hands out a
        // material built for the other look, and so an alpha-cut card never
        // borrows the opaque clone of the same atlas.
        var key = (source != null ? source.GetInstanceID() : 0) + "|" +
                  (texture != null ? texture.GetInstanceID() : 0) + "|" +
                  (Plugin.BodyAlphaClipping.Value ? "clip" : "solid") + "|" +
                  Plugin.OutlineMode.Value + "|" + Plugin.OutlineSize.Value + "|" +
                  (alphaCut ? "cut" : "opaque");
        if (Materials.TryGetValue(key, out var cached) && cached != null)
        {
            return cached;
        }

        Material material;
        if (source != null)
        {
            // Exactly the combination validated on screen in 0.7.x: the host
            // material with its own shader and keywords, our atlas on the albedo
            // aliases, and values only. Do NOT assign `Material.shader` (it
            // resets the property block and keyword set: that is what turned the
            // bodies white in 0.8.7, and the outline-free shader never sampled
            // our atlas) and do NOT touch keywords (this interop throws on the
            // getters and the sets have no compiled variants).
            material = new Material(source);
        }
        else
        {
            // No host material to borrow from: Unlit/Texture is in every build
            // and samples _MainTex with no keywords, so at least the atlas is
            // shown instead of the error shader.
            var unlit = Shader.Find("Unlit/Texture");
            material = unlit != null ? new Material(unlit) : null;
            Plugin.Logger.LogWarning("body material: no host material, drawing the atlas with Unlit/Texture");
            if (material == null)
            {
                return null;
            }
        }

        material.name = "WraeclastBodyMaterial";
        try
        {
            ApplySurface(material, texture);
            if (alphaCut)
            {
                ApplyCutout(material);
            }
        }
        catch (Exception error)
        {
            // Never let a material step abort the body swap: a renderer with no
            // material is the white character.
            Plugin.Logger.LogWarning($"body material paint failed: {error.Message}");
        }

        Report(material, source, texture);
        Materials[key] = material;
        return material;
    }

    /// <summary>
    /// Values only: white tint, black emission, the atlas on every alias MK Toon
    /// reads, and the GLB's alpha cutoff. No keyword calls and no shader swap —
    /// the host material already has <c>_MK_ALBEDO_MAP</c> on because it samples
    /// its own atlas, and assigning a shader or a keyword is what broke the
    /// bodies (reset variant / missing variant / aborted attach).
    ///
    /// The outline is exposed as two config knobs (<c>[Presentation] OutlineMode</c>,
    /// <c>OutlineSize</c>, both <c>-1</c> = leave the host values): the host
    /// shader carries an outline pass, and a size of 0 makes that hull coincide
    /// with the body and z-fight it black, so nothing is changed by default.
    /// </summary>
    internal static void ApplySurface(Material material, Texture2D texture)
    {
        Paint(material, "_Color", Color.white);
        Paint(material, "_AlbedoColor", Color.white);
        Paint(material, "_EmissionColor", Color.black);
        Paint(material, "_AlbedoMapIntensity", 1f);

        if (texture != null)
        {
            material.mainTexture = texture;
            Paint(material, "_MainTex", texture);
            Paint(material, "_AlbedoMap", texture);
            Paint(material, "_BaseMap", texture);
        }

        if (material.HasProperty("_AlphaClipping"))
        {
            material.SetFloat("_AlphaClipping", Plugin.BodyAlphaClipping.Value ? 1f : 0f);
            Paint(material, "_AlphaCutoff", BodyAlphaCutoff);
        }

        if (Plugin.OutlineMode.Value >= 0)
        {
            Paint(material, "_Outline", Plugin.OutlineMode.Value);
        }

        if (Plugin.OutlineSize.Value >= 0f)
        {
            Paint(material, "_OutlineSize", Plugin.OutlineSize.Value);
        }
    }

    /// <summary>
    /// A2e: draw one submesh as a cut-out card. Values only, like
    /// <see cref="ApplySurface"/>: <c>_AlphaClipping</c>/<c>_AlphaCutoff</c> is
    /// the client's own cut (the grid in a leaf sheet is transparent), and
    /// <c>_RenderFace</c> 0 keeps the back of a card visible — MK Toon culls
    /// front faces by default, the same property the cloth creatures set.
    /// </summary>
    private static void ApplyCutout(Material material)
    {
        if (material.HasProperty("_AlphaClipping"))
        {
            material.SetFloat("_AlphaClipping", 1f);
        }

        if (material.HasProperty("_AlphaCutoff"))
        {
            material.SetFloat("_AlphaCutoff", BodyAlphaCutoff);
        }

        if (material.HasProperty("_RenderFace"))
        {
            material.SetFloat("_RenderFace", 0f);
        }
    }

    private static void Paint(Material material, string property, Color colour)
    {
        if (material.HasProperty(property))
        {
            material.SetColor(property, colour);
        }
    }

    private static void Paint(Material material, string property, Texture texture)
    {
        if (material.HasProperty(property))
        {
            material.SetTexture(property, texture);
        }
    }

    internal static void Paint(Material material, string property, float value)
    {
        if (material.HasProperty(property))
        {
            material.SetFloat(property, value);
        }
    }

    /// <summary>
    /// Logs the shader and albedo binding once per (source material, shader)
    /// pair, so a run shows what the body is actually drawn with without a line
    /// per enemy spawn. Nothing here may throw: a logging failure used to abort
    /// the whole attach and leave the mesh with no material (white body).
    /// </summary>
    private static void Report(Material material, Material source, Texture2D texture)
    {
        try
        {
            var shader = material.shader != null ? material.shader.name : "<none>";
            var key = shader + "|" + (source != null ? source.name : "<none>");
            if (!LoggedMaterials.Add(key))
            {
                return;
            }

            var albedo = material.HasProperty("_AlbedoMap") ? material.GetTexture("_AlbedoMap") : null;
            Plugin.Logger.LogInfo(
                $"body material: shader={shader} source={source?.name ?? "<none>"} " +
                $"albedo={albedo?.name ?? "<none>"} texture={texture?.name ?? "<none>"} " +
                $"outlineMode={Plugin.OutlineMode.Value} outlineSize={Plugin.OutlineSize.Value} " +
                $"alphaClipping={Plugin.BodyAlphaClipping.Value} " +
                $"tint={material.HasProperty("_AlbedoColor")} props={material.GetTexturePropertyNames().Length}");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"body material report failed: {error.Message}");
        }
    }
}
