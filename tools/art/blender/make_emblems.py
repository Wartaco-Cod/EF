"""Draws the heraldic emblem decals used by figures.py (original shapes, 512x512 RGBA, white-on-clear masks).

    python tools/art/blender/make_emblems.py
The figure builder tints the mask with the emblem colour, so one mask serves every colour variant.
"""
import math, os
from PIL import Image, ImageDraw, ImageFilter

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "emblems")
N, S = 512, 4  # output size, supersampling

def canvas():
    img = Image.new("L", (N * S, N * S), 0)
    return img, ImageDraw.Draw(img)

def save(img, name):
    img = img.resize((N, N), Image.LANCZOS)
    rgba = Image.new("RGBA", (N, N), (255, 255, 255, 0))
    rgba.putalpha(img)
    rgba.save(os.path.join(OUT, name + ".png"))

def P(x, y):  # unit coordinates (-1..1, y down) to pixels
    return ((x + 1) * N * S / 2, (y + 1) * N * S / 2)

def cross_pattee():
    img, d = canvas()
    # Four arms flaring outward with concave sides: each arm is a polygon from a narrow root to a wide end.
    for ang in range(4):
        pts = []
        for t in [i / 20 for i in range(21)]:
            # Left edge curving out.
            r = 0.1 + 0.8 * t
            w = 0.11 + 0.26 * t ** 2.2
            pts.append((r, -w))
        pts += [(r, w) for r, w in reversed([(0.1 + 0.8 * t, 0.11 + 0.26 * t ** 2.2) for t in [i / 20 for i in range(21)]])]
        pts = [(0, -0.11)] + pts + [(0, 0.11)]
        a = ang * math.pi / 2
        d.polygon([P(x * math.cos(a) - y * math.sin(a), x * math.sin(a) + y * math.cos(a)) for x, y in pts], fill=255)
    d.rectangle([*P(-0.12, -0.12), *P(0.12, 0.12)], fill=255)
    return img

def bez(p0, p1, p2, p3, n=40):
    out = []
    for i in range(n + 1):
        t = i / n
        u = 1 - t
        out.append((u**3 * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t**3 * p3[0],
                    u**3 * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t**3 * p3[1]))
    return out

def fleur():
    img, d = canvas()
    # Central petal: tall, pointed, swelling in the middle.
    right = bez((0.0, -0.95), (0.08, -0.7), (0.26, -0.45), (0.1, 0.08))
    d.polygon([P(x, y) for x, y in right] + [P(-x, y) for x, y in reversed(right)], fill=255)
    for side in (-1, 1):
        outer = bez((0.06, 0.08), (0.1, -0.62), (0.86, -0.62), (0.66, 0.1))
        inner = bez((0.66, 0.1), (0.62, -0.28), (0.3, -0.36), (0.2, 0.08))
        d.polygon([P(side * x, y) for x, y in outer + inner], fill=255)
        hook = bez((0.66, 0.1), (0.64, 0.2), (0.56, 0.22), (0.5, 0.16), 12)
        d.line([P(side * x, y) for x, y in hook], fill=255, width=int(0.05 * N * S / 2))
        lo_out = bez((0.04, 0.24), (0.1, 0.52), (0.34, 0.6), (0.44, 0.42))
        lo_in = bez((0.44, 0.42), (0.3, 0.44), (0.16, 0.38), (0.1, 0.24))
        d.polygon([P(side * x, y) for x, y in lo_out + lo_in], fill=255)
    d.polygon([P(-0.08, 0.2), P(0.08, 0.2), P(0.0, 0.62)], fill=255)
    d.rounded_rectangle([*P(-0.4, 0.06), *P(0.4, 0.22)], radius=0.04 * N * S, fill=255)
    return img

def key(d, ang, flip):
    def R(x, y):
        x = -x if flip else x
        c, s = math.cos(ang), math.sin(ang)
        return P(x * c - y * s, x * s + y * c)
    # Bow (ring) at the bottom, shaft up, bit near the top.
    bow = [R(0.18 * math.cos(t / 30 * 2 * math.pi), 0.62 + 0.18 * math.sin(t / 30 * 2 * math.pi)) for t in range(31)]
    d.polygon(bow, fill=255)
    hole = [R(0.08 * math.cos(t / 30 * 2 * math.pi), 0.62 + 0.08 * math.sin(t / 30 * 2 * math.pi)) for t in range(31)]
    d.polygon(hole, fill=0)
    d.polygon([R(-0.05, 0.45), R(0.05, 0.45), R(0.05, -0.85), R(-0.05, -0.85)], fill=255)
    d.polygon([R(0.05, -0.85), R(0.3, -0.85), R(0.3, -0.72), R(0.18, -0.72), R(0.18, -0.62), R(0.3, -0.62), R(0.3, -0.5), R(0.05, -0.5)], fill=255)
    d.polygon([R(-0.12, 0.3), R(0.12, 0.3), R(0.12, 0.38), R(-0.12, 0.38)], fill=255)

def keys():
    img, d = canvas()
    key(d, math.radians(35), False)
    key(d, math.radians(-35), True)
    return img

def eye():
    img, d = canvas()
    for i in range(12):  # rays
        a = i / 12 * 2 * math.pi
        d.polygon([P(0.55 * math.cos(a - 0.08), 0.55 * math.sin(a - 0.08)), P(0.95 * math.cos(a), 0.95 * math.sin(a)), P(0.55 * math.cos(a + 0.08), 0.55 * math.sin(a + 0.08))], fill=255)
    top = [P(x / 20 * 0.8 - 0.8 + 0.8 * 0, 0) for x in range(0)]
    almond = [P(-0.62 + 1.24 * t / 40, -0.34 * math.sin(math.pi * t / 40)) for t in range(41)] + \
             [P(0.62 - 1.24 * t / 40, 0.34 * math.sin(math.pi * t / 40)) for t in range(41)]
    d.polygon(almond, fill=255)
    inner = [P(-0.52 + 1.04 * t / 40, -0.25 * math.sin(math.pi * t / 40)) for t in range(41)] + \
            [P(0.52 - 1.04 * t / 40, 0.25 * math.sin(math.pi * t / 40)) for t in range(41)]
    d.polygon(inner, fill=0)
    d.ellipse([*P(-0.2, -0.2), *P(0.2, 0.2)], fill=255)
    return img

if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    for name, fn in (("cross", cross_pattee), ("fleur", fleur), ("keys", keys), ("eye", eye)):
        save(fn(), name)
    print("emblems written to", OUT)
