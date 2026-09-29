#!/usr/bin/env python3
"""Generates src/erp-printer.ico (printer with an upload arrow) without third-party packages.

Run: python3 build/make-icon.py
"""
import struct
import zlib
from pathlib import Path

SIZES = [16, 24, 32, 48, 64, 256]
SS = 4  # supersampling factor per axis

BODY = (37, 99, 235)      # blue
BODY_DARK = (29, 78, 216)
PAPER = (255, 255, 255)
EDGE = (148, 163, 184)
ARROW = (22, 163, 74)     # green


def rrect(x, y, x0, y0, x1, y1, r):
    """True if (x, y) lies inside a rounded rectangle (unit coordinates)."""
    if not (x0 <= x <= x1 and y0 <= y <= y1):
        return False
    cx = min(max(x, x0 + r), x1 - r)
    cy = min(max(y, y0 + r), y1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def shade(x, y):
    """Colour at unit coordinate (x, y), or None for transparent. Later shapes win."""
    colour = None
    # Paper coming out of the top (behind the body).
    if rrect(x, y, 0.27, 0.06, 0.73, 0.45, 0.03):
        colour = EDGE if not rrect(x, y, 0.29, 0.08, 0.71, 0.45, 0.02) else PAPER
    # Printer body.
    if rrect(x, y, 0.06, 0.34, 0.94, 0.78, 0.09):
        colour = BODY
    # Output slot.
    if rrect(x, y, 0.20, 0.64, 0.80, 0.70, 0.02):
        colour = BODY_DARK
    # Upload arrow badge (bottom right).
    cx, cy, rad = 0.74, 0.76, 0.22
    if (x - cx) ** 2 + (y - cy) ** 2 <= rad * rad:
        colour = ARROW
        shaft = abs(x - cx) <= 0.045 and cy - 0.06 <= y <= cy + 0.12
        head = y >= cy - 0.14 and y <= cy - 0.0 and abs(x - cx) <= (y - (cy - 0.14)) * 0.95
        if shaft or head:
            colour = PAPER
    return colour


def render(size):
    rows = []
    n = size * SS
    for py in range(size):
        row = bytearray([0])  # PNG filter type 0
        for px in range(size):
            r = g = b = a = 0
            for sy in range(SS):
                for sx in range(SS):
                    c = shade((px * SS + sx + 0.5) / n, (py * SS + sy + 0.5) / n)
                    if c:
                        r += c[0]; g += c[1]; b += c[2]; a += 255
            count = SS * SS
            if a:
                cov = a / 255
                row += bytes([round(r / cov), round(g / cov), round(b / cov), round(a / count)])
            else:
                row += bytes(4)
        rows.append(bytes(row))
    raw = b"".join(rows)

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def main():
    images = [render(s) for s in SIZES]
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    for size, png in zip(SIZES, images):
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset)
        offset += len(png)
    out = Path(__file__).resolve().parent.parent / "src" / "erp-printer.ico"
    out.write_bytes(header + entries + b"".join(images))
    print(f"wrote {out} ({out.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
