using System.Globalization;
using PCL.Living.Features;

internal static class InstanceKeybindingViewTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("keybinding view unbound text uses exact whole-value checks", UnboundText);
        yield return ("keybinding view named mouse, modifier and navigation keys keep captions", NamedKeys);
        yield return ("keybinding view fallback retains replacement order and invariant uppercase", FallbackKeys);
        yield return ("keybinding view modifiers retain exact filters and substring replacements", ModifierText);
        yield return ("keybinding view empty result preserves row and block counts", EmptyView);
        yield return ("keybinding view all filter stays flat in source order", AllFlat);
        yield return ("keybinding view search uses names, owners, key and displayed binding", SearchFields);
        yield return ("keybinding view search remains ordinal ignore-case under Turkish culture", SearchCulture);
        yield return ("keybinding view raw saved value is not an additional search field", SearchDoesNotUseRawValue);
        yield return ("keybinding view query trimming remains caller responsibility", CallerTrim);
        yield return ("keybinding view counts rows independently of rendered groups", RowCounts);
        yield return ("keybinding view search singleton keeps group and global total", SearchSingleton);
        yield return ("keybinding view group total uses first shared count rather than all-member max", FirstSharedCount);
        yield return ("keybinding view case-sensitive blocks keep case-insensitive shared counts", CaseBlocks);
        yield return ("keybinding view null-empty membership differs from rendered-key equality", NullEmptyGroups);
        yield return ("keybinding view exact unknown stays flat while modified unknown groups", UnknownBlocks);
        yield return ("keybinding view hidden nonconflict rows still contribute to global total", HiddenRows);
        yield return ("keybinding view groups and members follow first visible source order", InterleavedGroups);
        yield return ("keybinding view derived generic members retain original object identity", GenericIdentity);
        yield return ("keybinding view nullable search fields use original empty-string concatenation", NullableFields);
        yield return ("keybinding view does not rewrite input fields or row order", InputsUnchanged);
    }

    private static void UnboundText() {
        foreach (string? value in new[] { null, "", "key.keyboard.unknown" }) Equal("未绑定", InstanceKeybindingView.DisplayKey(value));
        Equal("Alt + UNKNOWN", InstanceKeybindingView.DisplayKey("key.keyboard.unknown:ALT"));
        Equal("UNKNOWN", InstanceKeybindingView.DisplayKey("key.keyboard.unknown:NONE:UNIVERSAL"));
        Equal("KEY.KEYBOARD.UNKNOWN", InstanceKeybindingView.DisplayKey("KEY.KEYBOARD.UNKNOWN"));
        Equal("Alt + ", InstanceKeybindingView.DisplayKey(":ALT"));
    }

    private static void NamedKeys() {
        foreach ((string key, string caption) in new[] {
            ("key.mouse.left", "鼠标左键"), ("key.mouse.right", "鼠标右键"), ("key.mouse.middle", "鼠标中键"), ("key.mouse.4", "鼠标侧键 1"), ("key.mouse.5", "鼠标侧键 2"),
            ("key.keyboard.left.control", "左 Ctrl"), ("key.keyboard.right.control", "右 Ctrl"), ("key.keyboard.left.shift", "左 Shift"), ("key.keyboard.right.shift", "右 Shift"),
            ("key.keyboard.left.alt", "左 Alt"), ("key.keyboard.right.alt", "右 Alt"), ("key.keyboard.space", "空格"), ("key.keyboard.enter", "回车"), ("key.keyboard.backspace", "退格"),
            ("key.keyboard.up", "↑"), ("key.keyboard.down", "↓"), ("key.keyboard.left", "←"), ("key.keyboard.right", "→")
        }) Equal(caption, InstanceKeybindingView.DisplayKey(key));
    }

    private static void FallbackKeys() {
        foreach ((string key, string caption) in new[] { ("key.keyboard.keypad.add", "小键盘 ADD"), ("key.keyboard.f24", "F24"), ("key.mouse.8", "鼠标 8"), ("prefix.key.keyboard.w", "PREFIX.W"), ("key.keyboard.keypad.key.keyboard.w", "小键盘 W"), ("w", "W") }) Equal(caption, InstanceKeybindingView.DisplayKey(key));
        CultureInfo previous = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Equal("I", InstanceKeybindingView.DisplayKey("key.keyboard.i"));
        } finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void ModifierText() {
        Equal("Ctrl + Shift + Alt + W", InstanceKeybindingView.DisplayKey("key.keyboard.w:CONTROL:SHIFT:ALT"));
        Equal("W", InstanceKeybindingView.DisplayKey("key.keyboard.w:NONE:UNIVERSAL"));
        Equal("none + universal + W", InstanceKeybindingView.DisplayKey("key.keyboard.w:none:universal"));
        Equal(" + W", InstanceKeybindingView.DisplayKey("key.keyboard.w:"));
        Equal(" + Alt + W", InstanceKeybindingView.DisplayKey("key.keyboard.w::ALT"));
        Equal("LCtrl_Shift + control + W", InstanceKeybindingView.DisplayKey("key.keyboard.w:LCONTROL_SHIFT:control"));
    }

    private static void EmptyView() {
        foreach (bool conflictsOnly in new[] { false, true }) {
            InstanceKeybindingViewResult<DerivedBinding> view = InstanceKeybindingView.BuildView(Array.Empty<DerivedBinding>(), "", conflictsOnly);
            Counts(view, 0, 0, 0, 0);
        }
    }

    private static void AllFlat() {
        DerivedBinding first = Row("first", "First", "key.keyboard.r", 3);
        DerivedBinding second = Row("second", "Second", "key.keyboard.r", 3);
        DerivedBinding single = Row("single", "Single", "key.keyboard.s", 1);
        DerivedBinding[] entries = [first, second, single];
        InstanceKeybindingViewResult<DerivedBinding> view = InstanceKeybindingView.BuildView(entries, "", false);
        Counts(view, 3, 2, 3, 3);
        for (int index = 0; index < entries.Length; index++) {
            Equal(false, view.Blocks[index].IsGroup);
            Equal(1, view.Blocks[index].Members.Count);
            Same(entries[index], view.Blocks[index].Members[0]);
        }
    }

    private static void SearchFields() {
        DerivedBinding named = Row("named", "中文跳跃", "key.keyboard.w", 1);
        DerivedBinding owned = Row("owned", "Other", "key.keyboard.w", 1);
        owned.Owners = "ExampleMod（owned.jar）";
        DerivedBinding key = Row("fixture.special", "Other", "key.keyboard.w", 1);
        DerivedBinding display = Row("display", "Other", "key.mouse.left", 1);
        DerivedBinding[] entries = [named, owned, key, display];
        foreach ((string query, DerivedBinding expected) in new[] { ("跳跃", named), ("examplemod", owned), ("owned.jar", owned), ("fixture.special", key), ("鼠标左键", display) }) {
            InstanceKeybindingViewResult<DerivedBinding> view = InstanceKeybindingView.BuildView(entries, query, false);
            Counts(view, 1, 0, 1, 1);
            Same(expected, view.Blocks[0].Members[0]);
        }
    }

    private static void SearchCulture() {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            DerivedBinding row = Row("key", "Istanbul", "key.keyboard.w", 1);
            Counts(InstanceKeybindingView.BuildView(new[] { row }, "istanbul", false), 1, 0, 1, 1);
            Counts(InstanceKeybindingView.BuildView(new[] { row }, "ıstanbul", false), 0, 0, 0, 0);
        } finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void SearchDoesNotUseRawValue() {
        DerivedBinding row = Row("fixture.forward", "Forward", "key.keyboard.w", 1);
        row.Value = "19";
        Counts(InstanceKeybindingView.BuildView(new[] { row }, "19", false), 0, 0, 0, 0);
    }

    private static void CallerTrim() {
        DerivedBinding row = Row("key", "needle", null, 1);
        Counts(InstanceKeybindingView.BuildView(new[] { row }, " needle ", false), 0, 0, 0, 0);
        Counts(InstanceKeybindingView.BuildView(new[] { row }, "needle", false), 1, 0, 1, 1);
        Counts(InstanceKeybindingView.BuildView(new[] { row }, null!, false), 1, 0, 1, 1);
    }

    private static void RowCounts() {
        DerivedBinding[] entries = [Row("a", "A", "key.keyboard.c", 3), Row("b", "B", "key.keyboard.c", 3), Row("c", "C", "key.keyboard.c", 3), Row("d", "D", "key.keyboard.v", 2), Row("e", "E", "key.keyboard.v", 2), Row("single", "Single", "key.keyboard.e", 1)];
        InstanceKeybindingViewResult<DerivedBinding> view = InstanceKeybindingView.BuildView(entries, "", true);
        Counts(view, 6, 5, 5, 2);
        Equal(3, view.Blocks[0].Members.Count);
        Equal(3, view.Blocks[0].TotalCount);
        Equal(2, view.Blocks[1].Members.Count);
        Equal(2, view.Blocks[1].TotalCount);
    }

    private static void SearchSingleton() {
        DerivedBinding first = Row("first", "保存快捷栏", "key.keyboard.c", 3);
        DerivedBinding target = Row("target", "复制位置", "key.keyboard.c", 3);
        DerivedBinding third = Row("third", "调试崩溃", "key.keyboard.c", 3);
        InstanceKeybindingViewResult<DerivedBinding> view = InstanceKeybindingView.BuildView(new[] { first, target, third }, "复制位置", true);
        Counts(view, 1, 1, 1, 1);
        Equal(true, view.Blocks[0].IsGroup);
        Equal(1, view.Blocks[0].Members.Count);
        Equal(3, view.Blocks[0].TotalCount);
        Same(target, view.Blocks[0].Members[0]);
    }

    private static void FirstSharedCount() {
        DerivedBinding first = Row("first", "First", "key.keyboard.r", 2);
        DerivedBinding later = Row("later", "Later", "key.keyboard.r", 9);
        InstanceKeybindingViewResult<DerivedBinding> view = InstanceKeybindingView.BuildView(new[] { first, later }, "", true);
        Counts(view, 2, 2, 2, 1);
        Equal(2, view.Blocks[0].TotalCount);
        view = InstanceKeybindingView.BuildView(new[] { later, first }, "", true);
        Equal(9, view.Blocks[0].TotalCount);
    }

    private static void CaseBlocks() {
        DerivedBinding upper = Row("upper", "Upper", "key.keyboard.R", 2);
        DerivedBinding lower = Row("lower", "Lower", "key.keyboard.r", 2);
        InstanceKeybindingViewResult<DerivedBinding> view = InstanceKeybindingView.BuildView(new[] { upper, lower }, "", true);
        Counts(view, 2, 2, 2, 2);
        Equal("key.keyboard.R", view.Blocks[0].Normalized);
        Equal("key.keyboard.r", view.Blocks[1].Normalized);
        Equal(1, view.Blocks[0].Members.Count);
        Equal(1, view.Blocks[1].Members.Count);
        Equal(2, view.Blocks[0].TotalCount);
        Equal(2, view.Blocks[1].TotalCount);
        Same(upper, view.Blocks[0].Members[0]);
        Same(lower, view.Blocks[1].Members[0]);
    }

    private static void NullEmptyGroups() {
        DerivedBinding nothing = Row("nothing", "Nothing", null, 3);
        DerivedBinding empty = Row("empty", "Empty", "", 2);
        DerivedBinding hidden = Row("hidden", "Hidden", null, 1);
        InstanceKeybindingViewResult<DerivedBinding> view = InstanceKeybindingView.BuildView(new[] { nothing, empty, hidden }, "", true);
        Counts(view, 3, 2, 2, 2);
        Equal<string?>(null, view.Blocks[0].Normalized);
        Equal("", view.Blocks[1].Normalized);
        foreach (InstanceKeybindingViewBlock<DerivedBinding> block in view.Blocks) {
            Equal(true, block.IsGroup);
            Equal(2, block.Members.Count);
            Same(nothing, block.Members[0]);
            Same(empty, block.Members[1]);
            Equal(3, block.TotalCount);
        }
    }

    private static void UnknownBlocks() {
        DerivedBinding exact = Row("exact", "Exact", "key.keyboard.unknown", 8);
        DerivedBinding modified = Row("modified", "Modified", "key.keyboard.unknown:ALT", 2);
        DerivedBinding same = Row("same", "Same", "key.keyboard.unknown:ALT", 2);
        DerivedBinding single = Row("single", "Single", "key.keyboard.r", 1);
        InstanceKeybindingViewResult<DerivedBinding> view = InstanceKeybindingView.BuildView(new[] { exact, modified, same, single }, "", true);
        Counts(view, 4, 3, 3, 2);
        Equal(false, view.Blocks[0].IsGroup);
        Same(exact, view.Blocks[0].Members[0]);
        Equal(true, view.Blocks[1].IsGroup);
        Equal(2, view.Blocks[1].Members.Count);
        Equal("Alt + UNKNOWN", InstanceKeybindingView.DisplayKey(view.Blocks[1].Normalized));
    }

    private static void HiddenRows() {
        DerivedBinding visible = Row("visible", "Visible", "key.keyboard.r", 2);
        DerivedBinding hidden = Row("hidden", "Hidden", "key.keyboard.r", 1);
        DerivedBinding hidden2 = Row("hidden2", "Hidden2", "key.keyboard.r", 0);
        InstanceKeybindingViewResult<DerivedBinding> view = InstanceKeybindingView.BuildView(new[] { visible, hidden, hidden2 }, "Visible", true);
        Counts(view, 1, 1, 1, 1);
        Equal(1, view.Blocks[0].Members.Count);
        Equal(3, view.Blocks[0].TotalCount);
    }

    private static void InterleavedGroups() {
        DerivedBinding a = Row("a", "A", "key.keyboard.z", 2);
        DerivedBinding b = Row("b", "B", "key.keyboard.a", 2);
        DerivedBinding c = Row("c", "C", "key.keyboard.z", 2);
        DerivedBinding d = Row("d", "D", "key.keyboard.a", 2);
        InstanceKeybindingViewResult<DerivedBinding> view = InstanceKeybindingView.BuildView(new[] { a, b, c, d }, "", true);
        Equal("key.keyboard.z", view.Blocks[0].Normalized);
        Equal("key.keyboard.a", view.Blocks[1].Normalized);
        Same(a, view.Blocks[0].Members[0]);
        Same(c, view.Blocks[0].Members[1]);
        Same(b, view.Blocks[1].Members[0]);
        Same(d, view.Blocks[1].Members[1]);
    }

    private static void GenericIdentity() {
        DerivedBinding a = Row("a", "A", "key.keyboard.r", 2);
        DerivedBinding b = Row("b", "B", "key.keyboard.r", 2);
        InstanceKeybindingViewResult<DerivedBinding> view = InstanceKeybindingView.BuildView<DerivedBinding>(new List<DerivedBinding> { a, b }, "", true);
        DerivedBinding member = view.Blocks[0].Members[0];
        Same(a, member);
        a.Marker = "mutated after planning";
        Equal("mutated after planning", member.Marker);
        Same(b, view.Blocks[0].Members[1]);
    }

    private static void NullableFields() {
        DerivedBinding row = new() { Key = null, Name = null, Owners = null!, Normalized = null, SharedCount = 1 };
        InstanceKeybindingViewResult<DerivedBinding> view = InstanceKeybindingView.BuildView(new[] { row }, "未绑定", false);
        Counts(view, 1, 0, 1, 1);
        Same(row, view.Blocks[0].Members[0]);
        Counts(InstanceKeybindingView.BuildView(new[] { row }, "null", false), 0, 0, 0, 0);
    }

    private static void InputsUnchanged() {
        DerivedBinding a = Row("a", "A", "key.keyboard.r", 2);
        DerivedBinding b = Row("b", "B", "key.keyboard.r", 2);
        List<DerivedBinding> rows = [b, a];
        a.Saved = false;
        a.Value = "19:alt";
        InstanceKeybindingView.BuildView(rows, "A", true);
        Same(b, rows[0]);
        Same(a, rows[1]);
        Equal("A", a.Name);
        Equal("Minecraft", a.Owners);
        Equal("19:alt", a.Value);
        Equal("key.keyboard.r", a.Normalized);
        Equal(2, a.SharedCount);
        Equal(false, a.Saved);
    }

    private static DerivedBinding Row(string key, string name, string? normalized, int sharedCount) => new() { Key = key, Name = name, Owners = "Minecraft", Value = normalized, Normalized = normalized, SharedCount = sharedCount };
    private static void Counts<T>(InstanceKeybindingViewResult<T> view, int matching, int conflicts, int visible, int blocks) where T : InstanceKeybindingData {
        Equal(matching, view.MatchingCount);
        Equal(conflicts, view.ConflictCount);
        Equal(visible, view.VisibleCount);
        Equal(blocks, view.Blocks.Count);
    }
    private static void Same(object expected, object actual) => Equal(true, ReferenceEquals(expected, actual));
    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
    private sealed class DerivedBinding : InstanceKeybindingData { internal string Marker = "derived"; }
}
