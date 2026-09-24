using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using WhiteMC.Core;

namespace WhiteMC.Dev;

public static class DevManifestGenerator
{
    public static void GenerateAll(DevState st, Action<string> log)
    {
        var outDir = string.IsNullOrWhiteSpace(st.Settings.OutputDir)
            ? Constants.LauncherDir
            : st.Settings.OutputDir!;
        Directory.CreateDirectory(outDir);

        var version = string.IsNullOrWhiteSpace(st.Settings.ManifestVersion)
            ? DateTime.Now.ToString("yyyy.MM.dd-HHmm")
            : st.Settings.ManifestVersion;
        var baseUrl = st.Settings.BaseUrl.TrimEnd('/');

        // ---------- links.json ----------
        var links = new JsonObject
        {
            ["profiles_url"] = $"{baseUrl}/profiles.json",
            ["optional_manifest_url"] = "",
            ["launcher_version"] = st.Settings.LauncherVersion,
            ["launcher_download_url"] = st.Settings.LauncherDownloadUrl,
        };
        File.WriteAllText(Path.Combine(outDir, "links.json"),
            links.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        log($"links.json → {Path.Combine(outDir, "links.json")}");

        // ---------- profiles.json ----------
        var profilesOut = new JsonObject();

        foreach (var (key, p) in st.Profiles)
        {
            var required = p.Mods.Where(m => !m.IsOptional).ToList();
            var optional = p.Mods.Where(m => m.IsOptional).ToList();
            var unresolvedRequired = required.Where(m => m.Source != "modrinth").ToList();

            var defM = $"manifest-{DevStateService.Slug(key)}";
            var defO = $"optional-{DevStateService.Slug(key)}";
            var defZ = $"mods-{DevStateService.Slug(key)}";

            var manifestUrl = DevStateService.ResolveUrl(baseUrl, p.ManifestUrlOverride, defM, ".json");
            var optionalUrl = DevStateService.ResolveUrl(baseUrl, p.OptionalManifestUrlOverride, defO, ".json");
            var archiveUrl  = DevStateService.ResolveUrl(baseUrl, p.ModsArchiveUrlOverride, defZ, ".zip");

            var manifestFile = DevStateService.ResolveFilename(p.ManifestUrlOverride, defM, ".json");
            var optionalFile = DevStateService.ResolveFilename(p.OptionalManifestUrlOverride, defO, ".json");
            var archiveFile  = DevStateService.ResolveFilename(p.ModsArchiveUrlOverride, defZ, ".zip");

            var prof = new JsonObject
            {
                ["display_name"] = string.IsNullOrEmpty(p.DisplayName) ? key : p.DisplayName,
                ["description"]  = p.Description ?? "",
                ["mc"]           = p.Mc,
                ["neoforge"]     = p.NeoForge,
                ["neoforge_optional"] = p.NeoForgeOptional,
                ["optional_mods"]     = p.OptionalMods || optional.Count > 0,
            };
            var modpack = new JsonObject();
            if (required.Count > 0)  modpack["manifest_url"] = manifestUrl;
            if (optional.Count > 0)  modpack["optional_manifest_url"] = optionalUrl;
            if (unresolvedRequired.Count > 0)
            {
                modpack["components"] = new JsonObject
                {
                    ["mods"] = archiveUrl
                };
            }
            if (modpack.Count > 0) prof["modpack"] = modpack;
            profilesOut[key] = prof;

            // -------- manifest-{key}.json --------
            if (required.Count > 0)
            {
                var modsArr = new JsonArray();
                foreach (var m in required)
                {
                    var e = new JsonObject
                    {
                        ["filename"] = m.Filename,
                        ["sha512"]   = m.Sha512,
                        ["sha1"]     = m.Sha1,
                        ["size"]     = m.Size,
                        ["source"]   = string.IsNullOrEmpty(m.Source) ? "unresolved" : m.Source,
                    };
                    if (m.Source == "modrinth")
                    {
                        e["modrinth_version_id"] = m.ModrinthVersionId;
                        e["modrinth_project_id"] = m.ModrinthProjectId;
                        e["modrinth_url"]        = m.ModrinthUrl;
                    }
                    modsArr.Add(e);
                }
                var mf = new JsonObject
                {
                    ["manifest_version"] = version,
                    ["mods"] = modsArr
                };
                File.WriteAllText(Path.Combine(outDir, manifestFile),
                    mf.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                log($"{manifestFile}: {required.Count} модов → {manifestUrl}");

                var zipPath = Path.Combine(outDir, archiveFile);
                if (unresolvedRequired.Count > 0)
                {
                    using var fs = File.Create(zipPath);
                    using var zf = new ZipArchive(fs, ZipArchiveMode.Create);
                    foreach (var m in unresolvedRequired)
                    {
                        if (!File.Exists(m.Path)) continue;
                        var entry = zf.CreateEntry($"mods/{m.Filename}",
                            CompressionLevel.Optimal);
                        using var es = entry.Open();
                        using var src = File.OpenRead(m.Path);
                        src.CopyTo(es);
                    }
                    log($"{archiveFile}: {unresolvedRequired.Count} файлов → {archiveUrl}");
                }
                else if (File.Exists(zipPath))
                {
                    try { File.Delete(zipPath); } catch { }
                }
            }

            // -------- optional-{key}.json --------
            if (optional.Count > 0)
            {
                var optMods = new JsonArray();
                foreach (var m in optional)
                {
                    var e = new JsonObject
                    {
                        ["filename"]     = m.Filename,
                        ["display_name"] = string.IsNullOrEmpty(m.DisplayName) ? m.Filename : m.DisplayName,
                        ["label"]        = m.DisplayName ?? "",
                        ["description"]  = m.Description ?? "",
                        ["groups"]       = new JsonArray(m.Groups.Select(g => (JsonNode)g!).ToArray()),
                        ["depends_on"]   = new JsonArray(m.DependsOn.Select(g => (JsonNode)g!).ToArray()),
                        ["sha512"]       = m.Sha512,
                        ["sha1"]         = m.Sha1,
                        ["size"]         = m.Size,
                        ["source"]       = string.IsNullOrEmpty(m.Source) ? "unresolved" : m.Source,
                    };
                    if (m.EnabledOnDefault.HasValue)
                        e["enabled_on_default"] = m.EnabledOnDefault.Value;
                    else
                        e["enabled_on_default"] = null;

                    if (m.Source == "modrinth")
                    {
                        e["modrinth_version_id"] = m.ModrinthVersionId;
                        e["modrinth_project_id"] = m.ModrinthProjectId;
                        e["modrinth_url"]        = m.ModrinthUrl;
                    }
                    optMods.Add(e);
                }

                var groupsObj = new JsonObject();
                foreach (var (gid, g) in p.Groups)
                {
                    groupsObj[gid] = new JsonObject
                    {
                        ["display_name"]       = string.IsNullOrEmpty(g.DisplayName) ? gid : g.DisplayName,
                        ["description"]        = g.Description ?? "",
                        ["mode"]               = g.Mode,
                        ["exclusive_set"]      = g.ExclusiveSet is null ? null : JsonValue.Create(g.ExclusiveSet),
                        ["depends_on"]         = new JsonArray(g.DependsOn.Select(x => (JsonNode)x!).ToArray()),
                        ["enabled_on_default"] = g.EnabledOnDefault,
                    };
                }

                var op = new JsonObject
                {
                    ["manifest_version"] = version,
                    ["groups"] = groupsObj,
                    ["mods"]   = optMods
                };
                File.WriteAllText(Path.Combine(outDir, optionalFile),
                    op.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                log($"{optionalFile}: {optional.Count} опц., {p.Groups.Count} групп → {optionalUrl}");
            }
        }

        File.WriteAllText(Path.Combine(outDir, "profiles.json"),
            profilesOut.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        log($"profiles.json: {st.Profiles.Count} профилей → {Path.Combine(outDir, "profiles.json")}");
    }
}