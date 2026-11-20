"""
Procedural scenery: trees, rocks, bushes, graves, braziers, tents, banners, market stalls and grass, built in Blender
and rendered with the game's locked prop camera (props.py), so they sit beside the Meshy props.

    python tools/art/blender/scenery.py [--samples 48] [--names oak_tree,boulder]     (bpy module)

Writes assets/sprites/prop_<name>_1.png / _2.png and their entries in assets/data/props.json. Only the parts marked
solid (trunks, stones, posts) count toward the ground footprint, so heroes walk under a canopy but not through a trunk.
Props with a fire also get a "light" (see LightDef).
"""
from __future__ import annotations

import json
import math
import os
import random
import sys

import bpy  # first: the bpy module makes bmesh importable
import bmesh
from mathutils import Matrix, Vector, noise

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import armoury as A  # noqa: E402
import figures as F  # noqa: E402
import props as P  # noqa: E402

ROOT = F.ROOT
PROPS_JSON = os.path.join(ROOT, "assets", "data", "props.json")
T, R, S = A.T, A.R, A.S

# ----------------------------------------------------------------------------------------------- materials

def bark(dark=False):
    return A.mat("bark_d" if dark else "bark", "#2A2522" if dark else "#4A3A2C", rough=0.9, grime=0.6, scale=14,
                 bump="hammered", bump_scale=40, bump_strength=0.6)

def charred(): return A.mat("charred", "#1C1816", rough=0.95, grime=0.5, scale=20, bump="hammered", bump_scale=50, bump_strength=0.7)

def leaves(tone="#3E5A24", name="leaf"):
    m = A.mat(name, tone, rough=0.8, grime=0.55, scale=6, bump="hammered", bump_scale=55, bump_strength=0.9)
    return m

def needles(): return leaves("#26402A", "needle")
def bush_leaf(): return leaves("#35502A", "bushleaf")
def grass(): return A.mat("grass", "#5E7A30", rough=0.85, grime=0.45, scale=9)
def dry_grass(): return A.mat("drygrass", "#8A7A44", rough=0.9, grime=0.4, scale=9)
def rock(tone="#77726A"): return A.mat("rock" + tone, tone, rough=0.85, grime=0.55, scale=5, bump="hammered", bump_scale=18, bump_strength=0.5)
def moss(): return A.mat("moss", "#4A5E2A", rough=0.95, grime=0.5, scale=10, bump="hammered", bump_scale=60, bump_strength=0.4)
def earth(): return A.mat("earth", "#3E3024", rough=0.95, grime=0.5, scale=8, bump="hammered", bump_scale=30, bump_strength=0.4)
def canvas(): return A.mat("canvas", "#B8AC90", rough=0.9, grime=0.35, scale=10)
def stripe_cloth():
    m = bpy.data.materials.get("arm_stripe")
    if m: return m
    m = A.mat("stripe", "#8A1712", rough=0.85, grime=0.3, scale=12)
    nt = m.node_tree; N = nt.nodes; L = nt.links
    b = N["Principled BSDF"]
    tc = N.new("ShaderNodeTexCoord"); w = N.new("ShaderNodeTexWave"); w.wave_type = "BANDS"; w.bands_direction = "X"
    w.inputs["Scale"].default_value = 1.6; w.inputs["Distortion"].default_value = 0
    L.new(tc.outputs["Object"], w.inputs["Vector"])
    ramp = N.new("ShaderNodeValToRGB"); ramp.color_ramp.interpolation = "CONSTANT"
    ramp.color_ramp.elements[0].color = (*A._hex("#5E1A14"), 1); ramp.color_ramp.elements[1].position = 0.5
    ramp.color_ramp.elements[1].color = (*A._hex("#9E927A"), 1)
    L.new(w.outputs["Fac"], ramp.inputs["Fac"]); L.new(ramp.outputs["Color"], b.inputs["Base Color"])
    return m

def glow(colour="#FF7A20", strength=6.0):
    key = f"glow_{colour}_{strength}"
    m = bpy.data.materials.get(key)
    if m: return m
    m = bpy.data.materials.new(key); m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = (*A._hex("#2A1008"), 1)
    b.inputs["Emission Color"].default_value = (*A._hex(colour), 1)
    b.inputs["Emission Strength"].default_value = strength
    return m

# ----------------------------------------------------------------------------------------------- geometry

def tube(points, radii, seg=10, cap_top=True):
    """A tube through 3D points with a radius at each point (trunks, branches, poles, logs)."""
    bm = bmesh.new()
    rings = []
    for i, p in enumerate(points):
        p = Vector(p)
        d = (Vector(points[min(i + 1, len(points) - 1)]) - Vector(points[max(i - 1, 0)])).normalized()
        up = Vector((0, 0, 1)) if abs(d.z) < 0.95 else Vector((1, 0, 0))
        a = d.cross(up).normalized(); b = d.cross(a).normalized()
        ring = []
        for k in range(seg):
            t = k / seg * math.tau
            ring.append(bm.verts.new(p + (a * math.cos(t) + b * math.sin(t)) * radii[i]))
        rings.append(ring)
    for r0, r1 in zip(rings, rings[1:]):
        for k in range(seg):
            bm.faces.new((r0[k], r0[(k + 1) % seg], r1[(k + 1) % seg], r1[k]))
    if cap_top: bm.faces.new(rings[-1])
    bm.faces.new(list(reversed(rings[0])))
    return bm


def blob(r, sub=3, rough=0.35, freq=1.6, seed=0.0, flat_bottom=None, squash=(1, 1, 1)):
    """A lumpy sphere: icosphere pushed in and out by fractal noise (foliage clumps, rocks, bushes)."""
    bm = bmesh.new()
    bmesh.ops.create_icosphere(bm, subdivisions=sub, radius=r)
    off = Vector((seed * 13.1, seed * 7.7, seed * 3.3))
    for v in bm.verts:
        n = v.co.normalized()
        k = 1 + rough * noise.fractal(n * freq + off, 0.6, 2.0, 4)
        v.co = Vector((n.x * r * k * squash[0], n.y * r * k * squash[1], n.z * r * k * squash[2]))
        if flat_bottom is not None and v.co.z < flat_bottom: v.co.z = flat_bottom + (v.co.z - flat_bottom) * 0.1
    return bm


def canopy(p, centre, r, rng, tones, clumps=70, squash=0.8, core_mat=None, leaf=0.075):
    """Foliage: a dark core blob covered in small leaf cards of a few tones, so the edge breaks up like real leaves.
    clumps sets the density (leaves = clumps * 30)."""
    c = Vector(centre)
    p.add(blob(r * 0.86, 3, 0.25, 1.8, rng.random() * 50, squash=(1, 1, squash)), core_mat or tones[0], matrix=T(*c))
    by_tone = [bmesh.new() for _ in tones]
    for _ in range(clumps * 30):
        d = Vector((rng.gauss(0, 1), rng.gauss(0, 1), rng.gauss(0, 1) + 0.3)).normalized()
        if d.z < -0.6: continue
        pos = c + Vector((d.x * r, d.y * r, d.z * r * squash)) * rng.uniform(0.86, 1.08)
        n = (d + Vector((rng.gauss(0, 0.6), rng.gauss(0, 0.6), rng.gauss(0, 0.6)))).normalized()
        a = n.orthogonal().normalized(); b = n.cross(a)
        rot = rng.uniform(0, math.tau)
        a, b = a * math.cos(rot) + b * math.sin(rot), b * math.cos(rot) - a * math.sin(rot)
        L = leaf * rng.uniform(0.7, 1.3) * (r / 0.9)
        bm = by_tone[rng.randrange(len(tones))]
        q = [bm.verts.new(pos + a * L), bm.verts.new(pos + b * L * 0.45), bm.verts.new(pos - a * L), bm.verts.new(pos - b * L * 0.45)]
        bm.faces.new(q)
    for bm, m in zip(by_tone, tones):
        p.add(bm, m, smooth=False)


def wander(start, direction, length, steps, rng, bend=0.25):
    pts = [Vector(start)]
    d = Vector(direction).normalized()
    for _ in range(steps):
        d = (d + Vector((rng.uniform(-bend, bend), rng.uniform(-bend, bend), rng.uniform(-bend * 0.4, bend * 0.6)))).normalized()
        pts.append(pts[-1] + d * (length / steps))
    return pts


class Prop:
    """Collects solid parts (count toward the footprint) and soft parts (canopies, cloth, grass)."""

    def __init__(self, name):
        self.solid, self.soft = A.Kit(name + "_solid"), A.Kit(name + "_soft")
        self.has_solid = self.has_soft = False

    def add(self, bm, m, solid=False, smooth=True, matrix=None):
        (self.solid if solid else self.soft).put(bm, m, smooth=smooth, matrix=matrix)
        if solid: self.has_solid = True
        else: self.has_soft = True
        return self

    def build(self):
        out = []
        if self.has_solid: out.append(self.solid.build())
        if self.has_soft:
            o = self.soft.build(); o["soft"] = True; out.append(o)
        return out

# ----------------------------------------------------------------------------------------------- props

def oak_tree(seed=1):
    rng = random.Random(seed)
    p = Prop("oak")
    trunk = wander((0, 0, 0), (0, 0, 1), 2.3, 6, rng, 0.12)
    p.add(tube(trunk, [0.32, 0.26, 0.23, 0.21, 0.19, 0.17, 0.15], 14), bark(), solid=True)
    # Root flare.
    for k in range(5):
        a = k / 5 * math.tau + rng.uniform(-0.3, 0.3)
        p.add(tube([(math.cos(a) * 0.1, math.sin(a) * 0.1, 0.35), (math.cos(a) * 0.55, math.sin(a) * 0.55, 0.02)], [0.14, 0.05], 8), bark(), solid=True)
    tips = []
    for k in range(6):
        a = k / 6 * math.tau + rng.uniform(-0.4, 0.4)
        base = trunk[rng.randint(3, 6)]
        d = Vector((math.cos(a), math.sin(a), rng.uniform(0.5, 1.1)))
        br = wander(base, d, rng.uniform(1.1, 1.7), 4, rng, 0.2)
        p.add(tube(br, [0.12, 0.09, 0.07, 0.05, 0.03], 8), bark())
        tips.append(br[-1])
    tips.append(trunk[-1] + Vector((0, 0, 0.6)))
    tones = [leaves("#2A4418", "leaf_a"), leaves("#36521E", "leaf_b"), leaves("#446024", "leaf_c"), leaves("#56682A", "leaf_d")]
    core = leaves("#1E3012", "leaf_core")
    for i, t in enumerate(tips):
        c = t + Vector((rng.uniform(-0.3, 0.3), rng.uniform(-0.3, 0.3), rng.uniform(0.0, 0.4)))
        canopy(p, c, rng.uniform(0.6, 1.1), rng, tones, clumps=45, core_mat=core)
        if rng.random() < 0.6:
            canopy(p, c + Vector((rng.uniform(-0.8, 0.8), rng.uniform(-0.8, 0.8), rng.uniform(-0.5, 0.1))), rng.uniform(0.45, 0.65), rng, tones, clumps=22, core_mat=core)
    return p.build()


def pine_tree(seed=2):
    rng = random.Random(seed)
    p = Prop("pine")
    p.add(tube([(0, 0, 0), (0, 0, 1.5), (0.03, 0, 3.6), (0, 0.02, 5.2)], [0.22, 0.17, 0.1, 0.03], 12), bark(dark=True), solid=True)
    tones = [leaves("#1C3220", "needle_a"), leaves("#26402A", "needle_b"), leaves("#304C30", "needle_c")]
    tiers = 7
    for i in range(tiers):
        z = 0.8 + i * 0.62
        r = 1.5 * (1 - i / (tiers + 0.4)) + 0.2
        h = 0.95
        # Dark cone underneath, then needle sprays hanging down from it.
        cone = bmesh.new()
        bmesh.ops.create_cone(cone, cap_ends=True, segments=14, radius1=r * 0.85, radius2=0.05, depth=h)
        p.add(cone, tones[0], smooth=True, matrix=T(0, 0, z + h / 2 - 0.15))
        by = [bmesh.new() for _ in tones]
        for _ in range(int(900 * r)):
            a = rng.uniform(0, math.tau); t = rng.uniform(0.15, 1.0)  # t: from the tip (0) to the rim (1)
            rr = r * t * rng.uniform(0.85, 1.08)
            pos = Vector((math.cos(a) * rr, math.sin(a) * rr, z - 0.15 + h * (1 - t)))
            out = Vector((math.cos(a), math.sin(a), -0.55 - rng.uniform(0, 0.5))).normalized()
            side = Vector((-math.sin(a), math.cos(a), 0)) * rng.uniform(-0.4, 0.4)
            L = rng.uniform(0.12, 0.22)
            w = (out.cross(Vector((0, 0, 1))).normalized() + side) * 0.025
            bm = by[rng.randrange(len(tones))]
            q = [bm.verts.new(pos - w), bm.verts.new(pos + w), bm.verts.new(pos + out * L + w * 0.3), bm.verts.new(pos + out * L - w * 0.3)]
            bm.faces.new(q)
        for bm, m in zip(by, tones):
            p.add(bm, m, smooth=False)
    return p.build()


def dead_tree(seed=3, burnt=False):
    rng = random.Random(seed)
    p = Prop("dead")
    m = charred() if burnt else bark(dark=True)
    trunk = wander((0, 0, 0), (0, 0, 1), 2.8, 6, rng, 0.18)
    p.add(tube(trunk, [0.27, 0.22, 0.18, 0.15, 0.11, 0.08, 0.04], 12), m, solid=True)

    def branch(base, d, length, r, depth):
        pts = wander(base, d, length, 3, rng, 0.35)
        p.add(tube(pts, [r, r * 0.75, r * 0.5, r * 0.25], 6), m)
        if depth > 0:
            for _ in range(2):
                nd = (Vector(d) + Vector((rng.uniform(-0.9, 0.9), rng.uniform(-0.9, 0.9), rng.uniform(0.2, 0.8)))).normalized()
                branch(pts[rng.randint(1, 3)], nd, length * 0.6, r * 0.55, depth - 1)

    for k in range(5):
        a = k / 5 * math.tau + rng.uniform(-0.4, 0.4)
        branch(trunk[rng.randint(2, 5)], (math.cos(a), math.sin(a), rng.uniform(0.3, 0.9)), rng.uniform(1.0, 1.6), 0.08, 2)
    return p.build()


def boulder(seed=4):
    p = Prop("boulder")
    p.add(blob(0.75, 4, 0.38, 1.2, seed, flat_bottom=-0.25, squash=(1.3, 1.0, 0.85)), rock(), solid=True, matrix=T(0, 0, 0.25))
    p.add(blob(0.42, 3, 0.4, 1.6, seed + 1, flat_bottom=-0.15, squash=(1.1, 1, 0.8)), rock("#6E6A64"), solid=True, matrix=T(0.85, -0.3, 0.12))
    return p.build()


def rock_cluster(seed=5):
    rng = random.Random(seed)
    p = Prop("rocks")
    for i in range(6):
        r = rng.uniform(0.18, 0.42)
        p.add(blob(r, 3, 0.42, 1.8, seed + i, flat_bottom=-r * 0.4, squash=(1.2, 1, 0.75)), rock("#7E786E" if i % 2 else "#6A655E"),
              solid=True, matrix=T(rng.uniform(-0.7, 0.7), rng.uniform(-0.45, 0.45), r * 0.4))
    return p.build()


def bush(seed=6):
    rng = random.Random(seed)
    p = Prop("bush")
    tones = [leaves("#2A4220", "bush_a"), leaves("#3A5226", "bush_b"), leaves("#4A622C", "bush_c")]
    for i in range(3):
        c = (rng.uniform(-0.35, 0.35), rng.uniform(-0.25, 0.25), 0.45)
        canopy(p, c, rng.uniform(0.42, 0.55), rng, tones, clumps=40, squash=0.85, core_mat=leaves("#1A2C12", "bush_core"))
    p.add(tube([(0, 0, 0), (0, 0, 0.3)], [0.06, 0.05], 6), bark(), solid=True)
    return p.build()


def tree_stump(seed=7):
    rng = random.Random(seed)
    p = Prop("stump")
    p.add(tube([(0, 0, 0), (0, 0, 0.3), (0.02, 0, 0.55)], [0.42, 0.36, 0.34], 16), bark(), solid=True)
    top = bmesh.new(); bmesh.ops.create_circle(top, cap_ends=True, segments=16, radius=0.33)
    p.add(top, A.pale_wood(), solid=True, matrix=T(0.02, 0, 0.56))
    for k in range(4):
        a = k / 4 * math.tau + rng.uniform(-0.3, 0.3)
        p.add(tube([(math.cos(a) * 0.2, math.sin(a) * 0.2, 0.2), (math.cos(a) * 0.7, math.sin(a) * 0.7, 0.0)], [0.12, 0.04], 8), bark(), solid=True)
    return p.build()


def fallen_log(seed=8):
    rng = random.Random(seed)
    p = Prop("log")
    pts = [(-1.4, 0, 0.3), (-0.5, 0.05, 0.32), (0.5, -0.04, 0.3), (1.4, 0.02, 0.28)]
    p.add(tube(pts, [0.3, 0.29, 0.28, 0.26], 14), bark(), solid=True)
    for x, r in ((-1.4, 0.29), (1.4, 0.25)):
        e = bmesh.new(); bmesh.ops.create_circle(e, cap_ends=True, segments=14, radius=r)
        p.add(e, A.pale_wood(), solid=True, matrix=T(x, 0, 0.3) @ R(90, "Y"))
    p.add(blob(0.3, 3, 0.3, 2.5, seed, squash=(2.4, 1, 0.3)), moss(), matrix=T(0.2, 0, 0.56))
    return p.build()


def graves(seed=9):
    rng = random.Random(seed)
    p = Prop("graves")
    for i, x in enumerate((-0.9, 0.0, 0.9)):
        # Earth mound, then a stone with a rounded head or a wooden cross.
        p.add(blob(0.45, 3, 0.15, 2, seed + i, flat_bottom=0, squash=(1.0, 1.8, 0.35)), earth(), matrix=T(x, 0.35, 0))
        lean = rng.uniform(-8, 8)
        if i != 1:
            st = A.slab([(-0.24, 0), (0.24, 0), (0.24, 0.6), (0.17, 0.74), (0, 0.79), (-0.17, 0.74), (-0.24, 0.6)], 0.09, bevel=0.01)
            p.add(st, rock("#8A857C"), solid=True, smooth=False, matrix=T(x, -0.15, 0) @ R(lean, "Y"))
        else:
            post = A.box(0.08, 0.08, 1.0); cross = A.box(0.5, 0.07, 0.08)
            p.add(post, A.wood(), solid=True, smooth=False, matrix=T(x, -0.15, 0.5) @ R(lean, "Y"))
            p.add(cross, A.wood(), solid=True, smooth=False, matrix=T(x, -0.15, 0.72) @ R(lean, "Y"))
    return p.build()


def brazier(seed=10):
    p = Prop("brazier")
    iron = A.iron()
    for k in range(3):
        a = k / 3 * math.tau
        p.add(tube([(math.cos(a) * 0.38, math.sin(a) * 0.38, 0), (math.cos(a) * 0.18, math.sin(a) * 0.18, 0.75), (math.cos(a) * 0.3, math.sin(a) * 0.3, 0.95)], [0.03, 0.03, 0.025], 6), iron, solid=True)
    bowl = A.lathe([(0.8, 0.0), (0.82, 0.22), (0.95, 0.38), (1.12, 0.46), (1.14, 0.44)], seg=20)
    p.add(bowl, iron, solid=True, matrix=R(-90, "Y"))
    coals = blob(0.36, 3, 0.25, 4, seed, squash=(1, 1, 0.35))
    p.add(coals, glow("#E04A10", 3.0), matrix=T(0, 0, 1.08))
    for k, (dx, dy, h) in enumerate(((0, 0, 0.5), (0.12, 0.05, 0.36), (-0.1, -0.06, 0.32), (0.02, -0.12, 0.28))):
        p.add(blob(0.13, 2, 0.4, 3, seed + k, squash=(1, 1, h / 0.13 * 0.6)), glow("#FF6A18", 3.0 if k else 3.5), matrix=T(dx, dy, 1.2 + h * 0.45))
    return p.build()


def torch_post(seed=11):
    p = Prop("torch")
    p.add(tube([(0, 0, 0), (0, 0, 1.9)], [0.06, 0.05], 8), A.wood(), solid=True)
    cage = A.lathe([(1.82, 0.04), (1.9, 0.11), (2.1, 0.12), (2.12, 0.1)], seg=8)
    p.add(cage, A.iron(), solid=True, matrix=R(-90, "Y"))
    p.add(blob(0.11, 2, 0.4, 3, seed, squash=(1, 1, 2.2)), glow("#FF8A2A", 4.5), matrix=T(0, 0, 2.25))
    p.add(blob(0.09, 2, 0.2, 3, seed + 1), glow("#E04A10", 3.0), matrix=T(0, 0, 2.1))
    return p.build()


def tent(seed=12):
    p = Prop("tent")
    bm = bmesh.new()
    n = 10
    ring = []
    for k in range(n):
        a = k / n * math.tau
        ring.append(bm.verts.new((math.cos(a) * 1.5, math.sin(a) * 1.5, 0.0)))
    mid = [bm.verts.new((math.cos(k / n * math.tau) * 1.25, math.sin(k / n * math.tau) * 1.25, 1.1)) for k in range(n)]
    top = bm.verts.new((0, 0, 2.6))
    for k in range(n):
        bm.faces.new((ring[k], ring[(k + 1) % n], mid[(k + 1) % n], mid[k]))
        bm.faces.new((mid[k], mid[(k + 1) % n], top))
    bmesh.ops.subdivide_edges(bm, edges=bm.edges[:], cuts=2)
    for v in bm.verts:
        if 0.05 < v.co.z < 2.5: v.co += v.co.normalized() * 0.04 * noise.noise(v.co * 2)
    p.add(bm, stripe_cloth(), solid=True, smooth=True)
    door = A.slab([(-0.35, 0), (0.35, 0), (0, 1.3)], 0.02)
    p.add(door, A.dark(), smooth=False, matrix=T(0, -1.53, 0))
    p.add(tube([(0, 0, 2.5), (0, 0, 3.1)], [0.03, 0.02], 6), A.wood())
    pen = A.slab([(0, 0), (0.5, 0.1), (0, 0.2)], 0.01)
    p.add(pen, A.red_cloth(), smooth=False, matrix=T(0.02, 0, 2.9))
    return p.build()


def banner(seed=13):
    p = Prop("banner")
    p.add(tube([(0, 0, 0), (0, 0, 3.6)], [0.05, 0.04], 8), A.wood(), solid=True)
    p.add(tube([(-0.6, 0, 3.4), (0.6, 0, 3.4)], [0.03, 0.03], 6), A.wood())
    # The cloth hangs from the crossbar with a gentle wave; a red cross on the white field.
    bm = bmesh.new()
    W, H, nx, nz = 1.1, 1.8, 10, 14
    vs = [[bm.verts.new((-W / 2 + i / nx * W, 0.07 * math.sin(i / nx * 5 + j * 0.3), 3.38 - j / nz * H)) for i in range(nx + 1)] for j in range(nz + 1)]
    for j in range(nz):
        for i in range(nx):
            bm.faces.new((vs[j][i], vs[j][i + 1], vs[j + 1][i + 1], vs[j + 1][i]))
    p.add(bm, A.white_paint())
    for (x0, x1, z0, z1) in ((-0.1, 0.1, 1.75, 3.25), (-0.45, 0.45, 2.55, 2.75)):
        c = bmesh.new()
        q = [c.verts.new((x0, -0.075 + 0.07 * math.sin((x0 + W / 2) / W * 5), z0)), c.verts.new((x1, -0.075 + 0.07 * math.sin((x1 + W / 2) / W * 5), z0)),
             c.verts.new((x1, -0.075 + 0.07 * math.sin((x1 + W / 2) / W * 5), z1)), c.verts.new((x0, -0.075 + 0.07 * math.sin((x0 + W / 2) / W * 5), z1))]
        c.faces.new(q)
        p.add(c, A.red_paint(), smooth=False)
    return p.build()


def market_stall(seed=14):
    rng = random.Random(seed)
    p = Prop("stall")
    for x in (-1.1, 1.1):
        for y in (-0.55, 0.55):
            p.add(tube([(x, y, 0), (x, y, 2.1 if y > 0 else 1.8)], [0.05, 0.045], 6), A.wood(), solid=True)
    p.add(A.box(2.4, 1.2, 0.08), A.pale_wood(), solid=True, smooth=False, matrix=T(0, 0, 0.85))
    p.add(A.box(2.3, 0.05, 0.75), A.wood(), solid=True, smooth=False, matrix=T(0, -0.58, 0.45))
    aw = bmesh.new()
    q = [aw.verts.new(v) for v in ((-1.3, -0.95, 1.7), (1.3, -0.95, 1.7), (1.3, 0.65, 2.2), (-1.3, 0.65, 2.2))]
    aw.faces.new(q)
    bmesh.ops.subdivide_edges(aw, edges=aw.edges[:], cuts=6)
    p.add(aw, stripe_cloth(), smooth=False)
    goods = [A.mat("apple", "#8A2A18", rough=0.5), A.mat("pear", "#8A8A30", rough=0.5), A.mat("cabbage", "#4E6A2A", rough=0.8), A.parchment()]
    for i in range(10):
        x = -0.95 + i * 0.21
        r = rng.uniform(0.08, 0.12)
        p.add(blob(r, 2, 0.15, 3, seed + i), goods[i % 4], matrix=T(x, rng.uniform(-0.3, 0.2), 0.89 + r))
    for x in (-0.7, 0.6):
        p.add(A.box(0.45, 0.4, 0.35), A.wood(), smooth=False, matrix=T(x, 0.2, 1.06))
    return p.build()


def grass_tuft(seed=15, dry=False, flowers=False):
    rng = random.Random(seed)
    p = Prop("grass")
    m = dry_grass() if dry else grass()
    bm = bmesh.new()
    for i in range(70):
        x, y = rng.gauss(0, 0.3), rng.gauss(0, 0.2)
        h = rng.uniform(0.25, 0.55)
        lean = Vector((rng.uniform(-0.15, 0.15), rng.uniform(-0.1, 0.1), 0))
        w = 0.025
        a = rng.uniform(0, math.pi)
        dx, dy = math.cos(a) * w, math.sin(a) * w
        v0 = bm.verts.new((x - dx, y - dy, 0)); v1 = bm.verts.new((x + dx, y + dy, 0))
        v2 = bm.verts.new((x + lean.x, y + lean.y, h))
        bm.faces.new((v0, v1, v2))
    p.add(bm, m, smooth=False)
    if flowers:
        cols = [A.mat("flower_w", "#E8E4D8", rough=0.6), A.mat("flower_y", "#D8B830", rough=0.6), A.mat("flower_r", "#9A2A3A", rough=0.6)]
        for i in range(9):
            p.add(blob(0.035, 1, 0.1, 3, seed + i), cols[i % 3], matrix=T(rng.gauss(0, 0.28), rng.gauss(0, 0.18), rng.uniform(0.3, 0.5)))
    return p.build()


# name: (builder, real height in metres for the frame, light or None)
FIRE_LIGHT = {"colour": "#FF9A40", "radius": 340, "intensity": 1.45, "flicker": 0.85, "height": 1.35, "flame": True, "embers": 4}
TORCH_LIGHT = {"colour": "#FFA850", "radius": 280, "intensity": 1.25, "flicker": 0.8, "height": 2.2, "flame": True, "embers": 2}
SCENERY = {
    "oak_tree": (lambda: oak_tree(1), 5.2, None),
    "oak_tree_b": (lambda: oak_tree(21), 5.0, None),
    "pine_tree": (lambda: pine_tree(2), 5.6, None),
    "dead_tree": (lambda: dead_tree(3), 4.6, None),
    "burnt_tree": (lambda: dead_tree(31, burnt=True), 4.4, None),
    "boulder": (lambda: boulder(4), 1.5, None),
    "rock_cluster": (lambda: rock_cluster(5), 0.9, None),
    "bush": (lambda: bush(6), 1.2, None),
    "tree_stump": (lambda: tree_stump(7), 0.7, None),
    "fallen_log": (lambda: fallen_log(8), 0.7, None),
    "graves": (lambda: graves(9), 1.1, None),
    "brazier": (lambda: brazier(10), 1.7, FIRE_LIGHT),
    "torch_post": (lambda: torch_post(11), 2.5, TORCH_LIGHT),
    "tent": (lambda: tent(12), 3.2, None),
    "banner": (lambda: banner(13), 3.7, None),
    "market_stall": (lambda: market_stall(14), 2.3, None),
    "grass_tuft": (lambda: grass_tuft(15), 0.6, None),
    "grass_flowers": (lambda: grass_tuft(16, flowers=True), 0.6, None),
    "dry_grass": (lambda: grass_tuft(17, dry=True), 0.6, None),
}


# Footprints set by hand: a bush is all leaves, but heroes still shouldn't walk through its middle.
FOOTPRINT = {"bush": [26.0, 11.0]}


def bounds(objs):
    bpy.context.view_layer.update()
    pts = [o.matrix_world @ v.co for o in objs for v in o.data.vertices]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    return lo, hi


def render(name, samples, out_dir, render_images=True):
    build, height, light = SCENERY[name]
    F.reset_scene()
    F.setup_scene(samples, False, 256, 4.0)
    objs = build()
    lo, hi = bounds(objs)
    turn = bpy.data.objects.new("turn", None); bpy.context.scene.collection.objects.link(turn)
    for o in objs:
        o.parent = turn
        o.location = (-(lo.x + hi.x) / 2, -(lo.y + hi.y) / 2, -lo.z)
    size = hi - lo
    foot = max(size.x, size.y)
    span = max(1.2, height * 1.45, foot * 1.25)
    span = math.ceil(span * 4) / 4
    px = int(round(span * P.PX_PER_M / 8) * 8)
    sc = bpy.context.scene
    sc.camera.data.ortho_scale = span
    sc.render.resolution_x = sc.render.resolution_y = px
    solid = [o for o in objs if not o.get("soft")]
    entries = {}
    for v, yaw in P.VIEWS.items():
        rel = f"sprites/prop_{name}_{v}.png"
        turn.rotation_euler = (0, 0, math.radians(yaw)); bpy.context.view_layer.update()
        if render_images:
            sc.render.filepath = os.path.join(out_dir, rel)
            bpy.ops.render.render(write_still=True)
        e = {"sprite": rel, "metres": span, "anchorX": 0.5, "anchorY": F.ANCHOR_Y}
        if name in FOOTPRINT: e["footprint"] = FOOTPRINT[name]
        elif solid:
            turn.rotation_euler = (0, 0, 0); bpy.context.view_layer.update()
            e["footprint"] = P.footprint(solid, yaw)
        if light: e["light"] = dict(light)
        entries[f"{name}_{v}"] = e
    print(f"[scenery] {name}: {px}px, {span} m, footprint {entries[name + '_1'].get('footprint')}", flush=True)
    return entries


def main(argv):
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--samples", type=int, default=48)
    ap.add_argument("--names", default="")
    ap.add_argument("--no-render", action="store_true")
    a = ap.parse_args(argv)
    want = [n for n in a.names.split(",") if n] or list(SCENERY)
    for n in want:
        e = render(n, a.samples, os.path.join(ROOT, "assets"), not a.no_render)
        props = json.load(open(PROPS_JSON))
        props.update(e)
        with open(PROPS_JSON, "w", newline="\n") as f:
            json.dump(dict(sorted(props.items())), f, indent=1, ensure_ascii=False); f.write("\n")


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:])
