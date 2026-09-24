#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace WhiteMC.Core;

/// <summary>
/// DEV: при включённом флаге UseLocalOutput перехватывает URL'ы наших
/// манифестов и отдаёт локальный файл из output_dir вместо сети.
/// </summary>
public static class DevLocalSource
{
    // links.json, profiles.json, manifest-*.json, optional-*.json, mods-*.zip
    private static readonly Regex ManifestPattern = new(
        @"^(links|profiles|manifest-.+|optional-.+|mods-.+)\.(json|zip)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? _cachedOutputDir;
    private static DateTime _cachedStateMtime = DateTime.MinValue;

    private static void RefreshCache()
    {
        try
        {
            var sp = WhiteMC.Dev.DevStateService.StatePath;
            var mtime = File.Exists(sp)
                ? File.GetLastWriteTimeUtc(sp)
                : DateTime.MinValue;

            if (_cachedOutputDir != null && mtime == _cachedStateMtime) return;
            _cachedStateMtime = mtime;

            var st = WhiteMC.Dev.DevStateService.Load();
            _cachedOutputDir = string.IsNullOrWhiteSpace(st.Settings.OutputDir)
                ? Constants.LauncherDir
                : st.Settings.OutputDir;
        }
        catch
        {
            _cachedOutputDir = Constants.LauncherDir;
        }
    }

    public static bool TryGetLocalFile(string url, out string localPath)
    {
        localPath = "";
        if (!DevFlags.UseLocalOutput) return false;

        RefreshCache();
        if (string.IsNullOrEmpty(_cachedOutputDir)) return false;

        // Извлекаем basename без query/fragment.
        var tail = url.TrimEnd('/');
        var basename = tail.Split('/')[^1];
        var q = basename.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) basename = basename.Substring(0, q);

        if (!ManifestPattern.IsMatch(basename)) return false;

        var candidate = Path.Combine(_cachedOutputDir!, basename);
        if (!File.Exists(candidate)) return false;

        localPath = candidate;
        return true;
    }
}
#nullable restore