from __future__ import annotations

import json
from pathlib import Path

import UnityPy
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator


def split_ids(value: str | None) -> list[int]:
    if not value:
        return []
    result: list[int] = []
    for part in value.split("_"):
        if part and part != "0":
            try:
                result.append(int(part))
            except ValueError:
                continue
    return result


def as_int(value: object, default: int = 0) -> int:
    if value is None:
        return default
    if isinstance(value, bool):
        return int(value)
    if isinstance(value, (int, float)):
        return int(round(value))
    try:
        return int(str(value))
    except ValueError:
        return default


def build_shell_export(item: dict) -> dict:
    return {
        "Id": as_int(item.get("id")),
        "Name": item.get("name", ""),
        "Type": as_int(item.get("kind")),
        "Price": as_int(item.get("price")),
        "Currency": as_int(item.get("currency"), 1),
        "Damage": as_int(item.get("damage_hp")),
        "Penetration": as_int(item.get("ammo_pierce_100")),
    }


def build_part_export(module_type: int, item: dict, *, price_key: str, currency_key: str, value_key: str) -> dict:
    return {
        "Id": as_int(item.get("id")),
        "Name": item.get("name", ""),
        "ModuleType": module_type,
        "Tier": as_int(item.get("level"), 1),
        "Price": as_int(item.get(price_key)),
        "Currency": as_int(item.get(currency_key), 1),
        "Value": float(item.get(value_key, 0.0) or 0.0),
    }


def _find_game_root(repo_root: Path) -> Path:
    """
    Ищет папку игры (в которой есть Tank_Data/level0).

    Порядок:
      1. аргумент командной строки: extract_client_tables.py "D:\\...\\Ground War Tanks"
      2. переменная окружения GWT_GAME_ROOT
      3. стандартное место рядом с emuserverGWT: <repo_root>\\Ground War Tanks
      4. несколько типовых расположений (сама папка emuserverGWT, каталог выше и т.д.)

    Возвращает папку игры или бросает понятную ошибку со списком проверенных мест.
    """
    import os
    import sys

    def has_level0(p: Path) -> bool:
        return (p / "Tank_Data" / "level0").is_file()

    candidates: list[Path] = []

    # 1) явный аргумент
    if len(sys.argv) > 1 and sys.argv[1].strip():
        candidates.append(Path(sys.argv[1].strip()))

    # 2) переменная окружения
    env_root = os.environ.get("GWT_GAME_ROOT", "").strip()
    if env_root:
        candidates.append(Path(env_root))

    # 3) стандартное место
    candidates.append(repo_root / "Ground War Tanks")

    # 4) типовые расположения
    emu_dir = Path(__file__).resolve().parents[1]      # ...\emuserverGWT
    candidates.extend([
        emu_dir / "Ground War Tanks",
        emu_dir.parent / "Ground War Tanks",
        repo_root.parent / "Ground War Tanks",
        Path.cwd() / "Ground War Tanks",
    ])

    # если кандидат сам указывает прямо на level0 или на Tank_Data — поднимаемся
    normalized: list[Path] = []
    for c in candidates:
        if c.name.lower() == "level0" and c.is_file():
            normalized.append(c.parent.parent)
        elif c.name.lower() == "tank_data" and c.is_dir():
            normalized.append(c.parent)
        else:
            normalized.append(c)

    seen: set[str] = set()
    for c in normalized:
        key = str(c).lower()
        if key in seen:
            continue
        seen.add(key)
        if has_level0(c):
            return c

    checked = "\n".join(f"    - {c}" for c in normalized)
    raise SystemExit(
        "Не найдена папка игры с файлом Tank_Data\\level0.\n\n"
        "Проверены места:\n" + checked + "\n\n"
        "Что делать (любой вариант):\n"
        "  1) Положите папку \"Ground War Tanks\" рядом с папкой emuserverGWT\n"
        "     (то есть в " + str(repo_root) + ").\n"
        "  2) Либо перетащите папку игры прямо на .bat / укажите путь аргументом:\n"
        "     python tools\\extract_client_tables.py \"C:\\путь\\к\\Ground War Tanks\"\n"
        "  3) Либо задайте переменную окружения GWT_GAME_ROOT с этим путём.\n"
    )


def main() -> None:
    repo_root = Path(__file__).resolve().parents[2]
    game_root = _find_game_root(repo_root)
    level0_path = game_root / "Tank_Data" / "level0"
    output_path = repo_root / "emuserverGWT" / "GwtEmulator.Server" / "GameTables" / "Data" / "client_game_tables.json"
    # Если структура репозитория другая (папка называется не emuserverGWT), пишем
    # рядом со скриптом, лишь бы результат не потерялся.
    if not output_path.parent.parent.parent.exists():
        output_path = Path(__file__).resolve().parents[1] / "GwtEmulator.Server" / "GameTables" / "Data" / "client_game_tables.json"
    output_path.parent.mkdir(parents=True, exist_ok=True)

    print(f"Папка игры : {game_root}")
    print(f"level0     : {level0_path}")
    print(f"Результат  : {output_path}")
    print("Читаю level0 (это может занять несколько секунд)...")

    env = UnityPy.load(str(level0_path))
    try:
        generator = TypeTreeGenerator("5.6.1f1")
    except ImportError as exc:
        # UnityPy на месте, но нет отдельного нативного пакета TypeTreeGeneratorAPI —
        # именно он читает MonoBehaviour-таблицы. pip install UnityPy его НЕ тянет.
        raise SystemExit(
            "Не хватает пакета TypeTreeGeneratorAPI (" + str(exc) + ").\n\n"
            "Это отдельная библиотека, без неё таблицы прочитать нельзя.\n"
            "Установите её в этом же окне:\n\n"
            "    pip install TypeTreeGeneratorAPI\n\n"
            "Если pip не найден, попробуйте:\n"
            "    py -m pip install TypeTreeGeneratorAPI\n\n"
            "Готовые файлы идут для любого Python 3.6+, так что менять версию Python\n"
            "не нужно. После установки запустите этот .bat заново.\n"
        ) from exc
    generator.load_local_game(str(game_root))
    env.typetree_generator = generator

    game_table_obj = next(
        obj for obj in env.objects
        if obj.type.name == "MonoBehaviour"
        and obj.path_id == 211
    )
    tree = game_table_obj.read_typetree(nodes=game_table_obj.generate_monobehaviour_node(), check_read=False)

    hull_lookup = {as_int(item.get("id")): item for item in tree["_hullArray"]}
    turret_lookup = {as_int(item.get("id")): item for item in tree["_turretArray"]}

    tanks = []
    for item in tree["_vehicleArray"]:
        if as_int(item.get("id")) <= 0:
            continue
        hull = hull_lookup.get(as_int(item.get("hull")), {})
        turret = turret_lookup.get(as_int(item.get("turret")), {})
        tanks.append({
            "Id": as_int(item.get("id")),
            "Name": item.get("name", ""),
            "Tier": as_int(item.get("level"), 1),
            "Type": as_int(item.get("type")),
            "Nation": as_int(item.get("nation")),
            "VehicleType": as_int(item.get("vehicleType")),
            "Price": as_int(item.get("price")),
            "Currency": as_int(item.get("currency"), 1),
            "HullId": as_int(item.get("hull")),
            "UnderpanId": as_int(item.get("chassis")),
            "TurretId": as_int(item.get("turret")),
            "MainGunId": as_int(item.get("gun")),
            "EngineId": as_int(item.get("engine")),
            "FueltankId": as_int(item.get("fuelbox")),
            "CartridgeId": as_int(item.get("ammobox")),
            "RadioStationId": as_int(item.get("radio")),
            "GaugeId": as_int(item.get("watchpot")),
            "MaxHp": as_int(hull.get("hp_max"), 800),
            "Speed": as_int(item.get("speed_forward"), 30),
            "ViewRange": as_int(turret.get("view_range"), 300),
        })

    parts = []
    for item in tree["_hullArray"]:
        parts.append(build_part_export(1, item, price_key="price", currency_key="currency", value_key="hp_max"))
    for item in tree["_chassisArray"]:
        parts.append(build_part_export(2, item, price_key="price", currency_key="currency", value_key="rotate_speed"))
        parts.append(build_part_export(3, item, price_key="price", currency_key="currency", value_key="rotate_speed"))
    for item in tree["_turretArray"]:
        parts.append(build_part_export(4, item, price_key="price", currency_key="currency", value_key="view_range"))
    for item in tree["_gunArray"]:
        parts.append(build_part_export(5, item, price_key="price", currency_key="currency", value_key="caliber"))
    for item in tree["_engineArray"]:
        parts.append(build_part_export(6, item, price_key="price", currency_key="currency", value_key="power"))
    for item in tree["_radioArray"]:
        parts.append(build_part_export(7, item, price_key="price", currency_key="currency", value_key="distance"))
    for item in tree["_ammoBayArray"]:
        parts.append(build_part_export(8, item, price_key="id", currency_key="id", value_key="hp_max"))
        parts[-1]["Price"] = 0
        parts[-1]["Currency"] = 1
    for item in tree["_fuelTankArray"]:
        parts.append(build_part_export(9, item, price_key="price", currency_key="id", value_key="hp_max"))
        parts[-1]["Currency"] = 1
    for item in tree["_surveyingDeviceArray"]:
        parts.append(build_part_export(10, item, price_key="id", currency_key="id", value_key="armor"))
        parts[-1]["Price"] = 0
        parts[-1]["Currency"] = 1

    research = []
    for item in tree["_modulesResearchArray"]:
        node_id = as_int(item.get("node_id"))
        if node_id <= 0:
            continue
        research.append({
            "Id": as_int(item.get("id")),
            "TankId": as_int(item.get("vehicle")),
            "NodeId": node_id,
            "PartType": as_int(item.get("node_type")),
            "PartId": as_int(item.get("type_id")),
            "ExpCost": as_int(item.get("exp")),
            "RequiredLevel": as_int(item.get("level")),
            "ParentNodeIds": [
                as_int(item.get("parent_node_1")),
                as_int(item.get("parent_node_2")),
                as_int(item.get("parent_node_3")),
                as_int(item.get("parent_node_4")),
                as_int(item.get("parent_node_5")),
            ],
            "ChildNodeIds": [
                as_int(item.get("child_node_1")),
                as_int(item.get("child_node_2")),
                as_int(item.get("child_node_3")),
                as_int(item.get("child_node_4")),
                as_int(item.get("child_node_5")),
                as_int(item.get("child_node_6")),
                as_int(item.get("child_node_7")),
            ],
        })

    shells = [build_shell_export(item) for item in tree["_shellArray"] if as_int(item.get("id")) > 0]

    guns = []
    for item in tree["_gunArray"]:
        gun_id = as_int(item.get("id"))
        if gun_id <= 0:
            continue
        guns.append({
            "GunId": gun_id,
            "AmmoLoad": as_int(item.get("ammo_load"), 30),
            "Ammo1Id": as_int(item.get("ammo1_id")),
            "Ammo1Ratio": float(item.get("ammo1_ratio", 0.0) or 0.0),
            "Ammo2Id": as_int(item.get("ammo2_id")),
            "Ammo2Ratio": float(item.get("ammo2_ratio", 0.0) or 0.0),
            "Ammo3Id": as_int(item.get("ammo3_id")),
            "Ammo3Ratio": float(item.get("ammo3_ratio", 0.0) or 0.0),
            "UseTurrets": split_ids(item.get("use_turret")),
        })

    techtree = []
    for item in tree["_techtreeArray"]:
        node_id = as_int(item.get("id"))
        if node_id <= 0:
            continue
        techtree.append({
            "Id": node_id,
            "VehicleId": as_int(item.get("vehicleID")),
            "Level": as_int(item.get("level")),
            "ModuleStartId": as_int(item.get("module_start_id")),
            "ParentNodeIds": [
                as_int(item.get("parent_node_1")),
                as_int(item.get("parent_node_2")),
                as_int(item.get("parent_node_3")),
                as_int(item.get("parent_node_4")),
                as_int(item.get("parent_node_5")),
            ],
            "ChildNodeIds": [
                as_int(item.get("child_node_1")),
                as_int(item.get("child_node_2")),
                as_int(item.get("child_node_3")),
                as_int(item.get("child_node_4")),
                as_int(item.get("child_node_5")),
            ],
        })

    rewards = []
    for item in tree["_rewardArray"]:
        reward_id = as_int(item.get("id"))
        if reward_id <= 0:
            continue
        rewards.append({
            "Id": reward_id,
            "Reward1Type": as_int(item.get("reward1_type")),
            "Reward1Id": as_int(item.get("reward1_ID")),
            "Reward1Count": as_int(item.get("reward1_count")),
            "Reward2Type": as_int(item.get("reward2_type")),
            "Reward2Id": as_int(item.get("reward2_ID")),
            "Reward2Count": as_int(item.get("reward2_count")),
            "Reward3Type": as_int(item.get("reward3_type")),
            "Reward3Id": as_int(item.get("reward3_ID")),
            "Reward3Count": as_int(item.get("reward3_count")),
            "Reward4Type": as_int(item.get("reward4_type")),
            "Reward4Id": as_int(item.get("reward4_ID")),
            "Reward4Count": as_int(item.get("reward4_count")),
            "Reward5Type": as_int(item.get("reward5_type")),
            "Reward5Id": as_int(item.get("reward5_ID")),
            "Reward5Count": as_int(item.get("reward5_count")),
        })

    # v33: Table_New_bonusbox — таблица призов рулетки «Золотого запаса».
    # КРИТИЧНО: сервер шлёт НОМЕР СТРОКИ этой таблицы, а клиент сам
    # рисует и объявляет то, что в ней написано. Без этой выгрузки сервер
    # не знает, что именно показал клиент, и выдаёт другое.
    #
    # reward_itemtype (сверено по TreasureBox.cs / TreasureBoxManager.cs):
    #   1 = опыт, 2 = серебро, 3 = золото,
    #   4 = танк (reward_itemid = Table_Vehicle.id, reward_itemcount = дней; <0 = навсегда),
    #   5 = VIP (reward_itemcount: 3 -> 30 дней и т.д.), 6 = расходники.
    # reward_level == 1 у типа 4 — ГЛАВНЫЙ ПРИЗ (сектор 9 колеса, 324°),
    # остальные типы 4 — обычные премиум-танки (сектор 3).
    bonusboxes = []
    for item in tree.get("_newbonusboxArray", []) or []:
        bonusboxes.append({
            "Id": as_int(item.get("id")),
            "RewardLevel": as_int(item.get("reward_level")),
            "DropRate": as_int(item.get("drop_rate")),
            "MinValue": as_int(item.get("min_value")),
            "MaxValue": as_int(item.get("max_value")),
            "RewardItemType": as_int(item.get("reward_itemtype")),
            "RewardItemId": as_int(item.get("reward_itemid")),
            "RewardItemCount": as_int(item.get("reward_itemcount")),
        })

    # v30: Table_Cloth (камуфляжи). Поле в GameTableMgr называется _exteriorArray.
    # Названия ключей совпадают с ExteriorEntry на сервере (PropertyNameCaseInsensitive).
    exteriors = []
    for item in tree.get("_exteriorArray", []) or []:
        exteriors.append({
            "Id": as_int(item.get("id")),
            "Name": item.get("name", ""),
            "SuitMapType": as_int(item.get("suit_maptype")),
            "Price1Type": as_int(item.get("price1_type"), 1),
            "Price1Value": as_int(item.get("price1_value")),
            "Price2Type": as_int(item.get("price2_type"), 1),
            "Price2Value": as_int(item.get("price2_value")),
            "Price3Type": as_int(item.get("price3_type"), 1),
            "Price3Value": as_int(item.get("price3_value")),
            "Special": as_int(item.get("special")),
            "Buy": as_int(item.get("buy"), 1),
        })

    payload = {
        "Tanks": sorted(tanks, key=lambda item: item["Id"]),
        "Parts": sorted(parts, key=lambda item: (item["ModuleType"], item["Id"])),
        "Shells": sorted(shells, key=lambda item: item["Id"]),
        "Research": sorted(research, key=lambda item: item["Id"]),
        "Guns": sorted(guns, key=lambda item: item["GunId"]),
        "TechTree": sorted(techtree, key=lambda item: item["Id"]),
        "Rewards": sorted(rewards, key=lambda item: item["Id"]),
        "Exteriors": sorted(exteriors, key=lambda item: item["Id"]),
        "Bonusboxes": sorted(bonusboxes, key=lambda item: item["Id"]),
    }

    output_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Saved {output_path}")
    print(
        "Counts:",
        f"tanks={len(payload['Tanks'])}",
        f"parts={len(payload['Parts'])}",
        f"shells={len(payload['Shells'])}",
        f"research={len(payload['Research'])}",
        f"guns={len(payload['Guns'])}",
        f"techtree={len(payload['TechTree'])}",
        f"rewards={len(payload['Rewards'])}",
        f"exteriors={len(payload['Exteriors'])}",
        f"bonusboxes={len(payload['Bonusboxes'])}",
    )


if __name__ == "__main__":
    import traceback

    try:
        main()
        print("\nГотово. Таблицы выгружены успешно.")
    except SystemExit as exc:
        # Наши собственные понятные сообщения (папка не найдена и т.п.).
        print("\n" + str(exc))
    except ModuleNotFoundError as exc:
        if "UnityPy" in str(exc):
            print(
                "\nНе установлена библиотека UnityPy — без неё скрипт работать не может.\n"
                "Установите её командой (в этом же окне):\n\n"
                "    pip install UnityPy\n\n"
                "Если pip не найден, попробуйте:\n"
                "    py -m pip install UnityPy\n"
            )
        else:
            print(f"\nНе найден модуль: {exc}")
            traceback.print_exc()
    except Exception:
        print("\nСкрипт завершился с ошибкой. Полный текст ниже — пришлите его целиком:\n")
        traceback.print_exc()
    finally:
        # Главное: окно НЕ закрывается само — видно и успех, и ошибку.
        try:
            input("\nНажмите Enter, чтобы закрыть это окно...")
        except EOFError:
            pass
