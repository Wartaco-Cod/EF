"""
Templar Knights item icons, modelled and rendered in Blender (Cycles) with the same materials as the figures.

    blender -b --python tools/art/blender/icons.py -- [--size 128] [--samples 96] [--gpu]
    python  tools/art/blender/icons.py                                         (bpy module)

Output: assets/ui/icons/<kind>.png (RGBA, transparent). Kinds: sword, dagger, polearm, great, throwing, armor, helm,
shield, potion_health, potion_mana. The client picks one per item from its slot and weapon family (Ui.ItemIcon).
"""
from __future__ import annotations

import math
import os
import sys

import bpy
from mathutils import Euler, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import figures as F  # noqa: E402

OUT = os.path.join(F.ROOT, "assets", "ui", "icons")

def steel(): return F.material("metal", "#B8BEC8", rough=0.26)
def dark_steel(): return F.material("metal", "#6A6E76", rough=0.4)
def gold(): return F.material("gold", "#D9B23A")
def leather(): return F.material("leather", "#4A3222")
def wood(): return F.material("wood", "#6A4628")

def group(objs, rot=(0, 0, 0), loc=(0, 0, 0), scale=1.0):
    root = bpy.data.objects.new("root", None); bpy.context.scene.collection.objects.link(root)
    for o in objs:
        if o.parent is None: o.parent = root
    root.rotation_euler = Euler([math.radians(a) for a in rot]); root.location = loc; root.scale = (scale,) * 3
    return root

def sword(length=1.0, dagger=False):
    L = 0.32 if dagger else 0.85 * length
    o = []
    o.append(F.solid_from_outline("blade", [(-0.034, 0), (0.034, 0), (0.028, L * 0.86), (0.0, L), (-0.028, L * 0.86)], 0.014, steel(), 0.004))
    o.append(F.box("fuller", 0.012, 0.016, L * 0.62, dark_steel())); o[-1].location = (0, 0, L * 0.38)
    o.append(F.box("guard", 0.26 if not dagger else 0.13, 0.04, 0.035, gold(), bevel=0.008)); o[-1].location = (0, 0, -0.01)
    g = F.tube("grip", 0.022, 0.022, 0.16 if not dagger else 0.1, leather()); g.location = (0, 0, -0.02); o.append(g)
    p = F.sphere("pommel", 0.038, gold()); p.location = (0, 0, -0.21 if not dagger else -0.15); o.append(p)
    return o

def polearm():
    o = [F.tube("shaft", 0.022, 0.02, 1.5, wood())]
    o[-1].location = (0, 0, 0.75)
    o.append(F.solid_from_outline("tip", [(-0.04, 0), (0.04, 0), (0.0, 0.3)], 0.016, steel(), 0.003)); o[-1].location = (0, 0, 0.75)
    o.append(F.solid_from_outline("blade", [(0.0, -0.18), (0.22, -0.28), (0.26, -0.02), (0.22, 0.1), (0.0, 0.0)], 0.018, steel(), 0.003)); o[-1].location = (0.02, 0, 0.7)
    return o

def great():
    o = [F.tube("haft", 0.03, 0.028, 1.0, wood())]; o[-1].location = (0, 0, 0.45)
    h = F.box("head", 0.36, 0.17, 0.17, dark_steel(), bevel=0.015); h.location = (0, 0, 0.45); o.append(h)
    for s in (-1, 1):
        b = F.box("band", 0.03, 0.18, 0.18, gold(), bevel=0.004); b.location = (s * 0.12, 0, 0.45); o.append(b)
    return o

def throwing():
    o = [F.tube("haft", 0.02, 0.018, 0.55, wood())]; o[-1].location = (0, 0, 0.3)
    hb = F.solid_from_outline("axe", [(0.0, -0.06), (0.06, -0.05), (0.2, -0.14), (0.23, 0.0), (0.2, 0.12), (0.06, 0.05), (0.0, 0.06)], 0.02, steel(), 0.004)
    hb.location = (0.0, 0, 0.24); o.append(hb)
    w = F.lathe("wrap", [(0, 0.024), (0.12, 0.024)], 12, 1, 1, leather()); w.location = (0, 0, -0.25); o.append(w)
    return o

def armor():
    fig = F.Figure("ic", {"body": "#F0ECE2", "trim": "#B3241E", "head": "bare", "weapon": "none", "emblem": "cross", "dress": "plate"})
    r = fig.rig
    r.joint("hips", "root", (0, 0, 0.98)); r.joint("spine", "hips", (0, 0, 1.1)); r.joint("chest", "spine", (0, 0, 1.3))
    r.joint("neck", "chest", (0, 0, 1.54)); r.joint("head", "neck", (0, 0, 1.6))
    for s, x in (("R", 1), ("L", -1)):
        r.joint(f"shoulder.{s}", "chest", (0.26 * x, 0, 1.47)); r.joint(f"elbow.{s}", f"shoulder.{s}", (0.29 * x, 0, 1.19))
        r.joint(f"hand.{s}", f"elbow.{s}", (0.3 * x, 0.02, 0.94)); r.joint(f"hip.{s}", "hips", (0.105 * x, 0, 0.95))
        r.joint(f"knee.{s}", f"hip.{s}", (0.11 * x, 0.01, 0.53)); r.joint(f"ankle.{s}", f"knee.{s}", (0.11 * x, 0, 0.1))
    fig.build_body()
    # Keep only the torso pieces: hide limbs below the hem and the hands.
    for ob in list(r.objects):
        if any(k in ob.name for k in ("thigh", "shin", "foot", "poleyn", "hand.", "farm", "scabbard")):
            bpy.data.objects.remove(ob)
    r.turn.location = (0, 0, -1.15)
    return [r.turn]

def helm():
    st = steel()
    h = F.lathe("helm", [(-0.14, 0.0), (-0.14, 0.118), (0.0, 0.124), (0.1, 0.122), (0.14, 0.1), (0.155, 0.0)], 28, 1.0, 1.05, st)
    v = F.box("visor", 0.17, 0.02, 0.018, F.material("dark")); v.location = (0, 0.125, 0.03)
    n = F.box("nasal", 0.032, 0.02, 0.22, gold(), bevel=0.004); n.location = (0, 0.128, -0.02)
    b = F.box("brow", 0.034, 0.02, 0.2, gold(), bevel=0.004); b.location = (0, 0.128, 0.05); b.rotation_euler = (0, math.pi / 2, 0)
    o = [h, v, n, b]
    for zz in (-0.06, -0.09):
        for x in (-0.05, 0.05):
            s = F.sphere("breath", 0.008, F.material("dark")); s.location = (x, 0.127, zz); o.append(s)
    return o

def shield():
    outline = [(-0.24, 0.26), (0.24, 0.26)]
    for i in range(1, 12):
        t = i / 12; outline.append((0.24 * (1 - t ** 1.6), 0.26 - 0.62 * t))
    outline.append((0.0, -0.36))
    for i in range(11, 0, -1):
        t = i / 12; outline.append((-0.24 * (1 - t ** 1.6), 0.26 - 0.62 * t))
    sh = F.solid_from_outline("shield", outline, 0.04, F.material("leather", "#F0ECE2"), 0.006)
    rim = F.solid_from_outline("rim", [(x * 1.05, z * 1.04) for x, z in outline], 0.03, gold(), 0.004)
    pl = F.plane("cross", 0.34, 0.34, F.decal_material("cross", "#B3241E")); pl.location = (0, -0.026, 0.03); pl.rotation_euler = (0, 0, 0)
    return [sh, rim, pl]

def potion(colour):
    glass = bpy.data.materials.new("glass"); glass.use_nodes = True
    b = glass.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = F.rgb("#E8F0F0"); b.inputs["Roughness"].default_value = 0.05
    if "Transmission Weight" in b.inputs: b.inputs["Transmission Weight"].default_value = 1.0
    b.inputs["IOR"].default_value = 1.45
    liquid = F.material("glow", colour, power=1.2)
    bottle = F.lathe("bottle", [(0, 0.0), (0.0, 0.1), (0.08, 0.13), (0.16, 0.12), (0.22, 0.06), (0.26, 0.04), (0.32, 0.045), (0.33, 0.0)], 24, 1, 1, glass)
    liq = F.lathe("liquid", [(0.01, 0.0), (0.01, 0.092), (0.08, 0.12), (0.15, 0.11), (0.16, 0.0)], 24, 1, 1, liquid)
    cork = F.lathe("cork", [(0.3, 0.035), (0.37, 0.038), (0.38, 0.0)], 12, 1, 1, F.material("wood", "#8A6A48"))
    return [bottle, liq, cork]

ICONS = {
    "sword": (lambda: sword(), (0, 45, 0), (-0.2, 0, -0.2), 0.95),
    "dagger": (lambda: sword(dagger=True), (0, 45, 0), (0, 0, -0.05), 1.8),
    "polearm": (polearm, (0, 45, 0), (-0.05, 0, -0.05), 0.6),
    "great": (great, (0, 45, 0), (0, 0, -0.4), 1.15),
    "throwing": (throwing, (0, 40, 0), (0, 0, -0.1), 1.6),
    "armor": (armor, (0, 0, 200), (0, 0, -0.02), 1.12),
    "helm": (helm, (0, 0, 205), (0, 0, 0), 2.4),
    "shield": (shield, (0, 0, -20), (0, 0, 0.0), 1.5),
    "potion_health": (lambda: potion("#C0202A"), (0, 0, 0), (0, 0, -0.18), 2.0),
    "potion_mana": (lambda: potion("#2A50D0"), (0, 0, 0), (0, 0, -0.18), 2.0),
}

def render_icon(kind, size, samples, gpu, out):
    F.reset_scene()
    F.setup_scene(samples, gpu, size, 1.0)
    sc = bpy.context.scene
    cam = sc.camera
    cam.data.shift_y = 0.0
    build, rot, loc, scale = ICONS[kind]
    group(build(), rot, loc, scale)
    sc.render.filepath = os.path.join(out, kind + ".png")
    bpy.ops.render.render(write_still=True)

def main(argv):
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--size", type=int, default=128)
    ap.add_argument("--samples", type=int, default=96)
    ap.add_argument("--gpu", action="store_true")
    ap.add_argument("--out", default=OUT)
    ap.add_argument("--names", default="")
    a = ap.parse_args(argv)
    os.makedirs(a.out, exist_ok=True)
    for k in [n for n in a.names.split(",") if n] or list(ICONS):
        render_icon(k, a.size, a.samples, a.gpu, a.out)
        print("[icons]", k, flush=True)

if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:])
