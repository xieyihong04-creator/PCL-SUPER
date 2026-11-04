using System;
using System.Globalization;
using System.Numerics;

namespace PCL.Living.Features;

/// <summary>有限 double 的旧 G15／G17 候选；R 的回读选择由调用方另行完成。</summary>
internal static class LegacyPackMetadataNumberFormatting {
    // Independent exact-rational reconstruction for the LF CLR4 observations.
    // Same-source semantic reference (MIT): dotnet/coreclr
    // abbb8f685929c7aeaa087dae46fedc1bc2af4b17, number.cpp RoundNumber /
    // FormatGeneral. Its DoubleToNumber delegates to CRT _ecvt: this is not a
    // port of that CRT, and unmeasured cases remain subject to the real LF oracle.
    internal static string Format(double value, int precision, CultureInfo culture) {
        if (precision != 15 && precision != 17) throw new ArgumentOutOfRangeException(nameof(precision));
        if (culture is null) throw new ArgumentNullException(nameof(culture));
        if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (value == 0) return "0";

        ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        bool negative = (bits & 0x8000000000000000UL) != 0;
        int binaryExponent = (int)((bits >> 52) & 0x7ff);
        ulong mantissa = bits & 0x000fffffffffffffUL;
        if (binaryExponent == 0) {
            binaryExponent = -1074;
        } else {
            mantissa |= 0x0010000000000000UL;
            binaryExponent -= 1075;
        }

        // Keep the IEEE value exact until the requested decimal digit is rounded.
        BigInteger numerator = new(mantissa);
        BigInteger denominator = BigInteger.One;
        if (binaryExponent >= 0) numerator <<= binaryExponent;
        else denominator <<= -binaryExponent;

        int exponent = DecimalExponent(numerator, denominator);
        int shift = precision - 1 - exponent;
        BigInteger dividend = numerator;
        BigInteger divisor = denominator;
        if (shift >= 0) dividend *= BigInteger.Pow(10, shift);
        else divisor *= BigInteger.Pow(10, -shift);

        BigInteger digits = BigInteger.DivRem(dividend, divisor, out BigInteger remainder);
        // The measured CLR4 midpoint rounds away from zero. The sign is applied
        // after rounding the magnitude, so the negative midpoint follows that rule too.
        if (remainder * 2 >= divisor) digits += BigInteger.One;
        if (digits >= BigInteger.Pow(10, precision)) {
            digits /= 10;
            exponent++;
        }

        string significant = digits.ToString(CultureInfo.InvariantCulture).TrimEnd('0');
        NumberFormatInfo format = culture.NumberFormat;
        string magnitude = General(significant, exponent, precision, format);
        return negative ? format.NegativeSign + magnitude : magnitude;
    }

    private static int DecimalExponent(BigInteger numerator, BigInteger denominator) {
        // Decimal digit counts give either the exponent or one above it. One
        // exact comparison resolves powers of ten without a floating Log10 boundary.
        int exponent = numerator.ToString(CultureInfo.InvariantCulture).Length - denominator.ToString(CultureInfo.InvariantCulture).Length;
        int comparison = exponent >= 0
            ? numerator.CompareTo(denominator * BigInteger.Pow(10, exponent))
            : (numerator * BigInteger.Pow(10, -exponent)).CompareTo(denominator);
        return comparison < 0 ? exponent - 1 : exponent;
    }

    private static string General(string digits, int exponent, int precision, NumberFormatInfo format) {
        // CLR4 G switches at scale > precision or scale < -3; exponent = scale - 1.
        if (exponent >= precision || exponent < -4) {
            string coefficient = digits.Length == 1 ? digits : digits.Substring(0, 1) + format.NumberDecimalSeparator + digits.Substring(1);
            string sign = exponent < 0 ? format.NegativeSign : format.PositiveSign;
            return coefficient + "E" + sign + Math.Abs(exponent).ToString("D2", CultureInfo.InvariantCulture);
        }
        if (exponent < 0)
            return "0" + format.NumberDecimalSeparator + new string('0', -exponent - 1) + digits;
        int integralDigits = exponent + 1;
        if (digits.Length <= integralDigits) return digits + new string('0', integralDigits - digits.Length);
        return digits.Substring(0, integralDigits) + format.NumberDecimalSeparator + digits.Substring(integralDigits);
    }
}
