using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

public sealed partial class ModDependencyInspection<TContext, TMod, TDependency, TEdge, TIssue, TImpact, TReport, TOnlineProject, TOnlineDependency, TOnlineFile> {
    // LF ModDependencyInspector.vb: local archive and metadata reading, under the LF LICENCE.
    // CE 1a7653127d578efdb2a9388999a3fdc67d9e49d5 ModJarInJar.cs,
    // ModJarInJarDependency.cs and ModLocalComp.cs are C# references only;
    // their cache, five-level/512-item budgets and loader policies are not imported.
    private const int MaxMetadataBytes = 1024 * 1024;
    private const long MaxNestedBytes = 64L * 1024 * 1024;
    private const int MaxNestedDepth = 4;
    private const int MaxNestedCount = 256;

    public static TReport Inspect(string modsFolder, TContext? context = null, CancellationToken token = default) {
        TReport result = new() { Context = context ?? new TContext() };
        result.Builtins = CreateBuiltins(result.Context);
        if (!Directory.Exists(modsFolder)) return result;
        List<string> roots = new() { Path.GetFullPath(modsFolder) };
        if (!string.IsNullOrEmpty(result.Context.LegacyForgeVersionFolder) && Regex.IsMatch(result.Context.LegacyForgeVersionFolder, "^[0-9.]+$")) {
            string legacy = Path.Combine(modsFolder, result.Context.LegacyForgeVersionFolder);
            if (Directory.Exists(legacy)) roots.Add(Path.GetFullPath(legacy));
        }
        foreach (string folder in roots) {
            token.ThrowIfCancellationRequested();
            string[] files;
            try {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) {
                    Incomplete(result, "跳过链接目录：" + Path.GetFileName(folder));
                    continue;
                }
                files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).Where(IsModFile)
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
            } catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) {
                Incomplete(result, "无法枚举模组目录：" + error.Message);
                continue;
            }
            foreach (string fullPath in files) {
                token.ThrowIfCancellationRequested();
                bool enabled = !fullPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) && !fullPath.EndsWith(".old", StringComparison.OrdinalIgnoreCase);
                try {
                    if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("跳过链接文件");
                    using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using ZipArchive archive = new(stream, ZipArchiveMode.Read, false);
                    ReadBudget budget = new();
                    ReadArchive(archive, Path.GetFullPath(fullPath), "", enabled, false, 0, result, budget, token);
                } catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                    error is JsonException || error is FormatException || error is DecoderFallbackException) {
                    if (enabled) Incomplete(result, Path.GetFileName(fullPath) + "：无法完整读取元数据（" + error.Message + "）。");
                    else result.Notices.Add(Path.GetFileName(fullPath) + "：已禁用，无法读取元数据。");
                }
            }
        }
        BuildReport(result, token);
        return result;
    }

    private sealed class ReadBudget {
        internal long Bytes;
        internal int Count;
    }

    private static bool IsModFile(string fullPath) {
        string name = fullPath;
        if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".old", StringComparison.OrdinalIgnoreCase)) name = Path.GetFileNameWithoutExtension(name)!;
        return new[] { ".jar", ".zip", ".litemod" }.Contains(Path.GetExtension(name)!.ToLowerInvariant());
    }

    private static void Incomplete(TReport report, string message) {
        report.MetadataIncomplete = true;
        if (!report.Notices.Contains(message)) report.Notices.Add(message);
    }

    private static string ReadText(ZipArchiveEntry entry) {
        if (entry.Length > MaxMetadataBytes) throw new InvalidDataException("元数据超过 1 MiB");
        using Stream stream = entry.Open();
        using StreamReader reader = new(stream, new UTF8Encoding(false, true), true);
        char[] buffer = new char[4096];
        StringBuilder content = new();
        int count;
        do {
            count = reader.Read(buffer, 0, buffer.Length);
            content.Append(buffer, 0, count);
            if (content.Length > MaxMetadataBytes) throw new InvalidDataException("元数据超过 1 MiB");
        } while (count > 0);
        return content.ToString();
    }

    private static void ReadArchive(ZipArchive archive, string rootPath, string nestedPath, bool enabled, bool nested,
        int depth, TReport report, ReadBudget budget, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        List<string> choices = new();
        Dictionary<string, string?> loaders = report.Context.LoaderVersions;
        if (loaders.ContainsKey("quilt_loader")) choices.Add("quilt.mod.json");
        if (loaders.ContainsKey("fabricloader") || loaders.ContainsKey("quilt_loader")) choices.Add("fabric.mod.json");
        if (loaders.ContainsKey("neoforge")) choices.Add("META-INF/neoforge.mods.toml");
        if (loaders.ContainsKey("forge") || loaders.ContainsKey("neoforge")) choices.Add("META-INF/mods.toml");
        choices.AddRange(new[] { "quilt.mod.json", "fabric.mod.json", "META-INF/neoforge.mods.toml", "META-INF/mods.toml", "mcmod.info", "litemod.json" });
        ZipArchiveEntry? metadata = choices.Distinct(StringComparer.Ordinal).Select(archive.GetEntry).FirstOrDefault(entry => entry is not null);
        if (metadata is null) {
            if (enabled) Incomplete(report, Path.GetFileName(rootPath) + (nested ? "!" + nestedPath : "") + "：没有可读取的本地模组声明。");
            return;
        }
        List<TMod> items = new();
        List<string> nestedJars = new();
        string content = ReadText(metadata);
        switch (metadata.FullName) {
            case "fabric.mod.json": {
                JObject json = JObject.Parse(content);
                if (Text(json["schemaVersion"]) != "1" && enabled) Incomplete(report, Path.GetFileName(rootPath) + "：Fabric 元数据并非受支持的 schemaVersion 1。");
                foreach (string name in new[] { "depends", "recommends", "suggests", "breaks", "conflicts" })
                    if (json[name] is not null && json[name] is not JObject && enabled) Incomplete(report, Path.GetFileName(rootPath) + "：无法完整读取 " + name + " 声明。");
                TMod item = NewMod(json["id"], json["name"], json["version"], rootPath, nestedPath + metadata.FullName, "Fabric", enabled, nested);
                item.Side = ReadSide(json["environment"]);
                item.IconPath = ReadIconPath(json["icon"]);
                foreach (JToken aliasId in ArrayValues(json["provides"]))
                    if (aliasId.Type == JTokenType.String) item.Provides[Text(aliasId)] = item.Version;
                ReadFabricDependencies(item, json);
                items.Add(item);
                foreach (JToken jar in ArrayValues(json["jars"])) {
                    string entryName = jar.Type == JTokenType.Object ? Text(jar["file"]) : "";
                    if (entryName != "") nestedJars.Add(entryName);
                }
                break;
            }
            case "quilt.mod.json": {
                JObject json = JObject.Parse(content);
                JObject? loader = json["quilt_loader"] as JObject;
                if (loader is null) throw new FormatException("缺少 quilt_loader");
                if (Text(json["schema_version"]) != "1" && enabled) Incomplete(report, Path.GetFileName(rootPath) + "：Quilt 元数据并非受支持的 schema_version 1。");
                foreach (string name in new[] { "depends", "breaks", "provides", "jars" })
                    if (loader[name] is not null && loader[name] is not JArray && enabled) Incomplete(report, Path.GetFileName(rootPath) + "：无法完整读取 " + name + " 声明。");
                TMod item = NewMod(loader["id"], loader["metadata"]?["name"], loader["version"], rootPath, nestedPath + metadata.FullName, "Quilt", enabled, nested);
                item.Group = Text(loader["group"]);
                item.IconPath = ReadIconPath(loader["metadata"]?["icon"]);
                item.Side = ReadSide(json["minecraft"]?["environment"]);
                foreach (JToken aliasValue in ArrayValues(loader["provides"])) {
                    string aliasId = aliasValue.Type == JTokenType.String ? Text(aliasValue) : Text(aliasValue["id"]);
                    if (aliasId == "") continue;
                    string? aliasVersion = aliasValue.Type == JTokenType.Object && aliasValue["version"] is not null ? Text(aliasValue["version"]) : item.Version;
                    item.Provides[aliasId] = aliasVersion;
                    if (!aliasId.Contains(":") && !string.IsNullOrEmpty(item.Group)) item.Provides[item.Group + ":" + aliasId] = aliasVersion;
                    if (aliasId.Contains(":")) item.Provides[aliasId.Substring(aliasId.LastIndexOf(':') + 1)] = aliasVersion;
                }
                foreach (JToken value in ArrayValues(loader["depends"])) item.Dependencies.Add(ReadQuiltDependency(value, ModDependencyKind.Required, true));
                foreach (JToken value in ArrayValues(loader["breaks"])) item.Dependencies.Add(ReadQuiltDependency(value, ModDependencyKind.Incompatible, false));
                items.Add(item);
                foreach (JToken jar in ArrayValues(loader["jars"]))
                    if (jar.Type == JTokenType.String) nestedJars.Add(Text(jar));
                break;
            }
            case "META-INF/mods.toml":
            case "META-INF/neoforge.mods.toml": {
                bool neo = metadata.FullName.Contains("neoforge") || loaders.ContainsKey("neoforge");
                ReadForge(content, archive, rootPath, nestedPath + metadata.FullName, enabled, nested, neo, items, report);
                ZipArchiveEntry? jarJar = archive.GetEntry("META-INF/jarjar/metadata.json");
                if (jarJar is not null) {
                    foreach (JToken jar in ArrayValues(JObject.Parse(ReadText(jarJar))["jars"])) {
                        string entryName = Text(jar["path"]);
                        if (entryName != "") nestedJars.Add(entryName);
                    }
                }
                break;
            }
            case "mcmod.info":
                ReadLegacy(JToken.Parse(content), rootPath, nestedPath + metadata.FullName, enabled, nested, items, report);
                break;
            case "litemod.json": {
                JObject json = JObject.Parse(content);
                TMod item = NewMod(json["name"], json["displayName"], json["version"], rootPath, nestedPath + metadata.FullName, "LiteLoader", enabled, nested);
                item.IconPath = ReadIconPath(json["icon"]);
                items.Add(item);
                if (enabled) Incomplete(report, Path.GetFileName(rootPath) + "：LiteLoader 依赖可能在运行时代码中声明。");
                break;
            }
        }
        if (items.Count == 0) throw new FormatException("声明中未找到模组 ID");
        report.Mods.AddRange(items);
        foreach (string entryName in nestedJars.Distinct(StringComparer.Ordinal)) {
            token.ThrowIfCancellationRequested();
            string[] parts = entryName.Replace('\\', '/').Split('/');
            if (entryName.StartsWith("/", StringComparison.Ordinal) || parts.Any(part => part == ".." || part == "")) {
                if (enabled) Incomplete(report, Path.GetFileName(rootPath) + "：跳过不安全的内嵌路径。");
                continue;
            }
            ZipArchiveEntry? jar = archive.GetEntry(entryName);
            if (jar is null) {
                if (enabled) Incomplete(report, Path.GetFileName(rootPath) + "：声明的内嵌文件不存在：" + entryName);
                continue;
            }
            if (depth >= MaxNestedDepth || budget.Count >= MaxNestedCount || jar.Length > MaxNestedBytes - budget.Bytes) {
                if (enabled) Incomplete(report, Path.GetFileName(rootPath) + "：内嵌模组达到读取上限。");
                continue;
            }
            budget.Bytes = checked(budget.Bytes + jar.Length);
            budget.Count = checked(budget.Count + 1);
            try {
                using MemoryStream buffer = new();
                using (Stream input = jar.Open()) {
                    byte[] bytes = new byte[8192];
                    int count;
                    long remaining = jar.Length;
                    do {
                        count = input.Read(bytes, 0, Convert.ToInt32(Math.Min(bytes.Length, checked(remaining + 1))));
                        if (count > remaining) throw new InvalidDataException("内嵌文件长度与声明不符");
                        buffer.Write(bytes, 0, count);
                        remaining = checked(remaining - count);
                    } while (count > 0);
                }
                buffer.Position = 0;
                using ZipArchive inner = new(buffer, ZipArchiveMode.Read, true);
                ReadArchive(inner, rootPath, nestedPath + entryName + "!", enabled, true, checked(depth + 1), report, budget, token);
            } catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is JsonException || error is FormatException || error is DecoderFallbackException) {
                if (enabled) Incomplete(report, Path.GetFileName(rootPath) + "!" + entryName + "：无法完整读取内嵌模组。");
            }
        }
    }

    private static TMod NewMod(JToken? id, JToken? name, JToken? version, string rootPath, string metadataPath, string format, bool enabled, bool nested) {
        string modId = Text(id);
        if (!Regex.IsMatch(modId, "^[A-Za-z0-9_.-]+$")) throw new FormatException("模组 ID 无效");
        return new TMod { Id = modId, Name = Text(name) == "" ? modId : Text(name), Version = Text(version), RootPath = rootPath,
            MetadataPath = metadataPath, Format = format, Enabled = enabled, IsNested = nested };
    }

    private static string Text(JToken? value) => value is null || value.Type == JTokenType.Null ? "" : value.ToString();
    private static IEnumerable<JToken> ArrayValues(JToken? value) => value is JArray array ? array.AsEnumerable() : Enumerable.Empty<JToken>();

    private static string ReadIconPath(JToken? value) {
        if (value is JObject obj) {
            value = obj.Properties().OrderBy(item => {
                int size;
                return int.TryParse(item.Name, out size) && size >= 32 ? size : int.MaxValue;
            }).Select(item => item.Value).FirstOrDefault();
        }
        if (value is null || value.Type != JTokenType.String) return "";
        string name = Text(value).Replace('\\', '/');
        if (name.StartsWith("/", StringComparison.Ordinal) || name.Contains(":") || name.Split('/').Any(part => part == ".." || part == "" || part == ".")) return "";
        return name;
    }

    private static string ReadSide(JToken? value) {
        if (value is JArray array) {
            List<string> values = array.Select(Text).ToList();
            if (values.Contains("*") || (values.Contains("client") && values.Contains("server"))) return "*";
            return string.Join(",", values);
        }
        return (Text(value) == "" ? "*" : Text(value)).ToLowerInvariant().Replace("dedicated_server", "server");
    }

    private static bool SideApplies(string? side, TContext context) => side == "*" || string.IsNullOrEmpty(side) || side == "both" ||
        side!.Split(',').Contains((context.Side ?? "client").ToLowerInvariant());

    private static void ReadFabricDependencies(TMod item, JObject json) {
        Dictionary<string, ModDependencyKind> kinds = new() {
            { "depends", ModDependencyKind.Required }, { "recommends", ModDependencyKind.Recommended }, { "suggests", ModDependencyKind.Suggested },
            { "breaks", ModDependencyKind.Incompatible }, { "conflicts", ModDependencyKind.Conflict }
        };
        foreach (KeyValuePair<string, ModDependencyKind> pair in kinds) {
            JObject? values = json[pair.Key] as JObject;
            if (values is null) continue;
            foreach (JProperty entry in values.Properties())
                item.Dependencies.Add(new TDependency { Id = CanonicalId(entry.Name), Versions = entry.Value.DeepClone(), KindValue = pair.Value });
        }
    }

    private static TDependency ReadQuiltDependency(JToken value, ModDependencyKind kind, bool any) {
        TDependency result = new() { KindValue = kind, Join = any ? "any" : "all" };
        if (value is JArray array) {
            foreach (JToken child in array) result.Children.Add(ReadQuiltDependency(child, kind, any));
            result.IsUnknown = result.Children.Count == 0;
        } else if (value.Type == JTokenType.String) result.Id = CanonicalId(Text(value));
        else if (value is JObject) {
            result.Id = CanonicalId(Text(value["id"]));
            result.Versions = value["versions"]?.DeepClone() ?? new JValue("*");
            result.Reason = Text(value["reason"]);
            if (Text(value["optional"]).Equals("true", StringComparison.OrdinalIgnoreCase)) {
                result.KindValue = ModDependencyKind.Optional;
                result.EnforceWhenPresent = true;
            }
            if (value["unless"] is not null) result.Unless = ReadQuiltDependency(value["unless"]!, ModDependencyKind.Required, true);
        } else result.IsUnknown = true;
        if (result.Children.Count == 0 && string.IsNullOrEmpty(result.Id)) result.IsUnknown = true;
        return result;
    }

    private sealed class TomlSection {
        internal string Name = null!;
        internal Dictionary<string, string> Values = new(StringComparer.Ordinal);
    }

    // A bounded TOML subset reader. Descriptions never become dependency declarations.
    private static List<TomlSection> ReadToml(string content) {
        List<TomlSection> sections = new();
        TomlSection current = new() { Name = "" };
        StringBuilder statement = new();
        sections.Add(current);
        char quote = '\0';
        bool triple = false, escape = false, comment = false;
        int square = 0, curly = 0, index = 0;
        while (index <= content.Length) {
            char ch = index == content.Length ? '\n' : content[index];
            if (comment) {
                if (ch != '\n') { index = checked(index + 1); continue; }
                comment = false;
            }
            if (quote != '\0') {
                statement.Append(ch);
                if (escape) escape = false;
                else if (ch == '\\' && quote == '"') escape = true;
                else if (ch == quote) {
                    if (triple) {
                        if (index + 2 < content.Length && content[index + 1] == quote && content[index + 2] == quote) {
                            statement.Append(quote, 2);
                            index = checked(index + 2);
                            quote = '\0';
                            triple = false;
                        }
                    } else quote = '\0';
                }
            } else if (ch == '#') comment = true;
            else if (ch == '"' || ch == '\'') {
                quote = ch;
                triple = index + 2 < content.Length && content[index + 1] == ch && content[index + 2] == ch;
                statement.Append(ch);
                if (triple) { statement.Append(ch, 2); index = checked(index + 2); }
            } else {
                if (ch == '[') square = checked(square + 1);
                if (ch == ']') square = checked(square - 1);
                if (ch == '{') curly = checked(curly + 1);
                if (ch == '}') curly = checked(curly - 1);
                if (ch == '\n' && square <= 0 && curly <= 0) {
                    string line = statement.ToString().Trim();
                    statement.Clear();
                    if (line.StartsWith("[", StringComparison.Ordinal)) {
                        Match header = Regex.Match(line, @"^\[{1,2}\s*([A-Za-z0-9_.-]+)\s*\]{1,2}$");
                        current = new TomlSection { Name = header.Success ? header.Groups[1].Value : "?" };
                        sections.Add(current);
                    } else if (line != "") {
                        Match assignment = Regex.Match(line, @"^([A-Za-z0-9_.-]+)\s*=\s*(.+)$", RegexOptions.Singleline);
                        if (assignment.Success) current.Values[assignment.Groups[1].Value] = assignment.Groups[2].Value.Trim();
                    }
                    square = 0;
                    curly = 0;
                } else statement.Append(ch);
            }
            index = checked(index + 1);
        }
        if (quote != '\0' || square != 0 || curly != 0) throw new FormatException("TOML 字符串或容器未结束");
        return sections;
    }

    private static string Scalar(TomlSection section, string key, string defaultValue = "") {
        if (!section.Values.TryGetValue(key, out string? found)) return defaultValue;
        string raw = found!;
        if (raw.StartsWith("\"\"\"", StringComparison.Ordinal) || raw.StartsWith("'''", StringComparison.Ordinal)) return defaultValue;
        if (raw.Length >= 2 && raw[0] == '\'' && raw.Last() == '\'') return raw.Substring(1, raw.Length - 2);
        if (raw.Length >= 2 && raw[0] == '"' && raw.Last() == '"') {
            try { return JsonConvert.DeserializeObject<string>(raw)!; }
            catch (JsonException) { return defaultValue; }
        }
        if (raw == "true" || raw == "false") return raw;
        return defaultValue;
    }

    private static void ReadForge(string content, ZipArchive archive, string rootPath, string metadataPath, bool enabled,
        bool nested, bool neo, List<TMod> items, TReport report) {
        List<TomlSection> sections = ReadToml(content);
        TomlSection globalSection = sections[0];
        if (enabled && sections.Any(section => section.Name == "?")) Incomplete(report, Path.GetFileName(rootPath) + "：存在未识别的 TOML 表名，部分声明未解析。");
        string manifestVersion = "";
        ZipArchiveEntry? manifest = archive.GetEntry("META-INF/MANIFEST.MF");
        if (manifest is not null) {
            string manifestText = Regex.Replace(ReadText(manifest), @"\r?\n ", "");
            Match match = Regex.Match(manifestText, @"(?m)^Implementation-Version:\s*([^\r\n]+)");
            if (match.Success) manifestVersion = match.Groups[1].Value.Trim();
        }
        foreach (TomlSection section in sections.Where(entry => entry.Name == "mods")) {
            string version = Scalar(section, "version");
            if (version == "${file.jarVersion}") version = manifestVersion;
            TMod item = NewMod(new JValue(Scalar(section, "modId")), new JValue(Scalar(section, "displayName")), new JValue(version), rootPath, metadataPath, neo ? "NeoForge" : "Forge", enabled, nested);
            item.IconPath = ReadIconPath(new JValue(Scalar(section, "logoFile", Scalar(globalSection, "logoFile"))));
            if (Scalar(globalSection, "clientSideOnly") == "true") item.Side = "client";
            items.Add(item);
        }
        foreach (TomlSection section in sections.Where(entry => entry.Name.StartsWith("dependencies.", StringComparison.Ordinal))) {
            string ownerId = section.Name.Substring("dependencies.".Length);
            TMod? item = items.FirstOrDefault(entry => string.Equals(entry.Id ?? "", ownerId, StringComparison.Ordinal));
            if (item is null) {
                if (enabled) Incomplete(report, Path.GetFileName(rootPath) + "：依赖声明所属 ID 不存在：" + ownerId);
                continue;
            }
            string mandatory = Scalar(section, "mandatory");
            string kindText = Scalar(section, "type", neo && string.IsNullOrEmpty(mandatory) ? "required" : "");
            ModDependencyKind kind;
            bool unknown = false;
            switch (kindText) {
                case "required": kind = ModDependencyKind.Required; break;
                case "optional": kind = ModDependencyKind.Optional; break;
                case "incompatible": kind = ModDependencyKind.Incompatible; break;
                case "discouraged": kind = ModDependencyKind.Conflict; break;
                default:
                    if (mandatory == "true") kind = ModDependencyKind.Required;
                    else if (mandatory == "false") kind = ModDependencyKind.Optional;
                    else { kind = ModDependencyKind.Required; unknown = true; }
                    break;
            }
            string id = Scalar(section, "modId");
            item.Dependencies.Add(new TDependency { Id = CanonicalId(id), Versions = new JValue(Scalar(section, "versionRange", "*")), Syntax = "maven",
                KindValue = kind, Side = ReadSide(new JValue(Scalar(section, "side", "*"))), Reason = Scalar(section, "reason"),
                EnforceWhenPresent = kind == ModDependencyKind.Optional, IsUnknown = unknown || string.IsNullOrEmpty(id) });
        }
        foreach (TMod item in items) {
            TomlSection? features = sections.FirstOrDefault(entry => entry.Name == "features." + item.Id);
            if (features is not null && features.Values.ContainsKey("java_version"))
                item.Dependencies.Add(new TDependency { Id = "java", Versions = new JValue(Scalar(features, "java_version")), Syntax = "maven", KindValue = ModDependencyKind.Required });
            // Forge FML uses its Forge major version. NeoForge language-loader numbering is separate.
            string language = Scalar(globalSection, "modLoader"), range = Scalar(globalSection, "loaderVersion");
            if (!neo && new[] { "javafml", "lowcodefml" }.Contains(language) && !string.IsNullOrEmpty(range))
                item.Dependencies.Add(new TDependency { Id = language, Versions = new JValue(range), Syntax = "maven", KindValue = ModDependencyKind.Required, Reason = "FML 语言加载器版本" });
        }
    }

    private static void ReadLegacy(JToken json, string rootPath, string metadataPath, bool enabled, bool nested, List<TMod> items, TReport report) {
        JToken? values = json is JArray ? json : json["modList"];
        foreach (JObject value in ArrayValues(values).OfType<JObject>()) {
            TMod item = NewMod(value["modid"], value["name"], value["version"], rootPath, metadataPath, "Forge (旧版)", enabled, nested);
            item.IconPath = ReadIconPath(value["logoFile"]);
            foreach (JToken required in ArrayValues(value["requiredMods"])) {
                string raw = Text(required);
                int at = raw.IndexOf('@');
                item.Dependencies.Add(new TDependency { Id = at < 0 ? raw : raw.Substring(0, at), Versions = new JValue(at < 0 ? "*" : raw.Substring(at + 1)), KindValue = ModDependencyKind.Required, Syntax = "maven" });
            }
            items.Add(item);
        }
        if (enabled) Incomplete(report, Path.GetFileName(rootPath) + "：旧版 Forge 还可能通过 @Mod 注解声明依赖。");
    }
}
