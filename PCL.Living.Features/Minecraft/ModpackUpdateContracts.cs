using System.Net.Http;
using System.Threading;

namespace PCL.Living.Features;

/// <summary>整合包作者清单中的一个文件（对应 LF ModpackUpdate.vb 的 PackUpdateFile）。</summary>
public sealed class PackUpdateFile {
    public string Relative = null!;
    /// <summary>形如 <c>sha1:&lt;hex&gt;</c>。</summary>
    public string? Hash;
    public string? Identity;
    public List<string> Urls = [];
    /// <summary>暂存到本地后的实际路径；下载或解压后填入。</summary>
    public string? Payload;
    public bool OptionalFile;
}

/// <summary>解析后的整合包（Modrinth .mrpack 或 CurseForge .zip）。</summary>
public sealed class PackUpdatePackage {
    public string Name = "整合包";
    public string Version = "";
    public string Minecraft = "";
    public readonly Dictionary<ModLoaders, string> Loaders = [];
    public readonly Dictionary<string, PackUpdateFile> Files = new(StringComparer.OrdinalIgnoreCase);
    public JObject? Source;
}

/// <summary>预览中的一项差异。</summary>
public sealed class PackUpdateChange {
    public string Relative = null!;
    public string? Before;
    public PackUpdateFile? Incoming;
    public bool Conflict;
    public string? Reason;
}

/// <summary>更新计划：待替换的实际路径由 <see cref="PackUpdatePlan.Root" /> 推导。</summary>
public sealed class PackUpdatePlan {
    public string Root = null!;
    public PackUpdatePackage Package = null!;
    public readonly List<PackUpdateChange> Changes = [];
    public JObject Baseline = null!;
}

/// <summary>待下载项；宿主用自己的下载器执行，引擎只负责校验。</summary>
public sealed class PackUpdateNetFile {
    public List<string> Urls = null!;
    public string Destination = null!;
    /// <summary>形如 <c>sha1:&lt;hex&gt;</c>。</summary>
    public string Hash = null!;
}

/// <summary>宿主适配：路径限制、网络、下载、临时目录、版本隔离路径。</summary>
public sealed class PackUpdateHost {
    /// <summary>确保 <paramref name="path" /> 位于 <paramref name="root" /> 内，并拒绝链接。</summary>
    public Action<string, string> EnsureWithin = null!;

    /// <summary>发起一次 JSON 请求（用于 CurseForge 文件清单），返回响应体中的 <c>data</c>。</summary>
    public Func<string, HttpMethod, string?, JToken>? Request;

    /// <summary>下载并写入目标位置；失败时抛异常。为空表示由宿主另行处理（仅生成清单）。</summary>
    public Action<List<PackUpdateNetFile>, CancellationToken>? Download;

    /// <summary>为本次更新分配一个临时目录。</summary>
    public Func<string> TempFolder = null!;

    /// <summary>取版本文件夹对应的独立游戏目录（PathIndie）。</summary>
    public Func<string, string> IndiePath = null!;
}
