using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WhiteMC.Core;

public class LauncherSettings
{
    public string Username { get; set; } = "Player";
    public string Xms { get; set; } = "512M";
    public string Xmx { get; set; } = "2G";
    public string ExtraJvmArgs { get; set; } = "";
}

public class ModpackProfile
{
    public string? VersionUrl { get; set; }
    public string? ManifestUrl { get; set; }
    public Dictionary<string, string> Components { get; set; } = new();
}

public class GameProfile
{
    public string Mc { get; set; } = "";
    public bool NeoForge { get; set; }
    public ModpackProfile? Modpack { get; set; }
}

public static class Profiles
{
    public static readonly Dictionary<string, GameProfile> All = new()
    {
        ["IndustrialAdventure"] = new GameProfile
        {
            Mc = "1.21.1",
            NeoForge = true,
            Modpack = new ModpackProfile
            {
                VersionUrl = "https://raw.githubusercontent.com/aut1st1c/WhiteMC/refs/heads/main/version.json",
                ManifestUrl = "https://raw.githubusercontent.com/aut1st1c/WhiteMC/refs/heads/main/manifest.json",
//                Components = new Dictionary<string, string>
//               {
//                    ["config"] = "://raw.githubusercontent.com/aut1st1c/WhiteMC/refs/heads/main/manifest.json",
//                }
            }
        },
        ["VanillaSMP"] = new GameProfile
        {
            Mc = "26.2",
            NeoForge = false
        }
    };

    public static (string name, string version, bool neoforge) Resolve(string name)
    {
        if (All.TryGetValue(name, out var p))
            return (name, p.Mc, p.NeoForge);
        return (name, name, Constants.NeoForgeForMc.ContainsKey(name));
    }

    public static string Version(string name) => Resolve(name).version;
    public static bool UsesNeoForge(string name) => Resolve(name).neoforge;
    public static ModpackProfile? Modpack(string name) =>
        All.TryGetValue(name, out var p) ? p.Modpack : null;
    public static bool HasModpack(string name) => Modpack(name) is { Components.Count: > 0 };
}

public class VersionManifest
{
    public List<VersionInfo> Versions { get; set; } = new();
}

public class VersionInfo
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
}

// ---------------------------------------------------------------------- //
//  Mod sync models
// ---------------------------------------------------------------------- //

public class RemoteManifest
{
    [JsonPropertyName("manifest_version")]
    public string ManifestVersion { get; set; } = "";

    [JsonPropertyName("mods")]
    public List<RemoteMod> Mods { get; set; } = new();

    /// <summary>URL архива mods.zip на сервере — fallback для тех модов,
    /// которые не опознали на Modrinth/CurseForge.</summary>
    [JsonPropertyName("archive_url")]
    public string? ArchiveUrl { get; set; }
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

    /// <summary>modrinth | curseforge | unresolved</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    // Modrinth
    [JsonPropertyName("modrinth_version_id")]
    public string? ModrinthVersionId { get; set; }

    [JsonPropertyName("modrinth_project_id")]
    public string? ModrinthProjectId { get; set; }

    [JsonPropertyName("modrinth_url")]
    public string? ModrinthUrl { get; set; }

    // CurseForge
    [JsonPropertyName("curseforge_mod_id")]
    public long? CurseforgeModId { get; set; }

    [JsonPropertyName("curseforge_file_id")]
    public long? CurseforgeFileId { get; set; }
}

/// <summary>Локальное состояние: filename → sha512. Пишется после синхронизации.</summary>
public class LocalModsState
{
    [JsonPropertyName("manifest_version")]
    public string ManifestVersion { get; set; } = "";

    [JsonPropertyName("mods")]
    public Dictionary<string, string> Mods { get; set; } = new();
}