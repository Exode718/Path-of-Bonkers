using System;
using System.Collections.Generic;
using Assets.Scripts._Data;
using Assets.Scripts._Data.Tomes;
using UnityEngine;
using Wraeclast.Data;

namespace Wraeclast.Skills;

/// <summary>
/// R6: skill mods, one layer over the shapes <see cref="PoeSkills"/> already
/// runs. A mod is a row of the reward table (<c>kind: mod</c>) that changes
/// **how** the class's one skill behaves and always costs something; the class
/// never gains a second skill.
///
/// The engine owns three things:
///
/// - **Stacks per mod id**, run-scoped (<c>ItemInventory.Cleanup</c> resets the
///   layer with the rest of the run state). Two stacks of the same mod double
///   its effect; the cost is paid once, because the second stack is the reward
///   for having accepted the trade.
/// - **Exclusive branches**: mods that share a branch are never offered
///   together. A mod's branch is its <c>requires</c> (the branch opener names
///   itself); the pool asks <see cref="BranchTaken"/> with the offered id
///   excluded, so a mod can still take its own second stack.
/// - **Draft cards**: a mod is shown in the level-up draft as a real
///   <c>TomeData</c> clone whose text patches answer this sheet. The pick is
///   intercepted on <c>PlayerInventory.AddUpgrade</c>, so taking a mod adds a
///   stack here and never touches the native tome inventory.
///
/// The numbers below are ours (DOC.md "Duelist — Cyclone"), per stack as the
/// design table states them. R7 adds the two **projectile** classes on the same
/// layer: the Ranger's Ice Shot and the Witch's Fireball answer one
/// <see cref="Projectile"/> plan per cast, so a bolt only carries values.
/// </summary>
internal static class SkillMods
{
    internal const string Gale = "duelist-gale-offshoots";
    internal const string Vortex = "duelist-vortex-lure";
    internal const string Storm = "duelist-gathering-storm";

    internal const string Ricochet = "ranger-ricochet";
    internal const string Volley = "ranger-volley-stance";
    internal const string Lance = "ranger-glacial-lance";
    internal const string TwinFlame = "witch-twin-flame";
    internal const string SplitAsh = "witch-split-ash";
    internal const string Focus = "witch-pyromantic-focus";

    internal const string FanTheFlames = "marauder-fan-the-flames";
    internal const string PyreOfFaithful = "marauder-pyre-of-the-faithful";
    internal const string CinderTrail = "marauder-cinder-trail";
    internal const string GlacialEcho = "templar-glacial-echo";
    internal const string Convergence = "templar-convergence";
    internal const string DeepFreeze = "templar-deep-freeze";

    internal const string ToxicBloom = "shadow-toxic-bloom";
    internal const string VenomSpread = "shadow-venom-spread";
    internal const string SerpentsPatience = "shadow-serpents-patience";
    internal const string StaticField = "scion-static-field";
    internal const string Overcharge = "scion-overcharge";
    internal const string StormAnchor = "scion-storm-anchor";

    internal const string Razorwind = "duelist-razorwind";
    internal const string Momentum = "duelist-momentum";
    internal const string IronCyclone = "duelist-iron-cyclone";
    internal const string TwinOrbit = "duelist-twin-orbit";
    internal const string BloodWhirl = "duelist-blood-whirl";
    internal const string VacuumCut = "duelist-vacuum-cut";
    internal const string Afterimage = "duelist-afterimage";

    internal const string SacrificialPyre = "marauder-sacrificial-pyre";
    internal const string Immolation = "marauder-immolation";
    internal const string ZealotsCovenant = "marauder-zealots-covenant";
    internal const string AshenVeil = "marauder-ashen-veil";
    internal const string Kindle = "marauder-kindle";
    internal const string SmoulderingStep = "marauder-smouldering-step";
    internal const string LastRites = "marauder-last-rites";

    internal const string Shatterpoint = "ranger-shatterpoint";
    internal const string WintersGrasp = "ranger-winters-grasp";
    internal const string HuntersMark = "ranger-hunters-mark";
    internal const string Frostbite = "ranger-frostbite";
    internal const string SleetStep = "ranger-sleet-step";
    internal const string IcePrison = "ranger-ice-prison";
    internal const string ColdSnap = "ranger-cold-snap";

    internal const string RollingEmber = "witch-rolling-ember";
    internal const string IgniteBloom = "witch-ignite-bloom";
    internal const string EmberFamiliar = "witch-ember-familiar";
    internal const string MeteorCall = "witch-meteor-call";
    internal const string Wildfire = "witch-wildfire";
    internal const string AshCloud = "witch-ash-cloud";
    internal const string Combustion = "witch-combustion";

    internal const string FrozenBulwark = "templar-frozen-bulwark";
    internal const string Permafrost = "templar-permafrost";
    internal const string RimeCascade = "templar-rime-cascade";
    internal const string RingOfWinter = "templar-ring-of-winter";
    internal const string Fracture = "templar-fracture";
    internal const string Icebound = "templar-icebound";
    internal const string ArcticCharge = "templar-arctic-charge";

    internal const string Shadowstep = "shadow-shadowstep";
    internal const string CorrosiveBite = "shadow-corrosive-bite";
    internal const string TwinFang = "shadow-twin-fang";
    internal const string LethalDose = "shadow-lethal-dose";
    internal const string AddersGrace = "shadow-adders-grace";
    internal const string Haemorrhage = "shadow-haemorrhage";
    internal const string LastBreath = "shadow-last-breath";

    internal const string ForkedFate = "scion-forked-fate";
    internal const string Conductivity = "scion-conductivity";
    internal const string ChainLightning = "scion-chain-lightning";
    internal const string Thunderhead = "scion-thunderhead";
    internal const string StaticCling = "scion-static-cling";
    internal const string GalvanicRush = "scion-galvanic-rush";
    internal const string BallLightning = "scion-ball-lightning";

    private const string IceShotSkill = "ice-shot";
    private const string FireballSkill = "fireball";
    private const string RighteousFireSkill = "righteous-fire";
    private const string IceNovaSkill = "ice-nova";
    private const string ViperStrikeSkill = "viper-strike";
    private const string ArcSkill = "arc";
    private const string CycloneSkill = "cyclone";

    /// <summary>R7 numbers, per stack as the design table states them; the
    /// costs are paid once (see <see cref="Projectile"/>).</summary>
    private const float RicochetDamageShare = 0.60f;
    private const int VolleyArrowsPerStack = 3;
    private const float VolleyArcPerStack = 20f;
    private const float VolleyDamageShare = 0.65f;
    private const float LanceDamagePerStack = 0.30f;
    private const float LanceCooldownCost = 0.15f;
    private const float TwinBoltDamageShare = 0.35f;
    private const float TwinMainCost = 0.15f;
    private const int SplitBallsPerStack = 2;
    private const float SplitDamageShare = 0.45f;
    private const float SplitRadiusCost = 0.25f;
    private const float FocusDamagePerStack = 0.60f;
    private const float FocusRadiusPerStack = 0.25f;
    private const float FocusCooldownCost = 0.20f;

    /// <summary>R7b numbers, per stack as the design table states them; the
    /// costs are paid once.</summary>
    private const float FanRadiusPerEnemy = 0.08f;
    private const float FanRadiusCapPerStack = 0.40f;
    private const float FanBurnCost = 0.10f;
    private const float PyreDamagePerStack = 0.80f;
    private const float PyreStandstillSeconds = 1f;
    private const float PyreRadiusCost = 0.15f;
    private const float TrailCost = 0.10f;
    private const float TrailDamagePerStack = 0.25f;
    private const int EchoPerStack = 1;
    private const float EchoDamageShare = 0.50f;
    private const float ConvergenceEdgeDamage = 0.40f;
    private const float ConvergenceEdgeStart = 0.55f;
    private const float ConvergencePullMeters = 2f;
    private const float DeepFreezePerStack = 0.50f;
    private const float DeepFreezeCooldownCost = 0.20f;

    /// <summary>R7c numbers, per stack as the design table states them; the
    /// costs are paid once. The design leaves the cloud/zone sizes and the
    /// clap's timing open, so those are ours and stated on the cards.</summary>
    private const float BloomDamageShare = 0.50f;
    private const float BloomSeconds = 3f;
    private const float BloomRadius = 3f;
    private const float SpreadRadius = 4f;
    private const int SpreadTargetsPerStack = 1;
    private const float PatienceStandstillSeconds = 2f;
    private const float PatienceDamagePerStack = 0.50f;
    private const int PatienceStacksPerStack = 2;
    private const float SparkDamageShare = 0.25f;
    private const float SparkSeconds = 1.5f;
    private const float SparkRadius = 2f;
    private const int StaticFieldJumpCost = 1;
    private const int OverchargeJumpsPerStack = 2;
    private const float OverchargeFalloff = 0.75f;
    private const float ArcFalloff = 0.82f;
    private const float AnchorStunSeconds = 0.6f;
    private const float AnchorDelaySeconds = 0.4f;
    private const float AnchorRadius = 2f;

    /// <summary>R11b numbers: the remaining seven upgrades per skill, as the
    /// base design tables state them. Zone sizes and a few timings the design
    /// leaves open are ours and are named on the cards.</summary>
    private const float RazorDamageShare = 0.50f;
    private const float RazorLifeSeconds = 1.5f;
    private const float RazorMinDistance = 1f;
    private const float MomentumPerHit = 0.02f;
    private const float MomentumCap = 0.20f;
    private const float MomentumResetSeconds = 2f;
    internal const float IronRadius = 1.25f;
    internal const float IronDamage = 1.15f;
    internal const float IronMoveScale = 0.70f;
    private const float OrbitRadius = 1.20f;
    private const float OrbitDamage = 0.40f;
    private const float OrbitInnerShare = 0.40f;
    private const float BloodWhirlHeal = 0.01f;
    private const float BloodWhirlCritCost = 0.20f;
    private const int VacuumEvery = 5;
    private const float VacuumMeters = 2f;
    private const float AfterimageDamage = 0.50f;
    private const float AfterimageSeconds = 1.5f;
    private const float AfterimageLockout = 1f;

    private const float PyreSelfBurn = 0.015f;
    private const float PyreKillHeal = 0.01f;
    private const float PyreKillCap = 0.04f;
    private const float PyreFloor = 0.15f;
    private const float ImmolateShare = 0.25f;
    private const float ImmolateRadius = 3f;
    private const float ZealotSlow = 0.20f;
    private const float ZealotCost = 0.20f;
    private const float VeilReduction = 0.10f;
    private const float VeilRadiusCost = 0.15f;
    private const float KindlePerSecond = 0.06f;
    private const float KindleCap = 0.30f;
    private const float KindleCost = 0.15f;
    private const float SmoulderSeconds = 2f;
    private const float SmoulderShare = 0.25f;
    private const float SmoulderCooldownCost = 0.30f;
    private const float RitesMultiplier = 2f;
    private const float RitesSeconds = 4f;
    private const float RitesCooldown = 20f;

    private const float ShatterBonus = 0.50f;
    private const int ShatterShards = 3;
    private const float ShatterShare = 0.20f;
    private const float GraspSeconds = 2f;
    private const float GraspSlow = 0.40f;
    private const float MarkSplash = 0.25f;
    private const float MarkRadius = 2f;
    private const float FrostbitePerHit = 0.08f;
    private const float FrostbiteCost = 0.15f;
    private const float SleetBonus = 0.10f;
    private const float SleetSeconds = 2f;
    private const int PrisonHits = 3;
    private const float PrisonRoot = 1f;
    private const float ColdSnapRadius = 3f;
    private const float ColdSnapSeconds = 0.5f;
    private const float ColdSnapShatterCost = 0.30f;

    private const float IgniteBloomShare = 0.50f;
    private const float FamiliarInterval = 3f;
    private const float FamiliarShare = 0.40f;
    private const float FamiliarCost = 0.10f;
    private const float MeteorDelay = 0.8f;
    private const float MeteorDamage = 1.20f;
    private const float MeteorRadius = 1.40f;
    private const float MeteorDamagePerStack = 0.06f;
    private const float MeteorRadiusPerStack = 0.03f;
    private const float MeteorDelayPerStack = 0.01f;
    private const float MeteorMinDelay = 0.4f;
    private const float WildfireRadius = 2.5f;
    private const float WildfireInterval = 1f;
    private const float WildfireShare = 0.80f;
    private const float WildfireIgniteCost = 0.20f;
    private const float AshSeconds = 3f;
    private const float AshReduction = 0.20f;
    private const float AshSecondsPerStack = 0.15f;
    private const float AshSecondsCap = 8f;
    private const float AshReductionPerStack = 0.02f;
    private const float AshReductionCap = 0.60f;
    private const float AshRadius = 3.0f;
    private const float AshRadiusPerStack = 0.12f;
    private const float AshRadiusCap = 6.5f;
    private const float CombustionPerHit = 0.15f;
    private const int CombustionCap = 3;

    private const float BulwarkSeconds = 2f;
    private const float BulwarkSlow = 0.40f;
    private const float PermafrostSeconds = 3f;
    private const float PermafrostSlow = 0.40f;
    private const float PermafrostCost = 0.10f;
    private const int RimeTargets = 3;
    private const float RimeRefund = 0.30f;
    private const float WinterRadius = 1.25f;
    private const float WinterDamage = 0.75f;
    private const int FractureShards = 3;
    private const float FractureShare = 0.20f;
    private const float IceboundRoot = 0.4f;
    private const int ArcticEvery = 3;
    private const float ArcticDelay = 1f;
    private const float ArcticRadius = 1.40f;

    private const float ShadowBehind = 0.35f;
    private const int ShadowStacks = 1;
    private const float CorrosivePerStack = 0.10f;
    private const float CorrosiveCap = 0.30f;
    private const float TwinFangShare = 0.65f;

    /// <summary>Lethal Dose: +25% poison damage per stack, the duration cost
    /// (×0.65) paid once.</summary>
    internal const float LethalDamagePerStack = 0.25f;
    internal const float LethalDurationCost = 0.65f;
    private const float AdderBonus = 0.15f;
    private const float AdderCost = 0.10f;
    private const int HaemorrhageGold = 1;

    private const int ForkJumps = 2;
    private const int ConductivityTargets = 2;
    private const float ConductivityCost = 0.25f;
    private const float ChainRange = 8f;
    private const float ChainExtraFalloff = 0.90f;
    private const int ThunderEvery = 4;
    private const float ThunderDamage = 1.50f;
    private const float ThunderCooldownCost = 0.10f;
    private const float ClingBonus = 0.12f;
    private const float GalvanicBonus = 0.10f;
    private const float GalvanicSeconds = 1.5f;
    private const float GalvanicCost = 0.10f;
    private const float BallSeconds = 2f;
    private const float BallRadius = 2.5f;
    private const float BallShare = 0.25f;

    /// <summary>What one cast of a projectile skill should do after its mods.
    /// Defaults are the untouched skill (scales of 1, no extra behaviour).</summary>
    internal struct ProjectilePlan
    {
        /// <summary>Arrows in the fan, 0 = keep the skill's own count.</summary>
        internal int VolleyArrows;

        /// <summary>Total width of the fan in degrees (0 = keep the skill's own).</summary>
        internal float VolleyArc;

        /// <summary>Damage share of each fan arrow (0.65).</summary>
        internal float VolleyDamage;

        /// <summary>The shot loses every pierce (Ricochet/Volley cost).</summary>
        internal bool NoPierce;

        /// <summary>Extra balls per cast, each at <see cref="ExtraBoltDamage"/>.</summary>
        internal int ExtraBolts;

        internal float ExtraBoltDamage;

        /// <summary>Multiplier on the cast's damage (costs and Focus).</summary>
        internal float DamageScale;

        /// <summary>Multiplier on the bolt's impact radius.</summary>
        internal float RadiusScale;

        /// <summary>Enemies the arrow may hop to after a hit (60% each).</summary>
        internal int RicochetBounces;

        /// <summary>Balls an impact splits into, each at <see cref="SplitDamage"/>.</summary>
        internal int SplitBalls;

        internal float SplitDamage;

        /// <summary>Extra damage per enemy already pierced (Glacial Lance).</summary>
        internal float PierceDamage;

        internal float CooldownScale;

        // R11b: Ice Shot.
        internal bool Shatter;
        internal int ShatterShards;
        internal float ShatterDamage;
        internal bool WinterGrasp;
        internal float GraspSeconds;
        internal float GraspSlow;
        internal bool HuntersMark;
        internal float MarkSplash;
        internal float MarkRadius;
        internal bool Frostbite;
        internal bool SleetStep;
        internal bool IcePrison;
        internal bool ColdSnap;

        // R11b: Fireball.
        internal bool RollingEmber;
        internal bool IgniteBloom;
        internal float IgniteBloomShare;
        internal bool EmberFamiliar;
        internal float FamiliarDamage;
        internal float FamiliarInterval;
        internal bool MeteorCall;
        internal float MeteorDamage;
        internal float MeteorRadius;
        internal float MeteorDelay;
        internal bool Wildfire;
        internal float WildfireRadius;
        internal float WildfireInterval;
        internal float WildfireDamage;
        internal bool AshCloud;
        internal bool Combustion;
        internal float CombustionPerHit;
        internal int CombustionCap;
    }

    /// <summary>What one tick of Righteous Fire should do after its mods
    /// (R7b). Defaults are the untouched aura.</summary>
    internal struct AuraPlan
    {
        internal float DamageScale;

        internal float RadiusScale;

        /// <summary>Cinder Trail: this tick leaves burning ground.</summary>
        internal bool Trail;

        /// <summary>Share of the tick's damage one trail patch deals per pulse.</summary>
        internal float TrailDamage;

        // R11b: Cyclone.
        internal bool Razorwind;
        internal float RazorDamage;
        internal bool Momentum;
        internal bool IronCyclone;
        internal bool TwinOrbit;
        internal float OrbitInner;
        internal bool BloodWhirl;
        internal bool VacuumCut;
        internal bool Afterimage;

        // R11b: Righteous Fire.
        internal bool SacrificialPyre;
        internal bool Immolation;
        internal bool ZealotsCovenant;
        internal float ZealotSlow;
        internal bool AshenVeil;
        internal float VeilReduction;
        internal bool Kindle;
        internal bool SmoulderingStep;
        internal bool LastRites;
    }

    /// <summary>What one cast of Ice Nova should do after its mods (R7b).
    /// Defaults are the untouched burst.</summary>
    internal struct NovaPlan
    {
        internal float DamageScale;

        internal float RadiusScale;

        /// <summary>Convergence: extra damage on hits past <see cref="EdgeStart"/>.</summary>
        internal float EdgeDamage;

        /// <summary>Distance share of the radius where the edge begins.</summary>
        internal float EdgeStart;

        /// <summary>Metres an edge hit is dragged toward the centre.</summary>
        internal float Pull;

        /// <summary>Glacial Echo: repeats after 0.6 s, one per stack.</summary>
        internal int Echoes;

        internal float EchoDamage;

        // R11b: the remaining seven Ice Nova upgrades.
        internal bool FrozenBulwark;
        internal bool Permafrost;
        internal bool RimeCascade;
        internal float RimeRefund;
        internal bool RingOfWinter;
        internal bool Fracture;
        internal bool Icebound;
        internal bool ArcticCharge;
    }

    /// <summary>What one Viper Strike should do after its mods (R7c). The
    /// death side of Toxic Bloom and Venom Spread is read where the kill is
    /// reported, because a strike that never killed has nothing to leave
    /// behind.</summary>
    internal struct MeleePlan
    {
        internal float DamageScale;

        /// <summary>Serpent's Patience: extra poison stacks this strike adds.</summary>
        internal int ExtraPoisonStacks;

        /// <summary>True when the stand-still bonus was spent (the caller
        /// restarts its clock, so the next one needs another 2 s).</summary>
        internal bool Patience;

        // R11b: the remaining seven Viper Strike upgrades.
        internal bool Shadowstep;
        internal float BackBonus;
        internal bool CorrosiveBite;
        internal bool TwinFang;
        internal float TwinDamage;
        internal bool LethalDose;
        internal bool AddersGrace;
        internal bool Haemorrhage;
        internal bool LastBreath;
    }

    /// <summary>What one Arc should do after its mods (R7c).</summary>
    internal struct ChainPlan
    {
        internal float DamageScale;

        /// <summary>Overcharge: jumps added (2 per stack).</summary>
        internal int ExtraJumps;

        /// <summary>Static Field's cost: jumps removed, paid once.</summary>
        internal int LostJumps;

        /// <summary>Damage multiplier per jump (0.82, or Overcharge's 0.75).</summary>
        internal float Falloff;

        /// <summary>Static Field: every jump leaves a spark zone.</summary>
        internal bool Spark;

        /// <summary>Share of the jump's damage one spark pulse deals.</summary>
        internal float SparkDamage;

        internal float SparkSeconds;

        internal float SparkRadius;

        /// <summary>Storm Anchor: stacks, stun seconds and the delayed hit.</summary>
        internal int AnchorStacks;

        internal float AnchorStun;

        internal float AnchorDelay;

        /// <summary>Multiplier on the last jump's damage (one per stack).</summary>
        internal float AnchorDamage;

        internal float AnchorRadius;

        // R11b: the remaining seven Arc upgrades.
        internal bool ForkedFate;
        internal bool Conductivity;
        internal bool ChainLightning;
        internal bool Thunderhead;
        internal bool StaticCling;
        internal bool GalvanicRush;
        internal bool BallLightning;
    }

    /// <summary>Costs are paid once, whatever the stack count.</summary>
    private const float GaleDamageCost = 0.15f;
    private const float VortexRadiusCost = 0.10f;

    /// <summary>Per tick pull, per stack (30%, 45%, 60%…): the distance closed
    /// grows with every stack, still clamped to the game's 5-unit knockback.</summary>
    private const float VortexPullFirstStack = 0.30f;
    private const float VortexPullPerExtraStack = 0.15f;

    /// <summary>Gathering Storm: charge seconds and release multiplier climb
    /// linearly with every stack (4.0 s / 3x at 0, shortening and growing
    /// after), never capped at two stacks.</summary>
    internal const float StormRadiusMultiplier = 2.2f;

    private static readonly Dictionary<string, int> StacksById =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>One draft card per mod, cloned from a live tome so the game's own
    /// card frame, icon lookup and picker keep working.</summary>
    private static readonly Dictionary<string, TomeData> CardsById =
        new Dictionary<string, TomeData>(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<int, RewardEntry> EntryByCard = new Dictionary<int, RewardEntry>();

    /// <summary>Per-mod detail line, stored by <see cref="Describe"/>: the same
    /// lambda that prints the current stack also answers the next-stack value
    /// for the draft card ("what this pick gives").</summary>
    private static readonly Dictionary<string, Func<int, string>> Details =
        new Dictionary<string, Func<int, string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Last logged projectile plan, so a change is one log line and a
    /// steady build stays quiet.</summary>
    private static string planSignature;

    /// <summary>The aura and nova mod sets, logged when a stack changes them
    /// (their per-tick numbers move every tick, so the stacks are the key).</summary>
    private static string auraSignature;
    private static string novaSignature;
    private static string meleeSignature;
    private static string chainSignature;

    internal static bool Active => Plugin.SkillModsEnabled.Value;

    /// <summary>The skill's ladder (operator, 2026-11-02): 40 upgrades total,
    /// any distribution across the ten mods. Each pick is one skill level and
    /// the base skill gains +6% damage and −0.6% cooldown per level
    /// (compounding; the design's +4% read too thin for a 40-pick ladder).</summary>
    internal const int MaxStacks = 40;
    private const float SkillDamagePerLevel = 1.06f;
    private const float SkillCooldownPerLevel = 0.994f;

    internal static int TotalStacks
    {
        get
        {
            var total = 0;
            foreach (var stacks in StacksById.Values)
            {
                total += stacks;
            }

            return total;
        }
    }

    internal static float SkillDamageScale() => Mathf.Pow(SkillDamagePerLevel, TotalStacks);

    internal static float SkillCooldownScale() => Mathf.Pow(SkillCooldownPerLevel, TotalStacks);

    internal static int Stacks(string id) =>
        !string.IsNullOrEmpty(id) && StacksById.TryGetValue(id, out var stacks) ? stacks : 0;

    internal static int GaleStacks => Stacks(Gale);

    internal static int VortexStacks => Stacks(Vortex);

    /// <summary>Ash Cloud scales with its picks: +2% reduction and +0.15 s per
    /// extra stack, capped at 60% / 8 s, so the cloud is not a fixed 3 s card.</summary>
    internal static float AshCloudReduction =>
        Mathf.Min(AshReductionCap,
            AshReduction + AshReductionPerStack * Mathf.Max(0, Stacks(AshCloud) - 1));

    internal static float AshCloudSeconds =>
        Mathf.Min(AshSecondsCap,
            AshSeconds + AshSecondsPerStack * Mathf.Max(0, Stacks(AshCloud) - 1));

    /// <summary>The cloud's footprint grows with its picks too, and the visual
    /// uses this same radius (the cards stand on the floor, so the smoke is a
    /// ground cloud rather than a floating bank).</summary>
    internal static float AshCloudRadius =>
        Mathf.Min(AshRadiusCap,
            AshRadius + AshRadiusPerStack * Mathf.Max(0, Stacks(AshCloud) - 1));

    /// <summary>Meteor Call scales with its picks too: +0.06 damage multiplier
    /// (the card's x1.2 grows) and +3% radius per extra stack, with the
    /// telegraph 0.01 s shorter (down to 0.4 s), so a second pick is never a
    /// dead card.</summary>
    internal static float MeteorDamageValue =>
        MeteorDamage + MeteorDamagePerStack * Mathf.Max(0, Stacks(MeteorCall) - 1);

    internal static float MeteorRadiusValue =>
        MeteorRadius + MeteorRadiusPerStack * Mathf.Max(0, Stacks(MeteorCall) - 1);

    internal static float MeteorDelayValue =>
        Mathf.Max(MeteorMinDelay, MeteorDelay - MeteorDelayPerStack * Mathf.Max(0, Stacks(MeteorCall) - 1));

    // Item 19: linear per-stack benefits for the mods whose effect used to be
    // a fixed boolean. Costs stay one-time; only the benefit climbs, so a
    // 10th or 15th pick of the same card is never dead.

    internal static float OrbitDamageValue => OrbitDamage + 0.03f * Mathf.Max(0, Stacks(TwinOrbit) - 1);
    internal static float OrbitRadiusValue => OrbitRadius + 0.01f * Mathf.Max(0, Stacks(TwinOrbit) - 1);
    internal static float VacuumDistance => VacuumMeters + 0.30f * Mathf.Max(0, Stacks(VacuumCut) - 1);
    internal static float AfterimageShare => AfterimageDamage + 0.05f * Mathf.Max(0, Stacks(Afterimage) - 1);
    internal static float AfterimageLife => AfterimageSeconds + 0.10f * Mathf.Max(0, Stacks(Afterimage) - 1);
    internal static float ImmolateShareValue => Mathf.Min(1.5f, ImmolateShare + 0.05f * Mathf.Max(0, Stacks(Immolation) - 1));
    internal static float ImmolateRadiusValue => ImmolateRadius + 0.20f * Mathf.Max(0, Stacks(Immolation) - 1);
    internal static float PyreHealValue => PyreKillHeal * Mathf.Max(1, Stacks(SacrificialPyre));
    internal static float PyreHealCapValue => Mathf.Min(0.20f, PyreKillCap * Mathf.Max(1, Stacks(SacrificialPyre)));
    internal static float KindleCapValue => Mathf.Min(2f, KindleCap + 0.03f * Mathf.Max(0, Stacks(Kindle) - 1));
    internal static float SmoulderShareValue => SmoulderShare + 0.03f * Mathf.Max(0, Stacks(SmoulderingStep) - 1);
    internal static float SmoulderSecondsValue => SmoulderSeconds + 0.10f * Mathf.Max(0, Stacks(SmoulderingStep) - 1);
    internal static float RitesMultiplierValue => RitesMultiplier + 0.10f * Mathf.Max(0, Stacks(LastRites) - 1);
    internal static float RitesSecondsValue => RitesSeconds + 0.20f * Mathf.Max(0, Stacks(LastRites) - 1);
    internal static float RitesCooldownValue => Mathf.Max(8f, RitesCooldown - 0.30f * Mathf.Max(0, Stacks(LastRites) - 1));

    /// <summary>Zealot's Covenant and Ashen Veil are fractions of a fraction:
    /// both are capped, so a deep stack cannot slow past zero or turn the
    /// damage cut into a damage increase.</summary>
    internal static float ZealotSlowValue => Mathf.Min(1f, ZealotSlow * Mathf.Max(1, Stacks(ZealotsCovenant)));
    internal static float VeilReductionValue => Mathf.Min(0.60f, VeilReduction * Mathf.Max(1, Stacks(AshenVeil)));

    internal static float BulwarkSlowValue => Mathf.Min(0.80f, BulwarkSlow + 0.02f * Mathf.Max(0, Stacks(FrozenBulwark) - 1));
    internal static float BulwarkSecondsValue => BulwarkSeconds + 0.10f * Mathf.Max(0, Stacks(FrozenBulwark) - 1);
    internal static float PermafrostSlowValue => Mathf.Min(0.80f, PermafrostSlow + 0.02f * Mathf.Max(0, Stacks(Permafrost) - 1));
    internal static float PermafrostSecondsValue => PermafrostSeconds + 0.10f * Mathf.Max(0, Stacks(Permafrost) - 1);
    internal static float RimeRefundValue => Mathf.Min(0.80f, RimeRefund + 0.02f * Mathf.Max(0, Stacks(RimeCascade) - 1));
    internal static float WinterRadiusValue => WinterRadius + 0.02f * Mathf.Max(0, Stacks(RingOfWinter) - 1);

    /// <summary>The damage cost is paid once; only the radius grows, so a high
    /// stack cannot turn the cost into free damage.</summary>
    internal static float WinterDamageValue => WinterDamage;
    internal static int FractureShardsValue => FractureShards + Mathf.Max(0, Stacks(Fracture) - 1);
    internal static float FractureShareValue => FractureShare + 0.02f * Mathf.Max(0, Stacks(Fracture) - 1);
    internal static float IceboundRootValue => IceboundRoot + 0.05f * Mathf.Max(0, Stacks(Icebound) - 1);
    internal static float ArcticRadiusValue => ArcticRadius + 0.05f * Mathf.Max(0, Stacks(ArcticCharge) - 1);

    /// <summary>Every Nth landing cast is the charge (see <c>CastNova</c>).</summary>
    internal static int ArcticEveryValue => ArcticEvery;

    internal static float CorrosiveCapValue => Mathf.Min(1.0f, CorrosiveCap + 0.02f * Mathf.Max(0, Stacks(CorrosiveBite) - 1));
    internal static float TwinFangShareValue => Mathf.Min(1.0f, TwinFangShare + 0.01f * Mathf.Max(0, Stacks(TwinFang) - 1));
    internal static float AdderBonusValue => Mathf.Min(0.60f, AdderBonus * Mathf.Max(1, Stacks(AddersGrace)));
    internal static int HaemorrhageGoldValue => HaemorrhageGold * Mathf.Max(1, Stacks(Haemorrhage));

    internal static int ForkJumpsValue => ForkJumps + Mathf.Max(0, Stacks(ForkedFate) - 1);
    internal static int ConductivityTargetsValue => Mathf.Min(8, ConductivityTargets + Mathf.Max(0, Stacks(Conductivity) - 1));
    internal static float ChainRangeValue => ChainRange + 0.5f * Mathf.Max(0, Stacks(ChainLightning) - 1);
    internal static float ChainFalloffValue => Mathf.Max(0.80f, ChainExtraFalloff - 0.003f * Mathf.Max(0, Stacks(ChainLightning) - 1));
    internal static float ThunderDamageValue => ThunderDamage + 0.05f * Mathf.Max(0, Stacks(Thunderhead) - 1);
    internal static int ThunderEveryValue => Mathf.Max(2, ThunderEvery - Mathf.Max(0, Stacks(Thunderhead) - 1));
    internal static float ClingBonusValue => Mathf.Min(0.60f, ClingBonus * Mathf.Max(1, Stacks(StaticCling)));
    internal static float GalvanicBonusValue => Mathf.Min(0.50f, GalvanicBonus * Mathf.Max(1, Stacks(GalvanicRush)));
    internal static float GalvanicSecondsValue => GalvanicSeconds + 0.05f * Mathf.Max(0, Stacks(GalvanicRush) - 1);
    internal static float BallRadiusValue => BallRadius + 0.10f * Mathf.Max(0, Stacks(BallLightning) - 1);
    internal static float BallShareValue => BallShare + 0.02f * Mathf.Max(0, Stacks(BallLightning) - 1);
    internal static float BallSecondsValue => BallSeconds + 0.05f * Mathf.Max(0, Stacks(BallLightning) - 1);

    internal static float ShatterBonusValue => ShatterBonus + 0.05f * Mathf.Max(0, Stacks(Shatterpoint) - 1);
    internal static int ShatterShardsValue => ShatterShards + Mathf.Max(0, Stacks(Shatterpoint) - 1);
    internal static float ShatterShareValue => ShatterShare + 0.02f * Mathf.Max(0, Stacks(Shatterpoint) - 1);
    internal static float GraspSecondsValue => GraspSeconds + 0.10f * Mathf.Max(0, Stacks(WintersGrasp) - 1);
    internal static float GraspSlowValue => Mathf.Min(0.80f, GraspSlow + 0.02f * Mathf.Max(0, Stacks(WintersGrasp) - 1));
    internal static float MarkSplashValue => MarkSplash + 0.02f * Mathf.Max(0, Stacks(HuntersMark) - 1);
    internal static float MarkRadiusValue => MarkRadius + 0.10f * Mathf.Max(0, Stacks(HuntersMark) - 1);
    internal static float SleetBonusValue => Mathf.Min(0.50f, SleetBonus * Mathf.Max(1, Stacks(SleetStep)));
    internal static float PrisonRootValue => PrisonRoot + 0.05f * Mathf.Max(0, Stacks(IcePrison) - 1);
    internal static float ColdSnapRadiusValue => ColdSnapRadius + 0.10f * Mathf.Max(0, Stacks(ColdSnap) - 1);
    internal static float ColdSnapSecondsValue => ColdSnapSeconds + 0.03f * Mathf.Max(0, Stacks(ColdSnap) - 1);

    internal static float IgniteBloomShareValue => Mathf.Min(1.5f, IgniteBloomShare + 0.05f * Mathf.Max(0, Stacks(IgniteBloom) - 1));
    internal static float FamiliarShareValue => FamiliarShare + 0.03f * Mathf.Max(0, Stacks(EmberFamiliar) - 1);
    internal static float FamiliarIntervalValue => Mathf.Max(1f, FamiliarInterval - 0.10f * Mathf.Max(0, Stacks(EmberFamiliar) - 1));
    internal static float WildfireRadiusValue => WildfireRadius + 0.10f * Mathf.Max(0, Stacks(Wildfire) - 1);
    internal static float WildfireShareValue => Mathf.Min(1.5f, WildfireShare + 0.02f * Mathf.Max(0, Stacks(Wildfire) - 1));

    /// <summary>Wildfire's cost is the ignite, not the fireball's own hit: the
    /// burn it applies deals 20% less, paid once, so the growing spread share
    /// can never become extra direct damage.</summary>
    internal static float WildfireIgniteScale => Stacks(Wildfire) > 0 ? 1f - WildfireIgniteCost : 1f;
    internal static float WildfireIntervalValue => Mathf.Max(0.40f, WildfireInterval - 0.03f * Mathf.Max(0, Stacks(Wildfire) - 1));
    internal static float CombustionPerHitValue => CombustionPerHit + 0.005f * Mathf.Max(0, Stacks(Combustion) - 1);
    internal static int CombustionCapValue => CombustionCap + Mathf.Max(0, Stacks(Combustion) - 1);

    /// <summary>The branch a mod belongs to: its <c>requires</c>, or itself when
    /// the sheet leaves it empty (every mod is its own branch then).</summary>
    internal static string Branch(RewardEntry entry) =>
        string.IsNullOrEmpty(entry.Requires) ? entry.Id : entry.Requires;

    /// <summary>True when another mod in this branch is already taken, so the
    /// branch is closed to the offered card.</summary>
    internal static bool BranchTaken(string branch, string exceptId)
    {
        if (string.IsNullOrEmpty(branch))
        {
            return false;
        }

        foreach (var pair in StacksById)
        {
            if (pair.Value <= 0 || string.Equals(pair.Key, exceptId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (RewardTable.TryGet(pair.Key, out var other) && other.IsMod &&
                string.Equals(Branch(other), branch, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- stacks

    internal static void Add(RewardEntry entry)
    {
        if (entry == null)
        {
            return;
        }

        if (TotalStacks >= MaxStacks)
        {
            Plugin.Logger.LogWarning(
                $"skill mod: {entry.Name} refused, the skill already has {MaxStacks} upgrades");
            return;
        }

        var stacks = Stacks(entry.Id) + 1;
        StacksById[entry.Id] = stacks;
        Plugin.Logger.LogInfo(
            $"skill mod: {entry.Name} taken ({stacks}/{MaxStacks}, skill level {TotalStacks}/{MaxStacks}) " +
            $"[{entry.Rarity}/t{entry.Tier}] branch {Branch(entry)}");
    }

    /// <summary>A new run starts from zero. The cards themselves are kept: they
    /// are stateless clones and rebuilding them costs a clone per draft.</summary>
    internal static void Reset()
    {
        planSignature = null;
        auraSignature = null;
        novaSignature = null;
        meleeSignature = null;
        chainSignature = null;
        if (StacksById.Count == 0)
        {
            return;
        }

        StacksById.Clear();
        Plugin.Logger.LogInfo("skill mods reset with the run");
    }

    // ----------------------------------------------------------------- cards

    /// <summary>The draft card for a mod (a <c>TomeData</c> clone of the Damage
    /// tome, whose slot is never added to the inventory because the pick is
    /// intercepted). Null when no tome could be cloned.</summary>
    internal static TomeData Card(RewardEntry entry)
    {
        if (!Active || entry == null)
        {
            return null;
        }

        if (CardsById.TryGetValue(entry.Id, out var card) && card != null)
        {
            return card;
        }

        try
        {
            var manager = DataManager.Instance;
            var source = manager == null ? null : manager.GetTome(ETome.Damage);
            if (source == null)
            {
                Plugin.Logger.LogWarning($"skill mod: no tome to clone for {entry.Id}");
                return null;
            }

            card = UnityEngine.Object.Instantiate(source).TryCast<TomeData>();
            if (card == null)
            {
                Plugin.Logger.LogWarning($"skill mod: clone returned no TomeData for {entry.Id}");
                return null;
            }

            card.name = entry.Id;
            card.description = entry.Text;
            card.hideFlags = HideFlags.DontSave;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"skill mod: could not build the card for {entry.Id} ({error.Message})");
            return null;
        }

        CardsById[entry.Id] = card;
        EntryByCard[card.GetInstanceID()] = entry;
        Plugin.Logger.LogInfo($"skill mod: card ready for {entry.Name} (tier {entry.Tier}, {entry.Rarity})");
        return card;
    }

    internal static bool TryGetCard(TomeData card, out RewardEntry entry)
    {
        entry = null;
        return card != null && EntryByCard.TryGetValue(card.GetInstanceID(), out entry) && entry != null;
    }

    /// <summary>The pick: <c>PlayerInventory.AddUpgrade</c> asks this first, and
    /// a mod card is consumed here instead of by the native tome path.</summary>
    internal static bool TryTake(IUpgradable upgradable, out RewardEntry entry)
    {
        entry = null;
        if (upgradable == null)
        {
            return false;
        }

        var card = upgradable.TryCast<TomeData>();
        return card != null && TryGetCard(card, out entry);
    }

    // --------------------------------------------------------------- cyclone

    /// <summary>Gale Offshoots' cost: the spin deals 15% less, once.</summary>
    internal static float CycloneDamageScale() => GaleStacks > 0 ? 1f - GaleDamageCost : 1f;

    /// <summary>Vortex Lure's cost: a 10% smaller spin, once.</summary>
    internal static float CycloneRadiusScale() => VortexStacks > 0 ? 1f - VortexRadiusCost : 1f;

    internal static bool GatheringStorm(out int stacks)
    {
        stacks = Stacks(Storm);
        return stacks > 0 && Active;
    }

    internal static float StormCharge(int stacks) => Mathf.Max(1f, 2.5f - 0.5f * stacks);

    internal static float StormRelease(int stacks) => 3f + 0.6f * stacks;

    /// <summary>How much of the remaining distance one spin closes, clamped to
    /// the game's own knockback ceiling (<c>EnemyMovementRb.maxKnockback</c> 5),
    /// so the pull goes through the native movement code.</summary>
    internal static float VortexPull(float distance, int stacks)
    {
        if (stacks <= 0)
        {
            return 0f;
        }

        var share = VortexPullFirstStack + VortexPullPerExtraStack * (stacks - 1);
        return Mathf.Min(5f, distance * share);
    }

    // -------------------------------------------------------------- projectile

    /// <summary>
    /// The Ranger's Ice Shot and the Witch's Fireball in one plan (R7). Each
    /// mod belongs to one skill, so the other class's stacks are ignored; the
    /// numbers are the design table's, per stack, with the costs applied once.
    ///
    /// Branches: Ricochet, Volley Stance and Glacial Lance share one branch
    /// (all three rewrite what a hit is worth, and two of them remove the
    /// pierce the third rewards), Twin Flame and Split Ash share one (both add
    /// balls); Pyromantic Focus is independent.
    /// </summary>
    internal static ProjectilePlan Projectile(string skillId)
    {
        var plan = new ProjectilePlan
        {
            VolleyDamage = 1f,
            DamageScale = 1f,
            RadiusScale = 1f,
            CooldownScale = 1f
        };

        if (!Active || string.IsNullOrEmpty(skillId))
        {
            return plan;
        }

        if (string.Equals(skillId, IceShotSkill, StringComparison.OrdinalIgnoreCase))
        {
            var ricochet = Stacks(Ricochet);
            var volley = Stacks(Volley);
            var lance = Stacks(Lance);

            if (ricochet > 0)
            {
                plan.RicochetBounces = ricochet;
                plan.NoPierce = true;
            }

            if (volley > 0)
            {
                plan.VolleyArrows = VolleyArrowsPerStack * volley;
                plan.VolleyArc = VolleyArcPerStack * volley;
                plan.VolleyDamage = VolleyDamageShare;
                plan.NoPierce = true;
            }

            if (lance > 0)
            {
                plan.PierceDamage = LanceDamagePerStack * lance;
                plan.CooldownScale *= 1f + LanceCooldownCost;
            }

            if (Stacks(Shatterpoint) > 0)
            {
                plan.Shatter = true;
                plan.ShatterShards = ShatterShardsValue;
                plan.ShatterDamage = ShatterShareValue;
            }

            if (Stacks(WintersGrasp) > 0)
            {
                plan.WinterGrasp = true;
                plan.GraspSeconds = GraspSecondsValue;
                plan.GraspSlow = GraspSlowValue;
            }

            if (Stacks(HuntersMark) > 0)
            {
                plan.HuntersMark = true;
                plan.MarkSplash = MarkSplashValue;
                plan.MarkRadius = MarkRadiusValue;
            }

            if (Stacks(Frostbite) > 0)
            {
                plan.Frostbite = true;
                plan.DamageScale *= 1f - FrostbiteCost;
            }

            plan.SleetStep = Stacks(SleetStep) > 0;
            plan.IcePrison = Stacks(IcePrison) > 0;
            plan.ColdSnap = Stacks(ColdSnap) > 0;

            // Cold Snap's cost is paid on the shatter it depends on.
            if (plan.ColdSnap && plan.Shatter)
            {
                plan.ShatterDamage *= 1f - ColdSnapShatterCost;
            }
        }
        else if (string.Equals(skillId, FireballSkill, StringComparison.OrdinalIgnoreCase))
        {
            var twin = Stacks(TwinFlame);
            var split = Stacks(SplitAsh);
            var focus = Stacks(Focus);

            if (twin > 0)
            {
                plan.ExtraBolts = twin;
                plan.ExtraBoltDamage = TwinBoltDamageShare;
                plan.DamageScale *= 1f - TwinMainCost;
            }

            if (split > 0)
            {
                plan.SplitBalls = SplitBallsPerStack * split;
                plan.SplitDamage = SplitDamageShare;
                plan.RadiusScale *= 1f - SplitRadiusCost;
            }

            if (focus > 0)
            {
                plan.DamageScale *= 1f + FocusDamagePerStack * focus;
                plan.RadiusScale *= 1f + FocusRadiusPerStack * focus;
                plan.CooldownScale *= 1f + FocusCooldownCost;
            }

            if (Stacks(RollingEmber) > 0)
            {
                plan.RollingEmber = true;
                plan.RicochetBounces = Mathf.Max(plan.RicochetBounces, Stacks(RollingEmber));
            }

            if (Stacks(IgniteBloom) > 0)
            {
                plan.IgniteBloom = true;
                plan.IgniteBloomShare = IgniteBloomShareValue;
            }

            if (Stacks(EmberFamiliar) > 0)
            {
                plan.EmberFamiliar = true;
                plan.FamiliarDamage = FamiliarShareValue;
                plan.FamiliarInterval = FamiliarIntervalValue;
                plan.DamageScale *= 1f - FamiliarCost;
            }

            if (Stacks(MeteorCall) > 0)
            {
                plan.MeteorCall = true;
                plan.MeteorDamage = MeteorDamageValue;
                plan.MeteorRadius = MeteorRadiusValue;
                plan.MeteorDelay = MeteorDelayValue;
            }

            if (Stacks(Wildfire) > 0)
            {
                plan.Wildfire = true;
                plan.WildfireRadius = WildfireRadiusValue;
                plan.WildfireInterval = WildfireIntervalValue;
                plan.WildfireDamage = WildfireShareValue;
            }

            plan.AshCloud = Stacks(AshCloud) > 0;

            if (Stacks(Combustion) > 0)
            {
                plan.Combustion = true;
                plan.CombustionPerHit = CombustionPerHitValue;
                plan.CombustionCap = CombustionCapValue;
            }
        }

        Announce(skillId, plan);
        return plan;
    }

    // ------------------------------------------------------------- righteous fire

    /// <summary>
    /// The Marauder's Righteous Fire (R7b), one plan per tick. Fan the Flames
    /// reads the enemies the aura burned on its **last** tick, because inside a
    /// burning aura those are exactly the burning enemies: the count is one
    /// tick stale, which is what makes the radius grow instead of arriving at
    /// full size. Pyre of the Faithful pays its radius cost whether the player
    /// stands or walks; only the damage needs the stand-still.
    /// </summary>
    internal static AuraPlan Aura(string skillId, int burningInside, float standstillSeconds)
    {
        var plan = new AuraPlan { DamageScale = 1f, RadiusScale = 1f };
        if (!Active || string.IsNullOrEmpty(skillId))
        {
            return plan;
        }

        if (string.Equals(skillId, CycloneSkill, StringComparison.OrdinalIgnoreCase))
        {
            var razor = Stacks(Razorwind);
            if (razor > 0)
            {
                plan.Razorwind = true;
                plan.RazorDamage = RazorDamageShare * razor;
            }

            plan.Momentum = Stacks(Momentum) > 0;
            plan.BloodWhirl = Stacks(BloodWhirl) > 0;
            plan.VacuumCut = Stacks(VacuumCut) > 0;
            plan.Afterimage = Stacks(Afterimage) > 0;

            var iron = Stacks(IronCyclone);
            if (iron > 0)
            {
                plan.IronCyclone = true;
                plan.RadiusScale *= 1f + (IronRadius - 1f) * iron;
                plan.DamageScale *= 1f + (IronDamage - 1f) * iron;
            }

            var orbit = Stacks(TwinOrbit);
            if (orbit > 0)
            {
                plan.TwinOrbit = true;
                plan.RadiusScale *= OrbitRadiusValue;
                plan.DamageScale *= OrbitDamageValue;
                plan.OrbitInner = OrbitInnerShare;
            }

            AnnounceAura(skillId, plan, razor, 0, 0);
            return plan;
        }

        if (!string.Equals(skillId, RighteousFireSkill, StringComparison.OrdinalIgnoreCase))
        {
            return plan;
        }

        var fan = Stacks(FanTheFlames);
        var pyre = Stacks(PyreOfFaithful);
        var trail = Stacks(CinderTrail);

        if (fan > 0)
        {
            var bonus = Mathf.Min(
                FanRadiusPerEnemy * fan * Mathf.Max(0, burningInside),
                FanRadiusCapPerStack * fan);
            plan.RadiusScale *= 1f + bonus;
            plan.DamageScale *= 1f - FanBurnCost;
        }

        if (pyre > 0)
        {
            plan.RadiusScale *= 1f - PyreRadiusCost;
            if (standstillSeconds >= PyreStandstillSeconds)
            {
                plan.DamageScale *= 1f + PyreDamagePerStack * pyre;
            }
        }

        if (trail > 0)
        {
            plan.Trail = true;
            plan.TrailDamage = TrailDamagePerStack * trail;
            plan.DamageScale *= 1f - TrailCost;
        }

        plan.SacrificialPyre = Stacks(SacrificialPyre) > 0;
        plan.Immolation = Stacks(Immolation) > 0;
        plan.SmoulderingStep = Stacks(SmoulderingStep) > 0;
        plan.LastRites = Stacks(LastRites) > 0;

        var zealot = Stacks(ZealotsCovenant);
        if (zealot > 0)
        {
            plan.ZealotsCovenant = true;
            plan.ZealotSlow = ZealotSlowValue;
            plan.DamageScale *= 1f - ZealotCost;
        }

        var veil = Stacks(AshenVeil);
        if (veil > 0)
        {
            plan.AshenVeil = true;
            plan.VeilReduction = VeilReductionValue;
            plan.RadiusScale *= 1f - VeilRadiusCost;
        }

        var kindle = Stacks(Kindle);
        if (kindle > 0)
        {
            plan.Kindle = true;
            plan.DamageScale *= 1f - KindleCost;
        }

        AnnounceAura(skillId, plan, fan, pyre, trail);
        return plan;
    }

    // ------------------------------------------------------------------ ice nova

    /// <summary>
    /// The Templar's Ice Nova (R7b). Convergence and Glacial Echo share a
    /// branch: one rewrites where the burst is worth hitting, the other makes
    /// the same burst happen twice. Deep Freeze is independent and pays with
    /// the cooldown. The echo is damage only, so it can never freeze.
    /// </summary>
    internal static NovaPlan Nova(string skillId)
    {
        var plan = new NovaPlan { DamageScale = 1f, RadiusScale = 1f };
        if (!Active || !string.Equals(skillId, IceNovaSkill, StringComparison.OrdinalIgnoreCase))
        {
            return plan;
        }

        var echo = Stacks(GlacialEcho);
        var convergence = Stacks(Convergence);
        var freeze = Stacks(DeepFreeze);

        if (echo > 0)
        {
            plan.Echoes = EchoPerStack * echo;
            plan.EchoDamage = EchoDamageShare;
        }

        if (convergence > 0)
        {
            plan.EdgeDamage = ConvergenceEdgeDamage * convergence;
            plan.EdgeStart = ConvergenceEdgeStart;
            plan.Pull = ConvergencePullMeters;
        }

        plan.FrozenBulwark = Stacks(FrozenBulwark) > 0;
        plan.Fracture = Stacks(Fracture) > 0;
        plan.Icebound = Stacks(Icebound) > 0;
        plan.ArcticCharge = Stacks(ArcticCharge) > 0;

        if (Stacks(Permafrost) > 0)
        {
            plan.Permafrost = true;
            plan.DamageScale *= 1f - PermafrostCost;
        }

        if (Stacks(RimeCascade) > 0)
        {
            plan.RimeCascade = true;
            plan.RimeRefund = RimeRefundValue;
        }

        if (Stacks(RingOfWinter) > 0)
        {
            plan.RingOfWinter = true;
            plan.RadiusScale *= WinterRadiusValue;
            plan.DamageScale *= WinterDamageValue;
        }

        AnnounceNova(skillId, plan, echo, convergence, freeze);
        return plan;
    }

    // ------------------------------------------------------- viper strike (R7c)

    /// <summary>
    /// The Shadow's Viper Strike (R7c), one plan per strike. Serpent's
    /// Patience is the only cast-side mod: it pays the stand-still wait with
    /// more damage and more poison stacks, and the caller restarts the clock so
    /// the next bonus needs another 2 s. Toxic Bloom and Venom Spread both ride
    /// the poison already on the corpse, so they are read at the kill instead
    /// (see <see cref="ToxicBloomShare"/> / <see cref="VenomSpreadTargets"/>) —
    /// they share one branch and never stack together.
    /// </summary>
    internal static MeleePlan Melee(string skillId, float standstillSeconds)
    {
        var plan = new MeleePlan { DamageScale = 1f };
        if (!Active || !string.Equals(skillId, ViperStrikeSkill, StringComparison.OrdinalIgnoreCase))
        {
            return plan;
        }

        var patience = Stacks(SerpentsPatience);
        if (patience > 0 && standstillSeconds >= PatienceStandstillSeconds)
        {
            plan.DamageScale *= 1f + PatienceDamagePerStack * patience;
            plan.ExtraPoisonStacks = PatienceStacksPerStack * patience;
            plan.Patience = true;
        }

        plan.CorrosiveBite = Stacks(CorrosiveBite) > 0;
        plan.LethalDose = Stacks(LethalDose) > 0;
        plan.Haemorrhage = Stacks(Haemorrhage) > 0;
        plan.LastBreath = Stacks(LastBreath) > 0;

        if (Stacks(Shadowstep) > 0)
        {
            // R11b: “from behind” is retired — the monsters always face the
            // player. The strike itself now carries the bonus and the extra
            // poison stack, both scaling with the mod's stacks.
            var shadow = Stacks(Shadowstep);
            plan.Shadowstep = true;
            plan.BackBonus = ShadowBehind * shadow;
            plan.ExtraPoisonStacks += ShadowStacks * shadow;
        }

        if (Stacks(TwinFang) > 0)
        {
            plan.TwinFang = true;
            plan.TwinDamage = TwinFangShareValue;
        }

        if (Stacks(AddersGrace) > 0)
        {
            plan.AddersGrace = true;
            plan.DamageScale *= 1f - AdderCost;
        }

        AnnounceMelee(skillId, patience);
        return plan;
    }

    /// <summary>Toxic Bloom: the share of a strike's damage one cloud pulse
    /// deals, and the cloud's shape. Zero when the mod is not taken.</summary>
    internal static float ToxicBloomShare => Active ? BloomDamageShare * Stacks(ToxicBloom) : 0f;

    internal static float ToxicBloomSeconds => BloomSeconds;

    internal static float ToxicBloomRadius => BloomRadius;

    /// <summary>Venom Spread: corpses one death passes its poison on to.</summary>
    internal static int VenomSpreadTargets => Active ? SpreadTargetsPerStack * Stacks(VenomSpread) : 0;

    internal static float VenomSpreadRadius => SpreadRadius;

    // ---------------------------------------------------------------- arc (R7c)

    /// <summary>
    /// The Scion's Arc (R7c), one plan per cast. Static Field and Overcharge
    /// share a branch: one trades a jump for ground damage, the other adds
    /// jumps and pays with a steeper falloff. Storm Anchor is independent and
    /// names its numbers because the design leaves the clap's timing to us.
    /// </summary>
    internal static ChainPlan Chain(string skillId)
    {
        var plan = new ChainPlan { Falloff = ArcFalloff, DamageScale = 1f };
        if (!Active || !string.Equals(skillId, ArcSkill, StringComparison.OrdinalIgnoreCase))
        {
            return plan;
        }

        var field = Stacks(StaticField);
        var overcharge = Stacks(Overcharge);
        var anchor = Stacks(StormAnchor);

        if (field > 0)
        {
            plan.LostJumps = StaticFieldJumpCost;
            plan.Spark = true;
            plan.SparkDamage = SparkDamageShare * field;
            plan.SparkSeconds = SparkSeconds + 0.05f * Mathf.Max(0, field - 1);
            plan.SparkRadius = SparkRadius + 0.10f * Mathf.Max(0, field - 1);
        }

        if (overcharge > 0)
        {
            plan.ExtraJumps = OverchargeJumpsPerStack * overcharge;
            plan.Falloff = OverchargeFalloff;
        }

        if (anchor > 0)
        {
            plan.AnchorStacks = anchor;
            plan.AnchorStun = AnchorStunSeconds * anchor;
            plan.AnchorDelay = AnchorDelaySeconds;
            plan.AnchorDamage = anchor;
            plan.AnchorRadius = AnchorRadius;
        }

        plan.ForkedFate = Stacks(ForkedFate) > 0;
        plan.Conductivity = Stacks(Conductivity) > 0;
        plan.Thunderhead = Stacks(Thunderhead) > 0;
        plan.StaticCling = Stacks(StaticCling) > 0;
        plan.BallLightning = Stacks(BallLightning) > 0;

        if (Stacks(ChainLightning) > 0)
        {
            plan.ChainLightning = true;
            plan.Falloff *= ChainFalloffValue;
        }

        if (Stacks(GalvanicRush) > 0)
        {
            plan.GalvanicRush = true;
            plan.DamageScale *= 1f - GalvanicCost;
        }

        AnnounceChain(skillId, plan, field, overcharge, anchor);
        return plan;
    }

    /// <summary>The value the next stack of this mod would have, for the draft
    /// card: “if you take this” rather than the sheet's static text. The
    /// details are the ones <see cref="Describe"/> prints (populated on first
    /// use), with the stack count raised by one.</summary>
    internal static string Preview(string id)
    {
        if (!Active || string.IsNullOrEmpty(id))
        {
            return null;
        }

        // The detail lines close over the plans Describe() builds, and those
        // plans read the *current* stacks. Bump this mod to the candidate stack
        // for the duration of the rebuild, so every plan-derived value (Static
        // Field's spark, a nova's echo, …) is the one the pick would produce.
        var had = StacksById.TryGetValue(id, out var previous);
        var candidate = (had ? previous : 0) + 1;
        StacksById[id] = candidate;
        try
        {
            Describe();
            return Details.TryGetValue(id, out var detail) ? detail(candidate) : null;
        }
        finally
        {
            if (had)
            {
                StacksById[id] = previous;
            }
            else
            {
                StacksById.Remove(id);
            }
        }
    }

    /// <summary>One line per taken mod for the Échap panel, in class order. The
    /// numbers come from the same plans the casts read (or from the constants
    /// those plans are built with), so the panel cannot state a value the skill
    /// does not use. A line only appears for a mod that has a stack.
    /// </summary>
    internal static List<string> Describe()
    {
        var lines = new List<string>();
        if (!Active)
        {
            return lines;
        }

        var skillId = PoeSkills.ActiveSkillId;
        var projectile = Projectile(skillId);
        var nova = Nova(skillId);
        var chain = Chain(skillId);

        if (TotalStacks > 0)
        {
            lines.Add(
                $"Skill level {TotalStacks}/{MaxStacks}: damage x{SkillDamageScale():0.##}, " +
                $"cooldown x{SkillCooldownScale():0.##}");
        }

        Add(lines, Gale, stacks =>
            $"{stacks} offshoot(s) every 3rd spin at 40 %; spin damage x{CycloneDamageScale():0.##}");
        Add(lines, Vortex, stacks =>
            $"spin radius x{CycloneRadiusScale():0.##} (paid once); each spin pulls " +
            $"{VortexPull(1f, stacks):0%} of the distance (max 5 m)");
        Add(lines, Storm, stacks =>
            $"charge {StormCharge(stacks):0.0} s standing still, release x{StormRelease(stacks):0.0} in x{StormRadiusMultiplier:0.0} radius");

        Add(lines, Ricochet, _ =>
            $"arrows hop to {projectile.RicochetBounces} more target(s) at {RicochetDamageShare:0%}; no pierce");
        Add(lines, Volley, _ =>
            $"fan {projectile.VolleyArrows} arrows over {projectile.VolleyArc:0}° at {projectile.VolleyDamage:0%}; no pierce");
        Add(lines, Lance, _ =>
            $"+{projectile.PierceDamage:0%} damage per enemy already pierced; cooldown x{projectile.CooldownScale:0.##}");
        Add(lines, TwinFlame, _ =>
            $"+{projectile.ExtraBolts} extra bolt(s) at {projectile.ExtraBoltDamage:0%}; main cast x{projectile.DamageScale:0.##}");
        Add(lines, SplitAsh, _ =>
            $"{projectile.SplitBalls} split ball(s) at {projectile.SplitDamage:0%}; radius x{projectile.RadiusScale:0.##}");
        Add(lines, Focus, _ =>
            $"damage x{projectile.DamageScale:0.##}, radius x{projectile.RadiusScale:0.##}, cooldown x{projectile.CooldownScale:0.##}");

        Add(lines, FanTheFlames, stacks =>
            $"radius up to +{FanRadiusCapPerStack * stacks:0%} " +
            $"({FanRadiusPerEnemy * stacks:0%} per burning enemy); aura damage x{1f - FanBurnCost:0.##}");
        Add(lines, PyreOfFaithful, stacks =>
            $"+{PyreDamagePerStack * stacks:0%} damage after {PyreStandstillSeconds:0.#} s still; " +
            $"radius x{1f - PyreRadiusCost:0.##}");
        Add(lines, CinderTrail, stacks =>
            $"the ground keeps burning at x{TrailDamagePerStack * stacks:0.##} per tick; " +
            $"aura damage x{1f - TrailCost:0.##}");

        Add(lines, GlacialEcho, _ =>
            $"{nova.Echoes} repeat(s) at {nova.EchoDamage:0%} damage each");
        Add(lines, Convergence, _ =>
            $"+{nova.EdgeDamage:0%} past {nova.EdgeStart:0%} of the radius, dragged {nova.Pull:0.#} m in");
        Add(lines, DeepFreeze, _ =>
            $"freeze x{FreezeScale(IceNovaSkill):0.##}, cooldown x{CooldownScale(IceNovaSkill):0.##}");

        Add(lines, ToxicBloom, stacks =>
            $"a poisoned kill leaves a {ToxicBloomRadius:0.#} m cloud for {ToxicBloomSeconds:0.#} s " +
            $"at x{BloomDamageShare * stacks:0.##}");
        Add(lines, VenomSpread, stacks =>
            $"a poisoned kill passes on to {SpreadTargetsPerStack * stacks} enemy(ies) in {SpreadRadius:0.#} m");
        Add(lines, SerpentsPatience, stacks =>
            $"+{PatienceDamagePerStack * stacks:0%} damage and +{PatienceStacksPerStack * stacks} poison stack(s) " +
            $"after {PatienceStandstillSeconds:0.#} s still");

        Add(lines, StaticField, _ =>
            $"-{chain.LostJumps} jump, every jump leaves a {chain.SparkRadius:0.#} m spark " +
            $"at x{chain.SparkDamage:0.##} for {chain.SparkSeconds:0.#} s");
        Add(lines, Overcharge, _ =>
            $"+{chain.ExtraJumps} jumps, damage falloff x{chain.Falloff:0.##}");
        Add(lines, StormAnchor, stacks =>
            $"the last enemy is stunned {chain.AnchorStun:0.#} s, then hit x{chain.AnchorDamage:0.##} " +
            $"after {chain.AnchorDelay:0.#} s in {chain.AnchorRadius:0.#} m");

        // R11b: the remaining seven per skill.
        Add(lines, Razorwind, stacks =>
            $"the spin leaves a {TrailLifeText()} blade trail at x{RazorDamageShare * stacks:0.##}");
        Add(lines, Momentum, stacks =>
            $"+{MomentumPerHit * stacks:0%} spin damage per enemy hit, cap +{MomentumCap * stacks:0%}");
        Add(lines, IronCyclone, stacks =>
            $"spin radius x{1f + (IronRadius - 1f) * stacks:0.##}, damage x{1f + (IronDamage - 1f) * stacks:0.##}, " +
            $"movement x{IronMoveScale:0.##}");
        Add(lines, TwinOrbit, _ =>
            $"radius x{OrbitRadiusValue:0.##}, damage x{OrbitDamageValue:0.##}; the inner {OrbitInnerShare:0%} is empty");
        Add(lines, BloodWhirl, stacks =>
            $"crits heal {BloodWhirlHeal * stacks:0%} max Life; crit chance x{CritScale(CycloneSkill):0.##}");
        Add(lines, VacuumCut, _ =>
            $"every {VacuumEvery}th spin knocks back {VacuumDistance:0.#} m and deals no damage");
        Add(lines, Afterimage, _ =>
            $"a {AfterimageLife:0.#} s ghost spin at x{AfterimageShare:0.##} when the pack empties");

        Add(lines, SacrificialPyre, _ =>
            $"self-burn {PyreSelfBurn:0.0%}/s while enemies burn, heal {PyreHealValue:0%} per kill (cap {PyreHealCapValue:0%}/s)");
        Add(lines, Immolation, _ =>
            $"a burning death explodes for {ImmolateShareValue:0%} max Life in {ImmolateRadiusValue:0.#} m");
        Add(lines, ZealotsCovenant, stacks =>
            $"the aura slows {ZealotSlowValue:0%}; burn damage x{1f - ZealotCost:0.##}");
        Add(lines, AshenVeil, stacks =>
            $"{VeilReductionValue:0%} less damage taken with 3+ burning; radius x{1f - VeilRadiusCost:0.##}");
        Add(lines, Kindle, _ =>
            $"+{KindlePerSecond:0%} damage per second burning (cap +{KindleCapValue:0%}); burn x{1f - KindleCost:0.##}");
        Add(lines, SmoulderingStep, _ =>
            $"a dash leaves a {SmoulderSecondsValue:0.#} s fire patch at x{SmoulderShareValue:0.##}; " +
            $"cooldown x{1f + SmoulderCooldownCost:0.##}");
        Add(lines, LastRites, _ =>
            $"under {PyreFloor:0%} Life: aura damage x{RitesMultiplierValue:0.#} for {RitesSecondsValue:0.#} s " +
            $"(once/{RitesCooldownValue:0} s)");

        Add(lines, Shatterpoint, _ =>
            $"frozen hits +{ShatterBonusValue:0%}, {ShatterShardsValue} shards at {ShatterShareValue:0%}");
        Add(lines, WintersGrasp, _ =>
            $"a {GraspSecondsValue:0.#} s frost patch at {GraspSlowValue:0%} slow where the arrow expires");
        Add(lines, HuntersMark, _ =>
            $"the farthest enemy is marked; hits splash {MarkSplashValue:0%} in {MarkRadiusValue:0.#} m");
        Add(lines, Frostbite, stacks =>
            $"+{FrostbitePerHit * stacks:0%} freeze chance per hit on a frozen enemy; damage x{1f - FrostbiteCost:0.##}");
        Add(lines, SleetStep, _ =>
            $"+{SleetBonusValue:0%} Movement Speed for {SleetSeconds:0.#} s after a hit");
        Add(lines, IcePrison, _ =>
            $"every {PrisonHits}rd hit on the same enemy roots {PrisonRootValue:0.#} s");
        Add(lines, ColdSnap, _ =>
            $"a frozen death freezes {ColdSnapRadiusValue:0.#} m for {ColdSnapSecondsValue:0.#} s; " +
            $"shatter x{1f - ColdSnapShatterCost:0.##}");

        Add(lines, RollingEmber, stacks =>
            $"the ball bounces {stacks} time(s); the bounced hit does not ignite");
        Add(lines, IgniteBloom, _ =>
            $"an ignited death explodes for {IgniteBloomShareValue:0%} of the killing hit");
        Add(lines, EmberFamiliar, stacks =>
            $"{Mathf.Min(8, stacks)} ember(s) lob a {FamiliarShareValue:0%} fireball in turn every {FamiliarIntervalValue:0.#} s; " +
            $"own casts x{1f - FamiliarCost:0.##}");
        Add(lines, MeteorCall, _ =>
            $"after {MeteorDelayValue:0.##} s: x{MeteorDamageValue:0.##} damage in x{MeteorRadiusValue:0.##} radius, +1 impact per projectile");
        Add(lines, Wildfire, _ =>
            $"the ignite spreads in {WildfireRadiusValue:0.#} m every {WildfireIntervalValue:0.#} s " +
            $"at x{WildfireShareValue:0.##}");
        Add(lines, AshCloud, _ =>
            $"a {AshCloudSeconds:0.#} s {AshCloudRadius * 2f:0.#} m smoke cloud: " +
            $"{AshCloudReduction:0%} less damage taken inside");
        Add(lines, Combustion, _ =>
            $"+{CombustionPerHitValue:0%} damage per re-hit on an ignited enemy (cap {CombustionCapValue})");

        Add(lines, FrozenBulwark, _ =>
            $"a {BulwarkSecondsValue:0.#} s ice field at the edge, {BulwarkSlowValue:0%} slow");
        Add(lines, Permafrost, _ =>
            $"the ground slows {PermafrostSlowValue:0%} for {PermafrostSecondsValue:0.#} s; " +
            $"nova x{1f - PermafrostCost:0.##}");
        Add(lines, RimeCascade, _ =>
            $"{RimeTargets}+ enemies refund {RimeRefundValue:0%} of the cooldown");
        Add(lines, RingOfWinter, _ =>
            $"radius x{WinterRadiusValue:0.##}, damage x{WinterDamageValue:0.##}");
        Add(lines, Fracture, _ =>
            $"{FractureShardsValue} ice shards on a nova kill at x{FractureShareValue:0.##}");
        Add(lines, Icebound, _ =>
            $"root {IceboundRootValue:0.#} s instead of freeze");
        Add(lines, ArcticCharge, _ =>
            $"every {ArcticEvery}rd cast charges {ArcticDelay:0.#} s and bursts x{ArcticRadiusValue:0.##}");

        Add(lines, Shadowstep, stacks =>
            $"+{ShadowBehind * stacks:0%} strike damage and +{ShadowStacks * stacks} poison stack(s), always (no back read)");
        Add(lines, CorrosiveBite, stacks =>
            $"+{CorrosivePerStack * stacks:0%} damage taken per poison stack (cap {CorrosiveCapValue:0%})");
        Add(lines, TwinFang, _ =>
            $"two hits at {TwinFangShareValue:0%} each; the first cannot poison");
        Add(lines, LethalDose, stacks =>
            $"poison damage x{1f + LethalDamagePerStack * stacks:0.##}; duration x{LethalDurationCost:0.##} (paid once)");
        Add(lines, AddersGrace, _ =>
            $"+{AdderBonusValue:0%} Movement Speed while a poisoned enemy lives; damage x{1f - AdderCost:0.##}");
        Add(lines, Haemorrhage, _ =>
            $"poisoned kills drop {HaemorrhageGoldValue} extra gold");
        Add(lines, LastBreath, stacks =>
            $"a poisoned kill refunds the strike's cooldown and grants +{5 * stacks}% damage for 2 s");

        Add(lines, ForkedFate, _ =>
            $"two bolts at once, {ForkJumpsValue} jumps each");
        Add(lines, Conductivity, _ =>
            $"a stunned hit spreads to {ConductivityTargetsValue} neighbours; stun x{1f - ConductivityCost:0.##}");
        Add(lines, ChainLightning, _ =>
            $"jumps reach {ChainRangeValue:0.#} m; falloff x{chain.Falloff:0.##}");
        Add(lines, Thunderhead, _ =>
            $"every {ThunderEveryValue}th cast hits the densest pack at x{ThunderDamageValue:0.##}");
        Add(lines, StaticCling, _ =>
            $"stunned enemies take +{ClingBonusValue:0%} damage");
        Add(lines, GalvanicRush, _ =>
            $"+{GalvanicBonusValue:0%} Movement Speed for {GalvanicSecondsValue:0.#} s after a chain; " +
            $"damage x{1f - GalvanicCost:0.##}");
        Add(lines, BallLightning, _ =>
            $"a {BallSecondsValue:0.#} s orb zaps {BallRadiusValue:0.#} m for x{BallShareValue:0.##} per pulse");

        return lines;
    }

    private static string TrailLifeText() => $"{RazorLifeSeconds:0.#} s";

    private static void Add(List<string> lines, string id, Func<int, string> detail)
    {
        Details[id] = detail;
        var stacks = Stacks(id);
        if (stacks <= 0)
        {
            return;
        }

        var name = RewardTable.TryGet(id, out var entry) && !string.IsNullOrEmpty(entry.Name) ? entry.Name : id;
        lines.Add($"{name} x{stacks} → {detail(stacks)}");
    }

    /// <summary>Deep Freeze: how much longer a cold freeze lasts on this skill.</summary>
    internal static float FreezeScale(string skillId) =>
        Active && string.Equals(skillId, IceNovaSkill, StringComparison.OrdinalIgnoreCase)
            ? 1f + DeepFreezePerStack * Stacks(DeepFreeze)
            : 1f;

    /// <summary>The cast-time costs, read where the cooldown is set so a single
    /// number stays in charge of when the next cast happens.</summary>
    internal static float CooldownScale(string skillId)
    {
        if (!Active || string.IsNullOrEmpty(skillId))
        {
            return 1f;
        }

        var scale = 1f;
        if (string.Equals(skillId, IceShotSkill, StringComparison.OrdinalIgnoreCase) && Stacks(Lance) > 0)
        {
            scale *= 1f + LanceCooldownCost;
        }

        if (string.Equals(skillId, FireballSkill, StringComparison.OrdinalIgnoreCase) && Stacks(Focus) > 0)
        {
            scale *= 1f + FocusCooldownCost;
        }

        if (string.Equals(skillId, IceNovaSkill, StringComparison.OrdinalIgnoreCase) && Stacks(DeepFreeze) > 0)
        {
            scale *= 1f + DeepFreezeCooldownCost;
        }

        if (string.Equals(skillId, RighteousFireSkill, StringComparison.OrdinalIgnoreCase) &&
            Stacks(SmoulderingStep) > 0)
        {
            scale *= 1f + SmoulderCooldownCost;
        }

        if (string.Equals(skillId, ArcSkill, StringComparison.OrdinalIgnoreCase) && Stacks(Thunderhead) > 0)
        {
            scale *= 1f + ThunderCooldownCost;
        }

        return scale;
    }

    /// <summary>Momentum: the spin's damage climbs with the enemies it is
    /// hitting, up to the base table's cap.</summary>
    internal static float MomentumScale(int hits)
    {
        if (!Active || Stacks(Momentum) <= 0 || hits <= 0)
        {
            return 1f;
        }

        return 1f + Mathf.Min(MomentumPerHit * Stacks(Momentum) * hits, MomentumCap * Stacks(Momentum));
    }

    /// <summary>Blood Whirl's crit penalty, read by <c>RollCrit</c>. The cost is
    /// paid once: scaling it per stack drove the chance to zero at five picks
    /// and negative beyond, which silently deleted crits.</summary>
    internal static float CritScale(string skillId) =>
        Active && string.Equals(skillId, CycloneSkill, StringComparison.OrdinalIgnoreCase) &&
        Stacks(BloodWhirl) > 0
            ? 1f - BloodWhirlCritCost
            : 1f;

    /// <summary>Frostbite: extra freeze chance after N hits on a frozen enemy.</summary>
    internal static float FrostbiteChance(int hits)
    {
        if (!Active || Stacks(Frostbite) <= 0 || hits <= 0)
        {
            return 0f;
        }

        return Mathf.Min(1f, FrostbitePerHit * Stacks(Frostbite) * hits);
    }

    /// <summary>Kindle: more damage the longer the target has been burning.</summary>
    internal static float KindleMultiplier(float seconds)
    {
        if (!Active || Stacks(Kindle) <= 0 || seconds <= 0f)
        {
            return 1f;
        }

        return 1f + Mathf.Min(KindlePerSecond * Stacks(Kindle) * seconds, KindleCapValue);
    }

    /// <summary>Corrosive Bite: a poisoned target takes more damage.</summary>
    internal static float CorrosiveMultiplier(int poisonStacks)
    {
        if (!Active || Stacks(CorrosiveBite) <= 0 || poisonStacks <= 0)
        {
            return 1f;
        }

        return 1f + Mathf.Min(CorrosivePerStack * Stacks(CorrosiveBite) * poisonStacks, CorrosiveCapValue);
    }

    private static void Announce(string skillId, ProjectilePlan plan)
    {
        var signature =
            $"{skillId}|{plan.VolleyArrows}|{plan.VolleyArc:0}|{plan.VolleyDamage:0.##}|{plan.NoPierce}|" +
            $"{plan.ExtraBolts}|{plan.ExtraBoltDamage:0.##}|{plan.DamageScale:0.##}|{plan.RadiusScale:0.##}|" +
            $"{plan.RicochetBounces}|{plan.SplitBalls}|{plan.SplitDamage:0.##}|{plan.PierceDamage:0.##}|" +
            $"{plan.CooldownScale:0.##}";

        if (string.Equals(signature, planSignature, StringComparison.Ordinal))
        {
            return;
        }

        planSignature = signature;
        Plugin.Logger.LogInfo(
            $"skill mod: {skillId} plan volley {plan.VolleyArrows} in {plan.VolleyArc:0} deg " +
            $"(x{plan.VolleyDamage:0.##}), bounce {plan.RicochetBounces}, split {plan.SplitBalls} " +
            $"(x{plan.SplitDamage:0.##}), pierce +{plan.PierceDamage:0.##} per hit, " +
            $"damage x{plan.DamageScale:0.##}, radius x{plan.RadiusScale:0.##}, " +
            $"cooldown x{plan.CooldownScale:0.##}");
    }

    private static void AnnounceAura(string skillId, AuraPlan plan, int fan, int pyre, int trail)
    {
        var signature = $"{skillId}|{fan}|{pyre}|{trail}";
        if (string.Equals(signature, auraSignature, StringComparison.Ordinal))
        {
            return;
        }

        auraSignature = signature;
        Plugin.Logger.LogInfo(
            $"skill mod: {skillId} plan damage x{plan.DamageScale:0.##}, radius x{plan.RadiusScale:0.##}, " +
            $"trail {(plan.Trail ? $"on at x{plan.TrailDamage:0.##} per stack" : "off")}, " +
            $"fan {fan} stack(s), pyre {pyre} stack(s), trail {trail} stack(s)");
    }

    private static void AnnounceNova(string skillId, NovaPlan plan, int echo, int convergence, int freeze)
    {
        var signature = $"{skillId}|{echo}|{convergence}|{freeze}";
        if (string.Equals(signature, novaSignature, StringComparison.Ordinal))
        {
            return;
        }

        novaSignature = signature;
        Plugin.Logger.LogInfo(
            $"skill mod: {skillId} plan damage x{plan.DamageScale:0.##}, radius x{plan.RadiusScale:0.##}, " +
            $"echo {plan.Echoes} at x{plan.EchoDamage:0.##}, edge +{plan.EdgeDamage:0.##} past " +
            $"{plan.EdgeStart:0.##} of the radius with a {plan.Pull:0.#} m pull, " +
            $"freeze x{FreezeScale(skillId):0.##}, cooldown x{CooldownScale(skillId):0.##}");
    }

    private static void AnnounceMelee(string skillId, int patience)
    {
        // Keyed on the stacks, not the plan: the patience bonus flips every
        // strike (a spent bonus restarts the clock), so the numbers it can
        // reach are printed once and a standing Shadow does not spam the log.
        var signature = $"{skillId}|{patience}";
        if (string.Equals(signature, meleeSignature, StringComparison.Ordinal))
        {
            return;
        }

        meleeSignature = signature;
        Plugin.Logger.LogInfo(
            $"skill mod: {skillId} plan patience {patience} stack(s): " +
            $"+{PatienceDamagePerStack * patience:0%} damage, +{PatienceStacksPerStack * patience} poison stack(s) " +
            $"after {PatienceStandstillSeconds:0.#} s still, " +
            $"bloom x{ToxicBloomShare:0.##} for {BloomSeconds:0.#} s in {BloomRadius:0.#} m, " +
            $"spread {VenomSpreadTargets} target(s) in {SpreadRadius:0.#} m");
    }

    private static void AnnounceChain(string skillId, ChainPlan plan, int field, int overcharge, int anchor)
    {
        var signature = $"{skillId}|{field}|{overcharge}|{anchor}";
        if (string.Equals(signature, chainSignature, StringComparison.Ordinal))
        {
            return;
        }

        chainSignature = signature;
        Plugin.Logger.LogInfo(
            $"skill mod: {skillId} plan jumps +{plan.ExtraJumps} -{plan.LostJumps}, " +
            $"falloff x{plan.Falloff:0.##}, " +
            $"spark {(plan.Spark ? $"on at x{plan.SparkDamage:0.##} for {plan.SparkSeconds:0.#} s in {plan.SparkRadius:0.#} m" : "off")}, " +
            $"anchor {plan.AnchorStacks} stack(s): stun {plan.AnchorStun:0.#} s, " +
            $"clap x{plan.AnchorDamage:0.##} after {plan.AnchorDelay:0.#} s in {plan.AnchorRadius:0.#} m");
    }
}
