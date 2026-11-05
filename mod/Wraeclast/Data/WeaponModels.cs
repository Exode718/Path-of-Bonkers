using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace Wraeclast.Data;

/// <summary>
/// The class starter weapon in the hand. The exported weapon GLBs are static
/// meshes with the client's grip at the origin, in the same space as the class
/// models (metres, root node Rx(+90) for Y-up), and <c>classes.json</c> names the
/// skeleton bone each one hangs from (<c>R_Weapon</c>/<c>L_Weapon</c>, both
/// exported in every class GLB).
///
/// The mesh is parented to that bone with the world transform it has in the bind
/// pose, so it keeps the client's grip offset and follows the hand through the
/// idle/walk clips. Scale is the centimetre factor local to the bone (the world
/// fit comes from the model root above it), so the sheet's <c>scale: 100</c>
/// returns the weapon to metres and the body fit gives it the client's
/// proportions.
/// </summary>
internal static class WeaponModels
{
    private static readonly Dictionary<string, GltfSkin.StaticModel> Cache =
        new Dictionary<string, GltfSkin.StaticModel>(StringComparer.OrdinalIgnoreCase);

    /// <summary>One weapon per body root, so a clip change can re-aim it
    /// without rebuilding anything (see <see cref="SetPose"/>).</summary>
    private sealed class Attachment
    {
        internal GameObject Holder;
        internal Transform Root;
        internal GltfSkin.StaticModel Source;
        internal ClassDefinition.WeaponDefinition Spec;

        /// <summary>The weapon's own mesh renderer, so it can be hidden for a
        /// strike (the Shadow's dagger leaves the hand) without a search.</summary>
        internal MeshRenderer Renderer;

        /// <summary>The clip the base pose was last written for. The base pose
        /// rides the hand, so it is written once per clip (a per-clip override
        /// is body-fixed and needs no such guard).</summary>
        internal string LastClip;

        /// <summary>While a clip switch is still blending, the base pose must be
        /// rewritten every frame: writing it once against a half-blended hand
        /// froze the weapon at the old clip's local angle, so the Templar's
        /// staff ended up upside down the moment he stopped (the hand settled
        /// 180° from the frame the pose was written). After this window the hand
        /// is settled and the pose rides the bone, which keeps its swing.</summary>
        internal float SettleUntil;
    }

    private static readonly Dictionary<int, List<Attachment>> Attached =
        new Dictionary<int, List<Attachment>>();

    /// <summary>One line per thrown weapon, naming the numbers the copy uses
    /// (mesh size, world scale), so a wrong-looking throw can be read.</summary>
    private static readonly HashSet<string> ThrownLogged =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    internal static void Attach(GltfSkin.Model model, GameObject root, Renderer body, ClassDefinition definition)
    {
        if (!Plugin.ShowWeapons.Value || model == null || root == null || definition == null)
        {
            return;
        }

        AttachOne(model, root, body, definition, definition.StarterWeapon);
        AttachOne(model, root, body, definition, definition.Offhand);

        // The body is posed on its idle clip when the class swaps in, so the
        // idle override must be live from the first frame.
        SetPose(root, "idle");
    }

    private static void AttachOne(
        GltfSkin.Model model, GameObject root, Renderer body,
        ClassDefinition definition, ClassDefinition.WeaponDefinition spec)
    {
        if (spec == null || string.IsNullOrEmpty(spec.Id))
        {
            return;
        }

        var bone = ResolveBone(model, root, spec.Grip, out var boneNote);
        if (bone == null)
        {
            Plugin.Logger.LogWarning(
                $"{definition.Name}: {spec.Id} grip '{spec.Grip}' not found in the skeleton");
            return;
        }

        var source = Load(spec.Id);
        if (source == null)
        {
            return;
        }

        var holder = new GameObject($"WraeclastWeapon_{spec.Id}");
        holder.transform.SetParent(root.transform, false);
        holder.transform.localPosition = source.RootPosition;
        holder.transform.localRotation = source.RootRotation;
        holder.transform.localScale = source.RootScale;

        var meshObject = new GameObject("Mesh");
        meshObject.transform.SetParent(holder.transform, false);
        meshObject.AddComponent<MeshFilter>().sharedMesh = source.Mesh;
        var renderer = meshObject.AddComponent<MeshRenderer>();
        var weaponMaterial = PoeCharacters.BuildMaterial(body, source.Texture);
        if (weaponMaterial != null)
        {
            renderer.sharedMaterial = weaponMaterial;
        }
        renderer.shadowCastingMode = ShadowCastingMode.On;
        renderer.receiveShadows = true;

        // The authored root (Rx +90) is part of the mesh space; the sheet's
        // rotation turns the weapon around the body axes (the bow needs 180).
        // A mirrored offhand gets 180° of body yaw so the pair points outwards.
        var tuning = Euler(spec.Rotation);
        if (spec.Mirror)
        {
            tuning = Quaternion.Euler(0f, 180f, 0f) * tuning;
        }

        // `roll` is the doorknob turn around the weapon's own length: applied in
        // the mesh space (right of the authored Rx+90) so it only rotates the
        // blade's edge/face, not where the blade points.
        var roll = Quaternion.Euler(0f, 0f, spec.Roll);
        holder.transform.rotation = root.transform.rotation * tuning * source.RootRotation * roll;

        // Grip at the bone, plus our own offset (body space, metres).
        holder.transform.position = bone.position + root.transform.TransformVector(Vector(spec.Offset));

        // Local under the model root: the root already carries the body fit, so
        // only the centimetre factor (via the bone) and the art multipliers are
        // local here. Using the root's lossyScale applied the fit twice (fit²).
        var display = spec.DisplayScale > 0.01f ? spec.DisplayScale : 1f;
        var scale = (spec.Scale * 0.01f) * display * Plugin.WeaponScale.Value;
        holder.transform.localScale = Vector3.Scale(Vector3.one, source.RootScale) * scale;

        holder.transform.SetParent(bone, true);

        if (!Attached.TryGetValue(root.GetInstanceID(), out var attachments))
        {
            attachments = new List<Attachment>();
            Attached[root.GetInstanceID()] = attachments;
        }

        attachments.Add(new Attachment
        {
            Holder = holder,
            Root = root.transform,
            Source = source,
            Spec = spec,
            Renderer = renderer
        });

        var worldLength = source.Mesh.bounds.size.y *
                          (root.transform.lossyScale.y * source.RootScale.y * scale);
        Plugin.Logger.LogInfo(
            $"{definition.Name}: {spec.Id} in {bone.name}, " +
            $"{source.Mesh.vertexCount} vertices, {worldLength:0.00} units long, scale {spec.Scale:0}" +
            $"{(spec.Mirror ? ", mirrored" : string.Empty)}" +
            $"{(Mathf.Abs(spec.Roll) > 0.01f ? $", roll {spec.Roll:0}" : string.Empty)}" +
            $"{(string.IsNullOrEmpty(boneNote) ? string.Empty : ", " + boneNote)}");
    }

    /// <summary>
    /// Re-aims every weapon of a body root for the given body clip, called every
    /// frame after the body's clip is sampled. Two kinds of pose:
    /// <list type="bullet">
    /// <item>a clip with a <c>poses</c> entry is an explicit **body-space**
    /// orientation and is re-applied every frame (the Ranger's idle bow), so it
    /// cannot be carried away by the hand's bone;</item>
    /// <item>the base tuning is the weapon's **rest pose in the hand**: it is
    /// written once per clip change and then rides the bone, so the hand's
    /// swing moves the weapon (the pre-S8 behaviour, and the Ranger's run
    /// pose).</item>
    /// </list>
    /// Only the orientation is written — the grip stays parented to the bone.
    /// </summary>
    internal static void SetPose(GameObject root, string clip)
    {
        if (root == null ||
            !Attached.TryGetValue(root.GetInstanceID(), out var attachments))
        {
            return;
        }

        foreach (var attachment in attachments)
        {
            if (attachment.Holder == null || attachment.Root == null || attachment.Source == null)
            {
                continue;
            }

            var spec = attachment.Spec;
            ClassDefinition.WeaponDefinition.PoseDefinition pose = null;
            var overridden = spec.Poses != null && !string.IsNullOrEmpty(clip) &&
                             spec.Poses.TryGetValue(clip, out pose) && pose != null;

            if (!overridden)
            {
                // The base pose rides the hand: writing it again once the hand
                // has settled would cancel the swing, so it is written once per
                // clip. **Only the idle entry is re-aimed through its blend**:
                // stopping is where a single write against a half-blended hand
                // left the Templar's staff upside down. Rewriting the walk entry
                // too (0.9.194) changed the run pose the operator had tuned —
                // the Templar and Ranger run read wrong. The walk keeps its
                // one-shot transition write; idle settles over 0.45 s.
                var idle = string.Equals(clip, "idle", StringComparison.Ordinal);
                if (string.Equals(attachment.LastClip, clip, StringComparison.Ordinal))
                {
                    if (!idle || Time.time >= attachment.SettleUntil)
                    {
                        continue;
                    }
                }
                else
                {
                    attachment.LastClip = clip;
                    attachment.SettleUntil = idle ? Time.time + 0.45f : 0f;
                }
            }
            else
            {
                // The body-fixed pose rewrote the local rotation against the
                // bone, so the base pose is stale and must be written again on
                // the next clip that uses it.
                attachment.LastClip = null;
            }

            var tuning = Euler(
                pose != null && pose.Rotation != null && pose.Rotation.Length >= 3
                    ? pose.Rotation
                    : spec.Rotation);
            var roll = pose != null && pose.Roll.HasValue ? pose.Roll.Value : spec.Roll;

            if (spec.Mirror)
            {
                tuning = Quaternion.Euler(0f, 180f, 0f) * tuning;
            }

            attachment.Holder.transform.rotation =
                attachment.Root.rotation * tuning * attachment.Source.RootRotation *
                Quaternion.Euler(0f, 0f, roll);
        }
    }

    /// <summary>
    /// The main-hand weapon's tip in world space, so a skill can be cast from
    /// the weapon instead of the chest (S9: the Fireball leaves the hand). The
    /// client's weapon GLBs put the grip at the origin, so the farthest corner
    /// of the mesh bounds is the blade's end; the holder carries the pose and
    /// the bone carries the hand.
    /// </summary>
    internal static bool TryMuzzle(GameObject root, out Vector3 position)
    {
        position = Vector3.zero;
        if (root == null || !Attached.TryGetValue(root.GetInstanceID(), out var attachments))
        {
            return false;
        }

        foreach (var attachment in attachments)
        {
            if (attachment.Holder == null || attachment.Source == null || attachment.Spec == null)
            {
                continue;
            }

            if (!string.Equals(attachment.Spec.Grip, "R_Weapon", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bounds = attachment.Source.Mesh.bounds;
            var best = bounds.center;
            var bestDistance = best.sqrMagnitude;
            for (var corner = 0; corner < 8; corner++)
            {
                var point = bounds.center + Vector3.Scale(
                    bounds.extents,
                    new Vector3(
                        (corner & 1) == 0 ? -1f : 1f,
                        (corner & 2) == 0 ? -1f : 1f,
                        (corner & 4) == 0 ? -1f : 1f));
                if (point.sqrMagnitude > bestDistance)
                {
                    best = point;
                    bestDistance = point.sqrMagnitude;
                }
            }

            position = attachment.Holder.transform.TransformPoint(best);
            return true;
        }

        return false;
    }

    /// <summary>
    /// S9c: one free-standing copy of a held weapon for a skill that throws it
    /// (the Shadow's Viper Strike). The copy takes the hand weapon's **exact
    /// world transform** — position, rotation and world scale — so it is the
    /// same rigid mesh the hand draws, just detached; the caller only moves it
    /// (see <c>PoeSkills.Throw</c>). The first build re-aimed it with its bounds
    /// axis and spun it, which read on screen as a deformed knife: an unparented
    /// root's rotation and the parented holder's are not interchangeable.
    /// </summary>
    internal static GameObject SpawnThrown(GameObject root, string grip)
    {
        if (root == null || !Attached.TryGetValue(root.GetInstanceID(), out var attachments))
        {
            return null;
        }

        foreach (var attachment in attachments)
        {
            if (attachment.Holder == null || attachment.Source == null || attachment.Spec == null)
            {
                continue;
            }

            if (!string.Equals(attachment.Spec.Grip, grip, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var copy = new GameObject("WraeclastThrown");
            copy.transform.position = attachment.Holder.transform.position;
            copy.transform.rotation = attachment.Holder.transform.rotation;
            copy.transform.localScale = attachment.Holder.transform.lossyScale;

            if (ThrownLogged.Add(attachment.Spec.Id))
            {
                Plugin.Logger.LogInfo(
                    $"thrown {attachment.Spec.Id}: mesh {attachment.Source.Mesh.bounds.size}, " +
                    $"world scale {attachment.Holder.transform.lossyScale}, " +
                    $"{attachment.Source.Mesh.vertexCount} verts");
            }

            var meshObject = new GameObject("Mesh");
            meshObject.transform.SetParent(copy.transform, false);
            meshObject.AddComponent<MeshFilter>().sharedMesh = attachment.Source.Mesh;

            var renderer = meshObject.AddComponent<MeshRenderer>();
            if (attachment.Renderer != null)
            {
                renderer.sharedMaterial = attachment.Renderer.sharedMaterial;
            }

            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return copy;
        }

        return null;
    }

    /// <summary>
    /// S9e: hides or re-shows a held weapon's mesh without detaching it. The
    /// Shadow's strike hides the hand copy while the same knife is planted in
    /// the target, so the hand does not hold a second one; the holder stays on
    /// the bone and keeps following the hand, so showing it again is seamless.
    /// </summary>
    internal static void SetHeldVisible(GameObject root, string grip, bool visible)
    {
        if (root == null || !Attached.TryGetValue(root.GetInstanceID(), out var attachments))
        {
            return;
        }

        foreach (var attachment in attachments)
        {
            if (attachment.Spec == null)
            {
                continue;
            }

            if (!string.Equals(attachment.Spec.Grip, grip, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (attachment.Renderer != null)
            {
                attachment.Renderer.enabled = visible;
            }

            return;
        }
    }

    /// <summary>The body root is going away: drop its attachments.</summary>
    internal static void Forget(GameObject root)
    {
        if (root != null)
        {
            Attached.Remove(root.GetInstanceID());
        }
    }

    /// <summary>
    /// The bone the weapon hangs from. PoE's rigs do not all put <c>R_Weapon</c>
    /// next to the hand: the Templar's sits on the left wrist (so it is trusted),
    /// but the Ranger's is 0.49 m below the wrist in the idle clip and the bow
    /// floated at hip height. When the weapon bone is far from both wrists, fall
    /// back to the nearest wrist; otherwise keep the rig's own weapon bone.
    /// </summary>
    private static Transform ResolveBone(
        GltfSkin.Model model, GameObject root, string grip, out string note)
    {
        note = string.Empty;
        var weapon = FindBone(model, grip);
        var wristR = FindBone(model, "wrist_r");
        var wristL = FindBone(model, "wrist_l");
        var preferred = grip != null && grip.StartsWith("L", StringComparison.OrdinalIgnoreCase)
            ? wristL
            : wristR;

        if (weapon == null)
        {
            note = $"{grip} missing, using {preferred?.name}";
            return preferred;
        }

        var nearest = NearestWrist(root, weapon, wristR, wristL, out var distance);
        if (nearest != null && distance > 0.20f)
        {
            note = $"{grip} is {distance:0.00} m from the hand, using {nearest.name}";
            return nearest;
        }

        return weapon;
    }

    private static Transform NearestWrist(
        GameObject root, Transform weapon,
        Transform wristR, Transform wristL, out float distance)
    {
        distance = float.MaxValue;
        Transform best = null;
        foreach (var wrist in new[] { wristR, wristL })
        {
            if (wrist == null)
            {
                continue;
            }

            var gap = DistanceInModelSpace(root, weapon, wrist);
            if (gap < distance)
            {
                distance = gap;
                best = wrist;
            }
        }

        return best;
    }

    /// <summary>Distance in the GLB's own metres, not the fitted world units.</summary>
    private static float DistanceInModelSpace(GameObject root, Transform a, Transform b)
    {
        var inverse = root.transform.worldToLocalMatrix;
        var localA = inverse.MultiplyPoint3x4(a.position);
        var localB = inverse.MultiplyPoint3x4(b.position);
        return Vector3.Distance(localA, localB);
    }

    private static Transform FindBone(GltfSkin.Model model, string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        foreach (var node in model.Nodes)
        {
            if (node.Transform != null && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return node.Transform;
            }
        }

        return null;
    }

    private static Quaternion Euler(float[] rotation)
    {
        return rotation != null && rotation.Length >= 3
            ? Quaternion.Euler(rotation[0], rotation[1], rotation[2])
            : Quaternion.identity;
    }

    private static Vector3 Vector(float[] value)
    {
        return value != null && value.Length >= 3
            ? new Vector3(value[0], value[1], value[2])
            : Vector3.zero;
    }

    /// <summary>R11b: a weapon mesh for a visual that is not a held weapon
    /// (Twin Orbit's spinning Starforge). Same cached static loader as the
    /// starter weapons.</summary>
    internal static GltfSkin.StaticModel Get(string id) => Load(id);

    private static GltfSkin.StaticModel Load(string id)
    {
        if (Cache.TryGetValue(id, out var cached) && cached != null && cached.Alive)
        {
            return cached;
        }

        var path = ModPaths.Weapon(id);
        if (!File.Exists(path))
        {
            Plugin.Logger.LogWarning($"Missing weapon mesh: {path} (run mod/tools/prepare-data.ps1)");
            return null;
        }

        try
        {
            var model = GltfSkin.LoadStatic(path);
            Cache[id] = model;
            return model;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Weapon mesh failed for {id}: {error}");
            return null;
        }
    }
}
