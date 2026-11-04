using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCL.Living.Features;

// Scoped to LF ReadPackInfo metadata. Evidence: original CLR4 v5 report at
// 4eb50fbbc884fb284aac7377a897aaef4176f8d2 and 93c51e2dcfc60caf38327a60a11bacd2bc6c6b31.
// Newtonsoft source 0a2e291c0d9c0c7675d445703e51750363a549ef; numeric source citations live in the two scoped helpers.
internal static class InstanceResourcePackMetadataCompatibility {
    internal static JObject ReadObject(string metadata) {
#if NETFRAMEWORK
        return JObject.Parse(metadata);
#else
        using var reader = new MetadataReader(metadata);
        JObject result = JObject.Load(reader);
        // Same trailing-content check as the pinned JObject.Parse. Parsing all
        // properties (including ignored/overwritten ones) precedes ref mutation.
        while (reader.Read()) { }
        return result;
#endif
    }

    internal static string? TokenText(JToken? token) {
#if !NETFRAMEWORK
        // JValue.ToString used Framework's default 15-digit general format.
        // Containers still use Newtonsoft's own invariant JSON writer, not G15.
        if (token is JValue { Value: double number } && !double.IsNaN(number) && !double.IsInfinity(number))
            return LegacyPackMetadataNumberFormatting.Format(number, 15, CultureInfo.CurrentCulture);
        if (token is JContainer) {
            using var text = new StringWriter(CultureInfo.InvariantCulture);
            using var writer = new MetadataWriter(text) { Formatting = Formatting.Indented };
            token.WriteTo(writer);
            return text.ToString();
        }
#endif
        return token?.ToString();
    }

    // Finite values only; named nonfinite JSON values retain the original writer.
    internal static string FormatRoundTrip(double value, CultureInfo culture) {
#if NETFRAMEWORK
        return value.ToString("R", culture);
#else
        string candidate = LegacyPackMetadataNumberFormatting.Format(value, 15, CultureInfo.InvariantCulture);
        int precision = LegacyPackMetadataNumberParser.TryParseRoundTripCandidate(candidate, out double parsed) && parsed == value ? 15 : 17;
        return LegacyPackMetadataNumberFormatting.Format(value, precision, culture);
#endif
    }

#if !NETFRAMEWORK
    private sealed class MetadataWriter : JsonTextWriter {
        internal MetadataWriter(TextWriter output) : base(output) { }

        public override void WriteValue(double value) {
            if (double.IsNaN(value) || double.IsInfinity(value)) { base.WriteValue(value); return; }
            // The old R format first tries 15 digits with the OLD parser. A
            // modern round-trip test would make different choices at subnormals.
            string number = FormatRoundTrip(value, CultureInfo.InvariantCulture);
            // Pinned JsonConvert.EnsureDecimalPlace keeps a float token as 0.0,
            // while exponent forms already distinguish it from an integer.
            if (number.IndexOf('.') < 0 && number.IndexOf('E') < 0) number += ".0";
            WriteRawValue(number);
        }
    }

    private sealed class MetadataReader : JsonTextReader {
        private readonly string source;
        private int mappedLine = 1;
        private int lineStart;

        internal MetadataReader(string metadata) : base(new StringReader(metadata)) { source = metadata; }

        public override bool Read() {
            bool read = base.Read();
            if (!read || TokenType != JsonToken.Float || Value is not double number) return read;
            // Named nonfinite tokens remain the original reader's values. Every
            // numeric Float lexeme uses the old conversion, including finite edges.
            if (double.IsNaN(number)) return read;
            int end = TokenEnd();
            if (double.IsInfinity(number) && source[end - 1] == 'y') return read;
            int start = end;
            while (start > 0 && IsNumberCharacter(source[start - 1])) start--;
            string literal = source.Substring(start, end - start);
            if (!LegacyPackMetadataNumberParser.TryParseDouble(literal, out double value)) {
                string message = string.Format(CultureInfo.InvariantCulture,
                    "Input string '{0}' is not a valid number. Path '{1}', line {2}, position {3}.",
                    literal, Path, LineNumber, LinePosition);
                throw new JsonReaderException(message, Path, LineNumber, LinePosition, null);
            }
            // Do not advance the reader's array index a second time.
            SetToken(JsonToken.Float, value == 0 ? 0d : value, false);
            return read;
        }

        private int TokenEnd() {
            // JsonTextReader counts CR, LF and CRLF as one line; its position is
            // a UTF16 offset after the token, independent of internal buffer shifts.
            while (mappedLine < LineNumber) {
                char value = source[lineStart++];
                if (value == '\r') {
                    if (lineStart < source.Length && source[lineStart] == '\n') lineStart++;
                    mappedLine++;
                } else if (value == '\n') mappedLine++;
            }
            return lineStart + LinePosition;
        }

        private static bool IsNumberCharacter(char value) =>
            value is >= '0' and <= '9' or '+' or '-' or '.' or 'e' or 'E';
    }
#endif
}
