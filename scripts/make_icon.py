"""Draws src/Shoji/app.ico: a vermilion ensō brush circle (which also reads as a gauge dial, open at the bottom)
around a small shoji window of backlit paper panes, on washi paper.
Large sizes get brush texture and paper grain; 16-24 px get a bolder, simpler drawing so they stay readable.
Usage (from the repo root):  python scripts/make_icon.py [preview.png]   (needs Pillow)"""
import math
import random
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
SS = 4  # supersampling

VERMILION = (206, 62, 40)
INK = (38, 30, 28)
PAPER_TOP = (247, 241, 229)
PAPER_BOTTOM = (233, 221, 200)


def gradient(n, top, bottom):
    img = Image.new("RGBA", (n, n))
    d = ImageDraw.Draw(img)
    for y in range(n):
        t = y / max(1, n - 1)
        d.line([(0, y), (n, y)], fill=tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,))
    return img


def backlit_pane(w, h):
    """Paper lit from behind: warm in the middle, cooler at the edges."""
    img = Image.new("RGBA", (w, h))
    px = img.load()
    cx, cy, r = w * 0.45, h * 0.4, math.hypot(w, h) * 0.7
    for y in range(h):
        for x in range(w):
            t = min(1, math.hypot(x - cx, y - cy) / r)
            px[x, y] = (int(255 - 12 * t), int(246 - 40 * t), int(226 - 70 * t), 255)
    return img


def draw(size):
    n = size * SS
    u = n / 64.0
    small = size <= 24
    rnd = random.Random(7)

    img = gradient(n, PAPER_TOP, PAPER_BOTTOM)
    if size >= 64:
        # Washi grain: a few faint fibres, blended on their own layer.
        layer = Image.new("RGBA", (n, n), (0, 0, 0, 0))
        grain = ImageDraw.Draw(layer)
        for _ in range(int(size * 0.35)):
            x, y = rnd.uniform(0, n), rnd.uniform(0, n)
            a = rnd.uniform(0, math.pi)
            L = rnd.uniform(3, 8) * u
            grain.line([(x, y), (x + math.cos(a) * L, y + math.sin(a) * L)], fill=(170, 145, 110, 34), width=max(1, int(0.3 * u)))
        img.alpha_composite(layer)
    if not small:
        # Warm light spilling from the window onto the paper.
        glow = Image.new("RGBA", (n, n), (0, 0, 0, 0))
        ImageDraw.Draw(glow).ellipse([16 * u, 16 * u, 48 * u, 48 * u], fill=(255, 196, 120, 90))
        img.alpha_composite(glow.filter(ImageFilter.GaussianBlur(6 * u)))

    # Shoji window: four backlit panes in a dark wood frame.
    d = ImageDraw.Draw(img)
    x0, y0, x1, y1 = (21 * u, 21 * u, 43 * u, 43 * u) if not small else (19 * u, 19 * u, 45 * u, 45 * u)
    frame = max(SS, round((2.4 if not small else 3.6) * u))
    pane = backlit_pane(int(x1 - x0), int(y1 - y0))
    img.paste(pane, (int(x0), int(y0)))
    mx, my = (x0 + x1) / 2, (y0 + y1) / 2
    if size >= 64:
        # Fine kumiko bars inside each pane (drawn before the frame, which covers their ends).
        thin = max(1, round(0.8 * u))
        wood = (92, 72, 60, 255)  # lighter than the frame, opaque (paper is behind it)
        for qx in (x0, mx):
            for qy in (y0, my):
                cx = qx + (mx - x0) / 2
                d.rectangle([cx - thin / 2, qy + frame / 2, cx + thin / 2, qy + (my - y0) - frame / 2], fill=wood)

    d.rectangle([x0, y0, x1, y1], outline=INK + (255,), width=frame)
    d.rectangle([mx - frame / 2, y0, mx + frame / 2, y1], fill=INK + (255,))
    d.rectangle([x0, my - frame / 2, x1, my + frame / 2], fill=INK + (255,))

    # Ensō: one brush stroke, heavy where the brush lands, tapering into a dry tail; open at the bottom like a dial.
    ring = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    rd = ImageDraw.Draw(ring)
    r = 23.5 * u if not small else 22 * u
    start, sweep, steps = 112, 318, 360
    base_w = (6.4 if not small else 8.2) * u
    for i in range(steps):
        t = i / steps
        a = math.radians(start + sweep * t)
        landing = 1.25 if t < 0.06 else 1.0          # brush lands a little heavier
        taper = math.sin(math.pi * min(1, t * 1.06 + 0.03)) ** 0.55
        w = base_w * (0.28 + 0.72 * taper) * landing
        rr = r + math.sin(t * 9) * 0.35 * u          # hand-drawn wobble
        x, y = 32 * u + math.cos(a) * rr, 32 * u + math.sin(a) * rr
        rd.ellipse([x - w / 2, y - w / 2, x + w / 2, y + w / 2], fill=VERMILION + (255,))
    if size >= 96:
        # Dry brush: a few bristle gaps, long and thin along the stroke, only in the last third where the ink runs out.
        holes = ImageDraw.Draw(ring)
        for k in range(4):
            off = (k - 1.5) * base_w * 0.2
            t = 0.62 + rnd.random() * 0.1
            while t < 1:
                length = rnd.uniform(0.02, 0.07) * (0.5 + t)
                if rnd.random() < 0.45 + 0.5 * t:
                    pts = []
                    for j in range(8):
                        a = math.radians(start + sweep * min(1, t + length * j / 7))
                        pts.append((32 * u + math.cos(a) * (r + off), 32 * u + math.sin(a) * (r + off)))
                    holes.line(pts, fill=(0, 0, 0, 0), width=max(1, int(rnd.uniform(0.35, 0.75) * u)))
                t += length + rnd.uniform(0.01, 0.04)
        ring = ring.filter(ImageFilter.GaussianBlur(0.2 * u))
    img.alpha_composite(ring)

    # Rounded-square tile with a soft inner edge.
    mask = Image.new("L", (n, n), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, n - 1, n - 1], radius=int(13 * u), fill=255)
    out = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    out.paste(img, (0, 0), mask)
    edge = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    ImageDraw.Draw(edge).rounded_rectangle([0, 0, n - 1, n - 1], radius=int(13 * u), outline=(150, 120, 90, 90), width=max(1, int(0.6 * u)))
    out.alpha_composite(edge)
    return out.resize((size, size), Image.LANCZOS)


root = Path(__file__).resolve().parent.parent
images = {s: draw(s) for s in SIZES}
ico = root / "src" / "Shoji" / "app.ico"
images[256].save(ico, format="ICO", sizes=[(s, s) for s in SIZES], append_images=[images[s] for s in SIZES if s != 256])
print(f"wrote {ico}")

if len(sys.argv) > 1:
    # Preview sheet: every size on dark and light backgrounds.
    show = [256, 96, 48, 32, 24, 16]
    sheet = Image.new("RGBA", (sum(s + 28 for s in show) + 28, 2 * 300), (0, 0, 0, 0))
    for row, bg in enumerate([(30, 31, 36, 255), (243, 243, 243, 255)]):
        band = Image.new("RGBA", (sheet.width, 300), bg)
        x = 28
        for s in show:
            band.alpha_composite(images[s], (x, 150 - s // 2))
            x += s + 28
        sheet.alpha_composite(band, (0, row * 300))
    sheet.save(sys.argv[1])
    print(f"preview {sys.argv[1]}")
