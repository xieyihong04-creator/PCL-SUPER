using System;
using System.IO;

namespace PCL.Living.Features;

/// <summary>供 GetFileName／Combine／GetExtension 调用前使用的 Framework Windows 原串字符检查。</summary>
internal static class LegacyPathCharacters {
    // 不直接用于 GetFullPath：后者先归约，可允许非法字符所在段被 .. 消除。
    // 原样提取 LF 2d111e141fec34d6f284c1060884bf1ac02290dc 的
    // QuickPlayWorldRules.CheckLegacyPathCharacters／IsSeparator（C006a 已有适配）。
    // 原行为依据 microsoft/referencesource ec9fa9ae770d522a5b5f0607898044b7478574a3
    // mscorlib/system/io/path.cs CheckInvalidPathChars 与 pathinternal.cs HasIllegalCharacters。
    internal static void Check(string? path) {
#if !NETFRAMEWORK
        // Framework 在 GetFileName／Combine 前抛 ArgumentException；日志只捕获 IOException。
        // 现代 Windows 保留这个错误边界；Framework 自身仍负责其原始系统文案。
        if (path is null || Path.DirectorySeparatorChar != '\\') return;
        bool legacy = AppContext.TryGetSwitch("Switch.System.IO.UseLegacyPathHandling", out bool enabled) && enabled;
        bool device = path.Length >= 4 &&
            ((path[0] == '\\' && (path[1] == '\\' || path[1] == '?') && path[2] == '?' && path[3] == '\\') ||
             (IsSeparator(path[0]) && IsSeparator(path[1]) && (path[2] == '.' || path[2] == '?') && IsSeparator(path[3])));
        // 现代 GetInvalidPathChars 的集合也已变化，不能用它替代旧集合。
        if (legacy || !device) {
            foreach (char value in path) {
                if (value < 32 || value is '"' or '<' or '>' or '|')
                    throw new ArgumentException("Illegal characters in path.");
            }
        }
#endif
    }

#if !NETFRAMEWORK
    private static bool IsSeparator(char value) => value == '\\' || value == '/';
#endif
}
