using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PCL.Living.Features;

internal static class InstanceKeybindingReaderTests {
    private const string ActionKey = "key_fixture.action";
    private const string LanguagePath = "assets/fixture/lang/zh_cn.json";
    private const string Warning = "读取按键所属 Mod 的本地信息失败";

    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("keybinding reader default fields, order and derived identity stay compatible", DefaultsAndFields);
        yield return ("keybinding reader missing options ignores cancellation and bad mods", MissingOptions);
        yield return ("keybinding reader constructs all defaults before matching existing rows", FactorySequence);
        yield return ("keybinding reader duplicates move to last position and keep raw values", Duplicates);
        yield return ("keybinding reader swap aliases keep first matching original row", AliasOrder);
        yield return ("keybinding reader conflicts preserve defaults, case and unknown exceptions", Conflicts);
        yield return ("keybinding reader options keep automatic BOM detection", OptionsEncodings);
        yield return ("keybinding reader options decode and guard errors precede cancellation", InputErrorOrder);
        yield return ("keybinding reader options retain exact 4 MiB boundary", OptionsLimit);
        yield return ("keybinding reader target language overrides English independent of ZIP order", LanguagePriority);
        yield return ("keybinding reader candidate case and priority case remain distinct", LanguageCase);
        yield return ("keybinding reader equal-priority languages keep ZIP entry order", StableEntries);
        yield return ("keybinding reader legacy language text uses raw first equals", LangText);
        yield return ("keybinding reader JSON language values require exact string keys", JsonValues);
        yield return ("keybinding reader archive StreamReader keeps BOM and replacement fallback", EntryEncodings);
        yield return ("keybinding reader metadata missing, null and scalar names remain distinct", MetadataNames);
        yield return ("keybinding reader metadata preserves Fabric and NeoForge precedence", MetadataPrecedence);
        yield return ("keybinding reader metadata enforces strict 1 MiB thresholds", MetadataLimit);
        yield return ("keybinding reader language entry enforces strict 4 MiB threshold", LanguageLimit);
        yield return ("keybinding reader passes raw title to original newline delegate", TitleDelegate);
        yield return ("keybinding reader aggregates owners using current culture", OwnersCulture);
        yield return ("keybinding reader warns for guarded jars and continues other files", GuardWarning);
        yield return ("keybinding reader bad metadata warns without falling back", BadMetadata);
        yield return ("keybinding reader keeps partial name and owner after later language failure", PartialLanguage);
        yield return ("keybinding reader bad ZIP is fatal even without nonvanilla targets", BadZip);
        yield return ("keybinding reader warning callback exceptions propagate", WarningFailure);
        yield return ("keybinding reader line cancellation preserves completed row state", LineCancellation);
        yield return ("keybinding reader jar cancellation occurs before per-file guard", JarCancellation);
        yield return ("keybinding reader language cancellation keeps earlier row changes", LanguageCancellation);
        yield return ("keybinding reader keeps absence of a final cancellation check", NoFinalCancellation);
        yield return ("keybinding reader metadata failure precedes language cancellation", MetadataBeforeCancellation);
        yield return ("keybinding reader title exceptions retain original catch classification", TitleExceptions);
        if (!OperatingSystem.IsWindows()) yield return ("keybinding reader owner set ignores case on distinct case-sensitive files", OwnerCase);
    }

    private static void DefaultsAndFields() {
        using Fixture fixture = new();
        List<InstanceKeybindingData> defaults = InstanceKeybindingReader.DefaultBindings(fixture.Create);
        Equal(31, defaults.Count);
        Equal(31, fixture.Created.Count);
        Equal("key_key.attack", defaults[0].Key);
        Equal("攻击/破坏", defaults[0].Name);
        Equal("key.mouse.left", defaults[0].Value);
        Equal("key_key.loadToolbarActivator", defaults[21].Key);
        Equal("key_key.hotbar.1", defaults[22].Key);
        Equal("key_key.hotbar.9", defaults[30].Key);
        Equal(false, defaults.Any(item => item.Key is "key_key.smoothCamera" or "key_key.spectatorOutlines"));
        for (int index = 0; index < defaults.Count; index++) {
            Equal(true, ReferenceEquals(defaults[index], fixture.Created[index]));
            Equal("Minecraft", defaults[index].Owners);
            Equal(false, defaults[index].Saved);
            Equal(1, defaults[index].SharedCount);
            Equal(defaults[index].Value, defaults[index].Normalized);
        }
        foreach ((string name, Type type) in new[] { ("Key", typeof(string)), ("Name", typeof(string)), ("Owners", typeof(string)), ("Value", typeof(string)), ("Normalized", typeof(string)), ("SharedCount", typeof(int)), ("Saved", typeof(bool)) }) {
            FieldInfo field = typeof(DerivedBinding).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            Equal(type, field.FieldType);
            Equal(typeof(InstanceKeybindingData), field.DeclaringType);
        }
        DerivedBinding blank = new();
        Equal("未知归属", blank.Owners);
        Equal(true, blank.Saved);
        Equal(0, blank.SharedCount);
        Equal<string?>(null, blank.Normalized);
    }

    private static void MissingOptions() {
        using Fixture fixture = new();
        fixture.InvalidJar("bad.jar");
        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        List<InstanceKeybindingData> rows = fixture.Read(cancel.Token);
        Equal(31, rows.Count);
        Equal(31, fixture.Created.Count);
        Equal(0, fixture.Guards.Count);
        Equal(0, fixture.Warnings.Count);
        Equal(false, File.Exists(fixture.OptionsPath));
    }

    private static void FactorySequence() {
        using Fixture fixture = new();
        fixture.Options("key_key.forward:17");
        fixture.OnCreate = count => { if (count == 32) Equal("key.forward", fixture.Created[0].Name); };
        List<InstanceKeybindingData> rows = fixture.Read();
        Equal(32, fixture.Created.Count);
        InstanceKeybindingData forward = Find(rows, "key_key.forward");
        Equal(true, ReferenceEquals(fixture.Created[0], forward));
        Equal("前进", forward.Name);
        Equal("17", forward.Value);
        Equal("key.keyboard.w", forward.Normalized);
        Equal(true, forward.Saved);
        Equal(31, rows.Count);
    }

    private static void Duplicates() {
        using Fixture fixture = new();
        fixture.Options("# ignored\n:ignored\nkey_fixture.action:key.keyboard.r\nkey_fixture.other:key.keyboard.s\nkey_fixture.action:19:alt\nKey_fixture.unrecognized:20\n");
        List<InstanceKeybindingData> rows = fixture.Read();
        Equal("key_fixture.other", rows[0].Key);
        Equal(ActionKey, rows[1].Key);
        Equal(true, ReferenceEquals(fixture.Created[1], rows[0]));
        Equal(true, ReferenceEquals(fixture.Created[2], rows[1]));
        Equal(false, rows.Any(item => ReferenceEquals(item, fixture.Created[0])));
        Equal("19:alt", rows[1].Value);
        Equal("key.keyboard.r:ALT", rows[1].Normalized);
        Equal("未知归属", rows[1].Owners);
        Equal(true, rows[1].Saved);
        Equal(34, fixture.Created.Count);
    }

    private static void AliasOrder() {
        foreach (bool legacyFirst in new[] { false, true }) {
            using Fixture fixture = new();
            string first = legacyFirst ? "key_key.swapHands" : "key_key.swapOffhand";
            string second = legacyFirst ? "key_key.swapOffhand" : "key_key.swapHands";
            fixture.Options(first + ":33\n" + second + ":19");
            List<InstanceKeybindingData> rows = fixture.Read();
            Equal(first, rows[0].Key);
            Equal("交换副手物品", rows[0].Name);
            Equal(second.Substring(4), rows[1].Name);
            Equal("Minecraft", rows[0].Owners);
            Equal("Minecraft", rows[1].Owners);
            Equal(32, rows.Count);
        }
    }

    private static void Conflicts() {
        using Fixture fixture = new();
        fixture.Options("key_fixture.action:key.keyboard.r\nkey_fixture.same:key.keyboard.R\nkey_fixture.chat:key.keyboard.t\nkey_fixture.invalid:invalid\nkey_fixture.unbound:key.keyboard.unknown\nkey_fixture.modified:key.keyboard.unknown:alt\nkey_fixture.modified2:key.keyboard.unknown:ALT\n");
        List<InstanceKeybindingData> rows = fixture.Read();
        Equal(2, Find(rows, ActionKey).SharedCount);
        Equal(2, Find(rows, "key_fixture.same").SharedCount);
        Equal(2, Find(rows, "key_fixture.chat").SharedCount);
        Equal(2, Find(rows, "key_key.chat").SharedCount);
        Equal(0, Find(rows, "key_fixture.invalid").SharedCount);
        Equal(0, Find(rows, "key_fixture.unbound").SharedCount);
        Equal(2, Find(rows, "key_fixture.modified").SharedCount);
        Equal(2, Find(rows, "key_fixture.modified2").SharedCount);
        Equal(false, Find(rows, "key_key.chat").Saved);
    }

    private static void OptionsEncodings() {
        foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32 }) {
            using Fixture fixture = new();
            byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes("key_fixture.action:19\r\n")];
            fixture.OptionsBytes(bytes);
            List<InstanceKeybindingData> rows = fixture.Read();
            Equal("key.keyboard.r", Find(rows, ActionKey).Normalized);
            Bytes(bytes, File.ReadAllBytes(fixture.OptionsPath));
        }
        using Fixture repeated = new();
        repeated.OptionsBytes([239, 187, 191, 239, 187, 191, .. Encoding.UTF8.GetBytes("key_fixture.action:19")]);
        Equal(31, repeated.Read().Count);
    }

    private static void InputErrorOrder() {
        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        using Fixture guarded = new();
        guarded.Options("key_fixture.action:19");
        IOException sentinel = new("original guard denied options");
        guarded.Guard = (_, _) => throw sentinel;
        Equal(sentinel, Throws<IOException>(() => guarded.Read(cancel.Token)));
        Equal(0, guarded.Created.Count);
        Equal(0, guarded.Warnings.Count);
        using Fixture invalid = new();
        invalid.OptionsBytes([255]);
        Throws<DecoderFallbackException>(() => invalid.Read(cancel.Token));
        Equal(0, invalid.Created.Count);
        using Fixture valid = new();
        valid.Options("");
        Throws<OperationCanceledException>(() => valid.Read(cancel.Token));
        Equal(0, valid.Created.Count);
    }

    private static void OptionsLimit() {
        using Fixture fixture = new();
        const int limit = 4 * 1024 * 1024;
        string prefix = "key_fixture.action:19\n#";
        fixture.Options(prefix + new string('x', limit - prefix.Length));
        Equal("19", Find(fixture.Read(), ActionKey).Value);
        using (FileStream output = new(fixture.OptionsPath, FileMode.Append)) output.WriteByte(0);
        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        Throws<IOException>(() => fixture.Read(cancel.Token), "游戏设置文件过大。");
        Equal(0, fixture.Warnings.Count);
    }

    private static void LanguagePriority() {
        using Fixture fixture = new();
        fixture.Options("lang:ZH_CN\nkey_fixture.action:19");
        fixture.Jar("named.jar", (LanguagePath, JsonTitle("目标语言")), ("assets/fixture/lang/en_us.json", JsonTitle("English")));
        Equal("目标语言", Find(fixture.Read(), ActionKey).Name);
    }

    private static void LanguageCase() {
        using Fixture fixture = new();
        fixture.Options("key_fixture.action:19");
        fixture.Jar("case.jar", ("ASSETS/fixture/LANG/ZH_CN.JSON", JsonTitle("Uppercase candidate")), ("assets/fixture/lang/en_us.json", JsonTitle("English later")));
        Equal("English later", Find(fixture.Read(), ActionKey).Name);
    }

    private static void StableEntries() {
        using Fixture fixture = new();
        fixture.Options("key_fixture.action:19");
        fixture.Jar("stable.jar", ("assets/z/lang/zh_cn.json", JsonTitle("First ZIP entry")), ("assets/a/lang/zh_cn.lang", "fixture.action=Second ZIP entry"));
        Equal("Second ZIP entry", Find(fixture.Read(), ActionKey).Name);
    }

    private static void LangText() {
        using Fixture fixture = new();
        fixture.Options("key_fixture.action:19\nkey_fixture.other:20");
        fixture.Jar("text.jar", ("assets/fixture/lang/zh_cn.lang", "fixture.action=first\r\n fixture.action=wrong whitespace\nfixture.action =wrong trailing space\n=empty key\nfixture.action=last=tail\nfixture.other=Other title"));
        List<InstanceKeybindingData> rows = fixture.Read();
        Equal("last=tail", Find(rows, ActionKey).Name);
        Equal("Other title", Find(rows, "key_fixture.other").Name);
    }

    private static void JsonValues() {
        using Fixture fixture = new();
        fixture.Options("key_fixture.action:19\nkey_fixture.other:20\nkey_fixture.empty:21");
        fixture.Jar("values.jar", (LanguagePath, "{\"fixture.action\":42,\"Fixture.action\":\"wrong case\",\"fixture.other\":\"Correct\",\"fixture.empty\":\"\",\"unused\":null}"));
        List<InstanceKeybindingData> rows = fixture.Read();
        Equal("fixture.action", Find(rows, ActionKey).Name);
        Equal("未知归属", Find(rows, ActionKey).Owners);
        Equal("Correct", Find(rows, "key_fixture.other").Name);
        Equal("", Find(rows, "key_fixture.empty").Name);
    }

    private static void EntryEncodings() {
        using Fixture fixture = new();
        fixture.Options("key_fixture.action:19");
        string title = JsonTitle("中文标题");
        fixture.JarBytes("bom.jar", (LanguagePath, [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(title)]));
        Equal("中文标题", Find(fixture.Read(), ActionKey).Name);
        using Fixture replacement = new();
        replacement.Options("key_fixture.action:19");
        replacement.JarBytes("replacement.jar", (LanguagePath, [.. Encoding.UTF8.GetBytes("{\"fixture.action\":\""), 255, .. Encoding.UTF8.GetBytes("\"}")]));
        Equal("\uFFFD", Find(replacement.Read(), ActionKey).Name);
        Equal(0, replacement.Warnings.Count);
    }

    private static void MetadataNames() {
        foreach ((string json, string expected) in new[] { ("{}", "base"), ("{\"name\":null}", ""), ("{\"name\":42}", "42"), ("{\"name\":\"Shown\"}", "Shown") }) {
            using Fixture fixture = new();
            fixture.Options("key_fixture.action:19");
            fixture.Jar("base.jar", ("fabric.mod.json", json), (LanguagePath, JsonTitle("Action")));
            Equal(expected + "（base.jar）", Find(fixture.Read(), ActionKey).Owners);
        }
    }

    private static void MetadataPrecedence() {
        foreach ((bool fabric, bool neo, string expected) in new[] { (true, true, "Fabric"), (false, true, "Neo"), (false, false, "Forge") }) {
            using Fixture fixture = new();
            fixture.Options("key_fixture.action:19");
            List<(string, string)> entries = [("META-INF/mods.toml", "displayName = \"Forge\"")];
            if (neo) entries.Add(("META-INF/neoforge.mods.toml", "  displayName = \"Neo\""));
            if (fabric) entries.Add(("fabric.mod.json", "{\"name\":\"Fabric\"}"));
            entries.Add((LanguagePath, JsonTitle("Action")));
            fixture.Jar("metadata.jar", entries.ToArray());
            Equal(expected + "（metadata.jar）", Find(fixture.Read(), ActionKey).Owners);
        }
    }

    private static void MetadataLimit() {
        const int limit = 1024 * 1024;
        foreach ((int length, string expected) in new[] { (limit - 1, "Fabric"), (limit, "Neo") }) {
            using Fixture fixture = new();
            fixture.Options("key_fixture.action:19");
            fixture.Jar("boundary.jar", ("fabric.mod.json", Pad("{\"name\":\"Fabric\"}", length)), ("META-INF/neoforge.mods.toml", "displayName = \"Neo\""), (LanguagePath, JsonTitle("Action")));
            Equal(expected + "（boundary.jar）", Find(fixture.Read(), ActionKey).Owners);
        }
        using Fixture oversizedNeo = new();
        oversizedNeo.Options("key_fixture.action:19");
        oversizedNeo.Jar("base.jar", ("META-INF/neoforge.mods.toml", Pad("displayName = \"Neo\"", limit)), ("META-INF/mods.toml", "displayName = \"Forge\""), (LanguagePath, JsonTitle("Action")));
        Equal("base（base.jar）", Find(oversizedNeo.Read(), ActionKey).Owners);
    }

    private static void LanguageLimit() {
        const int limit = 4 * 1024 * 1024;
        foreach (int length in new[] { limit - 1, limit }) {
            using Fixture fixture = new();
            fixture.Options("key_fixture.action:19");
            fixture.Jar("boundary.jar", ("assets/fixture/lang/zh_cn.lang", Pad("fixture.action=Near limit\n#", length)));
            Equal(length < limit ? "Near limit" : "fixture.action", Find(fixture.Read(), ActionKey).Name);
        }
    }

    private static void TitleDelegate() {
        using Fixture fixture = new();
        fixture.Options("key_fixture.action:19");
        const string raw = "A\r\nB\n\nC\rD\fE\u0085F\u2028G\u2029H\vI";
        fixture.Jar("title.jar", (LanguagePath, JsonTitle(raw)));
        fixture.Title = value => {
            Equal(raw, value);
            Equal("fixture.action", fixture.Created[0].Name);
            return LegacyTitle(value);
        };
        List<InstanceKeybindingData> rows = fixture.Read();
        Equal("A B  C D E F G H\vI", Find(rows, ActionKey).Name);
        Equal(true, ReferenceEquals(fixture.Created[0], Find(rows, ActionKey)));
    }

    private static void OwnersCulture() {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            using Fixture fixture = new();
            fixture.Options("key_fixture.action:19");
            fixture.Jar("accent.jar", ("fabric.mod.json", "{\"name\":\"ä\"}"), (LanguagePath, JsonTitle("Accent title")));
            fixture.Jar("zeta.jar", ("fabric.mod.json", "{\"name\":\"z\"}"), (LanguagePath, JsonTitle("Zeta title")));
            InstanceKeybindingData row = Find(fixture.Read(), ActionKey);
            Equal("ä（accent.jar）、z（zeta.jar）", row.Owners);
            Equal(true, row.Name is "Accent title" or "Zeta title");
        } finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void GuardWarning() {
        using Fixture fixture = new();
        fixture.Options("key_fixture.action:19");
        fixture.Jar("denied.jar", (LanguagePath, JsonTitle("Denied")));
        fixture.Jar("io.jar", (LanguagePath, JsonTitle("IO")));
        fixture.Jar("good.jar", (LanguagePath, JsonTitle("Good")));
        fixture.Guard = (_, file) => {
            if (Path.GetFileName(file) == "denied.jar") throw new UnauthorizedAccessException("fixture denied");
            if (Path.GetFileName(file) == "io.jar") throw new IOException("fixture IO");
        };
        InstanceKeybindingData row = Find(fixture.Read(), ActionKey);
        Equal("Good", row.Name);
        Equal("good（good.jar）", row.Owners);
        Equal(2, fixture.Warnings.Count);
        Equal(true, fixture.Warnings.Any(error => error is UnauthorizedAccessException));
        Equal(true, fixture.Warnings.Any(error => error is IOException));
    }

    private static void BadMetadata() {
        using Fixture fixture = new();
        fixture.Options("key_fixture.action:19");
        fixture.Jar("bad.jar", ("fabric.mod.json", "{"), ("META-INF/mods.toml", "displayName = \"Must not fallback\""), (LanguagePath, JsonTitle("Skipped")));
        fixture.Jar("good.jar", (LanguagePath, JsonTitle("Good")));
        InstanceKeybindingData row = Find(fixture.Read(), ActionKey);
        Equal("Good", row.Name);
        Equal("good（good.jar）", row.Owners);
        Equal(1, fixture.Warnings.Count);
        Equal(true, fixture.Warnings[0] is JsonException);
    }

    private static void PartialLanguage() {
        using Fixture fixture = new();
        fixture.Options("key_fixture.action:19");
        fixture.Jar("partial.jar", ("assets/fixture/lang/en_us.json", JsonTitle("Earlier success")), (LanguagePath, "{"));
        InstanceKeybindingData row = Find(fixture.Read(), ActionKey);
        Equal("Earlier success", row.Name);
        Equal("partial（partial.jar）", row.Owners);
        Equal(1, fixture.Warnings.Count);
    }

    private static void BadZip() {
        foreach (bool target in new[] { false, true }) {
            using Fixture fixture = new();
            fixture.Options(target ? "key_fixture.action:19" : "lang:zh_cn");
            fixture.InvalidJar("bad.jar");
            Throws<InvalidDataException>(() => fixture.Read());
            Equal(0, fixture.Warnings.Count);
            Equal(true, fixture.Guards.Any(file => Path.GetFileName(file) == "bad.jar"));
        }
    }

    private static void WarningFailure() {
        using Fixture fixture = new();
        fixture.Options("key_fixture.action:19");
        fixture.Jar("bad.jar", ("fabric.mod.json", "{"));
        InvalidOperationException sentinel = new("warning sink failed");
        fixture.OnWarn = (_, _) => throw sentinel;
        Equal(sentinel, Throws<InvalidOperationException>(() => fixture.Read()));
    }

    private static void LineCancellation() {
        using Fixture fixture = new();
        using CancellationTokenSource cancel = new();
        fixture.Options("key_fixture.action:19\nkey_fixture.other:20");
        fixture.OnCreate = count => { if (count == 1) cancel.Cancel(); };
        Throws<OperationCanceledException>(() => fixture.Read(cancel.Token));
        Equal(1, fixture.Created.Count);
        Equal("key.keyboard.r", fixture.Created[0].Normalized);
        Equal(0, fixture.Warnings.Count);
    }

    private static void JarCancellation() {
        using Fixture fixture = new();
        using CancellationTokenSource cancel = new();
        fixture.Options("key_fixture.action:19");
        fixture.InvalidJar("bad.jar");
        fixture.OnCreate = count => { if (count == 32) cancel.Cancel(); };
        Throws<OperationCanceledException>(() => fixture.Read(cancel.Token));
        Equal(1, fixture.Guards.Count);
        Equal(0, fixture.Warnings.Count);
    }

    private static void LanguageCancellation() {
        using Fixture fixture = new();
        using CancellationTokenSource cancel = new();
        fixture.Options("key_fixture.action:19");
        fixture.Jar("cancel.jar", ("assets/fixture/lang/en_us.json", JsonTitle("First")), (LanguagePath, JsonTitle("Later")));
        fixture.Title = title => { cancel.Cancel(); return title; };
        Throws<OperationCanceledException>(() => fixture.Read(cancel.Token));
        Equal("First", fixture.Created[0].Name);
        Equal("未知归属", fixture.Created[0].Owners);
        Equal(0, fixture.Warnings.Count);
    }

    private static void NoFinalCancellation() {
        using Fixture fixture = new();
        using CancellationTokenSource cancel = new();
        fixture.Options("key_fixture.action:19");
        fixture.OnCreate = count => { if (count == 1) cancel.Cancel(); };
        Equal("key.keyboard.r", Find(fixture.Read(cancel.Token), ActionKey).Normalized);
        Equal(true, cancel.IsCancellationRequested);
    }

    private static void MetadataBeforeCancellation() {
        using Fixture fixture = new();
        using CancellationTokenSource cancel = new();
        fixture.Options("key_fixture.action:19");
        fixture.Jar("bad.jar", ("fabric.mod.json", "{"), (LanguagePath, JsonTitle("Not read")));
        fixture.Guard = (_, file) => { if (Path.GetExtension(file) == ".jar") cancel.Cancel(); };
        Equal("fixture.action", Find(fixture.Read(cancel.Token), ActionKey).Name);
        Equal(1, fixture.Warnings.Count);
        Equal(true, fixture.Warnings[0] is JsonException);
        Equal(true, cancel.IsCancellationRequested);
    }

    private static void TitleExceptions() {
        foreach (bool caught in new[] { false, true }) {
            using Fixture fixture = new();
            fixture.Options("key_fixture.action:19");
            fixture.Jar("title.jar", ("assets/fixture/lang/en_us.json", JsonTitle("Earlier")), (LanguagePath, JsonTitle("Later")));
            Exception sentinel = caught ? new IOException("title IO") : new InvalidDataException("title invalid data");
            fixture.Title = title => title == "Later" ? throw sentinel : title;
            if (caught) {
                InstanceKeybindingData row = Find(fixture.Read(), ActionKey);
                Equal("Earlier", row.Name);
                Equal("title（title.jar）", row.Owners);
                Equal(sentinel, fixture.Warnings.Single());
            } else {
                Equal(sentinel, Throws<InvalidDataException>(() => fixture.Read()));
                Equal("Earlier", fixture.Created[0].Name);
                Equal("未知归属", fixture.Created[0].Owners);
                Equal(0, fixture.Warnings.Count);
            }
        }
    }

    private static void OwnerCase() {
        using Fixture fixture = new();
        fixture.Options("key_fixture.action:19");
        fixture.Jar("same.jar", ("fabric.mod.json", "{\"name\":\"Case\"}"), (LanguagePath, JsonTitle("one")));
        fixture.Jar("SAME.jar", ("fabric.mod.json", "{\"name\":\"case\"}"), (LanguagePath, JsonTitle("two")));
        string owners = Find(fixture.Read(), ActionKey).Owners;
        Equal(false, owners.Contains('、'));
        Equal(true, string.Equals("Case（same.jar）", owners, StringComparison.OrdinalIgnoreCase));
    }

    private static InstanceKeybindingData Find(List<InstanceKeybindingData> rows, string key) => rows.Single(item => item.Key == key);
    private static string JsonTitle(string title) => new JObject(new JProperty("fixture.action", title)).ToString();
    private static string Pad(string prefix, int length) => prefix + new string(' ', length - Encoding.UTF8.GetByteCount(prefix));
    private static string LegacyTitle(string title) => Regex.Replace(title, "\r\n|[\n\r\f\u0085\u2028\u2029]", " ");
    private static void Bytes(byte[] expected, byte[] actual) {
        if (!expected.SequenceEqual(actual)) throw new InvalidOperationException("File bytes differ.");
    }
    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
    private static T Throws<T>(Action action, string? message = null) where T : Exception {
        try { action(); } catch (T error) {
            if (message is not null) Equal(message, error.Message);
            return error;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class DerivedBinding : InstanceKeybindingData { }
    private sealed class Fixture : IDisposable {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PclKeybindingReader-" + Guid.NewGuid().ToString("N"));
        internal string OptionsPath => Path.Combine(Root, "options.txt");
        internal List<DerivedBinding> Created { get; } = [];
        internal List<string> Guards { get; } = [];
        internal List<Exception> Warnings { get; } = [];
        internal Action<int>? OnCreate;
        internal Action<string, string>? Guard;
        internal Action<Exception, string>? OnWarn;
        internal Func<string, string>? Title;
        internal Fixture() => Directory.CreateDirectory(Root);
        internal InstanceKeybindingData Create() {
            OnCreate?.Invoke(Created.Count + 1);
            DerivedBinding item = new();
            Created.Add(item);
            return item;
        }
        internal List<InstanceKeybindingData> Read(CancellationToken token = default) => InstanceKeybindingReader.ReadBindings(Root, token,
            (root, file) => { Equal(Root, root); Guards.Add(file); Guard?.Invoke(root, file); },
            (error, message) => { Equal(Warning, message); Warnings.Add(error); OnWarn?.Invoke(error, message); },
            value => Title?.Invoke(value) ?? LegacyTitle(value), Create);
        internal void Options(string text) => OptionsBytes(Encoding.UTF8.GetBytes(text));
        internal void OptionsBytes(byte[] bytes) => File.WriteAllBytes(OptionsPath, bytes);
        internal void Jar(string name, params (string Name, string Text)[] entries) => JarBytes(name, entries.Select(entry => (entry.Name, Encoding.UTF8.GetBytes(entry.Text))).ToArray());
        internal void JarBytes(string name, params (string Name, byte[] Bytes)[] entries) {
            string folder = Path.Combine(Root, "mods");
            Directory.CreateDirectory(folder);
            using ZipArchive archive = ZipFile.Open(Path.Combine(folder, name), ZipArchiveMode.Create);
            foreach ((string path, byte[] bytes) in entries) {
                using Stream stream = archive.CreateEntry(path).Open();
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        internal void InvalidJar(string name) {
            Directory.CreateDirectory(Path.Combine(Root, "mods"));
            File.WriteAllText(Path.Combine(Root, "mods", name), "not a ZIP archive");
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
