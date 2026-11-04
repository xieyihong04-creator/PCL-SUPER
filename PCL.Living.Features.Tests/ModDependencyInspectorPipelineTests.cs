using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCL.Living.Features;
using Inspector = PCL.Living.Features.ModDependencyInspection<ModDependencyInspectorPipelineTests.Context, ModDependencyInspectorPipelineTests.Mod, ModDependencyInspectorPipelineTests.Dependency, ModDependencyInspectorPipelineTests.Edge, ModDependencyInspectorPipelineTests.Issue, ModDependencyInspectorPipelineTests.Impact, ModDependencyInspectorPipelineTests.Report, ModDependencyInspectorPipelineTests.OnlineProject, ModDependencyInspectorPipelineTests.OnlineDependency, ModDependencyInspectorPipelineTests.OnlineFile>;

// Real local declarations from LF ModDependencyInspector.vb and Test-ModDependencies.ps1,
// pinned at 859fd4fce6c921f324245aeeaa443b1902749f07, under the LF LICENCE.
// PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8 has no corresponding module.
// CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 is a same-source reference only:
// its JAR scan/selection/cache budgets differ; these tests retain LF's 4/256/64 MiB policy.
internal static class ModDependencyInspectorPipelineTests {
    private static readonly UTF8Encoding Utf8 = new(false);
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("mod inspector pipeline parses real Fabric aliases sides and disabled files into reversible dependency impacts", FabricGraph);
        yield return ("mod inspector pipeline preserves Forge manifest TOML optional checks and Quilt compound declarations", TextDeclarations);
        yield return ("mod inspector pipeline tracks real nested providers external alternatives and cyclic removal chains", NestedAndCycles);
        yield return ("mod inspector pipeline distinguishes unknown predicates from required mismatches and hard or soft conflicts", VersionsAndConflicts);
        yield return ("mod inspector pipeline keeps partial metadata conservative without hiding definite versions or changing files", ReliabilityAndReadOnly);
        yield return ("mod inspector pipeline enforces original archive depth count byte metadata limits and cancellation", ArchiveBudgetsAndCancellation);
    }

    private static Context EnvironmentFor(string loader = "fabricloader", string version = "0.16.0") {
        Context context = new() { MinecraftVersion = "1.20.1", JavaVersion = "17.0.9", LoaderKnown = true };
        context.LoaderVersions[loader] = version;
        return context;
    }
    private static Issue[] Issues(Report report, ModDependencyIssueKind kind, string? owner = null) =>
        report.Issues.Where(item => item.KindValue == kind && (owner is null || item.Owner.Id == owner)).ToArray();
    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
    private static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Canceled(Action action) {
        try { action(); } catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Canceled production inspection unexpectedly completed.");
    }

    private static void FabricGraph() {
        using Fixture fixture = new(); string folder = fixture.Folder("fabric");
        string core = fixture.Fabric(folder, "core.jar", "core", "1.2.0", new JObject { ["provides"] = new JArray("api_alias") });
        string addon = fixture.Fabric(folder, "addon.jar", "addon", extra: new JObject {
            ["depends"] = new JObject { ["api_alias"] = ">=1.0 <2.0", ["minecraft"] = "1.20.1", ["java"] = ">=17", ["fabricloader"] = ">=0.15" },
            ["recommends"] = new JObject { ["recommendation"] = "*" }, ["suggests"] = new JObject { ["suggestion"] = "*" }
        });
        string consumer = fixture.Fabric(folder, "consumer.jar", "consumer", extra: new JObject { ["depends"] = new JObject { ["addon"] = "*" } });
        fixture.Fabric(folder, "server.jar", "server", extra: new JObject { ["environment"] = "server", ["depends"] = new JObject { ["server_only_missing"] = "*" } });
        string disabled = fixture.Fabric(folder, "disabled.jar.disabled", "disabled", extra: new JObject { ["depends"] = new JObject { ["disabled_missing"] = "*" } });
        Dictionary<string, string> before = Snapshot(folder);
        Report report = Inspector.Inspect(folder, EnvironmentFor());
        Equal(5, report.Mods.Count); Equal(false, report.HasErrors); Equal(false, report.MetadataIncomplete);
        Equal(1, Issues(report, ModDependencyIssueKind.Recommendation).Length); Equal(0, Issues(report, ModDependencyIssueKind.MissingRequired).Length);
        Edge edge = report.Edges.Single(item => item.Owner.Id == "addon" && item.Requirement.Id == "api_alias");
        Equal(ModDependencyMatchState.Satisfied, edge.StateValue); Equal(ModDependencyFileProviderKind.Required, Inspector.FileProviderRelation(report, edge, core));
        Equal("1.2.0", Inspector.Identities(report.Mods.Single(item => item.Id == "core"))["api_alias"]);
        List<Impact> impacts = Inspector.GetImpacts(report, new[] { core });
        Equal(2, impacts.Count); True(impacts.Single(item => item.Mod.Id == "addon").IsDirect, "A direct real provider impact became transitive.");
        True(!impacts.Single(item => item.Mod.Id == "consumer").IsDirect && impacts.All(item => item.IsDefinite), "The actual reverse chain lost its definite/transitive status.");
        Equal(0, Inspector.GetImpacts(report, new[] { core, addon, consumer }).Count); Equal(0, Inspector.GetImpacts(report, new[] { disabled }).Count);
        True(report.DescribeFile(core).Contains("被这些模组依赖", StringComparison.Ordinal) && Inspector.DescribeImpacts(report, new[] { core }).Contains("间接受影响", StringComparison.Ordinal), "The report lost its real reverse/detail explanation.");
        Report enabled = Inspector.WithEnabledFiles(report, new[] { disabled }, CancellationToken.None);
        Equal(1, Issues(enabled, ModDependencyIssueKind.MissingRequired, "disabled").Length);
        Equal(false, report.Mods.Single(item => item.Id == "disabled").Enabled); Equal(true, enabled.Mods.Single(item => item.Id == "disabled").Enabled);
        SameSnapshot(before, folder);
    }

    private static void TextDeclarations() {
        using Fixture fixture = new(); string forge = fixture.Folder("forge");
        const string toml = """"
            modLoader="javafml"
            loaderVersion="[47]"
            [[mods]]
            modId="multi_one"
            version="${file.jarVersion}"
            description="""
            [[dependencies.multi_one]]
            modId="fake_from_description"
            mandatory=true
            """
            [[mods]]
            modId="multi_two"
            version="1.0"
            displayName="Name # preserved"
            [[dependencies.multi_one]]
            modId="multi_two"
            mandatory=false
            versionRange="[2.0,)" # present optional still checks its version
            [[dependencies.multi_one]]
            modId="optional_missing"
            mandatory=false
            [[dependencies.multi_one]]
            modId="server_missing"
            mandatory=true
            side="SERVER"
            """";
        fixture.Jar(forge, "multi.jar", new Dictionary<string, byte[]> { ["META-INF/mods.toml"] = Utf8.GetBytes(toml), ["META-INF/MANIFEST.MF"] = Utf8.GetBytes("Manifest-Version: 1.0\r\nImplementation-Version: 1.2.3\r\n") });
        Report report = Inspector.Inspect(forge, EnvironmentFor("forge", "47.2.0"));
        Equal(2, report.Mods.Count); Equal("1.2.3", report.Mods.Single(item => item.Id == "multi_one").Version); Equal(false, report.MetadataIncomplete);
        Equal(1, Issues(report, ModDependencyIssueKind.VersionMismatch, "multi_one").Length);
        True(report.Issues.All(item => item.Requirement.Id == "multi_two") && report.Edges.All(item => item.Requirement.Id != "fake_from_description"), "Multiline text, comments, side or absent optional dependencies created false declarations.");
        Edge optional = report.Edges.Single(item => item.Requirement.Id == "multi_two"); Equal(ModDependencyKind.Optional, optional.Requirement.KindValue); Equal(true, optional.Requirement.EnforceWhenPresent);

        string quilt = fixture.Folder("quilt");
        JObject provider = Quilt("provider", new JObject { ["provides"] = new JArray(new JObject { ["id"] = "fixture:virtual", ["version"] = "2.5" }) });
        JObject consumer = Quilt("quilt_consumer", new JObject {
            ["depends"] = new JArray(new JObject { ["id"] = "fixture:virtual", ["versions"] = new JObject { ["all"] = new JArray(">=2.0", "<3.0") } },
                new JArray("missing_alternative", "fixture:virtual"), new JObject { ["id"] = "optional_absent", ["optional"] = true }, new JObject { ["id"] = "unless_absent", ["unless"] = "provider" }),
            ["breaks"] = new JArray(new JObject { ["id"] = "provider", ["versions"] = "*", ["unless"] = "fixture:virtual" })
        });
        fixture.Jar(quilt, "provider.jar", new Dictionary<string, byte[]> { ["quilt.mod.json"] = Json(provider) });
        fixture.Jar(quilt, "consumer.jar", new Dictionary<string, byte[]> { ["quilt.mod.json"] = Json(consumer) });
        Report qualified = Inspector.Inspect(quilt, EnvironmentFor("quilt_loader", "0.28.0"));
        Equal(false, qualified.HasErrors); Equal(false, qualified.MetadataIncomplete);
        True(qualified.Edges.Any(item => item.ConditionBypassed) && qualified.Edges.Any(item => item.Requirement.Children.Count > 0), "Real Quilt alternatives/all/unless declarations were not retained.");
    }

    private static void NestedAndCycles() {
        using Fixture fixture = new(); string folder = fixture.Folder("nested");
        byte[] inner = JarBytes(Fabric("nested_api", "1.0"));
        string parent = fixture.Jar(folder, "parent.jar", new Dictionary<string, byte[]> {
            ["fabric.mod.json"] = Json(Fabric("parent", extra: new JObject { ["depends"] = new JObject { ["nested_api"] = ">=1.0" }, ["jars"] = new JArray(new JObject { ["file"] = "META-INF/jars/api.jar" }) })),
            ["META-INF/jars/api.jar"] = inner
        });
        fixture.Fabric(folder, "dependent.jar", "dependent", extra: new JObject { ["depends"] = new JObject { ["nested_api"] = ">=1.0" } });
        Report report = Inspector.Inspect(folder, EnvironmentFor());
        Equal(false, report.HasErrors); Equal(3, report.Mods.Count);
        Mod nested = report.Mods.Single(item => item.IsNested); Equal(false, nested.IsCandidate); Equal(parent, nested.RootPath);
        True(nested.MetadataPath.Contains("META-INF/jars/api.jar!", StringComparison.Ordinal), "The nested declaration lost its containing real JAR.");
        Equal("dependent", Inspector.GetImpacts(report, new[] { parent }).Single().Mod.Id);
        fixture.Fabric(folder, "external.jar", "nested_api", "2.0");
        Report external = Inspector.Inspect(folder, EnvironmentFor()); Equal(0, Issues(external, ModDependencyIssueKind.DuplicateId).Length); Equal(0, Inspector.GetImpacts(external, new[] { parent }).Count);
        True(external.Mods.Single(item => item.IsNested).IsCandidate, "An outer installed provider did not leave its bundled fallback as a candidate.");

        string cycle = fixture.Folder("cycle");
        string a = fixture.Fabric(cycle, "a.jar", "a", extra: new JObject { ["depends"] = new JObject { ["b"] = "*" } });
        fixture.Fabric(cycle, "b.jar", "b", extra: new JObject { ["depends"] = new JObject { ["a"] = "*" } });
        fixture.Fabric(cycle, "tail.jar", "tail", extra: new JObject { ["depends"] = new JObject { ["b"] = "*" } });
        Report cyclic = Inspector.Inspect(cycle, EnvironmentFor()); Equal(false, cyclic.HasErrors); Equal(3, cyclic.Edges.Count);
        List<Impact> lost = Inspector.GetImpacts(cyclic, new[] { a }); Equal(2, lost.Count);
        Equal(true, lost.Single(item => item.Mod.Id == "b").IsDirect); Equal(false, lost.Single(item => item.Mod.Id == "tail").IsDirect);
    }

    private static void VersionsAndConflicts() {
        Equal(ModDependencyMatchState.Satisfied, ModDependencyVersions.MatchVersion("1.2.3", ">=1.2 <2.0"));
        Equal(ModDependencyMatchState.Unsatisfied, ModDependencyVersions.MatchVersion("2.0", ">=1.2 <2.0"));
        Equal(ModDependencyMatchState.Unknown, ModDependencyVersions.MatchVersion("${version}", ">=2.0"));
        Equal(ModDependencyMatchState.Unknown, ModDependencyVersions.MatchVersion("1.0-final", "[1.0]", "maven"));
        using Fixture fixture = new(); string folder = fixture.Folder("versions");
        fixture.Fabric(folder, "lib.jar", "lib", "1.20.10");
        fixture.Fabric(folder, "consumer.jar", "consumer", extra: new JObject { ["depends"] = new JObject { ["lib"] = "1.20.1" } });
        fixture.Fabric(folder, "unknown.jar", "unknown", "${version}");
        fixture.Fabric(folder, "dynamic.jar", "dynamic", extra: new JObject { ["depends"] = new JObject { ["unknown"] = ">=2.0", ["lib"] = "latest.release", ["java"] = ">=21" } });
        fixture.Fabric(folder, "hard.jar", "hard", extra: new JObject { ["breaks"] = new JObject { ["lib"] = "*" } });
        fixture.Fabric(folder, "soft.jar", "soft", extra: new JObject { ["conflicts"] = new JObject { ["lib"] = "*" } });
        Context unknownJava = EnvironmentFor(); unknownJava.JavaVersion = null;
        Report report = Inspector.Inspect(folder, unknownJava);
        Equal(1, Issues(report, ModDependencyIssueKind.VersionMismatch).Length); Equal("consumer", Issues(report, ModDependencyIssueKind.VersionMismatch).Single().Owner.Id);
        True(report.Edges.Where(item => item.Owner.Id == "dynamic").All(item => item.StateValue == ModDependencyMatchState.Unknown), "Unknown actual/range/Java values became definite errors.");
        Equal(true, Issues(report, ModDependencyIssueKind.Incompatible, "hard").Single().IsError); Equal(false, Issues(report, ModDependencyIssueKind.Incompatible, "soft").Single().IsError);
    }

    private static void ReliabilityAndReadOnly() {
        using Fixture fixture = new(); string folder = fixture.Folder("uncertain");
        fixture.Fabric(folder, "consumer.jar", "consumer", extra: new JObject { ["depends"] = new JObject { ["library"] = ">=2" } });
        Report complete = Inspector.Inspect(folder, EnvironmentFor()); Equal(true, Issues(complete, ModDependencyIssueKind.MissingRequired).Single().IsError);
        fixture.Jar(folder, "unread.jar", new Dictionary<string, byte[]> { ["fixture.txt"] = Utf8.GetBytes("no recognized declaration") });
        Report partial = Inspector.Inspect(folder, EnvironmentFor()); Equal(true, partial.MetadataIncomplete); Equal(false, partial.HasErrors); Equal(false, Issues(partial, ModDependencyIssueKind.MissingRequired).Single().IsError);
        fixture.Fabric(folder, "library.jar", "library", "1.0"); File.WriteAllBytes(Path.Combine(folder, "corrupt.jar"), Utf8.GetBytes("not a zip"));
        fixture.Jar(folder, "utf8.jar", new Dictionary<string, byte[]> { ["fabric.mod.json"] = [0xFF, 0xFE, 0xFF] });
        Dictionary<string, string> before = Snapshot(folder); Report mismatch = Inspector.Inspect(folder, EnvironmentFor());
        Equal(true, mismatch.MetadataIncomplete); Equal(true, Issues(mismatch, ModDependencyIssueKind.VersionMismatch).Single().IsError);
        True(mismatch.ToSummary().Contains("部分文件的元数据未读完整", StringComparison.Ordinal), "The real summary lost its uncertainty explanation."); SameSnapshot(before, folder);
        string disabled = fixture.Folder("disabled-corrupt"); fixture.Fabric(disabled, "consumer.jar", "consumer", extra: new JObject { ["depends"] = new JObject { ["absent"] = "*" } });
        File.WriteAllBytes(Path.Combine(disabled, "broken.jar.disabled"), [1, 2, 3]);
        Report inactive = Inspector.Inspect(disabled, EnvironmentFor()); Equal(false, inactive.MetadataIncomplete); Equal(true, inactive.HasErrors);
    }

    private static void ArchiveBudgetsAndCancellation() {
        using Fixture fixture = new(); string depth = fixture.Folder("depth"); byte[] chain = JarBytes(Fabric("depth_5"));
        for (int index = 4; index >= 0; index--) chain = JarBytes(Fabric("depth_" + index, extra: new JObject { ["jars"] = new JArray(new JObject { ["file"] = "next.jar" }) }), new Dictionary<string, byte[]> { ["next.jar"] = chain });
        File.WriteAllBytes(Path.Combine(depth, "depth.jar"), chain);
        Report deep = Inspector.Inspect(depth, EnvironmentFor()); Equal(5, deep.Mods.Count); Equal(4, deep.Mods.Count(item => item.IsNested)); Equal(true, deep.MetadataIncomplete);
        True(deep.Notices.Any(item => item.Contains("读取上限", StringComparison.Ordinal)) && deep.Mods.All(item => item.Id != "depth_5"), "The original four-level nested boundary changed.");

        string count = fixture.Folder("count"); JArray jars = new(); Dictionary<string, byte[]> entries = new();
        for (int index = 0; index < 257; index++) { string entry = "nested/" + index + ".jar"; jars.Add(new JObject { ["file"] = entry }); entries[entry] = JarBytes(Fabric("count_" + index)); }
        entries["fabric.mod.json"] = Json(Fabric("count_parent", extra: new JObject { ["jars"] = jars })); fixture.Jar(count, "a-limit.jar", entries);
        fixture.Jar(count, "b-independent.jar", new Dictionary<string, byte[]> { ["fabric.mod.json"] = Json(Fabric("independent", extra: new JObject { ["jars"] = new JArray(new JObject { ["file"] = "tail.jar" }) })), ["tail.jar"] = JarBytes(Fabric("tail_nested")) });
        Report many = Inspector.Inspect(count, EnvironmentFor()); Equal(257, many.Mods.Count(item => item.IsNested)); Equal(true, many.MetadataIncomplete);
        True(many.Mods.Any(item => item.Id == "tail_nested") && many.Mods.All(item => item.Id != "count_256"), "The original 256-item budget changed or leaked between root archives.");

        string bytes = fixture.Folder("bytes"); string oversized = Path.Combine(bytes, "oversized.jar");
        using (ZipArchive archive = ZipFile.Open(oversized, ZipArchiveMode.Create)) {
            using (Stream metadata = archive.CreateEntry("fabric.mod.json").Open()) metadata.Write(Json(Fabric("large_parent", extra: new JObject { ["jars"] = new JArray(new JObject { ["file"] = "large.jar" }) })));
            using Stream payload = archive.CreateEntry("large.jar", CompressionLevel.Optimal).Open(); byte[] zeros = new byte[8192]; long remaining = 64L * 1024 * 1024 + 1;
            while (remaining > 0) { int take = (int)Math.Min(remaining, zeros.Length); payload.Write(zeros, 0, take); remaining -= take; }
        }
        Report large = Inspector.Inspect(bytes, EnvironmentFor()); Equal(1, large.Mods.Count); Equal(true, large.MetadataIncomplete);
        True(large.Notices.Any(item => item.Contains("读取上限", StringComparison.Ordinal)), "A real ZIP entry beyond 64 MiB reached nested decoding instead of the original budget guard.");
        string metadataLimit = fixture.Folder("metadata"); fixture.Jar(metadataLimit, "large-metadata.jar", new Dictionary<string, byte[]> { ["fabric.mod.json"] = new byte[1024 * 1024 + 1] });
        Report metadataReport = Inspector.Inspect(metadataLimit, EnvironmentFor()); Equal(true, metadataReport.MetadataIncomplete); Equal(0, metadataReport.Mods.Count);
        True(metadataReport.Notices.Any(item => item.Contains("1 MiB", StringComparison.Ordinal)), "The original metadata-size limit was not enforced before JSON parsing.");
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Canceled(() => Inspector.Inspect(depth, EnvironmentFor(), cancellation.Token));
        Canceled(() => Inspector.BuildReport(deep, cancellation.Token));
    }

    private static JObject Fabric(string id, string version = "1.0", JObject? extra = null) {
        JObject value = new() { ["schemaVersion"] = 1, ["id"] = id, ["name"] = id, ["version"] = version };
        if (extra is not null) foreach (JProperty pair in extra.Properties()) value[pair.Name] = pair.Value.DeepClone(); return value;
    }
    private static JObject Quilt(string id, JObject extra) {
        JObject loader = new() { ["group"] = "fixture", ["id"] = id, ["version"] = "1.0", ["metadata"] = new JObject { ["name"] = id } };
        foreach (JProperty pair in extra.Properties()) loader[pair.Name] = pair.Value.DeepClone(); return new JObject { ["schema_version"] = 1, ["quilt_loader"] = loader };
    }
    private static byte[] Json(JObject value) => Utf8.GetBytes(value.ToString(Formatting.None));
    private static void WriteArchive(Stream stream, IReadOnlyDictionary<string, byte[]> entries) {
        using ZipArchive archive = new(stream, ZipArchiveMode.Create, true);
        foreach (KeyValuePair<string, byte[]> pair in entries) { using Stream entry = archive.CreateEntry(pair.Key, CompressionLevel.Optimal).Open(); entry.Write(pair.Value, 0, pair.Value.Length); }
    }
    private static byte[] JarBytes(JObject metadata, Dictionary<string, byte[]>? extra = null) {
        Dictionary<string, byte[]> entries = extra is null ? new() : new(extra); entries["fabric.mod.json"] = Json(metadata);
        using MemoryStream stream = new(); WriteArchive(stream, entries); return stream.ToArray();
    }
    private static Dictionary<string, string> Snapshot(string folder) => Directory.GetFiles(folder).ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    private static void SameSnapshot(Dictionary<string, string> expected, string folder) {
        Dictionary<string, string> actual = Snapshot(folder); Equal(expected.Count, actual.Count);
        foreach (KeyValuePair<string, string> pair in expected) { Equal(pair.Value, actual[pair.Key]); using FileStream exclusive = new(pair.Key, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
    }
    private sealed class Fixture : IDisposable {
        private readonly string root = Path.Combine(Path.GetTempPath(), "PclModInspector-" + Guid.NewGuid().ToString("N"));
        internal Fixture() => Directory.CreateDirectory(root);
        internal string Folder(string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
        internal string Jar(string folder, string name, IReadOnlyDictionary<string, byte[]> entries) { string path = Path.Combine(folder, name); using FileStream stream = File.Create(path); WriteArchive(stream, entries); return path; }
        internal string Fabric(string folder, string file, string id, string version = "1.0", JObject? extra = null) => Jar(folder, file, new Dictionary<string, byte[]> { ["fabric.mod.json"] = Json(ModDependencyInspectorPipelineTests.Fabric(id, version, extra)) });
        public void Dispose() => Directory.Delete(root, true);
    }

    internal sealed class Context : Inspector.ContextData { public Context() { } }
    internal sealed class Mod : Inspector.ModData { public Mod() { } }
    internal sealed class Dependency : Inspector.DependencyData { public Dependency() { } public override ModDependencyKind KindValue { get; set; } }
    internal sealed class Edge : Inspector.EdgeData { public Edge() { } public override ModDependencyMatchState StateValue { get; set; } }
    internal sealed class Issue : Inspector.IssueData { public Issue() { } public override ModDependencyIssueKind KindValue { get; set; } }
    internal sealed class Impact : Inspector.ImpactData { public Impact() { } }
    internal sealed class Report : Inspector.ReportData { public Report() { } }
    internal sealed class OnlineProject : Inspector.OnlineProjectData { public OnlineProject() { } }
    internal sealed class OnlineDependency : Inspector.OnlineDependencyData { public OnlineDependency() { } }
    internal sealed class OnlineFile : Inspector.OnlineFileData { public OnlineFile() { } }
}
