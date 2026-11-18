# Сборка «Клава.blend» из «Клава.glb», который выгружает Редактор Клавы (кнопка «Экспорт для Blender»).
# Запуск без окна:  blender -b --python build_blend.py   (или двойной клик по «Пересобрать Клава.blend.bat»)
#
# Что делает: импортирует GLB (скелет, формы лица, каждая часть — отдельный объект), раскладывает части по
# коллекциям, то, что у Клавы спрятано, кладёт в «Спрятанное» выключенным, ставит камеру и свет,
# включает вид с материалами и упаковывает картинки внутрь .blend — файл можно носить одним куском.
# Скрипт для Blender пишется только на Python (bpy) — другого API у Blender нет.
import json
import os

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
GLB = os.path.join(HERE, "Клава.glb")
PARTS = os.path.join(HERE, "Клава.parts.json")
OUT = os.path.join(HERE, "Клава.blend")

# Какой кусок в какую коллекцию: имя объекта начинается с подписи из редактора («Лицо · Facebaked»).
GROUPS = [
    ("Мех (слои)", lambda n: "_слой" in n),
    ("Кимоно", lambda n: n.startswith("Кимоно")),
    ("Уши", lambda n: n.startswith("Ухо")),
    ("Хвост", lambda n: n.startswith("Хвост")),
    ("Волосы", lambda n: n.startswith(("Волосы", "Пряди волос"))),
    ("Лицо и глаза", lambda n: n.startswith(("Лицо", "Рот", "Белки глаз", "Радужки", "Блики глаз", "Глаза", "Подводка", "Ресницы", "Брови"))),
    ("Тело", lambda n: n.startswith(("Тело", "Обувь", "Родной костюм"))),
]
HIDDEN_COLL = "Спрятанное (включи глаз — наденется)"


def collection(name, parent=None):
    c = bpy.data.collections.get(name) or bpy.data.collections.new(name)
    parent = parent or bpy.context.scene.collection
    if c.name not in [x.name for x in parent.children]:
        parent.children.link(c)
    return c


def move(obj, coll):
    for c in list(obj.users_collection):
        c.objects.unlink(obj)
    coll.objects.link(obj)


def main():
    if not os.path.exists(GLB):
        raise SystemExit(f"Нет {GLB} — сначала нажми «Экспорт для Blender» в Редакторе Клавы")
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=GLB, bone_heuristic="TEMPERANCE")

    hidden = set()
    if os.path.exists(PARTS):
        with open(PARTS, encoding="utf-8-sig") as f:   # редактор может записать с BOM
            hidden = set(json.load(f).get("hidden", []))

    root = collection("Клава")
    colls = {name: collection(name, root) for name, _ in GROUPS}
    colls["Прочее"] = collection("Прочее", root)
    hidden_coll = collection(HIDDEN_COLL, root)
    rig_coll = collection("Скелет", root)

    for obj in list(bpy.data.objects):
        if obj.type == "ARMATURE":
            obj.name = "Скелет Клавы"
            obj.show_in_front = True
            obj.data.display_type = "STICK"
            move(obj, rig_coll)
            continue
        if obj.type != "MESH":
            continue
        if obj.name in hidden:
            move(obj, hidden_coll)
            obj.hide_set(True)
            obj.hide_render = True
            continue
        target = next((colls[name] for name, rule in GROUPS if rule(obj.name)), colls["Прочее"])
        move(obj, target)

    # пустые коллекции не нужны; пустышки узлов glTF (крепления ушей и т. п.) — в «Скелет»
    for obj in list(bpy.data.objects):
        if obj.type == "EMPTY":
            move(obj, rig_coll)
            obj.hide_set(True)
    for c in list(root.children):
        if not c.all_objects:
            bpy.data.collections.remove(c)

    # мир — мягкий серо-голубой свет со всех сторон: без него тени на рендере уходят в чёрное
    world = bpy.data.worlds.new("Мир")
    world.use_nodes = True
    bg = world.node_tree.nodes.get("Background")
    bg.inputs["Color"].default_value = (0.42, 0.45, 0.52, 1.0)
    bg.inputs["Strength"].default_value = 1.0
    bpy.context.scene.world = world

    # свет и камера: она смотрит на нас (вид спереди — Numpad 1)
    sun = bpy.data.objects.new("Солнце", bpy.data.lights.new("Солнце", "SUN"))
    sun.data.energy = 3.0
    sun.rotation_euler = (0.9, 0.2, 0.3)
    bpy.context.scene.collection.objects.link(sun)
    cam = bpy.data.objects.new("Камера", bpy.data.cameras.new("Камера"))
    cam.location = (0.0, -3.4, 0.95)
    cam.rotation_euler = (1.5708, 0.0, 0.0)
    cam.data.lens = 50
    bpy.context.scene.collection.objects.link(cam)
    bpy.context.scene.camera = cam

    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type == "VIEW_3D":
                for space in area.spaces:
                    if space.type == "VIEW_3D":
                        space.shading.type = "MATERIAL"

    bpy.context.preferences.filepaths.save_version = 0   # без Клава.blend1 рядом при пересборке
    bpy.ops.file.pack_all()
    bpy.ops.wm.save_as_mainfile(filepath=OUT)
    meshes = sum(1 for o in bpy.data.objects if o.type == "MESH")
    bones = sum(len(o.data.bones) for o in bpy.data.objects if o.type == "ARMATURE")
    print(f"КЛАВА: готово — {OUT}; объектов-мешей {meshes}, костей {bones}, спрятанных {len(hidden)}")


main()
