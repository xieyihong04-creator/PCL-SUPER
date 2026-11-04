using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using PCL.Living.Features;

internal static class ResourceRecyclePipelineTests {
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("resource recycle invalid IDs fail before reading paths or calling host adapters", InvalidIdBeforeIo);
        yield return ("map install pre-cancellation wins before path parsing and host adapters", MapPreCancellation);
        if (OperatingSystem.IsWindows()) {
            yield return ("resource Windows recycle lists original derived entries and restores or purges selected payloads", RecycleRoundTrip);
            yield return ("resource Windows recycle hides malformed oversized and foreign records without deleting them", HiddenRecords);
            yield return ("resource Windows restore prevalidates every collision and cancellation preserves all payloads", RestorePreflightAndCancellation);
            yield return ("resource Windows purge prevalidates linked trees and refuses a linked recycle root", PurgeLinkBoundary);
            yield return ("map Windows install truncates names to 110 UTF16 units and preserves collisions and archive", MapNamesAndCollisions);
            yield return ("map Windows download directory guard and failed or canceled staging preserve source", MapFolderAndStageCleanup);
        }
    }

    private static void InvalidIdBeforeIo() {
        string absent = Path.Combine(Path.GetTempPath(), "PCLLF-MissingRecycle-" + Guid.NewGuid().ToString("N"));
        int callbacks = 0;
        ResourceRecycleFiles<DerivedEntry> store = new(_ => { callbacks++; throw new Exception("Unexpected path adapter."); },
            (_, _) => { callbacks++; throw new Exception("Unexpected host comparison."); },
            () => { callbacks++; throw new Exception("Unexpected entry factory."); }, (_, _, _) => callbacks++);
        foreach (string? id in new[] { null, "", "../outside", new string('A', 32), Guid.NewGuid().ToString("D") })
            Equal("回收站记录编号无效。", Throws<IOException>(() => store.ReadRecord(absent, absent, id)).Message);
        Equal(0, callbacks);
        Equal(false, Directory.Exists(absent));
    }

    private static void MapPreCancellation() {
        using CancellationTokenSource source = new();
        source.Cancel();
        int adapters = 0;
        MapDownloadFiles map = new(_ => { adapters++; throw new Exception("Canceled install used a host adapter."); });
        OperationCanceledException error = Throws<OperationCanceledException>(() => map.Install("invalid\0archive", "invalid\0saves", source.Token));
        Equal(source.Token, error.CancellationToken);
        Equal(0, adapters);
    }

    private static void RecycleRoundTrip() {
        using Fixture fixture = new();
        string root = fixture.Directory("saves");
        string recycle = Path.Combine(fixture.Root, "trash");
        InstanceResourceTransactions transactions = new(text => text);
        List<string> boxes = new();
        foreach (string name in new[] { "World A", "World B", "World C" }) {
            string world = fixture.Directory("saves/" + name);
            fixture.Text("saves/" + name + "/level.dat", name);
            fixture.Bytes("saves/" + name + "/icon.png", [1]);
            boxes.Add(transactions.MoveToRecycle(root, Snapshot(world), recycle));
        }
        List<DerivedEntry> made = new();
        List<int> widths = new();
        string rawRoot = root + Path.DirectorySeparatorChar;
        int comparisons = 0;
        ResourceRecycleFiles<DerivedEntry> store = Store((first, second) => {
            Equal(rawRoot, first); // The caller's host comparison sees the raw root.
            Equal(root, second);
            comparisons++;
            return SameFixturePath(first, second);
        }, () => { DerivedEntry entry = new(); made.Add(entry); return entry; }, (entry, path, width) => {
            Equal(true, made.Any(candidate => ReferenceEquals(candidate, entry)));
            Equal(Path.Combine(entry.FullPath!, "icon.png"), path);
            Equal(true, File.Exists(path));
            widths.Add(width);
            entry.Icon = new object();
        });
        List<DerivedEntry> entries = store.List(rawRoot, recycle, 0);
        Equal(3, comparisons);
        Equal(3, entries.Count);
        Equal(true, widths.All(width => width == 40));
        foreach (DerivedEntry entry in entries) {
            Equal(true, made.Any(candidate => ReferenceEquals(candidate, entry)));
            Equal(true, entry.Icon is not null);
            Equal(true, entry.IsDirectory);
            Equal(0L, entry.Length);
            Equal(Path.Combine(recycle, entry.RecycleId!, "content"), entry.FullPath);
            Equal(Directory.GetCreationTimeUtc(Path.Combine(recycle, entry.RecycleId!)), entry.CreatedUtc);
            Equal(true, entry.Description!.StartsWith("删除于 ", StringComparison.Ordinal));
            Equal(true, entry.Description.Contains(" · 原位置：" + root, StringComparison.Ordinal));
        }
        string firstId = entries.Single(entry => entry.Name == "World A").RecycleId!;
        string secondId = entries.Single(entry => entry.Name == "World B").RecycleId!;
        store.Restore(rawRoot, recycle, new[] { firstId, secondId, firstId }, CancellationToken.None);
        Equal("World A", File.ReadAllText(Path.Combine(root, "World A", "level.dat")));
        Equal("World B", File.ReadAllText(Path.Combine(root, "World B", "level.dat")));
        string thirdId = entries.Single(entry => entry.Name == "World C").RecycleId!;
        store.Purge(rawRoot, recycle, new[] { thirdId, thirdId }, CancellationToken.None);
        Equal(false, Directory.Exists(Path.Combine(root, "World C")));
        Equal(0, Directory.GetDirectories(recycle).Length);

        string screenshots = fixture.Directory("screenshots");
        string screenshot = fixture.Bytes("screenshots/capture.png", [3, 4, 5]);
        string imageTrash = Path.Combine(fixture.Root, "image-trash");
        transactions.MoveToRecycle(screenshots, Snapshot(screenshot), imageTrash);
        int imageCallbacks = 0;
        ResourceRecycleFiles<DerivedEntry> images = Store(loadIcon: (entry, path, width) => {
            imageCallbacks++;
            Equal(64, width);
            Equal(entry.FullPath, path);
            Equal(true, File.Exists(path));
            entry.Icon = new object();
        });
        DerivedEntry imageEntry = images.List(screenshots, imageTrash, 1).Single();
        Equal(1, imageCallbacks);
        Equal(3L, imageEntry.Length);
        Equal(false, imageEntry.IsDirectory);
        Equal("capture.png", imageEntry.Name);
        images.Restore(screenshots, imageTrash, new[] { imageEntry.RecycleId! }, CancellationToken.None);
        Sequence(new byte[] { 3, 4, 5 }, File.ReadAllBytes(screenshot));
    }

    private static void HiddenRecords() {
        using Fixture fixture = new();
        string root = fixture.Directory("saves");
        string recycle = fixture.Directory("trash");
        string good = MakeRecord(recycle, root, "Good");
        List<string> hidden = new();
        hidden.Add(MakeRecord(recycle, root, "Broken"));
        File.WriteAllText(Path.Combine(hidden[^1], "resource.json"), "{", Utf8);
        hidden.Add(MakeRecord(recycle, root, "Oversized"));
        File.WriteAllBytes(Path.Combine(hidden[^1], "resource.json"), new byte[65537]);
        string other = fixture.Directory("other-saves");
        hidden.Add(MakeRecord(recycle, other, "Foreign"));
        hidden.Add(MakeRecord(recycle, root, "Wrong type", json => json["directory"] = "true"));
        hidden.Add(MakeRecord(recycle, root, "../escape"));
        hidden.Add(MakeRecord(recycle, root, "Outside", json => json["folder"] = fixture.Root));
        hidden.Add(MakeRecord(recycle, root, "Changed payload", json => json["directory"] = false));
        Dictionary<string, byte[]> originals = hidden.ToDictionary(box => box, box => File.ReadAllBytes(Path.Combine(box, "resource.json")));
        ResourceRecycleFiles<DerivedEntry> store = Store();
        DerivedEntry only = store.List(root, recycle, 2).Single();
        Equal("Good", only.Name);
        Equal(Path.GetFileName(good), only.RecycleId);
        Equal("回收站记录过大。", Throws<IOException>(() => store.ReadRecord(root, recycle, Path.GetFileName(hidden[1]))).Message);
        Equal("此记录不属于当前资源目录。", Throws<IOException>(() => store.ReadRecord(root, recycle, Path.GetFileName(hidden[2]))).Message);
        foreach (string box in hidden) {
            Equal(true, Directory.Exists(Path.Combine(box, "content")));
            Sequence(originals[box], File.ReadAllBytes(Path.Combine(box, "resource.json")));
            Equal("owned payload", File.ReadAllText(Path.Combine(box, "content", "level.dat")));
        }
        Equal(false, Directory.Exists(Path.Combine(fixture.Root, "escape")));
    }

    private static void RestorePreflightAndCancellation() {
        using Fixture fixture = new();
        string root = fixture.Directory("images");
        string recycle = Path.Combine(fixture.Root, "trash");
        InstanceResourceTransactions transactions = new(text => text);
        string first = fixture.Bytes("images/a.png", [1]);
        string second = fixture.Bytes("images/b.png", [2]);
        string firstBox = transactions.MoveToRecycle(root, Snapshot(first), recycle);
        string secondBox = transactions.MoveToRecycle(root, Snapshot(second), recycle);
        string[] ids = { Path.GetFileName(firstBox), Path.GetFileName(secondBox) };
        File.WriteAllBytes(second, [9]);
        ResourceRecycleFiles<DerivedEntry> store = Store();
        using CancellationTokenSource source = new();
        source.Cancel();
        Equal("原位置已有同名资源，未覆盖。请先重命名或移走它，再还原。",
            Throws<IOException>(() => store.Restore(root, recycle, ids, source.Token)).Message);
        Equal(false, File.Exists(first));
        Sequence(new byte[] { 9 }, File.ReadAllBytes(second));
        Sequence(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(firstBox, "content")));
        Sequence(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(secondBox, "content")));
        File.Delete(second);
        Throws<OperationCanceledException>(() => store.Restore(root, recycle, ids, source.Token));
        Throws<OperationCanceledException>(() => store.Purge(root, recycle, ids, source.Token));
        foreach (string box in new[] { firstBox, secondBox }) {
            Equal(true, File.Exists(Path.Combine(box, "resource.json")));
            Equal(true, File.Exists(Path.Combine(box, "content")));
        }
        Equal(false, File.Exists(first));
        Equal(false, File.Exists(second));
    }

    private static void PurgeLinkBoundary() {
        using Fixture fixture = new();
        string root = fixture.Directory("saves");
        string recycle = Path.Combine(fixture.Root, "trash");
        InstanceResourceTransactions transactions = new(text => text);
        string first = fixture.Bytes("saves/owned.bin", [1]);
        string world = fixture.Directory("saves/World");
        fixture.Text("saves/World/level.dat", "world");
        string firstBox = transactions.MoveToRecycle(root, Snapshot(first), recycle);
        string worldBox = transactions.MoveToRecycle(root, Snapshot(world), recycle);
        string outside = fixture.Bytes("outside.bin", [7, 8]);
        fixture.FileLink(Path.Combine(worldBox, "content", "linked.bin"), outside);
        ResourceRecycleFiles<DerivedEntry> store = Store();
        Throws<IOException>(() => store.Purge(root, recycle, new[] { Path.GetFileName(firstBox), Path.GetFileName(worldBox) }, CancellationToken.None));
        Sequence(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(firstBox, "content")));
        Equal(true, File.Exists(Path.Combine(firstBox, "resource.json")));
        Equal(true, Directory.Exists(Path.Combine(worldBox, "content")));
        Sequence(new byte[] { 7, 8 }, File.ReadAllBytes(outside));
        string recycleLink = fixture.DirectoryLink("trash-link", recycle);
        Equal("不操作链接形式的回收站。", Throws<IOException>(() => store.ReadRecord(root, recycleLink, Path.GetFileName(firstBox))).Message);
        Sequence(new byte[] { 7, 8 }, File.ReadAllBytes(outside));
    }

    private static void MapNamesAndCollisions() {
        using Fixture fixture = new();
        string saves = fixture.Directory("game/saves");
        MapDownloadFiles maps = new(text => text);
        string downloads = maps.GetDownloadFolder(saves);
        Equal(Path.Combine(saves, "PCLLFWorldDownload"), downloads);
        string originalStem = new string('a', 108) + "\U0001F642" + "suffix";
        string baseName = new string('a', 108) + "\U0001F642";
        Equal(110, baseName.Length);
        string archive = fixture.Zip(Path.Combine(downloads, originalStem + ".zip"), ("wrapped/world/level.dat", Utf8.GetBytes("map")), ("wrapped/world/region/data.bin", new byte[] { 3 }));
        byte[] original = File.ReadAllBytes(archive);
        string occupied = Path.Combine(saves, baseName);
        File.WriteAllBytes(occupied, [9]);
        string first = maps.Install(archive, saves, CancellationToken.None);
        Equal(Path.Combine(saves, baseName + " (2)"), first);
        Equal("map", File.ReadAllText(Path.Combine(first, "level.dat")));
        Sequence(new byte[] { 3 }, File.ReadAllBytes(Path.Combine(first, "region", "data.bin")));
        string second = maps.Install(archive, saves, CancellationToken.None);
        Equal(Path.Combine(saves, baseName + " (3)"), second);
        Sequence(new byte[] { 9 }, File.ReadAllBytes(occupied));
        Sequence(original, File.ReadAllBytes(archive));
        Equal(0, Directory.GetDirectories(Path.GetDirectoryName(saves)!, ".pcl-import-*").Length);
    }

    private static void MapFolderAndStageCleanup() {
        using Fixture fixture = new();
        string saves = fixture.Directory("game/saves");
        MapDownloadFiles maps = new(text => text);
        string downloads = maps.GetDownloadFolder(saves);
        Equal(downloads, maps.GetDownloadFolder(saves));
        File.WriteAllText(Path.Combine(downloads, "level.dat"), "existing world", Utf8);
        Equal("地图压缩包目录与现有存档同名，请先重命名该存档。", Throws<IOException>(() => maps.GetDownloadFolder(saves)).Message);
        Equal("existing world", File.ReadAllText(Path.Combine(downloads, "level.dat")));
        File.Delete(Path.Combine(downloads, "level.dat"));
        string archive = fixture.Zip(Path.Combine(downloads, "Canceled map.zip"), ("level.dat", Utf8.GetBytes("map")));
        byte[] original = File.ReadAllBytes(archive);
        using CancellationTokenSource source = new();
        bool observedStage = false;
        // The test host adapter deterministically cancels after Install's initial
        // cancellation check, at an observable owned-staging path boundary. This
        // is fault injection, not a claim about actual Meloong PathUtils behavior.
        MapDownloadFiles cancelDuringStage = new(path => {
            if (Path.GetFileName(path).StartsWith(".pcl-import-", StringComparison.Ordinal)) {
                observedStage = true;
                source.Cancel();
            }
            return path;
        });
        Throws<OperationCanceledException>(() => cancelDuringStage.Install(archive, saves, source.Token));
        Equal(true, observedStage);
        Equal(false, Directory.Exists(Path.Combine(saves, "Canceled map")));
        Sequence(original, File.ReadAllBytes(archive));
        string invalid = fixture.Zip(Path.Combine(downloads, "Not a world.zip"), ("README.txt", Utf8.GetBytes("not a world")));
        Throws<IOException>(() => maps.Install(invalid, saves, CancellationToken.None));
        Equal(true, File.Exists(invalid));
        Equal(0, Directory.GetDirectories(Path.GetDirectoryName(saves)!, ".pcl-import-*").Length);
    }

    // These delegates are fixture host stubs over owned native paths. They do
    // not replace, exercise or claim equivalence with actual Meloong PathUtils.
    private static bool SameFixturePath(string first, string second) => string.Equals(
        Path.GetFullPath(first).TrimEnd('\\', '/'), Path.GetFullPath(second).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static ResourceRecycleFiles<DerivedEntry> Store(Func<string, string, bool>? same = null,
            Func<DerivedEntry>? factory = null, Action<DerivedEntry, string, int>? loadIcon = null) =>
        new(text => text, same ?? SameFixturePath, factory ?? (() => new DerivedEntry()), loadIcon ?? ((_, _, _) => { }));

    private static string MakeRecord(string recycle, string root, string name, Action<JObject>? mutate = null) {
        string box = Path.Combine(recycle, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(box);
        Directory.CreateDirectory(Path.Combine(box, "content"));
        File.WriteAllText(Path.Combine(box, "content", "level.dat"), "owned payload", Utf8);
        JObject json = new(new JProperty("root", root), new JProperty("name", name), new JProperty("folder", root), new JProperty("directory", true));
        mutate?.Invoke(json);
        File.WriteAllText(Path.Combine(box, "resource.json"), json.ToString(), Utf8);
        return box;
    }

    private static DerivedEntry Snapshot(string path) {
        bool directory = Directory.Exists(path);
        return new DerivedEntry { Name = Path.GetFileName(path), FullPath = path, IsDirectory = directory,
            ModifiedUtc = File.GetLastWriteTimeUtc(path), CreatedUtc = File.GetCreationTimeUtc(path), Length = directory ? 0 : new FileInfo(path).Length };
    }

    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual) { if (!expected.SequenceEqual(actual)) throw new Exception("Sequences differ."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}]."); }
    private static T Throws<T>(Action run) where T : Exception {
        try { run(); } catch (Exception error) { Equal(typeof(T), error.GetType()); return (T)error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private sealed class DerivedEntry : InstanceResourceEntryData { internal object? Icon; }

    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PCLLF-RecyclePipeline-" + Guid.NewGuid().ToString("N"));
        private readonly List<(string Path, bool Directory)> links = new();
        internal Fixture() => System.IO.Directory.CreateDirectory(Root);
        internal string Directory(string relative) { string path = Owned(relative); System.IO.Directory.CreateDirectory(path); return path; }
        internal string Bytes(string relative, byte[] content) { string path = Owned(relative); System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, content); return path; }
        internal void Text(string relative, string text) => Bytes(relative, Utf8.GetBytes(text));
        internal string Zip(string path, params (string Name, byte[] Bytes)[] entries) {
            path = Owned(path);
            using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using ZipArchive archive = new(output, ZipArchiveMode.Create, false, Utf8);
            foreach ((string name, byte[] bytes) in entries) { using Stream target = archive.CreateEntry(name, CompressionLevel.NoCompression).Open(); target.Write(bytes, 0, bytes.Length); }
            return path;
        }
        internal void FileLink(string path, string target) { path = Owned(path); File.CreateSymbolicLink(path, target); links.Add((path, false)); }
        internal string DirectoryLink(string relative, string target) { string path = Owned(relative); System.IO.Directory.CreateSymbolicLink(path, target); links.Add((path, true)); return path; }
        private string Owned(string path) {
            path = Path.GetFullPath(Path.Combine(Root, path));
            if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new Exception("Unsafe recycle fixture child.");
            return path;
        }
        public void Dispose() {
            string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Root.StartsWith(temporary, StringComparison.Ordinal) || !Regex.IsMatch(Path.GetFileName(Root), @"\APCLLF-RecyclePipeline-[0-9a-f]{32}\z")) throw new Exception("Unsafe recycle fixture cleanup.");
            foreach ((string path, bool directory) in links.AsEnumerable().Reverse()) { if (directory) System.IO.Directory.Delete(path, false); else File.Delete(path); }
            foreach (string path in System.IO.Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            System.IO.Directory.Delete(Root, true);
        }
    }
}
