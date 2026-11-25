using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using GwtEmulator.Server.Database.Models;

namespace GwtEmulator.Server.Database.Repositories;

public class BattleStatsRepository
{
    private readonly DatabaseService _db;

    public BattleStatsRepository(DatabaseService db) => _db = db;

    public BattleStats? GetByPlayerAndTank(long playerGuid, long tankGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM BattleStats WHERE PlayerGUID = @pg AND TankGUID = @tg";
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.Parameters.AddWithValue("@tg", tankGuid);
        using var reader = cmd.ExecuteReader();
        if (reader.Read()) return MapStats(reader);
        return null;
    }

    public BattleStats GetOrCreate(long playerGuid, long tankGuid)
    {
        var stats = GetByPlayerAndTank(playerGuid, tankGuid);
        if (stats != null) return stats;

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO BattleStats (PlayerGUID, TankGUID, KillNum, DeathNum, Score, Damage, ShootNum, HitNum, BattleCount, WinCount)
            VALUES (@pg, @tg, 0, 0, 0, 0, 0, 0, 0, 0)
            RETURNING Id";
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.Parameters.AddWithValue("@tg", tankGuid);
        cmd.ExecuteScalar();
        return GetByPlayerAndTank(playerGuid, tankGuid)!;
    }

    public void RecordBattle(long playerGuid, long tankGuid, int kills, int deaths, int score, int damage, int shoots, int hits, bool isWin, int exp = 0)
    {
        var stats = GetOrCreate(playerGuid, tankGuid);
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE BattleStats SET
                KillNum = KillNum + @kills,
                DeathNum = DeathNum + @deaths,
                Score = Score + @score,
                Damage = Damage + @damage,
                ShootNum = ShootNum + @shoots,
                HitNum = HitNum + @hits,
                BattleCount = BattleCount + 1,
                WinCount = WinCount + @win,
                ExpTotal = ExpTotal + @exp,
                MaxKillInBattle = MAX(MaxKillInBattle, @kills),
                MaxExpInBattle = MAX(MaxExpInBattle, @exp)
            WHERE PlayerGUID = @pg AND TankGUID = @tg";
        cmd.Parameters.AddWithValue("@kills", kills);
        cmd.Parameters.AddWithValue("@deaths", deaths);
        cmd.Parameters.AddWithValue("@score", score);
        cmd.Parameters.AddWithValue("@damage", damage);
        cmd.Parameters.AddWithValue("@shoots", shoots);
        cmd.Parameters.AddWithValue("@hits", hits);
        cmd.Parameters.AddWithValue("@win", isWin ? 1 : 0);
        cmd.Parameters.AddWithValue("@exp", exp);
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.Parameters.AddWithValue("@tg", tankGuid);
        cmd.ExecuteNonQuery();
    }

    public List<BattleStats> GetPlayerStats(long playerGuid)
    {
        var stats = new List<BattleStats>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM BattleStats WHERE PlayerGUID = @pg ORDER BY BattleCount DESC";
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) stats.Add(MapStats(reader));
        return stats;
    }

    private static BattleStats MapStats(SqliteDataReader reader)
    {
        return new BattleStats
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            PlayerGUID = reader.GetInt64(reader.GetOrdinal("PlayerGUID")),
            TankGUID = reader.GetInt64(reader.GetOrdinal("TankGUID")),
            KillNum = reader.GetInt32(reader.GetOrdinal("KillNum")),
            DeathNum = reader.GetInt32(reader.GetOrdinal("DeathNum")),
            Score = reader.GetInt32(reader.GetOrdinal("Score")),
            Damage = reader.GetInt32(reader.GetOrdinal("Damage")),
            ShootNum = reader.GetInt32(reader.GetOrdinal("ShootNum")),
            HitNum = reader.GetInt32(reader.GetOrdinal("HitNum")),
            BattleCount = reader.GetInt32(reader.GetOrdinal("BattleCount")),
            WinCount = reader.GetInt32(reader.GetOrdinal("WinCount")),
            // v77: колонки добавлены поздним ALTER TABLE — на старой БД, где строка уже
            // существовала до миграции, значение там DEFAULT 0, так что GetOrdinal
            // отработает нормально в любом случае.
            ExpTotal = SafeGetInt32(reader, "ExpTotal"),
            MaxKillInBattle = SafeGetInt32(reader, "MaxKillInBattle"),
            MaxExpInBattle = SafeGetInt32(reader, "MaxExpInBattle"),
        };
    }

    private static int SafeGetInt32(SqliteDataReader reader, string column)
    {
        try { return reader.GetInt32(reader.GetOrdinal(column)); }
        catch { return 0; }
    }
}
