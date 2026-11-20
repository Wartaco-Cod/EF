"""
The Templar Knights armoury: every weapon, armour, helm and shield the game drops, plus the extra arms the enemy
concepts carry (spear, war hammer, tongs, flanged mace, book, serrated greatsword), built procedurally in Blender.

Used two ways:
  * icons_items.py renders each loot item to its own inventory icon (assets/ui/icons/items/<slug>.png);
  * meshy_figures.py bolts weapons and shields to the hand bones of the Meshy enemies (render manifest "loadout").

Frames (metres):
  * hand-held things: the grip centre is at the origin and the business end points along +X; blades are broad in Z
    and thin in Y. That matches hand_weapons.attach on a Mixamo-style right hand (a held blade comes out along +X).
  * shields: the painted face looks along -X, the strap side is +X, height runs along Y (point toward +Y), width Z.
  * armour and helms (icons only): Z up, front toward -Y.
"""
from __future__ import annotations

import math
import random

import bmesh
import bpy
from mathutils import Matrix, Vector

# ----------------------------------------------------------------------------------------------- materials

def _hex(h):
    h = h.lstrip("#")
    srgb = [int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    return tuple(c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4 for c in srgb)


def mat(name, colour, metal=0.0, rough=0.5, grime=0.45, scale=30.0, bump=None, bump_scale=0.0, bump_strength=0.3):
    """Principled material with a little procedural wear: noise darkens the colour and varies the roughness.
    bump: None, 'mail' (rings), 'quilt' (vertical quilting), 'grain' (wood) or 'hammered'."""
    key = f"arm_{name}"
    m = bpy.data.materials.get(key)
    if m: return m
    m = bpy.data.materials.new(key); m.use_nodes = True
    nt = m.node_tree; N = nt.nodes; L = nt.links
    b = N["Principled BSDF"]
    base = _hex(colour) if isinstance(colour, str) else colour
    b.inputs["Metallic"].default_value = metal
    tc = N.new("ShaderNodeTexCoord")
    noise = N.new("ShaderNodeTexNoise"); noise.inputs["Scale"].default_value = scale; noise.inputs["Detail"].default_value = 6
    L.new(tc.outputs["Object"], noise.inputs["Vector"])
    ramp = N.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].position = 0.35; ramp.color_ramp.elements[0].color = (*[c * (1 - grime) for c in base], 1)
    ramp.color_ramp.elements[1].position = 0.7; ramp.color_ramp.elements[1].color = (*base, 1)
    L.new(noise.outputs["Fac"], ramp.inputs["Fac"]); L.new(ramp.outputs["Color"], b.inputs["Base Color"])
    mr = N.new("ShaderNodeMapRange")
    mr.inputs["To Min"].default_value = max(0.05, rough - 0.12); mr.inputs["To Max"].default_value = min(1.0, rough + 0.2)
    L.new(noise.outputs["Fac"], mr.inputs["Value"]); L.new(mr.outputs["Result"], b.inputs["Roughness"])
    if bump:
        if bump == "mail":
            t = N.new("ShaderNodeTexVoronoi"); t.inputs["Scale"].default_value = bump_scale or 260
            out = t.outputs["Distance"]
        elif bump == "quilt":
            t = N.new("ShaderNodeTexWave"); t.wave_type = "BANDS"; t.bands_direction = "X"
            t.inputs["Scale"].default_value = bump_scale or 2.2; t.inputs["Distortion"].default_value = 0.0
            out = t.outputs["Fac"]
        elif bump == "grain":
            t = N.new("ShaderNodeTexWave"); t.wave_type = "BANDS"; t.bands_direction = "X"
            t.inputs["Scale"].default_value = bump_scale or 3; t.inputs["Distortion"].default_value = 8
            out = t.outputs["Fac"]
        else:
            t = N.new("ShaderNodeTexVoronoi"); t.inputs["Scale"].default_value = bump_scale or 60
            out = t.outputs["Distance"]
        L.new(tc.outputs["Object"], t.inputs["Vector"])
        bn = N.new("ShaderNodeBump"); bn.inputs["Strength"].default_value = bump_strength
        L.new(out, bn.inputs["Height"]); L.new(bn.outputs["Normal"], b.inputs["Normal"])
    return m


def steel():      return mat("steel", "#A9AEB6", metal=0.95, rough=0.28, grime=0.35, scale=18)
def blackened():  return mat("black", "#3A3A3E", metal=0.9, rough=0.42, grime=0.5)
def iron():       return mat("iron", "#5B5A58", metal=0.9, rough=0.5, grime=0.55, bump="hammered", bump_strength=0.15)
def lead():       return mat("lead", "#6E7275", metal=0.7, rough=0.55, grime=0.4, bump="hammered", bump_scale=25, bump_strength=0.25)
def brass():      return mat("brass", "#B88A3A", metal=0.95, rough=0.33, grime=0.45)
def leather():    return mat("leather", "#3E2616", rough=0.7, grime=0.45, scale=60)
def red_leather():return mat("rleather", "#5A1A12", rough=0.65, grime=0.4, scale=60)
def wood():       return mat("wood", "#4A3020", rough=0.65, grime=0.4, scale=8, bump="grain", bump_strength=0.2)
def pale_wood():  return mat("pwood", "#6E5032", rough=0.6, grime=0.35, scale=8, bump="grain", bump_strength=0.2)
def mail():       return mat("mail", "#6C7076", metal=0.9, rough=0.48, grime=0.45, bump="mail", bump_strength=0.6)
def linen():      return mat("linen", "#C8BFAA", rough=0.85, grime=0.35, scale=12)
def quilt():      return mat("quilt", "#6E5A3A", rough=0.9, grime=0.35, scale=12, bump="quilt", bump_strength=0.5)
def red_cloth():  return mat("redcloth", "#8A1712", rough=0.85, grime=0.3, scale=12)
def velvet():     return mat("velvet", "#5C1420", rough=0.9, grime=0.3, scale=14)
def blue_paint(): return mat("bluep", "#24345E", rough=0.7, grime=0.45, scale=14)
def gold_paint(): return mat("goldp", "#B8902E", rough=0.55, grime=0.4, scale=14)
def white_paint():return mat("whitep", "#BDB3A0", rough=0.75, grime=0.4, scale=14)
def red_paint():  return mat("redp", "#7E1410", rough=0.7, grime=0.35, scale=14)
def dark():       return mat("dark", "#050505", rough=0.9, grime=0.0)
def parchment():  return mat("parch", "#CDBB92", rough=0.9, grime=0.3, scale=20)

# ----------------------------------------------------------------------------------------------- geometry

class Kit:
    """Collects primitives (each with a material) into one object."""

    def __init__(self, name):
        self.name, self.bm, self.mats = name, bmesh.new(), []

    def _mi(self, m):
        if m not in self.mats: self.mats.append(m)
        return self.mats.index(m)

    def put(self, bm, m, smooth=False, matrix=None):
        if matrix is not None: bmesh.ops.transform(bm, matrix=matrix, verts=bm.verts)
        idx = self._mi(m)
        for f in bm.faces:
            f.material_index = idx; f.smooth = smooth
        me = bpy.data.meshes.new("tmp"); bm.to_mesh(me); bm.free()
        self.bm.from_mesh(me); bpy.data.meshes.remove(me)
        return self

    def build(self):
        me = bpy.data.meshes.new(self.name)
        self.bm.normal_update(); self.bm.to_mesh(me); self.bm.free()
        for m in self.mats: me.materials.append(m)
        o = bpy.data.objects.new(self.name, me); bpy.context.scene.collection.objects.link(o)
        return o


def T(x=0.0, y=0.0, z=0.0): return Matrix.Translation((x, y, z))
def R(deg, axis): return Matrix.Rotation(math.radians(deg), 4, axis)
def S(x, y=None, z=None): return Matrix.Diagonal((x, y if y is not None else x, z if z is not None else x, 1))


def box(sx, sy, sz, bevel=0.0):
    bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1)
    bmesh.ops.scale(bm, vec=Vector((sx, sy, sz)), verts=bm.verts)
    if bevel > 0:
        try: bmesh.ops.bevel(bm, geom=list(bm.edges), offset=bevel, segments=2, affect="EDGES", clamp_overlap=True)
        except Exception: pass
    return bm


def lathe(profile, seg=20, ry=1.0, rz=1.0, cap=True):
    """Surface of revolution about X. profile: [(x, r)]."""
    bm = bmesh.new(); rings = []
    for x, r in profile:
        ring = [bm.verts.new((x, r * ry * math.cos(2 * math.pi * i / seg), r * rz * math.sin(2 * math.pi * i / seg))) for i in range(seg)]
        rings.append(ring)
    for a, b in zip(rings, rings[1:]):
        for i in range(seg):
            j = (i + 1) % seg
            try: bm.faces.new((a[i], a[j], b[j], b[i]))
            except ValueError: pass
    if cap:
        for ring, (x, r) in ((rings[0], profile[0]), (rings[-1], profile[-1])):
            if r > 1e-5:
                try: bm.faces.new(ring if ring is rings[-1] else list(reversed(ring)))
                except ValueError: pass
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-6)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def sphere(r, seg=16):
    bm = bmesh.new(); bmesh.ops.create_uvsphere(bm, u_segments=seg, v_segments=seg // 2 + 1, radius=r)
    return bm


def slab(pts, t, bevel=0.0):
    """Flat outline in (x, z), thickness t along Y, centred on y=0."""
    bm = bmesh.new()
    vs = [bm.verts.new((x, -t / 2, z)) for x, z in pts]
    f = bm.faces.new(vs)
    r = bmesh.ops.extrude_face_region(bm, geom=[f])
    for v in [e for e in r["geom"] if isinstance(e, bmesh.types.BMVert)]: v.co.y += t
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    if bevel > 0:
        try: bmesh.ops.bevel(bm, geom=list(bm.edges), offset=bevel, segments=1, affect="EDGES", clamp_overlap=True)
        except Exception: pass
    return bm


def blade(stations, tip, t=0.006, back=0.0):
    """Diamond (or, with back > 0, single-edged wedge) blade along +X.
    stations: [(x, z_top, z_bottom)]; tip: (x, z). Width in Z, thickness t (half) along Y.
    With back > 0 the top is a flat back of half-thickness `back` and the bottom is the edge."""
    bm = bmesh.new(); rings = []
    for x, zt, zb in stations:
        if back > 0:
            zs = zt - (zt - zb) * 0.25
            ring = [(x, back, zt), (x, t, zs), (x, 0.0, zb), (x, -t, zs), (x, -back, zt)]
        else:
            zs = (zt + zb) / 2
            ring = [(x, 0.0, zt), (x, t, zs), (x, 0.0, zb), (x, -t, zs)]
        rings.append([bm.verts.new(p) for p in ring])
    n = len(rings[0])
    for a, b in zip(rings, rings[1:]):
        for i in range(n):
            j = (i + 1) % n
            bm.faces.new((a[i], a[j], b[j], b[i]))
    tv = bm.verts.new((tip[0], 0.0, tip[1]))
    last = rings[-1]
    for i in range(n):
        j = (i + 1) % n
        try: bm.faces.new((last[i], last[j], tv))
        except ValueError: pass
    bm.faces.new(list(reversed(rings[0])))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def straight_blade(L, w, x0=0.03, taper=0.62, point=0.14, t=0.0065):
    h = w / 2
    st = [(x0, h, -h), (x0 + L * 0.45, h * 0.88, -h * 0.88), (x0 + L * (1 - point), h * taper, -h * taper)]
    return blade(st, (x0 + L, 0.0), t)

# ----------------------------------------------------------------------------------------------- hilts

def cross_guard(k, width, droop=0.0, m=None):
    m = m or blackened()
    seg = 0.5 * width
    for s in (-1, 1):
        a = math.degrees(math.atan2(droop, seg)) * s
        k.put(box(0.02, 0.026, seg, bevel=0.004), m, matrix=T(0.0, 0, s * seg / 2) @ R(-a, "Y"))
        k.put(sphere(0.013, 10), m, True, T(droop * 0.6, 0, s * width / 2))
    k.put(box(0.03, 0.032, 0.05, bevel=0.005), m, matrix=T(0, 0, 0))


def grip(k, length, r=0.016, m=None, wraps=True, x0=-0.012):
    k.put(lathe([(x0, r * 0.9), (x0 - length * 0.5, r * 1.08), (x0 - length, r * 0.92)], 14), m or leather(), True)
    if wraps:
        for i in range(1, 4):
            x = x0 - length * i / 4
            k.put(lathe([(x - 0.004, r * 1.12), (x + 0.004, r * 1.12)], 14), brass(), True)
    return x0 - length


def wheel_pommel(k, x, r=0.032, m=None):
    k.put(lathe([(-0.015, 0.0), (-0.015, r * 0.75), (-0.008, r), (0.008, r), (0.015, r * 0.75), (0.015, 0.0)], 20), m or blackened(), True,
          T(x - r * 0.8, 0, 0) @ R(90, "Z"))


def pear_pommel(k, x, r=0.026, m=None):
    k.put(lathe([(0, 0.008), (-0.01, r * 0.7), (-0.03, r), (-0.05, r * 0.8), (-0.062, 0.0)], 16), m or blackened(), True, T(x, 0, 0))


def disc(k, x, r, th=0.012, m=None):
    k.put(lathe([(-th / 2, 0.0), (-th / 2, r), (th / 2, r), (th / 2, 0.0)], 20), m or blackened(), True, T(x, 0, 0))

# ----------------------------------------------------------------------------------------------- weapons

def arming_sword():
    k = Kit("arming_sword")
    k.put(straight_blade(0.76, 0.05), steel())
    cross_guard(k, 0.2, droop=0.0)
    end = grip(k, 0.11)
    wheel_pommel(k, end)
    return k.build()


def falchion():
    k = Kit("falchion")
    st = [(0.03, 0.022, -0.022), (0.2, 0.022, -0.04), (0.4, 0.02, -0.065), (0.55, 0.018, -0.082), (0.63, 0.012, -0.075)]
    k.put(blade(st, (0.7, 0.022), t=0.006, back=0.005), steel())
    cross_guard(k, 0.17, droop=0.012)
    end = grip(k, 0.1, m=wood(), wraps=False)
    k.put(lathe([(0, 0.02), (-0.03, 0.022), (-0.04, 0.0)], 14), brass(), True, T(end, 0, 0))
    return k.build()


def messer():
    k = Kit("messer")
    st = []
    for x in (0.03, 0.18, 0.33, 0.46, 0.57, 0.64):
        lift = 0.09 * (x / 0.7) ** 2.2
        st.append((x, 0.02 + lift, -0.024 + lift * 0.8))
    k.put(blade(st, (0.72, 0.11), t=0.006, back=0.005), steel())
    cross_guard(k, 0.14)
    k.put(box(0.03, 0.012, 0.03, bevel=0.003), blackened(), matrix=T(0.0, 0.02, 0.025))  # the nagel
    end = grip(k, 0.12, m=wood(), wraps=False)
    k.put(lathe([(0, 0.02), (-0.02, 0.026), (-0.035, 0.0)], 14), blackened(), True, T(end, 0, 0) @ R(-15, "Y"))
    return k.build()


def rondel_dagger():
    k = Kit("rondel_dagger")
    k.put(blade([(0.02, 0.012, -0.012), (0.18, 0.007, -0.007)], (0.3, 0.0), t=0.007), steel())
    disc(k, 0.012, 0.034)
    end = grip(k, 0.1, r=0.014, m=wood(), wraps=False)
    disc(k, end - 0.006, 0.032)
    return k.build()


def misericorde():
    k = Kit("misericorde")
    k.put(blade([(0.02, 0.009, -0.009), (0.24, 0.005, -0.005)], (0.32, 0.0), t=0.0045), steel())
    cross_guard(k, 0.07)
    end = grip(k, 0.09, r=0.013)
    pear_pommel(k, end, r=0.017)
    return k.build()


def baselard():
    k = Kit("baselard")
    k.put(blade([(0.02, 0.02, -0.02), (0.18, 0.015, -0.015)], (0.3, 0.0), t=0.006), steel())
    k.put(box(0.022, 0.03, 0.1, bevel=0.004), blackened(), matrix=T(0.006, 0, 0))
    end = grip(k, 0.1, r=0.015, m=wood(), wraps=False)
    k.put(box(0.022, 0.03, 0.085, bevel=0.004), blackened(), matrix=T(end - 0.01, 0, 0))
    return k.build()


def longsword():
    k = Kit("longsword")
    k.put(straight_blade(0.92, 0.052, taper=0.55, point=0.2), steel())
    cross_guard(k, 0.26)
    end = grip(k, 0.22)
    pear_pommel(k, end, r=0.028)
    return k.build()


def bastard_sword():
    k = Kit("bastard_sword")
    k.put(straight_blade(0.86, 0.05, taper=0.6), steel())
    cross_guard(k, 0.24, droop=0.025)
    end = grip(k, 0.17, m=red_leather())
    wheel_pommel(k, end, r=0.034)
    return k.build()


def greatsword(serrated=False):
    k = Kit("serrated_greatsword" if serrated else "greatsword")
    w, L, x0 = 0.064, 1.2, 0.03
    # ricasso, then the blade proper with parrying lugs between
    k.put(box(0.13, 0.014, 0.034, bevel=0.003), steel(), matrix=T(x0 + 0.065, 0, 0))
    for s in (-1, 1):
        k.put(slab([(0, 0), (0.05, 0.0), (0.02, 0.035), (0.0, 0.03)], 0.01), blackened(), matrix=T(x0 + 0.13, 0, s * 0.016) @ S(1, 1, s))
    b0 = x0 + 0.13
    if serrated:
        st = []
        for i in range(12):
            x = b0 + (L - 0.15) * i / 11
            tooth = 0.014 if i % 2 else 0.0
            st.append((x, w / 2 + tooth - 0.01 * i / 11, -w / 2 + 0.012 * i / 11))
        k.put(blade(st, (b0 + L - 0.02, -0.005), t=0.008, back=0.006), blackened())
    else:
        h = w / 2
        st = [(b0, h, -h), (b0 + L * 0.5, h * 0.85, -h * 0.85), (b0 + L * 0.78, h * 0.6, -h * 0.6)]
        k.put(blade(st, (b0 + L * 0.92, 0.0), t=0.0075), steel())
    cross_guard(k, 0.38, droop=0.03)
    end = grip(k, 0.32, r=0.018, m=leather())
    pear_pommel(k, end, r=0.034)
    return k.build()


def haft(k, x0, x1, r0=0.02, r1=None, m=None, bands=()):
    r1 = r1 or r0
    k.put(lathe([(x0, r0 * 0.9), (x0 + 0.01, r0), (x1 - 0.01, r1), (x1, r1 * 0.9)], 14), m or wood(), True)
    for x in bands:
        k.put(lathe([(x - 0.012, r0 * 1.2), (x + 0.012, r0 * 1.2)], 14), iron(), True)


def great_maul():
    k = Kit("great_maul")
    haft(k, -0.45, 0.62, 0.021, m=wood(), bands=(0.45,))
    k.put(box(0.17, 0.15, 0.36, bevel=0.014),
          iron(), matrix=T(0.62, 0, 0))
    for z in (-0.12, 0.12):
        k.put(box(0.18, 0.16, 0.03, bevel=0.003), blackened(), matrix=T(0.62, 0, z))
    for s in (-1, 1):
        k.put(box(0.13, 0.12, 0.02, bevel=0.01), iron(), matrix=T(0.62, 0, s * 0.185))
    k.put(lathe([(0, 0.024), (-0.04, 0.0)], 12), iron(), True, T(-0.45, 0, 0))
    return k.build()


def lead_maul():
    k = Kit("lead_maul")
    haft(k, -0.4, 0.55, 0.02, bands=(0.4,))
    k.put(lathe([(-0.15, 0.0), (-0.15, 0.075), (-0.13, 0.095), (0.13, 0.095), (0.15, 0.075), (0.15, 0.0)], 24), lead(), True,
          T(0.56, 0, 0) @ R(90, "Y"))
    for z in (-0.09, 0.09):
        k.put(lathe([(-0.014, 0.0), (-0.014, 0.1), (0.014, 0.1), (0.014, 0.0)], 24), iron(), True, T(0.56, 0, z) @ R(90, "Y"))
    return k.build()


def knotted_club():
    k = Kit("knotted_war_club")
    k.put(lathe([(-0.2, 0.0), (-0.2, 0.02), (0.0, 0.022), (0.3, 0.04), (0.55, 0.058), (0.63, 0.05), (0.66, 0.0)], 14), wood(), True)
    rnd = random.Random(7)
    for i in range(16):
        x = 0.18 + 0.45 * rnd.random(); a = rnd.random() * math.tau
        r = 0.022 + 0.06 * (x - 0.0) / 0.6 * 0.6
        k.put(sphere(0.012 + 0.008 * rnd.random(), 8), wood(), True, T(x, r * math.cos(a), r * math.sin(a)))
    k.put(lathe([(-0.15, 0.025), (-0.05, 0.025)], 14), leather(), True)
    return k.build()


def great_club():
    k = Kit("great_club")
    k.put(lathe([(-0.4, 0.0), (-0.4, 0.024), (0.0, 0.027), (0.35, 0.05), (0.65, 0.085), (0.72, 0.07), (0.75, 0.0)], 16), pale_wood(), True)
    for x, n in ((0.4, 7), (0.53, 8), (0.66, 8)):
        r = 0.027 + (0.085 - 0.027) * (x / 0.65)
        k.put(lathe([(x - 0.012, r + 0.002), (x + 0.012, r + 0.002)], 16), iron(), True)
        for i in range(n):
            a = math.tau * (i + 0.5 * (x > 0.5)) / n
            k.put(lathe([(0, 0.012), (0.035, 0.0)], 8), iron(), True,
                  T(x + 0.06, r * math.cos(a), r * math.sin(a)) @ Matrix.Rotation(a, 4, "X") @ R(90, "Z"))
    k.put(lathe([(-0.36, 0.028), (-0.18, 0.028)], 14), leather(), True)
    return k.build()


def axe(scale=1.0, name="throwing_axe", length=0.42):
    k = Kit(name)
    haft(k, -length * 0.45, length * 0.62, 0.014)
    head = [(-0.025, 0.02), (0.025, 0.02), (0.03, -0.01), (0.09, -0.06), (0.12, -0.035), (0.13, 0.02), (0.11, 0.07), (0.07, 0.06),
            (0.025, 0.03), (-0.025, 0.035)]
    k.put(slab([(z, x) for x, z in head], 0.018, bevel=0.002), iron(), matrix=T(length * 0.47, 0, 0))
    o = k.build(); o.scale = (scale,) * 3
    return o


def throwing_knife(name="throwing_knife"):
    k = Kit(name)
    k.put(blade([(0.0, 0.016, -0.016), (0.08, 0.012, -0.012)], (0.15, 0.0), t=0.005), steel())
    k.put(box(0.1, 0.008, 0.022, bevel=0.002), leather(), matrix=T(-0.05, 0, 0))
    k.put(lathe([(-0.004, 0.017), (0.004, 0.017)], 16), blackened(), True, T(-0.11, 0, 0) @ R(90, "Z"))
    return k.build()


def javelin(name="javelin", length=1.0):
    k = Kit(name)
    haft(k, -length * 0.55, length * 0.4, 0.011, m=pale_wood())
    k.put(blade([(0.0, 0.006, -0.006), (0.05, 0.022, -0.022), (0.1, 0.016, -0.016)], (0.19, 0.0), t=0.007), steel(), matrix=T(length * 0.4, 0, 0))
    k.put(lathe([(0, 0.013), (0.05, 0.009)], 12), iron(), True, T(length * 0.4 - 0.04, 0, 0))
    return k.build()


def spear():
    k = Kit("spear")
    # held a little below the middle, so the head stays inside the sprite frame
    haft(k, -1.05, 0.82, 0.017, m=wood())
    k.put(blade([(0.0, 0.01, -0.01), (0.08, 0.035, -0.035), (0.18, 0.026, -0.026)], (0.3, 0.0), t=0.009), steel(), matrix=T(0.84, 0, 0))
    k.put(lathe([(0, 0.02), (0.09, 0.012)], 12), iron(), True, T(0.77, 0, 0))
    k.put(lathe([(0, 0.019), (-0.08, 0.0)], 12), iron(), True, T(-1.05, 0, 0))
    return k.build()


def war_hammer():
    k = Kit("war_hammer")
    haft(k, -0.2, 0.42, 0.018, bands=(0.3,))
    k.put(box(0.07, 0.065, 0.12, bevel=0.006), iron(), matrix=T(0.42, 0, 0.0))
    k.put(lathe([(0, 0.038), (0.06, 0.042), (0.075, 0.0)], 4), iron(), True, T(0.42, 0, 0.06) @ R(-90, "Y") @ R(45, "X"))
    k.put(lathe([(0, 0.026), (0.17, 0.0)], 8), iron(), True, T(0.42, 0, -0.06) @ R(90, "Y"))
    k.put(lathe([(0, 0.016), (0.08, 0.0)], 8), iron(), True, T(0.455, 0, 0))
    return k.build()


def tongs():
    k = Kit("tongs")
    for s in (-1, 1):
        k.put(box(0.4, 0.014, 0.016, bevel=0.003), iron(), matrix=T(0.1, 0, s * 0.012) @ R(s * 3, "Y"))
        k.put(box(0.1, 0.016, 0.02, bevel=0.003), iron(), matrix=T(0.33, 0, s * 0.03) @ R(-s * 25, "Y"))
    k.put(lathe([(-0.02, 0.012), (0.02, 0.012)], 12), blackened(), True, T(0.25, 0, 0) @ R(90, "X"))
    return k.build()


def flanged_mace():
    k = Kit("flanged_mace")
    haft(k, -0.25, 0.45, 0.016, m=blackened())
    k.put(lathe([(-0.24, 0.02), (-0.08, 0.02)], 12), leather(), True)
    for i in range(7):
        a = math.tau * i / 7
        fl = slab([(0, 0), (0.15, 0.0), (0.13, 0.05), (0.06, 0.065), (0.0, 0.03)], 0.01, bevel=0.002)
        k.put(fl, iron(), matrix=T(0.4, 0, 0) @ Matrix.Rotation(a, 4, "X") @ T(0, 0, 0.012))
    k.put(lathe([(0.0, 0.03), (0.05, 0.025), (0.06, 0.0)], 12), iron(), True, T(0.55, 0, 0))
    return k.build()


def book():
    k = Kit("book")
    k.put(box(0.25, 0.06, 0.18, bevel=0.006), leather(), matrix=T(0.1, 0, 0))
    k.put(box(0.235, 0.052, 0.17), mat("pages", "#7A6E58", rough=0.9, grime=0.4, scale=20), matrix=T(0.104, 0, 0.004))
    for x in (0.0, 0.2):
        for z in (-0.085, 0.085):
            k.put(box(0.035, 0.064, 0.03, bevel=0.003), iron(), matrix=T(x - 0.015 + (0.03 if x else 0), 0, z * 0.95))
    k.put(box(0.018, 0.066, 0.05, bevel=0.003), iron(), matrix=T(0.21, 0, 0.0))
    k.put(slab([(-0.02, -0.04), (0.02, -0.04), (0.02, 0.04), (-0.02, 0.04)], 0.004), iron(), matrix=T(0.1, -0.032, 0) @ S(1))
    k.put(slab([(-0.045, -0.012), (0.045, -0.012), (0.045, 0.012), (-0.045, 0.012)], 0.004), iron(), matrix=T(0.1, -0.032, 0))
    return k.build()

# ----------------------------------------------------------------------------------------------- shields
# Built in the shield frame: painted face toward -X, height along Y (point toward +Y), width along Z.

def _shield_frame(bm_pts_uw, t):
    """Outline (u along Y, w along Z) to a slab with its face toward -X."""
    bm = bmesh.new()
    vs = [bm.verts.new((0.0, u, w)) for u, w in bm_pts_uw]
    f = bm.faces.new(vs)
    r = bmesh.ops.extrude_face_region(bm, geom=[f])
    for v in [e for e in r["geom"] if isinstance(e, bmesh.types.BMVert)]: v.co.x += t
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm


def _curve(o, depth, half_w):
    for v in o.data.vertices:
        v.co.x += depth * (v.co.z / half_w) ** 2


def _outline_heater(h=0.66, w=0.27):
    top = -h * 0.42
    pts = [(top, -w), (top, w)]
    for i in range(1, 12):
        t = i / 12; pts.append((top + (h) * t, w * (1 - t ** 2.2)))
    pts.append((top + h, 0.0))
    for i in range(11, 0, -1):
        t = i / 12; pts.append((top + (h) * t, -w * (1 - t ** 2.2)))
    return pts


def _outline_kite(h=0.86, w=0.25):
    top = -h * 0.36
    def hw(t):
        if t <= 0.22: return w * (0.75 + 0.25 * math.sin(t / 0.22 * math.pi / 2))
        return w * (1 - ((t - 0.22) / 0.78) ** 1.4)
    right = [(top + h * i / 16, hw(i / 16)) for i in range(16)]
    pts = right + [(top + h, 0.0)] + [(u, -z) for u, z in reversed(right)]
    pts += [(top - 0.05 * math.sin(math.pi * i / 8), -0.75 * w * math.cos(math.pi * i / 8)) for i in range(1, 8)]
    return pts


def _charge(k, shape_uw, m, x=-0.004):
    """A painted charge: a very thin slab laid on the face (before the curve is applied)."""
    k.put(_shield_frame(shape_uw, 0.004), m, matrix=T(x, 0, 0))


def _cross_pattee(cu, cw, size):
    s = size; n = s * 0.18; f = s * 0.45
    return [(cu - s, cw - f), (cu - s, cw + f), (cu - n, cw + n * 0.8), (cu - f, cw + s), (cu + f, cw + s), (cu + n, cw + n * 0.8),
            (cu + s, cw + f), (cu + s, cw - f), (cu + n, cw - n * 0.8), (cu + f, cw - s), (cu - f, cw - s), (cu - n, cw - n * 0.8)]


def _rim(k, pts, m, t=0.02, inset=0.022):
    """A rim band: the outline scaled out minus the outline itself, done as a ring of quads."""
    import statistics
    cu = statistics.mean(p[0] for p in pts); cw = statistics.mean(p[1] for p in pts)
    bm = bmesh.new(); n = len(pts)
    outer = []; inner = []
    for u, w in pts:
        du, dw = u - cu, w - cw; L = math.hypot(du, dw) or 1
        outer.append((u + du / L * 0.006, w + dw / L * 0.006)); inner.append((u - du / L * inset, w - dw / L * inset))
    vf = [bm.verts.new((-0.006, u, w)) for u, w in outer] + [bm.verts.new((-0.006, u, w)) for u, w in inner]
    vb = [bm.verts.new((t, u, w)) for u, w in outer]
    for i in range(n):
        j = (i + 1) % n
        bm.faces.new((vf[i], vf[j], vf[n + j], vf[n + i]))
        bm.faces.new((vf[j], vf[i], vb[i], vb[j]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    k.put(bm, m)


def _straps(k, length):
    for u in (-length * 0.2, length * 0.18):
        k.put(box(0.012, 0.035, 0.26), leather(), matrix=T(0.035, u, 0))


def heater_shield():
    k = Kit("heater_shield"); pts = _outline_heater()
    k.put(_shield_frame(pts, 0.025), blue_paint())
    # azure, three gold bezants two and one, within a gold border
    for u, w in ((-0.15, -0.1), (-0.15, 0.1), (0.04, 0.0)):
        _charge(k, [(u + 0.06 * math.cos(a), w + 0.06 * math.sin(a)) for a in [i * math.tau / 20 for i in range(20)]], gold_paint())
    _rim(k, pts, gold_paint(), inset=0.03)
    _straps(k, 0.6)
    o = k.build()
    return o


def kite_shield():
    k = Kit("kite_shield"); pts = _outline_kite()
    k.put(_shield_frame(pts, 0.025), white_paint())
    _charge(k, [(-0.27, -0.035), (-0.27, 0.035), (0.4, 0.035), (0.4, -0.035)], red_paint())
    _charge(k, [(-0.1, -0.2), (-0.1, 0.2), (-0.03, 0.2), (-0.03, -0.2)], red_paint(), x=-0.0045)
    _rim(k, pts, blackened())
    _straps(k, 0.8)
    k.put(sphere(0.03, 14), iron(), True, T(-0.012, -0.04, 0) @ S(0.6, 1, 1))
    o = k.build()
    return o


def buckler():
    k = Kit("buckler")
    k.put(lathe([(0.0, 0.0), (0.0, 0.17), (0.012, 0.172), (0.03, 0.17), (0.03, 0.0)], 32), steel(), True, R(0, "Z"))
    k.put(lathe([(0.0, 0.0), (-0.0, 0.06), (-0.03, 0.05), (-0.055, 0.02), (-0.06, 0.0)], 24), blackened(), True)
    k.put(lathe([(-0.004, 0.155), (-0.004, 0.172), (0.004, 0.172), (0.004, 0.155)], 32), iron(), True)
    for i in range(12):
        a = math.tau * i / 12
        k.put(sphere(0.008, 8), brass(), True, T(-0.006, 0.12 * math.cos(a), 0.12 * math.sin(a)))
    k.put(box(0.04, 0.025, 0.12, bevel=0.004), leather(), matrix=T(0.05, 0, 0))
    o = k.build()
    for v in o.data.vertices:  # dish the face
        r = math.hypot(v.co.y, v.co.z); v.co.x += 0.03 * (r / 0.17) ** 2
    return o

# ----------------------------------------------------------------------------------------------- armour (icons)

def _torso(profile, ry=0.62, seg=28):
    """Hollow-looking torso: lathe about Z with oval sections. profile [(z, r)]."""
    bm = lathe([(z, r) for z, r in profile], seg, ry=ry, rz=1.0, cap=False)
    # lathe() revolves about X; turn it so the axis is Z and the depth (rz) is along Y
    bmesh.ops.transform(bm, matrix=R(-90, "Y"), verts=bm.verts)
    return bm


def _sleeve(k, m, length, r0, r1, side, drop=35, z=0.33, x=0.2, seg=16):
    bm = lathe([(0, r0), (length, r1)], seg, cap=False)
    k.put(bm, m, True, T(side * x, 0, z) @ R(side * drop, "Y") @ (S(1) if side > 0 else R(180, "Z")))


def gambeson():
    k = Kit("gambeson")
    prof = [(-0.42, 0.21), (-0.3, 0.2), (-0.1, 0.18), (0.1, 0.2), (0.27, 0.22), (0.35, 0.19), (0.4, 0.11), (0.42, 0.075)]
    k.put(_torso(prof), quilt(), True)
    for s in (-1, 1): _sleeve(k, quilt(), 0.36, 0.085, 0.06, s)
    k.put(lathe([(0.41, 0.085), (0.47, 0.08)], 24, ry=0.85, cap=False), quilt(), True, R(-90, "Y"))  # standing collar
    for z in (0.32, 0.18, 0.04, -0.1, -0.24):
        k.put(sphere(0.011, 8), brass(), True, T(0, -0.205 * 0.62 - 0.003, z))
    return k.build()


def mail_hauberk():
    k = Kit("mail_hauberk")
    prof = [(-0.55, 0.25), (-0.42, 0.22), (-0.1, 0.18), (0.1, 0.2), (0.27, 0.22), (0.35, 0.19), (0.4, 0.11), (0.43, 0.08)]
    k.put(_torso(prof), mail(), True)
    for s in (-1, 1): _sleeve(k, mail(), 0.5, 0.085, 0.055, s, drop=40)
    k.put(lathe([(-0.12, 0.19), (-0.06, 0.19)], 28, ry=0.64, cap=False), leather(), True, R(-90, "Y"))  # belt
    k.put(box(0.05, 0.02, 0.05, bevel=0.004), brass(), matrix=T(0, -0.125, -0.09))
    return k.build()


def templar_surcoat():
    k = Kit("templar_surcoat")
    # mail beneath
    k.put(_torso([(-0.3, 0.2), (0.1, 0.19), (0.3, 0.215), (0.38, 0.17), (0.42, 0.09)], ry=0.6), mail(), True)
    for s in (-1, 1): _sleeve(k, mail(), 0.5, 0.08, 0.055, s, drop=40)
    # the sleeveless surcoat, longer, flaring
    k.put(_torso([(-0.7, 0.3), (-0.4, 0.24), (-0.1, 0.2), (0.15, 0.215), (0.3, 0.228), (0.36, 0.2), (0.4, 0.12)], ry=0.64), linen(), True)
    k.put(lathe([(-0.13, 0.205), (-0.07, 0.205)], 28, ry=0.66, cap=False), leather(), True, R(-90, "Y"))
    cross = _cross_pattee(0.0, 0.0, 0.11)
    bm = bmesh.new(); vs = [bm.verts.new((w, 0, u)) for u, w in cross]; bm.faces.new(vs)
    r = bmesh.ops.extrude_face_region(bm, geom=list(bm.faces))
    for v in [e for e in r["geom"] if isinstance(e, bmesh.types.BMVert)]: v.co.y += 0.006
    k.put(bm, red_cloth(), matrix=T(0, -0.143, 0.16))
    return k.build()


def brigandine():
    k = Kit("brigandine")
    prof = [(-0.36, 0.205), (-0.15, 0.19), (0.05, 0.195), (0.22, 0.215), (0.32, 0.205), (0.37, 0.15), (0.4, 0.1)]
    k.put(_torso(prof, ry=0.64), velvet(), True)
    for s in (-1, 1): _sleeve(k, mail(), 0.32, 0.08, 0.065, s, drop=40)
    # rows of gilt rivets following the front curve
    for z in [0.3 - 0.075 * i for i in range(9)]:
        rr = 0.2 if z > -0.1 else 0.195
        for a in [-55 + 18.33 * j for j in range(7)]:
            ang = math.radians(a - 90)
            k.put(sphere(0.008, 8), brass(), True, T(rr * math.cos(ang), 0.64 * rr * math.sin(ang) - 0.002, z))
    k.put(lathe([(-0.22, 0.2), (-0.17, 0.2)], 28, ry=0.66, cap=False), leather(), True, R(-90, "Y"))
    return k.build()


def kettle_helm():
    k = Kit("kettle_helm")
    k.put(lathe([(0.0, 0.11), (0.06, 0.112), (0.12, 0.095), (0.17, 0.055), (0.19, 0.0)], 28), steel(), True, R(-90, "Y"))
    k.put(lathe([(-0.004, 0.11), (-0.004, 0.2), (-0.03, 0.215), (-0.024, 0.2), (0.004, 0.11)], 32, cap=False), steel(), True, R(-90, "Y"))
    k.put(lathe([(0.0, 0.113), (0.03, 0.113)], 28, cap=False), brass(), True, R(-90, "Y"))
    return k.build()


def nasal_helm():
    k = Kit("nasal_helm")
    k.put(lathe([(0.0, 0.105), (0.08, 0.105), (0.16, 0.07), (0.24, 0.0)], 28), steel(), True, R(-90, "Y"))
    k.put(lathe([(-0.005, 0.108), (0.025, 0.108)], 28, cap=False), brass(), True, R(-90, "Y"))
    k.put(box(0.03, 0.014, 0.12, bevel=0.004), steel(), matrix=T(0, -0.11, -0.04))
    return k.build()


def great_helm():
    k = Kit("great_helm")
    k.put(lathe([(-0.12, 0.115), (0.12, 0.11), (0.15, 0.1), (0.16, 0.0)], 28, rz=1.05), blackened(), True, R(-90, "Y"))
    for s_ in (-1, 1):
        k.put(box(0.085, 0.03, 0.016), dark(), matrix=T(s_ * 0.058, -0.112, 0.035))  # sights either side of the cross
    k.put(box(0.022, 0.02, 0.2, bevel=0.003), brass(), matrix=T(0, -0.124, -0.01))
    k.put(box(0.2, 0.02, 0.022, bevel=0.003), brass(), matrix=T(0, -0.12, 0.062))
    for x in (-0.06, -0.035, 0.035, 0.06):
        for z in (-0.04, -0.07, -0.1):
            k.put(box(0.008, 0.03, 0.014), dark(), matrix=T(x, -0.108, z))
    k.put(lathe([(-0.12, 0.118), (-0.1, 0.118)], 28, rz=1.07, cap=False), brass(), True, R(-90, "Y"))
    return k.build()


def coif():
    k = Kit("coif")
    k.put(lathe([(-0.32, 0.26), (-0.22, 0.2), (-0.12, 0.12), (0.0, 0.12), (0.1, 0.115), (0.16, 0.08), (0.2, 0.0)], 28, rz=1.05), mail(), True,
          R(-90, "Y"))
    face = [(0.07 * math.cos(a), 0.09 * math.sin(a)) for a in [i * math.tau / 20 for i in range(20)]]
    bm = bmesh.new(); vs = [bm.verts.new((x, 0, z)) for x, z in face]; bm.faces.new(vs)
    k.put(bm, dark(), matrix=T(0, -0.124, 0.02))
    k.put(lathe([(-0.004, 0.122), (0.004, 0.122)], 28, rz=1.05, cap=False), leather(), True, R(-90, "Y") @ T(0.0, 0, 0))
    return k.build()

# ----------------------------------------------------------------------------------------------- catalogue

# Loot base names (Sim/Items.cs) -> builder. Slugs are the icon file names (lower case, spaces to underscores).
LOOT = {
    "Arming Sword": arming_sword, "Falchion": falchion, "Messer": messer,
    "Rondel Dagger": rondel_dagger, "Misericorde": misericorde, "Baselard": baselard,
    "Longsword": longsword, "Bastard Sword": bastard_sword, "Greatsword": greatsword,
    "Great Maul": great_maul, "Lead Maul": lead_maul, "Knotted War Club": knotted_club, "Great Club": great_club,
    "Throwing Axes": None, "Throwing Knives": None, "Javelins": None,  # sets of several: see icons_items.py
    "Gambeson": gambeson, "Mail Hauberk": mail_hauberk, "Templar Surcoat": templar_surcoat, "Brigandine": brigandine,
    "Kettle Helm": kettle_helm, "Nasal Helm": nasal_helm, "Great Helm": great_helm, "Coif": coif,
    "Heater Shield": heater_shield, "Buckler": buckler, "Kite Shield": kite_shield,
}

# Hand-held kit for enemy loadouts (render manifest "loadout": {"right": ..., "left": ...}).
HELD = {
    "arming_sword": arming_sword, "falchion": falchion, "messer": messer, "rondel_dagger": rondel_dagger,
    "misericorde": misericorde, "baselard": baselard, "longsword": longsword, "bastard_sword": bastard_sword,
    "greatsword": greatsword, "serrated_greatsword": lambda: greatsword(True), "great_maul": great_maul,
    "lead_maul": lead_maul, "knotted_war_club": knotted_club, "great_club": great_club, "spear": spear,
    "war_hammer": war_hammer, "tongs": tongs, "flanged_mace": flanged_mace, "book": book, "javelin": javelin,
    "throwing_axe": axe,
}
SHIELDS = {"heater_shield": heater_shield, "kite_shield": kite_shield, "buckler": buckler}


def slug(name: str) -> str:
    return name.lower().replace("'", "").replace(" ", "_")
