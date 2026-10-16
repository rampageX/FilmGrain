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
            return result;
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
                return "[Grain strength] " + percent.ToString(CultureInfo.InvariantCulture) + "% of selected model; timestamps, seeds and AR coefficients preserved.";
            };
            return new List<BridgeExecutionStage> { inspect, apply };
        }
        internal static void NumberStages(IList<BridgeExecutionStage> stages)
        { for (int i=0;i<stages.Count;i++) { stages[i].StageCurrent=i+1; stages[i].StageTotal=stages.Count; } }
    }

    // Strength is the pixel delta between the existing selected model and its clean route.
    // At 100% the validated graph is returned without adding branches or conversions.
    internal static class PixelGrainStrengthCore
    {
        internal static int Read(IDictionary<string,string> state)
        {
            string value;int percent;
            if(state==null||!state.TryGetValue("FG_PIXEL_GRAIN_STRENGTH",out value)||string.IsNullOrEmpty(value))return 100;
            if(!int.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out percent)||percent<1||percent>100)
                throw new ArgumentException("Pixel grain strength must be 1..100 percent.");
            return percent;
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
            else args.AddRange(new[]{"-filter_complex","[0:v:0][0:v:1]blend=all_expr='A+(B-A)*"+(percent/100.0).ToString("0.00",CultureInfo.InvariantCulture)+"':shortest=1,"+displayFilter+"[preview]","-map","[preview]"});
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
