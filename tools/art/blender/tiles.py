"""
Templar Knights ground tiles, rendered from 3D geometry in Blender (Cycles), seamless by construction.

    blender -b --python tools/art/blender/tiles.py -- [--names act1_road,act2_crypt] [--size 512] [--samples 96] [--gpu]
    python  tools/art/blender/tiles.py --names act1_road            (bpy module)

How the seams disappear: every tile is one unit square. The ground texture is periodic noise built from integer
frequencies (so it wraps exactly), and every stone, plank, bone or leaf is placed on a periodic layout and copied
into the eight neighbouring squares. The camera looks straight down at the centre square only, so shapes and
shadows that cross an edge continue on the other side. Output: assets/tiles/<name>.png (opaque RGB).
Everything is generated here: no photos or third-party textures.
"""
from __future__ import annotations

import math
import os
import random
import sys

import bpy
import bmesh
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
OUT = os.path.join(ROOT, "assets", "tiles")

# ----------------------------------------------------------------------------------------------- utilities
def lin(hexstr):
    h = hexstr.lstrip("#")
    c = [int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    return [x / 12.92 if x <= 0.04045 else ((x + 0.055) / 1.055) ** 2.4 for x in c]

def mix(a, b, t):
    return [x + (y - x) * t for x, y in zip(a, b)]

def periodic_noise(n, seed, octaves=6, base=2, falloff=0.55):
    """Seamless fractal noise in [0, 1] on an n x n grid (sum of integer-frequency waves)."""
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:n, 0:n] / n
    h = np.zeros((n, n))
    amp = 1.0
    for o in range(octaves):
        f = base * 2 ** o
        for _ in range(10):
            kx, ky = rng.integers(-f, f + 1, 2)
            if kx == 0 and ky == 0: continue
            h += amp * np.cos(2 * np.pi * (kx * x + ky * y) + rng.uniform(0, 2 * np.pi))
        amp *= falloff
    h -= h.min(); h /= max(1e-6, h.max())
    return h

def make_image(name, rgb_arr):
    """rgb_arr: (n, n, 3) linear floats, row 0 = top. Returns a float image Blender can sample."""
    n = rgb_arr.shape[0]
    img = bpy.data.images.new(name, n, n, alpha=False, float_buffer=True)
    img.colorspace_settings.name = "Non-Color"  # before writing: changing it later regenerates the buffer
    rgba = np.ones((n, n, 4), dtype=np.float32)
    rgba[..., :3] = rgb_arr
    img.pixels.foreach_set(np.ascontiguousarray(rgba[::-1]).ravel())
    img.update()
    img.pack()
    return img

_mats = {}

def material(name, color, rough=0.8, metal=0.0, emit=None, power=0.0, spec=0.5, coat=0.0, bump=0.0, bump_scale=40.0):
    key = (name, tuple(color), rough, metal, tuple(emit) if emit else None, power, bump)
    if key in _mats: return _mats[key]
    m = bpy.data.materials.new(name); m.use_nodes = True
    nt = m.node_tree; b = nt.nodes.get("Principled BSDF")
    b.inputs["Base Color"].default_value = (*color, 1)
    b.inputs["Roughness"].default_value = rough
    b.inputs["Metallic"].default_value = metal
    if "Coat Weight" in b.inputs: b.inputs["Coat Weight"].default_value = coat
    if emit:
        b.inputs["Emission Color"].default_value = (*emit, 1); b.inputs["Emission Strength"].default_value = power
    if bump:
        # Object-space noise is fine here: per-object detail never crosses a tile edge as a pattern.
        tc = nt.nodes.new("ShaderNodeTexCoord"); nz = nt.nodes.new("ShaderNodeTexNoise")
        nz.inputs["Scale"].default_value = bump_scale; nz.inputs["Detail"].default_value = 8
        bp = nt.nodes.new("ShaderNodeBump"); bp.inputs["Strength"].default_value = bump
        nt.links.new(tc.outputs["Object"], nz.inputs["Vector"]); nt.links.new(nz.outputs["Fac"], bp.inputs["Height"])
        nt.links.new(bp.outputs["Normal"], b.inputs["Normal"])
        # Slight colour variation from the same noise.
        mr = nt.nodes.new("ShaderNodeMix"); mr.data_type = "RGBA"
        mr.inputs[6].default_value = (*[c * 0.8 for c in color], 1); mr.inputs[7].default_value = (*[min(1, c * 1.15) for c in color], 1)
        nt.links.new(nz.outputs["Fac"], mr.inputs[0]); nt.links.new(mr.outputs[2], b.inputs["Base Color"])
    _mats[key] = m
    return m

def link(ob):
    bpy.context.scene.collection.objects.link(ob)
    return ob

def tiled(make, x, y, z=0.0, rz=0.0, rx=0.0, ry=0.0, s=(1, 1, 1)):
    """Places an object made by make() at (x, y) in the unit tile and copies it into the 8 neighbours."""
    base = make()
    base.location = (x % 1.0 - 0.5, y % 1.0 - 0.5, z)
    base.rotation_euler = (rx, ry, rz)
    base.scale = s
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            if dx == 0 and dy == 0: continue
            c = base.copy(); c.data = base.data
            c.location = (base.location.x + dx, base.location.y + dy, z)
            link(c)
    return base

def mesh_box(name, sx, sy, sz, mat, bevel=0.004):
    bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1)
    for v in bm.verts: v.co.x *= sx; v.co.y *= sy; v.co.z *= sz
    if bevel:
        bmesh.ops.bevel(bm, geom=list(bm.edges), offset=bevel, segments=2, affect="EDGES")
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    for p in me.polygons: p.use_smooth = False  # flat: smooth normals on a box shade the whole top face as a gradient
    ob = bpy.data.objects.new(name, me); ob.data.materials.append(mat)
    return link(ob)

def mesh_blob(name, r, mat, sx=1, sy=1, sz=1, rough_amt=0.15, seed=0, seg=14):
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=seg, v_segments=max(6, seg // 2), radius=r)
    rng = random.Random(seed)
    for v in bm.verts:
        k = 1 + rng.uniform(-rough_amt, rough_amt)
        v.co.x *= sx * k; v.co.y *= sy * k; v.co.z *= sz * k
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    for p in me.polygons: p.use_smooth = True
    ob = bpy.data.objects.new(name, me); ob.data.materials.append(mat)
    return link(ob)

def mesh_cyl(name, r, h, mat, seg=10):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=seg, radius1=r, radius2=r, depth=h)
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    for p in me.polygons: p.use_smooth = True
    ob = bpy.data.objects.new(name, me); ob.data.materials.append(mat)
    return link(ob)

# ----------------------------------------------------------------------------------------------- ground
def ground(seed, c1, c2, c3=None, rough=0.9, height=0.02, res=512, spec=0.3, wet=None):
    """A displaced 3x3 ground plane with a seamless coloured texture. wet=(colour, level) floods the low areas."""
    h = periodic_noise(res, seed)
    detail = periodic_noise(res, seed + 7, octaves=4, base=16, falloff=0.6)
    a, b = np.array(lin(c1)), np.array(lin(c2))
    t = np.clip(h * 0.8 + detail * 0.35 - 0.1, 0, 1)[..., None]
    col = a * (1 - t) + b * t
    if c3:
        c = np.array(lin(c3)); m = np.clip((detail - 0.62) * 5, 0, 1)[..., None]
        col = col * (1 - m) + c * m
    rgh = np.full((res, res), rough)
    if wet:
        wc, level = wet
        m = np.clip((level - h) * 12, 0, 1)
        col = col * (1 - m[..., None]) + np.array(lin(wc)) * m[..., None]
        rgh = rgh * (1 - m) + 0.06 * m
    img = make_image(f"g{seed}", col)
    himg = make_image(f"h{seed}", np.repeat((h * 0.75 + detail * 0.25)[..., None], 3, axis=2))
    rimg = make_image(f"r{seed}", np.repeat(rgh[..., None], 3, axis=2))
    # Plane covering the 3x3 block, UVs 0..3 so the texture repeats exactly once per tile.
    me = bpy.data.meshes.new("ground")
    bm = bmesh.new()
    bmesh.ops.create_grid(bm, x_segments=180, y_segments=180, size=1.5)
    uv = bm.loops.layers.uv.new()
    for f in bm.faces:
        for l in f.loops:
            l[uv].uv = ((l.vert.co.x + 1.5), (l.vert.co.y + 1.5))
    bm.to_mesh(me); bm.free()
    ob = link(bpy.data.objects.new("ground", me))
    m = bpy.data.materials.new("ground"); m.use_nodes = True
    nt = m.node_tree; bs = nt.nodes.get("Principled BSDF")
    def tex(image, colour=True):
        t = nt.nodes.new("ShaderNodeTexImage"); t.image = image; t.extension = "REPEAT"; t.interpolation = "Cubic"
        return t
    tc, th, tr = tex(img), tex(himg), tex(rimg)
    nt.links.new(tc.outputs[0], bs.inputs["Base Color"])
    nt.links.new(tr.outputs[0], bs.inputs["Roughness"])
    bump = nt.nodes.new("ShaderNodeBump"); bump.inputs["Strength"].default_value = 0.6; bump.inputs["Distance"].default_value = 0.01
    nt.links.new(th.outputs[0], bump.inputs["Height"]); nt.links.new(bump.outputs[0], bs.inputs["Normal"])
    ob.data.materials.append(m)
    # Real displacement for relief and shadows.
    tx = bpy.data.textures.new("disp", "IMAGE"); tx.image = himg; tx.extension = "REPEAT"
    d = ob.modifiers.new("disp", "DISPLACE"); d.texture = tx; d.texture_coords = "UV"; d.strength = height; d.mid_level = 0.5
    for p in me.polygons: p.use_smooth = True
    return ob

# ----------------------------------------------------------------------------------------------- features
def flagstones(rng, rows, cols, colours, gap=0.012, height=0.025, tilt=0.03, irregular=0.35, grout_low=True, rough=0.85):
    """Rows of rectangular slabs with random widths; widths per row sum to 1 so the pattern wraps."""
    mats = [material(f"flag{i}", lin(c), rough=rough, bump=0.25, bump_scale=60) for i, c in enumerate(colours)]
    rh = 1 / rows
    for r in range(rows):
        widths = [1 + rng.uniform(-irregular, irregular) for _ in range(cols)]
        tot = sum(widths); widths = [w / tot for w in widths]
        x = rng.uniform(0, 1)
        for w in widths:
            m = rng.choice(mats)
            sx, sy = w - gap, rh - gap
            hz = height * rng.uniform(0.8, 1.2)
            tiled(lambda: mesh_box("flag", sx, sy, hz, m, bevel=0.006), x + w / 2, r * rh + rh / 2, hz / 2 - 0.004,
                  rz=rng.uniform(-0.01, 0.01), rx=rng.uniform(-tilt, tilt), ry=rng.uniform(-tilt, tilt))
            x += w

def cobbles(rng, n, colours, size=0.9, height=0.03):
    mats = [material(f"cob{i}", lin(c), rough=0.75, bump=0.2) for i, c in enumerate(colours)]
    cell = 1 / n
    for i in range(n):
        for j in range(n):
            x = (i + 0.5 + (0.5 if j % 2 else 0)) * cell + rng.uniform(-0.12, 0.12) * cell
            y = (j + 0.5) * cell + rng.uniform(-0.12, 0.12) * cell
            r = cell * 0.5 * size * rng.uniform(0.85, 1.05)
            m = rng.choice(mats); sd = rng.randrange(10**6)
            tiled(lambda: mesh_blob("cob", r, m, 1.0, rng.uniform(0.8, 1.0), height / r, 0.12, sd), x, y, 0.0, rz=rng.uniform(0, 6.28))

def planks(rng, n, colours, gap=0.006, along_x=True):
    mats = [material(f"plank{i}", lin(c), rough=0.7, bump=0.3, bump_scale=8) for i, c in enumerate(colours)]
    w = 1 / n
    for i in range(n):
        # Each board row is split into two boards at a random point (wraps across the tile edge).
        cut = rng.uniform(0, 1)
        for k, (a, b) in enumerate(((cut, cut + 0.55), (cut + 0.55, cut + 1.0))):
            m = rng.choice(mats); L = b - a - gap
            if along_x:
                tiled(lambda: mesh_box("plank", L, w - gap, 0.02, m, 0.003), (a + b) / 2, i * w + w / 2, 0.006)
            else:
                tiled(lambda: mesh_box("plank", w - gap, L, 0.02, m, 0.003), i * w + w / 2, (a + b) / 2, 0.006)
        for k in range(2):  # nail heads
            nm = material("nail", lin("#3A3A3A"), rough=0.4, metal=1.0)
            tiled(lambda: mesh_cyl("nail", 0.004, 0.004, nm, 6), cut + 0.01, i * w + w * (0.3 + 0.4 * k), 0.017)

def bricks(rng, rows, per_row, colours, gap=0.008):
    mats = [material(f"brick{i}", lin(c), rough=0.9, bump=0.35, bump_scale=70) for i, c in enumerate(colours)]
    rh, bw = 1 / rows, 1 / per_row
    for r in range(rows):
        off = (bw / 2) if r % 2 else 0
        for c in range(per_row):
            m = rng.choice(mats)
            tiled(lambda: mesh_box("brick", bw - gap, rh - gap, 0.02, m, 0.003), off + c * bw + bw / 2, r * rh + rh / 2, 0.006)

def scatter(rng, kind, count, colours=None, zone=(0, 1)):
    mats = [material(f"{kind}{i}", lin(c), rough=0.8, bump=0.2) for i, c in enumerate(colours or ["#888888"])]
    for _ in range(count):
        x, y = rng.uniform(0, 1), rng.uniform(*zone)
        m = rng.choice(mats); rz = rng.uniform(0, 6.28); sd = rng.randrange(10**6)
        if kind == "pebble":
            r = rng.uniform(0.006, 0.018)
            tiled(lambda: mesh_blob("pebble", r, m, 1, rng.uniform(0.7, 1), 0.6, 0.2, sd, 8), x, y, 0.004, rz=rz)
        elif kind == "rock":
            r = rng.uniform(0.025, 0.06)
            tiled(lambda: mesh_blob("rock", r, m, 1, rng.uniform(0.6, 1), 0.55, 0.3, sd, 10), x, y, 0.01, rz=rz)
        elif kind == "straw":
            L = rng.uniform(0.02, 0.045)
            tiled(lambda: mesh_cyl("straw", 0.0018, L, m, 5), x, y, 0.006, rx=math.pi / 2 + rng.uniform(-0.2, 0.2), rz=rz)
        elif kind == "stubble":
            L = rng.uniform(0.015, 0.03)
            tiled(lambda: mesh_cyl("stub", 0.0025, L, m, 5), x, y, L / 2, rx=rng.uniform(-0.3, 0.3), ry=rng.uniform(-0.3, 0.3))
        elif kind == "leaf":
            r = rng.uniform(0.012, 0.024)
            tiled(lambda: mesh_blob("leaf", r, m, 1.0, 0.55, 0.08, 0.1, sd, 8), x, y, 0.008 + rng.uniform(0, 0.006), rz=rz, rx=rng.uniform(-0.3, 0.3))
        elif kind == "bone":
            L = rng.uniform(0.04, 0.08)
            def mk():
                o = mesh_cyl("bone", 0.005, L, m, 8)
                for s in (-1, 1):
                    k = mesh_blob("knob", 0.009, m, 1.2, 1, 1, 0.05, sd, 8); k.parent = o; k.location = (0, 0, s * L / 2)
                return o
            tiled(mk, x, y, 0.008, rx=math.pi / 2, rz=rz)
        elif kind == "skull":
            tiled(lambda: mesh_blob("skull", 0.022, m, 0.9, 1.1, 0.85, 0.05, sd, 12), x, y, 0.016, rz=rz)
        elif kind == "plank":
            L = rng.uniform(0.08, 0.16)
            tiled(lambda: mesh_box("charred", L, 0.022, 0.01, m, 0.002), x, y, 0.008, rz=rz)
        elif kind == "ember":
            em = material("ember", lin("#200800"), emit=lin("#FF6A1A"), power=rng.uniform(4, 12))
            r = rng.uniform(0.003, 0.007)
            tiled(lambda: mesh_blob("ember", r, em, 1, 1, 0.6, 0.2, sd, 6), x, y, 0.004)
        elif kind == "candle":
            wax = material("wax", lin("#E8DFC4"), rough=0.4)
            flame = material("flame", lin("#FFB050"), emit=lin("#FFB050"), power=25)
            def mk():
                o = mesh_cyl("candle", 0.008, 0.03, wax, 10)
                f = mesh_blob("flame", 0.004, flame, 1, 1, 2.2, 0.0, sd, 6); f.parent = o; f.location = (0, 0, 0.022)
                p = mesh_blob("pool", 0.016, wax, 1, 1, 0.12, 0.2, sd, 10); p.parent = o; p.location = (0, 0, -0.015)
                return o
            tiled(mk, x, y, 0.017)
        elif kind == "snow":
            r = rng.uniform(0.03, 0.08)
            sm = material("snow", lin("#E8ECF2"), rough=0.6, bump=0.3, bump_scale=120)
            tiled(lambda: mesh_blob("snow", r, sm, 1, rng.uniform(0.6, 1), 0.08, 0.25, sd, 14), x, y, 0.0, rz=rz)
        elif kind == "puddle":
            r = rng.uniform(0.06, 0.14)
            wm = material("water", lin("#1A2026"), rough=0.03, coat=1.0)
            tiled(lambda: mesh_blob("puddle", r, wm, 1, rng.uniform(0.5, 0.9), 0.01, 0.25, sd, 16), x, y, 0.012, rz=rz)
        elif kind == "moss":
            r = rng.uniform(0.02, 0.05)
            tiled(lambda: mesh_blob("moss", r, m, 1, rng.uniform(0.6, 1), 0.12, 0.35, sd, 10), x, y, 0.018, rz=rz)
        elif kind == "petal":
            r = rng.uniform(0.004, 0.008)
            tiled(lambda: mesh_blob("petal", r, m, 1, 0.7, 0.2, 0.1, sd, 6), x, y, 0.01, rz=rz)
        elif kind == "root":
            L = rng.uniform(0.12, 0.3)
            tiled(lambda: mesh_cyl("root", rng.uniform(0.006, 0.012), L, m, 8), x, y, 0.006, rx=math.pi / 2, rz=rz)
        elif kind == "grass":
            L = rng.uniform(0.012, 0.03)
            tiled(lambda: mesh_cyl("blade", 0.0018, L, m, 4), x, y, L / 2, rx=rng.uniform(-0.5, 0.5), ry=rng.uniform(-0.5, 0.5))

def rug(colour, trim, w=0.55, h=0.8):
    m = material("rug", lin(colour), rough=0.95, bump=0.4, bump_scale=200)
    t = material("rugtrim", lin(trim), rough=0.9, bump=0.3, bump_scale=200)
    tiled(lambda: mesh_box("rug", w, h, 0.004, m, 0.001), 0.5, 0.5, 0.018)
    tiled(lambda: mesh_box("rugt", w + 0.03, h + 0.03, 0.003, t, 0.001), 0.5, 0.5, 0.0165)

def furrows(rng, rows, colour):
    """Ploughed or trellised rows running across the tile (wraps)."""
    m = material("furrow", lin(colour), rough=0.95, bump=0.4)
    for r in range(rows):
        y = (r + 0.5) / rows
        tiled(lambda: mesh_blob("ridge", 0.5, m, 1.02, 0.06, 0.016, 0.02, r, 24), 0.5, y, 0.0)

# ----------------------------------------------------------------------------------------------- tile recipes
def T_fields(rng):
    ground(11, "#3A2C1E", "#4E3E2A", "#30261A", height=0.02, wet=("#241E18", 0.12))
    furrows(rng, 2, "#463626")
    scatter(rng, "stubble", 110, ["#9A7E46", "#8A7040", "#A88E58"])
    scatter(rng, "straw", 45, ["#A88E50", "#8A7448"])
    scatter(rng, "pebble", 24, ["#6A625A", "#5A524A"])

def T_burning(rng):
    ground(12, "#2A221C", "#4A3A2C", "#151210", height=0.02)
    scatter(rng, "plank", 18, ["#1A1612", "#2A2018"])
    scatter(rng, "ember", 40)
    scatter(rng, "pebble", 40, ["#3A3430", "#4A4038"])
    scatter(rng, "straw", 40, ["#3A2E20"])

def T_catacomb(rng):
    ground(13, "#24221E", "#2E2A26", height=0.01)
    flagstones(rng, 4, 3, ["#4A4640", "#3E3A36", "#55504A", "#45403A"], gap=0.014, height=0.03)
    scatter(rng, "bone", 10, ["#C8BEA4", "#B0A68C"])
    scatter(rng, "skull", 2, ["#CFC6AE"])
    scatter(rng, "pebble", 30, ["#3A3632"])

def T_charnel(rng):
    ground(14, "#2E261C", "#4A3C2A", "#1E1A14", height=0.035)
    scatter(rng, "bone", 45, ["#C8BEA4", "#B0A68C", "#9A9078"])
    scatter(rng, "skull", 8, ["#CFC6AE", "#B8AE96"])
    scatter(rng, "pebble", 40, ["#4A4038"])

def T_hall(rng):
    ground(15, "#2A1E14", "#3A2A1C", height=0.0)
    planks(rng, 8, ["#6A4A2E", "#5E4228", "#74502F", "#5A3E26"])
    scatter(rng, "straw", 30, ["#8A7448", "#A88E50"])

def T_drowned(rng):
    ground(16, "#1E2420", "#2E3830", height=0.01, wet=("#141A1C", 0.5))
    flagstones(rng, 3, 3, ["#3A443E", "#46504A", "#323A36"], gap=0.016, height=0.022, tilt=0.06)
    scatter(rng, "puddle", 6)
    scatter(rng, "moss", 26, ["#3E5A2E", "#4A6A34", "#2E4422"])

def T_forest1(rng):
    ground(17, "#2E2418", "#3E3220", "#1E1A12", height=0.035)
    scatter(rng, "root", 6, ["#4A3622", "#3A2A1A"])
    scatter(rng, "leaf", 420, ["#8A4A1E", "#A0602A", "#6A3A18", "#B07A2E", "#5A4A22", "#7A2E14"])
    scatter(rng, "moss", 12, ["#3E5A2E", "#4A6A34"])
    scatter(rng, "pebble", 20, ["#5A524A"])

def T_frost(rng):
    ground(18, "#3A3430", "#5A5450", "#C8D0DA", height=0.025)
    furrows(rng, 3, "#4A423A")
    scatter(rng, "snow", 12)
    scatter(rng, "stubble", 120, ["#7A6A50", "#8A7A5A"])
    scatter(rng, "pebble", 30, ["#6A6460"])

def T_preceptory(rng):
    ground(19, "#2A2620", "#3A342C", height=0.01)
    flagstones(rng, 4, 4, ["#6A6052", "#5E564A", "#766A5A", "#544C42"], gap=0.012)
    scatter(rng, "ember", 12)
    scatter(rng, "plank", 6, ["#1A1612"])
    scatter(rng, "pebble", 40, ["#3A3430", "#4A443C"])

def T_road(rng):
    ground(20, "#5A4A36", "#6E5C44", "#3E3224", height=0.03)
    furrows(rng, 2, "#4A3C2A")
    scatter(rng, "pebble", 160, ["#7A7064", "#8A8072", "#6A6054", "#5A5248"])
    scatter(rng, "grass", 200, ["#5A6A32", "#6A7A3A", "#4A5A2A"], zone=(0, 0.12))
    scatter(rng, "grass", 200, ["#5A6A32", "#6A7A3A", "#4A5A2A"], zone=(0.52, 0.64))

def T_quarry(rng):
    ground(21, "#5A564E", "#7A7468", "#3E3A34", height=0.05)
    scatter(rng, "rock", 30, ["#8A8478", "#7A7468", "#6A645A"])
    scatter(rng, "pebble", 200, ["#9A9488", "#7A7468", "#5A564E"])

def T_sanctuary(rng):
    ground(22, "#24201C", "#2E2A24", height=0.01)
    flagstones(rng, 3, 2, ["#4E4840", "#5A5248", "#463F38"], gap=0.012, height=0.028)
    scatter(rng, "candle", 3)
    scatter(rng, "pebble", 20, ["#3A3632"])

def T_cellar(rng):
    ground(23, "#2A2018", "#3A2C20", height=0.0)
    bricks(rng, 10, 5, ["#6A3E2A", "#5A3424", "#74462E", "#5E3A28"])
    scatter(rng, "straw", 60, ["#8A7448", "#6A5A38"])

def T_cobbles(rng):
    ground(24, "#2A2622", "#3A3530", height=0.01)
    cobbles(rng, 9, ["#6A645C", "#5A554E", "#7A7268", "#4E4A44"])

def T_crypt(rng):
    ground(25, "#22201E", "#2C2A26", height=0.01)
    flagstones(rng, 3, 3, ["#46443E", "#3C3A36", "#504C46"], gap=0.016, height=0.03)
    scatter(rng, "bone", 6, ["#B8AE96"])
    scatter(rng, "candle", 2)

def T_yard(rng):
    ground(26, "#5A4E3A", "#6E604A", "#3E3628", height=0.03)
    scatter(rng, "straw", 160, ["#C8AE6E", "#A88E50", "#8A7448"])
    scatter(rng, "pebble", 80, ["#7A7064", "#5A5248"])

def T_cave(rng):
    ground(27, "#2A2624", "#3A3430", "#1A1816", height=0.06)
    scatter(rng, "rock", 18, ["#4A4440", "#3A3632"])
    scatter(rng, "pebble", 120, ["#4A4440", "#5A544E"])
    scatter(rng, "puddle", 2)

def T_stone(rng):
    ground(28, "#2E2E30", "#3A3A3E", height=0.01)
    flagstones(rng, 5, 4, ["#5A5A5E", "#4E4E52", "#66666A"], gap=0.01, height=0.022, irregular=0.2)

def T_gardens(rng):
    ground(29, "#34442A", "#46582E", "#2A3420", height=0.02)
    scatter(rng, "grass", 1400, ["#4A6A2A", "#5A7A32", "#3E5A24", "#6A8A3A"])
    scatter(rng, "petal", 60, ["#E8E2D2", "#E6C14A", "#C04A5A"])
    scatter(rng, "pebble", 20, ["#8A8478"])

def T_forest3(rng):
    ground(30, "#2A2A1A", "#36361E", "#1E1E12", height=0.035)
    scatter(rng, "root", 8, ["#4A3622", "#3A2A1A"])
    scatter(rng, "leaf", 360, ["#4A5A22", "#5A6A2A", "#3A4A1A", "#6A5A22"])
    scatter(rng, "moss", 20, ["#3E5A2E", "#4A6A34"])
    scatter(rng, "grass", 300, ["#4A6A2A", "#3E5A24"])

def T_cloister(rng):
    ground(31, "#4A4438", "#5A5446", height=0.0)
    flagstones(rng, 4, 4, ["#9A9282", "#8A8272", "#A49C8C", "#7E7668"], gap=0.01, irregular=0.1)
    scatter(rng, "grass", 120, ["#4A6A2A", "#5A7A32"])

def T_court(rng):
    ground(32, "#5A5244", "#6A6050", height=0.0)
    flagstones(rng, 6, 6, ["#A09684", "#948A78", "#ACA290"], gap=0.008, irregular=0.05, height=0.02)

def T_cathedral3(rng):
    ground(33, "#24222A", "#2E2C34", height=0.0)
    flagstones(rng, 2, 2, ["#5A5864", "#3A3842"], gap=0.008, irregular=0.0, height=0.02, tilt=0.0)

def T_marble(rng):
    ground(34, "#8A8478", "#9A948A", height=0.0)
    flagstones(rng, 4, 4, ["#D8D2C4", "#6A3A3A"], gap=0.006, irregular=0.0, height=0.02, tilt=0.0, rough=0.45)

def T_camp(rng):
    ground(35, "#6A6460", "#8A8480", "#E0E4EA", height=0.03, wet=("#3A3430", 0.15))
    scatter(rng, "snow", 16)
    scatter(rng, "straw", 60, ["#8A7448"])
    scatter(rng, "pebble", 40, ["#5A544E"])

def T_square(rng):
    ground(36, "#2E2822", "#3A3228", height=0.01)
    cobbles(rng, 8, ["#6A5E50", "#5A5046", "#766A5A", "#4E463C"])
    scatter(rng, "straw", 30, ["#8A7448"])

def T_palace(rng):
    ground(37, "#5A4E3A", "#6A5E48", height=0.0)
    flagstones(rng, 3, 3, ["#B8AA88", "#8A7A5A"], gap=0.006, irregular=0.0, height=0.02, tilt=0.0, rough=0.45)

def T_cathedral4(rng):
    ground(38, "#2A2630", "#34303A", height=0.0)
    flagstones(rng, 3, 3, ["#4E4A56", "#5A5664", "#44404C"], gap=0.01, irregular=0.0, height=0.02, tilt=0.0)
    scatter(rng, "candle", 2)

def T_tower(rng):
    ground(39, "#24221E", "#302C28", "#14120E", height=0.015)
    flagstones(rng, 4, 3, ["#46423C", "#3A3632", "#504A44"], gap=0.016, height=0.03, tilt=0.05)
    scatter(rng, "straw", 50, ["#6A5A38", "#5A4A30"])
    scatter(rng, "puddle", 2)

TILES = {
    # Act I (own set)
    "act1_fields": T_fields, "act1_burning": T_burning, "act1_catacomb": T_catacomb, "act1_charnel": T_charnel,
    "act1_hall": T_hall, "act1_drowned": T_drowned, "act1_forest": T_forest1, "act1_frost": T_frost,
    "act1_preceptory": T_preceptory, "act1_road": T_road, "act1_quarry": T_quarry, "act1_sanctuary": T_sanctuary,
    "act1_cellar": T_cellar,
    # Acts II-IV
    "act2_cobbles": T_cobbles, "act2_crypt": T_crypt, "act2_yard": T_yard, "act2_cave": T_cave, "act2_stone": T_stone,
    "act3_gardens": T_gardens, "act3_forest": T_forest3, "act3_cloister": T_cloister, "act3_court": T_court,
    "act3_cathedral": T_cathedral3, "act3_marble": T_marble,
    "act4_camp": T_camp, "act4_square": T_square, "act4_palace": T_palace, "act4_cathedral": T_cathedral4, "act4_tower": T_tower,
    "ground_placeholder": T_road,
}

def render_tile(name, size, samples, gpu, out=OUT):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    _mats.clear()
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"; sc.cycles.samples = samples; sc.cycles.use_denoising = True
    sc.render.resolution_x = sc.render.resolution_y = size
    sc.render.image_settings.file_format = "PNG"; sc.render.image_settings.color_mode = "RGB"
    try: sc.view_settings.view_transform = "Standard"
    except Exception: pass
    if gpu:
        prefs = bpy.context.preferences.addons["cycles"].preferences
        for t in ("OPTIX", "CUDA", "HIP", "METAL"):
            try:
                prefs.compute_device_type = t; prefs.refresh_devices()
                if any(d.type == t for d in prefs.devices):
                    for d in prefs.devices: d.use = d.type == t
                    sc.cycles.device = "GPU"; break
            except Exception: continue
    w = bpy.data.worlds.new("w"); sc.world = w; w.use_nodes = True
    w.node_tree.nodes["Background"].inputs[0].default_value = (0.03, 0.03, 0.035, 1)
    cam = bpy.data.cameras.new("cam"); cam.type = "ORTHO"; cam.ortho_scale = 1.0
    co = link(bpy.data.objects.new("cam", cam)); co.location = (0, 0, 5); sc.camera = co
    # Same light family as the figures: warm key from the upper left, cool fill.
    for nm, e, col, rot, ang in (("key", 3.2, (1.0, 0.84, 0.66), (0.75, -0.55, 0.0), 0.08), ("fill", 0.6, (0.62, 0.74, 1.0), (0.9, 0.7, 0), 0.5)):
        l = bpy.data.lights.new(nm, "SUN"); l.energy = e; l.color = col; l.angle = ang
        o = link(bpy.data.objects.new(nm, l)); o.rotation_euler = rot
    rng = random.Random(name)
    TILES[name](rng)
    os.makedirs(out, exist_ok=True)
    sc.render.filepath = os.path.join(out, name + ".png")
    bpy.ops.render.render(write_still=True)

def main(argv):
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--names", default="")
    ap.add_argument("--size", type=int, default=512)
    ap.add_argument("--samples", type=int, default=96)
    ap.add_argument("--gpu", action="store_true")
    ap.add_argument("--out", default=OUT)
    a = ap.parse_args(argv)
    names = [n for n in a.names.split(",") if n] or list(TILES)
    for n in names:
        render_tile(n, a.size, a.samples, a.gpu, a.out)
        print("[tiles]", n)

if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:])
