"""
Placeholder art for Templar Knights. Superseded: the shipped art is rendered in 3D by tools/art/blender/
(figures.py, tiles.py, scenes.py, icons.py). This tool only fills gaps for new content that has no render yet,
and never overwrites a rendered asset (a sprite with a sheet .json, or any tile/ui image) unless --force-all.

    python tools/art/placeholders.py            # make every missing placeholder
    python tools/art/placeholders.py --force    # remake them all (never touches files without a look entry)

Figures (monsters, NPCs, bosses) are drawn from tools/art/placeholder_looks.json: a body colour, trim,
head (helm, hood, crown, skull...), weapon, emblem and shape (humanoid, beast, swarm, chest). Bosses are
drawn larger and more ornate. Also makes ground tiles per act, the title screen and cutscene backdrops.
Everything is original, simple geometry: no reference images are used.
"""
import json, math, os, random, sys
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ASSETS = os.path.join(ROOT, "assets")
LOOKS = os.path.join(os.path.dirname(__file__), "placeholder_looks.json")
S = 2  # supersampling

def hexc(h, a=255):
    h = h.lstrip("#")
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), a)

def shade(c, k):
    return tuple(max(0, min(255, int(v * k))) for v in c[:3]) + (c[3],)

# ---------------------------------------------------------------- figures

def draw_weapon(d, kind, x, y, s, trim):
    """Weapon held at the figure's right side. (x, y) is the hand; s is the scale."""
    steel, wood, gold, dark = hexc("#C8CCD4"), hexc("#7A5230"), hexc("#E6C14A"), hexc("#2A2420")
    w = max(2, int(5 * s))
    if kind in ("sword", "greatsword"):
        L = (150 if kind == "greatsword" else 115) * s
        d.line([(x, y), (x - 8 * s, y - L)], fill=steel, width=w)
        d.line([(x - 18 * s, y - 14 * s), (x + 14 * s, y - 10 * s)], fill=gold, width=w)
    elif kind in ("maul", "club", "mace"):
        d.line([(x, y + 20 * s), (x - 6 * s, y - 100 * s)], fill=wood, width=w)
        head = steel if kind != "club" else shade(wood, 0.8)
        r = (22 if kind == "maul" else 16) * s
        if kind == "maul": d.rectangle([x - 6 * s - r, y - 100 * s - r * 0.7, x - 6 * s + r, y - 100 * s + r * 0.7], fill=head)
        else: d.ellipse([x - 6 * s - r, y - 100 * s - r, x - 6 * s + r, y - 100 * s + r], fill=head)
    elif kind in ("axe", "greataxe"):
        L = (130 if kind == "greataxe" else 100) * s
        d.line([(x, y + 20 * s), (x - 6 * s, y - L)], fill=wood, width=w)
        d.pieslice([x - 6 * s - 4 * s, y - L - 26 * s, x - 6 * s + 44 * s, y - L + 26 * s], 270, 90, fill=steel)
    elif kind in ("spear", "halberd"):
        d.line([(x, y + 40 * s), (x - 4 * s, y - 150 * s)], fill=wood, width=w)
        d.polygon([(x - 4 * s, y - 175 * s), (x - 12 * s, y - 145 * s), (x + 4 * s, y - 145 * s)], fill=steel)
        if kind == "halberd": d.pieslice([x - 4 * s - 2 * s, y - 150 * s - 18 * s, x - 4 * s + 30 * s, y - 150 * s + 18 * s], 270, 90, fill=steel)
    elif kind == "dagger":
        d.line([(x, y), (x - 4 * s, y - 45 * s)], fill=steel, width=w)
        d.line([(x - 10 * s, y - 6 * s), (x + 8 * s, y - 4 * s)], fill=gold, width=max(2, int(3 * s)))
    elif kind in ("crossbow", "bow"):
        if kind == "bow":
            d.arc([x - 40 * s, y - 70 * s, x + 10 * s, y + 30 * s], 260, 100, fill=wood, width=w)
            d.line([(x - 6 * s, y - 66 * s), (x - 6 * s, y + 26 * s)], fill=hexc("#E8E4DA"), width=max(1, int(2 * s)))
        else:
            d.line([(x - 40 * s, y - 10 * s), (x + 20 * s, y - 10 * s)], fill=wood, width=w)
            d.arc([x - 50 * s, y - 40 * s, x - 10 * s, y + 20 * s], 200, 340, fill=shade(wood, 0.7), width=w)
    elif kind == "book":
        d.rectangle([x - 26 * s, y - 34 * s, x + 14 * s, y + 4 * s], fill=hexc("#6E1C18"))
        d.rectangle([x - 22 * s, y - 30 * s, x + 10 * s, y], outline=gold, width=max(1, int(2 * s)))
    elif kind in ("staff", "crozier", "oar"):
        col = gold if kind == "crozier" else wood
        d.line([(x, y + 40 * s), (x - 4 * s, y - 150 * s)], fill=col, width=w)
        if kind == "crozier": d.arc([x - 34 * s, y - 182 * s, x + 2 * s, y - 140 * s], 180, 0, fill=col, width=w)
        if kind == "oar": d.ellipse([x - 18 * s, y - 180 * s, x + 10 * s, y - 120 * s], fill=wood)
    elif kind == "torch":
        d.line([(x, y + 10 * s), (x - 4 * s, y - 70 * s)], fill=wood, width=w)
        d.ellipse([x - 20 * s, y - 104 * s, x + 12 * s, y - 64 * s], fill=hexc("#F2A03A"))
        d.ellipse([x - 12 * s, y - 96 * s, x + 4 * s, y - 72 * s], fill=hexc("#FCE7A0"))
    elif kind == "whip":
        pts = [(x - i * 6 * s, y + math.sin(i * 0.9) * 14 * s + i * 4 * s) for i in range(10)]
        d.line(pts, fill=shade(wood, 0.6), width=max(2, int(3 * s)))
    elif kind in ("scroll", "seal"):
        d.rectangle([x - 22 * s, y - 30 * s, x + 6 * s, y + 4 * s], fill=hexc("#E8DCB8"))
        if kind == "seal": d.ellipse([x - 16 * s, y - 20 * s, x, y - 4 * s], fill=hexc("#A01818"))
    elif kind == "keys_ring":
        d.ellipse([x - 20 * s, y - 20 * s, x + 4 * s, y + 4 * s], outline=gold, width=w)
        for i in range(3): d.line([(x - 8 * s + i * 5 * s, y), (x - 8 * s + i * 5 * s, y + 22 * s)], fill=gold, width=max(2, int(3 * s)))
    elif kind == "hammer":
        d.line([(x, y + 10 * s), (x - 4 * s, y - 70 * s)], fill=wood, width=w)
        d.rectangle([x - 22 * s, y - 86 * s, x + 14 * s, y - 64 * s], fill=hexc("#5A5A60"))

def draw_emblem(d, kind, cx, cy, s, trim):
    red, gold, black = hexc("#B3241E"), hexc("#E6C14A"), hexc("#1A1A1A")
    if kind in ("cross", "black_cross"):
        c = red if kind == "cross" else black
        d.rectangle([cx - 7 * s, cy - 40 * s, cx + 7 * s, cy + 40 * s], fill=c)
        d.rectangle([cx - 30 * s, cy - 14 * s, cx + 30 * s, cy], fill=c)
    elif kind == "fleur":
        c = gold
        d.ellipse([cx - 7 * s, cy - 30 * s, cx + 7 * s, cy + 4 * s], fill=c)
        d.ellipse([cx - 24 * s, cy - 16 * s, cx - 6 * s, cy + 6 * s], fill=c)
        d.ellipse([cx + 6 * s, cy - 16 * s, cx + 24 * s, cy + 6 * s], fill=c)
        d.rectangle([cx - 20 * s, cy + 4 * s, cx + 20 * s, cy + 10 * s], fill=c)
    elif kind == "keys":
        for sgn in (-1, 1):
            d.line([(cx - 20 * s * sgn, cy - 24 * s), (cx + 20 * s * sgn, cy + 24 * s)], fill=gold, width=int(6 * s))
            d.ellipse([cx - 20 * s * sgn - 9 * s, cy - 24 * s - 9 * s, cx - 20 * s * sgn + 9 * s, cy - 24 * s + 9 * s], outline=gold, width=int(4 * s))
    elif kind == "eye":
        d.ellipse([cx - 22 * s, cy - 10 * s, cx + 22 * s, cy + 10 * s], fill=hexc("#E8E4DA"))
        d.ellipse([cx - 7 * s, cy - 7 * s, cx + 7 * s, cy + 7 * s], fill=hexc("#8A1C18"))

def draw_head(d, kind, cx, cy, r, body, trim):
    skin, steel, dark = hexc("#D8B08C"), hexc("#9EA2AA"), hexc("#2A2420")
    if kind in ("helm", "kettle", "greathelm", "greathelm_plume", "crownhelm"):
        if kind == "kettle":
            d.ellipse([cx - r * 1.5, cy - r * 0.1, cx + r * 1.5, cy + r * 0.45], fill=shade(steel, 0.85))
        d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=steel)
        if kind.startswith("greathelm") or kind == "crownhelm":
            d.rectangle([cx - r, cy - r * 0.5, cx + r, cy + r], fill=steel)
            d.rectangle([cx - r * 0.75, cy - r * 0.05, cx + r * 0.75, cy + r * 0.12], fill=dark)
            d.rectangle([cx - r * 0.08, cy - r * 0.05, cx + r * 0.08, cy + r * 0.8], fill=dark)
        else:
            d.rectangle([cx - r * 0.35, cy - r * 0.1, cx + r * 0.35, cy + r * 0.12], fill=dark)
        if kind == "greathelm_plume":
            d.ellipse([cx - r * 0.3, cy - r * 2.0, cx + r * 0.5, cy - r * 0.8], fill=trim)
        if kind == "crownhelm":
            draw_crown(d, cx, cy - r * 1.05, r, hexc("#F2D25A"))
    elif kind in ("hood", "hood_black", "hood_white"):
        col = {"hood": body, "hood_black": hexc("#1A1816"), "hood_white": hexc("#F0ECE2")}[kind]
        d.ellipse([cx - r * 1.05, cy - r * 1.1, cx + r * 1.05, cy + r], fill=shade(col, 0.9))
        d.ellipse([cx - r * 0.6, cy - r * 0.55, cx + r * 0.6, cy + r * 0.75], fill=hexc("#2A221C"))
    elif kind in ("bare", "bare_beard", "tonsure", "hat", "feathered"):
        d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=skin)
        d.ellipse([cx - r * 0.45, cy - r * 0.15, cx - r * 0.2, cy + r * 0.1], fill=dark)
        d.ellipse([cx + r * 0.2, cy - r * 0.15, cx + r * 0.45, cy + r * 0.1], fill=dark)
        if kind == "bare_beard": d.pieslice([cx - r * 0.9, cy - r * 0.2, cx + r * 0.9, cy + r * 1.3], 0, 180, fill=hexc("#5A4030"))
        if kind == "tonsure": d.arc([cx - r, cy - r, cx + r, cy + r], 200, 340, fill=hexc("#5A4030"), width=int(r * 0.35))
        if kind in ("hat", "feathered"):
            d.rectangle([cx - r * 1.3, cy - r * 0.95, cx + r * 1.3, cy - r * 0.7], fill=shade(body, 0.7))
            d.rectangle([cx - r * 0.8, cy - r * 1.6, cx + r * 0.8, cy - r * 0.8], fill=shade(body, 0.7))
            if kind == "feathered": d.ellipse([cx + r * 0.4, cy - r * 2.4, cx + r * 1.1, cy - r * 0.9], fill=trim)
    elif kind == "coif":
        d.ellipse([cx - r * 1.05, cy - r * 1.05, cx + r * 1.05, cy + r * 1.05], fill=hexc("#8C8E94"))
        d.ellipse([cx - r * 0.55, cy - r * 0.45, cx + r * 0.55, cy + r * 0.65], fill=skin)
    elif kind == "executioner":
        d.ellipse([cx - r, cy - r * 1.2, cx + r, cy + r], fill=hexc("#141210"))
        d.ellipse([cx - r * 0.45, cy - r * 0.2, cx - r * 0.15, cy + r * 0.05], fill=hexc("#E8E4DA"))
        d.ellipse([cx + r * 0.15, cy - r * 0.2, cx + r * 0.45, cy + r * 0.05], fill=hexc("#E8E4DA"))
    elif kind in ("skull", "skull_mitre"):
        d.ellipse([cx - r, cy - r, cx + r, cy + r * 0.8], fill=hexc("#E2DAC4"))
        d.ellipse([cx - r * 0.55, cy - r * 0.3, cx - r * 0.1, cy + r * 0.15], fill=dark)
        d.ellipse([cx + r * 0.1, cy - r * 0.3, cx + r * 0.55, cy + r * 0.15], fill=dark)
        if kind == "skull_mitre": draw_mitre(d, cx, cy - r * 0.8, r, hexc("#E8E2D2"), hexc("#8A1C18"))
    elif kind == "wraith":
        d.ellipse([cx - r, cy - r * 1.2, cx + r, cy + r], fill=shade(body, 0.8))
        d.ellipse([cx - r * 0.5, cy - r * 0.3, cx - r * 0.15, cy], fill=hexc("#E8F4FF"))
        d.ellipse([cx + r * 0.15, cy - r * 0.3, cx + r * 0.5, cy], fill=hexc("#E8F4FF"))
    elif kind == "stone":
        d.rectangle([cx - r, cy - r, cx + r, cy + r], fill=hexc("#8A867E"))
        d.line([(cx - r * 0.5, cy), (cx + r * 0.5, cy)], fill=hexc("#5A564E"), width=3)
    elif kind in ("mitre", "cardinal", "tiara", "crown"):
        d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=skin)
        d.ellipse([cx - r * 0.45, cy - r * 0.15, cx - r * 0.2, cy + r * 0.1], fill=dark)
        d.ellipse([cx + r * 0.2, cy - r * 0.15, cx + r * 0.45, cy + r * 0.1], fill=dark)
        if kind == "mitre": draw_mitre(d, cx, cy - r * 0.8, r, hexc("#F0ECE2"), hexc("#E6C14A"))
        if kind == "cardinal": d.pieslice([cx - r * 1.2, cy - r * 1.7, cx + r * 1.2, cy - r * 0.2], 180, 360, fill=hexc("#A01818"))
        if kind == "tiara":
            for i, k in enumerate((1.0, 0.85, 0.7)):
                d.ellipse([cx - r * k, cy - r * (0.9 + i * 0.45) - r * 0.35, cx + r * k, cy - r * (0.9 + i * 0.45) + r * 0.25], fill=hexc("#F0ECE2"), outline=hexc("#E6C14A"), width=3)
        if kind == "crown": draw_crown(d, cx, cy - r * 0.9, r, hexc("#F2D25A"))

def draw_crown(d, cx, cy, r, gold):
    d.rectangle([cx - r * 0.9, cy - r * 0.15, cx + r * 0.9, cy + r * 0.25], fill=gold)
    for i in range(-2, 3):
        x = cx + i * r * 0.42
        d.polygon([(x - r * 0.18, cy - r * 0.1), (x, cy - r * 0.65), (x + r * 0.18, cy - r * 0.1)], fill=gold)

def draw_mitre(d, cx, cy, r, white, gold):
    d.polygon([(cx - r * 0.8, cy + r * 0.2), (cx, cy - r * 1.4), (cx + r * 0.8, cy + r * 0.2)], fill=white)
    d.line([(cx, cy - r * 1.2), (cx, cy + r * 0.2)], fill=gold, width=4)

def figure(look, size=256):
    W = size * S
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    body = hexc(look.get("body", "#6A6050"))
    trim = hexc(look.get("trim", "#C9A227"))
    shape = look.get("shape", "humanoid")
    boss = look.get("boss", False)
    k = W / 256  # unit scale

    if shape == "chest":
        wood, iron = hexc("#6A4A2A"), hexc("#3A3A40")
        d.rectangle([60 * k, 120 * k, 196 * k, 220 * k], fill=wood)
        d.pieslice([60 * k, 80 * k, 196 * k, 160 * k], 180, 360, fill=shade(wood, 1.1))
        for x in (80, 128, 176): d.rectangle([x * k - 5 * k, 90 * k, x * k + 5 * k, 220 * k], fill=iron)
        d.rectangle([116 * k, 140 * k, 140 * k, 166 * k], fill=hexc("#E6C14A"))
        draw_emblem(d, "cross", 128 * k, 185 * k, 0.45 * k, trim)
        return finish(img, size)
    if shape == "swarm":
        rng = random.Random(look.get("body", "") + "swarm")
        for _ in range(9):
            x, y = rng.uniform(50, 206) * k, rng.uniform(140, 225) * k
            d.ellipse([x - 16 * k, y - 9 * k, x + 16 * k, y + 9 * k], fill=shade(body, rng.uniform(0.8, 1.2)))
            d.ellipse([x + 10 * k, y - 4 * k, x + 15 * k, y + 1 * k], fill=hexc("#E0463C"))
        return finish(img, size)
    if shape == "beast":
        big = 1.25 if look.get("big") else 1.0
        cy = 175 * k
        d.ellipse([46 * k / big, cy - 42 * k * big, 196 * k * min(1.15, big), cy + 30 * k * big], fill=body)
        for lx in (70, 100, 150, 175):
            d.rectangle([lx * k, cy + 10 * k, lx * k + 12 * k, cy + 52 * k], fill=shade(body, 0.75))
        hx = 205 * k
        d.ellipse([hx - 34 * k * big, cy - 58 * k * big, hx + 22 * k * big, cy - 4 * k], fill=shade(body, 1.1))
        d.ellipse([hx - 8 * k, cy - 44 * k * big, hx + 2 * k, cy - 34 * k * big], fill=hexc(look.get("eyes", "#E0463C")))
        if look.get("tusk"): d.polygon([(hx + 10 * k, cy - 20 * k), (hx + 30 * k, cy - 34 * k), (hx + 16 * k, cy - 14 * k)], fill=hexc("#F0ECE2"))
        return finish(img, size)

    # Humanoid: robe or tabard body, head, weapon, emblem, optional shield and cape.
    scale = 1.0 if boss else 0.92
    if look.get("big"): scale *= 1.08
    top, bottom = (96 if boss else 100) * k, 236 * k
    half_top, half_bottom = 46 * k * scale, 70 * k * scale
    cx = 128 * k
    if look.get("cape"):
        cape = hexc(look["cape"])
        d.polygon([(cx - half_top * 1.1, top + 4 * k), (cx + half_top * 1.1, top + 4 * k), (cx + half_bottom * 1.25, bottom), (cx - half_bottom * 1.25, bottom)], fill=cape)
    alpha = 170 if look.get("ghost") else 255
    bcol = body[:3] + (alpha,)
    d.polygon([(cx - half_top, top), (cx + half_top, top), (cx + half_bottom, bottom), (cx - half_bottom, bottom)], fill=bcol)
    # Trim down both edges.
    d.line([(cx - half_top, top), (cx - half_bottom, bottom)], fill=trim, width=int(5 * k))
    d.line([(cx + half_top, top), (cx + half_bottom, bottom)], fill=trim, width=int(5 * k))
    if look.get("stone"):
        for i in range(5): d.line([(cx - half_bottom + i * 30 * k, top + 20 * k), (cx - half_bottom + i * 28 * k, bottom)], fill=shade(body, 0.7), width=int(2 * k))
    if look.get("emblem"): draw_emblem(d, look["emblem"], cx, top + 62 * k, 0.9 * k * scale, trim)
    if boss:
        d.rectangle([cx - half_top, top + 2 * k, cx + half_top, top + 14 * k], fill=trim)
    r = 30 * k * scale
    draw_head(d, look.get("head", "hood"), cx, top - r * 0.85, r, body, trim)
    weapon = look.get("weapon", "none")
    if weapon != "none": draw_weapon(d, weapon, cx - half_top - 12 * k, top + 80 * k, k * scale, trim)
    if look.get("shield"):
        sx, sy = cx + half_top + 4 * k, top + 40 * k
        d.polygon([(sx, sy), (sx + 50 * k, sy), (sx + 50 * k, sy + 50 * k), (sx + 25 * k, sy + 82 * k), (sx, sy + 50 * k)], fill=shade(trim, 0.55) if look.get("emblem") != "cross" else hexc("#F0ECE2"))
        if look.get("emblem"): draw_emblem(d, look["emblem"], sx + 25 * k, sy + 38 * k, 0.55 * k, trim)
    if look.get("undead"):
        img = Image.alpha_composite(img, Image.new("RGBA", img.size, (90, 130, 110, 0)))
    return finish(img, size, glow=hexc("#F2D25A") if boss else None)

def finish(img, size, glow=None):
    out = img.resize((size, size), Image.LANCZOS)
    if glow:
        a = out.split()[3].filter(ImageFilter.GaussianBlur(4))
        halo = Image.new("RGBA", out.size, glow[:3] + (0,))
        halo.putalpha(a.point(lambda v: int(v * 0.35)))
        out = Image.alpha_composite(halo, out)
    return out

# ---------------------------------------------------------------- tiles, title, cutscenes

TILES = {
    "act1_burning": ("#3A2A20", "#5A3A24", 0.18), "act2_cobbles": ("#4A4640", "#3A3632", 0.25), "act2_crypt": ("#3A3A36", "#2C2A28", 0.2),
    "act2_yard": ("#5A5040", "#4A4232", 0.22), "act2_cave": ("#2E2A28", "#3A3430", 0.25), "act2_stone": ("#4A4A4E", "#3A3A3E", 0.18),
    "act3_gardens": ("#4A5A3A", "#3E4E30", 0.22), "act3_forest": ("#34482A", "#2A3A22", 0.28), "act3_cloister": ("#6A6258", "#5A524A", 0.15),
    "act3_court": ("#7A7060", "#6A6050", 0.14), "act3_cathedral": ("#3E3C44", "#302E36", 0.2), "act3_marble": ("#8A8478", "#9A948A", 0.1),
    "act4_camp": ("#5A5A58", "#E8E8EC", 0.3), "act4_square": ("#5A5048", "#4A4038", 0.22), "act4_palace": ("#6A5E4A", "#7A6E58", 0.12),
    "act4_cathedral": ("#4A4650", "#3C3842", 0.16), "act4_tower": ("#3A3838", "#2C2A2A", 0.2),
}

def tile(name, a, b, noise):
    rng = random.Random(name)
    W = 128
    img = Image.new("RGBA", (W, W), hexc(a))
    d = ImageDraw.Draw(img)
    for _ in range(int(900 * noise)):
        x, y = rng.randrange(W), rng.randrange(W)
        r = rng.choice((1, 1, 2, 3))
        d.ellipse([x - r, y - r, x + r, y + r], fill=hexc(b, rng.randrange(90, 220)))
    if any(k in name for k in ("cobbles", "court", "palace", "marble", "cloister", "square")):
        step = 32 if "cobbles" in name or "square" in name else 64
        for i in range(0, W, step):
            d.line([(i, 0), (i, W)], fill=shade(hexc(a), 0.7), width=1)
            d.line([(0, i), (W, i)], fill=shade(hexc(a), 0.7), width=1)
    if "burning" in name:
        for _ in range(12):
            x, y = rng.randrange(W), rng.randrange(W)
            d.ellipse([x - 2, y - 2, x + 2, y + 2], fill=hexc("#F28A3A", 200))
    # Wrap the edges so the tile repeats without seams.
    img = img.filter(ImageFilter.SMOOTH)
    return img.convert("RGB")  # ground tiles are always fully opaque

def gradient(w, h, top, bottom):
    img = Image.new("RGBA", (w, h))
    t, b = hexc(top), hexc(bottom)
    px = img.load()
    for y in range(h):
        k = y / (h - 1)
        c = tuple(int(t[i] + (b[i] - t[i]) * k) for i in range(3)) + (255,)
        for x in range(w): px[x, y] = c
    return img

def scene(kind, w=1280, h=720, seed="x"):
    """A simple silhouetted backdrop: night sky, skyline, and a light source."""
    rng = random.Random(seed)
    palettes = {
        "fire": ("#120A08", "#5A2210", "#F28A3A"), "crypt": ("#0A0A0C", "#2A2620", "#E6C14A"), "town": ("#0E1018", "#3A3024", "#E6A84A"),
        "castle": ("#141A28", "#3A3A44", "#C9D6E8"), "court": ("#1A1624", "#4A3A5A", "#F2D25A"), "cathedral": ("#0C0C14", "#2A2634", "#9FC6FF"),
        "paris": ("#100C10", "#4A1E14", "#F2A03A"), "tower": ("#08080C", "#24242C", "#E8E4DA"), "dawn": ("#2A3A5A", "#E8B87A", "#FFF4D8"),
    }
    top, bottom, light = palettes[kind]
    img = gradient(w, h, top, bottom)
    d = ImageDraw.Draw(img)
    lx, ly = rng.randint(w // 4, 3 * w // 4), rng.randint(h // 6, h // 3)
    glow = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    for r in range(260, 0, -20):
        gd.ellipse([lx - r, ly - r, lx + r, ly + r], fill=hexc(light, int(10 + (260 - r) / 4)))
    img = Image.alpha_composite(img, glow.filter(ImageFilter.GaussianBlur(30)))
    d = ImageDraw.Draw(img)
    sil = hexc("#06060A")
    x = 0
    while x < w:
        bw = rng.randint(60, 180)
        bh = rng.randint(h // 6, h // 2) if kind not in ("tower", "cathedral") else rng.randint(h // 8, h // 3)
        d.rectangle([x, h - bh, x + bw, h], fill=sil)
        if rng.random() < 0.5: d.polygon([(x, h - bh), (x + bw / 2, h - bh - rng.randint(30, 90)), (x + bw, h - bh)], fill=sil)
        if rng.random() < 0.4: d.rectangle([x + bw / 2 - 5, h - bh - 140, x + bw / 2 + 5, h - bh], fill=sil)
        x += bw - rng.randint(0, 30)
    if kind in ("tower", "cathedral"):
        tx = w // 2 + rng.randint(-200, 200)
        d.rectangle([tx - 70, h // 5, tx + 70, h], fill=sil)
        d.polygon([(tx - 80, h // 5), (tx, h // 5 - 120), (tx + 80, h // 5)], fill=sil)
    if kind in ("fire", "paris"):
        flames = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        fd = ImageDraw.Draw(flames)
        for _ in range(40):
            fx, fy = rng.randint(0, w), h - rng.randint(h // 5, h // 2)
            fd.ellipse([fx - 30, fy - 60, fx + 30, fy + 40], fill=hexc("#F2A03A", 60))
        img = Image.alpha_composite(img, flames.filter(ImageFilter.GaussianBlur(18)))
    # Vignette.
    vig = Image.new("L", (w, h), 0)
    vd = ImageDraw.Draw(vig)
    vd.ellipse([-w * 0.2, -h * 0.3, w * 1.2, h * 1.3], fill=255)
    vig = vig.filter(ImageFilter.GaussianBlur(120))
    dark = Image.new("RGBA", (w, h), (0, 0, 0, 255))
    dark.putalpha(vig.point(lambda v: 255 - v))
    return Image.alpha_composite(img, dark)

CUTSCENE_KIND = {
    "prologue": "fire", "sanctuary_first": "crypt", "ledger_of_payns": "crypt", "town_in_shadow": "town", "castle_retaken": "castle",
    "hermit_secret": "crypt", "marcus_archive": "crypt", "valois_estate": "court", "court_disguise": "court", "de_molay_bars": "cathedral",
    "legate_vault": "court", "remnant_camp": "paris", "stake_at_greve": "paris", "tower_of_truth": "tower",
    "ending_rescue": "dawn", "ending_vengeance": "paris", "ending_darkness": "tower", "ending_redemption": "dawn",
}

def main(force=False):
    made = 0
    looks = json.load(open(LOOKS, encoding="utf-8"))
    force_all = "--force-all" in sys.argv
    for name, look in sorted(looks.items()):
        path = os.path.join(ASSETS, "sprites", name + ".png")
        if os.path.exists(os.path.join(ASSETS, "sprites", name + ".json")) and not force_all: continue
        if os.path.exists(path) and not force: continue
        figure(look).save(path)
        made += 1
    os.makedirs(os.path.join(ASSETS, "tiles"), exist_ok=True)
    for name, (a, b, n) in TILES.items():
        path = os.path.join(ASSETS, "tiles", name + ".png")
        if os.path.exists(path) and not force_all: continue
        tile(name, a, b, n).save(path)
        made += 1
    os.makedirs(os.path.join(ASSETS, "ui"), exist_ok=True)
    title = os.path.join(ASSETS, "ui", "title.png")
    if force_all or not os.path.exists(title):
        img = scene("fire", 1920, 1080, "title")
        knight = figure(dict(body="#F0ECE2", trim="#B3241E", head="greathelm", weapon="sword", emblem="cross", shield=True, boss=True), 512)
        img.alpha_composite(knight, (1920 - 760, 1080 - 600))
        img.convert("RGB").save(title, quality=92)
        made += 1
    cdir = os.path.join(ASSETS, "ui")
    for cid, kind in CUTSCENE_KIND.items():
        path = os.path.join(cdir, f"cutscene_{cid}.png")
        if os.path.exists(path) and not force_all: continue
        scene(kind, seed=cid).convert("RGB").save(path)
        made += 1
    print(f"placeholders: made {made} image(s)")

if __name__ == "__main__":
    main(force="--force" in sys.argv)
