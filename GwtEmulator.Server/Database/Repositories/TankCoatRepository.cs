using System;
using System.Collections.Generic;
using GwtEmulator.Protocol;

namespace GwtEmulator.Server.Database.Repositories;

/// <summary>
/// Камуфляжи танка. Модель ровно как у клиента (TankOwnCoat):
/// у каждого танка ТРИ слота — 0 = Ice (снежный), 1 = Field (полевой),
/// 2 = Desert (пустынный); в каждом лежит максимум один купленный камуфляж.
/// Отдельно хранится, какой слот сейчас НАДЕТ (Tanks.CurCoatSlot, 3 = None).
///
/// Слот определяется НЕ игроком, а колонкой suit_maptype в Table_Cloth: снежный
/// камуфляж всегда ложится в слот 0 и т.д. Клиент присылает его в CoatSlotIndex.
/// </summary>
public class TankCoatRepository
{
    public const int Slots    = PK_TankCoatInfo.CoatSlots; // 3
    public const int SlotNone = 3;                          // EnTankCoatType.None

    private readonly DatabaseService _db;

    public TankCoatRepository(DatabaseService db) => _db = db;

    /// <summary>Один слот камуфляжа. OverdueUnix = 0 → куплен навсегда.</summary>
    public record CoatSlot(int CoatTableId, int OverdueUnix)
    {
        public bool IsEmpty   => CoatTableId <= 0;
        public bool IsForever => OverdueUnix == 0;
        public bool IsExpired(long nowUnix) => !IsEmpty && !IsForever && OverdueUnix <= nowUnix;
    }

    private static CoatSlot[] EmptySlots()
    {
        var arr = new CoatSlot[Slots];
        for (int i = 0; i < Slots; i++) arr[i] = new CoatSlot(0, 0);
        return arr;
    }

    /// <summary>
    /// Читает все три слота. Просроченные аренды (7/30 дней) вычищаются прямо здесь,
    /// чтобы клиент никогда не увидел камуфляж с истёкшим сроком.
    /// </summary>
    public CoatSlot[] GetCoats(long tankGuid)
    {
        var slots = EmptySlots();
        long now  = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expired = new List<int>();

        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT SlotIndex, CoatTableId, OverdueUnix FROM TankCoats WHERE TankGuid = @guid";
            cmd.Parameters.AddWithValue("@guid", tankGuid);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int slot = reader.GetInt32(0);
                if (slot < 0 || slot >= Slots) continue;
                var entry = new CoatSlot(reader.GetInt32(1), reader.GetInt32(2));
                if (entry.IsExpired(now)) { expired.Add(slot); continue; }
                slots[slot] = entry;
            }
        }

        foreach (int slot in expired) ClearSlot(tankGuid, slot);
        return slots;
    }

    public void SetSlot(long tankGuid, int slot, int coatTableId, int overdueUnix)
    {
        if (slot < 0 || slot >= Slots) return;
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO TankCoats (TankGuid, SlotIndex, CoatTableId, OverdueUnix)
            VALUES (@guid, @slot, @coat, @overdue)
            ON CONFLICT(TankGuid, SlotIndex)
            DO UPDATE SET CoatTableId = @coat, OverdueUnix = @overdue";
        cmd.Parameters.AddWithValue("@guid", tankGuid);
        cmd.Parameters.AddWithValue("@slot", slot);
        cmd.Parameters.AddWithValue("@coat", coatTableId);
        cmd.Parameters.AddWithValue("@overdue", overdueUnix);
        cmd.ExecuteNonQuery();
    }

    public void ClearSlot(long tankGuid, int slot)
    {
        if (slot < 0 || slot >= Slots) return;
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM TankCoats WHERE TankGuid = @guid AND SlotIndex = @slot";
        cmd.Parameters.AddWithValue("@guid", tankGuid);
        cmd.Parameters.AddWithValue("@slot", slot);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Надетый слот: 0..2, либо 3 (None). Значения вне диапазона → None.</summary>
    public int GetCurSlot(long tankGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT CurCoatSlot FROM Tanks WHERE TankGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", tankGuid);
        object? raw = cmd.ExecuteScalar();
        if (raw == null || raw == DBNull.Value) return SlotNone;
        int slot = Convert.ToInt32(raw);
        return (slot < 0 || slot >= Slots) ? SlotNone : slot;
    }

    /// <summary>
    /// Пишет надетый слот и синхронно — CurTankCoatId/CurRoomTankCoatID, откуда
    /// RoomServer берёт камуфляж при заходе в бой (см. BuildPlayerInfo).
    /// </summary>
    public void SetCurSlot(long tankGuid, int slot, int coatTableId)
    {
        if (slot < 0 || slot >= Slots) { slot = SlotNone; coatTableId = 0; }
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE Tanks
               SET CurCoatSlot = @slot, CurTankCoatId = @coat, CurRoomTankCoatID = @coat
             WHERE TankGUID = @guid";
        cmd.Parameters.AddWithValue("@guid", tankGuid);
        cmd.Parameters.AddWithValue("@slot", slot);
        cmd.Parameters.AddWithValue("@coat", coatTableId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>В каком слоте лежит этот камуфляж, или -1.</summary>
    public int FindSlotOf(long tankGuid, int coatTableId)
    {
        if (coatTableId <= 0) return -1;
        var slots = GetCoats(tankGuid);
        for (int i = 0; i < Slots; i++)
            if (slots[i].CoatTableId == coatTableId) return i;
        return -1;
    }
}
