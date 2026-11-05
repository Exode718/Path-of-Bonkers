"""Audit the relic sheet (mod/data/items.json) against the reference dumps.

Two kinds of relic live in the sheet:

- **Donor-backed** (R3, 67 slots): the effect is code-side (`ItemFactory` builds
  an `ItemImplementations.Item*` class that writes its own `ItemBase.SetStat`
  modifiers), so the sheet pairs the relic with a donor `EItem` slot and copies
  that donor's native card and `SetStat` lines. This script proves the copies are
  still exact and that every number the donor writes as a constant is stated in
  the relic text in the game's own units (fractions shown as percentages,
  `Armor` shown in armour points, `MaxHealth`/`HealthRegen`/`Thorns` in their
  display units).
- **Engine-owned** (R4, the PoE uniques that never had a donor): the sheet's own
  `stats` and `triggers` are the effect (`RelicEngine` applies them and the donor
  class is neutralised), so the script checks every `EStat`/`EStatModifyType`
  name, the trigger vocabulary and that the card states the numbers the engine
  writes.

Usage:
    python mod/tools/data/audit-relics.py           # report, exit 1 on a failure
    python mod/tools/data/audit-relics.py --quiet   # failures only
"""

import argparse
import json
import pathlib
import re
import sys

MOD = pathlib.Path(__file__).resolve().parents[2]
ROOT = MOD.parent
SHEET = MOD / "data/items.json"
RELICS = MOD / "design/arena/relics.json"
DONORS = ROOT / "docs/ITEM_DONORS.md"
ENUMS = MOD / "design/megabonk/enums.json"
REFERENCE = MOD / "design/megabonk/items.json"

# Stats shown as a percentage: the donor writes the fraction (0.08 -> 8%).
PERCENT_STATS = {
    "AttackSpeed",
    "DamageMultiplier",
    "MoveSpeedMultiplier",
    "CritChance",
    "Evasion",
    "Luck",
    "XpIncreaseMultiplier",
    "Difficulty",
    "Lifesteal",
}

# Stats whose flat value is a display number as-is (>= 1 only: below that the
# donor writes a hidden field scale the dump does not resolve).
FLAT_STATS = {"MaxHealth", "HealthRegen", "Thorns"}
# Armor is written as a fraction of 100 in the dump (0.1 -> 10 Armour).
ARMOR_STAT = "Armor"

WRITE = re.compile(r"^(?P<stat>\w+) (?P<type>\w+) amount(?: \* (?P<value>-?\d+(?:\.\d+)?))?$")


def donor_rows():
    rows = {}
    for line in DONORS.read_text(encoding="utf-8").splitlines():
        if not line.startswith("| ") or line.startswith("| EItem"):
            continue
        cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
        if len(cells) < 10 or not cells[0].isdigit():
            continue
        rows[int(cells[0])] = {
            "name": cells[1],
            "native": cells[7],
            "stats": cells[8],
        }
    return rows


def format_number(value):
    return f"{value:g}"


def stated_numbers(stats):
    """Display numbers the donor writes as unconditional constants.

    Conditional writes ("... (OnEvade)") are per-proc values the card states
    qualitatively, so they are not checked as passive numbers.
    """
    numbers = []
    if stats.strip() in ("", "—"):
        return numbers
    for write in stats.split(";"):
        match = WRITE.match(write.strip())
        if not match or match.group("value") is None:
            continue
        stat = match.group("stat")
        value = float(match.group("value"))
        if stat in PERCENT_STATS:
            numbers.append((f"{format_number(value * 100)}%", f"{stat} {value}"))
        elif stat == ARMOR_STAT and abs(value) >= 1:
            numbers.append((format_number(value * 100), f"Armor {value}"))
        elif stat in FLAT_STATS and abs(value) >= 1:
            numbers.append((format_number(value), f"{stat} {value}"))
    return numbers


def engine_numbers(entry):
    """Display numbers an engine-owned relic (R4) promises.

    Same rule as the donor table: only what the sheet writes is checked. A stat
    with no display convention (`PickupRange`, stated qualitatively) contributes
    nothing.
    """
    numbers = []
    for stat in entry.get("stats", []):
        name, kind, value = stat.get("stat"), stat.get("type"), float(stat.get("value", 0))
        if name in PERCENT_STATS:
            # Multiplication is a factor (0.94 -> 6%), Addition/Flat a fraction
            # the game shows as a percentage (0.06 -> 6%). The sign is not
            # checked: "+35% Life" and "6% slower" are both honest cards.
            shown = abs(value - 1) * 100 if kind == "Multiplication" else value * 100
            numbers.append((f"{shown:.1f}".rstrip("0").rstrip(".") + "%", f"{name} {value:g} ({kind})"))
        elif name in ("Armor", "Shield", "MaxHealth", "HealthRegen", "Thorns") and kind == "Flat":
            # The engine sheet writes absolute values (25 armour, not 0.25);
            # the donor dump's fractions are handled in stated_numbers().
            numbers.append((f"{value:g}", f"{name} {value}"))
        elif name == "Projectiles" and kind == "Flat":
            numbers.append((f"+{value:g}", f"{name} {value}"))
        elif name in ("ProjectileBounces", "ExtraJumps") and kind == "Flat":
            numbers.append((f"{value:g}", f"{name} {value}"))

    for trigger in entry.get("triggers", []):
        effect = trigger.get("effect")
        if effect == "explode" or (effect == "damageRamp" and trigger.get("value")):
            numbers.append((f"{float(trigger['value']) * 100:g}%", f"{effect} {trigger['value']}"))
        if effect in ("poison", "burn", "curse") and trigger.get("chance"):
            numbers.append((f"{float(trigger['chance']) * 100:g}%", f"{effect} chance"))
        if effect == "curse" and trigger.get("value"):
            numbers.append((f"{float(trigger['value']) * 100:g}%", f"curse {trigger['value']}"))
    return numbers


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--quiet", action="store_true")
    args = parser.parse_args()

    sheet = json.loads(SHEET.read_text(encoding="utf-8"))
    relics = {relic["id"]: relic for relic in json.loads(RELICS.read_text(encoding="utf-8"))}
    enum_table = json.loads(ENUMS.read_text(encoding="utf-8"))
    enums = enum_table["EItem"]
    estats = enum_table["EStat"]
    emodify = enum_table["EStatModifyType"]
    reference = {row["eItem"]: row for row in json.loads(REFERENCE.read_text(encoding="utf-8"))}
    donors = donor_rows()

    failures = []
    seen_slots = set()
    seen_relics = set()

    for entry in sheet:
        slot = entry["slot"]
        relic_id = entry["id"]
        label = f"{slot} ({relic_id})"

        if slot not in enums:
            failures.append(f"{label}: {slot} is not an EItem member")
            continue
        if slot in seen_slots:
            failures.append(f"{label}: slot already mapped")
        if relic_id in seen_relics:
            failures.append(f"{label}: relic already mapped")
        seen_slots.add(slot)
        seen_relics.add(relic_id)

        relic = relics.get(relic_id)
        if relic is None:
            failures.append(f"{label}: no such relic in relics.json")
        elif relic["name"] != entry["name"]:
            failures.append(f"{label}: name {entry['name']!r} != relic name {relic['name']!r}")

        enum_id = enums[slot]
        row = reference.get(enum_id)
        donor = donors.get(enum_id)

        # R4: a relic with the sheet's own stats/triggers is engine-owned. The
        # donor is only the slot's container, so the card states OUR numbers and
        # the donor card/SetStat copies are not required.
        if "stats" in entry or "triggers" in entry:
            if not entry.get("text"):
                failures.append(f"{label}: empty card text")
            for stat in entry.get("stats", []):
                stat_name, type_name = stat.get("stat"), stat.get("type")
                if stat_name not in estats:
                    failures.append(f"{label}: {stat_name} is not an EStat member")
                if type_name not in emodify:
                    failures.append(f"{label}: {type_name} is not an EStatModifyType member")
                if not float(stat.get("value", 0)):
                    failures.append(f"{label}: {stat_name} writes 0")
            for trigger in entry.get("triggers", []):
                if trigger.get("on") not in ("hit", "kill", "standstill"):
                    failures.append(f"{label}: unknown trigger event {trigger.get('on')!r}")
                if trigger.get("effect") not in ("poison", "burn", "curse", "explode", "damageRamp"):
                    failures.append(f"{label}: unknown trigger effect {trigger.get('effect')!r}")
            text = entry["text"]
            for needle, source in engine_numbers(entry):
                if needle not in text:
                    failures.append(f"{label}: text omits the engine's {source} (expected {needle!r})")
            if not args.quiet:
                print(f"relic {slot} -> {entry['name']}: engine "
                      f"{len(entry.get('stats', []))} stat(s), {len(entry.get('triggers', []))} trigger(s), "
                      f"\"{text}\"")
            continue

        if row is None:
            failures.append(f"{label}: no reference ItemData row")
            continue
        if donor is None:
            failures.append(f"{label}: no donor table row")
            continue

        if entry["nativeName"] != row["name"]:
            failures.append(f"{label}: nativeName {entry['nativeName']!r} != {row['name']!r}")
        if entry["native"].strip() != row["description"].strip():
            failures.append(f"{label}: native card drifted from the reference")
        if entry["nativeStats"].strip() != donor["stats"].strip():
            failures.append(f"{label}: nativeStats differ from the donor table")

        text = entry["text"]
        if not text:
            failures.append(f"{label}: empty card text")
        for needle, source in stated_numbers(entry["nativeStats"]):
            if needle not in text:
                failures.append(f"{label}: text omits the donor's {source} (expected {needle!r})")

        if not args.quiet:
            stats = entry["nativeStats"] if entry["nativeStats"].strip() not in ("", "—") else "no SetStat writes"
            print(f"relic {slot} -> {entry['name']}: native \"{entry['native']}\" "
                  f"[{stats}] (ok), \"{text}\"")

    unmapped = sorted(relic for relic in relics if relic.startswith("mb-") and relic not in seen_relics)
    for relic_id in unmapped:
        failures.append(f"{relic_id}: donor-backed relic is not mapped")

    # R4: every PoE unique without a donor must be a live, engine-owned relic.
    uniques = sorted(relic for relic in relics if not relic.startswith("mb-"))
    for relic_id in uniques:
        if relic_id not in seen_relics:
            failures.append(f"{relic_id}: PoE unique is not mapped (R4 owns it now)")

    if not args.quiet:
        print()
    for failure in failures:
        print(f"FAIL {failure}")
    print(f"{len(sheet)} relic(s) mapped, {len(seen_slots)} slot(s), "
          f"{len(unmapped)} unmapped donor-backed relic(s), {len(failures)} failure(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
