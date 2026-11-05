using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Wraeclast.Data;

/// <summary>
/// One playable class. Field names match <c>assets/classes.json</c>, which is a
/// copy of <c>mod/design/arena/classes.json</c> (our own design data, not
/// a Megabonk asset).
/// </summary>
internal sealed class ClassDefinition
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Title { get; set; }
    public string Flavour { get; set; }
    public string SkillId { get; set; }
    public string Portrait { get; set; }
    public int Life { get; set; }
    public int Armour { get; set; }
    public int MoveSpeed { get; set; }
    public int DamagePct { get; set; }
    public int CritChance { get; set; }
    public int PickupRadius { get; set; }
    public float Regen { get; set; }
    public WeaponDefinition StarterWeapon { get; set; }
    public WeaponDefinition Offhand { get; set; }
    public PassiveDefinition Passive { get; set; }

    /// <summary>
    /// V9: the class's passive, our design (it replaces Megabonk's native
    /// passive, which <c>ClassSwap</c> switches off). <see cref="Stats"/> are
    /// written into the character's own <c>statModifiers</c> list in the game's
    /// <c>EStat</c>/<c>EStatModifyType</c> terms, so the effect rides the same
    /// pipeline as the class sheet; <see cref="Text"/> is what the card states.
    /// </summary>
    public sealed class PassiveDefinition
    {
        public string Name { get; set; }
        public string Text { get; set; }
        public List<StatEntry> Stats { get; set; }

        /// <summary>2026-11-05: the class passive is per level. One entry is
        /// applied at level 1 and again on every level-up (or every
        /// <see cref="StatEntry.Every"/> levels, capped at <see cref="StatEntry.Max"/>).</summary>
        public List<StatEntry> PerLevel { get; set; }
    }

    public sealed class StatEntry
    {
        public string Stat { get; set; }
        public string Type { get; set; }
        public float Value { get; set; }

        /// <summary>Apply this entry once every N levels (1 = every level).</summary>
        public int Every { get; set; } = 1;

        /// <summary>Maximum increments (0 = unlimited). The Scion's chain entry
        /// uses 3, so Arc gains its third chain at level 30 and stops there.</summary>
        public int Max { get; set; }
    }

    /// <summary>
    /// One held weapon from <c>classes.json</c>. <see cref="Model"/> is the Godot
    /// resource path the reference project loads, <see cref="Grip"/> names the
    /// skeleton bone it hangs from (<c>R_Weapon</c>/<c>L_Weapon</c>, both present
    /// in the exported class GLBs), and scale/rotation/offset are our tuning data.
    /// </summary>
    public sealed class WeaponDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string BaseType { get; set; }
        public string Family { get; set; }
        public string Slot { get; set; }
        public string Grip { get; set; }
        public string Model { get; set; }
        public float Scale { get; set; } = 100f;
        public float[] Rotation { get; set; } = new float[3];
        public float[] Offset { get; set; } = new float[3];

        /// <summary>The weapon's own stat bonuses from <c>classes.json</c>
        /// (<c>damagePct</c>, <c>areaPct</c>, <c>armour</c>, <c>moveSpeedPct</c>,
        /// <c>critChance</c>, <c>maxLifePct</c>, <c>regen</c>,
        /// <c>projectileSpeedPct</c>). They were dead data until 2026-11-05.</summary>
        public Dictionary<string, float> Bonus { get; set; }

        /// <summary>The weapon's ailment/chain traits. Applied to the class's
        /// skill once (<c>ignitePct</c>, <c>igniteSeconds</c>, <c>chainChance</c>,
        /// <c>chainJumps</c>, <c>poisonSpread</c>).</summary>
        public Dictionary<string, double> Traits { get; set; }

        /// <summary>Mirror the weapon horizontally (180° yaw in body space) so a
        /// dual-wield pair points outwards on both sides instead of the same way.</summary>
        public bool Mirror { get; set; }

        /// <summary>Roll around the weapon's own length (the doorknob turn), in
        /// degrees. Applied in the mesh space before the body tilt, so it spins
        /// the blade's edge/face without moving the blade's direction.</summary>
        public float Roll { get; set; }

        /// <summary>Art-level size multiplier on top of the centimetre factor
        /// (1 = the client's own proportions). Mod-only; the Godot reference
        /// ignores it.</summary>
        public float DisplayScale { get; set; } = 1f;

        /// <summary>Per-clip pose overrides, keyed by the body clip's name
        /// (<c>idle</c>, <c>walk</c>). An entry replaces the base
        /// <see cref="Rotation"/>/<see cref="Roll"/> while that clip plays; a
        /// clip with no entry keeps the base tuning.</summary>
        public Dictionary<string, PoseDefinition> Poses { get; set; }

        public sealed class PoseDefinition
        {
            public float[] Rotation { get; set; }

            /// <summary>Null keeps the weapon's base roll.</summary>
            public float? Roll { get; set; }
        }
    }
}

/// <summary>
/// Loads the class table once at plugin start. Keyed by class id (the same id
/// the portrait and the GLB use), never by list index: the JSON order is our
/// design order, the game's <c>unsortedCharacterData</c> is alphabetical.
/// </summary>
internal static class ClassDatabase
{
    private static readonly Dictionary<string, ClassDefinition> ById =
        new Dictionary<string, ClassDefinition>(StringComparer.OrdinalIgnoreCase);

    private static readonly List<ClassDefinition> Ordered = new List<ClassDefinition>();

    internal static IReadOnlyList<ClassDefinition> All => Ordered;

    internal static bool TryGet(string id, out ClassDefinition definition)
    {
        definition = null;
        return id != null && ById.TryGetValue(id, out definition);
    }

    internal static bool Load(string path)
    {
        ById.Clear();
        Ordered.Clear();

        if (!File.Exists(path))
        {
            Plugin.Logger.LogError($"Class table missing: {path} (run mod/tools/prepare-data.ps1)");
            return false;
        }

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var classes = JsonSerializer.Deserialize<List<ClassDefinition>>(File.ReadAllText(path), options);
            if (classes == null)
            {
                Plugin.Logger.LogError($"Class table is empty: {path}");
                return false;
            }

            foreach (var definition in classes)
            {
                if (definition == null || string.IsNullOrEmpty(definition.Id))
                {
                    continue;
                }

                ById[definition.Id] = definition;
                Ordered.Add(definition);
            }

            Plugin.Logger.LogInfo($"Class table: {Ordered.Count} class(es) from {path}");
            return Ordered.Count > 0;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Class table failed to parse: {error}");
            return false;
        }
    }
}
