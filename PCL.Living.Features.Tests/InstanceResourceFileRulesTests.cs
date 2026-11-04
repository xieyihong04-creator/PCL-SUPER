using System.Globalization;
using PCL.Living.Features;

internal static class InstanceResourceFileRulesTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("resource file filter preserves four image formats for screenshots only", Screenshots);
        yield return ("resource file filter shares ZIP between resource and shader packs only", Archives);
        yield return ("resource file filter preserves four schematic formats for schematics only", Schematics);
        yield return ("resource file filter preserves full paths dotfiles and last extension behavior", NameShapes);
        yield return ("resource file filter keeps empty names suffix whitespace and unknown kinds", RejectedNamesAndKinds);
        yield return ("resource file filter evaluates null filename before every kind branch", NullBeforeKind);
        yield return ("resource file filter lowercases extensions invariantly under Turkish culture", InvariantExtensions);
        if (OperatingSystem.IsWindows()) {
            yield return ("resource file Windows filter checks the complete raw path against 36 legacy characters", WindowsCharacters);
            yield return ("resource file Windows filter preserves device exceptions and explicit legacy switch", WindowsDevices);
        } else {
            yield return ("resource file Unix filter leaves native extension character behavior unchanged", UnixCharacters);
        }
    }

    private static void Screenshots() {
        foreach (string name in new[] { "Screenshot.png", "photo.JPG", "image.JpEg", "capture.BMP" }) Kinds(name, 1);
    }

    private static void Archives() {
        foreach (string name in new[] { "Resource Pack.zip", "Shader.ZIP", "folder.v1/Pack.ZiP" }) Kinds(name, 2, 3);
    }

    private static void Schematics() {
        foreach (string name in new[] { "house.litematic", "build.SCHEM", "scene.Schematic", "structure.NBT" }) Kinds(name, 4);
    }

    private static void NameShapes() {
        Kinds(@"D:\fixture.folder\Shot.PNG", 1);
        Kinds("folder.with.dot/image.jpeg", 1);
        Kinds("archive.zip.PNG", 1);
        Kinds("shot.png.ZIP", 2, 3);
        Kinds(".png", 1);
        Kinds(".ZIP", 2, 3);
        Kinds(".nbt", 4);
        Kinds(" capture.PNG", 1);
        Kinds("CON.PNG", 1);
        Kinds(".pcl-temp.PNG", 1);
        Kinds(new string('a', 121) + ".png", 1);
        // Wildcards and colons are not part of the old invalid-path character set.
        Kinds("capture?.PNG", 1);
        Kinds("capture*.PNG", 1);
        Kinds("photo.PNG:stream");
        Kinds("photo.PNG:stream.JPG", 1);
        Kinds("archive.ZIP:stream");
        Kinds("archive.ZIP:stream.zip", 2, 3);
        Kinds("photo.PNG:");
        Kinds("C:.ZIP", 2, 3);
        Kinds("a:photo.PNG", 1);
    }

    private static void RejectedNamesAndKinds() {
        foreach (string name in new[] { "", " ", "file", ".", "..", "photo.PNG ", "photo.PNG.", "image.png.tmp", "folder.png/", "folder.png\\" })
            Kinds(name);
        foreach (int kind in new[] { int.MinValue, -1, 5, int.MaxValue }) {
            foreach (string name in new[] { "file.PNG", "file.ZIP", "file.NBT", "" })
                Equal(false, InstanceResourceFileRules.AcceptFile(kind, name));
        }
    }

    private static void NullBeforeKind() {
        foreach (int kind in new[] { int.MinValue, -1, 0, 1, 2, 3, 4, 5, int.MaxValue })
            Throws<NullReferenceException>(() => InstanceResourceFileRules.AcceptFile(kind, null));
    }

    private static void InvariantExtensions() {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Kinds("pack.ZIP", 2, 3);
            Kinds("house.LITEMATIC", 4);
            Kinds("scene.SCHEMATIC", 4);
        } finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void WindowsCharacters() {
        // Independent Framework path catalog, not the different 41 filename characters
        // and not modern Path.GetInvalidPathChars(), which omits some old characters.
        char[] invalid = {
            '\0', '\u0001', '\u0002', '\u0003', '\u0004', '\u0005', '\u0006', '\u0007',
            '\u0008', '\u0009', '\u000a', '\u000b', '\u000c', '\u000d', '\u000e', '\u000f',
            '\u0010', '\u0011', '\u0012', '\u0013', '\u0014', '\u0015', '\u0016', '\u0017',
            '\u0018', '\u0019', '\u001a', '\u001b', '\u001c', '\u001d', '\u001e', '\u001f',
            '"', '<', '>', '|'
        };
        Equal(36, invalid.Length);
        foreach (char value in invalid) {
            string name = "bad" + value + @"directory\valid.PNG";
            Throws<ArgumentException>(() => InstanceResourceFileRules.AcceptFile(1, name));
            Throws<ArgumentException>(() => InstanceResourceFileRules.AcceptFile(int.MaxValue, name));
        }
        Throws<ArgumentException>(() => InstanceResourceFileRules.AcceptFile(1, @"bad|directory\..\valid.PNG"));
    }

    private static void WindowsDevices() {
        const string switchName = "Switch.System.IO.UseLegacyPathHandling";
        AppContext.TryGetSwitch(switchName, out bool previous);
        try {
            foreach (bool legacy in new[] { false, true }) {
                AppContext.SetSwitch(switchName, legacy);
                foreach (string prefix in new[] { @"\\?\", @"\\.\", @"\??\", "//./", "//?/" }) {
                    foreach (string suffix in new[] { @"C:\bad|directory\valid.PNG", "C:\\bad\0name.PNG" }) {
                        string name = prefix + suffix;
                        if (legacy) {
                            Throws<ArgumentException>(() => InstanceResourceFileRules.AcceptFile(1, name));
                            Throws<ArgumentException>(() => InstanceResourceFileRules.AcceptFile(-1, name));
                        } else {
                            Equal(true, InstanceResourceFileRules.AcceptFile(1, name));
                            Equal(false, InstanceResourceFileRules.AcceptFile(-1, name));
                        }
                        // Device recognition is on the raw string; do not trim first.
                        Throws<ArgumentException>(() => InstanceResourceFileRules.AcceptFile(1, " " + name));
                    }
                }
                Throws<NullReferenceException>(() => InstanceResourceFileRules.AcceptFile(-1, null));
            }
        } finally { AppContext.SetSwitch(switchName, previous); }
    }

    private static void UnixCharacters() {
        foreach (string name in new[] { "bad\0name.PNG", "bad|name.PNG", "bad<name.PNG", "bad>name.PNG", "bad\"name.PNG", "bad\tname.PNG", @"bad\directory\image.PNG" })
            Kinds(name, 1);
        Kinds("directory.png/leaf");
        Kinds("directory.PNG\\leaf");
    }

    private static void Kinds(string name, params int[] accepted) {
        for (int kind = 0; kind < 5; kind++) Equal(accepted.Contains(kind), InstanceResourceFileRules.AcceptFile(kind, name));
    }

    private static void Throws<T>(Action run) where T : Exception {
        try { run(); }
        catch (Exception error) {
            Equal(typeof(T), error.GetType());
            return;
        }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}].");
    }
}
