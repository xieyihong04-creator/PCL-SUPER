using System;
using System.Collections.Generic;
using System.Linq;

namespace PCL.Living.Features;

public sealed class InstanceKeybindingViewResult<T> where T : InstanceKeybindingData {
    public int MatchingCount { get; }
    public int ConflictCount { get; }
    public int VisibleCount { get; }
    public IReadOnlyList<InstanceKeybindingViewBlock<T>> Blocks { get; }

    internal InstanceKeybindingViewResult(int matchingCount, int conflictCount, int visibleCount, IReadOnlyList<InstanceKeybindingViewBlock<T>> blocks) {
        MatchingCount = matchingCount;
        ConflictCount = conflictCount;
        VisibleCount = visibleCount;
        Blocks = blocks;
    }
}

public sealed class InstanceKeybindingViewBlock<T> where T : InstanceKeybindingData {
    public bool IsGroup { get; }
    public string? Normalized { get; }
    public IReadOnlyList<T> Members { get; }
    public int TotalCount { get; }

    internal InstanceKeybindingViewBlock(bool isGroup, string? normalized, IReadOnlyList<T> members, int totalCount) {
        IsGroup = isGroup;
        Normalized = normalized;
        Members = members;
        TotalCount = totalCount;
    }
}

/// <summary>按原页面规则准备显示文本、搜索计数及平铺／同键卡片；不创建或更新 WPF 控件。</summary>
public static class InstanceKeybindingView {
    public static string DisplayKey(string? value) {
        if (string.IsNullOrEmpty(value) || value == "key.keyboard.unknown") return "未绑定";
        string[] parts = value!.Split(':');
        string name = parts[0] switch {
            "key.mouse.left" => "鼠标左键",
            "key.mouse.right" => "鼠标右键",
            "key.mouse.middle" => "鼠标中键",
            "key.mouse.4" => "鼠标侧键 1",
            "key.mouse.5" => "鼠标侧键 2",
            "key.keyboard.left.control" => "左 Ctrl",
            "key.keyboard.right.control" => "右 Ctrl",
            "key.keyboard.left.shift" => "左 Shift",
            "key.keyboard.right.shift" => "右 Shift",
            "key.keyboard.left.alt" => "左 Alt",
            "key.keyboard.right.alt" => "右 Alt",
            "key.keyboard.space" => "空格",
            "key.keyboard.enter" => "回车",
            "key.keyboard.backspace" => "退格",
            "key.keyboard.up" => "↑",
            "key.keyboard.down" => "↓",
            "key.keyboard.left" => "←",
            "key.keyboard.right" => "→",
            _ => parts[0].Replace("key.keyboard.keypad.", "小键盘 ").Replace("key.keyboard.", "").Replace("key.mouse.", "鼠标 ").ToUpperInvariant()
        };
        List<string> modifiers = parts.Skip(1).Where(part => part != "NONE" && part != "UNIVERSAL")
            .Select(part => part.Replace("CONTROL", "Ctrl").Replace("SHIFT", "Shift").Replace("ALT", "Alt")).ToList();
        return modifiers.Count == 0 ? name : string.Join(" + ", modifiers) + " + " + name;
    }

    /// <param name="trimmedQuery">由原页面 Trim 后传入的查询，保持原有 OrdinalIgnoreCase 搜索。</param>
    public static InstanceKeybindingViewResult<T> BuildView<T>(IReadOnlyList<T> entries, string trimmedQuery, bool conflictsOnly) where T : InstanceKeybindingData {
        List<T> matching = entries.Where(item => string.IsNullOrEmpty(trimmedQuery) ||
            (item.Name + " " + item.Owners + " " + item.Key + " " + DisplayKey(item.Normalized)).IndexOf(trimmedQuery, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        List<T> visible = matching.Where(item => !conflictsOnly || item.SharedCount > 1).ToList();
        int conflictCount = matching.Count(item => item.SharedCount > 1);
        HashSet<string?> rendered = new(StringComparer.Ordinal);
        List<InstanceKeybindingViewBlock<T>> blocks = new();
        foreach (T entry in visible) {
            if (!conflictsOnly || entry.SharedCount <= 1 || entry.Normalized == "key.keyboard.unknown") {
                blocks.Add(new InstanceKeybindingViewBlock<T>(false, entry.Normalized, new[] { entry }, 1));
                continue;
            }
            if (!rendered.Add(entry.Normalized)) continue;
            List<T> members = visible.Where(item => SameNormalized(item.Normalized, entry.Normalized)).ToList();
            int total = Math.Max(entry.SharedCount, entries.Count(item => SameNormalized(item.Normalized, entry.Normalized)));
            blocks.Add(new InstanceKeybindingViewBlock<T>(true, entry.Normalized, members, total));
        }
        return new InstanceKeybindingViewResult<T>(matching.Count, conflictCount, visible.Count, blocks);
    }

    // VB Binary 字符串比较将 Nothing 与空串视为相等；Rendered 的 Ordinal HashSet 仍区分二者。
    private static bool SameNormalized(string? first, string? second) => string.Equals(first ?? "", second ?? "", StringComparison.Ordinal);
}
