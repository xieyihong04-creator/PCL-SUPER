using System.Runtime.CompilerServices;

namespace PCL.Living.Features;

/// <summary>
/// 从 MeloongCore.Shared（LF 依赖）迁入的最小字符串工具集。
/// 语义与原实现一致，以保证 LF 写入磁盘的格式与解析行为不变。
/// </summary>
internal static class LivingStrings {
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ContainsIgnoreCase(this string value, string subString)
        => value.IndexOf(subString, StringComparison.OrdinalIgnoreCase) >= 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Lower(this string str) => str.ToLowerInvariant();

    public static string AfterLast(this string str, string? text, bool ignoreCase = false) {
        int pos = string.IsNullOrEmpty(text)
            ? -1
            : str.LastIndexOf(text!, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return pos < 0 ? str : str[(pos + text!.Length)..];
    }

    public static string AfterFirst(this string str, string? text, bool ignoreCase = false) {
        int pos = string.IsNullOrEmpty(text)
            ? -1
            : str.IndexOf(text!, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return pos < 0 ? str : str[(pos + text!.Length)..];
    }

    public static string? RegexSeek(this string str, string pattern, RegexOptions options = RegexOptions.None) {
        try {
            var match = Regex.Match(str, pattern, options);
            return match.Success ? match.Value : null;
        } catch (Exception ex) {
            Logger.Warn(ex, $"RegexSeek 失败（{pattern}）");
            return null;
        }
    }

    public static bool RegexCheck(this string str, string pattern, RegexOptions options = RegexOptions.None) {
        try {
            return Regex.IsMatch(str, pattern, options);
        } catch (Exception ex) {
            Logger.Warn(ex, $"RegexCheck 失败（{pattern}）");
            return false;
        }
    }

    public static List<string> RegexSearch(this string str, string pattern, RegexOptions options = RegexOptions.None) {
        try {
            return Regex.Matches(str, pattern, options).Select(m => m.Value).ToList();
        } catch (Exception ex) {
            Logger.Warn(ex, $"RegexSearch 失败（{pattern}）");
            return [];
        }
    }

    public static bool StartsWithF(this string str, string prefix, bool ignoreCase = false)
        => str.StartsWith(prefix, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static bool EndsWithF(this string str, string suffix, bool ignoreCase = false)
        => str.EndsWith(suffix, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <summary>
/// 日志门面。CE 侧通过 <see cref="Sink" /> 接到 PCL.Core 的日志管道；默认静默。
/// </summary>
public static class Logger {
    /// <summary>CE 宿主注入的日志接收器：级别、消息、异常。</summary>
    public static Action<string, string, Exception?>? Sink { get; set; }

    public static void Debug(string message) => Sink?.Invoke("Debug", message, null);
    public static void Info(string message) => Sink?.Invoke("Info", message, null);
    public static void Warn(string message) => Sink?.Invoke("Warn", message, null);
    public static void Warn(Exception ex) => Sink?.Invoke("Warn", ex.Message, ex);
    public static void Warn(Exception ex, string message) => Sink?.Invoke("Warn", message, ex);
    public static void Error(string message) => Sink?.Invoke("Error", message, null);
    public static void Error(Exception ex, string message) => Sink?.Invoke("Error", message, ex);
}
