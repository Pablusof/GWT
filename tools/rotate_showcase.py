#!/usr/bin/env python3
"""
Ротация витрины главного приза «Золотого запаса».

Витрину (иконку танка справа от колеса + вкладку «Подробнее») клиент читает из level0
ОДИН РАЗ при запуске — таблица Table_New_bonusbox_power_reward, строка id=1, поле
reward_id. Живьём её сменить нельзя. Но level0 читается при КАЖДОМ старте игры,
поэтому если менять его перед запуском — витрина будет меняться от сессии к сессии.

Этот скрипт ставит в витрину СЛЕДУЮЩИЙ (или случайный) танк из списка и заодно
правит строку главного приза в самой таблице Table_New_bonusbox (чтобы «Подробнее» и
объявление совпадали). Прописывается в start.bat ПЕРЕД запуском Tank.exe.

Список витрины (по заявке):
    197 T95/Chieftain, 149 Obj279, 137 Object 140, 163 FV214,
    168 1K-17, 174 FV4030/4, 179 Object 263, 208 XM815 Chrysler

Порядок по умолчанию — по кругу (each start = следующий танк), состояние хранится в
tools/.showcase_state. Флаг --random — случайный вместо циклического.

Запуск (обычно из start.bat, но можно вручную):
    python tools/rotate_showcase.py
    python tools/rotate_showcase.py --random
    python tools/rotate_showcase.py --level0 "D:/.../Tank_Data/level0"

Быстрый режим без правки таблицы (только картинка):  --showcase-only
"""
from __future__ import annotations

import argparse
import random
import sys
from pathlib import Path

# id танков для витрины (в порядке цикла).
SHOWCASE = [197, 149, 137, 163, 168, 174, 179, 208]
NAMES = {
    197: "T95/Chieftain", 149: "Obj279", 137: "Object 140", 163: "FV214",
    168: "1K-17", 174: "FV4030/4", 179: "Object 263", 208: "XM815 Chrysler",
}

FIELD_POWER = "_newbonusboxPowerArray"
FIELD_BONUS = "_newbonusboxArray"
STATE_FILE = Path(__file__).resolve().parent / ".showcase_state"


def find_level0(explicit):
    if explicit:
        p = Path(explicit)
        if not p.exists():
            sys.exit(f"level0 не найден: {p}")
        return p
    here = Path(__file__).resolve()
    for cand in [
        here.parents[2] / "Ground War Tanks" / "Tank_Data" / "level0",
        here.parents[2] / "Tank_Data" / "level0",
        Path.cwd() / "Tank_Data" / "level0",
        Path.cwd() / "level0",
    ]:
        if cand.exists():
            return cand
    sys.exit('Не нашёл level0. Укажите путь: --level0 "…/Tank_Data/level0"')


def next_id(use_random: bool) -> int:
    if use_random:
        return random.choice(SHOWCASE)
    # циклический выбор: читаем прошлый индекс, берём следующий
    idx = 0
    try:
        idx = (int(STATE_FILE.read_text().strip()) + 1) % len(SHOWCASE)
    except Exception:
        idx = 0
    try:
        STATE_FILE.write_text(str(idx))
    except Exception:
        pass
    return SHOWCASE[idx]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--level0", default=None)
    ap.add_argument("--game", default=None)
    ap.add_argument("--random", action="store_true", help="случайный танк (иначе по кругу)")
    ap.add_argument("--showcase-only", action="store_true",
                    help="менять только картинку витрины, не трогая таблицу призов")
    ap.add_argument("--id", type=int, default=None, help="принудительно конкретный id")
    ap.add_argument("--sync-server", default=None,
                    help="путь к bonusboxes.json сервера — синхронизировать главный приз "
                         "(если сервер на этой же машине)")
    args = ap.parse_args()

    level0 = find_level0(args.level0)
    tank_data = level0.parent
    game_root = Path(args.game) if args.game else tank_data.parent

    new_id = args.id if args.id is not None else next_id(args.random)

    try:
        import UnityPy
        from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator
    except ImportError as exc:
        sys.exit(f"Нужны UnityPy и TypeTreeGeneratorAPI: pip install UnityPy TypeTreeGeneratorAPI ({exc})")

    env = UnityPy.load(str(tank_data))   # вся папка — иначе не резолвится m_Script
    gen = TypeTreeGenerator("5.6.1f1")
    gen.load_local_game(str(game_root))
    env.typetree_generator = gen

    target = None
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            nodes = obj.generate_monobehaviour_node()
            tree = obj.read_typetree(nodes=nodes, check_read=False)
        except Exception:
            continue
        if isinstance(tree, dict) and FIELD_POWER in tree:
            target = (obj, nodes, tree)
            break
    if target is None:
        sys.exit(f"Не найден контейнер с {FIELD_POWER} (проверьте Managed/*.dll и globalgamemanagers.assets).")

    obj, nodes, tree = target
    rows = tree.get(FIELD_POWER) or []
    if not rows:
        sys.exit(f"{FIELD_POWER} пуст.")
    old = int(rows[0].get("reward_id", 0))
    rows[0]["reward_id"] = new_id

    if not args.showcase_only:
        for r in tree.get(FIELD_BONUS, []) or []:
            if int(r.get("reward_level", 0)) == 1:
                r["reward_itemid"] = new_id

    obj.save_typetree(tree, nodes=nodes)

    level0_file = None
    for name, f in env.files.items():
        if Path(str(name)).name == "level0":
            level0_file = f
            break
    if level0_file is None:
        sys.exit("level0 не найден среди файлов окружения.")

    level0.write_bytes(level0_file.save())
    nm = NAMES.get(new_id, str(new_id))
    print(f"[showcase] витрина: {old} -> {new_id} ({nm})")

    # Опционально: синхронизируем главный приз в bonusboxes.json сервера, чтобы
    # сервер ВЫДАВАЛ ровно тот танк, что на витрине (когда сервер на этой же машине).
    if args.sync_server:
        try:
            import json
            sp = Path(args.sync_server)
            data = json.loads(sp.read_text(encoding="utf-8"))
            for r in data.get("Bonusboxes", []):
                if r.get("RewardLevel") == 1:
                    r["RewardItemId"] = new_id
            for r in data.get("PowerRewards", []):
                r["RewardId"] = new_id
            sp.write_text(json.dumps(data, ensure_ascii=False, indent=1), encoding="utf-8")
            print(f"[showcase] сервер синхронизирован: главный приз = {new_id}")
        except Exception as exc:  # noqa: BLE001
            print(f"[showcase] не удалось синхронизировать сервер ({exc}) — витрина всё равно обновлена")


if __name__ == "__main__":
    main()
