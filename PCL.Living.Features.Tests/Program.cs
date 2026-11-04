using System.Buffers.Binary;
using System.Text;
using PCL.Living.Features;
using PCL.Living.Features.Tests;

internal static class Program {
    private const int Budget = 16 * 1024 * 1024;
    private const string LengthError = "存档元数据长度无效。";
    private const string ListError = "存档元数据列表长度无效。";
    private const string DepthError = "存档元数据层级过深。";
    private const string BudgetError = "存档元数据超过读取限制。";
    private const string KindError = "未知的存档元数据类型。";

    private static int Main(string[] args) {
        // Factories keep unselected groups from constructing or enumerating cases.
        // Canonical order matches the original all-tests runner; aliases only
        // select groups, so overlapping/repeated selectors cannot duplicate cases.
        (string Key, Func<IEnumerable<(string Name, Action Run)>> Cases)[] groups = [
            ("world-metadata", () => WorldMetadataCases().Concat(WorldMetadataFileTests.Cases())),
            ("local-data", LocalGameDataTests.Cases),
            ("personal-options", () => PersonalOptionsRulesTests.Cases()
                .Concat(PersonalOptionsFileTests.Cases()).Concat(PersonalOptionsFlowTests.Cases())),
            ("keybindings", () => InstanceKeybindingFileTests.Cases()
                .Concat(InstanceKeybindingReaderTests.Cases()).Concat(InstanceKeybindingViewTests.Cases())),
            ("quick-play", () => QuickPlayWorldRulesTests.Cases()
                .Concat(QuickPlayHistoryTests.Cases()).Concat(QuickPlayFlowTests.Cases())),
            ("resource-entry", InstanceResourceEntryTests.Cases),
            ("resource-paths", () => InstanceResourcePathTests.Cases()
                .Concat(InstanceResourcePathTextTests.Cases()).Concat(InstanceResourcePathCompatibilityTests.Cases())),
            ("resource-files", () => InstanceResourceKindRulesTests.Cases()
                .Concat(InstanceResourceNameRulesTests.Cases()).Concat(InstanceResourceFileRulesTests.Cases())
                .Concat(InstanceResourcePackageRulesTests.Cases()).Concat(InstanceResourcePipelineTests.Cases()).Concat(ResourceRecyclePipelineTests.Cases()).Concat(InstanceResourceOnlineTests.Cases())),
            ("resource-metadata", () => InstanceResourceDetailsRulesTests.Cases()
                .Concat(InstanceResourcePackMetadataTests.Cases()).Concat(InstanceResourceMetadataRuntimeTests.Cases())),
            ("world-backups", WorldBackupPipelineTests.Cases),
            ("environment-history", EnvironmentHistoryPipelineTests.Cases),
            ("instance-clone", InstanceClonePipelineTests.Cases),
            ("mod-dependencies", ModDependencyInspectorPipelineTests.Cases),
            ("wave2-modpack-map", () => Wave2Tests.Cases)
        ];
        if (args.Length == 1 && args[0] == "--list-groups") {
            foreach (var group in groups) Console.WriteLine(group.Key);
            Console.WriteLine("resources (alias for all resource-* groups)");
            return 0;
        }

        HashSet<string> selected = new(StringComparer.Ordinal);
        if (args.Length == 0) {
            selected.UnionWith(groups.Select(group => group.Key));
        } else {
            for (int index = 0; index < args.Length; index++) {
                if (args[index] != "--group" || ++index >= args.Length)
                    return SelectionError("Expected --group <exact-key> or --list-groups.");
                string key = args[index];
                if (key == "resources") {
                    selected.UnionWith(groups.Where(group => group.Key.StartsWith("resource-", StringComparison.Ordinal))
                        .Select(group => group.Key));
                } else if (groups.Any(group => group.Key == key)) {
                    selected.Add(key);
                } else {
                    return SelectionError($"Unknown or empty migration group: [{key}].");
                }
            }
        }

        var selectedGroups = groups.Where(group => selected.Contains(group.Key)).ToArray();
        (string Name, Action Run)[] tests = selectedGroups.SelectMany(group => group.Cases()).ToArray();
        if (tests.Length == 0) return SelectionError("The migration test selection contains no cases.");
        Console.WriteLine($"Selected migration groups ({selectedGroups.Length}/{groups.Length}): {string.Join(", ", selectedGroups.Select(group => group.Key))}.");
        Console.WriteLine("Only selected groups are executed; unselected groups are not counted as passed.");
        int failures = 0;
        foreach ((string name, Action run) in tests) {
            try {
                run();
                Console.WriteLine($"PASS {name}");
            } catch (Exception exception) {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception}");
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} migration test groups passed.");
        return failures == 0 ? 0 : 1;
    }

    private static int SelectionError(string message) {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine("Use --list-groups to see exact keys; repeat --group to select multiple groups. No arguments runs all groups.");
        return 2;
    }

    private static IEnumerable<(string Name, Action Run)> WorldMetadataCases() {
        return [
            ("modern details match Test-WorldDetails.ps1", ModernDetails),
            ("basic modern metadata stops before details", ModernBasic),
            ("legacy details without LastPlayed", LegacyDetails),
            ("legacy basic metadata requires LastPlayed", LegacyBasic),
            ("empty Data and absent fields", EmptyData),
            ("LastPlayed without LevelName", TimestampWithoutName),
            ("nonpositive and duplicate LastPlayed", NonpositiveTimestamps),
            ("LastPlayed range and UTC", TimestampRange),
            ("signed integer and long extrema", SignedValues),
            ("legacy and modern seed precedence", SeedPrecedence),
            ("byte booleans and Difficulty representations", ByteFields),
            ("known keys require exact case and tag types", ExactKeysAndKinds),
            ("root shape and first Data behavior", RootShape),
            ("skip all NBT payload types", UnknownPayloads),
            ("skip unknown values inside known sections", ExtraSections),
            ("Unicode, empty and invalid UTF-8 strings", Strings),
            ("basic short circuit preserves unread tail", BasicShortCircuit),
            ("truncated headers and payloads throw", TruncatedData),
            ("unknown tag and list element types throw", UnknownKinds),
            ("invalid array lengths and multiplication", ArrayLengths),
            ("list length limits and empty lists", ListLengths),
            ("Skip compound and list depth boundary", SkipDepth),
            ("extra section Skip starts at depth one", ExtraSkipDepth),
            ("exact 16 MiB parsing budget", ExactBudget),
            ("budget exhaustion at following tag", ExceededBudget),
            ("caller owns nonseekable stream on every outcome", StreamOwnership),
            ("null input contract", NullInput)
        ];
    }

    private static void ModernDetails() {
        byte[] fixture = ModernFixture(out _);
        byte[] before = (byte[]) fixture.Clone();
        WorldMetadataInfo data = Required(Read(fixture, true));
        Equal("Fixture world", data.Name);
        Equal(new DateTime(2024, 10, 27, 3, 33, 20, DateTimeKind.Utc), data.LastPlayedUtc);
        Equal(DateTimeKind.Utc, data.LastPlayedUtc.Kind);
        Equal("1.20.1", data.VersionName);
        Equal<int?>(3465, data.DataVersion);
        Equal<int?>(1, data.GameMode);
        Equal<int?>(3, data.Difficulty);
        Equal<bool?>(true, data.Hardcore);
        Equal<bool?>(false, data.AllowCommands);
        Equal<bool?>(true, data.WasModded);
        Equal<bool?>(false, data.Snapshot);
        Equal<long?>(-9876543210123, data.Seed);
        Equal<int?>(-32, data.SpawnX);
        Equal<int?>(70, data.SpawnY);
        Equal<int?>(256, data.SpawnZ);
        Equal<long?>(48000, data.DayTime);
        Equal<long?>(92000, data.Time);
        Equal(true, fixture.SequenceEqual(before), "input bytes changed");
    }

    private static void ModernBasic() {
        byte[] fixture = ModernFixture(out int basicEnd);
        using MemoryStream stream = new(fixture, writable: false);
        WorldMetadataInfo data = Required(WorldMetadataReader.Read(stream, false));
        Equal("Fixture world", data.Name);
        Equal(new DateTime(2024, 10, 27, 3, 33, 20, DateTimeKind.Utc), data.LastPlayedUtc);
        Equal((long) basicEnd, stream.Position);
        NoDetails(data);
    }

    private static void LegacyDetails() {
        WorldMetadataInfo data = Required(Read(LegacyFixture(), true));
        Equal("Old world", data.Name);
        Equal<long?>(-42, data.Seed);
        Equal(DateTime.MinValue, data.LastPlayedUtc);
        Equal(DateTimeKind.Unspecified, data.LastPlayedUtc.Kind);
        Equal<int?>(null, data.GameMode);
        Equal<int?>(null, data.Difficulty);
        Equal<string?>(null, data.VersionName);
    }

    private static void LegacyBasic() {
        Equal<WorldMetadataInfo?>(null, Read(LegacyFixture(), false));
    }

    private static void EmptyData() {
        byte[] fixture = World(_ => { });
        WorldMetadataInfo data = Required(Read(fixture, true));
        Equal<string?>(null, data.Name);
        Equal(DateTime.MinValue, data.LastPlayedUtc);
        NoDetails(data);
        Equal<WorldMetadataInfo?>(null, Read(fixture, false));
    }

    private static void TimestampWithoutName() {
        byte[] fixture = World(writer => writer.Long("LastPlayed", 1));
        foreach (bool details in new[] { false, true }) {
            WorldMetadataInfo data = Required(Read(fixture, details));
            Equal<string?>(null, data.Name);
            Equal(new DateTime(1970, 1, 1, 0, 0, 0, 1, DateTimeKind.Utc), data.LastPlayedUtc);
        }
    }

    private static void NonpositiveTimestamps() {
        foreach (long timestamp in new[] { 0L, -1, long.MinValue }) {
            byte[] fixture = World(writer => {
                writer.Text("LevelName", "No date");
                writer.Long("LastPlayed", timestamp);
            });
            Equal(DateTime.MinValue, Required(Read(fixture, true)).LastPlayedUtc);
            Equal<WorldMetadataInfo?>(null, Read(fixture, false));
        }

        byte[] duplicates = World(writer => {
            writer.Long("LastPlayed", 1);
            writer.Long("LastPlayed", 0);
            writer.Long("LastPlayed", -1);
        });
        Equal(new DateTime(1970, 1, 1, 0, 0, 0, 1, DateTimeKind.Utc), Required(Read(duplicates, true)).LastPlayedUtc);
    }

    private static void TimestampRange() {
        WorldMetadataInfo last = Required(Read(World(writer => writer.Long("LastPlayed", 253402300799999)), true));
        Equal(new DateTime(9999, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc), last.LastPlayedUtc);
        foreach (long timestamp in new[] { 253402300800000L, long.MaxValue }) {
            Throws<ArgumentOutOfRangeException>(() => Read(World(writer => writer.Long("LastPlayed", timestamp)), true));
        }
    }

    private static void SignedValues() {
        WorldMetadataInfo data = Required(Read(World(writer => {
            writer.Int("GameType", -1);
            writer.Int("Difficulty", int.MinValue);
            writer.Int("DataVersion", int.MaxValue);
            writer.Int("SpawnX", int.MinValue);
            writer.Int("SpawnY", -64);
            writer.Int("SpawnZ", int.MaxValue);
            writer.Long("RandomSeed", long.MinValue);
            writer.Long("DayTime", -1);
            writer.Long("Time", long.MaxValue);
        }), true));
        Equal<int?>(-1, data.GameMode);
        Equal<int?>(int.MinValue, data.Difficulty);
        Equal<int?>(int.MaxValue, data.DataVersion);
        Equal<int?>(int.MinValue, data.SpawnX);
        Equal<int?>(-64, data.SpawnY);
        Equal<int?>(int.MaxValue, data.SpawnZ);
        Equal<long?>(long.MinValue, data.Seed);
        Equal<long?>(-1, data.DayTime);
        Equal<long?>(long.MaxValue, data.Time);
    }

    private static void SeedPrecedence() {
        Equal<long?>(-42, Required(Read(World(writer => {
            writer.Long("RandomSeed", -42);
            writer.Long("RandomSeed", 100);
        }), true)).Seed);

        foreach (bool modernFirst in new[] { false, true }) {
            WorldMetadataInfo data = Required(Read(World(writer => {
                if (!modernFirst) writer.Long("RandomSeed", 100);
                writer.Compound("WorldGenSettings");
                writer.Long("seed", -99);
                writer.End();
                if (modernFirst) writer.Long("RandomSeed", 100);
            }), true));
            Equal<long?>(-99, data.Seed);
        }

        WorldMetadataInfo repeated = Required(Read(World(writer => {
            writer.Compound("WorldGenSettings");
            writer.Long("seed", -1);
            writer.Long("seed", -2);
            writer.End();
            writer.Compound("WorldGenSettings");
            writer.Long("seed", -3);
            writer.End();
        }), true));
        Equal<long?>(-3, repeated.Seed);
    }

    private static void ByteFields() {
        WorldMetadataInfo data = Required(Read(World(writer => {
            writer.Byte("Difficulty", 255);
            writer.Byte("hardcore", 255);
            writer.Byte("allowCommands", 2);
            writer.Byte("WasModded", 0);
            writer.Compound("Version");
            writer.Byte("Snapshot", 128);
            writer.End();
        }), true));
        Equal<int?>(255, data.Difficulty);
        Equal<bool?>(true, data.Hardcore);
        Equal<bool?>(true, data.AllowCommands);
        Equal<bool?>(false, data.WasModded);
        Equal<bool?>(true, data.Snapshot);

        Equal<int?>(-10, Required(Read(World(writer => {
            writer.Byte("Difficulty", 3);
            writer.Int("Difficulty", -10);
        }), true)).Difficulty);
        Equal<int?>(3, Required(Read(World(writer => {
            writer.Int("Difficulty", -10);
            writer.Byte("Difficulty", 3);
        }), true)).Difficulty);
    }

    private static void ExactKeysAndKinds() {
        WorldMetadataInfo data = Required(Read(World(writer => {
            writer.Int("LevelName", 4);
            writer.Text("levelname", "wrong case");
            writer.Int("LastPlayed", 1);
            writer.Long("lastplayed", 1);
            writer.Byte("GameType", 1);
            writer.Header(2, "Difficulty");
            writer.Raw([0, 3]);
            writer.Long("DataVersion", 42);
            writer.Int("Hardcore", 1);
            writer.Byte("AllowCommands", 1);
            writer.Byte("wasModded", 1);
            writer.Int("RandomSeed", -42);
            writer.Long("SpawnX", 1);
            writer.Text("SpawnY", "70");
            writer.Long("SpawnZ", -32);
            writer.Int("DayTime", 24000);
            writer.Int("Time", 1);
            writer.Compound("version");
            writer.Text("Name", "wrong section case");
            writer.End();
            writer.Compound("Version");
            writer.Text("name", "wrong key case");
            writer.Int("Snapshot", 1);
            writer.End();
            writer.Compound("WorldGenSettings");
            writer.Long("Seed", -1);
            writer.Int("seed", -2);
            writer.End();
        }), true));
        Equal<string?>(null, data.Name);
        Equal(DateTime.MinValue, data.LastPlayedUtc);
        NoDetails(data);
    }

    private static void RootShape() {
        Equal<WorldMetadataInfo?>(null, Read([0], true));
        Equal<WorldMetadataInfo?>(null, Read([8], true));
        Equal<WorldMetadataInfo?>(null, Read([10, 0, 0, 0], true));

        NbtWriter writer = new();
        writer.Compound("arbitrary root name");
        writer.Int("Data", 42);
        writer.Compound("data");
        writer.Text("LevelName", "wrong case");
        writer.End();
        writer.Compound("Unrelated");
        writer.Compound("Data");
        writer.Text("LevelName", "nested Data is ignored");
        writer.End();
        writer.End();
        writer.Compound("Data");
        writer.Text("LevelName", "Root Data");
        writer.End();
        // The reader returns at the end of Data; the root does not need to end.
        Equal("Root Data", Required(Read(writer.Bytes(), true)).Name);

        NbtWriter first = new();
        first.Compound("");
        first.Compound("Data");
        first.End();
        first.Compound("Data");
        first.Text("LevelName", "second Data is ignored");
        first.Long("LastPlayed", 1);
        first.End();
        first.End();
        Equal<string?>(null, Required(Read(first.Bytes(), true)).Name);
        Equal<WorldMetadataInfo?>(null, Read(first.Bytes(), false));
    }

    private static void UnknownPayloads() {
        byte[] fixture = World(writer => {
            writer.Byte("UnknownByte", 255);
            writer.Header(2, "UnknownShort");
            writer.Raw([255, 254]);
            writer.Int("UnknownInt", -42);
            writer.Long("UnknownLong", long.MinValue);
            writer.Header(5, "UnknownFloat");
            writer.Raw([0x3f, 0x80, 0, 0]);
            writer.Header(6, "UnknownDouble");
            writer.Raw([0xbf, 0xf0, 0, 0, 0, 0, 0, 0]);
            writer.ByteArray("UnknownBytes", [0, 128, 255]);
            writer.Text("UnknownText", "忽略 🎮");
            writer.List("UnknownInts", 3, 3);
            writer.IntPayload(-1);
            writer.IntPayload(0);
            writer.IntPayload(int.MaxValue);
            writer.List("UnknownCompounds", 10, 2);
            writer.Text("LevelName", "nested name");
            writer.End();
            writer.Compound("nested");
            writer.Int("SpawnX", 100);
            writer.End();
            writer.End();
            writer.List("UnknownLists", 9, 1);
            writer.Raw([12]);
            writer.IntPayload(2);
            writer.IntPayload(2);
            writer.LongPayload(long.MinValue);
            writer.LongPayload(long.MaxValue);
            writer.IntPayload(0);
            writer.Compound("UnknownCompound");
            writer.Int("SpawnX", 123);
            writer.ByteArray("data", [1, 2]);
            writer.End();
            writer.IntArray("UnknownIntArray", [-1, int.MinValue, int.MaxValue]);
            writer.LongArray("UnknownLongArray", [-1, long.MinValue, long.MaxValue]);
            writer.ByteArray("EmptyByteArray", []);
            writer.IntArray("EmptyIntArray", []);
            writer.LongArray("EmptyLongArray", []);
            writer.Text("EmptyString", "");
            writer.Text("LevelName", "After all unknown payloads");
            writer.Long("LastPlayed", 1);
        });
        foreach (bool details in new[] { false, true }) {
            WorldMetadataInfo data = Required(Read(fixture, details));
            Equal("After all unknown payloads", data.Name);
            Equal<int?>(null, data.SpawnX);
        }
    }

    private static void ExtraSections() {
        WorldMetadataInfo data = Required(Read(World(writer => {
            writer.Compound("Version");
            writer.Compound("Unknown");
            writer.Text("Name", "nested version name ignored");
            writer.Byte("Snapshot", 1);
            writer.End();
            writer.List("UnknownList", 11, 1);
            writer.IntPayload(2);
            writer.IntPayload(-32);
            writer.IntPayload(64);
            writer.Text("Name", "first");
            writer.Text("Name", "25w01a");
            writer.Byte("Snapshot", 1);
            writer.End();
            writer.Compound("WorldGenSettings");
            writer.Compound("Unknown");
            writer.Long("seed", 123);
            writer.End();
            writer.LongArray("Ignored", [-1, 0, 1]);
            writer.Long("seed", -42);
            writer.End();
        }), true));
        Equal("25w01a", data.VersionName);
        Equal<bool?>(true, data.Snapshot);
        Equal<long?>(-42, data.Seed);
    }

    private static void Strings() {
        string unicode = "世界 Café \0 🎮";
        Equal(unicode, Required(Read(World(writer => writer.Text("LevelName", unicode)), true)).Name);
        Equal("", Required(Read(World(writer => writer.Text("LevelName", "")), true)).Name);
        Equal("\ufffd", Required(Read(World(writer => {
            writer.Header(8, "LevelName");
            writer.Raw([0, 1, 255]);
        }), true)).Name);
        string largest = new('a', ushort.MaxValue);
        Equal(largest, Required(Read(World(writer => writer.Text("LevelName", largest)), true)).Name);
    }

    private static void BasicShortCircuit() {
        foreach (bool nameFirst in new[] { false, true }) {
            foreach (string name in new[] { "", "Quick read" }) {
                NbtWriter writer = StartWorld();
                if (nameFirst) writer.Text("LevelName", name);
                writer.Long("LastPlayed", 1);
                if (!nameFirst) writer.Text("LevelName", name);
                int expectedPosition = writer.Length;
                writer.Raw([255]); // The unread tail is deliberately corrupt.
                using MemoryStream stream = new(writer.Bytes(), writable: false);
                Equal(name, Required(WorldMetadataReader.Read(stream, false)).Name);
                Equal((long) expectedPosition, stream.Position);
                Equal(255, stream.ReadByte());
                Throws<EndOfStreamException>(() => Read(writer.Bytes(), true));
            }
        }

        NbtWriter noEnd = StartWorld();
        noEnd.Text("LevelName", "No closing tags");
        noEnd.Long("LastPlayed", 1);
        Equal("No closing tags", Required(Read(noEnd.Bytes(), false)).Name);
        Throws<EndOfStreamException>(() => Read(noEnd.Bytes(), true));
    }

    private static void TruncatedData() {
        // A lone non-compound root returns null; a compound header must be complete.
        foreach (byte[] fixture in new byte[][] { [], [10], [10, 0], [10, 0, 1], [10, 0, 0] }) {
            Throws<EndOfStreamException>(() => Read(fixture, true));
        }

        byte[] valid = ModernFixture(out _);
        // Removing the root terminator is allowed: it was never read by the original parser.
        Equal("Fixture world", Required(Read(valid[..^1], true)).Name);
        Throws<EndOfStreamException>(() => Read(valid[..^2], true));

        foreach (byte kind in new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }) {
            NbtWriter writer = StartWorld();
            writer.Header(kind, "truncated");
            Throws<EndOfStreamException>(() => Read(writer.Bytes(), true));
        }

        foreach (string key in new[] { "SpawnX", "LastPlayed", "LevelName" }) {
            NbtWriter writer = StartWorld();
            writer.Header(key == "SpawnX" ? 3 : key == "LastPlayed" ? 4 : 8, key);
            writer.Raw([0]);
            Throws<EndOfStreamException>(() => Read(writer.Bytes(), true));
        }

        NbtWriter array = StartWorld();
        array.Header(7, "array");
        array.IntPayload(3);
        array.Raw([1, 2]);
        Throws<EndOfStreamException>(() => Read(array.Bytes(), true));

        NbtWriter list = StartWorld();
        list.List("list", 3, 2);
        list.IntPayload(1);
        Throws<EndOfStreamException>(() => Read(list.Bytes(), true));
    }

    private static void UnknownKinds() {
        foreach (byte kind in new byte[] { 13, 127, 255 }) {
            Throws<InvalidDataException>(() => Read(World(writer => writer.Header(kind, "unknown")), true), KindError);
            Throws<InvalidDataException>(() => Read(World(writer => writer.List("list", kind, 1)), true), KindError);
        }
        NbtWriter root = new();
        root.Compound("");
        root.Header(13, "unknown");
        Throws<InvalidDataException>(() => Read(root.Bytes(), true), KindError);
    }

    private static void ArrayLengths() {
        foreach (byte kind in new byte[] { 7, 11, 12 }) {
            foreach (int count in new[] { -1, int.MinValue, int.MaxValue }) {
                byte[] fixture = World(writer => {
                    writer.Header(kind, "array");
                    writer.IntPayload(count);
                });
                Throws<InvalidDataException>(() => Read(fixture, true), LengthError);
            }
        }
    }

    private static void ListLengths() {
        foreach (int count in new[] { -1, int.MinValue, 100001, int.MaxValue }) {
            Throws<InvalidDataException>(() => Read(World(writer => writer.List("list", 0, count)), true), ListError);
        }
        // Original Skip accepts TAG_End list elements and never examines the kind for empty lists.
        foreach (int count in new[] { 0, 1, 100000 }) {
            Equal("After list", Required(Read(World(writer => {
                writer.List("list", 0, count);
                writer.Text("LevelName", "After list");
            }), true)).Name);
        }
        Equal("Empty unknown list", Required(Read(World(writer => {
            writer.List("list", 255, 0);
            writer.Text("LevelName", "Empty unknown list");
        }), true)).Name);
    }

    private static void SkipDepth() {
        Equal("After nested compound", Required(Read(World(writer => {
            NestedCompound(writer, 63);
            writer.Text("LevelName", "After nested compound");
        }), true)).Name);
        Throws<InvalidDataException>(() => Read(World(writer => NestedCompound(writer, 64)), true), DepthError);

        Equal("After nested list", Required(Read(World(writer => {
            NestedList(writer, 63);
            writer.Text("LevelName", "After nested list");
        }), true)).Name);
        Throws<InvalidDataException>(() => Read(World(writer => NestedList(writer, 64)), true), DepthError);
    }

    private static void ExtraSkipDepth() {
        foreach (string section in new[] { "Version", "WorldGenSettings" }) {
            Equal("After extra", Required(Read(World(writer => {
                writer.Compound(section);
                NestedCompound(writer, 62);
                writer.End();
                writer.Text("LevelName", "After extra");
            }), true)).Name);
            Throws<InvalidDataException>(() => Read(World(writer => {
                writer.Compound(section);
                NestedCompound(writer, 63);
                writer.End();
            }), true), DepthError);
        }
    }

    private static void ExactBudget() {
        byte[] fixture = BudgetFixture(Budget);
        Equal(Budget, fixture.Length);
        // Every Read gets a new budget, even when the same static entry point is reused.
        for (int attempt = 0; attempt < 2; attempt++) {
            using MemoryStream stream = new(fixture, writable: false);
            Required(WorldMetadataReader.Read(stream, true));
            Equal((long) Budget, stream.Position);
        }
    }

    private static void ExceededBudget() {
        byte[] fixture = BudgetFixture(Budget + 1);
        using MemoryStream stream = new(fixture, writable: false);
        Throws<InvalidDataException>(() => WorldMetadataReader.Read(stream, true), BudgetError);
        // The budget check runs before the attempted byte read.
        Equal((long) Budget, stream.Position);
        Equal(0, stream.ReadByte());
    }

    private static void StreamOwnership() {
        foreach (byte[] fixture in new byte[][] { LegacyFixture(), [0], [10] }) {
            using ReadByteOnlyStream stream = new(fixture);
            try {
                WorldMetadataReader.Read(stream, true);
            } catch (EndOfStreamException) {
                // A malformed stream also belongs to the caller.
            }
            Equal(false, stream.WasDisposed);
            Equal(true, stream.CanRead);
            stream.Dispose();
            Equal(true, stream.WasDisposed);
        }

        using MemoryStream positioned = new();
        positioned.WriteByte(255);
        positioned.Write(LegacyFixture());
        positioned.Position = 1;
        Equal("Old world", Required(WorldMetadataReader.Read(positioned, true)).Name);
    }

    private static void NullInput() {
        foreach (bool details in new[] { false, true }) {
            Throws<ArgumentNullException>(() => WorldMetadataReader.Read(null!, details));
        }
    }

    private static byte[] ModernFixture(out int basicEnd) {
        NbtWriter writer = StartWorld();
        writer.Text("LevelName", "Fixture world");
        writer.Long("LastPlayed", 1730000000000);
        basicEnd = writer.Length;
        writer.Int("GameType", 1);
        writer.Byte("Difficulty", 3);
        writer.Byte("hardcore", 1);
        writer.Byte("allowCommands", 0);
        writer.Byte("WasModded", 1);
        writer.Int("DataVersion", 3465);
        writer.Int("SpawnX", -32);
        writer.Int("SpawnY", 70);
        writer.Int("SpawnZ", 256);
        writer.Long("DayTime", 48000);
        writer.Long("Time", 92000);
        writer.Compound("Version");
        writer.Text("Name", "1.20.1");
        writer.Byte("Snapshot", 0);
        writer.End();
        writer.Compound("Unknown");
        writer.Int("Ignored", 42);
        writer.End();
        writer.Compound("WorldGenSettings");
        writer.Long("seed", -9876543210123);
        writer.End();
        writer.End();
        writer.End();
        return writer.Bytes();
    }

    private static byte[] LegacyFixture() {
        return World(writer => {
            writer.Text("LevelName", "Old world");
            writer.Long("RandomSeed", -42);
        });
    }

    private static byte[] World(Action<NbtWriter> contents) {
        NbtWriter writer = StartWorld();
        contents(writer);
        writer.End();
        writer.End();
        return writer.Bytes();
    }

    private static NbtWriter StartWorld() {
        NbtWriter writer = new();
        writer.Compound("");
        writer.Compound("Data");
        return writer;
    }

    private static void NestedCompound(NbtWriter writer, int descendants) {
        writer.Compound("Unknown");
        for (int index = 0; index < descendants; index++) writer.Compound("");
        writer.Int("leaf", 42);
        for (int index = 0; index <= descendants; index++) writer.End();
    }

    private static void NestedList(NbtWriter writer, int descendants) {
        writer.List("Unknown", descendants == 0 ? (byte) 1 : (byte) 9, 1);
        for (int index = 0; index < descendants; index++) {
            writer.Raw([index == descendants - 1 ? (byte) 1 : (byte) 9]);
            writer.IntPayload(1);
        }
        writer.Raw([42]);
    }

    private static byte[] BudgetFixture(int totalBytes) {
        // root header (3), Data header (7), byte array header+length (11), Data end (1).
        NbtWriter writer = StartWorld();
        writer.ByteArray("blob", new byte[totalBytes - 22]);
        writer.End();
        return writer.Bytes();
    }

    private static WorldMetadataInfo? Read(byte[] fixture, bool details) {
        using MemoryStream stream = new(fixture, writable: false);
        return WorldMetadataReader.Read(stream, details);
    }

    private static WorldMetadataInfo Required(WorldMetadataInfo? value) {
        return value ?? throw new InvalidOperationException("Expected readable world metadata.");
    }

    private static void NoDetails(WorldMetadataInfo data) {
        Equal<string?>(null, data.VersionName);
        Equal<int?>(null, data.DataVersion);
        Equal<int?>(null, data.GameMode);
        Equal<int?>(null, data.Difficulty);
        Equal<bool?>(null, data.Hardcore);
        Equal<bool?>(null, data.AllowCommands);
        Equal<bool?>(null, data.WasModded);
        Equal<bool?>(null, data.Snapshot);
        Equal<long?>(null, data.Seed);
        Equal<int?>(null, data.SpawnX);
        Equal<int?>(null, data.SpawnY);
        Equal<int?>(null, data.SpawnZ);
        Equal<long?>(null, data.DayTime);
        Equal<long?>(null, data.Time);
    }

    private static void Equal<T>(T expected, T actual, string? context = null) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) {
            throw new InvalidOperationException($"{context ?? "Value"}: expected <{expected}>, got <{actual}>.");
        }
    }

    private static void Throws<T>(Action action, string? message = null) where T : Exception {
        try {
            action();
        } catch (Exception exception) {
            Equal(typeof(T), exception.GetType(), "exception type");
            if (message is not null) Equal(message, exception.Message, "exception message");
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    // Fixtures use the NBT wire format, not parser internals. BinaryPrimitives independently
    // writes signed big-endian numeric values so sign and byte-order regressions are visible.
    private sealed class NbtWriter {
        private readonly MemoryStream _stream = new();

        internal int Length => checked((int) _stream.Length);
        internal byte[] Bytes() => _stream.ToArray();
        internal void Raw(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);
        internal void End() => _stream.WriteByte(0);
        internal void Compound(string name) => Header(10, name);

        internal void Header(int kind, string name) {
            _stream.WriteByte(checked((byte) kind));
            StringPayload(name);
        }

        internal void Byte(string name, byte value) {
            Header(1, name);
            _stream.WriteByte(value);
        }

        internal void Int(string name, int value) {
            Header(3, name);
            IntPayload(value);
        }

        internal void Long(string name, long value) {
            Header(4, name);
            LongPayload(value);
        }

        internal void Text(string name, string value) {
            Header(8, name);
            StringPayload(value);
        }

        internal void ByteArray(string name, byte[] values) {
            Header(7, name);
            IntPayload(values.Length);
            Raw(values);
        }

        internal void IntArray(string name, int[] values) {
            Header(11, name);
            IntPayload(values.Length);
            foreach (int value in values) IntPayload(value);
        }

        internal void LongArray(string name, long[] values) {
            Header(12, name);
            IntPayload(values.Length);
            foreach (long value in values) LongPayload(value);
        }

        internal void List(string name, byte elementKind, int count) {
            Header(9, name);
            _stream.WriteByte(elementKind);
            IntPayload(count);
        }

        internal void IntPayload(int value) {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            Raw(bytes);
        }

        internal void LongPayload(long value) {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            Raw(bytes);
        }

        private void StringPayload(string value) {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort) bytes.Length));
            Raw(length);
            Raw(bytes);
        }
    }

    private sealed class ReadByteOnlyStream(byte[] bytes) : Stream {
        private readonly MemoryStream _inner = new(bytes, writable: false);
        internal bool WasDisposed { get; private set; }
        public override bool CanRead => !WasDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int ReadByte() => _inner.ReadByte();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing) {
            WasDisposed = true;
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
