using System;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

/// <summary>LF 原本地资源大小与包元数据显示；保留当前文化和字符串处理顺序。</summary>
public static class InstanceResourceDetailsRules {
    // LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 InstanceResourceStore.FormatSize。
    public static string FormatSize(long size) {
        if (size < 1024) return size.ToString() + " B";
        int unit = Math.Min(3, (int)Math.Floor(Math.Log(size, 1024)));
        string[] units = { " B", " KB", " MB", " GB" };
        return (size / Math.Pow(1024, unit)).ToString("0.#") + units[unit];
    }

    // LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 ReadPackInfo's final metadata block.
    // IO, icons and the original exception filter remain in the caller. The
    // adapter supplies the actual pinned Shared normalization implementation.
    public static void ApplyPackMetadata(string? metadata, ref string detail, Func<string, string> normalize) {
        if (metadata is null) return;
        JToken? pack = InstanceResourcePackMetadataCompatibility.ReadObject(metadata)["pack"];
        JToken? description = pack?["description"];
        string? text = description?.Type == JTokenType.String ? description?.ToString() : InstanceResourcePackMetadataCompatibility.TokenText(description?["text"]);
        if (!string.IsNullOrWhiteSpace(text))
            detail = normalize(text!).Substring(0, Math.Min(180, normalize(text!).Length));
        if (pack?["pack_format"] is not null)
            detail += " · 资源格式 " + InstanceResourcePackMetadataCompatibility.TokenText(pack["pack_format"]);
    }
}
