using System.Collections;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCL.Living.Features;

internal static class InstanceResourceOnlineTests {
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("resource online unsupported kinds empty versions and directory-only checks remain lazy", LazyChecks);
        yield return ("resource online hash helper reads actual ZIP bytes and releases its input", RealHashes);
        if (OperatingSystem.IsWindows()) {
            yield return ("resource online Windows checks retain per-entry results and batch distinct requests at 100", BatchedRequests);
            yield return ("resource online Windows candidate matching clones selected files and preserves outage status", MatchingAndCandidates);
            yield return ("resource online Windows install preserves selected filename and original recycle payload", InstallRoundTrip);
            yield return ("resource online Windows all downloads validate before replacement and failure cleans staging", InstallPrecommitFailures);
            yield return ("resource online Windows stale content hash stops installation before downloader", StaleBeforeDownload);
            yield return ("resource online Windows failed staged move restores the recycled original", ReplacementRollback);
        }
    }

    private static void LazyChecks() {
        using CancellationTokenSource source = new();
        source.Cancel();
        int factories = 0, requests = 0, fingerprints = 0;
        var host = Host();
        host.Request = (_, _, _) => { requests++; throw new Exception("Unexpected network request."); };
        host.Fingerprint = _ => { fingerprints++; throw new Exception("Unexpected fingerprint."); };
        var engine = Engine(host, factory: () => { factories++; throw new Exception("Unexpected info factory."); });
        foreach (int kind in new[] { -1, 0, 1, 4 }) {
            Equal(false, InstanceResourceOnlineFiles<Entry, RemoteVersion, Download, Info>.Supported(kind));
            Equal(0, engine.Check("invalid\0root", new ExplosiveEntries(), kind, "1.20.1", source.Token).Count);
        }
        Equal(true, InstanceResourceOnlineFiles<Entry, RemoteVersion, Download, Info>.Supported(2));
        Equal(true, InstanceResourceOnlineFiles<Entry, RemoteVersion, Download, Info>.Supported(3));
        foreach (string? game in new[] { null, "", " \t" })
            Equal(0, engine.Check("invalid\0root", new ExplosiveEntries(), 2, game, source.Token).Count);
        Equal(0, engine.Check("invalid\0root", new[] { new Entry { IsDirectory = true, FullPath = "invalid\0entry" } }, 2, "1.20.1", source.Token).Count);
        Equal(0, factories);
        Equal(0, requests);
        Equal(0, fingerprints);
    }

    private static void RealHashes() {
        using Fixture fixture = new();
        string path = fixture.Zip("hash.zip", "old");
        byte[] bytes = File.ReadAllBytes(path);
        Equal(Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(), Hash(path));
        Equal(Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant(), Hash(path, true));
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
        Sequence(bytes, File.ReadAllBytes(path));
    }

    private static void BatchedRequests() {
        using Fixture fixture = new();
        string root = fixture.Directory("packs");
        string template = fixture.Zip("template.zip", "pack");
        string sha1 = Hash(template);
        List<Entry> entries = new();
        for (int index = 0; index < 101; index++) {
            string path = Path.Combine(root, index.ToString("D3") + ".zip");
            File.Copy(template, path);
            entries.Add(Snapshot(path));
        }
        List<(string Url, string? Body)> requests = new();
        int fingerprints = 0;
        var host = Host();
        host.Request = (url, method, body) => {
            Equal("POST", method.Method);
            requests.Add((url, body));
            JObject request = JObject.Parse(body!);
            if (url == "https://api.modrinth.com/v2/version_files") {
                Equal("sha1", (string?)request["algorithm"]);
                Sequence(new[] { sha1 }, request["hashes"]!.Values<string>()!);
                return new JObject();
            }
            if (url == "https://api.curseforge.com/v1/fingerprints/432") {
                Sequence(new uint[] { 7 }, request["fingerprints"]!.Values<uint>());
                return EmptyCurseForge();
            }
            throw new Exception("Unexpected lookup after an empty batch response.");
        };
        host.Fingerprint = path => { Equal(true, File.Exists(path)); fingerprints++; return 7; };
        List<Info> infos = Engine(host).Check(root, entries, 2, "1.20.1", CancellationToken.None);
        Equal(101, infos.Count);
        Equal(101, fingerprints);
        Equal(2, requests.Count(call => call.Url == "https://api.modrinth.com/v2/version_files"));
        Equal(2, requests.Count(call => call.Url == "https://api.curseforge.com/v1/fingerprints/432"));
        for (int index = 0; index < entries.Count; index++) {
            Equal(true, ReferenceEquals(entries[index], infos[index].Entry));
            Equal(sha1, infos[index].Sha1);
            Equal("未匹配到在线项目", infos[index].Status);
            Equal(false, infos[index].HasUpdate);
        }
    }

    private static void MatchingAndCandidates() {
        using Fixture fixture = new();
        Scenario scenario = new(fixture);
        JObject current = RawVersion("old", scenario.OldHash, scenario.OldBytes.Length, "2025-01-01T00:00:00Z");
        ((JArray)current["files"]!).Add(new JObject(new JProperty("filename", "Other.zip"), new JProperty("primary", false), new JProperty("hashes", new JObject(new JProperty("sha1", new string('a', 40))))));
        JObject candidate = RawVersion("new", scenario.NewHash, scenario.NewBytes.Length, "2025-02-01T00:00:00Z");
        List<(Exception Error, string Label)> warnings = new();
        var host = Host();
        Func<JObject, int, RemoteVersion?> parser = host.ParseVersion;
        int parses = 0;
        host.ParseVersion = (data, kind) => { Equal(2, kind); Equal(1, ((JArray)data["files"]!).Count); parses++; return parser(data, kind); };
        host.Warn = (error, label) => warnings.Add((error, label));
        JObject? returnedCurrent = null;
        string before = current.ToString(Formatting.None);
        host.Request = (url, method, body) => {
            if (url == "https://api.modrinth.com/v2/version_files") {
                returnedCurrent = (JObject)current.DeepClone();
                return new JObject(new JProperty(scenario.OldHash, returnedCurrent));
            }
            if (url.StartsWith("https://api.modrinth.com/v2/projects?", StringComparison.Ordinal)) {
                Equal("GET", method.Method);
                Equal<string?>(null, body);
                return new JArray(new JObject(new JProperty("id", "project"), new JProperty("title", "Fixture pack"), new JProperty("project_type", "resourcepack")));
            }
            if (url == "https://api.modrinth.com/v2/version_files/update_individual") {
                JObject request = JObject.Parse(body!);
                JObject query = (JObject)((JArray)request["hashes"]!)[0];
                Equal(scenario.OldHash, (string?)query["hash"]);
                Sequence(new[] { "minecraft" }, query["loaders"]!.Values<string>()!);
                Sequence(new[] { "1.20.1" }, query["game_versions"]!.Values<string>()!);
                Sequence(new[] { "release" }, query["version_types"]!.Values<string>()!);
                return new JObject(new JProperty(scenario.OldHash, candidate.DeepClone()));
            }
            if (url == "https://api.curseforge.com/v1/fingerprints/432") throw new IOException("Synthetic CurseForge outage.");
            throw new Exception("Unexpected online request.");
        };
        Info Check() => Engine(host).Check(scenario.Root, new[] { Snapshot(scenario.Original) }, 2, "1.20.1", CancellationToken.None).Single();
        Info info = Check();
        Equal(true, info.HasUpdate);
        Equal(scenario.NewHash, info.UpdateVersion!.Hash);
        Equal("Fixture pack", info.ProjectName);
        Equal(true, info.Status.Contains("CurseForge", StringComparison.Ordinal));
        Equal(1, warnings.Count);
        Equal(true, warnings[0].Label.Contains("CurseForge", StringComparison.Ordinal));
        Equal(before, returnedCurrent!.ToString(Formatting.None)); // Private file selection must not mutate the response.
        Equal(true, parses >= 2);
        candidate["game_versions"] = new JArray("1.21.1");
        Equal(false, Check().HasUpdate);
        candidate["game_versions"] = new JArray("1.20.1");
        candidate["version_type"] = "alpha";
        Equal(false, Check().HasUpdate);
        Equal(3, warnings.Count);
        Equal(true, warnings.All(item => item.Label.Contains("CurseForge", StringComparison.Ordinal)));
    }

    private static void InstallRoundTrip() {
        using Fixture fixture = new();
        Scenario scenario = new(fixture);
        var host = Host();
        int conversions = 0;
        var originalConversion = host.ToNetFile;
        host.ToNetFile = (version, path, game) => { conversions++; Equal("1.20.1", game); return originalConversion(version, path, game); };
        Engine(host).Install(scenario.Root, new[] { scenario.Info() }, scenario.Recycle, 2, "1.20.1", CancellationToken.None, (files, token) => scenario.CopyDownloads(files, token));
        Equal(1, conversions);
        Sequence(scenario.NewBytes, File.ReadAllBytes(scenario.Original));
        Equal(false, File.Exists(Path.Combine(scenario.Root, "Remote.zip")));
        string backup = Directory.GetDirectories(scenario.Recycle).Single();
        Sequence(scenario.OldBytes, File.ReadAllBytes(Path.Combine(backup, "content")));
        JObject metadata = JObject.Parse(File.ReadAllText(Path.Combine(backup, "resource.json")));
        Equal("Selected.zip", (string?)metadata["name"]);
        Equal(0, Directory.GetDirectories(scenario.Root, ".pcl-import-*").Length);
    }

    private static void InstallPrecommitFailures() {
        foreach (string mode in new[] { "corrupt-second", "cancel-after-download", "download-error" }) {
            using Fixture fixture = new();
            Scenario scenario = new(fixture);
            string second = fixture.Bytes("packs/Second.zip", scenario.OldBytes);
            Info[] infos = { scenario.Info(), scenario.Info(second) };
            using CancellationTokenSource source = new();
            IOException sentinel = new("Synthetic download failure.");
            void DownloadFiles(List<Download> files, CancellationToken token) {
                Equal(2, files.Count);
                if (mode == "download-error") throw sentinel;
                scenario.CopyDownloads(files, token);
                if (mode == "corrupt-second") File.WriteAllBytes(files[1].LocalPath, new byte[scenario.NewBytes.Length]);
                if (mode == "cancel-after-download") source.Cancel();
            }
            Exception error = Throws<Exception>(() => Engine(Host()).Install(scenario.Root, infos, scenario.Recycle, 2, "1.20.1", source.Token, DownloadFiles));
            if (mode == "cancel-after-download") Equal(typeof(OperationCanceledException), error.GetType());
            else Equal(true, error is IOException);
            if (mode == "download-error") Equal(true, ReferenceEquals(sentinel, error));
            Sequence(scenario.OldBytes, File.ReadAllBytes(scenario.Original));
            Sequence(scenario.OldBytes, File.ReadAllBytes(second));
            Equal(false, Directory.Exists(scenario.Recycle));
            Equal(0, Directory.GetDirectories(scenario.Root, ".pcl-import-*").Length);
        }
    }

    private static void StaleBeforeDownload() {
        using Fixture fixture = new();
        Scenario scenario = new(fixture);
        Info info = scenario.Info();
        File.WriteAllBytes(scenario.Original, new byte[scenario.OldBytes.Length]);
        File.SetLastWriteTimeUtc(scenario.Original, info.Entry.ModifiedUtc);
        int downloads = 0;
        Throws<IOException>(() => Engine(Host()).Install(scenario.Root, new[] { info }, scenario.Recycle, 2, "1.20.1", CancellationToken.None, (_, _) => downloads++));
        Equal(0, downloads);
        Sequence(new byte[scenario.OldBytes.Length], File.ReadAllBytes(scenario.Original));
        Equal(false, Directory.Exists(scenario.Recycle));
        Equal(0, Directory.GetDirectories(scenario.Root, ".pcl-import-*").Length);
    }

    private static void ReplacementRollback() {
        using Fixture fixture = new();
        Scenario scenario = new(fixture);
        string? staged = null;
        bool armed = false, removed = false;
        // Fault injection through the host path boundary removes the staged
        // source only when commit revalidates the local file after all downloads.
        // This is not an implementation or equivalence test for real PathUtils.
        string RemovePrefix(string path) {
            if (armed && path == scenario.Original && staged is not null && File.Exists(staged)) {
                File.Delete(staged);
                removed = true;
            }
            return path;
        }
        var engine = Engine(Host(), RemovePrefix);
        Throws<IOException>(() => engine.Install(scenario.Root, new[] { scenario.Info() }, scenario.Recycle, 2, "1.20.1", CancellationToken.None, (files, token) => {
            scenario.CopyDownloads(files, token);
            staged = files.Single().LocalPath;
            armed = true;
        }));
        Equal(true, removed);
        Sequence(scenario.OldBytes, File.ReadAllBytes(scenario.Original));
        Equal(true, Directory.Exists(scenario.Recycle));
        Equal(0, Directory.GetDirectories(scenario.Recycle).Length);
        Equal(0, Directory.GetDirectories(scenario.Root, ".pcl-import-*").Length);
    }

    private static InstanceResourceOnlineFiles<Entry, RemoteVersion, Download, Info> Engine(
            InstanceResourceOnlineHost<RemoteVersion, Download> host, Func<string, string>? removePrefix = null, Func<Info>? factory = null) =>
        new(removePrefix ?? (text => text), host, factory ?? (() => new Info()));

    // POCO versions/downloads implement the generic host contract only. Actual
    // LF ResourceVersion/NetFile/LoaderDownload parsing and consumers remain
    // covered by the unchanged Windows Test-ResourceUpdates.ps1 script.
    private static InstanceResourceOnlineHost<RemoteVersion, Download> Host() => new() {
        ParseVersion = (json, _) => {
            JObject file = (JObject)((JArray)json["files"]!)[0];
            string? url = (string?)file["url"];
            return new RemoteVersion { Hash = (string?)file["hashes"]?["sha1"], FileName = (string?)file["filename"],
                Size = (int)file["size"]!, DownloadAvailable = url is not null,
                DownloadUrls = url is null ? new List<string>() : new List<string> { url },
                ReleaseDate = json["date_published"]!.ToObject<DateTime>(), ReleaseType = (string?)json["version_type"] switch { "release" => 1, "beta" => 2, _ => 3 },
                Version = (string?)json["version_number"], Display = (string?)json["name"] };
        },
        DownloadAvailable = value => value.DownloadAvailable, Size = value => value.Size, Hash = value => value.Hash,
        FileName = value => value.FileName, DownloadUrls = value => value.DownloadUrls, ReleaseDate = value => value.ReleaseDate,
        ReleaseType = value => value.ReleaseType, Version = value => value.Version, Display = value => value.Display,
        ToNetFile = (version, path, game) => new Download { LocalPath = path, GameVersion = game, Version = version },
        Request = (_, _, _) => throw new Exception("Unexpected default request."), Fingerprint = _ => 7,
        Download = (_, _) => throw new Exception("Unexpected default download."), Warn = (_, _) => { }
    };

    private static JObject RawVersion(string id, string hash, int size, string date) => new(
        new JProperty("id", id), new JProperty("project_id", "project"), new JProperty("name", id), new JProperty("version_number", id),
        new JProperty("date_published", date), new JProperty("version_type", "release"), new JProperty("game_versions", new JArray("1.20.1")),
        new JProperty("loaders", new JArray("minecraft")), new JProperty("files", new JArray(new JObject(new JProperty("filename", "Remote.zip"),
            new JProperty("url", "https://cdn.modrinth.com/data/project/Remote.zip"), new JProperty("size", size), new JProperty("primary", true),
            new JProperty("hashes", new JObject(new JProperty("sha1", hash)))))));

    private static JObject EmptyCurseForge() => new(new JProperty("data", new JObject(new JProperty("exactMatches", new JArray()))));
    private static string Hash(string path, bool md5 = false) => InstanceResourceOnlineFiles<Entry, RemoteVersion, Download, Info>.FileHash(path, md5);
    private static Entry Snapshot(string path) => new() { Name = Path.GetFileName(path), FullPath = path, Length = new FileInfo(path).Length,
        ModifiedUtc = File.GetLastWriteTimeUtc(path), CreatedUtc = File.GetCreationTimeUtc(path) };
    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual) { if (!expected.SequenceEqual(actual)) throw new Exception("Sequences differ."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}]."); }
    private static T Throws<T>(Action run) where T : Exception {
        try { run(); } catch (Exception error) { if (error is T expected) return expected; throw; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class Entry : InstanceResourceEntryData { }
    private sealed class Info : InstanceResourceOnlineInfoData<Entry, RemoteVersion> { }
    private sealed class RemoteVersion {
        internal string? Hash, FileName, Version, Display;
        internal int Size, ReleaseType = 1;
        internal bool DownloadAvailable = true;
        internal List<string> DownloadUrls = new();
        internal DateTime ReleaseDate;
    }
    private sealed class Download { internal string LocalPath = null!, GameVersion = null!; internal RemoteVersion Version = null!; }
    private sealed class ExplosiveEntries : IEnumerable<Entry> {
        public IEnumerator<Entry> GetEnumerator() => throw new Exception("Unsupported check enumerated local resources.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Scenario {
        internal readonly string Root, Original, Recycle;
        internal readonly byte[] OldBytes, NewBytes;
        internal readonly string OldHash, NewHash;
        internal Scenario(Fixture fixture) {
            Root = fixture.Directory("packs");
            Original = fixture.Zip("packs/Selected.zip", "old");
            string update = fixture.Zip("new.zip", "new");
            OldBytes = File.ReadAllBytes(Original); NewBytes = File.ReadAllBytes(update);
            OldHash = Hash(Original); NewHash = Hash(update);
            Recycle = Path.Combine(fixture.Root, "recycle");
        }
        internal Info Info(string? path = null) => new() { Entry = Snapshot(path ?? Original), Sha1 = OldHash,
            UpdateVersion = new RemoteVersion { Hash = NewHash, Size = NewBytes.Length, FileName = "Remote.zip", DownloadUrls = new List<string> { "https://cdn.modrinth.com/data/project/Remote.zip" }, Version = "2.0", Display = "Updated" } };
        internal void CopyDownloads(List<Download> files, CancellationToken token) { foreach (Download file in files) { token.ThrowIfCancellationRequested(); File.WriteAllBytes(file.LocalPath, NewBytes); } }
    }

    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PCLLF-ResourceOnline-" + Guid.NewGuid().ToString("N"));
        internal Fixture() => System.IO.Directory.CreateDirectory(Root);
        internal string Directory(string relative) { string path = Owned(relative); System.IO.Directory.CreateDirectory(path); return path; }
        internal string Bytes(string relative, byte[] bytes) { string path = Owned(relative); System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); return path; }
        internal string Zip(string relative, string description) {
            string path = Owned(relative); System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using ZipArchive archive = new(output, ZipArchiveMode.Create, false, Utf8);
            using Stream payload = archive.CreateEntry("pack.mcmeta", CompressionLevel.NoCompression).Open();
            byte[] bytes = Utf8.GetBytes("{\"pack\":{\"description\":\"" + description + "\"}}"); payload.Write(bytes, 0, bytes.Length);
            return path;
        }
        private string Owned(string relative) { string path = Path.GetFullPath(Path.Combine(Root, relative)); if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new Exception("Unsafe online fixture child."); return path; }
        public void Dispose() {
            string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Root.StartsWith(temporary, StringComparison.Ordinal) || !Regex.IsMatch(Path.GetFileName(Root), @"\APCLLF-ResourceOnline-[0-9a-f]{32}\z")) throw new Exception("Unsafe online fixture cleanup.");
            foreach (string path in System.IO.Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            System.IO.Directory.Delete(Root, true);
        }
    }
}
