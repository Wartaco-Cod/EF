"""
Act II's underground and Paris props, built from primitives and rendered with the scenery camera (scenery.render):
ossuary walls, coffins, stalagmites, cave fungus, rubble, mine timbers, wine casks, smugglers' caches, gibbet cages,
the rack, the stocks, cell bars, Inquisition banners, lecterns, puddles and stone pillars.

    blender -b --python tools/art/blender/underground_art.py -- [--names coffin,rubble] [--samples 48]

Writes assets/sprites/prop_<name>_1/_2.png and their props.json entries (with footprints and lights).
"""
from __future__ import annotations

import json
import math
import os
import random
import sys

import bpy  # first: the bpy module makes bmesh importable
import bmesh
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import armoury as A  # noqa: E402
import scenery as SC  # noqa: E402

T, R, S = A.T, A.R, A.S
Prop, tube, blob, glow, rock = SC.Prop, SC.tube, SC.blob, SC.glow, SC.rock
box, lathe, slab, sphere = A.box, A.lathe, A.slab, A.sphere


def bone(): return A.mat("bone_ug", "#D8CCB0", rough=0.75, grime=0.55, scale=14)
def old_wood(): return A.mat("oldwood", "#3E2E22", rough=0.85, grime=0.6, scale=8, bump="grain", bump_strength=0.25)
def damp_stone(): return A.mat("dampstone", "#5A5A58", rough=0.8, grime=0.6, scale=5, bump="hammered", bump_scale=20, bump_strength=0.5)
def cave_rock(): return A.mat("caverock", "#5E544A", rough=0.9, grime=0.55, scale=4, bump="hammered", bump_scale=16, bump_strength=0.6)
def black_cloth(): return A.mat("blackcloth", "#1A1818", rough=0.9, grime=0.3, scale=12)
def water_dark(): return A.mat("puddle", "#14181C", metal=0.0, rough=0.04, grime=0.05, scale=3)
def iron_dark(): return A.mat("iron_dark", "#2E2E32", metal=0.85, rough=0.5, grime=0.6)


def skull(p, at, m, s=1.0, yaw=0.0):
    x, y, z = at
    p.add(sphere(0.085 * s, 12), m, matrix=T(x, y, z + 0.07 * s) @ S(1, 1.1, 0.95))
    p.add(box(0.11 * s, 0.09 * s, 0.05 * s, bevel=0.01 * s), m, matrix=T(x, y - 0.035 * s, z + 0.005 * s) @ R(yaw, "Z"))
    for dx in (-0.032, 0.032):
        p.add(sphere(0.022 * s, 8), A.dark(), matrix=T(x + dx * s, y - 0.075 * s, z + 0.075 * s))


def ossuary_wall(seed=1):
    """A stretch of catacomb wall with niches, its shelves stacked with long bones and rows of skulls."""
    rng = random.Random(seed)
    p = Prop("ossuary_wall")
    st = damp_stone()
    p.add(box(3.0, 0.5, 2.4, bevel=0.02), st, solid=True, matrix=T(0, 0.2, 1.2))
    for i, x in enumerate((-0.95, 0.0, 0.95)):
        # Niche: a dark recess with an arched head.
        p.add(box(0.72, 0.06, 1.5), A.dark(), matrix=T(x, -0.06, 1.05))
        p.add(lathe([(0, 0.36), (0.06, 0.36)], 16, cap=True), A.dark(), matrix=T(x, -0.06, 1.8) @ R(90, "Z") @ S(1, 1, 1))
        for shelf in (0.45, 1.05):
            p.add(box(0.74, 0.3, 0.05), st, solid=True, matrix=T(x, -0.05, shelf))
            for k in range(6):
                L = 0.62
                p.add(tube([(x - L / 2, -0.1 + k * 0.035, shelf + 0.05 + (k % 2) * 0.03), (x + L / 2, -0.1 + k * 0.035, shelf + 0.05 + (k % 2) * 0.03)], [0.022, 0.022], 6), bone())
            for k in range(4):
                skull(p, (x - 0.24 + k * 0.16, -0.14, shelf + 0.12), bone(), 0.85, rng.uniform(-15, 15))
    for x in (-1.45, -0.48, 0.48, 1.45):
        p.add(box(0.14, 0.56, 2.45, bevel=0.01), st, solid=True, matrix=T(x, 0.18, 1.22))
    return p.build()


def coffin(seed=2):
    p = Prop("coffin")
    w = old_wood()
    outline = [(-0.95, 0.0), (-0.95, 0.0)]
    pts = [(-0.95, -0.2), (0.55, -0.32), (0.95, -0.2), (0.95, 0.2), (0.55, 0.32), (-0.95, 0.2)]
    bm = bmesh.new(); vs = [bm.verts.new((x, y, 0)) for x, y in pts]; f = bm.faces.new(vs)
    r = bmesh.ops.extrude_face_region(bm, geom=[f])
    for v in [e for e in r["geom"] if isinstance(e, bmesh.types.BMVert)]: v.co.z += 0.42
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    p.add(bm, w, solid=True)
    bm = bmesh.new(); vs = [bm.verts.new((x, y, 0)) for x, y in pts]; f = bm.faces.new(vs)
    r = bmesh.ops.extrude_face_region(bm, geom=[f])
    for v in [e for e in r["geom"] if isinstance(e, bmesh.types.BMVert)]: v.co.z += 0.06
    p.add(bm, w, solid=True, matrix=T(0.12, 0.12, 0.44) @ R(-12, "Z") @ R(4, "Y"))
    p.add(box(0.06, 0.4, 0.02), A.mat("lidcross", "#7A6A52", rough=0.7), matrix=T(0.4, 0.12, 0.51) @ R(-12, "Z"))
    p.add(box(0.8, 0.06, 0.02), A.mat("lidcross", "#7A6A52", rough=0.7), matrix=T(0.2, 0.08, 0.51) @ R(-12, "Z"))
    skull(p, (-0.62, -0.05, 0.42), bone(), 0.9)
    return p.build()


def stalagmites(seed=3):
    rng = random.Random(seed)
    p = Prop("stalagmites")
    for k in range(6):
        a = rng.uniform(0, math.tau); d = rng.uniform(0, 0.55)
        h = rng.uniform(0.6, 1.9) * (1.2 if k == 0 else 1)
        r0 = h * 0.22
        prof = [(0, r0), (h * 0.3, r0 * 0.75), (h * 0.7, r0 * 0.4), (h, 0.0)]
        bm = lathe(prof, 10)
        p.add(bm, cave_rock(), solid=True, matrix=T(math.cos(a) * d, math.sin(a) * d, 0) @ R(-90, "Y") @ R(rng.uniform(-6, 6), "X"))
    p.add(blob(0.55, 2, 0.35, 2, seed, flat_bottom=0, squash=(1.3, 1.1, 0.35)), cave_rock(), solid=True)
    return p.build()


def fungus(seed=4):
    rng = random.Random(seed)
    p = Prop("fungus")
    for k in range(9):
        a = rng.uniform(0, math.tau); d = rng.uniform(0, 0.45)
        h = rng.uniform(0.1, 0.35)
        x, y = math.cos(a) * d, math.sin(a) * d
        p.add(tube([(x, y, 0), (x, y, h)], [0.025, 0.02], 6), A.mat("stalk", "#C8C0A8", rough=0.8))
        p.add(lathe([(0, 0.0), (0.0, 0.07 + h * 0.2), (0.04, 0.06 + h * 0.18), (0.06, 0.0)], 12), glow("#9AE0A0", 1.6), matrix=T(x, y, h) @ R(-90, "Y"))
    p.add(blob(0.3, 2, 0.3, 3, seed, flat_bottom=0, squash=(1.4, 1.2, 0.25)), cave_rock())
    return p.build()


def rubble(seed=5):
    rng = random.Random(seed)
    p = Prop("rubble")
    st = damp_stone()
    for k in range(10):
        s = rng.uniform(0.18, 0.4)
        p.add(box(s * 1.4, s, s * 0.8, bevel=0.03), st, solid=True, matrix=T(rng.uniform(-0.7, 0.7), rng.uniform(-0.4, 0.4), s * 0.35 + (k > 6) * 0.25) @ R(rng.uniform(0, 90), "Z") @ R(rng.uniform(-20, 20), "X"))
    p.add(blob(0.6, 2, 0.4, 3, seed, flat_bottom=0, squash=(1.6, 1.0, 0.3)), A.mat("dirt_ug", "#4A4036", rough=0.95))
    # A broken column drum among the stones.
    p.add(lathe([(0, 0.24), (0.55, 0.24)], 14), st, solid=True, matrix=T(0.3, 0.1, 0.24) @ R(80, "Z"))
    return p.build()


def mine_timbers(seed=6):
    p = Prop("mine_timbers")
    w = old_wood()
    for x in (-1.0, 1.0):
        p.add(box(0.2, 0.2, 2.5), w, solid=True, matrix=T(x, 0, 1.25) @ R(x * 2, "Y"))
    p.add(box(2.4, 0.24, 0.24), w, solid=True, matrix=T(0, 0, 2.52))
    p.add(tube([(-0.9, 0, 2.0), (-0.6, 0, 2.4)], [0.06, 0.06], 6), w)
    p.add(tube([(0.9, 0, 2.0), (0.6, 0, 2.4)], [0.06, 0.06], 6), w)
    # A lantern hanging from the beam.
    p.add(tube([(0.3, -0.05, 2.4), (0.3, -0.05, 2.1)], [0.006, 0.006], 4), A.iron())
    p.add(box(0.12, 0.12, 0.16, bevel=0.01), A.iron(), matrix=T(0.3, -0.05, 2.0))
    p.add(blob(0.04, 1, 0.1, 2, 2), glow("#FFB050", 6.0), matrix=T(0.3, -0.05, 2.0))
    return p.build()


def wine_casks(seed=7):
    p = Prop("wine_casks")
    w = A.mat("caskwood", "#5A3A22", rough=0.7, grime=0.5, scale=8, bump="grain", bump_strength=0.2)
    p.add(box(1.9, 0.9, 0.12), old_wood(), solid=True, matrix=T(0, 0, 0.06))
    for row, (z, xs) in enumerate(((0.42, (-0.6, 0.0, 0.6)), (0.95, (-0.3, 0.3)))):
        for x in xs:
            cask = lathe([(-0.42, 0.26), (-0.2, 0.32), (0.0, 0.34), (0.2, 0.32), (0.42, 0.26)], 18)
            p.add(cask, w, solid=True, matrix=T(x, 0, z) @ R(90, "Z"))
            for b in (-0.3, 0.3):
                p.add(lathe([(b - 0.02, 0.33), (b + 0.02, 0.33)], 18, cap=False), A.iron(), matrix=T(x, 0, z) @ R(90, "Z"))
            p.add(lathe([(-0.43, 0.24), (-0.42, 0.24)], 18), A.mat("caskend", "#6A4A2C", rough=0.7), matrix=T(x, 0, z) @ R(90, "Z"))
    return p.build()


def smuggler_cache(seed=8):
    rng = random.Random(seed)
    p = Prop("smuggler_cache")
    for k in range(5):
        s = rng.uniform(0.45, 0.6)
        p.add(box(s, s, s * 0.8, bevel=0.02), A.wood(), solid=True, matrix=T(rng.uniform(-0.6, 0.6), rng.uniform(-0.3, 0.3), s * 0.4 + (k == 4) * 0.5) @ R(rng.uniform(-20, 20), "Z"))
    for k in range(3):
        p.add(blob(0.25, 2, 0.15, 2, k, flat_bottom=0, squash=(1.0, 0.8, 1.2)), SC.canvas(), matrix=T(0.8 + k * 0.1, -0.4 + k * 0.3, 0.0))
    p.add(box(0.14, 0.14, 0.2, bevel=0.01), A.iron(), matrix=T(-0.1, -0.15, 1.05))
    p.add(blob(0.05, 1, 0.1, 2, 4), glow("#FFB050", 6.0), matrix=T(-0.1, -0.15, 1.05))
    return p.build()


def gibbet_cage(seed=9):
    p = Prop("gibbet_cage")
    w = old_wood(); ir = iron_dark()
    p.add(box(0.22, 0.22, 3.0), w, solid=True, matrix=T(-0.7, 0, 1.5))
    p.add(box(1.2, 0.18, 0.18), w, matrix=T(-0.2, 0, 2.9))
    p.add(tube([(-0.85, 0, 2.4), (-0.4, 0, 2.85)], [0.05, 0.05], 6), w)
    p.add(tube([(0.25, 0, 2.85), (0.25, 0, 2.55)], [0.012, 0.012], 4), ir)
    for k in range(10):
        a = k / 10 * math.tau
        p.add(tube([(0.25 + math.cos(a) * 0.3, math.sin(a) * 0.3, 2.5), (0.25 + math.cos(a) * 0.3, math.sin(a) * 0.3, 1.2)], [0.012, 0.012], 4), ir)
    for z in (1.2, 1.85, 2.5):
        p.add(SC.tube([(0.25 + 0.3 * math.cos(a), 0.3 * math.sin(a), z) for a in [i / 16 * math.tau for i in range(17)]], [0.014] * 17, 4), ir)
    skull(p, (0.25, 0.0, 1.22), bone(), 1.0)
    return p.build()


def torture_rack(seed=10):
    p = Prop("torture_rack")
    w = old_wood()
    for x in (-0.95, 0.95):
        for y in (-0.35, 0.35):
            p.add(box(0.12, 0.12, 0.85), w, solid=True, matrix=T(x, y, 0.42))
    for y in (-0.35, 0.35):
        p.add(box(2.1, 0.1, 0.12), w, solid=True, matrix=T(0, y, 0.8))
    for x in (-0.95, 0.95):
        p.add(lathe([(-0.4, 0.09), (0.4, 0.09)], 12), w, matrix=T(x, 0, 0.92) @ R(90, "Z"))
        for k in range(4):
            p.add(box(0.04, 0.04, 0.32), w, matrix=T(x + 0.0, 0.4, 0.92) @ R(k * 45, "Y"))
    p.add(box(1.7, 0.6, 0.06), w, matrix=T(0, 0, 0.84))
    p.add(tube([(-0.85, -0.1, 0.92), (0.85, -0.1, 0.92)], [0.012, 0.012], 4), A.mat("rope", "#9A8460", rough=0.9))
    p.add(tube([(-0.85, 0.1, 0.92), (0.85, 0.1, 0.92)], [0.012, 0.012], 4), A.mat("rope", "#9A8460", rough=0.9))
    return p.build()


def stocks(seed=11):
    p = Prop("stocks")
    w = old_wood()
    for x in (-0.6, 0.6): p.add(box(0.14, 0.14, 1.3), w, solid=True, matrix=T(x, 0, 0.65))
    p.add(box(1.4, 0.12, 0.34), w, solid=True, matrix=T(0, 0, 1.05))
    for x in (-0.3, 0.0, 0.3): p.add(lathe([(-0.07, 0.07), (0.07, 0.07)], 12), A.dark(), matrix=T(x, 0, 1.05) @ R(90, "Z"))
    p.add(box(1.5, 0.5, 0.06), w, matrix=T(0, 0.1, 0.03))
    return p.build()


def cell_bars(seed=12):
    p = Prop("cell_bars")
    ir = iron_dark(); st = damp_stone()
    p.add(box(0.4, 0.4, 2.6), st, solid=True, matrix=T(-1.3, 0, 1.3))
    p.add(box(0.4, 0.4, 2.6), st, solid=True, matrix=T(1.3, 0, 1.3))
    p.add(box(3.0, 0.4, 0.3), st, solid=True, matrix=T(0, 0, 2.45))
    for k in range(14):
        x = -1.05 + k * 0.16
        p.add(tube([(x, 0, 0), (x, 0, 2.3)], [0.018, 0.018], 6), ir, solid=True)
    for z in (0.4, 1.5): p.add(box(2.2, 0.06, 0.05), ir, solid=True, matrix=T(0, 0, z))
    p.add(box(0.1, 0.08, 0.14), A.brass(), matrix=T(0.4, -0.05, 1.1))
    return p.build()


def inquisition_banner(seed=13):
    """The Dominicans' black-and-white banner with its cross, on a pole."""
    p = Prop("inquisition_banner")
    p.add(tube([(0, 0, 0), (0, 0, 3.4)], [0.05, 0.04], 8), old_wood(), solid=True)
    p.add(tube([(-0.6, 0, 3.2), (0.6, 0, 3.2)], [0.03, 0.03], 6), old_wood())
    bm = box(1.1, 0.02, 1.8)
    p.add(bm, black_cloth(), matrix=T(0, -0.04, 2.3))
    p.add(box(0.55, 0.025, 1.8), A.linen(), matrix=T(-0.275, -0.045, 2.3))
    cross = A._cross_pattee(0.0, 0.0, 0.2) if hasattr(A, "_cross_pattee") else None
    if cross:
        bmc = bmesh.new(); vs = [bmc.verts.new((w, 0, u)) for u, w in cross]; bmc.faces.new(vs)
        r = bmesh.ops.extrude_face_region(bmc, geom=list(bmc.faces))
        for v in [e for e in r["geom"] if isinstance(e, bmesh.types.BMVert)]: v.co.y += 0.006
        p.add(bmc, A.mat("cross_bw", "#8A1A14", rough=0.8), matrix=T(0, -0.06, 2.55))
    for x in (-0.45, -0.15, 0.15, 0.45):
        p.add(slab([(-0.12, 0), (0.12, 0), (0, -0.2)], 0.02), black_cloth() if x > 0 else A.linen(), matrix=T(x, -0.04, 1.4))
    return p.build()


def lectern(seed=14):
    p = Prop("lectern")
    w = old_wood()
    p.add(lathe([(0, 0.25), (0.08, 0.2), (0.12, 0.08), (1.0, 0.06), (1.05, 0.1)], 12), w, solid=True, matrix=R(-90, "Y"))
    p.add(box(0.6, 0.45, 0.05, bevel=0.01), w, solid=True, matrix=T(0, 0, 1.12) @ R(-20, "X"))
    p.add(box(0.5, 0.36, 0.06, bevel=0.01), A.parchment(), matrix=T(0, -0.01, 1.17) @ R(-20, "X"))
    p.add(box(0.02, 0.36, 0.07), A.red_leather(), matrix=T(0, -0.01, 1.17) @ R(-20, "X"))
    p.add(tube([(0.38, 0.1, 0.0), (0.38, 0.1, 1.3)], [0.03, 0.025], 6), A.iron(), solid=True)
    p.add(tube([(0.38, 0.1, 1.3), (0.38, 0.1, 1.42)], [0.03, 0.03], 8), A.mat("wax", "#E8DCC0", rough=0.6))
    p.add(blob(0.022, 1, 0.2, 2, 3, squash=(1, 1, 2)), glow("#FFB050", 6.0), matrix=T(0.38, 0.1, 1.47))
    return p.build()


def puddle(seed=15):
    rng = random.Random(seed)
    p = Prop("puddle")
    for k in range(3):
        bm = blob(0.6 * rng.uniform(0.6, 1.0), 2, 0.25, 2, seed + k, squash=(1.6, 1.0, 0.02))
        p.add(bm, water_dark(), matrix=T(rng.uniform(-0.6, 0.6), rng.uniform(-0.3, 0.3), 0.005))
    return p.build()


def stone_pillar(seed=16):
    p = Prop("stone_pillar")
    st = damp_stone()
    p.add(box(0.7, 0.7, 0.25, bevel=0.02), st, solid=True, matrix=T(0, 0, 0.12))
    p.add(lathe([(0.25, 0.27), (0.4, 0.25), (3.2, 0.22), (3.35, 0.26)], 16), st, solid=True, matrix=R(-90, "Y"))
    p.add(box(0.75, 0.75, 0.3, bevel=0.02), st, solid=True, matrix=T(0, 0, 3.5))
    return p.build()


LANTERN = {"colour": "#FFB060", "radius": 300, "intensity": 1.2, "flicker": 0.5, "height": 1.0, "flame": True, "embers": 0}
HANGING = {"colour": "#FFB060", "radius": 360, "intensity": 1.25, "flicker": 0.5, "height": 2.0, "flame": True, "embers": 0}
CANDLE = {"colour": "#FFC070", "radius": 240, "intensity": 1.0, "flicker": 0.6, "height": 1.45, "flame": True, "embers": 0}
FUNGUS = {"colour": "#9AE0A0", "radius": 200, "intensity": 0.7, "flicker": 0.15, "height": 0.25, "flame": False, "embers": 0}

PROPS = {
    "ossuary_wall": (ossuary_wall, 2.4, None),
    "coffin": (coffin, 0.6, None),
    "stalagmites": (stalagmites, 2.0, None),
    "fungus": (fungus, 0.4, FUNGUS),
    "rubble": (rubble, 0.8, None),
    "mine_timbers": (mine_timbers, 2.6, HANGING),
    "wine_casks": (wine_casks, 1.3, None),
    "smuggler_cache": (smuggler_cache, 1.2, LANTERN),
    "gibbet_cage": (gibbet_cage, 3.0, None),
    "torture_rack": (torture_rack, 1.0, None),
    "stocks": (stocks, 1.3, None),
    "cell_bars": (cell_bars, 2.6, None),
    "inquisition_banner": (inquisition_banner, 3.5, None),
    "lectern": (lectern, 1.5, CANDLE),
    "puddle": (puddle, 0.1, None),
    "stone_pillar": (stone_pillar, 3.7, None),
}


def main(argv):
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--names", default=""); ap.add_argument("--samples", type=int, default=48)
    a = ap.parse_args(argv)
    for n in [x for x in a.names.split(",") if x] or list(PROPS):
        SC.SCENERY[n] = PROPS[n]
        e = SC.render(n, a.samples, os.path.join(SC.ROOT, "assets"), True)
        if n == "puddle":
            for v in e.values(): v.pop("footprint", None)
        pj = json.load(open(SC.PROPS_JSON))
        pj.update(e)
        with open(SC.PROPS_JSON, "w", newline="\n") as f:
            json.dump(dict(sorted(pj.items())), f, indent=1, ensure_ascii=False); f.write("\n")
        print("[underground]", n, flush=True)


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:])
