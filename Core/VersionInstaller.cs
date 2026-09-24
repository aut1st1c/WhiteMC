using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WhiteMC.Core;

public static class VersionInstaller
{
    private static VersionManifest? _manifestCache;

    public static async Task<VersionManifest> GetManifestAsync(bool force = false, CancellationToken ct = default)
    {
        var cacheFile = Path.Combine(Constants.LauncherDir, "version_manifest.json");

        // Возвращаем кэш в памяти только если он НЕ пустой.
        if (!force && _manifestCache != null && _manifestCache.Versions.Count > 0)
            return _manifestCache;

        // Дисковый кэш — тоже только если не пустой.
        if (!force && File.Exists(cacheFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(cacheFile, ct);
                var m = JsonSerializer.Deserialize<VersionManifest>(json, Json.CaseInsensitive);
                if (m != null && m.Versions.Count > 0)
                {
                    _manifestCache = m;
                    return m;
                }
            }
            catch
            {
                // Битый кэш — пойдём в сеть.
            }
        }

        // Скачиваем свежий манифест.
        var data = await Http.GetBytesAsync(Constants.VersionManifestUrl, ct);
        var manifest = JsonSerializer.Deserialize<VersionManifest>(
            System.Text.Encoding.UTF8.GetString(data),
            Json.CaseInsensitive)
            ?? throw new Exception("Пустой version_manifest_v2.json");

        if (manifest.Versions.Count == 0)
            throw new Exception(
                "version_manifest_v2.json не содержит версий. " +
                "Проверьте доступ к launchermeta.mojang.com.");

        Directory.CreateDirectory(Constants.LauncherDir);
        await File.WriteAllBytesAsync(cacheFile, data, ct);

        _manifestCache = manifest;
        return manifest;
    }

    public static async Task InstallAsync(
        string versionId,
        Action<int, int, string>? onProgress = null,
        Action<string>? logger = null,
        bool checkUpdates = false,
        CancellationToken ct = default,
        bool installNeoForge = true)
    {
        void P(int i, int t, string m)
        {
            onProgress?.Invoke(i, t, m);
            logger?.Invoke(m);
        }

        if (Constants.AllowedVersions.Length > 0 && !Constants.AllowedVersions.Contains(versionId))
            throw new Exception($"Версия '{versionId}' не входит в разрешённый список");

        var manifest = await GetManifestAsync(force: checkUpdates, ct);
        var vinfo = manifest.Versions.FirstOrDefault(v => v.Id == versionId)
            ?? throw new Exception($"Версия '{versionId}' не найдена в манифесте");

        var vdir = Path.Combine(Constants.VersionsDir, versionId);
        Directory.CreateDirectory(vdir);
        var vjsonPath = Path.Combine(vdir, $"{versionId}.json");

        if (checkUpdates || !File.Exists(vjsonPath))
            await Downloader.DownloadAsync(vinfo.Url, vjsonPath, ct: ct);

        var vjsonText = await File.ReadAllTextAsync(vjsonPath, ct);
        var vjson = JsonNode.Parse(vjsonText)!.AsObject();

        int requiredMajor = RequiredJavaMajor(vjson);

        // Сначала управляемая JRE лаунчера, затем системная (PATH/реестр/стандартные папки).
        var managedJava = JavaService.InstalledJavaPath(requiredMajor);
        var systemJava  = managedJava == null ? JavaService.SystemJavaPath(requiredMajor) : null;

        if (managedJava != null)
        {
            P(1, 1, $"Java {requiredMajor}: уже установлена локально");
        }
        else if (systemJava != null)
        {
            P(1, 1, $"Java {requiredMajor}: найдена на ПК — скачивание не требуется");
        }
        else
        {
            P(0, 1, $"Подходящая Java {requiredMajor} на ПК не найдена — устанавливаю…");
            await JavaService.EnsureJavaAsync(requiredMajor, onProgress, ct);
        }

        // Collect library downloads
        var libJobs = new List<(string url, string dest)>();
        var nativeJobs = new List<(string url, string dest)>();
        var libs = (vjson["libraries"]?.AsArray() ?? new JsonArray())
            .Select(n => n!.AsObject())
            .Where(l => RulesAllow(l["rules"] as JsonArray))
            .ToList();

        foreach (var lib in libs)
        {
            var art = (lib["downloads"] as JsonObject)?["artifact"] as JsonObject;
            if (art == null) continue;
            libJobs.Add((art["url"]!.GetValue<string>(),
                         Path.Combine(Constants.LibrariesDir, art["path"]!.GetValue<string>())));
        }
        foreach (var lib in libs)
        {
            var nc = NativeClassifier(lib);
            if (nc == null) continue;
            var art = (lib["downloads"] as JsonObject)!["classifiers"]!.AsObject()[nc]!.AsObject();
            nativeJobs.Add((art["url"]!.GetValue<string>(),
                            Path.Combine(Constants.NativesTmpDir,
                                         Path.GetFileName(art["path"]!.GetValue<string>()))));
        }

        var client = (vjson["downloads"] as JsonObject)?["client"] as JsonObject;
        var clientJobs = new List<(string url, string dest)>();
        if (client != null)
            clientJobs.Add((client["url"]!.GetValue<string>(),
                            Path.Combine(vdir, $"{versionId}.jar")));

        // Assets
        var assetIndex = vjson["assetIndex"] as JsonObject;
        string? idxPath = null;
        if (assetIndex != null)
        {
            idxPath = Path.Combine(Constants.AssetsDir, "indexes",
                                   assetIndex["id"]!.GetValue<string>() + ".json");
            if (!File.Exists(idxPath) || new FileInfo(idxPath).Length == 0)
                await Downloader.DownloadAsync(assetIndex["url"]!.GetValue<string>(), idxPath, ct: ct);
        }

        var assetJobs = new List<(string url, string dest)>();
        if (idxPath != null && File.Exists(idxPath))
        {
            var idx = JsonNode.Parse(await File.ReadAllTextAsync(idxPath, ct))!.AsObject();
            var objects = idx["objects"] as JsonObject;
            if (objects != null)
            {
                const string baseUrl = "https://resources.download.minecraft.net/";
                foreach (var kv in objects)
                {
                    var h = kv.Value!["hash"]!.GetValue<string>();
                    var dest = Path.Combine(Constants.AssetsDir, "objects", h[..2], h);
                    assetJobs.Add((baseUrl + h[..2] + "/" + h, dest));
                }
            }
        }

        // Скачивание по категориям. Каждая категория показывает свой счётчик n/total.
        await DownloadManyAsync(libJobs,   onProgress, logger, "Установка библиотек", ct);
        await DownloadManyAsync(nativeJobs,onProgress, logger, "Установка нативов",   ct);
        await DownloadManyAsync(clientJobs,onProgress, logger, "Установка клиента",   ct);

        // Extract natives
        var nativesDir = Path.Combine(Constants.NativesDir, versionId);
        if (nativeJobs.Count > 0)
        {
            Directory.CreateDirectory(nativesDir);
            await Task.Run(() =>
            {
                foreach (var (_, jar) in nativeJobs)
                    ExtractNatives(jar, nativesDir);
            }, ct);
        }

        if (assetJobs.Count > 0)
            await DownloadManyAsync(assetJobs, onProgress, logger, "Установка ассетов", ct);

        try { if (Directory.Exists(Constants.NativesTmpDir)) Directory.Delete(Constants.NativesTmpDir, true); }
        catch { }

        // NeoForge (может быть опциональным для профиля — тогда не ставим автоматически)
        if (installNeoForge && Constants.NeoForgeForMc.ContainsKey(versionId))
        {
            P(0, 1, $"NeoForge: проверка/установка для MC {versionId}…");
            await NeoForgeService.InstallAsync(versionId, onProgress, logger: logger, ct: ct);
        }

        P(1, 1, $"Версия {versionId} установлена");
    }

    public static int RequiredJavaMajor(JsonObject vjson)
    {
        var jv = vjson["javaVersion"] as JsonObject;
        if (jv == null) return 8;
        return jv["majorVersion"]?.GetValue<int>() ?? 8;
    }

    public static bool RulesAllow(JsonArray? rules, HashSet<string>? features = null)
    {
        features ??= new HashSet<string>();
        if (rules == null || rules.Count == 0) return true;

        bool allowed = false;
        foreach (var rn in rules)
        {
            if (rn is not JsonObject rule) continue;
            bool ok = true;

            if (rule["os"] is JsonObject os)
            {
                var osName = os["name"]?.GetValue<string>();
                if (osName != null && osName != Constants.OsName) ok = false;
                var arch = os["arch"]?.GetValue<string>();
                if (arch != null)
                {
                    var expected = Constants.OsArchBits == 32 ? "x86" : "x86_64";
                    if (arch != expected) ok = false;
                }
            }

            if (rule["features"] is JsonObject feats)
            {
                foreach (var kv in feats)
                {
                    bool expected = kv.Value?.GetValue<bool>() ?? false;
                    bool has = features.Contains(kv.Key);
                    if (has != expected) ok = false;
                }
            }

            var action = rule["action"]?.GetValue<string>();
            if (action == "allow" && ok) allowed = true;
            if (action == "disallow" && ok) return false;
        }
        return allowed;
    }

    private static string? NativeClassifier(JsonObject lib)
    {
        var classifiers = (lib["downloads"] as JsonObject)?["classifiers"] as JsonObject;
        if (classifiers == null) return null;

        var candidates = new List<string> { $"natives-{Constants.OsName}" };
        if (Constants.OsName == "windows" && Constants.OsArchBits == 32)
        {
            candidates.Insert(0, "natives-windows");
            candidates.Insert(0, "natives-windows-x86");
        }
        foreach (var name in candidates)
            if (classifiers.ContainsKey(name)) return name;

        foreach (var kv in classifiers)
            if (kv.Key.StartsWith($"natives-{Constants.OsName}", StringComparison.Ordinal))
                return kv.Key;
        return null;
    }

    private static void ExtractNatives(string jarPath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        using var z = ZipFile.OpenRead(jarPath);
        foreach (var e in z.Entries)
        {
            var name = e.FullName;
            if (name.StartsWith("META-INF/", StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(e.Name)) continue;
            var target = Path.Combine(destDir, Path.GetFileName(name));
            try { e.ExtractToFile(target, overwrite: true); } catch { }
        }
    }

    /// <summary>
    /// Параллельно скачивает список файлов. В onProgress уходит только агрегат
    /// вида "{label} {done}/{total}". Отдельные имена файлов — в logger.
    /// </summary>
    private static async Task DownloadManyAsync(
        List<(string url, string dest)> jobs,
        Action<int, int, string>? onProgress,
        Action<string>? logger,
        string label,
        CancellationToken ct)
    {
        if (jobs.Count == 0) return;

        int total = jobs.Count;

        // Отсеиваем уже существующие
        var pending = new List<(string url, string dest)>();
        foreach (var (url, dest) in jobs)
        {
            bool exists = File.Exists(dest) && new FileInfo(dest).Length > 0;
            if (!exists)
                pending.Add((url, dest));
        }

        int done = total - pending.Count;
        onProgress?.Invoke(done, total, $"{label} {done}/{total}");

        if (pending.Count == 0) return;

        var sem = new SemaphoreSlim(Constants.MaxParallelDownloads);
        var tasks = pending.Select(async job =>
        {
            await sem.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                await Downloader.DownloadAsync(job.url, job.dest, ct: ct);
                logger?.Invoke($"[{label}] {Path.GetFileName(job.dest)}");
            }
            finally
            {
                int n = Interlocked.Increment(ref done);
                onProgress?.Invoke(n, total, $"{label} {n}/{total}");
                sem.Release();
            }
        }).ToList();

        try { await Task.WhenAll(tasks); }
        catch (Exception ex)
        {
            throw new Exception($"Не удалось скачать: {ex.Message}");
        }
    }
}