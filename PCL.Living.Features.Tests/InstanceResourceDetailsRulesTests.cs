using System.Globalization;
using PCL.Living.Features;

internal static class InstanceResourceDetailsRulesTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("resource size display preserves bytes including zero and negative extrema", Bytes);
        yield return ("resource size display preserves the original unit thresholds", UnitBoundaries);
        yield return ("resource size display uses the original single optional decimal place", FractionalValues);
        yield return ("resource size display caps units at GB through the long maximum", GigabyteCap);
        yield return ("resource size display follows French decimal formatting", FrenchCulture);
        yield return ("resource size display follows the caller's number format without grouping", CustomNumberFormat);
    }

    private static void Bytes() => WithCulture(CultureInfo.GetCultureInfo("en-US"), () => {
        Equal("-9223372036854775808 B", InstanceResourceDetailsRules.FormatSize(long.MinValue));
        Equal("-1024 B", InstanceResourceDetailsRules.FormatSize(-1024));
        Equal("-1 B", InstanceResourceDetailsRules.FormatSize(-1));
        Equal("0 B", InstanceResourceDetailsRules.FormatSize(0));
        Equal("1 B", InstanceResourceDetailsRules.FormatSize(1));
        Equal("1023 B", InstanceResourceDetailsRules.FormatSize(1023));
    });

    private static void UnitBoundaries() => WithCulture(CultureInfo.GetCultureInfo("en-US"), () => {
        Equal("1 KB", InstanceResourceDetailsRules.FormatSize(1024));
        Equal("1 KB", InstanceResourceDetailsRules.FormatSize(1025));
        Equal("1024 KB", InstanceResourceDetailsRules.FormatSize(1048575));
        Equal("1 MB", InstanceResourceDetailsRules.FormatSize(1048576));
        Equal("1 MB", InstanceResourceDetailsRules.FormatSize(1048577));
        Equal("1024 MB", InstanceResourceDetailsRules.FormatSize(1073741823));
        Equal("1 GB", InstanceResourceDetailsRules.FormatSize(1073741824));
        Equal("1 GB", InstanceResourceDetailsRules.FormatSize(1073741825));
    });

    private static void FractionalValues() => WithCulture(CultureInfo.GetCultureInfo("en-US"), () => {
        // These exact halves and values away from rounding ties do not assume an
        // unmeasured Framework result for an extreme floating-point midpoint.
        Equal("1.5 KB", InstanceResourceDetailsRules.FormatSize(1536));
        Equal("1.6 KB", InstanceResourceDetailsRules.FormatSize(1638));
        Equal("1.5 MB", InstanceResourceDetailsRules.FormatSize(1572864));
        Equal("1.5 GB", InstanceResourceDetailsRules.FormatSize(1610612736));
    });

    private static void GigabyteCap() => WithCulture(CultureInfo.GetCultureInfo("en-US"), () => {
        Equal("1024 GB", InstanceResourceDetailsRules.FormatSize(1099511627776));
        Equal("1024 GB", InstanceResourceDetailsRules.FormatSize(1099511627777));
        Equal("5120 GB", InstanceResourceDetailsRules.FormatSize(5497558138880));
        Equal("8589934592 GB", InstanceResourceDetailsRules.FormatSize(long.MaxValue));
    });

    private static void FrenchCulture() => WithCulture(CultureInfo.GetCultureInfo("fr-FR"), () => {
        Equal("1023 B", InstanceResourceDetailsRules.FormatSize(1023));
        Equal("1,5 KB", InstanceResourceDetailsRules.FormatSize(1536));
        Equal("1,5 MB", InstanceResourceDetailsRules.FormatSize(1572864));
        Equal("1,5 GB", InstanceResourceDetailsRules.FormatSize(1610612736));
        Equal("1024 GB", InstanceResourceDetailsRules.FormatSize(1099511627776));
    });

    private static void CustomNumberFormat() {
        CultureInfo culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = ":";
        culture.NumberFormat.NegativeSign = "negative:";
        culture.NumberFormat.NumberGroupSeparator = "_";
        WithCulture(culture, () => {
            Equal("negative:8 B", InstanceResourceDetailsRules.FormatSize(-8));
            Equal("1023 B", InstanceResourceDetailsRules.FormatSize(1023));
            Equal("1:5 KB", InstanceResourceDetailsRules.FormatSize(1536));
            Equal("5120 GB", InstanceResourceDetailsRules.FormatSize(5497558138880));
        });
    }

    private static void WithCulture(CultureInfo culture, Action run) {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = culture;
            run();
        } finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void Equal(string expected, string actual) {
        if (expected != actual) throw new Exception($"Expected [{expected}], got [{actual}].");
    }
}
