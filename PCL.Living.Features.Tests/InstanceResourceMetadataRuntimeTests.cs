using System.Globalization;
using Newtonsoft.Json;
using PCL.Living.Features;

internal static class InstanceResourceMetadataRuntimeTests {
    private const string Format = " · 资源格式 ";

    // Independent real CLR4 report for migration commit 4eb50fb, using the
    // verbatim f4 metadata snippet and original Newtonsoft DLL. Numeric strings
    // below come from its pack-metadata.numeric/container-* results, not .NET 10.
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("resource metadata retains measured CLR4 scalar precision and culture", ScalarPrecision);
        yield return ("resource metadata retains measured CLR4 zero signs after parsing", SignedZero);
        yield return ("resource metadata keeps nested JSON roundtrip precision and zero formatting", NestedNumbers);
        yield return ("resource metadata rejects visible and nested numeric overflow before mutation", VisibleOverflows);
        yield return ("resource metadata rejects ignored and overwritten overflow tokens", IgnoredAndDuplicateOverflows);
        yield return ("resource metadata still accepts explicit NaN and infinity tokens", ExplicitNonFinite);
        yield return ("resource metadata preserves strings comments and ordinary duplicate properties", LexicalBoundaries);
        yield return ("resource metadata overflow failures precede shape access and normalization", OverflowPrecedence);
        yield return ("resource metadata retains measured finite parse rounding and container roundtrip boundaries", FiniteBoundaries);
    }

    private static void ScalarPrecision() {
        WithCultures(culture => {
            string precision = culture == "fr-FR" ? "1,23456789012346" : "1.23456789012346";
            Equal(precision + Format + precision, Apply(Numeric("1.2345678901234567")));
            Equal("1E+308" + Format + "1E+308", Apply(Numeric("1e308")));
        });
    }

    private static void SignedZero() {
        WithCultures(_ => {
            foreach (string literal in new[] { "-0.0", "-1e-999" })
                Equal("0" + Format + "0", Apply(Numeric(literal)));
        });
    }

    private static void NestedNumbers() {
        WithCultures(_ => {
            foreach ((string literal, string oldJsonNumber) in new[] { ("-0.0", "0.0"), ("1.2345678901234567", "1.2345678901234567") }) {
                string metadata = "{\"pack\":{\"description\":{\"text\":{\"outer\":[{\"n\":" + literal + "}]}},\"pack_format\":[{\"n\":" + literal + "}]},\"ignored\":" + literal + "}";
                // The report's Windows CRLF is the platform writer newline. Only
                // that newline changes here; the measured digits and indentation stay fixed.
                string description = Lines("{", "  \"outer\": [", "    {", "      \"n\": " + oldJsonNumber, "    }", "  ]", "}").Replace(Environment.NewLine, " ");
                string format = Lines("[", "  {", "    \"n\": " + oldJsonNumber, "  }", "]");
                Equal(description + Format + format, Apply(metadata));
            }
        });
    }

    private static void VisibleOverflows() {
        WithCultures(_ => {
            foreach (string literal in new[] { "1e400", "-1e400" }) {
                RejectOverflow(Numeric(literal));
                RejectOverflow("{\"pack\":{\"description\":\"new\",\"pack_format\":" + literal + "}}");
                RejectOverflow("{\"pack\":{\"description\":{\"text\":{\"outer\":[{\"n\":" + literal + "}]}},\"pack_format\":[{\"n\":" + literal + "}]}}");
            }
        });
    }

    private static void IgnoredAndDuplicateOverflows() {
        WithCultures(_ => {
            foreach (string literal in new[] { "1e400", "-1e400" }) {
                // The first two placements extend the report's ignored.nested
                // case; replacing a property later must not erase its parse failure.
                RejectOverflow("{\"pack\":{\"description\":\"stable\"},\"ignored\":{\"nested\":" + literal + "}}");
                RejectOverflow("{\"ignored\":{\"nested\":" + literal + "},\"pack\":{\"description\":\"stable\"}}");
                RejectOverflow("{\"pack\":{\"description\":\"stable\"},\"ignored\":{\"n\":" + literal + ",\"n\":0}}");
                RejectOverflow("{\"pack\":{\"description\":{\"text\":" + literal + "},\"description\":\"later\"}}");
            }
        });
    }

    private static void ExplicitNonFinite() {
        WithCultures(_ => {
            // Both measured cultures in the CLR4 report use these symbols.
            foreach ((string literal, string oldText) in new[] { ("NaN", "NaN"), ("Infinity", "\u221e"), ("-Infinity", "-\u221e") }) {
                Equal(oldText + Format + oldText, Apply(Numeric(literal)));
                Equal("stable", Apply("{\"pack\":{\"description\":\"stable\"},\"ignored\":{\"nested\":" + literal + "}}"));
                foreach (string ending in new[] { "\r", "\n", "\r\n" }) {
                    string padding = new string('p', 5000);
                    Equal("stable" + Format + oldText,
                        Apply("/* " + padding + " */" + ending + "{\"pack\":{\"description\":\"stable\",\"pack_format\":" + literal + "}}"));
                }
            }
        });
    }

    private static void LexicalBoundaries() {
        WithCultures(_ => {
            Equal("1e400 -1e400 NaN Infinity" + Format + "1e400",
                Apply("{\"pack\":{\"description\":\"1e400 -1e400 NaN Infinity\",\"pack_format\":\"1e400\"},\"ignored\":\"-1e400\"}"));
            Equal("quoted \"1e400\" and /* -1e400 */" + Format + "Infinity",
                Apply("{\"pack\":{\"description\":\"quoted \\\"1e400\\\" and /* -1e400 */\",\"pack_format\":\"Infinity\"}}"));
            Equal("1e400" + Format + "-1e400", Apply("{'pack':{'description':'1e400','pack_format':'-1e400'}}"));
            Equal("stable" + Format + "22", Apply("/* 1e400 -1e400 */\r\n{\r\n\"pack\":{\r\n\"description\":\"stable\", // 1e400 -1e400\r\n\"pack_format\":22\r\n}} // 1e400\r\n"));
            Equal("after" + Format + "22", Apply("{\"pack\":{\"description\":\"before\",\"description\":\"after\",\"pack_format\":7,\"pack_format\":22}}"));
        });
    }

    private static void OverflowPrecedence() {
        WithCultures(_ => {
            // Full JObject parsing must fail before a scalar pack/description can
            // raise its unrelated InvalidOperationException, or any callback runs.
            RejectOverflow("{\"pack\":0,\"ignored\":1e400}");
            RejectOverflow("{\"pack\":{\"description\":true},\"ignored\":-1e400}");
            RejectOverflow("{\"pack\":{\"description\":1e400}}");
            RejectOverflow("{\r\n\"pack\":{\"description\":\"new\"},\r\n\"ignored\":{\r\n\"n\":-1e400\r\n}}\r\n");
            RejectOverflow("/* " + new string('p', 5000) + " */\r\n{\"pack\":{\"description\":\"new\"},\"ignored\":1e400}");
            // A normalized zero must not increment the array position again.
            RejectOverflow("{\"ignored\":[-0.0,1e400]}", "ignored[1]");
        });
    }

    private static void FiniteBoundaries() {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            // Real original snippet outputs from 93c51e2 artifact 13286565797.
            // Scalar and container digits are independent Frame observations.
            foreach ((string literal, string scalar, string json) in new[] {
                ("4.9406564584124654e-324", "4.94065645841247E-324", "4.94065645841247E-324"),
                ("2.2250738585072012e-308", "2.2250738585072E-308", "2.2250738585072009E-308"),
                ("0.84551240822557006", "0.84551240822557", "0.84551240822557"),
                ("282589933980712.5", "282589933980713", "282589933980712.5"),
                ("1.7976931348623158e308", "1.79769313486232E+308", "1.7976931348623157E+308") }) {
                string metadata = "{\"pack\":{\"description\":{\"text\":" + literal + "},\"pack_format\":{\"n\":" + literal + "}}}";
                Equal(scalar + Format + Lines("{", "  \"n\": " + json, "}"), Apply(metadata));
            }
            RejectOverflow("{\"pack\":{\"description\":\"new\",\"pack_format\":1.7976931348623159e308}}");
        } finally { CultureInfo.CurrentCulture = previous; }
    }

    private static string Numeric(string literal) => "{\"pack\":{\"description\":{\"text\":" + literal + "},\"pack_format\":" + literal + "},\"ignored\":{\"nested\":" + literal + "}}";

    private static string Apply(string metadata) {
        string detail = "before";
        // Nested numeric fixtures contain only the JSON writer's platform
        // newline. Real Shared normalization is independently used by the Frame oracle.
        InstanceResourceDetailsRules.ApplyPackMetadata(metadata, ref detail, text => text.Replace(Environment.NewLine, " "));
        return detail;
    }

    private static void RejectOverflow(string metadata, string? expectedPath = null) {
        string detail = "before";
        int normalizations = 0;
        Exception? observed = null;
        try {
            InstanceResourceDetailsRules.ApplyPackMetadata(metadata, ref detail, text => {
                normalizations++;
                return text;
            });
        } catch (Exception error) { observed = error; }
        Equal(typeof(JsonReaderException), observed?.GetType());
        if (expectedPath is not null) Equal(expectedPath, ((JsonReaderException)observed!).Path);
        Equal("before", detail);
        Equal(0, normalizations);
    }

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines);

    private static void WithCultures(Action<string> run) {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try {
            foreach (string name in new[] { "en-US", "fr-FR" }) {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                run(name);
            }
        } finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}].");
    }
}
