using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;

namespace PCL.Living.Features;

/// <summary>LF 本地资源扫描、包信息与图片文件读取；图片呈现由原宿主同步完成。</summary>
public static class InstanceResourceCatalog {
    // LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 InstanceResourceStore:
    // Scan / ReadPackInfo / LoadImage. The factory preserves the caller's actual
    // derived entry and image callbacks update that same object before metadata.
    public static List<TEntry> Scan<TEntry>(string root, string folder, int kind, CancellationToken token,
            string downloadFolderName, Func<TEntry> factory, Action<TEntry, string, int> loadFileIcon,
            Action<TEntry, Stream, int> decodeStreamIcon, Func<string, string> normalizeLineEndings,
            Func<string, string> removeExtendedPrefix) where TEntry : InstanceResourceEntryData {
        InstanceResourcePaths.EnsureWithin(root, folder, true, removeExtendedPrefix);
        var result = new List<TEntry>();
        if (!Directory.Exists(folder)) return result;
        FileSystemInfo[] children = new DirectoryInfo(folder).GetFileSystemInfos();
        int previewCount = 0;
        // Enumeration/sort keys are evaluated outside the per-child try, before
        // the original first cancellation check. Empty/missing folders stay lazy.
        foreach (FileSystemInfo child in children.OrderByDescending(item => item.LastWriteTimeUtc)) {
            token.ThrowIfCancellationRequested();
            try {
                if (InstanceResourcePathText.StartsWithPathText(child.Name, ".pcl-") ||
                        (child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                bool isDirectory = (child.Attributes & FileAttributes.Directory) != 0;
                if (isDirectory) {
                    if (kind == 0 && InstanceResourcePathText.SamePathText(child.Name, downloadFolderName) &&
                            !File.Exists(ChildPath(child.FullName, "level.dat"))) continue;
                    if (kind == 1) continue;
                } else if (!InstanceResourceFileRules.AcceptFile(kind, child.Name)) {
                    continue;
                }

                TEntry entry = factory();
                entry.Name = child.Name;
                entry.FullPath = child.FullName;
                entry.IsDirectory = isDirectory;
                entry.ModifiedUtc = child.LastWriteTimeUtc;
                entry.CreatedUtc = child.CreationTimeUtc;
                entry.Length = isDirectory ? 0 : ((FileInfo)child).Length;
                string detail = isDirectory ? "文件夹" : InstanceResourceDetailsRules.FormatSize(entry.Length);
                if (kind == 0) {
                    detail = File.Exists(ChildPath(child.FullName, "level.dat"))
                        ? "Minecraft 存档" : "未找到 level.dat，存档可能不完整";
                    loadFileIcon(entry, ChildPath(child.FullName, "icon.png"), 64);
                } else if (kind == 2) {
                    ReadPackInfo(entry, ref detail, loadFileIcon, decodeStreamIcon, normalizeLineEndings);
                } else if (kind == 1 && previewCount < 80) {
                    loadFileIcon(entry, child.FullName, 64);
                    // This is the original attempt count, even when no image
                    // was returned. A failed callback does not advance it.
                    previewCount++;
                }
                entry.Description = detail + " · " + child.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                result.Add(entry);
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                // Moving/unreadable individual children do not hide other entries.
            }
        }
        if (kind != 0 && kind != 1)
            return result.OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, Comparer<string?>.Create(InstanceResourcePathText.ComparePathText)).ToList();
        return result;
    }

    public static void ReadPackInfo<TEntry>(TEntry entry, ref string detail,
            Action<TEntry, string, int> loadFileIcon, Action<TEntry, Stream, int> decodeStreamIcon,
            Func<string, string> normalizeLineEndings) where TEntry : InstanceResourceEntryData {
        try {
            string? metadata = null;
            if (entry.IsDirectory) {
                string fileName = ChildPath(entry.FullPath, "pack.mcmeta");
                if (File.Exists(fileName) && new FileInfo(fileName).Length <= 1024 * 1024)
                    metadata = File.ReadAllText(fileName);
                loadFileIcon(entry, ChildPath(entry.FullPath, "pack.png"), 64);
            } else {
                // Unlike ValidatePackage's OpenRead, the old metadata stream
                // permits concurrent writers and deletion while it is open.
                using (var stream = new FileStream(ReadablePath(entry.FullPath), FileMode.Open,
                        FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, false,
                        InstanceResourcePackageRules.EntryNameEncoding())) {
                    ZipArchiveEntry? json = archive.GetEntry("pack.mcmeta");
                    if (json is not null && json.Length <= 1024 * 1024) {
                        using (var reader = new StreamReader(json.Open())) {
                            metadata = reader.ReadToEnd();
                        }
                    }
                    ZipArchiveEntry? icon = archive.GetEntry("pack.png");
                    if (icon is not null && icon.Length <= 4 * 1024 * 1024) {
                        using (Stream input = icon.Open())
                        using (var buffer = new MemoryStream()) {
                            input.CopyTo(buffer);
                            buffer.Position = 0;
                            // Keep both archive and buffer alive during the
                            // synchronous decode; image assignment precedes JSON.
                            decodeStreamIcon(entry, buffer, 64);
                        }
                    }
                }
            }
            InstanceResourceDetailsRules.ApplyPackMetadata(metadata, ref detail, normalizeLineEndings);
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                JsonException or NotSupportedException or ArgumentException) {
            // Keep the original five-class filter. In particular, a ZIP decoder's
            // FormatException, InvalidDataException or cancellation still escapes.
        }
    }

    public static TImage? LoadImage<TImage>(string? fileName, int width, Func<Stream, int, TImage> decode)
            where TImage : class {
        try {
            if (!File.Exists(fileName) || new FileInfo(fileName!).Length > 64L * 1024 * 1024) return null;
            using (var stream = new FileStream(ReadablePath(fileName), FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                return decode(stream, width);
            }
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                NotSupportedException or ArgumentException or FormatException) {
            return null;
        }
    }

    private static string ChildPath(string? folder, string name) {
        // Framework Combine checked the raw directory before any later IO.
        LegacyPathCharacters.Check(folder);
        return Path.Combine(folder!, name);
    }

    private static string ReadablePath(string? fileName) {
        // Same public FileStream raw-character/normalization boundary already
        // established for C007c4; preserve this method's distinct share flags.
        LegacyPathCharacters.Check(fileName);
#if !NETFRAMEWORK
        if (Path.DirectorySeparatorChar == '\\' && !string.IsNullOrEmpty(fileName))
            return InstanceResourcePathCompatibility.GetFullPath(fileName);
#endif
        return fileName!;
    }
}
