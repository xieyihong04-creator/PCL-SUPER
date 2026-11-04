using System.IO.Compression;

namespace PCL.Living.Features;

/// <summary>地图导入的宿主适配：只提供路径越界检查。</summary>
public sealed class MapDownloadHost {
    public Action<string, string> EnsureWithin = null!;
}

/// <summary>LF 地图下载与安装的 C# 移植（对应 LF MapDownloadInstaller.vb 与 InstanceResourceStore 的导入规则）。</summary>
public sealed class MapDownloadInstaller {
    public const string DownloadFolderName = "PCLLFWorldDownload";

    private readonly MapDownloadHost host;

    public MapDownloadInstaller(MapDownloadHost host) => this.host = host;

    /// <summary>地图压缩包默认保存目录；与现有存档同名时拒绝，避免覆盖。</summary>
    public string GetDownloadFolder(string savesFolder) {
        string root = Path.GetFullPath(savesFolder);
        string folder = Path.Combine(root, DownloadFolderName);
        host.EnsureWithin(root, folder);
        if (File.Exists(Path.Combine(folder, "level.dat"))) throw new IOException("地图压缩包目录与现有存档同名，请先重命名该存档。");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>解压压缩包并安装为存档，返回最终存档目录；保留原压缩包。</summary>
    public string Install(string archivePath, string savesFolder, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        string full = Path.GetFullPath(archivePath);
        string root = Path.GetFullPath(savesFolder);
        host.EnsureWithin(Path.GetDirectoryName(full)!, full);
        string name = Path.GetFileNameWithoutExtension(full).TrimEnd(' ', '.');
        if (name.Length > 110) name = name[..110].TrimEnd(' ', '.');
        name = SafeName(name);
        string parent = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar)) ?? root;
        if (string.IsNullOrEmpty(parent)) parent = root;
        string stage = Path.Combine(parent, ".pcl-import-" + Guid.NewGuid().ToString("N"));
        host.EnsureWithin(parent, root);
        host.EnsureWithin(parent, stage);
        Directory.CreateDirectory(root);
        try {
            string world = ImportWorld(stage, full, "world", token);
            for (int index = 1; index <= 10000; index++) {
                token.ThrowIfCancellationRequested();
                string destination = Path.Combine(root, name + (index == 1 ? "" : " (" + index + ")"));
                host.EnsureWithin(root, destination);
                if (Directory.Exists(destination) || File.Exists(destination)) continue;
                try {
                    Directory.Move(world, destination);
                    return destination;
                } catch (IOException) when (Directory.Exists(destination) || File.Exists(destination)) {
                    // 与其他导入抢占了同一名称，继续尝试下一个。
                }
            }
            throw new IOException("没有可用的地图存档名称，原压缩包已保留。");
        } finally {
            DeleteStage(parent, stage);
        }
    }

    /// <summary>解压到临时目录并定位唯一的 level.dat，返回存档根目录。</summary>
    private string ImportWorld(string root, string source, string name, CancellationToken token) {
        string destination = Path.Combine(root, SafeName(name));
        host.EnsureWithin(root, destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("已存在同名存档，未覆盖原存档。");
        Directory.CreateDirectory(root);
        string stage = Path.Combine(root, ".pcl-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try {
            using (ZipArchive archive = ZipFile.OpenRead(source)) {
                foreach (ZipArchiveEntry entry in archive.Entries) {
                    token.ThrowIfCancellationRequested();
                    string relative = entry.FullName.Replace('/', '\\');
                    if (Path.IsPathRooted(relative)
                        || relative.Split('\\').Any(part => part == ".." || part.Contains(':'))
                        || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                        throw new IOException("存档压缩包包含越界路径或链接。");
                    if (relative == "") continue;
                    // 校验用 Windows 分隔符语义，落盘用当前平台分隔符。
                    string target = Path.GetFullPath(Path.Combine(stage, relative.Replace('\\', Path.DirectorySeparatorChar)));
                    host.EnsureWithin(stage, target);
                    if (relative.EndsWith('\\')) {
                        Directory.CreateDirectory(target);
                    } else {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        using Stream input = entry.Open();
                        using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        input.CopyTo(output);
                    }
                }
            }
            string[] candidates = Directory.EnumerateFiles(stage, "level.dat", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName).OfType<string>().ToArray();
            if (candidates.Length != 1) throw new IOException("压缩包中没有找到单个 Minecraft 存档（level.dat）。");
            token.ThrowIfCancellationRequested();
            Directory.Move(candidates[0], destination);
            return destination;
        } finally {
            DeleteStage(root, stage);
        }
    }

    private void DeleteStage(string root, string stage) {
        if (!Directory.Exists(stage)) return;
        host.EnsureWithin(root, stage);
        if (!Regex.IsMatch(Path.GetFileName(stage), @"\A\.pcl-import-[0-9a-f]{32}\z")) throw new IOException("无效的导入临时目录。");
        Directory.Delete(stage, true);
    }

    private static string SafeName(string value) {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.EndsWith('.') || value.Length > 120
            || value.Any(c => c < 32 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
            || value is "." or "..")
            throw new IOException("文件名无效，请使用不含路径和特殊字符的名称。");
        if (Regex.IsMatch(value, @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
            throw new IOException("此名称为系统保留名称。");
        if (value.StartsWith(".pcl-", StringComparison.OrdinalIgnoreCase))
            throw new IOException("此名称为启动器临时目录保留。");
        return value;
    }
}
