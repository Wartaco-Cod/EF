"""
Procedural weapons bolted to a Meshy rig's hand bones. Meshy drops thin parts (blades, shields) when it rigs a
character, so the sprite renderer adds our own: a knightly sword in the right fist and a kite shield with the red
cross on the left forearm. Sizes are in metres; placement is in each bone's own axes (Y runs along the bone toward
the fingers, +X is the thumb side of the right fist, which is where a held blade comes out).
"""
from __future__ import annotations

import math

import bmesh
import bpy
from mathutils import Matrix, Vector


def _mat(name, rgb, metal=0.0, rough=0.5):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = (*rgb, 1)
    b.inputs["Metallic"].default_value = metal
    b.inputs["Roughness"].default_value = rough
    return m


def _box(name, size, loc, mat):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1)
    bmesh.ops.scale(bm, vec=Vector(size), verts=bm.verts)
    bmesh.ops.translate(bm, vec=Vector(loc), verts=bm.verts)
    bm.to_mesh(me); bm.free()
    me.materials.append(mat)
    return me


def _join(name, meshes):
    bm = bmesh.new()
    for me in meshes:
        bm.from_mesh(me)  # appends; material indices kept per mesh below
    out = bpy.data.meshes.new(name)
    bm.to_mesh(out); bm.free()
    return out


def _object_from(name, parts):
    """parts: list of (mesh) each with one material; builds one object with all materials."""
    obj_meshes = []
    for me in parts:
        o = bpy.data.objects.new(me.name, me); bpy.context.scene.collection.objects.link(o); obj_meshes.append(o)
    bpy.ops.object.select_all(action="DESELECT")
    for o in obj_meshes: o.select_set(True)
    bpy.context.view_layer.objects.active = obj_meshes[0]
    bpy.ops.object.join()
    o = bpy.context.view_layer.objects.active; o.name = name
    return o


def sword(length=0.95):
    steel = _mat("w_steel", (0.55, 0.56, 0.6), metal=0.9, rough=0.32)
    dark = _mat("w_iron", (0.12, 0.11, 0.1), metal=0.8, rough=0.45)
    grip = _mat("w_grip", (0.18, 0.09, 0.05), rough=0.7)
    blade = length - 0.2
    parts = [
        _box("blade", (blade, 0.012, 0.055), (0.07 + blade / 2, 0, 0), steel),
        _box("fuller", (blade * 0.8, 0.014, 0.012), (0.09 + blade * 0.4, 0, 0), dark),
        _box("guard", (0.03, 0.04, 0.24), (0.05, 0, 0), dark),
        _box("grip", (0.15, 0.035, 0.035), (-0.03, 0, 0), grip),
        _box("pommel", (0.05, 0.055, 0.055), (-0.13, 0, 0), dark),
    ]
    o = _object_from("hero_sword", parts)
    # the blade tapers to a point
    for v in o.data.vertices:
        if v.co.x > 0.07 + blade - 0.12:
            v.co.z *= 0.35
    return o


def kite_shield():
    white = _mat("w_paint", (0.52, 0.49, 0.43), rough=0.75)  # weathered, smoke-stained paint
    red = _mat("w_red", (0.38, 0.03, 0.03), rough=0.7)
    rim = _mat("w_rim", (0.2, 0.19, 0.18), metal=0.85, rough=0.4)
    # outline in (u along the bone, w across): flat top at u=-0.32, point at u=+0.46
    pts = [(-0.32, -0.27), (-0.32, 0.27), (-0.12, 0.27), (0.08, 0.22), (0.26, 0.12), (0.46, 0.0), (0.26, -0.12), (0.08, -0.22), (-0.12, -0.27)]
    me = bpy.data.meshes.new("face"); bm = bmesh.new()
    vs = [bm.verts.new((0.0, u, w)) for u, w in pts]
    f = bm.faces.new(vs)
    r = bmesh.ops.extrude_face_region(bm, geom=[f])
    for v in [e for e in r["geom"] if isinstance(e, bmesh.types.BMVert)]: v.co.x -= 0.03
    for v in bm.verts:  # a gentle curve across the face
        v.co.x += 0.06 * (v.co.z / 0.27) ** 2
    bm.to_mesh(me); bm.free(); me.materials.append(white)
    parts = [me,
             _box("cross_v", (0.012, 0.6, 0.055), (-0.012, 0.05, 0), red),
             _box("cross_h", (0.012, 0.055, 0.38), (-0.012, -0.1, 0), red)]
    return _object_from("hero_shield", parts)


def attach(arm, obj, bone, offset: Matrix, size: float = 1.0):
    """Parents obj to a pose bone; offset is in the bone's own axes, in metres. size scales the object (sprites read
    better with weapons a little larger than life)."""
    pb = arm.pose.bones[bone]
    obj.parent = arm; obj.parent_type = "BONE"; obj.parent_bone = bone
    bpy.context.view_layer.update()
    s = 1.0 / arm.matrix_world.to_scale().x  # Meshy armatures are scaled 0.01 (centimetres)
    obj.matrix_world = arm.matrix_world @ pb.matrix @ Matrix.Diagonal((s, s, s, 1)) @ offset @ Matrix.Diagonal((size, size, size, 1))


def arm_knight(arm):
    """Sword in the right fist, shield on the left forearm, for the hero knight."""
    bpy.context.view_layer.update()
    sw = sword()
    attach(arm, sw, "RightHand", Matrix.Translation((0.0, 0.085, 0.0)))
    sh = kite_shield()
    attach(arm, sh, "LeftForeArm", Matrix.Translation((-0.07, 0.12, 0.0)))
    return [sw, sh]


def arm_loadout(arm, loadout: dict):
    """Arms a Meshy rig from the armoury (render manifest "loadout"):
         right:  a held weapon, grip in the right fist, business end out of the thumb side
         left:   a shield (strapped to the left forearm), "knight" (the hero's own shield) or a held thing in the left fist
         right_offset / left_offset: optional [x, y, z] nudges in metres, in the bone's axes
         left_flip: true turns a left-hand item end for end (if the rig's left thumb axis runs the other way)
         scale: size of held weapons against life (default 1.2), shield_scale (default 1.1)"""
    import armoury as A
    bpy.context.view_layer.update()
    out = []
    if loadout.get("right"):
        o = A.HELD[loadout["right"]]()
        dx, dy, dz = loadout.get("right_offset", (0, 0, 0))
        attach(arm, o, "RightHand", Matrix.Translation((dx, 0.085 + dy, dz)), loadout.get("scale", 1.2))
        out.append(o)
    left = loadout.get("left")
    if left == "knight":  # the hero knight's own red-cross kite shield, where arm_knight straps it
        o = kite_shield()
        attach(arm, o, "LeftForeArm", Matrix.Translation((-0.07, 0.12, 0.0)))
        out.append(o)
    elif left in A.SHIELDS:
        o = A.SHIELDS[left]()
        dx, dy, dz = loadout.get("left_offset", (0, 0, 0))
        attach(arm, o, "LeftForeArm", Matrix.Translation((-0.07 + dx, 0.12 + dy, dz)), loadout.get("shield_scale", 1.1))
        out.append(o)
    elif left:
        o = A.HELD[left]()
        dx, dy, dz = loadout.get("left_offset", (0, 0, 0))
        turn = Matrix.Rotation(math.pi, 4, "Z") if loadout.get("left_flip") else Matrix.Identity(4)
        attach(arm, o, "LeftHand", Matrix.Translation((dx, 0.085 + dy, dz)) @ turn, loadout.get("scale", 1.2))
        out.append(o)
    return out
