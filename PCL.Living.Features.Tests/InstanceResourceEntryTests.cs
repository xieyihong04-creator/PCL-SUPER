using System.Reflection;
using PCL.Living.Features;

internal static class InstanceResourceEntryTests {
    private const string Missing = "资源已被移动或删除，请刷新列表后重试。";
    private const string Stale = "资源已变化，请刷新列表后重试。";

    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("resource entry data preserves eight inherited fields and defaults", FieldShape);
        yield return ("resource entry validates actual file snapshot without replacing derived object", ValidFile);
        yield return ("resource entry original guard receives raw root and path before stat", GuardFirst);
        yield return ("resource entry null semantics do not add parameter validation", NullInputs);
        yield return ("resource entry rereads mutable path and kind after guard", GuardMutation);
        yield return ("resource entry missing file and directory retain missing error", MissingNodes);
        yield return ("resource entry detects actual file modification time change", FileTime);
        yield return ("resource entry detects length change with original mtime restored", FileLength);
        yield return ("resource entry stale mtime short-circuits file length access", TimeBeforeLength);
        yield return ("resource entry directory compares creation time only", DirectoryCreation);
        yield return ("resource entry does not introduce a content hash check", NoContentHash);
        yield return ("resource entry readonly metadata snapshot remains usable", ReadOnlyFile);
        yield return ("resource entry metadata validation does not open shared file payload", HeldFile);
        if (OperatingSystem.IsWindows()) yield return ("resource entry Windows directory content change is not stale", WindowsDirectoryContents);
    }

    private static void FieldShape() {
        foreach ((string name, Type type) in new[] {
            ("RecycleId", typeof(string)), ("Name", typeof(string)), ("FullPath", typeof(string)), ("Description", typeof(string)),
            ("IsDirectory", typeof(bool)), ("ModifiedUtc", typeof(DateTime)), ("CreatedUtc", typeof(DateTime)), ("Length", typeof(long))
        }) {
            FieldInfo field = typeof(DerivedEntry).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            Equal(type, field.FieldType);
            Equal(typeof(InstanceResourceEntryData), field.DeclaringType);
        }
        DerivedEntry blank = new();
        Equal<string?>(null, blank.RecycleId);
        Equal<string?>(null, blank.Name);
        Equal<string?>(null, blank.FullPath);
        Equal<string?>(null, blank.Description);
        Equal(false, blank.IsDirectory);
        Equal(DateTime.MinValue, blank.ModifiedUtc);
        Equal(DateTime.MinValue, blank.CreatedUtc);
        Equal(DateTimeKind.Unspecified, blank.CreatedUtc.Kind);
        Equal(0L, blank.Length);
        Equal<FieldInfo?>(null, typeof(InstanceResourceEntryData).GetField("Icon"));
    }

    private static void ValidFile() {
        using Fixture fixture = new();
        DerivedEntry entry = fixture.FileEntry("file.bin", [1, 2, 3]);
        entry.RecycleId = "existing recycle marker";
        entry.Description = "existing description";
        entry.CreatedUtc = DateTime.MinValue;
        object marker = entry.Marker;
        Validate(fixture, entry);
        Equal(true, ReferenceEquals(marker, entry.Marker));
        Equal("existing recycle marker", entry.RecycleId);
        Equal("existing description", entry.Description);
        Equal("file.bin", entry.Name);
        Equal(3L, entry.Length);
        Equal(true, new byte[] { 1, 2, 3 }.SequenceEqual(File.ReadAllBytes(entry.FullPath!)));
    }

    private static void GuardFirst() {
        using Fixture fixture = new();
        DerivedEntry entry = new() { FullPath = Path.Combine(fixture.Root, "missing.bin") };
        string rawRoot = fixture.Root + Path.DirectorySeparatorChar;
        IOException sentinel = new("original guard rejected resource");
        int calls = 0;
        Equal(sentinel, Throws<IOException>(() => InstanceResourceEntryValidator.ValidateEntry(rawRoot, entry, (root, file) => {
            calls++;
            Equal(rawRoot, root);
            Equal(entry.FullPath, file);
            throw sentinel;
        })));
        Equal(1, calls);
        Equal(false, File.Exists(entry.FullPath));
    }

    private static void NullInputs() {
        using Fixture fixture = new();
        int calls = 0;
        Throws<NullReferenceException>(() => InstanceResourceEntryValidator.ValidateEntry(fixture.Root, null!, (_, _) => calls++));
        Equal(0, calls);
        DerivedEntry entry = new();
        Throws<IOException>(() => InstanceResourceEntryValidator.ValidateEntry(fixture.Root, entry, (_, path) => {
            Equal<string?>(null, path);
            calls++;
        }), Missing);
        Equal(1, calls);
    }

    private static void GuardMutation() {
        using Fixture fixture = new();
        DerivedEntry entry = fixture.FileEntry("original.bin", [1]);
        string original = entry.FullPath!;
        DerivedEntry directory = fixture.DirectoryEntry("replacement directory");
        InstanceResourceEntryValidator.ValidateEntry(fixture.Root, entry, (_, path) => {
            Equal(original, path);
            File.Delete(original);
            entry.FullPath = directory.FullPath;
            entry.IsDirectory = true;
            entry.CreatedUtc = directory.CreatedUtc;
            entry.ModifiedUtc = DateTime.MinValue;
            entry.Length = -1;
        });
        Equal(directory.FullPath, entry.FullPath);
        Equal(true, entry.IsDirectory);
        Equal(false, File.Exists(original));
    }

    private static void MissingNodes() {
        using Fixture fixture = new();
        DerivedEntry file = fixture.FileEntry("removed.bin", [1]);
        File.Delete(file.FullPath!);
        Throws<IOException>(() => Validate(fixture, file), Missing);
        DerivedEntry directory = fixture.DirectoryEntry("removed directory");
        Directory.Delete(directory.FullPath!);
        Throws<IOException>(() => Validate(fixture, directory), Missing);
        Equal(false, Directory.Exists(directory.FullPath));
    }

    private static void FileTime() {
        using Fixture fixture = new();
        DerivedEntry entry = fixture.FileEntry("time.bin", [1, 2]);
        File.SetLastWriteTimeUtc(entry.FullPath!, entry.ModifiedUtc.AddMinutes(2));
        Throws<IOException>(() => Validate(fixture, entry), Stale);
        Equal(2L, new FileInfo(entry.FullPath!).Length);
    }

    private static void FileLength() {
        using Fixture fixture = new();
        DerivedEntry entry = fixture.FileEntry("size.bin", [1, 2]);
        File.WriteAllBytes(entry.FullPath!, [1, 2, 3]);
        File.SetLastWriteTimeUtc(entry.FullPath!, entry.ModifiedUtc);
        Equal(entry.ModifiedUtc, File.GetLastWriteTimeUtc(entry.FullPath!));
        Throws<IOException>(() => Validate(fixture, entry), Stale);
    }

    private static void TimeBeforeLength() {
        using Fixture fixture = new();
        DerivedEntry directory = fixture.DirectoryEntry("directory passed as file");
        directory.IsDirectory = false;
        directory.ModifiedUtc = DateTime.MinValue;
        // FileInfo.Length on a directory would fail; the original stale-time branch must win first.
        Throws<IOException>(() => Validate(fixture, directory), Stale);
    }

    private static void DirectoryCreation() {
        using Fixture fixture = new();
        DerivedEntry entry = fixture.DirectoryEntry("world");
        entry.ModifiedUtc = DateTime.MinValue;
        entry.Length = long.MaxValue;
        Validate(fixture, entry);
        entry.CreatedUtc = entry.CreatedUtc.AddTicks(1);
        Throws<IOException>(() => Validate(fixture, entry), Stale);
    }

    private static void NoContentHash() {
        using Fixture fixture = new();
        DerivedEntry entry = fixture.FileEntry("same-stat.bin", [1, 2, 3]);
        File.WriteAllBytes(entry.FullPath!, [9, 8, 7]);
        File.SetLastWriteTimeUtc(entry.FullPath!, entry.ModifiedUtc);
        Equal(entry.ModifiedUtc, File.GetLastWriteTimeUtc(entry.FullPath!));
        Validate(fixture, entry);
        Equal(true, new byte[] { 9, 8, 7 }.SequenceEqual(File.ReadAllBytes(entry.FullPath!)));
    }

    private static void ReadOnlyFile() {
        using Fixture fixture = new();
        DerivedEntry entry = fixture.FileEntry("readonly.bin", [1, 2, 3]);
        File.SetAttributes(entry.FullPath!, File.GetAttributes(entry.FullPath!) | FileAttributes.ReadOnly);
        try {
            Validate(fixture, entry);
            Equal(true, (File.GetAttributes(entry.FullPath!) & FileAttributes.ReadOnly) != 0);
        } finally { File.SetAttributes(entry.FullPath!, FileAttributes.Normal); }
    }

    private static void HeldFile() {
        using Fixture fixture = new();
        DerivedEntry entry = fixture.FileEntry("held.bin", [1, 2, 3]);
        using FileStream held = new(entry.FullPath!, FileMode.Open, FileAccess.Read, FileShare.None);
        Validate(fixture, entry);
        Equal(3L, held.Length);
    }

    private static void WindowsDirectoryContents() {
        using Fixture fixture = new();
        DerivedEntry entry = fixture.DirectoryEntry("world");
        File.WriteAllText(Path.Combine(entry.FullPath!, "new region file"), "world progress");
        Validate(fixture, entry);
        Equal(entry.CreatedUtc, Directory.GetCreationTimeUtc(entry.FullPath!));
    }

    private static void Validate(Fixture fixture, InstanceResourceEntryData entry) => InstanceResourceEntryValidator.ValidateEntry(fixture.Root, entry, (_, _) => { });
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
    private sealed class DerivedEntry : InstanceResourceEntryData { internal object Marker { get; } = new(); }
    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PclResourceEntry-" + Guid.NewGuid().ToString("N"));
        internal Fixture() => Directory.CreateDirectory(Root);
        internal DerivedEntry FileEntry(string name, byte[] bytes) {
            string path = Path.Combine(Root, name);
            File.WriteAllBytes(path, bytes);
            FileInfo info = new(path);
            return new DerivedEntry { Name = name, FullPath = path, IsDirectory = false, ModifiedUtc = info.LastWriteTimeUtc, CreatedUtc = info.CreationTimeUtc, Length = info.Length };
        }
        internal DerivedEntry DirectoryEntry(string name) {
            string path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return new DerivedEntry { Name = name, FullPath = path, IsDirectory = true, ModifiedUtc = Directory.GetLastWriteTimeUtc(path), CreatedUtc = Directory.GetCreationTimeUtc(path), Length = 0 };
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
