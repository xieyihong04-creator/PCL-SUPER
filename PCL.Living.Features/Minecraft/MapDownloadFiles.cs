using System;
using System.IO;
using System.Threading;

namespace PCL.Living.Features;

/// <summary>LF 已下载地图的落盘目录与安装交接；保留原两层暂存目录及碰撞处理。</summary>
public sealed class MapDownloadFiles {
    // LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 MapDownloadInstaller.vb.
    public const string DownloadFolderName = "PCLLFWorldDownload";
    private readonly Func<string, string> removeExtendedPrefix;
    private readonly InstanceResourceTransactions transactions;

    public MapDownloadFiles(Func<string, string> removeExtendedPrefix) {
        this.removeExtendedPrefix = removeExtendedPrefix;
        transactions = new InstanceResourceTransactions(removeExtendedPrefix);
    }

    private void EnsureWithin(string root, string path, bool allowRoot = false) =>
        InstanceResourcePaths.EnsureWithin(root, path, allowRoot, removeExtendedPrefix);

    public string GetDownloadFolder(string savesFolder) {
        string root = InstanceResourcePathCompatibility.GetFullPath(savesFolder);
        string folder = Path.Combine(root, DownloadFolderName);
        EnsureWithin(root, folder);
        if (File.Exists(Path.Combine(folder, "level.dat")))
            throw new IOException("地图压缩包目录与现有存档同名，请先重命名该存档。");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public string Install(string archivePath, string savesFolder, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        string full = InstanceResourcePathCompatibility.GetFullPath(archivePath);
        string root = InstanceResourcePathCompatibility.GetFullPath(savesFolder);
        EnsureWithin(Path.GetDirectoryName(full)!, full);
        string name = Path.GetFileNameWithoutExtension(full).TrimEnd(' ', '.');
        if (name.Length > 110) name = name.Substring(0, 110).TrimEnd(' ', '.');
        name = InstanceResourceNameRules.SafeName(name);
        string parent = Path.GetDirectoryName(root.TrimEnd('\\')) ?? root;
        if (string.IsNullOrEmpty(parent)) parent = root;
        string stage = Path.Combine(parent, ".pcl-import-" + Guid.NewGuid().ToString("N"));
        EnsureWithin(parent, root, true);
        EnsureWithin(parent, stage);
        Directory.CreateDirectory(root);
        try {
            // ImportWorld owns its nested extraction stage. The outer sibling
            // stage remains alive until a final destination move succeeds/fails.
            string world = transactions.ImportWorld(stage, full, "world", token);
            for (int index = 1; index <= 10000; index++) {
                token.ThrowIfCancellationRequested();
                string destination = Path.Combine(root, name + (index == 1 ? "" : " (" + index + ")"));
                EnsureWithin(root, destination);
                if (Directory.Exists(destination) || File.Exists(destination)) continue;
                try {
                    Directory.Move(world, destination);
                    return destination;
                } catch (IOException) when (Directory.Exists(destination) || File.Exists(destination)) {
                }
            }
            throw new IOException("没有可用的地图存档名称，原压缩包已保留。");
        } finally {
            transactions.DeleteStage(parent, stage);
        }
    }
}
