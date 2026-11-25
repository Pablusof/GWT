using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using GwtEmulator.Server.Database.Models;

namespace GwtEmulator.Server.Database.Repositories;

public class AccountRepository
{
    public const int LocalTestSilver = 500_000_000;
    public const int LocalTestGold = 500_000_000;
    public const int LocalTestPlayerExp = 500_000_000;
    public const int LocalTestLevel = 99;
    // ВАЖНО: не ставить сюда 85 (== CarportSlotCapMax, потолок клиента)!
    // На клиенте есть два независимых места, которые жёстко сравнивают
    // GameManager.Player.CarportNum именно со значением 85 (а не "нет свободных
    // слотов"):
    //   1) TankSelectList.OnClickBuyPort(): if (CarportNum < 85) {...} else OnCarPortMax();
    //      — блокирует ПОКУПКУ НОВОГО СЛОТА АНГАРА, если CarportNum уже 85.
    //   2) GoldTankControl (покупка танка): else if (CarportNum == 85) { PortNumIsMax }
    //      — блокирует ПОКУПКУ ЛЮБОГО ТАНКА без галочки "докупить слот", даже если
    //      свободные слоты реально есть (проверяется точное равенство 85, а не
    //      CarportNum - HallTanks.Count > 0).
    // Если новый аккаунт стартует сразу с CasernCapacity=85, обе покупки
    // "вылетают с ошибкой" (на самом деле — с диалогом "слоты закончились")
    // с самого первого запуска. Поэтому берём заведомо безопасное значение
    // ниже потолка.
    public const int LocalTestCasernCapacity = 50;
    // ВАЖНО: не отправлять клиенту long.MaxValue ("все 63 шага обучения пройдены").
    // Клиентский HallGuideManager.HallAwake() присваивает поле hallGuideHelper
    // ТОЛЬКО если хотя бы один guide-бит ещё не завершён (_hasUnfinished == true).
    // Если все биты выставлены, hallGuideHelper остаётся null, а
    // HallGuideTools.CancelHighlightButton()/DeleteMessage() всё равно вызываются
    // при каждом открытии/закрытии окна поверх Ангара (в т.ч. при покупке танка
    // и покупке слота ангара) и падают с NullReferenceException, обрывая цепочку
    // остальных подписчиков события — из-за этого покупка визуально "не работает"/
    // "вылетает ошибка".
    // Снимаем ровно один бит — индекс 62 (EnumHallGuide.FirstLogin), у него нет
    // зарегистрированного обработчика в HallGuideManager (_listHandlers[62] == null),
    // поэтому никакой видимой обучающей подсказки не появится, но _hasUnfinished
    // станет true и hallGuideHelper будет корректно проинициализирован.
    public const long LocalTestGuideStates = long.MaxValue & ~(1L << 62);
    public const string LocalTestMissionData = "0,0,0,0,0,0";

    private readonly DatabaseService _db;

    public AccountRepository(DatabaseService db) => _db = db;

    public Account? GetByUsername(string username)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Accounts WHERE Username = @username";
        cmd.Parameters.AddWithValue("@username", username);
        using var reader = cmd.ExecuteReader();
        if (reader.Read()) return MapAccount(reader);
        return null;
    }

    public Account? FindByPlayerName(string playerName)
    {
        using var cmd = _db.Connection.CreateCommand();
        if (!string.IsNullOrWhiteSpace(playerName))
        {
            var trimmed = playerName.Trim();
            cmd.CommandText = "SELECT * FROM Accounts WHERE LOWER(PlayerName) = LOWER(@name) OR LOWER(Username) = LOWER(@name) LIMIT 1";
            cmd.Parameters.AddWithValue("@name", trimmed);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? MapAccount(reader) : null;
        }

        // Empty query fallback only: return first account
        cmd.CommandText = "SELECT * FROM Accounts ORDER BY LastLogin DESC LIMIT 1";
        using var r2 = cmd.ExecuteReader();
        return r2.Read() ? MapAccount(r2) : null;
    }

    public List<Account> GetAllAccounts()
    {
        var list = new List<Account>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Accounts ORDER BY LastLogin DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(MapAccount(reader));
        return list;
    }

    public List<Account> SearchAccountsByName(string query, int limit = 20)
    {
        var list = new List<Account>();
        using var cmd = _db.Connection.CreateCommand();
        string trimmed = (query ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            cmd.CommandText = "SELECT * FROM Accounts ORDER BY LastLogin DESC LIMIT @limit";
        }
        else
        {
            cmd.CommandText = @"
                SELECT * FROM Accounts
                WHERE LOWER(PlayerName) LIKE LOWER(@q) OR LOWER(Username) LIKE LOWER(@q)
                ORDER BY CASE WHEN LOWER(PlayerName) = LOWER(@exact) THEN 0 ELSE 1 END, LastLogin DESC
                LIMIT @limit";
            cmd.Parameters.AddWithValue("@q", $"%{trimmed}%");
            cmd.Parameters.AddWithValue("@exact", trimmed);
        }
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 50));
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(MapAccount(reader));
        return list;
    }

    public string? GetNicknameByPersId(string persId)
    {
        if (string.IsNullOrWhiteSpace(persId)) return null;
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT PlayerName FROM PlayerAliases WHERE PersId = @persId LIMIT 1";
        cmd.Parameters.AddWithValue("@persId", persId.Trim());
        var res = cmd.ExecuteScalar();
        return res == null || res == DBNull.Value ? null : res.ToString();
    }

    public static string SanitizeCustomNickname(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var chars = raw.Trim()
            .Where(c => !char.IsControl(c) && c != '&' && c != '=' && c != '?' && c != '"' && c != '<' && c != '>' && c != '/')
            .ToArray();
        var s = new string(chars).Trim();
        return s.Length > 18 ? s[..18] : s;
    }

    public bool SetNicknameForPersId(string persId, string requestedNick, out string error)
    {
        error = string.Empty;
        string cleanPersId = (persId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(cleanPersId))
        {
            error = "Пустой AccountId";
            return false;
        }

        string cleanNick = SanitizeCustomNickname(requestedNick);
        if (cleanNick.Length < 2)
        {
            error = "Никнейм должен содержать минимум 2 символа";
            return false;
        }

        string? prevNick = GetNicknameByPersId(cleanPersId);
        string defaultUsername = "Player_" + cleanPersId;

        Account? existingForThisPers =
            (!string.IsNullOrWhiteSpace(prevNick) ? GetByUsername(prevNick) ?? FindByPlayerName(prevNick) : null)
            ?? GetByUsername(defaultUsername)
            ?? FindByPlayerName(defaultUsername);

        // Ensure target nick is not owned by a DIFFERENT account
        var ownerOfTargetNick = FindByPlayerName(cleanNick);
        if (ownerOfTargetNick != null &&
            (existingForThisPers == null || ownerOfTargetNick.PlayerGUID != existingForThisPers.PlayerGUID))
        {
            // Also check if an unlogged PlayerAlias reserved it for a different persId
            error = $"Ник «{cleanNick}» уже занят другим игроком";
            return false;
        }

        using var checkAlias = _db.Connection.CreateCommand();
        checkAlias.CommandText = "SELECT PersId FROM PlayerAliases WHERE LOWER(PlayerName) = LOWER(@n) AND PersId <> @p LIMIT 1";
        checkAlias.Parameters.AddWithValue("@n", cleanNick);
        checkAlias.Parameters.AddWithValue("@p", cleanPersId);
        if (checkAlias.ExecuteScalar() != null)
        {
            error = $"Ник «{cleanNick}» уже занят другим игроком";
            return false;
        }

        if (existingForThisPers != null)
        {
            using var upd = _db.Connection.CreateCommand();
            upd.CommandText = "UPDATE Accounts SET Username = @n, PlayerName = @n WHERE PlayerGUID = @guid";
            upd.Parameters.AddWithValue("@n", cleanNick);
            upd.Parameters.AddWithValue("@guid", existingForThisPers.PlayerGUID);
            upd.ExecuteNonQuery();
        }

        using var upsert = _db.Connection.CreateCommand();
        upsert.CommandText = @"
            INSERT INTO PlayerAliases (PersId, PlayerName, UpdatedAt)
            VALUES (@p, @n, CURRENT_TIMESTAMP)
            ON CONFLICT(PersId) DO UPDATE SET PlayerName = excluded.PlayerName, UpdatedAt = CURRENT_TIMESTAMP";
        upsert.Parameters.AddWithValue("@p", cleanPersId);
        upsert.Parameters.AddWithValue("@n", cleanNick);
        upsert.ExecuteNonQuery();

        return true;
    }

    public Account? GetByPlayerGUID(long playerGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Accounts WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        using var reader = cmd.ExecuteReader();
        if (reader.Read()) return MapAccount(reader);
        return null;
    }

    public Account CreateAccount(string username, string password, string playerName)
    {
        long playerGuid = GenerateGUID();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Accounts (Username, Password, PlayerGUID, PlayerName, Silver, Gold, Experience, Level, BattleCap, CasernCapacity, GuideStates, MissionData)
            VALUES (@username, @password, @guid, @name, @silver, @gold, @exp, @level, @battleCap, @casernCapacity, @guideStates, @missionData)
            RETURNING Id, PlayerGUID";
        cmd.Parameters.AddWithValue("@username", username);
        cmd.Parameters.AddWithValue("@password", password);
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.Parameters.AddWithValue("@name", playerName);
        cmd.Parameters.AddWithValue("@silver", LocalTestSilver);
        cmd.Parameters.AddWithValue("@gold", LocalTestGold);
        cmd.Parameters.AddWithValue("@exp", LocalTestPlayerExp);
        cmd.Parameters.AddWithValue("@level", LocalTestLevel);
        cmd.Parameters.AddWithValue("@battleCap", 500_000);
        cmd.Parameters.AddWithValue("@casernCapacity", LocalTestCasernCapacity);
        cmd.Parameters.AddWithValue("@guideStates", LocalTestGuideStates);
        cmd.Parameters.AddWithValue("@missionData", LocalTestMissionData);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new Account
            {
                Id = reader.GetInt64(0),
                PlayerGUID = reader.GetInt64(1),
                Username = username,
                Password = password,
                PlayerName = playerName,
                Silver = LocalTestSilver,
                Gold = LocalTestGold,
                Experience = LocalTestPlayerExp,
                Level = LocalTestLevel,
                BattleCap = 500_000,
                CasernCapacity = LocalTestCasernCapacity,
                GuideStates = LocalTestGuideStates,
                MissionData = LocalTestMissionData
            };
        }
        throw new Exception("Failed to create account");
    }

    public void UpdateLastLogin(long playerGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Accounts SET LastLogin = CURRENT_TIMESTAMP WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateResources(long playerGuid, int? silver = null, int? gold = null, int? experience = null, int? level = null, int? battleCap = null, SqliteTransaction? tx = null)
    {
        var updates = new List<string>();
        using var cmd = _db.Connection.CreateCommand();
            if (tx != null) cmd.Transaction = tx;
            if (silver.HasValue) { updates.Add("Silver = @silver"); cmd.Parameters.AddWithValue("@silver", silver.Value); }
            if (gold.HasValue) { updates.Add("Gold = @gold"); cmd.Parameters.AddWithValue("@gold", gold.Value); }
            if (experience.HasValue) { updates.Add("Experience = @exp"); cmd.Parameters.AddWithValue("@exp", experience.Value); }
            if (level.HasValue) { updates.Add("Level = @level"); cmd.Parameters.AddWithValue("@level", level.Value); }
            if (battleCap.HasValue) { updates.Add("BattleCap = @bc"); cmd.Parameters.AddWithValue("@bc", battleCap.Value); }
            if (updates.Count == 0) return;
            cmd.CommandText = $"UPDATE Accounts SET {string.Join(", ", updates)} WHERE PlayerGUID = @guid";
            cmd.Parameters.AddWithValue("@guid", playerGuid);
            cmd.ExecuteNonQuery();
        }

    public void UpdateGarageBackground(long playerGuid, int backgroundId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Accounts SET GarageBackground = @bg WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@bg", backgroundId);
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateContinuousLogin(long playerGuid, int days)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Accounts SET ContinuousLoginDays = @days WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@days", days);
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateVipStatus(long playerGuid, bool isVip, DateTime expireDate)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Accounts SET IsVip = @vip, VipExpireDate = @expire WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@vip", isVip ? 1 : 0);
        cmd.Parameters.AddWithValue("@expire", expireDate.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdatePremiumStatus(long playerGuid, bool isPremium, DateTime expireDate)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Accounts SET IsPremium = @prem, PremiumExpireDate = @expire WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@prem", isPremium ? 1 : 0);
        cmd.Parameters.AddWithValue("@expire", expireDate.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdatePlayerName(long playerGuid, string playerName)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Accounts SET PlayerName = @name WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@name", playerName);
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateLevel(long playerGuid, int level)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Accounts SET Level = @level WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@level", level);
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateBattleCap(long playerGuid, int battleCap)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Accounts SET BattleCap = @bc WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@bc", battleCap);
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateCasernCapacity(long playerGuid, int casernCapacity)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Accounts SET CasernCapacity = @cc WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@cc", casernCapacity);
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.ExecuteNonQuery();
    }

    private static Account MapAccount(SqliteDataReader reader)
    {
        var account = new Account
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            Username = reader.GetString(reader.GetOrdinal("Username")),
            Password = reader.GetString(reader.GetOrdinal("Password")),
            PlayerGUID = reader.GetInt64(reader.GetOrdinal("PlayerGUID")),
            PlayerName = reader.GetString(reader.GetOrdinal("PlayerName")),
            Silver = reader.GetInt32(reader.GetOrdinal("Silver")),
            Gold = reader.GetInt32(reader.GetOrdinal("Gold")),
            Experience = reader.GetInt32(reader.GetOrdinal("Experience")),
            Level = reader.GetInt32(reader.GetOrdinal("Level")),
            BattleCap = reader.GetInt32(reader.GetOrdinal("BattleCap")),
            CasernCapacity = reader.GetInt32(reader.GetOrdinal("CasernCapacity")),
            GarageBackground = reader.GetInt32(reader.GetOrdinal("GarageBackground")),
            ChannelId = reader.GetByte(reader.GetOrdinal("ChannelId")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            LastLogin = DateTime.Parse(reader.GetString(reader.GetOrdinal("LastLogin"))),
            ContinuousLoginDays = reader.GetInt32(reader.GetOrdinal("ContinuousLoginDays")),
            MonthlyLoginDay = reader.GetInt32(reader.GetOrdinal("MonthlyLoginDay")),
            IsVip = reader.GetInt32(reader.GetOrdinal("IsVip")) == 1,
            VipExpireDate = DateTime.TryParse(reader.GetString(reader.GetOrdinal("VipExpireDate")), out var vipDate) ? vipDate : DateTime.MinValue,
            IsPremium = reader.GetInt32(reader.GetOrdinal("IsPremium")) == 1,
            PremiumExpireDate = DateTime.TryParse(reader.GetString(reader.GetOrdinal("PremiumExpireDate")), out var premDate) ? premDate : DateTime.MinValue
        };
        try { account.GuideStates = reader.GetInt64(reader.GetOrdinal("GuideStates")); } catch { }
        try { account.MissionData = reader.GetString(reader.GetOrdinal("MissionData")); } catch { }
        try { account.ShootTimesLvl1 = reader.GetInt32(reader.GetOrdinal("ShootTimesLvl1")); } catch { }
        try { account.ShootTimesLvl2 = reader.GetInt32(reader.GetOrdinal("ShootTimesLvl2")); } catch { }
        try { account.ShootTimesLvl3 = reader.GetInt32(reader.GetOrdinal("ShootTimesLvl3")); } catch { }
        try { account.StandaloneRewardClaimed = reader.GetInt32(reader.GetOrdinal("StandaloneRewardClaimed")) == 1; } catch { }
        return account;
    }

    // v.fix (важно!): в проводе (PK_CH_BuyBox.Level, PK_CH_ShootPrize.nLevl,
    // PK_HC_RemainShootTimesItem.nLvl) уровни идут С НУЛЯ — decfilesgame/NewTreasureBox.cs:
    // enum TreasureBoxLevel { None = -1, One = 0, Two = 1, Three = 2 }, и клиент кастует
    // байт из сети НАПРЯМУЮ в этот enum. Раньше здесь ждали 1/2/3 — из-за чего покупка в
    // Тире 1 (level=0) улетала в default-ветку "ShootTimesLvl1" (случайно совпадало),
    // а Тир 3 (level=2) писался в "ShootTimesLvl2" вместо Lvl3, и Тир 2 (level=1) — тоже
    // мимо. Хуже того: PK_HC_RemainShootTimes с level=3 (то, что слал сервер) не входит
    // ни в один case у клиента (0/1/2) → GetTreasureBoxDataByLevel возвращает null →
    // NullReferenceException в OnTreasureRemainShootTimes КАЖДЫЙ раз при получении этого
    // пакета (видно в output_log.txt) — что и ломало отображение наград/танков без
    // перезахода. Теперь везде 0/1/2, как у клиента.
    /// <summary>Текущее число оставшихся выстрелов в Тире для уровня 0/1/2 (см. комментарий выше).</summary>
    public int GetShootTimes(long playerGuid, int level)
    {
        string col = level switch { 0 => "ShootTimesLvl1", 1 => "ShootTimesLvl2", 2 => "ShootTimesLvl3", _ => "ShootTimesLvl1" };
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = $"SELECT {col} FROM Accounts WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        object? raw = cmd.ExecuteScalar();
        return raw == null || raw == DBNull.Value ? 0 : Convert.ToInt32(raw);
    }

    /// <summary>Устанавливает абсолютное число оставшихся выстрелов для уровня 0/1/2.</summary>
    public void SetShootTimes(long playerGuid, int level, int value)
    {
        string col = level switch { 0 => "ShootTimesLvl1", 1 => "ShootTimesLvl2", 2 => "ShootTimesLvl3", _ => "ShootTimesLvl1" };
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = $"UPDATE Accounts SET {col} = @v WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@v", Math.Max(0, value));
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Прибавляет (или убавляет, если delta отрицательна) число выстрелов; не уходит ниже 0.</summary>
    public int AddShootTimes(long playerGuid, int level, int delta)
    {
        int cur = GetShootTimes(playerGuid, level);
        int updated = Math.Max(0, cur + delta);
        SetShootTimes(playerGuid, level, updated);
        return updated;
    }

    public bool IsStandaloneRewardClaimed(long playerGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT StandaloneRewardClaimed FROM Accounts WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        object? raw = cmd.ExecuteScalar();
        return raw != null && raw != DBNull.Value && Convert.ToInt32(raw) == 1;
    }

    public void MarkStandaloneRewardClaimed(long playerGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Accounts SET StandaloneRewardClaimed = 1 WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateGuideStates(long playerGuid, long guideStates, string missionData)
    {
        if (string.IsNullOrWhiteSpace(missionData))
        {
            missionData = LocalTestMissionData;
        }

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Accounts SET GuideStates = @gs, MissionData = @md WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@gs", guideStates);
        cmd.Parameters.AddWithValue("@md", missionData);
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public void EnsureLocalTestGuideState(long playerGuid)
    {
        var account = GetByPlayerGUID(playerGuid);
        if (account == null)
        {
            return;
        }

        if (account.GuideStates == LocalTestGuideStates && !string.IsNullOrWhiteSpace(account.MissionData))
        {
            return;
        }

        UpdateGuideStates(playerGuid, LocalTestGuideStates, string.IsNullOrWhiteSpace(account.MissionData) ? LocalTestMissionData : account.MissionData);
    }

    private static long GenerateGUID()
    {
        var bytes = new byte[8];
        new Random().NextBytes(bytes);
        return Math.Abs(BitConverter.ToInt64(bytes, 0));
    }
}
