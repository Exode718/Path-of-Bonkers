using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Wraeclast.Data;

namespace Wraeclast;

/// <summary>
/// Entry point: loads the class table, then applies every Harmony patch in this
/// assembly. Data is swapped at <c>DataManager.Load</c>, presentation at
/// <c>PlayerRenderer.SetCharacter</c> and <c>CharacterMenu.TryInit</c>; a failed
/// patch is logged and must never take the game down at boot.
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "wraeclast.conversion";
    public const string PluginName = "Wraeclast Conversion";
    public const string PluginVersion = "0.9.216";

    /// <summary>Game build this source targets (see manifest.json).</summary>
    public const string TargetGameBuild = "2023.2.22f1";

    internal static ManualLogSource Logger = null!;
    internal static ConfigEntry<bool> VerboseLogging = null!;
    internal static ConfigEntry<float> ModelYaw = null!;
    internal static ConfigEntry<bool> GpuSkinning = null!;
    internal static ConfigEntry<string> CpuSkinCreatures = null!;
    internal static ConfigEntry<bool> BodyOutline = null!;
    internal static ConfigEntry<int> OutlineMode = null!;
    internal static ConfigEntry<float> OutlineSize = null!;
    internal static ConfigEntry<bool> BodyAlphaClipping = null!;
    internal static ConfigEntry<bool> ShowWeapons = null!;
    internal static ConfigEntry<float> WeaponScale = null!;
    internal static ConfigEntry<bool> SkillsEnabled = null!;
    internal static ConfigEntry<bool> SkillModsEnabled = null!;
    internal static ConfigEntry<int> SkillUpgradeEvery = null!;

    /// <summary>V11: the burn / freeze / poison / stun badges above an enemy.</summary>
    internal static ConfigEntry<bool> EnemyStatusIcons = null!;

    /// <summary>The blue ground circle that shows the skill's reach.</summary>
    internal static ConfigEntry<bool> RangeIndicator = null!;

    /// <summary>SND (interim): play a host clip on each cast.</summary>
    internal static ConfigEntry<bool> SkillSounds = null!;
    internal static ConfigEntry<bool> DisableBaseWeapons = null!;
    internal static ConfigEntry<bool> ReplaceEnemyModels = null!;
    internal static ConfigEntry<int> MaxLiveModels = null!;
    internal static ConfigEntry<int> MaxAliveEnemies = null!;
    internal static ConfigEntry<bool> ReplaceBossSkills = null!;
    internal static ConfigEntry<float> BossHeight = null!;
    internal static ConfigEntry<float> BossScale = null!;
    internal static ConfigEntry<bool> ReplaceTomes = null!;
    internal static ConfigEntry<bool> ReplaceItems = null!;
    internal static ConfigEntry<bool> ReplacePools = null!;
    internal static ConfigEntry<bool> UseRewardTable = null!;
    internal static ConfigEntry<bool> LockWeapons = null!;
    internal static ConfigEntry<bool> BossWindowEnabled = null!;
    internal static ConfigEntry<float> BossWindowSeconds = null!;
    internal static ConfigEntry<float> FloorSeconds = null!;
    internal static ConfigEntry<bool> AutoNextFloor = null!;
    internal static ConfigEntry<bool> GoldOnlyCurrency = null!;
    internal static ConfigEntry<bool> ForestOnlyMaps = null!;
    internal static ConfigEntry<bool> ReplaceTerrain = null!;

    /// <summary>A2: the stage's random scenery (trees, rocks, walls) wears the
    /// client's own prop meshes over Megabonk's own colliders.</summary>
    internal static ConfigEntry<bool> ReplaceProps = null!;

    /// <summary>A3a: the map's chests wear the client's own chest meshes (the
    /// paid chest the town chest, the free gold chest a distinct golden one).</summary>
    internal static ConfigEntry<bool> ReplaceChests = null!;

    /// <summary>A3b: the charge and greed shrines wear the client's Azmeri shrine
    /// totem; the blessing, the charge zone and the icons stay Megabonk's.</summary>
    internal static ConfigEntry<bool> ReplaceShrines = null!;

    /// <summary>A5.1: the shady-guy merchant wears a PoE town NPC; the shop,
    /// icons, smoke and interaction stay Megabonk's.</summary>
    internal static ConfigEntry<bool> ReplaceMerchant = null!;

    /// <summary>A5.2: the microwave and the cursed/challenge/magnet shrines wear
    /// client art; their icons, effects, zones and interaction stay Megabonk's.</summary>
    internal static ConfigEntry<bool> ReplaceElements = null!;

    /// <summary>A2: global size multiplier on the fitted props, for tuning.</summary>
    internal static ConfigEntry<float> PropScale = null!;

    /// <summary>A2c: the per-floor survey that names where the scenery really
    /// comes from, so the swap can be mounted on the right method.</summary>
    internal static ConfigEntry<bool> PropProbe = null!;

    /// <summary>A2e: keep the placer's tilt onto the terrain normal instead of
    /// standing every prop upright with only its yaw.</summary>
    internal static ConfigEntry<bool> PropAlignToGround = null!;

    /// <summary>A2g: how far every prop is buried into the ground, in metres.</summary>
    internal static ConfigEntry<float> PropSink = null!;

    /// <summary>A2o: tallest a swapped prop mesh may be drawn, in metres (0 = no
    /// cap). Retextured families keep Megabonk's own size.</summary>
    internal static ConfigEntry<float> PropMaxHeight = null!;

    /// <summary>A2g: re-shape the native collider to the mesh that is drawn, so
    /// the hitbox matches the visual.</summary>
    internal static ConfigEntry<bool> FitPropColliders = null!;
    internal static ConfigEntry<bool> CleanMainMenu = null!;
    internal static ConfigEntry<bool> PoeMenuArt = null!;
    internal static ConfigEntry<bool> DeathScreenSource = null!;
    internal static ConfigEntry<bool> DeathScreenName = null!;
    internal static ConfigEntry<bool> MenuProbeKeys = null!;
    internal static ConfigEntry<bool> SpawnBossKeys = null!;
    internal static ConfigEntry<bool> RunTestKeys = null!;
    internal static ConfigEntry<bool> GalleryKeys = null!;
    internal static ConfigEntry<bool> LogBossCasts = null!;
    internal static ConfigEntry<bool> LogFrameTime = null!;

    public override void Load()
    {
        Logger = base.Log;

        VerboseLogging = Config.Bind(
            "Debug",
            "VerboseLogging",
            false,
            "Log each data id once (character, item). Off by default: the data load already logs the counts and the roster.");

        ModelYaw = Config.Bind(
            "Presentation",
            "ModelYaw",
            180f,
            "Yaw in degrees applied to the PoE model inside the player root. The exported GLB faces -Z, so 180 makes it face Unity forward (+Z).");

        GpuSkinning = Config.Bind(
            "Presentation",
            "GpuSkinning",
            true,
            "Let Unity skin the PoE bodies on the GPU (SkinnedMeshRenderer with the GLB bone weights) instead of recomputing and uploading every vertex from C#. Fallback: the CPU path skins at 20 Hz and costs far more.");

        CpuSkinCreatures = Config.Bind(
            "Presentation",
            "CpuSkinCreatures",
            "",
            "Diagnostic switch (E1), empty since 0.9.35: creature ids (comma separated, e.g. 'elder' or 'elder,shaper') forced onto the CPU skin path instead of the GPU one. It exists to rule the SkinnedMeshRenderer path in or out for one creature, and it costs a full vertex/normal pass plus a mesh upload per skin tick (the Elder is 15 293 verts, 225 bones), so a creature listed here is the first suspect for a frame drop once it comes into view. Empty keeps GPU skinning for every creature; the Elder's own fault (a duplicate zero-scale root track in its clips) is fixed in the asset, not the skin path.");

        BodyOutline = Config.Bind(
            "Presentation",
            "BodyOutline",
            false,
            "Unused since 0.9.0: the outline is controlled by OutlineMode/OutlineSize below. Kept so an existing config file still loads.");

        OutlineMode = Config.Bind(
            "Presentation",
            "OutlineMode",
            -1,
            "MK Toon _Outline value written on every PoE body and weapon: -1 leaves the host material's value (validated look; the shader's outline pass is not disturbed), 0 asks for no outline, 3 is the host's hull-with-clip mode. Changing the mode at 0 can leave the black hull pass coincident with the body (patchy black), so try size 0 with mode 3 first.");

        OutlineSize = Config.Bind(
            "Presentation",
            "OutlineSize",
            -1f,
            "MK Toon _OutlineSize written on every PoE body and weapon: -1 leaves the host value (30-50), 0 removes the hull width but can z-fight the body black, a small negative value pushes the hull inside the mesh.");

        BodyAlphaClipping = Config.Bind(
            "Presentation",
            "BodyAlphaClipping",
            true,
            "Alpha-test the body atlas at 0.35 (the alphaMode MASK / alphaCutoff every exported body GLB declares) through the material's _AlphaClipping/_AlphaCutoff values, so hair and cloth cards are cut out instead of drawn as their black texels. The keyword is left as the host material has it (this interop's keyword getters throw).");

        ShowWeapons = Config.Bind(
            "Presentation",
            "ShowWeapons",
            true,
            "Put the class starter weapon (and offhand) from classes.json in the PoE hand bones, replacing the Megabonk handheld meshes the body swap hides.");

        WeaponScale = Config.Bind(
            "Presentation",
            "WeaponScale",
            1f,
            "Size multiplier for every held weapon on top of the client proportions (1 = the exported mesh size, 0.6 = stylised smaller weapons). Applied at spawn, so a run restart picks it up.");

        SkillsEnabled = Config.Bind(
            "Skills",
            "Enabled",
            true,
            "Auto-cast the class starter skill (skills.json) at the nearest enemy while a run is active.");

        SkillModsEnabled = Config.Bind(
            "Skills",
            "SkillMods",
            true,
            "R6: skill mods. Every skill has ten upgrades; the class skill's level is the number of upgrade picks taken (40 max, any distribution). Each pick also raises the base skill by +4% damage and −0.6% cooldown. R11 offers the upgrades as the class skill's three-card draft at level 1 and then every SkillUpgradeEvery levels. Each mod changes how the skill behaves and states its cost on the card; every stack counts toward the 40 cap. Off leaves the draft to tomes and relics only.");

        SkillUpgradeEvery = Config.Bind(
            "Skills",
            "SkillUpgradeEvery",
            5,
            "R11: the level-up draft is the class skill's three upgrade cards at character level 1 and then every N levels (default 5: 1, 5, 10, 15, …), until the skill has 40 upgrades. Every other level stays a tome draft. Set 0 to turn the cadence off (the skill's mods then only come from the pool's other sources).");

        EnemyStatusIcons = Config.Bind(
            "Skills",
            "EnemyStatusIcons",
            true,
            "V11: draw a small camera-facing badge above every enemy that is burning (orange orb), frozen (blue ring), poisoned (green orb) or stunned (yellow ring). The native debuffs exist but have no visual. The debuff check runs at 5 Hz and at most 48 enemies are badged at once; the class skill's tooltip also states its damage type.");

        RangeIndicator = Config.Bind(
            "Skills",
            "RangeIndicator",
            true,
            "Draw a translucent blue circle on the floor at the class skill's effective reach: the burst/swing radius for a melee or nova, the spin radius for the Duelist's Cyclone, and the range at which a projectile, chain or ground skill locks a target. Righteous Fire's aura and the Scion's orbiting blades already draw their own area, so they get no second circle. The ring follows the player and resizes when tomes or upgrades change the radius. Also available in the game's Settings window, Other tab (Range Indicator).");

        SkillSounds = Config.Bind(
            "Skills",
            "SkillSounds",
            true,
            "SND (interim): play a sound on each class skill cast. Custom clips cannot be built in this IL2CPP interop (every AudioClip creation path is broken or crashes), so this plays a fitting clip from Megabonk's own RandomSfx pools, chosen by name per skill. Off silences the layer.");

        DisableBaseWeapons = Config.Bind(
            "Skills",
            "DisableBaseWeapons",
            true,
            "Turn Megabonk's starting weapon off for our classes so damage comes from our skills.");

        ReplaceEnemyModels = Config.Bind(
            "Enemies",
            "ReplaceModels",
            true,
            "Swap Megabonk enemy meshes for the PoE creature models (beasts, skeletons, zombies, ghosts, goatman, stonegolem, fireelemental, spiders). AI, colliders, damage and loot stay native.");

        MaxLiveModels = Config.Bind(
            "Enemies",
            "MaxLiveModels",
            64,
            "Number of enemy bodies that keep the full clip rate (20 Hz near, 8 Hz far) and cast shadows. Every enemy still draws its PoE model and texture at every distance; beyond this count the bodies drop to 2 Hz and stop casting shadows until they are close again (a swarm of hundreds otherwise pays a 40-bone sample per body per frame plus a shadow pass). Bosses and dissolving corpses always stay full-rate. 0 = every body full-rate (the old behaviour).");

        MaxAliveEnemies = Config.Bind(
            "Enemies",
            "MaxAliveEnemies",
            250,
            "Soft cap on simultaneously live enemies. New non-boss spawns are refused at the cap and accepted again as soon as an enemy is released to the pool; bosses always spawn. A player who cannot clear a wave otherwise lets it pile up until the host's AI, physics and draw calls dominate the frame, which is the swarm frame drop. 0 = no cap.");

        ReplaceBossSkills = Config.Bind(
            "Enemies",
            "BossSkills",
            true,
            "Give the twelve boss-tier enemies the Atlas boss attack kit (Elder/Shaper/Maven) built from Megabonk's own special attacks. Stats, HP, speed and melee stay native.");

        BossHeight = Config.Bind(
            "Enemies",
            "BossHeight",
            13.42f,
            "S6: the fixed world height every boss-tier spawn is fitted to (minibosses and floor bosses alike), so a boss reads the same size whatever Megabonk slot it replaces. 13.42 is the measured Maven (about 2.7x the player). Normal creatures keep the native renderer height, so only the model changes, never the size. 0 falls back to the native height.");

        BossScale = Config.Bind(
            "Enemies",
            "BossScale",
            1.25f,
            "S2c: a multiplier on BossHeight, so every boss-tier spawn reads a touch larger than the measured Maven (default 1.25 = 16.78 units). Applies to floor bosses and minibosses alike; 1 keeps the old size.");

        ReplaceTomes = Config.Bind(
            "Items",
            "ReplaceTomes",
            true,
            "Put the Path of Exile tome names and card text on the game's own ETome slots (mod/data/tomes.json). Each slot keeps its native statModifier, so the level-up draft, the shop and the chests apply the effect the card states. Off keeps Megabonk's tomes.");

        ReplaceItems = Config.Bind(
            "Items",
            "ReplaceItems",
            true,
            "Put the Path of Exile relic names and card text on the mapped EItem slots (mod/data/items.json). A relic with the sheet's own stats/triggers is engine-owned: the donor class is replaced with no implementation and RelicEngine applies the sheet through the game's stat pipeline and its hit/kill events. The rest keep the donor class's own effect, so their card states the donor's numbers. Off keeps Megabonk's item cards and effects.");

        ReplacePools = Config.Bind(
            "Items",
            "ReplacePools",
            true,
            "Our pools only: every native EItem row leaves the run's item pool (inItemPool = false) and only the audited PoE relic slots stay in it, the four code-side tomes (Balance/Chaos/Gambler/Hoarder) enter the tome pool, and every item draw is logged and guarded. Off keeps Megabonk's item and tome pools untouched.");

        UseRewardTable = Config.Bind(
            "Items",
            "RewardTable",
            true,
            "R5: one reward table (mod/data/rewards.json) feeds the level-up draft, chests, the merchant and the boss reward. Every card carries a tier (depth gate), a rarity (weight share, card frame), a weight (scaled by luck), a level floor and a stack cap. Off keeps the game's own draws (the R3 guards still apply).");

        LockWeapons = Config.Bind(
            "Items",
            "LockWeapons",
            true,
            "One weapon per class: the level-up draft never offers a weapon unlock or weapon level, the weapon inventory refuses every native weapon (the class starter is a PoE mesh on the hand bones), and the weapon slot plus the shop's weapon-slot card are hidden. Off restores Megabonk's weapons.");

        SpawnBossKeys = Config.Bind(
            "Debug",
            "SpawnBossKeys",
            false,
            "F5/F6/F7 spawn the Elder, Shaper and Maven in front of the player (the live boss-run check). Off by default.");

        RunTestKeys = Config.Bind(
            "Debug",
            "RunTestKeys",
            false,
            "Live check: F1 toggles invincible + no clip, F2 spawns the Elder/Shaper/Maven trio frozen (same size, no attacks), F3 grants the Quantity tome at Rare and Legendary (S3), F9 jumps the stage clock to 6:00 remaining (the first swarm wave), F10 finishes the bosses the boss window tracks, F11 loads the final boss stage directly, F8 probes the tracked bosses, F12 pauses one-way for a screenshot. Off by default.");

        GalleryKeys = Config.Bind(
            "Debug",
            "GalleryKeys",
            false,
            "Model gallery: F1 spawns every enemy in two rows in front of the player, frozen and facing the player, with the player invincible; F2 jumps the stage clock to the gate (also automatic 30 s after the build). Off by default.");

        BossWindowEnabled = Config.Bind(
            "Run",
            "BossWindow",
            true,
            "Floors 1-2 are a race: at the 10:00 beat the floor boss is summoned, one ghost wave runs, a 60 s countdown is drawn under the stage timer, the run ends if the boss is alive at the deadline, and the floor portal is taken at the deadline (not on the kill, so a boss killed early leaves the rest of the floor to play; an altar boss summoned before the beat consumes the beat: no second boss, no ghost wave). Floor 3 has no floor boss: the beat jumps straight into the final boss stage, and the final boss kill reloads stage 1 on the same build (endless). Off keeps Megabonk's stage flow (walk to the altar, no deadline).");

        BossWindowSeconds = Config.Bind(
            "Run",
            "BossWindowSeconds",
            60f,
            "Length of the boss window in seconds. The deadline is absolute on the stage clock (beat + this): the portal is taken there, and a boss still alive ends the run with the normal death screen. A boss killed earlier does not shorten the floor. Floor 3 has no window: the final boss is a marathon, not a race.");

        FloorSeconds = Config.Bind(
            "Run",
            "FloorSeconds",
            0f,
            "Length of a floor in seconds: waves until this beat, then the floor boss and the final wave (floors 1-2) or the final arena (floor 3). 0 (default) uses Megabonk's own per-stage timelines: 600 / 540 / 480 s for floors 1 / 2 / 3. A value above 0 forces the same length on every floor.");

        AutoNextFloor = Config.Bind(
            "Run",
            "AutoNextFloor",
            true,
            "Take the floor portal by ourselves at the deadline, so the floor advances there instead of waiting for the player to walk into the portal (a boss killed early still leaves the rest of the floor to play). Also owns floor 3 (straight to the final boss) and the endless reload after the final boss kill. Off leaves the portal to the player and the final portal ends the run.");

        GoldOnlyCurrency = Config.Bind(
            "Run",
            "GoldOnly",
            true,
            "One currency: gold, and only gold. Silver drops (enemies, tumbleweeds, pots), the run credit, achievement payouts and the saved balance are cut, the Sanctum shop cannot buy or refund, its saved levels are cleared and its UI (menu button, tab, counters) is hidden. Gold still drops and chests still cost gold. Off restores Megabonk's silver and Sanctum.");

        ForestOnlyMaps = Config.Bind(
            "Run",
            "ForestOnly",
            true,
            "M1: Forest is the only map. Desert and Graveyard are closed through the same MapData.isEnabled flag that hides Hell and Dungeon, the map window hides the closed rows and cannot select them, and a run config that names a closed map is rewritten to Forest before the stage loads. Off leaves Megabonk's five maps as they are.");

        ReplaceTerrain = Config.Bind(
            "Terrain",
            "ReplaceTerrain",
            true,
            "A1: the Forest ground and terrain edges wear the client's own art. The tile tops (StageData.flatMaterials), the four fill/side slots and the triplanar edge material get a PoE forest floor, cliff and soil albedo; Megabonk's shader, tiling, noise and colours stay, so the lighting and fog of the host are unchanged. Forest only (it is the only map since M1). Off keeps Megabonk's ground textures.");

        ReplaceProps = Config.Bind(
            "Props",
            "ReplaceProps",
            true,
            "A2: the stage's random scenery wears the client's own prop meshes. Every object RandomObjectPlacer places (the trees, rocks, walls and doodads of StageData.randomMapObjects) draws a PoE prop instead of Megabonk's model: a child mesh fitted inside the native renderer's own box, around the same pivot. The collider, its size and the spawn rule are untouched — only what is drawn changes. Off keeps Megabonk's scenery.");

        ReplaceChests = Config.Bind(
            "Props",
            "ReplaceChests",
            true,
            "A3a: the map's chests wear the client's own chest meshes. Every chest SpawnInteractables places draws a PoE chest instead of Megabonk's cube: the paid chest the town chest (Lioneye's Watch's stash, wood and metal), the free (gold) chest a distinct golden one, a child mesh fitted inside the native renderer's own box and stood upright on the terrain. The pivot, the collider and the spawn rule are untouched, and the corrupt and ghost chests keep Megabonk's model until their own exports land. Off keeps Megabonk's chests.");

        ReplaceShrines = Config.Bind(
            "Props",
            "ReplaceShrines",
            true,
            "A3b/A3c: the charge and greed shrines wear the client's own Azmeri shrine totem and the moai wears a client marble statue. The body is the only thing replaced: the charge zone, the blessing, the healing zone, the minimap/alert icons, the interaction and the particles stay Megabonk's, and each model is fitted inside the body's own box. The moai's solid collider follows the drawn statue. Off keeps Megabonk's shrine models.");

        ReplaceMerchant = Config.Bind(
            "Props",
            "ReplaceMerchant",
            true,
            "A5.1: the shady-guy merchant wears a PoE town NPC (Tarkleigh, a standing trader with one idle clip). Only the body renderer changes: the model is fitted inside the merchant's own body box, the native skinned body is hidden, and the shop, items, prices, rarity, smoke, minimap/alert icons and interaction stay Megabonk's. Off keeps Megabonk's shady guy.");

        ReplaceElements = Config.Bind(
            "Props",
            "ReplaceElements",
            true,
            "A5.2: the microwave and the cursed, challenge and magnet shrines wear client art — the Expedition plinth, the Vaal soul vessel, the Labyrinth's Izaro pedestal and the Act 10 Kitava horn arch. Only the body renderer changes: each model is fitted inside its own body box (never the 20 m encounter/charge zone), and the icons, minimap/alert markers, effects, interaction and colliders stay Megabonk's. Off keeps Megabonk's element models.");

        PropScale = Config.Bind(
            "Props",
            "PropScale",
            1f,
            "A2: global size multiplier on the fitted props. The fit already keeps a PoE prop inside its Megabonk counterpart's box; this scales it about the native pivot on top of that, for a look the operator prefers (1 = the fit as measured).");

        PropAlignToGround = Config.Bind(
            "Props",
            "PropAlignToGround",
            false,
            "A2e: the placer tilts every scenery object onto the terrain normal. Megabonk's own low-poly props hide that; a mossy PoE tree does not and leans on a slope. Off keeps the placer's random yaw and stands the prop upright; on restores the full native rotation.");

        PropSink = Config.Bind(
            "Props",
            "PropSink",
            0.05f,
            "A2g: how far every scenery prop is buried into the ground, in metres. A client prop's base plate is not exactly flat, so a few centimetres stop the 'floating' read: 0 places the mesh on the floor of its box, 0.05 sinks it 5 cm.");

        PropMaxHeight = Config.Bind(
            "Props",
            "PropMaxHeight",
            0f,
            "A2o/A2q: optional ceiling for a swapped prop mesh, in metres (0 = no cap, the default). 0 keeps the floor's variety: a prop is fitted to its own native box, so a small tree stays small and the landmark `Tree` (52 m) stays the giant it is. A positive value flattens every prop taller than it, which is why it is off by default. Retextured families (rocks, ruins, castles, crates, bushes, logs) are Megabonk's own models and are never capped.");

        FitPropColliders = Config.Bind(
            "Props",
            "FitPropColliders",
            true,
            "A2g: re-shape the native collider to the mesh that is drawn (a box takes the mesh footprint, a capsule its half-width radius, a sphere its horizontal bounding radius). Megabonk's own collider is sized for its own prop — a 7.4 m capsule around a 4.2 m tree, a 12 m sphere around a 21 m rock — so without this the player hits air beside the tree or walks into the rock. Off keeps Megabonk's collider untouched.");

        PropProbe = Config.Bind(
            "Props",
            "PropProbe",
            true,
            "A2c/A5.0 discovery: once per floor, about 3 s after the stage starts, the log surveys the scenery — the RandomObjectPlacer found in the scene, its randomObjects rows (amount, scale, prefab names), its charge/greed shrine rows, its child count, then every scene object named like a prop (tree/rock/log/bush/box/shrine/moai/pot/urn) grouped by name with its renderer count, bounds, parent path and what the swap rules would do with it. A5.0 adds the map-element register: once a second it names every BaseInteractable kind the floor spawns (the shady-guy merchant, the microwave, the cursed and challenge shrines, the chests, shrines, moai and pots) the first time it appears, with its body renderer, renderer count, bounds and parent chain — so an element that walks in mid-floor is still listed. Off silences the survey.");

        CleanMainMenu = Config.Bind(
            "UI",
            "CleanMenu",
            true,
            "Strip the main menu down to Play, Characters, Maps and Settings: the Unlocks and Quests buttons and tabs, the quick-quest progress cards, the right-hand leaderboard, the credits panel and the Discord button are hidden, and their screens cannot be opened. Off leaves Megabonk's menu as it is.");

        PoeMenuArt = Config.Bind(
            "UI",
            "PoeMenuArt",
            true,
            "V6: the client's Path of Exile logo replaces Megabonk's main menu title. Nothing else on the menu changes — buttons, tabs and backgrounds stay the game's own. Off keeps the native logo.");

        DeathScreenSource = Config.Bind(
            "UI",
            "DeathScreenSource",
            true,
            "V7: a death-screen damage-source row the game cannot localize names the source. Contact and projectile damage are recorded as \"Enemy\" and the client's DamageSources table has no such key, so the row reads \"Missing localization for DamageSources.Enemy\"; with this on it names the creature that landed the killing blow (our Atlas boss name for a roster spawn, the game's enemy name otherwise), and any other unknown key reads as itself. Off leaves the game's fallback text.");

        DeathScreenName = Config.Bind(
            "UI",
            "DeathScreenName",
            true,
            "V8: the death screen names the run's character from the Megabonk slot's internal name (\"cl4nk\"). With this on the label states the class that ran (Witch, Marauder, …) from our class table. Off keeps the native slot name.");

        MenuProbeKeys = Config.Bind(
            "Debug",
            "MenuProbeKeys",
            false,
            "V6 discovery: F4 dumps the main menu hierarchy (path, rect, Image sprite/type, RawImage texture, TMP text) to the log. Off by default; the art pass itself logs the nodes it swaps.");

        LogBossCasts = Config.Bind(
            "Debug",
            "LogBossCasts",
            true,
            "Log the name of every enemy special attack the game starts, so the Atlas boss kits can be read from the log.");

        LogFrameTime = Config.Bind(
            "Debug",
            "LogFrameTime",
            true,
            "Log frame time, live enemy count and the mod's clip-evaluation cost every 5 s during a run.");

        Log.LogInfo($"{PluginName} {PluginVersion} (target game {TargetGameBuild})");

        ClassDatabase.Load(ModPaths.Classes);
        SkillDatabase.Load(ModPaths.Skills);
        TomeDatabase.Load(ModPaths.Tomes);
        ItemDatabase.Load(ModPaths.Items);
        RewardTable.Load(ModPaths.Rewards);

        try
        {
            var harmony = new Harmony(PluginGuid);
            var patchedClasses = 0;
            var failedClasses = 0;

            // One processor per patch class, each in its own try: a single bad
            // hook (a parameter renamed by a game update, a method that moved)
            // costs that class only. `CreateAndPatchAll` on the whole assembly
            // throws out of the first failure and leaves every other patch
            // unapplied, which is how one wrong name disabled the entire mod.
            foreach (var type in typeof(Plugin).Assembly.GetTypes())
            {
                if (!IsPatchClass(type))
                {
                    continue;
                }

                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    patchedClasses++;
                }
                catch (Exception error)
                {
                    failedClasses++;
                    Log.LogError($"Harmony: {type.Name} failed to patch ({error.Message.Trim()})");
                }
            }

            var patched = harmony.GetPatchedMethods().ToArray();
            Log.LogInfo(
                $"Harmony: {patched.Length} method(s) patched from {patchedClasses} class(es)" +
                (failedClasses > 0 ? $", {failedClasses} class(es) failed" : ""));

            foreach (var method in patched)
            {
                Log.LogInfo($"  patched {method.DeclaringType?.Name}.{method.Name}");
            }
        }
        catch (Exception error)
        {
            // A failed patch must never take the game down at boot.
            Log.LogError($"Harmony setup failed: {error}");
        }

        // A pass reachable from a single hook must say it is mounted: a floor
        // where that hook never fires otherwise looks like a working feature.
        PropSwap.Mount();
        ChestSwap.Mount();
        ShrineSwap.Mount();
        MerchantSwap.Mount();
        ElementSwap.Mount();

        // Own Harmony id: a missing private target must not cost the main patch set.
        Dev.BossRun.Install();
    }

    /// <summary>A type is a patch class when it (or a method on it) carries any
    /// Harmony attribute; the class-level <c>[HarmonyPatch]</c> alone would miss
    /// classes that only annotate their methods.</summary>
    private static bool IsPatchClass(Type type)
    {
        const System.Reflection.BindingFlags All =
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.DeclaredOnly;

        if (type.GetCustomAttributes(typeof(HarmonyAttribute), true).Length > 0)
        {
            return true;
        }

        foreach (var method in type.GetMethods(All))
        {
            if (method.GetCustomAttributes(typeof(HarmonyAttribute), true).Length > 0)
            {
                return true;
            }
        }

        return false;
    }
}


