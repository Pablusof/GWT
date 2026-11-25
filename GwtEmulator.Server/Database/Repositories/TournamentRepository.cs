using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using GwtEmulator.Server.Database.Models;

namespace GwtEmulator.Server.Database.Repositories;

public class TournamentRepository
{
    private readonly DatabaseService _db;

    public TournamentRepository(DatabaseService db) => _db = db;

    public TournamentData? GetByPlayerGUID(long playerGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM TournamentData WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        using var reader = cmd.ExecuteReader();
        if (reader.Read()) return MapTournament(reader);
        return null;
    }

    public TournamentData CreateEntry(long playerGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO TournamentData (PlayerGUID, IsRegistered, TotalDamage, VictoryTime, TotalBattleTime, MaxDamage, RewardClaimed)
            VALUES (@guid, 0, 0, 0, 0, 0, 0)
            RETURNING Id";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.ExecuteScalar();
        return GetByPlayerGUID(playerGuid)!;
    }

    public void Register(long playerGuid)
    {
        var entry = GetByPlayerGUID(playerGuid);
        if (entry == null) CreateEntry(playerGuid);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE TournamentData SET IsRegistered = 1 WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public void Unregister(long playerGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE TournamentData SET IsRegistered = 0 WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateBattleStats(long playerGuid, int damage, int battleTime, bool isVictory)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE TournamentData SET
                TotalDamage = TotalDamage + @dmg,
                TotalBattleTime = TotalBattleTime + @bt,
                MaxDamage = CASE WHEN @dmg > MaxDamage THEN @dmg ELSE MaxDamage END,
                VictoryTime = CASE WHEN @victory = 1 THEN VictoryTime + 1 ELSE VictoryTime END
            WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@dmg", damage);
        cmd.Parameters.AddWithValue("@bt", battleTime);
        cmd.Parameters.AddWithValue("@victory", isVictory ? 1 : 0);
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public void ClaimReward(long playerGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE TournamentData SET RewardClaimed = 1 WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public List<TournamentRankEntry> GetRankings(int topN = 50)
    {
        var rankings = new List<TournamentRankEntry>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT t.PlayerGUID, a.PlayerName, t.TotalDamage, t.MaxDamage, t.VictoryTime, t.TotalBattleTime
            FROM TournamentData t
            JOIN Accounts a ON t.PlayerGUID = a.PlayerGUID
            WHERE t.IsRegistered = 1 AND t.TotalDamage > 0
            ORDER BY t.TotalDamage DESC, t.MaxDamage DESC
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", topN);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rankings.Add(new TournamentRankEntry
            {
                PlayerGUID = reader.GetInt64(reader.GetOrdinal("PlayerGUID")),
                PlayerName = reader.GetString(reader.GetOrdinal("PlayerName")),
                TotalDamage = reader.GetInt32(reader.GetOrdinal("TotalDamage")),
                MaxDamage = reader.GetInt32(reader.GetOrdinal("MaxDamage")),
                VictoryTime = reader.GetInt32(reader.GetOrdinal("VictoryTime")),
                TotalBattleTime = reader.GetInt32(reader.GetOrdinal("TotalBattleTime"))
            });
        }
        return rankings;
    }

    public int GetPlayerRank(long playerGuid)
    {
        var rankings = GetRankings(1000);
        for (int i = 0; i < rankings.Count; i++)
        {
            if (rankings[i].PlayerGUID == playerGuid) return i + 1;
        }
        return 0;
    }

    private static TournamentData MapTournament(SqliteDataReader reader)
    {
        return new TournamentData
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            PlayerGUID = reader.GetInt64(reader.GetOrdinal("PlayerGUID")),
            IsRegistered = reader.GetInt32(reader.GetOrdinal("IsRegistered")) == 1,
            TotalDamage = reader.GetInt32(reader.GetOrdinal("TotalDamage")),
            VictoryTime = reader.GetInt32(reader.GetOrdinal("VictoryTime")),
            TotalBattleTime = reader.GetInt32(reader.GetOrdinal("TotalBattleTime")),
            MaxDamage = reader.GetInt32(reader.GetOrdinal("MaxDamage")),
            RewardClaimed = reader.GetInt32(reader.GetOrdinal("RewardClaimed")) == 1
        };
    }
}

public class TournamentRankEntry
{
    public long PlayerGUID { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public int TotalDamage { get; set; }
    public int MaxDamage { get; set; }
    public int VictoryTime { get; set; }
    public int TotalBattleTime { get; set; }
}
