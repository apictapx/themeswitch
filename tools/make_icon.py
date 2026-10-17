#!/usr/bin/env python3
"""Generate src/ThemeSwitch/app.ico: a disc split into a sun half and a night half.

Pure standard library (zlib + struct), so it runs anywhere without Pillow:
    python3 tools/make_icon.py
"""
import math
import struct
import zlib
from pathlib import Path

SIZES = [16, 24, 32, 48, 64, 256]
SUPERSAMPLE = 4
AMBER = (245, 165, 36)
NIGHT = (46, 58, 89)
RIM = (30, 36, 52)


def pixel(x: float, y: float):
    """Colour at a point in the unit square, or None outside the disc."""
    dx, dy = x - 0.5, y - 0.5
    r = math.hypot(dx, dy)
    if r > 0.47:
        return None
    if r > 0.43:
        return RIM
    # Diagonal split, top-left is day, bottom-right is night.
    return AMBER if dx + dy < 0 else NIGHT


def render(size: int) -> bytes:
    rows = []
    n = SUPERSAMPLE
    for py in range(size):
        row = bytearray([0])  # PNG filter type: none
        for px in range(size):
            acc = [0, 0, 0, 0]
            for sy in range(n):
                for sx in range(n):
                    c = pixel((px + (sx + 0.5) / n) / size, (py + (sy + 0.5) / n) / size)
                    if c:
                        acc[0] += c[0]; acc[1] += c[1]; acc[2] += c[2]; acc[3] += 255
            covered = acc[3] // 255
            if covered:
                row += bytes([acc[0] // covered, acc[1] // covered, acc[2] // covered, acc[3] // (n * n)])
            else:
                row += bytes(4)
        rows.append(bytes(row))
    return png(size, b"".join(rows))


def png(size: int, raw: bytes) -> bytes:
    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)  # 8-bit RGBA
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def ico(images: list[tuple[int, bytes]]) -> bytes:
    out = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    for size, data in images:
        dim = 0 if size >= 256 else size  # 0 means 256 in the ICO directory
        out += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    return out + b"".join(data for _, data in images)


if __name__ == "__main__":
    target = Path(__file__).resolve().parent.parent / "src" / "ThemeSwitch" / "app.ico"
    target.write_bytes(ico([(s, render(s)) for s in SIZES]))
    print(f"wrote {target} ({target.stat().st_size} bytes)")
