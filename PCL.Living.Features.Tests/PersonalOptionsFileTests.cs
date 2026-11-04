using System.Text;
using PCL.Living.Features;

internal static class PersonalOptionsFileTests {
    private const string ReadError = "不读取过大或链接形式的 options.txt。";
    private const string ChangedError = "游戏设置已被其他程序修改，本次同步已停止。";

    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("personal option file missing read leaves first-run folder alone", MissingRead);
        yield return ("personal option file read keeps raw bytes and closes handles", RawRead);
        yield return ("personal option file read accepts read-only files", ReadOnlyRead);
        yield return ("personal option file read enforces exact 4 MiB boundary", ReadLimit);
        yield return ("personal option file unchanged text preserves existing bytes and backup", NoOpExisting);
        yield return ("personal option file unchanged text ignores missing path and byte input", NoOpMissing);
        yield return ("personal option file unchanged decoded text leaves repeated BOMs alone", NoOpBom);
        yield return ("personal option file text comparison keeps binary case differences", CaseChange);
        yield return ("personal option file writes Unicode and exact merged line endings", ExactWrite);
        yield return ("personal option file writes preserve original UTF-8 BOM convention", BomWrites);
        yield return ("personal option file stale snapshot rejects external edit without cache clear", ExternalEdit);
        yield return ("personal option file deleted destination is not recreated by stale snapshot", DeletedTarget);
        yield return ("personal option file replace failure skips cache and removes staging", WriteFailure);
        yield return ("personal option file cache callback observes committed and unlocked files", CacheAfterCommit);
        yield return ("personal option file cache callback failure propagates after commit", CacheFailure);
        if (CanCreateSymbolicLinks()) {
            yield return ("personal option file read rejects linked options", LinkedRead);
            yield return ("personal option file apply rejects destination replaced by link", LinkedWrite);
            yield return ("personal option file apply protects linked backup", LinkedBackup);
        }
        if (OperatingSystem.IsWindows()) {
            yield return ("personal option file read keeps Windows sharing behavior", WindowsReadSharing);
            yield return ("personal option file failed Windows read-only write skips cache", WindowsReadOnlyWrite);
        }
    }

    private static void MissingRead() {
        using Fixture fixture = new();
        string file = fixture.Path("first-run", "options.txt");
        Equal<byte[]?>(null, PersonalOptionsFiles.Read(file));
        Equal(false, Directory.Exists(fixture.Path("first-run")));
        Equal<byte[]?>(null, PersonalOptionsFiles.Read(fixture.Root));
        Equal<byte[]?>(null, PersonalOptionsFiles.Read(null!));
        Equal(0, Directory.GetFileSystemEntries(fixture.Root).Length);
    }

    private static void RawRead() {
        using Fixture fixture = new();
        byte[] original = [239, 187, 191, 0, 255, 192, 13, 10, 0, 127];
        File.WriteAllBytes(fixture.File, original);
        byte[] read = PersonalOptionsFiles.Read(fixture.File)!;
        Bytes(original, read);
        read[0] = 0;
        Bytes(original, PersonalOptionsFiles.Read(fixture.File)!);
        using FileStream exclusive = new(fixture.File, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Equal((long) original.Length, exclusive.Length);
        Equal(false, File.Exists(fixture.Backup));
        NoStaging(fixture);
    }

    private static void ReadOnlyRead() {
        using Fixture fixture = new();
        byte[] original = Encoding.UTF8.GetBytes("key_key.forward:17\r\n");
        File.WriteAllBytes(fixture.File, original);
        File.SetAttributes(fixture.File, File.GetAttributes(fixture.File) | FileAttributes.ReadOnly);
        try {
            Bytes(original, PersonalOptionsFiles.Read(fixture.File)!);
            Equal(true, (File.GetAttributes(fixture.File) & FileAttributes.ReadOnly) != 0);
        } finally {
            File.SetAttributes(fixture.File, FileAttributes.Normal);
        }
    }

    private static void ReadLimit() {
        using Fixture fixture = new();
        const int limit = 4 * 1024 * 1024;
        byte[] original = new byte[limit];
        original[0] = 255;
        original[^1] = 127;
        File.WriteAllBytes(fixture.File, original);
        Bytes(original, PersonalOptionsFiles.Read(fixture.File)!);
        using (FileStream output = new(fixture.File, FileMode.Append)) output.WriteByte(0);
        Throws<IOException>(() => PersonalOptionsFiles.Read(fixture.File), ReadError);
        Equal((long) limit + 1, new FileInfo(fixture.File).Length);
        NoStaging(fixture);
    }

    private static void NoOpExisting() {
        using Fixture fixture = new();
        byte[] actual = [255, 0, 13, 10];
        File.WriteAllBytes(fixture.File, actual);
        File.WriteAllText(fixture.Backup, "older backup");
        File.SetLastWriteTimeUtc(fixture.File, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        DateTime before = File.GetLastWriteTimeUtc(fixture.File);
        Equal(false, PersonalOptionsFiles.ApplyMerged(fixture.File, [1, 2], "same text", "same text", _ => throw new InvalidOperationException("No-op must not clear cache.")));
        Bytes(actual, File.ReadAllBytes(fixture.File));
        Equal("older backup", File.ReadAllText(fixture.Backup));
        Equal(before, File.GetLastWriteTimeUtc(fixture.File));
        NoStaging(fixture);
    }

    private static void NoOpMissing() {
        using Fixture fixture = new();
        string missing = fixture.Path("no-folder", "options.txt");
        Equal(false, PersonalOptionsFiles.ApplyMerged(missing, null!, "unchanged", "unchanged", _ => throw new InvalidOperationException("No-op must not clear cache.")));
        Equal(false, Directory.Exists(fixture.Path("no-folder")));
        Equal(0, Directory.GetFileSystemEntries(fixture.Root).Length);
    }

    private static void NoOpBom() {
        using Fixture fixture = new();
        byte[] original = [239, 187, 191, 239, 187, 191, .. Encoding.UTF8.GetBytes("lang:zh_cn\r\n")];
        File.WriteAllBytes(fixture.File, original);
        string text = PersonalOptionsRules.Decode(original);
        Equal(false, PersonalOptionsFiles.ApplyMerged(fixture.File, original, text, text, _ => throw new InvalidOperationException("No-op must not clear cache.")));
        Bytes(original, File.ReadAllBytes(fixture.File));
        Equal(false, File.Exists(fixture.Backup));
    }

    private static void CaseChange() {
        using Fixture fixture = new();
        byte[] original = Encoding.UTF8.GetBytes("fov:70");
        File.WriteAllBytes(fixture.File, original);
        int calls = 0;
        Equal(true, PersonalOptionsFiles.ApplyMerged(fixture.File, original, "fov:70", "FOV:70", _ => calls++));
        Equal(1, calls);
        Equal("FOV:70", File.ReadAllText(fixture.File));
        Bytes(original, File.ReadAllBytes(fixture.Backup));
    }

    private static void ExactWrite() {
        using Fixture fixture = new();
        byte[] original = Encoding.UTF8.GetBytes("# author\r\nname:原世界\n\n");
        const string merged = "# author\r\nname:中文 Ω 😀\nlast:raw\rvalue\n\n";
        File.WriteAllBytes(fixture.File, original);
        int calls = 0;
        Equal(true, PersonalOptionsFiles.ApplyMerged(fixture.File, original, PersonalOptionsRules.Decode(original), merged, path => {
            Equal(fixture.File, path);
            calls++;
        }));
        Equal(1, calls);
        Bytes(Encoding.UTF8.GetBytes(merged), File.ReadAllBytes(fixture.File));
        Bytes(original, File.ReadAllBytes(fixture.Backup));
        NoStaging(fixture);
    }

    private static void BomWrites() {
        foreach (int bomCount in new[] { 0, 1, 2 }) {
            using Fixture fixture = new();
            byte[] prefix = Enumerable.Repeat(new byte[] { 239, 187, 191 }, bomCount).SelectMany(bytes => bytes).ToArray();
            byte[] original = [.. prefix, .. Encoding.UTF8.GetBytes("fov:70\r\n")];
            byte[] before = (byte[]) original.Clone();
            File.WriteAllBytes(fixture.File, original);
            Equal(true, PersonalOptionsFiles.ApplyMerged(fixture.File, original, PersonalOptionsRules.Decode(original), "fov:90\r\n", _ => { }));
            byte[] expected = [.. bomCount == 0 ? Array.Empty<byte>() : new byte[] { 239, 187, 191 }, .. Encoding.UTF8.GetBytes("fov:90\r\n")];
            Bytes(expected, File.ReadAllBytes(fixture.File));
            Bytes(before, File.ReadAllBytes(fixture.Backup));
            Bytes(before, original);
            NoStaging(fixture);
        }
    }

    private static void ExternalEdit() {
        using Fixture fixture = new();
        byte[] snapshot = Encoding.UTF8.GetBytes("fov:70\n");
        File.WriteAllText(fixture.File, "external edit");
        File.WriteAllText(fixture.Backup, "earlier backup");
        int calls = 0;
        Throws<IOException>(() => PersonalOptionsFiles.ApplyMerged(fixture.File, snapshot, "fov:70\n", "fov:90\n", _ => calls++), ChangedError);
        Equal(0, calls);
        Equal("external edit", File.ReadAllText(fixture.File));
        Equal("earlier backup", File.ReadAllText(fixture.Backup));
        NoStaging(fixture);
    }

    private static void DeletedTarget() {
        using Fixture fixture = new();
        byte[] snapshot = Encoding.UTF8.GetBytes("fov:70\n");
        File.WriteAllBytes(fixture.File, snapshot);
        File.Delete(fixture.File);
        int calls = 0;
        Throws<IOException>(() => PersonalOptionsFiles.ApplyMerged(fixture.File, snapshot, "fov:70\n", "fov:90\n", _ => calls++), ChangedError);
        Equal(0, calls);
        Equal(false, File.Exists(fixture.File));
        Equal(false, File.Exists(fixture.Backup));
        NoStaging(fixture);
    }

    private static void WriteFailure() {
        using Fixture fixture = new();
        byte[] snapshot = Encoding.UTF8.GetBytes("fov:70\n");
        File.WriteAllBytes(fixture.File, snapshot);
        Directory.CreateDirectory(fixture.Backup);
        File.WriteAllText(System.IO.Path.Combine(fixture.Backup, "keep"), "backup folder");
        int calls = 0;
        FileOperationError(() => PersonalOptionsFiles.ApplyMerged(fixture.File, snapshot, "fov:70\n", "fov:90\n", _ => calls++));
        Equal(0, calls);
        Bytes(snapshot, File.ReadAllBytes(fixture.File));
        Equal("backup folder", File.ReadAllText(System.IO.Path.Combine(fixture.Backup, "keep")));
        NoStaging(fixture);
    }

    private static void CacheAfterCommit() {
        using Fixture fixture = new();
        byte[] snapshot = Encoding.UTF8.GetBytes("fov:70\n");
        File.WriteAllBytes(fixture.File, snapshot);
        int calls = 0;
        Equal(true, PersonalOptionsFiles.ApplyMerged(fixture.File, snapshot, "fov:70\n", "fov:90\n", path => {
            calls++;
            Equal(fixture.File, path);
            NoStaging(fixture);
            using FileStream destination = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using FileStream backup = new(fixture.Backup, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using StreamReader destinationReader = new(destination, Encoding.UTF8);
            using StreamReader backupReader = new(backup, Encoding.UTF8);
            Equal("fov:90\n", destinationReader.ReadToEnd());
            Equal("fov:70\n", backupReader.ReadToEnd());
        }));
        Equal(1, calls);
    }

    private static void CacheFailure() {
        using Fixture fixture = new();
        byte[] snapshot = Encoding.UTF8.GetBytes("fov:70\n");
        File.WriteAllBytes(fixture.File, snapshot);
        InvalidOperationException sentinel = new("cache invalidation failed");
        Equal(sentinel, Throws<InvalidOperationException>(() => PersonalOptionsFiles.ApplyMerged(fixture.File, snapshot, "fov:70\n", "fov:90\n", _ => throw sentinel)));
        Equal("fov:90\n", File.ReadAllText(fixture.File));
        Bytes(snapshot, File.ReadAllBytes(fixture.Backup));
        NoStaging(fixture);
    }

    private static void LinkedRead() {
        using Fixture fixture = new();
        string target = fixture.Path("target.txt");
        File.WriteAllText(target, "target bytes");
        File.CreateSymbolicLink(fixture.File, target);
        Throws<IOException>(() => PersonalOptionsFiles.Read(fixture.File), ReadError);
        Equal("target bytes", File.ReadAllText(target));
        Equal(false, File.Exists(fixture.Backup));
        NoStaging(fixture);
    }

    private static void LinkedWrite() {
        using Fixture fixture = new();
        byte[] snapshot = Encoding.UTF8.GetBytes("fov:70\n");
        File.WriteAllBytes(fixture.File, snapshot);
        string target = fixture.Path("moved.txt");
        File.Move(fixture.File, target);
        File.CreateSymbolicLink(fixture.File, target);
        int calls = 0;
        Throws<IOException>(() => PersonalOptionsFiles.ApplyMerged(fixture.File, snapshot, "fov:70\n", "fov:90\n", _ => calls++), "不修改链接形式的游戏设置文件。");
        Equal(0, calls);
        Bytes(snapshot, File.ReadAllBytes(target));
        Equal(true, (File.GetAttributes(fixture.File) & FileAttributes.ReparsePoint) != 0);
        NoStaging(fixture);
    }

    private static void LinkedBackup() {
        using Fixture fixture = new();
        byte[] snapshot = Encoding.UTF8.GetBytes("fov:70\n");
        File.WriteAllBytes(fixture.File, snapshot);
        string target = fixture.Path("backup-target.txt");
        File.WriteAllText(target, "earlier backup");
        File.CreateSymbolicLink(fixture.Backup, target);
        int calls = 0;
        Throws<IOException>(() => PersonalOptionsFiles.ApplyMerged(fixture.File, snapshot, "fov:70\n", "fov:90\n", _ => calls++), "备份位置是链接，已停止修改游戏设置。");
        Equal(0, calls);
        Bytes(snapshot, File.ReadAllBytes(fixture.File));
        Equal("earlier backup", File.ReadAllText(target));
        NoStaging(fixture);
    }

    private static void WindowsReadSharing() {
        using Fixture fixture = new();
        byte[] original = Encoding.UTF8.GetBytes("fov:70\n");
        File.WriteAllBytes(fixture.File, original);
        using (FileStream reader = new(fixture.File, FileMode.Open, FileAccess.Read, FileShare.Read)) Bytes(original, PersonalOptionsFiles.Read(fixture.File)!);
        using (FileStream exclusive = new(fixture.File, FileMode.Open, FileAccess.Read, FileShare.None)) FileOperationError(() => PersonalOptionsFiles.Read(fixture.File));
        using (FileStream writer = new(fixture.File, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) FileOperationError(() => PersonalOptionsFiles.Read(fixture.File));
        Bytes(original, File.ReadAllBytes(fixture.File));
    }

    private static void WindowsReadOnlyWrite() {
        using Fixture fixture = new();
        byte[] snapshot = Encoding.UTF8.GetBytes("fov:70\n");
        File.WriteAllBytes(fixture.File, snapshot);
        File.SetAttributes(fixture.File, File.GetAttributes(fixture.File) | FileAttributes.ReadOnly);
        int calls = 0;
        try {
            FileOperationError(() => PersonalOptionsFiles.ApplyMerged(fixture.File, snapshot, "fov:70\n", "fov:90\n", _ => calls++));
            Equal(0, calls);
            Bytes(snapshot, File.ReadAllBytes(fixture.File));
            Equal(false, File.Exists(fixture.Backup));
            NoStaging(fixture);
        } finally {
            File.SetAttributes(fixture.File, FileAttributes.Normal);
        }
    }

    private static bool CanCreateSymbolicLinks() {
        if (!OperatingSystem.IsWindows()) return true;
        using Fixture fixture = new();
        File.WriteAllText(fixture.File, "probe");
        try {
            File.CreateSymbolicLink(fixture.Path("link"), fixture.File);
            return true;
        } catch (UnauthorizedAccessException) {
            Console.WriteLine("SKIP personal-option file link cases: Windows runner lacks symbolic-link permission.");
            return false;
        } catch (IOException error) when ((error.HResult & 0xffff) == 1314) {
            Console.WriteLine("SKIP personal-option file link cases: Windows runner lacks symbolic-link permission.");
            return false;
        }
    }

    private static void NoStaging(Fixture fixture) => Equal(0, Directory.GetFiles(fixture.Root, "*.pcl-write-*", SearchOption.AllDirectories).Length);

    private static void Bytes(byte[] expected, byte[] actual) {
        if (!expected.SequenceEqual(actual)) throw new InvalidOperationException("File bytes differ.");
    }

    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static T Throws<T>(Action action, string? message = null) where T : Exception {
        try {
            action();
        } catch (T error) {
            if (message is not null) Equal(message, error.Message);
            return error;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void FileOperationError(Action action) {
        try {
            action();
        } catch (IOException) {
            return;
        } catch (UnauthorizedAccessException) {
            return;
        }
        throw new InvalidOperationException("Expected the file operation to be rejected.");
    }

    private sealed class Fixture : IDisposable {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PclPersonalOptionFiles-" + Guid.NewGuid().ToString("N"));
        internal string File => Path("options.txt");
        internal string Backup => File + ".pcl-backup";
        internal Fixture() => Directory.CreateDirectory(Root);
        internal string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
