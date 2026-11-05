using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Wraeclast.Data;

/// <summary>
/// Files the mod brings (<c>BepInEx/plugins/Wraeclast/assets</c>), generated
/// from our own art by <c>mod/tools/prepare-data.ps1</c>:
///
///   assets/classes.json      copy of mod/design/arena/classes.json
///   assets/icons/{Class}.png PoE class portrait (card icon)
///   assets/icons/skills/{id}.png PoE skill icon (HUD weapon row)
///   assets/models/{Class}.glb PoE class model (first in-run model)
/// </summary>
internal static class ModPaths
{
    internal static string Root => Path.GetDirectoryName(typeof(ModPaths).Assembly.Location);

    internal static string Data => Path.Combine(Root, "assets");

    internal static string Classes => Path.Combine(Data, "classes.json");

    internal static string Icon(string classId) => Path.Combine(Data, "icons", classId + ".png");

    /// <summary>PoE skill icon, keyed by the <c>skills.json</c> id.</summary>
    internal static string SkillIcon(string skillId) =>
        Path.Combine(Data, "icons", "skills", skillId + ".png");

    /// <summary>S5+: a PoE unique icon, keyed by the relic id
    /// (<c>assets/icons/items/{id}.png</c>).</summary>
    internal static string ItemIcon(string id) =>
        Path.Combine(Data, "icons", "items", id + ".png");

    internal static string Model(string classId) => Path.Combine(Data, "models", classId + ".glb");

    /// <summary>Enemy creature GLB (beasts, zombies, ghosts...). Same
    /// models directory as the classes, no class id collision.</summary>
    internal static string Creature(string creatureId) => Path.Combine(Data, "models", creatureId + ".glb");

    /// <summary>V6: the client's Path of Exile logo for the main menu title.</summary>
    internal static string MenuLogo => Path.Combine(Data, "ui", "poe-logo.png");

    internal static string Skills => Path.Combine(Data, "skills.json");

    /// <summary>PoE tome sheet (mod/data/tomes.json), one entry per Megabonk
    /// <c>ETome</c> slot: card name and text, plus the native statModifier the
    /// slot is expected to carry.</summary>
    internal static string Tomes => Path.Combine(Data, "tomes.json");

    /// <summary>PoE relic sheet (mod/data/items.json), one entry per Megabonk
    /// <c>EItem</c> slot: card name and text, plus the donor's native card and
    /// <c>SetStat</c> lines the text was written against.</summary>
    internal static string Items => Path.Combine(Data, "items.json");

    /// <summary>Our reward table (mod/data/rewards.json): the code-side tomes
    /// (and, from R5 on, every tome/relic/mod entry with tier and weight).</summary>
    internal static string Rewards => Path.Combine(Data, "rewards.json");

    /// <summary>The Atlas boss roster (mod/data/bosses.json): the floor and
    /// miniboss roles, their display names and their meshes.</summary>
    internal static string Bosses => Path.Combine(Data, "bosses.json");

    /// <summary>Starter weapon GLB (nebulis, windripper...), static mesh with the
    /// grip at the origin, authored in the same space as the class models.</summary>
    internal static string Weapon(string weaponId) => Path.Combine(Data, "weapons", weaponId + ".glb");

    /// <summary>PoE effect mesh (fireball, ice-shot, arc), a static effect GLB
    /// carrying the client's own shape and colour map.</summary>
    internal static string Effect(string effectId) => Path.Combine(Data, "effects", effectId + ".glb");

    /// <summary>A2: a PoE terrain prop (assets/models/props/{id}.glb, the GLB
    /// builder's own output tree), a static mesh that may carry one material
    /// and colour map per shape group.</summary>
    internal static string Prop(string propId) =>
        Path.Combine(Data, "models", "props", propId + ".glb");

    internal static string Vfx(string name) => Path.Combine(Data, "vfx", name + ".png");

    /// <summary>A1: a PoE terrain texture (assets/terrain/{name}.png), tiled by
    /// the host's own ground shader.</summary>
    internal static string Terrain(string name) => Path.Combine(Data, "terrain", name + ".png");
}

/// <summary>Runtime asset cache. Everything is loaded on first use and kept for
/// the session: textures and meshes survive scene changes because the plugin
/// object does.</summary>
internal static class PoeAssets
{
    private static readonly Dictionary<string, Texture2D> Icons =
        new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, GlbLoader.Result> Models =
        new Dictionary<string, GlbLoader.Result>(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, Texture2D> SkillIcons =
        new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

    /// <summary>S5+ unique icons, keyed by the relic id.</summary>
    private static readonly Dictionary<string, Texture2D> ItemIcons =
        new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, Texture2D> Vfx =
        new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

    /// <summary>A1 terrain textures, keyed by file name.</summary>
    private static readonly Dictionary<string, Texture2D> Terrain =
        new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

    /// <summary>V6 menu art (the PoE logo), keyed by file name.</summary>
    private static readonly Dictionary<string, Texture2D> MenuArt =
        new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ground decals derived from a VFX sheet (premultiplied, masked
    /// to a disc), keyed by sheet name and gain.</summary>
    private static readonly Dictionary<string, Texture2D> Decals =
        new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, GltfSkin.StaticModel> Effects =
        new Dictionary<string, GltfSkin.StaticModel>(StringComparer.OrdinalIgnoreCase);

    /// <summary>A2 terrain props, keyed by GLB name.</summary>
    private static readonly Dictionary<string, GltfSkin.StaticModel> Props =
        new Dictionary<string, GltfSkin.StaticModel>(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Missing =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    internal static Texture2D GetIcon(string classId)
    {
        if (Icons.TryGetValue(classId, out var cached) && cached != null)
        {
            return cached;
        }

        Icons.Remove(classId);
        var path = ModPaths.Icon(classId);
        if (!File.Exists(path))
        {
            ReportMissing(path);
            return null;
        }

        try
        {
            var image = PngDecoder.Decode(File.ReadAllBytes(path));
            var texture = CreateTexture("WraeclastIcon_" + classId, image, TextureWrapMode.Clamp);
            Icons[classId] = texture;
            Plugin.Logger.LogInfo($"Icon {classId}: {texture.width}x{texture.height}");
            return texture;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Icon load failed for {classId}: {error}");
            return null;
        }
    }

    /// <summary>PoE skill icon (assets/icons/skills/{id}.png), decoded like the
    /// class portraits. The HUD weapon row shows the class's one skill.</summary>
    internal static Texture2D GetSkillIcon(string skillId)
    {
        if (SkillIcons.TryGetValue(skillId, out var cached) && cached != null)
        {
            return cached;
        }

        SkillIcons.Remove(skillId);
        var path = ModPaths.SkillIcon(skillId);
        if (!File.Exists(path))
        {
            ReportMissing(path);
            return null;
        }

        try
        {
            var image = PngDecoder.Decode(File.ReadAllBytes(path));
            var texture = CreateTexture("WraeclastSkillIcon_" + skillId, image, TextureWrapMode.Clamp);
            SkillIcons[skillId] = texture;
            Plugin.Logger.LogInfo($"Skill icon {skillId}: {texture.width}x{texture.height}");
            return texture;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Skill icon load failed for {skillId}: {error}");
            return null;
        }
    }

    /// <summary>S5+: a relic's client unique icon (assets/icons/items/{id}.png),
    /// decoded like the class and skill icons. A missing file keeps the donor's
    /// own icon (the caller falls through), so no card is ever blank.</summary>
    internal static Texture2D GetItemIcon(string id)
    {
        if (ItemIcons.TryGetValue(id, out var cached) && cached != null)
        {
            return cached;
        }

        ItemIcons.Remove(id);
        var path = ModPaths.ItemIcon(id);
        if (!File.Exists(path))
        {
            // No warning: the 2 relics the client has no unique for keep the
            // donor icon by design, and the caller decides the fallback.
            return null;
        }

        try
        {
            var image = PngDecoder.Decode(File.ReadAllBytes(path));
            var texture = CreateTexture("WraeclastItemIcon_" + id, image, TextureWrapMode.Clamp);
            ItemIcons[id] = texture;
            return texture;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Item icon load failed for {id}: {error}");
            return null;
        }
    }

    /// <summary>V6: the Path of Exile logo drawn over the main menu title.</summary>
    internal static Texture2D GetMenuLogo() => GetMenuArt(ModPaths.MenuLogo, "poe-logo", "Menu logo");

    private static Texture2D GetMenuArt(string path, string key, string label)
    {
        if (MenuArt.TryGetValue(key, out var cached) && cached != null)
        {
            return cached;
        }

        MenuArt.Remove(key);
        if (!File.Exists(path))
        {
            ReportMissing(path);
            return null;
        }

        try
        {
            var image = PngDecoder.Decode(File.ReadAllBytes(path));
            var texture = CreateTexture("Wraeclast" + key, image, TextureWrapMode.Clamp);
            MenuArt[key] = texture;
            Plugin.Logger.LogInfo($"{label}: {texture.width}x{texture.height}");
            return texture;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"{label} load failed: {error}");
            return null;
        }
    }

    internal static GlbLoader.Result GetModel(string classId)
    {
        // Unity destroys unreferenced runtime assets when a scene is unloaded
        // (the menu -> run transition). The cached wrapper then compares equal
        // to null; rebuild it instead of handing back a dead mesh.
        if (Models.TryGetValue(classId, out var cached) && cached.Alive)
        {
            return cached;
        }

        Models.Remove(classId);
        var path = ModPaths.Model(classId);
        if (!File.Exists(path))
        {
            ReportMissing(path);
            return null;
        }

        try
        {
            var model = GlbLoader.Load(path);
            Models[classId] = model;
            Plugin.Logger.LogInfo(
                $"Model {classId}: {model.Mesh.vertexCount} vertices, " +
                $"{model.Mesh.triangles.Length / 3} triangles, " +
                $"texture {(model.Texture == null ? "none" : model.Texture.width + "x" + model.Texture.height)}");
            return model;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Model load failed for {classId}: {error}");
            return null;
        }
    }

    /// <summary>PoE effect mesh (fireball core, Ice Shot arrow, Arc ribbon),
    /// loaded with the static path: one mesh, its own colour texture and the
    /// client's Rx(+90) root. Kept for the session like the other models.</summary>
    internal static GltfSkin.StaticModel GetEffect(string name)
    {
        if (Effects.TryGetValue(name, out var cached) && cached.Alive)
        {
            return cached;
        }

        Effects.Remove(name);
        var path = ModPaths.Effect(name);
        if (!File.Exists(path))
        {
            ReportMissing(path);
            return null;
        }

        try
        {
            var effect = GltfSkin.LoadStatic(path);
            Effects[name] = effect;
            Plugin.Logger.LogInfo(
                $"Effect {name}: {effect.Mesh.vertexCount} vertices, " +
                $"{effect.Mesh.triangles.Length / 3} triangles, " +
                $"{effect.Mesh.bounds.size.x:0.00}x{effect.Mesh.bounds.size.y:0.00}x" +
                $"{effect.Mesh.bounds.size.z:0.00} m");
            return effect;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Effect load failed for {name}: {error}");
            return null;
        }
    }

    /// <summary>A2: a client terrain prop, loaded through the static path like
    /// an effect — one mesh, one submesh per client material group, its own
    /// colour map each, and the client's root rotation. Kept for the session;
    /// the prop meshes are shared by every instance, so a floor of 700 props
    /// costs one load per prop kind.</summary>
    internal static GltfSkin.StaticModel GetProp(string name)
    {
        if (Props.TryGetValue(name, out var cached) && cached.Alive)
        {
            return cached;
        }

        Props.Remove(name);
        var path = ModPaths.Prop(name);
        if (!File.Exists(path))
        {
            ReportMissing(path);
            return null;
        }

        try
        {
            var prop = GltfSkin.LoadStatic(path);
            Props[name] = prop;
            var bounds = prop.Mesh.bounds.size;
            Plugin.Logger.LogInfo(
                $"Prop {name}: {prop.Mesh.vertexCount} vertices, {prop.Mesh.subMeshCount} submesh(es), " +
                $"{prop.Textures?.Length ?? 0} texture(s), " +
                $"{bounds.x:0.00}x{bounds.y:0.00}x{bounds.z:0.00} m");
            return prop;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Prop load failed for {name}: {error}");
            return null;
        }
    }

    /// <summary>Skill VFX textures (assets/vfx/*.png), decoded the same way as
    /// the class icons.</summary>
    internal static Texture2D GetVfx(string name)
    {
        if (Vfx.TryGetValue(name, out var cached) && cached != null)
        {
            return cached;
        }

        Vfx.Remove(name);
        var path = ModPaths.Vfx(name);
        if (!File.Exists(path))
        {
            ReportMissing(path);
            return null;
        }

        try
        {
            var image = PngDecoder.Decode(File.ReadAllBytes(path));
            var texture = CreateTexture("WraeclastVfx_" + name, image, TextureWrapMode.Clamp);
            Vfx[name] = texture;
            Plugin.Logger.LogInfo($"Vfx {name}: {texture.width}x{texture.height}");
            return texture;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Vfx load failed for {name}: {error}");
            return null;
        }
    }

    /// <summary>
    /// A1: one PoE terrain texture for the Forest ground and cliff materials.
    /// Unlike the icons these **tile** and are seen at every distance, so they
    /// are uploaded with a mip chain, repeat wrapping and trilinear filtering:
    /// a clamped, mip-less ground texture shimmers across the whole floor.
    /// The host material keeps its own shader; only the albedo slot changes.
    /// </summary>
    internal static Texture2D GetTerrain(string name)
    {
        if (Terrain.TryGetValue(name, out var cached) && cached != null)
        {
            return cached;
        }

        Terrain.Remove(name);
        var path = ModPaths.Terrain(name);
        if (!File.Exists(path))
        {
            ReportMissing(path);
            return null;
        }

        try
        {
            var image = PngDecoder.Decode(File.ReadAllBytes(path));
            var texture = CreateTexture(
                "WraeclastTerrain_" + name,
                image,
                TextureWrapMode.Repeat,
                mipmaps: true,
                filter: FilterMode.Trilinear);
            Terrain[name] = texture;

            // The probe answers the one question a screenshot cannot: whether
            // the pixels really reached the GPU. A raw upload whose size does
            // not match the texture draws white, so the centre texel is read
            // back once per file (a diagnostic must never cost the texture).
            try
            {
                var centre = texture.GetPixel(texture.width / 2, texture.height / 2);
                Plugin.Logger.LogInfo(
                    $"Terrain {name}: {texture.width}x{texture.height}, mip {texture.mipmapCount}, " +
                    $"centre rgb {centre.r:0.00}/{centre.g:0.00}/{centre.b:0.00}");
            }
            catch (Exception probe)
            {
                Plugin.Logger.LogInfo(
                    $"Terrain {name}: {texture.width}x{texture.height} (pixel probe failed: {probe.Message})");
            }

            return texture;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Terrain load failed for {name}: {error}");
            return null;
        }
    }

    /// <summary>
    /// A ground decal ready for the additive effect pass (S9b). The client's
    /// skill sheets are authored for a shader that reads a separate mask: their
    /// **alpha channel is the shape** and their RGB is the colour, which in
    /// their own bundle covers the whole square. A plain additive draw therefore
    /// shows the sheet's square (the operator saw an orange square under the
    /// Righteous Fire runes and a square around the Ice Nova ring). The colour
    /// is premultiplied by the alpha and everything outside the inscribed
    /// circle is zeroed, so the texture reads as a disc under whichever blend
    /// the host shader ends up using. <paramref name="gain"/> lifts the dim
    /// sheets (their masks peak well under 255).
    /// </summary>
    internal static Texture2D GetGroundVfx(string name, float gain)
    {
        var key = name + "|" + Mathf.RoundToInt(gain * 100f);
        if (Decals.TryGetValue(key, out var cached) && cached != null)
        {
            return cached;
        }

        Decals.Remove(key);
        var path = ModPaths.Vfx(name);
        if (!File.Exists(path))
        {
            ReportMissing(path);
            return null;
        }

        try
        {
            var image = PngDecoder.Decode(File.ReadAllBytes(path));
            PremultiplyDisc(image, gain);
            var texture = CreateTexture("WraeclastDecal_" + name, image, TextureWrapMode.Clamp);
            Decals[key] = texture;
            Plugin.Logger.LogInfo($"Decal {name}: {texture.width}x{texture.height}, gain {gain:0.##}");
            return texture;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Decal load failed for {name}: {error}");
            return null;
        }
    }

    /// <summary>Bakes the two fixes described in <see cref="GetGroundVfx"/> into
    /// the decoded pixels: rgb × alpha × gain, and a circular reach of 1 (the
    /// edge midpoints) so the square corners are black and add nothing.</summary>
    private static void PremultiplyDisc(PngDecoder.Image image, float gain)
    {
        var width = image.Width;
        var height = image.Height;
        var rgba = image.Rgba;
        var halfWidth = width * 0.5f;
        var halfHeight = height * 0.5f;

        for (var y = 0; y < height; y++)
        {
            var dy = (y + 0.5f - halfHeight) / halfHeight;
            for (var x = 0; x < width; x++)
            {
                var dx = (x + 0.5f - halfWidth) / halfWidth;
                var radius = Mathf.Sqrt(dx * dx + dy * dy);

                // Inscribed circle, with a soft 6 % rim so the edge is not a
                // hard cut; everything beyond it (the square corners) is gone.
                var mask = Mathf.Clamp01((1f - radius) / 0.06f);
                var index = (y * width + x) * 4;
                var alpha = rgba[index + 3] / 255f * mask;

                rgba[index] = (byte)Mathf.Clamp(rgba[index] * alpha * gain, 0f, 255f);
                rgba[index + 1] = (byte)Mathf.Clamp(rgba[index + 1] * alpha * gain, 0f, 255f);
                rgba[index + 2] = (byte)Mathf.Clamp(rgba[index + 2] * alpha * gain, 0f, 255f);
                rgba[index + 3] = (byte)Mathf.RoundToInt(mask * 255f);
            }
        }
    }

    /// <summary>
    /// Uploads decoded RGBA through the raw pointer overload. The managed array
    /// overloads of the interop bindings have no stable IL2CPP lifetime here
    /// (see PngDecoder).
    /// </summary>
    internal static unsafe Texture2D CreateTexture(
        string name,
        PngDecoder.Image image,
        TextureWrapMode wrap,
        bool mipmaps = false,
        FilterMode filter = FilterMode.Bilinear)
    {
        var texture = new Texture2D(image.Width, image.Height, TextureFormat.RGBA32, mipmaps)
        {
            name = name,
            wrapMode = wrap,
            filterMode = filter,
            // Our atlases are session-scoped (the asset caches hand them out
            // again), so a scene unload must not collect them: a material that
            // still pointed at a collected map drew black (0.9.116, the chests
            // after a run restart).
            hideFlags = HideFlags.DontUnloadUnusedAsset
        };

        // PNG rows run top to bottom, Unity's raw upload expects the bottom row
        // first. Without this flip every icon and model texture is upside down.
        var rowBytes = image.Width * 4;
        var buffer = new byte[mipmaps ? ChainSize(image.Width, image.Height) : image.Rgba.Length];
        for (var y = 0; y < image.Height; y++)
        {
            Array.Copy(image.Rgba, y * rowBytes, buffer, (image.Height - 1 - y) * rowBytes, rowBytes);
        }

        // A mipmapped texture's raw buffer is the whole chain, not one level:
        // handing `LoadRawTextureData` only the base level leaves the texture
        // without pixels (it draws white) instead of throwing. The chain's tail
        // is zero here and `Apply(true)` regenerates it from the base level.
        fixed (byte* pointer = buffer)
        {
            texture.LoadRawTextureData((IntPtr)pointer, buffer.Length);
        }

        texture.Apply(mipmaps, false);
        return texture;
    }

    /// <summary>Bytes of the full mip chain of an RGBA32 texture, the size
    /// <see cref="Texture2D.LoadRawTextureData(IntPtr, int)"/> expects.</summary>
    private static int ChainSize(int width, int height)
    {
        var total = 0;
        while (width > 1 || height > 1)
        {
            total += width * height * 4;
            width = Mathf.Max(1, width / 2);
            height = Mathf.Max(1, height / 2);
        }

        return total + 4;
    }

    private static void ReportMissing(string path)
    {
        if (Missing.Add(path))
        {
            Plugin.Logger.LogWarning($"Missing asset: {path} (run mod/tools/prepare-data.ps1)");
        }
    }
}
