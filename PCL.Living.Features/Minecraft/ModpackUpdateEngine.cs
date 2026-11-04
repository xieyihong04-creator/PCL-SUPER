using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace PCL.Living.Features;

/// <summary>
/// LF 整合包更新引擎的 C# 移植（对应 LF ModpackUpdate.vb）。
/// 保留 LF 的作者清单 <c>PCL\ModpackOrigin.json</c>、事务日志与回滚语义，
/// 宿主相关能力（路径限制、网络、下载、临时目录、版本隔离）通过 <see cref="PackUpdateHost" /> 注入。
/// </summary>
public sealed class ModpackUpdateEngine {
    public const string ManifestName = "PCL/ModpackOrigin.json";

    private static readonly string[] PrivatePrefixes = ["pcl/", ".pcl-"];
    private static readonly string[] PersonalPrefixes =
        ["saves/", "screenshots/", "xaerowaypoints/", "xaeroworldmap/", "journeymap/", "voxelmap/", "schematics/"];
    private static readonly string[] ArchiveLoaderFiles =
        ["fabric.mod.json", "quilt.mod.json", "META-INF/mods.toml", "META-INF/neoforge.mods.toml", "mcmod.info"];

    private const long MaxJsonEntry = 16L * 1024 * 1024;
    private const long MaxPackageBytes = 20L * 1024 * 1024 * 1024;
    private const long MaxPackageFileBytes = 2L * 1024 * 1024 * 1024;

    private readonly PackUpdateHost host;

    public ModpackUpdateEngine(PackUpdateHost host) => this.host = host;

    #region 路径与校验

    /// <summary>校验整合包内相对路径，返回以 <c>/</c> 分隔的规范形式。</summary>
    public string RelativePath(string? value) {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) throw new IOException("整合包包含无效路径。");
        value = value.Replace('\\', '/');
        foreach (string part in value.Split('/')) {
            if (part == "" || part == "." || part == ".." || part.EndsWith('.') || part.EndsWith(' ')
                || HasInvalidWindowsChar(part)
                || Regex.IsMatch(part, @"\A(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase))
                throw new IOException("整合包包含无效路径：" + value);
        }
        return value;
    }

    private static bool HasInvalidWindowsChar(string part) {
        foreach (char c in part) {
            if (c < 32) return true;
            if ("<>:\"/\\|?*".Contains(c)) return true;
        }
        return false;
    }

    /// <summary>把整合包相对路径映射为实际路径，并拒绝越界与被文件夹占用。</summary>
    public string Target(string root, string relative) {
        string result = Path.Combine(root, RelativePath(relative).Replace('/', Path.DirectorySeparatorChar));
        host.EnsureWithin(root, result);
        if (Directory.Exists(result)) throw new IOException("文件位置已被文件夹占用：" + relative);
        return result;
    }

    /// <summary>返回 <c>alg:hex</c>；文件不存在时返回 <see langword="null" />。</summary>
    public static string? Digest(string? fileName, string algorithm = "sha1") {
        if (fileName is null || !File.Exists(fileName)) return null;
        using FileStream input = File.OpenRead(fileName);
        using HashAlgorithm hasher = algorithm switch {
            "md5" => MD5.Create(),
            "sha512" => SHA512.Create(),
            _ => SHA1.Create()
        };
        return algorithm + ":" + Convert.ToHexString(hasher.ComputeHash(input)).ToLowerInvariant();
    }

    private static bool Matches(string? fileName, string? expected) {
        if (expected is null) return false;
        string algorithm = expected.Split(':')[0];
        return Digest(fileName, algorithm) == expected;
    }

    private static string ValidHash(string algorithm, string? value) {
        int length = algorithm switch { "md5" => 32, "sha512" => 128, _ => 40 };
        if (!Regex.IsMatch(value ?? "", @"\A[0-9a-fA-F]{" + length + @"}\z")) throw new IOException("整合包文件缺少有效校验值。");
        return algorithm + ":" + value!.ToLowerInvariant();
    }

    /// <summary>启动器私有文件，不参与整合包内容比较。</summary>
    public static bool IsPrivate(string relative) {
        string lower = relative.ToLowerInvariant();
        return PrivatePrefixes.Any(prefix => lower.StartsWith(prefix, StringComparison.Ordinal)) || lower == ".pclignore";
    }

    /// <summary>玩家个人数据，更新时一律保留。</summary>
    public static bool IsPersonal(string relative) {
        string lower = relative.ToLowerInvariant();
        return lower == "options.txt" || lower.StartsWith("optionsof", StringComparison.Ordinal)
            || lower.StartsWith("optionsshaders", StringComparison.Ordinal)
            || lower is "servers.dat" or "servers.dat_old"
            || PersonalPrefixes.Any(prefix => lower.StartsWith(prefix, StringComparison.Ordinal))
            || lower.Contains("waypoint", StringComparison.Ordinal)
            || lower.StartsWith("config/xaero", StringComparison.Ordinal)
            || lower.StartsWith("config/journeymap", StringComparison.Ordinal)
            || lower.StartsWith("config/voxelmap", StringComparison.Ordinal);
    }

    private bool PersonalAreaExists(string root, string relative) {
        string[] parts = relative.Split('/');
        if (parts.Length < 2) return false;
        string prefix = parts[0].ToLowerInvariant();
        string area = prefix == "saves" && parts.Length > 2 ? parts[0] + "/" + parts[1] : parts[0];
        string folder = Path.Combine(root, area.Replace('/', Path.DirectorySeparatorChar));
        host.EnsureWithin(root, folder);
        return IsPersonal(relative) && Directory.Exists(folder);
    }

    #endregion

    #region 整合包解析

    public PackUpdatePackage ReadPackage(string archivePath, string stage, CancellationToken token) {
        PackUpdatePackage result = new();
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(item => item.FullName is "modrinth.index.json" or "manifest.json");
        if (entry is null) {
            List<ZipArchiveEntry> candidates = archive.Entries
                .Where(item => item.FullName.Split('/').Length == 2
                    && (item.Name == "modrinth.index.json" || item.Name == "manifest.json")).ToList();
            if (candidates.Count != 1) throw new IOException("请选择 CurseForge 或 Modrinth 格式的整合包文件。");
            entry = candidates[0];
        }
        string prefix = entry.FullName[..^entry.Name.Length];
        JObject json = JsonEntry(entry);
        result.Name = json["name"]?.ToString() ?? "整合包";
        result.Version = json["versionId"]?.ToString() ?? json["version"]?.ToString() ?? "";
        List<string> overrideFolders = [];

        if (entry.Name == "modrinth.index.json") {
            result.Minecraft = json["dependencies"]?["minecraft"]?.ToString() ?? "";
            if (json["dependencies"] is JObject dependencies) {
                Dictionary<string, ModLoaders> known = new() {
                    ["fabric-loader"] = ModLoaders.Fabric,
                    ["forge"] = ModLoaders.Forge,
                    ["neoforge"] = ModLoaders.NeoForge
                };
                foreach (JProperty dependency in dependencies.Properties()) {
                    if (dependency.Name == "minecraft") continue;
                    if (!known.TryGetValue(dependency.Name, out ModLoaders kind)) throw new IOException("暂不支持此整合包加载器：" + dependency.Name);
                    result.Loaders[kind] = dependency.Value.ToString();
                }
            }
            foreach (JToken item in json["files"] as JArray ?? []) {
                token.ThrowIfCancellationRequested();
                string? client = item["env"]?["client"]?.ToString();
                if (client == "unsupported") continue;
                PackUpdateFile value = new() {
                    Relative = RelativePath(item["path"]?.ToString()),
                    Hash = ValidHash("sha1", item["hashes"]?["sha1"]?.ToString()),
                    OptionalFile = client == "optional",
                    Urls = (item["downloads"] as JArray ?? []).Select(url => url.ToString()).ToList()
                };
                foreach (string url in value.Urls) {
                    Match match = Regex.Match(url, @"\Ahttps://cdn\.modrinth\.com/data/([A-Za-z0-9]+)/versions/");
                    if (match.Success) {
                        value.Identity = "modrinth:" + match.Groups[1].Value;
                        break;
                    }
                }
                AddFile(result, value);
            }
            overrideFolders.Add(prefix + "overrides/");
            overrideFolders.Add(prefix + "client-overrides/");
        } else {
            result.Minecraft = json["minecraft"]?["version"]?.ToString() ?? "";
            foreach (JToken item in json["minecraft"]?["modLoaders"] as JArray ?? []) {
                string id = item["id"]?.ToString() ?? throw new IOException("无效的整合包加载器。");
                int split = id.IndexOf('-');
                if (split < 1) throw new IOException("无效的整合包加载器。");
                ModLoaders kind = id[..split] switch {
                    "forge" => ModLoaders.Forge,
                    "neoforge" => ModLoaders.NeoForge,
                    "fabric" => ModLoaders.Fabric,
                    _ => throw new IOException("暂不支持此整合包加载器：" + id)
                };
                result.Loaders[kind] = id[(split + 1)..];
            }
            List<JToken> files = (json["files"] as JArray ?? []).ToList();
            if (files.Count > 0) {
                string body = new JObject(new JProperty("fileIds", new JArray(files.Select(item => (int)item["fileID"]!)))).ToString();
                JToken response = Request()("https://api.curseforge.com/v1/mods/files", HttpMethod.Post, body)["data"]
                    ?? throw new IOException("整合包所需文件不可用，未修改当前版本。");
                foreach (JToken item in files) {
                    token.ThrowIfCancellationRequested();
                    JToken? data = (response as JArray)?.FirstOrDefault(value => value["id"]?.ToString() == item["fileID"]?.ToString());
                    if (data is null || data["modId"]?.ToString() != item["projectID"]?.ToString())
                        throw new IOException("整合包所需文件不可用，未修改当前版本。");
                    string fileName = data["fileName"]?.ToString() ?? throw new IOException("整合包所需文件不可用，未修改当前版本。");
                    string? rawHash = data["hashes"]?["sha1"]?.ToString() ?? data["hashes"]?["md5"]?.ToString();
                    if (string.IsNullOrEmpty(rawHash)) throw new IOException("整合包文件缺少有效校验值。");
                    List<string> urls = data["downloadUrl"]?.ToString() is { Length: > 0 } download ? [download] : [];
                    if (urls.Count == 0) throw new IOException("整合包所需文件不允许下载：" + fileName);
                    List<string> modules = (data["modules"] as JArray ?? []).Select(module => module["name"]?.ToString() ?? "").ToList();
                    string folder = fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? "mods"
                        : modules.Contains("shaders") ? "shaderpacks"
                        : modules.Contains("pack.mcmeta") ? "resourcepacks"
                        : "mods";
                    AddFile(result, new PackUpdateFile {
                        Relative = RelativePath(folder + "/" + fileName),
                        Hash = ValidHash(rawHash.Length == 32 ? "md5" : "sha1", rawHash),
                        Identity = "curseforge:" + item["projectID"],
                        OptionalFile = item["required"] is not null && !(bool)item["required"]!,
                        Urls = urls
                    });
                }
            }
            string overridePath = (json["overrides"]?.ToString() ?? "overrides").TrimEnd('/');
            overrideFolders.Add(prefix + RelativePath(overridePath) + "/");
        }

        if (string.IsNullOrWhiteSpace(result.Minecraft)) throw new IOException("整合包缺少 Minecraft 版本。");
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (string folder in overrideFolders) {
            seen.Clear();
            foreach (ZipArchiveEntry item in archive.Entries.Where(value =>
                         value.FullName.StartsWith(folder, StringComparison.Ordinal) && !value.FullName.EndsWith('/'))) {
                token.ThrowIfCancellationRequested();
                string relative = RelativePath(item.FullName[folder.Length..]);
                if (IsPrivate(relative)) continue;
                if (!seen.Add(relative)) throw new IOException("整合包包含重复路径：" + relative);
                total += item.Length;
                if (total > MaxPackageBytes || item.Length > MaxPackageFileBytes) throw new IOException("整合包覆写文件过大。");
                string payload = Target(stage, "payload/" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
                using (Stream input = item.Open())
                using (FileStream output = File.Create(payload)) input.CopyTo(output);
                result.Files[relative] = new PackUpdateFile { Relative = relative, Payload = payload, Hash = Digest(payload) };
            }
        }
        return result;
    }

    private void AddFile(PackUpdatePackage package, PackUpdateFile value) {
        if (IsPrivate(value.Relative)) return;
        foreach (string url in value.Urls) {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? address) || address.Scheme != "https" || address.UserInfo != "")
                throw new IOException("整合包包含无效下载地址。");
        }
        if (package.Files.ContainsKey(value.Relative)) throw new IOException("整合包包含重复路径：" + value.Relative);
        package.Files.Add(value.Relative, value);
    }

    private static JObject JsonEntry(ZipArchiveEntry entry) {
        if (entry.Length > MaxJsonEntry) throw new IOException("整合包清单过大。");
        using StreamReader reader = new(entry.Open(), Encoding.UTF8);
        return JObject.Parse(reader.ReadToEnd());
    }

    private Func<string, HttpMethod, string?, JToken> Request() {
        Func<string, HttpMethod, string?, JToken>? request = host.Request;
        if (request is null) throw new IOException("当前环境无法访问 CurseForge 文件清单，请改用本地整合包文件。");
        return request;
    }

    public static JObject Manifest(PackUpdatePackage package) {
        JObject files = [];
        foreach (PackUpdateFile item in package.Files.Values)
            files[item.Relative] = new JObject(new JProperty("hash", item.Hash), new JProperty("identity", item.Identity));
        return new JObject(
            new JProperty("schema", 1),
            new JProperty("name", package.Name),
            new JProperty("version", package.Version),
            new JProperty("minecraft", package.Minecraft),
            new JProperty("loaders", JObject.FromObject(package.Loaders)),
            new JProperty("files", files),
            new JProperty("source", package.Source));
    }

    public JObject? ReadManifest(string versionFolder) {
        string fileName = Target(versionFolder, ManifestName);
        if (!File.Exists(fileName)) return null;
        if (new FileInfo(fileName).Length > MaxJsonEntry) throw new IOException("整合包作者清单过大。");
        JObject result = JObject.Parse(File.ReadAllText(fileName));
        if ((int)result["schema"]! != 1 || result["files"] is not JObject) throw new IOException("不支持此版本的整合包作者清单。");
        return result;
    }

    public JObject? Detect(string versionFolder) {
        JObject? recorded = ReadManifest(versionFolder);
        if (recorded is not null) return recorded;
        foreach (string name in new[] { "modrinth.index.json", "manifest.json" }) {
            string address = Target(versionFolder, name);
            if (!File.Exists(address)) continue;
            if (new FileInfo(address).Length > MaxJsonEntry) throw new IOException("整合包清单过大。");
            JObject data = JObject.Parse(File.ReadAllText(address));
            string? minecraft = name == "modrinth.index.json"
                ? data["dependencies"]?["minecraft"]?.ToString()
                : data["minecraft"]?["version"]?.ToString();
            if (string.IsNullOrWhiteSpace(minecraft) || data["files"] is not JArray) continue;
            return new JObject(
                new JProperty("name", data["name"]?.ToString() ?? "整合包"),
                new JProperty("version", data["versionId"]?.ToString() ?? data["version"]?.ToString()),
                new JProperty("minecraft", minecraft),
                new JProperty("legacy", true));
        }
        return null;
    }

    #endregion

    #region 差异与下载

    public PackUpdatePlan Plan(string root, JObject baseline, PackUpdatePackage package) {
        if (baseline is null) throw new IOException("此版本没有作者文件清单，请先选择当前使用的原版整合包建立记录。");
        PackUpdatePlan result = new() { Root = root, Package = package, Baseline = baseline };
        Dictionary<string, JToken> old = ((JObject)baseline["files"]!)
            .Properties().ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase);
        foreach (string relative in old.Keys.Union(package.Files.Keys, StringComparer.OrdinalIgnoreCase)) {
            if (IsPrivate(relative)) continue;
            string fileName = Target(root, relative);
            old.TryGetValue(relative, out JToken? previous);
            package.Files.TryGetValue(relative, out PackUpdateFile? incoming);
            string? before = Digest(fileName);
            string? oldHash = previous?["hash"]?.ToString();
            if (incoming is not null && incoming.OptionalFile && previous is null && before is null) {
                result.Changes.Add(new PackUpdateChange { Relative = relative, Incoming = incoming, Conflict = true, Reason = "保留未安装的可选组件" });
                continue;
            }
            // 上游未变化时不恢复玩家删除、禁用或修改的文件。
            if (incoming is not null && (incoming.Hash == oldHash || Matches(fileName, incoming.Hash))) continue;
            if (incoming is null && before is null) continue;
            bool conflict = (before is not null && IsPersonal(relative)) || PersonalAreaExists(root, relative)
                || (previous is not null && !Matches(fileName, oldHash))
                || (previous is null && before is not null);
            result.Changes.Add(new PackUpdateChange {
                Relative = relative,
                Before = before,
                Incoming = incoming,
                Conflict = conflict,
                Reason = conflict
                    ? (before is null ? "保留玩家删除或禁用" : "保留个人文件或修改")
                    : (incoming is null ? "移除作者已删除的文件" : (before is null ? "新增" : "更新"))
            });
        }
        // 同一项目改了文件名时，不能一边保留个人版本，一边安装另一个版本。
        HashSet<string> protectedIds = new(StringComparer.Ordinal);
        foreach (PackUpdateChange change in result.Changes.Where(item => item.Conflict)) {
            string? id = old.ContainsKey(change.Relative) ? old[change.Relative]["identity"]?.ToString() : change.Incoming?.Identity;
            if (!string.IsNullOrEmpty(id)) protectedIds.Add(id);
        }
        foreach (PackUpdateChange change in result.Changes.Where(item => item.Incoming is not null && protectedIds.Contains(item.Incoming.Identity ?? ""))) {
            change.Conflict = true;
            change.Reason = "保留此项目的个人版本，跳过同项目新文件";
        }
        return result;
    }

    public void DownloadChanges(PackUpdatePlan plan, string stage, CancellationToken token) {
        List<PackUpdateNetFile> files = [];
        foreach (PackUpdateChange change in plan.Changes.Where(item => !item.Conflict && item.Incoming is not null)) {
            PackUpdateFile value = change.Incoming!;
            if (value.Payload is not null) continue;
            value.Payload = Target(stage, "download/" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(value.Payload)!);
            files.Add(new PackUpdateNetFile { Urls = value.Urls, Destination = value.Payload, Hash = value.Hash! });
        }
        if (files.Count > 0) {
            Action<List<PackUpdateNetFile>, CancellationToken>? download = host.Download;
            if (download is null) throw new IOException("当前环境没有可用的下载器，无法获取整合包更新文件。");
            download(files, token);
        }
        foreach (PackUpdateChange change in plan.Changes.Where(item => !item.Conflict && item.Incoming is not null)) {
            token.ThrowIfCancellationRequested();
            if (!Matches(change.Incoming!.Payload, change.Incoming.Hash)) throw new IOException("整合包更新文件校验失败：" + change.Relative);
        }
        PreserveModCollisions(plan);
    }

    private static HashSet<string> ModIds(string fileName) {
        HashSet<string> result = new(StringComparer.Ordinal);
        try {
            using ZipArchive archive = ZipFile.OpenRead(fileName);
            foreach (string name in ArchiveLoaderFiles) {
                ZipArchiveEntry? entry = archive.GetEntry(name);
                if (entry is null || entry.Length > 1024 * 1024) continue;
                using StreamReader reader = new(entry.Open());
                string text = reader.ReadToEnd();
                if (name.EndsWith(".toml", StringComparison.Ordinal)) {
                    foreach (Match match in Regex.Matches(text, "(?m)^\\s*modId\\s*=\\s*['\" ]*([a-zA-Z0-9_-]+)"))
                        result.Add(match.Groups[1].Value);
                } else {
                    JToken data = JToken.Parse(text);
                    if (name == "mcmod.info") {
                        JArray? items = data as JArray ?? data["modList"] as JArray;
                        foreach (JToken item in items ?? [])
                            if (item["modid"] is not null) result.Add(item["modid"]!.ToString());
                    } else {
                        string? id = name == "quilt.mod.json" ? data["quilt_loader"]?["id"]?.ToString() : data["id"]?.ToString();
                        if (!string.IsNullOrEmpty(id)) result.Add(id);
                    }
                }
            }
        } catch (InvalidDataException) {
        } catch (JsonException) {
        }
        return result;
    }

    private void PreserveModCollisions(PackUpdatePlan plan) {
        string folder = Path.Combine(plan.Root, "mods");
        host.EnsureWithin(plan.Root, folder);
        if (!Directory.Exists(folder)) return;
        Dictionary<string, HashSet<string>> retained = new(StringComparer.OrdinalIgnoreCase);
        foreach (string fileName in Directory.GetFiles(folder)) {
            host.EnsureWithin(plan.Root, fileName);
            if (!new[] { ".jar", ".disabled", ".old" }.Contains(Path.GetExtension(fileName).ToLowerInvariant())) continue;
            string relative = "mods/" + Path.GetFileName(fileName);
            if (plan.Changes.Any(item => !item.Conflict && item.Relative.Equals(relative, StringComparison.OrdinalIgnoreCase))) continue;
            retained[relative] = ModIds(fileName);
        }
        foreach (PackUpdateChange change in plan.Changes.Where(item =>
                     !item.Conflict && item.Incoming is not null
                     && item.Relative.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))) {
            HashSet<string> ids = ModIds(change.Incoming!.Payload!);
            KeyValuePair<string, HashSet<string>> collision = retained.FirstOrDefault(item => item.Value.Overlaps(ids));
            if (collision.Value is null) continue;
            change.Conflict = true;
            change.Reason = "保留个人模组 " + collision.Key + "，跳过重复模组";
            // 若本次也计划删除同项目旧作者文件，一并保留，避免只完成了一半升级。
            foreach (PackUpdateChange other in plan.Changes.Where(item =>
                         item.Incoming is null && !item.Conflict
                         && item.Relative.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))) {
                if (ModIds(Target(plan.Root, other.Relative)).Overlaps(ids)) {
                    other.Conflict = true;
                    other.Reason = "此模组的新版与个人模组冲突，保留旧版";
                }
            }
        }
    }

    #endregion

    #region 提交、回滚与记录

    public string Commit(PackUpdatePlan plan, string versionFolder, CancellationToken token,
        Dictionary<string, string>? extraFiles = null, Action<int>? beforeApply = null) {
        token.ThrowIfCancellationRequested();
        List<(string Path, string? Payload, string? Before)> operations = [];
        foreach (PackUpdateChange change in plan.Changes.Where(item => !item.Conflict)) {
            string fileName = Target(plan.Root, change.Relative);
            if (Digest(fileName) != change.Before) throw new IOException("文件已被其他程序修改，请重新检查更新：" + change.Relative);
            operations.Add((fileName, change.Incoming?.Payload, change.Before));
        }
        if (extraFiles is not null) {
            foreach (KeyValuePair<string, string> pair in extraFiles) {
                string destination = Target(versionFolder, pair.Key);
                operations.Add((destination, pair.Value, Digest(destination)));
            }
        }
        string box = Target(versionFolder, "PCL/PackUpdateBackups/" + Guid.NewGuid().ToString("N") + "/transaction.json");
        Directory.CreateDirectory(Path.GetDirectoryName(box)!);
        string statePayload = Path.Combine(Path.GetDirectoryName(box)!, "next-manifest.json");
        JObject nextManifest = Manifest(plan.Package);
        foreach (PackUpdateChange change in plan.Changes.Where(item => item.Conflict)) {
            JToken? previous = plan.Baseline["files"]?[change.Relative];
            if (previous is not null) nextManifest["files"]![change.Relative] = previous.DeepClone();
        }
        File.WriteAllText(statePayload, nextManifest.ToString());
        string statePath = Target(versionFolder, ManifestName);
        operations.Add((statePath, statePayload, Digest(statePath)));
        return ApplyTransaction(plan.Root, versionFolder, operations, token, beforeApply, box);
    }

    public string ApplyTransaction(string gameRoot, string versionFolder,
        List<(string Path, string? Payload, string? Before)> operations, CancellationToken token,
        Action<int>? beforeApply = null, string? box = null) {
        token.ThrowIfCancellationRequested();
        box ??= Target(versionFolder, "PCL/PackUpdateBackups/" + Guid.NewGuid().ToString("N") + "/transaction.json");
        string boxFolder = Path.GetDirectoryName(box)!;
        Directory.CreateDirectory(boxFolder);
        foreach ((string path, _, string? before) in operations) {
            if (IsParentOf(versionFolder, path)) host.EnsureWithin(versionFolder, path);
            else host.EnsureWithin(gameRoot, path);
            if (Digest(path) != before) throw new IOException("文件已变化，请重新检查环境。");
        }
        JObject log = new(new JProperty("complete", false), new JProperty("gameRoot", Path.GetFullPath(gameRoot)), new JProperty("files", new JArray()));
        JArray logFiles = (JArray)log["files"]!;
        for (int index = 0; index < operations.Count; index++) {
            token.ThrowIfCancellationRequested();
            (string path, string? payload, string? before) = operations[index];
            string backup = Path.Combine(boxFolder, index + ".bak");
            if (before is not null) File.Copy(path, backup, false);
            logFiles.Add(new JObject(
                new JProperty("target", path),
                new JProperty("backup", before is null ? null : backup),
                new JProperty("before", before),
                new JProperty("after", payload is null ? null : Digest(payload))));
        }
        token.ThrowIfCancellationRequested();
        File.WriteAllText(box, log.ToString());
        List<int> applied = [];
        try {
            for (int index = 0; index < operations.Count; index++) {
                beforeApply?.Invoke(index);
                token.ThrowIfCancellationRequested();
                (string path, string? payload, string? before) = operations[index];
                if (Digest(path) != before) throw new IOException("提交期间文件发生变化，已停止更新。");
                applied.Add(index);
                if (payload is null) {
                    File.Delete(path);
                } else {
                    ReplaceFrom(payload, path);
                }
            }
            token.ThrowIfCancellationRequested();
            log["complete"] = true;
            LocalGameDataStore.AtomicWrite(box, Encoding.UTF8.GetBytes(log.ToString()), null, false);
        } catch {
            foreach (int index in applied.AsEnumerable().Reverse()) {
                JToken item = logFiles[index];
                string destination = item["target"]!.ToString();
                if (item["backup"]!.Type == JTokenType.Null) {
                    if (Digest(destination) == item["after"]?.ToString() && File.Exists(destination)) File.Delete(destination);
                } else {
                    ReplaceFrom(item["backup"]!.ToString(), destination);
                }
            }
            log["complete"] = true;
            log["rolledBack"] = true;
            File.WriteAllText(box, log.ToString());
            throw;
        }
        return boxFolder;
    }

    private static void ReplaceFrom(string source, string destination) {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".pcl-update-" + Guid.NewGuid().ToString("N");
        try {
            File.Copy(source, temporary, false);
            if (File.Exists(destination)) {
                if (OperatingSystem.IsWindows()) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination, true);
            } else {
                File.Move(temporary, destination);
            }
        } finally {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public List<string> Pending(string versionFolder) {
        string folder = Path.Combine(versionFolder, "PCL", "PackUpdateBackups");
        host.EnsureWithin(versionFolder, folder);
        List<string> result = [];
        if (!Directory.Exists(folder)) return result;
        foreach (string child in Directory.GetDirectories(folder)) {
            host.EnsureWithin(versionFolder, child);
            string logFile = Path.Combine(child, "transaction.json");
            host.EnsureWithin(versionFolder, logFile);
            if (File.Exists(logFile) && new FileInfo(logFile).Length <= MaxJsonEntry
                && !(bool)JObject.Parse(File.ReadAllText(logFile))["complete"]!)
                result.Add(logFile);
        }
        return result;
    }

    public void Recover(string versionFolder, string gameRoot) {
        foreach (string logFile in Pending(versionFolder)) {
            JObject log = JObject.Parse(File.ReadAllText(logFile));
            JArray items = (JArray)log["files"]!;
            if (!IsSame(log["gameRoot"]!.ToString(), gameRoot))
                throw new IOException("更新备份对应的游戏目录已经变化，请手动检查备份。");
            foreach (JToken item in items) {
                string destination = item["target"]!.ToString();
                if (IsParentOf(versionFolder, destination)) host.EnsureWithin(versionFolder, destination);
                else host.EnsureWithin(gameRoot, destination);
                string? current = Digest(destination);
                string? before = item["before"]!.Type == JTokenType.Null ? null : item["before"]!.ToString();
                string? after = item["after"]!.Type == JTokenType.Null ? null : item["after"]!.ToString();
                if (current != before && current != after) throw new IOException("中断后文件又发生变化，已保留现场：" + destination);
                if (item["backup"]!.Type != JTokenType.Null) {
                    string backup = item["backup"]!.ToString();
                    host.EnsureWithin(Path.GetDirectoryName(logFile)!, backup);
                    if (Digest(backup) != before) throw new IOException("更新备份校验失败。");
                }
            }
            foreach (JToken item in items.Reverse()) {
                string destination = item["target"]!.ToString();
                if (item["backup"]!.Type == JTokenType.Null) {
                    if (File.Exists(destination)) File.Delete(destination);
                } else {
                    ReplaceFrom(item["backup"]!.ToString(), destination);
                }
            }
            log["complete"] = true;
            log["rolledBack"] = true;
            LocalGameDataStore.AtomicWrite(logFile, Encoding.UTF8.GetBytes(log.ToString()), null, false);
        }
    }

    /// <summary>首次安装后登记作者文件清单；只登记与整合包一致的文件。</summary>
    public void RecordInstalled(string archivePath, string versionFolder, JObject? source = null) {
        string stage = host.TempFolder();
        try {
            PackUpdatePackage package = ReadPackage(archivePath, stage, CancellationToken.None);
            package.Source = source;
            string root = host.IndiePath(versionFolder);
            foreach (string relative in package.Files.Keys.ToList()) {
                if (!Matches(Target(root, relative), package.Files[relative].Hash)) package.Files.Remove(relative);
            }
            LocalGameDataStore.AtomicWrite(Target(versionFolder, ManifestName), Encoding.UTF8.GetBytes(Manifest(package).ToString()), null, false);
        } catch (Exception ex) {
            Logger.Warn(ex, "整合包已安装，但记录作者文件清单失败；更新时可重新提供原始整合包");
        }
    }

    #endregion

    #region 路径比较

    private static string Normalize(string path) => Path.GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    public static bool IsSame(string left, string right) => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    public static bool IsParentOf(string parent, string child) {
        string root = Normalize(parent);
        string value = Normalize(child);
        return value.Length > root.Length
            && value.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
