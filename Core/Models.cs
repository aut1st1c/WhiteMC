using System.Collections.Generic;

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
                VersionUrl = "https://raw.githubusercontent.com/aut1st1c/White/refs/heads/main/version.json",
                Components = new Dictionary<string, string>
                {
                    ["config"] = "https://github.com/aut1st1c/White/releases/download/Server-instances/config.zip",
                    ["mods"]   = "https://github.com/aut1st1c/White/releases/download/Server-instances/mods.zip",
                }
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