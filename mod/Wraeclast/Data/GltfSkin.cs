using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Wraeclast.Data;

/// <summary>
/// Skinned GLB reader: skeleton, bind poses, bone weights and the clips baked
/// into each class model (idle, walk, jump, hit, attack, cast). This is what
/// gets the PoE characters out of their bind pose.
///
/// Coordinates stay in glTF space (no axis mirror): the skeleton, bind poses and
/// mesh must agree, so the only change is reversed triangle winding (Unity
/// winds clockwise for front faces) and flipped V (Unity UVs start at the
/// bottom). Facing is corrected with the model root's yaw.
///
/// Four influences per vertex (Unity's limit) are exactly what the exporter
/// writes.
/// </summary>
internal static class GltfSkin
{
    private const uint GlbMagic = 0x46546C67;
    private const uint ChunkJson = 0x4E4F534A;
    private const uint ChunkBin = 0x004E4942;

    /// <summary>Mesh bounds refresh rate. <c>RecalculateBounds</c> walks every
    /// vertex natively; they only feed culling and the attach fit, so twice a
    /// second is enough.</summary>
    private const float BoundsInterval = 0.5f;

    internal sealed class Node
    {
        internal string Name;
        internal int Parent = -1;
        internal Vector3 Translation = Vector3.zero;
        internal Quaternion Rotation = Quaternion.identity;
        internal Vector3 Scale = Vector3.one;
        internal Transform Transform;
    }

    internal sealed class Channel
    {
        internal int Node;
        internal int Path; // 0 translation, 1 rotation, 2 scale
        internal float[] Times;
        internal Vector3[] Vectors;
        internal Quaternion[] Rotations;
    }

    internal sealed class Clip
    {
        internal string Name;
        internal float Duration = 1f;
        internal readonly List<Channel> Channels = new List<Channel>();
    }

    internal sealed class Model
    {
        internal string Name;
        internal Node[] Nodes;
        internal int[] Joints;
        internal Matrix4x4[] Bindposes;
        internal Mesh Mesh;
        internal Texture2D Texture;
        internal Transform Root;
        internal bool Gpu;
        internal Vector3[] BindPositions;
        internal Vector3[] BindNormals;
        internal Vector3[] SkinnedPositions;
        internal Vector3[] SkinnedNormals;
        internal Matrix4x4[] SkinMatrices;
        internal float NextBounds = -1f;
        internal int[] JointIds;
        internal float[] Weights;
        internal readonly Dictionary<string, Clip> Clips = new Dictionary<string, Clip>(StringComparer.OrdinalIgnoreCase);

        /// <summary>S10b: the local rotation each bent bone had right after the
        /// clip wrote this frame's pose. The bend is re-applied from that value
        /// next frame instead of from the already-bent rotation: `Sample` only
        /// slerps ~18 % per frame toward the clip, so composing on the bent pose
        /// converged to roughly 5× the angle — the body rolled backwards.</summary>
        internal readonly Dictionary<Transform, Quaternion> BendBase = new Dictionary<Transform, Quaternion>();
    }

    /// <param name="forceCpuSkin">Skip the GPU skin upload for this model
    /// (<c>[Presentation] CpuSkinCreatures</c>): <see cref="Instantiate"/> then
    /// builds a MeshFilter/MeshRenderer and the tick skins on the CPU. E1 uses it
    /// to rule the SkinnedMeshRenderer path in or out for one creature without
    /// turning GPU skinning off for the whole bestiary.</param>
    internal static Model Load(string path, bool forceCpuSkin = false)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 20 || BitConverter.ToUInt32(bytes, 0) != GlbMagic)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a GLB file");
        }

        byte[] json = null;
        byte[] bin = null;
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var length = BitConverter.ToInt32(bytes, offset);
            var type = BitConverter.ToUInt32(bytes, offset + 4);
            offset += 8;
            if (length < 0 || offset + length > bytes.Length)
            {
                break;
            }

            if (type == ChunkJson)
            {
                json = new byte[length];
                Array.Copy(bytes, offset, json, 0, length);
            }
            else if (type == ChunkBin)
            {
                bin = new byte[length];
                Array.Copy(bytes, offset, bin, 0, length);
            }

            offset += length;
        }

        if (json == null || bin == null)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is missing a chunk");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var model = new Model { Name = Path.GetFileNameWithoutExtension(path) };

        model.Nodes = ReadNodes(root);
        LinkChildren(root, model.Nodes);

        var skin = root.GetProperty("skins")[0];
        var jointElements = skin.GetProperty("joints");
        model.Joints = new int[jointElements.GetArrayLength()];
        for (var index = 0; index < model.Joints.Length; index++)
        {
            model.Joints[index] = jointElements[index].GetInt32();
        }

        var bindAccessor = skin.GetProperty("inverseBindMatrices").GetInt32();
        model.Bindposes = ReadMatrices(root, bin, bindAccessor);

        model.Mesh = BuildMesh(root, bin, model);
        model.Texture = BuildTexture(root, bin);
        ReadAnimations(root, bin, model);
        model.Gpu = !forceCpuSkin && TryEnableGpuSkinning(model);
        if (forceCpuSkin)
        {
            Plugin.Logger.LogInfo(
                $"skinning: CPU forced for {model.Name} " +
                $"({model.BindPositions.Length} verts, {model.Joints.Length} bones, " +
                "[Presentation] CpuSkinCreatures)");
        }

        return model;
    }

    /// <summary>
    /// Independent copy of a loaded model for one live instance. A template's
    /// bones cannot drive two actors at once, so each instance gets its own
    /// node transforms. Bind data, clips and texture are immutable and stay
    /// shared with the template. With GPU skinning the mesh is shared too —
    /// the bone weights live on the mesh while each SkinnedMeshRenderer owns
    /// its own bone array, so there is nothing per-instance in the geometry.
    /// </summary>
    internal static Model Clone(Model template)
    {
        var nodes = new Node[template.Nodes.Length];
        for (var index = 0; index < nodes.Length; index++)
        {
            var source = template.Nodes[index];
            nodes[index] = new Node
            {
                Name = source.Name,
                Parent = source.Parent,
                Translation = source.Translation,
                Rotation = source.Rotation,
                Scale = source.Scale
            };
        }

        var clone = new Model
        {
            Name = template.Name,
            Nodes = nodes,
            Joints = template.Joints,
            Bindposes = template.Bindposes,
            Mesh = template.Gpu ? template.Mesh : CloneMesh(template),
            Texture = template.Texture,
            Gpu = template.Gpu,
            BindPositions = template.BindPositions,
            BindNormals = template.BindNormals,
            JointIds = template.JointIds,
            Weights = template.Weights,
            SkinnedPositions = new Vector3[template.BindPositions.Length],
            SkinnedNormals = new Vector3[template.BindPositions.Length]
        };

        foreach (var clip in template.Clips)
        {
            clone.Clips[clip.Key] = clip.Value;
        }

        return clone;
    }

    /// <summary>Private geometry copy for the CPU-skinning fallback, which
    /// rewrites the vertex buffer of the mesh it owns.</summary>
    private static Mesh CloneMesh(Model template)
    {
        var mesh = new Mesh { name = template.Mesh.name };
        mesh.indexFormat = template.Mesh.indexFormat;
        mesh.vertices = template.BindPositions;
        mesh.normals = template.BindNormals;
        mesh.uv = template.Mesh.uv;
        mesh.triangles = template.Mesh.triangles;
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Hands the GLB skin data to the mesh so Unity can deform the body on the
    /// GPU. The interop has no <c>Mesh.boneWeights</c> property (Unity 2023
    /// removed it), but it does expose <c>Mesh.InternalSetBoneWeights</c>, which
    /// takes the raw bones-per-vertex and <c>BoneWeight1</c> buffers — the same
    /// buffers <c>Mesh.SetBoneWeights</c> passes.
    /// </summary>
    private static bool TryEnableGpuSkinning(Model model)
    {
        if (!Plugin.GpuSkinning.Value)
        {
            return false;
        }

        try
        {
            // The Il2Cpp array must be rooted across the call: an implicit
            // conversion creates a wrapper nobody holds, and the IL2CPP domain
            // can collect it before the native setter reads it
            // (ObjectCollectedException, the same trap as LoadImage). The
            // property setter goes through Span<T> and still faults, so the
            // raw native entry point is used instead.
            UploadBindposes(model);

            UploadBoneWeights(model);
            var ok = model.Mesh.HasBoneWeights();
            Plugin.Logger.LogInfo(
                $"skinning: {(ok ? "GPU (SkinnedMeshRenderer)" : "CPU fallback")} " +
                $"{model.BindPositions.Length} verts, {model.Joints.Length} bones, " +
                $"bone weights={ok}");
            return ok;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"skinning: GPU path failed for {model.Name}: {error}");
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BoneWeight1
    {
        public float Weight;
        public int BoneIndex;
    }

    /// <summary>
    /// Bind poses through <c>Mesh.SetBindposesFromScript_NativeArray</c>: the
    /// managed <c>Mesh.bindposes</c> setter builds a <c>Span</c> over a
    /// temporary Il2Cpp array that the domain collects before the call
    /// (<c>ObjectCollectedException</c>). Unity's native setter copies these
    /// poses into the mesh, so the buffer is freed here.
    /// </summary>
    private static void UploadBindposes(Model model)
    {
        var size = Marshal.SizeOf<Matrix4x4>();
        var count = model.Bindposes.Length;
        var buffer = Marshal.AllocHGlobal(count * size);

        try
        {
            for (var index = 0; index < count; index++)
            {
                Marshal.StructureToPtr(model.Bindposes[index], buffer + index * size, false);
            }

            model.Mesh.SetBindposesFromScript_NativeArray(buffer, count);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Four normalized influences per vertex, the layout the exporter
    /// writes and the limit <see cref="SkinQuality.Bone4"/> uses.</summary>
    private static void UploadBoneWeights(Model model)
    {
        var vertexCount = model.BindPositions.Length;
        var influenceCount = vertexCount * 4;
        var size = Marshal.SizeOf<BoneWeight1>();
        var bonesPerVertex = Marshal.AllocHGlobal(vertexCount);
        var weights = Marshal.AllocHGlobal(influenceCount * size);

        try
        {
            for (var vertex = 0; vertex < vertexCount; vertex++)
            {
                var sum = 0f;
                for (var influence = 0; influence < 4; influence++)
                {
                    sum += Mathf.Max(0f, model.Weights[vertex * 4 + influence]);
                }

                if (sum <= 0.0001f)
                {
                    sum = 1f;
                }

                for (var influence = 0; influence < 4; influence++)
                {
                    var weight = new BoneWeight1
                    {
                        Weight = Mathf.Max(0f, model.Weights[vertex * 4 + influence]) / sum,
                        BoneIndex = model.JointIds[vertex * 4 + influence]
                    };
                    Marshal.StructureToPtr(weight, weights + (vertex * 4 + influence) * size, false);
                }

                Marshal.WriteByte(bonesPerVertex, vertex, 4);
            }

            model.Mesh.InternalSetBoneWeights(bonesPerVertex, vertexCount, weights, influenceCount);
        }
        finally
        {
            Marshal.FreeHGlobal(bonesPerVertex);
            Marshal.FreeHGlobal(weights);
        }
    }

    /// <summary>Creates the bone hierarchy and the mesh object under
    /// <paramref name="parent"/>. Returns the model root.</summary>
    internal static GameObject Instantiate(Model model, Transform parent)
    {
        var root = new GameObject("WraeclastModel");
        root.transform.SetParent(parent, false);
        root.transform.localPosition = Vector3.zero;
        model.Root = root.transform;

        var transforms = new Transform[model.Nodes.Length];
        for (var index = 0; index < model.Nodes.Length; index++)
        {
            var node = model.Nodes[index];
            var bone = new GameObject(string.IsNullOrEmpty(node.Name) ? "node" + index : node.Name);
            node.Transform = bone.transform;
            transforms[index] = bone.transform;
        }

        for (var index = 0; index < model.Nodes.Length; index++)
        {
            var node = model.Nodes[index];
            var bone = node.Transform;
            var parentTransform = node.Parent >= 0 ? transforms[node.Parent] : root.transform;
            bone.SetParent(parentTransform, false);
            bone.localPosition = node.Translation;
            bone.localRotation = node.Rotation;
            bone.localScale = node.Scale;
        }

        var meshObject = new GameObject("Body");
        meshObject.transform.SetParent(root.transform, false);

        if (model.Gpu)
        {
            // Native skinning: the mesh carries the GLB bone weights and each
            // renderer owns its bone array, so the GPU deforms the body and C#
            // only writes bone transforms.
            var skinned = meshObject.AddComponent<SkinnedMeshRenderer>();
            skinned.sharedMesh = model.Mesh;
            var bones = BoneTransforms(model);
            skinned.bones = bones;
            GC.KeepAlive(bones);
            skinned.rootBone = root.transform;
            skinned.quality = SkinQuality.Bone4;
            skinned.updateWhenOffscreen = false;
            skinned.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            skinned.receiveShadows = true;
            return root;
        }

        var filter = meshObject.AddComponent<MeshFilter>();
        filter.sharedMesh = model.Mesh;

        var renderer = meshObject.AddComponent<MeshRenderer>();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        renderer.receiveShadows = true;

        return root;
    }

    private static Il2CppReferenceArray<Transform> BoneTransforms(Model model)
    {
        var bones = new Transform[model.Joints.Length];
        for (var index = 0; index < bones.Length; index++)
        {
            bones[index] = model.Nodes[model.Joints[index]].Transform;
        }

        return bones;
    }

    /// <summary>
    /// Bone matrix buffer for this instance, in joint order:
    /// <c>bone.localToWorld * bindpose</c>. Cached on the model — a fresh array
    /// per call was one allocation per enemy per skin upload.
    /// </summary>
    private static Matrix4x4[] BuildMatrices(Model model)
    {
        var matrices = model.SkinMatrices;
        if (matrices == null || matrices.Length != model.Joints.Length)
        {
            matrices = new Matrix4x4[model.Joints.Length];
            model.SkinMatrices = matrices;
        }

        for (var index = 0; index < matrices.Length; index++)
        {
            var node = model.Nodes[model.Joints[index]];
            matrices[index] = node.Transform.localToWorldMatrix * model.Bindposes[index];
        }

        return matrices;
    }

    /// <summary>
    /// CPU skinning, used only when the GPU path is unavailable: recomputes the
    /// vertices from the bone matrices and uploads them.
    /// </summary>
    internal static void ApplySkin(Model model)
    {
        if (model.Root == null || model.Mesh == null)
        {
            return;
        }

        var vertexCount = model.BindPositions.Length;
        var rootToLocal = model.Root.worldToLocalMatrix;
        var matrices = BuildMatrices(model);

        var positions = model.SkinnedPositions;
        var normals = model.SkinnedNormals;

        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            var position = model.BindPositions[vertex];
            var normal = model.BindNormals[vertex];
            var skinned = Vector3.zero;
            var skinnedNormal = Vector3.zero;

            for (var influence = 0; influence < 4; influence++)
            {
                var weight = model.Weights[vertex * 4 + influence];
                if (weight <= 0.0001f)
                {
                    continue;
                }

                var matrix = matrices[model.JointIds[vertex * 4 + influence]];
                skinned += matrix.MultiplyPoint3x4(position) * weight;
                skinnedNormal += matrix.MultiplyVector(normal) * weight;
            }

            positions[vertex] = rootToLocal.MultiplyPoint3x4(skinned);
            normals[vertex] = rootToLocal.MultiplyVector(skinnedNormal).normalized;
        }

        model.Mesh.vertices = positions;
        model.Mesh.normals = normals;

        // The first call (attach) must produce bounds: the fit-to-height reads
        // them, and NextBounds starts at -1 so it always runs once.
        if (Time.time >= model.NextBounds)
        {
            model.Mesh.RecalculateBounds();
            model.NextBounds = Time.time + BoundsInterval;
        }
    }

    /// <summary>
    /// Bounds of the current pose in the model root's local space, computed on
    /// the CPU without touching the render mesh (which is shared under GPU
    /// skinning). Used once at attach to fit the body to the host's height; a
    /// SkinnedMeshRenderer's own bounds are only updated on render, which is
    /// too late for the fit.
    /// </summary>
    internal static Bounds PoseBounds(Model model)
    {
        var vertexCount = model.BindPositions.Length;
        if (vertexCount == 0 || model.Root == null)
        {
            return new Bounds(Vector3.zero, Vector3.zero);
        }

        var rootToLocal = model.Root.worldToLocalMatrix;
        var matrices = BuildMatrices(model);
        var positions = model.SkinnedPositions;
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            var position = model.BindPositions[vertex];
            var skinned = Vector3.zero;

            for (var influence = 0; influence < 4; influence++)
            {
                var weight = model.Weights[vertex * 4 + influence];
                if (weight <= 0.0001f)
                {
                    continue;
                }

                skinned += matrices[model.JointIds[vertex * 4 + influence]].MultiplyPoint3x4(position) * weight;
            }

            var local = rootToLocal.MultiplyPoint3x4(skinned);
            positions[vertex] = local;
            min = Vector3.Min(min, local);
            max = Vector3.Max(max, local);
        }

        return new Bounds((min + max) * 0.5f, max - min);
    }

    internal static Transform Bone(Model model, string name)
    {
        if (model == null || model.Nodes == null)
        {
            return null;
        }

        for (var index = 0; index < model.Nodes.Length; index++)
        {
            var node = model.Nodes[index];
            if (node != null && node.Transform != null &&
                string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return node.Transform;
            }
        }

        return null;
    }

    /// <summary>S10b: undo last frame's bend before the clip is sampled again,
    /// so the pose is re-derived from the clip instead of compounding. Called
    /// once per tick, before <see cref="Sample"/>.</summary>
    internal static void RestoreBends(Model model)
    {
        if (model == null || model.BendBase.Count == 0)
        {
            return;
        }

        foreach (var pair in model.BendBase)
        {
            if (pair.Key != null)
            {
                pair.Key.localRotation = pair.Value;
            }
        }

        model.BendBase.Clear();
    }

    /// <summary>Rotates one bone by <paramref name="degrees"/> about a world
    /// axis, on top of the pose the clip just wrote. The rigs carry different
    /// bone rolls (the Shadow's legs are mirrored, the Witch's and Duelist's are
    /// twisted), so a pose expressed in a bone's own axes would bend a different
    /// way per class; a world axis is the body's own. The axis is brought into
    /// the parent's space first, and the base is the post-sample local rotation
    /// saved on the first bend of this frame — never the previous frame's bent
    /// value, which would compound (S10b).</summary>
    internal static void BendBone(Model model, string name, Vector3 axis, float degrees)
    {
        if (Mathf.Abs(degrees) < 0.01f)
        {
            return;
        }

        var bone = Bone(model, name);
        if (bone == null)
        {
            return;
        }

        if (!model.BendBase.TryGetValue(bone, out var baseRotation))
        {
            baseRotation = bone.localRotation;
            model.BendBase[bone] = baseRotation;
        }

        var localAxis = bone.parent != null
            ? bone.parent.InverseTransformDirection(axis)
            : axis;
        bone.localRotation = Quaternion.AngleAxis(degrees, localAxis) * baseRotation;
    }

    /// <summary>Transforms a root-local bounds box to world space through the
    /// model root (yaw + uniform scale, no shear).</summary>
    internal static Bounds ToWorldBounds(Bounds local, Transform root)
    {
        var matrix = root.localToWorldMatrix;
        var center = local.center;
        var extents = local.extents;
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

        for (var corner = 0; corner < 8; corner++)
        {
            var offset = new Vector3(
                (corner & 1) == 0 ? -extents.x : extents.x,
                (corner & 2) == 0 ? -extents.y : extents.y,
                (corner & 4) == 0 ? -extents.z : extents.z);
            var world = matrix.MultiplyPoint3x4(center + offset);
            min = Vector3.Min(min, world);
            max = Vector3.Max(max, world);
        }

        return new Bounds((min + max) * 0.5f, max - min);
    }

    /// <summary>Samples one clip at <paramref name="time"/> and applies the
    /// result to the bone transforms, smoothing towards the target pose so a
    /// clip switch (idle -> walk) does not pop. <paramref name="loop"/> false
    /// clamps instead of wrapping, which is what the one-shot attack and death
    /// clips need (a wrapped death clip would stand the corpse back up).</summary>
    internal static void Sample(Model model, Clip clip, float time, float blend, bool loop = true)
    {
        if (clip == null)
        {
            return;
        }

        var looped = clip.Duration > 0.001f
            ? (loop ? Mathf.Repeat(time, clip.Duration) : Mathf.Clamp(time, 0f, clip.Duration))
            : 0f;

        for (var index = 0; index < clip.Channels.Count; index++)
        {
            var channel = clip.Channels[index];
            var node = model.Nodes[channel.Node];
            var bone = node.Transform;
            if (bone == null)
            {
                continue;
            }

            switch (channel.Path)
            {
                case 0:
                {
                    var value = SampleVector(channel, looped, node.Translation);
                    bone.localPosition = Vector3.Lerp(bone.localPosition, value, blend);
                    break;
                }

                case 1:
                {
                    var value = SampleRotation(channel, looped, node.Rotation);
                    bone.localRotation = Quaternion.Slerp(bone.localRotation, value, blend);
                    break;
                }

                default:
                {
                    var value = SampleVector(channel, looped, node.Scale);
                    bone.localScale = Vector3.Lerp(bone.localScale, value, blend);
                    break;
                }
            }
        }
    }

    private static Vector3 SampleVector(Channel channel, float time, Vector3 fallback)
    {
        var times = channel.Times;
        var values = channel.Vectors;
        if (times == null || values == null || times.Length == 0)
        {
            return fallback;
        }

        if (time <= times[0])
        {
            return values[0];
        }

        if (time >= times[times.Length - 1])
        {
            return values[values.Length - 1];
        }

        var index = Keyframe(times, time);
        var span = times[index + 1] - times[index];
        var t = span > 0.00001f ? (time - times[index]) / span : 0f;
        return Vector3.Lerp(values[index], values[Math.Min(index + 1, values.Length - 1)], t);
    }

    /// <summary>Largest keyframe index whose time is at or before <paramref name="time"/>.
    /// A linear scan is cheaper on a short table (measured on this machine:
    /// 26 ns vs 30 ns per lookup at 60 keys, because the branches predict), and
    /// most creature clips sit under 64 keys; the binary search only earns its
    /// branch cost on long clips (150 keys: 36 vs 52 ns; 1476 keys: 57 vs 486 ns),
    /// which is the boss and merchant range. The threshold keeps the swarm on the
    /// fast path and still fixes the long tables.</summary>
    private static int Keyframe(float[] times, float time)
    {
        if (times.Length <= 96)
        {
            var index = 0;
            while (index + 1 < times.Length && times[index + 1] < time)
            {
                index++;
            }

            return index;
        }

        var low = 0;
        var high = times.Length - 1;
        while (low < high)
        {
            var mid = (low + high + 1) >> 1;
            if (times[mid] <= time)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    private static Quaternion SampleRotation(Channel channel, float time, Quaternion fallback)
    {
        var times = channel.Times;
        var values = channel.Rotations;
        if (times == null || values == null || times.Length == 0)
        {
            return fallback;
        }

        if (time <= times[0])
        {
            return values[0];
        }

        if (time >= times[times.Length - 1])
        {
            return values[values.Length - 1];
        }

        var index = Keyframe(times, time);
        var span = times[index + 1] - times[index];
        var t = span > 0.00001f ? (time - times[index]) / span : 0f;
        return Quaternion.Slerp(values[index], values[Math.Min(index + 1, values.Length - 1)], t);
    }

    private static Node[] ReadNodes(JsonElement root)
    {
        var elements = root.GetProperty("nodes");
        var nodes = new Node[elements.GetArrayLength()];

        for (var index = 0; index < nodes.Length; index++)
        {
            var element = elements[index];
            var node = new Node
            {
                Name = element.TryGetProperty("name", out var name) ? name.GetString() : "node" + index
            };

            if (element.TryGetProperty("translation", out var translation))
            {
                node.Translation = new Vector3(
                    translation[0].GetSingle(),
                    translation[1].GetSingle(),
                    translation[2].GetSingle());
            }

            if (element.TryGetProperty("rotation", out var rotation))
            {
                node.Rotation = new Quaternion(
                    rotation[0].GetSingle(),
                    rotation[1].GetSingle(),
                    rotation[2].GetSingle(),
                    rotation[3].GetSingle());
            }

            if (element.TryGetProperty("scale", out var scale))
            {
                node.Scale = new Vector3(
                    scale[0].GetSingle(),
                    scale[1].GetSingle(),
                    scale[2].GetSingle());
            }

            nodes[index] = node;
        }

        return nodes;
    }

    private static void LinkChildren(JsonElement root, Node[] nodes)
    {
        var elements = root.GetProperty("nodes");
        for (var index = 0; index < nodes.Length; index++)
        {
            if (!elements[index].TryGetProperty("children", out var children))
            {
                continue;
            }

            for (var child = 0; child < children.GetArrayLength(); child++)
            {
                var childIndex = children[child].GetInt32();
                if (childIndex >= 0 && childIndex < nodes.Length)
                {
                    nodes[childIndex].Parent = index;
                }
            }
        }
    }

    private static void ReadAnimations(JsonElement root, byte[] bin, Model model)
    {
        if (!root.TryGetProperty("animations", out var animations))
        {
            return;
        }

        for (var index = 0; index < animations.GetArrayLength(); index++)
        {
            var element = animations[index];
            var clip = new Clip
            {
                Name = element.TryGetProperty("name", out var name) ? name.GetString() : "clip" + index
            };

            var samplers = element.GetProperty("samplers");
            var channels = element.GetProperty("channels");

            for (var channelIndex = 0; channelIndex < channels.GetArrayLength(); channelIndex++)
            {
                var channelElement = channels[channelIndex];
                var target = channelElement.GetProperty("target");
                var path = target.GetProperty("path").GetString();
                var samplerIndex = channelElement.GetProperty("sampler").GetInt32();
                var sampler = samplers[samplerIndex];

                var times = ReadAccessorFloats(root, bin, sampler.GetProperty("input").GetInt32(), 1);
                var outputAccessor = sampler.GetProperty("output").GetInt32();

                var channel = new Channel
                {
                    Node = target.GetProperty("node").GetInt32(),
                    Times = times
                };

                switch (path)
                {
                    case "translation":
                        channel.Path = 0;
                        channel.Vectors = ToVectors(ReadAccessorFloats(root, bin, outputAccessor, 3));
                        break;

                    case "rotation":
                        channel.Path = 1;
                        channel.Rotations = ToQuaternions(ReadAccessorFloats(root, bin, outputAccessor, 4));
                        break;

                    case "scale":
                        channel.Path = 2;
                        channel.Vectors = ToVectors(ReadAccessorFloats(root, bin, outputAccessor, 3));
                        break;

                    default:
                        continue;
                }

                clip.Channels.Add(channel);

                if (times.Length > 0)
                {
                    clip.Duration = Mathf.Max(clip.Duration, times[times.Length - 1]);
                }
            }

            model.Clips[clip.Name] = clip;
        }
    }

    private static Vector3[] ToVectors(float[] values)
    {
        var count = values.Length / 3;
        var result = new Vector3[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = new Vector3(values[index * 3], values[index * 3 + 1], values[index * 3 + 2]);
        }

        return result;
    }

    private static Quaternion[] ToQuaternions(float[] values)
    {
        var count = values.Length / 4;
        var result = new Quaternion[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = new Quaternion(
                values[index * 4],
                values[index * 4 + 1],
                values[index * 4 + 2],
                values[index * 4 + 3]);
        }

        return result;
    }

    /// <summary>
    /// PoE atlases address their islands with integer-shifted UVs (u in
    /// -1..0 for the mirrored atlases, the Maven down to -13.9, v past 1).
    /// The client samples them with repeat; a clamp sampler pins those
    /// vertices to the atlas edge instead, so the body is drawn from a single
    /// texel row or column. Folding into [0, 1) reproduces what repeat does
    /// and leaves the mesh independent of the texture's wrap mode.
    /// </summary>
    private static float WrapUv(float value)
    {
        var wrapped = value - Mathf.Floor(value);
        return wrapped >= 1f ? 0f : wrapped;
    }

    /// <summary>
    /// True when most triangles have their vertex normals pointing the wrong
    /// way round: <c>cross(b - a, c - a) · n</c> is negative, i.e. the lit side
    /// is the inside of the mesh.
    /// </summary>
    private static bool NormalsOpposeWinding(IList<Vector3> vertices, IList<Vector3> normals, int[] indices)
    {
        var limit = Mathf.Min(indices.Length, 6000);
        var agree = 0;
        var oppose = 0;
        for (var index = 0; index + 2 < limit; index += 3)
        {
            var a = indices[index];
            var b = indices[index + 1];
            var c = indices[index + 2];
            var face = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
            var normal = normals[a] + normals[b] + normals[c];
            if (Vector3.Dot(face, normal) > 0f)
            {
                agree++;
            }
            else
            {
                oppose++;
            }
        }

        return oppose > agree;
    }

    /// <summary>
    /// Static GLB (one mesh, no skin, no clips): the starter weapon meshes.
    /// They share the class models' coordinate conventions — positions are
    /// already metres, the root node carries the Rx(+90) that turns PoE's -Z-up
    /// into Unity's +Y — and keep the client's grip at the origin, so the mesh
    /// can be parented straight to an <c>R_Weapon</c>/<c>L_Weapon</c> bone.
    /// </summary>
    internal sealed class StaticModel
    {
        internal string Name;
        internal Mesh Mesh;
        internal Texture2D Texture;

        /// <summary>A2: one colour map per submesh, in the GLB's primitive
        /// order. A weapon or an effect is one primitive, so this holds a
        /// single entry and <see cref="Texture"/> is the same object; a client
        /// terrain prop splits its shapes over one material each, and the
        /// loader used to drop every primitive but the first.</summary>
        internal Texture2D[] Textures;

        /// <summary>A2: which entries of <see cref="Textures"/> were a real map
        /// when the GLB was read. A submesh the client draws with no colour map
        /// is legitimately null, so "null now" alone cannot tell a texture that
        /// never existed from one the scene unload destroyed — this flag can
        /// (0.9.116, the black chests after a run restart).</summary>
        internal bool[] HasTexture;

        /// <summary>A2e: one flag per submesh — its colour map really carries
        /// transparent texels (a foliage or branch card). The client's own
        /// materials clip those with an alpha test; drawn opaque they show the
        /// sheet's near-black surround as hard squares on the branches.</summary>
        internal bool[] AlphaCut;

        internal Quaternion RootRotation = Quaternion.identity;
        internal Vector3 RootPosition = Vector3.zero;
        internal Vector3 RootScale = Vector3.one;

        /// <summary>The model is reusable only while its mesh **and the maps it
        /// was built with** are alive. Unity destroys runtime assets on a scene
        /// unload; the mesh survived a run restart while its atlas did not, and
        /// a chest built from the cached model then had no texture at all (the
        /// chest path draws a map-less submesh black on purpose) — so the check
        /// has to cover the textures, like <c>GlbLoader.Result</c> does.</summary>
        internal bool Alive
        {
            get
            {
                if (Mesh == null)
                {
                    return false;
                }

                if (Textures == null || HasTexture == null)
                {
                    return true;
                }

                for (var index = 0; index < Textures.Length && index < HasTexture.Length; index++)
                {
                    if (HasTexture[index] && Textures[index] == null)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }

    internal static StaticModel LoadStatic(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 20 || BitConverter.ToUInt32(bytes, 0) != GlbMagic)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a GLB file");
        }

        byte[] json = null;
        byte[] bin = null;
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var length = BitConverter.ToInt32(bytes, offset);
            var type = BitConverter.ToUInt32(bytes, offset + 4);
            offset += 8;
            if (length < 0 || offset + length > bytes.Length)
            {
                break;
            }

            if (type == ChunkJson)
            {
                json = new byte[length];
                Array.Copy(bytes, offset, json, 0, length);
            }
            else if (type == ChunkBin)
            {
                bin = new byte[length];
                Array.Copy(bytes, offset, bin, 0, length);
            }

            offset += length;
        }

        if (json == null || bin == null)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is missing a chunk");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var result = new StaticModel { Name = Path.GetFileNameWithoutExtension(path) };

        // The exported weapon GLB is two nodes: the mesh child (index 0) and the
        // scene root that carries the Rx(+90) turning PoE's -Z-up into Y-up (and
        // any authored TRS). Reading nodes[0] dropped that rotation and laid
        // every weapon flat on its side.
        var nodeIndex = 0;
        if (root.TryGetProperty("scenes", out var scenes) && scenes.GetArrayLength() > 0 &&
            scenes[0].TryGetProperty("nodes", out var sceneNodes) && sceneNodes.GetArrayLength() > 0)
        {
            nodeIndex = sceneNodes[0].GetInt32();
        }

        if (root.TryGetProperty("nodes", out var nodes) &&
            nodeIndex >= 0 && nodeIndex < nodes.GetArrayLength())
        {
            var node = nodes[nodeIndex];
            if (node.TryGetProperty("rotation", out var rotation) && rotation.GetArrayLength() == 4)
            {
                result.RootRotation = new Quaternion(
                    rotation[0].GetSingle(), rotation[1].GetSingle(),
                    rotation[2].GetSingle(), rotation[3].GetSingle());
            }

            if (node.TryGetProperty("translation", out var translation) && translation.GetArrayLength() == 3)
            {
                result.RootPosition = new Vector3(
                    translation[0].GetSingle(), translation[1].GetSingle(), translation[2].GetSingle());
            }

            if (node.TryGetProperty("scale", out var scale) && scale.GetArrayLength() == 3)
            {
                result.RootScale = new Vector3(
                    scale[0].GetSingle(), scale[1].GetSingle(), scale[2].GetSingle());
            }
        }

        result.Mesh = BuildStaticMesh(root, bin, out var textures, out var alphaCut);
        result.Textures = textures;
        result.AlphaCut = alphaCut;
        result.HasTexture = new bool[textures.Length];
        for (var index = 0; index < textures.Length; index++)
        {
            result.HasTexture[index] = textures[index] != null;
        }

        result.Texture = textures.Length > 0 ? textures[0] : null;
        return result;
    }

    /// <summary>
    /// Every primitive of the GLB's first mesh, merged into **one** mesh with
    /// one submesh per primitive and an index offset per primitive. The runtime
    /// only ever needs one `Mesh` per model (the renderer holds one material
    /// array), and a client prop's shapes are split per material, so reading
    /// <c>primitives[0]</c> alone drew a fraction of every prop (A2).
    /// </summary>
    private static Mesh BuildStaticMesh(
        JsonElement root,
        byte[] bin,
        out Texture2D[] textures,
        out bool[] alphaCut)
    {
        var primitives = root.GetProperty("meshes")[0].GetProperty("primitives");
        var primitiveCount = primitives.GetArrayLength();
        textures = new Texture2D[primitiveCount];
        alphaCut = new bool[primitiveCount];

        var allVertices = new List<Vector3>();
        var allNormals = new List<Vector3>();
        var allUvs = new List<Vector2>();
        var submeshes = new List<int[]>(primitiveCount);

        for (var primitiveIndex = 0; primitiveIndex < primitiveCount; primitiveIndex++)
        {
            var primitive = primitives[primitiveIndex];
            var attributes = primitive.GetProperty("attributes");

            var positions = ReadAccessorFloats(root, bin, attributes.GetProperty("POSITION").GetInt32(), 3);
            var normals = attributes.TryGetProperty("NORMAL", out var normalAccessor)
                ? ReadAccessorFloats(root, bin, normalAccessor.GetInt32(), 3)
                : null;
            var uvs = attributes.TryGetProperty("TEXCOORD_0", out var uvAccessor)
                ? ReadAccessorFloats(root, bin, uvAccessor.GetInt32(), 2)
                : null;
            var indices = primitive.TryGetProperty("indices", out var indexAccessor)
                ? ReadIndices(root, bin, indexAccessor.GetInt32())
                : Run(count: positions.Length / 3);

            var vertexCount = positions.Length / 3;
            var vertices = new Vector3[vertexCount];
            var meshNormals = new Vector3[vertexCount];

            for (var index = 0; index < vertexCount; index++)
            {
                vertices[index] = new Vector3(positions[index * 3], positions[index * 3 + 1], positions[index * 3 + 2]);
                meshNormals[index] = normals != null && normals.Length == vertexCount * 3
                    ? new Vector3(normals[index * 3], normals[index * 3 + 1], normals[index * 3 + 2])
                    : Vector3.up;

                allUvs.Add(uvs != null && uvs.Length == vertexCount * 2
                    ? new Vector2(WrapUv(uvs[index * 2]), WrapUv(1f - uvs[index * 2 + 1]))
                    : Vector2.zero);
            }

            // The vote is per primitive: a prop's rock and its foliage come from
            // different client materials and can follow different conventions.
            if (NormalsOpposeWinding(vertices, meshNormals, indices))
            {
                for (var index = 0; index < vertexCount; index++)
                {
                    meshNormals[index] = -meshNormals[index];
                }
            }

            var baseVertex = allVertices.Count;
            allVertices.AddRange(vertices);
            allNormals.AddRange(meshNormals);

            var submesh = new int[indices.Length];
            for (var index = 0; index < indices.Length; index++)
            {
                submesh[index] = indices[index] + baseVertex;
            }

            submeshes.Add(submesh);

            var materialIndex = primitive.TryGetProperty("material", out var materialElement)
                ? materialElement.GetInt32()
                : 0;
            textures[primitiveIndex] = BuildTextureAt(
                root, bin, materialIndex, "WraeclastProp", TextureWrapMode.Repeat, out var cut);
            alphaCut[primitiveIndex] = cut;
        }

        var mesh = new Mesh
        {
            name = "WraeclastStatic",
            // Kept for the session like the texture next to it: a scene unload
            // must not collect a model the cache still hands out (0.9.116).
            hideFlags = HideFlags.DontUnloadUnusedAsset
        };
        mesh.indexFormat = allVertices.Count > 65000
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.vertices = allVertices.ToArray();
        mesh.normals = allNormals.ToArray();
        mesh.uv = allUvs.ToArray();
        mesh.subMeshCount = submeshes.Count;
        for (var index = 0; index < submeshes.Count; index++)
        {
            mesh.SetTriangles(submeshes[index], index);
        }

        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>Indices for a primitive that ships none (a triangle soup).</summary>
    private static int[] Run(int count)
    {
        var indices = new int[count];
        for (var index = 0; index < count; index++)
        {
            indices[index] = index;
        }

        return indices;
    }

    private static Mesh BuildMesh(JsonElement root, byte[] bin, Model model)
    {
        var primitive = root.GetProperty("meshes")[0].GetProperty("primitives")[0];
        var attributes = primitive.GetProperty("attributes");

        var positions = ReadAccessorFloats(root, bin, attributes.GetProperty("POSITION").GetInt32(), 3);
        var normals = ReadAccessorFloats(root, bin, attributes.GetProperty("NORMAL").GetInt32(), 3);
        var uvs = ReadAccessorFloats(root, bin, attributes.GetProperty("TEXCOORD_0").GetInt32(), 2);
        var joints = ReadJoints(root, bin, attributes.GetProperty("JOINTS_0").GetInt32());
        var weights = ReadAccessorFloats(root, bin, attributes.GetProperty("WEIGHTS_0").GetInt32(), 4);
        var indices = ReadIndices(root, bin, primitive.GetProperty("indices").GetInt32());

        var vertexCount = positions.Length / 3;
        var meshVertices = new List<Vector3>(vertexCount);
        var meshNormals = new List<Vector3>(vertexCount);
        var meshUvs = new List<Vector2>(vertexCount);
        var meshJoints = new List<int>(joints);
        var meshWeights = new List<float>(weights);
        var wrappedUvs = 0;

        for (var index = 0; index < vertexCount; index++)
        {
            meshVertices.Add(new Vector3(positions[index * 3], positions[index * 3 + 1], positions[index * 3 + 2]));
            meshNormals.Add(new Vector3(normals[index * 3], normals[index * 3 + 1], normals[index * 3 + 2]));

            var u = uvs[index * 2];
            var v = 1f - uvs[index * 2 + 1];
            if (u < 0f || u > 1f || v < 0f || v > 1f)
            {
                wrappedUvs++;
            }

            meshUvs.Add(new Vector2(WrapUv(u), WrapUv(v)));
        }

        if (wrappedUvs > 0)
        {
            Plugin.Logger.LogInfo($"uv: {wrappedUvs} of {vertexCount} outside [0,1) (PoE repeat addressing)");
        }

        // PoE ships its winding counter-clockwise, which is also the side Unity
        // treats as the front face (checked against Megabonk's own meshes: the
        // face normal there agrees with the vertex normals on every triangle).
        // The triangles are therefore used as they come. What is wrong in the
        // source data is the normals: the character SMDs carry them pointing
        // into the body, so the lit side faced away from the camera and every
        // body rendered near black. The winding decides the side; normals that
        // oppose it are flipped.
        if (NormalsOpposeWinding(meshVertices, meshNormals, indices))
        {
            for (var index = 0; index < meshNormals.Count; index++)
            {
                meshNormals[index] = -meshNormals[index];
            }

            Plugin.Logger.LogInfo($"normals: {vertexCount} flipped to face the winding");
        }

        // The vote above is per model, but a few models mix both conventions
        // (The Shaper: 80 % of the body inward, the cloth authored outward).
        // The global flip leaves those triangles with a normal pointing into
        // the surface, and MK Toon shades them black — the Shaper's robe was a
        // flat dark sheet. Normals are per vertex, so a disagreeing triangle
        // gets its own copies of its three vertices with the normal negated,
        // which is the only way to light both groups from their own side.
        var splitTriangles = 0;
        for (var index = 0; index + 2 < indices.Length; index += 3)
        {
            var a = indices[index];
            var b = indices[index + 1];
            var c = indices[index + 2];
            var face = Vector3.Cross(meshVertices[b] - meshVertices[a], meshVertices[c] - meshVertices[a]);
            var normal = meshNormals[a] + meshNormals[b] + meshNormals[c];
            if (Vector3.Dot(face, normal) >= 0f)
            {
                continue;
            }

            for (var corner = 0; corner < 3; corner++)
            {
                var source = indices[index + corner];
                meshVertices.Add(meshVertices[source]);
                meshUvs.Add(meshUvs[source]);
                meshNormals.Add(-meshNormals[source]);
                for (var influence = 0; influence < 4; influence++)
                {
                    meshJoints.Add(meshJoints[source * 4 + influence]);
                    meshWeights.Add(meshWeights[source * 4 + influence]);
                }

                indices[index + corner] = meshVertices.Count - 1;
            }

            splitTriangles++;
        }

        if (splitTriangles > 0)
        {
            Plugin.Logger.LogInfo(
                $"normals: {splitTriangles} triangle(s) split to face the winding " +
                $"({meshVertices.Count} verts)");
        }

        var finalVertices = meshVertices.ToArray();
        var finalNormals = meshNormals.ToArray();
        var finalUvs = meshUvs.ToArray();
        var finalJoints = meshJoints.ToArray();
        var finalWeights = meshWeights.ToArray();

        var mesh = new Mesh
        {
            name = "WraeclastSkinnedMesh",
            hideFlags = HideFlags.DontUnloadUnusedAsset
        };
        mesh.indexFormat = finalVertices.Length > 65000
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.vertices = finalVertices;
        mesh.normals = finalNormals;
        mesh.uv = finalUvs;
        mesh.triangles = indices;
        mesh.RecalculateBounds();

        model.BindPositions = finalVertices;
        model.BindNormals = finalNormals;
        model.JointIds = finalJoints;
        model.Weights = finalWeights;
        model.SkinnedPositions = new Vector3[finalVertices.Length];
        model.SkinnedNormals = new Vector3[finalVertices.Length];

        return mesh;
    }

    private static Texture2D BuildTexture(JsonElement root, byte[] bin) =>
        BuildTextureAt(root, bin, 0, "WraeclastBody", TextureWrapMode.Clamp, out _);

    /// <summary>One primitive's colour map: its material's
    /// <c>baseColorTexture</c>, decoded and uploaded like every other atlas.
    /// A material with no texture (the prop builder's fallback for a group
    /// whose client map is missing) answers null and the caller keeps the
    /// host's own material colour.</summary>
    private static Texture2D BuildTextureAt(
        JsonElement root,
        byte[] bin,
        int materialIndex,
        string name,
        TextureWrapMode wrap,
        out bool alphaCut)
    {
        alphaCut = false;
        if (!root.TryGetProperty("images", out var images) || images.GetArrayLength() == 0 ||
            !root.TryGetProperty("materials", out var materials) || materials.GetArrayLength() == 0)
        {
            return null;
        }

        materialIndex = Mathf.Clamp(materialIndex, 0, materials.GetArrayLength() - 1);
        var imageIndex = 0;
        var material = materials[materialIndex];
        if (material.TryGetProperty("pbrMetallicRoughness", out var pbr) &&
            pbr.TryGetProperty("baseColorTexture", out var baseColor))
        {
            imageIndex = baseColor.GetProperty("index").GetInt32();
            if (root.TryGetProperty("textures", out var textures))
            {
                imageIndex = textures[imageIndex].GetProperty("source").GetInt32();
            }
        }

        if (imageIndex < 0 || imageIndex >= images.GetArrayLength())
        {
            return null;
        }

        var image = images[imageIndex];
        if (!image.TryGetProperty("bufferView", out var bufferViewIndex))
        {
            return null;
        }

        var view = root.GetProperty("bufferViews")[bufferViewIndex.GetInt32()];
        var viewOffset = view.TryGetProperty("byteOffset", out var offsetElement) ? offsetElement.GetInt32() : 0;
        var viewLength = view.GetProperty("byteLength").GetInt32();

        var png = new byte[viewLength];
        Array.Copy(bin, viewOffset, png, 0, viewLength);

        var decoded = PngDecoder.Decode(png);
        alphaCut = HasTransparency(decoded);
        return PoeAssets.CreateTexture(name, decoded, wrap);
    }

    /// <summary>A2e: does this colour map use its alpha as a cutout? The client
    /// keeps a foliage or branch sheet's shape in the alpha channel and its
    /// colour in RGB, and that RGB covers the whole card, so drawn opaque the
    /// card shows a hard near-black square. One texel in a hundred below half
    /// alpha is enough to say the sheet is cut out.</summary>
    private static bool HasTransparency(PngDecoder.Image image)
    {
        var pixels = image != null ? image.Rgba : null;
        if (pixels == null || pixels.Length < 4)
        {
            return false;
        }

        var count = pixels.Length / 4;
        var clear = 0;
        var step = Mathf.Max(1, count / 4096);
        var sampled = 0;
        for (var index = 3; index < pixels.Length; index += 4 * step)
        {
            sampled++;
            if (pixels[index] < 128)
            {
                clear++;
            }
        }

        return sampled > 0 && clear * 100 >= sampled;
    }

    private static float[] ReadAccessorFloats(JsonElement root, byte[] bin, int accessorIndex, int components)
    {
        var accessor = root.GetProperty("accessors")[accessorIndex];
        var count = accessor.GetProperty("count").GetInt32();
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];

        var viewOffset = view.TryGetProperty("byteOffset", out var viewOffsetElement) ? viewOffsetElement.GetInt32() : 0;
        var accessorOffset = accessor.TryGetProperty("byteOffset", out var accessorOffsetElement) ? accessorOffsetElement.GetInt32() : 0;
        var stride = view.TryGetProperty("byteStride", out var strideElement) ? strideElement.GetInt32() : components * 4;

        var values = new float[count * components];
        var start = viewOffset + accessorOffset;
        for (var i = 0; i < count; i++)
        {
            for (var c = 0; c < components; c++)
            {
                values[i * components + c] = BitConverter.ToSingle(bin, start + i * stride + c * 4);
            }
        }

        return values;
    }

    private static int[] ReadJoints(JsonElement root, byte[] bin, int accessorIndex)
    {
        var accessor = root.GetProperty("accessors")[accessorIndex];
        var componentType = accessor.GetProperty("componentType").GetInt32();
        var count = accessor.GetProperty("count").GetInt32();
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];

        var viewOffset = view.TryGetProperty("byteOffset", out var viewOffsetElement) ? viewOffsetElement.GetInt32() : 0;
        var accessorOffset = accessor.TryGetProperty("byteOffset", out var accessorOffsetElement) ? accessorOffsetElement.GetInt32() : 0;
        var stride = view.TryGetProperty("byteStride", out var strideElement) ? strideElement.GetInt32() : 8;

        var values = new int[count * 4];
        var start = viewOffset + accessorOffset;
        for (var i = 0; i < count; i++)
        {
            for (var c = 0; c < 4; c++)
            {
                values[i * 4 + c] = componentType == 5121
                    ? bin[start + i * stride + c]
                    : BitConverter.ToUInt16(bin, start + i * stride + c * 2);
            }
        }

        return values;
    }

    private static int[] ReadIndices(JsonElement root, byte[] bin, int accessorIndex)
    {
        var accessor = root.GetProperty("accessors")[accessorIndex];
        var componentType = accessor.GetProperty("componentType").GetInt32();
        var count = accessor.GetProperty("count").GetInt32();
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];

        var viewOffset = view.TryGetProperty("byteOffset", out var viewOffsetElement) ? viewOffsetElement.GetInt32() : 0;
        var accessorOffset = accessor.TryGetProperty("byteOffset", out var accessorOffsetElement) ? accessorOffsetElement.GetInt32() : 0;
        var start = viewOffset + accessorOffset;

        var indices = new int[count];
        for (var i = 0; i < count; i++)
        {
            indices[i] = componentType switch
            {
                5121 => bin[start + i],
                5123 => BitConverter.ToUInt16(bin, start + i * 2),
                _ => (int)BitConverter.ToUInt32(bin, start + i * 4)
            };
        }

        return indices;
    }

    private static Matrix4x4[] ReadMatrices(JsonElement root, byte[] bin, int accessorIndex)
    {
        var floats = ReadAccessorFloats(root, bin, accessorIndex, 16);
        var count = floats.Length / 16;
        var matrices = new Matrix4x4[count];

        for (var index = 0; index < count; index++)
        {
            var offset = index * 16;
            var matrix = new Matrix4x4();
            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    // glTF stores column-major matrices.
                    matrix[row, column] = floats[offset + column * 4 + row];
                }
            }

            matrices[index] = matrix;
        }

        return matrices;
    }
}
