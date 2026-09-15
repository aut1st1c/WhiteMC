using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WhiteMC.Core;

public static class Downloader
{
    public static async Task DownloadAsync(
        string url, string destPath,
        Action<long, long>? onBytes = null,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = destPath + $".{Environment.ProcessId}.{Thread.CurrentThread.ManagedThreadId}.tmp";

        try
        {
            using var resp = await Http.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long total = resp.Content.Headers.ContentLength ?? 0;
            long downloaded = 0;
            long lastReport = 0;

            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            {
                var buf = new byte[256 * 1024];
                int n;
                while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                    downloaded += n;
                    if (onBytes != null)
                    {
                        var now = Environment.TickCount64;
                        if (now - lastReport >= 100)
                        {
                            lastReport = now;
                            try { onBytes(downloaded, total); } catch { }
                        }
                    }
                }
            }

            onBytes?.Invoke(downloaded, total > 0 ? total : downloaded);
            File.Move(tmp, destPath, true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}