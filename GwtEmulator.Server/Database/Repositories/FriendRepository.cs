using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using GwtEmulator.Server.Database.Models;

namespace GwtEmulator.Server.Database.Repositories;

public class FriendRepository
{
    private readonly DatabaseService _db;

    public FriendRepository(DatabaseService db) => _db = db;

    public List<Friend> GetFriends(long playerGuid)
    {
        var friends = new List<Friend>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Friends WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", playerGuid);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) friends.Add(MapFriend(reader));
        return friends;
    }

    public void AddFriend(long playerGuid, long friendGuid, string friendName)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Friends (PlayerGUID, FriendGUID, FriendName)
            VALUES (@pg, @fg, @fn)";
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.Parameters.AddWithValue("@fg", friendGuid);
        cmd.Parameters.AddWithValue("@fn", friendName);
        cmd.ExecuteNonQuery();
    }

    public void RemoveFriend(long playerGuid, long friendGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Friends WHERE PlayerGUID = @pg AND FriendGUID = @fg";
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.Parameters.AddWithValue("@fg", friendGuid);
        cmd.ExecuteNonQuery();
    }

    public void SetOnlineStatus(long playerGuid, bool isOnline)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Friends SET IsOnline = @online WHERE FriendGUID = @fg";
        cmd.Parameters.AddWithValue("@online", isOnline ? 1 : 0);
        cmd.Parameters.AddWithValue("@fg", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public bool IsFriend(long playerGuid, long friendGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM Friends WHERE PlayerGUID = @pg AND FriendGUID = @fg";
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.Parameters.AddWithValue("@fg", friendGuid);
        return cmd.ExecuteScalar() != null;
    }

    public void UpdateOnlineStatus(long playerGuid, bool isOnline)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Friends SET IsOnline = @online WHERE FriendGUID = @fg";
        cmd.Parameters.AddWithValue("@online", isOnline ? 1 : 0);
        cmd.Parameters.AddWithValue("@fg", playerGuid);
        cmd.ExecuteNonQuery();
    }

    public void UpdateFriendLevel(long playerGuid, long friendGuid, int level)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Friends SET FriendLevel = @lvl WHERE PlayerGUID = @pg AND FriendGUID = @fg";
        cmd.Parameters.AddWithValue("@lvl", level);
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.Parameters.AddWithValue("@fg", friendGuid);
        cmd.ExecuteNonQuery();
    }

    public void SetRelationType(long playerGuid, long friendGuid, byte relationType)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Friends SET RelationType = @rt WHERE PlayerGUID = @pg AND FriendGUID = @fg";
        cmd.Parameters.AddWithValue("@rt", relationType);
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.Parameters.AddWithValue("@fg", friendGuid);
        cmd.ExecuteNonQuery();
    }

    public int GetFriendCount(long playerGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM Friends WHERE PlayerGUID = @pg";
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public Friend? GetFriend(long playerGuid, long friendGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM Friends WHERE PlayerGUID = @pg AND FriendGUID = @fg";
        cmd.Parameters.AddWithValue("@pg", playerGuid);
        cmd.Parameters.AddWithValue("@fg", friendGuid);
        using var reader = cmd.ExecuteReader();
        if (reader.Read()) return MapFriend(reader);
        return null;
    }

    public List<long> GetOwnersHavingFriend(long targetFriendGuid)
    {
        var owners = new List<long>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT PlayerGUID FROM Friends WHERE FriendGUID = @fg";
        cmd.Parameters.AddWithValue("@fg", targetFriendGuid);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) owners.Add(reader.GetInt64(0));
        return owners;
    }

    private static Friend MapFriend(SqliteDataReader reader)
    {
        return new Friend
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            PlayerGUID = reader.GetInt64(reader.GetOrdinal("PlayerGUID")),
            FriendGUID = reader.GetInt64(reader.GetOrdinal("FriendGUID")),
            FriendName = reader.GetString(reader.GetOrdinal("FriendName")),
            IsOnline = reader.GetInt32(reader.GetOrdinal("IsOnline")) == 1,
            FriendLevel = reader.GetInt32(reader.GetOrdinal("FriendLevel")),
            RelationType = reader.GetByte(reader.GetOrdinal("RelationType"))
        };
    }
}
