using System;
using System.Globalization;

namespace FilmGrainStudioPreview
{
    // Phase 3.7.1: pure counterpart of the previously verified MainForm bitrate formulas.
    internal static class BitrateRecommendationCore
    {
        internal static double ParseMediaFps(MediaProbeInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.AvgFrameRate)) return 0.0;
            string[] parts = info.AvgFrameRate.Split('/');
            double numerator;
            double denominator;
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out numerator) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out denominator) &&
                denominator != 0.0)
                return numerator / denominator;
            double fps;
            return double.TryParse(info.AvgFrameRate, NumberStyles.Float, CultureInfo.InvariantCulture, out fps) ? fps : 0.0;
        }

        internal static double GetRecommendedOutputFps(MediaProbeInfo info, bool interpolate, bool deinterlaceAuto, bool keepSourceFps)
        {
            if (interpolate) return 60.0;
            double sourceFps = ParseMediaFps(info);
            if (sourceFps <= 0.0) return 60.0;
            if (deinterlaceAuto && info != null && info.IsInterlaced) return sourceFps * 2.0;
            if (keepSourceFps) return sourceFps;

            double[] family23976 = new double[] { 23.976023976, 29.970029970, 47.952047952, 59.940059940, 119.880119880 };
            double[] family24 = new double[] { 24.0, 25.0, 30.0, 48.0, 50.0, 60.0, 100.0, 120.0 };
            double best = double.MaxValue;
            bool use23976 = false;
            foreach (double target in family23976)
            {
                double d = Math.Abs(sourceFps - target);
                if (d < best) { best = d; use23976 = true; }
            }
            foreach (double target in family24)
            {
                double d = Math.Abs(sourceFps - target);
                if (d < best) { best = d; use23976 = false; }
            }
            if (best <= 0.25) return use23976 ? (24000.0 / 1001.0) : 24.0;
            return sourceFps;
        }

        private static double GetBitrateFpsFactor(double fps)
        {
            if (fps <= 0.0) return 1.0;
            double[,] points = new double[,] {
                { 24.0, 0.60 },
                { 25.0, 0.62 },
                { 30.0, 0.70 },
                { 50.0, 0.90 },
                { 60.0, 1.00 },
                { 120.0, 1.65 }
            };
            if (fps <= 24.0) return Math.Max(0.40, 0.60 * (fps / 24.0));
            for (int i = 1; i < points.GetLength(0); i++)
            {
                double x1 = points[i - 1, 0];
                double y1 = points[i - 1, 1];
                double x2 = points[i, 0];
                double y2 = points[i, 1];
                if (fps <= x2)
                {
                    double t = (fps - x1) / (x2 - x1);
                    return y1 + ((y2 - y1) * t);
                }
            }
            return 1.65 * Math.Pow(fps / 120.0, 0.75);
        }

        internal static int GetRecommendedBitrate(int codecIndex, int width, int height, double fps, bool highMotion)
        {
            if (width <= 0) width = 1920;
            if (height <= 0) height = 1080;
            if (fps <= 0.0) fps = 60.0;
            int longEdge = Math.Max(width, height);
            int tier = longEdge <= 1280 ? 0 : (longEdge <= 1920 ? 1 : (longEdge <= 2560 ? 2 : 3));
            int[,] normal = new int[,] {
                { 3500, 5000, 7000, 10000 },
                { 4000, 6000, 8000, 12000 },
                { 5000, 7500, 10000, 15000 }
            };
            int[,] motion = new int[,] {
                { 6000, 9000, 12000, 18000 },
                { 7000, 11000, 15000, 22000 },
                { 10000, 15000, 20000, 30000 }
            };
            codecIndex = Math.Max(0, Math.Min(2, codecIndex));
            double baseRate = highMotion ? motion[codecIndex, tier] : normal[codecIndex, tier];
            double raw = baseRate * GetBitrateFpsFactor(fps);
            int rate = (int)(Math.Floor((raw + 250.0) / 500.0) * 500.0);
            return Math.Max(1000, rate);
        }

    }
}
