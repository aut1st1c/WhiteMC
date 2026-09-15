using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WhiteMC.Core;

public static class NeoForgeService
{
    public static string InstallDir(string mcVersion) =>
        Path.Combine(Constants.NeoForgeDir, mcVersion);

    public static async Task<string> LatestReleaseAsync(string prefix, CancellationToken ct = default)
    {
        var xml = await Http.GetStringAsync(Constants.NeoForgeMetadataUrl, ct);
        var m = Regex.Match(xml, @"<release>([^<]+)</release>");
        if (!m.Success)
            throw new Exception("Не удалось найти <release> в maven-metadata.xml NeoForge");

        var release = m.Groups[1].Value.Trim();
        if (!string.IsNullOrEmpty(prefix) && !release.StartsWith(prefix, StringComparison.Ordinal))
        {
            var versions = Regex.Matches(xml, @"<version>([^<]+)</version>")
                                .Select(x => x.Groups[1].Value)
                                .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
                                .ToList();
            if (versions.Count == 0)
                throw new Exception($"NeoForge: не найдено версий с префиксом '{prefix}'");
            release = versions.OrderBy(v => v, Comparer<string>.Create(CompareVersions)).Last();
        }
        return release;
    }

    private static int CompareVersions(string a, string b)
    {
        var pa = Regex.Split(a, @"[.\-]");
        var pb = Regex.Split(b, @"[.\-]");
        int n = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < n; i++)
        {
            var sa = i < pa.Length ? pa[i] : "0";
            var sb = i < pb.Length ? pb[i] : "0";
            int cmp;
            if (int.TryParse(sa, out var ia) && int.TryParse(sb, out var ib))
                cmp = ia.CompareTo(ib);
            else
                cmp = string.Compare(sa, sb, StringComparison.Ordinal);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    public static string? Installed(string mcVersion)
    {
        var d = InstallDir(mcVersion);
        if (!Directory.Exists(d)) return null;

        var vdir = Path.Combine(d, "versions");
        if (Directory.Exists(vdir))
        {
            var candidates = Directory.EnumerateDirectories(vdir)
                .Select(Path.GetFileName)
                .Where(n => n != null && n.StartsWith("neoforge-", StringComparison.Ordinal))
                .Where(n => File.Exists(Path.Combine(vdir, n!, n + ".json")))
                .Select(n => n!)
                .OrderBy(v => v, Comparer<string>.Create(CompareVersions))
                .ToList();
            if (candidates.Count > 0) return candidates[^1];
        }

        foreach (var jf in Directory.EnumerateFiles(d, "neoforge-*.json", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(jf);
            if (name.Contains("installer", StringComparison.OrdinalIgnoreCase)) continue;
            return name;
        }
        return null;
    }

    public static string? VersionFromProfileId(string mcVersion)
    {
        var pid = Installed(mcVersion);
        if (pid == null) return null;
        var m = Regex.Match(pid, @"^neoforge-(.+)$");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string? FindProfileJson(string mcVersion)
    {
        var id = Installed(mcVersion);
        if (id == null) return null;
        var d = InstallDir(mcVersion);
        var candidate = Path.Combine(d, "versions", id, id + ".json");
        if (File.Exists(candidate)) return candidate;
        foreach (var jf in Directory.EnumerateFiles(d, id + ".json", SearchOption.AllDirectories))
            return jf;
        return null;
    }

    public static async Task<string> InstallAsync(
        string mcVersion,
        Action<int, int, string>? onProgress = null,
        bool forceReinstall = false,
        Action<string>? logger = null,
        CancellationToken ct = default)
    {
        void P(string m)
        {
            onProgress?.Invoke(0, 1, m);
            logger?.Invoke(m);
        }

        if (!Constants.NeoForgeForMc.TryGetValue(mcVersion, out var prefix))
            throw new Exception($"NeoForge: неизвестный префикс для MC {mcVersion}");

        P($"NeoForge: определение последнего релиза для MC {mcVersion}…");
        var latest = await LatestReleaseAsync(prefix, ct);
        var current = VersionFromProfileId(mcVersion);

        if (current == latest && !forceReinstall)
        {
            P($"NeoForge {current}: уже актуален");
            return Installed(mcVersion)!;
        }

        if (current != null && current != latest)
        {
            P($"NeoForge {current} → {latest}: удаление старой версии…");
            WipeDir(mcVersion);
        }
        else if (forceReinstall && current != null)
        {
            P($"NeoForge {current}: принудительная переустановка…");
            WipeDir(mcVersion);
        }

        if (Directory.Exists(InstallDir(mcVersion)) && current == null)
        {
            P("NeoForge: удаление повреждённой установки…");
            WipeDir(mcVersion);
        }

        var target = InstallDir(mcVersion);
        EnsureLauncherProfiles(target);

        P($"NeoForge {latest}: скачивание установщика…");
        var installerName = $"neoforge-{latest}-installer.jar";
        var installerUrl = $"{Constants.NeoForgeMaven}/{latest}/{installerName}";

        Directory.CreateDirectory(Constants.NeoForgeDir);
        var installer = Path.Combine(Constants.NeoForgeDir, installerName);
        await Downloader.DownloadAsync(installerUrl, installer, ct: ct);

        var java = JavaService.InstalledJavaPath(Constants.NeoForgeInstallerJava);
        if (java == null)
        {
            P($"NeoForge: установка Java {Constants.NeoForgeInstallerJava} для установщика…");
            await JavaService.EnsureJavaAsync(Constants.NeoForgeInstallerJava, onProgress, ct);
            java = JavaService.InstalledJavaPath(Constants.NeoForgeInstallerJava)!;
        }

        P($"NeoForge {latest}: запуск установщика…");
        var psi = new ProcessStartInfo
        {
            FileName = java,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installer);
        psi.ArgumentList.Add("--installClient");
        psi.ArgumentList.Add(target);

        using var proc = Process.Start(psi)
            ?? throw new Exception("Не удалось запустить установщик NeoForge");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (logger != null)
        {
            foreach (var line in stdout.Split('\n')) logger($"[installer] {line.TrimEnd()}");
            foreach (var line in stderr.Split('\n')) logger($"[installer:err] {line.TrimEnd()}");
        }

        if (proc.ExitCode != 0)
        {
            WipeDir(mcVersion);
            throw new Exception(
                $"Установщик NeoForge завершился с кодом {proc.ExitCode}:\n{stdout}\n{stderr}");
        }

        try { File.Delete(installer); } catch { }
        CleanupOldInstallers();

        var vid = Installed(mcVersion)
            ?? throw new Exception("NeoForge: установщик отработал, но профиль не найден");

        if (logger != null)
        {
            try
            {
                foreach (var p in Directory.EnumerateFiles(target, "neoforge-*.json",
                                                            SearchOption.AllDirectories))
                    logger($"[WhiteMC] NeoForge профиль найден: {Path.GetRelativePath(target, p)}");
            }
            catch { }
        }

        P($"NeoForge {latest}: установлен ({vid})");
        return vid;
    }

    private static void WipeDir(string mcVersion)
    {
        var d = InstallDir(mcVersion);
        if (Directory.Exists(d))
            try { Directory.Delete(d, true); } catch { }
    }

    private static void EnsureLauncherProfiles(string target)
    {
        Directory.CreateDirectory(target);
        var file = Path.Combine(target, "launcher_profiles.json");
        if (!File.Exists(file))
        {
            File.WriteAllText(file,
                "{\n  \"profiles\": {},\n  \"settings\": {},\n  \"version\": 3\n}");
        }
    }

    private static void CleanupOldInstallers()
    {
        if (!Directory.Exists(Constants.NeoForgeDir)) return;
        foreach (var p in Directory.EnumerateFiles(Constants.NeoForgeDir,
                                                   "neoforge-*-installer.jar",
                                                   SearchOption.AllDirectories))
        {
            try { File.Delete(p); } catch { }
        }
    }
}