"""T1 — the act 2 and act 4 floor assets, curated from the client's own exports.

The first T1 pass shipped the wrong files for acts 2 and 4, and the operator
reported both symptoms (2026-11-02): "le sol de l'étage 3 est buggé, c'est pas
du tout la bonne texture", and the grass tufts are black on floors 2-3 and in
the final arena.

- `act4-ground-1.png` was the client's carved **rock atlas**
  (`kaomrockdarkgrey_colour`): the floor tiles sampled a sheet of red rock
  pieces on black, which read as red slabs.
- `act2-grass.png` and `act4-grass.png` were the same **ground** sheet (a field
  texture, opaque, no card alpha), so the grass billboards had no tuft to draw.

This pass rebuilds the five files from the client exports, with the source path
of each one recorded here:

    act2-grass    <- art/textures/environment/forest/nobiax_grass_01_colour.dds
    act4-grass    <- art/textures/environment/forest/nobiax_grass_02_colour.dds
    act4-ground-1 <- art/textures/environment/act4/kaomskingdom/grassground_colour.dds
    act4-ground-2 <- art/textures/environment/act4/kaomskingdom/kaomground2_colour.dds
    act4-rock     <- art/textures/environment/act4/desolation/rock_colour.dds

Export (PoE client, `Bundles2`), then copy the five PNGs into
`mod/art/raw/terrain/` under the names this tool reads:

    ggpk-explorer export art/textures/environment/forest --steam <Bundles2> \
        --poe1 --textures png -o <tmp>
    ggpk-explorer export art/textures/environment/act4 --steam <Bundles2> \
        --poe1 --textures png -o <tmp>

Rules (the A1/A1b lessons):

- a **ground** tile ships at 512² **RGB with the alpha dropped before the
  resize** (Pillow resizes RGBA through its alpha, which is what moved A1b's
  means);
- a **grass card** keeps its alpha — the billboard's shape is that alpha — and
  is resized to 256², so one tuft fills the quad the host's grass material
  draws (floor 1's A1 tuft is the same idea at 128²).

Usage:
    python mod/tools/data/curate-act-terrain.py            # rewrite the files
    python mod/tools/data/curate-act-terrain.py --check    # audit only
"""

import argparse
import pathlib
import sys

from PIL import Image

MOD = pathlib.Path(__file__).resolve().parents[2]
SHIPPED = MOD / "art/game/terrain"
RAW = MOD / "art/raw/terrain"

GROUND = 512
CARD = 256

# shipped id -> (raw source name, size, keep alpha)
FILES = {
    "act2-grass": ("act2-grass.png", CARD, True),
    "act4-grass": ("act4-grass.png", CARD, True),
    "act4-ground-1": ("act4-ground-1.png", GROUND, False),
    "act4-ground-2": ("act4-ground-2.png", GROUND, False),
    "act4-rock": ("act4-rock.png", GROUND, False),
}


def curate(src, size, keep_alpha):
    image = Image.open(src)
    if not keep_alpha:
        # Drop the alpha before the resize: Pillow resamples RGBA through it, so
        # the colours of a masked sheet move (A1b's measured 73.6 -> 88.4).
        image = image.convert("RGB")
        resample = Image.LANCZOS
    else:
        image = image.convert("RGBA")
        resample = Image.LANCZOS

    return image.resize((size, size), resample)


def run(check):
    failures = 0
    for name, (source_name, size, keep_alpha) in FILES.items():
        source = RAW / source_name
        shipped = SHIPPED / f"{name}.png"
        mode = "RGBA" if keep_alpha else "RGB"

        if check:
            if not shipped.exists():
                print(f"MISSING {shipped}")
                failures += 1
                continue

            image = Image.open(shipped)
            ok = image.size == (size, size) and image.mode == mode
            print(f"{name:14} {image.size[0]}x{image.size[1]} {image.mode}"
                  f" {'ok' if ok else f'EXPECTED {size}x{size} {mode}'}")
            failures += 0 if ok else 1
            continue

        if not source.exists():
            print(f"SKIP {name}: {source} is missing (see the export commands above)")
            failures += 1
            continue

        curate(source, size, keep_alpha).save(shipped)
        print(f"{name:14} <- {source_name} ({size}x{size} {mode})")

    return failures


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="audit the shipped files only")
    args = parser.parse_args()
    failures = run(args.check)
    if failures:
        print(f"{failures} file(s) to fix")
        return 1

    print("act terrain is curated")
    return 0


if __name__ == "__main__":
    sys.exit(main())
