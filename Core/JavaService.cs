using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WhiteMC.Core;

public static class JavaService
{
    // Кэш версий: запуск процесса java -version не бесплатный (особенно на Windows),
    // а GetJavaMajor в JavaFromCommonDirs вызывается для каждой найденной папки.
    private static readonly Dictionary<string, int?> VersionCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object VersionCacheLock = new();

    public static void InvalidateVersionCache()
    {
        lock (VersionCacheLock) VersionCache.Clear();
    }

    // ------------------------------------------------------------------ //
    //  Управляемые JRE (в папке лаунчера)
    // ------------------------------------------------------------------ //

    public static string? InstalledJavaPath(int major)
    {
        var baseDir = Path.Combine(Constants.JavaDir, major.ToString());
        if (!Directory.Exists(baseDir)) return null;

        var direct = Path.Combine(baseDir, "bin", Constants.JavaBinaryName);
        if (File.Exists(direct)) return direct;

        foreach (var p in Directory.EnumerateFiles(baseDir, Constants.JavaBinaryName,
                                                    SearchOption.AllDirectories))
            return p;
        return null;
    }

    // ------------------------------------------------------------------ //
    //  Обнаружение системной Java
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Запускает `java -version` и возвращает мажорную версию (1.8.x → 8).
    /// null — определить не удалось. Результат кэшируется по пути.
    /// </summary>
    public static int? GetJavaMajor(string javaPath)
    {
        if (string.IsNullOrWhiteSpace(javaPath)) return null;

        lock (VersionCacheLock)
        {
            if (VersionCache.TryGetValue(javaPath, out var cached)) return cached;
        }

        int? result = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-version");

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                // У java -version вывод идёт в stderr.
                var stderrTask = proc.StandardError.ReadToEndAsync();
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                if (proc.WaitForExit(15000))
                {
                    var output = stderrTask.Result + "\n" + stdoutTask.Result;
                    // "openjdk version \"21.0.3+9\"", "java version \"1.8.0_422\""
                    var m = Regex.Match(output, @"version\s+""([\d._]+)");
                    if (m.Success)
                    {
                        var parts = m.Groups[1].Value.Split('.');
                        if (parts.Length >= 2 && parts[0] == "1")
                        {
                            if (int.TryParse(parts[1], out var j8)) result = j8;
                        }
                        else if (parts.Length >= 1 && int.TryParse(parts[0], out var major))
                        {
                            result = major;
                        }
                    }
                }
                else
                {
                    try { proc.Kill(); } catch { }
                }
            }
        }
        catch { /* ignore */ }

        lock (VersionCacheLock) { VersionCache[javaPath] = result; }
        return result;
    }

    /// <summary>Первый java.exe/java, найденный в PATH (без проверки версии).</summary>
    public static string? FindOnPath()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), Constants.JavaBinaryName);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Ищет СИСТЕМНУЮ Java строго нужной мажорной версии:
    ///   1) PATH (с проверкой версии через java -version)
    ///   2) Реестр Windows (JavaSoft\JDK / JRE / Java Runtime Environment)
    ///   3) Стандартные папки установки (Program Files\Java, /usr/lib/jvm и т.п.)
    /// </summary>
    public static string? SystemJavaPath(int major)
    {
        var path = FindOnPath();
        if (path != null && GetJavaMajor(path) == major)
            return path;

        var reg = JavaFromRegistry(major);
        if (reg != null) return reg;

        return JavaFromCommonDirs(major);
    }

    /// <summary>Лучший кандидат для запуска: сначала управляемая лаунчером, затем системная.</summary>
    public static string? FindLocalJava(int major)
        => InstalledJavaPath(major) ?? SystemJavaPath(major);

    private static string? JavaFromRegistry(int major)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            foreach (var root in new[]
                     {
                         Microsoft.Win32.Registry.LocalMachine,
                         Microsoft.Win32.Registry.CurrentUser
                     })
            {
                using var javaSoft = root.OpenSubKey(@"SOFTWARE\JavaSoft");
                if (javaSoft == null) continue;

                foreach (var family in new[] { "JDK", "JRE", "Java Runtime Environment" })
                {
                    using var familyKey = javaSoft.OpenSubKey(family);
                    if (familyKey == null) continue;

                    foreach (var sub in familyKey.GetSubKeyNames())
                    {
                        using var vKey = familyKey.OpenSubKey(sub);
                        var home = vKey?.GetValue("JavaHome") as string;
                        if (string.IsNullOrWhiteSpace(home)) continue;

                        var java = Path.Combine(home, "bin", Constants.JavaBinaryName);
                        if (!File.Exists(java)) continue;
                        if (GetJavaMajor(java) == major) return java;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static string? JavaFromCommonDirs(int major)
    {
        var roots = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            foreach (var pf in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                     })
            {
                if (string.IsNullOrEmpty(pf)) continue;
                roots.Add(Path.Combine(pf, "Java"));
                roots.Add(Path.Combine(pf, "Eclipse Adoptium"));
                roots.Add(Path.Combine(pf, "Eclipse Foundation"));
                roots.Add(Path.Combine(pf, "Microsoft"));
                roots.Add(Path.Combine(pf, "Zulu"));
                roots.Add(Path.Combine(pf, "Amazon Corretto"));
                roots.Add(Path.Combine(pf, "OpenJDK"));
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            roots.Add("/Library/Java/JavaVirtualMachines");
            roots.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".sdkman", "candidates", "java"));
        }
        else
        {
            roots.Add("/usr/lib/jvm");
            roots.Add("/usr/java");
            roots.Add("/opt/java");
            roots.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".sdkman", "candidates", "java"));
        }

        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;

                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    try
                    {
                        var java = Path.Combine(dir, "bin", Constants.JavaBinaryName);
                        if (File.Exists(java) && GetJavaMajor(java) == major)
                            return java;

                        foreach (var sub in Directory.EnumerateDirectories(dir))
                        {
                            var java2 = Path.Combine(sub, "bin", Constants.JavaBinaryName);
                            if (File.Exists(java2) && GetJavaMajor(java2) == major)
                                return java2;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
        return null;
    }

    // ------------------------------------------------------------------ //
    //  Установка (только если нет ни управляемой, ни подходящей системной)
    // ------------------------------------------------------------------ //

    public static async Task<string> EnsureJavaAsync(
        int major,
        Action<int, int, string>? onProgress = null,
        CancellationToken ct = default)
    {
        var existing = FindLocalJava(major);
        if (existing != null)
        {
            onProgress?.Invoke(1, 1, $"Java {major}: используем существующую ({existing})");
            LogService.Log($"[Java] Java {major}: используется существующая: {existing}");
            return existing;
        }

        void P(string m) => onProgress?.Invoke(0, 1, m);

        P($"Java {major}: получение ссылки…");
        var (link, name, kind) = await GetAdoptiumAssetAsync(major, ct);

        Directory.CreateDirectory(Constants.JavaDir);
        var archive = Path.Combine(Constants.JavaDir, $"jre-{major}-{name}");

        P($"Java {major}: скачивание {name}…");
        await Downloader.DownloadAsync(link, archive, ct: ct);

        P($"Java {major}: распаковка…");
        var dest = Path.Combine(Constants.JavaDir, major.ToString());
        await Task.Run(() => ExtractJavaArchive(archive, dest, kind), ct);

        try { File.Delete(archive); } catch { }

        var path = InstalledJavaPath(major)
                   ?? throw new Exception($"Java {major}: не найдена после распаковки");

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                foreach (var p in Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories))
                {
                    var parent = Path.GetFileName(Path.GetDirectoryName(p));
                    if (parent == "bin")
                    {
                        try
                        {
                            var mode = File.GetUnixFileMode(p);
                            File.SetUnixFileMode(p, mode | UnixFileMode.UserExecute
                                                       | UnixFileMode.GroupExecute
                                                       | UnixFileMode.OtherExecute);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // После установки новой JRE сбрасываем кэш — следующий вызов GetJavaMajor
        // увидит новый бинарь.
        InvalidateVersionCache();

        P($"Java {major}: установлена в {path}");
        return path;
    }

    private static async Task<(string link, string name, string kind)> GetAdoptiumAssetAsync(
        int major, CancellationToken ct)
    {
        var osName = OperatingSystem.IsWindows() ? "windows"
                   : OperatingSystem.IsMacOS()   ? "mac"
                                                 : "linux";

        var url = $"{string.Format(Constants.AdoptiumApi, major)}" +
                  $"?architecture={Constants.NativeClassifierArch}&image_type=jre&os={osName}&vendor=eclipse";

        var json = JsonNode.Parse(await Http.GetStringAsync(url, ct));
        var arr = json?.AsArray();
        if (arr == null || arr.Count == 0)
            throw new Exception($"Adoptium не вернул ассет для Java {major} ({osName})");

        var pkg = arr[0]!["binary"]!["package"]!;
        var link = pkg["link"]!.GetValue<string>();
        var name = pkg["name"]!.GetValue<string>();
        var kind = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "zip" : "tar.gz";
        return (link, name, kind);
    }

    private static void ExtractJavaArchive(string archive, string dest, string kind)
    {
        var tmp = dest + ".tmp_extract";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);

        if (kind == "zip")
        {
            ZipFile.ExtractToDirectory(archive, tmp, overwriteFiles: true);
        }
        else
        {
            using var fs = File.OpenRead(archive);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var tar = new TarReader(gz);
            TarEntry? entry;
            while ((entry = tar.GetNextEntry()) != null)
            {
                var safeRel = entry.Name.Replace('\\', '/').TrimStart('/');
                var target = Path.GetFullPath(Path.Combine(tmp, safeRel));
                if (!target.StartsWith(Path.GetFullPath(tmp), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (entry.EntryType == TarEntryType.Directory)
                {
                    Directory.CreateDirectory(target);
                }
                else if (entry.EntryType == TarEntryType.RegularFile ||
                         entry.EntryType == TarEntryType.V7RegularFile)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var ofs = File.Create(target);
                    entry.DataStream?.CopyTo(ofs);
                }
            }
        }

        var direct = Path.Combine(tmp, "bin", Constants.JavaBinaryName);
        string? root = null;
        if (File.Exists(direct))
        {
            root = tmp;
        }
        else
        {
            foreach (var p in Directory.EnumerateFiles(tmp, Constants.JavaBinaryName,
                                                       SearchOption.AllDirectories))
            {
                if (Path.GetFileName(Path.GetDirectoryName(p)) == "bin")
                {
                    root = Path.GetDirectoryName(Path.GetDirectoryName(p));
                    break;
                }
            }
        }

        if (root == null)
            throw new Exception($"Не найден bin/{Constants.JavaBinaryName} в архиве {Path.GetFileName(archive)}");

        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.Move(root, dest);
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
}