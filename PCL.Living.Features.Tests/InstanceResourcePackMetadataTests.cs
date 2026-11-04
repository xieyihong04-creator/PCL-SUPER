using System.Globalization;
using Newtonsoft.Json;
using PCL.Living.Features;

internal static class InstanceResourcePackMetadataTests {
    private const string Format = " · 资源格式 ";

    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("resource metadata null and missing fields leave the detail unchanged", MissingMetadata);
        yield return ("resource metadata keeps description whitespace and appends the format afterward", StringDescription);
        yield return ("resource metadata skips normalization for empty and whitespace descriptions", BlankDescriptions);
        yield return ("resource metadata text members retain nonfloat scalar and container ToString behavior", TextMembers);
        yield return ("resource metadata preserves scalar and array shape exceptions", ShapeErrors);
        yield return ("resource metadata parses the complete object before changing the detail", ParseErrors);
        yield return ("resource metadata format fields accept arbitrary original nonfloat tokens", FormatTokens);
        yield return ("resource metadata scalar values follow the current culture including BigInteger", IntegerCulture);
        yield return ("resource metadata uses the first normalization result and the second result length", NormalizationOrder);
        yield return ("resource metadata truncates normalized text at 180 UTF16 units", Utf16Limit);
        yield return ("resource metadata callback failures preserve their exception and detail state", CallbackFailures);
        yield return ("resource metadata retains description assignment before a later format failure", LateFormatFailure);
        yield return ("resource metadata preserves Substring failure before assigning its detail", NormalizationLengthMismatch);
    }

    private static void MissingMetadata() {
        foreach (string? metadata in new[] { null, "{}", "{\"Pack\":{\"description\":\"ignored\"}}", "{\"pack\":{}}" }) {
            string detail = "original";
            InstanceResourceDetailsRules.ApplyPackMetadata(metadata, ref detail, null!);
            Equal("original", detail);
        }
        string absent = null!;
        InstanceResourceDetailsRules.ApplyPackMetadata(null, ref absent, null!);
        Equal<string?>(null, absent);
    }

    private static void StringDescription() {
        string detail = "old";
        int calls = 0;
        InstanceResourceDetailsRules.ApplyPackMetadata("{\"pack\":{\"description\":\"  世界 title  \",\"pack_format\":22}}", ref detail, text => {
            calls++;
            Equal("  世界 title  ", text);
            Equal("old", detail);
            return text;
        });
        Equal(2, calls);
        Equal("  世界 title  " + Format + "22", detail);
    }

    private static void BlankDescriptions() {
        foreach (string description in new[] { "\"\"", "\" \\t\\u00a0\"", "{}", "{\"text\":null}", "{\"text\":\"\"}" }) {
            string detail = "old";
            string metadata = "{\"pack\":{\"description\":" + description + ",\"pack_format\":7}}";
            InstanceResourceDetailsRules.ApplyPackMetadata(metadata, ref detail, _ => throw new Exception("Blank text must not be normalized."));
            Equal("old" + Format + "7", detail);
        }
    }

    private static void TextMembers() {
        WithCulture(CultureInfo.GetCultureInfo("en-US"), () => {
            foreach ((string json, string expected) in new[] {
                ("\"plain\"", "plain"), ("12", "12"), ("true", "True"),
                ("[1,false]", Lines("[", "  1,", "  false", "]")),
                ("{\"one\":1}", Lines("{", "  \"one\": 1", "}"))
            }) {
                string detail = "old";
                int calls = 0;
                InstanceResourceDetailsRules.ApplyPackMetadata("{\"pack\":{\"description\":{\"text\":" + json + "}}}", ref detail, value => {
                    calls++;
                    Equal(expected, value);
                    return "normalized";
                });
                Equal(2, calls);
                Equal("normalized", detail);
            }
        });
    }

    private static void ShapeErrors() {
        foreach (string metadata in new[] {
            "{\"pack\":null}", "{\"pack\":0}", "{\"pack\":false}", "{\"pack\":\"scalar\"}",
            "{\"pack\":{\"description\":null}}", "{\"pack\":{\"description\":3}}", "{\"pack\":{\"description\":true}}"
        }) {
            string detail = "original";
            Throws<InvalidOperationException>(() => InstanceResourceDetailsRules.ApplyPackMetadata(metadata, ref detail, _ => throw new Exception("Unexpected normalization.")));
            Equal("original", detail);
        }
        foreach (string metadata in new[] { "{\"pack\":[]}", "{\"pack\":{\"description\":[]}}" }) {
            string detail = "original";
            Throws<ArgumentException>(() => InstanceResourceDetailsRules.ApplyPackMetadata(metadata, ref detail, _ => throw new Exception("Unexpected normalization.")));
            Equal("original", detail);
        }
    }

    private static void ParseErrors() {
        foreach (string metadata in new[] { "", "{", "[]", "null", "{\"pack\":{\"description\":\"new\"},\"later\":[}" }) {
            string detail = "old";
            Throws<JsonReaderException>(() => InstanceResourceDetailsRules.ApplyPackMetadata(metadata, ref detail, _ => throw new Exception("Incomplete JSON must not be normalized.")));
            Equal("old", detail);
        }
    }

    private static void FormatTokens() {
        WithCulture(CultureInfo.GetCultureInfo("en-US"), () => {
            foreach ((string json, string expected) in new[] {
                ("null", ""), ("\"named\"", "named"), ("-8", "-8"), ("false", "False"),
                ("[1,true]", Lines("[", "  1,", "  true", "]")),
                ("{\"key\":1}", Lines("{", "  \"key\": 1", "}"))
            }) {
                string detail = "old";
                InstanceResourceDetailsRules.ApplyPackMetadata("{\"pack\":{\"pack_format\":" + json + "}}", ref detail, null!);
                Equal("old" + Format + expected, detail);
            }
            string initiallyNull = null!;
            InstanceResourceDetailsRules.ApplyPackMetadata("{\"pack\":{\"pack_format\":22}}", ref initiallyNull, null!);
            Equal(Format + "22", initiallyNull);
        });
    }

    private static void IntegerCulture() {
        CultureInfo custom = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        custom.NumberFormat.NegativeSign = "NEG";
        custom.NumberFormat.NumberGroupSeparator = "_";
        foreach ((CultureInfo culture, string sign) in new[] { (CultureInfo.GetCultureInfo("en-US"), "-"), (CultureInfo.GetCultureInfo("fr-FR"), "-"), (custom, "NEG") }) {
            WithCulture(culture, () => {
                string detail = "old";
                InstanceResourceDetailsRules.ApplyPackMetadata("{\"pack\":{\"description\":{\"text\":-9223372036854775809},\"pack_format\":-42}}", ref detail, value => value);
                Equal(sign + "9223372036854775809" + Format + sign + "42", detail);
            });
        }
    }

    private static void NormalizationOrder() {
        string detail = "original";
        int calls = 0;
        InstanceResourceDetailsRules.ApplyPackMetadata("{\"pack\":{\"description\":\"source\",\"pack_format\":5}}", ref detail, value => {
            Equal("source", value);
            Equal("original", detail);
            return ++calls == 1 ? "receiver" : "abc";
        });
        Equal(2, calls);
        Equal("rec" + Format + "5", detail);
    }

    private static void Utf16Limit() {
        string prefix = new('a', 179);
        string detail = "old";
        InstanceResourceDetailsRules.ApplyPackMetadata("{\"pack\":{\"description\":\"" + prefix + "\\ud83c\\udf0d\"}}", ref detail, value => value);
        Equal(180, detail.Length);
        Equal(prefix + "\ud83c", detail);

        string raw = prefix + "\r\nb";
        string normalized = prefix + " b";
        int calls = 0;
        InstanceResourceDetailsRules.ApplyPackMetadata("{\"pack\":{\"description\":\"" + prefix + "\\r\\nb\"}}", ref detail, value => {
            calls++;
            Equal(raw, value);
            return normalized;
        });
        Equal(2, calls);
        Equal(prefix + " ", detail);

        raw = "a\r\nb\nc\rd\fe\u0085f\u2028g\u2029h\u000bi";
        normalized = "a b c d e f g h\u000bi";
        calls = 0;
        // This checks the callback boundary, not a replacement implementation of
        // Shared. The production adapter and CLR4 oracle use the real pinned helper.
        InstanceResourceDetailsRules.ApplyPackMetadata("{\"pack\":{\"description\":\"a\\r\\nb\\nc\\rd\\fe\\u0085f\\u2028g\\u2029h\\u000bi\"}}", ref detail, value => {
            calls++;
            Equal(raw, value);
            return normalized;
        });
        Equal(2, calls);
        Equal(normalized, detail);
    }

    private static void CallbackFailures() {
        foreach (int failAt in new[] { 1, 2 }) {
            string detail = "old";
            int calls = 0;
            IOException failure = new("sentinel", new ArgumentException("business inner exception"));
            IOException observed = Throws<IOException>(() => InstanceResourceDetailsRules.ApplyPackMetadata("{\"pack\":{\"description\":\"new\"}}", ref detail, _ => {
                if (++calls == failAt) throw failure;
                return "receiver";
            }));
            Equal(true, ReferenceEquals(failure, observed));
            Equal(failAt, calls);
            Equal("old", detail);
        }
        string changedByCallback = "old";
        ArgumentException callbackFailure = new("sentinel");
        Throws<ArgumentException>(() => InstanceResourceDetailsRules.ApplyPackMetadata("{\"pack\":{\"description\":\"new\"}}", ref changedByCallback, _ => {
            changedByCallback = "callback side effect";
            throw callbackFailure;
        }));
        Equal("callback side effect", changedByCallback);
    }

    private static void LateFormatFailure() {
        CultureInfo culture = (CultureInfo)CultureInfo.GetCultureInfo("ar-SA").Clone();
        culture.DateTimeFormat.Calendar = new UmAlQuraCalendar();
        WithCulture(culture, () => {
            string detail = "old";
            int calls = 0;
            // The parsed DateTime is outside this explicit calendar's range. Its
            // ToString happens only after the description has been assigned.
            Throws<ArgumentOutOfRangeException>(() => InstanceResourceDetailsRules.ApplyPackMetadata("{\"pack\":{\"description\":\"new\",\"pack_format\":\"1000-01-01T00:00:00\"}}", ref detail, value => {
                calls++;
                return value;
            }));
            Equal(2, calls);
            Equal("new", detail);
        });
    }

    private static void NormalizationLengthMismatch() {
        string detail = "old";
        int calls = 0;
        Throws<ArgumentOutOfRangeException>(() => InstanceResourceDetailsRules.ApplyPackMetadata("{\"pack\":{\"description\":\"new\"}}", ref detail, _ => ++calls == 1 ? "short" : new string('x', 200)));
        Equal(2, calls);
        Equal("old", detail);
    }

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines);

    private static void WithCulture(CultureInfo culture, Action run) {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = culture;
            run();
        } finally { CultureInfo.CurrentCulture = previous; }
    }

    private static T Throws<T>(Action run) where T : Exception {
        try { run(); }
        catch (Exception error) {
            Equal(typeof(T), error.GetType());
            return (T)error;
        }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}].");
    }
}
