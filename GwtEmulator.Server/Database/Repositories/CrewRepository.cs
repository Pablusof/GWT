using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using GwtEmulator.Server.Database.Models;

namespace GwtEmulator.Server.Database.Repositories;

public class CrewRepository
{
    private readonly DatabaseService _db;

    public CrewRepository(DatabaseService db) => _db = db;

    public List<CrewMember> GetPlayerCrew(long playerGuid)
    {
        var crew = new List<CrewMember>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Crew WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) crew.Add(MapCrew(reader));
        return crew;
    }

    public CrewMember? GetCrewByGuid(long crewGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Crew WHERE CrewGuid = @guid";
        cmd.Parameters.AddWithValue("@guid", crewGuid);
        using var reader = cmd.ExecuteReader();
        if (reader.Read()) return MapCrew(reader);
        return null;
    }

    public CrewMember CreateCrew(long playerGuid, int crewId, long tankGuid, short familyNameId, short nameId, short bigIconId, short smallIconId, short resumeId, int initLv = 1, SqliteTransaction? tx = null)
    {
        long crewGuid = GenerateGUID();
        int isEquipped = tankGuid != 0 ? 1 : 0;
        using var cmd = _db.Connection.CreateCommand();
            if (tx != null) cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO Crew (CrewGuid, PlayerGUID, TankGuid, CrewId, FamilyNameId, NameId, BigIconId, SmallIconId, ResumeId, IsEquipped, CurLv)
                VALUES (@cg, @pg, @tg, @cid, @fn, @n, @bi, @si, @r, @equipped, @curlv)
                RETURNING CrewGuid";
            cmd.Parameters.AddWithValue("@cg", crewGuid);
            cmd.Parameters.AddWithValue("@pg", playerGuid);
            cmd.Parameters.AddWithValue("@tg", tankGuid);
            cmd.Parameters.AddWithValue("@cid", crewId);
            cmd.Parameters.AddWithValue("@fn", familyNameId);
            cmd.Parameters.AddWithValue("@n", nameId);
            cmd.Parameters.AddWithValue("@bi", bigIconId);
            cmd.Parameters.AddWithValue("@si", smallIconId);
            cmd.Parameters.AddWithValue("@r", resumeId);
            cmd.Parameters.AddWithValue("@equipped", isEquipped);
            // Recruit (Новобранец) is a valid level-0 crew, so allow 0 here — only guard
            // against out-of-range values. Previously this forced a minimum of 1, which is
            // why a freshly hired Recruit could never actually show level 0.
            cmd.Parameters.AddWithValue("@curlv", Math.Clamp(initLv, 0, 12));
            cmd.ExecuteScalar();
            return GetCrewByGuid(crewGuid)!;
        }

    public void UpdateCrewTank(long crewGuid, long tankGuid, SqliteTransaction? tx = null)
    {
        using var cmd = _db.Connection.CreateCommand();
            if (tx != null) cmd.Transaction = tx;
            cmd.CommandText = "UPDATE Crew SET TankGuid = @tg, IsEquipped = @equipped WHERE CrewGuid = @cg";
            cmd.Parameters.AddWithValue("@tg", tankGuid);
            cmd.Parameters.AddWithValue("@equipped", tankGuid != 0 ? 1 : 0);
            cmd.Parameters.AddWithValue("@cg", crewGuid);
            cmd.ExecuteNonQuery();
        }

    public void UpdateCrewExp(long crewGuid, int exp, int level)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Crew SET CurExp = @exp, CurLv = @lv WHERE CrewGuid = @cg";
        cmd.Parameters.AddWithValue("@exp", exp);
        cmd.Parameters.AddWithValue("@lv", level);
        cmd.Parameters.AddWithValue("@cg", crewGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateCrewSkills(long crewGuid, string skillIds, int lastSkillId, int lastSkillExp)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Crew SET SkillIds = @skills, Lastlearn = @lsid, LastSkillExp = @lsexp WHERE CrewGuid = @cg";
        cmd.Parameters.AddWithValue("@skills", skillIds);
        cmd.Parameters.AddWithValue("@lsid", lastSkillId);
        cmd.Parameters.AddWithValue("@lsexp", lastSkillExp);
        cmd.Parameters.AddWithValue("@cg", crewGuid);
        cmd.ExecuteNonQuery();
    }

    public void ClearTankAssignment(long playerGuid, long tankGuid, long exceptCrewGuid = 0, SqliteTransaction? tx = null)
    {
        using var cmd = _db.Connection.CreateCommand();
            if (tx != null) cmd.Transaction = tx;
            cmd.CommandText = @"
                UPDATE Crew
                SET TankGuid = 0, IsEquipped = 0
                WHERE PlayerGUID = @pg AND TankGuid = @tg AND (@except = 0 OR CrewGuid <> @except)";
            cmd.Parameters.AddWithValue("@pg", playerGuid);
            cmd.Parameters.AddWithValue("@tg", tankGuid);
            cmd.Parameters.AddWithValue("@except", exceptCrewGuid);
            cmd.ExecuteNonQuery();
        }

    public void NormalizeAssignments(long playerGuid)
    {
        using var fixEquipped = _db.Connection.CreateCommand();
        fixEquipped.CommandText = @"
            UPDATE Crew
            SET IsEquipped = CASE WHEN TankGuid = 0 THEN 0 ELSE 1 END
            WHERE PlayerGUID = @pg";
        fixEquipped.Parameters.AddWithValue("@pg", playerGuid);
        fixEquipped.ExecuteNonQuery();

        using var duplicates = _db.Connection.CreateCommand();
        duplicates.CommandText = @"
            SELECT CrewGuid
            FROM (
                SELECT CrewGuid,
                       ROW_NUMBER() OVER (PARTITION BY TankGuid ORDER BY Id DESC) AS rn
                FROM Crew
                WHERE PlayerGUID = @pg AND TankGuid <> 0
            )
            WHERE rn > 1";
        duplicates.Parameters.AddWithValue("@pg", playerGuid);

        var crewGuidsToIdle = new List<long>();
        using (var reader = duplicates.ExecuteReader())
        {
            while (reader.Read())
            {
                crewGuidsToIdle.Add(reader.GetInt64(0));
            }
        }

        foreach (var crewGuid in crewGuidsToIdle)
        {
            UpdateCrewTank(crewGuid, 0);
        }
    }

    public int GetCrewCount(long playerGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM Crew WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void DeleteCrew(long crewGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Crew WHERE CrewGuid = @cg";
        cmd.Parameters.AddWithValue("@cg", crewGuid);
        cmd.ExecuteNonQuery();
    }

    private static CrewMember MapCrew(SqliteDataReader reader)
    {
        return new CrewMember
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            CrewGuid = reader.GetInt64(reader.GetOrdinal("CrewGuid")),
            PlayerGUID = reader.GetInt64(reader.GetOrdinal("PlayerGUID")),
            TankGuid = reader.GetInt64(reader.GetOrdinal("TankGuid")),
            CrewId = reader.GetInt32(reader.GetOrdinal("CrewId")),
            FamilyNameId = reader.GetInt16(reader.GetOrdinal("FamilyNameId")),
            NameId = reader.GetInt16(reader.GetOrdinal("NameId")),
            BigIconId = reader.GetInt16(reader.GetOrdinal("BigIconId")),
            SmallIconId = reader.GetInt16(reader.GetOrdinal("SmallIconId")),
            ResumeId = reader.GetInt16(reader.GetOrdinal("ResumeId")),
            CurExp = reader.GetInt32(reader.GetOrdinal("CurExp")),
            CurLv = reader.GetInt32(reader.GetOrdinal("CurLv")),
            Lastlearn = reader.GetInt32(reader.GetOrdinal("Lastlearn")),
            LastSkillExp = reader.GetInt32(reader.GetOrdinal("LastSkillExp")),
            LastExp = reader.GetInt32(reader.GetOrdinal("LastExp")),
            LastLv = reader.GetInt32(reader.GetOrdinal("LastLv")),
            SkillIds = reader.GetString(reader.GetOrdinal("SkillIds")),
            IsEquipped = reader.GetInt32(reader.GetOrdinal("IsEquipped")) == 1
        };
    }

    private static long GenerateGUID()
    {
        var bytes = new byte[8];
        new Random().NextBytes(bytes);
        return Math.Abs(BitConverter.ToInt64(bytes, 0));
    }
}
