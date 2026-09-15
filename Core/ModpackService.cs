using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WhiteMC.Core;

public static partial class ModpackService
{
    // ---------------------------------------------------------------------- //
    //  Manifest handling
    // ---------------------------------------------------------------------- //

    private static JsonObject? ReadManifest(string instDir)
    {
        var p = Path.Combine(instDir, Constants.ModpackManifestFile);
        if (!File.Exists(p))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(p)) is not JsonObject node)
            {
                return null;
            }

            if (node.ContainsKey("components"))
            {
                return node;
            }

            if (node.ContainsKey("files"))
            {
                var legacy = new JsonObject
                {
                    ["__legacy"] = new JsonObject
                    {
                        ["version"] = node["version"]?.DeepClone(),
                        ["files"]   = node["files"]?.DeepClone() ?? new JsonArray()
                    }
                };
                return new JsonObject { ["components"] = legacy };
            }

            return new JsonObject { ["components"] = new JsonObject() };
        }
        catch
        {
            return null;
        }
    }

    private static void WriteManifest(string instDir, JsonObject data)
    {
        File.WriteAllText(
            Path.Combine(instDir, Constants.ModpackManifestFile),
            data.ToJsonString(Json.Indented));
    }

    // ---------------------------------------------------------------------- //
    //  Archive helpers
    // ---------------------------------------------------------------------- //

    [GeneratedRegex(@"(\d+)")]
    private static partial Regex VersionSplitRegex();

    private static string? StripCommonPrefix(IEnumerable<string> names)
    {
        var firsts = new HashSet<string>();
        foreach (var n0 in names)
        {
            var n = (n0 ?? "").Replace('\\', '/').TrimStart('/');
            if (n.Length == 0)
            {
                continue;
            }
            if (!n.Contains('/'))
            {
                return null;
            }
            firsts.Add(n.Split('/', 2)[0]);
        }

        if (firsts.Count != 1)
        {
            return null;
        }

        var only = firsts.First();
        if (Constants.WrapperBlacklist.Contains(only))
        {
            return null;
        }
        return only;
    }

    private static bool IsZip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[4];
            if (fs.Read(buf, 0, 4) < 4)
            {
                return false;
            }
            return buf[0] == 0x50 && buf[1] == 0x4B;  // "PK"
        }
        catch
        {
            return false;
        }
    }

    private static List<string> ListEntries(string archive)
    {
        var entries = new List<string>();
        if (IsZip(archive))
        {
            using var z = ZipFile.OpenRead(archive);
            foreach (var e in z.Entries)
            {
                if (!string.IsNullOrEmpty(e.Name))
                {
                    entries.Add(e.FullName);
                }
            }
            return entries;
        }

        using var fs = File.OpenRead(archive);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) != null)
        {
            if (entry.EntryType == TarEntryType.RegularFile ||
                entry.EntryType == TarEntryType.V7RegularFile)
            {
                entries.Add(entry.Name);
            }
        }
        return entries;
    }

    public static List<string> ExtractTo(string archive, string dest, string? stripPrefix)
    {
        dest = Path.GetFullPath(dest);
        var written = new List<string>();

        string? ResolveTarget(string raw, out string rel)
        {
            rel = (raw ?? "").Replace('\\', '/').TrimStart('/');
            if (stripPrefix != null && rel.StartsWith(stripPrefix + "/", StringComparison.Ordinal))
            {
                rel = rel[(stripPrefix.Length + 1)..];
            }
            if (rel.Length == 0)
            {
                return null;
            }
            var target = Path.GetFullPath(Path.Combine(dest, rel));
            if (!target.StartsWith(dest, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return target;
        }

        if (IsZip(archive))
        {
            using var z = ZipFile.OpenRead(archive);
            foreach (var e in z.Entries)
            {
                if (string.IsNullOrEmpty(e.Name))
                {
                    continue;
                }
                var target = ResolveTarget(e.FullName, out var rel);
                if (target == null)
                {
                    continue;
                }
                if (Path.GetFileName(rel) == Constants.ModpackManifestFile)
                {
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                e.ExtractToFile(target, overwrite: true);
                written.Add(rel);
            }
        }
        else
        {
            using var fs = File.OpenRead(archive);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var tar = new TarReader(gz);
            TarEntry? entry;
            while ((entry = tar.GetNextEntry()) != null)
            {
                if (entry.EntryType != TarEntryType.RegularFile &&
                    entry.EntryType != TarEntryType.V7RegularFile)
                {
                    continue;
                }
                var target = ResolveTarget(entry.Name, out var rel);
                if (target == null)
                {
                    continue;
                }
                if (Path.GetFileName(rel) == Constants.ModpackManifestFile)
                {
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var ofs = File.Create(target);
                entry.DataStream?.CopyTo(ofs);
                written.Add(rel);
            }
        }
        return written;
    }

    private static void CleanupEmptyDirs(string baseDir)
    {
        if (!Directory.Exists(baseDir))
        {
            return;
        }

        foreach (var d in Directory.EnumerateDirectories(baseDir, "*", SearchOption.AllDirectories)
                                 .OrderByDescending(d => d.Length))
        {
            try
            {
                if (d == baseDir)
                {
                    continue;
                }
                if (!Directory.EnumerateFileSystemEntries(d).Any())
                {
                    Directory.Delete(d);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    // ---------------------------------------------------------------------- //
    //  version.json
    // ---------------------------------------------------------------------- //

    public static async Task<Dictionary<string, string>> FetchRemoteVersionsAsync(
        string versionUrl,
        Action<string>? logger = null)
    {
        var result = new Dictionary<string, string>();
        try
        {
            var raw = await Http.GetStringAsync(versionUrl);
            var node = JsonNode.Parse(raw);
            JsonObject? d = null;
            if (node is JsonArray arr && arr.Count > 0 && arr[0] is JsonObject o)
            {
                d = o;
            }
            else if (node is JsonObject oo)
            {
                d = oo;
            }

            if (d == null)
            {
                logger?.Invoke("[WhiteMC] Модпак: version.json имеет неожиданный формат");
                return result;
            }

            foreach (var kv in d)
            {
                result[kv.Key] = kv.Value?.ToString() ?? "";
            }

            logger?.Invoke($"[WhiteMC] Модпак: version.json → {string.Join(", ", result.Select(k => $"{k.Key}={k.Value}"))}");
        }
        catch (Exception ex)
        {
            logger?.Invoke($"[WhiteMC] Модпак: не удалось прочитать version.json ({ex.Message})");
        }
        return result;
    }

    // ---------------------------------------------------------------------- //
    //  Download component
    // ---------------------------------------------------------------------- //

    private static async Task<string> DownloadComponentAsync(
        string profileName,
        string compName,
        string url,
        string? version,
        Action<long, long>? onBytes)
    {
        var cache = Path.Combine(Constants.ModpackCacheDir, profileName);
        Directory.CreateDirectory(cache);

        var rawBase = url.Split('/')[^1].Split('?')[0];
        if (string.IsNullOrEmpty(rawBase))
        {
            rawBase = compName + ".zip";
        }

        string fname;
        if (!string.IsNullOrEmpty(version))
        {
            var ext = Path.GetExtension(rawBase);
            if (string.IsNullOrEmpty(ext))
            {
                ext = ".zip";
            }
            fname = $"{compName}-{version}{ext}";
        }
        else
        {
            fname = rawBase;
        }

        var dest = Path.Combine(cache, fname);
        await Downloader.DownloadAsync(url, dest, onBytes);
        return dest;
    }

    // ---------------------------------------------------------------------- //
    //  Main install
    // ---------------------------------------------------------------------- //

    public static async Task<int> InstallAsync(
        string profileName,
        Action<int, int, string>? onProgress = null,
        Action<string>? logger = null,
        bool checkUpdates = false)
    {
        var cfg = Profiles.Modpack(profileName);
        if (cfg == null || cfg.Components.Count == 0)
        {
            return 0;
        }

        void P(int i, int t, string m)
        {
            onProgress?.Invoke(i, t, m);
            logger?.Invoke(m);
        }

        var instDir = InstanceManager.GetDir(profileName);

        // 1) Remote versions
        var remoteVersions = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(cfg.VersionUrl))
        {
            if (onProgress != null)
            {
                onProgress(0, 1, "Модпак: чтение version.json…");
            }
            remoteVersions = await FetchRemoteVersionsAsync(cfg.VersionUrl, logger);
        }

        // 2) Local manifest
        var manifest = ReadManifest(instDir) ?? new JsonObject { ["components"] = new JsonObject() };
        var components = manifest["components"] as JsonObject ?? new JsonObject();

        int totalFiles = 0;
        var updated = new List<string>();

        foreach (var (compName, compUrl) in cfg.Components)
        {
            var remoteVer = remoteVersions.TryGetValue(compName, out var rv) ? rv : null;
            var localState = components[compName] as JsonObject;
            var localVer = localState?["version"]?.GetValue<string>();
            var localFiles = (localState?["files"] as JsonArray)?
                .Select(n => n?.GetValue<string>() ?? "")
                .Where(s => s.Length > 0)
                .ToList()
                ?? new List<string>();

            // Broken legacy install detection
            bool isBroken = localFiles.Count > 0 && localFiles.All(f => !f.Contains('/'));

            string reason;
            if (isBroken)
            {
                reason = "переустановка после старого формата (файлы были в корне)";
            }
            else if (localFiles.Count == 0)
            {
                reason = "не установлен";
            }
            else if (!string.IsNullOrEmpty(remoteVer) && remoteVer != localVer)
            {
                reason = $"{localVer} → {remoteVer}";
            }
            else if (string.IsNullOrEmpty(remoteVer) && checkUpdates)
            {
                reason = "принудительная проверка (нет данных о версии)";
            }
            else
            {
                P(0, 1, $"Модпак [{compName}]: уже актуален ({localVer})");
                continue;
            }

            logger?.Invoke($"[WhiteMC] Модпак [{compName}]: обновление ({reason})");

            void OnBytes(long dl, long total)
            {
                string msg;
                if (total > 0)
                {
                    msg = $"Модпак [{compName}]: скачивание {100.0 * dl / total:F1}%  ({HumanSize(dl)} / {HumanSize(total)})";
                }
                else
                {
                    msg = $"Модпак [{compName}]: скачивание  ({HumanSize(dl)})";
                }
                onProgress?.Invoke(
                    (int)Math.Min(dl, int.MaxValue),
                    (int)Math.Min(total > 0 ? total : dl, int.MaxValue),
                    msg);
            }

            string arch;
            try
            {
                arch = await DownloadComponentAsync(profileName, compName, compUrl, remoteVer, OnBytes);
            }
            catch (Exception ex)
            {
                throw new Exception($"Модпак [{compName}]: не удалось скачать {compUrl}: {ex.Message}");
            }

            List<string> entries;
            try
            {
                entries = ListEntries(arch);
            }
            catch (Exception ex)
            {
                throw new Exception($"Модпак [{compName}]: не удалось прочитать архив {Path.GetFileName(arch)}: {ex.Message}");
            }

            var strip = StripCommonPrefix(entries);
            if (strip != null)
            {
                logger?.Invoke($"[WhiteMC] Модпак [{compName}]: в архиве обнаружена обёртка «{strip}/», она будет срезана");
            }

            int removed = 0;
            foreach (var rel in localFiles)
            {
                var p = Path.Combine(instDir, rel);
                try
                {
                    if (File.Exists(p))
                    {
                        File.Delete(p);
                        removed++;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            onProgress?.Invoke(0, 1, $"Модпак [{compName}]: распаковка…");
            var written = ExtractTo(arch, instDir, strip);

            JsonArray filesArr = new();
            foreach (var f in written.Distinct().Order())
            {
                filesArr.Add(f);
            }

            var newState = new JsonObject
            {
                ["version"] = remoteVer ?? localVer,
                ["files"] = filesArr
            };
            components[compName] = newState;

            totalFiles += written.Count;
            updated.Add(compName);

            logger?.Invoke($"[WhiteMC] Модпак [{compName}]: удалено {removed}, " +
                           $"распаковано {written.Count} файл(ов) → {instDir}");
            foreach (var s in written.Take(3))
            {
                logger?.Invoke($"[WhiteMC]   + {s}");
            }
            if (written.Count == 0)
            {
                logger?.Invoke($"[WhiteMC]   ВНИМАНИЕ: архив {Path.GetFileName(arch)} распаковался в 0 файлов");
            }
        }

        foreach (var sub in new[] { "mods", "config" })
        {
            CleanupEmptyDirs(Path.Combine(instDir, sub));
        }

        manifest["components"] = components;
        manifest["installed_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        WriteManifest(instDir, manifest);

        if (updated.Count == 0)
        {
            onProgress?.Invoke(1, 1, "Модпак: обновлений нет");
            return 0;
        }

        onProgress?.Invoke(1, 1, $"Модпак: обновлено {updated.Count} компонент(ов), {totalFiles} файл(ов)");
        return totalFiles;
    }

    private static string HumanSize(long n)
    {
        var v = (double)n;
        string[] units = { "Б", "КБ", "МБ", "ГБ" };
        foreach (var u in units)
        {
            if (v < 1024)
            {
                return $"{v:F1} {u}";
            }
            v /= 1024;
        }
        return $"{v:F1} ТБ";
    }
}