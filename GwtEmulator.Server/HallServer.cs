using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using GwtEmulator.Protocol;
using GwtEmulator.Server.Database;
using GwtEmulator.Server.Database.Models;
using GwtEmulator.Server.Database.Repositories;
using GwtEmulator.Server.GameTables;

namespace GwtEmulator.Server;

public class HallServer : GameServer
{
    private const byte ItemTypeVehicle = 100;
    private const byte ItemTypeEquipment = 101;
    private const byte ItemTypeCrew = 102;   // экипаж из рулетки (тип 7)
    private const byte ItemTypeShell = 102;
    private const byte ItemTypeProvision = 103;

    // IP address the client is told to connect to for the RoomServer (battle server).
    // For solo play this is 127.0.0.1. For LAN multiplayer, set the GWT_PUBLIC_IP
    // environment variable to the host machine's LAN IP (e.g. 192.168.1.50) before
    // launching the server, and make sure each client's ClientConfig.tk points GameIP/
    // AuthorizeIP at that same host IP. The server already binds 0.0.0.0 (all NICs),
    // so the only thing that has to change for LAN play is this advertised address.
    // ── Multiplayer matchmaking ──────────────────────────────────────────────
    // For Respawn and Defense modes: queue the first player until a second one
    // queues for the same mode, then send both the same RoomGUID.
    private sealed record MatchEntry(
        GameSession Session, long PlayerGuid, long TankGuid,
        long RoomGuid, byte WireMode, byte NormMode, byte Level, int Tier,
        CancellationTokenSource? WaitCts = null)
    {
        public DateTime QueuedAt { get; init; } = DateTime.UtcNow;
    }

    private sealed class MatchLobby
    {
        public required long RoomGuid { get; init; }
        public required byte NormMode { get; init; }
        public required CancellationTokenSource WaitCts { get; init; }
        public List<MatchEntry> Entries { get; } = new();
    }

    // Waiting lobby per normalized mode (collects all players queueing within 20s) + active open rooms for hot-join.
    private static readonly Dictionary<byte, MatchLobby>  _waitingLobbies    = new();
    private static readonly Dictionary<byte, MatchEntry>  _activeRoomsByMode = new();
    private static readonly object                        _matchLock         = new();
    // v12: игроки, нажавшие «ВЫХОД» на экране «Подбор команды» — их отложенный JoinRoomRes отменяется.
    private static readonly HashSet<long>                 _queueCancelled = new();
    // v13: холл-сессии по GUID — RoomServer шлёт через них окно «Статистика боя»
    // СРАЗУ при конце боя, пока клиент ещё в сцене Room (иначе окно не открывается:
    // клиент ставит CurShowPrizeData только в Room-ветке HandlePrizeData).
    private static readonly ConcurrentDictionary<long, GameSession> _hallByGuid = new();
    public static GameSession? GetHallSession(long playerGuid) =>
        _hallByGuid.TryGetValue(playerGuid, out var s) && s.IsConnected ? s : null;

    /// <summary>
    /// Returns true if an account with the given PlayerGUID already has a live HallServer
    /// connection from a different IP address (used by LoginServer to avoid two friends
    /// colliding on the same PlayerGUID and kicking each other out of matches).
    /// </summary>
    public static bool IsGuidOnlineFromOtherIp(long playerGuid, string? remoteEndPoint)
    {
        if (!_hallByGuid.TryGetValue(playerGuid, out var existing) || !existing.IsConnected)
            return false;
        string ipA = ExtractBareIp(existing.RemoteEndPoint);
        string ipB = ExtractBareIp(remoteEndPoint);
        if (string.IsNullOrEmpty(ipA) || string.IsNullOrEmpty(ipB))
            return false;
        return !string.Equals(ipA, ipB, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractBareIp(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return string.Empty;
        int colon = endpoint.LastIndexOf(':');
        return colon > 0 ? endpoint[..colon].Trim() : endpoint.Trim();
    }

    // v: общая логика сборки и отправки ВСЕЙ статистики игрока (основная вкладка +
    // Revive/Commander/PVE + "карточка статистики" AccountFightStatistics). Раньше
    // это считалось только один раз при HandleHallLogin — после боёв (которые идут
    // через RoomServer, тот же Hall-сокет остаётся открытым весь сеанс) клиент никогда
    // не получал обновлённые цифры, пока не переподключался. Теперь RoomServer.EndBattle
    // тоже вызывает этот метод сразу после каждого боя (через уже существующий канал
    // "push через holl-сессию", которым уже пользуется окно результатов боя).
    // v: HandleAccountStatisticsProxy на клиенте (AccountProfilesManager) принимает
    // ВТОРОЙ и последующие пакеты этого семейства ТОЛЬКО если msg.ItemCount == 2
    // ровно (self-сводка + один танк) — иначе тихо возвращает null и весь bucket
    // (_apDataNml/_apDataRvv/_apDataCommander/_apDataPVE) остаётся null до
    // переподключения. Именно это и есть причина бага «новый танк не выбирается
    // до перезахода»: Revive/Commander/PVE всегда писали ItemCount=0 на проводе
    // (client.ItemCount=1 != 2) — то есть ЛЮБОЙ повторный (после первого боя)
    // вызов PushAccountStatistics гарантированно обнулял все три bucket'а, а
    // FillPersonalProfilesWhenUpdateTankInfo() (вызывается на КАЖДЫЙ AddTank)
    // падает с NullReferenceException прямо на первом обращении к
    // personalProfiles.DataItemList — клиент не успевает вызвать
    // TankTechData.BroadcastBaseDataChange(...), из-за чего новая плитка танка не
    // регистрирует клик как надо, пока не будет полного релогина (когда bucket'ы
    // пересоздаются заново с нуля через ветку if(personalProfiles==null)).
    //
    // Поэтому: при ЛОГИНЕ (justPlayedVehicleId == null) шлём полный снимок как
    // раньше — это ПЕРВЫЙ пакет за сессию, клиент принимает любой ItemCount.
    // После боя (justPlayedVehicleId задан) шлём ТОЛЬКО основную вкладку — и
    // ровно с одной дополнительной записью (только что сыгранный танк), это
    // единственная форма, которую клиент готов принять повторно. Revive/
    // Commander/PVE после боя НЕ шлём вовсе (для них нет данных по конкретному
    // танку, а хардкодить ItemCount=0 снова — гарантированно всё сломает);
    // их значения просто останутся такими, какими были на момент логина, до
    // следующего захода — это гораздо безопаснее, чем ронять клиент.
    internal static void PushAccountStatistics(GameSession session, long playerGuid,
        AccountRepository accountRepo, BattleStatsRepository battleStatsRepo,
        ModeStatsRepository modeStatsRepo, TankRepository tankRepo,
        int? justPlayedVehicleId = null)
    {
        var account = accountRepo.GetByPlayerGUID(playerGuid);
        if (account == null) return;

        var creationTimeUnix = (uint)new DateTimeOffset(account.CreatedAt).ToUnixTimeSeconds();
        var tankStatRows = battleStatsRepo.GetPlayerStats(playerGuid);
        int totBattles = tankStatRows.Sum(r => r.BattleCount);
        int totWins    = tankStatRows.Sum(r => r.WinCount);
        int totKills   = tankStatRows.Sum(r => r.KillNum);
        int totDamage  = tankStatRows.Sum(r => r.Damage);
        int totShots   = tankStatRows.Sum(r => r.ShootNum);
        int totHits    = tankStatRows.Sum(r => r.HitNum);
        int totSurvive = tankStatRows.Sum(r => Math.Max(0, r.BattleCount - r.DeathNum));
        var bestKillRow = tankStatRows.OrderByDescending(r => r.MaxKillInBattle).FirstOrDefault();
        var bestExpRow  = tankStatRows.OrderByDescending(r => r.MaxExpInBattle).FirstOrDefault();
        int MapVehicleId(BattleStats r) => tankRepo.GetTankByGUID(r.TankGUID)?.VehicleId ?? 0;

        AccountStatisticItem MakeItem(BattleStats r) => new AccountStatisticItem
        {
            VehicleId = MapVehicleId(r),
            BattleCount = r.BattleCount,
            WinCount = r.WinCount,
            LoseCount = Math.Max(0, r.BattleCount - r.WinCount),
            SurviveCount = Math.Max(0, r.BattleCount - r.DeathNum),
            TotalKillCount = r.KillNum,
            MaxKillCount = r.MaxKillInBattle,
            TotalDamage = r.Damage,
            TotalExp = r.ExpTotal,
            ShootCount = r.ShootNum,
            HitCount = r.HitNum,
            MaxExp = r.MaxExpInBattle,
            MedalLevel = 0,
        };

        // v.fix3 (настоящая причина бага «нельзя нажать на Статистику»): формат пакета
        // PK_HC_AccountStatistic на клиенте (ClientSocket/PK_HC_AccountStatistic.ReadByte)
        // устроен НЕ так, как можно подумать по составу полей. Клиент:
        //   1) сам синтезирует ItemList[0] с VehicleId=0, ЧИТАЯ поля из ЗАГОЛОВКА пакета
        //      (наши Total* поля — BattleCount, WinCount, ..., MaxExp) — то есть эти
        //      Total*-поля УЖЕ И ЕСТЬ сводная (aggregate) запись, отдельно её слать не нужно;
        //   2) читает "сырое" ItemCount с провода и присваивает msg.ItemCount = сырое+1
        //      (единица — это уже готовый ItemList[0] из заголовка);
        //   3) в цикле i=1..msg.ItemCount-1 читает РОВНО "сырое" (msg.ItemCount-1) структур
        //      AccProItemForNmlRvv с провода — то есть ровно столько, сколько мы укажем в
        //      поле ItemCount пакета (см. PK_HC_AccountStatistic.WriteByte: цикл на сервере
        //      идёт `for i < ItemCount`, без какого-либо +1).
        // Раньше здесь ItemCount слался как "танков+1", а в TankStats дополнительно ещё раз
        // клался MakeAggregateItem() как явный элемент [0] массива. В результате клиент
        // получал ДВА элемента с VehicleId=0: свой синтезированный ItemList[0] (из Total*)
        // и наш лишний MakeAggregateItem(), прочитанный с провода как ItemList[1] — отсюда
        // ArgumentException в Dictionary.Add при первой же попытке открыть панель
        // статистики. Правильно: ItemCount = ЧИСЛО РЕАЛЬНЫХ ТАНКОВ (без +1 и без агрегата
        // в массиве), TankStats = только настоящие танковые записи, без сводной.
        if (justPlayedVehicleId is int playedVid && playedVid != 0)
        {
            // Инкрементальное обновление после боя: на проводе — РОВНО 1 танковая запись
            // (сыгранный танк). Клиент досчитает msg.ItemCount = 1+1 = 2 и, увидев
            // itemCount==2, обновит dataItemList[0] (агрегат, из заголовка) и найдёт/добавит
            // запись танка по VehicleId — это и есть та самая проверка "itemCount != 2" в
            // HandleAccountStatisticsProxy.
            var playedRow = tankStatRows.FirstOrDefault(r => MapVehicleId(r) == playedVid);
            var playedItem = playedRow != null
                ? MakeItem(playedRow)
                : new AccountStatisticItem { VehicleId = playedVid };
            session.Send(new PK_HC_AccountStatistic
            {
                PlayerName = account.PlayerName,
                CreationTime = creationTimeUnix,
                MaxKillVehicleId = bestKillRow != null ? MapVehicleId(bestKillRow) : 0,
                MaxExpVehicleId = bestExpRow != null ? MapVehicleId(bestExpRow) : 0,
                TotalBattleCount = totBattles,
                TotalWinCount = totWins,
                TotalLoseCount = Math.Max(0, totBattles - totWins),
                TotalSurviveCount = totSurvive,
                TotalKillCount = totKills,
                TotalMaxKillCount = bestKillRow?.MaxKillInBattle ?? 0,
                TotalDamage = totDamage,
                TotalExp = account.Experience,
                TotalShootCount = totShots,
                TotalHitCount = totHits,
                TotalMaxExp = bestExpRow?.MaxExpInBattle ?? 0,
                ItemCount = 1,
                TankStats = new[] { playedItem }
            });
        }
        else if (justPlayedVehicleId == null)
        {
            // Полный снимок при логине: сводная запись едет ТОЛЬКО через заголовочные
            // Total*-поля (клиент сам построит из них ItemList[0]). TankStats содержит
            // ТОЛЬКО настоящие танки — без явной сводной записи и без VehicleId=0.
            // На всякий случай (если BattleStats ссылается на уже несуществующий/
            // непроконвертированный TankGUID) выбрасываем строки с VehicleId==0 и
            // схлопываем возможные дубли по VehicleId (например, если танк продали и
            // купили заново того же типа под новым TankGUID) — это не даёт клиенту
            // получить два элемента с одинаковым VehicleId в цикле чтения.
            var tankItems = tankStatRows
                .Select(MakeItem)
                .Where(item => item.VehicleId != 0)
                .GroupBy(item => item.VehicleId)
                .Select(g => g.Aggregate((a, b) => new AccountStatisticItem
                {
                    VehicleId = a.VehicleId,
                    BattleCount = a.BattleCount + b.BattleCount,
                    WinCount = a.WinCount + b.WinCount,
                    LoseCount = a.LoseCount + b.LoseCount,
                    SurviveCount = a.SurviveCount + b.SurviveCount,
                    TotalKillCount = a.TotalKillCount + b.TotalKillCount,
                    MaxKillCount = Math.Max(a.MaxKillCount, b.MaxKillCount),
                    TotalDamage = a.TotalDamage + b.TotalDamage,
                    TotalExp = a.TotalExp + b.TotalExp,
                    ShootCount = a.ShootCount + b.ShootCount,
                    HitCount = a.HitCount + b.HitCount,
                    MaxExp = Math.Max(a.MaxExp, b.MaxExp),
                    MedalLevel = 0,
                }))
                .ToArray();

            session.Send(new PK_HC_AccountStatistic
            {
                PlayerName = account.PlayerName,
                CreationTime = creationTimeUnix,
                MaxKillVehicleId = bestKillRow != null ? MapVehicleId(bestKillRow) : 0,
                MaxExpVehicleId = bestExpRow != null ? MapVehicleId(bestExpRow) : 0,
                TotalBattleCount = totBattles,
                TotalWinCount = totWins,
                TotalLoseCount = Math.Max(0, totBattles - totWins),
                TotalSurviveCount = totSurvive,
                TotalKillCount = totKills,
                TotalMaxKillCount = bestKillRow?.MaxKillInBattle ?? 0,
                TotalDamage = totDamage,
                TotalExp = account.Experience,
                TotalShootCount = totShots,
                TotalHitCount = totHits,
                TotalMaxExp = bestExpRow?.MaxExpInBattle ?? 0,
                ItemCount = tankItems.Length,
                TankStats = tankItems
            });

            var reviveStats    = modeStatsRepo.GetByPlayerAndMode(playerGuid, 1);
            var commanderStats = modeStatsRepo.GetByPlayerAndMode(playerGuid, 2);
            var pveStats       = modeStatsRepo.GetByPlayerAndMode(playerGuid, 4);

            session.Send(new PK_HC_AccountStatisticRevive
            {
                PlayerName = account.PlayerName,
                CreationTime = creationTimeUnix,
                BattleCount = reviveStats?.BattleCount ?? 0,
                WinCount = reviveStats?.WinCount ?? 0,
                LoseCount = Math.Max(0, (reviveStats?.BattleCount ?? 0) - (reviveStats?.WinCount ?? 0)),
                SurviveCount = Math.Max(0, (reviveStats?.BattleCount ?? 0) - (reviveStats?.DeathNum ?? 0)),
                TotalKillCount = reviveStats?.KillNum ?? 0,
                MaxKillCount = reviveStats?.MaxKillInBattle ?? 0,
                TotalDamage = reviveStats?.Damage ?? 0,
                TotalExp = reviveStats?.ExpTotal ?? 0,
                ShootCount = reviveStats?.ShootNum ?? 0,
                HitCount = reviveStats?.HitNum ?? 0,
                MaxExp = reviveStats?.MaxExpInBattle ?? 0,
            });
            session.Send(new PK_HC_AccountStatisticCommander
            {
                PlayerName = account.PlayerName,
                CreationTime = creationTimeUnix,
                BattleCount = commanderStats?.BattleCount ?? 0,
                WinCount = commanderStats?.WinCount ?? 0,
                LoseCount = Math.Max(0, (commanderStats?.BattleCount ?? 0) - (commanderStats?.WinCount ?? 0)),
                SurviveCount = Math.Max(0, (commanderStats?.BattleCount ?? 0) - (commanderStats?.DeathNum ?? 0)),
                TotalKillCount = commanderStats?.KillNum ?? 0,
                MaxKillCount = commanderStats?.MaxKillInBattle ?? 0,
                TotalDamage = commanderStats?.Damage ?? 0,
                TotalExp = commanderStats?.ExpTotal ?? 0,
                ShootCount = commanderStats?.ShootNum ?? 0,
                HitCount = commanderStats?.HitNum ?? 0,
                MaxExp = commanderStats?.MaxExpInBattle ?? 0,
            });
            session.Send(new PK_HC_AccountStatisticPVE
            {
                PlayerName = account.PlayerName,
                CreationTime = creationTimeUnix,
                BattleCount = pveStats?.BattleCount ?? 0,
                WinCount = pveStats?.WinCount ?? 0,
                TotalKill = pveStats?.KillNum ?? 0,
                MaxKillOneBattle = pveStats?.MaxKillInBattle ?? 0,
                TotalScore = pveStats?.ScoreTotal ?? 0,
                MaxScoreOneBattle = pveStats?.MaxScoreInBattle ?? 0,
                TotalExp = pveStats?.ExpTotal ?? 0,
                MaxExpOneBattle = pveStats?.MaxExpInBattle ?? 0,
            });
        }
        // justPlayedVehicleId == 0 (танк не сматчился с таблицей танков) — ничего
        // не шлём вообще, чтобы не рисковать неверным ItemCount.

        int fsBattles = totBattles;
        int fsWinRate = fsBattles > 0 ? (int)(100L * totWins / fsBattles) : 0;
        int fsAvgExp  = fsBattles > 0 ? (int)(account.Experience / (long)Math.Max(1, fsBattles)) : 0;
        int fsMaxExp  = bestExpRow?.MaxExpInBattle ?? 0;
        int fsAvgHurt = fsBattles > 0 ? (int)(totDamage / (long)fsBattles) : 0;
        int fsMaxKill = bestKillRow?.MaxKillInBattle ?? 0;
        int fsAvgKill = fsBattles > 0 ? (int)(100L * totKills / fsBattles) : 0; // клиент делит на 100
        int fsEffect = fsBattles > 0
            ? (int)((totDamage / (long)fsBattles) / 10 + (100L * totKills / fsBattles) + (totHits / (long)Math.Max(1, fsBattles)))
            : 0;
        var favoriteTankIds = tankStatRows
            .OrderByDescending(r => r.BattleCount)
            .Take(5)
            .Select(r => MapVehicleId(r))
            .Where(id => id != 0)
            .ToArray();

        session.Send(new PK_HC_AccountFightStatistics
        {
            Rank = 0,
            WinRate = fsWinRate,
            FightTimes = fsBattles,
            AvgExp = fsAvgExp,
            MaxExp = fsMaxExp,
            LightTimes = 0,
            AvgHurt = fsAvgHurt,
            MaxKill = fsMaxKill,
            AvgKill = fsAvgKill,
            Effect = fsEffect,
            TankIds = favoriteTankIds,
        });
    }

    // All game modes support real-player matchmaking (0=Capture, 1=Respawn, 2=Commander, 3/6=Simplified, 4/5=Defense)
    private static bool IsMultiplayerMode(byte rawMode) => true;

    private static byte NormalizeMatchMode(byte rawMode) => rawMode switch
    {
        4 or 5 => 4, // Defense
        3 or 6 => 6, // Simplified
        _ => rawMode,
    };

    // ── Multiplayer matchmaking ──────────────────────────────────────────────
    private static readonly string? ExplicitRoomServerIp =
        Environment.GetEnvironmentVariable("GWT_PUBLIC_IP") is { Length: > 0 } ip ? ip.Trim() : null;

    private static string ResolveRoomServerIp(GameSession session) =>
        ExplicitRoomServerIp ?? session.LocalIpAddress;

    private readonly DatabaseService _db;
    private readonly AccountRepository _accountRepo;
    private readonly TankRepository _tankRepo;
    private readonly InventoryRepository _inventoryRepo;
    private readonly TankEquipmentRepository _tankEquipmentRepo;
    // v30 (камуфляжи): собственный репозиторий, поднимается из _db — App-обвязку не трогаем.
    private readonly TankCoatRepository _tankCoatRepo;
    // v77: та же лёгкая схема — статистика боёв (BattleStatsRepository) уже существовала
    // и писалась после каждого боя (RoomServer.cs), но здесь, в холле, никогда не
    // читалась — окно «Статистика» всегда слало захардкоженные нули.
    private readonly BattleStatsRepository _battleStatsRepo;
    // Пер-режимная статистика (Respawn/Commander/Defense) для вкладок профиля
    // AccountStatisticRevive/Commander/PVE — раньше всегда слались нулями.
    private readonly ModeStatsRepository _modeStatsRepo;
    private readonly CrewRepository _crewRepo;
    private readonly CorpsRepository _corpsRepo;
    private readonly FriendRepository _friendRepo;
    private readonly TournamentRepository _tournamentRepo;
    private readonly MatchmakingService _matchmaking;
    private readonly GameTablesService _gameTables;
    private readonly ModuleRepository? _moduleRepo;
    private readonly Random _random = new();
    private Timer? _keepaliveTimer;


    // Corps tank list — order MUST match the client's CorpsTankId numbering.
    // The client sends CorpsTankId = 1-based index into THIS array (not the vehicle ID).
    // v47: порядок выверен по КЛИЕНТСКОЙ таблице Table_Corps_tank (_corpsTankArray,
    // level0 path_id=207): поле id → поле tank_id. Раньше массив был в другом порядке,
    // из-за чего изучение выдавало ЧУЖИЕ танки (id6 давал FV221 вместо Обj280 и т.п.).
    // Столбцы client-таблицы: develop=лоялти(навсегда), need_prev_exp=опыт(навсегда),
    // corp_level=требуемый уровень легиона.
    private static readonly CorpsTankItem[] LegionTanks = new CorpsTankItem[]
    {
        // [0] CorpsTankId=1  → tank_id 119
        new() { TankId = 119, Nation = 5, Tier = 9,  ExpCost = 390000, LoyaltyCost = 58500,  StudyStatus = 0 }, // Chatillon 25t
        // [1] CorpsTankId=2  → tank_id 154
        new() { TankId = 154, Nation = 4, Tier = 10, ExpCost = 584800, LoyaltyCost = 84000,  StudyStatus = 0 }, // FV4005
        // [2] CorpsTankId=3  → tank_id 152
        new() { TankId = 152, Nation = 4, Tier = 5,  ExpCost = 106220, LoyaltyCost = 3600,   StudyStatus = 0 }, // A33 Excelsior
        // [3] CorpsTankId=4  → tank_id 153
        new() { TankId = 153, Nation = 5, Tier = 8,  ExpCost = 210500, LoyaltyCost = 7680,   StudyStatus = 0 }, // AMX50 100
        // [4] CorpsTankId=5  → tank_id 162
        new() { TankId = 162, Nation = 1, Tier = 8,  ExpCost = 300700, LoyaltyCost = 10000,  StudyStatus = 0 }, // IS-6
        // [5] CorpsTankId=6  → tank_id 164
        new() { TankId = 164, Nation = 1, Tier = 10, ExpCost = 835500, LoyaltyCost = 120000, StudyStatus = 0 }, // Object 280
        // [6] CorpsTankId=7  → tank_id 165
        new() { TankId = 165, Nation = 4, Tier = 7,  ExpCost = 165000, LoyaltyCost = 6000,   StudyStatus = 0 }, // A30
        // [7] CorpsTankId=8  → tank_id 166
        new() { TankId = 166, Nation = 5, Tier = 10, ExpCost = 835500, LoyaltyCost = 120000, StudyStatus = 0 }, // AMX30
        // [8] CorpsTankId=9  → tank_id 170  (corp_level 3)
        new() { TankId = 170, Nation = 5, Tier = 10, ExpCost = 487300, LoyaltyCost = 70000,  StudyStatus = 0 }, // Mars 15
        // [9] CorpsTankId=10 → tank_id 171  (corp_level 2)
        new() { TankId = 171, Nation = 4, Tier = 8,  ExpCost = 350700, LoyaltyCost = 12000,  StudyStatus = 0 }, // FV221
        // [10] CorpsTankId=11 → tank_id 45
        new() { TankId = 45,  Nation = 1, Tier = 4,  ExpCost = 88000,  LoyaltyCost = 3000,   StudyStatus = 0 }, // T-50
        // [11] CorpsTankId=12 → tank_id 201  (corp_level 4)
        new() { TankId = 201, Nation = 3, Tier = 11, ExpCost = 167500, LoyaltyCost = 40000,  StudyStatus = 0 }, // Starship
    };
    public HallServer(int port, DatabaseService db, AccountRepository accountRepo, TankRepository tankRepo,
        InventoryRepository inventoryRepo, TankEquipmentRepository tankEquipmentRepo, CrewRepository crewRepo,
        CorpsRepository corpsRepo, FriendRepository friendRepo,
        TournamentRepository tournamentRepo,
        MatchmakingService matchmaking,
        GameTablesService gameTables,
        ModuleRepository? moduleRepo = null) : base("HS", port)
    {
        _db = db;
        _accountRepo = accountRepo;
        _tankRepo = tankRepo;
        _inventoryRepo = inventoryRepo;
        _tankEquipmentRepo = tankEquipmentRepo;
        _tankCoatRepo = new TankCoatRepository(db);
        _battleStatsRepo = new BattleStatsRepository(db);
        _modeStatsRepo = new ModeStatsRepository(db);
        _crewRepo = crewRepo;
        _corpsRepo = corpsRepo;
        _friendRepo = friendRepo;
        _tournamentRepo = tournamentRepo;
        _matchmaking = matchmaking;
        _gameTables = gameTables;
        _moduleRepo = moduleRepo;

        _matchmaking.OnMatchFound += OnMatchFound;

        // Главный приз рулетки: строка reward_level=1 в таблице призов (bonusboxes.json),
        // та же, что на витрине. Ротируется скриптом rotate_showcase.py между сессиями.
        int mainId = _gameTables.Bonusboxes.Values
            .Where(b => b.IsMainPrize).Select(b => b.RewardItemId).FirstOrDefault();
        if (mainId <= 0) mainId = FallbackMainPrizeId;
        var mainTank = _gameTables.GetTank(mainId);
        Console.WriteLine($"[Lottery] Главный приз: vehicleId={mainId} ({mainTank?.Name ?? "?"}). "
                        + $"Витрину (картинку справа) ротирует rotate_showcase.py между запусками игры; "
                        + $"сервер выдаёт танк из строки reward_level=1 таблицы.");
    }

    public override void Start(string bindIp = "0.0.0.0")
    {
        base.Start(bindIp);
        _keepaliveTimer = new Timer(_ =>
        {
            List<GameSession> sessionsCopy;
            lock (_sessions)
                sessionsCopy = _sessions.Values.ToList();

            foreach (var s in sessionsCopy)
            {
                try
                {
                    if (s.IsConnected && s.PlayMainDelivered)
                    {
                        SendServerTimePacket(s);
                    }
                }
                catch { }
            }
        }, null, 20000, 20000);
    }

    public override void Stop()
    {
        _keepaliveTimer?.Dispose();
        base.Stop();
    }

    private void OnMatchFound(long playerGuid, long roomGuid, byte battleMode, byte battleLevel, byte tier)
    {
        Console.WriteLine($"[HS] Match found for {playerGuid}: Room={roomGuid} Mode={battleMode}");
    }

    protected override GameSession CreateSession(TcpClient client) => new(client);
    protected override void OnClientConnected(GameSession session) { }

    protected override void OnMessageReceived(GameSession session, IRequest message)
    {
        try
        {

        switch (message.MsgType)
        {
            case TkMsgType.NM_CH_LOGIN: HandleHallLogin(session, (PK_CH_PlayerLogin)message); break;
            case TkMsgType.NM_CH_Ping: HandlePing(session, (PK_CH_Ping)message); break;
            case TkMsgType.NM_CR_Ping: HandleCRPing(session, (PK_CR_Ping)message); break;
            case TkMsgType.NM_CH_ReqServerTime: HandleServerTime(session); break;
            case TkMsgType.NM_CH_HALLCHAT: HandleChat(session, (PK_CH_HallChat)message); break;
            case TkMsgType.NM_CH_RequestJoinRoom: HandleJoinRoom(session, (PK_CH_RequestJoinRoom)message); break;
            case TkMsgType.NM_CH_CheckIntegrity: HandleCheckIntegrity(session, (PK_CH_CheckIntegrity)message); break;
            case TkMsgType.NM_CH_PlayerSuccessInCarport: HandlePlayerSuccessInCarport(session); break;
            case TkMsgType.NM_CH_LoadCarport: HandleLoadCarport(session, (PK_CH_LoadCarport)message); break;
            case TkMsgType.NM_CH_GetLoginReward: HandleGetLoginReward(session); break;
            case TkMsgType.NM_CH_TankRankData: HandleTankRankData(session, (PK_CH_TankRankData)message); break;
            case TkMsgType.NM_CH_PlayerRankData: HandlePlayerRankData(session); break;
            case TkMsgType.NM_CH_ResearchRequest: HandleResearchRequest(session, (PK_CH_ResearchRequest)message); break;
            case TkMsgType.NM_CH_BUYITEM: HandleBuyItem(session, (PK_CH_BUYITEM)message); break;
            case TkMsgType.NM_CH_SELLITEM: HandleSellItem(session, (PK_CH_SELLITEM)message); break;
            case TkMsgType.NM_CH_BuyShell: HandleBuyShell(session, (PK_CH_BuyShell)message); break;
            case TkMsgType.NM_CH_SetShell: HandleSetShell(session, (PK_CH_SetShell)message); break;
            case TkMsgType.NM_CH_BuyCrew: HandleBuyCrew(session, (PK_CH_BuyCrew)message); break;
            case TkMsgType.NM_CH_OperCrew: HandleOperCrew(session, (PK_CH_OperCrew)message); break;
            case TkMsgType.NM_CH_LearnCrewSkill: HandleLearnCrewSkill(session, (PK_CH_LearnCrewSkill)message); break;
            case TkMsgType.NM_CH_QuickLearnCrewSkill: HandleQuickLearnCrewSkill(session, (PK_CH_QuickLearnCrewSkill)message); break;
            case TkMsgType.NM_CH_ResetCrewSkill: HandleResetCrewSkill(session, (PK_CH_ResetCrewSkill)message); break;
            case TkMsgType.NM_CH_CrewFastTrain: HandleCrewFastTrain(session); break;
            case TkMsgType.NM_CH_FireCrew: HandleFireCrew(session, (PK_CH_FireCrew)message); break;
            case TkMsgType.NM_CH_BuyCasernCrew: HandleBuyCasernCrew(session, (PK_CH_BuyCasernCrew)message); break;
            case TkMsgType.NM_CH_EquipmentOperate: HandleEquipmentOperate(session, (PK_CH_EquipmentOperate)message); break;
            case TkMsgType.NM_CH_TankCoatOperator: HandleTankCoatOperator(session, (PK_CH_TankCoatOperator)message); break;
            case TkMsgType.NM_CH_BuyTankSignature: HandleBuyTankSignature(session, (PK_CH_BuyTankSignature)message); break;
            case TkMsgType.NM_CH_GetTankHonor: HandleGetTankHonor(session); break;
            case TkMsgType.NM_CH_TANKREPAIR: HandleTankRepair(session, (PK_CH_TANKREPAIR)message); break;
            case TkMsgType.NM_CH_TransformTank: HandleTransformTank(session, (PK_CH_TransformTank)message); break;
            case TkMsgType.NM_CH_BUYGOLDTANK: HandleBuyGoldTank(session, (PK_CH_BUYGOLDTANK)message); break;
            case TkMsgType.NM_CH_BUYCARPORT: HandleBuyCarport(session, (PK_CH_BUYCARPORT)message); break;
            case TkMsgType.NM_CH_GOLDEXCHANGE_MONEY: HandleGoldExchangeMoney(session, (PK_CH_GOLDEXCHANGE_MONEY)message); break;
            case TkMsgType.NM_CH_GOLDEXCHANGE_EXP: HandleGoldExchangeExp(session, (PK_CH_GOLDEXCHANGE_EXP)message); break;
            case TkMsgType.NM_CH_VipOperate: HandleVipOperate(session, (PK_CH_VipOperate)message); break;
            case TkMsgType.NM_CH_GiftCardUse: HandleGiftCardUse(session, (PK_CH_GiftCardUse)message); break;
            case TkMsgType.NM_CH_ModuleOperate: HandleModuleOperate(session, (PK_CH_ModuleOperate)message); break;
            case TkMsgType.NM_CH_EndShoot: HandleEndShoot(session, (PK_CH_EndShoot)message); break;
            case TkMsgType.NM_CH_ShootPrize: HandleShootPrize(session, (PK_CH_ShootPrize)message); break;
            case TkMsgType.NM_CH_ChangeBattleTankList: HandleChangeBattleTankList(session, (PK_CH_ChangeBattleTankList)message); break;
            case TkMsgType.NM_CH_Change_Tank: HandleChangeTank(session, (PK_CH_Change_Tank)message); break;
            case TkMsgType.NM_CH_Set_Garage_Background: HandleSetGarageBackground(session, (PK_CH_Set_Garage_Background)message); break;
            case TkMsgType.NM_CH_Team_Ready: HandleTeamReady(session, (PK_CH_Team_Ready)message); break;
            case TkMsgType.NM_CH_Invite_Team: HandleInviteTeam(session, (PK_CH_Invite_Team)message); break;
            case TkMsgType.NM_CH_Join_Team: HandleJoinTeam(session, (PK_CH_Join_Team)message); break;
            case TkMsgType.NM_CH_Leave_Team: HandleLeaveTeam(session); break;
            case TkMsgType.NM_CH_Kick_Team: HandleKickTeam(session, (PK_CH_Kick_Team)message); break;
            case TkMsgType.NM_CH_Create_Team: HandleCreateTeam(session, (PK_CH_Create_Team)message); break;
            case TkMsgType.NM_CH_Team_Change_Mode: HandleTeamChangeMode(session); break;
            case TkMsgType.NM_CH_Quick_Match: HandleQuickMatch(session, (PK_CH_Quick_Match)message); break;
            case TkMsgType.NM_CH_Cancel_Quick_Match:
                lock (_matchLock) { RemovePlayerFromWaitingLobbiesLocked(session.PlayerGUID); }
                session.Send(new PK_HC_Start_End_Match { State = 0 });
                session.Send(new PK_HC_CancleJoinRoom { Flag = 1 });
                break;
            case TkMsgType.NM_CH_Start_Match: HandleStartMatch(session); break;
            case TkMsgType.NM_CH_Set_Free_Team: HandleSetFreeTeam(session, (PK_CH_Set_Free_Team)message); break;
            case TkMsgType.NM_CH_Be_Invite_Team_Ret: HandleBeInviteTeamRet(session); break;
            case TkMsgType.NM_CH_RespReconnect: HandleRespReconnect(session); break;
            case TkMsgType.NM_CH_CancleJoinRoom: HandleCancleJoinRoom(session); break;
            case TkMsgType.NM_CH_LotteryDraw: HandleLotteryDraw(session, (PK_CH_LotteryDraw)message); break;
            case TkMsgType.NM_CH_10_LotteryDraw: Handle10LotteryDraw(session); break;
            case TkMsgType.NM_CH_BuyDiscountItem: HandleBuyDiscountItem(session, (PK_CH_BuyDiscountItem)message); break;
            case TkMsgType.NM_CH_HotGoods: HandleHotGoods(session); break;
            case TkMsgType.NM_CH_Discount_Item_Remain: HandleDiscountItemRemain(session); break;
            case TkMsgType.NM_CH_LimitBuyActivityTime: HandleLimitBuyActivityTime(session); break;
            case TkMsgType.NM_CH_GetSingleRechargeReward: HandleGetSingleRechargeReward(session); break;
            case TkMsgType.NM_CH_GetAccumulateRechargeReward: HandleGetAccumulateRechargeReward(session); break;
            case TkMsgType.NM_CH_GetContinuousLoginReward: HandleGetContinuousLoginReward(session); break;
            case TkMsgType.NM_CH_ContinuousLoginRewardID: HandleContinuousLoginRewardID(session); break;
            case TkMsgType.NM_CH_TournamentRegister: HandleTournamentRegister(session); break;
            case TkMsgType.NM_CH_TournamentGetAllRange: HandleTournamentGetAllRange(session); break;
            case TkMsgType.NM_CH_TournamentGetReward: HandleTournamentGetReward(session); break;
            case TkMsgType.NM_CH_BeginLott: HandleBeginLott(session, (PK_CH_BeginLott)message); break;
            case TkMsgType.NM_CH_AskGetPowerPrize: HandleAskGetPowerPrize(session); break;
            case TkMsgType.NM_CH_GETPOWERPRIZE: HandleGetPowerPrize(session, (PK_CH_GETPOWERPRIZE)message); break;
            case TkMsgType.NM_CH_GIVEUP_PRIZE: HandleGiveUpPrize(session); break;
            case TkMsgType.NM_CH_ReqDomainBidInfo: HandleReqDomainBidInfo(session); break;
            case TkMsgType.NM_CH_Domain_FightList: HandleDomainFightList(session); break;
            case TkMsgType.NM_CH_RenameRequest: HandleRenameRequest(session, (PK_CH_RenameRequest)message); break;
            case TkMsgType.NM_CH_TestAutoGen: HandleTestAutoGen(session); break;
            case TkMsgType.NM_CH_EnterOutNewGuideOrBattle: HandleEnterOutNewGuide(session, (PK_CH_EnterOutNewGuideOrBattle)message); break;
            case TkMsgType.NM_CS_NEWGUIDE: HandleCSNewGuide(session, (PK_CS_NEWGUIDE)message); break;
            case TkMsgType.NM_CH_CheckHashRuntime: HandleCheckHashRuntime(session); break;
            case TkMsgType.NM_CH_SetManualMapID: HandleSetManualMapID(session, (PK_CH_SetManualMapID)message); break;
            case TkMsgType.NM_CH_GetGlobalCardTankLvl: HandleGetGlobalCardTankLvl(session); break;
            case TkMsgType.NM_CH_PersonalEmblemAccess: HandlePersonalEmblemAccess(session); break;
            case TkMsgType.NM_CH_BuyTankEmblem: HandleBuyTankEmblem(session, (PK_CH_BuyTankEmblem)message); break;
            case TkMsgType.NM_CH_DropTankEmblem: HandleDropTankEmblem(session, (PK_CH_DropTankEmblem)message); break;
            case TkMsgType.NM_CH_UseOffer: HandleUseOffer(session, (PK_CH_UseOffer)message); break;
            case TkMsgType.NM_CH_BuyBox: HandleBuyBox(session, (PK_CH_BuyBox)message); break;
            case TkMsgType.NM_CH_OPENBOX: HandleOpenBox(session, (PK_CH_OPENBOX)message); break;
            case TkMsgType.NM_CH_GETBOXPRIZE: HandleGetBoxPrize(session, (PK_CH_GETBOXPRIZE)message); break;
            case TkMsgType.NM_CH_GetChouJiang: HandleGetChouJiang(session); break;
            case TkMsgType.NM_CH_ChouJiangRun: HandleChouJiangRun(session); break;
            case TkMsgType.NM_CH_GetTaskReward: HandleGetTaskReward(session); break;
            case TkMsgType.NM_CH_Get_MonthlyLoginAward: HandleGetMonthlyLoginAward(session); break;
            case TkMsgType.NM_CH_SignIn_MonthlyLoginAward: HandleSignInMonthlyLoginAward(session); break;
            case TkMsgType.NM_CH_AskGetPayTask: HandleAskGetPayTask(session); break;
            case TkMsgType.NM_CH_AskStandaloneLoginReward: HandleAskStandaloneLoginReward(session); break;
            case TkMsgType.NM_CH_NewRecruitGetReq: HandleNewRecruitGetReq(session); break;
            case TkMsgType.NM_CH_NewRecruitPushReq: HandleNewRecruitPushReq(session); break;
            case TkMsgType.NM_CH_EXCHANGE_TANKEXP: HandleExchangeTankExp(session); break;
            case TkMsgType.NM_CH_ASKBONUSBOXDATA: HandleAskBonusBoxData(session); break;
            case TkMsgType.NM_CH_CHANNEL: HandleChannel(session, (PK_CH_CHANNEL)message); break;
            case TkMsgType.NM_CH_HALLCHAT_ALL: HandleHallChatAll(session, (PK_CH_HALLCHAT_ALL)message); break;
            case TkMsgType.NM_CHW_CORPS_CREATE: HandleCorpsCreate(session, (PK_CHW_CORPS_CREATE)message); break;
            case TkMsgType.NM_CHW_CORPS_QUIT: HandleCorpsQuit(session, (PK_CHW_CORPS_QUIT)message); break;
            case TkMsgType.NM_CHW_CORPS_DISMISS: HandleCorpsDismiss(session, (PK_CHW_CORPS_DISMISS)message); break;
            case TkMsgType.NM_CHW_CORPS_MOD_INTRODUCE: HandleCorpsModIntroduce(session, (PK_CHW_CORPS_MOD_INTRODUCE)message); break;
            case TkMsgType.NM_CH_CORPS_MOD_TIPNAME: HandleCorpsModTipName(session, (PK_CH_CORPS_MOD_TIPNAME)message); break;
            case TkMsgType.NM_CH_CORPS_MOD_NAME: HandleCorpsModName(session, (PK_CH_CORPS_MOD_NAME)message); break;
            case TkMsgType.NM_CHW_CORPS_APPLICATION_REQ: HandleCorpsApplicationReq(session, (PK_CHW_CORPS_APPLICATION_REQ)message); break;
            case TkMsgType.NM_CHW_CORPS_APPLICATION_REP: HandleCorpsApplicationRep(session, (PK_CHW_CORPS_APPLICATION_REP)message); break;
            case TkMsgType.NM_CHW_CORPS_SEARCH: HandleCorpsSearch(session, (PK_CHW_CORPS_SEARCH)message); break;
            case TkMsgType.NM_CHW_CORPS_QUERY: HandleCorpsQuery(session, (PK_CHW_CORPS_QUERY)message); break;
            case TkMsgType.NM_CHW_CORPS_DATA: HandleCorpsData(session); break;
            case TkMsgType.NM_CHW_CORPS_MEMBER_REQ: HandleCorpsMemberReq(session, (PK_CHW_CORPS_MEMBER_REQ)message); break;
            case TkMsgType.NM_CHW_CORPS_COMMISSION: HandleCorpsCommission(session, (PK_CHW_CORPS_COMMISSION)message); break;
            case TkMsgType.NM_CHW_CORPS_EXPEL: HandleCorpsExpel(session, (PK_CHW_CORPS_EXPEL)message); break;
            case TkMsgType.NM_CHW_CORPS_CLOSE: HandleCorpsClose(session, (PK_CHW_CORPS_CLOSE)message); break;
            case TkMsgType.NM_CHW_CORPS_INVITE_REP: HandleCorpsInviteRep(session, (PK_CHW_CORPS_INVITE_REP)message); break;
            case TkMsgType.NM_CHW_CORPS_ASSIGNMENT: HandleCorpsAssignment(session, (PK_CHW_CORPS_ASSIGNMENT)message); break;
            case TkMsgType.NM_CHW_Corps_Mod_TaxRate: HandleCorpsModTaxRate(session, (PK_CHW_Corps_Mod_TaxRate)message); break;
            case TkMsgType.NM_CHW_Corps_ConvertMoney: HandleCorpsConvertMoney(session, (PK_CHW_Corps_ConvertMoney)message); break;
            case TkMsgType.NM_CHW_Corps_MoneyChangeList: HandleCorpsMoneyChangeList(session); break;
            case TkMsgType.NM_CHW_Corps_MoneyChangeList2: HandleCorpsMoneyChangeList2(session); break;
            case TkMsgType.NM_CHW_CorpsAth_Invite: HandleCorpsAthInvite(session, (PK_CHW_CorpsAth_Invite)message); break;
            case TkMsgType.NM_CHM_CorpsAth_AllowJoinAll: HandleCorpsAthAllowJoinAll(session, (PK_CHM_CorpsAth_AllowJoinAll)message); break;
            case TkMsgType.NM_CHM_CorpsAth_AllowQuit: HandleCorpsAthAllowQuit(session, (PK_CHM_CorpsAth_AllowQuit)message); break;
            case TkMsgType.NM_CH_CorpsAth_RankList: HandleCorpsAthRankList(session); break;
            case TkMsgType.NM_CH_Corps_TankList_Req: HandleCorpsTankListReq(session); break;
            case TkMsgType.NM_CH_CorpsAth_MyRankList: HandleCorpsAthMyRankList(session); break;
            case TkMsgType.NM_CH_CorpsAth_AwardList: HandleCorpsAthAwardList(session); break;
            case TkMsgType.NM_CH_Corps_IntelligenceList: HandleCorpsIntelligenceList(session); break;
            case TkMsgType.NM_CH_Corps_PrevTankList: HandleCorpsPrevTankList(session); break;
            case TkMsgType.NM_CH_CorpsAth_GetAward: HandleCorpsAthGetAward(session); break;
            case TkMsgType.NM_CH_Corps_DevelopTank: HandleCorpsDevelopTank(session, (PK_CH_Corps_DevelopTank)message); break;
            case TkMsgType.NM_CH_CorpsAth_JoinReady: HandleCorpsAthJoinReady(session); break;
            case TkMsgType.NM_CH_CorpsAth_Join: HandleCorpsAthJoin(session, (PK_CH_CorpsAth_Join)message); break;
            case TkMsgType.NM_CH_CorpsAth_JoinQuit: HandleCorpsAthJoinQuit(session, (PK_CH_CorpsAth_JoinQuit)message); break;
            case TkMsgType.NM_CH_CorpsAth_ChangeTank: HandleCorpsAthChangeTank(session, (PK_CH_CorpsAth_ChangeTank)message); break;
            case TkMsgType.NM_CH_CorpsAth_AllowJoin: HandleCorpsAthAllowJoin(session, (PK_CH_CorpsAth_AllowJoin)message); break;
            case TkMsgType.NM_CH_Corps_Buy_NumLimitTimes: HandleCorpsBuyNumLimitTimes(session); break;
            case TkMsgType.NM_CH_Corps_FastDevelop: HandleCorpsFastDevelop(session, (PK_CH_Corps_SpeedStudyTank)message); break;
            case TkMsgType.NM_CHW_FriendStateReq: HandleFriendStateReq(session, (PK_CHW_FriendStateReq)message); break;
            case TkMsgType.NM_CHW_FRIEND_UPDATE: HandleFriendUpdate(session); break;
            case TkMsgType.NM_CHW_FRIEND_OPERATE: HandleFriendOperate(session, (PK_CHW_FRIEND_OPERATE)message); break;
            case TkMsgType.NM_CHW_Domain_Bidding: HandleDomainBidding(session, (PK_CHW_Domain_Bidding)message); break;
            case TkMsgType.NM_CHW_Domain_FightOther: HandleDomainFightOther(session, (PK_CHW_Domain_FightOther)message); break;
            case TkMsgType.NM_CHW_Domain_FightTime: HandleDomainFightTime(session); break;
            case TkMsgType.NM_CHW_PLAYER_QUERY: HandlePlayerQuery(session, (PK_CHW_PLAYER_QUERY)message); break;
            case TkMsgType.NM_CHW_CorpsBid_PlaceBid: HandleCorpsBidPlaceBid(session, (PK_CHW_CorpsBid_PlaceBid)message); break;
            case TkMsgType.NM_CHW_CorpsBid_GetTank: HandleCorpsBidGetTank(session, (PK_CHW_CorpsBid_GetTank)message); break;
            case TkMsgType.NM_CHW_CorpsBid_Lists: HandleCorpsBidLists(session); break;
            case TkMsgType.NM_CHW_CorpsLevel_upgrade: HandleCorpsLevelUpgrade(session); break;
            case TkMsgType.NM_SEND_CONFIG: break;
            default: Console.WriteLine($"[HS] Unhandled: {message.MsgType}"); break;
        }
    
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HS] Exception in msgType={message?.MsgType}: {ex.Message}");
        }
}

    private HallPlayerData? GetPlayerData(GameSession session)
    {
        var account = _accountRepo.GetByPlayerGUID(session.PlayerGUID);
        if (account == null) return null;

        bool isVipActive = account.IsVip && account.VipExpireDate > DateTime.UtcNow;
        bool isPremiumActive = account.IsPremium && account.PremiumExpireDate > DateTime.UtcNow;

        return new HallPlayerData
        {
            PlayerGUID = account.PlayerGUID, PlayerName = account.PlayerName,
            Silver = account.Silver, Gold = account.Gold,
            Experience = account.Experience, Level = account.Level,
            BattleCap = account.BattleCap, CasernCapacity = account.CasernCapacity,
            GarageBackground = account.GarageBackground,
            ChannelId = account.ChannelId, ContinuousLoginDays = account.ContinuousLoginDays,
            MonthlyLoginDay = account.MonthlyLoginDay,
            IsVip = isVipActive, VipExpireDate = account.VipExpireDate,
            IsPremium = isPremiumActive, PremiumExpireDate = account.PremiumExpireDate,
            CreatedAt = account.CreatedAt
        };
    }

    private void HandleHallLogin(GameSession session, PK_CH_PlayerLogin login)
    {
        try
        {
            session.PlayerGUID = login.PlayerGUID;
            session.Certification = login.Certification;
            _hallByGuid[login.PlayerGUID] = session;   // v13: для доставки окна статистики

            var data = GetPlayerData(session);
            if (data == null)
            {
                Console.WriteLine($"[HS] Hall login FAILED: no data for GUID={login.PlayerGUID}");
                session.Send(new PK_HC_LoginResult { Flag = 0 });
                return;
            }

            session.PlayerName = data.PlayerName;
            Console.WriteLine($"[HS] ========================================");
            Console.WriteLine($"[HS] Player Profile:");
            Console.WriteLine($"[HS]   Nickname: {data.PlayerName}");
            Console.WriteLine($"[HS]   GUID: {data.PlayerGUID}");
            Console.WriteLine($"[HS]   Level: {data.Level}");
            Console.WriteLine($"[HS]   Silver: {data.Silver}");
            Console.WriteLine($"[HS]   Gold: {data.Gold}");
            Console.WriteLine($"[HS]   Experience: {data.Experience}");
            Console.WriteLine($"[HS]   VIP: {(data.IsVip ? "Yes" : "No")}");
            Console.WriteLine($"[HS]   Premium: {(data.IsPremium ? "Yes" : "No")}");
            Console.WriteLine($"[HS] ========================================");
            Console.WriteLine($"[HS] Hall login: GUID={login.PlayerGUID}, sending init data...");
            SendInitData(session, data);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HS] Error in HandleHallLogin: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    // v63-хак поднимал CarportNum выше 85 (Math.Max(cap, tankCount+5)) и клампил
    // в 1..255 вместо 1..85 — из-за этого при CasernCapacity=89 в БД клиент получал
    // CarportNum=89, а ВЕСЬ клиентский код (ShopTools.cs, TankSelectList.cs,
    // TreasureBox.cs, GetRewardRule.cs) жёстко считает 85 абсолютным потолком игры:
    // при CarportNum >= 85 кнопка «Купить слот» блокируется НА КЛИЕНТЕ ещё до
    // отправки запроса серверу («Вы достигли предельного количества слотов»), и
    // выдача призов-танков тоже блокируется. Слать больше 85 нельзя никогда.
    private byte ComputeCarportNum(HallPlayerData data)
    {
        int cap = data.CasernCapacity <= 0 ? 85 : data.CasernCapacity;
        return (byte)Math.Clamp(cap, 1, 85);
    }

    private void SendInitData(GameSession session, HallPlayerData data)
    {
        EnsurePlayerHangarConsistency(data.PlayerGUID);
        session.HangarConsistencyChecked = true;

        // v45: РАННИЙ PlayMain — доставляем CarportNum ДО первого TANKINFO.
        // Панель ангара (клиентский TankSelectList) при построении кэширует число
        // пустых слотов: _emptyPortCs.Num = CarportNum - HallTanks.Count (метод
        // AddEmptyPort). Построение панели происходит на первом TANKINFO. При этом
        // клиент выставляет Player.CarportNum ТОЛЬКО из PlayMain (InitPlayerMain), а
        // PlayMain раньше слался с задержкой 2с в HandleLoadCarport — то есть ПОСЛЕ
        // TANKINFO. Итог: панель строилась при CarportNum=0 → Num = 0 - 1 = -1, и
        // живое добавление танка (выдача из рулетки, NM_HC_ADDTANK) падало в
        // TankSelectList.AddTank на проверке "_emptyPortCs.Num <= 0" с логом
        // "have not enough empty port". Танк добавлялся в HallTanks, но плитка не
        // создавалась — появлялась лишь после ПЕРЕСБОРКИ панели (уход в дерево
        // исследований и обратно, либо перезапуск), когда CarportNum уже = 85.
        // OnHCPlayerMain только выставляет данные игрока (CarportNum/деньги) и НЕ
        // запускает переход сцены, поэтому ранний PlayMain безопасен. Отложенный
        // PlayMain в HandleLoadCarport оставлен как есть (идемпотентно шлёт те же
        // значения и держит гейт PlayMainDelivered для ответа на ServerTime).
        session.Send(new PK_HC_PlayMain
        {
            PlayerGUID = data.PlayerGUID, PlayerName = data.PlayerName,
            CarportNum = ComputeCarportNum(data),
            LastSelect = 0, TankProjectIndex = 1,
            GameMoney = data.Silver, GoldMoney = data.Gold,
            LeftRenameTime = 0, PlayerPlatformId = 1,
            // v.fix: раньше всегда 0 — кнопка "награда за скачивание" (TankStandaloneRewardBtn)
            // после уже полученной награды не пряталась сама (SetActive(false) только
            // если StandaloneReward==1), и по возвращении в игру появлялась заново.
            StandaloneReward = (byte)(_accountRepo.IsStandaloneRewardClaimed(data.PlayerGUID) ? 1 : 0), FirstLoginFlag = 0
        });
        Console.WriteLine($"[HS] v45: ранний PlayMain до TANKINFO, CarportNum={ComputeCarportNum(data)} (панель ангара строится с верным числом слотов)");

        // 1. TANKINFO - send tank data (client needs this for hangar init)
        var tanks = _tankRepo.GetPlayerTanks(data.PlayerGUID);
        var tankDataList = new List<TankData>();
        foreach (var tank in tanks)
        {
            tankDataList.Add(ConvertTankToTankData(tank));
        }
        session.Send(new PK_HC_TANKINFO { TankTotalCount = (byte)tankDataList.Count, TankDatas = tankDataList.ToArray() });
        session.InitialTankInfoSent = true;
        Console.WriteLine($"[HS] SendInitData: packed {tankDataList.Count} tanks");

        // v48.2: помечаем ВСЕ машины игрока ЭЛИТНЫМИ (nState=1). Клиент разрешает
        // «Модифицировать» только элитным танкам (у которых изучены все модули и зависимые
        // машины). На тест-аккаунте всё изучено — иначе кнопка модификации заблокирована,
        // и покупка модификации не проходит (ровно баг со скрина). nTankID = VehicleId.
        if (tanks.Count > 0)
        {
            // v65 (п.3): элитным (nState=1) помечаем ТОЛЬКО танк с полностью изученными модулями
            // (или без дерева — премы). Иначе клиент показывает попап «все исследования завершены»
            // на неизученной технике (баг со скрина). Считаем изученные модули на машину и сравниваем
            // с числом модульных узлов дерева.
            var researchedByTank = (_moduleRepo?.GetResearchedModules(data.PlayerGUID)
                                    ?? new List<(int VehicleId, int ModuleType, int ModuleId)>())
                                   .GroupBy(m => m.VehicleId)
                                   .ToDictionary(gr => gr.Key, gr => gr.Count());
            session.Send(new PK_HC_TankDevpState
            {
                TankStates = tanks.Select(t =>
                {
                    int nodeCount = _gameTables.GetTankModuleNodeCount(t.VehicleId);
                    int done = researchedByTank.TryGetValue(t.VehicleId, out var rc) ? rc : 0;
                    bool elite = nodeCount == 0 || done >= nodeCount; // без дерева (премы) = элит
                    return new tankDevpState { nTankID = t.VehicleId, nState = (byte)(elite ? 1 : 0) };
                }).ToArray()
            });
        }

        // 2. AllTankAttribute - initialize AttributeContainer for each tank (prevents NRE in TankAttribute)
        var crewByTank = BuildCrewLevelLookup(data.PlayerGUID);
        var tankAttrs = new TankAllAttributeStruct[tanks.Count];
        for (int i = 0; i < tanks.Count; i++)
        {
            tankAttrs[i] = GenerateTankAttributes(tanks[i], crewByTank);
        }
        session.Send(new PK_HC_AllTankAttribute { TankCount = (byte)tanks.Count, TankAttributes = tankAttrs });

        // 2b. v30 (камуфляжи): состояние всех трёх слотов и надетого камуфляжа по
        // каждому танку. Шлём ПОСЛЕ TANKINFO — Player.OnSynTankCoatInfo делает
        // TryGetHallTank(TankID) и молча пропускает танки, которых клиент ещё не знает.
        SendAllTankCoats(session, data.PlayerGUID);

        // 3a. PlayerOwnTankRank - populate RankListManager._tankOwnRankList.
        // TankInfoPanle.RefreshRankInfos() indexes TankOwnRankList[VehicleId] WITHOUT a
        // ContainsKey guard, so selecting any tank whose VehicleId is missing throws a
        // KeyNotFoundException that aborts OnClickTankButton *before* the 3D model is loaded
        // (this is the "switching tanks doesn't change the model" bug). Send an entry for
        // every tank in the game tables (nRankOrder = -1 => "not ranked", handled gracefully).
        var rankVehicleIds = _gameTables.Tanks.Values.Select(t => t.Id).Distinct().ToList();
        var ownRank = new PK_HC_PlayerOwnTankRank { Count = rankVehicleIds.Count };
        ownRank.Rankitems = new PlayerOwnTankRankItem[rankVehicleIds.Count];
        for (int i = 0; i < rankVehicleIds.Count; i++)
        {
            ownRank.Rankitems[i] = new PlayerOwnTankRankItem
            {
                nTankID = rankVehicleIds[i],
                nRankOrder = -1, // -1 = not yet ranked
                UpOrDown = 0
            };
        }
        session.Send(ownRank);

        // 3. Basic info - items with data
        var inventoryItems = _inventoryRepo.GetPlayerItems(data.PlayerGUID);
        var itemInfo = new PK_HC_ItemInfo { ItemCount = inventoryItems.Count };
        itemInfo.Items = new ItemInfoData[inventoryItems.Count];
        for (int i = 0; i < inventoryItems.Count; i++)
        {
            itemInfo.Items[i] = new ItemInfoData
            {
                ItemType = inventoryItems[i].ItemType,
                ItemId = inventoryItems[i].ItemId,
                ItemNum = inventoryItems[i].ItemNum,
                ItemFlag = inventoryItems[i].ItemFlag
            };
        }
        session.Send(itemInfo);

        SendShellInfo(session, tanks.FirstOrDefault());
        session.Send(new PK_HC_LoginReward { WeekID = 0, Days = 0, IsGet = 0 });
        session.Send(new PK_HC_NewGuideLoginReward
        {
            LoginDays = 1,
            OperRewardDayIndex = 0,
            Rewards = new byte[] { 0, 0, 0, 0, 0, 0, 0 },
            IsLoginRewardFinish = false
        });
        session.Send(new PK_HC_Channel_Res { ChannelId = 1 });

        // Corps data — send full packet so client keeps the legion tab loaded
        var playerCorpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (playerCorpsGuid.HasValue)
        {
            var corps = _corpsRepo.GetByGUID(playerCorpsGuid.Value);
            var member = _corpsRepo.GetMember(playerCorpsGuid.Value, data.PlayerGUID);
            var app = _corpsRepo.GetPlayerApplication(data.PlayerGUID);
        var member2 = _corpsRepo.GetMember(playerCorpsGuid.Value, data.PlayerGUID);
        // Ensure player always has at least 10,000,000 loyalty (admin top-up)
        int loyalty = member2?.LoyalPoint ?? 0;
        if (loyalty < 10_000_000)
        {
            _corpsRepo.UpdateMemberLoyalPoint(playerCorpsGuid.Value, data.PlayerGUID, 10_000_000 - loyalty);
            loyalty = 10_000_000;
        }
        session.Send(BuildCorpsDataPacket(corps, member2?.Job ?? 0,
            _corpsRepo.GetMemberCount(playerCorpsGuid.Value), app, playerCorpsGuid.Value,
            fealtyPoint: loyalty));
        }
        else
        {
            session.Send(new PK_WHC_CorpsData { TroopsID = 0 }); // no corps
        }

        // Friend list - client adds self in FriendsManagerModel.ReCreate(), so only send actual friends
        var friends = _friendRepo.GetFriends(data.PlayerGUID);
        var friendList = new PK_WHC_FriendList
        {
            PlayerGUID = data.PlayerGUID,
            FriendCount = friends.Count
        };
        friendList.Friends = new FriendInfoData[friends.Count];
        for (int i = 0; i < friends.Count; i++)
        {
            friendList.Friends[i] = new FriendInfoData
            {
                FriendGUID = friends[i].FriendGUID,
                FriendName = friends[i].FriendName,
                RelationType = 0, // PlayerRelation.Friend
                OnlineState = friends[i].IsOnline ? (byte)2 : (byte)1, // PS_ONLINE / PS_OFFLINE
                HSIndex = 0,
                TeamGUID = 0
            };
        }
        session.Send(friendList);

        SendMoneyExp(session, data);
        SendCasernUpdate(session, data.CasernCapacity);

        // Crew data with actual structures
        SendPlayerAllCrew(session, data.PlayerGUID);

        SendResearchState(session, data.PlayerGUID);

        session.Send(new PK_HC_ActivityConfig());
        session.Send(new PK_HC_HotGoods());

        // Курс обмена золота на серебро/опыт. Клиент НЕ вычисляет его сам — берёт
        // значения GoldToSilver/GoldToExp из этого пакета (Player.HandleActivityEffectInfor)
        // и использует их в диалогах "Покупка серебра"/"Покупка опыта". Если сервер этот
        // пакет не шлёт, поля остаются 0 по умолчанию — отсюда "ОБМЕННЫЙ КУРС 1 = 0" на
        // скриншоте. 1 золото = 400 серебра, 1 золото = 25 свободного опыта.
        session.Send(new PK_HC_ActivityEffectInfor
        {
            GoldToSilver = GoldToSilverRate,
            GoldToExp = GoldToExpRate,
            TankDiscountCount = 0,
            TankDiscounts = Array.Empty<TankDiscount>()
        });

        // v: VIP-статус нужно толкать клиенту сразу при входе в Ангар, а не только
        // в ответ на попытку покупки. Иначе VipData на клиенте остаётся "пустым"
        // (badge/таймер выключены) до первого клика "Купить VIP" в текущей сессии —
        // выглядит как "VIP работает только после ещё одной покупки".
        {
            int initialVipLeftTime = 0;
            if (data.IsVip)
            {
                initialVipLeftTime = (int)(data.VipExpireDate - DateTime.UtcNow).TotalSeconds;
                if (initialVipLeftTime < 0) initialVipLeftTime = 0;
            }
            session.Send(new PK_HC_VipTime { VipLeftTime = initialVipLeftTime });
        }

        // ВАЖНО: раньше тут всегда слался хардкод 10001, независимо от того, что игрок
        // выбрал и сохранил через HandleSetGarageBackground — выбор пропадал при каждом
        // следующем логине. Плюс GarageBackground хранился как byte, а реальные id
        // (10001..10005) в него не влезают — теперь поле int, каста нет.
        session.Send(new PK_HC_Garage_Background { BackgroundId = data.GarageBackground });
        SendTankEquipment(session, tanks);
        session.Send(new PK_HC_TankHonorList());
        // Skip BattleEndInfo, PlayerStatistics, UpdateCasern, PayTaskData, ActivityOpenState - not needed for hall init
        // v77/v: статистика (основная вкладка + Revive/Commander/PVE + карточка
        // AccountFightStatistics) теперь считается общим методом PushAccountStatistics,
        // который также вызывается из RoomServer.EndBattle сразу после каждого боя —
        // чтобы клиент не ждал переподключения для обновления цифр.
        PushAccountStatistics(session, data.PlayerGUID, _accountRepo, _battleStatsRepo, _modeStatsRepo, _tankRepo);
        session.Send(new PK_HC_AskStandaloneLoginReward { HasReward = 0 });
        session.Send(new PK_HC_AskGetPowerPrize { HasPowerPrize = 0 });
        session.Send(new PK_HC_LeftSuperPrizeCount { RewardLeft = 100 });
        session.Send(new PK_HC_LotteryData { LeftTime = 86400, HaveGot = 0, PowerValue = 0f, IsEveryDayFirstLottery_One = true, IsEveryDayFirstLottery_Ten = true });
        session.Send(new PK_HC_TournamentInfo { Status = 0, RegisterState = 0, GetRewardState = 0 });
        session.Send(new PK_HC_BeginLott { nLevl = 1, nConfigID = new int[] { 1,2,3,4,5,6,7,8,9,10,11,12 } });
        session.Send(new PK_HC_ChouJiangOpen { IsOpen = 0 });
        // v.fix (Тир): без этого клиент никогда не узнаёт реальное число оставшихся
        // выстрелов и всегда показывает 0, даже если они были куплены/остались с
        // прошлой сессии (см. HandleBuyBox/HandleShootPrize — там же обновление).
        SendRemainShootTimes(session, data.PlayerGUID);
        session.Send(new PK_HC_EndShoot { nlevl = 1 });
        session.Send(new PK_HC_Discount_Item_Remain { RemainCount = 0 });
        session.Send(new PK_HC_LimitBuyActivityTime
        {
            ServerStartTime = 0,
            ServerEndTime = 0,
            PersonStartTime = 0,
            PersonEndTime = 0
        });
        session.Send(new PK_HC_ContinuousLoginRewardID { RewardIds = new int[7] });
        session.Send(new PK_HC_SetManualMapID { MapId = 0, BattleMode = 0 });
        // Set Veteran status (IsAdvanced=true, AdvanceLevel=2) so veteran equipment is purchasable.
        // NM_HC_UpdatePlayerCharged → Player.HandleHCUpdatePlayerCharged → 
        //   AdvancedAccountManager.Instance.CurrentCharge = TotalCharge
        // Only the CurrentCharge SETTER triggers IsAdvanced=true (not the private field).
        session.Send(new PK_HC_UpdatePlayerCharged { IsChargedFlag = true, TotalCharge = 999999 });
        // v: раньше не слался вообще — DomainHsDataList на клиенте оставался null, и
        // открытие глобальной карты падало с NullReferenceException (foreach по null
        // списку в ContendMap.SetMapSituation). Шлём пустой список (нейтральная карта,
        // без владений корпусов) — подробности см. комментарий у PK_HC_DomainInfo.
        session.Send(new PK_HC_DomainInfo { DomainPhase = 0, BiddingPhase = 0 });
        // Do not send server time during the fragile pre-hall bootstrap.
        // The client touches time-related singletons too early and can stall
        // further inbound packet handling after a NullReferenceException.
        System.Threading.Thread.Sleep(200);

        // 4. LoginResult (triggers Hall scene load)
        Console.WriteLine($"[HS] All init data sent, sending LoginResult...");
        session.Send(new PK_HC_LoginResult { Flag = 1 });
        Console.WriteLine($"[HS] LoginResult sent");
        SendGuideState(session);
    }

    private void SendMoneyExp(GameSession session, HallPlayerData data)
    {
        session.Send(new PK_HC_ChangeMoneyExp
        {
            GameMoney = data.Silver,
            GoldMoney = data.Gold,
            PlayerExp = data.Experience
        });
    }

    private void SendCasernUpdate(GameSession session, int maxCount)
    {
        session.Send(new PK_HC_UpdateCasern { MaxCount = maxCount });
    }

    private void SendGuideState(GameSession session)
    {
        var account = _accountRepo.GetByPlayerGUID(session.PlayerGUID);
        if (account == null)
        {
            session.Send(new PK_SC_NEWGUIDE
            {
                guide = AccountRepository.LocalTestGuideStates
            });
            return;
        }

        // account.GuideStates == long.MaxValue — старое "sentinel"-значение
        // (все 63 шага обучения пройдены), которым помечались тестовые аккаунты
        // ДО фикса hallGuideHelper (см. комментарий у AccountRepository.LocalTestGuideStates).
        // Приводим его к новой безопасной константе на лету, без миграции БД —
        // иначе уже созданные ранее аккаунты так и будут ловить NullReferenceException
        // в HallGuideTools при покупке танка/слота ангара.
        bool isLegacyOrEmptyGuideState = account.GuideStates == 0 || account.GuideStates == long.MaxValue;

        var missionData = isLegacyOrEmptyGuideState
            ? ParseGuideMissionData(AccountRepository.LocalTestMissionData)
            : ParseGuideMissionData(account.MissionData);

        session.Send(new PK_SC_NEWGUIDE
        {
            missionId1 = missionData[0],
            present1 = missionData[1],
            missionId2 = missionData[2],
            present2 = missionData[3],
            missionId3 = missionData[4],
            present3 = missionData[5],
            guide = isLegacyOrEmptyGuideState ? AccountRepository.LocalTestGuideStates : account.GuideStates
        });
    }

    private static int[] ParseGuideMissionData(string? missionData)
    {
        var result = new int[6];
        if (string.IsNullOrWhiteSpace(missionData))
        {
            return result;
        }

        var parts = missionData.Split(',');
        for (int i = 0; i < result.Length && i < parts.Length; i++)
        {
            if (int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                result[i] = value;
            }
        }

        return result;
    }

    private void SendPlayerAllCrew(GameSession session, long playerGuid)
    {
        var crewMembers = _crewRepo.GetPlayerCrew(playerGuid);
        var crewInfo = new PK_HC_PlayerAllCrew { CrewCount = crewMembers.Count };
        crewInfo.Crews = new CrewInfoData[crewMembers.Count];
        for (int i = 0; i < crewMembers.Count; i++)
        {
            crewInfo.Crews[i] = BuildCrewInfoData(crewMembers[i]);
        }

        session.Send(crewInfo);
    }

    private void SendMoneyExpByGuid(GameSession session, long playerGuid)
    {
        var account = _accountRepo.GetByPlayerGUID(playerGuid);
        if (account == null) return;
        Console.WriteLine($"[HS] SendMoneyExpByGuid: Silver={account.Silver}, Gold={account.Gold}, Exp={account.Experience}");
        session.Send(new PK_HC_ChangeMoneyExp
        {
            GameMoney = account.Silver,
            GoldMoney = account.Gold,
            PlayerExp = account.Experience
        });
    }

    private void SendTankExp(GameSession session, params Tank[] tanks)
    {
        if (tanks.Length == 0)
        {
            return;
        }

        var data = GetPlayerData(session);
        session.Send(new PK_HC_TankPlayerExp
        {
            PlayerExp = data?.Experience ?? 0,
            TankCount = tanks.Length,
            TankArray = tanks
                .Select(tank => new StructTankExp { TankId = tank.VehicleId, TankExp = tank.TankExp })
                .ToArray()
        });
    }

    private void EnsurePlayerHangarConsistency(long playerGuid)
    {
        _tankRepo.NormalizePlayerTanks(playerGuid);
        _crewRepo.NormalizeAssignments(playerGuid);
        _moduleRepo?.NormalizePlayerModules(playerGuid);
        var tanks = _tankRepo.GetPlayerTanks(playerGuid);
        if (tanks.Count == 0) return;

        if (!tanks.Any(t => t.IsBattleTank))
        {
            MarkBattleTank(playerGuid, tanks[0].TankGUID);
        }
    }

    private long GetSelectedTankGuid(long playerGuid, long preferredTankGuid = 0)
    {
        if (preferredTankGuid != 0)
        {
            var preferredTank = _tankRepo.GetTankByGUID(preferredTankGuid);
            if (preferredTank?.PlayerGUID == playerGuid)
            {
                return preferredTankGuid;
            }
        }

        var tanks = _tankRepo.GetPlayerTanks(playerGuid);
        return tanks.FirstOrDefault(t => t.IsBattleTank)?.TankGUID
            ?? tanks.FirstOrDefault()?.TankGUID
            ?? 0;
    }

    private void MarkBattleTank(long playerGuid, long tankGuid)
    {
        if (tankGuid == 0) return;
        var tankGuids = new long[5];
        tankGuids[0] = tankGuid;
        _tankRepo.UpdateBattleTankList(playerGuid, tankGuids);
    }

    private void SendResearchState(GameSession session, long playerGuid)
    {
        var tanks = _tankRepo.GetPlayerTanks(playerGuid);
        var ownedVehicleIds = new HashSet<int>(tanks.Select(t => t.VehicleId));
        var researchedModules = _moduleRepo?.GetResearchedModules(playerGuid) ?? new List<(int VehicleId, int ModuleType, int ModuleId)>();

        var tankList = _gameTables.Tanks.Values
            .OrderBy(t => t.Id)
            .Select(t => new ResearchTank
            {
                Id = t.Id,
                HasBuy = ownedVehicleIds.Contains(t.Id) ? (byte)1 : (byte)0
            })
            .ToArray();

        var partList = researchedModules
            .GroupBy(module => (module.ModuleType, module.ModuleId))
            .Select(group => new ResearchNode { TypeId = group.Key.ModuleType, Id = group.Key.ModuleId })
            .OrderBy(node => node.TypeId)
            .ThenBy(node => node.Id)
            .ToArray();

        session.Send(new PK_HC_TankResearchList { TankCount = tankList.Length, TankList = tankList });
        session.Send(new PK_HC_PartResearchList { PartCount = partList.Length, PartList = partList });
    }

    private void SendTankEquipment(GameSession session, IReadOnlyList<Tank> tanks)
    {
        var packet = new PK_HC_TankEquipment
        {
            TankCount = (byte)tanks.Count,
            TankEquipments = new PK_HC_TankEquipmentInfor[tanks.Count]
        };

        var equipByTank = tanks.Count > 0
            ? _tankEquipmentRepo.GetAllPlayerTankEquipmentIds(tanks[0].PlayerGUID)
            : new Dictionary<long, List<int>>();

        for (int i = 0; i < tanks.Count; i++)
        {
            equipByTank.TryGetValue(tanks[i].TankGUID, out var equipmentIds);
            packet.TankEquipments[i] = new PK_HC_TankEquipmentInfor
            {
                TankGuid = tanks[i].TankGUID
            };

            if (equipmentIds != null)
            {
                for (int slotIndex = 0; slotIndex < packet.TankEquipments[i].EquipmentIDs.Length && slotIndex < equipmentIds.Count; slotIndex++)
                {
                    packet.TankEquipments[i].EquipmentIDs[slotIndex] = (ushort)equipmentIds[slotIndex];
                }
            }
        }

        session.Send(packet);
    }

    private void SendAllEquipmentData(GameSession session, IReadOnlyList<Tank> tanks)
    {
        var equipByTank = tanks.Count > 0
            ? _tankEquipmentRepo.GetAllPlayerTankEquipmentIds(tanks[0].PlayerGUID)
            : new Dictionary<long, List<int>>();

        var packet = new PK_HC_AllEquipmentData
        {
            TankCount = (byte)tanks.Count,
            EquipmentInfors = new EquipmentInfor[tanks.Count]
        };

        for (int i = 0; i < tanks.Count; i++)
        {
            var ted = new TankEquipmentData();
            if (equipByTank.TryGetValue(tanks[i].TankGUID, out var eqIds))
            {
                foreach (int eqId in eqIds)
                {
                    var eq = _gameTables.GetEquipment(eqId);
                    if (eq == null || eq.DefenseOnly) continue;
                    ApplyToTankEquipmentData(ted, eq.Attribute1, eq.Value1);
                    ApplyToTankEquipmentData(ted, eq.Attribute2, eq.Value2);
                    ApplyToTankEquipmentData(ted, eq.Attribute3, eq.Value3);
                    ApplyToTankEquipmentData(ted, eq.Attribute4, eq.Value4);
                    ApplyToTankEquipmentData(ted, eq.Attribute5, eq.Value5);
                }
            }
            packet.EquipmentInfors[i] = new EquipmentInfor
            {
                TankGuid = tanks[i].TankGUID,
                TankEquData = ted
            };
        }

        session.Send(packet);
    }

    private static void ApplyToTankEquipmentData(TankEquipmentData ted, int attr, float val)
    {
        if (attr <= 0 || attr >= 1000 || val == 0f) return;
        switch (attr)
        {
            case 1:  ted.PowerBuff += val; break;
            case 16: ted.Stable += val; break;
            case 19: ted.AimcircleSpeed += val; break;
            case 20: ted.CircularVisionRadiusRange += val; break;
            case 21: ted.HideFactor += val; break;
            case 24: ted.ReloadBuff += val; break;
            case 25: ted.LoadBuff += val; break;
            case 27: ted.FragmentFactor += val; break;
            case 31: ted.AmmobayHpBuff += val; break;
            case 32: ted.FueltankHpBuff += val; break;
            case 37: ted.ChassisHpBuff += val; break;
            case 38: ted.TurretHpBuff += val; break;
            case 39: ted.HpBuff += val; break;
            case 44: ted.AmmobayNodamgePercentBuff += val; break;
            case 45: ted.FueltankNodamgePercentBuff += val; break;
        }
    }

    private void SendShellInfo(GameSession session, Tank? tank)
    {
        if (tank == null) return;

        // Look up the gun for drum-size reference
        var gun = _gameTables.GetGun(tank.MainGunID);
        int drumSize = Math.Max(1, gun?.AmmoLoad ?? 20);

        var shellInfo = new PK_HC_ShellInfo { TankGuid = tank.TankGUID };
        var shellParts = tank.ShellSlots.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < 3; i++)
        {
            shellInfo.ShellInfoStruct[i].ShellSlotId = (byte)i;
            if (i >= shellParts.Length) continue;

            var parts = shellParts[i].Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) continue;

            int.TryParse(parts[0], out shellInfo.ShellInfoStruct[i].ShellID);
            int.TryParse(parts[1], out shellInfo.ShellInfoStruct[i].Count);

            // Drum fix: if DB has Count=0, use gun.AmmoLoad so client shows full drum
            // This is the ROOT CAUSE of empty drum on battle start (NM_HC_ShellInfo Count=0)
            if (shellInfo.ShellInfoStruct[i].Count == 0
                && shellInfo.ShellInfoStruct[i].ShellID > 0)
            {
                shellInfo.ShellInfoStruct[i].Count = drumSize;
            }
        }

        // If no shell was found at all, send slot 0 with gun defaults
        if (shellInfo.ShellInfoStruct[0].ShellID == 0 && gun != null)
        {
            shellInfo.ShellInfoStruct[0].ShellSlotId = 0;
            shellInfo.ShellInfoStruct[0].ShellID     = gun.Ammo1Id;
            shellInfo.ShellInfoStruct[0].Count       = drumSize;
        }

        session.Send(shellInfo);
    }

    private void SendAddedTank(GameSession session, Tank? tank)
    {
        if (tank == null) return;
        session.Send(new PK_HC_AddTank { TankDataItem = ConvertTankToTankData(tank) });
        SendShellInfo(session, tank);
        session.Send(new PK_HC_AllTankAttribute
        {
            TankCount = 1,
            TankAttributes = new[] { GenerateTankAttributes(tank) }
        });
    }

    private void SendSingleTankInfo(GameSession session, Tank? tank)
    {
        if (tank == null) return;
        session.Send(new PK_HC_TANKINFO { TankTotalCount = 1, TankDatas = new[] { ConvertTankToTankData(tank) } });
        SendShellInfo(session, tank);
        session.Send(new PK_HC_AllTankAttribute
        {
            TankCount = 1,
            TankAttributes = new[] { GenerateTankAttributes(tank) }
        });
    }

    private static CrewInfoData BuildCrewInfoData(CrewMember crew) => new()
    {
        Id = crew.CrewId,
        CurExp = crew.CurExp,
        CurLv = crew.CurLv,
        CrewGuid = crew.CrewGuid,
        TankGuid = crew.TankGuid,
        FamilyNameId = crew.FamilyNameId,
        NameId = crew.NameId,
        BigIconId = crew.BigIconId,
        SmallIconId = crew.SmallIconId,
        ResumeId = crew.ResumeId,
        Lastlearn = crew.Lastlearn,
        LastSkillExp = crew.LastSkillExp,
        SkillIds = BuildCrewSkillIdArray(crew.SkillIds)
    };

    private static List<(int SkillId, int Level)> ParseCrewSkills(string skillIds)
    {
        var result = new List<(int SkillId, int Level)>();
        if (string.IsNullOrWhiteSpace(skillIds))
        {
            return result;
        }

        foreach (var entry in skillIds.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !int.TryParse(parts[0], out int skillId) || skillId <= 0)
            {
                continue;
            }

            int level = 1;
            if (parts.Length > 1)
            {
                int.TryParse(parts[1], out level);
            }

            result.Add((skillId, Math.Max(1, level)));
        }

        return result;
    }

    private static string SerializeCrewSkills(IEnumerable<(int SkillId, int Level)> skillEntries) =>
        string.Join(";", skillEntries.Where(entry => entry.SkillId > 0).Select(entry => $"{entry.SkillId},{Math.Max(1, entry.Level)}"));

    private static int[] BuildCrewSkillIdArray(string skillIds)
    {
        var ids = new int[30];
        var entries = ParseCrewSkills(skillIds);
        for (int i = 0; i < Math.Min(ids.Length, entries.Count); i++)
        {
            ids[i] = entries[i].SkillId;
        }

        return ids;
    }

    private static int GetCrewSkillLevel(string skillIds, int skillId)
    {
        foreach (var entry in ParseCrewSkills(skillIds))
        {
            if (entry.SkillId == skillId)
            {
                return entry.Level;
            }
        }

        return 0;
    }

    private static string UpsertCrewSkillLevel(string skillIds, int skillId, int level)
    {
        var entries = ParseCrewSkills(skillIds);
        int index = entries.FindIndex(entry => entry.SkillId == skillId);
        if (index >= 0)
        {
            entries[index] = (skillId, Math.Max(1, level));
        }
        else
        {
            entries.Add((skillId, Math.Max(1, level)));
        }

        return SerializeCrewSkills(entries);
    }

    private void HandlePing(GameSession session, PK_CH_Ping msg) => session.Send(new PK_HC_Ping { Marker = msg.Marker });
    private void HandleCRPing(GameSession session, PK_CR_Ping msg) => session.Send(new PK_RC_Ping { Marker = msg.Marker });
    private void HandleServerTime(GameSession session)
    {
        Console.WriteLine($"[HS] HandleServerTime");
        if (!session.PlayMainDelivered)
        {
            Console.WriteLine("[HS] Skip early server time response until PlayMain is sent");
            return;
        }

        SendServerTimePacket(session);
    }

    private void HandleChat(GameSession session, PK_CH_HallChat msg)
    {
        Console.WriteLine($"[HS] Chat [{session.PlayerName}]: {msg.Message}");
        session.Send(new PK_HC_HallChatRes { PlayerName = session.PlayerName, Message = msg.Message });
    }

    private void HandleHallChatAll(GameSession session, PK_CH_HALLCHAT_ALL msg)
    {
        Console.WriteLine($"[HS] HallChatAll [{session.PlayerName}]: {msg.Message}");
        session.Send(new PK_HC_HallChatRes { PlayerName = session.PlayerName, Message = msg.Message });
    }

    private void HandleCreateTeam(GameSession session, PK_CH_Create_Team msg)
    {
        Console.WriteLine($"[HS] CreateTeam");
        session.Send(new PK_HC_Team_Info { TeamGuid = _random.Next(100000, 999999), BattleMode = msg.BattleMode, MemberCount = 1, MaxMemberCount = 3 });
    }

    private void HandleTeamReady(GameSession session, PK_CH_Team_Ready msg)
    {
        Console.WriteLine($"[HS] TeamReady: {msg.ReadyState}");
        session.Send(new PK_HC_Confirm_Ready { PlayerGuid = session.PlayerGUID, ReadyState = msg.ReadyState });
    }

    private void HandleQuickMatch(GameSession session, PK_CH_Quick_Match msg)
    {
        Console.WriteLine($"[HS] QuickMatch: Mode={msg.BattleMode}, Level={msg.BattleLevel} — redirecting to RoomServer");
        long tankGuid = GetSelectedTankGuid(session.PlayerGUID);
        MarkBattleTank(session.PlayerGUID, tankGuid);
        long roomGuid = Math.Abs(BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0));
        BattleModeRegistry.Record(roomGuid, session.PlayerGUID, msg.BattleMode, msg.BattleLevel);
        session.Send(new PK_HC_JoinRoomRes
        {
            Flag = 1, Certification = session.Certification,
            Ip = ResolveRoomServerIp(session), Port = 9601, RoomGUID = roomGuid,
            TankGUID = tankGuid, IsObserver = 0
        });
    }

    private void HandleStartMatch(GameSession session)
    {
        Console.WriteLine($"[HS] StartMatch — redirecting to RoomServer");
        long tankGuid = GetSelectedTankGuid(session.PlayerGUID);
        MarkBattleTank(session.PlayerGUID, tankGuid);
        long roomGuid = Math.Abs(BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0));
        session.Send(new PK_HC_JoinRoomRes
        {
            Flag = 1, Certification = session.Certification,
            Ip = ResolveRoomServerIp(session), Port = 9601, RoomGUID = roomGuid,
            TankGUID = tankGuid, IsObserver = 0
        });
    }

    private void HandleJoinRoom(GameSession session, PK_CH_RequestJoinRoom msg)
    {
        Console.WriteLine($"[HS] JoinRoom: Mode={msg.BattleMode}, Level={msg.BattleLevel}");

        // v67.1: Генералы — ОТДЕЛЬНЫЙ режим (не заменяет Захват/Упрощённый). Клиентская вкладка
        // «Генералы» (перепривязанная вкладка обучения, см. патч DLL) шлёт Commander(2) напрямую,
        // поэтому серверный хайджек слота больше НЕ нужен: Захват(0), Возрождение(1), Оборона(4),
        // Упрощённый(6) идут как есть. Для Commander(2) шлём BeginQueueWait, чтобы клиент загрузил
        // CommanderRoom. GWT_FORCE_MODE=<n> по-прежнему форсит режим глобально.
        byte effectiveMode = msg.BattleMode;
        var forcedMode = Environment.GetEnvironmentVariable("GWT_FORCE_MODE");
        if (byte.TryParse(forcedMode, out var fmode))
            effectiveMode = fmode;

        if (effectiveMode != msg.BattleMode)
        {
            Console.WriteLine($"[HS] Remapping battle mode {msg.BattleMode} -> {effectiveMode}");
            msg.BattleMode = effectiveMode;
        }
        if (msg.BattleMode == 2)                    // Генералы: переключить клиент в CommanderRoom
            session.Send(new PK_HC_BeginQueueWait { Flag = 1, TotalNum = 1, BattleMode = 2 });

        EnsurePlayerHangarConsistency(session.PlayerGUID);

        long tankGuid = GetSelectedTankGuid(session.PlayerGUID, msg.TankGUID);
        if (tankGuid == 0)
        {
            try
            {
                var newTank = _tankRepo.CreateStarterTank(session.PlayerGUID);
                tankGuid = newTank?.TankGUID ?? 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HS] Failed to create starter tank: {ex.Message}");
            }
        }
        MarkBattleTank(session.PlayerGUID, tankGuid);

        if (IsMultiplayerMode(msg.BattleMode))
        {
            TryMatchmake(session, tankGuid, msg.BattleMode, msg.BattleLevel);
        }
        else
        {
            // Single-player modes (BaseCapture, Simplified, etc.)
            SendSinglePlayerBattle(session, tankGuid, msg.BattleMode, msg.BattleLevel);
        }
    }

    // ── v12: экран «Подбор команды» (5 секунд) ──────────────────────────────────
    //
    // Перед отправкой JoinRoomRes клиенту показывается штатный экран очереди
    // (PK_HC_BeginQueueWait, id 452): «Вы член команды / Подбор команды / Участники
    // боя N» + две таблицы по уровням. Через QueueScreenSec секунд уходит
    // JoinRoomRes, клиент коннектится к RoomServer и экран закрывается сам при
    // загрузке боя — ровно как в оригинале.
    //
    // Показываем ТОЛЬКО для wire-режимов 0..3: ConvertModeFromServer у клиента даёт
    // 0=Standard, 1=Relive, 2=Commander, 3->6=Simplify — все есть в switch
    // обработчика. Для обороны (4) конвертация даёт 3, которого в switch НЕТ:
    // клиент залогирует ошибку и не выставит режим — поэтому оборона идёт как
    // раньше, без экрана (её очередь в оригинале — отдельный PVE-экран).
    private const int QueueScreenSec = 5;
    private const int MatchSearchWaitSec = 20;
    private const int CoopFoundDelayMs = 1500;
    private const int DefenseTeamCap = 6;
    private const int PvpRoomCap = 14;

    // v13 (баг 9): тир выбранного танка для честных цифр экрана подбора.
    private int GetQueueTier(long tankGuid)
    {
        try
        {
            var t = _tankRepo.GetTankByGUID(tankGuid);
            if (t != null) return _gameTables.GetTank(t.VehicleId)?.Tier ?? 1;
        }
        catch { }
        return 1;
    }

    private static int CountOnlineHumans()
    {
        int count = 0;
        foreach (var s in _hallByGuid.Values)
            if (s.IsConnected) count++;
        return Math.Max(1, count);
    }

    private void SendQueueSnapshot(GameSession session, byte wireMode, int playerTier, IReadOnlyList<int> realHumanTiers, int activeRoomCount, int targetHumansForPve)
    {
        if (!session.IsConnected) return;

        if (wireMode == 4 || wireMode == 5)
        {
            var pveTanks = new ushort[11];
            foreach (var tier in realHumanTiers)
            {
                int i = Math.Clamp(tier, 1, 11) - 1;
                pveTanks[i]++;
            }
            int curHumans = realHumanTiers.Count;
            var pveBattle = new ushort[5];
            pveBattle[0] = (ushort)Math.Max(1, activeRoomCount);
            session.Send(new PK_HC_PVEBeginQueueWait
            {
                Success           = 1,
                CurrentCount      = (byte)Math.Clamp(curHumans, 0, DefenseTeamCap),
                TotalCount        = (byte)Math.Max(DefenseTeamCap, targetHumansForPve),
                TotalWaitingCount = curHumans,
                TankLevelCount    = pveTanks,
                BattleLevelCount  = pveBattle,
                // CommonTools.ConvertModeFromServer(5) == 4 (EBattleMode.PVE); 4 maps to 3!
                BattleMode        = 5,
            });
            return;
        }

        if (wireMode > 3) return;

        var people = new ushort[11];
        var rooms  = new ushort[11];
        foreach (var tier in realHumanTiers)
        {
            int idx = Math.Clamp(tier, 1, 11) - 1;
            people[idx]++;
        }
        rooms[Math.Clamp(playerTier, 1, 11) - 1] = (ushort)Math.Max(1, activeRoomCount);

        session.Send(new PK_HC_BeginQueueWait
        {
            Flag         = 1,
            TotalNum     = realHumanTiers.Count,
            PeopleCounts = people,
            RoomCounts   = rooms,
            BattleMode   = wireMode,
        });
    }

    private void BroadcastLobbyUpdatesLocked(MatchLobby lobby, int activeRoomCount, long? excludePlayerGuid = null)
    {
        var tiers = lobby.Entries.Select(e => e.Tier).ToList();
        if (tiers.Count == 0) return;

        if (lobby.NormMode == 4)
        {
            var pveTanks = new ushort[11];
            foreach (var tier in tiers)
            {
                int idx = Math.Clamp(tier, 1, 11) - 1;
                pveTanks[idx]++;
            }
            var pveBattle = new ushort[5];
            pveBattle[0] = (ushort)Math.Max(1, activeRoomCount);
            byte cur = (byte)Math.Clamp(tiers.Count, 0, DefenseTeamCap);

            foreach (var entry in lobby.Entries)
            {
                if (excludePlayerGuid.HasValue && entry.PlayerGuid == excludePlayerGuid.Value) continue;
                if (!entry.Session.IsConnected) continue;
                try
                {
                    entry.Session.Send(new PK_HC_UpdateWaitingInfo
                    {
                        CurrentCount = cur,
                        TotalCount   = DefenseTeamCap,
                    });
                    entry.Session.Send(new PK_HC_PVEFreshWaitingBattle
                    {
                        TotalCount       = tiers.Count,
                        BattleLevelCount = pveBattle,
                        TankLevelCount   = pveTanks,
                    });
                }
                catch { }
            }
        }
        else
        {
            foreach (var entry in lobby.Entries)
            {
                if (excludePlayerGuid.HasValue && entry.PlayerGuid == excludePlayerGuid.Value) continue;
                if (!entry.Session.IsConnected) continue;
                try
                {
                    SendQueueSnapshot(entry.Session, entry.WireMode, entry.Tier, tiers, activeRoomCount, tiers.Count);
                }
                catch { }
            }
        }
    }

    private void SendQueueScreenThenJoin(GameSession session, byte wireMode, int playerTier, PK_HC_JoinRoomRes res, int delaySec = QueueScreenSec, IReadOnlyList<int>? humanTiers = null)
    {
        lock (_matchLock) _queueCancelled.Remove(session.PlayerGUID);
        var tiers = humanTiers ?? new[] { playerTier };
        SendQueueSnapshot(session, wireMode, playerTier, tiers, activeRoomCount: 1, targetHumansForPve: tiers.Count);
        Console.WriteLine($"[HS] Queue screen {delaySec}s (realPlayers={tiers.Count}) for {session.PlayerGUID} mode={wireMode}");

        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(delaySec * 1000);
            lock (_matchLock)
            {
                if (_queueCancelled.Remove(session.PlayerGUID))
                {
                    Console.WriteLine($"[HS] Queue cancelled by {session.PlayerGUID}");
                    return;
                }
            }
            try { if (session.IsConnected) session.Send(res); } catch { }
        });
    }

    /// <summary>
    /// Matchmaking for all modes:
    /// - Shows ONLY real human players on the "Подбор команды" screen (never bots).
    /// - If 2+ humans are online, holds the first player in queue (up to 60s) until a friend
    ///   presses Battle, then pairs both into the EXACT same RoomGUID (same map).
    /// - If a battle room in the same mode is still active, hot-joins late arrivals into it.
    /// </summary>
    private void TryMatchmake(GameSession session, long tankGuid, byte mode, byte level)
    {
        byte normMode = NormalizeMatchMode(mode);
        int myTier = GetQueueTier(tankGuid);
        int maxHumansInMode = normMode == 4 ? DefenseTeamCap : PvpRoomCap;

        lock (_matchLock)
        {
            _queueCancelled.Remove(session.PlayerGUID);

            var emptyLobbies = new List<byte>();
            foreach (var kv in _waitingLobbies)
            {
                kv.Value.Entries.RemoveAll(e => !e.Session.IsConnected || _queueCancelled.Contains(e.PlayerGuid));
                if (kv.Value.Entries.Count == 0)
                {
                    try { kv.Value.WaitCts.Cancel(); } catch { }
                    emptyLobbies.Add(kv.Key);
                }
            }
            foreach (var k in emptyLobbies) _waitingLobbies.Remove(k);

            var endedRooms = _activeRoomsByMode
                .Where(kv => !BattleModeRegistry.IsRoomActive(kv.Value.RoomGuid))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in endedRooms) _activeRoomsByMode.Remove(k);

            // 1. Join an already-open queue lobby for this mode (collects all players, not just 2!)
            if (_waitingLobbies.TryGetValue(normMode, out var openLobby)
                && openLobby.Entries.Count < maxHumansInMode)
            {
                int replaced = openLobby.Entries.RemoveAll(e => e.PlayerGuid == session.PlayerGUID);
                if (replaced > 0)
                    Console.WriteLine($"[HS] Warning: duplicate PlayerGUID={session.PlayerGUID} in queue lobby {openLobby.RoomGuid} ({session.RemoteEndPoint})");
                var joinEntry = new MatchEntry(session, session.PlayerGUID, tankGuid, openLobby.RoomGuid, mode, normMode, level, myTier, openLobby.WaitCts);
                openLobby.Entries.Add(joinEntry);

                var allTiers = openLobby.Entries.Select(e => e.Tier).ToList();
                int activeCount = Math.Max(1, _activeRoomsByMode.Count + 1);
                SendQueueSnapshot(session, mode, myTier, allTiers, activeRoomCount: activeCount, targetHumansForPve: maxHumansInMode);
                BroadcastLobbyUpdatesLocked(openLobby, activeCount, excludePlayerGuid: session.PlayerGUID);

                Console.WriteLine($"[HS] Joined queue lobby mode={normMode} room={openLobby.RoomGuid}: {openLobby.Entries.Count}/{maxHumansInMode} real players");

                if (openLobby.Entries.Count >= maxHumansInMode)
                {
                    _waitingLobbies.Remove(normMode);
                    try { openLobby.WaitCts.Cancel(); } catch { }
                    Task.Run(async () =>
                    {
                        await Task.Delay(CoopFoundDelayMs);
                        LaunchMatchLobby(openLobby);
                    });
                }
                return;
            }

            // old block removed

            // 2. Otherwise, if there is an active battle with free human slots, hot-join it
            if (_activeRoomsByMode.TryGetValue(normMode, out var active)
                && BattleModeRegistry.IsRoomActive(active.RoomGuid)
                && BattleModeRegistry.GetRoomHumanCount(active.RoomGuid) < maxHumansInMode)
            {
                BattleModeRegistry.Record(active.RoomGuid, session.PlayerGUID, mode, level);
                int liveHumans = Math.Clamp(BattleModeRegistry.GetRoomHumanCount(active.RoomGuid), 1, maxHumansInMode);
                var tiers = new List<int>(liveHumans) { myTier };
                while (tiers.Count < liveHumans) tiers.Add(active.Tier);

                Console.WriteLine($"[HS] Hot-joining active room={active.RoomGuid} ({liveHumans}/{maxHumansInMode} humans) P={session.PlayerGUID} mode={mode}");
                SendQueueScreenThenJoin(session, mode, myTier, new PK_HC_JoinRoomRes
                {
                    Flag = 1, Certification = session.Certification,
                    Ip = ResolveRoomServerIp(session), Port = 9601, RoomGUID = active.RoomGuid,
                    TankGUID = tankGuid, IsObserver = 0
                }, delaySec: 2, humanTiers: tiers);
                return;
            }

            // 3. Open a fresh 20-second matchmaking lobby for this mode
            long roomGuid = Math.Abs(BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0));
            var cts = new CancellationTokenSource();
            var lobby = new MatchLobby
            {
                RoomGuid = roomGuid,
                NormMode = normMode,
                WaitCts  = cts,
            };
            var firstEntry = new MatchEntry(session, session.PlayerGUID, tankGuid, roomGuid, mode, normMode, level, myTier, cts);
            lobby.Entries.Add(firstEntry);
            _waitingLobbies[normMode] = lobby;

            int roomsShown = Math.Max(1, _activeRoomsByMode.Count + 1);
            SendQueueSnapshot(session, mode, myTier, new[] { myTier }, activeRoomCount: roomsShown, targetHumansForPve: maxHumansInMode);
            Console.WriteLine($"[HS] Opened {MatchSearchWaitSec}s lobby P1={session.PlayerGUID} mode={mode} norm={normMode} room={roomGuid}");

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(MatchSearchWaitSec * 1000, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                bool shouldLaunch = false;
                lock (_matchLock)
                {
                    if (_waitingLobbies.TryGetValue(normMode, out var cur) && ReferenceEquals(cur, lobby))
                    {
                        _waitingLobbies.Remove(normMode);
                        shouldLaunch = true;
                    }
                }
                if (shouldLaunch) LaunchMatchLobby(lobby);
            });
        }
    }

    private void LaunchMatchLobby(MatchLobby lobby)
    {
        List<MatchEntry> valid;
        lock (_matchLock)
        {
            valid = lobby.Entries
                .Where(e => !_queueCancelled.Remove(e.PlayerGuid) && e.Session.IsConnected)
                .ToList();
            if (valid.Count == 0) return;

            foreach (var e in valid)
                BattleModeRegistry.Record(lobby.RoomGuid, e.PlayerGuid, e.WireMode, e.Level);

            BattleModeRegistry.SetExpectedPlayers(lobby.RoomGuid, valid.Count);
            _activeRoomsByMode[lobby.NormMode] = valid[0] with { WaitCts = null };
        }

        Console.WriteLine($"[HS] Launching match Room={lobby.RoomGuid} normMode={lobby.NormMode} realPlayers={valid.Count}");
        foreach (var e in valid)
        {
            try
            {
                Console.WriteLine($"[HS]   -> JoinRoomRes P={e.PlayerGuid} ({e.Session.RemoteEndPoint}) room={lobby.RoomGuid}");
                e.Session.Send(new PK_HC_JoinRoomRes
                {
                    Flag = 1,
                    Certification = e.Session.Certification,
                    Ip = ResolveRoomServerIp(e.Session),
                    Port = 9601,
                    RoomGUID = lobby.RoomGuid,
                    TankGUID = e.TankGuid,
                    IsObserver = 0
                });
            }
            catch { }
        }
    }

    private void RemovePlayerFromWaitingLobbiesLocked(long playerGuid)
    {
        _queueCancelled.Add(playerGuid);
        var toRemove = new List<byte>();
        foreach (var kv in _waitingLobbies)
        {
            int removed = kv.Value.Entries.RemoveAll(e => e.PlayerGuid == playerGuid);
            if (removed == 0) continue;
            if (kv.Value.Entries.Count == 0)
            {
                try { kv.Value.WaitCts.Cancel(); } catch { }
                toRemove.Add(kv.Key);
            }
            else
            {
                BroadcastLobbyUpdatesLocked(kv.Value, Math.Max(1, _activeRoomsByMode.Count + 1));
            }
        }
        foreach (var k in toRemove) _waitingLobbies.Remove(k);
    }

    private void SendSinglePlayerBattle(GameSession session, long tankGuid, byte mode, byte level)
    {
        long roomGuid = Math.Abs(BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0));
        BattleModeRegistry.Record(roomGuid, session.PlayerGUID, mode, level);
        // ExpectedPlayers defaults to 1 in BattleModeRegistry
        SendQueueScreenThenJoin(session, mode, GetQueueTier(tankGuid), new PK_HC_JoinRoomRes
        {
            Flag = 1, Certification = session.Certification,
            Ip = ResolveRoomServerIp(session), Port = 9601, RoomGUID = roomGuid,
            TankGUID = tankGuid, IsObserver = 0
        });
    }

    private void HandleCheckIntegrity(GameSession session, PK_CH_CheckIntegrity msg) { }
    private void HandlePlayerSuccessInCarport(GameSession session) { }

    private async void HandleLoadCarport(GameSession session, PK_CH_LoadCarport? msg = null)
    {
        byte loadFlag = msg?.LoadFlag ?? 0;
        if (loadFlag == 2 && session.PlayMainSent) return;

        Console.WriteLine($"[HS] HandleLoadCarport (LoadFlag={loadFlag})");
        var data = GetPlayerData(session);
        if (data == null) return;

        if (!session.HangarConsistencyChecked)
        {
            EnsurePlayerHangarConsistency(data.PlayerGUID);
            session.HangarConsistencyChecked = true;
        }

        if (!session.InitialTankInfoSent)
        {
            var tanks = _tankRepo.GetPlayerTanks(data.PlayerGUID);
            var tankDataList = new List<TankData>(tanks.Count);
            foreach (var tank in tanks)
            {
                tankDataList.Add(ConvertTankToTankData(tank));
            }
            session.Send(new PK_HC_TANKINFO { TankTotalCount = (byte)tankDataList.Count, TankDatas = tankDataList.ToArray() });

            var crewByTank = BuildCrewLevelLookup(data.PlayerGUID);
            var tankAttrs = new TankAllAttributeStruct[tanks.Count];
            for (int i = 0; i < tanks.Count; i++)
            {
                tankAttrs[i] = GenerateTankAttributes(tanks[i], crewByTank);
            }
            session.Send(new PK_HC_AllTankAttribute { TankCount = (byte)tanks.Count, TankAttributes = tankAttrs });
            SendResearchState(session, data.PlayerGUID);
            SendCasernUpdate(session, data.CasernCapacity);
            session.InitialTankInfoSent = true;
        }
        else if (session.PlayMainSent && loadFlag != 2)
        {
            // Returning to hangar on an existing session: sync lightweight state only.
            SendResearchState(session, data.PlayerGUID);
            SendCasernUpdate(session, data.CasernCapacity);
            SendMoneyExp(session, data);
        }

        if (session.PlayMainSent) return;
        session.PlayMainSent = true;
        
        await System.Threading.Tasks.Task.Delay(2000);
        
        Console.WriteLine($"[HS] Sending PlayMain: PlayerName={data.PlayerName}, Silver={data.Silver}, Gold={data.Gold}");
        session.Send(new PK_HC_PlayMain
        {
            PlayerGUID = data.PlayerGUID, PlayerName = data.PlayerName,
            // CarportNum — вместимость ангара. ВАЖНО: клиент считает максимумом 85
            // (TankSelectList.OnClickBuyPort: при CarportNum >= 85 показывает «слоты
            // на максимуме», а GetRewardRule запрещает выдачу при HallTanks.Count >=
            // CarportNum). Если прислать 100, клиент решит, что все слоты уже куплены,
            // даже на новом аккаунте. Поэтому берём CasernCapacity, но клампим в
            // 1..85. По умолчанию 85 — потолок клиента, этого хватает на все призы.
            CarportNum = ComputeCarportNum(data),
            LastSelect = 0, TankProjectIndex = 1,
            GameMoney = data.Silver, GoldMoney = data.Gold,
            LeftRenameTime = 0, PlayerPlatformId = 1,
            StandaloneReward = (byte)(_accountRepo.IsStandaloneRewardClaimed(data.PlayerGUID) ? 1 : 0), FirstLoginFlag = 0
        });
        session.PlayMainDelivered = true;
        Console.WriteLine($"[HS] Sending ChangeMoneyExp: Silver={data.Silver}, Gold={data.Gold}, Exp={data.Experience}");
        SendMoneyExp(session, data);
        SendServerTimePacket(session);
        Console.WriteLine($"[HS] PlayMain and Money/Exp sent after 2s delay");
    }

    private void SendServerTimePacket(GameSession session)
    {
        var now = DateTime.Now;
        var unixTime = new DateTimeOffset(now).ToUnixTimeSeconds();
        var timeString = now.ToString("yyyy-MM-dd HH:mm:ss");
        session.Send(new PK_HC_RespServerTime { UnixTime = unixTime, TimeString = timeString });
    }

    private TankData ConvertTankToTankData(Tank tank)
    {
        var td = new TankData
        {
            TankGUID = tank.TankGUID, PlayerGUID = tank.PlayerGUID,
            CarportIndex = tank.CarportIndex, VehicleId = tank.VehicleId,
            HullID = tank.HullID, UnderpanID = tank.UnderpanID,
            TurretID = tank.TurretID, MainGunID = tank.MainGunID,
            EngineID = tank.EngineID, FueltankID = tank.FueltankID,
            CartridgeID = tank.CartridgeID, RadioStationID = tank.RadioStationID,
            GaugeID = tank.GaugeID, TankHp = tank.TankHp,
            TankExp = tank.TankExp, TankCurHp = tank.TankCurHp,
            AutoRepair = tank.AutoRepair, TankCurrencytType = ResolveClientCurrencyType(tank),
            GoldTankLeftTime = ResolveGoldTankLeftTime(tank),
            CurTankCoatId = tank.CurTankCoatId, CurRoomTankCoatID = tank.CurRoomTankCoatID,
            CurShellId = tank.CurShellId, ModificationId = tank.ModificationId,
            IsElite = true, AutoFillCostFlag = tank.AutoFillCostFlag  // v48.2: элит → доступна модификация
        };

        // v48.2: согласование HP с клиентом. Клиент считает макс. HP как
        // EHpBuff+ETurretHpBuff (= realTotalHp + (realTotalHp − hullHp), см. BuildTankAttributes),
        // а «Модифицировать» требует TankCurHp == TankHp. Раньше слали HP в «корпусной» шкале
        // (tankEntry.MaxHp), клиент считал макс иначе → танк всегда «повреждён», модификация
        // не проходила (ошибка со скрина). Приводим к клиентской шкале, сохраняя долю здоровья
        // (повреждённый в бою так и просит ремонт, целый — модифицируется).
        var teHp = _gameTables.GetTank(tank.VehicleId);
        if (teHp != null)
        {
            // v48.4: проверка модификации (OnBuyBtn) сверяет HP БОЕВОГО танка: TankCurHp != TankHp
            // → «повреждена, отремонтируйте». Клиент сам переопределяет TankHp = EHpBuff+ETurretHpBuff,
            // а TankCurHp берёт с сервера — если танк был повреждён в бою, модификация блокируется.
            // На приватном/тест-сервере это только мешает: в АНГАРЕ отдаём HP всегда полным
            // (TankCurHp = TankHp = клиентский максимум). Бой всё равно стартует с полным HP
            // (RoomServer считает пул по GetRealMaxHp), так что урон не теряется по смыслу.
            // v53 (задача 6): HP карточки — по РЕАЛЬНЫМ модулям танка (корпус + башня), а не по
            // стоку из ExactHangarHp по имени. Раньше смена корпуса/башни HP не меняла вовсе.
            int clientMax = _gameTables.GetTankMaxHp(tank.VehicleId, tank.HullID, tank.TurretID);
            if (clientMax > 0)
            {
                td.TankHp = clientMax;
                td.TankCurHp = clientMax;   // всегда «целый» в ангаре → модификация не блокируется
            }
        }

        FillShellSlots(tank.ShellSlots, td.ShellSlots);
        FillShellSlots(tank.DeployShellSlots, td.DeployShellSlots);

        // v68 (п.1): тот же фикс, что для PK_HC_ShellInfo при старте — если в БД Count=0, ставим
        // ПОЛНЫЙ боезапас (gun.AmmoLoad). Клиент при RESET (скилл пополнения / ВОЗРОЖДЕНИЕ) вызывает
        // ResetShellSlotData, который копирует td.ShellSlots в текущий боезапас. Раньше td.ShellSlots
        // тут не чинился → после пополнения/возрождения барабан восполнялся до УРЕЗАННОГО значения
        // (максимум «откатывался»). Теперь td.ShellSlots содержит полный боезапас → восполнение полное.
        {
            var gunForDrum = _gameTables.GetGun(tank.MainGunID);
            int drumFull = Math.Max(1, gunForDrum?.AmmoLoad ?? 20);
            foreach (var arr in new[] { td.ShellSlots, td.DeployShellSlots })
                for (int i = 0; i < arr.Length; i++)
                    if (arr[i].ID > 0 && arr[i].Count == 0)
                        arr[i].Count = drumFull;
        }
        if (!HasConfiguredShells(td.DeployShellSlots))
        {
            for (int i = 0; i < td.ShellSlots.Length; i++)
            {
                td.DeployShellSlots[i] = td.ShellSlots[i];
            }
        }

        FillIntArray(tank.Provisions, td.Provisions);
        FillIntArray(tank.DeployProvisions, td.DeployProvisions);
        FillIntArray(tank.SignatureIds, td.SignatureId);
        FillLongArray(tank.SignatureTimes, td.SignatureTime);
        FillIntArray(tank.EmblemIds, td.EmblemId);
        FillLongArray(tank.EmblemTimes, td.EmblemTime);
        FillBehaveArray(tank.BehaveArray, td.BehaveArray);

        if (td.CurShellId == 0)
        {
            for (int i = 0; i < td.DeployShellSlots.Length; i++)
            {
                if (td.DeployShellSlots[i].ID > 0)
                {
                    td.CurShellId = td.DeployShellSlots[i].ID;
                    break;
                }
            }
        }

        return td;
    }

    // Permanent gold tanks are stored with GoldTankLeftTime <= 0. The client shows a
    // "доступен ещё N дн." label + "продлить / купить навсегда" panel for any gold
    // (currency-type-2) tank with GoldTankLeftTime > 0. The old workaround reported a
    // ~50-year remaining time to dodge the "about to expire" nag, but that made the
    // client render "доступен ещё 18249 дн." on a tank that is actually permanent.
    //
    // Correct fix: report such tanks with the client's dedicated PERMANENT gold-tank
    // currency type 4 (UserTankIsGoldNoLimitType / IsGoldNoLimit == true) and
    // GoldTankLeftTime = 0. Then TankSelectList.SetGoldTankTimeVisible hides the whole
    // time/buy panel, GetLeftTimeString returns empty, the expiry nag (which requires
    // TankCurrencytType == 2) never fires, and the shop treats the tank as HasInGarage.
    // Gold STYLING (frame/icon) is unaffected: it keys off Table_Vehicle.currency, not
    // this per-instance field. Rented tanks with a real positive duration keep type 2
    // and their real remaining time.
    private static int ResolveGoldTankLeftTime(Tank tank)
    {
        if (tank.TankCurrencyType == 2 && tank.GoldTankLeftTime <= 0)
            return 0; // постоянный золотой танк: без остатка времени → нет «доступен ещё»
        return tank.GoldTankLeftTime;
    }

    // Валюта-тип танка, отправляемая клиенту. Постоянный золотой танк (type 2, срок <= 0)
    // → type 4 (GoldNoLimit): клиент прячет панель времени/продления и считает танк
    // навсегда своим. Арендованный (срок > 0) остаётся type 2. Остальное — как в БД.
    private static byte ResolveClientCurrencyType(Tank tank)
    {
        if (tank.TankCurrencyType == 2 && tank.GoldTankLeftTime <= 0)
            return 4; // постоянный золотой танк без ограничения времени
        return tank.TankCurrencyType;
    }

    private static void FillShellSlots(string rawValue, ShellSlot[] target)
    {
        Array.Clear(target, 0, target.Length);
        if (string.IsNullOrWhiteSpace(rawValue)) return;

        var slots = rawValue.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Math.Min(target.Length, slots.Length); i++)
        {
            var parts = slots[i].Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            int.TryParse(parts[0], out target[i].ID);
            int.TryParse(parts[1], out target[i].Count);
        }
    }

    private void SendInventoryUpdate(GameSession session, long playerGuid, byte itemType, int itemId)
    {
        session.Send(new PK_HC_UPDATEITEM
        {
            ItemType = itemType,
            ItemId = itemId,
            ItemNum = _inventoryRepo.GetItemCount(playerGuid, itemType, itemId)
        });
    }

    private static ShellSlot[] ParseShellSlots(string rawValue)
    {
        var slots = new ShellSlot[3];
        FillShellSlots(rawValue, slots);
        return slots;
    }

    private static string SerializeShellSlots(IReadOnlyList<ShellSlot> slots)
    {
        return string.Join(
            ";",
            Enumerable.Range(0, 3)
                .Select(index =>
                {
                    if (index >= slots.Count)
                    {
                        return "0,0";
                    }

                    return $"{slots[index].ID},{slots[index].Count}";
                }));
    }

    private static int GetFirstConfiguredShellId(IEnumerable<ShellSlot> slots)
    {
        foreach (var slot in slots)
        {
            if (slot.ID > 0 && slot.Count > 0)
            {
                return slot.ID;
            }
        }

        return 0;
    }

    private static bool HasConfiguredShells(ShellSlot[] slots) => slots.Any(slot => slot.ID > 0 || slot.Count > 0);

    private static void FillIntArray(string rawValue, int[] target)
    {
        Array.Clear(target, 0, target.Length);
        if (string.IsNullOrWhiteSpace(rawValue)) return;

        var parts = rawValue.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Math.Min(target.Length, parts.Length); i++)
        {
            int.TryParse(parts[i], out target[i]);
        }
    }

    private static void FillLongArray(string rawValue, long[] target)
    {
        Array.Clear(target, 0, target.Length);
        if (string.IsNullOrWhiteSpace(rawValue)) return;

        var parts = rawValue.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Math.Min(target.Length, parts.Length); i++)
        {
            long.TryParse(parts[i], out target[i]);
        }
    }

    private static void FillBehaveArray(string rawValue, BehaveStruct[] target)
    {
        for (int i = 0; i < target.Length; i++)
        {
            target[i].BehaveType = 0;
            target[i].PrizeType = 0;
            target[i].Multiple = 1f;
        }

        if (string.IsNullOrWhiteSpace(rawValue)) return;

        var entries = rawValue.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Math.Min(target.Length, entries.Length); i++)
        {
            var parts = entries[i].Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0) byte.TryParse(parts[0], out target[i].BehaveType);
            if (parts.Length > 1) byte.TryParse(parts[1], out target[i].PrizeType);
            if (parts.Length > 2 &&
                float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float multiple))
            {
                target[i].Multiple = multiple;
            }
        }
    }

    // Thin wrapper around the shared GameTablesService.BuildTankAttributes — this used to
    // be a separate, hand-written 57-float array here that put values at indices which
    // don't match the real EqAttributeType enum at all (e.g. reload time was written to
    // index 27, which is actually EFragmentFactor; real EReloadTime is 24), and never
    // looked up the tank's actual equipped gun/shell for penetration and damage. That's
    // exactly why the research-tree tooltip (which reads this struct over the network,
    // PK_HC_AllTankAttribute) showed nonsense like "Infinity" rate of fire, 0 payload, and
    // a wildly wrong top speed, while the hangar's tank-detail panel (which computes its
    // numbers locally from the tank's actual installed parts, not this struct) showed the
    // correct values. Now both — and battle — pull from the same place.
    private Dictionary<long, int> BuildCrewLevelLookup(long playerGuid)
    {
        var map = new Dictionary<long, int>();
        foreach (var c in _crewRepo.GetPlayerCrew(playerGuid))
        {
            if (c.TankGuid != 0 && !map.ContainsKey(c.TankGuid))
                map[c.TankGuid] = c.CurLv;
        }
        return map;
    }

    private TankAllAttributeStruct GenerateTankAttributes(Tank tank, Dictionary<long, int>? crewLevelByTank = null)
    {
        var tankEntry = _gameTables.GetTank(tank.VehicleId);
        // v74: считаем атрибуты (в т.ч. грузоподъёмность/массу) по РЕАЛЬНО установленным
        // модулям танка игрока, а не по стоковым ID из статической таблицы — иначе после
        // установки улучшенной ходовой сервер продолжал бы слать клиенту старую
        // (стоковую) грузоподъёмность, и клиент ошибочно блокировал установку новой
        // пушки как «недостаточная грузоподъёмность».
        var equipped = tankEntry != null
            ? _gameTables.WithEquippedModules(tankEntry, tank.HullID, tank.UnderpanID, tank.TurretID,
                                               tank.EngineID, tank.FueltankID, tank.CartridgeID,
                                               tank.RadioStationID, tank.GaugeID)
            : null;
        int gunId = tank.MainGunID;
        int shellId = tank.CurShellId > 0 ? tank.CurShellId : (_gameTables.GetGun(gunId)?.Ammo1Id ?? 0);
        var attrs = _gameTables.BuildTankAttributes(equipped, gunId, shellId, tank.TankCurHp);
        // Crew reload bonus (2.4775% per level → 29.73% at level 12), applied HERE to
        // EReloadTime (attrs[24]). The client only applies the crew effect in the HANGAR
        // (ComputeNewAttribute, patched in the DLL); IN BATTLE it reads this server value
        // directly (AimAreaUI → AttributeContainer.GetValue(EReloadTime)), so without this
        // the crew never sped up reload in a fight. Hangar preview uses client tables, so
        // there's no double-count.
        if (attrs != null && attrs.Length > 24 && attrs[24] > 0.001f)
        {
            int crewLv;
            if (crewLevelByTank != null)
            {
                crewLevelByTank.TryGetValue(tank.TankGUID, out crewLv);
            }
            else
            {
                var crew = _crewRepo.GetPlayerCrew(tank.PlayerGUID)
                                    .FirstOrDefault(c => c.TankGuid == tank.TankGUID);
                crewLv = crew?.CurLv ?? 0;
            }
            if (crewLv > 0)
            {
                float rate = Math.Min(crewLv * (0.2291667f / 12f), 0.9f); // clamp so reload can't hit 0
                attrs[24] *= (1f - rate);
            }
        }
        return new TankAllAttributeStruct { TankGUID = tank.TankGUID, Attributes = attrs };
    }

    private bool AreResearchParentsSatisfied(long playerGuid, int tankId, ResearchEntry researchEntry)
    {
        foreach (int parentNodeId in researchEntry.ParentNodeIds)
        {
            var parentNode = _gameTables.GetResearchNode(tankId, parentNodeId);
            if (parentNode == null)
            {
                continue;
            }

            if (parentNode.PartType == 100)
            {
                if (!_tankRepo.HasTankByVehicleId(playerGuid, parentNode.PartId))
                {
                    return false;
                }

                continue;
            }

            if (_moduleRepo == null || !_moduleRepo.IsModuleResearched(playerGuid, tankId, parentNode.PartType, parentNode.PartId))
            {
                return false;
            }
        }

        return true;
    }

    private static int NormalizeCurrentHpForEquipmentChange(int oldMaxHp, int oldCurHp, int newMaxHp)
    {
        if (newMaxHp <= 0)
        {
            return 1;
        }

        if (oldMaxHp <= 0)
        {
            return Math.Clamp(oldCurHp, 0, newMaxHp);
        }

        double ratio = Math.Clamp((double)oldCurHp / oldMaxHp, 0d, 1d);
        int normalized = (int)Math.Round(ratio * newMaxHp);
        if (oldCurHp > 0 && normalized == 0)
        {
            normalized = 1;
        }

        return Math.Clamp(normalized, 0, newMaxHp);
    }

    private void HandleGetLoginReward(GameSession session) => session.Send(new PK_HC_LoginReward { WeekID = 0, Days = 0, IsGet = 0 });
    private void HandleTankRankData(GameSession session, PK_CH_TankRankData msg) => session.Send(new PK_HC_TankRankData { SendNewData = 0, DelPrevData = 0, EndTankData = 0, Count = 0, TankID = msg.TankId, DataSendTime = 0 });
    private void HandlePlayerRankData(GameSession session) => session.Send(new PK_HC_PlayerRankData { SendNewData = 0, DelPrevData = 0, EndPlayerData = 0, Count = 0 });
    private void HandleResearchRequest(GameSession session, PK_CH_ResearchRequest msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        EnsurePlayerHangarConsistency(data.PlayerGUID);

        Console.WriteLine($"[HS] ResearchRequest: Type={msg.TypeId}, TableId={msg.TableId}, TankId={msg.TankId}");

        if (msg.TypeId == 0 || msg.TableId == 0)
        {
            SendResearchState(session, data.PlayerGUID);
            return;
        }

        if (msg.TypeId == 100)
        {
            int vehicleId = msg.TableId;
            if (_tankRepo.HasTankByVehicleId(data.PlayerGUID, vehicleId))
            {
                SendResearchState(session, data.PlayerGUID);
                return;
            }

            var tankEntry = _gameTables.GetTank(vehicleId);
            if (tankEntry == null)
            {
                Console.WriteLine($"[HS] ResearchRequest: unknown tank {vehicleId}");
                SendResearchState(session, data.PlayerGUID);
                return;
            }

            var account = _accountRepo.GetByPlayerGUID(data.PlayerGUID);
            if (account == null) return;

            if (account.Silver < tankEntry.CostSilver || account.Gold < tankEntry.CostGold)
            {
                SendMoneyExpByGuid(session, data.PlayerGUID);
                return;
            }

            using var tx = _db.Connection.BeginTransaction();
            Tank newTank;
            try
            {
                _accountRepo.UpdateResources(
                    data.PlayerGUID,
                    silver: account.Silver - tankEntry.CostSilver,
                    gold: account.Gold - tankEntry.CostGold,
                    tx: tx);

                newTank = _tankRepo.CreateTank(
                    data.PlayerGUID,
                    vehicleId,
                    tankEntry.HullId,
                    tankEntry.UnderpanId,
                    tankEntry.TurretId,
                    tankEntry.MainGunId,
                    tankEntry.EngineId,
                    tankEntry.FueltankId,
                    tankEntry.CartridgeId,
                    tankEntry.RadioStationId,
                    tankEntry.GaugeId,
                    tankEntry.MaxHp, tx);

                tx.Commit();
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                Console.WriteLine($"[HS] BuyTank transaction failed: {ex}");
                SendMoneyExpByGuid(session, data.PlayerGUID);
                SendResearchState(session, data.PlayerGUID);
                return;
            }

            _moduleRepo?.ResearchTankBaseModules(
                data.PlayerGUID,
                vehicleId,
                tankEntry.HullId,
                tankEntry.UnderpanId,
                tankEntry.TurretId,
                tankEntry.MainGunId,
                tankEntry.EngineId,
                tankEntry.FueltankId,
                tankEntry.CartridgeId,
                tankEntry.RadioStationId,
                tankEntry.GaugeId);

            SendAddedTank(session, newTank);
            SendMoneyExpByGuid(session, data.PlayerGUID);
            SendResearchState(session, data.PlayerGUID);
            return;
        }

        if (_moduleRepo == null || !ModuleRepository.TryNormalizeClientResearchModuleType(msg.TypeId, out int researchTypeId))
        {
            SendResearchState(session, data.PlayerGUID);
            return;
        }

        int vehicleModuleId = msg.TankId > 0 ? msg.TankId : 0;
        if (vehicleModuleId == 0)
        {
            SendResearchState(session, data.PlayerGUID);
            return;
        }

        if (_moduleRepo.IsModuleResearched(data.PlayerGUID, vehicleModuleId, researchTypeId, msg.TableId))
        {
            SendResearchState(session, data.PlayerGUID);
            return;
        }

        var ownedTank = _tankRepo.GetPlayerTanks(data.PlayerGUID).FirstOrDefault(t => t.VehicleId == vehicleModuleId);
        if (ownedTank == null)
        {
            SendResearchState(session, data.PlayerGUID);
            return;
        }

        var part = _gameTables.GetPart(researchTypeId, msg.TableId);
        if (part == null)
        {
            Console.WriteLine($"[HS] ResearchRequest: unknown module type={researchTypeId}, id={msg.TableId} for tank={vehicleModuleId}");
            SendResearchState(session, data.PlayerGUID);
            return;
        }

        var researchEntry = _gameTables.GetResearchEntry(vehicleModuleId, researchTypeId, msg.TableId);
        if (researchEntry != null)
        {
            if (data.Level < researchEntry.RequiredLevel || !AreResearchParentsSatisfied(data.PlayerGUID, vehicleModuleId, researchEntry))
            {
                SendResearchState(session, data.PlayerGUID);
                return;
            }

            if (researchEntry.ExpCost > 0)
            {
                if (ownedTank.TankExp >= researchEntry.ExpCost)
                {
                    // хватает опыта машины — списываем с него
                    int newTankExp = ownedTank.TankExp - researchEntry.ExpCost;
                    _tankRepo.UpdateTankExp(ownedTank.TankGUID, newTankExp);
                    ownedTank.TankExp = newTankExp;
                    SendTankExp(session, ownedTank);
                }
                else
                {
                    // v66 (п.«опыт»): не хватает опыта МАШИНЫ — добираем недостающее из
                    // СВОБОДНОГО опыта аккаунта (свободный опыт универсален, как в WoT). Так можно
                    // исследовать танки с нулевым опытом машины (в т.ч. выданные читом). Всё в int —
                    // никакого переполнения. Если и суммарно (машина+свободный) мало — не исследуем.
                    int need = researchEntry.ExpCost - ownedTank.TankExp;
                    var acc = _accountRepo.GetByPlayerGUID(data.PlayerGUID);
                    if (acc == null || acc.Experience < need)
                    {
                        SendTankExp(session, ownedTank);
                        SendResearchState(session, data.PlayerGUID);
                        return;
                    }
                    _accountRepo.UpdateResources(data.PlayerGUID, experience: acc.Experience - need);
                    _tankRepo.UpdateTankExp(ownedTank.TankGUID, 0);
                    ownedTank.TankExp = 0;
                    SendMoneyExpByGuid(session, data.PlayerGUID);   // обновить свободный опыт у клиента
                    SendTankExp(session, ownedTank);
                }
            }
        }

        _moduleRepo.AddResearchedModule(data.PlayerGUID, vehicleModuleId, researchTypeId, msg.TableId);
        session.Send(new PK_HC_ResearchResult
        {
            PartCount = 1,
            PartList = new[] { new ResearchNode { TypeId = researchTypeId, Id = msg.TableId } }
        });
        SendResearchState(session, data.PlayerGUID);
    }

    private void HandleBuyItem(GameSession session, PK_CH_BUYITEM msg)
    {
        Console.WriteLine($"[HS] BuyItem: Type={msg.ItemType}, ItemId={msg.ItemId}, ItemNum={msg.ItemNum}");
        var data = GetPlayerData(session);
        if (data == null) return;

        int itemNum = Math.Max(1, msg.ItemNum);

        if (msg.ItemType == ItemTypeVehicle)
        {
            var tankEntry = _gameTables.GetTank(msg.ItemId);
            if (tankEntry == null || _tankRepo.HasTankByVehicleId(data.PlayerGUID, msg.ItemId))
            {
                session.Send(new PK_HC_BUYITEM { IsBuy = 0 });
                return;
            }

            if (data.Silver < tankEntry.CostSilver || data.Gold < tankEntry.CostGold)
            {
                session.Send(new PK_HC_BUYITEM { IsBuy = 0 });
                return;
            }

            using var tx = _db.Connection.BeginTransaction();
            Tank newTank;
            try
            {
                _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver - tankEntry.CostSilver, gold: data.Gold - tankEntry.CostGold, tx: tx);
                newTank = _tankRepo.CreateTank(
                    data.PlayerGUID,
                    tankEntry.Id,
                    tankEntry.HullId,
                    tankEntry.UnderpanId,
                    tankEntry.TurretId,
                    tankEntry.MainGunId,
                    tankEntry.EngineId,
                    tankEntry.FueltankId,
                    tankEntry.CartridgeId,
                    tankEntry.RadioStationId,
                    tankEntry.GaugeId,
                    Math.Max(1, _gameTables.GetHullMaxHp(tankEntry.Id, tankEntry.HullId)), tx);
                tx.Commit();
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                Console.WriteLine($"[HS] HandleBuyItem (vehicle) failed: {ex}");
                session.Send(new PK_HC_BUYITEM { IsBuy = 0 });
                SendMoneyExpByGuid(session, data.PlayerGUID);
                return;
            }

            _moduleRepo?.ResearchTankBaseModules(
                data.PlayerGUID,
                tankEntry.Id,
                tankEntry.HullId,
                tankEntry.UnderpanId,
                tankEntry.TurretId,
                tankEntry.MainGunId,
                tankEntry.EngineId,
                tankEntry.FueltankId,
                tankEntry.CartridgeId,
                tankEntry.RadioStationId,
                tankEntry.GaugeId);

            session.Send(new PK_HC_BUYITEM { IsBuy = 1 });
            SendAddedTank(session, newTank);
            SendResearchState(session, data.PlayerGUID);
            SendMoneyExpByGuid(session, data.PlayerGUID);
            return;
        }

        if (msg.ItemType >= 1 && msg.ItemType <= 10)
        {
            var part = _gameTables.GetPart(msg.ItemType, msg.ItemId);
            if (part == null)
            {
                session.Send(new PK_HC_BUYITEM { IsBuy = 0 });
                return;
            }

            if (_inventoryRepo.GetItemCount(data.PlayerGUID, msg.ItemType, msg.ItemId) > 0)
            {
                session.Send(new PK_HC_BUYITEM { IsBuy = 1 });
                SendInventoryUpdate(session, data.PlayerGUID, msg.ItemType, msg.ItemId);
                return;
            }

            if (data.Silver < part.CostSilver || data.Gold < part.CostGold)
            {
                session.Send(new PK_HC_BUYITEM { IsBuy = 0 });
                return;
            }

            _inventoryRepo.AddItem(data.PlayerGUID, msg.ItemType, msg.ItemId, 1, msg.ItemFlag);
            _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver - part.CostSilver, gold: data.Gold - part.CostGold);
            session.Send(new PK_HC_BUYITEM { IsBuy = 1 });
            SendInventoryUpdate(session, data.PlayerGUID, msg.ItemType, msg.ItemId);
            SendMoneyExpByGuid(session, data.PlayerGUID);
            return;
        }

        if (msg.ItemType == ItemTypeShell)
        {
            var shell = _gameTables.GetShell(msg.ItemId);
            if (shell == null)
            {
                session.Send(new PK_HC_BUYITEM { IsBuy = 0 });
                return;
            }

            int costSilver = shell.CostSilver * itemNum;
            int costGold = shell.CostGold * itemNum;
            if (data.Silver < costSilver || data.Gold < costGold)
            {
                session.Send(new PK_HC_BUYITEM { IsBuy = 0 });
                return;
            }

            _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver - costSilver, gold: data.Gold - costGold);
            _inventoryRepo.AddItem(data.PlayerGUID, msg.ItemType, msg.ItemId, itemNum, msg.ItemFlag);
            session.Send(new PK_HC_BUYITEM { IsBuy = 1 });
            SendInventoryUpdate(session, data.PlayerGUID, msg.ItemType, msg.ItemId);
            SendMoneyExpByGuid(session, data.PlayerGUID);
            return;
        }

        if (msg.ItemType == ItemTypeEquipment)
        {
            var eq = _gameTables.GetEquipment(msg.ItemId);
            if (eq == null || eq.Buy == 0)
            {
                session.Send(new PK_HC_BUYITEM { IsBuy = 0 });
                return;
            }
            long costSilver = eq.PriceCurrency != 2 ? (long)eq.Price * itemNum : 0;
            long costGold   = eq.PriceCurrency == 2 ? (long)eq.Price * itemNum : 0;
            if (data.Silver < costSilver || data.Gold < costGold)
            {
                session.Send(new PK_HC_BUYITEM { IsBuy = 0 });
                return;
            }
            _accountRepo.UpdateResources(data.PlayerGUID,
                silver: data.Silver - (int)costSilver,
                gold: data.Gold - (int)costGold);
            _inventoryRepo.AddItem(data.PlayerGUID, msg.ItemType, msg.ItemId, itemNum, msg.ItemFlag);
            Console.WriteLine($"[HS] BuyEquipment: player={data.PlayerGUID} id={msg.ItemId} x{itemNum} silver={costSilver} gold={costGold}");
            session.Send(new PK_HC_BUYITEM { IsBuy = 1 });
            SendInventoryUpdate(session, data.PlayerGUID, msg.ItemType, msg.ItemId);
            SendMoneyExpByGuid(session, data.PlayerGUID);
            return;
        }

        if (msg.ItemType == ItemTypeProvision)
        {
            var con = _gameTables.GetConsumable(msg.ItemId);
            if (con == null || con.Buy == 0)
            {
                session.Send(new PK_HC_BUYITEM { IsBuy = 0 });
                return;
            }
            long costSilver = 0;
            long costGold   = 0;
            if (msg.ItemFlag == 1 && con.SilverToGold > 0)
                costSilver = (long)con.SilverToGold * itemNum;
            else if (con.Currency == 2)
                costGold = (long)con.Price * itemNum;
            else
                costSilver = (long)con.Price * itemNum;

            if (data.Silver < costSilver || data.Gold < costGold)
            {
                session.Send(new PK_HC_BUYITEM { IsBuy = 0 });
                return;
            }
            _accountRepo.UpdateResources(data.PlayerGUID,
                silver: data.Silver - (int)costSilver,
                gold: data.Gold - (int)costGold);
            _inventoryRepo.AddItem(data.PlayerGUID, msg.ItemType, msg.ItemId, itemNum, msg.ItemFlag);
            Console.WriteLine($"[HS] BuyConsumable: player={data.PlayerGUID} id={msg.ItemId} x{itemNum} silver={costSilver} gold={costGold}");
            session.Send(new PK_HC_BUYITEM { IsBuy = 1 });
            SendInventoryUpdate(session, data.PlayerGUID, msg.ItemType, msg.ItemId);
            SendMoneyExpByGuid(session, data.PlayerGUID);
            return;
        }

        var item = _gameTables.GetItem(msg.ItemId);
        if (item != null)
        {
            int costSilver = item.CostSilver * itemNum;
            int costGold = item.CostGold * itemNum;

            if (data.Silver < costSilver || data.Gold < costGold)
            {
                session.Send(new PK_HC_BUYITEM { IsBuy = 0 });
                return;
            }

            _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver - costSilver, gold: data.Gold - costGold);
            _inventoryRepo.AddItem(data.PlayerGUID, msg.ItemType, msg.ItemId, itemNum, msg.ItemFlag);
            session.Send(new PK_HC_BUYITEM { IsBuy = 1 });
            SendInventoryUpdate(session, data.PlayerGUID, msg.ItemType, msg.ItemId);
            SendMoneyExpByGuid(session, data.PlayerGUID);
            return;
        }

        if (msg.ItemType > ItemTypeProvision && msg.ItemId > 0)
        {
            _inventoryRepo.AddItem(data.PlayerGUID, msg.ItemType, msg.ItemId, itemNum, msg.ItemFlag);
            session.Send(new PK_HC_BUYITEM { IsBuy = 1 });
            SendInventoryUpdate(session, data.PlayerGUID, msg.ItemType, msg.ItemId);
            SendMoneyExpByGuid(session, data.PlayerGUID);
            return;
        }

        session.Send(new PK_HC_BUYITEM { IsBuy = 0 });
    }

    private void HandleSellItem(GameSession session, PK_CH_SELLITEM msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        // SellItemType=100 (NodePart.Vehicle из клиента) → продажа ТАНКА.
        // v71: раньше здесь стояла проверка == 0, но клиент реально шлёт
        // NodePart.Vehicle = 100 (см. decfilesgame enum NodePart), поэтому продажа
        // танка никогда не срабатывала и уходила в общую ветку ниже (которая ищет
        // танк как обычный предмет в инвентаре, не находит и отвечает отказом).
        if (msg.SellItemType == 100 || msg.SellItemType == 0)
        {
            HandleSellTank(session, data, msg.SellItemId, msg.SellItemFlag);
            return;
        }

        int currentStock = _inventoryRepo.GetItemCount(data.PlayerGUID, msg.SellItemType, msg.SellItemId);
        if (currentStock < msg.SellItemNum) { session.Send(new PK_HC_SELLITEM { IsSell = 0 }); return; }

        int sellSilver = 0;
        int sellGold = 0;

        if (msg.SellItemType >= 1 && msg.SellItemType <= 10)
        {
            var part = _gameTables.GetPart(msg.SellItemType, msg.SellItemId);
            if (part != null)
            {
                sellSilver = (int)(part.CostSilver * 0.5f) * msg.SellItemNum;
                sellGold = (int)(part.CostGold * 0.5f) * msg.SellItemNum;
            }
        }
        else if (msg.SellItemType == ItemTypeShell)
        {
            var shell = _gameTables.GetShell(msg.SellItemId);
            if (shell != null)
            {
                sellSilver = (int)(shell.CostSilver * 0.5f) * msg.SellItemNum;
                sellGold = (int)(shell.CostGold * 0.5f) * msg.SellItemNum;
            }
        }
        else if (msg.SellItemType == ItemTypeEquipment)
        {
            var eq = _gameTables.GetEquipment(msg.SellItemId);
            if (eq != null)
            {
                int silverEquiv = eq.PriceCurrency == 2 ? eq.Price * 500 : eq.Price;
                sellSilver = (int)(silverEquiv * 0.5f) * msg.SellItemNum;
            }
        }
        else if (msg.SellItemType == ItemTypeProvision)
        {
            var con = _gameTables.GetConsumable(msg.SellItemId);
            if (con != null)
            {
                int silverEquiv = con.Currency == 2
                    ? (con.SilverToGold > 0 ? con.SilverToGold : con.Price * 500)
                    : con.Price;
                sellSilver = (int)(silverEquiv * 0.5f) * msg.SellItemNum;
            }
        }
        else
        {
            var item = _gameTables.GetItem(msg.SellItemId);
            if (item != null)
            {
                sellSilver = (int)(item.CostSilver * 0.5f) * msg.SellItemNum;
                sellGold = (int)(item.CostGold * 0.5f) * msg.SellItemNum;
            }
        }

        _inventoryRepo.RemoveItem(data.PlayerGUID, msg.SellItemType, msg.SellItemId, msg.SellItemNum);
        _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver + sellSilver, gold: data.Gold + sellGold);
        session.Send(new PK_HC_SELLITEM { IsSell = 1 });
        SendInventoryUpdate(session, data.PlayerGUID, msg.SellItemType, msg.SellItemId);
        SendMoneyExpByGuid(session, data.PlayerGUID);
    }

    // ── Sell vehicle ─────────────────────────────────────────────────────────
    // SellItemType=0 means "sell tank". SellItemId = vehicleId.
    // SellItemFlag bits: 0x01=disassemble equipment back, 0x02=fire crew.
    private void HandleSellTank(GameSession session, HallPlayerData data, int vehicleId, byte flags)
    {
        var playerTanks = _tankRepo.GetPlayerTanks(data.PlayerGUID);
        // Must keep at least one silver-currency tank after selling
        var silvTanks = playerTanks.Where(t =>
        {
            var te = _gameTables.GetTank(t.VehicleId);
            return te == null || te.CostGold <= 0; // silver tank = not a gold tank
        }).ToList();

        var tank = playerTanks.FirstOrDefault(t => t.VehicleId == vehicleId);
        if (tank == null) { session.Send(new PK_HC_SELLITEM { IsSell = 0 }); return; }

        if (silvTanks.Count <= 1 && silvTanks.Any(t => t.VehicleId == vehicleId))
        {
            // Can't sell last silver tank
            session.Send(new PK_HC_SELLITEM { IsSell = 0 });
            return;
        }

        var tankEntry = _gameTables.GetTank(vehicleId);
        int sellSilver = tankEntry != null ? (int)(tankEntry.CostSilver * 0.25f) : 10_000;
        sellSilver = Math.Max(sellSilver, 10_000);

        // Remove crew assigned to this tank
        if ((flags & 0x02) != 0)
        {
            var crew = _crewRepo.GetPlayerCrew(data.PlayerGUID)
                                .Where(c => c.TankGuid == tank.TankGUID).ToList();
            foreach (var c in crew) _crewRepo.UpdateCrewTank(c.CrewGuid, 0);
        }
        _tankRepo.DeleteTank(tank.TankGUID);
        _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver + sellSilver);

        // v65 (п.2): убрать проданный танк из ангара клиента (OnHCSellItem у клиента пуст).
        session.Send(new PK_HC_DeleteTank { TankId = vehicleId, TankGUID = tank.TankGUID });
        session.Send(new PK_HC_SELLITEM { IsSell = 1 });
        SendMoneyExpByGuid(session, data.PlayerGUID);
        SendGarageRefresh(session, data.PlayerGUID);
        Console.WriteLine($"[HS] Sold tank VehicleId={vehicleId} for {sellSilver} silver ({data.PlayerName})");
    }

    private void HandleBuyShell(GameSession session, PK_CH_BuyShell msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        if (msg.Shells.Length == 0)
        {
            session.Send(new PK_HC_BuyShell { Success = false });
            return;
        }

        int totalSilver = 0;
        int totalGold = 0;
        foreach (var shellRequest in msg.Shells)
        {
            if (shellRequest.ShellID <= 0 || shellRequest.Count <= 0)
            {
                continue;
            }

            var shell = _gameTables.GetShell(shellRequest.ShellID);
            if (shell == null)
            {
                session.Send(new PK_HC_BuyShell { Success = false });
                return;
            }

            totalSilver += shell.CostSilver * shellRequest.Count;
            totalGold += shell.CostGold * shellRequest.Count;
        }

        if (data.Silver < totalSilver || data.Gold < totalGold)
        {
            session.Send(new PK_HC_BuyShell { Success = false });
            return;
        }

        _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver - totalSilver, gold: data.Gold - totalGold);
        foreach (var shellRequest in msg.Shells)
        {
            if (shellRequest.ShellID <= 0 || shellRequest.Count <= 0)
            {
                continue;
            }

            _inventoryRepo.AddItem(data.PlayerGUID, ItemTypeShell, shellRequest.ShellID, shellRequest.Count);
            SendInventoryUpdate(session, data.PlayerGUID, ItemTypeShell, shellRequest.ShellID);
        }

        session.Send(new PK_HC_BuyShell { Success = true });
        SendMoneyExpByGuid(session, data.PlayerGUID);
    }

    private void HandleSetShell(GameSession session, PK_CH_SetShell msg)
    {
        var data = GetPlayerData(session);
        if (data == null)
        {
            session.Send(new PK_HC_SetShell { nSuccessFlag = false });
            return;
        }

        var tank = _tankRepo.GetTankByGUID(msg.TankGuid);
        if (tank == null || tank.PlayerGUID != data.PlayerGUID)
        {
            session.Send(new PK_HC_SetShell { nSuccessFlag = false });
            return;
        }

        var shellSlots = ParseShellSlots(tank.ShellSlots);
        foreach (var shell in msg.SetShells)
        {
            if (shell.ShellSlotId >= shellSlots.Length)
            {
                continue;
            }

            if (shell.ShellID > 0 && !_gameTables.IsShellCompatibleWithGun(tank.MainGunID, shell.ShellID))
            {
                continue;
            }

            shellSlots[shell.ShellSlotId] = new ShellSlot
            {
                ID = Math.Max(0, shell.ShellID),
                Count = Math.Max(0, shell.Count)
            };
        }

        string serializedSlots = SerializeShellSlots(shellSlots);
        int curShellId = GetFirstConfiguredShellId(shellSlots);
        _tankRepo.UpdateTankEquipment(
            tank.TankGUID,
            tank.HullID,
            tank.UnderpanID,
            tank.TurretID,
            tank.MainGunID,
            tank.EngineID,
            tank.FueltankID,
            tank.CartridgeID,
            tank.RadioStationID,
            tank.GaugeID,
            tank.TankHp,
            tank.TankCurHp,
            tank.TankCurrencyType,
            serializedSlots,
            serializedSlots,
            curShellId);

        session.Send(new PK_HC_SetShell { nSuccessFlag = true });
        SendShellInfo(session, _tankRepo.GetTankByGUID(msg.TankGuid));
    }

    private void HandleBuyCrew(GameSession session, PK_CH_BuyCrew msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        var tank = _tankRepo.GetTankByGUID(msg.TankGuid);
        long targetTankGuid = tank?.PlayerGUID == data.PlayerGUID ? msg.TankGuid : 0;

        // The client sends the raw crew-table row ID from Table_Tank_crew (per nation),
        // NOT a level. The starting level + price live in that table and are NOT derivable
        // from the id by any formula, so we look the row up by id. When the row isn't in
        // the data yet (Table_Tank_crew not exported to client_game_tables.json), fall back
        // to a free level-0 recruit and log the id so it can be added.
        var hire = _gameTables.GetCrew(msg.CrewId);
        int initLv, costSilver, costGold; string tierName;
        if (hire != null)
        {
            initLv = hire.InitLv; costSilver = hire.CostSilver; costGold = hire.CostGold; tierName = hire.Name;
        }
        else if (msg.CrewId >= 29)
        {
            // Обычные экипажи (type 1–3) имеют НИЗКИЕ id (все ≤ 28 — подтверждено таблицей).
            // Асы (type 4) — высокие id; помимо диапазона 60–99 у ПРЕМ-танков асы попадают в
            // «дыру» 29–59 (наблюдалось: 32/35/38 для Объект 701 / Lowe / T34). Раньше порог был
            // ≥50 и эти асы падали в level 0 («экипаж асов слетел»). Порог опущен до 29: всё, что
            // выше нормальных 28 и не в таблице — считаем асом (level 12).
            initLv = 12; costSilver = 0; costGold = 2200; tierName = "Ace(byId)";
            Console.WriteLine($"[HS] BuyCrew: crew id {msg.CrewId} not in table but id>=29 → treated as Ace (level 12).");
        }
        else
        {
            initLv = 0; costSilver = 0; costGold = 0; tierName = "UNKNOWN";
            Console.WriteLine($"[HS] BuyCrew: crew id {msg.CrewId} is not in Table_Tank_crew data — granting level 0 (low id = normal tier; add its type if it's a Specialist/Elite).");
        }
        // (Client already validated affordability client-side; we just bill it.)
        if (data.Silver < costSilver || data.Gold < costGold)
        {
            session.Send(new PK_HC_AddCrew { Id = 0 });
            return;
        }

        using var tx = _db.Connection.BeginTransaction();
        try
        {
            if (costSilver > 0 || costGold > 0)
            {
                _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver - costSilver, gold: data.Gold - costGold, tx: tx);
            }

            if (targetTankGuid != 0)
            {
                _crewRepo.ClearTankAssignment(data.PlayerGUID, targetTankGuid, 0, tx);
            }

            var crew = _crewRepo.CreateCrew(data.PlayerGUID, msg.CrewId, targetTankGuid, msg.FamilyNameId, msg.NameId, msg.BigIconId, msg.SmallIconId, msg.ResumeId, initLv, tx);
            Console.WriteLine($"[HS] BuyCrew: clientCrewId={msg.CrewId} -> tier={tierName} level={initLv} cost={costSilver}s/{costGold}g");

            tx.Commit();

            session.Send(new PK_HC_AddCrew
            {
                Id = crew.CrewId, CurExp = crew.CurExp, CurLv = crew.CurLv,
                Guid = crew.CrewGuid, TankGuid = crew.TankGuid,
                FamilyNameId = crew.FamilyNameId, NameId = crew.NameId,
                BigIconId = crew.BigIconId, SmallIconId = crew.SmallIconId,
                ResumeId = crew.ResumeId, Lastlearn = crew.Lastlearn, LastSkillExp = crew.LastSkillExp
            });
            SendPlayerAllCrew(session, data.PlayerGUID);
            SendMoneyExpByGuid(session, data.PlayerGUID);
        }
        catch (Exception ex)
        {
            try { tx.Rollback(); } catch { }
            Console.WriteLine($"[HS] BuyCrew transaction failed: {ex}");
            session.Send(new PK_HC_AddCrew { Id = 0 });
        }
    }

    private void HandleOperCrew(GameSession session, PK_CH_OperCrew msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        var crew = _crewRepo.GetCrewByGuid(msg.CrewGuid);
        if (crew == null || crew.PlayerGUID != data.PlayerGUID)
        {
            return;
        }

        // OperType: 1 = Equip (assign idle crew), 2 = Change (replace crew on tank),
        // 3 = Unload (remove crew from tank). The client ALWAYS sends the battle tank's GUID,
        // even for Unload, so we must look at OperType to know whether to assign or detach -
        // otherwise an Unload re-assigns the crew and the slot never frees.
        const byte operUnload = 3;
        long targetTankGuid = 0;
        if (msg.OperType != operUnload && msg.TankGuid != 0)
        {
            var tank = _tankRepo.GetTankByGUID(msg.TankGuid);
            if (tank == null || tank.PlayerGUID != data.PlayerGUID)
            {
                return;
            }

            targetTankGuid = msg.TankGuid;
        }

        Console.WriteLine($"[HS] OperCrew: Op={msg.OperType}, CrewGuid={msg.CrewGuid}, TankGuid={targetTankGuid}");
        if (targetTankGuid != 0)
        {
            _crewRepo.ClearTankAssignment(data.PlayerGUID, targetTankGuid, msg.CrewGuid);
        }
        _crewRepo.UpdateCrewTank(msg.CrewGuid, targetTankGuid);
        var updatedCrew = _crewRepo.GetCrewByGuid(msg.CrewGuid);
        session.Send(new PK_HC_UpdateCrew
        {
            CurExp = updatedCrew?.CurExp ?? 0,
            CurLv = updatedCrew?.CurLv ?? 1,
            CrewGuid = msg.CrewGuid,
            TankGuid = targetTankGuid
        });
        SendPlayerAllCrew(session, data.PlayerGUID);
    }

    private void HandleLearnCrewSkill(GameSession session, PK_CH_LearnCrewSkill msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        var skill = _gameTables.GetSkill(msg.SkillId);
        if (skill == null) { session.Send(new PK_HC_LearnCrewSkill { Flag = 0, SkillId = msg.SkillId, CrewGuid = msg.CrewGuid }); return; }

        var crew = _crewRepo.GetCrewByGuid(msg.CrewGuid);
        if (crew == null) { session.Send(new PK_HC_LearnCrewSkill { Flag = 0, SkillId = msg.SkillId, CrewGuid = msg.CrewGuid }); return; }

        int currentLevel = GetCrewSkillLevel(crew.SkillIds, msg.SkillId);

        if (currentLevel >= skill.MaxLevel)
        {
            session.Send(new PK_HC_LearnCrewSkill { Flag = 0, SkillId = msg.SkillId, CrewGuid = msg.CrewGuid });
            return;
        }

        int newLevel = currentLevel + 1;
        int skillCost = 1000 * newLevel * crew.CurLv;
        if (data.Silver < skillCost)
        {
            session.Send(new PK_HC_LearnCrewSkill { Flag = 0, SkillId = msg.SkillId, CrewGuid = msg.CrewGuid });
            return;
        }

        string newSkillIds = UpsertCrewSkillLevel(crew.SkillIds, msg.SkillId, newLevel);
        _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver - skillCost);
        _crewRepo.UpdateCrewSkills(msg.CrewGuid, newSkillIds, msg.SkillId, 0);
        session.Send(new PK_HC_LearnCrewSkill { Flag = 1, SkillId = msg.SkillId, CrewGuid = msg.CrewGuid });
        session.Send(new PK_HC_Update_CrewSkill
        {
            CrewGuid = msg.CrewGuid,
            LastSkillId = msg.SkillId,
            LastSkillExp = 0,
            Ids = BuildCrewSkillIdArray(newSkillIds)
        });
        SendMoneyExpByGuid(session, data.PlayerGUID);
    }

    private void HandleQuickLearnCrewSkill(GameSession session, PK_CH_QuickLearnCrewSkill msg)
    {
        var data = GetPlayerData(session);
        if (data == null) { session.Send(new PK_HC_QuickLearnCrewSkill { Result = 0, CrewGuid = msg.CrewGuid }); return; }

        var crew = _crewRepo.GetCrewByGuid(msg.CrewGuid);
        if (crew == null) { session.Send(new PK_HC_QuickLearnCrewSkill { Result = 0, CrewGuid = msg.CrewGuid }); return; }

        var skill = _gameTables.GetSkill(crew.Lastlearn);
        if (skill == null) { session.Send(new PK_HC_QuickLearnCrewSkill { Result = 0, CrewGuid = msg.CrewGuid }); return; }

        int currentLevel = GetCrewSkillLevel(crew.SkillIds, crew.Lastlearn);
        int targetLevel = skill.MaxLevel;
        int totalCost = 0;
        for (int lvl = currentLevel + 1; lvl <= targetLevel; lvl++)
        {
            totalCost += 1000 * lvl * crew.CurLv;
        }

        if (data.Gold < totalCost)
        {
            session.Send(new PK_HC_QuickLearnCrewSkill { Result = 0, CrewGuid = msg.CrewGuid });
            return;
        }

        string skillIds = UpsertCrewSkillLevel(crew.SkillIds, crew.Lastlearn, targetLevel);
        _accountRepo.UpdateResources(data.PlayerGUID, gold: data.Gold - totalCost);
        _crewRepo.UpdateCrewSkills(msg.CrewGuid, skillIds, crew.Lastlearn, targetLevel);
        session.Send(new PK_HC_QuickLearnCrewSkill { Result = 1, CrewGuid = msg.CrewGuid });
        session.Send(new PK_HC_Update_CrewSkill
        {
            CrewGuid = msg.CrewGuid,
            LastSkillId = crew.Lastlearn,
            LastSkillExp = targetLevel,
            Ids = BuildCrewSkillIdArray(skillIds)
        });
        SendMoneyExpByGuid(session, data.PlayerGUID);
    }

    private void HandleResetCrewSkill(GameSession session, PK_CH_ResetCrewSkill msg)
    {
        var data = GetPlayerData(session);
        if (data == null) { session.Send(new PK_HC_ResetCrewSkill { ResetFlag = msg.ResetFlag, ResultFlag = 0, CrewGuid = msg.CrewGuid }); return; }

        var crew = _crewRepo.GetCrewByGuid(msg.CrewGuid);
        if (crew == null) { session.Send(new PK_HC_ResetCrewSkill { ResetFlag = msg.ResetFlag, ResultFlag = 0, CrewGuid = msg.CrewGuid }); return; }

        int resetCost = 0;
        if (!string.IsNullOrEmpty(crew.SkillIds))
        {
            var skillEntries = crew.SkillIds.Split(';');
            foreach (var entry in skillEntries)
            {
                var parts = entry.Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0], out int skillId) && int.TryParse(parts[1], out int level))
                {
                    resetCost += 500 * level;
                }
            }
        }

        if (msg.ResetFlag == 1 && data.Silver < resetCost)
        {
            session.Send(new PK_HC_ResetCrewSkill { ResetFlag = msg.ResetFlag, ResultFlag = 0, CrewGuid = msg.CrewGuid });
            return;
        }

        if (msg.ResetFlag == 2 && data.Gold < resetCost)
        {
            session.Send(new PK_HC_ResetCrewSkill { ResetFlag = msg.ResetFlag, ResultFlag = 0, CrewGuid = msg.CrewGuid });
            return;
        }

        if (msg.ResetFlag == 1)
            _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver - resetCost);
        else if (msg.ResetFlag == 2)
            _accountRepo.UpdateResources(data.PlayerGUID, gold: data.Gold - resetCost);

        _crewRepo.UpdateCrewSkills(msg.CrewGuid, "", 0, 0);
        session.Send(new PK_HC_ResetCrewSkill { ResetFlag = msg.ResetFlag, ResultFlag = 1, CrewGuid = msg.CrewGuid });
        SendMoneyExpByGuid(session, data.PlayerGUID);
    }

    private void HandleCrewFastTrain(GameSession session) => session.Send(new PK_HC_CrewFastTrainTankInfo { TankCount = 0 });

    private void HandleFireCrew(GameSession session, PK_CH_FireCrew msg)
    {
        _crewRepo.DeleteCrew(msg.CrewGuid);
        session.Send(new PK_HC_DelCrew { CrewGuid = msg.CrewGuid });
    }

    private void HandleBuyCasernCrew(GameSession session, PK_CH_BuyCasernCrew msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        const int casernUpgradeCostGold = 100;
        const int maxCasernCapacity = 85;   // потолок клиента (TankSelectList: CarportNum >= 85 = максимум)
        if (data.CasernCapacity >= maxCasernCapacity || data.Gold < casernUpgradeCostGold)
        {
            SendCasernUpdate(session, data.CasernCapacity);
            return;
        }

        int newCapacity = Math.Min(maxCasernCapacity, data.CasernCapacity + 1);
        _accountRepo.UpdateResources(data.PlayerGUID, gold: data.Gold - casernUpgradeCostGold);
        _accountRepo.UpdateCasernCapacity(data.PlayerGUID, newCapacity);
        SendCasernUpdate(session, newCapacity);
        SendMoneyExpByGuid(session, data.PlayerGUID);
    }

    private void HandleEquipmentOperate(GameSession session, PK_CH_EquipmentOperate msg)
    {
        var data = GetPlayerData(session);
        if (data == null)
        {
            session.Send(new PK_HC_EquipmentOperate { TankGuid = msg.TankGuid, OperateType = msg.OperateType, EquipmentID = msg.EquipmentID, IsBuy = 0 });
            return;
        }

        var tank = _tankRepo.GetTankByGUID(msg.TankGuid);
        if (tank == null || tank.PlayerGUID != data.PlayerGUID)
        {
            session.Send(new PK_HC_EquipmentOperate { TankGuid = msg.TankGuid, OperateType = msg.OperateType, EquipmentID = msg.EquipmentID, IsBuy = 0 });
            return;
        }

        Console.WriteLine($"[HS] EquipmentOperate: Tank={msg.TankGuid}, Equip={msg.EquipmentID}, Op={msg.OperateType}, IsBuy={msg.IsBuy}");
        var equipmentIds = _tankEquipmentRepo.GetTankEquipmentIds(msg.TankGuid);
        var eqEntry = _gameTables.GetEquipment(msg.EquipmentID);
        bool success = false;
        bool moneyChanged = false;

        switch (msg.OperateType)
        {
            case 1:
            {
                if (equipmentIds.Count >= 36 || equipmentIds.Contains(msg.EquipmentID))
                    break;

                // Prevent stacking conflicting equipment (Table_Equipment.anti_id, checked symmetrically)
                bool hasConflict = false;
                if (eqEntry != null && !string.IsNullOrEmpty(eqEntry.AntiId))
                {
                    hasConflict = eqEntry.AntiId
                        .Split('_', StringSplitOptions.RemoveEmptyEntries)
                        .Any(s => int.TryParse(s, out int aid) && equipmentIds.Contains(aid));
                }
                if (!hasConflict)
                {
                    string myIdStr = msg.EquipmentID.ToString();
                    foreach (int mountedId in equipmentIds)
                    {
                        var mountedEq = _gameTables.GetEquipment(mountedId);
                        if (mountedEq != null && !string.IsNullOrEmpty(mountedEq.AntiId) &&
                            mountedEq.AntiId.Split('_', StringSplitOptions.RemoveEmptyEntries).Contains(myIdStr))
                        {
                            hasConflict = true;
                            break;
                        }
                    }
                }
                if (hasConflict)
                {
                    Console.WriteLine($"[HS] EquipmentOperate: conflict AntiId for equip={msg.EquipmentID}");
                    break;
                }

                int availableCount = _inventoryRepo.GetItemCount(data.PlayerGUID, ItemTypeEquipment, msg.EquipmentID);
                if (availableCount > 0)
                {
                    if (_inventoryRepo.RemoveItem(data.PlayerGUID, ItemTypeEquipment, msg.EquipmentID, 1))
                    {
                        SendInventoryUpdate(session, data.PlayerGUID, ItemTypeEquipment, msg.EquipmentID);
                        equipmentIds.Add(msg.EquipmentID);
                        success = true;
                    }
                }
                else if (msg.IsBuy == 1)
                {
                    int costSilver = eqEntry != null && eqEntry.PriceCurrency != 2 ? eqEntry.Price : 0;
                    int costGold   = eqEntry != null && eqEntry.PriceCurrency == 2 ? eqEntry.Price : 0;
                    if (data.Silver < costSilver || data.Gold < costGold)
                        break;

                    if (costSilver > 0 || costGold > 0)
                    {
                        _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver - costSilver, gold: data.Gold - costGold);
                        moneyChanged = true;
                    }
                    equipmentIds.Add(msg.EquipmentID);
                    success = true;
                }

                break;
            }
            case 2:
            {
                if (!equipmentIds.Contains(msg.EquipmentID))
                    break;

                // Complex equipment (removable==0 && removable_price>0) costs gold/silver to demount to depot.
                int demountSilver = eqEntry != null && eqEntry.Removable == 0 && eqEntry.RemovablePrice > 0 && eqEntry.RemovablePriceCurrency != 2
                    ? eqEntry.RemovablePrice : 0;
                int demountGold = eqEntry != null && eqEntry.Removable == 0 && eqEntry.RemovablePrice > 0 && eqEntry.RemovablePriceCurrency == 2
                    ? eqEntry.RemovablePrice : 0;
                if (data.Silver < demountSilver || data.Gold < demountGold)
                    break;

                if (demountSilver > 0 || demountGold > 0)
                {
                    _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver - demountSilver, gold: data.Gold - demountGold);
                    moneyChanged = true;
                }
                equipmentIds.Remove(msg.EquipmentID);
                _inventoryRepo.AddItem(data.PlayerGUID, ItemTypeEquipment, msg.EquipmentID, 1);
                SendInventoryUpdate(session, data.PlayerGUID, ItemTypeEquipment, msg.EquipmentID);
                success = true;

                break;
            }
            case 3:
            {
                success = equipmentIds.Remove(msg.EquipmentID);
                break;
            }
        }

        if (!success)
        {
            session.Send(new PK_HC_EquipmentOperate { TankGuid = msg.TankGuid, OperateType = msg.OperateType, EquipmentID = msg.EquipmentID, IsBuy = 0 });
            return;
        }

        _tankEquipmentRepo.SaveTankEquipment(msg.TankGuid, equipmentIds);
        session.Send(new PK_HC_EquipmentOperate { TankGuid = msg.TankGuid, OperateType = msg.OperateType, EquipmentID = msg.EquipmentID, IsBuy = msg.IsBuy });
        if (moneyChanged) SendMoneyExpByGuid(session, data.PlayerGUID);
    }

    // ── Камуфляжи (Table_Cloth / TankOwnCoat) ─────────────────────────────────
    //
    // Клиент (TankCoatNodeListContinerUi + Hall.SendTankCoatOperator) шлёт:
    //   TankId        = Table_Vehicle.id — НЕ TankGUID! (CurTank.TankData.TableVehicle.id)
    //   CoatTableID   = id строки Table_Cloth
    //   OperatorType  = EnTankCoatOperatorType: 0 Buy, 1 Sell, 2 Equ(надеть), 3 Unequ
    //   CoatSlotIndex = EnTankCoatType: 0 Ice, 1 Field, 2 Desert, 3 None
    //   BuyType       = BUY_TYPE: 0 = 7 дней, 1 = 30 дней, 2 = навсегда, 100 = нет
    //
    // Покупка/продажа приходят с реальным номером слота, а «надеть» — всегда из
    // вкладки «Имеющиеся», то есть с CoatSlotIndex = 3 (None): нужный слот сервер
    // ищет сам по CoatTableID.
    private void HandleTankCoatOperator(GameSession session, PK_CH_TankCoatOperator msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        // TankId — это VehicleId. Ищем экземпляр танка игрока.
        var tank = _tankRepo.GetPlayerTanks(data.PlayerGUID)
                            .FirstOrDefault(t => t.VehicleId == msg.TankId);
        if (tank == null)
        {
            Console.WriteLine($"[Coat] Танк VehicleId={msg.TankId} не найден в гараже {data.PlayerGUID}");
            SendTankCoatInfo(session, data.PlayerGUID, msg.TankId, changed: false);
            return;
        }

        int tankLevel = _gameTables.GetTank(tank.VehicleId)?.Tier ?? 1;
        bool ok = false;

        switch (msg.OperatorType)
        {
            case 0: ok = CoatBuy(session, data, tank, msg, tankLevel); break;
            case 1: ok = CoatSell(tank, msg); break;
            case 2: ok = CoatEquip(tank, msg); break;
            case 3: ok = CoatUnequip(tank); break;
            default:
                Console.WriteLine($"[Coat] Неизвестный OperatorType={msg.OperatorType}");
                break;
        }

        // OperateType = 1 заставляет Player.OnSynTankCoatInfo вызвать OnTankCoatChange
        // и перекрасить модель в ангаре немедленно. При отказе шлём 0 — просто откат
        // состояния на клиенте, без анимации.
        SendTankCoatInfo(session, data.PlayerGUID, tank.VehicleId, changed: ok);
    }

    private bool CoatBuy(GameSession session, HallPlayerData data, Database.Models.Tank tank,
                         PK_CH_TankCoatOperator msg, int tankLevel)
    {
        int slot = msg.CoatSlotIndex;
        if (slot < 0 || slot >= TankCoatRepository.Slots)
        {
            Console.WriteLine($"[Coat] Покупка в некорректный слот {slot}");
            return false;
        }
        if (msg.CoatTableID <= 0) return false;

        var ext = _gameTables.GetExterior(msg.CoatTableID);
        if (ext.Buy == 0)
        {
            Console.WriteLine($"[Coat] Камуфляж {msg.CoatTableID} не продаётся (buy=0)");
            return false;
        }

        int buyType = msg.BuyType;
        if (buyType > 2)
        {
            Console.WriteLine($"[Coat] Покупка без срока (BuyType={buyType}) — отклонено");
            return false;
        }

        var (cost, currency) = ext.GetPrice(buyType, tankLevel);
        if (cost < 0) return false;

        // currency: 1 = серебро, 2 = золото (Table_Cloth.priceN_type)
        if (currency == 2)
        {
            if (data.Gold < cost)
            {
                Console.WriteLine($"[Coat] Не хватает золота: нужно {cost}, есть {data.Gold}");
                return false;
            }
            _accountRepo.UpdateResources(data.PlayerGUID, gold: data.Gold - cost);
        }
        else
        {
            if (data.Silver < cost)
            {
                Console.WriteLine($"[Coat] Не хватает серебра: нужно {cost}, есть {data.Silver}");
                return false;
            }
            _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver - cost);
        }

        int duration = ExteriorEntry.DurationSeconds(buyType);
        int overdue  = duration == 0 ? 0
                     : (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + duration);

        // Покупка ЗАМЕЩАЕТ содержимое слота — так же ведёт себя оригинал
        // (клиент перед отправкой сам показывает предупреждение exterior_buy_tip).
        _tankCoatRepo.SetSlot(tank.TankGUID, slot, msg.CoatTableID, overdue);
        // И сразу надеваем: игрок ждёт увидеть камуфляж на танке после покупки.
        _tankCoatRepo.SetCurSlot(tank.TankGUID, slot, msg.CoatTableID);

        SendMoneyExpByGuid(session, data.PlayerGUID);
        Console.WriteLine($"[Coat] Куплен камуфляж {msg.CoatTableID} ({ext.Name}) в слот {slot} " +
                          $"танка {tank.VehicleId}, цена {cost} " +
                          $"{(currency == 2 ? "золота" : "серебра")}, срок " +
                          $"{(overdue == 0 ? "навсегда" : duration / 86400 + " дн.")}");
        return true;
    }

    private bool CoatSell(Database.Models.Tank tank, PK_CH_TankCoatOperator msg)
    {
        // Клиент называет это Sell, в UI — «снять» (exterior_toolbt_unload):
        // камуфляж удаляется из слота без возврата денег.
        int slot = msg.CoatSlotIndex;
        if (slot < 0 || slot >= TankCoatRepository.Slots)
            slot = _tankCoatRepo.FindSlotOf(tank.TankGUID, msg.CoatTableID);
        if (slot < 0) return false;

        _tankCoatRepo.ClearSlot(tank.TankGUID, slot);
        if (_tankCoatRepo.GetCurSlot(tank.TankGUID) == slot)
            _tankCoatRepo.SetCurSlot(tank.TankGUID, TankCoatRepository.SlotNone, 0);

        Console.WriteLine($"[Coat] Снят камуфляж из слота {slot} танка {tank.VehicleId}");
        return true;
    }

    private bool CoatEquip(Database.Models.Tank tank, PK_CH_TankCoatOperator msg)
    {
        // Приходит с CoatSlotIndex = 3 (None) — слот ищем по id камуфляжа.
        int slot = _tankCoatRepo.FindSlotOf(tank.TankGUID, msg.CoatTableID);
        if (slot < 0)
        {
            Console.WriteLine($"[Coat] Камуфляж {msg.CoatTableID} не куплен для танка {tank.VehicleId}");
            return false;
        }

        _tankCoatRepo.SetCurSlot(tank.TankGUID, slot, msg.CoatTableID);
        Console.WriteLine($"[Coat] Надет камуфляж {msg.CoatTableID} (слот {slot}) на танк {tank.VehicleId}");
        return true;
    }

    private bool CoatUnequip(Database.Models.Tank tank)
    {
        _tankCoatRepo.SetCurSlot(tank.TankGUID, TankCoatRepository.SlotNone, 0);
        Console.WriteLine($"[Coat] Камуфляж снят с танка {tank.VehicleId}");
        return true;
    }

    /// <summary>Собирает PK_TankCoatInfo по одному танку игрока.</summary>
    private PK_TankCoatInfo BuildCoatInfo(Database.Models.Tank tank)
    {
        var slots = _tankCoatRepo.GetCoats(tank.TankGUID);
        var info  = new PK_TankCoatInfo
        {
            TankID         = tank.VehicleId,   // клиент сверяет с TankData.VehicleId
            TankCoatArray  = PK_TankCoatInfo.CreateSlots(),
            CurSelectType  = (byte)_tankCoatRepo.GetCurSlot(tank.TankGUID),
        };
        for (int i = 0; i < PK_TankCoatInfo.CoatSlots; i++)
        {
            info.TankCoatArray[i].TableID     = (ushort)slots[i].CoatTableId;
            info.TankCoatArray[i].OverdueTime = slots[i].OverdueUnix;
        }
        // Надетый слот пуст (камуфляж истёк или снят) — принудительно None,
        // иначе клиентский GetCurTankCoatId вернёт 0 при CurSelectType != None.
        if (info.CurSelectType < PK_TankCoatInfo.CoatSlots &&
            info.TankCoatArray[info.CurSelectType].TableID == 0)
        {
            info.CurSelectType = TankCoatRepository.SlotNone;
            _tankCoatRepo.SetCurSlot(tank.TankGUID, TankCoatRepository.SlotNone, 0);
        }
        return info;
    }

    /// <summary>Ответ на операцию: состояние одного танка.</summary>
    private void SendTankCoatInfo(GameSession session, long playerGuid, int vehicleId, bool changed)
    {
        var tank = _tankRepo.GetPlayerTanks(playerGuid).FirstOrDefault(t => t.VehicleId == vehicleId);
        if (tank == null) return;
        session.Send(new PK_HC_TankCoatInfo
        {
            OperateType         = (byte)(changed ? 1 : 0),
            TankCoatsInfoArray  = new[] { BuildCoatInfo(tank) },
        });
    }

    /// <summary>
    /// Полная синхронизация камуфляжей всего гаража при входе в ангар.
    /// OperateType = 0 — клиент молча запоминает, без перекраски моделей.
    /// Без этого пакета вкладка «Имеющиеся» всегда пустая, а надетый камуфляж
    /// не переживает перезаход в игру.
    /// </summary>
    private void SendAllTankCoats(GameSession session, long playerGuid)
    {
        try
        {
            var tanks = _tankRepo.GetPlayerTanks(playerGuid);
            if (tanks.Count == 0) return;

            // По одному VehicleId: клиент адресует танки именно так, дубли ему не нужны.
            var infos = tanks.GroupBy(t => t.VehicleId)
                             .Select(g => BuildCoatInfo(g.First()))
                             .ToArray();

            // Count пишется байтом — режем пачками по 200 с запасом.
            const int chunk = 200;
            for (int i = 0; i < infos.Length; i += chunk)
            {
                session.Send(new PK_HC_TankCoatInfo
                {
                    OperateType        = 0,
                    TankCoatsInfoArray = infos.Skip(i).Take(chunk).ToArray(),
                });
            }
            Console.WriteLine($"[Coat] Синхронизировано камуфляжей: {infos.Length} танк(ов)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Coat] Ошибка синхронизации: {ex.Message}");
        }
    }

    // Условная цена (Table_Cloth не выгружена — см. предупреждение GameTables при
    // старте — используем фолбэк, как и для камуфляжей).
    private const int SignaturePriceSilver = 500;
    private const int EmblemPriceSilver = 500;
    private static long FarFutureUnixTime() => DateTimeOffset.UtcNow.AddYears(10).ToUnixTimeSeconds();

    private void HandleBuyTankSignature(GameSession session, PK_CH_BuyTankSignature msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        var tank = _tankRepo.GetPlayerTanks(data.PlayerGUID).FirstOrDefault(t => t.VehicleId == msg.TankID);
        if (tank == null || msg.SignaturePos < 0 || msg.SignaturePos >= 2)
        {
            session.Send(new PK_HC_BuyTankSignature { SucessFlag = 0 });
            return;
        }

        if (data.Silver < SignaturePriceSilver)
        {
            SendMoneyExpByGuid(session, data.PlayerGUID);
            session.Send(new PK_HC_BuyTankSignature { SucessFlag = 0 });
            return;
        }

        _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver - SignaturePriceSilver);
        long expireTime = FarFutureUnixTime();
        _tankRepo.UpdateTankSignature(tank.TankGUID, tank, msg.SignaturePos, msg.Signature, expireTime);
        SendMoneyExpByGuid(session, data.PlayerGUID);

        // ВАЖНО: клиент (Player.OnSynTankDecalInfo) применяет надпись к танку
        // именно из полей этого ответа — без него UI ничего не покажет.
        session.Send(new PK_HC_BuyTankSignature
        {
            SucessFlag = 1,
            PriceTimeType = msg.PriceTime,
            SignaturePos = msg.SignaturePos,
            TankID = msg.TankID,
            Signature = msg.Signature,
            SignatureTime = expireTime
        });
        Console.WriteLine($"[HS] BuyTankSignature: {data.PlayerName} tank={msg.TankID} slot={msg.SignaturePos} signature={msg.Signature}");
    }

    private void HandleGetTankHonor(GameSession session) => session.Send(new PK_HC_TankHonorList());

    private void HandleTankRepair(GameSession session, PK_CH_TANKREPAIR msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        var tank = _tankRepo.GetTankByGUID(msg.TankGuid);
        if (tank == null || tank.PlayerGUID != data.PlayerGUID)
        {
            session.Send(new PK_HC_TANKREPAIR { TankGuid = msg.TankGuid, RepairResult = 0 });
            return;
        }

        // ── 1. Repair the hull to full if it's damaged ────────────────────────────
        var tankEntry = _gameTables.GetTank(tank.VehicleId);
        int maxHp     = tank.TankHp > 0 ? tank.TankHp : (tankEntry?.MaxHp ?? tank.TankCurHp);
        int hpDeficit = Math.Max(0, maxHp - tank.TankCurHp);
        int tier      = tankEntry?.Tier ?? 1;
        int repairCost = (int)(hpDeficit * (5 + tier * 2));
        if (data.Silver < repairCost) { session.Send(new PK_HC_TANKREPAIR { TankGuid = msg.TankGuid, RepairResult = 0 }); return; }

        int newCurHp = hpDeficit > 0 ? maxHp : tank.TankCurHp;

        // ── 2. Apply the loadout the service screen asked for ─────────────────────
        var newSlots = new ShellSlot[3];
        for (int i = 0; i < 3; i++)
        {
            newSlots[i].ID    = msg.Shell[i].ShellID;
            newSlots[i].Count = Math.Max(0, msg.Shell[i].Count);
        }
        string shellSlotsStr = SerializeShellSlots(newSlots);
        int curShellId = GetFirstConfiguredShellId(newSlots);
        if (curShellId == 0) curShellId = tank.CurShellId;

        // Parse currently mounted consumables vs requested ones so we only bill/consume
        // deltas (keeping or swapping already-equipped consumables costs 0).
        int[] oldProvisions = new int[3];
        if (!string.IsNullOrWhiteSpace(tank.Provisions))
        {
            var oldParts = tank.Provisions.Split(';', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < Math.Min(3, oldParts.Length); i++)
                int.TryParse(oldParts[i], out oldProvisions[i]);
        }

        int[] reqProvisions = new int[3];
        for (int i = 0; i < 3; i++) reqProvisions[i] = Math.Max(0, msg.Provision[i]);

        // Multiset accounting: keep already-mounted instances intact; for net newly-added
        // instances, take from depot first and buy the rest according to ConsumableMoneyType.
        var keptCounts = new Dictionary<int, int>();
        foreach (int id in oldProvisions)
            if (id > 0) keptCounts[id] = keptCounts.GetValueOrDefault(id) + 1;

        var reqCounts = new Dictionary<int, int>();
        foreach (int id in reqProvisions)
            if (id > 0) reqCounts[id] = reqCounts.GetValueOrDefault(id) + 1;

        var returnedIds = new Dictionary<int, int>();
        foreach (var (id, oldCnt) in keptCounts)
        {
            int wantCnt = reqCounts.GetValueOrDefault(id);
            if (oldCnt > wantCnt) returnedIds[id] = oldCnt - wantCnt;
        }

        var fromDepot = new Dictionary<int, int>();
        var depotAvail = new Dictionary<int, int>();
        long extraSilverCost = 0;
        long extraGoldCost   = 0;

        // Track remaining already-mounted count per id as we iterate slots 0..2
        var remainingMounted = new Dictionary<int, int>(keptCounts);
        for (int i = 0; i < 3; i++)
        {
            int pid = reqProvisions[i];
            if (pid <= 0) continue;

            if (remainingMounted.TryGetValue(pid, out int rem) && rem > 0)
            {
                remainingMounted[pid] = rem - 1;
                continue; // already equipped on tank
            }

            if (!depotAvail.TryGetValue(pid, out int avail))
                avail = _inventoryRepo.GetItemCount(data.PlayerGUID, ItemTypeProvision, pid);

            if (avail > 0)
            {
                depotAvail[pid] = avail - 1;
                fromDepot[pid]  = fromDepot.GetValueOrDefault(pid) + 1;
                continue;
            }

            var con = _gameTables.GetConsumable(pid);
            if (con == null) continue;
            byte payType = msg.ConsumableMoneyType != null && i < msg.ConsumableMoneyType.Length ? msg.ConsumableMoneyType[i] : (byte)1;
            if (payType == 2 && con.Currency == 2)
                extraGoldCost += con.Price;
            else if (con.Currency == 2 && con.SilverToGold > 0)
                extraSilverCost += con.SilverToGold;
            else if (con.Currency == 2)
                extraGoldCost += con.Price;
            else
                extraSilverCost += con.Price;
        }

        long totalSilverCost = repairCost + extraSilverCost;
        if (data.Silver < totalSilverCost || data.Gold < extraGoldCost)
        {
            session.Send(new PK_HC_TANKREPAIR { TankGuid = msg.TankGuid, RepairResult = 0 });
            return;
        }

        // Apply depot deltas and deduct cost
        var touchedConsumableIds = new HashSet<int>();
        foreach (var (id, cnt) in returnedIds)
        {
            _inventoryRepo.AddItem(data.PlayerGUID, ItemTypeProvision, id, cnt);
            touchedConsumableIds.Add(id);
        }
        foreach (var (id, cnt) in fromDepot)
        {
            _inventoryRepo.RemoveItem(data.PlayerGUID, ItemTypeProvision, id, cnt);
            touchedConsumableIds.Add(id);
        }
        if (totalSilverCost > 0 || extraGoldCost > 0)
        {
            _accountRepo.UpdateResources(data.PlayerGUID,
                silver: data.Silver - (int)totalSilverCost,
                gold: data.Gold - (int)extraGoldCost);
        }

        string provisionsStr = string.Join(";", reqProvisions.Select(p => p.ToString()));
        byte autoRepair = msg.AutoRepair;

        _tankRepo.UpdateTankServiceState(
            msg.TankGuid,
            shellSlotsStr,
            shellSlotsStr,        // active magazine == saved loadout
            provisionsStr,
            provisionsStr,
            autoRepair,
            newCurHp,
            curShellId);

        Console.WriteLine($"[HS] TankRepair: Tank={msg.TankGuid} HP {tank.TankCurHp}->{newCurHp} " +
            $"shells=[{shellSlotsStr}] prov=[{provisionsStr}] auto={autoRepair} silverCost={totalSilverCost} goldCost={extraGoldCost}");

        // ── 3. Tell the client it succeeded, THEN sync the new state ──────────────
        session.Send(new PK_HC_TANKREPAIR { TankGuid = msg.TankGuid, RepairResult = 1 });

        foreach (int cid in touchedConsumableIds)
            SendInventoryUpdate(session, data.PlayerGUID, ItemTypeProvision, cid);

        // This is the packet the СЕРВИС screen waits on: it both syncs the tank locally
        // (Player.OnSyncTankInfo) AND lets RepairFaceBase.StoreConfigInfo close the panel
        // when OK was pressed. Without it the OK button appears to do nothing.
        var sync = new PK_HC_ChangeShellComsumable
        {
            TankGuid = msg.TankGuid,
            AutoRepair = autoRepair,
            HP = newCurHp,
            AutoFillCostFlag = tank.AutoFillCostFlag,
        };
        for (int i = 0; i < 3; i++)
        {
            sync.Shell[i].ShellID = newSlots[i].ID;
            sync.Shell[i].Count   = newSlots[i].Count;
            sync.ShellProject[i].ShellID = newSlots[i].ID;
            sync.ShellProject[i].Count   = newSlots[i].Count;
            sync.Provision[i]         = msg.Provision[i];
            sync.ProvisionProjects[i] = msg.Provision[i];
        }
        session.Send(sync);

        var repairedTank = _tankRepo.GetTankByGUID(msg.TankGuid);
        if (repairedTank != null) SendSingleTankInfo(session, repairedTank);
        SendMoneyExpByGuid(session, data.PlayerGUID);
    }

    // v47 (задача 4): ПОКУПКА МОДИФИКАЦИИ техники. Клиент шлёт ModificationId (id строки
    // Table_Tank_transform) + CurrencyType (валюта оплаты). Модификация — набор
    // альтернативных модулей (корпус/ходовая/башня/орудие/двигатель/рация) для конкретного
    // танка. Применяем: списываем цену, подменяем модули танку, пишем ModificationId,
    // обновляем клиенту деньги и карточку танка. TankGuid клиент не шлёт — целевой танк
    // находим по transform.TankId среди машин игрока.
    private void HandleTransformTank(GameSession session, PK_CH_TransformTank msg)
    {
        var tr = _gameTables.GetTankTransform(msg.ModificationId);
        if (tr == null)
        {
            Console.WriteLine($"[HS] TransformTank: неизвестная модификация id={msg.ModificationId} — отказ");
            session.Send(new PK_HC_TransformTank { SucFlag = 0, ModificationId = msg.ModificationId });
            return;
        }

        var tanks = _tankRepo.GetPlayerTanks(session.PlayerGUID);
        var tank  = tanks.FirstOrDefault(t => t.VehicleId == tr.TankId);
        if (tank == null)
        {
            Console.WriteLine($"[HS] TransformTank: у игрока нет танка tank_id={tr.TankId} для модификации '{tr.Name}' — отказ");
            session.Send(new PK_HC_TransformTank { SucFlag = 0, ModificationId = msg.ModificationId });
            return;
        }

        // Цена: клиент выбирает вариант оплаты (CurrencyType == price_type). Подбираем цену
        // под валюту; если не совпало — берём вариант 1.
        int currency = msg.CurrencyType;
        int price;
        if (currency == tr.PriceType2 && tr.Price2 > 0) price = tr.Price2;
        else { price = tr.Price1; currency = tr.PriceType1; }

        var account = _accountRepo.GetByPlayerGUID(session.PlayerGUID);
        if (account != null && price > 0)
        {
            // currency: 1 = серебро, 2 = золото.
            bool ok = currency == 2 ? account.Gold >= price : account.Silver >= price;
            if (!ok)
            {
                Console.WriteLine($"[HS] TransformTank: не хватает валюты (type={currency}, цена={price}) — отказ");
                SendMoneyExpByGuid(session, session.PlayerGUID);
                session.Send(new PK_HC_TransformTank { SucFlag = 0, ModificationId = msg.ModificationId });
                return;
            }
            if (currency == 2) _accountRepo.UpdateResources(session.PlayerGUID, gold: account.Gold - price);
            else               _accountRepo.UpdateResources(session.PlayerGUID, silver: account.Silver - price);
        }

        // Применяем ровно то, что делает клиент в ModificationSuccess: HullID = use_hull,
        // TurretID = use_turret (это ВИЗУАЛЬНЫЕ модели модификации), остальные модули не трогаем.
        // Так серверная карточка танка совпадает с тем, что показывает клиент, и после
        // перезахода модификация восстанавливается по ModificationId.
        _tankRepo.UpdateTankModification(tank.TankGUID, tr.Id,
            hull: tr.UseHull, underpan: 0, turret: tr.UseTurret, mainGun: 0, engine: 0, radio: 0);

        tank.ModificationId = tr.Id;
        if (tr.UseHull   > 0) tank.HullID   = tr.UseHull;
        if (tr.UseTurret > 0) tank.TurretID = tr.UseTurret;

        Console.WriteLine($"[HS] TransformTank: применена модификация '{tr.Name}' (id={tr.Id}) " +
                          $"танку tank_id={tr.TankId} guid={tank.TankGUID}: HullID→{tr.UseHull}, TurretID→{tr.UseTurret}, " +
                          $"списано {price} (валюта {currency})");

        session.Send(new PK_HC_TransformTank { SucFlag = 1, ModificationId = tr.Id });
        SendMoneyExpByGuid(session, session.PlayerGUID);
        SendSingleTankInfo(session, tank);
    }
    private void HandleBuyGoldTank(GameSession session, PK_CH_BUYGOLDTANK msg)
    {
        Console.WriteLine($"[HS] BuyGoldTank: TankId={msg.TankId}");
        var data = GetPlayerData(session);
        if (data == null) return;

        var tankEntry = _gameTables.GetTank(msg.TankId);
        if (tankEntry == null) 
        { 
            Console.WriteLine($"[HS] BuyGoldTank: Tank {msg.TankId} not found in tables");
            session.Send(new PK_HC_BUYGOLDTANK { TankId = msg.TankId, TimeLimitType = 0, IsBuyCarport = 0, BuyResult = 0 }); 
            return; 
        }

        if (_tankRepo.HasTankByVehicleId(data.PlayerGUID, msg.TankId))
        {
            Console.WriteLine($"[HS] BuyGoldTank: Player already has tank {msg.TankId}");
            session.Send(new PK_HC_BUYGOLDTANK { TankId = msg.TankId, TimeLimitType = 0, IsBuyCarport = 0, BuyResult = 0 });
            return;
        }

        int costSilver = tankEntry.CostSilver;
        int costGold = tankEntry.CostGold;
        Console.WriteLine($"[HS] BuyGoldTank: Cost={costSilver} silver, {costGold} gold. Player has {data.Silver} silver, {data.Gold} gold");

        if (data.Silver < costSilver || data.Gold < costGold)
        {
            Console.WriteLine($"[HS] BuyGoldTank: Not enough resources");
            session.Send(new PK_HC_BUYGOLDTANK { TankId = msg.TankId, TimeLimitType = 0, IsBuyCarport = 0, BuyResult = 0 });
            return;
        }

        _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver - costSilver, gold: data.Gold - costGold);
        var newTank = _tankRepo.CreateTank(data.PlayerGUID, msg.TankId, tankEntry.HullId, tankEntry.UnderpanId, tankEntry.TurretId,
            tankEntry.MainGunId, tankEntry.EngineId, tankEntry.FueltankId, tankEntry.CartridgeId,
            tankEntry.RadioStationId, tankEntry.GaugeId, tankEntry.MaxHp);
        _moduleRepo?.ResearchTankBaseModules(data.PlayerGUID, msg.TankId, tankEntry.HullId, tankEntry.UnderpanId, tankEntry.TurretId,
            tankEntry.MainGunId, tankEntry.EngineId, tankEntry.FueltankId, tankEntry.CartridgeId,
            tankEntry.RadioStationId, tankEntry.GaugeId);
        
        Console.WriteLine($"[HS] BuyGoldTank: Tank {tankEntry.Name} (GUID={newTank?.TankGUID}) created successfully");

        if (msg.IsBuyCarport != 0)
        {
            // Та же проблема, что и в HandleBuyCarport: без сохранения в БД
            // бонусный слот исчезал после следующего логина/HandleLoadCarport.
            int newCap = Math.Min(CarportSlotCapMax, data.CasernCapacity + 1);
            byte gained = (byte)(newCap - data.CasernCapacity);
            if (gained > 0) _accountRepo.UpdateCasernCapacity(data.PlayerGUID, newCap);
            session.Send(new PK_HC_BUYCARPORT { FlagSucceed = 1, CarNum = gained });
        }

        SendAddedTank(session, newTank);
        SendResearchState(session, data.PlayerGUID);
        SendMoneyExpByGuid(session, data.PlayerGUID);

        // Send BuyResult LAST
        Console.WriteLine($"[HS] BuyGoldTank: Sending BuyResult=1");
        session.Send(new PK_HC_BUYGOLDTANK { TankId = msg.TankId, TimeLimitType = msg.TimeLimitType, IsBuyCarport = msg.IsBuyCarport, BuyResult = 1 });
    }
    // Цена слота = purchase_config_carport_price_golden в клиентском конфиге,
    // дефолт (ReadConfig.LoadConfig) — 300 золота, если строка не найдена.
    private const int CarportSlotPriceGold = 300;
    private const int CarportSlotCapMax = 85; // TankSelectList.OnClickBuyPort: >=85 показывает «слоты закончились»

    private void HandleBuyCarport(GameSession session, PK_CH_BUYCARPORT msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        // Раньше здесь всегда слался FlagSucceed=1 без списания денег и без
        // сохранения прироста в БД: TankSelectList.BuyPortSuccess прибавляет
        // CarNum к CarportNum ЛОКАЛЬНО в клиенте, но при следующем логине/
        // HandleLoadCarport сервер пересчитывает CarportNum заново из
        // data.CasernCapacity (см. ComputeCarportNum) — купленный слот пропадал,
        // а сообщение о "покупке" без списания денег выглядело как ошибка.
        if (data.CasernCapacity >= CarportSlotCapMax)
        {
            session.Send(new PK_HC_BUYCARPORT { FlagSucceed = 0, CarNum = 0 });
            return;
        }
        if (data.Gold < CarportSlotPriceGold)
        {
            session.Send(new PK_HC_BUYCARPORT { FlagSucceed = 0, CarNum = 0 });
            return;
        }

        byte carNum = msg.BuyCar == 0 ? (byte)1 : msg.BuyCar;
        int newCapacity = Math.Min(CarportSlotCapMax, data.CasernCapacity + carNum);
        carNum = (byte)(newCapacity - data.CasernCapacity);

        _accountRepo.UpdateResources(data.PlayerGUID, gold: data.Gold - CarportSlotPriceGold);
        _accountRepo.UpdateCasernCapacity(data.PlayerGUID, newCapacity);

        session.Send(new PK_HC_BUYCARPORT { FlagSucceed = 1, CarNum = carNum });
        SendMoneyExpByGuid(session, data.PlayerGUID);
        Console.WriteLine($"[HS] BuyCarport: {data.PlayerName} купил {carNum} слот(ов), "
                        + $"CasernCapacity {data.CasernCapacity} -> {newCapacity}, Gold -{CarportSlotPriceGold}");
    }

    // Официальный курс обмена: 1 золото = 400 серебра, 1 золото = 25 свободного опыта.
    // Используется и в самом обмене (HandleGoldExchangeMoney/HandleGoldExchangeExp), и в
    // пакете PK_HC_ActivityEffectInfor, из которого клиент берёт число для отображения
    // в диалогах "Покупка серебра"/"Покупка опыта" — значения должны совпадать, иначе
    // диалог покажет один курс, а спишется/начислится по другому.
    private const int GoldToSilverRate = 400;
    private const int GoldToExpRate = 25;

    private void HandleGoldExchangeMoney(GameSession session, PK_CH_GOLDEXCHANGE_MONEY msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        if (data.Gold < msg.GoldAmount) return;
        _accountRepo.UpdateResources(data.PlayerGUID, gold: data.Gold - msg.GoldAmount, silver: data.Silver + msg.GoldAmount * GoldToSilverRate);
        SendMoneyExpByGuid(session, data.PlayerGUID);
    }

    private void HandleGoldExchangeExp(GameSession session, PK_CH_GOLDEXCHANGE_EXP msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        if (data.Gold < msg.GoldAmount) return;
        _accountRepo.UpdateResources(data.PlayerGUID, gold: data.Gold - msg.GoldAmount, experience: data.Experience + msg.GoldAmount * GoldToExpRate);
        SendMoneyExpByGuid(session, data.PlayerGUID);
    }

    // Соответствует decfilesgame/EVipKind.cs и порядку в UIVipDialog.OnClickBuy:
    // VipKind (1..6) -> (дней, цена в золоте). Цены зеркалят клиентский конфиг VIP_sell
    // (client-only ReadConfig, недоступен на сервере как таблица), подставлены разумные
    // значения — при желании поправьте под свой баланс.
    private static readonly (int Days, int GoldPrice)[] VipKindTable = new (int, int)[]
    {
        (0,   0),    // индекс 0 не используется (EV_Valid)
        (360, 5400), // EV_12_Month_Sell
        (180, 3000), // EV_6_Month_Sell
        (30,  700),  // EV_1_Month_Sell
        (7,   200),  // EV_7_Day_Sell
        (3,   100),  // EV_3_Day_Sell
        (1,   40),   // EV_1_Day_Sell
    };

    private void HandleVipOperate(GameSession session, PK_CH_VipOperate msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        int kind = msg.VipKind;
        // VipKind==0 (или запрос "просто прислать текущий статус") — просто отвечаем статусом,
        // ничего не покупаем и не списываем.
        if (kind >= 1 && kind < VipKindTable.Length)
        {
            var (days, price) = VipKindTable[kind];
            if (data.Gold >= price)
            {
                _accountRepo.UpdateResources(data.PlayerGUID, gold: data.Gold - price);
                var from = data.VipExpireDate < DateTime.UtcNow ? DateTime.UtcNow : data.VipExpireDate;
                _accountRepo.UpdateVipStatus(data.PlayerGUID, true, from.AddDays(days));
                Console.WriteLine($"[VIP] GUID={data.PlayerGUID} bought VipKind={kind} (+{days}d, -{price} gold)");

                // Обновляем локальные данные и шлём актуальные золото/статус клиенту.
                data.Gold -= price;
                SendMoneyExpByGuid(session, data.PlayerGUID);
            }
            else
            {
                Console.WriteLine($"[VIP] GUID={data.PlayerGUID} insufficient gold for VipKind={kind}: has={data.Gold}, need={price}");
            }
        }

        var refreshed = GetPlayerData(session) ?? data;
        int vipLeftTime = 0;
        if (refreshed.IsVip)
        {
            vipLeftTime = (int)(refreshed.VipExpireDate - DateTime.UtcNow).TotalSeconds;
            if (vipLeftTime < 0) vipLeftTime = 0;
        }

        session.Send(new PK_HC_VipTime { VipLeftTime = vipLeftTime });
    }
    private void HandleGiftCardUse(GameSession session, PK_CH_GiftCardUse msg) => session.Send(new PK_HC_GiftCardUse { UseResult = 0 });
    
    private void HandleModuleOperate(GameSession session, PK_CH_ModuleOperate msg)
    {
        var data = GetPlayerData(session);
        if (data == null)
        {
            session.Send(new PK_HC_ModuleOperate { TankGUID = msg.TankGUID, ModuleType = msg.ModuleType, ModuleID = msg.ModuleID, ResultFlag = 0 });
            return;
        }

        var tank = _tankRepo.GetTankByGUID(msg.TankGUID);
        if (tank == null)
        {
            session.Send(new PK_HC_ModuleOperate { TankGUID = msg.TankGUID, ModuleType = msg.ModuleType, ModuleID = msg.ModuleID, ResultFlag = 0 });
            return;
        }

        var part = _gameTables.GetPart(msg.ModuleType, msg.ModuleID);
        if (part == null)
        {
            Console.WriteLine($"[HS] ModuleOperate rejected: unknown module type={msg.ModuleType}, id={msg.ModuleID}");
            session.Send(new PK_HC_ModuleOperate { TankGUID = msg.TankGUID, ModuleType = msg.ModuleType, ModuleID = msg.ModuleID, ResultFlag = 0 });
            return;
        }

        _moduleRepo?.AddResearchedModule(data.PlayerGUID, tank.VehicleId, msg.ModuleType, msg.ModuleID);

        int hullId = tank.HullID, underpanId = tank.UnderpanID, turretId = tank.TurretID, mainGunId = tank.MainGunID;
        int engineId = tank.EngineID, fueltankId = tank.FueltankID, cartridgeId = tank.CartridgeID, radioId = tank.RadioStationID, gaugeId = tank.GaugeID;

        switch (msg.ModuleType)
        {
            case 1: hullId = msg.ModuleID; break;
            case 2: underpanId = msg.ModuleID; break;
            case 3: underpanId = msg.ModuleID; break;
            case 4: turretId = msg.ModuleID; break;
            case 5: mainGunId = msg.ModuleID; break;
            case 6: engineId = msg.ModuleID; break;
            case 7: radioId = msg.ModuleID; break;
            case 8: cartridgeId = msg.ModuleID; break;
            case 9: fueltankId = msg.ModuleID; break;
            case 10: gaugeId = msg.ModuleID; break;
        }

        int tankHp = _gameTables.GetTankMaxHp(tank.VehicleId, hullId, turretId);   // v53: корпус + башня
        int tankCurHp = NormalizeCurrentHpForEquipmentChange(tank.TankHp, tank.TankCurHp, tankHp);
        string shellSlots = tank.ShellSlots;
        string deployShellSlots = tank.DeployShellSlots;
        int curShellId = tank.CurShellId;

        if (msg.ModuleType == 5)
        {
            var loadout = _gameTables.BuildDefaultShellLoadout(mainGunId);
            shellSlots = loadout.ShellSlots;
            deployShellSlots = loadout.DeployShellSlots;
            curShellId = loadout.CurShellId;
        }

        _tankRepo.UpdateTankEquipment(
            msg.TankGUID,
            hullId,
            underpanId,
            turretId,
            mainGunId,
            engineId,
            fueltankId,
            cartridgeId,
            radioId,
            gaugeId,
            tankHp,
            tankCurHp,
            tank.TankCurrencyType,
            shellSlots,
            deployShellSlots,
            curShellId);

        Console.WriteLine($"[HS] Module operated: Player={data.PlayerGUID}, Vehicle={tank.VehicleId}, Type={msg.ModuleType}, ID={msg.ModuleID}");

        session.Send(new PK_HC_ModuleOperate { TankGUID = msg.TankGUID, ModuleType = msg.ModuleType, ModuleID = msg.ModuleID, ResultFlag = 1 });
        SendSingleTankInfo(session, _tankRepo.GetTankByGUID(msg.TankGUID));
        SendResearchState(session, data.PlayerGUID);
    }
    // v.fix: раньше всегда отвечал nlevl=1 (Тир 2) — теперь эхом тот тир, который
    // реально закрыли (см. комментарий у PK_CH_EndShoot).
    private void HandleEndShoot(GameSession session, PK_CH_EndShoot msg) => session.Send(new PK_HC_EndShoot { nlevl = msg.nLevl });
    private void HandleShootPrize(GameSession session, PK_CH_ShootPrize msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        // v.fix (Тир): раньше выстрел ничего не стоил — приз выдавался за клик
        // вообще без проверки/списания оставшихся выстрелов. Теперь: нет выстрелов
        // на этом уровне — приз не выдаём (nResult=0), как и при "PickBonusbox() == null".
        int remaining = _accountRepo.GetShootTimes(data.PlayerGUID, msg.nLevl);
        if (remaining <= 0)
        {
            session.Send(new PK_HC_ShootPrize { nLevl = msg.nLevl, nResult = 0, nID = 0, nIndex = msg.nIndex, sTime = "" });
            return;
        }

        // Раньше отвечали { ShootType, PrizeId=0 } в неверном формате — клиент
        // (NewTreasureBoxDownPanel.OnShootItemPrizeRet) требует nResult==1, чтобы
        // засчитать приз в нажатой ячейке nIndex.
        var box = PickBonusbox();
        if (box == null)
        {
            session.Send(new PK_HC_ShootPrize { nLevl = msg.nLevl, nResult = 0, nID = 0, nIndex = msg.nIndex, sTime = "" });
            return;
        }

        _accountRepo.AddShootTimes(data.PlayerGUID, msg.nLevl, -1);
        GrantBonusbox(session, data.PlayerGUID, box);
        SendMoneyExpByGuid(session, data.PlayerGUID);
        SendRemainShootTimes(session, data.PlayerGUID);

        session.Send(new PK_HC_ShootPrize
        {
            nLevl = msg.nLevl,
            nResult = 1,
            nID = box.Id,
            nIndex = msg.nIndex,
            sTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")
        });
        Console.WriteLine($"[HS] ShootPrize: {data.PlayerName} ячейка={msg.nIndex} приз id={box.Id} (type={box.RewardItemType})");
    }

    private void HandleChangeBattleTankList(GameSession session, PK_CH_ChangeBattleTankList msg)
    {
        var data = GetPlayerData(session);
        bool success = data != null;
        if (data != null && msg.Index == 1)
        {
            _tankRepo.UpdateBattleTankList(data.PlayerGUID, msg.TankGuids);
        }
        session.Send(new PK_HC_ChangeBattleTankList { Flag = success });
    }

    private void HandleChangeTank(GameSession session, PK_CH_Change_Tank msg)
    {
        Console.WriteLine($"[HS] ChangeTank: TankGuid={msg.TankGuid}");
        var data = GetPlayerData(session);
        if (data == null) return;

        var tank = _tankRepo.GetTankByGUID(msg.TankGuid);
        if (tank == null || tank.PlayerGUID != data.PlayerGUID)
        {
            return;
        }

        MarkBattleTank(data.PlayerGUID, msg.TankGuid);
        SendSingleTankInfo(session, tank);
    }
    private void HandleSetGarageBackground(GameSession session, PK_CH_Set_Garage_Background msg)
    {
        var data = GetPlayerData(session);
        if (data != null) _accountRepo.UpdateGarageBackground(data.PlayerGUID, msg.BackgroundId);
        // Клиент (AdvancedAccountManager.OnSetGarageBackgroundResult) применяет смену
        // фона ТОЛЬКО если Result == 1, и берёт итоговый id гаража из TargetIndex
        // (реальный id, напр. 10001..10005 — тот же, что пришёл в запросе BackgroundId).
        session.Send(new PK_HC_Set_Garage_Background { Result = 1, TargetIndex = msg.BackgroundId });
    }

    private void HandleInviteTeam(GameSession session, PK_CH_Invite_Team msg)
    {
        Console.WriteLine($"[HS] Invite team by {session.PlayerName}");
    }
    private void HandleJoinTeam(GameSession session, PK_CH_Join_Team msg) => session.Send(new PK_HC_Team_Info { TeamGuid = msg.TeamGuid, BattleMode = 1, MemberCount = 1, MaxMemberCount = 2 });
    private void HandleLeaveTeam(GameSession session) => session.Send(new PK_HC_Leave_Team { PlayerGuid = session.PlayerGUID });
    private void HandleKickTeam(GameSession session, PK_CH_Kick_Team msg) => session.Send(new PK_HC_Leave_Team { PlayerGuid = msg.PlayerGuid });

    private void HandleTeamChangeMode(GameSession session) => session.Send(new PK_HC_Team_Update_Mode { });
    private void HandleSetFreeTeam(GameSession session, PK_CH_Set_Free_Team msg) => session.Send(new PK_HC_Free_Team { FreeTeamState = msg.FreeTeamState });
    private void HandleBeInviteTeamRet(GameSession session)
    {
        Console.WriteLine($"[HS] BeInviteTeamRet by {session.PlayerName}");
        session.Send(new PK_HC_Team_Info { TeamGuid = 0, BattleMode = 0, MemberCount = 0, MaxMemberCount = 0 });
    }
    private void HandleRespReconnect(GameSession session) => session.Send(new PK_HC_RespReconnectResult { Result = 1 });
    // v12: собирает окно «Статистика боя» из пакета RoomServer'а. Оборона (mode 4)
    // получает PVE-вариант (id 442) с массивом волн/скиллов, остальные режимы —
    // обычный (id 434) с очками лояльности. Пустой пакет -> нулевое окно (не падаем).
    internal static BaseRequest BuildBattleEndMessage(BattleEndResult? r)
    {
        var m = new PK_HC_BattleEndInfo();
        if (r != null)
        {
            m.RoomGuid    = r.RoomGuid;
            m.MapId       = r.MapId;
            m.Money       = r.Money;
            m.Exp         = r.Exp;
            m.VipMoney    = r.VipMoney;
            m.VipExp      = r.VipExp;
            m.State       = r.LiveState;
            m.Result      = r.Result;
            m.StartSecond = r.StartUnix;
            m.TankId      = r.TankId;
            m.ShotCount   = r.ShotCount;
            m.HitCount    = r.HitCount;
            m.PierceCount = r.PierceCount;
            m.Damage      = r.Damage;
            m.KillCount   = r.Kills;
            m.DamagedEnemies = r.DamagedEnemies;
            m.OccupyValue = r.OccupyValue;
            m.BeHitCount  = r.BeHitCount;
            m.LostHp      = r.LostHp;
            m.MaxHp       = r.MaxHp;
            m.RepairSilver= r.RepairCost;
            m.SupplyCost  = r.SupplyCost;
            m.ShellCost   = r.ShellCost;
            m.LightDamage = r.LightDamage;
            m.LightCount  = r.LightCount;
            m.ChassisShareDamage = r.ChassisShareDamage;
            m.PotentialDamage    = r.PotentialDamage;
            m.RoomTypeWire= r.ModeWire;
            m.GroupReward = r.GroupReward;
            m.LoyalPoint  = r.LoyalPoint;
            m.Victims     = r.Victims;   // v16: «повреждено врагов» подробно
        }
        if (r != null && r.ModeWire == 4)   // оборона -> PVE-окно
            return new PK_HC_BattleEndInfoPVE
            {
                Common       = m,
                PveInfo      = r.PveInfo,
                CrewExtraExp = r.CrewExtraExp,
            };
        return m;
    }

    // v16 (баг 3): вкладка «Результат команды». Отдельное сообщение id 231.
    internal static PK_HC_TeamBattleResult? BuildTeamResultMessage(BattleEndResult? r)
    {
        if (r == null || r.TeamRows.Count == 0) return null;
        return new PK_HC_TeamBattleResult { RoomGUID = r.RoomGuid, Rows = r.TeamRows };
    }

    private void HandleCancleJoinRoom(GameSession session)
    {
        lock (_matchLock)
        {
            RemovePlayerFromWaitingLobbiesLocked(session.PlayerGUID);
        }
        Console.WriteLine($"[HS] Cancelled matchmaking queue for {session.PlayerGUID}");
        session.Send(new PK_HC_CancleJoinRoom { Flag = 1 });
    }

    // ── Lottery prize table ────────────────────────────────────────────────────
    //
    // Custom 8-slot wheel. Each entry:  (slotId, weight, silverGain, goldGain, vipDays, vehicleId)
    //   vehicleId > 0 → add that tank permanently to the garage
    //   vehicleId = 0 → resources/VIP only
    //
    // slotIds 1-8 match the 8 positions on the client's roulette wheel graphic.
    // v30 (пункт 4): у слота теперь СПИСОК танков — главный приз выдаёт сразу два.
    // ═══════════════════════ Рулетка «Золотой запас» ═══════════════════════════
    //
    // ГЛАВНОЕ ПРО ЭТОТ МЕХАНИЗМ. Сервер шлёт в PK_HC_LotteryDraw / PK_HC_10_LotteryDraw
    // ТОЛЬКО номер строки клиентской таблицы Table_New_bonusbox. Всё остальное клиент
    // берёт из неё сам: на каком секторе остановить стрелку, какую иконку показать и
    // что написать в «Официальной хронике» («Получена машина: M4A2 L (на 7 дней)»).
    //
    // Из-за этого прошлая версия и разъезжалась: сервер держал собственную табличку
    // из 8 слотов и слал id 1..8, считая их «серебро/золото/VIP», а в клиентской
    // таблице под этими номерами лежат ВРЕМЕННЫЕ ТАНКИ. Клиент честно объявлял танки,
    // сервер честно начислял серебро — отсюда сразу три жалобы: выпадают одни танки,
    // они не сохраняются, и вообще ничего кроме танков не видно.
    //
    // Поэтому источник истины теперь один — Table_New_bonusbox (GameTables/Data/
    // client_game_tables.json, секция Bonusboxes). Сервер выбирает СТРОКУ, шлёт её id
    // и выдаёт ровно то, что в строке написано.
    //
    // reward_itemtype (сверено по TreasureBox.cs / TreasureBoxManager.cs):
    //   1 опыт · 2 серебро · 3 золото · 4 танк · 5 VIP · 6 расходники
    // У типа 4 reward_level == 1 — ГЛАВНЫЙ приз (сектор 9), иначе премиум-танк (сектор 3).
    // reward_itemcount у типа 4 — срок в днях, отрицательное значение = навсегда.

    private const double ChanceMainPrize   = 0.01;   // 1 %  — главный приз
    private const double ChancePremiumTank = 0.05;   // 5 %  — премиум-танки

    // ── Главный приз «Золотого запаса» ────────────────────────────────────────
    //
    // Запасной пул из 7 машин — на случай, если в таблице нет строки главного приза.
    //   149 Obj279 · 137 Object 140 · 163 FV214 · 168 1K-17 · 174 FV4030/4 ·
    //   179 Object 263 · 208 XM815 Chrysler
    private static readonly int[] MainPrizePool = { 149, 137, 163, 168, 174, 179, 208 };
    private static readonly int FallbackMainPrizeId =
        MainPrizePool[new Random().Next(MainPrizePool.Length)];

    /// <summary>
    /// Какой танк — главный приз. ИСТОЧНИК ИСТИНЫ — строка reward_level=1 в таблице
    /// призов (bonusboxes.json). Её же показывает витрина, если синхронизировать
    /// level0 ротатором tools/rotate_showcase.py (он меняет и витрину, и эту строку,
    /// и — при --sync-server — bonusboxes.json). Так витрина, «Подробнее» и реальная
    /// выдача всегда совпадают.
    ///
    /// Если строки главного приза в таблице нет — берём из запасного пула.
    /// Если выбранная машина у игрока уже есть — отдаём другую из пула, чтобы прокрут
    /// не пропал.
    ///
    /// Про КАРТИНКУ витрины: клиент читает её из level0 (Table_New_bonusbox_power_reward)
    /// при запуске; «на лету» сервер её не меняет. Ротация витрины между сессиями —
    /// через rotate_showcase.py в start.bat (см. tools/start_with_showcase.bat).
    /// <summary>
    /// Читает id текущего главного приза из state-файла current_mainprize.txt, если он
    /// есть. Файл пишет батник ротации витрины (start_showcase.bat) — так сервер
    /// выдаёт РОВНО тот танк, что сейчас показан на витрине клиента. Читаем каждый раз
    /// заново (без кэша), чтобы подхватывать смену витрины без перезапуска сервера.
    /// Возвращает 0, если файла нет или он пуст/битый.
    /// </summary>
    private int ReadShowcaseMainPrize()
    {
        // Ищем current_mainprize.txt в нескольких местах — какое найдётся первым.
        // Так работает и без ручной настройки пути: батник ротации пишет файл в
        // %TEMP% (общий для процессов пользователя) и рядом с сервером.
        var paths = new[]
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "GameTables", "Data", "current_mainprize.txt"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "current_mainprize.txt"),
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gwt_mainprize.txt"),
            Environment.GetEnvironmentVariable("GWT_MAINPRIZE") ?? "",
        };
        foreach (var path in paths)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) continue;
                string txt = System.IO.File.ReadAllText(path).Trim();
                if (int.TryParse(txt, out int id) && id > 0) return id;
            }
            catch { /* пробуем следующий путь */ }
        }
        return 0;
    }

    private int GetCurrentMainPrize(long playerGuid)
    {
        // 1) главный приз, синхронный с витриной клиента: сначала state-файл (его
        //    пишет батник ротации), затем строка reward_level=1 в таблице, затем пул.
        int showcaseId = ReadShowcaseMainPrize();
        if (showcaseId > 0)
            Console.WriteLine($"[Lottery] Главный приз синхронизирован с витриной: vehicleId={showcaseId}");
        int tableMain  = _gameTables.Bonusboxes.Values
            .Where(b => b.IsMainPrize)
            .Select(b => b.RewardItemId)
            .FirstOrDefault();
        int primary = showcaseId > 0 ? showcaseId
                    : tableMain  > 0 ? tableMain
                    : FallbackMainPrizeId;

        var ownedIds = _tankRepo.GetPlayerTanks(playerGuid).Select(t => t.VehicleId).ToHashSet();
        if (!ownedIds.Contains(primary)) return primary;

        // 2) уже есть — отдаём любую другую из пула, которой нет
        foreach (int id in MainPrizePool)
            if (!ownedIds.Contains(id)) return id;
        return primary;   // всё есть — уйдёт в компенсацию серебром
    }

    /// <summary>
    /// Выбирает строку приза. Шансы танков заданы жёстко (1 % и 5 %), всё остальное
    /// делится по drop_rate из клиентской таблицы.
    /// </summary>
    private BonusboxEntry? PickBonusbox()
    {
        // ВАЖНО: Bonusboxes — IReadOnlyDictionary, у его Values нет свойства Count,
        // там только LINQ-метод Count(). Материализуем в список один раз — и Count
        // становится свойством, и три последующих Where идут по готовой коллекции.
        var all = _gameTables.Bonusboxes.Values.ToList();
        if (all.Count == 0) return null;

        var mainPrize = all.Where(b => b.IsMainPrize).ToList();
        var premium   = all.Where(b => b.IsPremiumTank).ToList();
        var rest      = all.Where(b => !b.IsTank).ToList();

        double roll = _random.NextDouble();
        if (mainPrize.Count > 0 && roll < ChanceMainPrize)
            return mainPrize[_random.Next(mainPrize.Count)];
        if (premium.Count > 0 && roll < ChanceMainPrize + ChancePremiumTank)
            return premium[_random.Next(premium.Count)];

        if (rest.Count == 0) return all.First();

        // Внутри «не-танков» соблюдаем drop_rate из таблицы.
        int total = rest.Sum(b => Math.Max(1, b.DropRate));
        int pick  = _random.Next(0, total);
        int acc   = 0;
        foreach (var b in rest)
        {
            acc += Math.Max(1, b.DropRate);
            if (pick < acc) return b;
        }
        return rest[^1];
    }

    /// <summary>
    /// Выдаёт РОВНО то, что записано в строке таблицы, — так объявление клиента и
    /// реальное начисление всегда совпадают.
    /// </summary>
    private void GrantBonusbox(GameSession session, long playerGuid, BonusboxEntry box)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        switch (box.RewardItemType)
        {
            case 1: // опыт
                _accountRepo.UpdateResources(playerGuid, experience: data.Experience + box.RewardItemCount);
                Console.WriteLine($"[Lottery] id={box.Id} опыт +{box.RewardItemCount}");
                break;

            case 2: // серебро
                _accountRepo.UpdateResources(playerGuid, silver: data.Silver + box.RewardItemCount);
                Console.WriteLine($"[Lottery] id={box.Id} серебро +{box.RewardItemCount}");
                break;

            case 3: // золото
                _accountRepo.UpdateResources(playerGuid, gold: data.Gold + box.RewardItemCount);
                Console.WriteLine($"[Lottery] id={box.Id} золото +{box.RewardItemCount}");
                break;

            case 4: // танк
                GrantLotteryTank(session, playerGuid, box);
                break;

            case 5: // VIP. reward_itemcount — КОД срока (сверено с TreasureBoxManager):
            {       //   3→30 дней, 5→3, 4→7, 2→180, 1→360.
                int days = box.RewardItemCount switch
                {
                    3 => 30, 5 => 3, 4 => 7, 2 => 180, 1 => 360, _ => box.RewardItemCount,
                };
                if (days > 0)
                {
                    var acc = _accountRepo.GetByPlayerGUID(playerGuid);
                    if (acc != null)
                    {
                        var from = acc.VipExpireDate < DateTime.UtcNow ? DateTime.UtcNow : acc.VipExpireDate;
                        _accountRepo.UpdateVipStatus(playerGuid, true, from.AddDays(days));
                        Console.WriteLine($"[Lottery] id={box.Id} VIP +{days}d");
                    }
                }
                break;
            }

            case 6: // свободный опыт (клиент: IntData1=12). reward_itemcount — количество.
                _accountRepo.UpdateResources(playerGuid, experience: data.Experience + box.RewardItemCount);
                Console.WriteLine($"[Lottery] id={box.Id} свободный опыт +{box.RewardItemCount}");
                break;

            case 7: // экипаж (клиент: GetTankCrew). reward_itemid — id экипажа.
                _inventoryRepo.AddItem(playerGuid, ItemTypeCrew, box.RewardItemId,
                                       Math.Max(1, box.RewardItemCount));
                Console.WriteLine($"[Lottery] id={box.Id} экипаж {box.RewardItemId} x{box.RewardItemCount}");
                break;

            case 8: // оснащение (клиент: GetEquipmentById). reward_itemid — id оснащения.
                _inventoryRepo.AddItem(playerGuid, ItemTypeEquipment, box.RewardItemId,
                                       Math.Max(1, box.RewardItemCount));
                Console.WriteLine($"[Lottery] id={box.Id} оснащение {box.RewardItemId} x{box.RewardItemCount}");
                break;

            case 9: // модуль/трансформация (клиент: GetTankTransform).
                _inventoryRepo.AddItem(playerGuid, ItemTypeEquipment, box.RewardItemId,
                                       Math.Max(1, box.RewardItemCount));
                Console.WriteLine($"[Lottery] id={box.Id} модуль {box.RewardItemId} x{box.RewardItemCount}");
                break;

            default:
                Console.WriteLine($"[Lottery] id={box.Id}: неизвестный reward_itemtype={box.RewardItemType} — пропущено");
                break;
        }

        SendMoneyExpByGuid(session, playerGuid);
    }

    /// <summary>
    /// Танк из рулетки. Ставит машину в гараж и, если приз временный,
    /// проставляет срок в GoldTankLeftTime — клиент показывает его как «на N дней».
    /// </summary>
    private void GrantLotteryTank(GameSession session, long playerGuid, BonusboxEntry box)
    {
        // v46: режим срока танков, выпадающих из рулетки. Меняется ОДНОЙ строкой ниже:
        //   0 = по таблице bonusboxes.json (RewardItemCount > 0 → столько дней,
        //       RewardItemCount <= 0 → навсегда). Сейчас: главный приз навсегда, премы 7 дн.
        //   1 = ВСЕ выпавшие танки НАВСЕГДА.
        //   2 = ВСЕ выпавшие танки на 7 дней.
        // Для отдельного танка срок по-прежнему задаётся знаком RewardItemCount в
        // bonusboxes.json (>0 = дни, <0 = навсегда) — режим 0 это уважает.
        const int ForceLotteryTankMode = 0;
        int effDays = ForceLotteryTankMode switch
        {
            1 => 0,            // навсегда
            2 => 7,            // 7 дней
            _ => box.TankDays, // по таблице (>0 — дни; 0/<0 — навсегда)
        };

        // Для ГЛАВНОГО приза (reward_level=1) фактическую машину выбираем по гаражу:
        // в таблице стоит FV4030/4, но если она уже есть — выдаём FV214 Conqueror.
        // Так пара чередуется и в колесе, и по шкале мощи, переживая перезапуск.
        // Клиенту при этом отправлен id строки главного приза, поэтому анимация и
        // витрина остаются корректными независимо от того, какую из двух выдали.
        int vehicleId = box.IsMainPrize ? GetCurrentMainPrize(playerGuid) : box.RewardItemId;
        var tankEntry = _gameTables.GetTank(vehicleId);
        if (tankEntry == null)
        {
            Console.WriteLine($"[Lottery] id={box.Id}: vehicleId={vehicleId} нет в таблице танков — приз пропущен");
            return;
        }

        var owned = _tankRepo.GetPlayerTanks(playerGuid);
        var existing = owned.FirstOrDefault(t => t.VehicleId == vehicleId);
        if (existing != null)
        {
            // Уже есть. Для временного приза продлеваем срок, для постоянного —
            // компенсируем серебром, чтобы прокрут не пропадал впустую.
            if (effDays > 0)
            {
                int left = Math.Max(0, existing.GoldTankLeftTime) + effDays * 86400;
                _tankRepo.SetGoldTankLeftTime(existing.TankGUID, left);
                Console.WriteLine($"[Lottery] id={box.Id}: {tankEntry.Name} уже есть — срок продлён на {effDays} дн.");
                // Не шлём полный TANKINFO (ломает клиент при непустом HallTanks).
                // Новый срок применится при следующем обновлении ангара/перезаходе.
            }
            else
            {
                // Постоянный танк уже есть — компенсируем серебром. Читаем профиль
                // и молча выходим, если он не прочитался: иначе «0 + 200k» затёрло бы
                // весь баланс игрока.
                var acc = GetPlayerData(session);
                if (acc != null)
                {
                    _accountRepo.UpdateResources(playerGuid, silver: acc.Silver + 200_000);
                    Console.WriteLine($"[Lottery] id={box.Id}: {tankEntry.Name} уже есть — +200k серебра компенсации");
                }
            }
            return;
        }

        var newTank = _tankRepo.CreateTank(playerGuid, vehicleId,
            tankEntry.HullId, tankEntry.UnderpanId, tankEntry.TurretId,
            tankEntry.MainGunId, tankEntry.EngineId, tankEntry.FueltankId,
            tankEntry.CartridgeId, tankEntry.RadioStationId, tankEntry.GaugeId,
            tankEntry.MaxHp);
        _moduleRepo?.ResearchTankBaseModules(playerGuid, vehicleId,
            tankEntry.HullId, tankEntry.UnderpanId, tankEntry.TurretId,
            tankEntry.MainGunId, tankEntry.EngineId, tankEntry.FueltankId,
            tankEntry.CartridgeId, tankEntry.RadioStationId, tankEntry.GaugeId);

        if (effDays > 0)
            _tankRepo.SetGoldTankLeftTime(newTank.TankGUID, effDays * 86400);

        string term = effDays > 0 ? $"на {effDays} дн." : "навсегда";
        Console.WriteLine($"[Lottery] id={box.Id}: выдан {tankEntry.Name} (vehicleId={vehicleId}) {term} → {playerGuid}");

        // ТОЛЬКО AddTank. Раньше следом шёл ещё и SendGarageRefresh (полный TANKINFO
        // со всеми танками) — и это ЛОМАЛО клиент: PK_HC_AddTank уже добавил танк в
        // HallTanks, а следующий полный TANKINFO с Count>1 при непустом HallTanks
        // попадает в клиентскую ветку ошибки (TankInfoOperator: "failed") и не
        // обновляет список; вдобавок HallTanks.Count раздувался, из-за чего клиент
        // считал слоты занятыми («достигли предела слотов») уже при одном видимом
        // танке. AddTank — правильный и достаточный путь: клиент сам добавит и данные,
        // и плитку (если ангар активен).
        SendAddedTank(session, newTank);
        // v.fix: без этого плитка нового танка показывала только название и была
        // "пустой" (без иконки/характеристик/доступных модулей) до перезахода —
        // ровно так же, как раньше вело себя HandleBuyGoldTank ДО того, как туда
        // добавили тот же вызов. TANKINFO это не ломает (в отличие от полного
        // SendGarageRefresh выше), это отдельное сообщение с деревом исследований.
        SendResearchState(session, playerGuid);
    }

    private readonly ConcurrentDictionary<long, float> _lotteryPower = new();

    private void HandleLotteryDraw(GameSession session, PK_CH_LotteryDraw msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        const int SilverCost = 150_000;
        const int GoldCost   = 300;
        bool useGold = msg.Flag == 1;

        if (useGold)
        {
            if (data.Gold < GoldCost) return;
            _accountRepo.UpdateResources(data.PlayerGUID, gold: data.Gold - GoldCost);
        }
        else
        {
            if (data.Silver < SilverCost) return;
            _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver - SilverCost);
        }

        var box = PickBonusbox();
        if (box == null)
        {
            Console.WriteLine("[Lottery] Table_New_bonusbox не выгружен — прокрут отменён, деньги возвращены");
            if (useGold) _accountRepo.UpdateResources(data.PlayerGUID, gold: data.Gold);
            else         _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver);
            SendMoneyExpByGuid(session, data.PlayerGUID);
            return;
        }

        // Сначала объявляем клиенту номер строки, потом выдаём её же содержимое —
        // объявление и начисление гарантированно совпадают.
        session.Send(new PK_HC_LotteryDraw { PrizeId = box.Id });
        GrantBonusbox(session, data.PlayerGUID, box);

        float power = Math.Min(100f, _lotteryPower.GetOrAdd(data.PlayerGUID, 0f) + 1f);
        _lotteryPower[data.PlayerGUID] = power;

        session.Send(new PK_HC_LotteryData
        {
            LeftTime   = 86400, HaveGot = Math.Abs(power - 100f) < float.Epsilon ? (byte)1 : (byte)0,
            PowerValue = power, IsEveryDayFirstLottery_One = false, IsEveryDayFirstLottery_Ten = false
        });
        Console.WriteLine($"[Lottery] Draw: player={data.PlayerName} id={box.Id} type={box.RewardItemType} power={power:F1}");
    }

    private void Handle10LotteryDraw(GameSession session)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        const int GoldCost10 = 3000;
        if (data.Gold < GoldCost10) return;
        _accountRepo.UpdateResources(data.PlayerGUID, gold: data.Gold - GoldCost10);

        var boxes  = new List<BonusboxEntry>(10);
        var prizes = new PK_HC_10_LotteryDraw();
        for (int i = 0; i < 10; i++)
        {
            var b = PickBonusbox();
            if (b == null)
            {
                Console.WriteLine("[Lottery] Table_New_bonusbox не выгружен — набор попыток отменён");
                _accountRepo.UpdateResources(data.PlayerGUID, gold: data.Gold);
                SendMoneyExpByGuid(session, data.PlayerGUID);
                return;
            }
            boxes.Add(b);
            prizes.PrizeIds[i] = b.Id;
        }
        session.Send(prizes);

        foreach (var b in boxes) GrantBonusbox(session, data.PlayerGUID, b);

        float power = Math.Min(100f, _lotteryPower.GetOrAdd(data.PlayerGUID, 0f) + 10f);
        _lotteryPower[data.PlayerGUID] = power;

        session.Send(new PK_HC_LotteryData
        {
            LeftTime   = 86400, HaveGot = Math.Abs(power - 100f) < float.Epsilon ? (byte)1 : (byte)0,
            PowerValue = power, IsEveryDayFirstLottery_One = false, IsEveryDayFirstLottery_Ten = false
        });
        Console.WriteLine($"[Lottery] 10Draw: player={data.PlayerName} power={power:F1}");
    }

    private void HandleBuyDiscountItem(GameSession session, PK_CH_BuyDiscountItem msg)
    {
        var data = GetPlayerData(session);
        if (data == null) { session.Send(new PK_HC_BuyDiscountItem { BuyResult = 0 }); return; }

        var item = _gameTables.GetItem(msg.ItemId);
        if (item == null) { session.Send(new PK_HC_BuyDiscountItem { BuyResult = 0 }); return; }

        int costSilver = (int)(item.CostSilver * 0.8f) * msg.ItemNum;
        int costGold = (int)(item.CostGold * 0.8f) * msg.ItemNum;

        if (data.Silver < costSilver && data.Gold < costGold)
        {
            session.Send(new PK_HC_BuyDiscountItem { BuyResult = 0 });
            return;
        }

        _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver - costSilver, gold: data.Gold - costGold);
        _inventoryRepo.AddItem(data.PlayerGUID, item.Type, msg.ItemId, msg.ItemNum, 0);
        session.Send(new PK_HC_BuyDiscountItem { BuyResult = 1 });
        session.Send(new PK_HC_UPDATEITEM
        {
            ItemType = item.Type,
            ItemId = msg.ItemId,
            ItemNum = _inventoryRepo.GetItemCount(data.PlayerGUID, item.Type, msg.ItemId)
        });
        SendMoneyExpByGuid(session, data.PlayerGUID);
    }
    private void HandleHotGoods(GameSession session) => session.Send(new PK_HC_HotGoods());
    private void HandleDiscountItemRemain(GameSession session) => session.Send(new PK_HC_Discount_Item_Remain { RemainCount = 0 });
    private void HandleLimitBuyActivityTime(GameSession session) =>
        session.Send(new PK_HC_LimitBuyActivityTime
        {
            ServerStartTime = 0,
            ServerEndTime = 0,
            PersonStartTime = 0,
            PersonEndTime = 0
        });
    private void HandleGetSingleRechargeReward(GameSession session)
    {
        session.Send(new PK_HC_SingleRechargeRewardStatus());
    }
    private void HandleGetAccumulateRechargeReward(GameSession session)
    {
        session.Send(new PK_HC_AccumulateRechargeRewardStatus());
        // Also push the veteran status via the correct channel.
        session.Send(new PK_HC_UpdatePlayerCharged { IsChargedFlag = true, TotalCharge = 999999 });
    }
    private void HandleGetContinuousLoginReward(GameSession session)
    {
        var resp = new PK_HC_ContinuousLoginRewardStatus();
        for (int i = 0; i < 7; i++) resp.RewardStatus[i] = 0;
        session.Send(resp);
    }
    private void HandleContinuousLoginRewardID(GameSession session) =>
        session.Send(new PK_HC_ContinuousLoginRewardID { RewardIds = new int[7] });

    private void HandleTournamentRegister(GameSession session)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        _tournamentRepo.Register(data.PlayerGUID);
        session.Send(new PK_HC_TournamentRegisterResult { Result = 1 });
        session.Send(new PK_HC_TournamentInfo { Status = 1, RegisterState = 1, GetRewardState = 0 });
    }

    private void HandleTournamentGetAllRange(GameSession session)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var rankings = _tournamentRepo.GetRankings(50);
        Console.WriteLine($"[HS] Tournament rankings: {rankings.Count} players");
        var tData = _tournamentRepo.GetByPlayerGUID(data.PlayerGUID);
        int selfRank = _tournamentRepo.GetPlayerRank(data.PlayerGUID);
        Console.WriteLine($"[HS] Self rank: {selfRank}, damage: {tData?.TotalDamage ?? 0}");
        session.Send(new PK_HC_TournamentAllRange { });
    }

    private void HandleTournamentGetReward(GameSession session)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var tData = _tournamentRepo.GetByPlayerGUID(data.PlayerGUID);
        if (tData == null || tData.RewardClaimed)
        {
            session.Send(new PK_HC_TournamentGetRewardResult { Result = 0 });
            return;
        }
        int rank = _tournamentRepo.GetPlayerRank(data.PlayerGUID);
        int rewardSilver = rank > 0 ? Math.Max(1000, 100000 / rank) : 0;
        int rewardGold = rank > 0 ? Math.Max(10, 500 / rank) : 0;
        if (rewardSilver > 0 || rewardGold > 0)
        {
            _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver + rewardSilver, gold: data.Gold + rewardGold);
            _tournamentRepo.ClaimReward(data.PlayerGUID);
            Console.WriteLine($"[HS] Tournament reward: rank {rank}, silver={rewardSilver}, gold={rewardGold}");
            session.Send(new PK_HC_TournamentGetRewardResult { Result = 1 });
            SendMoneyExpByGuid(session, data.PlayerGUID);
        }
        else
        {
            session.Send(new PK_HC_TournamentGetRewardResult { Result = 0 });
        }
    }
    private void HandleBeginLott(GameSession session, PK_CH_BeginLott msg)
    {
        var data = GetPlayerData(session);
        int prizeId = 1;
        if (data != null)
        {
            var tank = _tankRepo.GetPlayerTanks(data.PlayerGUID).FirstOrDefault();
            prizeId = tank?.VehicleId ?? 1;
        }
        // v.fix: раньше отвечали ВСЕГДА одним и тем же nLevl=1 (Тир 2), что бы ни
        // прислал клиент, — теперь эхом отдаём именно тот уровень, который просили
        // (0=Тир1, 1=Тир2, 2=Тир3, см. комментарий у PK_CH_BeginLott).
        session.Send(new PK_HC_BeginLott { nLevl = msg.nLevl, nConfigID = new int[] { 1,2,3,4,5,6,7,8,9,10,11,12 } });
    }
    // ── Главный приз «Золотого запаса» (шкала мощи 0..100) ─────────────────────
    //
    // Клиент шлёт PK_CH_AskGetPowerPrize (140) по клику на танк справа от колеса,
    // сервер отвечает PK_HC_AskGetPowerPrize (223) { byte resultFlag; float powerValue }.
    //
    // ВАЖНО про картинку: КАКОЙ танк там нарисован, сервер не решает. TreasureBox.cs
    // берёт его из клиентской таблицы Table_New_bonusbox_power_reward (строка id=1,
    // поле reward_id) внутри level0 — сейчас там T95/Chieftain. Сервер управляет
    // только тем, что реально выдаётся. Чтобы поменять и картинку, прогоните
    // tools/patch_power_reward.py (правит level0 на FV4030/4).
    private const float PowerPrizeFull = 100f;   // шкала мощи заполнена

    private void HandleAskGetPowerPrize(GameSession session)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        float power = _lotteryPower.TryGetValue(data.PlayerGUID, out float p) ? p : 0f;
        if (power < PowerPrizeFull)
        {
            // Шкала не заполнена — отказ, значение отдаём как есть (клиент перерисует бар).
            session.Send(new PK_HC_AskGetPowerPrize { HasPowerPrize = 0, PowerValue = power });
            Console.WriteLine($"[PowerPrize] Отказ: шкала {power:0.#}/100 у {data.PlayerName}");
            return;
        }

        // Главный приз — РОВНО ОДИН танк: берём строку Table_New_bonusbox
        // с reward_itemtype = 4 и reward_level = 1. Если таких строк несколько,
        // выбирается первая, чьего танка у игрока ещё нет, — так призы чередуются
        // сами и состояние переживает перезапуск (берётся из гаража, не из памяти).
        var mainRows = _gameTables.Bonusboxes.Values.Where(b => b.IsMainPrize)
                                                   .OrderBy(b => b.Id).ToList();
        if (mainRows.Count == 0)
        {
            Console.WriteLine("[PowerPrize] В Table_New_bonusbox нет строки главного приза "
                            + "(reward_itemtype=4, reward_level=1) — выдача невозможна");
            session.Send(new PK_HC_AskGetPowerPrize { HasPowerPrize = 0, PowerValue = power });
            return;
        }

        var ownedIds = _tankRepo.GetPlayerTanks(data.PlayerGUID).Select(t => t.VehicleId).ToHashSet();
        var box = mainRows.FirstOrDefault(b => !ownedIds.Contains(b.RewardItemId)) ?? mainRows[0];

        GrantBonusbox(session, data.PlayerGUID, box);

        _lotteryPower[data.PlayerGUID] = 0f;
        session.Send(new PK_HC_AskGetPowerPrize { HasPowerPrize = 1, PowerValue = 0f });
        Console.WriteLine($"[PowerPrize] Выдан главный приз: строка id={box.Id}, "
                        + $"vehicleId={box.RewardItemId} → {data.PlayerName}");
    }
    // Кнопка «Забрать приз» после того, как AskGetPowerPrize уже выдал танк
    // (HasPowerPrize=1). Клиент (TreasureBoxManager.GetPowerPrizeResult) применяет
    // результат только при Result==1 и ждёт также PowerIndex/PowerValue — раньше
    // сервер всегда слал Result=0 в пакете из одного байта неверного формата,
    // поэтому анимация получения приза никогда не проигрывалась.
    private void HandleGetPowerPrize(GameSession session, PK_CH_GETPOWERPRIZE msg)
    {
        var data = GetPlayerData(session);
        session.Send(new PK_HC_GETPOWERPRIZE { PowerIndex = msg?.PowerID ?? 0, PowerValue = 0, Result = 1 });
    }
    private void HandleGiveUpPrize(GameSession session) => session.Send(new PK_HC_GIVEUP_PRIZE { GiveUpResult = 1 });
    private void HandleReqDomainBidInfo(GameSession session) => session.Send(new PK_HC_RespDomainBidInfo { BidInfoCount = 0 });
    private void HandleDomainFightList(GameSession session) => session.Send(new PK_HC_Domain_FightList { FightCount = 0 });

    // v73: реальное переименование игрока. Раньше сервер НЕ читал NewName из запроса
    // и всегда отвечал «успех» одним битым байтом (см. правку PK_HC_RenameReply выше) —
    // имя нигде не менялось. Теперь: валидируем имя, пишем его в Accounts.PlayerName и
    // отвечаем в РЕАЛЬНОМ формате клиента (Name+LeftTime+ReturnFlag), иначе клиент
    // (см. ReceiveSeverMessage/OnRenameReply в decompile) не примет новое имя в UI.
    private const int RenameNameMinLen = 3;
    private const int RenameNameMaxLen = 16;
    private void HandleRenameRequest(GameSession session, PK_CH_RenameRequest msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        string newName = (msg.NewName ?? string.Empty).Trim();
        bool valid = newName.Length >= RenameNameMinLen
                  && newName.Length <= RenameNameMaxLen
                  && newName.IndexOf(' ') < 0;

        if (!valid)
        {
            Console.WriteLine($"[HS] RenameRequest REJECTED: '{msg.NewName}' (GUID={data.PlayerGUID})");
            // ReturnFlag=0 → клиент просто не примет имя и оставит старое поле ввода.
            session.Send(new PK_HC_RenameReply { Name = data.PlayerName, LeftTime = 0, ReturnFlag = 0 });
            return;
        }

        _accountRepo.UpdatePlayerName(data.PlayerGUID, newName);
        session.PlayerName = newName;
        Console.WriteLine($"[HS] Renamed: GUID={data.PlayerGUID} '{data.PlayerName}' -> '{newName}'");
        session.Send(new PK_HC_RenameReply { Name = newName, LeftTime = 0, ReturnFlag = 1 });
    }
    private void HandleTestAutoGen(GameSession session) => session.Send(new PK_HC_TestAutoGen());
    private void HandleEnterOutNewGuide(GameSession session, PK_CH_EnterOutNewGuideOrBattle msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        Console.WriteLine($"[HS] EnterOutNewGuide: {data.PlayerName}, State={msg.State}");
        if (msg.State == 2)
        {
            int rewardSilver = 5000;
            int rewardGold = 100;
            int rewardExp = 500;
            _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver + rewardSilver, gold: data.Gold + rewardGold, experience: data.Experience + rewardExp);
            _accountRepo.UpdateGuideStates(data.PlayerGUID, AccountRepository.LocalTestGuideStates, AccountRepository.LocalTestMissionData);
            Console.WriteLine($"[HS] Training reward: silver={rewardSilver}, gold={rewardGold}, exp={rewardExp}");
            SendMoneyExpByGuid(session, data.PlayerGUID);
        }
        else if (msg.State == 4)
        {
            // v12: забираем пакет послебоевой статистики, посчитанный RoomServer'ом.
            var res = BattleEndRegistry.Take(data.PlayerGUID);

            // Battle end — grant loyalty to corps member + crew EXP
            var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
            if (corpsGuid.HasValue)
            {
                // v12: лояльность теперь из расчёта боя (урон/фраги/победа), флэт — запасной.
                int loyaltyGain = res?.LoyalPoint ?? 500;
                const int BattleExpPerBattle = 1000;
                _corpsRepo.UpdateMemberLoyalPoint(corpsGuid.Value, data.PlayerGUID, loyaltyGain);
                _corpsRepo.UpdateMemberBattleCap(corpsGuid.Value, data.PlayerGUID,
                    (_corpsRepo.GetMember(corpsGuid.Value, data.PlayerGUID)?.BattleCap ?? 0) + BattleExpPerBattle);
                _corpsRepo.RecalculateCorpsBattleCap(corpsGuid.Value);
                Console.WriteLine($"[HS] Corps loyalty +{loyaltyGain} for {data.PlayerName}");
            }

            // Crew: add EXP to all crew in the player's active tank and level up
            var tanks = _tankRepo.GetPlayerTanks(data.PlayerGUID);
            var activeTank = tanks.FirstOrDefault();   // first tank = currently selected
            if (activeTank != null)
            {
                var tankEntry   = _gameTables.GetTank(activeTank.VehicleId);
                int tier        = tankEntry?.Tier ?? 1;
                int crewExpGain = tier * 120;           // tier 10 → 1200 EXP per battle
                var crew        = _crewRepo.GetPlayerCrew(data.PlayerGUID)
                                           .Where(c => c.TankGuid == activeTank.TankGUID)
                                           .ToList();
                foreach (var member in crew)
                {
                    int newExp   = member.CurExp + crewExpGain;
                    int newLevel = member.CurLv;
                    // Level threshold: level * level * 500  (lv 1→2=500, 2→3=2000, 3→4=4500...)
                    while (newLevel < 20 && newExp >= CrewLevelThreshold(newLevel + 1))
                        newLevel++;
                    _crewRepo.UpdateCrewExp(member.CrewGuid, newExp, newLevel);
                    if (newLevel > member.CurLv)
                        Console.WriteLine($"[HS] Crew {member.CrewId} leveled up {member.CurLv}→{newLevel}");
                }
                if (crew.Count > 0)
                    SendPlayerAllCrew(session, data.PlayerGUID);
            }

            // v13: окно статистики теперь уходит из RoomServer.EndBattle через холл-сессию
            // (только так клиент его показывает). Здесь — лишь запасной путь, если тогда
            // сессии не было.
            if (res != null && !res.SentToClient)
            {
                res.SentToClient = true;
                var teamFallback = BuildTeamResultMessage(res);
                if (teamFallback != null) session.Send(teamFallback);
                session.Send(BuildBattleEndMessage(res));
                Console.WriteLine($"[HS] Battle end info FALLBACK sent to {data.PlayerName}");
            }
            SendMoneyExpByGuid(session, data.PlayerGUID);
        }
    }

    private static int CrewLevelThreshold(int level) =>
        level <= 0 ? 0 : level * level * 500;  // lv 1=500, lv 2=2000, lv 3=4500...
    private void HandleCSNewGuide(GameSession session, PK_CS_NEWGUIDE msg)
    {
        Console.WriteLine($"[HS] CS_NEWGUIDE: guide={msg.guide}, missions={msg.missionId1},{msg.missionId2},{msg.missionId3}");
        var missionData = $"{msg.missionId1},{msg.present1},{msg.missionId2},{msg.present2},{msg.missionId3},{msg.present3}";
        _accountRepo.UpdateGuideStates(session.PlayerGUID, AccountRepository.LocalTestGuideStates, missionData);
    }
    private void HandleCheckHashRuntime(GameSession session)
    {
        Console.WriteLine("[HS] Check hash runtime");
    }
    private void HandleNewRecruitGetReq(GameSession session)
    {
        Console.WriteLine("[HS] New recruit get req");
        session.Send(new PK_HC_NewRecruitActivityInfo());
    }
    private void HandleNewRecruitPushReq(GameSession session)
    {
        Console.WriteLine("[HS] New recruit push req");
        session.Send(new PK_HC_NewRecruitActivityInfo());
    }

    private void HandleSetManualMapID(GameSession session, PK_CH_SetManualMapID msg) =>
        session.Send(new PK_HC_SetManualMapID { MapId = msg.MapId, BattleMode = msg.BattleMode });
    private void HandleGetGlobalCardTankLvl(GameSession session)
    {
        var data = new PK_GlobalCardTankLvlDay[8];
        for (int i = 0; i < 8; i++) data[i] = new PK_GlobalCardTankLvlDay { TankMaxLevel = 0, UnixTimeStamp = 0 };
        session.Send(new PK_HC_GetGlobalCardTankLvl { TankLvl = data });
    }
    private void HandlePersonalEmblemAccess(GameSession session)
    {
        // Клиент (Hall.OnPersonalEmblemAccess) ждёт ответ этим же msgType со
        // списком личных (кастомных) эмблем игрока. Раньше сервер вообще не
        // отвечал — окно выбора личной эмблемы оставалось пустым. Своих
        // кастомных (загружаемых) эмблем эмулятор не хранит — отвечаем пустым
        // валидным списком, чтобы клиент корректно открыл окно вместо зависания.
        session.Send(new PK_CH_PersonalEmblemAccess());
    }

    private void HandleBuyTankEmblem(GameSession session, PK_CH_BuyTankEmblem msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        var tank = _tankRepo.GetPlayerTanks(data.PlayerGUID).FirstOrDefault(t => t.VehicleId == msg.TankID);
        if (tank == null || msg.EmblemSlot < 0 || msg.EmblemSlot >= 4)
        {
            session.Send(new PK_HC_BuyTankEmblem { SucessFlag = 0 });
            return;
        }

        if (data.Silver < EmblemPriceSilver)
        {
            SendMoneyExpByGuid(session, data.PlayerGUID);
            session.Send(new PK_HC_BuyTankEmblem { SucessFlag = 0 });
            return;
        }

        _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver - EmblemPriceSilver);
        long expireTime = FarFutureUnixTime();
        _tankRepo.UpdateTankEmblem(tank.TankGUID, tank, msg.EmblemSlot, msg.EmblemID, expireTime);
        SendMoneyExpByGuid(session, data.PlayerGUID);

        // ВАЖНО: клиент (Player.OnSynTankEmblemInfo) применяет эмблему к танку
        // именно из полей этого ответа — без него UI ничего не покажет.
        session.Send(new PK_HC_BuyTankEmblem
        {
            SucessFlag = 1,
            PriceId = msg.PriceType,
            EmblemSlot = msg.EmblemSlot,
            TankID = msg.TankID,
            EmblemID = msg.EmblemID,
            EmblemTime = expireTime
        });
        Console.WriteLine($"[HS] BuyTankEmblem: {data.PlayerName} tank={msg.TankID} slot={msg.EmblemSlot} emblem={msg.EmblemID}");
    }

    private void HandleDropTankEmblem(GameSession session, PK_CH_DropTankEmblem msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        var tank = _tankRepo.GetPlayerTanks(data.PlayerGUID).FirstOrDefault(t => t.VehicleId == msg.TankID);
        if (tank == null || msg.EmblemSlot < 0 || msg.EmblemSlot >= 4) return;

        _tankRepo.UpdateTankEmblem(tank.TankGUID, tank, msg.EmblemSlot, 0, 0);
        // Клиент снимает эмблему сам, локально, сразу после отправки запроса —
        // подтверждение сервера ему не требуется (нет клиентского обработчика
        // ответа для этого msgType), важно лишь реально сохранить снятие в БД,
        // чтобы оно не вернулось после следующего логина.
        Console.WriteLine($"[HS] DropTankEmblem: {data.PlayerName} tank={msg.TankID} slot={msg.EmblemSlot}");
    }
    private void HandleUseOffer(GameSession session, PK_CH_UseOffer msg) => session.Send(new PK_HC_UseOfferStatus { OfferId = msg.OfferId, UseResult = 1 });

    // v.fix (Тир): раньше просто отвечал BuyResult=1 ничего не делая — деньги не
    // списывались, выстрелы не начислялись, поэтому "Осталось выстрелов" всегда
    // оставалось 0 и купить ничего было нельзя по факту. Цена берётся из клиентского
    // локального конфига (ReadConfig["TreasureBox"]["TreasureBoxCost0N"], видно в
    // decfilesgame/NewTreasureBoxUpPanel.BuyBox — списывается ЗОЛОТО). Значение "60"
    // для уровня 1 подтверждено скриншотом игрока; для 2/3 используем то же значение
    // по умолчанию — поправьте под реальные цифры из своего client_config, если они
    // отличаются.
    // v.fix: ключи 0/1/2 (не 1/2/3!) — см. развёрнутый комментарий в
    // AccountRepository.GetShootTimes про 0-based enum TreasureBoxLevel клиента.
    // 0=Тир1, 1=Тир2, 2=Тир3. Значение "60" для Тира 1 подтверждено скриншотом
    // игрока; для Тир2/3 используем то же по умолчанию — поправьте под реальные
    // цифры из своего client_config, если они отличаются.
    private static readonly Dictionary<byte, int> TreasureBoxCostGold = new()
    {
        { 0, 60 },
        { 1, 60 },
        { 2, 60 },
    };
    // Сколько выстрелов начисляется за одну покупку. В клиенте это не передаётся —
    // сервер сам решает; 1 — самое безопасное значение по умолчанию.
    private const int TreasureBoxShotsPerPurchase = 1;

    private void HandleBuyBox(GameSession session, PK_CH_BuyBox msg)
    {
        var data = GetPlayerData(session);
        if (data == null) { session.Send(new PK_HC_BuyBox { Level = msg.Level, BuyResult = 0 }); return; }

        int cost = TreasureBoxCostGold.TryGetValue(msg.Level, out int c) ? c : 60;
        if (data.Gold < cost)
        {
            session.Send(new PK_HC_BuyBox { Level = msg.Level, BuyResult = 0 });
            return;
        }

        _accountRepo.UpdateResources(data.PlayerGUID, gold: data.Gold - cost);
        int newCount = _accountRepo.AddShootTimes(data.PlayerGUID, msg.Level, TreasureBoxShotsPerPurchase);

        session.Send(new PK_HC_BuyBox { Level = msg.Level, BuyResult = 1 });
        SendMoneyExpByGuid(session, data.PlayerGUID);
        SendRemainShootTimes(session, data.PlayerGUID);
        Console.WriteLine($"[HS] BuyBox: {data.PlayerName} уровень={msg.Level}, цена={cost} золота, теперь выстрелов={newCount}");
    }

    /// <summary>Шлёт клиенту актуальное число оставшихся выстрелов Тира по всем 3 уровням.</summary>
    private void SendRemainShootTimes(GameSession session, long playerGuid)
    {
        // v.fix: Level ДОЛЖЕН быть 0/1/2 — клиент кастует байт прямо в enum
        // TreasureBoxLevel{One=0,Two=1,Three=2}; значение 3 (как было раньше)
        // не совпадает ни с одним case → GetTreasureBoxDataByLevel возвращает null →
        // NullReferenceException на КАЖДОМ таком пакете (см. output_log.txt).
        session.Send(new PK_HC_RemainShootTimes
        {
            Items = new List<RemainShootTimesItem>
            {
                new() { Level = 0, LeftTimes = _accountRepo.GetShootTimes(playerGuid, 0) },
                new() { Level = 1, LeftTimes = _accountRepo.GetShootTimes(playerGuid, 1) },
                new() { Level = 2, LeftTimes = _accountRepo.GetShootTimes(playerGuid, 2) },
            }
        });
    }

    private void HandleOpenBox(GameSession session, PK_CH_OPENBOX msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        // Раньше этот пакет вообще не заполнялся (PK_HC_OPENBOX{BoxId, OpenResult}),
        // а клиент (TreasureBoxManager.GetOpenBoxResult) именно из ЭТОГО пакета
        // формирует TreasureRewarditems — GETBOXPRIZE их только подтверждает.
        var box = PickBonusbox();
        if (box == null)
        {
            session.Send(new PK_HC_OPENBOX { nBtnTxtStata = (int)0 /* NONE */, nPrizeNum = 0 });
            return;
        }

        GrantBonusbox(session, data.PlayerGUID, box);
        SendMoneyExpByGuid(session, data.PlayerGUID);

        session.Send(new PK_HC_OPENBOX
        {
            powerValue = 0,
            IsShine = 0,
            nBtnTxtStata = 3, // TreasureBtnState.GET — открыт, можно забрать
            nTotalValue = Math.Max(box.MinValue, box.MaxValue),
            Price = 0,
            nPrizeNum = 1,
            rewards = new[] { new TreasureRewardItemDto { PrizeID = box.Id, IsGiveUp = 0 } }
        });
        Console.WriteLine($"[HS] OpenBox: {data.PlayerName} открыл сундук, приз id={box.Id} (type={box.RewardItemType})");
    }

    private void HandleGetBoxPrize(GameSession session, PK_CH_GETBOXPRIZE msg)
    {
        // Клиент раздаёт награды из TreasureRewarditems, заполненных предыдущим
        // OPENBOX — этот пакет только подтверждает клик по кнопке «Забрать».
        session.Send(new PK_HC_GETBOXPRIZE { result = 1 });
    }
    private void HandleGetChouJiang(GameSession session) => session.Send(new PK_HC_ChouJiang { PrizeId = 0 });
    private void HandleChouJiangRun(GameSession session) => session.Send(new PK_HC_ChouJiangRunRes { PrizeId = _random.Next(1, 50) });
    private void HandleGetTaskReward(GameSession session)
    {
        Console.WriteLine("[HS] Get task reward");
        session.Send(new PK_HC_TaskData { MissionID = 0, Schedule = 0, CompleteTag = 2 });
    }
    private void HandleGetMonthlyLoginAward(GameSession session)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var resp = new PK_HC_Update_MonthlyLoginAward_GetInfo { AwardNum = 0, AwardState = Array.Empty<byte>() };
        session.Send(resp);
    }
    private void HandleSignInMonthlyLoginAward(GameSession session)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        int newDay = data.MonthlyLoginDay + 1;
        if (newDay > 30) newDay = 1;
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE Accounts SET MonthlyLoginDay = @day WHERE PlayerGUID = @guid";
        cmd.Parameters.AddWithValue("@day", newDay);
        cmd.Parameters.AddWithValue("@guid", data.PlayerGUID);
        cmd.ExecuteNonQuery();
        int rewardSilver = newDay * 1000;
        int rewardGold = newDay >= 10 ? 50 : (newDay >= 5 ? 20 : 0);
        _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver + rewardSilver, gold: data.Gold + rewardGold);
        var now = DateTime.Now;
        var resp = new PK_HC_Update_MonthlyLoginAward_SignInfo
        {
            Year = (short)now.Year, Month = (byte)now.Month, Date = (byte)now.Day,
            IsSignIn = 1, BoomFactor = 1, SignInNum = 1,
            SignInList = new byte[] { (byte)now.Day }
        };
        session.Send(resp);
        Console.WriteLine($"[HS] Sign in monthly: day {newDay}, silver={rewardSilver}, gold={rewardGold}");
        SendMoneyExpByGuid(session, data.PlayerGUID);
    }
    private void HandleAskGetPayTask(GameSession session) => session.Send(new PK_HC_AskGetPayTask());

    // v.fix: раньше всегда отвечал HasReward=0 — награда за скачивание/первый вход
    // не начислялась НИКОГДА, сколько бы игрок ни жал кнопку "Получить".
    // Теперь: первый клик — начисляем серебро и помечаем как забранное; повторные
    // клики (кнопка может слаться клиентом многократно, см. server.log) — просто
    // молча отвечаем HasReward=0, ничего не начисляя повторно.
    private const int StandaloneLoginRewardSilver = 100_000;
    private void HandleAskStandaloneLoginReward(GameSession session)
    {
        var data = GetPlayerData(session);
        if (data == null) { session.Send(new PK_HC_AskStandaloneLoginReward { HasReward = 0 }); return; }

        if (_accountRepo.IsStandaloneRewardClaimed(data.PlayerGUID))
        {
            session.Send(new PK_HC_AskStandaloneLoginReward { HasReward = 0 });
            return;
        }

        _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver + StandaloneLoginRewardSilver);
        _accountRepo.MarkStandaloneRewardClaimed(data.PlayerGUID);
        Console.WriteLine($"[HS] StandaloneLoginReward: выдано {StandaloneLoginRewardSilver} серебра, GUID={data.PlayerGUID}");
        session.Send(new PK_HC_AskStandaloneLoginReward { HasReward = 1 });
        SendMoneyExpByGuid(session, data.PlayerGUID);
    }

    private void HandleExchangeTankExp(GameSession session)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        int expToSilver = data.Experience / 100;
        if (expToSilver > 0)
        {
            _accountRepo.UpdateResources(data.PlayerGUID, experience: 0, silver: data.Silver + expToSilver);
            Console.WriteLine($"[HS] Exchange tank exp: {expToSilver} silver");
            SendMoneyExpByGuid(session, data.PlayerGUID);
        }
    }
    private void HandleAskBonusBoxData(GameSession session)
    {
        Console.WriteLine("[HS] Ask bonus box data");
    }

    private void HandleChannel(GameSession session, PK_CH_CHANNEL msg) => session.Send(new PK_HC_Channel_Res { ChannelId = msg.ChannelId });
    private void HandleCorpsCreate(GameSession session, PK_CHW_CORPS_CREATE msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        if (_corpsRepo.GetByName(msg.CorpsName) != null) return;
        if (_corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID).HasValue) return; // already in corps
        long corpsGuid = _random.Next(100000, 999999);
        var corps = _corpsRepo.CreateCorps(corpsGuid, msg.CorpsName, msg.CorpsTag ?? "", data.PlayerGUID, data.PlayerName);
        // Give creator 1,000,000 loyalty so they can immediately research any tank
        _corpsRepo.UpdateMemberLoyalPoint(corpsGuid, data.PlayerGUID, 1_000_000);
        var pkt = BuildCorpsDataPacket(corps, 2, 1, null, corpsGuid);
        session.Send(pkt);
        Console.WriteLine($"[HS] Corps created: {msg.CorpsName} by {data.PlayerName}");
    }

    private PK_WHC_CorpsData BuildCorpsDataPacket(Corps? corps, byte myJob, int memberCount,
        CorpsApplication? pendingApp, long corpsGuidOverride = 0, int fealtyPoint = 0)
    {
        if (corps == null)
            return new PK_WHC_CorpsData { TroopsID = 0 };

        Corps? pendingCorps = pendingApp != null ? _corpsRepo.GetByGUID(pendingApp.CorpsGUID) : null;

        return new PK_WHC_CorpsData
        {
            TroopsID          = corps.CorpsGUID,
            TroopsName        = corps.CorpsName,
            TroopsTag         = corps.CorpsTag ?? "",
            TroopsLevel       = (ushort)corps.CorpsLevel,
            TroopsNotice      = corps.Notice ?? "",
            TroopsIntroduce   = corps.Introduce ?? "",
            TroopsCreateTime  = corps.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            TroopsState       = corps.State,
            AddMaxMemberCount = corps.MaxMemberCount,
            GoldMoney         = (long)corps.Money,
            SilverMoney       = 0,
            TaxRate           = 0,
            ConvertRate       = 100,
            ExternMulti       = 1,
            MaxMemSize        = corps.MaxMemberCount,
            PlayerNum         = (ushort)memberCount,
            LeaderName        = corps.LeaderName,
            MyJob             = myJob,
            FealtyPoint       = fealtyPoint,  // player's current loyalty
            JoinTroopsID      = pendingApp?.CorpsGUID ?? 0,
            JoinTroopsName    = pendingCorps?.CorpsName ?? "",
        };
    }

    private void HandleCorpsQuit(GameSession session, PK_CHW_CORPS_QUIT msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (corpsGuid.HasValue) _corpsRepo.RemoveMember(corpsGuid.Value, data.PlayerGUID);
    }

    private void HandleCorpsDismiss(GameSession session, PK_CHW_CORPS_DISMISS msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (corpsGuid.HasValue) _corpsRepo.DisbandCorps(corpsGuid.Value);
        session.Send(new PK_WHC_CorpsData { TroopsID = 0 });
    }

    private void HandleCorpsModIntroduce(GameSession session, PK_CHW_CORPS_MOD_INTRODUCE msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) return;
        var member = _corpsRepo.GetMember(corpsGuid.Value, data.PlayerGUID);
        if (member == null || member.Job < 1) return;
        _corpsRepo.UpdateCorpsIntroduce(corpsGuid.Value, msg.Introduce);
    }

    private void HandleCorpsModTipName(GameSession session, PK_CH_CORPS_MOD_TIPNAME msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) return;
        var member = _corpsRepo.GetMember(corpsGuid.Value, data.PlayerGUID);
        if (member == null || member.Job < 1) return;
        _corpsRepo.UpdateCorpsTipName(corpsGuid.Value, msg.TipName);
    }

    private void HandleCorpsModName(GameSession session, PK_CH_CORPS_MOD_NAME msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) return;
        var member = _corpsRepo.GetMember(corpsGuid.Value, data.PlayerGUID);
        if (member == null || member.Job < 2) return;
        _corpsRepo.UpdateCorpsName(corpsGuid.Value, msg.CorpsName);
    }
    private void HandleCorpsApplicationReq(GameSession session, PK_CHW_CORPS_APPLICATION_REQ msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        // Can't apply if already in a corps
        if (_corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID).HasValue) return;
        // Target corps must actually exist in DB (otherwise FK fails)
        var corps = _corpsRepo.GetByGUID(msg.CorpsId);
        if (corps == null) return;
        // Cancel any existing applications first to avoid UNIQUE / FK conflicts
        _corpsRepo.RemoveAllPlayerApplications(data.PlayerGUID);
        _corpsRepo.AddApplication(msg.CorpsId, data.PlayerGUID, data.PlayerName);
        // Confirm application to client
        session.Send(new PK_HC_Corps_Application_Res { CorpsGuid = msg.CorpsId, Flag = 1 });
        Console.WriteLine($"[HS] Corps application: {data.PlayerName} -> {corps.CorpsName}");
    }

    private void HandleCorpsApplicationRep(GameSession session, PK_CHW_CORPS_APPLICATION_REP msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) return;
        var member = _corpsRepo.GetMember(corpsGuid.Value, data.PlayerGUID);
        if (member == null || member.Job < 1) return;

        if (msg.AcceptState == 1)
        {
            var corps = _corpsRepo.GetByGUID(corpsGuid.Value);
            if (corps != null && _corpsRepo.GetMemberCount(corpsGuid.Value) < corps.MaxMemberCount)
            {
                var applicant = _accountRepo.GetByPlayerGUID(msg.ApplicantGuid);
                if (applicant != null)
                {
                    _corpsRepo.AddMember(corpsGuid.Value, msg.ApplicantGuid, applicant.PlayerName, 0);
                    _corpsRepo.RecalculateCorpsBattleCap(corpsGuid.Value);
                }
            }
        }
        _corpsRepo.RemoveApplication(corpsGuid.Value, msg.ApplicantGuid);
    }

    private void HandleCorpsSearch(GameSession session, PK_CHW_CORPS_SEARCH msg)
    {
        var results = string.IsNullOrWhiteSpace(msg.SearchName)
            ? _corpsRepo.GetAllCorps(20)
            : _corpsRepo.SearchCorps(msg.SearchName);
        var list = new PK_WHC_CORPS_LIST
        {
            CorpsTotalCount = results.Count, CurrentPageCount = (ushort)results.Count,
            CurrentPageNumber = results.Count, CorpsList = new CorpsInfo[results.Count]
        };
        for (int i = 0; i < results.Count; i++)
        {
            var c = results[i];
            list.CorpsList[i] = new CorpsInfo
            {
                CorpsID = c.CorpsGUID, CorpsName = c.CorpsName, CorpsLevel = c.CorpsLevel,
                PlayerCount = (ushort)_corpsRepo.GetMemberCount(c.CorpsGUID),
                Notice = c.Notice, Introduce = c.Introduce,
                CreateTime = c.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                State = c.State, LeaderName = c.LeaderName,
                MaxMemberCount = c.MaxMemberCount, BattleCap = c.BattleCap,
                MaxMemSize = c.MaxMemberCount
            };
        }
        session.Send(list);
    }

    private void HandleCorpsQuery(GameSession session, PK_CHW_CORPS_QUERY msg)
    {
        var corps = _corpsRepo.GetByGUID(msg.CorpsId);
        if (corps == null) return;
        var list = new PK_WHC_CORPS_LIST { CorpsTotalCount = 1, CurrentPageCount = 1, CurrentPageNumber = 1, CorpsList = new CorpsInfo[1] };
        list.CorpsList[0] = new CorpsInfo
        {
            CorpsID = corps.CorpsGUID, CorpsName = corps.CorpsName, CorpsLevel = corps.CorpsLevel,
            PlayerCount = (ushort)_corpsRepo.GetMemberCount(corps.CorpsGUID),
            Notice = corps.Notice, Introduce = corps.Introduce,
            CreateTime = corps.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            State = corps.State, LeaderName = corps.LeaderName,
            MaxMemberCount = corps.MaxMemberCount, BattleCap = corps.BattleCap,
            MaxMemSize = corps.MaxMemberCount
        };
        session.Send(list);
    }

    private void HandleCorpsData(GameSession session)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) { session.Send(new PK_WHC_CorpsData { TroopsID = 0 }); return; }
        var corps   = _corpsRepo.GetByGUID(corpsGuid.Value);
        var member  = _corpsRepo.GetMember(corpsGuid.Value, data.PlayerGUID);
        var app     = _corpsRepo.GetPlayerApplication(data.PlayerGUID);
        int count   = _corpsRepo.GetMemberCount(corpsGuid.Value);
        session.Send(BuildCorpsDataPacket(corps, member?.Job ?? 0, count, app, corpsGuid.Value,
            fealtyPoint: member?.LoyalPoint ?? 0));
    }

    private void HandleCorpsMemberReq(GameSession session, PK_CHW_CORPS_MEMBER_REQ msg)
    {
        var members = _corpsRepo.GetMembers(msg.CorpsId);
        int pageSize = 20; int skip = msg.Bias;
        var pageMems = members.Skip(skip).Take(pageSize).ToList();
        var res = new PK_WHC_CORPS_MEMBER
        {
            GUID = msg.CorpsId, MemberNumber = members.Count,
            CurrentPageCount = (short)pageMems.Count, PageType = msg.PageType,
            PageNumber = pageMems.Count, Members = new CorpsMemberInfo[pageMems.Count]
        };
        for (int i = 0; i < pageMems.Count; i++)
        {
            res.Members[i] = new CorpsMemberInfo
            {
                CorpsID = pageMems[i].CorpsGUID, UserGUID = pageMems[i].PlayerGUID,
                Job = pageMems[i].Job, UserName = pageMems[i].PlayerName,
                LoyalPoint = pageMems[i].LoyalPoint, BattleCap = pageMems[i].BattleCap,
                JoinDate = pageMems[i].JoinDate.ToString("yyyy-MM-dd HH:mm:ss"),
                LastLoginDate = pageMems[i].LastLoginDate.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }
        session.Send(res);
    }

    private void HandleCorpsCommission(GameSession session, PK_CHW_CORPS_COMMISSION msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) return;
        var member = _corpsRepo.GetMember(corpsGuid.Value, data.PlayerGUID);
        if (member == null || member.Job < 2) return;
        _corpsRepo.UpdateMemberJob(corpsGuid.Value, msg.MemberGuid, msg.NewJob);
    }

    private void HandleCorpsExpel(GameSession session, PK_CHW_CORPS_EXPEL msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) return;
        var member = _corpsRepo.GetMember(corpsGuid.Value, data.PlayerGUID);
        if (member == null || member.Job < 1) return;
        if (msg.MemberGuid == data.PlayerGUID) return;
        _corpsRepo.ExpelMember(corpsGuid.Value, msg.MemberGuid);
        _corpsRepo.RecalculateCorpsBattleCap(corpsGuid.Value);
    }

    private void HandleCorpsClose(GameSession session, PK_CHW_CORPS_CLOSE msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) return;
        var member = _corpsRepo.GetMember(corpsGuid.Value, data.PlayerGUID);
        if (member == null || member.Job < 2) return;
        var corps = _corpsRepo.GetByGUID(corpsGuid.Value);
        if (corps != null)
        {
            byte newState = (byte)(corps.State == 0 ? 1 : 0);
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "UPDATE Corps SET State = @state WHERE CorpsGUID = @cg";
            cmd.Parameters.AddWithValue("@state", newState);
            cmd.Parameters.AddWithValue("@cg", corpsGuid.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private void HandleCorpsInviteRep(GameSession session, PK_CHW_CORPS_INVITE_REP msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        if (msg.AcceptState == 1)
        {
            var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
            if (!corpsGuid.HasValue) return;
            var corps = _corpsRepo.GetByGUID(corpsGuid.Value);
            if (corps == null || _corpsRepo.GetMemberCount(corpsGuid.Value) >= corps.MaxMemberCount) return;
            Console.WriteLine($"[HS] Corps invite accepted by {data.PlayerName}");
        }
    }

    private void HandleCorpsAssignment(GameSession session, PK_CHW_CORPS_ASSIGNMENT msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) return;
        var member = _corpsRepo.GetMember(corpsGuid.Value, data.PlayerGUID);
        if (member == null || member.Job < 1) return;
        Console.WriteLine("[HS] Corps assignment");
    }

    private void HandleCorpsModTaxRate(GameSession session, PK_CHW_Corps_Mod_TaxRate msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) return;
        var member = _corpsRepo.GetMember(corpsGuid.Value, data.PlayerGUID);
        if (member == null || member.Job < 2) return;
        if (msg.TaxRate > 30) return;
        _corpsRepo.UpdateCorpsTaxRate(corpsGuid.Value, msg.TaxRate);
    }

    private void HandleCorpsConvertMoney(GameSession session, PK_CHW_Corps_ConvertMoney msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue || msg.ConvertTimes <= 0) return;

        // Exchange rates (per 1 ConvertTimes):
        //   Gold:   150 gold  → 10,000 loyalty
        //   Silver: 90,000 silver → 10,000 loyalty
        const int GoldCostPerConvert   = 150;
        const int SilverCostPerConvert = 90_000;
        const int LoyaltyPerConvert    = 10_000;

        int times    = Math.Max(1, msg.ConvertTimes);
        int totalLoyalty = LoyaltyPerConvert * times;
        bool success = false;

        if (msg.Type == 0) // Gold
        {
            int totalCost = GoldCostPerConvert * times;
            if (data.Gold >= totalCost)
            {
                _accountRepo.UpdateResources(data.PlayerGUID, gold: data.Gold - totalCost);
                success = true;
            }
        }
        else // Silver
        {
            int totalCost = SilverCostPerConvert * times;
            if (data.Silver >= totalCost)
            {
                _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver - totalCost);
                success = true;
            }
        }

        if (!success)
        {
            session.Send(new PK_WHC_Corps_ConvertMoney_Res { PlayerGuid = data.PlayerGUID, RetFlag = 0 });
            return;
        }

        _corpsRepo.UpdateMemberLoyalPoint(corpsGuid.Value, data.PlayerGUID, totalLoyalty);
        var member = _corpsRepo.GetMember(corpsGuid.Value, data.PlayerGUID);
        int newLoyalty = member?.LoyalPoint ?? 0;

        session.Send(new PK_WHC_Corps_ConvertMoney_Res { PlayerGuid = data.PlayerGUID, RetFlag = 1 });
        // Refresh corps data so the client sees the updated loyalty points
        var corps = _corpsRepo.GetByGUID(corpsGuid.Value);
        var updatedMember = _corpsRepo.GetMember(corpsGuid.Value, data.PlayerGUID);
        session.Send(BuildCorpsDataPacket(corps, updatedMember?.Job ?? 0,
            _corpsRepo.GetMemberCount(corpsGuid.Value), null, corpsGuid.Value));
        SendMoneyExpByGuid(session, data.PlayerGUID);
        Console.WriteLine($"[HS] CorpsConvert: {data.PlayerName} type={msg.Type} x{times} → loyalty+{totalLoyalty} (total={newLoyalty})");
    }

    private void HandleCorpsMoneyChangeList(GameSession session)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        session.Send(new PK_HC_Corps_MoneyChangeList
        {
            CorpsId = corpsGuid ?? 0,
            PlayerGuid = data.PlayerGUID
        });
    }

    private void HandleCorpsMoneyChangeList2(GameSession session)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        session.Send(new PK_HC_Corps_MoneyChangeList2
        {
            CorpsId = corpsGuid ?? 0,
            PlayerGuid = data.PlayerGUID
        });
    }
    private void HandleCorpsAthInvite(GameSession session, PK_CHW_CorpsAth_Invite msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) return;
        Console.WriteLine($"[HS] Corps tournament invite: {data.PlayerName}");
    }

    private void HandleCorpsAthAllowJoinAll(GameSession session, PK_CHM_CorpsAth_AllowJoinAll msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) return;
        var member = _corpsRepo.GetMember(corpsGuid.Value, data.PlayerGUID);
        if (member == null || member.Job < 1) return;
    }

    private void HandleCorpsAthAllowQuit(GameSession session, PK_CHM_CorpsAth_AllowQuit msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) return;
        var member = _corpsRepo.GetMember(corpsGuid.Value, data.PlayerGUID);
        if (member == null || member.Job < 1) return;
    }

    private void HandleCorpsAthRankList(GameSession session)
    {
        // Send empty ranking list
        session.Send(new PK_HC_CorpsAth_RankList_Res());
    }

    private void HandleCorpsTankListReq(GameSession session)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        // Get the player's current owned tanks to build the study-state list.
        // The DISPLAY of available tanks (tier, costs, etc.) comes from the client's
        // built-in game resources. The server only sends WHO has studied WHAT.
        var ownedIds = new HashSet<int>(_tankRepo.GetPlayerTanks(data.PlayerGUID).Select(t => t.VehicleId));

        var entries = new List<PK_HC_Corps_TankList.StudyEntry>();
        for (int i = 0; i < LegionTanks.Length; i++)
        {
            var lt = LegionTanks[i];
            int corpsTankId = i + 1;
            bool owned = ownedIds.Contains(lt.TankId);
            // Include ALL tanks — this resets stale client-cached "Studying (2)" states.
            // Owned → StudyFinish (4): permanently done, no timer.
            // Not owned → NoStudy (1): shows "Изучить" button, overrides any stale timer.
            entries.Add(new PK_HC_Corps_TankList.StudyEntry(
                corpsTankId,
                PlayerTankGuid: owned ? 1L : 0L,
                PlayerGuid: owned ? data.PlayerGUID : 0L,
                TimeType: 0,
                StudyState: owned ? (byte)4 : (byte)1,
                PassStudyTime: 0L
            ));
        }

        session.Send(new PK_HC_Corps_TankList { Entries = entries });

        // Also send the PrevTankList (exp from owned tanks) — needed for the research dialog
        var prevList = new PK_HC_Corps_PrevTankList();
        // Empty list is fine; client uses free exp (FealtyPoint) from CorpsData
        session.Send(prevList);

        Console.WriteLine($"[HS] Corps tank list sent: {entries.Count} studied tanks for {data.PlayerName}");
    }

    private void HandleCorpsAthMyRankList(GameSession session)
    {
        // My rank within corps — send empty list (no tournament active)
        session.Send(new PK_HC_CorpsAth_RankList_Res());
    }

    private void HandleCorpsAthAwardList(GameSession session)
    {
        session.Send(new PK_HC_CorpsAth_AwardList_Res());
    }

    private void HandleCorpsIntelligenceList(GameSession session)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        int corpsId = corpsGuid.HasValue ? (int)corpsGuid.Value : 0;
        session.Send(new PK_HC_Corps_IntelligenceList { CorpsId = corpsId });
    }

    private void HandleCorpsPrevTankList(GameSession session)
    {
        session.Send(new PK_HC_Corps_PrevTankList());
    }

    private void HandleCorpsAthGetAward(GameSession session)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) return;
        var corps = _corpsRepo.GetByGUID(corpsGuid.Value);
        if (corps == null) return;
        int awardAmount = corps.BattleCap > 0 ? (int)(corps.BattleCap / 100) : 0;
        if (awardAmount > 0 && corps.Money >= awardAmount)
        {
            _corpsRepo.UpdateCorpsMoney(corpsGuid.Value, corps.Money - awardAmount);
            _accountRepo.UpdateResources(data.PlayerGUID, silver: data.Silver + awardAmount);
            Console.WriteLine($"[HS] Corps award: {awardAmount} silver");
            SendMoneyExpByGuid(session, data.PlayerGUID);
        }
    }

    private void SendGarageRefresh(GameSession session, long playerGuid)
    {
        // Reload and resend all garage data after a tank is added or modified.
        Task.Run(async () =>
        {
            await Task.Delay(300); // let client process prior packets first
            var tanks = _tankRepo.GetPlayerTanks(playerGuid);
            if (tanks.Count == 0) return;
            var tankDataList = tanks.Select(t => ConvertTankToTankData(t)).ToArray();
            session.Send(new PK_HC_TANKINFO { TankTotalCount = (byte)tankDataList.Length, TankDatas = tankDataList });
            var crewByTank = BuildCrewLevelLookup(playerGuid);
            var tankAttrs = tanks.Select(t => GenerateTankAttributes(t, crewByTank)).ToArray();
            session.Send(new PK_HC_AllTankAttribute { TankCount = (byte)tankAttrs.Length, TankAttributes = tankAttrs });
            SendResearchState(session, playerGuid);
            SendPlayerAllCrew(session, playerGuid);
            SendTankEquipment(session, tanks);
            SendMoneyExpByGuid(session, playerGuid);
        });
    }


    private void HandleCorpsDevelopTank(GameSession session, PK_CH_Corps_DevelopTank msg)
    {
        var data = GetPlayerData(session);
        if (data == null)
        {
            session.Send(new PK_HC_Corps_StudyTankRes { CorpsTankId = msg.TankId, RetFlag = 0 });
            return;
        }

        Console.WriteLine($"[HS] Corps develop tank: TankId={msg.TankId}, GUID={data.PlayerGUID}");

        // Client sends CorpsTankId = 1-based index into LegionTanks array.
        // We must NOT look up by VehicleId — the client doesn't send vehicle IDs here.
        int corpsTankIndex = msg.TankId - 1;
        if (corpsTankIndex < 0 || corpsTankIndex >= LegionTanks.Length)
        {
            Console.WriteLine($"[HS] Corps develop tank: CorpsTankId {msg.TankId} out of range");
            session.Send(new PK_HC_Corps_StudyTankRes { CorpsTankId = msg.TankId, RetFlag = 0 });
            return;
        }
        var legionEntry = LegionTanks[corpsTankIndex];

        // Must be in a corps
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue)
        {
            session.Send(new PK_HC_Corps_StudyTankRes { CorpsTankId = msg.TankId, RetFlag = 0 });
            return;
        }

        // v46: изучение танка легиона — БЕСПЛАТНО и МГНОВЕННО (по просьбе). Раньше здесь
        // стояли гейты по лояльности и свободному опыту — сняты.
        // КРИТИЧНО: реальный vehicleId берём из legionEntry.TankId, а НЕ из msg.TankId.
        // msg.TankId — это 1-based индекс CorpsTankId (1..12), а не id машины. Старый код
        // подставлял его в GetTank/CreateTank/проверку владения → выдавались СОВЕРШЕННО
        // ДРУГИЕ танки (жалоба: «изучаются сразу, но выдаются другие танки»).
        int realVehicleId = legionEntry.TankId;

        // Уже есть — не выдаём дубликат (сверяем по реальному vehicleId).
        var existingTanks = _tankRepo.GetPlayerTanks(data.PlayerGUID);
        if (existingTanks.Any(t => t.VehicleId == realVehicleId))
        {
            Console.WriteLine($"[HS] Corps develop tank: уже есть vehicleId={realVehicleId} (CorpsTankId={msg.TankId})");
            session.Send(new PK_HC_Corps_StudyTankRes { CorpsTankId = msg.TankId, RetFlag = 1 });
            return;
        }

        var tankEntry = _gameTables.GetTank(realVehicleId);
        if (tankEntry == null)
        {
            Console.WriteLine($"[HS] Corps develop tank: нет в таблице vehicleId={realVehicleId} (CorpsTankId={msg.TankId})");
            session.Send(new PK_HC_Corps_StudyTankRes { CorpsTankId = msg.TankId, RetFlag = 0 });
            return;
        }

        var newTank = _tankRepo.CreateTank(data.PlayerGUID, realVehicleId,
            tankEntry.HullId, tankEntry.UnderpanId, tankEntry.TurretId,
            tankEntry.MainGunId, tankEntry.EngineId, tankEntry.FueltankId,
            tankEntry.CartridgeId, tankEntry.RadioStationId, tankEntry.GaugeId,
            tankEntry.MaxHp);
        _moduleRepo?.ResearchTankBaseModules(data.PlayerGUID, realVehicleId,
            tankEntry.HullId, tankEntry.UnderpanId, tankEntry.TurretId,
            tankEntry.MainGunId, tankEntry.EngineId, tankEntry.FueltankId,
            tankEntry.CartridgeId, tankEntry.RadioStationId, tankEntry.GaugeId);

        Console.WriteLine($"[HS] Corps develop tank: выдан {tankEntry.Name} " +
                          $"(CorpsTankId={msg.TankId} → vehicleId={realVehicleId}, IsTimeTank={msg.IsTimeTank}) бесплатно/мгновенно");

        if (msg.IsTimeTank == 1)
        {
            // 7-day option selected. Use 3-packet chain to bypass the timer entirely:
            // 1. StudyTankRes {TimeType=1} → client: NoStudy→Studying (shows timer for a frame)
            session.Send(new PK_HC_Corps_StudyTankRes { CorpsTankId = msg.TankId, TroopsTankTimeTypeRaw = 1, RetFlag = 1 });
            // 2. FastDevelop_Res {RetFlag=1} → client: Studying→WaitForPick (removes timer)
            session.Send(new PK_HC_Corps_SpeedStudyTankRes { CorpsTankId = msg.TankId, RetFlag = 1 });
            // 3. StudyTankRes {TimeType=0=Forever} → client: WaitForPick→StudyFinish (permanent)
            session.Send(new PK_HC_Corps_StudyTankRes { CorpsTankId = msg.TankId, TroopsTankTimeTypeRaw = 0, RetFlag = 1 });
        }
        else
        {
            // Permanent — direct StudyFinish response is not valid from NoStudy state,
            // so use the same 3-packet chain to reliably reach StudyFinish.
            session.Send(new PK_HC_Corps_StudyTankRes { CorpsTankId = msg.TankId, TroopsTankTimeTypeRaw = 1, RetFlag = 1 });
            session.Send(new PK_HC_Corps_SpeedStudyTankRes { CorpsTankId = msg.TankId, RetFlag = 1 });
            session.Send(new PK_HC_Corps_StudyTankRes { CorpsTankId = msg.TankId, TroopsTankTimeTypeRaw = 0, RetFlag = 1 });
        }
        SendMoneyExpByGuid(session, data.PlayerGUID);
        SendAddedTank(session, newTank);
        SendResearchState(session, data.PlayerGUID);
        SendGarageRefresh(session, data.PlayerGUID);
    }

    private void HandleCorpsAthJoinReady(GameSession session)
    {
        Console.WriteLine("[HS] Corps ath join ready");
    }

    private void HandleCorpsAthJoin(GameSession session, PK_CH_CorpsAth_Join msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) return;
        var corps = _corpsRepo.GetByGUID(corpsGuid.Value);
        if (corps == null) return;
        _corpsRepo.AddMember(corpsGuid.Value, data.PlayerGUID, data.PlayerName, 0);
        _corpsRepo.RecalculateCorpsBattleCap(corpsGuid.Value);
    }

    private void HandleCorpsAthJoinQuit(GameSession session, PK_CH_CorpsAth_JoinQuit msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) return;
        _corpsRepo.RemoveMember(corpsGuid.Value, data.PlayerGUID);
        _corpsRepo.RecalculateCorpsBattleCap(corpsGuid.Value);
    }

    private void HandleCorpsAthChangeTank(GameSession session, PK_CH_CorpsAth_ChangeTank msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) return;
        var tank = _tankRepo.GetTankByGUID(msg.TankGuid);
        if (tank != null)
        {
            int bc = _gameTables.GetTank(tank.VehicleId)?.MaxHp ?? 1000;
            _corpsRepo.UpdateMemberBattleCap(corpsGuid.Value, data.PlayerGUID, bc);
            _corpsRepo.RecalculateCorpsBattleCap(corpsGuid.Value);
        }
    }

    private void HandleCorpsAthAllowJoin(GameSession session, PK_CH_CorpsAth_AllowJoin msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) return;
        var member = _corpsRepo.GetMember(corpsGuid.Value, data.PlayerGUID);
        if (member == null || member.Job < 1) return;
    }

    private void HandleCorpsBuyNumLimitTimes(GameSession session)
    {
        Console.WriteLine("[HS] Corps buy num limit times");
    }

    private void HandleCorpsFastDevelop(GameSession session, PK_CH_Corps_SpeedStudyTank msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;

        int corpsTankId = msg.CorpsTankId;
        Console.WriteLine($"[HS] Corps fast develop: CorpsTankId={corpsTankId}");

        // Deduct 300 gold for acceleration
        const int GoldCost = 300;
        if (data.Gold < GoldCost)
        {
            session.Send(new PK_HC_Corps_SpeedStudyTankRes { CorpsTankId = corpsTankId, RetFlag = 0 });
            return;
        }
        _accountRepo.UpdateResources(data.PlayerGUID, gold: data.Gold - GoldCost);

        // Grant the tank if not already owned
        int arrayIndex = corpsTankId - 1;
        if (arrayIndex >= 0 && arrayIndex < LegionTanks.Length)
        {
            var lt = LegionTanks[arrayIndex];
            var ownedIds = new HashSet<int>(_tankRepo.GetPlayerTanks(data.PlayerGUID).Select(t => t.VehicleId));
            if (!ownedIds.Contains(lt.TankId))
            {
                var tankEntry = _gameTables.GetTank(lt.TankId);
                if (tankEntry != null)
                {
                    var newTank = _tankRepo.CreateTank(data.PlayerGUID, lt.TankId,
                        tankEntry.HullId, tankEntry.UnderpanId, tankEntry.TurretId,
                        tankEntry.MainGunId, tankEntry.EngineId, tankEntry.FueltankId,
                        tankEntry.CartridgeId, tankEntry.RadioStationId, tankEntry.GaugeId, tankEntry.MaxHp);
                    _moduleRepo?.ResearchTankBaseModules(data.PlayerGUID, lt.TankId,
                        tankEntry.HullId, tankEntry.UnderpanId, tankEntry.TurretId,
                        tankEntry.MainGunId, tankEntry.EngineId, tankEntry.FueltankId,
                        tankEntry.CartridgeId, tankEntry.RadioStationId, tankEntry.GaugeId);
                    SendAddedTank(session, newTank);
                    SendGarageRefresh(session, data.PlayerGUID);
                    Console.WriteLine($"[HS] Corps fast develop: granted {tankEntry.Name} (CorpsTankId={corpsTankId})");
                }
            }
        }

        // FastDevelop_Res → client: WaitForPick (3)
        session.Send(new PK_HC_Corps_SpeedStudyTankRes { CorpsTankId = corpsTankId, RetFlag = 1 });
        // StudyTankRes TimeType=Forever → client jumps WaitForPick→StudyFinish (4), removes timer
        session.Send(new PK_HC_Corps_StudyTankRes { CorpsTankId = corpsTankId, TroopsTankTimeTypeRaw = 0, RetFlag = 1 });
        SendMoneyExpByGuid(session, data.PlayerGUID);
    }

    private void HandleFriendStateReq(GameSession session, PK_CHW_FriendStateReq msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var friends = _friendRepo.GetFriends(data.PlayerGUID);
        Console.WriteLine($"[HS] Friend state req: {friends.Count} friends");
    }

    private void HandleFriendUpdate(GameSession session)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var friends = _friendRepo.GetFriends(data.PlayerGUID);
        foreach (var friend in friends)
        {
            _friendRepo.UpdateFriendLevel(data.PlayerGUID, friend.FriendGUID, data.Level);
        }
    }

    private void HandleFriendOperate(GameSession session, PK_CHW_FRIEND_OPERATE msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        if (msg.OperType == 0)
        {
            var target = _accountRepo.GetByUsername(msg.FriendName);
            if (target != null && !_friendRepo.IsFriend(data.PlayerGUID, target.PlayerGUID))
            {
                _friendRepo.AddFriend(data.PlayerGUID, target.PlayerGUID, target.PlayerName);
                session.Send(new PK_WHC_FRIEND_OPERATE_RES { OperType = msg.OperType, Result = 1 });
            }
            else session.Send(new PK_WHC_FRIEND_OPERATE_RES { OperType = msg.OperType, Result = 0 });
        }
        else if (msg.OperType == 1)
        {
            var target = _accountRepo.GetByUsername(msg.FriendName);
            if (target != null) _friendRepo.RemoveFriend(data.PlayerGUID, target.PlayerGUID);
            session.Send(new PK_WHC_FRIEND_OPERATE_RES { OperType = msg.OperType, Result = 1 });
        }
    }

    private void HandleDomainBidding(GameSession session, PK_CHW_Domain_Bidding msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) return;
        var corps = _corpsRepo.GetByGUID(corpsGuid.Value);
        if (corps == null) return;
        Console.WriteLine($"[HS] Domain bid: {data.PlayerName} corps={corps.CorpsName}");
    }

    private void HandleDomainFightOther(GameSession session, PK_CHW_Domain_FightOther msg)
    {
        Console.WriteLine($"[HS] Domain fight: {session.PlayerName}");
        HandleJoinRoom(session, new PK_CH_RequestJoinRoom { BattleMode = 5, BattleLevel = 1, TankGUID = GetSelectedTankGuid(session.PlayerGUID) });
    }

    private void HandleDomainFightTime(GameSession session)
    {
        Console.WriteLine("[HS] Domain fight time");
    }

    private void HandlePlayerQuery(GameSession session, PK_CHW_PLAYER_QUERY msg)
    {
        var target = _accountRepo.GetByUsername(msg.PlayerName);
        if (target != null)
            session.Send(new PK_WHC_PLAYER_QUERY_RES { PlayerGuid = target.PlayerGUID, PlayerName = target.PlayerName, Level = target.Level, BattleCap = target.BattleCap });
        else
            session.Send(new PK_WHC_PLAYER_QUERY_RES { PlayerName = msg.PlayerName });
    }

    private void HandleCorpsBidPlaceBid(GameSession session, PK_CHW_CorpsBid_PlaceBid msg)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) return;
        var member = _corpsRepo.GetMember(corpsGuid.Value, data.PlayerGUID);
        if (member == null || member.Job < 1) return;
        Console.WriteLine($"[HS] Corps bid placed by {data.PlayerName}");
    }
    private void HandleCorpsBidGetTank(GameSession session, PK_CHW_CorpsBid_GetTank msg)
    {
        Console.WriteLine($"[HS] Corps get tank bid: {session.PlayerName}");
    }
    private void HandleCorpsBidLists(GameSession session)
    {
        Console.WriteLine($"[HS] Corps bid lists: {session.PlayerName}");
    }
    private void HandleCorpsLevelUpgrade(GameSession session)
    {
        var data = GetPlayerData(session);
        if (data == null) return;
        var corpsGuid = _corpsRepo.GetPlayerCorpsGUID(data.PlayerGUID);
        if (!corpsGuid.HasValue) return;
        var member = _corpsRepo.GetMember(corpsGuid.Value, data.PlayerGUID);
        if (member == null || member.Job < 2) return;
        var corps = _corpsRepo.GetByGUID(corpsGuid.Value);
        if (corps == null) return;
        int upgradeCost = corps.CorpsLevel * 10000;
        if (corps.Money < upgradeCost)
        {
            session.Send(new PK_WHC_CorpsLevel_upgrade_Res { Flag = 0, NewLevel = corps.CorpsLevel });
            return;
        }
        _corpsRepo.UpdateCorpsMoney(corpsGuid.Value, corps.Money - upgradeCost);
        _corpsRepo.UpgradeCorpsLevel(corpsGuid.Value);
        int newLevel = corps.CorpsLevel + 1;
        session.Send(new PK_WHC_CorpsLevel_upgrade_Res { Flag = 1, NewLevel = newLevel });
        Console.WriteLine($"[HS] Corps level upgrade: {corps.CorpsName} -> Lvl {newLevel}");
    }

    public bool IsPlayerOnline(long playerGuid) =>
        _hallByGuid.TryGetValue(playerGuid, out var s) && s.IsConnected;

    public void NotifyAdminResourcesUpdated(long playerGuid)
    {
        try
        {
            if (_hallByGuid.TryGetValue(playerGuid, out var s) && s.IsConnected)
                SendMoneyExpByGuid(s, playerGuid);
        }
        catch { }
    }

    public void NotifyAdminTankAdded(long playerGuid, int vehicleId)
    {
        try
        {
            if (!_hallByGuid.TryGetValue(playerGuid, out var s) || !s.IsConnected) return;
            var tank = _tankRepo.GetPlayerTanks(playerGuid).FirstOrDefault(t => t.VehicleId == vehicleId);
            if (tank != null) SendAddedTank(s, tank);
        }
        catch { }
    }

    public void NotifyAdminInventoryUpdated(long playerGuid, byte itemType, int itemId)
    {
        try
        {
            if (_hallByGuid.TryGetValue(playerGuid, out var s) && s.IsConnected)
                SendInventoryUpdate(s, playerGuid, itemType, itemId);
        }
        catch { }
    }

    protected override void OnClientDisconnected(GameSession session)
    {
        _friendRepo.SetOnlineStatus(session.PlayerGUID, false);
        _hallByGuid.TryRemove(session.PlayerGUID, out _);   // v13
        lock (_matchLock)
        {
            RemovePlayerFromWaitingLobbiesLocked(session.PlayerGUID);
        }
    }
}

public class HallPlayerData
{
    public long PlayerGUID { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public int Silver { get; set; }
    public int Gold { get; set; }
    public int Experience { get; set; }
    public int Level { get; set; }
    public int BattleCap { get; set; }
    public int CasernCapacity { get; set; }
    public int GarageBackground { get; set; }
    public byte ChannelId { get; set; }
    public int ContinuousLoginDays { get; set; }
    public int MonthlyLoginDay { get; set; }
    public bool IsVip { get; set; }
    public DateTime VipExpireDate { get; set; }
    public bool IsPremium { get; set; }
    public DateTime PremiumExpireDate { get; set; }
    public DateTime CreatedAt { get; set; }
}
