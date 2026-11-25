using System.Collections.Generic;
using System.Linq;
namespace GwtEmulator.Server.Database.Repositories;

public class TankEquipmentRepository
{
    private readonly DatabaseService _db;

    public TankEquipmentRepository(DatabaseService db) => _db = db;

    public List<int> GetTankEquipmentIds(long tankGuid)
    {
        var equipmentIds = new List<int>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT EquipmentId FROM TankEquipment WHERE TankGuid = @guid ORDER BY SlotIndex ASC";
        cmd.Parameters.AddWithValue("@guid", tankGuid);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            equipmentIds.Add(reader.GetInt32(0));
        }

        return equipmentIds;
    }

    public Dictionary<long, List<int>> GetAllPlayerTankEquipmentIds(long playerGuid)
    {
        var result = new Dictionary<long, List<int>>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            SELECT te.TankGuid, te.EquipmentId
            FROM TankEquipment te
            INNER JOIN Tanks t ON t.TankGUID = te.TankGuid
            WHERE t.PlayerGUID = @pg
            ORDER BY te.TankGuid ASC, te.SlotIndex ASC";
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            long tankGuid = reader.GetInt64(0);
            if (!result.TryGetValue(tankGuid, out var list))
            {
                list = new List<int>();
                result[tankGuid] = list;
            }
            list.Add(reader.GetInt32(1));
        }

        return result;
    }

    public void SaveTankEquipment(long tankGuid, IReadOnlyList<int> equipmentIds)
    {
        using var tx = _db.Connection.BeginTransaction();

        using (var deleteCmd = _db.Connection.CreateCommand())
        {
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = "DELETE FROM TankEquipment WHERE TankGuid = @guid";
            deleteCmd.Parameters.AddWithValue("@guid", tankGuid);
            deleteCmd.ExecuteNonQuery();
        }

        int slotIndex = 0;
        foreach (int equipmentId in equipmentIds.Where(id => id > 0).Take(36))
        {
            using var insertCmd = _db.Connection.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = @"
                INSERT INTO TankEquipment (TankGuid, SlotIndex, EquipmentId)
                VALUES (@guid, @slot, @equipmentId)";
            insertCmd.Parameters.AddWithValue("@guid", tankGuid);
            insertCmd.Parameters.AddWithValue("@slot", slotIndex++);
            insertCmd.Parameters.AddWithValue("@equipmentId", equipmentId);
            insertCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
