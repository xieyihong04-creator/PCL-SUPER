using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using PCL.Living.Features;

internal static class EnvironmentHistoryPipelineTests {
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("environment history pipeline shares real payloads and compares only healthy private-value-free changes", CaptureAndHealthyChanges);
        yield return ("environment history pipeline opt-in retention protects legacy manual and recoverable deleted references", RetentionReferences);
        yield return ("environment history pipeline restore rejects isolation cancellation and corrupt payloads before its retained transaction host", RestoreGuards);
        yield return ("environment history pipeline plans legacy and current coverage without simulating the retained VB transaction", CoveragePlans);
        yield return ("environment history pipeline canceled capture and unstable or canceled comparisons release owned temporary files", ScanAndCancellation);
    }

    private static void CaptureAndHealthyChanges() {
        using Fixture fixture = new();
        Context context = new(fixture);
        context.Put("mods/example.jar", "mod-payload");
        context.Put("config/private.toml", "api_key=private-before");
        context.Put("config/accounts.json", "private-account-before");
        context.Put("config/xaero/waypoints.txt", "private-waypoints-before");
        context.Put("saves/World/level.dat", "world-before");
        context.Put("screenshots/picture.png", "screenshot-before");
        Equal(false, context.Changes.BuildInstance(context.Owner, CancellationToken.None).HasBaseline);
        string healthy = context.Capture("healthy fixture");
        Equal(false, (bool)context.Point(healthy)["healthy"]!);
        int objects = context.ObjectFiles().Length;
        context.Engine.MarkHealthy(context.Owner.VersionPath, healthy, false);
        Equal(false, context.Changes.Build(context.Owner.VersionPath, context.Owner.GameFolder, CancellationToken.None).HasBaseline);
        context.Engine.MarkHealthy(context.Owner.VersionPath, healthy, true);
        string newer = context.Capture("newer untested fixture");
        Equal(objects, context.ObjectFiles().Length);
        Report unchanged = context.Changes.BuildInstance(context.Owner, CancellationToken.None);
        Equal(healthy, unchanged.BaselineId);
        Equal(0, unchanged.Changes.Count);
        File.Move(context.GamePath("mods/example.jar"), context.GamePath("mods/example.jar.disabled"));
        context.Put("config/private.toml", "api_key=private-after");
        context.Put("config/accounts.json", "private-account-after");
        context.Put("config/xaero/waypoints.txt", "private-waypoints-after");
        context.Put("saves/World/level.dat", "world-after");
        context.Put("screenshots/picture.png", "screenshot-after");
        context.Put("example.json", "{\"id\":\"example\",\"type\":\"snapshot\"}");
        Report report = context.Changes.BuildInstance(context.Owner, CancellationToken.None);
        Equal(true, report.HasBaseline);
        Equal(healthy, report.BaselineId);
        Equal(false, newer == report.BaselineId);
        Equal(true, report.BaselineCreatedUtc.HasValue);
        Equal(3, report.Changes.Count);
        Change disabled = report.Changes.Single(change => change.Relative == "g/mods/example.jar.disabled");
        Equal("g/mods/example.jar", disabled.PreviousRelative);
        Equal(true, disabled.Kind!.Contains("禁用", StringComparison.Ordinal));
        Equal(true, report.Changes.All(change => change.Marker == "derived change"));
        Equal("derived report", report.Marker);
        string text = report.ToAnalysisText();
        foreach (string secret in new[] { fixture.Root, "private-", "api_key", "accounts.json", "waypoints", "world-after", "screenshot-after" })
            Equal(false, text.Contains(secret, StringComparison.Ordinal));
        Equal(true, text.Contains("不能据此确定崩溃原因", StringComparison.Ordinal));
        Equal(true, report.ToAnalysisText(1).Contains("另有 2 项变更", StringComparison.Ordinal));
        string appended = context.Changes.AppendAnalysis("existing crash reason", report) ?? throw new Exception("A report must produce an analysis appendix.");
        Equal(true, appended.StartsWith("existing crash reason", StringComparison.Ordinal));
        Equal(true, appended.Contains("上次正常运行后改了什么", StringComparison.Ordinal));
        AssertClean(context);
    }

    private static void RetentionReferences() {
        using Fixture fixture = new();
        Context context = new(fixture);
        context.Put("config/pack.toml", "manual payload");
        string manual = context.Capture("手动还原点");
        JObject legacy = context.Point(manual);
        legacy.Remove("manual");
        context.WritePoint(manual, legacy);
        context.Put("config/pack.toml", "recoverable deleted payload");
        string deleted = context.Capture("整合包更新前");
        string deletedBlob = context.Blob(deleted, "g/config/pack.toml");
        string deletedFolder = context.Engine.DeletePoints(context.Owner.VersionPath, [deleted]) ?? throw new Exception("Expected a recoverable deletion directory.");
        context.Put("config/pack.toml", "unreferenced expired payload");
        string expired = context.Capture("游戏启动前");
        string expiredBlob = context.Blob(expired, "g/config/pack.toml");
        JObject oldAutomatic = context.Point(expired);
        oldAutomatic.Remove("manual");
        context.WritePoint(expired, oldAutomatic);
        context.Put("config/pack.toml", "newest payload");
        string newest = context.Capture("更新模组前");
        context.Owner.Retention = 1;
        context.Engine.ApplyRetention(context.Owner, newest, CancellationToken.None);
        Equal(3, context.Points().Count);
        context.Owner.AutoCleanup = true;
        context.Engine.ApplyRetention(context.Owner, newest, CancellationToken.None);
        Sequence(new[] { manual, newest }.Order(), context.Points().Select(point => (string)point["id"]!).Order());
        Equal(true, context.Engine.IsManualPoint(context.Point(manual)));
        Equal(false, File.Exists(expiredBlob));
        Equal(true, File.Exists(deletedBlob));
        Equal(true, File.Exists(Path.Combine(deletedFolder, deleted + ".json")));
        foreach (JObject point in context.Points())
            foreach (JProperty entry in ((JObject)point["files"]!).Properties())
                Equal(true, File.Exists(Path.Combine(context.Store, "objects", entry.Value.ToString()[5..])));
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        string[] files = Directory.GetFiles(context.Store, "*", SearchOption.AllDirectories).Order().ToArray();
        Throws<OperationCanceledException>(() => context.Engine.PruneAutomatic(context.Owner.VersionPath, 1, newest, canceled.Token));
        Throws<IOException>(() => context.Engine.PruneAutomatic(context.Owner.VersionPath, 0, newest, CancellationToken.None));
        legacy["files"]!["g/config/pack.toml"] = "sha1:invalid";
        context.WritePoint(manual, legacy);
        Throws<IOException>(() => context.Engine.PruneAutomatic(context.Owner.VersionPath, 1, newest, CancellationToken.None));
        Sequence(files, Directory.GetFiles(context.Store, "*", SearchOption.AllDirectories).Order());
        Equal("newest payload", File.ReadAllText(context.GamePath("config/pack.toml")));
        AssertClean(context);
    }

    private static void RestoreGuards() {
        using Fixture fixture = new();
        Context context = new(fixture);
        context.Put("config/pack.toml", "recorded payload");
        string id = context.Capture("manual fixture");
        context.Put("config/pack.toml", "current payload");
        int safetyCaptures = 0;
        string CaptureBefore() { safetyCaptures++; throw new Exception("A rejected restore must not capture or commit."); }
        string other = fixture.Directory("other-game");
        Throws<IOException>(() => context.Engine.Restore(context.Owner.VersionPath, other, context.MinecraftRoot, id, CancellationToken.None, captureBefore: CaptureBefore));
        Equal(0, context.Engine.RestoringCount);
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        Throws<OperationCanceledException>(() => context.Engine.Restore(context.Owner.VersionPath, context.Owner.GameFolder, context.MinecraftRoot, id, canceled.Token, captureBefore: CaptureBefore));
        Equal(0, context.Engine.RestoringCount);
        File.WriteAllText(context.Blob(id, "g/config/pack.toml"), "corrupt payload", Utf8);
        IOException corruption = Throws<IOException>(() => context.Engine.Restore(context.Owner.VersionPath, context.Owner.GameFolder, context.MinecraftRoot, id, CancellationToken.None, captureBefore: CaptureBefore));
        Equal(true, corruption.Message.Contains("损坏", StringComparison.Ordinal));
        Equal(0, safetyCaptures);
        Equal(0, context.TransactionCalls);
        Equal(1, context.Points().Count);
        Equal("current payload", File.ReadAllText(context.GamePath("config/pack.toml")));
        Equal("{\"id\":\"example\",\"type\":\"release\"}", File.ReadAllText(context.GamePath("example.json")));
        AssertClean(context);
    }

    private static void CoveragePlans() {
        using Fixture fixture = new();
        Context context = new(fixture);
        context.Put("config/pack.toml", "unchanged payload");
        string id = context.Capture("coverage fixture");
        context.Engine.MarkHealthy(context.Owner.VersionPath, id, true);
        JObject point = context.Point(id);
        point.Remove("coverageVersion");
        context.WritePoint(id, point);
        context.Put("coremods/new.jar", "untracked by legacy coverage");
        Report legacy = context.Changes.BuildInstance(context.Owner, CancellationToken.None);
        Equal(true, legacy.HasBaseline);
        Equal(0, legacy.Changes.Count);
        Equal(true, legacy.Message!.Contains("旧还原点", StringComparison.Ordinal));
        List<List<Tuple<string, string?, string?>>> plans = new();
        NotSupportedException boundary = new("Inspect only the C# plan; the real VB transaction is exercised by Windows product tests.");
        context.Host.ApplyTransaction = (_, _, operations, _, _) => { plans.Add(operations); throw boundary; };
        Equal(boundary, Throws<NotSupportedException>(() => context.Engine.Restore(context.Owner.VersionPath, context.Owner.GameFolder, context.MinecraftRoot, id, CancellationToken.None)));
        Equal(0, plans.Single().Count);
        point["coverageVersion"] = 2;
        context.WritePoint(id, point);
        Report current = context.Changes.BuildInstance(context.Owner, CancellationToken.None);
        Equal("g/coremods/new.jar", current.Changes.Single().Relative);
        Equal(boundary, Throws<NotSupportedException>(() => context.Engine.Restore(context.Owner.VersionPath, context.Owner.GameFolder, context.MinecraftRoot, id, CancellationToken.None)));
        Tuple<string, string?, string?> removal = plans[1].Single();
        Equal(context.GamePath("coremods/new.jar"), removal.Item1);
        Equal<string?>(null, removal.Item2);
        Equal(Digest(removal.Item1), removal.Item3);
        point["coverageVersion"] = 99;
        context.WritePoint(id, point);
        Throws<IOException>(() => context.Changes.BuildInstance(context.Owner, CancellationToken.None));
        Throws<IOException>(() => context.Engine.Restore(context.Owner.VersionPath, context.Owner.GameFolder, context.MinecraftRoot, id, CancellationToken.None));
        Equal(2, plans.Count);
        Equal("untracked by legacy coverage", File.ReadAllText(context.GamePath("coremods/new.jar")));
        Equal("unchanged payload", File.ReadAllText(context.GamePath("config/pack.toml")));
        AssertClean(context);
    }

    private static void ScanAndCancellation() {
        using Fixture fixture = new();
        Context context = new(fixture);
        context.Put("config/pack.toml", "recorded payload");
        string target = context.GamePath("config/pack.toml");
        using (CancellationTokenSource canceled = new()) {
            bool triggered = false;
            context.Host.Digest = path => {
                string? hash = Digest(path);
                if (!triggered && path == target) { triggered = true; canceled.Cancel(); }
                return hash;
            };
            Throws<OperationCanceledException>(() => context.Engine.Capture(context.Owner.VersionPath, context.Owner.GameFolder, "canceled capture", canceled.Token));
            Equal(true, triggered);
            Equal(0, context.Points().Count);
        }
        context.Host.Digest = Digest;
        AssertClean(context);
        string id = context.Capture("healthy fixture");
        context.Engine.MarkHealthy(context.Owner.VersionPath, id, true);
        bool changed = false;
        context.RecentHost.Digest = (path, token) => {
            token.ThrowIfCancellationRequested();
            string hash = Digest(path)!;
            if (!changed && path == target) { changed = true; File.WriteAllText(target, "changed during scan", Utf8); }
            return hash;
        };
        IOException unstable = Throws<IOException>(() => context.Changes.BuildInstance(context.Owner, CancellationToken.None));
        Equal(true, changed);
        Equal(true, unstable.Message.Contains("扫描期间", StringComparison.Ordinal));
        context.RecentHost.Digest = (path, token) => { token.ThrowIfCancellationRequested(); return Digest(path)!; };
        Equal("g/config/pack.toml", context.Changes.BuildInstance(context.Owner, CancellationToken.None).Changes.Single().Relative);
        using (CancellationTokenSource canceled = new()) {
            bool triggered = false;
            context.RecentHost.Digest = (path, token) => { token.ThrowIfCancellationRequested(); string hash = Digest(path)!; triggered = true; canceled.Cancel(); return hash; };
            Throws<OperationCanceledException>(() => context.Changes.BuildInstance(context.Owner, canceled.Token));
            Equal(true, triggered);
        }
        Equal(1, context.Points().Count);
        using (new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        AssertClean(context);
    }

    private static void AssertClean(Context context) {
        Equal(0, context.Engine.RestoringCount);
        if (Directory.Exists(context.Store))
            Equal(false, Directory.EnumerateFiles(context.Store, "*", SearchOption.AllDirectories).Any(path => path.Contains(".tmp-", StringComparison.Ordinal) || path.Contains(".pcl-write-", StringComparison.Ordinal)));
    }
    // SHA1 and native paths are controlled adapters for services still owned by
    // the VB host, not replacements for its path checks or transaction algorithm.
    // Fixtures create no links; real link/lock/restore behavior is covered by
    // the unchanged Windows EnvironmentHistory/Retention/Coverage product tests.
    private static string? Digest(string path) {
        if (!File.Exists(path)) return null;
        using Stream input = File.OpenRead(path);
        using SHA1 hash = SHA1.Create();
        return "sha1:" + Convert.ToHexString(hash.ComputeHash(input)).ToLowerInvariant();
    }
    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual) { if (!expected.SequenceEqual(actual)) throw new Exception("Sequences differ."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}]."); }
    private static T Throws<T>(Action run) where T : Exception { try { run(); } catch (Exception error) { if (error is T match) return match; throw; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class Owner { internal string VersionPath = null!, GameFolder = null!; internal bool AutoCleanup; internal int Retention = 5; }
    private sealed class Change : RecentEnvironmentChangeData { internal readonly string Marker = "derived change"; }
    private sealed class Report : RecentEnvironmentChangesReportData<Change> { internal readonly string Marker = "derived report"; }

    private sealed class Context {
        internal readonly Owner Owner;
        internal readonly EnvironmentHistoryHost<Owner> Host;
        internal readonly EnvironmentHistoryEngine<Owner> Engine;
        internal readonly RecentEnvironmentHost<Owner> RecentHost;
        internal readonly RecentEnvironmentChangesEngine<Owner, Change, Report> Changes;
        internal readonly string MinecraftRoot;
        internal int TransactionCalls;
        private long clock;
        internal Context(Fixture fixture) {
            MinecraftRoot = fixture.Directory("minecraft");
            Owner = new Owner { VersionPath = fixture.Directory("minecraft/versions/example") };
            Owner.GameFolder = Owner.VersionPath;
            Host = new EnvironmentHistoryHost<Owner> {
                DataRoot = () => LocalGameDataStore.GetRoot(fixture.Path("profile")), Hash = LocalGameDataStore.Hash,
                AtomicWrite = (path, bytes) => LocalGameDataStore.AtomicWrite(path, bytes, null, false),
                Target = fixture.Target, RelativePath = RelativePath, AddSlashSuffix = path => System.IO.Path.TrimEndingDirectorySeparator(path) + System.IO.Path.DirectorySeparatorChar,
                RemoveExtendedPrefix = path => path, SafeName = InstanceResourceNameRules.SafeName, Digest = Digest,
                EnsureNoLinks = fixture.EnsureOwned, EnsureWithin = EnsureWithin, IsParentOf = IsParentOf, SamePath = SamePath,
                ApplyTransaction = (_, _, _, _, _) => { TransactionCalls++; throw new Exception("The portable fixture must not invoke the retained VB transaction."); },
                VersionPath = owner => owner.VersionPath, GameFolder = owner => owner.GameFolder, VanillaName = _ => "1.20.1",
                LibraryPaths = _ => Array.Empty<string>(), ModLoaderVersions = _ => Array.Empty<string>(), JsonObject = _ => new JObject(new JProperty("id", "example")),
                AutoCleanup = owner => owner.AutoCleanup, Retention = owner => owner.Retention,
                Info = _ => { }, OpenFolder = _ => { }, Hint = _ => { }, Warn = (_, _) => { }
            };
            Engine = new EnvironmentHistoryEngine<Owner>(new object(), Host);
            RecentHost = new RecentEnvironmentHost<Owner> {
                NormalizeRoot = path => System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar), RelativePath = RelativePath,
                EnsureNoLinks = fixture.EnsureOwned, EnsureWithin = EnsureWithin, Target = fixture.Target,
                Digest = (path, token) => { token.ThrowIfCancellationRequested(); return Digest(path)!; },
                SamePath = (first, second) => first is not null && SamePath(first, second),
                VersionPath = owner => owner.VersionPath, GameRootForInstance = owner => owner.GameFolder
            };
            Changes = new RecentEnvironmentChangesEngine<Owner, Change, Report>(Engine, RecentHost, () => new Change(), () => new Report());
            Put("example.json", "{\"id\":\"example\",\"type\":\"release\"}");
        }
        internal string Store => Engine.Store(Owner.VersionPath);
        internal string GamePath(string relative) => System.IO.Path.Combine(Owner.GameFolder, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        internal void Put(string relative, string text) { string path = GamePath(relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); File.WriteAllText(path, text, Utf8); }
        internal List<JObject> Points() => Engine.Points(Owner.VersionPath);
        internal JObject Point(string id) => Points().Single(point => (string?)point["id"] == id);
        internal void WritePoint(string id, JObject point) => LocalGameDataStore.AtomicWrite(System.IO.Path.Combine(Store, "points", id + ".json"), Utf8.GetBytes(point.ToString()), null, false);
        internal string Capture(string label) {
            string id = Engine.Capture(Owner.VersionPath, Owner.GameFolder, label, CancellationToken.None);
            JObject point = Point(id);
            point["created"] = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++clock);
            WritePoint(id, point);
            return id;
        }
        internal string Blob(string id, string key) => System.IO.Path.Combine(Store, "objects", Point(id)["files"]![key]!.ToString()[5..]);
        internal string[] ObjectFiles() => Directory.GetFiles(System.IO.Path.Combine(Store, "objects"));
    }

    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static bool SamePath(string first, string second) => string.Equals(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(first)), System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(second)), Comparison);
    private static bool IsParentOf(string root, string path) => System.IO.Path.GetFullPath(path).StartsWith(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root)) + System.IO.Path.DirectorySeparatorChar, Comparison);
    private static void EnsureWithin(string root, string path) { if (!SamePath(root, path) && !IsParentOf(root, path)) throw new IOException("Fixture path escaped its root."); }
    private static string RelativePath(string path) {
        if (string.IsNullOrWhiteSpace(path) || System.IO.Path.IsPathRooted(path)) throw new IOException("Invalid fixture relative path.");
        string normalized = path.Replace('\\', '/');
        if (normalized.Split('/').Any(part => part.Length == 0 || part == "." || part == "..")) throw new IOException("Invalid fixture relative path.");
        return normalized;
    }
    private sealed class Fixture : IDisposable {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PCLLF-EnvironmentHistory-" + Guid.NewGuid().ToString("N"));
        internal Fixture() => System.IO.Directory.CreateDirectory(Root);
        internal string Path(string relative) { string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, relative)); EnsureWithin(Root, path); return path; }
        internal string Directory(string relative) { string path = Path(relative); System.IO.Directory.CreateDirectory(path); return path; }
        internal string Target(string root, string relative) { string path = System.IO.Path.Combine(root, RelativePath(relative).Replace('/', System.IO.Path.DirectorySeparatorChar)); EnsureWithin(root, path); if (System.IO.Directory.Exists(path)) throw new IOException("Fixture target is a directory."); return path; }
        internal void EnsureOwned(string path) => EnsureWithin(Root, path);
        public void Dispose() {
            string temporary = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (!Root.StartsWith(temporary, Comparison) || !Regex.IsMatch(System.IO.Path.GetFileName(Root), @"\APCLLF-EnvironmentHistory-[0-9a-f]{32}\z")) throw new Exception("Unsafe history fixture cleanup.");
            foreach (string path in System.IO.Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            System.IO.Directory.Delete(Root, true);
        }
    }
}
