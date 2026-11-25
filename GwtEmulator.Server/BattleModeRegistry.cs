using System;
using System.Collections.Concurrent;
using GwtEmulator.Protocol;   // v16: TeamResultRow для вкладки «Результат команды»

namespace GwtEmulator.Server;

/// <summary>
/// Bridges the chosen battle mode from HallServer to RoomServer,
/// and carries expected player count for multiplayer matchmaking.
/// </summary>
public static class BattleModeRegistry
{
    private static readonly ConcurrentDictionary<long, ModeEntry>   _byRoom   = new();
    private static readonly ConcurrentDictionary<long, ModeEntry>   _byPlayer = new();
    private static readonly ConcurrentDictionary<long, int>          _expectedPlayers = new();
    private static readonly ConcurrentDictionary<long, bool>         _endedRooms = new();
    private static readonly ConcurrentDictionary<long, ConcurrentDictionary<long, byte>> _roomHumans = new();
    private static readonly ConcurrentDictionary<long, ConcurrentDictionary<long, long>> _roomSquads = new();

    public readonly struct ModeEntry
    {
        public byte     BattleMode  { get; init; }
        public byte     BattleLevel { get; init; }
        public DateTime Stamp       { get; init; }
    }

    public static void Record(long roomGuid, long playerGuid, byte battleMode, byte battleLevel)
    {
        var e = new ModeEntry { BattleMode = battleMode, BattleLevel = battleLevel, Stamp = DateTime.UtcNow };
        _byRoom[roomGuid]     = e;
        _byPlayer[playerGuid] = e;
        _endedRooms.TryRemove(roomGuid, out _);
        _roomHumans.GetOrAdd(roomGuid, _ => new ConcurrentDictionary<long, byte>())[playerGuid] = 1;
        Console.WriteLine($"[ModeRegistry] room={roomGuid} player={playerGuid} mode={battleMode} level={battleLevel}");
    }

    public static byte? Resolve(long roomGuid, long playerGuid)
    {
        if (_byRoom.TryGetValue(roomGuid,   out var e))  return e.BattleMode;
        if (_byPlayer.TryGetValue(playerGuid, out var e2)) return e2.BattleMode;
        return null;
    }

    // ── Multiplayer: expected player count per room ───────────────────────────
    /// <summary>Call from HallServer when creating a multiplayer room.</summary>
    public static void SetExpectedPlayers(long roomGuid, int count)
    {
        _expectedPlayers[roomGuid] = count;
        Console.WriteLine($"[ModeRegistry] room={roomGuid} expectedPlayers={count}");
    }

    /// <summary>Returns how many human players the room should wait for. Defaults to 1.</summary>
    public static int GetExpectedPlayers(long roomGuid) =>
        _expectedPlayers.TryGetValue(roomGuid, out var c) ? c : 1;

    public static void RecordSquad(long roomGuid, long squadId, System.Collections.Generic.IEnumerable<long> memberGuids)
    {
        if (roomGuid == 0 || squadId == 0) return;
        var map = _roomSquads.GetOrAdd(roomGuid, _ => new ConcurrentDictionary<long, long>());
        foreach (var guid in memberGuids)
            map[guid] = squadId;
    }

    public static long GetPlayerSquadId(long roomGuid, long playerGuid)
    {
        if (_roomSquads.TryGetValue(roomGuid, out var map) && map.TryGetValue(playerGuid, out var squadId))
            return squadId;
        return 0;
    }

    public static void MarkEnded(long roomGuid)
    {
        _endedRooms[roomGuid] = true;
        _roomHumans.TryRemove(roomGuid, out _);
        _roomSquads.TryRemove(roomGuid, out _);
    }

    public static bool IsRoomActive(long roomGuid) =>
        _byRoom.ContainsKey(roomGuid) && !_endedRooms.ContainsKey(roomGuid);

    public static int GetRoomHumanCount(long roomGuid) =>
        _roomHumans.TryGetValue(roomGuid, out var dict) ? dict.Count : 0;

    public static void RemoveHuman(long roomGuid, long playerGuid)
    {
        if (_roomHumans.TryGetValue(roomGuid, out var dict))
            dict.TryRemove(playerGuid, out _);
    }

    public static void Clear(long roomGuid, long playerGuid)
    {
        _byRoom.TryRemove(roomGuid,     out _);
        _byPlayer.TryRemove(playerGuid, out _);
        _expectedPlayers.TryRemove(roomGuid, out _);
        _endedRooms.TryRemove(roomGuid, out _);
        _roomHumans.TryRemove(roomGuid, out _);
        _roomSquads.TryRemove(roomGuid, out _);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// v12: пакет послебоевой статистики. RoomServer.EndBattle считает и кладёт сюда,
// HallServer при возврате игрока в ангар (EnterOutNewGuideOrBattle State=4)
// забирает, строит PK_HC_BattleEndInfo / PK_HC_BattleEndInfoPVE и начисляет
// лояльность легиона. Один пакет на игрока, забирается однократно.
// ─────────────────────────────────────────────────────────────────────────────
public sealed class BattleEndResult
{
    public long  RoomGuid;
    public int   MapId;
    public byte  ModeWire;         // серверный байт режима (0/1/2/4) — клиент конвертирует сам
    public byte  Result;           // 1=победа, 0=поражение, 2=ничья (сверено с PrizeExpMoneyShow)
    public byte  LiveState;        // 1 = танк уцелел («Ваш танк не был уничтожен в бою»)
    public int   StartUnix;        // unix-время старта боя (клиент форматирует в дату)
    public int   Money, Exp, VipMoney, VipExp;
    public int   TankId;
    public ushort ShotCount, HitCount, PierceCount, OccupyValue, BeHitCount;
    public int   Damage;
    public byte  Kills, DamagedEnemies;
    public int   LostHp, MaxHp;
    public int   RepairCost, SupplyCost, ShellCost;
    public int   LightDamage;
    public byte  LightCount;
    public int   ChassisShareDamage, PotentialDamage;
    public float GroupReward = 1f;
    public int   LoyalPoint;       // очки лояльности легиона (в PvP-варианте пакета)
    // PVE (оборона): раскладка ровно по клиентскому enum PVEEndInfo (18 значений).
    public int[] PveInfo = new int[18];
    public int   CrewExtraExp;
    // v13: окно уже доставлено клиенту из EndBattle (Hall не дублирует).
    public bool  SentToClient;
    // v16: вкладка «Результат команды» (PK_HC_TeamBattleResult) и список жертв.
    public System.Collections.Generic.List<TeamResultRow> TeamRows = new();
    public System.Collections.Generic.List<(string Name, int TankId, int DamageCount,
        int DestroyModules, byte KillOrDamage, int Damage, byte IsLight, byte IsLighted,
        int LightDamage, int ShareDamage)> Victims = new();
}

public static class BattleEndRegistry
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, BattleEndResult> _results = new();
    public static void Store(long playerGuid, BattleEndResult r) => _results[playerGuid] = r;
    public static BattleEndResult? Take(long playerGuid) =>
        _results.TryRemove(playerGuid, out var r) ? r : null;
}
