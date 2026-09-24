#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhiteMC.Core;

public class DevFlagsData
{
    [JsonPropertyName("skip_integrity_check")]
    public bool SkipIntegrityCheck { get; set; }

    [JsonPropertyName("skip_missing")]
    public bool SkipMissing { get; set; }

    [JsonPropertyName("verbose_log")]
    public bool VerboseLog { get; set; }

    /// <summary>
    /// Тестовый режим: links.json / profiles.json / manifest-*.json /
    /// optional-*.json / mods-*.zip берутся из output_dir вместо сети.
    /// </summary>
    [JsonPropertyName("use_local_output")]
    public bool UseLocalOutput { get; set; }
}

public static class DevFlags
{
    private static DevFlagsData _data = new();
    private static readonly object _lock = new();
    private static bool _loaded;
    private static DateTime _lastMtime = DateTime.MinValue;

    public static string FilePath =>
        Path.Combine(Constants.LauncherDir, "dev.flags.json");

    public static bool SkipIntegrityCheck
    {
        get { EnsureLoaded(); lock (_lock) return _data.SkipIntegrityCheck; }
    }

    public static bool SkipMissing
    {
        get { EnsureLoaded(); lock (_lock) return _data.SkipMissing; }
    }

    public static bool VerboseLog
    {
        get { EnsureLoaded(); lock (_lock) return _data.VerboseLog; }
    }

    public static bool UseLocalOutput
    {
        get { EnsureLoaded(); lock (_lock) return _data.UseLocalOutput; }
    }

    public static DevFlagsData Snapshot()
    {
        EnsureLoaded();
        lock (_lock)
        {
            return new DevFlagsData
            {
                SkipIntegrityCheck = _data.SkipIntegrityCheck,
                SkipMissing = _data.SkipMissing,
                VerboseLog = _data.VerboseLog,
                UseLocalOutput = _data.UseLocalOutput,
            };
        }
    }

    public static void Update(Action<DevFlagsData> action)
    {
        EnsureLoaded();
        lock (_lock) action(_data);
        Save();
    }

    private static void EnsureLoaded()
    {
        DateTime mtime;
        try
        {
            mtime = File.Exists(FilePath)
                ? File.GetLastWriteTimeUtc(FilePath)
                : DateTime.MinValue;
        }
        catch { mtime = DateTime.MinValue; }

        if (_loaded && mtime == _lastMtime) return;

        lock (_lock)
        {
            // повторная проверка внутри lock
            try
            {
                mtime = File.Exists(FilePath)
                    ? File.GetLastWriteTimeUtc(FilePath)
                    : DateTime.MinValue;
            }
            catch { }
            if (_loaded && mtime == _lastMtime) return;
            _lastMtime = mtime;

            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var d = JsonSerializer.Deserialize<DevFlagsData>(json);
                    if (d != null) _data = d;
                }
                else
                {
                    _data = new DevFlagsData();
                }
            }
            catch (Exception ex)
            {
                LogService.Log($"[DEV] не удалось прочитать {FilePath}: {ex.Message}");
            }
            _loaded = true;

            if (_data.SkipIntegrityCheck || _data.SkipMissing
                || _data.VerboseLog || _data.UseLocalOutput)
            {
                LogService.Log($"[DEV] флаги: " +
                    $"skip_integrity_check={_data.SkipIntegrityCheck}, " +
                    $"skip_missing={_data.SkipMissing}, " +
                    $"verbose_log={_data.VerboseLog}, " +
                    $"use_local_output={_data.UseLocalOutput}");
            }
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Constants.LauncherDir);
            string json;
            lock (_lock)
            {
                json = JsonSerializer.Serialize(_data,
                    new JsonSerializerOptions { WriteIndented = true });
            }
            File.WriteAllText(FilePath, json);
            try { _lastMtime = File.GetLastWriteTimeUtc(FilePath); } catch { }
        }
        catch (Exception ex)
        {
            LogService.Log($"[DEV] не удалось сохранить флаги: {ex.Message}");
        }
    }
}