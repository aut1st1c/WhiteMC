using System.IO;
using System.Text.Json;

namespace WhiteMC.Core;

public static class SettingsService
{
    public static LauncherSettings Load()
    {
        var s = new LauncherSettings();
        try
        {
            if (File.Exists(Constants.SettingsFile))
            {
                var json = File.ReadAllText(Constants.SettingsFile);
                var loaded = JsonSerializer.Deserialize<LauncherSettings>(json, Json.Indented);
                if (loaded != null) s = loaded;
            }
        }
        catch { }
        return s;
    }

    public static void Save(LauncherSettings s)
    {
        Directory.CreateDirectory(Constants.LauncherDir);
        File.WriteAllText(Constants.SettingsFile,
            JsonSerializer.Serialize(s, Json.Indented));
    }
}