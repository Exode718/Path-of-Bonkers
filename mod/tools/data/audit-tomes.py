"""Offline audit of mod/data/tomes.json against the reference TomeData rows.

Every PoE tome occupies one ETome slot and keeps that slot's native
statModifier. The sheet states the statModifier it was written against; this
script proves the sheet still matches the reference install, so the load-time
audit in TomeSwap will report "ok" and the card text stays true.
"""

import json
import pathlib

root = pathlib.Path(__file__).resolve().parents[2]
repo = root.parent
sheet = json.loads((root / "data/tomes.json").read_text(encoding="utf-8"))
rows = json.loads((repo / "lab/unitypy/out/content/TomeData.json").read_text(encoding="utf-8"))
etome = json.loads((root / "design/megabonk/enums.json").read_text(encoding="utf-8"))["ETome"]

by_number = {value: name for name, value in etome.items()}
by_slot = {by_number[row["fields"]["eTome"]]: row["fields"] for row in rows}

errors = []
seen = set()
for tome in sheet:
    slot = tome["slot"]
    if slot in seen:
        errors.append(f"duplicate slot {slot}")
    seen.add(slot)

    if slot not in etome:
        errors.append(f"{tome['id']}: {slot} is not an ETome member")
        continue

    fields = by_slot.get(slot)
    if fields is None:
        errors.append(f"{tome['id']}: no TomeData row for {slot}")
        continue

    modifier = fields["statModifier"]
    native = (modifier["stat"], modifier["modifyType"], round(modifier["modification"], 4))
    expected = (tome["nativeStat"], tome["nativeType"], round(tome["nativeValue"], 4))

    if tome["nativeStat"] < 0:
        status = "special"
    elif native != expected:
        errors.append(f"{tome['id']}: {slot} native {native} != sheet {expected}")
        status = "MISMATCH"
    elif tome.get("rewriteStat", -1) >= 0:
        # The sheet documents the reference row, and the loader rewrites the
        # slot to a stat that still means something (Tome of Silver -> Crit
        # Damage, silver being cut). The card states the rewrite, not the row.
        status = "rewrite"
    else:
        status = "ok"

    line = f"{slot:<16} {status:<8} {native!s:<22} {tome['name']:<22} {tome['description']}"
    if tome.get("rewriteStat", -1) >= 0:
        target = (tome["rewriteStat"], tome.get("rewriteType", -1), round(tome.get("rewriteValue", 0), 4))
        line += f"  <- rewritten to {target}"
    print(line)

missing = [slot for slot in by_slot if slot not in seen]
print(f"\n{len(sheet)} sheet entries, {len(by_slot)} native rows, unmapped: {missing or 'none'}")
if errors:
    print("\nERRORS:")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)
print("all slots resolve; every native row matches the reference, rewrites are stated")
