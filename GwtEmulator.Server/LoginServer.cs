using System;
using System.Net.Sockets;
using GwtEmulator.Protocol;
using GwtEmulator.Server.Database.Models;
using GwtEmulator.Server.Database.Repositories;

namespace GwtEmulator.Server;

public class LoginServer : GameServer
{
    // Optional explicit override (e.g., behind router NAT). Otherwise auto-detects from the accepted socket.
    private static readonly string? ExplicitPublicIp =
        Environment.GetEnvironmentVariable("GWT_PUBLIC_IP") is { Length: > 0 } ip ? ip.Trim() : null;

    private static string ResolveHallIp(GameSession session) =>
        ExplicitPublicIp ?? session.LocalIpAddress;

    private readonly AccountRepository _accountRepo;
    private readonly TankRepository _tankRepo;
    private readonly ModuleRepository? _moduleRepo;
    private int _nextPlayerId = 1000;

    public LoginServer(int port, AccountRepository accountRepo, TankRepository tankRepo, ModuleRepository? moduleRepo = null) : base("LS", port)
    {
        _accountRepo = accountRepo;
        _tankRepo = tankRepo;
        _moduleRepo = moduleRepo;
    }

    protected override GameSession CreateSession(TcpClient client) => new(client);

    protected override void OnClientConnected(GameSession session) { }

    protected override void OnMessageReceived(GameSession session, IRequest message)
    {
        switch (message.MsgType)
        {
            case TkMsgType.NM_CL_LOGIN: HandlePlayerLogin(session, (PK_CL_PlayerLogin)message); break;
            case TkMsgType.NM_CL_GameBoxLogin: HandleGameBoxLogin(session); break;
            case TkMsgType.NM_CL_FaceBookLogin: HandleFacebookLogin(session); break;
            case TkMsgType.NM_CL_SouzLogin: HandleSouzLogin(session, (PK_CL_SouzLogin)message); break;
            case TkMsgType.NM_CL_CreatRoleName: HandleCreateRoleName(session); break;
            case TkMsgType.NM_CH_Ping: break;
            default: Console.WriteLine($"[LS] Unhandled: {message.MsgType}"); break;
        }
    }

    private string SanitizePlayerName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Player_" + new Random().Next(10000, 99999);
        // If name contains URL params garbage, generate a clean name
        if (name.Contains('&') || name.Contains('=') || name.Contains('?') || name.Contains("result="))
            return "Player_" + new Random().Next(10000, 99999);
        if (name.Length > 20) name = name.Substring(0, 20);
        return name;
    }

    // Generous starting resources are seeded ONCE, when an account is first created. They must NOT
    // be re-applied on every login: doing so reset silver/gold/exp/level (and any manual DB-editor
    // edits or in-game spending) back up to the test defaults on the next session — i.e. "nothing
    // saved on the account". Persistence requires that ordinary logins leave stored values alone.
    private void SeedLocalTestAccountState(long playerGuid)
    {
        _accountRepo.UpdateResources(
            playerGuid,
            AccountRepository.LocalTestSilver,
            AccountRepository.LocalTestGold,
            AccountRepository.LocalTestPlayerExp,
            AccountRepository.LocalTestLevel,
            500_000);
        _accountRepo.UpdateCasernCapacity(playerGuid, AccountRepository.LocalTestCasernCapacity);
        _tankRepo.EnsureMinimumTankExp(playerGuid, TankRepository.LocalTestTankExp);
    }

    // Runs on every login. Only ensures the tutorial/guide state is complete so the player lands in
    // the hangar; it never touches currency, experience, level or tank progress, so spending and
    // manual edits persist across sessions.
    //
    // Exception: CasernCapacity. Accounts created before this fix were seeded with
    // CasernCapacity = 85 (== CarportSlotCapMax), which is not a valid gameplay value —
    // the client hard-codes two checks against the literal number 85 (TankSelectList.OnClickBuyPort
    // and GoldTankControl's buy-tank flow) that outright refuse to buy a new carport slot OR a new
    // tank while CarportNum == 85, regardless of how many slots are actually free. So an account
    // stuck at exactly 85 can never buy anything via those flows — it's a broken state, not
    // legitimate progress, and must be corrected automatically (a one-off manual admin?cmd=set_caserne
    // call is easy to forget/miss). We only ever lower it down to the new safe constant, and only
    // when the account is stuck at exactly the old bad value with room to spare (owned tanks fit
    // under the new cap) — a player who has genuinely earned all the way up to 85 via real slot
    // purchases would own tanks close to that number and won't be touched.
    private void EnsureLocalTestCasernCapacity(long playerGuid, Account account)
    {
        const int oldBuggyValue = 85;
        if (account.CasernCapacity != oldBuggyValue) return;

        int ownedTanks = _tankRepo.GetPlayerTanks(playerGuid).Count;
        if (ownedTanks >= AccountRepository.LocalTestCasernCapacity) return;

        _accountRepo.UpdateCasernCapacity(playerGuid, AccountRepository.LocalTestCasernCapacity);
        Console.WriteLine($"[LS] CasernCapacity {oldBuggyValue} -> {AccountRepository.LocalTestCasernCapacity} " +
                           $"(GUID={playerGuid}, tanks={ownedTanks}) — снята застрявшая на потолке ёмкость ангара");
    }

    private void EnsureLocalTestAccountState(long playerGuid)
    {
        var account = _accountRepo.GetByPlayerGUID(playerGuid);
        if (account == null)
        {
            return;
        }

        _accountRepo.EnsureLocalTestGuideState(playerGuid);
        EnsureLocalTestCasernCapacity(playerGuid, account);
    }

    private void HandlePlayerLogin(GameSession session, PK_CL_PlayerLogin login)
    {
        string cleanName = SanitizePlayerName(login.PlayerName);
        Console.WriteLine($"[LS] PlayerLogin: '{login.PlayerName}' -> clean: '{cleanName}' (Ver: {login.ClientVersion}, ResVer: {login.ResourceVersion})");

        var account = _accountRepo.GetByUsername(cleanName);
        if (account == null)
        {
            // Try to find by old garbage name too (migration)
            account = _accountRepo.GetByUsername(login.PlayerName);
        }
        if (account == null)
        {
            account = _accountRepo.CreateAccount(cleanName, login.PassWord, cleanName);
            _tankRepo.CreateStarterTank(account.PlayerGUID);
            _tankRepo.CreateAllTanks(account.PlayerGUID);
            SeedLocalTestAccountState(account.PlayerGUID);
            Console.WriteLine($"[LS] New account created: '{cleanName}' GUID={account.PlayerGUID}");
        }
        else
        {
            // Update name if it was garbage
            if (account.PlayerName != cleanName)
            {
                _accountRepo.UpdatePlayerName(account.PlayerGUID, cleanName);
                Console.WriteLine($"[LS] Updated player name: '{account.PlayerName}' -> '{cleanName}'");
            }
            Console.WriteLine($"[LS] Existing account: '{cleanName}' GUID={account.PlayerGUID}");
        }

        EnsureLocalTestAccountState(account.PlayerGUID);
        account = _accountRepo.GetByPlayerGUID(account.PlayerGUID)!;

        _accountRepo.UpdateLastLogin(account.PlayerGUID);

        int certification = new Random().Next(100000, 999999);
        session.PlayerName = cleanName;
        session.PlayerGUID = account.PlayerGUID;
        session.Certification = certification;

        string hallIp = ResolveHallIp(session);
        session.Send(new PK_LC_LoginRet
        {
            Certification = certification,
            PlayerGUID = account.PlayerGUID,
            Ip = hallIp,
            ServerId = 1, AgentId = 1, Port = 9301,
            IsEncrypted = 1
        });
        Console.WriteLine($"[LS] Login success -> HS {hallIp}:9301");

        session.EnableEncryption(account.PlayerGUID, certification);
    }

    private void HandleGameBoxLogin(GameSession session)
    {
        Console.WriteLine($"[LS] GameBoxLogin");
        SendLoginRedirect(session, "GameBoxUser");
    }

    private void HandleFacebookLogin(GameSession session)
    {
        Console.WriteLine($"[LS] FacebookLogin");
        SendLoginRedirect(session, "FBUser");
    }

    private void HandleSouzLogin(GameSession session, PK_CL_SouzLogin login)
    {
        Console.WriteLine($"[LS] SouzLogin: PersId={login.PersId}, Name='{login.PersName}', ServerId={login.ServerId}");

        var account = _accountRepo.GetByUsername(login.PersName);
        if (account == null)
        {
            account = _accountRepo.CreateAccount(login.PersName, "", login.PersName);
            _tankRepo.CreateStarterTank(account.PlayerGUID);
            _tankRepo.CreateAllTanks(account.PlayerGUID);
            SeedLocalTestAccountState(account.PlayerGUID);
            Console.WriteLine($"[LS] New account from SouzLogin: '{login.PersName}' GUID={account.PlayerGUID}");
        }
        else
        {
            Console.WriteLine($"[LS] Existing account from SouzLogin: '{login.PersName}' GUID={account.PlayerGUID}");
        }

        EnsureLocalTestAccountState(account.PlayerGUID);
        account = _accountRepo.GetByPlayerGUID(account.PlayerGUID)!;

        _accountRepo.UpdateLastLogin(account.PlayerGUID);

        int certification = new Random().Next(100000, 999999);
        session.PlayerName = account.PlayerName;
        session.PlayerGUID = account.PlayerGUID;
        session.Certification = certification;

        long reqTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string sign = $"souz_{account.PlayerGUID}_{reqTime}";

        session.Send(new PK_LC_SouzLogin
        {
            PersId = (int)account.PlayerGUID,
            ServerId = 1,
            StrMD5 = sign
        });

        Console.WriteLine($"[LS] SouzLogin response: PersId={account.PlayerGUID}, ServerId=1, StrMD5={sign}, Cert={certification}");

        System.Threading.Thread.Sleep(200);

        string hallIp = ResolveHallIp(session);
        session.Send(new PK_LC_LoginRet
        {
            Certification = certification,
            PlayerGUID = account.PlayerGUID,
            Ip = hallIp,
            ServerId = 1, AgentId = 1, Port = 9301,
            IsEncrypted = 0
        });

        Console.WriteLine($"[LS] LoginRet sent -> HS {hallIp}:9301");
    }

    private void SendLoginRedirect(GameSession session, string name)
    {
        long playerGuid = _nextPlayerId++;
        int certification = new Random().Next(100000, 999999);
        session.Send(new PK_LC_LoginRet
        {
            Certification = certification, PlayerGUID = playerGuid,
            Ip = ResolveHallIp(session), Port = 9301, ServerId = 1, AgentId = 1, IsEncrypted = 1
        });
    }

    private void HandleCreateRoleName(GameSession session)
    {
        Console.WriteLine($"[LS] CreateRoleName");
        session.Send(new PK_LC_CreatRoleNameRet { Flag = 1 });
    }
}
