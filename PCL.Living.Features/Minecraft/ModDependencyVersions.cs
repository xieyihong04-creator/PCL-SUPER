using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using MatchState = PCL.Living.Features.ModDependencyMatchState;

namespace PCL.Living.Features;

// LF ModDependencyInspector.vb version predicates and three-state aggregation.
// Unknown deliberately remains non-fatal; preserve LF metadata rules without new compatibility policy.
public static class ModDependencyVersions {
    public static MatchState Combine(IEnumerable<MatchState> states, bool all) {
        MatchState[] values = states.ToArray();
        if (values.Length == 0) return MatchState.Unknown;
        if (all) {
            if (values.Contains(MatchState.Unsatisfied)) return MatchState.Unsatisfied;
            return values.Contains(MatchState.Unknown) ? MatchState.Unknown : MatchState.Satisfied;
        }
        if (values.Contains(MatchState.Satisfied)) return MatchState.Satisfied;
        return values.Contains(MatchState.Unknown) ? MatchState.Unknown : MatchState.Unsatisfied;
    }
    public static MatchState MatchVersion(string? actual, string? requirement, string syntax = "fabric") =>
        MatchVersions(actual, new JValue(requirement), syntax);
    public static MatchState MatchVersions(string? actual, JToken? requirement, string syntax, int depth = 0) {
        if (depth > 32 || requirement is null) return MatchState.Unknown;
        if (requirement is JArray array) return Combine(array.Select(value => MatchVersions(actual, value, syntax, checked(depth + 1))), false);
        if (requirement is JObject) {
            JToken? any = requirement["any"], all = requirement["all"];
            if ((any is null) == (all is null)) return MatchState.Unknown;
            JToken? values = any ?? all;
            if (values is not JArray children) return MatchState.Unknown;
            return Combine(children.Select(value => MatchVersions(actual, value, syntax, checked(depth + 1))), all is not null);
        }
        if (requirement.Type != JTokenType.String) return MatchState.Unknown;
        string range = requirement.ToString().Trim();
        if (range == "" || range == "*") return MatchState.Satisfied;
        if (string.IsNullOrWhiteSpace(actual)) return MatchState.Unknown;
        string version = actual!;
        if (version == "未知版本" || version.Equals("unknown", StringComparison.OrdinalIgnoreCase) || version.Contains("${") || range.Contains("${")) return MatchState.Unknown;
        if (syntax == "maven") return MatchMaven(version, range);
        if (range.Contains("||")) return Combine(range.Split(new[] { "||" }, StringSplitOptions.None).Select(value => MatchVersion(version, value, syntax)), false);
        range = Regex.Replace(range, @"([><=~^]+)\s+", "$1");
        string[] tokens = Regex.Split(range, @"\s+").Where(value => value != "").ToArray();
        if (tokens.Any(value => !Regex.IsMatch(value, @"^(?:>=|<=|>|<|=|~|\^)?(?:[A-Za-z0-9][A-Za-z0-9._:+*-]*|\*)$"))) return MatchState.Unknown;
        return Combine(tokens.Select(value => MatchPredicate(version, value)), true);
    }
    private sealed class NumericVersion {
        public long[] Parts = null!;
        public string? Prerelease;
    }
    private static NumericVersion? ParseVersion(string? value) {
        Match match = Regex.Match(value ?? "", @"^(\d+(?:\.\d+)*)(?:-((?:[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?))?(?:\+[0-9A-Za-z.-]+)?$");
        if (!match.Success) return null;
        List<long> parts = new();
        foreach (string part in match.Groups[1].Value.Split('.')) {
            if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out long number)) return null;
            parts.Add(number);
        }
        return new NumericVersion { Parts = parts.ToArray(), Prerelease = match.Groups[2].Success ? match.Groups[2].Value : null };
    }
    private static int CompareVersions(NumericVersion left, NumericVersion right) {
        for (int index = 0; index < Math.Max(left.Parts.Length, right.Parts.Length); index++) {
            int comparison = (index < left.Parts.Length ? left.Parts[index] : 0L).CompareTo(index < right.Parts.Length ? right.Parts[index] : 0L);
            if (comparison != 0) return comparison;
        }
        if (string.Equals(left.Prerelease, right.Prerelease, StringComparison.Ordinal)) return 0;
        if (left.Prerelease is null) return 1;
        if (right.Prerelease is null) return -1;
        if (left.Prerelease == "") return -1;
        if (right.Prerelease == "") return 1;
        string[] leftParts = left.Prerelease.Split('.'), rightParts = right.Prerelease.Split('.');
        for (int index = 0; index < Math.Min(leftParts.Length, rightParts.Length); index++) {
            bool leftNumeric = long.TryParse(leftParts[index], out long leftNumber), rightNumeric = long.TryParse(rightParts[index], out long rightNumber);
            int comparison = leftNumeric && rightNumeric ? leftNumber.CompareTo(rightNumber)
                : leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1) : StringComparer.Ordinal.Compare(leftParts[index], rightParts[index]);
            if (comparison != 0) return comparison;
        }
        return leftParts.Length.CompareTo(rightParts.Length);
    }
    private static MatchState MatchPredicate(string actual, string predicate) {
        if (predicate == "*" || predicate == "x" || predicate == "X") return MatchState.Satisfied;
        Match match = Regex.Match(predicate, @"^(>=|<=|>|<|=|~|\^)?(.+)$");
        if (!match.Success) return MatchState.Unknown;
        string operatorText = match.Groups[1].Value, expected = match.Groups[2].Value;
        NumericVersion? current = ParseVersion(actual);
        if (Regex.IsMatch(expected, @"^(?:\d+\.)*(?:\*|[xX])$")) {
            if (operatorText != "" && operatorText != "=") return MatchState.Unknown;
            if (current is null) return MatchState.Unknown;
            string[] prefix = expected.Split('.').TakeWhile(part => part != "*" && part != "x" && part != "X").ToArray();
            for (int index = 0; index < prefix.Length; index++) {
                if (!long.TryParse(prefix[index], out long number)) return MatchState.Unknown;
                if ((index < current.Parts.Length ? current.Parts[index] : 0L) != number) return MatchState.Unsatisfied;
            }
            return MatchState.Satisfied;
        }
        NumericVersion? target = ParseVersion(expected);
        if (current is null || target is null) {
            if (operatorText != "" && operatorText != "=") return MatchState.Unknown;
            if (!Regex.IsMatch(expected, @"^[A-Za-z0-9][A-Za-z0-9._:+-]*$") || expected.EndsWith("+", StringComparison.Ordinal) ||
                new[] { "latest", "latest.release", "latest.integration" }.Contains(expected.ToLowerInvariant())) return MatchState.Unknown;
            return string.Equals(actual, expected, StringComparison.Ordinal) ? MatchState.Satisfied : MatchState.Unsatisfied;
        }
        int comparison = CompareVersions(current, target);
        switch (operatorText) {
            case "": case "=": return comparison == 0 ? MatchState.Satisfied : MatchState.Unsatisfied;
            case ">": return comparison > 0 ? MatchState.Satisfied : MatchState.Unsatisfied;
            case ">=": return comparison >= 0 ? MatchState.Satisfied : MatchState.Unsatisfied;
            case "<": return comparison < 0 ? MatchState.Satisfied : MatchState.Unsatisfied;
            case "<=": return comparison <= 0 ? MatchState.Satisfied : MatchState.Unsatisfied;
            case "~": case "^":
                long[] upper = (long[])target.Parts.Clone();
                int bump;
                if (operatorText == "~") bump = upper.Length > 1 ? 1 : 0;
                else { bump = 0; while (bump < upper.Length - 1 && upper[bump] == 0) bump++; }
                if (upper[bump] == long.MaxValue) return MatchState.Unknown;
                upper[bump] = checked(upper[bump] + 1);
                for (int index = checked(bump + 1); index < upper.Length; index++) upper[index] = 0;
                NumericVersion upperVersion = new() { Parts = upper, Prerelease = null };
                return comparison >= 0 && CompareVersions(current, upperVersion) < 0 ? MatchState.Satisfied : MatchState.Unsatisfied;
            default: return MatchState.Unknown;
        }
    }
    private static MatchState MatchMaven(string actual, string range) {
        if (!range.StartsWith("[", StringComparison.Ordinal) && !range.StartsWith("(", StringComparison.Ordinal)) return MatchState.Unknown;
        MatchCollection intervals = Regex.Matches(range, @"[\[\(][^\[\]\(\)]*[\]\)]");
        if (intervals.Count == 0 || Regex.Replace(range, @"[\[\(][^\[\]\(\)]*[\]\)]", "").Trim(' ', ',') != "") return MatchState.Unknown;
        List<MatchState> values = new();
        NumericVersion? current = ParseVersion(actual);
        foreach (Match interval in intervals) {
            string raw = interval.Value, body = raw.Substring(1, raw.Length - 2);
            string[] parts = body.Split(',');
            if (parts.Length == 1) {
                if (raw[0] != '[' || raw.Last() != ']') values.Add(MatchState.Unknown);
                else {
                    NumericVersion? target = ParseVersion(parts[0].Trim());
                    if (current is null || target is null || !Regex.IsMatch(actual, @"^\d+(?:\.\d+)*$") || !Regex.IsMatch(parts[0].Trim(), @"^\d+(?:\.\d+)*$"))
                        values.Add(string.Equals(actual, parts[0].Trim(), StringComparison.Ordinal) ? MatchState.Satisfied : MatchState.Unknown);
                    else values.Add(CompareVersions(current, target) == 0 ? MatchState.Satisfied : MatchState.Unsatisfied);
                }
                continue;
            }
            if (parts.Length != 2 || current is null || !Regex.IsMatch(actual, @"^\d+(?:\.\d+)*$")) {
                values.Add(MatchState.Unknown);
                continue;
            }
            List<MatchState> bounds = new();
            for (int index = 0; index <= 1; index++) {
                string bound = parts[index].Trim();
                if (bound == "") { bounds.Add(MatchState.Satisfied); continue; }
                NumericVersion? target = ParseVersion(bound);
                if (target is null || !Regex.IsMatch(bound, @"^\d+(?:\.\d+)*$")) { bounds.Add(MatchState.Unknown); continue; }
                int compare = CompareVersions(current, target);
                bool fits = index == 0 ? compare > 0 || (compare == 0 && raw[0] == '[') : compare < 0 || (compare == 0 && raw.Last() == ']');
                bounds.Add(fits ? MatchState.Satisfied : MatchState.Unsatisfied);
            }
            values.Add(Combine(bounds, true));
        }
        return Combine(values, false);
    }
}
