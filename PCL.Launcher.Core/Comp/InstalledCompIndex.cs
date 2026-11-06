using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PCL.Living.Features;

namespace PCL;

/// <summary>
///     扫描当前实例的资源目录，把本地文件映射到平台工程 ID，用于在下载页显示「已安装」。
/// </summary>
public static class InstalledCompIndex
{
    private const int ModrinthChunkSize = 100;
    private const int CurseForgeChunkSize = 500;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>仅模组、资源包与光影包有稳定的实例级目录。</summary>
    public static bool IsSupported(Comp.CompType type) =>
        type is Comp.CompType.Mod or Comp.CompType.ResourcePack or Comp.CompType.Shader;

    public static Task<HashSet<string>> GetInstalledProjectIdsAsync(McInstance instance, Comp.CompType type)
    {
        if (!IsSupported(type))
            return Task.FromResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        string folder;
        try
        {
            folder = Path.Combine(instance.PathIndie, LocalComp.GetPathNameByCompType(type));
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(ex, "[Comp] 读取实例资源目录失败", LauncherLogLevel.Developer);
            return Task.FromResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        var cacheKey = instance.PathInstance + "|" + type;
        return Task.Run(() => GetOrScan(cacheKey, folder, type));
    }

    private static HashSet<string> GetOrScan(string cacheKey, string folder, Comp.CompType type)
    {
        var signature = BuildSignature(folder, type);
        if (Cache.TryGetValue(cacheKey, out var entry)
            && entry.Signature == signature
            && DateTime.UtcNow - entry.Time < CacheDuration)
            return new HashSet<string>(entry.Ids, StringComparer.OrdinalIgnoreCase);

        var ids = Scan(folder, type);
        Cache[cacheKey] = new CacheEntry(DateTime.UtcNow, signature, ids);
        return new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildSignature(string folder, Comp.CompType type)
    {
        try
        {
            if (!Directory.Exists(folder))
                return "missing";

            long count = 0, size = 0, ticks = 0;
            foreach (var file in EnumerateFiles(folder, type))
            {
                var info = new FileInfo(file);
                if (!info.Exists)
                    continue;
                count++;
                size += info.Length;
                ticks = Math.Max(ticks, info.LastWriteTimeUtc.Ticks);
            }

            return $"{count}|{size}|{ticks}";
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(ex, "[Comp] 统计资源目录失败", LauncherLogLevel.Developer);
            return "unavailable";
        }
    }

    private static HashSet<string> Scan(string folder, Comp.CompType type)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(folder))
                return ids;

            var files = EnumerateFiles(folder, type).ToList();
            if (files.Count == 0)
                return ids;

            var sha1ByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                try
                {
                    using var stream = File.OpenRead(file);
                    sha1ByFile[file] = Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
                }
                catch (Exception ex)
                {
                    LauncherCommon.Log(ex, $"[Comp] 计算资源哈希失败：{Path.GetFileName(file)}",
                        LauncherLogLevel.Developer);
                }
            }

            if (sha1ByFile.Count == 0)
                return ids;

            var resolved = InstalledResourceIndexRules.LocalCacheProjectIds(
                CeDependencyProbe.LoadLocalCompCache(),
                sha1ByFile.Values);
            foreach (var projectId in resolved.Values)
                ids.Add(projectId);
            var cacheHits = resolved.Count;

            var unresolved = sha1ByFile
                .Where(pair => !resolved.ContainsKey(pair.Value))
                .ToList();
            if (unresolved.Count > 0)
            {
                _LookupModrinth(unresolved.Select(pair => pair.Value), resolved);
                foreach (var projectId in resolved.Values)
                    ids.Add(projectId);

                var stillUnresolved = unresolved
                    .Where(pair => !resolved.ContainsKey(pair.Value))
                    .Select(pair => pair.Key)
                    .ToList();
                if (stillUnresolved.Count > 0)
                    foreach (var projectId in _LookupCurseForge(stillUnresolved).Values)
                        ids.Add(projectId);
            }

            LauncherCommon.Log(
                $"[Comp] 已安装索引：{type} 共 {files.Count} 个文件，"
                + $"缓存命中 {cacheHits}，联网识别 {resolved.Count - cacheHits}，"
                + $"共 {ids.Count} 个工程");
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(ex, "[Comp] 扫描已安装资源失败", LauncherLogLevel.Developer);
        }

        return ids;
    }

    /// <summary>用 SHA1 批量查询 Modrinth 版本信息，命中后写入 resolved。</summary>
    private static void _LookupModrinth(IEnumerable<string> hashes, Dictionary<string, string> resolved)
    {
        foreach (var chunk in hashes.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(ModrinthChunkSize))
        {
            try
            {
                var body = "{\"hashes\": [\""
                           + string.Join("\",\"", chunk)
                           + "\"], \"algorithm\": \"sha1\"}";
                var response = LauncherCommon.GetJson(
                    GameDownload.DlModRequest(
                        "https://api.modrinth.com/v2/version_files",
                        "POST",
                        body,
                        "application/json")) as JsonObject;
                foreach (var (hash, projectId) in InstalledResourceIndexRules.ModrinthProjectIds(response))
                    resolved[hash] = projectId;
            }
            catch (Exception ex)
            {
                LauncherCommon.Log(ex, "[Comp] 查询 Modrinth 已安装资源失败", LauncherLogLevel.Developer);
            }
        }
    }

    /// <summary>用 CurseForge 指纹批量查询工程 ID，返回「指纹 → 工程 ID」。</summary>
    private static Dictionary<uint, string> _LookupCurseForge(IEnumerable<string> files)
    {
        var fingerprints = new Dictionary<uint, string>();
        foreach (var file in files)
        {
            try
            {
                fingerprints[new LocalComp.LocalCompFile(file).CurseForgeHash] = file;
            }
            catch (Exception ex)
            {
                LauncherCommon.Log(ex, $"[Comp] 计算 CurseForge 指纹失败：{Path.GetFileName(file)}",
                    LauncherLogLevel.Developer);
            }
        }

        var resolved = new Dictionary<uint, string>();
        foreach (var chunk in fingerprints.Keys.Chunk(CurseForgeChunkSize))
        {
            try
            {
                var body = "{\"fingerprints\": [" + string.Join(",", chunk) + "]}";
                var response = LauncherCommon.GetJson(
                    GameDownload.DlModRequest(
                        "https://api.curseforge.com/v1/fingerprints/432",
                        "POST",
                        body,
                        "application/json")) as JsonObject;
                foreach (var (fingerprint, projectId) in InstalledResourceIndexRules.CurseForgeProjectIds(response))
                    resolved[fingerprint] = projectId;
            }
            catch (Exception ex)
            {
                LauncherCommon.Log(ex, "[Comp] 查询 CurseForge 已安装资源失败", LauncherLogLevel.Developer);
            }
        }

        return resolved;
    }

    private static IEnumerable<string> EnumerateFiles(string folder, Comp.CompType type)
    {
        if (type == Comp.CompType.Mod)
            return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(LocalComp.LocalCompFile.IsModFile);

        return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(file => LocalComp.LocalCompFile.IsCompFile(file, type));
    }

    private sealed record CacheEntry(DateTime Time, string Signature, HashSet<string> Ids);
}
