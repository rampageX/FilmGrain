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

    // Shared by actual preview and both AV1 metadata output routes. No pixel noise approximation.
    internal static class Av1GrainStrengthCore
    {
        internal static int Read(IDictionary<string,string> state)
        {
            string value; int percent;
            if (state == null || !state.TryGetValue("FG_AV1_GRAIN_STRENGTH", out value) || string.IsNullOrEmpty(value)) return 100;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out percent) || percent < 1 || percent > 100)
                throw new ArgumentException("AV1 grain strength must be 1..100 percent; use Enable grain to disable grain.");
            return percent;
        }

        internal static string ScaleTable(string text, int percent)
        {
            if (percent < 1 || percent > 100) throw new ArgumentOutOfRangeException("percent");
            if (percent == 100) return text; // Preserve the baseline byte-for-byte.
            if (string.IsNullOrEmpty(text) || !text.TrimStart('\ufeff').StartsWith("filmgrn1", StringComparison.Ordinal))
                throw new InvalidDataException("Unsupported grain table header.");
            int curves = 0, events = 0;
            string result = Regex.Replace(text, @"(?m)^([^\S\r\n]*)(sY|sCb|sCr)([^\r\n]*)", delegate(Match match)
            {
                string[] tokens = match.Groups[3].Value.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                int count;
                if (tokens.Length == 0 || !int.TryParse(tokens[0], out count) || count < 0 || count > 14 || tokens.Length != 1 + count * 2)
                    throw new InvalidDataException("Invalid grain scaling curve: " + match.Groups[2].Value);
                var line = new System.Text.StringBuilder(match.Groups[1].Value + match.Groups[2].Value + " " + count.ToString(CultureInfo.InvariantCulture));
                int previous = -1;
                for (int i = 0; i < count; i++)
                {
                    int x, y;
                    if (!int.TryParse(tokens[1+i*2], out x) || !int.TryParse(tokens[2+i*2], out y) || x < 0 || x > 255 || y < 0 || y > 255 || x <= previous)
                        throw new InvalidDataException("Invalid grain scaling point.");
                    previous = x;
                    line.Append(" " + x.ToString(CultureInfo.InvariantCulture) + " " + ((y * percent + 50) / 100).ToString(CultureInfo.InvariantCulture));
                }
                curves++; return line.ToString();
            });
            events = Regex.Matches(text, @"(?m)^E\s").Count;
            if (events == 0 || curves == 0) throw new InvalidDataException("No grain events/scaling curves found.");
            // E (time/seed/enabled/update), p and AR coefficients are deliberately untouched.
            return NormalizeInactiveChromaCoefficients(result);
        }

        // inspect can emit one zero for a disabled chroma plane, while older
        // grav1synth table parsers require 2*lag*(lag+1)+1 coefficients even then.
        // Only pad an explicitly inactive plane with zeroes; active AR is untouched.
        internal static string NormalizeInactiveChromaCoefficients(string text)
        {
            return Regex.Replace(text,@"(?ms)^E[^\r\n]*(?:\r?\n)(?:(?!^E[ \t]).)*",delegate(Match block){
                string body=block.Value;
                Match p=Regex.Match(body,@"(?m)^[ \t]*p[ \t]+([^\r\n]+)");
                if(!p.Success)return body;
                string[] fields=p.Groups[1].Value.Split((char[])null,StringSplitOptions.RemoveEmptyEntries);
                int lag,fromLuma;
                if(fields.Length!=12||!int.TryParse(fields[0],out lag)||lag<1||lag>3||!int.TryParse(fields[4],out fromLuma)||fromLuma!=0)return body;
                int expected=2*lag*(lag+1)+1;
                foreach(string plane in new[]{"Cb","Cr"}) {
                    if(!Regex.IsMatch(body,@"(?m)^[ \t]*s"+plane+@"[ \t]+0[ \t]*\r?$"))continue;
                    body=Regex.Replace(body,@"(?m)^([ \t]*c"+plane+@")[ \t]+0[ \t]*(?=\r?$)",delegate(Match coefficients){
                        return coefficients.Groups[1].Value+" "+new string('0',expected).Replace("0","0 ").TrimEnd();
                    });
                }
                return body;
            });
        }

        internal static string ShiftTable(string text, double seconds)
        {
            if (seconds <= 0.0000001) return text;
            ulong offset = checked((ulong)Math.Round(seconds * 10000000.0));
            MatchCollection events = Regex.Matches(text, @"(?m)^E[^\r\n]*");
            if (events.Count == 0 || !text.TrimStart('\ufeff').StartsWith("filmgrn1", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid grain table for preview.");
            var output = new System.Text.StringBuilder("filmgrn1\n");
            string lastModel = null;
            for (int i=0;i<events.Count;i++)
            {
                Match match=events[i];
                string[] fields=match.Value.Split((char[])null,StringSplitOptions.RemoveEmptyEntries);
                ulong begin,end; int enabled,update;
                if(fields.Length!=6 || !ulong.TryParse(fields[1],NumberStyles.None,CultureInfo.InvariantCulture,out begin)||!ulong.TryParse(fields[2],NumberStyles.None,CultureInfo.InvariantCulture,out end)||end<begin||
                    !int.TryParse(fields[3],out enabled)||!int.TryParse(fields[5],out update))throw new InvalidDataException("Invalid grain event.");
                string body=text.Substring(match.Index+match.Length,(i+1<events.Count?events[i+1].Index:text.Length)-match.Index-match.Length);
                if(enabled==1 && update==1)lastModel=body;
                // Empty intervals have no displayed frames, but an updated model can
                // still be inherited by a later nonempty event. Keep lastModel above.
                if(end<=offset || end==begin)continue;
                if(enabled==1 && update==0)
                {
                    if(lastModel==null)throw new InvalidDataException("Preview cannot resolve inherited grain model.");
                    body=lastModel;fields[5]="1";
                }
                fields[1]=(begin>offset?begin-offset:0UL).ToString(CultureInfo.InvariantCulture);
                fields[2]=(end-offset).ToString(CultureInfo.InvariantCulture);
                output.Append(string.Join(" ",fields));output.Append(body);
            }
            if(output.ToString()=="filmgrn1\n")throw new InvalidDataException("Selected grain table does not cover the preview start time.");
            return output.ToString();
        }

        internal static string Quote(string value)
        {
            var result = new System.Text.StringBuilder("\""); int slashes = 0;
            foreach (char c in value) { if (c == '\\') { slashes++; continue; } if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append(c); slashes = 0; continue; } result.Append('\\', slashes); slashes = 0; result.Append(c); }
            result.Append('\\', slashes * 2); result.Append('"'); return result.ToString();
        }
        private static string Command(params string[] args)
        { var quoted = new List<string>(); foreach (string arg in args) quoted.Add(Quote(arg)); return string.Join(" ", quoted.ToArray()); }

        // External tables already contain the model: scale text before a single apply.
        internal static void AttachTableScale(BridgeExecutionStage stage, List<string> arguments, string sourceTable, string scaledTable, int percent, IList<string> temporary)
        {
            if(percent==100)return;
            int index=arguments.IndexOf("--grain");if(index<0)index=arguments.IndexOf("-g");
            if(index<0 || index+1>=arguments.Count)throw new InvalidDataException("External grain table argument missing.");
            temporary.Add(scaledTable);
            stage.BeforeRunPreparation=delegate {
                string scaled=ScaleTable(File.ReadAllText(sourceTable),percent);
                File.WriteAllText(scaledTable,scaled,new System.Text.UTF8Encoding(false));
                arguments[index+1]=scaledTable;
                stage.DirectArguments=Command(arguments.ToArray());
                return "[Grain strength] external table scaled before single apply; strength="+percent.ToString(CultureInfo.InvariantCulture)+"%; no model inspect or second video rewrite; source table unchanged.";
            };
        }

        // Original built-in models from rampageX/grav1synth v0.2.2, not inspect output.
        // Keep complete event timelines, including the last open-ended event.
        internal static string PresetTable(string name)
        {
            string encoded;
            switch((name??"").ToLowerInvariant())
            {
                case "16mm": encoded=
                    "H4sIAAAAAAACA+1cSY4kyZE8d7/CP5CAmdp+JvgIXmcAEgQ4g0HP/wGabra7R1RF9oUooplLRKS5l7u4qKioqv39n//6n3/88b/2979e5vIG/3fZy5oS4mV/" +
                    "/+3/Lnel+o61F70O+bIFLsB3+8+///b/f7usv+pnMlzW4VdIV7FX8PVz/oqpvh2uXFdIdR3nL/D1gz5fAO6y0V5g6idzpvfB1M+FUN+rv0Pkz0L9vb5Wv9ej" +
                    "/eW/6iniCQc8Wv1aj1bfrEeLePLpSo6Okuqi9f/J0prRXWADfhBX8gkX+kMWotP2dNrB4EIp41nnug7YKyc62cLnWhKtY42jhXL+/bf//tv1hZfoCw94fdXV" +
                    "8HeLP8Xry2X+kb64+k7AUzL4I65fX41XcHUV/HfhKvjvxz+s54N/5PD6f/n6RqHl6DCG/izj/am/erz+X87gP/ILLK71x2Xlj8PFtw/ot/qHDn/H4+ILuR6v" +
                    "riuHAzoW1GuA/wSouBBUQEOH+SlkFLzx+LVeYmuAMFEPipgoigm+zFBPjzBRLw3ePsIFYSMwNuRGWl8YIy4xNlwasZHolia6pQkPlyzdzFToKPgt8m917QoY" +
                    "XCoArRRgxIajJQBRkf1V6n+BFipAp42oQHBkPikb+aQEHfX7AA8geDiGhxEIFL4VCpl61glBgu8B32dfQanw4I9VwOj9kntawfuV8dUOj3q+8UJYIDxwtfp+" +
                    "vdsAJ3gIPEt9ydEvdLr1bOsZR3z1csQMhBafBByKihQ/Q0emJznTk1zoSbYVzEQYES85GH4Sod7zO/5g3siCCcOfwe+EDfPMG455IzNvBOaNwISRGBu8ki8z" +
                    "b+ASRHTZEGMQzSFakTf4ZBHf+IrlcypMQCUMwHAjbxADhM4b9Hu9vfVSJQGNPKZh4A174g0z8wa/Uv+s8kaceQMW3oAFGMQboMcRaGThDUKMYd7As8V4opDI" +
                    "5kNoFIJGYeIwjI3M/AGEDZvo5oF3go0iPCLYsHw7bRGMRMVIEWyUERt0OE+Hq2Cqf5P5OUdsdN7IfE8rOnHJJLwRRmxkoZ5ACIip8YZnVDCCS6L1iC+IN6zw" +
                    "hpnhkRtv0BdPN1bhAQoPN8Fj542gvEHwqI+1t0IbMNJGYtpwJ9oQPFpHP8lvkUBkrg5eRGJibFg8UL1J3uNJVmgoJAp8Z0wRaBDX2yCYcIoJL3wRhS+ixBbF" +
                    "hBFMOOENEGwAY6OsISXiYSoxUkgR2uA1E0uWwDcy2LtYQjEQpQVigkNgsQIG4LOyzHQlMmPEO8ZQSJwYA3osEcqAl7HE9ljSKcPzKxJLKqWYkTI4hnmRGgqv" +
                    "gTGsMkYaGQODSRHGUDiUm2Bi3oSFz3Sfcqf3eoMwluDanq4qXnP6Y7rarDEKS09EBCo0FqDIFfUO4Le6VMwDUwBdifq1HqZejrqyEJDjdSsScVnHCHNMRMBa" +
                    "BdyACiBU4GPm8RmpF8gzKnyk9TxjtgIK1wusggIv5PMmL+BME3bAhJ/1haOo9iKMHDERJkwIUbgBE4O+6PLTX3I6LD9r1FCFwSBVhREQFAoG+DSOEJtlutYF" +
                    "ODUJnJoklhjAEgOvs8pRCh9ewoc8lDXkzfJTUhN3m5qQaky+hZHE3zB+MDgw68Frbg/yc0xNVGgUpgoRoYRh2zVGPalNY9hNfDZ0QHtR0BEkT7lVnw0dLWgw" +
                    "OuKiPgkdKjLqI0LouBEZxB95YYy6YKWnRX5mBgcyRgfFBwLUETqI2zNxe8kcSTg7yQoHt0UQhoMoDC/CX9WpkwTAuREWke5mnEMIKk+JJIlBkhgkiRPXdIwk" +
                    "uNAcTwqrijmeECo4qhVmsXLLGeYhjthXcWRExS1neIkjvnPGKY7snOF26ZkbZyyBpKMh+z+NNCQvMawzIMidt4ISEJQIaQQvKBGdIXHAQrwjDUd5CS+foOUl" +
                    