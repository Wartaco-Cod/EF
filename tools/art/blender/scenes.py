"""
Templar Knights title screen and cutscene illustrations, staged as 3D scenes in Blender and rendered with Cycles.

    blender -b --python tools/art/blender/scenes.py -- [--names prologue,title] [--samples 128] [--gpu] [--scale 100]
    python  tools/art/blender/scenes.py --names prologue --scale 50          (bpy module, quick preview)

Each scene is built from the procedural set kit below (walls, vaults, buildings, tents, pyres, bars, shelves,
trees, fire, smoke, fog and sky) and populated with the same 3D figures as the game sprites (figures.py), posed
from their animations. Output: assets/ui/cutscene_<id>.png (1280x720) and assets/ui/title.png (1920x1080).
Everything is modelled here; there are no third-party models, photos or textures.
"""
from __future__ import annotations

import json
import math
import os
import random
import sys

import bpy
import bmesh
from mathutils import Euler, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import figures as F  # noqa: E402

ROOT = F.ROOT
OUT = os.path.join(ROOT, "assets", "ui")
LOOKS = json.load(open(F.LOOKS, encoding="utf-8"))

# ----------------------------------------------------------------------------------------------- basics
def link(ob):
    bpy.context.scene.collection.objects.link(ob)
    return ob

def rgb(h, k=1.0):
    return F.rgb(h, k)

def mat(name, color, rough=0.8, metal=0.0, emit=None, power=0.0, bump=0.0, bump_scale=8.0, kind="noise", alpha=1.0):
    m = bpy.data.materials.new(name); m.use_nodes = True
    nt = m.node_tree; b = nt.nodes.get("Principled BSDF")
    b.inputs["Base Color"].default_value = rgb(color)
    b.inputs["Roughness"].default_value = rough
    b.inputs["Metallic"].default_value = metal
    if alpha < 1: b.inputs["Alpha"].default_value = alpha
    if emit:
        b.inputs["Emission Color"].default_value = rgb(emit); b.inputs["Emission Strength"].default_value = power
    if bump:
        tc = nt.nodes.new("ShaderNodeTexCoord")
        if kind == "brick":
            tx = nt.nodes.new("ShaderNodeTexBrick"); tx.inputs["Scale"].default_value = bump_scale
            tx.inputs["Color1"].default_value = rgb(color); tx.inputs["Color2"].default_value = rgb(color, 0.82)
            tx.inputs["Mortar"].default_value = rgb(color, 0.45); tx.inputs["Mortar Size"].default_value = 0.012
            nt.links.new(tc.outputs["Object"], tx.inputs["Vector"])
            nz = nt.nodes.new("ShaderNodeTexNoise"); nz.inputs["Scale"].default_value = 30
            nt.links.new(tc.outputs["Object"], nz.inputs["Vector"])
            mix = nt.nodes.new("ShaderNodeMix"); mix.data_type = "RGBA"; mix.inputs[0].default_value = 0.25
            nt.links.new(tx.outputs["Color"], mix.inputs[6]); nt.links.new(nz.outputs["Color"], mix.inputs[7])
            nt.links.new(mix.outputs[2], b.inputs["Base Color"])
            bp = nt.nodes.new("ShaderNodeBump"); bp.inputs["Strength"].default_value = bump
            nt.links.new(tx.outputs["Fac"], bp.inputs["Height"]); nt.links.new(bp.outputs[0], b.inputs["Normal"])
        else:
            nz = nt.nodes.new("ShaderNodeTexNoise"); nz.inputs["Scale"].default_value = bump_scale; nz.inputs["Detail"].default_value = 10
            nt.links.new(tc.outputs["Object"], nz.inputs["Vector"])
            bp = nt.nodes.new("ShaderNodeBump"); bp.inputs["Strength"].default_value = bump
            nt.links.new(nz.outputs["Fac"], bp.inputs["Height"]); nt.links.new(bp.outputs[0], b.inputs["Normal"])
            mix = nt.nodes.new("ShaderNodeMix"); mix.data_type = "RGBA"
            mix.inputs[6].default_value = rgb(color, 0.75); mix.inputs[7].default_value = rgb(color, 1.15)
            nt.links.new(nz.outputs["Fac"], mix.inputs[0]); nt.links.new(mix.outputs[2], b.inputs["Base Color"])
    return m

def cube(name, size, loc, m, rot=(0, 0, 0), bevel=0.0):
    bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1)
    for v in bm.verts: v.co.x *= size[0]; v.co.y *= size[1]; v.co.z *= size[2]
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    ob = link(bpy.data.objects.new(name, me)); ob.location = loc; ob.rotation_euler = rot
    ob.data.materials.append(m)
    if bevel:
        md = ob.modifiers.new("b", "BEVEL"); md.width = bevel; md.segments = 2
    return ob

def cyl(name, r, h, loc, m, seg=24, r2=None, rot=(0, 0, 0), smooth=True):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=seg, radius1=r, radius2=r if r2 is None else r2, depth=h)
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    if smooth:
        for p in me.polygons: p.use_smooth = True
    ob = link(bpy.data.objects.new(name, me)); ob.location = loc; ob.rotation_euler = rot
    ob.data.materials.append(m)
    return ob

def ball(name, r, loc, m, scale=(1, 1, 1), seg=24):
    bm = bmesh.new(); bmesh.ops.create_uvsphere(bm, u_segments=seg, v_segments=seg // 2, radius=r)
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    for p in me.polygons: p.use_smooth = True
    ob = link(bpy.data.objects.new(name, me)); ob.location = loc; ob.scale = scale
    ob.data.materials.append(m)
    return ob

def prism_roof(name, w, d, h, loc, m, rot_z=0.0):
    me = bpy.data.meshes.new(name)
    v = [(-w / 2, -d / 2, 0), (w / 2, -d / 2, 0), (w / 2, d / 2, 0), (-w / 2, d / 2, 0), (0, -d / 2, h), (0, d / 2, h)]
    f = [(0, 1, 4), (3, 5, 2), (0, 4, 5, 3), (1, 2, 5, 4), (0, 3, 2, 1)]
    me.from_pydata(v, [], f)
    ob = link(bpy.data.objects.new(name, me)); ob.location = loc; ob.rotation_euler = (0, 0, rot_z)
    ob.data.materials.append(m)
    return ob

def light(kind, loc, energy, color=(1, 1, 1), size=0.1, rot=(0, 0, 0), spot=None):
    l = bpy.data.lights.new("l", kind); l.energy = energy; l.color = color
    if kind in ("POINT", "SPOT"): l.shadow_soft_size = size
    if kind == "AREA": l.size = size
    if kind == "SUN": l.angle = size
    if kind == "SPOT" and spot: l.spot_size = spot
    o = link(bpy.data.objects.new("l", l)); o.location = loc; o.rotation_euler = rot
    return o

def look_at(ob, target):
    d = Vector(target) - ob.location
    ob.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()

# ----------------------------------------------------------------------------------------------- set kit
class Kit:
    def __init__(self, seed):
        self.rng = random.Random(seed)
        self.stone = mat("stone", "#6A6458", 0.9, bump=0.5, bump_scale=1.2, kind="brick")
        self.dark_stone = mat("dstone", "#3E3A34", 0.9, bump=0.5, bump_scale=1.5, kind="brick")
        self.plaster = mat("plaster", "#B8A888", 0.95, bump=0.15, bump_scale=6)
        self.timber = mat("timber", "#3A2A1C", 0.8, bump=0.2, bump_scale=20)
        self.roof = mat("roof", "#4A3A30", 0.85, bump=0.4, bump_scale=14)
        self.slate = mat("slate", "#3A3E46", 0.7, bump=0.3, bump_scale=18)
        self.wood = mat("wood", "#5A3E26", 0.7, bump=0.3, bump_scale=10)
        self.iron = mat("iron", "#2A2A2E", 0.45, metal=1.0, bump=0.1, bump_scale=30)
        self.gold = mat("gold", "#D9B23A", 0.3, metal=1.0)
        self.window = mat("window", "#200C04", 0.5, emit="#FF9A40", power=6)
        self.flame = mat("flame", "#FF7A20", 0.5, emit="#FF8A2A", power=30)
        self.ember = mat("ember", "#200800", 0.9, emit="#FF5010", power=8)
        self.wax = mat("candlewax", "#E8DFC4", 0.4)
        self.paper = mat("paper", "#E2D6B4", 0.85)
        self.cloth_white = mat("whitecloth", "#E8E2D2", 0.9, bump=0.1, bump_scale=60)

    def ground(self, colour="#3A342C", size=80, bump=0.4, rough=0.9, scale=3.0):
        m = mat("ground", colour, rough, bump=bump, bump_scale=scale)
        bpy.ops.mesh.primitive_plane_add(size=size, location=(0, 0, 0))
        ob = bpy.context.active_object; ob.data.materials.append(m)
        return ob

    def floor_flags(self, w, d, loc=(0, 0, 0), colour="#5A544A"):
        m = mat("flags", colour, 0.85, bump=0.6, bump_scale=0.7, kind="brick")
        return cube("floor", (w, d, 0.1), (loc[0], loc[1], loc[2] - 0.05), m)

    def wall(self, a, b, h=4.0, t=0.6, m=None, z=0.0):
        a, b = Vector(a), Vector(b)
        d = b - a; L = d.length
        ob = cube("wall", (L, t, h), ((a.x + b.x) / 2, (a.y + b.y) / 2, z + h / 2), m or self.stone, (0, 0, math.atan2(d.y, d.x)))
        return ob

    def pillar(self, loc, h=4.0, r=0.3, m=None):
        m = m or self.stone
        cyl("shaft", r, h, (loc[0], loc[1], h / 2), m, 20)
        cube("base", (r * 2.6, r * 2.6, 0.3), (loc[0], loc[1], 0.15), m, bevel=0.03)
        cube("cap", (r * 2.8, r * 2.8, 0.3), (loc[0], loc[1], h - 0.1), m, bevel=0.03)

    def arch(self, loc, span=3.0, h=4.0, depth=0.6, rot=0.0, m=None):
        """A round arch: two piers and a half-ring."""
        m = m or self.stone
        R = span / 2
        bm = bmesh.new()
        seg = 16
        outer, inner = R + 0.45, R
        verts = []
        for i in range(seg + 1):
            a = math.pi * i / seg
            for rr in (inner, outer):
                for y in (-depth / 2, depth / 2):
                    verts.append(bm.verts.new((math.cos(a) * rr, y, math.sin(a) * rr)))
        for i in range(seg):
            q = [verts[i * 4 + k] for k in range(4)] + [verts[(i + 1) * 4 + k] for k in range(4)]
            # inner, outer faces and front/back
            bm.faces.new((q[0], q[1], q[5], q[4])); bm.faces.new((q[2], q[6], q[7], q[3]))
            bm.faces.new((q[0], q[4], q[6], q[2])); bm.faces.new((q[1], q[3], q[7], q[5]))
        me = bpy.data.meshes.new("arch"); bm.to_mesh(me); bm.free()
        ob = link(bpy.data.objects.new("arch", me)); ob.data.materials.append(m)
        ob.location = (loc[0], loc[1], h); ob.rotation_euler = (0, 0, rot)
        for s in (-1, 1):
            off = Vector((math.cos(rot), math.sin(rot), 0)) * (R + 0.22) * s
            cube("pier", (0.45, depth, h), (loc[0] + off.x, loc[1] + off.y, h / 2), m, (0, 0, rot))
        return ob

    def vault(self, length, width, h, loc=(0, 0, 0), m=None):
        """Barrel vault ceiling along Y."""
        m = m or self.dark_stone
        R = width / 2
        bm = bmesh.new()
        seg = 20
        rows = []
        for j in (0, 1):
            row = []
            for i in range(seg + 1):
                a = math.pi * i / seg
                row.append(bm.verts.new((math.cos(a) * R, -length / 2 + j * length, math.sin(a) * R)))
            rows.append(row)
        for i in range(seg):
            bm.faces.new((rows[0][i], rows[0][i + 1], rows[1][i + 1], rows[1][i]))
        me = bpy.data.meshes.new("vault"); bm.to_mesh(me); bm.free()
        for p in me.polygons: p.use_smooth = True
        ob = link(bpy.data.objects.new("vault", me)); ob.data.materials.append(m)
        ob.location = (loc[0], loc[1], loc[2] + h)
        sol = ob.modifiers.new("s", "SOLIDIFY"); sol.thickness = 0.3
        return ob

    def house(self, loc, w=5, d=4, h=4, rot=0.0, lit=True, burning=False):
        x, y = loc
        base = cube("house", (w, d, h), (x, y, h / 2), self.plaster, (0, 0, rot))
        prism_roof("roof", w + 0.6, d + 0.6, h * 0.55, (x, y, h), self.roof, rot + math.pi / 2)
        # Timber framing on the front.
        fwd = Vector((-math.sin(rot), math.cos(rot), 0))
        side = Vector((math.cos(rot), math.sin(rot), 0))
        for k in (-0.5, 0, 0.5):
            p = Vector((x, y, 0)) + fwd * (-d / 2 - 0.03) + side * (k * w * 0.95)
            cube("beam", (0.18, 0.08, h), (p.x, p.y, h / 2), self.timber, (0, 0, rot))
        p = Vector((x, y, 0)) + fwd * (-d / 2 - 0.03)
        cube("beamh", (w, 0.08, 0.18), (p.x, p.y, h * 0.5), self.timber, (0, 0, rot))
        if lit:
            for k in (-0.25, 0.25):
                q = p + side * (k * w)
                cube("win", (0.6, 0.06, 0.8), (q.x, q.y, h * 0.72), self.window if self.rng.random() < 0.8 else self.timber, (0, 0, rot))
        door = p + side * (-0.0)
        cube("door", (0.9, 0.08, 1.8), (door.x, door.y, 0.9), self.wood, (0, 0, rot))
        if burning:
            self.fire((x, y, h + 0.6), scale=w * 0.35, smoke=True)
        return base

    def round_church(self, loc, r=5, h=7, burning=False):
        x, y = loc
        cyl("rotunda", r, h, (x, y, h / 2), self.stone, 40)
        cyl("roof", r + 0.5, h * 0.5, (x, y, h + h * 0.25), self.slate if not burning else self.timber, 40, r2=0.2)
        cyl("drum", r * 0.45, h * 0.35, (x, y, h * 1.35), self.stone, 24)
        for i in range(10):
            a = i / 10 * 2 * math.pi
            cube("buttress", (0.8, 0.9, h * 0.8), (x + math.cos(a) * r, y + math.sin(a) * r, h * 0.4), self.stone, (0, 0, a))
            if i % 2 == 0:
                cube("lancet", (0.5, 0.2, 1.6), (x + math.cos(a + 0.3) * (r + 0.02), y + math.sin(a + 0.3) * (r + 0.02), h * 0.62),
                     self.window if burning else self.timber, (0, 0, a + 0.3 + math.pi / 2))
        if burning:
            self.fire((x, y, h + 1.0), scale=r * 0.9, smoke=True)

    def tower(self, loc, r=3, h=16, m=None, crenel=True, windows=True):
        x, y = loc
        m = m or self.stone
        cyl("tower", r, h, (x, y, h / 2), m, 32)
        if crenel:
            cyl("parapet", r + 0.3, 0.8, (x, y, h + 0.4), m, 32)
            for i in range(14):
                a = i / 14 * 2 * math.pi
                cube("merlon", (0.6, 0.6, 0.7), (x + math.cos(a) * (r + 0.1), y + math.sin(a) * (r + 0.1), h + 1.15), m, (0, 0, a))
        if windows:
            for k in range(3):
                z = h * (0.3 + 0.22 * k)
                cube("slit", (0.25, 0.3, 1.0), (x, y - r - 0.02, z), self.window)

    def keep(self, loc, w=10, d=8, h=10):
        x, y = loc
        cube("keep", (w, d, h), (x, y, h / 2), self.stone)
        for i in range(int(w / 1.2)):
            cube("merlon", (0.6, 0.6, 0.8), (x - w / 2 + 0.6 + i * 1.2, y - d / 2 + 0.3, h + 0.4), self.stone)
        for cx in (-w / 2, w / 2):
            for cy in (-d / 2, d / 2):
                self.tower((x + cx, y + cy), 1.6, h + 3, windows=False)

    def cathedral_front(self, loc, w=18, h=22):
        """A twin-towered west front with a rose window, as a silhouette in the distance."""
        x, y = loc
        cube("nave", (w * 0.5, 6, h * 0.7), (x, y, h * 0.35), self.stone)
        for s in (-1, 1):
            cube("tower", (w * 0.28, 6.4, h), (x + s * w * 0.36, y, h / 2), self.stone)
            for k in range(2):
                cube("lancet", (0.8, 0.2, 4.0), (x + s * w * 0.36, y - 3.22, h * (0.55 + 0.25 * k)), self.window)
        rose = cyl("rose", 2.2, 0.2, (x, y - 3.05, h * 0.5), self.window, 32, rot=(math.pi / 2, 0, 0))
        for k in range(3):
            cube("portal", (2.2, 0.3, 4.4), (x + (k - 1) * 3.6, y - 3.05, 2.2), self.timber)

    def tent(self, loc, r=2.2, h=3.0, colour="#D8D0BC", rot=0.0, stripe=None):
        x, y = loc
        m = mat("tent", colour, 0.95, bump=0.15, bump_scale=30)
        cyl("tent", r, h, (x, y, h / 2), m, 16, r2=0.05)
        cyl("pole", 0.05, 0.8, (x, y, h + 0.3), self.wood, 8)
        if stripe:
            s = mat("tentstripe", stripe, 0.95)
            cyl("stripe", r * 0.62, 0.25, (x, y, h * 0.42), s, 16, r2=r * 0.55)

    def tree(self, loc, h=7.0, bare=False, colour="#2E3A20"):
        x, y = loc
        bark = mat("bark", "#3A2C20", 0.9, bump=0.6, bump_scale=6)
        cyl("trunk", 0.3, h * 0.6, (x, y, h * 0.3), bark, 10, r2=0.18)
        if bare:
            for i in range(6):
                a = self.rng.uniform(0, 6.28); t = self.rng.uniform(0.5, 1.0)
                b = cyl("branch", 0.08, h * 0.35, (x + math.cos(a) * 0.6, y + math.sin(a) * 0.6, h * (0.45 + 0.2 * t)), bark, 6, r2=0.02,
                        rot=(math.cos(a) * 0.8, math.sin(a) * 0.8 * -1, 0))
        else:
            leaf = mat("leaf", colour, 0.85, bump=0.8, bump_scale=4)
            for i in range(5):
                ball("crown", h * 0.22, (x + self.rng.uniform(-0.8, 0.8), y + self.rng.uniform(-0.8, 0.8), h * (0.65 + self.rng.uniform(0, 0.25))), leaf,
                     (1, 1, 0.85), 16)

    def fire(self, loc, scale=1.0, smoke=False, light_power=None):
        """Volumetric flames: an emissive noise volume shaped like a teardrop, plus a warm light."""
        x, y, z = loc
        m = bpy.data.materials.new("fire"); m.use_nodes = True
        nt = m.node_tree
        for n in list(nt.nodes):
            if n.type == "BSDF_PRINCIPLED": nt.nodes.remove(n)
        out = next(n for n in nt.nodes if n.type == "OUTPUT_MATERIAL")
        vol = nt.nodes.new("ShaderNodeVolumePrincipled")
        tc = nt.nodes.new("ShaderNodeTexCoord")
        # Noise stretched upward so the flames read as tongues.
        mp = nt.nodes.new("ShaderNodeMapping"); mp.inputs["Scale"].default_value = (2.6, 2.6, 0.8)
        mp.inputs["Location"].default_value = (self.rng.uniform(0, 50), self.rng.uniform(0, 50), 0)
        nt.links.new(tc.outputs["Object"], mp.inputs["Vector"])
        nz = nt.nodes.new("ShaderNodeTexNoise"); nz.inputs["Scale"].default_value = 3.2; nz.inputs["Detail"].default_value = 10
        nz.inputs["Distortion"].default_value = 1.4
        nt.links.new(mp.outputs[0], nz.inputs["Vector"])
        grad = nt.nodes.new("ShaderNodeTexGradient"); grad.gradient_type = "SPHERICAL"
        nt.links.new(tc.outputs["Object"], grad.inputs["Vector"])
        sep = nt.nodes.new("ShaderNodeSeparateXYZ"); nt.links.new(tc.outputs["Object"], sep.inputs[0])
        up = nt.nodes.new("ShaderNodeMapRange"); up.inputs["From Min"].default_value = -1; up.inputs["From Max"].default_value = 1
        up.inputs["To Min"].default_value = 1.0; up.inputs["To Max"].default_value = 0.0
        nt.links.new(sep.outputs["Z"], up.inputs["Value"])
        m1 = nt.nodes.new("ShaderNodeMath"); m1.operation = "MULTIPLY"
        nt.links.new(nz.outputs["Fac"], m1.inputs[0]); nt.links.new(grad.outputs["Fac"], m1.inputs[1])
        m2 = nt.nodes.new("ShaderNodeMath"); m2.operation = "MULTIPLY"
        nt.links.new(m1.outputs[0], m2.inputs[0]); nt.links.new(up.outputs[0], m2.inputs[1])
        ramp = nt.nodes.new("ShaderNodeValToRGB")
        ramp.color_ramp.elements[0].position = 0.17; ramp.color_ramp.elements[0].color = (0, 0, 0, 1)
        ramp.color_ramp.elements[1].position = 0.42; ramp.color_ramp.elements[1].color = (1, 1, 1, 1)
        nt.links.new(m2.outputs[0], ramp.inputs[0])
        temp = nt.nodes.new("ShaderNodeMapRange"); temp.inputs["To Min"].default_value = 1200; temp.inputs["To Max"].default_value = 2600
        nt.links.new(ramp.outputs[0], temp.inputs["Value"])
        bb = nt.nodes.new("ShaderNodeBlackbody"); nt.links.new(temp.outputs[0], bb.inputs[0])
        nt.links.new(bb.outputs[0], vol.inputs["Emission Color"])
        em = nt.nodes.new("ShaderNodeMath"); em.operation = "MULTIPLY"; em.inputs[1].default_value = 70.0
        nt.links.new(ramp.outputs[0], em.inputs[0]); nt.links.new(em.outputs[0], vol.inputs["Emission Strength"])
        vol.inputs["Density"].default_value = 0.0
        nt.links.new(vol.outputs[0], out.inputs["Volume"])
        o = ball("fire", 1.0, (x, y, z + scale * 1.2), m, (scale * 1.1, scale * 1.1, scale * 2.1), 16)
        light("POINT", (x, y, z + scale * 0.8), light_power or 4000 * scale * scale, (1.0, 0.5, 0.18), size=scale * 0.7)
        if smoke:
            self.smoke((x, y, z + scale * 3.2), scale)

    def smoke(self, loc, scale=1.0, density=0.35, colour="#2A2420"):
        m = bpy.data.materials.new("smoke"); m.use_nodes = True
        nt = m.node_tree
        for n in list(nt.nodes):
            if n.type == "BSDF_PRINCIPLED": nt.nodes.remove(n)
        out = next(n for n in nt.nodes if n.type == "OUTPUT_MATERIAL")
        vol = nt.nodes.new("ShaderNodeVolumePrincipled")
        vol.inputs["Color"].default_value = rgb(colour)
        nz = nt.nodes.new("ShaderNodeTexNoise"); nz.inputs["Scale"].default_value = 1.2; nz.inputs["Detail"].default_value = 6
        tc = nt.nodes.new("ShaderNodeTexCoord"); nt.links.new(tc.outputs["Object"], nz.inputs["Vector"])
        grad = nt.nodes.new("ShaderNodeTexGradient"); grad.gradient_type = "SPHERICAL"
        nt.links.new(tc.outputs["Object"], grad.inputs["Vector"])
        mul = nt.nodes.new("ShaderNodeMath"); mul.operation = "MULTIPLY"
        nt.links.new(nz.outputs["Fac"], mul.inputs[0]); nt.links.new(grad.outputs["Fac"], mul.inputs[1])
        mul2 = nt.nodes.new("ShaderNodeMath"); mul2.operation = "MULTIPLY"; mul2.inputs[1].default_value = density * 6
        nt.links.new(mul.outputs[0], mul2.inputs[0]); nt.links.new(mul2.outputs[0], vol.inputs["Density"])
        nt.links.new(vol.outputs[0], out.inputs["Volume"])
        o = ball("smoke", 1.0, loc, m, (scale * 2.2, scale * 2.2, scale * 3.5), 16)
        return o

    def candle(self, loc, h=0.25, power=8):
        x, y, z = loc
        cyl("candle", 0.03, h, (x, y, z + h / 2), self.wax, 10)
        ball("cflame", 0.012, (x, y, z + h + 0.02), self.flame, (1, 1, 2.2), 8)
        light("POINT", (x, y, z + h + 0.06), power, (1.0, 0.68, 0.35), size=0.03)

    def table(self, loc, w=2.2, d=1.0, h=0.85, rot=0.0):
        x, y = loc
        cube("tabletop", (w, d, 0.08), (x, y, h), self.wood, (0, 0, rot), bevel=0.01)
        for sx in (-1, 1):
            for sy in (-1, 1):
                off = Vector((sx * (w / 2 - 0.1), sy * (d / 2 - 0.1), 0))
                off.rotate(Euler((0, 0, rot)))
                cube("leg", (0.08, 0.08, h), (x + off.x, y + off.y, h / 2), self.wood, (0, 0, rot))

    def book(self, loc, open_=True, rot=0.0, scale=1.0):
        x, y, z = loc
        cov = mat("bookcover", "#4A1A12", 0.6)
        if open_:
            for s in (-1, 1):
                cube("page", (0.2 * scale, 0.28 * scale, 0.02 * scale), (x + s * 0.105 * scale, y, z + 0.015), self.paper, (0, s * 0.08, rot))
            cube("cover", (0.44 * scale, 0.3 * scale, 0.01 * scale), (x, y, z), cov, (0, 0, rot))
        else:
            cube("book", (0.22 * scale, 0.3 * scale, 0.06 * scale), (x, y, z + 0.03), cov, (0, 0, rot), bevel=0.005)

    def shelf(self, loc, w=3.0, h=3.2, rot=0.0):
        x, y = loc
        cube("shelfback", (w, 0.1, h), (x, y, h / 2), self.wood, (0, 0, rot))
        side = Vector((math.cos(rot), math.sin(rot), 0)); fwd = Vector((-math.sin(rot), math.cos(rot), 0))
        cols = ["#4A1A12", "#2A3A5A", "#3A4A2A", "#5A4A2A", "#6A2A1A", "#E2D6B4"]
        for k in range(5):
            z = 0.3 + k * 0.62
            p = Vector((x, y, 0)) - fwd * 0.25  # shelves face -Y, towards the room
            cube("plank", (w, 0.42, 0.05), (p.x, p.y, z), self.wood, (0, 0, rot))
            xx = -w / 2 + 0.1
            while xx < w / 2 - 0.15:
                bw = self.rng.uniform(0.05, 0.1); bh = self.rng.uniform(0.3, 0.48)
                q = p + side * xx
                if self.rng.random() < 0.85:
                    cube("vol", (bw, 0.3, bh), (q.x, q.y, z + bh / 2 + 0.03), mat("vol", self.rng.choice(cols), 0.7), (0, 0, rot + self.rng.uniform(-0.05, 0.05)))
                else:
                    cyl("scroll", 0.05, 0.36, (q.x, q.y, z + 0.08), self.paper, 10, rot=(0, math.pi / 2, rot))
                xx += bw + 0.01

    def bars(self, x0, x1, y, h=3.0, step=0.22):
        x = x0
        while x <= x1:
            cyl("bar", 0.03, h, (x, y, h / 2), self.iron, 10)
            x += step
        for z in (0.2, h * 0.5, h - 0.1):
            cube("crossbar", (x1 - x0 + 0.1, 0.06, 0.06), ((x0 + x1) / 2, y, z), self.iron)

    def banner(self, loc, colour, emblem=None, ecol="#E6C14A", h=3.0, w=1.2, rot=0.0, torn=False, pole=True):
        x, y, z = loc
        m = mat("banner", colour, 0.9, bump=0.2, bump_scale=40)
        me = bpy.data.meshes.new("banner")
        bm = bmesh.new()
        cols, rows = 8, 12
        grid = []
        for j in range(rows + 1):
            row = []
            for i in range(cols + 1):
                u, v = i / cols, j / rows
                wave = 0.06 * math.sin(v * 5 + u * 2)
                if torn and v < 0.18 and (i % 3 == 1): v = 0.18
                row.append(bm.verts.new(((u - 0.5) * w, wave, -v * h)))
            grid.append(row)
        uv = bm.loops.layers.uv.new()
        for j in range(rows):
            for i in range(cols):
                f = bm.faces.new((grid[j][i], grid[j][i + 1], grid[j + 1][i + 1], grid[j + 1][i]))
                for l, (uu, vv) in zip(f.loops, ((i, j), (i + 1, j), (i + 1, j + 1), (i, j + 1))):
                    l[uv].uv = (uu / cols, 1 - vv / rows)
        # Swallowtail at the bottom.
        bm.to_mesh(me); bm.free()
        ob = link(bpy.data.objects.new("banner", me)); ob.data.materials.append(m)
        ob.location = (x, y, z + h); ob.rotation_euler = (0, 0, rot)
        if emblem:
            name, col = (emblem, ecol)
            if emblem == "cross": col = "#B3241E"
            dm = F.decal_material(name, col)
            pl = F.plane("bannerdecal", w * 0.7, w * 0.7, dm)
            pl.location = (x, y, z + h - h * 0.38); pl.rotation_euler = (0, 0, rot)
            pl.location += Vector((0, -0.07, 0)).to_4d().to_3d() if False else Vector((-math.sin(rot) * -0.0, -0.08 * math.cos(rot), 0))
            pl.rotation_euler.z = rot + math.pi
        if pole:
            cyl("pole", 0.04, h + 1.0, (x, y + 0.02, z + (h + 1.0) / 2), self.wood, 8)
            cyl("cross", 0.03, w + 0.2, (x, y + 0.02, z + h), self.wood, 8, rot=(0, math.pi / 2, rot))
        return ob

    def pyre(self, loc, burning=False, h=2.2):
        x, y = loc
        for layer in range(8):
            for i in range(9):
                a = self.rng.uniform(0, 6.28); r = self.rng.uniform(0.4, 1.8) * (1 - layer / 10)
                cyl("log", 0.12, 1.6, (x + math.cos(a) * r, y + math.sin(a) * r, 0.15 + layer * 0.18), self.wood, 8,
                    rot=(math.pi / 2, 0, a + self.rng.uniform(-0.6, 0.6)))
        cyl("stake", 0.12, h + 2.5, (x, y, (h + 2.5) / 2), self.timber, 10)
        if burning:
            self.fire((x, y, 1.2), scale=1.8, smoke=True, light_power=6000)

    def chest(self, loc, rot=0.0, open_=False):
        x, y = loc
        cube("chest", (0.9, 0.55, 0.5), (x, y, 0.25), self.wood, (0, 0, rot), bevel=0.02)
        for k in (-0.3, 0, 0.3):
            off = Vector((k, 0, 0)); off.rotate(Euler((0, 0, rot)))
            cube("band", (0.06, 0.57, 0.52), (x + off.x, y + off.y, 0.25), self.iron, (0, 0, rot))
        if open_:
            for i in range(5):
                cube("letter", (0.22, 0.3, 0.004), (x + self.rng.uniform(-0.3, 0.3), y + self.rng.uniform(-0.2, 0.2), 0.52 + i * 0.01), self.paper,
                     (0, 0, self.rng.uniform(-0.6, 0.6)))

    def crowd(self, centre, count, radius, facing_to, looks=None, seed=1):
        rng = random.Random(seed)
        for i in range(count):
            a = rng.uniform(0, 6.28); r = math.sqrt(rng.uniform(0.05, 1)) * radius
            pos = (centre[0] + math.cos(a) * r, centre[1] + math.sin(a) * r * 0.6)
            look = dict(rng.choice(looks or CROWD))
            figure(f"crowd{i}", look, pos, face=facing_to, anim="idle", frame=rng.randint(1, 8), scale=rng.uniform(0.92, 1.05))

CROWD = [
    {"body": "#6A5038", "trim": "#3A2A1E", "head": "bare", "weapon": "none", "dress": "tunic"},
    {"body": "#4A5034", "trim": "#2A2018", "head": "hood", "weapon": "none", "dress": "robe"},
    {"body": "#5A3A24", "trim": "#2A1A10", "head": "bare_beard", "weapon": "none", "dress": "apron"},
    {"body": "#6E2438", "trim": "#C9A227", "head": "veil", "weapon": "none", "dress": "gown"},
    {"body": "#3A3236", "trim": "#7A7468", "head": "hood", "weapon": "none", "dress": "tunic"},
    {"body": "#7A6A50", "trim": "#3A2A1E", "head": "hat", "weapon": "none", "dress": "tunic"},
]

# ----------------------------------------------------------------------------------------------- figures
_fig_count = [0]

def figure(name, look, pos, face=None, rot=None, anim="idle", frame=1, scale=1.0):
    """Builds a posed game figure at pos, facing a point (or rotation in degrees)."""
    _fig_count[0] += 1
    nm = f"{name}_{_fig_count[0]}"
    fig = F.build(nm, dict(look))
    F.animate(fig, anim, F.ANIMS.get(anim, (8,))[0])
    sc = bpy.context.scene
    sc.frame_set(frame)
    # Freeze the pose: copy evaluated transforms, then drop the animation.
    saved = {k: (j.location.copy(), j.rotation_euler.copy()) for k, j in fig.rig.j.items()}
    for k, j in fig.rig.j.items():
        j.animation_data_clear()
        j.location, j.rotation_euler = saved[k]
    t = fig.rig.turn
    t.location = (pos[0], pos[1], pos[2] if len(pos) > 2 else 0.0)
    if face is not None:
        d = Vector((face[0] - pos[0], face[1] - pos[1]))
        t.rotation_euler = (0, 0, math.atan2(d.y, d.x) - math.pi / 2)
    elif rot is not None:
        t.rotation_euler = (0, 0, math.radians(rot))
    big = 1.15 if look.get("big") else 1.0
    t.scale = (scale * big,) * 3
    return fig

def L(name, **over):
    d = dict(LOOKS[name]); d.update(over)
    return d

# ----------------------------------------------------------------------------------------------- scene setup
def setup(samples, gpu, w, h, sky=("#0A0C14", "#1A1A26"), strength=1.0, fog=0.0, fog_colour="#3A3440", exposure=0.0):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    F._mats.clear(); _fig_count[0] = 0
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.samples = samples; sc.cycles.use_denoising = True
    try: sc.cycles.denoiser = "OPENIMAGEDENOISE"
    except Exception: pass
    sc.cycles.max_bounces = 8
    sc.cycles.volume_bounces = 1
    sc.cycles.volume_step_rate = 4.0
    sc.render.resolution_x, sc.render.resolution_y = w, h
    sc.render.image_settings.file_format = "PNG"; sc.render.image_settings.color_mode = "RGB"
    try:
        sc.view_settings.view_transform = "AgX"
        sc.view_settings.look = "AgX - Punchy"
    except Exception:
        try: sc.view_settings.view_transform = "Filmic"
        except Exception: pass
    sc.view_settings.exposure = exposure
    if gpu:
        prefs = bpy.context.preferences.addons["cycles"].preferences
        for t in ("OPTIX", "CUDA", "HIP", "METAL"):
            try:
                prefs.compute_device_type = t; prefs.refresh_devices()
                if any(d.type == t for d in prefs.devices):
                    for d in prefs.devices: d.use = d.type == t
                    sc.cycles.device = "GPU"; break
            except Exception: continue
    world = bpy.data.worlds.new("w"); sc.world = world; world.use_nodes = True
    nt = world.node_tree; bg = nt.nodes["Background"]
    tc = nt.nodes.new("ShaderNodeTexCoord"); sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    nt.links.new(tc.outputs["Generated"], sep.inputs[0])
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].position = 0.5; ramp.color_ramp.elements[0].color = rgb(sky[1])
    ramp.color_ramp.elements[1].position = 0.85; ramp.color_ramp.elements[1].color = rgb(sky[0])
    nt.links.new(sep.outputs["Z"], ramp.inputs[0]); nt.links.new(ramp.outputs[0], bg.inputs[0])
    bg.inputs[1].default_value = strength
    if fog > 0:
        # A bounded fog box, not a world volume: a world volume would swallow the sky and the sun (both at infinity).
        m = bpy.data.materials.new("fog"); m.use_nodes = True
        mt = m.node_tree
        for n in list(mt.nodes):
            if n.type == "BSDF_PRINCIPLED": mt.nodes.remove(n)
        vol = mt.nodes.new("ShaderNodeVolumePrincipled")
        vol.inputs["Density"].default_value = fog
        vol.inputs["Color"].default_value = rgb(fog_colour)
        vol.inputs["Anisotropy"].default_value = 0.3
        mt.links.new(vol.outputs[0], next(n for n in mt.nodes if n.type == "OUTPUT_MATERIAL").inputs["Volume"])
        box = cube("fogbox", (70, 70, 26), (0, 18, 12.5), m)
        box.visible_shadow = True
    return sc

def camera(loc, target, lens=35, dof=None):
    cam = bpy.data.cameras.new("cam"); cam.lens = lens
    co = link(bpy.data.objects.new("cam", cam)); co.location = loc
    look_at(co, target)
    bpy.context.scene.camera = co
    if dof:
        cam.dof.use_dof = True; cam.dof.focus_distance = dof[0]; cam.dof.aperture_fstop = dof[1]
    return co

def moon(loc, r=3.0, power=6.0):
    m = mat("moon", "#E8E8F0", 0.5, emit="#DDE4FF", power=power)
    ball("moon", r, loc, m)

# ----------------------------------------------------------------------------------------------- scenes
def S_prologue(k: Kit):
    """Payns, 13 October 1307: the preceptory burns in the night; brothers flee as the king's men arrive."""
    k.ground("#2A241E", bump=0.6)
    k.round_church((6, 22), 5, 7, burning=True)
    k.house((-6, 20), 7, 5, 5, 0.1, burning=True)
    k.house((-14, 26), 6, 5, 4, -0.2, burning=True)
    k.wall((-20, 14), (20, 14), 2.5, 0.8)
    k.tower((14, 16), 2.0, 9)
    k.smoke((0, 26, 12), 6, density=0.25)
    for i in range(30):
        ball("ember", 0.03, (k.rng.uniform(-12, 14), k.rng.uniform(10, 24), k.rng.uniform(2, 14)), k.ember)
    figure("brother", L("class_knight"), (-1.2, 4.0), face=(2, 22), anim="walk", frame=3)
    figure("thomas", L("npc_brother_thomas"), (0.6, 3.2), face=(4, 22), anim="idle")
    figure("sergeant", L("mon_royal_sergeant"), (5.5, 9), face=(0, 3), anim="walk", frame=5)
    figure("sergeant", L("mon_crossbowman"), (7.5, 10.5), face=(0, 3), anim="idle")
    figure("torch", L("mon_paris_zealot", body="#23407A", trim="#D9B23A"), (3.8, 11), face=(0, 3), anim="idle")
    light("SUN", (0, 0, 10), 0.5, (0.5, 0.6, 1.0), 0.02, rot=(0.8, 0, 2.6))
    light("AREA", (2, -2, 4), 300, (1.0, 0.6, 0.35), 3.0, rot=(1.0, 0, 0.3))
    moon((-40, 90, 40), 3.5)
    camera((-0.6, -4.5, 1.9), (1.5, 10, 3.6), 30)

def S_title(k: Kit):
    k.ground("#2A241E", bump=0.6)
    k.round_church((-9, 26), 6, 8, burning=True)
    k.house((-22, 30), 7, 5, 5, 0.3, burning=True)
    k.house((8, 32), 7, 5, 6, -0.2, burning=True)
    k.tower((20, 36), 2.6, 14)
    k.wall((-40, 18), (40, 18), 1.6, 0.8)
    k.smoke((-8, 30, 16), 8, density=0.2)
    for i in range(60):
        ball("ember", 0.035, (k.rng.uniform(-20, 20), k.rng.uniform(4, 26), k.rng.uniform(0.5, 12)), k.ember)
    figure("hero", L("class_knight"), (2.3, 1.4), face=(-0.5, -6), anim="idle", scale=1.0)
    k.banner((3.6, 2.4, 0), "#F0ECE2", "cross", h=2.4, w=0.9, rot=0.1)
    figure("paladin", L("class_paladin"), (4.4, 4.4), face=(0, -6), anim="idle", frame=3)
    figure("wizard", L("class_wizard"), (-2.6, 2.6), face=(0.5, -6), anim="idle", frame=5)
    figure("healer", L("class_healer"), (-4.2, 4.6), face=(0, -6), anim="idle", frame=2)
    light("SUN", (0, 0, 10), 0.6, (0.5, 0.6, 1.0), 0.02, rot=(0.9, 0, 2.8))
    light("AREA", (0, -3, 4), 600, (1.0, 0.75, 0.55), 6.0, rot=(1.1, 0, 0))
    light("AREA", (-2, 9, 3), 900, (1.0, 0.45, 0.2), 8.0, rot=(-1.3, 0, 0))
    moon((40, 120, 50), 4.0)
    camera((0.0, -4.4, 1.35), (0.0, 6, 2.6), 30)

def S_crypt(k: Kit, gather=True, table=True):
    k.floor_flags(14, 24, colour="#4A443C")
    k.vault(24, 8, 3.2, (0, 4, 0))
    k.wall((-4.3, -8), (-4.3, 16), 3.4, 0.6, k.dark_stone)
    k.wall((4.3, -8), (4.3, 16), 3.4, 0.6, k.dark_stone)
    k.wall((-5, 12), (5, 12), 8, 0.6, k.dark_stone)
    for y in (0, 6):
        for x in (-3.4, 3.4):
            k.pillar((x, y), 3.4, 0.35, k.dark_stone)
    k.banner((0, 11.4, 1.0), "#F0ECE2", "cross", h=2.6, w=1.4, pole=False)
    if table:
        k.table((0, 6), 3.0, 1.1)
        for x in (-1.0, 0.0, 1.1):
            k.candle((x, 6 + k.rng.uniform(-0.3, 0.3), 0.89), power=40)
        k.book((0.4, 6.0, 0.9))
    for x, y in ((-3.9, 2), (3.9, 2), (-3.9, 9), (3.9, 9)):
        k.candle((x, y, 0.0), 0.5, power=60)
    light("POINT", (0, 3, 2.8), 250, (1.0, 0.7, 0.4), 1.0)
    light("POINT", (0, 8, 2.6), 200, (1.0, 0.65, 0.35), 1.0)
    if gather:
        figure("gerard", L("npc_gerard_de_villiers"), (-0.6, 7.6), face=(0, 0), anim="idle")
        figure("thomas", L("npc_brother_thomas"), (1.4, 7.4), face=(0, 0), anim="idle", frame=3)
        figure("ysabeau", L("npc_sister_ysabeau"), (2.4, 6.4), face=(0, 2), anim="idle", frame=5)
        figure("aubry", L("npc_master_aubry"), (-2.4, 6.2), face=(0, 2), anim="idle", frame=2)
        figure("player", L("class_knight"), (0.5, 2.4), face=(0, 7), anim="idle")
        figure("brother", L("class_paladin"), (-1.2, 3.4), face=(0, 7), anim="idle", frame=4)

def S_sanctuary_first(k):
    S_crypt(k)
    camera((0.4, -2.6, 1.7), (0, 7, 1.5), 28)

def S_ledger(k):
    k.floor_flags(10, 10, colour="#3A342C")
    k.table((0, 0), 2.4, 1.2, 0.85)
    k.book((0, -0.05, 0.9), scale=1.4)
    for i in range(40):
        ball("ash", k.rng.uniform(0.01, 0.03), (k.rng.uniform(-1.1, 1.1), k.rng.uniform(-0.5, 0.5), 0.9), mat("ash", "#2A2622", 1.0), (1, 1, 0.3), 8)
    k.candle((-0.8, 0.25, 0.89), 0.18, power=10)
    k.candle((0.9, 0.3, 0.89), 0.12, power=8)
    cyl("quill", 0.006, 0.4, (0.55, -0.15, 0.92), mat("quill", "#E8E2D2", 0.6), 6, rot=(1.4, 0, 0.6))
    for i in range(6):
        ball("ember", 0.01, (k.rng.uniform(-1, 1), k.rng.uniform(-0.4, 0.4), 0.9), k.ember)
    k.wall((-5, 2), (5, 2), 3, 0.4, k.dark_stone)
    camera((0.5, -1.6, 1.75), (0, 0.05, 0.9), 40, dof=(1.75, 2.8))

def S_town(k):
    k.ground("#3A3228", bump=0.5)
    k.floor_flags(8, 40, (0, 10, 0.01), "#5A5046")
    for i, y in enumerate((2, 8, 14, 20, 26)):
        k.house((-6.5, y), 5, 5, 5 + (i % 2), math.pi / 2 * 0 - 0.0 + math.pi / 2, lit=True)
        k.house((6.5, y + 2), 5, 5, 4.5 + (i % 3) * 0.5, -math.pi / 2, lit=True)
    figure("informer", L("npc_broker_act2"), (0.4, 4.5), face=(0, -2), anim="idle")
    figure("hooded", L("mon_court_spy", weapon="none"), (-1.6, 8), face=(0, 4), anim="walk", frame=2)
    figure("player", L("class_knight"), (1.4, 1.0), face=(0, 5), anim="idle")
    light("SUN", (0, 0, 10), 1.2, (1.0, 0.6, 0.35), 0.03, rot=(1.35, 0, 2.6))
    camera((-0.5, -6.5, 1.9), (0.5, 12, 3.5), 30)

def S_castle(k):
    k.ground("#3A3A2A", bump=0.8, scale=0.5)
    for i in range(12):
        ball("hill", 8, (k.rng.uniform(-30, 30), 40 + k.rng.uniform(-6, 6), -5), mat("hill", "#2E3424", 0.9, bump=0.4, bump_scale=0.3), (1.5, 1, 0.6), 24)
    k.keep((0, 40), 12, 9, 11)
    k.wall((-14, 33), (14, 33), 6, 1.2)
    k.tower((-14, 33), 2.4, 9); k.tower((14, 33), 2.4, 9)
    k.banner((-14, 33, 9.5), "#5E5A4E", None, h=2.2, w=1.0, torn=True)
    k.banner((14, 33, 9.5), "#5E5A4E", None, h=2.2, w=1.0, torn=True)
    figure("player", L("class_knight"), (-0.8, 2), face=(0, 40), anim="idle")
    figure("paladin", L("class_paladin"), (1.2, 2.8), face=(0, 40), anim="idle", frame=3)
    k.banner((2.5, 3.4, 0), "#F0ECE2", "cross", h=2.4, w=0.9)
    light("SUN", (0, 0, 10), 3.5, (1.0, 0.62, 0.38), 0.02, rot=(1.42, 0, 3.1))
    camera((-1.5, -5, 1.6), (0.5, 30, 6), 30)

def S_hermit(k):
    k.ground("#2A2624", bump=1.0, scale=1.0)
    for i in range(16):
        ball("rock", k.rng.uniform(1.5, 4), (k.rng.uniform(-10, 10), k.rng.uniform(6, 14), k.rng.uniform(0, 3)), mat("cave", "#3A3430", 0.9, bump=1.0, bump_scale=1.5), (1, 1, 1.6), 20)
    cyl("well", 1.0, 0.9, (0, 4, 0.45), k.stone, 24)
    cyl("wellhole", 0.8, 0.95, (0, 4, 0.5), mat("void", "#020202", 1.0), 24)
    for x in (-0.4, 0.4):
        ball("eyes", 0.05, (x * 0.4, 4.0, 0.98), mat("paleeye", "#9FE0C0", 0.5, emit="#9FE0C0", power=25))
    cube("wellbeam", (2.4, 0.15, 0.15), (0, 4, 2.0), k.wood)
    for x in (-1.1, 1.1): cube("wellpost", (0.15, 0.15, 2.0), (x, 4, 1.0), k.wood)
    figure("hermit", L("npc_the_hermit"), (-1.8, 3.2), face=(0, 0), anim="idle")
    figure("player", L("class_wizard"), (1.4, 1.2), face=(0, 4), anim="idle")
    k.candle((-1.2, 4.6, 0.9), 0.2, 60)
    light("SPOT", (0, 4, 6), 1500, (0.6, 0.7, 1.0), 0.5, rot=(0, 0, 0), spot=0.7)
    light("POINT", (-2.4, 2.0, 2.2), 300, (1.0, 0.65, 0.35), 0.4)
    light("AREA", (1, -2, 3), 200, (0.6, 0.7, 1.0), 3, rot=(1.0, 0, 0))
    camera((0.2, -3.5, 2.2), (0, 4, 0.9), 32)

def S_archive(k):
    k.floor_flags(14, 14, colour="#4A443C")
    k.wall((-6, 6), (6, 6), 5, 0.5, k.dark_stone)
    for x in (-3.3, 0, 3.3):
        k.shelf((x, 5.6), 3.1, 3.4)
    k.table((0.5, 2), 2.4, 1.0)
    for i in range(10):
        cube("paper", (0.22, 0.3, 0.003), (k.rng.uniform(-2.5, 3), k.rng.uniform(-0.5, 3), 0.005), k.paper, (0, 0, k.rng.uniform(0, 3)))
    k.book((0.2, 2.0, 0.9)); k.candle((1.2, 2.2, 0.89), 0.2, 14); k.candle((-0.5, 1.8, 0.89), 0.15, 10)
    figure("marcus", L("boss_marcus"), (-0.5, 0.8), rot=10, anim="death", frame=10)
    figure("player", L("class_paladin"), (1.8, -0.3), face=(-0.5, 0.8), anim="idle")
    light("POINT", (0, 1, 3), 300, (1.0, 0.75, 0.5), 1.5)
    light("AREA", (3, -2, 3.5), 250, (1.0, 0.8, 0.6), 3, rot=(0.9, 0, 0.6))
    camera((2.5, -4.5, 2.6), (0, 2, 0.8), 30)

def S_estate(k):
    k.floor_flags(16, 16, colour="#6A5E4A")
    k.wall((-7, 7), (7, 7), 6, 0.5, k.plaster)
    k.wall((-7, -2), (-7, 7), 6, 0.5, k.plaster)
    for x in (-4, 0, 4):
        k.banner((x, 6.6, 1.2), "#1B3366", "fleur", h=3.6, w=1.6, pole=False)
    cube("fireplace", (3, 1, 2.6), (6.4, 3, 1.3), k.stone, (0, 0, math.pi / 2))
    k.fire((6.2, 3, 0.2), 0.5)
    figure("count", L("npc_count_de_valois"), (0.2, 4.0), face=(0, 0), anim="idle")
    figure("eleanor", L("npc_eleanor_of_valois"), (1.6, 4.4), face=(0, 0), anim="idle", frame=4)
    figure("player", L("class_knight"), (-0.6, 0.6), face=(0.5, 4), anim="idle")
    light("AREA", (-3, 0, 5), 200, (1.0, 0.92, 0.8), 3, rot=(0.6, -0.4, 0))
    camera((-1.5, -7.0, 2.4), (0.6, 4, 1.4), 32)

def S_court(k):
    k.floor_flags(20, 30, colour="#8A7E68")
    for y in range(0, 24, 4):
        for x in (-4.5, 4.5):
            k.pillar((x, y), 7, 0.45)
    k.vault(30, 9, 7.0, (0, 10, 0), k.stone)
    for y in (4, 12):
        k.banner((-4.5, y, 2.5), "#1B3366", "fleur", h=3, w=1.0, pole=False, rot=math.pi / 2)
    figure("guard", L("mon_royal_guardsman"), (-2.6, 6), face=(0, 0), anim="idle")
    figure("guard", L("mon_royal_guardsman"), (2.8, 9), face=(0, 0), anim="idle", frame=4)
    figure("guard", L("mon_court_guard"), (-2.2, 14), face=(0, 0), anim="idle", frame=2)
    figure("clerk", L("mon_royal_almoner"), (1.2, 13), face=(-1, 5), anim="walk", frame=2)
    figure("player", L("mon_dominican_inquisitor"), (0.2, 2.2), face=(0, 12), anim="walk", frame=4)
    light("SUN", (0, 0, 10), 2.5, (1.0, 0.88, 0.7), 0.05, rot=(0.9, 0.0, 2.2))
    for y in (4, 12, 20):
        light("POINT", (0, y, 6), 150, (1.0, 0.75, 0.5), 1)
    camera((0.8, -5.0, 1.7), (0, 14, 2.6), 28)

def S_de_molay(k):
    k.floor_flags(10, 10, colour="#3A3632")
    k.wall((-5, 4), (5, 4), 5, 0.5, k.dark_stone)
    k.wall((-4, -1), (-4, 4), 5, 0.5, k.dark_stone)
    k.wall((4, -1), (4, 4), 5, 0.5, k.dark_stone)
    cube("window", (0.6, 0.3, 0.8), (1.2, 3.8, 3.6), mat("sky", "#AAB8D0", 0.5, emit="#C8D4F0", power=6))
    k.bars(-3.8, 3.8, 0.0, 3.0)
    figure("demolay", L("npc_gerard_de_villiers", head="bare_beard", body="#E8E2D2", cape=None), (0.6, 1.8), face=(0, -2), anim="idle")
    cube("straw", (1.6, 1.0, 0.1), (-2.2, 2.8, 0.05), mat("straw", "#8A7448", 0.95, bump=0.8, bump_scale=40))
    sp = light("SPOT", (1.2, 4.6, 3.6), 3000, (0.75, 0.82, 1.0), 0.15, spot=0.7)
    look_at(sp, (0.4, 1.0, 0.0))
    light("POINT", (-2.5, -2.0, 2.2), 250, (1.0, 0.6, 0.3), 0.3)
    light("AREA", (1.5, -3.0, 2.0), 80, (1.0, 0.75, 0.5), 2, rot=(1.3, 0, 0.3))
    camera((0.5, -3.2, 1.65), (0.5, 1.8, 1.55), 35, dof=(3.2, 4.0))

def S_vault(k):
    k.floor_flags(12, 12, colour="#4A443C")
    k.vault(12, 7, 2.6, (0, 2, 0))
    k.wall((-3.6, -4), (-3.6, 8), 2.8, 0.5, k.dark_stone)
    k.wall((3.6, -4), (3.6, 8), 2.8, 0.5, k.dark_stone)
    k.wall((-4, 6), (4, 6), 6, 0.5, k.dark_stone)
    k.banner((0, 5.6, 0.8), "#8E1C1C", "keys", h=2.6, w=1.4, pole=False)
    for x, y in ((-2.2, 3.5), (-1.0, 4.4), (1.5, 4.2), (2.4, 3.0), (0.2, 3.4)):
        k.chest((x, y), k.rng.uniform(-0.4, 0.4), open_=k.rng.random() < 0.6)
    for x, y in ((-3, 1), (3, 1), (0, 5)):
        k.candle((x, y, 0), 0.4, 18)
    figure("fallen", L("boss_inquisitor_general"), (-0.8, 1.2), rot=-30, anim="death", frame=10)
    figure("player", L("class_healer"), (1.6, 0.6), face=(0, 4), anim="idle")
    light("POINT", (0, 2.5, 2.2), 250, (1.0, 0.7, 0.4), 1.0)
    camera((0.8, -5.0, 2.2), (0, 3, 0.7), 30)

def S_camp(k):
    k.ground("#5A5650", bump=0.6, scale=1.5)
    for i in range(30):
        ball("snow", k.rng.uniform(0.6, 2.0), (k.rng.uniform(-20, 20), k.rng.uniform(-2, 30), -0.3), mat("snow", "#D8DCE4", 0.7, bump=0.3, bump_scale=3), (1, 1, 0.3), 16)
    k.wall((-60, 50), (60, 50), 10, 2.0)
    for x in range(-50, 51, 12): k.tower((x, 50), 3, 14)
    for i, (x, y) in enumerate(((-6, 10), (5, 12), (-12, 18), (11, 20), (0, 22), (-3, 15))):
        k.tent((x, y), 2.2, 3.0, "#D8D0BC", stripe="#B3241E" if i % 2 == 0 else None)
    for x, y in ((-2, 6), (4, 7)):
        k.fire((x, y, 0.1), 0.6)
    k.banner((0.5, 9, 0), "#F0ECE2", "cross", h=3, w=1.2)
    looks = [L("class_knight"), L("class_paladin"), L("class_healer"), L("class_wizard"), L("npc_aubry_act4"), L("npc_ysabeau_act4"),
             L("npc_reginald_act4"), L("npc_marie_act4"), L("npc_thomas_act4")]
    rng = random.Random(4)
    for i, lk in enumerate(looks):
        a = i / len(looks) * 6.28
        figure("rem", lk, (-2 + math.cos(a) * 2.2, 6 + math.sin(a) * 1.6), face=(-2, 6), anim="idle", frame=rng.randint(1, 8))
    light("SUN", (0, 0, 10), 0.6, (0.6, 0.7, 1.0), 0.05, rot=(1.1, 0, 2.6))
    camera((1, -6, 3.2), (-1, 10, 1.5), 32)

def S_greve(k, burning=False, night=False):
    k.ground("#4A4238", bump=0.4)
    k.floor_flags(40, 40, (0, 10, 0.01), "#5A5046")
    k.cathedral_front((6, 46), 22, 24)
    for i in range(4):
        k.house((-14 - (i % 2) * 2, 4 + i * 7), 6, 5, 6 + (i % 3), -math.pi / 2, lit=night)
        k.house((16 + (i % 2) * 2, 4 + i * 7), 6, 5, 6 + (i % 3), math.pi / 2, lit=night)
    k.pyre((0, 12), burning=burning)
    k.crowd((0, 5.5), 26, 5.5, (0, 12), seed=7)
    figure("guard", L("mon_royal_guardsman"), (-2.6, 10.5), face=(0, 0), anim="idle")
    figure("guard", L("mon_royal_guardsman"), (2.6, 10.5), face=(0, 0), anim="idle", frame=3)
    figure("player", L("class_knight"), (-0.6, -0.8), face=(0, 12), anim="idle")
    if not night:
        light("SUN", (0, 0, 10), 2.4, (1.0, 0.82, 0.62), 0.03, rot=(1.1, 0, 2.4))

def S_stake(k):
    S_greve(k)
    camera((-0.5, -7.5, 4.6), (0, 14, 2.5), 30)

def S_tower(k, dawn=False):
    k.ground("#2E2A26", bump=0.5)
    k.tower((0, 18), 5, 30, k.stone)
    cube("gate", (2.4, 0.4, 3.4), (0, 12.95, 1.7), mat("gatedark", "#0A0806", 1.0) if not dawn else mat("gatelight", "#FFE8C0", 0.5, emit="#FFE0B0", power=8))
    k.wall((-30, 16), (-5, 16), 7, 1.2); k.wall((5, 16), (30, 16), 7, 1.2)
    k.banner((-3.4, 13, 4), "#142A55", "fleur", h=3, w=1.3, pole=False)
    k.banner((3.4, 13, 4), "#142A55", "fleur", h=3, w=1.3, pole=False)
    if not dawn:
        figure("guard", L("mon_chosen_knight"), (-2.2, 11), face=(0, 0), anim="idle")
        figure("guard", L("mon_chosen_knight"), (2.2, 11), face=(0, 0), anim="idle", frame=3)
        figure("player", L("class_knight"), (0.4, 1.5), face=(0, 12), anim="idle")
        figure("ally", L("class_paladin"), (-1.6, 2.4), face=(0, 12), anim="idle", frame=5)
        moon((-30, 80, 60), 4, 8)
        light("SUN", (0, 0, 10), 1.6, (0.5, 0.6, 1.0), 0.02, rot=(0.7, 0, 2.6))
        light("AREA", (0, -3, 3), 400, (1.0, 0.7, 0.45), 4, rot=(1.2, 0, 0))
        for x in (-3, 3):
            k.fire((x, 12.6, 3.4), 0.25)
        camera((0.5, -6, 1.6), (0, 14, 9), 28)
    else:
        figure("demolay", L("npc_gerard_de_villiers", body="#E8E2D2", cape=None), (0, 10.5), face=(0, 0), anim="walk", frame=2)
        figure("player", L("class_knight"), (1.2, 9.8), face=(0, 0), anim="walk", frame=6)
        light("SUN", (0, 0, 10), 4.0, (1.0, 0.75, 0.5), 0.02, rot=(1.45, 0, 3.0))
        camera((-1.5, -3.5, 1.4), (0, 12, 4), 28)

def S_tower_of_truth(k): S_tower(k)
def S_rescue(k): S_tower(k, dawn=True)

def S_vengeance(k):
    water = mat("water", "#0A0E14", 0.05, metal=0.0)
    bpy.ops.mesh.primitive_plane_add(size=200, location=(0, 0, -0.2)); bpy.context.active_object.data.materials.append(water)
    island = ball("island", 6, (0, 14, -0.9), mat("isle", "#1A1612", 0.95, bump=0.5, bump_scale=2), (1.6, 1.0, 0.16), 32)
    k.pyre((0, 14), burning=True)
    k.cathedral_front((-14, 44), 20, 22)
    for i in range(8): k.house((-36 + i * 8, 36), 6, 5, 6 + i % 3, math.pi, lit=True)
    cube("boat", (1.2, 3.2, 0.5), (3.4, 3.0, -0.1), k.wood, (0, 0, 0.2), bevel=0.1)
    figure("player", L("class_knight"), (3.4, 3.2, 0.15), face=(0, 14), anim="idle")
    moon((30, 90, 40), 3.0, 4)
    camera((1.0, -4.0, 1.5), (0, 14, 3), 32)

def S_darkness(k):
    k.ground("#1E1A16", bump=0.8)
    for i in range(7):
        x = -20 + i * 7
        h = k.rng.uniform(2, 6)
        k.wall((x, 18 + k.rng.uniform(-3, 3)), (x + 4, 18 + k.rng.uniform(-3, 3)), h, 0.6, k.dark_stone)
        k.fire((x + 2, 16, 0.2), k.rng.uniform(0.6, 1.2), smoke=True)
    k.round_church((10, 30), 5, 5, burning=True)
    for i in range(6):
        figure("dead", L(k.rng.choice(["mon_royal_guardsman", "mon_papal_guard", "mon_court_guard", "npc_townsfolk"])), (k.rng.uniform(-5, 5), k.rng.uniform(3, 10)),
               rot=k.rng.uniform(0, 360), anim="death", frame=10)
    figure("player", L("class_knight", body="#3A3634", trim="#5A1A16", cape="#2A1414"), (0, 1.8), face=(0, 20), anim="idle")
    light("SUN", (0, 0, 10), 0.6, (1.0, 0.3, 0.2), 0.05, rot=(1.2, 0, 3.0))
    camera((0.8, -4.5, 1.8), (0, 12, 2.2), 30)

def S_redemption(k):
    k.floor_flags(10, 10, colour="#7A6A54")
    k.wall((-5, 4), (-1.2, 4), 5, 0.5, k.plaster); k.wall((1.2, 4), (5, 4), 5, 0.5, k.plaster)
    cube("lintel", (2.4, 0.5, 1.6), (0, 4, 4.2), k.plaster)
    sea = mat("sea", "#2A4A6A", 0.08)
    bpy.ops.mesh.primitive_plane_add(size=300, location=(0, 60, -2)); bpy.context.active_object.data.materials.append(sea)
    sail = mat("sail", "#F0ECE2", 0.9)
    cube("hull", (6, 1.6, 1.2), (1.5, 40, -1.6), k.wood, bevel=0.3)
    cyl("mast", 0.12, 7, (1.5, 40, 1.5), k.wood, 8)
    cube("sail", (4.0, 0.1, 4.2), (1.5, 39.8, 2.6), sail)
    dm = F.decal_material("cross", "#B3241E"); pl = F.plane("saildecal", 2.4, 2.4, dm); pl.location = (1.5, 39.7, 2.7); pl.rotation_euler = (0, 0, math.pi)
    k.chest((0.4, 1.6), 0.15)
    cube("mantle", (0.8, 0.5, 0.12), (0.4, 1.6, 0.56), k.cloth_white, (0, 0, 0.15), bevel=0.03)
    dm2 = F.decal_material("cross", "#B3241E"); p2 = F.plane("mantlecross", 0.36, 0.36, dm2)
    p2.location = (0.4, 1.6, 0.625); p2.rotation_euler = (-math.pi / 2, 0, 0.15)
    light("SUN", (0, 0, 10), 4.0, (1.0, 0.82, 0.62), 0.02, rot=(1.25, 0, 3.1))
    light("AREA", (0.4, 0.5, 3), 150, (1.0, 0.85, 0.7), 2, rot=(0, 0, 0))
    camera((-0.6, -1.8, 1.5), (0.6, 10, 0.4), 30)

def S_valois(k): S_estate(k)

SCENES = {
    # id: (builder, sky, sky strength, fog density, fog colour, exposure)
    "title": (S_title, ("#0A0E1E", "#3A2418"), 1.0, 0.004, "#6A4A3A", 1.0),
    "prologue": (S_prologue, ("#0A0E1E", "#3A2418"), 1.0, 0.005, "#6A4A3A", 1.0),
    "sanctuary_first": (S_sanctuary_first, ("#000000", "#050505"), 0.0, 0.012, "#8A6A4A", 1.2),
    "ledger_of_payns": (S_ledger, ("#000000", "#050505"), 0.0, 0.0, "#000000", 0.6),
    "town_in_shadow": (S_town, ("#1A1E3A", "#C8704A"), 0.7, 0.012, "#C8906A", 0.2),
    "castle_retaken": (S_castle, ("#2A3A6A", "#E8905A"), 1.0, 0.008, "#E8B08A", 0.0),
    "hermit_secret": (S_hermit, ("#000000", "#020204"), 0.0, 0.03, "#3A4A5A", 0.8),
    "marcus_archive": (S_archive, ("#000000", "#050505"), 0.0, 0.02, "#5A4A3A", 0.6),
    "valois_estate": (S_estate, ("#1A1A22", "#2A2A30"), 0.3, 0.0, "#000000", 0.4),
    "court_disguise": (S_court, ("#3A4A6A", "#8A9AB0"), 0.8, 0.01, "#C0B8A8", 0.2),
    "de_molay_bars": (S_de_molay, ("#000000", "#050505"), 0.0, 0.05, "#6A7A9A", 0.6),
    "legate_vault": (S_vault, ("#000000", "#050505"), 0.0, 0.02, "#5A4A3A", 0.6),
    "remnant_camp": (S_camp, ("#1A2A4A", "#7A8AA0"), 0.8, 0.015, "#A8B4C4", 0.2),
    "stake_at_greve": (S_stake, ("#4A5A7A", "#C8B8A0"), 0.9, 0.01, "#C8BCA8", 0.1),
    "tower_of_truth": (S_tower_of_truth, ("#04060E", "#141A2A"), 0.5, 0.02, "#3A4458", 0.5),
    "ending_rescue": (S_rescue, ("#3A5A8A", "#F0B080"), 1.0, 0.012, "#F0C8A0", 0.1),
    "ending_vengeance": (S_vengeance, ("#04060E", "#2A1410"), 0.5, 0.012, "#4A2A20", 0.4),
    "ending_darkness": (S_darkness, ("#0A0404", "#3A0A06"), 0.6, 0.03, "#4A1A10", 0.4),
    "ending_redemption": (S_redemption, ("#4A7AB0", "#F0D0A0"), 1.2, 0.0008, "#E8D8C0", 0.0),
}

def render_scene(sid, samples, gpu, scale, out):
    fn, sky, strength, fog, fogc, exp = SCENES[sid]
    w, h = (1920, 1080) if sid == "title" else (1280, 720)
    sc = setup(samples, gpu, w, h, sky, strength, fog, fogc, exp)
    sc.render.resolution_percentage = scale
    fn(Kit(sid))
    os.makedirs(out, exist_ok=True)
    name = "title.png" if sid == "title" else f"cutscene_{sid}.png"
    sc.render.filepath = os.path.join(out, name)
    bpy.ops.render.render(write_still=True)

def main(argv):
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--names", default="")
    ap.add_argument("--samples", type=int, default=128)
    ap.add_argument("--gpu", action="store_true")
    ap.add_argument("--scale", type=int, default=100)
    ap.add_argument("--out", default=OUT)
    a = ap.parse_args(argv)
    for n in [n for n in a.names.split(",") if n] or list(SCENES):
        try:
            render_scene(n, a.samples, a.gpu, a.scale, a.out)
            print("[scenes]", n, flush=True)
        except Exception as e:
            import traceback; traceback.print_exc()
            print("[scenes] FAILED", n, e, flush=True)

if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:])
