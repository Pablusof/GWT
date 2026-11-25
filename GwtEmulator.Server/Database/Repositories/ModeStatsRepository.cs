using Microsoft.Data.Sqlite;
using GwtEmulator.Server.Database.Models;

namespace GwtEmulator.Server.Database.Repositories;

public class ModeStatsRepository
{
    private readonly DatabaseService _db;

    public ModeStatsRepository(DatabaseService db) => _db = db;

    public ModeStats? GetByPlayerAndMode(long playerGuid, int gameMode)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM ModeStats WHERE PlayerGUID = @pg AND GameMode = @gm";
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.Parameters.AddWithValue("@gm", gameMode);
        using var reader = cmd.ExecuteReader();
        if (reader.Read()) return MapStats(reader);
        return null;
    }

    private ModeStats GetOrCreate(long playerGuid, int gameMode)
    {
        var stats = GetByPlayerAndMode(playerGuid, gameMode);
        if (stats != null) return stats;

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ModeStats (PlayerGUID, GameMode)
            VALUES (@pg, @gm)
            RETURNING Id";
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.Parameters.AddWithValue("@gm", gameMode);
        cmd.ExecuteScalar();
        return GetByPlayerAndMode(playerGuid, gameMode)!;
    }

    // score здесь — очки за бой (например, число волн/захвата для PVE), используется
    // для ScoreTotal/MaxScoreInBattle, нужных только вкладке PVE; для остальных режимов
    // можно передавать 0.
    public void RecordBattle(long playerGuid, int gameMode, int kills, int deaths, int damage,
        int shoots, int hits, bool isWin, int exp, int score = 0)
    {
        GetOrCreate(playerGuid, gameMode);
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE ModeStats SET
                KillNum = KillNum + @kills,
                DeathNum = DeathNum + @deaths,
                Damage = Damage + @damage,
                ShootNum = ShootNum + @shoots,
                HitNum = HitNum + @hits,
                BattleCount = BattleCount + 1,
                WinCount = WinCount + @win,
                ExpTotal = ExpTotal + @exp,
                ScoreTotal = ScoreTotal + @score,
                MaxKillInBattle = MAX(MaxKillInBattle, @kills),
                MaxExpInBattle = MAX(MaxExpInBattle, @exp),
                MaxScoreInBattle = MAX(MaxScoreInBattle, @score)
            WHERE PlayerGUID = @pg AND GameMode = @gm";
        cmd.Parameters.AddWithValue("@kills", kills);
        cmd.Parameters.AddWithValue("@deaths", deaths);
        cmd.Parameters.AddWithValue("@damage", damage);
        cmd.Parameters.AddWithValue("@shoots", shoots);
        cmd.Parameters.AddWithValue("@hits", hits);
        cmd.Parameters.AddWithValue("@win", isWin ? 1 : 0);
        cmd.Parameters.AddWithValue("@exp", exp);
        cmd.Parameters.AddWithValue("@score", score);
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.Parameters.AddWithValue("@gm", gameMode);
        cmd.ExecuteNonQuery();
    }

    private static ModeStats MapStats(SqliteDataReader reader)
    {
        return new ModeStats
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            PlayerGUID = reader.GetInt64(reader.GetOrdinal("PlayerGUID")),
            GameMode = reader.GetInt32(reader.GetOrdinal("GameMode")),
            BattleCount = reader.GetInt32(reader.GetOrdinal("BattleCount")),
            WinCount = reader.GetInt32(reader.GetOrdinal("WinCount")),
            KillNum = reader.GetInt32(reader.GetOrdinal("KillNum")),
            DeathNum = reader.GetInt32(reader.GetOrdinal("DeathNum")),
            Damage = reader.GetInt32(reader.GetOrdinal("Damage")),
            ShootNum = reader.GetInt32(reader.GetOrdinal("ShootNum")),
            HitNum = reader.GetInt32(reader.GetOrdinal("HitNum")),
            ExpTotal = reader.GetInt32(reader.GetOrdinal("ExpTotal")),
            ScoreTotal = reader.GetInt32(reader.GetOrdinal("ScoreTotal")),
            MaxKillInBattle = reader.GetInt32(reader.GetOrdinal("MaxKillInBattle")),
            MaxExpInBattle = reader.GetInt32(reader.GetOrdinal("MaxExpInBattle")),
            MaxScoreInBattle = reader.GetInt32(reader.GetOrdinal("MaxScoreInBattle")),
        };
    }
}
