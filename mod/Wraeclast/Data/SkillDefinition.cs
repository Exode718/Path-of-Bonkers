using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Wraeclast.Data;

internal sealed class SkillTraits
{
    public double IgnitePct { get; set; }
    public double IgniteSeconds { get; set; }
    public double PoisonPct { get; set; }
    public double PoisonSeconds { get; set; }
    public double PoisonStacks { get; set; }

    /// <summary>Viper Strike's base poison spread (metres). Until 2026-11-05
    /// the sheet carried it but this class did not, so only the Toxic Bloom /
    /// Venom Spread mods ever spread a poison.</summary>
    public double PoisonSpread { get; set; }

    /// <summary>Cyclone's base pull on the pack (skill sheet
    /// <c>traits.pullStrength</c>), before the Vortex Lure mod.</summary>
    public double PullStrength { get; set; }

    public double ChillPct { get; set; }
    public double FreezeChance { get; set; }
    public double ShockPct { get; set; }
    public double ShockSeconds { get; set; }
    public double StunChance { get; set; }
    public double ChainChance { get; set; }
    public double ChainJumps { get; set; }
    public double NovaLineChance { get; set; }
}

/// <summary>
/// One skill from <c>assets/skills.json</c> (copy of
/// <c>mod/design/arena/skills.json</c>). The <c>shape</c> field picks the
/// runtime behaviour (projectile, melee, nova, chain, aura, cyclone, ground,
/// orbit).
/// </summary>
internal sealed class SkillDefinition
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Shape { get; set; }
    public string Tag { get; set; }
    public string Animation { get; set; }
    public float Damage { get; set; }
    public float Cooldown { get; set; }
    public float Range { get; set; }
    public float Speed { get; set; }
    public int Projectiles { get; set; }
    public int Pierce { get; set; }
    public float Spread { get; set; }
    public float Arc { get; set; }
    public float Radius { get; set; }
    public int Chains { get; set; }

    /// <summary>Impact splash radius in metres (Fireball). 0 = single target.
    /// The bolt's <c>Radius</c> is only the hit-detection radius; this is the
    /// area that takes <see cref="SplashShare"/> of the hit around the target.</summary>
    public float Splash { get; set; }

    /// <summary>Share of the hit the splash deals (0.5 = 50%).</summary>
    public float SplashShare { get; set; } = 0.5f;

    public string Description { get; set; }
    public long Colour { get; set; }
    public SkillTraits Traits { get; set; }

    /// <summary>V11: the damage type as the player sees it. The <c>tag</c> field
    /// already drives the ailment and the damage-number colour; this is the same
    /// value stated in text, for the skill tooltip.</summary>
    internal string TypeName()
    {
        switch (Tag)
        {
            case "fire":
                return "Fire damage";
            case "cold":
                return "Cold damage";
            case "lightning":
                return "Lightning damage";
            case "chaos":
                return "Chaos damage";
            default:
                return "Physical damage";
        }
    }
}

internal static class SkillDatabase
{
    private static readonly Dictionary<string, SkillDefinition> ById =
        new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);

    internal static bool TryGet(string id, out SkillDefinition definition)
    {
        definition = null;
        return id != null && ById.TryGetValue(id, out definition);
    }

    internal static bool Load(string path)
    {
        ById.Clear();
        if (!File.Exists(path))
        {
            Plugin.Logger.LogError($"Skill table missing: {path} (run mod/tools/prepare-data.ps1)");
            return false;
        }

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var skills = JsonSerializer.Deserialize<List<SkillDefinition>>(File.ReadAllText(path), options);
            if (skills == null)
            {
                return false;
            }

            foreach (var skill in skills)
            {
                if (skill != null && !string.IsNullOrEmpty(skill.Id))
                {
                    if (string.IsNullOrEmpty(skill.Shape))
                    {
                        skill.Shape = "projectile";
                    }

                    if (skill.Arc <= 0f)
                    {
                        skill.Arc = 2f;
                    }

                    ById[skill.Id] = skill;
                }
            }

            Plugin.Logger.LogInfo($"Skill table: {ById.Count} skill(s) from {path}");
            return ById.Count > 0;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Skill table failed to parse: {error}");
            return false;
        }
    }
}
