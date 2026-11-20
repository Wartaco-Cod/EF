"""
One inventory icon per loot base item (Sim/Items.cs), rendered from the armoury models in Cycles.

    blender -b --python tools/art/blender/icons_items.py -- [--size 128] [--samples 64] [--names "Arming Sword,Buckler"]
    python  tools/art/blender/icons_items.py                                                        (bpy module)

Writes assets/ui/icons/items/<slug>.png (RGBA, transparent), slug = lower case name with underscores. The client
uses the icon for the item's base name and falls back to the slot icon in assets/ui/icons/ (Ui.ItemIcon).
"""
from __future__ import annotations

import math
import os
import sys

import bpy
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import armoury as A  # noqa: E402

ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
OUT = os.path.join(ROOT, "assets", "ui", "icons", "items")

R, T = A.R, A.T
DIAG = R(-45, "Y") @ R(25, "X")             # hand-held: grip lower left, business end upper right
DIAG_L = R(-135, "Y") @ R(-25, "X")         # mirrored, for crossed pairs
SHIELD = R(18, "Z") @ R(-90, "Y") @ R(90, "Z")  # face to the camera, point down, a little turned
WORN = R(-28, "Z")                           # armour and helms: three-quarter view


def scene(size, samples):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"; sc.cycles.samples = samples; sc.cycles.use_denoising = True
    sc.render.film_transparent = True
    sc.render.resolution_x = sc.render.resolution_y = size
    sc.render.image_settings.file_format = "PNG"; sc.render.image_settings.color_mode = "RGBA"
    sc.view_settings.view_transform = "AgX"; sc.view_settings.look = "AgX - Medium High Contrast"
    w = bpy.data.worlds.new("w"); w.use_nodes = True; sc.world = w
    bg = w.node_tree.nodes["Background"]; bg.inputs["Color"].default_value = (0.32, 0.3, 0.28, 1); bg.inputs["Strength"].default_value = 0.7
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam")); sc.collection.objects.link(cam); sc.camera = cam
    cam.data.type = "ORTHO"; cam.rotation_euler = (math.radians(90), 0, 0)
    for name, loc, energy, size_ in (("key", (-1.5, -2.5, 2.5), 600, 1.5), ("fill", (2.5, -2.0, 0.5), 150, 2.0), ("rim", (1.0, 2.5, 2.0), 600, 1.0)):
        L = bpy.data.lights.new(name, "AREA"); L.energy = energy; L.size = size_
        o = bpy.data.objects.new(name, L); sc.collection.objects.link(o); o.location = loc
        d = Vector((0, 0, 0)) - Vector(loc); o.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()
    return sc, cam


def place(objs, matrix):
    for o in objs:
        o.matrix_world = matrix @ o.matrix_world


def fit(cam, objs, margin=1.12):
    bpy.context.view_layer.update()
    xs, zs, ys = [], [], []
    for o in objs:
        for v in o.data.vertices:
            p = o.matrix_world @ v.co; xs.append(p.x); zs.append(p.z); ys.append(p.y)
    cx, cz = (min(xs) + max(xs)) / 2, (min(zs) + max(zs)) / 2
    cam.location = (cx, min(ys) - 3.0, cz)
    cam.data.ortho_scale = max(max(xs) - min(xs), max(zs) - min(zs)) * margin
    cam.data.clip_end = 20


def bold(objs):
    """Icons are drawn as small as 26 px, where a true-to-life blade is a hairline: thicken hand-held weapons across
    their width (Y and Z) in proportion to their length, leaving the length alone."""
    for o in objs:
        xs = [v.co.x for v in o.data.vertices]
        f = max(1.2, min(2.4, (max(xs) - min(xs)) / 0.5))
        o.matrix_world = o.matrix_world @ Matrix.Diagonal((1.0, f, f, 1.0))
    return objs


def build(name):
    if name == "Throwing Axes":
        a, b = bold([A.axe(), A.axe()])
        place([a], DIAG); place([b], T(0, 0.03, 0) @ DIAG_L); return [a, b]
    if name == "Throwing Knives":
        ks = bold([A.throwing_knife() for _ in range(3)])
        for i, k in enumerate(ks):
            place([k], T(0, 0.01 * i, 0) @ R(-20 - 25 * i, "Y") @ R(20, "X"))
        return ks
    if name == "Javelins":
        a, b = bold([A.javelin(), A.javelin()])
        place([a], DIAG); place([b], T(0, 0.03, 0) @ DIAG_L); return [a, b]
    o = A.LOOT[name]()
    if name in ("Heater Shield", "Kite Shield", "Buckler"):
        place([o], SHIELD)
    elif name in ("Gambeson", "Mail Hauberk", "Templar Surcoat", "Brigandine", "Kettle Helm", "Nasal Helm", "Great Helm", "Coif"):
        place([o], WORN)
    else:
        place(bold([o]), DIAG)
    return [o]


def main(argv):
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--size", type=int, default=128)
    ap.add_argument("--samples", type=int, default=64)
    ap.add_argument("--out", default=OUT)
    ap.add_argument("--names", default="")
    a = ap.parse_args(argv)
    os.makedirs(a.out, exist_ok=True)
    names = [n for n in a.names.split(",") if n] or list(A.LOOT)
    for n in names:
        sc, cam = scene(a.size, a.samples)
        objs = build(n)
        fit(cam, objs)
        sc.render.filepath = os.path.join(a.out, A.slug(n) + ".png")
        bpy.ops.render.render(write_still=True)
        print("[icons_items]", n, flush=True)


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:])
