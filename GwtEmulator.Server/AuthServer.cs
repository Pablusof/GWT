using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using GwtEmulator.Server.Database.Repositories;
using GwtEmulator.Server.GameTables;

namespace GwtEmulator.Server;

public class AuthServer
{
    private readonly int _port;
    private TcpListener _listener = null!;
    private bool _running;

    public AuthServer(int port)
    {
        _port = port;
    }

    public void Start()
    {
        _running = true;
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        Console.WriteLine($"[Auth] HTTP Auth Server listening on http://0.0.0.0:{_port}/ (panel: http://127.0.0.1:{_port}/panel)");
        Task.Run(ListenLoop);
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
    }

    private async Task ListenLoop()
    {
        while (_running)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClient(client));
            }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }
        }
    }

    private async Task HandleClient(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            try
            {
                var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                bool isLocalHost = remoteIp != null && IPAddress.IsLoopback(remoteIp);

                var reader = new StreamReader(stream, Encoding.UTF8);
                string? requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(requestLine))
                    return;

                string headerLine;
                int contentLength = 0;
                while (!string.IsNullOrEmpty(headerLine = await reader.ReadLineAsync() ?? string.Empty))
                {
                    if (headerLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(headerLine.Substring(15).Trim(), out contentLength);
                }

                if (contentLength > 0)
                {
                    char[] bodyBuf = new char[contentLength];
                    await reader.ReadAsync(bodyBuf, 0, contentLength);
                }

                var parts = requestLine.Split(' ');
                string path = parts.Length > 1 ? parts[1] : "/";
                string query = "";
                int qIdx = path.IndexOf('?');
                if (qIdx >= 0)
                {
                    query = path.Substring(qIdx);
                    path = path.Substring(0, qIdx);
                }

                string statusLine = "HTTP/1.1 200 OK";
                string responseBody;
                string contentType = "text/plain; charset=utf-8";

                if (path == "/TK/auth.action")
                {
                    Console.WriteLine($"[Auth] {requestLine}");
                    responseBody = HandleAuthAction(query);
                }
                else if (path == "/TK/login.action")
                {
                    Console.WriteLine($"[Auth] {requestLine}");
                    responseBody = HandleLoginAction(query);
                }
                else if (path == "/TK/set_nick")
                {
                    responseBody = HandleSetNicknameAction(query);
                }
                else if (path == "/admin")
                {
                    var qp = ParseQueryString(query);
                    if ((qp["cmd"] ?? "") == "set_nick")
                    {
                        responseBody = HandleSetNicknameAction(query);
                    }
                    else if (!isLocalHost)
                    {
                        statusLine = "HTTP/1.1 403 Forbidden";
                        responseBody = "ERR:Чит-панель /admin доступна только с хост-ПК (127.0.0.1)";
                    }
                    else
                    {
                        if ((qp["cmd"] ?? "").StartsWith("list_", StringComparison.Ordinal))
                            contentType = "application/json; charset=utf-8";
                        responseBody = HandleAdminAction(query);
                    }
                }
                else if (path == "/panel" || path == "/cheat")
                {
                    if (!isLocalHost)
                    {
                        statusLine = "HTTP/1.1 403 Forbidden";
                        responseBody = "403 Forbidden: Admin panel is only accessible from host PC (127.0.0.1).";
                    }
                    else
                    {
                        contentType = "text/html; charset=utf-8";
                        responseBody = BuildWebCheatPanelHtml();
                    }
                }
                else
                {
                    statusLine = "HTTP/1.1 404 Not Found";
                    responseBody = "404 Not Found";
                }

                byte[] body = Encoding.UTF8.GetBytes(responseBody);
                string header = $"{statusLine}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\nAccess-Control-Allow-Origin: *\r\n\r\n";
                byte[] headerBytes = Encoding.ASCII.GetBytes(header);
                await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
                await stream.WriteAsync(body, 0, body.Length);
                await stream.FlushAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Auth] Error handling request: {ex.Message}");
            }
        }
    }

    private string HandleAuthAction(string query)
    {
        var queryParams = ParseQueryString(query);
        Console.WriteLine($"[Auth] auth.action params: {query}");

        string persId = queryParams["sz_pers_id"] ?? "1001";
        string login = queryParams["login"] ?? "Player";
        string? customNick = AdminAccountRepo?.GetNicknameByPersId(persId);

        string playerName;
        if (!string.IsNullOrWhiteSpace(customNick))
        {
            playerName = customNick;
        }
        else if (string.IsNullOrWhiteSpace(login) || login == "Player" || login.Contains("result"))
        {
            playerName = "Player_" + persId;
        }
        else
        {
            playerName = login;
        }

        string serverId = "1";
        long reqTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string sign = "auth_sign_" + reqTime;

        string result = $"callback:name:\"result=succ&id={persId}&status=ok&login={playerName}&pers_id={persId}&mailru=1&rts=0&req_time={reqTime}&sign={sign}&server_id={serverId}&new_agreement=false\"";
        Console.WriteLine($"[Auth] auth.response -> playerName={playerName}, pers_id={persId}");
        return result;
    }

    private string HandleLoginAction(string query)
    {
        Console.WriteLine($"[Auth] login.action params: {query}");
        return "name:\"result=succ&status=ok&login=Player\"";
    }

    private string HandleSetNicknameAction(string query)
    {
        if (AdminAccountRepo == null) return "ERR:AccountRepo не инициализирован";
        var p = ParseQueryString(query);
        string persId = p["persId"] ?? p["sz_pers_id"] ?? "";
        string nick = p["nick"] ?? p["name"] ?? "";

        if (AdminAccountRepo.SetNicknameForPersId(persId, nick, out string err))
        {
            string saved = AdminAccountRepo.GetNicknameByPersId(persId) ?? nick;
            Console.WriteLine($"[Auth] Nickname bound: persId={persId} -> '{saved}'");
            return $"OK:{saved}";
        }
        return $"ERR:{err}";
    }

    private static NameValueCollection ParseQueryString(string query)
    {
        var result = new NameValueCollection();
        if (string.IsNullOrEmpty(query)) return result;
        if (query.StartsWith("?")) query = query.Substring(1);
        foreach (var part in query.Split('&'))
        {
            var idx = part.IndexOf('=');
            if (idx > 0)
                result[part.Substring(0, idx)] = HttpUtility.UrlDecode(part.Substring(idx + 1));
        }
        return result;
    }

    // Admin panel repos — set from Program.cs after construction
    public AccountRepository?   AdminAccountRepo;
    public TankRepository?      AdminTankRepo;
    public CorpsRepository?     AdminCorpsRepo;
    public InventoryRepository? AdminInventoryRepo;
    public GameTablesService?   AdminGameTables;
    public HallServer?          AdminHallServer;

    // ── Admin endpoint ────────────────────────────────────────────────────────
    // GET http://127.0.0.1:18080/admin?cmd=...&player=...
    public string HandleAdminAction(string query)
    {
        if (AdminAccountRepo == null) return "ERR:Admin repos not wired";
        var p      = ParseQueryString(query);
        string cmd = p["cmd"]    ?? "";
        string pl  = p["player"] ?? "";

        try
        {
            switch (cmd)
            {
                case "list_players":
                {
                    var accounts = AdminAccountRepo.GetAllAccounts();
                    var items = accounts.Select(a => new
                    {
                        playerName = a.PlayerName,
                        username = a.Username,
                        playerGuid = a.PlayerGUID,
                        level = a.Level,
                        gold = a.Gold,
                        silver = a.Silver,
                        experience = a.Experience,
                        isPremium = a.IsPremium && a.PremiumExpireDate > DateTime.UtcNow,
                        isVip = a.IsVip && a.VipExpireDate > DateTime.UtcNow,
                        tankCount = AdminTankRepo?.GetPlayerTanks(a.PlayerGUID).Count ?? 0,
                        online = AdminHallServer?.IsPlayerOnline(a.PlayerGUID) ?? false,
                    });
                    return JsonSerializer.Serialize(items);
                }

                case "list_catalog_tanks":
                {
                    if (AdminGameTables == null) return "[]";
                    var catalog = AdminGameTables.Tanks.Values
                        .Where(t => !string.IsNullOrWhiteSpace(t.Name) && t.HullId > 0 && t.MainGunId > 0)
                        .OrderBy(t => t.Tier)
                        .ThenBy(t => t.Id)
                        .Select(t => new
                        {
                            id = t.Id,
                            name = t.Name,
                            tier = t.Tier,
                            nation = t.Nation,
                            currency = t.Currency,
                            type = t.Type,
                            vehicleType = t.VehicleType,
                        });
                    return JsonSerializer.Serialize(catalog);
                }
            }

            var account = AdminAccountRepo.FindByPlayerName(pl);
            if (account == null) return $"ERR:Игрок '{pl}' не найден";

            switch (cmd)
            {
                case "list_player_tanks":
                {
                    if (AdminTankRepo == null) return "[]";
                    var owned = AdminTankRepo.GetPlayerTanks(account.PlayerGUID)
                        .Select(t =>
                        {
                            var te = AdminGameTables?.GetTank(t.VehicleId);
                            return new
                            {
                                tankGuid = t.TankGUID,
                                vehicleId = t.VehicleId,
                                name = te?.Name ?? $"Tank #{t.VehicleId}",
                                tier = te?.Tier ?? 0,
                                currency = te?.Currency ?? 1,
                            };
                        })
                        .OrderBy(x => x.tier)
                        .ThenBy(x => x.vehicleId);
                    return JsonSerializer.Serialize(owned);
                }

                case "set_gold":
                {
                    int gold = int.Parse(p["amount"] ?? "0");
                    AdminAccountRepo.UpdateResources(account.PlayerGUID, gold: gold);
                    AdminHallServer?.NotifyAdminResourcesUpdated(account.PlayerGUID);
                    return $"Золото ({account.PlayerName}) → {gold:N0}";
                }

                case "set_silver":
                {
                    int silver = int.Parse(p["amount"] ?? "0");
                    AdminAccountRepo.UpdateResources(account.PlayerGUID, silver: silver);
                    AdminHallServer?.NotifyAdminResourcesUpdated(account.PlayerGUID);
                    return $"Серебро ({account.PlayerName}) → {silver:N0}";
                }

                case "set_exp":
                {
                    int exp = int.Parse(p["amount"] ?? "0");
                    AdminAccountRepo.UpdateResources(account.PlayerGUID, experience: exp);
                    AdminHallServer?.NotifyAdminResourcesUpdated(account.PlayerGUID);
                    return $"Свободный опыт ({account.PlayerName}) → {exp:N0}";
                }

                case "set_loyal":
                {
                    int loyal = int.Parse(p["amount"] ?? "0");
                    var cid = AdminCorpsRepo?.GetPlayerCorpsGUID(account.PlayerGUID);
                    if (!cid.HasValue) return "ERR:Нет легиона";
                    var m   = AdminCorpsRepo!.GetMember(cid.Value, account.PlayerGUID);
                    int diff = loyal - (m?.LoyalPoint ?? 0);
                    if (diff != 0) AdminCorpsRepo!.UpdateMemberLoyalPoint(cid.Value, account.PlayerGUID, diff);
                    return $"Лояльность ({account.PlayerName}) → {loyal:N0}";
                }

                case "give_tank":
                {
                    int vid = int.Parse(p["vehicleId"] ?? "0");
                    if (AdminTankRepo == null || AdminGameTables == null) return "ERR:Нет TankRepo";
                    if (AdminTankRepo.HasTankByVehicleId(account.PlayerGUID, vid))
                        return $"У {account.PlayerName} уже есть танк id={vid}";
                    var te = AdminGameTables.GetTank(vid);
                    if (te == null) return $"ERR:VehicleId {vid} не найден";

                    // Auto-expand carport slots if needed (below 85 client cap bug threshold)
                    int ownedCount = AdminTankRepo.GetPlayerTanks(account.PlayerGUID).Count;
                    if (ownedCount >= account.CasernCapacity && account.CasernCapacity < 84)
                    {
                        AdminAccountRepo.UpdateCasernCapacity(account.PlayerGUID, Math.Min(84, ownedCount + 5));
                    }

                    AdminTankRepo.CreateTank(account.PlayerGUID, vid,
                        te.HullId, te.UnderpanId, te.TurretId, te.MainGunId,
                        te.EngineId, te.FueltankId, te.CartridgeId,
                        te.RadioStationId, te.GaugeId, te.MaxHp);
                    AdminHallServer?.NotifyAdminTankAdded(account.PlayerGUID, vid);
                    return $"✓ Танк {te.Name} (id={vid}) выдан игроку {account.PlayerName}";
                }

                case "remove_tank":
                {
                    int rid = int.Parse(p["vehicleId"] ?? "0");
                    if (AdminTankRepo == null) return "ERR:Нет TankRepo";
                    var tank = AdminTankRepo.GetPlayerTanks(account.PlayerGUID)
                                           .FirstOrDefault(t => t.VehicleId == rid);
                    if (tank == null) return $"ERR:Танк id={rid} у {account.PlayerName} не найден";
                    AdminTankRepo.DeleteTank(tank.TankGUID);
                    return $"✓ Танк VehicleId={rid} удалён у {account.PlayerName}";
                }

                case "get_info":
                {
                    var cur = AdminAccountRepo.GetByPlayerGUID(account.PlayerGUID);
                    return $"{account.PlayerName}: Gold={cur?.Gold:N0} Silver={cur?.Silver:N0} Exp={cur?.Experience:N0} Lvl={cur?.Level}";
                }

                case "give_all_tanks":
                {
                    if (AdminTankRepo == null || AdminGameTables == null) return "ERR:Нет TankRepo";
                    int added = 0;
                    foreach (var kv in AdminGameTables.Tanks)
                    {
                        var t = kv.Value;
                        if (string.IsNullOrWhiteSpace(t.Name)) continue;
                        if (t.HullId <= 0 || t.MainGunId <= 0) continue;
                        if (AdminTankRepo.HasTankByVehicleId(account.PlayerGUID, t.Id)) continue;
                        AdminTankRepo.CreateTank(account.PlayerGUID, t.Id,
                            t.HullId, t.UnderpanId, t.TurretId, t.MainGunId,
                            t.EngineId, t.FueltankId, t.CartridgeId,
                            t.RadioStationId, t.GaugeId, t.MaxHp);
                        AdminHallServer?.NotifyAdminTankAdded(account.PlayerGUID, t.Id);
                        added++;
                    }
                    return $"✓ Выдано танков ({account.PlayerName}): {added}";
                }

                case "add_gold":
                {
                    int add = int.Parse(p["amount"] ?? "0");
                    var c = AdminAccountRepo.GetByPlayerGUID(account.PlayerGUID);
                    int ng = (c?.Gold ?? 0) + add;
                    AdminAccountRepo.UpdateResources(account.PlayerGUID, gold: ng);
                    AdminHallServer?.NotifyAdminResourcesUpdated(account.PlayerGUID);
                    return $"Золото ({account.PlayerName}) +{add:N0} → {ng:N0}";
                }

                case "add_silver":
                {
                    int add = int.Parse(p["amount"] ?? "0");
                    var c = AdminAccountRepo.GetByPlayerGUID(account.PlayerGUID);
                    int ns = (c?.Silver ?? 0) + add;
                    AdminAccountRepo.UpdateResources(account.PlayerGUID, silver: ns);
                    AdminHallServer?.NotifyAdminResourcesUpdated(account.PlayerGUID);
                    return $"Серебро ({account.PlayerName}) +{add:N0} → {ns:N0}";
                }

                case "add_exp":
                {
                    int add = int.Parse(p["amount"] ?? "0");
                    var c = AdminAccountRepo.GetByPlayerGUID(account.PlayerGUID);
                    int ne = (c?.Experience ?? 0) + add;
                    AdminAccountRepo.UpdateResources(account.PlayerGUID, experience: ne);
                    AdminHallServer?.NotifyAdminResourcesUpdated(account.PlayerGUID);
                    return $"Свободный опыт ({account.PlayerName}) +{add:N0} → {ne:N0}";
                }

                case "max":
                {
                    AdminAccountRepo.UpdateResources(account.PlayerGUID,
                        gold: 999_999_999, silver: 999_999_999, experience: 999_999_999);
                    AdminHallServer?.NotifyAdminResourcesUpdated(account.PlayerGUID);
                    return $"✓ {account.PlayerName}: Золото/Серебро/Опыт → максимум";
                }

                case "list_tanks":
                {
                    if (AdminTankRepo == null) return "ERR:Нет TankRepo";
                    var owned = AdminTankRepo.GetPlayerTanks(account.PlayerGUID)
                                             .Select(t => t.VehicleId).OrderBy(x => x).ToList();
                    return $"{account.PlayerName}: Танков {owned.Count} — VehicleId: {string.Join(",", owned)}";
                }

                case "clear_tanks":
                {
                    if (AdminTankRepo == null) return "ERR:Нет TankRepo";
                    var all = AdminTankRepo.GetPlayerTanks(account.PlayerGUID).ToList();
                    foreach (var t in all) AdminTankRepo.DeleteTank(t.TankGUID);
                    return $"✓ Удалено танков у {account.PlayerName}: {all.Count}";
                }

                case "give_premium":
                {
                    int days = int.Parse(p["days"] ?? p["amount"] ?? "30");
                    AdminAccountRepo.UpdatePremiumStatus(account.PlayerGUID, true, DateTime.UtcNow.AddDays(days));
                    return $"✓ Премиум для {account.PlayerName} выдан на {days} дн.";
                }

                case "give_vip":
                {
                    int days = int.Parse(p["days"] ?? p["amount"] ?? "30");
                    AdminAccountRepo.UpdateVipStatus(account.PlayerGUID, true, DateTime.UtcNow.AddDays(days));
                    return $"✓ VIP для {account.PlayerName} выдан на {days} дн.";
                }

                case "set_level":
                {
                    int lvl = int.Parse(p["amount"] ?? "99");
                    AdminAccountRepo.UpdateLevel(account.PlayerGUID, lvl);
                    AdminHallServer?.NotifyAdminResourcesUpdated(account.PlayerGUID);
                    return $"✓ Уровень {account.PlayerName} = {lvl}";
                }

                case "set_battlecap":
                {
                    int bc = int.Parse(p["amount"] ?? "85");
                    AdminAccountRepo.UpdateBattleCap(account.PlayerGUID, bc);
                    return $"Ёмкость боёв = {bc}";
                }

                case "set_caserne":
                {
                    int cc = int.Parse(p["amount"] ?? "80");
                    AdminAccountRepo.UpdateCasernCapacity(account.PlayerGUID, cc);
                    return $"Ёмкость казармы ({account.PlayerName}) = {cc}";
                }

                case "give_consumables":
                {
                    if (AdminInventoryRepo == null || AdminGameTables == null) return "ERR:Нет InventoryRepo";
                    int qty = Math.Clamp(int.Parse(p["amount"] ?? "50"), 1, 999);
                    int count = 0;
                    foreach (var con in AdminGameTables.Consumables.Values)
                    {
                        if (con.Id <= 0) continue;
                        AdminInventoryRepo.AddItem(account.PlayerGUID, 103, con.Id, qty);
                        AdminHallServer?.NotifyAdminInventoryUpdated(account.PlayerGUID, 103, con.Id);
                        count++;
                    }
                    foreach (var sb in AdminGameTables.SmokeBombs.Values)
                    {
                        if (sb.Id <= 0) continue;
                        AdminInventoryRepo.AddItem(account.PlayerGUID, 103, sb.Id, qty);
                        AdminHallServer?.NotifyAdminInventoryUpdated(account.PlayerGUID, 103, sb.Id);
                        count++;
                    }
                    return $"✓ Выдано расходников x{qty} ({count} позиций) игроку {account.PlayerName}";
                }

                case "give_equipment":
                {
                    if (AdminInventoryRepo == null || AdminGameTables == null) return "ERR:Нет InventoryRepo";
                    int qty = Math.Clamp(int.Parse(p["amount"] ?? "5"), 1, 99);
                    int count = 0;
                    foreach (var eq in AdminGameTables.Equipments.Values)
                    {
                        if (eq.Id <= 0) continue;
                        AdminInventoryRepo.AddItem(account.PlayerGUID, 101, eq.Id, qty);
                        AdminHallServer?.NotifyAdminInventoryUpdated(account.PlayerGUID, 101, eq.Id);
                        count++;
                    }
                    return $"✓ Выдано оборудования x{qty} ({count} позиций) игроку {account.PlayerName}";
                }

                case "give_all_premium":
                case "give_all_regular":
                {
                    if (AdminTankRepo == null || AdminGameTables == null) return "ERR:Нет TankRepo";
                    int wantCur = cmd == "give_all_premium" ? 2 : 1;
                    int added = 0;
                    foreach (var kv in AdminGameTables.Tanks)
                    {
                        var t = kv.Value;
                        if (string.IsNullOrWhiteSpace(t.Name) || t.HullId <= 0 || t.MainGunId <= 0) continue;
                        if (t.Currency != wantCur) continue;
                        if (AdminTankRepo.HasTankByVehicleId(account.PlayerGUID, t.Id)) continue;
                        AdminTankRepo.CreateTank(account.PlayerGUID, t.Id,
                            t.HullId, t.UnderpanId, t.TurretId, t.MainGunId,
                            t.EngineId, t.FueltankId, t.CartridgeId,
                            t.RadioStationId, t.GaugeId, t.MaxHp);
                        AdminHallServer?.NotifyAdminTankAdded(account.PlayerGUID, t.Id);
                        added++;
                    }
                    return $"✓ Выдано танков ({(wantCur == 2 ? "премы" : "обычные")}) игроку {account.PlayerName}: {added}";
                }

                case "god":
                {
                    AdminAccountRepo.UpdateResources(account.PlayerGUID,
                        gold: 999_999_999, silver: 999_999_999, experience: 999_999_999, level: 99);
                    AdminAccountRepo.UpdatePremiumStatus(account.PlayerGUID, true, DateTime.UtcNow.AddYears(10));
                    AdminAccountRepo.UpdateVipStatus(account.PlayerGUID, true, DateTime.UtcNow.AddYears(10));
                    int added = 0;
                    if (AdminTankRepo != null && AdminGameTables != null)
                        foreach (var kv in AdminGameTables.Tanks)
                        {
                            var t = kv.Value;
                            if (string.IsNullOrWhiteSpace(t.Name) || t.HullId <= 0 || t.MainGunId <= 0) continue;
                            if (AdminTankRepo.HasTankByVehicleId(account.PlayerGUID, t.Id)) continue;
                            AdminTankRepo.CreateTank(account.PlayerGUID, t.Id,
                                t.HullId, t.UnderpanId, t.TurretId, t.MainGunId,
                                t.EngineId, t.FueltankId, t.CartridgeId,
                                t.RadioStationId, t.GaugeId, t.MaxHp);
                            AdminHallServer?.NotifyAdminTankAdded(account.PlayerGUID, t.Id);
                            added++;
                        }
                    AdminHallServer?.NotifyAdminResourcesUpdated(account.PlayerGUID);
                    return $"✓ GOD для {account.PlayerName}: ресурсы MAX, прем+VIP 10 лет, ур. 99, танков +{added}";
                }

                default:
                    return $"ERR:Неизвестная команда: {cmd}";
            }
        }
        catch (Exception ex) { return $"ERR:{ex.Message}"; }
    }

    private static string BuildWebCheatPanelHtml() => """
<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="utf-8">
<title>Ground War Tanks — Чит-панель</title>
<style>
  :root{--bg:#0b0d12;--card:#131722;--inp:#0e121a;--red:#ef4434;--cyan:#38bdf8;--txt:#f1f5f9;--mut:#94a3b8;--brd:#262d3d;}
  body{margin:0;font-family:Segoe UI,sans-serif;background:var(--bg);color:var(--txt);padding:24px;}
  h1{margin:0 0 6px;font-size:22px;} .sub{color:var(--mut);font-size:13px;margin-bottom:20px;}
  .grid{display:grid;grid-template-columns:360px 1fr;gap:18px;max-width:1180px;margin:0 auto;}
  .card{background:var(--card);border:1px solid var(--brd);border-radius:10px;padding:16px;}
  label{display:block;font-size:11px;font-weight:700;color:var(--red);margin:10px 0 6px;text-transform:uppercase;}
  input,select{width:100%;box-sizing:border-box;background:var(--inp);border:1px solid var(--brd);color:var(--txt);padding:8px 10px;border-radius:6px;font-size:13px;}
  .row{display:flex;gap:8px;margin-top:8px;flex-wrap:wrap;}
  button{cursor:pointer;background:#1e2433;border:1px solid var(--brd);color:var(--txt);padding:8px 12px;border-radius:6px;font-weight:600;font-size:12px;}
  button:hover{background:#283044;}
  button.primary{background:var(--red);border-color:var(--red);color:#fff;}
  button.primary:hover{background:#f05344;}
  .list{max-height:240px;overflow:auto;border:1px solid var(--brd);border-radius:6px;margin-top:8px;background:var(--inp);}
  .item{padding:7px 10px;border-bottom:1px solid #1b2130;cursor:pointer;display:flex;justify-content:space-between;font-size:12.5px;}
  .item:hover,.item.active{background:#231b20;border-left:3px solid var(--red);}
  .badge{font-size:11px;color:var(--cyan);} .on{color:#22c55e;font-weight:700;}
  #log{margin-top:14px;padding:10px;background:#090b10;border:1px solid var(--brd);border-radius:6px;font-family:Consolas,monospace;font-size:12.5px;min-height:42px;color:#cbd5e1;}
</style>
</head>
<body>
<div style="max-width:1180px;margin:0 auto 14px;display:flex;justify-content:space-between;align-items:center;">
  <div><h1>GROUND WAR <span style="color:var(--red)">TANKS</span> — Чит-панель</h1><div class="sub">Управление игроками, ресурсами и танками по нику</div></div>
  <button onclick="loadAll()">Обновить данные</button>
</div>
<div class="grid">
  <div class="card">
    <label>Игрок (по нику)</label>
    <input id="playerNick" placeholder="Введите ник игрока (например Player_12345)">
    <div id="playersList" class="list"></div>
    <label>Ресурсы и уровень</label>
    <div class="row">
      <input id="amount" type="number" value="1000000" style="flex:1">
      <button onclick="runCmd('add_gold',{amount:val('amount')})">+Золото</button>
      <button onclick="runCmd('add_silver',{amount:val('amount')})">+Серебро</button>
      <button onclick="runCmd('add_exp',{amount:val('amount')})">+Опыт</button>
    </div>
    <div class="row">
      <button onclick="runCmd('set_level',{amount:99})">Уровень 99</button>
      <button onclick="runCmd('give_premium',{days:30})">Прем 30д</button>
      <button onclick="runCmd('give_vip',{days:30})">VIP 30д</button>
      <button onclick="runCmd('max')">МАКС Ресурсы</button>
    </div>
    <div class="row">
      <button onclick="runCmd('give_consumables',{amount:50})">Расходники x50</button>
      <button onclick="runCmd('give_equipment',{amount:5})">Оборудование x5</button>
      <button class="primary" onclick="runCmd('god')">GOD MODE (ВСЁ)</button>
    </div>
  </div>
  <div class="card">
    <label>Каталог танков</label>
    <div class="row">
      <input id="tankSearch" placeholder="Поиск танка по названию или ID..." oninput="renderTanks()" style="flex:1">
      <select id="tierFilter" onchange="renderTanks()" style="width:110px">
        <option value="0">Все уровни</option>
        <option value="10">10 ур.</option><option value="9">9 ур.</option><option value="8">8 ур.</option>
        <option value="7">7 ур.</option><option value="6">6 ур.</option><option value="5">5 ур.</option>
      </select>
    </div>
    <div id="tanksList" class="list" style="max-height:300px"></div>
    <div class="row" style="margin-top:12px">
      <button class="primary" onclick="giveSelectedTank()">ВЫДАТЬ ВЫБРАННЫЙ ТАНК</button>
      <button onclick="removeSelectedTank()">Удалить танк</button>
      <button onclick="runCmd('give_all_tanks')">Выдать ВСЕ танки</button>
      <button onclick="runCmd('give_all_premium')">Все премы</button>
    </div>
    <div id="log">Готово к работе.</div>
  </div>
</div>
<script>
let players=[], tanks=[], selTankId=0;
const val=id=>document.getElementById(id).value;
async function api(cmd, extra={}){
  const u=new URL('/admin', location.origin);
  u.searchParams.set('cmd', cmd);
  u.searchParams.set('player', val('playerNick').trim());
  for(const [k,v] of Object.entries(extra)) u.searchParams.set(k, v);
  const r=await fetch(u); return await r.text();
}
async function runCmd(cmd, extra={}){
  const res=await api(cmd, extra);
  document.getElementById('log').textContent=res;
  if(cmd!=='list_players' && cmd!=='list_catalog_tanks') loadPlayers();
}
async function loadPlayers(){
  try{
    players=JSON.parse(await api('list_players'));
    const box=document.getElementById('playersList'); box.innerHTML='';
    players.forEach(p=>{
      const d=document.createElement('div'); d.className='item';
      d.innerHTML=`<span>${p.playerName} ${p.online?'<span class="on">●</span>':''}</span><span class="badge">Lvl ${p.level} • ${p.tankCount} т.</span>`;
      d.onclick=()=>{ document.getElementById('playerNick').value=p.playerName; };
      box.appendChild(d);
    });
    if(!val('playerNick') && players.length) document.getElementById('playerNick').value=players[0].playerName;
  }catch(e){ document.getElementById('log').textContent='Ошибка загрузки игроков: '+e; }
}
async function loadCatalog(){
  tanks=JSON.parse(await api('list_catalog_tanks'));
  renderTanks();
}
function renderTanks(){
  const q=val('tankSearch').toLowerCase(), tier=+val('tierFilter');
  const box=document.getElementById('tanksList'); box.innerHTML='';
  tanks.filter(t=>(!tier||t.tier===tier)&&(!q||t.name.toLowerCase().includes(q)||String(t.id).includes(q))).slice(0,250).forEach(t=>{
    const d=document.createElement('div'); d.className='item'+(t.id===selTankId?' active':'');
    d.innerHTML=`<span>[${t.id}] ${t.name}</span><span class="badge">${t.tier} ур. ${t.currency===2?'★ Прем':''}</span>`;
    d.onclick=()=>{ selTankId=t.id; renderTanks(); };
    box.appendChild(d);
  });
}
function giveSelectedTank(){ if(!selTankId){alert('Выберите танк');return;} runCmd('give_tank',{vehicleId:selTankId}); }
function removeSelectedTank(){ if(!selTankId){alert('Выберите танк');return;} runCmd('remove_tank',{vehicleId:selTankId}); }
async function loadAll(){ await Promise.all([loadPlayers(), loadCatalog()]); }
loadAll();
</script>
</body>
</html>
""";
}
