// Adapted from .NET CoreCLR at abbb8f685929c7aeaa087dae46fedc1bc2af4b17:
// src/mscorlib/src/System/Number.cs (ParseNumber, NumberMaxDigits, IsWhite),
// src/classlibnative/bcltype/number.cpp (DigitsToInt, Mul64Lossy, power tables,
// NumberToDouble and NumberBufferToDouble).
// https://github.com/dotnet/coreclr/tree/abbb8f685929c7aeaa087dae46fedc1bc2af4b17
// This is a managed port of that same-origin algorithm, not a claim that the
// CoreCLR source is the original Framework implementation or its test oracle.
//
// The MIT License (MIT)
//
// Copyright (c) .NET Foundation and Contributors
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;

namespace PCL.Living.Features;

/// <summary>
/// The old decimal-to-double conversion used only for LF pack metadata.
/// JSON tokenization, integer tokens and explicit NaN/Infinity remain with
/// Newtonsoft. This class does not format doubles or replace a general parser.
/// </summary>
internal static class LegacyPackMetadataNumberParser {
    private const int MaximumDigits = 50;
    private const ulong SignMask = 0x8000000000000000UL;
    private const ulong InfinityBits = 0x7FF0000000000000UL;

    // Normalized mantissas: 10^1..10^15, followed by 10^-1..10^-15.
    private static readonly ulong[] Powers10 = {
        0xA000000000000000UL, 0xC800000000000000UL, 0xFA00000000000000UL,
        0x9C40000000000000UL, 0xC350000000000000UL, 0xF424000000000000UL,
        0x9896800000000000UL, 0xBEBC200000000000UL, 0xEE6B280000000000UL,
        0x9502F90000000000UL, 0xBA43B74000000000UL, 0xE8D4A51000000000UL,
        0x9184E72A00000000UL, 0xB5E620F480000000UL, 0xE35FA931A0000000UL,
        0xCCCCCCCCCCCCCCCDUL, 0xA3D70A3D70A3D70BUL, 0x83126E978D4FDF3CUL,
        0xD1B71758E219652EUL, 0xA7C5AC471B478425UL, 0x8637BD05AF6C69B7UL,
        0xD6BF94D5E57A42BEUL, 0xABCC77118461CEFFUL, 0x89705F4136B4A599UL,
        0xDBE6FECEBDEDD5C2UL, 0xAFEBFF0BCB24AB02UL, 0x8CBCCC096F5088CFUL,
        0xE12E13424BB40E18UL, 0xB424DC35095CD813UL, 0x901D7CF73AB0ACDCUL
    };

    private static readonly int[] Power10Exponents = {
        4, 7, 10, 14, 17, 20, 24, 27, 30, 34, 37, 40, 44, 47, 50
    };

    // Normalized mantissas: 10^16..10^336, then 10^-16..10^-336.
    private static readonly ulong[] Powers10By16 = {
        0x8E1BC9BF04000000UL, 0x9DC5ADA82B70B59EUL, 0xAF298D050E4395D6UL,
        0xC2781F49FFCFA6D4UL, 0xD7E77A8F87DAF7FAUL, 0xEFB3AB16C59B14A0UL,
        0x850FADC09923329CUL, 0x93BA47C980E98CDEUL, 0xA402B9C5A8D3A6E6UL,
        0xB616A12B7FE617A8UL, 0xCA28A291859BBF90UL, 0xE070F78D39275566UL,
        0xF92E0C3537826140UL, 0x8A5296FFE33CC92CUL, 0x9991A6F3D6BF1762UL,
        0xAA7EEBFB9DF9DE8AUL, 0xBD49D14AA79DBC7EUL, 0xD226FC195C6A2F88UL,
        0xE950DF20247C83F8UL, 0x81842F29F2CCE373UL, 0x8FCAC257558EE4E2UL,
        0xE69594BEC44DE160UL, 0xCFB11EAD453994C3UL, 0xBB127C53B17EC165UL,
        0xA87FEA27A539E9B3UL, 0x97C560BA6B0919B5UL, 0x88B402F7FD7553ABUL,
        0xF64335BCF065D3A0UL, 0xDDD0467C64BCE4C4UL, 0xC7CABA6E7C5382EDUL,
        0xB3F4E093DB73A0B7UL, 0xA21727DB38CB0053UL, 0x91FF83775423CC29UL,
        0x8380DEA93DA4BC82UL, 0xECE53CEC4A314F00UL, 0xD5605FCDCF32E217UL,
        0xC0314325637A1978UL, 0xAD1C8EAB5EE43BA2UL, 0x9BECCE62836AC5B0UL,
        0x8C71DCD9BA0B495CUL, 0xFD00B89747823938UL, 0xE3E27A444D8D991AUL
    };

    private static readonly int[] Power10By16Exponents = {
        54, 107, 160, 213, 266, 319, 373, 426, 479, 532, 585,
        638, 691, 745, 798, 851, 904, 957, 1010, 1064, 1117
    };

    /// <summary>
    /// Parses the invariant NumberStyles.Float decimal subset using the old
    /// number buffer and integer conversion. Overflow returns false; underflow
    /// and negative zero return positive zero, as NumberBufferToDouble did.
    /// Named nonfinite literals, hexadecimal and culture-specific syntax are
    /// deliberately outside this entry point.
    /// </summary>
    internal static bool TryParseDouble(string? text, out double value) {
        value = 0;
        if (text is null || !TryReadNumber(text, out char[] digits, out int count,
                out int scale, out bool negative)) return false;

        return TryConvert(digits, count, scale, negative, out value);
    }

    // FormatDouble(R) called NumberToDouble directly on the 15-digit _ecvt
    // buffer BEFORE FormatGeneral removed trailing zeroes. Re-parsing the
    // displayed G15 text would remove them and change the lossy conversion path.
    internal static bool TryParseRoundTripCandidate(string text, out double value) {
        value = 0;
        if (!TryReadNumber(text, out char[] digits, out int count, out int scale, out bool negative) || count > 15) return false;
        for (int index = count; index < 15; index++) digits[index] = '0';
        return TryConvert(digits, 15, scale, negative, out value);
    }

    private static bool TryConvert(char[] digits, int count, int scale, bool negative, out double value) {
        value = 0;
        ulong bits = NumberToDoubleBits(digits, count, scale, negative);
        if ((bits & InfinityBits) == InfinityBits) return false;
        // The native NumberBufferToDouble clears the sign of every zero.
        if ((bits & ~SignMask) == 0) bits = 0;
        value = BitConverter.Int64BitsToDouble(unchecked((long)bits));
        return true;
    }

    private static bool TryReadNumber(string text, out char[] digits, out int count,
            out int scale, out bool negative) {
        digits = new char[MaximumDigits];
        count = 0;
        scale = 0;
        negative = false;
        int position = 0;
        while (position < text.Length && IsWhite(text[position])) position++;
        if (position < text.Length && (text[position] == '+' || text[position] == '-')) {
            negative = text[position] == '-';
            position++;
        }

        bool anyDigit = false;
        bool nonZero = false;
        bool decimalPoint = false;
        int storedDigits = 0;
        while (position < text.Length) {
            char digit = text[position];
            if (IsDigit(digit)) {
                anyDigit = true;
                if (digit != '0' || nonZero) {
                    if (storedDigits < MaximumDigits) {
                        digits[storedDigits++] = digit;
                        // As parseDecimal=false, remove trailing zero digits
                        // from the fixed buffer without changing the scale.
                        if (digit != '0') count = storedDigits;
                    }
                    if (!decimalPoint) scale++;
                    nonZero = true;
                } else if (decimalPoint) {
                    scale--;
                }
            } else if (digit == '.' && !decimalPoint) {
                decimalPoint = true;
            } else {
                break;
            }
            position++;
        }
        if (!anyDigit) return false;

        if (position < text.Length && (text[position] == 'e' || text[position] == 'E')) {
            position++;
            bool negativeExponent = false;
            if (position < text.Length && (text[position] == '+' || text[position] == '-')) {
                negativeExponent = text[position] == '-';
                position++;
            }
            if (position == text.Length || !IsDigit(text[position])) return false;
            int exponent = 0;
            do {
                exponent = exponent * 10 + text[position++] - '0';
                // The source parser saturates as soon as the accumulated
                // exponent exceeds 1000, not merely on Int32 overflow.
                if (exponent > 1000) {
                    exponent = 9999;
                    while (position < text.Length && IsDigit(text[position])) position++;
                    break;
                }
            } while (position < text.Length && IsDigit(text[position]));
            scale += negativeExponent ? -exponent : exponent;
        }

        while (position < text.Length && IsWhite(text[position])) position++;
        // Preserve TryStringToNumber's allowance for terminating NULs. These
        // do not add whitespace or JSON syntax to the actual reader's lexeme.
        while (position < text.Length && text[position] == '\0') position++;
        if (position != text.Length) return false;
        if (!nonZero) {
            scale = 0;
            if (!decimalPoint) negative = false;
        }
        return true;
    }

    private static bool IsWhite(char value) => value == ' ' || (value >= '\t' && value <= '\r');

    private static bool IsDigit(char value) => value >= '0' && value <= '9';

    private static uint DigitsToInt(char[] digits, int start, int count) {
        uint value = (uint)(digits[start] - '0');
        for (int i = start + 1; i < start + count; i++)
            value = 10 * value + (uint)(digits[i] - '0');
        return value;
    }

    private static ulong Multiply32(ulong left, ulong right) => unchecked((ulong)(uint)left * (uint)right);

    private static ulong MultiplyLossy(ulong left, ulong right, ref int exponent) {
        // Retain the original deliberate low-bit loss: replacing this with an
        // exact product changes historical finite parse results at boundaries.
        unchecked {
            ulong value = Multiply32(left >> 32, right >> 32)
                + (Multiply32(left >> 32, right) >> 32)
                + (Multiply32(left, right >> 32) >> 32);
            if ((value & SignMask) == 0) {
                value <<= 1;
                exponent--;
            }
            return value;
        }
    }

    private static ulong NumberToDoubleBits(char[] digits, int count, int scale, bool negative) {
        unchecked {
            int start = 0;
            int remaining = count;
            while (remaining > 0 && digits[start] == '0') {
                remaining--;
                start++;
            }
            ulong sign = negative ? SignMask : 0;
            if (remaining == 0) return sign;

            int taken = Math.Min(remaining, 9);
            remaining -= taken;
            ulong value = DigitsToInt(digits, start, taken);
            if (remaining > 0) {
                taken = Math.Min(remaining, 9);
                remaining -= taken;
                uint multiplier = (uint)(Powers10[taken - 1] >> (64 - Power10Exponents[taken - 1]));
                value = Multiply32(value, multiplier) + DigitsToInt(digits, start + 9, taken);
            }

            scale -= count - remaining;
            int absoluteScale = scale < 0 ? -scale : scale;
            if (absoluteScale >= 22 * 16) return sign | (scale > 0 ? InfinityBits : 0);

            int exponent = 64;
            if ((value & 0xFFFFFFFF00000000UL) == 0) { value <<= 32; exponent -= 32; }
            if ((value & 0xFFFF000000000000UL) == 0) { value <<= 16; exponent -= 16; }
            if ((value & 0xFF00000000000000UL) == 0) { value <<= 8; exponent -= 8; }
            if ((value & 0xF000000000000000UL) == 0) { value <<= 4; exponent -= 4; }
            if ((value & 0xC000000000000000UL) == 0) { value <<= 2; exponent -= 2; }
            if ((value & SignMask) == 0) { value <<= 1; exponent--; }

            int index = absoluteScale & 15;
            if (index != 0) {
                int multiplierExponent = Power10Exponents[index - 1];
                exponent += scale < 0 ? -multiplierExponent + 1 : multiplierExponent;
                ulong multiplier = Powers10[index + (scale < 0 ? 15 : 0) - 1];
                value = MultiplyLossy(value, multiplier, ref exponent);
            }
            index = absoluteScale >> 4;
            if (index != 0) {
                int multiplierExponent = Power10By16Exponents[index - 1];
                exponent += scale < 0 ? -multiplierExponent + 1 : multiplierExponent;
                ulong multiplier = Powers10By16[index + (scale < 0 ? 21 : 0) - 1];
                value = MultiplyLossy(value, multiplier, ref exponent);
            }

            if (((uint)value & (1U << 10)) != 0) {
                ulong rounded = value + ((1U << 10) - 1) + (((uint)value >> 11) & 1U);
                if (rounded < value) {
                    rounded = (rounded >> 1) | SignMask;
                    exponent++;
                }
                value = rounded;
            }
            exponent += 0x3FE;
            if (exponent <= 0) {
                if (exponent == -52 && value >= 0x8000000000000058UL) {
                    value = 1;
                } else if (exponent <= -52) {
                    value = 0;
                } else {
                    value >>= -exponent + 12;
                }
            } else if (exponent >= 0x7FF) {
                value = InfinityBits;
            } else {
                value = ((ulong)exponent << 52) + ((value >> 11) & 0x000FFFFFFFFFFFFFUL);
            }
            return value | sign;
        }
    }
}
