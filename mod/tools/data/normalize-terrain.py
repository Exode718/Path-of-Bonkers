"""A1b — the five Forest floor tiles share one tone.

`TerrainSwap` cycles the five client floor textures over a stage's tile
materials, so two neighbouring tiles can be drawn with different images. They
were shipped as the client exports them and their tone differs by up to 1.6x
(mean luminance 49.2 against 80.3/255), which reads on screen as light and dark
blocks ("on voit la distinction entre certains blocs").

This pass rewrites every tile so all five share one mean colour. Only the mean
moves: a per-channel gain (a scale about zero) takes each channel's mean to the
mean of the five, so a tile keeps its own pattern, its own contrast and its own
local cast. The measured gains are 0.820..1.374 and the worst 99.9th percentile
after the pass is 195/255, so the match is not a clipping pass.

Source = the raw client export (`art/raw/terrain/forest-ground-N`, 1024² RGBA,
an extensionless PNG); the shipped file is 512² RGB, which is A1's curation.
When the raw export is absent the shipped file is the source, so the tool still
runs on a checkout without the ignored raw tree.

  python mod/tools/data/normalize-terrain.py            # rewrite the five tiles
  python mod/tools/data/normalize-terrain.py --check    # audit only, exit 1 on a spread
  python mod/tools/data/normalize-terrain.py --sheet    # + lab/out/terrain-tone.png
"""

import argparse
import pathlib
import sys

from PIL import Image, ImageDraw

MOD = pathlib.Path(__file__).resolve().parents[2]
REPO = MOD.parent
SHIPPED = MOD / "art/game/terrain"
RAW = MOD / "art/raw/terrain"
SHEET = REPO / "lab/out/terrain-tone.png"

SIZE = 512
GROUNDS = (1, 2, 3, 4, 5)

# The mean of the five tiles as the client exports them (measured 2026-11-01).
# Matching every tile to it leaves the floor's overall tone exactly as it is.
TARGET = (67.2, 69.2, 48.7)

# A shipped tile's mean may sit this far from the target (8-bit rounding).
TOLERANCE = 0.75
# The five luminance means may sit this far apart.
SPREAD = 1.0


def mean_rgb(image):
    pixels = list(image.getdata())
    count = len(pixels)
    return tuple(sum(pixel[i] for pixel in pixels) / count for i in range(3))


def luminance(mean):
    return 0.2126 * mean[0] + 0.7152 * mean[1] + 0.0722 * mean[2]


def source(tile):
    raw = RAW / f"forest-ground-{tile}"
    if raw.exists():
        return raw, "raw"
    return SHIPPED / f"forest-ground-{tile}.png", "shipped"


def load(tile):
    path, origin = source(tile)
    # RGB before the resize: Pillow resizes RGBA through its alpha, which
    # brightens a tile's mean (g1 reads 73.6 as RGB and 88.4 as RGBA), and A1's
    # curation dropped the alpha for the same reason.
    image = Image.open(path).convert("RGB")
    if image.size != (SIZE, SIZE):
        image = image.resize((SIZE, SIZE), Image.LANCZOS)
    return image, origin


def scale_channel(channel, gain):
    lut = [min(255, int(round(value * gain))) for value in range(256)]
    return channel.point(lut)


def matched(image):
    mean = mean_rgb(image)
    gains = [TARGET[i] / max(mean[i], 1e-6) for i in range(3)]
    channels = image.split()
    return Image.merge("RGB", [scale_channel(channels[i], gains[i]) for i in range(3)]), gains


def report(origin, tile, before, after):
    print(
        f"  forest-ground-{tile} [{origin}]: "
        f"mean {before[0]:5.1f}/{before[1]:5.1f}/{before[2]:5.1f} "
        f"luma {luminance(before):5.1f} -> "
        f"{after[0]:5.1f}/{after[1]:5.1f}/{after[2]:5.1f} "
        f"luma {luminance(after):5.1f}"
    )


def audit():
    failures = []
    lumas = []
    print(f"terrain tone audit: target mean {TARGET[0]:.1f}/{TARGET[1]:.1f}/{TARGET[2]:.1f}")
    for tile in GROUNDS:
        path = SHIPPED / f"forest-ground-{tile}.png"
        if not path.exists():
            failures.append(f"forest-ground-{tile}.png is missing")
            continue
        mean = mean_rgb(Image.open(path).convert("RGB"))
        lumas.append(luminance(mean))
        off = max(abs(mean[i] - TARGET[i]) for i in range(3))
        status = "ok" if off <= TOLERANCE else f"OFF by {off:.2f}"
        print(
            f"  forest-ground-{tile}: mean {mean[0]:5.1f}/{mean[1]:5.1f}/{mean[2]:5.1f} "
            f"luma {luminance(mean):5.1f} ({status})"
        )
        if off > TOLERANCE:
            failures.append(f"forest-ground-{tile}: mean off the target by {off:.2f}/255")

    if lumas:
        delta = max(lumas) - min(lumas)
        print(f"  luminance spread: {delta:.2f}/255 (limit {SPREAD:.2f})")
        if delta > SPREAD:
            failures.append(f"the tiles' luminance spread is {delta:.2f}/255")

    for failure in failures:
        print(f"FAIL {failure}")
    print(f"{len(GROUNDS)} tile(s) audited, {len(failures)} failure(s)")
    return 1 if failures else 0


def contact_sheet(before, after):
    cell = 256
    width = cell * len(GROUNDS)
    strip = 160
    image = Image.new("RGB", (width, cell * 2 + strip), (18, 18, 18))
    draw = ImageDraw.Draw(image)

    for column, tile in enumerate(GROUNDS):
        row_before = before[tile].resize((cell, cell), Image.LANCZOS)
        row_after = after[tile].resize((cell, cell), Image.LANCZOS)
        image.paste(row_before, (column * cell, 0))
        image.paste(row_after, (column * cell, cell))
        draw.text((column * cell + 8, 8), f"client g{tile} {luminance(mean_rgb(before[tile])):.0f}", fill=(255, 255, 0))
        draw.text((column * cell + 8, cell + 8), f"matched {luminance(mean_rgb(after[tile])):.0f}", fill=(255, 255, 0))

    mosaic = (1, 2, 3, 4, 5, 1, 4, 2)
    for column, tile in enumerate(mosaic):
        piece = after[tile].resize((strip, strip), Image.LANCZOS)
        image.paste(piece, (column * strip, cell * 2))

    SHEET.parent.mkdir(parents=True, exist_ok=True)
    image.save(SHEET)
    print(f"contact sheet: {SHEET}")


def normalize(sheet):
    before = {}
    after = {}
    print(f"terrain tone match: target mean {TARGET[0]:.1f}/{TARGET[1]:.1f}/{TARGET[2]:.1f}")
    for tile in GROUNDS:
        image, origin = load(tile)
        result, gains = matched(image)
        before[tile] = image
        after[tile] = result
        result.save(SHIPPED / f"forest-ground-{tile}.png")
        report(origin, tile, mean_rgb(image), mean_rgb(result))
        print(
            f"      gain {gains[0]:.3f}/{gains[1]:.3f}/{gains[2]:.3f}, "
            f"{SIZE}x{SIZE} RGB"
        )

    if sheet:
        contact_sheet(before, after)
    return audit()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="audit the shipped tiles, write nothing")
    parser.add_argument("--sheet", action="store_true", help="also write the contact sheet")
    arguments = parser.parse_args()

    if arguments.check:
        return audit()
    return normalize(arguments.sheet)


if __name__ == "__main__":
    sys.exit(main())
