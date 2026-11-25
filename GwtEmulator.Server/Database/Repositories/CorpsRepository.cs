using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using GwtEmulator.Server.Database.Models;

namespace GwtEmulator.Server.Database.Repositories;

public class CorpsRepository
{
    private readonly DatabaseService _db;

    public CorpsRepository(DatabaseService db) => _db = db;

    public Corps? GetByGUID(long corpsGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Corps WHERE CorpsGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", corpsGuid);
        using var reader = cmd.ExecuteReader();
        if (reader.Read()) return MapCorps(reader);
        return null;
    }

    public Corps? GetByName(string name)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Corps WHERE CorpsName = @name";
        cmd.Parameters.AddWithValue("@name", name);
        using var reader = cmd.ExecuteReader();
        if (reader.Read()) return MapCorps(reader);
        return null;
    }

    public Corps CreateCorps(long corpsGuid, string name, string tag, long leaderGuid, string leaderName)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Corps (CorpsGUID, CorpsName, CorpsTag, LeaderGUID, LeaderName)
            VALUES (@cg, @name, @tag, @lg, @ln) RETURNING CorpsGUID";
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@tag", tag);
        cmd.Parameters.AddWithValue("@lg", leaderGuid);
        cmd.Parameters.AddWithValue("@ln", leaderName);
        cmd.ExecuteScalar();

        AddMember(corpsGuid, leaderGuid, leaderName, 2);
        return GetByGUID(corpsGuid)!;
    }

    public List<Corps> SearchCorps(string name, int limit = 20)
    {
        var list = new List<Corps>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Corps WHERE CorpsName LIKE @name LIMIT @limit";
        cmd.Parameters.AddWithValue("@name", $"%{name}%");
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(MapCorps(reader));
        return list;
    }

    public List<CorpsMember> GetMembers(long corpsGuid)
    {
        var members = new List<CorpsMember>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM CorpsMembers WHERE CorpsGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", corpsGuid);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) members.Add(MapMember(reader));
        return members;
    }

    public void AddMember(long corpsGuid, long playerGuid, string playerName, byte job = 0)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO CorpsMembers (CorpsGUID, PlayerGUID, PlayerName, Job)
            VALUES (@cg, @pg, @pn, @job)";
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.Parameters.AddWithValue("@pn", playerName);
        cmd.Parameters.AddWithValue("@job", job);
        cmd.ExecuteNonQuery();
    }

    public void RemoveMember(long corpsGuid, long playerGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM CorpsMembers WHERE CorpsGUID = @cg AND PlayerGUID = @pg";
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateMemberJob(long corpsGuid, long playerGuid, byte job)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE CorpsMembers SET Job = @job WHERE CorpsGUID = @cg AND PlayerGUID = @pg";
        cmd.Parameters.AddWithValue("@job", job);
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public List<CorpsApplication> GetApplications(long corpsGuid)
    {
        var apps = new List<CorpsApplication>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM CorpsApplications WHERE CorpsGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", corpsGuid);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) apps.Add(MapApplication(reader));
        return apps;
    }

    public void AddApplication(long corpsGuid, long playerGuid, string playerName)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO CorpsApplications (CorpsGUID, PlayerGUID, PlayerName)
            VALUES (@cg, @pg, @pn)";
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.Parameters.AddWithValue("@pn", playerName);
        cmd.ExecuteNonQuery();
    }

    public void RemoveApplication(long corpsGuid, long playerGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM CorpsApplications WHERE CorpsGUID = @cg AND PlayerGUID = @pg";
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.ExecuteNonQuery();
    }

    // Cancel ALL pending applications for a player (needed before submitting a new one)
    public void RemoveAllPlayerApplications(long playerGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM CorpsApplications WHERE PlayerGUID = @pg";
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public CorpsApplication? GetPlayerApplication(long playerGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM CorpsApplications WHERE PlayerGUID = @pg LIMIT 1";
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        using var reader = cmd.ExecuteReader();
        if (reader.Read()) return MapApplication(reader);
        return null;
    }

    public void DisbandCorps(long corpsGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM CorpsApplications WHERE CorpsGUID = @cg";
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        cmd.ExecuteNonQuery();
        using var cmd2 = _db.Connection.CreateCommand();
        cmd2.CommandText = "DELETE FROM CorpsMembers WHERE CorpsGUID = @cg";
        cmd2.Parameters.AddWithValue("@cg", corpsGuid);
        cmd2.ExecuteNonQuery();
        using var cmd3 = _db.Connection.CreateCommand();
        cmd3.CommandText = "DELETE FROM Corps WHERE CorpsGUID = @cg";
        cmd3.Parameters.AddWithValue("@cg", corpsGuid);
        cmd3.ExecuteNonQuery();
    }

    public long? GetPlayerCorpsGUID(long playerGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT CorpsGUID FROM CorpsMembers WHERE PlayerGUID = @guid LIMIT 1";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        var result = cmd.ExecuteScalar();
        return result != null ? Convert.ToInt64(result) : (long?)null;
    }

    public void UpdateCorpsIntroduce(long corpsGuid, string introduce)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Corps SET Introduce = @intro WHERE CorpsGUID = @cg";
        cmd.Parameters.AddWithValue("@intro", introduce);
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateCorpsNotice(long corpsGuid, string notice)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Corps SET Notice = @notice WHERE CorpsGUID = @cg";
        cmd.Parameters.AddWithValue("@notice", notice);
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateCorpsTipName(long corpsGuid, string tipName)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Corps SET TipName = @tip WHERE CorpsGUID = @cg";
        cmd.Parameters.AddWithValue("@tip", tipName);
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateCorpsName(long corpsGuid, string name)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Corps SET CorpsName = @name WHERE CorpsGUID = @cg";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateCorpsTaxRate(long corpsGuid, byte taxRate)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Corps SET TaxRate = @tax WHERE CorpsGUID = @cg";
        cmd.Parameters.AddWithValue("@tax", taxRate);
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateCorpsMoney(long corpsGuid, long amount)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Corps SET Money = @money WHERE CorpsGUID = @cg";
        cmd.Parameters.AddWithValue("@money", amount);
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        cmd.ExecuteNonQuery();
    }

    public void AddCorpsMoney(long corpsGuid, long amount)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Corps SET Money = Money + @amount WHERE CorpsGUID = @cg";
        cmd.Parameters.AddWithValue("@amount", amount);
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpgradeCorpsLevel(long corpsGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Corps SET CorpsLevel = CorpsLevel + 1, MaxMemberCount = MaxMemberCount + 5 WHERE CorpsGUID = @cg";
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateMemberBattleCap(long corpsGuid, long playerGuid, int battleCap)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE CorpsMembers SET BattleCap = @bc WHERE CorpsGUID = @cg AND PlayerGUID = @pg";
        cmd.Parameters.AddWithValue("@bc", battleCap);
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateMemberLoyalPoint(long corpsGuid, long playerGuid, int points)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE CorpsMembers SET LoyalPoint = LoyalPoint + @lp WHERE CorpsGUID = @cg AND PlayerGUID = @pg";
        cmd.Parameters.AddWithValue("@lp", points);
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public void RecalculateCorpsBattleCap(long corpsGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Corps SET BattleCap = (SELECT COALESCE(SUM(BattleCap), 0) FROM CorpsMembers WHERE CorpsGUID = @cg) WHERE CorpsGUID = @cg";
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        cmd.ExecuteNonQuery();
    }

    public int GetMemberCount(long corpsGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM CorpsMembers WHERE CorpsGUID = @cg";
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public CorpsMember? GetMember(long corpsGuid, long playerGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM CorpsMembers WHERE CorpsGUID = @cg AND PlayerGUID = @pg";
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        using var reader = cmd.ExecuteReader();
        if (reader.Read()) return MapMember(reader);
        return null;
    }

    public void ExpelMember(long corpsGuid, long playerGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM CorpsMembers WHERE CorpsGUID = @cg AND PlayerGUID = @pg";
        cmd.Parameters.AddWithValue("@cg", corpsGuid);
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public List<Corps> GetAllCorps(int limit = 50)
    {
        var list = new List<Corps>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Corps ORDER BY BattleCap DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(MapCorps(reader));
        return list;
    }

    private static Corps MapCorps(SqliteDataReader reader)
    {
        return new Corps
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            CorpsGUID = reader.GetInt64(reader.GetOrdinal("CorpsGUID")),
            CorpsName = reader.GetString(reader.GetOrdinal("CorpsName")),
            CorpsTag = reader.GetString(reader.GetOrdinal("CorpsTag")),
            TipName = reader.GetString(reader.GetOrdinal("TipName")),
            Introduce = reader.GetString(reader.GetOrdinal("Introduce")),
            Notice = reader.GetString(reader.GetOrdinal("Notice")),
            LeaderGUID = reader.GetInt64(reader.GetOrdinal("LeaderGUID")),
            LeaderName = reader.GetString(reader.GetOrdinal("LeaderName")),
            CorpsLevel = (ushort)reader.GetInt32(reader.GetOrdinal("CorpsLevel")),
            State = reader.GetByte(reader.GetOrdinal("State")),
            TaxRate = reader.GetByte(reader.GetOrdinal("TaxRate")),
            MaxMemberCount = reader.GetInt32(reader.GetOrdinal("MaxMemberCount")),
            BattleCap = reader.GetInt64(reader.GetOrdinal("BattleCap")),
            Money = reader.GetInt64(reader.GetOrdinal("Money")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("CreatedAt")))
        };
    }

    private static CorpsMember MapMember(SqliteDataReader reader)
    {
        return new CorpsMember
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            CorpsGUID = reader.GetInt64(reader.GetOrdinal("CorpsGUID")),
            PlayerGUID = reader.GetInt64(reader.GetOrdinal("PlayerGUID")),
            PlayerName = reader.GetString(reader.GetOrdinal("PlayerName")),
            Job = reader.GetByte(reader.GetOrdinal("Job")),
            LoyalPoint = reader.GetInt32(reader.GetOrdinal("LoyalPoint")),
            BattleCap = reader.GetInt32(reader.GetOrdinal("BattleCap")),
            JoinDate = DateTime.Parse(reader.GetString(reader.GetOrdinal("JoinDate"))),
            LastLoginDate = DateTime.Parse(reader.GetString(reader.GetOrdinal("LastLoginDate")))
        };
    }

    private static CorpsApplication MapApplication(SqliteDataReader reader)
    {
        return new CorpsApplication
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            CorpsGUID = reader.GetInt64(reader.GetOrdinal("CorpsGUID")),
            PlayerGUID = reader.GetInt64(reader.GetOrdinal("PlayerGUID")),
            PlayerName = reader.GetString(reader.GetOrdinal("PlayerName")),
            ApplyTime = DateTime.Parse(reader.GetString(reader.GetOrdinal("ApplyTime")))
        };
    }
}
