using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System;

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

    /// <summary>Последняя версия лаунчера на сервере (semver-подобная).</summary>
    [JsonPropertyName("launcher_version")]
    public string? LauncherVersion { get; set; }

    /// <summary>Страница загрузки новой версии.</summary>
    [JsonPropertyName("launcher_download_url")]
    public string? LauncherDownloadUrl { get; set; }
}

// ---------------------------------------------------------------------- //
//  profiles.json (без изменений)
// ---------------------------------------------------------------------- //

public class ModpackProfile
{
    public string? VersionUrl  { get; set; }
    public string? ManifestUrl { get; set; }
    public Dictionary<string, string> Components { get; set; } = new();
}

public class GameProfile
{
    public string Mc { get; set; } = "";
    public bool   NeoForge { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public ModpackProfile? Modpack { get; set; }
}

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
}

// ---------------------------------------------------------------------- //
//  Mojang version manifest (без изменений)
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
//  Mod sync (Modrinth, без изменений)
// ---------------------------------------------------------------------- //

public class RemoteManifest
{
    [JsonPropertyName("manifest_version")]
    public string ManifestVersion { get; set; } = "";

    [JsonPropertyName("mods")]
    public List<RemoteMod> Mods { get; set; } = new();

    [JsonPropertyName("archive_url")]
    public string? ArchiveUrl { get; set; }
}

public class RemoteMod
{
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("sha512")]   public string Sha512   { get; set; } = "";
    [JsonPropertyName("sha1")]     public string? Sha1    { get; set; }
    [JsonPropertyName("size")]     public long   Size     { get; set; }
    [JsonPropertyName("source")]   public string Source   { get; set; } = "";

    [JsonPropertyName("modrinth_version_id")] public string? ModrinthVersionId { get; set; }
    [JsonPropertyName("modrinth_project_id")] public string? ModrinthProjectId { get; set; }
    [JsonPropertyName("modrinth_url")]        public string? ModrinthUrl       { get; set; }
}

public class LocalModsState
{
    [JsonPropertyName("manifest_version")] public string ManifestVersion { get; set; } = "";
    [JsonPropertyName("mods")]             public Dictionary<string, string> Mods { get; set; } = new();
}

public class ModCheckResult
{
    public string ManifestVersion { get; set; } = "";
    public int    TotalRemote { get; set; }
    public int    TotalLocal  { get; set; }

    public List<RemoteMod> Missing    { get; set; } = new();
    public List<RemoteMod> Mismatched { get; set; } = new();
    public List<RemoteMod> Unresolved { get; set; } = new();
    public List<string>    Extra      { get; set; } = new();

    public int  Issues     => Missing.Count + Mismatched.Count + Unresolved.Count + Extra.Count;
    public bool IsUpToDate => Issues == 0;
}