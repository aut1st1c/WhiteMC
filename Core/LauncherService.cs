using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WhiteMC.Core;

public static class InstanceManager
{
    public static string GetDir(string instanceName, bool create = true)
    {
        var d = Path.Combine(Constants.InstancesDir, instanceName);
        if (create)
        {
            Directory.CreateDirectory(d);
            foreach (var sub in new[]
            {
                "mods", "saves", "config", "resourcepacks", "shaderpacks",
                "logs", "screenshots", "crash-reports"
            })
            {
                Directory.CreateDirectory(Path.Combine(d, sub));
            }
        }
        return d;
    }
}

public static class LauncherService
{
    // ---------------------------------------------------------------------- //
    //  UUID / tokens
    // ---------------------------------------------------------------------- //

    public static string OfflineUuid(string username)
    {
        var md5 = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        md5[6] = (byte)((md5[6] & 0x0F) | 0x30);
        md5[8] = (byte)((md5[8] & 0x3F) | 0x80);
        var hex = Convert.ToHexString(md5).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    public static string RandomToken(int n = 32)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var rng = Random.Shared;
        var sb = new StringBuilder(n);
        for (int i = 0; i < n; i++)
        {
            sb.Append(alphabet[rng.Next(alphabet.Length)]);
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------------- //
    //  Game args
    // ---------------------------------------------------------------------- //

    private static (JsonArray? args, string fmt) ExtractGameArgs(JsonObject? vjson)
    {
        if (vjson == null)
        {
            return (null, "none");
        }

        if (vjson["arguments"] is JsonObject args && args["game"] is JsonArray ga)
        {
            return (ga, "arguments");
        }

        var ma = vjson["minecraftArguments"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(ma))
        {
            JsonArray arr = new();
            arr.Add(ma);
            return (arr, "minecraftArguments");
        }

        return (null, "none");
    }

    private static string? ArgFlag(JsonNode? item)
    {
        if (item is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith("--"))
        {
            return s;
        }

        if (item is JsonObject o && o["value"] is JsonNode val)
        {
            if (val is JsonArray arr && arr.Count > 0
                && arr[0] is JsonValue jv0
                && jv0.TryGetValue<string>(out var s0)
                && s0.StartsWith("--"))
            {
                return s0;
            }

            if (val is JsonValue jv
                && jv.TryGetValue<string>(out var s1)
                && s1.StartsWith("--"))
            {
                return s1;
            }
        }

        return null;
    }

    private static (JsonArray merged, string fmt) MergeGameArgs(JsonObject? primary, JsonObject? fallback)
    {
        var (pArgs, pFmt) = ExtractGameArgs(primary);

        if (pArgs == null || pArgs.Count == 0)
        {
            if (fallback != null && !ReferenceEquals(fallback, primary))
            {
                var (fArgs, fFmt) = ExtractGameArgs(fallback);
                return (fArgs ?? new JsonArray(), fFmt);
            }
            return (new JsonArray(), "none");
        }

        if (pFmt != "arguments")
        {
            return (pArgs, pFmt);
        }

        if (fallback == null || ReferenceEquals(fallback, primary))
        {
            return (pArgs, "arguments");
        }

        var (fArgs2, fFmt2) = ExtractGameArgs(fallback);
        if (fArgs2 == null || fFmt2 != "arguments")
        {
            return (pArgs, "arguments");
        }

        var pFlags = new HashSet<string>();
        foreach (var it in pArgs)
        {
            var f = ArgFlag(it);
            if (f != null)
            {
                pFlags.Add(f);
            }
        }

        JsonArray merged = new();
        foreach (var it in pArgs)
        {
            merged.Add(it?.DeepClone());
        }
        foreach (var it in fArgs2)
        {
            var f = ArgFlag(it);
            if (f != null && pFlags.Contains(f))
            {
                continue;
            }
            merged.Add(it?.DeepClone());
        }
        return (merged, "arguments");
    }

    private static string KeyOfArg(JsonNode? item)
    {
        if (item is JsonValue v && v.TryGetValue<string>(out var s))
        {
            return s.Split('=')[0].Split(' ')[0];
        }

        if (item is JsonObject o && o["value"] is JsonNode val)
        {
            if (val is JsonArray arr && arr.Count > 0
                && arr[0] is JsonValue jv0
                && jv0.TryGetValue<string>(out var s0))
            {
                return s0.Split('=')[0];
            }

            if (val is JsonValue jv && jv.TryGetValue<string>(out var s1))
            {
                return s1.Split('=')[0];
            }
        }

        return "";
    }

    private static List<string> CollectJvmArgs(
        JsonObject? primary,
        JsonObject? fallback,
        Dictionary<string, string> placeholders)
    {
        static JsonArray Extract(JsonObject? v)
        {
            if (v == null)
            {
                return new JsonArray();
            }
            return (v["arguments"] as JsonObject)?["jvm"] as JsonArray ?? new JsonArray();
        }

        var pRaw = Extract(primary);
        var fRaw = (fallback != null && !ReferenceEquals(fallback, primary))
            ? Extract(fallback)
            : new JsonArray();

        var seenKeys = new HashSet<string>();
        var raw = new List<JsonNode?>();
        foreach (var it in pRaw.Concat(fRaw))
        {
            var k = KeyOfArg(it);
            if (k.Length > 0 && !Constants.SingleValueJvmFlags.Contains(k))
            {
                if (!seenKeys.Add(k))
                {
                    continue;
                }
            }
            raw.Add(it);
        }

        string Sub(string s)
        {
            foreach (var (k, v) in placeholders)
            {
                s = s.Replace("${" + k + "}", v);
            }
            return s;
        }

        var result = new List<string>();
        foreach (var item in raw)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s))
            {
                result.Add(Sub(s));
            }
            else if (item is JsonObject o)
            {
                if (!VersionInstaller.RulesAllow(o["rules"] as JsonArray))
                {
                    continue;
                }

                var val = o["value"];
                if (val is JsonArray arr)
                {
                    foreach (var x in arr)
                    {
                        if (x is JsonValue xv && xv.TryGetValue<string>(out var sx))
                        {
                            result.Add(Sub(sx));
                        }
                    }
                }
                else if (val is JsonValue sv && sv.TryGetValue<string>(out var sval))
                {
                    result.Add(Sub(sval));
                }
            }
        }
        return result;
    }

    private static List<string> NormalizeJvmArgs(List<string> args)
    {
        var outList = new List<string>();
        int i = 0;
        while (i < args.Count)
        {
            var a = args[i];
            if (Constants.SingleValueJvmFlags.Contains(a))
            {
                outList.Add(a);
                i++;
                bool first = true;
                while (i < args.Count && !args[i].StartsWith("-"))
                {
                    if (!first)
                    {
                        outList.Add(a);
                    }
                    outList.Add(args[i]);
                    first = false;
                    i++;
                }
            }
            else
            {
                outList.Add(a);
                i++;
            }
        }
        return outList;
    }

    // ---------------------------------------------------------------------- //
    //  Classpath
    // ---------------------------------------------------------------------- //

    private static void CollectNeoForgeLibs(
        string mcVersion,
        JsonObject nfJson,
        HashSet<string> seen,
        List<string> cp,
        Action<string>? logger)
    {
        var libsDir = Path.Combine(NeoForgeService.InstallDir(mcVersion), "libraries");
        if (!Directory.Exists(libsDir))
        {
            logger?.Invoke($"[WhiteMC] ВНИМАНИЕ: не найдена папка libraries NeoForge: {libsDir}");
            return;
        }

        int added = 0;
        var missing = new List<string>();
        if (nfJson["libraries"] is JsonArray arr)
        {
            foreach (var ln in arr)
            {
                if (ln is not JsonObject lib)
                {
                    continue;
                }

                if (!VersionInstaller.RulesAllow(lib["rules"] as JsonArray))
                {
                    continue;
                }

                if ((lib["downloads"] as JsonObject)?["artifact"] is not JsonObject art)
                {
                    continue;
                }

                var p = Path.Combine(libsDir, art["path"]!.GetValue<string>());
                if (File.Exists(p))
                {
                    var s = Path.GetFullPath(p);
                    if (seen.Add(s))
                    {
                        cp.Add(s);
                        added++;
                    }
                }
                else
                {
                    missing.Add(art["path"]!.GetValue<string>());
                }
            }
        }

        logger?.Invoke($"[WhiteMC] NeoForge libraries: добавлено {added} jar из {libsDir}, " +
                       $"отсутствует {missing.Count}");
        if (missing.Count > 0)
        {
            logger?.Invoke($"[WhiteMC] NeoForge libraries: пример отсутствующего: {missing[0]}");
        }
    }

    // ---------------------------------------------------------------------- //
    //  Build args
    // ---------------------------------------------------------------------- //

    /// <summary>
    /// Формирует полный список аргументов для запуска java.
    /// Первый элемент — путь к java.exe. Остальные — аргументы JVM и игры.
    /// </summary>
    public static List<string> BuildArgs(
        string instanceName,
        LauncherSettings settings,
        Action<string>? logger = null)
    {
        var (profileName, versionId, wantsNeoForge) = Profiles.Resolve(instanceName);

        var vdir = Path.Combine(Constants.VersionsDir, versionId);
        var vanillaText = File.ReadAllText(Path.Combine(vdir, $"{versionId}.json"));
        var vanillaJson = JsonNode.Parse(vanillaText)!.AsObject();

        JsonObject? nfJson = null;
        string? nfId = null;
        var nfPath = NeoForgeService.FindProfileJson(versionId);
        if (nfPath != null)
        {
            try
            {
                nfJson = JsonNode.Parse(File.ReadAllText(nfPath))!.AsObject();
                nfId = Path.GetFileNameWithoutExtension(nfPath);
            }
            catch
            {
                // ignore
            }
        }

        bool useNeoForge = wantsNeoForge && nfJson != null;

        if (useNeoForge)
        {
            logger?.Invoke($"[WhiteMC] Запуск через NeoForge-профиль: {nfId}");
        }
        else if (wantsNeoForge && nfJson == null)
        {
            logger?.Invoke($"[WhiteMC] NeoForge для MC {versionId} не найден — запуск ванильной версии");
        }

        var vjson = useNeoForge ? nfJson! : vanillaJson;

        var instDir = InstanceManager.GetDir(profileName);
        var cpSep = OperatingSystem.IsWindows() ? ";" : ":";

        var cp = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddLib(JsonObject lib)
        {
            if (!VersionInstaller.RulesAllow(lib["rules"] as JsonArray))
            {
                return;
            }

            if ((lib["downloads"] as JsonObject)?["artifact"] is not JsonObject art)
            {
                return;
            }

            var p = Path.Combine(Constants.LibrariesDir, art["path"]!.GetValue<string>());
            if (File.Exists(p))
            {
                var s = Path.GetFullPath(p);
                if (seen.Add(s))
                {
                    cp.Add(s);
                }
            }
        }

        if (useNeoForge)
        {
            if (vjson["libraries"] is JsonArray l1)
            {
                foreach (var l in l1)
                {
                    if (l is JsonObject o)
                    {
                        AddLib(o);
                    }
                }
            }

            CollectNeoForgeLibs(versionId, nfJson!, seen, cp, logger);

            if (vanillaJson["libraries"] is JsonArray l2)
            {
                foreach (var l in l2)
                {
                    if (l is JsonObject o)
                    {
                        AddLib(o);
                    }
                }
            }
        }
        else
        {
            if (vanillaJson["libraries"] is JsonArray l3)
            {
                foreach (var l in l3)
                {
                    if (l is JsonObject o)
                    {
                        AddLib(o);
                    }
                }
            }
        }

        var clientJar = Path.Combine(vdir, $"{versionId}.jar");
        if (!File.Exists(clientJar))
        {
            throw new Exception($"Клиентский jar не найден: {clientJar}");
        }
        if (seen.Add(Path.GetFullPath(clientJar)))
        {
            cp.Add(Path.GetFullPath(clientJar));
        }

        var nativesDir = Path.Combine(Constants.NativesDir, versionId);
        Directory.CreateDirectory(nativesDir);

        string? Pick(string field, string? def = null)
        {
            if (vjson[field] is JsonValue jv
                && jv.TryGetValue<string>(out var sv)
                && !string.IsNullOrEmpty(sv))
            {
                return sv;
            }
            if (vanillaJson[field] is JsonValue jv2
                && jv2.TryGetValue<string>(out var sv2)
                && !string.IsNullOrEmpty(sv2))
            {
                return sv2;
            }
            return def;
        }

        var assetIndexName = Pick("assets")
            ?? (vjson["assetIndex"] as JsonObject)?["id"]?.GetValue<string>()
            ?? (vanillaJson["assetIndex"] as JsonObject)?["id"]?.GetValue<string>()
            ?? "legacy";

        var versionType = Pick("type", "release")!;
        var username = string.IsNullOrWhiteSpace(settings.Username) ? "Player" : settings.Username;

        var placeholders = new Dictionary<string, string>
        {
            ["auth_player_name"]   = username,
            ["version_name"]       = versionId,
            ["game_directory"]     = instDir,
            ["assets_root"]        = Constants.AssetsDir,
            ["assets_index_name"]  = assetIndexName,
            ["auth_uuid"]          = OfflineUuid(username),
            ["auth_access_token"]  = RandomToken(),
            ["clientid"]           = RandomToken(16),
            ["auth_xuid"]          = RandomToken(16),
            ["user_type"]          = "legacy",
            ["version_type"]       = versionType,
            ["resolution_width"]   = "854",
            ["resolution_height"]  = "480",
            ["natives_directory"]  = nativesDir,
            ["launcher_name"]      = "WhiteMC",
            ["launcher_version"]   = "1.0",
            ["classpath"]          = string.Join(cpSep, cp),
            ["classpath_separator"]= cpSep,
            ["library_directory"]  = useNeoForge
                ? Path.Combine(NeoForgeService.InstallDir(versionId), "libraries")
                : Constants.LibrariesDir,
        };

        JsonArray? rawArgs;
        string fmt;
        if (useNeoForge)
        {
            (rawArgs, fmt) = MergeGameArgs(vjson, vanillaJson);
        }
        else
        {
            (rawArgs, fmt) = ExtractGameArgs(vanillaJson);
        }

        logger?.Invoke($"[WhiteMC] Источник game-аргументов: {fmt} " +
                       $"({(useNeoForge ? "NeoForge+vanilla" : "vanilla")}), " +
                       $"элементов: {rawArgs?.Count ?? 0}");

        string Sub(string s)
        {
            foreach (var (k, v) in placeholders)
            {
                s = s.Replace("${" + k + "}", v);
            }
            return s;
        }

        var gameArgs = new List<string>();
        if (fmt == "arguments" && rawArgs != null)
        {
            foreach (var item in rawArgs)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    gameArgs.Add(Sub(s));
                }
                else if (item is JsonObject o)
                {
                    var features = new HashSet<string> { "has_custom_resolution" };
                    if (!VersionInstaller.RulesAllow(o["rules"] as JsonArray, features))
                    {
                        continue;
                    }

                    var val = o["value"];
                    if (val is JsonArray arr)
                    {
                        foreach (var x in arr)
                        {
                            if (x is JsonValue xv && xv.TryGetValue<string>(out var sx))
                            {
                                gameArgs.Add(Sub(sx));
                            }
                        }
                    }
                    else if (val is JsonValue sv && sv.TryGetValue<string>(out var sval))
                    {
                        gameArgs.Add(Sub(sval));
                    }
                }
            }
        }
        else if (fmt == "minecraftArguments" && rawArgs is { Count: > 0 })
        {
            var line = rawArgs[0]?.GetValue<string>() ?? "";
            foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                gameArgs.Add(Sub(token));
            }
        }

        var java = PickJava(vanillaJson);

        var profileJvmArgs = NormalizeJvmArgs(CollectJvmArgs(
            vjson,
            useNeoForge ? vanillaJson : null,
            placeholders));

        var cmd = new List<string> { java };
        cmd.Add($"-Xms{settings.Xms}");
        cmd.Add($"-Xmx{settings.Xmx}");

        if (profileJvmArgs.Count > 0)
        {
            cmd.AddRange(profileJvmArgs);
            logger?.Invoke($"[WhiteMC] JVM-аргументов из профиля: {profileJvmArgs.Count}");
        }

        if (useNeoForge)
        {
            cmd.AddRange(Constants.NeoForgeAddOpens);
        }

        var extra = settings.ExtraJvmArgs.Trim();
        if (extra.Length > 0)
        {
            cmd.AddRange(extra.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        if (!cmd.Any(a => a.StartsWith("-Djava.library.path=")))
        {
            cmd.Add($"-Djava.library.path={nativesDir}");
        }

        if (!cmd.Any(a => a == "-cp" || a == "-classpath"))
        {
            cmd.Add("-cp");
            cmd.Add(string.Join(cpSep, cp));
        }

        var mainClass = (vjson["mainClass"] as JsonValue)?.GetValue<string>()
            ?? (vanillaJson["mainClass"] as JsonValue)?.GetValue<string>()
            ?? throw new Exception("Не найден mainClass");
        cmd.Add(mainClass);
        cmd.AddRange(gameArgs);

        if (!gameArgs.Contains("--gameDir"))
        {
            cmd.Add("--gameDir");
            cmd.Add(instDir);
        }

        logger?.Invoke($"[WhiteMC] mainClass: {mainClass}");
        logger?.Invoke($"[WhiteMC] Элементов в classpath: {cp.Count}");
        logger?.Invoke($"[WhiteMC] Всего аргументов командной строки: {cmd.Count}");
        logger?.Invoke($"[WhiteMC] --add-opens применены: {(useNeoForge ? "да" : "нет")}");

        return cmd;
    }

    // Оставлено для обратной совместимости/отладки.
    public static string BuildCommand(
        string instanceName,
        LauncherSettings settings,
        Action<string>? logger = null)
    {
        var args = BuildArgs(instanceName, settings, logger);
        return string.Join(" ", args.Select(Quote));
    }

    private static string Quote(string s) =>
        s.Contains(' ') ? "\"" + s + "\"" : s;

    public static string PickJava(JsonObject vanillaJson)
    {
        int major = VersionInstaller.RequiredJavaMajor(vanillaJson);
        var local = JavaService.InstalledJavaPath(major);
        if (local != null)
        {
            return local;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            try
            {
                var p = Path.Combine(dir, Constants.JavaBinaryName);
                if (File.Exists(p))
                {
                    return p;
                }
            }
            catch
            {
                // ignore bad PATH entries
            }
        }

        throw new Exception(
            $"Java {major} не найдена. Нажмите «Установить» — лаунчер скачает JRE автоматически в {Path.Combine(Constants.JavaDir, major.ToString())}.");
    }

    // ---------------------------------------------------------------------- //
    //  Launch
    // ---------------------------------------------------------------------- //

    public static Process Launch(
        string instanceName,
        LauncherSettings settings,
        Action<string>? onLog = null)
    {
        var (profileName, versionId, _) = Profiles.Resolve(instanceName);
        var instDir = InstanceManager.GetDir(profileName);

        // Формируем аргументы. Первый элемент — путь к java.
        var args = BuildArgs(instanceName, settings, onLog);
        var java = args[0];

        onLog?.Invoke($"Запуск {profileName} (MC {versionId}) для пользователя '{settings.Username}'");
        onLog?.Invoke($"Инстанс: {instDir}");
        onLog?.Invoke($"Java: {java}");

        // Для диагностики — показываем укороченную версию.
        // Полная команда слишком длинная (может быть 15–20k символов).
        var fullDisplay = string.Join(" ", args.Select(Quote));
        var shortDisplay = fullDisplay.Length > 2000
            ? fullDisplay[..2000] + " …[усечено]"
            : fullDisplay;
        onLog?.Invoke("Команда: " + shortDisplay);

        // ВАЖНО: запускаем java напрямую, без cmd.exe / sh.
        // У cmd.exe лимит на командную строку ~8191 символов — MC-команда
        // с 100+ jar-ов в classpath его превышает. CreateProcess (через
        // ArgumentList) имеет лимит 32767 и нормально переваривает нашу команду.
        var psi = new ProcessStartInfo
        {
            FileName = java,
            WorkingDirectory = instDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        for (int i = 1; i < args.Count; i++)
        {
            psi.ArgumentList.Add(args[i]);
        }

        var proc = Process.Start(psi)
            ?? throw new Exception("Не удалось запустить Java-процесс");

        void Pump()
        {
            try
            {
                string? line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    onLog?.Invoke(line);
                }
                while ((line = proc.StandardError.ReadLine()) != null)
                {
                    onLog?.Invoke(line);
                }
            }
            catch (Exception ex)
            {
                onLog?.Invoke($"[WhiteMC] Ошибка чтения вывода Java: {ex.Message}");
            }
        }

        new Thread(Pump) { IsBackground = true }.Start();
        return proc;
    }

    public static void Kill(Process? proc, Action<string>? onLog = null)
    {
        if (proc == null)
        {
            return;
        }

        try
        {
            if (proc.HasExited)
            {
                return;
            }
            proc.Kill(entireProcessTree: true);
            onLog?.Invoke("[WhiteMC] Процесс завершён (kill)");
        }
        catch (Exception ex)
        {
            onLog?.Invoke($"[WhiteMC] Не удалось kill(): {ex.Message}");
        }
    }
}