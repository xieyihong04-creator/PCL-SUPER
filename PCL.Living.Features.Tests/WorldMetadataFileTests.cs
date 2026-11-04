using System.Globalization;
using System.IO.Compression;
using System.Text;
using PCL.Living.Features;

internal static class WorldMetadataFileTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() => [
        ("world file: basic and detail production entry points", ModernFile),
        ("world file: legacy basic and detail distinction", LegacyFile),
        ("world file: missing, absent Data and compressed size limit", MissingAndSize),
        ("world file: exact 8 MiB is accepted before path validation", ExactSize),
        ("world file: path guard parameters, ordering and exception identity", PathGuard),
        ("world file: corrupt gzip and NBT release handles", CorruptFiles),
        ("world file: shared writer and read-only file remain untouched", SharedReadOnlyFile),
        ("world caption: all fields and exact CRLF order", FullCaption),
        ("world caption: absent and whitespace fields", AbsentCaption),
        ("world caption: all game modes and difficulties", NamedModes),
        ("world caption: unknown negative values and nullable false", UnknownModes),
        ("world caption: missing spawn axis and day boundaries", SpawnAndDay),
        ("world caption: current culture and local date", CaptionCulture),
        ("world caption: missing versus corrupt fallback and logging", CaptionFallback),
        ("world caption: folder and logging failures stay observable", OuterFailures)
    ];

    private static void ModernFile() {
        using Fixture fixture = new();
        fixture.Write(FullWorld());
        byte[] before = File.ReadAllBytes(fixture.FileName);
        DateTime lastWrite = File.GetLastWriteTimeUtc(fixture.FileName);
        WorldMetadataInfo basic = Required(fixture.Read(false));
        Equal("Fixture world", basic.Name);
        Equal(new DateTime(2024, 10, 27, 3, 33, 20, DateTimeKind.Utc), basic.LastPlayedUtc);
        Equal<int?>(null, basic.GameMode);
        Equal<long?>(null, basic.Seed);
        WorldMetadataInfo details = Required(fixture.Read(true));
        Equal("1.20.1", details.VersionName);
        Equal<int?>(3465, details.DataVersion);
        Equal<int?>(1, details.GameMode);
        Equal<int?>(3, details.Difficulty);
        Equal<bool?>(true, details.Hardcore);
        Equal<bool?>(false, details.AllowCommands);
        Equal<bool?>(true, details.WasModded);
        Equal<bool?>(false, details.Snapshot);
        Equal<long?>(-9876543210123, details.Seed);
        Equal<int?>(-32, details.SpawnX);
        Equal<int?>(70, details.SpawnY);
        Equal<int?>(256, details.SpawnZ);
        Equal<long?>(48000, details.DayTime);
        Equal<long?>(92000, details.Time);
        Equal(true, before.SequenceEqual(File.ReadAllBytes(fixture.FileName)));
        Equal(lastWrite, File.GetLastWriteTimeUtc(fixture.FileName));
        fixture.AssertReleased();
    }

    private static void LegacyFile() {
        using Fixture fixture = new();
        fixture.Write(World(writer => { Text(writer, "LevelName", "Old world"); Long(writer, "RandomSeed", -42); }));
        Equal<WorldMetadataInfo?>(null, fixture.Read(false));
        WorldMetadataInfo details = Required(fixture.Read(true));
        Equal("Old world", details.Name);
        Equal(DateTime.MinValue, details.LastPlayedUtc);
        Equal<long?>(-42, details.Seed);
        fixture.AssertReleased();
    }

    private static void MissingAndSize() {
        using Fixture fixture = new();
        Action<string, string> unexpected = (_, _) => throw new Exception("Path guard should not run for an absent or oversized file.");
        Equal<WorldMetadataInfo?>(null, WorldMetadataFiles.Read(fixture.Folder, true, unexpected));
        using (FileStream file = File.Create(fixture.FileName)) file.SetLength(8 * 1024 * 1024 + 1);
        Equal<WorldMetadataInfo?>(null, WorldMetadataFiles.Read(fixture.Folder, true, unexpected));
        Equal<WorldMetadataInfo?>(null, WorldMetadataFiles.Read(fixture.Folder, false, unexpected));
        fixture.Write([10, 0, 0, 0]); // A valid root compound without Data.
        Equal<WorldMetadataInfo?>(null, fixture.Read(true));
        fixture.AssertReleased();
    }

    private static void ExactSize() {
        using Fixture fixture = new();
        fixture.Write(FullWorld());
        using (FileStream file = new(fixture.FileName, FileMode.Open, FileAccess.Write)) file.SetLength(8 * 1024 * 1024);
        int called = 0;
        IOException rejected = new("fixture boundary guard");
        Same(rejected, Throws<IOException>(() => WorldMetadataFiles.Read(fixture.Folder, false, (_, _) => { called++; throw rejected; })));
        Equal(1, called); // Exactly 8 MiB reaches validation, rather than silently returning null.
        Equal("Fixture world", Required(fixture.Read(false)).Name);
        fixture.AssertReleased();
    }

    private static void PathGuard() {
        using Fixture fixture = new();
        fixture.Write(FullWorld());
        int called = 0;
        WorldMetadataInfo data = Required(WorldMetadataFiles.Read(fixture.Folder, true, (root, path) => {
            Equal(fixture.Folder, root);
            Equal(fixture.FileName, path);
            using FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            called++;
        }));
        Equal(1, called);
        Equal("Fixture world", data.Name);
        UnauthorizedAccessException denied = new("fixture path policy");
        Same(denied, Throws<UnauthorizedAccessException>(() => WorldMetadataFiles.Read(fixture.Folder, true, (_, _) => throw denied)));
        fixture.AssertReleased();
    }

    private static void CorruptFiles() {
        using Fixture fixture = new();
        File.WriteAllText(fixture.FileName, "invalid gzip");
        Throws<InvalidDataException>(() => fixture.Read(true));
        fixture.AssertReleased();
        fixture.Write([10, 0, 0, 10, 0, 4, (byte)'D', (byte)'a', (byte)'t', (byte)'a', 8]);
        Throws<EndOfStreamException>(() => fixture.Read(true));
        fixture.AssertReleased();
    }

    private static void SharedReadOnlyFile() {
        using Fixture fixture = new();
        fixture.Write(FullWorld());
        byte[] before = File.ReadAllBytes(fixture.FileName);
        using (FileStream writer = new(fixture.FileName, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) {
            Equal("Fixture world", Required(fixture.Read(false)).Name);
        }
        FileAttributes attributes = File.GetAttributes(fixture.FileName);
        try {
            File.SetAttributes(fixture.FileName, attributes | FileAttributes.ReadOnly);
            Equal("Fixture world", Required(fixture.Read(true)).Name);
            Equal(true, fixture.Describe().Contains("世界种子：-9876543210123", StringComparison.Ordinal));
        } finally {
            File.SetAttributes(fixture.FileName, attributes);
        }
        Equal(true, before.SequenceEqual(File.ReadAllBytes(fixture.FileName)));
        fixture.AssertReleased();
    }

    private static void FullCaption() {
        using Fixture fixture = new();
        fixture.Write(FullWorld());
        string date = new DateTime(2024, 10, 27, 3, 33, 20, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        Equal(string.Join("\r\n", new[] {
            "世界名称：Fixture world", "存档文件夹：" + new DirectoryInfo(fixture.Folder).Name,
            "游戏版本：1.20.1", "数据版本：3465", "最后游玩：" + date,
            "游戏模式：创造", "难度：困难", "极限模式：开启", "允许作弊：关闭", "模组环境记录：使用过",
            "世界种子：-9876543210123", "世界出生点：-32, 70, 256", "游戏日：第 2 天", "世界累计刻数：92000",
            "", "位置：" + Path.GetFullPath(fixture.Folder)
        }), fixture.Describe());
    }

    private static void AbsentCaption() {
        using Fixture fixture = new();
        fixture.Write(World(writer => {
            Text(writer, "LevelName", " \t");
            Header(writer, 10, "Version"); Text(writer, "Name", " "); Byte(writer, "Snapshot", 1); writer.Write((byte)0);
        }));
        Equal(FolderLine(fixture) + "\r\n\r\n位置：" + fixture.Folder, fixture.Describe());
    }

    private static void NamedModes() {
        using Fixture fixture = new();
        string[] modes = ["生存", "创造", "冒险", "旁观"];
        string[] difficulties = ["和平", "简单", "普通", "困难"];
        for (int index = 0; index < 4; index++) {
            fixture.Write(World(writer => { Int(writer, "GameType", index); Byte(writer, "Difficulty", (byte)index); }));
            Equal(FolderLine(fixture) + "\r\n游戏模式：" + modes[index] + "\r\n难度：" + difficulties[index] + "\r\n\r\n位置：" + fixture.Folder, fixture.Describe());
        }
    }

    private static void UnknownModes() {
        using Fixture fixture = new();
        fixture.Write(World(writer => {
            Int(writer, "GameType", -1); Int(writer, "Difficulty", 8);
            Byte(writer, "hardcore", 0); Byte(writer, "allowCommands", 1); Byte(writer, "WasModded", 0);
            Long(writer, "RandomSeed", long.MinValue); Long(writer, "Time", long.MaxValue);
            Header(writer, 10, "Version"); Text(writer, "Name", "snapshot"); Byte(writer, "Snapshot", 1); writer.Write((byte)0);
        }));
        Equal(FolderLine(fixture) + "\r\n游戏版本：snapshot（快照）\r\n游戏模式：未知（-1）\r\n难度：未知（8）\r\n极限模式：关闭\r\n允许作弊：开启\r\n模组环境记录：未标记\r\n世界种子：-9223372036854775808\r\n世界累计刻数：9223372036854775807\r\n\r\n位置：" + fixture.Folder, fixture.Describe());
    }

    private static void SpawnAndDay() {
        using Fixture fixture = new();
        foreach (long dayTime in new long[] { -1, 0, 23999, 24000, long.MaxValue }) {
            fixture.Write(World(writer => { Int(writer, "SpawnX", 1); Int(writer, "SpawnY", 2); Long(writer, "DayTime", dayTime); }));
            string day = dayTime < 0 ? "" : "\r\n游戏日：第 " + (dayTime / 24000) + " 天";
            Equal(FolderLine(fixture) + day + "\r\n\r\n位置：" + fixture.Folder, fixture.Describe());
        }
    }

    private static void CaptionCulture() {
        using Fixture fixture = new();
        fixture.Write(World(writer => {
            Long(writer, "LastPlayed", 1730000000000); Int(writer, "DataVersion", -1); Int(writer, "GameType", -2);
            Long(writer, "RandomSeed", -42); Int(writer, "SpawnX", -1); Int(writer, "SpawnY", 2); Int(writer, "SpawnZ", 3);
        }));
        CultureInfo previous = CultureInfo.CurrentCulture;
        try {
            CultureInfo culture = (CultureInfo)CultureInfo.GetCultureInfo("th-TH").Clone();
            culture.NumberFormat.NegativeSign = "~";
            CultureInfo.CurrentCulture = culture;
            string date = new DateTime(2024, 10, 27, 3, 33, 20, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", culture);
            Equal(FolderLine(fixture) + "\r\n数据版本：~1\r\n最后游玩：" + date + "\r\n游戏模式：未知（~2）\r\n世界种子：~42\r\n世界出生点：~1, 2, 3\r\n\r\n位置：" + fixture.Folder, fixture.Describe());
        } finally {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static void CaptionFallback() {
        using Fixture fixture = new();
        List<(Exception Error, string Message)> logs = [];
        Action<Exception, string> warn = (error, message) => logs.Add((error, message));
        Equal(FolderLine(fixture) + "\r\n未找到可读取的 level.dat 元数据。\r\n\r\n位置：" + fixture.Folder,
            WorldMetadataFiles.Describe(fixture.Folder, ValidatePath, warn));
        Equal(0, logs.Count);
        File.WriteAllText(fixture.FileName, "invalid gzip");
        string corrupt = WorldMetadataFiles.Describe(fixture.Folder, ValidatePath, warn);
        Equal(FolderLine(fixture) + "\r\n无法读取 level.dat；文件可能正在写入、已损坏或格式不受支持。\r\n\r\n位置：" + fixture.Folder, corrupt);
        Equal(1, logs.Count);
        Equal(true, logs[0].Error is InvalidDataException);
        Equal("读取本地存档详情失败", logs[0].Message);
        logs.Clear();
        IOException denied = new("fixture validation error");
        Equal(corrupt, WorldMetadataFiles.Describe(fixture.Folder, (_, _) => throw denied, warn));
        Equal(1, logs.Count);
        Same(denied, logs[0].Error);
        fixture.AssertReleased();
    }

    private static void OuterFailures() {
        int called = 0;
        Throws<ArgumentNullException>(() => WorldMetadataFiles.Describe(null!, ValidatePath, (_, _) => called++));
        Equal(0, called);
        using Fixture fixture = new();
        File.WriteAllText(fixture.FileName, "invalid gzip");
        InvalidOperationException loggingError = new("fixture logger failed");
        Same(loggingError, Throws<InvalidOperationException>(() => WorldMetadataFiles.Describe(fixture.Folder, ValidatePath, (_, _) => throw loggingError)));
        fixture.AssertReleased();
    }

    private static string FolderLine(Fixture fixture) => "存档文件夹：" + new DirectoryInfo(fixture.Folder).Name;
    private static WorldMetadataInfo Required(WorldMetadataInfo? value) => value ?? throw new Exception("Expected readable world metadata.");
    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}].");
    }
    private static void Same(object expected, object actual) {
        if (!ReferenceEquals(expected, actual)) throw new Exception("Exception identity changed.");
    }
    private static T Throws<T>(Action run) where T : Exception {
        try { run(); } catch (T exception) { return exception; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    // This callback validates the fixture boundary. Windows launcher regressions separately
    // exercise the real InstanceResourceStore.EnsureWithin adapter, including reparse points.
    private static void ValidatePath(string root, string path) {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new IOException("Fixture path escaped.");
    }

    private static byte[] FullWorld() => World(writer => {
        Text(writer, "LevelName", "Fixture world"); Long(writer, "LastPlayed", 1730000000000);
        Int(writer, "GameType", 1); Byte(writer, "Difficulty", 3); Byte(writer, "hardcore", 1); Byte(writer, "allowCommands", 0); Byte(writer, "WasModded", 1);
        Int(writer, "DataVersion", 3465); Int(writer, "SpawnX", -32); Int(writer, "SpawnY", 70); Int(writer, "SpawnZ", 256); Long(writer, "DayTime", 48000); Long(writer, "Time", 92000);
        Header(writer, 10, "Version"); Text(writer, "Name", "1.20.1"); Byte(writer, "Snapshot", 0); writer.Write((byte)0);
        Header(writer, 10, "WorldGenSettings"); Long(writer, "seed", -9876543210123); writer.Write((byte)0);
    });
    private static byte[] World(Action<BinaryWriter> data) {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        Header(writer, 10, ""); Header(writer, 10, "Data"); data(writer); writer.Write((byte)0); writer.Write((byte)0);
        writer.Flush();
        return stream.ToArray();
    }
    private static void Header(BinaryWriter writer, byte kind, string name) { writer.Write(kind); String(writer, name); }
    private static void String(BinaryWriter writer, string value) {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((byte)(bytes.Length >> 8)); writer.Write((byte)bytes.Length); writer.Write(bytes);
    }
    private static void Text(BinaryWriter writer, string name, string value) { Header(writer, 8, name); String(writer, value); }
    private static void Byte(BinaryWriter writer, string name, byte value) { Header(writer, 1, name); writer.Write(value); }
    private static void Int(BinaryWriter writer, string name, int value) {
        Header(writer, 3, name);
        for (int shift = 24; shift >= 0; shift -= 8) writer.Write((byte)(value >> shift));
    }
    private static void Long(BinaryWriter writer, string name, long value) {
        Header(writer, 4, name);
        for (int shift = 56; shift >= 0; shift -= 8) writer.Write((byte)(value >> shift));
    }

    private sealed class Fixture : IDisposable {
        internal string Folder { get; } = Path.Combine(Path.GetTempPath(), "PclWorldMetadataFiles-" + Guid.NewGuid().ToString("N"));
        internal string FileName => Path.Combine(Folder, "level.dat");
        internal Fixture() => Directory.CreateDirectory(Folder);
        internal void Write(byte[] nbt) {
            using FileStream file = File.Create(FileName);
            using GZipStream zip = new(file, CompressionMode.Compress);
            zip.Write(nbt, 0, nbt.Length);
        }
        internal WorldMetadataInfo? Read(bool details) => WorldMetadataFiles.Read(Folder, details, ValidatePath);
        internal string Describe() => WorldMetadataFiles.Describe(Folder, ValidatePath, (error, _) => throw new Exception("Unexpected metadata failure.", error));
        internal void AssertReleased() {
            using FileStream exclusive = new(FileName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        public void Dispose() => Directory.Delete(Folder, recursive: true);
    }
}
