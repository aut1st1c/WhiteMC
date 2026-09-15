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
    private const string ModsDirName = "mods";
    private const int MaxParallelModDownloads = 4;

    public static async Task SyncAsync(
        string profileName,
        string manifestUrl,
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

        // 1) Тянем серверный манифест
        P(0, 1, "Синхронизация: получение манифеста…");
        var remoteJson = await Http.GetStringAsync(manifestUrl, ct);
        var remote = JsonSerializer.Deserialize<RemoteManifest>(
            remoteJson, Json.CaseInsensitive)
            ?? throw new Exception("Пустой манифест");

        var remoteByName = remote.Mods.ToDictionary(
            m => m.Filename, StringComparer.OrdinalIgnoreCase);

        // 2) Сканируем локальные моды и считаем SHA-512
        P(0, 1, "Синхронизация: хеширование локальных модов…");
        var localHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(modsDir, "*.jar",
                                                       SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var hash = await HashUtil.ComputeAsync(path, "sha512", ct);
            localHashes[Path.GetFileName(path)] = hash;
        }

        // 3) Удаляем локальные моды, которых нет на сервере
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

        // 4) Определяем, что скачать отдельно
        var toDownload = new List<RemoteMod>();
        var unresolvedOnServer = new List<RemoteMod>();

        foreach (var rm in remote.Mods)
        {
            if (localHashes.TryGetValue(rm.Filename, out var localHash)
                && string.Equals(localHash, rm.Sha512, StringComparison.OrdinalIgnoreCase))
            {
                continue;   // хеш совпал — файл на месте
            }

            // CurseForge временно отключён: всё, что не modrinth, уходит в unresolved.
            if (rm.Source == "modrinth")
                toDownload.Add(rm);
            else
                unresolvedOnServer.Add(rm);
        }

        // 5) Качаем параллельно, до MaxParallelModDownloads одновременно
        int total = toDownload.Count;
        int done = 0;

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
                        await DownloadModAsync(rm, dest, ct);

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

        // 6) Fallback: скачиваем архив для неопознанных
        if (unresolvedOnServer.Count > 0)
        {
            if (!string.IsNullOrEmpty(remote.ArchiveUrl))
            {
                logger?.Invoke($"[Синхр] Неопознанных: {unresolvedOnServer.Count}. " +
                               $"Качаю архив mods.zip…");
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

        // 7) Сохраняем локальное состояние
        var statePath = Path.Combine(instDir, LocalStateFile);
        var state = new LocalModsState
        {
            ManifestVersion = remote.ManifestVersion,
            Mods = new Dictionary<string, string>(localHashes)
        };
        await File.WriteAllTextAsync(statePath,
            JsonSerializer.Serialize(state, Json.Indented), ct);

        P(1, 1, $"Синхронизация завершена: {localHashes.Count} мод(ов)");
    }

    // ------------------------------------------------------------------ //

    private static async Task DownloadModAsync(
        RemoteMod rm, string dest, CancellationToken ct)
    {
        // CurseForge временно отключён — см. закомментированный switch ниже.
        // string url = rm.Source switch
        // {
        //     "modrinth" => rm.ModrinthUrl
        //         ?? throw new Exception("В манифесте нет modrinth_url"),
        //     "curseforge" => await ResolveCurseForgeUrlAsync(
        //         rm.CurseforgeModId ?? throw new Exception("Нет curseforge_mod_id"),
        //         rm.CurseforgeFileId ?? throw new Exception("Нет curseforge_file_id"),
        //         ct),
        //     _ => throw new Exception($"Неизвестный источник: {rm.Source}")
        // };

        if (rm.Source != "modrinth")
            throw new Exception($"Источник «{rm.Source}» пока не поддерживается");

        var url = rm.ModrinthUrl
            ?? throw new Exception("В манифесте нет modrinth_url");

        await Downloader.DownloadAsync(url, dest, ct: ct);
    }

    // CurseForge-резолвер временно отключён.
    // private static async Task<string> ResolveCurseForgeUrlAsync(
    //     long modId, long fileId, CancellationToken ct)
    // {
    //     // Прокси на твоём сервере — API-ключ CF нельзя вшивать в клиент
    //     var proxyUrl = $"{Constants.CurseForgeProxy}/download" +
    //                    $"?modId={modId}&fileId={fileId}";
    //
    //     var json = await Http.GetStringAsync(proxyUrl, ct);
    //     var node = System.Text.Json.Nodes.JsonNode.Parse(json);
    //     var url = node?["data"]?.GetValue<string>();
    //     if (string.IsNullOrEmpty(url))
    //         throw new Exception($"CF: не удалось получить URL для {modId}/{fileId}");
    //     return url;
    // }

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