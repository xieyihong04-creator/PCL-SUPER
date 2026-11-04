using System.IO.Compression;
using System.Text;

namespace PCL.Living.Features.Tests;

/// <summary>第 2 批：整合包更新与地图安装引擎的用例。对应 LF ModpackUpdate.vb 的关键规则。</summary>
internal static class Wave2Tests {
    public static IEnumerable<(string Name, Action Run)> Cases => [
        ("modpack path rejects traversal reserved and invalid names", PathRules),
        ("modpack reads modrinth manifest and extracts overrides", ReadModrinth),
        ("modpack plan classifies new update removal and personal conflicts", PlanClassification),
        ("modpack commit applies files and writes author manifest", CommitApplies),
        ("modpack recovers an interrupted transaction", RecoverInterrupted),
        ("map installer extracts single world and avoids overwrite", MapInstall),
        ("map installer rejects traversal archives", MapRejectsTraversal),
        ("dependency gate fingerprint is stable and order independent", GateFingerprintStable),
        ("dependency gate invalidates on file or flag change", GateInvalidates),
        ("dependency gate accepts per folder and clears on settings change", GateAcceptPerFolder)
    ];

    #region 夹具

    private sealed class Sandbox : IDisposable {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "pcl-super-wave2-" + Guid.NewGuid().ToString("N"));
        public Sandbox() => Directory.CreateDirectory(Root);
        public string Combine(params string[] parts) => Path.Combine([Root, .. parts]);
        public void Dispose() {
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void EnsureWithin(string root, string path) {
        string parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && !string.Equals(full, parent.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new IOException("资源路径超出了当前目录。");
    }

    private static PackUpdateHost Host(string sandbox, Action<List<PackUpdateNetFile>, CancellationToken>? download = null) => new() {
        EnsureWithin = EnsureWithin,
        TempFolder = () => sandbox,
        IndiePath = version => sandbox,
        Download = download
    };

    private static void WriteZip(string path, params (string Name, string Content)[] entries) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = File.Create(path);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);
        foreach ((string name, string content) in entries) {
            ZipArchiveEntry entry = archive.CreateEntry(name);
            using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    private static string Sha1(string file) => ModpackUpdateEngine.Digest(file)!;

    private static void Require(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireThrows(Action action, string fragment) {
        try {
            action();
        } catch (IOException exception) {
            Require(exception.Message.Contains(fragment, StringComparison.Ordinal), $"异常信息不符：{exception.Message}");
            return;
        }
        throw new InvalidOperationException("预期抛出 IOException，但没有抛出。");
    }

    #endregion

    private static void PathRules() {
        PackUpdateHost host = Host(Path.GetTempPath());
        ModpackUpdateEngine engine = new(host);
        Require(engine.RelativePath("mods/a.jar") == "mods/a.jar", "普通路径应被接受");
        Require(engine.RelativePath("config\\x.json") == "config/x.json", "反斜杠应被规范化");
        RequireThrows(() => engine.RelativePath("../escape"), "无效路径");
        RequireThrows(() => engine.RelativePath("/abs/path"), "无效路径");
        RequireThrows(() => engine.RelativePath("mods/con.jar"), "无效路径");
        RequireThrows(() => engine.RelativePath("mods/bad?.jar"), "无效路径");
        RequireThrows(() => engine.RelativePath("mods/trailing."), "无效路径");
        Require(ModpackUpdateEngine.IsPrivate("PCL/config.json"), "PCL 目录应视为私有");
        Require(ModpackUpdateEngine.IsPersonal("saves/world/level.dat"), "存档应视为个人数据");
        Require(!ModpackUpdateEngine.IsPersonal("mods/a.jar"), "模组不是个人数据");
    }

    private static void ReadModrinth() {
        using Sandbox box = new();
        string archive = box.Combine("pack.mrpack");
        string index = """
            {
              "formatVersion": 1,
              "name": "Test Pack",
              "versionId": "2.0",
              "dependencies": { "minecraft": "1.20.1", "fabric-loader": "0.15.0" },
              "files": [
                { "path": "mods/cool.jar", "hashes": { "sha1": "0123456789abcdef0123456789abcdef01234567" },
                  "downloads": ["https://cdn.modrinth.com/data/abcd1234/versions/1.0/cool.jar"],
                  "env": { "client": "required" } },
                { "path": "mods/clientonly.jar", "hashes": { "sha1": "0123456789abcdef0123456789abcdef01234567" },
                  "downloads": ["https://cdn.modrinth.com/data/abcd1234/versions/1.0/clientonly.jar"],
                  "env": { "client": "unsupported" } }
              ]
            }
            """;
        WriteZip(archive, ("modrinth.index.json", index), ("overrides/config/a.txt", "hello"),
            ("client-overrides/options.txt", "fov=90"), ("overrides/PCL/private.json", "{}"));

        string stage = box.Combine("stage");
        Directory.CreateDirectory(stage);
        PackUpdatePackage package = new ModpackUpdateEngine(Host(box.Root)).ReadPackage(archive, stage, CancellationToken.None);
        Require(package.Name == "Test Pack", "名称解析失败");
        Require(package.Version == "2.0", "版本解析失败");
        Require(package.Minecraft == "1.20.1", "Minecraft 版本解析失败");
        Require(package.Loaders[ModLoaders.Fabric] == "0.15.0", "加载器解析失败");
        Require(!package.Files.ContainsKey("mods/clientonly.jar"), "客户端不支持的文件应被跳过");
        Require(package.Files.ContainsKey("mods/cool.jar"), "普通模组应被收录");
        Require(package.Files["mods/cool.jar"].Identity == "modrinth:abcd1234", "项目标识解析失败");
        Require(package.Files["config/a.txt"].Payload is not null, "覆写文件应被暂存");
        Require(package.Files.ContainsKey("options.txt"), "client-overrides 应被收录");
        Require(!package.Files.ContainsKey("PCL/private.json"), "PCL 私有文件应被跳过");
        Require(File.ReadAllText(package.Files["config/a.txt"].Payload!) == "hello", "覆写内容不一致");
    }

    private static void PlanClassification() {
        using Sandbox box = new();
        string root = box.Combine("game");
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        Directory.CreateDirectory(Path.Combine(root, "saves", "world"));
        File.WriteAllText(Path.Combine(root, "mods", "same.jar"), "same");
        File.WriteAllText(Path.Combine(root, "mods", "changed.jar"), "old");
        File.WriteAllText(Path.Combine(root, "mods", "removed.jar"), "gone-by-upstream");
        File.WriteAllText(Path.Combine(root, "options.txt"), "fov=70");
        File.WriteAllText(Path.Combine(root, "saves", "world", "level.dat"), "world");

        JObject baseline = new() {
            ["schema"] = 1,
            ["files"] = new JObject {
                ["mods/same.jar"] = new JObject { ["hash"] = Sha1(Path.Combine(root, "mods", "same.jar")) },
                ["mods/changed.jar"] = new JObject { ["hash"] = Sha1(Path.Combine(root, "mods", "changed.jar")) },
                ["options.txt"] = new JObject { ["hash"] = Sha1(Path.Combine(root, "options.txt")) },
                ["mods/removed.jar"] = new JObject { ["hash"] = Sha1(Path.Combine(root, "mods", "removed.jar")) }
            }
        };
        PackUpdatePackage package = new() { Minecraft = "1.20.1" };
        package.Files["mods/same.jar"] = new PackUpdateFile { Relative = "mods/same.jar", Hash = Sha1(Path.Combine(root, "mods", "same.jar")) };
        package.Files["mods/changed.jar"] = new PackUpdateFile { Relative = "mods/changed.jar", Hash = "sha1:" + new string('b', 40), Urls = [] };
        package.Files["mods/new.jar"] = new PackUpdateFile { Relative = "mods/new.jar", Hash = "sha1:" + new string('c', 40), Urls = [] };
        package.Files["options.txt"] = new PackUpdateFile { Relative = "options.txt", Hash = "sha1:" + new string('d', 40), Urls = [] };

        PackUpdatePlan plan = new ModpackUpdateEngine(Host(box.Root)).Plan(root, baseline, package);
        Require(plan.Changes.All(change => change.Relative != "mods/same.jar"), "未变化的文件不应出现在差异中");
        PackUpdateChange added = plan.Changes.Single(change => change.Relative == "mods/new.jar");
        Require(!added.Conflict && added.Reason == "新增", "新文件分类错误");
        PackUpdateChange updated = plan.Changes.Single(change => change.Relative == "mods/changed.jar");
        Require(!updated.Conflict && updated.Reason == "更新", "更新文件分类错误");
        PackUpdateChange removed = plan.Changes.Single(change => change.Relative == "mods/removed.jar");
        Require(!removed.Conflict && removed.Reason == "移除作者已删除的文件", "删除分类错误");
        PackUpdateChange personal = plan.Changes.Single(change => change.Relative == "options.txt");
        Require(personal.Conflict && personal.Reason == "保留个人文件或修改", "个人文件应被保留");
    }

    private static void CommitApplies() {
        using Sandbox box = new();
        string gameRoot = box.Combine("game");
        string versionFolder = Path.Combine(gameRoot, "versions", "v1");
        string mods = Path.Combine(gameRoot, "mods");
        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(versionFolder);
        File.WriteAllText(Path.Combine(mods, "old.jar"), "old");
        string payload = box.Combine("payload.jar");
        File.WriteAllText(payload, "new");

        PackUpdatePlan plan = new() {
            Root = gameRoot,
            Package = new PackUpdatePackage { Name = "Pack", Version = "2", Minecraft = "1.20.1" },
            Baseline = new JObject { ["files"] = new JObject() }
        };
        plan.Package.Files["mods/new.jar"] = new PackUpdateFile { Relative = "mods/new.jar", Hash = Sha1(payload), Payload = payload };
        plan.Changes.Add(new PackUpdateChange { Relative = "mods/new.jar", Incoming = plan.Package.Files["mods/new.jar"], Reason = "新增" });
        plan.Changes.Add(new PackUpdateChange { Relative = "mods/old.jar", Before = Sha1(Path.Combine(mods, "old.jar")), Reason = "移除作者已删除的文件" });

        string backup = new ModpackUpdateEngine(Host(box.Root)).Commit(plan, versionFolder, CancellationToken.None);
        Require(File.ReadAllText(Path.Combine(mods, "new.jar")) == "new", "新增文件未写入");
        Require(!File.Exists(Path.Combine(mods, "old.jar")), "删除文件未移除");
        Require(Directory.Exists(backup), "事务备份目录不存在");
        string manifest = Path.Combine(versionFolder, "PCL", "ModpackOrigin.json");
        Require(File.Exists(manifest), "作者清单未写入");
        JObject saved = JObject.Parse(File.ReadAllText(manifest));
        Require((int)saved["schema"]! == 1, "作者清单格式错误");
        Require(saved["files"]!["mods/new.jar"] is not null, "作者清单缺少新文件");
    }

    private static void RecoverInterrupted() {
        using Sandbox box = new();
        string gameRoot = box.Combine("game");
        string versionFolder = Path.Combine(gameRoot, "versions", "v1");
        string target = Path.Combine(gameRoot, "mods", "a.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(versionFolder);
        File.WriteAllText(target, "broken");

        string boxFolder = Path.Combine(versionFolder, "PCL", "PackUpdateBackups", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(boxFolder);
        string backup = Path.Combine(boxFolder, "0.bak");
        File.WriteAllText(backup, "original");
        JObject log = new() {
            ["complete"] = false,
            ["gameRoot"] = Path.GetFullPath(gameRoot),
            ["files"] = new JArray {
                new JObject {
                    ["target"] = target, ["backup"] = backup,
                    ["before"] = Sha1(backup),
                    ["after"] = Sha1(target)
                }
            }
        };
        File.WriteAllText(Path.Combine(boxFolder, "transaction.json"), log.ToString());

        ModpackUpdateEngine engine = new(Host(box.Root));
        Require(engine.Pending(versionFolder).Count == 1, "未识别未完成的事务");
        engine.Recover(versionFolder, gameRoot);
        Require(File.ReadAllText(target) == "original", "回滚未恢复原文件");
        Require(engine.Pending(versionFolder).Count == 0, "回滚后事务仍未完成");
    }

    private static void MapInstall() {
        using Sandbox box = new();
        string saves = box.Combine("saves");
        Directory.CreateDirectory(saves);
        string archive = box.Combine("MyMap.zip");
        WriteZip(archive, ("MyMap/level.dat", "level"), ("MyMap/region/r.0.0.mca", "region"));
        MapDownloadHost host = new() { EnsureWithin = EnsureWithin };
        MapDownloadInstaller installer = new(host);
        Require(installer.GetDownloadFolder(saves) == Path.Combine(saves, MapDownloadInstaller.DownloadFolderName), "下载目录不一致");
        string installed = installer.Install(archive, saves, CancellationToken.None);
        Require(Directory.Exists(Path.Combine(installed, "region")), "世界内容未解压");
        Require(File.Exists(archive), "原压缩包应保留");
        string second = installer.Install(archive, saves, CancellationToken.None);
        Require(Path.GetFileName(second).EndsWith("(2)", StringComparison.Ordinal), "同名存档未顺延命名");
    }

    private static DependencyReportView SampleReport(string root, bool enabled = true) {
        return new DependencyReportView {
            ContextJson = "{\"minecraft\":\"1.20.1\",\"loader\":\"fabric\"}",
            Notices = ["n2", "n1"],
            Mods = [
                new DependencyReportMod { Key = "sodium", Version = "0.5", Format = "fabric", Enabled = enabled,
                    RootPath = Path.Combine(root, "mods", "sodium.jar"), Dependencies = ["Required:indium", "Optional:iris"] },
                new DependencyReportMod { Key = "indium", Version = "1.0", Format = "fabric", Enabled = true,
                    RootPath = Path.Combine(root, "mods", "indium.jar"), Dependencies = ["Required:sodium"] }
            ],
            Issues = ["MissingRequired|True|sodium 需要 indium"]
        };
    }

    private static void GateFingerprintStable() {
        using Sandbox box = new();
        Directory.CreateDirectory(Path.Combine(box.Root, "mods"));
        File.WriteAllText(Path.Combine(box.Root, "mods", "sodium.jar"), "s");
        File.WriteAllText(Path.Combine(box.Root, "mods", "indium.jar"), "i");
        DependencyReportView first = SampleReport(box.Root);
        DependencyReportView reordered = SampleReport(box.Root);
        reordered.Notices.Reverse();
        reordered.Mods.Reverse();
        reordered.Mods[1].Dependencies.Reverse();
        Require(DependencyLaunchGate.Fingerprint(first) == DependencyLaunchGate.Fingerprint(reordered), "顺序不同的相同报告应得到同一指纹");
    }

    private static void GateInvalidates() {
        using Sandbox box = new();
        Directory.CreateDirectory(Path.Combine(box.Root, "mods"));
        File.WriteAllText(Path.Combine(box.Root, "mods", "sodium.jar"), "s");
        File.WriteAllText(Path.Combine(box.Root, "mods", "indium.jar"), "i");
        DependencyLaunchGate gate = new();
        gate.Accept(box.Root, SampleReport(box.Root));
        Require(gate.WasAccepted(box.Root, SampleReport(box.Root)), "相同环境应视为已确认");
        Require(!gate.WasAccepted(box.Root, SampleReport(box.Root, enabled: false)), "启用状态变化应重新确认");
        File.WriteAllText(Path.Combine(box.Root, "mods", "sodium.jar"), "changed-content");
        Require(!gate.WasAccepted(box.Root, SampleReport(box.Root)), "文件内容变化应重新确认");
    }

    private static void GateAcceptPerFolder() {
        using Sandbox box = new();
        string left = box.Combine("a");
        string right = box.Combine("b");
        Directory.CreateDirectory(Path.Combine(left, "mods"));
        Directory.CreateDirectory(Path.Combine(right, "mods"));
        File.WriteAllText(Path.Combine(left, "mods", "sodium.jar"), "s");
        File.WriteAllText(Path.Combine(left, "mods", "indium.jar"), "i");
        File.WriteAllText(Path.Combine(right, "mods", "sodium.jar"), "s");
        File.WriteAllText(Path.Combine(right, "mods", "indium.jar"), "i");
        DependencyLaunchGate gate = new();
        gate.Accept(left, SampleReport(left));
        Require(gate.WasAccepted(left, SampleReport(left)), "同目录应视为已确认");
        Require(!gate.WasAccepted(right, SampleReport(right)), "不同版本目录不应共享确认");
        gate.SettingsChanged();
        Require(!gate.WasAccepted(left, SampleReport(left)), "设置变化后应清空确认");
    }

    private static void MapRejectsTraversal() {
        using Sandbox box = new();
        string saves = box.Combine("saves");
        Directory.CreateDirectory(saves);
        string archive = box.Combine("bad.zip");
        WriteZip(archive, ("../evil.txt", "x"), ("world/level.dat", "level"));
        MapDownloadInstaller installer = new(new MapDownloadHost { EnsureWithin = EnsureWithin });
        RequireThrows(() => installer.Install(archive, saves, CancellationToken.None), "越界");
    }
}
