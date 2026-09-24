using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WhiteMC.Core;

namespace WhiteMC.Dev;

public static class DevModrinthService
{
    private const int ChunkSize = 100;

    public static async Task<Dictionary<string, ModrinthInfo>> LookupAsync(
        IReadOnlyList<string> hashes,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, ModrinthInfo>(StringComparer.OrdinalIgnoreCase);
        if (hashes.Count == 0) return result;

        for (int i = 0; i < hashes.Count; i += ChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = hashes.Skip(i).Take(ChunkSize).ToList();
            var body = JsonSerializer.Serialize(new
            {
                hashes = chunk,
                algorithm = "sha512"
            });

            try
            {
                var respJson = await Http.PostJsonAsync(
                    "https://api.modrinth.com/v2/version_files",
                    body, ct).ConfigureAwait(false);

                var node = JsonNode.Parse(respJson)?.AsObject();
                if (node == null) continue;

                foreach (var kv in node)
                {
                    var h = kv.Key.ToLowerInvariant();
                    var v = kv.Value?.AsObject();
                    var files = v?["files"]?.AsArray();
                    if (files == null || files.Count == 0) continue;

                    JsonObject? primary = null;
                    foreach (var f in files)
                    {
                        if (f is JsonObject fo && fo["primary"]?.GetValue<bool>() == true)
                        { primary = fo; break; }
                    }
                    primary ??= files[0]?.AsObject();
                    if (primary == null) continue;

                    result[h] = new ModrinthInfo
                    {
                        ProjectId = v?["project_id"]?.GetValue<string>(),
                        VersionId = v?["id"]?.GetValue<string>(),
                        Url = primary["url"]?.GetValue<string>(),
                        Size = primary["size"]?.GetValue<long>() ?? 0,
                        Sha1 = primary["hashes"]?["sha1"]?.GetValue<string>(),
                    };
                }

                log?.Invoke($"[Modrinth] опознано {result.Count} / {Math.Min(i + ChunkSize, hashes.Count)}");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Modrinth] ошибка: {ex.Message}");
            }

            if (i + ChunkSize < hashes.Count)
                await Task.Delay(300, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Достаёт title'ы проектов по их project_id. Параллельно (8 запросов),
    /// чтобы не тормозить на больших сборках.
    /// </summary>
    public static async Task<Dictionary<string, string>> FetchProjectTitlesAsync(
        IReadOnlyList<string> projectIds,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (projectIds.Count == 0) return result;

        var unique = projectIds
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sem = new SemaphoreSlim(8);
        var tasks = unique.Select(async pid =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var url = $"https://api.modrinth.com/v2/project/{pid}";
                var json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
                var node = JsonNode.Parse(json);
                var title = node?["title"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(title))
                {
                    lock (result) result[pid] = title!;
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Modrinth] title {pid}: {ex.Message}");
            }
            finally
            {
                sem.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return result;
    }

    public static async Task ComputeHashesAsync(DevMod mod, CancellationToken ct = default)
    {
        if (!File.Exists(mod.Path)) throw new FileNotFoundException(mod.Path);
        mod.Sha512 = await HashUtil.ComputeAsync(mod.Path, "sha512", ct);
        mod.Sha1   = await HashUtil.ComputeAsync(mod.Path, "sha1", ct);
    }
}