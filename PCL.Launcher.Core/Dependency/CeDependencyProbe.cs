using System.Text.Json.Nodes;
using PCL.Living.Features;
using Inspector = PCL.Living.Features.ModDependencyInspection<
    PCL.CeDependencyInspection.Context,
    PCL.CeDependencyInspection.Mod,
    PCL.CeDependencyInspection.Dependency,
    PCL.CeDependencyInspection.Edge,
    PCL.CeDependencyInspection.Issue,
    PCL.CeDependencyInspection.Impact,
    PCL.CeDependencyInspection.Report,
    PCL.CeDependencyInspection.OnlineProject,
    PCL.CeDependencyInspection.OnlineDependency,
    PCL.CeDependencyInspection.OnlineFile>;
using CompLoaderType = PCL.Comp.CompLoaderType;
using LocalCompFile = PCL.LocalComp.LocalCompFile;

namespace PCL;

/// <summary>
///     在 CE 的实例信息与 LF 依赖检查器之间做桥接。只做只读探测，不写文件。
/// </summary>
internal static class CeDependencyProbe
{
    internal const string ModFolderName = "mods";

    internal static string ModsFolder(McInstance instance) => Path.Combine(instance.PathIndie, ModFolderName);

    internal static CeDependencyInspection.Context ContextFor(McInstance instance, string? javaVersion = null)
    {
        var info = instance.Info;
        var minecraftVersion = info.Reliable
                               && !string.IsNullOrEmpty(info.VanillaName)
                               && info.VanillaName != McInstanceInfo.UnknownVanillaName
            ? info.VanillaName
            : null;
        var result = new CeDependencyInspection.Context
        {
            MinecraftVersion = minecraftVersion,
            JavaVersion = javaVersion,
            Side = "client",
            LoaderKnown = true
        };
        if (info.HasForge)
            result.LoaderVersions["forge"] = info.Forge;
        if (info.HasNeoForge)
            result.LoaderVersions["neoforge"] = info.NeoForge;
        if (info.HasFabric)
            result.LoaderVersions["fabricloader"] = info.Fabric;
        if (info.HasQuilt)
        {
            result.LoaderVersions["quilt_loader"] = info.Quilt;
            result.LoaderVersions["fabricloader"] = info.Fabric;
        }

        if (info.HasLiteLoader)
            result.LoaderVersions["liteloader"] = null;
        if (result.LoaderVersions.ContainsKey("forge")
            && System.Text.RegularExpressions.Regex.IsMatch(minecraftVersion ?? "", @"^1\.(?:[0-9]|1[0-2])(?:\.|$)"))
            result.LegacyForgeVersionFolder = minecraftVersion;
        if (result.LoaderVersions.ContainsKey("neoforge") && minecraftVersion == "1.20.1")
            result.LoaderVersions["forge"] = result.LoaderVersions["neoforge"];
        if (info.HasOptiFine && !string.IsNullOrEmpty(info.OptiFine))
            result.AdditionalBuiltins["optifine"] = info.OptiFine;
        return result;
    }

    /// <summary>检查器上下文必须稳定：同一次检查与后续确认使用同一字符串。</summary>
    internal static string ContextJson(CeDependencyInspection.Context context)
    {
        var loaders = new JsonObject();
        foreach (var pair in context.LoaderVersions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            loaders[pair.Key] = pair.Value is null ? null : JsonValue.Create(pair.Value);
        var builtins = new JsonObject();
        foreach (var pair in context.AdditionalBuiltins.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builtins[pair.Key] = pair.Value is null ? null : JsonValue.Create(pair.Value);
        var json = new JsonObject
        {
            ["minecraft"] = context.MinecraftVersion is null ? null : JsonValue.Create(context.MinecraftVersion),
            ["java"] = context.JavaVersion is null ? null : JsonValue.Create(context.JavaVersion),
            ["side"] = JsonValue.Create(context.Side),
            ["loaderKnown"] = JsonValue.Create(context.LoaderKnown),
            ["loaders"] = loaders,
            ["builtins"] = builtins,
            ["legacyForge"] = context.LegacyForgeVersionFolder is null
                ? null
                : JsonValue.Create(context.LegacyForgeVersionFolder)
        };
        return json.ToJsonString();
    }

    internal static List<CompLoaderType> LoadersFor(McInstance instance)
    {
        var info = instance.Info;
        var loaders = new List<CompLoaderType>();
        if (info.HasForge)
            loaders.Add(CompLoaderType.Forge);
        if (info.HasNeoForge)
            loaders.Add(CompLoaderType.NeoForge);
        if (info.HasFabric)
            loaders.Add(CompLoaderType.Fabric);
        if (info.HasQuilt)
            loaders.AddRange([CompLoaderType.Fabric, CompLoaderType.Quilt]);
        if (info.HasLiteLoader)
            loaders.Add(CompLoaderType.LiteLoader);
        return loaders;
    }

    internal static List<string> LoaderNames(McInstance instance) =>
    [
        .. LoadersFor(instance)
            .Where(loader => loader != CompLoaderType.Any)
            .Select(loader => loader.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
    ];

    internal static DependencyReportView ToView(CeDependencyInspection.Report report, CeDependencyInspection.Context context)
    {
        var view = new DependencyReportView
        {
            ContextJson = ContextJson(context),
            MetadataIncomplete = report.MetadataIncomplete,
            Notices = [.. report.Notices]
        };
        foreach (var mod in report.Mods)
        {
            view.Mods.Add(new DependencyReportMod
            {
                Key = mod.Key,
                Version = mod.Version ?? "",
                Format = mod.Format ?? "",
                RootPath = mod.RootPath,
                Enabled = mod.Enabled,
                IsNested = mod.IsNested,
                Dependencies =
                [
                    .. mod.Dependencies.Select(dependency =>
                        dependency.KindValue + ":" + dependency.Display)
                ]
            });
        }

        foreach (var issue in report.Issues)
            view.Issues.Add((issue.IsError ? "E:" : "W:") + issue.Message);
        return view;
    }

    /// <summary>
    ///     CE 把联网取得的 CompFile 缓存在临时目录的 LocalComp.json 中，键为文件 SHA1 + 版本 + 加载器。
    ///     命中该缓存即代表“平台记录与本地文件精确对应”，无需再按名称猜测。
    /// </summary>
    internal static JsonObject LoadLocalCompCache()
    {
        try
        {
            var path = Path.Combine(LauncherPaths.Temp, "Cache", "LocalComp.json");
            if (!File.Exists(path))
                return new JsonObject();
            var content = LauncherCommon.ReadFile(path);
            if (string.IsNullOrWhiteSpace(content))
                return new JsonObject();
            return LauncherCommon.GetJson(content) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(ex, "[Deps] 读取本地 Mod 信息缓存失败");
            return new JsonObject();
        }
    }

    internal static bool Hydrate(
        LocalCompFile file,
        JsonObject cache,
        McInstance instance,
        List<CompLoaderType> loaders)
    {
        try
        {
            if (cache.Count == 0)
                return false;
            // 先按文件哈希命中缓存，避免为没有平台记录的模组重复解析压缩包。
            var key = file.ModrinthHash + instance.Info.VanillaName + string.Join("", loaders);
            if (cache[key] is not JsonObject cached)
                return false;
            file.PreferredLoader = LocalComp.GetPreferredLoader(instance);
            file.Load();
            file.FromJson(cached);
            return file.CompFile is not null;
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(ex, "[Deps] 读取模组平台记录失败：" + file.FileName);
            return false;
        }
    }

    internal static IEnumerable<string> EnumerateModFiles(string folder)
    {
        if (!Directory.Exists(folder))
            return [];
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(LocalCompFile.IsModFile)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LauncherCommon.Log(ex, "[Deps] 枚举模组目录失败：" + folder);
            return [];
        }
    }
}
