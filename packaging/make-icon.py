"""生成 AppImage / 安装包用的占位图标（256x256 PNG）。

设计：深色圆角方块底 + 一道白色斜笔 + 笔尖一段强调蓝。
没有字体、没有依赖：纯数学画形，PNG 手写（zlib + struct）。

用法：python make-icon.py <输出 png>
正式图标到位后直接覆盖同一路径即可。
"""

import struct
import sys
import zlib

SIZE = 256


def rounded_rect_alpha(x: int, y: int, radius: int = 40) -> float:
    """圆角方块的覆盖度：圆角外 0，圆角内 255，边缘 1 像素内做一次平滑。"""
    margin = 8
    inner = SIZE - margin
    dx = 0.0
    dy = 0.0
    if x < margin + radius:
        dx = (margin + radius) - x
    elif x > inner - radius:
        dx = x - (inner - radius)
    if y < margin + radius:
        dy = (margin + radius) - y
    elif y > inner - radius:
        dy = y - (inner - radius)
    dist = (dx * dx + dy * dy) ** 0.5
    if dx <= 0 or dy <= 0:
        return 255 if margin <= x <= inner and margin <= y <= inner else 0
    if dist <= radius - 1:
        return 255
    if dist <= radius + 1:
        # 两像素宽的过渡带：dist 从 radius-1 走到 radius+1，覆盖度从 255 线性掉到 0。
        return int(255 * (radius + 1 - dist) / 2)
    return 0


def stroke_alpha(x: int, y: int) -> float:
    """一道从左下到右上的斜笔（带宽 26），再在右上端补一个笔尖三角。"""
    # 斜笔：点到"经过 (64, 200) 方向 (1,-1)"的直线的距离。
    px, py = x - 64.0, y - 200.0
    # 法向量 (1, 1) / sqrt(2)，投影长度即点到直线的有向距离。
    distance = abs(px + py) / (2 ** 0.5)
    along = (px - py) / (2 ** 0.5)
    if distance <= 13:
        return 255
    if distance <= 15 and -8 <= along <= 150:
        return int(255 * (15 - distance) / 2)
    return 0


def color_at(x: int, y: int) -> tuple[int, int, int, int]:
    plate = rounded_rect_alpha(x, y)
    if plate == 0:
        return (0, 0, 0, 0)

    base = (31, 31, 31)
    stroke = stroke_alpha(x, y)
    if stroke > 0:
        r = base[0] + (255 - base[0]) * stroke // 255
        g = base[1] + (255 - base[1]) * stroke // 255
        b = base[2] + (255 - base[2]) * stroke // 255
        return (r, g, b, plate)

    # 笔尖：右上角一小段强调蓝（与画笔色板里的"蓝"同一族）。
    if 168 <= x <= 208 and 48 <= y <= 88:
        tip_x, tip_y = x - 168, y - 48
        if tip_x + tip_y <= 40:
            return (0, 120, 212, plate)

    return (base[0], base[1], base[2], plate)


def main() -> None:
    out = sys.argv[1] if len(sys.argv) > 1 else "lanstartwrite.png"

    raw = bytearray()
    for y in range(SIZE):
        raw.append(0)  # 每行一个 filter 字节（None）。
        for x in range(SIZE):
            raw.extend(color_at(x, y))

    def chunk(tag: bytes, data: bytes) -> bytes:
        return (
            struct.pack(">I", len(data))
            + tag
            + data
            + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
        )

    ihdr = struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0)
    png = (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", ihdr)
        + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + chunk(b"IEND", b"")
    )
    with open(out, "wb") as handle:
        handle.write(png)
    print(f"icon written: {out}")


if __name__ == "__main__":
    main()
