"""Offline content audit for the Wraeclast mod.

Checks every data sheet the plugin ships (mod/data and mod/design/public) for
internal consistency, and proves the flat level-up draft with a Monte-Carlo:
every tome and every skill upgrade must have the same chance.

Run:  python mod/tools/data/audit-content.py
Exit: 0 = clean, 1 = at least one error.
"""

from __future__ import annotations

import json
import pathlib
import random
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]          # mod/
REPO = ROOT.parent
DATA = ROOT / "data"
DESIGN = ROOT / "design"
ASSETS = ROOT / "Wraeclast" / "assets"

RARITIES = {"common", "rare", "epic", "legendary"}
TOME_COUNT = 26
RELIC_COUNT = 79
MOD_COUNT = 70
MODS_PER_SKILL = 10
DRAFT_CARDS = 3
MONTE_CARLO_DRAFTS = 100000

errors: list[str] = []
warnings: list[str] = []


def load(path: pathlib.Path):
    raw = path.read_bytes()
    if raw.startswith(b"\xef\xbb\xbf"):
        warnings.append(f"{path.name}: UTF-8 BOM (readers must use utf-8-sig)")
        return json.loads(raw.decode("utf-8-sig"))
    return json.loads(raw.decode("utf-8"))


def fail(message: str) -> None:
    errors.append(message)


# ---------------------------------------------------------------- rewards table
rewards = load(DATA / "rewards.json")
tomes = load(DATA / "tomes.json")
items = load(DATA / "items.json")
mods = load(DATA / "mods.json")
bosses = load(DATA / "bosses.json")
classes = load(DESIGN / "arena" / "classes.json")
skills = load(DESIGN / "arena" / "skills.json")
estat = set(load(DESIGN / "megabonk" / "enums.json")["EStat"].keys())

# Flat units: these are absolute numbers (25 armour, not 0.25); the fraction
# stats below are 0..1 by design. 0.25 on Armor is the bug the item sim found.
WHOLE_FLAT = {"Armor", "Shield", "Thorns", "MaxHealth", "HealthRegen", "Projectiles", "ProjectileBounces"}
TRIGGER_ON = {"hit", "kill", "standstill"}
TRIGGER_EFFECT = {"poison", "curse", "explode", "damageRamp"}

by_kind: dict[str, list[dict]] = {"tome": [], "relic": [], "mod": []}
ids = set()
for row in rewards:
    kind = row.get("kind")
    if kind not in by_kind:
        fail(f"rewards: unknown kind {kind!r}")
        continue
    by_kind[kind].append(row)

    rid = row.get("id")
    if not rid:
        fail(f"rewards: {kind} row without id")
    elif rid in ids:
        fail(f"rewards: duplicate id {rid}")
    ids.add(rid)

    if row.get("rarity") not in RARITIES:
        fail(f"rewards/{rid}: rarity {row.get('rarity')!r}")
    if row.get("tier") not in (1, 2, 3):
        fail(f"rewards/{rid}: tier {row.get('tier')!r}")
    if not isinstance(row.get("weight"), (int, float)) or row["weight"] <= 0:
        fail(f"rewards/{rid}: weight {row.get('weight')!r}")
    if not isinstance(row.get("minLevel"), int) or row["minLevel"] < 1:
        fail(f"rewards/{rid}: minLevel {row.get('minLevel')!r}")
    if not isinstance(row.get("maxStacks"), int) or row["maxStacks"] < 1:
        fail(f"rewards/{rid}: maxStacks {row.get('maxStacks')!r}")

if len(by_kind["tome"]) != TOME_COUNT:
    fail(f"rewards: {len(by_kind['tome'])} tomes, expected {TOME_COUNT}")
if len(by_kind["relic"]) != RELIC_COUNT:
    fail(f"rewards: {len(by_kind['relic'])} relics, expected {RELIC_COUNT}")
if len(by_kind["mod"]) != MOD_COUNT:
    fail(f"rewards: {len(by_kind['mod'])} mods, expected {MOD_COUNT}")

# ---------------------------------------------------------------- tome sheet
if len(tomes) != TOME_COUNT:
    fail(f"tomes.json: {len(tomes)} rows, expected {TOME_COUNT}")
tome_slots = [t.get("slot") for t in tomes]
if len(set(tome_slots)) != len(tome_slots):
    fail("tomes.json: duplicate slot")
tome_ids = {t.get("id") for t in tomes}
for row in by_kind["tome"]:
    if row.get("id") not in tome_ids:
        fail(f"rewards tome {row.get('id')} has no tomes.json row")
    if row.get("slot") not in tome_slots:
        fail(f"rewards tome {row.get('id')}: slot {row.get('slot')} unknown")
for tome in tomes:
    tid = tome.get("id")
    special = tome.get("nativeStat", 0) < 0
    if not special and "{value}" not in (tome.get("description") or ""):
        fail(f"{tid}: native tome lacks {{value}} in its text")
    if special and "{value}" in (tome.get("description") or ""):
        # Balance is code-side but still states the value its reward row grants.
        row = next((r for r in by_kind["tome"] if r["id"] == tid), None)
        if not row or not row.get("value"):
            fail(f"{tid}: special tome states {{value}} with no reward value")
    if not special and tome.get("rewriteStat", -1) >= 0 and not isinstance(tome.get("rewriteValue"), (int, float)):
        fail(f"{tid}: rewrite without a value")
    if not tome.get("name"):
        fail(f"{tid}: empty name")

# ---------------------------------------------------------------- relic sheet
if len(items) != RELIC_COUNT:
    fail(f"items.json: {len(items)} rows, expected {RELIC_COUNT}")
item_slots = [i.get("slot") for i in items]
if len(set(item_slots)) != len(item_slots):
    fail("items.json: duplicate slot")
item_ids = {i.get("id") for i in items}
for row in by_kind["relic"]:
    if row.get("id") not in item_ids:
        fail(f"rewards relic {row.get('id')} has no items.json row")
for item in items:
    iid = item.get("id")
    if not item.get("name"):
        fail(f"{iid}: empty name")
    if not item.get("stats") and not item.get("triggers") and not item.get("text"):
        fail(f"{iid}: no stats, triggers or text")
    if item.get("rarity") is not None and item["rarity"] not in RARITIES:
        fail(f"{iid}: rarity override {item['rarity']!r}")
    for stat in item.get("stats") or []:
        name = stat.get("stat")
        if name not in estat:
            fail(f"{iid}: stat {name!r} is not an EStat member")
        if stat.get("type") not in ("Addition", "Multiplication", "Flat"):
            fail(f"{iid}: stat {name} type {stat.get('type')!r}")
        if stat.get("type") == "Flat" and name in WHOLE_FLAT and stat.get("value", 0) < 1:
            fail(f"{iid}: flat {name} {stat.get('value')} (absolute value expected, e.g. 25)")
        if name == "Difficulty":
            fail(f"{iid}: Difficulty is a malus; relics are bonuses only")
        value = stat.get("value", 0)
        if stat.get("type") == "Multiplication" and value < 1 and name != "ChestPriceMultiplier":
            fail(f"{iid}: {name} is a malus ({value}); relics are bonuses only")
        if stat.get("type") in ("Addition", "Flat") and value < 0:
            fail(f"{iid}: {name} is negative ({value}); relics are bonuses only")
    for trigger in item.get("triggers") or []:
        if trigger.get("on") not in TRIGGER_ON:
            fail(f"{iid}: trigger on {trigger.get('on')!r}")
        if trigger.get("effect") not in TRIGGER_EFFECT:
            fail(f"{iid}: trigger effect {trigger.get('effect')!r}")

# ---------------------------------------------------------------- mods sheet
if len(mods) != MOD_COUNT:
    fail(f"mods.json: {len(mods)} rows, expected {MOD_COUNT}")
mod_ids = [m.get("id") for m in mods]
if len(set(mod_ids)) != len(mod_ids):
    fail("mods.json: duplicate id")
mod_rows = {row.get("id") for row in by_kind["mod"]}
for mod in mods:
    mid = mod.get("id")
    if mid not in mod_rows:
        fail(f"mods.json: {mid} missing from rewards mod rows")
    if not mod.get("text"):
        fail(f"{mid}: empty text")
    if mod.get("requires") and mod["requires"] not in mod_ids:
        fail(f"{mid}: requires unknown mod {mod['requires']}")
for row in by_kind["mod"]:
    if row.get("maxStacks") != 40:
        fail(f"rewards mod {row.get('id')}: maxStacks {row.get('maxStacks')} (expected 40)")

# branch graph must be acyclic; a mod may name itself as its branch (the sheet's
# way of saying "own branch"), so self-loops are dropped before the walk.
edges = {
    m["id"]: (m.get("requires") if m.get("requires") and m.get("requires") != m["id"] else None)
    for m in mods
}
state = {node: 0 for node in edges}


def visit(node: str) -> None:
    state[node] = 1
    nxt = edges.get(node)
    if nxt in edges:
        if state[nxt] == 1:
            fail(f"mods.json: requires cycle through {node} -> {nxt}")
        elif state[nxt] == 0:
            visit(nxt)
    state[node] = 2


for node in edges:
    if state[node] == 0:
        visit(node)

# 10 upgrades per class skill, and every class skill exists
skill_by_id = {s.get("id"): s for s in skills}
class_skills: set[str] = set()
for klass in classes:
    sid = klass.get("skillId")
    if sid not in skill_by_id:
        fail(f"classes.json: {klass.get('id')} skill {sid!r} missing from skills.json")
    else:
        class_skills.add(sid)
for sid in sorted(class_skills):
    count = sum(1 for m in mods if m.get("skill") == sid)
    if count != MODS_PER_SKILL:
        fail(f"{sid}: {count} upgrades, expected {MODS_PER_SKILL}")
for mod in mods:
    if mod.get("skill") not in skill_by_id:
        fail(f"{mod.get('id')}: unknown skill {mod.get('skill')!r}")

# ---------------------------------------------------------------- bosses
boss_ids = {b.get("id") for b in bosses.get("bosses", [])}
for floor in bosses.get("floors", []):
    if floor.get("boss") not in boss_ids:
        fail(f"bosses.json: floor {floor.get('floor')} names unknown boss {floor.get('boss')!r}")
for mb in bosses.get("minibosses", []):
    if mb not in boss_ids:
        fail(f"bosses.json: unknown miniboss {mb!r}")
for boss in bosses.get("bosses", []):
    model = boss.get("model")
    if not model:
        fail(f"bosses.json: {boss.get('id')} has no model")
        continue
    for base in (ROOT / "art" / "game" / "models", ASSETS / "models"):
        if not (base / f"{model}.glb").exists():
            fail(f"bosses.json: missing {model}.glb under {base.relative_to(REPO)}")

# ---------------------------------------------------------------- assets shipped
for name in ("classes.json", "skills.json", "tomes.json", "items.json", "rewards.json"):
    if not (ASSETS / name).exists():
        fail(f"assets: {name} not staged (run prepare-data.ps1)")
for glb in (ROOT / "art" / "game" / "effects").glob("*.glb"):
    if not (ASSETS / "effects" / glb.name).exists():
        fail(f"assets: effect {glb.name} not staged")
icons = list((ROOT / "art" / "game" / "ui" / "items").glob("*.png"))
icon_ids = {p.stem for p in icons}
missing_icons = sorted(item_ids - icon_ids)
if len(icons) < RELIC_COUNT - 2:
    fail(f"assets: {len(icons)} item icons, expected at least {RELIC_COUNT - 2}")
warnings.append(f"item icons: {len(icons)}/{RELIC_COUNT} (fallback: {', '.join(missing_icons) or 'none'})")

# ---------------------------------------------------------------- flat draft proof
def simulate(shape: str | None) -> dict[str, float]:
    eligible = []
    for row in by_kind["tome"]:
        tome = next((t for t in tomes if t.get("id") == row["id"]), None)
        if tome is None:
            continue
        shapes = tome.get("shapes")
        if shape is not None and shapes and shape not in shapes:
            continue
        eligible.append(row["id"])

    if not eligible:
        return {}

    counts = {rid: 0 for rid in eligible}
    for _ in range(MONTE_CARLO_DRAFTS):
        for rid in random.sample(eligible, min(DRAFT_CARDS, len(eligible))):
            counts[rid] += 1
    total = MONTE_CARLO_DRAFTS * min(DRAFT_CARDS, len(eligible))
    return {rid: n / total for rid, n in counts.items()}


for shape in (None, "projectile", "aura", "nova", "melee", "chain", "cyclone", "ground"):
    rates = simulate(shape)
    if not rates:
        continue
    expected = 1.0 / len(rates)
    worst = max(abs(rate - expected) for rate in rates.values())
    if worst > expected * 0.10:
        fail(
            f"draft({shape or 'all'}): {len(rates)} cards, expected {expected:.4f} each, "
            f"worst deviation {worst:.4f}"
        )
    else:
        print(f"draft({shape or 'all'}): {len(rates)} cards, each ~ {expected:.4f} (worst {worst:.4f})")

# ---------------------------------------------------------------- report
print(f"\nrewards: {len(by_kind['tome'])} tome / {len(by_kind['relic'])} relic / {len(by_kind['mod'])} mod")
print(f"mods: {len(set(m.get('skill') for m in mods))} skills x {MODS_PER_SKILL} upgrades")
for message in warnings:
    print(f"WARN  {message}")
if errors:
    print("\nERRORS:")
    for message in errors:
        print(" -", message)
    sys.exit(1)
print("\ncontent audit clean")
