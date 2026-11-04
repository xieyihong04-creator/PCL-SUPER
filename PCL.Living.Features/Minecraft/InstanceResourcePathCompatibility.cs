using System;
using System.IO;
#if !NETFRAMEWORK
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
#endif

namespace PCL.Living.Features;

/// <summary>保留 LF 资源边界的 Framework 路径检查；完整归约仍使用系统 API。</summary>
public static class InstanceResourcePathCompatibility {
    public static string GetFullPath(string path) {
#if NETFRAMEWORK
        return Path.GetFullPath(path);
#else
        if (Path.DirectorySeparatorChar != '\\') return Path.GetFullPath(path);
        if (path is null) return Path.GetFullPath(path!);
        // Framework 在 extended 路径的 NUL、空白和长度检查之前直接返回。
        if (IsExtended(path)) return path;
        if (path.IndexOf('\0') >= 0) throw InvalidCharacters();
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("The path is not of a legal form.");

        bool checkCharacters = !IsDevice(path);
        string prepared = SkipLeadingRootSpaces(path);
        // Frame 已在原串上判断过 extended；前导空格移走后仍须进入系统归约，
        // 不能再次触发现代 Path.GetFullPath 的 extended 原串提前返回。
        string normalized = (IsExtended(prepared) ? GetSystemFullPath(prepared, path) : Path.GetFullPath(prepared)).TrimEnd(TrimEndCharacters);
        ValidateNormalizedPath(normalized, checkCharacters);
        if (!IsDevice(normalized)) {
            if (normalized.IndexOfAny(new[] { '?', '*' }) >= 0) throw InvalidCharacters();
            int colon = normalized.IndexOf(':');
            if (colon >= 0 && (colon != 1 || !IsDriveLetter(normalized[0]) || normalized.IndexOf(':', colon + 1) >= 0))
                throw new NotSupportedException("The given path's format is not supported.");
        }
        return normalized;
#endif
    }

    /// <summary>Normalize 后祖先遍历所需的旧目录边界；不是所有原始输入的通用 BCL 替代。</summary>
    public static string? GetDirectoryName(string? path) {
#if NETFRAMEWORK
        return Path.GetDirectoryName(path);
#else
        if (Path.DirectorySeparatorChar != '\\') return Path.GetDirectoryName(path);
        // Unlike FullPath, Framework GetDirectoryName checks the original
        // non-device string before limited separator/length normalization.
        if (path is not null && !IsDevice(path)) {
            foreach (char value in path)
                if (value < 32 || value is '"' or '<' or '>' or '|') throw InvalidCharacters();
        }
        // Frame 把 NT extended 的根看作首个斜线，并保留原串的重复分隔符与 NUL。
        if (path is not null && path.StartsWith(@"\??\", StringComparison.Ordinal)) {
            int separator = Math.Max(1, path.LastIndexOfAny(new[] { '\\', '/' }));
            return path.Substring(0, separator);
        }
        if (path is not null && !IsExtended(path)) {
            path = NormalizeLimitedSeparators(SkipLeadingRootSpaces(path));
            ValidateLimitedLength(path);
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("The path is not of a legal form.");
        }
        string? parent = Path.GetDirectoryName(path);
        // Frame 的 dot-device 盘符根按 UNC 边界处理，不把末斜线计入根。
        // 仅修正实测根形状；其余标准化输入仍交给系统 API。
        if (parent is not null && IsDotDriveRoot(parent)) return parent.Substring(0, 6);
        if (parent is null && path is not null && IsDotDriveRoot(path)) return path.Substring(0, 6);
        return parent;
#endif
    }

#if !NETFRAMEWORK
    // 根据下列 MIT 来源的公开行为独立补充检查，未移植其内部归约／短名解析器：
    // microsoft/referencesource ec9fa9ae770d522a5b5f0607898044b7478574a3
    // mscorlib/system/io/path.cs、pathinternal.cs、security/permissions/fileiopermission.cs。
    // dotnet/coreclr abbb8f685929c7aeaa087dae46fedc1bc2af4b17
    // src/mscorlib/src/System/IO/Path.cs、LongPathHelper.cs。
    // GetFullPathNameW 失败类型同时依据 Reference Source system/io/__error.cs。
    // LF 默认 UseLegacyPathHandling／BlockLongPaths 均为 false；Framework 自身处理其原有开关。
    private static readonly char[] TrimEndCharacters = { '\t', '\n', '\v', '\f', '\r', ' ', '\u0085', '\u00a0' };

    private static string NormalizeLimitedSeparators(string path) {
        // Limited GetDirectoryName normalization only: no .. collapse, FullPath
        // trim, wildcard/ADS rejection or short-name expansion. Normalize before
        // counting, so repeated separators do not create false length failures.
        StringBuilder result = new(path.Length);
        for (int index = 0; index < path.Length; index++) {
            if (!IsSeparator(path[index])) result.Append(path[index]);
            else if (index <= 1 || !IsSeparator(path[index - 1])) result.Append('\\');
        }
        return result.ToString();
    }

    private static void ValidateLimitedLength(string path) {
        // Reference Source PathInternal.AreSegmentsTooLong: a middle component
        // includes its preceding separator in the checked span; terminal does not.
        if (path.Length >= 32767) throw new PathTooLongException();
        int previousSeparator = 0;
        for (int index = 0; index < path.Length; index++) {
            if (path[index] != '\\') continue;
            if (index - previousSeparator > 255) throw new PathTooLongException();
            previousSeparator = index;
        }
        if (path.Length - 1 - previousSeparator > 255) throw new PathTooLongException();
    }

    private static void ValidateNormalizedPath(string path, bool checkCharacters) {
        if (path.Length >= 32767) throw new PathTooLongException();
        int componentStart = 0;
        bool needsShare = path.StartsWith(@"\\", StringComparison.Ordinal) && !IsDevice(path);
        for (int index = 0; index < path.Length; index++) {
            char value = path[index];
            if (checkCharacters && (value < 32 || value is '"' or '<' or '>' or '|')) throw InvalidCharacters();
            if (value == '\\') {
                if (index - componentStart > 255) throw new PathTooLongException();
                componentStart = index + 1;
                if (needsShare && index >= 2) {
                    if (index == path.Length - 1) throw InvalidUnc();
                    needsShare = false;
                }
            }
        }
        if (needsShare) throw InvalidUnc();
        if (path.Length - componentStart > 255) throw new PathTooLongException();
    }

    private static string GetSystemFullPath(string path, string originalPath) {
        StringBuilder buffer = new(260);
        while (true) {
            uint length = GetFullPathNameW(path, (uint)buffer.Capacity, buffer, IntPtr.Zero);
            if (length == 0) {
                int error = Marshal.GetLastWin32Error();
                throw SystemPathError(error == 0 ? 161 : error, originalPath);
            }
            if (length < (uint)buffer.Capacity) return buffer.ToString();
            buffer.Capacity = checked((int)length + 1);
        }
    }

    private static Exception SystemPathError(int error, string path) {
        string message = new Win32Exception(error).Message;
        return error switch {
            2 => new FileNotFoundException(message, path),
            3 => new DirectoryNotFoundException(message),
            5 => new UnauthorizedAccessException(message),
            15 => new DriveNotFoundException(message),
            206 => new PathTooLongException(message),
            995 => new OperationCanceledException(message),
            _ => new IOException(message, unchecked((int)0x80070000) | error)
        };
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFullPathNameW(string path, uint bufferLength, StringBuilder buffer, IntPtr filePart);

    private static string SkipLeadingRootSpaces(string path) {
        int start = 0;
        while (start < path.Length && path[start] == ' ') start++;
        if (start == 0 || start == path.Length) return path;
        bool rooted = IsSeparator(path[start]);
        bool drive = start + 1 < path.Length && IsDriveLetter(path[start]) && path[start + 1] == ':';
        return rooted || drive ? path.Substring(start) : path;
    }

    private static bool IsExtended(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal);

    private static bool IsDevice(string path) => IsExtended(path) ||
        (path.Length >= 4 && IsSeparator(path[0]) && IsSeparator(path[1]) && (path[2] == '.' || path[2] == '?') && IsSeparator(path[3]));

    private static bool IsDotDriveRoot(string path) => path.Length == 7 && path.StartsWith(@"\\.\", StringComparison.Ordinal) && path[5] == ':' && IsSeparator(path[6]);

    private static bool IsSeparator(char value) => value == '\\' || value == '/';
    private static bool IsDriveLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    private static ArgumentException InvalidCharacters() => new("Illegal characters in path.");
    private static ArgumentException InvalidUnc() => new(@"The UNC path should be of the form \\server\share.");
#endif
}
