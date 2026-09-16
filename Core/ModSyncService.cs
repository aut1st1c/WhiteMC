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
    //  SYNC
    // ------------------------------------------------------------------- //

    /// <summary>
    /// Синхронизирует моды:
    ///  1. Докачивает Modrinth-моды (missing/mismatch).
    ///  2. Если среди unresolved-модов есть отсутствующие или с несовпавшим хэшем —
    ///     дополнительно скачивает архив из profiles.json (modpack.components["mods"])
    ///     и распаковывает из него нужные файлы.
    ///  3. Удаляет лишние jar'ы, которых нет в манифесте.
    /// </summary>
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

        // 1) Манифест
        P(0, 1, "Проверка обновлений: получение манифеста…");
        var remote = await FetchManifestAsync(manifestUrl, ct);
        var remoteByName = remote.Mods.ToDictionary(m => m.Filename, StringComparer.OrdinalIgnoreCase);

        // 2) Хэширование локальных модов
        P(0, 1, "Проверка обновлений: хеширование локальных модов…");
        var localHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var hash = await HashUtil.ComputeAsync(path, "sha512", ct);
            localHashes[Path.GetFileName(path)] = hash;
        }

        // 3) Удаляем лишние (нет в манифесте)
        foreach (var name in localHashes.Keys.ToList())
        {
            if (remoteByName.ContainsKey(name)) continue;
            try
            {
                File.Delete(Path.Combine(modsDir, name));
                localHashes.Remove(name);
                logger?.Invoke($"[Sync] − удалён {name}");
            }
            catch (Exception ex)
            {
                logger?.Invoke($"[Sync] не удалось удалить {name}: {ex.Message}");
            }
        }

        // 4) Раскладываем по источникам
        var toDownload         = new List<RemoteMod>();
        var unresolvedOnServer = new List<RemoteMod>();

        foreach (var rm in remote.Mods)
        {
            if (localHashes.TryGetValue(rm.Filename, out var localHash)
                && string.Equals(localHash, rm.Sha512, StringComparison.OrdinalIgnoreCase))
                continue;   // файл на месте и хэш совпал

            if (rm.Source == "modrinth" && !string.IsNullOrEmpty(rm.ModrinthUrl))
                toDownload.Add(rm);        // докачаем с Modrinth
            else
                unresolvedOnServer.Add(rm); // должен лежать в архиве
        }

        // 5) Качаем Modrinth-моды параллельно
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
                            throw new Exception("хеш не совпал после скачивания");

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

        // 6) Архив — если среди unresolved-модов есть отсутствующие или повреждённые.
        //    Источник архива — только profiles.json → modpack.components["mods"].
        if (unresolvedOnServer.Count > 0)
        {
            var archiveUrl = Profiles.ModsArchiveUrl(profileName);

            if (string.IsNullOrEmpty(archiveUrl))
            {
                logger?.Invoke($"[Sync] ВНИМАНИЕ: {unresolvedOnServer.Count} unresolved мод(ов), " +
                               $"но в profiles.json не задан компонент 'mods' " +
                               $"(modpack.components[\"mods\"]). Файлы не восстановить:");
                foreach (var u in unresolvedOnServer)
                    logger?.Invoke($"  - {u.Filename} ({Shorten(u.Sha512)}…)");
            }
            else
            {
                logger?.Invoke($"[Sync] Unresolved мод(ов) для восстановления: {unresolvedOnServer.Count}. " +
                               $"Качаю архив из profiles.json: {archiveUrl}");
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

        // 7) Финальная верификация: всё ли на месте?
        var stillMissing = new List<string>();
        foreach (var rm in remote.Mods)
        {
            if (!localHashes.TryGetValue(rm.Filename, out var actual)
                || !string.Equals(actual, rm.Sha512, StringComparison.OrdinalIgnoreCase))
            {
                stillMissing.Add(rm.Filename);
            }
        }

        if (stillMissing.Count > 0)
        {
            logger?.Invoke($"[Sync] ВНИМАНИЕ: {stillMissing.Count} мод(ов) всё ещё не на месте после синхронизации:");
            foreach (var n in stillMissing.Take(20))
                logger?.Invoke($"  - {n}");
        }

        // 8) Сохраняем локальное состояние
        var statePath = Path.Combine(instDir, LocalStateFile);
        var state = new LocalModsState
        {
            ManifestVersion = remote.ManifestVersion,
            Mods            = new Dictionary<string, string>(localHashes)
        };
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state, Json.Indented), ct);

        // 9) Итог
        if (total == 0 && unresolvedOnServer.Count == 0 && stillMissing.Count == 0)
            P(1, 1, "Обновления не требуются");
        else
            P(1, 1, $"Синхронизация завершена: скачано {total}, " +
                    $"из архива {unresolvedOnServer.Count - stillMissing.Count}, " +
                    $"не восстановлено {stillMissing.Count}");
    }

    // ------------------------------------------------------------------- //
    //  Архив
    // ------------------------------------------------------------------- //

    /// <summary>
    /// Скачивает архив и распаковывает из него ТОЛЬКО те jar-файлы, что значатся
    /// в unresolved и сейчас отсутствуют или повреждены. Ничего лишнего не трогает.
    /// </summary>
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

            // Собираем имена файлов, реально присутствующих в архиве (в нижнем регистре
            // и без пути) — чтобы устойчиво матчить по имени, если архивист положил
            // их в подпапку.
            var archiveFiles = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var name = Path.GetFileName(entry.Name);
                if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                    archiveFiles[name] = entry;
            }

            foreach (var (fileName, rm) in wanted)
            {
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
                    logger?.Invoke($"[Архив] {fileName}: хэш не совпал, файл удалён");
                    try { File.Delete(dest); } catch { }
                    continue;
                }

                localHashes[fileName] = actual;
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