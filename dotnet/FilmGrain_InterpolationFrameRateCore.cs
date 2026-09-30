using System;
using System.Globalization;

namespace FilmGrainStudioPreview
{
    internal sealed class InterpolationFrameRate
    {
        internal long Numerator;
        internal long Denominator;
        internal double Fps { get { return (double)Numerator / Denominator; } }
        internal string Rational { get { return Numerator.ToString(CultureInfo.InvariantCulture) + "/" + Denominator.ToString(CultureInfo.InvariantCulture); } }
        internal string Label { get { return Fps.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', '_'); } }
    }

    internal static class InterpolationFrameRateCore
    {
        internal static bool TryParse(string text, string sourceRate, out InterpolationFrameRate result, out string error)
        {
            result = null; error = "";
            string value = (text ?? "").Trim().ToLowerInvariant().Replace(" ", "");
            if (value.Length == 0) { error = "Enter an output frame rate, for example 60, 60000/1001, 2x, or 1.5x."; return false; }
            bool multiplier = value.EndsWith("x", StringComparison.Ordinal);
            long n, d;
            if (multiplier)
            {
                long factorN, factorD, sourceN, sourceD;
                if (!TryRational(value.Substring(0, value.Length - 1), out factorN, out factorD) || factorN <= 0 ||
                    !TryRational(sourceRate, out sourceN, out sourceD) || sourceN <= 0)
                { error = "A frame-rate multiplier needs a valid source frame rate and a positive multiplier (for example 2x)."; return false; }
                try { checked { n = factorN * sourceN; d = factorD * sourceD; } }
                catch (OverflowException) { error = "The requested frame rate is too large."; return false; }
            }
            else if (!TryRational(value, out n, out d))
            { error = "Invalid frame rate. Use a positive number, a fraction such as 60000/1001, or a source multiplier such as 1.5x."; return false; }
            if (n <= 0 || d <= 0 || (double)n / d < 1.0 || (double)n / d > 1000.0)
            { error = "Output frame rate must be between 1 and 1000 fps."; return false; }
            long gcd = Gcd(n, d); n /= gcd; d /= gcd;
            result = new InterpolationFrameRate(); result.Numerator = n; result.Denominator = d;
            return true;
        }

        private static bool TryRational(string value, out long numerator, out long denominator)
        {
            numerator = 0; denominator = 1;
            string[] parts = (value ?? "").Split('/');
            if (parts.Length == 2)
            {
                return long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out numerator) &&
                    long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out denominator) && denominator != 0;
            }
            if (parts.Length != 1 || string.IsNullOrWhiteSpace(value)) return false;
            decimal parsed;
            if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out parsed)) return false;
            if (parsed > long.MaxValue / 1000000m || parsed < long.MinValue / 1000000m) return false;
            decimal scaled = parsed * 1000000m;
            if (scaled > long.MaxValue || scaled < long.MinValue) return false;
            numerator = (long)decimal.Round(scaled, 0, MidpointRounding.AwayFromZero);
            denominator = 1000000;
            long gcd = Gcd(numerator, denominator); numerator /= gcd; denominator /= gcd;
            return true;
        }

        private static long Gcd(long a, long b)
        {
            if (a == long.MinValue) return 1;
            a = Math.Abs(a); b = Math.Abs(b);
            while (b != 0) { long t = a % b; a = b; b = t; }
            return a == 0 ? 1 : a;
        }
    }
}
