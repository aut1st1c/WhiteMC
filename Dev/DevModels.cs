using System.Collections.Generic;

namespace WhiteMC.Dev;

public class DevMod
{
    public string Filename { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }

    public string Sha512 { get; set; } = "";
    public string Sha1 { get; set; } = "";
    public string Source { get; set; } = "";
    public string ModrinthUrl { get; set; } = "";
    public string ModrinthVersionId { get; set; } = "";
    public string ModrinthProjectId { get; set; } = "";

    /// <summary>Название проекта на Modrinth. Заполняется при сканировании,
    /// используется как авто-display_name, если DisplayName пуст.</summary>
    public string ModrinthTitle { get; set; } = "";

    public bool IsOptional { get; set; }
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Groups { get; set; } = new();
    public List<string> DependsOn { get; set; } = new();
    public bool? EnabledOnDefault { get; set; }

    public string State { get; set; } = "pending";
}

public class DevGroup
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Mode { get; set; } = "grouped";
    public string? ExclusiveSet { get; set; }
    public List<string> DependsOn { get; set; } = new();
    public bool EnabledOnDefault { get; set; }
}

public class DevProfile
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Mc { get; set; } = "1.21.1";
    public bool NeoForge { get; set; }
    public bool NeoForgeOptional { get; set; }
    public bool OptionalMods { get; set; }
    public string ModsDir { get; set; } = "";

    public string ManifestUrlOverride { get; set; } = "";
    public string OptionalManifestUrlOverride { get; set; } = "";
    public string ModsArchiveUrlOverride { get; set; } = "";

    public List<DevMod> Mods { get; set; } = new();
    public Dictionary<string, DevGroup> Groups { get; set; } = new();
}

public class DevSettings
{
    public string BaseUrl { get; set; } =
        "https://raw.githubusercontent.com/aut1st1c/WhiteMC/refs/heads/main";
    public string LauncherVersion { get; set; } = "0.1.4";
    public string LauncherDownloadUrl { get; set; } =
        "https://github.com/aut1st1c/WhiteMC/releases/latest";
    public string OutputDir { get; set; } = "";
    public string ManifestVersion { get; set; } = "";
    public string ModsBaseDir { get; set; } = "";
}

public class ModrinthInfo
{
    public string? ProjectId { get; set; }
    public string? VersionId { get; set; }
    public string? Url { get; set; }
    public long Size { get; set; }
    public string? Sha1 { get; set; }
}

public class DevState
{
    public DevSettings Settings { get; set; } = new();
    public Dictionary<string, DevProfile> Profiles { get; set; } = new();
    public string ActiveProfile { get; set; } = "";
    public Dictionary<string, ModrinthInfo> ModrinthCache { get; set; } = new();

    /// <summary>project_id → title. Кеш для авто-display_name.</summary>
    public Dictionary<string, string> ProjectTitles { get; set; } = new();
}