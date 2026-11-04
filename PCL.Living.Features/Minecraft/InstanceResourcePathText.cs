using System;
#if !NETFRAMEWORK
using System.ComponentModel;
using System.Runtime.InteropServices;
#endif

namespace PCL.Living.Features;

/// <summary>保留 LF 路径边界使用的 Framework Windows ordinal 比较。</summary>
public static class InstanceResourcePathText {
    // Reference Source ec9fa9ae770d522a5b5f0607898044b7478574a3:
    // String.Equals / StartsWith -> TextInfo.CompareOrdinalIgnoreCase[Ex];
    // textinfo.cs:977 explicitly identifies the underlying CompareStringOrdinal API.
    // The independent CLR4 oracle still calls the original String APIs.
    public static bool SamePathText(string value, string? other) {
#if NETFRAMEWORK
        return value.Equals(other, StringComparison.OrdinalIgnoreCase);
#else
        if (!OperatingSystem.IsWindows()) return value.Equals(other, StringComparison.OrdinalIgnoreCase);
        // An instance-style null receiver must still fail here. In particular, the
        // original drive-root walk throws NRE at its loop condition, before GetDirectoryName.
        int length = value.Length;
        if (other is null || length != other.Length) return false;
        return length == 0 || WindowsEqual(value, other, length);
#endif
    }

    // The resource catalog needs a total order, not just an equality predicate.
    // Consumers use the comparison sign; Framework does not promise its magnitude.
    public static int ComparePathText(string? value, string? other) {
#if NETFRAMEWORK
        return StringComparer.OrdinalIgnoreCase.Compare(value, other);
#else
        if (!OperatingSystem.IsWindows()) return StringComparer.OrdinalIgnoreCase.Compare(value, other);
        if (value is null) return other is null ? 0 : -1;
        if (other is null) return 1;
        if (value.Length == 0) return other.Length == 0 ? 0 : -1;
        if (other.Length == 0) return 1;
        int result = CompareStringOrdinal(value, value.Length, other, other.Length, true);
        if (result == 0)
            throw new ArgumentException("Windows ordinal comparison failed.", new Win32Exception(Marshal.GetLastWin32Error()));
        return result - 2;
#endif
    }

    public static bool StartsWithPathText(string value, string prefix) {
#if NETFRAMEWORK
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
#else
        if (!OperatingSystem.IsWindows()) return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        int length = value.Length;
        // String.StartsWith names its prefix parameter "value" in the original API.
        if (prefix is null) throw new ArgumentNullException("value");
        int prefixLength = prefix.Length;
        return length >= prefixLength && (prefixLength == 0 || WindowsEqual(value, prefix, prefixLength));
#endif
    }

#if !NETFRAMEWORK
    private static bool WindowsEqual(string value, string other, int length) {
        // Both lengths are explicit UTF16 code-unit counts: embedded NUL and surrogate
        // pairs must not be truncated or reinterpreted as scalar-counted strings.
        int result = CompareStringOrdinal(value, length, other, length, true);
        if (result == 0) {
            int error = Marshal.GetLastWin32Error();
            throw new ArgumentException("Windows ordinal comparison failed.", new Win32Exception(error));
        }
        return result == 2; // CSTR_EQUAL; 1 and 3 are successful unequal results.
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int CompareStringOrdinal(string value, int valueLength, string other, int otherLength, [MarshalAs(UnmanagedType.Bool)] bool ignoreCase);
#endif
}
