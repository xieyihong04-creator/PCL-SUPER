using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCL.Living.Features;

internal static class QuickPlayHistoryTests {
    private const string HistoryName = "recent-worlds.json";
    private const string UnavailableWarning = "忽略不可用的最近存档记录";
    private static readonly DateTime Moment = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("Quick Play history construction and disabled reads stay lazy", LazyConstruction);
        yield return ("Quick Play history models retain inherited field shape and defaults", ModelFields);
        yield return ("Quick Play history missing and nonarray worlds remain empty", EmptyEntries);
        yield return ("Quick Play history bad records keep later valid derived identities", ReadEntries);
        yield return ("Quick Play history root JSON failure is not swallowed as a bad record", RootFailure);
        yield return ("Quick Play history equal or newer record short-circuits without writing", RememberEarly);
        yield return ("Quick Play history keeps Binary null-empty identifier and name equality", NullEmptyEquality);
        yield return ("Quick Play history normal remember reads fresh data twice inside original gate", RememberFreshRead);
        yield return ("Quick Play history remember ignores enable gate and deduplicates by path and id", RememberIdentity);
        yield return ("Quick Play history retains forty newest records and stable tie order", FortyLimit);
        yield return ("Quick Play history recent returns five valid rows after filtering", RecentFiltering);
        yield return ("Quick Play history recent retains original derived row objects", RecentIdentity);
        yield return ("Quick Play history recent propagates root failure and creates no missing folders", RecentFailures);
        yield return ("Quick Play log missing path and disabled mode return before storage", LogMissing);
        yield return ("Quick Play log includes root and descendant objects in order", LogContainers);
        yield return ("Quick Play log id and name null differ from missing properties", LogFallbacks);
        yield return ("Quick Play log timestamps retain invariant offset and file fallback", LogTimes);
        yield return ("Quick Play log obtains file timestamp for each valid entry", LogTimePerEntry);
        yield return ("Quick Play log exact one MiB is allowed and overflow propagates", LogLimit);
        yield return ("Quick Play log top-level guard and JSON errors propagate without warning", LogTopErrors);
        yield return ("Quick Play log per-entry IO warns and continues later objects", LogIoBoundary);
        yield return ("Quick Play log non-IO path and history failures propagate", LogNonIoBoundary);
        yield return ("Quick Play log warning sink exceptions propagate", LogWarningFailure);
        yield return ("Quick Play log does not add an outer gate and remember can reenter Monitor", LogGate);
        yield return ("Quick Play log history IO failure is caught only within entry boundary", LogHistoryIo);
        yield return ("Quick Play history factories retain original partial assignment order", FactoryOrder);
    }

    private static void LazyConstruction() {
        using Fixture fixture = new();
        Equal(0, fixture.Events.Count);
        fixture.Enabled = false;
        Equal(0, fixture.History.Recent().Count);
        fixture.History.ReadSessionLog(null!);
        Equal("enabled|enabled", string.Join("|", fixture.Events));
        Equal(false, Directory.Exists(fixture.ProfileRoot));
        Equal(0, fixture.Created.Count);
    }

    private static void ModelFields() {
        foreach ((string name, Type type) in new[] { ("VersionPath", typeof(string)), ("GameFolder", typeof(string)), ("Identifier", typeof(string)), ("Name", typeof(string)), ("PlayedUtc", typeof(DateTime)) }) {
            FieldInfo field = typeof(DerivedWorld).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            Equal(type, field.FieldType);
            Equal(typeof(RecentWorldData), field.DeclaringType);
        }
        foreach (string name in new[] { "Id", "VersionPath", "GameFolder", "LogFile" }) {
            FieldInfo field = typeof(DerivedSession).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            Equal(typeof(string), field.FieldType);
            Equal(typeof(QuickPlaySessionData), field.DeclaringType);
        }
        DerivedWorld world = new();
        Equal<string?>(null, world.Identifier);
        Equal<string?>(null, world.Name);
        Equal(DateTime.MinValue, world.PlayedUtc);
        Equal(DateTimeKind.Unspecified, world.PlayedUtc.Kind);
        DerivedSession session = new();
        Equal(true, Guid.TryParseExact(session.Id, "N", out _));
        Equal<string?>(null, session.LogFile);
    }

    private static void EmptyEntries() {
        using Fixture fixture = new();
        Equal(0, fixture.History.ReadEntries().Count);
        fixture.RawHistory(new JObject(new JProperty("worlds", new JObject())));
        Equal(0, fixture.History.ReadEntries().Count);
        fixture.RawHistory(new JObject(new JProperty("worlds", new JArray(1, "text", JValue.CreateNull(), new JArray()))));
        Equal(0, fixture.History.ReadEntries().Count);
        Equal(0, fixture.Created.Count);
    }

    private static void ReadEntries() {
        using Fixture fixture = new();
        JObject bad = Encode(fixture.Record("bad", "Bad", Moment));
        bad.Remove("gameFolder");
        JObject valid = Encode(fixture.Record("valid", "Valid", Moment));
        fixture.RawHistory(new JObject(new JProperty("worlds", new JArray(bad, 1, valid))));
        fixture.OnData = name => { if (name == HistoryName) Equal(false, Monitor.IsEntered(fixture.Gate)); };
        List<RecentWorldData> rows = fixture.History.ReadEntries();
        Equal(1, rows.Count);
        Equal(2, fixture.Created.Count);
        Same(fixture.Created[1], rows[0]);
        Equal(fixture.VersionPath, fixture.Created[0].VersionPath);
        Equal<string?>(null, fixture.Created[0].GameFolder);
        Equal(Moment, rows[0].PlayedUtc);
        Equal(DateTimeKind.Utc, rows[0].PlayedUtc.Kind);
    }

    private static void RootFailure() {
        using Fixture fixture = new();
        fixture.HistoryFileText("{");
        Throws<JsonReaderException>(() => fixture.History.ReadEntries());
        Equal(0, fixture.Warnings.Count);
        Equal(0, fixture.Created.Count);
    }

    private static void RememberEarly() {
        foreach (DateTime existingTime in new[] { Moment, Moment.AddMinutes(1) }) {
            using Fixture fixture = new();
            fixture.WriteHistory(fixture.Record("World", "Same", existingTime));
            byte[] before = File.ReadAllBytes(fixture.HistoryPath);
            int addresses = 0;
            fixture.OnData = name => { if (name == HistoryName) { addresses++; Equal(true, Monitor.IsEntered(fixture.Gate)); } };
            fixture.History.Remember(fixture.Record("World", "Same", Moment));
            Equal(1, addresses);
            Bytes(before, File.ReadAllBytes(fixture.HistoryPath));
            Equal(false, File.Exists(fixture.HistoryPath + ".pcl-backup"));
        }
    }

    private static void NullEmptyEquality() {
        using Fixture fixture = new();
        JObject record = Encode(fixture.Record("unused", "unused", Moment));
        record["identifier"] = JValue.CreateNull();
        record["name"] = JValue.CreateNull();
        fixture.RawHistory(new JObject(new JProperty("worlds", new JArray(record))));
        byte[] before = File.ReadAllBytes(fixture.HistoryPath);
        RecentWorldData incoming = fixture.Record(null, null, Moment);
        fixture.History.Remember(incoming);
        Bytes(before, File.ReadAllBytes(fixture.HistoryPath));
        Equal(false, File.Exists(fixture.HistoryPath + ".pcl-backup"));
    }

    private static void RememberFreshRead() {
        using Fixture fixture = new();
        fixture.WriteHistory(fixture.Record("old first read", "Old", Moment));
        int addresses = 0;
        fixture.OnData = name => {
            if (name != HistoryName) return;
            addresses++;
            Equal(true, Monitor.IsEntered(fixture.Gate));
            if (addresses == 2) fixture.WriteHistory(fixture.Record("fresh second read", "Fresh", Moment.AddMinutes(1)));
        };
        fixture.History.Remember(fixture.Record("new", "New", Moment.AddMinutes(2)));
        Equal(3, addresses);
        JArray values = fixture.Stored();
        Equal("new", (string?) values[0]["identifier"]);
        Equal("fresh second read", (string?) values[1]["identifier"]);
        Equal(false, values.OfType<JObject>().Any(item => (string?) item["identifier"] == "old first read"));
        Equal(1, (int) fixture.StoredRoot()["version"]!);
        Equal(true, File.Exists(fixture.HistoryPath + ".pcl-backup"));
    }

    private static void RememberIdentity() {
        using Fixture fixture = new();
        fixture.Enabled = false;
        RecentWorldData old = fixture.Record("World", "Old name", Moment);
        fixture.WriteHistory(old, fixture.Record("world", "Case-sensitive identifier", Moment));
        RecentWorldData incoming = fixture.Record("World", "New name", Moment);
        incoming.GameFolder = Path.Combine(fixture.Root, "different game folder");
        incoming.VersionPath = fixture.VersionPath.ToUpperInvariant();
        fixture.History.Remember(incoming);
        Equal(2, fixture.Stored().Count);
        JObject updated = fixture.Stored().OfType<JObject>().Single(item => (string?) item["identifier"] == "World");
        Equal("New name", (string?) updated["name"]);
        Equal(incoming.GameFolder, (string?) updated["gameFolder"]);
        Equal(false, fixture.Events.Contains("enabled"));
    }

    private static void FortyLimit() {
        using Fixture fixture = new();
        fixture.WriteHistory(Enumerable.Range(0, 45).Select(index => fixture.Record("old" + index, "Old", Moment.AddMinutes(index))).ToArray());
        fixture.History.Remember(fixture.Record("new", "New", Moment.AddMinutes(100)));
        JArray stored = fixture.Stored();
        Equal(40, stored.Count);
        Equal("new", (string?) stored[0]["identifier"]);
        Equal("old44", (string?) stored[1]["identifier"]);
        Equal("old6", (string?) stored[39]["identifier"]);
        using Fixture ties = new();
        ties.WriteHistory(Enumerable.Range(0, 40).Select(index => ties.Record("tie" + index, "Tie", Moment)).ToArray());
        ties.History.Remember(ties.Record("new tie", "New tie", Moment));
        Equal(40, ties.Stored().Count);
        Equal("tie0", (string?) ties.Stored()[0]["identifier"]);
        Equal("tie39", (string?) ties.Stored()[39]["identifier"]);
    }

    private static void RecentFiltering() {
        using Fixture fixture = new();
        List<RecentWorldData> records = [];
        RecentWorldData missing = fixture.Record("missing", "Missing version", Moment.AddMinutes(100));
        missing.VersionPath = Path.Combine(fixture.Root, "missing version");
        records.Add(missing);
        string blockedVersion = Path.Combine(fixture.Root, "blocked version");
        Directory.CreateDirectory(blockedVersion);
        foreach (string id in new[] { "blocked", "throws", "missing world" }) {
            RecentWorldData record = fixture.Record(id, id, Moment.AddMinutes(90 - records.Count));
            if (id == "blocked") record.VersionPath = blockedVersion;
            records.Add(record);
        }
        foreach (string id in new[] { "blocked", "throws", "0", "1", "2", "3", "4", "5", "6" }) fixture.World(id);
        for (int index = 0; index < 7; index++) records.Add(fixture.Record(index.ToString(), "Valid", Moment.AddMinutes(70 - index)));
        fixture.WriteHistory(records.ToArray());
        int allowsCalls = 0;
        fixture.Allows = version => { allowsCalls++; Equal(true, Monitor.IsEntered(fixture.Gate)); return version != blockedVersion; };
        bool blockedGuardSeen = false;
        fixture.Guard = (_, candidate) => {
            if (Path.GetFileName(candidate) == "blocked") blockedGuardSeen = true;
            if (Path.GetFileName(candidate) == "throws") throw new InvalidOperationException("world failure");
        };
        List<RecentWorldData> recent = fixture.History.Recent();
        Equal(5, recent.Count);
        Equal("0|1|2|3|4", string.Join("|", recent.Select(item => item.Identifier)));
        Equal(8, allowsCalls);
        Equal(false, blockedGuardSeen);
        Equal(false, Directory.Exists(missing.VersionPath));
        Equal(0, fixture.Warnings.Count);
    }

    private static void RecentIdentity() {
        using Fixture fixture = new();
        fixture.World("World");
        fixture.WriteHistory(fixture.Record("World", "World", Moment));
        List<RecentWorldData> rows = fixture.History.Recent();
        Same(fixture.Created[0], rows[0]);
        Equal(typeof(DerivedWorld), rows[0].GetType());
        fixture.Created[0].Name = "Same object";
        Equal("Same object", rows[0].Name);
    }

    private static void RecentFailures() {
        using Fixture fixture = new();
        fixture.WriteHistory(fixture.Record("Missing", "Missing", Moment));
        Equal(0, fixture.History.Recent().Count);
        Equal(false, Directory.Exists(fixture.VersionPath));
        Equal(false, Directory.Exists(fixture.GameFolder));
        fixture.HistoryFileText("{");
        Throws<JsonReaderException>(() => fixture.History.Recent());
        Equal(0, fixture.Warnings.Count);
    }

    private static void LogMissing() {
        using Fixture fixture = new();
        fixture.History.ReadSessionLog(new DerivedSession());
        fixture.History.ReadSessionLog(fixture.Session());
        Equal("enabled|enabled", string.Join("|", fixture.Events));
        Equal(false, Directory.Exists(fixture.ProfileRoot));
    }

    private static void LogContainers() {
        using Fixture fixture = new();
        fixture.World("Root");
        fixture.World("Nested");
        JObject root = Entry("Root");
        root["type"] = "SiNgLePlAyEr";
        root["nested"] = new JArray(Entry("Nested"), new JObject(new JProperty("type", "multiplayer"), new JProperty("id", "Ignored")));
        fixture.Log(root.ToString());
        List<string> worlds = [];
        fixture.Guard = (scope, candidate) => { if (scope == Path.Combine(fixture.GameFolder, "saves")) worlds.Add(Path.GetFileName(candidate)); };
        fixture.History.ReadSessionLog(fixture.Session());
        Equal("Root|Nested", string.Join("|", worlds));
        Equal(2, fixture.Stored().Count);
        Equal(true, File.Exists(fixture.LogPath));
        using Fixture scalar = new();
        scalar.Log("42");
        scalar.History.ReadSessionLog(scalar.Session());
        Equal(false, File.Exists(scalar.HistoryPath));
        Equal(0, scalar.Created.Count);
        using Fixture bom = new();
        bom.World("UTF16 世界");
        bom.Log("");
        byte[] encoded = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(Entry("UTF16 世界").ToString())];
        File.WriteAllBytes(bom.LogPath, encoded);
        bom.History.ReadSessionLog(bom.Session());
        Equal("UTF16 世界", (string?) bom.Stored().Single()["identifier"]);
        Bytes(encoded, File.ReadAllBytes(bom.LogPath));
    }

    private static void LogFallbacks() {
        using Fixture fixture = new();
        fixture.World("Legacy");
        fixture.World("Named");
        JObject legacy = new(new JProperty("type", "singleplayer"), new JProperty("identifier", "Legacy"));
        JObject nullId = new(new JProperty("type", "singleplayer"), new JProperty("id", JValue.CreateNull()), new JProperty("identifier", "Legacy"));
        JObject nullName = Entry("Named");
        nullName["name"] = JValue.CreateNull();
        fixture.Log(new JArray(legacy, nullId, nullName).ToString());
        fixture.History.ReadSessionLog(fixture.Session());
        JArray values = fixture.Stored();
        Equal("Legacy", (string?) values.OfType<JObject>().Single(item => (string?) item["identifier"] == "Legacy")["name"]);
        Equal("", (string?) values.OfType<JObject>().Single(item => (string?) item["identifier"] == "Named")["name"]);
        Equal(1, fixture.Warnings.Count);
        Equal(InvalidWorldMessage, fixture.Warnings[0].Message);
    }

    private const string InvalidWorldMessage = "存档文件夹名称无效。";

    private static void LogTimes() {
        using Fixture fixture = new();
        fixture.World("Offset");
        fixture.World("Fallback");
        JObject offset = Entry("Offset");
        offset["lastPlayedTime"] = "01/02/2024 03:04:05 +02:00";
        JObject fallback = Entry("Fallback");
        fallback["lastPlayedTime"] = "not a timestamp";
        fixture.Log(new JArray(offset, fallback).ToString());
        File.SetLastWriteTimeUtc(fixture.LogPath, Moment.AddDays(1));
        DateTime expectedFallback = File.GetLastWriteTimeUtc(fixture.LogPath);
        fixture.History.ReadSessionLog(fixture.Session());
        List<RecentWorldData> entries = fixture.History.ReadEntries();
        Equal(new DateTime(2024, 1, 2, 1, 4, 5, DateTimeKind.Utc), entries.Single(item => item.Identifier == "Offset").PlayedUtc);
        Equal(expectedFallback, entries.Single(item => item.Identifier == "Fallback").PlayedUtc);
    }

    private static void LogTimePerEntry() {
        using Fixture fixture = new();
        fixture.World("First");
        fixture.World("Second");
        fixture.Log(new JArray(Entry("First"), Entry("Second")).ToString());
        Dictionary<string, DateTime> expected = new();
        fixture.Guard = (scope, candidate) => {
            if (scope != Path.Combine(fixture.GameFolder, "saves")) return;
            string id = Path.GetFileName(candidate);
            File.SetLastWriteTimeUtc(fixture.LogPath, id == "First" ? Moment : Moment.AddDays(1));
            expected[id] = File.GetLastWriteTimeUtc(fixture.LogPath);
        };
        fixture.History.ReadSessionLog(fixture.Session());
        List<RecentWorldData> rows = fixture.History.ReadEntries();
        Equal(2, expected.Count);
        Equal(2, rows.Count);
        Equal("First|Second", string.Join("|", rows.Select(world => world.Identifier).OrderBy(id => id, StringComparer.Ordinal)));
        foreach (RecentWorldData world in rows) Equal(expected[world.Identifier!], world.PlayedUtc);
    }

    private static void LogLimit() {
        using Fixture fixture = new();
        const int limit = 1024 * 1024;
        fixture.Log("[]" + new string(' ', limit - 2));
        fixture.History.ReadSessionLog(fixture.Session());
        using (FileStream output = new(fixture.LogPath, FileMode.Append)) output.WriteByte(32);
        Throws<IOException>(() => fixture.History.ReadSessionLog(fixture.Session()), "Quick Play 记录过大。");
        Equal(0, fixture.Warnings.Count);
        Equal(true, File.Exists(fixture.LogPath));
    }

    private static void LogTopErrors() {
        using Fixture fixture = new();
        fixture.Log("{");
        IOException sentinel = new("log root guard failed");
        fixture.Guard = (_, _) => throw sentinel;
        Equal(sentinel, Throws<IOException>(() => fixture.History.ReadSessionLog(fixture.Session())));
        Equal(0, fixture.Warnings.Count);
        fixture.Guard = null;
        Throws<JsonReaderException>(() => fixture.History.ReadSessionLog(fixture.Session()));
        Equal(0, fixture.Warnings.Count);
    }

    private static void LogIoBoundary() {
        using Fixture fixture = new();
        fixture.World("Valid");
        fixture.Log(new JArray(Entry("Missing"), Entry("Valid")).ToString());
        fixture.History.ReadSessionLog(fixture.Session());
        Equal(1, fixture.Warnings.Count);
        Equal("Valid", (string?) fixture.Stored().Single()["identifier"]);
        Equal(true, File.Exists(fixture.LogPath));
    }

    private static void LogNonIoBoundary() {
        using Fixture fixture = new();
        fixture.World("Valid");
        fixture.Log(new JArray(Entry("Valid")).ToString());
        ArgumentException sentinel = new("world path argument failed");
        fixture.Guard = (scope, _) => { if (scope == Path.Combine(fixture.GameFolder, "saves")) throw sentinel; };
        Equal(sentinel, Throws<ArgumentException>(() => fixture.History.ReadSessionLog(fixture.Session())));
        Equal(0, fixture.Warnings.Count);
        fixture.Guard = null;
        fixture.HistoryFileText("{");
        Throws<JsonReaderException>(() => fixture.History.ReadSessionLog(fixture.Session()));
        Equal(0, fixture.Warnings.Count);
    }

    private static void LogWarningFailure() {
        using Fixture fixture = new();
        fixture.Log(new JArray(Entry("Missing")).ToString());
        InvalidOperationException sentinel = new("warning sink failed");
        fixture.OnWarn = (_, _) => throw sentinel;
        Equal(sentinel, Throws<InvalidOperationException>(() => fixture.History.ReadSessionLog(fixture.Session())));
        Equal(true, File.Exists(fixture.LogPath));
    }

    private static void LogGate() {
        using Fixture fixture = new();
        fixture.World("World");
        fixture.Log(Entry("World").ToString());
        fixture.Guard = (_, _) => Equal(false, Monitor.IsEntered(fixture.Gate));
        fixture.OnData = name => {
            if (name == HistoryName) Equal(true, Monitor.IsEntered(fixture.Gate));
            if (name == "quickplay-logs") Equal(false, Monitor.IsEntered(fixture.Gate));
        };
        fixture.History.ReadSessionLog(fixture.Session());
        fixture.Guard = (_, _) => Equal(true, Monitor.IsEntered(fixture.Gate));
        fixture.OnData = _ => Equal(true, Monitor.IsEntered(fixture.Gate));
        lock (fixture.Gate) fixture.History.ReadSessionLog(fixture.Session());
        Equal(false, Monitor.IsEntered(fixture.Gate));
    }

    private static void LogHistoryIo() {
        using Fixture fixture = new();
        fixture.World("World");
        fixture.Log(Entry("World").ToString());
        IOException sentinel = new("history access failed");
        fixture.OnData = name => { if (name == HistoryName) throw sentinel; };
        fixture.History.ReadSessionLog(fixture.Session());
        Equal(sentinel, fixture.Warnings.Single());
        Equal(false, File.Exists(fixture.HistoryPath));
    }

    private static void FactoryOrder() {
        using Fixture fixture = new();
        JObject partial = Encode(fixture.Record("World", "Name", Moment));
        partial.Remove("name");
        fixture.RawHistory(new JObject(new JProperty("worlds", new JArray(partial))));
        fixture.OnCreate = world => {
            Equal<string?>(null, world.VersionPath);
            Equal(DateTime.MinValue, world.PlayedUtc);
        };
        Equal(0, fixture.History.ReadEntries().Count);
        DerivedWorld item = fixture.Created.Single();
        Equal(fixture.VersionPath, item.VersionPath);
        Equal(fixture.GameFolder, item.GameFolder);
        Equal("World", item.Identifier);
        Equal<string?>(null, item.Name);
        Equal(DateTime.MinValue, item.PlayedUtc);
    }

    private static JObject Entry(string id) => new(new JProperty("type", "singleplayer"), new JProperty("id", id));
    private static JObject Encode(RecentWorldData world) => new(new JProperty("versionPath", world.VersionPath), new JProperty("gameFolder", world.GameFolder), new JProperty("identifier", world.Identifier), new JProperty("name", world.Name), new JProperty("playedUtc", world.PlayedUtc));
    private static void Same(object expected, object actual) => Equal(true, ReferenceEquals(expected, actual));
    private static void Bytes(byte[] expected, byte[] actual) => Equal(true, expected.SequenceEqual(actual));
    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
    private static T Throws<T>(Action action, string? message = null) where T : Exception {
        try { action(); } catch (T error) {
            if (message is not null) Equal(message, error.Message);
            return error;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private sealed class DerivedWorld : RecentWorldData { }
    private sealed class DerivedSession : QuickPlaySessionData { }

    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PclQuickPlayHistory-" + Guid.NewGuid().ToString("N"));
        internal string ProfileRoot => Path.Combine(Root, "profile");
        internal string VersionPath => Path.Combine(Root, "version");
        internal string GameFolder => Path.Combine(Root, "game");
        internal string HistoryPath => Path.Combine(ProfileRoot, HistoryName);
        internal string LogPath => Path.Combine(ProfileRoot, "quickplay-logs", "fixture.json");
        internal object Gate { get; } = new();
        internal bool Enabled = true;
        internal List<string> Events { get; } = [];
        internal List<DerivedWorld> Created { get; } = [];
        internal List<Exception> Warnings { get; } = [];
        internal Action<string>? OnData;
        internal Action<string, string>? Guard;
        internal Action<Exception, string>? OnWarn;
        internal Action<DerivedWorld>? OnCreate;
        internal Func<string, bool>? Allows;
        internal LocalGameDataStore Data { get; }
        internal QuickPlayHistory History { get; }

        internal Fixture() {
            Directory.CreateDirectory(Root);
            Data = new LocalGameDataStore(() => { Events.Add("root"); return ProfileRoot; }, (_, file) => {
                string name = Path.GetRelativePath(ProfileRoot, file).Replace('\\', '/');
                Events.Add("data:" + name);
                OnData?.Invoke(name);
            });
            History = new QuickPlayHistory(Data, Gate,
                () => { Events.Add("enabled"); return Enabled; },
                (scope, file) => { Events.Add("guard:" + file); Guard?.Invoke(scope, file); },
                (first, second) => { Events.Add("samepath"); return string.Equals(first, second, StringComparison.OrdinalIgnoreCase); },
                version => { Events.Add("allows:" + version); return Allows?.Invoke(version) ?? true; },
                (error, message) => { Equal(UnavailableWarning, message); Events.Add("warn"); Warnings.Add(error); OnWarn?.Invoke(error, message); },
                () => {
                    Events.Add("factory");
                    DerivedWorld world = new();
                    Created.Add(world);
                    OnCreate?.Invoke(world);
                    return world;
                });
        }

        internal DerivedWorld Record(string? id, string? name, DateTime played) => new() { VersionPath = VersionPath, GameFolder = GameFolder, Identifier = id, Name = name, PlayedUtc = played };
        internal DerivedSession Session() => new() { VersionPath = VersionPath, GameFolder = GameFolder, LogFile = LogPath };
        internal void World(string id) {
            Directory.CreateDirectory(VersionPath);
            string folder = Path.Combine(GameFolder, "saves", id);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "level.dat"), [255]);
        }
        internal void HistoryFileText(string text) {
            Directory.CreateDirectory(ProfileRoot);
            File.WriteAllText(HistoryPath, text, new UTF8Encoding(false));
        }
        internal void RawHistory(JObject root) => HistoryFileText(root.ToString());
        internal void WriteHistory(params RecentWorldData[] worlds) => RawHistory(new JObject(new JProperty("version", 1), new JProperty("worlds", new JArray(worlds.Select(Encode)))));
        internal JObject StoredRoot() => JObject.Parse(File.ReadAllText(HistoryPath));
        internal JArray Stored() => (JArray) StoredRoot()["worlds"]!;
        internal void Log(string text) {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.WriteAllText(LogPath, text, new UTF8Encoding(false));
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
