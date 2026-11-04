using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PCL.Living.Features;

internal static class InstanceResourcePackageRulesTests {
    private const string MissingPack = "资源包缺少 pack.mcmeta，未替换原文件。";
    private const string MissingShaders = "光影包缺少 shaders 目录，未替换原文件。";

    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("resource package metadata uses the exact root entry name", ExactMetadata);
        yield return ("resource shader packages require only the original root prefix", ShaderPrefixes);
        yield return ("resource package checks do not parse JSON or open entry payloads", PayloadsRemainUnread);
        yield return ("resource package empty archives retain the two checks and other-kind acceptance", EmptyArchives);
        yield return ("resource package opening failures precede every kind branch", OpenBeforeKind);
        yield return ("resource package null and empty filenames retain API errors", NullAndEmpty);
        yield return ("resource package unknown kinds do not read a corrupt central directory", LazyCentralDirectory);
        yield return ("resource package success preserves bytes and allows exclusive reopen", SuccessfulReads);
        yield return ("resource package validation failures preserve bytes and allow exclusive reopen", ValidationFailures);
        yield return ("resource package constructor failures release their file handles", ConstructorFailures);
        yield return ("resource package structure checking is independent of filename extension", ExtensionIsNotAContentRule);
        if (OperatingSystem.IsWindows()) {
            yield return ("resource Windows package reads check raw characters before kind or normalization", WindowsRawCharacters);
            yield return ("resource Windows package reads retain normalization and Unicode whitespace rules", WindowsNormalization);
            yield return ("resource Windows package reads allow shared readers and reject exclusive locks", WindowsSharing);
            yield return ("resource Windows package reads preserve readonly archives", WindowsReadonly);
        } else {
            yield return ("resource Unix package reads retain native filename character behavior", UnixCharacters);
        }
    }

    private static void ExactMetadata() {
        using Fixture fixture = new();
        string exact = fixture.Zip("exact.zip", "pack.mcmeta");
        InstanceResourcePackageRules.ValidatePackage(exact, 2);
        foreach (string entry in new[] { "Pack.mcmeta", "PACK.MCMETA", "nested/pack.mcmeta", "pack.mcmeta/" }) {
            string path = fixture.Zip(Guid.NewGuid().ToString("N") + ".zip", entry);
            Reject(path, 2, MissingPack);
            ExclusiveRead(path);
        }
    }

    private static void ShaderPrefixes() {
        using Fixture fixture = new();
        foreach (string entry in new[] { "shaders/", "SHADERS/program.vert", "ShAdErS/../not-a-shader.txt" }) {
            string path = fixture.Zip(Guid.NewGuid().ToString("N") + ".zip", entry);
            InstanceResourcePackageRules.ValidatePackage(path, 3);
            ExclusiveRead(path);
        }
        foreach (string entry in new[] { "shaders", "nested/shaders/program.vert", @"shaders\program.vert", "shadersx/program.vert", " shaders/program.vert" }) {
            string path = fixture.Zip(Guid.NewGuid().ToString("N") + ".zip", entry);
            Reject(path, 3, MissingShaders);
            ExclusiveRead(path);
        }
    }

    private static void PayloadsRemainUnread() {
        using Fixture fixture = new();
        foreach ((int kind, string entryName) in new[] { (2, "pack.mcmeta"), (3, "shaders/program.vert") }) {
            string path = fixture.Zip("unread-" + kind + ".zip", entryName);
            byte[] bytes = File.ReadAllBytes(path);
            Equal(0x04034b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)));
            bytes[0] = 0; // Central directory remains valid; opening this entry must fail.
            File.WriteAllBytes(path, bytes);
            Snapshot before = Capture(path);
            InstanceResourcePackageRules.ValidatePackage(path, kind);
            using (ZipArchive archive = ZipFile.OpenRead(path)) {
                ZipArchiveEntry entry = archive.GetEntry(entryName) ?? throw new Exception("Fixture entry missing.");
                Throws<InvalidDataException>(() => {
                    using Stream payload = entry.Open();
                    payload.ReadByte();
                });
            }
            Unchanged(path, before);
            ExclusiveRead(path);
        }
    }

    private static void EmptyArchives() {
        using Fixture fixture = new();
        string path = fixture.Zip("empty.zip");
        Reject(path, 2, MissingPack);
        Reject(path, 3, MissingShaders);
        foreach (int kind in new[] { int.MinValue, -1, 0, 1, 4, 5, int.MaxValue })
            InstanceResourcePackageRules.ValidatePackage(path, kind);
        ExclusiveRead(path);
    }

    private static void OpenBeforeKind() {
        using Fixture fixture = new();
        string missing = Path.Combine(fixture.Root, "missing.zip");
        string bad = fixture.Bytes("bad.zip", Encoding.ASCII.GetBytes("not a ZIP"));
        foreach (int kind in new[] { int.MinValue, -1, 0, 1, 2, 3, 4, 5, int.MaxValue }) {
            Throws<FileNotFoundException>(() => InstanceResourcePackageRules.ValidatePackage(missing, kind));
            Throws<InvalidDataException>(() => InstanceResourcePackageRules.ValidatePackage(bad, kind));
        }
        Equal(false, File.Exists(missing));
        ExclusiveRead(bad);
    }

    private static void NullAndEmpty() {
        foreach (int kind in new[] { -1, 2, 3 }) {
            Throws<ArgumentNullException>(() => InstanceResourcePackageRules.ValidatePackage(null!, kind));
            Throws<ArgumentException>(() => InstanceResourcePackageRules.ValidatePackage("", kind));
        }
    }

    private static void LazyCentralDirectory() {
        using Fixture fixture = new();
        string path = fixture.Zip("lazy-central.zip", "owned.txt");
        byte[] bytes = File.ReadAllBytes(path);
        int end = bytes.Length - 22; // The owned fixture has one entry and no archive comment.
        Equal(0x06054b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end, 4)));
        int central = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16, 4)));
        Equal(0x02014b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central, 4)));
        bytes[central] = 0;
        File.WriteAllBytes(path, bytes);
        Snapshot before = Capture(path);
        foreach (int kind in new[] { int.MinValue, -1, 0, 1, 4, 5, int.MaxValue })
            InstanceResourcePackageRules.ValidatePackage(path, kind);
        Throws<InvalidDataException>(() => InstanceResourcePackageRules.ValidatePackage(path, 2));
        Throws<InvalidDataException>(() => InstanceResourcePackageRules.ValidatePackage(path, 3));
        Unchanged(path, before);
        ExclusiveRead(path);
    }

    private static void SuccessfulReads() {
        using Fixture fixture = new();
        string path = fixture.Zip("valid.zip", "pack.mcmeta", "SHADERS/owned.vert");
        Snapshot before = Capture(path);
        foreach (int kind in new[] { 2, 3, -1, 2, 3 }) {
            InstanceResourcePackageRules.ValidatePackage(path, kind);
            Unchanged(path, before);
            ExclusiveRead(path);
        }
    }

    private static void ValidationFailures() {
        using Fixture fixture = new();
        string path = fixture.Zip("unrelated.zip", "not-metadata.txt");
        Snapshot before = Capture(path);
        Reject(path, 2, MissingPack);
        Unchanged(path, before);
        ExclusiveRead(path);
        Reject(path, 3, MissingShaders);
        Unchanged(path, before);
        ExclusiveRead(path);
    }

    private static void ConstructorFailures() {
        using Fixture fixture = new();
        string path = fixture.Bytes("not-a-zip.zip", new byte[] { 1, 2, 3, 4, 5 });
        Snapshot before = Capture(path);
        foreach (int kind in new[] { 2, 3, -1 }) {
            Throws<InvalidDataException>(() => InstanceResourcePackageRules.ValidatePackage(path, kind));
            Unchanged(path, before);
            ExclusiveRead(path);
        }
    }

    private static void ExtensionIsNotAContentRule() {
        using Fixture fixture = new();
        string path = fixture.Zip("archive.bin", "pack.mcmeta", "shaders/owned.vert");
        InstanceResourcePackageRules.ValidatePackage(path, 2);
        InstanceResourcePackageRules.ValidatePackage(path, 3);
        ExclusiveRead(path);
    }

    private static void WindowsRawCharacters() {
        using Fixture fixture = new();
        string owned = fixture.Zip("owned.zip", "pack.mcmeta");
        Snapshot before = Capture(owned);
        // Fixed Framework path characters, not the 41-character filename table.
        char[] invalid = {
            '\0', '\u0001', '\u0002', '\u0003', '\u0004', '\u0005', '\u0006', '\u0007',
            '\u0008', '\u0009', '\u000a', '\u000b', '\u000c', '\u000d', '\u000e', '\u000f',
            '\u0010', '\u0011', '\u0012', '\u0013', '\u0014', '\u0015', '\u0016', '\u0017',
            '\u0018', '\u0019', '\u001a', '\u001b', '\u001c', '\u001d', '\u001e', '\u001f',
            '"', '<', '>', '|'
        };
        Equal(36, invalid.Length);
        foreach (char value in invalid) {
            string collapsed = fixture.Root + "\\bad" + value + @"segment\..\owned.zip";
            foreach (int kind in new[] { 2, -1 })
                Throws<ArgumentException>(() => InstanceResourcePackageRules.ValidatePackage(collapsed, kind));
        }
        Unchanged(owned, before);
        Equal(1, Directory.GetFileSystemEntries(fixture.Root).Length);
        ExclusiveRead(owned);
    }

    private static void WindowsNormalization() {
        using Fixture fixture = new();
        string owned = fixture.Zip("owned.zip", "pack.mcmeta");
        Snapshot before = Capture(owned);
        // These source-order expectations remain subject to the independent CLR4
        // ZIP oracle; this test alone is not evidence of Framework execution.
        foreach (string segment in new[] { "bad?segment", "bad*segment" }) {
            string collapsed = fixture.Root + "\\" + segment + @"\..\owned.zip";
            InstanceResourcePackageRules.ValidatePackage(collapsed, 2);
        }
        foreach (string path in new[] { " ", "\u00a0", "\u2000" }) {
            Throws<ArgumentException>(() => InstanceResourcePackageRules.ValidatePackage(path, 2));
            Throws<ArgumentException>(() => InstanceResourcePackageRules.ValidatePackage(path, -1));
        }
        Unchanged(owned, before);
        Equal(1, Directory.GetFileSystemEntries(fixture.Root).Length);
        ExclusiveRead(owned);
    }

    private static void WindowsSharing() {
        using Fixture fixture = new();
        string path = fixture.Zip("shared.zip", "pack.mcmeta");
        Snapshot before = Capture(path);
        using (FileStream reader = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            InstanceResourcePackageRules.ValidatePackage(path, 2);
        using (FileStream exclusive = new(path, FileMode.Open, FileAccess.Read, FileShare.None)) {
            Throws<IOException>(() => InstanceResourcePackageRules.ValidatePackage(path, 2));
            Throws<IOException>(() => InstanceResourcePackageRules.ValidatePackage(path, -1));
        }
        Unchanged(path, before);
        ExclusiveRead(path);
    }

    private static void WindowsReadonly() {
        using Fixture fixture = new();
        string path = fixture.Zip("readonly.zip", "pack.mcmeta");
        Snapshot before = Capture(path);
        FileAttributes attributes = File.GetAttributes(path);
        try {
            File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
            InstanceResourcePackageRules.ValidatePackage(path, 2);
            Unchanged(path, before);
            Equal(true, (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0);
            ExclusiveRead(path);
        } finally { File.SetAttributes(path, attributes); }
    }

    private static void UnixCharacters() {
        using Fixture fixture = new();
        string path = fixture.Zip(@"pack|?:\name.zip", "pack.mcmeta");
        Snapshot before = Capture(path);
        InstanceResourcePackageRules.ValidatePackage(path, 2);
        Unchanged(path, before);
        ExclusiveRead(path);
        string nulPath = fixture.Root + Path.DirectorySeparatorChar + "bad\0name.zip";
        Throws<ArgumentException>(() => InstanceResourcePackageRules.ValidatePackage(nulPath, 2));
    }

    private static void Reject(string path, int kind, string message) {
        IOException error = Throws<IOException>(() => InstanceResourcePackageRules.ValidatePackage(path, kind));
        Equal(message, error.Message);
    }

    private static T Throws<T>(Action run) where T : Exception {
        try { run(); }
        catch (Exception error) {
            Equal(typeof(T), error.GetType());
            return (T)error;
        }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static Snapshot Capture(string path) => new(File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));

    private static void Unchanged(string path, Snapshot before) {
        Equal(true, before.Bytes.SequenceEqual(File.ReadAllBytes(path)));
        Equal(before.LastWriteUtc, File.GetLastWriteTimeUtc(path));
    }

    private static void ExclusiveRead(string path) {
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
    }

    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}].");
    }

    private sealed record Snapshot(byte[] Bytes, DateTime LastWriteUtc);

    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PCLLF-PackageRules-" + Guid.NewGuid().ToString("N"));

        internal Fixture() => Directory.CreateDirectory(Root);

        internal string Zip(string name, params string[] entries) {
            string path = Path.Combine(Root, name);
            using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: false, entryNameEncoding: new UTF8Encoding(false));
            foreach (string nameInArchive in entries) {
                ZipArchiveEntry entry = archive.CreateEntry(nameInArchive, CompressionLevel.NoCompression);
                if (nameInArchive.EndsWith("/", StringComparison.Ordinal)) continue;
                using Stream payload = entry.Open();
                payload.Write(Encoding.UTF8.GetBytes("not JSON or inspected shader content"));
            }
            return path;
        }

        internal string Bytes(string name, byte[] content) {
            string path = Path.Combine(Root, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        public void Dispose() {
            foreach (string path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
}
