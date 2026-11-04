using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

/// <summary>
/// LF 个人游戏设置的纯转换规则；设置开关、文件事务及游戏会话由调用者管理。
/// </summary>
public static class PersonalOptionsRules {
    private static readonly HashSet<string> BasicKeys = new(
        new[] { "attack", "use", "pickItem", "forward", "back", "left", "right", "jump", "sneak", "sprint", "drop", "inventory", "chat", "playerlist", "command", "screenshot", "togglePerspective", "smoothCamera", "fullscreen", "spectatorOutlines", "advancements", "swapOffhand", "saveToolbarActivator", "loadToolbarActivator" }
            .Select(name => "key_key." + name).Concat(Enumerable.Range(1, 9).Select(index => "key_key.hotbar." + index)), StringComparer.Ordinal);
    private static readonly HashSet<string> MouseKeys = new(
        ["mouseSensitivity", "invertYMouse", "mouseWheelSensitivity", "discrete_mouse_scroll", "rawMouseInput", "touchscreen", "toggleSprint", "toggleCrouch"], StringComparer.Ordinal);
    private static readonly HashSet<string> ViewKeys = new(
        ["fov", "fovEffectScale", "screenEffectScale", "bobView"], StringComparer.Ordinal);
    private static readonly HashSet<string> AccessibilityKeys = new(
        ["showSubtitles", "autoJump", "highContrast", "darknessEffectScale", "hideLightningFlashes", "notificationDisplayTime"], StringComparer.Ordinal);
    private static readonly Dictionary<int, string> LegacyKeys = CreateLegacyKeys();

    public static string CanonicalKey(string key) => key == "key_key.swapHands" ? "key_key.swapOffhand" : key;
    public static bool VanillaBinding(string key) => BasicKeys.Contains(CanonicalKey(key));
    public static bool IsMouseKey(string key) => MouseKeys.Contains(key);
    public static bool IsViewKey(string key) => ViewKeys.Contains(key);
    public static bool IsAccessibilityKey(string key) => AccessibilityKeys.Contains(key);

    public static Dictionary<string, string> Parse(string text) {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string line in text.Replace("\r\n", "\n").Split('\n')) {
            int separator = line.IndexOf(':');
            if (separator <= 0) continue;
            string key = CanonicalKey(line.Substring(0, separator));
            values[key] = line.Substring(separator + 1);
        }
        return values;
    }

    public static string Decode(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');

    public static string? CanonicalValue(string key, string value) {
        if (value.Length > 4096 || value.IndexOfAny(['\0', '\n', '\r']) >= 0) return null;
        if (key == "fov") {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double fovNumber) || double.IsNaN(fovNumber) || double.IsInfinity(fovNumber)) return null;
            if (fovNumber >= -1 && fovNumber <= 1) fovNumber = 70 + fovNumber * 40;
            return fovNumber >= 30 && fovNumber <= 110 ? fovNumber.ToString("0.########", CultureInfo.InvariantCulture) : null;
        }
        if (!key.StartsWith("key_", StringComparison.Ordinal)) return value;
        if (!int.TryParse(value, out int number)) return value.StartsWith("key.keyboard.") || value.StartsWith("key.mouse.") ? value : null;
        if (number <= -1 && number >= -100) {
            int button = number + 100;
            return "key.mouse." + (button == 0 ? "left" : button == 1 ? "right" : button == 2 ? "middle" : (button + 1).ToString(CultureInfo.InvariantCulture));
        }
        return LegacyKeys.TryGetValue(number, out string? name) ? "key.keyboard." + name : null;
    }

    public static string? ForVersion(string key, string value, bool isModern) {
        string? converted = CanonicalValue(key, value);
        if (converted is null || isModern || !key.StartsWith("key_", StringComparison.Ordinal)) return converted;
        if (converted.StartsWith("key.mouse.")) {
            string button = converted.Substring(10);
            if (button == "left") return "-100";
            if (button == "right") return "-99";
            if (button == "middle") return "-98";
            if (int.TryParse(button, out int number) && number >= 4 && number <= 100) return (number - 101).ToString(CultureInfo.InvariantCulture);
            return null;
        }
        KeyValuePair<int, string> pair = LegacyKeys.FirstOrDefault(item => "key.keyboard." + item.Value == converted);
        return pair.Value is null ? null : pair.Key.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 保留原文本中的非同步字段及行结构；selected 按原顺序即时查询设置，不缓存类别开关。
    /// </summary>
    public static string MergeText(string text, JObject values, bool isModern, ISet<string> keys, Func<string, bool> selected, bool fovDegrees = true) {
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        bool finalNewline = text.EndsWith("\n", StringComparison.Ordinal);
        List<string> lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        Dictionary<string, string> present = Parse(text);
        HashSet<string> applied = new(StringComparer.Ordinal);
        Dictionary<string, string> replacements = new(StringComparer.Ordinal);
        foreach (JProperty pair in values.Properties()) {
            string key = CanonicalKey(pair.Name);
            if (!keys.Contains(key) || !selected(key)) continue;
            // 版本专用设置只有在目标已存在该键时才同步；不注入 Mod 或未知配置。
            if (!present.ContainsKey(key) && !BasicKeys.Contains(key) && !new[] { "mouseSensitivity", "invertYMouse", "fov", "showSubtitles" }.Contains(key)) continue;
            string? converted = ForVersion(key, pair.Value.ToString(), isModern);
            if (key == "fov" && converted is not null) {
                bool usesDegrees = fovDegrees;
                if (present.TryGetValue(key, out string? existing) && TryLegacyExistingFov(existing, out double numeric)) usesDegrees = Math.Abs(numeric) > 1;
                double degrees = double.Parse(converted, CultureInfo.InvariantCulture);
                converted = usesDegrees ? Math.Round(degrees).ToString(CultureInfo.InvariantCulture) : ((degrees - 70) / 40).ToString("0.########", CultureInfo.InvariantCulture);
            }
            if (converted is not null) replacements[key] = converted;
        }
        for (int index = 0; index < lines.Count; index++) {
            int separator = lines[index].IndexOf(':');
            if (separator <= 0) continue;
            string key = CanonicalKey(lines[index].Substring(0, separator));
            if (!replacements.TryGetValue(key, out string? value)) continue;
            lines[index] = lines[index].Substring(0, separator + 1) + value;
            applied.Add(key);
        }
        foreach (KeyValuePair<string, string> pair in replacements.Where(item => !applied.Contains(item.Key))) {
            if (lines.Count > 0 && lines.Last() == "") lines.RemoveAt(lines.Count - 1);
            lines.Add((pair.Key == "key_key.swapOffhand" && !isModern ? "key_key.swapHands" : pair.Key) + ":" + pair.Value);
        }
        string result = string.Join(newline, lines);
        return result + (finalNewline && !result.EndsWith(newline, StringComparison.Ordinal) ? newline : "");
    }

    private static bool TryLegacyExistingFov(string? value, out double numeric) {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out numeric)) return false;
        if (!double.IsInfinity(numeric) && !double.IsNaN(numeric)) return true;
        // Framework 拒绝溢出数值，且特殊值字面量区分大小写；现代运行时放宽了这些输入。
        // https://github.com/microsoft/referencesource/blob/ec9fa9ae770d522a5b5f0607898044b7478574a3/mscorlib/system/double.cs#L274
        NumberFormatInfo format = CultureInfo.InvariantCulture.NumberFormat;
        string? trimmed = value?.Trim();
        return string.Equals(trimmed, format.PositiveInfinitySymbol, StringComparison.Ordinal) ||
               string.Equals(trimmed, format.NegativeInfinitySymbol, StringComparison.Ordinal) ||
               string.Equals(trimmed, format.NaNSymbol, StringComparison.Ordinal);
    }

    public static string? NormalizeBinding(string? value) {
        if (value is null) return null;
        string[] parts = value.Split(':');
        string? main = CanonicalValue("key_key.forward", parts[0]);
        if (main is null) return null;
        return main + (parts.Length > 1 ? ":" + string.Join(":", parts.Skip(1)).ToUpperInvariant() : "");
    }

    public static string? BindingForVersion(string? value, bool isModern) {
        if (value is null) return null;
        string[] parts = value.Split(':');
        string? main = ForVersion("key_key.forward", parts[0], isModern);
        return main is null ? null : main + (parts.Length > 1 ? ":" + string.Join(":", parts.Skip(1)) : "");
    }

    public static Dictionary<int, string> CreateLegacyKeys() {
        Dictionary<int, string> values = new() {
            {0, "unknown"}, {1, "escape"}, {14, "backspace"}, {15, "tab"}, {28, "enter"}, {29, "left.control"}, {42, "left.shift"}, {54, "right.shift"}, {56, "left.alt"}, {57, "space"}, {58, "caps.lock"}, {69, "num.lock"}, {70, "scroll.lock"}, {87, "f11"}, {88, "f12"}, {156, "keypad.enter"}, {157, "right.control"}, {181, "keypad.divide"}, {183, "print.screen"}, {184, "right.alt"}, {197, "pause"}, {199, "home"}, {200, "up"}, {201, "page.up"}, {203, "left"}, {205, "right"}, {207, "end"}, {208, "down"}, {209, "page.down"}, {210, "insert"}, {211, "delete"}, {219, "left.win"}, {220, "right.win"}, {221, "menu"}, {12, "minus"}, {13, "equal"}, {26, "left.bracket"}, {27, "right.bracket"}, {43, "backslash"}, {39, "semicolon"}, {40, "apostrophe"}, {41, "grave.accent"}, {51, "comma"}, {52, "period"}, {53, "slash"}, {55, "keypad.multiply"}, {74, "keypad.subtract"}, {78, "keypad.add"}, {83, "keypad.decimal"}
        };
        for (int index = 0; index <= 9; index++) values[2 + index] = index == 9 ? "0" : (index + 1).ToString();
        foreach ((int start, string row) in new[] { (16, "qwertyuiop"), (30, "asdfghjkl"), (44, "zxcvbnm") }) {
            for (int index = 0; index < row.Length; index++) values[start + index] = row[index].ToString();
        }
        for (int index = 1; index <= 10; index++) values[58 + index] = "f" + index;
        foreach (KeyValuePair<int, string> pair in new Dictionary<int, string> { {71, "7"}, {72, "8"}, {73, "9"}, {75, "4"}, {76, "5"}, {77, "6"}, {79, "1"}, {80, "2"}, {81, "3"}, {82, "0"} }) values[pair.Key] = "keypad." + pair.Value;
        return values;
    }
}
