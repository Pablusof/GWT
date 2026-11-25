using System.Collections.Generic;

namespace GwtEmulator.Server.GameTables;

public class TankEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Tier { get; set; }
    public int Type { get; set; }
    public int Nation { get; set; }
    public int VehicleType { get; set; }
    public int Price { get; set; }
    public int Currency { get; set; } = 1;
    public int CostSilver { get; set; }
    public int CostGold { get; set; }
    public int HullId { get; set; }
    public int UnderpanId { get; set; }
    public int TurretId { get; set; }
    public int MainGunId { get; set; }
    public int EngineId { get; set; }
    public int FueltankId { get; set; }
    public int CartridgeId { get; set; }
    public int RadioStationId { get; set; }
    public int GaugeId { get; set; }
    public int MaxHp { get; set; }
    public int Speed { get; set; }
    public int ViewRange { get; set; }
}

public class PartEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ModuleType { get; set; }
    public int Price { get; set; }
    public int Currency { get; set; } = 1;
    public int Type { get; set; }
    public int Tier { get; set; }
    public int CostSilver { get; set; }
    public int CostGold { get; set; }
    public float Value { get; set; }
}

public class ShellEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Price { get; set; }
    public int Currency { get; set; } = 1;
    public int Type { get; set; }
    public int CostSilver { get; set; }
    public int CostGold { get; set; }
    public int Damage { get; set; }
    public int Penetration { get; set; }
}

public class GunEntry
{
    public int GunId { get; set; }
    public int AmmoLoad { get; set; }
    public int Ammo1Id { get; set; }
    public float Ammo1Ratio { get; set; }
    public int Ammo2Id { get; set; }
    public float Ammo2Ratio { get; set; }
    public int Ammo3Id { get; set; }
    public float Ammo3Ratio { get; set; }
    public List<int> UseTurrets { get; set; } = new();
}

public class ItemEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte Type { get; set; }
    public int CostSilver { get; set; }
    public int CostGold { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class CrewEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int CostSilver { get; set; }
    public int CostGold { get; set; }
    public int Type { get; set; }
    public int[] SkillIds { get; set; } = new int[0];
    // Starting crew level when hired (Recruit=0, Specialist=5, Elite=10, Ace=12)
    public int InitLv { get; set; } = 1;
}

// Mirrors the client's Table_Equipment row (оснащение). Each item defines up to 5 stat
// effects: AttributeN (EqAttributeType index, same space the server's attr array uses),
// ValueN, ChangeTypeN (0 = flat +value, 1 = +base*value — matches the client's OperateType).
// AttributeN == 0 or >= 1000 means "no stat change" (a scripted/special mechanic instead).
// Populate the "Equipments" array in client_game_tables.json from Table_Equipment.
public class EquipmentEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Price { get; set; }
    public int PriceCurrency { get; set; } = 1; // 1 = silver, 2 = gold
    public int Tags { get; set; }               // 1=General, 2=Defense/PVE, 3=Commander, 4=Tactics
    public bool DefenseOnly { get; set; }       // effect applies only in defense mode
    public int Removable { get; set; }          // 1 = free to demount, 0 = costs RemovablePrice
    public int RemovablePrice { get; set; }
    public int RemovablePriceCurrency { get; set; } = 2;
    public int Script { get; set; }             // special-mechanic id (0 = pure stat item)
    public int Weight { get; set; }
    public int Level { get; set; } = 1;
    public int InShop { get; set; } = 1;
    public int Buy { get; set; } = 1;
    public int MemberExclusive { get; set; }
    public int ParticularType { get; set; }
    public int ParticularParameter { get; set; }
    public string AntiId { get; set; } = string.Empty;
    public int Attribute1 { get; set; } public float Value1 { get; set; } public int ChangeType1 { get; set; }
    public int Attribute2 { get; set; } public float Value2 { get; set; } public int ChangeType2 { get; set; }
    public int Attribute3 { get; set; } public float Value3 { get; set; } public int ChangeType3 { get; set; }
    public int Attribute4 { get; set; } public float Value4 { get; set; } public int ChangeType4 { get; set; }
    public int Attribute5 { get; set; } public float Value5 { get; set; } public int ChangeType5 { get; set; }
}

public class ConsumableEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Price { get; set; }
    public int Currency { get; set; } = 1;      // 1 = silver, 2 = gold
    public int SilverToGold { get; set; }       // silver alternative price when bought with silver
    public int Type { get; set; }               // 1=Repair, 2=Extinguish, 3=SuperRepair, 4=Smoke, 5=ReusableRepair, 6=Artillery, 7=FullRepair, 8=Recon
    public int CdTime { get; set; }             // seconds; 0 = single-use per battle, >0 = reusable cooldown
    public int InShop { get; set; } = 1;
    public int Buy { get; set; } = 1;
    public int ActionId { get; set; }           // client ProvisionAction enum
    public int CarryCount { get; set; } = 1;
    public string AddEffect { get; set; } = string.Empty;       // e.g. "1", "2", "1_2"
    public string AddEffectCount { get; set; } = string.Empty;  // e.g. "20", "40_20"
    public string AddReduce { get; set; } = string.Empty;
    public int AdditionalId { get; set; }       // Table_Smoke_bomb.ID for smoke bombs
}

public class SmokeBombEntry
{
    public int Id { get; set; }
    public int Type { get; set; }
    public int SmokeCount { get; set; }
    public int Angle { get; set; }
    public int SmokeRadius { get; set; }
    public int Distance { get; set; }
    public int ContinuedTime { get; set; }
}

// Mirrors the client's Table_Tank_crew row (the hire-dialog crew templates). The client
// sends only the row ID on NM_CH_BuyCrew, so the server needs this table to know the
// starting level + price for that id. Populate the "TankCrews" array in
// client_game_tables.json from Table_Tank_crew (columns: ID, type, nation, level,
// max_level, cost_type, cost_number).
public class TankCrewEntry
{
    public int Id { get; set; }
    public int Type { get; set; }
    public int Nation { get; set; }
    public int Level { get; set; }
    public int MaxLevel { get; set; }
    public int CostType { get; set; }   // 1 = silver, 2 = gold (see LoadCrews mapping)
    public int CostNumber { get; set; }
}

public class MapEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int BattleMode { get; set; }
    public int MaxPlayers { get; set; } = 8;
}

public class SkillEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int MaxLevel { get; set; } = 10;
    public float[] Effects { get; set; } = new float[0];
}

public class ResearchEntry
{
    public int Id { get; set; }
    public int TankId { get; set; }
    public int NodeId { get; set; }
    public int PartType { get; set; }
    public int PartId { get; set; }
    public int ExpCost { get; set; }
    public int RequiredLevel { get; set; }
    public int[] ParentNodeIds { get; set; } = new int[0];
    public int[] ChildNodeIds { get; set; } = new int[0];
}

public class TechTreeEntry
{
    public int Id { get; set; }
    public int VehicleId { get; set; }
    public int Level { get; set; }
    public int ModuleStartId { get; set; }
    public int[] ParentNodeIds { get; set; } = new int[0];
    public int[] ChildNodeIds { get; set; } = new int[0];
}

public class RewardEntry
{
    public int Id { get; set; }
    public int Reward1Type { get; set; }
    public int Reward1Id { get; set; }
    public int Reward1Count { get; set; }
    public int Reward2Type { get; set; }
    public int Reward2Id { get; set; }
    public int Reward2Count { get; set; }
    public int Reward3Type { get; set; }
    public int Reward3Id { get; set; }
    public int Reward3Count { get; set; }
    public int Reward4Type { get; set; }
    public int Reward4Id { get; set; }
    public int Reward4Count { get; set; }
    public int Reward5Type { get; set; }
    public int Reward5Id { get; set; }
    public int Reward5Count { get; set; }

    public int GetRewardType(int slot) => slot switch
    {
        1 => Reward1Type, 2 => Reward2Type, 3 => Reward3Type,
        4 => Reward4Type, 5 => Reward5Type, _ => 0
    };
    public int GetRewardId(int slot) => slot switch
    {
        1 => Reward1Id, 2 => Reward2Id, 3 => Reward3Id,
        4 => Reward4Id, 5 => Reward5Id, _ => 0
    };
    public int GetRewardCount(int slot) => slot switch
    {
        1 => Reward1Count, 2 => Reward2Count, 3 => Reward3Count,
        4 => Reward4Count, 5 => Reward5Count, _ => 0
    };
}

public class ClientGameTablesPayload
{
    public List<TankEntry> Tanks { get; set; } = new();
    public List<PartEntry> Parts { get; set; } = new();
    public List<ShellEntry> Shells { get; set; } = new();
    public List<ResearchEntry> Research { get; set; } = new();
    public List<GunEntry> Guns { get; set; } = new();
    public List<TechTreeEntry> TechTree { get; set; } = new();
    public List<RewardEntry> Rewards { get; set; } = new();
    // Optional: hire-dialog crew templates (Table_Tank_crew). When present, the server
    // maps a bought crew id to its real starting level + price. When empty, HandleBuyCrew
    // falls back to a safe default and logs the unknown id.
    public List<TankCrewEntry> TankCrews { get; set; } = new();
    // Optional: оснащение effect rows (Table_Equipment). When present, the server applies
    // each item's real per-id effect instead of the hardcoded fallback table.
    public List<EquipmentEntry> Equipments { get; set; } = new();
    public List<ConsumableEntry> Consumables { get; set; } = new();
    public List<SmokeBombEntry> SmokeBombs { get; set; } = new();

    /// <summary>
    /// Необязательно: строки Table_Cloth (камуфляжи). Если список пуст — сервер
    /// работает по фолбэку из ExteriorEntry.Fallback и пишет предупреждение в лог.
    /// Выгружается из level0 скриптом tools/extract_client_tables.py.
    /// </summary>
    public List<ExteriorEntry> Exteriors { get; set; } = new();

    /// <summary>
    /// Необязательно: строки Table_New_bonusbox — призы рулетки «Золотого запаса».
    /// Пока список пуст, рулетка работает на аварийной раскладке и в лог идёт
    /// предупреждение: сервер физически не может знать, что показал клиент.
    /// </summary>
    public List<BonusboxEntry> Bonusboxes { get; set; } = new();
}

/// <summary>
/// v47: строка Table_Tank_transform — ОДНА «модификация» техники. Модификация — это
/// именованный набор альтернативных модулей (корпус/ходовая/башня/орудие/двигатель/рация)
/// для конкретного танка (TankId) плюс цена и срок. Покупка (PK_CH_TransformTank) ставит
/// танку ModificationId и подменяет модули. Данные — из level0 (_tankTransformArray),
/// файл GameTables/Data/tank_transforms.json.
/// </summary>
public class TankTransformEntry
{
    public int Id { get; set; }          // = ModificationId (PlanID)
    public int TankId { get; set; }      // для какого танка (Table_Vehicle.id)
    public string Name { get; set; } = "";
    public int Hull { get; set; }
    public int Chassis { get; set; }     // = Underpan (ходовая)
    public int Turret { get; set; }
    public int Gun { get; set; }
    public int Engine { get; set; }
    public int Radio { get; set; }
    public int UseHull { get; set; }     // клиентский флаг: менять ли визуальный корпус
    public int UseTurret { get; set; }
    public int PriceType1 { get; set; }  // 1=серебро, 2=золото (валюта варианта 1)
    public int Price1 { get; set; }
    public int Time1 { get; set; }       // срок в днях (0 = навсегда)
    public int PriceType2 { get; set; }
    public int Price2 { get; set; }
    public int Time2 { get; set; }
}

/// <summary>
/// Строка Table_New_bonusbox — ОДИН приз рулетки «Золотого запаса».
///
/// Главное про этот механизм: сервер шлёт в PK_HC_LotteryDraw ТОЛЬКО номер строки,
/// а клиент по нему сам берёт из своей таблицы иконку, текст «Получена машина …»
/// и сектор, на котором остановится стрелка. То есть таблица — источник истины,
/// и сервер обязан выдать ровно то, что в ней записано, иначе получается расхождение
/// «клиент объявил танк, сервер начислил серебро».
/// </summary>
public class BonusboxEntry
{
    public int Id { get; set; }

    /// <summary>У типа 4: 1 — это ГЛАВНЫЙ приз (сектор 9), иначе обычный премиум-танк.</summary>
    public int RewardLevel { get; set; }

    /// <summary>Вес из клиентской таблицы. Сервером используется как базовая вероятность.</summary>
    public int DropRate { get; set; }

    public int MinValue { get; set; }
    public int MaxValue { get; set; }

    /// <summary>1 опыт, 2 серебро, 3 золото, 4 танк, 5 VIP, 6 расходники.</summary>
    public int RewardItemType { get; set; }

    /// <summary>Для типа 4 — Table_Vehicle.id, для типа 6 — id расходника.</summary>
    public int RewardItemId { get; set; }

    /// <summary>Для типа 4 — СРОК В ДНЯХ (отрицательное = навсегда), иначе количество.</summary>
    public int RewardItemCount { get; set; }

    public bool IsTank      => RewardItemType == 4;
    public bool IsMainPrize => RewardItemType == 4 && RewardLevel == 1;
    public bool IsPremiumTank => RewardItemType == 4 && RewardLevel != 1;

    /// <summary>Танк выдаётся навсегда (в таблице отрицательный срок).</summary>
    public bool IsPermanentTank => IsTank && RewardItemCount < 0;

    /// <summary>Сколько дней действует временный танк; 0 = бессрочно.</summary>
    public int TankDays => IsTank && RewardItemCount > 0 ? RewardItemCount : 0;

    /// <summary>
    /// VIP в днях. Клиент (TreasureBox.ShowRewardName) трактует reward_itemcount
    /// как код срока: 3 → 30 дней, 5 → 5 дней и т.д. — берём его напрямую как код,
    /// а известный случай 3 разворачиваем в 30.
    /// </summary>
    public int VipDays => RewardItemType == 5
        ? (RewardItemCount == 3 ? 30 : RewardItemCount)
        : 0;
}

/// <summary>
/// Камуфляж (клиентский Table_Cloth). Поля названы как в таблице, чтобы выгрузка
/// из level0 ложилась один в один.
///
/// suit_maptype — тип местности И ОДНОВРЕМЕННО номер слота у клиента:
///   1 = снежный → слот 0 (Ice), 2 = полевой → слот 1 (Field), 3 = пустынный → слот 2 (Desert).
/// priceN_type: 1 = серебро, 2 = золото. Итоговая цена = уровень_танка × priceN_value
/// (см. ShopTools.GetCoatPrice в клиенте — скидок на сервере не применяем).
/// Индексы цен: 1 = 7 дней, 2 = 30 дней, 3 = навсегда.
/// </summary>
public class ExteriorEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SuitMapType { get; set; }
    public int Price1Type { get; set; }
    public int Price1Value { get; set; }
    public int Price2Type { get; set; }
    public int Price2Value { get; set; }
    public int Price3Type { get; set; }
    public int Price3Value { get; set; }
    /// <summary>1 — камуфляж только для премиум-аккаунта (Table_Cloth.special).</summary>
    public int Special { get; set; }
    /// <summary>0 — в магазине не продаётся (Table_Cloth.buy).</summary>
    public int Buy { get; set; } = 1;

    /// <summary>
    /// Заглушка для неизвестного id: слот берём из того, что прислал клиент,
    /// цену — усреднённую по оригинальной таблице (серебро, ×уровень танка).
    /// Даёт рабочую покупку даже без выгруженного Table_Cloth.
    /// </summary>
    public static ExteriorEntry Fallback(int id) => new()
    {
        Id = id, Name = $"exterior_{id}", SuitMapType = 0,
        Price1Type = 1, Price1Value = 1_500,    // 7 дней
        Price2Type = 1, Price2Value = 4_000,    // 30 дней
        Price3Type = 1, Price3Value = 12_000,   // навсегда
        Special = 0, Buy = 1,
    };

    /// <summary>Цена для BUY_TYPE: 0 = 7 дней, 1 = 30 дней, 2 = навсегда.</summary>
    public (int cost, int currency) GetPrice(int buyType, int tankLevel)
    {
        if (tankLevel < 1) tankLevel = 1;
        return buyType switch
        {
            0 => (tankLevel * Price1Value, Price1Type <= 0 ? 1 : Price1Type),
            1 => (tankLevel * Price2Value, Price2Type <= 0 ? 1 : Price2Type),
            2 => (tankLevel * Price3Value, Price3Type <= 0 ? 1 : Price3Type),
            _ => (0, 1),
        };
    }

    /// <summary>Сколько секунд действует покупка; 0 = бессрочно.</summary>
    public static int DurationSeconds(int buyType) => buyType switch
    {
        0 => 7  * 24 * 3600,
        1 => 30 * 24 * 3600,
        _ => 0,
    };
}
