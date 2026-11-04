using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using PCL.Living.Features;

internal static class InstanceClonePipelineTests {
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("instance clone pipeline publishes independent files sanitized metadata and only selected personal data", CloneFilesAndMetadata);
        yield return ("instance clone pipeline cancellation changed source and target collisions leave no owned staging", CloneFailureCleanup);
        yield return ("instance clone pipeline resolves isolation without writing settings and keeps shared source files independent", IsolationAndGuards);
        yield return ("instance clone pipeline rejects stale previews then captures real history before inspecting the retained transaction boundary", MigrationPlanBoundary);
        yield return ("instance clone pipeline groups actual clone origins and keeps missing malformed or cyclic parents visible", ActualOriginGroups);
        yield return ("instance clone export pipeline reads real ZIP choices and preserves Windows relative routing without a fake INI host", ExportArchivePolicy);
    }

    private static void CloneFilesAndMetadata() {
        using Fixture fixture = new();
        Context context = new(fixture);
        context.Put("mods/example.jar", "mod bytes");
        context.Put("config/user.toml", "user-config");
        context.Put("options.txt", "key_key.jump:key.keyboard.space");
        context.Put("coremods/extra.jar", "coremod bytes");
        context.Put("resourcepacks/favorite.zip", "resource bytes");
        context.Put("saves/World/level.dat", "world bytes");
        context.Put("screenshots/shot.png", "screenshot bytes");
        context.Put("servers.dat", "server bytes");
        foreach (string relative in new[] { "config/accounts.json", "mods/.connector/cache.jar", "logs/latest.log", "PCL/PackUpdateBackups/old.bak" }) context.Put(relative, "private excluded bytes");
        Dictionary<string, string> original = Snapshot(context.Source);
        string[] copied = ["original.json", "original.jar", "PCL/Setup.ini", "PCL/config.json", "mods/example.jar", "config/user.toml", "options.txt", "coremods/extra.jar", "resourcepacks/favorite.zip"];
        long bytes = copied.Sum(relative => new FileInfo(context.SourcePath(relative)).Length);
        Result result = context.Engine.CloneInstance(context.Owner, "minimal", null, CancellationToken.None);
        string destination = Folder(result);
        Equal("derived result", result.Marker);
        Equal("minimal", result.Name);
        Equal(1, context.OptionsCreated);
        Equal(copied.Length, result.FileCount);
        Equal(bytes, result.Bytes);
        Equal("mod bytes", File.ReadAllText(Path.Combine(destination, "mods", "example.jar")));
        JObject json = ReadJson(Path.Combine(destination, "minimal.json"));
        Equal("minimal", (string?)json["id"]);
        Equal("minimal", (string?)json["jar"]);
        Equal(destination + "\\java\\bin", (string?)json["windowsPath"]);
        Equal(destination.Replace('\\', '/') + "/runtime/lib", (string?)json["forwardPath"]);
        string setup = File.ReadAllText(Path.Combine(destination, "PCL", "Setup.ini"));
        Equal(true, setup.Contains("VersionArgumentIndieV2:True", StringComparison.Ordinal));
        Equal(true, setup.Contains("VersionRamCustom:23", StringComparison.Ordinal));
        Equal(false, setup.Contains("secret", StringComparison.Ordinal));
        JObject config = ReadJson(Path.Combine(destination, "PCL", "config.json"));
        Equal(1, config.Count);
        Equal("old", (string?)config["MinecraftInstallManifest"]?["fixture"]);
        JObject origin = ReadJson(Path.Combine(destination, "PCL", "CloneOrigin.json"));
        Equal(context.Source, (string?)origin["sourceVersionFolder"]);
        Equal(context.Source, (string?)origin["sourceGameRoot"]);
        Equal("minimal", (string?)origin["cloneName"]);
        foreach (string relative in new[] { "saves", "screenshots", "servers.dat", "config/accounts.json", "mods/.connector", "logs", "PCL/PackUpdateBackups" })
            Equal(false, Path.Exists(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar))));
        File.WriteAllText(Path.Combine(destination, "mods", "example.jar"), "clone edit", Utf8);
        SameSnapshot(original, context.Source);
        Result full = context.Engine.CloneInstance(context.Owner, "with-personal-data", new Options { CopySaves = true, CopyScreenshots = true, CopyServers = true }, CancellationToken.None);
        foreach (string relative in new[] { "saves/World/level.dat", "screenshots/shot.png", "servers.dat" })
            Equal(File.ReadAllText(context.SourcePath(relative)), File.ReadAllText(Path.Combine(Folder(full), relative.Replace('/', Path.DirectorySeparatorChar))));
        SameSnapshot(original, context.Source);
        AssertClean(context);
    }

    private static void CloneFailureCleanup() {
        using Fixture fixture = new();
        Context context = new(fixture);
        context.Put("config/user.toml", "unchanged");
        Dictionary<string, string> original = Snapshot(context.Source);
        using (CancellationTokenSource canceled = new()) {
            canceled.Cancel();
            Throws<OperationCanceledException>(() => context.Engine.Clone(context.Source, context.Source, "pre-canceled", null, canceled.Token));
        }
        using (CancellationTokenSource canceled = new()) {
            int copied = 0;
            Throws<OperationCanceledException>(() => context.Engine.Clone(context.Source, context.Source, "mid-canceled", null, canceled.Token, count => { copied = count; canceled.Cancel(); }));
            Equal(1, copied);
        }
        string external = context.SourcePath("config/during-copy.toml");
        Throws<IOException>(() => context.Engine.Clone(context.Source, context.Source, "source-changed", null, CancellationToken.None, count => { if (count == 1) context.Put("config/during-copy.toml", "external user edit"); }));
        Equal("external user edit", File.ReadAllText(external));
        File.Delete(external);
        string collision = fixture.Directory("minecraft/versions/collision");
        fixture.Text("minecraft/versions/collision/keep.txt", "existing destination");
        Throws<IOException>(() => context.Engine.Clone(context.Source, context.Source, "collision", null, CancellationToken.None));
        Equal("existing destination", File.ReadAllText(Path.Combine(collision, "keep.txt")));
        foreach (string name in new[] { "pre-canceled", "mid-canceled", "source-changed" }) Equal(false, Directory.Exists(Path.Combine(context.Versions, name)));
        SameSnapshot(original, context.Source);
        AssertClean(context);
        context.Engine.Clone(context.Source, context.Source, "after-failures", null, CancellationToken.None);
        SameSnapshot(original, context.Source);
        AssertClean(context);
    }

    private static void IsolationAndGuards() {
        using Fixture fixture = new();
        Context context = new(fixture);
        context.Put("PCL/Setup.ini", "VersionArgumentIndie:1\r\nVersionArgumentIndieV2:False\r\nUnrelated:preserved\r\n");
        Dictionary<string, string> explicitChoice = Snapshot(context.Source);
        Equal(context.MinecraftRoot, context.Engine.GameRootForInstance(context.Owner));
        SameSnapshot(explicitChoice, context.Source);
        context.Put("PCL/Setup.ini", "VersionArgumentIndie:1\r\n");
        Equal(context.Source, context.Engine.GameRootForInstance(context.Owner));
        File.Delete(context.SourcePath("PCL/Setup.ini"));
        Equal(context.MinecraftRoot, context.Engine.GameRootForInstance(context.Owner));
        context.IsolationMode = 1; context.Owner.HasModLoaders = true;
        Equal(context.Source, context.Engine.GameRootForInstance(context.Owner));
        context.Owner.HasModLoaders = false; context.IsolationMode = 2; context.Owner.IsSnapshot = true;
        Equal(context.Source, context.Engine.GameRootForInstance(context.Owner));
        context.Owner.IsSnapshot = false; context.IsolationMode = 0;
        context.Put("mods/direct.jar", "direct mod");
        Dictionary<string, string> inferred = Snapshot(context.Source);
        Equal(context.Source, context.Engine.GameRootForInstance(context.Owner));
        Equal(false, File.Exists(context.SourcePath("PCL/Setup.ini")));
        SameSnapshot(inferred, context.Source);
        context.Pending = true;
        Throws<IOException>(() => context.Engine.Clone(context.Source, context.Source, "pending", null, CancellationToken.None));
        context.Pending = false; context.Active = true;
        Throws<IOException>(() => context.Engine.Clone(context.Source, context.Source, "active", null, CancellationToken.None));
        context.Active = false;
        fixture.Text("minecraft/mods/shared.jar", "shared root mod");
        Result result = context.Engine.Clone(context.Source, context.MinecraftRoot, "from-shared-root", null, CancellationToken.None);
        string destination = Folder(result);
        Equal("shared root mod", File.ReadAllText(Path.Combine(destination, "mods", "shared.jar")));
        Equal(false, File.Exists(Path.Combine(destination, "mods", "direct.jar")));
        Equal(true, File.ReadAllText(Path.Combine(destination, "PCL", "Setup.ini")).Contains("VersionArgumentIndieV2:True", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(destination, "mods", "shared.jar"), "independent clone edit", Utf8);
        Equal("shared root mod", File.ReadAllText(Path.Combine(context.MinecraftRoot, "mods", "shared.jar")));
        SameSnapshot(inferred, context.Source);
        AssertClean(context);
    }

    private static void MigrationPlanBoundary() {
        using Fixture fixture = new();
        Context context = new(fixture);
        context.Put("mods/example.jar", "original mod");
        string clone = Folder(context.Engine.CloneInstance(context.Owner, "tested-clone", null, CancellationToken.None));
        Equal(0, context.Engine.PreviewMigration(clone, context.Source, CancellationToken.None).Changes.Count);
        File.WriteAllText(Path.Combine(clone, "mods", "example.jar"), "tested mod", Utf8);
        Plan plan = context.Engine.PreviewMigration(clone, context.Source, CancellationToken.None);
        Equal("derived plan", plan.Marker);
        Equal(true, plan.Changes.Any(change => change.Relative == "g/mods/example.jar" && change.Marker == "derived change"));
        context.Put("mods/example.jar", "external source edit");
        Throws<IOException>(() => context.Engine.ApplyMigration(plan, CancellationToken.None));
        Equal("external source edit", File.ReadAllText(context.SourcePath("mods/example.jar")));
        context.Put("mods/example.jar", "original mod");
        string markerPath = Path.Combine(clone, "PCL", "CloneOrigin.json");
        string originalMarker = File.ReadAllText(markerPath);
        JObject changedMarker = JObject.Parse(originalMarker); changedMarker["fixtureMutation"] = true;
        File.WriteAllText(markerPath, changedMarker.ToString(), Utf8);
        Throws<IOException>(() => context.Engine.ApplyMigration(plan, CancellationToken.None));
        File.WriteAllText(markerPath, originalMarker, Utf8);
        using (CancellationTokenSource canceled = new()) {
            canceled.Cancel();
            Throws<OperationCanceledException>(() => context.Engine.ApplyMigration(plan, canceled.Token));
        }
        Equal(0, context.History.Points(context.Source).Count);
        Equal(0, context.TransactionCalls);
        JObject cloneJson = ReadJson(Path.Combine(clone, "tested-clone.json")); cloneJson["clientVersion"] = "1.21.1";
        File.WriteAllText(Path.Combine(clone, "tested-clone.json"), cloneJson.ToString(), Utf8);
        File.WriteAllText(Path.Combine(clone, "PCL", "config.json"), "{\"MinecraftInstallManifest\":{\"fixture\":\"new\"},\"AccountToken\":\"foreign-clone-token\"}", Utf8);
        plan = context.Engine.PreviewMigration(clone, context.Source, CancellationToken.None);
        Equal("1.20.1", plan.OldMinecraft); Equal("1.21.1", plan.NewMinecraft);
        Throws<IOException>(() => context.Engine.ApplyMigration(plan, CancellationToken.None));
        Equal(0, context.History.Points(context.Source).Count);
        Dictionary<string, string> original = Snapshot(context.Source);
        NotSupportedException boundary = new("Inspect the real prepared payloads; Windows product tests run the retained VB transaction.");
        context.Host.ApplyTransaction = (_, _, operations, _, _) => {
            context.TransactionCalls++; context.Events.Add("transaction boundary");
            Tuple<string, string?, string?> mod = operations.Single(operation => operation.Item1 == context.SourcePath("mods/example.jar"));
            Equal("tested mod", File.ReadAllText(mod.Item2!));
            JObject config = ReadJson(operations.Single(operation => operation.Item1 == context.SourcePath("PCL/config.json")).Item2!);
            Equal("source-secret-token", (string?)config["AccountToken"]);
            Equal("new", (string?)config["MinecraftInstallManifest"]?["fixture"]);
            JObject json = ReadJson(operations.Single(operation => operation.Item1 == context.SourcePath("original.json")).Item2!);
            Equal("original", (string?)json["id"]); Equal("original", (string?)json["jar"]);
            throw boundary;
        };
        Equal(boundary, Throws<NotSupportedException>(() => context.Engine.ApplyMigration(plan, CancellationToken.None, beforeMinecraftChange: (oldVersion, newVersion) => { Equal("1.20.1", oldVersion); Equal("1.21.1", newVersion); context.Events.Add("world backup boundary"); })));
        Sequence(new[] { "world backup boundary", "history capture", "transaction boundary" }, context.Events);
        Equal(1, context.History.Points(context.Source).Count);
        Equal("测试副本迁移前", (string?)context.History.Points(context.Source).Single()["label"]);
        Equal(1, context.TransactionCalls);
        SameSnapshot(original, context.Source);
        AssertClean(context);
    }

    private static void ActualOriginGroups() {
        using Fixture fixture = new();
        Context context = new(fixture);
        string first = Folder(context.Engine.CloneInstance(context.Owner, "first-clone", null, CancellationToken.None));
        string second = Folder(context.Engine.Clone(first, first, "second-clone", null, CancellationToken.None));
        InstanceCloneGroupsEngine<Node, GroupSet> groups = new(path => path, context.Engine.EnsureNoLinks, () => new Node(), () => new GroupSet());
        GroupSet set = groups.Build([second, first, context.Source, first], CancellationToken.None);
        Node root = set.Roots.Single();
        Equal(context.Source, root.VersionFolder);
        Sequence(new[] { context.Source, first, second }, groups.Flatten(root).Select(node => node.VersionFolder!));
        Equal(true, groups.Flatten(root).All(node => node.Marker == "derived node"));
        Equal("derived group set", set.Marker);
        Equal(true, ReferenceEquals(root, groups.Find(set, context.Source)));
        Equal(true, groups.Contains(root, second));
        GroupSet hiddenParent = groups.Build([second], CancellationToken.None);
        Equal<string?>(null, hiddenParent.Roots.Single().ParentFolder);
        Equal(first, hiddenParent.Roots.Single().DeclaredSourceFolder);
        string marker = Path.Combine(first, "PCL", "CloneOrigin.json");
        string original = File.ReadAllText(marker);
        File.WriteAllText(marker, "[]", Utf8);
        GroupSet malformed = groups.Build([second, first, context.Source], CancellationToken.None);
        Equal(3, malformed.ByPath.Count);
        Equal(2, malformed.Roots.Count);
        Equal(first, groups.Find(malformed, second)!.ParentFolder);
        JObject cycle = JObject.Parse(original); cycle["sourceVersionFolder"] = second; cycle["sourceName"] = "second-clone"; cycle["sourceGameRoot"] = second;
        File.WriteAllText(marker, cycle.ToString(), Utf8);
        GroupSet cyclic = groups.Build([second, first, context.Source], CancellationToken.None);
        Equal(3, cyclic.Roots.Count);
        Equal(true, cyclic.ByPath.Values.All(node => node.ParentFolder is null));
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Throws<OperationCanceledException>(() => groups.Build([context.Source, first], canceled.Token));
        Equal(true, File.Exists(context.SourcePath("original.json")));
        AssertClean(context);
    }

    private static void ExportArchivePolicy() {
        using Fixture fixture = new();
        InstanceCloneExportIsolationHost host = new() {
            WriteIni = (_, _, _) => throw new Exception("The archive-only fixture must not emulate LF INI writes."),
            ClearIniCache = _ => throw new Exception("The archive-only fixture must not emulate LF caches."),
            AddSlashSuffix = path => path + Path.DirectorySeparatorChar,
            RemoveSettingCache = (_, _) => throw new Exception("The archive-only fixture must not emulate LF settings."),
            CopyFile = (_, _) => throw new Exception("Windows product tests exercise actual override copying.")
        };
        InstanceCloneExportIsolationEngine<Choice> engine = new(host, (enabled, folders) => new Choice(enabled, folders));
        foreach (string name in new[] { "wrapper/overrides/PCL/Setup.ini", "wrapper\\overrides\\PCL\\Setup.ini" }) {
            string zipPath = fixture.Path("policy-" + Guid.NewGuid().ToString("N") + ".zip");
            using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            using (StreamWriter writer = new(archive.CreateEntry(name).Open(), Utf8))
                writer.Write("LivingExportVersionIsolation:False\r\nLivingExportVersionFolders:PCL|runtime/jre\r\nVersionArgumentIndieV2:True\r\n");
            using ZipArchive input = ZipFile.OpenRead(zipPath);
            Choice policy = engine.ReadArchiveChoice(input, "wrapper/") ?? throw new Exception("Explicit LF export choice was lost.");
            Equal(false, policy.Enabled);
            Equal("derived choice", policy.Marker);
            string version = fixture.Directory("minecraft/versions/imported"), game = fixture.Directory("minecraft");
            Equal(game, engine.GameFolder(version, game, policy));
            Equal(version, engine.GameFolder(version, game, null));
            // Keep the original Windows relative-string contract on Linux too;
            // no override filesystem behavior is inferred from this assertion.
            Equal(Path.Combine(version, "runtime\\jre\\bin\\java.exe"), engine.OverrideTarget("runtime/jre/bin/java.exe", version, game, policy));
            Equal(Path.Combine(game, "mods\\fixture.jar"), engine.OverrideTarget("mods/fixture.jar", version, game, policy));
            Throws<InvalidDataException>(() => engine.OverrideTarget("versions/other/other.json", version, game, policy));
            Throws<InvalidDataException>(() => engine.OverrideTarget("../escape", version, game, policy));
        }
        using MemoryStream legacy = new();
        using (ZipArchive archive = new(legacy, ZipArchiveMode.Create, true))
        using (StreamWriter writer = new(archive.CreateEntry("overrides/PCL/Setup.ini").Open(), Utf8)) writer.Write("VersionArgumentIndieV2:False\r\n");
        legacy.Position = 0;
        using (ZipArchive archive = new(legacy, ZipArchiveMode.Read, true)) Equal<Choice?>(null, engine.ReadArchiveChoice(archive, ""));
        using MemoryStream oversized = new();
        using (ZipArchive archive = new(oversized, ZipArchiveMode.Create, true))
        using (StreamWriter writer = new(archive.CreateEntry("overrides/PCL/Setup.ini").Open(), Utf8)) writer.Write(new string('x', 1024 * 1024 + 1));
        oversized.Position = 0;
        using (ZipArchive archive = new(oversized, ZipArchiveMode.Read, true))
            Throws<InvalidDataException>(() => engine.ReadArchiveChoice(archive, ""));
    }

    private static string Folder(Result result) => Path.TrimEndingDirectorySeparator(result.VersionFolder ?? throw new Exception("Clone folder was not returned."));
    private static JObject ReadJson(string path) => JObject.Parse(File.ReadAllText(path));
    private static Dictionary<string, string> Snapshot(string folder) => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(path => Path.GetRelativePath(folder, path), path => LocalGameDataStore.Hash(File.ReadAllBytes(path)));
    private static void SameSnapshot(Dictionary<string, string> expected, string folder) => Sequence(expected.OrderBy(pair => pair.Key), Snapshot(folder).OrderBy(pair => pair.Key));
    private static void AssertClean(Context context) => Equal(0, Directory.GetDirectories(context.Versions, ".pcl-clone-*").Length);
    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual) { if (!expected.SequenceEqual(actual)) throw new Exception("Sequences differ."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}]."); }
    private static T Throws<T>(Action run) where T : Exception { try { run(); } catch (Exception error) { if (error is T match) return match; throw; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class Owner { internal string VersionPath = null!; internal bool HasModLoaders, IsSnapshot; }
    private sealed class Options : InstanceCloneOptionsData { }
    private sealed class Result : InstanceCloneResultData { internal readonly string Marker = "derived result"; }
    private sealed class Change : InstanceCloneMigrationChangeData { internal readonly string Marker = "derived change"; }
    private sealed class Plan : InstanceCloneMigrationPlanData<Change> { internal readonly string Marker = "derived plan"; }
    private sealed class Node : InstanceCloneGroupNodeData<Node> { internal readonly string Marker = "derived node"; }
    private sealed class GroupSet : InstanceCloneGroupSetData<Node> { internal readonly string Marker = "derived group set"; }
    private sealed class Choice(bool enabled, IEnumerable<string>? folders) : InstanceCloneExportChoiceData(enabled, folders) { internal readonly string Marker = "derived choice"; }

    private sealed class Context {
        internal readonly Owner Owner;
        internal readonly string MinecraftRoot, Versions, Source;
        internal readonly InstanceCloneHost<Owner> Host;
        internal readonly InstanceCloneEngine<Owner, Options, Result, Change, Plan> Engine;
        internal readonly EnvironmentHistoryEngine<Owner> History;
        internal readonly List<string> Events = new();
        internal int OptionsCreated, TransactionCalls, IsolationMode;
        internal bool Pending, Active;
        internal Context(Fixture fixture) {
            MinecraftRoot = fixture.Directory("minecraft"); Versions = fixture.Directory("minecraft/versions"); Source = fixture.Directory("minecraft/versions/original");
            Owner = new Owner { VersionPath = Source };
            // Native containment/identity and process state are controlled host
            // adapters. Scanning, link detection, copying, hashing, version
            // parsing and history storage use production C#; no VB transaction
            // or INI behavior is simulated.
            Host = new InstanceCloneHost<Owner> {
                RemoveExtendedPrefix = path => path, RelativePath = RelativePath, SafeName = InstanceResourceNameRules.SafeName,
                AddSlashSuffix = path => Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar,
                EnsureWithin = EnsureWithin, SamePath = SamePath,
                HasRunningGame = () => false, HasRunningSessions = () => false, LaunchLoading = () => false, WorldBackupBusy = () => false,
                HasActiveGame = _ => Active, HasPending = _ => Pending,
                VersionPath = owner => owner.VersionPath, VanillaName = _ => "1.20.1", IsFool = _ => false,
                IsSnapshot = owner => owner.IsSnapshot, HasModLoaders = owner => owner.HasModLoaders, ApiType = _ => false, IsolationMode = () => IsolationMode,
                ApplyTransaction = (_, _, _, _, _) => { TransactionCalls++; throw new Exception("A portable test cannot replace the retained VB transaction."); }
            };
            Engine = new InstanceCloneEngine<Owner, Options, Result, Change, Plan>(new object(), Host, () => { OptionsCreated++; return new Options(); }, () => new Result(), () => new Change(), () => new Plan());
            History = new EnvironmentHistoryEngine<Owner>(new object(), new EnvironmentHistoryHost<Owner> {
                DataRoot = () => fixture.Path("profile"), Hash = LocalGameDataStore.Hash,
                AtomicWrite = (path, bytes) => LocalGameDataStore.AtomicWrite(path, bytes, null, false),
                RelativePath = RelativePath, Target = (root, relative) => { string path = Path.Combine(root, RelativePath(relative).Replace('/', Path.DirectorySeparatorChar)); EnsureWithin(root, path); return path; },
                AddSlashSuffix = Host.AddSlashSuffix, RemoveExtendedPrefix = path => path, SafeName = InstanceResourceNameRules.SafeName,
                Digest = path => File.Exists(path) ? Engine.Digest(path, CancellationToken.None) : null,
                EnsureNoLinks = Engine.EnsureNoLinks, EnsureWithin = EnsureWithin, SamePath = SamePath
            });
            Host.CaptureEnvironment = (version, game, label, token) => { Events.Add("history capture"); return History.Capture(version, game, label, token); };
            Put("original.json", new JObject(new JProperty("id", "original"), new JProperty("jar", "original"), new JProperty("clientVersion", "1.20.1"), new JProperty("type", "release"), new JProperty("mainClass", "net.minecraft.client.main.Main"), new JProperty("libraries", new JArray()), new JProperty("windowsPath", Source + "\\java\\bin"), new JProperty("forwardPath", Source.Replace('\\', '/') + "/runtime/lib")).ToString());
            Put("original.jar", "synthetic client bytes");
            Put("PCL/Setup.ini", "VersionArgumentIndieV2:True\r\nVersionRamCustom:23\r\nCacheMsAccess:secret-token\r\nVersionAdvanceRun:secret-command\r\n");
            Put("PCL/config.json", "{\"MinecraftInstallManifest\":{\"fixture\":\"old\"},\"AccountToken\":\"source-secret-token\"}");
        }
        internal string SourcePath(string relative) => Path.Combine(Source, relative.Replace('/', Path.DirectorySeparatorChar));
        internal void Put(string relative, string text) { string path = SourcePath(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, Utf8); }
    }

    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static bool SamePath(string first, string second) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), Comparison);
    private static void EnsureWithin(string root, string path) { if (!SamePath(root, path) && !Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, Comparison)) throw new IOException("Fixture path escaped its root."); }
    private static string RelativePath(string path) { string normalized = path.Replace('\\', '/'); if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || normalized.Split('/').Any(part => part.Length == 0 || part == "." || part == "..")) throw new IOException("Invalid fixture relative path."); return normalized; }
    private sealed class Fixture : IDisposable {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PCLLF-InstanceClone-" + Guid.NewGuid().ToString("N"));
        internal Fixture() => System.IO.Directory.CreateDirectory(Root);
        internal string Path(string relative) { string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, relative)); EnsureWithin(Root, path); return path; }
        internal string Directory(string relative) { string path = Path(relative); System.IO.Directory.CreateDirectory(path); return path; }
        internal void Text(string relative, string text) { string path = Path(relative); System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); File.WriteAllText(path, text, Utf8); }
        public void Dispose() {
            string temporary = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (!Root.StartsWith(temporary, Comparison) || !Regex.IsMatch(System.IO.Path.GetFileName(Root), @"\APCLLF-InstanceClone-[0-9a-f]{32}\z")) throw new Exception("Unsafe clone fixture cleanup.");
            foreach (string path in System.IO.Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            System.IO.Directory.Delete(Root, true);
        }
    }
}
