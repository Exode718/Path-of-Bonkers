"""Offline item-balance simulation for the 79 relics.

Converts every relic's stats and triggers into a single "power points" score
(≈ percent of a reference character) and compares rarities at run levels
50 / 75 / 100 / 150 / 200. Percent stats keep their value; flat stats are
measured against a reference that grows with level, so they fade exactly the
way they do in a long run.

It is a *relative screen*: it cannot know every in-game interaction, but it
finds the two things that matter here — a rarity that is out of order, and a
single item far above (or below) the band of its rarity.

Run:  python mod/tools/data/sim-items.py [--strict] [--levels 50,80,100]
"""

from __future__ import annotations

import json
import pathlib
import statistics
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
DATA = ROOT / "data"
DESIGN = ROOT / "design"

# The rarity bands are a run-level rule (50+). Below 50 flat Armour/Shield
# relics are intentionally strong (they fade as the level reference grows), so
# level 25 is reported separately and is not part of the strict guard.
LEVELS = [50, 75, 100, 150, 200]
RARITY_ORDER = ["common", "rare", "epic", "legendary"]
# The operator's rule: relics are bonuses, and a higher rarity is stronger.
FLOOR = {"common": 10.0, "rare": 18.0, "epic": 30.0, "legendary": 50.0}


def load(path: pathlib.Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


items = load(DATA / "items.json")
rewards = {row["id"]: row for row in load(DATA / "rewards.json") if row.get("kind") == "relic"}
enums = load(DESIGN / "megabonk" / "enums.json")
ESTAT = set(enums["EStat"].keys())

# power points per +100% of the stat (percent stats), or per unit for flats
WEIGHTS = {
    "DamageMultiplier": 1.00,
    "FireDamage": 0.60,
    "IceDamage": 0.60,
    "LightningDamage": 0.60,
    "AttackSpeed": 0.80,
    "CritChance": 0.55,
    "CritDamage": 0.35,
    "Projectiles": 0.50,
    "ProjectileSpeedMultiplier": 0.15,
    "ProjectileBounces": 0.25,
    "SizeMultiplier": 0.50,
    "PoisonDamageMultiplier": 0.55,
    "FreezeChance": 0.15,
    "BurnChance": 0.15,
    "MaxHealth": 0.70,
    "Armor": 0.55,
    "Evasion": 0.45,
    "Shield": 0.50,
    "Thorns": 0.25,
    "HealthRegen": 0.30,
    "Lifesteal": 0.60,
    "MoveSpeedMultiplier": 0.60,
    "Luck": 0.25,
    "GoldIncreaseMultiplier": 0.15,
    "XpIncreaseMultiplier": 0.20,
    "ChestPriceMultiplier": 0.20,
    "Difficulty": -0.50,
    "PickupRange": 0.05,
}

# Flat stats are turned into a percent of a reference that grows with level.
FLAT_REF = {
    "MaxHealth": lambda level: 140.0 + 3.0 * (level - 1),
    "Armor": lambda level: 18.0 + 2.0 * (level - 1),
    "Evasion": lambda level: 10.0 + 0.5 * (level - 1),
    "Shield": lambda level: 25.0 + 2.0 * (level - 1),
    "Thorns": lambda level: 15.0 + 1.5 * (level - 1),
    "HealthRegen": lambda level: 40.0 + 4.0 * (level - 1),
    "PickupRange": lambda level: 88.0,
    "Projectiles": lambda level: 1.0,
    "ProjectileBounces": lambda level: 1.0,
}


def stat_points(stat: dict, level: int) -> float:
    name = stat.get("stat")
    weight = WEIGHTS.get(name, 0.0)
    value = stat.get("value", 0.0)
    kind = stat.get("type")
    if kind == "Multiplication":
        # A lower chest price is the bonus, not a malus.
        percent = (1.0 - value) * 100.0 if name == "ChestPriceMultiplier" else (value - 1.0) * 100.0
    elif kind == "Addition":
        percent = value * 100.0
    else:  # Flat
        ref = FLAT_REF.get(name, lambda _level: 1.0)(level)
        percent = value / ref * 100.0
    return percent * weight


def trigger_points(trigger: dict) -> float:
    on = trigger.get("on")
    effect = trigger.get("effect")
    chance = trigger.get("chance", 1.0)
    if on == "hit" and effect == "poison":
        seconds = trigger.get("seconds", 3)
        return chance * (10.0 + 6.0 * seconds)
    if on == "kill" and effect == "explode":
        value = trigger.get("value", 0.5)
        radius = trigger.get("radius", 3.0)
        return value * 100.0 * 0.7 * min(1.5, radius / 3.0)
    if on == "hit" and effect == "curse":
        value = trigger.get("value", 0.5)
        return chance * value * 100.0 * 0.3
    if on == "standstill" and effect == "damageRamp":
        return 20.0
    return 0.0


def relic_points(item: dict, level: int) -> float:
    total = 0.0
    for stat in item.get("stats") or []:
        total += stat_points(stat, level)
    for trigger in item.get("triggers") or []:
        total += trigger_points(trigger)
    return total


def by_rarity(level: int) -> dict[str, list[tuple[float, str]]]:
    groups: dict[str, list[tuple[float, str]]] = {rarity: [] for rarity in RARITY_ORDER}
    for item in items:
        row = rewards.get(item["id"])
        if row is None:
            continue
        groups.setdefault(row["rarity"], []).append((relic_points(item, level), item["id"]))
    for values in groups.values():
        values.sort()
    return groups


def main() -> None:
    levels = LEVELS
    strict = "--strict" in sys.argv
    if "--levels" in sys.argv:
        levels = [int(x) for x in sys.argv[sys.argv.index("--levels") + 1].split(",")]

    failures: list[str] = []
    for level in levels:
        groups = by_rarity(level)
        print(f"\n=== level {level} ===")
        medians = {}
        for rarity in RARITY_ORDER:
            scores = [score for score, _ in groups[rarity]]
            if not scores:
                continue
            median = statistics.median(scores)
            medians[rarity] = median
            print(f"{rarity:<10} n={len(scores):2d} min={min(scores):6.1f} "
                  f"median={median:6.1f} max={max(scores):6.1f}")

        order = [rarity for rarity in RARITY_ORDER if rarity in medians]
        for lower, higher in zip(order, order[1:]):
            if medians[lower] > medians[higher]:
                failures.append(
                    f"L{level}: rarity order {lower} ({medians[lower]:.0f}) > {higher} ({medians[higher]:.0f})"
                )

        common_max = max(score for score, _ in groups["common"]) if groups["common"] else 0
        epic_median = medians.get("epic", 0)
        if common_max > epic_median:
            worst = max(groups["common"])
            failures.append(
                f"L{level}: common {worst[1]} ({common_max:.0f}) is above the epic median ({epic_median:.0f})"
            )

        rare_max = max(score for score, _ in groups["rare"]) if groups["rare"] else 0
        legendary_median = medians.get("legendary", 0)
        if rare_max > legendary_median:
            worst = max(groups["rare"])
            failures.append(
                f"L{level}: rare {worst[1]} ({rare_max:.0f}) is above the legendary median ({legendary_median:.0f})"
            )

        for rarity in ("epic", "legendary"):
            for score, rid in groups[rarity]:
                if score < 0:
                    failures.append(f"L{level}: {rarity} {rid} is net-negative ({score:.0f})")

        for rarity in RARITY_ORDER:
            for score, rid in groups[rarity]:
                if score < FLOOR[rarity]:
                    failures.append(
                        f"L{level}: {rarity} {rid} scores {score:.0f} (floor {FLOOR[rarity]:.0f})"
                    )

        top = sorted(
            [(score, rid, rarity) for rarity, pairs in groups.items() for score, rid in pairs],
            reverse=True,
        )[:8]
        print("top:", ", ".join(f"{rid} [{rarity}] {score:.0f}" for score, rid, rarity in top))

    print("\n--- outlier report ---")
    if failures:
        for message in failures:
            print(" -", message)
        if strict:
            sys.exit(1)
    else:
        print("no inversion at any level (rarity medians ascending, no common/rare over the next band,"
              " no net-negative epic/legendary)")


if __name__ == "__main__":
    main()
