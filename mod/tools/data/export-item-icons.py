"""Copy each relic's Path of Exile unique icon into `mod/art/game/ui/items/`.

S5+ replaces the donor (Megabonk) item art with the client's own unique art.
The `export-data` dump gives us the mapping for free: `uniques.json` carries a
`visual_identity.dds_file` per unique, and `export-data --images` writes the
same art as PNG beside the JSON (80x80, the donor icon size).

Mapping is by the relic's display name in `mod/data/items.json` (case
insensitive). Three relics use a name the client spells differently, so they
are aliased below; two (`Voyage`, `Apocalypse`) name no unique in the current
client data and keep the donor icon.

Usage:
    python mod/tools/data/export-item-icons.py <export-data-dir>

`<export-data-dir>` is the folder `ggpk-explorer export-data --images` wrote
(`uniques.json`, `Art/2DItems/...`). The script is idempotent and overwrites
the PNGs; it prints every miss instead of writing a blank.
"""

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
ITEMS = ROOT / "mod" / "data" / "items.json"
OUT = ROOT / "mod" / "art" / "game" / "ui" / "items"

# Relics whose display name differs from the client unique's exact name.
ALIASES = {
    "blood thorn": "the blood thorn",
    "eternity shroud": "the eternity shroud",
    "omniscience": "crystallised omniscience",
}


def load_unique_art(export_dir: Path) -> dict[str, str]:
    path = export_dir / "uniques.json"
    if not path.exists():
        sys.exit(f"missing {path} (run ggpk-explorer export-data --images)")

    uniques = json.loads(path.read_text(encoding="utf-8-sig"))
    art: dict[str, str] = {}
    for row in uniques.values():
        if row.get("is_alternate_art"):
            continue
        name = (row.get("name") or "").strip().lower()
        dds = (row.get("visual_identity") or {}).get("dds_file")
        if name and dds and name not in art:
            art[name] = dds
    return art


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("export_dir", type=Path, help="ggpk-explorer export-data output")
    args = parser.parse_args()

    items = json.loads(ITEMS.read_text(encoding="utf-8-sig"))
    art = load_unique_art(args.export_dir)
    OUT.mkdir(parents=True, exist_ok=True)

    copied = 0
    missing: list[str] = []
    for item in items:
        name = (item.get("name") or "").strip()
        key = name.lower()
        key = ALIASES.get(key, key)
        dds = art.get(key)
        if not dds:
            missing.append(name)
            continue

        png = (args.export_dir / dds).with_suffix(".png")
        if not png.exists():
            missing.append(f"{name} (no {png.name})")
            continue

        target = OUT / f"{item['id']}.png"
        target.write_bytes(png.read_bytes())
        copied += 1

    print(f"item icons: {copied}/{len(items)} copied to {OUT.relative_to(ROOT)}")
    if missing:
        print("missing (the donor icon stays):")
        for name in missing:
            print(f"  - {name}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
