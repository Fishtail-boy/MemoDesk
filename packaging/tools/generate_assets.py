#!/usr/bin/env python3
"""生成 MSIX 所需的占位图标（纯 Python，无第三方依赖）。

用法：python packaging/tools/generate_assets.py
输出：packaging/Assets/{StoreLogo,Square44x44Logo,Square150x150Logo}.png
"""

import os
import struct
import zlib

BG = (38, 84, 75, 255)      # 深青色背景
FG = (255, 255, 255, 255)   # 白色便签
LINE = (38, 84, 75, 255)    # 便签上的文字线条

OUT_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "Assets")
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
APP_ASSETS_DIR = os.path.join(REPO_ROOT, "src", "MemoDesk.App", "Assets")


def inside_rounded_rect(x, y, left, top, right, bottom, radius):
    if x < left or x >= right or y < top or y >= bottom:
        return False
    corners = (
        (left + radius, top + radius, -1, -1),
        (right - radius, top + radius, 1, -1),
        (left + radius, bottom - radius, -1, 1),
        (right - radius, bottom - radius, 1, 1),
    )
    if x < left + radius and y < top + radius:
        cx, cy, _, _ = corners[0]
        return (x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2
    if x >= right - radius and y < top + radius:
        cx, cy, _, _ = corners[1]
        return (x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2
    if x < left + radius and y >= bottom - radius:
        cx, cy, _, _ = corners[2]
        return (x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2
    if x >= right - radius and y >= bottom - radius:
        cx, cy, _, _ = corners[3]
        return (x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2
    return True


def pixel(size, x, y):
    note_left, note_right = size * 0.26, size * 0.74
    note_top, note_bottom = size * 0.18, size * 0.82
    radius = max(size * 0.06, 1.5)

    if inside_rounded_rect(x, y, note_left, note_top, note_right, note_bottom, radius):
        # 便签内的三条“文字”线
        for frac in (0.36, 0.48, 0.60):
            line_y = size * frac
            if abs(y - line_y) <= max(size * 0.022, 0.8) and size * 0.34 <= x <= size * 0.66:
                return LINE
        return FG
    return BG


def png_bytes(size):
    raw = bytearray()
    for y in range(size):
        raw.append(0)  # filter: None
        for x in range(size):
            raw.extend(pixel(size, x, y))

    def chunk(tag, data):
        return (
            struct.pack(">I", len(data))
            + tag
            + data
            + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
        )

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", ihdr)
        + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + chunk(b"IEND", b"")
    )


def ico_bytes(sizes):
    """把多个 PNG 尺寸封装为 ICO（Vista+ 支持 PNG 压缩条目）。"""
    images = [(size, png_bytes(size)) for size in sizes]
    header = struct.pack("<HHH", 0, 1, len(images))
    entries = bytearray()
    offset = 6 + 16 * len(images)
    for size, data in images:
        dimension = 0 if size >= 256 else size
        entries += struct.pack(
            "<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(data), offset
        )
        offset += len(data)
    return header + bytes(entries) + b"".join(data for _, data in images)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    outputs = {
        "Square44x44Logo.png": 44,
        "Square150x150Logo.png": 150,
        "StoreLogo.png": 50,
    }
    for name, size in outputs.items():
        path = os.path.join(OUT_DIR, name)
        with open(path, "wb") as handle:
            handle.write(png_bytes(size))
        print(f"written {path} ({size}x{size})")

    os.makedirs(APP_ASSETS_DIR, exist_ok=True)
    ico_path = os.path.join(APP_ASSETS_DIR, "memo.ico")
    with open(ico_path, "wb") as handle:
        handle.write(ico_bytes([16, 32, 48, 256]))
    print(f"written {ico_path} (16/32/48/256)")


if __name__ == "__main__":
    main()
