# .blend → VRM 0.x для Клавы через аддон «VRM format» (saturday06). Запускает KlavaEditor.exe --swap:
#   blender -b модель.blend --python export_vrm.py -- выход.vrm исходная.vrm
#
# Почему VRM 0.x, а не 1.0: у three-vrm оси нормализованных костей VRM 0.x и 1.0 зеркальные, а все позы
# и жесты Клавы написаны под VRM 0.x (Вита) — в 1.0 руки «вниз» уходили вверх.
# «исходная.vrm» — модель, из которой когда-то сделан .blend (у Клавы — Vita): из неё берутся таблица эмоций
# и пружины волос. При выгрузке через GLB формы лица теряют имена и приезжают номерами «0…40», а какая из
# них улыбка и какая «а» — записано только в VRM (blendShapeMaster). Без этого у Клавы не было бы ни мимики,
# ни губ. .blend не сохраняется — всё меняется только в памяти этого запуска Blender.
# Скрипт для Blender — только на Python (bpy), другого API у Blender нет.
import json
import struct
import sys

import bpy
import numpy as np

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if len(args) < 2:
    raise SystemExit("нужно: -- выход.vrm исходная.vrm")
OUT, SOURCE = args[0], args[1]

# VRM 1.0 preset → VRM 0.x preset (если исходник уже 1.0)
FROM_VRM1 = {"aa": "a", "ih": "i", "ou": "u", "ee": "e", "oh": "o", "blink": "blink", "blinkLeft": "blink_l",
             "blinkRight": "blink_r", "angry": "angry", "relaxed": "fun", "happy": "joy", "sad": "sorrow",
             "neutral": "neutral", "lookUp": "lookup", "lookDown": "lookdown", "lookLeft": "lookleft", "lookRight": "lookright"}


def read_glb_json(path):
    with open(path, "rb") as f:
        data = f.read()
    magic, _, _ = struct.unpack_from("<III", data, 0)
    if magic != 0x46546C67:
        raise SystemExit(f"{path} — не GLB/VRM")
    length, _ = struct.unpack_from("<II", data, 12)
    return json.loads(data[20:20 + length].decode("utf-8"))


def source_tables(src):
    """Эмоции [(preset VRM 0.x, имя, [(номер формы, вес 0..1)], двоичная?)] и пружины из исходной VRM."""
    j = read_glb_json(src)
    ext = j.get("extensions", {})
    names = [n.get("name", "") for n in j.get("nodes", [])]
    exprs, springs = [], []
    if "VRM" in ext:
        for g in ext["VRM"].get("blendShapeMaster", {}).get("blendShapeGroups", []):
            binds = [(b["index"], b.get("weight", 100) / 100.0) for b in g.get("binds", [])]
            exprs.append((g.get("presetName", "unknown"), g.get("name", ""), binds, bool(g.get("isBinary"))))
        for g in ext["VRM"].get("secondaryAnimation", {}).get("boneGroups", []):
            springs.append({
                "comment": g.get("comment", ""), "stiffness": g.get("stiffiness", 1.0), "gravity": g.get("gravityPower", 0.0),
                "dir": g.get("gravityDir", {"x": 0, "y": -1, "z": 0}), "drag": g.get("dragForce", 0.4),
                "radius": g.get("hitRadius", 0.02), "center": names[g["center"]] if g.get("center", -1) >= 0 else "",
                "bones": [names[i] for i in g.get("bones", []) if 0 <= i < len(names)],
            })
    elif "VRMC_vrm" in ext:
        e = ext["VRMC_vrm"].get("expressions", {})
        for group, custom in (("preset", False), ("custom", True)):
            for name, x in e.get(group, {}).items():
                binds = [(b["index"], b.get("weight", 1.0)) for b in x.get("morphTargetBinds", [])]
                exprs.append(("unknown" if custom else FROM_VRM1.get(name, "unknown"), name, binds, bool(x.get("isBinary"))))
    return exprs, springs


def has_image(mat):
    return bool(mat and mat.use_nodes and any(n.type == "TEX_IMAGE" and n.image for n in mat.node_tree.nodes))


def bake_vertex_colors(o, size=256):
    """Цвет в вершинах (хвост Клавы: голубой → белый кончик) → картинка по UV. VRM вершинные цвета не несёт,
    без этого хвост приезжает белым. Треугольники растеризуются по UV с барицентрической смесью цветов углов."""
    me = o.data
    mats = [s.material for s in o.material_slots if s.material]
    if not me.color_attributes or not me.uv_layers or any(has_image(m) for m in mats):
        return False
    ca = me.color_attributes.active_color or me.color_attributes[0]
    uv = me.uv_layers.active.data
    me.calc_loop_triangles()
    img = np.zeros((size, size, 4), np.float32)
    got = np.zeros((size, size), bool)

    def loop_color(li):
        c = ca.data[me.loops[li].vertex_index] if ca.domain == "POINT" else ca.data[li]
        return np.array(c.color_srgb, np.float32)

    for tri in me.loop_triangles:
        p = np.array([uv[li].uv for li in tri.loops], np.float64) * size - 0.5
        c = np.array([loop_color(li) for li in tri.loops])
        x0, y0 = np.floor(p.min(0)).astype(int)
        x1, y1 = np.ceil(p.max(0)).astype(int)
        x0, y0, x1, y1 = max(x0, 0), max(y0, 0), min(x1, size - 1), min(y1, size - 1)
        if x1 < x0 or y1 < y0:
            continue
        xs, ys = np.meshgrid(np.arange(x0, x1 + 1), np.arange(y0, y1 + 1))
        d = (p[1, 1] - p[2, 1]) * (p[0, 0] - p[2, 0]) + (p[2, 0] - p[1, 0]) * (p[0, 1] - p[2, 1])
        if abs(d) < 1e-12:
            continue
        a = ((p[1, 1] - p[2, 1]) * (xs - p[2, 0]) + (p[2, 0] - p[1, 0]) * (ys - p[2, 1])) / d
        b = ((p[2, 1] - p[0, 1]) * (xs - p[2, 0]) + (p[0, 0] - p[2, 0]) * (ys - p[2, 1])) / d
        g = 1 - a - b
        inside = (a >= -0.02) & (b >= -0.02) & (g >= -0.02)
        if not inside.any():
            continue
        col = a[..., None] * c[0] + b[..., None] * c[1] + g[..., None] * c[2]
        img[ys[inside], xs[inside]] = col[inside]
        got[ys[inside], xs[inside]] = True
    if not got.any():
        return False
    # пустоту вокруг островов развёртки красим соседним цветом, чтобы на швах не проступал чёрный
    for _ in range(6):
        grow = ~got & (np.roll(got, 1, 0) | np.roll(got, -1, 0) | np.roll(got, 1, 1) | np.roll(got, -1, 1))
        if not grow.any():
            break
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            m = grow & np.roll(got, (dy, dx), (0, 1)) & ~got
            img[m] = np.roll(img, (dy, dx), (0, 1))[m]
            got |= m
    img[..., 3] = 1.0
    im = bpy.data.images.new(f"{o.name}_цвет", size, size, alpha=True)
    im.pixels.foreach_set(img.ravel())
    im.pack()
    for m in mats:
        if not m.use_nodes:
            m.use_nodes = True
        nt = m.node_tree
        bsdf = next((n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"), None)
        if bsdf is None:
            continue
        tex = nt.nodes.new("ShaderNodeTexImage")
        tex.image = im
        for link in list(bsdf.inputs["Base Color"].links):
            nt.links.remove(link)
        bsdf.inputs["Base Color"].default_value = (1, 1, 1, 1)
        nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    return True


def main():
    arms = [o for o in bpy.data.objects if o.type == "ARMATURE"]
    if not arms:
        raise SystemExit("в .blend нет скелета — VRM без скелета не бывает")
    arm = max(arms, key=lambda o: len(o.data.bones))
    hips = next((b.name for b in arm.data.bones if b.name.lower().endswith("hips")), arm.data.bones[0].name)

    # слои меха (…_слойN) — приём для живой Клавы: в VRM из пяти-шести слоёв выходят пятнистые «доски»,
    # а не пух. Уши и хвост остаются своими основными сетками.
    for o in [o for o in bpy.data.objects if o.type == "MESH" and "_слой" in o.name]:
        bpy.data.objects.remove(o, do_unlink=True)

    # К какой кости основного скелета объект был прицеплен на самом деле: идём вверх по родителям
    # (ухо → пустышки → кость головы). Раньше всё непривязанное цеплялось к тазу — уши не ходили за головой.
    def anchor_bone(o):
        p, child = o.parent, o
        while p is not None:
            if p is arm:
                return child.parent_bone if child.parent_type == "BONE" and child.parent_bone else hips
            child, p = p, p.parent
        return hips

    rigid = []
    for o in bpy.data.objects:
        if o.type != "MESH" or not o.visible_get():
            continue
        if any(m.type == "ARMATURE" and m.object is not None for m in o.modifiers):
            continue   # скиннинг (свой или другого скелета) — разберёмся при слиянии скелетов
        rigid.append((o, anchor_bone(o), o.matrix_world.copy()))

    # Другие скелеты (хвост) вливаем в основной: кости сохраняются, корень цепляется к своей кости
    # (у хвоста — к тазу), сетки переключаются на основной скелет. Хвост остаётся гибким и получит пружину.
    extra_roots = []
    skinned_saved = []   # сетки чужих скелетов: join переподцепляет их без учёта поворота — ставим на место сами
    for a in arms:
        if a is arm:
            continue
        attach = anchor_bone(a)
        extra_roots += [(b.name, attach) for b in a.data.bones if b.parent is None]
        for o in bpy.data.objects:
            if o.type == "MESH":
                for m in o.modifiers:
                    if m.type == "ARMATURE" and m.object is a:
                        m.object = arm
                        skinned_saved.append((o, o.matrix_world.copy()))
        a.hide_set(False)
        a.hide_select = False
    for o in bpy.data.objects:
        o.hide_set(False) if o.type == "ARMATURE" else None
    arm.hide_select = False
    if len(arms) > 1:
        bpy.ops.object.select_all(action="DESELECT")
        for a in arms:
            a.select_set(True)
        bpy.context.view_layer.objects.active = arm
        bpy.ops.object.join()

    # скелет — в корень сцены (пустышки glTF-импорта сверху держали поворот модели)
    mw = arm.matrix_world.copy()
    arm.parent = None
    arm.matrix_world = mw

    if extra_roots:
        bpy.context.view_layer.objects.active = arm
        bpy.ops.object.mode_set(mode="EDIT")
        eb = arm.data.edit_bones
        for root, attach in extra_roots:
            if root in eb and attach in eb:
                eb[root].parent = eb[attach]
                eb[root].use_connect = False
        bpy.ops.object.mode_set(mode="OBJECT")

    bpy.context.view_layer.update()
    for o, w in skinned_saved:
        drift = max(abs(a - b) for ra, rb in zip(o.matrix_world, w) for a, b in zip(ra, rb))
        o.parent = arm
        o.parent_type = "OBJECT"
        o.matrix_world = w
        print(f"VRMEXPORT {o.name}: на место после слияния скелетов (уехала на {drift:.3f})")
    for o in bpy.data.objects:
        if o.type == "MESH" and any(m.type == "ARMATURE" and m.object is arm for m in o.modifiers) and o.parent is not arm:
            w = o.matrix_world.copy()
            o.parent = arm
            o.parent_type = "OBJECT"
            o.matrix_world = w
    for o, bone, w in rigid:
        o.parent = arm
        o.parent_type = "BONE"
        o.parent_bone = bone
        o.matrix_world = w
    bone_names = {b.name for b in arm.data.bones}
    print("VRMEXPORT хвостовые/доп. цепочки:", ", ".join(f"{r}→{a}" for r, a in extra_roots) or "нет")
    baked = [o.name for o in bpy.data.objects if o.type == "MESH" and o.visible_get() and bake_vertex_colors(o)]
    print("VRMEXPORT цвет из вершин → текстура:", ", ".join(baked) or "нет")

    ext = arm.data.vrm_addon_extension
    ext.spec_version = ext.SPEC_VERSION_VRM0
    v0 = ext.vrm0
    bpy.context.view_layer.objects.active = arm
    bpy.ops.vrm.assign_vrm0_humanoid_human_bones_automatically(armature_object_name=arm.name)
    v0.meta.title = bpy.path.display_name_from_filepath(bpy.data.filepath) or "Клава"
    if not v0.meta.author:
        v0.meta.author = "KOT"

    exprs, springs = source_tables(SOURCE)

    # эмоции: одна привязка на каждый кусок лица, у которого есть форма с таким номером
    face_objs = [o for o in bpy.data.objects if o.type == "MESH" and o.data.shape_keys and o.visible_get()]
    groups = v0.blend_shape_master.blend_shape_groups
    groups.clear()
    made = []
    for preset, name, binds, binary in exprs:
        g = groups.add()
        g.name = name
        g.preset_name = preset if preset in {i.identifier for i in g.bl_rna.properties["preset_name"].enum_items} else "unknown"
        g.is_binary = binary
        n = 0
        for index, weight in binds:
            key = str(index)
            for o in face_objs:
                if key in o.data.shape_keys.key_blocks:
                    b = g.binds.add()
                    b.mesh.mesh_object_name = o.name
                    b.index = key
                    b.weight = weight
                    n += 1
        made.append(f"{name}:{n}")
    print("VRMEXPORT эмоции", " ".join(made))

    # пружины волос и груди из исходника; кости сверяем по именам (у VRoid они одинаковые)
    sec = v0.secondary_animation
    sec.bone_groups.clear()
    kept = 0
    for s in springs:
        bones = [b for b in s["bones"] if b in bone_names]
        if not bones:
            continue
        g = sec.bone_groups.add()
        g.comment = s["comment"]
        g.stiffiness = s["stiffness"]
        g.gravity_power = s["gravity"]
        d = s["dir"]
        g.gravity_dir = (d.get("x", 0.0), -d.get("z", 0.0), d.get("y", -1.0))   # glTF (Y вверх) → Blender (Z вверх)
        g.drag_force = s["drag"]
        g.hit_radius = s["radius"]
        if s["center"] in bone_names:
            g.center.bone_name = s["center"]
        for b in bones:
            g.bones.add().bone_name = b
        kept += 1
    # влитые цепочки (хвост) — своя пружина: мягко качается и чуть провисает
    for root, _ in extra_roots:
        if root not in bone_names:
            continue
        g = sec.bone_groups.add()
        g.comment = "Хвост"
        g.stiffiness = 0.55
        g.gravity_power = 0.15
        g.gravity_dir = (0.0, 0.0, -1.0)
        g.drag_force = 0.35
        g.hit_radius = 0.03
        g.bones.add().bone_name = root
        kept += 1
    print(f"VRMEXPORT пружины: {kept} групп (из исходника {len(springs)} + хвост)")

    result = bpy.ops.export_scene.vrm(filepath=OUT, armature_object_name=arm.name, ignore_warning=True)
    print("VRMEXPORT итог", result, OUT)
    if "FINISHED" not in result:
        raise SystemExit(1)


main()
