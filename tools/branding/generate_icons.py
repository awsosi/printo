#!/usr/bin/env python3
"""Draws the Printo mark and writes the icon files the apps embed.

Run it from the repository root:

    python tools/branding/generate_icons.py

It rewrites everything under ``assets/brand/``. The outputs are committed, so a normal build
needs neither Python nor Pillow; this exists so the mark can be changed by editing geometry
rather than by opening a binary in an icon editor, and so every size comes from one source.

Why the artwork is drawn per size rather than scaled from one master
-------------------------------------------------------------------
An icon is looked at mostly at 16 pixels - in the notification area, in a Start Menu list, on a
taskbar button - and a 256-pixel drawing shrunk to 16 turns into grey soup. The subject is the
same at every size, deliberately, but the detail is not: the arrowheads only appear from 32
pixels up, where there is room for them to be arrowheads rather than three dark pixels. That is
what an icon set is, and it is the difference between a product that looks made and one that
looks generated.

The palette is not invented here. It is the admin console's own: --accent (#1d5a48) and
--paper-strong (#fffdfa) out of the `:root` block in apps/web/src/app.ts. Using a different
green for the Windows client would make one product look like two.
"""

from __future__ import annotations

import struct
from pathlib import Path

from PIL import Image, ImageDraw

# --------------------------------------------------------------------------------------------
# The brand, as the console already defines it.
# --------------------------------------------------------------------------------------------

# A gentle vertical gradient rather than a flat fill: flat reads as a placeholder at 256 pixels,
# and the two greens are close enough that at 16 pixels it is simply "green".
GREEN_TOP = (0x25, 0x6B, 0x52)
GREEN_BOTTOM = (0x16, 0x47, 0x39)

PAPER = (0xFF, 0xFD, 0xFA)

# Every size Windows asks for. 20 and 40 are the ones people forget: 20 is the notification area
# at 125% scaling and 40 is the Start Menu at 150%, and without them Windows scales 16 up, which
# looks exactly as bad as it sounds.
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)

# Sizes stored as a device-independent bitmap rather than a PNG. Windows reads both, but some
# older shell surfaces and some third-party resource editors only ever learned the bitmap form,
# and at these sizes a PNG saves a few hundred bytes for nothing.
BITMAP_UP_TO = 64

SUPERSAMPLE = 16


def rounded_tile(size: int, margin: float) -> Image.Image:
    """The green tile: a rounded square with a vertical gradient."""
    edge = size - 2 * margin
    radius = edge * 0.235

    gradient = Image.new("RGB", (1, size), GREEN_TOP)
    for y in range(size):
        ratio = y / max(1, size - 1)
        gradient.putpixel((0, y), tuple(
            round(top + (bottom - top) * ratio)
            for top, bottom in zip(GREEN_TOP, GREEN_BOTTOM)
        ))

    tile = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    tile.paste(gradient.resize((size, size)), (0, 0))

    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (margin, margin, size - margin - 1, size - margin - 1), radius=radius, fill=255)
    tile.putalpha(mask)
    return tile


# The glyph, in its own coordinates: x and y run from -0.5 to 0.5 about the centre, which is
# what lets the whole thing be scaled and centred as one object rather than nudged into place.
#
# The branches leave the split steeply and reach wide apart. Earlier drafts kept them shallow,
# and the two arrowheads then merged into one solid dart filling the right half of the tile: the
# mark has to read as *two* destinations or it says nothing about routing.
STEM_END = (-0.50, 0.0)
SPLIT = (-0.19, 0.0)
BRANCH_END = (0.48, 0.50)

# Long and narrow. A stubby arrowhead - which is what a head as wide as it is long looks like -
# merges into the stroke it terminates and reads as a blob at every size below 64.
HEAD_LENGTH = 2.35
HEAD_HALF_WIDTH = 1.00

# Optically adjusted rather than proportional. Below 32 pixels the glyph is drawn larger and
# heavier: a stroke that measures 1.8 pixels renders as a grey smear, and a mark that is
# technically the right size looks smaller than the same mark at 256.
SPAN_SMALL, SPAN_LARGE = 0.78, 0.66
STROKE_SMALL, STROKE_LARGE = 0.132, 0.104

Point = tuple[float, float]


def _unit(from_point: Point, to_point: Point) -> Point:
    dx, dy = to_point[0] - from_point[0], to_point[1] - from_point[1]
    length = (dx * dx + dy * dy) ** 0.5
    return (dx / length, dy / length)


class Geometry:
    """
    The glyph: one path in, two paths out.

    What the product does, in three strokes. A document arrives and is routed - to the laser, to
    the thermal printer, to an operator. A page with a folded corner would have said "document"
    and said nothing at all about the part that is ours.

    Worked out once here and then drawn twice, as pixels and as SVG. It was two copies of this
    arithmetic for one draft, and the copies immediately disagreed: the vector one kept an older
    stroke weight and shortened the stem as though it ended in an arrowhead, so the console's
    mark was a different drawing from the client's. One set of numbers, two renderers.
    """

    def __init__(self, size: float, arrowheads: bool) -> None:
        span = size * (SPAN_SMALL if size < 32 else SPAN_LARGE)
        self.stroke = size * (STROKE_SMALL if size < 32 else STROKE_LARGE)

        def place(point: Point) -> Point:
            return (size / 2 + point[0] * span, size / 2 + point[1] * span)

        self.split = place(SPLIT)
        head_length = self.stroke * HEAD_LENGTH
        half_width = self.stroke * HEAD_HALF_WIDTH

        # The stem never carries an arrowhead - it is where the document comes in - so it always
        # runs the whole way to its end and is always round-capped.
        self.strokes: list[Point] = [place(STEM_END)]
        self.caps: list[Point] = [self.split, place(STEM_END)]
        self.heads: list[list[Point]] = []

        for branch in (place((BRANCH_END[0], -BRANCH_END[1])), place(BRANCH_END)):
            if not arrowheads:
                self.strokes.append(branch)
                self.caps.append(branch)
                continue

            direction = _unit(self.split, branch)
            perpendicular = (-direction[1], direction[0])

            # The stroke stops short of the tip and the triangle covers the gap, so the join is
            # one solid shape rather than a stroke with a triangle sitting on top of it.
            self.strokes.append((
                branch[0] - direction[0] * head_length * 0.80,
                branch[1] - direction[1] * head_length * 0.80,
            ))

            base = (
                branch[0] - direction[0] * head_length,
                branch[1] - direction[1] * head_length,
            )
            self.heads.append([
                branch,
                (base[0] + perpendicular[0] * half_width, base[1] + perpendicular[1] * half_width),
                (base[0] - perpendicular[0] * half_width, base[1] - perpendicular[1] * half_width),
            ])


def draw_mark(draw: ImageDraw.ImageDraw, size: int, arrowheads: bool) -> None:
    """Draws the geometry above with Pillow."""
    mark = Geometry(size, arrowheads)

    for head in mark.heads:
        draw.polygon(head, fill=PAPER)

    for end in mark.strokes:
        draw.line([mark.split, end], fill=PAPER, width=round(mark.stroke))

    # Round caps and a round join, which Pillow's line does not draw for us.
    for centre in mark.caps:
        draw.ellipse(
            (centre[0] - mark.stroke / 2, centre[1] - mark.stroke / 2,
             centre[0] + mark.stroke / 2, centre[1] + mark.stroke / 2),
            fill=PAPER)


def tile_margin(size: float) -> float:
    """
    Air around the tile, as a fraction of the canvas edge.

    Full bleed while the icon is small - every pixel counts at 16 - and a little once there is
    room, so that the large sizes do not look cramped against their own edge.
    """
    return 0.0 if size < 48 else 0.035


def render(size: int) -> Image.Image:
    """One icon, drawn large and reduced, which is how the edges come out smooth."""
    canvas = size * SUPERSAMPLE

    image = rounded_tile(canvas, canvas * tile_margin(size))
    draw = ImageDraw.Draw(image)
    draw_mark(draw, canvas, arrowheads=size >= 32)

    return image.resize((size, size), Image.LANCZOS)


# --------------------------------------------------------------------------------------------
# Writing the .ico container by hand.
#
# Pillow can save an ICO, but only by scaling one image down to every size it is asked for,
# which throws away the whole point of drawing each size separately. The container itself is a
# directory of independent images, so writing it takes a header and sixteen bytes per entry.
# --------------------------------------------------------------------------------------------


def as_bitmap(image: Image.Image) -> bytes:
    """A 32-bit bottom-up DIB, plus the 1-bit mask the format still requires."""
    width, height = image.size
    pixels = image.convert("RGBA").load()

    header = struct.pack(
        "<IiiHHIIiiII",
        40,             # biSize
        width,
        height * 2,     # biHeight: the colour rows and the mask rows together
        1,              # biPlanes
        32,             # biBitCount
        0,              # biCompression: BI_RGB
        0,              # biSizeImage
        0, 0, 0, 0,     # resolution and palette counts, all unused at 32 bits
    )

    colour = bytearray()
    for y in range(height - 1, -1, -1):
        for x in range(width):
            red, green, blue, alpha = pixels[x, y]
            colour += bytes((blue, green, red, alpha))

    # All zero: with an alpha channel present Windows ignores the mask, but a missing or
    # wrongly sized one makes the entry unreadable.
    stride = ((width + 31) // 32) * 4
    mask = bytes(stride * height)

    return header + bytes(colour) + mask


def as_png(image: Image.Image) -> bytes:
    from io import BytesIO

    buffer = BytesIO()
    image.save(buffer, format="PNG", optimize=True)
    return buffer.getvalue()


def write_ico(path: Path, images: dict[int, Image.Image]) -> None:
    payloads: list[tuple[int, bytes]] = []
    for size in sorted(images):
        image = images[size]
        payloads.append((size, as_bitmap(image) if size <= BITMAP_UP_TO else as_png(image)))

    offset = 6 + 16 * len(payloads)
    directory = bytearray(struct.pack("<HHH", 0, 1, len(payloads)))
    body = bytearray()

    for size, payload in payloads:
        directory += struct.pack(
            "<BBBBHHII",
            0 if size >= 256 else size,   # 256 is recorded as zero, which is the format's way
            0 if size >= 256 else size,
            0,                            # palette size: none
            0,                            # reserved
            1,                            # colour planes
            32,                           # bits per pixel
            len(payload),
            offset,
        )
        body += payload
        offset += len(payload)

    path.write_bytes(bytes(directory) + bytes(body))


def write_svg(path: Path) -> None:
    """
    The same mark as vector, for the admin console and for anywhere a browser is involved.

    Generated from the same numbers as the bitmaps so the two cannot drift apart. It carries the
    arrowheads, because a browser tab renders at whatever size it likes and an SVG has no small
    size to degrade to.

    Full bleed, like the small bitmaps and unlike the 256-pixel one, because a browser uses this
    as a favicon: sixteen or thirty-two pixels in a tab, where the margin the large sizes want
    would cost a tenth of the width for nothing anyone can see.
    """
    unit = 256
    mark = Geometry(unit, arrowheads=True)
    radius = unit * (1 - 2 * tile_margin(16)) * 0.235

    paths = "\n".join(
        f'    <path d="M {mark.split[0]:.2f} {mark.split[1]:.2f} '
        f'L {end[0]:.2f} {end[1]:.2f}"/>'
        for end in mark.strokes)

    heads = "\n".join(
        '  <polygon points="'
        + " ".join(f"{x:.2f},{y:.2f}" for x, y in head)
        + f'" fill="#%02X%02X%02X"/>' % PAPER
        for head in mark.heads)

    green_top = "#%02X%02X%02X" % GREEN_TOP
    green_bottom = "#%02X%02X%02X" % GREEN_BOTTOM
    paper = "#%02X%02X%02X" % PAPER

    path.write_text(
        f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" role="img"
     aria-label="Printo">
  <title>Printo</title>
  <defs>
    <linearGradient id="printo-tile" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="{green_top}"/>
      <stop offset="1" stop-color="{green_bottom}"/>
    </linearGradient>
  </defs>
  <rect width="256" height="256" rx="{radius:.2f}" fill="url(#printo-tile)"/>
  <g stroke="{paper}" stroke-width="{mark.stroke:.2f}" stroke-linecap="round" fill="none">
{paths}
  </g>
{heads}
</svg>
""",
        encoding="utf-8",
    )


def write_web_module(path: Path, svg: str) -> None:
    """
    The mark again, as a TypeScript module for the admin console.

    Inlined rather than served from a file on disk, and that is not laziness. The console is
    built with plain `tsc`, which copies no assets into `dist`, and it is deployed as a Docker
    image that copies only what it needs - so a route reading `assets/brand/printo.svg` would
    work in development and 404 in production, which is the worst of the available outcomes. A
    generated module survives both, and is still a text file that reviews and diffs like one.
    """
    escaped = svg.replace("\\", "\\\\").replace("`", "\\`").replace("${", "\\${")
    path.write_text(
        f"""// Generated by tools/branding/generate_icons.py. Do not edit by hand: change the geometry
// there and re-run it, so the console's mark and the Windows client's cannot drift apart.

/** The Printo mark, as an inline SVG document. */
export const BRAND_MARK_SVG = `{escaped}`;
""",
        encoding="utf-8",
    )


def main() -> None:
    root = Path(__file__).resolve().parents[2]
    brand = root / "assets" / "brand"
    brand.mkdir(parents=True, exist_ok=True)

    images = {size: render(size) for size in SIZES}

    write_ico(brand / "printo.ico", images)
    write_svg(brand / "printo.svg")
    write_web_module(
        root / "apps" / "web" / "src" / "brand.ts",
        (brand / "printo.svg").read_text(encoding="utf-8"))

    # The two a browser and a README want, and the one a proof sheet wants.
    images[256].save(brand / "printo-256.png", optimize=True)
    images[32].save(brand / "printo-32.png", optimize=True)

    sheet = Image.new("RGBA", (sum(SIZES) + 12 * len(SIZES), 272), (244, 239, 228, 255))
    x = 6
    for size in SIZES:
        sheet.alpha_composite(images[size], (x, 264 - size))
        x += size + 12
    sheet.save(brand / "printo-sizes.png", optimize=True)

    print(f"wrote {brand / 'printo.ico'} ({(brand / 'printo.ico').stat().st_size} bytes)")
    print(f"  sizes: {', '.join(str(size) for size in SIZES)}")
    print(f"wrote {brand / 'printo.svg'}")
    print(f"wrote {root / 'apps' / 'web' / 'src' / 'brand.ts'}")
    print(f"wrote {brand / 'printo-sizes.png'} - the proof sheet, to look at before committing")


if __name__ == "__main__":
    main()
