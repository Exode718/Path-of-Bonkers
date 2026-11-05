using System;
using System.Collections.Generic;
using System.IO;
using Assets.Scripts.Managers;
using Assets.Scripts.Utility;
using UnityEngine;

namespace Wraeclast.Data;

/// <summary>
/// A5.1 — the shady-guy merchant wears a PoE town NPC.
///
/// Megabonk spawns him from the merchant chain as an <c>InteractableShadyGuy</c>
/// whose body is a <c>SkinnedMeshRenderer</c> in <c>meshRenderer</c> (the A5.0
/// register named the prefab `ShadyGuy` and its 5.02 × 5.25 × 3.96 m body box).
/// Only that renderer's art changes: the client's <c>merchants</c> model
/// (Tarkleigh, one idle clip) is parented to the merchant, fitted inside the
/// native body box, and the native renderer is hidden. The shop itself — items,
/// prices, rarity, smoke, icons, interaction — stays Megabonk's, exactly like
/// the A3 rules.
///
/// The model is **skinned and animated**: the merchant is a standing figure,
/// and `GltfSkin.Sample` advances its single idle clip from `MyPlayer.Update`
/// (the same clock the skills and the props use). One template is loaded once;
/// every merchant clone shares its mesh and texture and owns its own skeleton.
/// </summary>
internal static class MerchantSwap
{
    /// <summary>Child that carries our model, so a second sweep over the same
    /// merchant cannot stack two NPCs.</summary>
    private const string ChildName = "WraeclastMerchant";

    /// <summary>The GLB `build-glb.mjs#merchants` writes (Tarkleigh v2).</summary>
    private const string ModelId = "merchants";

    /// <summary>How often the net looks for a merchant the `Start` hook missed:
    /// the merchant is spawned and destroyed through the shop chain, so it can
    /// appear long after the floor is built.</summary>
    private const float SweepSeconds = 1f;

    /// <summary>Which of the model's clips is the standing loop.</summary>
    private const string IdleClip = "idle";

    /// <summary>
    /// Smallest native body height that can be the **drawn** merchant. At
    /// `Start` the `SkinnedMeshRenderer` still answers its **bind-pose** box —
    /// measured on this prefab as 7.02 × **1.72** × 7.00 m, i.e. the Z-up mesh's
    /// depth on the height axis — while the drawn body is 5.02 × 5.25 × 3.96 m
    /// (the A5.0 register). Fitting to the bind box gave the operator's “trop
    /// petit 1.72 m NPC; the mount is therefore deferred until the renderer
    /// reports a box that can only be the drawn body, then it uses that box for
    /// the fit and the centre.
    /// </summary>
    private const float MinBodyHeight = 2f;

    private sealed class Instance
    {
        internal GltfSkin.Model Model;
        internal GltfSkin.Clip Clip;
        internal Transform Root;
        internal Transform Holder;
        internal Vector3 Offset;
        internal float Yaw;
        internal float Time;
    }

    private static readonly Dictionary<int, Instance> Instances = new Dictionary<int, Instance>();
    private static readonly HashSet<string> PlaceLogged = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Merchants seen before their native body had drawn a real box;
    /// retried every tick, so the client NPC appears the first frame the fit can
    /// be measured instead of one sweep later (and the native body is hidden at
    /// that same moment).</summary>
    private static readonly List<InteractableShadyGuy> Pending = new List<InteractableShadyGuy>();

    private static GltfSkin.Model template;
    private static bool templateTried;
    private static float nextSweep;

    internal static bool Active => Plugin.ReplaceMerchant.Value;

    /// <summary>Logged once at load, like every pass that hangs off one hook: a
    /// floor where the hook never fires must not read like a working feature.</summary>
    internal static void Mount()
    {
        if (!Active)
        {
            Plugin.Logger.LogInfo("merchant: merchant pass off ([Props] ReplaceMerchant), Megabonk's shady guy stays");
            return;
        }

        Plugin.Logger.LogInfo(
            "merchant: pass mounted on InteractableShadyGuy.Start (postfix) with a once-a-second sweep, and " +
            "its model ticks from MyPlayer.Update; only the body renderer changes, the shop, icons, smoke " +
            "and interaction stay Megabonk's");
    }

    /// <summary>Animates every live merchant and, once a second, mounts the ones
    /// the `Start` hook could not see yet. A destroyed merchant's root nulls out
    /// and its instance is dropped.</summary>
    internal static void Tick()
    {
        if (!Active)
        {
            return;
        }

        try
        {
            // A merchant seen before its native body drew is retried every frame,
            // so the client NPC (and the hiding of the native one) waits only for
            // the first real box, not for the one-second sweep.
            if (Pending.Count > 0)
            {
                for (var index = Pending.Count - 1; index >= 0; index--)
                {
                    var merchant = Pending[index];
                    if (merchant == null || Apply(merchant))
                    {
                        Pending.RemoveAt(index);
                    }
                }
            }

            if (Instances.Count > 0)
            {
                var gone = default(List<int>);
                foreach (var pair in Instances)
                {
                    var instance = pair.Value;
                    if (instance.Root == null || instance.Model == null)
                    {
                        gone ??= new List<int>();
                        gone.Add(pair.Key);
                        continue;
                    }

                    // The merchant spawner may set the prefab root's rotation
                    // (the terrain alignment its own props use) *after* our
                    // `Start` postfix ran, which tilted the model with it — the
                    // operator's face-down second look. Re-assert the world
                    // orientation every tick from the holder's ground-projected
                    // forward, so no inherited or later parent rotation can
                    // reach the model. Position too: a parent rotation also
                    // swings the child's world offset. The merchant **root** is
                    // followed, not a fixed world point: the spawner can place
                    // or nudge the shade after our `Start` postfix ran, and a
                    // frozen position then left the NPC behind (the operator's
                    // "pas forcément la bonne position de spawn").
                    if (instance.Holder != null)
                    {
                        var yaw = YawOf(instance.Holder);
                        var offset = Quaternion.Euler(0f, yaw - instance.Yaw, 0f) * instance.Offset;
                        instance.Root.SetPositionAndRotation(
                            instance.Holder.position + offset,
                            Quaternion.Euler(0f, yaw + Plugin.ModelYaw.Value, 0f));
                    }

                    if (instance.Clip == null)
                    {
                        continue;
                    }

                    instance.Time += MyTime.deltaTime;
                    GltfSkin.Sample(instance.Model, instance.Clip, instance.Time, 0.6f, true);
                    if (!instance.Model.Gpu)
                    {
                        GltfSkin.ApplySkin(instance.Model);
                    }
                }

                if (gone != null)
                {
                    foreach (var key in gone)
                    {
                        Instances.Remove(key);
                    }
                }
            }

            if (Time.time < nextSweep)
            {
                return;
            }

            nextSweep = Time.time + SweepSeconds;
            ApplyAll();
        }
        catch (Exception error)
        {
            // A failed tick must not repeat every frame for the whole floor.
            nextSweep = Time.time + 5f;
            Plugin.Logger.LogError($"merchant: the tick failed: {error}");
        }
    }

    internal static void ApplyAll()
    {
        if (!Active)
        {
            return;
        }

        var found = UnityEngine.Object.FindObjectsOfType<InteractableShadyGuy>(true);
        if (found == null)
        {
            return;
        }

        for (var index = 0; index < found.Length; index++)
        {
            Apply(found[index]);
        }
    }

    /// <summary>One merchant: hide its native body and mount the client NPC in
    /// the same box. Idempotent on <see cref="ChildName"/> and on the instance
    /// map, so the `Start` postfix and the sweep cannot double it.</summary>
    /// <returns>True when the merchant is mounted (or already was); false while
    /// its native body has not drawn a box the fit can trust (it is then kept in
    /// <see cref="Pending"/> and retried every tick).</returns>
    internal static bool Apply(InteractableShadyGuy merchant)
    {
        if (!Active || merchant == null)
        {
            return true;
        }

        try
        {
            var key = merchant.GetInstanceID();
            var holder = merchant.transform.parent != null ? merchant.transform.parent : merchant.transform;
            if (Instances.ContainsKey(key) || holder.Find(ChildName) != null)
            {
                return true;
            }

            var body = merchant.meshRenderer;
            if (body == null)
            {
                return false;
            }

            var model = LoadTemplate();
            if (model == null)
            {
                return false;
            }

            // Until the renderer has drawn, `bounds` is the bind pose (1.72 m on
            // this prefab) — not the merchant. Wait for the drawn body, then fit
            // and centre on that same box.
            var box = body.bounds;
            if (box.size.y < MinBodyHeight)
            {
                if (!Pending.Contains(merchant))
                {
                    Pending.Add(merchant);
                }

                return false;
            }

            // The component sits on the body child (`ShadyGuy` under
            // `ShadyGuy(Clone)`), and that child carries the **stand-up rotation
            // for Megabonk's Z-up mesh** (a -90Â° pitch). Our GLB is already
            // Y-up — the builder bakes the Rx(+90) on its scene root — so
            // parenting to the child laid the NPC on his back (the operator's
            // screenshot). Parent to the prefab root instead, exactly where the
            // creature pass parents to the enemy root; the body's box and pivot
            // are only read for the fit.
            var clone = GltfSkin.Clone(model);
            var root = GltfSkin.Instantiate(clone, holder);
            root.name = ChildName;

            var meshObject = root.transform.Find("Body");
            var renderer = meshObject != null ? meshObject.GetComponent<Renderer>() : null;
            if (renderer != null)
            {
                // The frozen body-material recipe: the merchant's own material is
                // the source, our colour map the albedo. Nothing else is written.
                var material = PoeCharacters.BuildMaterial(body, clone.Texture);
                if (material != null)
                {
                    renderer.sharedMaterial = material;
                }
            }

            // Measure the posed model (the bind pose ignores the rig's own
            // scale). A model without the clip still uses the bind pose.
            var clip = clone.Clips.TryGetValue(IdleClip, out var idle) ? idle : null;
            var local = GltfSkin.PoseBounds(clone);
            if (clip != null)
            {
                GltfSkin.Sample(clone, clip, 0.4f, 1f);
                local = GltfSkin.PoseBounds(clone);
            }

            if (local.size.y <= 0.01f)
            {
                local = clone.Mesh.bounds;
            }

            // The GLBs face -Z; the game's forward is +Z, so the model takes the
            // same `ModelYaw` correction the bodies and creatures use. The
            // rotation is set in **world** space from the prefab root's yaw:
            // a local rotation would inherit any stand-up pitch the prefab
            // carries, which is what laid the first build on his back.
            var yaw = YawOf(holder) + Plugin.ModelYaw.Value;
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            root.transform.localScale = Vector3.one;
            root.transform.localPosition = Vector3.zero;

            // Fit **inside** the merchant's own body box, uniformly: only what is
            // drawn changes, so the NPC cannot overhang the shop's footprint.
            // The factor and the placement come from the **posed local box**, not
            // from the wrapper's world matrix: the merchant spawner sets the
            // prefab root's rotation after `Start` (the face-down look), and a
            // world-matrix fit then measured the tilted body and shrank the NPC.
            var parentScale = holder.lossyScale;
            var size = local.size;
            var factor = Mathf.Min(
                box.size.x / Mathf.Max(size.x, 0.001f),
                Mathf.Min(box.size.y / Mathf.Max(size.y, 0.001f), box.size.z / Mathf.Max(size.z, 0.001f)));
            root.transform.localScale = new Vector3(
                factor / Safe(parentScale.x),
                factor / Safe(parentScale.y),
                factor / Safe(parentScale.z));

            var spin = Quaternion.Euler(0f, yaw, 0f);
            var centred = spin * new Vector3(local.center.x * factor, 0f, local.center.z * factor);
            var position = new Vector3(
                box.center.x - centred.x,
                box.min.y - local.min.y * factor,
                box.center.z - centred.z);

            // Never stand the NPC under the floor. The native body's `bounds` is
            // a world AABB of a posed (and sometimes tilted) skinned body, so its
            // minimum y can sit below the terrain — the operator's "spawn trop
            // bas". One downward sweep that skips the merchant's own colliders
            // and only ever raises the model; a correct box keeps its floor.
            var ground = PropSwap.GroundAt(holder, position, size.y * factor);
            if (ground > position.y)
            {
                position.y = ground;
            }

            root.transform.position = position;

            var world = new Bounds(Vector3.zero, size * factor);

            if (!clone.Gpu)
            {
                GltfSkin.ApplySkin(clone);
            }

            if (renderer != null && local.size.y > 0.01f)
            {
                // Culling follows localBounds; the posted box is the real one.
                renderer.localBounds = new Bounds(local.center, local.size * 1.2f);
            }

            body.enabled = false;

            Instances[key] = new Instance
            {
                Model = clone,
                Clip = clip,
                Root = root.transform,
                Holder = holder,
                Offset = position - holder.position,
                Yaw = YawOf(holder),
                Time = 0.4f,
            };

            if (PlaceLogged.Add(merchant.name))
            {
                Plugin.Logger.LogInfo(
                    $"merchant: '{merchant.name}' wears {ModelId} " +
                    $"({(clip == null ? "no idle clip" : $"clip {clip.Name} {clip.Duration:0.00}s")}, " +
                    $"{clone.Joints.Length} bones), body {box.size.x:0.00} x {box.size.y:0.00} x " +
                    $"{box.size.z:0.00} m -> drawn {world.size.x:0.00} x {world.size.y:0.00} x " +
                    $"{world.size.z:0.00} m (yaw {yaw:0.#}, holder euler " +
                    $"{holder.rotation.eulerAngles.x:0.#}/{holder.rotation.eulerAngles.y:0.#}/" +
                    $"{holder.rotation.eulerAngles.z:0.#}); native body hidden (shop, icons and particles untouched)");
            }

            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"merchant: '{merchant.name}' could not take the client model: {error}");
            return false;
        }
    }

    private static GltfSkin.Model LoadTemplate()
    {
        if (template != null)
        {
            return template;
        }

        if (templateTried)
        {
            return null;
        }

        templateTried = true;
        var path = ModPaths.Creature(ModelId);
        if (!File.Exists(path))
        {
            Plugin.Logger.LogWarning($"merchant: missing model {path} (run mod/tools/prepare-data.ps1)");
            return null;
        }

        try
        {
            template = GltfSkin.Load(path);
            return template;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"merchant: {path} failed to load: {error}");
            return null;
        }
    }

    /// <summary>The ground-projected facing of a transform, in degrees. A parent
    /// that is tilted (a terrain alignment, the native stand-up pitch) cannot
    /// scramble it, unlike <c>eulerAngles.y</c>, and the yaw-only world rotation
    /// built from it keeps our model upright. Falls back to the Euler yaw when
    /// the forward has no horizontal component at all.</summary>
    private static float YawOf(Transform holder)
    {
        var forward = holder.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude > 0.0001f)
        {
            return Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        }

        return holder.rotation.eulerAngles.y;
    }

    private static float Safe(float value) => Mathf.Abs(value) < 1e-4f ? 1e-4f : value;
}

