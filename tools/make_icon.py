# -*- coding: utf-8 -*-
"""
生成应用图标 app.ico —— 只用 Python 标准库（zlib / struct / math）。

图形含义：一枚显存仪表盘。
  外环   = 显存总量的刻度
  亮弧   = 当前占用（按 82% 绘制，对应工具里的「警告」阈值）
  中心点 = 探针

用 3x3 超采样做抗锯齿，逐尺寸直接绘制（不是缩放大图），
16px 下依然清晰。
"""

import math
import os
import struct
import zlib

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "app.ico")

SIZES = [16, 24, 32, 48, 64, 128, 256]
SS = 3  # 每个像素的超采样倍率（SS x SS）

# 品牌色：与工具箱界面一致
GRAD_A = (83, 74, 183)     # #534AB7 品牌紫
GRAD_B = (22, 100, 255)    # #1664FF 品牌蓝
ARC = (255, 255, 255)      # 进度弧：纯白
TRACK = (255, 255, 255)    # 刻度环：白色半透明
TRACK_ALPHA = 0.20
FACE_ALPHA = 0.18          # 表盘内圈压暗，增加层次

SWEEP_DEG = 250.0          # 仪表盘总张角
PROGRESS = 0.82            # 弧长比例（对应工具里的「警告」阈值）


def _rounded_alpha(x, y, size, radius):
    """圆角矩形的覆盖率（0/1）。x,y 为像素中心坐标。"""
    if x < 0 or y < 0 or x > size or y > size:
        return 0.0
    # 四条直边的内缩判断 + 四角圆弧
    cx = min(max(x, radius), size - radius)
    cy = min(max(y, radius), size - radius)
    if radius > 0:
        d = math.hypot(x - cx, y - cy)
        return 1.0 if d <= radius else 0.0
    return 1.0


def _sample(x, y, size):
    """返回该采样点的 (r, g, b, a)。"""
    # --- 背景圆角矩形 ---
    radius = size * 0.215
    if _rounded_alpha(x, y, size, radius) <= 0.0:
        return (0, 0, 0, 0)

    # 斜向渐变
    t = (x + y) / (2.0 * size) if size > 0 else 0.0
    t = 0.0 if t < 0.0 else (1.0 if t > 1.0 else t)
    r = int(round(GRAD_A[0] + (GRAD_B[0] - GRAD_A[0]) * t))
    g = int(round(GRAD_A[1] + (GRAD_B[1] - GRAD_A[1]) * t))
    b = int(round(GRAD_A[2] + (GRAD_B[2] - GRAD_A[2]) * t))

    # --- 仪表盘 ---
    c = size / 2.0
    dx = x - c
    dy = y - c
    d = math.hypot(dx, dy)

    ring_out = size * 0.358
    ring_in = size * 0.252

    if ring_in <= d <= ring_out:
        # 角度：0 = 正上方，顺时针为正
        ang = math.degrees(math.atan2(dx, -dy)) % 360.0
        # 把仪表盘起点对齐到 -SWEEP/2
        s = (ang + SWEEP_DEG / 2.0) % 360.0

        if s <= SWEEP_DEG:
            if s <= SWEEP_DEG * PROGRESS:
                return (ARC[0], ARC[1], ARC[2], 255)
            return (TRACK[0], TRACK[1], TRACK[2], int(round(TRACK_ALPHA * 255)))
        # 缺口以外的环段不画
        return (r, g, b, 255)

    # 表盘内圈：轻微压暗，让环更立体
    if d <= ring_in:
        k = FACE_ALPHA * (1.0 - d / ring_in * 0.45)
        return (int(r * (1 - k)), int(g * (1 - k)), int(b * (1 - k)), 255)

    return (r, g, b, 255)


def render(size):
    """渲染一张 size x size 的 RGBA 位图，返回 bytes(row-major)。"""
    buf = bytearray(size * size * 4)
    inv = 1.0 / (SS * SS)
    for py in range(size):
        for px in range(size):
            ar = ag = ab = aa = 0.0
            for sy in range(SS):
                for sx in range(SS):
                    x = px + (sx + 0.5) / SS
                    y = py + (sy + 0.5) / SS
                    sr, sg, sb, sa = _sample(x, y, size)
                    w = sa / 255.0
                    ar += sr * w
                    ag += sg * w
                    ab += sb * w
                    aa += sa
            aa *= inv
            a8 = int(round(aa))
            if a8 <= 0:
                continue
            # 反预乘
            wsum = aa / 255.0
            if wsum <= 0:
                continue
            r = int(round(ar * inv / wsum))
            g = int(round(ag * inv / wsum))
            b = int(round(ab * inv / wsum))
            r = 0 if r < 0 else (255 if r > 255 else r)
            g = 0 if g < 0 else (255 if g > 255 else g)
            b = 0 if b < 0 else (255 if b > 255 else b)
            o = (py * size + px) * 4
            buf[o + 0] = r
            buf[o + 1] = g
            buf[o + 2] = b
            buf[o + 3] = a8
    return bytes(buf)


def png_encode(size, rgba):
    """把 RGBA 原始位图编码成 PNG。"""
    raw = bytearray()
    stride = size * 4
    for y in range(size):
        raw.append(0)  # filter: none
        raw += rgba[y * stride:(y + 1) * stride]

    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        c += struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
        return c

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
            + chunk(b"IEND", b""))


def _blit(dst, dw, dh, src, sw, sh, ox, oy, scale, bg):
    """把 src 以 scale 倍最近邻放大后贴到 dst 上（dst 已填充 bg）。"""
    for y in range(sh):
        for x in range(sw):
            so = (y * sw + x) * 4
            sa = src[so + 3]
            if sa == 0:
                continue
            # 与背景做 alpha 混合
            a = sa / 255.0
            r = int(round(src[so] * a + bg[0] * (1 - a)))
            g = int(round(src[so + 1] * a + bg[1] * (1 - a)))
            b = int(round(src[so + 2] * a + bg[2] * (1 - a)))
            for yy in range(y * scale, (y + 1) * scale):
                ty = oy + yy
                if ty < 0 or ty >= dh:
                    continue
                for xx in range(x * scale, (x + 1) * scale):
                    tx = ox + xx
                    if tx < 0 or tx >= dw:
                        continue
                    to = (ty * dw + tx) * 4
                    dst[to] = r
                    dst[to + 1] = g
                    dst[to + 2] = b
                    dst[to + 3] = 255


def write_preview(images, path):
    """把若干尺寸放大后排成一行，用于人工核对小尺寸可辨认度。"""
    scale = 5
    pad = 12
    bg = (240, 242, 245)
    strip = [(s, render(s)) for s in (16, 24, 32, 48)]
    w = pad
    for s, _ in strip:
        w += s * scale + pad
    h = 48 * scale + pad * 2
    dst = bytearray(w * h * 4)
    for i in range(w * h):
        dst[i * 4 + 0] = bg[0]
        dst[i * 4 + 1] = bg[1]
        dst[i * 4 + 2] = bg[2]
        dst[i * 4 + 3] = 255
    x = pad
    for s, rgba in strip:
        oy = pad + (48 * scale - s * scale) // 2
        _blit(dst, w, h, rgba, s, s, x, oy, scale, bg)
        x += s * scale + pad
    with open(path, "wb") as f:
        f.write(png_encode_exact(w, h, bytes(dst)))


def png_encode_exact(w, h, rgba):
    """编码任意宽高的 RGBA 位图为 PNG。"""
    raw = bytearray()
    stride = w * 4
    for y in range(h):
        raw.append(0)
        raw += rgba[y * stride:(y + 1) * stride]

    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        c += struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
        return c

    ihdr = struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
            + chunk(b"IEND", b""))


def main():
    images = []
    for s in SIZES:
        print("rendering %dx%d ..." % (s, s))
        images.append((s, png_encode(s, render(s))))

    # ICO 容器：ICONDIR + ICONDIRENTRY[] + 各图数据
    n = len(images)
    header = struct.pack("<HHH", 0, 1, n)
    offset = 6 + 16 * n
    entries = b""
    blobs = b""
    for s, data in images:
        w = 0 if s >= 256 else s
        h = 0 if s >= 256 else s
        entries += struct.pack("<BBBBHHII", w, h, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
        blobs += data

    with open(OUT, "wb") as f:
        f.write(header + entries + blobs)

    print("wrote: %s (%d bytes, %d sizes)" % (os.path.abspath(OUT), os.path.getsize(OUT), n))

    prev = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_preview.png")
    write_preview(images, prev)
    print("preview: %s" % os.path.abspath(prev))


if __name__ == "__main__":
    main()
