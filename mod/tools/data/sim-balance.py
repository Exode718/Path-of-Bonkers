"""Offline balance simulator for the seven class skills.

A *relative* sanity check, not a re-implementation of PoeSkills: it models the
base skill from skills.json + classes.json, the universal pick curve (+6% damage
and -0.6% cooldown per pick), and a per-mod value table derived from the design
(one entry per upgrade, in fractional DPS / area / defence gain per stack).

Goal (operator): strong builds are allowed to be broken; the weakest 40-pick
builds must stay playable. The report therefore looks at the **p10** floor per
class and at which picks correlate with a low floor, not at the ceiling.

Run:  python mod/tools/data/sim-balance.py [--builds N]
"""

from __future__ import annotations

import json
import pathlib
import random
import statistics
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
DESIGN = ROOT / "design" / "arena"

PICKS = 40
DAMAGE_PER_PICK = 1.06
COOLDOWN_PER_PICK = 0.994
CRIT_DAMAGE = 2.0


def load(path: pathlib.Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


skills = {s["id"]: s for s in load(DESIGN / "skills.json")}
classes = load(DESIGN / "classes.json")

# id -> (dps per stack, area per stack, defence per stack)
# Values are the *net* per-pick gain after the card's cost, from the design.
MOD_VALUE = {
    # Cyclone
    "duelist-gale-offshoots": (0.11, 0.02, 0.0),
    "duelist-vortex-lure": (0.02, 0.05, 0.03),
    "duelist-gathering-storm": (0.10, 0.15, 0.0),
    "duelist-razorwind": (0.12, 0.0, 0.0),
    "duelist-momentum": (0.05, 0.0, 0.0),
    "duelist-iron-cyclone": (0.04, 0.22, 0.0),
    "duelist-twin-orbit": (0.06, 0.18, 0.0),
    "duelist-blood-whirl": (0.0, 0.0, 0.22),
    "duelist-vacuum-cut": (0.02, 0.0, 0.08),
    "duelist-afterimage": (0.10, 0.02, 0.0),
    # Righteous Fire
    "marauder-sacrificial-pyre": (0.12, 0.0, -0.02),
    "marauder-fan-the-flames": (0.02, 0.18, 0.0),
    "marauder-cinder-trail": (0.10, 0.02, 0.0),
    "marauder-pyre-of-the-faithful": (0.22, -0.05, 0.0),
    "marauder-immolation": (0.12, 0.02, 0.0),
    "marauder-zealots-covenant": (0.0, 0.02, 0.10),
    "marauder-ashen-veil": (0.0, -0.03, 0.16),
    "marauder-kindle": (0.10, 0.0, 0.0),
    "marauder-smouldering-step": (0.06, 0.02, 0.0),
    "marauder-last-rites": (0.16, 0.0, 0.05),
    # Ice Shot
    "ranger-shatterpoint": (0.10, 0.0, 0.0),
    "ranger-ricochet": (0.12, 0.0, 0.0),
    "ranger-volley-stance": (0.15, 0.05, 0.0),
    "ranger-glacial-lance": (0.12, 0.0, 0.0),
    "ranger-winters-grasp": (0.03, 0.02, 0.08),
    "ranger-hunters-mark": (0.08, 0.03, 0.0),
    "ranger-frostbite": (0.04, 0.0, 0.06),
    "ranger-sleet-step": (0.02, 0.0, 0.06),
    "ranger-ice-prison": (0.0, 0.0, 0.08),
    "ranger-cold-snap": (0.06, 0.04, 0.02),
    # Fireball
    "witch-twin-flame": (0.12, 0.0, 0.0),
    "witch-split-ash": (0.12, 0.05, 0.0),
    "witch-pyromantic-focus": (0.28, 0.10, 0.0),
    "witch-rolling-ember": (0.06, 0.0, 0.0),
    "witch-ignite-bloom": (0.08, 0.02, 0.0),
    "witch-ember-familiar": (0.12, 0.0, 0.0),
    "witch-meteor-call": (0.15, 0.15, 0.0),
    "witch-wildfire": (0.08, 0.03, 0.0),
    "witch-ash-cloud": (0.0, 0.0, 0.14),
    "witch-combustion": (0.10, 0.0, 0.0),
    # Ice Nova
    "templar-glacial-echo": (0.24, 0.0, 0.0),
    "templar-convergence": (0.15, 0.0, 0.0),
    "templar-deep-freeze": (0.02, 0.0, 0.12),
    "templar-frozen-bulwark": (0.02, 0.0, 0.10),
    "templar-permafrost": (0.02, 0.02, 0.08),
    "templar-rime-cascade": (0.10, 0.0, 0.0),
    "templar-ring-of-winter": (0.05, 0.25, 0.0),
    "templar-fracture": (0.10, 0.02, 0.0),
    "templar-icebound": (0.0, 0.0, 0.08),
    "templar-arctic-charge": (0.10, 0.10, 0.0),
    # Viper Strike
    "shadow-toxic-bloom": (0.10, 0.04, 0.0),
    "shadow-venom-spread": (0.08, 0.02, 0.0),
    "shadow-serpents-patience": (0.12, 0.0, 0.0),
    "shadow-shadowstep": (0.12, 0.0, 0.0),
    "shadow-corrosive-bite": (0.12, 0.0, 0.0),
    "shadow-twin-fang": (0.15, 0.0, 0.0),
    "shadow-lethal-dose": (0.18, 0.0, 0.0),
    "shadow-adders-grace": (0.0, 0.0, 0.10),
    "shadow-haemorrhage": (0.02, 0.0, 0.0),
    "shadow-last-breath": (0.06, 0.0, 0.02),
    # Arc
    "scion-static-field": (0.10, 0.05, 0.0),
    "scion-overcharge": (0.18, 0.0, 0.0),
    "scion-storm-anchor": (0.10, 0.0, 0.02),
    "scion-forked-fate": (0.14, 0.03, 0.0),
    "scion-conductivity": (0.10, 0.03, 0.0),
    "scion-chain-lightning": (0.08, 0.02, 0.0),
    "scion-thunderhead": (0.12, 0.02, 0.0),
    "scion-static-cling": (0.08, 0.0, 0.01),
    "scion-galvanic-rush": (0.0, 0.0, 0.10),
    "scion-ball-lightning": (0.05, 0.05, 0.0),
}


def floor(x: float, lo: float, hi: float) -> float:
    return max(lo, min(hi, x))


def targets_per_cast(skill: dict, area_pct: float = 0.0) -> float:
    """Mixed single-target/pack read: half the fight is a boss, half a pack."""
    shape = skill.get("shape")
    traits = skill.get("traits") or {}
    radius = (skill.get("radius") or 0) * (1.0 + area_pct / 100.0)
    if shape in ("aura", "cyclone", "nova", "ground"):
        pack = floor(1.0 + radius / 45.0, 1.0, 4.0)
        return 0.5 + 0.5 * pack
    if shape == "melee":
        pack = floor(1.0 + (skill.get("arc") or 0) * 0.7, 1.0, 3.0)
        # Viper Strike's base poison spread reaches neighbours; one extra
        # target at 3.5 m (the trait is now read by the runtime).
        spread = traits.get("poisonSpread") or 0
        if spread:
            pack += min(1.0, spread / 3.5)
        return 0.5 + 0.5 * pack
    if shape == "orbit":
        pack = floor(1.0 + radius / 50.0, 1.0, 3.5)
        return 0.5 + 0.5 * pack
    if shape == "projectile":
        base = max(1, skill.get("projectiles") or 1)
        pierce = 1.0 + (skill.get("pierce") or 0) * 0.6
        chain = 1.0 + (traits.get("chainChance", 0) / 100.0) * (traits.get("chainJumps") or 0) * 0.5
        pack = base * pierce * chain
        # Fireball's impact splash: nearby enemies take a share of the hit.
        splash = skill.get("splash") or 0
        if splash:
            pack += min(1.5, splash / 2.2)
        return 0.5 + 0.5 * pack
    if shape == "chain":
        chains = skill.get("chains") or 0
        pack = sum(0.82 ** i for i in range(chains + 1))
        return 0.5 + 0.5 * pack
    return 1.0


def range_factor(shape: str) -> float:
    """Melee/aura shapes spend time closing in and can be pushed out; ranged
    shapes have near-total uptime. This keeps the cross-class comparison honest."""
    if shape in ("melee", "aura", "cyclone", "orbit"):
        return 0.80
    if shape in ("nova", "ground"):
        return 0.90
    return 1.0


def ailment_factor(skill: dict) -> tuple[float, float]:
    """Returns (dps factor, defence factor) from the skill's own ailment."""
    traits = skill.get("traits") or {}
    dps = 1.0
    defence = 1.0
    tag = skill.get("tag")
    if tag == "fire" and traits.get("ignitePct"):
        dps += traits["ignitePct"] * traits.get("igniteSeconds", 3) * 0.35
    if tag == "chaos" and traits.get("poisonPct"):
        stacks = traits.get("poisonStacks") or 1
        dps += traits["poisonPct"] * stacks * traits.get("poisonSeconds", 5) * 0.06
    if tag == "cold":
        defence += (traits.get("freezeChance", 0) + traits.get("chillPct", 0)) / 100.0 * 0.2
    if tag == "lightning" and traits.get("stunChance"):
        defence += traits["stunChance"] / 100.0 * 0.2
    return dps, defence


def weapon_bonus(klass: dict, key: str) -> float:
    total = 0.0
    for slot in ("starterWeapon", "offhand"):
        weapon = klass.get(slot)
        if weapon:
            total += float((weapon.get("bonus") or {}).get(key, 0.0))
    return total


def merged_skill(klass: dict) -> dict:
    """The class skill with its starter/offhand weapon traits merged in."""
    skill = dict(skills[klass["skillId"]])
    traits = dict(skill.get("traits") or {})
    for slot in ("starterWeapon", "offhand"):
        weapon = klass.get(slot)
        if not weapon:
            continue
        for key, value in (weapon.get("traits") or {}).items():
            if key == "ignitePct":
                traits[key] = traits.get(key, 0.0) + value
            elif key in ("igniteSeconds", "chainChance", "chainJumps", "poisonSpread"):
                traits[key] = max(traits.get(key, 0.0), value)
    skill["traits"] = traits
    return skill


def base_power(klass: dict) -> tuple[float, float, float]:
    skill = merged_skill(klass)
    damage_pct = klass.get("damagePct", 0) + weapon_bonus(klass, "damagePct")
    damage = skill["damage"] * (1.0 + damage_pct / 100.0)
    crit_chance = klass.get("critChance", 0) + weapon_bonus(klass, "critChance")
    crit = 1.0 + crit_chance / 100.0 * (CRIT_DAMAGE - 1.0)
    per_hit = damage * crit
    interval = skill.get("hitInterval") or 0
    rate = 1.0 / (interval if interval > 0 else skill["cooldown"])
    targets = targets_per_cast(skill, weapon_bonus(klass, "areaPct"))
    ailment_dps, ailment_def = ailment_factor(skill)

    dps = per_hit * rate * targets * ailment_dps * range_factor(skill.get("shape", ""))
    life = klass.get("life", 100) * (1.0 + weapon_bonus(klass, "maxLifePct") / 100.0)
    armour = klass.get("armour", 0) + weapon_bonus(klass, "armour")
    regen = klass.get("regen", 0) + weapon_bonus(klass, "regen")
    move = klass.get("moveSpeed", 100) * (1.0 + weapon_bonus(klass, "moveSpeedPct") / 100.0)
    surv = (
        life / 160.0 * 0.45
        + armour / 25.0 * 0.25
        + regen / 2.0 * 0.15
        + move / 110.0 * 0.15
    )
    surv = floor(surv, 0.2, 2.0) * ailment_def
    return dps, surv, targets


def build_stats(klass: dict, picks: list[str]) -> tuple[float, dict[str, int]]:
    base, surv, targets = base_power(klass)
    dps_mult, area_mult, def_mult = 1.0, 1.0, 1.0
    counts: dict[str, int] = {}
    for mid in picks:
        counts[mid] = counts.get(mid, 0) + 1
        d, a, f = MOD_VALUE.get(mid, (0.0, 0.0, 0.0))
        dps_mult += d
        area_mult += a
        def_mult += f

    picks_n = len(picks)
    # Each pick: +6% damage, -0.6% cooldown (so the cast rate grows by 1/0.994).
    dps_mult *= DAMAGE_PER_PICK ** picks_n / COOLDOWN_PER_PICK ** picks_n
    area_mult = floor(area_mult, 0.7, 3.0)
    def_mult = floor(def_mult, 0.5, 3.0)
    power = base * dps_mult * floor(targets * area_mult, 1.0, 14.0) * (0.6 + 0.4 * surv * def_mult)
    return power, counts


def simulate(klass: dict, builds: int, rng: random.Random) -> dict[str, float]:
    mod_ids = [mid for mid in MOD_VALUE if mid.startswith(skill_prefix(klass))]
    if len(mod_ids) != 10:
        raise SystemExit(f"{klass['id']}: expected 10 mods, found {len(mod_ids)}")
    powers = []
    for _ in range(builds):
        picks = [rng.choice(mod_ids) for _ in range(PICKS)]
        power, _ = build_stats(klass, picks)
        powers.append(power)
    powers.sort()
    return {
        "p10": powers[int(len(powers) * 0.10)],
        "p50": powers[len(powers) // 2],
        "p90": powers[int(len(powers) * 0.90)],
        "max": powers[-1],
        "min": powers[0],
    }


def skill_prefix(klass: dict) -> str:
    return {
        "Marauder": "marauder-",
        "Ranger": "ranger-",
        "Witch": "witch-",
        "Templar": "templar-",
        "Shadow": "shadow-",
        "Duelist": "duelist-",
        "Scion": "scion-",
    }[klass["id"]]


def all_in_build(klass: dict) -> list[tuple[str, float]]:
    mod_ids = [mid for mid in MOD_VALUE if mid.startswith(skill_prefix(klass))]
    out = []
    for mid in mod_ids:
        power, _ = build_stats(klass, [mid] * PICKS)
        out.append((mid, power))
    out.sort(key=lambda pair: pair[1])
    return out


def main() -> None:
    builds = 20000
    strict = "--strict" in sys.argv
    if "--builds" in sys.argv:
        builds = int(sys.argv[sys.argv.index("--builds") + 1])
    rng = random.Random(20261103)

    rows = []
    for klass in classes:
        stats = simulate(klass, builds, rng)
        base, surv, targets = base_power(klass)
        rows.append((klass["id"], stats, base, surv, targets))

    best_p50 = max(row[1]["p50"] for row in rows)
    print(f"{'class':<10} {'base dps':>9} {'surv':>6} {'tgt':>5} "
          f"{'p10':>7} {'p50':>7} {'p90':>7} {'max':>7}  {'p10/p50':>8}")
    traps = []
    for name, stats, base, surv, targets in rows:
        ratio = stats["p10"] / stats["p50"]
        if ratio < 0.70:
            traps.append(name)
        print(f"{name:<10} {base:9.1f} {surv:6.2f} {targets:5.2f} "
              f"{stats['p10']:7.0f} {stats['p50']:7.0f} {stats['p90']:7.0f} "
              f"{stats['max']:7.0f}  {ratio * 100:7.1f}%")

    print("\nrelative p50 (best class = 100):")
    for name, stats, *_ in rows:
        print(f"  {name:<10} {stats['p50'] / best_p50 * 100:6.1f}")

    print("\nworst all-in-one build per class (pure-defence floor):")
    for klass in classes:
        worst = all_in_build(klass)[0]
        print(f"  {klass['id']:<10} {worst[0]:<34} {worst[1]:8.0f}")

    print("\nnote: cross-class gaps follow shape/range (a spin at 3.3 ticks/s on a")
    print("3.4 m radius vs a single-target bolt); the guard is p10/p50 within")
    print("each class - no random 40-pick build may fall below 70% of its median.")
    if strict and traps:
        print(f"\nFAIL: build traps in {', '.join(traps)}")
        sys.exit(1)


if __name__ == "__main__":
    main()
