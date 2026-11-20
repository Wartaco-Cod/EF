"""
Renders Meshy-built enemies (tools/art/meshy_enemies.py: rigged character + animation clips) to game sprite sheets,
with the same camera, lights and sheet format as figures.py.

    blender -b --python tools/art/blender/meshy_figures.py -- [--names torturer,...] [--samples 32] [--gpu]
    python  tools/art/blender/meshy_figures.py                                   (bpy module)

Reads art_source/enemies/render_manifest.json (height, extra_walk, strip_base per enemy). For each enemy it uses:
  <name>_walk.glb   the rig's walk cycle (from Rigging), or the clip asked for as an extra walk
  <name>_anims.glb  the merged clips in the order idle, attack, hit, death[, walk]
Root motion is removed (figures animate in place; the game moves them), so walks and attacks stay centred.
Writes assets/sprites/<sprite>_sheet.png, <sprite>.json and a 256 px still <sprite>.png, where <sprite> is
mon_<name> for rank and file and boss_<name> for bosses (boss_ prefix kept from the manifest name).
"""
from __future__ import annotations

import json
import math
import os
import shutil
import sys

import bpy
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import figures as F  # noqa: E402

ROOT = F.ROOT
MANIFEST = os.path.join(ROOT, "art_source", "enemies", "render_manifest.json")
SRC = os.path.join(ROOT, "art_source", "enemies", "models")
CLIP_ORDER = ["idle", "attack", "hit", "death", "walk2"]
FRAMES = {"idle": 8, "walk": 8, "attack": 8, "hit": 4, "death": 10}
PX_PER_M = 160 / 2.6  # same density as the player sheets


def sprite_name(name: str, it: dict | None = None) -> str:
    if it and it.get("sprite"): return it["sprite"]
    return name if name.startswith("boss_") else f"mon_{name}"


def import_glb(path):
    before_objs, before_acts = set(bpy.data.objects), set(bpy.data.actions)
    bpy.ops.import_scene.gltf(filepath=path)
    objs = [o for o in bpy.data.objects if o not in before_objs]
    acts = [a for a in bpy.data.actions if a not in before_acts]
    return objs, acts


def hips_bone(arm):
    for key in ("hips", "pelvis", "root", "spine"):
        for b in arm.pose.bones:
            if key in b.name.lower():
                return b
    return arm.pose.bones[0]


def strip_base(arm, objs, radius):
    """Delete a ground slab the concept image stood the figure on: vertices near the ground (rest pose) that are
    further than `radius` metres from every foot and toe joint. The manifest names it per enemy (strip_base)."""
    import bmesh
    bpy.context.view_layer.update()
    joints = [arm.matrix_world @ arm.data.bones[b].head_local for b in ("LeftFoot", "RightFoot", "LeftToeBase", "RightToeBase")
              if b in arm.data.bones]
    for o in objs:
        if o.type != "MESH" or o.parent is not arm: continue
        mw = o.matrix_world
        bm = bmesh.new(); bm.from_mesh(o.data)
        zs = [(mw @ v.co).z for v in bm.verts]
        z0 = min(zs); cut = z0 + 0.12 * (max(zs) - z0)
        gone = []
        for v, z in zip(bm.verts, zs):
            if z > cut: continue
            p = mw @ v.co
            if min(math.hypot(p.x - j.x, p.y - j.y) for j in joints) > radius: gone.append(v)
        bmesh.ops.delete(bm, geom=gone, context="VERTS")
        bm.to_mesh(o.data); bm.free()
        print(f"[meshy_figures] stripped {len(gone)} base vertices", flush=True)


def strip_box(arm, objs, box):
    """Delete the vertices inside an axis box [xmin, xmax, zmin, zmax] (rest pose, world metres, before the turn):
    used to cut away a weapon Meshy half kept (a stretched haft and a head left on the ground)."""
    import bmesh
    x0, x1, z0, z1 = box
    for o in objs:
        if o.type != "MESH" or o.parent is not arm: continue
        mw = o.matrix_world
        bm = bmesh.new(); bm.from_mesh(o.data)
        gone = [v for v in bm.verts if x0 <= (mw @ v.co).x <= x1 and z0 <= (mw @ v.co).z <= z1]
        bmesh.ops.delete(bm, geom=gone, context="VERTS")
        bm.to_mesh(o.data); bm.free()
        print(f"[meshy_figures] cut {len(gone)} vertices from the box", flush=True)


def clear_scene_objects(objs):
    for o in objs:
        bpy.data.objects.remove(o, do_unlink=True)


PREVIEW = False  # --preview: only a few stills to a preview folder, for checking loadouts


def render_enemy(name, it, samples, gpu, out_dir):
    F.reset_scene()
    boss = name.startswith("boss_")
    height = it.get("height", 1.9)
    # "frame" widens the cell for figures whose weapons reach past the body; the sheet records metres, so the figure
    # is drawn the same size in game either way.
    span = max(2.6, height * 1.35) * (1.15 if boss else 1.0) * it.get("frame", 1.0)
    px = int(round(span * PX_PER_M / 8) * 8)
    F.setup_scene(samples, gpu, px, span)
    sc = bpy.context.scene

    turn = bpy.data.objects.new("turn", None); sc.collection.objects.link(turn)
    clips = {}  # anim -> (armature, action, objects)

    def load(path, names):
        objs, acts = import_glb(path)
        arm = next(o for o in objs if o.type == "ARMATURE")
        if it.get("strip_base"):
            strip_base(arm, objs, it["strip_base"])
        if it.get("strip_box"):
            strip_box(arm, objs, it["strip_box"])
        if it.get("weapons") == "knight":  # Meshy drops thin blades and shields when it rigs; bolt our own on
            import hand_weapons
            hand_weapons.arm_knight(arm)
        if it.get("loadout"):  # enemies: weapons and shields from the armoury (armoury.py)
            import hand_weapons
            hand_weapons.arm_loadout(arm, it["loadout"])
        for o in objs:
            if o.parent is None: o.parent = turn
        # Meshy characters face -Y after import; our figures face +Y at yaw 0.
        arm_root = arm if arm.parent is turn else arm.parent
        arm_root.rotation_mode = "XYZ"  # glTF imports use quaternions, which would ignore the Euler turn below
        arm_root.rotation_euler.z += math.pi
        return arm, [a for a in bpy.data.actions if a in acts], objs

    model = it.get("model", name)  # variants (e.g. the knight with another weapon) share one model
    anims_file = os.path.join(SRC, f"{model}_anims.glb")
    walk_file = os.path.join(SRC, f"{model}_walk.glb")
    arm, acts, objs = load(anims_file, CLIP_ORDER)
    wanted = ["idle", "attack", "hit", "death"] + (["walk2"] if it.get("extra_walk") else [])
    # Match clips by name keywords first, then by position.
    by_kw = {}
    kws = {"idle": ("idle", "stance"), "attack": ("attack", "slash", "swing", "strike", "thrust", "combo"),
           "hit": ("hit", "react"), "death": ("dying", "dead", "death", "fall"), "walk2": ("walk",)}
    for a in acts:
        low = a.name.lower()
        for k, words in kws.items():
            if k not in by_kw and any(w in low for w in words):
                by_kw[k] = a; break
    for i, k in enumerate(wanted):
        if k not in by_kw and i < len(acts): by_kw[k] = acts[i]
    walk_arm = None
    if not it.get("extra_walk") and os.path.exists(walk_file):
        walk_arm, wacts, wobjs = load(walk_file, ["walk"])
        by_kw["walk"] = wacts[0] if wacts else None
        for o in wobjs: o.hide_render = True
    elif "walk2" in by_kw:
        by_kw["walk"] = by_kw.pop("walk2")

    anims = {k: (FRAMES[k], F.ANIMS[k][1], F.ANIMS[k][2]) for k in ("idle", "walk", "attack", "hit", "death") if by_kw.get(k)}
    dirs = F.dirs_for(it.get("dirs") or ("full" if boss else "minor"))
    sprite = sprite_name(name, it)
    tmp = os.path.join(HERE, "_frames", sprite); os.makedirs(tmp, exist_ok=True)

    def use(anim):
        """Show the armature carrying this clip, hide the other."""
        a = walk_arm if (anim == "walk" and walk_arm is not None) else arm
        for o in turn.children_recursive:
            o.hide_render = True
        for o in [a] + list(a.children_recursive):
            o.hide_render = False
        a.animation_data_create(); a.animation_data.action = by_kw[anim]
        return a

    if PREVIEW:
        sc.render.resolution_x = sc.render.resolution_y = 320
        for anim in ("idle", "attack", "walk"):
            if anim not in anims: continue
            a = use(anim); f0, f1 = by_kw[anim].frame_range
            for ang in (70, 200):
                turn.rotation_euler = (0, 0, F.facing_rotation(ang))
                sc.frame_set(int(f0 + (f1 - f0) * 0.45))
                sc.render.filepath = os.path.join(out_dir, f"{sprite}_{anim}_{ang}.png")
                bpy.ops.render.render(write_still=True)
        print(f"[meshy_figures] preview {sprite}", flush=True)
        return sprite
    for anim, (n, fps, loop) in anims.items():
        a = use(anim)
        act = by_kw[anim]
        f0, f1 = act.frame_range
        hb = hips_bone(a)
        sc.frame_set(int(f0)); base = (a.matrix_world @ hb.head).copy()
        for di, ang in enumerate(dirs):
            turn.rotation_euler = (0, 0, F.facing_rotation(ang))
            for i in range(n):
                t = i / n if loop else i / max(1, n - 1)
                fr = f0 + (f1 - f0) * t
                sc.frame_set(int(fr), subframe=fr - int(fr))
                # Remove root motion: keep the hips over the origin (deaths keep their fall but stay centred).
                a.location = (0, 0, 0); bpy.context.view_layer.update()
                hip = a.matrix_world @ hb.head
                drift = hip - turn.matrix_world.translation
                local = turn.matrix_world.inverted().to_3x3() @ Vector((drift.x, drift.y, 0))
                a.location = (a.location.x - local.x * (0.5 if anim == "death" else 1.0),
                              a.location.y - local.y * (0.5 if anim == "death" else 1.0), 0)
                sc.render.filepath = os.path.join(tmp, f"{anim}_d{di}_{i + 1:04d}.png")
                bpy.ops.render.render(write_still=True)
    F.pack(sprite, tmp, out_dir, px, dirs, anims, span)
    shutil.rmtree(tmp, ignore_errors=True)
    # Still for portraits: idle, three-quarter view.
    a = use("idle"); sc.frame_set(int(by_kw["idle"].frame_range[0]))
    turn.rotation_euler = (0, 0, F.facing_rotation(70))
    sc.render.resolution_x = sc.render.resolution_y = 256
    sc.render.filepath = os.path.join(out_dir, sprite + ".png")
    bpy.ops.render.render(write_still=True)
    print(f"[meshy_figures] {sprite}: {', '.join(anims)} x {len(dirs)} dirs, {px}px", flush=True)
    return sprite


def main(argv):
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--names", default="")
    ap.add_argument("--samples", type=int, default=32)
    ap.add_argument("--gpu", action="store_true")
    ap.add_argument("--out", default=F.SPRITES)
    ap.add_argument("--manifest", default=MANIFEST, help="render manifest (e.g. art_source/hero/render_manifest.json)")
    ap.add_argument("--src", default=None, help="folder with the GLBs (default: models/ next to the manifest)")
    ap.add_argument("--preview", action="store_true", help="a few stills per figure to --out instead of sheets")
    a = ap.parse_args(argv)
    global SRC, PREVIEW
    PREVIEW = a.preview
    SRC = a.src or os.path.join(os.path.dirname(os.path.abspath(a.manifest)), "models")
    m = json.load(open(a.manifest))
    want = [n for n in a.names.split(",") if n]
    for name, it in m["items"].items():
        if want and name not in want: continue
        if not os.path.exists(os.path.join(SRC, f"{it.get('model', name)}_anims.glb")): continue
        try:
            render_enemy(name, it, a.samples, a.gpu, a.out)
        except Exception as e:
            import traceback; traceback.print_exc()
            print(f"[meshy_figures] FAILED {name}: {e}", flush=True)


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:])
