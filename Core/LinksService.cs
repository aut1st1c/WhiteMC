using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WhiteMC.Core;

public static class LinksService
{
    private static LinksConfig? _config;
    private static readonly object _lock = new();

    public static LinksConfig Current
    {
        get
        {
            lock (_lock)
            {
                return _config ?? throw new InvalidOperationException(
                    "LinksService не инициализирован. Вызовите LinksService.InitializeAsync().");
            }
        }
    }

    public static async Task InitializeAsync(CancellationToken ct = default)
    {
        var cfg = await LoadAsync(ct).ConfigureAwait(false);
        lock (_lock) { _config = cfg; }
    }

    private static async Task<LinksConfig> LoadAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Constants.LauncherDir);

        var localOverride = Path.Combine(Constants.LauncherDir, "links.json");
        var cachePath     = Path.Combine(Constants.LauncherDir, "links.cache.json");

        var envUrl = Environment.GetEnvironmentVariable("WHITEMC_LINKS_URL");
        var url = !string.IsNullOrWhiteSpace(envUrl) ? envUrl : Constants.LinksManifestUrl;

        // 1) Сетевой links.json
        try
        {
            var json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
            var cfg = JsonSerializer.Deserialize<LinksConfig>(json, Json.CaseInsensitive);
            if (cfg != null)
            {
                await File.WriteAllTextAsync(cachePath, json, ct).ConfigureAwait(false);
                LogService.Log($"[WhiteMC] links.json загружен с {url}");
                return cfg;
            }
        }
        catch (Exception ex)
        {
            LogService.Log($"[WhiteMC] Не удалось скачать links.json ({url}): {ex.Message}");
        }

        // 2) Локальный override ~/.whitemc/links.json
        if (File.Exists(localOverride))
        {
            try
            {
                var json = await File.ReadAllTextAsync(localOverride, ct).ConfigureAwait(false);
                var cfg = JsonSerializer.Deserialize<LinksConfig>(json, Json.CaseInsensitive);
                if (cfg != null)
                {
                    LogService.Log($"[WhiteMC] links.json взят из локального файла: {localOverride}");
                    return cfg;
                }
            }
            catch (Exception ex)
            {
                LogService.Log($"[WhiteMC] Локальный links.json невалиден: {ex.Message}");
            }
        }

        // 3) Кэш
        if (File.Exists(cachePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(cachePath, ct).ConfigureAwait(false);
                var cfg = JsonSerializer.Deserialize<LinksConfig>(json, Json.CaseInsensitive);
                if (cfg != null)
                {
                    LogService.Log("[WhiteMC] links.json взят из кэша (нет сети)");
                    return cfg;
                }
            }
            catch { }
        }

        throw new Exception(
            "Не удалось загрузить links.json ни с сервера, ни из локального файла, ни из кэша.");
    }
}