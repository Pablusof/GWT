using System;
using Microsoft.Data.Sqlite;

namespace GwtEmulator.Server.Database;

public class DatabaseService : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _connectionString;

    public DatabaseService(string dbPath = "gwt_server.db")
    {
        _connectionString = $"Data Source={dbPath}";
        _connection = new SqliteConnection(_connectionString);
        _connection.Open();
        InitializeTables();
    }

    public SqliteConnection Connection => _connection;

    private void InitializeTables()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;

            CREATE TABLE IF NOT EXISTS Accounts (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Username TEXT UNIQUE NOT NULL,
                Password TEXT NOT NULL,
                PlayerGUID INTEGER UNIQUE NOT NULL,
                PlayerName TEXT NOT NULL,
                Silver INTEGER DEFAULT 50000,
                Gold INTEGER DEFAULT 1000,
                Experience INTEGER DEFAULT 0,
                Level INTEGER DEFAULT 1,
                BattleCap INTEGER DEFAULT 0,
                CasernCapacity INTEGER DEFAULT 100,
                GarageBackground INTEGER DEFAULT 10001,
                ChannelId INTEGER DEFAULT 1,
                CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP,
                LastLogin TEXT DEFAULT CURRENT_TIMESTAMP,
                ContinuousLoginDays INTEGER DEFAULT 0,
                MonthlyLoginDay INTEGER DEFAULT 0,
                IsVip INTEGER DEFAULT 0,
                VipExpireDate TEXT DEFAULT '',
                IsPremium INTEGER DEFAULT 0,
                PremiumExpireDate TEXT DEFAULT '',
                GuideStates INTEGER DEFAULT 0,
                MissionData TEXT DEFAULT '0,0,0,0,0,0',
                ShootTimesLvl1 INTEGER DEFAULT 0,
                ShootTimesLvl2 INTEGER DEFAULT 0,
                ShootTimesLvl3 INTEGER DEFAULT 0,
                StandaloneRewardClaimed INTEGER DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS Tanks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TankGUID INTEGER UNIQUE NOT NULL,
                PlayerGUID INTEGER NOT NULL,
                CarportIndex INTEGER DEFAULT 0,
                VehicleId INTEGER DEFAULT 0,
                HullID INTEGER DEFAULT 0,
                UnderpanID INTEGER DEFAULT 0,
                TurretID INTEGER DEFAULT 0,
                MainGunID INTEGER DEFAULT 0,
                EngineID INTEGER DEFAULT 0,
                FueltankID INTEGER DEFAULT 0,
                CartridgeID INTEGER DEFAULT 0,
                RadioStationID INTEGER DEFAULT 0,
                GaugeID INTEGER DEFAULT 0,
                TankHp INTEGER DEFAULT 1000,
                TankExp INTEGER DEFAULT 0,
                TankCurHp INTEGER DEFAULT 1000,
                AutoRepair INTEGER DEFAULT 1,
                TankCurrencyType INTEGER DEFAULT 0,
                GoldTankLeftTime INTEGER DEFAULT 0,
                CurTankCoatId INTEGER DEFAULT 0,
                CurRoomTankCoatID INTEGER DEFAULT 0,
                CurShellId INTEGER DEFAULT 0,
                ModificationId INTEGER DEFAULT 0,
                IsElite INTEGER DEFAULT 0,
                AutoFillCostFlag INTEGER DEFAULT 0,
                ShellSlots TEXT DEFAULT '1,10;1,10;1,10',
                DeployShellSlots TEXT DEFAULT '0,0;0,0;0,0',
                Provisions TEXT DEFAULT '0;0;0',
                DeployProvisions TEXT DEFAULT '0;0;0',
                SignatureIds TEXT DEFAULT '0;0',
                SignatureTimes TEXT DEFAULT '0;0',
                EmblemIds TEXT DEFAULT '0;0;0;0',
                EmblemTimes TEXT DEFAULT '0;0;0;0',
                BehaveArray TEXT DEFAULT '0,0,1;0,0,1;0,0,1;0,0,1;0,0,1',
                IsBattleTank INTEGER DEFAULT 0,
                BattleSlot INTEGER DEFAULT 0,
                FOREIGN KEY (PlayerGUID) REFERENCES Accounts(PlayerGUID)
            );

            CREATE TABLE IF NOT EXISTS Inventory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PlayerGUID INTEGER NOT NULL,
                ItemType INTEGER NOT NULL,
                ItemId INTEGER NOT NULL,
                ItemNum INTEGER DEFAULT 0,
                ItemFlag INTEGER DEFAULT 0,
                FOREIGN KEY (PlayerGUID) REFERENCES Accounts(PlayerGUID)
            );

            CREATE TABLE IF NOT EXISTS TankEquipment (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TankGuid INTEGER NOT NULL,
                SlotIndex INTEGER NOT NULL,
                EquipmentId INTEGER NOT NULL,
                FOREIGN KEY (TankGuid) REFERENCES Tanks(TankGUID)
            );

            CREATE TABLE IF NOT EXISTS Crew (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CrewGuid INTEGER UNIQUE NOT NULL,
                PlayerGUID INTEGER NOT NULL,
                TankGuid INTEGER DEFAULT 0,
                CrewId INTEGER NOT NULL,
                FamilyNameId INTEGER DEFAULT 0,
                NameId INTEGER DEFAULT 0,
                BigIconId INTEGER DEFAULT 0,
                SmallIconId INTEGER DEFAULT 0,
                ResumeId INTEGER DEFAULT 0,
                CurExp INTEGER DEFAULT 0,
                CurLv INTEGER DEFAULT 1,
                Lastlearn INTEGER DEFAULT 0,
                LastSkillExp INTEGER DEFAULT 0,
                LastExp INTEGER DEFAULT 0,
                LastLv INTEGER DEFAULT 1,
                SkillIds TEXT DEFAULT '',
                IsEquipped INTEGER DEFAULT 0,
                FOREIGN KEY (PlayerGUID) REFERENCES Accounts(PlayerGUID)
            );

            CREATE TABLE IF NOT EXISTS Corps (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CorpsGUID INTEGER UNIQUE NOT NULL,
                CorpsName TEXT UNIQUE NOT NULL,
                CorpsTag TEXT DEFAULT '',
                TipName TEXT DEFAULT '',
                Introduce TEXT DEFAULT '',
                Notice TEXT DEFAULT '',
                LeaderGUID INTEGER NOT NULL,
                LeaderName TEXT DEFAULT '',
                CorpsLevel INTEGER DEFAULT 1,
                State INTEGER DEFAULT 0,
                TaxRate INTEGER DEFAULT 0,
                MaxMemberCount INTEGER DEFAULT 20,
                BattleCap INTEGER DEFAULT 0,
                Money INTEGER DEFAULT 0,
                CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS CorpsMembers (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CorpsGUID INTEGER NOT NULL,
                PlayerGUID INTEGER NOT NULL,
                PlayerName TEXT DEFAULT '',
                Job INTEGER DEFAULT 0,
                LoyalPoint INTEGER DEFAULT 0,
                BattleCap INTEGER DEFAULT 0,
                JoinDate TEXT DEFAULT CURRENT_TIMESTAMP,
                LastLoginDate TEXT DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY (CorpsGUID) REFERENCES Corps(CorpsGUID),
                FOREIGN KEY (PlayerGUID) REFERENCES Accounts(PlayerGUID)
            );

            CREATE TABLE IF NOT EXISTS CorpsApplications (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CorpsGUID INTEGER NOT NULL,
                PlayerGUID INTEGER NOT NULL,
                PlayerName TEXT DEFAULT '',
                ApplyTime TEXT DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY (CorpsGUID) REFERENCES Corps(CorpsGUID),
                FOREIGN KEY (PlayerGUID) REFERENCES Accounts(PlayerGUID)
            );

            CREATE TABLE IF NOT EXISTS Friends (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PlayerGUID INTEGER NOT NULL,
                FriendGUID INTEGER NOT NULL,
                FriendName TEXT DEFAULT '',
                IsOnline INTEGER DEFAULT 0,
                FriendLevel INTEGER DEFAULT 1,
                RelationType INTEGER DEFAULT 0,
                FOREIGN KEY (PlayerGUID) REFERENCES Accounts(PlayerGUID)
            );

            CREATE TABLE IF NOT EXISTS BattleStats (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PlayerGUID INTEGER NOT NULL,
                TankGUID INTEGER NOT NULL,
                KillNum INTEGER DEFAULT 0,
                DeathNum INTEGER DEFAULT 0,
                Score INTEGER DEFAULT 0,
                Damage INTEGER DEFAULT 0,
                ShootNum INTEGER DEFAULT 0,
                HitNum INTEGER DEFAULT 0,
                BattleCount INTEGER DEFAULT 0,
                WinCount INTEGER DEFAULT 0,
                FOREIGN KEY (PlayerGUID) REFERENCES Accounts(PlayerGUID)
            );

            CREATE TABLE IF NOT EXISTS ModeStats (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PlayerGUID INTEGER NOT NULL,
                GameMode INTEGER NOT NULL,
                KillNum INTEGER DEFAULT 0,
                DeathNum INTEGER DEFAULT 0,
                Damage INTEGER DEFAULT 0,
                ShootNum INTEGER DEFAULT 0,
                HitNum INTEGER DEFAULT 0,
                BattleCount INTEGER DEFAULT 0,
                WinCount INTEGER DEFAULT 0,
                ExpTotal INTEGER DEFAULT 0,
                ScoreTotal INTEGER DEFAULT 0,
                MaxKillInBattle INTEGER DEFAULT 0,
                MaxExpInBattle INTEGER DEFAULT 0,
                MaxScoreInBattle INTEGER DEFAULT 0,
                FOREIGN KEY (PlayerGUID) REFERENCES Accounts(PlayerGUID)
            );

            CREATE TABLE IF NOT EXISTS Tasks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PlayerGUID INTEGER NOT NULL,
                TaskId INTEGER NOT NULL,
                Progress INTEGER DEFAULT 0,
                IsCompleted INTEGER DEFAULT 0,
                IsClaimed INTEGER DEFAULT 0,
                FOREIGN KEY (PlayerGUID) REFERENCES Accounts(PlayerGUID)
            );

            CREATE TABLE IF NOT EXISTS TournamentData (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PlayerGUID INTEGER UNIQUE NOT NULL,
                IsRegistered INTEGER DEFAULT 0,
                TotalDamage INTEGER DEFAULT 0,
                VictoryTime INTEGER DEFAULT 0,
                TotalBattleTime INTEGER DEFAULT 0,
                MaxDamage INTEGER DEFAULT 0,
                RewardClaimed INTEGER DEFAULT 0,
                FOREIGN KEY (PlayerGUID) REFERENCES Accounts(PlayerGUID)
            );

            CREATE TABLE IF NOT EXISTS ResearchedModules (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PlayerGUID INTEGER NOT NULL,
                VehicleId INTEGER NOT NULL,
                ModuleType INTEGER NOT NULL,
                ModuleId INTEGER NOT NULL,
                FOREIGN KEY (PlayerGUID) REFERENCES Accounts(PlayerGUID)
            );

            CREATE TABLE IF NOT EXISTS TankCoats (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TankGuid INTEGER NOT NULL,
                SlotIndex INTEGER NOT NULL,
                CoatTableId INTEGER DEFAULT 0,
                OverdueUnix INTEGER DEFAULT 0,
                FOREIGN KEY (TankGuid) REFERENCES Tanks(TankGUID)
            );

            CREATE INDEX IF NOT EXISTS idx_tanks_player ON Tanks(PlayerGUID);
            CREATE INDEX IF NOT EXISTS idx_inventory_player ON Inventory(PlayerGUID);
            CREATE INDEX IF NOT EXISTS idx_tank_equipment_tank ON TankEquipment(TankGuid);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_tank_equipment_slot ON TankEquipment(TankGuid, SlotIndex);
            CREATE INDEX IF NOT EXISTS idx_tank_coats_tank ON TankCoats(TankGuid);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_tank_coats_slot ON TankCoats(TankGuid, SlotIndex);
            CREATE INDEX IF NOT EXISTS idx_crew_player ON Crew(PlayerGUID);
            CREATE INDEX IF NOT EXISTS idx_corps_members ON CorpsMembers(CorpsGUID);
            CREATE INDEX IF NOT EXISTS idx_friends_player ON Friends(PlayerGUID);
            CREATE INDEX IF NOT EXISTS idx_battle_stats_player ON BattleStats(PlayerGUID);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_mode_stats_player_mode ON ModeStats(PlayerGUID, GameMode);
            CREATE INDEX IF NOT EXISTS idx_researched_modules_player ON ResearchedModules(PlayerGUID);
        ";
        cmd.ExecuteNonQuery();
        using (var dedupeModules = _connection.CreateCommand())
        {
            dedupeModules.CommandText = @"
                DELETE FROM ResearchedModules
                WHERE Id NOT IN (
                    SELECT MIN(Id)
                    FROM ResearchedModules
                    GROUP BY PlayerGUID, VehicleId, ModuleType, ModuleId
                )";
            dedupeModules.ExecuteNonQuery();
        }
        using (var uniqueModules = _connection.CreateCommand())
        {
            uniqueModules.CommandText = @"
                CREATE UNIQUE INDEX IF NOT EXISTS idx_researched_modules_unique
                ON ResearchedModules(PlayerGUID, VehicleId, ModuleType, ModuleId)";
            uniqueModules.ExecuteNonQuery();
        }
        try { using var alter1 = _connection.CreateCommand(); alter1.CommandText = "ALTER TABLE Accounts ADD COLUMN GuideStates INTEGER DEFAULT 0"; alter1.ExecuteNonQuery(); } catch { }
        try { using var alter2 = _connection.CreateCommand(); alter2.CommandText = "ALTER TABLE Accounts ADD COLUMN MissionData TEXT DEFAULT '0,0,0,0,0,0'"; alter2.ExecuteNonQuery(); } catch { }
        try { using var alter3 = _connection.CreateCommand(); alter3.CommandText = "ALTER TABLE Accounts ADD COLUMN CasernCapacity INTEGER DEFAULT 100"; alter3.ExecuteNonQuery(); } catch { }
        // v30 (камуфляжи): какой из 3 слотов надет сейчас (EnTankCoatType: 0..2, 3 = None).
        // Отдельная колонка, а не вывод из CurTankCoatId — иначе снятый камуфляж
        // (CurSelectType = None при непустых слотах) неотличим от пустого гаража.
        try { using var alter4 = _connection.CreateCommand(); alter4.CommandText = "ALTER TABLE Tanks ADD COLUMN CurCoatSlot INTEGER DEFAULT 3"; alter4.ExecuteNonQuery(); } catch { }
        // v77: копится суммарный опыт по танку — раньше BattleStats не хранил Exp вообще,
        // из-за чего окно «Статистика танка/аккаунта» после боя оставалось нулевым
        // (сервер отправлял хардкод из HandleHallLogin, а не реальные данные из БД).
        try { using var alter5 = _connection.CreateCommand(); alter5.CommandText = "ALTER TABLE BattleStats ADD COLUMN ExpTotal INTEGER DEFAULT 0"; alter5.ExecuteNonQuery(); } catch { }
        try { using var alter6 = _connection.CreateCommand(); alter6.CommandText = "ALTER TABLE BattleStats ADD COLUMN MaxKillInBattle INTEGER DEFAULT 0"; alter6.ExecuteNonQuery(); } catch { }
        try { using var alter7 = _connection.CreateCommand(); alter7.CommandText = "ALTER TABLE BattleStats ADD COLUMN MaxExpInBattle INTEGER DEFAULT 0"; alter7.ExecuteNonQuery(); } catch { }
        // v.fix (Тир/треасур-бокс): сколько купленных выстрелов осталось на каждом из 3
        // уровней тира. Раньше сервер вообще не хранил это и не слал NM_HC_RemainShootTimes,
        // поэтому клиент (NewTreasureBoxData.RemainShellShootNum) всегда показывал 0, а
        // покупка выстрелов (NM_CH_BuyBox) не имела видимого эффекта.
        try { using var alter8 = _connection.CreateCommand(); alter8.CommandText = "ALTER TABLE Accounts ADD COLUMN ShootTimesLvl1 INTEGER DEFAULT 0"; alter8.ExecuteNonQuery(); } catch { }
        try { using var alter9 = _connection.CreateCommand(); alter9.CommandText = "ALTER TABLE Accounts ADD COLUMN ShootTimesLvl2 INTEGER DEFAULT 0"; alter9.ExecuteNonQuery(); } catch { }
        try { using var alter10 = _connection.CreateCommand(); alter10.CommandText = "ALTER TABLE Accounts ADD COLUMN ShootTimesLvl3 INTEGER DEFAULT 0"; alter10.ExecuteNonQuery(); } catch { }
        // v.fix (награда за скачивание/первый вход): раньше сервер всегда отвечал
        // HasReward=0 на NM_CH_AskStandaloneLoginReward — награда не выдавалась никогда.
        // Флаг "уже забрал" нужен, чтобы не выдавать награду повторно на каждый клик.
        try { using var alter11 = _connection.CreateCommand(); alter11.CommandText = "ALTER TABLE Accounts ADD COLUMN StandaloneRewardClaimed INTEGER DEFAULT 0"; alter11.ExecuteNonQuery(); } catch { }
        // Самолечение: раньше GarageBackground хранился/читался как byte, а реальные id
        // фонов ангара — 10001..10005 (клиент, AdvancedAccountManager._realGarageImgIndex) —
        // не влезают в byte, из-за чего в старых БД там осело обрезанное (испорченное)
        // число. Сбрасываем такие записи на дефолтный первый фон.
        try
        {
            using var fixGarage = _connection.CreateCommand();
            fixGarage.CommandText = "UPDATE Accounts SET GarageBackground = 10001 WHERE GarageBackground NOT IN (10001,10002,10003,10004,10005)";
            int fixedRows = fixGarage.ExecuteNonQuery();
            if (fixedRows > 0) Console.WriteLine($"[DB] Исправлено испорченных GarageBackground: {fixedRows}");
        }
        catch { }
        // Самолечение: раньше LocalTestTankExp=9_999_999 принудительно накачивал опыт
        // КАЖДОЙ уже существующей машины почти до 10 млн (при каждом логине и каждом
        // запросе исследования) — из-за этого исследования всегда списывались с
        // TankExp, а свободный опыт аккаунта не расходовался. Сбрасываем это
        // искусственное значение, чтобы механика пошла как задумано — из свободного
        // опыта. Порог 1 000 000 отличает явно «тестовую» накачку от честно
        // накопленного небольшого опыта.
        try
        {
            using var fixTankExp = _connection.CreateCommand();
            fixTankExp.CommandText = "UPDATE Tanks SET TankExp = 0 WHERE TankExp >= 1000000";
            int fixedTanks = fixTankExp.ExecuteNonQuery();
            if (fixedTanks > 0) Console.WriteLine($"[DB] Сброшен искусственно накачанный TankExp у {fixedTanks} машин(ы)");
        }
        catch { }
        Console.WriteLine("[DB] Database initialized successfully");
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
    }
}
