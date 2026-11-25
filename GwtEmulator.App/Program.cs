using System;
using System.IO;
using System.Threading;
using GwtEmulator.Protocol;
using GwtEmulator.Server;
using GwtEmulator.Server.Database;
using GwtEmulator.Server.Database.Repositories;
using GwtEmulator.Server.GameTables;

namespace GwtEmulator.App;

class TeeWriter : TextWriter
{
    private readonly TextWriter _console;
    private readonly TextWriter _file;
    public override System.Text.Encoding Encoding => _console.Encoding;
    public TeeWriter(TextWriter console, TextWriter file) { _console = console; _file = file; }
    public override void Write(char value) { _console.Write(value); _file.Write(value); }
    public override void Write(string value) { _console.Write(value); _file.Write(value); }
    public override void WriteLine(string value) { _console.WriteLine(value); _file.WriteLine(value); }
    public override void Flush() { _console.Flush(); _file.Flush(); }
}

class Program
{
    private static string ResolveDataDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("GWT_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(envDir))
            return Path.GetFullPath(envDir.Trim());

        var baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var norm = baseDir.Replace('\\', '/');
        if (norm.Contains("/bin/Debug/", StringComparison.OrdinalIgnoreCase)
            || norm.Contains("/bin/Release/", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(Path.Combine(baseDir, "..", "..", ".."));
        }
        return baseDir;
    }

    static void Main(string[] args)
    {
        string dataDir = ResolveDataDirectory();
        Directory.CreateDirectory(dataDir);
        var logPath = Path.Combine(dataDir, "server.log");
        TextWriter logStreamWriter;
        try
        {
            var fs = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            logStreamWriter = new StreamWriter(fs) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Warning] Failed to open server.log for writing: {ex.Message}");
            logStreamWriter = Console.Out;
        }
        var teeOut = new TeeWriter(Console.Out, logStreamWriter);
        var teeErr = new TeeWriter(Console.Error, logStreamWriter);
        Console.SetOut(teeOut);
        Console.SetError(teeErr);
        
        Console.WriteLine("========================================");
        Console.WriteLine("  Ground War Tanks - Server Emulator");
        Console.WriteLine("========================================");
        Console.WriteLine();

        MessageRegistry.Initialize();

        var dbPath = Path.Combine(dataDir, "gwt_server.db");
        Console.WriteLine($"[App] Database path: {Path.GetFullPath(dbPath)}");
        using var database = new DatabaseService(dbPath);
        var gameTables = new GameTablesService();
        gameTables.Initialize();

        var accountRepo = new AccountRepository(database);
        var moduleRepo = new ModuleRepository(database);
        var tankRepo = new TankRepository(database, gameTables, moduleRepo);
        var inventoryRepo = new InventoryRepository(database);
        var tankEquipmentRepo = new TankEquipmentRepository(database);
        var crewRepo = new CrewRepository(database);
        var corpsRepo = new CorpsRepository(database);
        var friendRepo = new FriendRepository(database);
        var tournamentRepo = new TournamentRepository(database);
        var battleStatsRepo = new BattleStatsRepository(database);
        var modeStatsRepo = new ModeStatsRepository(database);

        var matchmaking = new MatchmakingService(tankRepo, gameTables);

        var loginServer = new LoginServer(9801, accountRepo, tankRepo, moduleRepo);
        var hallServer = new HallServer(9301, database, accountRepo, tankRepo, inventoryRepo, tankEquipmentRepo, crewRepo, corpsRepo, friendRepo, tournamentRepo, matchmaking, gameTables, moduleRepo);
        var roomServer = new RoomServer(9601, database, tankRepo, battleStatsRepo, gameTables, accountRepo, crewRepo, tankEquipmentRepo, modeStatsRepo);
        var authServer = new AuthServer(18080);

        // Wire admin panel repos so /admin endpoint can give resources and tanks
        authServer.AdminAccountRepo = accountRepo;
        authServer.AdminTankRepo    = tankRepo;
        authServer.AdminCorpsRepo   = corpsRepo;
        authServer.AdminGameTables  = gameTables;

        // AssetBundles path: read from asset_root.txt (written by setup.ps1 next to
        // itself, i.e. the repo root). Check a few plausible locations since build
        // output depth can vary (Debug/Release, single-file publish, etc.).
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var assetRootCandidates = new[]
        {
            Path.Combine(baseDir, "..", "..", "..", "..", "asset_root.txt"), // repo root (net8.0/Debug/bin/GwtEmulator.App/<root>)
            Path.Combine(baseDir, "..", "..", "..", "asset_root.txt"),       // GwtEmulator.App folder
            Path.Combine(baseDir, "asset_root.txt"),                        // next to the built exe
            Path.Combine(Directory.GetCurrentDirectory(), "asset_root.txt"),// current working directory
        };
        string? assetRootConfigPath = Array.Find(assetRootCandidates, File.Exists);
        string localAssetsDir = Path.Combine(baseDir, "AssetBundles");
        string assetRootDir = Directory.Exists(localAssetsDir) ? localAssetsDir : "AssetBundles";
        if (assetRootConfigPath != null)
        {
            var configured = File.ReadAllText(assetRootConfigPath).Trim();
            if (!string.IsNullOrWhiteSpace(configured)) assetRootDir = configured;
            Console.WriteLine($"[App] asset_root.txt found at {Path.GetFullPath(assetRootConfigPath)} -> {assetRootDir}");
        }
        else
        {
            Console.WriteLine("[App] asset_root.txt not found in any of these locations:");
            foreach (var c in assetRootCandidates) Console.WriteLine($"      {Path.GetFullPath(c)}");
            Console.WriteLine("      Create it (next to setup.ps1) with the full path to your game's AssetBundles folder, e.g.:");
            Console.WriteLine(@"      D:\Ground War Tanks\AssetBundles");
        }
        var assetServer = new AssetServer(18081, assetRootDir);

        authServer.Start();
        assetServer.Start();
        loginServer.Start();
        hallServer.Start();
        roomServer.Start();

        Console.WriteLine();
        Console.WriteLine("All servers started. Press Ctrl+C to quit.");
        Console.WriteLine();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) => { cts.Cancel(); e.Cancel = true; };

        while (!cts.Token.WaitHandle.WaitOne(1000)) { }

        loginServer.Stop();
        hallServer.Stop();
        roomServer.Stop();
        authServer.Stop();
        assetServer.Stop();
        matchmaking.Stop();
    }
}
