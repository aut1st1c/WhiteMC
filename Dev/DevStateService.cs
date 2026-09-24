using System;
using System.IO;
using System.Text.Json;
using WhiteMC.Core;

namespace WhiteMC.Dev;

public static class DevStateService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static string StatePath =>
        Path.Combine(Constants.LauncherDir, "dev_editor.state.json");

    public static DevState Load()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var json = File.ReadAllText(StatePath);
                var st = JsonSerializer.Deserialize<DevState>(json, Options);
                if (st != null) return st;
            }
        }
        catch (Exception ex)
        {
            LogService.Log($"[DEV] не удалось загрузить состояние: {ex.Message}");
        }
        return new DevState();
    }

    public static void Save(DevState st)
    {
        try
        {
            Directory.CreateDirectory(Constants.LauncherDir);
            File.WriteAllText(StatePath,
                JsonSerializer.Serialize(st, Options));
        }
        catch (Exception ex)
        {
            LogService.Log($"[DEV] не удалось сохранить состояние: {ex.Message}");
        }
    }

    public static string DefaultModsDirFor(string baseDir, string key)
        => string.IsNullOrEmpty(baseDir) ? "" : Path.Combine(baseDir, key);

    public static string Slug(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');
        return sb.ToString();
    }

    public static bool IsUrl(string v)
        => v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || v.StartsWith("//");

    public static string ResolveUrl(string baseUrl, string over, string defName, string ext)
    {
        over = (over ?? "").Trim();
        if (IsUrl(over)) return over;
        var name = string.IsNullOrEmpty(over) ? defName : over;
        return $"{baseUrl.TrimEnd('/')}/{name}{ext}";
    }

    public static string ResolveFilename(string over, string defName, string ext)
    {
        over = (over ?? "").Trim();
        if (IsUrl(over))
        {
            var name = over.TrimEnd('/').Split('/')[^1];
            foreach (var e in new[] { ".json", ".zip" })
                if (name.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                    name = name[..^e.Length];
            return name + ext;
        }
        return (string.IsNullOrEmpty(over) ? defName : over) + ext;
    }
}