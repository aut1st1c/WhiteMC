using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace WhiteMC.Core;

public static class Constants
{
    // --------------------------------------------------------------------- //
    //  Единственная захардкоженная ссылка в лаунчере.
    //  Всё остальное приходит из links.json.
    // --------------------------------------------------------------------- //
    public const string LinksManifestUrl =
        "https://raw.githubusercontent.com/aut1st1c/WhiteMC/refs/heads/main/links.json";

    // Разрешено оставить в коде: Mojang / Adoptium / NeoForge / Modrinth.
    public const string VersionManifestUrl  = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    public const string AdoptiumApi         = "https://api.adoptium.net/v3/assets/latest/{0}/hotspot";
    public const string NeoForgeMaven        = "https://maven.neoforged.net/releases/net/neoforged/neoforge";

    /// <summary>XML-метаданные. Может возвращать 404 из-за блокировок ISP (особенно РФ).</summary>
    public const string NeoForgeMetadataUrl  = NeoForgeMaven + "/maven-metadata.xml";

    /// <summary>JSON API Reposilite. Работает там, где XML блокируется.</summary>
    public const string NeoForgeVersionsApi  =
        "https://maven.neoforged.net/api/maven/versions/releases/net%2Fneoforged%2Fneoforge";

    /// <summary>Зеркало через CurseForge CDN — на случай полной блокировки основного домена.</summary>
    public const string NeoForgeMavenMirror  = "https://neoforged.forgecdn.net/releases/net/neoforged/neoforge";

    public const string UserAgent             = "WhiteMC/1.0 (github.com/aut1st1c/White)";
    public const int    MaxParallelDownloads  = 8;
    public const int    NeoForgeInstallerJava = 21;
    public const string ModpackManifestFile   = ".whitemc_modpack.json";

    public static readonly string[] AllowedVersions = { "1.21.1", "26.2" };

    public static readonly Dictionary<string, string> NeoForgeForMc = new()
    {
        ["1.21.1"] = "21.1."
    };

    public static readonly HashSet<string> WrapperBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks",
        "saves", "screenshots", "logs", "crash-reports", "schematics",
        "scripts", "kubejs", "openloader", "patchouli_books", "datapacks",
        "oresources", "oreexcavation", "journeymap", "xaero"
    };

    public static readonly HashSet<string> SingleValueJvmFlags = new()
    {
        "--add-opens", "--add-exports", "--add-reads"
    };

    public static readonly string[] NeoForgeAddOpens =
    {
        "--add-opens=java.base/java.lang.invoke=ALL-UNNAMED",
        "--add-opens=java.base/java.lang=ALL-UNNAMED",
        "--add-opens=java.base/java.util=ALL-UNNAMED",
        "--add-opens=java.base/java.util.jar=ALL-UNNAMED",
        "--add-opens=java.base/java.nio=ALL-UNNAMED",
        "--add-opens=java.base/sun.nio.ch=ALL-UNNAMED",
        "--add-opens=java.base/java.text=ALL-UNNAMED",
        "--add-opens=java.desktop/java.awt.font=ALL-UNNAMED",
    };

    // Paths ------------------------------------------------------------------

    public static string LauncherDir     { get; } = ResolveLauncherDir();
    public static string SettingsFile    => Path.Combine(LauncherDir, "settings.json");
    public static string JavaDir         => Path.Combine(LauncherDir, "java");
    public static string NeoForgeDir     => Path.Combine(LauncherDir, "neoforge");
    public static string InstancesDir    => Path.Combine(LauncherDir, "instances");
    public static string ModpackCacheDir => Path.Combine(LauncherDir, "cache", "modpack");
    public static string VersionsDir     => Path.Combine(LauncherDir, "versions");
    public static string LibrariesDir    => Path.Combine(LauncherDir, "libraries");
    public static string AssetsDir       => Path.Combine(LauncherDir, "assets");
    public static string NativesDir      => Path.Combine(LauncherDir, "natives");
    public static string NativesTmpDir   => Path.Combine(LauncherDir, "natives_tmp");
    public static string LogDir          => Path.Combine(LauncherDir, "logs");
    public static string LogFile         => Path.Combine(LogDir, "latest.log");
    public static string LogPrevFile     => Path.Combine(LogDir, "latest.prev.log");

    /// <summary>
    /// Определяет корневую папку лаунчера в порядке приоритета:
    ///   1. WHITEMC_DIR (переменная окружения) — для явного переопределения.
    ///   2. Портативный режим: папка рядом с .exe, если в неё можно писать.
    ///   3. Fallback: %USERPROFILE%\.whitemc
    /// </summary>
    private static string ResolveLauncherDir()
    {
        // 1) Явное переопределение
        var env = Environment.GetEnvironmentVariable("WHITEMC_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            return Path.GetFullPath(env);

        // 2) Портативный режим — папка с .exe
        var exeDir = (AppContext.BaseDirectory ?? "").TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!string.IsNullOrEmpty(exeDir) && IsWritable(exeDir))
            return exeDir;

        // 3) Fallback
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".whitemc");
    }

    /// <summary>Проверяет, можно ли писать в папку — создаёт и сразу удаляет пробный файл.</summary>
    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".whitemc_probe_{Environment.ProcessId}_{Guid.NewGuid():N}");
            using (var fs = new FileStream(
                probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // OS ---------------------------------------------------------------------

    public static string OsName =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS()   ? "osx"     : "linux";

    public static int OsArchBits => IntPtr.Size * 8;

    public static string JavaBinaryName => OperatingSystem.IsWindows() ? "java.exe" : "java";

    public static string NativeClassifierArch =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "aarch64",
            Architecture.X86   => "x32",
            _                  => "x64"
        };
}