"""
Renders scenery props (GLB models, e.g. from tools/art/meshy_world.py) to sprites with the game's locked camera and lights.

    blender -b --python tools/art/blender/props.py -- [--samples 64] [--gpu]
    python  tools/art/blender/props.py                                          (bpy module)

Reads art_source/world/manifest.json for the props and their real heights. For each prop it writes two views
(turned 35 degrees either way) to assets/sprites/prop_<name>_1.png and _2.png, and lists them in
assets/data/props.json with the world height the image covers, where the prop's base sits in the frame, and its solid
ground footprint (ellipse half-widths in world units; World.Obstacles blocks movement there).
"""
from __future__ import annotations

import json
import math
import os
import sys

import bpy
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import figures as F  # noqa: E402

ROOT = F.ROOT
MANIFEST = os.path.join(ROOT, "art_source", "world", "manifest.json")
PROPS_JSON = os.path.join(ROOT, "assets", "data", "props.json")
GAME_PX_PER_M = 180 / 2.6  # the client's world scale (GameClient.PixelsPerMetre)
FOOT_SHRINK = 0.8           # a little smaller than the silhouette, so figures don't snag on empty corners
PX_PER_M = 96  # render density; the game draws at about 69 px/m, so sprites are downsampled (sharp)
VIEWS = {"1": 35.0, "2": -40.0}


def import_glb(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path)
    objs = [o for o in bpy.data.objects if o not in before]
    meshes = [o for o in objs if o.type == "MESH"]
    root = bpy.data.objects.new("prop_root", None); bpy.context.scene.collection.objects.link(root)
    for o in objs:
        if o.parent is None: o.parent = root
    bpy.context.view_layer.update()
    pts = [o.matrix_world @ Vector(c) for o in meshes for c in o.bound_box]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    return root, meshes, lo, hi


def footprint(meshes, yaw_deg):
    """Ground footprint [rx, ry] in world units for the prop turned by yaw: vertex extents on the ground plane,
    with depth squashed by sin(camera pitch) because the world map is the camera's view of the ground."""
    import numpy as np
    pts = []
    for o in meshes:
        me = o.data
        co = np.empty(len(me.vertices) * 3, dtype=np.float32); me.vertices.foreach_get("co", co)
        co = co.reshape(-1, 3)
        mw = np.array(o.matrix_world)
        w = co @ mw[:3, :3].T + mw[:3, 3]
        pts.append(w[:, :2])
    xy = np.concatenate(pts)
    c, s_ = math.cos(math.radians(yaw_deg)), math.sin(math.radians(yaw_deg))
    rx = xy[:, 0] * c - xy[:, 1] * s_
    ry = xy[:, 0] * s_ + xy[:, 1] * c
    half_w = (rx.max() - rx.min()) / 2
    half_d = (ry.max() - ry.min()) / 2
    k = GAME_PX_PER_M * FOOT_SHRINK
    return [round(float(half_w * k), 1), round(float(half_d * k * math.sin(math.radians(F.CAM_PITCH))), 1)]


def render_prop(name, glb, height, samples, gpu, out_dir, render=True):
    F.reset_scene()
    F.setup_scene(samples, gpu, 256, 4.0)
    root, meshes, lo, hi = import_glb(glb)
    size = hi - lo
    s = height / max(size.z, 1e-6)
    # Centre on the origin, base on the ground, scaled to the real height.
    root.scale = (s, s, s)
    root.location = (-(lo.x + hi.x) / 2 * s, -(lo.y + hi.y) / 2 * s, -lo.z * s)
    for o in meshes:
        for m in o.data.materials:
            if m and m.node_tree:
                b = m.node_tree.nodes.get("Principled BSDF")
                if b and "Specular IOR Level" in b.inputs: b.inputs["Specular IOR Level"].default_value = 0.35
    turn = bpy.data.objects.new("turn", None); bpy.context.scene.collection.objects.link(turn)
    root.parent = turn
    foot = max(size.x, size.y) * s
    span = max(1.6, height * 1.45, foot * 1.25)
    span = math.ceil(span * 4) / 4
    px = int(round(span * PX_PER_M / 8) * 8)
    sc = bpy.context.scene
    sc.camera.data.ortho_scale = span
    sc.render.resolution_x = sc.render.resolution_y = px
    entries = {}
    bpy.context.view_layer.update()
    for v, yaw in VIEWS.items():
        rel = f"sprites/prop_{name}_{v}.png"
        if render:
            turn.rotation_euler = (0, 0, math.radians(yaw))
            sc.render.filepath = os.path.join(out_dir, rel)
            bpy.ops.render.render(write_still=True)
        turn.rotation_euler = (0, 0, 0); bpy.context.view_layer.update()
        entries[f"{name}_{v}"] = {"sprite": rel, "metres": span, "anchorX": 0.5, "anchorY": F.ANCHOR_Y,
                                  "footprint": footprint(meshes, yaw)}
    print(f"[props] {name}: {px}px, {span} m", flush=True)
    return entries


def main(argv):
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--samples", type=int, default=64)
    ap.add_argument("--gpu", action="store_true")
    ap.add_argument("--names", default="")
    ap.add_argument("--footprints-only", action="store_true", help="recompute footprints in props.json without rendering")
    a = ap.parse_args(argv)
    m = json.load(open(MANIFEST))
    props = json.load(open(PROPS_JSON)) if os.path.exists(PROPS_JSON) else {}
    want = [n for n in a.names.split(",") if n]
    for name, it in m["items"].items():
        if it["kind"] != "prop" or not it.get("file") or (want and name not in want): continue
        props.update(render_prop(name, os.path.join(ROOT, it["file"]), it["height"], a.samples, a.gpu, os.path.join(ROOT, "assets"),
                                 render=not a.footprints_only))
    with open(PROPS_JSON, "w", newline="\n") as f:
        json.dump(dict(sorted(props.items())), f, indent=1)


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:])
