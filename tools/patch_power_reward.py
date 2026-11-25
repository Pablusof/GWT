#!/usr/bin/env python3
"""
Меняет КАРТИНКУ витрины главного приза «Золотого запаса» в клиенте.

Танк в рамке справа от колеса (иконка + «Подробнее») клиент рисует из level0 —
таблица Table_New_bonusbox_power_reward, поле reward_id. Сервер на эту картинку не
влияет, только правка level0.

Пул главного приза (тот же, что на сервере):
    149 Obj279, 137 Object 140, 163 FV214, 168 1K-17, 174 FV4030/4,
    179 Object 263, 208 XM815 Chrysler

Запуск (на машине с игрой; нужны Unity-DLL в Tank_Data/Managed/):
    python tools/patch_power_reward.py            # поставить FV4030/4 (174)
    python tools/patch_power_reward.py --id 163   # конкретный танк
    python tools/patch_power_reward.py --random   # случайный из пула
    python tools/patch_power_reward.py --id 174 --also-table  # + главный приз в таблице

Перед записью создаётся level0.bak. Игра должна быть закрыта.
Витрина статична: меняется только этим патчем, не «на лету».
"""
from __future__ import annotations

import argparse
import random
import shutil
import sys
from pathlib import Path

DEFAULT_ID = 174  # FV4030/4
POOL = [149, 137, 163, 168, 174, 179, 208]

FIELD_POWER = "_newbonusboxPowerArray"
FIELD_BONUS = "_newbonusboxArray"


def find_level0(explicit):
    if explicit:
        p = Path(explicit)
        if not p.exists():
            sys.exit(f"level0 не найден: {p}")
        return p
    root = Path(__file__).resolve().parents[2]
    for cand in [
        root / "Ground War Tanks" / "Tank_Data" / "level0",
        root / "Tank_Data" / "level0",
        Path.cwd() / "Tank_Data" / "level0",
        Path.cwd() / "level0",
    ]:
        if cand.exists():
            return cand
    sys.exit('Не нашёл level0. Укажите путь: --level0 "…/Tank_Data/level0"')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--level0", default=None)
    ap.add_argument("--game", default=None, help="корень игры (папка с Tank_Data/Managed)")
    ap.add_argument("--id", type=int, default=None, help="vehicle id для витрины")
    ap.add_argument("--random", action="store_true", help="случайный танк из пула")
    ap.add_argument("--also-table", action="store_true",
                    help="заменить и главный приз в самой таблице (reward_level=1)")
    args = ap.parse_args()

    new_id = random.choice(POOL) if args.random else (args.id if args.id is not None else DEFAULT_ID)

    level0 = find_level0(args.level0)
    tank_data = level0.parent
    game_root = Path(args.game) if args.game else tank_data.parent

    try:
        import UnityPy
        from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator
    except ImportError as exc:
        sys.exit(f"Нужны UnityPy и TypeTreeGeneratorAPI: pip install UnityPy TypeTreeGeneratorAPI ({exc})")

    # Грузим ВСЮ папку Tank_Data (не только level0): MonoBehaviour ссылается на
    # globalgamemanagers.assets, без него не резолвится скрипт таблицы.
    env = UnityPy.load(str(tank_data))
    gen = TypeTreeGenerator("5.6.1f1")
    gen.load_local_game(str(game_root))
    env.typetree_generator = gen

    # Ищем контейнер по имени поля, а не по path_id (он разный в сборках).
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
        sys.exit(f"Не найден контейнер с {FIELD_POWER}. "
                 "Проверьте, что рядом с level0 есть globalgamemanagers.assets и Managed/*.dll.")

    obj, nodes, tree = target
    rows = tree.get(FIELD_POWER) or []
    if not rows:
        sys.exit(f"{FIELD_POWER} пуст.")

    old = int(rows[0].get("reward_id", 0))
    rows[0]["reward_id"] = new_id

    if args.also_table:
        for r in tree.get(FIELD_BONUS, []) or []:
            if int(r.get("reward_level", 0)) == 1:
                r["reward_itemid"] = new_id

    backup = level0.with_suffix(".bak")
    if not backup.exists():
        shutil.copy2(level0, backup)
        print(f"Бэкап: {backup}")

    obj.save_typetree(tree, nodes=nodes)

    level0_file = None
    for name, f in env.files.items():
        if Path(str(name)).name == "level0":
            level0_file = f
            break
    if level0_file is None:
        sys.exit("Не нашёл level0 среди загруженных файлов окружения.")

    data = level0_file.save()
    level0.write_bytes(data)
    print(f"Готово: витрина reward_id {old} -> {new_id}"
          + (" (и главный приз в таблице)" if args.also_table else ""))
    print("Перезапустите клиент. Сервер выдаёт свой танк независимо — сверьте с логом сервера.")


if __name__ == "__main__":
    main()
