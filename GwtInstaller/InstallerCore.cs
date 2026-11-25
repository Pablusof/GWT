using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace GwtInstaller;

public sealed class InstallOptions
{
    public string InstallDir { get; set; } = DefaultInstallDir;
    public string Nickname { get; set; } = string.Empty;
    public bool CreateDesktopShortcut { get; set; } = true;
    public bool CreateStartMenuShortcut { get; set; } = true;
    public bool LaunchAfterInstall { get; set; } = true;
    public string SettingsLocalAppDataRoot { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string DesktopDir { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public string StartMenuProgramsDir { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.Programs);

    public static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ground War Tanks");
}

public static class InstallerCore
{
    private const string LauncherExeRes = "GwtInstaller.Payload.GWT_Launcher_Player.exe";
    private const string ConfigJsonRes  = "GwtInstaller.Payload.launcher_config.json";
    private const string GameIconRes    = "GwtInstaller.Payload.GameIcon.ico";

    public static string SanitizeNickname(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var chars = raw.Trim()
            .Where(c => !char.IsControl(c) && c != '&' && c != '=' && c != '?' && c != '"' && c != '<' && c != '>' && c != '/')
            .ToArray();
        var s = new string(chars).Trim();
        return s.Length > 18 ? s[..18] : s;
    }

    public static async Task<string> RunInstallAsync(
        InstallOptions opts,
        IProgress<(int pct, string status)>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(opts.InstallDir))
            throw new ArgumentException("Укажите папку установки.");

        string targetDir = Path.GetFullPath(opts.InstallDir.Trim());
        Directory.CreateDirectory(targetDir);

        progress?.Report((10, "Подготовка папки установки..."));
        await Task.Yield();

        string launcherPath = Path.Combine(targetDir, "GWT_Launcher_Player.exe");
        string configPath   = Path.Combine(targetDir, "launcher_config.json");
        string iconPath     = Path.Combine(targetDir, "GameIcon.ico");

        progress?.Report((25, "Распаковка лаунчера (GWT_Launcher_Player.exe)..."));
        await ExtractResourceToFileAsync(LauncherExeRes, launcherPath);

        progress?.Report((65, "Запись конфигурации сервера..."));
        await ExtractResourceToFileAsync(ConfigJsonRes, configPath);
        await ExtractResourceToFileAsync(GameIconRes, iconPath);

        progress?.Report((80, "Сохранение параметров профиля и пути игры..."));
        WriteOrUpdateLauncherSettings(opts, targetDir, configPath);

        if (opts.CreateDesktopShortcut && !string.IsNullOrWhiteSpace(opts.DesktopDir))
        {
            progress?.Report((90, "Создание ярлыка на Рабочем столе..."));
            string lnk = Path.Combine(opts.DesktopDir, "Ground War Tanks.lnk");
            CreateWindowsShortcut(lnk, launcherPath, targetDir, iconPath, "Ground War Tanks — Лаунчер");
        }

        if (opts.CreateStartMenuShortcut && !string.IsNullOrWhiteSpace(opts.StartMenuProgramsDir))
        {
            progress?.Report((95, "Создание ярлыка в меню «Пуск»..."));
            string menuFolder = Path.Combine(opts.StartMenuProgramsDir, "Ground War Tanks");
            Directory.CreateDirectory(menuFolder);
            string lnk = Path.Combine(menuFolder, "Ground War Tanks.lnk");
            CreateWindowsShortcut(lnk, launcherPath, targetDir, iconPath, "Ground War Tanks — Лаунчер");
        }

        progress?.Report((100, "Установка завершена!"));
        return launcherPath;
    }

    private static async Task ExtractResourceToFileAsync(string resourceName, string destPath)
    {
        var asm = Assembly.GetExecutingAssembly();
        using Stream? src = asm.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Не найден встроенный ресурс: {resourceName}");

        string tmpPath = destPath + ".tmp";
        await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await src.CopyToAsync(fs);
        }
        File.Move(tmpPath, destPath, overwrite: true);
    }

    private static void WriteOrUpdateLauncherSettings(InstallOptions opts, string installDir, string shippedConfigPath)
    {
        string settingsDir = Path.Combine(opts.SettingsLocalAppDataRoot, "GwtLauncher");
        Directory.CreateDirectory(settingsDir);
        string settingsPath = Path.Combine(settingsDir, "settings.json");

        JsonObject obj = new();
        if (File.Exists(settingsPath))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(settingsPath, Encoding.UTF8)) is JsonObject existing)
                    obj = existing;
            }
            catch { obj = new JsonObject(); }
        }

        string serverHost = "26.226.103.84";
        string manifestUrl = "https://github.com/Pablusof/GWT/releases/latest/download/manifest.json";
        int gamePort = 9801, authPort = 18080, assetPort = 18081;

        try
        {
            if (File.Exists(shippedConfigPath) &&
                JsonNode.Parse(File.ReadAllText(shippedConfigPath, Encoding.UTF8)) is JsonObject cfg)
            {
                if (cfg["serverHost"]?.GetValue<string>() is { Length: > 0 } h) serverHost = h.Trim();
                if (cfg["manifestUrl"]?.GetValue<string>() is { Length: > 0 } m) manifestUrl = m.Trim();
                if (cfg["gamePort"]?.GetValue<int>() is > 0 and var gp) gamePort = gp;
                if (cfg["authPort"]?.GetValue<int>() is > 0 and var ap) authPort = ap;
                if (cfg["assetPort"]?.GetValue<int>() is > 0 and var sp) assetPort = sp;
            }
        }
        catch { }

        string accountId = obj["AccountId"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(accountId))
            accountId = Random.Shared.Next(100_000, 9_999_999).ToString();

        string cleanNick = SanitizeNickname(opts.Nickname);
        if (cleanNick.Length < 2)
        {
            cleanNick = obj["Nickname"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(cleanNick))
                cleanNick = $"Player_{accountId}";
        }

        obj["ManifestUrl"] = manifestUrl;
        obj["GameDir"] = installDir;
        obj["ServerHost"] = serverHost;
        obj["GamePort"] = gamePort;
        obj["AuthPort"] = authPort;
        obj["AssetPort"] = assetPort;
        obj["UseLocalServer"] = false;
        obj["AccountId"] = accountId;
        obj["Nickname"] = cleanNick;
        obj["AutoStopServer"] = false;
        obj["RotateShowcase"] = true;

        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(settingsPath, obj.ToJsonString(jsonOpts), new UTF8Encoding(false));
    }

    private static void CreateWindowsShortcut(string lnkPath, string targetExe, string workDir, string iconPath, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        object? shell = Activator.CreateInstance(shellType);
        if (shell == null) return;
        try
        {
            dynamic wsh = shell;
            dynamic sc = wsh.CreateShortcut(lnkPath);
            sc.TargetPath = targetExe;
            sc.WorkingDirectory = workDir;
            sc.Description = description;
            if (File.Exists(iconPath))
                sc.IconLocation = iconPath + ",0";
            sc.Save();
        }
        finally
        {
            if (System.Runtime.InteropServices.Marshal.IsComObject(shell))
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }
}
