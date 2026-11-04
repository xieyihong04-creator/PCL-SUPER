using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

/// <summary>LF 资源回收记录、列表、还原与彻底删除；原派生条目和图片仍由宿主提供。</summary>
public sealed class ResourceRecycleFiles<TEntry> where TEntry : InstanceResourceEntryData {
    // LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 ResourceRecycleStore.vb.
    // Keep the actual host's PathUtils.IsSame: its contract is not replaced by
    // the resource Normalize/ordinal helpers or a newly chosen path policy.
    private readonly Func<string, string> removeExtendedPrefix;
    private readonly Func<string, string, bool> samePath;
    private readonly Func<TEntry> factory;
    private readonly Action<TEntry, string, int> loadIcon;

    public ResourceRecycleFiles(Func<string, string> removeExtendedPrefix,
            Func<string, string, bool> samePath, Func<TEntry> factory, Action<TEntry, string, int> loadIcon) {
        this.removeExtendedPrefix = removeExtendedPrefix;
        this.samePath = samePath;
        this.factory = factory;
        this.loadIcon = loadIcon;
    }

    public class Record {
        public string Id = null!;
        public string Box = null!;
        public string Destination = null!;
        public TEntry Entry = null!;
    }

    private void EnsureWithin(string root, string path) =>
        InstanceResourcePaths.EnsureWithin(root, path, false, removeExtendedPrefix);

    public Record ReadRecord(string root, string recycle, string? id) {
        if (!Regex.IsMatch(id ?? "", @"\A[0-9a-f]{32}\z"))
            throw new IOException("回收站记录编号无效。");
        if ((File.GetAttributes(recycle) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("不操作链接形式的回收站。");
        string box = Path.Combine(recycle, id!);
        string meta = Path.Combine(box, "resource.json");
        string payload = Path.Combine(box, "content");
        EnsureWithin(recycle, meta);
        EnsureWithin(recycle, payload);
        if (new FileInfo(meta).Length > 65536) throw new IOException("回收站记录过大。");
        JObject json = JObject.Parse(File.ReadAllText(meta));
        if (new[] { "root", "name", "folder" }.Any(key => json[key] is null || json[key]!.Type != JTokenType.String) ||
                json["directory"] is null || json["directory"]!.Type != JTokenType.Boolean)
            throw new IOException("回收站记录格式无效。");
        if (!samePath(root, json["root"]?.ToString()!))
            throw new IOException("此记录不属于当前资源目录。");
        string name = InstanceResourceNameRules.SafeName(json["name"]!.ToString());
        string destination = Path.Combine(json["folder"]!.ToString(), name);
        EnsureWithin(root, destination);
        bool isDirectory = json["directory"]!.ToObject<bool>();
        if (isDirectory ? !Directory.Exists(payload) : !File.Exists(payload))
            throw new IOException("回收站内容已不存在或类型已变化。");

        var record = new Record { Id = id!, Box = box, Destination = destination };
        TEntry entry = factory();
        entry.Name = name;
        entry.FullPath = payload;
        entry.RecycleId = id;
        entry.IsDirectory = isDirectory;
        entry.CreatedUtc = Directory.GetCreationTimeUtc(box);
        entry.ModifiedUtc = File.GetLastWriteTimeUtc(payload);
        entry.Length = isDirectory ? 0 : new FileInfo(payload).Length;
        entry.Description = "删除于 " + Directory.GetCreationTime(box).ToString("yyyy-MM-dd HH:mm") +
            " · 原位置：" + Path.GetDirectoryName(destination);
        record.Entry = entry;
        return record;
    }

    public List<TEntry> List(string root, string recycle, int kind) {
        var result = new List<TEntry>();
        if (!Directory.Exists(recycle)) return result;
        // Keep lazy directory enumeration outside the per-record try/filter.
        foreach (string box in Directory.EnumerateDirectories(recycle)) {
            try {
                TEntry item = ReadRecord(root, recycle, Path.GetFileName(box)).Entry;
                if (kind == 0) loadIcon(item, Path.Combine(item.FullPath!, "icon.png"), 40);
                if (kind == 1) loadIcon(item, item.FullPath!, 64);
                result.Add(item);
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) {
                // Invalid/foreign records stay on disk and are not exposed as items.
            }
        }
        return result.OrderByDescending(item => item.CreatedUtc).ToList();
    }

    public void Restore(string root, string recycle, IEnumerable<string> ids, CancellationToken token) {
        // Distinct uses the original default string comparer; materialize all
        // records and check every collision before any cancellation/move loop.
        List<Record> items = ids.Distinct().Select(id => ReadRecord(root, recycle, id)).ToList();
        foreach (Record item in items) {
            if (File.Exists(item.Destination) || Directory.Exists(item.Destination))
                throw new IOException("原位置已有同名资源，未覆盖。请先重命名或移走它，再还原。");
        }
        foreach (Record item in items) {
            token.ThrowIfCancellationRequested();
            string payload = item.Entry.FullPath!;
            Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
            if (item.Entry.IsDirectory) Directory.Move(payload, item.Destination);
            else File.Move(payload, item.Destination);
            File.Delete(Path.Combine(item.Box, "resource.json"));
            Directory.Delete(item.Box, false);
        }
    }

    private void ValidateTree(string recycle, string folder) {
        EnsureWithin(recycle, folder);
        foreach (string item in Directory.EnumerateFileSystemEntries(folder)) {
            EnsureWithin(recycle, item);
            if (Directory.Exists(item)) ValidateTree(recycle, item);
        }
    }

    public void Purge(string root, string recycle, IEnumerable<string> ids, CancellationToken token) {
        List<Record> items = ids.Distinct().Select(id => ReadRecord(root, recycle, id)).ToList();
        foreach (Record item in items) {
            token.ThrowIfCancellationRequested();
            // The original recursive traversal has no internal cancellation
            // check; do not move deletion into this prevalidation pass.
            ValidateTree(recycle, item.Box);
        }
        foreach (Record item in items) {
            token.ThrowIfCancellationRequested();
            if (item.Entry.IsDirectory) Directory.Delete(item.Entry.FullPath!, true);
            else File.Delete(item.Entry.FullPath!);
            File.Delete(Path.Combine(item.Box, "resource.json"));
            Directory.Delete(item.Box, false);
        }
    }
}
