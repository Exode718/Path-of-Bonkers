using System;
using System.Collections.Generic;
using UnityEngine;
using Wraeclast.Data;

namespace Wraeclast.Skills;

/// <summary>
/// Procedural VFX for our skills: no imported art, no custom shader. Three
/// 128x128 textures (soft orb, ring, beam) are generated once and tinted per
/// element with a <c>Sprites/Default</c> material, so a skill reads as fire,
/// cold or lightning by colour alone.
///
/// Everything is a billboard/flat quad; effects fade and scale over their life
/// and are destroyed by <see cref="PoeSkills"/>' tick.
/// </summary>
internal static class SkillVisuals
{
    private const int Size = 128;

    private static Texture2D orb;
    private static Texture2D ring;
    private static Texture2D beam;
    private static Mesh quad;
    private static Shader shader;
    private static Shader effectShader;
    private static readonly Dictionary<int, Material> Materials = new Dictionary<int, Material>();
    private static readonly Dictionary<int, Material> EffectMaterials = new Dictionary<int, Material>();

    internal static Mesh Quad()
    {
        if (quad != null)
        {
            return quad;
        }

        var mesh = new Mesh { name = "WraeclastQuad" };
        mesh.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3(0.5f, -0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f)
        };
        mesh.uv = new[]
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(1f, 1f),
            new Vector2(0f, 1f)
        };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateBounds();
        quad = mesh;
        return quad;
    }

    internal static Texture2D Orb()
    {
        if (orb == null)
        {
            orb = Build("WraeclastOrb", (x, y) =>
            {
                var d = Distance(x, y);
                var alpha = Mathf.Clamp01(1f - d * 1.15f);
                return alpha * alpha;
            });
        }

        return orb;
    }

    internal static Texture2D Ring()
    {
        if (ring == null)
        {
            ring = Build("WraeclastRing", (x, y) =>
            {
                var d = Distance(x, y);
                var band = Mathf.Clamp01(1f - Mathf.Abs(d - 0.86f) * 10f);
                var inner = Mathf.Clamp01(1f - d * 1.1f) * 0.06f;
                return Mathf.Clamp01(band + inner);
            });
        }

        return ring;
    }

    internal static Texture2D Beam()
    {
        if (beam == null)
        {
            beam = Build("WraeclastBeam", (x, y) =>
            {
                var dx = Mathf.Abs(x - 0.5f) * 2f;
                var dy = Mathf.Abs(y - 0.5f) * 2f;
                var core = Mathf.Clamp01(1f - dy * 6f);
                var ends = Mathf.Clamp01(1f - Mathf.Pow(dx, 6f));
                return Mathf.Clamp01(core * ends);
            });
        }

        return beam;
    }

    internal static Material MaterialFor(Texture2D texture, Color colour)
    {
        var key = texture.GetInstanceID() * 397 ^ ColorKey(colour);
        if (Materials.TryGetValue(key, out var cached) && cached != null)
        {
            return cached;
        }

        if (shader == null)
        {
            shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("Legacy Shaders/Particles/Additive");
            }

            if (shader == null)
            {
                shader = Shader.Find("Unlit/Transparent");
            }
        }

        var material = new Material(shader) { name = "WraeclastSkill" };
        material.mainTexture = texture;
        if (material.HasProperty("_Color"))
        {
            material.color = colour;
        }

        Materials[key] = material;
        return material;
    }

    /// <summary>
    /// Additive unlit material for the PoE effect meshes. The game ships
    /// <c>Particles/Standard Unlit</c> (its own <c>Default-ParticleSystem</c>
    /// material uses it) and that shader takes its blend from the
    /// <c>_SrcBlend</c>/<c>_DstBlend</c> material properties, so an additive
    /// pass needs no custom shader: SrcAlpha/One makes the black background of
    /// the client's effect textures (the Arc bolt, the Ice Shot arrow atlas)
    /// contribute nothing.
    /// </summary>
    internal static Material EffectMaterial(Texture2D texture, Color colour)
    {
        var key = texture.GetInstanceID() * 397 ^ ColorKey(colour) ^ 0x5EED;
        if (EffectMaterials.TryGetValue(key, out var cached) && cached != null)
        {
            return cached;
        }

        if (effectShader == null)
        {
            effectShader = Shader.Find("Particles/Standard Unlit");
        }

        Material material;
        if (effectShader != null)
        {
            material = new Material(effectShader) { name = "WraeclastEffect" };
            if (material.HasProperty("_MainTex"))
            {
                material.SetTexture("_MainTex", texture);
            }

            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", colour);
            }

            if (material.HasProperty("_SrcBlend"))
            {
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            }

            if (material.HasProperty("_DstBlend"))
            {
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
            }

            if (material.HasProperty("_ZWrite"))
            {
                material.SetInt("_ZWrite", 0);
            }

            if (material.HasProperty("_Cull"))
            {
                // Both sides: the Arc ribbon and the fireball shell are
                // single-sided quads and must read from any camera angle.
                material.SetFloat("_Cull", 0f);
            }

            if (material.HasProperty("_LightingEnabled"))
            {
                material.SetFloat("_LightingEnabled", 0f);
            }

            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.renderQueue = 3000;
        }
        else
        {
            material = MaterialFor(texture, colour);
        }

        EffectMaterials[key] = material;
        return material;
    }

    /// <summary>
    /// An additive quad for the client's effect textures, aimed by the caller.
    /// The quad lies in its local XY plane (x is the texture's u axis), so a
    /// stretched sheet uses <c>localScale = (length, width, 1)</c>. Unlike
    /// <see cref="Sprite"/> it draws with <see cref="EffectMaterial"/>, which is
    /// what makes the opaque black background of a PoE effect atlas vanish.
    /// </summary>
    internal static GameObject EffectQuad(
        string name,
        Texture2D texture,
        Color colour,
        Vector3 position,
        Quaternion rotation)
    {
        var quad = new GameObject(name);
        quad.transform.position = position;
        quad.transform.rotation = rotation;

        quad.AddComponent<MeshFilter>().sharedMesh = Quad();
        var renderer = quad.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = EffectMaterial(texture, colour);
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        return quad;
    }

    /// <summary>
    /// The unit quad on the additive effect material, scaled uniformly. The
    /// client's particle sheets are drawn on black, so additive blending is what
    /// makes them read as a shape: this is how a PoE ground sheet (the Ice Nova
    /// shockwave ring, the Righteous Fire rune circle, the Cyclone swirl)
    /// replaces one of the procedural textures.
    /// </summary>
    internal static GameObject EffectSprite(
        string name,
        Texture2D texture,
        Color colour,
        Vector3 position,
        Quaternion rotation,
        float scale)
    {
        var quad = EffectQuad(name, texture, colour, position, rotation);
        quad.transform.localScale = Vector3.one * scale;
        return quad;
    }

    internal static GameObject Sprite(
        string name,
        Texture2D texture,
        Color colour,
        Vector3 position,
        Quaternion rotation,
        float scale)
    {
        var sprite = new GameObject(name);
        sprite.transform.position = position;
        sprite.transform.rotation = rotation;
        sprite.transform.localScale = Vector3.one * scale;

        var filter = sprite.AddComponent<MeshFilter>();
        filter.sharedMesh = Quad();

        var renderer = sprite.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = MaterialFor(texture, colour);
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        return sprite;
    }

    /// <summary>
    /// A PoE effect mesh under <paramref name="parent"/>: the holder takes the
    /// GLB's own root rotation (Rx+90 turning PoE's -Z-up upright) and the
    /// requested scale, the mesh is referenced as-is (one mesh per effect, kept
    /// alive by <see cref="PoeAssets.GetEffect"/>). Returns the holder so the
    /// caller can aim it; its +Z is the model's long axis.
    /// </summary>
    internal static GameObject Mount(
        Transform parent, GltfSkin.StaticModel model, Color colour, float scale)
    {
        var holder = new GameObject("Mesh");
        holder.transform.SetParent(parent, false);
        holder.transform.localRotation = model.RootRotation;
        holder.transform.localScale = model.RootScale * scale;

        holder.AddComponent<MeshFilter>().sharedMesh = model.Mesh;
        var renderer = holder.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = EffectMaterial(model.Texture, colour);
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        return holder;
    }

    /// <summary>Element colour; falls back to the skill's JSON colour.</summary>
    internal static Color Colour(SkillDefinition definition)
    {
        switch (definition.Tag)
        {
            case "fire":
                return new Color(1f, 0.55f, 0.18f, 1f);
            case "cold":
                return new Color(0.45f, 0.8f, 1f, 1f);
            case "lightning":
                return new Color(0.75f, 0.75f, 1f, 1f);
            case "chaos":
                return new Color(0.55f, 1f, 0.35f, 1f);
            default:
                return new Color(1f, 0.92f, 0.75f, 1f);
        }
    }

    private static float Distance(float x, float y)
    {
        var dx = (x - 0.5f) * 2f;
        var dy = (y - 0.5f) * 2f;
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    private static int ColorKey(Color colour)
    {
        var r = Mathf.RoundToInt(colour.r * 255f);
        var g = Mathf.RoundToInt(colour.g * 255f);
        var b = Mathf.RoundToInt(colour.b * 255f);
        return (r << 16) | (g << 8) | b;
    }

    private static Texture2D Build(string name, Func<float, float, float> alpha)
    {
        var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
        {
            name = name,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        var rgba = new byte[Size * Size * 4];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var a = Mathf.Clamp01(alpha((x + 0.5f) / Size, (y + 0.5f) / Size));
                var index = (y * Size + x) * 4;
                rgba[index] = 255;
                rgba[index + 1] = 255;
                rgba[index + 2] = 255;
                rgba[index + 3] = (byte)(a * 255f);
            }
        }

        unsafe
        {
            fixed (byte* pointer = rgba)
            {
                texture.LoadRawTextureData((IntPtr)pointer, rgba.Length);
            }
        }

        texture.Apply(false, false);
        return texture;
    }
}
