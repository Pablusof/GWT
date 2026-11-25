using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using GwtEmulator.Server.Database.Models;

namespace GwtEmulator.Server.Database.Repositories;

public class InventoryRepository
{
    private readonly DatabaseService _db;

    public InventoryRepository(DatabaseService db) => _db = db;

    public List<InventoryItem> GetPlayerItems(long playerGuid)
    {
        var items = new List<InventoryItem>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Inventory WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) items.Add(MapItem(reader));
        return items;
    }

    public InventoryItem? GetItem(long playerGuid, byte itemType, int itemId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Inventory WHERE PlayerGUID = @guid AND ItemType = @type AND ItemId = @id";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.Parameters.AddWithValue("@type", itemType);
        cmd.Parameters.AddWithValue("@id", itemId);
        using var reader = cmd.ExecuteReader();
        if (reader.Read()) return MapItem(reader);
        return null;
    }

    public void AddItem(long playerGuid, byte itemType, int itemId, int num, byte flag = 0)
    {
        var existing = GetItem(playerGuid, itemType, itemId);
        if (existing != null)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "UPDATE Inventory SET ItemNum = ItemNum + @num WHERE PlayerGUID = @guid AND ItemType = @type AND ItemId = @id";
            cmd.Parameters.AddWithValue("@num", num);
            cmd.Parameters.AddWithValue("@guid", playerGuid);
            cmd.Parameters.AddWithValue("@type", itemType);
            cmd.Parameters.AddWithValue("@id", itemId);
            cmd.ExecuteNonQuery();
        }
        else
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "INSERT INTO Inventory (PlayerGUID, ItemType, ItemId, ItemNum, ItemFlag) VALUES (@guid, @type, @id, @num, @flag)";
            cmd.Parameters.AddWithValue("@guid", playerGuid);
            cmd.Parameters.AddWithValue("@type", itemType);
            cmd.Parameters.AddWithValue("@id", itemId);
            cmd.Parameters.AddWithValue("@num", num);
            cmd.Parameters.AddWithValue("@flag", flag);
            cmd.ExecuteNonQuery();
        }
    }

    public bool RemoveItem(long playerGuid, byte itemType, int itemId, int num)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT ItemNum FROM Inventory WHERE PlayerGUID = @guid AND ItemType = @type AND ItemId = @id";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.Parameters.AddWithValue("@type", itemType);
        cmd.Parameters.AddWithValue("@id", itemId);
        var result = cmd.ExecuteScalar();
        if (result == null) return false;
        int current = Convert.ToInt32(result);
        if (current < num) return false;

        using var cmd2 = _db.Connection.CreateCommand();
        if (current == num)
        {
            cmd2.CommandText = "DELETE FROM Inventory WHERE PlayerGUID = @guid AND ItemType = @type AND ItemId = @id";
        }
        else
        {
            cmd2.CommandText = "UPDATE Inventory SET ItemNum = ItemNum - @num WHERE PlayerGUID = @guid AND ItemType = @type AND ItemId = @id";
            cmd2.Parameters.AddWithValue("@num", num);
        }
        cmd2.Parameters.AddWithValue("@guid", playerGuid);
        cmd2.Parameters.AddWithValue("@type", itemType);
        cmd2.Parameters.AddWithValue("@id", itemId);
        cmd2.ExecuteNonQuery();
        return true;
    }

    public int GetItemCount(long playerGuid, byte itemType, int itemId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(ItemNum, 0) FROM Inventory WHERE PlayerGUID = @guid AND ItemType = @type AND ItemId = @id";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        cmd.Parameters.AddWithValue("@type", itemType);
        cmd.Parameters.AddWithValue("@id", itemId);
        var result = cmd.ExecuteScalar();
        return result != null ? Convert.ToInt32(result) : 0;
    }

    private static InventoryItem MapItem(SqliteDataReader reader)
    {
        return new InventoryItem
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            PlayerGUID = reader.GetInt64(reader.GetOrdinal("PlayerGUID")),
            ItemType = reader.GetByte(reader.GetOrdinal("ItemType")),
            ItemId = reader.GetInt32(reader.GetOrdinal("ItemId")),
            ItemNum = reader.GetInt32(reader.GetOrdinal("ItemNum")),
            ItemFlag = reader.GetByte(reader.GetOrdinal("ItemFlag"))
        };
    }
}
