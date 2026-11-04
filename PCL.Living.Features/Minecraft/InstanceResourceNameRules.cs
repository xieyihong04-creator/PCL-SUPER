using System;
using System.IO;
using System.Text.RegularExpressions;

namespace PCL.Living.Features;

/// <summary>LF 本地资源名称校验；保留原检查顺序与当前平台的非法字符表。</summary>
public static class InstanceResourceNameRules {
    // LF f4aa0fae62c3ee8af24c81e61844c8fdd13c8983 InstanceResourceStore.SafeName。
    public static string SafeName(string value) {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.EndsWith(".") || value.Length > 120 || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value == "." || value == "..")
            throw new IOException("文件名无效，请使用不含路径和特殊字符的名称。");
        if (Regex.IsMatch(value, @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
            throw new IOException("此名称为系统保留名称。");
        if (value.StartsWith(".pcl-", StringComparison.OrdinalIgnoreCase))
            throw new IOException("此名称为启动器临时目录保留。");
        return value;
    }
}
