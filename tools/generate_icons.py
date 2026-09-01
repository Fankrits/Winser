#!/usr/bin/env python3
"""Render the Winser app icon (Assets/Winser.ico + Assets/Winser.png + the
Assets/Msix/*.png package logos).

Everything is drawn analytically with signed distance fields and 4x supersampling,
so the mark stays crisp at every size and the artwork can be regenerated from
source instead of living in the repo as an opaque binary blob.

Usage:  python3 tools/generate_icons.py
"""

from __future__ import annotations

import math
import os
import struct
import zlib

OUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "Winser", "Assets")

# Brand gradient: indigo -> cyan, sampled along the top-left / bottom-right diagonal.
GRAD_FROM = (99, 102, 241)
GRAD_TO = (34, 211, 238)

# The mark: a ring with a crescent "bite" cut into a disc, centered in a
# [-1, 1] square, y pointing down. Mirrored in Assets/Web/newtab.html's
# inline SVG - keep the two in sync if this changes.
R_OUTER = 0.78
R_INNER = 0.60
R_BLOB = 0.46
R_BITE = 0.24
BITE_DX = 0.29

SS = 4  # supersampling factor per axis


def _ring_distance(px, py, r_outer, r_inner):
    d = math.hypot(px, py)
    mid = (r_outer + r_inner) / 2.0
    half = (r_outer - r_inner) / 2.0
    return abs(d - mid) - half


def _crescent_distance(px, py, blob_r, bite_r, bite_dx):
    d_blob = math.hypot(px, py) - blob_r
    d_bite = math.hypot(px - bite_dx, py) - bite_r
    return max(d_blob, -d_bite)  # SDF subtraction: blob minus bite


def _rounded_box_distance(px, py, half, radius):
    qx = abs(px) - (half - radius)
    qy = abs(py) - (half - radius)
    outside = math.hypot(max(qx, 0.0), max(qy, 0.0))
    inside = min(max(qx, qy), 0.0)
    return outside + inside - radius


def render(size: int) -> bytes:
    """Return `size` x `size` RGBA pixels, row-major."""
    # Small icons need a proportionally chunkier ring to stay readable.
    boost = 1.0 + (0.35 if size <= 20 else 0.18 if size <= 32 else 0.0)
    r_inner = R_OUTER - (R_OUTER - R_INNER) * boost
    corner = 0.30 if size >= 32 else 0.24
    px_buf = bytearray(size * size * 4)

    for y in range(size):
        for x in range(size):
            acc_r = acc_g = acc_b = acc_a = 0.0
            for sy in range(SS):
                for sx in range(SS):
                    # Map the subsample to [-1, 1].
                    u = ((x + (sx + 0.5) / SS) / size) * 2.0 - 1.0
                    v = ((y + (sy + 0.5) / SS) / size) * 2.0 - 1.0

                    plate = _rounded_box_distance(u, v, 0.96, corner)
                    if plate > 0.02:
                        continue
                    plate_a = min(1.0, max(0.0, (0.01 - plate) / 0.02))

                    # Diagonal gradient, 0 at top-left corner, 1 at bottom-right.
                    t = min(1.0, max(0.0, (u + v + 2.0) / 4.0))
                    r = GRAD_FROM[0] + (GRAD_TO[0] - GRAD_FROM[0]) * t
                    g = GRAD_FROM[1] + (GRAD_TO[1] - GRAD_FROM[1]) * t
                    b = GRAD_FROM[2] + (GRAD_TO[2] - GRAD_FROM[2]) * t

                    glyph = min(
                        _ring_distance(u, v, R_OUTER, r_inner),
                        _crescent_distance(u, v, R_BLOB, R_BITE, BITE_DX),
                    )
                    glyph_a = min(1.0, max(0.0, (0.008 - glyph) / 0.016))
                    if glyph_a > 0.0:
                        r += (255 - r) * glyph_a
                        g += (255 - g) * glyph_a
                        b += (255 - b) * glyph_a

                    acc_r += r * plate_a
                    acc_g += g * plate_a
                    acc_b += b * plate_a
                    acc_a += plate_a

            n = SS * SS
            alpha = acc_a / n
            i = (y * size + x) * 4
            if alpha <= 0.0:
                continue
            # Un-premultiply so the stored colour is correct at partial coverage.
            px_buf[i + 0] = min(255, int(acc_r / acc_a + 0.5))
            px_buf[i + 1] = min(255, int(acc_g / acc_a + 0.5))
            px_buf[i + 2] = min(255, int(acc_b / acc_a + 0.5))
            px_buf[i + 3] = min(255, int(alpha * 255 + 0.5))
    return bytes(px_buf)


def to_png(size: int, rgba: bytes) -> bytes:
    def chunk(kind: bytes, payload: bytes) -> bytes:
        return (
            struct.pack(">I", len(payload))
            + kind
            + payload
            + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)
        )

    raw = b"".join(b"\x00" + rgba[y * size * 4:(y + 1) * size * 4] for y in range(size))
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )


def to_ico_bmp(size: int, rgba: bytes) -> bytes:
    """A 32bpp BITMAPINFOHEADER DIB, bottom-up, with an empty AND mask."""
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, size * size * 4, 0, 0, 0, 0)
    rows = []
    for y in range(size - 1, -1, -1):
        row = bytearray()
        for x in range(size):
            i = (y * size + x) * 4
            row += bytes((rgba[i + 2], rgba[i + 1], rgba[i + 0], rgba[i + 3]))
        rows.append(bytes(row))
    mask_stride = ((size + 31) // 32) * 4
    return header + b"".join(rows) + b"\x00" * (mask_stride * size)


def to_ico(images: dict[int, bytes]) -> bytes:
    entries, blobs = [], []
    offset = 6 + 16 * len(images)
    for size in sorted(images):
        rgba = images[size]
        # Windows reads PNG-compressed entries fine, but classic DIB entries are the
        # safest choice for the small sizes the shell scales into list views.
        blob = to_png(size, rgba) if size >= 128 else to_ico_bmp(size, rgba)
        entries.append(
            struct.pack(
                "<BBBBHHII",
                size if size < 256 else 0,
                size if size < 256 else 0,
                0, 0, 1, 32, len(blob), offset,
            )
        )
        blobs.append(blob)
        offset += len(blob)
    return struct.pack("<HHH", 0, 1, len(images)) + b"".join(entries) + b"".join(blobs)


def main() -> None:
    os.makedirs(OUT_DIR, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = {s: render(s) for s in sizes}

    ico_path = os.path.normpath(os.path.join(OUT_DIR, "Winser.ico"))
    png_path = os.path.normpath(os.path.join(OUT_DIR, "Winser.png"))
    with open(ico_path, "wb") as f:
        f.write(to_ico(images))
    with open(png_path, "wb") as f:
        f.write(to_png(256, images[256]))
    print(f"wrote {ico_path} ({os.path.getsize(ico_path)} bytes)")
    print(f"wrote {png_path} ({os.path.getsize(png_path)} bytes)")

    # MSIX package logos - used only by the release workflow's MSIX build (see
    # Package.appxmanifest). Same mark, just different canvas sizes.
    msix_dir = os.path.join(OUT_DIR, "Msix")
    for name, size in {"Square150x150Logo.png": 150, "Square44x44Logo.png": 44, "StoreLogo.png": 50}.items():
        path = os.path.normpath(os.path.join(msix_dir, name))
        with open(path, "wb") as f:
            f.write(to_png(size, render(size)))
        print(f"wrote {path} ({os.path.getsize(path)} bytes)")


if __name__ == "__main__":
    main()
