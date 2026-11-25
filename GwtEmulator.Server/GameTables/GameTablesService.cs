using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GwtEmulator.Protocol;

namespace GwtEmulator.Server.GameTables;

public class GameTablesService
{
    private readonly Dictionary<int, TankEntry> _tanks = new();
    private readonly Dictionary<(int ModuleType, int Id), PartEntry> _parts = new();
    private readonly Dictionary<int, ShellEntry> _shells = new();
    private readonly Dictionary<int, GunEntry> _guns = new();
    private readonly Dictionary<int, ItemEntry> _items = new();
    private readonly Dictionary<int, CrewEntry> _crews = new();
    private readonly Dictionary<int, EquipmentEntry> _equipments = new();
    private readonly Dictionary<int, ConsumableEntry> _consumables = new();
    private readonly Dictionary<int, SmokeBombEntry> _smokeBombs = new();
    private readonly Dictionary<int, ExteriorEntry> _exteriors = new();   // v30: Table_Cloth (камуфляжи)
    private readonly Dictionary<int, BonusboxEntry> _bonusboxes = new();  // v33: Table_New_bonusbox (рулетка)
    // v48.11: реальные сведение (сек), грузоподъёмность и массы модулей (кг) из level0 (part_stats.json).
    private readonly Dictionary<int, float> _gunAim = new();       // gunId  -> aim_max (сек)
    // v61 (пункт 6): пер-танковый коэффициент фарма (серебро/опыт за бой). Грузится из
    // GameTables/Data/vehicle_economy.json { "vehicleId": {"silver":x,"exp":y} }; по умолчанию
    // 1.0. В оригинале премы/акционные фармят больше — здесь задаётся явным списком.
    private readonly Dictionary<int, (float Silver, float Exp)> _vehicleEconomy = new();
    // v63 (п.1): категорийный коэффициент фарма.
    //   обычная техника (серебро, Currency=1) = 100%
    //   легион (список ID ниже, из vehicle_economy.json → "legion_ids") = 150%
    //   премы (золотая валюта, Currency=2) = 200%
    // Множители категорий и список легиона грузятся из vehicle_economy.json (LoadVehicleEconomy),
    // чтобы менять без пересборки. Числовые оверрайды в том же файле имеют высший приоритет.
    private readonly HashSet<int> _legionVehicleIds = new();
    private (float Silver, float Exp) _multRegular = (1.0f, 1.0f);
    private (float Silver, float Exp) _multLegion  = (1.5f, 1.5f);
    private (float Silver, float Exp) _multPremium = (2.0f, 2.0f);
    public (float Silver, float Exp) GetVehicleEconomyMult(int vehicleId)
    {
        if (_vehicleEconomy.TryGetValue(vehicleId, out var m)) return m;          // 1) ручной оверрайд
        if (_legionVehicleIds.Contains(vehicleId))            return _multLegion; // 2) легион 150%
        if (_tanks.TryGetValue(vehicleId, out var t) && t.Currency == 2)
                                                             return _multPremium;// 3) премы (золото) 200%
        return _multRegular;                                                     // 4) обычные 100%
    }
    private readonly Dictionary<int, int>   _chassisLoad = new();  // chassisId -> load (кг)
    private readonly Dictionary<int, int>   _gunWeight = new();
    private readonly Dictionary<int, int>   _chassisWeight = new();
    private readonly Dictionary<int, int>   _turretWeight = new();
    private readonly Dictionary<int, int>   _hullWeight = new();
    // v49: точность и сведение орудий (Table_Gun) + стандарты машин (Table_Vehicle) из level0
    // (gun_precision.json). Нужны, чтобы задавать РЕАЛЬНУЮ точность (attr13) как игроку, так и
    // ботам, и чтобы боты «сводились» за реальное время орудия, а не за хардкод.
    private readonly struct GunPrec
    {
        public readonly float AMin, AMax, AReal, AiMin, AiMax;
        public GunPrec(float amin, float amax, float areal, float aimin, float aimax)
        { AMin = amin; AMax = amax; AReal = areal; AiMin = aimin; AiMax = aimax; }
    }
    private readonly Dictionary<int, GunPrec> _gunPrec = new();                     // gunId -> точность/сведение
    private readonly Dictionary<int, (float astd, float aimstd)> _vehStd = new();   // vehicleId -> стандарты

    // v54 (задача 1): разрушаемые объекты сцены (can_shot) по картам. mapId -> список объектов
    // с их sid (objectID из клиентского обхода RegionDatas→ModelGroups→Transes) и координатами.
    // Извлечено из папки_17 (MapData). Нужно, чтобы БОТ (у него нет клиента) сам засекал попадание
    // снаряда по объекту и рассылал снос; для игрока снос уже работает через релей PK_CR_SceneInfo.
    public readonly record struct SceneObj(ushort Id, float X, float Y, float Z, bool IsFall);
    private readonly Dictionary<int, List<SceneObj>> _destructibles = new();        // mapId -> объекты
    public IReadOnlyList<SceneObj> GetDestructibles(int mapId)
        => _destructibles.TryGetValue(mapId, out var l) ? l : (IReadOnlyList<SceneObj>)System.Array.Empty<SceneObj>();
    private readonly Dictionary<int, TankTransformEntry> _transforms = new();            // v47: Table_Tank_transform (модификации)
    private readonly Dictionary<int, List<TankTransformEntry>> _transformsByTank = new(); // v47: tank_id -> модификации
    private readonly Dictionary<int, MapEntry> _maps = new();
    private readonly Dictionary<int, SkillEntry> _skills = new();
    private readonly List<ResearchEntry> _research = new();
    private readonly Dictionary<int, List<ResearchEntry>> _researchByTank = new();
    private readonly Dictionary<(int TankId, int PartType, int PartId), ResearchEntry> _researchByPart = new();
    private readonly Dictionary<(int TankId, int NodeId), ResearchEntry> _researchByNode = new();
    private readonly Dictionary<int, RewardEntry> _rewards = new();

    public IReadOnlyDictionary<int, TankEntry> Tanks => _tanks;
    public IReadOnlyCollection<PartEntry> Parts => _parts.Values;
    public IReadOnlyDictionary<int, ShellEntry> Shells => _shells;
    public IReadOnlyDictionary<int, GunEntry> Guns => _guns;
    public IReadOnlyDictionary<int, ItemEntry> Items => _items;
    public IReadOnlyDictionary<int, CrewEntry> Crews => _crews;
    public IReadOnlyDictionary<int, MapEntry> Maps => _maps;
    public IReadOnlyDictionary<int, SkillEntry> Skills => _skills;
    public IReadOnlyDictionary<int, EquipmentEntry> Equipments => _equipments;
    public IReadOnlyDictionary<int, ConsumableEntry> Consumables => _consumables;
    public IReadOnlyDictionary<int, SmokeBombEntry> SmokeBombs => _smokeBombs;
    public IReadOnlyList<ResearchEntry> Research => _research;

    // v65 (п.3): число МОДУЛЬНЫХ узлов исследования у танка (без узла покупки машины PartType=100).
    // Используется, чтобы пометить танк «элитным» (nState=1) только когда изучены ВСЕ модули,
    // а не всегда — иначе клиент показывает попап «все исследования завершены» на неизученной технике.
    public int GetTankModuleNodeCount(int vehicleId)
        => _researchByTank.TryGetValue(vehicleId, out var l) ? l.Count(e => e.PartType != 100) : 0;

    // See the detailed comment inside BuildTankAttributes: tankEntry.MaxHp is hull-only
    // (confirmed against the Parts table), and we don't have real turret HP data anywhere.
    // This is the single shared place that approximates the real (hull+turret) total, used
    // for both the actual battle HP pool (RoomServer) and the display attribute
    // (BuildTankAttributes) so they stay consistent with each other.
    // Exact HP (hull + turret) for ALL tanks extracted from level0 binary tables.
    // Generated automatically — do not edit manually.
    private static readonly Dictionary<string, float> ExactHangarHp = new(StringComparer.OrdinalIgnoreCase)
    {
        { "t_vehicle_name_1K-17", 4500f },
        { "t_vehicle_name_A-20", 550f },
        { "t_vehicle_name_A-34", 530f },
        { "t_vehicle_name_A30", 1696f },
        { "t_vehicle_name_A33_Excelsior", 860f },
        { "t_vehicle_name_A39_PT", 2380f },
        { "t_vehicle_name_AMX30", 3350f },
        { "t_vehicle_name_AMX50_100", 2440f },
        { "t_vehicle_name_ASU_57", 960f },
        { "t_vehicle_name_ASU_85", 1400f },
        { "t_vehicle_name_AT-1", 220f },
        { "t_vehicle_name_AT_15A", 1820f },
        { "t_vehicle_name_BT-2", 240f },
        { "t_vehicle_name_BT-7", 340f },
        { "t_vehicle_name_Black_Prince", 2150f },
        { "t_vehicle_name_Borsig_WT", 2700f },
        { "t_vehicle_name_Brummbar", 1000f },
        { "t_vehicle_name_Chieftain_Mk_V", 2880f },
        { "t_vehicle_name_Churchill_I", 1180f },
        { "t_vehicle_name_Churchill_VII", 1520f },
        { "t_vehicle_name_Covenanter", 515f },
        { "t_vehicle_name_Cromwell", 1220f },
        { "t_vehicle_name_Cruiser_Mk_II", 300f },
        { "t_vehicle_name_Cruiser_Mk_III", 370f },
        { "t_vehicle_name_Crusader", 640f },
        { "t_vehicle_name_E-10", 795f },
        { "t_vehicle_name_E-100", 4500f },
        // v12: сверка с level0 — корпус 2100 + башня 680 = 2780 (бот E-50 в оригинале ровно 2780).
        { "t_vehicle_name_E-50", 2780f },
        { "t_vehicle_name_E-75", 3160f },
        { "t_vehicle_name_E-90", 4550f },
        { "t_vehicle_name_FV214_Conqueror", 3700f },
        { "t_vehicle_name_FV215B", 3430f },
        { "t_vehicle_name_FV221", 2380f },
        { "t_vehicle_name_FV221_Caernarvon_Mk_II", 1960f },
        { "t_vehicle_name_FV4005", 4650f },
        { "t_vehicle_name_FV4030/4", 4200f },
        { "t_vehicle_name_FV4030/4_F", 4200f },
        { "t_vehicle_name_Ferdinand", 2060f },
        { "t_vehicle_name_Flakpanzer_E-100", 5150f },
        { "t_vehicle_name_Hetzer", 450f },
        { "t_vehicle_name_IS", 2080f },
        { "t_vehicle_name_IS-3", 2430f },
        { "t_vehicle_name_IS-4", 2900f },
        { "t_vehicle_name_IS-6", 2700f },
        // v12: сверка с level0 — было 2480 (терялось ХП башни). Корпус 3040 + башня 760
        // (turret id 102, обзор 260 совпадает с ViewRange танка) = 3800.
        { "t_vehicle_name_IS-7", 3800f },
        { "t_vehicle_name_IS-8", 2900f },
        { "t_vehicle_name_ISU-152", 1700f },
        { "t_vehicle_name_JPZ4-5", 1580f },
        { "t_vehicle_name_JagdPanther", 1420f },
        { "t_vehicle_name_JagdPzIV", 980f },
        { "t_vehicle_name_JagdTiger", 2950f },
        { "t_vehicle_name_JgPz_E-100", 4320f },
        { "t_vehicle_name_KPz_70", 4100f },
        { "t_vehicle_name_KV-1", 1070f },
        { "t_vehicle_name_KV-13", 1600f },
        { "t_vehicle_name_KV-1S", 1480f },
        { "t_vehicle_name_KV-2", 1500f },
        { "t_vehicle_name_KV-3", 2100f },
        { "t_vehicle_name_KV-4", 2450f },
        { "t_vehicle_name_KV-5", 2600f },
        { "t_vehicle_name_KV-9", 1080f },
        { "t_vehicle_name_KV_4_122", 2750f },
        { "t_vehicle_name_Leopard1", 3380f },
        { "t_vehicle_name_Leopard_PT_B", 2195f },
        { "t_vehicle_name_Lowe", 2800f },
        { "t_vehicle_name_M1", 195f },
        { "t_vehicle_name_M10", 590f },
        { "t_vehicle_name_M103", 2910f },
        { "t_vehicle_name_M2", 410f },
        { "t_vehicle_name_M24_Chaffee", 400f },
        { "t_vehicle_name_M26", 2010f },
        { "t_vehicle_name_M36", 1080f },
        { "t_vehicle_name_M3lee", 550f },
        { "t_vehicle_name_M4", 1000f },
        { "t_vehicle_name_M41", 1325f },
        { "t_vehicle_name_M46", 2600f },
        { "t_vehicle_name_M48", 3910f },
        { "t_vehicle_name_M4A2_L", 1000f },
        { "t_vehicle_name_M4A3_76", 1180f },
        { "t_vehicle_name_M4_FL10", 1110f },
        { "t_vehicle_name_M551", 1970f },
        { "t_vehicle_name_M5A1", 654f },
        { "t_vehicle_name_M6", 1400f },
        { "t_vehicle_name_M60", 4700f },
        { "t_vehicle_name_M60A2_Starship", 4400f },
        { "t_vehicle_name_M60_F", 4700f },
        { "t_vehicle_name_M6A2E1", 2080f },
        { "t_vehicle_name_M7", 650f },
        { "t_vehicle_name_MS-1", 195f },
        { "t_vehicle_name_Mammut", 2800f },
        { "t_vehicle_name_Marder_II", 280f },
        { "t_vehicle_name_Mars_15", 3000f },
        { "t_vehicle_name_Matilda", 620f },
        { "t_vehicle_name_Maus", 5000f },
        { "t_vehicle_name_Nashorn", 1420f },
        { "t_vehicle_name_Obj277", 3900f },
        { "t_vehicle_name_Obj279", 3800f },
        { "t_vehicle_name_Obj280", 3800f },
        { "t_vehicle_name_Object_140", 4000f },
        { "t_vehicle_name_Object_140_F", 4000f },
        { "t_vehicle_name_Object_263", 3800f },
        { "t_vehicle_name_Object_268", 3950f },
        { "t_vehicle_name_Object_701", 2800f },
        { "t_vehicle_name_Object_704", 2665f },
        { "t_vehicle_name_Object_777", 4600f },
        { "t_vehicle_name_P40_737(i)", 640f },
        { "t_vehicle_name_Panther", 2100f },
        { "t_vehicle_name_Panther_II", 2380f },
        { "t_vehicle_name_PanzerJager_I", 215f },
        { "t_vehicle_name_Pz35t", 285f },
        { "t_vehicle_name_Pz38nA", 560f },
        { "t_vehicle_name_Pz38t", 420f },
        { "t_vehicle_name_PzI", 240f },
        { "t_vehicle_name_PzII", 285f },
        { "t_vehicle_name_PzIII", 594f },
        { "t_vehicle_name_PzIII_A", 415f },
        { "t_vehicle_name_PzIV", 809f },
        { "t_vehicle_name_RAM_MK_II", 800f },
        { "t_vehicle_name_SU-100", 980f },
        { "t_vehicle_name_SU-100P", 1800f },
        { "t_vehicle_name_SU-101", 1580f },
        { "t_vehicle_name_SU-122-54", 2580f },
        { "t_vehicle_name_SU-152", 1440f },
        { "t_vehicle_name_SU-152P", 2300f },
        { "t_vehicle_name_SU-152_Taran", 3200f },
        { "t_vehicle_name_SU-76", 265f },
        { "t_vehicle_name_SU-76M", 432f },
        { "t_vehicle_name_SU-85", 590f },
        { "t_vehicle_name_StuGIII", 580f },
        { "t_vehicle_name_Sturer_Emil", 1750f },
        { "t_vehicle_name_Sturmtiger", 4300f },
        { "t_vehicle_name_Sturmtiger_Pt", 3500f },
        { "t_vehicle_name_Super_Hellcat", 1040f },
        { "t_vehicle_name_T-25", 950f },
        { "t_vehicle_name_T-26", 300f },
        { "t_vehicle_name_T-28", 570f },
        { "t_vehicle_name_T-30", 305f },
        { "t_vehicle_name_T-34", 750f },
        { "t_vehicle_name_T-34-100", 1550f },
        { "t_vehicle_name_T-34-57", 790f },
        { "t_vehicle_name_T-34-85", 1210f },
        { "t_vehicle_name_T-34_M183", 1250f },
        { "t_vehicle_name_T-43", 1630f },
        { "t_vehicle_name_T-44", 2100f },
        { "t_vehicle_name_T-46", 410f },
        { "t_vehicle_name_T-46-5", 650f },
        { "t_vehicle_name_T-50", 595f },
        { "t_vehicle_name_T-54", 2700f },
        { "t_vehicle_name_T-55", 3280f },
        { "t_vehicle_name_T-62A", 4300f },
        { "t_vehicle_name_T110E5", 4550f },
        { "t_vehicle_name_T18", 220f },
        { "t_vehicle_name_T20", 1500f },
        { "t_vehicle_name_T26E4", 2880f },
        { "t_vehicle_name_T26E5", 1445f },
        { "t_vehicle_name_T28", 2670f },
        { "t_vehicle_name_T28_PT", 1820f },
        { "t_vehicle_name_T3", 320f },
        { "t_vehicle_name_T32", 2350f },
        { "t_vehicle_name_T34", 2690f },
        { "t_vehicle_name_T40", 435f },
        { "t_vehicle_name_T43", 2600f },
        { "t_vehicle_name_T54", 3070f },
        { "t_vehicle_name_T58", 3800f },
        { "t_vehicle_name_T69", 2700f },
        { "t_vehicle_name_T82", 270f },
        { "t_vehicle_name_T95/Chieftain", 4220f },
        { "t_vehicle_name_T95/Chieftain_F", 4220f },
        { "t_vehicle_name_T96", 3960f },
        { "t_vehicle_name_Tiger", 2420f },
        { "t_vehicle_name_Tiger_II", 2700f },
        { "t_vehicle_name_VK3001P", 1380f },
        { "t_vehicle_name_VK3002DB", 1380f },
        { "t_vehicle_name_VK3601H", 1390f },
        { "t_vehicle_name_VK4502H", 2450f },
        { "t_vehicle_name_VK4502PB", 2600f },
        { "t_vehicle_name_VK7201", 3220f },
        { "t_vehicle_name_Vickers_Mk_I", 210f },
        { "t_vehicle_name_Vickers_Mk_II", 300f },
        { "t_vehicle_name_Vickers_Mk_III", 435f },
        { "t_vehicle_name_WT_Auf_PzIV", 3700f },
        { "t_vehicle_name_XM815", 4300f },
        { "t_vehicle_name_XM815_Chrysler", 3950f },
        { "t_vehicle_name_chatillon_25t", 2880f },
        { "t_vehicle_name_chatillon_25t_F", 2880f },
        { "t_vehicle_name_tiger_p", 2350f },
    };

    // Exact reload_min time (seconds) for ALL tanks.
    private static readonly Dictionary<string, float> ReloadOverrideSeconds = new(StringComparer.OrdinalIgnoreCase)
    {
        { "t_vehicle_name_1K-17", 22.0f },
        { "t_vehicle_name_A-20", 2.653f },
        { "t_vehicle_name_A-34", 4.921f },
        { "t_vehicle_name_A30", 5.1f },
        { "t_vehicle_name_A33_Excelsior", 10.0f },
        { "t_vehicle_name_A39_PT", 6.67f },
        { "t_vehicle_name_AMX30", 12.9f },
        { "t_vehicle_name_AMX50_100", 40.0f },
        { "t_vehicle_name_ASU_57", 4.2f },
        { "t_vehicle_name_ASU_85", 6.0f },
        { "t_vehicle_name_AT-1", 2.653f },
        { "t_vehicle_name_AT_15A", 6.315f },
        { "t_vehicle_name_BT-2", 2.993f },
        { "t_vehicle_name_BT-7", 2.893f },
        { "t_vehicle_name_Black_Prince", 4.286f },
        { "t_vehicle_name_Borsig_WT", 14.509f },
        { "t_vehicle_name_Brummbar", 28.0f },
        { "t_vehicle_name_Chieftain_Mk_V", 7.4f },
        { "t_vehicle_name_Churchill_I", 4.0f },
        { "t_vehicle_name_Churchill_VII", 2.4f },
        { "t_vehicle_name_Covenanter", 3.2f },
        { "t_vehicle_name_Cromwell", 11.6f },
        { "t_vehicle_name_Cruiser_Mk_II", 3.0f },
        { "t_vehicle_name_Cruiser_Mk_III", 3.0f },
        { "t_vehicle_name_Crusader", 2.5f },
        { "t_vehicle_name_E-10", 4.656f },
        { "t_vehicle_name_E-100", 14.0f },
        { "t_vehicle_name_E-50", 5.986f },
        { "t_vehicle_name_E-75", 9.044f },
        { "t_vehicle_name_E-90", 13.3f },
        { "t_vehicle_name_FV214_Conqueror", 18.2f },
        { "t_vehicle_name_FV215B", 19.2f },
        { "t_vehicle_name_FV221", 4.5f },
        { "t_vehicle_name_FV221_Caernarvon_Mk_II", 5.0f },
        { "t_vehicle_name_FV4005", 28.0f },
        { "t_vehicle_name_FV4030/4", 17.2f },
        { "t_vehicle_name_FV4030/4_F", 17.2f },
        { "t_vehicle_name_Ferdinand", 7.98f },
        { "t_vehicle_name_Flakpanzer_E-100", 9.044f },
        { "t_vehicle_name_Hetzer", 5.188f },
        { "t_vehicle_name_IS", 6.517f },
        { "t_vehicle_name_IS-3", 19.152f },
        { "t_vehicle_name_IS-4", 17.63f },
        { "t_vehicle_name_IS-6", 17.0f },
        { "t_vehicle_name_IS-7", 18.208f },
        { "t_vehicle_name_IS-8", 17.63f },
        { "t_vehicle_name_ISU-152", 22.743f },
        { "t_vehicle_name_JPZ4-5", 10.0f },
        { "t_vehicle_name_JagdPanther", 5.054f },
        { "t_vehicle_name_JagdPzIV", 4.656f },
        { "t_vehicle_name_JagdTiger", 14.509f },
        { "t_vehicle_name_JgPz_E-100", 21.429f },
        { "t_vehicle_name_KPz_70", 9.7f },
        { "t_vehicle_name_KV-1", 4.921f },
        { "t_vehicle_name_KV-13", 4.788f },
        { "t_vehicle_name_KV-1S", 5.6f },
        { "t_vehicle_name_KV-2", 13.3f },
        { "t_vehicle_name_KV-3", 4.921f },
        { "t_vehicle_name_KV-4", 12.97f },
        { "t_vehicle_name_KV-5", 12.97f },
        { "t_vehicle_name_KV-9", 13.57f },
        { "t_vehicle_name_KV_4_122", 14.3f },
        { "t_vehicle_name_Leopard1", 8.246f },
        { "t_vehicle_name_Leopard_PT_B", 9.539f },
        { "t_vehicle_name_Lowe", 13.4f },
        { "t_vehicle_name_M1", 6.0f },
        { "t_vehicle_name_M10", 3.0f },
        { "t_vehicle_name_M103", 14.28f },
        { "t_vehicle_name_M2", 2.1f },
        { "t_vehicle_name_M24_Chaffee", 5.27f },
        { "t_vehicle_name_M26", 4.6f },
        { "t_vehicle_name_M36", 5.0f },
        { "t_vehicle_name_M3lee", 5.27f },
        { "t_vehicle_name_M4", 5.27f },
        { "t_vehicle_name_M41", 5.0f },
        { "t_vehicle_name_M46", 10.5f },
        { "t_vehicle_name_M48", 7.0f },
        { "t_vehicle_name_M4A2_L", 5.27f },
        { "t_vehicle_name_M4A3_76", 4.0f },
        { "t_vehicle_name_M4_FL10", 21.698f },
        { "t_vehicle_name_M551", 18.0f },
        { "t_vehicle_name_M5A1", 1.53f },
        { "t_vehicle_name_M6", 5.27f },
        { "t_vehicle_name_M60", 10.2f },
        { "t_vehicle_name_M60A2_Starship", 21.0f },
        { "t_vehicle_name_M60_F", 10.2f },
        { "t_vehicle_name_M6A2E1", 4.0f },
        { "t_vehicle_name_M7", 6.1f },
        { "t_vehicle_name_MS-1", 2.753f },
        { "t_vehicle_name_Mammut", 12.244f },
        { "t_vehicle_name_Marder_II", 3.159f },
        { "t_vehicle_name_Mars_15", 6.5f },
        { "t_vehicle_name_Matilda", 4.0f },
        { "t_vehicle_name_Maus", 14.3f },
        { "t_vehicle_name_Nashorn", 7.98f },
        { "t_vehicle_name_Obj277", 26.0f },
        { "t_vehicle_name_Obj279", 15.0f },
        { "t_vehicle_name_Obj280", 30.0f },
        { "t_vehicle_name_Object_140", 8.778f },
        { "t_vehicle_name_Object_140_F", 8.778f },
        { "t_vehicle_name_Object_263", 14.225f },
        { "t_vehicle_name_Object_268", 19.39f },
        { "t_vehicle_name_Object_701", 10.0f },
        { "t_vehicle_name_Object_704", 22.743f },
        { "t_vehicle_name_Object_777", 18.208f },
        { "t_vehicle_name_P40_737(i)", 6.384f },
        { "t_vehicle_name_Panther", 5.986f },
        { "t_vehicle_name_Panther_II", 5.986f },
        { "t_vehicle_name_PanzerJager_I", 1.264f },
        { "t_vehicle_name_Pz35t", 3.897f },
        { "t_vehicle_name_Pz38nA", 3.059f },
        { "t_vehicle_name_Pz38t", 3.059f },
        { "t_vehicle_name_PzI", 0.898f },
        { "t_vehicle_name_PzII", 0.698f },
        { "t_vehicle_name_PzIII", 2.906f },
        { "t_vehicle_name_PzIII_A", 2.906f },
        { "t_vehicle_name_PzIV", 4.92f },
        { "t_vehicle_name_RAM_MK_II", 3.2f },
        { "t_vehicle_name_SU-100", 5.852f },
        { "t_vehicle_name_SU-100P", 8.427f },
        { "t_vehicle_name_SU-101", 10.5f },
        { "t_vehicle_name_SU-122-54", 13.3f },
        { "t_vehicle_name_SU-152", 23.541f },
        { "t_vehicle_name_SU-152P", 20.68f },
        { "t_vehicle_name_SU-152_Taran", 15.4f },
        { "t_vehicle_name_SU-76", 3.458f },
        { "t_vehicle_name_SU-76M", 4.522f },
        { "t_vehicle_name_SU-85", 5.453f },
        { "t_vehicle_name_StuGIII", 5.188f },
        { "t_vehicle_name_Sturer_Emil", 11.0f },
        { "t_vehicle_name_Sturmtiger", 30.0f },
        { "t_vehicle_name_Sturmtiger_Pt", 33.0f },
        { "t_vehicle_name_Super_Hellcat", 8.4f },
        { "t_vehicle_name_T-25", 5.7f },
        { "t_vehicle_name_T-26", 0.834f },
        { "t_vehicle_name_T-28", 2.893f },
        { "t_vehicle_name_T-30", 1.516f },
        { "t_vehicle_name_T-34", 4.921f },
        { "t_vehicle_name_T-34-100", 10.5f },
        { "t_vehicle_name_T-34-57", 2.926f },
        { "t_vehicle_name_T-34-85", 4.788f },
        { "t_vehicle_name_T-34_M183", 10.773f },
        { "t_vehicle_name_T-43", 4.788f },
        { "t_vehicle_name_T-44", 6.517f },
        { "t_vehicle_name_T-46", 2.893f },
        { "t_vehicle_name_T-46-5", 2.893f },
        { "t_vehicle_name_T-50", 3.857f },
        { "t_vehicle_name_T-54", 10.374f },
        { "t_vehicle_name_T-55", 9.64f },
        { "t_vehicle_name_T-62A", 9.09f },
        { "t_vehicle_name_T110E5", 12.5f },
        { "t_vehicle_name_T18", 3.13f },
        { "t_vehicle_name_T20", 4.0f },
        { "t_vehicle_name_T26E4", 9.5f },
        { "t_vehicle_name_T26E5", 5.0f },
        { "t_vehicle_name_T28", 12.5f },
        { "t_vehicle_name_T28_PT", 12.6f },
        { "t_vehicle_name_T3", 2.5f },
        { "t_vehicle_name_T32", 9.57f },
        { "t_vehicle_name_T34", 19.4f },
        { "t_vehicle_name_T40", 5.14f },
        { "t_vehicle_name_T43", 9.57f },
        { "t_vehicle_name_T54", 7.0f },
        { "t_vehicle_name_T58", 50.0f },
        { "t_vehicle_name_T69", 37.0f },
        { "t_vehicle_name_T82", 5.14f },
        { "t_vehicle_name_T95/Chieftain", 14.3f },
        { "t_vehicle_name_T95/Chieftain_F", 14.3f },
        { "t_vehicle_name_T96", 22.2f },
        { "t_vehicle_name_Tiger", 5.986f },
        { "t_vehicle_name_Tiger_II", 8.0f },
        { "t_vehicle_name_VK3001P", 5.7f },
        { "t_vehicle_name_VK3002DB", 5.7f },
        { "t_vehicle_name_VK3601H", 5.7f },
        { "t_vehicle_name_VK4502H", 9.044f },
        { "t_vehicle_name_VK4502PB", 10.0f },
        { "t_vehicle_name_VK7201", 9.524f },
        { "t_vehicle_name_Vickers_Mk_I", 4.5f },
        { "t_vehicle_name_Vickers_Mk_II", 4.1f },
        { "t_vehicle_name_Vickers_Mk_III", 2.8f },
        { "t_vehicle_name_WT_Auf_PzIV", 15.96f },
        { "t_vehicle_name_XM815", 8.6f },
        { "t_vehicle_name_XM815_Chrysler", 34.6f },
        { "t_vehicle_name_chatillon_25t", 42.5f },
        { "t_vehicle_name_chatillon_25t_F", 42.5f },
        { "t_vehicle_name_tiger_p", 5.986f },
    };

    // Exact turret traverse speed (deg/s) for ALL tanks.
    private static readonly Dictionary<string, float> TurretTraverseOverride = new(StringComparer.OrdinalIgnoreCase)
    {
        { "t_vehicle_name_1K-17", 14.4f },
        { "t_vehicle_name_A-20", 40.5f },
        { "t_vehicle_name_A-34", 33.3f },
        { "t_vehicle_name_A30", 35.0f },
        { "t_vehicle_name_A33_Excelsior", 15.0f },
        { "t_vehicle_name_A39_PT", 35.0f },
        { "t_vehicle_name_AMX30", 36.0f },
        { "t_vehicle_name_AMX50_100", 27.0f },
        { "t_vehicle_name_ASU_57", 33.0f },
        { "t_vehicle_name_ASU_85", 18.0f },
        { "t_vehicle_name_AT-1", 39.38f },
        { "t_vehicle_name_AT_15A", 20.0f },
        { "t_vehicle_name_BT-2", 32.4f },
        { "t_vehicle_name_BT-7", 45.0f },
        { "t_vehicle_name_Black_Prince", 33.3f },
        { "t_vehicle_name_Borsig_WT", 18.0f },
        { "t_vehicle_name_Brummbar", 18.0f },
        { "t_vehicle_name_Chieftain_Mk_V", 24.0f },
        { "t_vehicle_name_Churchill_I", 33.3f },
        { "t_vehicle_name_Churchill_VII", 27.0f },
        { "t_vehicle_name_Covenanter", 40.0f },
        { "t_vehicle_name_Cromwell", 43.5f },
        { "t_vehicle_name_Cruiser_Mk_II", 32.0f },
        { "t_vehicle_name_Cruiser_Mk_III", 42.0f },
        { "t_vehicle_name_Crusader", 43.5f },
        { "t_vehicle_name_E-10", 37.01f },
        { "t_vehicle_name_E-100", 20.6f },
        { "t_vehicle_name_E-50", 31.5f },
        { "t_vehicle_name_E-75", 23.27f },
        { "t_vehicle_name_E-90", 20.0f },
        { "t_vehicle_name_FV214_Conqueror", 18.8f },
        { "t_vehicle_name_FV215B", 15.0f },
        { "t_vehicle_name_FV221", 35.0f },
        { "t_vehicle_name_FV221_Caernarvon_Mk_II", 30.0f },
        { "t_vehicle_name_FV4005", 14.4f },
        { "t_vehicle_name_FV4030/4", 29.0f },
        { "t_vehicle_name_FV4030/4_F", 29.0f },
        { "t_vehicle_name_Ferdinand", 29.09f },
        { "t_vehicle_name_Flakpanzer_E-100", 20.0f },
        { "t_vehicle_name_Hetzer", 39.07f },
        { "t_vehicle_name_IS", 28.8f },
        { "t_vehicle_name_IS-3", 24.3f },
        { "t_vehicle_name_IS-4", 23.4f },
        { "t_vehicle_name_IS-6", 16.8f },
        { "t_vehicle_name_IS-7", 25.2f },
        { "t_vehicle_name_IS-8", 25.2f },
        { "t_vehicle_name_ISU-152", 16.2f },
        { "t_vehicle_name_JPZ4-5", 20.0f },
        { "t_vehicle_name_JagdPanther", 33.25f },
        { "t_vehicle_name_JagdPzIV", 33.25f },
        { "t_vehicle_name_JagdTiger", 24.94f },
        { "t_vehicle_name_JgPz_E-100", 21.26f },
        { "t_vehicle_name_KPz_70", 36.2f },
        { "t_vehicle_name_KV-1", 18.0f },
        { "t_vehicle_name_KV-13", 43.2f },
        { "t_vehicle_name_KV-1S", 28.0f },
        { "t_vehicle_name_KV-2", 16.2f },
        { "t_vehicle_name_KV-3", 14.4f },
        { "t_vehicle_name_KV-4", 14.4f },
        { "t_vehicle_name_KV-5", 19.8f },
        { "t_vehicle_name_KV-9", 18.0f },
        { "t_vehicle_name_KV_4_122", 15.5f },
        { "t_vehicle_name_Leopard1", 41.4f },
        { "t_vehicle_name_Leopard_PT_B", 33.25f },
        { "t_vehicle_name_Lowe", 23.0f },
        { "t_vehicle_name_M1", 33.3f },
        { "t_vehicle_name_M10", 16.0f },
        { "t_vehicle_name_M103", 32.0f },
        { "t_vehicle_name_M2", 29.09f },
        { "t_vehicle_name_M24_Chaffee", 17.1f },
        { "t_vehicle_name_M26", 40.0f },
        { "t_vehicle_name_M36", 16.0f },
        { "t_vehicle_name_M3lee", 30.0f },
        { "t_vehicle_name_M4", 39.0f },
        { "t_vehicle_name_M41", 27.0f },
        { "t_vehicle_name_M46", 38.0f },
        { "t_vehicle_name_M48", 40.0f },
        { "t_vehicle_name_M4A2_L", 18.0f },
        { "t_vehicle_name_M4A3_76", 38.0f },
        { "t_vehicle_name_M4_FL10", 31.5f },
        { "t_vehicle_name_M551", 38.2f },
        { "t_vehicle_name_M5A1", 19.6f },
        { "t_vehicle_name_M6", 26.0f },
        { "t_vehicle_name_M60", 40.0f },
        { "t_vehicle_name_M60A2_Starship", 37.6f },
        { "t_vehicle_name_M60_F", 40.0f },
        { "t_vehicle_name_M6A2E1", 23.0f },
        { "t_vehicle_name_M7", 46.0f },
        { "t_vehicle_name_MS-1", 39.38f },
        { "t_vehicle_name_Mammut", 16.0f },
        { "t_vehicle_name_Marder_II", 39.07f },
        { "t_vehicle_name_Mars_15", 40.0f },
        { "t_vehicle_name_Matilda", 33.3f },
        { "t_vehicle_name_Maus", 24.94f },
        { "t_vehicle_name_Nashorn", 18.0f },
        { "t_vehicle_name_Obj277", 22.0f },
        { "t_vehicle_name_Obj279", 26.0f },
        { "t_vehicle_name_Obj280", 24.0f },
        { "t_vehicle_name_Object_140", 38.0f },
        { "t_vehicle_name_Object_140_F", 38.0f },
        { "t_vehicle_name_Object_263", 26.0f },
        { "t_vehicle_name_Object_268", 26.0f },
        { "t_vehicle_name_Object_701", 23.4f },
        { "t_vehicle_name_Object_704", 17.1f },
        { "t_vehicle_name_Object_777", 26.0f },
        { "t_vehicle_name_P40_737(i)", 22.0f },
        { "t_vehicle_name_Panther", 33.25f },
        { "t_vehicle_name_Panther_II", 33.25f },
        { "t_vehicle_name_PanzerJager_I", 37.41f },
        { "t_vehicle_name_Pz35t", 41.56f },
        { "t_vehicle_name_Pz38nA", 37.41f },
        { "t_vehicle_name_Pz38t", 37.41f },
        { "t_vehicle_name_PzI", 35.44f },
        { "t_vehicle_name_PzII", 41.56f },
        { "t_vehicle_name_PzIII", 41.56f },
        { "t_vehicle_name_PzIII_A", 41.56f },
        { "t_vehicle_name_PzIV", 39.07f },
        { "t_vehicle_name_RAM_MK_II", 38.0f },
        { "t_vehicle_name_SU-100", 30.6f },
        { "t_vehicle_name_SU-100P", 18.0f },
        { "t_vehicle_name_SU-101", 20.0f },
        { "t_vehicle_name_SU-122-54", 23.63f },
        { "t_vehicle_name_SU-152", 18.0f },
        { "t_vehicle_name_SU-152P", 18.0f },
        { "t_vehicle_name_SU-152_Taran", 18.0f },
        { "t_vehicle_name_SU-76", 33.3f },
        { "t_vehicle_name_SU-76M", 35.43f },
        { "t_vehicle_name_SU-85", 29.7f },
        { "t_vehicle_name_StuGIII", 37.01f },
        { "t_vehicle_name_Sturer_Emil", 18.0f },
        { "t_vehicle_name_Sturmtiger", 18.0f },
        { "t_vehicle_name_Sturmtiger_Pt", 18.0f },
        { "t_vehicle_name_Super_Hellcat", 16.7f },
        { "t_vehicle_name_T-25", 30.0f },
        { "t_vehicle_name_T-26", 45.0f },
        { "t_vehicle_name_T-28", 31.5f },
        { "t_vehicle_name_T-30", 45.0f },
        { "t_vehicle_name_T-34", 33.3f },
        { "t_vehicle_name_T-34-100", 33.3f },
        { "t_vehicle_name_T-34-57", 33.3f },
        { "t_vehicle_name_T-34-85", 33.3f },
        { "t_vehicle_name_T-34_M183", 33.3f },
        { "t_vehicle_name_T-43", 43.2f },
        { "t_vehicle_name_T-44", 46.55f },
        { "t_vehicle_name_T-46", 46.8f },
        { "t_vehicle_name_T-46-5", 43.2f },
        { "t_vehicle_name_T-50", 39.6f },
        { "t_vehicle_name_T-54", 40.5f },
        { "t_vehicle_name_T-55", 40.5f },
        { "t_vehicle_name_T-62A", 46.0f },
        { "t_vehicle_name_T110E5", 20.0f },
        { "t_vehicle_name_T18", 33.3f },
        { "t_vehicle_name_T20", 44.0f },
        { "t_vehicle_name_T26E4", 30.0f },
        { "t_vehicle_name_T26E5", 26.0f },
        { "t_vehicle_name_T28", 26.0f },
        { "t_vehicle_name_T28_PT", 18.0f },
        { "t_vehicle_name_T3", 35.44f },
        { "t_vehicle_name_T32", 23.0f },
        { "t_vehicle_name_T34", 18.0f },
        { "t_vehicle_name_T40", 25.0f },
        { "t_vehicle_name_T43", 14.0f },
        { "t_vehicle_name_T54", 16.8f },
        { "t_vehicle_name_T58", 30.0f },
        { "t_vehicle_name_T69", 38.0f },
        { "t_vehicle_name_T82", 25.0f },
        { "t_vehicle_name_T95/Chieftain", 29.4f },
        { "t_vehicle_name_T95/Chieftain_F", 29.4f },
        { "t_vehicle_name_T96", 22.0f },
        { "t_vehicle_name_Tiger", 31.5f },
        { "t_vehicle_name_Tiger_II", 29.09f },
        { "t_vehicle_name_VK3001P", 25.0f },
        { "t_vehicle_name_VK3002DB", 31.5f },
        { "t_vehicle_name_VK3601H", 31.5f },
        { "t_vehicle_name_VK4502H", 29.09f },
        { "t_vehicle_name_VK4502PB", 20.0f },
        { "t_vehicle_name_VK7201", 22.0f },
        { "t_vehicle_name_Vickers_Mk_I", 30.0f },
        { "t_vehicle_name_Vickers_Mk_II", 30.0f },
        { "t_vehicle_name_Vickers_Mk_III", 38.0f },
        { "t_vehicle_name_WT_Auf_PzIV", 18.0f },
        { "t_vehicle_name_XM815", 22.0f },
        { "t_vehicle_name_XM815_Chrysler", 30.0f },
        { "t_vehicle_name_chatillon_25t", 30.0f },
        { "t_vehicle_name_chatillon_25t_F", 30.0f },
        { "t_vehicle_name_tiger_p", 29.0f },
    };

    // Exact hull/chassis traverse speed (deg/s) for ALL tanks.
    private static readonly Dictionary<string, float> ChassisRotateOverride = new(StringComparer.OrdinalIgnoreCase)
    {
        { "t_vehicle_name_1K-17", 11.0f },
        { "t_vehicle_name_A-20", 40.0f },
        { "t_vehicle_name_A-34", 33.0f },
        { "t_vehicle_name_A30", 33.0f },
        { "t_vehicle_name_A33_Excelsior", 16.0f },
        { "t_vehicle_name_A39_PT", 28.0f },
        { "t_vehicle_name_AMX30", 36.0f },
        { "t_vehicle_name_AMX50_100", 25.0f },
        { "t_vehicle_name_ASU_57", 30.0f },
        { "t_vehicle_name_ASU_85", 27.0f },
        { "t_vehicle_name_AT-1", 27.0f },
        { "t_vehicle_name_AT_15A", 24.0f },
        { "t_vehicle_name_BT-2", 32.0f },
        { "t_vehicle_name_BT-7", 43.0f },
        { "t_vehicle_name_Black_Prince", 24.0f },
        { "t_vehicle_name_Borsig_WT", 28.0f },
        { "t_vehicle_name_Brummbar", 25.0f },
        { "t_vehicle_name_Chieftain_Mk_V", 26.0f },
        { "t_vehicle_name_Churchill_I", 20.0f },
        { "t_vehicle_name_Churchill_VII", 20.0f },
        { "t_vehicle_name_Covenanter", 36.0f },
        { "t_vehicle_name_Cromwell", 32.0f },
        { "t_vehicle_name_Cruiser_Mk_II", 31.0f },
        { "t_vehicle_name_Cruiser_Mk_III", 34.0f },
        { "t_vehicle_name_Crusader", 35.0f },
        { "t_vehicle_name_E-10", 44.0f },
        { "t_vehicle_name_E-100", 21.0f },
        { "t_vehicle_name_E-50", 23.0f },
        { "t_vehicle_name_E-75", 17.0f },
        { "t_vehicle_name_E-90", 25.0f },
        { "t_vehicle_name_FV214_Conqueror", 25.0f },
        { "t_vehicle_name_FV215B", 16.4f },
        { "t_vehicle_name_FV221", 28.0f },
        { "t_vehicle_name_FV221_Caernarvon_Mk_II", 19.0f },
        { "t_vehicle_name_FV4005", 11.0f },
        { "t_vehicle_name_FV4030/4", 27.0f },
        { "t_vehicle_name_FV4030/4_F", 27.0f },
        { "t_vehicle_name_Ferdinand", 17.0f },
        { "t_vehicle_name_Flakpanzer_E-100", 35.0f },
        { "t_vehicle_name_Hetzer", 27.0f },
        { "t_vehicle_name_IS", 32.0f },
        { "t_vehicle_name_IS-3", 24.0f },
        { "t_vehicle_name_IS-4", 23.0f },
        { "t_vehicle_name_IS-6", 22.0f },
        { "t_vehicle_name_IS-7", 27.0f },
        { "t_vehicle_name_IS-8", 25.0f },
        { "t_vehicle_name_ISU-152", 16.0f },
        { "t_vehicle_name_JPZ4-5", 36.0f },
        { "t_vehicle_name_JagdPanther", 25.0f },
        { "t_vehicle_name_JagdPzIV", 30.0f },
        { "t_vehicle_name_JagdTiger", 17.0f },
        { "t_vehicle_name_JgPz_E-100", 23.0f },
        { "t_vehicle_name_KPz_70", 35.0f },
        { "t_vehicle_name_KV-1", 18.0f },
        { "t_vehicle_name_KV-13", 34.0f },
        { "t_vehicle_name_KV-1S", 25.0f },
        { "t_vehicle_name_KV-2", 16.0f },
        { "t_vehicle_name_KV-3", 14.0f },
        { "t_vehicle_name_KV-4", 14.0f },
        { "t_vehicle_name_KV-5", 18.0f },
        { "t_vehicle_name_KV-9", 18.0f },
        { "t_vehicle_name_KV_4_122", 16.0f },
        { "t_vehicle_name_Leopard1", 39.0f },
        { "t_vehicle_name_Leopard_PT_B", 30.0f },
        { "t_vehicle_name_Lowe", 23.0f },
        { "t_vehicle_name_M1", 24.0f },
        { "t_vehicle_name_M10", 25.0f },
        { "t_vehicle_name_M103", 28.0f },
        { "t_vehicle_name_M2", 25.0f },
        { "t_vehicle_name_M24_Chaffee", 30.0f },
        { "t_vehicle_name_M26", 44.0f },
        { "t_vehicle_name_M36", 30.0f },
        { "t_vehicle_name_M3lee", 20.0f },
        { "t_vehicle_name_M4", 35.0f },
        { "t_vehicle_name_M41", 33.0f },
        { "t_vehicle_name_M46", 44.0f },
        { "t_vehicle_name_M48", 44.0f },
        { "t_vehicle_name_M4A2_L", 27.0f },
        { "t_vehicle_name_M4A3_76", 37.0f },
        { "t_vehicle_name_M4_FL10", 37.0f },
        { "t_vehicle_name_M551", 37.0f },
        { "t_vehicle_name_M5A1", 30.0f },
        { "t_vehicle_name_M6", 21.0f },
        { "t_vehicle_name_M60", 40.0f },
        { "t_vehicle_name_M60A2_Starship", 36.0f },
        { "t_vehicle_name_M60_F", 40.0f },
        { "t_vehicle_name_M6A2E1", 23.0f },
        { "t_vehicle_name_M7", 36.0f },
        { "t_vehicle_name_MS-1", 31.0f },
        { "t_vehicle_name_Mammut", 20.0f },
        { "t_vehicle_name_Marder_II", 32.0f },
        { "t_vehicle_name_Mars_15", 45.0f },
        { "t_vehicle_name_Matilda", 34.0f },
        { "t_vehicle_name_Maus", 14.0f },
        { "t_vehicle_name_Nashorn", 28.0f },
        { "t_vehicle_name_Obj277", 22.0f },
        { "t_vehicle_name_Obj279", 22.0f },
        { "t_vehicle_name_Obj280", 24.0f },
        { "t_vehicle_name_Object_140", 38.0f },
        { "t_vehicle_name_Object_140_F", 38.0f },
        { "t_vehicle_name_Object_263", 34.0f },
        { "t_vehicle_name_Object_268", 30.0f },
        { "t_vehicle_name_Object_701", 26.0f },
        { "t_vehicle_name_Object_704", 30.0f },
        { "t_vehicle_name_Object_777", 21.0f },
        { "t_vehicle_name_P40_737(i)", 22.0f },
        { "t_vehicle_name_Panther", 23.0f },
        { "t_vehicle_name_Panther_II", 25.0f },
        { "t_vehicle_name_PanzerJager_I", 30.0f },
        { "t_vehicle_name_Pz35t", 38.0f },
        { "t_vehicle_name_Pz38nA", 30.0f },
        { "t_vehicle_name_Pz38t", 29.0f },
        { "t_vehicle_name_PzI", 32.0f },
        { "t_vehicle_name_PzII", 32.0f },
        { "t_vehicle_name_PzIII", 36.0f },
        { "t_vehicle_name_PzIII_A", 36.0f },
        { "t_vehicle_name_PzIV", 27.0f },
        { "t_vehicle_name_RAM_MK_II", 37.0f },
        { "t_vehicle_name_SU-100", 31.0f },
        { "t_vehicle_name_SU-100P", 27.0f },
        { "t_vehicle_name_SU-101", 21.0f },
        { "t_vehicle_name_SU-122-54", 36.0f },
        { "t_vehicle_name_SU-152", 18.0f },
        { "t_vehicle_name_SU-152P", 22.0f },
        { "t_vehicle_name_SU-152_Taran", 24.0f },
        { "t_vehicle_name_SU-76", 29.0f },
        { "t_vehicle_name_SU-76M", 27.0f },
        { "t_vehicle_name_SU-85", 27.0f },
        { "t_vehicle_name_StuGIII", 33.0f },
        { "t_vehicle_name_Sturer_Emil", 28.0f },
        { "t_vehicle_name_Sturmtiger", 16.0f },
        { "t_vehicle_name_Sturmtiger_Pt", 18.0f },
        { "t_vehicle_name_Super_Hellcat", 33.0f },
        { "t_vehicle_name_T-25", 32.0f },
        { "t_vehicle_name_T-26", 45.0f },
        { "t_vehicle_name_T-28", 32.0f },
        { "t_vehicle_name_T-30", 40.0f },
        { "t_vehicle_name_T-34", 33.0f },
        { "t_vehicle_name_T-34-100", 34.0f },
        { "t_vehicle_name_T-34-57", 33.0f },
        { "t_vehicle_name_T-34-85", 32.0f },
        { "t_vehicle_name_T-34_M183", 30.0f },
        { "t_vehicle_name_T-43", 32.0f },
        { "t_vehicle_name_T-44", 40.0f },
        { "t_vehicle_name_T-46", 47.0f },
        { "t_vehicle_name_T-46-5", 36.0f },
        { "t_vehicle_name_T-50", 40.0f },
        { "t_vehicle_name_T-54", 41.0f },
        { "t_vehicle_name_T-55", 41.0f },
        { "t_vehicle_name_T-62A", 38.0f },
        { "t_vehicle_name_T110E5", 25.0f },
        { "t_vehicle_name_T18", 27.0f },
        { "t_vehicle_name_T20", 37.0f },
        { "t_vehicle_name_T26E4", 20.0f },
        { "t_vehicle_name_T26E5", 16.0f },
        { "t_vehicle_name_T28", 22.0f },
        { "t_vehicle_name_T28_PT", 16.0f },
        { "t_vehicle_name_T3", 26.0f },
        { "t_vehicle_name_T32", 23.0f },
        { "t_vehicle_name_T34", 18.0f },
        { "t_vehicle_name_T40", 30.0f },
        { "t_vehicle_name_T43", 20.0f },
        { "t_vehicle_name_T54", 23.0f },
        { "t_vehicle_name_T58", 23.0f },
        { "t_vehicle_name_T69", 42.0f },
        { "t_vehicle_name_T82", 23.0f },
        { "t_vehicle_name_T95/Chieftain", 42.0f },
        { "t_vehicle_name_T95/Chieftain_F", 42.0f },
        { "t_vehicle_name_T96", 18.0f },
        { "t_vehicle_name_Tiger", 20.0f },
        { "t_vehicle_name_Tiger_II", 23.0f },
        { "t_vehicle_name_VK3001P", 25.0f },
        { "t_vehicle_name_VK3002DB", 32.0f },
        { "t_vehicle_name_VK3601H", 22.0f },
        { "t_vehicle_name_VK4502H", 23.0f },
        { "t_vehicle_name_VK4502PB", 20.0f },
        { "t_vehicle_name_VK7201", 15.0f },
        { "t_vehicle_name_Vickers_Mk_I", 35.0f },
        { "t_vehicle_name_Vickers_Mk_II", 26.0f },
        { "t_vehicle_name_Vickers_Mk_III", 30.0f },
        { "t_vehicle_name_WT_Auf_PzIV", 22.0f },
        { "t_vehicle_name_XM815", 24.0f },
        { "t_vehicle_name_XM815_Chrysler", 32.0f },
        { "t_vehicle_name_chatillon_25t", 36.0f },
        { "t_vehicle_name_chatillon_25t_F", 36.0f },
        { "t_vehicle_name_tiger_p", 18.0f },
    };

    public float GetRealMaxHp(TankEntry? tankEntry, float fallback = 1000f)
    {
        if (tankEntry == null) return fallback;
        // Use exact total HP (hull + turret) from binary level0 tables — matches hangar exactly.
        if (tankEntry.Name != null && ExactHangarHp.TryGetValue(tankEntry.Name, out float exact))
            return exact;
        // Fallback for tanks not in the table (shouldn't happen for normal tanks).
        return tankEntry.MaxHp * 1.53f;
    }

    // v53 (задачи 6 и 2): полный HP танка по РЕАЛЬНЫМ модулям = HP корпуса + HP башни. Клиент
    // считает так же: TankHp = EHpBuff + ETurretHpBuff = hull.hp_max + turret.hp_max, где hp_max
    // это Part.Value (тип 1 = корпус, тип 4 = башня). Точный СТОК берём из ExactHangarHp
    // (GetRealMaxHp), а замену/прокачку модулей учитываем как ДЕЛЬТУ HP корпуса и башни
    // относительно стоковых. Так стоковый HP совпадает 1:1 с оригиналом, а другой корпус/башня
    // реально меняет HP — раньше сервер брал HP только по корпусу (смена башни ничего не меняла)
    // или вообще сток по имени танка (смена любого модуля ничего не меняла).
    public int GetTankMaxHp(int vehicleId, int hullId, int turretId)
    {
        var tank = GetTank(vehicleId);
        float stockTotal = GetRealMaxHp(tank);
        if (tank == null) return Math.Max(1, (int)MathF.Round(stockTotal));
        float stockHull   = GetPart(1, tank.HullId)?.Value   ?? 0f;
        float stockTurret = GetPart(4, tank.TurretId)?.Value  ?? 0f;
        float curHull     = GetPart(1, hullId)?.Value   ?? stockHull;
        float curTurret   = GetPart(4, turretId)?.Value  ?? stockTurret;
        float total = stockTotal + (curHull - stockHull) + (curTurret - stockTurret);
        return Math.Max(1, (int)MathF.Round(total));
    }

    // Real default attribute set extracted from the original game's own
    // TextAsset/NewGuideTankAttr.txt (the same table the client itself falls back to —
    // see EqAttributeType enum / GetNewGuidTankattrArray in the decompiled client).
    // Index 0 is unused (EqAttributeType.None); index i+1 below matches EqAttributeType
    // value (i+1) EXACTLY as declared in the decompiled enum — this is not a guess, it was
    // checked field-by-field against EqAttributeType.cs. Units are whatever the client
    // expects raw: weight/load in kg, speed in km/h (client divides indices 2/3/11 by 3.6
    // itself when it builds this locally, so we pre-divide before sending), angles in
    // degrees, distances in meters.
    private static readonly float[] DefaultBattleAttributes = new float[]
    {
        0f,        // 0  None
        750f,      // 1  EPowerBuff (engine power)
        50f,       // 2  EVehicleSpeedLimitsForward (km/h, pre-/3.6 below)
        20f,       // 3  EVehicleSpeedLimitsBackward (km/h, pre-/3.6 below)
        40f,       // 4  EChassisRotationSpeed (deg/s)
        1.0f,      // 5  EChassisTerrainResistanceHard
        1.2f,      // 6  EChassisTerrainResistanceNormal
        1.5f,      // 7  EChassisTerrainResistanceSoft
        60f,       // 8  EChassisMaxClimbAngle
        50000f,    // 9  EChassisBrakeForce
        1.0f,      // 10 EChassisRotationForwardSpeed
        1.2f,      // 11 EChassisSpeedFactorTurn (pre-/3.6 below, per client code)
        46f,       // 12 ETurretRotationSpeed
        0.3f,      // 13 EGunShotDispersionRadius
        0.0f,      // 14 EShotDispersionRadiusMultiple
        0.3f,      // 15 EGunShotDispersionFactorTurretRotation
        0.3f,      // 16 EStable
        0.3f,      // 17 EChassisShotDispersionFactorVehicleRotation
        2.0f,      // 18 EGunShotDispersionFactorAfterShot
        3f,        // 19 EGunAimingTime
        350f,      // 20 ECircularVisionRadiusRange
        0.2f,      // 21 EHideFactor
        0.05f,     // 22 EVehicleCamoMove
        0.05f,     // 23 EGunShotCamo
        6f,        // 24 EReloadTime
        40000f,    // 25 ELoadBuff (kg)
        600f,      // 26 ERadioDistance
        1f,        // 27 EFragmentFactor
        0f,        // 28 EShellExplosionRadius
        350f,      // 29 EShell100PiercingPower
        300f,      // 30 EShell500PiercingPower
        200f,      // 31 EAmmobayHpBuff
        200f,      // 32 EFueltankHpBuff
        300f,      // 33 EShellDamageArmor
        150f,      // 34 EShellDamageDevices
        80f,       // 35 EChassisArmorLeftTrack
        80f,       // 36 EChassisArmorRightTrack
        350f,      // 37 EChassisHpBuff
        500f,      // 38 ETurretHpBuff
        2000f,     // 39 EHpBuff
        0.5f,      // 40 EChassisAbsorb
        1000f,     // 41 EShellAmmoSpeed
        9.8f,      // 42 EShellAmmoGravity
        1500f,     // 43 EShellAmmoMaxDistance
        0.3f,      // 44 EAmmobayNodamgePercentBuff
        0.3f,      // 45 EFueltankNodamgePercentBuff
        -180f,     // 46 ETurretYawLimitLeft
        180f,      // 47 ETurretYawLimitRight
        -20f,      // 48 EGunPitchLimitUp
        10f,       // 49 EGunPitchLimitDown
        100f,      // 50 EGunMaxAmmo
        37500f,    // 51 ETankWeight (kg)
        140f,      // 52 EChassisSpeedInTurn
        120f,      // 53 EViewScopeAngle_Left
        120f,      // 54 EViewScopeAngle_Right
        1f,        // 55 EViewScope_Reduce
        0.33f      // 56 EShotDispersionRadiusReal
    };

    // Builds the 57-slot battle attribute array using real per-tank data wherever we have
    // it (speed/HP/view range from Tanks, engine power from Parts, shell penetration/damage
    // from Shells, ammo capacity from Guns), falling back to the game's own real default
    // table for everything else instead of zeros or guesses.
    //
    // This is THE single source of truth for tank attributes — used for battle
    // (RoomServer's PK_RC_PlayerLoginSuccess.RoomMainTankAttribute), the hangar's tank
    // detail panel, and the research-tree tooltip (HallServer's PK_HC_AllTankAttribute).
    // It used to be duplicated in HallServer.cs as a separate, hand-written copy
    // ("GenerateTankAttributes") that put values at indices that don't match
    // EqAttributeType at all (e.g. reload time went to index 27, which is actually
    // EFragmentFactor — real EReloadTime is 24), which is exactly why the research tree
    // showed nonsense like "Infinity" rate of fire and 0 payload while the hangar (which
    // computes its numbers from the tank's actual equipped parts, not this struct) showed
    // the correct values. Routing every screen through this one method keeps them in sync.
    // v74: клон базовой (стоковой) записи танка с ПОДСТАВЛЕННЫМИ реально установленными
    // игроком модулями (корпус/ходовая/башня/двигатель/бак/боеукладка/рация/прибор).
    // Зачем: BuildTankAttributes ниже считает грузоподъёмность (ELoadBuff) и массу
    // (ETankWeight) по tankEntry.UnderpanId/.HullId/.TurretId — а это ID СТОКОВОГО
    // модуля из статической таблицы Table_Vehicle, а НЕ то, что реально стоит на
    // танке игрока. Из-за этого, например, после установки улучшенной ходовой с
    // бОльшей грузоподъёмностью сервер продолжал присылать клиенту СТАРУЮ (стоковую)
    // грузоподъёмность в PK_HC_AllTankAttribute — клиент валидировал установку новой
    // пушки по этому неверному числу и ошибочно ругался на нехватку грузоподъёмности.
    // Вызывающий код должен передавать сюда РЕАЛЬНЫЕ ID модулей танка игрока (из
    // таблицы Tanks / RoomPlayer), а не значения из GameTables.Tanks[vehicleId].
    public TankEntry WithEquippedModules(TankEntry baseEntry, int hullId, int underpanId, int turretId,
        int engineId = 0, int fueltankId = 0, int cartridgeId = 0, int radioStationId = 0, int gaugeId = 0)
    {
        return new TankEntry
        {
            Id = baseEntry.Id, Name = baseEntry.Name, Tier = baseEntry.Tier, Type = baseEntry.Type,
            Nation = baseEntry.Nation, VehicleType = baseEntry.VehicleType, Price = baseEntry.Price,
            Currency = baseEntry.Currency, CostSilver = baseEntry.CostSilver, CostGold = baseEntry.CostGold,
            HullId = hullId > 0 ? hullId : baseEntry.HullId,
            UnderpanId = underpanId > 0 ? underpanId : baseEntry.UnderpanId,
            TurretId = turretId > 0 ? turretId : baseEntry.TurretId,
            MainGunId = baseEntry.MainGunId, // пушка передаётся в BuildTankAttributes отдельным параметром gunId
            EngineId = engineId > 0 ? engineId : baseEntry.EngineId,
            FueltankId = fueltankId > 0 ? fueltankId : baseEntry.FueltankId,
            CartridgeId = cartridgeId > 0 ? cartridgeId : baseEntry.CartridgeId,
            RadioStationId = radioStationId > 0 ? radioStationId : baseEntry.RadioStationId,
            GaugeId = gaugeId > 0 ? gaugeId : baseEntry.GaugeId,
            MaxHp = baseEntry.MaxHp, Speed = baseEntry.Speed, ViewRange = baseEntry.ViewRange,
        };
    }

    public float[] BuildTankAttributes(TankEntry? tankEntry, int gunId, int shellId, int? tankCurHp = null)
    {
        var attrs = (float[])DefaultBattleAttributes.Clone();

        if (tankEntry != null)
        {
            attrs[2] = tankEntry.Speed;                 // EVehicleSpeedLimitsForward (km/h, converted below)
            attrs[3] = tankEntry.Speed * 0.4f;           // EVehicleSpeedLimitsBackward (~40% of forward, no dedicated reverse stat)
            var turretPart = GetPart(4, tankEntry.TurretId);
            attrs[20] = (turretPart != null && turretPart.Value > 0) ? turretPart.Value : tankEntry.ViewRange; // ECircularVisionRadiusRange
            // tankEntry.MaxHp turns out to be the HULL's HP only (confirmed byte-exact
            // against the Parts table's hull entry for multiple tanks), not the real
            // hangar-displayed total (hull+turret combined) — we don't have real turret HP
            // anywhere in the extracted data (the only turret-level numeric field we have
            // is ViewRange, confirmed by cross-referencing two different tanks). VehicleType
            // reliably separates vehicles with a real separate turret (2) from casemate/
            // fixed-gun vehicles like most tank destroyers (1, confirmed against Ferdinand,
            // Hetzer, JagdTiger, StuGIII, SU-100, ISU-152, all VehicleType=1) — for those,
            // hull HP genuinely IS the total. For VehicleType=2 we apply an approximate
            // turret-HP multiplier calibrated against two tanks where the real hangar total
            // is independently known (Mammut 2800/1600=1.75x, Object 279 3800/2300≈1.65x) —
            // this is a best-effort approximation, not real per-tank turret HP data.
            float hullHp = tankEntry.MaxHp;
            float realTotalHp = GetRealMaxHp(tankEntry);
            attrs[39] = realTotalHp; // EHpBuff
            attrs[37] = hullHp;                          // EChassisHpBuff (hull's real share)
            attrs[38] = realTotalHp - hullHp;             // ETurretHpBuff (approximated turret share)

            var hull = GetPart(1, tankEntry.HullId);
            if (hull != null && hull.Value > 0) attrs[35] = attrs[36] = hull.Value * 0.05f; // rough track armor from hull tier

            var engine = GetPart(6, tankEntry.EngineId);
            if (engine != null && engine.Value > 0) attrs[1] = engine.Value; // EPowerBuff (engine hp) — MT6 matches hangar exactly

            // Hull traverse speed (Скорость поворота корпуса). The underpan module
            // (ModuleType 2) stores this — verified byte-exact against the hangar (T96=18,
            // T26E4=20). Previously this was left at the uniform default (40), which is why
            // every tank turned its hull at the same rate. Now it's per-tank.
            // Chassis traverse from exact binary table data (confirmed accurate).
            var underpan = GetPart(2, tankEntry.UnderpanId);
            if (tankEntry.Name != null && ChassisRotateOverride.TryGetValue(tankEntry.Name, out float chRot) && chRot > 0)
                attrs[4] = chRot;
            else if (underpan != null && underpan.Value > 0)
                attrs[4] = underpan.Value; // EChassisRotationSpeed fallback

            // Per-tank reload + turret-traverse override. These two stats are NOT in any
            // extracted table (the gun table has no reload, and the turret module stores
            // view_range, not rotate_speed) — the same binary-table gap as turret HP. For
            // tanks whose real hangar values we know, we set them exactly; everything else
            // keeps the uniform default. ReloadOverride is in SECONDS (60 / rounds-per-min).
            if (tankEntry.Name != null)
            {
                if (ReloadOverrideSeconds.TryGetValue(tankEntry.Name, out float rl) && rl > 0)
                    attrs[24] = rl;  // EReloadTime
                if (TurretTraverseOverride.TryGetValue(tankEntry.Name, out float tt) && tt > 0)
                    attrs[12] = tt;  // ETurretRotationSpeed
            }

            // v48.11: РЕАЛЬНЫЕ сведение / грузоподъёмность / масса из level0 (part_stats.json).
            // Раньше не выставлялись → у всех танков были дефолты (сведение одинаковое,
            // грузоподъёмность всегда 40 т). Значения в кг; клиент делит на 1000 для тонн.
            // v49: РЕАЛЬНАЯ точность и сведение по правилу клиента (ComputeNewAttribute) с учётом
            // стандарта конкретной машины (Table_Vehicle). Раньше точность (attr13) не задавалась
            // вообще — у всех танков был дефолт; сведение бралось только по aim_max. Теперь оба
            // значения точно совпадают с тем, что клиент считает сам (проверка задачи 3 для игрока).
            attrs[13] = GetGunAccuracyAttr(gunId, tankEntry.Id, attrs[13]);  // EGunShotDispersionRadius (разброс)
            attrs[56] = GetGunAccuracyReal(gunId, attrs[56]);                // EShotDispersionRadiusReal
            attrs[19] = GetGunAimAttr(gunId, tankEntry.Id, attrs[19]);       // EGunAimingTime (сведение, сек)
            if (_chassisLoad.TryGetValue(tankEntry.UnderpanId, out int load) && load > 0)
                attrs[25] = load;                         // ELoadBuff (кг) — по ходовой
            // Масса машины = сумма масс установленных модулей (что есть в таблицах).
            int wsum = 0;
            if (_gunWeight.TryGetValue(gunId, out int gw)) wsum += gw;
            if (_turretWeight.TryGetValue(tankEntry.TurretId, out int tw)) wsum += tw;
            if (_hullWeight.TryGetValue(tankEntry.HullId, out int hw)) wsum += hw;
            if (_chassisWeight.TryGetValue(tankEntry.UnderpanId, out int cw)) wsum += cw;
            if (wsum > 0) attrs[51] = wsum;               // ETankWeight (кг)

            var radio = GetPart(7, tankEntry.RadioStationId);
            if (radio != null && radio.Value > 0) attrs[26] = radio.Value; // ERadioDistance
        }

        var gun = GetGun(gunId);
        if (gun != null && gun.AmmoLoad > 0) attrs[50] = gun.AmmoLoad; // EGunMaxAmmo

        var shell = shellId > 0 ? GetShell(shellId) : null;
        if (shell != null)
        {
            attrs[29] = shell.Penetration;               // EShell100PiercingPower
            attrs[30] = shell.Penetration * 0.7f;         // EShell500PiercingPower (range falloff)
            attrs[33] = shell.Damage;                     // EShellDamageArmor
            attrs[34] = shell.Damage * 0.5f;              // EShellDamageDevices
        }

        // The client divides these three slots by 3.6 itself when it builds this struct
        // locally from km/h source data (see SetTankProperty in the decompiled client) —
        // since our struct goes over the network as final values, we must do that
        // conversion here so the physics (TankMotorMovement.MaxForwardSpeed etc.) receives
        // m/s, not km/h.
        attrs[2] /= 3.6f;
        attrs[3] /= 3.6f;
        attrs[11] /= 3.6f;

        return attrs;
    }

    public void Initialize()
    {
        _tanks.Clear();
        _parts.Clear();
        _shells.Clear();
        _guns.Clear();
        _items.Clear();
        _crews.Clear();
        _maps.Clear();
        _skills.Clear();
        _research.Clear();
        _researchByTank.Clear();
        _researchByPart.Clear();
        _researchByNode.Clear();
        _rewards.Clear();

        _transforms.Clear();
        _transformsByTank.Clear();
        var clientData = LoadClientGameTables();
        LoadTanks(clientData.Tanks);
        LoadParts(clientData.Parts);
        LoadShells(clientData.Shells);
        LoadGuns(clientData.Guns);
        LoadResearch(clientData.Research);
        LoadRewards(clientData.Rewards);

        LoadItems();
        LoadCrews(clientData.TankCrews);
        LoadEquipments(clientData.Equipments);
        LoadConsumables(clientData.Consumables);
        LoadSmokeBombs(clientData.SmokeBombs);
        LoadExteriors(clientData.Exteriors);
        LoadBonusboxes(clientData.Bonusboxes);
        LoadTransforms();   // v47: модификации техники
        LoadPartStats();    // v48.11: сведение/грузоподъёмность/массы из level0
        LoadGunPrecision(); // v49: точность/сведение орудий + стандарты машин из level0
        LoadVehicleEconomy(); // v61 (п.6): пер-танковые коэффициенты фарма
        LoadDestructibles(); // v54: разрушаемые объекты сцены по картам (для сноса ботами)
        LoadMaps();
        LoadSkills();

        Console.WriteLine(
            $"[GameTables] Loaded: {_tanks.Count} tanks, {_parts.Count} typed parts, {_shells.Count} shells, {_guns.Count} guns, {_research.Count} research nodes, {_items.Count} items, {_crews.Count} crews, {_maps.Count} maps, {_rewards.Count} rewards");
    }

    public TankEntry? GetTank(int id) => _tanks.TryGetValue(id, out var tank) ? tank : null;
    public PartEntry? GetPart(int moduleType, int id) => _parts.TryGetValue((moduleType, id), out var part) ? part : null;
    public ShellEntry? GetShell(int id) => _shells.TryGetValue(id, out var shell) ? shell : null;
    public GunEntry? GetGun(int gunId) => _guns.TryGetValue(gunId, out var gun) ? gun : null;

    /// <summary>
    /// Проверяет по Table_Gun.use_turret, подходит ли орудие gunId к башне turretId.
    /// Клиентский CommonTools.FindGunModelName возвращает false и срывает CreateTank(),
    /// если выбранная башня отсутствует в use_turret орудия (бот остаётся невидимым в 3D).
    /// </summary>
    public bool IsGunCompatibleWithTurret(int gunId, int turretId)
    {
        if (turretId <= 0 || !_guns.TryGetValue(gunId, out var gun))
            return true;
        if (gun.UseTurrets == null || gun.UseTurrets.Count == 0)
            return true;
        return gun.UseTurrets.Contains(turretId);
    }
    public ItemEntry? GetItem(int id) => _items.TryGetValue(id, out var item) ? item : null;
    public CrewEntry? GetCrew(int id) => _crews.TryGetValue(id, out var crew) ? crew : null;

    public EquipmentEntry? GetEquipment(int id) => _equipments.TryGetValue(id, out var eq) ? eq : null;
    public ConsumableEntry? GetConsumable(int id) => _consumables.TryGetValue(id, out var con) ? con : null;
    public SmokeBombEntry? GetSmokeBomb(int id) => _smokeBombs.TryGetValue(id, out var sb) ? sb : null;

    public IReadOnlyDictionary<int, ExteriorEntry> Exteriors => _exteriors;

    public IReadOnlyDictionary<int, BonusboxEntry> Bonusboxes => _bonusboxes;
    public BonusboxEntry? GetBonusbox(int id) => _bonusboxes.TryGetValue(id, out var b) ? b : null;

    // v47: модификации техники (Table_Tank_transform).
    public IReadOnlyDictionary<int, TankTransformEntry> Transforms => _transforms;
    public TankTransformEntry? GetTankTransform(int id) => _transforms.TryGetValue(id, out var t) ? t : null;
    public IReadOnlyList<TankTransformEntry> GetTransformsForTank(int tankId)
        => _transformsByTank.TryGetValue(tankId, out var list) ? list : System.Array.Empty<TankTransformEntry>();

    // v33: строки Table_New_bonusbox. Пустой список — не ошибка: сработает фолбэк.
    private void LoadBonusboxes(IEnumerable<BonusboxEntry>? rows)
    {
        // v37: приоритет источников таблицы призов (сверху вниз):
        //   1) GameTables/Data/bonusboxes.json — РЕАЛЬНАЯ таблица, выгруженная из
        //      клиентского level0 через tools/export_bonusbox.py. Совпадает построчно;
        //   2) секция Bonusboxes в client_game_tables.json (передана в rows);
        //   3) встроенная раскладка BuiltinBonusboxes().
        // Как только сработал источник выше — нижние не трогаем.
        if (TryLoadBonusboxFile())
        {
            LogBonusboxSummary("bonusboxes.json");
            return;
        }

        if (rows != null)
            foreach (var r in rows)
                if (r.Id > 0) _bonusboxes[r.Id] = r;

        // v34: если клиентская таблица не выгружена — поднимаем ВСТРОЕННУЮ раскладку,
        // чтобы рулетка работала БЕЗ Python. id строк и reward_itemid сверены со
        // скриншотом колеса. Точная выгрузка (export_bonusbox.py) остаётся
        // рекомендуемой — с ней совпадут и ресурсные секторы.
        if (_bonusboxes.Count == 0)
        {
            foreach (var e in BuiltinBonusboxes())
                _bonusboxes[e.Id] = e;
            Console.WriteLine("[GameTables] Table_New_bonusbox не выгружен — поднята ВСТРОЕННАЯ "
                            + "раскладка. Для ТОЧНОГО совпадения с клиентом (включая ресурсные "
                            + "секторы) запустите tools/export_bonusbox.py на машине с игрой.");
            LogBonusboxSummary("встроенная раскладка");
            return;
        }

        LogBonusboxSummary("client_game_tables.json");
    }

    /// <summary>
    /// Пытается загрузить РЕАЛЬНУЮ таблицу призов из GameTables/Data/bonusboxes.json
    /// (её кладёт tools/export_bonusbox.py). Возвращает true, если файл прочитан и в
    /// нём есть хотя бы одна строка. Заодно принимает выгруженную витрину PowerRewards
    /// (справочно — сервер использует поле для лога, картинку витрины рисует клиент).
    /// </summary>
    // v48.11: грузим реальные характеристики из part_stats.json (вытащены из level0 через
    // type-tree из Assembly-CSharp.dll). Секции: gun_aim (сек), chassis_load (кг),
    // *_weight (кг). Ключи — id соответствующего модуля.
    private void LoadPartStats()
    {
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "GameTables", "Data", "part_stats.json");
            if (!System.IO.File.Exists(path)) { Console.WriteLine("[GameTables] part_stats.json не найден — сведение/грузоподъёмность по умолчанию."); return; }
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            var root = doc.RootElement;
            void FillF(string sec, Dictionary<int, float> d)
            { if (root.TryGetProperty(sec, out var o)) foreach (var p in o.EnumerateObject()) if (int.TryParse(p.Name, out var k)) d[k] = p.Value.GetSingle(); }
            void FillI(string sec, Dictionary<int, int> d)
            { if (root.TryGetProperty(sec, out var o)) foreach (var p in o.EnumerateObject()) if (int.TryParse(p.Name, out var k)) d[k] = p.Value.GetInt32(); }
            FillF("gun_aim", _gunAim);
            FillI("chassis_load", _chassisLoad);
            FillI("gun_weight", _gunWeight);
            FillI("chassis_weight", _chassisWeight);
            FillI("turret_weight", _turretWeight);
            FillI("hull_weight", _hullWeight);
            Console.WriteLine($"[GameTables] part_stats: сведение={_gunAim.Count}, грузоп.={_chassisLoad.Count}, массы g/c/t/h={_gunWeight.Count}/{_chassisWeight.Count}/{_turretWeight.Count}/{_hullWeight.Count}");
        }
        catch (Exception ex) { Console.WriteLine($"[GameTables] Ошибка чтения part_stats.json: {ex.Message}"); }
    }

    // v61 (пункт 6): пер-танковые коэффициенты фарма из vehicle_economy.json.
    // Формат: { "191": {"silver":1.5,"exp":1.3}, ... }. Отсутствующие поля = 1.0.
    private void LoadVehicleEconomy()
    {
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "GameTables", "Data", "vehicle_economy.json");
            if (!System.IO.File.Exists(path)) { Console.WriteLine("[GameTables] vehicle_economy.json not found - default categories (regular 100%, legion 150%, premium 200%)."); return; }
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            var root = doc.RootElement;

            (float, float) ReadMult(string key, (float, float) def)
            {
                if (root.TryGetProperty(key, out var e) && e.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    float s2 = e.TryGetProperty("silver", out var sv) ? sv.GetSingle() : def.Item1;
                    float x2 = e.TryGetProperty("exp",    out var xv) ? xv.GetSingle() : def.Item2;
                    return (s2, x2);
                }
                return def;
            }
            _multRegular = ReadMult("regular_mult", _multRegular);
            _multLegion  = ReadMult("legion_mult",  _multLegion);
            _multPremium = ReadMult("premium_mult", _multPremium);

            if (root.TryGetProperty("legion_ids", out var lids) && lids.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var v in lids.EnumerateArray())
                    if (v.TryGetInt32(out var lid)) _legionVehicleIds.Add(lid);

            void ReadOverrides(System.Text.Json.JsonElement obj)
            {
                foreach (var p in obj.EnumerateObject())
                {
                    if (!int.TryParse(p.Name, out var id) || p.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    float sil = p.Value.TryGetProperty("silver", out var sv) ? sv.GetSingle() : 1f;
                    float exp = p.Value.TryGetProperty("exp",    out var ev) ? ev.GetSingle() : 1f;
                    _vehicleEconomy[id] = (sil, exp);
                }
            }
            ReadOverrides(root);
            if (root.TryGetProperty("overrides", out var ov) && ov.ValueKind == System.Text.Json.JsonValueKind.Object) ReadOverrides(ov);

            Console.WriteLine($"[GameTables] vehicle_economy: regular {_multRegular.Item1:P0}, legion {_multLegion.Item1:P0} (ids={_legionVehicleIds.Count}), premium {_multPremium.Item1:P0}; overrides={_vehicleEconomy.Count}");
        }
        catch (Exception ex) { Console.WriteLine($"[GameTables] vehicle_economy.json read error: {ex.Message}"); }
    }

    // v49: точность/сведение орудий (Table_Gun) + стандарты машин (Table_Vehicle) из level0.
    // gun_precision.json: { gun_precision: {gunId:{amin,amax,areal,aimin,aimax}},
    //                       vehicle_std:   {vehId:{astd,aimstd}} }
    private void LoadGunPrecision()
    {
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "GameTables", "Data", "gun_precision.json");
            if (!System.IO.File.Exists(path)) { Console.WriteLine("[GameTables] gun_precision.json не найден — точность/сведение по умолчанию."); return; }
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            var root = doc.RootElement;
            static float F(System.Text.Json.JsonElement e, string n) => e.TryGetProperty(n, out var v) ? v.GetSingle() : 0f;
            if (root.TryGetProperty("gun_precision", out var gp))
                foreach (var p in gp.EnumerateObject())
                    if (int.TryParse(p.Name, out var k))
                        _gunPrec[k] = new GunPrec(F(p.Value,"amin"), F(p.Value,"amax"), F(p.Value,"areal"), F(p.Value,"aimin"), F(p.Value,"aimax"));
            if (root.TryGetProperty("vehicle_std", out var vs))
                foreach (var p in vs.EnumerateObject())
                    if (int.TryParse(p.Name, out var k))
                        _vehStd[k] = (F(p.Value,"astd"), F(p.Value,"aimstd"));
            Console.WriteLine($"[GameTables] gun_precision: орудий={_gunPrec.Count}, стандартов машин={_vehStd.Count}");
        }
        catch (Exception ex) { Console.WriteLine($"[GameTables] Ошибка чтения gun_precision.json: {ex.Message}"); }
    }

    private void LoadDestructibles()
    {
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "GameTables", "Data", "destructibles.json");
            if (!System.IO.File.Exists(path)) { Console.WriteLine("[GameTables] destructibles.json не найден — снос объектов ботами отключён."); return; }
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            foreach (var mapProp in doc.RootElement.EnumerateObject())
            {
                if (!int.TryParse(mapProp.Name, out var mapId)) continue;
                var list = new List<SceneObj>();
                foreach (var o in mapProp.Value.EnumerateArray())
                {
                    ushort id = (ushort)o.GetProperty("id").GetInt32();
                    float x = o.GetProperty("x").GetSingle();
                    float y = o.GetProperty("y").GetSingle();
                    float z = o.GetProperty("z").GetSingle();
                    bool fall = o.TryGetProperty("f", out var fp) && fp.GetInt32() != 0;   // 1=падает (дерево/столб)
                    list.Add(new SceneObj(id, x, y, z, fall));
                }
                _destructibles[mapId] = list;
            }
            Console.WriteLine($"[GameTables] destructibles: карт={_destructibles.Count}, объектов={_destructibles.Values.Sum(l => l.Count)}");
        }
        catch (Exception ex) { Console.WriteLine($"[GameTables] Ошибка чтения destructibles.json: {ex.Message}"); }
    }

    // Точность (EGunShotDispersionRadius, attr13) по правилу клиента (ComputeNewAttribute):
    // из {accuracy_min, accuracy_max} берём то, что ДАЛЬШЕ от accuracy_standard машины.
    public float GetGunAccuracyAttr(int gunId, int vehicleId, float fallback)
    {
        if (!_gunPrec.TryGetValue(gunId, out var g) || g.AMax <= 0f) return fallback;
        float std = _vehStd.TryGetValue(vehicleId, out var v) && v.astd > 0f ? v.astd : g.AMax;
        return Math.Abs(g.AMin - std) < Math.Abs(g.AMax - std) ? g.AMin : g.AMax;
    }
    // «Реальный» разброс (EShotDispersionRadiusReal, attr56).
    public float GetGunAccuracyReal(int gunId, float fallback)
        => _gunPrec.TryGetValue(gunId, out var g) && g.AReal > 0f ? g.AReal : fallback;
    // Сведение (EGunAimingTime, attr19) по тому же правилу для aim_min/aim_max.
    // Если gun_precision.json не загружен — откат на aim_max из part_stats (как было в v48.11),
    // чтобы отсутствие файла НЕ ухудшало сведение до дефолта.
    public float GetGunAimAttr(int gunId, int vehicleId, float fallback)
    {
        if (!_gunPrec.TryGetValue(gunId, out var g) || g.AiMax <= 0f)
            return _gunAim.TryGetValue(gunId, out var am) && am > 0f ? am : fallback;
        float std = _vehStd.TryGetValue(vehicleId, out var v) && v.aimstd > 0f ? v.aimstd : g.AiMax;
        return Math.Abs(g.AiMin - std) < Math.Abs(g.AiMax - std) ? g.AiMin : g.AiMax;
    }
    // Для ботов: «сырые» значения орудия (без привязки к машине). aim_max с откатом на part_stats.
    public float GetGunAimMax(int gunId)
        => _gunPrec.TryGetValue(gunId, out var g) && g.AiMax > 0f ? g.AiMax
         : (_gunAim.TryGetValue(gunId, out var am) ? am : 0f);
    public float GetGunAccuracyMax(int gunId) => _gunPrec.TryGetValue(gunId, out var g) ? g.AMax  : 0f;
    public float GetGunAccuracyMin(int gunId) => _gunPrec.TryGetValue(gunId, out var g) ? g.AMin  : 0f;

    private bool TryLoadBonusboxFile()
    {
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory,
                "GameTables", "Data", "bonusboxes.json");
            if (!System.IO.File.Exists(path)) return false;

            var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var doc  = System.Text.Json.JsonSerializer.Deserialize<BonusboxFilePayload>(
                           System.IO.File.ReadAllText(path), opts);
            if (doc?.Bonusboxes == null || doc.Bonusboxes.Count == 0) return false;

            foreach (var r in doc.Bonusboxes)
                if (r.Id > 0) _bonusboxes[r.Id] = r;

            if (doc.PowerRewards is { Count: > 0 })
            {
                var first = doc.PowerRewards[0];
                Console.WriteLine($"[GameTables] Витрина главного приза (Table_New_bonusbox_power_reward): "
                                + $"reward_id={first.RewardId} — это КАРТИНКА справа от колеса, её рисует "
                                + $"клиент из level0.");
            }
            return _bonusboxes.Count > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameTables] Ошибка чтения bonusboxes.json: {ex.Message} — "
                            + "перехожу к следующему источнику таблицы призов");
            _bonusboxes.Clear();
            return false;
        }
    }

    // v47: загрузка модификаций техники из GameTables/Data/tank_transforms.json
    // (выгрузка _tankTransformArray из level0). Пустой/битый файл — не фатально:
    // просто не будет модификаций (покупка вернёт неудачу, боты останутся стоковыми).
    private void LoadTransforms()
    {
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory,
                "GameTables", "Data", "tank_transforms.json");
            if (!System.IO.File.Exists(path))
            {
                Console.WriteLine("[GameTables] tank_transforms.json не найден — модификации выключены");
                return;
            }
            var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var doc  = System.Text.Json.JsonSerializer.Deserialize<TransformFilePayload>(
                           System.IO.File.ReadAllText(path), opts);
            if (doc?.Transforms == null) return;
            foreach (var t in doc.Transforms)
            {
                if (t.Id <= 0) continue;
                _transforms[t.Id] = t;
                if (!_transformsByTank.TryGetValue(t.TankId, out var list))
                    _transformsByTank[t.TankId] = list = new List<TankTransformEntry>();
                list.Add(t);
            }
            Console.WriteLine($"[GameTables] Модификации (Table_Tank_transform): {_transforms.Count} строк "
                            + $"для {_transformsByTank.Count} танков");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameTables] Ошибка чтения tank_transforms.json: {ex.Message}");
            _transforms.Clear();
            _transformsByTank.Clear();
        }
    }

    private sealed class TransformFilePayload
    {
        public List<TankTransformEntry>? Transforms { get; set; }
    }

    private void LogBonusboxSummary(string source)
    {
        int tanks = _bonusboxes.Values.Count(b => b.IsTank);
        int main  = _bonusboxes.Values.Count(b => b.IsMainPrize);
        Console.WriteLine($"[GameTables] Table_New_bonusbox ← {source}: {_bonusboxes.Count} строк "
                        + $"(танков {tanks}, из них главный приз {main})");
    }

    // Формат bonusboxes.json (выход tools/export_bonusbox.py).
    private sealed class BonusboxFilePayload
    {
        public List<BonusboxEntry>? Bonusboxes { get; set; }
        public List<PowerRewardEntry>? PowerRewards { get; set; }
    }

    private sealed class PowerRewardEntry
    {
        public int Id { get; set; }
        public int ActiveRatio { get; set; }
        public int RewardType { get; set; }
        public int RewardCount { get; set; }
        public int RewardDiscount { get; set; }
        public int RewardId { get; set; }
        public int RewardGoldValue { get; set; }
    }

    /// <summary>
    /// Встроенная раскладка призов рулетки — запасной вариант, когда клиентская
    /// таблица не выгружена. reward_itemtype: 1 опыт, 2 серебро, 3 золото, 4 танк,
    /// 5 VIP, 6 расходники. Для типа 4 reward_itemcount — срок в днях (>0) или
    /// навсегда (<0), reward_level = 1 помечает ГЛАВНЫЙ приз.
    ///
    /// id строк подобраны так, чтобы reward_itemid временных танков совпадал с тем,
    /// что показывает клиент на этой сборке.
    /// </summary>
    private static IEnumerable<BonusboxEntry> BuiltinBonusboxes()
    {
        // Обычные (не-танковые) призы: делятся между собой по DropRate.
        yield return new BonusboxEntry { Id = 1,  RewardItemType = 2, RewardItemId = 0,  RewardItemCount = 50_000,  DropRate = 300 }; // серебро 50k
        yield return new BonusboxEntry { Id = 2,  RewardItemType = 3, RewardItemId = 0,  RewardItemCount = 300,     DropRate = 220 }; // золото 300
        yield return new BonusboxEntry { Id = 3,  RewardItemType = 2, RewardItemId = 0,  RewardItemCount = 100_000, DropRate = 200 }; // серебро 100k
        yield return new BonusboxEntry { Id = 4,  RewardItemType = 5, RewardItemId = 0,  RewardItemCount = 1,       DropRate = 130 }; // VIP 1 день
        yield return new BonusboxEntry { Id = 5,  RewardItemType = 3, RewardItemId = 0,  RewardItemCount = 500,     DropRate = 90  }; // золото 500
        yield return new BonusboxEntry { Id = 6,  RewardItemType = 1, RewardItemId = 0,  RewardItemCount = 100_000, DropRate = 120 }; // опыт 100k
        yield return new BonusboxEntry { Id = 7,  RewardItemType = 2, RewardItemId = 0,  RewardItemCount = 250_000, DropRate = 60  }; // серебро 250k

        // Премиум-танки (сектор 3), на 7 дней. Суммарный шанс задаётся в PickBonusbox.
        yield return new BonusboxEntry { Id = 20, RewardItemType = 4, RewardLevel = 0, RewardItemId = 84,  RewardItemCount = 7 }; // T-30
        yield return new BonusboxEntry { Id = 21, RewardItemType = 4, RewardLevel = 0, RewardItemId = 86,  RewardItemCount = 7 }; // T-46-5
        yield return new BonusboxEntry { Id = 22, RewardItemType = 4, RewardLevel = 0, RewardItemId = 90,  RewardItemCount = 7 }; // M4A2 L
        yield return new BonusboxEntry { Id = 23, RewardItemType = 4, RewardLevel = 0, RewardItemId = 172, RewardItemCount = 7 }; // M7
        yield return new BonusboxEntry { Id = 24, RewardItemType = 4, RewardLevel = 0, RewardItemId = 88,  RewardItemCount = 7 }; // P40 737(i)

        // ГЛАВНЫЙ приз (сектор 9, reward_level = 1), навсегда. Две строки —
        // FV4030/4 и FV214 Conqueror; сервер сам выбирает ту, чьего танка ещё нет.
        yield return new BonusboxEntry { Id = 40, RewardItemType = 4, RewardLevel = 1, RewardItemId = 174, RewardItemCount = -1 }; // FV4030/4
        yield return new BonusboxEntry { Id = 41, RewardItemType = 4, RewardLevel = 1, RewardItemId = 163, RewardItemCount = -1 }; // FV214 Conqueror
    }

    /// <summary>
    /// Камуфляж по id. Если Table_Cloth не выгружен (список пуст) или id незнаком —
    /// возвращаем заглушку с усреднённой ценой, чтобы покупка всё равно работала.
    /// Клиент цену считает сам по своей таблице, поэтому расхождение возможно
    /// только на неизвестных id — оно логируется.
    /// </summary>
    public ExteriorEntry GetExterior(int id)
    {
        if (_exteriors.TryGetValue(id, out var ext)) return ext;
        if (_exteriors.Count > 0)
            Console.WriteLine($"[GameTables] WARNING: камуфляж id={id} отсутствует в Table_Cloth — беру фолбэк");
        return ExteriorEntry.Fallback(id);
    }

    // v30: строки Table_Cloth. Пустой список — не ошибка: сервер уйдёт на фолбэк.
    private void LoadExteriors(IEnumerable<ExteriorEntry>? exteriors)
    {
        if (exteriors == null) return;
        foreach (var e in exteriors)
            if (e.Id > 0) _exteriors[e.Id] = e;

        if (_exteriors.Count > 0)
            Console.WriteLine($"[GameTables] Loaded {_exteriors.Count} Table_Cloth rows (камуфляжи)");
        else
            Console.WriteLine("[GameTables] Table_Cloth не выгружен — цены камуфляжей по фолбэку "
                            + "(запустите tools/extract_client_tables.py, чтобы получить реальные)");
    }

    // Load оснащение effect rows (Table_Equipment) if present in the JSON. When empty,
    // RoomServer falls back to its hardcoded EquipmentEffects table.
    private void LoadEquipments(IEnumerable<EquipmentEntry>? equipments)
    {
        _equipments.Clear();
        if (equipments == null) return;
        int n = 0;
        foreach (var e in equipments)
        {
            if (e.Id <= 0) continue;
            _equipments[e.Id] = e;
            n++;
        }
        if (n > 0) Console.WriteLine($"[GameTables] Loaded {n} Table_Equipment rows");
    }

    private void LoadConsumables(IEnumerable<ConsumableEntry>? consumables)
    {
        _consumables.Clear();
        if (consumables == null) return;
        int n = 0;
        foreach (var c in consumables)
        {
            if (c.Id <= 0) continue;
            _consumables[c.Id] = c;
            n++;
        }
        if (n > 0) Console.WriteLine($"[GameTables] Loaded {n} Table_Consumable rows");
    }

    private void LoadSmokeBombs(IEnumerable<SmokeBombEntry>? smokeBombs)
    {
        _smokeBombs.Clear();
        if (smokeBombs == null) return;
        int n = 0;
        foreach (var s in smokeBombs)
        {
            if (s.Id <= 0) continue;
            _smokeBombs[s.Id] = s;
            n++;
        }
        if (n > 0) Console.WriteLine($"[GameTables] Loaded {n} Table_Smoke_bomb rows");
    }
    public MapEntry? GetMap(int id) => _maps.TryGetValue(id, out var map) ? map : null;
    public SkillEntry? GetSkill(int id) => _skills.TryGetValue(id, out var skill) ? skill : null;
    public RewardEntry? GetReward(int id) => _rewards.TryGetValue(id, out var reward) ? reward : null;

    public List<TankEntry> GetTanksByTier(int tier) => _tanks.Values.Where(t => t.Tier == tier).ToList();
    public List<ShellEntry> GetShellsByType(int type) => _shells.Values.Where(s => s.Type == type).ToList();
    public List<ItemEntry> GetItemsByType(byte type) => _items.Values.Where(i => i.Type == type).ToList();
    public IReadOnlyList<ResearchEntry> GetResearchForTank(int tankId) => _researchByTank.TryGetValue(tankId, out var nodes) ? nodes : Array.Empty<ResearchEntry>();
    public ResearchEntry? GetResearchEntry(int tankId, int moduleType, int partId) => _researchByPart.TryGetValue((tankId, moduleType, partId), out var node) ? node : null;
    public ResearchEntry? GetResearchNode(int tankId, int nodeId) => _researchByNode.TryGetValue((tankId, nodeId), out var node) ? node : null;

    public int GetHullMaxHp(int vehicleId, int hullId)
    {
        var hull = GetPart(1, hullId);
        if (hull != null && hull.Value > 0)
        {
            return Math.Max(1, (int)MathF.Round(hull.Value));
        }

        return GetTank(vehicleId)?.MaxHp ?? 1;
    }

    public bool IsShellCompatibleWithGun(int gunId, int shellId)
    {
        if (shellId <= 0)
        {
            return false;
        }

        var gun = GetGun(gunId);
        return gun != null && (gun.Ammo1Id == shellId || gun.Ammo2Id == shellId || gun.Ammo3Id == shellId);
    }

    public (string ShellSlots, string DeployShellSlots, int CurShellId) BuildDefaultShellLoadout(int gunId)
    {
        var slots = BuildDefaultShellSlots(gunId);
        var serialized = SerializeShellSlots(slots);
        var currentShellId = slots.FirstOrDefault(slot => slot.ID > 0 && slot.Count > 0).ID;
        return (serialized, serialized, currentShellId);
    }

    private static ClientGameTablesPayload LoadClientGameTables()
    {
        string dataPath = Path.Combine(AppContext.BaseDirectory, "GameTables", "Data", "client_game_tables.json");
        if (!File.Exists(dataPath))
        {
            throw new FileNotFoundException($"Client game tables JSON not found: {dataPath}");
        }

        var payload = JsonSerializer.Deserialize<ClientGameTablesPayload>(
            File.ReadAllText(dataPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return payload ?? new ClientGameTablesPayload();
    }

    private void LoadRewards(IEnumerable<RewardEntry> source)
    {
        foreach (var r in source.Where(r => r.Id > 0))
            _rewards[r.Id] = r;
    }

    private void LoadTanks(IEnumerable<TankEntry> source)
    {
        foreach (var tank in source.Where(t => t.Id > 0))
        {
            ResolveCurrencyCost(tank.Price, tank.Currency, out int silver, out int gold);
            _tanks[tank.Id] = new TankEntry
            {
                Id = tank.Id,
                Name = tank.Name,
                Tier = tank.Tier,
                Type = tank.Type,
                Nation = tank.Nation,
                VehicleType = tank.VehicleType,
                Price = tank.Price,
                Currency = tank.Currency,
                CostSilver = silver,
                CostGold = gold,
                HullId = tank.HullId,
                UnderpanId = tank.UnderpanId,
                TurretId = tank.TurretId,
                MainGunId = tank.MainGunId,
                EngineId = tank.EngineId,
                FueltankId = tank.FueltankId,
                CartridgeId = tank.CartridgeId,
                RadioStationId = tank.RadioStationId,
                GaugeId = tank.GaugeId,
                MaxHp = tank.MaxHp,
                Speed = tank.Speed,
                ViewRange = tank.ViewRange
            };
        }
    }

    private void LoadParts(IEnumerable<PartEntry> source)
    {
        foreach (var part in source.Where(p => p.Id > 0 && p.ModuleType > 0))
        {
            ResolveCurrencyCost(part.Price, part.Currency, out int silver, out int gold);
            _parts[(part.ModuleType, part.Id)] = new PartEntry
            {
                Id = part.Id,
                Name = part.Name,
                ModuleType = part.ModuleType,
                Price = part.Price,
                Currency = part.Currency,
                Type = part.ModuleType,
                Tier = part.Tier,
                CostSilver = silver,
                CostGold = gold,
                Value = part.Value
            };
        }
    }

    private void LoadShells(IEnumerable<ShellEntry> source)
    {
        foreach (var shell in source.Where(s => s.Id > 0))
        {
            ResolveCurrencyCost(shell.Price, shell.Currency, out int silver, out int gold);
            _shells[shell.Id] = new ShellEntry
            {
                Id = shell.Id,
                Name = shell.Name,
                Price = shell.Price,
                Currency = shell.Currency,
                Type = shell.Type,
                CostSilver = silver,
                CostGold = gold,
                Damage = shell.Damage,
                Penetration = shell.Penetration
            };
        }
    }

    private void LoadGuns(IEnumerable<GunEntry> source)
    {
        foreach (var gun in source.Where(g => g.GunId > 0))
        {
            _guns[gun.GunId] = gun;
        }
    }

    private void LoadResearch(IEnumerable<ResearchEntry> source)
    {
        foreach (var entry in source.Where(r => r.Id > 0 && r.TankId > 0 && r.NodeId > 0))
        {
            entry.ParentNodeIds = entry.ParentNodeIds.Where(id => id > 0).Distinct().ToArray();
            entry.ChildNodeIds = entry.ChildNodeIds.Where(id => id > 0).Distinct().ToArray();

            _research.Add(entry);
            _researchByPart[(entry.TankId, entry.PartType, entry.PartId)] = entry;
            _researchByNode[(entry.TankId, entry.NodeId)] = entry;

            if (!_researchByTank.TryGetValue(entry.TankId, out var nodes))
            {
                nodes = new List<ResearchEntry>();
                _researchByTank[entry.TankId] = nodes;
            }

            nodes.Add(entry);
        }

        foreach (var nodes in _researchByTank.Values)
        {
            nodes.Sort((left, right) => left.NodeId.CompareTo(right.NodeId));
        }
    }

    private static void ResolveCurrencyCost(int price, int currency, out int silver, out int gold)
    {
        silver = 0;
        gold = 0;

        switch (currency)
        {
            case 2:
                gold = price;
                break;
            default:
                silver = price;
                break;
        }
    }

    private ShellSlot[] BuildDefaultShellSlots(int gunId)
    {
        var result = new ShellSlot[3];
        var gun = GetGun(gunId);
        if (gun == null)
        {
            return result;
        }

        var ammoDefs = new List<(int ShellId, float Ratio)>
        {
            (gun.Ammo1Id, gun.Ammo1Ratio),
            (gun.Ammo2Id, gun.Ammo2Ratio),
            (gun.Ammo3Id, gun.Ammo3Ratio)
        }
        .Where(ammo => ammo.ShellId > 0)
        .ToList();

        if (ammoDefs.Count == 0)
        {
            return result;
        }

        int ammoLoad = Math.Max(gun.AmmoLoad, 1);
        float totalPositiveRatio = ammoDefs.Where(ammo => ammo.Ratio > 0f).Sum(ammo => ammo.Ratio);
        var counts = new int[ammoDefs.Count];
        int allocated = 0;

        for (int i = 0; i < ammoDefs.Count; i++)
        {
            float weight = totalPositiveRatio > 0f
                ? Math.Max(0f, ammoDefs[i].Ratio) / totalPositiveRatio
                : (i == 0 ? 1f : 0f);

            counts[i] = i == ammoDefs.Count - 1
                ? Math.Max(0, ammoLoad - allocated)
                : Math.Max(0, (int)MathF.Round(ammoLoad * weight));

            allocated += counts[i];
        }

        while (allocated > ammoLoad)
        {
            int index = Array.FindLastIndex(counts, count => count > 0);
            if (index < 0)
            {
                break;
            }

            counts[index]--;
            allocated--;
        }

        while (allocated < ammoLoad)
        {
            counts[0]++;
            allocated++;
        }

        for (int i = 0; i < result.Length; i++)
        {
            if (i >= ammoDefs.Count)
            {
                result[i] = new ShellSlot();
                continue;
            }

            result[i] = new ShellSlot
            {
                ID = ammoDefs[i].ShellId,
                Count = counts[i]
            };
        }

        return result;
    }

    private static string SerializeShellSlots(IReadOnlyList<ShellSlot> slots)
    {
        return string.Join(
            ";",
            Enumerable.Range(0, 3)
                .Select(index =>
                {
                    if (index >= slots.Count)
                    {
                        return "0,0";
                    }

                    return $"{slots[index].ID},{slots[index].Count}";
                }));
    }

    private void LoadItems()
    {
        AddItem(1, "Repair Kit", 1, 500, 0, "Repair tank in battle");
        AddItem(2, "First Aid Kit", 1, 300, 0, "Heal crew in battle");
        AddItem(3, "Fire Extinguisher", 1, 400, 0, "Extinguish fire in battle");
        AddItem(4, "Premium Account 1 day", 2, 0, 100, "Double exp and silver for 1 day");
        AddItem(5, "Premium Account 7 days", 2, 0, 500, "Double exp and silver for 7 days");
        AddItem(6, "Premium Account 30 days", 2, 0, 1500, "Double exp and silver for 30 days");
        AddItem(7, "Camouflage", 3, 10000, 0, "Reduce visibility by 10%");
        AddItem(8, "Emblem Slot", 4, 5000, 0, "Additional emblem slot");
        AddItem(9, "Garage Slot", 5, 0, 200, "Additional tank slot in garage");
        AddItem(10, "Crew Slot", 6, 0, 100, "Additional crew slot");
        AddItem(11, "XP Booster", 7, 0, 50, "Double tank XP for 1 battle");
        AddItem(12, "Silver Booster", 7, 0, 30, "Double silver for 1 battle");
    }

    private void AddItem(int id, string name, byte type, int costSilver, int costGold, string desc)
    {
        _items[id] = new ItemEntry
        {
            Id = id,
            Name = name,
            Type = type,
            CostSilver = costSilver,
            CostGold = costGold,
            Description = desc
        };
    }

    private void LoadCrews(IEnumerable<TankCrewEntry>? tankCrews = null)
    {
        // Hire-dialog crews. The client sends ONLY the crew id (Table_Tank_crew.ID) on
        // NM_CH_BuyCrew; the tier is Table_Tank_crew.type, and the starting level is a
        // fixed function of type (confirmed from live hires):
        //   type 1  Новобранец / Recruit    -> level 0   (free)
        //   type 2  Специалист / Specialist -> level 5   (80 000 silver)
        //   type 3  Элита / Elite            -> level 10  (800 gold)
        //   type 4  Ас / Ace                 -> level 12  (2 200 gold)
        // The ids are per-nation and do NOT follow any formula, so each real id is mapped
        // to its type here. Confirmed so far (extend as more nations get logged):
        //   Soviet:  5 = Recruit, 6 = Specialist, 7 = Elite, 94 = Ace
        AddCrewByType(5, 1);  AddCrewByType(6, 2);  AddCrewByType(7, 3);                        // Soviet normals (6,7 confirmed)
        AddCrewByType(10, 1); AddCrewByType(11, 2); AddCrewByType(12, 3);                        // English normals (12 confirmed)
        // Elites (type 3) of the other nations, from ordered "Elite then Ace" hires (billed
        // as Elite client-side, came in UNKNOWN on the server). Add more here if any Elite
        // still shows level 0 after a fresh hire.
        AddCrewByType(20, 3); AddCrewByType(21, 3); AddCrewByType(3, 3); AddCrewByType(28, 3); AddCrewByType(16, 3);
        // Aces (type 4) are all HIGH ids (60–99+) and are handled by a rule in HandleBuyCrew
        // (id >= 50 → Ace, level 12) — no need to list them here.
        // and read the "[HS] BuyCrew: crew id X" lines, then add the ids here.

        // Optional override/extension from client_game_tables.json "TankCrews" (id + type).
        if (tankCrews != null)
        {
            int loaded = 0;
            foreach (var c in tankCrews)
            {
                if (c.Id <= 0) continue;
                AddCrewByType(c.Id, c.Type);
                loaded++;
            }
            if (loaded > 0) Console.WriteLine($"[GameTables] Loaded {loaded} Table_Tank_crew hire rows from JSON");
        }
    }

    // Register a hire-dialog crew by its Table_Tank_crew.type. Level and price are a fixed
    // function of type (uniform across all nations).
    private void AddCrewByType(int id, int type)
    {
        int lv = type switch { 1 => 0, 2 => 5, 3 => 10, 4 => 12, _ => 0 };
        (int silver, int gold) = type switch
        {
            2 => (80000, 0),
            3 => (0, 800),
            4 => (0, 2200),
            _ => (0, 0),
        };
        _crews[id] = new CrewEntry
        {
            Id = id,
            Name = $"tier{type}",
            CostSilver = silver,
            CostGold = gold,
            Type = type,
            SkillIds = new[] { 1, 2, 3 },
            InitLv = lv,
        };
    }

    private void AddCrew(int id, string name, int costSilver, int costGold, int type, int[] skillIds, int initLv = 1)
    {
        _crews[id] = new CrewEntry
        {
            Id = id,
            Name = name,
            CostSilver = costSilver,
            CostGold = costGold,
            Type = type,
            SkillIds = skillIds,
            InitLv = initLv,
        };
    }

    private void LoadMaps()
    {
        AddMap(1, "Desert Storm", 1, 8);
        AddMap(2, "Forest Battle", 1, 8);
        AddMap(3, "City Ruins", 1, 8);
        AddMap(4, "Mountain Pass", 1, 8);
        AddMap(5, "Winter Field", 1, 8);
        AddMap(6, "Industrial Zone", 2, 8);
        AddMap(7, "River Crossing", 2, 8);
        AddMap(8, "Open Plains", 2, 8);
    }

    private void AddMap(int id, string name, int battleMode, int maxPlayers)
    {
        _maps[id] = new MapEntry
        {
            Id = id,
            Name = name,
            BattleMode = battleMode,
            MaxPlayers = maxPlayers
        };
    }

    private void LoadSkills()
    {
        AddSkill(1, "Eagle Eye", 10, new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f });
        AddSkill(2, "Iron Will", 10, new[] { 2f, 4f, 6f, 8f, 10f, 12f, 14f, 16f, 18f, 20f });
        AddSkill(3, "Quick Reload", 10, new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f });
        AddSkill(4, "Smooth Ride", 10, new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f });
        AddSkill(5, "Fireproof", 10, new[] { 5f, 10f, 15f, 20f, 25f, 30f, 35f, 40f, 45f, 50f });
        AddSkill(6, "Repair Master", 10, new[] { 5f, 10f, 15f, 20f, 25f, 30f, 35f, 40f, 45f, 50f });
        AddSkill(7, "Sixth Sense", 10, new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f });
        AddSkill(8, "Brothers in Arms", 10, new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f });
        AddSkill(9, "Camouflage", 10, new[] { 2f, 4f, 6f, 8f, 10f, 12f, 14f, 16f, 18f, 20f });
    }

    private void AddSkill(int id, string name, int maxLevel, float[] effects)
    {
        _skills[id] = new SkillEntry
        {
            Id = id,
            Name = name,
            MaxLevel = maxLevel,
            Effects = effects
        };
    }
}
