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

    /// <summary>
    /// Возвращает последнюю версию NeoForge, начинающуюся с prefix (например "21.1.").
    ///
    /// Порядок источников:
    ///   1. JSON API Reposilite — работает даже когда XML блокируется ISP.
    ///   2. XML maven-metadata.xml — классический путь.
    ///   3. Зеркало neoforged.forgecdn.net.
    /// </summary>
    public static async Task<string> LatestReleaseAsync(string prefix, CancellationToken ct = default)
    {
        // ---------- 1) JSON API ----------
        try
        {
            LogService.Log($"[NeoForge] запрашиваю список версий: {Constants.NeoForgeVersionsApi}");
            var json = await Http.GetStringAsync(Constants.NeoForgeVersionsApi, ct);

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("versions", out var arr))
            {
                var versions = new List<string>();
                foreach (var item in arr.EnumerateArray())
                {
                    var v = item.GetString();
                    if (!string.IsNullOrEmpty(v)
                        && v.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        versions.Add(v);
                    }
                }

                if (versions.Count > 0)
                {
                    var latest = versions
                        .OrderBy(v => v, Comparer<string>.Create(CompareVersions))
                        .Last();
                    LogService.Log($"[NeoForge] JSON API: последняя {prefix}x → {latest}");
                    return latest;
                }

                LogService.Log($"[NeoForge] JSON API: нет версий с префиксом '{prefix}'");
            }
        }
        catch (Exception ex)
        {
            LogService.Log($"[NeoForge] JSON API недоступен: {ex.Message}");
        }

        // ---------- 2) XML metadata ----------
        try
        {
            LogService.Log($"[NeoForge] пробую XML: {Constants.NeoForgeMetadataUrl}");
            var xml = await Http.GetStringAsync(Constants.NeoForgeMetadataUrl, ct);

            var m = System.Text.RegularExpressions.Regex.Match(xml, @"<release>([^<]+)</release>");
            if (m.Success)
            {
                var release = m.Groups[1].Value.Trim();
                if (release.StartsWith(prefix, StringComparison.Ordinal))
                {
                    LogService.Log($"[NeoForge] XML: release → {release}");
                    return release;
                }
            }

            var versions = System.Text.RegularExpressions.Regex
                .Matches(xml, @"<version>([^<]+)</version>")
                .Select(x => x.Groups[1].Value)
                .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();

            if (versions.Count > 0)
            {
                var latest = versions
                    .OrderBy(v => v, Comparer<string>.Create(CompareVersions))
                    .Last();
                LogService.Log($"[NeoForge] XML: последняя {prefix}x → {latest}");
                return latest;
            }
        }
        catch (Exception ex)
        {
            LogService.Log($"[NeoForge] XML недоступен: {ex.Message}");
        }

        // ---------- 3) Зеркало forgecdn.net ----------
        try
        {
            var mirrorUrl = Constants.NeoForgeMavenMirror + "/maven-metadata.xml";
            LogService.Log($"[NeoForge] пробую зеркало: {mirrorUrl}");
            var xml = await Http.GetStringAsync(mirrorUrl, ct);

            var versions = System.Text.RegularExpressions.Regex
                .Matches(xml, @"<version>([^<]+)</version>")
                .Select(x => x.Groups[1].Value)
                .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();

            if (versions.Count > 0)
            {
                var latest = versions
                    .OrderBy(v => v, Comparer<string>.Create(CompareVersions))
                    .Last();
                LogService.Log($"[NeoForge] зеркало: последняя {prefix}x → {latest}");
                return latest;
            }
        }
        catch (Exception ex)
        {
            LogService.Log($"[NeoForge] зеркало недоступно: {ex.Message}");
        }

        throw new Exception(
            $"NeoForge: не удалось получить список версий с префиксом '{prefix}'.\n" +
            $"Все источники недоступны:\n" +
            $"  • {Constants.NeoForgeVersionsApi}\n" +
            $"  • {Constants.NeoForgeMetadataUrl}\n" +
            $"  • {Constants.NeoForgeMavenMirror}/maven-metadata.xml\n\n" +
            $"Проверьте соединение или VPN.");
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

        // ИСПРАВЛЕНО: сначала пробуем любую локальную Java (управляемую ЛИБО системную).
        // EnsureJavaAsync возвращает готовый путь — не нужно перепроверять
        // InstalledJavaPath(), который видит только управляемую JRE.
        var java = JavaService.FindLocalJava(Constants.NeoForgeInstallerJava);
        if (java == null)
        {
            P($"NeoForge: установка Java {Constants.NeoForgeInstallerJava} для установщика…");
            java = await JavaService.EnsureJavaAsync(Constants.NeoForgeInstallerJava, onProgress, ct);
        }
        else
        {
            P($"NeoForge: используется Java {Constants.NeoForgeInstallerJava} ({java})");
        }

        if (string.IsNullOrWhiteSpace(java) || !File.Exists(java))
            throw new Exception(
                $"NeoForge: не удалось получить путь к Java {Constants.NeoForgeInstallerJava}. " +
                "Установите JDK/JRE вручную или очистите папку java и попробуйте снова.");

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