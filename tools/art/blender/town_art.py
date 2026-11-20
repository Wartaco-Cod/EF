"""
Town in Shadow's buildings and street furniture, built from primitives and rendered with the scenery camera
(scenery.render): two kinds of timber-framed townhouse, a boarded-up house marked by the Inquisition, a notice board
nailed with edicts, and a barred prison wagon.

    blender -b --python tools/art/blender/town_art.py -- [--names townhouse,notice_board] [--samples 48]

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
box, lathe, slab = A.box, A.lathe, A.slab


def plaster(tone="#B8A88A"): return A.mat("plaster" + tone, tone, rough=0.9, grime=0.65, scale=6, bump="hammered", bump_scale=10, bump_strength=0.15)
def beam(): return A.mat("beam", "#2E2219", rough=0.85, grime=0.5, scale=8, bump="grain", bump_strength=0.25)
def tiles(tone="#5C3A2C"): return A.mat("rooftile" + tone, tone, rough=0.8, grime=0.6, scale=3, bump="hammered", bump_scale=26, bump_strength=0.6)
def stone(): return A.mat("townstone", "#6A645A", rough=0.85, grime=0.6, scale=5, bump="hammered", bump_scale=18, bump_strength=0.5)
def board(): return A.mat("board", "#4A3A2A", rough=0.9, grime=0.55, scale=8, bump="grain", bump_strength=0.3)
def iron(): return A.mat("iron_dark", "#2E2E32", metal=0.85, rough=0.5, grime=0.6)
def window_glow(): return glow("#FFB060", 2.2)


def gable_roof(p, w, d, z, rise, m, over=0.35, front=True):
    """A pitched roof. front=True: gable end faces the street (ridge runs front to back)."""
    if front:
        p.add(slab([(-w / 2 - over, z), (w / 2 + over, z), (0, z + rise)], d + 2 * over), m, solid=True)
        p.add(slab([(-w / 2 - over - 0.05, z - 0.05), (w / 2 + over + 0.05, z - 0.05), (0, z + rise + 0.07)], 0.12), beam(),
              matrix=T(0, -d / 2 - over + 0.06, 0))
    else:
        p.add(slab([(-d / 2 - over, z), (d / 2 + over, z), (0, z + rise)], w + 2 * over), m, solid=True, matrix=R(90, "Z"))


def frame(p, x0, x1, z0, z1, y, posts=4, braces=True):
    """Dark timber framing on a facade plane at depth y."""
    b = beam()
    for z in (z0, z1): p.add(box(x1 - x0 + 0.1, 0.1, 0.16), b, matrix=T((x0 + x1) / 2, y, z))
    for k in range(posts + 1):
        x = x0 + (x1 - x0) * k / posts
        p.add(box(0.15, 0.1, z1 - z0), b, matrix=T(x, y, (z0 + z1) / 2))
    if braces:
        for k in (0, posts - 1):
            xa = x0 + (x1 - x0) * k / posts; xb = x0 + (x1 - x0) * (k + 1) / posts
            L = math.hypot(xb - xa, z1 - z0); ang = math.degrees(math.atan2(z1 - z0, xb - xa))
            p.add(box(L, 0.08, 0.12), b, matrix=T((xa + xb) / 2, y, (z0 + z1) / 2) @ R(-ang if k == 0 else ang, "Y"))


def window(p, x, z, y, lit=True, shutters=True, w=0.6, h=0.75, boarded=False):
    p.add(box(w, 0.06, h), window_glow() if lit else A.dark(), matrix=T(x, y + 0.02, z))
    p.add(box(w + 0.14, 0.12, 0.1), beam(), matrix=T(x, y - 0.02, z - h / 2 - 0.04))
    if shutters:
        for s in (-1, 1):
            p.add(box(w / 2, 0.05, h), board(), matrix=T(x + s * (w * 0.75 + 0.02), y - 0.04, z) @ R(s * 12, "Z"))
    if boarded:
        for k, a in enumerate((18, -14, 4)):
            p.add(box(w + 0.3, 0.05, 0.12), board(), matrix=T(x, y - 0.06 - k * 0.01, z + (k - 1) * 0.2) @ R(a, "Y"))


def door(p, x, y, m=None, w=0.95, h=1.95):
    p.add(box(w + 0.2, 0.12, h + 0.15), beam(), matrix=T(x, y, h / 2))
    p.add(box(w, 0.08, h), m or board(), matrix=T(x, y - 0.04, h / 2))
    for z in (0.4, h - 0.4): p.add(box(w * 0.8, 0.09, 0.05), iron(), matrix=T(x, y - 0.06, z))
    return h


def chimney(p, x, y, z0, z1):
    p.add(box(0.5, 0.5, z1 - z0), stone(), solid=True, matrix=T(x, y, (z0 + z1) / 2))
    p.add(box(0.6, 0.6, 0.1), stone(), matrix=T(x, y, z1))


def townhouse(seed=1, tone="#B8A88A", lit=True):
    """Two storeys, the upper one jettied over the street, gable end to the front."""
    rng = random.Random(seed)
    p = Prop("townhouse")
    w, d = 4.6, 4.0
    p.add(box(w, d, 3.0), plaster(tone), solid=True, matrix=T(0, 0, 1.5))
    # Jetty: the upper floor overhangs the front by half a metre on joists.
    p.add(box(w + 0.2, d + 0.5, 2.6), plaster(tone), solid=True, matrix=T(0, -0.25, 4.3))
    for k in range(7): p.add(box(0.12, 0.6, 0.14), beam(), matrix=T(-w / 2 + 0.2 + k * (w - 0.4) / 6, -d / 2 - 0.2, 3.0))
    yf, yu = -d / 2 - 0.05, -d / 2 - 0.55
    frame(p, -w / 2, w / 2, 0.08, 3.0, yf, 4, braces=False)
    frame(p, -w / 2 - 0.1, w / 2 + 0.1, 3.05, 5.6, yu, 4, braces=True)
    door(p, -1.0, yf - 0.02)
    window(p, 1.0, 1.55, yf - 0.03, lit and rng.random() < 0.6)
    for x in (-1.2, 1.2): window(p, x, 4.3, yu - 0.03, lit and rng.random() < 0.5, w=0.55, h=0.8)
    gable_roof(p, w + 0.2, d + 0.5, 5.6, 2.6, tiles(), front=True)
    p.add(box(0.6, 0.06, 0.7), A.dark(), matrix=T(0, yu - 0.36, 6.4))  # loft hatch in the gable
    chimney(p, 1.4, 0.8, 5.6, 8.1)
    # Street life at its foot.
    p.add(box(0.5, 0.5, 0.55, bevel=0.03), board(), solid=True, matrix=T(1.9, yf - 0.45, 0.28))
    p.add(lathe([(0, 0.22), (0.5, 0.22)], 12), board(), matrix=T(1.4, yf - 0.4, 0.0) @ R(-90, "Y"))
    return p.build()


def townhouse_b(seed=2):
    """A stone ground floor with a shop opening, dark timber above, eaves to the street and a dormer."""
    rng = random.Random(seed)
    p = Prop("townhouse_b")
    w, d = 5.4, 3.8
    p.add(box(w, d, 2.8), stone(), solid=True, matrix=T(0, 0, 1.4))
    p.add(box(w, d, 2.4), plaster("#A89878"), solid=True, matrix=T(0, 0, 4.0))
    yf = -d / 2 - 0.05
    frame(p, -w / 2, w / 2, 2.85, 5.2, yf, 5, braces=True)
    # Shop: a wide opening with a let-down counter and an awning.
    p.add(box(2.0, 0.08, 1.2), A.dark(), matrix=T(1.1, yf, 1.5))
    p.add(box(2.1, 0.6, 0.08), board(), matrix=T(1.1, yf - 0.3, 0.92))
    p.add(box(2.3, 1.0, 0.05), A.mat("awning", "#6A3A2A", rough=0.9, grime=0.5), matrix=T(1.1, yf - 0.5, 2.35) @ R(-18, "X"))
    for x in (0.4, 1.0, 1.6): p.add(lathe([(0, 0.14), (0.25, 0.14)], 10), A.mat("pot", "#8A5A3A", rough=0.7), matrix=T(x, yf - 0.35, 0.96) @ R(-90, "Y"))
    door(p, -1.4, yf - 0.02, h=2.0)
    for x in (-1.6, 0.0, 1.6): window(p, x, 4.0, yf - 0.03, rng.random() < 0.5, w=0.55, h=0.75)
    gable_roof(p, w, d, 5.2, 2.2, tiles("#4A3A34"), front=False)
    # Dormer.
    p.add(box(1.0, 0.8, 0.9), plaster("#A89878"), solid=True, matrix=T(-0.6, -1.4, 5.8))
    p.add(slab([(-0.7, 6.25), (0.7, 6.25), (0, 6.85)], 1.1), tiles("#4A3A34"), matrix=T(-0.6, -1.35, 0))
    window(p, -0.6, 5.8, -1.85, rng.random() < 0.5, shutters=False, w=0.45, h=0.5)
    chimney(p, -2.1, 0.6, 5.2, 7.6)
    p.add(box(0.06, 0.8, 0.06), iron(), matrix=T(-2.55, yf - 0.4, 2.9))  # sign bracket
    p.add(box(0.5, 0.04, 0.4), board(), matrix=T(-2.55, yf - 0.75, 2.6))
    return p.build()


def boarded_house(seed=3):
    """Shut up by the Inquisition: boards across every opening, a red cross daubed on the door, roof tiles gone."""
    rng = random.Random(seed)
    p = Prop("boarded_house")
    w, d = 4.4, 3.8
    p.add(box(w, d, 2.9), plaster("#9A8E78"), solid=True, matrix=T(0, 0, 1.45))
    p.add(box(w, d, 2.3), plaster("#9A8E78"), solid=True, matrix=T(0, 0, 4.05))
    yf = -d / 2 - 0.05
    frame(p, -w / 2, w / 2, 0.08, 2.9, yf, 4, braces=False)
    frame(p, -w / 2, w / 2, 2.95, 5.2, yf, 4, braces=True)
    door(p, 0.0, yf - 0.02)
    for a in (22, -22): p.add(box(1.5, 0.05, 0.14), board(), matrix=T(0, yf - 0.12, 1.0) @ R(a, "Y"))
    red = A.mat("daub", "#8A1A14", rough=0.95, grime=0.4)
    p.add(box(0.12, 0.02, 0.9), red, matrix=T(0, yf - 0.16, 1.45))
    p.add(box(0.6, 0.02, 0.12), red, matrix=T(0, yf - 0.16, 1.6))
    for x in (-1.4, 1.4): window(p, x, 1.6, yf - 0.03, False, shutters=False, boarded=True)
    for x in (-1.2, 1.2): window(p, x, 4.0, yf - 0.03, False, shutters=False, boarded=True, w=0.55)
    gable_roof(p, w, d, 5.2, 2.4, tiles("#4E3A30"), front=True)
    # Missing tiles: dark holes and bare battens on the roof slope.
    for k in range(3):
        x = rng.uniform(-1.2, 1.2); y = rng.uniform(-1.0, 1.2)
        zz = 5.2 + 2.4 * (1 - abs(x) / (w / 2 + 0.35)) + 0.02
        p.add(box(0.7, 0.6, 0.06), A.dark(), matrix=T(x, y, zz - 0.2) @ R(math.copysign(44, -x), "Y"))
    p.add(box(0.5, 0.5, 0.4), A.mat("ash", "#3A3632", rough=1.0), matrix=T(1.6, yf - 0.5, 0.2))
    return p.build()


def notice_board(seed=4):
    """A post-and-board nailed with the Inquisition's edicts."""
    rng = random.Random(seed)
    p = Prop("notice_board")
    for x in (-0.7, 0.7): p.add(box(0.14, 0.14, 2.3), beam(), solid=True, matrix=T(x, 0, 1.15))
    p.add(box(1.5, 0.08, 1.0), board(), solid=True, matrix=T(0, 0, 1.55))
    p.add(slab([(-0.95, 2.1), (0.95, 2.1), (0, 2.45)], 0.5), tiles("#4A3A34"), matrix=T(0, 0.05, 0))
    for k in range(5):
        x = -0.5 + k * 0.25 + rng.uniform(-0.05, 0.05); z = 1.55 + rng.uniform(-0.25, 0.25)
        p.add(box(0.3, 0.02, 0.4), A.parchment(), matrix=T(x, -0.06, z) @ R(rng.uniform(-8, 8), "Y"))
        p.add(lathe([(0, 0.035), (0.01, 0.035)], 8), A.mat("seal", "#8A1A14", rough=0.5), matrix=T(x, -0.075, z - 0.12) @ R(90, "X"))
    return p.build()


def prison_wagon(seed=5):
    """A barred wagon for taking the accused to the Inquisition's house."""
    p = Prop("prison_wagon")
    w = beam(); ir = iron()
    p.add(box(2.6, 1.4, 0.15), w, solid=True, matrix=T(0, 0, 0.75))
    for x in (-0.85, 0.85):
        for y in (-0.75, 0.75):
            p.add(lathe([(-0.06, 0.45), (0.06, 0.45)], 16), w, solid=True, matrix=T(x, y, 0.45) @ R(90, "Z"))
            p.add(lathe([(-0.07, 0.08), (0.07, 0.08)], 8), ir, matrix=T(x, y * 1.02, 0.45) @ R(90, "Z"))
    for x in (-1.25, 1.25):
        for y in (-0.65, 0.65): p.add(box(0.1, 0.1, 1.4), w, solid=True, matrix=T(x, y, 1.5))
    p.add(box(2.7, 1.5, 0.12), w, solid=True, matrix=T(0, 0, 2.25))
    for k in range(11):
        x = -1.1 + k * 0.22
        for y in (-0.66, 0.66): p.add(tube([(x, y, 0.83), (x, y, 2.2)], [0.02, 0.02], 5), ir)
    for k in range(5):
        y = -0.5 + k * 0.25
        p.add(tube([(1.26, y, 0.83), (1.26, y, 2.2)], [0.02, 0.02], 5), ir)
    p.add(box(0.12, 0.12, 0.35), w, matrix=T(-1.25, 0, 2.45))
    p.add(tube([(-1.3, -0.1, 0.7), (-2.6, -0.3, 0.55)], [0.05, 0.04], 6), w)
    p.add(tube([(-1.3, 0.1, 0.7), (-2.6, 0.3, 0.55)], [0.05, 0.04], 6), w)
    p.add(box(0.5, 1.1, 0.06), A.mat("straw", "#B89A5A", rough=1.0), matrix=T(0.2, 0, 0.86))
    return p.build()


WINDOWS = {"colour": "#FFB060", "radius": 300, "intensity": 0.8, "flicker": 0.3, "height": 1.5, "flame": False, "embers": 0}

PROPS = {
    "townhouse": (townhouse, 8.2, WINDOWS),
    "townhouse_b": (townhouse_b, 7.6, WINDOWS),
    "boarded_house": (boarded_house, 7.6, None),
    "notice_board": (notice_board, 2.5, None),
    "prison_wagon": (prison_wagon, 2.6, None),
}


def main(argv):
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--names", default=""); ap.add_argument("--samples", type=int, default=48)
    a = ap.parse_args(argv)
    for n in [x for x in a.names.split(",") if x] or list(PROPS):
        SC.SCENERY[n] = PROPS[n]
        e = SC.render(n, a.samples, os.path.join(SC.ROOT, "assets"), True)
        pj = json.load(open(SC.PROPS_JSON))
        pj.update(e)
        with open(SC.PROPS_JSON, "w", newline="\n") as f:
            json.dump(dict(sorted(pj.items())), f, indent=1, ensure_ascii=False); f.write("\n")
        print("[town]", n, flush=True)


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:])
