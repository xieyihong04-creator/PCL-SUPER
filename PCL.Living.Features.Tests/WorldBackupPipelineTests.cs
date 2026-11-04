using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using PCL.Living.Features;

internal static class WorldBackupPipelineTests {
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("world backup pipeline captures verified files and empty folders then restores with a real undo point", CaptureRestoreRoundTrip);
        yield return ("world backup pipeline retention protects manual points and other worlds", PerWorldRetention);
        yield return ("world backup pipeline cancellation and bad destinations release busy state and owned staging", CaptureFailureCleanup);
        yield return ("world backup pipeline commit faults cancellation and persisted journal recovery preserve current world", RestoreFailureAndRecovery);
        yield return ("world backup workflow keeps confirmation output stopped and rejected-worker boundaries", WorkflowLifecycle);
        if (OperatingSystem.IsWindows())
            yield return ("world backup Windows session lock prevents capture and restore until the holder releases it", WindowsSessionLock);
    }

    private static void CaptureRestoreRoundTrip() {
        using Fixture fixture = new();
        Context context = new(fixture);
        string world = context.World("World A", [1, 2, 3]);
        fixture.Bytes("game/saves/World A/region/data.bin", [4, 5]);
        fixture.Directory("game/saves/World A/empty/nested");
        fixture.Text("game/saves/World A/session.lock", "runtime lock");
        string id = context.Engine.CaptureWorld(context.Owner.GameFolder, "World A", "manual", 5, CancellationToken.None);
        Equal(true, Regex.IsMatch(id, @"\A[0-9a-f]{32}\z"));
        JObject point = context.Engine.Points(context.Owner.GameFolder).Single();
        Equal(id, (string?)point["id"]);
        Equal("World A", (string?)point["label"]);
        Equal(true, context.Engine.IsManualPoint(point));
        Equal(2, ((JObject)point["files"]!).Count);
        Equal<JToken?>(null, point["files"]!["session.lock"]);
        Equal(true, ((JArray)point["directories"]!).Values<string>().Contains("empty/nested"));
        string archive = Path.Combine(context.Engine.Store(context.Owner.GameFolder), "points", id, "world.zip");
        using (ZipArchive zip = ZipFile.OpenRead(archive)) {
            Equal(2, zip.Entries.Count);
            using Stream level = zip.GetEntry("level.dat")!.Open();
            Sequence(new byte[] { 1, 2, 3 }, ReadBytes(level));
        }
        File.WriteAllBytes(Path.Combine(world, "level.dat"), [9, 8]);
        fixture.Bytes("game/saves/World A/newer.bin", [7]);
        Directory.Delete(Path.Combine(world, "empty"), true);
        string? undo = context.Engine.Restore(context.Owner.GameFolder, id, 5, CancellationToken.None);
        Equal(true, undo is not null && Regex.IsMatch(undo, @"\A[0-9a-f]{32}\z"));
        Sequence(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(world, "level.dat")));
        Equal(false, File.Exists(Path.Combine(world, "newer.bin")));
        Equal(true, Directory.Exists(Path.Combine(world, "empty", "nested")));
        Equal(false, File.Exists(Path.Combine(world, "session.lock")));
        Equal("恢复前自动保存", (string?)context.Engine.Metadata(context.Owner.GameFolder, undo!)["reason"]);
        context.Engine.Restore(context.Owner.GameFolder, undo!, 5, CancellationToken.None);
        Sequence(new byte[] { 9, 8 }, File.ReadAllBytes(Path.Combine(world, "level.dat")));
        Sequence(new byte[] { 7 }, File.ReadAllBytes(Path.Combine(world, "newer.bin")));
        Equal(false, Directory.Exists(Path.Combine(world, "empty")));
        AssertClean(context);
    }

    private static void PerWorldRetention() {
        using Fixture fixture = new();
        Context context = new(fixture);
        string world = context.World("World A", [1]);
        context.World("World B", [2]);
        string legacyManual = context.Engine.CaptureWorld(context.Owner.GameFolder, "World A", "manual", 1, CancellationToken.None);
        string manual = context.Engine.CaptureWorld(context.Owner.GameFolder, "World A", "手动备份", 1, CancellationToken.None);
        string other = context.Engine.CaptureWorld(context.Owner.GameFolder, "World B", "automatic", 1, CancellationToken.None);
        for (byte index = 3; index < 6; index++) {
            File.WriteAllBytes(Path.Combine(world, "level.dat"), [index]);
            context.Engine.CaptureWorld(context.Owner.GameFolder, "World A", "automatic", 1, CancellationToken.None);
        }
        List<JObject> points = context.Engine.Points(context.Owner.GameFolder);
        List<JObject> firstWorld = points.Where(point => (string?)point["world"] == "World A").ToList();
        Equal(3, firstWorld.Count);
        Equal(1, firstWorld.Count(point => !context.Engine.IsManualPoint(point)));
        foreach (string id in new[] { legacyManual, manual, other })
            Equal(true, points.Any(point => (string?)point["id"] == id));
        Equal(1, points.Count(point => (string?)point["world"] == "World B"));
        AssertClean(context);
    }

    private static void CaptureFailureCleanup() {
        using Fixture fixture = new();
        Context context = new(fixture);
        context.World("World A", [1]);
        using CancellationTokenSource source = new();
        bool observedStage = false;
        context.Host.WorldLabel = _ => {
            observedStage = Directory.GetDirectories(context.Engine.Store(context.Owner.GameFolder), ".pcl-backup-*").Length == 1;
            source.Cancel();
            return null;
        };
        Throws<OperationCanceledException>(() => context.Engine.CaptureWorld(context.Owner.GameFolder, "World A", "canceled", 5, source.Token));
        Equal(true, observedStage);
        Equal(0, context.Engine.Points(context.Owner.GameFolder).Count);
        AssertClean(context);
        context.Host.WorldLabel = _ => null;
        string blockedRoot = fixture.Bytes("not-a-directory.bin", [9]);
        context.Engine.TestRoot = blockedRoot;
        try { Throws<IOException>(() => context.Engine.CaptureWorld(context.Owner.GameFolder, "World A", "bad destination", 5, CancellationToken.None)); }
        finally { context.Engine.TestRoot = null; }
        Equal(0, context.Engine.BusyCount);
        Sequence(new byte[] { 9 }, File.ReadAllBytes(blockedRoot));
        string id = context.Engine.CaptureWorld(context.Owner.GameFolder, "World A", "manual", 5, CancellationToken.None);
        Equal(id, (string?)context.Engine.Points(context.Owner.GameFolder).Single()["id"]);
        AssertClean(context);
    }

    private static void RestoreFailureAndRecovery() {
        for (int phase = 0; phase <= 2; phase++) {
            using Fixture fixture = new();
            Context context = new(fixture);
            string world = context.World("World A", [1]);
            string id = context.Engine.CaptureWorld(context.Owner.GameFolder, "World A", "manual", 50, CancellationToken.None);
            File.WriteAllBytes(Path.Combine(world, "level.dat"), [8, 9]);
            fixture.Bytes("game/saves/World A/newer.bin", [7]);
            IOException fault = new("Injected restore commit fault " + phase);
            List<int> reached = new();
            Exception actual = Throws<IOException>(() => context.Engine.Restore(context.Owner.GameFolder, id, 50, CancellationToken.None, index => {
                reached.Add(index);
                if (index == phase) throw fault;
            }));
            Equal(true, ReferenceEquals(fault, actual));
            Sequence(Enumerable.Range(0, phase + 1), reached);
            Sequence(new byte[] { 8, 9 }, File.ReadAllBytes(Path.Combine(world, "level.dat")));
            Sequence(new byte[] { 7 }, File.ReadAllBytes(Path.Combine(world, "newer.bin")));
            AssertClean(context);
        }
        using (Fixture fixture = new()) {
            Context context = new(fixture);
            string world = context.World("World A", [1]);
            string id = context.Engine.CaptureWorld(context.Owner.GameFolder, "World A", "manual", 50, CancellationToken.None);
            File.WriteAllBytes(Path.Combine(world, "level.dat"), [5]);
            using CancellationTokenSource source = new();
            Throws<OperationCanceledException>(() => context.Engine.Restore(context.Owner.GameFolder, id, 50, source.Token, phase => { if (phase == 1) source.Cancel(); }));
            Sequence(new byte[] { 5 }, File.ReadAllBytes(Path.Combine(world, "level.dat")));
            AssertClean(context);
        }
        RecoverActualJournal();
    }

    private static void RecoverActualJournal() {
        using Fixture fixture = new();
        Context context = new(fixture);
        string world = context.World("World A", [1]);
        string id = context.Engine.CaptureWorld(context.Owner.GameFolder, "World A", "manual", 50, CancellationToken.None);
        File.WriteAllBytes(Path.Combine(world, "level.dat"), [6]);
        string? journal = null, journalFile = null;
        IOException fault = new("Record the actual in-progress restore journal.");
        Throws<IOException>(() => context.Engine.Restore(context.Owner.GameFolder, id, 50, CancellationToken.None, phase => {
            if (phase != 1) return;
            journalFile = Directory.GetFiles(Path.Combine(context.Engine.Store(context.Owner.GameFolder), "transactions"), "*.json").Single();
            journal = File.ReadAllText(journalFile);
            throw fault;
        }));
        AssertClean(context);
        string transaction = Path.GetFileNameWithoutExtension(journalFile!);
        string previous = Path.Combine(context.Owner.GameFolder, "saves", ".pcl-world-" + transaction + ".before");
        string stage = Path.Combine(context.Owner.GameFolder, "saves", ".pcl-world-" + transaction + ".stage");
        // Recreate the interrupted swap input using the engine's own archive
        // extraction and actual journal; no backup or recovery algorithm is mocked.
        Directory.Move(world, previous);
        Directory.CreateDirectory(stage);
        context.Engine.ReadArchive(Path.Combine(context.Engine.Store(context.Owner.GameFolder), "points", id), context.Engine.Metadata(context.Owner.GameFolder, id), CancellationToken.None, stage);
        File.WriteAllText(journalFile!, journal!, Utf8);
        context.Engine.RecoverPending(context.Owner.GameFolder, CancellationToken.None);
        Sequence(new byte[] { 6 }, File.ReadAllBytes(Path.Combine(world, "level.dat")));
        AssertClean(context);
    }

    private static void WorkflowLifecycle() {
        using Fixture fixture = new();
        Context context = new(fixture);
        context.World("World A", [1]);
        context.Workflow.Start(context.Owner, worldIdentifier: "World A");
        WorldBackupWork capture = context.Started.Single();
        Equal("备份世界", capture.StepName);
        Equal(true, context.Workflow.IsBusy);
        Equal(true, context.Engine.IsBusy);
        context.Workflow.Start(context.Owner);
        Equal(1, context.Started.Count);
        string? output = null;
        capture.Execute(CancellationToken.None, value => output = value);
        Equal("1", output);
        Equal(true, context.Workflow.IsBusy);
        capture.Stopped();
        Equal(false, context.Workflow.IsBusy);
        Equal(false, context.NotifiedBusy.Last());
        string id = context.Engine.Points(context.Owner.GameFolder).Single()["id"]!.ToString();
        context.ConfirmResult = 0;
        context.Workflow.Start(context.Owner, restoreId: id);
        WorldBackupWork restore = context.Started.Last();
        try { Throws<OperationCanceledException>(() => restore.Execute(CancellationToken.None, _ => throw new Exception("A declined restore produced output."))); }
        finally { restore.Stopped(); }
        Equal(true, context.ConfirmText!.Contains("共用目录", StringComparison.Ordinal));
        Sequence(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(context.Owner.GameFolder, "saves", "World A", "level.dat")));
        context.StartAllowed = false;
        context.Workflow.Start(context.Owner, worldIdentifier: "World A");
        Equal(false, context.Workflow.IsBusy);
        AssertClean(context);
        using Fixture emptyFixture = new();
        Context empty = new(emptyFixture);
        empty.Workflow.Start(empty.Owner);
        WorldBackupWork emptyWork = empty.Started.Single();
        emptyWork.Execute(CancellationToken.None, value => empty.Events.Add("output:" + value));
        int outputIndex = empty.Events.FindIndex(value => value == "output:0");
        int hintIndex = empty.Events.FindIndex(value => value.StartsWith("hint:", StringComparison.Ordinal));
        Equal(true, outputIndex >= 0 && hintIndex > outputIndex);
        Equal(true, empty.Events.Any(value => value.StartsWith("hint:此游戏目录没有可备份的存档。", StringComparison.Ordinal)));
        emptyWork.Stopped();
        Equal(false, empty.Workflow.IsBusy);
        AssertClean(empty);
    }

    private static void WindowsSessionLock() {
        using Fixture fixture = new();
        Context context = new(fixture);
        string world = context.World("World A", [1]);
        fixture.Text("game/saves/World A/session.lock", "runtime");
        string id = context.Engine.CaptureWorld(context.Owner.GameFolder, "World A", "manual", 5, CancellationToken.None);
        using (FileStream held = new(Path.Combine(world, "session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            Throws<IOException>(() => context.Engine.CaptureWorld(context.Owner.GameFolder, "World A", "locked", 5, CancellationToken.None));
            Throws<IOException>(() => context.Engine.Restore(context.Owner.GameFolder, id, 5, CancellationToken.None));
            Equal(0, context.Engine.BusyCount);
        }
        context.Engine.CaptureWorld(context.Owner.GameFolder, "World A", "after release", 5, CancellationToken.None);
        using (new FileStream(Path.Combine(world, "session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        AssertClean(context);
    }

    private static void AssertClean(Context context) {
        Equal(0, context.Engine.BusyCount);
        Equal(false, context.Engine.IsBusy);
        string saves = Path.Combine(context.Owner.GameFolder, "saves");
        if (Directory.Exists(saves)) Equal(0, Directory.GetDirectories(saves, ".pcl-world-*").Length);
        string store = context.Engine.Store(context.Owner.GameFolder);
        if (Directory.Exists(store)) Equal(0, Directory.GetDirectories(store, ".pcl-backup-*").Length);
        string transactions = Path.Combine(store, "transactions");
        if (Directory.Exists(transactions)) Equal(0, Directory.GetFiles(transactions, "*.json").Length);
    }

    private static byte[] ReadBytes(Stream input) { using MemoryStream output = new(); input.CopyTo(output); return output.ToArray(); }
    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual) { if (!expected.SequenceEqual(actual)) throw new Exception("Sequences differ."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}]."); }
    private static T Throws<T>(Action run) where T : Exception { try { run(); } catch (Exception error) { if (error is T match) return match; throw; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class Owner { internal string GameFolder = null!, VersionPath = null!, Name = "Fixture"; internal int Retention = 5; internal bool AutoEnabled = true; }

    private sealed class Context {
        internal readonly Owner Owner;
        internal readonly WorldBackupHost<Owner> Host;
        internal readonly WorldBackupEngine<Owner> Engine;
        internal readonly WorldBackupWorkflow<Owner> Workflow;
        internal readonly List<WorldBackupWork> Started = new();
        internal readonly List<bool> NotifiedBusy = new();
        internal readonly List<string> Events = new();
        internal bool StartAllowed = true;
        internal int ConfirmResult = 1;
        internal string? ConfirmText;
        private readonly Fixture fixture;

        internal Context(Fixture fixture) {
            this.fixture = fixture;
            Owner = new Owner { GameFolder = fixture.Directory("game"), VersionPath = fixture.Directory("game/versions/fixture") };
            string profile = fixture.Directory("profile");
            // Use the production profile address store. The supplied boundary
            // guard and owner/UI services are fixture hosts, not copies of the
            // backup/hash/archive/retention/recovery implementation.
            LocalGameDataStore addresses = new(() => profile, (root, path) => {
                string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Profile fixture escaped its root.");
            });
            Host = new WorldBackupHost<Owner> {
                RemoveExtendedPrefix = text => text, ProfileAddress = addresses.Address,
                GameFolder = owner => owner.GameFolder, VersionPath = owner => owner.VersionPath,
                Retention = owner => owner.Retention, AutoEnabled = owner => owner.AutoEnabled,
                Watchers = () => Array.Empty<WorldBackupWatcher<Owner>?>(), WorldLabel = _ => null,
                FormatSize = bytes => bytes + " bytes", Warn = (_, _) => { }, UiBusy = () => Workflow?.IsBusy ?? false,
                Dispatch = action => { Events.Add("dispatch"); action(); }, NotifyChanged = () => NotifiedBusy.Add(Workflow?.IsBusy ?? false),
                OpenFolder = _ => { }, Hint = (text, _) => Events.Add("hint:" + text)
            };
            Engine = new WorldBackupEngine<Owner>(Host);
            Workflow = new WorldBackupWorkflow<Owner>(Engine, new WorldBackupWorkflowHost<Owner> {
                GameFolder = owner => owner.GameFolder, VersionPath = owner => owner.VersionPath, InstanceName = owner => owner.Name,
                Retention = owner => owner.Retention, LauncherBusy = () => false,
                SamePath = (first, second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase),
                Confirm = (text, _, _) => { ConfirmText = text; return ConfirmResult; }, Hint = (text, _) => Events.Add("hint:" + text),
                Dispatch = action => { Events.Add("dispatch"); action(); }, StartWork = (_, work) => { Started.Add(work); return StartAllowed; }
            });
        }
        internal string World(string name, byte[] level) { string path = fixture.Bytes("game/saves/" + name + "/level.dat", level); return Path.GetDirectoryName(path)!; }
    }

    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PCLLF-WorldBackup-" + Guid.NewGuid().ToString("N"));
        internal Fixture() => System.IO.Directory.CreateDirectory(Root);
        internal string Directory(string relative) { string path = Owned(relative); System.IO.Directory.CreateDirectory(path); return path; }
        internal string Bytes(string relative, byte[] bytes) { string path = Owned(relative); System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); return path; }
        internal void Text(string relative, string text) => Bytes(relative, Utf8.GetBytes(text));
        private string Owned(string relative) { string path = Path.GetFullPath(Path.Combine(Root, relative)); if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new Exception("Unsafe backup fixture child."); return path; }
        public void Dispose() {
            string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Root.StartsWith(temporary, StringComparison.Ordinal) || !Regex.IsMatch(Path.GetFileName(Root), @"\APCLLF-WorldBackup-[0-9a-f]{32}\z")) throw new Exception("Unsafe backup fixture cleanup.");
            foreach (string path in System.IO.Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            System.IO.Directory.Delete(Root, true);
        }
    }
}
