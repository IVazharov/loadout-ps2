"""Renders Loadout's window icon, src/Loadout.App/Assets/loadout.ico.

Draws Loadout's mark (the amber tile, the chevron dropping
into the tray) for each frame of the app icon, with the straight
edges snapped to the pixel grid: the tray's sides and bottom keep equal
margins, and the strokes are 2 px at 16 px and 3 px at 20 and 24 px. Only
the diagonals (the tile's corners and the chevron) are anti-aliased. Run it
again after a change to the mark:

    python ps2/tools/make_icon.py

Needs Pillow (pip install pillow).
"""

import math
from pathlib import Path

from PIL import Image, ImageDraw

AMBER = (0xF2, 0x96, 0x3A, 0xFF)
INK = (0x0F, 0x17, 0x24, 0xFF)
SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
STROKES = {16: 2, 20: 3, 24: 3}
SUPERSAMPLE = 16
OUT = Path(__file__).resolve().parents[2] / "src" / "Loadout.App" / "Assets" / "loadout.ico"


def snap(value):
    return math.floor(value + 0.5)


def frame(size):
    s = size / 32
    w = STROKES.get(size, 4 * s)
    chamfer = 6 * s

    # The tray: its sides and bottom sit at the same margin, 5 of 32 units.
    left = snap(5 * s)
    right = size - left
    top = snap(16 * s)
    bottom = size - left

    # The chevron: 45 degree arms of 7 units, apex 13 units down, butt ends and a mitred apex.
    middle = size / 2
    apex = snap(13 * s)
    arm = 7 * s
    half = w / 2 * math.sqrt(2)
    # The stroke's edges are the centre line moved w/2 across it; across a 45 degree line that is
    # w/2/sqrt(2) in x and in y, and the mitre sits w/2*sqrt(2) above and below the apex.
    d = w / 2 / math.sqrt(2)
    chevron = [
        (middle - arm - d, apex - arm + d),
        (middle - arm + d, apex - arm - d),
        (middle, apex - half),
        (middle + arm - d, apex - arm - d),
        (middle + arm + d, apex - arm + d),
        (middle, apex + half),
    ]

    k = SUPERSAMPLE
    image = Image.new("RGBA", (size * k, size * k), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    def polygon(points, colour):
        draw.polygon([(x * k, y * k) for x, y in points], fill=colour)

    def rect(x0, y0, x1, y1, colour):
        draw.rectangle([x0 * k, y0 * k, x1 * k - 1, y1 * k - 1], fill=colour)

    c = chamfer
    polygon([(c, 0), (size - c, 0), (size, c), (size, size - c), (size - c, size), (c, size), (0, size - c), (0, c)], AMBER)
    polygon(chevron, INK)
    rect(left, top, left + w, bottom, INK)
    rect(right - w, top, right, bottom, INK)
    rect(left, bottom - w, right, bottom, INK)

    # Box filtering over premultiplied alpha gives each pixel its exact coverage.
    return image.convert("RGBa").resize((size, size), Image.Resampling.BOX).convert("RGBA")


def main():
    frames = [frame(size) for size in SIZES]
    largest = frames[-1]
    largest.save(OUT, format="ICO", sizes=[(size, size) for size in SIZES], append_images=frames[:-1])
    print(f"Wrote {OUT} ({', '.join(str(size) for size in SIZES)} px)")


if __name__ == "__main__":
    main()
