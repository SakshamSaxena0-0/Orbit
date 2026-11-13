"""Draws Orbit's icon (a wheel of rounded glass slices around a hub) and writes a multi-size .ico.

usage: python -I make_icon.py <out.ico> [preview.png]
"""
import math
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter

S = 1024                     # drawn large, then scaled down
C = S / 2
OUTER, INNER, HUB = 470, 262, 150
SLICES, GAP = 6, 30          # gap in pixels, constant width like the app's slices
COLOURS = [(79, 209, 255), (96, 140, 255), (140, 108, 255), (214, 98, 236), (255, 99, 162), (255, 160, 90)]


def slice_mask(i):
    """One slice: a ring sector with parallel seams, corners rounded by blur + threshold."""
    m = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(m)
    span = 360 / SLICES
    mid = -90 + i * span
    d.pieslice([C - OUTER, C - OUTER, C + OUTER, C + OUTER], mid - span / 2, mid + span / 2, fill=255)
    d.ellipse([C - INNER, C - INNER, C + INNER, C + INNER], fill=0)
    # Cut the seams as bands of constant width along both edges.
    for edge in (mid - span / 2, mid + span / 2):
        a = math.radians(edge)
        dx, dy = math.cos(a), math.sin(a)
        nx, ny = -dy * GAP / 2, dx * GAP / 2
        far = OUTER + 40
        d.polygon([(C + nx, C + ny), (C + dx * far + nx, C + dy * far + ny),
                   (C + dx * far - nx, C + dy * far - ny), (C - nx, C - ny)], fill=0)
    return m.filter(ImageFilter.GaussianBlur(16)).point(lambda v: 255 if v > 128 else 0).filter(ImageFilter.GaussianBlur(1.2))


def vertical(top, bottom):
    g = Image.new("RGBA", (1, S))
    for y in range(S):
        t = y / (S - 1)
        g.putpixel((0, y), tuple(int(top[k] + (bottom[k] - top[k]) * t) for k in range(4)))
    return g.resize((S, S))


def draw():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    shine = vertical((255, 255, 255, 120), (255, 255, 255, 0))
    for i, col in enumerate(COLOURS):
        mask = slice_mask(i)
        body = vertical(tuple(min(255, int(c * 1.08)) for c in col) + (255,), tuple(int(c * 0.72) for c in col) + (255,))
        layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        layer.paste(body, (0, 0), mask)
        # Glass: a highlight over the upper half of each slice, and a bright rim.
        hl = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        hl.paste(shine, (0, 0), ImageChops.multiply(mask, Image.new("L", (S, S), 150)))
        layer = Image.alpha_composite(layer, hl)
        rim = ImageChops.subtract(mask, mask.filter(ImageFilter.MinFilter(9)))
        layer.paste((255, 255, 255, 255), (0, 0), ImageChops.multiply(rim, Image.new("L", (S, S), 110)))
        img = Image.alpha_composite(img, layer)

    hub = Image.new("L", (S, S), 0)
    ImageDraw.Draw(hub).ellipse([C - HUB, C - HUB, C + HUB, C + HUB], fill=255)
    hub = hub.filter(ImageFilter.GaussianBlur(1.5))
    hub_layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    hub_layer.paste(vertical((250, 252, 255, 255), (214, 222, 240, 255)), (0, 0), hub)
    img = Image.alpha_composite(img, hub_layer)

    # A soft shadow so the wheel reads on light taskbars too.
    shadow = img.split()[3].filter(ImageFilter.GaussianBlur(18)).point(lambda v: v * 0.45)
    base = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    base.paste((10, 12, 20, 255), (0, 14), shadow)
    return Image.alpha_composite(base, img)


def main():
    out = sys.argv[1]
    big = draw()
    sizes = [256, 128, 64, 48, 40, 32, 24, 20, 16]
    frames = [big.resize((n, n), Image.LANCZOS) for n in sizes]
    frames[0].save(out, format="ICO", sizes=[(n, n) for n in sizes], append_images=frames[1:])
    if len(sys.argv) > 2:
        big.resize((256, 256), Image.LANCZOS).save(sys.argv[2])


if __name__ == "__main__":
    main()
