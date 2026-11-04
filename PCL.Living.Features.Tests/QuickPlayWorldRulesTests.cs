using PCL.Living.Features;

internal static class QuickPlayWorldRulesTests {
    private const string InvalidWorld = "存档文件夹名称无效。";
    private const string MissingWorld = "该存档已移动、删除或尚未完整生成，请刷新存档列表。";
    private static readonly string[] LaunchFlags = ["--quickPlaySingleplayer", "--quickPlayMultiplayer", "--quickPlayRealms", "--server", "--port"];

    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("Quick Play path rejects invalid identifiers before folder or guard", InvalidIdentifiers);
        yield return ("Quick Play path keeps platform invalid-character and path API ordering", PlatformInvalidCharacters);
        yield return ("Quick Play path delegates exact root and candidate before existence", GuardOrder);
        yield return ("Quick Play path checks existence after original guard callback", GuardBeforeExistence);
        yield return ("Quick Play path explains missing world and missing level.dat without creating", MissingFiles);
        yield return ("Quick Play path accepts Unicode and spaces without reading or changing level.dat", ValidWorld);
        yield return ("Quick Play path returns caller relative layout without normalization", RelativePath);
        yield return ("Quick Play path leaves null folder exception to Path.Combine", NullFolder);
        if (OperatingSystem.IsWindows()) yield return ("Quick Play Windows path errors retain Framework type, order and device handling", WindowsLegacyPaths);
        yield return ("Quick Play argument removal mutates original list and keeps unrelated order", MixedArguments);
        yield return ("Quick Play argument removal revisits adjacent and repeated flags", AdjacentArguments);
        yield return ("Quick Play argument removal keeps case and equals-form distinctions", OrdinalArguments);
        yield return ("Quick Play argument removal consumes one non-double-dash value only", ArgumentValues);
        yield return ("Quick Play argument names are enumerated before list mutation", NamesFirst);
        yield return ("Quick Play argument null collection exceptions keep original ordering", NullCollections);
        yield return ("Quick Play argument null tokens preserve original partial mutation", NullTokens);
        yield return ("Quick Play argument empty and unmatched inputs stay unchanged", EmptyArguments);
    }

    private static void InvalidIdentifiers() {
        int checks = 0;
        foreach (string? identifier in new[] { null, "", " ", "\t", "\r\n", ".", "..", Path.Combine("nested", "world"), "world" + Path.DirectorySeparatorChar }) {
            Throws<IOException>(() => QuickPlayWorldRules.WorldPath(null!, identifier, (_, _) => checks++), InvalidWorld);
        }
        Equal(0, checks);
    }

    private static void PlatformInvalidCharacters() {
        using Fixture fixture = new();
        int checks = 0;
        foreach (char character in Path.GetInvalidFileNameChars()) {
            string identifier = "A" + character + "B";
            Exception actual = ExceptionFrom(() => QuickPlayWorldRules.WorldPath(fixture.Root, identifier, (_, _) => checks++));
            bool legacyPathError = OperatingSystem.IsWindows() && (character < 32 || character is '"' or '<' or '>' or '|');
            if (!legacyPathError) {
                Equal(typeof(IOException), actual.GetType());
                Equal(InvalidWorld, actual.Message);
            } else {
                Equal(typeof(ArgumentException), actual.GetType());
            }
        }
        Equal(0, checks);
        Equal(0, Directory.GetFileSystemEntries(fixture.Root).Length);
    }

    private static void GuardOrder() {
        using Fixture fixture = new();
        IOException sentinel = new("original path guard rejected candidate");
        int checks = 0;
        Equal(sentinel, Throws<IOException>(() => QuickPlayWorldRules.WorldPath(fixture.Root, "Uncreated", (root, world) => {
            checks++;
            Equal(Path.Combine(fixture.Root, "saves"), root);
            Equal(Path.Combine(fixture.Root, "saves", "Uncreated"), world);
            Equal(false, Directory.Exists(world));
            throw sentinel;
        })));
        Equal(1, checks);
        Equal(false, Directory.Exists(Path.Combine(fixture.Root, "saves")));
    }

    private static void GuardBeforeExistence() {
        using Fixture fixture = new();
        string expected = Path.Combine(fixture.Root, "saves", "Created by fixture callback");
        string actual = QuickPlayWorldRules.WorldPath(fixture.Root, "Created by fixture callback", (_, world) => {
            Equal(expected, world);
            Directory.CreateDirectory(world);
            File.WriteAllBytes(Path.Combine(world, "level.dat"), [255]);
        });
        Equal(expected, actual);
    }

    private static void MissingFiles() {
        using Fixture fixture = new();
        int checks = 0;
        Action<string, string> guard = (_, _) => checks++;
        string missing = Path.Combine(fixture.Root, "saves", "Missing");
        Throws<IOException>(() => QuickPlayWorldRules.WorldPath(fixture.Root, "Missing", guard), MissingWorld);
        Equal(false, Directory.Exists(missing));
        Directory.CreateDirectory(missing);
        Throws<IOException>(() => QuickPlayWorldRules.WorldPath(fixture.Root, "Missing", guard), MissingWorld);
        Equal(false, File.Exists(Path.Combine(missing, "level.dat")));
        Directory.CreateDirectory(Path.Combine(missing, "level.dat"));
        Throws<IOException>(() => QuickPlayWorldRules.WorldPath(fixture.Root, "Missing", guard), MissingWorld);
        Equal(3, checks);
    }

    private static void ValidWorld() {
        using Fixture fixture = new();
        string world = fixture.World("World with spaces 中文");
        string metadata = Path.Combine(world, "level.dat");
        byte[] original = File.ReadAllBytes(metadata);
        using (FileStream held = new(metadata, FileMode.Open, FileAccess.Read, FileShare.None)) {
            Equal(world, QuickPlayWorldRules.WorldPath(fixture.Root, "World with spaces 中文", (_, candidate) => Equal(world, candidate)));
        }
        Equal(true, original.SequenceEqual(File.ReadAllBytes(metadata)));
        Equal(1, Directory.GetFileSystemEntries(world).Length);
    }

    private static void RelativePath() {
        using Fixture fixture = new();
        fixture.World("Relative");
        string relativeRoot = Path.GetRelativePath(Environment.CurrentDirectory, fixture.Root);
        string expected = Path.Combine(relativeRoot, "saves", "Relative");
        Equal(expected, QuickPlayWorldRules.WorldPath(relativeRoot, "Relative", (root, world) => {
            Equal(Path.Combine(relativeRoot, "saves"), root);
            Equal(expected, world);
        }));
    }

    private static void NullFolder() {
        int checks = 0;
        Throws<ArgumentNullException>(() => QuickPlayWorldRules.WorldPath(null!, "Valid name", (_, _) => checks++));
        Equal(0, checks);
        Throws<IOException>(() => QuickPlayWorldRules.WorldPath(null!, null, null!), InvalidWorld);
    }

    private static void WindowsLegacyPaths() {
        int checks = 0;
        Action<string, string> guard = (_, _) => checks++;
        const string switchName = "Switch.System.IO.UseLegacyPathHandling";
        AppContext.TryGetSwitch(switchName, out bool previous);
        try {
            foreach (bool legacy in new[] { false, true }) {
                AppContext.SetSwitch(switchName, legacy);
                foreach (string identifier in new[] { "bad\0name", "bad|name", "dir/world<name", "dir\\world\"name" })
                    Equal(typeof(ArgumentException), ExceptionFrom(() => QuickPlayWorldRules.WorldPath(null!, identifier, guard)).GetType());
                foreach (string identifier in new[] { @"\\?\C:\bad|name", @"\\.\C:\bad|name", @"\??\C:\bad|name", "//./C:/bad|name" }) {
                    if (legacy) Equal(typeof(ArgumentException), ExceptionFrom(() => QuickPlayWorldRules.WorldPath(null!, identifier, guard)).GetType());
                    else Throws<IOException>(() => QuickPlayWorldRules.WorldPath(null!, identifier, guard), InvalidWorld);
                }
                Equal(typeof(ArgumentException), ExceptionFrom(() => QuickPlayWorldRules.WorldPath("bad|folder", "Valid name", guard)).GetType());
                Throws<IOException>(() => QuickPlayWorldRules.WorldPath("bad|folder", ".", guard), InvalidWorld);
                Throws<IOException>(() => QuickPlayWorldRules.WorldPath("bad|folder", "dir/world", guard), InvalidWorld);
                Throws<ArgumentNullException>(() => QuickPlayWorldRules.WorldPath(null!, "Valid name", guard));
            }
        } finally { AppContext.SetSwitch(switchName, previous); }
        Equal(0, checks);
    }

    private static void MixedArguments() {
        List<string> arguments = ["--keep", "keep value", "--quickPlayMultiplayer", "example.invalid", "--server", "server.invalid", "--port", "25565", "--quickPlayPath", "user log.json", "trailing"];
        List<string> same = arguments;
        QuickPlayWorldRules.RemoveArguments(arguments, LaunchFlags);
        Equal(true, ReferenceEquals(same, arguments));
        Sequence(["--keep", "keep value", "--quickPlayPath", "user log.json", "trailing"], arguments);
    }

    private static void AdjacentArguments() {
        List<string> arguments = ["--server", "--port", "25565", "--server", "one", "--server", "two", "--quickPlayRealms"];
        QuickPlayWorldRules.RemoveArguments(arguments, LaunchFlags);
        Sequence([], arguments);
        arguments.AddRange(["keep", "--quickPlaySingleplayer", "--unknown", "unknown value"]);
        QuickPlayWorldRules.RemoveArguments(arguments, LaunchFlags);
        Sequence(["keep", "--unknown", "unknown value"], arguments);
    }

    private static void OrdinalArguments() {
        List<string> arguments = ["--Server", "Capital", "--server=example.invalid", "equal value", "--port=25565", "--QUICKPLAYREALMS", "case value", "--server", "remove"];
        QuickPlayWorldRules.RemoveArguments(arguments, LaunchFlags);
        Sequence(["--Server", "Capital", "--server=example.invalid", "equal value", "--port=25565", "--QUICKPLAYREALMS", "case value"], arguments);
    }

    private static void ArgumentValues() {
        foreach (string value in new[] { "", "-", "-Xmx1G", "World with spaces", " --literal" }) {
            List<string> arguments = ["before", "--server", value, "after"];
            QuickPlayWorldRules.RemoveArguments(arguments, LaunchFlags);
            Sequence(["before", "after"], arguments);
        }
        List<string> guarded = ["--server", "--other", "value"];
        QuickPlayWorldRules.RemoveArguments(guarded, LaunchFlags);
        Sequence(["--other", "value"], guarded);
    }

    private static void NamesFirst() {
        List<string> arguments = ["--server", "keep original"];
        InvalidOperationException sentinel = new("names enumeration failed");
        Equal(sentinel, Throws<InvalidOperationException>(() => QuickPlayWorldRules.RemoveArguments(arguments, FailingNames(sentinel))));
        Sequence(["--server", "keep original"], arguments);
        Equal(sentinel, Throws<InvalidOperationException>(() => QuickPlayWorldRules.RemoveArguments([], FailingNames(sentinel))));
    }

    private static void NullCollections() {
        Throws<ArgumentNullException>(() => QuickPlayWorldRules.RemoveArguments(null!, null!));
        List<string> original = ["keep"];
        Throws<ArgumentNullException>(() => QuickPlayWorldRules.RemoveArguments(original, null!));
        Sequence(["keep"], original);
        int reads = 0;
        IEnumerable<string> Names() { reads++; yield return "--server"; }
        Throws<NullReferenceException>(() => QuickPlayWorldRules.RemoveArguments(null!, Names()));
        Equal(1, reads);
    }

    private static void NullTokens() {
        List<string> unmatched = [null!, "keep"];
        QuickPlayWorldRules.RemoveArguments(unmatched, LaunchFlags);
        Sequence([null, "keep"], unmatched);
        List<string> following = ["--server", null!, "keep"];
        Throws<NullReferenceException>(() => QuickPlayWorldRules.RemoveArguments(following, LaunchFlags));
        Sequence([null, "keep"], following);
        List<string> matched = [null!, "remove value", "keep"];
        QuickPlayWorldRules.RemoveArguments(matched, [null!]);
        Sequence(["keep"], matched);
    }

    private static void EmptyArguments() {
        List<string> empty = [];
        QuickPlayWorldRules.RemoveArguments(empty, LaunchFlags);
        Sequence([], empty);
        List<string> original = ["--keep", "value", "tail"];
        QuickPlayWorldRules.RemoveArguments(original, []);
        Sequence(["--keep", "value", "tail"], original);
        QuickPlayWorldRules.RemoveArguments(original, ["--absent", "--absent"]);
        Sequence(["--keep", "value", "tail"], original);
    }

    private static IEnumerable<string> FailingNames(Exception error) { yield return "--server"; throw error; }
    private static void Sequence(string?[] expected, List<string> actual) {
        Equal(expected.Length, actual.Count);
        for (int index = 0; index < expected.Length; index++) Equal(expected[index], actual[index]);
    }
    private static Exception ExceptionFrom(Action action) {
        try { action(); } catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected an exception.");
    }
    private static T Throws<T>(Action action, string? message = null) where T : Exception {
        try { action(); } catch (T error) {
            if (message is not null) Equal(message, error.Message);
            return error;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PclQuickPlayRules-" + Guid.NewGuid().ToString("N"));
        internal Fixture() => Directory.CreateDirectory(Root);
        internal string World(string identifier) {
            string world = Path.Combine(Root, "saves", identifier);
            Directory.CreateDirectory(world);
            File.WriteAllBytes(Path.Combine(world, "level.dat"), [255, 0, 13, 10]);
            return world;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
