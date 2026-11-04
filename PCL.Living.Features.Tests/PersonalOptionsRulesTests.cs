using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;
using PCL.Living.Features;

internal static class PersonalOptionsRulesTests {
    private const string BindingKey = "key_key.forward";

    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("personal options whitelists keep exact categories and aliases", Whitelists);
        yield return ("personal options parsing preserves raw keys and colon tails", ParseRaw);
        yield return ("personal options duplicate and aliased keys use last value", ParseDuplicates);
        yield return ("personal options decode strips leading BOMs but keeps interior BOM", DecodeBom);
        yield return ("personal options decode rejects invalid UTF-8 and UTF-16 bytes", DecodeInvalid);
        yield return ("personal option values keep limit and newline restrictions", ValueLimits);
        yield return ("personal options FOV canonicalization accepts normalized and degree forms", FovValues);
        yield return ("personal options FOV rejects nonfinite and out-of-range values", InvalidFov);
        yield return ("personal options keyboard mapping preserves old rows and special keys", KeyboardValues);
        yield return ("personal options mouse mapping covers legacy button boundaries", MouseValues);
        yield return ("personal options key conversion uses current-culture integer signs", IntegerCulture);
        yield return ("personal options invalid keys and unknown legacy codes stay rejected", InvalidBindings);
        yield return ("personal options modifier normalization and version suffixes stay distinct", ModifierBindings);
        yield return ("personal options legacy key factory does not expose cached rules", LegacyFactory);
        yield return ("personal options merge preserves author and Mod fields", MergeExistingFixture);
        yield return ("personal options merge appends only permitted missing defaults", MergeMissingKeys);
        yield return ("personal options merge updates duplicates while retaining old key spelling", MergeDuplicates);
        yield return ("personal options merge writes legacy swap alias only when adding", MergeSwapAlias);
        yield return ("personal options merge keeps caller key-set alias semantics", CallerKeys);
        yield return ("personal options merge evaluates selection live after key membership", SelectionOrder);
        yield return ("personal options merge propagates selector failures without mutating JSON", SelectionFailure);
        yield return ("personal options merge rejects invalid replacements without changing target", InvalidReplacements);
        yield return ("personal options merge keeps target FOV representation and fallback", MergeFovForms);
        yield return ("personal options FOV inference keeps Framework special and overflow semantics", MergeLegacyFovParsing);
        yield return ("personal options merge rounds degree FOV to even", MergeFovRounding);
        yield return ("personal options merge preserves line content and trailing newline state", MergeLines);
        yield return ("personal options merge retains existing mixed-newline normalization", MixedNewlines);
        yield return ("personal options merge honors caller key-set comparer and selection", CallerComparer);
    }

    private static void Whitelists() {
        string[] names = ["attack", "use", "pickItem", "forward", "back", "left", "right", "jump", "sneak", "sprint", "drop", "inventory", "chat", "playerlist", "command", "screenshot", "togglePerspective", "smoothCamera", "fullscreen", "spectatorOutlines", "advancements", "swapOffhand", "saveToolbarActivator", "loadToolbarActivator"];
        foreach (string name in names) Equal(true, PersonalOptionsRules.VanillaBinding("key_key." + name));
        for (int slot = 1; slot <= 9; slot++) Equal(true, PersonalOptionsRules.VanillaBinding("key_key.hotbar." + slot));
        Equal(true, PersonalOptionsRules.VanillaBinding("key_key.swapHands"));
        Equal("key_key.swapOffhand", PersonalOptionsRules.CanonicalKey("key_key.swapHands"));
        foreach (string key in new[] { "key_key.hotbar.0", "key_key.hotbar.10", "key_mod.magic", "Key_key.forward", "key_key.Forward", "key_key.swapHands ", "" }) Equal(false, PersonalOptionsRules.VanillaBinding(key));
        foreach (string key in new[] { "mouseSensitivity", "invertYMouse", "mouseWheelSensitivity", "discrete_mouse_scroll", "rawMouseInput", "touchscreen", "toggleSprint", "toggleCrouch" }) Equal(true, PersonalOptionsRules.IsMouseKey(key));
        foreach (string key in new[] { "fov", "fovEffectScale", "screenEffectScale", "bobView" }) Equal(true, PersonalOptionsRules.IsViewKey(key));
        foreach (string key in new[] { "showSubtitles", "autoJump", "highContrast", "darknessEffectScale", "hideLightningFlashes", "notificationDisplayTime" }) Equal(true, PersonalOptionsRules.IsAccessibilityKey(key));
        foreach (string key in new[] { "MouseSensitivity", "renderDistance", "soundCategory_master", "resourcePacks", "key_mod.magic" }) {
            Equal(false, PersonalOptionsRules.IsMouseKey(key));
            Equal(false, PersonalOptionsRules.IsViewKey(key));
            Equal(false, PersonalOptionsRules.IsAccessibilityKey(key));
        }
    }

    private static void ParseRaw() {
        Dictionary<string, string> values = PersonalOptionsRules.Parse("no separator\r\n:empty key\n key :  value:tail \n#comment:unchanged\r\n世界:名字:后缀\nkey_key.forward:key.keyboard.w:ALT\n");
        Equal(4, values.Count);
        Equal("  value:tail ", values[" key "]);
        Equal("unchanged", values["#comment"]);
        Equal("名字:后缀", values["世界"]);
        Equal("key.keyboard.w:ALT", values[BindingKey]);
        Equal(false, values.ContainsKey("key"));
        Equal(false, values.ContainsKey("KEY_KEY.FORWARD"));
        Equal(StringComparer.Ordinal, values.Comparer);
        Equal("raw\rvalue", PersonalOptionsRules.Parse("setting:raw\rvalue")["setting"]);
    }

    private static void ParseDuplicates() {
        Dictionary<string, string> values = PersonalOptionsRules.Parse("key_key.swapOffhand:key.keyboard.f\nkey_key.swapHands:33\nkey_key.forward:17\nkey_key.forward:19\nKey_key.forward:18");
        Equal(3, values.Count);
        Equal("33", values["key_key.swapOffhand"]);
        Equal(false, values.ContainsKey("key_key.swapHands"));
        Equal("19", values[BindingKey]);
        Equal("18", values["Key_key.forward"]);
        Equal("", PersonalOptionsRules.Parse("mouseSensitivity:")["mouseSensitivity"]);
    }

    private static void DecodeBom() {
        Equal("", PersonalOptionsRules.Decode([]));
        Equal("世界:中文 Ω\n", PersonalOptionsRules.Decode(Encoding.UTF8.GetBytes("世界:中文 Ω\n")));
        Equal("fov:70", PersonalOptionsRules.Decode([239, 187, 191, 239, 187, 191, .. Encoding.UTF8.GetBytes("fov:70")]));
        Equal("name:x\uFEFFy", PersonalOptionsRules.Decode(Encoding.UTF8.GetBytes("\uFEFFname:x\uFEFFy")));
        Equal(" \uFEFFname:x", PersonalOptionsRules.Decode(Encoding.UTF8.GetBytes(" \uFEFFname:x")));
        Equal("\0", PersonalOptionsRules.Decode([0]));
    }

    private static void DecodeInvalid() {
        foreach (byte[] bytes in new byte[][] { [255], [195], [192, 175], [237, 160, 128], [255, 254, 102, 0] }) Throws<DecoderFallbackException>(() => PersonalOptionsRules.Decode(bytes));
        Throws<ArgumentNullException>(() => PersonalOptionsRules.Decode(null!));
    }

    private static void ValueLimits() {
        string longest = new('x', 4096);
        Equal(longest, PersonalOptionsRules.CanonicalValue("mouseSensitivity", longest));
        Equal<string?>(null, PersonalOptionsRules.CanonicalValue("mouseSensitivity", longest + "x"));
        foreach (string value in new[] { "a\0b", "a\nb", "a\rb" }) Equal<string?>(null, PersonalOptionsRules.CanonicalValue("mouseSensitivity", value));
        Equal("", PersonalOptionsRules.CanonicalValue("mouseSensitivity", ""));
        Equal("not a number:tail", PersonalOptionsRules.CanonicalValue("mouseSensitivity", "not a number:tail"));
        Equal("True", PersonalOptionsRules.CanonicalValue("showSubtitles", "True"));
        Equal("raw", PersonalOptionsRules.CanonicalValue("FOV", "raw"));
    }

    private static void FovValues() {
        foreach ((string input, string expected) in new[] { ("-1", "30"), ("0", "70"), ("0.3", "82"), ("0.5", "90"), ("1", "110"), ("30", "30"), ("110", "110"), (" 9e1 ", "90"), ("70.123456789", "70.12345679"), ("-0", "70") }) Equal(expected, PersonalOptionsRules.CanonicalValue("fov", input));
    }

    private static void InvalidFov() {
        foreach (string value in new[] { "", "NaN", "Infinity", "-Infinity", "1e5000", "-1.01", "1.01", "29.999", "110.001", "70,5", "9:0", "bad" }) Equal<string?>(null, PersonalOptionsRules.CanonicalValue("fov", value));
    }

    private static void KeyboardValues() {
        foreach ((string code, string name) in new[] { ("0", "unknown"), ("1", "escape"), ("2", "1"), ("11", "0"), ("16", "q"), ("25", "p"), ("30", "a"), ("38", "l"), ("44", "z"), ("50", "m"), ("57", "space"), ("58", "caps.lock"), ("59", "f1"), ("68", "f10"), ("87", "f11"), ("88", "f12"), ("156", "keypad.enter"), ("157", "right.control"), ("183", "print.screen"), ("197", "pause"), ("219", "left.win"), ("220", "right.win"), ("221", "menu"), ("71", "keypad.7"), ("82", "keypad.0"), ("83", "keypad.decimal") }) {
            Equal("key.keyboard." + name, PersonalOptionsRules.CanonicalValue(BindingKey, code));
            Equal(code, PersonalOptionsRules.ForVersion(BindingKey, "key.keyboard." + name, false));
        }
        Equal("key.keyboard.r", PersonalOptionsRules.CanonicalValue(BindingKey, "  +0019  "));
        Equal("key.keyboard.r", PersonalOptionsRules.ForVersion(BindingKey, "19", true));
        Equal("19", PersonalOptionsRules.ForVersion(BindingKey, "19", false));
    }

    private static void MouseValues() {
        foreach ((string code, string name) in new[] { ("-100", "left"), ("-99", "right"), ("-98", "middle"), ("-97", "4"), ("-1", "100") }) {
            Equal("key.mouse." + name, PersonalOptionsRules.CanonicalValue(BindingKey, code));
            Equal(code, PersonalOptionsRules.ForVersion(BindingKey, "key.mouse." + name, false));
        }
        Equal("key.mouse.42", PersonalOptionsRules.CanonicalValue(BindingKey, "-59"));
        foreach (string suffix in new[] { "0", "1", "2", "3", "101", "LEFT", "unknown" }) {
            Equal("key.mouse." + suffix, PersonalOptionsRules.ForVersion(BindingKey, "key.mouse." + suffix, true));
            Equal<string?>(null, PersonalOptionsRules.ForVersion(BindingKey, "key.mouse." + suffix, false));
        }
    }

    private static void IntegerCulture() {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo changed = (CultureInfo) CultureInfo.GetCultureInfo("fr-FR").Clone();
        changed.NumberFormat.NegativeSign = "~";
        changed.NumberFormat.PositiveSign = "^";
        try {
            CultureInfo.CurrentCulture = changed;
            Equal("key.keyboard.w", PersonalOptionsRules.CanonicalValue(BindingKey, "^17"));
            Equal("key.mouse.left", PersonalOptionsRules.CanonicalValue(BindingKey, "~100"));
            Equal("-100", PersonalOptionsRules.ForVersion(BindingKey, "key.mouse.left", false));
            Equal("70.5", PersonalOptionsRules.CanonicalValue("fov", "70.5"));
            Equal<string?>(null, PersonalOptionsRules.CanonicalValue("fov", "70,5"));
        } finally {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static void InvalidBindings() {
        foreach (string value in new[] { "", "r", "999", "2147483648", "-101", "Key.keyboard.r", " key.keyboard.r", "19:ALT", "key.keyboard.r\n" }) Equal<string?>(null, PersonalOptionsRules.CanonicalValue(BindingKey, value));
        Equal("key.keyboard.unrecognized", PersonalOptionsRules.ForVersion(BindingKey, "key.keyboard.unrecognized", true));
        Equal<string?>(null, PersonalOptionsRules.ForVersion(BindingKey, "key.keyboard.unrecognized", false));
        Equal("0", PersonalOptionsRules.ForVersion(BindingKey, "key.keyboard.unknown", false));
        Equal<string?>(null, PersonalOptionsRules.NormalizeBinding(null));
        Equal<string?>(null, PersonalOptionsRules.BindingForVersion(null, true));
    }

    private static void ModifierBindings() {
        Equal("key.keyboard.r:ALT:SHIFT", PersonalOptionsRules.NormalizeBinding("19:alt:Shift"));
        Equal("key.mouse.left:CTRL", PersonalOptionsRules.NormalizeBinding("-100:ctrl"));
        Equal("key.keyboard.r:", PersonalOptionsRules.NormalizeBinding("key.keyboard.r:"));
        Equal("19:alt:Shift", PersonalOptionsRules.BindingForVersion("key.keyboard.r:alt:Shift", false));
        Equal("key.keyboard.r:alt:Shift", PersonalOptionsRules.BindingForVersion("19:alt:Shift", true));
        Equal<string?>(null, PersonalOptionsRules.BindingForVersion("key.keyboard.unrecognized:ALT", false));
        Equal<string?>(null, PersonalOptionsRules.NormalizeBinding(":"));
    }

    private static void LegacyFactory() {
        Dictionary<int, string> first = PersonalOptionsRules.CreateLegacyKeys();
        Dictionary<int, string> second = PersonalOptionsRules.CreateLegacyKeys();
        Equal(false, ReferenceEquals(first, second));
        first[19] = "modified";
        first.Clear();
        Equal("r", second[19]);
        Equal("key.keyboard.r", PersonalOptionsRules.CanonicalValue(BindingKey, "19"));
    }

    private static void MergeExistingFixture() {
        const string text = "mouseSensitivity:0.2\r\nfov:0.0\r\nkey_key.forward:key.keyboard.w\r\nshowSubtitles:false\r\nrenderDistance:32\r\nresourcePacks:[\"author-target.zip\"]\r\nkey_mod.magic:key.keyboard.v\r\n";
        JObject values = JObject.Parse("{\"mouseSensitivity\":\"0.8\",\"fov\":\"82\",\"key_key.forward\":\"key.keyboard.r\",\"showSubtitles\":\"true\",\"renderDistance\":\"9\",\"key_mod.magic\":\"key.keyboard.x\"}");
        string expected = text.Replace("mouseSensitivity:0.2", "mouseSensitivity:0.8").Replace("fov:0.0", "fov:0.3").Replace("key_key.forward:key.keyboard.w", "key_key.forward:key.keyboard.r").Replace("showSubtitles:false", "showSubtitles:true");
        Equal(expected, Merge(text, values));
        Equal("key.keyboard.r", (string?) values[BindingKey]);
    }

    private static void MergeMissingKeys() {
        JObject values = JObject.Parse("{\"mouseSensitivity\":\"0.8\",\"invertYMouse\":\"true\",\"fov\":\"90\",\"showSubtitles\":\"true\",\"key_key.forward\":\"key.keyboard.r\",\"rawMouseInput\":\"false\",\"autoJump\":\"true\",\"screenEffectScale\":\"0.5\",\"soundCategory_master\":\"0.1\",\"key_mod.magic\":\"key.keyboard.x\",\"renderDistance\":\"9\"}");
        const string expected = "lang:zh_cn\nmouseSensitivity:0.8\ninvertYMouse:true\nfov:90\nshowSubtitles:true\nkey_key.forward:key.keyboard.r\n";
        Equal(expected, Merge("lang:zh_cn\n", values, selector: _ => true));
        Equal("rawMouseInput:false\nautoJump:true\nscreenEffectScale:0.5\nsoundCategory_master:0.1", Merge("rawMouseInput:true\nautoJump:false\nscreenEffectScale:1\nsoundCategory_master:1", values, selector: key => key is "rawMouseInput" or "autoJump" or "screenEffectScale" or "soundCategory_master"));
    }

    private static void MergeDuplicates() {
        const string text = "key_key.swapHands:33\n# note:keep\nkey_key.swapOffhand:18\nkey_key.forward:17\nkey_key.forward:18\n";
        JObject values = JObject.Parse("{\"key_key.swapOffhand\":\"key.keyboard.r\",\"key_key.forward\":\"key.keyboard.y\"}");
        Equal("key_key.swapHands:19\n# note:keep\nkey_key.swapOffhand:19\nkey_key.forward:21\nkey_key.forward:21\n", Merge(text, values, false));
        Equal("key_key.swapHands:key.keyboard.r\n# note:keep\nkey_key.swapOffhand:key.keyboard.r\nkey_key.forward:key.keyboard.y\nkey_key.forward:key.keyboard.y\n", Merge(text, values));
    }

    private static void MergeSwapAlias() {
        JObject values = JObject.Parse("{\"key_key.swapOffhand\":\"key.keyboard.f\"}");
        Equal("key_key.swapHands:33", Merge("", values, false));
        Equal("key_key.swapOffhand:key.keyboard.f", Merge("", values));
        Equal("key_key.swapOffhand:33", Merge("key_key.swapOffhand:17", values, false));
    }

    private static void CallerKeys() {
        JObject alias = JObject.Parse("{\"key_key.swapHands\":\"key.keyboard.r\"}");
        int calls = 0;
        Equal("key_key.swapHands:33", Merge("key_key.swapHands:33", alias, selector: _ => { calls++; return true; }));
        Equal(0, calls);
        HashSet<string> canonicalKeys = new(["key_key.swapOffhand"], StringComparer.Ordinal);
        Equal("key_key.swapHands:key.keyboard.r", Merge("key_key.swapHands:33", alias, keys: canonicalKeys));
    }

    private static void SelectionOrder() {
        JObject values = JObject.Parse("{\"rawMouseInput\":\"true\",\"mouseSensitivity\":\"0.8\",\"invertYMouse\":\"true\"}");
        List<string> calls = [];
        HashSet<string> keys = new(["rawMouseInput", "mouseSensitivity"], StringComparer.Ordinal);
        Equal("mouseSensitivity:0.2\n", Merge("mouseSensitivity:0.2\n", values, keys: keys, selector: key => { calls.Add(key); return calls.Count == 1; }));
        Equal("rawMouseInput|mouseSensitivity", string.Join("|", calls));
    }

    private static void SelectionFailure() {
        JObject values = JObject.Parse("{\"mouseSensitivity\":\"0.8\",\"fov\":\"90\"}");
        string before = values.ToString();
        InvalidOperationException sentinel = new("selection failed");
        Equal(sentinel, Throws<InvalidOperationException>(() => Merge("mouseSensitivity:0.2\n", values, selector: _ => throw sentinel)));
        Equal(before, values.ToString());
    }

    private static void InvalidReplacements() {
        const string original = "key_key.forward:17\nfov:0.0\nmouseSensitivity:0.2\n";
        JObject values = new(new JProperty(BindingKey, "key.keyboard.unrecognized"), new JProperty("fov", "NaN"), new JProperty("mouseSensitivity", "bad\nvalue"));
        Equal(original, Merge(original, values, false));
        values["mouseSensitivity"] = new string('x', 4097);
        Equal(original, Merge(original, values, false));
    }

    private static void MergeFovForms() {
        JObject values = JObject.Parse("{\"fov\":\"0.5\"}");
        Equal("fov:90\n", Merge("fov:70\n", values, degrees: false));
        Equal("fov:0.5\n", Merge("fov:0.0\n", values, degrees: true));
        Equal("fov:0.5", Merge("fov:1", values));
        Equal("fov:90", Merge("fov:unknown", values));
        Equal("fov:0.5", Merge("fov:unknown", values, degrees: false));
        Equal("fov:90", Merge("", values));
        Equal("fov:0.5", Merge("", values, degrees: false));
        Equal("fov:0.5", Merge("fov:NaN", values));
        Equal("fov:90", Merge("fov:Infinity", values));
        Equal("fov:0.5\nfov:0.5\n", Merge("fov:70\nfov:0.0\n", values));
    }

    private static void MergeFovRounding() {
        Equal("fov:30", Merge("fov:70", JObject.Parse("{\"fov\":\"30.5\"}")));
        Equal("fov:32", Merge("fov:70", JObject.Parse("{\"fov\":\"31.5\"}")));
        Equal("fov:110", Merge("fov:70", JObject.Parse("{\"fov\":\"109.5\"}")));
    }

    private static void MergeLegacyFovParsing() {
        JObject values = JObject.Parse("{\"fov\":\"90\"}");
        foreach (bool fallbackDegrees in new[] { false, true }) {
            foreach (string existing in new[] { "1e400", "-1e400", "infinity", "INFINITY", "+Infinity", "nan", "NAN", "+NaN", "-NaN" }) {
                Equal(fallbackDegrees ? "fov:90\n" : "fov:0.5\n", Merge("fov:" + existing + "\n", values, degrees: fallbackDegrees));
            }
            foreach (string existing in new[] { "Infinity", "-Infinity", " Infinity ", "\u00A0Infinity\u00A0", "\t-Infinity\t" }) Equal("fov:90\n", Merge("fov:" + existing + "\n", values, degrees: fallbackDegrees));
            foreach (string existing in new[] { "NaN", " NaN ", "\u00A0NaN\u00A0", "0", "-1", "1" }) Equal("fov:0.5\n", Merge("fov:" + existing + "\n", values, degrees: fallbackDegrees));
            Equal("fov:90\n", Merge("fov:1.0000001\n", values, degrees: fallbackDegrees));
        }
    }

    private static void MergeLines() {
        JObject value = JObject.Parse("{\"mouseSensitivity\":\"0.8\"}");
        Equal("# author comment\nmouseSensitivity:0.8\n\n", Merge("# author comment\nmouseSensitivity:0.2\n\n", value));
        Equal("lang:zh_cn\n\nmouseSensitivity:0.8\n", Merge("lang:zh_cn\n\n", value));
        Equal("lang:zh_cn\nmouseSensitivity:0.8", Merge("lang:zh_cn", value));
        Equal("mouseSensitivity:0.8", Merge("", value));
        Equal("\nmouseSensitivity:0.8\n", Merge("\n", value));
        Equal(":ignored\n# mouseSensitivity:comment\n mouseSensitivity:keep\nmouseSensitivity:0.8", Merge(":ignored\n# mouseSensitivity:comment\n mouseSensitivity:keep", value));
    }

    private static void MixedNewlines() {
        const string mixed = "# comment\r\nmouseSensitivity:0.2\nlang:zh_cn\r\n";
        JObject value = JObject.Parse("{\"mouseSensitivity\":\"0.8\"}");
        Equal("# comment\r\nmouseSensitivity:0.8\r\nlang:zh_cn\r\n", Merge(mixed, value));
        Equal("# comment\r\nmouseSensitivity:0.2\r\nlang:zh_cn\r\n", Merge(mixed, value, selector: _ => false));
        Equal("x:1\r\ny:2", Merge("x:1\r\ny:2", new JObject()));
        Equal("", Merge("", new JObject()));
    }

    private static void CallerComparer() {
        JObject values = JObject.Parse("{\"mouseSensitivity\":\"0.8\"}");
        Equal("mouseSensitivity:0.2", Merge("mouseSensitivity:0.2", values, keys: new HashSet<string>(["MOUSESENSITIVITY"], StringComparer.Ordinal)));
        Equal("mouseSensitivity:0.8", Merge("mouseSensitivity:0.2", values, keys: new HashSet<string>(["MOUSESENSITIVITY"], StringComparer.OrdinalIgnoreCase)));
        Equal("mouseSensitivity:0.2", Merge("mouseSensitivity:0.2", values, selector: _ => false));
    }

    private static string Merge(string text, JObject values, bool modern = true, ISet<string>? keys = null, Func<string, bool>? selector = null, bool degrees = true) =>
        PersonalOptionsRules.MergeText(text, values, modern, keys ?? new HashSet<string>(values.Properties().Select(pair => pair.Name), StringComparer.Ordinal), selector ?? Selected, degrees);

    private static bool Selected(string key) => PersonalOptionsRules.VanillaBinding(key) || PersonalOptionsRules.IsMouseKey(key) || PersonalOptionsRules.IsViewKey(key) || PersonalOptionsRules.IsAccessibilityKey(key);

    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static T Throws<T>(Action action) where T : Exception {
        try {
            action();
        } catch (T error) {
            return error;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
