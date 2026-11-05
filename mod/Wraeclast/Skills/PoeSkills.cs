using System;
using System.Collections.Generic;
using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Game.Combat;
using Assets.Scripts.Game.Combat.EnemyDebuffs;
using Assets.Scripts.Inventory.Stats;
using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using Assets.Scripts.Menu.Shop;
using Assets.Scripts.Utility;
using UnityEngine;
using Wraeclast.Data;

namespace Wraeclast.Skills;

/// <summary>
/// Our skill layer: the class's starter skill from <c>skills.json</c>, cast on
/// cooldown at the nearest enemy, damaging through
/// <c>Enemy.DamageFromPlayerWeapon</c> so kills, XP, gold, damage numbers and
/// the level-up draft stay the game's own.
///
/// No custom MonoBehaviour: IL2CPP cannot register managed component types at
/// runtime, so this is a static tick driven from a Harmony postfix on
/// <c>MyPlayer.Update</c>.
///
/// Units follow the old Godot reference (now in git history): <c>range * 0.03</c>,
/// <c>speed * 0.06</c>, <c>radius * 0.03</c>.
/// </summary>
internal static class PoeSkills
{
    private const float RangeScale = 0.03f;
    private const float SpeedScale = 0.06f;

    /// <summary>Gathering Storm's recovery after a release, before the next
    /// channel may root the player (ours; see <c>ChannelStorm</c>).</summary>
    private const float StormRecovery = 1.2f;

    /// <summary>R7b: Cinder Trail's patch life and damage pulse, and Glacial
    /// Echo's delay before the repeated burst.</summary>
    private const float TrailLifeSeconds = 1.5f;
    private const float TrailPulseSeconds = 0.5f;
    private const float EchoSeconds = 0.6f;

    /// <summary>R7c: how long a remembered poison keeps a corpse eligible for
    /// Toxic Bloom / Venom Spread (the game clears the debuff on death).</summary>
    private const float PoisonMemorySeconds = 8f;

    /// <summary>S4: an Arc jump is a flash by design, but 0.18 s was stepped
    /// over on screen at any distance; 0.35 s reads as one bolt per jump.</summary>
    private const float BeamLifeSeconds = 0.35f;

    /// <summary>S4: the client's ribbon is a thin bright line on a black atlas
    /// (1.2 m across a 5 m model), so a 6-10 m jump drew a hairline. The
    /// cross-section is widened on its own; the length still follows the jump.</summary>
    private const float BeamWidthScale = 2.2f;

    /// <summary>S4: the Ice Shot arrow's longest extent as a share of the
    /// player's height (0.32 was a needle at boss distances, 0.5 still read
    /// small).</summary>
    private const float ArrowBodyShare = 0.7f;

    /// <summary>S9e: how long the Shadow's dagger stays planted in the target
    /// while the hand's copy is hidden. The flight itself is instant: an
    /// animated throw read as a deformed knife crossing the whole arc.</summary>
    private const float ThrowSeconds = 0.4f;

    /// <summary>S9g: the planted knife's own yaw (a rotation around its up axis,
    /// applied in the weapon's space). The hand pose left the blade edge-on and
    /// unreadable; the operator asked for 45° to the left.</summary>
    private const float ThrowYawDegrees = -45f;

    /// <summary>S4: an arrow flying away from a third-person camera is
    /// foreshortened to its 7 cm cross-section, so the shaft and fletching are
    /// widened by this factor; the length is untouched.</summary>
    private const float ArrowWidthScale = 2.5f;

    /// <summary>S4: the non-fire halo is stretched along the sight-line into a
    /// streak (width and length as shares of the bolt's size), so the shot
    /// reads at distance instead of being a soft dot.</summary>
    private const float ArrowStreakWidth = 0.55f;
    private const float ArrowStreakLength = 1.2f;

    /// <summary>R7c: the Shadow's skill id, the only class whose death-side
    /// mods are ours to read on the corpse.</summary>
    private const string ViperStrikeSkillId = "viper-strike";

    private sealed class Bolt
    {
        internal GameObject Visual;
        internal Transform Mesh;
        internal Quaternion MeshRotation = Quaternion.identity;
        internal Transform Glow;
        internal Enemy Target;
        internal Vector3 Position;
        internal Vector3 Direction;
        internal float Speed;
        internal float Life;
        internal float Damage;
        internal bool Crit;
        internal int Pierce;

        /// <summary>Impact radius the bolt hits inside (R7: Pyromantic Focus
        /// widens it, Split Ash narrows it).</summary>
        internal float Radius = 1.1f;

        /// <summary>Fireball's impact splash: enemies around the hit take
        /// <see cref="SplashShare"/> of the damage. 0 = single target.</summary>
        internal float Splash;
        internal float SplashShare;

        /// <summary>Ricochet hops left (Ranger).</summary>
        internal int Bounces;

        /// <summary>Balls this impact splits into (Witch); 0 for a split child.</summary>
        internal int Splits;

        internal float SplitDamage;

        /// <summary>Enemies pierced so far, and the extra damage each one adds
        /// (Glacial Lance).</summary>
        internal int Pierces;
        internal float PierceDamage;

        /// <summary>True for a single aimed bolt: it keeps steering at its
        /// target so a moving enemy is still hit. A fan/volley bolt flies
        /// straight, otherwise every bolt in the spread turns onto the same
        /// target within a frame and the volley reads as one projectile.</summary>
        internal bool Homing;

        /// <summary>R11b: Rolling Ember's bounced hit cannot ignite.</summary>
        internal bool NoIgnite;

        /// <summary>R11b: Ice Prison/Fracture must not apply a freeze.</summary>
        internal bool NoFreeze;

        internal readonly HashSet<int> Hit = new HashSet<int>();
    }

    private sealed class Fx
    {
        internal GameObject Visual;
        internal float Life;
        internal float MaxLife;
        internal float StartScale;
        internal float EndScale;
        internal bool Flat;

        /// <summary>Degrees per second around the sheet's local z, so a client
        /// ground sheet stays alive instead of sitting as a still picture.</summary>
        internal float Spin;

        /// <summary>R11b: one Ash Cloud, so the cap counts clouds rather than
        /// the three crossed blades each one draws.</summary>
        internal bool Smoke;
    }

    /// <summary>S9e: the Shadow's dagger planted in the monster it struck. The
    /// hand's copy is hidden for as long as this visual lives, so the same knife
    /// appears to have left the hand and stuck in the target; then it is
    /// destroyed and the hand's copy comes back.</summary>
    private sealed class Throw
    {
        internal GameObject Visual;
        internal GameObject Body;
        internal Enemy Target;
        internal Vector3 Offset;
        internal float Life;
    }

    /// <summary>A Vlad-style shockwave: travels forward from the caster and
    /// damages everything it passes through once.</summary>
    private sealed class Wave
    {
        internal GameObject Visual;
        internal Vector3 Position;
        internal Vector3 Direction;
        internal float Radius;
        internal float Speed;
        internal float Life;
        internal float Damage;
        internal bool Crit;
        internal SkillDefinition Skill;
        internal readonly HashSet<int> Hit = new HashSet<int>();
    }

    /// <summary>Cinder Trail's ground (R7b): one patch per engaged aura tick,
    /// 1.5 s of life, pulsing damage every 0.5 s. Visual only is the ring
    /// spawned with it; the patch itself is just numbers.</summary>
    private sealed class Trail
    {
        internal Vector3 Position;
        internal float Radius;
        internal float Life;
        internal float Pulse;
        internal float Damage;
        internal bool Crit;
        internal SkillDefinition Skill;

        /// <summary>R11b: a frost/permafrost field slows every enemy inside;
        /// 0 means the patch only deals damage.</summary>
        internal float Slow;

        /// <summary>R11b item 4: this patch is a Ball Lightning orb, so each
        /// pulse also draws one zap from the orb to the nearest enemy.</summary>
        internal bool Ball;

        /// <summary>Item 22: Static Field's patch draws live lightning arcs on
        /// every pulse instead of a flat sheet.</summary>
        internal bool Spark;
    }

    /// <summary>Glacial Echo's repeat (R7b): the same burst, 0.6 s later, at a
    /// share of the damage and with no ailment at all.</summary>
    private sealed class Echo
    {
        internal Vector3 Position;
        internal float Radius;
        internal float Life;
        internal float Damage;
        internal bool Crit;
        internal SkillDefinition Skill;

        /// <summary>R11b: a Meteor Call fireball falls from <see cref="Start"/>
        /// to <see cref="Position"/> over <see cref="Total"/> seconds, drawn by
        /// <see cref="Visual"/>, then bursts; <see cref="Splits"/> and
        /// <see cref="SplitDamage"/> carry Split Ash onto that impact.</summary>
        internal bool Meteor;
        internal GameObject Visual;
        internal Vector3 Start;
        internal float Total;
        internal int Splits;
        internal float SplitDamage;

        /// <summary>R11b: Ricochet's hops the impact can still take.</summary>
        internal int Bounces;
    }

    private static readonly List<Bolt> Bolts = new List<Bolt>();
    private static readonly List<Fx> Effects = new List<Fx>();
    private static readonly List<Wave> Waves = new List<Wave>();

    /// <summary>R7b: Cinder Trail's patches and Glacial Echo's repeats.</summary>
    private static readonly List<Trail> Trails = new List<Trail>();
    private static readonly List<Echo> Echoes = new List<Echo>();
    private static readonly List<Throw> Throws = new List<Throw>();

    /// <summary>R7c: enemies our chaos hits poisoned, with the wall-clock
    /// moment the poison stops counting. The death path clears debuffs before
    /// the kill hook runs, so the debuff is read on the hit and remembered; the
    /// expiry keeps a pooled instance id from firing a cloud on a corpse it
    /// never poisoned.</summary>
    private static readonly Dictionary<int, float> PoisonedUntil = new Dictionary<int, float>();

    /// <summary>R11b: per-enemy state for the new upgrades. A pooled enemy
    /// keeps its instance id for as long as it lives, and the run reset clears
    /// the whole block, so a recycled id cannot inherit a stale burn or hit
    /// counter.</summary>
    private static readonly Dictionary<int, float> BurnSince = new Dictionary<int, float>();
    private static readonly Dictionary<int, float> BurningUntil = new Dictionary<int, float>();
    private static readonly Dictionary<int, Enemy> BurningEnemies = new Dictionary<int, Enemy>();
    private static readonly Dictionary<int, int> CombustionHits = new Dictionary<int, int>();
    private static readonly Dictionary<int, int> FrostbiteHits = new Dictionary<int, int>();
    private static readonly Dictionary<int, int> PrisonHits = new Dictionary<int, int>();
    private static readonly Dictionary<int, float> ShatteredUntil = new Dictionary<int, float>();
    private static readonly Dictionary<int, float> FrozenUntil = new Dictionary<int, float>();
    private static readonly Dictionary<int, int> PoisonStacks = new Dictionary<int, int>();

    private sealed class SlowEntry
    {
        internal Enemy Enemy;
        internal float Until;
    }

    private static readonly Dictionary<int, SlowEntry> Slows = new Dictionary<int, SlowEntry>();

    /// <summary>R11b: named temporary stats. The game keeps one modifier per
    /// name, so refreshing the same name every tick is safe; the timer removes
    /// it once the last refresh expires.</summary>
    private static readonly Dictionary<string, float> MovingUntil =
        new Dictionary<string, float>(StringComparer.Ordinal);

    private const float MovingStatSeconds = 0.5f;

    private static Enemy markedEnemy;
    private static int momentumHits;
    private static float momentumAt;
    private static Vector3 lastRazorPosition;
    private static int lastCycloneHits;
    private static float afterimageReady;
    private static float novaRefund;
    private static float lastRitesUntil;
    private static float lastRitesReady;
    private static float lastBreathUntil;
    private static float lastBreathMult = 1f;
    private static int arcticCasts;
    private static int chainCasts;
    private static float familiarNext;
    private static float familiarAngle;
    private static int familiarFireIndex;
    private static readonly List<GameObject> familiarOrbs = new List<GameObject>();
    private static float wildfireNext;
    private static float pyreHealWindow;
    private static float pyreHealed;

    /// <summary>R11b: Static Cling makes the first hit of a chain unable to
    /// stun; the flag is set around that one hit.</summary>
    private static bool suppressStun;

    /// <summary>R11b: Twin Fang's first hit cannot poison.</summary>
    private static bool suppressPoison;

    private static MyPlayer owner;
    private static SkillDefinition active;
    private static float cooldown;
    private static int casts;
    private static bool announced;
    /// <summary>S9: 0 until the Fireball's cast origin is reported, 1 when it
    /// answered the main-hand weapon, 2 when it fell back to the chest.</summary>
    private static int castOriginReported;

    private static GameObject auraVisual;
    private static SkillDefinition auraSkill;
    private static float auraRadius;
    private static float auraSpin;

    /// <summary>The translucent ground circle showing the skill's reach.</summary>
    private static GameObject rangeIndicator;
    private static SkillDefinition rangeIndicatorSkill;
    private static float rangeIndicatorRadius = -1f;
    private static float rangeIndicatorNext;
    private static readonly Color RangeColour = new Color(0.36f, 0.72f, 1f, 0.42f);

    /// <summary>R11b item 17: Twin Orbit's spinning Starforge (two blades on a
    /// world-space holder, one per side).</summary>
    private static GameObject orbitVisual;
    private static float orbitSpin;
    private static float spin;
    private static float stormCharge;
    private static float stormRecovery;
    private static Vector3 stormAnchor;
    private static bool stormStill;
    private static bool stormEngaged;
    private static float stormRetarget;
    private static GameObject stormVisual;
    private static int cycloneTicks;

    /// <summary>Degrees between two neighbouring bolts when the sheet authors
    /// no spread at all: a second projectile becomes a second lane instead of
    /// two bolts on one line (S3).</summary>
    private const float DefaultFanDegrees = 10f;

    /// <summary>R11b: how high above the target a Meteor Call fireball starts,
    /// so the fall reads as coming from the sky rather than a hop.</summary>
    private const float MeteorDropHeight = 18f;

    /// <summary>R11b item 1: the client Meteor's two meshes are drawn as one
    /// falling body — the glowing head shell and the rock inside it. Sizes are
    /// longest extents in game units; the rock sits at two thirds of the shell.</summary>
    private const float MeteorHeadSize = 3.2f;
    private const float MeteorRockSize = 2.2f;

    /// <summary>R11b: Ember Familiar is an ember that orbits the caster and
    /// lobs its fireball from there, so the orb is a real object on a ring
    /// around the player's waist line, not a bolt from the chest.</summary>
    private const float FamiliarOrbitRadius = 1.2f;
    private const float FamiliarOrbitHeightShare = 0.55f;
    private const float FamiliarOrbSize = 0.55f;
    private const float FamiliarSpinDegrees = 120f;
    private const int FamiliarMaxOrbs = 8;

    /// <summary>R11b: the meteors start on a small ring around the landing
    /// point and arrive a beat apart, so the shower reads as several balls
    /// converging instead of one ball drawn twice.</summary>
    private const float MeteorStartSpread = 2.4f;
    private const float MeteorStagger = 0.15f;

    /// <summary>R11b: how far apart two impact points land — far enough that
    /// their blasts do not fully overlap, so moving the pack into the rings is
    /// what maximises the damage.</summary>
    private const float MeteorImpactSpread = 4f;

    /// <summary>The landing marker is the operator's small generic red ring
    /// (clearer than a client sheet for “where will this hit?”).</summary>
    private static readonly Color MeteorMarkerTint = new Color(1f, 0.16f, 0.10f, 0.95f);

    /// <summary>R11b: the orbit blades are drawn in world metres, not in the
    /// weapon bone's centimetre space, so they need their own size multiplier
    /// (the live look read them as tiny against the 4 m ring).</summary>
    private const float TwinOrbitScale = 2.2f;

    /// <summary>R11b item 17: the orbiting blades turn at the caster's pivot
    /// height (half the body), not on the floor — the operator's “hauteur du
    /// pivot”, so they read as orbiting the character.</summary>
    private const float TwinOrbitHeightShare = 0.5f;

    /// <summary>R11b item 3: Ash Cloud draws the client's smoke card as three
    /// crossed, alpha-blended cards standing on the floor (never a ground
    /// ring), capped so a fast projectile cannot flood the frame.</summary>
    private const int AshCloudCap = 6;
    private const int AshCloudBlades = 3;
    private const float AshCloudGroundHeight = 0.08f;
    private const float AshCloudStartShare = 0.40f;
    private static readonly Color AshCloudTint = new Color(1f, 1f, 1f, 0.9f);

    /// <summary>R11b item 4: Ball Lightning draws the client's own orb mesh
    /// (0.42 m in its build) floating above the patch; the scale lands it near
    /// 1 m and the height puts it at chest level over the ground.</summary>
    private const float BallOrbHeight = 1.1f;
    private const float BallOrbSize = 3.6f;

    /// <summary>R11b item 6: Gale Offshoots wears Megabonk's own Desert tornado
    /// (the `TornadoRoaming` mesh-particle prefab, whose particles are 15-21
    /// units tall), shrunk to a mini tornado. Megabonk's atlas sheet is a UI
    /// icon grid, so the prefab is cloned whole rather than rebuilt as a card.</summary>
    private const float OffshootTornadoScale = 0.40f;
    private static bool tornadoLogged;

    /// <summary>The last projectile cast summary, so count/pierce/stats are
    /// logged when they change rather than on every cast.</summary>
    private static string castSummary;

    /// <summary>R7b: burning enemies the aura ticked last, and the stand-still
    /// clock Fan the Flames / Pyre of the Faithful read.</summary>
    private static int auraBurning;
    private static float stillTime;
    private static Vector3 stillAnchor;
    private static bool stillTracked;

    /// <summary>The skill the run is actually casting (skills.json id), which
    /// the reward table uses to offer only that skill's mods (R6).</summary>
    internal static string ActiveSkillId => active == null ? null : active.Id;

    internal static void Tick(MyPlayer player)
    {
        if (!Plugin.SkillsEnabled.Value || player == null || player.inventory == null)
        {
            DestroyRangeIndicator();
            return;
        }

        try
        {
            if (owner != player)
            {
                owner = player;
                Bolts.Clear();
                Effects.Clear();
                Waves.Clear();
                Trails.Clear();
                ClearEchoes();
                ClearThrows();
                PoisonedUntil.Clear();
                ClearModState();
                DestroyAura();
                spin = 0f;
                cycloneTicks = 0;
                auraBurning = 0;
                stillTracked = false;
                stillTime = 0f;
                ResetStorm();
                cooldown = 0.6f;
                casts = 0;
                announced = false;
                castOriginReported = 0;
                active = ResolveSkill(player);
                DisableBaseWeapons(player);
            }

            if (active == null)
            {
                DestroyRangeIndicator();
                return;
            }

            if (MyTime.paused || MyTime.timeScale <= 0f || player.inventory.pause)
            {
                return;
            }

            // MyTime.deltaTime is the game's own clock: it stops with the pause
            // menu (MyTime.Pause also switches Physics to Script) instead of
            // Unity's Time.timeScale, so nothing here ticks while paused.
            var delta = MyTime.deltaTime;
            if (delta <= 0f)
            {
                return;
            }

            PoeCharacters.Tick(player.playerRenderer, delta);
            RelicEngine.Tick(player, delta);
            UpdateBolts(delta);
            UpdateThrows(delta);
            UpdateWaves(delta);
            UpdateEffects(delta);
            UpdateTrails(delta);
            UpdateEchoes(delta);
            UpdatePersistent(player, delta);
            UpdateMovingStats();
            UpdateSlows();
            UpdateWildfire(player);
            UpdateFamiliar(player, delta);

            // SND: resolve this skill's host clip ahead of the first cast. Kept
            // after the frame systems and fully exception-safe, so audio can
            // never stop the run.
            SkillAudio.Warmup(active.Id);
            UpdateRangeIndicator(player, active, delta);

            cooldown -= delta;
            if (cooldown > 0f)
            {
                return;
            }

            if (Cast(player))
            {
                // Attack speed shortens our cooldown exactly as it shortens a
                // Megabonk weapon's: the level-up tomes are the only source.
                // A skill mod's own cost (Glacial Lance, Pyromantic Focus) is
                // applied after the stat, so both stay readable. The skill's
                // own level (R11b) cuts the cooldown by 0.6% per upgrade pick.
                cooldown = Mathf.Max(
                    0.08f,
                    active.Cooldown / StatScale(player, EStat.AttackSpeed) *
                    SkillMods.CooldownScale(active.Id) *
                    SkillMods.SkillCooldownScale());

                if (novaRefund > 0f)
                {
                    cooldown *= 1f - novaRefund;
                    novaRefund = 0f;
                }
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"Skill tick failed ({active?.Id}): {error}");
            cooldown = 2f;
        }
    }

    /// <summary>The skill bound to the character's class in classes.json.</summary>
    private static SkillDefinition ResolveSkill(MyPlayer player)
    {
        if (!ClassSwap.TryGetDefinition(player.character, out var definition))
        {
            Plugin.Logger.LogWarning($"No class definition for {player.character}; skills idle");
            return null;
        }

        if (!SkillDatabase.TryGet(definition.SkillId, out var skill))
        {
            Plugin.Logger.LogWarning($"Skill {definition.SkillId} is missing from skills.json");
            return null;
        }

        if (!announced)
        {
            announced = true;
            Plugin.Logger.LogInfo(
                $"{definition.Name}: {skill.Name} ({skill.Shape}), damage {skill.Damage}, " +
                $"cooldown {skill.Cooldown}s, tag {skill.Tag}");
        }

        return skill;
    }

    private static bool Cast(MyPlayer player)
    {
        var skill = active;
        if (!CastShape(player, skill))
        {
            return false;
        }

        // SND: a persistent shape (aura, cyclone, orbit) plays its sound when
        // the ring is created (EnsureAura), not on every damage tick.
        switch (skill.Shape)
        {
            case "aura":
            case "cyclone":
            case "orbit":
                break;

            default:
                SkillAudio.PlayCast(skill.Id);
                break;
        }

        return true;
    }

    private static bool CastShape(MyPlayer player, SkillDefinition skill)
    {
        switch (skill.Shape)
        {
            case "aura":
            case "cyclone":
                return CastAura(player, skill);

            case "nova":
                return CastNova(player, skill);

            case "melee":
                return CastMelee(player, skill);

            case "chain":
                return CastChain(player, skill);

            case "ground":
                return CastGround(player, skill);

            case "orbit":
                return CastAura(player, skill);

            default:
                return CastProjectile(player, skill);
        }
    }

    // ---------------------------------------------------------------- shapes

    private static bool CastProjectile(MyPlayer player, SkillDefinition skill)
    {
        var range = Mathf.Max(2f, skill.Range * RangeScale);
        var target = FindTarget(player.transform.position, range);
        if (target == null)
        {
            return false;
        }

        var origin = CastOrigin(player, skill);
        var aimPoint = target.transform.position + Vector3.up;
        var aim = (aimPoint - origin).normalized;

        // R7: the class's projectile mods answer one plan per cast. Volley
        // replaces the skill's own count and spread, Twin Flame adds balls,
        // Pyromantic Focus/Split Ash scale damage and the impact radius, and
        // Ricochet/Volley take the pierce away.
        var plan = SkillMods.Projectile(skill.Id);
        var count = plan.VolleyArrows > 0
            ? plan.VolleyArrows
            : Mathf.Max(1, skill.Projectiles + ExtraCount(player, EStat.Projectiles));
        var spread = plan.VolleyArrows > 0 ? plan.VolleyArc : skill.Spread * Mathf.Rad2Deg * (count - 1);
        if (count > 1 && spread <= 0.01f)
        {
            // A skill with no authored spread (Fireball) stacked its extra
            // bolts on one line: they read as a single projectile that passes
            // through the first enemy. Fan them, so an extra projectile is a
            // second bolt the player can see and that can hit its own target.
            spread = DefaultFanDegrees * (count - 1);
        }

        var pierce = plan.NoPierce ? 0 : skill.Pierce + StatFlat(player, EStat.ProjectileBounces);
        ReportCast(skill, player, count, pierce, spread);
        var crit = RollCrit(player);
        var damage = Damage(skill, player, crit) * plan.DamageScale * plan.VolleyDamage;
        var speed = Mathf.Max(4f, skill.Speed * SpeedScale * StatScale(player, EStat.ProjectileSpeedMultiplier));
        var colour = SkillVisuals.Colour(skill);

        // Hunter's Mark: the farthest enemy is the one the splash reads.
        markedEnemy = plan.HuntersMark ? FarthestEnemy(player.transform.position, range) : null;

        // Meteor Call (R11b): one meteor per impact point. The base cast has
        // one point; every extra projectile (Twin Flame included) adds its own
        // point on a ring around the target, and **each point rolls the card's
        // damage in its own radius**. The extra falling balls are pure
        // animation (at least three), so one strike does not look flat while
        // the damage count stays honest.
        if (plan.MeteorCall)
        {
            var radius = 1.1f * plan.MeteorRadius * plan.RadiusScale *
                         StatScale(player, EStat.SizeMultiplier);
            var impactPoints = 1 + ExtraCount(player, EStat.Projectiles) + plan.ExtraBolts;
            var primary = GroundPoint(target.transform.position, player, target);
            var landings = new Vector3[impactPoints];
            landings[0] = primary;

            for (var index = 1; index < impactPoints; index++)
            {
                var yaw = 360f * (index - 1) / Mathf.Max(1, impactPoints - 1);
                var offset = Quaternion.Euler(0f, yaw, 0f) * new Vector3(MeteorImpactSpread, 0f, 0f);
                landings[index] = GroundPoint(primary + offset, player, target);
            }

            var balls = Mathf.Max(3, impactPoints);
            var lastArrival = plan.MeteorDelay + MeteorStagger * (balls - 1);

            // One ground telegraph per impact point, at the real blast radius
            // (AoE tomes included). The operator's rule: the landing marker is
            // the small generic red ring, so “where will it hit?” is immediate.
            for (var index = 0; index < impactPoints; index++)
            {
                SpawnEffect(
                    landings[index],
                    radius * 2f,
                    lastArrival,
                    SkillVisuals.Ring(),
                    MeteorMarkerTint,
                    true,
                    grow: false);
            }

            var ball = 0;
            for (var index = 0; index < impactPoints; index++)
            {
                LaunchMeteor(
                    skill, colour, landings[index], radius,
                    plan.MeteorDelay + MeteorStagger * ball,
                    damage * plan.MeteorDamage, crit,
                    index == 0 ? plan.SplitBalls : 0, plan.SplitDamage,
                    index == 0 ? plan.RicochetBounces : 0,
                    ball, balls);
                ball++;
            }

            // Decorative balls on the primary point so a lone strike still
            // reads as a shower; they carry no damage.
            for (; ball < balls; ball++)
            {
                LaunchMeteor(
                    skill, colour, primary, radius,
                    plan.MeteorDelay + MeteorStagger * ball,
                    0f, crit, 0, 0f, 0,
                    ball, balls);
            }

            Counted(skill, $"{impactPoints} impact(s), {balls} meteor(s)");
            return true;
        }

        for (var index = 0; index < count; index++)
        {
            var yaw = count == 1 ? 0f : -spread * 0.5f + spread * index / (count - 1);
            var direction = Quaternion.Euler(0f, yaw, 0f) * aim;

            var bolt = new Bolt
            {
                Position = origin,
                Target = target,
                Direction = direction,
                Speed = speed,
                Life = Mathf.Max(0.4f, range / speed * 1.6f),
                Damage = damage,
                Crit = crit,
                Pierce = pierce,
                Radius = 1.1f * plan.RadiusScale * StatScale(player, EStat.SizeMultiplier),
                Splash = skill.Splash > 0f
                    ? skill.Splash * plan.RadiusScale * StatScale(player, EStat.SizeMultiplier)
                    : 0f,
                SplashShare = skill.SplashShare > 0f ? skill.SplashShare : 0.5f,
                Bounces = plan.RicochetBounces,
                Splits = plan.SplitBalls,
                SplitDamage = plan.SplitDamage,
                PierceDamage = plan.PierceDamage,
                Homing = count == 1 && plan.VolleyArrows == 0
            };

            bolt.Visual = ProjectileVisual(
                skill, origin, colour,
                out bolt.Mesh, out bolt.MeshRotation, out bolt.Glow);
            Bolts.Add(bolt);
        }

        // Twin Flame (Witch): the extra balls are the same shot at 35% of its
        // damage, thrown at a random angle, so they are not part of the fan.
        for (var extra = 0; extra < plan.ExtraBolts; extra++)
        {
            var yaw = UnityEngine.Random.Range(-60f, 60f);
            var bolt = new Bolt
            {
                Position = origin,
                Target = target,
                Direction = Quaternion.Euler(0f, yaw, 0f) * aim,
                Speed = speed,
                Life = Mathf.Max(0.4f, range / speed * 1.6f),
                Damage = damage * plan.ExtraBoltDamage,
                Crit = crit,
                Pierce = pierce,
                Radius = 1.1f * plan.RadiusScale * StatScale(player, EStat.SizeMultiplier),
                Splash = skill.Splash > 0f
                    ? skill.Splash * plan.RadiusScale * StatScale(player, EStat.SizeMultiplier)
                    : 0f,
                SplashShare = skill.SplashShare > 0f ? skill.SplashShare : 0.5f,
                PierceDamage = plan.PierceDamage
            };

            bolt.Visual = ProjectileVisual(
                skill, origin, colour,
                out bolt.Mesh, out bolt.MeshRotation, out bolt.Glow);
            Bolts.Add(bolt);
        }

        Counted(skill, target.name);
        return true;
    }

    /// <summary>
    /// One line when a projectile cast changes shape, naming the values the
    /// game's stat pipeline actually answered. This is what tells a "+1
    /// projectile" tome from a pierce bonus in the live log: the count and
    /// `EStat.Projectiles` move, `EStat.ProjectileBounces` and the pierce do not.
    /// </summary>
    private static void ReportCast(SkillDefinition skill, MyPlayer player, int count, int pierce, float spread)
    {
        var summary = $"{count} projectile(s), pierce {pierce}, fan {spread:0.#} deg";
        if (string.Equals(castSummary, summary, StringComparison.Ordinal))
        {
            return;
        }

        castSummary = summary;
        Plugin.Logger.LogInfo(
            $"cast: {skill.Id} {summary} " +
            $"(Projectiles {StatValue(player, EStat.Projectiles):0.##}/{PlayerStatsNew.GetBaseValue(EStat.Projectiles):0.##}, " +
            $"bounces {StatValue(player, EStat.ProjectileBounces):0.##}/{PlayerStatsNew.GetBaseValue(EStat.ProjectileBounces):0.##})");
    }

    private static float StatValue(MyPlayer player, EStat stat)
    {
        var stats = player != null && player.inventory != null ? player.inventory.playerStats : null;
        return stats == null ? 0f : stats.GetStat(stat);
    }

    /// <summary>Fire uses the PoE fireball texture; everything else gets the
    /// procedural orb tinted per element.</summary>
    private static Texture2D ProjectileTexture(SkillDefinition skill)
    {
        if (skill.Tag == "fire")
        {
            var fireball = PoeAssets.GetVfx("fireball");
            if (fireball != null)
            {
                return fireball;
            }
        }

        return SkillVisuals.Orb();
    }

    private static float ProjectileSize(SkillDefinition skill) => skill.Tag == "fire" ? 2.2f : 1.3f;

    /// <summary>The client's own projectile mesh for the element, if one was
    /// built: Fireball's core ball, Ice Shot's frozen arrow. Anything else
    /// keeps the billboard.</summary>
    private static string ProjectileEffect(SkillDefinition skill)
    {
        switch (skill.Tag)
        {
            case "fire":
                return "fireball";
            case "cold":
                return "ice-shot";
            default:
                return null;
        }
    }

    /// <summary>Longest extent of the projectile in game units, from the
    /// player's own height (the class fit is ~5 units): a fireball reads at a
    /// quarter of the body, the arrow at 0.7 of it — and its cross-section is
    /// widened separately, because a bolt flying away is foreshortened.</summary>
    private static float ProjectileMeshSize(SkillDefinition skill)
    {
        var height = owner != null && owner.height > 0.1f ? owner.height : 4.5f;
        return skill.Tag == "fire" ? height * 0.26f : height * ArrowBodyShare;
    }

    /// <summary>
    /// The bolt's visual: the PoE effect mesh aimed along the flight path plus
    /// the old elemental halo billboard in front of it (the mesh carries the
    /// client's shape and flame texture, the halo keeps the skill readable at
    /// distance). Falls back to the halo alone when the GLB is missing.
    /// </summary>
    private static GameObject ProjectileVisual(
        SkillDefinition skill,
        Vector3 origin,
        Color colour,
        out Transform mesh,
        out Quaternion meshRotation,
        out Transform glow)
    {
        mesh = null;
        meshRotation = Quaternion.identity;
        glow = null;

        var effectId = ProjectileEffect(skill);
        var model = effectId != null ? PoeAssets.GetEffect(effectId) : null;
        if (model == null)
        {
            var sprite = SkillVisuals.Sprite(
                "WraeclastBolt",
                ProjectileTexture(skill),
                colour,
                origin,
                Quaternion.identity,
                ProjectileSize(skill));
            glow = sprite.transform;
            return sprite;
        }

        var size = ProjectileMeshSize(skill);
        var extents = model.Mesh.bounds.size;
        var longest = Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z));
        var scale = size / Mathf.Max(0.01f, longest);

        var root = new GameObject("WraeclastBolt");
        root.transform.position = origin;

        var holder = SkillVisuals.Mount(root.transform, model, Color.white, scale);
        mesh = holder.transform;
        meshRotation = model.RootRotation;

        // The arrow's own axes: x is the shaft's width, y its length (the
        // model's long axis), z its thickness. Seen from behind, only the
        // cross-section is on screen, so an arrow that is merely long stays a
        // needle; the section is widened and the length left alone.
        if (skill.Tag != "fire")
        {
            var local = holder.transform.localScale;
            holder.transform.localScale = new Vector3(
                local.x * ArrowWidthScale, local.y, local.z * ArrowWidthScale);
        }

        var halo = SkillVisuals.Sprite(
            "Glow",
            ProjectileTexture(skill),
            colour,
            origin,
            Quaternion.identity,
            size * (skill.Tag == "fire" ? 1.5f : 1.1f));
        halo.transform.SetParent(root.transform, false);

        // S4: the halo is the only camera-facing part, so for the arrow it is
        // long and thin and follows the sight-line (see UpdateBolts); the
        // fireball keeps its round glow.
        if (skill.Tag != "fire")
        {
            halo.transform.localScale = new Vector3(
                size * ArrowStreakWidth, size * ArrowStreakLength, 1f);
        }

        glow = halo.transform;

        return root;
    }

    /// <summary>R11b: one falling meteor. A positive <paramref name="damage"/>
    /// is a real impact point; zero is a decorative ball of the same shower
    /// (the extra visual balls on a lone strike). The sky offset is cosmetic —
    /// it only keeps the balls from overlapping while they fall.</summary>
    private static void LaunchMeteor(
        SkillDefinition skill,
        Color colour,
        Vector3 landing,
        float radius,
        float arrival,
        float damage,
        bool crit,
        int splits,
        float splitDamage,
        int bounces,
        int index,
        int count)
    {
        var yaw = 360f * index / Mathf.Max(1, count);
        var offset = Quaternion.Euler(0f, yaw, 0f) * new Vector3(MeteorStartSpread, 0f, 0f);
        var echo = new Echo
        {
            Position = landing,
            Radius = radius,
            Life = arrival,
            Damage = damage,
            Crit = crit,
            Skill = skill,
            Meteor = true,
            Start = landing + Vector3.up * MeteorDropHeight + offset,
            Total = arrival,
            Splits = splits,
            SplitDamage = splitDamage,
            Bounces = bounces
        };
        echo.Visual = MeteorVisual(skill, colour, echo.Start);
        Echoes.Add(echo);
    }

    /// <summary>R11b item 1: the falling meteor draws the client's own Meteor
    /// effect — the glowing head shell plus the rock chunk inside it, both on
    /// the additive effect path, with the halo for distance. The GLBs are
    /// `meteor` and `meteor-rock`; a missing pair falls back to the fireball
    /// mesh exactly as before.</summary>
    private static GameObject MeteorVisual(SkillDefinition skill, Color colour, Vector3 start)
    {
        var head = PoeAssets.GetEffect("meteor");
        var rock = PoeAssets.GetEffect("meteor-rock");
        if (head == null && rock == null)
        {
            var fallback = ProjectileVisual(
                skill, start, colour, out var mesh, out var meshRotation, out _);
            if (mesh != null)
            {
                mesh.rotation = Quaternion.LookRotation(Vector3.down) * meshRotation;
            }

            return fallback;
        }

        var root = new GameObject("WraeclastMeteor");
        root.transform.position = start;

        if (head != null)
        {
            MountEffect(root.transform, head, MeteorHeadSize);
        }

        if (rock != null)
        {
            MountEffect(root.transform, rock, MeteorRockSize);
        }

        var halo = SkillVisuals.Sprite(
            "MeteorGlow",
            ProjectileTexture(skill),
            colour,
            start,
            Quaternion.identity,
            MeteorHeadSize * 1.6f);
        halo.transform.SetParent(root.transform, false);
        return root;
    }

    /// <summary>One effect GLB under a holder at a desired longest extent.</summary>
    private static void MountEffect(Transform parent, GltfSkin.StaticModel model, float size)
    {
        var extents = model.Mesh.bounds.size;
        var longest = Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z));
        SkillVisuals.Mount(parent, model, Color.white, size / Mathf.Max(0.01f, longest));
    }

    /// <summary>R11b: where a meteor lands — a downward ray under the target,
    /// skipping the player's and enemies' own colliders and non-upward faces,
    /// so the fireball touches the floor instead of stopping at the target's
    /// pivot. Falls back to the enemy's feet, then to its pivot.</summary>
    private static Vector3 GroundPoint(Vector3 near, MyPlayer player, Enemy target)
    {
        var hits = Physics.RaycastAll(near + Vector3.up * 2f, Vector3.down, 40f);
        var highest = float.MinValue;
        var point = near;
        var found = false;

        foreach (var hit in hits)
        {
            if (hit.collider == null || hit.normal.y < 0.4f)
            {
                continue;
            }

            if (player != null && hit.collider.transform.IsChildOf(player.transform))
            {
                continue;
            }

            if (hit.collider.GetComponentInParent<Enemy>() != null)
            {
                continue;
            }

            if (hit.point.y > highest)
            {
                highest = hit.point.y;
                point = hit.point;
                found = true;
            }
        }

        if (found)
        {
            return point;
        }

        return target != null ? target.GetFeetPosition() : near;
    }

    private static bool CastMelee(MyPlayer player, SkillDefinition skill)
    {
        var radius = Mathf.Max(1.2f, skill.Radius * RangeScale * StatScale(player, EStat.SizeMultiplier));
        var position = player.transform.position;
        var target = FindTarget(position, radius + 1.5f);
        if (target == null)
        {
            return false;
        }

        var aim = target.transform.position - position;
        aim.y = 0f;
        if (aim.sqrMagnitude < 0.0001f)
        {
            aim = player.transform.forward;
        }

        aim.Normalize();

        // S9c: the Shadow throws one of its own daggers at the monster it is
        // striking; the impact read stays the burst on each enemy hit.
        if (skill.Tag == "chaos")
        {
            ThrowWeapon(player, target);
        }

        // R7c: Serpent's Patience is the one cast-side Viper Strike mod.
        var plan = SkillMods.Melee(skill.Id, stillTime);
        var halfArc = Mathf.Max(0.4f, skill.Arc) * 0.5f;
        var crit = RollCrit(player);
        var damage = Damage(skill, player, crit) * plan.DamageScale;
        var hits = 0;

        ForEachEnemy(position, radius + 0.6f, enemy =>
        {
            var offset = enemy.transform.position - position;
            offset.y = 0f;
            if (offset.sqrMagnitude > 0.0001f && Vector3.Angle(aim, offset) > halfArc * Mathf.Rad2Deg)
            {
                return;
            }

            // R11b: Shadowstep no longer reads “from behind” (the monsters
            // always face the player); the strike carries the bonus itself.
            var dealt = damage * (plan.Shadowstep ? 1f + plan.BackBonus : 1f);

            if (plan.TwinFang)
            {
                // Twin Fang: two hits at 65%, the first cannot poison.
                suppressPoison = true;
                HitEx(enemy, dealt * plan.TwinDamage, crit, skill, noIgnite: false, noFreeze: false);
                suppressPoison = false;
                Hit(enemy, dealt * plan.TwinDamage, crit, skill);
            }
            else
            {
                Hit(enemy, dealt, crit, skill);
            }

            // The extra stacks ride the same strike, so they land on the same
            // screen frame as the hit that earned them.
            if (plan.ExtraPoisonStacks > 0)
            {
                ExtraPoison(enemy, skill, plan.ExtraPoisonStacks);
            }

            // S9b: the visual sits on the enemy that was hit — one small burst
            // per target — instead of one big sheet in front of the caster.
            if (skill.Tag == "chaos")
            {
                SpawnClientEffect(
                    enemy.GetFeetPosition() + Vector3.up * 0.02f,
                    1.5f,
                    0.3f,
                    "viper-strike",
                    SkillVisuals.Colour(skill),
                    true,
                    gain: 5f);
            }

            hits++;
        });

        // The bonus was spent: standing still again is what buys the next one.
        if (hits > 0 && plan.Patience)
        {
            stillTime = 0f;
        }

        if (plan.AddersGrace && PoisonStacks.Count > 0)
        {
            SetMoving(
                "adders-grace",
                EStat.MoveSpeedMultiplier,
                EStatModifyType.Addition,
                SkillMods.AdderBonusValue,
                0.5f);
        }

        // The physical melee shapes keep the arc ring; a chaos strike's read is
        // per target (see the burst spawned on each enemy above).
        if (skill.Tag != "chaos")
        {
            SpawnClientEffect(
                position + aim * radius * 0.6f,
                radius,
                0.22f,
                ElementSheet(skill),
                SkillVisuals.Colour(skill),
                flat: false,
                gain: 2f);
        }

        Counted(skill, $"{hits} target(s)");
        return hits > 0;
    }

    /// <summary>Ice Nova is a targeted burst: it lands on a monster and hits
    /// everything inside the radius around the impact (the melee shockwave's
    /// shape), not a ring travelling away from the caster. R7b's mods read as
    /// one plan: Convergence rewards edge hits and drags them inward, Glacial
    /// Echo schedules the repeat, Deep Freeze lengthens the freeze.</summary>
    private static bool CastNova(MyPlayer player, SkillDefinition skill)
    {
        var plan = SkillMods.Nova(skill.Id);
        var radius = Mathf.Max(1.5f, skill.Radius * RangeScale * StatScale(player, EStat.SizeMultiplier)) *
                     plan.RadiusScale;
        var feet = player.GetFeetPosition();
        var target = FindTarget(feet, radius + 1.5f);
        if (target == null)
        {
            return false;
        }

        var position = target.GetFeetPosition() + Vector3.up * 0.02f;
        var crit = RollCrit(player);
        var damage = Damage(skill, player, crit) * plan.DamageScale;

        // Arctic Charge: every 3rd landing cast is a 1 s charge, then a 40%
        // wider burst. The charge only counts casts that actually hit, so a
        // target just outside the burst used to advance the counter every frame
        // (a failed cast never starts the cooldown) and re-armed the burst
        // non-stop. The charge does not root either: at the cooldowns a real
        // build reaches, a 1 s ×0 move-speed stat was permanently zero.
        if (plan.ArcticCharge && arcticCasts >= SkillMods.ArcticEveryValue - 1)
        {
            arcticCasts = 0;
            Echoes.Add(new Echo
            {
                Position = position,
                Radius = radius * SkillMods.ArcticRadiusValue,
                Life = 1f,
                Damage = damage,
                Crit = crit,
                Skill = skill
            });
            Counted(skill, "arctic charge");
            return true;
        }

        var hits = 0;

        ForEachEnemy(position, radius + 0.6f, enemy =>
        {
            var dealt = damage;
            if (plan.EdgeDamage > 0f)
            {
                var offset = enemy.transform.position - position;
                offset.y = 0f;
                if (radius > 0.01f && offset.magnitude / radius >= plan.EdgeStart)
                {
                    // Edge: worth more, and the burst drags the target toward
                    // the centre instead of blasting it outwards.
                    dealt *= 1f + plan.EdgeDamage;
                    Pull(enemy, position, plan.Pull);
                }
            }

            Hit(enemy, dealt, crit, skill);
            hits++;
        });

        if (plan.ArcticCharge && hits > 0)
        {
            arcticCasts = Mathf.Min(arcticCasts + 1, SkillMods.ArcticEveryValue - 1);
        }

        if (hits > 0 && plan.Echoes > 0)
        {
            for (var index = 1; index <= plan.Echoes; index++)
            {
                Echoes.Add(new Echo
                {
                    Position = position,
                    Radius = radius,
                    Life = EchoSeconds * index,
                    Damage = damage * plan.EchoDamage,
                    Crit = crit,
                    Skill = skill
                });
            }
        }

        if (hits > 0 && plan.RimeCascade && hits >= 3)
        {
            novaRefund = plan.RimeRefund;
        }

        if (plan.Permafrost)
        {
            SpawnZone(
                position,
                radius,
                SkillMods.PermafrostSecondsValue,
                0f,
                false,
                skill,
                "ice-nova",
                SkillMods.PermafrostSlowValue);
        }

        if (plan.FrozenBulwark)
        {
            // The bulwark sits at the burst's edge: a frost field, not a wall.
            var edge = position;
            ForEachEnemy(position, radius + 0.6f, enemy =>
            {
                var offset = enemy.transform.position - position;
                offset.y = 0f;
                if (offset.magnitude >= radius * 0.6f)
                {
                    edge = enemy.transform.position;
                }
            });
            SpawnZone(
                edge,
                2f * AreaScale(),
                SkillMods.BulwarkSecondsValue,
                0f,
                false,
                skill,
                "ice-nova",
                SkillMods.BulwarkSlowValue);
        }

        // S9: the client's own shockwave ring (cold novas only) instead of the
        // procedural ring, which read as a generic white circle.
        SpawnClientEffect(
            position,
            radius * 2f,
            0.45f,
            skill.Tag == "cold" ? "ice-nova" : null,
            SkillVisuals.Colour(skill),
            true,
            grow: false,
            gain: 1.2f);

        Counted(skill, $"{hits} target(s)");
        return hits > 0;
    }

    /// <summary>
    /// Righteous Fire: a persistent aura that follows the caster, burning
    /// everything inside it every tick (Megachad's Aura behaviour). Cyclone
    /// shares the area tick but spins the model instead of drawing a ring;
    /// with Gathering Storm (R6) it loses the spin and charges a release
    /// instead.
    /// </summary>
    private static bool CastAura(MyPlayer player, SkillDefinition skill)
    {
        if (skill.Shape == "cyclone" && SkillMods.GatheringStorm(out var stormStacks))
        {
            return CastStorm(player, skill, stormStacks);
        }

        var cyclone = skill.Shape == "cyclone";
        var baseRadius = Mathf.Max(1.5f, skill.Radius * RangeScale * StatScale(player, EStat.SizeMultiplier));
        if (skill.Shape == "aura")
        {
            baseRadius = Mathf.Max(5.5f, baseRadius);
        }
        else if (cyclone)
        {
            // Vortex Lure's cost: a smaller spin, paid once whatever the stacks.
            baseRadius *= SkillMods.CycloneRadiusScale();
        }

        // R7b/R11b: one plan per tick for the aura or the spin.
        var plan = SkillMods.Aura(skill.Id, auraBurning, stillTime);
        var radius = baseRadius * plan.RadiusScale;
        var position = player.GetFeetPosition() + Vector3.up * 0.02f;
        var crit = RollCrit(player);
        var damage = Damage(skill, player, crit) * plan.DamageScale;

        // Momentum (Cyclone): last tick's hits decide this tick's damage.
        if (plan.Momentum)
        {
            if (Time.time > momentumAt)
            {
                momentumHits = 0;
            }

            damage *= SkillMods.MomentumScale(momentumHits);
        }

        // Iron Cyclone: the persistent spin pays with movement, not a root.
        if (plan.IronCyclone)
        {
            SetMoving("iron-cyclone", EStat.MoveSpeedMultiplier, EStatModifyType.Multiplication, SkillMods.IronMoveScale, 0.5f);
        }

        // Last Rites: under 30% Life, double the aura for 4 s once per 20 s.
        if (plan.LastRites && Time.time >= lastRitesReady && HpRatio() <= 0.30f)
        {
            lastRitesUntil = Time.time + SkillMods.RitesSecondsValue;
            lastRitesReady = Time.time + SkillMods.RitesCooldownValue;
            SetMoving(
                "last-rites-regen",
                EStat.HealthRegen,
                EStatModifyType.Multiplication,
                0f,
                SkillMods.RitesSecondsValue);
            Plugin.Logger.LogInfo(
                $"skill mod: Last Rites fired — aura x{SkillMods.RitesMultiplierValue:0.##} " +
                $"for {SkillMods.RitesSecondsValue:0.#} s");
        }

        if (plan.LastRites && Time.time < lastRitesUntil)
        {
            damage *= SkillMods.RitesMultiplierValue;
        }

        // Sacrificial Pyre: the enemies the last tick burned keep burning us.
        if (plan.SacrificialPyre && auraBurning > 0)
        {
            var interval = Mathf.Max(0.1f, active == null ? 0.5f : active.Cooldown);
            SelfBurn(0.015f * interval);
        }

        var hits = 0;
        var burning = 0;
        var vacuum = cyclone && plan.VacuumCut && cycloneTicks % 5 == 0;

        EnsureAura(player, skill, radius);

        ForEachEnemy(position, radius + 0.4f, enemy =>
        {
            var offset = enemy.transform.position - position;

            // Twin Orbit: the blades never touch the inner 40% of the ring.
            if (plan.TwinOrbit && offset.magnitude < radius * plan.OrbitInner)
            {
                return;
            }

            if (vacuum)
            {
                // Vacuum Cut: this ring only knocks the pack away.
                var movement = enemy.enemyMovement;
                if (movement != null && offset.magnitude > 0.4f)
                {
                    var away = offset;
                    away.y = 0f;
                    movement.Knockback(away.normalized, SkillMods.VacuumDistance);
                }

                hits++;
                return;
            }

            var dealt = damage;
            if (plan.Kindle)
            {
                var age = BurnSince.TryGetValue(enemy.GetInstanceID(), out var since) ? Time.time - since : 0f;
                dealt *= SkillMods.KindleMultiplier(age);
            }

            var container = new DamageContainer(1f, skill.Name);
            container.damage = dealt;
            container.direction = offset.normalized;
            container.element = Element(skill.Tag);
            container.damageEffect = DamageEffect(skill.Tag);
            container.crit = crit;
            container.knockback = 0f;
            enemy.DamageFromPlayerWeapon(container);

            // The aura is what burns: refresh the debuff on everything in it.
            if (skill.Tag == "fire")
            {
                var traits = skill.Traits;
                var seconds = AilmentSeconds(Seconds(traits != null ? traits.IgniteSeconds : 0, 3f));
                enemy.AddDebuff(EDebuff.Burn, container, seconds);
                RememberBurn(enemy, seconds);
                burning++;
            }
            else
            {
                ApplyAilment(enemy, container, skill);
            }

            if (plan.ZealotsCovenant)
            {
                Slow(enemy, plan.ZealotSlow, 0.6f);
            }

            RelicEngine.OnHit(enemy, container);

            // Vortex Lure: each spin drags the pack toward the blades through
            // the game's own knockback (boss resistance included).
            if (cyclone)
            {
                Lure(enemy, position);
            }

            hits++;
        });

        auraBurning = burning;

        if (plan.Momentum)
        {
            if (hits > 0)
            {
                momentumHits = Mathf.Min(50, momentumHits + hits);
                momentumAt = Time.time + 2f;
            }
        }

        if (plan.AshenVeil && burning >= 3)
        {
            SetMoving("ashen-veil", EStat.DamageReductionMultiplier, EStatModifyType.Multiplication, 1f - plan.VeilReduction, 0.5f);
        }

        if (cyclone)
        {
            cycloneTicks++;

            var offshoots = SkillMods.GaleStacks;
            if (offshoots > 0 && cycloneTicks % 3 == 0)
            {
                for (var index = 0; index < offshoots; index++)
                {
                    SpawnOffshoot(player, skill, damage * 0.4f, crit);
                }
            }

            // Razorwind: a blade trail behind the spin, one patch per metre. It
            // draws the cyclone aura's own sheet on the additive path (R11b:
            // the ground-zone path laid the premultiplied disc down opaque, so
            // the swirl read as a black square) and keeps the aura's spin.
            if (plan.Razorwind &&
                (lastRazorPosition == Vector3.zero ||
                 (position - lastRazorPosition).sqrMagnitude >= 1f))
            {
                lastRazorPosition = position;
                SpawnRazorwind(position, radius * 0.5f, 1.5f, damage * plan.RazorDamage, crit, skill);
            }

            // Blood Whirl: a crit tick heals once, not once per enemy.
            if (plan.BloodWhirl && crit && hits > 0)
            {
                HealPlayer(0.01f * SkillMods.Stacks(SkillMods.BloodWhirl));
            }

            // Afterimage: the pack empties, the spin leaves a ghost behind.
            if (plan.Afterimage && hits == 0 && lastCycloneHits > 0 && Time.time >= afterimageReady)
            {
                afterimageReady = Time.time + 1f;
                SpawnZone(
                    position,
                    radius * 0.5f,
                    SkillMods.AfterimageLife,
                    damage * SkillMods.AfterimageShare,
                    crit,
                    skill,
                    "cyclone",
                    0f);
            }

            lastCycloneHits = hits;
        }

        if (hits > 0)
        {
            // Cinder Trail: the ground behind the aura keeps burning for 1.5 s.
            if (plan.Trail)
            {
                SpawnTrail(position, radius, damage * plan.TrailDamage, crit, skill);
            }

            Counted(skill, $"{hits} target(s)");
        }

        return hits > 0;
    }

    private static float HpRatio()
    {
        var health = owner == null || owner.inventory == null ? null : owner.inventory.playerHealth;
        if (health == null)
        {
            return 1f;
        }

        try
        {
            return health.GetHpRatio();
        }
        catch (Exception)
        {
            return 1f;
        }
    }

    private static void HealPlayer(float share)
    {
        var health = owner == null || owner.inventory == null ? null : owner.inventory.playerHealth;
        if (health == null || share <= 0f)
        {
            return;
        }

        try
        {
            health.Heal(health.GetCombinedMaxHp() * share, true);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"skill mod: heal failed ({error.Message})");
        }
    }

    private static void SelfBurn(float share)
    {
        var health = owner == null || owner.inventory == null ? null : owner.inventory.playerHealth;
        if (health == null || share <= 0f)
        {
            return;
        }

        try
        {
            if (health.GetHpRatio() <= 0.15f)
            {
                return;
            }

            health.DamagePlayerExternal(health.GetCombinedMaxHp() * share, 0f, Vector3.zero, true, "Sacrificial Pyre");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"skill mod: Sacrificial Pyre failed ({error.Message})");
        }
    }

    /// <summary>Smouldering Step: the player's own dash leaves a fire patch
    /// (the host's dash is not ours to slow, so the cost is the skill cooldown).</summary>
    internal static void DashTrail(MyPlayer player)
    {
        if (player == null ||
            active == null ||
            !SkillMods.Active ||
            SkillMods.Stacks(SkillMods.SmoulderingStep) <= 0 ||
            !string.Equals(active.Id, "righteous-fire", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var position = player.GetFeetPosition() + Vector3.up * 0.02f;
        SpawnZone(
            position,
            2f * AreaScale(),
            SkillMods.SmoulderSecondsValue,
            Damage(active, player, false) * SkillMods.SmoulderShareValue,
            false,
            active,
            "righteous-fire",
            0f);
    }

    /// <summary>S9d/S9f: both persistent areas are drawn on the ground at the
    /// caster's feet — Righteous Fire burns the floor and the Cyclone swirl is
    /// the same kind of area; the operator tried the pelvis and the shoulders
    /// and asked for the floor back.</summary>
    private static Vector3 AuraPosition(MyPlayer player, SkillDefinition skill) =>
        player.GetFeetPosition() + Vector3.up * 0.02f;

    /// <summary>
    /// The area sheet lies in the plane it is drawn on: a downward ray finds the
    /// floor under the caster and the sheet takes its normal, so a ring on a
    /// slope follows the terrain instead of sinking into one side of it. Hits
    /// on the player's own colliders and non-upward faces are skipped, and the
    /// highest valid hit is the surface the player stands on.
    /// </summary>
    private static Quaternion AuraRotation(MyPlayer player, Vector3 position)
    {
        var normal = Vector3.up;
        var hits = Physics.RaycastAll(position + Vector3.up * 2f, Vector3.down, 6f);
        var highest = float.MinValue;
        foreach (var hit in hits)
        {
            if (hit.collider == null || hit.normal.y < 0.4f)
            {
                continue;
            }

            if (player != null && hit.collider.transform.IsChildOf(player.transform))
            {
                continue;
            }

            if (hit.point.y > highest)
            {
                highest = hit.point.y;
                normal = hit.normal;
            }
        }

        return Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.Euler(90f, 0f, 0f);
    }

    private static void EnsureAura(MyPlayer player, SkillDefinition skill, float radius)
    {
        if (auraVisual != null && auraSkill == skill)
        {
            // Fan the Flames resizes the aura as the burning count changes:
            // scale the live ring instead of rebuilding (and re-logging) it.
            if (Mathf.Abs(auraRadius - radius) >= 0.05f)
            {
                auraRadius = radius;
                auraVisual.transform.localScale = Vector3.one * radius * 2f;
            }

            return;
        }

        DestroyAura();

        // S9: the client's own ground sheet carries the persistent area — the
        // Righteous Fire rune circle or the Cyclone swirl — on the additive
        // material so its black surround vanishes. The procedural ring (or the
        // Fireball ball, which read as a flat projectile at the feet) stays the
        // fallback when the sheet is missing.
        var cyclone = skill.Shape == "cyclone";
        var texture = PoeAssets.GetGroundVfx(cyclone ? "cyclone" : "righteous-fire", cyclone ? 1f : 2.5f);
        auraVisual = texture != null
            ? SkillVisuals.EffectSprite(
                "WraeclastAura",
                texture,
                Color.white,
                AuraPosition(player, skill),
                AuraRotation(player, AuraPosition(player, skill)),
                radius * 2f)
            : SkillVisuals.Sprite(
                "WraeclastAura",
                SkillVisuals.Ring(),
                SkillVisuals.Colour(skill),
                AuraPosition(player, skill),
                AuraRotation(player, AuraPosition(player, skill)),
                radius * 2f);

        auraSkill = skill;
        auraRadius = radius;
        SkillAudio.PlayCast(skill.Id);
        Plugin.Logger.LogInfo(
            $"{skill.Name}: persistent aura at feet y {auraVisual.transform.position.y:0.##}, radius {radius:0.#} m");
    }

    private static void DestroyAura()
    {
        if (auraVisual != null)
        {
            UnityEngine.Object.Destroy(auraVisual);
        }

        auraVisual = null;
        auraSkill = null;
        auraRadius = 0f;
        auraSpin = 0f;
        DestroyTwinOrbit();
    }

    // -------------------------------------------------------- range indicator

    /// <summary>
    /// The radius the indicator draws: a burst or a swing shows the circle its
    /// hits are actually checked against (<c>radius + 0.6</c>, the constant the
    /// casts use), a targeted shape shows the reach at which it locks an enemy.
    /// </summary>
    private static float SkillRange(MyPlayer player, SkillDefinition skill)
    {
        var area = Mathf.Max(1.5f, skill.Radius * RangeScale * StatScale(player, EStat.SizeMultiplier));
        switch (skill.Shape)
        {
            case "melee":
                return Mathf.Max(1.2f, area) + 0.6f;
            case "nova":
                return area * SkillMods.Nova(skill.Id).RadiusScale + 0.6f;
            case "cyclone":
            {
                // Mirrors CastAura: Vortex Lure's one-time shrink, then the
                // per-tick plan radius, plus the 0.4 m the spin's hit test adds.
                var radius = area * SkillMods.CycloneRadiusScale();
                return radius * SkillMods.Aura(skill.Id, auraBurning, stillTime).RadiusScale + 0.4f;
            }
            case "ground":
                return Mathf.Max(3f, skill.Range * RangeScale);
            case "chain":
                return Mathf.Max(3f, skill.Range * RangeScale);
            default:
                return Mathf.Max(2f, skill.Range * RangeScale);
        }
    }

    /// <summary>
    /// Keeps the blue reach circle under the player. Righteous Fire's aura and
    /// the Scion's orbiting blades already draw their own area, so those shapes
    /// get no second ring; the Duelist's Cyclone does get one (the swirl alone
    /// reads smaller than the radius the spin actually hits). The plan objects
    /// behind the radius are rebuilt per call, so the radius is re-read at 4 Hz
    /// while the circle follows every frame (rotating only on the slower pass,
    /// because aligning it to the floor casts a ray).
    /// </summary>
    private static void UpdateRangeIndicator(MyPlayer player, SkillDefinition skill, float delta)
    {
        if (!Plugin.RangeIndicator.Value || player == null || skill == null ||
            skill.Shape == "aura" || skill.Shape == "orbit")
        {
            DestroyRangeIndicator();
            return;
        }

        rangeIndicatorNext -= delta;
        if (rangeIndicator != null && rangeIndicatorSkill == skill && rangeIndicatorNext > 0f)
        {
            rangeIndicator.transform.position = player.GetFeetPosition() + Vector3.up * 0.03f;
            return;
        }

        rangeIndicatorNext = 0.25f;
        var radius = SkillRange(player, skill);
        if (radius <= 0.05f)
        {
            DestroyRangeIndicator();
            return;
        }

        var position = player.GetFeetPosition() + Vector3.up * 0.03f;
        if (rangeIndicator == null || rangeIndicatorSkill != skill)
        {
            DestroyRangeIndicator();
            rangeIndicator = SkillVisuals.Sprite(
                "WraeclastRange",
                SkillVisuals.RangeRing(),
                RangeColour,
                position,
                AuraRotation(player, position),
                radius * 2f);
            rangeIndicatorSkill = skill;
            rangeIndicatorRadius = radius;
            Plugin.Logger.LogInfo($"{skill.Name}: range indicator {radius:0.#} m");
            return;
        }

        rangeIndicator.transform.position = position;
        rangeIndicator.transform.rotation = AuraRotation(player, position);
        if (Mathf.Abs(rangeIndicatorRadius - radius) >= 0.02f)
        {
            rangeIndicatorRadius = radius;
            rangeIndicator.transform.localScale = Vector3.one * radius * 2f;
        }
    }

    private static void DestroyRangeIndicator()
    {
        if (rangeIndicator != null)
        {
            UnityEngine.Object.Destroy(rangeIndicator);
        }

        rangeIndicator = null;
        rangeIndicatorSkill = null;
        rangeIndicatorRadius = -1f;
        rangeIndicatorNext = 0f;
    }

    /// <summary>Per-frame upkeep for the persistent effects: aura follows the
    /// player, cyclone spins the PoE model, Gathering Storm channels instead.</summary>
    private static void UpdatePersistent(MyPlayer player, float delta)
    {
        if (active == null)
        {
            return;
        }

        // R7b: the stand-still clock the Marauder's Pyre of the Faithful reads.
        // The anchor only moves when the player does, so standing still for a
        // second is a real second and any step resets it.
        var position = player.transform.position;
        if (!stillTracked)
        {
            stillTracked = true;
            stillAnchor = position;
            stillTime = 0f;
        }
        else if ((position - stillAnchor).sqrMagnitude > 0.01f)
        {
            stillAnchor = position;
            stillTime = 0f;
        }
        else
        {
            stillTime += delta;
        }

        if (active.Shape == "aura" || active.Shape == "cyclone")
        {
            if (auraVisual != null)
            {
                var auraPosition = AuraPosition(player, active);
                auraVisual.transform.position = auraPosition;

                // S9: a still client sheet reads as a decal; the rune circle
                // drifts and the whirlwind turns, so the area looks alive. The
                // sheet is laid flat by Rx(+90), so its local +z is world **-y**:
                // a clockwise turn on screen (the model's own spin, +Y) is a
                // negative angle here. The spin is composed on the right, around
                // the sheet's own normal, so it also works once the sheet has
                // been tilted onto a slope.
                var rate = active.Shape == "cyclone" ? -240f : -18f;
                auraSpin = Mathf.Repeat(auraSpin + rate * delta, 360f);
                auraVisual.transform.rotation =
                    AuraRotation(player, auraPosition) *
                    Quaternion.AngleAxis(auraSpin, Vector3.forward);
            }
        }

        if (active.Shape == "cyclone")
        {
            if (SkillMods.GatheringStorm(out var stormStacks))
            {
                ChannelStorm(player, stormStacks, delta);
                return;
            }

            spin = Mathf.Repeat(spin + delta * 1080f, 360f);
            SetModelSpin(spin);
            DestroyStormVisual();

            // R11b item 17: Twin Orbit draws two Starforge blades spinning
            // around the player at the ring's radius.
            if (SkillMods.Stacks(SkillMods.TwinOrbit) > 0)
            {
                EnsureTwinOrbit(player, Mathf.Max(1.5f, auraRadius * 0.75f));
                orbitSpin = Mathf.Repeat(orbitSpin + delta * 240f, 360f);
                var bodyHeight = PoeCharacters.HeightOf(player.playerRenderer);
                if (bodyHeight <= 0.1f)
                {
                    bodyHeight = player.height;
                }

                orbitVisual.transform.position =
                    AuraPosition(player, active) +
                    Vector3.up * Mathf.Max(1.5f, bodyHeight * TwinOrbitHeightShare);
                orbitVisual.transform.rotation = Quaternion.Euler(0f, orbitSpin, 0f);
            }
            else
            {
                DestroyTwinOrbit();
            }
        }
        else
        {
            if (spin != 0f)
            {
                spin = 0f;
                SetModelSpin(0f);
            }

            DestroyTwinOrbit();
        }
    }

    /// <summary>Builds the two-blade Twin Orbit visual once per activation: the
    /// Starforge mesh with its own texture on the Unlit/Texture material, one
    /// copy per side, mounted at the ring radius.</summary>
    private static void EnsureTwinOrbit(MyPlayer player, float radius)
    {
        if (orbitVisual != null)
        {
            return;
        }

        var model = WeaponModels.Get("starforge");
        if (model == null)
        {
            return;
        }

        orbitVisual = new GameObject("WraeclastTwinOrbit");
        for (var side = -1; side <= 1; side += 2)
        {
            var blade = new GameObject(side < 0 ? "BladeL" : "BladeR");
            blade.transform.SetParent(orbitVisual.transform, false);
            blade.transform.localPosition = new Vector3(side * radius, 0f, 0f);

            // Both blades point outward, so the pair is a true horizontal
            // mirror (the old side*90 pointed both toward the middle).
            blade.transform.localRotation = Quaternion.Euler(0f, 0f, -side * 90f);

            var mesh = new GameObject("Mesh");
            mesh.transform.SetParent(blade.transform, false);
            mesh.transform.localRotation = model.RootRotation;
            mesh.transform.localScale = model.RootScale * TwinOrbitScale;
            mesh.AddComponent<MeshFilter>().sharedMesh = model.Mesh;
            var renderer = mesh.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = PoeCharacters.BuildMaterial((Material)null, model.Texture);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
    }

    private static void DestroyTwinOrbit()
    {
        if (orbitVisual != null)
        {
            UnityEngine.Object.Destroy(orbitVisual);
        }

        orbitVisual = null;
        orbitSpin = 0f;
    }

    private static void SetModelSpin(float angle)
    {
        var renderer = owner != null ? owner.playerRenderer : null;
        if (renderer == null)
        {
            return;
        }

        PoeCharacters.SetSpin(renderer, angle);
    }

    // --------------------------------------------------------- skill mods (R6)

    /// <summary>
    /// Gathering Storm's channel: the spin is replaced by a charge that only
    /// grows while the player **stands still** — moving is always allowed, it
    /// simply restarts the charge (the operator's “immobile” instead of the old
    /// hard root, which made the mod unplayable). Charging only starts near a
    /// target, otherwise the mod would stall the whole floor; walking out of
    /// range drops it. A short recovery after a release is ours: the design
    /// fixes the charge and release numbers, not how the player repositions
    /// between storms.
    /// </summary>
    private static void ChannelStorm(MyPlayer player, int stacks, float delta)
    {
        SetModelSpin(0f);
        if (auraVisual != null)
        {
            DestroyAura();
        }

        var stormRadius = Mathf.Max(1.5f, active.Radius * RangeScale * StatScale(player, EStat.SizeMultiplier)) *
                          SkillMods.StormRadiusMultiplier;

        if (stormRecovery > 0f)
        {
            stormRecovery -= delta;
            stormCharge = 0f;
            stormStill = false;
            DestroyStormVisual();
            return;
        }

        if (stormCharge >= SkillMods.StormCharge(stacks))
        {
            return;
        }

        stormRetarget -= delta;
        if (stormRetarget <= 0f)
        {
            stormRetarget = 0.25f;
            var range = Mathf.Max(6f, active.Radius * RangeScale * 4f);
            stormEngaged = FindTarget(player.transform.position, range) != null;
        }

        if (!stormEngaged)
        {
            stormCharge = 0f;
            stormStill = false;
            DestroyStormVisual();
            return;
        }

        var position = player.transform.position;
        if (!stormStill)
        {
            stormStill = true;
            stormAnchor = position;
        }
        else if ((position - stormAnchor).sqrMagnitude > 0.01f)
        {
            // Movement breaks the channel rather than being forbidden: the
            // charge restarts from the new spot on the next still frame.
            stormCharge = 0f;
            stormAnchor = position;
            StormChargeVisual(player, 0f, stormRadius);
            return;
        }

        stormCharge += delta;
        StormChargeVisual(player, stormCharge / SkillMods.StormCharge(stacks), stormRadius);
    }

    /// <summary>The storm's charge read: a flat client ring under the caster
    /// that fills out to the release radius as the charge climbs, so the wait
    /// is visible (the operator could not see the old “bar”). It replaces the
    /// spin swirl while the channel runs.</summary>
    private static void StormChargeVisual(MyPlayer player, float progress, float radius)
    {
        if (player == null || active == null)
        {
            return;
        }

        if (stormVisual == null)
        {
            stormVisual = SkillVisuals.Sprite(
                "WraeclastStormCharge",
                SkillVisuals.Ring(),
                new Color(0.75f, 0.9f, 1f, 0.95f),
                AuraPosition(player, active),
                Quaternion.Euler(90f, 0f, 0f),
                1f);
        }

        stormVisual.transform.position = AuraPosition(player, active) + Vector3.up * 0.04f;
        stormVisual.transform.rotation = Quaternion.Euler(90f, Time.time * 90f, 0f);
        stormVisual.transform.localScale =
            Vector3.one * Mathf.Max(0.6f, radius * 2f * Mathf.Clamp01(progress));
    }

    private static void DestroyStormVisual()
    {
        if (stormVisual != null)
        {
            UnityEngine.Object.Destroy(stormVisual);
            stormVisual = null;
        }
    }

    /// <summary>The release of a fully charged storm: 2.2x the spin radius and
    /// the design's multiplier (3x / 3.6x / 4.2x by stack). Vortex Lure's pull
    /// still applies, so the pack is dragged into the blast.</summary>
    private static bool CastStorm(MyPlayer player, SkillDefinition skill, int stacks)
    {
        if (stormCharge < SkillMods.StormCharge(stacks))
        {
            return false;
        }

        var radius = Mathf.Max(1.5f, skill.Radius * RangeScale * StatScale(player, EStat.SizeMultiplier)) *
                     SkillMods.StormRadiusMultiplier;
        var position = player.GetFeetPosition() + Vector3.up * 0.02f;
        var crit = RollCrit(player);
        var damage = Damage(skill, player, crit) * SkillMods.StormRelease(stacks);
        var hits = 0;

        ForEachEnemy(position, radius + 0.6f, enemy =>
        {
            Lure(enemy, position);
            Hit(enemy, damage, crit, skill);
            hits++;
        });

        SpawnClientEffect(
            position,
            radius * 2f,
            0.5f,
            "cyclone",
            SkillVisuals.Colour(skill),
            true,
            grow: false,
            spin: -260f);
        stormCharge = 0f;
        stormRecovery = StormRecovery;
        stormStill = false;
        DestroyStormVisual();
        SkillAudio.PlayCast(skill.Id);
        Counted(skill, $"{hits} target(s), storm release");
        return true;
    }

    /// <summary>Ends the channel and gives the player back control. Safe to call
    /// when nothing is charged: the run reset and the class swap use it too.</summary>
    internal static void ResetStorm()
    {
        stormCharge = 0f;
        stormRecovery = 0f;
        stormEngaged = false;
        stormRetarget = 0f;
        stormStill = false;
        stormAnchor = Vector3.zero;
        DestroyStormVisual();
    }

    /// <summary>Cyclone's pull. The base skill pulls gently through its
    /// <c>traits.pullStrength</c> (dead data until 2026-11-05); Vortex Lure
    /// replaces that with its stack-scaled, stronger drag. Both go through the
    /// game's own knockback path, so boss resistance and the 5-unit ceiling
    /// stay <c>EnemyMovementRb</c>'s.</summary>
    private static void Lure(Enemy enemy, Vector3 position)
    {
        if (enemy == null)
        {
            return;
        }

        var stacks = SkillMods.VortexStacks;
        var traits = active == null ? null : active.Traits;
        var basePull = traits == null ? 0f : (float)traits.PullStrength * 0.15f;
        if (stacks <= 0 && basePull <= 0f)
        {
            return;
        }

        try
        {
            var movement = enemy.enemyMovement;
            if (movement == null)
            {
                return;
            }

            var offset = position - enemy.transform.position;
            offset.y = 0f;
            var distance = offset.magnitude;
            if (distance < 0.4f)
            {
                return;
            }

            var pull = stacks > 0
                ? SkillMods.VortexPull(distance, stacks)
                : Mathf.Min(0.6f, basePull);
            movement.Knockback(offset / distance, pull);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"cyclone pull: failed ({error.Message})");
        }
    }

    /// <summary>Gale Offshoots: a small cyclone thrown in a random direction,
    /// 4 m of travel at 40% of the spin's damage and no pierce limit.</summary>
    private static void SpawnOffshoot(MyPlayer player, SkillDefinition skill, float damage, bool crit)
    {
        var direction = Quaternion.Euler(0f, UnityEngine.Random.value * 360f, 0f) * Vector3.forward;
        var origin = player.GetFeetPosition() + Vector3.up * 0.6f;
        const float speed = 14f;
        const float distance = 4f;

        var bolt = new Bolt
        {
            Position = origin,
            Direction = direction,
            Speed = speed,
            Life = distance / speed,
            Damage = damage,
            Crit = crit,
            Pierce = 999
        };

        // R11b: the gust is Megabonk's own Desert tornado (a mesh-particle
        // prefab) cloned as a mini tornado. The flat cyclone sheet stays the
        // fallback when the game's effect manager or the prefab is missing.
        var gust = PoeAssets.GetGroundVfx("cyclone", 1f);
        var tornado = SpawnTornado(origin);
        bolt.Visual = tornado != null
            ? tornado
            : gust != null
                ? SkillVisuals.EffectSprite(
                    "WraeclastOffshoot", gust, Color.white, origin, Quaternion.Euler(90f, 0f, 0f), 1.2f)
                : SkillVisuals.Sprite(
                    "WraeclastOffshoot",
                    SkillVisuals.Ring(),
                    SkillVisuals.Colour(skill),
                    origin,
                    Quaternion.Euler(90f, 0f, 0f),
                    1.2f);
        Bolts.Add(bolt);
    }

    /// <summary>R11b item 6: clones Megabonk's roaming tornado prefab
    /// (`EffectManager.Instance.tornado`, a mesh-particle system) as a pure
    /// visual: the `Tornado` controller, its rigidbody, collider and audio are
    /// disabled/removed, the particle systems are started and the whole clone
    /// is shrunk to <see cref="OffshootTornadoScale"/>. Returns null when the
    /// manager or the prefab is unavailable, so the caller keeps its sheet.
    /// </summary>
    private static GameObject SpawnTornado(Vector3 origin)
    {
        try
        {
            var manager = EffectManager.Instance;
            if (manager == null || manager.tornado == null)
            {
                return null;
            }

            var clone = (GameObject)UnityEngine.Object.Instantiate(
                manager.tornado, origin, Quaternion.identity);
            clone.name = "WraeclastTornado";
            clone.transform.localScale = Vector3.one * OffshootTornadoScale;

            var script = clone.GetComponent<Tornado>();
            if (script != null)
            {
                script.enabled = false;
            }

            var body = clone.GetComponent<Rigidbody>();
            if (body != null)
            {
                UnityEngine.Object.Destroy(body);
            }

            var collider = clone.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.Destroy(collider);
            }

            foreach (var audio in clone.GetComponentsInChildren<AudioSource>(true))
            {
                audio.enabled = false;
            }

            foreach (var system in clone.GetComponentsInChildren<ParticleSystem>(true))
            {
                system.Play();
            }

            if (!tornadoLogged)
            {
                tornadoLogged = true;
                Plugin.Logger.LogInfo(
                    $"gale offshoots: '{manager.tornado.name}' tornado cloned at scale {OffshootTornadoScale:0.##}");
            }

            return clone;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"gale offshoots: tornado clone failed ({error.Message})");
            return null;
        }
    }

    // ------------------------------------------------------- skill mods (R7b)

    /// <summary>R11b item 5: Razorwind's patch. The damage is the same Trail a
    /// ground zone uses, but the read is the Cyclone aura's own client sheet on
    /// the **additive** path — the premultiplied disc's alpha is fully opaque,
    /// so the alpha-sprite path (which every ground zone uses) drew the black
    /// surround as a square. It stays small and turns at the aura's −240°/s,
    /// so the trail reads as the spinning blades' wake.</summary>
    private static void SpawnRazorwind(
        Vector3 position,
        float radius,
        float life,
        float damage,
        bool crit,
        SkillDefinition skill)
    {
        Trails.Add(new Trail
        {
            Position = position,
            Radius = radius,
            Life = life,
            Pulse = 0f,
            Damage = damage,
            Crit = crit,
            Skill = skill
        });

        SpawnClientEffect(
            position,
            radius * 2f,
            life,
            "cyclone",
            SkillVisuals.Colour(skill),
            flat: true,
            grow: false,
            spin: -240f);
    }

    /// <summary>Cinder Trail: one patch of burning ground at the aura's feet.
    /// The patch carries the client's own Righteous Fire rune circle (the same
    /// sheet the aura draws), not the procedural ring that reads as a monster
    /// explosion; it pulses every 0.5 s for its 1.5 s of life, so three patches
    /// are alive while walking.</summary>
    private static void SpawnTrail(Vector3 position, float radius, float damage, bool crit, SkillDefinition skill)
    {
        var patch = Mathf.Max(1.2f, radius * 0.5f);
        Trails.Add(new Trail
        {
            Position = position,
            Radius = patch,
            Life = TrailLifeSeconds,
            Pulse = 0f,
            Damage = damage,
            Crit = crit,
            Skill = skill
        });

        SpawnClientEffect(
            position,
            patch * 2f,
            TrailLifeSeconds,
            "righteous-fire",
            SkillVisuals.Colour(skill),
            flat: true,
            grow: false,
            gain: 1.5f);
    }

    private static void UpdateTrails(float delta)
    {
        for (var index = Trails.Count - 1; index >= 0; index--)
        {
            var trail = Trails[index];
            trail.Life -= delta;
            trail.Pulse -= delta;

            if (trail.Pulse <= 0f)
            {
                trail.Pulse += TrailPulseSeconds;
                var damage = trail.Damage;
                var crit = trail.Crit;
                var skill = trail.Skill;
                Enemy zap = null;
                var zapPosition = Vector3.zero;
                var zapDistance = float.MaxValue;
                if (damage > 0f)
                {
                    ForEachEnemy(trail.Position, trail.Radius, enemy =>
                    {
                        Hit(enemy, damage, crit, skill);
                        if (trail.Ball)
                        {
                            var distance = (enemy.transform.position - trail.Position).sqrMagnitude;
                            if (distance < zapDistance)
                            {
                                zapDistance = distance;
                                zap = enemy;
                                zapPosition = enemy.transform.position;
                            }
                        }
                    });
                }

                // Ball Lightning's pulse is a zap: one arc from the orb to the
                // nearest enemy it caught, so the orb is visibly the source.
                if (trail.Ball && zap != null)
                {
                    Beam(
                        trail.Position + Vector3.up * BallOrbHeight,
                        zapPosition + Vector3.up * 0.6f,
                        Color.white,
                        null);
                }

                // Static Field: real lightning arcs on the ground each pulse,
                // so the patch reads as a crackling spark, not a flat image.
                if (trail.Spark)
                {
                    SparkCrackle(trail.Position, trail.Radius);
                }

                if (trail.Slow > 0f)
                {
                    ForEachEnemy(trail.Position, trail.Radius, enemy => Slow(enemy, trail.Slow, 0.6f));
                }
            }

            if (trail.Life <= 0f)
            {
                Trails.RemoveAt(index);
            }
        }
    }

    /// <summary>R11b: the run reset drops pending echoes, and a meteor still in
    /// the air must take its fireball with it.</summary>
    private static void ClearEchoes()
    {
        foreach (var echo in Echoes)
        {
            if (echo.Visual != null)
            {
                UnityEngine.Object.Destroy(echo.Visual);
            }
        }

        Echoes.Clear();
    }

    private static void UpdateEchoes(float delta)
    {
        for (var index = Echoes.Count - 1; index >= 0; index--)
        {
            var echo = Echoes[index];
            echo.Life -= delta;

            // R11b: the Meteor Call fireball travels down from its sky start
            // to the landing point over its telegraph.
            if (echo.Meteor && echo.Visual != null && echo.Total > 0f)
            {
                var progress = Mathf.Clamp01(1f - echo.Life / echo.Total);
                echo.Visual.transform.position = Vector3.Lerp(echo.Start, echo.Position, progress);
            }

            if (echo.Life > 0f)
            {
                continue;
            }

            EchoHit(echo);
            Echoes.RemoveAt(index);
        }
    }

    /// <summary>Glacial Echo's repeat: the burst happens again for a share of
    /// the damage and applies no ailment at all, so it cannot freeze.</summary>
    private static void EchoHit(Echo echo)
    {
        var damage = echo.Damage;
        var crit = echo.Crit;
        var skill = echo.Skill;

        // R11b: a meteor's fall ends here; the fireball is replaced by the
        // impact, and Split Ash's balls leave from the landing point.
        if (echo.Meteor && echo.Visual != null)
        {
            UnityEngine.Object.Destroy(echo.Visual);
            echo.Visual = null;
        }

        // R11b: a meteor impact runs the shared projectile pipeline below, so
        // every projectile mod works on it as it does on a bolt.
        if (echo.Meteor)
        {
            MeteorHit(echo);
            return;
        }

        ForEachEnemy(echo.Position, echo.Radius + 0.6f, enemy =>
        {
            var container = new DamageContainer(1f, skill.Name);
            container.damage = damage;
            container.direction = (enemy.transform.position - echo.Position).normalized;
            container.element = Element(skill.Tag);
            container.damageEffect = DamageEffect(skill.Tag);
            container.crit = crit;
            container.knockback = 0f;
            enemy.DamageFromPlayerWeapon(container);
            RelicEngine.OnHit(enemy, container);
        });

        SpawnClientEffect(
            echo.Position,
            echo.Radius * 2f,
            0.35f,
            ElementSheet(skill),
            SkillVisuals.Colour(skill),
            flat: true,
            grow: false,
            gain: 1.5f);
    }

    /// <summary>R11b: one Meteor Call impact — the blast hits every enemy in
    /// its radius through the same <see cref="ImpactEffects"/> pipeline a bolt
    /// uses, then Split Ash throws its balls and Ricochet continues on to a
    /// fresh enemy, so the meteor combines with the whole projectile kit.</summary>
    private static void MeteorHit(Echo echo)
    {
        // A follow-up meteor is the animation of the same strike: it bursts on
        // the same spot for the read, but the first one already dealt the
        // damage, so this one never hits again.
        if (echo.Damage <= 0f)
        {
            MeteorImpact(echo);
            return;
        }

        var caught = new List<int>();

        ForEachEnemy(echo.Position, echo.Radius + 0.6f, enemy =>
        {
            caught.Add(enemy.GetInstanceID());
            ImpactEffects(
                enemy, echo.Damage, echo.Crit, echo.Skill,
                0f, 0, noIgnite: false, noFreeze: false);
        });

        MeteorImpact(echo);

        if (echo.Splits > 0)
        {
            SpawnMeteorSplits(echo);
        }

        if (echo.Bounces > 0)
        {
            var hop = new Bolt
            {
                Position = echo.Position,
                Damage = echo.Damage,
                Crit = echo.Crit,
                Radius = echo.Radius,
                Bounces = echo.Bounces
            };

            foreach (var id in caught)
            {
                hop.Hit.Add(id);
            }

            hop.Visual = ProjectileVisual(
                echo.Skill, hop.Position, SkillVisuals.Colour(echo.Skill),
                out hop.Mesh, out hop.MeshRotation, out hop.Glow);

            if (Bounce(hop))
            {
                // Draw the hop itself: the ball is fast and small against the
                // blast, so a camera-facing fire streak names the next target.
                if (hop.Target != null)
                {
                    Link(
                        echo.Position + Vector3.up * 0.4f,
                        hop.Target.transform.position + Vector3.up * 0.6f,
                        PoeAssets.GetGroundVfx("fireball", 1.5f),
                        SkillVisuals.Colour(echo.Skill));
                }

                Bolts.Add(hop);
            }
            else
            {
                UnityEngine.Object.Destroy(hop.Visual);
            }
        }
    }

    /// <summary>R11b: one meteor's impact read — the flat ground decal plus an
    /// upright, growing plume, so the blast is visible from the chase camera
    /// and not only as a circle under the ball.</summary>
    private static void MeteorImpact(Echo echo)
    {
        var sheet = ElementSheet(echo.Skill);
        SpawnClientEffect(
            echo.Position,
            echo.Radius * 2f,
            0.35f,
            sheet,
            SkillVisuals.Colour(echo.Skill),
            flat: true,
            grow: false,
            gain: 1.5f);

        SpawnClientEffect(
            echo.Position,
            echo.Radius * 2.2f,
            0.45f,
            sheet,
            SkillVisuals.Colour(echo.Skill),
            flat: false,
            grow: true,
            gain: 1.8f);
    }

    /// <summary>R11b: Split Ash on a Meteor Call impact — the burst throws the
    /// balls a projectile impact would, fanned around the landing point and
    /// each for the card's share of the meteor's damage, never re-splitting.</summary>
    private static void SpawnMeteorSplits(Echo echo)
    {
        var colour = SkillVisuals.Colour(echo.Skill);
        var speed = Mathf.Max(4f, echo.Skill.Speed * SpeedScale);

        for (var index = 0; index < echo.Splits; index++)
        {
            var yaw = 360f * index / Mathf.Max(1, echo.Splits) + UnityEngine.Random.Range(-15f, 15f);
            var child = new Bolt
            {
                Position = echo.Position,
                Direction = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward,
                Speed = speed,
                Life = 0.9f,
                Damage = echo.Damage * echo.SplitDamage,
                Crit = echo.Crit,
                Pierce = 0,
                Radius = echo.Radius
            };

            child.Visual = ProjectileVisual(
                echo.Skill, child.Position, colour,
                out child.Mesh, out child.MeshRotation, out child.Glow);
            Bolts.Add(child);
        }
    }

    /// <summary>Convergence: drags an edge hit toward the burst's centre through
    /// the game's own knockback, so boss resistance still applies.</summary>
    private static void Pull(Enemy enemy, Vector3 position, float meters)
    {
        if (enemy == null || meters <= 0f)
        {
            return;
        }

        try
        {
            var movement = enemy.enemyMovement;
            if (movement == null)
            {
                return;
            }

            var offset = position - enemy.transform.position;
            offset.y = 0f;
            var distance = offset.magnitude;
            if (distance < 0.4f)
            {
                return;
            }

            movement.Knockback(offset / distance, Mathf.Min(meters, distance));
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"convergence: pull failed ({error.Message})");
        }
    }

    private static bool CastChain(MyPlayer player, SkillDefinition skill)
    {
        var plan = SkillMods.Chain(skill.Id);
        var range = Mathf.Max(3f, skill.Range * RangeScale);
        var target = FindTarget(player.transform.position, range);
        if (target == null)
        {
            return false;
        }

        var colour = SkillVisuals.Colour(skill);
        var crit = RollCrit(player);
        var damage = Damage(skill, player, crit) * plan.DamageScale;
        var from = CastOrigin(player, skill);
        // R11b item 9: an extra projectile is an extra jump on the chain, so the
        // Quantity/Swarm tomes are fun for the Scion instead of being filtered
        // out of its drafts. Half a projectile per stack accumulates, like the
        // bolts: two common stacks are one more jump.
        var jumps = Mathf.Max(
            1,
            skill.Chains + 1 + ExtraCount(player, EStat.Projectiles) + plan.ExtraJumps - plan.LostJumps);
        var beam = PoeAssets.GetVfx("arc");
        if (beam == null)
        {
            beam = SkillVisuals.Beam();
        }

        // Ball Lightning: the bolt never chains; it becomes a hovering orb that
        // zaps what is around it (R11b item 4: the client's own orb mesh).
        if (plan.BallLightning)
        {
            SpawnBallOrb(
                target.GetFeetPosition(),
                SkillMods.BallRadiusValue * AreaScale(),
                SkillMods.BallSecondsValue,
                damage * SkillMods.BallShareValue,
                crit,
                skill);
            Counted(skill, "ball lightning");
            return true;
        }

        Enemy last;
        float lastDamage;
        Vector3 lastPosition;

        if (plan.ForkedFate)
        {
            // Forked Fate: two bolts at once, each with half the jumps.
            var firstTarget = target;
            var secondTarget = NearestEnemy(
                player.transform.position, range, new HashSet<int> { firstTarget.GetInstanceID() }) ?? firstTarget;
            var each = Mathf.Max(
                1,
                jumps / 2 + (SkillMods.Stacks(SkillMods.ForkedFate) - 1));
            RunChain(skill, plan, firstTarget, from, each, damage, crit, colour, beam,
                out last, out lastDamage, out lastPosition);
            RunChain(skill, plan, secondTarget, from, each, damage, crit, colour, beam,
                out _, out _, out _);
        }
        else
        {
            RunChain(skill, plan, target, from, jumps, damage, crit, colour, beam,
                out last, out lastDamage, out lastPosition);
        }

        if (last != null && plan.AnchorStacks > 0)
        {
            // Storm Anchor: the last enemy is held, then the sky answers.
            try
            {
                var container = new DamageContainer(1f, skill.Name);
                container.damage = 0f;
                container.direction = Vector3.down;
                container.element = Element(skill.Tag);
                container.damageEffect = DamageEffect(skill.Tag);
                container.knockback = 0f;
                last.AddDebuff(EDebuff.Stun, container, plan.AnchorStun);
            }
            catch (Exception error)
            {
                Plugin.Logger.LogWarning($"storm anchor: stun failed ({error.Message})");
            }

            Echoes.Add(new Echo
            {
                Position = lastPosition,
                Radius = plan.AnchorRadius * AreaScale(),
                Life = plan.AnchorDelay,
                Damage = lastDamage * plan.AnchorDamage,
                Crit = crit,
                Skill = skill
            });
        }

        // Thunderhead: every 4th cast calls a bolt on the target's pack.
        if (plan.Thunderhead && ++chainCasts % SkillMods.ThunderEveryValue == 0)
        {
            Echoes.Add(new Echo
            {
                Position = target.transform.position + Vector3.up,
                Radius = 3.5f * AreaScale(),
                Life = 0.4f,
                Damage = damage * SkillMods.ThunderDamageValue,
                Crit = crit,
                Skill = skill
            });
        }

        if (plan.GalvanicRush)
        {
            SetMoving(
                "galvanic-rush",
                EStat.MoveSpeedMultiplier,
                EStatModifyType.Addition,
                SkillMods.GalvanicBonusValue,
                SkillMods.GalvanicSecondsValue);
        }

        Counted(skill, target.name);
        return true;
    }

    private static void RunChain(
        SkillDefinition skill,
        SkillMods.ChainPlan plan,
        Enemy target,
        Vector3 from,
        int jumps,
        float damage,
        bool crit,
        Color colour,
        Texture2D beam,
        out Enemy last,
        out float lastDamage,
        out Vector3 lastPosition)
    {
        var used = new HashSet<int>();
        var current = target;
        last = null;
        lastDamage = 0f;
        lastPosition = Vector3.zero;

        for (var index = 0; index < jumps && current != null; index++)
        {
            used.Add(current.GetInstanceID());
            var dealt = damage * Mathf.Pow(plan.Falloff, index);

            // Static Cling: the first hit of the chain cannot stun.
            suppressStun = plan.StaticCling && index == 0;
            Hit(current, dealt, crit, skill);
            suppressStun = false;

            // Conductivity: a stunned hit passes the stun to two neighbours.
            if (plan.Conductivity && HasDebuff(current, EDebuff.Stun))
            {
                var exclude = new HashSet<int> { current.GetInstanceID() };
                for (var spread = 0; spread < SkillMods.ConductivityTargetsValue; spread++)
                {
                    var next = NearestEnemy(current.transform.position, 6f, exclude);
                    if (next == null)
                    {
                        break;
                    }

                    exclude.Add(next.GetInstanceID());
                    var container = new DamageContainer(1f, skill.Name);
                    container.damage = 0f;
                    container.direction = Vector3.zero;
                    container.element = Element(skill.Tag);
                    container.damageEffect = DamageEffect(skill.Tag);
                    container.knockback = 0f;
                    TryStun(next, container, skill, 1.5f);
                }
            }

            var position = current.transform.position + Vector3.up;
            Beam(from, position, colour, beam);
            from = position;

            // Static Field: the jump scars the ground where it landed.
            if (plan.Spark)
            {
                SparkZone(position, dealt * plan.SparkDamage, crit, skill, plan);
            }

            last = current;
            lastDamage = dealt;
            lastPosition = position;

            current = NearestEnemy(from, plan.ChainLightning ? SkillMods.ChainRangeValue : 6f, used);
        }
    }

    /// <summary>Static Field: one small lightning patch where a jump landed.
    /// Same lifecycle as a trail (a pulse every 0.5 s), with the spark's own
    /// life and radius so the two mods read differently on screen.</summary>
    private static void SparkZone(
        Vector3 position,
        float damage,
        bool crit,
        SkillDefinition skill,
        SkillMods.ChainPlan plan)
    {
        var radius = plan.SparkRadius * AreaScale();
        Trails.Add(new Trail
        {
            Position = position,
            Radius = radius,
            Life = plan.SparkSeconds,
            Pulse = 0f,
            Damage = damage,
            Crit = crit,
            Skill = skill,
            Spark = true
        });

        SparkCrackle(position, radius);
    }

    /// <summary>Item 22: Static Field's read is living lightning, not a flat
    /// sheet. Each call throws a handful of short client Arc ribbons across the
    /// patch (the same mesh the chain's jumps draw), re-fired on every pulse so
    /// the field keeps crackling for its whole life.</summary>
    private static void SparkCrackle(Vector3 position, float radius)
    {
        var ground = position + Vector3.up * 0.15f;
        for (var index = 0; index < 3; index++)
        {
            var yaw = UnityEngine.Random.Range(0f, 360f);
            var reach = radius * UnityEngine.Random.Range(0.5f, 1f);
            var end = ground + Quaternion.Euler(0f, yaw, 0f) * new Vector3(reach, 0f, 0f);
            Beam(ground, end, Color.white, null);
        }
    }

    // ------------------------------------------------------- skill mods (R7c)

    /// <summary>
    /// A poisoned enemy died: the Shadow's death-side mods read the corpse
    /// here. Toxic Bloom leaves a cloud at the body (poison's own colour), Venom
    /// Spread passes the poison to a neighbour. Both are the same branch, so at
    /// most one is ever live; a corpse that never carried our poison does
    /// nothing.
    /// </summary>
    internal static void OnEnemyKilled(Enemy enemy, DamageContainer container)
    {
        if (enemy == null || owner == null || !SkillMods.Active || MyTime.paused)
        {
            return;
        }

        try
        {
            var id = enemy.GetInstanceID();
            var position = enemy.transform.position + Vector3.up * 0.02f;
            var skill = active;
            var wasBurning = HasDebuff(enemy, EDebuff.Burn) ||
                (BurningUntil.TryGetValue(id, out var burnUntil) && Time.time <= burnUntil);
            var wasFrozen = HasDebuff(enemy, EDebuff.Freeze) ||
                (FrozenUntil.TryGetValue(id, out var frozenUntil) && Time.time <= frozenUntil);
            var poisoned = Poisoned(enemy);

            // R11b death effects, whichever skill landed the kill.
            if (wasBurning && SkillMods.Stacks(SkillMods.Immolation) > 0 && skill != null)
            {
                var maxHp = 0f;
                try
                {
                    maxHp = EnemyStats.GetHp(enemy);
                }
                catch (Exception)
                {
                    // A corpse may refuse the stat query; the hit damage stands in.
                }

                var burst = maxHp > 0f
                    ? maxHp * SkillMods.ImmolateShareValue
                    : (container != null ? container.damage : 0f) * 0.5f;
                ForEachEnemy(
                    position,
                    SkillMods.ImmolateRadiusValue * AreaScale(),
                    other => Hit(other, burst, false, skill));
            }

            if (wasBurning && SkillMods.Stacks(SkillMods.IgniteBloom) > 0 && skill != null && container != null)
            {
                ForEachEnemy(
                    position,
                    3f * AreaScale(),
                    other => Hit(other, container.damage * SkillMods.IgniteBloomShareValue, false, skill));
            }

            if (wasFrozen && SkillMods.Stacks(SkillMods.ColdSnap) > 0 && skill != null)
            {
                var freeze = new DamageContainer(1f, "Cold Snap");
                freeze.damage = 0f;
                freeze.direction = Vector3.zero;
                freeze.element = EElement.Ice;
                freeze.damageEffect = EDamageEffect.None;
                freeze.knockback = 0f;
                ForEachEnemy(position, SkillMods.ColdSnapRadiusValue * AreaScale(), other =>
                {
                    try
                    {
                        other.AddDebuff(EDebuff.Freeze, freeze, AilmentSeconds(SkillMods.ColdSnapSecondsValue));
                    }
                    catch (Exception)
                    {
                        // A pooled corpse has no debuff slot left.
                    }
                });
            }

            if (SkillMods.Stacks(SkillMods.Fracture) > 0 &&
                skill != null &&
                string.Equals(skill.Id, "ice-nova", StringComparison.Ordinal))
            {
                var shard = container != null ? container.damage : Damage(skill, owner, false);
                SpawnShards(
                    position,
                    shard,
                    false,
                    skill,
                    SkillMods.FractureShardsValue,
                    SkillMods.FractureShareValue);
            }

            if (wasBurning && SkillMods.Stacks(SkillMods.SacrificialPyre) > 0 &&
                skill != null &&
                string.Equals(skill.Id, "righteous-fire", StringComparison.Ordinal) &&
                Vector3.Distance(enemy.transform.position, owner.GetFeetPosition()) <= auraRadius + 0.6f)
            {
                if (Time.time > pyreHealWindow + 1f)
                {
                    pyreHealWindow = Time.time;
                    pyreHealed = 0f;
                }

                if (pyreHealed < SkillMods.PyreHealCapValue)
                {
                    var heal = Mathf.Min(
                        SkillMods.PyreHealValue,
                        SkillMods.PyreHealCapValue - pyreHealed);
                    pyreHealed += heal;
                    HealPlayer(heal);
                }
            }

            if (poisoned && SkillMods.Stacks(SkillMods.Haemorrhage) > 0)
            {
                try
                {
                    owner.inventory.ChangeGold(SkillMods.HaemorrhageGoldValue);
                }
                catch (Exception error)
                {
                    Plugin.Logger.LogWarning($"haemorrhage: gold failed ({error.Message})");
                }
            }

            if (poisoned && SkillMods.Stacks(SkillMods.LastBreath) > 0)
            {
                cooldown = 0f;
                lastBreathUntil = Time.time + 2f;
                lastBreathMult = 1f + 0.05f * SkillMods.Stacks(SkillMods.LastBreath);
            }

            // R7c: the Shadow's death-side mods read the corpse here.
            if (skill != null &&
                string.Equals(skill.Id, ViperStrikeSkillId, StringComparison.OrdinalIgnoreCase) &&
                poisoned)
            {
                var bloom = SkillMods.ToxicBloomShare;
                PoisonedUntil.Remove(id);

                if (bloom > 0f)
                {
                    Trails.Add(new Trail
                    {
                        Position = position,
                        Radius = SkillMods.ToxicBloomRadius * AreaScale(),
                        Life = SkillMods.ToxicBloomSeconds,
                        Pulse = 0f,
                        Damage = Damage(skill, owner, false) * bloom,
                        Crit = false,
                        Skill = skill
                    });

                    SpawnClientEffect(
                        position,
                        SkillMods.ToxicBloomRadius * 2f * AreaScale(),
                        SkillMods.ToxicBloomSeconds,
                        ElementSheet(skill),
                        SkillVisuals.Colour(skill),
                        flat: true,
                        grow: false,
                        gain: 2f);
                }

                var spreads = SkillMods.VenomSpreadTargets;
                if (spreads > 0)
                {
                    Plugin.Logger.LogInfo(
                        $"venom spread: {spreads} target(s) in {SkillMods.VenomSpreadRadius:0.#} m");
                    // One jump per corpse: the neighbour gets the stacks, and its
                    // own death can pass them on again (that is the next corpse).
                    var exclude = new HashSet<int> { id };
                    for (var index = 0; index < spreads; index++)
                    {
                        var next = NearestEnemy(
                            position,
                            SkillMods.VenomSpreadRadius * AreaScale(),
                            exclude);
                        if (next == null)
                        {
                            break;
                        }

                        ExtraPoison(next, skill, Mathf.Max(1, (int)SkillTraitsPoisonStacks(skill)));
                        exclude.Add(next.GetInstanceID());
                    }
                }
            }

            // A pooled corpse must not hand its counters to the next spawn.
            BurnSince.Remove(id);
            BurningUntil.Remove(id);
            BurningEnemies.Remove(id);
            CombustionHits.Remove(id);
            FrostbiteHits.Remove(id);
            PrisonHits.Remove(id);
            ShatteredUntil.Remove(id);
            FrozenUntil.Remove(id);
            PoisonStacks.Remove(id);
            PoisonedUntil.Remove(id);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"death effect failed ({error.Message})");
        }
    }

    /// <summary>The skill's own poison stack count (skills.json
    /// <c>traits.poisonStacks</c>), so a spread corpse passes exactly what the
    /// strike applies.</summary>
    private static double SkillTraitsPoisonStacks(SkillDefinition skill) =>
        skill != null && skill.Traits != null ? skill.Traits.PoisonStacks : 0;

    private static bool CastGround(MyPlayer player, SkillDefinition skill)
    {
        var range = Mathf.Max(3f, skill.Range * RangeScale);
        var target = FindTarget(player.transform.position, range);
        if (target == null)
        {
            return false;
        }

        var radius = Mathf.Max(1.5f, skill.Radius * RangeScale * StatScale(player, EStat.SizeMultiplier));
        var position = target.transform.position;
        var crit = RollCrit(player);
        var damage = Damage(skill, player, crit);
        var hits = 0;

        ForEachEnemy(position, radius + 0.6f, enemy =>
        {
            Hit(enemy, damage, crit, skill);
            hits++;
        });

        SpawnClientEffect(
            position,
            radius * 2f,
            0.4f,
            ElementSheet(skill),
            SkillVisuals.Colour(skill),
            flat: true,
            gain: 1.5f);
        Counted(skill, $"{hits} target(s)");
        return true;
    }

    // ----------------------------------------------------------- damage / fx

    private static float Damage(SkillDefinition skill, MyPlayer player, bool crit)
    {
        var stats = player.inventory.playerStats;
        var multiplier = stats != null ? stats.GetStat(EStat.DamageMultiplier) : 1f;
        if (multiplier <= 0f)
        {
            multiplier = 1f;
        }

        var damage = skill.Damage * multiplier;

        // Element relics (S5+) write FireDamage / IceDamage / LightningDamage
        // through the game's own stat pipeline; our skills are not the host's
        // weapon damage, so nothing read them and 17 relics had 53-100% of
        // their power doing nothing. The bonus is `current - base`, which
        // works whatever unit the stat uses (a multiplier stat has base 1, a
        // flat stat base 0), exactly like the CritDamage read below.
        damage *= 1f + ElementBonus(stats, skill.Tag);

        if (skill.Shape == "aura" && stats != null)
        {
            damage += stats.GetStat(EStat.MaxHealth) * 0.08f;
        }

        // The skill's own level (R11b, operator 2026-11-02): every upgrade pick
        // is one level, +4% damage compounding, 40 levels max.
        damage *= SkillMods.SkillDamageScale();

        // Last Breath: a poisoned kill leaves a short, stack-scaled window.
        if (Time.time < lastBreathUntil)
        {
            damage *= lastBreathMult;
        }

        // Gale Offshoots' cost (R6): the spin itself deals 15% less, paid once.
        if (skill.Shape == "cyclone")
        {
            damage *= SkillMods.CycloneDamageScale();
        }

        if (crit && stats != null)
        {
            // CritDamage is a bonus fraction (the game's own weapon upgrades
            // write it Flat), so the crit deals the default x2 plus whatever
            // the sheet has added on top of the stat's base.
            var bonus = stats.GetStat(EStat.CritDamage) - PlayerStatsNew.GetBaseValue(EStat.CritDamage);
            damage *= 2f + Mathf.Max(0f, bonus);
        }

        // Relic triggers that are a damage multiplier while a condition holds
        // (Dying Sun's stand-still ramp) are applied here: our skills are the
        // only damage source they scale, and the value moves every frame.
        return damage * RelicEngine.CastMultiplier(player);
    }

    /// <summary>The part of a stat above its own base, clamped positive. The
    /// game's bases differ (a multiplier stat starts at 1, a flat one at 0), so
    /// this is the only unit-agnostic way to read "how much did the relics add".</summary>
    private static float StatBonus(PlayerStatsNew stats, EStat stat)
    {
        if (stats == null)
        {
            return 0f;
        }

        var value = stats.GetStat(stat) - PlayerStatsNew.GetBaseValue(stat);
        return value > 0f ? value : 0f;
    }

    private static float ElementBonus(PlayerStatsNew stats, string tag) => tag switch
    {
        "fire" => StatBonus(stats, EStat.FireDamage),
        "cold" => StatBonus(stats, EStat.IceDamage),
        "lightning" => StatBonus(stats, EStat.LightningDamage),
        _ => 0f
    };

    private static PlayerStatsNew OwnerStats() =>
        owner == null || owner.inventory == null ? null : owner.inventory.playerStats;

    /// <summary>A relic's BurnChance / FreezeChance is a flat fraction (0.6 =
    /// 60%); the skill's own roll is in percent.</summary>
    private static float AilmentChanceBonus(EStat stat) => StatBonus(OwnerStats(), stat) * 100f;

    private static bool RollCrit(MyPlayer player)
    {
        var stats = player.inventory.playerStats;
        if (stats == null)
        {
            return false;
        }

        var chance = stats.GetStat(EStat.CritChance);
        if (chance > 1f)
        {
            chance /= 100f;
        }

        chance *= SkillMods.CritScale(active == null ? null : active.Id);
        return UnityEngine.Random.value < chance;
    }

    private static void Hit(Enemy enemy, float damage, bool crit, SkillDefinition skill) =>
        HitEx(enemy, damage, crit, skill, noIgnite: false, noFreeze: false);

    private static void HitEx(
        Enemy enemy, float damage, bool crit, SkillDefinition skill, bool noIgnite, bool noFreeze)
    {
        if (enemy == null)
        {
            return;
        }

        // R11b on-hit multipliers.
        if (SkillMods.Stacks(SkillMods.CorrosiveBite) > 0 && Poisoned(enemy))
        {
            var stacks = PoisonStacks.TryGetValue(enemy.GetInstanceID(), out var known) ? known : 1;
            damage *= SkillMods.CorrosiveMultiplier(stacks);
        }

        if (SkillMods.Stacks(SkillMods.StaticCling) > 0 && HasDebuff(enemy, EDebuff.Stun))
        {
            damage *= 1f + SkillMods.ClingBonusValue;
        }

        var container = new DamageContainer(1f, skill.Name);
        container.damage = damage;
        container.direction = (enemy.transform.position - owner.transform.position).normalized;
        container.element = Element(skill.Tag);
        container.damageEffect = DamageEffect(skill.Tag);
        container.crit = crit;
        container.knockback = 0.35f * StatScale(owner, EStat.KnockbackMultiplier);
        enemy.DamageFromPlayerWeapon(container);
        ApplyAilment(enemy, container, skill, noIgnite, noFreeze);

        // R7c: the poison is read here, while the debuff is certainly live —
        // the death path clears it before our kill hook is reached.
        if (skill.Tag == "chaos" && Poisoned(enemy))
        {
            var id = enemy.GetInstanceID();
            PoisonedUntil[id] = Time.time + PoisonMemorySeconds;
            PoisonStacks[id] = (PoisonStacks.TryGetValue(id, out var count) ? count : 0) + 1;
        }

        if (skill.Tag == "fire" && HasDebuff(enemy, EDebuff.Burn))
        {
            RememberBurn(enemy, 3f);
        }

        if (skill.Tag == "cold")
        {
            var id = enemy.GetInstanceID();
            FrostbiteHits[id] = (FrostbiteHits.TryGetValue(id, out var hits) ? hits : 0) + 1;
        }

        RelicEngine.OnHit(enemy, container);
    }

    /// <summary>Base poison spread: the struck enemy passes a weaker poison to
    /// at most two neighbours inside the trait's radius. One hop only (the
    /// helper never calls itself), so a pack cannot cascade.</summary>
    private static void SpreadPoison(
        Enemy source,
        DamageContainer container,
        SkillDefinition skill,
        float seconds,
        SkillTraits traits)
    {
        var radius = traits != null ? traits.PoisonSpread : 0;
        if (radius <= 0.1f || container == null || source == null)
        {
            return;
        }

        var sourceId = source.GetInstanceID();
        var applied = 0;
        ForEachEnemy(source.transform.position, (float)radius, other =>
        {
            if (applied >= 2 || other.GetInstanceID() == sourceId)
            {
                return;
            }

            var copy = new DamageContainer(1f, skill.Name);
            copy.damage = container.damage * 0.5f;
            copy.direction = container.direction;
            copy.element = container.element;
            copy.damageEffect = container.damageEffect;
            copy.crit = false;
            copy.knockback = 0f;
            other.AddDebuff(EDebuff.Poison, copy, AilmentSeconds(seconds));
            applied++;
        });
    }

    /// <summary>R7c: a couple of poison stacks on top of the strike's own
    /// ailment roll (Serpent's Patience and Venom Spread).</summary>
    private static void ExtraPoison(Enemy enemy, SkillDefinition skill, int stacks)
    {
        if (enemy == null || stacks <= 0)
        {
            return;
        }

        try
        {
            var container = new DamageContainer(1f, skill.Name);
            container.damage = 0f;
            container.direction = Vector3.zero;
            container.element = Element(skill.Tag);
            container.damageEffect = DamageEffect(skill.Tag);
            container.knockback = 0f;
            var traits = skill.Traits;
            enemy.AddDebuff(
                EDebuff.Poison,
                container,
                AilmentSeconds(Seconds(traits != null ? traits.PoisonSeconds : 0, 4f)),
                stacks);
            var id = enemy.GetInstanceID();
            PoisonedUntil[id] = Time.time + PoisonMemorySeconds;
            PoisonStacks[id] = (PoisonStacks.TryGetValue(id, out var known) ? known : 0) + Mathf.Max(1, stacks);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"poison stacks failed ({error.Message})");
        }
    }

    /// <summary>The corpse (or the live enemy) still carries poison: either the
    /// game's own debuff or the poison our chaos hits remembered.</summary>
    private static bool Poisoned(Enemy enemy)
    {
        if (enemy == null)
        {
            return false;
        }

        try
        {
            if (enemy.HasDebuff(EDebuff.Poison))
            {
                return true;
            }
        }
        catch (Exception)
        {
            // A dead enemy can refuse the query; the memory below still answers.
        }

        return PoisonedUntil.TryGetValue(enemy.GetInstanceID(), out var until) && Time.time <= until;
    }

    private static EElement Element(string tag)
    {
        switch (tag)
        {
            case "fire":
                return EElement.Fire;
            case "cold":
                return EElement.Ice;
            case "lightning":
                return EElement.Lightning;
            default:
                return EElement.Neutral;
        }
    }

    /// <summary>
    /// The damage-number effect the game colours the hit with. Lightning is
    /// deliberately not used: <c>EDamageEffect</c> has the member, but
    /// <c>MyColorUtility.GetDamageEffectColor</c> has no colour arm for it and
    /// throws "Color not implemented for effect: …" (nine switch targets, seven
    /// colour fields at 0x198-0x1F8, one grey arm, one throw arm; out-of-range
    /// values such as <c>None</c> never reach it because
    /// <c>DamageContainer.GetColor</c> returns the default colour when
    /// <c>damageEffect == EDamageEffect.None</c>, field 0x30). Arc keeps its
    /// tint through the beam/ring visuals instead.
    /// </summary>
    private static EDamageEffect DamageEffect(string tag)
    {
        switch (tag)
        {
            case "fire":
                return EDamageEffect.Fire;
            case "chaos":
                return EDamageEffect.Poison;
            case "lightning":
                return EDamageEffect.None;
            default:
                return EDamageEffect.None;
        }
    }

    /// <summary>Wildfire's cost is paid by the ignite it feeds, not by the
    /// fireball's own hit: the Burn takes a 20% weaker copy, so the impact's
    /// damage number, Combustion and the shards keep their real value.</summary>
    private static DamageContainer IgniteContainer(DamageContainer source, SkillDefinition skill)
    {
        var scale = SkillMods.WildfireIgniteScale;
        if (source == null || scale >= 1f)
        {
            return source;
        }

        var copy = new DamageContainer(1f, skill.Name);
        copy.damage = source.damage * scale;
        copy.direction = source.direction;
        copy.element = source.element;
        copy.damageEffect = source.damageEffect;
        copy.crit = source.crit;
        copy.knockback = source.knockback;
        return copy;
    }

    /// <summary>Element ailments mapped onto the game's own debuffs. R11b adds
    /// the no-ignite/no-freeze flags (Rolling Ember, Ice Prison, shards) and the
    /// cold/poison upgrade hooks.</summary>
    private static void ApplyAilment(
        Enemy enemy, DamageContainer container, SkillDefinition skill, bool noIgnite = false, bool noFreeze = false)
    {
        var traits = skill.Traits;
        var id = enemy.GetInstanceID();
        switch (skill.Tag)
        {
            case "fire":
                if (noIgnite)
                {
                    break;
                }

                // The skill's own ignite chance, plus a fire relic's BurnChance
                // (a flat fraction: 0.6 = +60 points).
                var burnChance = traits != null && traits.IgnitePct > 0 ? traits.IgnitePct * 100.0 : 30.0;
                burnChance += AilmentChanceBonus(EStat.BurnChance);
                if (UnityEngine.Random.value * 100f < burnChance)
                {
                    var seconds = AilmentSeconds(Seconds(traits != null ? traits.IgniteSeconds : 0, 3f));
                    enemy.AddDebuff(EDebuff.Burn, IgniteContainer(container, skill), seconds);
                    RememberBurn(enemy, seconds);
                }

                break;

            case "cold":
                if (noFreeze)
                {
                    break;
                }

                // Ice Prison: the first two hits cannot freeze, the third roots.
                if (SkillMods.Stacks(SkillMods.IcePrison) > 0)
                {
                    var hits = (PrisonHits.TryGetValue(id, out var count) ? count : 0) + 1;
                    if (hits >= 3)
                    {
                        PrisonHits[id] = 0;
                        TryStun(enemy, container, skill, SkillMods.PrisonRootValue);
                    }
                    else
                    {
                        PrisonHits[id] = hits;
                    }

                    break;
                }

                var chance = traits != null ? traits.FreezeChance : 20;
                chance += AilmentChanceBonus(EStat.FreezeChance);
                if (SkillMods.Stacks(SkillMods.Frostbite) > 0 && HasDebuff(enemy, EDebuff.Freeze))
                {
                    var hits = FrostbiteHits.TryGetValue(id, out var known) ? known : 0;
                    chance += SkillMods.FrostbiteChance(hits) * 100f;
                }

                if (UnityEngine.Random.value * 100f < chance)
                {
                    if (SkillMods.Stacks(SkillMods.Icebound) > 0)
                    {
                        TryStun(enemy, container, skill, SkillMods.IceboundRootValue);
                    }
                    else
                    {
                        // Deep Freeze (R7b) lengthens the Templar's nova freeze; a
                        // cold skill without the mod reads 1.
                        var seconds = AilmentSeconds(2f * SkillMods.FreezeScale(skill.Id));
                        enemy.AddDebuff(EDebuff.Freeze, container, seconds);
                        FrozenUntil[id] = Time.time + seconds;
                    }
                }

                break;

            case "lightning":
                if (suppressStun)
                {
                    break;
                }

                if (Roll(traits != null ? traits.StunChance : 0, 12))
                {
                    TryStun(enemy, container, skill, 1.5f);
                }

                break;

            case "chaos":
                if (suppressPoison)
                {
                    break;
                }

                if (Roll(traits != null ? traits.PoisonPct : 0, 55))
                {
                    var poison = Seconds(traits != null ? traits.PoisonSeconds : 0, 4f);
                    // Lethal Dose: +25% poison damage per stack; the shorter
                    // duration (×0.65) is the one-time cost of the trade.
                    var lethal = SkillMods.Stacks(SkillMods.LethalDose);
                    if (lethal > 0)
                    {
                        container.damage *= 1f + SkillMods.LethalDamagePerStack * lethal;
                        poison *= SkillMods.LethalDurationCost;
                    }

                    // Maw of Conquest's PoisonDamageMultiplier scales the DoT,
                    // not the strike: the hit is already applied above, so this
                    // write reaches the debuff container only.
                    var poisonBonus = StatBonus(OwnerStats(), EStat.PoisonDamageMultiplier);
                    if (poisonBonus > 0f)
                    {
                        container.damage *= 1f + poisonBonus;
                    }

                    enemy.AddDebuff(EDebuff.Poison, container, AilmentSeconds(poison));
                    PoisonedUntil[id] = Time.time + PoisonMemorySeconds;
                    PoisonStacks[id] = (PoisonStacks.TryGetValue(id, out var stacks) ? stacks : 0) + 1;

                    // Viper Strike's base poison spread (skills.json
                    // traits.poisonSpread), dead data until 2026-11-05: one hop
                    // to up to two neighbours for half the poison, never a
                    // chain reaction. Toxic Bloom / Venom Spread stay the
                    // stronger death-side versions.
                    SpreadPoison(enemy, container, skill, poison, traits);
                }

                break;
        }
    }

    /// <summary>R11b: a stun with the shock nerf (Conductivity) and the first-
    /// hit suppression (Static Cling) in one place.</summary>
    private static void TryStun(Enemy enemy, DamageContainer container, SkillDefinition skill, float seconds)
    {
        if (SkillMods.Stacks(SkillMods.Conductivity) > 0)
        {
            seconds *= 0.75f;
        }

        enemy.AddDebuff(EDebuff.Stun, container, AilmentSeconds(seconds));
    }

    private static bool HasDebuff(Enemy enemy, EDebuff debuff)
    {
        if (enemy == null)
        {
            return false;
        }

        try
        {
            return enemy.HasDebuff(debuff);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Roll(double value, double defaultPercent)
    {
        var percent = value <= 0 ? defaultPercent : (value <= 1 ? value * 100 : value);
        return UnityEngine.Random.value * 100f < percent;
    }

    private static float Seconds(double value, float fallback) => value > 0.1 ? (float)value : fallback;

    /// <summary>
    /// How far a stat has been raised above its base (1 = untouched). The
    /// level-up tomes add flat fractions in the game's own units, so
    /// <c>current - base</c> is the bonus whatever the unit is: +7.5% attack
    /// speed reads 0.075. Clamped so a bad stat value cannot zero a skill or
    /// grow it without bound.
    /// </summary>
    private static float StatScale(MyPlayer player, EStat stat)
    {
        var stats = player != null && player.inventory != null ? player.inventory.playerStats : null;
        if (stats == null)
        {
            return 1f;
        }

        var scale = 1f + stats.GetStat(stat) - PlayerStatsNew.GetBaseValue(stat);
        return scale >= 0.25f && scale <= 5f ? scale : 1f;
    }

    /// <summary>The game's own <c>EStat.SizeMultiplier</c> (Tome of Size) is the
    /// universal AoE stat: every skill's area — projectile impact, chain spark,
    /// meteor, ball lightning and the mod zones — multiplies its radius by it.</summary>
    private static float AreaScale() => StatScale(owner, EStat.SizeMultiplier);

    /// <summary>An extra-count stat that may be fractional: the **whole part**
    /// is the count, the fractions accumulate until they make another one. The
    /// projectile tomes add **+0.5 per stack**, so two common stacks are +1
    /// projectile (a rare stack is 0.7, a legendary 1.0). No roll: the same
    /// stack count always fires the same number.
    ///
    /// It reads <c>GetUnclampedStat</c>, not <c>GetStat</c>: the game's
    /// <c>GetStat</c> itself applies <c>Math.Round</c> to Projectiles and
    /// ProjectileBounces, so 1.5 would read as 2 and an odd stack would fire
    /// early. The unclamped read is the same dictionary without that round.</summary>
    private static int ExtraCount(MyPlayer player, EStat stat)
    {
        var stats = player != null && player.inventory != null ? player.inventory.playerStats : null;
        if (stats == null)
        {
            return 0;
        }

        var raw = stats.GetUnclampedStat(stat) - PlayerStatsNew.GetBaseValue(stat);
        return raw <= 0f ? 0 : Mathf.FloorToInt(raw);
    }

    /// <summary>Whole extra count over the base (pierce and other whole stats).</summary>
    private static int StatFlat(MyPlayer player, EStat stat)
    {
        var stats = player != null && player.inventory != null ? player.inventory.playerStats : null;
        if (stats == null)
        {
            return 0;
        }

        return Mathf.RoundToInt(stats.GetStat(stat) - PlayerStatsNew.GetBaseValue(stat));
    }

    private static float AilmentSeconds(float seconds) =>
        seconds * StatScale(owner, EStat.DurationMultiplier);

    /// <summary>
    /// Where a cast leaves the body (S9). The Fireball starts at the main-hand
    /// weapon's tip; every other shape keeps the chest origin the operator
    /// prefers (the Ice Shot arrow leaves the middle of the body).
    /// </summary>
    private static Vector3 CastOrigin(MyPlayer player, SkillDefinition skill)
    {
        if (skill != null && string.Equals(skill.Id, "fireball", StringComparison.Ordinal))
        {
            var body = PoeCharacters.BodyRoot(player != null ? player.playerRenderer : null);
            if (WeaponModels.TryMuzzle(body, out var muzzle))
            {
                if (castOriginReported == 0)
                {
                    castOriginReported = 1;
                    Plugin.Logger.LogInfo(
                        $"fireball: cast origin is the main-hand weapon tip " +
                        $"({muzzle.x:0.00}, {muzzle.y:0.00}, {muzzle.z:0.00})");
                }

                return muzzle;
            }

            if (castOriginReported == 0)
            {
                castOriginReported = 2;
                Plugin.Logger.LogInfo("fireball: no main-hand weapon attached, casting from the chest");
            }
        }

        var height = player.height > 0.1f ? player.height : 4.5f;
        return player.transform.position + Vector3.up * (height * 0.6f) + player.transform.forward * 0.6f;
    }

    private static void Counted(SkillDefinition skill, string what)
    {
        casts++;
        if (casts == 1 || casts % 50 == 0)
        {
            Plugin.Logger.LogInfo($"{skill.Name}: {casts} cast(s), last {what}");
        }
    }

    // ------------------------------------------------------------ projectiles

    private static void UpdateBolts(float delta)
    {
        var camera = Camera.main;

        for (var index = Bolts.Count - 1; index >= 0; index--)
        {
            var bolt = Bolts[index];

            if (bolt.Visual == null)
            {
                Bolts.RemoveAt(index);
                continue;
            }

            bolt.Life -= delta;

            if (bolt.Homing && bolt.Target != null && bolt.Target.CanTakeDamage())
            {
                var aim = bolt.Target.transform.position + Vector3.up;
                bolt.Direction = (aim - bolt.Position).normalized;
            }

            bolt.Position += bolt.Direction * bolt.Speed * delta;
            bolt.Visual.transform.position = bolt.Position;
            if (bolt.Mesh != null)
            {
                // The effect GLBs stand the model upright and point its long
                // axis along +Z, so the arrow and the fireball face the flight
                // path instead of the camera.
                bolt.Mesh.rotation = Quaternion.LookRotation(bolt.Direction) * bolt.MeshRotation;
            }

            if (bolt.Glow != null && camera != null)
            {
                // A camera-facing quad whose local Y follows the bolt's
                // projected flight direction: the arrow's streak then lies
                // along the shot on screen instead of pointing into the depth
                // of the frame. A round halo (the fireball) is unaffected.
                var view = camera.transform.forward;
                var projected = Vector3.ProjectOnPlane(bolt.Direction, view);
                bolt.Glow.rotation = projected.sqrMagnitude > 0.0001f
                    ? Quaternion.LookRotation(view, projected.normalized)
                    : camera.transform.rotation;
            }

            var hit = FindHit(bolt.Position, bolt.Hit, bolt.Radius);
            if (hit != null)
            {
                bolt.Hit.Add(hit.GetInstanceID());

                // The shared projectile hit pipeline (R11b): a bolt hit and a
                // meteor impact both go through it, so Combustion, Shatterpoint,
                // Hunter's Mark, Sleet Step and Ash Cloud apply to every
                // projectile-shaped hit.
                var dealt = ImpactEffects(
                    hit, bolt.Damage, bolt.Crit, active,
                    bolt.PierceDamage, bolt.Pierces, bolt.NoIgnite, bolt.NoFreeze);

                // Fireball's impact splash: the target takes the full hit and
                // everything around it takes a share (2026-11-05). The bolt's
                // Radius is only hit detection; this is the real AoE.
                if (bolt.Splash > 0f && bolt.SplashShare > 0f)
                {
                    var splashDamage = dealt * bolt.SplashShare;
                    var primary = hit.GetInstanceID();
                    ForEachEnemy(bolt.Position, bolt.Splash, other =>
                    {
                        if (other.GetInstanceID() == primary)
                        {
                            return;
                        }

                        ImpactEffects(
                            other, splashDamage, bolt.Crit, active,
                            0f, 0, bolt.NoIgnite, bolt.NoFreeze);
                    });
                }

                // Split Ash: the impact bursts into balls; a child never
                // splits again, so a pack cannot recurse.
                if (bolt.Splits > 0)
                {
                    SpawnSplits(bolt, dealt);
                    bolt.Splits = 0;
                }

                if (bolt.Pierce <= 0)
                {
                    // Ricochet: a spent arrow hops to the nearest enemy that
                    // has not been hit yet at 60% of the damage that landed.
                    if (!Bounce(bolt))
                    {
                        RemoveBolt(index);
                        continue;
                    }
                }
                else
                {
                    bolt.Pierce--;
                    bolt.Pierces++;
                }
            }

            if (bolt.Life <= 0f)
            {
                // Winter's Grasp: where the arrow expires, a frost field stays.
                if (SkillMods.Stacks(SkillMods.WintersGrasp) > 0 &&
                    active != null &&
                    string.Equals(active.Id, "ice-shot", StringComparison.Ordinal))
                {
                    SpawnZone(
                        bolt.Position,
                        2f * AreaScale(),
                        SkillMods.GraspSecondsValue,
                        0f,
                        false,
                        active,
                        "ice-nova",
                        SkillMods.GraspSlowValue);
                }

                RemoveBolt(index);
            }
        }
    }

    private static void RemoveBolt(int index)
    {
        var bolt = Bolts[index];
        if (bolt.Visual != null)
        {
            UnityEngine.Object.Destroy(bolt.Visual);
        }

        Bolts.RemoveAt(index);
    }

    /// <summary>The shared projectile hit pipeline (R11b): every bolt hit and
    /// every meteor impact runs through here, so Combustion, Shatterpoint,
    /// Hunter's Mark, Sleet Step and Ash Cloud apply wherever a
    /// projectile-shaped hit lands. Returns the damage actually dealt after
    /// those multipliers.</summary>
    private static float ImpactEffects(
        Enemy hit, float damage, bool crit, SkillDefinition skill,
        float pierceDamage, int pierces, bool noIgnite, bool noFreeze)
    {
        var id = hit.GetInstanceID();

        // Glacial Lance: each enemy already pierced adds damage.
        var dealt = damage * (1f + pierceDamage * pierces);

        // Combustion: a re-hit on a burning enemy climbs to +45%.
        var burning = HasDebuff(hit, EDebuff.Burn);
        if (burning && SkillMods.Stacks(SkillMods.Combustion) > 0)
        {
            var stacks = Mathf.Min(
                SkillMods.CombustionCapValue,
                (CombustionHits.TryGetValue(id, out var known) ? known : 0) + 1);
            CombustionHits[id] = stacks;
            dealt *= 1f + SkillMods.CombustionPerHitValue * stacks;
        }

        // Shatterpoint: a frozen enemy takes +50% and shatters once.
        var shatter = false;
        if (SkillMods.Stacks(SkillMods.Shatterpoint) > 0 &&
            HasDebuff(hit, EDebuff.Freeze) &&
            (!ShatteredUntil.TryGetValue(id, out var shattered) || Time.time > shattered))
        {
            shatter = true;
            ShatteredUntil[id] = Time.time + 2f;
            dealt *= 1f + SkillMods.ShatterBonusValue;
        }

        // Combustion cannot refresh the ignite it is feeding on.
        HitEx(
            hit,
            dealt,
            crit,
            skill,
            noIgnite: noIgnite || (burning && SkillMods.Stacks(SkillMods.Combustion) > 0),
            noFreeze: noFreeze);

        if (shatter)
        {
            SpawnShards(
                hit.transform.position,
                dealt,
                crit,
                skill,
                SkillMods.ShatterShardsValue,
                SkillMods.ShatterShareValue);
        }

        if (markedEnemy != null && hit == markedEnemy && SkillMods.Stacks(SkillMods.HuntersMark) > 0)
        {
            var splash = dealt * SkillMods.MarkSplashValue;
            ForEachEnemy(hit.transform.position, SkillMods.MarkRadiusValue * AreaScale(), other =>
            {
                if (other != hit)
                {
                    Hit(other, splash, crit, skill);
                }
            });
        }

        if (SkillMods.Stacks(SkillMods.SleetStep) > 0)
        {
            SetMoving(
                "sleet-step",
                EStat.MoveSpeedMultiplier,
                EStatModifyType.Addition,
                SkillMods.SleetBonusValue,
                2f);
        }

        if (SkillMods.Stacks(SkillMods.AshCloud) > 0)
        {
            var seconds = SkillMods.AshCloudSeconds;
            SetMoving(
                "ash-cloud",
                EStat.DamageReductionMultiplier,
                EStatModifyType.Multiplication,
                1f - SkillMods.AshCloudReduction,
                seconds);
            SpawnAshCloud(GroundPoint(hit.transform.position, owner, hit), seconds);
        }

        return dealt;
    }

    /// <summary>Ricochet's hop: retarget the nearest enemy this bolt has not
    /// touched yet, inside a short reach, for 60% of its current damage.
    /// Returns false when nothing is left to bounce to, so the caller recycles
    /// the bolt as usual.</summary>
    private static bool Bounce(Bolt bolt)
    {
        if (bolt.Bounces <= 0)
        {
            return false;
        }

        var next = NearestEnemy(bolt.Position, 10f, bolt.Hit);
        if (next == null)
        {
            return false;
        }

        bolt.Bounces--;
        bolt.Target = next;
        var fireball = active != null && string.Equals(active.Id, "fireball", StringComparison.Ordinal);
        if (fireball && SkillMods.Stacks(SkillMods.RollingEmber) > 0)
        {
            // Rolling Ember: the bounced hit cannot ignite.
            bolt.NoIgnite = true;
        }

        bolt.Damage *= fireball ? 1f : 0.6f;
        bolt.Life = Mathf.Max(bolt.Life, 0.4f);
        var aim = next.transform.position + Vector3.up - bolt.Position;
        if (aim.sqrMagnitude > 0.0001f)
        {
            bolt.Direction = aim.normalized;
        }

        return true;
    }

    /// <summary>Split Ash's burst: the impact throws 2 balls per stack, at
    /// ±30 degrees and then ±60, each for 45% of the damage that landed. A
    /// child keeps the parent's impact radius and never re-splits.</summary>
    private static void SpawnSplits(Bolt parent, float damage)
    {
        var colour = SkillVisuals.Colour(active);
        for (var index = 0; index < parent.Splits; index++)
        {
            var side = index % 2 == 0 ? -1f : 1f;
            var angle = side * 30f * (1 + index / 2);
            var child = new Bolt
            {
                Position = parent.Position,
                Direction = Quaternion.Euler(0f, angle, 0f) * parent.Direction,
                Speed = parent.Speed,
                Life = 0.9f,
                Damage = damage * parent.SplitDamage,
                Crit = parent.Crit,
                Pierce = 0,
                Radius = parent.Radius
            };

            foreach (var id in parent.Hit)
            {
                child.Hit.Add(id);
            }

            child.Visual = ProjectileVisual(
                active, child.Position, colour,
                out child.Mesh, out child.MeshRotation, out child.Glow);
            Bolts.Add(child);
        }
    }

    // ------------------------------------------------- R11b shared systems

    private static StatInventory StatInventoryOf()
    {
        var inventory = owner == null ? null : owner.inventory;
        return inventory == null ? null : inventory.statInventory;
    }

    /// <summary>Run reset: per-enemy counters, chill entries and the named
    /// moving stats go with the rest of the run state.</summary>
    internal static void ResetModState() => ClearModState();

    private static void ClearModState()
    {
        DestroyTwinOrbit();
        DestroyRangeIndicator();
        BurnSince.Clear();
        BurningUntil.Clear();
        BurningEnemies.Clear();
        CombustionHits.Clear();
        FrostbiteHits.Clear();
        PrisonHits.Clear();
        ShatteredUntil.Clear();
        FrozenUntil.Clear();
        PoisonStacks.Clear();
        Slows.Clear();

        var stats = StatInventoryOf();
        if (stats != null)
        {
            foreach (var name in MovingUntil.Keys)
            {
                try
                {
                    stats.RemoveMovingStat(name);
                }
                catch (Exception)
                {
                    // A stat table that is already rebuilding is fine: the run
                    // reset drops it with the rest of the inventory.
                }
            }
        }

        MovingUntil.Clear();
        markedEnemy = null;
        momentumHits = 0;
        momentumAt = 0f;
        lastCycloneHits = 0;
        afterimageReady = 0f;
        novaRefund = 0f;
        lastRitesUntil = 0f;
        lastRitesReady = 0f;
        lastBreathUntil = 0f;
        lastBreathMult = 1f;
        arcticCasts = 0;
        chainCasts = 0;
        familiarNext = 0f;
        familiarAngle = 0f;
        DestroyFamiliar();
        wildfireNext = 0f;
        pyreHealWindow = 0f;
        pyreHealed = 0f;
        lastRazorPosition = Vector3.zero;
    }

    /// <summary>One named temporary stat, refreshed every tick while its
    /// condition holds (the game keeps one modifier per name).</summary>
    private static void SetMoving(string name, EStat stat, EStatModifyType type, float value, float seconds)
    {
        var stats = StatInventoryOf();
        if (stats == null)
        {
            return;
        }

        try
        {
            stats.ChangeMovingStat(name, new StatModifier
            {
                stat = stat,
                modifyType = type,
                modification = value
            });
            MovingUntil[name] = Time.time + Mathf.Max(0.1f, seconds);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"skill mod: moving stat {name} failed ({error.Message})");
        }
    }

    private static void UpdateMovingStats()
    {
        if (MovingUntil.Count == 0)
        {
            return;
        }

        List<string> expired = null;
        foreach (var pair in MovingUntil)
        {
            if (Time.time < pair.Value)
            {
                continue;
            }

            expired ??= new List<string>();
            expired.Add(pair.Key);
        }

        if (expired == null)
        {
            return;
        }

        var stats = StatInventoryOf();
        foreach (var name in expired)
        {
            MovingUntil.Remove(name);
            if (stats == null)
            {
                continue;
            }

            try
            {
                stats.RemoveMovingStat(name);
            }
            catch (Exception)
            {
                // See ClearModState.
            }
        }
    }

    /// <summary>R11b: a chill field. The game has no slow debuff, so the
    /// enemy's own speed multiplier is set and restored when the last refresh
    /// expires.</summary>
    private static void Slow(Enemy enemy, float share, float seconds)
    {
        if (enemy == null || share <= 0f)
        {
            return;
        }

        try
        {
            var id = enemy.GetInstanceID();
            enemy.SetSpeedMultiplier(Mathf.Clamp01(1f - share));
            Slows[id] = new SlowEntry { Enemy = enemy, Until = Time.time + Mathf.Max(0.2f, seconds) };
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"skill mod: slow failed ({error.Message})");
        }
    }

    private static void UpdateSlows()
    {
        if (Slows.Count == 0)
        {
            return;
        }

        List<int> expired = null;
        foreach (var pair in Slows)
        {
            if (Time.time >= pair.Value.Until)
            {
                expired ??= new List<int>();
                expired.Add(pair.Key);
            }
        }

        if (expired == null)
        {
            return;
        }

        foreach (var id in expired)
        {
            var entry = Slows[id];
            Slows.Remove(id);
            try
            {
                entry.Enemy.SetSpeedMultiplier(1f);
            }
            catch (Exception)
            {
                // A pooled/destroyed enemy has no speed left to restore.
            }
        }
    }

    private static void RememberBurn(Enemy enemy, float seconds)
    {
        if (enemy == null)
        {
            return;
        }

        var id = enemy.GetInstanceID();
        if (!BurnSince.ContainsKey(id))
        {
            BurnSince[id] = Time.time;
        }

        BurningUntil[id] = Time.time + Mathf.Max(0.2f, seconds);
        BurningEnemies[id] = enemy;
    }

    /// <summary>Ember Familiar: one ember per stack orbits the caster and the
    /// family lobs a small fireball every interval, taking turns (the embers
    /// fire in sequence, so a stack count adds the read without multiplying the
    /// damage). The fireball leaves the ember that fires, not the chest.</summary>
    private static void UpdateFamiliar(MyPlayer player, float delta)
    {
        if (player == null ||
            active == null ||
            SkillMods.Stacks(SkillMods.EmberFamiliar) <= 0 ||
            !string.Equals(active.Id, "fireball", StringComparison.OrdinalIgnoreCase))
        {
            DestroyFamiliar();
            return;
        }

        var count = Mathf.Clamp(SkillMods.Stacks(SkillMods.EmberFamiliar), 1, FamiliarMaxOrbs);
        while (familiarOrbs.Count > count)
        {
            var last = familiarOrbs.Count - 1;
            if (familiarOrbs[last] != null)
            {
                UnityEngine.Object.Destroy(familiarOrbs[last]);
            }

            familiarOrbs.RemoveAt(last);
        }

        familiarAngle = Mathf.Repeat(familiarAngle + FamiliarSpinDegrees * delta, 360f);
        var step = 360f / count;
        var ringCentre = player.transform.position + Vector3.up * (player.height * FamiliarOrbitHeightShare);
        for (var index = 0; index < count; index++)
        {
            var offset = Quaternion.Euler(0f, familiarAngle + step * index, 0f) * new Vector3(FamiliarOrbitRadius, 0f, 0f);
            var position = ringCentre + offset;
            if (index >= familiarOrbs.Count)
            {
                familiarOrbs.Add(CreateFamiliarOrb(position));
            }
            else if (familiarOrbs[index] != null)
            {
                familiarOrbs[index].transform.position = position;
            }
        }

        if (Time.time < familiarNext)
        {
            return;
        }

        familiarNext = Time.time + SkillMods.FamiliarIntervalValue;
        var target = NearestEnemy(player.transform.position, 30f, new HashSet<int>());
        if (target == null)
        {
            return;
        }

        // One ember fires per interval, in order, so the stack count is a
        // spectacle improvement, not a damage multiplier.
        var firing = familiarFireIndex % familiarOrbs.Count;
        familiarFireIndex++;
        var shooter = familiarOrbs[firing];
        var origin = shooter != null ? shooter.transform.position : ringCentre;
        var direction = (target.transform.position + Vector3.up - origin).normalized;
        var bolt = new Bolt
        {
            Position = origin,
            Target = target,
            Direction = direction,
            Speed = 430f * SpeedScale,
            Life = 1.6f,
            Damage = Damage(active, player, false) * SkillMods.FamiliarShareValue,
            Crit = false,
            Pierce = 0,
            Radius = 1.1f,
            Homing = true
        };

        bolt.Visual = ProjectileVisual(
            active, origin, SkillVisuals.Colour(active),
            out bolt.Mesh, out bolt.MeshRotation, out bolt.Glow);
        Bolts.Add(bolt);
    }

    /// <summary>The orbiting ember itself: the client's fireball core on the
    /// additive path with a small glow, so its position reads before it fires.</summary>
    private static GameObject CreateFamiliarOrb(Vector3 position)
    {
        var orb = new GameObject("WraeclastEmberFamiliar");
        orb.transform.position = position;

        var model = PoeAssets.GetEffect("fireball");
        if (model != null)
        {
            MountEffect(orb.transform, model, FamiliarOrbSize);
        }

        var glow = SkillVisuals.Sprite(
            "EmberFamiliarGlow",
            ProjectileTexture(active),
            SkillVisuals.Colour(active),
            position,
            Quaternion.identity,
            FamiliarOrbSize * 2.2f);
        glow.transform.SetParent(orb.transform, false);

        // SetParent(false) keeps the *local* transform, which was the world
        // position the quad was created at; without this the glow is offset by
        // the orb's world position and every ember reads away from its ring.
        glow.transform.localPosition = Vector3.zero;
        return orb;
    }

    private static void DestroyFamiliar()
    {
        foreach (var orb in familiarOrbs)
        {
            if (orb != null)
            {
                UnityEngine.Object.Destroy(orb);
            }
        }

        familiarOrbs.Clear();
        familiarFireIndex = 0;
    }

    /// <summary>Wildfire: one burning enemy passes the ignite to a neighbour
    /// within 2.5 m every second.</summary>
    private static void UpdateWildfire(MyPlayer player)
    {
        if (SkillMods.Stacks(SkillMods.Wildfire) <= 0 || player == null || Time.time < wildfireNext)
        {
            return;
        }

        wildfireNext = Time.time + SkillMods.WildfireIntervalValue;
        List<int> stale = null;
        var spread = Mathf.Min(4, SkillMods.Stacks(SkillMods.Wildfire));

        foreach (var pair in BurningEnemies)
        {
            if (Time.time > (BurningUntil.TryGetValue(pair.Key, out var until) ? until : 0f) ||
                pair.Value == null)
            {
                stale ??= new List<int>();
                stale.Add(pair.Key);
                continue;
            }

            var source = pair.Value.transform.position;
            var exclude = new HashSet<int> { pair.Key };
            for (var index = 0; index < spread; index++)
            {
                var next = NearestEnemy(source, SkillMods.WildfireRadiusValue * AreaScale(), exclude);
                if (next == null)
                {
                    break;
                }

                exclude.Add(next.GetInstanceID());
                var container = new DamageContainer(1f, "Wildfire");
                container.damage = Damage(active, player, false) * SkillMods.WildfireShareValue;
                container.direction = Vector3.zero;
                container.element = EElement.Fire;
                container.damageEffect = EDamageEffect.Fire;
                container.crit = false;
                container.knockback = 0f;
                try
                {
                    next.AddDebuff(EDebuff.Burn, container, AilmentSeconds(3f));
                    RememberBurn(next, AilmentSeconds(3f));
                }
                catch (Exception error)
                {
                    Plugin.Logger.LogWarning($"wildfire: spread failed ({error.Message})");
                }
            }
        }

        if (stale == null)
        {
            return;
        }

        foreach (var id in stale)
        {
            BurningEnemies.Remove(id);
            BurningUntil.Remove(id);
            BurnSince.Remove(id);
        }
    }

    /// <summary>R11b item 4: Ball Lightning's orb. The damage patch is the same
    /// Trail as before, but the read is the client's own lightning-ball mesh
    /// (the shared `storm_burst/balllightning` model, tiled-lightning shell)
    /// floating over the target instead of a ground ring; each pulse also draws
    /// one arc from the orb to the nearest enemy it hit. A missing GLB falls
    /// back to the old ground sheet, so the skill never goes invisible.</summary>
    private static void SpawnBallOrb(
        Vector3 position,
        float radius,
        float life,
        float damage,
        bool crit,
        SkillDefinition skill)
    {
        Trails.Add(new Trail
        {
            Position = position,
            Radius = radius,
            Life = life,
            Pulse = 0f,
            Damage = damage,
            Crit = crit,
            Skill = skill,
            Ball = true
        });

        var orb = PoeAssets.GetEffect("ball-lightning");
        if (orb == null)
        {
            var sheet = PoeAssets.GetGroundVfx("arc", 1.5f);
            SpawnEffect(
                position,
                radius * 2f,
                life,
                sheet ?? SkillVisuals.Ring(),
                sheet != null ? Color.white : SkillVisuals.Colour(skill),
                true,
                grow: false);
            return;
        }

        var holder = new GameObject("WraeclastBallLightning");
        holder.transform.position = position + Vector3.up * BallOrbHeight;
        SkillVisuals.Mount(
            holder.transform,
            orb,
            Color.white,
            BallOrbSize * Mathf.Max(1f, radius / 2.5f));

        Effects.Add(new Fx
        {
            Visual = holder,
            Life = life,
            MaxLife = life,
            StartScale = 1f,
            EndScale = 1f
        });
    }

    /// <summary>Item 22: the client sheet a generic ground zone draws, chosen
    /// from the skill's damage type, so no area falls back to the procedural
    /// ring while the element's own sheet is shipped.</summary>
    private static string ElementSheet(SkillDefinition skill)
    {
        switch (skill?.Tag)
        {
            case "fire":
                return "fireball";
            case "cold":
                return "ice-nova";
            case "lightning":
                return "arc";
            case "chaos":
                return "viper-strike";
            case "physical":
                return "cyclone";
            default:
                return null;
        }
    }

    /// <summary>A ground patch: damage pulses, a slow field, or both. The client
    /// ground sheet carries the read on the additive path — a premultiplied
    /// disc's alpha is opaque, so the alpha-sprite path drew its black surround
    /// as a square (the Razorwind lesson). The procedural ring stays only for a
    /// missing sheet.</summary>
    private static void SpawnZone(
        Vector3 position,
        float radius,
        float life,
        float damage,
        bool crit,
        SkillDefinition skill,
        string sheet,
        float slow)
    {
        Trails.Add(new Trail
        {
            Position = position,
            Radius = radius,
            Life = life,
            Pulse = 0f,
            Damage = damage,
            Crit = crit,
            Skill = skill,
            Slow = slow
        });

        SpawnClientEffect(
            position,
            radius * 2f,
            life,
            string.IsNullOrEmpty(sheet) ? ElementSheet(skill) : sheet,
            SkillVisuals.Colour(skill),
            flat: true,
            grow: false,
            gain: 1.5f);
    }

    /// <summary>Shatterpoint/Fracture: a small ring of ice shards thrown out
    /// from the body, each a short-lived bolt for a share of the hit.</summary>
    private static void SpawnShards(Vector3 position, float damage, bool crit, SkillDefinition skill, int count, float share)
    {
        var colour = SkillVisuals.Colour(skill);
        for (var index = 0; index < count; index++)
        {
            var yaw = 360f * index / Mathf.Max(1, count) + UnityEngine.Random.Range(-18f, 18f);
            var direction = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            var child = new Bolt
            {
                Position = position + Vector3.up * 0.6f,
                Direction = direction,
                Speed = 420f * SpeedScale,
                Life = 0.35f,
                Damage = damage * share,
                Crit = crit,
                Pierce = 0,
                Radius = 1.1f * AreaScale(),
                NoFreeze = true
            };

            child.Visual = ProjectileVisual(
                skill, child.Position, colour,
                out child.Mesh, out child.MeshRotation, out child.Glow);
            Bolts.Add(child);
        }
    }

    // ---------------------------------------------------------------- effects

    private static void SpawnEffect(Vector3 position, float size, float life, Texture2D texture, Color colour, bool flat, bool grow = true, bool smoke = false)
    {
        var rotation = flat ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
        var start = grow ? size * 0.25f : size;
        var visual = SkillVisuals.Sprite("WraeclastEffect", texture, colour, position + Vector3.up * 0.15f, rotation, start);

        Effects.Add(new Fx
        {
            Visual = visual,
            Life = life,
            MaxLife = life,
            StartScale = start,
            EndScale = grow ? size : start,
            Flat = flat,
            Smoke = smoke
        });
    }

    /// <summary>R11b item 3: Ash Cloud is a puffy cloud, not a ground ring. The
    /// client's own smoke card (grey/black, alpha-cut) is drawn on three crossed
    /// vertical blades through the alpha-blended sprite material, so the cloud
    /// has no edge-on angle and reads as smoke from anywhere. The cap recycles
    /// the oldest cloud, so a fast projectile cannot flood the frame.</summary>
    private static void SpawnAshCloud(Vector3 position, float seconds)
    {
        var radius = SkillMods.AshCloudRadius * AreaScale();
        var size = radius * 2f;
        var texture = PoeAssets.GetVfx("ash-cloud");
        if (texture == null)
        {
            SpawnEffect(
                position + Vector3.up * 0.02f,
                size,
                seconds,
                SkillVisuals.Ring(),
                new Color(0.45f, 0.45f, 0.45f, 1f),
                true,
                grow: false,
                smoke: true);
            return;
        }

        TrimAshClouds();

        var holder = new GameObject("WraeclastAshCloud");

        // Volumetric crossed cards, but standing on the floor: each card is
        // size × size and its centre sits half a card up, so the smoke's base
        // is the ground (not a floating bank at chest height). The footprint
        // is the mod's own radius, so the visual and the card growth match.
        var centre = position + Vector3.up * (size * 0.5f + AshCloudGroundHeight);
        holder.transform.position = centre;

        for (var index = 0; index < AshCloudBlades; index++)
        {
            var yaw = 180f * index / AshCloudBlades;
            var blade = SkillVisuals.Sprite(
                "WraeclastAshCloudBlade",
                texture,
                AshCloudTint,
                centre,
                Quaternion.Euler(0f, yaw, 0f),
                size);
            blade.transform.SetParent(holder.transform, worldPositionStays: true);
        }

        holder.transform.localScale = Vector3.one * AshCloudStartShare;
        Effects.Add(new Fx
        {
            Visual = holder,
            Life = seconds,
            MaxLife = seconds,
            StartScale = AshCloudStartShare,
            EndScale = 1f,
            Smoke = true
        });
    }

    /// <summary>Retires the oldest Ash Cloud when the cap is reached, so the
    /// cloud list never grows with the projectile rate.</summary>
    private static void TrimAshClouds()
    {
        var live = 0;
        var oldest = -1;
        for (var index = 0; index < Effects.Count; index++)
        {
            if (!Effects[index].Smoke)
            {
                continue;
            }

            if (oldest < 0)
            {
                oldest = index;
            }

            live++;
        }

        if (live < AshCloudCap || oldest < 0)
        {
            return;
        }

        if (Effects[oldest].Visual != null)
        {
            UnityEngine.Object.Destroy(Effects[oldest].Visual);
        }

        Effects.RemoveAt(oldest);
    }

    /// <summary>
    /// S9: one of the client's own particle sheets instead of a procedural
    /// sprite. Same lifetime and growth as <see cref="SpawnEffect"/>, but drawn
    /// on the additive effect material so the sheet's black surround vanishes.
    /// A missing texture falls back to the procedural ring, so a skill never
    /// goes invisible.
    /// </summary>
    private static void SpawnClientEffect(
        Vector3 position,
        float size,
        float life,
        string vfx,
        Color fallbackColour,
        bool flat,
        bool grow = true,
        float spin = 0f,
        float gain = 1f)
    {
        var texture = string.IsNullOrEmpty(vfx) ? null : PoeAssets.GetGroundVfx(vfx, gain);
        if (texture == null)
        {
            SpawnEffect(position, size, life, SkillVisuals.Ring(), fallbackColour, flat, grow);
            return;
        }

        var rotation = flat ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
        var start = grow ? size * 0.25f : size;
        var visual = SkillVisuals.EffectSprite(
            "WraeclastEffect", texture, Color.white, position + Vector3.up * 0.15f, rotation, start);

        Effects.Add(new Fx
        {
            Visual = visual,
            Life = life,
            MaxLife = life,
            StartScale = start,
            EndScale = grow ? size : start,
            Flat = flat,
            Spin = spin
        });
    }

    private static void Beam(Vector3 from, Vector3 to, Color colour, Texture2D texture)
    {
        var direction = to - from;
        var length = direction.magnitude;
        if (length < 0.05f)
        {
            return;
        }

        // Arc's own bolt mesh: the client's three crossed ribbons with the
        // lightning strip drawn across them. Its long axis runs -length..0 in
        // local Z, so anchoring it at the target fills the jump.
        var arc = PoeAssets.GetEffect("arc");
        if (arc != null)
        {
            var extents = arc.Mesh.bounds.size;
            var longest = Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z));
            var scale = length / Mathf.Max(0.01f, longest);

            var holder = new GameObject("WraeclastBeam");
            holder.transform.position = to;
            holder.transform.rotation = Quaternion.LookRotation(direction);
            var mounted = SkillVisuals.Mount(holder.transform, arc, Color.white, scale);

            // The width is scaled in the model's own axes (x/z across the
            // ribbon, y along it), so the bolt gets thicker without getting
            // longer than the jump.
            var local = mounted.transform.localScale;
            mounted.transform.localScale = new Vector3(
                local.x * BeamWidthScale, local.y, local.z * BeamWidthScale);

            Effects.Add(new Fx
            {
                Visual = holder,
                Life = BeamLifeSeconds,
                MaxLife = BeamLifeSeconds,
                StartScale = scale,
                EndScale = scale,
                Flat = false
            });

            // Three ribbons crossing the jump's axis are seen end-on from a
            // camera behind the caster, so on their own the link collapses to
            // a flash at the near end. The same atlas on a camera-facing sheet
            // carries the jump from any angle, which is what makes a chain read
            // as a chain.
            Link(from, to, arc.Texture, Color.white);
            return;
        }

        Link(from, to, texture, colour);
    }

    /// <summary>One camera-facing strip of the effect atlas spanning the jump,
    /// stretched along the beam and rolled to face the camera. Its local x is
    /// the texture's u axis (the bolt runs down it) and its local y is the
    /// ribbon's width.</summary>
    private static void Link(Vector3 from, Vector3 to, Texture2D texture, Color colour)
    {
        var direction = to - from;
        var length = direction.magnitude;
        if (length < 0.05f || texture == null)
        {
            return;
        }

        var axis = direction / length;
        var middle = from + direction * 0.5f;
        var camera = Camera.main;
        var view = camera != null ? camera.transform.position - middle : Vector3.up;
        var side = Vector3.Cross(axis, view);
        if (side.sqrMagnitude < 0.0001f)
        {
            side = Vector3.Cross(axis, Vector3.up);
        }
        if (side.sqrMagnitude < 0.0001f)
        {
            return;
        }

        side.Normalize();
        var normal = Vector3.Cross(side, axis).normalized;
        var quad = SkillVisuals.EffectQuad(
            "WraeclastLink", texture, colour, middle, Quaternion.LookRotation(normal, -side));

        var width = Mathf.Clamp(length * 0.25f, 1.2f, 3.5f);
        quad.transform.localScale = new Vector3(length, width, 1f);

        Effects.Add(new Fx
        {
            Visual = quad,
            Life = BeamLifeSeconds,
            MaxLife = BeamLifeSeconds,
            StartScale = 1f,
            EndScale = 1f,
            Flat = false
        });
    }

    /// <summary>
    /// S9e: the Shadow's strike plants its dagger in the monster it hits. The
    /// flight is instant on purpose — a knife crossing the whole arc was the
    /// operator's "attroce" — so the hand's copy is hidden and a copy of the
    /// same mesh appears the same frame in the target's head, in the pose the
    /// hand held it in (a rigid copy: no aim, no spin, no stretch). After
    /// <see cref="ThrowSeconds"/> the copy is destroyed and the hand's knife
    /// comes back.
    /// </summary>
    private static void ThrowWeapon(MyPlayer player, Enemy target)
    {
        if (target == null)
        {
            return;
        }

        var body = PoeCharacters.BodyRoot(player != null ? player.playerRenderer : null);
        var visual = WeaponModels.SpawnThrown(body, "R_Weapon");
        if (visual == null)
        {
            return;
        }

        // The targeting height, not the head: the same point the bolts aim at
        // (`aimPoint = target.position + up`), which the operator finds right.
        var offset = Vector3.up * 1f;
        visual.transform.position = target.transform.position + offset;
        visual.transform.rotation *= Quaternion.Euler(0f, ThrowYawDegrees, 0f);

        WeaponModels.SetHeldVisible(body, "R_Weapon", false);
        Throws.Add(new Throw
        {
            Visual = visual,
            Body = body,
            Target = target,
            Offset = offset,
            Life = ThrowSeconds
        });
    }

    private static void UpdateThrows(float delta)
    {
        for (var index = Throws.Count - 1; index >= 0; index--)
        {
            var thrown = Throws[index];
            thrown.Life -= delta;

            // The knife is planted in a moving monster: follow its pivot every
            // frame, or the world-space copy is left behind and reads as a
            // stretched knife at the place the target stood.
            if (thrown.Visual != null && thrown.Target != null)
            {
                thrown.Visual.transform.position = thrown.Target.transform.position + thrown.Offset;
            }

            if (thrown.Life > 0f)
            {
                continue;
            }

            if (thrown.Visual != null)
            {
                UnityEngine.Object.Destroy(thrown.Visual);
            }

            // A fast attack can plant a second dagger before the first returns:
            // the hand only gets its copy back when none is still out.
            var stillOut = false;
            foreach (var other in Throws)
            {
                if (other != thrown && other.Life > 0f && other.Body == thrown.Body)
                {
                    stillOut = true;
                    break;
                }
            }

            if (!stillOut)
            {
                WeaponModels.SetHeldVisible(thrown.Body, "R_Weapon", true);
            }

            Throws.RemoveAt(index);
        }
    }

    /// <summary>Drops every planted dagger and gives the hand its weapon back
    /// (a class swap or run reset while one is stuck).</summary>
    private static void ClearThrows()
    {
        foreach (var thrown in Throws)
        {
            if (thrown.Visual != null)
            {
                UnityEngine.Object.Destroy(thrown.Visual);
            }

            WeaponModels.SetHeldVisible(thrown.Body, "R_Weapon", true);
        }

        Throws.Clear();
    }

    private static void UpdateWaves(float delta)
    {
        for (var index = Waves.Count - 1; index >= 0; index--)
        {
            var wave = Waves[index];
            if (wave.Visual == null)
            {
                Waves.RemoveAt(index);
                continue;
            }

            wave.Life -= delta;
            wave.Position += wave.Direction * wave.Speed * delta;
            wave.Visual.transform.position = wave.Position + Vector3.up * 0.1f;
            wave.Visual.transform.localScale = Vector3.one * wave.Radius * 2f;

            ForEachEnemy(wave.Position, wave.Radius, enemy =>
            {
                if (wave.Hit.Add(enemy.GetInstanceID()))
                {
                    Hit(enemy, wave.Damage, wave.Crit, wave.Skill);
                }
            });

            if (wave.Life <= 0f)
            {
                UnityEngine.Object.Destroy(wave.Visual);
                Waves.RemoveAt(index);
            }
        }
    }

    private static void UpdateEffects(float delta)
    {
        for (var index = Effects.Count - 1; index >= 0; index--)
        {
            var effect = Effects[index];
            if (effect.Visual == null)
            {
                Effects.RemoveAt(index);
                continue;
            }

            effect.Life -= delta;
            if (effect.Life <= 0f)
            {
                UnityEngine.Object.Destroy(effect.Visual);
                Effects.RemoveAt(index);
                continue;
            }

            if (effect.EndScale > effect.StartScale)
            {
                var t = 1f - effect.Life / effect.MaxLife;
                var scale = Mathf.Lerp(effect.StartScale, effect.EndScale, t);
                effect.Visual.transform.localScale = Vector3.one * scale;
            }

            if (Mathf.Abs(effect.Spin) > 0.01f)
            {
                effect.Visual.transform.Rotate(0f, 0f, effect.Spin * delta, Space.Self);
            }
        }
    }

    // ---------------------------------------------------------------- enemies

    private static Enemy FindTarget(Vector3 position, float range)
    {
        // The game's own picker: it honours the player's Enemy Targeting Mode
        // setting (Smart Aim / Closest Enemy) and its line-of-sight rules, the
        // same way Megabonk's weapons choose a target. Our old loop was always
        // "closest", which ignored that setting.
        try
        {
            var target = EnemyTargeting.GetEnemy(position, range, 0, true, null);
            if (target != null && target.CanTakeDamage())
            {
                return target;
            }

            return null;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"EnemyTargeting failed ({error.Message}); using the nearest enemy");
        }

        Enemy best = null;
        var bestDistance = float.MaxValue;
        ForEachEnemy(position, range, enemy =>
        {
            var distance = (enemy.transform.position - position).sqrMagnitude;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = enemy;
            }
        });

        return best;
    }

    private static Enemy NearestEnemy(Vector3 position, float range, HashSet<int> exclude)
    {
        Enemy best = null;
        var bestDistance = float.MaxValue;
        ForEachEnemy(position, range, enemy =>
        {
            if (exclude.Contains(enemy.GetInstanceID()))
            {
                return;
            }

            var distance = (enemy.transform.position - position).sqrMagnitude;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = enemy;
            }
        });

        return best;
    }

    /// <summary>Hunter's Mark: the farthest live enemy inside the cast range.</summary>
    private static Enemy FarthestEnemy(Vector3 position, float range)
    {
        Enemy best = null;
        var bestDistance = -1f;
        ForEachEnemy(position, range, enemy =>
        {
            var distance = (enemy.transform.position - position).sqrMagnitude;
            if (distance > bestDistance)
            {
                bestDistance = distance;
                best = enemy;
            }
        });

        return best;
    }

    private static Enemy FindHit(Vector3 position, HashSet<int> exclude, float radius)
    {
        Enemy found = null;
        ForEachEnemy(position, radius, enemy =>
        {
            if (found == null && !exclude.Contains(enemy.GetInstanceID()))
            {
                found = enemy;
            }
        });

        return found;
    }

    private static void ForEachEnemy(Vector3 position, float radius, Action<Enemy> action)
    {
        var colliders = Physics.OverlapSphere(position, radius);
        var seen = new HashSet<int>();

        for (var index = 0; index < colliders.Length; index++)
        {
            var collider = colliders[index];
            if (collider == null)
            {
                continue;
            }

            var enemy = collider.GetComponentInParent<Enemy>();
            if (enemy == null || !enemy.CanTakeDamage())
            {
                continue;
            }

            if (seen.Add(enemy.GetInstanceID()))
            {
                action(enemy);
            }
        }
    }

    /// <summary>
    /// The plan keeps Megabonk's auto-attacking weapon out of our classes: the
    /// character's damage comes from our skills. Only weapons present at run
    /// start are toggled; later drafts still work.
    /// </summary>
    private static void DisableBaseWeapons(MyPlayer player)
    {
        if (!Plugin.DisableBaseWeapons.Value)
        {
            return;
        }

        try
        {
            var inventory = player.inventory.weaponInventory;
            if (inventory == null || inventory.weapons == null)
            {
                return;
            }

            var disabled = 0;
            foreach (EWeapon weapon in Enum.GetValues(typeof(EWeapon)))
            {
                if (inventory.weapons.ContainsKey(weapon))
                {
                    inventory.ToggleWeapon(weapon, false);
                    disabled++;
                }
            }

            if (disabled > 0)
            {
                Plugin.Logger.LogInfo($"Base weapons disabled for the run: {disabled}");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"Disabling the base weapon failed: {error.Message}");
        }
    }
}
