using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BuildManifest;

internal sealed class GameManifest
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

internal sealed class ManifestPackage
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

internal sealed class ManifestFileEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}

internal static class Program
{
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "-gup-",
        "showcase_level0",
        ".gwt_tmp",
    };

    private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "output_log.txt",
        "gc.log",
        "diag.xml",
        ".gwt_state.json",
        ".idx",
        "desktop.ini",
        "Thumbs.db",
        "GWT_Launcher.exe",
        "launcher_config.json",
        "launcher_config.example.json",
        "manifest.json",
    };

    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bak",
        ".orig",
        ".part",
        ".tmp",
    };

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        string? gameDir = null;
        string? outPath = null;
        string? baseUrl = null;
        string gameVersion = "1.0.0";
        string launcherVersion = "1.2.0";
        string? launcherUrl = null;
        string? serverHost = null;
        int gamePort = 9801, authPort = 18080, assetPort = 18081;
        string? packDir = null;
        string? packPrefix = null;
        string? packUrlPrefix = null;
        string? prevManifestPath = null;
        int maxPackMb = 1200;
        var compression = CompressionLevel.Fastest;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Не задано значение для {arg}");
            switch (arg)
            {
                case "--game-dir": gameDir = Next(); break;
                case "--out": outPath = Next(); break;
                case "--base-url": baseUrl = Next(); break;
                case "--game-version": gameVersion = Next(); break;
                case "--launcher-version": launcherVersion = Next(); break;
                case "--launcher-url": launcherUrl = Next(); break;
                case "--server-host": serverHost = Next(); break;
                case "--game-port": gamePort = int.Parse(Next()); break;
                case "--auth-port": authPort = int.Parse(Next()); break;
                case "--asset-port": assetPort = int.Parse(Next()); break;
                case "--pack-dir": packDir = Next(); break;
                case "--pack-prefix": packPrefix = Next(); break;
                case "--pack-url-prefix": packUrlPrefix = Next(); break;
                case "--prev-manifest": prevManifestPath = Next(); break;
                case "--max-pack-mb": maxPackMb = Math.Clamp(int.Parse(Next()), 50, 1900); break;
                case "--compression":
                    compression = Next().ToLowerInvariant() switch
                    {
                        "optimal" => CompressionLevel.Optimal,
                        "none" or "store" => CompressionLevel.NoCompression,
                        _ => CompressionLevel.Fastest,
                    };
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    return 0;
                default:
                    if (gameDir == null && !arg.StartsWith("--")) gameDir = arg;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(gameDir))
        {
            gameDir = ResolveDefaultGameDir()
                ?? throw new DirectoryNotFoundException("Укажите --game-dir \"путь к Ground War Tanks\"");
        }
        gameDir = Path.GetFullPath(gameDir);
        if (!Directory.Exists(gameDir))
            throw new DirectoryNotFoundException($"Папка игры не найдена: {gameDir}");

        outPath ??= Path.Combine(gameDir, "manifest.json");
        outPath = Path.GetFullPath(outPath);

        var allFiles = Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories)
            .Select(full => (full, rel: Path.GetRelativePath(gameDir, full).Replace('\\', '/')))
            .Where(x => !ShouldExclude(x.rel, x.full, outPath))
            .OrderBy(x => x.rel, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"Сканирую {gameDir} -> найдено файлов: {allFiles.Count}");
        var results = new ConcurrentBag<ManifestFileEntry>();
        int done = 0;

        await Parallel.ForEachAsync(allFiles, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
        }, async (item, ct) =>
        {
            var fi = new FileInfo(item.full);
            using var sha = SHA256.Create();
            await using var fs = new FileStream(item.full, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);
            byte[] hash = await sha.ComputeHashAsync(fs, ct);

            results.Add(new ManifestFileEntry
            {
                Path = item.rel,
                Size = fi.Length,
                Sha256 = Convert.ToHexString(hash).ToLowerInvariant(),
            });

            int n = Interlocked.Increment(ref done);
            if (n % 250 == 0 || n == allFiles.Count)
            {
                Console.WriteLine($"  хешировано {n}/{allFiles.Count}...");
            }
        });

        var manifest = new GameManifest
        {
            LauncherVersion = launcherVersion,
            LauncherUrl = string.IsNullOrWhiteSpace(launcherUrl) ? null : launcherUrl.Trim(),
            GameVersion = gameVersion,
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim(),
            ServerHost = string.IsNullOrWhiteSpace(serverHost) ? null : serverHost.Trim(),
            GamePort = gamePort,
            AuthPort = authPort,
            AssetPort = assetPort,
            Files = results.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(),
        };

        if (!string.IsNullOrWhiteSpace(packDir))
        {
            packDir = Path.GetFullPath(packDir);
            Directory.CreateDirectory(packDir);
            await BuildPackagesAsync(
                manifest,
                gameDir,
                packDir,
                packPrefix,
                packUrlPrefix ?? baseUrl,
                prevManifestPath,
                maxPackMb * 1024L * 1024L,
                compression);
        }

        var outDir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(manifest, opts), Encoding.UTF8);

        double totalMb = manifest.Files.Sum(f => f.Size) / (1024.0 * 1024.0);
        Console.WriteLine($"Готово: {outPath}");
        Console.WriteLine($"Файлов: {manifest.Files.Count}, общий размер: {totalMb:F1} МБ");
        if (manifest.Packages.Count > 0)
        {
            double packMb = manifest.Packages.Sum(p => p.Size) / (1024.0 * 1024.0);
            Console.WriteLine($"Пакетов: {manifest.Packages.Count}, суммарно в архивах: {packMb:F1} МБ");
        }
        return 0;
    }

    private static async Task BuildPackagesAsync(
        GameManifest manifest,
        string gameDir,
        string packDir,
        string? packPrefix,
        string? packUrlPrefix,
        string? prevManifestPath,
        long maxPackBytes,
        CompressionLevel compression)
    {
        List<ManifestFileEntry> filesToPack = manifest.Files;
        bool isIncremental = !string.IsNullOrWhiteSpace(prevManifestPath);

        if (isIncremental)
        {
            prevManifestPath = Path.GetFullPath(prevManifestPath!);
            if (!File.Exists(prevManifestPath))
                throw new FileNotFoundException($"Предыдущий манифест не найден: {prevManifestPath}");

            var prevManifest = JsonSerializer.Deserialize<GameManifest>(
                await File.ReadAllTextAsync(prevManifestPath, Encoding.UTF8),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Некорректный предыдущий manifest.json");

            var prevMap = (prevManifest.Files ?? new List<ManifestFileEntry>())
                .ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

            filesToPack = manifest.Files
                .Where(f => !prevMap.TryGetValue(f.Path, out var old)
                            || old.Size != f.Size
                            || !string.Equals(old.Sha256, f.Sha256, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var currentSet = new HashSet<string>(manifest.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            var changedSet = new HashSet<string>(filesToPack.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);

            foreach (var oldPkg in prevManifest.Packages ?? new List<ManifestPackage>())
            {
                var keptFiles = oldPkg.Files
                    .Where(p => currentSet.Contains(p) && !changedSet.Contains(p))
                    .ToList();
                if (keptFiles.Count > 0)
                {
                    manifest.Packages.Add(new ManifestPackage
                    {
                        Name = oldPkg.Name,
                        Url = oldPkg.Url,
                        Size = oldPkg.Size,
                        Sha256 = oldPkg.Sha256,
                        Files = keptFiles,
                    });
                }
            }

            Console.WriteLine($"Инкрементальный патч: изменилось/добавилось {filesToPack.Count} из {manifest.Files.Count} файлов.");
        }

        if (filesToPack.Count == 0)
        {
            Console.WriteLine("Изменённых файлов для нового пакета нет.");
            return;
        }

        string effectivePrefix = !string.IsNullOrWhiteSpace(packPrefix)
            ? packPrefix.Trim()
            : (isIncremental ? $"gwt_patch_{SanitizeTag(manifest.GameVersion)}" : "gwt_pack");

        var chunks = new List<List<ManifestFileEntry>>();
        var currentChunk = new List<ManifestFileEntry>();
        long currentChunkBytes = 0;

        foreach (var entry in filesToPack)
        {
            if (currentChunk.Count > 0 && currentChunkBytes + entry.Size > maxPackBytes)
            {
                chunks.Add(currentChunk);
                currentChunk = new List<ManifestFileEntry>();
                currentChunkBytes = 0;
            }
            currentChunk.Add(entry);
            currentChunkBytes += Math.Max(1, entry.Size);
        }
        if (currentChunk.Count > 0) chunks.Add(currentChunk);

        string? normalizedUrlPrefix = string.IsNullOrWhiteSpace(packUrlPrefix)
            ? null
            : packUrlPrefix.TrimEnd('/') + "/";

        for (int i = 0; i < chunks.Count; i++)
        {
            string zipName = (chunks.Count == 1 && isIncremental)
                ? $"{effectivePrefix}.zip"
                : $"{effectivePrefix}_part{i + 1:D2}.zip";
            string zipPath = Path.Combine(packDir, zipName);
            if (File.Exists(zipPath)) File.Delete(zipPath);

            Console.WriteLine($"Создаю пакет {i + 1}/{chunks.Count}: {zipName} ({chunks[i].Count} файлов)...");
            using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var entry in chunks[i])
                {
                    string src = Path.Combine(gameDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                    archive.CreateEntryFromFile(src, entry.Path, compression);
                }
            }

            var zipInfo = new FileInfo(zipPath);
            using var sha = SHA256.Create();
            await using var readFs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);
            byte[] zipHash = await sha.ComputeHashAsync(readFs);

            manifest.Packages.Add(new ManifestPackage
            {
                Name = zipName,
                Url = normalizedUrlPrefix == null ? null : normalizedUrlPrefix + Uri.EscapeDataString(zipName),
                Size = zipInfo.Length,
                Sha256 = Convert.ToHexString(zipHash).ToLowerInvariant(),
                Files = chunks[i].Select(f => f.Path).ToList(),
            });

            Console.WriteLine($"  -> {zipName}: {zipInfo.Length / (1024.0 * 1024.0):F1} МБ");
        }
    }

    private static string SanitizeTag(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_');
        }
        return sb.Length > 0 ? sb.ToString() : "update";
    }

    private static bool ShouldExclude(string relPath, string fullPath, string outPath)
    {
        if (string.Equals(Path.GetFullPath(fullPath), outPath, StringComparison.OrdinalIgnoreCase))
            return true;

        var segments = relPath.Split('/');
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (ExcludedDirs.Contains(segments[i])) return true;
        }

        string fileName = segments[^1];
        if (ExcludedFileNames.Contains(fileName)) return true;
        string ext = Path.GetExtension(fileName);
        if (ExcludedExtensions.Contains(ext)) return true;
        return false;
    }

    private static string? ResolveDefaultGameDir()
    {
        var cur = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 6 && cur != null; i++)
        {
            var cand = Path.Combine(cur.FullName, "Ground War Tanks");
            if (File.Exists(Path.Combine(cand, "Tank.exe"))) return cand;
            if (File.Exists(Path.Combine(cur.FullName, "Tank.exe"))) return cur.FullName;
            cur = cur.Parent;
        }
        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            BuildManifest — генератор manifest.json и архивов для GitHub Releases / CDN

            Полный релиз (GitHub Releases):
              BuildManifest --game-dir "D:\Games\Ground War Tanks\Ground War Tanks" ^
                            --out "D:\release\v1.0.0\manifest.json" ^
                            --pack-dir "D:\release\v1.0.0" ^
                            --base-url "https://github.com/USER/REPO/releases/download/v1.0.0/" ^
                            --game-version "1.0.0" ^
                            --launcher-version "1.2.0" ^
                            --launcher-url "https://github.com/USER/REPO/releases/latest/download/GWT_Launcher.exe" ^
                            --server-host "YOUR_VPS_IP"

            Патч-релиз (только изменённые файлы):
              BuildManifest ... --out "D:\release\v1.0.1\manifest.json" ^
                            --pack-dir "D:\release\v1.0.1" ^
                            --prev-manifest "D:\release\v1.0.0\manifest.json" ^
                            --base-url "https://github.com/USER/REPO/releases/download/v1.0.1/" ^
                            --game-version "1.0.1"

            Доп. опции:
              --max-pack-mb 1200      Макс. размер исходных файлов на 1 архив (МБ, лимит GitHub = 2048)
              --compression fastest|optimal|none
              --pack-prefix NAME      Префикс имени архива
              --pack-url-prefix URL   Отдельный base URL для новых архивов
            """);
    }
}
