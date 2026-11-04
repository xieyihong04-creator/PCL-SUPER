using System.Globalization;
using PCL.Living.Features;

internal static class InstanceResourcePathTextTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("resource text comparison preserves instance null and length boundaries", InstanceBoundaries);
        yield return ("resource text comparison counts embedded NUL rather than terminating", EmbeddedNull);
        yield return ("resource text comparison preserves raw UTF16 units", Utf16Units);
        yield return ("resource text comparison remains independent of current culture", CultureBoundary);
        if (OperatingSystem.IsWindows()) {
            yield return ("resource Windows ordinal comparison retains recorded CLR4 Unicode results", RecordedFrameworkResults);
            yield return ("resource Windows Unicode root equality and prefix use the production policy", UnicodeGuards);
        } else {
            yield return ("resource non-Windows text comparison retains the platform String APIs", NonWindows);
        }
    }

    private static void InstanceBoundaries() {
        Equal(true, InstanceResourcePathText.SamePathText("", ""));
        Equal(false, InstanceResourcePathText.SamePathText("", null));
        Equal(false, InstanceResourcePathText.SamePathText("a", ""));
        Equal(false, InstanceResourcePathText.SamePathText("a", "AA"));
        Equal(true, InstanceResourcePathText.SamePathText("C:\\Fixture\\A", "c:\\fixture\\a"));
        Equal(true, InstanceResourcePathText.StartsWithPathText("", ""));
        Equal(true, InstanceResourcePathText.StartsWithPathText("abc", ""));
        Equal(false, InstanceResourcePathText.StartsWithPathText("ab", "ABC"));
        Equal(true, InstanceResourcePathText.StartsWithPathText("ABC\\leaf", "abc\\"));
        Throws<NullReferenceException>(() => InstanceResourcePathText.SamePathText(null!, null));
        Throws<NullReferenceException>(() => InstanceResourcePathText.SamePathText(null!, ""));
        Throws<NullReferenceException>(() => InstanceResourcePathText.StartsWithPathText(null!, null!));
        Equal("value", Throws<ArgumentNullException>(() => InstanceResourcePathText.StartsWithPathText("", null!)).ParamName);
    }

    private static void EmbeddedNull() {
        Equal(true, InstanceResourcePathText.SamePathText("A\0B", "a\0b"));
        Equal(false, InstanceResourcePathText.SamePathText("A\0B", "a\0c"));
        Equal(true, InstanceResourcePathText.SamePathText("\0a", "\0A"));
        Equal(true, InstanceResourcePathText.StartsWithPathText("A\0Btail", "a\0b"));
        Equal(false, InstanceResourcePathText.StartsWithPathText("A\0Btail", "a\0c"));
        Equal(false, InstanceResourcePathText.StartsWithPathText("A\0", "a\0b"));
    }

    private static void Utf16Units() {
        Equal(true, InstanceResourcePathText.SamePathText("\ud800A", "\ud800a"));
        Equal(true, InstanceResourcePathText.SamePathText("\udc00A", "\udc00a"));
        Equal(false, InstanceResourcePathText.SamePathText("\ud800", "\ud801"));
        Equal(false, InstanceResourcePathText.SamePathText("\ud800", "\udc00"));
        Equal(true, InstanceResourcePathText.StartsWithPathText("\ud800a-tail", "\ud800A"));
        Equal(true, InstanceResourcePathText.StartsWithPathText("\U00010400A-tail", "\U00010400a"));
    }

    private static void CultureBoundary() {
        CultureInfo original = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Equal(true, InstanceResourcePathText.SamePathText("I", "i"));
            Equal(false, InstanceResourcePathText.SamePathText("\u0130", "i"));
            Equal(true, InstanceResourcePathText.StartsWithPathText("INDEX\\leaf", "index\\"));
        } finally { CultureInfo.CurrentCulture = original; }
    }

    private static void RecordedFrameworkResults() {
        // Independent b12b6cf Windows CLR4 artifact: these five pairs were false.
        // The expanded oracle continues to measure real String.Equals/StartsWith;
        // no modern String API is used as the Windows expected-value generator.
        foreach ((string first, string second) in new[] { ("\u0130", "i"), ("Σ", "ς"), ("K", "K"), ("ß", "SS"), ("\U00010400", "\U00010428") }) {
            Equal(false, InstanceResourcePathText.SamePathText(first, second));
            Equal(false, InstanceResourcePathText.SamePathText(second, first));
            // Equal-length pairs exercise prefix comparison with an actual remainder.
            if (first.Length == second.Length) {
                Equal(false, InstanceResourcePathText.StartsWithPathText(first + "\\leaf", second + "\\"));
                Equal(false, InstanceResourcePathText.StartsWithPathText(new string('a', 64) + first + "\\leaf", new string('A', 64) + second + "\\"));
            }
        }
    }

    private static void UnicodeGuards() {
        string fixture = Path.Combine(Path.GetTempPath(), "PCLLF-PathText-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try {
            foreach ((string first, string second) in new[] { ("Σ", "ς"), ("\U00010400", "\U00010428") }) {
                string root = Path.Combine(fixture, "root-" + first), variant = Path.Combine(fixture, "root-" + second);
                Throws<IOException>(() => Guard(root, variant, true), "资源路径超出了当前目录。");
                Throws<IOException>(() => Guard(root, Path.Combine(variant, "missing", "leaf")), "资源路径超出了当前目录。");
                Guard(root, root, true);
                Guard(root, Path.Combine(root, "missing", "leaf"));
            }
            Equal(0, Directory.GetFileSystemEntries(fixture).Length);
        } finally { Directory.Delete(fixture); }
    }

    private static void NonWindows() {
        foreach ((string first, string second) in new[] { ("Σ", "ς"), ("\U00010400", "\U00010428"), ("K", "K") }) {
            Equal(first.Equals(second, StringComparison.OrdinalIgnoreCase), InstanceResourcePathText.SamePathText(first, second));
            Equal((first + "tail").StartsWith(second, StringComparison.OrdinalIgnoreCase), InstanceResourcePathText.StartsWithPathText(first + "tail", second));
        }
    }

    private static void Guard(string root, string fileName, bool allowRoot = false) => InstanceResourcePaths.EnsureWithin(root, fileName, allowRoot, value => value);
    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}].");
    }
    private static T Throws<T>(Action run, string? message = null) where T : Exception {
        try { run(); }
        catch (Exception error) {
            Equal(typeof(T), error.GetType());
            if (message is not null) Equal(message, error.Message);
            return (T)error;
        }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
