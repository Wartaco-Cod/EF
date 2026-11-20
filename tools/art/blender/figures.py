"""
Templar Knights figure generator: builds every character, monster and NPC as a real 3D model in Blender from
its look spec (tools/art/placeholder_looks.json), animates it procedurally and renders it to animated
sprite sheets with the locked isometric camera and lighting.

Run with Blender (4.2+ / 5.x) or the bpy module:

    blender -b --python tools/art/blender/figures.py -- --names class_knight --samples 64 --gpu
    python  tools/art/blender/figures.py --names class_knight --samples 16          (bpy module, CPU)
    blender -b --python tools/art/blender/figures.py -- --all --gpu                   (everything)

Output, per figure, in assets/sprites/:
    <name>.png         a 256 px still (front three-quarter, idle) used for portraits, menus and as a fallback
    <name>_sheet.png   the animated atlas: rows = animation x direction, columns = frames
    <name>.json        the sheet description read by the client (SpriteSheet.cs)

Everything is modelled from primitives in this file: no third-party models, textures or reference images.
Figures are rigid parts parented to a joint hierarchy (empties), which reads cleanly at sprite size and keeps
the generator dependable. World units are metres; figures stand at the origin, Z up, facing +Y (right hand +X).
"""
from __future__ import annotations

import json
import math
import os
import sys
import time

import bpy
import bmesh
from mathutils import Euler, Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
LOOKS = os.path.join(ROOT, "tools", "art", "placeholder_looks.json")
EMBLEMS = os.path.join(HERE, "emblems")
SPRITES = os.path.join(ROOT, "assets", "sprites")

# ----------------------------------------------------------------------------------------------- render config
CAM_PITCH = 30.0           # degrees above the horizon; the game's ground is drawn as an oblique top-down view
ANCHOR_Y = 0.8            # feet sit this far down the frame
FIGURE_SPAN = 2.6          # metres covered by a frame's height for a normal figure (ortho scale)
KEY_ENERGY, FILL_ENERGY, RIM_ENERGY = 3.0, 0.7, 2.2
WORLD = "#1A1A1E"

# Animation set: name -> (frames, fps, loop). Frames are sampled evenly from the procedural motion.
ANIMS = {"idle": (8, 6, True), "walk": (8, 11, True), "attack": (8, 16, False), "hit": (4, 14, False), "death": (10, 12, False)}
HIT_FRAME = 4

def dirs_for(kind: str) -> list[float]:
    """Screen facing angles (degrees, 0 = right, 90 = down/towards the camera, clockwise)."""
    if kind == "minor":
        return [45, 135, 225, 315]
    return [0, 45, 90, 135, 180, 225, 270, 315]

# ----------------------------------------------------------------------------------------------- colour helpers
def srgb_to_lin(c: float) -> float:
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4

def rgb(hexstr: str, k: float = 1.0):
    h = hexstr.lstrip("#")
    return tuple(srgb_to_lin(min(1.0, int(h[i:i + 2], 16) / 255 * k)) for i in (0, 2, 4)) + (1.0,)

def hsv_of(hexstr: str):
    h = hexstr.lstrip("#")
    r, g, b = (int(h[i:i + 2], 16) / 255 for i in (0, 2, 4))
    mx, mn = max(r, g, b), min(r, g, b)
    s = 0 if mx == 0 else (mx - mn) / mx
    if mx == mn: hue = 0
    elif mx == r: hue = ((g - b) / (mx - mn)) % 6
    elif mx == g: hue = (b - r) / (mx - mn) + 2
    else: hue = (r - g) / (mx - mn) + 4
    return hue * 60, s, mx

def is_gold(hexstr: str) -> bool:
    hue, s, v = hsv_of(hexstr)
    return 35 <= hue <= 60 and s > 0.45 and v > 0.6

def darker(hexstr: str, k: float) -> str:
    h = hexstr.lstrip("#")
    return "#" + "".join(f"{max(0, min(255, int(int(h[i:i + 2], 16) * k))):02X}" for i in (0, 2, 4))

# ----------------------------------------------------------------------------------------------- materials
_mats: dict[str, bpy.types.Material] = {}

def _principled(m):
    m.use_nodes = True
    nt = m.node_tree
    for n in list(nt.nodes):
        if n.type != "OUTPUT_MATERIAL": nt.nodes.remove(n)
    out = next(n for n in nt.nodes if n.type == "OUTPUT_MATERIAL")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    nt.links.new(bsdf.outputs[0], out.inputs[0])
    return nt, bsdf

def _set(bsdf, name, value):
    if name in bsdf.inputs: bsdf.inputs[name].default_value = value

def _bump(nt, bsdf, scale: float, strength: float, kind: str = "noise", detail: float = 6.0):
    coord = nt.nodes.new("ShaderNodeTexCoord")
    if kind == "voronoi":
        tex = nt.nodes.new("ShaderNodeTexVoronoi"); tex.inputs["Scale"].default_value = scale
        src = tex.outputs["Distance"]
    elif kind == "wave":
        tex = nt.nodes.new("ShaderNodeTexWave"); tex.inputs["Scale"].default_value = scale
        tex.inputs["Distortion"].default_value = 2.0; src = tex.outputs["Fac"]
    else:
        tex = nt.nodes.new("ShaderNodeTexNoise"); tex.inputs["Scale"].default_value = scale
        tex.inputs["Detail"].default_value = detail; src = tex.outputs["Fac"]
    nt.links.new(coord.outputs["Object"], tex.inputs["Vector"])
    bump = nt.nodes.new("ShaderNodeBump"); bump.inputs["Strength"].default_value = strength
    nt.links.new(src, bump.inputs["Height"])
    nt.links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    return tex

def material(kind: str, color: str = "#808080", **kw) -> bpy.types.Material:
    """kind: cloth, metal, gold, leather, skin, wood, bone, stone, dark, glow, ghost, hair, paper, fur, chain."""
    key = f"{kind}:{color}:{sorted(kw.items())}"
    if key in _mats: return _mats[key]
    m = bpy.data.materials.new(key)
    nt, b = _principled(m)
    c = rgb(color)
    _set(b, "Base Color", c)
    if kind == "cloth":
        _set(b, "Roughness", 0.86); _set(b, "Sheen Weight", 0.35)
        _bump(nt, b, 90, 0.18, "wave" if kw.get("weave") else "noise", 4)
    elif kind == "metal":
        _set(b, "Metallic", 1.0); _set(b, "Roughness", kw.get("rough", 0.32))
        _bump(nt, b, 30, 0.08)
    elif kind == "chain":
        _set(b, "Metallic", 1.0); _set(b, "Roughness", 0.45)
        _bump(nt, b, 260, 0.55, "voronoi")
    elif kind == "gold":
        _set(b, "Metallic", 1.0); _set(b, "Roughness", 0.26)
        _bump(nt, b, 40, 0.05)
    elif kind == "leather":
        _set(b, "Roughness", 0.62); _bump(nt, b, 60, 0.2)
    elif kind == "skin":
        _set(b, "Roughness", 0.55); _set(b, "Subsurface Weight", 0.15)
        if "Subsurface Radius" in b.inputs: b.inputs["Subsurface Radius"].default_value = (0.6, 0.25, 0.15)
    elif kind == "wood":
        _set(b, "Roughness", 0.7); _bump(nt, b, 12, 0.25, "wave")
    elif kind == "bone":
        _set(b, "Roughness", 0.58); _bump(nt, b, 50, 0.25)
    elif kind == "stone":
        _set(b, "Roughness", 0.9); _bump(nt, b, 22, 0.45, "noise", 10)
    elif kind == "fur":
        _set(b, "Roughness", 0.95); _set(b, "Sheen Weight", 0.6); _bump(nt, b, 150, 0.45, "wave")
    elif kind == "hair":
        _set(b, "Roughness", 0.7); _bump(nt, b, 120, 0.3, "wave")
    elif kind == "paper":
        _set(b, "Roughness", 0.8)
    elif kind == "dark":
        _set(b, "Base Color", rgb("#060506")); _set(b, "Roughness", 1.0)
    elif kind == "glow":
        _set(b, "Emission Color", c); _set(b, "Emission Strength", kw.get("power", 6.0))
    elif kind == "ghost":
        _set(b, "Roughness", 0.6); _set(b, "Alpha", kw.get("alpha", 0.55))
        _set(b, "Emission Color", c); _set(b, "Emission Strength", 0.6)
        m.blend_method = "BLEND" if hasattr(m, "blend_method") else None
    _mats[key] = m
    return m

def decal_material(emblem: str, color: str) -> bpy.types.Material:
    key = f"decal:{emblem}:{color}"
    if key in _mats: return _mats[key]
    m = bpy.data.materials.new(key)
    nt, b = _principled(m)
    img = bpy.data.images.load(os.path.join(EMBLEMS, emblem + ".png"), check_existing=True)
    tex = nt.nodes.new("ShaderNodeTexImage"); tex.image = img; tex.interpolation = "Cubic"
    coord = nt.nodes.new("ShaderNodeTexCoord")
    nt.links.new(coord.outputs["UV"], tex.inputs["Vector"])
    _set(b, "Base Color", rgb(color))
    if is_gold(color):
        _set(b, "Metallic", 0.9); _set(b, "Roughness", 0.3)
    else:
        _set(b, "Roughness", 0.75)
    nt.links.new(tex.outputs["Alpha"], b.inputs["Alpha"])
    if hasattr(m, "blend_method"): m.blend_method = "CLIP"
    _mats[key] = m
    return m

# ----------------------------------------------------------------------------------------------- mesh helpers
def _obj(name: str, mesh: bpy.types.Mesh, mat=None, smooth=True) -> bpy.types.Object:
    if smooth:
        for p in mesh.polygons: p.use_smooth = True
    ob = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(ob)
    if mat is not None: ob.data.materials.append(mat)
    return ob

def lathe(name, profile, seg=18, sx=1.0, sy=1.0, mat=None, smooth=True, arc=1.0):
    """Revolves (z, r) pairs around Z. Rings with r == 0 collapse to a point. sx/sy squash the cross-section."""
    bm = bmesh.new()
    rings = []
    closed = arc >= 0.999
    n = seg if closed else seg + 1
    for z, r in profile:
        if r <= 1e-5:
            rings.append([bm.verts.new((0, 0, z))])
            continue
        ring = []
        for i in range(n):
            a = 2 * math.pi * arc * i / seg
            ring.append(bm.verts.new((math.cos(a) * r * sx, math.sin(a) * r * sy, z)))
        rings.append(ring)
    for a_ring, b_ring in zip(rings, rings[1:]):
        if len(a_ring) == 1 and len(b_ring) == 1: continue
        if len(a_ring) == 1:
            for i in range(n if closed else n - 1):
                bm.faces.new((a_ring[0], b_ring[i], b_ring[(i + 1) % n]))
        elif len(b_ring) == 1:
            for i in range(n if closed else n - 1):
                bm.faces.new((a_ring[i], a_ring[(i + 1) % n], b_ring[0]))
        else:
            for i in range(n if closed else n - 1):
                bm.faces.new((a_ring[i], a_ring[(i + 1) % n], b_ring[(i + 1) % n], b_ring[i]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    return _obj(name, me, mat, smooth)

def box(name, sx, sy, sz, mat=None, bevel=0.0, taper=1.0):
    """Box centred on the origin. taper scales the +Z face (for wedges, blades)."""
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        k = taper if v.co.z > 0 else 1.0
        v.co.x *= sx * k; v.co.y *= sy * (1 if taper == 1 else max(k, 0.5)); v.co.z *= sz
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    ob = _obj(name, me, mat, smooth=False)
    if bevel > 0:
        mod = ob.modifiers.new("bevel", "BEVEL"); mod.width = bevel; mod.segments = 2; mod.limit_method = "ANGLE"
        for p in ob.data.polygons: p.use_smooth = True
        if hasattr(ob.data, "use_auto_smooth"): ob.data.use_auto_smooth = True
        else:
            try: ob.modifiers.new("smooth", "SMOOTH_BY_ANGLE")
            except Exception: pass
    return ob

def sphere(name, r, mat=None, sx=1.0, sy=1.0, sz=1.0, seg=20, rings=12):
    prof = [(-r * sz + 0.0, 0.0)]
    for i in range(1, rings):
        a = -math.pi / 2 + math.pi * i / rings
        prof.append((math.sin(a) * r * sz, math.cos(a) * r))
    prof.append((r * sz, 0.0))
    return lathe(name, prof, seg, sx, sy, mat)

def tube(name, r0, r1, length, mat=None, seg=14, cap=True):
    """Tapered rod from z=0 down to z=-length with rounded ends (a limb or haft)."""
    prof = []
    if cap:
        prof += [(r0 * 0.7, 0.0), (r0 * 0.55, r0 * 0.7), (0.0, r0)]
    else:
        prof += [(0.0, 0.0), (0.0, r0)]
    prof += [(-length, r1)]
    if cap:
        prof += [(-length - r1 * 0.55, r1 * 0.7), (-length - r1 * 0.7, 0.0)]
    else:
        prof += [(-length, 0.0)]
    return lathe(name, prof, seg, 1, 1, mat)

def plane(name, w, h, mat=None):
    me = bpy.data.meshes.new(name)
    me.from_pydata([(-w / 2, 0, -h / 2), (w / 2, 0, -h / 2), (w / 2, 0, h / 2), (-w / 2, 0, h / 2)], [], [(0, 1, 2, 3)])
    uv = me.uv_layers.new()
    for i, (u, v) in enumerate([(0, 0), (1, 0), (1, 1), (0, 1)]): uv.data[i].uv = (u, v)
    return _obj(name, me, mat, smooth=False)

def solid_from_outline(name, pts2d, depth, mat=None, bevel=0.0):
    """Extrudes a 2D outline (x, z) along Y. Used for shields, blades, axe heads."""
    bm = bmesh.new()
    front = [bm.verts.new((x, -depth / 2, z)) for x, z in pts2d]
    back = [bm.verts.new((x, depth / 2, z)) for x, z in pts2d]
    bm.faces.new(front); bm.faces.new(list(reversed(back)))
    n = len(pts2d)
    for i in range(n):
        bm.faces.new((front[i], back[i], back[(i + 1) % n], front[(i + 1) % n]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    ob = _obj(name, me, mat, smooth=False)
    if bevel:
        mod = ob.modifiers.new("bevel", "BEVEL"); mod.width = bevel; mod.segments = 2
    return ob

def curve_tube(name, pts, radius, mat=None, taper_end=1.0):
    """A swept tube through 3D points (bows, whips, crozier curls)."""
    cu = bpy.data.curves.new(name, "CURVE"); cu.dimensions = "3D"
    cu.bevel_depth = radius; cu.bevel_resolution = 3; cu.use_fill_caps = True
    sp = cu.splines.new("POLY"); sp.points.add(len(pts) - 1)
    for i, p in enumerate(pts):
        sp.points[i].co = (p[0], p[1], p[2], 1.0)
        sp.points[i].radius = 1.0 + (taper_end - 1.0) * i / max(1, len(pts) - 1)
    ob = bpy.data.objects.new(name, cu); bpy.context.scene.collection.objects.link(ob)
    if mat is not None: cu.materials.append(mat)
    return ob

# ----------------------------------------------------------------------------------------------- rig
class Rig:
    """A joint hierarchy of empties. Rest pose has identity rotations, so parts are placed in world space."""

    def __init__(self, name: str):
        self.name = name
        self.j: dict[str, bpy.types.Object] = {}
        self.pos: dict[str, Vector] = {}
        self.objects: list[bpy.types.Object] = []
        self.turn = self.joint("turn", None, (0, 0, 0))      # rotated per render direction
        self.root = self.joint("root", "turn", (0, 0, 0))    # animated (bob, fall)

    def joint(self, name, parent, at, rot_mode="XYZ"):
        e = bpy.data.objects.new(f"{self.name}.{name}", None)
        e.empty_display_size = 0.05
        bpy.context.scene.collection.objects.link(e)
        at = Vector(at)
        if parent:
            e.parent = self.j[parent]
            e.location = at - self.pos[parent]
        else:
            e.location = at
        e.rotation_mode = rot_mode
        self.j[name] = e; self.pos[name] = at
        self.objects.append(e)
        return e

    def attach(self, ob, joint, at=(0, 0, 0), rot=(0, 0, 0), scale=None):
        """Places ob at world position `at` (rest pose) with rotation `rot` (degrees), parented to a joint."""
        ob.parent = self.j[joint]
        ob.location = Vector(at) - self.pos[joint]
        ob.rotation_euler = Euler([math.radians(a) for a in rot], "XYZ")
        if scale is not None: ob.scale = scale if hasattr(scale, "__len__") else (scale, scale, scale)
        self.objects.append(ob)
        return ob

# ----------------------------------------------------------------------------------------------- look -> parts
ARMOURED_HEADS = {"greathelm", "greathelm_plume", "crownhelm"}
MAIL_HEADS = {"helm", "kettle", "coif"}
ROBE_HEADS = {"hood_black", "hood_white", "tonsure", "mitre", "cardinal", "tiara", "skull_mitre", "wraith", "hat", "feathered", "crown"}
SWING = {"sword", "axe", "club", "mace", "maul", "hammer", "greataxe", "greatsword", "dagger", "crozier", "oar", "torch", "keys_ring", "whip", "staff", "staff_orb"}
THRUST = {"spear", "halberd"}
SHOOT = {"bow", "crossbow"}
CAST = {"book", "scroll", "seal", "none"}
TWO_HANDED = {"maul", "greataxe", "greatsword", "halberd", "spear", "oar", "staff_orb", "staff", "crozier"}

def dress_of(look: dict) -> str:
    if look.get("dress"): return look["dress"]
    head, weapon = look.get("head", "hood"), look.get("weapon", "none")
    if head in ARMOURED_HEADS: return "plate"
    if head in MAIL_HEADS: return "mail"
    if head == "executioner": return "tunic"
    if head == "skull": return "plate"
    if head in ROBE_HEADS: return "robe"
    if head == "hood" and weapon in ("bow", "dagger", "spear", "crossbow", "club", "sword", "axe"): return "tunic"
    if head == "hood": return "robe"
    if head in ("bare", "bare_beard"): return "tunic"
    return "tunic"

class Figure:
    """Builds a humanoid from a look spec and records how it should animate."""

    def __init__(self, name: str, look: dict):
        self.name, self.look = name, look
        self.rig = Rig(name)
        self.dress = dress_of(look)
        self.weapon = look.get("weapon", "none")
        self.undead, self.ghost, self.stone = look.get("undead", False), look.get("ghost", False), look.get("stone", False)
        self.boss, self.big = look.get("boss", False), look.get("big", False)
        self.body_hex = look.get("body", "#6A6050")
        self.trim_hex = look.get("trim", "#C9A227")
        self.style = ("shoot" if self.weapon in SHOOT else "thrust" if self.weapon in THRUST
                      else "cast" if self.weapon in CAST else "swing")
        if self.weapon == "none" and look.get("shape") == "ghoul": self.style = "claw"
        self.hunch = 0.0
        self.floating = self.ghost or look.get("head") == "wraith"

    # ---- materials for this figure
    def m_body(self):
        if self.stone: return material("stone", self.body_hex)
        if self.ghost: return material("ghost", self.body_hex, alpha=0.6)
        return material("cloth", self.body_hex)

    def m_trim(self):
        if self.stone: return material("stone", darker(self.body_hex, 0.8))
        if self.ghost: return material("ghost", self.trim_hex, alpha=0.7)
        if is_gold(self.trim_hex): return material("gold", self.trim_hex)
        return material("cloth", self.trim_hex, weave=True)

    def m_trim_solid(self):
        """Trim used on hard parts (shield rims, belts): gold if gold, else leather of that hue."""
        if self.stone: return material("stone", darker(self.body_hex, 0.7))
        if is_gold(self.trim_hex): return material("gold", self.trim_hex)
        return material("leather", self.trim_hex)

    def m_steel(self):
        if self.stone: return material("stone", self.body_hex)
        if self.ghost: return material("ghost", "#B8C4D4", alpha=0.6)
        if self.undead: return material("metal", "#7A766A", rough=0.6)
        if self.boss and is_gold(self.trim_hex): return material("metal", "#C4C8D0", rough=0.22)
        return material("metal", "#B4BAC4", rough=0.3)

    def m_chain(self):
        if self.stone: return material("stone", self.body_hex)
        if self.undead: return material("chain", "#6E6A60")
        return material("chain", "#8E949E")

    def m_skin(self):
        if self.stone: return material("stone", self.body_hex)
        if self.ghost: return material("ghost", "#C8D8E0", alpha=0.5)
        if self.undead: return material("skin", "#8A9A84")
        return material("skin", "#C8946E")

    def m_leather(self, hexc="#4A3222"):
        if self.stone: return material("stone", darker(self.body_hex, 0.85))
        return material("leather", hexc)

    def m_eye(self):
        return material("glow", self.look.get("eyes", "#9FE0C0" if self.undead else "#E0463C"), power=10.0)

    # ---- build
    def build(self):
        r = self.rig
        k = 1.0
        # Skeleton (metres). A slightly heroic 1.8 m figure.
        r.joint("hips", "root", (0, 0, 0.98))
        r.joint("spine", "hips", (0, 0, 1.1))
        r.joint("chest", "spine", (0, 0, 1.3))
        r.joint("neck", "chest", (0, 0, 1.54))
        r.joint("head", "neck", (0, 0, 1.6))
        for s, x in (("R", 1), ("L", -1)):
            r.joint(f"shoulder.{s}", "chest", (0.26 * x, 0, 1.47))
            r.joint(f"elbow.{s}", f"shoulder.{s}", (0.29 * x, 0, 1.19))
            r.joint(f"hand.{s}", f"elbow.{s}", (0.3 * x, 0.02, 0.94))
            r.joint(f"hip.{s}", "hips", (0.105 * x, 0, 0.95))
            r.joint(f"knee.{s}", f"hip.{s}", (0.11 * x, 0.01, 0.53))
            r.joint(f"ankle.{s}", f"knee.{s}", (0.11 * x, 0, 0.1))
        self.build_body()
        self.build_head()
        r.j["head"].scale = (1.18, 1.18, 1.18)
        self.build_weapon()
        if self.look.get("shield"): self.build_shield()
        if self.look.get("cape") or self.boss: self.build_cape()
        return self

    def limb(self, name, joint, child, r0, r1, mat):
        a, b = self.rig.pos[joint], self.rig.pos[child]
        d = b - a
        ob = tube(f"{self.name}.{name}", r0, r1, d.length, mat)
        # Tube runs along -Z; tilt it to point at the child joint.
        rot = (-d).to_track_quat("Z", "Y").to_euler()
        ob.rotation_euler = rot
        ob.parent = self.rig.j[joint]; ob.location = (0, 0, 0)
        self.rig.objects.append(ob)
        return ob

    def build_body(self):
        r, d = self.rig, self.dress
        body, trim = self.m_body(), self.m_trim()
        steel, chain, skin = self.m_steel(), self.m_chain(), self.m_skin()
        boots = self.m_leather("#3A2A1E")
        armour = d in ("plate", "mail")
        torso_mat = chain if armour else body
        # Torso (chest + belly): elliptical lathe.
        torso = lathe(f"{self.name}.torso", [(0.0, 0.0), (0.02, 0.15), (0.12, 0.17), (0.26, 0.2), (0.4, 0.215),
                                             (0.5, 0.18), (0.55, 0.09), (0.58, 0.0)], 20, 1.12, 0.7, torso_mat)
        r.attach(torso, "spine", (0, 0, 0.98))
        pelvis = lathe(f"{self.name}.pelvis", [(0.0, 0.0), (0.02, 0.15), (0.14, 0.175), (0.2, 0.16), (0.22, 0.0)],
                       18, 1.0, 0.72, torso_mat)
        r.attach(pelvis, "hips", (0, 0, 0.84))
        if d == "plate":
            # Breastplate under the surcoat shows at the collar, plus pauldrons and couters.
            for s, x in (("R", 1), ("L", -1)):
                p = sphere(f"{self.name}.pauldron.{s}", 0.12, steel, 1.05, 1.0, 0.75)
                r.attach(p, "chest", (0.27 * x, 0, 1.48), (0, 18 * x, 0))
                c = sphere(f"{self.name}.couter.{s}", 0.06, steel)
                r.attach(c, f"elbow.{s}", r.pos[f"elbow.{s}"])
        gorget = lathe(f"{self.name}.gorget", [(0, 0.11), (0.05, 0.085), (0.1, 0.07)], 16, 1, 0.9,
                       steel if armour else body)
        r.attach(gorget, "chest", (0, 0, 1.5))
        # Arms.
        sleeve = chain if armour else body
        for s in ("R", "L"):
            self.limb(f"uarm.{s}", f"shoulder.{s}", f"elbow.{s}", 0.082, 0.066, sleeve)
            self.limb(f"farm.{s}", f"elbow.{s}", f"hand.{s}", 0.066, 0.054, steel if d == "plate" else sleeve)
            hand_mat = steel if armour else (self.m_leather("#5A4030") if d == "tunic" else skin)
            h = sphere(f"{self.name}.hand.{s}", 0.052, hand_mat, 0.9, 1.1, 1.2)
            r.attach(h, f"hand.{s}", r.pos[f"hand.{s}"] + Vector((0, 0.01, -0.05)))
            if d == "robe" or d == "gown":
                cuff = lathe(f"{self.name}.cuff.{s}", [(0.0, 0.07), (-0.16, 0.11), (-0.17, 0.0)], 14, 1, 1, body)
                cuff.rotation_euler = Euler((0, 0, 0))
                r.attach(cuff, f"elbow.{s}", r.pos[f"elbow.{s}"] + Vector((0, 0, -0.06)))
        # Legs.
        legs = chain if armour else self.m_leather(darker(self.body_hex, 0.55)) if d != "robe" else material("cloth", darker(self.body_hex, 0.6))
        for s in ("R", "L"):
            self.limb(f"thigh.{s}", f"hip.{s}", f"knee.{s}", 0.1, 0.075, legs)
            self.limb(f"shin.{s}", f"knee.{s}", f"ankle.{s}", 0.074, 0.058, steel if d == "plate" else boots)
            foot = lathe(f"{self.name}.foot.{s}", [(0, 0.0), (0.004, 0.055), (0.06, 0.05), (0.09, 0.0)], 14, 1.0, 2.0, boots)
            r.attach(foot, f"ankle.{s}", r.pos[f"ankle.{s}"] + Vector((0, 0.05, -0.1)))
            if d == "plate":
                kp = sphere(f"{self.name}.poleyn.{s}", 0.065, steel, 1, 1.1, 1)
                r.attach(kp, f"knee.{s}", r.pos[f"knee.{s}"] + Vector((0, 0.03, 0)))
        # Garment over it.
        if d in ("plate", "mail"):
            self.surcoat(long=(d == "plate"))
        elif d in ("robe", "gown"):
            self.robe(gown=(d == "gown"))
        elif d == "apron":
            self.tunic(); self.apron()
        else:
            self.tunic()
        # Belt.
        belt = lathe(f"{self.name}.belt", [(0, 0.19), (0.045, 0.19)], 20, 1.0, 0.74, self.m_trim_solid() if d != "robe" else trim, smooth=True)
        r.attach(belt, "hips", (0, 0, 1.0))
        buckle = box(f"{self.name}.buckle", 0.06, 0.02, 0.05, material("gold", "#C9A227") if not self.stone else trim, bevel=0.005)
        r.attach(buckle, "hips", (0, 0.145, 1.022))
        if self.weapon in ("sword", "greatsword", "dagger") and d in ("plate", "mail", "tunic"):
            sc = box(f"{self.name}.scabbard", 0.05, 0.03, 0.7, self.m_leather("#2A1E16"), bevel=0.01, taper=0.6)
            r.attach(sc, "hips", (-0.22, -0.02, 0.68), (0, -12, 0))

    def _skirt(self, name, joint, top_z, bottom_z, r_top, r_bot, mat, flare=1.0, open_front=False, sy=0.75, seg=24):
        prof = []
        n = 6
        for i in range(n + 1):
            t = i / n
            z = top_z + (bottom_z - top_z) * t
            rr = r_top + (r_bot - r_top) * (t ** flare)
            prof.append((z, rr))
        ob = lathe(name, prof, seg, 1.0, sy, mat)
        ob.data.polygons.foreach_set("use_smooth", [True] * len(ob.data.polygons))
        sol = ob.modifiers.new("thick", "SOLIDIFY"); sol.thickness = 0.012
        return self.rig.attach(ob, joint, (0, 0, 0))

    def surcoat(self, long: bool):
        """A sleeveless surcoat/tabard over armour: chest panel plus a skirt split front and back."""
        r = self.rig
        body, trim = self.m_body(), self.m_trim()
        coat = lathe(f"{self.name}.coat", [(0.995, 0.2), (1.12, 0.215), (1.27, 0.228), (1.4, 0.226), (1.5, 0.19), (1.54, 0.12)],
                     22, 1.16, 0.74, body)
        coat.modifiers.new("thick", "SOLIDIFY").thickness = 0.01
        r.attach(coat, "spine", (0, 0, 0))
        hem = 0.42 if long else 0.62
        skirt = self._skirt(f"{self.name}.skirt", "hips", 1.0, hem, 0.205, 0.3, body, 1.4)
        # Split the skirt at the front and back so the legs can stride (clear two thin wedges).
        self._slit(skirt)
        # Trim bands at the hem and collar.
        band = lathe(f"{self.name}.hemband", [(hem + 0.035, 0.302), (hem, 0.306)], 24, 1.0, 0.75, trim)
        self._slit(band); r.attach(band, "hips", (0, 0, 0))
        collar = lathe(f"{self.name}.collar", [(1.5, 0.198), (1.54, 0.13)], 22, 1.18, 0.76, trim)
        r.attach(collar, "spine", (0, 0, 0))
        # Emblem front and back.
        em = self.look.get("emblem")
        if em:
            self.decal(em, "chest", (0, 0.186, 1.3), 0.28)
            self.decal(em, "chest", (0, -0.186, 1.3), 0.28, back=True)
            if long: self.decal(em, "hips", (0, 0.27, 0.62), 0.18)

    def _slit(self, ob):
        """Deletes faces of a lathe near the front (+Y) and back (-Y) centre lines."""
        me = ob.data
        bm = bmesh.new(); bm.from_mesh(me)
        kill = [f for f in bm.faces if abs(f.calc_center_median().x) < 0.035 and abs(f.calc_center_median().y) > 0.05]
        bmesh.ops.delete(bm, geom=kill, context="FACES")
        bm.to_mesh(me); bm.free()

    def robe(self, gown: bool):
        r = self.rig
        body, trim = self.m_body(), self.m_trim()
        top = lathe(f"{self.name}.robetop", [(0.98, 0.215), (1.12, 0.225), (1.27, 0.235), (1.4, 0.232), (1.5, 0.195), (1.55, 0.11)],
                    22, 1.16, 0.76, body)
        r.attach(top, "spine", (0, 0, 0))
        bottom = 0.0 if not self.floating else 0.25
        skirt = self._skirt(f"{self.name}.robe", "hips", 1.02, bottom + 0.02, 0.21, 0.36 if not gown else 0.42, body, 1.25)
        if self.floating:
            self._tatter(skirt, bottom)
        # Trim: a front stripe (orphrey) and hem.
        strip = self._skirt(f"{self.name}.orphrey", "hips", 1.02, bottom + 0.02, 0.212, (0.36 if not gown else 0.42) * 1.012, trim, 1.25)
        self._keep_front(strip, 0.045)
        strip2 = lathe(f"{self.name}.orphrey2", [(0.98, 0.218), (1.12, 0.228), (1.27, 0.238), (1.4, 0.235)], 22, 1.16, 0.765, trim)
        self._keep_front(strip2, 0.045); r.attach(strip2, "spine", (0, 0, 0))
        if self.floating: self._tatter(strip, bottom)
        if not self.floating:
            hemb = lathe(f"{self.name}.robehem", [(0.07, 0.355 if not gown else 0.415), (0.03, 0.362 if not gown else 0.422)], 24, 1.0, 0.75, trim)
            r.attach(hemb, "hips", (0, 0, 0))
        # Scapular/cowl over the shoulders.
        cowl = lathe(f"{self.name}.cowl", [(1.34, 0.0), (1.35, 0.25), (1.45, 0.25), (1.55, 0.16), (1.58, 0.0)], 22, 1.05, 0.8,
                     body if self.look.get("head", "").startswith("hood") or self.look.get("head") == "wraith" else
                     (self.m_body() if self.stone or self.ghost else material("cloth", darker(self.body_hex, 0.82))))
        r.attach(cowl, "chest", (0, 0, 0))
        em = self.look.get("emblem")
        if em:
            self.decal(em, "chest", (0, 0.192, 1.3), 0.22)

    def _keep_front(self, ob, half_width):
        me = ob.data
        bm = bmesh.new(); bm.from_mesh(me)
        kill = [f for f in bm.faces if not (abs(f.calc_center_median().x) < half_width and f.calc_center_median().y > 0)]
        bmesh.ops.delete(bm, geom=kill, context="FACES")
        bm.to_mesh(me); bm.free()

    def _tatter(self, ob, bottom):
        me = ob.data
        for v in me.vertices:
            if v.co.z < bottom + 0.2:
                a = math.atan2(v.co.y, v.co.x)
                v.co.z += 0.12 * (0.5 + 0.5 * math.sin(a * 7)) * (bottom + 0.2 - v.co.z) / 0.2
                v.co.x *= 0.8; v.co.y *= 0.8

    def tunic(self):
        r = self.rig
        body, trim = self.m_body(), self.m_trim()
        top = lathe(f"{self.name}.tunictop", [(0.98, 0.205), (1.12, 0.218), (1.27, 0.228), (1.4, 0.226), (1.5, 0.19), (1.54, 0.11)],
                    22, 1.15, 0.74, body)
        r.attach(top, "spine", (0, 0, 0))
        skirt = self._skirt(f"{self.name}.tunic", "hips", 1.0, 0.68, 0.2, 0.26, body, 1.3)
        self._slit(skirt)
        hem = lathe(f"{self.name}.tunichem", [(0.71, 0.262), (0.68, 0.266)], 22, 1, 0.75, trim)
        self._slit(hem); r.attach(hem, "hips", (0, 0, 0))
        em = self.look.get("emblem")
        if em: self.decal(em, "chest", (0, 0.184, 1.3), 0.24)
        # Hood draped back for archers and cutthroats is handled by the head.

    def apron(self):
        a = box(f"{self.name}.apron", 0.3, 0.015, 0.6, self.m_leather("#5A3A22"), bevel=0.006)
        self.rig.attach(a, "hips", (0, 0.2, 0.78), (-8, 0, 0))

    def decal(self, emblem, joint, at, size, back=False):
        name, color = emblem, self.trim_hex
        if emblem == "black_cross": name, color = "cross", "#141414"
        elif emblem == "cross_gold": name, color = "cross", "#E6C14A"
        elif emblem == "cross_white": name, color = "cross", "#F0ECE2"
        elif emblem == "cross": color = "#B3241E" if hsv_of(self.body_hex)[2] > 0.6 else self.trim_hex
        elif emblem == "fleur": color = "#E6C14A" if not is_gold(self.body_hex) else "#142A55"
        elif emblem == "keys": color = "#E6C14A" if not is_gold(self.body_hex) else "#8E1C1C"
        elif emblem == "eye": color = "#E6C14A"
        pl = plane(f"{self.name}.decal.{emblem}.{joint}.{int(back)}", size, size, decal_material(name, color))
        self.rig.attach(pl, joint, at, (0, 0, 180 if back else 0))
        # The plane faces -Y by default; front decals face +Y.
        if not back: pl.rotation_euler.z = math.radians(180)
        else: pl.rotation_euler.z = 0
        return pl

    def build_cape(self):
        r = self.rig
        col = self.look.get("cape") or darker(self.body_hex, 0.7)
        m = material("ghost", col, alpha=0.6) if self.ghost else material("cloth", col)
        prof = []
        for i in range(9):
            t = i / 8
            prof.append((1.5 - t * 1.34, 0.21 + 0.1 * t ** 1.2))
        cape = lathe(f"{self.name}.cape", prof, 24, 1.15, 0.5, m, arc=0.5)
        cape.rotation_euler.z = math.pi  # half-cylinder behind the figure (-Y)
        cape.modifiers.new("thick", "SOLIDIFY").thickness = 0.015
        r.attach(cape, "chest", (0, -0.04, 0), (0, 0, 180))
        clasp = sphere(f"{self.name}.clasp", 0.03, material("gold", "#E6C14A"))
        r.attach(clasp, "chest", (0.17, 0.12, 1.47))
        r.attach(sphere(f"{self.name}.clasp2", 0.03, material("gold", "#E6C14A")), "chest", (-0.17, 0.12, 1.47))

    # ---- heads
    def build_head(self):
        r = self.rig
        head = self.look.get("head", "hood")
        hz = 1.72
        skin, steel, body, trim = self.m_skin(), self.m_steel(), self.m_body(), self.m_trim()
        def add(ob, at, rot=(0, 0, 0)): return r.attach(ob, "head", at, rot)
        def face(beard=False, bald=False, tonsure=False):
            add(sphere(f"{self.name}.skull", 0.105, skin, 0.92, 1.0, 1.12), (0, 0.0, hz))
            add(sphere(f"{self.name}.nose", 0.022, skin, 0.8, 1.2, 1.1), (0, 0.1, hz - 0.01))
            for x in (-0.035, 0.035):
                add(sphere(f"{self.name}.eye{x}", 0.012, self.m_eye() if self.undead else material("dark")), (x, 0.088, hz + 0.02))
            hair = material("hair", "#3A2A1E" if not self.undead else "#5A5A50")
            if beard:
                add(sphere(f"{self.name}.beard", 0.08, hair, 0.95, 0.75, 0.9), (0, 0.055, hz - 0.08))
            if tonsure:
                add(lathe(f"{self.name}.tonsure", [(0.02, 0.112), (0.08, 0.1), (0.1, 0.0)], 18, 0.95, 1.02, hair), (0, -0.0, hz), (0, 0, 0))
            elif not bald:
                add(sphere(f"{self.name}.hair", 0.112, hair, 0.95, 1.02, 0.95), (0, -0.015, hz + 0.025))
        if head in ("bare", "bare_beard", "tonsure"):
            face(beard=(head == "bare_beard"), tonsure=(head == "tonsure"))
        elif head == "skull" or head == "skull_mitre":
            bone = material("bone", "#D8D0BC")
            add(sphere(f"{self.name}.skull", 0.1, bone, 0.9, 1.0, 1.1), (0, 0, hz))
            add(box(f"{self.name}.jaw", 0.11, 0.08, 0.05, bone, bevel=0.01), (0, 0.04, hz - 0.1))
            for x in (-0.035, 0.035):
                add(sphere(f"{self.name}.socket{x}", 0.024, material("dark")), (x, 0.08, hz + 0.01))
                add(sphere(f"{self.name}.eye{x}", 0.01, self.m_eye()), (x, 0.095, hz + 0.01))
            if head == "skull_mitre": self.mitre(add, hz + 0.06)
        elif head in ("hood", "hood_black", "hood_white", "wraith", "executioner", "veil"):
            col = {"hood_black": "#141214", "hood_white": "#F0ECE2"}.get(head, None)
            m = (material("cloth", col) if col and not self.ghost and not self.stone else body)
            if head == "veil": m = material("cloth", "#E8E2D2") if not self.stone else body
            if head in ("hood", "hood_black", "hood_white", "veil") and self.dress != "robe" and self.dress != "gown" and head != "veil":
                pass
            if head == "veil":
                add(sphere(f"{self.name}.veilback", 0.128, m, 1.0, 0.9, 1.05), (0, -0.045, hz + 0.01))
                add(lathe(f"{self.name}.veilfall", [(0.0, 0.11), (-0.18, 0.15), (-0.26, 0.16)], 20, 1.0, 0.8, m, arc=0.5), (0, -0.04, hz), (0, 0, 180))
                face(bald=True)
                add(sphere(f"{self.name}.wimple", 0.095, m, 1.05, 0.85, 0.7), (0, 0.0, hz - 0.1))
                add(lathe(f"{self.name}.circlet", [(0.0, 0.124), (0.02, 0.124)], 20, 0.95, 1.0, material("gold", "#E6C14A")), (0, -0.01, hz + 0.05))
                return
            hood = lathe(f"{self.name}.hood", [(-0.12, 0.0), (-0.115, 0.1), (-0.06, 0.135), (0.02, 0.14), (0.1, 0.12),
                                                (0.16, 0.07) if head != "executioner" else (0.2, 0.05), (0.19 if head != "executioner" else 0.3, 0.0)],
                         22, 0.95, 1.08, m)
            add(hood, (0, -0.02, hz))
            if head == "veil":
                face()
                add(sphere(f"{self.name}.wimple", 0.1, m, 1.05, 0.9, 0.75), (0, 0.0, hz - 0.1))
                add(lathe(f"{self.name}.circlet", [(0.0, 0.118), (0.02, 0.118)], 20, 0.95, 1.06, material("gold", "#E6C14A")), (0, -0.01, hz + 0.06))
                return
            # Shadowed face opening.
            add(sphere(f"{self.name}.face", 0.088, material("dark"), 1.0, 0.45, 1.05), (0, 0.118, hz - 0.01))
            if head in ("wraith",) or self.undead:
                for x in (-0.03, 0.03): add(sphere(f"{self.name}.eye{x}", 0.013, self.m_eye()), (x, 0.15, hz + 0.01))
            elif head == "executioner":
                for x in (-0.035, 0.035): add(sphere(f"{self.name}.eye{x}", 0.012, material("glow", "#E8C8A0", power=1.5)), (x, 0.15, hz + 0.01))
            else:
                add(sphere(f"{self.name}.chin", 0.062, skin, 1, 0.6, 0.75), (0, 0.1, hz - 0.045))
                add(sphere(f"{self.name}.nose", 0.02, skin, 0.8, 1.2, 1.1), (0, 0.15, hz - 0.0))
            if head == "executioner":
                add(lathe(f"{self.name}.mantle", [(-0.2, 0.25), (-0.1, 0.16), (0.0, 0.0)], 20, 1.0, 0.85, m), (0, 0, hz - 0.08))
        elif head in ("greathelm", "greathelm_plume", "crownhelm"):
            helm = lathe(f"{self.name}.greathelm", [(-0.14, 0.0), (-0.14, 0.118), (0.0, 0.124), (0.1, 0.122), (0.14, 0.1), (0.155, 0.0)],
                         22, 1.0, 1.05, steel)
            add(helm, (0, 0, hz))
            add(box(f"{self.name}.visor", 0.17, 0.02, 0.018, material("dark")), (0, 0.124, hz + 0.03))
            add(box(f"{self.name}.nasal", 0.03, 0.02, 0.2, self.m_trim_solid() if is_gold(self.trim_hex) else steel, bevel=0.004), (0, 0.128, hz - 0.02))
            for zz in (-0.06, -0.09):
                for x in (-0.05, 0.05):
                    add(sphere(f"{self.name}.breath{x}{zz}", 0.008, material("dark")), (x, 0.126, hz + zz))
            if head == "greathelm_plume":
                pl = material("cloth", self.trim_hex if not is_gold(self.trim_hex) else "#B3241E")
                for i in range(5):
                    add(sphere(f"{self.name}.plume{i}", 0.05 - i * 0.004, pl, 0.6, 1.0, 1.6), (0, -0.02 - i * 0.05, hz + 0.19 - i * 0.03), (20 + i * 12, 0, 0))
            if head == "crownhelm": self.crown(add, hz + 0.13, 0.13)
        elif head == "helm":
            helm = lathe(f"{self.name}.helm", [(-0.02, 0.0), (-0.02, 0.118), (0.06, 0.112), (0.13, 0.06), (0.19, 0.0)], 22, 1, 1.05, steel)
            face()
            add(helm, (0, 0, hz))
            add(box(f"{self.name}.nasal", 0.022, 0.015, 0.11, steel, bevel=0.004), (0, 0.112, hz - 0.03))
            add(lathe(f"{self.name}.aventail", [(-0.02, 0.122), (-0.16, 0.14), (-0.2, 0.13)], 22, 1, 1.05, self.m_chain(), arc=0.7), (0, 0, hz), (0, 0, 144))
        elif head == "kettle":
            face()
            add(lathe(f"{self.name}.coif", [(-0.18, 0.13), (-0.05, 0.122), (0.08, 0.115), (0.13, 0.0)], 22, 1, 1.05, self.m_chain()), (0, -0.01, hz))
            add(sphere(f"{self.name}.faceopen", 0.07, skin, 1, 0.5, 1.1), (0, 0.085, hz - 0.01))
            add(lathe(f"{self.name}.kettle", [(0.0, 0.2), (0.015, 0.2), (0.03, 0.125), (0.1, 0.11), (0.16, 0.05), (0.18, 0.0)], 24, 1, 1, steel), (0, 0, hz + 0.04))
        elif head == "coif":
            face()
            add(lathe(f"{self.name}.coif", [(-0.2, 0.15), (-0.06, 0.124), (0.06, 0.12), (0.12, 0.08), (0.135, 0.0)], 22, 1, 1.05, self.m_chain()), (0, -0.012, hz))
            add(sphere(f"{self.name}.faceopen", 0.072, skin, 1, 0.5, 1.1), (0, 0.088, hz - 0.01))
            add(box(f"{self.name}.brow", 0.12, 0.02, 0.02, self.m_chain()), (0, 0.1, hz + 0.05))
        elif head == "mitre":
            face(); self.mitre(add, hz + 0.08)
        elif head == "tiara":
            face()
            gold = material("gold", "#E6C14A")
            t = lathe(f"{self.name}.tiara", [(0, 0.11), (0.1, 0.1), (0.2, 0.07), (0.27, 0.03), (0.3, 0.0)], 22, 1, 1, material("cloth", "#F0ECE2"))
            add(t, (0, 0, hz + 0.06))
            for i, zz in enumerate((0.07, 0.15, 0.22)):
                add(lathe(f"{self.name}.tiaraband{i}", [(0, 0.112 - i * 0.022), (0.03, 0.112 - i * 0.022)], 22, 1, 1, gold), (0, 0, hz + zz))
            add(sphere(f"{self.name}.tiaraorb", 0.025, gold), (0, 0, hz + 0.37))
        elif head == "cardinal":
            face()
            red = material("cloth", "#A01818")
            add(lathe(f"{self.name}.galero", [(0.0, 0.0), (0.0, 0.26), (0.015, 0.26), (0.02, 0.12), (0.09, 0.1), (0.11, 0.0)], 28, 1, 1, red), (0, 0, hz + 0.08))
            add(box(f"{self.name}.tassel", 0.02, 0.02, 0.3, red), (0.2, 0, hz - 0.1))
            add(box(f"{self.name}.tassel2", 0.02, 0.02, 0.3, red), (-0.2, 0, hz - 0.1))
        elif head == "hat":
            face(beard=self.boss and self.weapon != "seal")
            hm = material("cloth", darker(self.body_hex, 0.75)) if not self.stone else body
            add(lathe(f"{self.name}.hat", [(0.0, 0.0), (0.0, 0.17), (0.03, 0.17), (0.04, 0.12), (0.12, 0.13), (0.15, 0.06), (0.16, 0.0)], 24, 1, 1, hm), (0, 0, hz + 0.06))
            add(lathe(f"{self.name}.hatband", [(0.04, 0.122), (0.07, 0.126)], 24, 1, 1, trim), (0, 0, hz + 0.06))
        elif head == "feathered":
            face(beard=True)
            hm = material("cloth", "#2E4A22")
            add(lathe(f"{self.name}.hat", [(0.0, 0.0), (0.0, 0.16), (0.03, 0.14), (0.12, 0.1), (0.16, 0.0)], 24, 1, 1.3, hm), (0, 0, hz + 0.06), (0, 0, 0))
            for i in range(4):
                add(sphere(f"{self.name}.feather{i}", 0.03, material("cloth", "#F2D25A" if i % 2 else "#B3241E"), 0.4, 1.0, 2.6),
                    (0.1, -0.06 - i * 0.02, hz + 0.18 + i * 0.03), (-40 - i * 8, 0, 0))
        elif head == "crown":
            face(beard=True)
            self.crown(add, hz + 0.08, 0.118)
        elif head == "stone":
            st = self.m_body()
            add(sphere(f"{self.name}.skull", 0.11, st, 0.92, 1.0, 1.15), (0, 0, hz))
            add(sphere(f"{self.name}.nose", 0.025, st, 0.8, 1.2, 1.2), (0, 0.1, hz))
            for x in (-0.035, 0.035): add(sphere(f"{self.name}.eye{x}", 0.013, self.m_eye()), (x, 0.093, hz + 0.02))
            add(sphere(f"{self.name}.veilstone", 0.13, st, 1.0, 1.0, 1.0), (0, -0.03, hz + 0.03))
        else:
            face()

    def mitre(self, add, z):
        white = material("cloth", "#F0ECE2") if not self.stone else self.m_body()
        gold = material("gold", "#E6C14A")
        m = lathe(f"{self.name}.mitre", [(0, 0.11), (0.12, 0.105), (0.22, 0.07), (0.3, 0.0)], 20, 1.0, 0.55, white)
        add(m, (0, 0, z))
        add(box(f"{self.name}.mitreband", 0.03, 0.135, 0.3, gold), (0, 0, z + 0.13))
        add(lathe(f"{self.name}.mitrebase", [(0, 0.113), (0.03, 0.113)], 20, 1, 0.6, gold), (0, 0, z))

    def crown(self, add, z, r):
        gold = material("gold", "#E6C14A")
        add(lathe(f"{self.name}.crownband", [(0, r), (0.05, r * 1.04)], 24, 1, 1.05, gold), (0, 0, z))
        for i in range(8):
            a = i / 8 * 2 * math.pi
            add(box(f"{self.name}.crownpt{i}", 0.03, 0.012, 0.06, gold, taper=0.2), (math.cos(a) * r * 1.02, math.sin(a) * r * 1.07, z + 0.07), (0, 0, math.degrees(a) + 90))
            if i % 2 == 0:
                add(sphere(f"{self.name}.gem{i}", 0.012, material("glow", "#C02020", power=0.8)), (math.cos(a) * r * 1.06, math.sin(a) * r * 1.1, z + 0.025))

    # ---- weapons
    def build_weapon(self):
        w = self.weapon
        if w == "none": return
        steel = self.m_steel() if not self.stone else self.m_body()
        wood = material("wood", "#6A4628") if not self.stone else self.m_body()
        leather = self.m_leather("#3A2418")
        gold = material("gold", "#D9B23A") if not self.stone else self.m_body()
        parts: list[tuple] = []  # (object, offset along weapon axis z, rot)
        hand = "hand.R"
        def P(ob, z=0.0, x=0.0, y=0.0, rot=(0, 0, 0)): parts.append((ob, Vector((x, y, z)), rot)); return ob
        n = self.name
        if w in ("sword", "greatsword", "dagger"):
            L = {"sword": 0.78, "greatsword": 1.12, "dagger": 0.3}[w]
            bl = solid_from_outline(f"{n}.blade", [(-0.028, 0), (0.028, 0), (0.024, L * 0.85), (0.0, L), (-0.024, L * 0.85)], 0.012, steel, 0.003)
            P(bl, 0.08)
            P(box(f"{n}.fuller", 0.012, 0.013, L * 0.6, material("metal", "#8A909A", rough=0.4)), 0.08 + L * 0.35)
            P(box(f"{n}.guard", 0.2 if w != "dagger" else 0.1, 0.03, 0.025, gold if self.boss else steel, bevel=0.006), 0.07)
            P(tube(f"{n}.grip", 0.018, 0.018, 0.14 if w != "greatsword" else 0.24, leather), 0.06)
            P(sphere(f"{n}.pommel", 0.03, gold if self.boss else steel), -0.1 if w != "greatsword" else -0.2)
        elif w in ("axe", "greataxe"):
            L = 0.75 if w == "axe" else 1.25
            P(tube(f"{n}.haft", 0.022, 0.02, L, wood), L - 0.15)
            hb = solid_from_outline(f"{n}.axehead", [(0.0, -0.06), (0.06, -0.05), (0.22, -0.13), (0.24, 0.0), (0.22, 0.13), (0.06, 0.05), (0.0, 0.06)], 0.02, steel, 0.004)
            if w == "greataxe": hb.scale = (1.4, 1, 1.5)
            P(hb, L - 0.22)
        elif w in ("club",):
            P(lathe(f"{n}.club", [(0, 0.0), (0.0, 0.025), (0.35, 0.04), (0.55, 0.065), (0.62, 0.05), (0.64, 0.0)], 12, 1, 1, wood), -0.05)
            for i in range(5):
                P(box(f"{n}.stud{i}", 0.02, 0.02, 0.02, steel, taper=0.2), 0.45 + 0.03 * i, math.cos(i * 1.3) * 0.06, math.sin(i * 1.3) * 0.06, (0, 90, i * 70))
        elif w in ("mace",):
            P(tube(f"{n}.haft", 0.02, 0.02, 0.6, wood), 0.5)
            for i in range(6):
                a = i / 6 * 2 * math.pi
                P(box(f"{n}.flange{i}", 0.09, 0.012, 0.12, steel, bevel=0.003), 0.56, math.cos(a) * 0.04, math.sin(a) * 0.04, (0, 0, math.degrees(a)))
            P(sphere(f"{n}.macecore", 0.04, steel), 0.56)
        elif w in ("maul", "hammer"):
            L = 1.0 if w == "maul" else 0.45
            P(tube(f"{n}.haft", 0.024, 0.022, L, wood), L - 0.1)
            hz = (0.32, 0.14, 0.14) if w == "maul" else (0.16, 0.07, 0.07)
            P(box(f"{n}.head", *hz, steel, bevel=0.01), L - 0.12, 0, 0, (0, 90, 0))
        elif w in ("spear", "halberd", "oar", "staff", "staff_orb", "crozier", "torch"):
            L = {"spear": 1.9, "halberd": 1.95, "oar": 1.7, "staff": 1.75, "staff_orb": 1.7, "crozier": 1.85, "torch": 0.55}[w]
            low = {"spear": 0.6, "halberd": 0.6, "oar": 0.55, "staff": 0.75, "staff_orb": 0.75, "crozier": 0.75, "torch": 0.05}[w]
            P(tube(f"{n}.shaft", 0.02, 0.018, L, wood if w not in ("crozier",) else gold), L - low)
            top = L - low
            if w == "spear":
                P(solid_from_outline(f"{n}.tip", [(-0.035, 0), (0.035, 0), (0.0, 0.26)], 0.014, steel, 0.003), top)
            elif w == "halberd":
                P(solid_from_outline(f"{n}.tip", [(-0.025, 0), (0.025, 0), (0.0, 0.3)], 0.014, steel), top)
                P(solid_from_outline(f"{n}.blade", [(0.0, -0.18), (0.2, -0.26), (0.24, -0.02), (0.2, 0.1), (0.0, 0.0)], 0.016, steel, 0.003), top - 0.05)
                P(solid_from_outline(f"{n}.hook", [(0.0, -0.06), (-0.16, 0.02), (0.0, 0.0)], 0.014, steel), top - 0.12)
            elif w == "oar":
                P(lathe(f"{n}.blade", [(0, 0.0), (0.02, 0.06), (0.4, 0.09), (0.45, 0.0)], 12, 1.0, 0.25, wood), -low - 0.4)
            elif w == "staff":
                P(box(f"{n}.crossv", 0.03, 0.03, 0.22, gold, bevel=0.006), top + 0.08)
                P(box(f"{n}.crossh", 0.15, 0.03, 0.03, gold, bevel=0.006), top + 0.12)
            elif w == "staff_orb":
                P(sphere(f"{n}.orb", 0.065, material("glow", "#7EC8FF", power=2.5)), top + 0.08)
                for i in range(3):
                    a = i / 3 * 2 * math.pi
                    P(tube(f"{n}.prong{i}", 0.01, 0.006, 0.16, gold), top + 0.18, math.cos(a) * 0.06, math.sin(a) * 0.06, (math.sin(a) * 15, -math.cos(a) * 15, 0))
            elif w == "crozier":
                curl = []
                for i in range(24):
                    a = i / 23 * 1.75 * math.pi
                    rr = 0.13 * (1 - i / 40)
                    curl.append((0.0, 0.13 - math.cos(a) * rr, top + 0.02 + math.sin(a) * rr + (0.1 if i == 0 else 0.1)))
                P(curve_tube(f"{n}.curl", [(0.0, 0.0, top)] + curl, 0.024, gold, 0.5))
            elif w == "torch":
                P(lathe(f"{n}.flame", [(0, 0.0), (0.0, 0.06), (0.1, 0.05), (0.22, 0.0)], 12, 1, 1, material("glow", "#FF9A3A", power=12)), top)
                P(lathe(f"{n}.wrap", [(0, 0.035), (0.06, 0.035)], 12, 1, 1, leather), top - 0.05)
        elif w == "bow":
            hand = "hand.L"
            arc = [(0.0, -0.12 * (1 - t * t), t * 0.72) for t in [i / 12 * 2 - 1 for i in range(13)]]
            P(curve_tube(f"{n}.bow", arc, 0.016, wood, 0.6))
            P(curve_tube(f"{n}.string", [(0.0, -0.0, -0.72), (0.0, 0.0, 0.72)], 0.003, material("paper", "#E8E2D2")))
            P(box(f"{n}.bowgrip", 0.035, 0.035, 0.12, leather), 0.0, 0, -0.12)
        elif w == "crossbow":
            P(box(f"{n}.stock", 0.05, 0.6, 0.06, wood, bevel=0.01), 0.0, 0, 0.2)
            P(lathe(f"{n}.prod", [(-0.24, 0.018), (0.0, 0.024), (0.24, 0.018)], 10, 1, 1, material("metal", "#6A6E76", rough=0.45)), 0.0, 0.06, 0.48, (0, 90, 0))
            P(box(f"{n}.bolt", 0.012, 0.4, 0.012, wood), 0.04, 0, 0.3)
        elif w == "book":
            hand = "hand.L"
            cov = material("leather", "#5A1A16" if not self.boss else "#3A1A10")
            P(box(f"{n}.book", 0.2, 0.06, 0.26, cov, bevel=0.008), 0.0, 0.0, 0.06)
            P(box(f"{n}.pages", 0.19, 0.05, 0.245, material("paper", "#EDE3C8")), 0.0, 0.005, 0.06)
            P(box(f"{n}.clasp", 0.08, 0.065, 0.03, gold), 0.0, 0.0, 0.06)
        elif w == "scroll":
            hand = "hand.L"
            P(lathe(f"{n}.scroll", [(-0.14, 0.035), (0.14, 0.035)], 14, 1, 1, material("paper", "#EDE3C8")), 0.0, 0, 0.06, (0, 90, 0))
            P(box(f"{n}.sheet", 0.26, 0.004, 0.3, material("paper", "#EDE3C8")), -0.17, 0, 0.08)
        elif w == "seal":
            P(lathe(f"{n}.seal", [(0, 0.0), (0.0, 0.05), (0.03, 0.05), (0.05, 0.02), (0.14, 0.02), (0.16, 0.0)], 16, 1, 1, gold), -0.05)
        elif w == "keys_ring":
            P(lathe(f"{n}.ring", [(-0.01, 0.07), (0.01, 0.07), (0.01, 0.055), (-0.01, 0.055)], 20, 1, 1, steel), 0.0, 0, 0, (90, 0, 0))
            for i in range(5):
                a = -0.6 + i * 0.3
                P(box(f"{n}.key{i}", 0.012, 0.012, 0.18, steel), -0.13, math.sin(a) * 0.1, 0, (0, math.degrees(a), 0))
        elif w == "whip":
            P(tube(f"{n}.handle", 0.02, 0.018, 0.22, leather), 0.12)
            lash = [(0.0, -0.05 * i * i * 0.12, 0.16 + i * 0.09 - 0.004 * i * i) for i in range(10)]
            P(curve_tube(f"{n}.lash", lash, 0.011, leather, 0.35))
        self.weapon_parts = []
        # Weapons are held in a fist; the weapon axis (+Z) points forward and up from the hand.
        r = self.rig
        r.joint("grip", hand, r.pos[hand] + Vector((0, 0.02, -0.07)))
        grip_rot = {"hand.R": (28, 0, 0), "hand.L": (28, 0, 0)}[hand]
        if w in ("spear", "halberd", "staff", "staff_orb", "crozier", "oar"): grip_rot = (-30, 0, 0)
        if w in ("maul", "greataxe", "greatsword"): grip_rot = (10, 0, -20)
        if w == "torch": grip_rot = (40, 0, 0)
        if w in ("book", "scroll"): grip_rot = (0, 0, 0)
        if w == "bow": grip_rot = (-10, 0, 0)
        if w == "crossbow": grip_rot = (0, 0, 0)
        if w == "keys_ring": grip_rot = (0, 0, 0)
        if w == "seal": grip_rot = (180, 0, 0)
        gm = Euler([math.radians(a) for a in grip_rot], "XYZ").to_matrix().to_4x4()
        for ob, off, rot in parts:
            local = gm @ Matrix.Translation(off) @ Euler([math.radians(a) for a in rot], "XYZ").to_matrix().to_4x4()
            ob.parent = r.j["grip"]
            ob.matrix_basis = local @ ob.matrix_basis
            r.objects.append(ob)
            self.weapon_parts.append(ob)
        if w == "torch":
            light = bpy.data.lights.new(f"{n}.torchlight", "POINT"); light.energy = 25; light.color = (1.0, 0.6, 0.25)
            lo = bpy.data.objects.new(f"{n}.torchlight", light); bpy.context.scene.collection.objects.link(lo)
            lo.parent = r.j["grip"]; lo.location = gm @ Vector((0, 0, 0.6))
            r.objects.append(lo)
        if w == "staff_orb":
            light = bpy.data.lights.new(f"{n}.orblight", "POINT"); light.energy = 6; light.color = (0.5, 0.75, 1.0)
            lo = bpy.data.objects.new(f"{n}.orblight", light); bpy.context.scene.collection.objects.link(lo)
            lo.parent = r.j["grip"]; lo.location = gm @ Vector((0, 0, 1.05))
            r.objects.append(lo)

    def build_shield(self):
        r = self.rig
        pts = []
        for i in range(17):
            t = i / 16
            a = math.pi * t
            pts.append((0.24 * math.cos(a) * -1 * -1 if False else 0.24 - 0.48 * t, 0.0))
        # Heater shield outline (x, z), point at the bottom.
        outline = [(-0.24, 0.26), (0.24, 0.26)]
        for i in range(1, 12):
            t = i / 12
            outline.append((0.24 * (1 - t ** 1.6), 0.26 - 0.62 * t))
        outline.append((0.0, -0.36))
        for i in range(11, 0, -1):
            t = i / 12
            outline.append((-0.24 * (1 - t ** 1.6), 0.26 - 0.62 * t))
        face_col = self.body_hex
        em = self.look.get("emblem")
        if em in ("cross", "black_cross"): face_col = "#F0ECE2" if em == "cross" else "#E6E2D8"
        fm = material("stone", self.body_hex) if self.stone else material("leather", face_col)
        sh = solid_from_outline(f"{self.name}.shield", outline, 0.035, fm, 0.006)
        rim = solid_from_outline(f"{self.name}.shieldrim", [(x * 1.05, z * 1.04 + 0.0) for x, z in outline], 0.025, self.m_trim_solid(), 0.004)
        r.joint("shieldmount", "elbow.L", r.pos["elbow.L"] + Vector((-0.08, 0.06, -0.13)))
        for ob in (rim, sh):
            ob.parent = r.j["shieldmount"]
            ob.rotation_euler = Euler((math.radians(-6), 0, math.radians(-28)), "XYZ")
            ob.location = (0, 0, 0)
            r.objects.append(ob)
        sh.location = Vector((0, 0, 0)) + (Euler((0, 0, math.radians(-28))).to_matrix() @ Vector((0, 0.008, 0)))
        if em:
            name, col = em, self.trim_hex
            if em == "cross": col = "#B3241E"
            elif em == "black_cross": name, col = "cross", "#141414"
            elif em == "cross_gold": name, col = "cross", "#E6C14A"
            elif em == "cross_white": name, col = "cross", "#F0ECE2"
            elif em in ("fleur", "keys", "eye"): col = "#E6C14A"
            pl = plane(f"{self.name}.shielddecal", 0.32, 0.32, decal_material(name, col))
            pl.parent = r.j["shieldmount"]
            pl.matrix_basis = Euler((math.radians(-6), 0, math.radians(-28))).to_matrix().to_4x4() @ Matrix.Translation((0, -0.03, 0.04)) @ Matrix.Rotation(math.pi, 4, "Z")
            r.objects.append(pl)
        self.shield_obj = sh

# ----------------------------------------------------------------------------------------------- non-humanoids
class Beast:
    """Quadrupeds: dogs and wolves, boars, and the pale crawlers."""

    def __init__(self, name, look):
        self.name, self.look = name, look
        self.rig = Rig(name)
        n = name.lower()
        self.kind = look.get("beast") or ("boar" if "boar" in n else "dog" if any(w in n for w in ("hound", "wolf", "mastiff")) else "crawler")
        if self.kind in ("cat", "deer"): self.variant, self.kind = self.kind, "dog"
        else: self.variant = self.kind
        self.style = "bite"
        self.floating = False

    def build(self):
        r, n, look = self.rig, self.name, self.look
        col = look.get("body", "#6A5038")
        undead = look.get("undead", False)
        fur = material("skin", col) if self.kind == "crawler" else material("fur", col)
        dark = material("fur", darker(col, 0.6)) if self.kind != "crawler" else material("skin", darker(col, 0.8))
        eye = material("glow", look.get("eyes", "#E0463C" if self.kind != "crawler" else "#9FE0C0"), power=8 if undead or self.kind == "crawler" else 2)
        sc = look.get("scale") or (1.25 if look.get("big") else 1.0)
        if self.kind == "boar": L, H, W = 0.95, 0.5, 0.26
        elif self.kind == "crawler": L, H, W = 0.95, 0.3, 0.26
        else: L, H, W = 0.95, 0.46, 0.2
        if self.variant == "deer": L, H, W = 0.9, 0.7, 0.17
        if self.variant == "cat": L, H, W = 0.85, 0.36, 0.17
        L, H, W = L * sc, H * sc, W * sc
        r.joint("body", "root", (0, 0, H))
        r.joint("neck", "body", (0, L * 0.45, H + 0.06 * sc))
        r.joint("head", "neck", (0, L * 0.62, H + (0.14 if self.kind == "dog" else 0.02) * sc))
        r.joint("tail", "body", (0, -L * 0.48, H + 0.05))
        for s, x in (("R", 1), ("L", -1)):
            for e, y in (("F", 0.33), ("B", -0.33)):
                r.joint(f"leg{e}.{s}", "body", (W * 0.8 * x, L * y, H - 0.05))
                spread = 1.6 if self.kind == "crawler" else 0.85
                kz = H * (1.25 if self.kind == "crawler" else 0.45)
                r.joint(f"knee{e}.{s}", f"leg{e}.{s}", (W * spread * x, L * y + (0.04 if e == "B" else -0.02) * (3 if self.kind == "crawler" else 1), kz))
                r.joint(f"foot{e}.{s}", f"knee{e}.{s}", (W * spread * 1.15 * x, L * y * 1.15 + (-0.02 if e == "B" else 0.0), 0.04))
        # Body.
        if self.kind == "boar":
            prof = [(-L * 0.55, 0.0), (-L * 0.5, 0.14), (-L * 0.3, 0.24), (0.0, 0.27), (L * 0.25, 0.3), (L * 0.42, 0.25), (L * 0.52, 0.0)]
        elif self.kind == "crawler":
            prof = [(-L * 0.5, 0.0), (-L * 0.45, 0.1), (-L * 0.2, 0.14), (L * 0.15, 0.16), (L * 0.4, 0.13), (L * 0.5, 0.0)]
        else:
            prof = [(-L * 0.55, 0.0), (-L * 0.5, 0.14), (-L * 0.25, 0.17), (0.0, 0.17), (L * 0.25, 0.21), (L * 0.42, 0.2), (L * 0.55, 0.0)]
        body = lathe(f"{n}.body", [(z, rr * sc) for z, rr in prof], 20, 1.0, 1.15, fur)
        body.rotation_euler = (math.radians(-90), 0, 0)
        r.attach(body, "body", (0, 0, H), (-90, 0, 0))
        if self.kind == "boar":
            for i in range(7):
                r.attach(box(f"{n}.bristle{i}", 0.03, 0.08, 0.1, dark, taper=0.2), "body", (0, L * (0.3 - i * 0.09), H + 0.3 * sc - abs(i - 2) * 0.02))
        if self.kind == "crawler":
            for i in range(6):
                r.attach(sphere(f"{n}.vert{i}", 0.035 * sc, material("bone", "#E0DCCF")), "body", (0, L * (0.35 - i * 0.13), H + 0.15 * sc))
        # Neck and head.
        a, b = r.pos["neck"], r.pos["head"]
        neck = tube(f"{n}.neckm", 0.11 * sc, 0.09 * sc, (b - a).length, fur)
        neck.rotation_euler = (-(b - a)).to_track_quat("Z", "Y").to_euler()
        neck.parent = r.j["neck"]; r.objects.append(neck)
        if self.kind == "boar":
            skull = lathe(f"{n}.skull", [(0, 0.0), (0.0, 0.14), (0.12, 0.13), (0.26, 0.08), (0.32, 0.07), (0.33, 0.0)], 18, 1.0, 1.1, fur)
        elif self.kind == "crawler":
            skull = lathe(f"{n}.skull", [(0, 0.0), (0.0, 0.1), (0.08, 0.1), (0.18, 0.06), (0.22, 0.0)], 18, 1.0, 1.0, fur)
        else:
            skull = lathe(f"{n}.skull", [(-0.06, 0.0), (-0.05, 0.11), (0.06, 0.12), (0.15, 0.08), (0.28, 0.055), (0.3, 0.0)], 18, 1.0, 1.0, fur)
        r.attach(skull, "head", r.pos["head"], (-90, 0, 0), scale=sc)
        r.attach(sphere(f"{n}.nose", 0.03 * sc, material("dark")), "head", r.pos["head"] + Vector((0, 0.28 * sc if self.kind != "crawler" else 0.21 * sc, 0.01)))
        for x in (-1, 1):
            r.attach(sphere(f"{n}.eye{x}", 0.018 * sc, eye), "head", r.pos["head"] + Vector((0.06 * x * sc, 0.11 * sc, 0.06 * sc)))
            if self.variant == "cat":
                r.attach(box(f"{n}.ear{x}", 0.07 * sc, 0.025 * sc, 0.12 * sc, dark, taper=0.05), "head", r.pos["head"] + Vector((0.06 * x * sc, 0.02, 0.12 * sc)), (0, 8 * x, 0))
            elif self.variant == "deer":
                r.attach(box(f"{n}.ear{x}", 0.09 * sc, 0.02 * sc, 0.06 * sc, dark, taper=0.3), "head", r.pos["head"] + Vector((0.1 * x * sc, 0.0, 0.1 * sc)), (0, 40 * x, 0))
                if look.get("antlers"):
                    ant = material("bone", "#D8CCB0")
                    r.attach(tube(f"{n}.ant{x}", 0.014 * sc, 0.006 * sc, 0.16 * sc, ant), "head", r.pos["head"] + Vector((0.04 * x * sc, 0.04, 0.12 * sc)), (-15, 20 * x, 0))
            elif self.kind == "dog":
                r.attach(box(f"{n}.ear{x}", 0.06 * sc, 0.03 * sc, 0.1 * sc, dark, taper=0.2), "head", r.pos["head"] + Vector((0.06 * x * sc, -0.02, 0.1 * sc)), (-15, 15 * x, 0))
            if self.kind == "boar":
                r.attach(box(f"{n}.ear{x}", 0.07 * sc, 0.03 * sc, 0.09 * sc, dark, taper=0.3), "head", r.pos["head"] + Vector((0.09 * x * sc, -0.0, 0.12 * sc)), (-30, 30 * x, 0))
            if look.get("tusk"):
                r.attach(lathe(f"{n}.tusk{x}", [(0, 0.016), (0.1, 0.0)], 8, 1, 1, material("bone", "#F0ECE2")), "head",
                         r.pos["head"] + Vector((0.07 * x * sc, 0.24 * sc, -0.03)), (-40, 25 * x, 0), scale=sc)
        if self.kind == "dog" and self.variant == "dog":
            r.attach(lathe(f"{n}.jaw", [(0, 0.0), (0.0, 0.05), (0.18, 0.03), (0.2, 0.0)], 12, 1, 0.7, fur), "head",
                     r.pos["head"] + Vector((0, 0.06, -0.06)), (-96, 0, 0), scale=sc)
        if look.get("collar"):
            trim = look.get("trim", "#3A3A40")
            r.attach(lathe(f"{n}.collar", [(0, 0.115 * sc), (0.04, 0.115 * sc)], 18, 1, 1, material("leather", trim)), "neck", r.pos["neck"] + Vector((0, 0.05, 0.03)), (-60, 0, 0))
            for i in range(6):
                a = i / 6 * 2 * math.pi
                r.attach(box(f"{n}.spike{i}", 0.015, 0.015, 0.04, material("metal", "#B4BAC4"), taper=0.1), "neck",
                         r.pos["neck"] + Vector((math.cos(a) * 0.12 * sc, 0.05 + math.sin(a) * 0.06, 0.03 + math.sin(a) * 0.1 * sc)), (0, 0, 0))
        # Legs.
        for s in ("R", "L"):
            for e in ("F", "B"):
                for j0, j1, r0, r1 in ((f"leg{e}.{s}", f"knee{e}.{s}", 0.07, 0.05), (f"knee{e}.{s}", f"foot{e}.{s}", 0.045, 0.035)):
                    a, b = r.pos[j0], r.pos[j1]
                    t = tube(f"{n}.{j0}", r0 * sc * (1.3 if self.kind == "boar" else 1), r1 * sc, (b - a).length, fur if j0.startswith("leg") else dark)
                    t.rotation_euler = (-(b - a)).to_track_quat("Z", "Y").to_euler()
                    t.parent = r.j[j0]; r.objects.append(t)
                r.attach(sphere(f"{n}.paw{e}{s}", 0.045 * sc, dark, 1, 1.4, 0.6), f"foot{e}.{s}", r.pos[f"foot{e}.{s}"] + Vector((0, 0.02, -0.01)))
        # Tail.
        tlen = {"cat": 0.5, "deer": 0.1}.get(self.variant, 0.35 if self.kind == "dog" else 0.18)
        tl = tube(f"{n}.tailm", 0.035 * sc, 0.012, tlen * sc, fur if self.kind != "crawler" else material("skin", col))
        tl.parent = r.j["tail"]; tl.rotation_euler = (math.radians(-160 if self.variant == "cat" else -130), 0, 0); r.objects.append(tl)
        return self


class Tarasque(Beast):
    """La Tarasque of the Rhone: a six-legged beast under a spiked shell, with a lion's head and a scaled tail."""

    def __init__(self, name, look):
        super().__init__(name, look)
        self.kind = self.variant = "tarasque"
        self.six = True

    def build(self):
        r, n, look = self.rig, self.name, self.look
        sc = look.get("scale", 1.0)
        L, H, W = 2.2 * sc, 0.75 * sc, 0.8 * sc
        hide = material("skin", look.get("body", "#4A5A3A"))
        belly = material("skin", look.get("belly", "#8A7A52"))
        shell = material("bone", look.get("shell", "#3A3424"))
        spike = material("bone", "#D8CCAA")
        mane = material("fur", look.get("mane", "#6A3A1A"))
        eye = material("glow", "#FF8A20", power=10)
        teeth = material("bone", "#F0E8D4")
        r.joint("body", "root", (0, 0, H))
        r.joint("neck", "body", (0, L * 0.45, H + 0.1 * sc))
        r.joint("head", "neck", (0, L * 0.68, H + 0.2 * sc))
        r.joint("jaw", "head", (0, L * 0.7, H + 0.1 * sc))
        r.joint("tail", "body", (0, -L * 0.48, H - 0.05 * sc))
        r.joint("tail2", "tail", (0, -L * 0.78, H - 0.2 * sc))
        for s, x in (("R", 1), ("L", -1)):
            for e, y in (("F", 0.3), ("M", 0.0), ("B", -0.3)):
                r.joint(f"leg{e}.{s}", "body", (W * 0.75 * x, L * y, H - 0.1 * sc))
                r.joint(f"knee{e}.{s}", f"leg{e}.{s}", (W * 1.15 * x, L * y, H * 0.45))
                r.joint(f"foot{e}.{s}", f"knee{e}.{s}", (W * 1.2 * x, L * y + 0.05 * sc, 0.06 * sc))
        # Body and belly.
        body = lathe(f"{n}.body", [(-L * 0.55, 0.0), (-L * 0.5, 0.35 * sc), (-L * 0.2, 0.55 * sc), (L * 0.15, 0.55 * sc), (L * 0.42, 0.4 * sc), (L * 0.52, 0.0)], 24, 1.35, 1.0, belly)
        r.attach(body, "body", (0, 0, H), (-90, 0, 0))
        # The shell: a dome over the back, ringed with plates and spikes.
        dome_ = lathe(f"{n}.shell", [(0.0, 0.0), (0.1 * sc, 0.62 * sc), (0.35 * sc, 0.55 * sc), (0.55 * sc, 0.32 * sc), (0.62 * sc, 0.0)], 28, 1.3, 1.0, shell)
        r.attach(dome_, "body", (0, -0.05 * sc, H + 0.12 * sc), (0, 0, 0), scale=(1.0, 1.55, 1.0))
        # Spikes in three rings over the dome, leaning outward towards the rim.
        Rx, Ry, Rz = 0.62 * sc * 1.3, 0.62 * sc * 1.55, 0.62 * sc
        top = H + 0.12 * sc
        i = 0
        for f, count in ((0.35, 4), (0.65, 7), (0.88, 10)):
            for k in range(count):
                a = (k + 0.5 * (count % 2)) / count * 2 * math.pi
                px, py = math.cos(a) * f * Rx, math.sin(a) * f * Ry - 0.05 * sc
                pz = top + Rz * math.sqrt(max(0.0, 1 - f * f)) + 0.05 * sc
                sp = lathe(f"{n}.spike{i}", [(-0.12 * sc, 0.065 * sc), (0.0, 0.06 * sc), (0.42 * sc * (1.1 - f * 0.4), 0.0)], 8, 1, 1, spike)
                tilt = Vector((math.cos(a) * f * 1.2, math.sin(a) * f * 1.2, 1.0)).normalized()
                r.attach(sp, "body", (px, py, pz - 0.05 * sc), tuple(math.degrees(v) for v in tilt.to_track_quat("X", "Z").to_euler()))
                i += 1
        # Neck, the lion's head and its mane.
        a_, b_ = r.pos["neck"], r.pos["head"]
        nk = tube(f"{n}.neckm", 0.32 * sc, 0.26 * sc, (b_ - a_).length, hide)
        nk.rotation_euler = (-(b_ - a_)).to_track_quat("Z", "Y").to_euler(); nk.parent = r.j["neck"]; r.objects.append(nk)
        skull = lathe(f"{n}.skull", [(-0.12, 0.0), (-0.1, 0.24), (0.1, 0.26), (0.3, 0.18), (0.42, 0.13), (0.46, 0.0)], 20, 1.0, 0.95, hide)
        r.attach(skull, "head", r.pos["head"], (-90, 0, 0), scale=sc * 1.5)
        r.attach(sphere(f"{n}.mane", 0.5 * sc, mane, 1.15, 0.7, 1.1), "head", r.pos["head"] + Vector((0, -0.1 * sc, 0.04 * sc)))
        for x in (-1, 1):
            r.attach(sphere(f"{n}.eye{x}", 0.06 * sc, eye), "head", r.pos["head"] + Vector((0.18 * x * sc, 0.39 * sc, 0.15 * sc)))
            r.attach(box(f"{n}.horn{x}", 0.09 * sc, 0.09 * sc, 0.34 * sc, spike, taper=0.1), "head", r.pos["head"] + Vector((0.2 * x * sc, 0.0, 0.38 * sc)), (-30, 20 * x, 0))
        jaw = lathe(f"{n}.jawm", [(0, 0.0), (0.0, 0.16), (0.3, 0.1), (0.36, 0.0)], 16, 1, 0.6, hide)
        r.attach(jaw, "jaw", r.pos["jaw"] + Vector((0, 0.05 * sc, -0.1 * sc)), (-96, 0, 0), scale=sc * 1.5)
        for i in range(6):
            x = (i - 2.5) * 0.07 * sc
            r.attach(lathe(f"{n}.tooth{i}", [(0, 0.033 * sc), (0.12 * sc, 0.0)], 6, 1, 1, teeth), "head", r.pos["head"] + Vector((x, 0.55 * sc, -0.08 * sc)), (0, 90, 0))
        # Six legs with claws.
        for s in ("R", "L"):
            for e in ("F", "M", "B"):
                for j0, j1, r0, r1 in ((f"leg{e}.{s}", f"knee{e}.{s}", 0.22, 0.17), (f"knee{e}.{s}", f"foot{e}.{s}", 0.17, 0.14)):
                    a, b = r.pos[j0], r.pos[j1]
                    t = tube(f"{n}.{j0}", r0 * sc, r1 * sc, (b - a).length, hide)
                    t.rotation_euler = (-(b - a)).to_track_quat("Z", "Y").to_euler(); t.parent = r.j[j0]; r.objects.append(t)
                r.attach(sphere(f"{n}.paw{e}{s}", 0.13 * sc, hide, 1, 1.3, 0.55), f"foot{e}.{s}", r.pos[f"foot{e}.{s}"] + Vector((0, 0.04 * sc, -0.02)))
                for c in (-1, 0, 1):
                    r.attach(lathe(f"{n}.claw{e}{s}{c}", [(0, 0.025 * sc), (0.1 * sc, 0.0)], 6, 1, 1, spike), f"foot{e}.{s}",
                             r.pos[f"foot{e}.{s}"] + Vector((c * 0.06 * sc, 0.16 * sc, -0.04)), (-80, 0, 0))
        # Tail in two parts, spiked.
        for jn, ln, r0, r1 in (("tail", 0.7, 0.24, 0.15), ("tail2", 0.7, 0.15, 0.04)):
            tl = tube(f"{n}.{jn}m", r0 * sc, r1 * sc, ln * sc, hide)
            tl.parent = r.j[jn]; tl.rotation_euler = (math.radians(-105), 0, 0); r.objects.append(tl)
            for i in range(3):
                r.attach(lathe(f"{n}.{jn}sp{i}", [(0, 0.05 * sc), (0.14 * sc, 0.0)], 6, 1, 1, spike), jn,
                         r.pos[jn] + Vector((0, -(0.12 + i * 0.2) * sc, (r0 - i * 0.03) * sc)), (0, 0, 0))
        return self

class Swarm:
    """Rat swarms and bat clouds: several small creatures moving as one."""

    def __init__(self, name, look, kind):
        self.name, self.look, self.kind = name, look, kind
        self.rig = Rig(name)
        self.style = "swarm"
        self.floating = kind == "bats"

    def build(self):
        r, n = self.rig, self.name
        col = self.look.get("body", "#5A5048")
        m = material("fur", col)
        dark = material("fur", darker(col, 0.55))
        eye = material("glow", "#E0463C", power=4)
        import random
        rng = random.Random(n)
        self.members = []
        count = 7 if self.kind == "rats" else 6
        for i in range(count):
            a = i / count * 2 * math.pi + rng.uniform(-0.3, 0.3)
            rad = rng.uniform(0.12, 0.5)
            z = 0.0 if self.kind == "rats" else rng.uniform(0.7, 1.5)
            p = Vector((math.cos(a) * rad, math.sin(a) * rad, z))
            j = r.joint(f"m{i}", "root", p, "XYZ")
            j.rotation_euler.z = rng.uniform(0, 2 * math.pi)
            self.members.append((f"m{i}", rng.uniform(0, 2 * math.pi), rng.uniform(0.8, 1.2)))
            s = rng.uniform(0.85, 1.15)
            if self.kind == "rats":
                body = sphere(f"{n}.rat{i}", 0.07 * s, m, 0.8, 1.6, 0.7)
                r.attach(body, f"m{i}", p + Vector((0, 0, 0.055 * s)))
                r.attach(sphere(f"{n}.rathead{i}", 0.04 * s, m, 0.8, 1.4, 0.8), f"m{i}", p + Vector((0, 0.12 * s, 0.05 * s)))
                for x in (-1, 1):
                    r.attach(sphere(f"{n}.rateye{i}{x}", 0.009, eye), f"m{i}", p + Vector((0.022 * x, 0.15 * s, 0.065 * s)))
                    r.attach(sphere(f"{n}.ratear{i}{x}", 0.018, dark, 1, 0.5, 1), f"m{i}", p + Vector((0.028 * x, 0.1 * s, 0.085 * s)))
                tail = tube(f"{n}.rattail{i}", 0.01, 0.004, 0.18, material("skin", "#B89088"))
                r.attach(tail, f"m{i}", p + Vector((0, -0.1 * s, 0.04)), (-80, 0, 0))
            else:
                r.attach(sphere(f"{n}.bat{i}", 0.05 * s, m, 0.9, 1.0, 1.3), f"m{i}", p)
                r.attach(sphere(f"{n}.bathead{i}", 0.035 * s, m), f"m{i}", p + Vector((0, 0.04, 0.06)))
                for x in (-1, 1):
                    r.attach(sphere(f"{n}.bateye{i}{x}", 0.008, eye), f"m{i}", p + Vector((0.015 * x, 0.07, 0.07)))
                    wj = r.joint(f"wing{i}{x}", f"m{i}", p + Vector((0.04 * x, 0, 0.02)))
                    wing = solid_from_outline(f"{n}.wing{i}{x}", [(0, 0.03), (0.18 * x, 0.06), (0.26 * x, -0.02), (0.18 * x, -0.04), (0.1 * x, -0.06), (0.0, -0.03)], 0.006, dark)
                    r.attach(wing, f"wing{i}{x}", p + Vector((0.04 * x, 0, 0.02)), (90, 0, 0))
        return self

class Crow:
    def __init__(self, name, look):
        self.name, self.look = name, look
        self.rig = Rig(name); self.style = "peck"; self.floating = True

    def build(self):
        r, n = self.rig, self.name
        m = material("fur", self.look.get("body", "#1E1C22"))
        beak = material("bone", "#3A3430")
        eye = material("glow", "#E8C040", power=2)
        r.joint("body", "root", (0, 0, 1.0))
        r.attach(sphere(f"{n}.body", 0.14, m, 0.8, 1.5, 0.8), "body", (0, 0, 1.0))
        r.attach(sphere(f"{n}.head", 0.075, m), "body", (0, 0.2, 1.08))
        r.attach(lathe(f"{n}.beak", [(0, 0.03), (0.12, 0.0)], 10, 1, 0.8, beak), "body", (0, 0.26, 1.07), (-90, 0, 0))
        for x in (-1, 1):
            r.attach(sphere(f"{n}.eye{x}", 0.012, eye), "body", (0.04 * x, 0.25, 1.11))
            r.joint(f"wing{x}", "body", (0.08 * x, 0.02, 1.04))
            outline = [(0, 0.1), (0.25 * x, 0.12), (0.5 * x, 0.04), (0.62 * x, -0.06), (0.45 * x, -0.08), (0.3 * x, -0.14), (0.15 * x, -0.1), (0.0, -0.12)]
            w = solid_from_outline(f"{n}.wing{x}", outline, 0.012, m)
            r.attach(w, f"wing{x}", (0.08 * x, 0.02, 1.04), (90, 0, 0))
        tail = solid_from_outline(f"{n}.tail", [(-0.07, 0), (0.07, 0), (0.1, -0.2), (-0.1, -0.2)], 0.01, m)
        r.attach(tail, "body", (0, -0.18, 1.0), (90, 0, 0))
        return self

class Chest:
    def __init__(self, name, look):
        self.name, self.look = name, look
        self.rig = Rig(name); self.style = "none"; self.floating = False

    def build(self):
        r, n = self.rig, self.name
        wood = material("wood", "#6A4A2A"); iron = material("metal", "#3A3A40", rough=0.5); gold = material("gold", "#E6C14A")
        r.attach(box(f"{n}.base", 0.9, 0.55, 0.45, wood, bevel=0.02), "root", (0, 0, 0.225))
        r.joint("lid", "root", (0, -0.275, 0.45))
        lid = lathe(f"{n}.lid", [(-0.45, 0.0), (-0.45, 0.275), (0.45, 0.275), (0.45, 0.0)], 16, 1.0, 1.0, wood, arc=0.5)
        r.attach(lid, "lid", (0, 0, 0.45), (0, 90, 0))
        for x in (-0.3, 0.0, 0.3):
            r.attach(box(f"{n}.band{x}", 0.06, 0.57, 0.46, iron, bevel=0.005), "root", (x, 0, 0.226))
            r.attach(lathe(f"{n}.lidband{x}", [(-0.03, 0.0), (-0.03, 0.285), (0.03, 0.285), (0.03, 0.0)], 16, 1, 1, iron, arc=0.5), "lid", (x, 0, 0.45), (0, 90, 0))
        r.attach(box(f"{n}.lock", 0.12, 0.04, 0.14, gold, bevel=0.01), "root", (0, 0.29, 0.4))
        return self

# ----------------------------------------------------------------------------------------------- animation
def key(ob, frame, rot=None, loc=None, scale=None):
    if rot is not None:
        ob.rotation_euler = Euler([math.radians(a) for a in rot], "XYZ"); ob.keyframe_insert("rotation_euler", frame=frame)
    if loc is not None:
        ob.location = loc; ob.keyframe_insert("location", frame=frame)
    if scale is not None:
        ob.scale = scale; ob.keyframe_insert("scale", frame=frame)

def clear_anim(rig: Rig):
    for nm, ob in rig.j.items():
        if nm == "turn": continue
        ob.animation_data_clear()
        ob.rotation_euler = (0, 0, 0)
    rig.root.location = (0, 0, 0)
    for name, ob in rig.j.items():
        if name not in ("turn", "root") and name in rig.pos:
            parent = ob.parent
            ob.location = rig.pos[name] - (rig.pos[[k for k, v in rig.j.items() if v == parent][0]] if parent else Vector())

def animate(fig, anim: str, frames: int) -> None:
    """Keyframes one animation over frames 1..frames (sampled one render per frame)."""
    rig = fig.rig
    clear_anim(rig)
    J = rig.j
    if isinstance(fig, Figure): _animate_humanoid(fig, anim, frames)
    elif isinstance(fig, Beast): _animate_beast(fig, anim, frames)
    elif isinstance(fig, Swarm): _animate_swarm(fig, anim, frames)
    elif isinstance(fig, Crow): _animate_crow(fig, anim, frames)
    elif isinstance(fig, Chest): _animate_chest(fig, anim, frames)
    for ob in J.values():
        if ob.animation_data and ob.animation_data.action:
            set_interp(ob.animation_data.action, "BEZIER")

def set_interp(action, mode):
    try:
        curves = action.fcurves
    except AttributeError:
        curves = []
        try:
            for layer in action.layers:
                for strip in layer.strips:
                    for bag in strip.channelbags: curves.extend(bag.fcurves)
        except Exception:
            return
    for fc in curves:
        for kp in fc.keyframe_points: kp.interpolation = mode

def _loop_keys(frames, fn):
    """Calls fn(frame, phase) for frames 1..frames+1 so cyclic motion closes cleanly."""
    for i in range(frames + 1):
        fn(1 + i, i / frames)

def _animate_humanoid(f: Figure, anim: str, n: int):
    J = f.rig.j
    two = f.weapon in TWO_HANDED
    shield = f.look.get("shield", False)
    # Rest pose (slightly bent arms; weapon arm forward).
    rest = {
        "shoulder.R": (8, 0, 6), "elbow.R": (38 if f.weapon != "none" else 12, 0, 0),
        "shoulder.L": (8, 0, -6), "elbow.L": (45 if shield or f.weapon in ("book", "scroll", "bow") else 12, 0, 0),
    }
    if two and f.weapon not in ("staff", "staff_orb", "crozier", "oar"):
        rest["shoulder.L"] = (30, 0, -18); rest["elbow.L"] = (60, 0, 25)
    if f.style == "claw": rest = {"shoulder.R": (25, 0, 10), "elbow.R": (30, 0, 0), "shoulder.L": (25, 0, -10), "elbow.L": (30, 0, 0)}
    hunch = 18 if f.style == "claw" else 0
    def pose(fr, extra: dict, root=(0, 0, 0), root_rot=(0, 0, 0)):
        for j, base in rest.items():
            e = extra.get(j, (0, 0, 0))
            key(J[j], fr, rot=tuple(a + b for a, b in zip(base, e)))
        for j in ("hips", "spine", "chest", "neck", "head", "hip.R", "hip.L", "knee.R", "knee.L", "ankle.R", "ankle.L", "hand.R", "hand.L"):
            e = extra.get(j, (0, 0, 0))
            if j == "chest": e = (e[0] + hunch, e[1], e[2])
            if j == "neck": e = (e[0] - hunch * 0.7, e[1], e[2])
            key(J[j], fr, rot=e)
        float_z = 0.15 if f.floating else 0.0
        key(J["root"], fr, rot=root_rot, loc=Vector(root) + Vector((0, 0, float_z)))

    if anim == "idle":
        def fn(fr, t):
            s = math.sin(2 * math.pi * t)
            pose(fr, {"spine": (1.5 * s, 0, 0), "chest": (1.0 * s, 0, 0), "head": (-1 * s, 0, 3 * math.sin(2 * math.pi * t + 1)),
                      "shoulder.R": (2 * s, 0, 1 * s), "shoulder.L": (-2 * s, 0, -1 * s), "elbow.R": (3 * s, 0, 0)},
                 root=(0, 0, 0.008 * s + (0.05 * s if f.floating else 0)))
        _loop_keys(n, fn)
    elif anim == "walk":
        def fn(fr, t):
            s, c = math.sin(2 * math.pi * t), math.cos(2 * math.pi * t)
            sw = 28 if not f.floating else 6
            kneeR = -max(0, -s) * 55 - 5
            kneeL = -max(0, s) * 55 - 5
            pose(fr, {"hip.R": (sw * s, 0, 0), "hip.L": (-sw * s, 0, 0), "knee.R": (kneeR if not f.floating else 0, 0, 0), "knee.L": (kneeL if not f.floating else 0, 0, 0),
                      "ankle.R": (-10 * s, 0, 0), "ankle.L": (10 * s, 0, 0),
                      "shoulder.R": (-18 * s if not two else -4 * s, 0, 0), "shoulder.L": (18 * s if not (shield or two) else 4 * s, 0, 0),
                      "spine": (4, 0, 5 * s), "chest": (0, 0, -4 * s), "head": (-3, 0, -2 * s), "hips": (0, 0, 4 * s)},
                 root=(0, 0, 0.03 * abs(c) - 0.015 + (0.04 * s if f.floating else 0)))
        _loop_keys(n, fn)
    elif anim == "attack":
        # Keyed poses at phases 0 (ready), 0.4 (wind-up), 0.55 (strike), 1 (recover).
        st = f.style
        if st == "swing":
            p = [{}, {"shoulder.R": (150, 0, 25), "elbow.R": (10, 0, 0), "spine": (-6, 0, 28), "chest": (-4, 0, 10), "shoulder.L": (30, 0, -10), "hip.R": (-10, 0, 0), "hip.L": (14, 0, 0)},
                 {"shoulder.R": (40, 0, -20), "elbow.R": (-30, 0, 0), "spine": (12, 0, -26), "chest": (6, 0, -12), "shoulder.L": (-10, 0, 0), "hip.R": (22, 0, 0), "hip.L": (-12, 0, 0), "knee.L": (-25, 0, 0)},
                 {}]
            if two:
                p[1]["shoulder.L"] = (140, 0, 10); p[2]["shoulder.L"] = (50, 0, 0)
        elif st == "thrust":
            p = [{}, {"shoulder.R": (-20, 0, 0), "shoulder.L": (10, 0, -10), "spine": (-6, 0, 18), "hip.R": (-8, 0, 0)},
                 {"shoulder.R": (60, 0, -12), "elbow.R": (-30, 0, 0), "shoulder.L": (75, 0, 10), "spine": (14, 0, -10), "hip.R": (26, 0, 0), "knee.L": (-30, 0, 0), "hip.L": (-14, 0, 0)},
                 {}]
        elif st == "shoot":
            if f.weapon == "bow":
                p = [{}, {"shoulder.L": (85, 0, -10), "elbow.L": (-40, 0, 0), "shoulder.R": (85, 0, 30), "elbow.R": (100, 0, 0), "spine": (0, 0, 20)},
                     {"shoulder.L": (85, 0, -10), "elbow.L": (-40, 0, 0), "shoulder.R": (70, 0, 50), "elbow.R": (20, 0, 0), "spine": (-4, 0, 22)}, {}]
            else:
                p = [{}, {"shoulder.R": (70, 0, 0), "elbow.R": (10, 0, 0), "shoulder.L": (70, 0, 0), "elbow.L": (40, 0, 20), "spine": (2, 0, 6)},
                     {"shoulder.R": (82, 0, 0), "elbow.R": (0, 0, 0), "shoulder.L": (78, 0, 0), "elbow.L": (40, 0, 20), "spine": (-8, 0, 6), "head": (-6, 0, 0)}, {}]
        elif st == "claw":
            p = [{}, {"shoulder.R": (130, 0, 30), "shoulder.L": (130, 0, -30), "spine": (-10, 0, 0)},
                 {"shoulder.R": (30, 0, -10), "shoulder.L": (30, 0, 10), "spine": (20, 0, 0), "hip.R": (20, 0, 0), "knee.L": (-30, 0, 0)}, {}]
        else:  # cast
            p = [{}, {"shoulder.R": (110, 0, 20), "elbow.R": (30, 0, 0), "shoulder.L": (60, 0, -10), "spine": (-8, 0, 0), "head": (-10, 0, 0)},
                 {"shoulder.R": (85, 0, -5), "elbow.R": (-5, 0, 0), "shoulder.L": (75, 0, -5), "spine": (10, 0, 0), "hip.R": (14, 0, 0)}, {}]
        phases = [0.0, 0.38, 0.56, 1.0]
        for ph, extra in zip(phases, p):
            pose(1 + round(ph * (n - 1)), extra)
    elif anim == "hit":
        pose(1, {}); pose(2, {"spine": (-14, 0, 6), "chest": (-6, 0, 0), "head": (-14, 0, 0), "shoulder.R": (-10, 0, 10), "shoulder.L": (-10, 0, -10)}, root=(0, -0.04, 0))
        pose(n, {"spine": (-3, 0, 1)})
    elif anim == "death":
        # Knees buckle, the body twists and falls backwards, settling flat.
        pose(1, {})
        pose(3, {"knee.R": (-50, 0, 0), "knee.L": (-35, 0, 0), "hip.R": (40, 0, 0), "hip.L": (30, 0, 0), "spine": (-10, 0, 10), "head": (-20, 0, 0),
                 "shoulder.R": (-20, 0, 30), "shoulder.L": (-10, 0, -30)}, root=(0, 0, -0.18))
        pose(6, {"knee.R": (-40, 0, 0), "knee.L": (-20, 0, 0), "hip.R": (30, 0, 0), "hip.L": (20, 0, 0), "spine": (-8, 0, 12), "head": (-20, 0, 15),
                 "shoulder.R": (-30, 0, 60), "shoulder.L": (-30, 0, -60)}, root=(0, 0.35, 0.02), root_rot=(55, 0, 8))
        pose(8, {"knee.R": (-15, 0, 0), "knee.L": (-5, 0, 0), "hip.R": (10, 0, 0), "hip.L": (5, 0, 0), "spine": (-4, 0, 10), "head": (-10, 0, 25),
                 "shoulder.R": (-20, 0, 85), "shoulder.L": (-15, 0, -80)}, root=(0, 0.8, 0.12), root_rot=(86, 0, 8))
        pose(n, {"knee.R": (-10, 0, 0), "knee.L": (-4, 0, 0), "hip.R": (8, 0, 0), "spine": (-4, 0, 10), "head": (-8, 0, 28),
                 "shoulder.R": (-20, 0, 88), "shoulder.L": (-15, 0, -84)}, root=(0, 0.85, 0.12), root_rot=(90, 0, 8))

def _animate_beast(b: Beast, anim: str, n: int):
    J = b.rig.j
    legs = ["legF.R", "legF.L", "legB.R", "legB.L"]
    off = {"legF.R": 0.0, "legB.L": 0.0, "legF.L": 0.5, "legB.R": 0.5}  # trot: diagonal pairs
    if getattr(b, "six", False):
        legs += ["legM.R", "legM.L"]
        off.update({"legM.L": 0.0, "legM.R": 0.5, "legF.R": 0.0, "legB.R": 0.0, "legF.L": 0.5, "legB.L": 0.5})
    def pose(fr, ex, root=(0, 0, 0), root_rot=(0, 0, 0)):
        for j in list(J):
            if j in ("turn",): continue
            if j == "root": key(J[j], fr, rot=root_rot, loc=Vector(root)); continue
            key(J[j], fr, rot=ex.get(j, (0, 0, 0)))
    if anim == "idle":
        _loop_keys(n, lambda fr, t: pose(fr, {"body": (1.5 * math.sin(2 * math.pi * t), 0, 0), "head": (4 * math.sin(2 * math.pi * t), 0, 6 * math.sin(2 * math.pi * t * 1 + 1)),
                                               "tail": (0, 0, 20 * math.sin(4 * math.pi * t))}))
    elif anim == "walk":
        def fn(fr, t):
            ex = {}
            for l in legs:
                s = math.sin(2 * math.pi * (t + off[l]))
                ex[l] = (32 * s, 0, 0)
                ex[l.replace("leg", "knee")] = ((-35 if "F" in l else 35) * max(0, -s), 0, 0)
            ex["head"] = (3 * math.sin(4 * math.pi * t), 0, 0)
            ex["tail"] = (0, 0, 15 * math.sin(2 * math.pi * t))
            pose(fr, ex, root=(0, 0, 0.03 * abs(math.sin(2 * math.pi * t))))
        _loop_keys(n, fn)
    elif anim == "attack":
        pose(1, {})
        pose(1 + round(0.38 * (n - 1)), {"body": (-10, 0, 0), "neck": (-15, 0, 0), "head": (-15, 0, 0), "legF.R": (-20, 0, 0), "legF.L": (-20, 0, 0), "legB.R": (20, 0, 0), "legB.L": (20, 0, 0)}, root=(0, -0.08, 0))
        pose(1 + round(0.56 * (n - 1)), {"body": (12, 0, 0), "neck": (20, 0, 0), "head": (15, 0, 0), "jaw": (35, 0, 0), "legF.R": (45, 0, 0), "legF.L": (40, 0, 0), "legB.R": (-30, 0, 0), "legB.L": (-30, 0, 0)}, root=(0, 0.25, 0.1))
        pose(n, {})
    elif anim == "hit":
        pose(1, {}); pose(2, {"body": (-8, 0, 8), "head": (-20, 0, 10)}, root=(0, -0.05, 0)); pose(n, {})
    elif anim == "death":
        pose(1, {})
        pose(4, {"legF.R": (30, 0, 0), "legF.L": (20, 0, 0), "head": (-10, 0, 0)}, root=(0, 0, -0.05), root_rot=(0, 30, 0))
        pose(7, {"legF.R": (40, 0, -20), "legF.L": (30, 0, 20), "legB.R": (-20, 0, -20), "legB.L": (-30, 0, 20), "head": (10, 0, 20)}, root=(0.2, 0, 0.08), root_rot=(0, 80, 0))
        pose(n, {"legF.R": (40, 0, -30), "legF.L": (30, 0, 25), "legB.R": (-20, 0, -25), "legB.L": (-30, 0, 25), "head": (15, 0, 25), "tail": (0, 0, 30)}, root=(0.22, 0, 0.1), root_rot=(0, 90, 0))

def _animate_swarm(s: Swarm, anim: str, n: int):
    J = s.rig.j
    def fn(fr, t):
        speed = {"idle": 0.4, "walk": 1.0, "attack": 1.6, "hit": 0.6, "death": 0.0}[anim]
        for name, ph, k in s.members:
            j = J[name]
            base = s.rig.pos[name]
            if anim == "death":
                fall = min(1, t * 1.6)
                z = base.z * (1 - fall) if s.kind == "bats" else 0
                key(j, fr, loc=Vector((base.x, base.y, z)), rot=(0, 180 * fall if s.kind == "rats" else 90 * fall, math.degrees(ph)))
                continue
            a = 2 * math.pi * t * speed * k + ph
            dx, dy = 0.08 * math.cos(a), 0.08 * math.sin(a * 1.3)
            hop = abs(math.sin(a * 3)) * 0.03 if s.kind == "rats" else 0.08 * math.sin(a * 2)
            lunge = 0.18 * math.sin(math.pi * t) if anim == "attack" else 0
            key(j, fr, loc=Vector((base.x + dx, base.y + dy + lunge, base.z + hop)), rot=(0, 0, math.degrees(ph + a * 0.3)))
        if s.kind == "bats":
            for jn in J:
                if jn.startswith("wing"):
                    side = 1 if jn.endswith("1") and not jn.endswith("-1") else -1
                    flap = 50 * math.sin(2 * math.pi * t * 3) if anim != "death" else 0
                    key(J[jn], fr, rot=(0, -flap * side, 0))
    loop = anim in ("idle", "walk")
    for i in range(n + (1 if loop else 0)):
        fn(1 + i, i / n if loop else i / max(1, n - 1))

def _animate_crow(c: Crow, anim: str, n: int):
    J = c.rig.j
    loop = anim in ("idle", "walk")
    for i in range(n + (1 if loop else 0)):
        t = i / n if loop else i / max(1, n - 1)
        fr = 1 + i
        flap = 55 * math.sin(2 * math.pi * t * (2 if anim != "idle" else 1))
        if anim == "death":
            key(J["root"], fr, loc=Vector((0, 0, -0.95 * min(1, t * 1.4))), rot=(0, 70 * min(1, t * 1.4), 0))
            flap = 10 * (1 - t)
        elif anim == "attack":
            dive = math.sin(math.pi * t)
            key(J["root"], fr, loc=Vector((0, 0.3 * dive, -0.45 * dive)), rot=(-35 * dive, 0, 0))
        elif anim == "hit":
            key(J["root"], fr, loc=Vector((0, -0.08 * math.sin(math.pi * t), 0.05)), rot=(15 * math.sin(math.pi * t), 0, 0))
        else:
            key(J["root"], fr, loc=Vector((0, 0, 0.06 * math.sin(2 * math.pi * t))), rot=(0, 0, 0))
        key(J["wing1"], fr, rot=(0, -flap, 0)); key(J["wing-1"], fr, rot=(0, flap, 0))

def _animate_chest(c: Chest, anim: str, n: int):
    J = c.rig.j
    for i in range(n + 1):
        key(J["lid"], 1 + i, rot=(0, 0, 0)); key(J["root"], 1 + i, loc=Vector((0, 0, 0)), rot=(0, 0, 0))

# ----------------------------------------------------------------------------------------------- scene
def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    _mats.clear()

def setup_scene(samples: int, gpu: bool, size: int, span: float):
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.samples = samples
    sc.cycles.use_denoising = True
    try: sc.cycles.denoiser = "OPENIMAGEDENOISE"
    except Exception: pass
    sc.cycles.use_adaptive_sampling = True
    sc.cycles.max_bounces = 6
    sc.cycles.transparent_max_bounces = 8
    sc.cycles.filter_width = 1.2
    sc.render.film_transparent = True
    sc.render.resolution_x = sc.render.resolution_y = size
    sc.render.resolution_percentage = 100
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGBA"
    sc.render.use_persistent_data = True
    try: sc.view_settings.view_transform = "Standard"
    except Exception: pass
    try: sc.view_settings.look = "Medium High Contrast"
    except Exception:
        try: sc.view_settings.look = "Standard - Medium High Contrast"
        except Exception: pass
    sc.render.fps = 24
    sc.view_settings.exposure = 0.3
    if gpu:
        prefs = bpy.context.preferences.addons["cycles"].preferences
        for t in ("OPTIX", "CUDA", "HIP", "METAL", "ONEAPI"):
            try:
                prefs.compute_device_type = t; prefs.refresh_devices()
                if any(d.type == t for d in prefs.devices):
                    for d in prefs.devices: d.use = d.type == t
                    sc.cycles.device = "GPU"
                    print(f"[figures] rendering on {t}")
                    break
            except Exception:
                continue
    # World.
    w = bpy.data.worlds.new("world"); sc.world = w; w.use_nodes = True
    nt = w.node_tree
    bg = nt.nodes.get("Background")
    bg.inputs[0].default_value = rgb(WORLD); bg.inputs[1].default_value = 1.0
    # Metals need something to reflect: glossy rays see a warm-over-cool studio gradient, everything else the dark world.
    tc = nt.nodes.new("ShaderNodeTexCoord"); sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    nt.links.new(tc.outputs["Generated"], sep.inputs[0])
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].position = 0.42; ramp.color_ramp.elements[0].color = (0.05, 0.05, 0.06, 1)
    ramp.color_ramp.elements[1].position = 0.75; ramp.color_ramp.elements[1].color = (1.0, 0.9, 0.78, 1)
    nt.links.new(sep.outputs["Z"], ramp.inputs[0])
    studio = nt.nodes.new("ShaderNodeBackground"); studio.inputs[1].default_value = 1.6
    nt.links.new(ramp.outputs[0], studio.inputs[0])
    lp = nt.nodes.new("ShaderNodeLightPath"); mix = nt.nodes.new("ShaderNodeMixShader")
    nt.links.new(lp.outputs["Is Glossy Ray"], mix.inputs[0])
    nt.links.new(bg.outputs[0], mix.inputs[1]); nt.links.new(studio.outputs[0], mix.inputs[2])
    out = nt.nodes.get("World Output"); nt.links.new(mix.outputs[0], out.inputs[0])
    # Camera: orthographic, pitched down, looking along +Y (figures facing -Y face the viewer).
    cam = bpy.data.cameras.new("cam"); cam.type = "ORTHO"; cam.ortho_scale = span
    co = bpy.data.objects.new("cam", cam); sc.collection.objects.link(co); sc.camera = co
    p = math.radians(CAM_PITCH)
    co.location = (0, -20 * math.cos(p), 20 * math.sin(p))
    co.rotation_euler = (math.pi / 2 - p, 0, 0)
    cam.shift_y = ANCHOR_Y - 0.5
    cam.clip_end = 100
    # Lights: warm key from the upper left front, cool fill from the right, rim from behind.
    def sun(name, energy, temp_rgb, rot, angle=0.1):
        l = bpy.data.lights.new(name, "SUN"); l.energy = energy; l.color = temp_rgb; l.angle = angle
        o = bpy.data.objects.new(name, l); sc.collection.objects.link(o)
        o.rotation_euler = Euler([math.radians(a) for a in rot], "XYZ")
        return o
    sun("key", KEY_ENERGY, (1.0, 0.82, 0.62), (40, 0, -35), 0.12)
    sun("fill", FILL_ENERGY, (0.62, 0.74, 1.0), (60, 0, 110), 0.4)
    sun("rim", RIM_ENERGY, (1.0, 0.93, 0.85), (-55, 0, 15), 0.05)
    return co

def build(name: str, look: dict):
    shape = look.get("shape", "humanoid")
    if shape == "beast": return Beast(name, look).build()
    if shape == "tarasque": return Tarasque(name, look).build()
    if shape == "swarm": return Swarm(name, look, "rats").build()
    if shape == "bats": return Swarm(name, look, "bats").build()
    if shape == "crow": return Crow(name, look).build()
    if shape == "chest": return Chest(name, look).build()
    f = Figure(name, look)
    if shape == "ghoul":
        f.style = "claw"; f.dress = "tunic"
        look = dict(look, head="bare", weapon="none")
        f.look = look
    return f.build()

# ----------------------------------------------------------------------------------------------- render + pack
def category(name: str, look: dict) -> str:
    if name.startswith("class_"): return "player"
    if name.startswith("boss_") or look.get("boss"): return "boss"
    if name.startswith("npc_"): return "npc"
    return "minor"

def frame_size(cat: str, look: dict) -> tuple[int, float]:
    """(pixels, metres) for a frame. Pixel density is the same for players and monsters so they match on screen."""
    if look.get("frame"): return int(look["frame"][0]), float(look["frame"][1])
    if cat == "player": return 160, FIGURE_SPAN
    if cat == "boss": return 192, FIGURE_SPAN * 1.2 * (1.15 if look.get("big") else 1.0)
    if cat == "npc": return 160, FIGURE_SPAN
    return 128, FIGURE_SPAN * (1.0 if not look.get("big") else 1.12) * (0.8 if look.get("shape") in ("swarm",) else 1.0)

def anims_for(cat: str, look: dict) -> dict:
    if cat == "npc" or look.get("shape") == "chest": return {"idle": ANIMS["idle"]}
    return ANIMS

def facing_rotation(angle_deg: float) -> float:
    """Figure yaw (radians) so it faces screen angle `angle_deg` (0 right, 90 down). Model faces +Y at yaw 0.
    The camera looks along +Y, so screen right is +X and screen down is -Y: yaw -(a + 90) turns +Y to (cos a, -sin a).
    (Sheets before 2026-10-31 used +(a + 90), which mirrored left and right; their JSON angles were remapped.)"""
    return math.radians(-(angle_deg + 90.0))

def render_figure(name: str, look: dict, out_dir: str, samples: int, gpu: bool, still: bool = True, scale_boss: bool = True):
    reset_scene()
    cat = category(name, look)
    px, span = frame_size(cat, look)
    setup_scene(samples, gpu, px, span)
    fig = build(name, look)
    if cat == "boss" and scale_boss:
        fig.rig.turn.scale = (1.2, 1.2, 1.2) if not look.get("big") else (1.32, 1.32, 1.32)
    elif look.get("big"):
        fig.rig.turn.scale = (1.12, 1.12, 1.12)
    sc = bpy.context.scene
    anims = anims_for(cat, look)
    dirs = dirs_for("minor" if cat in ("minor",) else "full")
    tmp = os.path.join(HERE, "_frames", name)
    os.makedirs(tmp, exist_ok=True)
    t0 = time.time()
    for anim, (frames, fps, loop) in anims.items():
        animate(fig, anim, frames)
        sc.frame_start, sc.frame_end = 1, frames
        for di, ang in enumerate(dirs):
            fig.rig.turn.rotation_euler = (0, 0, facing_rotation(ang))
            sc.render.filepath = os.path.join(tmp, f"{anim}_d{di}_")
            bpy.ops.render.render(animation=True)
    print(f"[figures] {name}: {sum(a[0] for a in anims.values()) * len(dirs)} frames in {time.time() - t0:.1f}s")
    pack(name, tmp, out_dir, px, dirs, anims, span)
    import shutil
    shutil.rmtree(tmp, ignore_errors=True)
    if still:
        render_still(fig, name, out_dir, samples, look, cat)

def render_still(fig, name, out_dir, samples, look, cat):
    """256 px portrait still: idle frame, three-quarter facing (down-right)."""
    sc = bpy.context.scene
    animate(fig, "idle", 8)
    sc.frame_set(1)
    sc.render.resolution_x = sc.render.resolution_y = 256
    sc.cycles.samples = max(samples, 64)
    fig.rig.turn.rotation_euler = (0, 0, facing_rotation(70))
    sc.render.filepath = os.path.join(out_dir, name + ".png")
    bpy.ops.render.render(write_still=True)

def _load_px(path):
    import numpy as np
    img = bpy.data.images.load(path)
    w, h = img.size
    arr = np.empty(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(arr)
    bpy.data.images.remove(img)
    return arr.reshape(h, w, 4)[::-1]  # top row first

def pack(name, frames_dir, out_dir, px, dirs, anims, metres=0.0):
    import numpy as np
    cols = max(a[0] for a in anims.values())
    rows = len(anims) * len(dirs)
    atlas = np.zeros((rows * px, cols * px, 4), dtype=np.float32)
    meta_anims = {}
    row = 0
    for anim, (frames, fps, loop) in anims.items():
        meta_anims[anim] = {"row": row, "frames": frames, "fps": fps, "loop": loop}
        if anim == "attack": meta_anims[anim]["hitFrame"] = HIT_FRAME
        for di in range(len(dirs)):
            for fr in range(frames):
                p = os.path.join(frames_dir, f"{anim}_d{di}_{fr + 1:04d}.png")
                atlas[(row + di) * px:(row + di + 1) * px, fr * px:(fr + 1) * px] = _load_px(p)
        row += len(dirs)
    H, W = atlas.shape[:2]
    img = bpy.data.images.new(name + "_sheet", W, H, alpha=True)
    img.pixels.foreach_set(atlas[::-1].ravel())
    img.filepath_raw = os.path.join(out_dir, name + "_sheet.png")
    img.file_format = "PNG"
    img.save()
    bpy.data.images.remove(img)
    meta = {"image": name + "_sheet.png", "frameWidth": px, "frameHeight": px, "directions": len(dirs),
            "directionAngles": dirs, "anchor": [px // 2, round(px * ANCHOR_Y)], "metres": round(metres, 3), "animations": meta_anims}
    with open(os.path.join(out_dir, name + ".json"), "w", newline="\n") as f:
        json.dump(meta, f, indent=1)

# ----------------------------------------------------------------------------------------------- loot icons
def render_loot(out_dir, samples, gpu):
    for name in ("loot_gold", "loot_item"):
        reset_scene()
        setup_scene(samples, gpu, 128, 1.0)
        bpy.context.scene.camera.data.shift_y = 0.12
        if name == "loot_gold":
            gold = material("gold", "#E6C14A")
            import random
            rng = random.Random(3)
            for i in range(26):
                c = lathe(f"coin{i}", [(0, 0.0), (0.0, 0.07), (0.018, 0.07), (0.018, 0.0)], 20, 1, 1, gold)
                a, rr = rng.uniform(0, 6.28), rng.uniform(0, 0.22) * (1 - i / 40)
                c.location = (math.cos(a) * rr, math.sin(a) * rr, 0.012 * (i // 6) + rng.uniform(0, 0.01))
                c.rotation_euler = (rng.uniform(-0.4, 0.4), rng.uniform(-0.4, 0.4), 0)
            bag = sphere("pouch", 0.16, material("leather", "#6A4A2A"), 1, 1, 1.1)
            bag.location = (-0.12, 0.12, 0.15)
            tie = lathe("tie", [(0, 0.06), (0.04, 0.05), (0.08, 0.08)], 14, 1, 1, material("leather", "#4A3222"))
            tie.location = (-0.12, 0.12, 0.3)
        else:
            sack = lathe("sack", [(0, 0.0), (0.02, 0.18), (0.15, 0.22), (0.3, 0.16), (0.36, 0.07), (0.42, 0.11), (0.44, 0.0)], 22, 1, 0.9, material("cloth", "#8A7A58"))
            rope = lathe("rope", [(0.34, 0.075), (0.37, 0.075)], 16, 1, 0.9, material("leather", "#4A3222"))
            hilt = box("hilt", 0.03, 0.03, 0.25, material("metal", "#B4BAC4"), bevel=0.005)
            hilt.location = (0.1, -0.02, 0.45); hilt.rotation_euler = (0.2, 0.4, 0)
            guard = box("guard", 0.14, 0.03, 0.025, material("gold", "#C9A227"), bevel=0.004)
            guard.location = (0.08, -0.02, 0.4); guard.rotation_euler = (0.2, 0.4, 0)
        sc = bpy.context.scene
        sc.render.filepath = os.path.join(out_dir, name + ".png")
        bpy.ops.render.render(write_still=True)

# ----------------------------------------------------------------------------------------------- main
def main(argv):
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--names", default="", help="comma-separated sprite names (e.g. class_knight,mon_grey_wolf)")
    ap.add_argument("--all", action="store_true")
    ap.add_argument("--prefix", default="", help="only names starting with this")
    ap.add_argument("--loot", action="store_true")
    ap.add_argument("--samples", type=int, default=48)
    ap.add_argument("--gpu", action="store_true")
    ap.add_argument("--out", default=SPRITES)
    ap.add_argument("--skip-existing", action="store_true")
    ap.add_argument("--no-still", action="store_true")
    a = ap.parse_args(argv)
    looks = json.load(open(LOOKS, encoding="utf-8"))
    names = [n for n in a.names.split(",") if n] if a.names else (sorted(looks) if a.all or a.prefix else [])
    if a.prefix: names = [n for n in names if n.startswith(a.prefix)]
    os.makedirs(a.out, exist_ok=True)
    done = 0
    for n in names:
        if n not in looks:
            print(f"[figures] no look for {n}"); continue
        if a.skip_existing and os.path.exists(os.path.join(a.out, n + "_sheet.png")): continue
        try:
            render_figure(n, looks[n], a.out, a.samples, a.gpu, still=not a.no_still)
            done += 1
        except Exception as e:
            import traceback; traceback.print_exc()
            print(f"[figures] FAILED {n}: {e}")
    if a.loot: render_loot(a.out, a.samples, a.gpu)
    print(f"[figures] done: {done} figure(s)")

if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    main(argv)
