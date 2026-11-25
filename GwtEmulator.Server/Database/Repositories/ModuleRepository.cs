using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace GwtEmulator.Server.Database.Repositories;

public class ModuleRepository
{
    private readonly DatabaseService _db;

    public ModuleRepository(DatabaseService db) => _db = db;

    public static bool TryNormalizeClientResearchModuleType(int moduleType, out int normalizedType)
    {
        switch (moduleType)
        {
            case 1:
            case 2:
            case 4:
            case 5:
            case 6:
            case 7:
                normalizedType = moduleType;
                return true;
            case 3:
                normalizedType = 2;
                return true;
            default:
                normalizedType = 0;
                return false;
        }
    }

    public void AddResearchedModule(long playerGuid, int vehicleId, int moduleType, int moduleId, SqliteTransaction? tx = null)
    {
        if (!TryNormalizeClientResearchModuleType(moduleType, out int normalizedType) || moduleId <= 0)
        {
            return;
        }

        using var cmd = _db.Connection.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO ResearchedModules (PlayerGUID, VehicleId, ModuleType, ModuleId)
            SELECT @pg, @vid, @mt, @mid
            WHERE NOT EXISTS (
                SELECT 1
                FROM ResearchedModules
                WHERE PlayerGUID = @pg AND VehicleId = @vid AND ModuleType = @mt AND ModuleId = @mid
            )";
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.Parameters.AddWithValue("@vid", vehicleId);
        cmd.Parameters.AddWithValue("@mt", normalizedType);
        cmd.Parameters.AddWithValue("@mid", moduleId);
        cmd.ExecuteNonQuery();
    }

    public HashSet<(int VehicleId, int ModuleType, int ModuleId)> GetResearchedModuleSet(long playerGuid)
    {
        var set = new HashSet<(int, int, int)>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT VehicleId, ModuleType, ModuleId
            FROM ResearchedModules
            WHERE PlayerGUID = @pg";
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (TryNormalizeClientResearchModuleType(reader.GetInt32(1), out int normalizedType))
                set.Add((reader.GetInt32(0), normalizedType, reader.GetInt32(2)));
        }
        return set;
    }

    public List<(int VehicleId, int ModuleType, int ModuleId)> GetResearchedModules(long playerGuid)
    {
        var modules = new List<(int, int, int)>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT VehicleId, ModuleType, ModuleId
            FROM ResearchedModules
            WHERE PlayerGUID = @pg
            ORDER BY VehicleId, ModuleType, ModuleId";
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int moduleType = reader.GetInt32(1);
            if (!TryNormalizeClientResearchModuleType(moduleType, out int normalizedType))
            {
                continue;
            }

            modules.Add((
                reader.GetInt32(0),
                normalizedType,
                reader.GetInt32(2)
            ));
        }
        return modules;
    }

    public bool IsModuleResearched(long playerGuid, int vehicleId, int moduleType, int moduleId)
    {
        if (!TryNormalizeClientResearchModuleType(moduleType, out int normalizedType) || moduleId <= 0)
        {
            return false;
        }

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(1) FROM ResearchedModules 
            WHERE PlayerGUID = @pg AND VehicleId = @vid AND ModuleType = @mt AND ModuleId = @mid";
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.Parameters.AddWithValue("@vid", vehicleId);
        cmd.Parameters.AddWithValue("@mt", normalizedType);
        cmd.Parameters.AddWithValue("@mid", moduleId);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public void NormalizePlayerModules(long playerGuid)
    {
        using (var normalizeChassis = _db.Connection.CreateCommand())
        {
            normalizeChassis.CommandText = @"
                UPDATE ResearchedModules
                SET ModuleType = 2
                WHERE PlayerGUID = @pg AND ModuleType = 3";
            normalizeChassis.Parameters.AddWithValue("@pg", playerGuid);
            normalizeChassis.ExecuteNonQuery();
        }

        using (var deleteUnsupported = _db.Connection.CreateCommand())
        {
            deleteUnsupported.CommandText = @"
                DELETE FROM ResearchedModules
                WHERE PlayerGUID = @pg AND ModuleType IN (8, 9, 10)";
            deleteUnsupported.Parameters.AddWithValue("@pg", playerGuid);
            deleteUnsupported.ExecuteNonQuery();
        }

        using var deleteDuplicates = _db.Connection.CreateCommand();
        deleteDuplicates.CommandText = @"
            DELETE FROM ResearchedModules
            WHERE PlayerGUID = @pg
              AND rowid NOT IN (
                  SELECT MIN(rowid)
                  FROM ResearchedModules
                  WHERE PlayerGUID = @pg
                  GROUP BY VehicleId, ModuleType, ModuleId
              )";
        deleteDuplicates.Parameters.AddWithValue("@pg", playerGuid);
        deleteDuplicates.ExecuteNonQuery();
    }

    public void ResearchTankBaseModules(
        long playerGuid, int vehicleId, int hullId, int underpanId, int turretId, int mainGunId, int engineId,
        int fuelTankId = 0, int cartridgeId = 0, int radioId = 0, int gaugeId = 0,
        HashSet<(int VehicleId, int ModuleType, int ModuleId)>? existing = null,
        SqliteTransaction? tx = null)
    {
        void Ensure(int moduleType, int moduleId)
        {
            if (moduleId <= 0) return;
            if (existing != null && !existing.Add((vehicleId, moduleType, moduleId))) return;
            AddResearchedModule(playerGuid, vehicleId, moduleType, moduleId, tx);
        }

        Ensure(1, hullId);
        Ensure(2, underpanId);
        Ensure(4, turretId);
        Ensure(5, mainGunId);
        Ensure(6, engineId);
        Ensure(7, radioId);
    }
}
