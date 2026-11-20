"""
Art for the endgame (0.8): icons for the new gear slots, upgrade materials and runes, and the props of the forge
room, the knight's cell and the Mist (anvil, forge hearth, quench tub, rune bench, Athanor, armoury rack, cot,
prie-dieu, Mist coffer). Everything is modelled from primitives here, with the armoury's materials, and rendered
with the same cameras as the rest of the game.

    blender -b --python tools/art/blender/gear_art.py -- [--icons] [--materials] [--runes] [--props] [--samples 48]

Writes assets/ui/icons/items/<slug>.png, assets/ui/icons/materials/<id>.png, assets/ui/icons/runes/<kind>.png,
assets/sprites/prop_<name>_1/_2.png (+ props.json) and the object NPC stills (npc_athanor.png, npc_armoury_rack.png).
"""
from __future__ import annotations

import json
import math
import os
import random
import shutil
import sys

import bpy  # first: the bpy module makes bmesh importable
import bmesh
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import armoury as A  # noqa: E402
import icons_items as I  # noqa: E402
import scenery as SC  # noqa: E402

ROOT = A.ROOT if hasattr(A, "ROOT") else os.path.abspath(os.path.join(HERE, "..", "..", ".."))
ICONS = os.path.join(ROOT, "assets", "ui", "icons")
T, R, S = A.T, A.R, A.S
Kit, lathe, sphere, box, slab = A.Kit, A.lathe, A.sphere, A.box, A.slab

# ----------------------------------------------------------------------------------------------- materials

def gold(): return A.mat("gold_r", "#C9A227", metal=1.0, rough=0.25, grime=0.3, scale=20)
def silver(): return A.mat("silver", "#C8CCD2", metal=1.0, rough=0.22, grime=0.3, scale=20)
def pewter(): return A.mat("pewter", "#8E9094", metal=0.85, rough=0.4, grime=0.45, scale=20)
def bronze(): return A.mat("bronze", "#9A6A32", metal=0.95, rough=0.35, grime=0.45, scale=20)
def wool(c="#5A4632", n="wool"): return A.mat(n, c, rough=0.95, grime=0.4, scale=14)
def sack(): return A.mat("sackcloth", "#7A7266", rough=0.95, grime=0.5, scale=10, bump="hammered", bump_scale=90, bump_strength=0.3)
def stone(c="#6E6A64"): return A.mat("stone" + c, c, rough=0.85, grime=0.55, scale=6, bump="hammered", bump_scale=25, bump_strength=0.5)
def straw(): return A.mat("straw", "#B89A58", rough=0.95, grime=0.35, scale=14, bump="grain", bump_scale=8, bump_strength=0.3)
def water(): return A.mat("water", "#1A2A30", metal=0.0, rough=0.05, grime=0.1, scale=4)

def glass(colour="#A8C8E0", strength=0.0, name="glass"):
    key = f"gl_{name}_{colour}_{strength}"
    m = bpy.data.materials.get(key)
    if m: return m
    m = bpy.data.materials.new(key); m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = (*A._hex(colour), 1)
    b.inputs["Roughness"].default_value = 0.05
    try: b.inputs["Transmission Weight"].default_value = 0.85
    except KeyError: pass
    if strength > 0:
        b.inputs["Emission Color"].default_value = (*A._hex(colour), 1)
        b.inputs["Emission Strength"].default_value = strength
    return m

glow = SC.glow

# ----------------------------------------------------------------------------------------------- helpers

def torus(R_, r, seg=24, rseg=10):
    """A ring about Z: radius R_, tube radius r."""
    bm = bmesh.new(); rings = []
    for i in range(seg):
        a = i / seg * math.tau
        c, s = math.cos(a), math.sin(a)
        rings.append([bm.verts.new(((R_ + r * math.cos(b)) * c, (R_ + r * math.cos(b)) * s, r * math.sin(b)))
                      for b in [j / rseg * math.tau for j in range(rseg)]])
    for i in range(seg):
        a, b = rings[i], rings[(i + 1) % seg]
        for j in range(rseg):
            bm.faces.new((a[j], a[(j + 1) % rseg], b[(j + 1) % rseg], b[j]))
    return bm


def dome(r, h, seg=24):
    """A cap of height h on a circle of radius r, about +Z."""
    prof = [(0.0, r)] + [(h * math.sin(t * math.pi / 2), r * math.cos(t * math.pi / 2)) for t in (0.25, 0.5, 0.75)] + [(h, 0.0)]
    bm = lathe(prof, seg)
    bmesh.ops.transform(bm, matrix=R(-90, "Y"), verts=bm.verts)
    return bm


def zlathe(prof, seg=20, ry=1.0, cap=True):
    """lathe() about Z instead of X: profile [(z, r)]."""
    bm = lathe(prof, seg, ry=ry, cap=cap)
    bmesh.ops.transform(bm, matrix=R(-90, "Y"), verts=bm.verts)
    return bm


def stroke(k, m, x0, z0, x1, z1, w=0.012, y=0.0, t=0.01):
    """A raised bar in the XZ plane (rune glyphs, crosses)."""
    L = math.hypot(x1 - x0, z1 - z0)
    ang = math.degrees(math.atan2(z1 - z0, x1 - x0))
    k.put(box(L + w, t, w, bevel=w * 0.2), m, matrix=T((x0 + x1) / 2, y, (z0 + z1) / 2) @ R(-ang, "Y"))


def arc(k, m, cx, cz, r, a0, a1, w=0.012, y=0.0, n=10):
    pts = [(cx + r * math.cos(math.radians(a0 + (a1 - a0) * i / n)), cz + r * math.sin(math.radians(a0 + (a1 - a0) * i / n))) for i in range(n + 1)]
    for (x0, z0), (x1, z1) in zip(pts, pts[1:]): stroke(k, m, x0, z0, x1, z1, w, y)


def chain(k, m, pts, r=0.008):
    for i, (a, b) in enumerate(zip(pts, pts[1:])):
        mid = (Vector(a) + Vector(b)) / 2
        d = Vector(b) - Vector(a)
        rot = d.to_track_quat("Z", "Y").to_matrix().to_4x4()
        k.put(torus(d.length * 0.45, r * 0.35, 10, 6), m, True, T(*mid) @ rot @ R(90 * (i % 2), "Z") @ R(90, "X"))


def pair(k, build, gap):
    for s in (-1, 1): build(k, s, gap * s)

# ----------------------------------------------------------------------------------------------- gear

def shoulders(kind):
    k = Kit(kind)
    m = {"mail": A.mail(), "leather": A.leather(), "plate": A.steel()}[kind]
    for s in (-1, 1):
        base = T(s * 0.17, 0, 0) @ R(s * 22, "Y")
        k.put(dome(0.13, 0.1), m, True, base)
        lames = 3 if kind == "plate" else 2
        for i in range(lames):
            z = -0.025 - i * 0.03
            k.put(zlathe([(z - 0.015, 0.14 + 0.012 * i), (z + 0.012, 0.135 + 0.012 * i)], 24, cap=False), m if kind != "leather" else A.red_leather(), True, base)
        if kind == "plate": k.put(zlathe([(-0.003, 0.136), (0.008, 0.136)], 24, cap=False), A.brass(), True, base)
        if kind == "leather":
            for a in range(6):
                ang = a / 6 * math.tau
                k.put(sphere(0.008, 8), A.brass(), True, base @ T(math.cos(ang) * 0.1, math.sin(ang) * 0.1, 0.06))
    k.put(box(0.2, 0.04, 0.02, bevel=0.005), A.leather(), matrix=T(0, 0, 0.09))
    return [k.build()]


def legs(kind):
    k = Kit(kind)
    for s in (-1, 1):
        x = s * 0.1
        if kind == "mail":
            k.put(zlathe([(-0.48, 0.05), (-0.25, 0.062), (0.0, 0.085), (0.18, 0.1)], 16, cap=False), A.mail(), True, T(x, 0, 0))
            k.put(zlathe([(-0.2, 0.067), (-0.17, 0.067)], 16, cap=False), A.leather(), True, T(x, 0, 0))
        elif kind == "padded":
            k.put(zlathe([(-0.2, 0.072), (-0.05, 0.085), (0.1, 0.1), (0.2, 0.11)], 18, cap=False), A.quilt(), True, T(x, 0, 0))
            k.put(zlathe([(-0.21, 0.074), (-0.18, 0.074)], 18, cap=False), A.leather(), True, T(x, 0, 0))
        else:
            k.put(zlathe([(-0.48, 0.05), (-0.25, 0.062), (0.0, 0.08), (0.18, 0.095)], 16, cap=False), A.mail(), True, T(x, 0, 0))
            k.put(zlathe([(-0.12, 0.072), (0.05, 0.088), (0.18, 0.102)], 20, ry=0.75, cap=False), A.steel(), True, T(x, -0.01, 0))
            k.put(sphere(0.06, 16), A.steel(), True, T(x, -0.035, -0.2) @ S(1, 0.8, 1))
            k.put(box(0.07, 0.01, 0.05, bevel=0.01), A.brass(), matrix=T(x, -0.085, -0.2))
    k.put(box(0.36, 0.06, 0.04, bevel=0.01), A.leather(), matrix=T(0, 0, 0.2))
    return [k.build()]


def boots(kind):
    k = Kit(kind)
    m = A.leather() if kind != "mail" else A.mail()
    for s in (-1, 1):
        x = s * 0.09
        if kind != "turn":
            k.put(zlathe([(0.05, 0.055), (0.22, 0.058), (0.3, 0.068)], 16, cap=False), m, True, T(x, 0, 0))
        else:
            k.put(zlathe([(0.04, 0.05), (0.09, 0.054)], 16, cap=False), m, True, T(x, 0, 0))
        k.put(sphere(0.06, 16), A.leather() if kind != "mail" else A.steel(), True, T(x, -0.06, 0.035) @ S(0.95, 1.9, 0.7))
        if kind == "mail":
            for i in range(3): k.put(box(0.1, 0.03, 0.012, bevel=0.004), A.steel(), matrix=T(x, -0.08 - i * 0.03, 0.07 - i * 0.012) @ R(-20, "X"))
        if kind == "riding":
            k.put(zlathe([(0.27, 0.07), (0.31, 0.074)], 16, cap=False), A.red_leather(), True, T(x, 0, 0))
            k.put(torus(0.022, 0.004, 12, 6), A.brass(), True, T(x + s * 0.06, 0.04, 0.05) @ R(90, "Y"))
    return [k.build()]


def gloves(kind):
    k = Kit(kind)
    for s in (-1, 1):
        x = s * 0.1
        if kind == "mittens":
            k.put(zlathe([(-0.12, 0.045), (0.0, 0.05)], 14, cap=False), A.mail(), True, T(x, 0, 0))
            k.put(sphere(0.05, 16), A.mail(), True, T(x, 0, 0.07) @ S(1, 0.6, 1.5))
            k.put(sphere(0.022, 10), A.mail(), True, T(x - s * 0.05, -0.01, 0.04) @ S(1, 0.8, 1.6))
        elif kind == "bracers":
            k.put(zlathe([(-0.13, 0.042), (0.12, 0.055)], 16, cap=False), A.leather(), True, T(x, 0, 0))
            for z in (-0.08, -0.03, 0.02, 0.07):
                k.put(sphere(0.007, 6), A.brass(), True, T(x, -0.05, z))
            k.put(zlathe([(0.1, 0.057), (0.12, 0.057)], 16, cap=False), A.brass(), True, T(x, 0, 0))
        else:
            k.put(zlathe([(-0.14, 0.07), (-0.07, 0.05), (0.0, 0.045)], 18, cap=False), A.steel(), True, T(x, 0, 0))
            k.put(sphere(0.05, 16), A.steel(), True, T(x, 0, 0.05) @ S(1, 0.55, 1.1))
            for i in range(4):
                k.put(box(0.022, 0.02, 0.05, bevel=0.004), A.steel(), matrix=T(x - 0.033 + i * 0.022, -0.006, 0.12) @ R(-10, "X"))
            k.put(zlathe([(-0.142, 0.072), (-0.13, 0.072)], 18, cap=False), A.brass(), True, T(x, 0, 0))
    return [k.build()]


def belt(kind):
    k = Kit(kind)
    m = A.leather() if kind != "studded" else A.red_leather()
    k.put(zlathe([(-0.025, 0.17), (0.025, 0.17)], 40, ry=0.62, cap=False), m, True)
    k.put(box(0.07, 0.02, 0.06, bevel=0.006), A.brass(), matrix=T(0, -0.108, 0))
    k.put(box(0.05, 0.022, 0.04), A.dark(), matrix=T(0, -0.11, 0))
    if kind == "studded":
        for i in range(18):
            a = i / 18 * math.tau
            k.put(sphere(0.009, 8), A.steel(), True, T(math.cos(a) * 0.172, math.sin(a) * 0.172 * 0.62, 0))
    if kind == "sword":
        k.put(box(0.03, 0.02, 0.16, bevel=0.005), A.leather(), matrix=T(-0.12, -0.08, -0.09))
        k.put(box(0.05, 0.03, 0.22, bevel=0.01), A.leather(), matrix=T(-0.13, -0.09, -0.25) @ R(12, "Y"))
        k.put(box(0.055, 0.032, 0.03, bevel=0.005), A.brass(), matrix=T(-0.13, -0.09, -0.14) @ R(12, "Y"))
    if kind == "girdle":
        k.put(sphere(0.06, 14), A.leather(), True, T(0.12, -0.08, -0.06) @ S(1, 0.6, 1.1))
        k.put(box(0.12, 0.005, 0.02), A.steel(), matrix=T(-0.1, -0.1, -0.06) @ R(70, "Y"))
    return [k.build()]


def pack(kind):
    k = Kit(kind)
    if kind == "satchel":
        k.put(box(0.3, 0.12, 0.24, bevel=0.03), A.leather(), matrix=T(0, 0, 0))
        k.put(box(0.31, 0.02, 0.16, bevel=0.01), A.red_leather(), matrix=T(0, -0.065, 0.05))
        k.put(zlathe([(-0.01, 0.2), (0.01, 0.2)], 24, ry=0.2, cap=False), A.leather(), True, T(0, 0, 0.14) @ R(90, "Y"))
        shell = Kit("shell")
        for i in range(7):
            a = -60 + i * 20
            stroke(k, A.brass(), 0, 0.0, 0.05 * math.sin(math.radians(a)), 0.05 * math.cos(math.radians(a)), 0.012, -0.08)
        k.put(box(0.035, 0.012, 0.012), A.brass(), matrix=T(0, -0.08, -0.004))
    elif kind == "haversack":
        k.put(zlathe([(-0.16, 0.0), (-0.15, 0.12), (0.0, 0.17), (0.12, 0.14), (0.18, 0.08), (0.2, 0.05), (0.24, 0.07)], 20, ry=0.7), SC.canvas(), True)
        k.put(zlathe([(0.17, 0.055), (0.2, 0.055)], 16, cap=False), A.leather(), True)
        for s in (-1, 1): k.put(box(0.03, 0.02, 0.34, bevel=0.005), A.leather(), matrix=T(s * 0.08, 0.1, 0.0))
    else:
        k.put(box(0.06, 0.06, 0.5, bevel=0.01), A.wood(), matrix=T(-0.16, 0, 0))
        k.put(box(0.06, 0.06, 0.5, bevel=0.01), A.wood(), matrix=T(0.16, 0, 0))
        k.put(box(0.38, 0.05, 0.05, bevel=0.01), A.wood(), matrix=T(0, 0, 0.2))
        for s in (-1, 1): k.put(box(0.17, 0.14, 0.2, bevel=0.03), SC.canvas(), matrix=T(s * 0.13, -0.08, -0.05))
        k.put(zlathe([(-0.13, 0.06), (0.13, 0.06)], 16), A.red_cloth(), True, T(0, -0.03, 0.27) @ R(90, "Y"))
    return [k.build()]


def mantle(kind):
    k = Kit(kind)
    m = {"templar": A.linen(), "pilgrim": wool("#5A4632", "pilgrimwool"), "penitent": sack()}[kind]
    k.put(A._torso([(-0.85, 0.38), (-0.5, 0.31), (-0.15, 0.25), (0.15, 0.22), (0.3, 0.2), (0.36, 0.13), (0.4, 0.08)], ry=0.55), m, True)
    k.put(zlathe([(0.36, 0.13), (0.43, 0.15)], 24, ry=0.8, cap=False), m, True)
    if kind == "templar":
        cross = A._cross_pattee(0.0, 0.0, 0.12)
        bm = bmesh.new(); vs = [bm.verts.new((w, 0, u)) for u, w in cross]; bm.faces.new(vs)
        r = bmesh.ops.extrude_face_region(bm, geom=list(bm.faces))
        for v in [e for e in r["geom"] if isinstance(e, bmesh.types.BMVert)]: v.co.y += 0.006
        k.put(bm, A.red_cloth(), matrix=T(0.0, -0.176, 0.1))
        k.put(torus(0.025, 0.007, 14, 6), A.brass(), True, T(0, -0.12, 0.36) @ R(90, "X"))
    elif kind == "pilgrim":
        for i in range(7):
            a = -60 + i * 20
            stroke(k, pewter(), 0.06, 0.12, 0.06 + 0.05 * math.sin(math.radians(a)), 0.12 + 0.05 * math.cos(math.radians(a)), 0.012, -0.17)
        k.put(sphere(0.05, 16), m, True, T(0, 0.05, 0.42) @ S(1.6, 1.4, 1.0))
    else:
        k.put(sphere(0.12, 18), m, True, T(0, 0.04, 0.46) @ S(1.0, 1.1, 1.15))
        k.put(zlathe([(-0.2, 0.2), (-0.17, 0.2)], 24, ry=0.6, cap=False), A.mat("rope", "#9A8460", rough=0.9, scale=40), True)
    return [k.build()]


def ring(kind):
    k = Kit(kind)
    m = {"signet": gold(), "iron": A.iron(), "gold": gold()}[kind]
    k.put(torus(0.1, 0.022 if kind != "iron" else 0.03, 32, 12), m, True, R(90, "X"))
    if kind == "signet":
        k.put(zlathe([(-0.02, 0.05), (0.02, 0.05)], 24), m, True, T(0, 0, 0.115))
        k.put(zlathe([(0.0, 0.035), (0.022, 0.035)], 24), A.red_paint(), True, T(0, 0, 0.115))
    if kind == "gold":
        k.put(zlathe([(-0.01, 0.035), (0.03, 0.04)], 16, cap=False), m, True, T(0, 0, 0.11))
        k.put(sphere(0.04, 18), glass("#C0182A", 0.4, "ruby"), True, T(0, 0, 0.145) @ S(1, 1, 0.8))
    o = k.build()
    o.matrix_world = R(20, "Z") @ R(35, "X")
    return [o]


def amulet(kind):
    k = Kit(kind)
    # A loop of chain closing at the bottom, where the pendant hangs.
    pts = [(0.15 * math.sin(math.radians(a)), 0, 0.25 - 0.23 * math.cos(math.radians(a))) for a in range(-180, 181, 15)]
    chain(k, gold() if kind == "reliquary" else silver() if kind == "cross" else A.leather(), pts, 0.02)
    if kind == "badge":
        for i in range(9):
            a = -70 + i * 17.5
            stroke(k, pewter(), 0, -0.06, 0.085 * math.sin(math.radians(a)), -0.06 + 0.085 * math.cos(math.radians(a)), 0.02, 0, 0.018)
        k.put(box(0.05, 0.02, 0.02), pewter(), matrix=T(0, 0, -0.07))
    elif kind == "reliquary":
        k.put(box(0.1, 0.035, 0.13, bevel=0.008), gold(), matrix=T(0, 0, -0.06))
        k.put(box(0.07, 0.02, 0.09), glass("#B8E0FF", 0.6, "relic"), matrix=T(0, -0.012, -0.06))
        k.put(dome(0.05, 0.03, 16), gold(), True, T(0, 0, 0.005))
    else:
        stroke(k, silver(), 0, 0.0, 0, -0.17, 0.03, 0, 0.02)
        stroke(k, silver(), -0.055, -0.05, 0.055, -0.05, 0.03, 0, 0.02)
        k.put(sphere(0.015, 10), glass("#3050C0", 0.3, "sapphire"), True, T(0, -0.012, -0.05))
    return [k.build()]


GEAR = {
    "Mail Spaulders": lambda: shoulders("mail"), "Leather Pauldrons": lambda: shoulders("leather"), "Plate Pauldrons": lambda: shoulders("plate"),
    "Mail Chausses": lambda: legs("mail"), "Padded Breeches": lambda: legs("padded"), "Plate Cuisses": lambda: legs("plate"),
    "Riding Boots": lambda: boots("riding"), "Mail Sabatons": lambda: boots("mail"), "Turnshoes": lambda: boots("turn"),
    "Mail Mittens": lambda: gloves("mittens"), "Leather Bracers": lambda: gloves("bracers"), "Gauntlets": lambda: gloves("gauntlets"),
    "Sword Belt": lambda: belt("sword"), "Leather Girdle": lambda: belt("girdle"), "Studded Belt": lambda: belt("studded"),
    "Pilgrim's Satchel": lambda: pack("satchel"), "Haversack": lambda: pack("haversack"), "Pannier Pack": lambda: pack("pannier"),
    "Templar Mantle": lambda: mantle("templar"), "Pilgrim's Cloak": lambda: mantle("pilgrim"), "Penitent's Robe": lambda: mantle("penitent"),
    "Signet Ring": lambda: ring("signet"), "Iron Band": lambda: ring("iron"), "Gold Ring": lambda: ring("gold"),
    "Pilgrim's Badge": lambda: amulet("badge"), "Reliquary Pendant": lambda: amulet("reliquary"), "Cross Pendant": lambda: amulet("cross"),
}

# ----------------------------------------------------------------------------------------------- materials (wallet)

def whetstone():
    k = Kit("whetstone")
    bm = SC.blob(0.1, 3, 0.08, 2, 1.0, squash=(2.6, 0.9, 0.6)); k.put(bm, stone("#7E7A72"), True)
    for i in range(3): stroke(k, stone("#A8A49A"), -0.12 + i * 0.05, 0.03, -0.08 + i * 0.05, 0.05, 0.004, -0.08)
    return [k.build()]

def holy_oil():
    k = Kit("holy_oil")
    k.put(zlathe([(0.0, 0.0), (0.0, 0.09), (0.08, 0.11), (0.16, 0.08), (0.2, 0.03), (0.27, 0.025), (0.28, 0.0)], 24), glass("#E8C860", 0.5, "oil"), True)
    k.put(zlathe([(0.26, 0.028), (0.32, 0.024)], 14), A.mat("cork", "#8A6A44", rough=0.9), True)
    k.put(sphere(0.03, 12), A.red_paint(), True, T(0, -0.02, 0.3) @ S(1.2, 0.6, 1))
    return [k.build()]

def soul_core():
    k = Kit("soul_core")
    bm = bmesh.new(); bmesh.ops.create_icosphere(bm, subdivisions=1, radius=0.11)
    k.put(bm, glow("#9A5AFF", 4.0), False, S(1, 1, 1.5))
    for a in (0, 60, 120):
        k.put(torus(0.13, 0.008, 28, 6), A.iron(), True, R(a, "Z") @ R(90, "X") @ S(1, 1.5, 1))
    return [k.build()]

def mist_ember():
    k = Kit("mist_ember")
    k.put(SC.blob(0.11, 3, 0.35, 3, 4.0), A.mat("ash", "#3A3E46", rough=0.95), True)
    k.put(SC.blob(0.075, 2, 0.3, 4, 5.0), glow("#B8D4FF", 5.0), False, T(0, -0.04, 0.03))
    return [k.build()]

def trophy_relic():
    k = Kit("trophy_relic")
    k.put(zlathe([(0.0, 0.07), (0.05, 0.08), (0.08, 0.06)], 18), gold(), True)
    tooth = zlathe([(0.0, 0.045), (0.1, 0.04), (0.2, 0.025), (0.28, 0.0)], 16)
    k.put(tooth, A.mat("ivory", "#E8DCC0", rough=0.5, grime=0.3), True, T(0, 0, 0.07) @ R(-12, "Y"))
    for a in range(4):
        ang = a / 4 * math.tau
        k.put(sphere(0.012, 8), glass("#C0182A", 0.4, "ruby"), True, T(math.cos(ang) * 0.075, math.sin(ang) * 0.075, 0.05))
    return [k.build()]

def pet_token():
    k = Kit("pet_token")
    k.put(zlathe([(-0.012, 0.12), (0.012, 0.12)], 36), bronze(), True, R(90, "X"))
    k.put(torus(0.11, 0.008, 36, 6), bronze(), True, T(0, -0.012, 0) @ R(90, "X"))
    hound = [(-0.07, -0.03), (-0.05, 0.02), (-0.02, 0.03), (0.03, 0.03), (0.05, 0.06), (0.08, 0.05), (0.07, 0.02), (0.05, -0.01), (0.05, -0.05),
             (0.03, -0.05), (0.03, -0.01), (-0.03, -0.01), (-0.04, -0.05), (-0.06, -0.05)]
    k.put(slab(hound, 0.012), bronze(), matrix=T(0, -0.015, 0))
    o = k.build()
    o.matrix_world = R(-20, "Z")
    return [o]

MATERIALS = {"whetstone": whetstone, "holy_oil": holy_oil, "soul_core": soul_core, "mist_ember": mist_ember,
             "trophy_relic": trophy_relic, "pet_token": pet_token}

# ----------------------------------------------------------------------------------------------- runes

def glyph(k, m, kind):
    y = -0.056
    if kind == "might": stroke(k, m, 0, -0.08, 0, 0.08, y=y); stroke(k, m, 0, 0.08, -0.05, 0.03, y=y); stroke(k, m, 0, 0.08, 0.05, 0.03, y=y); stroke(k, m, -0.05, -0.08, 0.05, -0.08, y=y)
    elif kind == "vigour": stroke(k, m, 0, -0.08, 0, 0.08, y=y); stroke(k, m, -0.07, 0.02, 0.07, 0.02, y=y); arc(k, m, 0, 0.02, 0.045, 0, 360, y=y)
    elif kind == "haste": stroke(k, m, -0.06, -0.07, 0.0, 0.0, y=y); stroke(k, m, 0.0, 0.0, -0.03, 0.0, y=y); stroke(k, m, -0.03, 0.0, 0.06, 0.08, y=y); stroke(k, m, 0.02, -0.07, 0.07, -0.02, y=y)
    elif kind == "wisdom": arc(k, m, 0, 0, 0.07, 20, 160, y=y); arc(k, m, 0, 0, 0.07, 200, 340, y=y); arc(k, m, 0, 0, 0.025, 0, 360, y=y)
    elif kind == "flame":
        arc(k, m, 0, -0.03, 0.05, 180, 360, y=y); stroke(k, m, -0.05, -0.03, 0.0, 0.08, y=y); stroke(k, m, 0.05, -0.03, 0.0, 0.08, y=y); stroke(k, m, 0, -0.05, 0.0, 0.02, y=y)
    elif kind == "frost":
        for a in (0, 60, 120):
            c, s = math.cos(math.radians(a)), math.sin(math.radians(a)); stroke(k, m, -0.08 * c, -0.08 * s, 0.08 * c, 0.08 * s, y=y)
    elif kind == "storms": stroke(k, m, 0.03, 0.08, -0.03, 0.0, y=y); stroke(k, m, -0.03, 0.0, 0.03, 0.0, y=y); stroke(k, m, 0.03, 0.0, -0.03, -0.08, y=y)
    elif kind == "venom": arc(k, m, 0, -0.025, 0.045, 180, 360, y=y); stroke(k, m, -0.045, -0.025, 0, 0.07, y=y); stroke(k, m, 0.045, -0.025, 0, 0.07, y=y); arc(k, m, 0, -0.03, 0.012, 0, 360, y=y)
    elif kind == "corrosion": arc(k, m, 0, 0, 0.075, 0, 300, y=y); arc(k, m, 0, 0, 0.045, 60, 360, y=y); arc(k, m, 0, 0, 0.015, 0, 360, y=y)
    elif kind == "keenness": stroke(k, m, 0, 0.08, 0.04, 0.0, y=y); stroke(k, m, 0.04, 0.0, 0, -0.08, y=y); stroke(k, m, 0, -0.08, -0.04, 0.0, y=y); stroke(k, m, -0.04, 0.0, 0, 0.08, y=y)
    elif kind == "warding": stroke(k, m, -0.06, 0.07, 0.06, 0.07, y=y); stroke(k, m, -0.06, 0.07, -0.06, 0.0, y=y); stroke(k, m, 0.06, 0.07, 0.06, 0.0, y=y); stroke(k, m, -0.06, 0.0, 0, -0.08, y=y); stroke(k, m, 0.06, 0.0, 0, -0.08, y=y)
    elif kind == "fortune": arc(k, m, 0, 0, 0.07, 0, 360, y=y); stroke(k, m, 0, -0.05, 0, 0.05, y=y); stroke(k, m, -0.03, 0.025, 0.03, 0.025, y=y); stroke(k, m, -0.03, -0.025, 0.03, -0.025, y=y)
    elif kind == "leech": arc(k, m, 0.0, 0.0, 0.07, 100, 260, y=y); arc(k, m, 0.03, 0.0, 0.055, 110, 250, y=y); stroke(k, m, -0.02, -0.03, 0.02, -0.07, y=y)


def rune(kind):
    k = Kit("rune_" + kind)
    k.put(SC.blob(0.14, 3, 0.07, 2.2, sum(map(ord, kind)) * 0.01, squash=(1, 0.35, 1.05)), stone("#5A5650"), True)
    glyph(k, glow("#F2E6C8", 2.4), kind)
    o = k.build()
    o.matrix_world = R(-12, "Z")
    return [o]

RUNES = ["might", "vigour", "haste", "wisdom", "flame", "frost", "storms", "venom", "corrosion", "keenness", "warding", "fortune", "leech"]

# ----------------------------------------------------------------------------------------------- props

def objs_of(builds):
    out = []
    for o in builds: out.extend(o if isinstance(o, list) else [o])
    return out


def anvil():
    p = SC.Prop("anvil")
    p.add(SC.tube([(0, 0, 0), (0, 0, 0.5)], [0.32, 0.28], 14), A.wood(), solid=True)
    iron = A.mat("anvil_iron", "#3E3E42", metal=0.9, rough=0.45, grime=0.5)
    p.add(box(0.3, 0.22, 0.12, bevel=0.02), iron, solid=True, matrix=T(0, 0, 0.56))
    p.add(box(0.18, 0.16, 0.18, bevel=0.02), iron, solid=True, matrix=T(0, 0, 0.7))
    p.add(box(0.62, 0.24, 0.14, bevel=0.025), iron, solid=True, matrix=T(0.04, 0, 0.86))
    horn = A.lathe([(0, 0.07), (0.15, 0.055), (0.28, 0.02), (0.32, 0.0)], 14)
    p.add(horn, iron, solid=True, matrix=T(0.35, 0, 0.87) @ S(1, 1, 0.9))
    p.add(box(0.6, 0.23, 0.01), A.mat("anvil_face", "#9A9CA2", metal=1.0, rough=0.2, grime=0.2), solid=True, matrix=T(0.04, 0, 0.935))
    # The hammer resting on it, and glowing work.
    p.add(SC.tube([(-0.2, -0.05, 0.96), (0.18, 0.12, 0.98)], [0.018, 0.018], 8), A.wood())
    p.add(box(0.1, 0.06, 0.06, bevel=0.01), A.steel(), matrix=T(-0.22, -0.06, 0.98) @ R(25, "Z"))
    p.add(box(0.26, 0.03, 0.012), glow("#FF7A20", 4.0), matrix=T(0.12, 0.05, 0.945) @ R(-10, "Z"))
    return p.build()


def forge_hearth():
    p = SC.Prop("forge_hearth")
    rk = stone("#5E5650")
    p.add(box(1.5, 1.0, 0.75, bevel=0.03), rk, solid=True, matrix=T(0, 0, 0.375))
    p.add(box(1.3, 0.8, 0.08, bevel=0.01), A.mat("hearth_bed", "#1A1412", rough=0.95), solid=True, matrix=T(0, 0, 0.77))
    p.add(SC.blob(0.42, 3, 0.25, 4, 2.0, squash=(1.6, 1.0, 0.35)), glow("#E04A10", 3.2), matrix=T(0, 0, 0.82))
    for i, (dx, dy) in enumerate(((0, 0), (0.25, 0.1), (-0.25, -0.05))):
        p.add(SC.blob(0.14, 2, 0.4, 3, i, squash=(1, 1, 1.8)), glow("#FF8A2A", 4.0), matrix=T(dx, dy, 0.98))
    # Hood and chimney.
    bm = A.lathe([(0, 0.95), (0.6, 0.35), (0.62, 0.3)], 4, ry=0.75)
    p.add(bm, rk, solid=True, matrix=T(0, 0.1, 1.5) @ R(-90, "Y") @ R(45, "X"))
    p.add(box(0.5, 0.45, 1.4, bevel=0.02), rk, solid=True, matrix=T(0, 0.15, 2.7))
    for s in (-1, 1): p.add(box(0.14, 0.14, 0.8), rk, solid=True, matrix=T(s * 0.68, 0.38, 1.15))
    # Bellows.
    p.add(box(0.5, 0.32, 0.12, bevel=0.04), A.leather(), matrix=T(-1.05, 0.0, 0.55) @ R(10, "Y"))
    p.add(SC.tube([(-0.8, 0, 0.55), (-0.62, 0, 0.62)], [0.03, 0.025], 8), A.iron())
    return p.build()


def quench_tub():
    p = SC.Prop("quench_tub")
    p.add(A.lathe([(0, 0.4), (0.5, 0.45)], 20, cap=False), A.wood(), solid=True, matrix=R(-90, "Y"))
    p.add(A.lathe([(0, 0.0), (0.0, 0.4)], 20), A.wood(), solid=True, matrix=R(-90, "Y"))
    for z in (0.08, 0.42): p.add(A.lathe([(z - 0.02, 0.43), (z + 0.02, 0.44)], 20, cap=False), A.iron(), solid=True, matrix=R(-90, "Y"))
    p.add(A.lathe([(0.0, 0.0), (0.001, 0.43)], 20), water(), solid=True, matrix=T(0, 0, 0.44) @ R(-90, "Y"))
    p.add(SC.tube([(0.1, -0.1, 0.4), (0.5, 0.25, 0.9)], [0.012, 0.012], 6), A.iron())
    p.add(SC.tube([(0.14, -0.06, 0.4), (0.52, 0.3, 0.88)], [0.012, 0.012], 6), A.iron())
    return p.build()


def rune_bench():
    p = SC.Prop("rune_bench")
    p.add(box(1.4, 0.7, 0.08, bevel=0.01), A.wood(), solid=True, matrix=T(0, 0, 0.82))
    for x in (-0.6, 0.6):
        for y in (-0.28, 0.28): p.add(box(0.08, 0.08, 0.8), A.wood(), solid=True, matrix=T(x, y, 0.4))
    for i, kind in enumerate(("flame", "frost", "might", "warding")):
        st = SC.blob(0.1, 2, 0.08, 2, i, squash=(1, 1, 0.4))
        p.add(st, stone("#5A5650"), matrix=T(-0.45 + i * 0.28, -0.05 + (i % 2) * 0.12, 0.9))
        p.add(SC.blob(0.03, 1, 0.1, 2, i), glow(("#FF7A30", "#9AC8FF", "#F2E6C8", "#C9A227")[i], 3.0), matrix=T(-0.45 + i * 0.28, -0.05 + (i % 2) * 0.12, 0.95))
    p.add(box(0.18, 0.02, 0.02), A.steel(), matrix=T(0.4, 0.2, 0.88) @ R(30, "Z"))
    p.add(SC.tube([(0.55, 0.2, 0.86), (0.55, 0.2, 1.04)], [0.03, 0.03], 8), A.mat("wax", "#E8DCC0", rough=0.6))
    p.add(SC.blob(0.02, 1, 0.2, 2, 9, squash=(1, 1, 2)), glow("#FFB050", 6.0), matrix=T(0.55, 0.2, 1.08))
    return p.build()


def athanor():
    p = SC.Prop("athanor")
    brick = stone("#7A5A48")
    p.add(SC.tube([(0, 0, 0), (0, 0, 1.6), (0, 0, 2.1)], [0.62, 0.52, 0.34], 20), brick, solid=True)
    p.add(A.lathe([(0, 0.36), (0.25, 0.28), (0.4, 0.12), (0.6, 0.08), (0.9, 0.07)], 20), A.iron(), solid=True, matrix=T(0, 0, 2.08) @ R(-90, "Y"))
    # The window where the fire shows, and the glass at the top.
    p.add(box(0.36, 0.1, 0.42, bevel=0.03), A.iron(), solid=True, matrix=T(0, -0.55, 0.7))
    p.add(box(0.28, 0.12, 0.34, bevel=0.03), glow("#C060FF", 5.0), matrix=T(0, -0.56, 0.7))
    p.add(A.lathe([(0, 0.0), (0.0, 0.12), (0.12, 0.16), (0.24, 0.1), (0.3, 0.04), (0.5, 0.03)], 20), glass("#9AE0C0", 1.2, "alembic"), matrix=T(0.25, -0.2, 1.75) @ R(-90, "Y"))
    p.add(SC.tube([(0.25, -0.2, 2.25), (0.55, -0.35, 2.15), (0.75, -0.4, 1.7)], [0.02, 0.018, 0.016], 8), glass("#9AE0C0", 0.6, "alembic2"))
    for a in range(6):
        ang = a / 6 * math.tau
        p.add(box(0.06, 0.06, 1.7), A.iron(), solid=True, matrix=T(math.cos(ang) * 0.6, math.sin(ang) * 0.6, 0.85))
    p.add(A.lathe([(1.3, 0.56), (1.38, 0.56)], 20, cap=False), A.brass(), solid=True, matrix=R(-90, "Y"))
    p.add(SC.blob(0.1, 2, 0.4, 3, 3, squash=(1, 1, 2.2)), glow("#B070FF", 3.5), matrix=T(0, 0, 3.05))
    return p.build()


def armoury_rack():
    out = []
    p = SC.Prop("armoury_rack")
    for x in (-0.55, 0.55):
        p.add(box(0.1, 0.1, 1.9, bevel=0.01), A.wood(), solid=True, matrix=T(x, 0, 0.95))
        p.add(box(0.1, 0.6, 0.08), A.wood(), solid=True, matrix=T(x, 0, 0.04))
    p.add(box(1.25, 0.1, 0.1), A.wood(), solid=True, matrix=T(0, 0, 1.6))
    p.add(box(0.06, 0.06, 0.45), A.wood(), solid=True, matrix=T(0, 0, 1.38))
    out += p.build()
    def bake(o, m):
        # Into the mesh itself: the scenery renderer moves objects by setting their location.
        o.data.transform(m @ o.matrix_world); o.matrix_world = Matrix.Identity(4); return o
    hauberk = bake(A.mail_hauberk(), T(0, -0.02, 1.15) @ S(1.1))
    helm = bake(A.great_helm(), T(0, -0.02, 1.75))
    sword = bake(A.arming_sword(), T(0.72, -0.15, 0.55) @ R(-95, "Y"))
    shield = bake(A.heater_shield(), T(-0.72, -0.18, 0.5) @ R(-90, "Y") @ R(-80, "X") @ R(90, "Z"))
    return out + [hauberk, helm, sword, shield]


def mist_coffer():
    p = SC.Prop("mist_coffer")
    wood = A.mat("mist_wood", "#3A3A44", rough=0.85, grime=0.5, scale=8, bump="grain", bump_strength=0.2)
    iron = A.mat("mist_iron", "#5A6070", metal=0.8, rough=0.45, grime=0.5)
    p.add(box(0.95, 0.58, 0.48, bevel=0.02), wood, solid=True, matrix=T(0, 0, 0.24))
    p.add(A.lathe([(-0.475, 0.0), (-0.475, 0.29), (0.475, 0.29), (0.475, 0.0)], 16, ry=1.0, rz=0.6, cap=True), wood, solid=True, matrix=T(0, 0, 0.48))
    for x in (-0.32, 0.0, 0.32):
        p.add(box(0.06, 0.6, 0.5, bevel=0.005), iron, solid=True, matrix=T(x, 0, 0.245))
    p.add(box(0.9, 0.6, 0.02), glow("#9AB8F0", 5.0), matrix=T(0, 0, 0.485))
    p.add(box(0.14, 0.04, 0.16, bevel=0.01), iron, solid=True, matrix=T(0, -0.3, 0.42))
    for i in range(5):
        p.add(SC.blob(0.18, 2, 0.4, 2, i, squash=(1.4, 1, 0.5)), A.mat("mistwisp", "#6A7890", rough=1.0, grime=0.2), matrix=T(-0.5 + i * 0.25, 0.2 - (i % 2) * 0.45, 0.03) @ S(1, 1, 0.6))
    return p.build()


def cot():
    p = SC.Prop("cot")
    p.add(box(2.0, 0.85, 0.12, bevel=0.01), A.wood(), solid=True, matrix=T(0, 0, 0.36))
    for x in (-0.95, 0.95):
        for y in (-0.38, 0.38): p.add(box(0.08, 0.08, 0.4), A.wood(), solid=True, matrix=T(x, y, 0.2))
    p.add(box(1.9, 0.78, 0.14, bevel=0.05), straw(), matrix=T(0, 0, 0.49))
    p.add(box(1.2, 0.8, 0.06, bevel=0.03), A.mat("blanket", "#6A1A14", rough=0.95, scale=12), matrix=T(0.32, 0, 0.58))
    p.add(box(0.06, 0.82, 0.02), A.linen(), matrix=T(-0.1, 0, 0.6))
    p.add(box(0.35, 0.5, 0.1, bevel=0.04), A.linen(), matrix=T(-0.75, 0, 0.6))
    return p.build()


def prie_dieu():
    p = SC.Prop("prie_dieu")
    p.add(box(0.7, 0.35, 0.08, bevel=0.01), A.wood(), solid=True, matrix=T(0, 0.2, 0.15))
    p.add(box(0.7, 0.08, 0.95, bevel=0.01), A.wood(), solid=True, matrix=T(0, -0.05, 0.48))
    p.add(box(0.72, 0.36, 0.06, bevel=0.01), A.wood(), solid=True, matrix=T(0, -0.13, 0.98) @ R(-15, "X"))
    p.add(box(0.3, 0.22, 0.05, bevel=0.01), A.red_leather(), matrix=T(0.0, -0.13, 1.03) @ R(-15, "X"))
    p.add(SC.tube([(0.28, -0.1, 1.0), (0.28, -0.1, 1.16)], [0.025, 0.025], 8), A.mat("wax", "#E8DCC0", rough=0.6))
    p.add(SC.blob(0.018, 1, 0.2, 2, 2, squash=(1, 1, 2)), glow("#FFB050", 6.0), matrix=T(0.28, -0.1, 1.2))
    return p.build()


HEARTH_LIGHT = {"colour": "#FF8A38", "radius": 460, "intensity": 1.6, "flicker": 0.8, "height": 1.0, "flame": True, "embers": 6}
ATHANOR_LIGHT = {"colour": "#B070FF", "radius": 380, "intensity": 1.2, "flicker": 0.4, "height": 0.7, "flame": True, "embers": 2}
CANDLE_LIGHT = {"colour": "#FFC070", "radius": 200, "intensity": 0.9, "flicker": 0.6, "height": 1.1, "flame": True, "embers": 0}
COFFER_LIGHT = {"colour": "#8AA8E0", "radius": 220, "intensity": 0.8, "flicker": 0.3, "height": 0.5, "flame": False, "embers": 0}

PROPS = {
    "anvil": (anvil, 1.1, None),
    "forge_hearth": (forge_hearth, 3.4, HEARTH_LIGHT),
    "quench_tub": (quench_tub, 1.0, None),
    "rune_bench": (rune_bench, 1.2, CANDLE_LIGHT),
    "athanor": (athanor, 3.2, ATHANOR_LIGHT),
    "armoury_rack": (armoury_rack, 2.0, None),
    "mist_coffer": (mist_coffer, 0.9, COFFER_LIGHT),
    "cot": (cot, 0.7, None),
    "prie_dieu": (prie_dieu, 1.2, CANDLE_LIGHT),
}
NPC_STILLS = {"athanor": "npc_athanor.png", "armoury_rack": "npc_armoury_rack.png"}

# ----------------------------------------------------------------------------------------------- render

def render_icon(objs, path, size, samples, orient=None):
    sc = bpy.context.scene
    if orient is not None:
        for o in objs: o.matrix_world = orient @ o.matrix_world
    I.fit(I_cam(), objs)
    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)


def I_cam():
    return bpy.context.scene.camera


def icons(names, size, samples):
    out = os.path.join(ICONS, "items")
    os.makedirs(out, exist_ok=True)
    for n in names:
        sc, cam = I.scene(size, samples)
        objs = GEAR[n]()
        orient = R(-24, "Z") if n not in ("Signet Ring", "Iron Band", "Gold Ring", "Pet Token") else None
        if n in ("Sword Belt", "Leather Girdle", "Studded Belt"): orient = R(-24, "Z") @ R(38, "X")  # seen from above, so the loop shows
        render_icon(objs, os.path.join(out, A.slug(n) + ".png"), size, samples, orient)
        print("[gear_art] icon", n, flush=True)


def material_icons(size, samples):
    out = os.path.join(ICONS, "materials"); os.makedirs(out, exist_ok=True)
    for n, f in MATERIALS.items():
        I.scene(size, samples)
        orient = None if n == "pet_token" else R(-18, "Z") @ R(40, "X") if n == "whetstone" else R(-18, "Z")
        render_icon(f(), os.path.join(out, n + ".png"), size, samples, orient)
        print("[gear_art] material", n, flush=True)


def rune_icons(size, samples):
    out = os.path.join(ICONS, "runes"); os.makedirs(out, exist_ok=True)
    for n in RUNES:
        I.scene(size, samples)
        render_icon(rune(n), os.path.join(out, n + ".png"), size, samples)
        print("[gear_art] rune", n, flush=True)


def props(names, samples):
    for n in names:
        SC.SCENERY[n] = PROPS[n]
        e = SC.render(n, samples, os.path.join(ROOT, "assets"), True)
        pj = json.load(open(SC.PROPS_JSON))
        pj.update(e)
        with open(SC.PROPS_JSON, "w", newline="\n") as f:
            json.dump(dict(sorted(pj.items())), f, indent=1, ensure_ascii=False); f.write("\n")
        if n in NPC_STILLS:
            shutil.copyfile(os.path.join(ROOT, "assets", "sprites", f"prop_{n}_1.png"), os.path.join(ROOT, "assets", "sprites", NPC_STILLS[n]))
        print("[gear_art] prop", n, flush=True)


def main(argv):
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--icons", action="store_true"); ap.add_argument("--materials", action="store_true")
    ap.add_argument("--runes", action="store_true"); ap.add_argument("--props", action="store_true")
    ap.add_argument("--names", default=""); ap.add_argument("--size", type=int, default=128); ap.add_argument("--samples", type=int, default=48)
    a = ap.parse_args(argv)
    want = [n for n in a.names.split(",") if n]
    if a.icons: icons(want or list(GEAR), a.size, a.samples)
    if a.materials: material_icons(a.size, a.samples)
    if a.runes: rune_icons(a.size, a.samples)
    if a.props: props(want or list(PROPS), a.samples)


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:])
