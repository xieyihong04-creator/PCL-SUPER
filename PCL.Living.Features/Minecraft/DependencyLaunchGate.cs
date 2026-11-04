using System.Security.Cryptography;
using System.Text;

namespace PCL.Living.Features;

/// <summary>报告中单个模组的视图（由宿主从自身的检查结果填充）。</summary>
public sealed class DependencyReportMod {
    public string Key = "";
    public string Version = "";
    public string Format = "";
    public string RootPath = "";
    public bool Enabled;
    public bool IsNested;
    /// <summary>形如 <c>Required:modid</c> 的依赖描述。</summary>
    public List<string> Dependencies = [];
}

/// <summary>
/// 依赖检查结果的宿主无关视图。CE 适配层把 <c>CompDependency</c> 或本地检查结果填进来，
/// 引擎只负责计算指纹与判定“本次运行已确认”。
/// </summary>
public sealed class DependencyReportView {
    /// <summary>检查上下文序列化结果（游戏版本、加载器、Java 等），需稳定。</summary>
    public string ContextJson = "";
    public bool MetadataIncomplete;
    public List<string> Notices = [];
    public List<DependencyReportMod> Mods = [];
    public List<string> Issues = [];
}

/// <summary>
/// LF 启动前依赖判定的 C# 移植（对应 LF LaunchDependencyCheck.vb）。
/// 指纹覆盖上下文、模组清单、依赖声明、文件大小与时间戳、问题列表，
/// 任意一项变化都会要求玩家重新确认，避免在同一次运行中反复弹窗。
/// </summary>
public sealed class DependencyLaunchGate {
    private readonly Dictionary<string, string> accepted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>设置变化时清空已确认记录。</summary>
    public void SettingsChanged() => accepted.Clear();

    public static string FilePathKey(string folder) {
        string full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.ToLowerInvariant();
    }

    public static string Fingerprint(DependencyReportView report) {
        List<string> values = [report.ContextJson, report.MetadataIncomplete.ToString()];
        values.AddRange(report.Notices.OrderBy(value => value, StringComparer.Ordinal));
        foreach (DependencyReportMod mod in report.Mods.OrderBy(item => item.Key, StringComparer.Ordinal)) {
            values.Add(mod.Key + "|" + mod.Version + "|" + mod.Enabled + "|" + mod.Format);
            values.AddRange(mod.Dependencies.OrderBy(value => value, StringComparer.Ordinal));
        }
        foreach (string fileName in report.Mods.Select(item => item.RootPath).Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)) {
            try {
                FileInfo info = new(fileName);
                values.Add(fileName + "|" + (info.Exists ? info.Length + "|" + info.LastWriteTimeUtc.Ticks : "missing"));
            } catch (Exception) {
                values.Add(fileName + "|unavailable");
            }
        }
        values.AddRange(report.Issues.OrderBy(value => value, StringComparer.Ordinal));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", values)));
        return Convert.ToBase64String(hash);
    }

    public bool WasAccepted(string folder, DependencyReportView report) {
        return accepted.TryGetValue(FilePathKey(folder), out string? previous) && previous == Fingerprint(report);
    }

    public void Accept(string folder, DependencyReportView report) {
        accepted[FilePathKey(folder)] = Fingerprint(report);
    }
}
