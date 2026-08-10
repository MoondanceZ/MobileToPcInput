from __future__ import annotations

import math
import struct
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageOps


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "pc_receiver" / "Assets"
PNG_PATH = ASSETS / "app.png"
ICO_PATH = ASSETS / "app.ico"
ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)


def _round_line(draw: ImageDraw.ImageDraw, points, width: int, fill: int = 255) -> None:
    draw.line(points, fill=fill, width=width, joint="curve")
    radius = width / 2
    for x, y in (points[0], points[-1]):
        draw.ellipse(
            (x - radius, y - radius, x + radius, y + radius),
            fill=fill,
        )


def _render_icon(size: int) -> Image.Image:
    # Higher supersampling at tray sizes keeps the round silhouette stable.
    scale = 16 if size <= 32 else 8 if size <= 128 else 4
    canvas_size = size * scale
    unit = canvas_size / 512.0

    vertical = Image.linear_gradient("L").resize((canvas_size, canvas_size))
    horizontal = vertical.transpose(Image.Transpose.ROTATE_90)
    diagonal = ImageChops.add(horizontal, vertical, scale=2.0)
    gradient = ImageOps.colorize(diagonal, "#078BFF", "#4B3FEA").convert("RGBA")

    circle_mask = Image.new("L", (canvas_size, canvas_size), 0)
    circle_draw = ImageDraw.Draw(circle_mask)
    margin = 12 * unit
    circle_draw.ellipse(
        (margin, margin, canvas_size - margin, canvas_size - margin),
        fill=255,
    )
    gradient.putalpha(circle_mask)

    glyph = Image.new("L", (canvas_size, canvas_size), 0)
    draw = ImageDraw.Draw(glyph)
    small = size <= 32

    outer = tuple(round(value * unit) for value in (199, 109, 313, 319))
    outer_radius = round((57 if small else 56) * unit)
    draw.rounded_rectangle(outer, radius=outer_radius, fill=255)

    inner = tuple(round(value * unit) for value in (
        (239, 151, 273, 278) if not small else (241, 153, 271, 277)
    ))
    inner_radius = round((17 if not small else 15) * unit)
    draw.rounded_rectangle(inner, radius=inner_radius, fill=0)

    arc_width = max(1, round((32 if small else 29) * unit))
    arc_box = tuple(round(value * unit) for value in (137, 208, 375, 378))
    draw.arc(arc_box, start=18, end=162, fill=255, width=arc_width)
    # Pillow arcs do not guarantee round caps, so add explicit cap circles.
    cx, cy = 256 * unit, 293 * unit
    rx, ry = 119 * unit, 85 * unit
    cap_radius = arc_width / 2
    for angle in (18, 162):
        radians = math.radians(angle)
        x = cx + rx * math.cos(radians)
        y = cy + ry * math.sin(radians)
        draw.ellipse(
            (x - cap_radius, y - cap_radius, x + cap_radius, y + cap_radius),
            fill=255,
        )

    stem_width = max(1, round((31 if small else 28) * unit))
    _round_line(
        draw,
        [(256 * unit, 365 * unit), (256 * unit, 416 * unit)],
        stem_width,
    )
    _round_line(
        draw,
        [(198 * unit, 416 * unit), (314 * unit, 416 * unit)],
        stem_width,
    )

    white = Image.new("RGBA", (canvas_size, canvas_size), (255, 255, 255, 0))
    white.putalpha(glyph)
    composed = Image.alpha_composite(gradient, white)
    result = composed.resize((size, size), Image.Resampling.LANCZOS)

    # Fully transparent pixels must not retain colored RGB data. This avoids
    # colored halos in Windows tray compositing implementations.
    pixels = list(result.get_flattened_data())
    result.putdata([(0, 0, 0, 0) if a == 0 else (r, g, b, a) for r, g, b, a in pixels])
    return result


def _dib_frame(image: Image.Image) -> bytes:
    image = image.convert("RGBA")
    width, height = image.size
    pixel_bytes = image.transpose(Image.Transpose.FLIP_TOP_BOTTOM).tobytes("raw", "BGRA")
    mask_stride = ((width + 31) // 32) * 4
    and_mask = bytes(mask_stride * height)
    header = struct.pack(
        "<IiiHHIIiiII",
        40,
        width,
        height * 2,
        1,
        32,
        0,
        len(pixel_bytes),
        0,
        0,
        0,
        0,
    )
    return header + pixel_bytes + and_mask


def _write_ico(path: Path, frames: list[Image.Image]) -> None:
    payloads = [_dib_frame(frame) for frame in frames]
    header_size = 6 + 16 * len(frames)
    offset = header_size
    entries = []
    for frame, payload in zip(frames, payloads, strict=True):
        width, height = frame.size
        entries.append(
            struct.pack(
                "<BBBBHHII",
                0 if width >= 256 else width,
                0 if height >= 256 else height,
                0,
                0,
                1,
                32,
                len(payload),
                offset,
            )
        )
        offset += len(payload)

    with path.open("wb") as stream:
        stream.write(struct.pack("<HHH", 0, 1, len(frames)))
        stream.write(b"".join(entries))
        stream.write(b"".join(payloads))


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    app_png = _render_icon(1024)
    app_png.save(PNG_PATH, format="PNG", optimize=True)

    frames = [_render_icon(size) for size in ICO_SIZES]
    _write_ico(ICO_PATH, frames)
    print(f"Generated {PNG_PATH} ({app_png.width}x{app_png.height})")
    print(f"Generated {ICO_PATH} ({', '.join(map(str, ICO_SIZES))} px DIB frames)")


if __name__ == "__main__":
    main()
