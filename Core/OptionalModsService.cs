using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WhiteMC.Core;

public static class OptionalModsService
{
    public const string StateFileName  = ".whitemc_optional.json";
    public const string DisabledSuffix = ".disabled";

    private static readonly Dictionary<string, OptionalManifest> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Lock = new();

    public static OptionalManifest? GetCached(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        lock (Lock) return Cache.TryGetValue(url!, out var m) ? m : null;
    }

    public static async Task<OptionalManifest?> LoadAsync(
        string? url, Action<string>? logger = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        lock (Lock)
        {
            if (Cache.TryGetValue(url!, out var hot)) return hot;
        }

        var cachePath = CachePathFor(url!);

        try
        {
            var json = await Http.GetStringAsync(url!, ct).ConfigureAwait(false);
            var m = JsonSerializer.Deserialize<OptionalManifest>(json, Json.CaseInsensitive);
            if (m != null)
            {
                Directory.CreateDirectory(Constants.LauncherDir);
                await File.WriteAllTextAsync(cachePath, json, ct).ConfigureAwait(false);
                lock (Lock) { Cache[url!] = m; }
                logger?.Invoke($"[WhiteMC] optional.json загружен ({m.Mods.Count} модов)");
                return m;
            }
        }
        catch (Exception ex)
        {
            logger?.Invoke($"[WhiteMC] Не удалось скачать optional.json: {ex.Message}");
        }

        if (File.Exists(cachePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(cachePath, ct).ConfigureAwait(false);
                var m = JsonSerializer.Deserialize<OptionalManifest>(json, Json.CaseInsensitive);
                if (m != null)
                {
                    lock (Lock) { Cache[url!] = m; }
                    logger?.Invoke("[WhiteMC] optional.json взят из кэша");
                    return m;
                }
            }
            catch { }
        }

        return null;
    }

    private static string CachePathFor(string url)
    {
        var hash = Convert.ToHexString(
            SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(Constants.LauncherDir, $"optional.cache.{hash[..12]}.json");
    }

    // ------------------------------------------------------------------ //
    //  Состояние
    // ------------------------------------------------------------------ //

    public static string StatePath(string profileName)
        => Path.Combine(InstanceManager.GetDir(profileName, create: false), StateFileName);

    public static OptionalState LoadState(string profileName)
    {
        try
        {
            var p = StatePath(profileName);
            if (File.Exists(p))
                return JsonSerializer.Deserialize<OptionalState>(
                           File.ReadAllText(p), Json.CaseInsensitive)
                       ?? new OptionalState();
        }
        catch { }
        return new OptionalState();
    }

    public static void SaveState(string profileName, OptionalState state)
    {
        try
        {
            var inst = InstanceManager.GetDir(profileName);
            File.WriteAllText(Path.Combine(inst, StateFileName),
                JsonSerializer.Serialize(state, Json.Indented));
        }
        catch (Exception ex)
        {
            LogService.Log($"[WhiteMC] Не удалось сохранить состояние опциональных модов: {ex.Message}");
        }
    }

    public static void EnsureInitialized(string profileName, OptionalManifest manifest)
    {
        var statePath    = StatePath(profileName);
        bool freshInstall = !File.Exists(statePath);

        var state = LoadState(profileName);
        if (state.Initialized) return;

        var disabledSet = new HashSet<string>(state.Disabled, StringComparer.OrdinalIgnoreCase);
        var enabledSet  = new HashSet<string>(state.Enabled,  StringComparer.OrdinalIgnoreCase);

        foreach (var mod in manifest.Mods)
        {
            if (disabledSet.Contains(mod.Filename)) continue;
            if (enabledSet.Contains(mod.Filename))  continue;

            bool enabled = freshInstall
                ? ComputeDefault(mod, manifest)
                : true;

            if (enabled) state.Enabled.Add(mod.Filename);
            else         state.Disabled.Add(mod.Filename);
        }

        state.Initialized = true;
        SaveState(profileName, state);

        LogService.Log($"[Optional] Состояние инициализировано для {profileName}: " +
                       $"enabled={state.Enabled.Count}, disabled={state.Disabled.Count}");
    }

    private static bool ComputeDefault(OptionalMod mod, OptionalManifest manifest)
    {
        if (mod.EnabledOnDefault.HasValue) return mod.EnabledOnDefault.Value;

        if (mod.Groups != null)
        {
            foreach (var gid in mod.Groups)
            {
                if (manifest.Groups.TryGetValue(gid, out var g) && g.EnabledOnDefault)
                    return true;
            }
        }
        return false;
    }

    public static bool IsEnabled(OptionalState state, string filename)
    {
        if (state.Enabled.Any(e => string.Equals(e, filename, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (state.Disabled.Any(d => string.Equals(d, filename, StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    public static string JarPath(string profileName, string filename)
        => Path.Combine(InstanceManager.GetDir(profileName), "mods", filename);

    public static string DisabledPath(string profileName, string filename)
        => JarPath(profileName, filename) + DisabledSuffix;

    public static bool IsPresentOnDisk(string profileName, string filename)
        => File.Exists(JarPath(profileName, filename))
           || File.Exists(DisabledPath(profileName, filename));

    // ------------------------------------------------------------------ //
    //  Переключение
    // ------------------------------------------------------------------ //

    public static async Task SetEnabledAsync(
        string profileName, OptionalMod mod, bool enabled,
        Action<string>? logger = null, CancellationToken ct = default)
    {
        var state = LoadState(profileName);
        var jar   = JarPath(profileName, mod.Filename);
        var dis   = jar + DisabledSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(jar)!);

        if (enabled)
        {
            if (File.Exists(dis) && !File.Exists(jar))
            {
                File.Move(dis, jar);
                logger?.Invoke($"[Optional] включён {mod.Filename} (jar.disabled → jar)");
            }
            else if (!File.Exists(jar))
            {
                if (string.IsNullOrEmpty(mod.ModrinthUrl))
                    throw new Exception(
                        $"{mod.Filename}: нет ссылки для скачивания (source = {mod.Source}). " +
                        "Добавьте файл вручную в папку mods.");

                logger?.Invoke($"[Optional] скачивание {mod.Filename}…");
                await Downloader.DownloadAsync(mod.ModrinthUrl, jar, ct: ct);
                var hash = await HashUtil.ComputeAsync(jar, "sha512", ct);
                if (!string.Equals(hash, mod.Sha512, StringComparison.OrdinalIgnoreCase))
                {
                    if (DevFlags.SkipIntegrityCheck)
                    {
                        logger?.Invoke($"[DEV] {mod.Filename}: хеш не совпал, но пропущено");
                    }
                    else
                    {
                        try { File.Delete(jar); } catch { }
                        throw new Exception($"{mod.Filename}: хеш не совпал после скачивания");
                    }
                }
                logger?.Invoke($"[Optional] включён {mod.Filename} (скачан)");
            }

            state.Disabled.RemoveAll(d => string.Equals(d, mod.Filename, StringComparison.OrdinalIgnoreCase));
            if (!state.Enabled.Any(e => string.Equals(e, mod.Filename, StringComparison.OrdinalIgnoreCase)))
                state.Enabled.Add(mod.Filename);
        }
        else
        {
            if (File.Exists(jar))
            {
                File.Move(jar, dis, overwrite: true);
                logger?.Invoke($"[Optional] выключен {mod.Filename} (jar → jar.disabled)");
            }
            else
            {
                logger?.Invoke($"[Optional] выключен {mod.Filename} (файл не скачан, просто запомнен выбор)");
            }

            state.Enabled.RemoveAll(e => string.Equals(e, mod.Filename, StringComparison.OrdinalIgnoreCase));
            if (!state.Disabled.Any(d => string.Equals(d, mod.Filename, StringComparison.OrdinalIgnoreCase)))
                state.Disabled.Add(mod.Filename);
        }

        SaveState(profileName, state);
    }
}