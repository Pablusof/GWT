using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace GwtLauncher;

public sealed record UpdateCheckResult(
    GameManifest Manifest,
    bool LauncherUpdateAvailable,
    List<ManifestFileEntry> FilesToDownload,
    long TotalBytesToDownload,
    string LocalGameVersion,
    int PackagesToDownloadCount = 0);

public sealed record UpdateProgress(
    string Stage,
    string CurrentFile,
    int CompletedFiles,
    int TotalFiles,
    long CompletedBytes,
    long TotalBytes,
    double BytesPerSecond);

public sealed class LocalStateCache
{
    [JsonPropertyName("gameVersion")]
    public string GameVersion { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public Dictionary<string, CachedFileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CachedFileState
{
    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("mtime")]
    public long MtimeUtcTicks { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}

public static class UpdaterService
{
    private const string StateFileName = ".gwt_state.json";
    private const string TempSubDirName = ".gwt_tmp";

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromHours(6),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GwtLauncher/1.2");
        return client;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<GameManifest> FetchManifestAsync(string manifestUrl, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));
        using var resp = await Http.GetAsync(manifestUrl.Trim(), HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(timeoutCts.Token);
        var manifest = await JsonSerializer.DeserializeAsync<GameManifest>(stream, JsonOpts, timeoutCts.Token)
            ?? throw new InvalidDataException("Пустой или некорректный manifest.json");
        manifest.Files ??= new List<ManifestFileEntry>();
        manifest.Packages ??= new List<ManifestPackage>();
        return manifest;
    }

    public static bool IsNewerVersion(string? remote, string? current)
    {
        if (string.IsNullOrWhiteSpace(remote)) return false;
        if (Version.TryParse(remote.Trim(), out var rVer) && Version.TryParse((current ?? "0.0.0").Trim(), out var cVer))
        {
            return rVer > cVer;
        }
        return !string.Equals(remote.Trim(), (current ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(
        GameManifest manifest,
        string gameDir,
        bool forceFullHashCheck,
        IProgress<UpdateProgress>? progress,
        CancellationToken ct)
    {
        var cache = LoadStateCache(gameDir);
        var toDownload = new ConcurrentBag<ManifestFileEntry>();
        int total = manifest.Files.Count;
        int checkedCount = 0;

        var newCache = new LocalStateCache { GameVersion = cache.GameVersion };
        var cacheLock = new object();

        var parallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 6),
            CancellationToken = ct,
        };

        await Parallel.ForEachAsync(manifest.Files, parallelOpts, async (entry, token) =>
        {
            var localPath = ResolveSafePath(gameDir, entry.Path);
            bool need = true;
            string? actualHash = null;

            if (File.Exists(localPath))
            {
                var fi = new FileInfo(localPath);
                if (entry.Path.Equals("ClientConfig.tk", StringComparison.OrdinalIgnoreCase) && fi.Length > 0)
                {
                    // ClientConfig.tk is rewritten locally with the active server IP/ports before Tank.exe starts.
                    need = false;
                    actualHash = entry.Sha256;
                }
                else if (fi.Length == entry.Size)
                {
                    if (!forceFullHashCheck
                        && cache.Files.TryGetValue(entry.Path, out var cached)
                        && cached.Size == fi.Length
                        && cached.MtimeUtcTicks == fi.LastWriteTimeUtc.Ticks
                        && string.Equals(cached.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        need = false;
                        actualHash = cached.Sha256;
                    }
                    else
                    {
                        actualHash = await ComputeSha256Async(localPath, token);
                        if (string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            need = false;
                            lock (cacheLock)
                            {
                                newCache.Files[entry.Path] = new CachedFileState
                                {
                                    Size = fi.Length,
                                    MtimeUtcTicks = fi.LastWriteTimeUtc.Ticks,
                                    Sha256 = actualHash,
                                };
                            }
                        }
                    }
                }
            }

            if (!need && actualHash != null)
            {
                var fi = new FileInfo(localPath);
                lock (cacheLock)
                {
                    newCache.Files[entry.Path] = new CachedFileState
                    {
                        Size = fi.Length,
                        MtimeUtcTicks = fi.LastWriteTimeUtc.Ticks,
                        Sha256 = actualHash,
                    };
                }
            }
            else
            {
                toDownload.Add(entry);
            }

            int done = Interlocked.Increment(ref checkedCount);
            if (done == total || done % 25 == 0)
            {
                progress?.Report(new UpdateProgress(
                    "Проверка файлов...",
                    entry.Path,
                    done,
                    total,
                    done,
                    Math.Max(1, total),
                    0));
            }
        });

        var list = toDownload.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
        {
            newCache.GameVersion = manifest.GameVersion;
        }
        SaveStateCache(gameDir, newCache);

        var plan = BuildDownloadPlan(manifest, list);
        long totalBytes = list.Count == 0 ? 0 : plan.TotalBytes;
        bool launcherUpd = IsNewerVersion(manifest.LauncherVersion, LauncherSettings.CurrentLauncherVersion)
                           && !string.IsNullOrWhiteSpace(manifest.LauncherUrl);

        return new UpdateCheckResult(
            manifest,
            launcherUpd,
            list,
            totalBytes,
            string.IsNullOrWhiteSpace(cache.GameVersion) ? "не установлена" : cache.GameVersion,
            plan.Packages.Count);
    }

    private sealed record DownloadPlan(
        List<ManifestPackage> Packages,
        List<ManifestFileEntry> DirectFiles,
        long TotalBytes);

    private static DownloadPlan BuildDownloadPlan(GameManifest manifest, List<ManifestFileEntry> files)
    {
        if (files.Count == 0)
            return new DownloadPlan(new List<ManifestPackage>(), new List<ManifestFileEntry>(), 0);

        var packages = manifest.Packages ?? new List<ManifestPackage>();
        var neededMap = files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var remaining = new HashSet<string>(neededMap.Keys, StringComparer.OrdinalIgnoreCase);
        var selectedIndices = new HashSet<int>();

        while (remaining.Count > 0 && packages.Count > 0)
        {
            int bestIdx = -1;
            double bestCostPerByte = double.MaxValue;
            int bestCount = 0;

            for (int i = 0; i < packages.Count; i++)
            {
                if (selectedIndices.Contains(i)) continue;
                var pkg = packages[i];
                if (pkg.Files == null || pkg.Files.Count == 0) continue;

                long coveredBytes = 0;
                int coveredCount = 0;
                foreach (var p in pkg.Files)
                {
                    if (remaining.Contains(p) && neededMap.TryGetValue(p, out var mf))
                    {
                        coveredCount++;
                        coveredBytes += Math.Max(1, mf.Size);
                    }
                }
                if (coveredCount == 0) continue;

                long pkgSize = Math.Max(1, pkg.Size);
                double costPerByte = pkgSize / (double)coveredBytes;
                if (costPerByte < bestCostPerByte ||
                    (Math.Abs(costPerByte - bestCostPerByte) < 1e-6 && coveredCount > bestCount))
                {
                    bestCostPerByte = costPerByte;
                    bestCount = coveredCount;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0) break;
            selectedIndices.Add(bestIdx);
            foreach (var p in packages[bestIdx].Files)
            {
                remaining.Remove(p);
            }
        }

        // Preserve manifest package order so newer patch packs extract after older base packs.
        var selectedPackages = selectedIndices.OrderBy(i => i).Select(i => packages[i]).ToList();
        var directFiles = files.Where(f => remaining.Contains(f.Path)).ToList();
        long totalBytes = selectedPackages.Sum(p => Math.Max(1, p.Size)) + directFiles.Sum(f => Math.Max(1, f.Size));
        return new DownloadPlan(selectedPackages, directFiles, totalBytes);
    }

    public static async Task DownloadUpdatesAsync(
        GameManifest manifest,
        string manifestUrl,
        List<ManifestFileEntry> files,
        string gameDir,
        IProgress<UpdateProgress>? progress,
        CancellationToken ct)
    {
        if (files.Count == 0) return;

        Directory.CreateDirectory(gameDir);
        var cache = LoadStateCache(gameDir);
        var cacheLock = new object();

        string baseUrl = ResolveBaseUrl(manifest, manifestUrl);
        var plan = BuildDownloadPlan(manifest, files);
        long totalBytes = Math.Max(1, plan.TotalBytes);
        long downloadedBytes = 0;
        int totalUnits = plan.Packages.Count + plan.DirectFiles.Count;
        int completedUnits = 0;
        var sw = Stopwatch.StartNew();
        long lastReportMs = 0;
        var reportLock = new object();

        var neededMap = files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var pendingFromPackages = new HashSet<string>(
            files.Where(f => !plan.DirectFiles.Contains(f)).Select(f => f.Path),
            StringComparer.OrdinalIgnoreCase);

        if (plan.Packages.Count > 0)
        {
            string tempDir = Path.Combine(gameDir, TempSubDirName);
            Directory.CreateDirectory(tempDir);
            try
            {
                for (int idx = 0; idx < plan.Packages.Count; idx++)
                {
                    ct.ThrowIfCancellationRequested();
                    var pkg = plan.Packages[idx];
                    string pkgUrl = ResolvePackageUrl(pkg, baseUrl);
                    string safeZipName = Path.GetFileName(pkg.Name);
                    if (string.IsNullOrWhiteSpace(safeZipName)) safeZipName = $"pack_{idx + 1}.zip";
                    string zipPath = Path.Combine(tempDir, safeZipName + ".part");

                    string dlStage = $"Скачивание пакета {idx + 1}/{plan.Packages.Count}:";
                    using (var resp = await Http.GetAsync(pkgUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        resp.EnsureSuccessStatusCode();
                        await using var netStream = await resp.Content.ReadAsStreamAsync(ct);
                        await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, useAsync: true);
                        byte[] buf = new byte[131072];
                        int read;
                        while ((read = await netStream.ReadAsync(buf, ct)) > 0)
                        {
                            await fs.WriteAsync(buf.AsMemory(0, read), ct);
                            long nowBytes = Interlocked.Add(ref downloadedBytes, read);

                            long elapsedMs = sw.ElapsedMilliseconds;
                            bool shouldReport = false;
                            lock (reportLock)
                            {
                                if (elapsedMs - lastReportMs >= 80)
                                {
                                    lastReportMs = elapsedMs;
                                    shouldReport = true;
                                }
                            }
                            if (shouldReport)
                            {
                                double sec = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                                progress?.Report(new UpdateProgress(
                                    dlStage,
                                    safeZipName,
                                    completedUnits,
                                    totalUnits,
                                    Math.Min(nowBytes, totalBytes),
                                    totalBytes,
                                    nowBytes / sec));
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(pkg.Sha256))
                    {
                        progress?.Report(new UpdateProgress(
                            $"Проверка архива {idx + 1}/{plan.Packages.Count}:",
                            safeZipName,
                            completedUnits,
                            totalUnits,
                            Math.Min(Volatile.Read(ref downloadedBytes), totalBytes),
                            totalBytes,
                            0));
                        string zipHash = await ComputeSha256Async(zipPath, ct);
                        if (!string.Equals(zipHash, pkg.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(zipPath); } catch { }
                            throw new IOException($"Контрольная сумма архива {safeZipName} не совпала.");
                        }
                    }

                    var pkgAllowed = new HashSet<string>(pkg.Files, StringComparer.OrdinalIgnoreCase);
                    int extractedCount = 0;
                    using (var archive = ZipFile.OpenRead(zipPath))
                    {
                        var entriesToExtract = archive.Entries
                            .Where(e => !string.IsNullOrEmpty(e.Name))
                            .ToList();

                        foreach (var ze in entriesToExtract)
                        {
                            ct.ThrowIfCancellationRequested();
                            string rel = ze.FullName.Replace('\\', '/').TrimStart('/');
                            if (!neededMap.TryGetValue(rel, out var mf)) continue;
                            if (pkgAllowed.Count > 0 && !pkgAllowed.Contains(rel)) continue;

                            string targetPath = ResolveSafePath(gameDir, mf.Path);
                            string? targetDir = Path.GetDirectoryName(targetPath);
                            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

                            string partPath = targetPath + ".part";
                            await using (var entryStream = ze.Open())
                            await using (var outFs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                            {
                                await entryStream.CopyToAsync(outFs, ct);
                            }

                            string actual = string.IsNullOrWhiteSpace(mf.Sha256)
                                ? string.Empty
                                : await ComputeSha256Async(partPath, ct);

                            File.Move(partPath, targetPath, overwrite: true);

                            if (string.IsNullOrWhiteSpace(mf.Sha256) ||
                                string.Equals(actual, mf.Sha256, StringComparison.OrdinalIgnoreCase))
                            {
                                var fi = new FileInfo(targetPath);
                                lock (cacheLock)
                                {
                                    cache.Files[mf.Path] = new CachedFileState
                                    {
                                        Size = fi.Length,
                                        MtimeUtcTicks = fi.LastWriteTimeUtc.Ticks,
                                        Sha256 = mf.Sha256,
                                    };
                                }
                                pendingFromPackages.Remove(mf.Path);
                            }

                            extractedCount++;
                            if (extractedCount == 1 || extractedCount % 40 == 0 || extractedCount == entriesToExtract.Count)
                            {
                                progress?.Report(new UpdateProgress(
                                    $"Распаковка {safeZipName} ({extractedCount}):",
                                    mf.Path,
                                    completedUnits,
                                    totalUnits,
                                    Math.Min(Volatile.Read(ref downloadedBytes), totalBytes),
                                    totalBytes,
                                    0));
                            }
                        }
                    }

                    try { File.Delete(zipPath); } catch { }
                    SaveStateCache(gameDir, cache);

                    completedUnits++;
                    double totalSec = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                    progress?.Report(new UpdateProgress(
                        "Обновление:",
                        safeZipName,
                        completedUnits,
                        totalUnits,
                        Math.Min(Volatile.Read(ref downloadedBytes), totalBytes),
                        totalBytes,
                        Volatile.Read(ref downloadedBytes) / totalSec));
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
                }
                catch { /* best effort */ }
            }

            if (pendingFromPackages.Count > 0)
            {
                string firstMissing = pendingFromPackages.First();
                throw new IOException($"После распаковки пакетов не удалось подтвердить {pendingFromPackages.Count} ф. (например, {firstMissing}).");
            }
        }

        if (plan.DirectFiles.Count > 0)
        {
            var parallelOpts = new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = ct,
            };

            await Parallel.ForEachAsync(plan.DirectFiles, parallelOpts, async (entry, token) =>
            {
                string targetPath = ResolveSafePath(gameDir, entry.Path);
                string? dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                string tempPath = targetPath + ".part";
                string url = ResolveFileUrl(entry, baseUrl);

                using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var netStream = await resp.Content.ReadAsStreamAsync(token);
                    await using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    byte[] buf = new byte[65536];
                    int read;
                    while ((read = await netStream.ReadAsync(buf, token)) > 0)
                    {
                        await fs.WriteAsync(buf.AsMemory(0, read), token);
                        long nowBytes = Interlocked.Add(ref downloadedBytes, read);

                        long elapsedMs = sw.ElapsedMilliseconds;
                        bool shouldReport = false;
                        lock (reportLock)
                        {
                            if (elapsedMs - lastReportMs >= 80)
                            {
                                lastReportMs = elapsedMs;
                                shouldReport = true;
                            }
                        }
                        if (shouldReport)
                        {
                            double sec = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                            progress?.Report(new UpdateProgress(
                                "Скачивание:",
                                entry.Path,
                                Volatile.Read(ref completedUnits),
                                totalUnits,
                                Math.Min(nowBytes, totalBytes),
                                totalBytes,
                                nowBytes / sec));
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(entry.Sha256))
                {
                    string actual = await ComputeSha256Async(tempPath, token);
                    if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(tempPath); } catch { }
                        throw new IOException($"Контрольная сумма не совпала для файла {entry.Path}");
                    }
                }

                File.Move(tempPath, targetPath, overwrite: true);
                var fi = new FileInfo(targetPath);
                lock (cacheLock)
                {
                    cache.Files[entry.Path] = new CachedFileState
                    {
                        Size = fi.Length,
                        MtimeUtcTicks = fi.LastWriteTimeUtc.Ticks,
                        Sha256 = entry.Sha256,
                    };
                }

                int done = Interlocked.Increment(ref completedUnits);
                double totalSec = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                progress?.Report(new UpdateProgress(
                    "Скачивание:",
                    entry.Path,
                    done,
                    totalUnits,
                    Math.Min(Volatile.Read(ref downloadedBytes), totalBytes),
                    totalBytes,
                    Volatile.Read(ref downloadedBytes) / totalSec));
            });
        }

        cache.GameVersion = manifest.GameVersion;
        SaveStateCache(gameDir, cache);
    }

    private static string ResolvePackageUrl(ManifestPackage pkg, string baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(pkg.Url)) return pkg.Url.Trim();
        var parts = pkg.Name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var escaped = string.Join('/', parts.Select(Uri.EscapeDataString));
        return baseUrl + escaped;
    }

    public static async Task ApplyLauncherSelfUpdateAsync(string launcherUrl, CancellationToken ct)
    {
        var currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь запущенного лаунчера.");
        if (!currentExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Самообновление доступно только для собранного .exe файла.");
        }

        string dir = Path.GetDirectoryName(currentExe)!;
        string stagedExe = Path.Combine(dir, Path.GetFileName(currentExe) + ".new");

        using (var resp = await Http.GetAsync(launcherUrl.Trim(), HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var netStream = await resp.Content.ReadAsStreamAsync(ct);
            await using var fs = new FileStream(stagedExe, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await netStream.CopyToAsync(fs, ct);
        }

        string batPath = Path.Combine(Path.GetTempPath(), $"gwt_launcher_update_{Environment.ProcessId}.cmd");
        string script = $"""
            @echo off
            chcp 65001 > nul
            timeout /t 1 /nobreak > nul
            :retry
            move /y "{stagedExe}" "{currentExe}" > nul 2>&1
            if errorlevel 1 (
                timeout /t 1 /nobreak > nul
                goto retry
            )
            start "" "{currentExe}"
            del "%~f0"
            """;
        File.WriteAllText(batPath, script, new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{batPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }

    public static async Task<(bool ok, string message)> SyncNicknameAsync(
        string host, int authPort, string persId, string nickname, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(1600));
            string url =
                $"http://{host.Trim()}:{authPort}/TK/set_nick?persId={Uri.EscapeDataString(persId.Trim())}&nick={Uri.EscapeDataString(nickname.Trim())}";
            using var resp = await Http.GetAsync(url, timeoutCts.Token);
            string body = (await resp.Content.ReadAsStringAsync(timeoutCts.Token)).Trim();
            if (body.StartsWith("OK:", StringComparison.OrdinalIgnoreCase))
                return (true, body[3..].Trim());
            if (body.StartsWith("ERR:", StringComparison.OrdinalIgnoreCase))
                return (false, body[4..].Trim());
            return (resp.IsSuccessStatusCode, body);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static async Task<string> AdminRequestAsync(
        string host, int authPort, string cmd, string? playerName = null,
        Dictionary<string, string>? extraParams = null, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        var sb = new StringBuilder($"http://{host.Trim()}:{authPort}/admin?cmd={Uri.EscapeDataString(cmd)}");
        if (!string.IsNullOrWhiteSpace(playerName))
            sb.Append("&player=").Append(Uri.EscapeDataString(playerName.Trim()));
        if (extraParams != null)
        {
            foreach (var kv in extraParams)
            {
                sb.Append('&').Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value));
            }
        }
        using var resp = await Http.GetAsync(sb.ToString(), timeoutCts.Token);
        return await resp.Content.ReadAsStringAsync(timeoutCts.Token);
    }

    private static string ResolveBaseUrl(GameManifest manifest, string manifestUrl)
    {
        if (!string.IsNullOrWhiteSpace(manifest.BaseUrl))
            return manifest.BaseUrl.TrimEnd('/') + "/";
        var uri = new Uri(manifestUrl.Trim());
        var idx = uri.AbsoluteUri.LastIndexOf('/');
        return idx >= 0 ? uri.AbsoluteUri[..(idx + 1)] : uri.AbsoluteUri + "/";
    }

    private static string ResolveFileUrl(ManifestFileEntry entry, string baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(entry.Url)) return entry.Url.Trim();
        var parts = entry.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var escaped = string.Join('/', parts.Select(Uri.EscapeDataString));
        return baseUrl + escaped;
    }

    private static string ResolveSafePath(string rootDir, string relativePath)
    {
        string normalizedRoot = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;
        string cleanRel = relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        string combined = Path.GetFullPath(Path.Combine(normalizedRoot, cleanRel));
        if (!combined.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Недопустимый путь в манифесте: {relativePath}");
        return combined;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        byte[] hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static LocalStateCache LoadStateCache(string gameDir)
    {
        try
        {
            string p = Path.Combine(gameDir, StateFileName);
            if (!File.Exists(p)) return new LocalStateCache();
            return JsonSerializer.Deserialize<LocalStateCache>(File.ReadAllText(p, Encoding.UTF8), JsonOpts)
                ?? new LocalStateCache();
        }
        catch
        {
            return new LocalStateCache();
        }
    }

    private static void SaveStateCache(string gameDir, LocalStateCache cache)
    {
        try
        {
            if (!Directory.Exists(gameDir)) Directory.CreateDirectory(gameDir);
            string p = Path.Combine(gameDir, StateFileName);
            File.WriteAllText(p, JsonSerializer.Serialize(cache, JsonOpts), Encoding.UTF8);
        }
        catch { /* ignore */ }
    }
}
