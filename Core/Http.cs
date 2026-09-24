using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WhiteMC.Core;

public static class Http
{
    public static HttpClient Client { get; }

    static Http()
    {
        Client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        })
        {
            Timeout = System.TimeSpan.FromSeconds(120)
        };
        Client.DefaultRequestHeaders.UserAgent.ParseAdd(Constants.UserAgent);
    }

    public static async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        // DEV: локальный файл из output_dir вместо сети.
        if (DevLocalSource.TryGetLocalFile(url, out var local))
        {
            try
            {
                LogService.Log($"[DEV] локальный файл: {url} → {local}");
                return await File.ReadAllBytesAsync(local, ct);
            }
            catch (Exception ex)
            {
                LogService.Log($"[DEV] не удалось прочитать {local}: {ex.Message}; иду в сеть");
            }
        }

        using var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct = default)
        => Encoding.UTF8.GetString(await GetBytesAsync(url, ct));

    public static async Task<string> PostJsonAsync(
        string url, string jsonBody,
        CancellationToken ct = default,
        Dictionary<string, string>? headers = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };

        if (headers != null)
        {
            foreach (var (k, v) in headers)
            {
                req.Headers.TryAddWithoutValidation(k, v);
            }
        }

        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }
}