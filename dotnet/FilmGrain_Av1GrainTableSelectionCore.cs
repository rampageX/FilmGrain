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
            long offset = checked((long)Math.Round(seconds * 10000000.0));
            MatchCollection events = Regex.Matches(text, @"(?m)^E[^\r\n]*");
            if (events.Count == 0 || !text.TrimStart('\ufeff').StartsWith("filmgrn1", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid grain table for preview.");
            var output = new System.Text.StringBuilder("filmgrn1\n");
            string lastModel = null;
            for (int i=0;i<events.Count;i++)
            {
                Match match=events[i];
                string[] fields=match.Value.Split((char[])null,StringSplitOptions.RemoveEmptyEntries);
                long begin,end; int enabled,update;
                if(fields.Length!=6 || !long.TryParse(fields[1],out begin)||!long.TryParse(fields[2],out end)||end<=begin||
                    !int.TryParse(fields[3],out enabled)||!int.TryParse(fields[5],out update))throw new InvalidDataException("Invalid grain event.");
                string body=text.Substring(match.Index+match.Length,(i+1<events.Count?events[i+1].Index:text.Length)-match.Index-match.Length);
                if(enabled==1 && update==1)lastModel=body;
                if(end<=offset)continue;
                if(enabled==1 && update==0)
                {
                    if(lastModel==null)throw new InvalidDataException("Preview cannot resolve inherited grain model.");
                    body=lastModel;fields[5]="1";
                }
                fields[1]=Math.Max(0,begin-offset).ToString(CultureInfo.InvariantCulture);
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
                    "vonOqn4pPfEcCOKIDpJE9XoEAhHqDJicjNhYg3UGMGvwUsUNrOE2RwPvZ2JoDNITcXyTmbBe9EwPIzzwDdNZw7Kl4ZulEXpqYmbW8CNriKXR8bFZGqZbGoSP" +
                    "BovyYd5aSAjXr+JqYLCt4ZSSEza8chEhmiS6qCANAhSJMiYLnXihkfyeqWEab0T6RpAozc1AIXoyNXzHmJgabiIOFaAS60SI9uzEvnA1NK6YR1fDvmSQ3dUI" +
                    "COIgEUHiSlkYBBM/OpNBwAyqo0nRsjJIFl+jAUMCzAcIoWe6BEYIPZGWmR/9LIaGeBpO8laQqGLENwgCDS/QAPE2QLwNmLwN4lW5vyw88kQeghTRH3DvbdhB" +
                    "cgBiuZjR1cBvqGkkcc0cYrKYom5OVeKeqkS5NT9AIWGnEIvmRhArk/BBHkzs+DgxyKZKgSIMYVYZBKykKqMpiu879L0UFvW5/TDCkCLIpAgqLZPwcBxassBD" +
                    "dSksulRtDScWqZB5FH9aEtDKJM+61DUjVCQHXmfzkkE2y5x1aS7N1iB0sNdS8g8IkGaKuskUXRLZkT6MJLICDzvTx+p9UcIbhqSljPC4EyCdPliAFOTeSkqr" +
                    "ADEED0EFfJC1uA0eieHB5nklb/JHNb4YMUjTQiaGQ31hoxs9MyYNd0caTgopxa+6Q2xR38opWx2FwqEwB9dRKDFWP5QT43oqiLHCUSqzuZHDYxllEB2NMQIy" +
                    "xmKH+nc0aekRZdMcnrTEbIee8li72KGSxx4ZAzVHQ0J9nr/FK/ezV06QsAyJtCSyUaBQJK4Ic+ckhBHOiWzZrfLI7hpK0qTCg8AQ6Pn25RBH3CQ1UIsWiia2" +
                    "2aB4MlYVR7plCvduqgKrC/qoM8oRFJO54bvh9ap4wphQU1T8Ltdc0MwagxChQAAXf2mMXxpj0xgKC/CfaozZ3MAkxYpNXhabXEuwsJVOWGNA1xZTljJpjNGb" +
                    "oGQo5palsEGuuMBkhRXC0S7fvS8YvS/Ts1jUGIWZgzmolFljxF1jZL5ND2b5rjHujNE4wiMIPPydWW7fNDnKg8nRUOFs/FZ4EC7YMOfLTUr0WHz1e/MGkUcR" +
                    "8pACG5QhoMzcsYMjNwF6V0R5jYrR+jKSwZp3qq633terquu5hBJPJZTBEI12UZ5Hu/xQdW2VtbXq2sDgPvXLC6GiECrw6lFQSdywIazhrQgNNb/yXnjVzIRY" +
                    "Q4tsUf4mvmzYEKUxeKJ+Mr9Cq4KM3kZHGFujREKZDC6Eh6ef6NQMC5fMSM1CGgSJswgti/MVWG4QcWxFV+WMdCdCm5ZEERqbrTHkJfAeZ5iBMsoIDrXLse7a" +
                    "MOHg04hy6PNiwcHXlvo1yBct3T2f6q7qi6ZnX7Ts2clQS4mqM1SDIiTiDglSP/gVXdEsrij2acRTn4YRCcrILnZwQ70EEi5FflEXhYsDJByrjNy8Sbu3d5F/" +
                    "2QlDDMwibRowtndx/9hAGHCSoFxxtUN7V2vgESs0rHyh3V0NCc6bP6HBy3MYSYuD4ZbmHcugEO6fwEBxREGRHkspic3ybNQlJ5VhWhPPXYLiDwkKRZKeoWiH" +
                    "hprl1P9X7txyt3FGb9SQ/j8n0aaHlDFHgTc7vMJaY8uLF3ogjXKIKBSEl6q8WqENGM7/CcWUuFZgTRrKaJKwWvEusjYAlkWFHuPJzhowVWALl145NkVxuji1" +
                    "CO7G1JCSSiGUePW7xjqs6SBBcPghoJwLsHYpwIb3C7Duavnq6HQtGexYgD2Aw98XYOG2ABtVbigm3F2O8nbTxvg407OYAndr8FPNHRuykuO+YnzMrZTnRe9l" +
                    "DgKJkRFZHkR/37ZRb6SjA7ncmjewa4OhJu1A0n0MY38XECCALFtPAtRzNPHSoCEKhc/Es2z2UqxrsGCFkDjllPIJUPnErf4nsYVvguGmKZSTBaUMdyrLZ8Kd" +
                    "FNhelOWt/qFjnND5loP9CYYpg3RGS00CfCNluCedkWf705vF/pQe4iDU4byElLG/a04sCrsFbHzSHS3rHcVb6fa6KysMOs/MGjaL7NE2VcPNArmc1OekNOZO" +
                    "8qMFCjcWqNZd04gKe667Ep3lue56aCWf665WWsmVLcrJ0bDdA21wcOHTlJVavDK1eNVshByNwpLDck+oFk384oFm0Z/avQESQuDc93eSGoUiwKGZJ7RQgoH4" +
                    "FEr2rg2KTMWvHeWUusbeUn5wNO6rJq3oClvbxok1/NDMUziUpFlnkN3sByWaJ9IwgxI1DEfVGWsneWbOmDvJGRuauMZPZQbF60z0XDhvlZK8920G5R2u0FL8" +
                    "nQzNKzTYa9U2UPYsPXeD+njo0hhxYAcx0fMRJguRxcY/5SXpxBbb3MncPx4bW8DapWH1Q8gWebM/n+dOtBkUllzVNLIgfGDBpPePC1m0zERx4BJ8b2aCiPAw" +
                    "d2eUThfa5EU0EW5GT/KiPMMdXfB4wWSSR50uINKwLT+5qZyAmBdze4YUWaf2nfhivqBpDHG6yu50uVd0MVtdZW7y8nON1b2XlowtGqvTlczYxDPlJQoL9/Hw" +
                    "yf3Qmp+H1myvpVK+GoUoJDMMig771AI4qww0QN3S3TV2hvJSgX32kGexgQsdJcfQ6iWNZ4aLfIVxVtJd7cQs5GF6XvJeixccWrzafIElMCzlVoPPx+aD+kvM" +
                    "+F5vtQN99OqJPY6tNVS4D3oA3WkAZRYbrblrrbQSfUjiKrlmax0O5tTcdewNHcYL0lRcS6168kZvqBtbvLqtMU4lHWYMzr2ho8jorAH3bsYyY+A3N6NlNANt" +
                    "vCqb2MO8ml06Q+1WNGlgcPlTB9RvVXjg2okvTBr+0oGjSWzYXjMhiIjYU09DK2rg7zgDxkFXqcKHcd41lmlCwN1xRumjETk0zqA0hQ24zF07mRfKae7QiDcd" +
                    "Gm4UHO9zRhMc6IQupROijPg86Ap9JGkedBWOK60p1E7JSSMMRYT7eCzpVzn+P7Ac32DhP20JPc8aSDme4rXv9bWprqbjBq437kx1teMg9NjBQ25VfQbIkwhT" +
                    "WGmpLNVSyiF5vau7zuPQVjvMH7Ronm3QoFNKbpQbq0f+PIdy9Mjt1C98PwV9FKN7Mb47XmUurDVI+E/H5Kew4jmslFNY0Rw238wd2C5Jp7paumvUoDQjrp1+" +
                    "WkIZ6msyjWJehRXeP0GCi7AHldckW5GCvHmgjcH0msonP6lEhzHo5nm9jipzGrt6Xlvfn5nDSkOE/7jm+qhDtc52nDXQgWgO723WIIlx6sX08o/DrjpqkEf9" +
                    "Ge39xgkzU3ASK41/IkO1YgK0uQYRhluKak2G2m1wbRtrDOqDqdWtY43m3iHfza4wVuL947YJdLg+Hn+YUSoavUa/yxEuBA7xY1zMXqjMxxMepJFczQ31uYJw" +
                    "RFhq8W/tnTCbG3P/joiNWWVg+moOnLFnKXZp45FJpcNco3+chA5vdvHAXHJ9GGCDuYsn7l089klrvNnF09KUhonofmnRX1p006INFuljs3zUojJfwN5XiEtr" +
                    "qHvBHpLA+tB3Ung1/0oy1DZX9OhuDL2h5lmN2sMcm21GKVVS4K6r3G0bKcCPDcDCfRfgGl22LkBzzx/2h/kDaO+Vti/Ph8GFVD2NAyYqQIhB3lzFQW207tDQ" +
                    "e4iTlGiRR7SckcQEi8If4WFrnqGrh+9kYrRJd2F42GpjGEKRPGVChs4puY4MGnCUurDhCGPkgtukPNF9sLQJEIz2dijRL/Mo2tUjzVr+FGIkY4HTPAqsjaLw" +
                    "AiJ3M7AMEUFGyp+OKPmhRi8pS9IaPaUsYdpfoeHELt1fpZfxKa7Y+7iCWSy0LZs2B0yLKtC6Lc6j0XlsAsxzXPF9lzeMLwcPbG0fhk14HItuN5s2uWGK5LBn" +
                    "UzzojldDStM8StvprbOGOGBGIaFISB+Pzf8qz/+HlecbJFKJ3zjROA+8hhcEoXV5sb2cEoZa5nmoy89zrjK3JkQhbeR3icluZpSxOM9mXAlzs7A/JiZx54e0" +
                    "JSb7FKObi/Jh8sgLW+Stg2fc1C09bOr2znjBTBG7Sd7ufv7YBD3s+GdZeLLDZdfa2p0JOmzHQxRx3I5nHi4o63ZuyhSp1eTv05JlwlVk50wRWluTIGLDkSLy" +
                    "TTco7Nvx3NqgvVX45Gp4IQ2gS71QRNpd0Dj2d6WlIv/UDAp9WzcEh/9GcHgtrpGWyPNsqyatVvbyUr8gwl6SV29j9zTW/UFP+Yhu7Ybq80F2GkEGwsJNmDDz" +
                    "fn++z7uCuKIkPOGltZG2LZua8Nz6ehRq88Z/2teTWgE2c/0VlhGUbeQVpp01mIysEpUUYNNh3sA0jAg0sv3U2SBFWDxnJhxIvMwniciIy3hSWYYbXdtAoW2c" +
                    "cCqjlH1byMh78kDfNyE0j7wzxxxWhu1GM2U2YmhoPV6inElTRrINQlutoOx1+c0QnbeDPG3zdmzmict2kGFt5pl2eTvUT8o4l3Sz20ruXmjDQrZ/SivP1MPj" +
                    "b2yMYQPZkTBa8188bdi0d3r1DaiTb9mqaekItgLKzFr4mQ2ouSFQzK6w7JxgX1RQ3qvLa9UjLYOuPKE0mV3rBtRLUgJt2qltFbptQG21ghLXCgo0dKjmcN+5" +
                    "yZuYXeEsNpYtE3AIQbdMOPb/xXfzESm1DltP+6d85CYTsVMboO5jGSa18bhJ01vbrLzee9rfFVH8QW+Ukxo1c7rqll1W0mECWjefbnjIn86uTbSxbvNG7AFz" +
                    "udUVkRvSxZNUbtg+wUbPtIYfeydG47j9dJxMjDDtwXO/naydZpIkljRxoYggkSG76RuBrYlbD7F7e+96uDO5wmPxddl0JTxs9PYiXXF7urIUXxswCoDDhlEX" +
                    "c/AphYwTRT8OFvjprSK112uOMe5HC2yzMJ03UTgU2PbNhnsdZZmTN/m2/vq41bD9vi0i/fOc/M9U2PLZIf83zrKUEfdiAAA="; break;
                case "16mm-1": encoded=
                    "H4sIAAAAAAACA+1cSY4cyRE8D1+RHygg9uUs6BFz1QASBEiCMPo/oPAt9siqZpI3zkL2UhWdnWFpbm7uHn//57/+/Y8//6O//fVSlzYK/rn0pVX24dLffvvv" +
                    "Za8I39IXft2kS2dzGfhu+/jbb//7/dLuKq9J8dIW/jTxyu7yrrxOXwG+muELOvmyevm2Ku+27jKmrOtS+dxeOpbvw0tT+Z735WvlJzpbXpPg2spP8vB3+Wl/" +
                    "+VtZpPw05+GnlT/LT/MG1g+0bLS4aiyLBX0lhWvF8jrtr+BxJR9hoT95IQNLlHeUC44aFooBrxwWgvUSLpQtXZwyuFIOuFLy33774/frpS9zvcoVxetlys2i" +
                    "z+H+vWyCz8rH+Icrn4QrmvKBw5eWl5VfMZT7/Qf8ZvTe8hL8Lvxb/srXy6nrVX5P+KysUpY05or5CrSmhleXz2x5c4KVyq9GF1BukSm/o6HPMv4w+KXhkgy8" +
                    "vbw1wHfhdRq+Xu5D+SsUVFRMBMGG+i5cZNgX+LPcZl323gHcPAKjXDEiwipChDWECO3wnuvMyCg3DBFRLo0QUe6+M7gMIcP3yEhX0nCPcRMDbWLAhZMiUGhc" +
                    "NtGqUeFi3hI27IqNDBcL7y0LJkJFcrggXCBcqHKICq08XmCmtbIhdCAk4FfCzWV04O23CJgX7NELNgH2o1yPhw8N3OJX+VV96tBRvuoJCPh619DhOnTg74iv" +
                    "YnSUbYXd14QOQ2jUFjfV0LoNHUrAUXZJ9+DA5+QVARwMCvrru8BhERwKwaGQNBKRRiJQaCKQCg5jmS483fMUmCYU3XsBRXkfgkK7HhQB6SIgXVh8ytVAF7Ro" +
                    "JAoq4BBkACRMDwm8VBeZLgow4K0d0ipdABDKMwrXVL4IC8U0skXDgxa2CA0PFvFQtgCec4uvgm/Fcs8PeJCHvPDtKw1sYeESYmOLck+RLfIX2CIidzVAwIVn" +
                    "4F4EhAAhpIdsgbyePbEFhZFyV4Atynuyo3AB+DCBSUMxWWgOI5nDSCBcWCYNnRgXqccFBi2HQcsjKQVmf0fbSUwEm1s+jBGX9EQ7bggjDpdQiAtcqOw9Egbh" +
                    "rJANXl25ekRt+R+uKhFYkxNkaIkjmm7hFhgURhZg+DyGkRUYCYBBROEkjCAwTBdGdCMKPRHFBAzFVydE4VBAYBQppIlRRPAQzTOiSIiLhLgoOw2wKHcJ1YUn" +
                    "dWFYXdDGGS1qwjFNGKaJtIdDIr4HNIgigPVCwreXe407T8+zNysxWMRQ0SweQyojqawAkcgSdspCOdK+W1wo0UJpHyjUVkXgLoCKAIZvKiKOKgJ5AfUBkz3z" +
                    "QgZMWNlG2f7ynMHrX1aDRvpURdTtD8AmulwMxyyFtADbL9ue1ENawOc0R6KFKCICInmyRAsSLjyHC8/hInC4EDzYFjYwXATGQdiFi/Iqn4gQeBvpOQbSh0BB" +
                    "8jLQbjqClEsrLyBiY+iEBNAD8IImPekrHQAsSD8kO7JC4Luumdat35BCAdybaBE6bQlPK4ArD/IBw0VawoVRO1bQVaWaOLMCPC/EChwt4BcpsKhwMO4ZLlJG" +
                    "WshIC5H0A2lL51hG2Ek+hJpt7HkhMi4i4yL2uHCIC9nTNevwhI5EbEFLepYRqmcLvOICMI9LdFkHwRfQ0WQE0MTKFquMqHSxyEoPUL2REVO0MBIt8llG+I2M" +
                    "OESL3GQlRwszqQgtOYfgwaRn8QISNbgQhWSuNfIyBAOkjDRJCLPPN1xiCWEYE5kxkXtMIAQdbqhHCIa2lZ2mtBUTEEE2mIB8t0qIIBICOIIvS/HlKFI0iQRv" +
                    "4lQ0dGThqoR48XMfahBhue8kiEgqukk2dhoCko1UgwhpiKToC9tkQzHTOE41IHohuSj6sGYbFkSEbnSRMNsgWAgarHkYRzTGEU1xhHSECSNhaAokJk3EkRgc" +
                    "DKCgGCSSjGpORvWYjJKyBFiowVdgxqDwBIkCZxzweOwTD4CZYsYIsaa4XvQFprgU3nI6BhK3TTxiLzAqY6DA0LNNofi9TgRGry8bY2RijDBEkhIsXc8YuPvI" +
                    "GFoWSiwwemzg3TNVYTBlYCZaIWGfGhWhTz1wy7RjtyAzYdiRMCD8YDBJjAXPhKGbdXUmDjRGHDoigA3QipH20ovIQHVBEsETCTlOPvzCHIWCPOIq0ELkAiCB" +
                    "8GUpDicMDpIrqWalqhEHywyzik+7s7Bixxv0tIc9b7iNhQVrSu4BG2463rATNkCvlNfpCo4aT9Qu+2iY8A+JI+FmJXyccyDecMIbkH9kTkt1ONgVnqHC+Qgo" +
                    "ANQbfmdXLO6m2BXkkHVpKdsVEFOIgLxe7QrMnGPqjYpE7msinzSHamtydtLRhjn7FZFDysdCw8xCI3V5SU8budLGvdDQs3/V00bNS5u7KUqjAsI/pA2NGSd5" +
                    "gBHzCVYaAI6oRuKAFfpgEhwFE5cZFWxaSADS7H1rdTYt4EdtY0rCtQOpUEd76gZ/UwC2KA69Nb01uVmkjGOc0aEkZ3WSnVjGhpPsJMzZiT5nJxRSAGHqkkxn" +
                    "F1PY+l49CyXmJvNGnLPWIlNsow0yszyCIwze94OYsvU3A8VqpA2RooYBwukqvJHdrDGyiJt1G1Gam+UPmiPijmNcYWCEXVEEIBGGfJVpI4wA4cASVylakSG0" +
                    "EUY3SyIKulnuBhqONKatTzupjQDoasjgvNUPeaue7Qzevi6icMxrPqfp/KzO0KiIYNnxY63v8DZ1ZSvjmLquKetaKMsVDGN6AlqDU1a9SU9651thQImD882+" +
                    "qzibnLPSSknPkLjLWa2ojK/nrF+3vt85nDWSHHLWCgT7WIDidWRLAtSQ923I+zZTHAktjrAQ3dNEYgF6ML8NJayNKNxAFE6IQswuEKBx435r5otMTreh6LPj" +
                    "CwwkTBcU3JJ662cIX+iGDciBttmJ8EVfRKXHfARHPhsae6Orl6CrAao6p4v5AtKTCgrr1A9UoOSAagKHi2x9s/TULDkjiwyfWGRwYd0ySIzYGobBYXpw4O9d" +
                    "PQmACJdNKf+NlPJEWwnjIEEHY5yCUqoZK9oapqmMlrpGCnAxdypjrqG6WYOSC+qrN47ZPhU1CByGBKKkrrbFgJq7dr4G6Qwn4NCgX17Arms06ZgjTkUzFKEW" +
                    "/xVwWKYOwYR76oPe6IxAOsO0qupgj0cmkNQyWYkp7+1xiilS5RgSE4wm5Gq7dLbH9fugwhhBbFDStASVt8TxcT1Vzw75ydZYDHK3CyrmXVAZ6ustqFRAOJOe" +
                    "piea0hPyvLQZ0hMUBGh5YXSJUw3F1VL7kLs6fSc4enBgATdghix2lZVSqsSUToPGXfOFxzIsKFHSylJRZYWsWA1B7wUIDuKNlE926K3g0IPlNZVU+9xVbwSH" +
                    "abZGZHYhOxRtjT49MYSNmrumodauxfICauk6c8DViAgOxoRX6icoDkuKQ09+hl8Ux0gY+ZP6SV9Xi+ecVcxyWsvRlrq8UxytbWuxN4KmyjDHFhalt8RRS2u7" +
                    "env61NjQWzVajY1PGjGOgmNoxHCrHK2Y8Mr9hJor8UbgaGLN1LglBRTOC4MUUhgcRt945R5jmFdcSMFHfUxYc7W7luraCA4p0EG5VQ99W3Shkl0ryVloreTX" +
                    "box9KUXs8i6uIHc0eARsM9wIUjtYoti3ZVvfVsy1b8u2UooZucM1eBgusGlmDkqwgTwGfKTauVVh4R/UUsyRPEgfRtMCSE8acN+xCJ9Wn7xv8Uwbrhi6tD6T" +
                    "FUd2kPKqa/YFhA62L+IpV/1SN87MDu7D+qoozl3d3cx190VWxI3r2ZGD7Ll/mqsm3J2EUT7T3msSnOXmAAQ8awmpsRqup6qNpsD44Tl+TO17ee7LobaqWFs5" +
                    "N+05u4I7XqmrjTncqufwoZfUFGWEqg0ZB/tCGMEMjJBnRsAiyagmtvaF7UTA2qFjIZLFVWnGrpPTTJ2cESEhoH1hJyhVV+2u0bdiwcfwE5r3qB3DS3kk7eVE" +
                    "lmqqm+REvjMwpJ+Tq6vcsDe0f4uc4FBxqr2vXeCMs1gJg7GChKEGOXHfkJG+kzB6OWFv8pB406hzkhN5kRPkbum5vFox4XN42MF3rqAZKjhw3UNERZ+iIlOE" +
                    "1sElDb8ICf/FDp1WOQuEMrG0wq5y5tExhfTDVkQ0vpCM9OB2mrVxi+OHndPSdNe4JZbFoC7TShZv+ra+FD8mNCBVCAiCf9jPmZG0MnXnKEVUoQgOXGzX/otU" +
                    "caCIvvEbEtJ86L+ABy58d8LROrZmF3xgiLeSorc4mSHMW/v7ywzxYee32jV+j5KiIiHE8POSUSNDGzWIIFPYyadIUzvGFyaIQBfY6lAM5rcg49DL15vf+6gR" +
                    "Rkxwk+e+9fvD4JHetnluer9vTCz/SfAwkotye/IZHDIVIJgIjxuAu8atnCeDE7GhGBOZU1JOPbSwBqd6gZnFkebH13pkmvtUdJwkYl8yqepTADg2beEVHK2S" +
                    "Kk05VlJR0p2UgmZiIJ4vy2ELDnVrYnW0wT19+7kAfL0V2ZnmxnAurRI2dnMBriWieujNYR8riLBYTKxuMACg4X4VRn4VRobCSMVESOnHF0aiNGAgb1jmDUlM" +
                    "MvNG1xOMvGE/sLBqAwb35ICF5ea8hHijd7LixsHo8CUWltk1bvnbeRLCRhrHUlu6OjZuAUiGeaIo/Z5mrKiKaFkHD2HSY+jpS+DTLT19WChFXqAJRquFNuo8" +
                    "EbzEj63AGZHBgAjZ/HIwfjkYRBeChfi4IDKkJVo0KA4YqdapNYjPU3pip+6cQyWVEhPYVDf0ag2EEdMXK6kgN9zQGE4tfdw7lKl1iFuAo9+OH/YaNPFDXsfY" +
                    "I46/fpyevKmHIF+wzjCTBjUtPal5jpKWsvJfxKsBfp7zEyCMComo3I9vATZywAHeXp5U1hxClGmtv2hc6Nb6i8iwd02eY3oCJrg/zxi5fE5cbSuGRJqNj0NP" +
                    "jh9pw342slwVqO+RUb2MMB5wYO4S16YyxBdJ0uY58cZmNtGsKuOllyk0Wzu3uDfHIzScQCP92FIZH26QdDv1QrSDzVOpjGsQUlv3mp0PzzqDNagxpw5gh+DI" +
                    "NUtxaHiivcWFEn+eHDDL5EDqZUuimJfSmMHO/eFddqJELsTKGkPmKmWyzcSR2rMGqIxRgOKdFWjYpcS+Od6AQaaQR/rE1e0HByoi4tNBtBsBSjIj5CmeSGsf" +
                    "F9ijWmdLVuH5wfkGnZnR+nKOlbPuZIM4VNSFLuw4jDYPk8yRRB9nE8MnLTk9JtKhdpb72pl0hR/cjOnIi3Ygyrknp0IhPp1Cw2I0PkUR+lVQepIVbsno8m6K" +
                    "JJojiR3HBqoV7jhhZWxo3dVRu3y1k57RSYp6Qw88pup5qhFESh4OVKEIx70gPNseSf/G1dgyFQpjgqpbghpgVz0HfZIDHT2YnQ0eZELAyp5igppvQofi2GSk" +
                    "D5xCTju+hyUnlpXcAAbOUCsGog2/GrR+NWgNDVoVE/Fx0+/mUC32trr2BbA01bHjoh6dhNEjsBTZyorR2squJpqmjhJ1A4l86kFYi2c2dbxRi2ddEGFHXPNV" +
                    "ifk5nptkR9Gp7ts6R835rv8C5UCYLXGE/drXKUFEkWOKo8zVvmBLXC8VNN0LC0hVHUBDEBGdezrhrog3FPEGJavl5yQmBCQMORVD2n7j4m4RYXT1NoRFOI8V" +
                    "EWFsxor80PurT6kInac19fmyk+HGXIQPS5kZA2ZxFm2R5roqZakh10qJKMUZF31AWQ/EwP3198DQw6zqUFl96b3nOTTuYaIqgEiPJedNY5adaCJO8yObU3J2" +
                    "3Zz50KA1nLrH6cdd28UHRdW+fdPeH49ypzVr/qG+3HaxVEe+p4szH7su3GpoVQikp1LzcA4GlVWDDB9qrpnJmRd6bdeTow6GppzDeRjAE0F4ojsCyc3hA9Rn" +
                    "OEaR3gMXXKTaxhkaT9S8dHsihhkVRhdGcq8/2QQ/D7ZPjuc42B6GwXZyPF07EGMRGN30su3SU1EYVnIR24eRWE/EqLhIT6tnGVPoTMPLyk5dnDg64s59OcM4" +
                    "IjsXRjrA/a4/R2wtJo3uZKUaRWR0pJXdP+jxZMNz4A6MIWY4iW8snn16Et/YAG4+bwD/aHLkXcfWXT/G0rBVAZHS4yN06EIUBRJNJ2L4TIlJIlN8OhjDyDx7" +
                    "nCqq9q6i2ludlmtnbGFACmKGfm/9pomvP0AnKWnKsPW8z26GaC2PmEVb2M04+3zYFiercTnI1e97tlw3zW72gJhPSDH9WVsJjbNhAtF1Z221kw7wrC3BQVbq" +
                    "J1pZtWXrtoVPzmaMbUx1bczpzc2dl/XlxpxWEtk6Wm14/dwE/omRxWdfuA+NLDsMsdthiD0uTeCnji09SE296elcjKwKhPwzxg55ht1PTXzdcSjczDkGjdCO" +
                    "Q3kfPJZZ9rm63nflvNedjIwuDwnjuQbMFV+eVz53bKXN0Rd+nldO/ZE5jI347gA2c66JDC6nmmqpAg7BRH581tbt/BBmqewOGbHAw5SltqNd63lsaF4czn3m" +
                    "Fppujt3UckjQlTjc25JZd+6zdPTluR4S6DBP3eo7901bPXXcx5L9oeBudzRKuj9ti8WF2Y2l6q6jb66Z9UejjPKioiI/7gA+d/TFw+hhXk76rY3AnzUAD0cp" +
                    "uUMDMGNjAUV/lNLBvADSYH1xEpzbCvt35ar67dHPp1zVvDn/4jAiYPvT+TpICBLy49NyhqPi6ThPS5jw1MGQxAj30+Ahd/blOFZT+8HDc7fW6fBnX8MJd3ee" +
                    "TtnqT24cnHA7sEa8mx4x76YNzTJteMxT5QC2/vTfenBjZ4QnPR/AptdcRJo87QqN2q01QUOM8IqIXASAjUbZkLyL0SdQhD/+zFcZMprHD8NBdMTWmnM+7KAX" +
                    "HXWihP1OTk8UdkBuCCSdznxFO1/k6HjmqxlRsj9A5+NyyVcKq9MZ0XO55EAgI0oms2s4O35u0YH2wW//BwoMFcSJZAAA"; break;
                case "16mm-2": encoded=
                    "H4sIAAAAAAACA+19244lOY7kc/dXxA8EoPvledEf0a8zwAwW2F0sev8f2COJlEiKcvcsz37o6pipjqzMOuFx8jidIo1Gs//6n//rf//3P/6P/evfvsxXMO3/" +
                    "vuyXNTWmL/vXv/zfL/+VP//Fmq/+56582eq+XPuv69//+pf/9/cv678+r8n+y+X2NXz+PX/F8FX8V8pf9fMvn++37dr5y4b2/Z9/T/7zfeHLfq7njG0vcZ/L" +
                    "ps/vguu/xPj55XP9//Ef4z2E0K78+fq5ZojtG0Ltl/y8sF2p/S6HL1/79bztF3KxX8jFdqF/zAt93ui4ULT93UQ3LxHK1+efdqWQ+5XCeEvtuz5XCuGvf/nP" +
                    "v399u8+Fvj9v//PP13f/x7Y/jJ8vtf2H9oO+/ecz/LauvcPUft8+hs8L0lf0n6u0v1j7rvYhfIX23e3/vz9/m89Xn9p/6H/aXtC+7XM/bLug+3yxof+g+PlZ" +
                    "3rRr/eNz/fHNfryj9iq41ueF7RaON/z56j7vqHx+6/t9/fw+tHf4bdMnFCAQCsaD+dVYCC0WrE3tM26/xPbuc/usrevB0D7hHgmf9zAioYxISHb8z7v+8bfv" +
                    "sqW91o0IyT1CWph93m32IzTaXy30aAv96smMGzquGcdlw4ixz4Xd56P+XKTHRCIxEfsb/Xwt43vbJcZbjePdxPEmoumX+ARKj4k8YiJjTHw++dTupoGY6Hek" +
                    "3SM7YuK7/9o/6/4jx62y7aZ+flLEoBg3Mrdf3fht6nfcV4ipGSaf+M5uXNi1H/yJhq+YV1Csb3bjNetaZkTMCMBxqVj7n7nx+/4Wv235RAWGg3sXFp+00FJE" +
                    "bg9y6Z9wGZFhzMgRGBkBIqNCZPgRNfCIt9e1u5DHk5nHk5kDTRb9Z3zue0sWtf8w38ID42JcsF3vE2E+9shyaY+LmSvG1cZ1enTEERZ2hIXrV8CwqCMs6h4W" +
                    "BjKF/yoYFSNSPv/SPuyohEXguSKPAOhXjVtYQK5oD8gMC8gVSYRFzxUrLOL4gRXe4Tku4sgWLS5mQKR3gVFj+5g/Xz8fc6390TM9P9vPe2qR8bk3PDICnB4Z" +
                    "IsOPHBFnjmiBwVMF3tCWK/qZ9Ll5tac+vKEQEi1j5HEEuHGKjOfc5REZLXd+XtoCo/RDxI1zaOaNfmzU/lZawvh8azQkFtix0XL9V39i2y2AYPAt83/+6p9r" +
                    "ZYiZ9snnGQyY7NP+XOctGD4XMfg6ePR7jnAkGMIMBkuCoaxg8ONarn1rvxA7OmoLBggC98cPj15I1HYYt6/jvGghkUdIuBESDmIhQnZwEAMFzg8/skMYQTBu" +
                    "QDY0CGx/nnt58rnTIxT6vcsyPbRYiOdSosdtGMHJv7n9kka6yP0Pe4LId+cGJoiACQL+cJwaPT0kSA8QER4jYpz3BdPDiojCIsKz9OBnRCjpoZ0ahvxuHBoi" +
                    "OSRMMyw75E9AYCD4l8dGTT0geklRywiFOEIhjuxQR2ExnmSbPQRC2AuJFhj5mB1a8dhPjtSf7ZXuW1nhe4HQI6KMeiL1m+pHhLlKA6PFAhQmmB7qDI80qosR" +
                    "F+2tRXd1cLBkEXo9YWZgxJY8WjkxPtRvhyUflhOYKgqmAI+PdwsMx1OFb8/LqAywmmi/WFJiJowLt+JiJgpHT43Ug9JhyM1EgfHg08tEQU+NwlKEHXERelx4" +
                    "LCusODRGPKRxaqfxfKdICkvacozTH8pLSBAQDnBKuBEHjsYBHhOkcvCzyRgHTS8ow8gPo2sJI6ICPTT6B00LiDTCAMtK1mvYGQciQbR02V/41VuJNOIgt//A" +
                    "ysrQykrHy8qyAmGlBDuPH7jvBeLUzpgaaXV1G759BKN+wAgI4V2GaN3jJ4/3T670Ur7aUVg6LCx7IGDvmUXHYSFB2JEgRvmQxtOYKj05xj92NaGfiIAiAvqN" +
                    "lvutbDuOB0fLDwY7USgi0mhIS5nlpZ2nRwuLxMMi0hY0rMISWlA4N6AFdXiXDt3G+dx42m3sZWXYyspRlUBZySuJdnBgOISXbWhuWbtXALn1zg2Y6Gmi9NOj" +
                    "jlOjtZs9KNzKDmlcvZeUYcTEeMbTuAUp85hoJSVWE3UcE743B/1JzwyYMCNZjNPHeRIUvicLX6GbHa0LuRCEFfagUEuMCwUvg4Lmioy5AupLt5KFqC89j4qE" +
                    "UfF5fdbqS0gWVksWogeN4z4ec4UZuaIcyksMhviymmjNZgNJTA+LliH6wVEHRGFGJZHHiQFogs1QYkZMFHE2oK3dGFm7/dI+UMsrzfac958xutGRL6A8gaYD" +
                    "LusHwuFGP+sCP0daPdGjt7/dT3g4UWcKfKIljHGkhYixsW6Ehd+GBU98A0TU+tBMzxEOTxiZMLyWMNY5YvBnYWgkWlB4WuGQawFm9W3JpaDz2AsKDIn4sqAo" +
                    "vfz7fA29uFAKijjCwwp8AjuQdJsrxsM9EIl+sThATGho4Ta2JNEiwR9zxQQmBo6CWSLZeWZAJJQRCUEHMUWycOIAWXhVoJ0HByaw85jAxCosSEAkkivsyhW9" +
                    "UBWFhcCrZushgYmgAxM9IiAS0ssTpHTE6vNstojo/Xgto7AoIyA8lJZuxyXoITLwoZTPgeEXKL1a0hYYhsETI8JcnPFBAqPSuBhhRgqLPA8PY2lYKPEQ9MMD" +
                    "CgqAJrY6c4MmPH2mPR4dbus3HOs3sl5mGgZMQDnBIG0Nl2hFJsZAfnlwlP63KL0NLb2zq+PQsHBoIC6Rd9SyHxqeHRoXGBWFJwrrP6G+NLO+BDy7ZQkNvuwg" +
                    "SvAQDRhPfjSlo87EArMViSPthAGQhyLhCdaGto+dARRp9KGr0PSAO0qAQoOsHC8pfKu+oQ894BPr2CAIOa0z44SxywHWxojIL+HL0v+ypX/YJawmpFSGbkeY" +
                    "gBUxARvwRB2dYDbHCZjvP8CPuDDjqfZsflVGHTHuJp4eoyxxlrcfrZxQmxA2DutHyMC/wrhOsCMq8OHzmCsMzL9OhSZmC5iAOVJo9q40CCS6RQXMxciww9Ns" +
                    "YccATM0WvPkQow6vD8Ba74GxUP74yeEO2QJmHXmcHE70ouO8zvdz0F6+QiPqCDRh50ExMIkeCiPnYEnp9hYUIao0gol3G3kEgbGz2xi9bHASzlaD4ObA8DwE" +
                    "MkEu4wZM0CGoZQdGEQAVzECNGGsQ4HIeGDwEAoQA3vrq3pWTDal2A7DuU1A/WowBSfgwO43eYdh1WEBzQIee5oxl4/BzVJQ9BCpDIcpEIfrkUwOz26cTe2ka" +
                    "KxalcEjACDTNQwJ7iyR6C4Fh095iQpVwROQx4lqthZethadQ5d5aWISozpWkkVMNr4DYEByJgtiit8AgqOm3VJI9c1czMoFn4FTYcQgYbY1ScjyIZZQPCFqO" +
                    "ozoVGhGOpIbeIIT+EyCrt+KB4I9BazHqaFVcRyAavmHopMzP6wBLwhUJWVoy51qJIS3kekMhrEqPIIfDwqY80iNEyZCNpEfIKZeOTe0wBFSTFmsGSA49HjAO" +
                    "jHk51egjgtrDAmeeadSTwJPwkBqMqCfLmoKTgDj3FsepBj7ms6rsZ0XSeosRWf3Dh4LBsnGGw3F4O3XwzKgTqCKFg54pikQhJmwZVRRCgy1XqnAKQGVkl9EG" +
                    "NnyqsUCI1iz1F1qZKWKFsdn8A6wmV0SY8BKH6M1g6ad86ZhPHdNwA/Ou0XB4KCgjBAjk/TYHoylCg7EpPSITGNtP6gsgltBfwMmxwdge+ot5tQh16QopEggq" +
                    "KmUJjL1SRcaTg03EAYVwSiHJzo52DwUK0SKDnh1YJRJ2BAuIyEGIsMoIR+fhfh4dDv+gTzdiDwiMg7fEmVogS/Sy32nJIq46oicLIFpBq9hiBYfiN/WEOhvn" +
                    "WOVOkNBiA0dfZARa9Am5wKvUssKS0XhlIw5JqHI7c8ZviGW+QCxlrkhaj6GcImYbcBQ64KC8mRkQ7iVAVTvAXPtNq0EBqNxIFXljXY6iIrOuo9wVFR76jfGj" +
                    "MFfwfuMGuMSyIgbkZKVZTySLNE4ATUK5wi0PA9GRQ/yx9aTVJg5EHRuM7xwaTweiQR+IBkK+XAFVZOdhBSBBOo8VDm85E7n20dcAtHuvCE1oxCZ0cmjUGkNg" +
                    "VnGmjS1fOCBUbfliYVUjUdyMONj0C4l7EGc+HJCqtE9EGQGTU+0ooL2oNHobEngbQiccbsMkBNNOBbRnRxpYF0I5e+Qo8SJhzHgI5iVpwvey0y92VcOn+kky" +
                    "iNrI0sUZR5ozcmxISUBo6GV/93TkhcijnU1l7j+Y8rQV8HKWreNCOzSxo5YtVSjgZSSNyIwI5NsZUlxsEIU28+IRkY8zL6uD2uYCppIzcr/PvBKy7WYgvCBP" +
                    "uDEQtWMSanHiRcPAc7J2dqu+oDhFOScIyrh0mPAhHga9DuIBubgQFjJBxAFVZeBoA0zR0mmaMEUpMxwAtYzXMMUMB6c0HwuncCxB2Emp8nIEilRcy6k0dk8Q" +
                    "7lxRBIVsZxZsSYccyKRZUfCaNNH7UqBq92yMcIUdpYTfSwlgU4221E8cu5UUUSNXlZHvPy/1BaBMClYgjRYmXVsJwacagFsvaCOaWTvAqKsVlonxJCwZfZKj" +
                    "4r6GkKSqqxoibDzcLRLUGuKKlO3ZCodT4MsVAfEtZtV/fOlAcek0u1JpN5pGQFTRekBAwPPYXtYDYnR/edyHnPbhp+c8O86narmBI5mclI0AxcK0LXCzOT0r" +
                    "zyF7HIEaxxkWLQmMExl3xUVkZP0VF+4wE2c7HD1MPGXjhsnGjdu0S8MnIr7QKKSqb6vC2isckvmNxaXdSXee9xx53+9BOu6RPVNHAdAyRCWUuwynCIGeAuBV" +
                    "sNVTaDhYkigKY+E6xqiybKvnXFSaOfKox4Cg9GyKV3leU05KxJyKWw5lWt5sEHzCzgaWTVIjJ0lEsr1hGUliAlYzDl7TJOiwq6jDLoyIE1uiTUALpV9qtYTv" +
                    "P+fzNfTzI/Wbi3gTLPC1kLCMJcGbDcKSEJPPUiYdN9/VEFGs/ilHx83Yix0dW00ZtjUv+5CPezH3slhDWNZ94uhzhUF+zbHzg2PnR2UZ2QAs8jXAutJDryPy" +
                    "TBME3r5ArFjj6CBN4DYHQawAWPCzESUNR6XoNkJVKuXyIiwwTcvmsx3fbI0jyMWesrFn8l5a7uyZ0MDdDcb0/nbJq2ydhmvf6rZ6oncaMxre8iRqp1xWQ9hU" +
                    "20y8h4RfvWfPD4WVlqlelJZYUCy0alSVSc6uWhAMNNQ94O+PIRgcPovFL0CrMln8jCkhWHaGpIs1CwsP6PuRVJpr4JE0+r6da4IkXYiNL8qfCSQyHB4gljYd" +
                    "0IOSZeEVEMX8FoC7DDzCiuUe3oHaVV72PJF5mQmd6AiOPIIjx/PEfC0NJ0PH3UimKedqE6FM4E5AeTEgb7rkE1i96cRGoNvGYXFDuIGTm2RDGu64VcGsaRhF" +
                    "rL4FQMHKixMHU7Cr5kDMsMn5LC9mUJTwW5l3K3tUJOdKVi7b6jCTlXuLcQ/kai39VFwaBphh4RVA53dV6U8pr6rnIKRsG3ohRLGMRsW0ur5AEtQaaE/DFbWG" +
                    "4laLWpP03S8KcVuWNMzsbe1+nBjlNNHJNSsYXjAqrCgyetbgK4FlcjJpDxLrXOrY4aq6xl+44ENmosCr8vPk6LMvBabq7yp6oOqvu78YuMirAgT70HOEc4lp" +
                    "noMTkYMTiz5BUEvPiNl+kuuKDlOZ65vvkVmlVJh4z91b9oTScXAyrhd4JcLYaZQSzjydh1KinZyHwtFPjomLXWG/mNmz5yDjr3lk0LPCikVhQyoJcwFeJgle" +
                    "HnKCDk0cFr6CTrISe4ARk8e27yU2hUlSmOHg3lIn+oPeWTC55fi28tV/TrGgRTMouHmj4FIRGigsx8OdAsEsR5Nhoa7sT3VinecAQbG7cHy+NfBOuuNF8ovH" +
                    "cEK0smUHSbc0k25pIUuMM6FHgmg8WwC4GQCUKTFgxDg4EWfRGUK5ZayqsovOhHvCZWdK8LQQgCgx77ozb8fhm3ZAO/7x+M2TbBcu5p441lgaAiqFphc8oT+x" +
                    "IRKqPpGe4augJw4NXREr86whPImVGWDadZYQCOouqKTopylH5Pfm8zj4dIryzPeOUl2LCCBqiaNYLBpZEQmkqpEZICKcfcnGrf35G42GDIo18sR2swdDmEs8" +
                    "CE+1JOHPBP2edgCYSoxtmWf1wGvHU9/ZoU9gSQRahEY54jiuglplFdQdYao7XQllwqFmCytZEpUWEZaWkNsqqKRJpC8dyZ6x4OzLjb++ybkQKpNY8RhE52k4" +
                    "t0pPFg9I2hct5w1He7CpCl4B1kB5+QB1A1kYz/v+J0kS172mPfWaJ4RKY1PJVnPXLBtVpb/Y44lk4gVJAtkRMwycM+9m4bmXlbmXlbArXuiueFgRkAf/qT3I" +
                    "dQhJFJhkjE89CwruGGUsjJKRqKauCECUHb6uCuQwgOvIBlzKPpebUMNBVmSjRGSZFyZQeZpwHYHKoquU2eOGuLnc3JFbwHNzh3BkGho1b75zL/GGWhW6ZWYL" +
                    "4pga0tzewQ3giT10xDKx1KBtiBc4+OFekgpizrd6UMAM9FxIxF5IQFNR2eYGjwp7J1YWKYrtdlGJQEUlriXstOBwymyD5ggr+DJek7BbhYTZxIjmmUHY2TMo" +
                    "3FvKZe5VS+5PYtbERpLY88OyIkKMWDbtWqvBN8ISZMcrMLnKyFa9FMLErEuASbXEqvASC7+G5mPf9bKk+eCkSysJE57StPXtYD0yAlBnZsvQSZeGbPgUgVPG" +
                    "bS6+nx4ASlQqR0T2g2dAuLekS+Dt9+xcE3IkgLHPJ5849IpCWCLPk+TzE8qo9cuoKIrRtgBXhKSJQG/oNYiOgFKVNydYwtIslGYWyv3T6nJ2o9KMI1pj3nUE" +
                    "RPdxT6vaSROFK0t4VfgyCAkzjXcZJe/Sn1h2VaFV9UoTQ8IF81YSdcxCh6pfI1vqy4BMdsTesnLbqTKe0+wURdRTvbmR+GWhAWS7xMl2+04gk0UFsUO5OB6u" +
                    "600uUKQ3pWmLCn3TJxL2PpmI2ks11EmcMEq9KTd92kR0BoMLv1NfAk4R0EuGsVeoN3Q7xxQmLmai6sbw6iDIaQIIFoxGDzPRyheHF7cGqfucSqHLFO1KE6fD" +
                    "5BH7biFZKoPfkmH5hdTERr7bpxtVEPin5CEGhIvvCfyUug8rHUxnQlJy47Y6DGyaG+kRStieSBZRGBrNTgiMfacx+ClhOy+xI8K+SxPaxvmHHHu5Tm1iLWp8" +
                    "qFFUycaP2+ahSyvXcrZV71G/rRSbOJQZVu1OFv1uSt0JBv8MCPealbmPPsJl5nC6Vi4MysNRRNn3EtePkHAT7V40CkwZ6Xb20c9epkoDo1DDULJKpXPFTMxy" +
                    "rQHHaBRhLg7yleIFfp/qzxu5Oy9nYlVrW9lWQNR0UgPWn3aj4c2AcPl9/dkiwy++bjWUZVP40k+qs6ygCYONxFTom678FCTFkIRhWQmaz2HBoe8tYTimhWg0" +
                    "1tWBx62uE4erlR+vLXhMkVS3iWNeFxnxLGHlHkpYzWhwb2lXqR9kqSOdqXMncy9CSz8P6qDjeSvqjAWCC/X9XbWoyGY1MakhO1c6Dirrvr81n+AEgqEYJ/Tn" +
                    "QNeIDxOQI6E/SkJ/mJIDF+pVTqpXhRtYy+4NqiD0B0bAM/umj5If8hTWDr+Rz094NJNx5Y7CAyCszYjb5k4m1fMMAdAW2QZMs5bYyRR1XKclCMsSBF8hpvqo" +
                    "aU8N2+7w46bU38LdVTswwp3sftJKiYOwNt8eDhgQGAeuvPXpMGNn1YQxBUmsNx1NKWz0zzKCbpb3aAhTVfumnBDTsX1WaqbeOpEd0GTNqBrm2CHOcgWMd6Xn" +
                    "YUjYosMo54afdF2/0/oFKzMRVqaYjrVnZV8MfKJQssPg6SCGOYPCvdUv6sgEait7LkPB0SyCgoPgOqVigk2HsvjDtr+ihJ0AoQAYCyYiLVGcVz3YFH2TxSRt" +
                    "hyrEbzmz34yYyIvBvYS2vRRcz3xApgttR0U7t1O4L70ZaEyoEzJlW3T1pc60oMBYcPW9RmoLigqyA0RwQCCdRtQRfiliEoEzBahgeSJS1fUguo4OVJT7IXrK" +
                    "U7uIL6UXSbY5SJxtgspRZ+JxoEKIkwRu6bOACpEmAlBuruSUVXUScogoW4J2IZt9WoaB4M3LmWlrODuyOUSMbB2JYywHhsQHZRS+AgGKSe0HYuZheF5A6hay" +
                    "PaHJGGn8dTg2mMlT1hrZylgVRaNmUmKuUctMliTisc48HhxZKysWN5Mwc+2DJGFQe42S+TWAYsaBN+/LzNWAshRRuWiuF2tgRQxR3d35ESJwtUmSiEx7vVKX" +
                    "BVRK1bj8VMkoki1DEhpAzyPyuQpCETe8O7MNII+LHvz4KE9WinfRs0BLirMsyc7P5JP1i8WwbtiBEeHf8vOYSWBdDmC14uiUSeElIWy1CFmoWrNT+UG7LpjV" +
                    "MWiJg5Gydr11B+UEZ1iwwQfawl20pAbc/dbmqJMTU7dAbsq3SYooohPSMmd5TNaAqAYNVFD5yL9iHalfZ8cMA/9a2OqHafGnY1rMoPDurYZuIdMxx5FMpqGb" +
                    "VOmJCw1dqrae0EuUANFpUiqAp3lSROQSygS7tEwAjxMrnlh2JKGkfCBqpvO2h+PIlawxndZ22IOX6PVCcVV2fXqemA5g/rWUbh415timsYWNzdMSXhfLPqiH" +
                    "OCWMbpY9ElBkmFYN6UqnZA11adCXgAY2sXweEm1AiPsTiKfutk9WG5/LheIpXXRyClwIxYZsYznhyb3s5YQhtnCZLhR7xqk49aJO6UXj6kVnMPi3WlaguE51" +
                    "MiPrRe20fUL/L1ZvpmkaudMoqBii2z2HCxPKzEw5VUOsTss/XLegTvnUp0pWWVOy8g93wqwErPziX52MI2+qy8CyhKMzc6267OcFhoEP4ceh4cehgdUSGBL+" +
                    "rWZN7aUTyGU62pQK7QF7aEoD3Ry90ta1G8/GCLdAQdckZ4jnHpJUQJUYzWaJVtw6/KwzxBxlrjytMpWp2E60uagt7rVK/LZJetYq2U1dZjz4t9o1taOEoxER" +
                    "bmBYZrjdDYwRbkY5UCqTRtQ6EBxnMSFuoq3r7qX6dyFuqqlH7AP37vSsV8KldY1k2iTFDSxccjbFNumFWXm6NivX0Aqv5AwyL50R4V/7ulSiVwLGa4ZJDjjR" +
                    "nBKJdsW6Xi0869IrCTww2HYPBsbpHNmVzzzlAxNXsOPWuS59Vp8qsz9UsDloZfqj3HKSBlAn6tUR2cQ48OVtwdnLm9KnpSURfJPL2JhFwBKY91l8gO8I06kY" +
                    "rzrB5ieMjE8I/+FaIdFc490as8Yo3i7P+FaakeR2ciQdvfLHk2PrSv2lAoXm8NPDAaPAvx2fCy1dyqSw07b+yZqxvysqxOyc9JOFcisBZ7iqN9XROcsTOEG/" +
                    "ODkeSCSGo0TiPbECBfsPJ4fGufJ0JkbKzUfEiolzY0D411NS6vbilBRhuSFx0L1ewpVf4OpARo7IUuEKqJk3m8YRNgqDIfKKRJCgSJuXZwbVV24vd2zuoKSK" +
                    "uBWZzO1lU7hCsugz7TPZmyLVZgZCeG330hd2QAENqszBphgciz4JZStByLoaa+d19AvA84dV0zKKzGIvef5ABIauF2tNP5Xtloc9bgbVxea2BBHjE7aA7PBe" +
                    "WIwUEeOlN3XVG9LGvTIXOsuCrBtP3CuviOFBhnCqlWQ4UnUVDdUWDzMMwmuPlx+v2T+J1+wMhfB2OlrMZteQNBnVcpBRRVOPQaDD+cdI+snxUdiUNQSn8QIb" +
                    "QdzZo4GbgFCAIrvZFj5AoNeDYBpRSDJS0uapg2CWM9Kg8WuuRSrSaRFoTAdZorj2nA3UyMNcDUhRoGJGQvhnqpYUPv2oq8ZUrDvsWT+Vru0EqmRTD+zMHZUo" +
                    "e2mJUCZ3+iFEzHQrXUOqB6JdEy8zQ1EZ/KSc9Nog9EnbGWXb6YR0jTD5mfc+OPMbtwQZDoGMGgJeSgWbPBi9dLXDnQceRyV2jmFyFbz71Q7i42JYn2Ev6Ji/" +
                    "x+bnogPdc0MQAjYod+U0/sS5qjS0qlTSAwZEcC+riGfgZW9BI6QHlEuNbNZhj7QJ5h6Yp3MHjDwIuSrfopY0QThWkwZcMrdjjncVFBpvu2rZIiq87fDE0MVK" +
                    "kCrVuSAI2SJSYUwKpBJ5/j1bhKGL6bTKEkMhvGBN/NC2/1y07RkLwf8ziFbTQbAAkQrXilmjUeb2DzlDrs0ll/hAx6ogX3CqFFkPhOwhxa56NQz0vcScfmY9" +
                    "gVOO3VbSPQGz/R29ynBvai/3AjWb0T803pjqA7Su+OarxCg/MIMh+D8OU5mx/5Po4o+QP8OiImxMfmRZHRRr+i0LUcoKEGeGcNFzMjNqTtUmHInMbMnP/oCa" +
                    "ePKluY/wpo8nQCooN9/xSUbRxPjvt8ipVo3jiuvzjoe3XAlp0TAOYWsZ9zKdTUVR/oysfl3wJfrfHEDGBPSX9Vjrwuua/BmXniAuYpgL/CwryV6grpU59wKd" +
                    "khmi6Dd+Zcx1bUBMtGoUg59dKdMy2qWApshW4IyIEF5vBXJRxMhgStjrMDw53NFyVZgKRp9jb3jBD5H2jkiNOmsiosY2mYJn6jRITKk18qXbLSOZcOpmGWkU" +
                    "ex/DHeGslLWa7sNjdk2kiwgN1xwRiI0gEXdUihwSvcfAKAgvlEgcLomSFIEe1HmbgKOQFXAixv0n1KqdFVGlP6ClPvNspzOYo80TMwbMQrGflAvubqa1kXDz" +
                    "7bKwZwpWCBcVlEzefRiY7VekClbYVohyIV5pa8dpHTpfJCWs5v0Pr4VHfuDJPwk8iZEQXrPqutx6zdBcKL7k/jABR0ufQl2eLlSKGAwxKTIaSkmpuNo0g3Kn" +
                    "LO1a9/OCeNdn5umzdRfuagS+UWXiWYo/Pqkh1vI486wXJoGNstZfqHm20N6CEC5nRIT0loxLuTKRik04NOpgNSaOOysbd94LIdJVjrL7M+Cq70g6OOyC2rLe" +
                    "+U9TaCNINXYy9gwnLNtpMuxqMaGZRx45dZZC2bubT3zoKmoVMPs0+cR4CK/NAvtzANz9Yc5Q6Q5gFPKpfpOvmhyJG+SyN7c+LxcwMcFe2uywPwyZ40i3JANQ" +
                    "I/y/6JpPfrwduukuh0Xg12Eqsc9h+ZoPMYjTJqAbFdfzEoMvDmsolSRUdTwCIyK8FbCqvfGoI1tUjSSRVtshHSSptlm87UiPVWcUi8OokLkvDhdaqhB69jIw" +
                    "XmJmII0Zzcngx8ltDrb0ddruWescGmV/H25s6xx5d4XzJLL26YafnJmsULN7OGAUhNd+gT9uHn8uN48ZEeEFE/cHufyXRC7xjoe3skTCoyEYnHJNm3pePzjR" +
                    "fuY7H0Dfmxrfuw1fiYdopdafKy46WumUToPmAsbCltISlD2lOMGpJi5WiYe73eD8ZH8nUv2ZcKE/I3eDdztyryvl9njAMAj17SnR807tjUaFdOBRzw6ZlWyy" +
                    "FRZWRV08zG3RQOXo4rETjXhWIBd7OyR6gMGyhhOCdohmlyLVBMyVL7l6Rnh6RtiLOXjZBpUDsHKCNeO/hocXbTQMZ1iqjcbBzEVua/QzAuMhmreD8H0ddLMb" +
                    "7hkirmMCO4tHhqE0UaxpOCkBI9sa96zBCHfrwVxGAK1CoaQ8KKGGI1vGsPXgzTA0XCJVuizR1l2APK79NbLMNAzl3QXWDDMM4muxkScUbDbvikLqkFPqLnQE" +
                    "sJxcyGOmEunLPZryIzRZon2RxwpJbSKD6Y7jLkG8Ja6RhplGliu3n6AeIFVz+wmYJzTPSH/yjFSo2EJmhODaMxziW9Mnbj5tGEjlkIs9FaswUxALF3scbu1d" +
                    "ZmBdJl/PUNDK2DMDbOok3XqcYg7hcVXpH5sJhzORLmlEOr7Yd3Ygj7eLfdPxyW6bnj0E8M7Htzy6bqMw7nccQjK6Owc1IKfd5d2Sp+YCRykxUzH7dv+X9RhH" +
                    "Ww7OmkrXzBi3r2ZEupqhC2aTipKZ7wj7LyvVZqLuIkuBB69khIldExdZoXw6oyC+tXhSsGvD512Xgy6kWFrGp9Ns4fZ9z9FpcCqcmWxbUlAWfkwMCwdl69Nk" +
                    "qjICg40DInV2hTNCXUSAk5p23UarJO5OU43KX6uL7PZO10IBjpUPpQcFlg/+R6vsR6tM6IvMoIj+fRc6Ucqa9X0uXfWS4BJrF1g/RsqoKZZWwKToE+cvq6lS" +
                    "VTlWr0JQhJcSB0v6eNA9nZ4c4bqE2E4Nhkv55Y0A2+CG6ohcTMI1OqUwn96KiHlq4J2Pby17NDOnKUSlMq+pQlk/MczDzpOJzGQiV5Zpf4G2fkbT16eyEUlZ" +
                    "7ZstJ54ZfMPvUmRG01KfSmUctLyehu8eHHRRw2xuThcYFVPEPQhHCCWRGRHxxTAcOFQGN7jg9OBtJ6LWkmWJ8jKwqfHA7c1TndOlMtfKypFZRJtZ6RmxiHRZ" +
                    "EqjyXBiNnp0N9pfUALjNAl31vZp5H73/VD4lJVqzhQxNDCDWWU46DkTNmx5/Jt4/E2824pwREV9MvC1MvBsmNebemWFSwLOsS3SqHxGFEmPyOBty4U3FIrI4" +
                    "sbbF9zlh/0LPClM+n0yyslz9t50j3D1BR2KKbifWGmwMquRTqgIAggkjvB/jhfmKk46gx6VO91RLSEiazhsey3vxSpEUqmM2wnmfXx2MH3XwSRUTWtjhGm8e" +
                    "9/6pptQ0etxsmQgqva/hWIk+bipCmjaIfcigPA6wuAAdt22zvyZuW+hAkx4PGADxrS2TOrriIhBmrxtR0RbYk49Ws0BGqPLegXDcHPXh85atZtFOAvc5+KRi" +
                    "7fWKVoJz7mHBQjkX+MJeWAt79lLZdouJtJ0LfizsmWs40jN2w+B2C6OuHqRWFaucgRBLeQtHjnLRGtzKgVVeufCvugSLhT1/Zr1EmGSuwhGcP/0ECnKY295r" +
                    "m5OoStWdgC9ETAmHvx7R6XTSipETinilDJK27kE3dgyKTH7aqwRh7JjoRs7SLq3CJXgWCRgF8TcYMbn+4S7ivXT87Hhk3K2C9ymF1k3SMYWhZLhwYMLA0GJr" +
                    "J3cxqcA4E5UBDZeMhw1oGMmBMePgxHAn4bm02SZcQg39bhDOveP4tNA5nrYJM9FM2r1RxaRmHMTXlIcLVZBZNjL6PVnLoaZtV0rHldaSmx5EZCt7cGDEnVY9" +
                    "j4ox2qbNiKOip0Rx7nplT5eTYhohW5MZzlPMXT3oIkdEvZwkQRE0GkwQtCjY2utJAkMhvRUYa3HQFzqNJgaRuZItQg2AP7VXlpH66UKnOyEOUxtswU9QBSSN" +
                    "J6lCkUMKIs3pBEYUaEAsrOGgAaEa7bATIx2M5S8ECPPF9oXRlrXc2ZTrqI3fj1mFZt+qyhkGybyEnEDq2i7RmOXCtA2ziatOqhdO8mW0BXONl5t7wkZWOooN" +
                    "xt7txC4gCOYIiX6rsFfy+sam4T7Q/Ukf+LCmaB3PjmxCBz9yn1e/S8v5q0GVIEhusqQGadO7LGnqdx5ueHrNc2F8BsuWeBeowBhx/o+OtKlhhqerl3KwDYXk" +
                    "UZe0wmY3dJkEfHRSkdTNrvUgab1vdmugAydL3mrM7aNt1QL4UgffqbzpeJZ8QC2pGRPJvlUm9SfPjHgwbgTTLW/oasXVbCLKARWOsz2zZivzjNjxh7JbssFC" +
                    "ll8+0MB1sWwzzyrlo8PeMhxsoO9tMjz3dd3t+YRscb0lzOpiMEHomocl+zFvfXI/jo0/jo1/W3GQXPhRB/pRB4I0gcGQ3rKgcqc3544ZDrPGtWLR8Qa/4w0M" +
                    "oeZ2bMeC8p4FRXXu/R09ViDUkB64CxtDIxdCrZEjL1du8hPbzt2mUdqvoczgk51duoknPX5lM4ERkH6HWlDvKv1IEVBNJoY1BGHDVvdUgaYYN9t4gZ35rLbk" +
                    "7AbgPh1o9TDXdpgjiCKpk2r3AEGc4cnTMt6263+xafEUe4gCe9D0SeONUYosKHa5+xkVKbw0atRWcCazntsq+cV2gukVpIiTGziOtMd63kgOohDY1/NADcRq" +
                    "SzcXcyyq+1CeejU+Wcey+zqWzqYvK1f4mSvWOpa/YFI7CU5W2X3as1XjDIH0QiWmx0LuUZAjsfjtoVBHsrDuMNb0VAgkj0jINBJgkY5r03IPPeayheLmB6vn" +
                    "fdlGtXoGgfw7tiwfaxrtyNi28ux5K69sy9uAQbCUUJf+x+WRYTYyXBUnBg41591P6fWJ4QYc6fDEeARSMw2pxPjT4QRSiz0bAKSA8MA38Q7oQw+A9jUunWKP" +
                    "e9o9NwSJSgJXVpJkVWVaq6IPJ2K9p8q0ls+xHKdQC02YC8aD4Mjah8JyPSYwFNJbeQe2kFeoJIxBm0au7pAXjxogiD7Qqg/pkX6HKBMjRi6q7IkfeSwwHcW3" +
                    "YJMUZAcxLk5GSvpK94k6u9WXTp12SyOlbftGDrRE+3m3mqcNtGZApNcUiP5XqQloUBS45HJSUcyzElezzg8EzmdpCcfHGnnvXMl0Biy5O6NY7tv7z3wl9BBZ" +
                    "ylC5k7/Lg08jyB0AywuWFKSITHUeKDKB8ZDKe0uMljKG8pwjA/BBkzGDQWvLclwTmzjPTDt9D3/fixagT3rNTimzfb37hEG2/ckqL9Cr1y6OsESwR+F7K63X" +
                    "7FkEwlARiLVW5bdlHHsw7axCojQsgoy/s1M6zDgwHFJ9eZQwEYieMDiHziMxgreinqtTOjOdO1u+ACgz8JNkaDhcSZ1zjgzRFyvETonLi9UgpmZQU0APKsrN" +
                    "A0Zx2L/5xBNQIkCd0so8oS/xBjHzihhmZPy99OaQSMsQzFVbEEGY1EQex4VYcRFbRGAgpJreUuyBDlHY4JtDFE4UGGvzv8fB8uFTBIJiLxiG01oN1Bhj9Rsg" +
                    "HYCrm/vgexSYRMKSJBfWdyClWpcpDXr/abT2czliaLPPYykRn6vDhEP7ecgMTus/8fbnFzwIK6AqwCF6AIwdXp+FUUpkojAX1HoIACfWM0EBZpjAU1/npEVA" +
                    "+4vEQYWJAsSg8wudQssnWo8lgfythTOZaEXurkep1FmXBDIX8OQFg7ZVCfNW5xdKH/ZColhIVe9rFRR7Urb33ZKmhtHjbuXd7nw5AtRx1IeFSBNnIT57BqjD" +
                    "uRo43/9wdf/jAwtvbfXugi7LF7Z1ewttvyr1AID7nt9SHvpK7Uj3Mayh1QgA2I/sp7+DFVy0whFOi3PFoo04oItQ9rYZ7WEV/2tVChb6wiAtwEK/MFnUJpxc" +
                    "u3rTpEX75tHyhqICD05dv5vVQWBdRD0L2ruDoD030DrToSjyYGeA7FKCUTAmCUI94yLbl4zJ3GdMuYCCNZlludlBqAx7o25mXugU0zFWQmocs/K+ZcjxypM4" +
                    "P3M69TNKjNzBcppMVFyrmeahULFKrrZy0BmOHaY/DDop2V4ZXiDZfkZEduZH++PfXvtjRkF+u8hf+pwIdrkr5U0WrvrhL3dx4gNfNcXlnauGEZpU1IgQ1J41" +
                    "coyyJY1E2TaCKpMPNcZpJYdjlA8pMqyxCAqj9lmNcawxgxCjVWpMDIccXvOr7agx7YAnA20zncFswfnVZfGrQdx8VhhkpMFxB2C3LJGGSZRl4oKIVu66H4we" +
                    "0y4QhfIsjjIuBGidgksSkz3Krw7y0PDcUC3viHXe2DG+PSNg3gZ1QusNF2LttUMjnsQFNWCypwmMgvyWEaGokDIACvrNKpSJwzo/KG5dtKlnlcLEgU+4dteD" +
                    "sk87Ge6UgVFBFORUpUmrDrYiMq51swN+amx0e0stcrhBa9UGGP5smKT7s/oT2b4K/hyOL2YM5PC6/3Rs6cIzzIHIRHlIBKgCxA+MDXMwBHRaRHnY4OcNKHHy" +
                    "3q15J+q0CJlhlg7LhnVDHUclFk9gE+NNLmNeeAbtgezgOdkhiNrRXy7miSm3VabcnmjPfu8GKKHfeWw848v9/dJd1IBUPQSkKyiF9RBQKHJChHgTFcUGgqgF" +
                    "yiEEVAQ4w4S68YboQJ2xOOII7PwBYB3ocWEzRvpldpxYtxCVAeUndPTpsKtrnoBP3IPXK7PLeedz/FGp/1Gppyr1MyJyeikrmStxzAocdcCNXbZzkYVvN9GF" +
                    "gv5yayw9821XufYkafjJvL/RICYlwgmq3JWg7LWIIKU6eCVZaKYWjjnxPhAatdcqgu6XBV96tsBIyOV3tRFQKhQ6pvJmYzlovpu2PuwxQz+dgPMQuGA5QFJp" +
                    "3ssrskOFbuJCsBwTBYAOBwU5tamQS5tJWqk9td3UZlf3SNRhOWsXiuIo9owNDIlcfr/sqOWy5XKGdSY7XGBRimx5mNUBn26QgVa8gyijwqkV2eIcFxs/Kt5W" +
                    "GMpQMxDLixP/ng813d1G5wPlcm2js58jGA/5LdtBWdqblu69zDQzMpA3J0z2mH55OtMdAiHOJQClapgVqJeQ5e7rXlZgWKow42fDAWMN6DsitAq3gmEMhWJg" +
                    "taJTvU863VZralv+d0x7qRdmqBfrmHMZpgTSMwTe//wbhB/Yau9hp5exaWFvz6MqcZKQQ4sFjW/NIevaxdjPds1F0xFTd3vJtCwIki2ya9tZ4gTj2m3IdVRP" +
                    "j0zJte7MlGPVwPRnZcXm7qyXBOX6YmNr91zkmNScZGBQlBcr/xYWthYa1euLUoTOoNAXFOy4jRZXydqFp1rSm5IgVahOx6PCEhiqMHYc1xmtWsNxxiPMeflC" +
                    "F4YJqjfSzaJWvPDCcWIS4sbd1rxwdlrcvPHFlLckqAlSgwIxFRj1IkMQwVE6zCrnZoMKBTniuA0u7IhJgLRgnPu922YWVYwqOMtSl/6vR5tJWjaXW8tmd4Yl" +
                    "deFR6re97+3F212cTRPGLFqEE84WpYcDREF5S4tg7NkhH5U4S7KXDUmwJEW7gf5IYzRZxu0slmNWNE8QuDIKvxOcWoAXK1ELqhKXmFkCkI3co80NhmOHK0fh" +
                    "EcsjIVrDVnrzU6MTEQ5SaDBIHdp0kR3W1NzQ7OAODt7IkZxhUN6yIH78Nf8E/pozCop7iUXkrvDKQCpm6w6kWbvjlrDyP/d5b3b++ToO2eQNMgqu13HEMEuI" +
                    "iXGelBlUiAOfUsUhnLJ0sSUKL6VpWaKIpJT0RCXwPlGke0ski5ZIur7cjIfyVg8i97jMPVvkcWJYPDEmYCmt0wRwSfOEikVUOuFaS/9ZUx5Mc851ExLQqwL7" +
                    "AfgT4P/dssWoUMPoLXy9QiAeiNTa3XWz16dBrngnmHR5hf7gtWQhpxwrloqy1StTBXpkzUAob7Ug7k0WpWwMtzaodYpQ3vDnhkRtWbSYyKREsd1MbKdTm3Gg" +
                    "fHXsHwgIzcH5QXxYF+cWJMekUm14aIrjb/WkjvrldGrZdWpZWFwxb2+gqYjTL7GgNeOhvHXFYZhlomhlmRPQfmqkDY7A1RsiRHlLl7KcV0lmX4vkkjs1t99j" +
                    "q3olsQ0twq7kkLa7sLywig5A2HuPpLnq7bu+1H0zzzPELvfNWWx+cwnjm2JzniHfdqs2jaDcoiDlDIjiXyqNTVGh/txZ3PCFoNCYEVJyDA0PLtc5180MdPXb" +
                    "STWA44SjDhp2MERoalvUA4YNEiU4bunkhq9BBNtu58fGlNBWfMPICzPpU4p+5LIQ7lp0zG8UmXLUON8R7BkDJbwdbfRsXEZIGJomEiLYvLDYG4+pKfQLBGzG" +
                    "qwz6CsdR/r4AbDmGXgB27CMOrhdydl40v6gtlE6EW3u5u6FtchYtMK60ha5Do2NWGBElvlS3Zk1pEe6bU1GIAVVxzkGlL+uJfk01CfOmHQJrYLj8bc/rvdxC" +
                    "ixDxtqVvzsyXKpVrymFGiKh0uiih7PyEXclxK4iJcGeYE49matckbCKAP0OhpJedaek/rURi3TytF7mLWl6kup4nMoSIY3Iy5rz5re5rqJjVTWggDZtY9oWD" +
                    "VgTRwj/v+mkTsEsrrWsiNpU2dsoITNYVT6QKVWXjfUre6woMifLWTYnOwLC64E57aS77yp0NmIZSrYhwHobuScPSpME17eNZXQZVj0kPkmepSdi3a/ClkTH3" +
                    "itM/tNnK2+rvBdqt2Wz5owL6qeLUFdAlatFrDIyH8lZepoMRXUTsU1r0gi73wrD0T74OKxVvxRBs1Zx0k0dFNstCLHBCTr0u7J2cqe9vzSfg75G5+lZRHIdg" +
                    "7lKuMB4Ebi+ATd1Q51rkeHCsrrSnAmtKjeRYaRtd8/6XnH7sU/7t7VNmFJS3lLsfa9Z/UWvWeefLW0OlqxEoE4qJwhOh/primGNlJGkZYT4e5mD8VEUSFi5f" +
                    "1DEClTgLRrjnNKoLFi7b+d0cErSdX7vZsV7Hg45aen0dvJ8QGAblraPSlYFOnlMvFZMq01QJFGPaTR8PZEonIDtdc7GJppTimsGQ7EA1AqJUOb7wXDsJl2rM" +
                    "7PQLNnyRTziEiK3bFCsXlG032wzqqiRnHOlLd0yZsVBq+lkE/7dfBJ9RUF8bKtnBpbIWq8iUl1nCQLBDOSz8KpIi4Np6LSmSyYpeYZZKy2mrhYVKva13zCq/" +
                    "TJyDTptwYqVnsizNHhaLNOEPHH2nMOw00oThYZE0JRE28IonkmUW1SSq4s9oqG99lRR9gCi42LOfoFZrwKrLWgT0GrhXqX6sdfXaIUzRDxx1Hu88V7imfq0c" +
                    "ryjdYKsHwEDG4ngjMWt4tVmFg2wlgkQm07bwqez6qSsagS76FkaoW0nBqmA1s4R3AmPwYw4e+90PePffgk/UVSsx6lSZKviqyNA+97y0WAuV9AKGO6kQxPpi" +
                    "xy9AJQn7PiQFBG3Hz14JAfDWonCn1olV+4N8zNZcMjedjXbdkuguULrhkV4IlJbNJiPgAHxf2pmBUN9yLBUSVaSMGaEvRPxTyNJngQU/EKIcGEGuzIiREmXo" +
                    "wo3TdEL2g0GRMjZC6dqNgUMXoBzlQtQVSd1hsW9mhqSij1bZ0Umb6VqcElN+MiGIhrHhBJl9M4OTKzX0kWYGvP3VvzVpDWQHXNnRkSatq5+g0pP5KEaHk266" +
                    "mUMRAj/biHbJs533EBxjQPZa8AT0EQgPB7ApPNWiu1emPZrvMbDJPdjVCg/cvJ3QIiRdxLz91b/n2LbWMoKUNeXYRi53b3a6NfNo9beqxdQbwxKHd+6o6Wk7" +
                    "oDoiYOFAAAv/q0pjosN0KiVm8q9b8WBOY83Ij4pBiaH8a5oizEMta7oNbr/ESZF2Qsw8KTAgajCvbZxHrTB2+ViGmDwYKkKXcN83sxIS1vdGhs7pzH3Q1Yo5" +
                    "Fn2qJRWFgCCM3oVfr7awY7dJd9h93v3yeTenYeaqJ+k6eFSgaeTPnTnYT6aZbFbhKPvBmRYUGAv1tY7Qj8nWv7bJ1oyAGsqPq/ePq3dHGzAU6mvLFGTMDUEg" +
                    "yqEMYmilEK+Rir+GV1dbGk7ufkejKRGGCVufiHNhm2EEaYFACk1/ZbQVeMI4UK8vlUTCecodNtFrRencHhoOdy96rNYTGBI1p9+27LkrEtq6xtzIv0bHFFzq" +
                    "442n6DhD3my+rRAbQqcUMtauu/2e3d2+Qdt4vJU4apI4SojrpT6nOqSMOIjS7TvrQLU7iQxRKUJBewmX4hBBMmuXBFk6+K3Nm1+L+fE9+Pf1PZi3v74Vj5ka" +
                    "hJANPLNPEhoRkjVJOC9kA/x2vTPujPtVO6DjO2SHgzjETBKDHMf1q4KmTEmc18rdJvjJodNcuPnqh8Wd7NSlboj9FYdOIjs1g6I65/3n4PCpxJBzLO0uvisp" +
                    "BoQd2XaG49kCoyQuP77WlRiNP6kFiWO6U8qyjmPSIRf6U3GDrlZbm5eM5YqV9rO5Sr59Yvk8i80oR55/RH8qXls+GxWfOHltFenPN4Lk/wMFVyJMx1sBAA=="; break;
                case "16mm-3": encoded=
                    "H4sIAAAAAAACA+1dyY4lS1Jd019xf6Akn4c14iN6CxIICRCC/5cIt8HdzIeIWxXVLKC69SorMyvjRl63sOHYsWP//K//9u//8l//Yf/yDx/zCab972M/1tSY" +
                    "PvYvf/efH//J13es/cDXXfnY6j6ufXf8/S9/999//Vj/af/GxY/L8CFcn3nzieH6kD/p+mK0n3JdJPtPvb5Y2zXsxwX/saF8nHUfm65vl4L/XT/vrtco9XqJ" +
                    "eH24XuXv//FTrxe5/tV11QRXyxaukitcJF+vmcMnXpcz9hPCdXX3uW6iXcGbdoX/un656xLZtdu8/rxeJcd2d6XdbvjkAneRM1zo+la70PUi7VZihgu1X/if" +
                    "/vr50d6TH9dNF/xre4Ouv4TPj+vX/WHp+9dPXn/4T6rXW+nb59cnP67Xhqtcv5BtP3Zd5bqJ9rH9//pwXdNf/9VPbJ/98O0L16953WT7y6e9ZdfH+kntX8OV" +
                    "rl/sA//yuhK8uKdPr5vw7VbgutfX2+1e5xGvL7V/YtvLX8fUvmcuMyAjKGwL5tfsIMIb3D40A4geTv461uvk4aTN9V5ebzec/PWLwck3q7jedHudRnvXwTra" +
                    "b2McvP3wIvH6gJYA55gCmEIAU0jt6sXDxQsZhIdL0xVDggte7ywYhEWDaCZV0AYiXKHAFaqBKxS8uWZg1zeaQVwmldCkkmFLwLe64gni5+56x34EOl74/Doy" +
                    "m5rRgZU4PMOyWELCQ6fzu6552eiPTJYQ4ECda/eV2vfhwJspxPbqWZgCGJWDk4cfvL7v8VNDJsZ2AAZlwcJis1l32QEbgPtlQwjoENAQHDxq1iV0CB4cQsB3" +
                    "GgzgMoZuEN0V5OEKwBBMN4TrELb2MLmG9kTjC2U0ruThktfPtSt58hABDSIMg2iPfG5/NgcBd9s8Q7tcRc8Q4JZS2duDBXuAdwodg4PTyvQMSsdQwDGY7hfy" +
                    "7BekNcDTfJ1cAT/DjsG1m8vts6AcQ9SOIQxrwPP23WsNe/Dwz68/HVw4Np/VwkM3hPTSIEIGg7g+gGeAh9PiE2zxAXSG7MKTE4jsKALYhTPo5i2552ZVYBAJ" +
                    "DSJJg4AIlOB5ac6gOdEIB9ler9lFxueb7ALP00c0iCg8BFyo/VDzLkX6BnJtl/9pNnW9g2ASHk3CC5MI4CLsp7vjBB4Cwwge34/rwFtwgr9uPYTtHoLcyuIh" +
                    "KsYKiyahHQTFCtd9jaXnn43Lkc1KD4HBBCziyhIqeQi2BDSMNy6ioIsomDM4TBYcJgsBTeF649BF4KmBiYCLwHNrHgVMwWLMqB6TB3ywS5AW4dvLJHiVlOBt" +
                    "wnjU7KLFioSW4DBWxF0OAR7Cg4fwHHTaW+5U8oDRpsWe6woxyeSh3Uh7xwubQ3cRRbqI9vCFljsEnTt0H2Fk7uBH7pBb7gBBBuPQdW7trszqI6RB+CligI/A" +
                    "3AZ9RDv60sKtgTvimOHIR7Al+NdBA31EM9oA7h8sIqBzIKcQIllEoqDhKXsgp2ALWkLFmGHQReA5VoMG0e4qQqoaIUW9fpYCBxoCWEBS2YJMH327vRYQmvVk" +
                    "9CqYNXCUwDBzPXlgAph3xKKjhLQB2x5iyh8pTncb8Acb2GUNhcNEFGHCtDBRtAnYff6IuSFlG6cw0Z2CMAE+ep/eJpAGE0jDYQK8LJlAorwhU3ywUyJZKD6Q" +
                    "cwj4/lcsJWo9JZAZnmX0ONX2tIHyBQuGAt6ATCIJkyhgDAUS3eZJKINsJuGmYAFRghIHNIlUyCtYyiPxjYPHrjmFOpwCGgQEickgojYIiAd2SSM5b2g2Vpu5" +
                    "JkxNOdQ0Z1GEQQT2CfBq5BPcahCR/gnVEz1KsCGE8MsGYdEnBPQJaVdZOvIJjnyB0TUkvtkF3+xSRil5XaM9/AkefjAucJOZk4SMDidgBbKrJBMEr+vxpBSx" +
                    "hQGsaDMGkozJayJXgKlKxAQDzmyc+y5f3DiCcC4kPTkCyxG+inyxBwMrHYE/FpKBkoORL9rp3CkWSEfguY6k807lZSy4jB/yRTzwkDBfNOgPHPqDOPwApfRo" +
                    "BpX8AeeJnuqIgDEBD6FGYQ9wmtefWEVUzPjamWLqmRCciOe6ASJXBrPkH40YCxCUyOhJCFLYZYleZIlsCcFISxBp4p0HOBeSnr6AkIKuHJY60t97AL8gCk5X" +
                    "kt0CTHgZEzwUZS1DBBeAtWTACiJGzBcpJFx2ss8O8igl13wAnugIeWisIiFA91KooMRLiiyhuQb7kCWARbS0gBxEst1BOAUyqSTBHHOEMjAmdg3ZPGBMi0G0" +
                    "wKJzBEA68hc5ghM5gnYN/pQjWC4luyGYt85hgpnIOVQ4qutsGW0Cp8BFJIELtY4iEiwCHXSlWlL7BjQM8ESxCogBs3+jUj/yFLVDTs0uvLQLcCAFbq8gClrQ" +
                    "pqiAaB8KlKbgKrCWSehsUlgKSjcsA5MFQ57Ccq5gEXIiwyg9eaQy1GYuKD0DRZVyBTjgArlCM2EBPoKp6lzBj4JSYo+2x4wMt5M8JyYMPrKrGAbxHneC0NY+" +
                    "UFEJ6UJBX0EIdBkINPgKP7IHqC0TVRJoIWQZUxYBJxrQMqBOSQQROE4gGJhuRhEVML3xGT2diOhoBIAVJVCRsLpJ4aa4FOkEWgYAhj/IG3iGn+LBZxiNSyP0" +
                    "YHRx6eG7DlJn4TQK5hPbKOKG97F0M9JphE1h0fKJYRHhLd5gMaNwFiFJBKJ84h4FZBRmCiOZTCNSYikK0XtEciovRanIcQTdfyBEEq/k8UreSdNIYBpwuzky" +
                    "7CCKTQoniIRkzHVS3SKTZTaNMlcYT5nmNpyUb8KJ+U3hhEvOYRAvagzCJhPmmpRZVAwnTmGTdcIkTc8xMcGoS465hpOKyGQ7TolPjrIxIQqRAz/hU6qJJSdg" +
                    "WFSvFgTIKLUomO4Ug7aASEZ2d8hk6Q9jLz8quW1ubFHeekg6VSgRSafdJJ22l50rNjlCCbVSRv0hs07Cok5Z5zCG8DbJsJh2WvQXFkEph8YR4oRPUgwxVHqU" +
                    "QPgkxvJalL/AAFDtCZ6kWtTPoYTwyeYv4gmwToBP0uUw+4xzB+MEVy/wpNPQlH0BTZ3TTgFNeYMRJKx+4ofteLUdrc2eXqT2qxgJWI8Q0i0h/zo65bASQYDS" +
                    "F3QU2M6KWPJdvwk4Cso7fZm6mlMza2MDEa4ewdNHuFouHZXAkMFVaDMBfwwZu2o0dIx6GyzO3awVqxb+4bfXIOXrGuS2KJ3hiV6DdBMo5k8N8qcGEYGjG0R5" +
                    "nU4YBV25GagADNtNmSY1tio1vyjR7yQIfNgruuvqdaYJ8Ri8BjzvKasCwvX+VssrjOJCeNHpDMSFYHch7KrMzqMZBXqwZAUZYtfp7O5CY1jSXbS8S5Qf0Ooc" +
                    "tBhsa1Sixeh0ImsUW7U6JYrtblDsInqdwiggdHRbqO4liNWqUg8fIKlIUzYBbQIuTJkXwXwIzjGJJuVrt4nFGCBZiXCUCTwEV5PoglLtWQQVpMIYBJ6ZIhUd" +
                    "EH5CTyC2ZYeDdxCMIU7wlVm6nFY5iKfIYTsTwqlyoz5GDkGEcGu5oQlST5EDTKFbQH1LhXARMYrI/W8oRKvqdhJXys0Qtx1uAgpSR27CnrMK8EYYpmKhenSE" +
                    "DsKnGz1DJBV+DR2pEjWm/WycW1xEkmpJBRpVNpukwkjunMI1KyWYVrS6/Owk/JxTMBWKD7Qh3QW/2nsexfVKlAys7IuOkVSYfa8r6QQTel2QVFRNiXgDdWMl" +
                    "yhi3UZwITzVHntzE4EY8dr5PEaPMTqJFDOqB2h3S3R7tDMkOw+MUIxAp4T7H0QgETuU0GEGBAmlS2RzBCENMGcmBqAxTJe0dClSkn6iKjLR6B1F3mgFUGRUp" +
                    "BH9S4FT96N3bdkdLDj2cYobnGopPTCUcxvZgNJO2TBVHfUwZoJlKziDJdpfvENUKW24KDapYELa0otgshjMGrjMQHARrwAsl99DqUGWGk8HCoJfPJ4bUXGUE" +
                    "lTYgNCXThrIDLS07hE2s6BWnokj1XLLbgDNvCbUWqwxLPEpqf6NfCJRL2jI5gky5gqN8AXtVhYBsLAuKosvB5ZExO8gwRLDWlkDUmAPBOkG3PgH6nopEH4ps" +
                    "jpOJRkQxIpYXMexolMSYZCB7IUkVcBB7HqXMHtCUzEywzsyjBAcx0gcv0wdLtN05fTAntNIvaGW3hOv3ftsZB2fYu6IeOx15giQgiwjkIQilCobgbCbYomFU" +
                    "fMArHkOVrLmJKMHZ4MqtrjfEWqw8LVHvIxiD6IoSMlEkey7bLTy1cxVJdEXtt3nlM4xdb2Dsc4MjrIVnmdApqxGJbg/ufYMDe182cIkRsCEWMX9PyKtSLbCZ" +
                    "TEewZWBaZT0nEV5ZxnAZnA9E1QELJz5lsoRXNrvwMgqtnLrR2Ij1AFI5bRIimbgxCbMh2GqTcCqZ2IBU2/Bx4tBQ+HCHzka3BBfeUicC2kJAIwjYB2Xuskcn" +
                    "kTV7pnc2EoFUbrBoVvC6nsApcBWJXQVDD9TqWgBLrC0qA5bULTuyaEznVw5bWGoLQ4mEJNEgFhUk1/qWRONUJpF7+7PsSDSnTMIRt36l1goSDdyaw9uRqQSb" +
                    "gMvuN/GtKwYMP5FsocRIZ5ItZBQEXKYwlxqLe5ApZmfTbGk0LXzks2lw3NA8GvQIEst2fTJjMY2X/c98Htl66yXcglguVWeQ2JT0EmwQ7jVg2drcHj5AhuAn" +
                    "L7EgEIpkF0ZrfJraOrXGA5WfIqeIikHZYWxwG7Y3yhfbQKZVYNsY4x1cf9S5z0FF6anP4W77HJCX3KNVD32OnybfyazCTVnFDfmu24R7AVxiz8uS2zBqkity" +
                    "OtEJuIRYYt6Aj3XNioeLc5QlrzSJRHhlwVOnwsOHPsjnOxH3VHggYAl3lwOXtKrwaHdGbXrBylWTndwSn4AJnt8SlSjVHQcvYefBzrob7HTtt6zaS/h1fksT" +
                    "83fzW2UqRd3g4/bz97+DWiVnfK2iVgVFw4zc+PIjp1C5BDe+qABBALoW3QwddEzICnKQrQk+107Q5TJE9MPRSRjCLbHwIPSSYMsc+ohnVpwJ6njlO4JuhtzC" +
                    "dBfxSJUQsOXMzx3x41sXERi1lFM7XrgIYkqkg4voBuFfD224NE1ybQBte2BWUV+jmjG+s45/r5QqrygSutNVOmB5YtthummIbZeypP9vx8BpoKs5i/w0xvPT" +
                    "TY6pGHXHMZ6lyXHkVJ1yiswoxb493k3Bv+ZUDSQbbEIzJngAuBxKj9wHeXb9cT3vKaQBHLmJ1fsHxZzYwRNwu2gM9LO33Q2rGuNPPH7d3gjfTfL09satn9A9" +
                    "0HMqwdD2PZE/7BwF24J/Takana/bEoSbnzzx2Y4vEo/Gok3U8IRpBshbkMyXDBgcJxWjzdFUIxCSDLUPfs4IRSFvwWUp4Zi5zwlxPYp5TlSOYsvKjfNQ+IOb" +
                    "IBjDBnYTfcgnDTfhuPRIc1JhV7LEQso1Myk3Tb1QN5od3RB8fM2oSlh6oJtgU6CnzvaxP/ASkbyE7yyaPgbOXmKe69mzcMU0uFGlhltYEUSlgiyHcSnXCTOY" +
                    "PkzzPd+QIjhvMNu84ba5MUMS2h9k6n0Nf3BkyvgbwHLTCO/ugM/cv6BQ0eEjUcZjk8uHyR1gjPCju8VUbDh9t072LK1vHumJgD4RoU5xsBnKfgAhwig0i5kZ" +
                    "MiJVcGrw9wGrtFus8uuMIU2Dv3VFIZYs8iFjCDJjMDf0/J4wsA34Ev5Ix/z/lo7pJuDfM2RmqFLSIOKEUbJ6jJ3GdzT1ekuHqAOpVDGiY5STXzjxbXnib+BQ" +
                    "1Did+LbujhWxgSjjTjzmCaK0Wj3GHUc0ypQ6Pkz8bZqeO+a1VAVgQwivxUI8kqUYopyngjlP2HY7mW3LzsE/dj234LXgR5SZbiuwB38aBS098SCAcsuZ2jEr" +
                    "/1aV5oGIbVZdoTuA0q9xY+16TklEN4jg3jIkNCxF2iEDn1RMGVFTyOmMR34ENTsjDfoJju0858c6dKOUcPWMPMh+6SBI0LhfDj2fwDZ49EedqUNJ4Z+nueKS" +
                    "R7SjMyv0sFORiXfzXEZXFe5EkejSAWwKwb8l2wb0FiHuBrpCb3tCSmEGR38SEXlgUe0nuUTjOvZJLkohDuGjeGZekwAJFRlCYyrORcaSXlKd6ajffaM0lSAc" +
                    "CaUpSb9mERHJrKRprjKx6lhpatv9dN28pjrTdU6dZ+w6b3iV4CTYDsJ7hBI5Mw45M85PcnSQV9gD+zop1nWpx3FgYtoiiwoiS+5yIuAeiHXtn/BJkLBjyLpS" +
                    "0SEGP7OiWpqOQXylMqSChpudxKn0nKAoTcc/jXKpZoadmxkLr84tvLqJZBmGqkz5TflE2MGTCAFSoQfNDNXxZHkhlhdxauQzrNM7yG7pNYck04cOPBAJmzKJ" +
                    "E4sKbpukS23veFK5kUNvY+gpP8W4vJUbMXPD81ZtxG4EqGTtgaiEIb2hX1AbseukhqZR9YSCLSLEt30ugyClQQsxmHHaPDEvIa/Ik5Yts/KtLD3YY+iWRpH0" +
                    "WVF6BAVRlk7Bnn2Eot4aJV6kM4nYRWwXbPIGkshyBtjftzyd1qx0PWrIlqeQIKpHCSKWowvoEeayw+kuxgxK9ESCTSDEt4Wo94pkWVXZYacuBjMjyvAT4PmT" +
                    "Hg3Hk6h4EjWvMqaOBakEJSIqnBIx8EDMfLySzwK1NJRmFttHBPFCBX+UrIInP0ktoI5KNAw5MraJYCRZpmcSXkeOMFPqTs3OZZDLT3Jk+yaGzC79NBee97mE" +
                    "BZvg0JHezmgs4zuil6GanqvSiFPCtvXc6+zNixh4mmu0MPApYnpE611Q3AibggPVIwrxIlLelB2URIgOxkaxMMwkyyzGwjfECNctIuuJjYihYDPPpYkRZSVG" +
                    "pFmgzm5EKzdCZWFDjOh2ENLreU9NssyKPhW0fIQbHU8JYGajsYmb2Q09JM6aM6qJIaQL78oOYPTgBA9LU/UkAowizGzLxTY4YahSRYJZl2YKIEdmhDmKSJi1" +
                    "w1EMfuFMuhTahXbi3xqRZXbLwFRilB1sECG/xjHTTha/S85oCQk3SdySaURqc1niXMYdSlG75nGszKIyuYsdGzkCHOtO1VCpX4+wwclEVslE3KoZmjv1ayUX" +
                    "8D3H0gppW2tULmFQHuGobXtmRJidm1jnNsBNsA2E92Ph3ylgP3CxLYlQn1VmOJVwmROKLlYsCo/YM4lWeJSuKrKozbAAdrvE6J2SVIAWmyFGRMsolGKA2RQc" +
                    "dj8bHmaK5aHb4SVXZul8gWmkr6jZ6+CG0U1wf6o42CCiedsH5VQiMbNuUsJmhRnIJs3ALtkttCQifwdeOjnuGQ9u4cSxZPBytaaslA2N0jydRMq+aXN8g13a" +
                    "3fKMLGEJKjjyTIfYaJSFrY9wz+LHzUd0A4ivx/1YiWoLXDIT20x4hBjU+I6BrZBLy9zK1TmEJziimGliQyOXeeSXo89x7HydWVOzckS4JWCnnSB2mOHspFlT" +
                    "5YxHzH1xVW+Eg3fophDf6+SH5906YS9paGpXjpB87C0gITtficQC4Fj7BBf1NuRQ+I1snVQ0jJKSRzmm77AlkSvvxjY28IRTcgFPcvl2nts4rNpZGdnHPscs" +
                    "R3Wa25i9BRtEdOGPBNEfCSLhM9ggok+/iX17aJcTu05JjHDb3FcKL2kiWG05NbXTrJLRVP0BLsTYjYEkRhab8JpAQdFEl6Jrn9zoBti9kIDWTLbz/F/6KRHt" +
                    "ImDtmXybbnErjWvvqZYyjrApxPC7BMtwcAPzQUvhm3hWHaYgbk0glr6LtIGHYk3OCsrcLOBBLXVSsdMtDxKiwu5VzH3DwsrMLiPFcArEnFOMk4fgrKI3HRGY" +
                    "WKa6vPIQWclRGa1GBf4d26FSQwAglOdh0LTM8oSPU3kmgP5E1p6XKnQbiOm1prrj/hdplMHx+1VRZBrS6KpDPB5OqkMHWctJP6BAwNfUSybmE1h18hATxeoo" +
                    "afiGgflGVv08s/GtgIC/2bhwI2nYbSHm3zvhtVBpuEvOmiMTpUahVHeUmjrGu4pYx0TD/2Ye/Guhwypku97qSfguMUKTfjeLNzYreLZVqPuGQWM3zXH3K3nl" +
                    "7exOmLTqmGzXjz++1qqb5oExYASyg6qzh7Q2vxjQls1xjWQXLDXgzSRyNlkAMXDF3O8uhQyiC26UM6iHdKFVnd/Q6r7WN/1m6cbj3O93othuX1y4yRlAhOCz" +
                    "T68pM0zO11s3SMwyTeR8Mc2nlnFVLXq7FyMb9FulI7LoBRBIRUJEG9LMxiEMFYojFXfhUN3Hh59lWj7oBGxn+n5NJ2AfH7olpPBH8vZPvSnqzW4Q+T1Gheqm" +
                    "zt9AVV7T9/Nhrsc+62T7XVJJc8BbvZkTRpVFcxwxKqfk68qjy7iRyf7pnHJHl/gdPiP8dE7ZDSK7t/vbbncrEGwZ/eQsWPwYm9c823V9VCO/o8HlREuCz85x" +
                    "mQmnrzkyCF7DzxYnty8wLZ9YdPmRLCMneTo5wi9kOjHiF563KIiO1GF3F/e2doM8nrQBeqUZ4Xx9EOevtih4NeDp4fwDn/9rAUMc8LRoBraommKUnXKvYyiT" +
                    "tMyzTJ1TLS7Jb8nzpB+FiUNHIzklR0YChkTME3MbGoia+ZWHRTyLR0jSIwQVLPbKAKdRnnzrEbyiy3ihI2K/3bbSDSG/Tiwncr7WJHNduHCe7aK8shMsBfCw" +
                    "SyvzIENwdIhqSKOqfb/5PNwF6ObQul310O3cwjgjD3erwX+CHHGTWLoj8HDQvNUNcPO86rGbBFtCfiEWsR0EToqNXdV012bZIxaaoSeUGyxy4NOFBzXEbi3b" +
                    "SRBi2Wu8kZIRelNOxY30yIWYEwb+Q+SSWm9qGgrfrXudmfkKp265ZFW5ZL1VvJUJw8Y9rDA1BAw2gfyafs2R4iBfSaCvoleGZYEbUiRKp1kevITSvU2y+DRK" +
                    "xFJHj7CoBHTZW+JXaj3s3ImaLGu6VyA7MqfMFyTsveCt5ETopeDT3ja72wNLlYNXwlNOL2I6CU91Q8gx/S+KWM4cbJFCsE7+Capet+8kRYHzB8RaBIx8oFUW" +
                    "KYPNDMp80J9zT8tfw9P65xmMzJMCNuJQ8+KMLwoIu8gCGMWaYxiqn3l+zbY2iEUaJM9ZI6cxHCIQgdxBnMToWLQyD2mpE+0Byte9pK2Y34pqBddOWMqRHxhs" +
                    "a72LJSlsOt3th9+tcXSTyDF4A/e8g2tOFYLWDBlalXGRx9cMKX+jaFvEaKfVExndDPLrxhUy49sHkokAZ4ALVErqXczDPmh0BvlhR59aB22U6nXZcazrVk8K" +
                    "cUgxgeEm1Gna0XfYC+0eWtqCXe2/052zYlWCFg8ys27pHnbaL1KZ1I3DQU6qm0BOf8Tx/4jjQyXBlpDfS9lWbF1VlrKVKcOEOm1EKolPqdfG+10Lq657NMBP" +
                    "6GSh3AxhVNnJWmUpt3vj9y2swXFwN5OcVg19/3oL6w57OvUr3LOucfcPbAO5lPfGAGVl3S1iK1qXMkwLPEleilz8rYrMSQ1ACRGOlSoCbtgQHQokIQXAct77" +
                    "WJQq5Vi8JTSNlWHw4S8CEWmjJZO/1ZKxj1oyj4MYdmZCHUpMN011gpNgc8j1bYkZUAaAG9tBrfFkbcow2NbbZCKNZIK9xIpOLvL4Rsrj07nSZte0SyYKAtVI" +
                    "gaIOlsAbtHfwerHObtfWoWWFj7054dJ6/r/Mi7Y6G658NeQ9Tfa6leOw28cHvoGPvrzdsrTOXGxY1X7iuORlMbyYvNipWxehYcw6QrUz4cSeLaEak9bEQbW2" +
                    "s+S6uC4TQ+LWp7mLPchw0KFcQYZb0ZhlJ1/+WcqLv29tn0DpbgXFlD8znH9mOAdTshtEceE38WDMbs6bO1dm6ldskEjWpDvlD1YQHewN+hgf1ehYrFIs56ty" +
                    "KwJrVpa+MeNNw8JLdxG+mOZchCHKLAxxcBdht1fHrO7CHSYvui0U/5opaZDvgLbhnCJMdr4Db/KUnNpFlU6s1zktdYW1MNTjTCwWcpTCrztkknVoHPwwwlNS" +
                    "B9fuVOl2mrbmkTYnhIW+3bjkD5q2elrLP8OV/jDlO29cmvKKbhDldftiSEwFFLIEMKIuejG37qI+b/KsR51CtV2HSXP78rPvdOWJi7sV8Q+rU2Ye7eh0n0Sm" +
                    "8k/z5cym0/0oWnnYvnVMKtgEymt8yiNo7Yvi0eopHDeVGXMDI+rtWzv3ILPMTqXW05xYZrDG1KH0lCrHgmOvWQ/fbPjd0qG2RLlf0KrMj7OcX3mHOzbUhEh0" +
                    "Qyg5/U22IgQm1codKmGjB/EzzoGgCKPLDuptjlXgrHufNozasKxXWjfBDx+xpVmbL7Qqf5H4wHhlUVvZHEHY7n7g+8s1GXq/a08k2A5KedveCghRodLP3M6g" +
                    "uiOeXQTr0K0ka6kztpn39oowORpbk8q1XsmXVB+jcySPK/m2SnOWRCknyXPYufO07zt2VUq3ohAZuA4t6SW0mo1oN4Y1S835SdVYrvu2cOFRW/Chl9do9Z99" +
                    "GP8n9mF0Qyj1NdeBoEmsNrH/s0kZbrfnpMGIOqnDuB1MPeI9SYvG9LjVVy7udF1Hf4jEcBtDr81ZN4CHoxylXfgPPzV0oeGH8vP79/xm/55/2r/XDaG+lgQx" +
                    "2NEy2Pe2tBC+zr3ObV3xzZxmPazWcoxBcHNToA47Pco6+Ni0rTMoIu5QiiFHsdQT+/EbqVlrvygt95ZwXpBRvsIol77F0tycR7K4nugWUJ35vdwHrSAVOk2S" +
                    "udScLGxXavkzmVrsS5lUwAJP5oF38H1a91RQ8OiN6Izuhe+HXzjL1f6uvSnfIQ7fEaTEVi2zaWdRzHAih2QP0a2hut+QRZxV5sykMjdcQx/pFiKltT4RqxOU" +
                    "Lgn4zigwyhQn3yVrWxYxuJInkesoWt+ptzO0mhS6ioll/Q1MuXQ6/R110ixdDbd2NWR10clS0L5ZPEZQsWPAlGbHtPcjmQB/wfZQXfmbKM75nQx+TyaEDD48" +
                    "sVXP650V5zpdiipNzZwUTY3HpKJAuULCc46F5wZKyUO+2w64WQb2FlYlnIKZNK7DE8naTqGkKmbE0v48Dey5Cb8OEw5Rm53up7K6QdTwNpLYqEjWiEy5ynRK" +
                    "0BQvWkvKsqcQIrYP4g/TDIZY6xsmXUq5Z8cfxaSKWNhZxLie4FqTp/gWq5z0Ke23SETa7QB/pSW1VxkTvsJtOhrdEOrrfZ0O00xeuZR5EZ9CIOwDsTJ+vWEH" +
                    "VyW4zS5XrRLjOynisLiTFj9n7mkMDfVpvDf1nsaeKjGtAJc8S0It1d5O8yCI77bKQZvpHP/QAHWTIL5RUSQedmd0g6jJvN8Ov9GSimpTozns5OLGRhxatlqH" +
                    "7iBAx5UI7dBwvWkpxrYedmj4Fcz2nU6FK/vALlxf8rvjYpvfv3rJ3eQXu+Fe1eA46uPbZ4adZa/BJlH/SF//oU1I2kQ3iPqi9+V2HDvlLgT3EnLP08AG+urt" +
                    "qh3oYSNlL+VNasHcKt9RzAMpmxa8ZikYEg7S11R/7E3hSQOACZfZTKTsG26VEwu50uwmYDz8xk+4xU/Yva7tTlEI/QTnm++F0L3qimc51BdQQCS7EUPUPsc8" +
                    "8k3m0DyUqGcqjWFlCAYwBKPGr8t/rZj3LCo9KT14UO2x6XyZc3P8GemOs3hpnKSO68zUN9+jWUO8VG9g2u97NaIEYUuoxfze1SpF+glereJnNRkz7X0tOnbc" +
                    "SB5LxNvKZY5iW5uIJPY449WNIhhWCCizOCHuhea0U3BoNNBZRb7Jy1+1ZgTtatttWFG72ubIgf4iDNiioNF8ogod5C7MVJ3u22I08JkOG1a6RVR3efLsjE8l" +
                    "hpxjaRjDL1MoAs5z3O/pYhWJmWClipOyGxAvA/2exzjE/Ma5/FADHKk3QAisWLlU5zmvG318+5sQzp9nTZxHwh/2hP8PQisSq2LCAAA="; break;
                case "classic35": encoded=
                    "H4sIAAAAAAACA71cW45ruQ38nlmFN2Dg6EFK+g5mEflNgAQBkiCY7B+IRFIS9Tr2vfZk7nS3+7StdrtUZLFI+W//+Oe//v77v82vvz2uR7zKfw/zMFcCfJhf" +
                    "f/nPwz1i/om5HnTdxodJ9mHLT/vtX3/5758fxj/yfSA9jCufbXggPjDkh+IjlofafDN/C/mrzd9j/up8/j4/wueloFzLHz5fj/l76x72yt+b/CvyNfpNkL/k" +
                    "X/anv/ATclf5Zflz/mUulF9WFsqLg6O1y5L5ik+0ostPKj/n8n9eyMay0O+8kIWyUP5cFsoPzw+9Hil/luWQl0NaAPgJAT8hyE/or3/Oazwt/e8fT1du8AV4" +
                    "PPHxjHQjXzb0GeTnrl52dJf8hP6a/7KQ/4F85L/p4fNHvj/kG0AXY3mKju4BBM8zmfLQ3+UBmC9HugX5o9zR09VyJb+YSBfCo9x6gsvAV9ixwn/9FPQFPFc+" +
                    "F+hjeRGTbIBUXkvjYt8ABHxg4MtHyN/bJMBbAd7Ty20ufqHzVw29JegtIZYI+sRYeVox41cW8fmLgbI5CmQOaCUHGvpI0EdayBH0tiwHtGvLc7T8pWyAQKth" +
                    "omUwVOQZQk+Im/q/J+iTwC1X2m1bbzP22LFHAQ4JPCz3RkEwP6XyTB3do2CfV4+BsY9yD6RFGHMrj+StlP/cegeTP54+ZOwr6M5+D/xwEez5scR7ENhBeO8m" +
                    "3l8T7IZgT+492mOlff4dFaZO+/yEPJ5pHwj7MGBfaJ8G1JGeGHD4yHci2ntFezfQXmPvZ9o36FfadwRjpb0hlCuAmSBOsXqgPTToUaDna55pb9q2YtrbDH2F" +
                    "HD7kPRCDgBiEUCgXAvPe8wa4psBvZAMI98t1CvxeNkLgDcCApXgO+BmL/KACmG/AF8SZpo7jRw6WRPprjPc60kcO8QpyoIXKd/kZFeRLwEdGHivyZhPwmeNr" +
                    "wDcb5MNK+tCRRwn4RPokPEYmfUd+JD0I8r4tZ4wkCtkzFPAr4ug/JD3xB4k/OYJn5CMRpIRsigAEDmX0gflRNkFkwE0cGO8Yd6dxp1/k6Bd5ImplfA/6Cn9m" +
                    "fNkmC/AkTBztGKCnPkV45OwRKCUhhyLgeA+JgJfwbiuQivMaeftmqkcBkZGP5R6MPDO3/BGCoC0/reE+NOw5cMAU7oXzVSQUzheNVxHH+NPIu2OupySfDmSv" +
                    "0b6quijJHQj0Eis4yXsNuyPYKUT7a0ryjhAktCzznZd0TFOH94HebZN8frp40TLIWgGhge4qwXWWB7rCkd6rkDDyne/S+A4txVduXgR4JW3+9Z6+9bwlIow5" +
                    "HuQWy7ioc7yhmzX5P30qoAvY4VOBR4EeKdAHZKI7JnrN8W7S9sJ0byS0syoripAYfzHjmajp0tCTIncUp/OjC1bXPtJLbs/r7FK8o43qjGgFoBXKapGQXxJ9" +
                    "kXfXNsM7Af7aqTs7Z3jcsR2nNB26uIMu7kCgLxn+0hkepjjPcYIjAIm7GucL268MfAPc+i9K+8TJPXJyd2fkq7qTHcC8d4w8MvLM1YRnZV9iPe8uic05BJSV" +
                    "nCR5ThrbWB8WTQ8bTY+0NfHa53g7SPsV+4O6s9tIjyrSo8a+qTugf0z6tEb6XtRhCyJBq7se6TvmNn4IvifwPdHeDvkdOL+7SeHXPYAS/0EKPCf5njGLgV7r" +
                    "ktE6+PS7nFe0N03a91K+lnX2LO11ovf7RF8UXqSFgOMIsFQEs1F415Dm05Lma8D3ivhS1cFQnDG1r1aaC/a2YV8MA2TskwT3yntHAR+UMsx/bFURXmEvkMvX" +
                    "7xI/jYn+Vci33cYpxPdMfE73yb9F/DvsWZvZNJb07xTzXNbhNQT9nu51XWe6uj/YOSvzj+o+jOo+lOA1lvRpX9KDuDZdPgx1XRD0G+hS4H2APiGCVor6ovJ4" +
                    "DxjLxL+mPWDOe+CHsBelN2Kv8j0XZFvskZQe0kIUrDKpTyW9SD0Ajf3k5Jm9vP+wsMNe2HXoY0/4cSjpe8J/EfQb4u7TjA+kv4D0F9LrGMXMM0z/cIa+FvWB" +
                    "TT+if74P6/wgOn+I+lqknQo72QWBwkL19hY3D1Vhb4j0YuHGTX0nhT1y2Ecz1Xewqe/S4uHdboCKGGNYKP/sMEKV+v1ezxjrBoCm5GPzdGLbABL569Yp3Pe0" +
                    "AQR357/I/Tcj/2zku63kY7IlONd5JfLjjaHH4Hm/kXynyB93hl7cGHpDab84erf0f635WuQPgqvofSZx1OjjYAZu6R/aD6DSv4Lu/M/T3xL9SYcB6TAkqgWq" +
                    "9JJg70XqUbuHvXzGmlV5qQ64vK9lvXZzLPWIRKFRTSaRQXbU0r0hvc/B3vlT2+YSW6iAzi4utOZN8XK49ER2l9ApiTeCzTA2sK1KByoXsDQod2GwI3ETBvv9" +
                    "Espzwa5CfXlBZisHlMAPcs8o14jqnA7IBPKRwG5Uj1/17jcC3wvjw2jk5de0mjoc7FFQr8H+EvSvk4cbVWWP+4hfrdwCvrnv2VFqKk9qNfRY51dT59KRXrVl" +
                    "rurpcKrX7r3fq7xU4Q+CbOU6xYYwe7g9qD+LwTnDH5VrrzzcS1b3FC2eWaj81lF38GnrhiQTUuYMRPoEtcTfxvtr7N+1El81bsvG4aosxkN5N0r8qJt3PjYf" +
                    "tzCfa3KrDT2dMRzFcFKUtJVVtgf270Mr8vIVn46dm3ah0NMr4qME+hrzy7WgunZVj4Ok+fyKCu7A3O++jiPujx3bvm9QAj2KATgZOyDGTsPc4fWhkbtK/DBq" +
                    "e7+27eZuTY38xHkjnDf3Cb6XdiPpS4VnhsaNOXVrRxdf2XlxsPOkqA9rZWdneZ9eyHur8zs2ec8okgobPXjl3g+VXZgeypVd0O69bdunG7kNbfdB58YtYxrU" +
                    "SEvcq3H4guRTSbft2aR9q45torI5FMehVfNvDGUYPZQRBo6zulwcHLuJ7y29i7EjF9TMBu+Q4urPQn6q5FwP79yXR4I6o/csLeuq5aBpuTh0Z1V8r1iHSvEK" +
                    "sbfXF3s1ader8V3IbydwXgv4u/J95HegjXC0bsJGwJ8LdzyatvbYkt9N4rxr3exNW2XdaMN+9nuP9btt9XvD2n9q2m4I7hrBScGnRcE3opOWA9kE2LrxBfxd" +
                    "U15X7zyFRSkE2BKQBb1r7p2490vxpq0b2jOethI4LQ+xsp42QFhb8iq0m0Pp5oXvq5wDPYM1mXZTaDfyTeX7HNuDiHc/uXaTdROF7w1x/4Gct6fKvTdrtkT3" +
                    "nehUvdkz07VBP5fqP2TUrZ34XanOyaJO3m1mb+zGo4Uf9mjx3qgbiD50Z+LQ1sPm0foV7aCJXkH2H6t3PYMhTVl26gzLd8HHiDgy6HvxtpHtVLSJWbu4tAvT" +
                    "gVVirblgM3YpNt0AfpvdyoBXyZ6meo3aM9wnluacePRSvY3wXz2Mr+jvknuYajeGLFQ3TasxoD+5zdTFntt7lOCRy1LgGXECxKG3snb5QZ28aqD7j2evxnHb" +
                    "4tNEZjzsZ6/sOnxH+oDr7MQ8i8zXmF5JeGnIC+GlVjP8eh0a8ptRjFnV9b4c8jqLQa9NukXV6cbcLsx35HuYr6YNNNOGJ23JngX5F7png+qRqAQ8tGJNaB+q" +
                    "QUcCvuEN16eeDcGBbpy33BZum478lxtzrVyv6u6E/bv2bI35BXsuLcC9nreMPzZvGZos307dhbU5Y+5GbcMu5qMas26Yg/3Unb1hPWNv9jT3dozxr2fqVd2G" +
                    "Nbhrotsz2KdOXHy3E7d2Yc3YivMvMnz4wVacl/v4uRWHbR4/nOs3Lv2qRdNABu+/P39h5vnKOn8xN2Iq0wl1mayG9+q4uK/jlsnq7ezN6tN4bc7GAXx7GLN8" +
                    "rwVv36G6LsYcn6io1TsNVphWvVsN/n7AdhLzNdvbNnjVMAewXz1UUUZsr/lUzW6qXlql/VAFjzmk0Abvlom7cfTmtgmrEn58Ie1vw7z0YJVLN3ThFm0PB5fO" +
                    "PtTN2Znv2j6OpVzN6qTuUPz1pBszOMDvRMJjn6vvLp3rcb5ijvjd6Zsbc/aU492Y42+YP7bg7wYwXk7dvXemosq8VwbOWNelz2ZvfsLACarRqtv52wGMBjl+" +
                    "OmG9OVUx5fjz+TnG/GptmNOM7Yj5duBKBJ3S9Gy52PAq1fO85RH5sJ6eU7LOLKX8MnJhqs8zQI6DpF/oLs6N7U7tNfMdxKll58YoXUd/JahjFhXzWIfq/dfn" +
                    "rYKW9neTF1s/RzQ9v9Yp3PM9TjKvtmIU33cdGc33lwNXcqCCkwbE22A/iXq7J7zfifqu1cLRyMFx4kpHiuoIuNPIhWsjFw3z4PAPHrYzk8qLZ9BfTFivhm1q" +
                    "XViV6OOs9Ddu/Rrrdx4etV/T7biNf2vc5ibYx8WzVcHer9gnXdCt22aS+LuCrkEev3p0tg1bkb7z5zbskOlNj/pS2Z1OTx6r+Tg3aQT9950cux3A0AWev5N5" +
                    "9ifGbOONY2/GuH+N5bwetoJNr2Zoxq5jtg305OMf1IPX8X5L+alBB+9ZOPGdBt2PUP4uyS/23f9nthoPs9W9qr+frQ7rbHVxbhvW6dPC7qVza7qVMzTkUQo8" +
                    "mbu4xLIN5zMVq8zjjpqt/TkiujRleQ9ZhszCq/7crjNHZR2v42XmJuiBK7cp6/A4fGHUYaqwnJSv6VpOyvfJucDDFxVA6shPjk49O9lna+tbJbSzk+L3U7hv" +
                    "kKdPXfvBvBVHR85KV8bj2qypIp+6s2YU+dxnS6zPkjkwn89Qij6DifhV47Nps2r83VtkrAen+8zNYbDeHU9M+31Nj2ryZuPboxq2hO7bJ0n5nnfGfGAa2rFZ" +
                    "kE0C3cyLUgGUx/JYdUM8fbWkjzJsNwzUXxPf44Q8TJO2uJ+1TPuoH+qEbTVvioHHYkHCvbP36v5SPfnQmM9D9bVZN3TpzL6yC8R4VJWdH9p0ph6bxT5hqzvr" +
                    "jkcs+2nJWIM9u3iZw8OYXRjmqavAC2uKN60n39BOH5+Xvk7jGHE9RyENOT4rlyTMx9aZK2F+16AbVT2/N4LWdVcbuCoUF123ma18U9fxPgQuMT3rQx/1aWnX" +
                    "hyjbYO0hzrefu0NrvvfXoR2WljdECXJcfjkzC0uXpp644x3Q3hClinqieoU6BfvFgk6i/FWjPFV09bwkTB35OloJ01iGOb5BgiXz1tJ7MDis8X1r2pYwf52t" +
                    "nAr+dFJ6Mu770J06Nrnry/OAnWa8nqtVb4/g99ZtP+/Ck7TPWsDxiHTpSvccf409Ol0PYPNyKuuNb5Uh5/gyVd1AT9Y6l79xGMGHALGQ9Gc3ApEfLyG/8vDD" +
                    "8sZIr47R6XfI2RV3ei46TeM5x0G8V8WdfXGSUiTfnaVn9oW9HULA/TE6PKn86U2RhrPT65F7N+yq4RwNu7j/A2eLPZ0TTQAA"; break;
                case "classic35-1": encoded=
                    "H4sIAAAAAAACA+1dyY4kSW49d31F/EAB7rbbWZiPmKsESBhgZiC0/h+QGzcjbXGPrsg6NLqknloyKz0i8xnJx8fF/vsf//zX//z+7/Pb317Hqxzt/17n6zxq" +
                    "TK/z22//+/Kvcn3mPF7wcVdeZ3Uv1z7b//ztt//7++sMr+vfZPc6ffvV5etxrxiuL02vlF/nWV71+ltoH7n+Ck+4nhjr9QXXx+L1heF6Yjhf7mjvwL/cGduX" +
                    "uOBe9bhe5/rbcb3Uf/wnvh3XvrL9er1I8PC0eMLD4DnlFa6XSecrXK9Urr9dD77esj/hSf5sT/qdnhTa270+eb3PUOFJoeonRXxTCR9xveX2ntpv15Ni/fbb" +
                    "f/399f18fffwX3h9d/DfCb9eH4mv7wV+9deLtY9dr99+XviP8dfrx3U95/reyiu/4iu9wvV7gd9L+zftD9eLwSev7xn/kvA536tvX/v7q8Kn28cTfG375wd8" +
                    "aaIvvVDAP7RPnPl66eOCnoF3fACOHwM/AfgJwE8A/vV65frScCDqySHq6STUA6F+vbN8/dlFi76HH/iFXvtJl6LRz4B+BvQDIFfMGch4BgI82Bd4nkPIXNLg" +
                    "R3hQbM+6Dkd7XG3Hjg5oPMzjCp4APETxBOgPxlmQd/yRdGHDIOMHg0AfO/SZoU8AVAPw+uCroQ7QezoPBD3id/0or0fUwtBH+UqEPrXXy4B9e9j17VT6S2p4" +
                    "XqcqxAt7Bj2ED8E/AHxAJme0/ICWn6zlZ8LeE/aBsA9gp2fDqYGOVlYQslI19gUgK2z5S5PPcC4a6s3kryc5PEWuWMtvT0L7R0+S2oGNzniBDKfyeuNg9x7B" +
                    "92L3YuWz3VfG+RwMPy4MX6OPRkvoo+Gn9i0EwtYj+hHRL/DpfnLga0/6p/i/C318bmb0/YU+ox7Kh+iDOV6/dtN3hzX9Y3D4J8HvCH5Hpo/wVwStoqXVU8Hv" +
                    "m4UDqpeJoAPI8Bp0sthW6RCgD/EYQfxhTb894iCjv87qhDt4/wMOZkQfksj28wD/rdunTwH8/kKHPuoH+CMdgUJQfm/QB0GV/H6GD57X82pm4090YBLFj4zw" +
                    "89eC48/0Cte3cz06hAY/wR4/9PwJYvH1qzL+aIzfD2G/DE4gUVDAH3LNW/TF8TfoPEDnjAuo6AKKuIDrVPm0sH3AwnmK/e1BEY9RMscIn3cdAvD8Tnt+CvpB" +
                    "GT97+fYfGn/QQV+BD7Z/Hoh+c9oBcGXjL+2LrrdNoRu+X476DkI3Gz/zhEj/MCD6mZyGCfsN/evnj8bPqMf0IfpA+hKQvgToF/jDef3kG/iePf8xmP6Jpn+9" +
                    "O/D8B0bXgqZfEPxiwAeG4YBheAdR35kwbcMAsgmPDtsFDT54Dweeqr2NAw+thwBAkR6M3sF5jMj82mm6nnSZDqMvB8Ar88ePFDZxRyckth9XBFKAH9XwJ/p/" +
                    "BhOidyTf3WzXJfH9BL9n+NE7dN9PRyfQYyNHfmR9rr091+An2PG3D3w/+OMMP9F8vdLReFtEzAuyMXABdcMBHcX9E02f0McfdQkrxn+9RHuVTtKcQHc9yWPY" +
                    "b/G+hf2I6MeZ8HPAb2f2GB1/AZcA4FPcP0fTP9nNe0X5xPEndRwixmuvHH/srC+Tl5+wj3Qkrnfq6SgAYRHHz1/boK7kQijuR4ojZ+2PQcZfLuwZ81S+kPIH" +
                    "QKUi8Y+IfRpInyPgDzF9AB29a/GIPTrZ4nTQB3rpDwr6M98vI/Nr+GfEPq+CfkP9XCd77RHXtwykL8FbCkhDQ1WM3ylup2nAEPTDgL3mfFmgMm4/as5Xgf4n" +
                    "5HwtoTgZ+yJfW9jtO0obyO1XekhqYDaHBNgT5iX8LLvHpO9y0OuYz6m+F+IPjM9hzEfwK4FfNd+/XsLXVZYXEXwvfH9B9LvFR070l16/Ye8w2KMPikFTPTcF" +
                    "+5nqeYn23lC9ukM9I+qBTDVhsEeP7U2wL5IbGKqXyT1QsGemf7YjgN5e0D4/jPYZvrEMvKmcaOwHO3oEvHaqzxQ/0YEATz9l983oV0k+O/xmsZFS/QH+hjtm" +
                    "kMHd5XkI/yq3Y5bXgO8ev+H/Q5ke+4F9pjfjr6j+KtNrVp+0x2erl2iR6UgYq4dMrxLZ67i7D+N9ggOQ4AAksKh6IN3zSPcKHQMK9MSnu+9PdAwyHoO0l/gs" +
                    "3bteyaM2Y3W+xPBD9F/Bv2B7brT7BGcCfH408LvB/IdML0Cov16n/RZ6ph/I6wfmh2L/HKmTJuzh1QlehEBH8cABhqWOXj+TRpTbw9nrtwc7R5SxtDcjbL/j" +
                    "7j51/EBCMtjUFbKb4y/oB1DwuT5cieYfaKGT6Ad/9pTxH3AOWOlRjl+CfiN8573EmwHamerpHL+oHH8Q9kjiIZ6P6WJImuo5K/BpqhfUB43hu1Hgy5SpZYAR" +
                    "jLexN09RGlXortkMVG+Sh3SOH9jx5y7uBsCd4Q7H12f5VtyPne2N4j4grmRepfGsLN9KfM3mH4S+lujlhcZj83sM/OUU6lfEDWTwK+D+/Uz2NNE/e063DPvW" +
                    "7aeu8GjTVQJfEFBdHd2+m9EvPcfX6Lcz/Srk9t3B8q6A/rHEp+O+Q3t3GPcZ9jLAzoaOwbWR/vbjbbIAwE+OH42tphXbR9Rm+MlfYwTxmDZsSjuzwLvK9wD3" +
                    "in4f31AcrT/aLF80nsBJwIL2BUP7sgn7nhM9Dt2K9gn+wYZ9TfsOYn0i8XDC71DfBeMX1L9U4ovNI5PIc3200APAyx9DaSeI7aO3d09pvkn1RI5XwRqfSOqR" +
                    "R+w41Qv7NJ/IqvL9yfiSgiHgGH0/Ip4U9KdV+HxXfRwF/cQBorDvz8qvi+93QvrI9zP/J323WuvPFDc86HhJcb7L3iOxCnT9sZV0O+j1x9H3b5L+ssnynER7" +
                    "If8NeDSyknZ0P63pPmV7GO2B7+VFTW9b0ClDNY+zvePkbC/Ud9h+scAv6zrK7JNx+7mb/cT23dLskTAYaZezPUCeHQK4fbB7BpxI348jnyASp0J2D0EfZd3A" +
                    "+X0k5HOXd5nrcX7fLJ6C/l0pnxL8uYjTuX2L83Gf5+twHwB58vR5leefGnml7L1Z0fPE88NW1R+Ttczyjqj6RbJ+SvRPbfN4KGr/2i7tEdHP8GxS9Rv0gvj1" +
                    "0/7yTP+oaPR+Q/R8N3Yq6qHRo6BS0tb2TcQPN8qeF2UvuLXAt5R6thk/ahE3xu+fj8BdUTdLDbaMUo/q5jBF3bBK9ZXUw44jDKl+IalHgHf+08oOFNsSFJ8y" +
                    "yLuFynonBn1mfmE4B7Ezv4KBAYL/ScwPNfkaV4y/OX94KdIRiEV2/METVHQBKBa6qvF3VB3sqcO5cf6U8qu6ntN1vaXAK84/9KgPuEme76eMb/AAuqqbektH" +
                    "4pYO17t5kunmIamHSR9w/mSlHvAADLv7uKlDewBp5zLibh5onx/EXaX5Mf1rLgBPQIl73tcMN68KshFPAOrzfALiD3iAskr68tIDnLbBa8X6x5x/6QGE9y2y" +
                    "vrRs65jCv5uJX4aAoD0AA+8+pv2/PMCf0QMw7C6nD4m/LfJoD3DuOYDp6VNy71zfWRV2H3U+v4c+3Ms9tqErLWT+Y6ry7Hi/ezf05zHdV7W990J/6KGfaQOF" +
                    "/mhVfsHbla8ReY3cE1juAfqfh4RvbuhC3IPw/o3Yt3D8dVXmiW+iv9B5ykLtZ/afTJXHWcMf5L46yn0T+08L9j8U9n1P3ooUd21TR1UHoHJTh6eTkEDuJ8+f" +
                    "sKeH9T7B3ZUP2X+C4ksCUSoD+68OyzzYzyGq37Hp5Q22l5cCPzL2klV513Z0NXd97NN+v+rhBSdFVZ7Y60S7sD/38I5J3+Dv866JNyyaeLNE+8pm34t7Eds5" +
                    "uooP/dvK7IOgXjjeF6gPYC34+nb8q+h0/wTUCWx/HF9Y23FG6BlzvrLp5qqd6bWcL69wt42c1Hu3LOxSqKe0f1ff1VWevDB8m/NFY/huaOCPU31v1dQRuuMP" +
                    "K8En7Ow+TVl/6nbfK0RROvilvCuOn5vFzi74CPD+4y5+W92DE0ARvwz1/bIp7yQp7P6BKk/UTT2HSf2zaef2D1WeleCXuIkT+rr8XvYZdH5nq3y2zhPX7R1l" +
                    "7/ej7uXEz9jAX0XzWXTyYs4Pfj/pOk9q+DPs/oP+jnMh9IPHR9XH+U0DD0v7ad+1q6Vdw+29ZmSe1J3ypO4UNfcB4hQdnC7zbQv5S4LneVyj2Paepaevo6Kf" +
                    "pQGTmu+Cje9YqNfNW2cv3iQK9pUNPZNfuAhU6EcAkD4AaQLYf2k9F5v2L6e9bNeuG+QHfW/v64fkruGW1i27Q453q/DGJbcnV6/a9+bkzu2Tuwd5z29mNjC5" +
                    "N51cRtuPm5mNJIlhYYHXdPIkndy15F5w9x937uruTajptWYM8PjUups3Md/Nwxs0rUfHYIr1Wt9N6/5NOgZK5S0PnXxpW9C3GZ7bevohxUuLFC/8MXV3kdtH" +
                    "re08lXaClAjnFA/gTwJ/+dIGTt3Pka0biPtpjQNlgIeZndWw5rGu6MOwJiaLPK9X72t7y9y+u/4b+w821M8lffdU0k9KjVvk+JntPy1L+mkW9/BMIDswtT3J" +
                    "8QV3n92vRq6/TiOXwO3zp2UdwD0B7hl+opfNAtEjaWcn7Y0NPbaR51nTtY08S013Z/YwY0TKbqKxr3JKil82HR3N6o8N73M26Ccl/hhFN9ig33Wd0vl9G+xy" +
                    "Mn9LnRwq6HvN75PijIW9Ps9qte/mVXhSr9DAhmDuS/jSDL8ZfV52c9VNEx9zP99ZP2V720nNOcGfvH6Gf/LWqGZeT+ws27hHYXdX0y3Lsv7S6Y+Kfuykjz09" +
                    "8f64zO+jGtSN3fgV6UsyrCWDugJ7+Lh7V61o4KifbU2vPLB+b5TdNeuv97q+beXBwEHAv9nKUx9becjw323lcfsOvjIrurvBjV3jflVN/8IVT9P0PTfuw4wu" +
                    "Ax68+xkdnBDoB5uXau5o82fP95Sme1fM3Ub8InO6RPhUS8+yn2Me2Jo6ORzKu21gC31/KDPlP6fIX21QGNdz+GFHA4/pciseOP/Qxb2s2/jCuqozTOmryB97" +
                    "5GfGJ8iHmr7e+AtS/oqhP+19APM+tv2+n+Xt4Z0aZOJqmt15wl8nfLengOj/exnfRtvzdxmf1vay7eJM94R/6udxdj3LcnRHUI9H+LCZD6hoiqqmc5qaDqN/" +
                    "zsTP7OpQzZzbzSyrco5p6WOfvxP1Z8p3qsepqU2a1V1W8W+qOmWR6I1CT77RdL3S9E0XRzCcb+zdHro4CiZ6QWp5MKhZ24A2wx2PH8/03cLzq0Svj+nu1jIw" +
                    "0Yd2TyR51+/3ht4XKaxCvtrI4Lf92pn9fX+QzfATzgwnPIJpJnnnvnrrh40Mys9z9bZKisdzGxCvI01iqyFN/B9YulrGk0QX4r0LSeRcADxRBFBdu4Jz/KCI" +
                    "80vE/xOI+AJw/KBc4xYiHu1cKThjzxF92aEXJKJLvXbauDBMXqslO3320m7aaLCnT3g8E3jichOJW3rzcNOYve7JCqonz7Nu66WlQsbu41Shi2pdQ2ESp6d4" +
                    "zkJRwsg3gnT8dAA3Ayq5zHvWCKO9Xu/n3hzAPshE9tSYrXP4tK7R9n0pjcerfTv5MYWfR3F74aal8GmRyYnZe6vc55HGqxFcP47g6nhOJ+CUE0Bmv67RZ7V0" +
                    "YxrKaISah/HkU93sGfr4cW/+YtFisX0a9c3aXXh74Rblcg2z5bKluNq0OMs4mMstWXw8pEODBvKaem8G8nY6Xpg4HXuBm2yelZZhOmPg8rOOpzt1pt780HUc" +
                    "I96DF2Dg46fZfIEMuwDhLsX0aQSZxh0dPzXo4LoltLSK/a/F7XevaBcQVrl8klEs2rK4W7xT1NoundErZ2KHMqZU/lA7lU71n87kQ8/twxj5L6T7yi1uncZG" +
                    "LejyijB4T+WcQzfmBlu7zaY/EwleV/XPIkov7lvzrUtDQI9fOpnhdV82dmu50JcwAPgH1W9o19Z52HHM+LxntVfuwyjhN6tH6c0j61tquCvWpx4U0X1ECgBk" +
                    "+5TIRwZ/XrqkF+8Es4LjdjSrKAOWJp0gYZwqd0Fv3HJ941bU/7AHD5noO8S9OBjN8hngZwrwaevG4P5bQoezedSWfyficxDgBL4Zf9wKee/sYFFE0MtwxmIq" +
                    "YzeKPbPBJGvXVPlOF2ndPrk7O/pq3R7EhqlDz4DflRhZt0Yrt9I4k5Hmr+3gn5WSicwdeq0zVzCPn07lDDMZehojPaxcYvXOFu7f2bWK1r9Ys0nDGU6ABy1n" +
                    "gf88mnmuaneJvQpkAHQC9Kbd02Z9p5Vzgsn67L5FcwCCDE+uh3Iydd2K9ZsmXU76pOmDP3geOJWDNTyZzATzZ+Djx7sYQMfLURXuZe0a6Pm0j6PMrbpavPWP" +
                    "U5nzqtVkRrIo8MfHjXua/CdN/uNg90Aky6Z6P2g7et2i3rMcBvTD4PyTquAily+2BC/ajurMlwVMWttJLBgUiQcB1y2aTRywbVOWrUb385bugW/AxD+SM0g8" +
                    "otcTf2nMv9+/5SHKeIgyvmi6ZlM2L6N4u+xfL3LiJdvtzSW3yv7jqkNfwE6qae9Ua7Z111YQDT8tiF82+7KAIugqHpVwIvP+9spH6LE/UfJnujcU9ct6/9r1" +
                    "riIQf9m2+um61VXbVrxbwzP163DX3tETAcoAbtcuykoGj/htE8DjporTY70VEIig8sJVOgLHYizbWRXXrfw/fMTNLdpZF3InAmDE3SJZ4FjFS9KiZeT8SA2g" +
                    "kPr5XuDpej4jHz9u3Hxctu6Gscw8DOjzWCY1cdBWDmjhAoH/2HMAWrlMbI/atUj0of783UULq2VMegtj5sEsLum0DCDrhdt62b63tdywOAKwRyWqyd32IxpF" +
                    "QD4NWa/jUWeA3EDpAn8WaphABPS6jSPM+9aBAhx95247AuHXxvW/4MZ1wT1+MJ/nHya1wpt9PM4mgXdt2++u5Hlr7e7xvI8jPQ3pDS1c4b2dPEMjTxobt1U5" +
                    "t4yN2+lmFZtTFYVdGV8QjzX9rDI+9m9KGSBs1rOEYUQzLmay54GNh7UsfSh3NZvF0/hqJU/dx/+EziOWedn2zubdbgefH/o3FsuYEgX+bHv33HI2S1d1R+DL" +
                    "CHybzhS806fTmUPm34CvFnE372IahjNF/rsXfYWyt9IfVme7s7frtyDvw9QvbFfto3R0aP0w8/4NcPbUsY1EJNA2DqzqTw18x0rxR164XLysYz4H99L79YNO" +
                    "/FjJkZjvR+GHA0Ji1Y8zCqr6RBR+PNE+QT39jNXLa71PN/CZmH/I9r03Zb+t5m9v1zqePT/Shva+/cb/J2ncJ+K/6eCcB7aCifnh1v4XfTxJlqavOjg3q7eD" +
                    "Jn3T6m21jUVwT+7TmL+o+mWe2AEBcDeaqy5XUqO5i10ci4jvVzO5ffPqvdxfzXCv8v52UIuG8vOC6rmn1atmUGup99lZm9Spnh938K327GfF9Q3VS7yI7Rjc" +
                    "Pto9o50+LfX9WsXw51nFIGAn/4W9ugWK7zWZQY3d5s1zGNDo6/bfX7wZuVVXgd/VPor4U6jXi5fCwtijyROp0Of04q1zX90PU3U/SHU/rfP7pPbvYI026kbt" +
                    "pD19GEe0khwNMXnWBz1O6fS+rvYOweYZ9BTCl7j61XBmskNauzX7sdd6eOtyc/b10dmvvLxu7KAFDH5X3BsqvKrFh/QdPJDcuTn26qZVX0cdOX6YW/T14h27" +
                    "gCPfL1oe1m7ejGkUO5WpWnsE8BTKl25cbaV9Al5dqLJ087lX9frFCsuqni3qqq78NF6b+dDWNxf13hjPSW8157tVWTfNizcSC/tMvpKu6nlTrJcoH9jfF427" +
                    "p4pg7iUh9gOhD2YVfX+mwJ3ipzuWtag73KfDBm9yO5LzKROTDt4TyR009YGWG+61XM/bGBTJ65feHSfr+T7uXf2uhdeO4U/s7twv1m8XakydXHHw9VX3ai+l" +
                    "XO7FtRfqRHuhRiJDl4lMJ4JgwU1bXM87e1YveKf407asbnp51IY9ulBFujhbVkczOdTM6e9XLGbN8g4Tn899Tq/vTsz2FB1ZEQbQ8o5FSn/H7aWaG4Z1O2E3" +
                    "jqlTOt9XpbEjaI6Oyr22k6dQOTcAQyz4tU61dsXO7cEfiJgnoKd0/DL7v5LZM97540Vbcw/X+v60vM/ph/3quzr+myp+MlvXyk7L2Xdvjdu2uJS7b+F2zyNZ" +
                    "j9dpbNZsvXGdxmK/5nCL1tC+K6Dnj5u4FpfphOWVuXE/sNNgxxyasjvq4K/+sXw7wx71LWpLRWcu35b96p2yuUxl6fX3tyqo+ayd189z8/Ys5OXH27K3i9WB" +
                    "4zchT0DPn3I9U8E59B1quICLm/hD3izVZapfnpfqyuqNYXDDTmvdLNTnCcyKR8beplCKGHoQ6baV7a2Cq9eqOjWbhxS+jhO4i87dOqb1Bf7s+fI0JeVV+QtJ" +
                    "+KfeqppkTUdkqoAfbF/meASoyuVpzesL4DmmL/f6wVytEPfV+3FO775xe7FWt6ySPNquGfYrt5ZtG7ZrT63eieutyvNyTW+VXCjrhM3MRtU1+2i775zavGHu" +
                    "yabCrarZB2GFRV+dGYkFqt2KPLaTAX0CPaevHN3KLLClLEMbJOn3W5IXhRy+LVn1bj6v08eIXWG2ZtGzcZqYnx4XL80jO8gaCH17X+7a7Z9Urxvdflf1wkLI" +
                    "LzJ/N81Sy/qFYfeKqHrVqHqhq3qZHkqXaMFd3crtC+VLn87fT1dnlspXZzbjP+vA9d0wo2mu0lkqO1bBx5orT2ytYGfbX0zfd6bvFmV7u2eRblAr6xtzB0nP" +
                    "bcv2fti3ZSU9S/YOKdsZ2CNHe1W2z+PKHTeW7RZX5wnauYSvX6nrlrvU6/6KZNWwfbNSeTb6OCzdMPclb2/QskXbXgsYXH6/QmO6JPuB603jGk/dOmFu2lje" +
                    "oLW7QoMr/tkuXArDFRrC9Iug/2uR+l+meidgl6N8fX7nMb+L6x27ZWjSiqOv3y3a0UP6hZfi3aX1atdW3ql6ZVqw6lZXZj4l927Vp3GzWHfieUOfxh+4Ijut" +
                    "kvs4N+eKtCOYl/Azt+jrpQxyRfqiJVMt1C0/Lu0s9zTEm726tKNx26DjhOe1LG9/RfrcoPmHXX7c35s2NmxsXX62V6TvLk9pPE9gL5/OZ60uTXR8W+q6UW++" +
                    "Itns2Lvp1FrKO/Ni1Lga0Ay3jZrzck21rJH2rNHFeWF3bZZbxXyzZ213e8ritmQ7Z6lOQJgEHrtbOaH7DxTnPW9cIvfvKOgL8KV+Tauee+PC1PrWTu3yvGjt" +
                    "uVWPd6y9s1t706pHLoC2bC6v0bjRd+s4ov3OskXF3BbrGcyKjthndNLoAob2/GWPtuBej69kfTigH8wepjD0aBe7pUErfO0Y1JW+WzYTup3hvSXt5f0m3SI3" +
                    "Zqht2rdO/5wWcsSnJWvPzZnTBemZl6xFfVUa72VMtkvP8HyFeMvyBOjqnG8rln0qMeQcS0PnC29Ne3PHqtsSgbcWLK7pvlq0+LxhcdvEYfZs3tP+m1bNndnX" +
                    "faa/vy7ZL2bzp9GMpK5LnoX98O3/AT52VM0FmQAA"; break;
                case "classic35-2": encoded=
                    "H4sIAAAAAAACA+19244kOZLd885X5A8U4LyTz4v9iH2VAAkCJEFY/T8gJ+1CMyPpEd1eM9Bge3q6sjMrwzOqjLTLsWPH/tv/+J//67//x/92f/u3n+snXv1/" +
                    "P+7HXS3lH/e3f/k/P+Gn3r/jrp/xdV9/XPM/vv/u/O+//cv//fcfF3/694T048v4kOL9If/k+7NYfur96ux/2v3Fcr/wur/awv0N9+ft/jze/45vgW9z9f7c" +
                    "349y6ef+Nh/9z/0Vn9L94f5p//pf4B2lq/+U+9f7sf3nePdT/HhYvt9KiT/3D+6Pitd40v3G/P2ufR5P8v19/+t/jD9fGe+sv/Z+QznBI9J4OzmO1/YP97u4" +
                    "v9Bfm++/m//67z+//M+v+PMr0//D+Mr9a/r51X5+Offzq45P4vh/Hd/k4Pfr+N77P+4/yf3v/bz7T1V+0s/9Bu5f8/3f5f7bT/e/Ab+Wxu/n+7P+DzzGudhf" +
                    "+h/j2+P4zTz+6S/65cb3wWvvv2l8de62vl+dbqujzSuZ/vpzZnfd5uNDt75r/a/c+TAOwf2VbpxQxyG4//bA+g6t39Dq99/s1Y9eGEaqHqzuwepeWD3m/kNu" +
                    "C3Xb+/7sVMYzSwL75WG/1Ibt7x/bH+ThQd6D0ceD+iEJ/dc0XpQLHaJc1UGI40H39/U3lsJ4UApwAtywbKITEOkrZVgYT0AchwK+muAEwCdxOQFgvDQsVYe1" +
                    "0jClx6/efzPDlvAPnIALTkA/MhkNXPHw3Gesv7qM1/ZvHo/p3+HGAQr3ESDb+7dnIMEZAA9wH4VxBhycAXAEoYHxaxu/4y9yAREPwYWHoMIhKHAIChyCIg9B" +
                    "HIcgoQPohyCDA3Bsu/uJ/YtlfMfwAxEOQRWHoB+T0H+FQzDOEbyr7ofu1/aD2R2AA/PDW0rwllJhPxDoFAxjX+NrGdyAp6vPN38egkpf7YegkBfI4irfP2nY" +
                    "H24yWDPzCQjj9c656QXo+JTx7d3j3B88nyXHHubqv5nqfQTI9uHPn4FgzkA3vmtgfA/GD+AAhrt2GaNATcsRYO+PR2Cxve9Pj8Pnd8vej+lPExc3sgeAIDC8" +
                    "f1VuHzyAGx5gnNH7xHQPkPh56PyvGQMgEt2PHbZvq+0TWGP8msEDrMaP8FXwABvjg5OfFrzf8/gXDFh/GjuAbszb9h5sX9Ur4f5fw+QUAPx4QmTTX7fpyeQh" +
                    "v73+F5j+guvvwfQXmN6T6fX1LyYGBLz+YLE7RIzrn+AIJHkE3Lj+YRwBD7fW8f3vBk3jUQGiiYfo7fH+R3n/67j/VRyBbIPATAO67fsRgLd0R1AKAvr6zyNQ" +
                    "IAiA4/d0KhIfgXn/e6jYHYE0jEb3H45AxiPQjTvOk7z/8IKCGQGdAUomLjwLFAF8PwNo+xhfngE//jL7h379A5yBkCAJzD+Y4Y0ksOARoOSPrr0ff88VAwDY" +
                    "rYLdapQnYPyM6fr7CcD8rUDYxoMQ+fb3E5A3TsAPJ+BFGjBPAMb/fgICn4AeB/QJkE4gr06AE0FI+TANiPQJfGP/rjYjQEbvT9EgjBMAdi/oGsQJuCp5gYT/" +
                    "iCzAjRNU0PR0BNKIBb9S7Hkgmj7++UzQ7zNBmQI2TgGVywebN0jPMd9vkGe1oB1/v/WxP7MbpvRsbFiqKb9fOevvZocI4ou89CNMpZE9ZMcefz6CbA4lSIZY" +
                    "lCGHzH5N/jEVE1e83+9CESGZW1/opcM3zMwPMvzC7tuP7D8Py5fh+Mnml0j8KmeN/bxUdvwRnUEYAYQufabMHy2d8m/L/UfChx4/gqmx5HOY9F946/Ms/VTy" +
                    "H+HaY8kHR6AGnfzHi4wfR+GXZdaPGWSAI9Wvez8C8CAf4Ai0funHCchwAsbTMCEplyr7MOkHt9H/3Oq2J7rUyvKOQj7f9ln2bZP+IpJ+ivmBHX4cRhUOXyb9" +
                    "VPbRdY903SlY0FGASNB/di/2yejw4cV17wEWcrvh6tH7JjC+yPK7j+8+v/T75b/O8oeR718b3FO86njHe26XuMjDaL+G+f4Huk+HOCp4xwP79Qs+YH4vEnun" +
                    "73g83XEvkzvIyaelnUruojC0H/9OY0lDJ7jjft7xZOpCN45KxaTOcfF3/RQs7ci8+W1gv+COX+DVL/DqDgtm8OqxQV7/KbfLKrfboDro5Sm363bDgkzFY8zu" +
                    "kzsGdlXaUWp3v1VT5WeCewbAc4GPv9abznE9ysyOb3rkm57ETY9405296QXNSEE9iQMQMOUDP1Fmeh8xRujUji66w7KgnwCPF50sn/98XHeQ2gVI7eC+33/d" +
                    "MrWb953iOt73YXIHJmoQPpvCcsIs4+8DBE59pPGXiejjfmMav4vocdg6YkTvtvb7uD4iORobHEWOxtgFb/pFtm581/M2nlcCeLqt6+rUK5bigQu5ML46Czl5" +
                    "14u465AJZFHIzXgOPiJjBkcWLq9xHLC0A0s7sLQPENbR0hHCesSw7vHOt3nnK1TkI5nPcNcBO63uGcurA4qFvK5EvqKY0HXzQ+rt06mQh3De6DkCGYRokQFb" +
                    "TGD9FGURx97eTwzHevo4DoTK4DGbMzGdjETJukcgLyKIA+BuIiDvatr+Udx0h1aPmMFnzuD7TS+3/cnu5W0+dwGGcwGOd0Fa5wLdTA3lQD1Pvh7QtgHzUTE3" +
                    "fD5iOQCc1KbLuDSceDf9OFzZ7XC8AOE+H6GcNHCHNMyahidZE/taqYo7ZvRhB+fjGQjkAfjSKzB3QjlNZvRpfKx4+52q4wtHe/hBXMU1FSfqcOoqrYsjmYvz" +
                    "N6CSJ+PX+BLI8xDpPTrkaiD8cfvDTOpVH+fiNA9OQeBa3hbxyeHF73b3Fr3vdveU5g27J0ZzRDo/nlTGk+6oUcfR6VdfHaAMB4jMfx2SvVMR30wRn79N65PI" +
                    "9qJI6+l2043e4jiE5Weq4qOI9uNTi+SQ3Wv9vUEA8dyqYgGWeFjbkcO/0nQGIwsAwKTWc8I3rm4cFowjo0hOpn0aykvHIKByAGgKpkMOgB09XynhE2hu1FCe" +
                    "N0BO2QI5tp+DSYDEZDMm/FDdJXYBAsr34ghUfFnkWgH7Nx5DAoWBxJVdL+vJ8i2/rewyVHaY6RUJ4qWm4n+tM/EbNz9i4gfXbJf4ceu2QiZ/rOwa92zXLB8r" +
                    "O0jssQd8jdA/7npkH99zvZ2rN5DtpoTfo/bbwk6j9vvCLgxLj2QvSNReFnaZEVtC7ROj9gWTPbbu9baycw0uegN/j5AtgDh3dj5Mmg1qU/hmw62PXNL1i47Z" +
                    "Hpiq5jXbjxavV/ANgrbUuTnVdhK2jwTa6kt+MWhLGV873vV0Am1twh/pk7CegXnPp7tPAsWhjK9/rtx9Za9uQNuAz6S2nQn30/xX/b0Bv6lI7w9t2zQPAtX4" +
                    "/b5Dk6TB33Zr67V3mOghJoP1ngDtkbcB8F2AeB9k2wYqxTyIJYTcOT4AAN0OJwCPyBB9MkSfXMcBuES9l3Rxr3r3nO7FtXdf2fx1qdrA2AFdfGE0tl/p4epn" +
                    "wl+wSkgSxIsiVIATIGQXXP00usu/zQtI4gZU9wHYOwmdAeX82YD4BOE2cAaRo/4C6gXRvh0nIRXbvV19wKeiT3Vvm8r5oArpH1Yol2980j4gybIvU2k4i36b" +
                    "8xVd9GdxCHTrfin6TRyI+ErCawOTARy3baDo7wDftLyPv5u+IY9ApiOgYfximnfgbWv72Lyjsl+Td5C/heEAm3cC193lfITx+UInATC+Zpq44wi4Q/c2Lhif" +
                    "CgONzsYjhaedsv6IfoAYPBnrgLgW/rqDz34gog8hMD8gIgDJwLS9f5v2X+AHLsgG7j97hJMx4N4GZ+ECuJf4G9GAfhXPAoD7DTCbBll2u9Yevkect/uBart5" +
                    "ooHbzrmA7OaNx2VsC2Ym8kRIBI8tHSdK/7TNByX416QfYIpfE2l/Eh05SAedYHEVbvOMtN8ZoF82fweDB4AjSvs9OoSCCWEeZ4BM/5rD81VLR1hbZfyA+DU0" +
                    "PvjuBmV2KwvuAz2DnFQKgNTNBlcfwgkWfwEaeUE28jK6f2lwbvAMg0NfOcObyfBmcjGoH+UAlyn5C6UAKgW0dz/pu0+WpzKPkvnJ3Bh33yt4P/OhKfjtF7ft" +
                    "M8aRhEcp0dVna7/g7yB1y4H/d3D1g+LvRI79ZH3M/7iR163uPxUAYbTjw3AksanLnumOYv4nSRs7v5/HnQfjDzwix10DH8sAAI5M/u90GxcMcrzzxQD+S+yf" +
                    "lVxhC3oB91RN3vUi+Dc0fv+3YUfvUuxPx6XkLzejPxs9Xm+ZO2B9D9a/K7p4cTEQPLkAxdwpR9jv3MNvsgoUV38pAjHzw6bPYv21+ku2tYvUzW7964z3f8vb" +
                    "m2FfUXZ0uydh/rer/ijse1H9qd5uFJWDom7nkSkGxgbY+mz038HbGmavVPx9l/dTBMD8H+J+4fx/hfuG+ZtCflrkm5t2EICG+oXHZ8QnAFKIDl/TNT30DrvZ" +
                    "6wPwE7aXfhZ9TpgdP8/7bI+K9iDomhE5/BjpKdvL7NkL0ziu8VJL2+nJXmGrk7HT60afB4/vweNHRdbV/B3/lq03qoqYka1XHYO7GvcJD2hfW9naSNlHznbl" +
                    "HI9Iersc7wPek7Z4zwfazuTjadrOzPTDPtOPNtMPnCFcWPHncfmx4mebv+/ybiv+vBvVqG0i+mpAJ/OADnb3uvV3Tb6BLcUiuZrROn687MjYO7T5x6npvwL4" +
                    "I2hbjod2auV6L7EjEJwO9wz7Hdn6aQ/xWw+wnoIiyVtBcTVnvSdwn+kCoF+YBHlroH5kfP9+ZiPDKcjE3RunIKpevzf9nbj0d/g0EACEhd9S8UkvMBxMvqwX" +
                    "6LAfADYBu31VDW00O7jDnG0k7wkIcLL3tflfxP1Nh8eaP31r/mLMHyx3b2I+GWG/HgHY6v4F7OcMX9tDZZ+ge9/NDxHa5ThBf+ny25miCbcdwnwgX6+xHeTs" +
                    "YU+3G3ylaHKK7wdIBMW9iPmJG/o9uTu4esrrFZXHEDQbd3IzRXyo5tsO1KtoSonsJq7qvO3jZhHjM0M4nvt+FwP/s4fHhvU+/raBnBHj6w7YTdzMoxg/8nia" +
                    "xdL5/G4gLwn3Dog8evewS+szD2OKtH4T3LGHL4J7VCNYB07uh+DuP3h127zPyq0HpnEVUcqb4F73wX0SsiPDeETVG1YnY3v/loT/DYwXTD6f8X4D4OIuBO7A" +
                    "+7Zjv56nsARpD+laEH2RtGeaOOWI2wmuDkI1bZu5xwnVIDmnCoLWJnuLCqaVOFtGX+2VfZXf3kE15LcTNOUyj+mG8SRIDCdBj83qX89aBSjTg1P1uYPrXOA6" +
                    "J8PNE3xcnLaWJOwHnBZJOm4frzVK/5i854r0POjZdVfO4I9AaRClX0etzf32Eqnzi/kP5BzmZhXlmAN32zJT9jKjd+DTr5WeWbluSxwOopixmkkbG93H+nsH" +
                    "LpzK1sLSsd0ma7pln86DdlmlbLt5C6rW+5V3pwn7TcrGvTrZoT8zsjaovN9WbOGBjZMV/T4sGM2aq6mCTXfoL57Wh4kc+m9s0I9cjYztX1frF+RqVyWfPpx5" +
                    "VRT8PHsy4857U7BVjdKXT7zsiDNWCNKgi4fxSrI39mZOk1aal914yDpXTt8wRz9ydVZenpfj1Zc4BVFydaznV4362YybGTtxsipHZ8Jp+eonBumrYGZHdAOB" +
                    "ezOUsA/HT7b372etvoJsWGBBkLL9dc1G/XdNWn0GkKiFcxh4BgRM6xU3U43YDn+VRlmA2R0j9tit754k82DlQ492pWuNpE0O28WVmxl2TVo9cRVE1UZjeHwG" +
                    "kiraq2Bn10nNjKJqm0V74ENA/j+/xuojgfQymU8SsMvNMLOLAekLZnnuzzF2JlwrhuxFzQ4P8m0G/3JNgi5y8y+b7F2OwkGBPKRcKzH7M2iL9dyzB8git/cq" +
                    "+Ef8/YBWNEytxHkD0fWcqgKtysoIA2RzX/w/gKYRjQewqZ9Ab4XUynk8QwF3SensOAbuPvTr9CwWmn+yPjEOJDB/Kjutlc9KC01kA+gD4qMP0HkctNbjQs+P" +
                    "1gcUUwEEtHbg+b6LHz3RWza+r/G9CxjADfbpIAuIXhUA0dDzhQsQcaBdX6aABUc0sGt3rAIi07QXvtY6aA+TnTulhSeaxlOVv6FrnasA3bLNOImxn8f0Frud" +
                    "Q9c6FaCE0JkqoKttsOXD9ZupOl4V905zdNLkbap8EHt14LRxJLOBH2huV/FDrV9BWEeM5yQF3ALCE66N2o4evBdjesX2649TOl8BuOmBsndKB0+0XTV87RTQ" +
                    "k/cAbtwCuJnkNsj2wb3l6sDd7B8SRPWB3EIiCBDaKPmUHxCqC6MYgCjQ6hHvkSlA0tJYJ7ruCvjUQ+d20Vvo+X+zwI/U2PIPBk+yZRv3PO2qMNmClzuw8y6j" +
                    "ZZtky3ZG/8oqbEzOcjzUSUItcGygZzsMbvSV3pCzoPYPUPsDBucgR3cJDF79maAh5Tb8cSgvDcAQUkkM04aXmSjsUxUoiBplInyjeUB5fglMzJ5g7sME3p6O" +
                    "+4dAviLEUCYXU07gJTZcsKQMAnETuwXRnJnQfVQxni0cXkP3e5APujJoahrB28E8I80PWmlhZ2v/NIIHeD0N4jxTcRW811Q/jiO7nMuBJ1FzNhhRlbSdwYMD" +
                    "UNYefT4F+CmoVwXi47GNUxSzDqTULjuFO5uzE+bT8/YC8WG7B/92JAMK8P5BXnKo8wA/nXe9rRqadgbzkN4Bh45Y2GXPypAHoe4KfVvi4QzmVNQEIv64+W43" +
                    "fOkpYEtNFZXZZTF72/aZnZv9G92XlQgPDV9mplKT/pKXSguR9fck1gf+wyHaI5t2bO8Q/O8evtxM4E9hHRq6lEGdFNQ+UzMI5JlzeIaM56WQYmw7gHdlZPrN" +
                    "OB4W+OX59vM0Rjzdfpna7VAep/DebEZy/DKR5TG9M7dfjmPAt8v52wnyFwHys+nDe0oe1Pk+SSYmMvNmEDiOZVnYF0nZPcMDH9CSoOSVRVRpUrJFeY8U/A0h" +
                    "g+L8lopRGdyj/A7eQk5fUfL2hJxzQn8m5KQdI8MvTMxlAEMzMqLo46PPJ2uH9wCvFdDck/FUQm9hHd3beVRXklM4x769U4T89IGVReobxPISEkueCZkd4dW8" +
                    "TCcCv9HUatsk3179eB6+n/1dQnfoFNCQnunfZ6G4TP4/mswv2syPbB9eqy15SPI9JPk0jKvRHT9bPGoUs+okP30MAANC4rF7LO2xItdaqkjQPHGyCeHDPv7y" +
                    "CKPA8dzwA0szzp8+uAIe4NjpbWU1cSPFVGVtb8eyLTiYzFj2xHmRkQ9KmuQJ8lt8J0CPh0Q0A01ky/n7gp4grvo7lpIv5fX0ZEZ/WykItlYk6g24Aonq6Q7f" +
                    "qOkB0m2GrqPks2n2Tl93g+h7Uk2z7Xx13TMZOh76OVZtZ7bsIo/iKcFMjeVmvu2RX+K4HehHpM/D0JTtvZbamKMX8ZpdHc3EjWcOpkr72kckbwQW7Od7zs90" +
                    "Z1eU9p4jvw35EszdSydTmf+svpBWlY0nwWwV8esThPcY8bXWkoZxNxFfcjDHPSerx+u10g60dL0H86eD0o5BdPg+f9fKlWK5RRGwvZHbGbfd7TiZEOpX3cw5" +
                    "absK7OQDOfNEwG5HsdwHql414hvxSOuIey7PhqqndZaS6eex1eNV/561XtZjd/6gll++Hb9rCsaX43dJUTS13N4nqR3G8b2zc1hqecZWOD0/4PhFq6hu0v62" +
                    "VdopSmVHTt4nOX0n+ZqRB24L0/Yi4wCeXwouwI0zQC7gNQv/gghwAeB3Ad7jIO3zkPZFInRR3ucW+QU1kxGPWT/PYHb25jhncTiblNSBgIkq5NpuWR1U70O+" +
                    "1xgcFp4f87ynvQkM+BFNO/DyDAb7vOnkHER4pvxKFsk9tfWD2p3hleJi5kG7LEaxgKYdWLWD+rmVvAAZPr7W3/hr/PKfbvySjR7/IXhf0GXfxvpU+4sYsOvm" +
                    "0+IU6ObLfH0ZxRM7kz5TOgT045Tohv9Scu8rCaZvaF1TNz8oDZ2kNHi2E3lJQT8T9JXtHpLNH4eAbB/j287eXkZd70wpi8Cuqu8c7LlqmYX0F16nrPZV+m9E" +
                    "NzKrL/RWLhQSwZ0wHzWJedHlp61JTwJcJ+uXldRX2d9bem/WbI4sLnJg5YzIq1PSFN+5LO5XbBpIp8JxABDlPls9xn9EuZ8PlH67QC3OanDdpNDWph9ycDSt" +
                    "S9B76wbyo7J/ofPp7Wnf4r5Sf6v8QUp/lc3eKZgYePg2M0EvM9iz7fVNVp+x/1oGwNIMSgHT68VJwO52OIh/Kbjf6wEtq77xbfrPS3MEj8P28ynkP69MeaTx" +
                    "rCJ8eXf5zYhepK/M5C+vVaDU2c8bue2ZBEQ+AZlbeRb1v+wcJojt4uK0LLbmeL79s9PLRo/prfaKltd3105e3/r+ZDSWs53osJEfq78LxVda5GmeLZM3fqe9" +
                    "4iXemzda67QyaWd9uTTvKf1rq9Syxf+IsS05PRLobVJ4xy0bNKzaejZIbyJkqCCTj40ec/y9gkuoqR+V60/rAhU1z5M+iexCrQ6AkpBG3mpuHqe4Cnt+095L" +
                    "rKX7cNHXIZ7C+mrZ0HZNpt824/aJizwvsD4jqh1FitfEekyq8gRdLwqsLwp5NTZwfI3pX0DbvYDAfwFrz0Gs95lneJSMsgjuspmbv1yWFGhNksb81o054ezt" +
                    "aX4Dyj0xtzcr/ZW0uevsS61NL4ldbkvalIS+aBo7u0WpUXD41HSuV7me5vyClh7RADLuVZHjG2NbEtk+vmbwe1iU5y9S3B35PsosOyW8G85NPuLvykRPd/Z3" +
                    "y1Q2SxKrpXeJjK+eF+VJuTWd8iWlugbnKWdJ7+JGr5sarBvIT+H+WZZ7VQvsF9GkC2ISs7D4TiLAx+n2XtZ7MpPaljuHeHqK0vdksvHje4F9ewqk+XGSt2r5" +
                    "5brlbz7oqudlLn/ZkLcOcO0C/tEBzN05Wmz5QWzN1HplS+2xwxtxuyr5LLuSVLa/nc+fUquLuL7u+bALIJvHln+bC8CWn+z55P1ulRPi4/Xe5F33R/M8VvM5" +
                    "pniJGd4dw5NoPlD6CZ4Hb1egDv+x+bMSPDdz3E+431anIX+Q3wmfdqcdzoGd5B55P5k/XW8xHw9TXD6pnm8+6a5RLNgpb1IM2PV8Ky5MnIprasElKe7PLTsr" +
                    "mxuWbCwzfDMRoEJPbFbwuzl+t9uoFcyWJavT4cRkbzaYTxYTeTvhfTdxO9iXKyd4iMJXzZqlaPbl+pH3j815ZPT0WnJ9ii+dV2Xm9fZLjtf1keNFkktziDPs" +
                    "k0Cpv/YJ7VV4n1ccL70tu25IH9sYoIo+f6oEdmhvEUE8bfXXgrj67gvMH8qBX26jzMNGT69bPmaef1vzV71Sz27NDLPn94H1QVLLKU7uh1BeLBvx1e341k5t" +
                    "G4+CXpksGoBhP72VvkH8y1cifOuSnSS4H1JxfaPpMEEDvTA7CoY/VQLDAZDl04thHpDfgrG6/mEQ/CH5A1TWwWgk93vdtVz8FeUT0G4gArYGdXFO87hHp0ov" +
                    "v+EHRMXgPHP3Mkop+1WfJ8gl2EHEgUNgp624yaxKrLhQZzO7U/hUFCZvXyyjHoyWPuV3bM/0WkbdQZ3vohrSjn+goccTPE7NaO/Ut4Jo6MX9KnQprvjFKnRs" +
                    "6OobPck9pK4Zd31d/6W64n6H2o7ck0WStluFvSxD17wOCvCs1JAZJSDRljhFW/w4COTkXws1kcAeMnmdklTn5s6nRJ9h/g9U3qHlibv0FpYXnASq7p+FmsaR" +
                    "mjxQVeMLaV3E+8zO1DXDUxn+tZXVDqustp7kicrDe+EMikb8dJi3HUGp1yK36JEjGPYns6fXstp/ifb8s4r2sO3T61Gevxaq/lMuVGXDp9e4/+zuxovBPqPC" +
                    "Gc0w14bi/Z1Wg6j45kplLNTWxapJEbzL83qlOdqF8xw4zosh4aHa31N9JdoT/5TW8lyxMJs/Ydfk/XK0y4I9bPv0HveHct9Due+bEtsNrNWg2J3JjO4HvVjx" +
                    "fAq42Ss1OkXLN1rplu4HtGrTCv0GKf+07Nk4jnaFJSX0puwvWo03mqEe7P20dckKQXgL5CNGe0ezN650/8IjvDMWSNgfeD6x9VNAxk+/cdpDdn+S4XvoZm8y" +
                    "K9aqGu79sgGQH9YtJh7tW32BUm/E6kKi/eFjl19Y/lrlG7/Aere3vyx4T9VNv7CR5JWanQrqpQWLSW3YY3On15D/pPdJgk/ZIf9WubXuk4DCRN+DIHPeyHYF" +
                    "I8VMvb+DBLfu+YkQ4FUiuF2t9wcqwfRE79JjHukw5ZNsJZhMzzcKfp8S6kk85CU7PiMHJKvn12tWNcPnAufvAPD3hQqBgfalpQ7Y3frymeAP1H7N89mKt5wg" +
                    "X83w3yzY0hT/01zvB563wfuV89+O9E8HnsxIf0/w3SU2Kl+LrIPMALLkecuZTuAAgO9n2+f3O1a3a3baTtGluRUPkuqdddftq/vFyma3at7Vfe2xvZM/63c4" +
                    "LdIUBZmzfjW/nU/wXxaMrmik2RjcD5uhriAShAsFnhJ7+Dip3GhnNG9+Mc/jzUgfdvdQpE8ye7PRaJzpPt/2095EQ+zRwuuROdfBsRbnKbnbTnHp5A5hvw+L" +
                    "tLMN8eVJsMkbba7TJGdhlA+YmEEtz9xMcWWl15O1GBvV+WkuUetoD5s7v27pBPDuAed2UJURvHtGEm81I9t1veffVfpqZNtZXocm9D1rcyKX2+iyi+Q+bBj9" +
                    "dP2Dke/JeJ8vyeTfj3RysY9Cvs7QObUuo1Rnn9VfsuPcTazerKjy9MvzIkV7+wPWeWz87K+/S6jXjb2mevuc6NtMz3OmdwL/o8j0ks30xLKVopatwJO8P6zU" +
                    "4cVKugH0AelJc4xPy7TL1Qxh7evG8zB/VvsyZ4zPosEHgaNZIn8U9d2cAUhM5i2Cx882zz7+tTz1P83yVLZ2ft3nMVSOoiDeuCzPPo3uCUrHwxhHVnd+Xcng" +
                    "VFEfj4pdSsKhCD3eZvV4sdxfs3t/6OYXPgLtAd/bMDqKSPo0o4P4erbZV/e7s+2ChiTm9+Dm92Y+Wz6/R/k1xuuU7d1elT0ZIgfc14YqHnAEGliuZc3o2wh2" +
                    "6Ry/2x5KRmR2bqf4CONVCznkFHdiZtdxjmvb6rGUjihyQNvqAdSvWOG+tEF55AznQcyjiE6PlHJJYpA/iU4Pmz7/+SG+Bs4frn9ALnehhIxRPKXQdHE9LzL8" +
                    "YikclWG7yA1XpOaES3jygrf4zMvOD8J7XL1NwJXucWFp9bzdnrVfhWyXmE9SZlCrkOeilUT523QBdQosTagm8TBWxEHckcWT7fJ7CTZN3ji0644QnerZI0fb" +
                    "fVbYB37eZGoHm8Xh6rQDd2Obv2lJJjGQh3M6Sl7/0tT8o8ryH12gpom560Q+FHObfl1W/bpLATVz2Q6k8F2SiS2f3y9Z2O5Cd2ok22ltlmSi+dTheN6utRvT" +
                    "ecLooxrQyB8GNMoukifF0v26YbcfyH/euKS36BUhwDxzdNrJZFCcqnYuWhXWiIi93Y3Ktse+3QsU52kkl0C7tKqx0GDOB7PLWdxiJ/CV4B5B9CeUdjeLK5Y0" +
                    "6NXYc0Tj3KbxRnU7bLu0/ii7ao2+dmmlov5liHtyMqMfkF9zpfbkZEM+R1IcbOvc/O9N5DmZkwS+vOTzlp27KvE8rsPdderFcHZQ6qvlQ6c+Y5tGL9YokROA" +
                    "j3Sd9KC867f7leI+kbdHQK7XKyfW3q7LdxlFLjojIv6T5fOLTh0uzwS6zgWV/IVbM8GZesjnox3My8tCjVNffm3OOKZWKbllSuB75q7zvQ9D2FXNZobnnYpe" +
                    "VW/XukzF7lUAL9028kuVM7fC66u9kWGbKdwGu9UKbtLmiRN3OYDPpi7vRzG2OV+0W5W2oxjp23G8uBJ1PwxhrIDtuSuLmb4Q2D+xM+OfCfAfLvp+pWLiVVph" +
                    "v0dT7uFS66+t6MaU3GFjl9fbU/4C6/55wDq2dvHx9wquht0KtXCQVHd8CuTozRMpe4VqslyHQl2aykM3pyBPyptaakmkeNFm+Q/c7L3iknuo8/fpvey5ZZXe" +
                    "az7epl03lTdVuy6Y1egE2uPtJ+uX8Pb2Tz7eOAZNie15NYGdput/RckXOdkyeitGNLaMfFDbwsaMmLtL3+5OzjsCXtsqbR7WKhyWZ1pCvuzaeLs+cZ25FD5f" +
                    "Lk9OgoXJxi7hr+zuP0l2x6YuL6B5rzYpXbtNSmZbouDdKEefzso64yglIlKImZntHd+WcQ3a8PGa63PkcszZgvnMs2JFLb/G9/3UjQTh235DatjO3AQ5cyPr" +
                    "t7ip3yZuY4fqO9OSjVzKWzEtB4sRXVPiChOwsdRaXI6pVLQ+J/NJ7UU/w3bxE2z3tBldGl/T7L8L62VV1/ketdNc+XWw+kC0PaxEDMtKRAnbsdVLvX73iuwI" +
                    "WJ7clJ0PmgrlmOMfknupqYYEe7/TVJsp/j63VyCe3wjqBZvni8z+pKNol6I+yihfWyXtis0bGrShyE2bFFBLbU3t5S4FKbDleYrLMXzHNi/1taffVnRCQMds" +
                    "wKUteUpEbbsKtx0ac16N1l0PM7aKYikEMzOtP6d07ujjT4zq+oTTygXIZcFp53Dt3I5CbKvMF3XRza9KMFPlcVMpfVXRYgOX13MUFyxKukBB5QIv7KKRUhtd" +
                    "93yYqHKLcuJJM9kvg5VzmkIoKH2WTNZCSuDr8TCpnVkipaeRiiq6dFkkb39k+W3Ybc6IDLfTXU9CMzVLop0eqEpcBTTp6RM2+8jTJy7gRh1Plq8v1HOuPd1S" +
                    "lmxhbkwxO9GIYmF9Od9utelesGt3m1GIVHEtOM04g2IF3roMbQvTfJ6aKKcNSHmTtpdlLUJWvRijiO0eZ2b0CqQgBubAmXf7slnr9fsYNbIBV58WpIg+vGTL" +
                    "l48iCuNnYBMGRI01jbJIaZS9Mu4GmkUvL5bfTpWcfrXBRaRkTC9XoE095LCl0CdBvDQd+Dk2TzPTEW1fMJAnWaePV+d1+Sn1bVcKfUbb9xfXYftItn8LyEMl" +
                    "3T/glCSKW6u1V+HDKoT4tP0W59rSGI9h5jPNNxdK3zaATK9bit/sN91CbpIZnagq3xdmYaXWpMWqTXCjq9pyQVG7CsYsJOflPAPrWBt7yiEkDtphWJU89muC" +
                    "/HeLDvSqK78sOpFz0F8QK1KkEUhNr4h2o9038qfBhOuqltx86LrkRQP3Uf5UkuWT3nWzTkmQ2nESsGsS6qebvXZig3lkpeS49+pk+vp+g/lW6H7BWxUYU4wk" +
                    "RvlEkGQ0BqhUGndF1L0H8cAEyXXNQTbb7SYtdlODrQZWcheBIRdJgffr3uq6ttUqkyCTWVwedkRYqZRRhupNXjYXRoW2DQOTXet7XB01b6raXunslJtNyb9l" +
                    "TTXeZ4bhOinNo8mW6o67frWxttiNtXqLkVhT77bLSo+yBp/okot2/Vcalu6wwEyLGBa+zrQGS0gcsZXr+70lX6ib2EDdnlQMsxppbc/zzH63rGaTlx3VC4Na" +
                    "Sur+hHSt2zIj43k1+eyZ2UUVxYCrcS9olLEyk+SYItonEb/u6XKTketrlvuTamG4SLWQWe72kmMnXYlYPNDcSbZw7qatpiKTIw7Hy37upgAuV+CdFEAACoSB" +
                    "4kTBnfSi0igjdxGYy0bDIG5Wkts1dYE7nXldUyfnl7WKjbzqs2EamN3uhu0pJ3+9osYgq0lBqryiWuPrJzGzubly3VJGxOj7h23p0Ua+VGga7oZcJDdOEy2z" +
                    "aqB6Lsk68KYrs7CRNlTuXk65tOOuMrjFu+4KFWZROQCUMooihc9qMrKgev2kRlN3JXBhNtw92b4m/7sPgYTXwx5eX3cVKgfgz4pmFy6sbJEVCJosuRIuKkqK" +
                    "O7MZdNm7AETWM5hfLCvz50rOSBerFeVZt9Gj7LKkdU+9bosmQZNN7AoUNCPh12IKuWucBHIRnjeUO1YzYePX/HrGFbgTF9Bkr6rkLBbxWqVrJXIABN/PoLs+" +
                    "BFO81j2sLcwMwR/1LbcEeTHv/uQK/LN+7apjv5l4a8vCurQMvJVlm8kRgj9lfpeAX2ngjU1fX/dZl8XFG0WbqEVO01HS5CRfPUym1hfnDYvmUlImuyggt9UV" +
                    "mf85q3QgZh2/21U4pQ7ausHgrGa0VuLRLLJZm6x1j8yapdWWO0VyNmzy+no24i8WzT8Ji4ZNXd+PRjTSqT+LGCajamKXF4UvZ5zTcCopoIDlKltfdichr4rl" +
                    "OaKUrdI1UEtKDS7/8eL7nYpZEkXBp9GIp7k4Xf2Z5G+HCh0Xl4h7T6av7fWiugc92+BJ22JZVoTzroovH4/cGujrgXwKFpM6TqdK2+pl0z1/ORtFz0MWNZQA" +
                    "hN+v1b/ZXOUNdLtOxuywHmfYFREBm4q3X26uKixJ7LV4oawdSc/6YnEcubCChqNG7k9Gb9f1e8ci434scl8AtukJ5Jpq/1naBOSsEOnbbq7AFvxJ1pywH7Go" +
                    "oFn2/DWwhWF+lDjBETn/cVVt3Q5I5+e0LynUjlL/qMKA2lxhZ2MTDtLRukIvUICpeEAugG3f3ivZ2dQ/QlW4FbRjZWvyCIpepdcVb+o/5QnGQ1Ngy3WrJ7u1" +
                    "7DtFa3QE6AHEeGT6RumqaJWbzfKyM/VCpwLF7KHzggldjlOSFQHAqHs41P/JnERmdgRunIFIZ+DvKmSXzqJGinXlp8gRegLrAkwXxwjai92FcJCIdqXVjWBz" +
                    "0TifZUQobN0GW/mh0lWdYqY7FxCFzMVeyXA/OZW2CndySlJurwtC0FrS6P0qY2sSAZqx/uVYA0+6AbR8e7++Zqty1WQmkFaSrebfeaVo+TBFkdQMDSobapbl" +
                    "d7PSI6bsUKCpkSFWWD5MykYBCB/LwMf1BnUl6NTNEiMjaR02wxQaBYpifw2187Og2rLp2+v9NYakUbckjbgkAFD7zwa+ae5Bpx1lMBSHhq09Lv1WuHQssBlp" +
                    "aXHE0MCAX6KUPuiGhWQhx+cQn0yIDx+aevJ+S5Q3Lon+3EZOaaLfETXqyA1EgZ+x9EtTwxQQPjZpez0k9xe/8p+TX8mWb+G1YD0cgalfKsdmTCM/mB5f0y7+" +
                    "swzKDuijBq3WLkbeBkqbWk3TM9J3EdAvRWy3suVekzm8xnz2Ykh+9fDttKEsCgpmGFn81KcCzOcgaubRk+sVhZANCg9Plm+vde2gA+sAfHfQiXVAkeUNZe7Q" +
                    "6nNT3Eyx8QrP1iyTVA77fJp7KyK8txj/oeYrw/4liHzPGykckrPEtv9J287ke1DiDzfvF1nLTaSfNR+FgSL2l+iuf7WaWHNrgZRF4X1VkVW0APcp6Ao8Jfxk" +
                    "/vZe3E6p1x9XVSrsz+okaOzvod8j1xhF5QSWTg/uLf5U+S9CCdFKGz6dgnjYV3rWS/CPE9NzcXF+GK2aDR8ny77Z+vVaxP60rpaN314QAAD1h0lHB2UZNXfk" +
                    "3qph72CGqcSWSqFsvev2sKSxXlE76/2EjZ6oBK5XwofMAHF7ppBDQveP7d51RfGa4fuTyVnG2kknoPQMi+Lj5Y1ifWO+rh+QnyX7JNxfF2WdD5urHGeEv9xY" +
                    "UQsWpyrvfbcfFlU7WFRJKA9WeY6rPEXYDQv+K1nYT5uLVsautwMYYqYu8fU/Li4Axm4uvOg0qpFKnMPYVgNOzMwz2uM+oD1hEbU0zb6ICX1kfi5kfhMVDpwH" +
                    "bq591YuLilhbIFUtr34IyPbthSAizNlEaPxE8PqQnblU54oys7BSwTpRUXsqWGfWd5nqO7WcRC6gRsMWYOPORWQXqyAIizqrSy25W9HoUq/bp/JnScPI/Zes" +
                    "dhLQQMVsxsGri91EWPgAOAHUAYhLmwgdShqy7Vr+7WOxkrwV9E1OeiyW8Zo4ZWr7TYYuOQmYhh1lA8ZiR8dFrRyfGsU0S7Uy99YOjleefJmLFj3chbjDHfl0" +
                    "Oge2qHMrgbPuj4FVqK1iNQUOyNJlbsIPVBzHwhhexJKChCV+oYyezN/yW4FycLIORNwd6E46ANWWjL6ZTN5Pyg4W9OtYDeFs/f9JxN9lCbGB6j7sIkZVhZW9" +
                    "NxN5gdGFZygnLEtnsmHsRnvxk0HaIpbyWs827bK2wq0elbvHhashe7Zs6PZ6+eAFXbsLSrkLkjcHXtxjzxZpm7RtesPY22gaPi4f6jE8YAe3OurdkfHA06eH" +
                    "pp3ePdQWg+vVQ2XXt/eiko+zjFerScqHsfjZr5mT7XPz0NxJEYSqZT8Xsl+TeR9NVsRdz33bIFbNV8riyPDtvZzt09hV1jq2dtxKSB0JYYwzcwMVEdj1rzvE" +
                    "L9Y6EnNXe7D+0LNLuyVU8YDkhGXuajNNq/aSmC20bauMIMXuaCd1Wcnbehd1ZiAwQUKDfj8KafIoyNts+vaarffX2NX/j2NXbNdWX8/VbcXncXA28nrRYeC0" +
                    "tmRlzr693U3ad9kSkRCpj6oTqzcKFgnM9YAe2bInyYtLDFpFTbl+3CGYDedy7hCc0EtlYfrEtBmabEcD52V1aBStG61tMtIJvUB2pPBk2FZft2RApPQCkdIL" +
                    "DO2Caskkk8JfE5RRJKz8CZCH4wTHCBo9CalYwWwRPO+Sr6sXJ/LVpFtRl3UnWRf1LLxVNLIYvBPBO4Dh8/TcmRXlCYQJog/jpu5ch1XkxGxhjL2SJrVH2kXE" +
                    "RJ/O0QBhYhl2pwSu/X3Ct527IaK15V8qsv0syvsF19sDj6tD4552dVZCeABfNfNyhm8iX+nFUkYRwc5Zuq+WCq7ChbIjGwX4mvGM4MRFXtx7MmIYWawWuZi2" +
                    "51jvhm3fvA/hDuYh1xRLSbVb6U9PXGpdDGd2B6tQfq1rBb9bHUyjd3PyxugVOUu9PVGvqCeLymZi6kIncOG5IxeEtN1G4GyTx4V1mfwicGb3yyUeuBW9GAnH" +
                    "yZcC/y7Cfrk5d0E9ucyZ3P8Di8EvdmcLAQA="; break;
                case "classic35-3": encoded=
                    "H4sIAAAAAAACA+1c2YokyRF83vmK+oGGuI9nsR+xrxJICCQhVv8PCj/DIyKzumdyQWjZaaa7uo6o6rQ0d3Nzj/zr3//xz7/9+i//7eeXewWH/17+5V3P5eW/" +
                    "/fTvV3y18ZB3L7w/tJfv4RXg0Xn720//+eXl02s8p4+nRfiexu30yunV66vUsUCB333M44VjoRLH7fF7Hg+lsVAK4z54gzo+hn+1+Ao+v6p/hfFIaeNd8vgx" +
                    "3uhPf6YPEz28RYyweOzjvvrKtGb2uGQaK7cGz4AFYd2xYIi4UvCw0q+vPhZKBR4c38dnzQ6Wy/TRg642loFnjU+S6PUpfvvpL7+8HH59+PXGR+Tf4Wv8DeM/" +
                    "Pz4O1njd+Avy+CrjS76n8T3iT3rE499X8TH8PY7FvYcX/8p30/+Ky1dEiRaDe8bT67gDfhmHOLw+khsAC7whCczueyEOAPH4IHDE4Mc4ZH7gDviG8Grj1eNw" +
                    "E76Z8R0rVHhWXHANeDSrw6NZncW1weLj+1hbcE2ERFBcx4rwvBPXQLjiSgPGgGBmQtGsRGfIeEigHUvAj/H3pkwIZ0XYA3B0g1FmhP2G8HgaPJMgBiwSPkS3" +
                    "IsOc8AtYABA3RdLDyj4QxIVhrXgLFhgnMi+WEVmfEGLCHiEOADFDG9oPQxwBYkQSvo8DOCAbh611QLl3fMgLuIVJS1i0TOBmPJCCcSCMg8E4IMbjO3C34Akj" +
                    "yDjCmBYeL0eMG64Uwokx8HGc5Ilpe49xJXArsbgSxhUxRtoqyE4wtywmFJTFUSCuyuLK7A3jf0SIx5/gCWLhObE4TxbTGVL5dllY3BCB8ZUI4vHa2ABihjaX" +
                    "ZyweYXkcuY488y4gh8cjGKP7yWGM0RGPZW87lQ8OIyoRTxwI+AcqwGHCF0N0IA6HIzYrhSmVrLjChxBcM+F6cBfI6g2QHwuwHslkwrMXYDPTujBKhYElDlN4" +
                    "NsDCu/gkwM4XVuar5S6H52rDM3BXAC3uh4HFDNzwRGuY1eDMHwcP6dXzkocTY+xm/q0UzRFciomVsl6l41rzymGI6cTkjkjj2ZOyEhmidNEoDQzmKB1N9kWE" +
                    "E37ChJ8QuD8BHnDDB4Ef473gR4bfGGCCV8OzJGD6PTB1G1LX0wNpRueMx38i7PHnRDhodh2/V0DYWeoWTbd5/CUBmaoJOPENRDiPc64MhBXZGp6F555RZGUE" +
                    "l7jlHDHYEbqV0U2byoozGwO88T4NB6RwsBSOGqk5RAPAdQHYLxSmEI2nSOqchoHJcY/QEAs6ZWGK9ZCMgcnlSme5r8qsokopGx6vKHvVUwW5aHhsURYet53H" +
                    "JLM4B4OOnujW8jALfx+TSaj2rsyFJMwEpsNZCyFcX8g6ysEA7Tj4QlqIx37RVNGCCaIY83KF3AukxSyeKGWIZuak2y9Jq8nWCueBpp8cpmOvcOYTzplvAdK0" +
                    "kZZQ82WDMyuYopolVDciLb22CWkrwllENj/UzR2P3fiekMEAZCMgGwFZbkkr+RZqoU61UP+uWoj1lCeylqt8S/jiWUGYwmsvaQp5txFb603efc/W8oatVeGN" +
                    "DO/7oijesbXuWTftbMWiSFAN6cc1s8eyyHUsi1ynsogkVUiIbRJsy8TUZwQTZDDJl0KHsdg0OwCAOIzyJyLhAYgTWUeQOlzIU73sGyELf0ZGygI/4eQoC6RU" +
                    "okmFS8kguU1D+aXEZfr6DU5KxgbPoqRNXORWxDMuKooQpQqozAooKZIZbyd9sha5XXLseONRlP88YQw5PNNRg6IgkPPqYUD5E5zydRxMD0jsvJWqiJMsRGKy" +
                    "MioFw+rvMM7wFmmyl2nLfMVKi1It4RScrXgRvoxnHgSAVXcDxpO9YXoaW65diTs1s+cILZr5w2+i2doSBSEODDwgBtVQMBUTieZoRXPlyqcg1FEXRPqCwuos" +
                    "2uC1sQPcgnJ4Wg9538jVoIpoAAqIR8cYt7MkQoy7ymZJvqegAp0bMbDGyrZGwfSJKTMrjzn1MsYG3E8IzFobeMyl7gzGfvoSRFhCjSFlOgum+thNJZRVQWkl" +
                    "hKCWrRIqMyYnBrUyfykmt6mTTUx24mIIltG5h0I5YcpNmHLR4eudmNyvdfJtpXuXea1MxlAPL3+XeN+r5PZGJVvmfrXedXde1bVMFhviWiZXSbzhbeK15e4G" +
                    "snEjFdvo0jOQwY5C5mZKvE0SL/uRmH/FlgwMsNcYvRRErJov6t2I2T1ick/sNF2aVZTSx9sj1ImgTpPIFunEYs0rj+9l84brrHzH8+jBMkm8wps5A08n0iMY" +
                    "dA/AmzUL573WFf5OXeVPXVU5OoxfSDYrqtE/zMNQ2qKucotV5SkVJ+XxXucKvKYmSp8Zk0sOvnOs1qoXsF6NSYzWnjPxWAiInNqeggnkREottUsGnw2FD884" +
                    "eE2/C8p5I3HeHA3jWSXjN1cTwtvWUsjoQq6elRcSC7jRlz/U8/+/elYY49MOwqeZt8yALK0E8SENY2cr4aDqmnkRmECKKi7xeHL0rodgU+/e+jOplz5SppPk" +
                    "JK5T+bQT10t4XpJvnK3AM/lSE0Gq3mh6fmBMbLzdW4HC5m69ZjiHsIkAslnhjfGpbHaBeBsoOlMzkJpHPmTtJ+zqyraNCJpCurVUC3Jm5kI8zqqXr8Iyguxv" +
                    "9VXGMyGjfyY8XtqKm79RtSd42e69tK4uFNYmoykJE4HjjRFZ7xVWvbA2FoVVhMwgoxXbmNxD5wrbgR0PYY/WdfaSjKlKcpvKirOpwPn3oDEm94iKl8P82zZ+" +
                    "u2r3YiMBP13C0zDVxY30mn9ZXQGy3bYAl3Trd+XssUDqiKvf6yOB1NZHGW8lw15B6zLrVtXH46xTZ1P7CCqdo2grwTM+tiQx6fa+gcnKSgc1RDEjll4nM6iB" +
                    "e8td2wRM1tVwewOBzY3T1eAeYEL+Nm8jfJpQcuOeVkgU4VOQ3q7oJEXYmxRMB4ois3nuZZco6ZhGltjshbbFoHvZB8ysnJulLeVgChmonDE2C6gxPe3i41s0" +
                    "dB5aR9oiPUY+xkxcZ+MAw3PWoteI5XvbCryPCN8TRi/JwKaPEJWstxm4U1NhthPqVSOhKmdDu+ref8yOQjDJ16trxUMTEpYL4Vs192auh/LqbuBZ3GZlZHNv" +
                    "V3VV9EQhfDOT1s/CKVp8Jfc+tqxcpNwbib/UFvKZ+EtNheQP4wpzrv9UOmNRvSTgtPT/OPNGhfr0rSjzYsI47chFQmPOpfGRREvtStqvAZpgj8asnK38I0Ln" +
                    "JUJnY27UJUIjxv4Ytco3XYV+mBuee4AKbXzQA1zn6SBOh3Oqrq5q2kzVGSqTOir9swYSBvLYrhtI27TVnY9F03TVTNP5i/WyW/pHJAxSslbHWgXvU1cipiVi" +
                    "x92vlB7SnNxYI7YdyQmfd3zLRQ9J1bSAHJt76nVgyIYfgbr5YDV5Gn7htkPi0A2LINadE3TaLGniUaHCtJCSLeWkeLGWVr1R1wA+FWOezh/fjyGOsMs3MqU3" +
                    "Pc2peuboi34hoJ3mXV5l9WF6zBgsuslv9rQ3mfiwp7OG62zIXSfcZW0ZwhiHohzbQ/l12+Av0m5CpJNJ1Fwjbxn6IjUTn9nxWAfsKlpcWyeYy6eDz2urf8vN" +
                    "Zk7HaYq+rI55Usev/lac5KYKGVWU3pPWEJ4Y68DETtwaXq2txGk6ihVSDKJlm5ZFa0tGKdtSIgu4sT21QpqdtHPigmCSDqt9+YbKn0xDU56umKe5jCr71KyZ" +
                    "uOOOor9I13ZqlgvlfJO0AfB8MRf9sdpdfhv68ErpZEotsUNELxcW2oWr5euRnW6ydTG9pqqEbhwdmoxUaqZ34noJwrGHh/UU+o2coZnE1IfwTYd19slZ03fg" +
                    "eSw6mDW+7xFPQhsL853Whoma1O2obBRCSzt4nbn62KZixexQXBe3Q/m66qVs8FsHOQTjY5Ajm/FnoGVQ57mSChevixoOZEULdqm032QydnU4aCY6xJWvXxiM" +
                    "XXXWiqIoYra1Lidiz7i8DMaWxfYkln6lUfhd43T+8z5hvh2nu3Wxmpm/Mi5Wnn1CkNIKZ6rhjw7/76TDr1im9nDqiqd0lg5DW3JrmXhetgIveoC2Z2Q5uk6+" +
                    "lqthuvDF4XXEsWkP8NBMa+48OwqzBvrw16p4lrwz/qajCLro51cjqW38LSeklqeCZOoPG74N5yQbsqFhZdIQgk6EdbTRyNfpOYt22poKUP20e8kkzrMZmswq" +
                    "mQolWBOL/a3DkRLvIDPBOC0DXH2hLxdBW9FLLI5rUA7HXgU/9fGpmBKXuh5VchV0uVFIQ+nUKExre79o2J4GBz89cnkEaHdWTIpxebqZzPLXDLFzsr0dYv/a" +
                    "NpRwbkOZM+ymsC2fZNvUeYT9s7Gc8p3Z9v1UztoYTKZndGZbUVHHtPPtJpRLFiueJT6VwnYjaLRj6+RP+j6V8Ok6u/uNCc5YVXUnm2kYJW0YnSMaq1W1CmJq" +
                    "LAhtjftcl/2B6dwf6GSE/Stbx8qlT7XvPylL3+h7Z53z6lMppuV519eTT0U7QR37RySsPI9c3e8eWz3JCzNydvZxaEN2F+R9dgNwdneNI+0Ink1FcStmJL5w" +
                    "Id3WJ4IdRaCb5j27K+VPEzIv3d4iFrPuU6i69TOf4Bb2pqLWPu3Scg4ILmPaavkt6x8dt0Ix1b46L8mDz/+bkLyR9wf2E322Q6H8FvuJjg0o8SokK56tuT92" +
                    "9f5Od/Uqtq09nIbtuGqPvHkhOW3v82iO6OZ0eo5M4fe9It34SW3fPn3hvBmOtlV0j3LmQVjG2sTqsGN9uVnBb21BqYXlrrxNxXrp/VazXSGrdJbuPsDsDGmp" +
                    "7s1r60BsZal7l/m6hi1ihFnm6xTd9tRUvpm7IrMxlHUfd5zEtnYyM5qynwznpPfT7WUZwKhSKMnk8/XIc/a3+8nE2ZjDdTDz7Nb9vXbYynNzyJsSmREShI0N" +
                    "KX19uyFFapsZrdVXnNF6381SsAxuNmvPCzTkqbEU2P7jexe6tAJD1djnyNvwFKqj3wZikxa9tyN073YFip/B7T3QTGuX7/Sn8pJl5yU07oTUPta8XFNj31K0" +
                    "lUDF9O73sTmnY62X7b3rEui4TIq5hoYC1x9sTCAjA0NxwzDXMBS3thoZy/7ddHvFFBOQL4yM0HkPPldEZg7HL4ZVViPjZv+Y5l2NyKK8D4XFrV3TNsiTsxSE" +
                    "vR1+XeAmQGQyeq+KitmrQBJJzCvCe9dXeXZ/5tfUV8mmXsE7TH2lMHfXnlZFhShbFsrWZR7HbXPPmdEOO9om7/ZzHsfuP8kLvJeSuVk/cjtD2Im823x/dG3d" +
                    "tROZzHRkmrPNiWfWC98O/LvM3gTNxIk3bMfdmsocgfuil8t2lYXEUkph7K39ltOReokUnmx+50mlTy+vsF4h5Xbf2GwHfVcfaPWYjUFVb6def3CjdlHz4k0Z" +
                    "FL94WYV3V69SNPsf/vLv3l9WjHt/eg0N1GYNg+eAOaExmVHLyojN5aWO0jG5zv1505jv2/w6bwQ/bSoz5nx1TQ38iCmby9B56RnNrpMRyqHdC2XNup5bgMHa" +
                    "VYUnLo4JyWJ2Cdp2UeQofaoso5PXr2YG4jHrBix4ycdSu0qB7SHEWIOLpeVUa25Auh/eRHa5VTDsm1H2rgJE7nrftF8H2XlE8vSdyzKBcbUHRQQ0bwpkab8U" +
                    "Q/biGp9cCse2CK+3lOnlkaKtiCbeEfNx4+vSYYL1x2aFaIdriu7Sz+pJEujdbhGMMgr7X7iNN65gUgAA"; break;
                case "modern35": encoded=
                    "H4sIAAAAAAACA+192Y5jSXLlc/dX8AcCuL67Pwv6CL3OADMQIAlC6/8BXbfNzXwh2UUm0F3IysqMJSMYkXFo+7Fj/+/f/+M////f/sv99V8f1yNe/b+He7ir" +
                    "pfxwf/3Lfz/Co95/464HvN/Xh2v+4fvfjtf/+pf/+beHi4/7Y1J7uND/9OWR0yPFR71fKfcDXI8W77+7f1/3m7Hh66m/fj9Quh++3i99fPjr/kL3g3iX7o+9" +
                    "v0D0/VN9SveL+0v9y//Bbyek/qXuP+8vFR087P0hzrtHCvCg99fojxXuF/f3Gi54pP7i/rBw9Uf6Gz7S/bf3Y4TQv93k+rd7f0S9Hyfjw90vsoN/1P0t3f+q" +
                    "/nCpwuOk+te//N/7H/+4H/nxgy88/Lrf+CmPH98f//79g+9J/U96z/16uj/7/veUR7t/VfrVHvn+s//g8/16uX/F+3e4f2d4eX9ec/0T/wZ/negv+gPc3x58" +
                    "cKbf9zd4v41vwSfGeiNNOFeG+/pDUGeAOgPUJfefXUerY33/fceaQHEpWqw79qW/T2F8f4VGUDuE2mmo4UsF+FIxAUDeIF3hEWOAB7o/qD9Qf5H6s0Mh3dG/" +
                    "f4RxQboI0v0vEzxq/+QL3tkfJw6kPSLNvwVXeK9AHeld978U3oiINSLdALD7e+qvdLDxHYUAHmDfn9g8gt0/sNAnjGdJgZdZfSI+SL6/qXSDTSj7+IfRDoD2" +
                    "BWjDzy9XNOmKJt0I5kAw12HSt9m4DgWbdIc5nS3aAcwOMKodo5jQrivi4tiuAe2IaDeEuWmD7jj2P/sDeQJbwVwE5tvn9H9Tt2t83qWkYIYfDb0mkDp50wPu" +
                    "YuED4woYl8meC72n2/OAuJvnTwsMMX5Qpg/NYviVbBh/97fhE5O/IWZs/YcWDYba/+wYw5sVDLplBJrsOZYJaEdAB/LhmeyakEaAWnuOdOhfoz/UAJqCwu1H" +
                    "wIEjQMFpoCEGeIgBAb7j+8mB7qFFCQdk1RmehznAw+QLcP6Z7LkDCVD2nxhYLuKcyar7r58wGfNw3B01LyZaAStPmHYcf2oZQOtnSSabTfA6frAHoBs49J/Y" +
                    "bZkBDvlDoHWUvp+C16M0NGlCupBJO0K6EdJ+IAwmnQhpNMWGHrcVjbQHpMEd355C2XRhjCFIV3ikEA3U1yub7iDPrhtAxpCS8BtKZbLpgXU32v5WGFCLSU9u" +
                    "G51vJmQRsPG+CEhXCtfdpC9EuhLWhT61GJMevkA+McUbaUYYLftLSGeLdHXwk5N8rL+eOC/LFKsLIn37ekDYoUljEtUuBLqtFt2fVYVDNACc0VtXjM2YPfmq" +
                    "AQ4AcIDAzd6/P06T5wtaMTxtqsnCChtzRxKxC9Zr4zsvDta+/w+WfxtzYK9dlekirJ7ssYAdojGj164jCyvirzk8j3QNLT+R1yaIVRZ2R9Y/7rX9Epkp2Wb7" +
                    "9WvmBfYb9lk2Zjotv2u/NiaTq+5gl439FmO5T1Kvnmvj02Rvv2y9XqO7M2A07zTQ1b9Gjj1ctY3Jjg24EqZsxhbdSDF5GHCPyYxq8R9m2WAVGayieMyyM4Ds" +
                    "saK60yCw3OymqMy+mizYocWAr75/tPAyge8+x+UO0sWRlMC+v05/+A42PnsY7HiKyw4gx4jcpgQMnDVWVP1FD8yYgHkItRdVVkGb8g8aOdowGTN+sIrMVRBr" +
                    "YtaBjJoR9OSG+8uflhjuTE6dE/JKIRwTs0CllE3BBGb/fYdN2XZCh31NofmUbUcy7fCeaZt0O09lNKTb6ZyFraEZnzV1zr/IZXfTXupnDs2ePXVkh64tO3Cu" +
                    "pkybs7CylM8jHlvTNrG5SGael9icp9h83VgPjD8oqhBsMBMoUnv90RPuCAl3wzQsj8oKTJwK6Tu7QcwTYV7ItB2ZtiPTNjU0hAkqgIsO0PioCR80eWma9G6J" +
                    "R7T9Ozl30BZOFVvB2sprsLFD8uOpkvJsupcCO4gbdxSkPWJ9fwvsjhHs2OtoAB2N3MHbiKWDlLuOXgl2QtgHFPhAhLdBqL+kfL7De3IANhdX14eWvY/TYNN+" +
                    "hOutKx8FlqTdlG53095l3dqPF8AIejIdazLp7seLBO1TZ+yUdFfjx7Hgy+HQGdOFtNeFdJgKaTbtYNLuqmppa9qjwEJvXHTULipqV2mjZUnBOWpXZdoCsr8+" +
                    "NW34ySX4yWW/mjb1QbvbBsivKVPDjpbz2HRsGLwrltK1PffiaIje5GcVzZp6JhhqfTpBDV4hVlVKj1bo/W0ljPwRv50YCGlH5bNXLZPA9dVFoVtl305n321y" +
                    "45lM0tZJWZXSbeRnVXom+Ml+QRrtuvdKGWm26w8idlgyNPjBNYzYzmGCdk1t0Nmq42h9U3/saWOs6KKoTvYMCRmG/G1jrKr+JzxN8rVpiSVsmSds4KS0dsYc" +
                    "VdFB8i5OwiVR00V00kV0WQJ1UdYcjDUH3S5hr90oko/grfsso4gWaP0vCNQBrLmgNZMDj3VAbDpiyTrudm6MQQsuQAuOnjjcxYpzhUXZWHfdu6Ssgj3XJSkr" +
                    "u6bYtam3uFNyUTWtkfYUpx23xvApMTVMuJJmq0azbGCGC9Ycp0djrMjLuO2B4kuspgVi7z+sp43nDpCBJ4zW3CvJE8rxbXQPbU/qvnSXbcPyO21PZ9qeFfP2" +
                    "/mhJovLTfFs6oDeSHdg4oJUUzNnudjXodpi4tGrknZ20PqrOwKqJydxIyfS00E8JcPGxALJ2hPGBFRdAFhoRGXxgpdrK23x7zsNU+wQAv2iAdVGNRe0w/xxq" +
                    "ycCotlJQ4wNRtm27YvA9T82TtbaiiVU5jDKcROXR9mQXviRg1zqusm2TSLa5r62cra24wzLXViMss8t2Y5TRwf7QZWcw4wxmXPDnziV0Owfkgu9bumMeZl1g" +
                    "1zg/6JFpgA1fK8DXihAYkpubZNg34Sb3KT5D3R9gcJ4gX0xQIqz5dnfaceO0/SELu5TTnivpoAL0HGdxnpFlcNXjbIK3MEBHdtqJsM7y0tPrTT1NGnxiErTZ" +
                    "aSf/xXQ7aN4BJmEhjXx7nmjsSitsUjR0mq2+MmzumtiGKKVk567J6I5dgHXUrIPR7+5eCp+DZNnZkWUHnl1pN56eF1fakcMAenjyBE64SC2NqI2JlDHuKpPK" +
                    "Yf1ZvDrnY01XV4yy/6Arijk3gJABhAxdaZpRSrId9i5ceCbcJvNno14nGk3X0Bj9YxYPfpprrDW0X9vfoyOq+iXcCh0Z99wc80u/xFOWlvQoei2gy1JAT6Po" +
                    "ajpjw38nNa0cZZWMootAnL9ILym68e2Qd9CTa4A6TSV0omid7ejq2o2uVvftJ3pJAOsExIvU0Ad6yZRtLyZtg3VG941PvRRMzo08Isy6mTQEaZhnrMl9/yzz" +
                    "aB5XMdreDDmKNLNNu6RQ9lYkuysSncfTBA1co52ZeRC/atC2OQYOvB5yMkrCA4+23BFmD88oCrGZsm+uoi3a3bCjkIk6zPlUWsUx3Vhr6ewEZsrJ3DTdcNwO" +
                    "HY477ukl82yjGdZBMKyDMdsIOkpX1QyrFKWDNE50j4X73QFYB8Iv+SZlrBqbRrCD6pUYm86CNrptrI1a3g036lpHq4wsM8hMCpx7nqZRngjdI0cMe7D50qSS" +
                    "SzKwi5BGLOHR0ZYJXBpm/ZguCbS4eW7VDqPK0fRMFt+sLHj47DaNKjkLgzYJw+rbp1kYGEaqY3hVL0MVqyusXEhxNIaMm+AN5+BspxkqC6vSLVHEQG+oYvX5" +
                    "pJKH09t55VJeecm+3KCBYv/LPzDv1ilYsP1tSMCaKqg1PaSCs42USDP1k9iA7hLnXKSG1nTAKNzPCjxQysEYY9/y19li2BOr0uEGrImDkB1lYPx6Gr0xVV3t" +
                    "aYELqcTOrEZPjNqfJ15gWbphdlyV2ag1CcEEaO5yc4i+diQE7oqNBmjQDdDhgHl4VajhHeBtTLJ63fVTM6Kdlc+uVG158uAVOuVNhlnwiQQ2YRw+HV4Zy47Q" +
                    "FMumdRItj0jPM5hx8qZlQzs9BEU4idM8Q3VMnrS618bJpcaUQg0Ei8ZokPDJlyIZdkBjxrw7ct69D9FhDtFNGWcjcBFkLp4d+WiItEwMrEuPLEvGxmE56rYJ" +
                    "dD8F3/DxNGOtrJL4biYeGITrZM8z38TTMNo/BznNDH4Cu7tvZA4EfLaE8JxVtMu4B/VzU0RzdcUmbQaUthe6MkCr+O22IY3N9VXZ8QLLVF8V1eYu04BS8A0f" +
                    "E7rBfWdw3yVgLhYwF6NSmlsm1TryTTcURpRtw9tfo3TiGcTaKsli0kuQ5lS7J9neeOxkPHYT2ueGPTZSbkrDBNbNfNLt5xlNxhKjkEa8cNUCX/dA288z0EmS" +
                    "MozMWEqjRVd6PVFpJfiGT1ndgBu0FAaDzJk9DT8KadMrQVcr2Zl3ZNGVaCaVLFu3x0xtlFTXJM3MbtUQxQcK+dQ1Ccd5Ja7mkP/OWAfkrEppJoMGpJz0uOwk" +
                    "Qded0DB3QqsZYEWx61E9jxqrjr5JVqMqyNGS6o0VKp+DPA+4yBKYQ/g0CQe8E+CdmmHxV+xqozmHiUw052Vx5OQUsecZx4R1N++xhxUiE8g4ZBtuqLVv8EUe" +
                    "fJFvhntQnYxMsmRlS6x2yoU7xTKZWPwUq4niv0vKEKBKVG507EwMQkbC/Q3/1GTrLR6HZProIrzB/plj9vkTW4eaEQ6f8vjXYN1nUWDaXnZz5nSMB9OF2uJm" +
                    "QB3HyOPFdo5lZquSy9AFD9s5mhZ6boVquLccBCyymQyqOAeO9uuEQGb9+GiJYs87kl/XcZcJ/lJcZ1WVFTOXThD3A/lybqTgiEMwDp9OLzc8k7LPzHimQXt2" +
                    "kZjAPNfygfx4Jj+eyY/nU+jOVHpRGj64oel6maDtemWjvi4mDfeHuO0fY8YxFVzCMsr0zmPF1dSuRsXZB1p6ohVKHECrkVZRbqFI01tjH2Xcxc0UgTl8PMDU" +
                    "CRrF7Ygt0Tp1yeLkwuNE83dmeWO3YGlZ/oP3HcWfW0f+zqxDExOiRrsgiaxt2EWjaTJsXFFDlaETf7Rbue6lZJWLJ0MNxQL5AgffkQRHXrUjb9JLySraZyCV" +
                    "9nKNAnoQEplgHGL9Kti9c5ZNqhYmasLcQZtp34OLspBQ1mmHO7TBiTg4snI7wNT9dKaUnUbWg4zSwW6KCKwHHrR69+PmBG2wj9KuB45B2ylasDOTjjrqrqJi" +
                    "NhfY0Uw6ikCv6i4GOHw6upx273Y04HCgAW8o/qetu5XsXQ/UfjJo4iU8pfa7I/0k69V42wxfqf1PCMDXSkh4Pr6Mai41DbSKIfcXs7Vjx5djjUPQDR+ML/0m" +
                    "WneDhgy5NeaG7vm/eRpeIiY1nYUP0Fmfx5UYVD0+kI/PDdhv1Q9oWulxXbMb8CXe2slGljJgYfV7Nb28dkyTukwqGzF4qwK27eyXOaQnYDHQU87NeIZP297b" +
                    "NAysuO1nHEWsVlQPjKeOZ6rJsUtWdl2yeLbinadexxumS5aadtV+6ZJdSxJmaEXXzPvdW3GbeL/lOYs/S85dDAlhctbNbs9+iUSWmERGA0s04Dzt5rhlN0ea" +
                    "KAA3N0+2bdEl6b7tzTA6eerRmyftTC3SSXeg3reqsOy0g+pqyrqzyrp/vFqm3bvtKMIXfl2XHkzvRCE5K1N3UjH3VGw47ioU76qG2PozPTXTMBNLY5m2Qx6/" +
                    "uoCnV+/cIQcrE39Qpd20u3EjXtHAa31ruTYb3iAugRDZe0s8OS3X5rmkJl0Tl7iDEgdvMFB7VJbjw6Af2Mw7rISEplagefdK97XH3LmOIeZMSChKv6aqpmo2" +
                    "hATBOF71iw0UaCo1b2vqcPDm7rxJTdPMpX+ivDlT+6uZZl7vjrZW83bHNkreMMqwmBoNs2tn3G6oXvi5YaalaHiNoylzHSE4aE5wUcThcuAEe6rGoTUKsVtQ" +
                    "js5/u192GyeYdt6n3vki0w4Eex7DLYemKpuX4M2vp0V1rEYSgcyaFmv9eeNyTcKvtapeUnGdgwfZ5JD2WOQhh/blYbe0o7cxuHouRuMkPF/BG7EgkhfPZruW" +
                    "niaQggvI0eXfxv3nN25GOYbvwq3IRwc1jHRN8XrPPvKb5Q67czkx/e2sg/Y6SPtks96xBu6NRMIgKGwYKWd6cFj3O7ZjzXIosPV+R7X7HeU4v9Y5vZaq6qm5" +
                    "oBzjp2taepmHFrReiQsyNaG84723+/JX0Rs8K8//Xe9tVmpBZY5G18QWzToZ91MLRZXZp9prZqJURVTQZMIiwkUyjhwEBb1oXylV14X22MGM0hIVXGPy3y60" +
                    "+3Brv6DV7MTaTxl4k+3pnojvlqhXsIXrTxXX2Mh7oi+45RFudzpGS4W3qJvKw51qgOv9ncH3Jmu+rDpCkUS8ma13pix0PcGoBl2yRJ23IjfM99br8rrQFoxj" +
                    "yl/sfwcedmg2ip+WtoZAFT4NMBS2/BJqHamhMUeKkk5X2oFbKoz7U4lBt2qeFFnbITkyFaez8MuYS6iZhCKDofRtDlbdhO2bRI5MM7mTcAWnzR1t1ZmE5xqV" +
                    "1FqOrHDFxfjGfH09Bc8zRXhLMytT8ztPmifXxoebvlc6DTGn7fm0ceGaSxgM18wOMRXJ7NLtM6eq62Bi9FCH3bTPDMfMpmRWGqHsM/CRyY3y2quNzOHCOSXr" +
                    "C7eCccyfcswU9egGO15E52iRNehEUJKxLriHixhjgeTc9WxQbbvhqofixXeXyAIYNKmOiE68Tk1SDXLQ0hcZM7uct4yjQKSEa6hWoeuOK79s0b+osncHyrC8" +
                    "ezmW5ysxwZkymsZatSYlcJM0TQvzVdaqoWPG4KYP6qzwXLIqnrWqDMUoPtnmOImcvFi6VDPL6+/niyZpiVJOpjujx3mWkUU47FyeBlr5KIGxaYWXl1pVhegn" +
                    "Am7y1/fTMSv3PO/juedSkk/28WwxTQOudbmDBlzJLGpt2qJBjbbydjat9/EmYpkzaJOjVpMP3RgdbfCkyUZFBL6zLNgOMe+4T76z6Z4Mfdis0OZdAM7HBOT0" +
                    "MWkUvGpOente8jGzqVWm+cc1LVbHpaieGf+bKN2eyD6Hs1bCsuCXMLWDJt+y6sHM/5lGyGgvQw9vcrKt6nOZZOi8MvWgCijDLKvCDh2m7acpl9GWhUaZYJyC" +
                    "/0VaVdWq27QpFbvGyiW48esPzDviTPvv/jsZydjdBubaCw2AcTqI23hUtutQ+w3UbNoEdVrlJT1l5MqNF9UWTUYoAZlkjt4ftQZKnRS+0yLirlf0WANFIE4h" +
                    "f1XHvadkXg2xWbPKT75cWbfest2F6pPwSTDtUKzUQ/179nc4XmvfQHWW29CLJDo7c5RBt8b82JzHxG2zZasSsga3McTInQwx0dBlW0urelfZyuLdDvjnlYlS" +
                    "TDBztM7f3NdKNid7KiPqJoJwXOVEO+JJKT8fVzHTTkY0vllLn+YbQ+D7WU52LSkZl1fLfEMzUFb1ULtNPWVkddsBfa5wI9Cmkn+B8NzpIoPhhaat2MkrHlme" +
                    "eGRmnYNF3N9hJMSjIDAF5bhhoGghyYWQ4Hd9br/m3awI3LZ9bm/63GHViC2THFledMwEZQY31V8mCIyW7NqkbHNiC76Ozkdlm2S2bcckq+OZt9H5RcdbgU0i" +
                    "g7FoNsKYXYWHG8tacV6hjyIw6XY976yIwEHWbC35D9ughgesBYGbPEsq+YGgN+9hhiUYp08FElaFsnaZwwxpCA2a7lgbyzuFDjiApas59fPuGBp3Mjp0lTc5" +
                    "AO8hDKzaY/XZ9o4sA60awE5Y3tp8EzW5ZwE6fxKga8jjLzKjHERPL81NRR5saoejwRMjq5H0UOpPcnYDwjLDmj4WSNA6kptMO0w6ktOqZaCNDoHVnSVD16g8" +
                    "X1ihxQ3LRFhanwpcUXtWtpyMShURgBHq7JfdLKEOhsekXpSp9cnjybwbT47bC4M5ODIrs0ettza0bl02xGE1xkakGeDU8hf3qEkP9rJnsdK6V8u59Rha8VWs" +
                    "V1PoprZrDeOAsfXnpY1dd3sXm/la0tIT46GVJ4gdDyj9Tqmf8+wgaxtFGGQk743Sv3VKpHg+YfrbmmeYzEoOtleYK6jMmbHNH9BD3ZyBUX/7Mp3tdC0l88uD" +
                    "G7qfDS4i5pm6TwI2CtZNAWU8QFxaYKezG9ZHu5FET61Oboe4Z9T9orY0krrAMFCtQioRJw3PgTw1tIPqkUTqgFfph/SUS9DMH1NAf9/B+se+gyUI5w8IoLtt" +
                    "nJ5wOXsCK7zghinS0DtaRbPu2LqvkYTsu5GXe093DAglJIJgul7e7MSzWJESjLwGytcqBTvOXRUBeRRVXrIulh1LWqFd88N4OM2M3yE7hs3sDCATttl989xZ" +
                    "2uj+ppczZ16yE+xpXrV4bzu6OJ1LYov2Uje/QeHfX17QuhfmGpalfMaXBbM/EcOaOXh2FDYZevy2YM6mYM4rowxqKAE5++vrtM9JwcZPt86KkHvt+Dm/1PNe" +
                    "BlWDNqRCNZZTyZ0pYi/o+9Zx++1+Dl8+80DxuQZFjJz1qkkVLeFT98GCQnEcq6xzip2XiWScLttF1URDqSLBN3+sYKNnVCJswnRuo3HhJhp32Bv0rgNmd7AW" +
                    "ZljgqzlEMgCzjhuI67ImmydCbx5EIWykcYgOZmIxlLyN7w6GRHKt9ALIs5vYc4Tm7ViO9vA2RmEH0mPJtjn5IBZv2iZyDg562VnCwU8E783o5o+nU6tqu5V2" +
                    "nlmfdZlASjoGjKHrCWPoqD4meu1To/N0feHU0g5nyn7cL9x5exbJW+8ddd288sO07IG+khTViHG5vlCF7N1kfzoLfWimGfTBs4Ccv3qatGqyUJElrFntl5kG" +
                    "HWVHA4urSBV9kBWcyt5u2fmgQZaFOPSS4KuvDeeDVNEAm7LvoFgGGuxLyPw0qRL8t8eGSTi0qNa2lQttthvGE66mJpJeLBsTbydKhdwjEYjzx/cYXsydw6GI" +
                    "3mzSvjgsfaQPlXkoqRZpw0sByTzY4GUiDoHCxU67Zhj2ZQi+wgV8ql0zr+JUNXGq6tx72x1jqCLQX0T0gF1AlAdKo6KWE6Ufa5n8Xrz6h1+8EpBzrL97n3/S" +
                    "3qdgmz9ez4CfXIafXIG5ZHNMzjAEsbQKt8/dsRdV1m4rfuWSkMjzM3O2RDO8sbLVFDxf3NDXDIOMrX7Usp3wuMdJrEXzgqdPVWm7YpCOYtB1nUFrmlgUhmBe" +
                    "2YBYUjPEOf1Oyv7sSRlDnPP19WYZlVsX8g3yqv87bVNakgnbed5FakveX9UEWWCOGN1I0T0IAY8nDp+v3DEC9Z0sM5L2j6C2KE1v1JwXn5qj+Sz47NT8cVze" +
                    "IOYAt8ua6qNU2sRgcdgqXRQ+pcMLOQJzzt9PzSwV8BSyJzK/M5dKtyJza2+UNirbEzlg/2Jp9snljSpy/ZsrljoDN1n4fF2ct/EW8TF7Gycqth+n09LQtnoX" +
                    "TRx5ptF1Vk49UI6Omv3QS2GMc66/j+r82Y/qCMi5+F/MJoqHs6UbFpHqnO1ytC2XiC5bvkklau9RiXoH3G1PWv4TMYoE2lx+80z+tLUWY5vr9UWtfj/EBLtF" +
                    "bhXcOf+mOYenobUjLiixuK2qoI3ORPEyQqBTWH7ur18tXVEmVuWU5U7eWZVVgbifjnPurE9YehOb54MMQaEXSMpVDJJjc6G4m2U9Fj8xqXs6XnbnWd9ZsM0f" +
                    "39OxlH0zpa6oWrE/vLFJuF9uRm/pgVOdtTrs+k6dRfOxE2u/O+62rmlcKvfeXNSZD9SGtxYq6/bA4VP6fjZCktF0yQBvhrl8rC34+y7DP8FdBoG5XvFXrUmn" +
                    "85q0YfCHsYPzrT1pJW8x70mv15O2ur+KkXA8Mb5fvnp6idjeLW3bPelt0j1LhuqDankr/NvNWtCtnzLKfsfqf9hYLdhW9+n9Ya0LGwhkurthxlqvduGLlNGq" +
                    "foatOvDXAfCJkeJq//hset8RlY/iZgvHcEvzclHcJt3UbYtaiuiSi0meeEVjqy4+mI3AfBMeW053sbTCZzTQZsv5s/bbzO5kkE8eBdU4s9FZRYJo/YBu4t87" +
                    "s6HkX2d+ySvpfeue1aZ7PqxKntfp9prd26zLbe/Ee4rCC5voOrIDw8QOXHfd83Y2qdbpLDfQLk3Os8mx6y6o1o/5JbonEhTngECmqVXMB8mSYK8iPblaedSl" +
                    "ibtJRjpLBO7EnDcb709Iv0wcc+OMRtoLBG5Y/FWt0tEWdFQiUt0GnQC25YGOPJsnHkPVQBXN6KMZ4BqvX7Qeu9yInzraXZBtcs4HLbFN16vqXfcgJzRe6JSs" +
                    "xG6mCNqcq0Ci/ey43TiVw5ySYE7ER76PFTZReKzGRnU2R6daqCUWrEiJvbGQ6RermyjhIbRlBrZ+yiox/CE8Slqersa2VZtkswe9UeKfrgTnwrTAa9qMZQX+" +
                    "Ey1wt01HrXGafeHgQrfAFqSDkigZqnHe6nSHo9hnBpSbWoqry8Y6b9O1VeyzGV3BKpeJoxKIRaqvAFy/yihpfG4BMq043UJyq/anKaLSHzi3UJ+ITOW/Qy7u" +
                    "cFN6JNULzeBibq9TqddFqB9W3dXEWZ/IGAPGqN42LZF2oO4XuThc7BoPkD8F3frxwGKjvf/mbZyxe2VMur27erXNxd47rvHuiRze1OjTsIlVYr33RAncRuh4" +
                    "OJFjlQ2sYHOZi6idotgYTcZF87M3RATkWvJvBtGfm0EkENdPVSwm2SEl+9kWyZKnBXN7os3dTkq+G6rvmD4/1ZZyz85T0i2Nno1tzyKxCqRXCbe6HF5swj3t" +
                    "XWGcJl/u5T5dVUolpAM57LnKEbsi0b1Kwu1l4baoQ8MCba3xixhDY6o0M26uZx32TW31TMPXTiRpFqk6mpcIiL3of+1EptYtOyvCjvacnLCGvNLbv3Z7GoH+" +
                    "v2YiyfDdbRpeNLX+ygpTRpVmvlmbVXGl76IVRS0QiGv9VH1dD6u2zex52qzOXT3PtTfj5jyvy9K8uTtrLIfCtelh58NW3czbfuKswyYwaz1AuxU9ncWBg5TF" +
                    "yPeOZZpKkAYZRZjaeTwHxgY8S7o2pfibFYNbUK3tm4qA0N8s5K6DVFbbE0gj254X695RmDLqYXUXodESIxEA09MVeEMWKrtrlBP/j/lBcv/IY73MN4UlUv8M" +
                    "iTGjB1hkFxo71kPFM1IiVmaxg6IandXU0PiECSowq34YA1xb/rq8VDNHr66xXXeiffJefMEG2pH/d9SXCvNNYTWDTFvDfraWkfVN4W1H7BJxKa+HFo61DoKm" +
                    "CoWV2VtUFb2qfOqsedMP0xxAvwj41knVQgBu1/ULiJ5bopBbj4UbueY3zuGsUC+ltML4yTWcfLqPUidCr+6YJC3q6mX3htWHLj4Dv/S5x5nZqWOiz8wGowSn" +
                    "KUNFX4/W0r3jTCXPl6OsamV1LVxAbtfvjas//caVgNz89e3Ue9w+mtsn88EU0zYJVp8oGbXPF6ZtiPtqhnXepVxz7+2JK0UxmHqhfkrPrp3AhWB95A0Vdecq" +
                    "LzdJOWKHnbIY52C2FzoUfLnMEoibj18tpRXWM4+EOdtE52X/fdWZqX+4Cr+OJYi8QF6XMabBRntPy3e9n1F3gMe56z1U5OKosqj5qa6cUSI+TTiA8UPiu01O" +
                    "nfDFhSybs4Ml5mch9ioNlCic/SysIUEbqJ8Ccgu/oCVKjjxMne91jGVluq0A6K6sft+0kV3CddhSVp+Wr3ZZOGdopqx22+WruPRQhHzgZunmuSUaFxMd6xfL" +
                    "PdL9fkbe7GeAbTPI7eMbdpoTeIFtB4rdz1Zy3JG/L/dTIGSn87Rj5RBZjhiFbOp/x+fzaR5eAl/F62Bd0NtkrPtzlQyNqX9G62JhBLrDUGs9XVVUwZ0kRUOm" +
                    "SRyMwGp6onwKafAWgtwolqDNGLeP5Q/sesYyvvSna2dl0j9IY8NubYAvTdG6L7KJNlbAya8llx2AznlZ3dl1tbIHQV0pHH1vp47jzCRfvzliVzZ0k3HETm7j" +
                    "jIlWVQKgQF8YqoPsu71igBLIjG376ox600lZQA6HOxp+bG28c0cjMw9hjKe7RY/Gma/Prtg9A3s4cVql3FxNCQ85h6JuJ/R3RcE2yEjTralZUfs4QNh10lGp" +
                    "qMJ/UYLuQawoj+QsT87AqSDu6DI1RoHG4ZpRbh9vxW80qTA7c8H67sO8miSkeDtHiuxdJr4UXepMztaDv8MmCysL1LLJwpZ/4pUwuxXgD2exorRKzTUpsrUC" +
                    "2ThRebhBWg1XsIhYUdk0zwTk9vFevJZzRuNOZlc6TUpzo2mGzP1rBGiorfOzQ2fHlY0w0RMAZxxMcfssrUcqNf0k7OpqOnGGbBYmKETlzS99GMkNtdDlJGk4" +
                    "l15NmGGj8Nb3bpKmodRJUlavbAzl0EKLljzFFJjbpxOuZxT++arGtj0+l9vxLCS5W7o7U0XTMyb/O5ekMWVUV2j1dQ1nDgz7Z/QEJSSZdsn4fGH4zYtndaMv" +
                    "aO9rMIFQQG6fDkEKGFyBH2Gp08UzgLu8uB++OZ5yWtzgbNy2Su0AxDL7qeReZJ91Xl/pCNZWJJYmIAdqsDMt09FWucYxBktROOqGVpWOVymh0rygoyWAV93Q" +
                    "0+KGoNzaN505/Nxr0Mxgf1iWDvujKk/YKEACDFHfik+Ctb0e/kJlUJdeekunHM5gLVhfqokWxiRkPmaYpTu+wbqaZR2eQqflNsqkMtg2fMI8nayU+SbwCQXi" +
                    "5n0IxV8h1xRLSbUD8z0ROojhrS0nGXbzL00W7t0VDLsVTby+2M0KzCGluK3kn7fXVdppM2soSybhKGx6Kl4t6Vx20XI9k+SeXRyusiHPZTaLGRx54Nqd++mI" +
                    "pZc9HsT6fwE9SE+tl84AAA=="; break;
                case "modern35-1": encoded=
                    "H4sIAAAAAAACA+1dWY4jSXL9nj4FL5CA78u3MIeYXwmQIEAShNH9AYXb5mbuHiR7yB5gMNXoymIujGTxhW3Pnpn/+3/+13//x1//x//254d7+ODGfw//8K7n" +
                    "8vC//el/H/HRxrfcA74e2sP38Ajju/Pxb3/6v788fHpcP1PDw8fxMdRH7Y+crqfWR7ueGh089ONL4fo8X4/j9Thfz0jXpVJ8+Dq+lx7B50cvj3D9hu4fIYVH" +
                    "d9fvub7orl/1L//66NdviuOa42Ny4/9xzezgkuNq5fp6hgte1x3Xu35wXCH0cYW/4j8o5PEyA7zMVB6lPlKHC40rjAtVeGnji9eFrh8aryhHuFCOv/3p3/5y" +
                    "vT3+Qe/N9Thcj37C46eOj9f1ry/+XP/Hx0+Gjx5+7qdc376eDf+Qfr2/11t1fRyPy/W3v/40+Gq7rlDhT72+d/2q67k9j2f+lX6i0892eOzpWQUxG6+MLzOe" +
                    "GvsFs4BcGGz3twFdAOgy3sGWAWhfx1vor/cT8O6Id0Fs4G/Am3G+nurGrVYQ7wxAtwbvbmsINMI00BgfB0wZrpobAu4RoASXioj09fvHlcb1B+DFAj6uBLBf" +
                    "92OGe+V6zeMeHFctDPu4zgD6us747LpOqoQ3YOwRyyCIF0Y8wDcTwB3hW+PHAH+E3DuFb6e//fWx0OcRMG/wlWE+F+YVMfeOQK90S1T8Z3W4RmUAfzzcA3zH" +
                    "pDhgJ7gR/Q9gjwB7BNg92ndH2B3CHsjMG5l5W8w8EPye4HeIO5pn8xp3uMWujwOnIICThQ/cKxr69XPXfXPdcMM+B7oD96xxh9d7fXO85Igm3vFKRUy84OUb" +
                    "3GfjtY2/BvIFkE/wnv0g6NrUnbL0BP9nvBk8PkLYm6COuGew1042XAH1CJgX+N7P9c6wpQPuCPJ0DGjmBS7j4bfjfRHgFsjuAp3BTn876HGAXsZ7NT4Op54B" +
                    "7evzgXaohHYhtPs08jKwQmiGa9DeHMHufnrzAL/i+ni58tiN9y0IckKQG3pzjAehnkAet0sCqL2Bmq7XEOMALyXjS8l+8eZB2bb25oExBtuGO2KaNlsze3P2" +
                    "yYXAi+LN27id/LTspqBtEAc6+QD2D+DNK11GzJqRreFDs4Zgen1UYbtZe647wmCGScI2Ip3Jrp/6c0L5hUUnh34dLxXqEfNx22jk8xn5AukB+HU06+yUQ2fI" +
                    "/erOJYBPyP0KeTOQNwPcgNwTrBGd+RLA6yGA42P4F1YVwNPI0wTqjyO4A8wdYF7Hm90dIh/RxK+bMCHAxsTbYuJ5mviI49XYtg3gI+yC/0jlhHoWP06oD7jb" +
                    "vYmPCzWM3HkBGkw8ohvP1sQRZUzYbvBOT/DuBMnE+87EKWErjHc3Jm7xPiZsGLkF509jdwNjaRi7wdN6FzBzy2jrebFxjxAHTIDHHQFQO4S6YaLVApp4UIhH" +
                    "uHwqJxPHUDv+GiYe0J+jNw5txzz6edtsedq44Lj8SM8bol3Rrztt5AmgppQtIqaIuseIjp9jnp4kT09Lnt4o7eJAyxncyNMymH2D6A1mnjTsVYI3BuxKjyvk" +
                    "AD9evIUfrsbNPH3Anj6DvUAaVSBTr2ja5NsD+3a3lGSrbw9k4pF8e4Ro3jDDbkWbegVTr4BZBD8PVcGo7gZyXgq1mhj8kbBvmTr6e879xhWyvovoChlfSaqH" +
                    "VB1tFz17EsyhEPPas0cqzcLJs2N6xqWZk4TtepFR5e0Uzb3GvMv9kVUeUNC6wwR9JMbXb80AOmMd2hcDekO3XtDII4HeF2MPE/SCFisBnfz6yOAwePaoMe/K" +
                    "sVPl3AWlMuGuaPJYV0V0GzFo1NNakKuSXl0PC72C2WRGp5Eb+XdnIrrjnA3wjlS8SZ4esWS7UrzEeTrjVrG84jI8kwFzRC+HiN4loncx9U6xoXIVLmU+uPgA" +
                    "sDPaOXwxUy/HTD3f5HEczuO09eHc0aRaPYXzYd3N+PaZq0/zju5Z+ram7O6UuM2yLLTFtaOV+3fyt/A0f+sqnleTv6mUXaHdVf5Wt2S/YzzXRs4J3AQ5f5OD" +
                    "iaYY95i7tcnBANj0tYETOHashaAY91kcOxTl40ZxTsd0MM4I7M5wFntIbwh7MZEds68YNeyYCULukeAFL0V5Xawd0Mdok6tUal4ZuQOQTUDXeTv+2DGPAxMf" +
                    "JunH/VKEVBuPI1XZ1605E7luEncE3METJXH3QuIR7g1wL4Z6+1pEv8ywUVWOHEzIC9W6czAYxwPGcSrVEKmWVVV+iuacgSniDR3xkXkBPpiieQXAIQVJdJ20" +
                    "lOaQwiHDmtihxzPvskG9BnLy6N6LXyYiBUN5s/49UEJms3bvVIlWgLfxjogbj9WZm7X5uGNGaEc7F5jrp3auSPUGbHdPbO3HGr29pNYH3ph2tW5DOdIwGNDR" +
                    "LescvhoH7+/JmGMk33k3dBmDhQngRwD4vlRqR8/uFBmTNBkTFM9aVbHGQNetVOsLG9PpGU0FB7+zMXh5YmPSQFyAbp/SrBBiK/jJVrFUQ8gDJMMAL3h2MnHO" +
                    "2HRVzpAzyz7SN0y6uqnVgAcQOpxY3bA5+YqYYzoY0Uaj29opdCFk/4tfGykqnOOVctX9FOJNsTqn+ly1T8C05fN7QoZRp5vAA63aKAvLwtBg/uadn7gX7SwK" +
                    "/DyVdHgh5NdHOlgpIbjKjT8rxNun5Rr43ALuErtOvbNzHzGd+yo5LEXbGts7xXTiXNE591fefXgWh2hR/8zdlubBY1YHgHsu0lyVOwdvmRyEhwlIvA68g6Tr" +
                    "XjGulJwT5RrpKxzH3Yq2cs/DYK9PsxAyTXj1IKm7du1Y00N06BTIm2qhdQatcs02XggEcoY4hA+J9YWCA5eeMIT3JV1ve7q+0q536fpCrVtvTlk7enNm2O/a" +
                    "Z5ZnxXLvlLRzFA8Y03UwDweC/RnbeiDYqwrIzLb2F9m67p9wnRYs1pN9o9IMWigMcfi4IofeYwUf2QIE8YLkW8AgXmy/dGulVEK6kUdvxqNrvjVCvhAhX4iN" +
                    "+9pHlp0a5Ipv7c/7pcDmjPtnmHldcR/+PO8dFcne6BNM1NeWipj4G/x6k+jLRFzd+Na2ceyeiBnmcpzEBAA8A+CEc4gfZm1LLQ7GTWWZu3HhYeHdVC0+8jYk" +
                    "SY+0m2bYk/bEkQk3xvitirwqhr2etBD+Ge8WDO/mmHcLJ96NvMAz3o2Tr04yiCoVOmbbPZxsvEkTrdAfoN0U5OMFjNgtSIf0adqWwMYTFeTDxhsT7FyPHzm3" +
                    "uBOt4ylPeFbNuVmn3iWzHjZOWVsyIpgD4t1a99427RzLuXtqEQ/Kq/uJ+PTqgVI4JYJJsxafzBmVZJiydTLyImx7NOx6lUg/s/NAEVvV4n1+gk0VQTqkb6Zr" +
                    "wMF05FlDWsj18gJzZLdafR3IJ/JJ6HTbOCOs3zHyyP04V/dOOetgjoGcPThBniSQU5UedZLu4Ls6beOUq5twzEbONBwjPjrIs23KJk7kjVc+XiI5OnywcnDs" +
                    "jHRI7YuNU+LWM2dtYOZxSdo4NacQzkWZz0S3IVTwN9Bu/tRRIbbV6tyODbXBrscD7bZzMSeZRJFmuWqe5k3tFqQ4K0YMlSYRs1Tk3eRvAJ20uNHbL3I3Z0m3" +
                    "Jt2XLqizof94naxTH60A7BzPP+DWwyFZh5IsTbHTsXHmyba9BPBh4idNhLbqemqbNpOzpfuijI2baNrtflloVeqXkigiCakeuAoPL7M2+om0satV2FTGlTM1" +
                    "dPAVi2gjhOmSk0tEoMeFyTZPn2A1jg6d0Q3tmwo3Q7Y12z97JYhhXQQxL25n2wzzkt6SwwzET6CnOyXrGsTZqLce+YFui79LGDFNWytZq2qbTWFEP+tgqnTN" +
                    "5hOdlj6F2TQToEP7FcL/SUI4Ix0/7aCciJcgsfsod3ovVX+Df1ECFts1zWvX9Gm7FCqKY9+MU/X4klQPZyuPL6zcluN+kbstCseyN8fflT8NMxeo48caR12c" +
                    "eR5NgDC+t1GO5XiaHp0UEbuY1Tp2nE2wMV0xbkEYtzvB2069uCd+HRs7dkIhqPa4odzinab1lk/vSxelPUWc+6RF1E6TmRXE/aRmA4VyATp+2jcrkPAW8KAF" +
                    "rKYhlU62zpSb8DAqX2cKHfJ0Z6l0kjjWOwIGYkjszKUbwlW793DoobDuqfk1hEu2Dx4d2T9GnHrjRcpxx6laEKuuqnMm9XhkOZTK3tA8i8q1MWRPyJtAnheW" +
                    "dSon2NiTYdcplk82HoZSBOn4cfamW6XesqwgjIiAfCYGroSz4jF6ugOcdExvRa00lXLM2GtivTm3ytEbR39y68UI5FTGjq8qY4DIWOjlrjGPqIHwUpoRxJVG" +
                    "UIhYjax7wjrOkDCNWthd8regrDVCeC+6NOtrAldFBuskmRveoQvm44rIswrSsbWv8qxDDdG4OjOtlH7TSimWUEfhU48HoZtlWakuUG3xoOeQIqXrp+wtbCxr" +
                    "OyhbJ+cGJl904wz7KKYUf6Jj3gbPZAqJhcx9ieRBfR7NFBK7hSYyNxpCklpciFZqmQaqxQXq2D/UuVVI2Wul6TNFtKLaDdHxqSy1Wp1mrpO39EQJoXtd2shp" +
                    "6Ci/nDkrRuw0bppshgyLiNuoG76F8AQY47tlNOuJ/Xlk0ybx8s+pS1pnxp2JbJkufWiYixI1yviRmVcrTM86NX4YWBIznk9wDzmEoBz7N4VuBeUQcZFDdMrY" +
                    "nvbQ+lHn1m+Br4zX7tbJn9f3xKw8ZjjVFZsQBuJ5XQO5R1/tKY8T6dMM5KvyqSy5m54Rna0TVK1z7kYzZzp1kyLeiU/nIp2N2tOUIvEwDTAvZubsayNJw8Q9" +
                    "65jBu9dl0HDnYdCtPxs94wkDnCQedloVI6pM3U6Z3hXld6InDudW5nbSQ4R9As1LKwXkEHFvl6an9ZkdR2rUSSncLm2WeLPt0vm5oz9TCwFhXGBO7lMWBsqz" +
                    "Am9hhYKne0zWKYz7G5tux2S9o0dmvXq6z9WpjFZhPJ/0EM+oViRh7qh09hj4imw9nkjHyu3xYCcOt2niQFmbFbk1ceVav9yAXTXzSMGK3CZDC2In3SDHyRRP" +
                    "/iPMIC5AJx++qIjAbmk1QfxOEbGOEoc5ckiWfluQD8jLIWWvK7s+67PDyoDoeBpp16rji1Liti2cR3brNyPkUpyluz4Kp+pzuCgazXqRzyl1azN1q0rYaMdM" +
                    "iVD3uyhCoE6+/FHJejwm63prgPHqszV+J4mwVCtvDdj9eZCx8TsN82nIVF+vyrg48W3DzA3XKq48kKU72yIPNJ1g03XNwlSTwlVl1gX0q1ytdai3Zvo2i/iq" +
                    "5s+KGiQHyKuGPAHkHMiD+6p6GQycdOpNFI2GXvfWvn2nbN3NlQED+8Pc4UG/HFkNM+MwzyP19wo0odeDV6MJGnK8dxLeOykbptUpCiYofl0qtLxAHgzBXqa0" +
                    "kQlWkbUGyL0Mxd53KUxV5HwR8RPmb1yVj8+AYhe00wd6t3DQsc4hBUdDKdm/niS/ceenCaTjDArKlEjvRJS65dvUZohl+UszSGMWmdOhW/rKn4dV8bSoGnXP" +
                    "064N0KW4WQKT9kHDqrRSRffOfrxJ20DCKuimT1Vuh4UgZN3kdO/D97l71t1p48/b0wmKXccQwdOlxyi+7wOx46XZzBQvvPrspJiRYjHvYDopYc3Uuxoj75Kd" +
                    "VyNs1HrUtUVe1URyUI8dTRgrJSs4dEY6pW+OH4ExdKzCHarcYpvjR8bA2Z+36c9Hpo5G2oiESXeAJ90u9WbTU5HRhDt/zmaulwV0Q64maaHQjOGWq0+tumd9" +
                    "hEDuhZZRqohlorhtxRllW0Wossm5rtLGpkQVXeXq3DFFMWuWlA+UjQJ1+lTmZvZ6NVaq6zUR0QrWJXHLk3qB8szLHPFdebb3yIsMi6l2aZTy7F4UAVOsb4nW" +
                    "20G8TDHZy7IIlC+edTCzeRZvynGeCKwS1LlH3mYnZWuXdjWCVHmGrRLiVe15gk6KAJ0+Vbn9ap79gzTPBOn08VypLs6q2QWT3xZHgGcPRrD+zLPT9j4VvbMx" +
                    "7/pGdbbb9/1OmCDC9bnaSyVwWsC8ThOn2wxON8/aomHiwO4NcnOKvKtGTNvW+PlFFuFouFSwTiV9mKdDAlfgXSxAXXZM3h1V5Muwgp4lX4YTthJcO2C49Bww" +
                    "M0EcWijt9wwRh2XGIUqjlZZD8PK2qXUKSgbhplF7TbNllkW4U65WuBIrpErkWZJImsZ96493XLllpW8LqvVGEvXC6naePBJgU2lfnUqAFN3bNri/SdHzvsaN" +
                    "+mV3AjeGXMFTF5UTeHFM9kO575vxlZBM193RSau2WaDRRErRhi3+O8z1nFF1zpa1P9OukxW/NK66y6Z3YqqNJsPnMKms7WsmhDcO4bysMVA6h2kbUqyMeGrf" +
                    "3RQwjJuK8G4nkdatIGkbI9Y1+dOtILM5Hk4C5moEzK+ljadUTWue0iFTD8Kqx5uCPLwaQZqKJ91FOS75Wpa6NcXazPuFtXLEts1NA2jsDHT6VMxqjD0sO0Go" +
                    "NX7ey7rGcGZa0+tFMIjUsQRfVK13mHNBzg5jL8WTDI0rknWpzybfFjl1Z6VbfHjVJo+4+cfbzI3XwLQ5hqRL8qR4dm949W4WOFbl42dJ7uiGomZpANTZxffw" +
                    "dY71bn9jW0jWRQpRXnZLn4rW0bSZe6NFAYN78wfd0+2OiLaqn2hQYdh70wMpYV3s5mbmZqi3RDqowzq/df1PUHomzcIgfdajLdC0fD2oxoyfvLq0zyBxY6jz" +
                    "pw3yAtZXGm1lHQRrMVtgWL0e7xc3mkXMU/+khE9dL+IddOvc6bfv2A5Ct26lmcY6c3Q4bnqa0mWuxtPi1Lc9MCvUz1hWXZgFmTSpsgPmtJxV7xjoWxLg9DqQ" +
                    "MJ26IJxd+uNUjY1mw7WcUefoA2o28hDe2PO0ChujbIRgqiyswsYBOubbMT1vnNHan02vPl27XefG252CWQdSRNXoVdcsHbf4abKUefAqfhpHS3lxYzuxrJ0l" +
                    "U1P4rGWNdS4C6QB6YtA/JWCgPV4gMlavBRHZ5m7lva0/b8Rx5F92mrVyw3RcMM567VCo3aRutvNa+AYCj+5syh7X/ayRGqU/fln7Mym3cijFpWvWjZA1yKLt" +
                    "vtXizeyIaHa7elddM3LqPGQoWOePu2bWqT9b4Ndez53dbtLHvtmeo8v+Pq7R7jeBTMuuT9b3QSkenm4JuNvDG3b96v24Wd3kytvBCWrtS90Yl77uXW46cI9k" +
                    "TaDNMXw9RffIpQfi0vsyVppuVoDUt5M1rXvZYa8yeXSvdNolEIpqS4ZEvxO9BFErmzEUL4QMr+V060ApK40Z7iZGXYVV9cyo7ntApOSuCnDuurjJq/JIKfCq" +
                    "gnP+uHlyXMYKtt2sTvmgeqEVWusWrwH4s2Vez3hVtbrNmx7pk5HSvVO6Ho6ilzRmS79s6378W7NHp5RN707m4K1ot7YaeVdb2+YsE5FuVZ2YAGu8BOqcvhm9" +
                    "naZVUarMKRuRl9ONr5RbeTlTatd45STH4fjzknU9XljuozeNNxx39uG+gdNMKTfLohIsZy7Bw7rtZ13V6EyzrKu2V1d8W9FyxrbI1OdYgo4InQSsOiJAW1yA" +
                    "zh8wL/F5PRZsAL8XLBuO7bwl4NAaPQogKIzfQf32uQn2xIx9evjVJq/43rh4fb3Ja+FatHnPjb39OEg6lE2Cb27tj4jfzU/lQ4m36sXj2UflNoCbPpk3axmP" +
                    "SfkTSVsye37Umkd3s6zvsJwRQ3WYWyFUm8w/gh1ECaukrak+ikiO50rO4dCzMm+QMQZG3TvVHW1UjklYn3JlzPcBdvDojHb5mG/Z1wTk80bG9t5GiPDyMByk" +
                    "0puXJVyNY7juobxWpisNRbifFF83MwYrfnCnrU4r31LsOVdVb+LUwwjtfLhZWisxu+2l7YxLZ/XikLUJxsV96MyXYfHjFhCzdzVJ+BYybVh2ux0mNOfXhZNl" +
                    "z/2bBPbdMXbltJ9vHzuZYCsetZDSJcy1q14H7kWsyuWYO/Go7MwN1syuFcWutdWy7dlHijlV6yAKbF0Ft5AQ7MRgf5dpGZGbBG3VtMDTOkaY514no3Eiy8a3" +
                    "uLd7qkV2rqa61uFz9OTIp+qWuD0loyceVeTAPaVtm84l3AXwsC5QlxVAUdZqNztUyOsAxOADAV+OwlWtcynk8JXWRW3ZRhVjTAA6u3P/B1ThsomTq/B1Nhzw" +
                    "cSRjpGrcdZExvhK7mOmTvVtGrfFyv6uPY3jzPEVqbZybZfX+LJxFraxGEaI+Cyfs4yeLjLGo8wuTOvSCe+LU4jST4ipeM/xRTL8A2RZkXxTE8EGvCdYlfHd1" +
                    "mxoqzHb8JL192NXNVu2lHU4d0l26ujVQbhXq6yDyvgKEhsTVIaVhy9WjljeFu21efp83Oi196WrFcuFF6m5VN9lF6stxlVPSRpPDo3MiOJePubZfi9T/sRap" +
                    "C+Il/QFHIGUeHeaT7dbGiVm7my3k/vVggjqL2J9yuWCEEO0t1i3csG52acDJ2v3aMlvz9nRLuukT7vq28MffnHDXlsB+u+JJKJhRmQvS5dMDcQwFkw4Ho6RZ" +
                    "qR1DOh9wh63w5u/PptWkm0wmWDGisvV0fzTt7dEoQYsq2MbD7aRZkHCuMrkfL6xbmD1SqNTLYVtjm0cMBzUeHGRz02TX/b2H97ZlxqvcWL8KkwkCdSnfFzx5" +
                    "bwaQtpPO1EZ1fQqSb68GkEzN5p9MokwJDO2I2Gq2V+ejVL3UlTfAZHvyVXiEWaDvB+PonsrzuN63DX5NrV0u09LTfpZlV+Mo7CPUcbWVKjZI3hnr8qmk9Xa4" +
                    "8I819PiOoSvK9ZehT6hLDV89QIHIGNrfZ06qPYhZuUxXJ6OQ1O10DFKEXxUzrV0mBfLxwFrqpd0p11k5NfO5pNYBpmaMnQQ11FFJZkGEn0t5TeM0yK4fqdZ+" +
                    "/FnM2swJd2y9QZgaxNxpzOHLXu1nJUUEOw2Duptn3AnY5dcOv3+SMSRBuny80O2ggaEWWrmRNP6u5azvHZxhN7vZaaTysnmq3Hs8nUEeb5qnzg6pxJNq/UYS" +
                    "o3vd3aTffdvolrYhpKp2RRVDt+ujLOeRhog5Q13/LsUaq91uVG6me9p/zxl3ZglQO/VQ/YtSTQ7iOA2ece62HV76bhM1fd5EPYli5vxwOZdqdRVBCc61fLeb" +
                    "ZjI4wDvcr1lftVDDxJ80zY8m/uoUrPri+Ku0bICBNN2vxn1YBON5T2d4OppiyNd0PA+pLVycPg9J9c273bpdlzm0ZfcP5+xAyIzqXJCu5dOemt3oZhcwa93b" +
                    "aRzNxPIn/RUm2YdBl/t+eZoHIr2jWC+Hc3GKTvyzP/RY0gziftc/eb26MeANsEyfzZ2anVTHUYRr128hor1xkeVdnDFca1nRiuf6PjfHkJi7A/GyQFw/TdZv" +
                    "m+XJvz6H2hydEd7er64WPeWbU6lpGOmd/epRH37VRPiuhOp0/Li355052d/nZxNNdkRsu73CfkZKOaxJL8q9L1MK5fkuGM27r6tgAHOGun3Mte+rYPiA2vhC" +
                    "+rQePp6MU+9Pjs6YLbX0VPTGXZbXJ2fcbVgnmaNexesPZ1H7N9a5+dNR1KcN6zfThkbKbM8nfmenviDdPj7j7lfn/B+lcy5Yt+S+qI0Jh72NzbbUTkJHaq29" +
                    "FDoel6urMeJsiNZyq4Tq54Pt1MhZEsS3lvk8QsFrfM0UUjyaeNLr+5opsqx8vUi2nez65a6aKXOHLycFdbr1RjNJ2DIXkFv/7sZGWqx+3AbSbxe6Ld78rjLT" +
                    "4yn9JoTbczOez5C/VLZ2qc0OoD+tzcIbXr29f1KKs2NI6/6+w+nj24FIgnR37pc05p9EGiNY9083sx6kMYFHVQB6dy+N4cEF0jabwrw/2fCFdq5k7M4Mkgeh" +
                    "WMfD0x6/k4y9JzH2trKuShoTtwOp4346jlu3+Pn78dJ+2PdwYGDy3ixvanNr3Ti3ReMqQPcPkrd4d0BxWAjWerNy2R0PNsQOSMvPRxby+Yg7PY/0yqXLmRm7" +
                    "fadnCyK208ejOfFsWae/jZb2G7qtLZy6HJESbG3WjodfHWcWoHkmEPfU/l6rverOvbAmajn86p3lrH05JUVpYewE2r2IXQfxcHN0wtzFe8rc5ikpkrhFJWuO" +
                    "c0OEf376eNvGkRop39ZN201lejdaGCdBXGlhBOhevr+lM3JpZoiYsgC9blWf4+OvNv+8Hj07HGN5XP2jjrZr63Z1YmCoLpuyiGX3z+bR/bIxIJ33LncztjBH" +
                    "0JpiWL098CzuJ1k2uVva3jmjMWMm1QXq3tJX18CoqYUbHqadN24H/3Iz65vy1nKSRZSbBcxL2F61jnrj9qFx5pYj7qpdxftMB6N3RNgg3s6petl1ME3S8y1V" +
                    "16IIEMII0D2EOM67i6XlVGtuA52/dXXA+4Va28aLX04eLqWZPctQlWazfXK/OuDV0gB1zA63VNKzOO6fEm7hjnArKgULS6M0EKbnQyz38xP6krSVuXw5/vb/" +
                    "QX2jWWajAAA="; break;
                case "modern35-2": encoded=
                    "H4sIAAAAAAACA+1dyW4kSXI9d38Ff4CAr+Ye58F8hK4SIEGAJAit/wcU7ra4mS+ZWRPshg6a6ilWscgoFl/a/uzZv/77f/znv/3xX/73v3+5r+Ta/778l3dX" +
                    "hi//+2///RW/6v0n3n3194f65a/wFdqfjl///tv//NOXT1/tY/z93tjfhHJ/jP/K6X5zfV33m5LvB93vrVf/k+DC/Vn3x5f7/3A/Pd/vL/fHJXf/Wfsq8lfw" +
                    "+eu6PzSFr3o/Muf7zf3X/e2f8UtqH1m+km+Ph/tp8X6T+0PB9Wfen9Ae2f6W+/MTPilAf1JoX/jf/sAn5f513z9nfMT9dULFByZ8YOpPgti/JsAn3V9xe1K+" +
                    "fv/tX+5vwf3dom/U/aO9/b7/y/3n++P72/vn1N8X22/bR7Xfx/vz739TuX/k/jP+gPv/8f7//dfeP+6vv/8V+P7cH+hd+8w/+p+n+yPws9vb+7vVP6P2z7y/" +
                    "T/cP6O9P9zO+U70hJ8Ar4+7+Mcxj7mDfbxrY8f77GpgVMU8D8/ub6a9MmF+I+f39bMi0J3es7zf3L6/Sv73tzf3tvYqC/P4r8L/7L2mQ3y+kBtT9zHwJ5A1r" +
                    "1x94v/Q65PjiCRUhb/++0p91/3x/mcUj1BGhDv1B95v2tdyvhvapkDvG7dvYYfQK6Aah/8L3EcKlIxz6xxuEoaPXMEG0a0c0C9qIE//6RjgiwvzR+Gf4nEjv" +
                    "a78P9+98f63U/qvvHG6EGdrwEGKf0KxTR9qX9u29H4rWfX9zEP6OeyDjBgL8QsBrIOMGBDojwgkRTtqoQzfq2J7antZMET3GDRbbdjPq0t3E/ZdsEL7aSyV3" +
                    "m+4vy4ZnkRcMfm3tTenm3p5jcUY4v9mQxXhz/5n+NLIxZ4Taz1APM8afUzc/NtFmzNB/oDG7ilBXMWYQlxDvfxFC3YBDqGOHOhLUgjE8xNoh1g6xvnFsIN9/" +
                    "1kAOaNwpd+NujpIdecc6kXEXcuDQLagi1hW/wTVrrGPHuht0Q7a0X1xJEMIIkRGo+8O6SZP7jtp9Xx3qq4Nc+hMuseXS4e8YB/TeDr03Pqj96wnx4b0JfbRs" +
                    "ry0byKX3j2jYD98NxndXsc/UoWcPjL+2lg3tn4F4X93do9vHT/f9E+4v9P4zxNs1vAlnhP0fwjsi3hHxjog3enHvEe+EeLMXZ8BLhyc4R8btR8RuSEdEOq6h" +
                    "OnSMfX9c87pHq0akQt5gDR3r/rq8PXY19py6w+6QJ4Q8CuQNa7buRFHZdysM9LsbT/xGth9k2PIK6L9BsNFqL3HkIGjhSwDdeO4/CGzPYONnosO/+mcF+mzo" +
                    "v9aBujmclpsxxuFpqA6uYx0cOvCInhvEc3eQy2TVa8hmD96MGwNsxUyoXhry9uz28/13kXH3GAEEf8R4XTpc2e3ceH8OdIcE3UlAf06L+PQCag+OiDS6h/a7" +
                    "UJfULJBpB47TbNxOjDtyYhbFuJMO2/yDPTIQatCR8mTcuVmo+HJOxgp5/SQpHeONvhyf2L6C3PAmnOPTuB0C4h0wHU8Ie8VEDdG/A3Lz29Uh+g3tiKaDph0p" +
                    "buO39Q7jHe6AcAeE+9KuXJu2AMRIq8xsZ9r9FZMLmXZ/kpMcj/z4/bn3R3SM8YvJ+MXcUXBEbideXBy3+HHPYTsx+ElZdqGkGS07U/qdCMHUEUv0/o70Ndx4" +
                    "IosuhDC/RjIhzZYdOtLpRpoRTk8t2wNmaIAQe2PZCXNxTs0yGXYR7405eNGGTWVXe9MyNL+L2s2kCaYw+3Kqt1SGhtl8KNq0uz8CR2VXM20Q2MGLE8dKEDC8" +
                    "AIYXiApvv+IdyOpHzUXv9BpwjtsgaRlInOYsPNq47XTc5jQNa60guXztgcUbwFuaxkDnp6a9D9to0yHvw3Yk4IE8OYdtKr6w4KmYFlXQiHuTk19YJFPJ1Ew7" +
                    "CNLdgyPgZOF5V2cT1LbABi/ZODlzsnB86eRCNdhIy6dMzU2Jedom5kXqZKD/px6ohzMPZLho4hy8q6neGPGLngbt+6QQTxS8GWlIP1aEJey09OoroKVHtHSH" +
                    "iXkl4GEN5QQ8WjyIqb+0carCisHLmXIbw0PAqBuu1zaedY4fJYgT4Nvo7VVqvo3eif/zArjy6SM1r1PRjTWUTc1H9AZ6iYD6zCrOATs+XMYnyswZaKg/G72p" +
                    "v4KVWEuuerye623l1HW2RkiHs1Pnsvv+zFZOkVNXeTW2RTIW8PcLrj0pYl4d3SmKJy7GKCuAYdsYZyBunXpElL2kbgR5wPeOXlqilK2/DuJSj2WxWUy8oqTr" +
                    "XsJz6g/0YfTSOMmrEhG4CweUQfITOZAz2CX8WCDvrZYLrZysr3A8t021OnospqkG4tdbzoZOtOpuWupFfsqUnGvQbR81cR+1Y+/WJH1K3ZqVJ2m2qEgeTP8U" +
                    "s/1cCXTEOlABhihTo5TabiNJ94K48utzDY5+HFTqlgn9buZl11sb3VMQM0e/nqgY/07XjTgjXR43XApG8oKRHBH3iPjtUCtl4uzQOYJzTaaRxvh7YS11IUyX" +
                    "U4DHizrmFV33rmPeIzkaZyjH1G2ycgs4hQnqojrPiTt4DXiQhrmuyjIDrlN1eRX02I6AV/LZRcIx1tL8EtDN1DwF8tq7Kbn/qlL1VSQFQMBHdMB2OQNd3dOG" +
                    "uceGOdpVxAiOmPhczKzk8lPqlsjEnYngtRxN/E7QW8McGyWJu91UnFH8vZ8UqzTXGuDYlw1pdFOLp355xU9triJJlw6Rbg9i09401765AA8D6tDbmVKAD2/u" +
                    "NdQgUINYa6A2me6b5+HNVc6WJVcvC9SRSneEujVcivTN73/r45wNGy7eoTeP6M2dydmCVGcG8Tp6bLo6S8cITrbdH56dqcOD5A2UqzUvTtOwTcomLbb7eWTh" +
                    "/UXKNbkXx1GrFGf4koGg5iRjRMI52vDruiqLkrJtu6lgGi6lv/VUleXeSNfdVE7ZimT6Qdpzl0RvLsm/b7z+LjjX+hf4crRwKMfc/DNXbuYkFpg0p+bNo9dz" +
                    "wsZwKyeeTa6PPgKcaZ+bogxrn/GDO6ij5+KVgVPTNU0GPmYeYAaYmGix2aKBu8wGjsAm6qhy8Gb//s3OHNGPFL0Z6etxiw0L8ECmnbhM6qbtEeprdFa3OTr1" +
                    "2+6Xyztn3p1w9tRGvfBvU83UKtG7OfO6m3dfMvyEPpdvrRYVr5c5d6iLM6cMfIxLJDkfWVli+ybfPwpwUBgXSrtSxx/IJfueqKl67BrGPcJAJSOuMiD3qpLD" +
                    "1DzeUDPE1+NEDVNzB8a4AY27onEDQZ3Iuv0hbmczBn1Vj6F525ZLG4qhM29OvyEeJV9bEjU7LyE3rhAn447SamvlWNDe3JmeS2Tjdmp4gpOxqIclkpdzJs3D" +
                    "kouya66nEvlyQNDUIJTnZzwYaxDX3mZt1tws25Flj/JbYHZP87SQdeFNs0/EQsK1kFnSsf5GwK+3/rzQFLR6bJNJas7MhsgTMm6jH5pscJF1Ny9xcQF+7LWU" +
                    "jTsX/orumgeZdAfl0bNQHTYhfJRfQCF8dF286o/rxmo1k3Pdr8HAHvrH8wsmUQgfcLv0cCI6JW0B7fxCz+650WbhL+LKTbYWj6481pGtpUsYR6qJjnBnTKsT" +
                    "OowYNtma5jlInqYqMcrTwrmlOproTnFadCUWFNeBujNJVWKj0ZIlucqKuKSb6HEqvUFhnXq5zeV4/RrBWzdbBsQh/ViPrc9Nkmm3VDM+8YeEDSvc/nJRnTbs" +
                    "Z11mBt6tNl3UbbmSbqlu2WrEYFrGJnoiWug/HpsYO58c/HDpemzidUHmf2lsYhN0TWRaeqqXbbYMqpouyHhswj3VwW4RxOtTvgMi7rCr6tKO7xCnKB4n674Q" +
                    "6nImMTEzcdRiZN0TWy0KyKfwfWys2fZN5vFJx5qKMWf6LIEC+cjNqfaOKmPT3dWZtFbE0oNMv8tk3ZbJVGlEVuRtpKy+qP458qI4gA+IH8+/PQ7JPObongJ5" +
                    "nAlrrhCuNB3tFjhlamM41hz6+xmZNkdqoOPUhWhMXH5veGsvWqkbosPUQw3SUnOq7h6zMc1ADXMPlTM0UNQz7n0XIqqNrrntoTL9AahCLzINLdJDLWo21lLz" +
                    "gXB6mqx5NGuPZu3Tfjpmx2LxPBbjeuzEbdHjscTZuUnSPmyj6uw86OycnLeXmpsKsU1zbbhxnoZ2Zz0NxEHGY0EnapqPWhS5ZZRjkZCs3Gtx2rwvNTuvhtzi" +
                    "thPwAXV+WpAFZJuHHld9dKafGkw/Na6QM/e801uC5GtUgS+jkjV4gwxM1v55OTr0npW1nxc6k5ujNhXiRGPTE3AENhB/TQVvtw/ezgZvMMEbJHiDaZzvHDoY" +
                    "tkSSZssYiA4Wsli5AA3uebrWS7JgPHkylVmcGqllMJEpX0Mrxw5bxYZqxYhZTRDvdVS6aCSqSnAQW7eJ2tptKboeo24LZQEV59yp/1s6umlJxinxCmrcTWWX" +
                    "CdhTf2Xw1arpr8zxGngu4gZbLSk4sxqYYqk+52Z+MFEbfxP+FH5iYXgtPzEe+Inh0wYqKGaioR1vGQ7hnI73LAN6BxBgSseNKXMddiAoaoqDUwbtZ4MOczYO" +
                    "huUAJhsHxUeFDYmJs3E7CasHElNHXICuT2vtiKlZjIbcgAYNyGYpM9VhbrHwakESurnimV9iyLnPrsAZauIgNSimmu2ptCdAJXddu+2qlgqxjV8Ov6KQilV2" +
                    "ndmgva2ul1oLBF0wVjlSLe6XVu6NX7a6BlVdB/rMTO46iLvmfukA9fE4JHi0Z8/8U008pU4KTPl3sCNuZidG9LLeYSZ0oflcJinr/brULTADU43J265zEb+j" +
                    "LG0WRzbzkGw2RxBtABWbvfz8behpzjJXxuKIZq6MJtnYJAAhmMf+cko09JjH2qXnY5X6pWt73AncTC4fKD+fhSSNc8hmvukmnOOos7hR2q04j+RbzTt3/vsY" +
                    "nvOBoUZl18xdkVUw6ZdOfZQgwdp5idnGxgeVwZnx11gWEu5KwAba92HAXRYbZ1Li6J76pZ+ik/CiOGpAr0L24AK6YP1gKoIePFwIOo5DIs22r7l1ZgZgIFhL" +
                    "t1S30ND/XmHXHaf9keF6s5Oyurlw7KRQdU1oR688OVCYrp6JiNlE6eHJQ+W9EWqkKBrDRFHhattNYTraNYIRkpl9GhSnFF1B1W3SaRJSzEpYlZeMo0kISN6N" +
                    "IMuO0OO5NlZaDvd6PVZaviLWbuEoHfqkZtUTS9krbLDedFJoc+/Sbph7aIQ8bFhpdq3X8suVgSfpmi2zrzCRT72ttbzZJ1Abn3lfa2UVgmFfa10Wc1h2eots" +
                    "fDrDZIh9VYigDg+WPuP75lkyvt2PpZHdFIR6Z7sNbr+shTHGRZpmdqv3xCzXJIY4ViD0XtjEQNxk4cOLx3kH0BmW0ilRG0n4YI6ONDwSAzHzyGsipIE0Splo" +
                    "DIKvJ446Ru7WSRGIw+Olz4l1WnniaffDOI7PywRUfokPV+sju7idVJqWdZp2sm7iMixL+2uj1GwlTFDXDecUt3KcZOeatsIcNKm0w8xAtNMrrotBqMK8PLIC" +
                    "XoTvgqnacAtVMnNDWkHjlqXPx60znxHwjMsEl+mTepuxlWO7VBXarxYBmWWst3yJZk6wG7yZarzU2b2aoFXuvrbGiX00i4AfrRIEO/YK+85ZWNfDNMN47ni+" +
                    "H3vNnbP92CuxhTPOAZ6u+SIIHnetPVofqzTUOJb/5hmIJiipEeeBtZJ7M4tWhMbWviqdPCZtXqbaLVuzpLRryslLfz0yiXF0UQjrjRDHW4KSXRjxdkNoZZsy" +
                    "8YwJCMgkT8JYsSoN4+ckAy+egnjZHQjCNhVwww/uCHU/Dq+FGuZ6jK17P/daBl7sx7G3gkW36owEIRZ3ItqJWDx5caq+KOejTFyxDrNe5A5DpsGP0WYarjww" +
                    "43C0zfKyCVZFhkGPJzMFh0zbYUHtiIzcLMuvx64B0BcDsuJL1szwhgerQWG/GpTcKMHWDSHeEpnyb9oIWvJvpp7lPsiyW54qoXLSNDsFaEs9I4dtNoE0M+VE" +
                    "PRtmzKYM6LCXzW2y7mxMeaYDR8VKylIdC3E87QaaaPxB8VscufrRTEkdYk6/y+O6WnpnxEyxvXBDSRm52FxHv2mB9xcOpWLotKNpoXhuj/aYfJ0XeTk0m20Q" +
                    "WhrECotJ4lRVN6Q3xfXUApd9vn0LPG9a4GBoJUwzCabOimaKWUjMoagmaRUKkjNdM7Flxjc83hGImHRHVFqJ9D0jL329llEqQzqr23a0QhyboRZRiAP3wlew" +
                    "i4nQSdjjE00B0zgopMCh2Exx1xLfjLgcJWHRFlphWtLnuVfeDDDrNMDkzZ9IHXHY8M2ycFnsCi+C3YIHt2a4kSIYh+cLAtu17fDR2vY8s46GLr6L0NGs95H/" +
                    "VTnZoBI38/5gbXvpowTukHJtfWIpDA6xnoMAV1Xe6uvQzqdmKcyOfAw1s9rnzOyOjX1n2fUuZN+gII+yMOBH5s1Ih+tpHyWgfQfYr3Ka5R830Unj1CiFMz9B" +
                    "04dHC0V572pEGYh4Vk6jrmJHXdROqUZuZUs/YojDMvyQFd2pgzKHaxDO+FBjsAV1onH0KKhBscyLoFxkgSSIRAvXV11ohcGNz0nDF2beF4+uN0TSWXjD2Ym1" +
                    "f0sxiz1cZw7Ux55JlG5ZS8s2eocmXgMbNIGMzXC9tuk2ayDot4fUitsJZuWVQbpZ8bITj3WlL2+KrEovCqupk7cM0ubDBeH4tBl+9YLl6ite17Vz4Ce9jTBG" +
                    "XKpxUs/6lv01RdNrbIdX2uPU4hvxrepGjwOom0gks6QF1niDkxplaROxnYnY3rbBrazOfucnK5IZG/NomLI6DggnRe/8gFAQKyFsGydBFjjZsAXk+Fj7MGKp" +
                    "FYNWucSYuvRP0mEZZOKS+rMgHrfKaC61WniVtZ/juu6L5qjqitvVvrCprv0ktTK0LhUPKQxiodtxxIeCRhbi6NA3S1ZaJ8+LP1lYKYH2+ZhY6IwEQwvZAnV8" +
                    "roGIztxd5w2QdK3ih7sNr/DWyFV3lNoo2axXR0MshLMEA4sgGhqSaocvjMKlPSpLP8GU25Fpo5aq4mTYRYhnJZowc/sxL/cy8LTqWXrYVU0RxkIOgTQvi6Iu" +
                    "CNAx/AB9mKBO2B3v37vAUJuNXd7pi6MIY/7RR/pZuSv4kEPPipRkJYeHoO2ReNanZ9A7BCSFmIWuYjSVmK6yKK1ERTR0mjwsy/qHujus882qSjHO5AaBTBx7" +
                    "0WTDQt02nm8WpVmMpRi+P9NygCAdn0um2cHmVkjper3zwySVzyXTlpaa2zOUPppop3WHiPd3k+TqobLsBvhNo8XvE7dPFdPmxA0+moHAhqpyTNwY6PhYM23Q" +
                    "S9XKNvPF01SC1VUIUQkq1fJCD9FCrjfzy8Q65LaaXdpe8jeO5SR2ql4w2aiTkzwibHURPTlsR3xEDN1Yqo1gjgylMfkKI4EranF7CCNmVZll+jPNGQdZ0B+b" +
                    "IV5Gn4NiKprXfdQpYMfHnPGXikqjMjOccZh65lYa8YWKlqWujCHn2jpXeyGw2QuxS51q098Z3umgIDbXTmtfnLI7029xg3aqxJNGky1YzZUyEcmB+iZJui9+" +
                    "Xvt6aecgnBcO5rZ1LkDHx1OwV2ylocLxelFfiWI2yGlRn5hp+fW0JBgiSzQ1eRB+2kRM2/TYsuGeRuPgsyadeqIx8EII8w2nORjoqfYrGgNIDEem0eitzVLl" +
                    "ReSMC20DjVhwSaKeaQOMYjgDHB+rpS29c900h8mop/ZLmRdCYMdWKut0O9LxATJK6qcSd2FdCNmNS5hOrqad3lDRwk6wfFJcscKn3iiuOKuCSVzkmVReZakT" +
                    "1Ix79FMzFW7erAyA6GaCIpWPgnxY95h9CtTx8WDMauywdc8aOwi5m1J1NQNVjvw9B3HRq/y403ZunXvTgonnGah01wJRv7+9XesM9hYBfXx+mbINYrhVSrOD" +
                    "7qJa7KxXrm8RfNOXU1Sa3p05oxwfq6UFuiVT+ZaM5hcXa+lh1aefBRGPVKVrNNGLarCqHd6172J7bWVeFClq5AJFWEn7kzFcXSsZ+knkciGqjP7KCNbzZkjq" +
                    "KVpe2uWWNZ6Msmml0k+NU9BxM5jxsZZOwKQsAEuSa1Sz1auemIbVrZsCbwhovSlvOCpDNum48OUX2SSy36K11Ya+pR6OxK2+pd7aDHZr872MbVGNlSJtU1BD" +
                    "T17yOitVzxNuu7U5drO7x2aM02MhnV/Rqk5TpU0Tbe8/1bHVygt+d8nJfaiysVe4XBWrxzr+htMwFC5lGzupxS98AcArPvElrtuOufVmdqEYPbfTitBLcczN" +
                    "0vZe9gWKYhsK0Mn/lIpt3vGJxz7ndkryEvEzXYnl6NX8cyES/8IufrS+WwupGOrhSmLx5tCE/2SPM9vxp93j3G3urnHaJuLznQm9xzk65oJzetwx38vgLc20" +
                    "WcSWtwW6Py/rpl+DHJ3oVe0y/lj9GroqpqeitQ9B734Z7UO9m638RBG1TI7bmDEC9vEBvyao04rIVG+FaQg+5+JxyCeVzXmRrFKysSekVTeqmpUXybkHnQV3" +
                    "RGZBPEE7pac8xP3eQDZ7A3FPUvsgbIf1zoSbh2F0fYA6Z58k4WkahtUTc/yorGJOyIST5oI/ay5ojMuy4Jc3pm27KTZ6BxW9xbQZ2vT80oSVPUSMaULCsofV" +
                    "bgxc032oYGdi8Xh2QGvY4tiFeIQsjuHNTKScET+K6NTDsSilYZskeH9bTVOzt20TNuqj2jsTIIJJZXLJo2O6Qxy2BIfRhcNtoGz2eAXo9LxjevEVz+TGZCTz" +
                    "csj+TFSa5NA+Dt/95bURYyjz+FtdHwgbT/7+TNTm+sC06xfp2kCwuirBDkON5GHedEyLbHLNtMQhXKj9OCh013JMZ+hF6aoI0AnSj5GNN5MRq0yepsOOftj2" +
                    "Gzbiui+Q52tRRi3rOOjmPO04DrGKh/7IVAuSqunFr7DTRJsOwFXVMQXZENFbnVx2z5dasxI6riR9BipRC9JNU4kaA5zgxxWqkxNh8rWJZnioYSEfv0N8Ovqn" +
                    "gIpCObVkRCIyHbc7jTtXtZ1s6neo85bNEhW7fFru/PY2Tcucr7+ae1a1gz3OyKQhsmK2RYpUY7z7FdXcc0y6ee4pUKfnKvTCTdRXmev2pkg9LPuF7QrYTuWy" +
                    "5wnYSRMzv8Sup5yNhA93Z/9e3QYz/lyfe8yryKWz18GmAdg7VZ16OMcMu66p22tcponbaBsu4zQYG/ljlvlLBduwKNjO/pzZ5bxK8FqUHJVs46VytiC2qRou" +
                    "8GIR7CRkG14scvtjj41W/wxjiaquYGmpW/Es3WFjomKSbeyFtKZucGep5DiGgzntGc3CEILOVn798L4+mEp8us48CIpo1URdIpHq9Nlx5qilqsGIFvu3Jybe" +
                    "KTMkg3Xd3OJWa56Bt0iQjagPc5upSNirZg07T6a95lV7LU5Qg9kNG9vbWshjXGhunFRBOD8/ElU5QdeUBpgd+uGM66yv86YKxx2xYqUudwvbPN8mMssHxx6z" +
                    "DMpte+20rm8dun+rWvxGJ+0V5zxtl0isR6+mv+YW1WIBOj8/EvVqb+hwpVmxkGcBlo+Gn5y3VVwKW0jIyS6AftZCt8zEaFZ8w0Fzh0faHq14Q2O5MS7qnaan" +
                    "WtXiPYhGQxaZjSD88bSslCTVTEtKDBfJDUhQTDw26YdFBOr8g2nbpgyfVLVgpa9wFFe6pi9YyLb+VuoMdSdRfh0JipM2uW2rKqoaGOKSm/Wqg+Uhq/39ZT0w" +
                    "7DL1Mm2ExkVzp2jmUhlmPp//9ZOZ28StjUUF6Vyf9lP3u6B1GLS585enxrkzsuQIzgW75U+9zX0afipBxHoSTVOXHXc3ZKZxSagT+5ja44OpNt1ydYbY4Nem" +
                    "alXqiHVDRQ2UrpXNHfbhzy/FiLCCWqkTmjL7c8YXXP1pwR0627zpn7+4y66v+LLwzs6f6zOPonS6xm5vRKtf1WRnBZYpkjfjXtjHfsc+nvZAD7YNKlwX6YfO" +
                    "kPPEIyximHWznFBfFGWCNPi/ro8uk7K61GaYu1HSRtdF0KRqPE/BFZ3F7yQcMPCGuDnJvm28UE1Gi/1urI7l7bI3p+hhd2piQtzvJydVrXvrw+q7NaLBPK5i" +
                    "5CB8U12GJ/n96KML0PBcVWtXhvP1P9ktOarmmQufl6nQsAy/ylvVDtVBX051q6StftJr48ZLMowXK4I6BMqdOkTw7U17VSAvODlhBlu2+js7G6+io7Ybg1/q" +
                    "CNx6sLmKW490CZARJ6Dh8cGJ6ZwM7Dhr6ezPjZCal2hONflSjPfsOhUlTA/TRsiqkpg3Xv2VfvX2jsxy71HragWpxLLK2Da6WvEwHa2q1TbralVWXPJW4lgn" +
                    "6fNOiZNXQuJrzQw0xMeFGQqpOWS6uIJencZXcNwHNrZdfnWLSJJ0apCp/AvLs/P5v7UUL3wU1pLMrW1vdDGjQpwFWjKzzr/9NknPk8zxIDkUJYxZqIUe5MSn" +
                    "FUssVJeNJD3IcVC96s9uHTrkbOTPT43gsMx7c9TVTxJ6trs61+A0CXciWX5aCJ4OCSl/njXhhVVw42eHhNIUyddzgCOgz0vg3rIgsgxMx3TU7JNkvQJeiNM2" +
                    "yKr2jJCemenMLUkjrkpVNqvf4guBC3HBGdJfsU/CMuZxYp6nMSzrqVvVmkwvWC/zHSE7O8NAziM0JeTxwsr1Sbg4j8RPpx+Duue67P3Hkb0DkZQ3ydvQ2wEZ" +
                    "iSfplQVzxHkWMx9nIvNmJO5ED4BLccEa0tM7YVMpDma1JE/Lgi/Oyughyk6PKZIwA+mgqhWDanAeXJfjlZH10iPV3tgY4F5qUjkaj8ai7AzI4Siv2MiLEmZR" +
                    "VVSRG5yXSKYFKr3z5rzjymfiVmoWYFkqIIo1cx32XPp0677dNBwz679pYix601W7Prbl7Y73Wnm/8t7ot22WZ687vmqeO7sPyL2WuO79vuCf60vqMDXPDf/c" +
                    "r4iDMNjKtqs22MgCNDy/GoTNc+aokkVj5Z0oYFdhtxxYTdI75+T81DzPiubi7fxbhe1iBmXRrAPuyjCdjweRN1YaPXu5Wz/YakxGN41UvQIahJgKO8pDJdGl" +
                    "cT8E90vMVW4hoF+G5pJF72XQHLxR8GhCqII0PF8BtQoefqfgUY8NtV+8/LfhqQazPDRSNcrRDk1VkfCw4h1jsDoRVf32lmucKOhi5t+b/f4kjj+voIPUYmC6" +
                    "595uHRStYj6C/WCq6vPc82agYA2PuU37VbFqJDPngUlctJnMvWb07btrM9Mtds1Bz/NElIhs7SLFKnVszjXXqalqGeg4kjONFg01gw2WsahU9cLMUbWnPUFt" +
                    "gNZJy8Fu/l6Uko3B+WX0rF13CNy7EZ/OEMPjLdCEBDbkGXkcXHmUSPCYCnUqi1HAzQdn7nbO/EKlFvxP7YIsauVq/9MG7orgQlmvyqA+prrHbCdgTmz49aoY" +
                    "nFumRTVVhtDGqIqDKDLZuUiDrFU45vh2lXGpU4PuIUwvcJbn2dmsYL25OuE/U2V5fwFuFaaPG4JDnBPwl4XW4czIMhY5CdNvRFnCB8mZXS/YX4CDQ3JmCal6" +
                    "veB0j1lwLvCnHm61lx3DWOg36wVMVYN3U+7NekEw5+DsPCS/vzay3FwvZq8gvtY79malxLRMx5lHlrbGXpZSZNFrYkMrE7YbJfZOGLPS8oa+tNPKbD1TQbo8" +
                    "Xv5dtDJfnBkJC7HhF23c0peI2GBn1ttCbKEv9dcpBCuZqehLVnkpHbksbj0fNSkvwZnXsO4QWfpS/Ojour3bWg19aURrAbr+SRvA/09f+j9HXxKo6wNFXOKy" +
                    "4NiTzzKTb3R89VHrml/+qLd0GoXok55enxZIehMkX++Wfo1gw2WOjdHWLzBhqWMNhomaFkU1I1Y/7Qaazf71LlwmZMZAo0rmzdpas7b1ehp0VFygVO85W0sd" +
                    "Y4K2Pp5+7YP3Z9ma32rovfTkbdy5paMeJZA35x7Fqpsm7mVt28o2KV2WuPfjQY0+0uc01GK2dvPCT4svbsKN3mkxN+FmGurw4wJzfb5YYIjHfFhmTzw+7fW7" +
                    "T6edO7xJLFNpd+TdePsF7/garDV3kExU423/InCfecf+002Sd7zjefk3vWydcjkmONfHJ11xuaO96U4cWyrodj1QWl6HvpYRrU9DdcdhKf4mPc+OWiq6diKm" +
                    "2mWuUsSdZCIYGR41DEkSptWRgkv3xp3Ip7nDlcc9wNEOswvJ11ohnnGdAJZ9wDIxVsZdMB5tMVf10gAzrpf7cXklwznMh2lmnWjG2d4WKke1PDkQpsZdaddR" +
                    "sRpq6/mR9Wivcty1znu+ZrM7vFoj2ExC/GlxpCgvXMxG/uAXa83bS8kuVXPTs77w3wLy5R7fmtmeW7cjkPxaNk/EzKvO0bbTr2oONG9PM09mvI42r0OO5k0v" +
                    "bTjtpbPCp5mVpDVpIwaR2IpvzxSA0sgLSt8yLB3ScUCqyB5YnT5z3PTUh5mhY50Y6/rT17htUraVZEn2rpCImcMcsxdNa90NX67LNKz9vPF74CaV/qTit4at" +
                    "TlPEc9FFstXaxGULMGh7z2L1cXdspggHwRo4LxCU5ZjURa3w9WjvOOQaRBCXCYiC9RXS82vcfdSJrRWuvWiRP5hF/njgH041WNkQD6uiHB6WRbYefWEe9hQP" +
                    "8tpTqYaHZn25WSE4roSFXeRWe4H2oidI5J41tt41Tk8XPd1WzVoQvkL9C5iHMPVS6rJBokQ6PiUyRG3kICNpZKGdSSo7ThKqdNjZyHIubu6UO6WpFkdDZV0O" +
                    "yhvAi0yhsxpvDlkWvAioxFVU8cWfCSTnYYVPeaaNOyN83FNwvjL8GI2ByMW2oSKEw1f7+5/3TXmN3xAYxgCMt7qvF2cprvnSUJ7apvYACSXnp6GIFulwH66B" +
                    "2ZNhdSIcfipUv0rc7ggrAvAVQmx6txFqTqXk2oB5XnHThtBGWK2+7pV/UnFfs1ufIf/srFQfiwDQQHurwbOcEHvVI59Uyz8FfZa6LZNK5qsV31kkN8vt3qRW" +
                    "fFGD538B84Q4R0C9AAA="; break;
                case "modern35-3": encoded=
                    "H4sIAAAAAAACA+192a5cuZLdc9+vOD9wAM7Ds9Ef0a82YMOAbRjt/wecm2QEV5DBnanaUrmvrL5dg1RSlkrrkIxhDf/1v/+P//nf/v1/2X/865f5Cub6vy/7" +
                    "ZU2N6cv+41/+95f/Kq9/Ys1X+35Xvmx1X+76p/Pv//Ev/+ffvqz/ev2YXK+/ef3Z5a9iv2L4Kvkr5a+arm9a614flb9sCK8f9vrj+pzw+pzw+v78+raNX87Y" +
                    "6+e413dl//r0+PrL61/wn/5z/0W49tHeXB/qS/u06NuHRdc+K7x+Bcl+hdQ+Mbx+xvWhoX2Sa5/07/2TXr+G1ye9ftzrFxnb58Xrv9C2D7o+z7bPe/3l+oTX" +
                    "j7h+Sa/vuD4olH/8y3/5t+t35PW/7/b/1++PG99y7fuv72l/69o3zPhHr1/Df3n915Sv12/M63+5/fH6l7/+Gtvf0fe8fpX9H5fXH/b6ZFOvn/vv40fNnx9f" +
                    "PyT0H399R73+jS9k2k8sX9dvz9e3ry+MB8KFgDY/CrK9QK7l+p17/Tle/9L2W2dfP/kC2PmvemGbB76lgWEv1C5cY8e1o5ED4Hr9hocvn64Pe4EGAFxIXoDa" +
                    "BujrR1yf5FL7JOc6rq7jWl8f9ELv9YuL/Qvt+gUODF8/KfYvhutbrn/n6yOuv8TrOxuiBFX/rfAdtnAhd8E4foPa3w3kFTSvP4eGaG3IXN8uhGaH2l5fDDZP" +
                    "NBP/7AvJ0n5u/74Bph9gxgZmuA4soej+MprtyNb29fH684VpvH7za+3I+o6sk5DCkU398xu0of3e0pG1CK1tR9ZdXyDXwXftXkCEc0fYd4RLg0eBNrQPujAL" +
                    "7atiP6umfdD1l+vs135W8zirL/TpoF7QtsP47RfA+eC+fj0IbWb0An870kF1428uvNtBtbFDmxrauUHakb/gndD66+d2rK8fZN04p4xpeohtadi283qd1Otk" +
                    "mdDRrQ1dZ9rvo01uwGoGrK+PLOWr5gZGqu2spH77paKhS1fxDm+/BC7oX5/4+n4F3n4jt6MbxmeMW92Izygd2dy+2EL/xYX+NRf8wNkNCNuFO69kQ+B/Wz7a" +
                    "252cANoyUEkD3XENp4HjAnUeMMdx3mM7uWV8ljXzTh63dRjHmDDukP8lrF3Dur2MdHZdP7u238qhH+E0jnBkjNuZdR3jcsZ4uZwlxBcurj+zvmPbb1bXb1Zn" +
                    "4XLmd3ZczuMjLkz7PQKYXs+tgNYMWPv/3Hh4+Qg7foPNdoTLOIgdmzC+Rbcz49qx7bh6vJ1r+3rIfKtff/SvkQZrGFd3nE8twenCQ1jjuJhfv1211VLWxF5M" +
                    "1Y5qGDVVmRd0q6V8P8EVL2YJaxywXhdz1GAdN3Llk3uAtd0Brz+nzDeyESc1nVG1484181G9LmZHtzV9v50ndsA6SyUqoSbI/dC+kIzjb2O7XRdYMx/ufhtX" +
                    "LsOu/4p2nvuL/Ppv+A7mgnXA6cpDWHODtZ2C2q/i143cDqtbbmTLeDYgY/utvA7tVUr1azQ7eVqvG7mf2dgK5boWVFBJ5dNz+/rhodVTHdViuapy9No2UHux" +
                    "fv1lvrb9NPYD6uZt/O34+NJzy6COO7jCY9ufyTKOV6JS2I0LdoJqOqjrU32d0zBAzf25tVhJva5gny9QB5jePSuMryd2vLR4CTtxCSt1VCn9p47W5wI0dkCr" +
                    "ckzV0vg6n/MShl4ndSTTqSbuuEfXYPPQ0FChi7D17/PjPOLTiS1JgXYm8Ql7ff3Sm7pcsfO7oT6eZVLpZZKfTyfdsYSXTw8PYy9+fTuMoR9G3+/Y3EGjM2nn" +
                    "C0qgXYexdyPXXy7sEmDXPtm3T/aBmtb9LGYNuiKgm09l7F8q7/sYAtLsZdCohfy5Nc1c9kRuSqAbTQx4P4K2Y1mhhS3Q0KT2YA4ooQp63as+XVAOCIN5WPG+" +
                    "r4Ly1sDw/Tp61Df3q8S0ttqFHk9oZwKPHq77VUI7iqHr1eSSCKrdStVuK4mqcsG6dg6pIhqNqh/f7Ii7L5pDXJfwvF3TuACTfkxt+44CfaqTT2bk57bdqIZH" +
                    "EOPN5K8LOXW4Ji4P5w4CW6vNHeI8n4TpBWY/ItkqzWl7h327FmmelLhfkcXP9Vp2LPSDqtVApfUux3fScUlrt1eSWlELZ1TUPhmqGAnkqH3SrH0snNHC391x" +
                    "DtCVRnomw/gKwenRwC/89don9GfS9WfSdST7jfv6Z6U3JQ3JjoWNZqlqOw5ltC69rsxjrhTF26kd1v0Czjx0uOAt2mG9/uNC+9oLrVKLZh06jGnDuJTnNczH" +
                    "j57O7zkgNOKgrrVtGWcqAcpxIBcbam2gxKWMbFkiNLF53LuFb/TQpw782LpRBhGu8elEqRWgtRWgNWktS5zgtmc1zDHhhWp4e2pliQuDpciz4F4YtSvYaY1L" +
                    "wWFh6QOHvQ/tPU/oE8sQRnE7H1czx0pj7EvH1s+5r5untvDLWkZhGxivXqTO+ZK4fZOYFhOorx8S+ZXdTm3rWAjN+LBIujB0Hcp2aks/tR3RYJaq1jOiNNjv" +
                    "BVNHJPchQ86iyr0g7bVu63OD1V5Vzwf1wjJ0LDNewEUMj8bRHIWSrJAsjISoQvJcHzmG0XBR7ObpzKJRoaeUaqZ2+3K1a+B0FhgUFx7ez2KXO88+ZeBxL+GX" +
                    "zJ9i95+82CUIU3j2kJb25VHaySltNFcNwhp5QLTiSYXvGBBB3avdttfhvv4cDA96o6hXE00BW6lkeUUD122f8LavkNDu7fmWisk9LmrGgEHs3Pq1+u1oJ0Ov" +
                    "qltqXwXqDCO/whOEeaVmGuXxPm2dF5XxppYxLyp85tuptVxEtenudfsSxKk8e1NLq6evsuf1a2+btxqXs9ungDDjpenRMusNrStq08AsUb5Or6WBEWxWrXYD" +
                    "93LWmY6y0eb4sQ0AlWm+7G9GYdxbreC4v7FzAEiDCE+TiXFniwmSI5hxKNgh9nzKx5A2ys3qOhacm9jKZde4nEObEw7MaSxI8Gb3tDS2/ZG1Nw1s2c+zaGjn" +
                    "m0s18l4cw2Cp3RK0T/O8brlOshdVcRBvbhsstU+Ikd5cZ3mcX/GGNvOONlA+ee5vxmGGOjjiSzvB82LOW/qZpceUXloz50qRG1aqriqPhK/fv9e/sjaUrxL0" +
                    "9SsqLzAJxJweXs9t6FVah1lqu54d1sSZi2Ga91IJBXP7tx2Oa5QL1z7f2/Xcjtb1+lqIjOJ11CSY/dy235CxZaXTexViZd21zq527Xd49WJEPwv/wNPJ7t8X" +
                    "1gV6VlY0aW5Zx+rNiX72tKJJVAxflXGkfscTzgnpEA/6HZzl97L4VR2PWf46lhAjfSee3nDHjsB+B4bw1L/2R9z3KonYEeMFTlBh+TbTb5v96OnYji+ZfjPT" +
                    "y3uharBUdjgV5inF2NNQdWXFMD/D1UmFVoCCK/Vx04QNOS5puYnl4z3WqbST51k+gVnCM1D/MJn+QzOZCN5Snna16izK04PbQa5LMZU/nVNgU5veEmDkAsdp" +
                    "e/OBKnx5VDG2qIcho8HXV+7Ne8UcJvFl37BGKKXmpBG4L/j6uvUA44aV5lKD+0LEmDEtbgd4IFrdz9wEqNPiNAupddM6ruWB8nkMNQuoKoAdhW6fQ/t+GbjI" +
                    "4ygomasyhxod1kD5zQzZAmuNZxoDXWp/7cB3nOQgxxhRjJILP5xj18oNUauUkzy5cxtQtJ0d38/06BKs9WlxJRuiq7iy2BalY3Elrmm/nuQV435Nu3rGeE41" +
                    "oFZW2Gtq7yubonGQlRNsv+bhpRFVWJqhMF9g0fPK8Qae4NL3skBfaRCHWVdRDZZhUkWnf0Bc2wf72fMytMY8pq99QDRVe6F5U1MxPSqs29IqaW3vxj/d72gx" +
                    "f6Ql+2QiwlhjVlWw1jFjhTdHWPbLTXCZE8NdUYU9Dw2tophotHu5aINk+djm/tx6PvW1Ixm4WuOTO+E04ScWzJL8YieZaT23YlkbRHklZ5GnpzdpT69kwciu" +
                    "aKwIOgtGL6hgNCmYFHZexWaWzGN8wXuDQJ2QmC4jr6lA2QuvruU9a5QF1brbI15TmjtaN+abBXe0E1NTnpJgBt20H1vbj+3rN6/tCdyyoB0dkL+hIFZ9554W" +
                    "AiINLPZNT+r7O7G5I1aaSkdTecLIPhS7HY8bO3nTJj5FhTayVdy0eW92+pmkDjbPQbLnQw6oEVhPWeB/ibq0LnnmbvZEnaDjWPuzp1bCfR1LjKZxOHdu2ty2" +
                    "z9miJH9fEFdlhLyS0PrFGwh0SQWX7ewsgROv7wowKtrhqmJIEefBjINPTGPkAIukQMPnPFn+12M6kbW/fBzVr15COej8w/crdxokI8lX1kqFyRRvJlI4SW6T" +
                    "FSrBHH7suz2foSJ4MNrcygcHosy3XbbwCeZSfswvwryJPZRYYio1uRgJyIii//HjYijimWWcnfkzwviNRxgT36cc8fXJHc3umEEeNvRONrtSv2PwPLdPDQ2V" +
                    "4PUaanS57dI22mJIfYOho+0VXEjbBT1nEt9+Vsv7Zl4wLApQ+5Fhwa+wSvUvfFoL7/h6qRTbt+HEpnFi+xXN+D1mhWszi23502DM8w2mt/e+kf2YEJ54SjG0" +
                    "HFBKZU1hJ5cCG5pLReVGK/PtBPHfT77/HFBUFt7kcbYqj4u5tUnL9qfQuZxL/IJ0xYi6Ojdb38BwEor+4TavtC+S0u620u622usq0zG1NIAyx+6VqmPY3SoC" +
                    "Hb6Ii12PqMP9esi8JthZjPqQIlSsrmgOdS/k4NU8vbSw5MNrWl7ActKfFmIUSznGGX5dwFmK7OixHQ1tHR8IF3Bm5VW/gBnfxwTytjmrdjBrghnN7VVHqyvb" +
                    "IHZ876ZPvnHqaJW+TRhBAnBUXg1g26iSmlnPXxpFbH7GtSzHT+PqdQwzMaVogOznIdfHT4W3d6KVDbynDbJizu3izaMoTqOAKkxTXSU6XEAxmg/I5H9a2b+7" +
                    "lWWwQnjayqaOWhJExV70urzP/GlQiKq5Iq7cTmZIVVZFLo/ayFelNpodz+3B3MaGm455sKHo6t3eVhgasg6AuxoizxiC2MmKNwFcRHvr/2SQZCL1tXMujFuh" +
                    "NNltZpAzRqHkxzvdB8P9ZDK04fHaTo4r/DKukGu7GzLqgRnzdiicBQnK7x3NfRdz/fuB2XhdvE5pXg0yyKESDqpAwCj6gCQ2d7h95zp4kqDcfFATS5kz82Zg" +
                    "hjhldH6u3ieqJwa5fQ+vueB1Y+viWiXsetHqYsfRjRvXDQKjM/2A2PFbaMfjZQPS23K7dnO7dnOa4442ce7q8tJbotwv4OT4Zh1XKmO7lun9w0r/ajP91I63" +
                    "vfZTW/uMo/bWtwZ8VolH4ZlGzh2sAbWdEXYTRbCPlWfV8lQqyLq4ApmC+McePq+h61j9PstiBjWmX77WSXuXs/iIfNjsLD2KJKvC2i5p93M/yIqwZ57jZFd3" +
                    "EXNc0U5l5Whc58LOHmQgGV5iBH2u5YZeK/TO5npOoZ/NoAMpLIcFnY8bK3khq5wAP+War3RGfzdVjrsuj2jn1y2dhWRA2/Go7gS7IK9f9y7Cjscs+lgQb1Vx" +
                    "O287HiMmTqt8CzjK9AMV2nnh+hdLrcGMSroQL4K+Z3KR+QQ7niziKJnhTI8HTqHjOrxEkiixKtfHrdKK0wtITCw8W4tstdVeIs/eJ1miVQTDu/bdC4ge4WC4" +
                    "uNK8YmhPMOQ+faod+pdIiKKvHch6qqQMeAI5mEUtTW2CZ3be0JGmimGW0Yhx2bwNCnBXbYdpbPZoqjjsRAjbVH56U5voxVMlenFOLYAWZZSJoiIoSKjYAcJM" +
                    "8xc6U83DaGvjXChJ7c/kRY3iWZ1PDUD7HzCfMjyxmByb1z+I+AzPJUHkA0yyAB4b57VPKmBakccuaG7huYbOq+vThPUB1/zP0OI/3NCC0XzAOu8q6db99qO6" +
                    "sJGtZCNnYSiiso9bM9Q+0KehzEOJtBMSd3WPNybG9LKCUtbcK7ockNem/AM6HZ1yXGBukcXwny/dvfFRhNFZlMbLyi4xeaI/rIxZMX9HabwynnD5/r40pme1" +
                    "QWI0HZekOqmLgP68OnCI8XppTBMMN7cDyzIedJh2jqK+AVra9cgRRoVjWcRIIk2A0kJti1hC4dKuDiYbTKnCvLn9UO5NgJ/yy9/xF/Om3cONQFyP7kprc2XQ" +
                    "FqnyUbd28hLeRZkkC1EEISYvS/jJR27HeuO6TSmXkMETAcrBRk8SGJMgMIZluTcGVWlItTY1V2H/nzwu4Dou6HEZ+7Ef8nAZM7jP6eVSNJ3I6uA4hIThY7ip" +
                    "owovZn3B5zEsPe7JJdOPjqer9M+UiqJ2s7KXdSDnkk/rXLRH6bBXxKI9ydXO1Eb3ktiull153MkVvhLEmeWxVGtnGcTqfr7x6f620uhxvZ0vSLUmVnExUDlR" +
                    "mTlR55GjMJQZLPJRNYG81q4+qAelntjUAS3VTdWPNnOUaoHC/OFRDkeaKZvlQs785UH9TkYNl+MbGmcVjGl9OJUqzSuzdIhbazIcLAoRyoeDxea2t04rxs28" +
                    "kd8Wge2NDyrNluFGtkcf1HEhy7M7b2KNSQ5KEMM2qLBL8CwjODuhpuEbk8Hysh1e8FTEfpbaI2qYyqqfdrwyiHB4q7THfLLMk1dxJpKMYMfEnWd83cFZm0Ft" +
                    "27xIHc9ufurF7CmIbV5of27H1S4jCjiZsiBiwrjhGcQ6hxAl8SyLpDkxu+gJEPHSnfRGWudRmzr6mcC0xTBXPQyXMw9nTaW1qaV9zZf2NV/aiaz94rX+oK3c" +
                    "RDzrFILbVPydNz/KiHnDU7RnnuJNHUzrWXb0MiR3d9sQogrdLI6I0+xxpkhAGCYmGFY0I68oJNJlVkhpwpsavOSDaR6bQd0RjyOPiNdJk47xX3XGVKU8+TxX" +
                    "XHYCc3ULei2gH+djFWXoPfWaVtqiVlrzLJHGp6DbstK0GB/axD++X+KVDQ2g8xEDiuaRSUA76345D/kg6dEb3JM4j4yp+WamZWw8CLYOYI/DHeTh3rnN9c7m" +
                    "zXH3A3oej8zkeXmvlHNtjxv5Pm9KH/qfYCCnzUOsihGj0ILAnoARdk/9xw+He/NjVOyGFluajx3eSl/tqVXVmFUdyOZa+bxKC/QYAVk+m1k+B+GmasS+3i58" +
                    "jCLciJPwuUh0pItmWlJ4/Jxh5CUEXRaPNPm9Mbzusfe44vjW7fxGfeXdbk+D5XI+7/jk9HHouOQYA0yHskYxL3MVZMjHeN/f9mFm7PV29FI94GaAAJ3cb5SK" +
                    "OLBo9KuMa9KQPdy8ebrJZ+Y0Vun2tlnixiYgqZNiM7f0ve5i33H31Fyq/StKO12l5UIM36FBYR1LAr8vCfBp/sS+ZC4Litjg2IMP515I1+M1/dkmd3qGkRjT" +
                    "UvVlwNzPg75604jExfk4oigoTsXd7nycxggzDauixQvO0xYJqDaMrntKOV9gFhzl9V4mOcg4sUczONnujrmG6rn5mS8NVdc3o8dPLuZpD2aZm4x5AlhrCQZG" +
                    "WuQgARx02Wg10i39OsULBT3CAohqrTw9TCpW1/0QU63l3c/lyblPCBh2I7RegFvFMky9m+W6TwpDmidVZ2dVuUjokoKxSJALYSirk4gTEI5hFh3DkE1l4On1" +
                    "UIB5eVHHcQrrMtdgLoYspxOucRO83KQQS7vKCzTyjKz769zz2hC2nYph+yCXJQVuW8oLNo1WSKEV8snpYF/beu2pnYWUalZS+UaeJ9TTqZSOb/D/k8a8hgbQ" +
                    "vVuW4ACqgBI53sbzKihCaIAHxwO3vLSRAj4IOPeUZv5nY/u3b2wZM/ecbS4pbtJAKB5bnNyFessNe/Jm3P2O/cFZ9Zi/U3DYWCy+nXtCy+LFOFRbM/1spale" +
                    "3EP3tRIpiuLESIP9jE6MGYgUwtSt7/sKT6bA9JoJMnFuBFppRIi6UP5sBH6/jQDD6p76lH9oIeQPh9evq/htB3/mQcl8pXyT37L75h508NL7YFvBO0FW5XQl" +
                    "siShAimAUb2TCwRpW55E2BJ7F9Q1aCnBwIpIyJ4T7yp5JFcIWuo1MKHr4uMhVO43dBZ6oNxv6LH/sdOJXhDOyxamdaLV0GsLPlFR1D5BC9NSV/MXmdELP9a6" +
                    "xN8txTCSVVHvY+EVJnWQW9joUQ6gsmCYYys7CMnkrjm3CJJMM4YVnnkXewlF0XcMrXtONL/TfKW5lH9jFnXSwIfWP4XezNaPOFMnOQG1Oh3kGtBLe8821AjJ" +
                    "HoeMbhKSvyG8cqFMiU5nOnJ6VgSB4dtKmfIa/SLD4HiJKR019mWN7C6QCVuXnlKmThMpSxOpTU29cKZgPaTxVgtYvZW1AVIzaE/7QD9pyaESP2de/POipj5o" +
                    "e5aRKAURP3KGQSY1bgk7TLA3iBD5E1EbhL2QW8vpxNvewOLNisqCNo+cRRdh654anf+AOOi9DRE8zP5ghUBhw27NKgUbIq95vklzsPG4fL76czMvRJibG5hV" +
                    "8A5wjyid6GaQ26bVUXfZ5a9rQ9IDAYEqCtJjb5YIU5fdz3VbjYfkH1FH59UP+zS/2B3f4v0geXd8y1tIqdfP7UJudODe56Zdn8Pe12F9HWRllYXSugCkY3iM" +
                    "CgInOyRooGhHEBu2tpvV01s+SyuC0T3mmreXvbqx/QmGD+44qnVzKRFTKKf2RKEt9eTCVvZE41C+2dv25l+WzopnST5LQ4DmhrEi5B5ltXBSsbhNi4a+oKOF" +
                    "Af2mGEkV4LwWHmWJHMu1NepPLeHpfiUP3W1hwpqf0GyLDuZRy952V2jpJBx/zindd/KQwHZnG+XAlnH2RWZxg9vcaYrwBsuL9Cfjgm8OroLkO07qlHxpWZsZ" +
                    "pWkUI+seENBNHxz31YDtqwHrydyiEa5ofEwHeNAtzFnTlWRykw/YvMDmzr+bUbUvPdjzjKIYQrm0otjNytdCs+vR1Y/1PotpVIVbl9bwQYRHcMphZ1FgexuX" +
                    "1ni4DIkFgPS7CL4ByUnCDxlUypjK45iqSt2I26TV5P/1JgT8LNq7O69y21OPwwxJjxsdkGJpYiRn5nsu8kRKrRqZWGBgNSMmAt7Iha/ojZ4s39rCFzAkC6Nn" +
                    "bu9wCVz31M58YU0B0SKxUkTJFa7lU3jdoEvB+d3k08CXsscOt/EzyMuPV3paCNAS+C6T3p10z515MegYtjPjysa5CHwtD8ltnJMIeSUnKI/LGGaCh7KfIxBi" +
                    "UvajTNj6p4bX55SntMmBlgCRzzhxwprztEKQqeD3rLhbVdAQkJxZcVaw4lBFAus+cWMfNwtF0OIyRlzS02s0BmTmiSQtgNdBBm3lGV1vHlJrGpREmkqtBaUF" +
                    "PWehiqlkmq/xmEqi1VQ9H2oLh9poCZleRPK5s9DaHjO97GE2OeIzZ26mncr5zddxMuOcYki1qkPSoqpPTGClGBmku055IFHgKn+CSKEYZ9o1sAOB/XSg0SqA" +
                    "kbrYwCbjGE+rQn6Y7wvpMb3aOiYCmerfG1dlusGBI+eUNdKuB4N0gnsdvdnubHRVlu6r8DBHmEqiDwaYnEjuY0CPk9k293JZeBUJl93A+A5Y/XPn7M/SjFU7" +
                    "+3pIM35DcpWBbWAa50SNrcYYg9rk5Eo1aHK604mVRxe7YAcUABnhpg2OYaXv+eSqI6sCdzWb6/bvcNMmgbeE10CSYfWPbbPflNflnDAi6683smx6mvecEXsI" +
                    "wNXbYcGWq4p78ntRAow8dmWnnmy8OmVj0lekpCAQHFklW3OdS0baJTn0wug8gTaYZGz9YwLzjfGuFxoUVHYumt1RYG+P8FtquhMSbMemJ+sjLMfN/qC+NpZn" +
                    "0FlJrxYmrQbF2GIR7Ng1Y90DJ+EFmWANVBZzZbNd1Eh1rWuYzIw9Bi4kQ+vdz10fqW6860RLChHeSO5x3a9mjRRxgqs2gP4sQHURJNi3V7ZdMjfNnnKwzaTn" +
                    "HFpqtGFJuJE6IiyayrQZo3GIsLVpmFMkMqPrf6WHtt9Gl4v3Jy4KtZmlMuQaq0FDw4/FTWHfEFZtcSSsbd7d0m5tkQxEIM8AIQvpMuMEoyQoCYl2xgQLNbhc" +
                    "ro8KJH6BwZGS+MWI+sc2ywpRMqtEybR4yc3N/ml8+Tajj/f8uPs1WpTbPo2eel64nAsPM3evMaBvuCm+N4KBZzYiFsanFp1DiXauS4s0cUQNaEWZWEb7bIbT" +
                    "P+VQfrBtiMed/meDD50Ru2eXe3Fw3WnZYFFGZMTTO4NnjpJPrUPiJBJpJRdOKfVZuDLDUj+vARVlCahIy9qX5x1pwZdg9fGp0xi2wBGdxowI/3ILkzJy7byb" +
                    "MG82vRNVMAq0bCd3KKlCHtuGlBeLMZhPAh8nKBWVMO50gh8r+DindD7Jx1mEvI6XDnHfIOE2P3BaxaLunLz1XjQTmv45se7eaJs2wCp70uFc+n6vtGpMoh6x" +
                    "eQ4f6Ui3uiBUsgo0cy7teBEsfbaVEccsmGEt+O2mkpuNjHbPXtwNUnhUxVBrD8OsZcqRBN8jiFS3uuvE+O0ljP0Dht2fLdN/8C0Tg+ufGruOO7oj2ydZpksZ" +
                    "bGYhr6LWtl2KeNADKrEHIzFK3T/sJOjSX17pwAzBmvZemIKjq8nimOm4BmRFSi5UFjmbQZbLFkYeQsBQBWk6Q+BbnEYLbtrncydEGPrHVDqlE6J62cywt4MT" +
                    "2YdhbzIG4RizKROud/Uua8ZGXeWU8gqlSFZKx7CmMqKuGmfYo7kcH9YkEMJ2iBCHHFVqhwxKtItYIwV2xilSegQB9Qyrz0/tyO5iVMVOeFgqZDGLdMrti49t" +
                    "k6CRm5FZfeV2PYoRvMix9pVWcmYmCIXDaApnEzyZch8UyNJOF2W3XCAv80eTZ4FcwDYyQc4xZKbWrUAmAH1+6ogfR2kMa/0o/BP84bzqWX16Hq7gRe7qTp4v" +
                    "n23mitIB7Q4KmsLeLXHk68yiH944RUfLHVzACeOI7JZkkUVOwh5S3uomN6ywcOhIgPrHRDplZLHrxhqkfpZMtOQdx9S1n3gUB8a9Nt5mFqNIar/G9km2KjJs" +
                    "vITvBSjUGim1k8OwLzedFGCr4EQlLS7jCEohtDEpfYUwdwtb5RSZwVVgp8+X8YwsiVOBwvj68rRywiOcxRGuuo4sbZ0Q3Mo36k+pJtuNes8t0Opwo7p+AiVW" +
                    "b3atMLXRjMpkNQWp5eihnYVyPtGOaFWf2DWTZiE7w2B5un/ic0vA+qderrV1aAPnzqcg0e/gsQ/zz+gnbWOQJ3GREN4LQWG9I89fvNnvKkJQ3Bw4zCwHkcIF" +
                    "bRWbIbkwCJjf52QZBR69WRBhIzOumHVVoG7GoXLZjW5GTOPojxwKfcfZ9Q1ZKqSecusWJ3xcHPAUclOMyZSwt10t8K5Obsxy/nhi1ym7oc0MBQZWWlKjgyGG" +
                    "mewqx8lhBmItZpMkhlZFqLrDALGHCo1ZZKasihXuAtSsflcnqRTkhQJZVTHEvj5d578V/coU7FUjmD6V7OM+v9wsFJymMbJaxkVa/dqXDJN0n81pJKFS8jko" +
                    "ClAJMcGYuAIGdGUdUU4Nv1uD4hKMrcaUKk5yNPM6PMM9UA6/yPU1qoqj+Km1ETS9oYeulmUZiCRKfyylSQsK9Raf5eNEY2r0rdgSebnWnxe10b2MJsNVECXX" +
                    "vBIRZ15Gfj3tbyuPNLnCGiweMrphBMNT1iSn6V6/5fXGisyfI5LlzuikA61j+givcdLS/8rNsuhGt68uBu8kSKD7ZGeylVX3rc+bUe0XwQifqy0aWGy7hQjr" +
                    "xAgdcaN57JHmrZxmjMMD1qTdZ1fvfCPpZj6uEurRREUsi85vb/racqSMaIKOa1ycIAuDX7E2AjNfVI3NHX1E0NQSOYuB1MDUkec62MnVtUZmtMJTMuSDAPMt" +
                    "h/5ElMO4NykQPJk37xwM7faFNN0b7oWR51FevubuQGbY0qdRM0WgInOWH2YfJNzRowtdhUFGL5Lt8pL2E0mIBpv+NLi/Y4PLwAYXfq5b7zFUV31lnWRmhKOX" +
                    "BtZNlNm2JWAPuvqbeD+HVilBj/ej2fNY7aq0dYtmrqBKMCLnxOqRF7KiSgLxYauetYi/LIJYpQ6Fb+spG2a5EeMcXPlFFq924UUKC9+d8Ewd0aEVEr4a+SN9" +
                    "wrvmd8iNjjKF2+bXrje1X3SiFsxCpbGGHEInIVNgXiQuAz2aXWWeZJJMgYcclp7mZRLNGAf/1Ha9NrD7A213N7N6jBdbTUHhfdbDHd83vnLTe6qeaZi1+ZnB" +
                    "sYaVklY+m9U2RRLanfJcBxk8RdTmIKiSQKnLsBu0MwVQytOQr5E1AX/bKDHIwT/dKDUeRW1Y1EZJrFHQ60ZqdbT71uFTokZvkt7rFWi+cSLmqErCqUgBMvvp" +
                    "TJtJtDLg0ey+4BVfsxSWGzyLLIWCbZLBSDmzMSfz2Fcktg6NbLbPxutsqOIazPRkh4e6hU+nWUXXjao98NUrubZadkULCYwiojW9u6zDYsW9H983ehQ9YtmK" +
                    "1BvDhZoYSc8L1y/Cozw3+3WPZp2svDqsAOiyzmhZB94MrRIjNEMIf8+QsnySh2LvBh03D/PRMufOoHC3JrSrMOV8VdtlBUETabO7mi0Kswy5ckStKzIvAQSE" +
                    "Go0jcHzOHHXkTq0zSNPhh5kwDk9tRu9yeEcBzt6EeEX/gOj7vaOOVJadH+SL0yKlwNLc7M00azJ0rLAnNODR4UC2sKYpJL6bp/1cnouGhHJ9qeyPTCTIIBKN" +
                    "eE+DBOL71Qz+64Q2PGZA39EAiKqzcp/namkMtd5RoH90qBV7y4xGK0UdQQLT0QxyK48gpyZQc+VOENAq8wAXc6uFtJ5hXFKG4+Rc6GdibghX7nYsCanwlN0s" +
                    "R1krUV2EPe7XMJqF3uRRKQGeUSNa3blKNvZc+6DYPygfdIFysCUnWna17nbTOd8sOZ97OmsCz4xJXoddoJxpwS4wA7WZLJDC6sMASVStSCZkQzJ/iwHwwqIr" +
                    "H6aNCT3gViGrYhPdERZD7kNd1oBAXNclJziXtMC6MpR171gFKBh1VSRPFdH2ipAxJDI7dLdKInh34V1Z7n9GIlE7vYRoeOoV+mMqwL+UL0YsyTQtEz5QJmiW" +
                    "sJQm1w1z1PSL95JACw8r6RIC4O6+vIiP3MX5SD/fUEYGrNMMc1abOiZd0eR6drkEbkjl75Bta8yN/JZVJx9W3TBHcmH3/rYKO0ltcY/TyXygalhc3Ful//Ei" +
                    "9dPNgilCwTQzakQuJI4Xt9FkmKskA9FxddpqpGkt2VogQjRk9+cA/7YHmMANOf2N+URhLnpBm3BWnEjl2L4utDeahILGkmNBuI0shqHknnVvhEbMoJ+kwyix" +
                    "PdYzLqOoAKdYxHrG1dRqSo4yE2CnGsFRfjYIxDqUVEyVx0ybz7x9xbJIGlllNWJqlsh2PZ1DeC/Dpd6Rm2P7pBF8r22MiEy1BaFMcf23Xdz1wXwyzL9ds9Ex" +
                    "rD6vywO7LHt4HoXVVBEqpIReoYXmUURuZkhDebwZ/CEW1RbE/E5KVPf4KQh6lLdxOqZkxDaEGvkJgU4tGCw4LQ/wqBZbnQex2QXSnFQpEDkqcExGkv43aXdd" +
                    "zxtTp8IIqqDsj0KM+mNLsIbH1GYhwLdvTNdRnfBeILa7cqeVK/euCQKr9SZGnMKVd7FU6P1quLOV3r4O9Z6g4pzbvcI+kmKIKOJP5LQpL8c7TBpO7ewpP886" +
                    "KbMZxlDdz6yLjRYplpZI7c3GaK+LM7Jvxsh4jJiOupI81/T9HbU4hpzCa0lVFRXvtxUmGJxfw4J7PY+o8ICwSM7bessuevrZ7VZ2Ux+ct+vcV0Ct96wEVqg/" +
                    "N6jGGi2oxtyHNQY0Oln2du2W7UOJCtTVUwT6wblGqXXjiuZ5T2ewnzFo0uwwbGqPuS9bODKdzygdMI7MOAxJqMA6z/IBxT0dIRofcFc3qebhQJ6sa+a0fxsf" +
                    "IhPO3PIqUNKnmJqYXdIns67HSL+oBupGtX1cH0yrGOSnJRwuQIV0/2Ki7GC3NIH9OgHaql3GMf4EV8/3Qvk3qapvBhDH4Out4j3evlI3f8xR9UcdHxAngBY1" +
                    "uRIW25iJbWVNdWGK8axYRaQjzw6nXY2Mvk68fY20kPOMLdhMMaTxKYm1tK+Y0gqX0o5WNYhz3LW4+1zYHh1NXHPSYFLUBa8Xe3a7eojpQ/9lD3ddAhlZrHRo" +
                    "z3t19JPCsXA/yMJ5GZrUKI4uUmLGPexh5rQ5L2OWcoQnFvKQCz6xlyUCIxrt06WOG4SYC05P6gFkuVndaErQknVaTMV29ZiFnG44rJvBlNd2OPlOQA8660MI" +
                    "q/uCflU6TJUlzKI/iXESJqb4Z1vWkf4rD07FPLLknK6dWcIzOvNT7fGDIZAT2V7o5EWKRbar9GfT/DjwL8k0M7xObVmHSvLwrsscdMe3aDvlRCpNVULMp6HU" +
                    "QNZDWKfkNLLYKyAfJgo3kghyLiafR30XK11Q5i42YbzfpEi0+QODGk/MZPthVokbTJV+Xbqe3OWG97Ebe1c3ElfdSFy1w4jAjqvPBnxec3vAc8Mzp/kVcn11" +
                    "mG7UWfpOPPe6NjkRt4iDh3VW3T9szLpMP/gjv2o0WLWf3tpPbw04fHBbgjkcZ4eTQ7vGHiSIQY6CjA7FMUnny/SpmetajFDOlMNqcdNOwi4GNbrya33ETg41" +
                    "Ytk+pbg7UUJzqkkZmRKyAYJ085sGiPo04cdKrkTSSExhmy/DYUstkBEBntd6VnKcEpRHQfJfPIZ1aq1thIMf+r0MAUO0j/WT5MTQRv84+e2DmOyyt0CLucnp" +
                    "claG/OHGNSxrRLYklnUgFqmqnwmOCYGu9B2AwuZHPKdVhksZct3QEJ2pTHkaYMvA8wytLM35abY0RAMJSBP9tBKA0T/NfNPMOq3AsU7/A5gVUk5F72RT3Dnj" +
                    "rcr2rR4jd1238BDPxVPRw1X9Sk27iqZ+qoNFN12QdUlHbMyOWgNV0xZGkQSs4+KtnJyLki5JaEszNRd8Od3uWcIIxvDQ0+8jSdfa6hxikd+429/a68qI8+MY" +
                    "OHUXxyQ6pWRxOqGNJZDTtI4lnJLbl8QYUBpJFdm6Rtm6ToshHOS3SbFwv644l7gm+YxkDOHpPSvDrusyQ9Tf0qT5WGgzid3tTQr1ZHrQjVBvmSJqhDUaJgJt" +
                    "WBHyfFuQcuAxNpxn/r3ZYBfmQODCHPkSfAMXjomKq69f4lrL74kylQCnpTlDG8NT2ZacT4jJxCmxnk4teTWO1JH73c19gP1uip1FsAzGjpDEo31dWnF4Z+zI" +
                    "tb3ZTMO+LViVGKyQuYiKutoWJw1ZQJylACCvy/Qk5sVrQtSwrVjIp21OQcjG+FSsRe2s0siuQWDEjWgnekDr2J7kfsi4SvPe5YGZ9yoeUADYH5TmLasfqxkp" +
                    "G9WBqC4smCjIimNruihv560dF2Y5+p+r6fX90iaQ42Oq8UeZ16qNcjhwnrYHWOSsmo8S7A/h5uxwn1ZKHBzpHG6XBm4ibRekzReYBLqtD6pwXRfR15yRli5x" +
                    "eHXX8RrnackaRAB2P9cEcEzmpz7PtOIbm6B68ByCfIo3orz8oQ+rNHncC2kOYe2U40UhcBo/muOLbHiN4EFg77T4giIGy+CDvorw7D6dmpn1XHSt5tjYExGa" +
                    "8bFtsuLEqm/35j19sEFXgikmfyKfaTGTpHhwu0ByTCQWRfeHO0oDHMh1+uTpG3LrraBPtOpacE/XmJEsVz7CQUp2uBjPGJY4CiGqHNzxVl8RiDGVn9nh3rJh" +
                    "4tr/nJQe8chClGw2e2NaQrdvb3Fp866lAqHBeb2TVc6ntndHDq9fK+PcggyBymBSHmC+nJbd3hpsnpekryiVlRaf2nlSqbDK7k/t/HvWzoRszOmPq8Vv7mrB" +
                    "GMdifhHYujUctEsomP6x01y0+NtNKa373LMoXjjC7axUzUPbwKE26AVnQJhHZJvvFebECpACMAOh0W0u2pxxX/hCKOxgsbhoY/oId0iEbizhj9X9P7PVPQMY" +
                    "H1ONPy6Vo77oq2gsJSFdLO7FdHkMM6jtkTMNJWhz7hEEk1z1iYtvLAC5FQpzWwTDyX0AXfhKLSKGURiuxv3xReZ5AZu4MB9fhzbozMMgVONTDitHfbHjDOTk" +
                    "rsa6mJ9aRfbijWbaDTeDY47BNrjaiRj92W03QGdOASEZhs9F8KhGIS0sswXdhn1114xNmGKEXRq/VljxTcZm2n7uDDXI6zIXzzBhm4z5mXKBkzQeuXH1U7VA" +
                    "UI+wnFKN9fyOrj/n9DkMqxjTqWPcNWYEoWuUwxAhTv/SbVdR3R5XD8CV1rrVzoWpj4F9Kkb0sRd+Qu34MqDJPBxklFaxlfY7W9r1V9rxrf2Ktv6s8ZFjyI30" +
                    "SAUUdjHmJtHrPH8UflHlOG+GNnhbDorxo1EtHp3KQq9iO4gDDiGqZSa5E+hOEW5zBYznQTNXy6nBGwjeh5uje7llFEHX0XxiLnRzUUsB9fBSvrunTy6PtCtM" +
                    "GXNVgc1cPsw154BGv9lIfe8GYVsGWNquaiGwzVA5RzSemW5wM7dPDJyFxUVuiA+g02ND3obCcPobK6SwJgutfrww8CATkxsZddiDheLNzDkcVQjaRkEEXesu" +
                    "Jklm9rmv6WkPJjWWY2f6CBPdoxJUzzOxL+6urZmWwXN3lECaKQda7IoeJhWA3mLGNT1lNP8FqzAF3ptjbGbnG82h3pKTy3F9b/c2eIRJqjuoqgFebW9khD+Y" +
                    "E/nIfHFbUVaDnUkSXVMChCPxOui8KgnJeXjfp7liYA6IXXKz+WUmeJMzf3IrfvvcCkY5ufB3pNhrwqLxPs9GSumgLoTdUJ7ULSnICvtlf/cwhzWX04iHOX9g" +
                    "pz5bJSdS4YxulK9EDhVonCL2TGuZ7aWeHlPh/Oq07YeOQaBLddhTxrP2Kut5f0X1nvJvU5Nl2uqiLpLjrXsT3hv7GqiwpVRQdaGyGGKBgbp2zrusvo3AAqwC" +
                    "85nOuBT16hv/aUE3PbYh275/cFsmtpKbEE6PbZflvQ03NssFe6ds9LnIxxFTpE8ZYlAZMgKu6nIGlhQmj71LmJJRrGCqrgiRBJNHhO7CfHMzHivgqrvON+Ps" +
                    "sRIlzawu+qsD/3yWGW+89fs7TTCnx8bLR6dHZ3Snx7RvGN+UZHSsS0d1pruNfEYQ/N57Kgi2u9U0DPW+mTKg/vSYr+zQNVLk3aOnHGWgi/Z5PcpGhmPTZDT0" +
                    "jtnIwHu/B+4ypOkpX/q9yExwtfxfzUDvazM7Bp2dMlAnyFq+ySL8BdvARBgPvoA0HLsvvAyaMCAVb1ESrr7p03AVs7LpGm7Dj4SUeL+3y3N3jKY5YIQyjG4u" +
                    "nT4Dm8L/txEY19dHOiRgQCl2q0n6Z0nAYJhT+PXGN1F3H7M/aCAILAHP7jXvQm3iNGfYbHoxsQY1ocC7+0ZD7TBd8kWThFye1JqdjNyOvbQSvLsoIokij79H" +
                    "W+THVAyraMIsxeexnT3LYhdzZ7kULjOIFQVlYx0xaFtjah1OG8UkZlvuhuMRxNo/I/tueAPCXkk1kTPI3mnIkhpUehcZdCQLu41cZfOFBB2tuICVR5bmWXFh" +
                    "dJRJvptTkqZJYizTYy700Q2dm2CR/uZ0QnS/7nI/JznL17W3v+gMFu63xYew3e6CKdcRuCU2wvq+SpdAJ27WKfY1GGERJnFroXLM+jiIHifJycbiuVxEAZZF" +
                    "YgW7x9GT66bmjGFN6anmTNsdqtZU/My6RRbqt4D0czI6eo8RNGo4ydHDVQ0Wg+7KUDI6pQHKkZa0cjVIqHSQsPwtCbXfG0VaZophZiec6rTQAsJul1PY57Nw" +
                    "xIVHc8jeFTXaJYOdUvhjSPbPYkjGYKX0cF0oTI6itjwKO3tSHNH6acsT9yVSWDtbuJoLH9jdPgXU+add0jtWFkM6WXVWkCvhdT748EbQmgiDzzG1QN1o2qZU" +
                    "ZMHABp+BHUNxakEAp6e0aA1paWflF+noEqqbPid4HLbCsqDqXziuClZ0gKoYXa3AAVJaP8oZhUU1iuGd4LdTyTnop5EFRIksjiA7yAivVpQnlbWFFVZzPKQg" +
                    "BFN+bnF0FVDuWEANxnN0W3G8SFFO/lUtSG6EJIs90UrYebcQVIkcUq0/aJSn8EbDfElzaHycjC9RRAxlsDoCnNgCS/q4hn7RtiHxoQ8zJKzSHIpjPEnEwMim" +
                    "8lNfU5XEUY5b/XejCcVU8LTOHzfxrluoW8sTf3Am4aRCZZZKGPF2tSLTiGN9dffd36ybpNmROXilTHZdQcqGkB9R0C6DmsrT5dCZ6+z13iepYtB4N0DG7dAI" +
                    "DdqVC+mGQlluabLMuCOA5bQJbma/TI/lmRWwVoA0g7f5vKt5etx739V5LoNBaAWzye5QtoXNtCeW0EzV/aT4+mt4nCDE3ovntkh9N02f6lzyXpVUdydP/cik" +
                    "JGUp/V5Oedimh3qwUrlveaTuSO2TC8lSyFNyc0w3XC4b6VjmRLABt0ZW2fvlRdOd0KUMrG5ktlBcwmvkosGTqVXmbqlPkgnlVB/a5AihWTqbSKZl7ZeOVvhM" +
                    "oYSdbtQsBkcyYzxa4Qtmhl2Gkks3+15w9G3FhsCgG77HkHS/J24WcLHaCO5j/b7ucxMo0WhiHJBCOYX8rSdqfCuCM5ufJThSdGUkKFtT0LUXOArJqB7b14k4" +
                    "e41c+CQPQh3MHrXYPj/hVsMZYdl3JlAaoT2SSVJ2dx7E1zgC2UK6hmbScyeZzmgKRnEW5tcFMPqNq3MzXNsMc37AiDZ9IxT6RiiI6IM97ABZN+pEue5qbkqK" +
                    "gaC+/gkuaJPkIx02rWdY0jK28bITRDq/09uvP8Ih0g99QmlWrJBhp3WZR/Evao8yD52ECzdT3fvZZQjzU9NmhUP30Z0sJSqzx92aW8hEju5QO+95qgrIemsk" +
                    "p46TghGt0v+Cm6SdToMO3Lo7xHa7num5TMIwA6QpXuK9DzLCwqxKKP4FH1jykmRk8wO+81m7b9XqeXqZvckRerfIszh1XPYES8Rm1580zPqPi37htcKz+Y05" +
                    "I10tpvWxUWxbkeFYZsRmZH6TQoNbY2XKHFWE+Q0aVTBG+TF3GdUm/UVtvJnSup7amx47UqDM9q7SaTxpdsu7dGOvsWX0dONr0eM16a7J8JgeD6ORM2R6Sz07" +
                    "aQDschhVl7eTjldCK/V8DpbJYlRBfjeJ/LbtSnRsrycBm92fUKjfLBSKIc0u/HGP/C3cIxnJ/NyJWbr0bvSn1T1SIZ6/F4a9U97LFUD5IWFYwmSoD4RhwG7r" +
                    "UoIpL1gr4ZWVXIRNUWJCRZrRB0fqTGK0C0RcpJWAHid1pt3LhG/27tc5zjk9XdOpMsAxZByDZP+Ggu5uPJnTyTGfYxiX+hccmougni8n2UKOheFBheTReJAM" +
                    "udUzA3c6CQKH8kxAnsq/gNdzGZVwARerjNcz2G+PZoeQzT799Hmj5oziDiQLJ/RCNxwLSVOlRASxKKBzPAS9uw1OQXnYcG0uvEICUoWaj2vQ5UYKO79ncKry" +
                    "+ibRkUaorjoubJqfzpF92ppv9LJR9LKNucio5sdc5JYiU/o5NvgGl02Bv55jZM+8j8mlOdQiKQCEndDkp/fK7Rujo7POc1rSQSICcmggrD6olXQUM+Wk+aak" +
                    "RbvtMKs+baZ0YWhSeoMwjY74xiaY81Nj5w9u7Hi0j0Rrjbv6ag8SM5rmz2or3YoLIS28ZhBX6cI+ywrcYrpghX3knn4hBUK9gArShN3B9keQbFDmV/ie7p/R" +
                    "6jMRbuIny4YBzY/tnOminish3bxKUWrrHrCHkzxW9o4mylAv8Xmmq/peFHZuhdfuiTa72+Z+ubjViYe7M9iYevwI6wAwp4twbQe5u88sAp72R4NSNS33/Vzu" +
                    "MsY5/vpY83xYHNSpDxpUm3Pk6mcrfLfaIW2JundWvyj6s3cRrEiYciDaNkKaD81U3OuvJLIyCP3hChvBFdaglKRAnvZFzKj4QE8FL0SbM8A5PRx9CCekBMt8" +
                    "a7ZEXXqYoRuOSkXtoNQyigFokKsEdz6/eVgfxQCkVmcXHzMZsCudj6yQYgs2q4gNPEmEMI4qnh0XZmGdtMqLlr9TAKxZajRgCc+cwp8om3/mKBsGMD/lt36o" +
                    "54t7i/QjnlYLX24hpkq3yPvVXwvF0XIfi5YgV46kSGWLC//A47trNItu6ZJS8DLOonq2uMWtYwKGLikJXVIibQ25eiaA8x/b39/U9peRzfnnG+GsCZAqwtty" +
                    "dxzqI7IqXU7yMeA0DwvYeFru8gE2eZfZH/dJyI6zt7HLvHlyUoKdRfTNZpyRNZAz8CoLs+gqOu338nnuHPo5JmxzeRrzKe1+oVdiV+dt2iHdJSVLYxX8ieOr" +
                    "BigUMdSqn23wV+Hnbm1Fy0NxmFeULaCM/pIgtBf85izMQydP495fcrqm4LtsDfqj+CHDztQSt7NM6OYS/shy/8lkuYxZLn9YNr8py4aQzfVPPNVvH0/FIOen" +
                    "ns6H0KKshxa5m+D0O8eqPPbBI1lbm1bhHulsiRHaGz5X/VKQLV/ek67IUibKlJ/YlbQeZLe8vLxTPJZW37ndmAz2woX7YjmejNKYjHqsPtsghMtjdnPrVUsd" +
                    "xgrBkEmZHF2VXZ3QsI9zynGh7Y/LYV1DZvJiZDTuCz/OtVZLY0vcbqKQFgnKYE4vhAC5IbZKlSXNqmbQkVW57HnR4vNOyWwrYi8nWRS8Pb34l0lWnjvEaxjN" +
                    "IJenfFiWCm5m7fWYOqf2SqeX2o6XeulhVVNYOQHZnuo0zIx6IFk9x4bSAnFYa6Dlgsicky2TUTh4omdKW5xVWc+0nFd6bIwTaHtJeZTW6G2xOWRwi32q7TWw" +
                    "Ix5aBbNSK9XFYTnws+6dgONZxD3e2LF36BuMZXSZNemgpLNvO2G7YovsSqYBeIyzcussOvMJTMKyqixaQXyaC/gxkFY7cC9d+zrJs4CQJ5gMafnrDNraXmbb" +
                    "xQm28xZt2k1RoLRuU6zTzqg98ZwfeGfzSyvgHbuyOmU4bQVceRQ9y2hP760Rck8xy9pOp5cc6AI4JladVAzMOPpDxsUPhdZHa3FFeiIGrjjzawObKVD9FAw5" +
                    "PDJGqaVUVRPWsjig6Lved1VVXxnBJ+02N3dGYwYLaDqnDuTbAuIoh88FNr9RRK+3+jlpQ6sCRNnE9XORcu0wfhgs+BnZ8pRVudDcN4K7wtuQqM7KOfVTk+oP" +
                    "jZzz6tj8hoGlEdzLJFCqOTfb3Qv7BfflxL4w7GOOSWEWqLotynUT9M6LOgDRVlsDdlTpWX1KobzYsa4Ld5HaHvHgjgWeTWF3CkQypSrHryuJMioITxvXvfFN" +
                    "UnsSCrY8iuLeSGU2uABaYaNvVAvAAq6eBfzyRcszl4BhfUKp9SFBZ55LQCJRAk+S4Sv+jwjltxShMLAlmD+s9t+Y1c74lvDr1wb70PnD8ZS0ktvJGYaER+f0" +
                    "oqyR2SGi6Bxc870w4nCpwEa8Gn09L0PHCN0OaFHKugPKoidKwi8jU11MrQ4WTQRhCeVxkNwbRyr3NovqTbsqIb1J1j77GBUkyUnSqwwmOkcw0zTi2y3RB6gE" +
                    "JdTj6oSS1UyiiGSMsC9xswh8raJycspAor24hGh5YPHphkX6NXZKw+YmmN3KSLrJuc064URkbu/3sLSJN6Xwphc7hAPSvGmMksdAUaaLzXmTZpmwiux33cke" +
                    "ACntxuRMsWCCXNWSauZqKAn3IpYiyJyaPpEgTEv6ewJ53UGdYIX2s7yPqRlc5rv0ZehvNVGC3UUJa9oFuqW/9Q00cymIqhN3csdIwKnAIBNQDC77AnLHwHJM" +
                    "roay9PXMko/BIJf0R5XwW6kSGNDy2PtTc2zNa1Cv8Oldl72A9Bv3T1k7Kz69Tmx709mnd4FaZgWCs9FMQFDWvAYYsZMl9x1uPKvi1Bhh3TVTxqJkNse95qLq" +
                    "DMLWzfiK6QfcUZhcmpz1Djq9zE9pkh9Rm/OnGaBQfpkbo8HdqXWzGKTW+CAQXcyZhUphkXir+tBJ1vBQX5s5DAmw7N8yfPEWjhB4AXZ09dwroda78igF6JJJ" +
                    "nG9CuOT0h2L1T0axYszKiQZpPzSlcn3me/3l9XvsupGui10p5MbGzI39nRuKocvxtX3feOBsQNByq9hy67fy8OImzqzppMfSIcj9vU2OVbnj7uRbeBU59Q8b" +
                    "Nh+mV85jqzj8vWuvqmqvqmpYuVVGWOSz+vP09haB70yGA4GJZcsxsg4cRVUVoSQJzJgzueJbZDITu4pBLeWpsQY6+IZPwg8O2a35nVkkZyEMm8ibfolWB+PE" +
                    "3rtFbr7bRzH3YqViwHGbx1ejZ/q2k4khErnjynlNEC6Vpb43S0rdFAYmseEP4MCdqIeOYp0wQKd6qzxNosF5RzjMOxQyBueFnf2rpBFD9wXVqXR7lnr5WhMe" +
                    "rZCG3Qj/nGAvm3129Y0sdbPGd8qEx2lvA/MqqJRQblKBgFW48S3jzh6DSDdNA4m/zBiW+nA3dKRR3dNr0hYUdiqYl5DW44F1Kz3ylhdZbqyPbnmRi1uznWnq" +
                    "tDPy/foe/yScpLyFd3Yw6VDHWOjtnBnkOiqqQYKNc0LJYyzCttT063rf3agZpxsbJ3KrjY8u+fYQO7RXxr0fah/U7ZljW03GyCuFPahVX9qDHGxOoFcnbg/p" +
                    "Jk6GLic4xRlOcJLkms1nHTXdmcmQ9DY7NOKOCPDAtRrzU4nsanTnymRfM5ffmKcsU6ybTuhTt+aT3GTpf7Py/Lov8exOgtw3LAjdgSG37hoyK0QSuVUdGHKr" +
                    "4zqW02MgHdgo1LXrur29jG81Pzn1zy2yfLkgdHf97s0G6d2Z1mIdF6jr7IraL+6Yunzn2ryQqibZ1YLhmcetg8hcLnBdI4s942C6uZYJtW+GaTZ2vPQV01B2" +
                    "HO/L20EGt5rya1FWQ7X9IVT7tAWWfKti2X3daOm7WaPibCPLsEjz31vkiJfY8Z4fd8Br3QV0yAAMHNKozEHW2PRHnWmVWMWSeAEch5QMohNoftUIzYxsfUp1" +
                    "/WPO/c9gzs0wV/u0BDtHZbit9OID/dlJ/rHrWt0kCve6xOfZrZSsM43DibA4iSvqP1dObBXjj+mSkVD/mUWO43KMZUbgUllbVKGQ4IjRrM78cpOcpO8N46dU" +
                    "9STVRqAhXJf/ZCS7X9SSrtMT5SRdhzzN3pxf94Vafc8VNq4bPLZYh8yxtCcpbG6ju4PdpN9FRpmzlom7xQY5DG594B9rP0cZWTru7VZY4efElZ8zsjGOCYEi" +
                    "2UaqQWl1dFde4dYIBdtcbllIlrOLl8rcHWR2tUlIXc+jOF6MrSrvH9KS6xlblhVnUhViSxOaVF258nR1dFddBe6HaWO46hPwcn6fIidHWiDWPxgzkLpokxWh" +
                    "VtAqWsFtUazpiijlZp9zmZVXSbBvyqK0uDGAyC9y3S0wx81i4o2/GHN5fsdhXchQV/+03HpHnVU5s3EjAPxYrXVjQ3lfVHOptRvJgnzsbFhnxbNr5ck2sIxQ" +
                    "Z5nThyyKxXCimlpGt7p1G5EXIl6aRqNhhoAyyIRtfcqPvr+mO7RG16UM4Vh+e5yVnN5wkAkeUyGz8LySal/Qj1npluMAT7ssf3HPb9ALKQl9WIb1EknwhzFd" +
                    "mnPLKd6fL27l6ur6a9egQHpgO/ETUKquntKhP5hbKrpP3WnyzMMD12dz0JGN8vnIl31vQUhuSGMhcdrxu1UdaMQe0eAEc/wjR3X0HFoWwcs7C/dXbVniIoqI" +
                    "7xn1vRkHmNcaggGujzm0Ya4QyaAhdaAN+zSsNnU60/LMoT3l9tqPOVp7wRXRXPI9PdrMjfA3cLUMjKy1wN68LB8wfCEjPbruxfMeEklU94gOHIsJISNaH/iI" +
                    "uhtNAzN1OEt78WQ4UKSVA7yZt08KnsQVdodKyKu0l3S6DwPqQsvB9Nns0u0whfnfVkoH3aoOzSJ3DtcQTMOra1VduHiKIm0uTelKH3CUKU5qnTChW2P6fygy" +
                    "C1sI92fiBiADxFVldrysf4iIpddXZlkMI9Ny9YkVQ8sC/hq4LE6rUT/IQzF1A5MiZwJSkgK0SEPqcZQJ2poeZ+SUjnFBe5VBsgxueYKdRpCWK+J67JBAdnYn" +
                    "BtWiUo4O/JD+SG0OZqUQr87NZxemF1Gs+LPgXwzUqnhb8+qdkXn0GDWnlDijUhpqBFZN4eeKym6pkumoLbtPC8QZZMKk7YCw3IUF0rOaNBMciM54a+AMlr5G" +
                    "kNsNh+HYpXgqIDxAYULk4pc5HNO/2ckhZOSs30aGbcPoONyc2zYh4YK/FcqEbH1sD7sbmVncAifd6youKUfhrUb0YA0r3tdlbaRnbgsfHM1ZUlzDq9mg0d5Y" +
                    "J7KQhJIbNIOoQpgWZhmtzHgOYZdCarX39kqYuuDqdJSpksp/onB+zygcRrbmh1E4Q2qWkIIl+yAomClqYfHe18XA29bobT+U173gibmz1FJg9m6FhKHcZywg" +
                    "B49FhmDkPTeDThZVVRRGWZiYRVwuRLqe0aQ9C9HDNMahCRZzaOl57ptgQrqWP8GDv1nwIENaS/gjDf6tpMGMaH3KghYbhXZ0SzuJtXR8/afao7XFZZMctKIz" +
                    "N4+ve59iJSw4NNkoivvPA0m2XfHowOLkPDLuRhxFONKlSa3LS4aVmaJCerNf/yF5se8WZlfkG9sHkoRqrY/1ZfJWDodbmd5iap6WOvqv7Ah32vvGod2NV4pi" +
                    "Jyrv6H2zYA9n2Cq6UZF6E7AtLmpM2dCNTkZOXKmUEe7mzNoVesUDEaoz0z76wIqArU+9Yo+RGsdwOq1XypqWQWZYUQW8G+oE4f4cNevQOo/x4qTzqf0+bP1m" +
                    "5ST3/FR99R8nfJ8TBKdkoU7KGBy4CEQL37wk9y7sp5KnaJCV/kyUZUa0c96/qmqfSgw5x3Kdsz+2Sb+RbdL/BapvXkzQuQEA"; break;
                case "super8": encoded=
                    "H4sIAAAAAAACA0WQWUoEQRBEv6dPERcoqFxq+xYPMb8KiqAiPfcHI7oE6emuXGJeRtbbx+fX+/ltxzMqlnvE8Bp9thyjzTpgsLpahx23HwQ6ZWb6wHzClsPV" +
                    "/Y+P2+POHNTEhA/EQku0ij7QKIvkIFg3DGLmRFJWDWlwa4gGJy0IS0ew2Fjk+MfTC5aoKV4M8ZKmE7kuqg5Ss8FGCic4dQJ4E+DkMiLUy5eJQ4K5ycOF4MIM" +
                    "dQjRt6O1HeW2ksft9Q5HCRTDvDyVK3XGxVl2+pPCdRGF47iJs7d0bexzaBeGG8kPm9A/il1JSUbKUmH8VThZdvYwlmxo0+LCnKryptD4qKdf7HPwlW6DTCYE" +
                    "qjJMG+y4Fi12/AKx+jfbDAIAAA=="; break;
                case "maxmid": encoded=
                    "H4sIAAAAAAACA71bW44kuQ383jlFXqAAiXp/G3uI/bUBGwZswxjfH7AYDCqV1VXdNVNje9bd9chUKpNkMBhk//Xv//jn377/K377/QhHlKD/O+IRwyj1iN9+" +
                    "+/eRjqFfhQOfSz/ikEP02/P1t9/+88cR0zGPqUVfzJ/SjpaOko/Rj9qOGOvR5wJpnpTyIfOgmLv9rnH+nt81/XwuGOb70ObruUrQC8w1R5sX+dOfbSOC5VPU" +
                    "5SXPz9oxl5V5mi0SuAaXOES32m2hrut85w1hs/OnroNtSsX2Mjane5p7i3ryPDutVXWdedN/+cMeyoELz3/27lb5kX04NxvwMug3UuZ5eh9JLxJ1VTzdeZrM" +
                    "J43Puv7S53PU+Wrube5gfhvt03bkee+6yvf5bcMiek5US+n9YrmOwyNeJ7wYuDkcNz/s07BJjluSafpl+OoOEH7U+ALjY/X5U42PpxrnarB+M+sPWl9o/WDW" +
                    "nwbURzytTFtlGivvVlfb6E+1fXLbm9V1MSxRYaS5TBb9ORdpXxrclpj7SFgiY4mMJWwfGbaeTwtWivpbV5uv1Yo3feb6Prm9hRa/zV1lszeeo8ztwao3vdI8" +
                    "VQ2e1S2ynqoLz/dYWM08byAcZVqK1lYrzqMHfsLkQU1eYeSgFsYiepN6tujl+jxi4LnNdaramja2Xz9l6/x5oBczdUVcxh7sAYfyJPDtoasrWHjVxwGvt/BF" +
                    "2JsHzF8I+8h1npl/wPzwSzHz6wPUdcqKdz2B+zEfsLAWBrcF+M0CXBjkUhcmqGdUsz/iFPGpvm//qf3VbtFsJIdBTtIA54FmzekUc7FML0h6XHM4wPlRscPO" +
                    "NV+wd4pf+rz06IT1AAXzk1sO0xfcB2r+aV8w0IeHzZ/qC83iXuAMEukFbv3IgC9E6mzPOjjsG2YDCGC9tHtB20K/YOGBS5graSBgLVuKbiAP3AAQEs0ByjGy" +
                    "Pct8mGtWrGa+oOA6b3khCSD/5lhPVN9APzo66F5nDAZ6QSPqTzhHXFe160Tg+WKoKxqyZP0B5C7j0JtDaM8bzPObTvNXu1E1asA/Q/kUEBMa75J4yFx43n8y" +
                    "n5pXNiDQZL8s/jYMAJQrnmhDshrjPt/DAYQwUM/wb3QIGG3QAQqTNtPsqB/zvmwA0NxwlcCH9O9+wPT/Ut7vyO26WlnutKEK4SQRBuIGAPG4eABhoKxv1QE8" +
                    "7TPW1eyNluvEBX3V7df8IlsGP0NZfUB2CLAsL47+9p/4zaXDogOUYDhgRKLEfIy3mVZ+3xyghzdB4AXmB09IhIJMTyhMBLQegrabNxS1X/w8EaSPfpAtdjtJ" +
                    "xRcO0IgDzCRG/66ET4/vO++TZetwOkBZL+V0AE0Lt7zygD7SYtBfmQFA4hD3QhPqIwaK5sO8cR5W1BRm/sbNG0ecBi3MAaBTzvoCra37x1PgSfhq3tstFbX/" +
                    "MnvPb5K/HQnM/u0lQuBc0JHAE3e4cr8T+tsG/czcbQVpAKcWOQ1/gX6kEAEpnYipMZ+RADo20W0124mWIgI8nauMRfdvNO98Ppb0ldUfmoRujv3VuGIi+Hvs" +
                    "B7UcltXgjM1+VPtChn2LJ66xUkBH54dz/fn+FstigY1OEsgRKtAQjgK3jZpKCAVgQpb38YDgxzC823vUN1MAiEiFYZo8MHw7oT9ZgJvBGwOf9RXAOloqwIXc" +
                    "fmN3BGDXNN196Oc70MZy5gjEbom7I3iJtSPAJQVkC0hdrdqGiu2nEAHClgCSp4DAj5IzQcOAvfLLHp7YQrGKIAIWiAbNGeJEfSAPcoGlBXUz4gCcoF3o3koo" +
                    "w68DOJwPImEhI6G4tlZ/RbnAcgCrAt9JAS+QAWeB6gGhLzaolo+D6WAUFF+xO3SPL1OAwA65O28zIImsJ/Pi8J+ngI4iYJhv1OgOoITRoPOEgsSgR6jB3KwE" +
                    "Ep0B0GIIkXYYuFm9QpaGQ8SKd6bpaDY1IFC/QT0oZtLpD7e2mKAYvDeeB65ifMLZA+liIyeIoARAQMWhydWnAyy7T4P8aja4BICOh5vD8+QPDLDiG9Ug8KCS" +
                    "DAjJgHxVFQ5Dvo0OMilErsSFXmcDLCvayi1XVnlhA5F+EYwNuCdgr8le3KWEaNfvDN/iFNGinbm+a9lgVjPM7/CdpQMN/7w5sGRiAmCjkgfgwXixoDjhpDCp" +
                    "NhXgCvQAkV9fGPSHWeFVXeC+MHgqDclWHz7RB56xg70w7K4MnAswUdVFCB9khbSEP08FW2EgjgqWIpQXXrLCcEofXdZJLBOihXWyQzJLiJUl6pIHmp+JBeWE" +
                    "gW4k0yQh0btMkXhAkXBufrKNoirRMr9IftMPnmsDgYQw0wNYGgo1AmH8Csmd0AO8QEDs6/v0HBP0AmXP6MFpgvFMW81MKE/TQ4ZD2Gq2Scct22Ky/JWILWXV" +
                    "iGmVgwmEgSWCM0fd40EtUdmKlwgSPZhB68Dq9K1lsgauQwVplgXqT9VySVIlwdNDNzBRpbedzN8VIomMftxZcm0ZgWTS8EhwBNo/pfoLAeFRfdg+KgX3wHDp" +
                    "EBTGcX2kFLwCCHGszPCKYHinF28NgroSjal1d0rBVSe4aEWbYChXpaATwIvlgmBVwinfD1LGCLUw8eiFBpFO4PLgpf6P/mFm4bnySnUSIicFnfu7yZi+sFwg" +
                    "5XeTw7VRMFlBb0/aBRfJ4J43Zlb4hOJO1bC3z1XDGFw2PDWDdkkOP94sGotzdGhzoJ/MDRGuoHFakCGSFYtZ+wC3AvKYlOQhPTixtPTA9kEFXaRwYFg9WAQu" +
                    "wI80WVcHIBMIpH4qHpXFFYS8oDv1aGQQhpmV9FHAPKSzXg26XXTTJjaoO7gXpNzfrBuwR5PjH/eN+h1trCQJjZHHbqHiWpbVRJi/r45gytfpDvWqJGQovdR8" +
                    "nnGEj2SxXqWjk3Pq+daNuUhHgYEfTCYIZ2Jg2yCahBAWXaxLPYzZFF854zeS5BvLQ0epHRQzDu8CqHw0zAFcb9TD0tHPFpFlG3wJc6dhncXqnYTgQOAGT0Xe" +
                    "tPzLHUMP/cHQd7HfRQNh6egmK7vlsX7Km80f9XmHLUlSUB+QgsYsOddJgSzxCQSwDeG2T6t9lEgBkrUOFALq2VQAY6j+pbUQ22oZV0v01hxqfHnTDC/RCodg" +
                    "CDAfeFH8qKYFFa0h41INBExzON8t5iqVeaKsCrJANchmS8WlpI1H7Rgvw6e3e0cPYj/uJePyAJeNCP7erVtZ3CuzzthPn/eO61U3bqRCJ7GvK2pfqhQ3dtku" +
                    "wnHbC4RA8VDMwOdcwNKNqRxTN2pn8MM5qtqtn5VgY3TGw1oKSN15WDFRWPsrQzgJwThWCynaGYW9wYIe0aBumDJlhWjuFcAKc1Pzu9VT7b+8a/BBNb5Xi9Hj" +
                    "YbAGf9au+Xq/Tz63/gMiYOUhVcit7fsZA2h+/pb06atkEixXA+P/Rmlwp4PlIiPq1tw1dtlQkcIMb0xAtYJEA6u7mMcmdotR8CWA90yeyZuHVuSaQKCFRTr6" +
                    "2Sk23MeFmCgR+qAAYLXzGxjf0b//z5qHd+B/n/bZJuCjRtqHtlM/S/97ZfiwhYic6Xn7dRZY9kbEZVboYf6PF7nIGsmoCMLZOqqLI6gH5CURDG/iFy/xo9cF" +
                    "hdEu1mIokSxhGL5PCKj0ga0NCDTo3jGKy694lewqQXM+iQuoI2RVCZYDpLebR3sP4bOxMacB7B0Er+QGH7gPDl3Sf77OjSyt2IfO5NI98hGEB/C/Nw0+jg95" +
                    "35HDTCZlUqVYAJBX3Zd9euBWEHL4ly/szxDAE4DPiGSjfGKjXjJYzlOVtFxdUNfeolWJWmxM7Ja0+seoNozXJ/eboqTGBCgTp8WAINCL8ioy5wYL7E+z5zfE" +
                    "gfS5Wlgf95A+UQtxgfKIBn6hD8blBpQDsnxdAjxwg27LsYucH7CAXQ2Qc4zofOuGXAmiXgRju0hg6W5TJKDuFf+HCbPXda4B5mnKRQIai4nVQ7SK0oVAioIu" +
                    "HVApDGvKSEdWkjKBZfn8tibwsY34QTBOVuuZTCiUCVkJuEBP0BUm4CfJABe7AEO6DhRZSqA/PEsG1yaidY/0TOsme4PTNpisF8WBJJG7QRKhdqx4gEbwUVwf" +
                    "yuuAHQ0KYxcqAmYH9UW2OdIolrnZcy4DUZxdEAgKB4lCIcYYcVbnfOgwsmlzo0ZZjB6k4JSQcwc6RzIUCpb58y+dKEqP/eBTnVDOieJ7hvhsomgfKRy7Qtjv" +
                    "R8qetZAqcfR+sjBe64s7nqj7kYtOKIsIrHZy4L87RDhbSMPCsh55tQryKfgqMVCSkwP1gkKRaH7hipDbdA0ohnOMyBAi+8ecjyRVwCSSCoTAgrp84OergviS" +
                    "D9Q7228EsdtowYN24Uua8Dk09KwGfGZq1oCUqcelw3BOIW7twsSQj+cQSfQuYWUD6RLzakbdqYlxNkmISB02MY7Zt5tNi6HAHwzUCW6K/mXBf2cZMDCCaDhv" +
                    "4+biM8QoElPi/VIhxOjogK1p4pLz/3+C8BLv+QkFqK/F+ycThD/aGDBfOicIPxQDe6Nwz/0PJgjjOUF4HR9ZmTmd0xxxdXsHBL1mEZ1XmIoPEZZzjthHzhIh" +
                    "X7zFGGV9wsoA0yIUnhIPUQqQdX5keUB5QweWZ+MDj7WgB40A2C1wcOTJ3w+ckf9gdGg4bdsDNz2wvfAJFERLRV++WwV9X0h+HCHbpwS8QXhrZ7M4HuT/N/lA" +
                    "/iri1ar3ZCifbad6HgDfSJSGf6UvKC5oBUD1V8HKunxSfAw5nsJShCdXNoOSWCygwpRiCjBMTksPkZTazK21l9xa6Rqpv3xi4ItR4vz8Lwmkb7Pk7RU14K5B" +
                    "+DOTxBsCsD+R9r9M+PgXBXsx8HiUeP+LgnOUmNVAJTM3JjD2wt4+ysP/CCicY2XbH5VEp3R3w8Rx+yMDHyc9x4nFUUj01nWc9L/txJkBTjYAAA=="; break;
                default: throw new ArgumentException("Unknown bundled AV1 preset: "+name);
            }
            using(var input=new MemoryStream(Convert.FromBase64String(encoded)))
            using(var zip=new System.IO.Compression.GZipStream(input,System.IO.Compression.CompressionMode.Decompress))
            using(var reader=new StreamReader(zip,System.Text.Encoding.UTF8))return reader.ReadToEnd();
        }
        internal static void AttachPresetScale(BridgeExecutionStage stage,List<string> arguments,string scaledTable,int percent,IList<string> temporary)
        {
            if(percent==100)return;
            int index=arguments.IndexOf("--preset");
            if(index<0||index+1>=arguments.Count)throw new InvalidDataException("Preset argument missing.");
            string preset=arguments[index+1];
            // Resolve now, write only when the stage runs; never inspect the video model.
            string original=PresetTable(preset);
            temporary.Add(scaledTable);
            stage.BeforeRunPreparation=delegate {
                File.WriteAllText(scaledTable,ScaleTable(original,percent),new System.Text.UTF8Encoding(false));
                arguments[index]="--grain";arguments[index+1]=scaledTable;
                stage.DirectArguments=Command(arguments.ToArray());
                return "[Grain strength] preset "+preset+" scaled before single apply; strength="+percent.ToString(CultureInfo.InvariantCulture)+"%; complete v0.2.2 model; no model inspect or second video rewrite.";
            };
        }

        /* Embedded preset data license:
MIT License

Copyright (c) 2022 Josh Holmer

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
        */
        internal static List<BridgeExecutionStage> Stages(string tool, string cwd, Func<string> input, string output, int percent, IList<string> temporary)
        {
            string original = output + ".original.tbl", scaled = output + ".scaled.tbl";
            temporary.Add(output); temporary.Add(original); temporary.Add(scaled);
            var inspect = new BridgeExecutionStage { ToolPath = tool, WorkingDirectory = cwd, StageText = "读取原颗粒模型 / Read original grain model" };
            inspect.BeforeRunPreparation = delegate { inspect.DirectArguments = Command("inspect", input(), "-o", original, "-y"); return "[Grain strength] model source: " + input(); };
            var apply = new BridgeExecutionStage { ToolPath = tool, WorkingDirectory = cwd, StageText = "颗粒强度 / Grain strength " + percent.ToString(CultureInfo.InvariantCulture) + "%" };
            apply.BeforeRunPreparation = delegate {
                File.WriteAllText(scaled, ScaleTable(File.ReadAllText(original), percent), new System.Text.UTF8Encoding(false));
                apply.DirectArguments = Command("apply", input(), "-o", output, "--grain", scaled, "--replace", "-y");
                return "[Grain strength] " + percent.ToString(CultureInfo.InvariantCulture) + "% of selected model; timestamps, seeds and active AR coefficients preserved; inactive chroma zero coefficients padded for table parser compatibility.";
            };
            return new List<BridgeExecutionStage> { inspect, apply };
        }
        internal static void NumberStages(IList<BridgeExecutionStage> stages)
        { for (int i=0;i<stages.Count;i++) { stages[i].StageCurrent=i+1; stages[i].StageTotal=stages.Count; } }
    }

    internal static class GrainControlCore
    {
        internal static int SnapFive(int value) { return Math.Max(15,Math.Min(100,((value+2)/5)*5)); }
        internal static readonly string[] FgsimPresets={"LIGHT","NORMAL","MEDIUM","HEAVY"};
        internal static readonly string[] FgsimStrengths={"0.10","0.15","0.20","0.30"};
        internal static bool IsFgsimPreset(string preset){return Array.IndexOf(FgsimPresets,preset)>=0;}
        internal static string FgsimStrength(string preset){int i=Array.IndexOf(FgsimPresets,preset);if(i<0)throw new ArgumentException("Unsupported FGSIM preset");return FgsimStrengths[i];}
        internal static string FgsimTag(string preset){return preset=="LIGHT"?"L":preset=="NORMAL"?"N":preset=="HEAVY"?"H":"M";}
        internal static int PlatePercent(IDictionary<string,string> state)
        {
            string value;int percent;
            if(state.TryGetValue("FG_GRAIN_PLATE_STRENGTH",out value)) {
                if(!int.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out percent)||percent<10||percent>100)throw new ArgumentException("Grain Plate strength must be 10..100");
                return percent;
            }
            // Legacy direct requests retain their original four-level meaning.
            int selected=3;if(state.TryGetValue("FG_HEVC_STRENGTH_SEL",out value))int.TryParse(value,out selected);
            return selected==1?65:selected==2?75:selected==4?100:85;
        }
    }

    // Strength is the pixel delta between the existing selected model and its clean route.
    // At 100% the validated graph is returned without adding branches or conversions.
    internal static class PixelGrainStrengthCore
    {
        internal static int Read(IDictionary<string,string> state)
        {
            // Retired relative pixel strength: old saved values must never add a blend branch.
            return 100;
        }
        // Parse link labels only outside quoted/escaped filter values. Brackets in filenames are not links.
        private static string Rename(string graph,string prefix,string input,string output)
        {
            var result=new System.Text.StringBuilder();bool quoted=false;int sources=0;
            for(int i=0;i<graph.Length;i++)
            {
                char c=graph[i];
                if(c=='\\'&&i+1<graph.Length){result.Append(c);result.Append(graph[++i]);continue;}
                if(c=='\''){quoted=!quoted;result.Append(c);continue;}
                if(c=='['&&!quoted)
                {
                    int end=graph.IndexOf(']',i+1);if(end<0)throw new InvalidDataException("Unclosed filter link.");
                    string label=graph.Substring(i+1,end-i-1);
                    if(label=="0:v:0"){result.Append("["+input+"]");sources++;}
                    else if(label=="vout")result.Append("["+output+"]");
                    else if(label.IndexOf(':')>=0)result.Append("["+label+"]");
                    else result.Append("["+prefix+label+"]");
                    i=end;
                }
                else result.Append(c);
            }
            if(sources!=1)throw new InvalidDataException("Expected one primary source in grain filter graph.");
            return result.ToString();
        }
        internal static string Pair(string full,string clean)
        {
            if(string.IsNullOrEmpty(full)||string.IsNullOrEmpty(clean))throw new InvalidDataException("Missing grain comparison graph.");
            return "[0:v:0]split=2[gs_fullin][gs_cleanin];"+Rename(full,"gsf_","gs_fullin","gs_full")+";"+Rename(clean,"gsc_","gs_cleanin","gs_clean");
        }
        internal static string Wrap(string full,string clean,int percent)
        {
            if(percent<1||percent>100)throw new ArgumentOutOfRangeException("percent");
            if(percent==100)return full;
            // Binary-exact quarter weights: identical integer result to the expression,
            // while FFmpeg can use its native normal-blend implementation.
            // Other percentages retain the expression and its established rounding.
            if(percent==25 || percent==50 || percent==75)
                return Pair(full,clean)+";[gs_clean][gs_full]blend=all_mode=normal:all_opacity="+
                    (1.0-percent/100.0).ToString("0.00",CultureInfo.InvariantCulture)+":shortest=1[vout]";
            return Pair(full,clean)+";[gs_clean][gs_full]blend=all_expr='A+(B-A)*"+(percent/100.0).ToString("0.00",CultureInfo.InvariantCulture)+"':shortest=1[vout]";
        }
        private static string ColorValue(string value,string fallback)
        {return string.IsNullOrWhiteSpace(value)||value=="unknown"||value=="reserved"?fallback:value;}
        internal static void StampPreviewColor(List<string> args,bool hdr,MediaProbeInfo media)
        {
            bool converted=media.IsHdr && !hdr;
            string primaries=converted?"bt709":ColorValue(media.ColorPrimaries,hdr?"bt2020":"bt709");
            string transfer=converted?"bt709":ColorValue(media.ColorTransfer,hdr?"smpte2084":"bt709");
            string matrix=converted?"bt709":ColorValue(media.ColorSpace,hdr?"bt2020nc":"bt709");
            args.InsertRange(args.Count-1,new[]{"-color_primaries",primaries,"-color_trc",transfer,"-colorspace",matrix,"-color_range",!converted&&media.ColorRange=="pc"?"pc":"tv"});
        }
        internal static List<string> FrameArguments(string input,int percent,string displayFilter,string output)
        {
            if(percent<0||percent>100)throw new ArgumentOutOfRangeException("percent");
            var args=new List<string>{"-v","error","-y","-i",input};
            if(percent==0||percent==100)args.AddRange(new[]{"-map",percent==0?"0:v:0":"0:v:1","-vf",displayFilter});
            else args.AddRange(new[]{"-filter_complex","[0:v:0][0:v:1]blend="+((percent==25||percent==50||percent==75)?"all_mode=normal:all_opacity="+(1.0-percent/100.0).ToString("0.00",CultureInfo.InvariantCulture):"all_expr='A+(B-A)*"+(percent/100.0).ToString("0.00",CultureInfo.InvariantCulture)+"'")+":shortest=1,"+displayFilter+"[preview]","-map","[preview]"});
            args.AddRange(new[]{"-fps_mode","passthrough","-frames:v","8","-threads","1","-pix_fmt","rgb24",output});return args;
        }
        internal static List<string> PreviewArguments(IList<string> encode,string full,string clean,string output)
        {
            int index=encode.IndexOf("-filter_complex");
            if(index<0)throw new InvalidDataException("No native grain filter graph in preview plan.");
            var result=new List<string>();for(int i=0;i<index;i++)result.Add(encode[i]);
            result.AddRange(new[]{"-filter_complex",Pair(full,clean),"-map","[gs_clean]","-map","[gs_full]","-an","-sn","-dn","-c:v","ffv1","-level","3","-threads","2","-frames:v:0","32","-frames:v:1","32","-t","8","-fps_mode","passthrough","-y",output});
            return result;
        }
    }
}
