using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCL.Living.Features;

internal static class QuickPlayFlowTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("Quick Play flow construction and null-session paths remain lazy", Construction);
        yield return ("Quick Play flow support keeps lazy version and snapshot boundary checks", Supported);
        yield return ("Quick Play flow unsupported requested world fails before recording checks", UnsupportedPrepare);
        yield return ("Quick Play flow world arguments apply before disabled or batch recording", PrepareWithoutRecording);
        yield return ("Quick Play flow respects exact user log flag and preserves equals form", ManualPath);
        yield return ("Quick Play flow rechecks support after world argument mutation", SupportRecheck);
        yield return ("Quick Play flow creates derived session before version and game getters", PrepareIdentity);
        yield return ("Quick Play flow creation failure preserves earlier argument mutation", PrepareFailure);
        yield return ("Quick Play flow preparation errors before creation try are not warnings", PrepareOuterErrors);
        yield return ("Quick Play flow tracks real process with original journal order", TrackProcess);
        yield return ("Quick Play flow failed track does not dispose caller process", TrackFailure);
        yield return ("Quick Play flow finishes real Mojang ISO record through history", MojangLifetime);
        yield return ("Quick Play flow finish clears journal and log after JSON failure", FinishReadFailure);
        yield return ("Quick Play flow disabled history still performs final cleanup", FinishDisabled);
        yield return ("Quick Play flow cleanup failure keeps original exception precedence", CleanupOverride);
        yield return ("Quick Play flow cleanup deletes journal before guarded log removal", CleanupOrder);
        yield return ("Quick Play flow live recovery reads log while retaining session files", RecoverLive);
        yield return ("Quick Play flow dead recovery processes log then cleans session files", RecoverDead);
        yield return ("Quick Play flow recovery populates session before checking live pid", RecoverFieldsFirst);
        yield return ("Quick Play flow corrupt journal does not block another recoverable file", RecoverBadJournal);
        yield return ("Quick Play flow recovery warning failure propagates", RecoverWarningFailure);
        yield return ("Quick Play flow dead corrupt log is cleaned while live corrupt log is retained", RecoverBadLog);
        yield return ("Quick Play flow running-state probe ignores enable and session fields", RunningState);
        yield return ("Quick Play flow running-state warning boundary stays per file", RunningWarnings);
        yield return ("Quick Play flow session-folder access stays outside recovery gate and catch", FolderAccess);
        yield return ("Quick Play flow discovery preserves enable and isolation short circuits", DiscoverGates);
        yield return ("Quick Play flow discovery uses real basic NBT and folder-name fallback", DiscoverMetadata);
        yield return ("Quick Play flow discovery creates world before two independent version reads", DiscoverVersionOrder);
        yield return ("Quick Play flow discovery ignores only original IO permission and argument failures", DiscoverIgnoredErrors);
        yield return ("Quick Play flow discovery lets bad gzip and unknown NBT escape", DiscoverFatalMetadata);
        yield return ("Quick Play flow discovery leaves missing oversized and legacy metadata alone", DiscoverNoInfo);
    }

    private static void Construction() {
        using Fixture fixture = new();
        Equal(0, fixture.Events.Count);
        Equal(false, fixture.Flow.Supported(null));
        Equal<QuickPlaySessionData?>(null, fixture.Flow.Prepare(null, null, null!, false));
        fixture.Flow.Track(null, null!);
        fixture.Flow.Finish(null);
        Equal(0, fixture.Events.Count);
        Equal(0, Directory.GetFileSystemEntries(fixture.Root).Length);
    }

    private static void Supported() {
        using Fixture fixture = new();
        fixture.Instance.ValidValue = false;
        Equal(false, fixture.Flow.Supported(fixture.Instance));
        Equal("instance.valid", string.Join("|", fixture.Events));
        fixture.Events.Clear();
        fixture.Instance.ValidValue = true;
        fixture.Instance.MajorValue = 19;
        Equal(false, fixture.Flow.Supported(fixture.Instance));
        Equal("instance.valid|instance.major", string.Join("|", fixture.Events));
        fixture.Events.Clear();
        fixture.Instance.MajorValue = 20;
        Equal(true, fixture.Flow.Supported(fixture.Instance));
        Equal("instance.valid|instance.major|instance.snapshot", string.Join("|", fixture.Events));
        fixture.Instance.SnapshotValue = true;
        fixture.Instance.Released = new DateTime(2023, 4, 4, 23, 59, 59, DateTimeKind.Utc);
        Equal(false, fixture.Flow.Supported(fixture.Instance));
        fixture.Instance.Released = new DateTime(2023, 4, 5, 0, 0, 0, DateTimeKind.Utc).ToLocalTime();
        Equal(true, fixture.Flow.Supported(fixture.Instance));
        fixture.Instance.SnapshotValue = false;
        fixture.Instance.MajorValue = 26;
        Equal(true, fixture.Flow.Supported(fixture.Instance));
    }

    private static void UnsupportedPrepare() {
        using Fixture fixture = new();
        fixture.Instance.MajorValue = 19;
        List<string> arguments = ["--server", "keep"];
        Throws<IOException>(() => fixture.Flow.Prepare(fixture.Instance, "World", arguments, false), "该 Minecraft 版本不支持 Quick Play。");
        Equal("--server|keep", string.Join("|", arguments));
        Equal(false, fixture.Events.Contains("enabled"));
        Equal(0, fixture.Warnings.Count);
        Equal(false, Directory.Exists(fixture.ProfileRoot));
    }

    private static void PrepareWithoutRecording() {
        foreach (bool batch in new[] { false, true }) {
            using Fixture fixture = new();
            fixture.World("World with spaces");
            fixture.Enabled = batch;
            List<string> arguments = ["--server", "remove", "--port", "25565", "--other", "keep"];
            Equal<QuickPlaySessionData?>(null, fixture.Flow.Prepare(fixture.Instance, "World with spaces", arguments, batch));
            Equal("--other|keep|--quickPlaySingleplayer|World with spaces", string.Join("|", arguments));
            Equal(false, Directory.Exists(fixture.ProfileRoot));
            Equal(false, fixture.Events.Contains("sessionfactory"));
        }
    }

    private static void ManualPath() {
        using Fixture fixture = new();
        List<string> manual = ["--quickPlayPath", "user log.json"];
        Equal<QuickPlaySessionData?>(null, fixture.Flow.Prepare(fixture.Instance, null, manual, false));
        Equal("--quickPlayPath|user log.json", string.Join("|", manual));
        Equal(false, Directory.Exists(fixture.ProfileRoot));
        List<string> missingValue = ["--quickPlayPath"];
        Equal<QuickPlaySessionData?>(null, fixture.Flow.Prepare(fixture.Instance, null, missingValue, false));
        List<string> equals = ["--quickPlayPath=user.json"];
        QuickPlaySessionData session = fixture.Flow.Prepare(fixture.Instance, null, equals, false)!;
        Equal("--quickPlayPath=user.json", equals[0]);
        Equal("--quickPlayPath", equals[1]);
        Equal(session.LogFile, equals[2]);
    }

    private static void SupportRecheck() {
        using Fixture fixture = new();
        fixture.World("World");
        Queue<bool> validity = new([true, false]);
        fixture.Instance.Valid = () => validity.Dequeue();
        List<string> arguments = ["--server", "remove"];
        Equal<QuickPlaySessionData?>(null, fixture.Flow.Prepare(fixture.Instance, "World", arguments, false));
        Equal("--quickPlaySingleplayer|World", string.Join("|", arguments));
        Equal(0, validity.Count);
        Equal(false, fixture.Events.Contains("enabled"));
        Equal(false, fixture.Events.Contains("sessionfactory"));
    }

    private static void PrepareIdentity() {
        using Fixture fixture = new();
        DerivedSession expected = new();
        fixture.CreateSession = () => {
            Equal<string?>(null, expected.VersionPath);
            Equal(false, fixture.Events.Contains("instance.version"));
            return expected;
        };
        List<string> arguments = [];
        QuickPlaySessionData actual = fixture.Flow.Prepare(fixture.Instance, null, arguments, false)!;
        Same(expected, actual);
        Equal("sessionfactory|instance.version|instance.folder", string.Join("|", fixture.Events.Where(value => value is "sessionfactory" or "instance.version" or "instance.folder")));
        Equal(fixture.VersionPath, actual.VersionPath);
        Equal(fixture.GameFolder, actual.GameFolder);
        // 原 Address 接收含 / 的完整相对名称；Windows 也保留内部斜杠。
        Equal(Path.Combine(fixture.ProfileRoot, "quickplay-logs/" + actual.Id + ".json"), actual.LogFile);
        Equal(true, Directory.Exists(Path.GetDirectoryName(actual.LogFile!)));
        Equal(false, File.Exists(actual.LogFile));
        Equal("--quickPlayPath", arguments[0]);
        Equal(actual.LogFile, arguments[1]);
    }

    private static void PrepareFailure() {
        using Fixture fixture = new();
        fixture.World("World");
        IOException sentinel = new("session factory failed");
        fixture.CreateSession = () => throw sentinel;
        List<string> arguments = ["--server", "remove"];
        Equal<QuickPlaySessionData?>(null, fixture.Flow.Prepare(fixture.Instance, "World", arguments, false));
        Equal("--quickPlaySingleplayer|World", string.Join("|", arguments));
        Equal(sentinel, fixture.Warnings.Single().Error);
        Equal("无法记录最近存档，不影响本次启动", fixture.Warnings[0].Message);
        InvalidOperationException warningFailure = new("warning failed");
        fixture.OnWarn = (_, _) => throw warningFailure;
        Equal(warningFailure, Throws<InvalidOperationException>(() => fixture.Flow.Prepare(fixture.Instance, null, [], false)));
    }

    private static void PrepareOuterErrors() {
        using Fixture fixture = new();
        Throws<IOException>(() => fixture.Flow.Prepare(fixture.Instance, "Missing", [], false));
        Throws<NullReferenceException>(() => fixture.Flow.Prepare(fixture.Instance, null, null!, false));
        Equal(0, fixture.Warnings.Count);
        Equal<QuickPlaySessionData?>(null, fixture.Flow.Prepare(fixture.Instance, null, null!, true));
    }

    private static void TrackProcess() {
        using Fixture fixture = new();
        QuickPlaySessionData session = fixture.Session();
        using Process game = Process.GetCurrentProcess();
        fixture.OnData = name => { if (name.StartsWith("quickplay-sessions/", StringComparison.Ordinal)) Equal(true, Monitor.IsEntered(fixture.Gate)); };
        fixture.Flow.Track(session, game);
        JObject journal = JObject.Parse(File.ReadAllText(fixture.JournalPath(session)));
        Equal("versionPath|gameFolder|logFile|pid|started", string.Join("|", journal.Properties().Select(item => item.Name)));
        Equal(game.Id, (int) journal["pid"]!);
        Equal(game.StartTime.ToUniversalTime().Ticks, (long) journal["started"]!);
        Equal(session.LogFile, (string?) journal["logFile"]);
        Equal(false, game.HasExited);
        Equal(false, game.Handle == IntPtr.Zero);
    }

    private static void TrackFailure() {
        using Fixture fixture = new();
        QuickPlaySessionData session = fixture.Session();
        using Process game = Process.GetCurrentProcess();
        IOException sentinel = new("journal failed");
        fixture.OnData = name => { if (name.StartsWith("quickplay-sessions/", StringComparison.Ordinal)) throw sentinel; };
        Equal(sentinel, Throws<IOException>(() => fixture.Flow.Track(session, game)));
        Equal(false, File.Exists(fixture.JournalPath(session)));
        Equal(false, game.HasExited);
        Equal(false, game.Handle == IntPtr.Zero);
    }

    private static void MojangLifetime() {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            using Fixture fixture = new();
            fixture.World("World with spaces");
            QuickPlaySessionData session = fixture.Flow.Prepare(fixture.Instance, null, [], false)!;
            using Process game = Process.GetCurrentProcess();
            fixture.Flow.Track(session, game);
            fixture.Log(session, "[{\"type\":\"singleplayer\",\"id\":\"World with spaces\",\"name\":\"Mojang world\",\"lastPlayedTime\":\"2026-10-01T12:00:00Z\",\"gamemode\":\"survival\"}]");
            fixture.Flow.Finish(session);
            RecentWorldData world = fixture.History.ReadEntries().Single();
            Equal("World with spaces", world.Identifier);
            Equal("Mojang world", world.Name);
            Equal(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), world.PlayedUtc);
            Equal(DateTimeKind.Utc, world.PlayedUtc.Kind);
            Equal(false, File.Exists(fixture.JournalPath(session)));
            Equal(false, File.Exists(session.LogFile));
            Equal(1, fixture.History.Recent().Count);
            Equal(false, game.HasExited);
        } finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void FinishReadFailure() {
        using Fixture fixture = new();
        QuickPlaySessionData session = fixture.Session();
        fixture.RawJournal(session, false);
        fixture.Log(session, "{");
        Throws<JsonReaderException>(() => fixture.Flow.Finish(session));
        Equal(false, File.Exists(fixture.JournalPath(session)));
        Equal(false, File.Exists(session.LogFile));
        Equal(0, fixture.Warnings.Count);
        Equal(false, Monitor.IsEntered(fixture.Gate));
    }

    private static void FinishDisabled() {
        using Fixture fixture = new();
        fixture.Enabled = false;
        QuickPlaySessionData session = fixture.Session();
        fixture.RawJournal(session, false);
        fixture.Log(session, "{");
        fixture.Flow.Finish(session);
        Equal(false, File.Exists(fixture.JournalPath(session)));
        Equal(false, File.Exists(session.LogFile));
        Equal(0, fixture.Warnings.Count);
    }

    private static void CleanupOverride() {
        using Fixture fixture = new();
        QuickPlaySessionData session = fixture.Session();
        fixture.RawJournal(session, false);
        fixture.Log(session, "{");
        IOException cleanupFailure = new("cleanup journal lookup failed");
        fixture.OnData = name => {
            if (name.StartsWith("quickplay-sessions/", StringComparison.Ordinal)) {
                Equal(true, Monitor.IsEntered(fixture.Gate));
                throw cleanupFailure;
            }
        };
        Equal(cleanupFailure, Throws<IOException>(() => fixture.Flow.Finish(session)));
        Equal(true, File.Exists(fixture.JournalPath(session)));
        Equal(true, File.Exists(session.LogFile));
    }

    private static void CleanupOrder() {
        using Fixture fixture = new();
        fixture.Enabled = false;
        QuickPlaySessionData session = fixture.Session();
        fixture.RawJournal(session, false);
        fixture.Log(session, "[]");
        ArgumentException sentinel = new("log cleanup guard failed");
        fixture.Guard = (_, file) => {
            Equal(true, Monitor.IsEntered(fixture.Gate));
            Equal(session.LogFile, file);
            Equal(false, File.Exists(fixture.JournalPath(session)));
            throw sentinel;
        };
        Equal(sentinel, Throws<ArgumentException>(() => fixture.Flow.Finish(session)));
        Equal(false, File.Exists(fixture.JournalPath(session)));
        Equal(true, File.Exists(session.LogFile));
    }

    private static void RecoverLive() {
        using Fixture fixture = new();
        fixture.World("World");
        QuickPlaySessionData session = fixture.Session();
        fixture.RawJournal(session, true);
        fixture.Log(session, "[{\"type\":\"singleplayer\",\"id\":\"World\"}]");
        fixture.Guard = (_, _) => Equal(true, Monitor.IsEntered(fixture.Gate));
        fixture.Flow.RecoverExitedSessions();
        Equal("World", fixture.History.ReadEntries().Single().Identifier);
        Equal(true, File.Exists(fixture.JournalPath(session)));
        Equal(true, File.Exists(session.LogFile));
        Equal(session.Id, fixture.CreatedSessions[0].Id);
        Equal(session.LogFile, fixture.CreatedSessions[0].LogFile);
        Equal(0, fixture.Warnings.Count);
    }

    private static void RecoverDead() {
        using Fixture fixture = new();
        fixture.World("World");
        QuickPlaySessionData session = fixture.Session();
        fixture.RawJournal(session, false);
        fixture.Log(session, "[{\"type\":\"singleplayer\",\"identifier\":\"World\"}]");
        fixture.Guard = (_, _) => Equal(true, Monitor.IsEntered(fixture.Gate));
        fixture.Flow.RecoverExitedSessions();
        Equal("World", fixture.History.ReadEntries().Single().Identifier);
        Equal(false, File.Exists(fixture.JournalPath(session)));
        Equal(false, File.Exists(session.LogFile));
        Equal(0, fixture.Warnings.Count);
    }

    private static void RecoverFieldsFirst() {
        using Fixture fixture = new();
        QuickPlaySessionData session = fixture.Session();
        JObject journal = fixture.Journal(session, true);
        journal.Remove("logFile");
        fixture.Raw(fixture.JournalPath(session), journal.ToString());
        fixture.Flow.RecoverExitedSessions();
        Equal(1, fixture.CreatedSessions.Count);
        Equal(session.Id, fixture.CreatedSessions[0].Id);
        Equal(session.VersionPath, fixture.CreatedSessions[0].VersionPath);
        Equal(session.GameFolder, fixture.CreatedSessions[0].GameFolder);
        Equal<string?>(null, fixture.CreatedSessions[0].LogFile);
        Equal(1, fixture.Warnings.Count);
        Equal("恢复最近存档记录失败", fixture.Warnings[0].Message);
        Equal(true, fixture.Flow.HasRunningSessions());
        Equal(1, fixture.CreatedSessions.Count);
    }

    private static void RecoverBadJournal() {
        using Fixture fixture = new();
        QuickPlaySessionData session = fixture.Session();
        fixture.RawJournal(session, false);
        fixture.Log(session, "[]");
        string bad = Path.Combine(fixture.ProfileRoot, "quickplay-sessions", "bad.json");
        fixture.Raw(bad, "{");
        fixture.Flow.RecoverExitedSessions();
        Equal(true, File.Exists(bad));
        Equal(false, File.Exists(fixture.JournalPath(session)));
        Equal(false, File.Exists(session.LogFile));
        Equal(1, fixture.Warnings.Count);
        Equal(true, fixture.Warnings[0].Error is JsonException);
    }

    private static void RecoverWarningFailure() {
        using Fixture fixture = new();
        fixture.Raw(Path.Combine(fixture.ProfileRoot, "quickplay-sessions", "bad.json"), "{");
        InvalidOperationException sentinel = new("warning failed");
        fixture.OnWarn = (_, _) => throw sentinel;
        Equal(sentinel, Throws<InvalidOperationException>(() => fixture.Flow.RecoverExitedSessions()));
        Equal(false, Monitor.IsEntered(fixture.Gate));
    }

    private static void RecoverBadLog() {
        foreach (bool live in new[] { false, true }) {
            using Fixture fixture = new();
            QuickPlaySessionData session = fixture.Session();
            fixture.RawJournal(session, live);
            fixture.Log(session, "{");
            fixture.Flow.RecoverExitedSessions();
            Equal(live, File.Exists(fixture.JournalPath(session)));
            Equal(live, File.Exists(session.LogFile));
            Equal(1, fixture.Warnings.Count);
            Equal("恢复最近存档记录失败", fixture.Warnings[0].Message);
        }
    }

    private static void RunningState() {
        using Fixture fixture = new();
        fixture.Enabled = false;
        using Process game = Process.GetCurrentProcess();
        fixture.Raw(Path.Combine(fixture.ProfileRoot, "quickplay-sessions", "live.json"), new JObject(new JProperty("pid", game.Id), new JProperty("started", game.StartTime.ToUniversalTime().Ticks)).ToString());
        Equal(true, fixture.Flow.HasRunningSessions());
        Equal(false, fixture.Events.Contains("enabled"));
        Equal(false, fixture.Events.Contains("sessionfactory"));
        Equal(0, fixture.Warnings.Count);
    }

    private static void RunningWarnings() {
        using Fixture fixture = new();
        string bad = Path.Combine(fixture.ProfileRoot, "quickplay-sessions", "bad.json");
        fixture.Raw(bad, "{");
        Equal(false, fixture.Flow.HasRunningSessions());
        Equal("读取游戏运行状态失败", fixture.Warnings.Single().Message);
        InvalidOperationException sentinel = new("warning failed");
        fixture.OnWarn = (_, _) => throw sentinel;
        Equal(sentinel, Throws<InvalidOperationException>(() => fixture.Flow.HasRunningSessions()));
    }

    private static void FolderAccess() {
        using Fixture fixture = new();
        ArgumentException sentinel = new("folder lookup failed");
        fixture.OnData = name => {
            if (name == "quickplay-sessions") {
                Equal(false, Monitor.IsEntered(fixture.Gate));
                throw sentinel;
            }
        };
        Equal(sentinel, Throws<ArgumentException>(() => fixture.Flow.RecoverExitedSessions()));
        Equal(sentinel, Throws<ArgumentException>(() => fixture.Flow.HasRunningSessions()));
        Equal(0, fixture.Warnings.Count);
    }

    private static void DiscoverGates() {
        using Fixture fixture = new();
        fixture.Enabled = false;
        fixture.Flow.DiscoverExistingWorlds(fixture.Instance);
        Equal("enabled", string.Join("|", fixture.Events));
        fixture.Enabled = true;
        fixture.Events.Clear();
        fixture.Instance.SavedValue = false;
        fixture.Flow.DiscoverExistingWorlds(fixture.Instance);
        Equal("enabled|instance.valid|instance.major|instance.snapshot|instance.saved", string.Join("|", fixture.Events));
        fixture.Events.Clear();
        fixture.Instance.SavedValue = true;
        fixture.Instance.IsolatedValue = false;
        fixture.Flow.DiscoverExistingWorlds(fixture.Instance);
        Equal(false, fixture.Events.Contains("instance.version"));
        Equal(false, fixture.Events.Contains("instance.folder"));
        Equal(false, Directory.Exists(fixture.ProfileRoot));
    }

    private static void DiscoverMetadata() {
        using Fixture fixture = new();
        long first = 1730000000000;
        fixture.IsolatedWorld("Named folder", "NBT display name", first);
        fixture.IsolatedWorld("Fallback folder", "   ", first + 1000);
        string[] files = [Path.Combine(fixture.VersionPath, "saves", "Named folder", "level.dat"), Path.Combine(fixture.VersionPath, "saves", "Fallback folder", "level.dat")];
        byte[][] originalBytes = files.Select(File.ReadAllBytes).ToArray();
        FileAttributes[] attributes = files.Select(File.GetAttributes).ToArray();
        fixture.Guard = (_, _) => Equal(false, Monitor.IsEntered(fixture.Gate));
        fixture.OnData = name => { if (name == "recent-worlds.json") Equal(true, Monitor.IsEntered(fixture.Gate)); };
        try {
            for (int index = 0; index < files.Length; index++) File.SetAttributes(files[index], attributes[index] | FileAttributes.ReadOnly);
            fixture.Flow.DiscoverExistingWorlds(fixture.Instance);
        } finally {
            for (int index = 0; index < files.Length; index++) File.SetAttributes(files[index], attributes[index]);
            fixture.OnData = null;
        }
        for (int index = 0; index < files.Length; index++) Equal(true, originalBytes[index].SequenceEqual(File.ReadAllBytes(files[index])));
        List<RecentWorldData> worlds = fixture.History.ReadEntries();
        Equal(2, worlds.Count);
        RecentWorldData named = worlds.Single(item => item.Identifier == "Named folder");
        Equal("NBT display name", named.Name);
        Equal(DateTimeOffset.FromUnixTimeMilliseconds(first).UtcDateTime, named.PlayedUtc);
        Equal(fixture.VersionPath, named.VersionPath);
        Equal(fixture.VersionPath, named.GameFolder);
        Equal("Fallback folder", worlds.Single(item => item.Identifier == "Fallback folder").Name);
        Equal(false, Directory.Exists(fixture.GameFolder));
        Equal(0, fixture.Warnings.Count);
    }

    private static void DiscoverVersionOrder() {
        using Fixture fixture = new();
        fixture.IsolatedWorld("World", "Name", 1730000000000);
        Queue<string> paths = new([fixture.VersionPath, Path.Combine(fixture.Root, "second path"), Path.Combine(fixture.Root, "third path")]);
        fixture.Instance.Version = () => paths.Dequeue();
        fixture.OnWorld = _ => Equal(false, Monitor.IsEntered(fixture.Gate));
        fixture.Flow.DiscoverExistingWorlds(fixture.Instance);
        Equal("instance.version|worldfactory|instance.version|instance.version", string.Join("|", fixture.Events.Where(value => value is "instance.version" or "worldfactory")));
        Equal(0, paths.Count);
        RecentWorldData result = fixture.CreatedWorlds[0];
        Equal(Path.Combine(fixture.Root, "second path"), result.VersionPath);
        Equal(Path.Combine(fixture.Root, "third path"), result.GameFolder);
    }

    private static void DiscoverIgnoredErrors() {
        using Fixture fixture = new();
        foreach (string id in new[] { "io", "permission", "argument", "good" }) fixture.IsolatedWorld(id, id, 1730000000000);
        fixture.GzipWorld("eos", []);
        string saves = Path.Combine(fixture.VersionPath, "saves");
        fixture.Guard = (scope, file) => {
            if (scope != saves) return;
            switch (Path.GetFileName(file)) {
                case "io": throw new IOException("fixture IO");
                case "permission": throw new UnauthorizedAccessException("fixture permission");
                case "argument": throw new ArgumentException("fixture argument");
            }
        };
        fixture.Flow.DiscoverExistingWorlds(fixture.Instance);
        Equal("good", fixture.History.ReadEntries().Single().Identifier);
        Equal(0, fixture.Warnings.Count);
    }

    private static void DiscoverFatalMetadata() {
        foreach (bool badGzip in new[] { false, true }) {
            using Fixture fixture = new();
            if (badGzip) {
                string folder = Path.Combine(fixture.VersionPath, "saves", "bad");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "level.dat"), "not a gzip stream");
            } else {
                fixture.GzipWorld("unknown", [10, 0, 0, 99, 0, 1, 88]);
            }
            Throws<InvalidDataException>(() => fixture.Flow.DiscoverExistingWorlds(fixture.Instance));
            Equal(0, fixture.Warnings.Count);
            Equal(false, File.Exists(fixture.HistoryPath));
        }
    }

    private static void DiscoverNoInfo() {
        using Fixture fixture = new();
        Directory.CreateDirectory(Path.Combine(fixture.VersionPath, "saves", "missing"));
        fixture.IsolatedWorld("no timestamp", "Old", 0);
        string large = Path.Combine(fixture.VersionPath, "saves", "large");
        Directory.CreateDirectory(large);
        using (FileStream file = File.Create(Path.Combine(large, "level.dat"))) file.SetLength(8 * 1024 * 1024 + 1);
        fixture.Flow.DiscoverExistingWorlds(fixture.Instance);
        Equal(0, fixture.CreatedWorlds.Count);
        Equal(false, File.Exists(fixture.HistoryPath));
        Equal(0, fixture.Warnings.Count);
    }

    private static void Same(object expected, object actual) => Equal(true, ReferenceEquals(expected, actual));
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
    private sealed class DerivedSession : QuickPlaySessionData { }
    private sealed class DerivedWorld : RecentWorldData { }

    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PclQuickPlayFlow-" + Guid.NewGuid().ToString("N"));
        internal string VersionPath => Path.Combine(Root, "version");
        internal string GameFolder => Path.Combine(Root, "game");
        internal string ProfileRoot => Path.Combine(Root, "profile");
        internal string HistoryPath => Path.Combine(ProfileRoot, "recent-worlds.json");
        internal object Gate { get; } = new();
        internal List<string> Events { get; } = [];
        internal List<(Exception Error, string Message)> Warnings { get; } = [];
        internal List<QuickPlaySessionData> CreatedSessions { get; } = [];
        internal List<RecentWorldData> CreatedWorlds { get; } = [];
        internal bool Enabled = true;
        internal Action<string>? OnData;
        internal Action<string, string>? Guard;
        internal Action<Exception, string>? OnWarn;
        internal Action<RecentWorldData>? OnWorld;
        internal Func<QuickPlaySessionData>? CreateSession;
        internal FakeInstance Instance { get; }
        internal LocalGameDataStore Data { get; }
        internal QuickPlayHistory History { get; }
        internal QuickPlayFlow Flow { get; }

        internal Fixture() {
            Directory.CreateDirectory(Root);
            Instance = new FakeInstance(this);
            Data = new LocalGameDataStore(() => { Events.Add("root"); return ProfileRoot; }, (_, file) => {
                string name = Path.GetRelativePath(ProfileRoot, file).Replace('\\', '/');
                Events.Add("data:" + name);
                OnData?.Invoke(name);
            });
            History = new QuickPlayHistory(Data, Gate, ReadEnabled, CheckPath,
                (first, second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase), _ => true, Warn, CreateWorld);
            Flow = new QuickPlayFlow(History, Data, Gate, ReadEnabled, CheckPath, Warn,
                () => {
                    Events.Add("sessionfactory");
                    QuickPlaySessionData session = CreateSession?.Invoke() ?? new DerivedSession();
                    CreatedSessions.Add(session);
                    return session;
                }, CreateWorld);
        }
        private bool ReadEnabled() { Events.Add("enabled"); return Enabled; }
        private void CheckPath(string root, string file) { Events.Add("guard:" + file); Guard?.Invoke(root, file); }
        private void Warn(Exception error, string message) { Events.Add("warn"); Warnings.Add((error, message)); OnWarn?.Invoke(error, message); }
        private RecentWorldData CreateWorld() {
            Events.Add("worldfactory");
            DerivedWorld world = new();
            CreatedWorlds.Add(world);
            OnWorld?.Invoke(world);
            return world;
        }
        internal DerivedSession Session() {
            DerivedSession session = new() { VersionPath = VersionPath, GameFolder = GameFolder };
            session.LogFile = Path.Combine(ProfileRoot, "quickplay-logs", session.Id + ".json");
            return session;
        }
        internal string JournalPath(QuickPlaySessionData session) => Path.Combine(ProfileRoot, "quickplay-sessions", session.Id + ".json");
        internal JObject Journal(QuickPlaySessionData session, bool live) {
            using Process game = Process.GetCurrentProcess();
            return new JObject(new JProperty("versionPath", session.VersionPath), new JProperty("gameFolder", session.GameFolder), new JProperty("logFile", session.LogFile),
                new JProperty("pid", live ? game.Id : int.MaxValue), new JProperty("started", live ? game.StartTime.ToUniversalTime().Ticks : 1L));
        }
        internal void RawJournal(QuickPlaySessionData session, bool live) => Raw(JournalPath(session), Journal(session, live).ToString());
        internal void Log(QuickPlaySessionData session, string text) => Raw(session.LogFile!, text);
        internal void Raw(string path, string text) {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        internal void World(string identifier) {
            Directory.CreateDirectory(VersionPath);
            string folder = Path.Combine(GameFolder, "saves", identifier);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "level.dat"), [255]);
        }
        internal void IsolatedWorld(string identifier, string name, long millis) {
            using MemoryStream nbt = new();
            Header(nbt, 10, "");
            Header(nbt, 10, "Data");
            Header(nbt, 8, "LevelName");
            Name(nbt, name);
            Header(nbt, 4, "LastPlayed");
            Span<byte> time = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(time, millis);
            nbt.Write(time);
            nbt.WriteByte(0);
            nbt.WriteByte(0);
            GzipWorld(identifier, nbt.ToArray());
        }
        internal void GzipWorld(string identifier, byte[] bytes) {
            string folder = Path.Combine(VersionPath, "saves", identifier);
            Directory.CreateDirectory(folder);
            using FileStream file = File.Create(Path.Combine(folder, "level.dat"));
            using GZipStream gzip = new(file, CompressionMode.Compress);
            gzip.Write(bytes, 0, bytes.Length);
        }
        private static void Header(Stream target, int kind, string name) { target.WriteByte((byte) kind); Name(target, name); }
        private static void Name(Stream target, string name) {
            byte[] bytes = Encoding.UTF8.GetBytes(name);
            target.WriteByte((byte) (bytes.Length >> 8));
            target.WriteByte((byte) bytes.Length);
            target.Write(bytes, 0, bytes.Length);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class FakeInstance : IQuickPlayInstance {
        private readonly Fixture _fixture;
        internal bool ValidValue = true;
        internal int MajorValue = 20;
        internal bool SnapshotValue;
        internal DateTime Released = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        internal bool SavedValue = true;
        internal bool IsolatedValue = true;
        internal Func<bool>? Valid;
        internal Func<string>? Version;
        internal FakeInstance(Fixture fixture) => _fixture = fixture;
        private void Record(string name) => _fixture.Events.Add("instance." + name);
        public string VersionPath { get { Record("version"); return Version?.Invoke() ?? _fixture.VersionPath; } }
        public string GameFolder { get { Record("folder"); return _fixture.GameFolder; } }
        public bool VersionValid { get { Record("valid"); return Valid?.Invoke() ?? ValidValue; } }
        public int VanillaMajor { get { Record("major"); return MajorValue; } }
        public bool IsSnapshot { get { Record("snapshot"); return SnapshotValue; } }
        public DateTime ReleaseTime { get { Record("release"); return Released; } }
        public bool HasSavedIsolation { get { Record("saved"); return SavedValue; } }
        public bool IsolationEnabled { get { Record("isolated"); return IsolatedValue; } }
    }
}
