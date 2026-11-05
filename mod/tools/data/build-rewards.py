"""Build the reward table (`mod/data/rewards.json`) from the tome and relic sheets.

R5's one table: the level-up draft, chests, the boss reward and the shop all
draw from it. Every row carries the fields the pool reads:

    kind      tome | relic | mod
    id        the sheet id (tome-<slot>, mb-<slug>, <poe-slug>)
    slot      ETome / EItem member the card sits on
    tier      1-3 depth gate (draft level band, chest tier, shop rarity)
    rarity    common | rare | epic | legendary (weight share, card frame)
    weight    base draw weight before luck and the rarity scale
    minLevel  player level floor
    maxStacks stack cap the pool checks before offering the card
    requires  an exclusive branch (mods only, R6)

Where the numbers come from:

- **Relics** take the donor slot's own `rarity` from Megabonk's `ItemData`
  dump (`mod/design/megabonk/items.json`, field `rarityName`): the slot
  is the same card the donor used, so its native rarity is the honest tier.
  The 12 engine-owned uniques (a row with `stats`/`triggers` in
  `mod/data/items.json`) are ours and sit at the top tier.
- **Tomes** carry no rarity in the game, so `TOME_META` below is our design
  (marked as ours in `DOC.md`): stat weight decides tier, the four code-side
  tomes are the legendary ones, and their `effect`/`value` fields keep R3's
  meaning (`native` = the game applies it, anything else = our code does).
- **Mods** (R6) come from `mod/data/mods.json`, our own sheet: one row per
  per-skill upgrade, carrying `skill` (the `skills.json` id it modifies) and
  `text` (the card text). A mod's `requires` is its **branch**: mods that share
  a branch are mutually exclusive, and the branch opener names itself so the
  load audit (`RewardTable`) can still check the field against a row. R11's
  three-card skill draft offers every mod of the class's skill, so the branch
  choice is the player's (the pool's branch filter is skipped for that draw).
- `maxStacks` is the tome sheet's `maxLevel` when the row sets one, else the
  sheet default (tomes **99**, relics 5, mods 2).
- `minLevel` is the tier's floor: 1 / 6 / 12.

Usage:
    python mod/tools/data/build-rewards.py            # write mod/data/rewards.json
    python mod/tools/data/build-rewards.py --check    # fail if the table is stale
"""

import argparse
import json
import pathlib
import sys

MOD = pathlib.Path(__file__).resolve().parents[2]
ROOT = MOD.parent
TOMES = MOD / "data/tomes.json"
ITEMS = MOD / "data/items.json"
MODS = MOD / "data/mods.json"
DONORS = MOD / "design/megabonk/items.json"
OUT = MOD / "data/rewards.json"

# Our tome design (Megabonk has no tome rarity): tier, rarity, weight.
TOME_META = {
    "Damage": (1, "common", 100),
    "Agility": (1, "common", 100),
    "Cooldown": (1, "common", 100),
    "Quantity": (2, "rare", 80),
    "Knockback": (1, "common", 100),
    "Armor": (1, "common", 100),
    "Health": (1, "common", 100),
    "Regeneration": (1, "common", 100),
    "Size": (2, "rare", 80),
    "ProjectileSpeed": (2, "rare", 80),
    "Duration": (2, "rare", 80),
    "Evasion": (1, "common", 100),
    "Attraction": (2, "rare", 80),
    "Luck": (3, "rare", 60),
    "Xp": (2, "rare", 80),
    "Precision": (2, "rare", 80),
    "Shield": (2, "rare", 80),
    "Blood": (3, "rare", 60),
    "Thorns": (2, "rare", 80),
    "Cursed": (3, "rare", 60),
    "Golden": (1, "common", 100),
    "Silver": (3, "rare", 60),
    "Balance": (3, "legendary", 40),
    "Chaos": (3, "legendary", 40),
    "Gambler": (3, "legendary", 40),
    "Hoarder": (3, "legendary", 40),
}

# The four code-side tomes keep R3's effect marker: `native` means the game's
# own TomeUtility implements it, anything else is our code (Balance).
TOME_EFFECT = {
    "Balance": "all-stats",
    "Chaos": "native",
    "Gambler": "native",
    "Hoarder": "native",
}
TOME_VALUE = {"Balance": 0.02}

# A tome's stack cap. The host's own tomes stop at 5 (`TomeData.GetMaxLevel`);
# ours go to 99 by default, which is what the operator's design says (0.9.102),
# unless the sheet row sets its own `maxLevel` (a flat count stat: Quantity and
# Swarm stop at 5). `TomeSwap.MaxLevelFor` reads the same field at runtime.
TOME_MAX_STACKS = 99

TIER_LEVEL = {1: 1, 2: 6, 3: 12}
RARITY_TIER = {"common": 1, "rare": 2, "epic": 3, "legendary": 3}
RARITY_WEIGHT = {"common": 100, "rare": 80, "epic": 65, "legendary": 50}

# Donor rarities that do not map one to one (EItemRarity has six members).
# `epic` stays epic (0.9.114): the merchant kinds ask for Common / Rare / Epic /
# Legendary by name, so folding it into `rare` left the black (Epic) trader with
# nothing to match. Only the two rarities the host has no card band for are
# folded: `corrupted` reads as the top band, `quest` as the base one.
DONOR_RARITY = {
    "common": "common",
    "rare": "rare",
    "epic": "epic",
    "legendary": "legendary",
    "corrupted": "legendary",
    "quest": "common",
}


def load(path):
    with path.open(encoding="utf-8") as handle:
        return json.load(handle)


def donor_rarity():
    """EItem member -> lower-case donor rarity, from the ItemData dump."""
    rarities = {}
    for row in load(DONORS):
        name = row.get("eItemName")
        if not name:
            continue
        rarities[name] = (row.get("rarityName") or "Common").lower()
    return rarities


def tome_rows():
    rows = []
    for tome in load(TOMES):
        slot = tome["slot"]
        tier, rarity, weight = TOME_META[slot]
        rows.append(
            {
                "kind": "tome",
                "id": tome["id"],
                "slot": slot,
                "name": tome["name"],
                "tier": tier,
                "rarity": rarity,
                "weight": weight,
                "minLevel": TIER_LEVEL[tier],
                "maxStacks": tome.get("maxLevel", TOME_MAX_STACKS),
                "requires": None,
                "effect": TOME_EFFECT.get(slot, "native"),
                "value": TOME_VALUE.get(slot, 0.0),
            }
        )
    return rows


# A chest-price discount is stated exactly on the card and capped at one stack;
# a second copy would silently make the price unreadable (`0.60²` = 64 % off).
RELIC_MAX_STACKS = {
    "perandus-blazon": 1,
}


def relic_rows():
    rarities = donor_rarity()
    donors = {row.get("eItemName"): row for row in load(DONORS)}
    rows = []
    for relic in load(ITEMS):
        slot = relic["slot"]
        # S5: every relic, engine-owned or donor-backed, sits on the slot whose
        # `ItemData` carries the card, so the slot's own rarity is the card's
        # (the game draws the frame and the chest roll from it). The 12 R4
        # uniques used to be forced to legendary/tier 3, which made the table
        # disagree with the frame on 10 of them.
        rarity = relic.get("rarity") or DONOR_RARITY.get(rarities.get(slot, "common"), "common")
        tier = RARITY_TIER[rarity]
        weight = RARITY_WEIGHT[rarity]
        native = donors.get(slot) or {}
        max_amount = native.get("maxAmount") or 0
        stacks = max_amount if 1 <= max_amount <= 10 else 5
        # A trigger is per hit/kill, not per stack (RelicEngine fires it once
        # while the relic is owned), so a relic with no stat and only triggers
        # gains nothing from a second copy: cap it at one.
        if not relic.get("stats") and relic.get("triggers"):
            stacks = 1
        stacks = RELIC_MAX_STACKS.get(relic["id"], stacks)

        rows.append(
            {
                "kind": "relic",
                "id": relic["id"],
                "slot": slot,
                "name": relic["name"],
                "tier": tier,
                "rarity": rarity,
                "weight": weight,
                "minLevel": TIER_LEVEL[tier],
                "maxStacks": stacks,
                "requires": None,
            }
        )
    return rows


def mod_rows():
    """R6: our per-skill upgrades, one row each, straight from the mod sheet."""
    rows = []
    for mod in load(MODS):
        rows.append(
            {
                "kind": "mod",
                "id": mod["id"],
                "slot": None,
                "skill": mod["skill"],
                "name": mod["name"],
                "text": mod["text"],
                "tier": mod["tier"],
                "rarity": mod["rarity"],
                "weight": mod["weight"],
                "minLevel": mod["minLevel"],
                "maxStacks": mod["maxStacks"],
                "requires": mod.get("requires"),
                "role": mod.get("role"),
            }
        )
    return rows


def build_text():
    rows = tome_rows() + relic_rows() + mod_rows()
    return json.dumps(rows, indent=2, ensure_ascii=False) + "\n"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    text = build_text()
    if args.check:
        current = OUT.read_text(encoding="utf-8") if OUT.exists() else ""
        if current != text:
            print("rewards.json is stale: run python mod/tools/data/build-rewards.py")
            return 1
        print("rewards.json is up to date")
        return 0

    OUT.write_text(text, encoding="utf-8")
    rows = json.loads(text)
    tomes = sum(1 for row in rows if row["kind"] == "tome")
    relics = sum(1 for row in rows if row["kind"] == "relic")
    mods = sum(1 for row in rows if row["kind"] == "mod")
    print(f"wrote {OUT.relative_to(ROOT)}: {tomes} tome(s), {relics} relic(s), {mods} mod(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
