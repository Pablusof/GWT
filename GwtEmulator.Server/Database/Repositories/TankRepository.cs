using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using GwtEmulator.Server.Database.Models;
using GwtEmulator.Server.GameTables;

namespace GwtEmulator.Server.Database.Repositories;

public class TankRepository
{
    // Было 9_999_999 — при создании КАЖДОГО танка, при каждом логине
    // (LoginServer.EnsureMinimumTankExp) и при каждом запросе исследования
    // (EnsurePlayerHangarConsistency) опыт машины принудительно поднимался почти
    // до 10 млн. Из-за этого в HandleResearchRequest ветка «хватает опыта машины»
    // (ownedTank.TankExp >= researchEntry.ExpCost) срабатывала всегда первой, и
    // свободный опыт аккаунта вообще не расходовался — выглядело так, будто
    // «весь свободный опыт перетёк в танк и тратится оттуда». Теперь танки
    // стартуют с 0 своего опыта, и все исследования закономерно идут из
    // свободного опыта аккаунта (ветка v66 в HandleResearchRequest).
    public const int LocalTestTankExp = 0;

    private readonly DatabaseService _db;
    private readonly GameTablesService _gameTables;
    private readonly ModuleRepository? _moduleRepo;

    public TankRepository(DatabaseService db, GameTablesService gameTables, ModuleRepository? moduleRepo = null)
    {
        _db = db;
        _gameTables = gameTables;
        _moduleRepo = moduleRepo;
    }

    public List<Tank> GetPlayerTanks(long playerGuid)
    {
        var tanks = new List<Tank>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Tanks WHERE PlayerGUID = @guid ORDER BY CarportIndex";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            tanks.Add(MapTank(reader));
        }

        return tanks;
    }

    public Tank? GetTankByGUID(long tankGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Tanks WHERE TankGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", tankGuid);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapTank(reader) : null;
    }

    public Tank CreateStarterTank(long playerGuid)
    {
        const int starterVehicleId = 81;
        var existingTank = GetPlayerTanks(playerGuid).FirstOrDefault(t => t.VehicleId == starterVehicleId);
        if (existingTank != null)
        {
            NormalizePlayerTanks(playerGuid);
            return GetPlayerTanks(playerGuid).First(t => t.VehicleId == starterVehicleId);
        }

        var starterEntry = _gameTables.GetTank(starterVehicleId)
            ?? throw new InvalidOperationException("Starter tank 81 is missing from client game tables");

        var starterTank = InsertTank(playerGuid, starterEntry, GetPlayerTanks(playerGuid).Count == 0);
        ResearchBaseModules(playerGuid, starterTank);
        return starterTank;
    }

    public void CreateAllTanks(long playerGuid)
    {
        if (GetPlayerTanks(playerGuid).Count == 0)
        {
            CreateStarterTank(playerGuid);
        }

        NormalizePlayerTanks(playerGuid);
    }

    public void NormalizePlayerTanks(long playerGuid)
    {
        lock (_db.SyncRoot)
        {
            var tanks = GetPlayerTanks(playerGuid);
            if (tanks.Count == 0) return;

            var existingModules = _moduleRepo?.GetResearchedModuleSet(playerGuid);
            using var tx = _db.Connection.BeginTransaction();

        foreach (var tank in tanks)
        {
            var tankEntry = _gameTables.GetTank(tank.VehicleId);
            if (tankEntry == null)
            {
                continue;
            }

            int hullId = _gameTables.GetPart(1, tank.HullID) != null ? tank.HullID : tankEntry.HullId;
            int underpanId = _gameTables.GetPart(2, tank.UnderpanID) != null || _gameTables.GetPart(3, tank.UnderpanID) != null
                ? tank.UnderpanID
                : tankEntry.UnderpanId;
            int turretId = _gameTables.GetPart(4, tank.TurretID) != null ? tank.TurretID : tankEntry.TurretId;
            int mainGunId = _gameTables.GetPart(5, tank.MainGunID) != null ? tank.MainGunID : tankEntry.MainGunId;
            int engineId = _gameTables.GetPart(6, tank.EngineID) != null ? tank.EngineID : tankEntry.EngineId;
            int radioId = _gameTables.GetPart(7, tank.RadioStationID) != null ? tank.RadioStationID : tankEntry.RadioStationId;
            int cartridgeId = _gameTables.GetPart(8, tank.CartridgeID) != null ? tank.CartridgeID : tankEntry.CartridgeId;
            int fuelTankId = _gameTables.GetPart(9, tank.FueltankID) != null ? tank.FueltankID : tankEntry.FueltankId;
            int gaugeId = _gameTables.GetPart(10, tank.GaugeID) != null ? tank.GaugeID : tankEntry.GaugeId;

            int tankHp = _gameTables.GetHullMaxHp(tank.VehicleId, hullId);
            int tankCurHp = NormalizeCurrentHp(tank.TankHp, tank.TankCurHp, tankHp);
            byte tankCurrencyType = ResolveTankCurrencyType(tankEntry.Currency);

            string shellSlots = tank.ShellSlots;
            string deployShellSlots = tank.DeployShellSlots;
            int curShellId = tank.CurShellId;

            bool shellLoadoutInvalid =
                !HasCompatibleShellLoadout(mainGunId, shellSlots) ||
                !HasCompatibleShellLoadout(mainGunId, deployShellSlots);

            if (shellLoadoutInvalid)
            {
                var defaultLoadout = _gameTables.BuildDefaultShellLoadout(mainGunId);
                shellSlots = defaultLoadout.ShellSlots;
                deployShellSlots = defaultLoadout.DeployShellSlots;
                curShellId = defaultLoadout.CurShellId;
            }
            else if (!_gameTables.IsShellCompatibleWithGun(mainGunId, curShellId))
            {
                curShellId = GetFirstConfiguredShellId(deployShellSlots);
                if (!_gameTables.IsShellCompatibleWithGun(mainGunId, curShellId))
                {
                    curShellId = GetFirstConfiguredShellId(shellSlots);
                }
            }

            bool changed =
                hullId != tank.HullID ||
                underpanId != tank.UnderpanID ||
                turretId != tank.TurretID ||
                mainGunId != tank.MainGunID ||
                engineId != tank.EngineID ||
                fuelTankId != tank.FueltankID ||
                cartridgeId != tank.CartridgeID ||
                radioId != tank.RadioStationID ||
                gaugeId != tank.GaugeID ||
                tankHp != tank.TankHp ||
                tankCurHp != tank.TankCurHp ||
                tankCurrencyType != tank.TankCurrencyType ||
                !string.Equals(shellSlots, tank.ShellSlots, StringComparison.Ordinal) ||
                !string.Equals(deployShellSlots, tank.DeployShellSlots, StringComparison.Ordinal) ||
                curShellId != tank.CurShellId;

            if (changed)
            {
                UpdateTankEquipment(
                    tank.TankGUID,
                    hullId,
                    underpanId,
                    turretId,
                    mainGunId,
                    engineId,
                    fuelTankId,
                    cartridgeId,
                    radioId,
                    gaugeId,
                    tankHp,
                    tankCurHp,
                    tankCurrencyType,
                    shellSlots,
                    deployShellSlots,
                    curShellId,
                    tx);
            }

            if (tank.TankExp < LocalTestTankExp)
            {
                UpdateTankExp(tank.TankGUID, LocalTestTankExp, tx);
            }

            _moduleRepo?.ResearchTankBaseModules(playerGuid, tank.VehicleId, hullId, underpanId, turretId, mainGunId, engineId, fuelTankId, cartridgeId, radioId, gaugeId, existingModules, tx);
        }

            tx.Commit();
        }
    }

    public void UpdateTankEquipment(
        long tankGuid,
        int hullId,
        int underpanId,
        int turretId,
        int mainGunId,
        int engineId,
        int fueltankId,
        int cartridgeId,
        int radioId,
        int gaugeId,
        int tankHp,
        int tankCurHp,
        byte tankCurrencyType,
        string shellSlots,
        string deployShellSlots,
        int curShellId,
        SqliteTransaction? tx = null)
    {
        using var cmd = _db.Connection.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = @"
            UPDATE Tanks
            SET HullID = @hull,
                UnderpanID = @under,
                TurretID = @turret,
                MainGunID = @gun,
                EngineID = @engine,
                FueltankID = @fuel,
                CartridgeID = @cart,
                RadioStationID = @radio,
                GaugeID = @gauge,
                TankHp = @tankHp,
                TankCurHp = @tankCurHp,
                TankCurrencyType = @currencyType,
                ShellSlots = @shellSlots,
                DeployShellSlots = @deployShellSlots,
                CurShellId = @curShellId
            WHERE TankGUID = @guid";
        cmd.Parameters.AddWithValue("@hull", hullId);
        cmd.Parameters.AddWithValue("@under", underpanId);
        cmd.Parameters.AddWithValue("@turret", turretId);
        cmd.Parameters.AddWithValue("@gun", mainGunId);
        cmd.Parameters.AddWithValue("@engine", engineId);
        cmd.Parameters.AddWithValue("@fuel", fueltankId);
        cmd.Parameters.AddWithValue("@cart", cartridgeId);
        cmd.Parameters.AddWithValue("@radio", radioId);
        cmd.Parameters.AddWithValue("@gauge", gaugeId);
        cmd.Parameters.AddWithValue("@tankHp", tankHp);
        cmd.Parameters.AddWithValue("@tankCurHp", tankCurHp);
        cmd.Parameters.AddWithValue("@currencyType", tankCurrencyType);
        cmd.Parameters.AddWithValue("@shellSlots", shellSlots);
        cmd.Parameters.AddWithValue("@deployShellSlots", deployShellSlots);
        cmd.Parameters.AddWithValue("@curShellId", curShellId);
        cmd.Parameters.AddWithValue("@guid", tankGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateBattleTankList(long playerGuid, long[] tankGuids)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Tanks SET IsBattleTank = 0, BattleSlot = 0 WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.ExecuteNonQuery();

        for (byte i = 0; i < tankGuids.Length; i++)
        {
            if (tankGuids[i] == 0)
            {
                continue;
            }

            using var cmd2 = _db.Connection.CreateCommand();
            cmd2.CommandText = "UPDATE Tanks SET IsBattleTank = 1, BattleSlot = @slot WHERE TankGUID = @tg";
            cmd2.Parameters.AddWithValue("@slot", i);
            cmd2.Parameters.AddWithValue("@tg", tankGuids[i]);
            cmd2.ExecuteNonQuery();
        }
    }

    public void UpdateTankCoat(long tankGuid, int coatId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Tanks SET CurTankCoatId = @coat WHERE TankGUID = @guid";
        cmd.Parameters.AddWithValue("@coat", coatId);
        cmd.Parameters.AddWithValue("@guid", tankGuid);
        cmd.ExecuteNonQuery();
    }

    // v47: применить «модификацию» — подменить модули и записать ModificationId.
    // Нулевые id модулей в модификации означают «оставить как есть» (не трогаем).
    public void UpdateTankModification(long tankGuid, int modificationId,
        int hull, int underpan, int turret, int mainGun, int engine, int radio)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText =
            "UPDATE Tanks SET ModificationId = @mod, " +
            "HullID       = CASE WHEN @hull    > 0 THEN @hull    ELSE HullID       END, " +
            "UnderpanID   = CASE WHEN @under   > 0 THEN @under   ELSE UnderpanID   END, " +
            "TurretID     = CASE WHEN @turret  > 0 THEN @turret  ELSE TurretID     END, " +
            "MainGunID    = CASE WHEN @gun     > 0 THEN @gun     ELSE MainGunID    END, " +
            "EngineID     = CASE WHEN @engine  > 0 THEN @engine  ELSE EngineID     END, " +
            "RadioStationID = CASE WHEN @radio > 0 THEN @radio   ELSE RadioStationID END " +
            "WHERE TankGUID = @guid";
        cmd.Parameters.AddWithValue("@mod", modificationId);
        cmd.Parameters.AddWithValue("@hull", hull);
        cmd.Parameters.AddWithValue("@under", underpan);
        cmd.Parameters.AddWithValue("@turret", turret);
        cmd.Parameters.AddWithValue("@gun", mainGun);
        cmd.Parameters.AddWithValue("@engine", engine);
        cmd.Parameters.AddWithValue("@radio", radio);
        cmd.Parameters.AddWithValue("@guid", tankGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateTankHP(long tankGuid, int curHp)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Tanks SET TankCurHp = @hp WHERE TankGUID = @guid";
        cmd.Parameters.AddWithValue("@hp", curHp);
        cmd.Parameters.AddWithValue("@guid", tankGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateTankExp(long tankGuid, int exp, SqliteTransaction? tx = null)
    {
        using var cmd = _db.Connection.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = "UPDATE Tanks SET TankExp = @exp WHERE TankGUID = @guid";
        cmd.Parameters.AddWithValue("@exp", exp);
        cmd.Parameters.AddWithValue("@guid", tankGuid);
        cmd.ExecuteNonQuery();
    }

    // Раньше HandleBuyTankEmblem/HandleBuyTankSignature только списывали деньги
    // и ничего не сохраняли — эмблема/надпись пропадала бесследно и клиент
    // (Player.OnSynTankEmblemInfo/OnSynTankDecalInfo) никогда не узнавал о ней.
    // Поля EmblemIds/EmblemTimes ("0;0;0;0", 4 слота) и SignatureIds/SignatureTimes
    // ("0;0", 2 слота) уже существовали в модели Tank и уже читались при отправке
    // TANKINFO (FillIntArray/FillLongArray в HallServer.cs) — просто никто в них
    // не писал.
    private static string SetArraySlot(string raw, int slot, long value, int slotCount)
    {
        var parts = (raw ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        var arr = new long[slotCount];
        for (int i = 0; i < Math.Min(slotCount, parts.Length); i++)
            long.TryParse(parts[i], out arr[i]);
        if (slot >= 0 && slot < slotCount) arr[slot] = value;
        return string.Join(';', arr);
    }

    public (string ids, string times) UpdateTankEmblem(long tankGuid, Tank tank, int slot, int emblemId, long emblemTime)
    {
        string newIds = SetArraySlot(tank.EmblemIds, slot, emblemId, 4);
        string newTimes = SetArraySlot(tank.EmblemTimes, slot, emblemTime, 4);
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Tanks SET EmblemIds = @ids, EmblemTimes = @times WHERE TankGUID = @guid";
        cmd.Parameters.AddWithValue("@ids", newIds);
        cmd.Parameters.AddWithValue("@times", newTimes);
        cmd.Parameters.AddWithValue("@guid", tankGuid);
        cmd.ExecuteNonQuery();
        tank.EmblemIds = newIds;
        tank.EmblemTimes = newTimes;
        return (newIds, newTimes);
    }

    public (string ids, string times) UpdateTankSignature(long tankGuid, Tank tank, int pos, int signatureId, long signatureTime)
    {
        string newIds = SetArraySlot(tank.SignatureIds, pos, signatureId, 2);
        string newTimes = SetArraySlot(tank.SignatureTimes, pos, signatureTime, 2);
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Tanks SET SignatureIds = @ids, SignatureTimes = @times WHERE TankGUID = @guid";
        cmd.Parameters.AddWithValue("@ids", newIds);
        cmd.Parameters.AddWithValue("@times", newTimes);
        cmd.Parameters.AddWithValue("@guid", tankGuid);
        cmd.ExecuteNonQuery();
        tank.SignatureIds = newIds;
        tank.SignatureTimes = newTimes;
        return (newIds, newTimes);
    }

    public void EnsureMinimumTankExp(long playerGuid, int minimumExp)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Tanks SET TankExp = @exp WHERE PlayerGUID = @guid AND TankExp < @exp";
        cmd.Parameters.AddWithValue("@exp", minimumExp);
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public bool HasTankByVehicleId(long playerGuid, int vehicleId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM Tanks WHERE PlayerGUID = @guid AND VehicleId = @vid";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.Parameters.AddWithValue("@vid", vehicleId);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// Срок жизни временного (призового) танка в СЕКУНДАХ; 0 — машина бессрочная.
    /// Клиент показывает это как «на N дней» в карточке танка.
    /// </summary>
    public void SetGoldTankLeftTime(long tankGuid, int secondsLeft)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Tanks SET GoldTankLeftTime = @left WHERE TankGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", tankGuid);
        cmd.Parameters.AddWithValue("@left", Math.Max(0, secondsLeft));
        cmd.ExecuteNonQuery();
    }

    public Tank CreateTank(
        long playerGuid,
        int vehicleId,
        int hullId,
        int underpanId,
        int turretId,
        int mainGunId,
        int engineId,
        int fueltankId,
        int cartridgeId,
        int radioId,
        int gaugeId,
            int hp, SqliteTransaction? tx = null)
    {
        long tankGuid = GenerateGUID();
        int carportIndex = GetNextCarportIndex(playerGuid);
        var loadout = _gameTables.BuildDefaultShellLoadout(mainGunId);
        byte tankCurrencyType = ResolveTankCurrencyType(_gameTables.GetTank(vehicleId)?.Currency ?? 1);

        using var cmd = _db.Connection.CreateCommand();
            if (tx != null) cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO Tanks (
                    TankGUID, PlayerGUID, CarportIndex, VehicleId, HullID, UnderpanID, TurretID, MainGunID, EngineID,
                    FueltankID, CartridgeID, RadioStationID, GaugeID, TankHp, TankExp, TankCurHp, IsBattleTank, BattleSlot,
                    TankCurrencyType, ShellSlots, DeployShellSlots, CurShellId)
                VALUES (
                    @tg, @pg, @ci, @vid, @hull, @under, @turret, @gun, @engine,
                    @fuel, @cart, @radio, @gauge, @hp, @tankExp, @hp, 0, 0,
                    @currencyType, @shellSlots, @deployShellSlots, @curShellId)
                RETURNING TankGUID";
            cmd.Parameters.AddWithValue("@tg", tankGuid);
            cmd.Parameters.AddWithValue("@pg", playerGuid);
            cmd.Parameters.AddWithValue("@ci", carportIndex);
            cmd.Parameters.AddWithValue("@vid", vehicleId);
            cmd.Parameters.AddWithValue("@hull", hullId);
            cmd.Parameters.AddWithValue("@under", underpanId);
            cmd.Parameters.AddWithValue("@turret", turretId);
            cmd.Parameters.AddWithValue("@gun", mainGunId);
            cmd.Parameters.AddWithValue("@engine", engineId);
            cmd.Parameters.AddWithValue("@fuel", fueltankId);
            cmd.Parameters.AddWithValue("@cart", cartridgeId);
            cmd.Parameters.AddWithValue("@radio", radioId);
            cmd.Parameters.AddWithValue("@gauge", gaugeId);
            cmd.Parameters.AddWithValue("@hp", hp);
            cmd.Parameters.AddWithValue("@tankExp", LocalTestTankExp);
            cmd.Parameters.AddWithValue("@currencyType", tankCurrencyType);
            cmd.Parameters.AddWithValue("@shellSlots", loadout.ShellSlots);
            cmd.Parameters.AddWithValue("@deployShellSlots", loadout.DeployShellSlots);
            cmd.Parameters.AddWithValue("@curShellId", loadout.CurShellId);
            cmd.ExecuteScalar();
            return GetTankByGUID(tankGuid)!;
        }

    public void RepairTank(long tankGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Tanks SET TankCurHp = TankHp WHERE TankGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", tankGuid);
        cmd.ExecuteNonQuery();
    }

    // Persist everything the СЕРВИС (service) screen can change in one shot: the loaded
    // magazine and the saved loadout ("проект"), the three consumables (active + saved),
    // the auto-repair/auto-refill bitmask, and the current durability. Called by
    // HandleTankRepair so ammo bought / consumables set on that screen actually stick.
    public void UpdateTankServiceState(
        long tankGuid,
        string shellSlots,
        string deployShellSlots,
        string provisions,
        string deployProvisions,
        byte autoRepair,
        int tankCurHp,
        int curShellId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE Tanks
            SET ShellSlots        = @shells,
                DeployShellSlots  = @deployShells,
                Provisions        = @prov,
                DeployProvisions  = @deployProv,
                AutoRepair        = @autoRepair,
                TankCurHp         = @curHp,
                CurShellId        = @curShell
            WHERE TankGUID = @guid";
        cmd.Parameters.AddWithValue("@shells", shellSlots);
        cmd.Parameters.AddWithValue("@deployShells", deployShellSlots);
        cmd.Parameters.AddWithValue("@prov", provisions);
        cmd.Parameters.AddWithValue("@deployProv", deployProvisions);
        cmd.Parameters.AddWithValue("@autoRepair", autoRepair);
        cmd.Parameters.AddWithValue("@curHp", tankCurHp);
        cmd.Parameters.AddWithValue("@curShell", curShellId);
        cmd.Parameters.AddWithValue("@guid", tankGuid);
        cmd.ExecuteNonQuery();
    }

    public void DeleteTank(long tankGuid)
    {
        // v72: Tanks.TankGUID имеет дочерние строки с внешним ключом —
        // TankEquipment.TankGuid и TankCoats.TankGuid (см. DatabaseService.cs).
        // SQLite здесь ЗАФИКСИРОВАННО включает проверку FK (PRAGMA foreign_keys=ON),
        // поэтому прямой DELETE из Tanks падал с «FOREIGN KEY constraint failed»
        // (Error 19), и продажа танка у игрока молча ломалась в этом самом месте —
        // сервер ловил исключение в HandleSellItem/HandleSellTank, а танк оставался
        // висеть непроданным. Чистим дочерние таблицы ПЕРЕД удалением самого танка.
        using (var cmdEquip = _db.Connection.CreateCommand())
        {
            cmdEquip.CommandText = "DELETE FROM TankEquipment WHERE TankGuid = @guid";
            cmdEquip.Parameters.AddWithValue("@guid", tankGuid);
            cmdEquip.ExecuteNonQuery();
        }
        using (var cmdCoat = _db.Connection.CreateCommand())
        {
            cmdCoat.CommandText = "DELETE FROM TankCoats WHERE TankGuid = @guid";
            cmdCoat.Parameters.AddWithValue("@guid", tankGuid);
            cmdCoat.ExecuteNonQuery();
        }
        // Crew.TankGuid не имеет FK-констрейнта (см. DatabaseService.cs), но экипаж,
        // всё ещё «сидящий» на удалённом танке, — фантомная ссылка. HandleSellTank уже
        // умеет явно освобождать экипаж по флагу 0x02 (UpdateCrewTank(..., 0)) до вызова
        // DeleteTank; на случай, если экипаж всё же остался прописан на этот танк
        // (например, флаг не пришёл), обнуляем его тут же — так экипаж просто
        // возвращается «в казарму» вместо ссылки на несуществующий танк.
        using (var cmdCrew = _db.Connection.CreateCommand())
        {
            cmdCrew.CommandText = "UPDATE Crew SET TankGuid = 0, IsEquipped = 0 WHERE TankGuid = @guid";
            cmdCrew.Parameters.AddWithValue("@guid", tankGuid);
            cmdCrew.ExecuteNonQuery();
        }

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Tanks WHERE TankGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", tankGuid);
        cmd.ExecuteNonQuery();
        Console.WriteLine($"[TankRepo] Deleted tank GUID={tankGuid}");
    }

    private Tank InsertTank(long playerGuid, TankEntry tankEntry, bool isBattleTank, SqliteTransaction? tx = null)
    {
        long tankGuid = GenerateGUID();
        int carportIndex = GetNextCarportIndex(playerGuid);
        var loadout = _gameTables.BuildDefaultShellLoadout(tankEntry.MainGunId);
        byte tankCurrencyType = ResolveTankCurrencyType(tankEntry.Currency);

        using var cmd = _db.Connection.CreateCommand();
            if (tx != null) cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO Tanks (
                    TankGUID, PlayerGUID, CarportIndex, VehicleId, HullID, UnderpanID, TurretID, MainGunID, EngineID,
                    FueltankID, CartridgeID, RadioStationID, GaugeID, TankHp, TankExp, TankCurHp, IsBattleTank, BattleSlot,
                    TankCurrencyType, ShellSlots, DeployShellSlots, CurShellId)
                VALUES (
                    @tg, @pg, @ci, @vid, @hull, @under, @turret, @gun, @engine,
                    @fuel, @cart, @radio, @gauge, @hp, @tankExp, @hp, @isBattleTank, @battleSlot,
                    @currencyType, @shellSlots, @deployShellSlots, @curShellId)";
            cmd.Parameters.AddWithValue("@tg", tankGuid);
            cmd.Parameters.AddWithValue("@pg", playerGuid);
            cmd.Parameters.AddWithValue("@ci", carportIndex);
            cmd.Parameters.AddWithValue("@vid", tankEntry.Id);
            cmd.Parameters.AddWithValue("@hull", tankEntry.HullId);
            cmd.Parameters.AddWithValue("@under", tankEntry.UnderpanId);
            cmd.Parameters.AddWithValue("@turret", tankEntry.TurretId);
            cmd.Parameters.AddWithValue("@gun", tankEntry.MainGunId);
            cmd.Parameters.AddWithValue("@engine", tankEntry.EngineId);
            cmd.Parameters.AddWithValue("@fuel", tankEntry.FueltankId);
            cmd.Parameters.AddWithValue("@cart", tankEntry.CartridgeId);
            cmd.Parameters.AddWithValue("@radio", tankEntry.RadioStationId);
            cmd.Parameters.AddWithValue("@gauge", tankEntry.GaugeId);
            cmd.Parameters.AddWithValue("@hp", Math.Max(1, tankEntry.MaxHp));
            cmd.Parameters.AddWithValue("@tankExp", LocalTestTankExp);
            cmd.Parameters.AddWithValue("@isBattleTank", isBattleTank ? 1 : 0);
            cmd.Parameters.AddWithValue("@battleSlot", isBattleTank ? 0 : 0);
            cmd.Parameters.AddWithValue("@currencyType", tankCurrencyType);
            cmd.Parameters.AddWithValue("@shellSlots", loadout.ShellSlots);
            cmd.Parameters.AddWithValue("@deployShellSlots", loadout.DeployShellSlots);
            cmd.Parameters.AddWithValue("@curShellId", loadout.CurShellId);
            cmd.ExecuteNonQuery();

            return GetTankByGUID(tankGuid)!;
        }

    private void ResearchBaseModules(long playerGuid, Tank tank)
    {
        _moduleRepo?.ResearchTankBaseModules(
            playerGuid,
            tank.VehicleId,
            tank.HullID,
            tank.UnderpanID,
            tank.TurretID,
            tank.MainGunID,
            tank.EngineID,
            tank.FueltankID,
            tank.CartridgeID,
            tank.RadioStationID,
            tank.GaugeID);
    }

    private int GetNextCarportIndex(long playerGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(CarportIndex), -1) + 1 FROM Tanks WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private bool HasCompatibleShellLoadout(int gunId, string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        bool hasAnyShell = false;
        foreach (var slot in rawValue.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = slot.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            if (!int.TryParse(parts[0], out int shellId) || !int.TryParse(parts[1], out int count))
            {
                continue;
            }

            if (shellId <= 0 || count <= 0)
            {
                continue;
            }

            hasAnyShell = true;
            if (!_gameTables.IsShellCompatibleWithGun(gunId, shellId))
            {
                return false;
            }
        }

        return hasAnyShell;
    }

    private static int GetFirstConfiguredShellId(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return 0;
        }

        foreach (var slot in rawValue.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = slot.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            if (int.TryParse(parts[0], out int shellId) &&
                int.TryParse(parts[1], out int count) &&
                shellId > 0 &&
                count > 0)
            {
                return shellId;
            }
        }

        return 0;
    }

    private static int NormalizeCurrentHp(int oldMaxHp, int oldCurHp, int newMaxHp)
    {
        if (newMaxHp <= 0)
        {
            return 1;
        }

        if (oldMaxHp <= 0)
        {
            return Math.Clamp(oldCurHp, 0, newMaxHp);
        }

        double ratio = Math.Clamp((double)oldCurHp / oldMaxHp, 0d, 1d);
        int normalized = (int)Math.Round(ratio * newMaxHp);
        if (oldCurHp > 0 && normalized == 0)
        {
            normalized = 1;
        }

        return Math.Clamp(normalized, 0, newMaxHp);
    }

    private static byte ResolveTankCurrencyType(int currency) => currency == 2 ? (byte)2 : (byte)0;

    private static Tank MapTank(SqliteDataReader reader)
    {
        return new Tank
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            TankGUID = reader.GetInt64(reader.GetOrdinal("TankGUID")),
            PlayerGUID = reader.GetInt64(reader.GetOrdinal("PlayerGUID")),
            CarportIndex = reader.GetInt32(reader.GetOrdinal("CarportIndex")),
            VehicleId = reader.GetInt32(reader.GetOrdinal("VehicleId")),
            HullID = reader.GetInt32(reader.GetOrdinal("HullID")),
            UnderpanID = reader.GetInt32(reader.GetOrdinal("UnderpanID")),
            TurretID = reader.GetInt32(reader.GetOrdinal("TurretID")),
            MainGunID = reader.GetInt32(reader.GetOrdinal("MainGunID")),
            EngineID = reader.GetInt32(reader.GetOrdinal("EngineID")),
            FueltankID = reader.GetInt32(reader.GetOrdinal("FueltankID")),
            CartridgeID = reader.GetInt32(reader.GetOrdinal("CartridgeID")),
            RadioStationID = reader.GetInt32(reader.GetOrdinal("RadioStationID")),
            GaugeID = reader.GetInt32(reader.GetOrdinal("GaugeID")),
            TankHp = reader.GetInt32(reader.GetOrdinal("TankHp")),
            TankExp = reader.GetInt32(reader.GetOrdinal("TankExp")),
            TankCurHp = reader.GetInt32(reader.GetOrdinal("TankCurHp")),
            AutoRepair = reader.GetByte(reader.GetOrdinal("AutoRepair")),
            TankCurrencyType = reader.GetByte(reader.GetOrdinal("TankCurrencyType")),
            GoldTankLeftTime = reader.GetInt32(reader.GetOrdinal("GoldTankLeftTime")),
            CurTankCoatId = reader.GetInt32(reader.GetOrdinal("CurTankCoatId")),
            CurRoomTankCoatID = reader.GetInt32(reader.GetOrdinal("CurRoomTankCoatID")),
            CurShellId = reader.GetInt32(reader.GetOrdinal("CurShellId")),
            ModificationId = reader.GetInt32(reader.GetOrdinal("ModificationId")),
            IsElite = reader.GetInt32(reader.GetOrdinal("IsElite")) == 1,
            AutoFillCostFlag = reader.GetByte(reader.GetOrdinal("AutoFillCostFlag")),
            ShellSlots = reader.GetString(reader.GetOrdinal("ShellSlots")),
            DeployShellSlots = reader.GetString(reader.GetOrdinal("DeployShellSlots")),
            Provisions = reader.GetString(reader.GetOrdinal("Provisions")),
            DeployProvisions = reader.GetString(reader.GetOrdinal("DeployProvisions")),
            SignatureIds = reader.GetString(reader.GetOrdinal("SignatureIds")),
            SignatureTimes = reader.GetString(reader.GetOrdinal("SignatureTimes")),
            EmblemIds = reader.GetString(reader.GetOrdinal("EmblemIds")),
            EmblemTimes = reader.GetString(reader.GetOrdinal("EmblemTimes")),
            BehaveArray = reader.GetString(reader.GetOrdinal("BehaveArray")),
            IsBattleTank = reader.GetInt32(reader.GetOrdinal("IsBattleTank")) == 1,
            BattleSlot = reader.GetByte(reader.GetOrdinal("BattleSlot"))
        };
    }

    private static long GenerateGUID()
    {
        var bytes = new byte[8];
        new Random().NextBytes(bytes);
        return Math.Abs(BitConverter.ToInt64(bytes, 0));
    }
}
