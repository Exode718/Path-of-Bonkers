"""Rebalance every relic: no maluses, and a real power budget by rarity.

Rules
-----
* Maluses are removed, never kept: `Difficulty`, negative Addition/Flat, and
  Multiplication factors below 1 (except `ChestPriceMultiplier`, where a lower
  price *is* the bonus).
* Each relic is then scaled to its rarity budget in the power points of
  `sim-items.py` (level 100): common 15 / rare 28 / epic 45 / legendary 75.
  Integer stats (projectiles, pierce) are kept as they are and count as "fixed";
  the remaining stats absorb the scale, up to per-stat caps so a card stays legible.
* The card text is regenerated from the final stats, so it can never lie.

Run:  python mod/tools/data/rebalance-relics.py          (dry run)
      python mod/tools/data/rebalance-relics.py --write  (rewrites items.json)
"""

from __future__ import annotations

import importlib.util
import json
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[1]                       # mod/
DATA = ROOT / "data"
LEVEL = 150   # budget at the deepest run the operator checks (flats fade by then)
TARGETS = {"common": 15.0, "rare": 28.0, "epic": 45.0, "legendary": 75.0}

spec = importlib.util.spec_from_file_location("sim_items", HERE / "sim-items.py")
sim = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sim)

INTEGER_STATS = {"Projectiles", "ProjectileBounces", "ExtraJumps"}
# The chest price is a discount, not a stat to scale to the rarity budget.
FIXED_STATS = INTEGER_STATS | {"ChestPriceMultiplier"}
# Flat stats that are fractions of 100 by design (0.1 = 10%), not absolute.
FRACTION_FLAT = {"CritChance", "CritDamage", "FreezeChance", "BurnChance", "Luck", "Lifesteal"}
PERCENT_CAP = {
    "DamageMultiplier": 1.20,
    "FireDamage": 1.50,
    "IceDamage": 1.50,
    "LightningDamage": 1.50,
    "PoisonDamageMultiplier": 1.00,
    "AttackSpeed": 0.60,
    "CritChance": 0.50,
    "CritDamage": 1.00,
    "Lifesteal": 0.25,
    "MoveSpeedMultiplier": 0.45,
    "SizeMultiplier": 0.60,
    "ProjectileSpeedMultiplier": 0.30,
    "Luck": 0.60,
    "GoldIncreaseMultiplier": 0.60,
    "XpIncreaseMultiplier": 0.60,
    "FreezeChance": 0.60,
    "BurnChance": 0.60,
    "MaxHealth": 0.80,           # Multiplication: +80%
    "Evasion": 0.60,
}
FLAT_CAP = {
    "MaxHealth": 250,
    "Armor": 150,
    "Shield": 250,
    "Thorns": 60,
    "HealthRegen": 250,
    "PickupRange": 400,
}

LABEL = {
    "DamageMultiplier": "Damage",
    "FireDamage": "Fire Damage",
    "IceDamage": "Ice Damage",
    "LightningDamage": "Lightning Damage",
    "PoisonDamageMultiplier": "Poison Damage",
    "AttackSpeed": "Attack Speed",
    "CritChance": "Critical Strike Chance",
    "CritDamage": "Critical Damage",
    "MaxHealth": "maximum Life",
    "Armor": "Armour",
    "Evasion": "Evasion",
    "Shield": "Shield",
    "Thorns": "Thorns",
    "HealthRegen": "Health Regeneration per minute",
    "Lifesteal": "Lifesteal",
    "MoveSpeedMultiplier": "Movement Speed",
    "SizeMultiplier": "Area of Effect",
    "ProjectileSpeedMultiplier": "Projectile Speed",
    "Projectiles": "Projectile Count",
    "ProjectileBounces": "pierce",
    "Luck": "Luck",
    "GoldIncreaseMultiplier": "Gold",
    "XpIncreaseMultiplier": "Experience gained",
    "PickupRange": "Pickup Range",
    "FreezeChance": "Freeze Chance",
    "BurnChance": "Burn Chance",
}


def is_malus(stat: dict) -> bool:
    name = stat.get("stat")
    kind = stat.get("type")
    value = stat.get("value", 0)
    if name == "Difficulty":
        return True
    if name == "ChestPriceMultiplier":
        return False
    if kind == "Multiplication":
        return value < 0.999
    return value < 0


def scale_stat(stat: dict, factor: float) -> None:
    name = stat.get("stat")
    kind = stat.get("type")
    if name in FIXED_STATS:
        return
    value = stat.get("value", 0)
    if kind == "Multiplication":
        percent = (value - 1.0) * factor
        cap = PERCENT_CAP.get(name, 1.0)
        percent = min(percent, cap)
        stat["value"] = round(1.0 + percent, 3)
    elif kind == "Addition" or (kind == "Flat" and name in FRACTION_FLAT):
        cap = PERCENT_CAP.get(name, 0.8)
        percent = min(value * factor, cap)
        stat["value"] = round(max(0.01, percent), 4)
    else:  # Flat absolute
        cap = FLAT_CAP.get(name, value * 4)
        flat = min(value * factor, cap)
        stat["value"] = int(round(max(1, flat)))


def round_stat(stat: dict) -> None:
    """Readable numbers: percentages in 2.5 / 5 % steps, flats in 5s.

    Move/Projectile speed and everything >= 20 % use 5 % steps (Karui Ward's
    42.9 % becomes 45 %; Lochtonial Caress' 6.8 % becomes 5 %); smaller
    percentages use 2.5 % steps (13.6 % becomes 12.5 %). Flats round to 5
    (Armour 78 -> 80, Shield 142 -> 140); multiplication stats use 5 % steps.
    """
    name = stat.get("stat")
    kind = stat.get("type")
    value = stat.get("value", 0)
    if name in INTEGER_STATS:
        return

    if name == "ChestPriceMultiplier" and kind == "Multiplication":
        # A chest-price discount is capped at 40 % and stated exactly; it is
        # never scaled further (a fifth stack made the price unreadable).
        discount = min(0.40, round((1.0 - value) / 0.05) * 0.05)
        stat["value"] = round(1.0 - discount, 2)
        return

    if kind == "Multiplication":
        percent = round(((value - 1.0) * 100.0) / 5.0) * 5.0
        stat["value"] = round(1.0 + percent / 100.0, 3)
        return

    if kind == "Addition" or (kind == "Flat" and name in FRACTION_FLAT):
        percent = value * 100.0
        if name == "ProjectileSpeedMultiplier":
            percent = min(percent, 30.0)
        step = 5.0 if name in ("MoveSpeedMultiplier", "ProjectileSpeedMultiplier") or percent >= 20.0 else 2.5
        percent = round(percent / step) * step
        stat["value"] = round(max(0.01, percent / 100.0), 4)
        return

    flat = int(round(value / 5.0) * 5)
    stat["value"] = max(5, flat)


def round_trigger(trigger: dict) -> None:
    if trigger.get("chance") is not None:
        trigger["chance"] = round(trigger["chance"] / 0.05) * 0.05
    if trigger.get("effect") in ("curse", "explode") and trigger.get("value") is not None:
        trigger["value"] = round(trigger["value"] / 0.05) * 0.05


def scale_trigger(trigger: dict, factor: float) -> None:
    effect = trigger.get("effect")
    if effect in ("poison", "curse") and trigger.get("chance") is not None:
        cap = 0.60 if effect == "curse" else 1.0
        trigger["chance"] = round(min(cap, trigger["chance"] * factor), 3)
    if effect == "curse" and trigger.get("value") is not None:
        trigger["value"] = round(min(0.90, trigger["value"] * factor), 3)
    if effect == "explode" and trigger.get("value") is not None:
        trigger["value"] = round(min(1.0, trigger["value"] * factor), 3)


def format_number(value: float) -> str:
    if abs(value - round(value)) < 0.05:
        return f"{round(value):d}"
    return f"{value:.1f}".rstrip("0").rstrip(".")


def stat_text(stat: dict) -> str:
    name = stat.get("stat")
    kind = stat.get("type")
    value = stat.get("value", 0)
    if name == "ChestPriceMultiplier" and kind == "Multiplication":
        return f"chests cost {format_number((1.0 - value) * 100)}% less"
    if kind == "Multiplication":
        percent = (value - 1.0) * 100
        return f"+{format_number(percent)}% {LABEL.get(name, name)}"
    if kind == "Addition" or (kind == "Flat" and name in FRACTION_FLAT):
        return f"+{format_number(value * 100)}% {LABEL.get(name, name)}"
    if name == "ProjectileBounces":
        plural = "enemy" if round(value) == 1 else "enemies"
        return f"projectiles pierce {format_number(value)} {plural}"
    if name == "Projectiles":
        return f"+{format_number(value)} Projectile Count"
    return f"+{format_number(value)} {LABEL.get(name, name)}"


def trigger_text(trigger: dict) -> str:
    on = trigger.get("on")
    effect = trigger.get("effect")
    chance = trigger.get("chance")
    if effect == "explode":
        return f"slain enemies explode for {format_number(trigger.get('value', 0.5) * 100)}% of the killing hit"
    if effect == "poison":
        return (f"{format_number((chance or 0) * 100)}% chance on hit to poison enemies "
                f"for {format_number(trigger.get('seconds', 3))} s")
    if effect == "curse":
        return (f"{format_number((chance or 0) * 100)}% chance on hit to curse nearby enemies "
                f"for {format_number(trigger.get('value', 0.5) * 100)}% of the hit")
    if effect == "damageRamp":
        return "while standing still, damage ramps up to +100%"
    return effect or "effect"


def compose(stats: list[dict], triggers: list[dict]) -> str:
    parts = [stat_text(stat) for stat in stats]
    parts += [trigger_text(trigger) for trigger in triggers]
    if not parts:
        return "A relic with no effect."
    if len(parts) == 1:
        text = parts[0]
    else:
        text = ", ".join(parts[:-1]) + " and " + parts[-1]
    return text[:1].upper() + text[1:] + "."


def clean(item: dict) -> None:
    item["stats"] = [stat for stat in item.get("stats") or [] if not is_malus(stat)]
    # A pure-trigger relic keeps its trigger; nothing to scale.
    if not item["stats"]:
        item.pop("stats", None)


def rebalance(item: dict, rarity: str) -> tuple[float, float]:
    target = TARGETS[rarity]
    before = sim.relic_points(item, LEVEL)

    fixed = sum(sim.stat_points(stat, LEVEL) for stat in item.get("stats") or []
                if stat.get("stat") in FIXED_STATS)
    fixed += sum(sim.trigger_points(trigger) for trigger in item.get("triggers") or [])
    scalable = sum(sim.stat_points(stat, LEVEL) for stat in item.get("stats") or []
                   if stat.get("stat") not in FIXED_STATS)
    trigger_scale = sum(1 for trigger in item.get("triggers") or [])

    # Scale up only: an item is never weakened by this pass.
    if scalable > 0.01:
        factor = max(1.0, min(6.0, (target - fixed) / scalable))
        for stat in item.get("stats") or []:
            scale_stat(stat, factor)
        if trigger_scale:
            for trigger in item.get("triggers") or []:
                scale_trigger(trigger, factor)
    elif trigger_scale and fixed > 0:
        factor = max(1.0, min(6.0, target / fixed))
        for trigger in item.get("triggers") or []:
            scale_trigger(trigger, factor)

    for stat in item.get("stats") or []:
        round_stat(stat)
    for trigger in item.get("triggers") or []:
        round_trigger(trigger)

    after = sim.relic_points(item, LEVEL)

    # A utility item that still falls short gets a plain damage bonus so its
    # frame is honest (the operator: items are bonuses, and a legendary is strong).
    if after < target * 0.75:
        need = (target - after) / 100.0
        need = min(1.2, max(0.05, need))
        item.setdefault("stats", []).append(
            {"stat": "DamageMultiplier", "type": "Addition", "value": round(need, 3)})
        after = sim.relic_points(item, LEVEL)

    item["text"] = compose(item.get("stats") or [], item.get("triggers") or [])
    return before, after


def main() -> None:
    write = "--write" in sys.argv
    round_only = "--round-only" in sys.argv
    items = json.loads((DATA / "items.json").read_text(encoding="utf-8"))
    rewards = {row["id"]: row for row in json.loads((DATA / "rewards.json").read_text(encoding="utf-8"))
               if row.get("kind") == "relic"}

    if round_only:
        print(f"{'id':<26}  text (rounded only, no scaling)")
        for item in items:
            for stat in item.get("stats") or []:
                round_stat(stat)
            for trigger in item.get("triggers") or []:
                round_trigger(trigger)
            if item["id"] == "perandus-blazon":
                # 40 % chest discount + 10 % Luck keeps the common on its floor
                # and the card exact; the discount itself stays one stack.
                for stat in item.get("stats") or []:
                    if stat.get("stat") == "Luck":
                        stat["value"] = 0.10
            item["text"] = compose(item.get("stats") or [], item.get("triggers") or [])
            print(f"{item['id']:<26}  {item['text']}")
    else:
        print(f"{'id':<26} {'rarity':<10} {'before':>7} {'after':>7}  text")
        for item in items:
            rarity = item.get("rarity") or rewards.get(item["id"], {}).get("rarity", "common")
            clean(item)
            before, after = rebalance(item, rarity)
            print(f"{item['id']:<26} {rarity:<10} {before:7.1f} {after:7.1f}  {item['text']}")

    if write:
        (DATA / "items.json").write_text(
            json.dumps(items, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print("\nitems.json written")


if __name__ == "__main__":
    main()
