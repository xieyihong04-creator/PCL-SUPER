using System.Text;
using PCL.Living.Features;

internal static class InstanceKeybindingFileTests {
    private const string Key = "key_key.forward";
    private const string StaleError = "该按键配置已变化，请刷新后重试。";

    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("keybinding save checks running game before other input", RunningFirst);
        yield return ("keybinding save rejects invalid input before path access", InvalidInput);
        yield return ("keybinding save calls original guard before checking file", GuardOrder);
        yield return ("keybinding save missing options leaves default generation alone", MissingFile);
        yield return ("keybinding save preserves exact 4 MiB size boundary", SizeBoundary);
        yield return ("keybinding save appends unsaved default with original trailing lines", AppendDefault);
        yield return ("keybinding save missing previous key rejects stale saved row", MissingPrevious);
        yield return ("keybinding save distinguishes null from empty previous value", NullAndEmptyPrevious);
        yield return ("keybinding save validates last duplicate and updates every matching row", DuplicateRows);
        yield return ("keybinding save rejects stale last duplicate without partial updates", StaleDuplicate);
        yield return ("keybinding save keeps exact key spelling without alias conversion", ExactKey);
        yield return ("keybinding save same value still commits backup and clears cache", SameValue);
        yield return ("keybinding save writes original replacement rather than normalized display", RawReplacement);
        yield return ("keybinding save preserves UTF-8 BOM convention and raw backup", BomBytes);
        yield return ("keybinding save keeps strict UTF-8 decoding before row validation", InvalidEncoding);
        yield return ("keybinding save keeps mixed-newline and trailing-blank behavior", Newlines);
        yield return ("keybinding save commit failure preserves files and skips cache", CommitFailure);
        yield return ("keybinding save cache runs after commit inside original Monitor", CacheOrder);
        yield return ("keybinding save cache exception propagates without rolling back commit", CacheFailure);
        if (CanCreateSymbolicLinks()) {
            yield return ("keybinding save does not replace a linked destination", LinkedDestination);
            yield return ("keybinding save does not overwrite linked backup", LinkedBackup);
        }
        if (OperatingSystem.IsWindows()) {
            yield return ("keybinding save Windows sharing failure leaves original intact", WindowsSharing);
            yield return ("keybinding save Windows read-only failure skips cache", WindowsReadOnly);
        }
    }

    private static void RunningFirst() {
        using Fixture fixture = new();
        fixture.Running = true;
        Throws<IOException>(() => fixture.Save(null!, "17", null!), "请先关闭游戏，再修改按键配置。");
        Equal("running", string.Join("|", fixture.Events));
        Equal(0, Directory.GetFileSystemEntries(fixture.Root).Length);
    }

    private static void InvalidInput() {
        using Fixture fixture = new();
        foreach ((string key, string? replacement) in new[] { ("", "19"), ("Key_key.forward", "19"), ("lang", "19"), (Key, "invalid"), (Key, ""), (Key, (string?) null) }) {
            fixture.Events.Clear();
            Throws<IOException>(() => fixture.Save(key, "17", replacement!), "按键值无效。");
            Equal("running", string.Join("|", fixture.Events));
        }
        Equal(0, Directory.GetFileSystemEntries(fixture.Root).Length);
    }

    private static void GuardOrder() {
        using Fixture fixture = new();
        IOException sentinel = new("existing path guard rejected options");
        fixture.Guard = (root, file) => {
            Equal(fixture.Root, root);
            Equal(Path.Combine(fixture.Root, "options.txt"), file);
            Equal(false, Monitor.IsEntered(fixture.Gate));
            throw sentinel;
        };
        Equal(sentinel, Throws<IOException>(() => fixture.Save(Key, null, "19")));
        Equal("running|guard", string.Join("|", fixture.Events));
        Equal(false, File.Exists(fixture.File));
    }

    private static void MissingFile() {
        using Fixture fixture = new();
        Throws<IOException>(() => fixture.Save(Key, null, "19"), "请先运行一次游戏，生成按键配置。");
        Equal("running|guard", string.Join("|", fixture.Events));
        Equal(0, Directory.GetFileSystemEntries(fixture.Root).Length);
    }

    private static void SizeBoundary() {
        using Fixture fixture = new();
        const int limit = 4 * 1024 * 1024;
        string beginning = Key + ":17\n#";
        byte[] original = Encoding.UTF8.GetBytes(beginning + new string('x', limit - beginning.Length));
        File.WriteAllBytes(fixture.File, original);
        fixture.Save(Key, "17", "19");
        Equal((long) limit, new FileInfo(fixture.File).Length);
        Bytes(original, File.ReadAllBytes(fixture.Backup));
        using (FileStream output = new(fixture.File, FileMode.Append)) output.WriteByte(0);
        fixture.Events.Clear();
        Throws<IOException>(() => fixture.Save(Key, "19", "20"), "请先运行一次游戏，生成按键配置。");
        Equal("running|guard", string.Join("|", fixture.Events));
        Equal((long) limit + 1, new FileInfo(fixture.File).Length);
        Bytes(original, File.ReadAllBytes(fixture.Backup));
        NoStaging(fixture);
    }

    private static void AppendDefault() {
        foreach ((string input, string expected) in new[] {
            ("", "key_key.forward:19"),
            ("lang:zh_cn", "lang:zh_cn\nkey_key.forward:19"),
            ("lang:zh_cn\n", "lang:zh_cn\nkey_key.forward:19\n"),
            ("lang:zh_cn\n\n", "lang:zh_cn\n\nkey_key.forward:19\n"),
            ("\n", "\nkey_key.forward:19\n")
        }) {
            using Fixture fixture = new();
            byte[] original = fixture.Put(input);
            fixture.Save(Key, null, "19");
            Equal(expected, File.ReadAllText(fixture.File));
            Bytes(original, File.ReadAllBytes(fixture.Backup));
            Equal(1, fixture.Events.Count(item => item == "cache"));
        }
    }

    private static void MissingPrevious() {
        using Fixture fixture = new();
        byte[] original = fixture.Put("lang:zh_cn\n");
        foreach (string previous in new[] { "17", "" }) Throws<IOException>(() => fixture.Save(Key, previous, "19"), StaleError);
        Bytes(original, File.ReadAllBytes(fixture.File));
        Equal(false, File.Exists(fixture.Backup));
        Equal(false, fixture.Events.Contains("cache"));
        NoStaging(fixture);
    }

    private static void NullAndEmptyPrevious() {
        using Fixture fixture = new();
        byte[] original = fixture.Put("key_key.forward:\n");
        Throws<IOException>(() => fixture.Save(Key, null, "19"), StaleError);
        Bytes(original, File.ReadAllBytes(fixture.File));
        fixture.Save(Key, "", "19");
        Equal("key_key.forward:19\n", File.ReadAllText(fixture.File));
        Bytes(original, File.ReadAllBytes(fixture.Backup));
    }

    private static void DuplicateRows() {
        using Fixture fixture = new();
        byte[] original = fixture.Put("# author:keep\nkey_key.forward:17\nkey_key.forwardExtra:99\n key_key.forward:keep\nkey_key.forward:19\nkey_mod.other:key.keyboard.y\n");
        fixture.Save(Key, "19", "key.keyboard.r:alt");
        Equal("# author:keep\nkey_key.forward:key.keyboard.r:alt\nkey_key.forwardExtra:99\n key_key.forward:keep\nkey_key.forward:key.keyboard.r:alt\nkey_mod.other:key.keyboard.y\n", File.ReadAllText(fixture.File));
        Bytes(original, File.ReadAllBytes(fixture.Backup));
        NoStaging(fixture);
    }

    private static void StaleDuplicate() {
        using Fixture fixture = new();
        byte[] original = fixture.Put("key_key.forward:17\nkey_key.forward:19\n");
        Throws<IOException>(() => fixture.Save(Key, "17", "20"), StaleError);
        Bytes(original, File.ReadAllBytes(fixture.File));
        Equal(false, File.Exists(fixture.Backup));
        Equal(false, fixture.Events.Contains("cache"));
    }

    private static void ExactKey() {
        using Fixture fixture = new();
        fixture.Put("key_key.swapHands:33\nkey_key.swapOffhand:18\nkey_mod.magic:17\n");
        fixture.Save("key_key.swapHands", "33", "19");
        Equal("key_key.swapHands:19\nkey_key.swapOffhand:18\nkey_mod.magic:17\n", File.ReadAllText(fixture.File));
        fixture.Save("key_mod.magic", "17", "key.keyboard.x");
        Equal("key_key.swapHands:19\nkey_key.swapOffhand:18\nkey_mod.magic:key.keyboard.x\n", File.ReadAllText(fixture.File));
        Throws<IOException>(() => fixture.Save("key_key.SwapHands", "19", "20"), StaleError);
    }

    private static void SameValue() {
        using Fixture fixture = new();
        byte[] original = fixture.Put("key_key.forward:17\r\n");
        File.WriteAllText(fixture.Backup, "older backup");
        fixture.Save(Key, "17", "17");
        Bytes(original, File.ReadAllBytes(fixture.File));
        Bytes(original, File.ReadAllBytes(fixture.Backup));
        Equal(1, fixture.Events.Count(item => item == "cache"));
        NoStaging(fixture);
    }

    private static void RawReplacement() {
        using Fixture fixture = new();
        fixture.Put("key_key.forward:17\n");
        fixture.Save(Key, "17", "19:alt:Shift");
        Equal("key_key.forward:19:alt:Shift\n", File.ReadAllText(fixture.File));
        Equal("key.keyboard.r:ALT:SHIFT", PersonalOptionsRules.NormalizeBinding("19:alt:Shift"));
    }

    private static void BomBytes() {
        foreach (int count in new[] { 0, 1, 2 }) {
            using Fixture fixture = new();
            byte[] original = [.. Enumerable.Repeat(new byte[] { 239, 187, 191 }, count).SelectMany(bytes => bytes), .. Encoding.UTF8.GetBytes("key_key.forward:17\r\n# 中文 Ω\r\n")];
            File.WriteAllBytes(fixture.File, original);
            fixture.Save(Key, "17", "19");
            byte[] expected = [.. count == 0 ? Array.Empty<byte>() : new byte[] { 239, 187, 191 }, .. Encoding.UTF8.GetBytes("key_key.forward:19\r\n# 中文 Ω\r\n")];
            Bytes(expected, File.ReadAllBytes(fixture.File));
            Bytes(original, File.ReadAllBytes(fixture.Backup));
        }
    }

    private static void InvalidEncoding() {
        foreach (byte[] invalid in new byte[][] { [255], [239, 187], [255, 254, 107, 0] }) {
            using Fixture fixture = new();
            File.WriteAllBytes(fixture.File, invalid);
            Throws<DecoderFallbackException>(() => fixture.Save(Key, "stale previous", "19"));
            Bytes(invalid, File.ReadAllBytes(fixture.File));
            Equal(false, File.Exists(fixture.Backup));
            Equal(false, fixture.Events.Contains("cache"));
        }
    }

    private static void Newlines() {
        using Fixture fixture = new();
        fixture.Put("# author\r\nkey_key.forward:17\nraw:line\rcontent\n\n");
        fixture.Save(Key, "17", "19");
        Equal("# author\r\nkey_key.forward:19\r\nraw:line\rcontent\r\n\r\n", File.ReadAllText(fixture.File));
        fixture.Put("lang:zh_cn\r\n\r\n");
        fixture.Save(Key, null, "19");
        Equal("lang:zh_cn\r\n\r\nkey_key.forward:19\r\n", File.ReadAllText(fixture.File));
    }

    private static void CommitFailure() {
        using Fixture fixture = new();
        byte[] original = fixture.Put("key_key.forward:17\n");
        Directory.CreateDirectory(fixture.Backup);
        File.WriteAllText(Path.Combine(fixture.Backup, "keep"), "backup folder");
        FileOperationError(() => fixture.Save(Key, "17", "19"));
        Bytes(original, File.ReadAllBytes(fixture.File));
        Equal("backup folder", File.ReadAllText(Path.Combine(fixture.Backup, "keep")));
        Equal(false, fixture.Events.Contains("cache"));
        NoStaging(fixture);
    }

    private static void CacheOrder() {
        using Fixture fixture = new();
        fixture.Put("key_key.forward:17\n");
        fixture.Guard = (_, _) => Equal(false, Monitor.IsEntered(fixture.Gate));
        fixture.Invalidate = path => {
            Equal(fixture.File, path);
            Equal(true, Monitor.IsEntered(fixture.Gate));
            lock (fixture.Gate) {
                using FileStream destination = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                using FileStream backup = new(fixture.Backup, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                using StreamReader next = new(destination, Encoding.UTF8);
                using StreamReader previous = new(backup, Encoding.UTF8);
                Equal("key_key.forward:19\n", next.ReadToEnd());
                Equal("key_key.forward:17\n", previous.ReadToEnd());
                NoStaging(fixture);
            }
        };
        fixture.Save(Key, "17", "19");
        Equal("running|guard|cache", string.Join("|", fixture.Events));
        Equal(false, Monitor.IsEntered(fixture.Gate));
    }

    private static void CacheFailure() {
        using Fixture fixture = new();
        byte[] original = fixture.Put("key_key.forward:17\n");
        InvalidOperationException sentinel = new("cache callback failed");
        fixture.Invalidate = _ => throw sentinel;
        Equal(sentinel, Throws<InvalidOperationException>(() => fixture.Save(Key, "17", "19")));
        Equal("key_key.forward:19\n", File.ReadAllText(fixture.File));
        Bytes(original, File.ReadAllBytes(fixture.Backup));
        Equal(false, Monitor.IsEntered(fixture.Gate));
        NoStaging(fixture);
    }

    private static void LinkedDestination() {
        using Fixture fixture = new();
        string target = Path.Combine(fixture.Root, "target.txt");
        File.WriteAllText(target, "key_key.forward:17\n");
        File.CreateSymbolicLink(fixture.File, target);
        Throws<IOException>(() => fixture.Save(Key, "17", "19"), "不修改链接形式的游戏设置文件。");
        Equal("key_key.forward:17\n", File.ReadAllText(target));
        Equal(true, (File.GetAttributes(fixture.File) & FileAttributes.ReparsePoint) != 0);
        Equal(false, fixture.Events.Contains("cache"));
        NoStaging(fixture);
    }

    private static void LinkedBackup() {
        using Fixture fixture = new();
        byte[] original = fixture.Put("key_key.forward:17\n");
        string target = Path.Combine(fixture.Root, "backup-target.txt");
        File.WriteAllText(target, "older backup");
        File.CreateSymbolicLink(fixture.Backup, target);
        Throws<IOException>(() => fixture.Save(Key, "17", "19"), "备份位置是链接，已停止修改游戏设置。");
        Bytes(original, File.ReadAllBytes(fixture.File));
        Equal("older backup", File.ReadAllText(target));
        Equal(false, fixture.Events.Contains("cache"));
        NoStaging(fixture);
    }

    private static void WindowsSharing() {
        using Fixture fixture = new();
        byte[] original = fixture.Put("key_key.forward:17\n");
        using (FileStream held = new(fixture.File, FileMode.Open, FileAccess.Read, FileShare.None)) FileOperationError(() => fixture.Save(Key, "17", "19"));
        Bytes(original, File.ReadAllBytes(fixture.File));
        Equal(false, fixture.Events.Contains("cache"));
        Equal(false, File.Exists(fixture.Backup));
        NoStaging(fixture);
    }

    private static void WindowsReadOnly() {
        using Fixture fixture = new();
        byte[] original = fixture.Put("key_key.forward:17\n");
        File.SetAttributes(fixture.File, File.GetAttributes(fixture.File) | FileAttributes.ReadOnly);
        try {
            FileOperationError(() => fixture.Save(Key, "17", "19"));
            Bytes(original, File.ReadAllBytes(fixture.File));
            Equal(false, fixture.Events.Contains("cache"));
            Equal(false, File.Exists(fixture.Backup));
            NoStaging(fixture);
        } finally { File.SetAttributes(fixture.File, FileAttributes.Normal); }
    }

    private static bool CanCreateSymbolicLinks() {
        if (!OperatingSystem.IsWindows()) return true;
        using Fixture fixture = new();
        fixture.Put("probe");
        try {
            File.CreateSymbolicLink(Path.Combine(fixture.Root, "link"), fixture.File);
            return true;
        } catch (UnauthorizedAccessException) {
            Console.WriteLine("SKIP keybinding file link cases: Windows runner lacks symbolic-link permission.");
            return false;
        } catch (IOException error) when ((error.HResult & 0xffff) == 1314) {
            Console.WriteLine("SKIP keybinding file link cases: Windows runner lacks symbolic-link permission.");
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
        try { action(); } catch (T error) {
            if (message is not null) Equal(message, error.Message);
            return error;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static void FileOperationError(Action action) {
        try { action(); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Expected the file operation to be rejected.");
    }

    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PclKeybindingFiles-" + Guid.NewGuid().ToString("N"));
        internal string File => Path.Combine(Root, "options.txt");
        internal string Backup => File + ".pcl-backup";
        internal object Gate { get; } = new();
        internal List<string> Events { get; } = [];
        internal bool Running;
        internal Action<string, string>? Guard;
        internal Action<string>? Invalidate;
        internal Fixture() => Directory.CreateDirectory(Root);
        internal byte[] Put(string text) {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            System.IO.File.WriteAllBytes(File, bytes);
            return bytes;
        }
        internal void Save(string key, string? previous, string replacement) => InstanceKeybindingFiles.Save(Root, key, previous, replacement, Gate,
            () => { Events.Add("running"); Equal(false, Monitor.IsEntered(Gate)); return Running; },
            (root, file) => { Events.Add("guard"); Guard?.Invoke(root, file); },
            file => { Events.Add("cache"); Invalidate?.Invoke(file); });
        public void Dispose() => Directory.Delete(Root, true);
    }
}
