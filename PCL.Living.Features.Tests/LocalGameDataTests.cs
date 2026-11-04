using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCL.Living.Features;

internal static class LocalGameDataTests {
    private const string ChangedMessage = "游戏设置已被其他程序修改，本次同步已停止。";

    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("local data root keeps legacy profile and dynamic override", Roots);
        yield return ("local data path callback precedes reads and writes", PathGuard);
        yield return ("local data missing JSON stays empty without creating files", MissingJson);
        yield return ("local data JSON keeps legacy format, Unicode and backup", JsonRoundTrip);
        yield return ("local data reads BOM encodings and rejects invalid UTF-8", JsonEncodings);
        yield return ("local data rejects malformed or non-object JSON", InvalidJson);
        yield return ("local data enforces exact 4 MiB input boundary", JsonSize);
        yield return ("local data SHA256 uses known Base64 vectors", HashVectors);
        yield return ("atomic write creates nested destination without a backup", CreateFile);
        yield return ("atomic replacement rotates previous file into backup", ReplaceWithBackup);
        yield return ("atomic replacement without backup leaves old backup alone", ReplaceWithoutBackup);
        yield return ("atomic replacement accepts matching hash and keeps exact bytes", MatchingHash);
        yield return ("atomic replacement rejects external edits without changing either file", StaleHash);
        yield return ("atomic replacement rejects missing hash target and cleans staging", MissingHashTarget);
        yield return ("atomic replacement compares hash with exact case", HashCase);
        yield return ("atomic move failure preserves occupied path and unrelated staging", MoveFailure);
        yield return ("atomic backup failure preserves original and removes staging", BackupFailure);
        yield return ("local data process matching checks process identity and start time", ProcessIdentity);
        if (CanCreateSymbolicLinks()) {
            yield return ("atomic replacement refuses a linked destination", LinkedDestination);
            yield return ("atomic replacement protects linked backup only when requested", LinkedBackup);
        }
        if (OperatingSystem.IsWindows()) {
            yield return ("atomic replacement respects Windows destination sharing lock", LockedDestination);
            yield return ("atomic replacement respects Windows read-only destination", ReadOnlyDestination);
        }
    }

    private static void Roots() {
        using Fixture fixture = new();
        Equal(Path.GetFullPath("."), LocalGameDataStore.GetRoot("."));
        Equal(fixture.Root, LocalGameDataStore.GetRoot(fixture.Root));
        string baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseFolder)) {
            Throws<IOException>(() => LocalGameDataStore.GetRoot(null), "无法确定当前用户的本机资料目录。");
        } else {
            Equal(Path.Combine(baseFolder, "PCL Living Fork", "GameData"), LocalGameDataStore.GetRoot(null));
        }
        string selected = Path.Combine(fixture.Root, "first");
        LocalGameDataStore store = new(() => selected, (_, _) => { });
        store.WriteJson("profile.json", new JObject(new JProperty("id", 1)));
        selected = Path.Combine(fixture.Root, "second");
        store.WriteJson("profile.json", new JObject(new JProperty("id", 2)));
        Equal(2, (int) store.ReadJson("profile.json")["id"]!);
        Equal(1, (int) JObject.Parse(File.ReadAllText(Path.Combine(fixture.Root, "first", "profile.json")))["id"]!);
    }

    private static void PathGuard() {
        using Fixture fixture = new();
        string root = Path.Combine(fixture.Root, "profile");
        IOException denied = new("legacy path validator rejected candidate");
        int checks = 0;
        LocalGameDataStore store = new(() => root, (actualRoot, candidate) => {
            Equal(root, actualRoot);
            Equal(Path.Combine(root, "../outside.json"), candidate);
            checks++;
            throw denied;
        });
        Equal(denied, Throws<IOException>(() => store.Address("../outside.json")));
        Equal(denied, Throws<IOException>(() => store.ReadJson("../outside.json")));
        Equal(denied, Throws<IOException>(() => store.WriteJson("../outside.json", new JObject())));
        Equal(3, checks);
        Equal(false, File.Exists(Path.Combine(fixture.Root, "outside.json")));
        Equal(false, Directory.Exists(root));
    }

    private static void MissingJson() {
        using Fixture fixture = new();
        JObject first = fixture.Store.ReadJson("missing/nested.json");
        JObject second = fixture.Store.ReadJson("missing/nested.json");
        Equal(0, first.Count);
        Equal(0, second.Count);
        Equal(false, ReferenceEquals(first, second));
        Equal(false, Directory.Exists(Path.Combine(fixture.Root, "missing")));
        Equal(0, Directory.GetFileSystemEntries(fixture.Root).Length);
    }

    private static void JsonRoundTrip() {
        using Fixture fixture = new();
        string path = fixture.Path("preferences.json");
        JObject original = new(new JProperty("version", 1), new JProperty("name", "旧习惯 中文 Ω"),
            new JProperty("values", new JObject(new JProperty("key", "value"))));
        fixture.Store.WriteJson("preferences.json", original);
        string expected = "{" + Environment.NewLine + "  \"version\": 1," + Environment.NewLine +
            "  \"name\": \"旧习惯 中文 Ω\"," + Environment.NewLine + "  \"values\": {" + Environment.NewLine +
            "    \"key\": \"value\"" + Environment.NewLine + "  }" + Environment.NewLine + "}";
        Bytes(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(path));
        Equal(true, JToken.DeepEquals(original, fixture.Store.ReadJson("preferences.json")));
        Bytes(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(path));
        original["version"] = 2;
        fixture.Store.WriteJson("preferences.json", original);
        Bytes(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(path + ".pcl-backup"));
        Equal(2, (int) fixture.Store.ReadJson("preferences.json")["version"]!);
        NoStaging(fixture);
    }

    private static void JsonEncodings() {
        using Fixture fixture = new();
        const string json = "{\"name\":\"中文 Ω\"}";
        foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32 }) {
            byte[] bytes = encoding.GetPreamble().Concat(encoding.GetBytes(json)).ToArray();
            File.WriteAllBytes(fixture.Path("encoding.json"), bytes);
            Equal("中文 Ω", (string?) fixture.Store.ReadJson("encoding.json")["name"]);
            Bytes(bytes, File.ReadAllBytes(fixture.Path("encoding.json")));
        }
        File.WriteAllBytes(fixture.Path("encoding.json"), [123, 34, 120, 34, 58, 34, 255, 34, 125]);
        Throws<DecoderFallbackException>(() => fixture.Store.ReadJson("encoding.json"));
    }

    private static void InvalidJson() {
        using Fixture fixture = new();
        foreach (string text in new[] { "{", "[1, 2]", "42", "" }) {
            File.WriteAllText(fixture.Path("invalid.json"), text);
            Throws<JsonReaderException>(() => fixture.Store.ReadJson("invalid.json"));
            Equal(text, File.ReadAllText(fixture.Path("invalid.json")));
        }
        NoStaging(fixture);
    }

    private static void JsonSize() {
        using Fixture fixture = new();
        const int limit = 4 * 1024 * 1024;
        string json = "{\"x\":\"" + new string('x', limit - 8) + "\"}";
        File.WriteAllText(fixture.Path("limit.json"), json, new UTF8Encoding(false));
        Equal((long) limit, new FileInfo(fixture.Path("limit.json")).Length);
        Equal(limit - 8, ((string) fixture.Store.ReadJson("limit.json")["x"]!).Length);
        using (FileStream output = new(fixture.Path("limit.json"), FileMode.Append)) output.WriteByte(32);
        Throws<IOException>(() => fixture.Store.ReadJson("limit.json"), "本机游戏资料文件过大。");
        Equal((long) limit + 1, new FileInfo(fixture.Path("limit.json")).Length);
    }

    private static void HashVectors() {
        Equal("47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=", LocalGameDataStore.Hash([]));
        Equal("ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=", LocalGameDataStore.Hash(Encoding.ASCII.GetBytes("abc")));
        byte[] values = [0, 1, 127, 128, 255];
        byte[] before = (byte[]) values.Clone();
        LocalGameDataStore.Hash(values);
        Bytes(before, values);
    }

    private static void CreateFile() {
        using Fixture fixture = new();
        string path = fixture.Path("nested", "more", "options.txt");
        byte[] bytes = [239, 187, 191, 0, 10, 13, 255];
        LocalGameDataStore.AtomicWrite(path, bytes, null, true);
        Bytes(bytes, File.ReadAllBytes(path));
        Equal(false, File.Exists(path + ".pcl-backup"));
        using FileStream held = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        held.Dispose();
        NoStaging(fixture);
    }

    private static void ReplaceWithBackup() {
        using Fixture fixture = new();
        string path = fixture.Path("options.txt");
        File.WriteAllText(path, "original");
        File.WriteAllText(path + ".pcl-backup", "older backup");
        LocalGameDataStore.AtomicWrite(path, Encoding.UTF8.GetBytes("changed"), null, true);
        Equal("changed", File.ReadAllText(path));
        Equal("original", File.ReadAllText(path + ".pcl-backup"));
        LocalGameDataStore.AtomicWrite(path, Encoding.UTF8.GetBytes("changed again"), null, true);
        Equal("changed again", File.ReadAllText(path));
        Equal("changed", File.ReadAllText(path + ".pcl-backup"));
        NoStaging(fixture);
    }

    private static void ReplaceWithoutBackup() {
        using Fixture fixture = new();
        string path = fixture.Path("options.txt");
        File.WriteAllText(path, "original");
        LocalGameDataStore.AtomicWrite(path, [0, 255, 10], null, false);
        Equal(false, File.Exists(path + ".pcl-backup"));
        File.WriteAllText(path + ".pcl-backup", "older backup");
        LocalGameDataStore.AtomicWrite(path, [1, 2, 3], null, false);
        Bytes([1, 2, 3], File.ReadAllBytes(path));
        Equal("older backup", File.ReadAllText(path + ".pcl-backup"));
        NoStaging(fixture);
    }

    private static void MatchingHash() {
        using Fixture fixture = new();
        string path = fixture.Path("options.txt");
        byte[] original = [239, 187, 191, 13, 10, 0, 255];
        byte[] replacement = [255, 0, 1, 13, 10];
        File.WriteAllBytes(path, original);
        LocalGameDataStore.AtomicWrite(path, replacement, LocalGameDataStore.Hash(original), true);
        Bytes(original, File.ReadAllBytes(path + ".pcl-backup"));
        Bytes(replacement, File.ReadAllBytes(path));
        NoStaging(fixture);
    }

    private static void StaleHash() {
        using Fixture fixture = new();
        string path = fixture.Path("options.txt");
        File.WriteAllText(path, "baseline");
        string baselineHash = LocalGameDataStore.Hash(File.ReadAllBytes(path));
        File.WriteAllText(path, "external edit");
        File.WriteAllText(path + ".pcl-backup", "existing backup");
        Throws<IOException>(() => LocalGameDataStore.AtomicWrite(path, Encoding.UTF8.GetBytes("uncommitted"), baselineHash, true), ChangedMessage);
        Equal("external edit", File.ReadAllText(path));
        Equal("existing backup", File.ReadAllText(path + ".pcl-backup"));
        NoStaging(fixture);
    }

    private static void MissingHashTarget() {
        using Fixture fixture = new();
        string path = fixture.Path("new", "options.txt");
        Throws<IOException>(() => LocalGameDataStore.AtomicWrite(path, [1, 2], LocalGameDataStore.Hash([3]), true), ChangedMessage);
        Equal(false, File.Exists(path));
        Equal(false, File.Exists(path + ".pcl-backup"));
        NoStaging(fixture);
    }

    private static void HashCase() {
        using Fixture fixture = new();
        string path = fixture.Path("options.txt");
        File.WriteAllText(path, "abc");
        string wrongCase = "UngWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=";
        Throws<IOException>(() => LocalGameDataStore.AtomicWrite(path, [0], wrongCase, false), ChangedMessage);
        Equal("abc", File.ReadAllText(path));
        NoStaging(fixture);
    }

    private static void MoveFailure() {
        using Fixture fixture = new();
        string occupied = fixture.Path("options.txt");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(System.IO.Path.Combine(occupied, "keep"), "directory payload");
        string unrelated = occupied + ".pcl-write-unrelated";
        File.WriteAllText(unrelated, "other operation");
        Throws<IOException>(() => LocalGameDataStore.AtomicWrite(occupied, [0], null, false));
        Equal("directory payload", File.ReadAllText(System.IO.Path.Combine(occupied, "keep")));
        Equal("other operation", File.ReadAllText(unrelated));
        Equal(1, Directory.GetFiles(fixture.Root, "*.pcl-write-*", SearchOption.AllDirectories).Length);
    }

    private static void BackupFailure() {
        using Fixture fixture = new();
        string path = fixture.Path("options.txt");
        File.WriteAllText(path, "original");
        Directory.CreateDirectory(path + ".pcl-backup");
        File.WriteAllText(System.IO.Path.Combine(path + ".pcl-backup", "keep"), "backup folder");
        FileOperationError(() => LocalGameDataStore.AtomicWrite(path, [0], null, true));
        Equal("original", File.ReadAllText(path));
        Equal("backup folder", File.ReadAllText(System.IO.Path.Combine(path + ".pcl-backup", "keep")));
        NoStaging(fixture);
    }

    private static void ProcessIdentity() {
        using Process process = Process.GetCurrentProcess();
        long started = process.StartTime.ToUniversalTime().Ticks;
        Equal(true, LocalGameDataStore.ProcessMatches(process.Id, started));
        Equal(false, LocalGameDataStore.ProcessMatches(process.Id, started + 1));
        Equal(false, LocalGameDataStore.ProcessMatches(int.MaxValue, started));
    }

    private static void LinkedDestination() {
        using Fixture fixture = new();
        string target = fixture.Path("real-options.txt"), link = fixture.Path("options.txt");
        File.WriteAllText(target, "real file");
        File.CreateSymbolicLink(link, target);
        Throws<IOException>(() => LocalGameDataStore.AtomicWrite(link, [0], null, true), "不修改链接形式的游戏设置文件。");
        Equal("real file", File.ReadAllText(target));
        Equal(true, (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        NoStaging(fixture);
    }

    private static void LinkedBackup() {
        using Fixture fixture = new();
        string path = fixture.Path("options.txt"), target = fixture.Path("real-backup.txt");
        File.WriteAllText(path, "original");
        File.WriteAllText(target, "backup payload");
        File.CreateSymbolicLink(path + ".pcl-backup", target);
        Throws<IOException>(() => LocalGameDataStore.AtomicWrite(path, [0], null, true), "备份位置是链接，已停止修改游戏设置。");
        Equal("original", File.ReadAllText(path));
        Equal("backup payload", File.ReadAllText(target));
        LocalGameDataStore.AtomicWrite(path, Encoding.UTF8.GetBytes("new file"), null, false);
        Equal("new file", File.ReadAllText(path));
        Equal("backup payload", File.ReadAllText(target));
        Equal(true, (File.GetAttributes(path + ".pcl-backup") & FileAttributes.ReparsePoint) != 0);
        NoStaging(fixture);
    }

    private static void LockedDestination() {
        using Fixture fixture = new();
        string path = fixture.Path("options.txt");
        File.WriteAllText(path, "original");
        using (FileStream held = new(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            FileOperationError(() => LocalGameDataStore.AtomicWrite(path, [0], null, true));
            Equal("original", File.ReadAllText(path));
            Equal(false, File.Exists(path + ".pcl-backup"));
            NoStaging(fixture);
        }
        LocalGameDataStore.AtomicWrite(path, [1], null, true);
        Bytes([1], File.ReadAllBytes(path));
    }

    private static void ReadOnlyDestination() {
        using Fixture fixture = new();
        string path = fixture.Path("options.txt");
        File.WriteAllText(path, "original");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try {
            FileOperationError(() => LocalGameDataStore.AtomicWrite(path, [0], null, true));
            Equal("original", File.ReadAllText(path));
            Equal(false, File.Exists(path + ".pcl-backup"));
            NoStaging(fixture);
        } finally {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    private static bool CanCreateSymbolicLinks() {
        if (!OperatingSystem.IsWindows()) return true;
        using Fixture fixture = new();
        File.WriteAllText(fixture.Path("target"), "probe");
        try {
            File.CreateSymbolicLink(fixture.Path("link"), fixture.Path("target"));
            return true;
        } catch (UnauthorizedAccessException) {
            Console.WriteLine("SKIP local data symbolic-link cases: Windows runner lacks symbolic-link permission.");
            return false;
        } catch (IOException error) when ((error.HResult & 0xffff) == 1314) {
            Console.WriteLine("SKIP local data symbolic-link cases: Windows runner lacks symbolic-link permission.");
            return false;
        }
    }

    private static void NoStaging(Fixture fixture) => Equal(0, Directory.GetFiles(fixture.Root, "*.pcl-write-*", SearchOption.AllDirectories).Length);

    private static void Bytes(byte[] expected, byte[] actual) {
        if (!expected.SequenceEqual(actual)) throw new InvalidOperationException("File bytes differ.");
    }

    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
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
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PclLocalData-" + Guid.NewGuid().ToString("N"));
        internal LocalGameDataStore Store { get; }

        internal Fixture() {
            Directory.CreateDirectory(Root);
            // The launcher's existing path validator is injected by the VB facade, not reimplemented here.
            Store = new LocalGameDataStore(() => Root, (_, _) => { });
        }

        internal string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
