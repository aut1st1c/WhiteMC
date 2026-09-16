using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WhiteMC.Core;

public class LauncherSettings
{
    public string Username     { get; set; } = "Player";
    public string Xms          { get; set; } = "512M";
    public string Xmx          { get; set; } = "2G";
    public string ExtraJvmArgs { get; set; } = "";

    /// <summary>ID темы (см. ThemeService.Available). null → Mocha.</summary>
    public string? Theme       { get; set; }
}

// ---------------------------------------------------------------------- //
//  links.json
// ---------------------------------------------------------------------- //

public class LinksConfig
{
    [JsonPropertyName("profiles_url")]
    public string? ProfilesUrl { get; set; }

    /// <summary>Последняя версия лаунчера на сервере.</summary>
    [JsonPropertyName("launcher_version")]
    public string? LauncherVersion { get; set; }

    /// <summary>Страница загрузки новой версии.</summary>
    [JsonPropertyName("launcher_download_url")]
    public string? LauncherDownloadUrl { get; set; }
}

// ---------------------------------------------------------------------- //
//  profiles.json
// ---------------------------------------------------------------------- //

public class ModpackProfile
{
    [JsonPropertyName("version_url")]
    public string? VersionUrl { get; set; }

    [JsonPropertyName("manifest_url")]
    public string? ManifestUrl { get; set; }

    [JsonPropertyName("components")]
    public Dictionary<string, string> Components { get; set; } = new();
}

public class GameProfile
{
    [JsonPropertyName("mc")]
    public string Mc { get; set; } = "";

    [JsonPropertyName("neoforge")]
    public bool NeoForge { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("modpack")]
    public ModpackProfile? Modpack { get; set; }
}

/// <summary>Элемент ComboBox: хранит ключ профиля и его отображаемое имя.</summary>
public sealed class ProfileEntry
{
    public string Key     { get; }
    public string Display { get; }

    public ProfileEntry(string key, string display)
    {
        Key = key;
        Display = display;
    }

    public override string ToString() => Display;
}

public static class Profiles
{
    private static Dictionary<string, GameProfile> _all = new();
    private static readonly object _lock = new();

    public static IReadOnlyDictionary<string, GameProfile> All
    {
        get { lock (_lock) return _all; }
    }

    public static async Task InitializeAsync(CancellationToken ct = default)
    {
        var links = LinksService.Current;
        if (string.IsNullOrWhiteSpace(links.ProfilesUrl))
            throw new Exception("В links.json не задан profiles_url");

        var cachePath = Path.Combine(Constants.LauncherDir, "profiles.cache.json");

        string json;
        try
        {
            json = await Http.GetStringAsync(links.ProfilesUrl, ct).ConfigureAwait(false);
            Directory.CreateDirectory(Constants.LauncherDir);
            await File.WriteAllTextAsync(cachePath, json, ct).ConfigureAwait(false);
            LogService.Log($"[WhiteMC] profiles.json загружен с {links.ProfilesUrl}");
        }
        catch (Exception ex)
        {
            LogService.Log($"[WhiteMC] Не удалось получить profiles.json ({links.ProfilesUrl}): {ex.Message}");
            if (!File.Exists(cachePath)) throw;
            json = await File.ReadAllTextAsync(cachePath, ct).ConfigureAwait(false);
            LogService.Log("[WhiteMC] profiles.json взят из кэша");
        }

        var parsed = JsonSerializer.Deserialize<Dictionary<string, GameProfile>>(
                         json, Json.CaseInsensitive)
                     ?? throw new Exception("profiles.json пуст или невалиден");

        lock (_lock) { _all = parsed; }
        LogService.Log($"[WhiteMC] Загружено профилей: {parsed.Count} ({string.Join(", ", parsed.Keys)})");
    }

    public static (string name, string version, bool neoforge) Resolve(string name)
    {
        if (All.TryGetValue(name, out var p))
            return (name, p.Mc, p.NeoForge);
        return (name, name, Constants.NeoForgeForMc.ContainsKey(name));
    }

    public static string Version(string name)      => Resolve(name).version;
    public static bool   UsesNeoForge(string name) => Resolve(name).neoforge;

    public static ModpackProfile? Modpack(string name)
        => All.TryGetValue(name, out var p) ? p.Modpack : null;

    public static bool HasModpack(string name)
    {
        var m = Modpack(name);
        return m != null && (m.Components.Count > 0 || !string.IsNullOrEmpty(m.ManifestUrl));
    }

    public static bool HasComponents(string name)
    {
        var m = Modpack(name);
        return m != null && m.Components.Count > 0;
    }

    public static bool HasModsManifest(string name)
    {
        var m = Modpack(name);
        return m != null && !string.IsNullOrEmpty(m.ManifestUrl);
    }

    /// <summary>
    /// Возвращает URL архива mods из profiles.json (modpack.components["mods"]).
    /// null — если компонент "mods" не задан.
    /// </summary>
    public static string? ModsArchiveUrl(string name)
    {
        var m = Modpack(name);
        if (m == null) return null;
        if (m.Components.TryGetValue("mods", out var url) && !string.IsNullOrWhiteSpace(url))
            return url;
        return null;
    }
}

// ---------------------------------------------------------------------- //
//  Mojang version manifest
// ---------------------------------------------------------------------- //

public class VersionManifest
{
    public List<VersionInfo> Versions { get; set; } = new();
}

public class VersionInfo
{
    public string Id  { get; set; } = "";
    public string Url { get; set; } = "";
}

// ---------------------------------------------------------------------- //
//  Mod sync (Modrinth)
// ---------------------------------------------------------------------- //

public class RemoteManifest
{
    [JsonPropertyName("manifest_version")]
    public string ManifestVersion { get; set; } = "";

    [JsonPropertyName("mods")]
    public List<RemoteMod> Mods { get; set; } = new();
}

public class RemoteMod
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("sha512")]
    public string Sha512 { get; set; } = "";

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>"modrinth" или "unresolved".</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("modrinth_version_id")]
    public string? ModrinthVersionId { get; set; }

    [JsonPropertyName("modrinth_project_id")]
    public string? ModrinthProjectId { get; set; }

    [JsonPropertyName("modrinth_url")]
    public string? ModrinthUrl { get; set; }
}

public class LocalModsState
{
    [JsonPropertyName("manifest_version")]
    public string ManifestVersion { get; set; } = "";

    [JsonPropertyName("mods")]
    public Dictionary<string, string> Mods { get; set; } = new();
}

/// <summary>Результат проверки локальных модов против серверного манифеста.</summary>
public class ModCheckResult
{
    public string ManifestVersion { get; set; } = "";
    public int    TotalRemote { get; set; }
    public int    TotalLocal  { get; set; }

    /// <summary>Есть на сервере, нет локально (можно скачать с Modrinth).</summary>
    public List<RemoteMod> Missing    { get; set; } = new();

    /// <summary>Есть локально, но хэш не совпал (можно перекачать с Modrinth).</summary>
    public List<RemoteMod> Mismatched { get; set; } = new();

    /// <summary>Есть на сервере, но без прямого URL — тянуть из архива.</summary>
    public List<RemoteMod> Unresolved { get; set; } = new();

    /// <summary>Есть локально, но нет в манифесте — на удаление.</summary>
    public List<string> Extra { get; set; } = new();

    public int  Issues     => Missing.Count + Mismatched.Count + Unresolved.Count + Extra.Count;
    public bool IsUpToDate => Issues == 0;
}