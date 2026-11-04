using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCL.Living.Features;

internal static class InstanceResourcePipelineTests {
    private const int MiB = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("resource pipeline directory icon precedes normalized metadata on the same derived entry", DirectoryPackOrder);
        yield return ("resource pipeline ZIP decoder receives owned position-zero stream and releases handles", ZipPackOwnership);
        yield return ("resource pipeline metadata byte limit accepts exactly one MiB and still attempts icons above it", MetadataLimit);
        yield return ("resource pipeline ZIP icon limit accepts exactly four MiB", ZipIconLimit);
        yield return ("resource pipeline pack filter retains five caught classes and escaping business errors", PackExceptionFilter);
        yield return ("resource pipeline ZIP decode failure disposes streams before escape", ZipFailureDisposal);
        yield return ("resource pipeline invalid and late-format metadata retain original icon and ref state", PartialDetail);
        yield return ("resource pipeline image file limit ownership and exception boundary stay distinct", ImageFileBoundary);
        yield return ("resource catalog canceled empty and missing directories remain lazy", CatalogCancellation);
        yield return ("resource catalog preserves derived snapshots filtering and directory-first order", CatalogEntries);
        yield return ("resource catalog screenshot preview budget counts eighty attempts without an image", CatalogPreviewBudget);
        yield return ("resource catalog skips per-entry IO failures but propagates decoder format failures", CatalogExceptionBoundary);
        if (OperatingSystem.IsWindows()) {
            yield return ("resource Windows transaction import rename export recycle and restore preserve bytes", TransactionRoundTrip);
            yield return ("resource Windows bulk operations validate every source before any mutation", BulkPreflight);
            yield return ("resource Windows canceled imports and exports remove only owned temporary output", CanceledTransactions);
            yield return ("resource Windows world import keeps nested layout and rejects unsafe archives", WorldImportBoundary);
            yield return ("resource Windows links and foreign staging directories retain original protections", LinkAndStageProtection);
            yield return ("resource Windows pack metadata permits a live writer and rename during decode", WindowsMetadataSharing);
        }
    }

    private static void DirectoryPackOrder() {
        using Fixture fixture = new();
        string folder = fixture.Directory("pack");
        fixture.Text("pack/pack.mcmeta", "{\"pack\":{\"description\":\"first\\r\\nsecond\",\"pack_format\":46}}");
        fixture.Bytes("pack/pack.png", [1, 2, 3]);
        DerivedEntry entry = new() { FullPath = folder, IsDirectory = true };
        string detail = "before";
        object icon = new();
        List<string> events = new();
        InstanceResourceCatalog.ReadPackInfo(entry, ref detail, (same, path, width) => {
            Equal(true, ReferenceEquals(entry, same));
            Equal("before", detail);
            Equal(Path.Combine(folder, "pack.png"), path);
            Equal(64, width);
            Equal(true, File.Exists(path));
            same.Icon = icon;
            events.Add("icon");
        }, (_, _, _) => throw new Exception("A directory must not use the ZIP decoder."), text => {
            Equal(true, ReferenceEquals(icon, entry.Icon));
            events.Add("normalize");
            return Normalize(text);
        });
        Equal("first second · 资源格式 46", detail);
        Sequence(new[] { "icon", "normalize", "normalize" }, events);
    }

    private static void ZipPackOwnership() {
        using Fixture fixture = new();
        string path = fixture.Zip("pack.zip", ("pack.mcmeta", Utf8.GetBytes("{\"pack\":{\"description\":\"zip\"}}")), ("pack.png", new byte[] { 3, 4, 5 }));
        DerivedEntry entry = new() { FullPath = path };
        string detail = "before";
        Stream? received = null;
        int normalizations = 0;
        InstanceResourceCatalog.ReadPackInfo(entry, ref detail, (_, _, _) => throw new Exception("A ZIP must not use the file-icon loader."), (same, stream, width) => {
            Equal(true, ReferenceEquals(entry, same));
            Equal("before", detail);
            Equal(64, width);
            Equal(0L, stream.Position);
            Equal(3L, stream.Length);
            Sequence(new byte[] { 3, 4, 5 }, ReadBytes(stream));
            received = stream;
            same.Icon = new object();
        }, text => {
            Equal(true, entry.Icon is not null);
            normalizations++;
            return text;
        });
        Equal("zip", detail);
        Equal(2, normalizations);
        Equal(false, received!.CanRead);
        Exclusive(path);
    }

    private static void MetadataLimit() {
        using Fixture fixture = new();
        foreach (int length in new[] { MiB, MiB + 1 }) {
            string metadata = SizedMetadata(length);
            foreach (bool directory in new[] { true, false }) {
                string name = (directory ? "folder-" : "zip-") + length;
                string path;
                if (directory) {
                    path = fixture.Directory(name);
                    fixture.Text(name + "/pack.mcmeta", metadata);
                } else path = fixture.Zip(name + ".zip", ("pack.mcmeta", Utf8.GetBytes(metadata)));
                DerivedEntry entry = new() { FullPath = path, IsDirectory = directory };
                string detail = "before";
                int icons = 0;
                InstanceResourceCatalog.ReadPackInfo(entry, ref detail, (_, _, width) => { Equal(64, width); icons++; }, (_, _, _) => throw new Exception("There is no ZIP icon."), text => text);
                Equal(length == MiB ? "inside" : "before", detail);
                Equal(directory ? 1 : 0, icons);
            }
        }
    }

    private static void ZipIconLimit() {
        using Fixture fixture = new();
        foreach (int length in new[] { 4 * MiB, 4 * MiB + 1 }) {
            string path = fixture.Zip(length + ".zip", ("pack.mcmeta", Utf8.GetBytes("{\"pack\":{\"description\":\"kept\"}}")), ("pack.png", new byte[length]));
            int decodes = 0;
            DerivedEntry entry = new() { FullPath = path };
            string detail = "before";
            InstanceResourceCatalog.ReadPackInfo(entry, ref detail, (_, _, _) => throw new Exception("Unexpected directory icon."), (_, stream, width) => {
                decodes++;
                Equal((long)length, stream.Length);
                Equal(0L, stream.Position);
                Equal(64, width);
            }, text => text);
            Equal("kept", detail);
            Equal(length == 4 * MiB ? 1 : 0, decodes);
            Exclusive(path);
        }
    }

    private static void PackExceptionFilter() {
        using Fixture fixture = new();
        string folder = fixture.Directory("pack");
        fixture.Text("pack/pack.mcmeta", "{\"pack\":{\"description\":\"new\"}}");
        DerivedEntry entry = new() { FullPath = folder, IsDirectory = true };
        foreach (Exception error in new Exception[] { new IOException(), new UnauthorizedAccessException(), new JsonReaderException(), new NotSupportedException(), new ArgumentException() }) {
            string detail = "before";
            int normalizations = 0;
            InstanceResourceCatalog.ReadPackInfo(entry, ref detail, (_, _, _) => throw error, (_, _, _) => { }, text => { normalizations++; return text; });
            Equal("before", detail);
            Equal(0, normalizations);
        }
        foreach (Exception error in new Exception[] { new FormatException(), new InvalidDataException(), new OperationCanceledException(), new InvalidOperationException() }) {
            string detail = "before";
            SameException(error, () => InstanceResourceCatalog.ReadPackInfo(entry, ref detail, (_, _, _) => throw error, (_, _, _) => { }, text => text));
            Equal("before", detail);
        }
    }

    private static void ZipFailureDisposal() {
        using Fixture fixture = new();
        string path = fixture.Zip("pack.zip", ("pack.mcmeta", Utf8.GetBytes("{\"pack\":{\"description\":\"new\"}}")), ("pack.png", new byte[] { 1 }));
        foreach (Exception error in new Exception[] { new FormatException(), new InvalidDataException(), new OperationCanceledException() }) {
            DerivedEntry entry = new() { FullPath = path };
            Stream? received = null;
            string detail = "before";
            SameException(error, () => InstanceResourceCatalog.ReadPackInfo(entry, ref detail, (_, _, _) => { }, (_, stream, _) => { received = stream; throw error; }, text => text));
            Equal("before", detail);
            Equal(false, received!.CanRead);
            Exclusive(path);
        }
    }

    private static void PartialDetail() {
        using Fixture fixture = new();
        string folder = fixture.Directory("pack");
        DerivedEntry entry = new() { FullPath = folder, IsDirectory = true };
        object icon = new();
        fixture.Text("pack/pack.mcmeta", "{");
        string detail = "before";
        InstanceResourceCatalog.ReadPackInfo(entry, ref detail, (same, _, _) => same.Icon = icon, (_, _, _) => { }, text => text);
        Equal("before", detail);
        Equal(true, ReferenceEquals(icon, entry.Icon));
        fixture.Text("pack/pack.mcmeta", "{\"pack\":{\"description\":\"changed\",\"pack_format\":\"1000-01-01T00:00:00\"}}");
        CultureInfo previous = CultureInfo.CurrentCulture;
        try {
            CultureInfo culture = (CultureInfo)CultureInfo.GetCultureInfo("ar-SA").Clone();
            culture.DateTimeFormat.Calendar = new UmAlQuraCalendar();
            CultureInfo.CurrentCulture = culture;
            InstanceResourceCatalog.ReadPackInfo(entry, ref detail, (same, _, _) => same.Icon = icon, (_, _, _) => { }, text => text);
            Equal("changed", detail);
            Equal(true, ReferenceEquals(icon, entry.Icon));
        } finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void ImageFileBoundary() {
        using Fixture fixture = new();
        string path = fixture.Sparse("image.bin", 64L * MiB);
        Stream? received = null;
        object image = new();
        Equal(image, InstanceResourceCatalog.LoadImage(path, 17, (stream, width) => {
            received = stream;
            Equal(64L * MiB, stream.Length);
            Equal(0L, stream.Position);
            Equal(17, width);
            return image;
        }));
        Equal(false, received!.CanRead);
        fixture.Sparse("image.bin", 64L * MiB + 1);
        int calls = 0;
        Equal<object?>(null, InstanceResourceCatalog.LoadImage(path, 64, (_, _) => { calls++; return image; }));
        Equal(0, calls);
        Equal<object?>(null, InstanceResourceCatalog.LoadImage<object>(null, 64, (_, _) => throw new Exception("Missing image decoded.")));
        fixture.Sparse("image.bin", 1);
        Equal<object?>(null, InstanceResourceCatalog.LoadImage<object>(path, 64, (_, _) => throw new FormatException()));
        Equal<object?>(null, InstanceResourceCatalog.LoadImage<object>(path, 64, (_, _) => throw new IOException()));
        OperationCanceledException canceled = new();
        SameException(canceled, () => InstanceResourceCatalog.LoadImage<object>(path, 64, (_, _) => throw canceled));
        JsonReaderException json = new();
        SameException(json, () => InstanceResourceCatalog.LoadImage<object>(path, 64, (_, _) => throw json));
        Exclusive(path);
    }

    private static void CatalogCancellation() {
        using Fixture fixture = new();
        using CancellationTokenSource source = new();
        source.Cancel();
        int factories = 0;
        List<DerivedEntry> ScanAt(string folder) => InstanceResourceCatalog.Scan(folder, folder, 1, source.Token, "downloads", () => { factories++; return new DerivedEntry(); }, (_, _, _) => { }, (_, _, _) => { }, Normalize, text => text);
        Equal(0, ScanAt(fixture.Root).Count);
        Equal(0, ScanAt(Path.Combine(fixture.Root, "missing")).Count);
        fixture.Bytes(".pcl-hidden.png", [1]);
        Throws<OperationCanceledException>(() => ScanAt(fixture.Root));
        Equal(0, factories);
    }

    private static void CatalogEntries() {
        using Fixture fixture = new();
        fixture.Directory("b-folder");
        fixture.Directory("A-folder");
        fixture.Directory(".PCL-hidden");
        fixture.Bytes("B.schem", [1, 2]);
        fixture.Bytes("a.schem", [1]);
        fixture.Bytes("not.zip", [1]);
        fixture.Bytes(".pcl-stage.schem", [1]);
        List<DerivedEntry> made = new();
        int callbacks = 0;
        List<DerivedEntry> entries = InstanceResourceCatalog.Scan(fixture.Root, fixture.Root, 4, CancellationToken.None, "downloads", () => { DerivedEntry value = new(); made.Add(value); return value; }, (_, _, _) => callbacks++, (_, _, _) => callbacks++, Normalize, text => text);
        Sequence(new[] { "A-folder", "b-folder", "a.schem", "B.schem" }, entries.Select(entry => entry.Name!));
        Equal(0, callbacks);
        Equal(4, made.Count);
        foreach (DerivedEntry entry in entries) {
            Equal(true, made.Any(candidate => ReferenceEquals(candidate, entry)));
            Equal(Path.Combine(fixture.Root, entry.Name!), entry.FullPath);
            Equal(File.GetLastWriteTimeUtc(entry.FullPath!), entry.ModifiedUtc);
            Equal(File.GetCreationTimeUtc(entry.FullPath!), entry.CreatedUtc);
            Equal(entry.IsDirectory ? 0 : new FileInfo(entry.FullPath!).Length, entry.Length);
            Equal(true, entry.Description!.EndsWith(File.GetLastWriteTime(entry.FullPath!).ToString("yyyy-MM-dd HH:mm"), StringComparison.Ordinal));
        }
        string worlds = fixture.Directory("world-catalog");
        fixture.Directory("world-catalog/downloads");
        fixture.Directory("world-catalog/incomplete");
        fixture.Directory("world-catalog/complete");
        fixture.Bytes("world-catalog/complete/level.dat", [1]);
        int icons = 0;
        List<DerivedEntry> worldEntries = Scan(worlds, 0, (_, _, width) => { Equal(64, width); icons++; });
        Equal(2, worldEntries.Count);
        Equal(2, icons);
        Equal(false, worldEntries.Any(entry => entry.Name == "downloads"));
        Equal(true, worldEntries.Single(entry => entry.Name == "complete").Description!.StartsWith("Minecraft 存档 · ", StringComparison.Ordinal));
        Equal(true, worldEntries.Single(entry => entry.Name == "incomplete").Description!.StartsWith("未找到 level.dat，存档可能不完整 · ", StringComparison.Ordinal));
    }

    private static void CatalogPreviewBudget() {
        using Fixture fixture = new();
        for (int index = 0; index < 82; index++) fixture.Bytes(index.ToString("D2") + ".png", [1]);
        int attempts = 0;
        List<DerivedEntry> entries = Scan(fixture.Root, 1, (_, _, _) => attempts++);
        Equal(82, entries.Count);
        Equal(80, attempts);
        Equal(true, entries.All(entry => entry.Icon is null));
        attempts = 0;
        entries = Scan(fixture.Root, 1, (_, _, _) => { if (++attempts == 1) throw new IOException("Image moved during first attempt."); });
        Equal(81, entries.Count);
        Equal(81, attempts); // One failed callback does not consume an attempted-preview slot.
    }

    private static void CatalogExceptionBoundary() {
        using Fixture fixture = new();
        fixture.Bytes("a.png", [1]);
        fixture.Bytes("b.png", [2]);
        int calls = 0;
        List<DerivedEntry> entries = Scan(fixture.Root, 1, (_, _, _) => { if (++calls == 1) throw new UnauthorizedAccessException(); });
        Equal(1, entries.Count);
        Equal(2, calls);
        FormatException error = new("Original decoder format failure.");
        SameException(error, () => Scan(fixture.Root, 1, (_, _, _) => throw error));
    }

    private static void TransactionRoundTrip() {
        using Fixture fixture = new();
        string root = fixture.Directory("resources");
        string source = fixture.Zip("Native Pack.zip", ("pack.mcmeta", Utf8.GetBytes("{}")));
        byte[] bytes = File.ReadAllBytes(source);
        InstanceResourceTransactions transactions = new(text => text);
        string imported = transactions.ImportFile(root, root, 2, source, CancellationToken.None);
        Sequence(bytes, File.ReadAllBytes(imported));
        Throws<IOException>(() => transactions.ImportFile(root, root, 2, source, CancellationToken.None));
        DerivedEntry entry = Snapshot(imported);
        string renamed = transactions.Rename(root, entry, "Renamed.zip");
        Equal(imported, entry.FullPath); // The old operation returns a new path without mutating the DTO.
        Equal(false, File.Exists(imported));
        Sequence(bytes, File.ReadAllBytes(renamed));
        string occupied = fixture.Bytes("resources/occupied.zip", [8]);
        Throws<IOException>(() => transactions.Rename(root, Snapshot(renamed), "occupied.zip"));
        Sequence(new byte[] { 8 }, File.ReadAllBytes(occupied));
        string exported = fixture.Bytes("export.zip", [9]);
        transactions.Export(root, Snapshot(renamed), exported, CancellationToken.None);
        Sequence(bytes, File.ReadAllBytes(exported));
        string recycle = Path.Combine(fixture.Root, "recycle");
        string box = transactions.MoveToRecycle(root, Snapshot(renamed), recycle);
        Equal(false, File.Exists(renamed));
        Sequence(bytes, File.ReadAllBytes(Path.Combine(box, "content")));
        JObject metadata = JObject.Parse(File.ReadAllText(Path.Combine(box, "resource.json")));
        Equal("Renamed.zip", (string?)metadata["name"]);
        Equal(false, (bool)metadata["directory"]!);
        Equal(renamed, transactions.RestoreLast(root, recycle));
        Sequence(bytes, File.ReadAllBytes(renamed));
        Equal(false, Directory.Exists(box));
        Sequence(bytes, File.ReadAllBytes(source));
        Equal(0, Directory.GetFiles(root, "*.pcl-copy-*").Length);
    }

    private static void BulkPreflight() {
        using Fixture fixture = new();
        string root = fixture.Directory("resources");
        string first = fixture.Bytes("resources/a.zip", [1]);
        string second = fixture.Bytes("resources/b.zip", [2]);
        DerivedEntry[] entries = { Snapshot(first), Snapshot(second) };
        File.Delete(second);
        string exports = fixture.Directory("exports");
        string recycle = Path.Combine(fixture.Root, "recycle");
        using CancellationTokenSource source = new();
        source.Cancel();
        Throws<IOException>(() => new InstanceResourceTransactions(text => text).ExportMany(root, entries, exports, source.Token));
        Equal(0, Directory.GetFileSystemEntries(exports).Length);
        Throws<IOException>(() => new InstanceResourceTransactions(text => text).RecycleMany(root, entries, recycle, source.Token));
        Equal(false, Directory.Exists(recycle));
        Sequence(new byte[] { 1 }, File.ReadAllBytes(first));
    }

    private static void CanceledTransactions() {
        using Fixture fixture = new();
        string root = fixture.Directory("resources");
        string world = fixture.Directory("source-world");
        fixture.Bytes("source-world/level.dat", [1]);
        string pack = fixture.Zip("pack.zip", ("pack.mcmeta", Utf8.GetBytes("{}")));
        string worldZip = fixture.Zip("world.zip", ("nested/level.dat", new byte[] { 1 }));
        string exportWorld = fixture.Directory("resources/export-world");
        fixture.Bytes("resources/export-world/level.dat", [1]);
        string exportFolder = fixture.Directory("exports");
        string foreign = fixture.Directory("resources/.pcl-import-not-owned");
        InstanceResourceTransactions transactions = new(text => text);
        using CancellationTokenSource source = new();
        source.Cancel();
        Throws<OperationCanceledException>(() => transactions.ImportFile(root, root, 2, pack, source.Token));
        Throws<OperationCanceledException>(() => transactions.ImportFolder(root, root, 0, world, "World", source.Token));
        Throws<OperationCanceledException>(() => transactions.ImportWorld(root, worldZip, "Archive world", source.Token));
        Throws<OperationCanceledException>(() => transactions.Export(root, Snapshot(exportWorld), Path.Combine(exportFolder, "world.zip"), source.Token));
        Equal(false, File.Exists(Path.Combine(root, "pack.zip")));
        Equal(false, Directory.Exists(Path.Combine(root, "World")));
        Equal(false, Directory.Exists(Path.Combine(root, "Archive world")));
        Equal(0, Directory.GetFiles(root, "*.pcl-copy-*").Length);
        Equal(0, Directory.GetFiles(exportFolder, "*.pcl-export-*").Length);
        Equal(false, Directory.GetDirectories(root).Any(path => Regex.IsMatch(Path.GetFileName(path), @"\A\.pcl-import-[0-9a-f]{32}\z")));
        Equal(true, Directory.Exists(foreign));
    }

    private static void WorldImportBoundary() {
        using Fixture fixture = new();
        string root = fixture.Directory("resources");
        InstanceResourceTransactions transactions = new(text => text);
        string valid = fixture.Zip("world.zip", ("nested/world/level.dat", new byte[] { 1, 2 }), ("nested/world/region/r.0.0.mca", new byte[] { 3 }));
        string imported = transactions.ImportWorld(root, valid, "World", CancellationToken.None);
        Sequence(new byte[] { 1, 2 }, File.ReadAllBytes(Path.Combine(imported, "level.dat")));
        Sequence(new byte[] { 3 }, File.ReadAllBytes(Path.Combine(imported, "region", "r.0.0.mca")));
        int index = 0;
        foreach (string unsafeName in new[] { "../escape.txt", "/absolute/level.dat", "C:/drive/level.dat", "folder/../level.dat", "folder:bad/level.dat" }) {
            string archive = fixture.Zip("bad-" + index + ".zip", (unsafeName, new byte[] { 7 }));
            string name = "Rejected " + index++;
            Throws<IOException>(() => transactions.ImportWorld(root, archive, name, CancellationToken.None));
            Equal(false, Directory.Exists(Path.Combine(root, name)));
        }
        string linked = fixture.Zip("symlink.zip", ("link/level.dat", new byte[] { 4 }));
        using (ZipArchive archive = ZipFile.Open(linked, ZipArchiveMode.Update)) archive.Entries[0].ExternalAttributes = unchecked((int)0xA0000000);
        Throws<IOException>(() => transactions.ImportWorld(root, linked, "Rejected link", CancellationToken.None));
        Equal(false, File.Exists(Path.Combine(fixture.Root, "escape.txt")));
        Equal(0, Directory.GetDirectories(root, ".pcl-import-*").Length);
    }

    private static void LinkAndStageProtection() {
        using Fixture fixture = new();
        string root = fixture.Directory("resources");
        string target = fixture.Bytes("outside.zip", [4, 5]);
        string link = fixture.FileLink("resources/link.zip", target);
        InstanceResourceTransactions transactions = new(text => text);
        Throws<IOException>(() => transactions.Rename(root, Snapshot(link), "renamed.zip"));
        Sequence(new byte[] { 4, 5 }, File.ReadAllBytes(target));
        Equal(true, (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        string folder = fixture.Directory("resources/world");
        fixture.Bytes("resources/world/level.dat", [1]);
        fixture.FileLink("resources/world/linked.bin", target);
        string exports = fixture.Directory("exports");
        Throws<IOException>(() => transactions.Export(root, Snapshot(folder), Path.Combine(exports, "world.zip"), CancellationToken.None));
        Equal(0, Directory.GetFiles(exports, "*.pcl-export-*").Length);
        string foreign = fixture.Directory("resources/.pcl-import-not-owned");
        Throws<IOException>(() => transactions.DeleteStage(root, foreign));
        Equal(true, Directory.Exists(foreign));
        Sequence(new byte[] { 4, 5 }, File.ReadAllBytes(target));
    }

    private static void WindowsMetadataSharing() {
        using Fixture fixture = new();
        string path = fixture.Zip("pack.zip", ("pack.mcmeta", Utf8.GetBytes("{\"pack\":{\"description\":\"shared\"}}")), ("pack.png", new byte[] { 1 }));
        string moved = Path.Combine(fixture.Root, "moved.zip");
        DerivedEntry entry = new() { FullPath = path };
        string detail = "before";
        using (FileStream writer = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) {
            InstanceResourceCatalog.ReadPackInfo(entry, ref detail, (_, _, _) => { }, (_, stream, _) => {
                Equal(0L, stream.Position);
                File.Move(path, moved);
            }, text => text);
        }
        Equal("shared", detail);
        Equal(false, File.Exists(path));
        Equal(true, File.Exists(moved));
        Exclusive(moved);
    }

    private static List<DerivedEntry> Scan(string folder, int kind, Action<DerivedEntry, string, int> load) =>
        InstanceResourceCatalog.Scan(folder, folder, kind, CancellationToken.None, "downloads", () => new DerivedEntry(), load, (_, _, _) => { }, Normalize, text => text);

    private static DerivedEntry Snapshot(string path) {
        bool directory = Directory.Exists(path);
        return new DerivedEntry { Name = Path.GetFileName(path), FullPath = path, IsDirectory = directory,
            ModifiedUtc = File.GetLastWriteTimeUtc(path), CreatedUtc = File.GetCreationTimeUtc(path), Length = directory ? 0 : new FileInfo(path).Length };
    }

    private static string SizedMetadata(int bytes) {
        const string json = "{\"pack\":{\"description\":\"inside\"}}";
        return json + new string(' ', bytes - Utf8.GetByteCount(json));
    }

    private static string Normalize(string text) => text.Replace("\r\n", " ").Replace("\n", " ");
    private static byte[] ReadBytes(Stream stream) { using MemoryStream result = new(); stream.CopyTo(result); return result.ToArray(); }
    private static void Exclusive(string path) { using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { } }
    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual) { if (!expected.SequenceEqual(actual)) throw new Exception("Sequences differ."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}]."); }
    private static T Throws<T>(Action run) where T : Exception {
        try { run(); } catch (Exception error) { Equal(typeof(T), error.GetType()); return (T)error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void SameException(Exception expected, Action run) { try { run(); } catch (Exception error) { Equal(true, ReferenceEquals(expected, error)); return; } throw new Exception("Expected callback exception."); }

    private sealed class DerivedEntry : InstanceResourceEntryData { internal object Marker { get; } = new(); internal object? Icon; }

    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PCLLF-ResourcePipeline-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> links = new();
        internal Fixture() => System.IO.Directory.CreateDirectory(Root);
        internal string Directory(string relative) { string path = OwnedPath(relative); System.IO.Directory.CreateDirectory(path); return path; }
        internal string Bytes(string relative, byte[] content) { string path = OwnedPath(relative); System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, content); return path; }
        internal string Text(string relative, string content) => Bytes(relative, Utf8.GetBytes(content));
        internal string Sparse(string relative, long length) { string path = OwnedPath(relative); using FileStream output = new(path, FileMode.Create, FileAccess.Write, FileShare.None); output.SetLength(length); return path; }
        internal string Zip(string relative, params (string Name, byte[] Bytes)[] entries) {
            string path = OwnedPath(relative);
            using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using ZipArchive archive = new(output, ZipArchiveMode.Create, false, Utf8);
            foreach ((string name, byte[] bytes) in entries) { using Stream payload = archive.CreateEntry(name, CompressionLevel.NoCompression).Open(); payload.Write(bytes, 0, bytes.Length); }
            return path;
        }
        internal string FileLink(string relative, string target) { string path = OwnedPath(relative); File.CreateSymbolicLink(path, target); links.Add(path); return path; }
        private string OwnedPath(string relative) {
            string path = Path.GetFullPath(Path.Combine(Root, relative));
            if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new Exception("Unsafe fixture child.");
            return path;
        }
        public void Dispose() {
            string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Root.StartsWith(temporary, StringComparison.Ordinal) || !Regex.IsMatch(Path.GetFileName(Root), @"\APCLLF-ResourcePipeline-[0-9a-f]{32}\z")) throw new Exception("Unsafe resource pipeline cleanup.");
            foreach (string link in links) File.Delete(link); // Unlink before recursive cleanup can inspect the owned target.
            foreach (string path in System.IO.Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            System.IO.Directory.Delete(Root, true);
        }
    }
}
