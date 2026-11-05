using System;
using System.IO;
using System.Text.Json;
using UnityEngine;

namespace Wraeclast.Data;

/// <summary>
/// Minimal GLB (binary glTF 2.0) reader: one static bind-pose mesh plus its
/// base colour texture. That is all the first in-run model swap needs.
///
/// Coordinate conversion: the exported PoE meshes are Blender Z-up and the
/// skin's bind space is the raw attribute space (we render the mesh in bind
/// pose). Unity is Y-up, left-handed:
///     (x, y, z) -> (-x, -z, y)
/// The source models hang from -Z (head at -Z), hence -z. X is mirrored, so the
/// triangle winding is reversed and the UVs are flipped vertically.
///
/// Skinning data (JOINTS_0/WEIGHTS_0) is deliberately ignored here: the mesh is
/// shown in bind pose, with no bone animation. Retargeting the clips is a
/// separate step (docs/MOD_SESSION_START.md, open question 1).
/// </summary>
internal static class GlbLoader
{
    private const uint GlbMagic = 0x46546C67;
    private const uint ChunkJson = 0x4E4F534A;
    private const uint ChunkBin = 0x004E4942;

    private const int ComponentUShort = 5123;
    private const int ComponentUInt = 5125;

    internal sealed class Result
    {
        internal Mesh Mesh;
        internal Texture2D Texture;
        internal string Source;
        internal bool HasTexture;

        /// <summary>Unity destroys runtime assets on scene unload; a destroyed
        /// object compares equal to null.</summary>
        internal bool Alive => Mesh != null && (!HasTexture || Texture != null);
    }

    internal static Result Load(string path)
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

        if (json == null)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} has no JSON chunk");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var result = new Result { Source = Path.GetFileName(path) };

        result.Mesh = BuildMesh(root, bin);
        result.Texture = BuildTexture(root, bin);
        result.HasTexture = result.Texture != null;
        return result;
    }

    private static Mesh BuildMesh(JsonElement root, byte[] bin)
    {
        var primitive = root.GetProperty("meshes")[0].GetProperty("primitives")[0];
        var attributes = primitive.GetProperty("attributes");

        var positions = ReadFloats(root, bin, attributes.GetProperty("POSITION").GetInt32());
        var normals = ReadFloats(root, bin, attributes.GetProperty("NORMAL").GetInt32());
        var uvs = ReadFloats(root, bin, attributes.GetProperty("TEXCOORD_0").GetInt32());
        var indices = ReadIndices(root, bin, primitive.GetProperty("indices").GetInt32());

        var vertexCount = positions.Length / 3;
        var vertices = new Vector3[vertexCount];
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

        for (var i = 0; i < vertexCount; i++)
        {
            var vertex = new Vector3(-positions[i * 3], -positions[i * 3 + 2], positions[i * 3 + 1]);
            vertices[i] = vertex;

            min = Vector3.Min(min, vertex);
            max = Vector3.Max(max, vertex);
        }

        // Feet on the ground and centred over the origin, so the runtime fit
        // only has to apply one uniform scale.
        var offset = new Vector3(-(min.x + max.x) * 0.5f, -min.y, -(min.z + max.z) * 0.5f);
        for (var i = 0; i < vertexCount; i++)
        {
            vertices[i] += offset;
        }

        var normalCount = normals.Length / 3;
        var meshNormals = new Vector3[normalCount];
        for (var i = 0; i < normalCount; i++)
        {
            meshNormals[i] = new Vector3(-normals[i * 3], -normals[i * 3 + 2], normals[i * 3 + 1]);
        }

        var uvCount = uvs.Length / 2;
        var meshUvs = new Vector2[uvCount];
        for (var i = 0; i < uvCount; i++)
        {
            meshUvs[i] = new Vector2(uvs[i * 2], 1f - uvs[i * 2 + 1]);
        }

        // Reverse each triangle so the mirrored mesh keeps outward faces.
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var swap = indices[i + 1];
            indices[i + 1] = indices[i + 2];
            indices[i + 2] = swap;
        }

        var mesh = new Mesh
        {
            name = "WraeclastMesh",
            // Session-scoped like the model's texture: a scene unload must not
            // collect a mesh the asset cache still returns (0.9.116).
            hideFlags = HideFlags.DontUnloadUnusedAsset
        };
        mesh.indexFormat = vertexCount > 65000
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.vertices = vertices;
        if (meshNormals.Length == vertexCount)
        {
            mesh.normals = meshNormals;
        }
        else
        {
            mesh.RecalculateNormals();
        }

        if (meshUvs.Length == vertexCount)
        {
            mesh.uv = meshUvs;
        }

        mesh.triangles = indices;
        mesh.RecalculateBounds();
        return mesh;
    }

    private static Texture2D BuildTexture(JsonElement root, byte[] bin)
    {
        if (bin == null || !root.TryGetProperty("images", out var images) || images.GetArrayLength() == 0)
        {
            return null;
        }

        var imageIndex = 0;
        var material = root.GetProperty("materials")[0];
        if (material.TryGetProperty("pbrMetallicRoughness", out var pbr) &&
            pbr.TryGetProperty("baseColorTexture", out var baseColor))
        {
            imageIndex = baseColor.GetProperty("index").GetInt32();
            if (root.TryGetProperty("textures", out var textures))
            {
                imageIndex = textures[imageIndex].GetProperty("source").GetInt32();
            }
        }

        if (imageIndex >= images.GetArrayLength())
        {
            return null;
        }

        var imageElement = images[imageIndex];
        if (!imageElement.TryGetProperty("bufferView", out var bufferViewIndex))
        {
            return null;
        }

        var view = root.GetProperty("bufferViews")[bufferViewIndex.GetInt32()];
        var viewOffset = view.TryGetProperty("byteOffset", out var offsetElement) ? offsetElement.GetInt32() : 0;
        var viewLength = view.GetProperty("byteLength").GetInt32();

        var png = new byte[viewLength];
        Array.Copy(bin, viewOffset, png, 0, viewLength);

        var decoded = PngDecoder.Decode(png);
        return PoeAssets.CreateTexture("WraeclastTexture", decoded, TextureWrapMode.Repeat);
    }

    private static float[] ReadFloats(JsonElement root, byte[] bin, int accessorIndex)
    {
        var accessor = root.GetProperty("accessors")[accessorIndex];
        var components = ComponentCount(accessor.GetProperty("type").GetString());
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
            if (componentType == ComponentUShort)
            {
                indices[i] = BitConverter.ToUInt16(bin, start + i * 2);
            }
            else if (componentType == ComponentUInt)
            {
                indices[i] = (int)BitConverter.ToUInt32(bin, start + i * 4);
            }
            else
            {
                throw new InvalidDataException($"Unsupported index component type {componentType}");
            }
        }

        return indices;
    }

    private static int ComponentCount(string type)
    {
        switch (type)
        {
            case "SCALAR":
                return 1;
            case "VEC2":
                return 2;
            case "VEC3":
                return 3;
            case "VEC4":
                return 4;
            default:
                throw new InvalidDataException($"Unsupported accessor type {type}");
        }
    }
}
