"""
Acts III and IV interiors and Paris, built from primitives and rendered with the scenery camera (scenery.render):
palace tapestries, banquet tables, thrones on a dais, suits of armour, bookcases, carpet runners, statues of saints,
altars, rows of pews, stained-glass walls, tomb effigies, the stake and its faggots, a scaffold, wall shackles,
reliquary chests and a font.

    blender -b --python tools/art/blender/interiors_art.py -- [--names altar,pews] [--samples 48]

Writes assets/sprites/prop_<name>_1/_2.png and their props.json entries (with footprints and lights).
"""
from __future__ import annotations

import json
import math
import os
import random
import sys

import bpy  # first: the bpy module makes bmesh importable
import bmesh  # noqa: F401

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import armoury as A  # noqa: E402
import scenery as SC  # noqa: E402

T, R, S = A.T, A.R, A.S
Prop, tube, blob, glow = SC.Prop, SC.tube, SC.blob, SC.glow
box, lathe, slab, sphere = A.box, A.lathe, A.slab, A.sphere


def marble(): return A.mat("marble", "#D8D2C6", rough=0.35, grime=0.35, scale=4, bump="hammered", bump_scale=8, bump_strength=0.08)
def grey_stone(): return A.mat("greystone", "#7A766E", rough=0.8, grime=0.55, scale=5, bump="hammered", bump_scale=18, bump_strength=0.45)
def dark_wood(): return A.mat("darkwood", "#3A2618", rough=0.6, grime=0.4, scale=8, bump="grain", bump_strength=0.2)
def oak(): return A.mat("oakwood", "#6A4A2E", rough=0.7, grime=0.45, scale=8, bump="grain", bump_strength=0.25)
def velvet(c="#7A1418"): return A.mat("velvet" + c, c, rough=0.95, grime=0.25, scale=12)
def gold(): return A.mat("gilt", "#C8A040", metal=0.95, rough=0.3, grime=0.35)
def steel(): return A.mat("steel_int", "#A8ACB0", metal=0.9, rough=0.32, grime=0.4)
def iron(): return A.mat("iron_dark", "#2E2E32", metal=0.85, rough=0.5, grime=0.6)
def straw(): return A.mat("faggot", "#8A6A3A", rough=1.0, grime=0.5, scale=10, bump="grain", bump_strength=0.4)
def wax(): return A.mat("wax", "#E8DCC0", rough=0.6)


def candle(p, x, y, z, h=0.25):
    p.add(tube([(x, y, z), (x, y, z + h)], [0.03, 0.03], 8), wax())
    p.add(blob(0.02, 1, 0.2, 2, 3, squash=(1, 1, 2)), glow("#FFB050", 6.0), matrix=T(x, y, z + h + 0.04))


def tapestry_wall(seed=1):
    """A stretch of palace wall hung with a woven tapestry between pilasters."""
    rng = random.Random(seed)
    p = Prop("tapestry_wall")
    st = marble()
    p.add(box(3.2, 0.4, 3.4), st, solid=True, matrix=T(0, 0.2, 1.7))
    for x in (-1.5, 1.5): p.add(box(0.3, 0.5, 3.5, bevel=0.02), st, solid=True, matrix=T(x, 0.12, 1.75))
    p.add(box(3.3, 0.55, 0.2, bevel=0.02), st, matrix=T(0, 0.12, 3.45))
    p.add(tube([(-1.25, -0.05, 3.1), (1.25, -0.05, 3.1)], [0.03, 0.03], 6), gold())
    field = rng.choice(["#2A3A6A", "#5A1A1A", "#2A4A2A"])
    p.add(box(2.4, 0.03, 2.5), A.mat("tap" + field, field, rough=0.95, grime=0.3, scale=14), matrix=T(0, -0.05, 1.8))
    p.add(box(2.2, 0.035, 2.3), A.mat("tapinner", "#B89A5A", rough=0.95, grime=0.3, scale=14), matrix=T(0, -0.055, 1.8))
    p.add(box(1.9, 0.04, 2.0), A.mat("tap" + field, field, rough=0.95, grime=0.3, scale=14), matrix=T(0, -0.06, 1.8))
    # A woven figure: a rider and a tree in simple shapes.
    p.add(box(0.5, 0.045, 0.35), A.mat("tapwhite", "#D8CCB0", rough=0.95), matrix=T(-0.3, -0.065, 1.3))
    p.add(box(0.12, 0.045, 0.6), A.mat("tapwhite", "#D8CCB0", rough=0.95), matrix=T(-0.25, -0.065, 1.7))
    p.add(box(0.1, 0.045, 0.9), A.mat("taptrunk", "#5A3A22", rough=0.95), matrix=T(0.55, -0.065, 1.4))
    p.add(sphere(0.35, 10), A.mat("tapleaf", "#3A6A3A", rough=0.95), matrix=T(0.55, -0.06, 2.1) @ S(1, 0.08, 1))
    for k in range(12): p.add(box(0.03, 0.04, 0.12), gold(), matrix=T(-1.1 + k * 0.2, -0.06, 0.5))
    return p.build()


def banquet_table(seed=2):
    p = Prop("banquet_table")
    w = oak()
    p.add(box(3.0, 1.0, 0.08, bevel=0.01), w, solid=True, matrix=T(0, 0, 0.8))
    p.add(box(3.05, 1.06, 0.02), A.linen(), matrix=T(0, 0, 0.85))
    for x in (-1.3, 1.3):
        for y in (-0.4, 0.4): p.add(box(0.1, 0.1, 0.8), w, solid=True, matrix=T(x, y, 0.4))
    for x in (-1.0, -0.33, 0.33, 1.0):
        for y in (-0.75, 0.75):
            p.add(box(0.45, 0.4, 0.06), w, matrix=T(x, y, 0.45))
            for dx in (-0.18, 0.18): p.add(box(0.05, 0.05, 0.45), w, matrix=T(x + dx, y, 0.22))
            p.add(box(0.45, 0.06, 0.55), w, matrix=T(x, y + (0.2 if y > 0 else -0.2), 0.75))
    for x in (-1.0, 0.0, 1.0):
        p.add(lathe([(0, 0.0), (0.02, 0.12), (0.03, 0.14)], 16), gold(), matrix=T(x, -0.25, 0.86) @ R(-90, "Y"))
        p.add(lathe([(0, 0.05), (0.12, 0.04), (0.16, 0.035)], 10), gold(), matrix=T(x + 0.2, 0.2, 0.86) @ R(-90, "Y"))
    p.add(sphere(0.12, 10), A.mat("bread", "#B07A3A", rough=0.8), matrix=T(0.5, 0.05, 0.92) @ S(1.4, 1, 0.7))
    for x in (-0.6, 0.6): candle(p, x, 0.0, 0.86, 0.3)
    return p.build()


def throne_dais(seed=3):
    p = Prop("throne_dais")
    st = marble()
    for k, (w, d) in enumerate(((3.0, 2.4), (2.4, 1.9), (1.8, 1.4))):
        p.add(box(w, d, 0.2, bevel=0.01), st, solid=True, matrix=T(0, 0.2 + k * 0.1, 0.1 + k * 0.2))
    p.add(box(1.0, 1.4, 0.03), velvet(), matrix=T(0, -0.6, 0.21) @ R(-15, "X"))
    g = gold(); v = velvet("#2A2A6A")
    p.add(box(1.1, 0.8, 0.5, bevel=0.03), dark_wood(), solid=True, matrix=T(0, 0.5, 0.85))
    p.add(box(0.95, 0.65, 0.1), v, matrix=T(0, 0.45, 1.14))
    p.add(box(1.1, 0.15, 1.9, bevel=0.03), dark_wood(), solid=True, matrix=T(0, 0.85, 1.55))
    p.add(box(0.85, 0.05, 1.45), v, matrix=T(0, 0.76, 1.6))
    p.add(slab([(-0.55, 2.5), (0.55, 2.5), (0.0, 2.95)], 0.16), g, matrix=T(0, 0.85, 0))
    for x in (-0.55, 0.55):
        p.add(box(0.12, 0.7, 0.35), dark_wood(), matrix=T(x, 0.45, 1.25))
        p.add(sphere(0.08, 10), g, matrix=T(x, 0.12, 1.45))
        p.add(sphere(0.08, 10), g, matrix=T(x, 0.85, 2.55))
    for x in (-1.3, 1.3):
        p.add(tube([(x, 0.0, 0.3), (x, 0.0, 2.4)], [0.04, 0.04], 6), g)
        p.add(box(0.6, 0.02, 1.0), velvet(), matrix=T(x, 0.0, 1.85))
    return p.build()


def suit_of_armour(seed=4):
    p = Prop("suit_of_armour")
    s = steel()
    p.add(box(0.6, 0.5, 0.15, bevel=0.02), grey_stone(), solid=True, matrix=T(0, 0, 0.07))
    for x in (-0.12, 0.12):
        p.add(tube([(x, 0, 0.15), (x, 0, 0.95)], [0.07, 0.08, 0.075], 10), s, solid=True)
        p.add(box(0.14, 0.22, 0.08, bevel=0.02), s, matrix=T(x, -0.04, 0.18))
    p.add(lathe([(0.0, 0.17), (0.35, 0.22), (0.55, 0.2), (0.65, 0.14)], 16), s, solid=True, matrix=T(0, 0, 0.95) @ R(-90, "Y") @ S(1, 1, 0.75))
    p.add(box(0.3, 0.18, 0.2), A.mat("tabard", "#E8E0D0", rough=0.9), matrix=T(0, -0.1, 0.92))
    for x in (-0.3, 0.3):
        p.add(sphere(0.11, 10), s, matrix=T(x, 0, 1.55))
        p.add(tube([(x, 0, 1.5), (x * 1.1, -0.05, 1.05)], [0.06, 0.05], 8), s)
    p.add(lathe([(0.0, 0.14), (0.2, 0.145), (0.3, 0.12), (0.34, 0.0)], 16), s, matrix=T(0, 0, 1.62) @ R(-90, "Y"))
    p.add(box(0.2, 0.02, 0.025), A.dark(), matrix=T(0, -0.14, 1.75))
    p.add(tube([(0.38, -0.08, 0.15), (0.38, -0.08, 2.1)], [0.022, 0.02], 6), dark_wood())
    p.add(slab([(-0.06, 2.1), (0.06, 2.1), (0, 2.45)], 0.02), s, matrix=T(0.38, -0.08, 0))
    return p.build()


def bookcase(seed=5):
    rng = random.Random(seed)
    p = Prop("bookcase")
    w = dark_wood()
    p.add(box(2.0, 0.45, 2.6), w, solid=True, matrix=T(0, 0.1, 1.3))
    p.add(box(1.85, 0.06, 2.45), A.dark(), matrix=T(0, -0.11, 1.3))
    cols = ["#6A1A14", "#2A3A5A", "#3A4A2A", "#6A4A22", "#4A2A3A", "#8A7A5A"]
    for shelf in (0.25, 0.85, 1.45, 2.05):
        p.add(box(1.9, 0.4, 0.04), w, matrix=T(0, -0.05, shelf))
        x = -0.88
        while x < 0.85:
            bw = rng.uniform(0.05, 0.1); bh = rng.uniform(0.3, 0.46)
            c = rng.choice(cols)
            p.add(box(bw, 0.28, bh), A.mat("book" + c, c, rough=0.8, grime=0.35), matrix=T(x + bw / 2, -0.1, shelf + 0.02 + bh / 2) @ R(rng.uniform(-4, 4) if rng.random() < 0.8 else 14, "Y"))
            x += bw + 0.01
    p.add(box(2.1, 0.5, 0.12, bevel=0.02), w, matrix=T(0, 0.08, 2.65))
    return p.build()


def carpet(seed=6):
    p = Prop("carpet")
    p.add(box(1.6, 4.0, 0.015), velvet("#6A1418"), matrix=T(0, 0, 0.008))
    p.add(box(1.3, 3.7, 0.017), A.mat("carpetbord", "#B8903A", rough=0.95), matrix=T(0, 0, 0.009))
    p.add(box(1.15, 3.55, 0.019), velvet("#6A1418"), matrix=T(0, 0, 0.01))
    for k in range(5): p.add(box(0.3, 0.3, 0.021), A.mat("carpetbord", "#B8903A", rough=0.95), matrix=T(0, -1.4 + k * 0.7, 0.011) @ R(45, "Z"))
    return p.build()


def saint_statue(seed=7):
    """A robed saint on a plinth, hands joined, with a gilt halo."""
    p = Prop("saint_statue")
    st = marble()
    p.add(box(0.8, 0.8, 0.9, bevel=0.03), grey_stone(), solid=True, matrix=T(0, 0, 0.45))
    p.add(box(0.9, 0.9, 0.1, bevel=0.02), grey_stone(), matrix=T(0, 0, 0.92))
    p.add(lathe([(0.0, 0.32), (0.6, 0.28), (1.2, 0.22), (1.5, 0.2), (1.65, 0.12)], 16), st, solid=True, matrix=T(0, 0, 0.97) @ R(-90, "Y"))
    p.add(sphere(0.14, 12), st, matrix=T(0, -0.02, 2.75))
    p.add(lathe([(0.0, 0.17), (0.08, 0.2), (0.25, 0.16)], 14), st, matrix=T(0, 0.04, 2.6) @ R(-90, "Y"))  # hood
    for x in (-0.07, 0.07): p.add(tube([(x * 3, -0.12, 2.2), (x, -0.25, 2.2), (0, -0.3, 2.3)], [0.05, 0.045, 0.04], 8), st)
    p.add(lathe([(-0.01, 0.26), (0.01, 0.26)], 24, cap=True), gold(), matrix=T(0, 0.12, 2.82) @ R(90, "X"))
    for x in (-0.3, 0.3): candle(p, x, -0.38, 0.97, 0.22)
    return p.build()


def altar(seed=8):
    p = Prop("altar")
    st = marble()
    p.add(box(2.2, 0.9, 0.12, bevel=0.01), grey_stone(), solid=True, matrix=T(0, 0, 0.06))
    p.add(box(1.9, 0.8, 1.0, bevel=0.02), st, solid=True, matrix=T(0, 0.05, 0.62))
    p.add(box(2.0, 0.9, 0.06), st, matrix=T(0, 0.05, 1.15))
    p.add(box(2.02, 0.05, 0.55), A.linen(), matrix=T(0, -0.42, 0.9))
    p.add(box(0.5, 0.055, 0.5), velvet("#6A1418"), matrix=T(0, -0.43, 0.9))
    p.add(box(0.14, 0.06, 0.4), gold(), matrix=T(0, -0.46, 0.92)); p.add(box(0.34, 0.06, 0.1), gold(), matrix=T(0, -0.46, 0.98))
    p.add(box(0.06, 0.06, 0.7), gold(), matrix=T(0, 0.25, 1.53)); p.add(box(0.35, 0.06, 0.06), gold(), matrix=T(0, 0.25, 1.7))
    p.add(lathe([(0, 0.06), (0.05, 0.03), (0.15, 0.03), (0.2, 0.08), (0.26, 0.07)], 14), gold(), matrix=T(-0.45, 0.0, 1.18) @ R(-90, "Y"))
    for x in (-0.85, 0.85):
        p.add(lathe([(0, 0.08), (0.05, 0.03), (0.6, 0.025), (0.65, 0.06)], 10), gold(), matrix=T(x, 0.1, 1.18) @ R(-90, "Y"))
        candle(p, x, 0.1, 1.83, 0.3)
    return p.build()


def pews(seed=9):
    p = Prop("pews")
    w = dark_wood()
    for k in range(3):
        y = -0.9 + k * 0.9
        p.add(box(3.0, 0.35, 0.06), w, solid=True, matrix=T(0, y, 0.45))
        p.add(box(3.0, 0.05, 0.5), w, solid=True, matrix=T(0, y + 0.2, 0.75))
        for x in (-1.5, 1.5): p.add(slab([(-0.22, 0), (0.22, 0), (0.22, 1.0), (0.0, 1.1), (-0.22, 1.0)], 0.06), w, solid=True, matrix=T(x, y, 0) @ R(90, "Z"))
        p.add(box(3.0, 0.12, 0.05), w, matrix=T(0, y - 0.3, 0.12))
    return p.build()


def stained_glass_wall(seed=10):
    """A stretch of nave wall with a tall pointed window of coloured glass, lit from outside."""
    rng = random.Random(seed)
    p = Prop("stained_glass_wall")
    st = grey_stone()
    p.add(box(3.0, 0.5, 4.4), st, solid=True, matrix=T(0, 0.25, 2.2))
    for x in (-1.45, 1.45): p.add(box(0.35, 0.65, 4.5, bevel=0.02), st, solid=True, matrix=T(x, 0.15, 2.25))
    cols = ["#3060C0", "#C02828", "#E0B030", "#30A050", "#7040A0"]
    z0, z1, hw = 0.9, 3.4, 0.6
    pts = [(-hw, z0), (hw, z0), (hw, z1)] + [(hw * math.cos(a), z1 + 0.9 * math.sin(a)) for a in [i / 10 * math.pi for i in range(1, 10)]] + [(-hw, z1)]
    p.add(slab(pts, 0.04), A.dark(), matrix=T(0, -0.02, 0))
    for i in range(4):
        for j in range(9):
            x = -hw + 0.15 + i * 0.3; z = z0 + 0.15 + j * 0.3
            if z > z1 + 0.6 * math.cos((x / hw) * math.pi / 2): continue
            c = rng.choice(cols)
            p.add(box(0.24, 0.03, 0.24), glow(c, 2.5), matrix=T(x, -0.05, z))
    p.add(box(0.04, 0.05, z1 - z0 + 0.6), iron(), matrix=T(0, -0.07, (z0 + z1 + 0.6) / 2))
    for z in (1.6, 2.4, 3.2): p.add(box(2 * hw, 0.05, 0.04), iron(), matrix=T(0, -0.07, z))
    p.add(box(1.5, 0.6, 0.12), st, matrix=T(0, -0.1, z0 - 0.06))
    return p.build()


def tomb_effigy(seed=11):
    """A knight's tomb: a carved figure lying on the lid, hands joined, sword along his body, a lion at his feet."""
    p = Prop("tomb_effigy")
    st = marble()
    p.add(box(2.4, 1.0, 0.85, bevel=0.03), grey_stone(), solid=True, matrix=T(0, 0, 0.43))
    for x in (-0.8, 0.0, 0.8): p.add(box(0.55, 0.04, 0.5), A.dark(), matrix=T(x, -0.5, 0.42))
    p.add(box(2.5, 1.1, 0.08, bevel=0.01), st, matrix=T(0, 0, 0.89))
    p.add(box(0.5, 0.45, 0.12), st, matrix=T(-0.95, 0, 0.99))
    p.add(lathe([(0.0, 0.0), (0.1, 0.25), (1.4, 0.2), (1.6, 0.12)], 12), st, matrix=T(-0.85, 0, 1.05) @ S(1, 1, 0.55))
    p.add(sphere(0.13, 12), st, matrix=T(-0.95, 0, 1.14))
    p.add(tube([(-0.5, 0, 1.18), (-0.4, 0, 1.3)], [0.05, 0.03], 6), st)
    p.add(box(1.0, 0.04, 0.04), st, matrix=T(0.05, 0.12, 1.17)); p.add(box(0.04, 0.22, 0.04), st, matrix=T(-0.4, 0.12, 1.17))
    p.add(blob(0.18, 2, 0.3, 2, 4, squash=(1.3, 1, 0.8)), st, matrix=T(0.9, 0, 1.1))
    return p.build()


def pyre_stake(seed=12):
    """The stake on the Place de Grève: a tall post, chains, and bundled faggots heaped round its foot. Unlit."""
    rng = random.Random(seed)
    p = Prop("pyre_stake")
    p.add(box(0.2, 0.2, 3.6), dark_wood(), solid=True, matrix=T(0, 0, 1.8))
    p.add(box(1.0, 0.14, 0.14), dark_wood(), matrix=T(0, 0, 3.0))
    for z in (1.3, 2.0): p.add(lathe([(-0.04, 0.17), (0.04, 0.17)], 16), iron(), matrix=T(0, 0, z) @ R(90, "Y"))
    for k in range(80):  # bundled faggots heaped into a cone round the post
        a = rng.uniform(0, math.tau); r = rng.uniform(0.3, 1.25)
        L = rng.uniform(0.7, 1.1)
        z = 0.3 + (1.25 - r) * 0.75 + rng.uniform(0, 0.15)
        x, y = math.cos(a) * r, math.sin(a) * r
        p.add(tube([(x - math.sin(a) * L / 2, y + math.cos(a) * L / 2, z), (x + math.sin(a) * L / 2, y - math.cos(a) * L / 2, z + rng.uniform(-0.1, 0.1))], [0.09, 0.09], 6), straw(), solid=r < 0.8)
    p.add(box(1.4, 1.4, 0.4), dark_wood(), solid=True, matrix=T(0, 0, 0.2))
    return p.build()


def scaffold(seed=13):
    p = Prop("scaffold")
    w = oak()
    p.add(box(3.2, 2.2, 0.12), w, solid=True, matrix=T(0, 0, 1.3))
    for x in (-1.5, 0.0, 1.5):
        for y in (-1.0, 1.0): p.add(box(0.16, 0.16, 1.3), w, solid=True, matrix=T(x, y, 0.65))
    for k in range(6):  # steps up the west side
        p.add(box(0.9, 0.3, 0.06), w, matrix=T(-1.9 - (5 - k) * 0.28, 0.0, 0.1 + k * 0.21))
    p.add(box(0.08, 0.08, 1.6), w, matrix=T(-3.3, 0.45, 0.8) @ R(-40, "Y"))
    p.add(box(3.2, 0.06, 0.6), velvet("#1A1A1A"), matrix=T(0, -1.11, 0.95))
    p.add(box(0.5, 0.5, 0.45, bevel=0.03), dark_wood(), matrix=T(0.3, 0.2, 1.58))
    p.add(box(1.2, 0.08, 0.08), w, matrix=T(0, 0.9, 1.4))
    return p.build()


def wall_shackles(seed=14):
    p = Prop("wall_shackles")
    st = grey_stone(); ir = iron()
    p.add(box(2.6, 0.45, 2.8), st, solid=True, matrix=T(0, 0.22, 1.4))
    for x in (-0.7, 0.7):
        for z in (1.7, 0.4):
            p.add(box(0.12, 0.06, 0.12), ir, matrix=T(x, -0.03, z))
            p.add(tube([(x, -0.06, z), (x + 0.02, -0.15, z - 0.35), (x, -0.08, z - 0.6)], [0.015, 0.015, 0.015], 4), ir)
            p.add(lathe([(-0.025, 0.07), (0.025, 0.07)], 12), ir, matrix=T(x, -0.08, z - 0.66) @ R(90, "Y"))
    p.add(box(0.6, 0.4, 0.3), A.mat("strawpile", "#8A7A4A", rough=1.0), matrix=T(-0.8, -0.35, 0.1))
    return p.build()


def reliquary(seed=15):
    p = Prop("reliquary")
    g = gold()
    p.add(box(1.2, 0.6, 0.6, bevel=0.03), g, solid=True, matrix=T(0, 0, 0.6))
    p.add(slab([(-0.6, 0.9), (0.6, 0.9), (0.6, 1.0), (0.0, 1.3), (-0.6, 1.0)], 0.6), g, matrix=T(0, 0, 0))
    for x in (-0.35, 0.0, 0.35):
        p.add(box(0.22, 0.04, 0.35), A.mat("enamel", "#1A3A8A", rough=0.3), matrix=T(x, -0.31, 0.6))
        p.add(sphere(0.035, 8), glow("#C02020", 1.2), matrix=T(x, -0.33, 0.85))
    p.add(box(1.3, 0.7, 0.3, bevel=0.02), marble(), solid=True, matrix=T(0, 0, 0.15))
    p.add(box(0.06, 0.06, 0.25), g, matrix=T(0, 0, 1.42)); p.add(box(0.18, 0.06, 0.06), g, matrix=T(0, 0, 1.47))
    return p.build()


def font(seed=16):
    p = Prop("font")
    st = marble()
    p.add(lathe([(0, 0.35), (0.12, 0.3), (0.2, 0.16), (0.7, 0.14), (0.8, 0.3), (1.0, 0.5), (1.05, 0.52)], 18), st, solid=True, matrix=R(-90, "Y"))
    p.add(lathe([(-0.01, 0.46), (0.01, 0.46)], 18, cap=True), A.mat("holywater", "#3A5A6A", rough=0.05), matrix=T(0, 0, 0.99) @ R(90, "Y"))
    return p.build()


CANDLES = {"colour": "#FFC070", "radius": 280, "intensity": 1.05, "flicker": 0.6, "height": 1.5, "flame": True, "embers": 0}
GLASS = {"colour": "#A0B8FF", "radius": 420, "intensity": 0.9, "flicker": 0.05, "height": 2.4, "flame": False, "embers": 0}

PROPS = {
    "tapestry_wall": (tapestry_wall, 3.6, None),
    "banquet_table": (banquet_table, 1.3, CANDLES),
    "throne_dais": (throne_dais, 3.0, None),
    "suit_of_armour": (suit_of_armour, 2.5, None),
    "bookcase": (bookcase, 2.8, None),
    "carpet": (carpet, 0.1, None),
    "saint_statue": (saint_statue, 3.0, CANDLES),
    "altar": (altar, 2.2, CANDLES),
    "pews": (pews, 1.2, None),
    "stained_glass_wall": (stained_glass_wall, 4.6, GLASS),
    "tomb_effigy": (tomb_effigy, 1.3, None),
    "pyre_stake": (pyre_stake, 3.7, None),
    "scaffold": (scaffold, 2.0, None),
    "wall_shackles": (wall_shackles, 2.8, None),
    "reliquary": (reliquary, 1.5, None),
    "font": (font, 1.1, None),
}
WALKABLE = {"carpet"}


def main(argv):
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--names", default=""); ap.add_argument("--samples", type=int, default=48)
    a = ap.parse_args(argv)
    for n in [x for x in a.names.split(",") if x] or list(PROPS):
        SC.SCENERY[n] = PROPS[n]
        e = SC.render(n, a.samples, os.path.join(SC.ROOT, "assets"), True)
        if n in WALKABLE:
            for v in e.values(): v.pop("footprint", None)
        pj = json.load(open(SC.PROPS_JSON))
        pj.update(e)
        with open(SC.PROPS_JSON, "w", newline="\n") as f:
            json.dump(dict(sorted(pj.items())), f, indent=1, ensure_ascii=False); f.write("\n")
        print("[interiors]", n, flush=True)


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:])
