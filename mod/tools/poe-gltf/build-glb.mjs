/**
 * Convert parsed PoE models (SMD + AST + texture) to glTF 2.0 GLB.
 *
 * The web spike parses the client's binary formats into JSON; this script takes
 * those JSON files and builds a standard skinned mesh + animations that Godot
 * can import. Output goes to mod/art/game/models.
 *
 *   node mod/tools/poe-gltf/build-glb.mjs
 *   node mod/tools/poe-gltf/build-glb.mjs Witch skeletons
 */
import { readFileSync, mkdirSync, writeFileSync, existsSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { deflateSync, inflateSync } from "node:zlib";
import { mat4, quat, vec3 } from "gl-matrix";

const root = path.resolve(fileURLToPath(new URL("../..", import.meta.url)));
const raw = path.join(root, "art", "raw");
const outDir = path.join(root, "art", "game", "models");
const weaponOutDir = path.join(root, "art", "game", "weapons");
const effectOutDir = path.join(root, "art", "game", "effects");

/** Client weapon meshes: skinned SM/SMD rigs or static FMT models. */
const WEAPONS = {
  "nebulis": { kind: "sm", sm: "sceptreofmemories.sm.json", texture: "colour.png", normal: "normal.png" },
  "aegis-aurora": { kind: "sm", sm: "shieldaegisaurora.sm.json", texture: "colour.png", normal: "normal.png" },
  "windripper": { kind: "sm", sm: "windripper.sm.json", texture: "colour.png", normal: "normal.png" },
  "pledge-of-hands": { kind: "fmt", fmt: "pledgeofhandstaff.fmt.json", texture: "colour.png" },
  "void-battery": { kind: "fmt", fmt: "voidbattery.fmt.json", texture: "colour.png", normal: "normal.png" },
  "binos-kitchen-knife": { kind: "fmt", fmt: "binoalt.fmt.json", texture: "colour.png", normal: "normal.png" },
  "starforge": { kind: "fmt", fmt: "starforgededge.fmt.json", texture: "colour.png", normal: "normal.png" },
  "the-dark-seer": { kind: "fmt", fmt: "onehandmacedemonclub.fmt.json", texture: "colour.png", normal: "normal.png" },
};

/**
 * Client effect meshes that replace our procedural skill billboards, exported
 * with `ggpk-explorer export <path> --steam ../PoE-Bundles2 --poe1 --parsed json
 * --textures png -o mod/art/raw/effects`:
 *
 *   fireball   the Fireball projectile core (the same rig the spell's `rig.ao`
 *              drives; the small ball shape, not the 60 cm shockwave flares)
 *   ice-shot   Ice Shot's arrow, the client's `arrow_ice` static model
 *   arc        Arc's lightning ribbon, three crossed quads the bolt texture is
 *              drawn across (the whole mesh is one material)
 *
 * `material` picks the shape group that shares one material; every entry keeps
 * that material's own colour texture. The assets are additive in game, so the
 * textures' black surroundings vanish.
 */
const EFFECTS = {
  fireball: {
    kind: "sm",
    dir: "art/models/effects/fireball",
    sm: "fireball.sm.json",
    material: "Art/Textures/Environment/lavacns.mat",
  },
  "ice-shot": {
    kind: "fmt",
    dir: "art/models/effects/base_arrow_attack",
    fmt: "arrow_ice.fmt.json",
    material: "Art/particles/base_arrow_attack/icearrow_clrc.mat",
  },
  arc: {
    kind: "sm",
    dir: "art/models/effects/arc_02",
    sm: "arc.sm.json",
    material: "Art/particles/arc_02/lightning_addc.mat",
  },
  // R11b: Ball Lightning's orb. The client's ball is four layered shells
  // (`insidec`, the tiled lightning shell, the scrolling crackle and the sharp
  // edge); the tiled shell alone carries the read, so the effect keeps one
  // material and the runtime's single-texture additive path. The same model is
  // shared by the Breach lightning ball (`lightning_tiled` map).
  "ball-lightning": {
    kind: "sm",
    dir: "art/models/effects/storm_burst",
    sm: "balllightning.sm.json",
    material: "Art/particles/monster_particles/Breach/Lightning/ball/lightning_tiledc.mat",
  },
  // R11b item 1: Meteor Call's falling body. The client's `meteor_new.ao`
  // (EnchantmentMeteor, "drops a meteor from above") drives `meteornew.sm` on
  // its own rig — a rock core wrapped in fire/lava shells and a bright flame
  // cloak. The runtime's effect path is one material per model and additive, so
  // the meteor is drawn as two of them: `meteor` is the glowing head shell
  // (`light_immortal_call`, a 1.4 × 1.5 m orange bowl) and `meteor-rock` is the
  // dark rock chunk that gives it its identity. The flat `goatmeteor` shapes
  // are ground markers, not the falling body.
  "meteor": {
    kind: "sm",
    dir: "art/models/effects/enchantment_skills/meteor/meteor/new",
    sm: "meteornew.sm.json",
    material: "Art/particles/enchantment_skills/meteor/light_immortal_callc.mat",
  },
  "meteor-rock": {
    kind: "sm",
    dir: "art/models/effects/enchantment_skills/meteor/meteor/new",
    sm: "meteornew.sm.json",
    material: "Art/particles/enchantment_skills/meteor/Rockscns.mat",
  },
};
const effectRawDir = path.join(raw, "effects");

/**
 * Client terrain props: static FMT clusters, exported with
 * `ggpk-explorer export <path> --steam ../PoE-Bundles2 --poe1 --parsed json
 *  --textures png -o mod/art/raw/props`. Each entry points at the .fmt.json and
 * the .mat.json / png beside it are resolved from the material names inside.
 */
const PROPS = {
  "ruin-wall": { fmt: "art/models/terrain/forest/ruins/fruinswall04.fmt.json" },
  "forest-statue": { fmt: "art/models/terrain/doodads/forest/ankor_statue3.fmt.json" },
  "vaal-pillar": { fmt: "art/models/terrain/doodads/vaal/vaalpillar01.fmt.json" },
  "kaom-rock": { fmt: "art/models/terrain/doodads/act4/kaomsarea/kaomrock01.fmt.json" },
  "farudin-torch": { fmt: "art/models/terrain/desert/doodads/dreadnought/farudintorchpost01.fmt.json" },
  "dreadnought-hull": { fmt: "art/models/terrain/desert/doodads/dreadnought/gazebowallnarrow01_01.fmt.json" },
  // A3a: the two chests the operator named, both client `.sm` models exported
  // with `ggpk-explorer export Art/Models/Chests/Chests/<name>.sm --steam
  // <Bundles2> --poe1 --parsed json --textures png` plus their `.smd` rig and
  // their materials' `*_colour.png`. `chest31` is the normal chest, `chest7old`
  // the golden one; `textures` is one colour map per client material, in the
  // `.sm` material order (see `buildSmProp`).
  "chest-normal": {
    kind: "sm",
    dir: "art/models/chests/chests",
    sm: "chest31.sm.json",
    textures: ["art/textures/environment/interactables/chests/chest31_colour.png"],
  },
  // The same chest, Y-up (`yUp`), for the opening shot: `ChestOpening` feeds
  // its own `SkinnedMeshRenderer` a mesh and a material, so the mesh has to
  // arrive already standing and with no root rotation for the renderer to
  // fight. The world chest keeps the Rx(+90) root the props use. The golden
  // chests are this same model **tinted** (0.9.100), not a second mesh.
  "chest-normal-open": {
    kind: "sm",
    yUp: true,
    dir: "art/models/chests/chests",
    sm: "chest31.sm.json",
    textures: ["art/textures/environment/interactables/chests/chest31_colour.png"],
  },
  // The Lioneye's Watch stash, kept built (it is the town chest) even though
  // no rule maps it now. Its texture coordinates are in the vertex's packed
  // field, so it needs `uv: "unk16"`.
  "poe-stash": { fmt: "art/models/terrain/foresttown/stash_chest.fmt.json", uv: "unk16" },
  // Shrine of Fury: PoE's Azmeri bear-head shrine (the map shrine model), a
  // single textured totem. Scaled to Megabonk's shrine height in `_build_shrine`.
  "poe-shrine-totem": { fmt: "art/models/terrain/forest/feature/forest_azmeri_shrine_clickable_bearhead.fmt.json" },
  // A3b greed shrine: the Forest bandit camp's **stick-skull** — a skull
  // impaled on a palisade stake, the "crâne au bout d'un bâton" the operator
  // asked for. Two client materials (Skullc, Palisadec). The charge shrine keeps
  // `poe-shrine-totem`, and a golden charge shrine keeps the gold tint.
  "greed-stickskull": { fmt: "art/models/terrain/forest/trees/treewitch_stickskull_v01_07.fmt.json" },
  // A3c moai: the client's Act 3 battlefield marble statue, a standing figure
  // on a plinth. It is the "monument" the moai needs: the 2.46 m model fits the
  // moai's own 8.44 m collider almost exactly on the height axis, so the swap
  // reads at Megabonk's native size instead of shrinking to an altar. The
  // client's shrine models (`art/models/misc/shrines/*`) were measured first
  // and rejected: they are sacrificial altars with a flayed body and candles,
  // not monuments.
  "poe-statue-moai": { fmt: "art/models/terrain/act3/battlefield/act3_statue_false_03_01_statuemale01.fmt.json" },
  // A3 pots: the client's encampment ceramic pots, the three tallest of the
  // `art/models/chests/pots` set (0.63 / 0.52 / 0.61 m in their own space). They
  // share `potsc` (`pots_colour`), and the rule rotates them so a floor's pots
  // are not one clone repeated.
  "poe-pot-1": { fmt: "art/models/chests/pots/doodads_pot_01.fmt.json" },
  "poe-pot-2": { fmt: "art/models/chests/pots/doodads_pot_03.fmt.json" },
  "poe-pot-3": { fmt: "art/models/chests/pots/doodads_pot_04.fmt.json" },
  // A2: the forest trees the scenery pass paints over Megabonk's Tree0-2 and
  // DeadTree0. Exported from `art/models/terrain/forest/trees` with their
  // materials and textures (`art/textures/environment/forest`), because every
  // shape's material lives there and `propMaterial` reads it beside the model.
  // The three living clusters differ in silhouette and height; the fourth is
  // the client's burnt skull-tree, which is the "dead tree" of the set.
  // A5.2: the microwave is Megabonk's item-cooking device. The client's
  // Expedition plinth is a metal device on a base with a socketed top — the
  // "put an item in, take one out" read — and it is one material
  // (`ExpeditionPlinthc`). Its own box is 0.50 × 0.51 × 0.95 m; the swap fits it
  // inside the microwave's drawn body box like every A3/A5 element.
  "poe-microwave": { fmt: "art/models/misc/expeditionplinth/expeditionplinth.fmt.json" },
  // A5.2: the cursed shrine (boss curse) wears the client's **Vaal soul
  // vessel** — a 2.2 m jar holding a soul, a single `VaalSoulVesselcns` map.
  // Themed on the curse: a sealed vessel that is opened, not a Megabonk statue.
  "poe-cursed-shrine": { fmt: "art/models/misc/vaalsoulvessel/vaalsoulvessel.fmt.json" },
  // A5.2: the challenge shrine wears the Labyrinth's **Izaro pedestal**
  // (`labyrinth_shrine_crown`, the shrine a trial places its reward on) — one
  // material (`Piedestal_for_boss_fight`), 0.53 × 0.43 × 1.25 m in its own space.
  // Exported with `Art/Models/Misc/Shrines/IzaroShrine/Crown` plus
  // `Art/Textures/Misc/Shrines/IzaroShrine` for its colour map.
  "poe-challenge-shrine": {
    kind: "sm",
    dir: "art/models/misc/shrines/izaroshrine/crown",
    sm: "labyrinth_shrine_crown.sm.json",
    textures: ["art/textures/misc/shrines/izaroshrine/piedestal_for_boss_fight_colour.png"],
    // The Izaro pedestal ships every surface twice, coincident and reversed
    // (849 pairs of 1854 triangles): the client's way of drawing a solid from
    // both sides while it still culls back faces. Our shrine material is
    // two-sided (it keeps the host shrine shader), so both layers cover the same
    // pixels and the inward copy wins at equal depth — the operator's "le pilier
    // devient noir selon l'angle". Keep one layer, as the Shaper's cape does.
    dedupeLayers: true,
  },
  // A5.2: the magnet shrine wears the Act 10 **Kitava horn arch** — two thick
  // horn arms rising from a flared base with the middle open, which is the
  // native `MagnetShrine`'s own read (a huge horseshoe magnet: 5.27 x 6.87 x
  // 5.27 m, two prongs and an open middle, measured from its 208-triangle mesh).
  // The first pick, a low crystal cluster, had nothing to do with that shape
  // (the operator: "aucun rapport avec le truc de base"). Extents 6.32 x 4.71 x
  // 6.08 m, two Kitava maps (`KitavaSpikeTrunk` body, `KitavaSpikeTip` tips).
  "poe-magnet-shrine": { fmt: "art/models/terrain/act10/ravenous_hideout/hornarch.fmt.json" },
  "forest-tree-1": { fmt: "art/models/terrain/forest/trees/tree_v03_01_forest_trees01.fmt.json" },
  "forest-tree-2": { fmt: "art/models/terrain/forest/trees/tree_v05_01_forest_trees03.fmt.json" },
  "forest-tree-3": { fmt: "art/models/terrain/forest/trees/tree_v06_05_forest_trees06.fmt.json" },
  "forest-tree-dead": { fmt: "art/models/terrain/forest/trees/treewitch_v02_01_skulltree01.fmt.json" },
};
const propRawDir = path.join(raw, "props");
const propOutDir = path.join(outDir, "props");

const CLASS_TEXTURES = {
  Marauder: "textures/characters/art/textures/characters/warrior/warrior_colour_dxt1.png",
  Ranger: "textures/characters/art/textures/characters/hunter/ranger_colour_dxt1_lum.png",
  Witch: "textures/characters/art/textures/characters/witch/witch_colour_dxt1_lum.png",
  Duelist: "textures/characters/art/textures/characters/swordsman/colour.png",
  Templar: "textures/characters/art/textures/characters/templar/templar_colour_dxt1_lum.png",
  Shadow: "textures/characters/art/textures/characters/trickster/trickster_colour_dxt1.png",
  Scion: "textures/characters/art/textures/characters/adventurer/adventurer_colour_dxt1_lum.png",
};

// Full decompressed normal maps where the client ships one. Swordsman only has
// the packed normal+specular atlas, so it keeps the current material.
const CLASS_NORMALS = {
  Marauder: "textures/characters/art/textures/characters/warrior/warrior_normal_dxt5.png",
  Ranger: "textures/characters/art/textures/characters/hunter/ranger_normal_dxt5.png",
  Witch: "textures/characters/art/textures/characters/witch/witch_normal_dxt5.png",
  Templar: "textures/characters/art/textures/characters/templar/templar_normal_dxt5.png",
  Shadow: "textures/characters/art/textures/characters/trickster/trickster_normal_dxt5.png",
  Scion: "textures/characters/art/textures/characters/adventurer/adventurer_normal_dxt5.png",
};

const CLASS_MODELS = {
  Marauder: { model: "warrior", idle: "idle_relaxed_sword_shield", walk: "run_sword_shield", jump: "leapslam_sword_shield", hit: "flinch_sword_shield", attack: "attack1_sword_shield", cast: "spell_aoe_sword_shield" },
  Ranger: { model: "hunter", idle: "idle_relaxed_bow", walk: "run_bow", jump: "leapslam_sword", hit: "flinch_bow", attack: "attack1_bow", cast: "spell_projectile_bow" },
  Witch: { model: "witch", idle: "idle_relaxed_wand", walk: "run_wand", jump: "leapslam_staff", hit: "flinch_wand", attack: "attack1_wand", cast: "spell_projectile_wand" },
  Duelist: { model: "swordsman", idle: "idle_relaxed_2hsword", walk: "run_2hsword", jump: "leapslam_2hsword", hit: "flinch_2hsword", attack: "attack1_2hsword", cast: "spellaoe_2hsword" },
  Templar: { model: "templar", idle: "idle_relaxed_staff", walk: "run_staff", jump: "leapslam_staff", hit: "flinch_staff", attack: "attack1_staff", cast: "spellaoe_staff" },
  Shadow: { model: "trickster", idle: "idle_relaxed_dagger", walk: "run_dagger", jump: "leapslam_sword_dagger", hit: "flinch_dagger", attack: "attack1_dagger", cast: "spellaoe_dagger" },
  Scion: { model: "adventurer", idle: "idle_relaxed_sword", walk: "run_sword", jump: "leapslam_sword", hit: "flinch_sword", attack: "attack1_sword", cast: "spellaoe_sword" },
};

const MONSTERS = {
  fireelemental: {
    model: "fireelemental",
    idle: "idle",
    walk: "walk",
    death: "death",
    hit: "flinch",
    attack: "attack",
    texture: "textures/monsters/FireElemental/art/textures/monsters/fireelemental/fireelemental_colour.png",
    // PoE lights the elemental with its glow map; without it the colour map
    // reads as charcoal instead of magma.
    emissive: "textures/monsters/FireElemental/art/textures/monsters/fireelemental/fireelemental_glow.png",
  },
  stonegolem: {
    model: "stonegolem",
    idle: "idle",
    walk: "run",
    death: "death",
    hit: "flinch",
    attack: "attack1",
    texture: "textures/monsters/StoneGolem/art/textures/monsters/stonegolem/StoneGolemRock_colour.png",
  },
  goatman: {
    model: "goatman",
    idle: "idle",
    walk: "walk",
    death: "death",
    hit: "flinch",
    attack: "attack1",
    texture: "textures/monsters/Goatman/art/textures/monsters/goatman/Abberath_colour.png",
  },
  beasts: {
    model: "bloodape",
    idle: "idle",
    walk: "run",
    death: "death_01",
    hit: "flinch",
    attack: "attack_01",
    texture: "models/monsters/bloodape/art/models/monsters/bloodape/textures/bloodapeandpaleservant01_colour_dxt5.png",
  },
  // A real spider for the spider slots (BoomerSpider). The client's
  // SpiderPlague rig: `idle_01`/`run_01`/`attack_01`/`flinch_01`/`death_01`,
  // 44 bones, one mesh, one colour map.
  spiders: {
    model: "spiderplague",
    idle: "idle_01",
    walk: "run_01",
    death: "death_01",
    hit: "flinch_01",
    attack: "attack_01",
    texture: "models/monsters/spiderplague/art/textures/monsters/spiderplague/spiderplague_colour.png",
    sm: "plaguespider.sm.json",
    smd: "rig_1aa4e72a.smd.json",
  },
  // Undead for the Zombie/Ghoul slots, which wore the goatman (a horned
  // humanoid, not a corpse). The client's dismembered zombie: 50 bones, one
  // material, `idle`/`walk1`/`attack1`/`flinch`/`death`.
  zombies: {
    model: "zombiedismember",
    modelDir: "art/raw/models/monsters/zombies/art/models/monsters/zombiedismember",
    idle: "idle",
    walk: "walk1",
    death: "death",
    hit: "flinch",
    attack: "attack1",
    texture: "models/monsters/zombies/art/textures/monsters/zombiedismember/dismemberedzombie_colour.png",
  },
  // Spectral bodies for the Ghost slots, which wore the walking bone cultist.
  // The Banshee rig's `wraith_purple` shape is a floating sheet ghost (43 bones,
  // idle/run/attack/flinch/death). Its hair is a second rig with hair-chain bones
  // only, so it is not merged: it could only ride the root as a rigid prop.
  ghosts: {
    model: "wraith_purple",
    modelDir: "art/raw/models/monsters/ghosts/art/models/monsters/banshee/bansheebody",
    idle: "idle",
    walk: "run",
    death: "death",
    hit: "flinch",
    attack: "attack",
    texture: "models/monsters/ghosts/art/textures/monsters/banshee/wraithbodypurple_colour.png",
  },
  // Every flying slot (the Bee, the Wisps and the Ghost kinds, S7b): the
  // client's Tormented Spirit, 50 bones, `idle_01`/`run_01`/`spell_projectile_01`/
  // `flinch_01`/`death_01`. The body's five shapes span two materials (body and
  // wood), so one 2x2 atlas keeps the frozen single-texture path.
  tormentedspirit: {
    model: "tormentedspirit",
    modelDir: "art/raw/models/monsters/spirit/art/models/monsters/tormentedspirit",
    idle: "idle_01",
    walk: "run_01",
    death: "death_01",
    hit: "flinch_01",
    attack: "spell_projectile_01",
    texture: "models/monsters/spirit/art/textures/monsters/tormentmonster/tormentmonster_colour.png",
    textureGroups: [
      "models/monsters/spirit/art/textures/monsters/tormentmonster/tormentmonster_colour.png",
      "models/monsters/spirit/art/textures/monsters/tormentmonster/tormentmonste_wood_colour.png",
    ],
  },
  // The Bee slot is a small flying pest, not a spectral body: the client's
  // MineBat rig (44 bones, one material) reads as an airborne swarm animal.
  bats: {
    model: "minebat",
    modelDir: "art/raw/models/monsters/bats/art/models/monsters/minebat",
    // The bat's own idle is a ceiling-hang (its lowest point sits 0.665 above
    // the origin), so the fit and every stationary frame would read as a
    // shrunken, hanging animal. Both states use the fly cycle instead.
    idle: "fly",
    walk: "fly",
    death: "death",
    hit: "flinch",
    attack: "attack1",
    texture: "models/monsters/bats/art/textures/monsters/minebat/minebat_diffuse.png",
  },
  // A canine for the smallest slots (the Slime), which wore the crouched blood
  // ape and read as a tiny blob. The client's Hound rig (the hyena shape): one
  // mesh, one colour map, 12 clips (`idle`/`walk`/`run`/`attack1`/`flinch`/
  // `death`), and a quadruped silhouette that still fits Megabonk's small
  // footprint.
  wolves: {
    model: "hound",
    modelDir: "art/raw/models/monsters/wolves/art/models/monsters/hound",
    idle: "idle",
    walk: "run",
    death: "death",
    hit: "flinch",
    attack: "attack1",
    texture: "models/monsters/wolves/art/textures/monsters/hound/hyena_colour.png",
  },
  // A5.1: the shady-guy merchant wears a PoE town NPC. Tarkleigh, the Act 1
  // vendor, is the read: 32 bones, a 190 cm standing body, one `idle` clip and
  // its own two colour maps (body + quiver) — a standing trader, exactly what
  // the merchant is. Exported with `ggpk-explorer export
  // Art/Models/NPC/Tarkleigh ... --parsed json --textures png` plus
  // `Art/Textures/NPC/Tarkleigh`, both under `art/raw/npc/`.
  merchants: {
    // The `_v02` rig is the one with Tarkleigh's own art: two client materials
    // (body + quiver), each with its colour map, and one `idle` clip. The first
    // rig's material is the shared `malecns` (Art/Textures/NPC/male_colour), so
    // the two maps are folded into the usual 2x2 atlas.
    model: "tarkleighv2",
    modelDir: "art/raw/npc/art/models/npc/tarkleigh/tarkleigh_v02",
    sm: "tarkleighv2.sm.json",
    smd: "rig_6a3b6fa7.smd.json",
    idle: "idle",
    walk: "idle",
    // **No atlas.** The body carries the same UVs the client authored for its
    // own map, so the single-texture path (the classes' path) draws it. The 2x2
    // fold put the body's only-row UVs in the half the runtime samples as empty
    // and the whole model drew black; a one-row atlas cannot work with the
    // flipped-PNG convention. The quiver is a small accessory and shares the
    // body map.
    //
    // The DXT5 export is the painted sheet (mean 89/73/64, 86 % of texels above
    // 30); the exporter's plain `tarkleigh_colour.png` is the 9 %-opaque mask
    // variant, and painting the body with it drew the scattered fragments the
    // first build showed.
    texture: "npc/art/textures/npc/tarkleigh/tarkleigh_colour_dxt5.png",
  },
  skeletons: {
    model: "basicskeleton",
    idle: "idle_01",
    walk: "walk_01",
    death: "death_broken_fwd_01",
    hit: "flinch_01",
    attack: "attack_01",
    texture: "textures/monsters/katarinaskeleton/katarinaskeleton1_colour.png",
    sm: "basicskeleton1.sm.json",
    smd: "basicskeleton1_armour_60aa0202.smd.json",
    rig: "animation/caster/rig.ast.json",
  },
  // Atlas bosses. The Shaper's figure is split into a body rig and a separate
  // head rig, so the head is merged onto the body skeleton by bone name (below).
  elder: {
    model: "elder",
    idle: "idle",
    walk: "run",
    death: "death",
    hit: "flinch",
    attack: "attack_01",
    texture: "textures/monsters/Elder/art/textures/monsters/elder/elder_cl.png",
    // The body's 11 shapes span four client materials, each with its own
    // colour map (elder.sm.json: 8 + 1 scrolls + 1 tentacle + 1 refractive
    // membrane). Painting all of them with elder_cl.png put the body atlas on
    // the scrolls and tentacles at their own UVs ("les textures sont un peu
    // mal placées comme maven"). One tile per group, one baked atlas.
    textureGroups: [
      "textures/monsters/Elder/art/textures/monsters/elder/elder_cl.png",
      "textures/monsters/Elder/art/textures/monsters/elder/scrollsao_cl.png",
      "textures/monsters/Elder/art/textures/monsters/elder/tentacle_colour.png",
      "textures/monsters/Elder/art/textures/monsters/elder/tentacle_refrac_colour.png",
    ],
  },
  shaper: {
    model: "theshaper",
    modelDir: "art/raw/models/monsters/theshaper/art/models/monsters/theshaper/theshaperbody",
    sm: "theshaperbody.sm.json",
    smd: "rig_7d06640a.smd.json",
    idle: "idle",
    walk: "run",
    death: "death",
    hit: "flinch",
    attack: "attack1",
    texture: "textures/monsters/TheShaper/art/textures/monsters/theshaper/theshaper_colour.png",
    // The body ships its cloth twice, coincident and reversed (668 pairs). Our
    // cloth bodies are drawn two-sided, so the twin z-fights and the inward copy
    // wins with a normal that shades black - the cape. Dropped at build time.
    dedupeLayers: true,
    merge: [
      {
        modelDir: "art/raw/models/monsters/theshaper/art/models/monsters/theshaper/theshaperhead",
        smd: "rig_d043bbd2.smd.json",
        // E2: the head rig has no head joint (root + jnt__neck + the beard
        // chain only), so the skull cannot follow the body's jnt_head — on that
        // joint it rotated out of the hood while the hood bones (children of
        // jnt_neck) swayed. The whole part is pinned to jnt_neck, the hood's
        // parent, so the skull stays inside the cowl; the beard follows the same
        // joint because its chain has no counterpart on the body rig.
        // `offset` is applied after the anchor transform (pre-multiplied), and
        // the anchor's basis is rotated relative to the body: the axes are NOT
        // x-right/y-forward/z-up. It was solved numerically instead of guessed -
        // lab/tools/shaper-head-sweep.py builds the GLB for candidate offsets and
        // reports the merged skull's centre against the body hood's, so the
        // offset -> skull Jacobian is measured. E2 used [0,-1,-16] (skull centred
        // in the cowl); the live look still read the head a touch too high, so
        // E2b lowered it with the same sweep: +1 z of the offset is -0.73 cm on
        // the skull's world Y and +0.66 cm on its Z, i.e. `[0,-1,-13]` puts the
        // skull 3.5 cm under the hood's centre (was 1.4 cm) and 2 cm further
        // back, still inside the cowl at idle and walk. Every head vertex is on
        // jnt_neck, the hood bones' parent. See shaper-head-fit.py (numbers)
        // and shaper-head-mark.py --zones (skull/beard/hood/neck in colour).
        boneMap: { root: "jnt_neck", jnt__neck: "jnt_neck" },
        defaultBone: "jnt_neck",
        offset: [0, -1, -13],
      },
    ],
  },
  // S2c: the two floor bosses' own client models. Their MonsterVarieties rows
  // both point at AtlasInvaders AO files, which in turn name these rigs:
  // CleansingBoss.ao -> TheCleansingFireBoss, ConsumeBoss.ao -> TangleBoss.
  // One material each (two body shapes), so no atlas fold.
  "searing-exarch": {
    model: "thecleansingfireboss",
    idle: "idle_01",
    walk: "run_01",
    death: "death_01",
    hit: "flinch_small_01",
    attack: "attack_01",
    texture: "models/monsters/thecleansingfireboss/art/models/monsters/3.17monsters/thecleasingfireboss/textures/thecleansingfireboss_colour_dxt1.png",
    // The base skin is the horned mantle only; the lower body is the client's
    // separate robe attachment (its own rig, same material). The body rig has
    // no robe/leg bones, so the part is anchored on the hip and its cloth chain
    // follows the hip (rigid, the skirt still reads).
    merge: [
      {
        modelDir: "art/raw/models/monsters/thecleansingfireboss/art/models/monsters/thecleansingfireboss/attachments/robe",
        smd: "armour_TCFBRobe_ba1e36de.smd.json",
        rig: "armour_tcfbrobe.ast.json",
        boneMap: { hip_jntBnd: "hip_jntBnd" },
        defaultBone: "hip_jntBnd",
      },
    ],
  },
  "eater-of-worlds": {
    model: "tangledboss",
    sm: "tangleboss.sm.json",
    idle: "idle_01",
    walk: "run_01",
    death: "death_01",
    hit: "flinch_01",
    attack: "attack_01",
    texture: "models/monsters/tangledboss/art/models/monsters/tangledboss/textures/tangledboss_colour_dxt5.png",
  },
  // Maven reuses The Collector's rig and mesh in the client (her monster entry
  // points at TheCollector.ao), so this is her actual model, not a stand-in.
  maven: {
    model: "thecollector",
    idle: "idle_01",
    walk: "run_01",
    death: "death_01",
    hit: "flinch_small_01",
    attack: "attack_01",
    texture: "models/monsters/thecollector/art/models/monsters/thecollector/textures/thecollector1_colour_dxt1.png",
    // thecollector.sm.json splits the 18 shapes across four materials with
    // four colour maps: 1c (body/robe/frills…), 3c (cape/crest/orbs…), 2c
    // (hair_base/layer_1) and 2_fur (hair_layer_2/3). Drawing all of them with
    // the 1c face atlas put the face on the cape ("the face on the back").
    // Tiles in material order; the builder folds each shape's UVs into its tile.
    textureGroups: [
      "models/monsters/thecollector/art/models/monsters/thecollector/textures/thecollector1_colour_dxt1.png",
      "models/monsters/thecollector/art/models/monsters/thecollector/textures/thecollector3_colour_dxt1.png",
      "models/monsters/thecollector/art/models/monsters/thecollector/textures/thecollector2_colour_dxt1.png",
      "models/monsters/thecollector/art/models/monsters/thecollector/textures/thecollector2_fur_dxt5.png",
    ],
  },
};

/** PoE animations are 30 fps; AST keys are frame indices. */
const FPS = 30;
/** PoE units are centimetres. */
const WORLD_SCALE = 0.01;

/**
 * Client rigs spell the same clip two ways (`spell_aoe_*` on the older rigs,
 * `spellaoe_*` on the newer ones). Compare names without underscores, then fall
 * back to a prefix match so per-rig variants still resolve.
 */
function findAnimation(ast, name) {
  const direct = ast.animations.find((entry) => entry.name === name);
  if (direct) {
    return direct;
  }
  const normalized = (value) => value.toLowerCase().replaceAll("_", "");
  const target = normalized(name);
  return ast.animations.find((entry) => normalized(entry.name) === target)
    ?? ast.animations.find((entry) => normalized(entry.name).startsWith(target));
}

function unitDefinitions(names) {
  const defs = [];
  const classes = names.length ? names.filter((name) => CLASS_MODELS[name]) : Object.keys(CLASS_MODELS);
  for (const name of classes) {
    const info = CLASS_MODELS[name];
    defs.push({
      name,
      modelDir: path.join(raw, "models", "characters", "art", "models", "characters", info.model),
      sm: `${info.model}.sm.json`,
      texture: path.join(raw, CLASS_TEXTURES[name]),
      normal: CLASS_NORMALS[name] ? path.join(raw, CLASS_NORMALS[name]) : null,
      idle: info.idle,
      walk: info.walk,
      jump: info.jump,
      hit: info.hit,
      attack: info.attack,
      cast: info.cast,
    });
  }
  const monsters = names.length ? names.filter((name) => MONSTERS[name]) : Object.keys(MONSTERS);
  for (const name of monsters) {
    const info = MONSTERS[name];
    defs.push({
      name,
      modelDir: info.modelDir ? path.resolve(root, info.modelDir) : path.join(raw, "models", "monsters", info.model, "art", "models", "monsters", info.model),
      sm: info.sm ?? `${info.model}.sm.json`,
      smd: info.smd,
      rig: info.rig,
      texture: info.textureAbsolute ? path.resolve(root, info.textureAbsolute) : path.join(raw, info.texture),
      textureGroups: info.textureGroups ? info.textureGroups.map((file) => path.join(raw, file)) : null,
      emissive: info.emissive ? path.join(raw, info.emissive) : null,
      dedupeLayers: info.dedupeLayers === true,
      merge: (info.merge ?? []).map((part) => ({
        modelDir: path.resolve(root, part.modelDir),
        smd: part.smd,
        rig: part.rig ?? "rig.ast.json",
        boneMap: part.boneMap ?? {},
        defaultBone: part.defaultBone ?? null,
        offset: part.offset ?? null,
      })),
      idle: info.idle,
      walk: info.walk,
      death: info.death,
      hit: info.hit,
      attack: info.attack,
    });
  }
  return defs;
}

function loadModel(def) {
  const sm = JSON.parse(readFileSync(path.join(def.modelDir, def.sm), "utf8"));
  const smdName = def.smd ?? `${path.basename(sm.smd_file).replace(/\.smd$/, "")}.smd.json`;
  const smd = JSON.parse(readFileSync(path.join(def.modelDir, smdName), "utf8"));
  const ast = JSON.parse(readFileSync(path.join(def.modelDir, def.rig ?? "rig.ast.json"), "utf8"));
  return { sm, smd, ast };
}

/**
 * Merge an extra SMD onto the primary skeleton.
 *
 * Boss rigs split one figure across files: The Shaper ships a body and a head,
 * each with its own rig and animation set. The part's bone list is remapped onto
 * the primary bones by name; bones the primary rig does not have (the head's
 * beard chain) fall back to `defaultBone`, so the piece follows its anchor
 * instead of collapsing to the root.
 */
function appendPart(part, vertices, indices, primaryBones, primaryBindWorld) {
  const smd = JSON.parse(readFileSync(path.join(part.modelDir, part.smd), "utf8"));
  const ast = JSON.parse(readFileSync(path.join(part.modelDir, part.rig), "utf8"));
  const remap = ast.bones.map((bone) => {
    const wanted = part.boneMap[bone.name] ?? bone.name;
    const index = primaryBones.get(wanted) ?? (part.defaultBone ? primaryBones.get(part.defaultBone) : undefined);
    if (index === undefined) {
      throw new Error(`${part.smd}: bone ${bone.name} has no counterpart on the primary rig`);
    }
    return index;
  });

  // The part is authored in its own rig's model space (The Shaper's head sits
  // around the origin of its own rig, while the body's head is ~195 cm up the
  // Z axis). Bind it at its anchor joint: T = primaryBindWorld(anchor) *
  // inverse(partBindWorld(anchor)) maps the part's rest pose into the primary's
  // model space. `offset` is the eye-tuned correction of the two rigs' joint
  // rest offsets (the head sat a little out of the hood).
  const partSkeleton = buildSkeleton(ast);
  const anchor = Object.entries(part.boneMap ?? {})[0];
  const transform = mat4.create();
  let anchored = false;
  if (anchor) {
    const [sourceName, primaryName] = anchor;
    const sourceIndex = ast.bones.findIndex((bone) => bone.name === sourceName);
    const primaryIndex = primaryBones.get(primaryName);
    if (sourceIndex >= 0 && primaryIndex !== undefined) {
      const inversePart = mat4.create();
      mat4.invert(inversePart, partSkeleton.bindWorld[sourceIndex]);
      mat4.multiply(transform, primaryBindWorld[primaryIndex], inversePart);
      if (part.offset) {
        // Applied in the primary rig's space (Z-up, centimetres), after the
        // joint mapping: the knobs are x = right, y = forward, z = up.
        const shifted = mat4.create();
        mat4.fromTranslation(shifted, vec3.fromValues(...part.offset));
        mat4.multiply(transform, shifted, transform);
      }
      anchored = true;
    }
  }

  if (!anchored) {
    throw new Error(`${part.smd}: no usable anchor bone for the merge`);
  }

  const extra = vertexData(smd);
  const offset = vertices.length;
  for (const vertex of extra.vertices) {
    const position = vec3.create();
    vec3.transformMat4(position, vertex.pos, transform);
    const normal = vec3.create();
    vec3.transformMat4(normal, normalOf(vertex), transform);
    vec3.normalize(normal, normal);
    vertices.push({
      ...vertex,
      pos: [position[0], position[1], position[2]],
      normal: [normal[0], normal[1], normal[2]],
      skin_bones: vertex.skin_bones.map((bone) => remap[bone] ?? 0),
    });
  }
  for (const index of extra.indices) {
    indices.push(index + offset);
  }
}

function buildSkeleton(ast) {
  const parentOf = new Array(ast.bones.length).fill(-1);
  const childrenOf = Array.from({ length: ast.bones.length }, () => []);
  const visit = (parent, first) => {
    let child = first;
    while (child !== null && child !== undefined) {
      parentOf[child] = parent;
      if (parent >= 0) childrenOf[parent].push(child);
      visit(child, ast.bones[child].child);
      child = ast.bones[child].sibling;
    }
  };
  visit(-1, 0);

  const bindLocal = ast.bones.map((bone) => {
    const matrix = mat4.create();
    for (let row = 0; row < 4; row += 1) {
      for (let column = 0; column < 4; column += 1) {
        matrix[column * 4 + row] = bone.transform[column][row];
      }
    }
    return matrix;
  });

  const bindWorld = new Array(ast.bones.length);
  const invBind = new Array(ast.bones.length);
  for (let index = 0; index < ast.bones.length; index += 1) {
    bindWorld[index] = mat4.create();
    if (parentOf[index] === -1) mat4.copy(bindWorld[index], bindLocal[index]);
    else mat4.multiply(bindWorld[index], bindWorld[parentOf[index]], bindLocal[index]);
    invBind[index] = mat4.create();
    mat4.invert(invBind[index], bindWorld[index]);
  }
  return { parentOf, childrenOf, bindLocal, bindWorld, invBind };
}

function vertexData(smd) {
  const section = smd.section.V3 ?? smd.section.V2;
  const lod = section.dolm ? section.dolm.lods[0] : null;
  const vertices = lod ? lod.vertices : Object.values(section.vertex_buffer);
  const indices = lod ? lod.indices.U16 : section.index_buffer.U16;
  return { vertices, indices };
}

function normalOf(vertex) {
  const n = vertex.normal;
  if (Math.abs(n[0]) <= 1.001 && Math.abs(n[1]) <= 1.001 && Math.abs(n[2]) <= 1.001) {
    return [n[0], n[1], n[2]];
  }
  return [n[0] / 127, n[1] / 127, n[2] / 127];
}

class GlbBuilder {
  constructor() {
    this.json = { asset: { version: "2.0", generator: "poe-gltf" }, scene: 0, scenes: [{ nodes: [] }], nodes: [], meshes: [], accessors: [], bufferViews: [], buffers: [{ byteLength: 0 }], materials: [], textures: [], images: [], samplers: [], skins: [], animations: [] };
    this.chunks = [];
    this.offset = 0;
  }

  bufferView(data, target) {
    const buffer = Buffer.isBuffer(data) ? data : Buffer.from(data.buffer, data.byteOffset, data.byteLength);
    const pad = (4 - (this.offset % 4)) % 4;
    if (pad > 0) {
      this.chunks.push(Buffer.alloc(pad));
      this.offset += pad;
    }
    const view = { buffer: 0, byteOffset: this.offset, byteLength: buffer.length };
    if (target) view.target = target;
    this.json.bufferViews.push(view);
    this.chunks.push(buffer);
    this.offset += buffer.length;
    return this.json.bufferViews.length - 1;
  }

  accessor(data, componentType, type, count, target, min, max) {
    const view = this.bufferView(data, target);
    const accessor = { bufferView: view, componentType, count, type };
    if (min) accessor.min = min;
    if (max) accessor.max = max;
    this.json.accessors.push(accessor);
    return this.json.accessors.length - 1;
  }

  build() {
    this.json.buffers[0].byteLength = this.offset;
    const json = Buffer.from(JSON.stringify(this.json), "utf8");
    const jsonPad = (4 - (json.length % 4)) % 4;
    const jsonChunk = Buffer.concat([json, Buffer.alloc(jsonPad, 0x20)]);
    const binPad = (4 - (this.offset % 4)) % 4;
    const bin = Buffer.concat([...this.chunks, Buffer.alloc(binPad)]);
    const total = 12 + 8 + jsonChunk.length + 8 + bin.length;
    const header = Buffer.alloc(12);
    header.writeUInt32LE(0x46546c67, 0);
    header.writeUInt32LE(2, 4);
    header.writeUInt32LE(total, 8);
    const jsonHeader = Buffer.alloc(8);
    jsonHeader.writeUInt32LE(jsonChunk.length, 0);
    jsonHeader.writeUInt32LE(0x4e4f534a, 4);
    const binHeader = Buffer.alloc(8);
    binHeader.writeUInt32LE(bin.length, 0);
    binHeader.writeUInt32LE(0x004e4942, 4);
    return Buffer.concat([header, jsonHeader, jsonChunk, binHeader, bin]);
  }
}

function addNode(builder, node) {
  builder.json.nodes.push(node);
  return builder.json.nodes.length - 1;
}

/**
 * A PoE cloth part can ship as two coincident layers: the same triangles twice,
 * one reversed, with opposite normals. That is how the client draws a sheet from
 * both sides while it still culls back faces.
 *
 * We draw cloth bodies two-sided (`PoeEnemies` writes `_RenderFace 0`), so both
 * copies cover the same pixels at the same depth: the inward copy wins the
 * equal-depth test with a normal pointing into the body, and MK Toon shades it
 * black. That was The Shaper's cape (174 pairs in the hood shape, 668 in the
 * whole mesh). Keep the layer whose winding faces away from the body - the side
 * the client's culling would draw - and drop its twin.
 *
 * The body axis is the mesh's mean X/Y in PoE's Z-up space; the pair is exactly
 * coincident, so the radial test only has to order two opposite faces.
 */
function dropCoincidentTwins(vertices, indices) {
  let axisX = 0;
  let axisY = 0;
  for (const vertex of vertices) {
    axisX += vertex.pos[0];
    axisY += vertex.pos[1];
  }
  axisX /= vertices.length;
  axisY /= vertices.length;

  const cornerOf = (start, corner) => vertices[indices[start + corner]].pos;
  const centroidOf = (start) => {
    const a = cornerOf(start, 0);
    const b = cornerOf(start, 1);
    const c = cornerOf(start, 2);
    return [(a[0] + b[0] + c[0]) / 3, (a[1] + b[1] + c[1]) / 3];
  };
  const faceOf = (start) => {
    const a = cornerOf(start, 0);
    const b = cornerOf(start, 1);
    const c = cornerOf(start, 2);
    return [
      (b[1] - a[1]) * (c[2] - a[2]) - (b[2] - a[2]) * (c[1] - a[1]),
      (b[2] - a[2]) * (c[0] - a[0]) - (b[0] - a[0]) * (c[2] - a[2]),
      (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]),
    ];
  };
  // Positive for the layer whose winding faces away from the body, negative for
  // its twin: the pair is coincident, so only the radial sign can order them.
  const outwardOf = (start) => {
    const [x, y] = centroidOf(start);
    const [nx, ny] = faceOf(start);
    return nx * (x - axisX) + ny * (y - axisY);
  };
  const keyOf = (start) =>
    [0, 1, 2]
      .map((corner) => cornerOf(start, corner).map((value) => value.toFixed(4)).join(","))
      .sort()
      .join("|");

  const kept = [];
  const seen = new Map();
  let dropped = 0;
  for (let start = 0; start + 2 < indices.length; start += 3) {
    const key = keyOf(start);
    const slot = seen.get(key);
    if (slot === undefined) {
      seen.set(key, kept.length);
      kept.push(start);
      continue;
    }

    dropped += 1;
    if (outwardOf(start) > outwardOf(kept[slot])) {
      kept[slot] = start;
    }
  }

  const result = [];
  for (const start of kept) {
    result.push(indices[start], indices[start + 1], indices[start + 2]);
  }

  return { indices: result, dropped, kept };
}

const PNG_SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let value = 0; value < 256; value += 1) {
    let entry = value;
    for (let bit = 0; bit < 8; bit += 1) {
      entry = entry & 1 ? 0xedb88320 ^ (entry >>> 1) : entry >>> 1;
    }
    table[value] = entry >>> 0;
  }
  return table;
})();

function crc32(buffer) {
  let crc = 0xffffffff;
  for (const byte of buffer) {
    crc = CRC_TABLE[(crc ^ byte) & 0xff] ^ (crc >>> 8);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function pngChunk(type, data) {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length, 0);
  const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
  const checksum = Buffer.alloc(4);
  checksum.writeUInt32BE(crc32(body), 0);
  return Buffer.concat([length, body, checksum]);
}

function paeth(left, up, upLeft) {
  const estimate = left + up - upLeft;
  const da = Math.abs(estimate - left);
  const db = Math.abs(estimate - up);
  const dc = Math.abs(estimate - upLeft);
  return da <= db && da <= dc ? left : db <= dc ? up : upLeft;
}

/** RGBA8 decode of the exported client PNGs (they are all 8-bit RGB/RGBA,
 * non-interlaced). Only the atlas baker needs it, so there is no image
 * dependency in this build. */
function decodePng(buffer, label) {
  if (buffer.length < 8 || !buffer.subarray(0, 8).equals(PNG_SIGNATURE)) {
    throw new Error(`${label}: not a PNG`);
  }

  let offset = 8;
  let width = 0;
  let height = 0;
  let bitDepth = 0;
  let colorType = 0;
  let interlace = 0;
  const compressed = [];
  while (offset + 8 <= buffer.length) {
    const length = buffer.readUInt32BE(offset);
    const type = buffer.toString("ascii", offset + 4, offset + 8);
    const data = buffer.subarray(offset + 8, offset + 8 + length);
    offset += 12 + length;
    if (type === "IHDR") {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      bitDepth = data[8];
      colorType = data[9];
      interlace = data[12];
    } else if (type === "IDAT") {
      compressed.push(data);
    } else if (type === "IEND") {
      break;
    }
  }

  if (bitDepth !== 8 || interlace !== 0 || (colorType !== 2 && colorType !== 6)) {
    throw new Error(`${label}: PNG is not 8-bit RGB/RGBA (depth ${bitDepth}, type ${colorType}, interlace ${interlace})`);
  }

  const channels = colorType === 6 ? 4 : 3;
  const stride = width * channels;
  const inflated = inflateSync(Buffer.concat(compressed));
  const pixels = Buffer.alloc(height * stride);
  if (inflated.length < height * (stride + 1)) {
    throw new Error(`${label}: PNG data is truncated`);
  }

  for (let y = 0; y < height; y += 1) {
    const rowStart = y * (stride + 1);
    const filter = inflated[rowStart];
    const target = y * stride;
    for (let x = 0; x < stride; x += 1) {
      const value = inflated[rowStart + 1 + x];
      const left = x >= channels ? pixels[target + x - channels] : 0;
      const up = y > 0 ? pixels[target - stride + x] : 0;
      const upLeft = y > 0 && x >= channels ? pixels[target - stride + x - channels] : 0;
      let result = value;
      if (filter === 1) result += left;
      else if (filter === 2) result += up;
      else if (filter === 3) result += (left + up) >> 1;
      else if (filter === 4) result += paeth(left, up, upLeft);
      else if (filter !== 0) throw new Error(`${label}: unknown PNG filter ${filter}`);
      pixels[target + x] = result & 0xff;
    }
  }

  const rgba = Buffer.alloc(width * height * 4);
  for (let pixel = 0; pixel < width * height; pixel += 1) {
    rgba[pixel * 4] = pixels[pixel * channels];
    rgba[pixel * 4 + 1] = pixels[pixel * channels + 1];
    rgba[pixel * 4 + 2] = pixels[pixel * channels + 2];
    rgba[pixel * 4 + 3] = channels === 4 ? pixels[pixel * channels + 3] : 255;
  }

  return { width, height, rgba };
}

/** RGBA8 encode with the usual per-row filter heuristic (the atlas is several
 * megabytes of pixels, so raw filter 0 would bloat the GLB). */
function encodePng(width, height, rgba) {
  const stride = width * 4;
  const raw = Buffer.alloc(height * (stride + 1));
  const candidate = Buffer.alloc(stride);
  const best = Buffer.alloc(stride);

  for (let y = 0; y < height; y += 1) {
    const rowStart = y * stride;
    const previousStart = (y - 1) * stride;
    let bestFilter = 0;
    let bestScore = Infinity;
    for (let filter = 0; filter < 5; filter += 1) {
      let score = 0;
      for (let x = 0; x < stride; x += 1) {
        const value = rgba[rowStart + x];
        const left = x >= 4 ? rgba[rowStart + x - 4] : 0;
        const up = y > 0 ? rgba[previousStart + x] : 0;
        const upLeft = y > 0 && x >= 4 ? rgba[previousStart + x - 4] : 0;
        let predicted = 0;
        if (filter === 1) predicted = left;
        else if (filter === 2) predicted = up;
        else if (filter === 3) predicted = (left + up) >> 1;
        else if (filter === 4) predicted = paeth(left, up, upLeft);
        candidate[x] = (value - predicted) & 0xff;
        score += Math.abs((candidate[x] << 24) >> 24);
      }
      if (score < bestScore) {
        bestScore = score;
        bestFilter = filter;
        candidate.copy(best);
      }
    }
    raw[y * (stride + 1)] = bestFilter;
    best.copy(raw, y * (stride + 1) + 1);
  }

  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header[8] = 8;
  header[9] = 6;
  header[10] = 0;
  header[11] = 0;
  header[12] = 0;
  return Buffer.concat([
    PNG_SIGNATURE,
    pngChunk("IHDR", header),
    pngChunk("IDAT", deflateSync(raw, { level: 9 })),
    pngChunk("IEND", Buffer.alloc(0)),
  ]);
}

/**
 * One texture per client material group, baked into a 2x2 atlas.
 *
 * The client paints several material groups on one mesh and samples each with
 * its own colour map, at UVs authored for that map. Our runtime draws one
 * material per mesh, so the groups are folded into one atlas instead: each
 * group's UVs move into its tile and the single frozen body-material path keeps
 * working unchanged. Both Atlas bosses have four groups (Maven: body, cape/hair
 * variants, fur; Elder: body, scrolls, tentacle, refractive membrane) and no
 * vertex is shared between groups, so the fold is unambiguous.
 */
function buildAtlas(paths) {
  if (paths.length > 4) {
    throw new Error(`atlas supports 4 material groups, got ${paths.length}`);
  }

  const images = paths.map((file) => ({ file, image: decodePng(readFileSync(file), file) }));
  const tile = Math.max(...images.map(({ image }) => Math.max(image.width, image.height)));
  const size = tile * 2;
  const rgba = Buffer.alloc(size * size * 4);

  images.forEach(({ image }, group) => {
    const column = group % 2;
    const row = Math.floor(group / 2);
    const scaleX = image.width / tile;
    const scaleY = image.height / tile;
    for (let y = 0; y < tile; y += 1) {
      const sourceY = Math.min(image.height - 1, Math.max(0, (y + 0.5) * scaleY - 0.5));
      const y0 = Math.floor(sourceY);
      const y1 = Math.min(image.height - 1, y0 + 1);
      const fy = sourceY - y0;
      for (let x = 0; x < tile; x += 1) {
        const sourceX = Math.min(image.width - 1, Math.max(0, (x + 0.5) * scaleX - 0.5));
        const x0 = Math.floor(sourceX);
        const x1 = Math.min(image.width - 1, x0 + 1);
        const fx = sourceX - x0;
        const target = ((row * tile + y) * size + column * tile + x) * 4;
        for (let channel = 0; channel < 4; channel += 1) {
          const p00 = image.rgba[(y0 * image.width + x0) * 4 + channel];
          const p10 = image.rgba[(y0 * image.width + x1) * 4 + channel];
          const p01 = image.rgba[(y1 * image.width + x0) * 4 + channel];
          const p11 = image.rgba[(y1 * image.width + x1) * 4 + channel];
          const top = p00 + (p10 - p00) * fx;
          const bottom = p01 + (p11 - p01) * fx;
          rgba[target + channel] = Math.round(top + (bottom - top) * fy);
        }
      }
    }
  });

  return { png: encodePng(size, size, rgba), size, tile, count: images.length };
}

/** Material group (client material index) of every vertex, from the `.sm`'s
 * per-material shape counts and the SMD's shape ranges. A vertex shared by two
 * groups would make the UV fold ambiguous, so that is an error. */
function textureGroupPerVertex(sm, smd) {
  const section = smd.section.V3 ?? smd.section.V2;
  const lod = section.dolm ? section.dolm.lods[0] : null;
  const indices = lod ? lod.indices.U16 : section.index_buffer.U16;
  const shapes = (lod ? lod.shape_extents : section.shape_extents) ?? [];

  const groups = [];
  sm.materials.forEach((material, group) => {
    for (let shape = 0; shape < material.shape_count; shape += 1) {
      groups.push(group);
    }
  });

  if (shapes.length !== groups.length) {
    throw new Error(
      `${sm.smd_file}: ${shapes.length} shape(s) but the materials declare ${groups.length}`,
    );
  }

  const map = new Map();
  shapes.forEach((shape, index) => {
    const start = shape.start_index ?? (shape.triangle_index ?? 0) * 3;
    const next = shapes[index + 1];
    const end = next ? next.start_index ?? (next.triangle_index ?? 0) * 3 : indices.length;
    const group = groups[index];
    for (let cursor = start; cursor < end; cursor += 1) {
      const vertex = indices[cursor];
      const existing = map.get(vertex);
      if (existing !== undefined && existing !== group) {
        throw new Error(
          `${sm.smd_file}: vertex ${vertex} is used by material groups ${existing} and ${group}`,
        );
      }
      map.set(vertex, group);
    }
  });

  return map;
}

function buildUnit(def) {
  const { sm, smd, ast } = loadModel(def);
  const base = vertexData(smd);
  const vertices = base.vertices.slice();
  let indices = Array.from(base.indices);
  const skeleton = buildSkeleton(ast);
  const boneCount = ast.bones.length;
  const primaryBones = new Map(ast.bones.map((bone, index) => [bone.name, index]));
  for (const part of def.merge ?? []) {
    appendPart(part, vertices, indices, primaryBones, skeleton.bindWorld);
  }
  if (def.dedupeLayers) {
    const deduped = dropCoincidentTwins(vertices, indices);
    indices = deduped.indices;
    console.log(
      `  ${def.name}: ${deduped.dropped} coincident cloth triangle(s) dropped ` +
        `(one two-sided layer each: the client culls its twin)`,
    );
  }

  // A mesh whose shapes span several client materials keeps one texture at
  // runtime by folding every group's UVs into its own tile of the baked atlas
  // (see buildAtlas). Without groups the UVs are used as they come.
  const groupOfVertex = def.textureGroups ? textureGroupPerVertex(sm, smd) : null;
  const atlas = def.textureGroups ? buildAtlas(def.textureGroups) : null;
  if (atlas) {
    console.log(
      `  ${def.name}: atlas ${atlas.count} material group(s) -> ${atlas.size}x${atlas.size} ` +
        `(${(atlas.png.length / 1024).toFixed(0)} KB, tile ${atlas.tile})`,
    );
  }

  const builder = new GlbBuilder();
  const positions = new Float32Array(vertices.length * 3);
  const normals = new Float32Array(vertices.length * 3);
  const uvs = new Float32Array(vertices.length * 2);
  const joints = new Uint16Array(vertices.length * 4);
  const weights = new Float32Array(vertices.length * 4);
  let minX = Infinity;
  let minY = Infinity;
  let minZ = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  let maxZ = -Infinity;

  vertices.forEach((vertex, index) => {
    const [x, y, z] = vertex.pos;
    positions[index * 3] = x;
    positions[index * 3 + 1] = y;
    positions[index * 3 + 2] = z;
    minX = Math.min(minX, x);
    minY = Math.min(minY, y);
    minZ = Math.min(minZ, z);
    maxX = Math.max(maxX, x);
    maxY = Math.max(maxY, y);
    maxZ = Math.max(maxZ, z);

    const [nx, ny, nz] = normalOf(vertex);
    normals[index * 3] = nx;
    normals[index * 3 + 1] = ny;
    normals[index * 3 + 2] = nz;

    if (groupOfVertex) {
      const group = groupOfVertex.get(index) ?? 0;
      const column = group % 2;
      const row = Math.floor(group / 2);
      const [u, v] = vertex.tex_coord0;
      uvs[index * 2] = column * 0.5 + (u - Math.floor(u)) * 0.5;
      uvs[index * 2 + 1] = row * 0.5 + (v - Math.floor(v)) * 0.5;
    } else {
      uvs[index * 2] = vertex.tex_coord0[0];
      uvs[index * 2 + 1] = vertex.tex_coord0[1];
    }

    let total = 0;
    for (let influence = 0; influence < 4; influence += 1) {
      const bone = vertex.skin_bones[influence] ?? 0;
      const weight = (vertex.skin_weights[influence] ?? 0) / 255;
      joints[index * 4 + influence] = bone;
      weights[index * 4 + influence] = weight;
      total += weight;
    }
    if (total <= 0) {
      joints[index * 4] = 0;
      weights[index * 4] = 1;
    } else {
      for (let influence = 0; influence < 4; influence += 1) {
        weights[index * 4 + influence] /= total;
      }
    }
  });

  const positionAccessor = builder.accessor(positions, 5126, "VEC3", vertices.length, 34962, [minX, minY, minZ], [maxX, maxY, maxZ]);
  const normalAccessor = builder.accessor(normals, 5126, "VEC3", vertices.length, 34962);
  const uvAccessor = builder.accessor(uvs, 5126, "VEC2", vertices.length, 34962);
  const jointAccessor = builder.accessor(joints, 5123, "VEC4", vertices.length, 34962);
  const weightAccessor = builder.accessor(weights, 5126, "VEC4", vertices.length, 34962);
  const indexAccessor = builder.accessor(new Uint16Array(indices), 5123, "SCALAR", indices.length, 34963);

  // Texture + material.
  const image = atlas ? atlas.png : readFileSync(def.texture);
  const imageView = builder.bufferView(image);
  builder.json.images.push({ bufferView: imageView, mimeType: "image/png" });
  builder.json.samplers.push({ magFilter: 9729, minFilter: 9987, wrapS: 10497, wrapT: 10497 });
  builder.json.textures.push({ source: 0, sampler: 0 });
  const material = {
    name: def.name,
    pbrMetallicRoughness: {
      baseColorTexture: { index: 0 },
      metallicFactor: 0,
      roughnessFactor: 0.85,
    },
    alphaMode: "MASK",
    alphaCutoff: 0.35,
    doubleSided: true,
  };
  if (def.normal && existsSync(def.normal)) {
    const normalImage = readFileSync(def.normal);
    const normalView = builder.bufferView(normalImage);
    builder.json.images.push({ bufferView: normalView, mimeType: "image/png" });
    builder.json.textures.push({ source: builder.json.images.length - 1, sampler: 0 });
    material.normalTexture = { index: builder.json.textures.length - 1 };
  }
  if (def.emissive && existsSync(def.emissive)) {
    const emissiveImage = readFileSync(def.emissive);
    builder.json.images.push({ bufferView: builder.bufferView(emissiveImage), mimeType: "image/png" });
    builder.json.textures.push({ source: builder.json.images.length - 1, sampler: 0 });
    material.emissiveTexture = { index: builder.json.textures.length - 1 };
    material.emissiveFactor = [1.0, 1.0, 1.0];
  }
  builder.json.materials.push(material);

  builder.json.meshes.push({
    name: def.name,
    primitives: [
      {
        attributes: {
          POSITION: positionAccessor,
          NORMAL: normalAccessor,
          TEXCOORD_0: uvAccessor,
          JOINTS_0: jointAccessor,
          WEIGHTS_0: weightAccessor,
        },
        indices: indexAccessor,
        material: 0,
        mode: 4,
      },
    ],
  });

  // Bone nodes with bind-pose TRS.
  const boneNodeOf = new Array(boneCount);
  const boneNodes = ast.bones.map((bone, index) => {
    const translation = mat4.getTranslation(vec3.create(), skeleton.bindLocal[index]);
    const rotation = quat.normalize(quat.create(), mat4.getRotation(quat.create(), skeleton.bindLocal[index]));
    const scale = mat4.getScaling(vec3.create(), skeleton.bindLocal[index]);
    const node = {
      name: bone.name || `bone_${index}`,
      translation: [translation[0], translation[1], translation[2]],
      rotation: [rotation[0], rotation[1], rotation[2], rotation[3]],
      scale: [scale[0], scale[1], scale[2]],
    };
    boneNodeOf[index] = addNode(builder, node);
  });
  ast.bones.forEach((_, index) => {
    const children = skeleton.childrenOf[index].map((child) => boneNodeOf[child]);
    if (children.length > 0) builder.json.nodes[boneNodeOf[index]].children = children;
  });

  // Mesh node and root transform (PoE is -Z up in centimetres -> Y-up metres).
  const meshNode = addNode(builder, { name: `${def.name}_mesh`, mesh: 0, skin: 0 });
  const rootRotation = quat.setAxisAngle(quat.create(), [1, 0, 0], Math.PI / 2);
  const rootChildren = [];
  for (let index = 0; index < boneCount; index += 1) {
    if (skeleton.parentOf[index] === -1) rootChildren.push(boneNodeOf[index]);
  }
  rootChildren.push(meshNode);
  const rootNode = addNode(builder, {
    name: def.name,
    rotation: [rootRotation[0], rootRotation[1], rootRotation[2], rootRotation[3]],
    scale: [WORLD_SCALE, WORLD_SCALE, WORLD_SCALE],
    children: rootChildren,
  });
  builder.json.scenes[0].nodes = [rootNode];

  // Skin.
  const inverseBind = new Float32Array(boneCount * 16);
  skeleton.invBind.forEach((matrix, index) => {
    inverseBind.set(matrix, index * 16);
  });
  const ibmAccessor = builder.accessor(inverseBind, 5126, "MAT4", boneCount);
  builder.json.skins.push({
    name: `${def.name}_skin`,
    joints: boneNodeOf,
    inverseBindMatrices: ibmAccessor,
    skeleton: rootNode,
  });

  // Animations: idle + walk when available.
  const clips = [
    ["idle", def.idle],
    ["walk", def.walk],
    ["jump", def.jump],
    ["death", def.death],
    ["hit", def.hit],
    ["attack", def.attack],
    ["cast", def.cast],
  ].filter(([, name]) => name);
  for (const [clipName, animName] of clips) {
    const anim = findAnimation(ast, animName);
    if (!anim) {
      console.warn(`  ${def.name}: clip ${animName} not found, skipped`);
      continue;
    }
    const samplers = [];
    const channels = [];
    // One channel set per node: glTF has a single TRS per node, so a bone
    // animated twice loses whichever track is written last. The Elder's rig
    // (the only one measured) ships two tracks per clip for bone 0,
    // `root_jntBnd`: the real motion (`unk1: 0`) and a placeholder
    // (`unk1: 1`) whose scale rows are [0, 0, 0]. Both became channels, the
    // placeholder won, and a zero scale on the root collapsed the whole
    // 225-bone skeleton to a point — the boss rendered nowhere while every
    // renderer/material probe answered fine. The first track of a bone is the
    // one the client animates; later ones are dropped.
    const usedBones = new Set();
    let duplicateTracks = 0;
    for (const track of anim.tracks) {
      const bone = track.header.bone_index;
      if (bone >= boneCount) continue;
      if (usedBones.has(bone)) {
        duplicateTracks += 1;
        continue;
      }
      usedBones.add(bone);
      const node = boneNodeOf[bone];
      const addChannel = (key, path, components) => {
        const rows = track[key];
        if (!rows || rows.length < 2) return;
        const count = rows.length;
        const times = new Float32Array(count);
        const values = new Float32Array(count * components);
        let previous = -1;
        let cursor = 0;
        for (const row of rows) {
          let time = row[0] / FPS;
          if (time <= previous) time = previous + 1 / FPS;
          previous = time;
          times[cursor] = time;
          for (let component = 0; component < components; component += 1) {
            values[cursor * components + component] = row[component + 1] ?? 0;
          }
          cursor += 1;
        }
        const input = builder.accessor(times, 5126, "SCALAR", count, undefined, [times[0]], [times[count - 1]]);
        const output = builder.accessor(
          values,
          5126,
          components === 4 ? "VEC4" : "VEC3",
          count,
        );
        samplers.push({ input, output, interpolation: "LINEAR" });
        channels.push({ sampler: samplers.length - 1, target: { node, path } });
      };
      addChannel("positions", "translation", 3);
      addChannel("rotations", "rotation", 4);
      addChannel("scales", "scale", 3);
    }
    if (duplicateTracks > 0) {
      console.log(
        `  ${def.name}: ${clipName}: ${duplicateTracks} duplicate bone track(s) dropped`,
      );
    }
    if (channels.length > 0) {
      builder.json.animations.push({ name: clipName, samplers, channels });
    }
  }

  mkdirSync(outDir, { recursive: true });
  const output = path.join(outDir, `${def.name}.glb`);
  const glb = builder.build();
  writeFileSync(output, glb);
  const triangles = indices.length / 3;
  console.log(
    `${def.name}: ${vertices.length} verts, ${triangles} tris, ${boneCount} bones, ` +
      `${builder.json.animations.length} clips, ${(glb.length / 1024).toFixed(0)} KB -> ${path.relative(root, output)}`,
  );
}

function weaponMeshData(def, dir) {
  if (def.kind === "sm") {
    const sm = JSON.parse(readFileSync(path.join(dir, def.sm), "utf8"));
    const smdName = `${path.basename(sm.smd_file).replace(/\.smd$/, "")}.smd.json`;
    const smd = JSON.parse(readFileSync(path.join(dir, smdName), "utf8"));
    const { vertices, indices } = vertexData(smd);
    return { vertices, indices, source: "sm" };
  }
  const fmt = JSON.parse(readFileSync(path.join(dir, def.fmt), "utf8"));
  const section = fmt.section.V9 ?? fmt.section.V8 ?? fmt.section.V7;
  const lod = section.dolm?.lods?.[0] ?? section.lods?.[0];
  if (lod) {
    return { vertices: lod.vertices, indices: lod.indices.U16 ?? lod.indices.U32, source: "lod" };
  }
  const vertices = Object.values(section.vertex_buffer);
  const indices = section.index_buffer.U16 ?? section.index_buffer.U32;
  return { vertices, indices, source: "fmt" };
}

function fmtNormal(vertex) {
  const packed = vertex.unk ?? [0, 127, 0, 0];
  const signed = (value) => ((value << 24) >> 24) / 127;
  const x = signed(packed[0]);
  const y = signed(packed[1]);
  const z = signed(packed[2]);
  const length = Math.hypot(x, y, z) || 1;
  return [x / length, y / length, z / length];
}

/**
 * Static weapon GLB: no skin, no clips. The mesh keeps the client's grip at the
 * origin and the shared Rx(+90)/0.01 root, so it slots into the same hand bone
 * transform as the procedural primitives it replaces.
 */
function buildWeapon(id, def) {
  const dir = path.join(raw, "weapons", id);
  const { vertices, indices, source } = weaponMeshData(def, dir);
  const builder = new GlbBuilder();
  const positions = new Float32Array(vertices.length * 3);
  const normals = new Float32Array(vertices.length * 3);
  const uvs = new Float32Array(vertices.length * 2);
  let minX = Infinity;
  let minY = Infinity;
  let minZ = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  let maxZ = -Infinity;

  vertices.forEach((vertex, index) => {
    const [x, y, z] = vertex.pos;
    positions[index * 3] = x * WORLD_SCALE;
    positions[index * 3 + 1] = y * WORLD_SCALE;
    positions[index * 3 + 2] = z * WORLD_SCALE;
    minX = Math.min(minX, x * WORLD_SCALE);
    minY = Math.min(minY, y * WORLD_SCALE);
    minZ = Math.min(minZ, z * WORLD_SCALE);
    maxX = Math.max(maxX, x * WORLD_SCALE);
    maxY = Math.max(maxY, y * WORLD_SCALE);
    maxZ = Math.max(maxZ, z * WORLD_SCALE);
    const [nx, ny, nz] = source === "fmt" ? fmtNormal(vertex) : normalOf(vertex);
    normals[index * 3] = nx;
    normals[index * 3 + 1] = ny;
    normals[index * 3 + 2] = nz;
    const uv = source === "fmt" ? vertex.uv : vertex.tex_coord0;    uvs[index * 2] = uv?.[0] ?? 0;
    uvs[index * 2 + 1] = uv?.[1] ?? 0;
  });

  const positionAccessor = builder.accessor(positions, 5126, "VEC3", vertices.length, 34962, [minX, minY, minZ], [maxX, maxY, maxZ]);
  const normalAccessor = builder.accessor(normals, 5126, "VEC3", vertices.length, 34962);
  const uvAccessor = builder.accessor(uvs, 5126, "VEC2", vertices.length, 34962);
  const indexAccessor = builder.accessor(new Uint16Array(indices), 5123, "SCALAR", indices.length, 34963);

  const image = readFileSync(path.join(dir, def.texture));
  builder.json.images.push({ bufferView: builder.bufferView(image), mimeType: "image/png" });
  builder.json.samplers.push({ magFilter: 9729, minFilter: 9987, wrapS: 10497, wrapT: 10497 });
  builder.json.textures.push({ source: 0, sampler: 0 });
  const material = {
    name: id,
    pbrMetallicRoughness: {
      baseColorTexture: { index: 0 },
      metallicFactor: 0,
      roughnessFactor: 0.8,
    },
    alphaMode: "MASK",
    alphaCutoff: 0.3,
    doubleSided: true,
  };
  if (def.normal) {
    const normalImage = readFileSync(path.join(dir, def.normal));
    builder.json.images.push({ bufferView: builder.bufferView(normalImage), mimeType: "image/png" });
    builder.json.textures.push({ source: 1, sampler: 0 });
    material.normalTexture = { index: 1 };
  }
  builder.json.materials.push(material);
  builder.json.meshes.push({
    name: id,
    primitives: [{
      attributes: { POSITION: positionAccessor, NORMAL: normalAccessor, TEXCOORD_0: uvAccessor },
      indices: indexAccessor,
      material: 0,
      mode: 4,
    }],
  });

  const meshNode = addNode(builder, { name: `${id}_mesh`, mesh: 0 });
  const rootRotation = quat.setAxisAngle(quat.create(), [1, 0, 0], Math.PI / 2);
  const rootNode = addNode(builder, {
    name: id,
    rotation: [rootRotation[0], rootRotation[1], rootRotation[2], rootRotation[3]],
    children: [meshNode],
  });
  builder.json.scenes[0].nodes = [rootNode];

  mkdirSync(weaponOutDir, { recursive: true });
  const output = path.join(weaponOutDir, `${id}.glb`);
  const glb = builder.build();
  writeFileSync(output, glb);
  console.log(
    `${id}: ${vertices.length} verts, ${indices.length / 3} tris, ` +
      `${(glb.length / 1024).toFixed(0)} KB -> ${path.relative(root, output)}`,
  );
}

/** PoE paths compare case-insensitively in the released client. */
function samePath(a, b) {
  return a.toLowerCase() === b.toLowerCase();
}

/**
 * One material's triangles from an effect rig. A client effect mesh carries
 * several shape groups (fireball: black core, flame shell, shockwave flares),
 * each owned by one material from the `.sm`. We keep the requested material's
 * groups and drop the rest, compacting the vertices so unused shapes cost
 * nothing in the GLB.
 */
function effectMeshData(def) {
  const dir = path.join(effectRawDir, def.dir);
  let vertices;
  let indices;
  let source = "sm";

  if (def.kind === "sm") {
    const sm = JSON.parse(readFileSync(path.join(dir, def.sm), "utf8"));
    const smdName = `${path.basename(sm.smd_file).replace(/\.smd$/, "")}.smd.json`;
    const smd = JSON.parse(readFileSync(path.join(dir, smdName), "utf8"));
    const data = vertexData(smd);
    vertices = data.vertices;
    indices = Array.from(data.indices);

    const section = smd.section.V3 ?? smd.section.V2;
    const extents = section.shape_extents ?? [];
    const materialIndex = sm.materials.findIndex((entry) => samePath(entry.mat_file, def.material));
    if (materialIndex < 0) {
      throw new Error(`${def.sm}: material ${def.material} not found`);
    }

    let firstShape = 0;
    for (let index = 0; index < materialIndex; index += 1) firstShape += sm.materials[index].shape_count;
    const lastShape = firstShape + sm.materials[materialIndex].shape_count;
    const firstTriangle = extents[firstShape]?.triangle_index ?? 0;
    const lastTriangle = extents[lastShape] ? extents[lastShape].triangle_index : indices.length / 3;
    indices = indices.slice(firstTriangle * 3, lastTriangle * 3);
  } else {
    source = "fmt";
    const fmt = JSON.parse(readFileSync(path.join(dir, def.fmt), "utf8"));
    const section = fmt.section.V9 ?? fmt.section.V8 ?? fmt.section.V7;
    const lod = section.dolm?.lods?.[0] ?? section.lods?.[0];
    vertices = lod ? lod.vertices : Object.values(section.vertex_buffer);
    const all = lod ? lod.indices.U16 ?? lod.indices.U32 : section.index_buffer.U16 ?? section.index_buffer.U32;
    const shapes = fmt.shapes ?? [];
    const ranges = [];
    if (lod?.shape_extents?.length === shapes.length) {
      shapes.forEach((shape, index) => {
        ranges.push([lod.shape_extents[index].start_index, lod.shape_extents[index].count_index]);
      });
    } else {
      const starts = shapes.map((shape) => shape.triangle_start ?? 0);
      shapes.forEach((shape, index) => {
        const next = index + 1 < shapes.length ? starts[index + 1] : all.length / 3;
        ranges.push([starts[index] * 3, (next - starts[index]) * 3]);
      });
    }

    indices = [];
    shapes.forEach((shape, index) => {
      if (!samePath(shape.material ?? "", def.material)) return;
      const [start, count] = ranges[index];
      indices.push(...all.slice(start, start + count));
    });
  }

  if (indices.length === 0) {
    throw new Error(`${def.dir}: no triangles for material ${def.material}`);
  }

  return { ...compactMesh(vertices, indices), source };
}

/** Drop vertices the selected shapes do not reference and remap the indices. */
function compactMesh(vertices, indices) {
  const remap = new Map();
  const outVertices = [];
  const outIndices = new Array(indices.length);
  indices.forEach((index, cursor) => {
    let target = remap.get(index);
    if (target === undefined) {
      target = outVertices.length;
      remap.set(index, target);
      outVertices.push(vertices[index]);
    }
    outIndices[cursor] = target;
  });
  return { vertices: outVertices, indices: outIndices };
}

/** First colour map of a `.mat.json`, exported beside the material. */
function effectTexture(def) {
  const mat = JSON.parse(readFileSync(path.join(effectRawDir, def.material) + ".json", "utf8"));
  const relative = mat.textures?.[0]?.sources?.[0]?.filename;
  if (!relative) {
    throw new Error(`${def.material}: no colour texture`);
  }
  return path.join(effectRawDir, "art", relative);
}

/**
 * Static effect GLB: one mesh, one material, the shared Rx(+90)/0.01 root.
 * The mesh keeps the client's own shape and UVs; the runtime draws it unlit
 * and additive so the texture's black background contributes nothing.
 */
function buildEffect(id, def) {
  const { vertices, indices, source } = effectMeshData(def);
  const builder = new GlbBuilder();
  const positions = new Float32Array(vertices.length * 3);
  const normals = new Float32Array(vertices.length * 3);
  const uvs = new Float32Array(vertices.length * 2);
  let minX = Infinity;
  let minY = Infinity;
  let minZ = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  let maxZ = -Infinity;

  vertices.forEach((vertex, index) => {
    const [x, y, z] = vertex.pos;
    positions[index * 3] = x * WORLD_SCALE;
    positions[index * 3 + 1] = y * WORLD_SCALE;
    positions[index * 3 + 2] = z * WORLD_SCALE;
    minX = Math.min(minX, x * WORLD_SCALE);
    minY = Math.min(minY, y * WORLD_SCALE);
    minZ = Math.min(minZ, z * WORLD_SCALE);
    maxX = Math.max(maxX, x * WORLD_SCALE);
    maxY = Math.max(maxY, y * WORLD_SCALE);
    maxZ = Math.max(maxZ, z * WORLD_SCALE);
    const [nx, ny, nz] = source === "fmt" ? fmtNormal(vertex) : normalOf(vertex);
    normals[index * 3] = nx;
    normals[index * 3 + 1] = ny;
    normals[index * 3 + 2] = nz;
    const uv = source === "fmt" ? vertex.uv : vertex.tex_coord0;
    uvs[index * 2] = uv?.[0] ?? 0;
    uvs[index * 2 + 1] = uv?.[1] ?? 0;
  });

  const positionAccessor = builder.accessor(positions, 5126, "VEC3", vertices.length, 34962, [minX, minY, minZ], [maxX, maxY, maxZ]);
  const normalAccessor = builder.accessor(normals, 5126, "VEC3", vertices.length, 34962);
  const uvAccessor = builder.accessor(uvs, 5126, "VEC2", vertices.length, 34962);
  const indexAccessor = builder.accessor(new Uint16Array(indices), 5123, "SCALAR", indices.length, 34963);

  const image = readFileSync(effectTexture(def));
  builder.json.images.push({ bufferView: builder.bufferView(image), mimeType: "image/png" });
  builder.json.samplers.push({ magFilter: 9729, minFilter: 9987, wrapS: 10497, wrapT: 10497 });
  builder.json.textures.push({ source: 0, sampler: 0 });
  builder.json.materials.push({
    name: id,
    pbrMetallicRoughness: { baseColorTexture: { index: 0 }, metallicFactor: 0, roughnessFactor: 1 },
    alphaMode: "BLEND",
    doubleSided: true,
    extras: { additive: true },
  });
  builder.json.meshes.push({
    name: id,
    primitives: [{
      attributes: { POSITION: positionAccessor, NORMAL: normalAccessor, TEXCOORD_0: uvAccessor },
      indices: indexAccessor,
      material: 0,
      mode: 4,
    }],
  });

  const meshNode = addNode(builder, { name: `${id}_mesh`, mesh: 0 });
  const rootRotation = quat.setAxisAngle(quat.create(), [1, 0, 0], Math.PI / 2);
  const rootNode = addNode(builder, {
    name: id,
    rotation: [rootRotation[0], rootRotation[1], rootRotation[2], rootRotation[3]],
    children: [meshNode],
  });
  builder.json.scenes[0].nodes = [rootNode];

  mkdirSync(effectOutDir, { recursive: true });
  const output = path.join(effectOutDir, `${id}.glb`);
  const glb = builder.build();
  writeFileSync(output, glb);
  console.log(
    `${id}: ${vertices.length} verts, ${indices.length / 3} tris, ` +
      `${(maxX - minX).toFixed(2)}x${(maxY - minY).toFixed(2)}x${(maxZ - minZ).toFixed(2)} m, ` +
      `${(glb.length / 1024).toFixed(0)} KB -> ${path.relative(root, output)}`,
  );
}

/** Per-shape index ranges of a prop's LOD 0, grouped by material. */
function propMeshData(def) {
  const fmt = JSON.parse(readFileSync(path.join(propRawDir, def.fmt), "utf8"));
  const section = fmt.section.V9 ?? fmt.section.V8 ?? fmt.section.V7;
  const lod = section.dolm?.lods?.[0] ?? section.lods?.[0];
  const vertices = lod ? lod.vertices : Object.values(section.vertex_buffer);
  const buffers = lod ? lod.indices : section.index_buffer;
  const indices = buffers.U16 ?? buffers.U32;
  const shapes = fmt.shapes ?? [];

  const groups = new Map();
  const addRange = (material, start, count) => {
    if (count <= 0) return;
    if (!groups.has(material)) groups.set(material, []);
    groups.get(material).push([start, count]);
  };

  if (lod?.shape_extents?.length === shapes.length) {
    // V9: the LOD carries one index extent per shape, in shape order.
    shapes.forEach((shape, index) => {
      const extent = lod.shape_extents[index];
      addRange(shape.material, extent.start_index, extent.count_index);
    });
  } else {
    const materials = [...new Set(shapes.map((shape) => shape.material))];
    if (materials.length === 1) {
      addRange(materials[0], 0, indices.length);
    } else {
      // V8 without extents: triangle_start only splits the mesh when it rises.
      const starts = shapes.map((shape) => (shape.triangle_start ?? 0) * 3);
      shapes.forEach((shape, index) => {
        const next = starts.filter((value) => value > starts[index]).sort((a, b) => a - b)[0];
        const end = next ?? indices.length;
        addRange(shape.material, starts[index], end - starts[index]);
      });
    }
  }
  if (groups.size === 0) throw new Error(`${def.fmt}: no shape ranges`);
  let entries = [...groups].map(([material, ranges]) => ({ material, ranges }));
  if (Array.isArray(def.materials) && def.materials.length > 0) {
    // Optional material filter: a client scene often bundles the prop with
    // candles, pets and decals; only the names listed here are exported.
    const keep = def.materials.map((name) => name.toLowerCase());
    entries = entries.filter((entry) =>
      keep.some((name) => entry.material.toLowerCase().includes(name.toLowerCase())),
    );
  }
  return { vertices, indices, groups: entries };
}

/** Albedo + normal png for a client material, from the exported .mat.json. */
function propMaterial(materialPath) {
  const jsonPath = path.join(propRawDir, materialPath.toLowerCase() + ".json");
  const mat = existsSync(jsonPath) ? JSON.parse(readFileSync(jsonPath, "utf8")) : {};
  // Textures are listed flat in `textures[]` or inline in the graph parameters.
  const files = [];
  const walk = (node) => {
    if (Array.isArray(node)) {
      node.forEach(walk);
    } else if (node && typeof node === "object") {
      for (const [key, value] of Object.entries(node)) {
        if ((key === "path" || key === "filename") && typeof value === "string" && value.toLowerCase().endsWith(".dds")) {
          files.push(value.toLowerCase());
        } else {
          walk(value);
        }
      }
    }
  };
  walk(mat);
  const toPng = (needle) => {
    const dds = files.find((file) => file.includes(needle));
    if (!dds) return null;
    const png = dds.replace(/\.dds$/, ".png");
    return existsSync(path.join(propRawDir, png)) ? png : null;
  };
  return { colour: toPng("colour"), normal: toPng("normal") };
}

/**
 * A3a: the texture coordinates some client models keep in their packed vertex
 * field — `unk[4..7]` as two little-endian u16, normalised. The exporter writes
 * them into `uv` for most assets, but not for every one (the town stash chest),
 * and the wrong pair samples the empty part of the atlas.
 */
function packedUv(vertex) {
  const packed = vertex.unk;
  if (!packed || packed.length < 8) return vertex.uv ?? [0, 0];
  const low = (packed[4] | (packed[5] << 8)) & 0xffff;
  const high = (packed[6] | (packed[7] << 8)) & 0xffff;
  return [low / 65535, high / 65535];
}

/**
 * A3a: a client chest, built from a skinned `.sm` model instead of an FMT
 * cluster. `props` are normally FMT; the two chests the operator named
 * (`chest31` for the normal ones, `chest7old` for the golden ones) are SMD
 * meshes, so they take this path.
 *
 * The `.sm` lists the materials with a shape count each and the `.smd` lists
 * its shapes with a `triangle_index`; walking both in order gives every
 * triangle its client material (the same grouping the boss atlas uses). One
 * primitive per material, each with that material's own colour map from
 * `def.textures`, feet on y = 0 and the shared Rx(+90) root, exactly like
 * `buildProp`.
 */
function buildSmProp(id, def) {
  const dir = path.join(propRawDir, def.dir);
  const sm = JSON.parse(readFileSync(path.join(dir, def.sm), "utf8"));
  const smdName = `${path.basename(sm.smd_file).replace(/\.smd$/, "")}.smd.json`;
  const smd = JSON.parse(readFileSync(path.join(dir, smdName), "utf8"));
  const base = vertexData(smd);
  const vertices = base.vertices;
  const original = Array.from(base.indices);
  const section = smd.section.V3 ?? smd.section.V2;
  const extents = section.shape_extents ?? [];

  const materialOfShape = [];
  for (let index = 0; index < sm.materials.length; index++) {
    for (let shape = 0; shape < sm.materials[index].shape_count; shape++) {
      materialOfShape.push(index);
    }
  }

  // Every triangle's client material, in the SMD's own triangle order: the
  // shape ranges must be read before a dedupe renumbers them.
  const originalTriangles = original.length / 3;
  const starts = extents.map((shape) => shape.triangle_index);
  const originalMaterial = new Int32Array(originalTriangles);
  for (let shape = 0; shape < starts.length; shape++) {
    const from = starts[shape];
    const to = shape + 1 < starts.length ? starts[shape + 1] : originalTriangles;
    const material = materialOfShape[Math.min(shape, materialOfShape.length - 1)] ?? 0;
    for (let triangle = from; triangle < to && triangle < originalTriangles; triangle++) {
      originalMaterial[triangle] = material;
    }
  }

  // A5.2: the same two-sided trick the Shaper's cape carries (849 coincident
  // reversed pairs on the Izaro pedestal). A two-sided shrine material draws
  // both layers and the inward copy wins at equal depth, which reads as black
  // faces that change with the view angle. Opt-in, like the units' `dedupeLayers`.
  let indices = original;
  let triangleMaterial = originalMaterial;
  if (def.dedupeLayers) {
    const deduped = dropCoincidentTwins(vertices, original);
    indices = deduped.indices;
    triangleMaterial = new Int32Array(indices.length / 3);
    for (let triangle = 0; triangle < triangleMaterial.length; triangle++) {
      triangleMaterial[triangle] = originalMaterial[deduped.kept[triangle] / 3];
    }
    console.log(
      `  ${id}: ${deduped.dropped} coincident two-sided triangle(s) dropped ` +
        `(one layer each: the client culls its twin)`,
    );
  }
  const triangles = indices.length / 3;

  const builder = new GlbBuilder();
  const positions = new Float32Array(vertices.length * 3);
  const normals = new Float32Array(vertices.length * 3);
  const uvs = new Float32Array(vertices.length * 2);
  let minX = Infinity, minY = Infinity, minZ = Infinity;
  let maxX = -Infinity, maxY = -Infinity, maxZ = -Infinity;

  // `yUp`: the client meshes are Z-up and the world props carry the shared
  // Rx(+90) root. The opening shot hands the mesh straight to the game's own
  // `SkinnedMeshRenderer`, which has no such root, so this variant bakes the
  // same rotation into the vertices and ships no root rotation at all.
  const rotate = def.yUp
    ? (x, y, z) => [x, -z, y]
    : (x, y, z) => [x, y, z];

  vertices.forEach((vertex, index) => {
    const [px, py, pz] = rotate(vertex.pos[0], vertex.pos[1], vertex.pos[2]);
    const x = px * WORLD_SCALE, y = py * WORLD_SCALE, z = pz * WORLD_SCALE;
    positions.set([x, y, z], index * 3);
    minX = Math.min(minX, x);
    maxX = Math.max(maxX, x);
    minY = Math.min(minY, y);
    maxY = Math.max(maxY, y);
    minZ = Math.min(minZ, z);
    maxZ = Math.max(maxZ, z);
    const [nx, ny, nz] = rotate(normalOf(vertex)[0], normalOf(vertex)[1], normalOf(vertex)[2]);
    normals.set([nx, ny, nz], index * 3);
    const uv = vertex.tex_coord0 ?? [0, 0];
    uvs.set([uv[0] ?? 0, uv[1] ?? 0], index * 2);
  });

  const positionAccessor = builder.accessor(positions, 5126, "VEC3", vertices.length, 34962, [minX, minY, minZ], [maxX, maxY, maxZ]);
  const normalAccessor = builder.accessor(normals, 5126, "VEC3", vertices.length, 34962);
  const uvAccessor = builder.accessor(uvs, 5126, "VEC2", vertices.length, 34962);

  const primitives = [];
  for (let material = 0; material < sm.materials.length; material++) {
    const owned = [];
    for (let triangle = 0; triangle < triangles; triangle++) {
      if (triangleMaterial[triangle] !== material) continue;
      owned.push(indices[triangle * 3], indices[triangle * 3 + 1], indices[triangle * 3 + 2]);
    }

    if (owned.length === 0) continue;

    const indexAccessor = builder.accessor(new Uint32Array(owned), 5125, "SCALAR", owned.length, 34963);
    const texture = def.textures?.[material];
    const gltfMaterial = {
      name: `${id}_${primitives.length}`,
      pbrMetallicRoughness: { metallicFactor: 0, roughnessFactor: 0.9 },
      alphaMode: "OPAQUE",
      doubleSided: true,
    };

    if (texture && existsSync(path.join(propRawDir, texture))) {
      const image = readFileSync(path.join(propRawDir, texture));
      builder.json.images.push({ bufferView: builder.bufferView(image), mimeType: "image/png" });
      builder.json.textures.push({ source: builder.json.images.length - 1, sampler: 0 });
      gltfMaterial.pbrMetallicRoughness.baseColorTexture = { index: builder.json.textures.length - 1 };
    } else {
      console.warn(`  ${id}: no colour texture for material ${material} (${sm.materials[material].mat_file})`);
      gltfMaterial.pbrMetallicRoughness.baseColorFactor = [0.45, 0.45, 0.45, 1.0];
    }

    builder.json.materials.push(gltfMaterial);
    primitives.push({
      attributes: { POSITION: positionAccessor, NORMAL: normalAccessor, TEXCOORD_0: uvAccessor },
      indices: indexAccessor,
      material: builder.json.materials.length - 1,
      mode: 4,
    });
  }

  builder.json.samplers.push({ magFilter: 9729, minFilter: 9987, wrapS: 10497, wrapT: 10497 });
  builder.json.meshes.push({ name: id, primitives });

  const meshNode = addNode(builder, { name: `${id}_mesh`, mesh: 0 });
  const rootDefinition = { name: id, children: [meshNode] };
  if (!def.yUp) {
    // The props' shared root: the client meshes are Z-up.
    const rootRotation = quat.setAxisAngle(quat.create(), [1, 0, 0], Math.PI / 2);
    rootDefinition.rotation = [rootRotation[0], rootRotation[1], rootRotation[2], rootRotation[3]];
  }
  const rootNode = addNode(builder, rootDefinition);
  builder.json.scenes[0].nodes = [rootNode];

  mkdirSync(propOutDir, { recursive: true });
  const output = path.join(propOutDir, `${id}.glb`);
  const glb = builder.build();
  writeFileSync(output, glb);
  console.log(
    `${id}: ${vertices.length} verts, ${triangles} tris, ${primitives.length} materials, ` +
      `${(maxX - minX).toFixed(2)}x${(maxY - minY).toFixed(2)}x${(maxZ - minZ).toFixed(2)} m, ` +
      `${(glb.length / 1024).toFixed(0)} KB -> ${path.relative(root, output)}`,
  );
}

/** Static prop GLB: one primitive per material, feet on y = 0, no skin. */
function buildProp(id, def) {
  if (def.kind === "sm") {
    buildSmProp(id, def);
    return;
  }

  const { vertices, indices, groups } = propMeshData(def);
  const builder = new GlbBuilder();

  const positions = new Float32Array(vertices.length * 3);
  const normals = new Float32Array(vertices.length * 3);
  const uvs = new Float32Array(vertices.length * 2);
  let minX = Infinity;
  let minY = Infinity;
  let minZ = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  let maxZ = -Infinity;

  vertices.forEach((vertex, index) => {
    const [x, y, z] = vertex.pos;
    positions.set([x * WORLD_SCALE, y * WORLD_SCALE, z * WORLD_SCALE], index * 3);
    minX = Math.min(minX, x * WORLD_SCALE);
    minY = Math.min(minY, y * WORLD_SCALE);
    minZ = Math.min(minZ, z * WORLD_SCALE);
    maxX = Math.max(maxX, x * WORLD_SCALE);
    maxY = Math.max(maxY, y * WORLD_SCALE);
    maxZ = Math.max(maxZ, z * WORLD_SCALE);
    const normal = vertex.normal ? normalOf(vertex) : fmtNormal(vertex);
    normals.set(normal, index * 3);
    // A3a: some client models carry their real texture coordinates in the
    // vertex's packed field, not in the exporter's `uv` (the Lioneye's Watch
    // stash is one: its `uv` lands in the empty part of its own atlas, 76 % of
    // the surface on transparent texels, while `unk[4..7]` read as two u16 is
    // 98 % on painted ones). `uv: "unk16"` selects that reading for a model.
    const uv = def.uv === "unk16" ? packedUv(vertex) : vertex.uv ?? vertex.tex_coord0 ?? [0, 0];
    uvs.set([uv[0] ?? 0, uv[1] ?? 0], index * 2);
  });

  const positionAccessor = builder.accessor(positions, 5126, "VEC3", vertices.length, 34962, [minX, minY, minZ], [maxX, maxY, maxZ]);
  const normalAccessor = builder.accessor(normals, 5126, "VEC3", vertices.length, 34962);
  const uvAccessor = builder.accessor(uvs, 5126, "VEC2", vertices.length, 34962);

  const primitives = [];
  for (const group of groups) {
    // Shadow-catcher planes ("black_shadowonlyc") are invisible in game.
    if (group.material.toLowerCase().includes("shadowonly")) continue;
    const ranges = group.ranges;
    const count = ranges.reduce((total, [, length]) => total + length, 0);
    const indices32 = new Uint32Array(count);
    let cursor = 0;
    for (const [start, length] of ranges) {
      indices32.set(indices.slice(start, start + length), cursor);
      cursor += length;
    }
    const indexAccessor = builder.accessor(indices32, 5125, "SCALAR", count, 34963);

    const files = propMaterial(group.material);
    if (!files.colour) console.warn(`  ${id}: no colour texture for ${group.material}`);
    const material = {
      name: `${id}_${primitives.length}`,
      pbrMetallicRoughness: { metallicFactor: 0, roughnessFactor: 0.9 },
      alphaMode: files.colour?.includes("leaves") ? "MASK" : "OPAQUE",
      alphaCutoff: 0.4,
      doubleSided: true,
    };
    if (files.colour) {
      const image = readFileSync(path.join(propRawDir, files.colour));
      builder.json.images.push({ bufferView: builder.bufferView(image), mimeType: "image/png" });
      builder.json.textures.push({ source: builder.json.images.length - 1, sampler: 0 });
      material.pbrMetallicRoughness.baseColorTexture = { index: builder.json.textures.length - 1 };
    } else {
      material.pbrMetallicRoughness.baseColorFactor = [0.45, 0.45, 0.45, 1.0];
    }
    if (files.normal) {
      const image = readFileSync(path.join(propRawDir, files.normal));
      builder.json.images.push({ bufferView: builder.bufferView(image), mimeType: "image/png" });
      builder.json.textures.push({ source: builder.json.images.length - 1, sampler: 0 });
      material.normalTexture = { index: builder.json.textures.length - 1 };
    }
    builder.json.materials.push(material);
    primitives.push({
      attributes: { POSITION: positionAccessor, NORMAL: normalAccessor, TEXCOORD_0: uvAccessor },
      indices: indexAccessor,
      material: builder.json.materials.length - 1,
      mode: 4,
    });
  }
  builder.json.samplers.push({ magFilter: 9729, minFilter: 9987, wrapS: 10497, wrapT: 10497 });

  builder.json.meshes.push({ name: id, primitives });
  const meshNode = addNode(builder, { name: `${id}_mesh`, mesh: 0 });
  const rootRotation = quat.setAxisAngle(quat.create(), [1, 0, 0], Math.PI / 2);
  const rootNode = addNode(builder, {
    name: id,
    rotation: [rootRotation[0], rootRotation[1], rootRotation[2], rootRotation[3]],
    children: [meshNode],
  });
  builder.json.scenes[0].nodes = [rootNode];

  mkdirSync(propOutDir, { recursive: true });
  const output = path.join(propOutDir, `${id}.glb`);
  const glb = builder.build();
  writeFileSync(output, glb);
  console.log(
    `${id}: ${vertices.length} verts, ${indices.length / 3} tris, ${primitives.length} materials, ` +
      `${(maxX - minX).toFixed(2)}x${(maxY - minY).toFixed(2)}x${(maxZ - minZ).toFixed(2)} m, ` +
      `${(glb.length / 1024).toFixed(0)} KB -> ${path.relative(root, output)}`,
  );
}

const requested = process.argv.slice(2);
if (requested.includes("--props")) {
  for (const [id, def] of Object.entries(PROPS)) {
    buildProp(id, def);
  }
} else if (requested.includes("--weapons")) {
  for (const [id, def] of Object.entries(WEAPONS)) {
    buildWeapon(id, def);
  }
} else if (requested.includes("--effects")) {
  for (const [id, def] of Object.entries(EFFECTS)) {
    buildEffect(id, def);
  }
} else {
  const definitions = unitDefinitions(requested);
  if (definitions.length === 0) {
    console.error(`No unit matched: ${requested.join(", ")}`);
    process.exitCode = 1;
  } else {
    for (const def of definitions) {
      if (!existsSync(def.texture)) {
        console.error(`Missing texture for ${def.name}: ${def.texture}`);
        process.exitCode = 1;
        continue;
      }
      buildUnit(def);
    }
  }
}
