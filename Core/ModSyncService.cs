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
    //  CHECK (только хэши, ничего не качает и не удаляет)
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

        logger?.Invoke($"[Проверка] Хеширование локальных модов в {modsDir}…");
        var result = await CheckInternalAsync(modsDir, remote, ct);

        logger?.Invoke(
            $"[Проверка] Итого: {result.TotalLocal} локальных / {result.TotalRemote} серверных. " +
            $"Нет: {result.Missing.Count}, повреждено: {result.Mismatched.Count}, " +
            $"unresolved: {result.Unresolved.Count}, лишних: {result.Extra.Count}");

        return result;
    }

    private static async Task<RemoteManifest> FetchManifestAsync(string manifestUrl, CancellationToken ct)
    {
        var json = await Http.GetStringAsync(manifestUrl, ct);
        return JsonSerializer.Deserialize<RemoteManifest>(json, Json.CaseInsensitive)
            ?? throw new Exception("Пустой манифест");
    }

    private static async Task<ModCheckResult> CheckInternalAsync(
        string modsDir, RemoteManifest remote, CancellationToken ct)
    {
        var remoteByName = remote.Mods.ToDictionary(m => m.Filename, StringComparer.OrdinalIgnoreCase);

        var result = new ModCheckResult
        {
            ManifestVersion = remote.ManifestVersion,
            TotalRemote     = remote.Mods.Count
        };

        var localHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var hash = await HashUtil.ComputeAsync(path, "sha512", ct);
            localHashes[Path.GetFileName(path)] = hash;
        }
        result.TotalLocal = localHashes.Count;

        foreach (var name in localHashes.Keys)
            if (!remoteByName.ContainsKey(name))
                result.Extra.Add(name);

        foreach (var rm in remote.Mods)
        {
            bool have  = localHashes.TryGetValue(rm.Filename, out var localHash);
            bool match = have && string.Equals(localHash, rm.Sha512, StringComparison.OrdinalIgnoreCase);
            if (match) continue;

            if (rm.Source == "modrinth" && !string.IsNullOrEmpty(rm.ModrinthUrl))
            {
                if (have) result.Mismatched.Add(rm);
                else      result.Missing.Add(rm);
            }
            else
            {
                result.Unresolved.Add(rm);
            }
        }

        return result;
    }

    // ------------------------------------------------------------------- //
    //  SYNC (применяет изменения)
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

        P(0, 1, "Синхронизация: получение манифеста…");
        var remote = await FetchManifestAsync(manifestUrl, ct);
        var remoteByName = remote.Mods.ToDictionary(m => m.Filename, StringComparer.OrdinalIgnoreCase);

        P(0, 1, "Синхронизация: хеширование локальных модов…");
        var localHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var hash = await HashUtil.ComputeAsync(path, "sha512", ct);
            localHashes[Path.GetFileName(path)] = hash;
        }

        // Лишние — удалить
        foreach (var name in localHashes.Keys.ToList())
        {
            if (remoteByName.ContainsKey(name)) continue;
            try
            {
                File.Delete(Path.Combine(modsDir, name));
                localHashes.Remove(name);
                logger?.Invoke($"[Синхр] − удалён {name}");
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[Синхр] не удалось удалить {name}: {ex.Message}");
            }
        }

        // Разложить по источникам
        var toDownload        = new List<RemoteMod>();
        var unresolvedOnServer = new List<RemoteMod>();

        foreach (var rm in remote.Mods)
        {
            if (localHashes.TryGetValue(rm.Filename, out var localHash)
                && string.Equals(localHash, rm.Sha512, StringComparison.OrdinalIgnoreCase))
                continue;

            if (rm.Source == "modrinth" && !string.IsNullOrEmpty(rm.ModrinthUrl))
                toDownload.Add(rm);
            else
                unresolvedOnServer.Add(rm);
        }

        // Скачивание с Modrinth
        int total = toDownload.Count;
        int done  = 0;

        if (total > 0)
        {
            P(0, total, $"Установка модов 0/{total}");

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
                            throw new Exception("хеш не совпал после скачивания");

                        lock (localHashes) { localHashes[rm.Filename] = actual; }
                        logger?.Invoke($"[Синхр] + {rm.Filename}");
                    }
                    catch (Exception ex)
                    {
                        logger?.Invoke($"[Синхр] ОШИБКА {rm.Filename}: {ex.Message}");
                        try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                        throw;
                    }
                }
                finally
                {
                    int n = Interlocked.Increment(ref done);
                    P(n, total, $"Установка модов {n}/{total}");
                    sem.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);
        }

        // Fallback-архив для unresolved
        if (unresolvedOnServer.Count > 0)
        {
            if (!string.IsNullOrEmpty(remote.ArchiveUrl))
            {
                logger?.Invoke($"[Синхр] Неопознанных: {unresolvedOnServer.Count}. Качаю архив mods.zip…");
                await DownloadArchiveFallbackAsync(
                    remote.ArchiveUrl, modsDir, unresolvedOnServer, localHashes, logger, ct);
            }
            else
            {
                logger?.Invoke($"[Синхр] ВНИМАНИЕ: {unresolvedOnServer.Count} мод(ов) " +
                               $"не опознаны и нет archive_url:");
                foreach (var u in unresolvedOnServer)
                    logger?.Invoke($"  - {u.Filename} ({u.Sha512[..16]}…)");
            }
        }

        // Сохранить локальное состояние
        var statePath = Path.Combine(instDir, LocalStateFile);
        var state = new LocalModsState
        {
            ManifestVersion = remote.ManifestVersion,
            Mods = new Dictionary<string, string>(localHashes)
        };
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state, Json.Indented), ct);

        P(1, 1, $"Синхронизация завершена: {localHashes.Count} мод(ов)");
    }

    private static async Task DownloadArchiveFallbackAsync(
        string archiveUrl, string modsDir,
        List<RemoteMod> unresolved, Dictionary<string, string> localHashes,
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

            using var zip = ZipFile.OpenRead(tmp);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var name = Path.GetFileName(entry.Name);
                if (!wanted.TryGetValue(name, out var rm)) continue;

                var dest = Path.Combine(modsDir, name);
                entry.ExtractToFile(dest, overwrite: true);

                var actual = await HashUtil.ComputeAsync(dest, "sha512", ct);
                if (!string.Equals(actual, rm.Sha512, StringComparison.OrdinalIgnoreCase))
                {
                    logger?.Invoke($"[Синхр] {name}: хеш из архива не совпал, пропускаю");
                    try { File.Delete(dest); } catch { }
                    continue;
                }
                localHashes[name] = actual;
                logger?.Invoke($"[Синхр] + {name} (из архива)");
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}