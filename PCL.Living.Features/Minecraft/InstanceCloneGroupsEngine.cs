using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

/// <summary>只解析 LF 可见版本的来源分组；迁回仍经真实克隆引擎完整验证。</summary>
// LF InstanceCloneGroups.vb at f4aa0fae62c3ee8af24c81e61844c8fdd13c8983,
// unchanged through 202aa3b4e7703b61865c875c9e6d98d993e7f18c. No CE code reused.
public sealed class InstanceCloneGroupsEngine<TNode, TSet>
        where TNode : InstanceCloneGroupNodeData<TNode> where TSet : InstanceCloneGroupSetData<TNode> {
    private const long MaximumOriginBytes = 64 * 1024;
    private const int MaximumDepth = 32;
    private readonly Func<string, string> removeExtendedPrefix;
    private readonly Action<string> ensureNoLinks;
    private readonly Func<TNode> createNode;
    private readonly Func<TSet> createSet;
    public InstanceCloneGroupsEngine(Func<string, string> removeExtendedPrefix, Action<string> ensureNoLinks, Func<TNode> createNode, Func<TSet> createSet) {
        this.removeExtendedPrefix = removeExtendedPrefix; this.ensureNoLinks = ensureNoLinks; this.createNode = createNode; this.createSet = createSet;
    }
    public string Key(string versionFolder) {
        if (string.IsNullOrWhiteSpace(versionFolder) || !Path.IsPathRooted(versionFolder)) throw new IOException("版本显示路径必须是完整路径。");
        string full = Path.GetFullPath(removeExtendedPrefix(versionFolder));
        if (full.Length > Path.GetPathRoot(full)!.Length) full = full.TrimEnd('\\', '/');
        return full;
    }
    private string? ReadSource(string versionFolder, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        try {
            string? versions = Path.GetDirectoryName(versionFolder);
            if (!string.Equals(Path.GetFileName(versions), "versions", StringComparison.OrdinalIgnoreCase)) return null;
            ensureNoLinks(versionFolder);
            string marker = Path.Combine(versionFolder, "PCL", "CloneOrigin.json");
            ensureNoLinks(marker);
            if (!File.Exists(marker)) return null;
            JObject data;
            using (FileStream input = new(marker, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                if (input.Length > MaximumOriginBytes) return null;
                token.ThrowIfCancellationRequested();
                using (StreamReader reader = new(input, Encoding.UTF8, true)) data = JObject.Parse(reader.ReadToEnd());
            }
            if (data.Value<int?>("schema").GetValueOrDefault() != 1) return null;
            string? rawSource = data["sourceVersionFolder"]?.ToString();
            if (string.IsNullOrWhiteSpace(rawSource) || !Path.IsPathRooted(rawSource!)) return null;
            string source = Key(rawSource!);
            if (string.Equals(source, versionFolder, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetDirectoryName(source), versions, StringComparison.OrdinalIgnoreCase)) return null;
            ensureNoLinks(source);
            string? cloneName = data["cloneName"]?.ToString(), sourceName = data["sourceName"]?.ToString();
            if (cloneName is not null && !string.Equals(cloneName, Path.GetFileName(versionFolder), StringComparison.OrdinalIgnoreCase)) return null;
            if (sourceName is not null && !string.Equals(sourceName, Path.GetFileName(source), StringComparison.OrdinalIgnoreCase)) return null;
            string? rawGameRoot = data["sourceGameRoot"]?.ToString();
            if (rawGameRoot is not null) {
                string gameRoot = Key(rawGameRoot), mcRoot = Path.GetDirectoryName(versions!)!;
                if (!string.Equals(gameRoot, source, StringComparison.OrdinalIgnoreCase) && !string.Equals(gameRoot, mcRoot, StringComparison.OrdinalIgnoreCase)) return null;
                ensureNoLinks(gameRoot);
            }
            return source;
        } catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException || error is ArgumentException || error is InvalidCastException || error is OverflowException || error is FormatException || error is System.Security.SecurityException) {
            return null;
        }
    }
    public TSet Build(IEnumerable<string>? visibleFolders, CancellationToken token) {
        if (visibleFolders is null) throw new ArgumentNullException("VisibleFolders");
        TSet result = createSet(); List<TNode> order = new();
        foreach (string folder in visibleFolders) {
            token.ThrowIfCancellationRequested(); string address = Key(folder);
            if (result.ByPath.ContainsKey(address)) continue;
            TNode node = createNode(); node.VersionFolder = address; result.ByPath.Add(address, node); order.Add(node);
        }
        Dictionary<string, string> parents = new(StringComparer.OrdinalIgnoreCase);
        foreach (TNode node in order) {
            token.ThrowIfCancellationRequested(); node.DeclaredSourceFolder = ReadSource(node.VersionFolder!, token);
            if (node.DeclaredSourceFolder is not null && result.ByPath.ContainsKey(node.DeclaredSourceFolder)) parents.Add(node.VersionFolder!, node.DeclaredSourceFolder);
        }
        HashSet<string> invalid = new(StringComparer.OrdinalIgnoreCase);
        foreach (TNode node in order) {
            token.ThrowIfCancellationRequested(); HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase); string current = node.VersionFolder!;
            while (parents.ContainsKey(current)) {
                token.ThrowIfCancellationRequested();
                if (!seen.Add(current) || seen.Count > MaximumDepth || invalid.Contains(current)) { invalid.UnionWith(seen); break; }
                current = parents[current];
            }
        }
        foreach (TNode node in order) {
            token.ThrowIfCancellationRequested();
            if (!invalid.Contains(node.VersionFolder!) && parents.TryGetValue(node.VersionFolder!, out string? parent) && !invalid.Contains(parent)) { node.ParentFolder = parent; result.ByPath[parent].Children.Add(node); }
            else result.Roots.Add(node);
        }
        return result;
    }
    public List<TNode> Flatten(TNode node) {
        List<TNode> result = new(); Stack<TNode> pending = new(); pending.Push(node);
        while (pending.Count > 0) { TNode current = pending.Pop(); result.Add(current); foreach (TNode child in current.Children.AsEnumerable().Reverse()) pending.Push(child); }
        return result;
    }
    public bool Contains(TNode node, string versionFolder) {
        if (string.IsNullOrWhiteSpace(versionFolder)) return false;
        string address = Key(versionFolder);
        return Flatten(node).Any(item => string.Equals(item.VersionFolder, address, StringComparison.OrdinalIgnoreCase));
    }
    public TNode? Find(TSet set, string versionFolder) { set.ByPath.TryGetValue(Key(versionFolder), out TNode? result); return result; }
}
