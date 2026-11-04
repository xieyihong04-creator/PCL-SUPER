using System.Diagnostics;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCL.Living.Features;

internal static class PersonalOptionsFlowTests {
    private const string ProfileName = "personal-options.json";
    private const string RecoveryWarning = "恢复个人游戏设置同步会话失败";

    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("personal option flow construction and null paths stay lazy", ConstructionAndNulls);
        yield return ("personal option flow disabled and running gates short circuit", DisabledAndRunning);
        yield return ("personal option flow import works while sync is disabled", ImportDisabled);
        yield return ("personal option flow missing first-run file is not created", MissingOptions);
        yield return ("personal option flow profile checks retain object and empty semantics", ProfileChecks);
        yield return ("personal option flow prepares actual BOM file and derived session identity", PrepareFile);
        yield return ("personal option flow preserves lazy version and factory access order", VersionOrder);
        yield return ("personal option flow decodes before later version reads", DecodeOrder);
        yield return ("personal option flow keeps live selection and invalid-version short circuits", SelectionAndInvalidVersion);
        yield return ("personal option flow session key snapshot retains raw profile aliases", RawAliasKeys);
        yield return ("personal option flow DTO inherits original public field shape", SessionFields);
        yield return ("personal option flow tracks current process only after durable journal", TrackProcess);
        yield return ("personal option flow failed track leaves Tracked false and process owned by caller", TrackFailure);
        yield return ("personal option flow distinguishes missing tracked and untracked journals", MissingJournal);
        yield return ("personal option flow capture writes only selected baseline changes", CaptureChanges);
        yield return ("personal option flow edited binding keeps profile extras and gate order", CaptureBinding);
        yield return ("personal option flow failed capture keeps journal for retry", CaptureRetry);
        yield return ("personal option flow normal capture early returns still remove journal", FinishEarlyReturns);
        yield return ("personal option flow live journal skips unrelated malformed fields", RecoverLive);
        yield return ("personal option flow warns for bad journal and continues valid recovery", RecoverBadAndValid);
        yield return ("personal option flow warning callback failure propagates with journal retained", WarnFailure);
        yield return ("personal option flow recovery assigns fields in original failure order", RecoveryAssignment);
        yield return ("personal option flow active-session path comparison short circuits pid reads", ActiveSession);
        yield return ("personal option flow active process prevents options preparation", PrepareActive);
        yield return ("personal option flow stale-file simulation rejects write before cache and session", PrepareStale);
        yield return ("personal option flow cache failure keeps committed file without creating session", PrepareCacheFailure);
        yield return ("personal option flow reenters original Monitor during callback and recovery", GateReentry);
    }

    private static void ConstructionAndNulls() {
        using Fixture fixture = new();
        Equal(0, fixture.Events.Count);
        Equal(false, fixture.Flow.Enabled(null));
        Equal<PersonalOptionsSessionData?>(null, fixture.Flow.Prepare(null));
        fixture.Flow.Track(null, null!);
        fixture.Flow.Finish(null);
        fixture.Flow.CaptureEditedBinding(null, "key_key.forward", "19");
        Throws<IOException>(() => fixture.Flow.ImportFrom(null), "请先选择一个已经运行过的游戏版本。");
        Equal(0, fixture.Events.Count);
        Equal(0, Directory.GetFileSystemEntries(fixture.Root).Length);
    }

    private static void DisabledAndRunning() {
        using Fixture fixture = new();
        fixture.Instance.EnabledValue = false;
        Equal<PersonalOptionsSessionData?>(null, fixture.Flow.Prepare(fixture.Instance));
        Equal("instance.enabled", string.Join("|", fixture.Events));
        fixture.Events.Clear();
        fixture.Instance.EnabledValue = true;
        fixture.Running = true;
        Equal<PersonalOptionsSessionData?>(null, fixture.Flow.Prepare(fixture.Instance));
        Equal("instance.enabled|running", string.Join("|", fixture.Events));
        fixture.Events.Clear();
        Throws<IOException>(() => fixture.Flow.ImportFrom(fixture.Instance), "请先关闭游戏，再导入个人设置。");
        Equal("running", string.Join("|", fixture.Events));
        Equal(false, Directory.Exists(fixture.ProfileRoot));
    }

    private static void ImportDisabled() {
        using Fixture fixture = new();
        fixture.Instance.EnabledValue = false;
        fixture.Options("mouseSensitivity:0.8\r\nfov:0.3\r\nkey_key.forward:19\r\nrenderDistance:32\r\nkey_mod.magic:key.keyboard.x\r\n");
        fixture.Select = key => {
            Equal(false, Monitor.IsEntered(fixture.Gate));
            return Fixture.Selected(key);
        };
        fixture.OnPath = name => { if (name == ProfileName) Equal(true, Monitor.IsEntered(fixture.Gate)); };
        fixture.Flow.ImportFrom(fixture.Instance);
        JObject profile = fixture.Profile();
        Equal(1, (int) profile["version"]!);
        Equal("82", (string?) profile["values"]!["fov"]);
        Equal("key.keyboard.r", (string?) profile["values"]!["key_key.forward"]);
        Equal<JToken?>(null, profile["values"]!["renderDistance"]);
        Equal<JToken?>(null, profile["values"]!["key_mod.magic"]);
        Equal(false, fixture.Events.Contains("instance.enabled"));
        Equal(false, fixture.Events.Any(item => item.StartsWith("instance.valid", StringComparison.Ordinal)));
    }

    private static void MissingOptions() {
        using Fixture fixture = new();
        Equal<PersonalOptionsSessionData?>(null, fixture.Flow.Prepare(fixture.Instance));
        Equal(false, Directory.Exists(fixture.GameFolder));
        Equal(false, Directory.Exists(fixture.ProfileRoot));
        Equal(false, fixture.Events.Contains("instance.valid"));
        Equal(false, fixture.Events.Contains("session"));
        fixture.Events.Clear();
        Throws<IOException>(() => fixture.Flow.ImportFrom(fixture.Instance), "该版本尚未生成 options.txt，请先启动一次游戏并保存设置。");
        Equal(false, Directory.Exists(fixture.GameFolder));
    }

    private static void ProfileChecks() {
        using Fixture fixture = new();
        fixture.OnPath = name => { if (name == ProfileName) Equal(true, Monitor.IsEntered(fixture.Gate)); };
        Equal(false, fixture.Flow.HasProfile());
        fixture.Raw(ProfileName, new JObject(new JProperty("values", new JArray("not an object"))));
        Equal(false, fixture.Flow.HasProfile());
        fixture.Profile(new JObject());
        Equal(false, fixture.Flow.HasProfile());
        fixture.Profile(JObject.Parse("{\"mouseSensitivity\":\"0.8\"}"));
        Equal(true, fixture.Flow.HasProfile());
        File.WriteAllText(fixture.DataPath(ProfileName), "{");
        Throws<JsonReaderException>(() => fixture.Flow.HasProfile());
    }

    private static void PrepareFile() {
        using Fixture fixture = new();
        byte[] original = fixture.Options("mouseSensitivity:0.2\r\nfov:0.0\r\nkey_key.forward:17\r\nrenderDistance:32\r\n", true);
        fixture.Profile(JObject.Parse("{\"mouseSensitivity\":\"0.8\",\"fov\":\"82\",\"key_key.forward\":\"key.keyboard.r\",\"renderDistance\":\"9\"}"));
        DerivedSession expected = new();
        fixture.CreateSession = () => expected;
        fixture.Invalidate = path => {
            Equal(true, Monitor.IsEntered(fixture.Gate));
            Equal(fixture.OptionsPath, path);
            Equal(true, fixture.Flow.HasProfile());
            Equal("mouseSensitivity:0.8\r\nfov:0.3\r\nkey_key.forward:key.keyboard.r\r\nrenderDistance:32\r\n", File.ReadAllText(path));
        };
        PersonalOptionsSessionData actual = fixture.Flow.Prepare(fixture.Instance)!;
        Equal(true, ReferenceEquals(expected, actual));
        Equal(fixture.VersionPath, actual.VersionPath);
        Equal(fixture.GameFolder, actual.GameFolder);
        Equal("82", (string?) actual.Baseline!["fov"]);
        Equal("key.keyboard.r", (string?) actual.Baseline["key_key.forward"]);
        Equal(false, actual.Keys.Contains("renderDistance"));
        Equal(StringComparer.Ordinal, actual.Keys.Comparer);
        Equal(false, actual.Tracked);
        Bytes(original, File.ReadAllBytes(fixture.OptionsPath + ".pcl-backup"));
        Bytes([239, 187, 191], File.ReadAllBytes(fixture.OptionsPath).Take(3).ToArray());
        NoStaging(fixture);
    }

    private static void VersionOrder() {
        using Fixture fixture = new();
        fixture.Options("key_key.forward:17\nfov:unknown\n");
        fixture.Profile(JObject.Parse("{\"key_key.forward\":\"key.keyboard.r\",\"fov\":\"90\"}"));
        Queue<bool> validity = new([false, true, true]);
        Queue<int> majors = new([12, 19]);
        fixture.Instance.Valid = () => validity.Dequeue();
        fixture.Instance.Major = () => majors.Dequeue();
        DerivedSession expected = new();
        fixture.CreateSession = () => expected;
        fixture.Instance.OnGet = property => {
            if (property == "version") fixture.Instance.FolderValue = Path.Combine(fixture.Root, "never-read-again");
        };
        PersonalOptionsSessionData actual = fixture.Flow.Prepare(fixture.Instance)!;
        Equal(true, ReferenceEquals(expected, actual));
        Equal(fixture.GameFolder, actual.GameFolder);
        Equal(false, actual.Keys.Contains("key_key.forward"));
        Equal("key_key.forward:17\nfov:90\n", File.ReadAllText(fixture.OptionsPath));
        Equal("instance.valid|instance.valid|instance.major|instance.valid|instance.major|session|instance.version", string.Join("|", fixture.Events.Where(item => item is "instance.valid" or "instance.major" or "session" or "instance.version")));
        Equal(0, validity.Count);
        Equal(0, majors.Count);
    }

    private static void DecodeOrder() {
        using Fixture fixture = new();
        fixture.OptionsBytes([255]);
        fixture.Profile(JObject.Parse("{\"fov\":\"90\"}"));
        Throws<DecoderFallbackException>(() => fixture.Flow.Prepare(fixture.Instance));
        Equal(1, fixture.Events.Count(item => item == "instance.valid"));
        Equal(false, fixture.Events.Contains("instance.major"));
        Equal(false, fixture.Events.Contains("session"));
        Equal(false, File.Exists(fixture.OptionsPath + ".pcl-backup"));
    }

    private static void SelectionAndInvalidVersion() {
        using Fixture fixture = new();
        fixture.Options("mouseSensitivity:0.2\nfov:unknown\nkey_key.forward:17\n");
        fixture.Profile(JObject.Parse("{\"mouseSensitivity\":\"0.8\",\"fov\":\"90\",\"key_key.forward\":\"key.keyboard.r\"}"));
        fixture.Instance.ValidValue = false;
        int sensitivityChecks = 0;
        fixture.Select = key => {
            Equal(true, Monitor.IsEntered(fixture.Gate));
            return key != "mouseSensitivity" || ++sensitivityChecks == 1;
        };
        PersonalOptionsSessionData session = fixture.Flow.Prepare(fixture.Instance)!;
        Equal(2, sensitivityChecks);
        Equal("mouseSensitivity:0.2\nfov:0.5\nkey_key.forward:17\n", File.ReadAllText(fixture.OptionsPath));
        Equal("0.2", (string?) session.Baseline!["mouseSensitivity"]);
        Equal(false, session.Keys.Contains("key_key.forward"));
        Equal(3, fixture.Events.Count(item => item == "instance.valid"));
        Equal(false, fixture.Events.Contains("instance.major"));
    }

    private static void RawAliasKeys() {
        using Fixture fixture = new();
        fixture.Options("key_key.swapHands:33\n");
        fixture.Profile(JObject.Parse("{\"key_key.swapHands\":\"key.keyboard.r\"}"));
        PersonalOptionsSessionData session = fixture.Flow.Prepare(fixture.Instance)!;
        Equal(true, session.Keys.SetEquals(["key_key.swapHands"]));
        Equal(0, session.Baseline!.Count);
        Equal("key_key.swapHands:33\n", File.ReadAllText(fixture.OptionsPath));
        Equal(false, File.Exists(fixture.OptionsPath + ".pcl-backup"));
    }

    private static void SessionFields() {
        DerivedSession session = new();
        foreach ((string name, Type type) in new[] { ("Id", typeof(string)), ("VersionPath", typeof(string)), ("GameFolder", typeof(string)), ("Baseline", typeof(JObject)), ("Keys", typeof(HashSet<string>)), ("Tracked", typeof(bool)) }) {
            FieldInfo field = typeof(DerivedSession).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            Equal(type, field.FieldType);
            Equal(typeof(PersonalOptionsSessionData), field.DeclaringType);
        }
        Equal(true, Guid.TryParseExact(session.Id, "N", out _));
        Equal<string?>(null, session.VersionPath);
        Equal<string?>(null, session.GameFolder);
        Equal<JObject?>(null, session.Baseline);
        Equal(0, session.Keys.Count);
        Equal(StringComparer.Ordinal, session.Keys.Comparer);
        Equal(false, session.Tracked);
    }

    private static void TrackProcess() {
        using Fixture fixture = new();
        PersonalOptionsSessionData session = fixture.Session();
        using Process process = Process.GetCurrentProcess();
        fixture.OnPath = name => {
            if (!name.StartsWith("options-sessions/", StringComparison.Ordinal)) return;
            Equal(true, Monitor.IsEntered(fixture.Gate));
            Equal(false, session.Tracked);
        };
        fixture.Flow.Track(session, process);
        Equal(true, session.Tracked);
        JObject journal = JObject.Parse(File.ReadAllText(fixture.JournalPath(session)));
        Equal("versionPath|gameFolder|pid|started|baseline|keys", string.Join("|", journal.Properties().Select(pair => pair.Name)));
        Equal(process.Id, (int) journal["pid"]!);
        Equal(process.StartTime.ToUniversalTime().Ticks, (long) journal["started"]!);
        Equal(fixture.VersionPath, (string?) journal["versionPath"]);
        Equal(false, process.HasExited);
        Equal(false, process.Handle == IntPtr.Zero);
        NoStaging(fixture);
    }

    private static void TrackFailure() {
        using Fixture fixture = new();
        PersonalOptionsSessionData session = fixture.Session();
        using Process process = Process.GetCurrentProcess();
        IOException sentinel = new("journal write blocked");
        fixture.OnPath = name => { if (name.StartsWith("options-sessions/", StringComparison.Ordinal)) throw sentinel; };
        Equal(sentinel, Throws<IOException>(() => fixture.Flow.Track(session, process)));
        Equal(false, session.Tracked);
        Equal(false, File.Exists(fixture.JournalPath(session)));
        Equal(false, process.HasExited);
        Equal(false, process.Handle == IntPtr.Zero);
        NoStaging(fixture);
    }

    private static void MissingJournal() {
        using Fixture fixture = new();
        fixture.Profile(JObject.Parse("{\"mouseSensitivity\":\"0.8\"}"));
        fixture.Options("mouseSensitivity:0.6\n");
        PersonalOptionsSessionData session = fixture.Session();
        session.Tracked = true;
        fixture.Flow.Finish(session);
        Equal("0.8", (string?) fixture.Profile()["values"]!["mouseSensitivity"]);
        Equal(false, fixture.Events.Any(item => item.StartsWith("factory:", StringComparison.Ordinal)));
        session.Tracked = false;
        fixture.Flow.Finish(session);
        Equal("0.6", (string?) fixture.Profile()["values"]!["mouseSensitivity"]);
        Equal(false, File.Exists(fixture.JournalPath(session)));
    }

    private static void CaptureChanges() {
        using Fixture fixture = new();
        fixture.Profile(JObject.Parse("{\"mouseSensitivity\":\"0.8\",\"showSubtitles\":\"newer profile\",\"fov\":\"70\",\"autoJump\":\"false\",\"renderDistance\":\"32\"}"));
        fixture.Options("mouseSensitivity:0.6\nshowSubtitles:false\nfov:90\nautoJump:true\nrenderDistance:9\n");
        PersonalOptionsSessionData session = fixture.Session();
        session.Baseline!["showSubtitles"] = "false";
        session.Keys.UnionWith(["showSubtitles", "fov", "autoJump", "missing"]);
        fixture.Select = key => { Equal(true, Monitor.IsEntered(fixture.Gate)); return key != "autoJump"; };
        fixture.Recreate = _ => { Equal(false, Monitor.IsEntered(fixture.Gate)); return fixture.Instance; };
        fixture.Flow.Capture(session);
        JObject values = (JObject) fixture.Profile()["values"]!;
        Equal("0.6", (string?) values["mouseSensitivity"]);
        Equal("newer profile", (string?) values["showSubtitles"]);
        Equal("70", (string?) values["fov"]);
        Equal("false", (string?) values["autoJump"]);
        Equal("32", (string?) values["renderDistance"]);
        Equal(false, fixture.Events.Contains("instance.folder"));
    }

    private static void CaptureBinding() {
        using Fixture fixture = new();
        fixture.Profile(JObject.Parse("{\"mouseSensitivity\":\"0.8\",\"key_key.forward\":\"key.keyboard.w\"}"));
        JObject profile = fixture.Profile();
        profile["unknown"] = "keep";
        fixture.Raw(ProfileName, profile);
        fixture.Running = true;
        fixture.Select = key => { Equal(false, Monitor.IsEntered(fixture.Gate)); return Fixture.Selected(key); };
        fixture.OnPath = name => { if (name == ProfileName) Equal(true, Monitor.IsEntered(fixture.Gate)); };
        fixture.Flow.CaptureEditedBinding(fixture.Instance, "key_key.swapHands", "19");
        JObject result = fixture.Profile();
        Equal("key.keyboard.r", (string?) result["values"]!["key_key.swapOffhand"]);
        Equal("key.keyboard.w", (string?) result["values"]!["key_key.forward"]);
        Equal("keep", (string?) result["unknown"]);
        Equal(false, fixture.Events.Contains("running"));
        byte[] before = File.ReadAllBytes(fixture.DataPath(ProfileName));
        fixture.Flow.CaptureEditedBinding(fixture.Instance, "key_key.forward", "invalid");
        Bytes(before, File.ReadAllBytes(fixture.DataPath(ProfileName)));
    }

    private static void CaptureRetry() {
        using Fixture fixture = new();
        fixture.Profile(JObject.Parse("{\"mouseSensitivity\":\"0.8\"}"));
        fixture.OptionsBytes([255]);
        PersonalOptionsSessionData session = fixture.Session();
        session.Tracked = true;
        fixture.RawJournal(session);
        Throws<DecoderFallbackException>(() => fixture.Flow.Finish(session));
        Equal(true, File.Exists(fixture.JournalPath(session)));
        Equal("0.8", (string?) fixture.Profile()["values"]!["mouseSensitivity"]);
        fixture.Options("mouseSensitivity:0.6\n");
        fixture.Flow.Finish(session);
        Equal(false, File.Exists(fixture.JournalPath(session)));
        Equal("0.6", (string?) fixture.Profile()["values"]!["mouseSensitivity"]);
    }

    private static void FinishEarlyReturns() {
        foreach (string reason in new[] { "disabled", "missing options", "missing profile values" }) {
            using Fixture fixture = new();
            PersonalOptionsSessionData session = fixture.Session();
            session.Tracked = true;
            fixture.RawJournal(session);
            if (reason == "disabled") fixture.Instance.EnabledValue = false;
            if (reason == "missing profile values") {
                fixture.Options("mouseSensitivity:0.6\n");
                fixture.Raw(ProfileName, new JObject(new JProperty("version", 1)));
            }
            fixture.Flow.Finish(session);
            Equal(false, File.Exists(fixture.JournalPath(session)));
            Equal(0, fixture.Warnings.Count);
        }
    }

    private static void RecoverLive() {
        using Fixture fixture = new();
        using Process process = Process.GetCurrentProcess();
        fixture.Raw("options-sessions/live.json", new JObject(new JProperty("pid", process.Id), new JProperty("started", process.StartTime.ToUniversalTime().Ticks), new JProperty("baseline", new JArray("wrong shape"))));
        fixture.Flow.RecoverExitedSessions();
        Equal(true, File.Exists(fixture.DataPath("options-sessions/live.json")));
        Equal(0, fixture.Warnings.Count);
        Equal(false, fixture.Events.Contains("session"));
        Equal(false, fixture.Events.Any(item => item.StartsWith("factory:", StringComparison.Ordinal)));
    }

    private static void RecoverBadAndValid() {
        using Fixture fixture = new();
        fixture.Profile(JObject.Parse("{\"mouseSensitivity\":\"0.8\"}"));
        fixture.Options("mouseSensitivity:0.6\n");
        PersonalOptionsSessionData session = fixture.Session();
        fixture.RawJournal(session);
        File.WriteAllText(fixture.DataPath("options-sessions/bad.json"), "{");
        fixture.OnWarn = (error, message) => {
            Equal(true, Monitor.IsEntered(fixture.Gate));
            Equal(RecoveryWarning, message);
            Equal(typeof(JsonReaderException), error.GetType());
        };
        fixture.Flow.RecoverExitedSessions();
        Equal(1, fixture.Warnings.Count);
        Equal(true, File.Exists(fixture.DataPath("options-sessions/bad.json")));
        Equal(false, File.Exists(fixture.JournalPath(session)));
        Equal("0.6", (string?) fixture.Profile()["values"]!["mouseSensitivity"]);
    }

    private static void WarnFailure() {
        using Fixture fixture = new();
        string file = fixture.DataPath("options-sessions/bad.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{");
        InvalidOperationException sentinel = new("warning sink failed");
        fixture.OnWarn = (_, _) => throw sentinel;
        Equal(sentinel, Throws<InvalidOperationException>(() => fixture.Flow.RecoverExitedSessions()));
        Equal(true, File.Exists(file));
        Equal(false, Monitor.IsEntered(fixture.Gate));
    }

    private static void RecoveryAssignment() {
        foreach (bool invalidBaseline in new[] { false, true }) {
            using Fixture fixture = new();
            DerivedSession recovered = new();
            fixture.CreateSession = () => recovered;
            PersonalOptionsSessionData source = fixture.Session();
            JObject journal = fixture.Journal(source);
            if (invalidBaseline) journal["baseline"] = new JArray("wrong type"); else journal.Remove("versionPath");
            fixture.Raw("options-sessions/original-id.json", journal);
            fixture.Flow.RecoverExitedSessions();
            Equal(1, fixture.Warnings.Count);
            Equal("original-id", recovered.Id);
            Equal(false, recovered.Tracked);
            Equal(0, recovered.Keys.Count);
            Equal<JObject?>(null, recovered.Baseline);
            Equal(invalidBaseline ? fixture.VersionPath : null, recovered.VersionPath);
            Equal(invalidBaseline ? fixture.GameFolder : null, recovered.GameFolder);
            Equal(false, fixture.Events.Any(item => item.StartsWith("factory:", StringComparison.Ordinal)));
            Equal(true, File.Exists(fixture.DataPath("options-sessions/original-id.json")));
        }
    }

    private static void ActiveSession() {
        using Fixture fixture = new();
        fixture.Raw("options-sessions/other.json", new JObject(new JProperty("gameFolder", "other folder")));
        fixture.SamePath = (first, second) => {
            Equal(true, Monitor.IsEntered(fixture.Gate));
            Equal("other folder", first);
            Equal(fixture.GameFolder, second);
            return false;
        };
        Equal(false, fixture.Flow.HasActiveSession(fixture.GameFolder));
        Equal(0, fixture.Warnings.Count);
        fixture.SamePath = (_, _) => true;
        Throws<NullReferenceException>(() => fixture.Flow.HasActiveSession(fixture.GameFolder));
        Equal(0, fixture.Warnings.Count);
    }

    private static void PrepareActive() {
        using Fixture fixture = new();
        using Process process = Process.GetCurrentProcess();
        fixture.Raw("options-sessions/live.json", new JObject(new JProperty("gameFolder", fixture.GameFolder), new JProperty("pid", process.Id), new JProperty("started", process.StartTime.ToUniversalTime().Ticks)));
        Equal<PersonalOptionsSessionData?>(null, fixture.Flow.Prepare(fixture.Instance));
        Equal(false, Directory.Exists(fixture.GameFolder));
        Equal(false, fixture.Events.Contains("instance.valid"));
        Equal(false, fixture.Events.Contains("session"));
        Equal(0, fixture.Warnings.Count);
    }

    private static void PrepareStale() {
        using Fixture fixture = new();
        fixture.Options("fov:70\n");
        fixture.Profile(JObject.Parse("{\"fov\":\"90\"}"));
        fixture.Select = _ => {
            File.WriteAllText(fixture.OptionsPath, "external edit");
            return true;
        };
        Throws<IOException>(() => fixture.Flow.Prepare(fixture.Instance), "游戏设置已被其他程序修改，本次同步已停止。");
        Equal("external edit", File.ReadAllText(fixture.OptionsPath));
        Equal(false, fixture.Events.Contains("cache"));
        Equal(false, fixture.Events.Contains("session"));
        Equal(false, File.Exists(fixture.OptionsPath + ".pcl-backup"));
        NoStaging(fixture);
    }

    private static void PrepareCacheFailure() {
        using Fixture fixture = new();
        byte[] original = fixture.Options("fov:70\n");
        fixture.Profile(JObject.Parse("{\"fov\":\"90\"}"));
        InvalidOperationException sentinel = new("cache failed");
        fixture.Invalidate = _ => throw sentinel;
        Equal(sentinel, Throws<InvalidOperationException>(() => fixture.Flow.Prepare(fixture.Instance)));
        Equal("fov:90\n", File.ReadAllText(fixture.OptionsPath));
        Bytes(original, File.ReadAllBytes(fixture.OptionsPath + ".pcl-backup"));
        Equal(false, fixture.Events.Contains("session"));
        NoStaging(fixture);
    }

    private static void GateReentry() {
        using Fixture fixture = new();
        fixture.Profile(JObject.Parse("{\"mouseSensitivity\":\"0.8\"}"));
        fixture.Options("mouseSensitivity:0.6\n");
        PersonalOptionsSessionData session = fixture.Session();
        fixture.RawJournal(session);
        fixture.Recreate = _ => {
            Equal(true, Monitor.IsEntered(fixture.Gate));
            Equal(true, fixture.Flow.HasProfile());
            return fixture.Instance;
        };
        fixture.OnPath = name => {
            if (name == "options-sessions") return;
            Equal(true, Monitor.IsEntered(fixture.Gate));
        };
        fixture.Flow.RecoverExitedSessions();
        Equal(false, File.Exists(fixture.JournalPath(session)));
        Equal("0.6", (string?) fixture.Profile()["values"]!["mouseSensitivity"]);
        Equal(0, fixture.Warnings.Count);
        Equal(false, Monitor.IsEntered(fixture.Gate));
    }

    private static void NoStaging(Fixture fixture) => Equal(0, Directory.GetFiles(fixture.Root, "*.pcl-write-*", SearchOption.AllDirectories).Length);
    private static void Bytes(byte[] expected, byte[] actual) {
        if (!expected.SequenceEqual(actual)) throw new InvalidOperationException("File bytes differ.");
    }
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

    private sealed class DerivedSession : PersonalOptionsSessionData { }

    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PclPersonalOptionsFlow-" + Guid.NewGuid().ToString("N"));
        internal string ProfileRoot => Path.Combine(Root, "profile");
        internal string VersionPath => Path.Combine(Root, "version");
        internal string GameFolder => Path.Combine(Root, "game");
        internal string OptionsPath => Path.Combine(GameFolder, "options.txt");
        internal object Gate { get; } = new();
        internal List<string> Events { get; } = [];
        internal List<Exception> Warnings { get; } = [];
        internal bool Running;
        internal Func<string, bool>? Select;
        internal Func<string, IPersonalOptionsInstance>? Recreate;
        internal Func<PersonalOptionsSessionData>? CreateSession;
        internal Action<string>? OnPath;
        internal Action<string>? Invalidate;
        internal Action<Exception, string>? OnWarn;
        internal FakeInstance Instance { get; }
        internal LocalGameDataStore Data { get; }
        internal PersonalOptionsFlow Flow { get; }

        internal Fixture() {
            Directory.CreateDirectory(Root);
            Instance = new FakeInstance(this);
            Data = new LocalGameDataStore(() => { Events.Add("root"); return ProfileRoot; }, (_, file) => {
                string name = Path.GetRelativePath(ProfileRoot, file).Replace('\\', '/');
                Events.Add("path:" + name);
                OnPath?.Invoke(name);
            });
            Flow = new PersonalOptionsFlow(Data, Gate,
                () => { Events.Add("running"); return Running; },
                path => { Events.Add("factory:" + path); return Recreate?.Invoke(path) ?? Instance; },
                key => { Events.Add("select:" + key); return Select?.Invoke(key) ?? Selected(key); },
                file => { Events.Add("cache"); Invalidate?.Invoke(file); },
                (first, second) => { Events.Add("samepath"); return SamePath?.Invoke(first, second) ?? string.Equals(first, second, StringComparison.OrdinalIgnoreCase); },
                (error, message) => { Events.Add("warn"); Warnings.Add(error); OnWarn?.Invoke(error, message); },
                () => { Events.Add("session"); return CreateSession?.Invoke() ?? new DerivedSession(); });
        }

        internal Func<string, string, bool>? SamePath;
        internal static bool Selected(string key) => PersonalOptionsRules.VanillaBinding(key) || PersonalOptionsRules.IsMouseKey(key) || PersonalOptionsRules.IsViewKey(key) || PersonalOptionsRules.IsAccessibilityKey(key);
        internal string DataPath(string name) => Path.Combine(ProfileRoot, name);
        internal string JournalPath(PersonalOptionsSessionData session) => DataPath("options-sessions/" + session.Id + ".json");
        internal void Raw(string name, JObject value) {
            string file = DataPath(name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, value.ToString(), new UTF8Encoding(false));
        }
        internal void Profile(JObject values) => Raw(ProfileName, new JObject(new JProperty("version", 1), new JProperty("values", values)));
        internal JObject Profile() => JObject.Parse(File.ReadAllText(DataPath(ProfileName)));
        internal byte[] Options(string text, bool bom = false) {
            byte[] bytes = [.. bom ? new byte[] { 239, 187, 191 } : Array.Empty<byte>(), .. Encoding.UTF8.GetBytes(text)];
            OptionsBytes(bytes);
            return bytes;
        }
        internal void OptionsBytes(byte[] bytes) {
            Directory.CreateDirectory(GameFolder);
            File.WriteAllBytes(OptionsPath, bytes);
        }
        internal PersonalOptionsSessionData Session() => new DerivedSession {
            VersionPath = VersionPath, GameFolder = GameFolder,
            Baseline = JObject.Parse("{\"mouseSensitivity\":\"0.8\"}"), Keys = new HashSet<string>(["mouseSensitivity"], StringComparer.Ordinal)
        };
        internal JObject Journal(PersonalOptionsSessionData session) => new(
            new JProperty("versionPath", session.VersionPath), new JProperty("gameFolder", session.GameFolder),
            new JProperty("pid", int.MaxValue), new JProperty("started", 1L), new JProperty("baseline", session.Baseline), new JProperty("keys", new JArray(session.Keys)));
        internal void RawJournal(PersonalOptionsSessionData session) => Raw("options-sessions/" + session.Id + ".json", Journal(session));
        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class FakeInstance : IPersonalOptionsInstance {
        private readonly Fixture _fixture;
        internal bool EnabledValue = true;
        internal bool ValidValue = true;
        internal string FolderValue;
        internal Func<bool>? Valid;
        internal Func<int>? Major;
        internal Action<string>? OnGet;
        internal FakeInstance(Fixture fixture) { _fixture = fixture; FolderValue = fixture.GameFolder; }
        private void Record(string name) { _fixture.Events.Add("instance." + name); OnGet?.Invoke(name); }
        public string VersionPath { get { Record("version"); return _fixture.VersionPath; } }
        public string GameFolder { get { Record("folder"); return FolderValue; } }
        public bool SyncEnabled { get { Record("enabled"); return EnabledValue; } }
        public bool VersionValid { get { Record("valid"); return Valid?.Invoke() ?? ValidValue; } }
        public int VanillaMajor { get { Record("major"); return Major?.Invoke() ?? 20; } }
    }
}
