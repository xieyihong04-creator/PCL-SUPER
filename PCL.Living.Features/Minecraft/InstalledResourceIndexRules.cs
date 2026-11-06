using System.Text.Json.Nodes;

namespace PCL.Living.Features;

/// <summary>把本地文件哈希映射到平台工程 ID 的纯规则。</summary>
public static class InstalledResourceIndexRules
{
    private const int Sha1HexLength = 40;

    /// <summary>本地缓存键为「SHA1 + 版本 + 加载器」，按前缀取 Comp.Id。</summary>
    public static Dictionary<string, string> LocalCacheProjectIds(
        JsonObject? cache,
        IEnumerable<string> sha1Hashes)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (cache is null)
            return result;

        foreach (var hash in sha1Hashes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (hash.Length != Sha1HexLength)
                continue;

            foreach (var (key, value) in cache)
            {
                if (key.Length < Sha1HexLength
                    || !key.StartsWith(hash, StringComparison.OrdinalIgnoreCase))
                    continue;

                var id = (value as JsonObject)?["Comp"]?["Id"];
                if (id is JsonValue valueNode
                    && valueNode.TryGetValue<string>(out var projectId)
                    && !string.IsNullOrWhiteSpace(projectId))
                {
                    result[hash] = projectId;
                    break;
                }
            }
        }

        return result;
    }

    public static Dictionary<string, string> ModrinthProjectIds(JsonObject? response)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (response is null)
            return result;

        foreach (var (hash, value) in response)
        {
            if (hash.Length != Sha1HexLength)
                continue;

            var projectId = (value as JsonObject)?["project_id"];
            if (projectId is JsonValue valueNode
                && valueNode.TryGetValue<string>(out var id)
                && !string.IsNullOrWhiteSpace(id))
                result[hash] = id;
        }

        return result;
    }

    public static Dictionary<uint, string> CurseForgeProjectIds(JsonObject? response)
    {
        var result = new Dictionary<uint, string>();
        if (response?["data"]?["exactMatches"] is not JsonArray matches)
            return result;

        foreach (var match in matches)
        {
            if (match is not JsonObject project)
                continue;

            var projectId = project["id"];
            if (projectId is not JsonValue idNode
                || !TryReadString(idNode, out var id)
                || string.IsNullOrWhiteSpace(id))
                continue;

            var fingerprint = project["file"]?["fileFingerprint"];
            if (fingerprint is not JsonValue fingerprintNode
                || !TryReadUInt(fingerprintNode, out var value))
                continue;

            result[value] = id;
        }

        return result;
    }

    private static bool TryReadString(JsonValue node, out string value)
    {
        if (node.TryGetValue<string>(out var text))
        {
            value = text;
            return true;
        }

        value = node.ToString();
        return value.Length != 0;
    }

    private static bool TryReadUInt(JsonValue node, out uint value)
    {
        if (node.TryGetValue<uint>(out value))
            return true;
        if (node.TryGetValue<string>(out var text) && uint.TryParse(text, out value))
            return true;
        value = 0;
        return false;
    }
}
