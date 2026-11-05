"""Offline audit of the class kit: per-level passives, weapon bonuses/traits,
skill traits and Fireball splash.

Catches the two dead-data classes of bug found on 2026-11-05:
  - a JSON key that the C# model does not have (`poisonSpread` was ignored),
  - a key the model has but no code reads (the weapon `bonus` block).

Run: python mod/tools/data/audit-class-kit.py
"""

from __future__ import annotations

import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
DESIGN = ROOT / "design"
ARENA = DESIGN / "arena"

STATS = set(json.loads((DESIGN / "megabonk" / "enums.json").read_text(encoding="utf-8-sig"))["EStat"])
MODIFY = set(json.loads((DESIGN / "megabonk" / "enums.json").read_text(encoding="utf-8-sig"))["EStatModifyType"])

# Keys the C# models actually deserialize.
SKILL_TRAITS = {
    "ignitePct", "igniteSeconds", "poisonPct", "poisonSeconds", "poisonStacks",
    "poisonSpread", "pullStrength", "chillPct", "freezeChance", "shockPct", "shockSeconds",
    "stunChance", "chainChance", "chainJumps", "novaLineChance",
}
WEAPON_BONUS = {
    "damagePct", "areaPct", "armour", "moveSpeedPct", "critChance",
    "maxLifePct", "regen", "projectileSpeedPct",
}
WEAPON_TRAITS = {"ignitePct", "igniteSeconds", "chainChance", "chainJumps", "poisonSpread"}

classes = json.loads((ARENA / "classes.json").read_text(encoding="utf-8-sig"))
skills = {s["id"]: s for s in json.loads((ARENA / "skills.json").read_text(encoding="utf-8-sig"))}

failures: list[str] = []


def fail(message: str) -> None:
    failures.append(message)
    print("FAIL", message)


for klass in classes:
    cid = klass["id"]
    skill = skills.get(klass.get("skillId"))
    if skill is None:
        fail(f"{cid}: skill {klass.get('skillId')} is missing")
        continue

    passive = klass.get("passive") or {}
    if passive.get("stats"):
        fail(f"{cid}: legacy static passive.stats is still present (double-counts with perLevel)")
    entries = passive.get("perLevel") or []
    if not entries:
        fail(f"{cid}: passive has no perLevel entry")
    for entry in entries:
        if entry.get("stat") not in STATS:
            fail(f"{cid}: passive stat {entry.get('stat')} is not an EStat")
        if entry.get("type") not in MODIFY:
            fail(f"{cid}: passive type {entry.get('type')} is not an EStatModifyType")
        if int(entry.get("every", 1)) < 1:
            fail(f"{cid}: passive every must be >= 1")
        if int(entry.get("max", 0)) < 0:
            fail(f"{cid}: passive max must be >= 0")

    for slot in ("starterWeapon", "offhand"):
        weapon = klass.get(slot)
        if not weapon:
            continue
        for key in (weapon.get("bonus") or {}):
            if key not in WEAPON_BONUS:
                fail(f"{cid}: weapon {weapon.get('id')} bonus {key} has no C# mapping")
        for key in (weapon.get("traits") or {}):
            if key not in WEAPON_TRAITS:
                fail(f"{cid}: weapon {weapon.get('id')} trait {key} has no C# mapping")

    for key in (skill.get("traits") or {}):
        if key not in SKILL_TRAITS:
            fail(f"{cid}: skill {skill['id']} trait {key} has no C# model field")

# Fireball's splash is the mechanic the operator asked for; keep it explicit.
fireball = skills.get("fireball")
if not fireball or float(fireball.get("splash") or 0) <= 0:
    fail("fireball: splash radius is missing")
elif float(fireball.get("splashShare") or 0) <= 0:
    fail("fireball: splashShare is missing")

viper = skills.get("viper-strike")
if not viper or float((viper.get("traits") or {}).get("poisonSpread") or 0) <= 0:
    fail("viper-strike: poisonSpread is missing")

print(f"\n{len(classes)} class(es), {len(skills)} skill(s) audited")
if failures:
    print(f"{len(failures)} failure(s)")
    sys.exit(1)
print("class kit audit clean")
