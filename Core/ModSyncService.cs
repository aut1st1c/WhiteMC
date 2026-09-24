using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WhiteMC.Core;

public static class ModSyncService
{
    private const string LocalStateFile = ".whitemc_mods.json";
    private const string ModsDirName    = "mods";
    private const int    MaxParallelModDownloads = 4;

    // ------------------------------------------------------------------- //
    //  CHECK
    // ------------------------------------------------------------------- //

    public static async Task<ModCheckResult> CheckAsync(
        string profileName, string manifestUrl,
        Action<string>? logger = null, CancellationToken ct = default)
    {
        var instDir = InstanceManager.GetDir(profileName, create: false);
        if (!Directory.Exists(instDir))
            instDir = InstanceManager.GetDir(profileName);

        var modsDir = Path.Combine(instDir, ModsDirName);
        Directory.CreateDirectory(modsDir);

        logger?.Invoke($"[Проверка] Получение манифеста: {manifestUrl}");
        var remote = await FetchManifestAsync(manifestUrl, ct);

        var optionalUrl = Profiles.OptionalManifestUrl(profileName);
        var optional = string.IsNullOrWhiteSpace(optionalUrl)
            ? null
            : await OptionalModsService.LoadAsync(optionalUrl, logger, ct);

        if (optional != null)
            OptionalModsService.EnsureInitialized(profileName, optional);

        var optionalNames = OptionalNameSet(optional);

        logger?.Invoke($"[Проверка] Хеширование локальных модов в {modsDir}…");
        var result = await CheckInternalAsync(modsDir, remote, optionalNames, ct);

        logger?.Invoke(
            $"[Проверка] Итого: {result.TotalLocal} локальных / {result.TotalRemote} серверных. " +
            $"Нет: {result.Missing.Count}, повреждено: {result.Mismatched.Count}, " +
            $"unresolved: {result.Unresolved.Count}, лишних: {result.Extra.Count}, " +
            $"выключено (опц.): {result.Disabled.Count}");

        return result;
    }

    private static async Task<RemoteManifest> FetchManifestAsync(string manifestUrl, CancellationToken ct)
    {
        var json = await Http.GetStringAsync(manifestUrl, ct);
        return JsonSerializer.Deserialize<RemoteManifest>(json, Json.CaseInsensitive)
            ?? throw new Exception("Пустой манифест");
    }

    private static HashSet<string> OptionalNameSet(OptionalManifest? optional)
        => optional == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(optional.Mods.Select(m => m.Filename),
                                  StringComparer.OrdinalIgnoreCase);

    private static async Task<(Dictionary<string, string> hashes, List<string> disabled)>
        EnumerateLocalAsync(string modsDir, CancellationToken ct)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var disabled = new List<string>();

        foreach (var path in Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            hashes[Path.GetFileName(path)] = await HashUtil.ComputeAsync(path, "sha512", ct);
        }

        foreach (var path in Directory.EnumerateFiles(modsDir, "*.jar.disabled",
                                                      SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(path);
            var stripped = fileName.EndsWith(OptionalModsService.DisabledSuffix,
                                             StringComparison.OrdinalIgnoreCase)
                ? fileName[..^OptionalModsService.DisabledSuffix.Length]
                : fileName;
            hashes[stripped] = await HashUtil.ComputeAsync(path, "sha512", ct);
            disabled.Add(stripped);
        }

        return (hashes, disabled);
    }

    private static async Task<ModCheckResult> CheckInternalAsync(
        string modsDir, RemoteManifest remote, HashSet<string> optionalNames,
        CancellationToken ct)
    {
        var remoteByName = remote.Mods.ToDictionary(m => m.Filename, StringComparer.OrdinalIgnoreCase);

        var result = new ModCheckResult
        {
            ManifestVersion = remote.ManifestVersion,
            TotalRemote     = remote.Mods.Count
        };

        var (localHashes, disabled) = await EnumerateLocalAsync(modsDir, ct);
        result.TotalLocal = localHashes.Count;

        var disabledSet = new HashSet<string>(disabled, StringComparer.OrdinalIgnoreCase);

        // --- extra ---
        foreach (var name in localHashes.Keys)
        {
            if (remoteByName.ContainsKey(name) || optionalNames.Contains(name)) continue;

            if (DevFlags.SkipIntegrityCheck)
            {
                if (DevFlags.VerboseLog)
                    LogService.Log($"[DEV] extra пропущен: {name}");
                continue;
            }
            result.Extra.Add(name);
        }

        // --- required ---
        foreach (var rm in remote.Mods)
        {
            if (optionalNames.Contains(rm.Filename)) continue;

            bool present = localHashes.TryGetValue(rm.Filename, out var localHash)
                           && !disabledSet.Contains(rm.Filename);
            bool match   = present
                           && string.Equals(localHash, rm.Sha512, StringComparison.OrdinalIgnoreCase);
            if (match) continue;

            if (present && DevFlags.SkipIntegrityCheck)
            {
                if (DevFlags.VerboseLog)
                    LogService.Log($"[DEV] mismatch пропущен: {rm.Filename}");
                continue;
            }

            if (!present && DevFlags.SkipMissing)
            {
                if (DevFlags.VerboseLog)
                    LogService.Log($"[DEV] missing пропущен: {rm.Filename}");
                continue;
            }

            if (rm.Source == "modrinth" && !string.IsNullOrEmpty(rm.ModrinthUrl))
            {
                if (present) result.Mismatched.Add(rm);
                else         result.Missing.Add(rm);
            }
            else
            {
                result.Unresolved.Add(rm);
            }
        }

        foreach (var d in disabled)
            if (optionalNames.Contains(d))
                result.Disabled.Add(d);

        return result;
    }

    // ------------------------------------------------------------------- //
    //  SYNC
    // ------------------------------------------------------------------- //

    public static async Task SyncAsync(
        string profileName, string manifestUrl,
        Action<int, int, string>? onProgress = null,
        Action<string>? logger = null,
        CancellationToken ct = default)
    {
        void P(int i, int t, string m)
        {
            onProgress?.Invoke(i, t, m);
            logger?.Invoke(m);
        }

        var instDir = InstanceManager.GetDir(profileName);
        var modsDir = Path.Combine(instDir, ModsDirName);
        Directory.CreateDirectory(modsDir);

        // 0) optional
        var optionalUrl = Profiles.OptionalManifestUrl(profileName);
        var optional = string.IsNullOrWhiteSpace(optionalUrl)
            ? null
            : await OptionalModsService.LoadAsync(optionalUrl, logger, ct);

        if (optional != null)
            OptionalModsService.EnsureInitialized(profileName, optional);

        var optionalByName = optional?.Mods
            .ToDictionary(m => m.Filename, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, OptionalMod>(StringComparer.OrdinalIgnoreCase);

        // 1) remote
        P(0, 1, "Проверка обновлений: получение манифеста…");
        var remote = await FetchManifestAsync(manifestUrl, ct);
        var remoteByName = remote.Mods.ToDictionary(m => m.Filename, StringComparer.OrdinalIgnoreCase);

        // 2) hashes
        P(0, 1, "Проверка обновлений: хеширование локальных модов…");
        var (localHashes, disabledList) = await EnumerateLocalAsync(modsDir, ct);
        var disabledSet = new HashSet<string>(disabledList, StringComparer.OrdinalIgnoreCase);

        // 3) удаление лишних
        foreach (var name in localHashes.Keys.ToList())
        {
            if (remoteByName.ContainsKey(name) || optionalByName.ContainsKey(name)) continue;

            if (DevFlags.SkipIntegrityCheck)
            {
                if (DevFlags.VerboseLog)
                    logger?.Invoke($"[DEV] extra пропущен: {name}");
                continue;
            }

            try
            {
                var jarPath = Path.Combine(modsDir, name);
                var disPath = jarPath + OptionalModsService.DisabledSuffix;
                if (File.Exists(jarPath)) File.Delete(jarPath);
                if (File.Exists(disPath)) File.Delete(disPath);
                localHashes.Remove(name);
                logger?.Invoke($"[Sync] − удалён {name}");
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[Sync] не удалось удалить {name}: {ex.Message}");
            }
        }

        // 4) раскладываем по источникам
        var toDownload         = new List<RemoteMod>();
        var unresolvedOnServer = new List<RemoteMod>();

        foreach (var rm in remote.Mods)
        {
            if (optionalByName.ContainsKey(rm.Filename)) continue;

            bool present = localHashes.TryGetValue(rm.Filename, out var localHash)
                           && !disabledSet.Contains(rm.Filename);
            bool match   = present
                           && string.Equals(localHash, rm.Sha512, StringComparison.OrdinalIgnoreCase);
            if (match) continue;

            if (present && DevFlags.SkipIntegrityCheck)
            {
                if (DevFlags.VerboseLog)
                    logger?.Invoke($"[DEV] mismatch пропущен: {rm.Filename}");
                continue;
            }

            if (!present && DevFlags.SkipMissing)
            {
                if (DevFlags.VerboseLog)
                    logger?.Invoke($"[DEV] missing пропущен: {rm.Filename}");
                continue;
            }

            if (rm.Source == "modrinth" && !string.IsNullOrEmpty(rm.ModrinthUrl))
                toDownload.Add(rm);
            else
                unresolvedOnServer.Add(rm);
        }

        // 5) параллельная загрузка
        int total = toDownload.Count;
        int done  = 0;

        if (total > 0)
        {
            P(0, total, $"Загрузка модов 0/{total}");

            var sem = new SemaphoreSlim(MaxParallelModDownloads);
            var tasks = toDownload.Select(async rm =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var dest = Path.Combine(modsDir, rm.Filename);
                    try
                    {
                        await Downloader.DownloadAsync(rm.ModrinthUrl!, dest, ct: ct);

                        var actual = await HashUtil.ComputeAsync(dest, "sha512", ct);
                        if (!string.Equals(actual, rm.Sha512, StringComparison.OrdinalIgnoreCase))
                        {
                            if (DevFlags.SkipIntegrityCheck)
                            {
                                logger?.Invoke($"[DEV] {rm.Filename}: хеш не совпал, но пропущено");
                            }
                            else
                            {
                                throw new Exception("хеш не совпал после скачивания");
                            }
                        }

                        try { File.Delete(dest + OptionalModsService.DisabledSuffix); } catch { }

                        lock (localHashes) { localHashes[rm.Filename] = actual; }
                        logger?.Invoke($"[Sync] + {rm.Filename}");
                    }
                    catch (Exception ex)
                    {
                        logger?.Invoke($"[Sync] ОШИБКА {rm.Filename}: {ex.Message}");
                        try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                        throw;
                    }
                }
                finally
                {
                    int n = Interlocked.Increment(ref done);
                    P(n, total, $"Загрузка модов {n}/{total}");
                    sem.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);
        }

        // 6) архив
        if (unresolvedOnServer.Count > 0)
        {
            var archiveUrl = Profiles.ModsArchiveUrl(profileName);

            if (string.IsNullOrEmpty(archiveUrl))
            {
                logger?.Invoke($"[Sync] ВНИМАНИЕ: {unresolvedOnServer.Count} unresolved мод(ов), " +
                               $"но в profiles.json не задан компонент 'mods'.");
                foreach (var u in unresolvedOnServer)
                    logger?.Invoke($"  - {u.Filename} ({Shorten(u.Sha512)}…)");
            }
            else
            {
                logger?.Invoke($"[Sync] Unresolved: {unresolvedOnServer.Count}. Качаю архив: {archiveUrl}");
                try
                {
                    await ApplyArchiveAsync(archiveUrl, modsDir, unresolvedOnServer, localHashes, logger, ct);
                }
                catch (Exception ex)
                {
                    logger?.Invoke($"[Sync] Не удалось применить архив: {ex.Message}");
                    throw new Exception($"Не удалось скачать/распаковать архив модпака: {ex.Message}");
                }
            }
        }

        // 7) optional
        if (optional != null && optional.Mods.Count > 0)
        {
            var optionalState = OptionalModsService.LoadState(profileName);
            foreach (var om in optional.Mods)
            {
                ct.ThrowIfCancellationRequested();
                var jarPath = Path.Combine(modsDir, om.Filename);
                var disPath = jarPath + OptionalModsService.DisabledSuffix;
                bool enabled = OptionalModsService.IsEnabled(optionalState, om.Filename);

                if (!enabled)
                {
                    if (File.Exists(jarPath))
                    {
                        File.Move(jarPath, disPath, overwrite: true);
                        localHashes.Remove(om.Filename);
                        logger?.Invoke($"[Sync] опциональный выключен: {om.Filename} → .disabled");
                    }
                    continue;
                }

                if (File.Exists(disPath) && !File.Exists(jarPath))
                    File.Move(disPath, jarPath);

                if (File.Exists(jarPath)
                    && localHashes.TryGetValue(om.Filename, out var existingHash)
                    && string.Equals(existingHash, om.Sha512, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrEmpty(om.ModrinthUrl))
                {
                    logger?.Invoke($"[Sync] опциональный {om.Filename}: нет URL, пропуск");
                    continue;
                }

                logger?.Invoke($"[Sync] опциональный: скачивание {om.Filename}…");
                await Downloader.DownloadAsync(om.ModrinthUrl, jarPath, ct: ct);
                var dlHash = await HashUtil.ComputeAsync(jarPath, "sha512", ct);
                if (!string.Equals(dlHash, om.Sha512, StringComparison.OrdinalIgnoreCase))
                {
                    if (DevFlags.SkipIntegrityCheck)
                    {
                        logger?.Invoke($"[DEV] {om.Filename}: хеш не совпал, но пропущено");
                    }
                    else
                    {
                        try { File.Delete(jarPath); } catch { }
                        throw new Exception($"Опциональный мод {om.Filename}: хеш не совпал после скачивания");
                    }
                }
                try { File.Delete(disPath); } catch { }
                lock (localHashes) { localHashes[om.Filename] = dlHash; }
                logger?.Invoke($"[Sync] + опциональный {om.Filename}");
            }
        }

        // 8) финальная верификация
        var stillMissing = new List<string>();
        foreach (var rm in remote.Mods)
        {
            if (optionalByName.ContainsKey(rm.Filename)) continue;
            if (!localHashes.TryGetValue(rm.Filename, out var actual))
            {
                if (DevFlags.SkipMissing) continue;
                stillMissing.Add(rm.Filename);
                continue;
            }
            if (!string.Equals(actual, rm.Sha512, StringComparison.OrdinalIgnoreCase)
                && !DevFlags.SkipIntegrityCheck)
            {
                stillMissing.Add(rm.Filename);
            }
        }

        if (stillMissing.Count > 0)
        {
            logger?.Invoke($"[Sync] ВНИМАНИЕ: {stillMissing.Count} мод(ов) всё ещё не на месте:");
            foreach (var n in stillMissing.Take(20))
                logger?.Invoke($"  - {n}");
        }

        // 9) state
        var statePath = Path.Combine(instDir, LocalStateFile);
        var state = new LocalModsState
        {
            ManifestVersion = remote.ManifestVersion,
            Mods            = new Dictionary<string, string>(localHashes)
        };
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state, Json.Indented), ct);

        // 10) итог
        if (total == 0 && unresolvedOnServer.Count == 0 && stillMissing.Count == 0)
            P(1, 1, "Обновления не требуются");
        else
            P(1, 1, $"Синхронизация завершена: скачано {total}, " +
                    $"из архива {unresolvedOnServer.Count - stillMissing.Count}, " +
                    $"не восстановлено {stillMissing.Count}");
    }

    // ------------------------------------------------------------------- //
    //  OPTIONAL ONLY
    // ------------------------------------------------------------------- //

    public static async Task SyncOptionalOnlyAsync(
        string profileName,
        Action<int, int, string>? onProgress = null,
        Action<string>? logger = null,
        CancellationToken ct = default)
    {
        var optionalUrl = Profiles.OptionalManifestUrl(profileName);
        var optional = string.IsNullOrWhiteSpace(optionalUrl)
            ? null
            : await OptionalModsService.LoadAsync(optionalUrl, logger, ct);

        if (optional == null || optional.Mods.Count == 0)
        {
            onProgress?.Invoke(1, 1, "Опциональные моды: манифест не задан");
            return;
        }

        OptionalModsService.EnsureInitialized(profileName, optional);

        var instDir = InstanceManager.GetDir(profileName);
        var modsDir = Path.Combine(instDir, ModsDirName);
        Directory.CreateDirectory(modsDir);

        var state  = OptionalModsService.LoadState(profileName);
        int done   = 0;
        int total  = optional.Mods.Count;

        foreach (var om in optional.Mods)
        {
            ct.ThrowIfCancellationRequested();
            var jarPath = Path.Combine(modsDir, om.Filename);
            var disPath = jarPath + OptionalModsService.DisabledSuffix;
            bool enabled = OptionalModsService.IsEnabled(state, om.Filename);

            if (!enabled)
            {
                if (File.Exists(jarPath))
                {
                    File.Move(jarPath, disPath, overwrite: true);
                    logger?.Invoke($"[Sync] опциональный выключен: {om.Filename} → .disabled");
                }
            }
            else
            {
                if (File.Exists(disPath) && !File.Exists(jarPath))
                    File.Move(disPath, jarPath);

                var actual = File.Exists(jarPath)
                    ? await HashUtil.ComputeAsync(jarPath, "sha512", ct)
                    : null;

                bool needDownload = actual == null
                    || !string.Equals(actual, om.Sha512, StringComparison.OrdinalIgnoreCase);

                if (!needDownload) { done++; onProgress?.Invoke(done, total, $"Опциональные моды {done}/{total}"); continue; }

                if (actual != null && DevFlags.SkipIntegrityCheck)
                {
                    if (DevFlags.VerboseLog)
                        logger?.Invoke($"[DEV] mismatch пропущен: {om.Filename}");
                    done++;
                    onProgress?.Invoke(done, total, $"Опциональные моды {done}/{total}");
                    continue;
                }

                if (actual == null && DevFlags.SkipMissing)
                {
                    if (DevFlags.VerboseLog)
                        logger?.Invoke($"[DEV] missing пропущен: {om.Filename}");
                    done++;
                    onProgress?.Invoke(done, total, $"Опциональные моды {done}/{total}");
                    continue;
                }

                if (string.IsNullOrEmpty(om.ModrinthUrl))
                {
                    logger?.Invoke($"[Sync] опциональный {om.Filename}: нет URL, пропуск");
                    done++;
                    onProgress?.Invoke(done, total, $"Опциональные моды {done}/{total}");
                    continue;
                }

                logger?.Invoke($"[Sync] опциональный: скачивание {om.Filename}…");
                await Downloader.DownloadAsync(om.ModrinthUrl, jarPath, ct: ct);
                var dlHash = await HashUtil.ComputeAsync(jarPath, "sha512", ct);
                if (!string.Equals(dlHash, om.Sha512, StringComparison.OrdinalIgnoreCase))
                {
                    if (DevFlags.SkipIntegrityCheck)
                    {
                        logger?.Invoke($"[DEV] {om.Filename}: хеш не совпал, но пропущено");
                    }
                    else
                    {
                        try { File.Delete(jarPath); } catch { }
                        throw new Exception($"Опциональный мод {om.Filename}: хеш не совпал после скачивания");
                    }
                }
                logger?.Invoke($"[Sync] + опциональный {om.Filename}");
            }

            done++;
            onProgress?.Invoke(done, total, $"Опциональные моды {done}/{total}");
        }
    }

    // ------------------------------------------------------------------- //
    //  ARCHIVE
    // ------------------------------------------------------------------- //

    private static async Task ApplyArchiveAsync(
        string archiveUrl, string modsDir,
        List<RemoteMod> unresolved,
        Dictionary<string, string> localHashes,
        Action<string>? logger, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(),
            $"whitemc_mods_{Environment.ProcessId}_{Guid.NewGuid():N}.zip");

        try
        {
            await Downloader.DownloadAsync(archiveUrl, tmp, ct: ct);

            var wanted = new Dictionary<string, RemoteMod>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in unresolved)
                wanted[u.Filename] = u;

            int extracted = 0;
            int hashMismatch = 0;
            int notInArchive = 0;

            using var zip = ZipFile.OpenRead(tmp);

            var archiveFiles = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var name = Path.GetFileName(entry.Name);
                if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                    archiveFiles[name] = entry;
            }

            foreach (var kv in wanted)
            {
                var fileName = kv.Key;
                var rm = kv.Value;
                ct.ThrowIfCancellationRequested();

                if (!archiveFiles.TryGetValue(fileName, out var entry))
                {
                    notInArchive++;
                    logger?.Invoke($"[Архив] Нет в архиве: {fileName}");
                    continue;
                }

                var dest = Path.Combine(modsDir, fileName);
                try { entry.ExtractToFile(dest, overwrite: true); }
                catch (Exception ex)
                {
                    logger?.Invoke($"[Архив] Не удалось извлечь {fileName}: {ex.Message}");
                    continue;
                }

                var actual = await HashUtil.ComputeAsync(dest, "sha512", ct);
                if (!string.Equals(actual, rm.Sha512, StringComparison.OrdinalIgnoreCase))
                {
                    hashMismatch++;
                    if (DevFlags.SkipIntegrityCheck)
                    {
                        logger?.Invoke($"[DEV] {fileName}: хеш не совпал, но пропущено");
                        lock (localHashes) { localHashes[fileName] = actual; }
                        extracted++;
                        continue;
                    }
                    logger?.Invoke($"[Архив] {fileName}: хэш не совпал, файл удалён");
                    try { File.Delete(dest); } catch { }
                    continue;
                }

                try { File.Delete(dest + OptionalModsService.DisabledSuffix); } catch { }

                lock (localHashes) { localHashes[fileName] = actual; }
                extracted++;
                logger?.Invoke($"[Архив] + {fileName}");
            }

            logger?.Invoke($"[Архив] Итог: извлечено {extracted}, " +
                           $"не совпал хэш {hashMismatch}, нет в архиве {notInArchive}");
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static string Shorten(string s)
        => s.Length <= 16 ? s : s[..16];
}