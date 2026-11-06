using System.Text.Json.Nodes;
using PCL.Living.Features;

internal static class InstalledResourceIndexTests
{
    private const string HashA = "0123456789abcdef0123456789abcdef01234567";
    private const string HashB = "89abcdef0123456789abcdef0123456789abcdef";

    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("installed index maps local cache keys by hash prefix", LocalCache);
        yield return ("installed index reads Modrinth project ids and skips missing", Modrinth);
        yield return ("installed index reads CurseForge fingerprints and skips malformed", CurseForge);
    }

    private static void LocalCache()
    {
        var cache = JsonNode.Parse(
            $$"""
              {
                "{{HashA}}26.2Fabric": { "Comp": { "Id": "modrinth-a" } },
                "{{HashB}}26.2Fabric": { "Comp": { "Id": "curseforge-b" } },
                "{{HashA.ToUpperInvariant()}}1.21.1Forge": { "Comp": { "Id": "other-loader" } },
                "{{HashA}}26.2Fabric.noId": { "Comp": {} },
                "version": 10
              }
              """)!.AsObject();

        var result = InstalledResourceIndexRules.LocalCacheProjectIds(
            cache,
            [HashA, HashB, "ffffffffffffffffffffffffffffffffffffffff"]);

        Equal(2, result.Count);
        Equal("modrinth-a", result[HashA]);
        Equal("curseforge-b", result[HashB]);
    }

    private static void Modrinth()
    {
        var response = JsonNode.Parse(
            $$"""
              {
                "{{HashA}}": { "project_id": "P7dR8mSH" },
                "{{HashB}}": { "project_id": null },
                "not-a-hash": { "project_id": "ignored" }
              }
              """)!.AsObject();

        var result = InstalledResourceIndexRules.ModrinthProjectIds(response);

        Equal(1, result.Count);
        Equal("P7dR8mSH", result[HashA]);
    }

    private static void CurseForge()
    {
        var response = JsonNode.Parse(
            """
            {
              "data": {
                "exactMatches": [
                  { "id": 238222, "file": { "fileFingerprint": 1234567890 } },
                  { "id": "998877", "file": { "fileFingerprint": "42" } },
                  { "id": 1, "file": {} },
                  { "file": { "fileFingerprint": 7 } }
                ]
              }
            }
            """)!.AsObject();

        var result = InstalledResourceIndexRules.CurseForgeProjectIds(response);

        Equal(2, result.Count);
        Equal("238222", result[1234567890u]);
        Equal("998877", result[42u]);
    }

    private static void Equal(int expected, int actual)
    {
        if (expected != actual) throw new Exception($"Expected count [{expected}], got [{actual}].");
    }

    private static void Equal(string expected, string actual)
    {
        if (expected != actual) throw new Exception($"Expected [{expected}], got [{actual}].");
    }
}
