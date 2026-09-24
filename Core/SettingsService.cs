using System;
using System.IO;
using System.Text.Json;

namespace WhiteMC.Core;

public static class SettingsService
{
    private static string BackupPath => Constants.SettingsFile + ".backup";

    public static LauncherSettings Load()
    {
        // Сначала пробуем основной файл, затем бэкап.
        foreach (var path in new[] { Constants.SettingsFile, BackupPath })
        {
            try
            {
                if (!File.Exists(path)) continue;

                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<LauncherSettings>(json, Json.Indented);
                if (loaded != null)
                {
                    if (path == BackupPath)
                        LogService.Log("[Settings] Основной файл не прочитан, загружено из .backup");
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                LogService.Log($"[Settings] Не удалось прочитать {path}: {ex.Message}");
            }
        }
        return new LauncherSettings();
    }

    public static void Save(LauncherSettings s)
    {
        Directory.CreateDirectory(Constants.LauncherDir);
        var json = JsonSerializer.Serialize(s, Json.Indented);

        // Сохраняем предыдущее состояние как .backup, прежде чем перезаписать.
        try
        {
            if (File.Exists(Constants.SettingsFile))
                File.Copy(Constants.SettingsFile, BackupPath, overwrite: true);
        }
        catch { }

        File.WriteAllText(Constants.SettingsFile, json);
    }
}