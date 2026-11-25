using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using GwtEmulator.Server.Database.Repositories;
using GwtEmulator.Server.GameTables;

namespace GwtEmulator.Server;

public class AuthServer
{
    private readonly int _port;
    private TcpListener _listener;
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
        Console.WriteLine($"[Auth] HTTP Auth Server listening on http://0.0.0.0:{_port}/ (all interfaces)");
        Task.Run(() => ListenLoop());
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
                var reader = new StreamReader(stream, Encoding.UTF8);
                string requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(requestLine))
                    return;

                Console.WriteLine($"[Auth] {requestLine}");

                // Read headers
                string headerLine;
                int contentLength = 0;
                while (!string.IsNullOrEmpty(headerLine = await reader.ReadLineAsync()))
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

                string responseBody = "";
                string contentType = "text/plain; charset=utf-8";

                if (path == "/TK/auth.action")
                {
                    responseBody = HandleAuthAction(query);
                }
                else if (path == "/TK/login.action")
                {
                    responseBody = HandleLoginAction(query);
                }
                else if (path == "/admin")
                {
                    responseBody = HandleAdminAction(query);
                }
                else
                {
                    responseBody = "404 Not Found";
                }

                byte[] body = Encoding.UTF8.GetBytes(responseBody);
                string header = $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\nAccess-Control-Allow-Origin: *\r\n\r\n";
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
        // IMPORTANT: derive a STABLE player name from sz_pers_id so the same account is reused on
        // every launch. Previously this generated a random "Player_#####" each time, which created
        // a brand-new account on every login (progress, tanks, currency and any manual DB edits all
        // reset). Keying on the (stable) pers_id makes the account persist across sessions.
        string playerName = string.IsNullOrWhiteSpace(login) || login == "Player" || login.Contains("result")
            ? "Player_" + persId
            : login;
        string serverId = "1";
        long reqTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string sign = "auth_sign_" + reqTime;

        // Client parses by splitting name:"..." on &, then extracting key=value pairs
        // PlayerName comes from the "login" field
        string result = $"callback:name:\"result=succ&id={persId}&status=ok&login={playerName}&pers_id={persId}&mailru=1&rts=0&req_time={reqTime}&sign={sign}&server_id={serverId}&new_agreement=false\"";

        Console.WriteLine($"[Auth] auth.response -> playerName={playerName}, pers_id={persId}");
        return result;
    }

    private string HandleLoginAction(string query)
    {
        Console.WriteLine($"[Auth] login.action params: {query}");
        string result = "name:\"result=succ&status=ok&login=Player\"";
        Console.WriteLine($"[Auth] login.response -> result=succ");
        return result;
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
    public AccountRepository?  AdminAccountRepo;
    public TankRepository?     AdminTankRepo;
    public CorpsRepository?    AdminCorpsRepo;
    public GameTablesService?  AdminGameTables;

    // ── Admin endpoint ────────────────────────────────────────────────────────
    // GET http://127.0.0.1:18080/admin?cmd=...&player=...
    private string HandleAdminAction(string query)
    {
        if (AdminAccountRepo == null) return "ERR:Admin repos not wired";
        var p      = ParseQueryString(query);
        string cmd = p["cmd"]    ?? "";
        string pl  = p["player"] ?? "";

        var account = AdminAccountRepo.FindByPlayerName(pl);
        if (account == null) return "ERR:Player not found";

        try
        {
            switch (cmd)
            {
                case "set_gold":
                    int gold = int.Parse(p["amount"] ?? "0");
                    AdminAccountRepo.UpdateResources(account.PlayerGUID, gold: gold);
                    return $"Золото → {gold:N0}";

                case "set_silver":
                    int silver = int.Parse(p["amount"] ?? "0");
                    AdminAccountRepo.UpdateResources(account.PlayerGUID, silver: silver);
                    return $"Серебро → {silver:N0}";

                case "set_exp":
                    int exp = int.Parse(p["amount"] ?? "0");
                    AdminAccountRepo.UpdateResources(account.PlayerGUID, experience: exp);
                    return $"Свободный опыт → {exp:N0}";

                case "set_loyal":
                    int loyal = int.Parse(p["amount"] ?? "0");
                    var cid = AdminCorpsRepo?.GetPlayerCorpsGUID(account.PlayerGUID);
                    if (!cid.HasValue) return "ERR:Нет легиона";
                    var m   = AdminCorpsRepo!.GetMember(cid.Value, account.PlayerGUID);
                    int diff = loyal - (m?.LoyalPoint ?? 0);
                    if (diff != 0) AdminCorpsRepo!.UpdateMemberLoyalPoint(cid.Value, account.PlayerGUID, diff);
                    return $"Лояльность → {loyal:N0}";

                case "give_tank":
                    int vid = int.Parse(p["vehicleId"] ?? "0");
                    if (AdminTankRepo == null || AdminGameTables == null) return "ERR:Нет TankRepo";
                    if (AdminTankRepo.HasTankByVehicleId(account.PlayerGUID, vid)) return "Уже есть";
                    var te = AdminGameTables.GetTank(vid);
                    if (te == null) return $"ERR:VehicleId {vid} не найден";
                    AdminTankRepo.CreateTank(account.PlayerGUID, vid,
                        te.HullId, te.UnderpanId, te.TurretId, te.MainGunId,
                        te.EngineId, te.FueltankId, te.CartridgeId,
                        te.RadioStationId, te.GaugeId, te.MaxHp);
                    return $"✓ Танк {te.Name} (id={vid}) выдан";

                case "remove_tank":
                    int rid = int.Parse(p["vehicleId"] ?? "0");
                    if (AdminTankRepo == null) return "ERR:Нет TankRepo";
                    var tank = AdminTankRepo.GetPlayerTanks(account.PlayerGUID)
                                           .FirstOrDefault(t => t.VehicleId == rid);
                    if (tank == null) return "ERR:Танк не найден";
                    AdminTankRepo.DeleteTank(tank.TankGUID);
                    return $"✓ Танк VehicleId={rid} удалён";

                case "get_info":
                    var cur = AdminAccountRepo.GetByPlayerGUID(account.PlayerGUID);
                    return $"Gold:{cur?.Gold:N0} Silver:{cur?.Silver:N0} Exp:{cur?.Experience:N0}";

                // ── v60: расширенные читы ─────────────────────────────────────────
                case "give_all_tanks":
                {
                    if (AdminTankRepo == null || AdminGameTables == null) return "ERR:Нет TankRepo";
                    int added = 0;
                    foreach (var kv in AdminGameTables.Tanks)
                    {
                        var t = kv.Value;
                        // пропускаем 24 placeholder-танка без модели (пустое имя) — иначе клиент падает
                        if (string.IsNullOrWhiteSpace(t.Name)) continue;
                        if (t.HullId <= 0 || t.MainGunId <= 0) continue;
                        if (AdminTankRepo.HasTankByVehicleId(account.PlayerGUID, t.Id)) continue;
                        AdminTankRepo.CreateTank(account.PlayerGUID, t.Id,
                            t.HullId, t.UnderpanId, t.TurretId, t.MainGunId,
                            t.EngineId, t.FueltankId, t.CartridgeId,
                            t.RadioStationId, t.GaugeId, t.MaxHp);
                        added++;
                    }
                    return $"✓ Выдано танков: {added}";
                }

                case "add_gold":
                {
                    int add = int.Parse(p["amount"] ?? "0");
                    var c = AdminAccountRepo.GetByPlayerGUID(account.PlayerGUID);
                    int ng = (c?.Gold ?? 0) + add;
                    AdminAccountRepo.UpdateResources(account.PlayerGUID, gold: ng);
                    return $"Золото +{add:N0} → {ng:N0}";
                }

                case "add_silver":
                {
                    int add = int.Parse(p["amount"] ?? "0");
                    var c = AdminAccountRepo.GetByPlayerGUID(account.PlayerGUID);
                    int ns = (c?.Silver ?? 0) + add;
                    AdminAccountRepo.UpdateResources(account.PlayerGUID, silver: ns);
                    return $"Серебро +{add:N0} → {ns:N0}";
                }

                case "add_exp":
                {
                    int add = int.Parse(p["amount"] ?? "0");
                    var c = AdminAccountRepo.GetByPlayerGUID(account.PlayerGUID);
                    int ne = (c?.Experience ?? 0) + add;
                    AdminAccountRepo.UpdateResources(account.PlayerGUID, experience: ne);
                    return $"Свободный опыт +{add:N0} → {ne:N0}";
                }

                case "max":
                    AdminAccountRepo.UpdateResources(account.PlayerGUID,
                        gold: 999_999_999, silver: 999_999_999, experience: 999_999_999);
                    return "✓ Золото/Серебро/Опыт → максимум";

                case "list_tanks":
                {
                    if (AdminTankRepo == null) return "ERR:Нет TankRepo";
                    var owned = AdminTankRepo.GetPlayerTanks(account.PlayerGUID)
                                             .Select(t => t.VehicleId).OrderBy(x => x).ToList();
                    return $"Танков: {owned.Count} — VehicleId: {string.Join(",", owned)}";
                }

                case "clear_tanks":
                {
                    if (AdminTankRepo == null) return "ERR:Нет TankRepo";
                    var all = AdminTankRepo.GetPlayerTanks(account.PlayerGUID).ToList();
                    foreach (var t in all) AdminTankRepo.DeleteTank(t.TankGUID);
                    return $"✓ Удалено танков: {all.Count}";
                }

                // ── v63 (п.3): расширенные читы ──────────────────────────────
                case "give_premium":
                {
                    int days = int.Parse(p["days"] ?? p["amount"] ?? "30");
                    AdminAccountRepo.UpdatePremiumStatus(account.PlayerGUID, true, DateTime.UtcNow.AddDays(days));
                    return $"Премиум-аккаунт выдан на {days} дн.";
                }

                case "give_vip":
                {
                    int days = int.Parse(p["days"] ?? p["amount"] ?? "30");
                    AdminAccountRepo.UpdateVipStatus(account.PlayerGUID, true, DateTime.UtcNow.AddDays(days));
                    return $"VIP выдан на {days} дн.";
                }

                case "set_level":
                {
                    int lvl = int.Parse(p["amount"] ?? "99");
                    AdminAccountRepo.UpdateLevel(account.PlayerGUID, lvl);
                    return $"Уровень = {lvl}";
                }

                case "set_battlecap":
                {
                    int bc = int.Parse(p["amount"] ?? "85");
                    AdminAccountRepo.UpdateBattleCap(account.PlayerGUID, bc);
                    return $"Ёмкость боёв = {bc}";
                }

                case "set_caserne":
                {
                    int cc = int.Parse(p["amount"] ?? "85");
                    AdminAccountRepo.UpdateCasernCapacity(account.PlayerGUID, cc);
                    return $"Ёмкость казармы = {cc}";
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
                        added++;
                    }
                    return $"Выдано танков ({(wantCur == 2 ? "премы" : "обычные")}): {added}";
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
                            added++;
                        }
                    return $"GOD: ресурсы MAX, према+VIP 10 лет, уровень 99, танков {added}";
                }

                default:
                    return $"ERR:Неизвестная команда: {cmd}";
            }
        }
        catch (Exception ex) { return $"ERR:{ex.Message}"; }
    }
}
