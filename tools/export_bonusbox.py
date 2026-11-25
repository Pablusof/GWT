#!/usr/bin/env python3
"""
Экспорт таблиц рулетки «Золотого запаса» из клиентского level0 в JSON, который
сервер подхватывает ОДИН В ОДИН.

Запускать НА МАШИНЕ С ИГРОЙ: там рядом с level0 лежат Unity-DLL (Managed/), и
typetree-генератор корректно разрешает типы. В песочнице сервера этих DLL нет,
поэтому скрипт живёт отдельно и запускается вручную.

Выгружает две таблицы:
  • Table_New_bonusbox              → сектора колеса и все призы (что реально выдаётся)
  • Table_New_bonusbox_power_reward → витрина главного приза справа (иконка/«Подробнее»)

Результат: bonusboxes.json — кладётся в GwtEmulator.Server/GameTables/Data/.
Сервер при старте берёт его С НАИВЫСШИМ ПРИОРИТЕТОМ (выше client_game_tables.json и
встроенной раскладки). После подмены рулетка на клиенте и выдача на сервере совпадут
построчно, включая ресурсные секторы.

──────────────────────────────────────────────────────────────────────────────
УСТАНОВКА (один раз):
    pip install UnityPy TypeTreeGeneratorAPI

ЗАПУСК (из корня репозитория, рядом с папкой "Ground War Tanks"):
    python tools/export_bonusbox.py

    # если структура папок другая — укажите пути явно:
    python tools/export_bonusbox.py --level0 "C:/.../Ground War Tanks/Tank_Data/level0"
    python tools/export_bonusbox.py --game   "C:/.../Ground War Tanks"
    python tools/export_bonusbox.py --out    "GwtEmulator.Server/GameTables/Data/bonusboxes.json"

После успешного запуска перезапустите сервер и проверьте в логе строку:
    [GameTables] bonusboxes.json: N строк рулетки (танков …, главный приз …)
──────────────────────────────────────────────────────────────────────────────
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

UNITY_VERSION = "5.6.1f1"        # версия движка GWT (как в остальных инструментах)

# Имена полей-массивов внутри контейнера таблиц (сверено с client GameTableMgr.cs).
FIELD_BONUSBOX = "_newbonusboxArray"
FIELD_POWER    = "_newbonusboxPowerArray"


def find_level0(explicit: str | None) -> Path:
    if explicit:
        p = Path(explicit)
        if not p.exists():
            sys.exit(f"level0 не найден по указанному пути: {p}")
        return p
    # ищем от корня репозитория: ../.. / "Ground War Tanks" / Tank_Data / level0
    root = Path(__file__).resolve().parents[2]
    for cand in [
        root / "Ground War Tanks" / "Tank_Data" / "level0",
        root / "Tank_Data" / "level0",
        Path.cwd() / "Ground War Tanks" / "Tank_Data" / "level0",
    ]:
        if cand.exists():
            return cand
    sys.exit(
        "Не нашёл level0 автоматически. Укажите путь явно:\n"
        '    python tools/export_bonusbox.py --level0 "…/Ground War Tanks/Tank_Data/level0"'
    )


def find_game_root(explicit: str | None, level0: Path) -> Path:
    if explicit:
        p = Path(explicit)
        if not p.exists():
            sys.exit(f"Папка игры не найдена: {p}")
        return p
    # game root = папка, содержащая Tank_Data (в ней есть Managed/*.dll)
    # level0 = <game>/Tank_Data/level0  →  game = level0.parent.parent
    return level0.parent.parent


def as_int(v, default=0) -> int:
    try:
        return int(v)
    except (TypeError, ValueError):
        return default


def as_str(v) -> str:
    return "" if v is None else str(v)


def load_env_and_generator(level0: Path, game_root: Path):
    try:
        import UnityPy
    except ImportError:
        sys.exit("Не установлен UnityPy. Выполните: pip install UnityPy")
    try:
        from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator
    except ImportError:
        sys.exit(
            "Не установлен TypeTreeGeneratorAPI (нужен для чтения типов из DLL).\n"
            "Выполните: pip install TypeTreeGeneratorAPI"
        )

    env = UnityPy.load(str(level0.parent))   # ВСЯ папка Tank_Data, а не только level0:
    # MonoBehaviour ссылается на globalgamemanagers.assets (m_Script), и без него
    # deref скрипта падает с FileNotFoundError. Грузим папку целиком — все ссылки
    # резолвятся.
    gen = TypeTreeGenerator(UNITY_VERSION)
    try:
        gen.load_local_game(str(game_root))
    except Exception as exc:  # noqa: BLE001
        sys.exit(
            f"Не удалось загрузить сборки игры из {game_root}:\n    {exc}\n\n"
            "Проверьте, что это корень игры и в нём есть <Tank_Data>/Managed/*.dll "
            "(Assembly-CSharp.dll, UnityEngine.dll, mscorlib.dll)."
        )
    env.typetree_generator = gen
    return env


def read_container_tree(env):
    """
    Находит MonoBehaviour, чей typetree содержит массивы таблиц призов, и возвращает
    его дерево. Перебираем все MonoBehaviour, а не хардкодим path_id: он различается
    между сборками.
    """
    best = None
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            nodes = obj.generate_monobehaviour_node()
            tree = obj.read_typetree(nodes=nodes, check_read=False)
        except Exception:
            continue
        if isinstance(tree, dict) and (FIELD_BONUSBOX in tree or FIELD_POWER in tree):
            # берём дерево, где есть основная таблица; если в разных объектах —
            # объединяем
            if best is None:
                best = tree
            else:
                for k in (FIELD_BONUSBOX, FIELD_POWER):
                    if k in tree and k not in best:
                        best[k] = tree[k]
            if FIELD_BONUSBOX in best and FIELD_POWER in best:
                break
    if best is None:
        _diagnose_missing(env)
        sys.exit(
            "В level0 не найден контейнер с таблицами призов "
            f"({FIELD_BONUSBOX} / {FIELD_POWER}).\n"
            "Смотрите диагностику выше."
        )
    return best


def _diagnose_missing(env) -> None:
    """Подсказки, если контейнер не найден: обычно не хватает Unity-DLL для typetree."""
    gen = getattr(env, "typetree_generator", None)
    print("\n── Диагностика ─────────────────────────────────────────────")
    try:
        dlls = gen.get_loaded_dll_names() if gen else []
        print(f"Загруженные сборки: {', '.join(dlls) if dlls else '(нет)'}")
        need = {"Assembly-CSharp.dll", "UnityEngine.dll", "mscorlib.dll"}
        missing = [d for d in need if d not in set(dlls)]
        if missing:
            print(f"НЕ ХВАТАЕТ сборок: {', '.join(missing)}")
            print("Они должны лежать в <корень игры>/Tank_Data/Managed/ (или <...>_Data/Managed/).")
            print("Без mscorlib.dll и UnityEngine.dll типы не разрешаются и таблицы не читаются.")
    except Exception as exc:  # noqa: BLE001
        print(f"Не удалось перечислить сборки: {exc}")

    # сколько MonoBehaviour вообще прочиталось
    total = read_ok = 0
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        total += 1
        try:
            obj.read_typetree(nodes=obj.generate_monobehaviour_node(), check_read=False)
            read_ok += 1
        except Exception:
            pass
    print(f"MonoBehaviour: всего {total}, успешно прочитано деревьев {read_ok}.")
    if read_ok == 0:
        print("Ни одного дерева не прочиталось → почти наверняка не хватает Unity-DLL.")
    print("────────────────────────────────────────────────────────────\n")


def extract_bonusboxes(tree) -> list[dict]:
    rows = []
    for item in tree.get(FIELD_BONUSBOX, []) or []:
        rows.append({
            "Id":              as_int(item.get("id")),
            "RewardLevel":     as_int(item.get("reward_level")),
            "DropRate":        as_int(item.get("drop_rate")),
            "MinValue":        as_int(item.get("min_value")),
            "MaxValue":        as_int(item.get("max_value")),
            "RewardItemType":  as_int(item.get("reward_itemtype")),
            "RewardItemId":    as_int(item.get("reward_itemid")),
            "RewardItemCount": as_int(item.get("reward_itemcount")),
            # строковые поля не нужны серверу, но сохраним стиль для отладки
            "_namestyle":      as_str(item.get("reward_namestyle")),
        })
    rows.sort(key=lambda r: r["Id"])
    return rows


def extract_power(tree) -> list[dict]:
    rows = []
    for item in tree.get(FIELD_POWER, []) or []:
        rows.append({
            "Id":             as_int(item.get("ID") if "ID" in item else item.get("id")),
            "ActiveRatio":    as_int(item.get("active_ratio")),
            "RewardType":     as_int(item.get("reward_type")),
            "RewardCount":    as_int(item.get("reward_count")),
            "RewardDiscount": as_int(item.get("reward_discount")),
            "RewardId":       as_int(item.get("reward_id")),
            "RewardGoldValue": as_int(item.get("reward_goldvalue")),
        })
    rows.sort(key=lambda r: r["Id"])
    return rows


def main() -> None:
    ap = argparse.ArgumentParser(description="Экспорт таблиц рулетки GWT в JSON для сервера")
    ap.add_argument("--level0", default=None, help="путь к клиентскому level0")
    ap.add_argument("--game", default=None, help="корень игры (папка с Tank_Data/Managed)")
    ap.add_argument(
        "--out",
        default=str(Path(__file__).resolve().parents[1]
                    / "GwtEmulator.Server" / "GameTables" / "Data" / "bonusboxes.json"),
        help="куда записать JSON (по умолчанию — в папку данных сервера)",
    )
    args = ap.parse_args()

    level0 = find_level0(args.level0)
    game_root = find_game_root(args.game, level0)
    print(f"level0:   {level0}")
    print(f"game:     {game_root}")

    env = load_env_and_generator(level0, game_root)
    tree = read_container_tree(env)

    bonusboxes = extract_bonusboxes(tree)
    power = extract_power(tree)

    if not bonusboxes:
        sys.exit("Таблица Table_New_bonusbox пуста — экспорт остановлен.")

    payload = {
        "_comment": "Выгружено tools/export_bonusbox.py из клиентского level0. "
                    "Сервер читает это с наивысшим приоритетом.",
        "Bonusboxes": bonusboxes,
        "PowerRewards": power,
    }

    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(payload, ensure_ascii=False, indent=1), encoding="utf-8")

    tanks = sum(1 for r in bonusboxes if r["RewardItemType"] == 4)
    mains = [r["RewardItemId"] for r in bonusboxes if r["RewardItemType"] == 4 and r["RewardLevel"] == 1]
    print()
    print(f"Готово: {out}")
    print(f"  строк рулетки:        {len(bonusboxes)}")
    print(f"  из них танков:        {tanks}")
    print(f"  главный приз (itemid): {mains if mains else '— (строка reward_level=1 не найдена)'}")
    print(f"  строк витрины power:  {len(power)}"
          + (f" (reward_id={power[0]['RewardId']})" if power else ""))
    print()
    print("Скопируйте bonusboxes.json в GwtEmulator.Server/GameTables/Data/ (если он")
    print("не туда записался) и перезапустите сервер. В логе появится строка")
    print("[GameTables] bonusboxes.json: … — значит сервер взял вашу таблицу.")


if __name__ == "__main__":
    main()
