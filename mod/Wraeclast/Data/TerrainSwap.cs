using System;
using System.Collections.Generic;
using Assets.Scripts._Data.MapsAndStages;
using UnityEngine;

namespace Wraeclast.Data;

/// <summary>
/// A1 — the Forest ground wears the client's own terrain art. Forest is
/// <c>mapType</c> 0 (tiles), so every surface the player walks on is a tile
/// whose renderer material comes from the stage, not from the tile prefab:
/// <c>ProceduralTile</c> calls <c>StageData.GetTopMaterial()</c> (a random
/// entry of <c>flatMaterials</c>) for a tile's top and
/// <c>GetSideMaterial(EFillType, useEdgeTextures)</c> for its edges, which
/// reads the four <c>m_fill*</c> materials. The stage's
/// <c>triplanarMaterial</c> is the terrain edge (top texture, side texture,
/// and the <c>_BelowYTexture</c> seen under an overhang) and
/// <c>grassMaterial</c> draws the grass billboards.
///
/// So one pass over each Forest <see cref="StageData"/> at
/// <c>DataManager.Load</c> repaints the whole floor: the albedo slots are
/// bound to the PoE textures and **nothing else changes** — Megabonk's shader,
/// its tiling (`_Scale`, `_BaseScale`), its noise and its colours stay, which
/// is what keeps the light and the fog of the host coherent.
///
/// The 11 top materials of stage 1 (a "grass" variant each, so a floor is not
/// one repeating tile) are cycled through five PoE ground textures in order,
/// and the side/fill slots take the cliff. <c>[Terrain] ReplaceTerrain</c> is
/// the switch.
/// </summary>
internal static class TerrainSwap
{
    /// <summary>
    /// One theme per floor (operator, 2026-11-01): act 1 on floor 1, act 2 on
    /// floor 2, act 4 on floor 3, on the same Forest map (M1 keeps it the only
    /// map). A palette is a file set, never a tint: every albedo is the client's
    /// own terrain art, so the host's shader, tiling and lighting stay.
    /// </summary>
    private sealed class Palette
    {
        internal Palette(string name, string[] grounds, string rock, string soil, string grass)
        {
            Name = name;
            Grounds = grounds;
            Rock = rock;
            Soil = soil;
            Grass = grass;
        }

        internal string Name { get; }

        internal string[] Grounds { get; }

        internal string Rock { get; }

        internal string Soil { get; }

        internal string Grass { get; }
    }

    /// <summary>The floors, in stage order. Floor 3's act 4 set is the Kaom's
    /// Kingdom ash ground (`grassground`, `kaomground2`) against the Desolation
    /// cliff. The first T1 pass shipped the client's carved **rock atlas**
    /// (`kaomrockdarkgrey`) as the ground — its tiles sampled a sheet of red rock
    /// pieces on black, which read as red slabs (“le sol de l'étage 3 est
    /// buggé, c'est pas du tout la bonne texture”) — and used a field sheet for
    /// the grass of both floors 2 and 3, which left the billboards black. The
    /// five files are rebuilt from named client exports by
    /// `mod/tools/data/curate-act-terrain.py`.</summary>
    private static readonly Palette[] Floors =
    {
        new Palette(
            "act 1 (forest)",
            new[] { "forest-ground-1", "forest-ground-2", "forest-ground-3", "forest-ground-4", "forest-ground-5" },
            "forest-rock",
            "forest-soil",
            "forest-grass"),
        new Palette(
            "act 2 (dry forest)",
            new[] { "act2-ground-1", "act2-ground-2", "act2-ground-3", "act2-ground-4" },
            "act2-rock",
            "act2-soil",
            "act2-grass"),
        new Palette(
            "act 4 (Kaom's)",
            new[] { "act4-ground-1", "act4-ground-2" },
            "act4-rock",
            "act4-soil",
            "act4-grass"),
    };

    /// <summary>A palette with its textures resolved and cached.</summary>
    private sealed class Theme
    {
        internal List<Texture2D> Grounds;
        internal Texture2D Rock;
        internal Texture2D Soil;
        internal Texture2D Grass;
    }

    private static readonly Dictionary<Palette, Theme> Themes = new Dictionary<Palette, Theme>();

    internal static bool Active => Plugin.ReplaceTerrain.Value;

    /// <summary>
    /// Runs from <c>DataManager.Load</c>'s postfix, before any stage loads, so
    /// the tiles are instantiated with the repainted materials.
    /// </summary>
    internal static void Apply(DataManager manager)
    {
        if (!Active)
        {
            Plugin.Logger.LogInfo("terrain off: Megabonk's own ground materials stay");
            return;
        }

        if (manager == null)
        {
            return;
        }

        var maps = manager.maps;
        if (maps == null)
        {
            Plugin.Logger.LogWarning("terrain: DataManager.maps is null, the ground is untouched");
            return;
        }

        var stages = 0;
        var tops = 0;
        var sides = 0;
        var order = new List<string>();

        try
        {
            for (var index = 0; index < maps.Count; index++)
            {
                var map = maps[index];
                if (map == null || (int)map.eMap != (int)EMap.Forest)
                {
                    continue;
                }

                var list = map.stages;
                if (list == null)
                {
                    continue;
                }

                for (var stageIndex = 0; stageIndex < list.Count; stageIndex++)
                {
                    var stage = list[stageIndex];
                    if (stage == null)
                    {
                        continue;
                    }

                    var palette = Floors[Mathf.Clamp(stageIndex, 0, Floors.Length - 1)];
                    var theme = Resolve(palette);
                    if (theme == null || theme.Grounds.Count == 0)
                    {
                        Plugin.Logger.LogWarning(
                            $"terrain: {palette.Name} has no shipped ground texture, floor {stageIndex + 1} " +
                            "keeps Megabonk's own tiles (run mod/tools/prepare-data.ps1)");
                        continue;
                    }

                    stages++;
                    order.Add(palette.Name);
                    tops += PaintTops(stage, theme.Grounds);
                    sides += PaintSides(stage, theme.Rock);
                    PaintEdge(stage, theme.Grounds[0], theme.Rock, theme.Soil);
                    PaintGrass(stage, theme.Grass);
                }
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"terrain: repainting the Forest ground failed: {error}");
            return;
        }

        Plugin.Logger.LogInfo(
            $"terrain: {stages} forest stage(s) <- PoE themes [{string.Join(", ", order)}], " +
            $"{tops} top material(s), {sides} side material(s)");
    }

    /// <summary>One palette's textures, loaded once and cached. A palette whose
    /// ground tiles are not shipped answers an empty list and that floor keeps
    /// the host's own materials (never a half-painted theme).</summary>
    private static Theme Resolve(Palette palette)
    {
        if (palette == null)
        {
            return null;
        }

        if (Themes.TryGetValue(palette, out var cached) && cached != null)
        {
            return cached;
        }

        var grounds = new List<Texture2D>(palette.Grounds.Length);
        foreach (var name in palette.Grounds)
        {
            var texture = PoeAssets.GetTerrain(name);
            if (texture != null)
            {
                grounds.Add(texture);
            }
        }

        var theme = new Theme
        {
            Grounds = grounds,
            Rock = PoeAssets.GetTerrain(palette.Rock),
            Soil = PoeAssets.GetTerrain(palette.Soil),
            Grass = PoeAssets.GetTerrain(palette.Grass),
        };

        Themes[palette] = theme;
        return theme;
    }

    /// <summary>The tile tops: one PoE floor texture per Megabonk grass
    /// variant, cycling so the whole floor is not a single tile.</summary>
    private static int PaintTops(StageData stage, List<Texture2D> grounds)
    {
        var materials = stage.flatMaterials;
        if (materials == null)
        {
            return 0;
        }

        var painted = 0;
        for (var index = 0; index < materials.Count; index++)
        {
            var material = materials[index];
            if (material == null)
            {
                continue;
            }

            var texture = grounds[index % grounds.Count];
            Paint(material, "_MainTex", texture);
            Paint(material, "_AlbedoMap", texture);
            painted++;
        }

        return painted;
    }

    /// <summary>Every vertical face: the four fill slots (middle/top, plain and
    /// edge variants) that <c>GetSideMaterial</c> picks from.</summary>
    private static int PaintSides(StageData stage, Texture2D rock)
    {
        if (rock == null)
        {
            return 0;
        }

        var painted = 0;
        painted += PaintSide(stage.m_fillMiddle, rock);
        painted += PaintSide(stage.m_fillTop, rock);
        painted += PaintSide(stage.m_fillMiddleEdge, rock);
        painted += PaintSide(stage.m_fillTopEdge, rock);
        return painted;
    }

    private static int PaintSide(Material material, Texture2D rock)
    {
        if (material == null)
        {
            return 0;
        }

        Paint(material, "_MainTex", rock);
        Paint(material, "_AlbedoMap", rock);
        return 1;
    }

    /// <summary>The terrain edge: its top is the floor texture, its side the
    /// cliff, and what it shows under an overhang is the soil.</summary>
    private static void PaintEdge(StageData stage, Texture2D top, Texture2D rock, Texture2D soil)
    {
        var material = stage.triplanarMaterial;
        if (material == null)
        {
            return;
        }

        Paint(material, "_MainTex", top);
        Paint(material, "_AlbedoMap", top);
        Paint(material, "_MainTexSide", rock);
        Paint(material, "_BelowYTexture", soil);
    }

    /// <summary>The grass billboards keep the host's own grass shader (its
    /// <c>_TopColor</c>/<c>_BottomColor</c> gradient); only the tuft sheet is
    /// ours.</summary>
    private static void PaintGrass(StageData stage, Texture2D grass)
    {
        var material = stage.grassMaterial;
        if (material == null || grass == null)
        {
            return;
        }

        Paint(material, "_MainTex", grass);

        // 0.9.120: name the sheet that reached the billboards. The grass was
        // black on floors 2-3 and in the arena because the first T1 pass shipped
        // a **ground** sheet (opaque field art, no card alpha) for it; this line
        // states the file and the material so the next look starts from facts.
        Plugin.Logger.LogInfo(
            $"terrain: grass '{material.name}' <- {grass.name} ({grass.width}x{grass.height})");
    }

    /// <summary>Binds a texture only where the material really carries the
    /// property, the same rule the body material follows: this interop's
    /// getters are not trusted, but <c>HasProperty</c>/<c>SetTexture</c> are
    /// the calls the game itself makes.</summary>
    private static void Paint(Material material, string property, Texture texture)
    {
        if (texture == null)
        {
            return;
        }

        if (material.HasProperty(property))
        {
            material.SetTexture(property, texture);
        }
    }
}
