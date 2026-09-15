using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WhiteMC.Core;

public static class JavaService
{
    public static string? InstalledJavaPath(int major)
    {
        var baseDir = Path.Combine(Constants.JavaDir, major.ToString());
        if (!Directory.Exists(baseDir)) return null;

        var direct = Path.Combine(baseDir, "bin", Constants.JavaBinaryName);
        if (File.Exists(direct)) return direct;

        foreach (var p in Directory.EnumerateFiles(baseDir, Constants.JavaBinaryName,
                                                    SearchOption.AllDirectories))
            return p;
        return null;
    }

    public static async Task<string> EnsureJavaAsync(
        int major,
        Action<int, int, string>? onProgress = null,
        CancellationToken ct = default)
    {
        var existing = InstalledJavaPath(major);
        if (existing != null) return existing;

        void P(string m) => onProgress?.Invoke(0, 1, m);

        P($"Java {major}: получение ссылки…");
        var (link, name, kind) = await GetAdoptiumAssetAsync(major, ct);

        Directory.CreateDirectory(Constants.JavaDir);
        var archive = Path.Combine(Constants.JavaDir, $"jre-{major}-{name}");

        P($"Java {major}: скачивание {name}…");
        await Downloader.DownloadAsync(link, archive, ct: ct);

        P($"Java {major}: распаковка…");
        var dest = Path.Combine(Constants.JavaDir, major.ToString());
        await Task.Run(() => ExtractJavaArchive(archive, dest, kind), ct);

        try { File.Delete(archive); } catch { }

        var path = InstalledJavaPath(major)
                   ?? throw new Exception($"Java {major}: не найдена после распаковки");

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                foreach (var p in Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories))
                {
                    var parent = Path.GetFileName(Path.GetDirectoryName(p));
                    if (parent == "bin")
                    {
                        try
                        {
                            var mode = File.GetUnixFileMode(p);
                            File.SetUnixFileMode(p, mode | UnixFileMode.UserExecute
                                                       | UnixFileMode.GroupExecute
                                                       | UnixFileMode.OtherExecute);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        P($"Java {major}: установлена в {path}");
        return path;
    }

    private static async Task<(string link, string name, string kind)> GetAdoptiumAssetAsync(
        int major, CancellationToken ct)
    {
        var osName = OperatingSystem.IsWindows() ? "windows"
                   : OperatingSystem.IsMacOS()   ? "mac"
                                                 : "linux";

        var url = $"{string.Format(Constants.AdoptiumApi, major)}" +
                  $"?architecture={Constants.NativeClassifierArch}&image_type=jre&os={osName}&vendor=eclipse";

        var json = JsonNode.Parse(await Http.GetStringAsync(url, ct));
        var arr = json?.AsArray();
        if (arr == null || arr.Count == 0)
            throw new Exception($"Adoptium не вернул ассет для Java {major} ({osName})");

        var pkg = arr[0]!["binary"]!["package"]!;
        var link = pkg["link"]!.GetValue<string>();
        var name = pkg["name"]!.GetValue<string>();
        var kind = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "zip" : "tar.gz";
        return (link, name, kind);
    }

    private static void ExtractJavaArchive(string archive, string dest, string kind)
    {
        var tmp = dest + ".tmp_extract";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);

        if (kind == "zip")
        {
            ZipFile.ExtractToDirectory(archive, tmp, overwriteFiles: true);
        }
        else
        {
            using var fs = File.OpenRead(archive);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var tar = new TarReader(gz);
            TarEntry? entry;
            while ((entry = tar.GetNextEntry()) != null)
            {
                var safeRel = entry.Name.Replace('\\', '/').TrimStart('/');
                var target = Path.GetFullPath(Path.Combine(tmp, safeRel));
                if (!target.StartsWith(Path.GetFullPath(tmp), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (entry.EntryType == TarEntryType.Directory)
                {
                    Directory.CreateDirectory(target);
                }
                else if (entry.EntryType == TarEntryType.RegularFile ||
                         entry.EntryType == TarEntryType.V7RegularFile)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var ofs = File.Create(target);
                    entry.DataStream?.CopyTo(ofs);
                }
            }
        }

        var direct = Path.Combine(tmp, "bin", Constants.JavaBinaryName);
        string? root = null;
        if (File.Exists(direct))
        {
            root = tmp;
        }
        else
        {
            foreach (var p in Directory.EnumerateFiles(tmp, Constants.JavaBinaryName,
                                                       SearchOption.AllDirectories))
            {
                if (Path.GetFileName(Path.GetDirectoryName(p)) == "bin")
                {
                    root = Path.GetDirectoryName(Path.GetDirectoryName(p));
                    break;
                }
            }
        }

        if (root == null)
            throw new Exception($"Не найден bin/{Constants.JavaBinaryName} в архиве {Path.GetFileName(archive)}");

        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.Move(root, dest);
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
}