#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""
scan_tk_bundles.py  (версия для двойного клика)
Открывает каждый .tk и печатает, что внутри: MapData / CollisionModelData / террейн,
сколько мешей и содержит ли бандл нужные model-id (коллизия зданий).

Запуск:
  * ДВОЙНОЙ КЛИК — скрипт сам спросит путь к папке и не закроется, пока не нажмёшь Enter.
  * из cmd:  python scan_tk_bundles.py "D:\Ground War Tanks\AssetBundles\1"
В конце создаёт рядом tk_manifest.txt и печатает список нужных бандлов.
"""
import os, sys, glob, argparse, traceback

DEFAULT_TARGET = "17025-17077,8011,8016,8025,8026,8028,8046,8051,8053,8059,8069"
# Имена террейн-мешей карт (для поиска бандлов карт: Москва, Кан и т.д.)
SCENE_WORDS = ("moscow","msk","normandy","caen","kaang","stalingrad","dota","caucas",
               "berlin","alm","aqqaqir","iceworld","xuedi","terrain","lyon","alkarfag")
ROCK_WORDS  = ("shitou","yanshi","rock","stone","boulder","shishan")


def parse_ids(spec):
    ids = set()
    for part in spec.split(","):
        part = part.strip()
        if not part:
            continue
        if "-" in part:
            a, b = part.split("-")
            ids.update(range(int(a), int(b) + 1))
        else:
            ids.add(int(part))
    return ids


def obj_name(obj):
    for getter in (lambda: obj.read().m_Name, lambda: obj.read().name, lambda: obj.peek_name()):
        try:
            n = getter()
            if n:
                return str(n)
        except Exception:
            pass
    return ""


def id_prefix(name):
    # Имя меша обычно вида "{номер_бандла}_{model-id}_имя" (напр. 107_4009_0_qiang),
    # т.е. НУЖНЫЙ model-id — ВТОРОЕ число. Если такого шаблона нет — берём первое.
    import re as _re
    m = _re.match(r"\d+_(\d+)", name)
    if m:
        return int(m.group(1))
    m = _re.match(r"(\d+)", name)
    return int(m.group(1)) if m else None


def scan_file(UnityPy, path, target_ids):
    info = {"meshes": 0, "mesh_names": [], "hit_ids": set(), "mono": [], "has_terrain": False, "scene": set(), "rock": set()}
    try:
        env = UnityPy.load(path)
    except Exception as e:
        info["error"] = str(e)
        return info
    for obj in env.objects:
        t = obj.type.name
        if t == "Mesh":
            info["meshes"] += 1
            nm = obj_name(obj)
            if nm:
                if len(info["mesh_names"]) < 8:
                    info["mesh_names"].append(nm)
                pid = id_prefix(nm)
                if pid is not None and pid in target_ids:
                    info["hit_ids"].add(pid)
                low = nm.lower()
                if any(w in low for w in SCENE_WORDS):
                    info.setdefault("scene", set()).add(nm)
                if any(w in low for w in ROCK_WORDS):
                    info.setdefault("rock", set()).add(nm)
        elif t == "MonoBehaviour":
            nm = obj_name(obj)
            if nm in ("MapData", "CollisionModelData", "ServerMapData") and nm not in info["mono"]:
                info["mono"].append(nm)
        elif t == "TerrainData":
            info["has_terrain"] = True
    return info


def run():
    ap = argparse.ArgumentParser()
    ap.add_argument("folder", nargs="?", default=None)
    ap.add_argument("--ids", default=DEFAULT_TARGET)
    ap.add_argument("--out", default="tk_manifest.txt")
    a = ap.parse_args()

    folder = a.folder
    if not folder:
        print("Укажи папку с бандлами .tk.")
        print("Можно ПЕРЕТАЩИТЬ папку прямо в это окно и нажать Enter,")
        print(r'либо вписать путь, например: D:\Ground War Tanks\AssetBundles\1')
        folder = input("Папка: ").strip().strip('"').strip("'")
    if not folder or not os.path.isdir(folder):
        print("\nПапка не найдена: %r" % folder)
        return

    try:
        import UnityPy
    except ImportError:
        print("\nНе установлен UnityPy. Установи:  python -m pip install UnityPy")
        return

    target = parse_ids(a.ids)
    files = sorted(glob.glob(os.path.join(folder, "**", "*.tk"), recursive=True))
    if not files:
        files = sorted(glob.glob(os.path.join(folder, "**", "*.unity3d"), recursive=True))
    if not files:
        print("\nВ папке нет .tk / .unity3d файлов: %s" % folder)
        return

    print("\nСканирую %d бандлов (ищу id %d..%d, %d шт.)…\n" % (len(files), min(target), max(target), len(target)))
    want, map_bundles, lines = [], [], []
    scene_bundles, rock_bundles = [], []
    for i, p in enumerate(files):
        info = scan_file(UnityPy, p, target)
        base = os.path.basename(p)
        if info.get("error"):
            line = "%-16s  ОШИБКА: %s" % (base, info["error"][:60])
        else:
            tags = []
            if info["mono"]:        tags.append("+".join(info["mono"]))
            if info["has_terrain"]: tags.append("TerrainData")
            if info["meshes"]:      tags.append("%d meshes" % info["meshes"])
            sample = (" | " + ", ".join(info["mesh_names"])) if info["mesh_names"] else ""
            line = "%-16s  %s%s" % (base, ", ".join(tags) or "(нет геометрии/карт)", sample)
            if info["hit_ids"]:
                line += "\n                  *** СОДЕРЖИТ НУЖНЫЕ ID: %s ***" % sorted(info["hit_ids"])
                want.append((base, sorted(info["hit_ids"])))
            if info.get("scene"):
                line += "\n                  >>> ТЕРРЕЙН/КАРТА: %s <<<" % sorted(info["scene"])[:3]
                scene_bundles.append((base, sorted(info["scene"])[:3]))
            if info.get("rock"):
                rock_bundles.append((base, sorted(info["rock"])[:3]))
            if info["mono"] or info["has_terrain"] or info.get("scene"):
                map_bundles.append(base)
        lines.append(line)
        if (i + 1) % 50 == 0:
            print("  …%d/%d" % (i + 1, len(files)))

    out_path = os.path.join(os.path.dirname(os.path.abspath(sys.argv[0])), a.out)
    with open(out_path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
    print("\nПолный отчёт сохранён: %s\n" % out_path)

    print("=" * 60)
    if want:
        print("БАНДЛЫ С НУЖНЫМИ МЕШАМИ КОЛЛИЗИИ (пришли эти файлы):")
        for base, ids in want:
            print("   %s   id: %s" % (base, ids))
    else:
        print("Бандлов с искомыми id НЕ найдено.")
        print("Тогда пришли БАНДЛЫ КАРТ из списка ниже (Москва/Кан/Кавказ) —")
        print("вытащу геометрию через UnityPy + Assembly-CSharp.dll.")
    print("-" * 60)
    if scene_bundles:
        print("БАНДЛЫ ТЕРРЕЙНОВ КАРТ (ищи тут Москву и Кан/Normandy):")
        for b, names in scene_bundles:
            print("   %s   -> %s" % (b, names))
    print("-" * 60)
    if rock_bundles:
        print("БАНДЛЫ КАМНЕЙ (для коллизии валунов):")
        for b, names in rock_bundles[:30]:
            print("   %s   -> %s" % (b, names))
    print("-" * 60)
    if map_bundles:
        print("Прочие бандлы с MapData, всего %d (первые 40):" % len(map_bundles))
        for b in map_bundles[:40]:
            print("   %s" % b)
    print("=" * 60)


if __name__ == "__main__":
    try:
        run()
    except Exception:
        print("\n--- ОШИБКА ---")
        traceback.print_exc()
    input("\nГотово. Нажми Enter, чтобы закрыть окно.")
