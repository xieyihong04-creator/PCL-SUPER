namespace PCL.Living.Features;

/// <summary>LF 原本地资源种类的标题与目录名；保留数组索引的异常边界。</summary>
public static class InstanceResourceKindRules {
    // LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 InstanceResourceStore.Title / FolderName。
    public static string Title(int kind) {
        string[] names = { "存档", "截图", "资源包", "光影包", "投影/原理图" };
        return names[kind];
    }

    public static string FolderName(int kind) {
        string[] folders = { "saves", "screenshots", "resourcepacks", "shaderpacks", "schematics" };
        return folders[kind];
    }
}
