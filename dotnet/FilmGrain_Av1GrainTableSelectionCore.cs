using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace FilmGrainStudioPreview
{
    // Phase 3.7.2: original AV1 Grain Table tier matching and sort rules, without UI state.
    internal static class Av1GrainTableSelectionCore
    {
        internal static string GetAv1GrainTierFromDimensions(int width, int height)
        {
            if (width <= 0) return "";
            if (width <= 1280) return "720p";
            if (width <= 1920) return "1080p";
            if (width <= 2560) return "1440p";
            return "2160p";
        }

        private static string GetAv1GrainTableTier(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            Match folderMatch = Regex.Match(path, @"[\\/](720p|1080p|1440p|2160p)([\\/]|$)", RegexOptions.IgnoreCase);
            if (folderMatch.Success) return folderMatch.Groups[1].Value.ToLowerInvariant();

            string stem = Path.GetFileNameWithoutExtension(path) ?? "";
            Match sizeMatch = Regex.Match(stem, @"([0-9]{3,4})x([0-9]{3,4})", RegexOptions.IgnoreCase);
            if (sizeMatch.Success)
            {
                int width;
                int height;
                if (int.TryParse(sizeMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out width) &&
                    int.TryParse(sizeMatch.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out height))
                    return GetAv1GrainTierFromDimensions(width, height);
            }

            Match tierMatch = Regex.Match(stem, @"(^|[_-])(720p|1080p|1440p|2160p)([_-]|$)", RegexOptions.IgnoreCase);
            return tierMatch.Success ? tierMatch.Groups[2].Value.ToLowerInvariant() : "";
        }

        private static void GetAv1GrainTableDimensions(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            string stem = string.IsNullOrEmpty(path) ? "" : (Path.GetFileNameWithoutExtension(path) ?? "");
            Match sizeMatch = Regex.Match(stem, @"([0-9]{3,4})x([0-9]{3,4})", RegexOptions.IgnoreCase);
            if (!sizeMatch.Success) return;
            int.TryParse(sizeMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out width);
            int.TryParse(sizeMatch.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
        }

        private static int GetAv1GrainTableSortDistance(string path, int sourceWidth, int sourceHeight)
        {
            int width;
            int height;
            GetAv1GrainTableDimensions(path, out width, out height);
            if (width > 0 && height > 0 && sourceWidth > 0 && sourceHeight > 0)
            {
                long distance = Math.Abs((long)width - sourceWidth) + Math.Abs((long)height - sourceHeight);
                return distance > int.MaxValue ? int.MaxValue : (int)distance;
            }
            return int.MaxValue;
        }

        internal static List<string> GetFilteredAv1GrainTables(IEnumerable<string> tablePaths, int sourceWidth, int sourceHeight, bool showAllAv1GrainTables)
        {
            List<string> files = new List<string>(tablePaths);
            string preferredTier = GetAv1GrainTierFromDimensions(sourceWidth, sourceHeight);

            if (!showAllAv1GrainTables)
            {
                if (string.IsNullOrEmpty(preferredTier)) files.Clear();
                else files.RemoveAll(delegate(string path) { return !string.Equals(GetAv1GrainTableTier(path), preferredTier, StringComparison.OrdinalIgnoreCase); });
            }

            if (sourceWidth > 0 && sourceHeight > 0)
            {
                Dictionary<string, int> tierOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                tierOrder["720p"] = 0; tierOrder["1080p"] = 1; tierOrder["1440p"] = 2; tierOrder["2160p"] = 3; tierOrder[""] = 9;
                int preferredOrder;
                if (!tierOrder.TryGetValue(preferredTier, out preferredOrder)) preferredOrder = 9;
                files.Sort(delegate(string a, string b)
                {
                    if (showAllAv1GrainTables)
                    {
                        int ao;
                        int bo;
                        if (!tierOrder.TryGetValue(GetAv1GrainTableTier(a), out ao)) ao = 9;
                        if (!tierOrder.TryGetValue(GetAv1GrainTableTier(b), out bo)) bo = 9;
                        int tierCompare = Math.Abs(ao - preferredOrder).CompareTo(Math.Abs(bo - preferredOrder));
                        if (tierCompare != 0) return tierCompare;
                    }
                    int distanceCompare = GetAv1GrainTableSortDistance(a, sourceWidth, sourceHeight).CompareTo(GetAv1GrainTableSortDistance(b, sourceWidth, sourceHeight));
                    if (distanceCompare != 0) return distanceCompare;
                    return StringComparer.OrdinalIgnoreCase.Compare(a, b);
                });
            }
            else files.Sort(StringComparer.OrdinalIgnoreCase);

            return files;
        }

    }
}
