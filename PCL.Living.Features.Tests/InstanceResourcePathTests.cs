using PCL.Living.Features;

internal static class InstanceResourcePathTests {
    private const string Outside = "资源路径超出了当前目录。";
    private const string Link = "请通过资源管理器管理链接文件或链接目录。";

    internal static IEnumerable<(string Name, Action Run)> Cases() {
        // Prefix callbacks here isolate the boundary; the Windows oracle supplies the real fixed Meloong implementation.
        yield return ("resource path normalization delegates raw input before resolving and trimming", NormalizeCallback);
        yield return ("resource path nulls retain callback and path API exception order", NormalizeNulls);
        yield return ("resource boundary normalizes root completely before reading target", RootFirst);
        yield return ("resource boundary invokes target prefix callback even when root is allowed", TargetCallback);
        // The original boundary has a literal backslash; Linux is not a substitute for its Windows filesystem behavior.
        if (OperatingSystem.IsWindows()) {
            yield return ("resource Windows boundary reads ordinary files and permits missing descendants", OrdinaryPaths);
            yield return ("resource Windows root equality requires AllowRoot and ignores exact case", RootEquality);
            yield return ("resource Windows boundary rejects sibling prefixes and normalized traversal", ScopeBoundary);
            yield return ("resource Windows boundary adds no directory or existence requirement for root", UncheckedRootKind);
            yield return ("resource Windows drive-root traversal retains original null-reference failure", DriveRoot);
            yield return ("resource Windows boundary rejects linked directory and linked inner ancestor", InnerDirectoryLink);
            yield return ("resource Windows boundary trusts root links and links above root", TrustedRootLinks);
            yield return ("resource Windows boundary rejects a linked file without changing its target", LinkedFile);
        }
    }

    private static void NormalizeCallback() {
        using Fixture fixture = new();
        string raw = "raw-prefix\0not-for-the-path-api";
        int calls = 0;
        string normalized = InstanceResourcePaths.Normalize(raw, value => {
            calls++;
            Equal(raw, value);
            return Path.Combine(fixture.Scope, "unused", "..", "leaf") + Path.DirectorySeparatorChar;
        });
        Equal(Path.Combine(fixture.Scope, "leaf"), normalized);
        Equal(1, calls);
        // TrimEnd removes both original literal characters, including backslash on Unix.
        Equal(Path.Combine(fixture.Scope, "leaf"), InstanceResourcePaths.Normalize(raw, _ => Path.Combine(fixture.Scope, "leaf") + "\\/"));
        Equal(false, Directory.Exists(Path.Combine(fixture.Scope, "unused")));
        Equal(false, Directory.Exists(normalized));
        Equal(0, Directory.GetFileSystemEntries(fixture.Scope).Length);
        if (!OperatingSystem.IsWindows()) Equal("", InstanceResourcePaths.Normalize("/", Identity));
    }

    private static void NormalizeNulls() {
        using Fixture fixture = new();
        IOException sentinel = new("prefix callback failure");
        int calls = 0;
        Equal(sentinel, Throws<IOException>(() => InstanceResourcePaths.Normalize(null!, value => {
            calls++;
            Equal<string?>(null, value);
            throw sentinel;
        })));
        Equal(1, calls);
        Equal(fixture.Scope, InstanceResourcePaths.Normalize(null!, value => {
            Equal<string?>(null, value);
            return fixture.Scope;
        }));
        Throws<NullReferenceException>(() => InstanceResourcePaths.Normalize(null!, null!));
        Throws<ArgumentNullException>(() => InstanceResourcePaths.Normalize("raw", _ => null!));
        Throws<ArgumentException>(() => InstanceResourcePaths.Normalize("raw", _ => ""));
    }

    private static void RootFirst() {
        List<string> calls = [];
        IOException sentinel = new("root prefix callback failure");
        Equal(sentinel, Throws<IOException>(() => InstanceResourcePaths.EnsureWithin("raw-root", null!, true, value => {
            calls.Add(value);
            throw sentinel;
        })));
        Sequence(["raw-root"], calls);
        calls.Clear();
        Throws<ArgumentNullException>(() => InstanceResourcePaths.EnsureWithin("raw-root", "raw-target", false, value => {
            calls.Add(value);
            return null!;
        }));
        Sequence(["raw-root"], calls);
        Throws<NullReferenceException>(() => InstanceResourcePaths.EnsureWithin(null!, null!, false, null!));
    }

    private static void TargetCallback() {
        using Fixture fixture = new();
        foreach (bool allowRoot in new[] { false, true }) {
            List<string> calls = [];
            IOException sentinel = new("target prefix callback failure");
            Equal(sentinel, Throws<IOException>(() => InstanceResourcePaths.EnsureWithin("raw-root", "raw-target", allowRoot, value => {
                calls.Add(value);
                if (value == "raw-root") return fixture.Scope;
                throw sentinel;
            })));
            Sequence(["raw-root", "raw-target"], calls);
        }
        int sameInputCalls = 0;
        IOException second = new("same input still needs second normalization");
        Equal(second, Throws<IOException>(() => InstanceResourcePaths.EnsureWithin("same-raw", "same-raw", true, _ => {
            if (++sameInputCalls == 1) return fixture.Scope;
            throw second;
        })));
        Equal(2, sameInputCalls);
    }

    private static void OrdinaryPaths() {
        using Fixture fixture = new();
        string file = Path.Combine(fixture.Scope, "file.bin"), directory = Path.Combine(fixture.Scope, "directory");
        File.WriteAllBytes(file, [1, 2, 3]);
        Directory.CreateDirectory(directory);
        using (FileStream held = new(file, FileMode.Open, FileAccess.Read, FileShare.None)) Guard(fixture.Scope, file);
        Guard(fixture.Scope.ToUpperInvariant(), directory);
        string missing = Path.Combine(directory, "missing", "child.bin");
        Guard(fixture.Scope + "\\", missing);
        Equal(false, Directory.Exists(Path.GetDirectoryName(missing)));
        Equal(false, File.Exists(missing));
        Equal(true, new byte[] { 1, 2, 3 }.SequenceEqual(File.ReadAllBytes(file)));
        Equal(2, Directory.GetFileSystemEntries(fixture.Scope).Length);
    }

    private static void RootEquality() {
        using Fixture fixture = new();
        Guard(fixture.Scope, fixture.Scope.ToUpperInvariant() + "\\", true);
        Guard(fixture.Scope + "\\", Path.Combine(fixture.Scope, "."), true);
        Throws<IOException>(() => Guard(fixture.Scope, fixture.Scope, false), Outside);
        Throws<IOException>(() => Guard(fixture.Scope, fixture.Scope.ToUpperInvariant(), false), Outside);
        Equal(0, Directory.GetFileSystemEntries(fixture.Scope).Length);
    }

    private static void ScopeBoundary() {
        using Fixture fixture = new();
        string sibling = Path.Combine(fixture.Root, "scope-sibling", "file.bin");
        Throws<IOException>(() => Guard(fixture.Scope, sibling), Outside);
        Throws<IOException>(() => Guard(fixture.Scope, fixture.Root), Outside);
        Throws<IOException>(() => Guard(fixture.Scope, Path.Combine(fixture.Scope, "..", "outside.bin")), Outside);
        Guard(fixture.Scope, Path.Combine(fixture.Scope, "unused", "..", "inside.bin"));
        Equal(false, Directory.Exists(Path.GetDirectoryName(sibling)));
        Equal(0, Directory.GetFileSystemEntries(fixture.Scope).Length);
    }

    private static void UncheckedRootKind() {
        using Fixture fixture = new();
        string fileRoot = Path.Combine(fixture.Root, "file-root"), missingRoot = Path.Combine(fixture.Root, "missing-root");
        File.WriteAllText(fileRoot, "unchanged root file");
        Guard(fileRoot, Path.Combine(fileRoot, "missing.bin"));
        Guard(missingRoot, Path.Combine(missingRoot, "missing", "child.bin"));
        Guard(missingRoot, missingRoot, true);
        Equal("unchanged root file", File.ReadAllText(fileRoot));
        Equal(false, Directory.Exists(missingRoot));
    }

    private static void DriveRoot() {
        using Fixture fixture = new();
        string drive = Path.GetPathRoot(fixture.Root)!;
        Equal(3, drive.Length);
        Equal(':', drive[1]);
        Equal(drive[..2], InstanceResourcePaths.Normalize(drive, Identity));
        Guard(drive, drive, true);
        Throws<IOException>(() => Guard(drive, drive), Outside);
        string missing = Path.Combine(drive, "PCLLF-ResourcePath-" + Guid.NewGuid().ToString("N"));
        Throws<NullReferenceException>(() => Guard(drive, missing));
        Equal(false, Directory.Exists(missing));
    }

    private static void InnerDirectoryLink() {
        using Fixture fixture = new();
        string target = Path.Combine(fixture.Root, "directory-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep outside scope");
        string link = fixture.DirectoryLink(Path.Combine("scope", "linked"), target);
        Throws<IOException>(() => Guard(fixture.Scope, link), Link);
        Throws<IOException>(() => Guard(fixture.Scope, Path.Combine(link, "keep.txt")), Link);
        Throws<IOException>(() => Guard(fixture.Scope, Path.Combine(link, "missing", "child.bin")), Link);
        Equal("keep outside scope", File.ReadAllText(Path.Combine(target, "keep.txt")));
        Equal(1, Directory.GetFileSystemEntries(target).Length);
    }

    private static void TrustedRootLinks() {
        using Fixture fixture = new();
        string target = Path.Combine(fixture.Root, "trusted-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "trusted root bytes");
        string rootLink = fixture.DirectoryLink("trusted-root", target);
        Guard(rootLink, rootLink, true);
        Throws<IOException>(() => Guard(rootLink, rootLink, false), Outside);
        Guard(rootLink, Path.Combine(rootLink, "keep.txt"));
        Guard(rootLink, Path.Combine(rootLink, "missing.bin"));
        string nested = Path.Combine(target, "nested");
        Directory.CreateDirectory(nested);
        Guard(Path.Combine(rootLink, "nested"), Path.Combine(rootLink, "nested", "missing.bin"));
        Equal(false, File.Exists(Path.Combine(nested, "missing.bin")));
        Equal("trusted root bytes", File.ReadAllText(Path.Combine(target, "keep.txt")));
    }

    private static void LinkedFile() {
        using Fixture fixture = new();
        string target = Path.Combine(fixture.Root, "file-target.bin"), link = Path.Combine(fixture.Scope, "linked.bin");
        File.WriteAllBytes(target, [4, 5, 6]);
        File.CreateSymbolicLink(link, target);
        Equal(true, (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        Throws<IOException>(() => Guard(fixture.Scope, link), Link);
        Equal(true, new byte[] { 4, 5, 6 }.SequenceEqual(File.ReadAllBytes(target)));
    }

    private static string Identity(string value) => value;
    private static void Guard(string root, string fileName, bool allowRoot = false) => InstanceResourcePaths.EnsureWithin(root, fileName, allowRoot, Identity);

    private static T Throws<T>(Action run, string? message = null) where T : Exception {
        try { run(); }
        catch (Exception exception) {
            Equal(typeof(T), exception.GetType());
            if (message is not null) Equal(message, exception.Message);
            return (T) exception;
        }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}].");
    }

    private static void Sequence(IEnumerable<string> expected, IEnumerable<string> actual) {
        if (!expected.SequenceEqual(actual)) throw new Exception("Unexpected prefix callback sequence.");
    }

    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PCLLF-ResourcePaths-" + Guid.NewGuid().ToString("N"));
        internal string Scope => Path.Combine(Root, "scope");
        private readonly List<string> directoryLinks = [];

        internal Fixture() => Directory.CreateDirectory(Scope);

        internal string DirectoryLink(string name, string target) {
            string link = Path.Combine(Root, name);
            Directory.CreateSymbolicLink(link, target);
            directoryLinks.Add(link);
            Equal(true, (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            return link;
        }

        public void Dispose() {
            foreach (string link in directoryLinks) Directory.Delete(link);
            Directory.Delete(Root, true);
        }
    }
}
