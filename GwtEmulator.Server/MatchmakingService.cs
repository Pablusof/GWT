using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GwtEmulator.Server.Database.Repositories;
using GwtEmulator.Server.GameTables;

namespace GwtEmulator.Server;

public class MatchmakingService
{
    private readonly TankRepository _tankRepo;
    private readonly GameTablesService _gameTables;
    private readonly ConcurrentDictionary<long, MatchmakingEntry> _queue = new();
    private readonly Random _random = new();
    private Timer? _matchTimer;
    private long _nextRoomGuid = 100000;

    public MatchmakingService(TankRepository tankRepo, GameTablesService gameTables)
    {
        _tankRepo = tankRepo;
        _gameTables = gameTables;
        _matchTimer = new Timer(TryMatch, null, 5000, 5000);
    }

    public void Enqueue(long playerGuid, byte battleMode, byte battleLevel)
    {
        var tanks = _tankRepo.GetPlayerTanks(playerGuid);
        var battleTank = tanks.FirstOrDefault(t => t.IsBattleTank) ?? tanks.FirstOrDefault();
        var tankEntry = battleTank != null ? _gameTables.GetTank(battleTank.VehicleId) : null;
        byte tier = (byte)(tankEntry?.Tier ?? 1);

        _queue[playerGuid] = new MatchmakingEntry
        {
            PlayerGUID = playerGuid,
            BattleMode = battleMode,
            BattleLevel = battleLevel,
            Tier = tier,
            QueueTime = DateTime.UtcNow
        };

        Console.WriteLine($"[MM] Enqueued: {playerGuid} mode={battleMode} tier={tier}");
    }

    public void Dequeue(long playerGuid)
    {
        _queue.TryRemove(playerGuid, out _);
        Console.WriteLine($"[MM] Dequeued: {playerGuid}");
    }

    public bool IsInQueue(long playerGuid) => _queue.ContainsKey(playerGuid);

    public int GetQueueSize() => _queue.Count;

    public int GetEstimatedWaitTime(long playerGuid)
    {
        if (!_queue.TryGetValue(playerGuid, out var entry)) return 0;
        return (int)(DateTime.UtcNow - entry.QueueTime).TotalSeconds;
    }

    private void TryMatch(object? state)
    {
        var entries = _queue.Values.ToList();
        if (entries.Count < 2) return;

        var modeGroups = entries.GroupBy(e => e.BattleMode).ToList();

        foreach (var modeGroup in modeGroups)
        {
            var players = modeGroup.OrderBy(e => e.QueueTime).ToList();
            if (players.Count < 2) continue;

            var tierGroups = players.GroupBy(e => e.Tier).ToList();

            List<MatchmakingEntry> matched = new();

            foreach (var tierGroup in tierGroups)
            {
                if (tierGroup.Count() >= 2)
                {
                    matched.AddRange(tierGroup.Take(Math.Min(tierGroup.Count(), 8)));
                }
            }

            if (matched.Count < 2)
            {
                var allSorted = players.OrderBy(e => e.Tier).ToList();
                for (int i = 0; i < allSorted.Count; i++)
                {
                    if (matched.Count >= 8) break;
                    if (matched.Count == 0 || Math.Abs(matched[0].Tier - allSorted[i].Tier) <= 2)
                    {
                        matched.Add(allSorted[i]);
                    }
                }
            }

            if (matched.Count >= 2)
            {
                long roomGuid = _nextRoomGuid++;
                var team1 = matched.Where((_, i) => i % 2 == 0).ToList();
                var team2 = matched.Where((_, i) => i % 2 == 1).ToList();

                Console.WriteLine($"[MM] Match found! Room={roomGuid} Team1={team1.Count} Team2={team2.Count}");

                foreach (var entry in matched)
                {
                    _queue.TryRemove(entry.PlayerGUID, out _);
                    OnMatchFound?.Invoke(entry.PlayerGUID, roomGuid, entry.BattleMode, entry.BattleLevel, entry.Tier);
                }
            }
        }
    }

    public event Action<long, long, byte, byte, byte>? OnMatchFound;

    public void Stop() => _matchTimer?.Dispose();
}

public class MatchmakingEntry
{
    public long PlayerGUID { get; set; }
    public byte BattleMode { get; set; }
    public byte BattleLevel { get; set; }
    public byte Tier { get; set; }
    public DateTime QueueTime { get; set; }
}
