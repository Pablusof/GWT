using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GwtLauncher;

public sealed class GameManifest
{
    [JsonPropertyName("launcherVersion")]
    public string LauncherVersion { get; set; } = "1.2.0";

    [JsonPropertyName("launcherUrl")]
    public string? LauncherUrl { get; set; }

    [JsonPropertyName("gameVersion")]
    public string GameVersion { get; set; } = "1.0.0";

    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("serverHost")]
    public string? ServerHost { get; set; }

    [JsonPropertyName("gamePort")]
    public int GamePort { get; set; } = 9801;

    [JsonPropertyName("authPort")]
    public int AuthPort { get; set; } = 18080;

    [JsonPropertyName("assetPort")]
    public int AssetPort { get; set; } = 18081;

    [JsonPropertyName("packages")]
    public List<ManifestPackage> Packages { get; set; } = new();

    [JsonPropertyName("files")]
    public List<ManifestFileEntry> Files { get; set; } = new();
}

public sealed class ManifestPackage
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<string> Files { get; set; } = new();
}

public sealed class ManifestFileEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class ShippedLauncherConfig
{
    [JsonPropertyName("manifestUrl")]
    public string? ManifestUrl { get; set; }

    [JsonPropertyName("serverHost")]
    public string? ServerHost { get; set; }

    [JsonPropertyName("gamePort")]
    public int GamePort { get; set; } = 9801;

    [JsonPropertyName("authPort")]
    public int AuthPort { get; set; } = 18080;

    [JsonPropertyName("assetPort")]
    public int AssetPort { get; set; } = 18081;

    [JsonPropertyName("useLocalServer")]
    public bool? UseLocalServer { get; set; }
}

public sealed class LauncherSettings
{
    public const string DefaultManifestUrl = "https://github.com/Pablusof/GWT/releases/latest/download/manifest.json";
    public const string DefaultOnlineServerHost = "26.226.103.84";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public string ManifestUrl { get; set; } = string.Empty;
    public string GameDir { get; set; } = string.Empty;
    public string ServerHost { get; set; } = "127.0.0.1";
    public int GamePort { get; set; } = 9801;
    public int AuthPort { get; set; } = 18080;
    public int AssetPort { get; set; } = 18081;
    public bool UseLocalServer { get; set; } = true;
    public string AccountId { get; set; } = string.Empty;
    public bool AutoStopServer { get; set; } = true;
    public bool MinimizeOnStart { get; set; } = false;
    public bool RotateShowcase { get; set; } = true;

    public static string CurrentLauncherVersion
    {
        get
        {
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            return ver == null ? "1.1.0" : $"{ver.Major}.{ver.Minor}.{Math.Max(0, ver.Build)}";
        }
    }

    public static string SettingsFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GwtLauncher",
            "settings.json");

    public static string DefaultOnlineInstallDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ground War Tanks");

    public static ShippedLauncherConfig? TryLoadShippedConfig()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var cfgPath = Path.Combine(baseDir, "launcher_config.json");
            if (!File.Exists(cfgPath)) return null;
            return JsonSerializer.Deserialize<ShippedLauncherConfig>(File.ReadAllText(cfgPath, Encoding.UTF8), JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public static LauncherSettings Load(string? detectedLocalGameDir, string? detectedLocalEmuDir)
    {
        var shipped = TryLoadShippedConfig();
        LauncherSettings? saved = null;
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                saved = JsonSerializer.Deserialize<LauncherSettings>(
                    File.ReadAllText(SettingsFilePath, Encoding.UTF8), JsonOpts);
            }
        }
        catch { /* fall back to defaults */ }

        var s = saved ?? new LauncherSettings();

        if (string.IsNullOrWhiteSpace(s.ManifestUrl))
        {
            if (!string.IsNullOrWhiteSpace(shipped?.ManifestUrl))
                s.ManifestUrl = shipped.ManifestUrl.Trim();
            else if (detectedLocalEmuDir == null)
                s.ManifestUrl = DefaultManifestUrl;
        }

        if (saved == null)
        {
            if (!string.IsNullOrWhiteSpace(shipped?.ServerHost))
            {
                s.ServerHost = shipped.ServerHost.Trim();
                s.GamePort = shipped.GamePort > 0 ? shipped.GamePort : 9801;
                s.AuthPort = shipped.AuthPort > 0 ? shipped.AuthPort : 18080;
                s.AssetPort = shipped.AssetPort > 0 ? shipped.AssetPort : 18081;
                s.UseLocalServer = shipped.UseLocalServer ?? IsLocalHost(s.ServerHost);
            }
            else if (detectedLocalEmuDir != null)
            {
                s.ServerHost = "127.0.0.1";
                s.UseLocalServer = true;
            }
            else
            {
                s.ServerHost = DefaultOnlineServerHost;
                s.UseLocalServer = false;
            }
        }

        if (string.IsNullOrWhiteSpace(s.GameDir))
        {
            s.GameDir = detectedLocalGameDir
                ?? (s.UseLocalServer ? Path.Combine(AppContext.BaseDirectory, "Ground War Tanks") : DefaultOnlineInstallDir);
        }

        if (string.IsNullOrWhiteSpace(s.AccountId))
        {
            // Keep existing local dev account on original local install; generate unique ID for distributed players.
            s.AccountId = detectedLocalGameDir != null && s.UseLocalServer
                ? "12345"
                : Random.Shared.Next(100_000, 9_999_999).ToString();
            s.Save();
        }

        s.RotateShowcase = true;
        return s;
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(this, JsonOpts), Encoding.UTF8);
        }
        catch { /* best effort */ }
    }

    public static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        var h = host.Trim();
        return h.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || h.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    public string BuildLaunchArgs()
    {
        var id = string.IsNullOrWhiteSpace(AccountId) ? "12345" : AccountId.Trim();
        return $"\"--sz_pers_id={id} --sz_token=tok_{id} mpop=cookie_{id} soc_auth=auth_{id} partner_id=1\"";
    }
}

public static class ClientConfigHelper
{
    public static void ApplyServerEndpoint(string gameDir, string host, int gamePort, int authPort, int assetPort)
    {
        var path = Path.Combine(gameDir, "ClientConfig.tk");
        if (!File.Exists(path)) return;

        var cleanHost = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var updated = new List<string>(lines.Length + 4);
        string section = string.Empty;
        bool wroteGameIp = false, wrotePort = false, wroteAssetIp = false, wroteAuthIp = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                section = line[1..^1].Trim();
                updated.Add(raw);
                continue;
            }

            if (section.Equals("GameServer", StringComparison.OrdinalIgnoreCase))
            {
                if (line.StartsWith("GameIP=", StringComparison.OrdinalIgnoreCase))
                {
                    updated.Add($"GameIP={cleanHost}");
                    wroteGameIp = true;
                    continue;
                }
                if (line.StartsWith("Port=", StringComparison.OrdinalIgnoreCase))
                {
                    updated.Add($"Port={gamePort}");
                    wrotePort = true;
                    continue;
                }
            }
            else if (section.Equals("AssetServer", StringComparison.OrdinalIgnoreCase) &&
                     line.StartsWith("AssetIP=", StringComparison.OrdinalIgnoreCase))
            {
                updated.Add($"AssetIP={cleanHost}:{assetPort}");
                wroteAssetIp = true;
                continue;
            }
            else if (section.Equals("AuthorizeServer", StringComparison.OrdinalIgnoreCase) &&
                     line.StartsWith("AuthorizeIP=", StringComparison.OrdinalIgnoreCase))
            {
                updated.Add($"AuthorizeIP=http://{cleanHost}:{authPort}");
                wroteAuthIp = true;
                continue;
            }

            updated.Add(raw);
        }

        if (!wroteGameIp && !wrotePort && !wroteAssetIp && !wroteAuthIp) return;
        File.WriteAllLines(path, updated, Encoding.UTF8);
    }
}
