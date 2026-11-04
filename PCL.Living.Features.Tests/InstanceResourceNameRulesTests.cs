using PCL.Living.Features;

internal static class InstanceResourceNameRulesTests {
    private const string Invalid = "文件名无效，请使用不含路径和特殊字符的名称。";
    private const string Reserved = "此名称为系统保留名称。";
    private const string Temporary = "此名称为启动器临时目录保留。";

    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("resource names return the original valid Unicode and mixed-case string", ValidNames);
        yield return ("resource names reject blank and surrounding Unicode whitespace", BlankAndTrimmed);
        yield return ("resource names reject dot-only names and trailing periods", TrailingDots);
        yield return ("resource names apply the 120 UTF16-unit boundary without normalization", Utf16Length);
        yield return ("resource names reject the DOS device catalog with case and extensions", ReservedDevices);
        yield return ("resource names retain the exact launcher temporary prefix", TemporaryNames);
        yield return ("resource names preserve general-error priority over reserved-name errors", ErrorPriority);
        if (OperatingSystem.IsWindows())
            yield return ("resource names reject the independent 41-character Windows table", WindowsInvalidCharacters);
        else
            yield return ("resource names preserve Unix invalid-character and accepted-character behavior", UnixCharacters);
    }

    private static void ValidNames() {
        foreach (string sample in new[] {
            "World A", "世界🌍_1", "Mixed CASE", "resource pack.zip", "folder.name", "..backup", ".hidden",
            "inside\u00a0space", "inside\u2000space", "COM0", "COM10", "COM1x", "LPT0", "LPT10", "LPT9x",
            "CONSOLE", "PRN1", "NUL1", "auxiliary", ".CON", "CON .txt", "COM¹", "LPT²",
            ".pcl", ".pcl_backup", ".pclbackup", "pcl-stage", "x.pcl-stage", "..pcl-stage"
        }) {
            string value = new(sample.ToCharArray());
            string actual = InstanceResourceNameRules.SafeName(value);
            Equal(sample, actual);
            Equal(true, ReferenceEquals(value, actual));
        }
    }

    private static void BlankAndTrimmed() {
        foreach (string? value in new[] { null, "", " ", "\t", "\r\n", "\u00a0", "\u2000", " world", "world ", "world\t", "\u00a0world", "world\u2000" })
            Reject(value!, Invalid);
    }

    private static void TrailingDots() {
        foreach (string value in new[] { ".", "..", "...", "World.", "World..", "archive.zip." }) Reject(value, Invalid);
        string valueAtLimit = new('a', 119);
        Reject(valueAtLimit + ".", Invalid);
    }

    private static void Utf16Length() {
        string ascii = new('a', 120), chinese = new('界', 120);
        Equal(ascii, InstanceResourceNameRules.SafeName(ascii));
        Equal(chinese, InstanceResourceNameRules.SafeName(chinese));
        Reject(ascii + "a", Invalid);
        Reject(chinese + "界", Invalid);
        string sixtyPairs = string.Concat(Enumerable.Repeat("\U0001f30d", 60));
        Equal(120, sixtyPairs.Length);
        Equal(sixtyPairs, InstanceResourceNameRules.SafeName(sixtyPairs));
        Reject(sixtyPairs + "a", Invalid); // 61 displayed symbols, but 121 UTF16 units.
        Reject(sixtyPairs + "\U0001f30d", Invalid);
    }

    private static void ReservedDevices() {
        foreach (string name in new[] {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        }) {
            Reject(name, Reserved);
            Reject(name.ToLowerInvariant(), Reserved);
            Reject(name + ".txt", Reserved);
            Reject(name.ToLowerInvariant() + ".tar.gz", Reserved);
        }
        Reject("COM1." + new string('a', 115), Reserved); // Exactly 120 units.
    }

    private static void TemporaryNames() {
        foreach (string value in new[] { ".pcl-", ".pcl-import-fixture", ".PCL-cache", ".PcL-世界" }) Reject(value, Temporary);
        Reject(".pcl-" + new string('a', 115), Temporary);
    }

    private static void ErrorPriority() {
        foreach (string value in new[] {
            "CON.", "COM1.txt.", "NUL ", "CON.txt\0data", "AUX/child", "COM1." + new string('a', 116),
            ".pcl-temp ", ".pcl-.", ".PCL-cache/child", ".pcl-" + new string('a', 116)
        }) Reject(value, Invalid);
    }

    private static void WindowsInvalidCharacters() {
        // This finite Windows catalog is independent of the production Path API lookup.
        char[] invalid = {
            '\0', '\u0001', '\u0002', '\u0003', '\u0004', '\u0005', '\u0006', '\u0007',
            '\u0008', '\u0009', '\u000a', '\u000b', '\u000c', '\u000d', '\u000e', '\u000f',
            '\u0010', '\u0011', '\u0012', '\u0013', '\u0014', '\u0015', '\u0016', '\u0017',
            '\u0018', '\u0019', '\u001a', '\u001b', '\u001c', '\u001d', '\u001e', '\u001f',
            '"', '<', '>', '|', ':', '*', '?', '\\', '/'
        };
        Equal(41, invalid.Length);
        Equal(true, invalid.OrderBy(value => value).SequenceEqual(Path.GetInvalidFileNameChars().OrderBy(value => value)));
        foreach (char value in invalid) Reject("valid" + value + "name", Invalid);
    }

    private static void UnixCharacters() {
        Reject("valid\0name", Invalid);
        Reject("valid/name", Invalid);
        foreach (char value in new[] { '"', '<', '>', '|', ':', '*', '?', '\\', '\u0001', '\u001f' }) {
            string name = "valid" + value + "name";
            Equal(name, InstanceResourceNameRules.SafeName(name));
        }
    }

    private static void Reject(string value, string message) {
        try { InstanceResourceNameRules.SafeName(value); }
        catch (Exception error) {
            Equal(typeof(IOException), error.GetType());
            Equal(message, error.Message);
            return;
        }
        throw new Exception("Expected IOException: " + message);
    }

    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}].");
    }
}
