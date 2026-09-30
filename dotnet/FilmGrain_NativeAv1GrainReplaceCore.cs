using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FilmGrainStudioPreview
{
    // AV1-specific stream-copy grain replacement. Video packets are never encoded.
    internal static class NativeAv1GrainReplaceCore
    {
        internal static bool TryPrepare(string appRoot, string input, MediaProbeInfo info,
            IDictionary<string, string> state, out BridgePreparedExecution prepared, out string reason)
        {
            prepared = null;
            reason = "";
            if (state == null || info == null) { reason = "missing state/media probe"; return false; }
            if (!File.Exists(input) || !string.Equals(info.VideoCodec, "av1", StringComparison.OrdinalIgnoreCase))
            { reason = "no-reencode input is not a confirmed AV1 file"; return false; }
            if (string.IsNullOrWhiteSpace(Get(state, "FG_AV1_SOURCE_GRAIN_ACTION")) ||
                (!Eq(state, "FG_AV1_SOURCE_GRAIN_ACTION", "ADDED") && !Eq(state, "FG_AV1_SOURCE_GRAIN_ACTION", "REPLACED")))
            { reason = "AV1 source grain inspection is not ready"; return false; }

            string mode = Get(state, "FG_AV1_GRAIN_MODE").ToUpperInvariant();
            List<string> grainArgs = new List<string>();
            string grainTag;
            if (!TryBuildGrainArgs(mode, state, grainArgs, out grainTag, out reason)) return false;
            bool mkv = Eq(state, "FG_CONTAINER", "MKV");
            if (!mkv && !Eq(state, "FG_CONTAINER", "MP4")) { reason = "unsupported AV1 no-reencode container"; return false; }
            string extension = mkv ? ".mkv" : ".mp4";
            string outputDirectory = ResolveOutputDirectory(input, state);
            if (string.IsNullOrWhiteSpace(outputDirectory)) { reason = "output directory is empty"; return false; }
            try { Directory.CreateDirectory(outputDirectory); }
            catch (Exception ex) { reason = "cannot create output directory: " + ex.Message; return false; }

            string sourceAction = Get(state, "FG_AV1_SOURCE_GRAIN_ACTION").ToUpperInvariant();
            string outputName = BuildOutputName(Path.GetFileNameWithoutExtension(input), grainTag, sourceAction, extension);
            string output = Path.Combine(outputDirectory, outputName);
            if (File.Exists(output)) { reason = "native AV1 no-reencode output already exists: " + output; return false; }

            string ffmpeg = Path.Combine(ReadConfigValue(appRoot, "FFMPEG_DIR"), "ffmpeg.exe");
            string grav1synth = ReadConfigValue(appRoot, "GRAV1SYNTH");
            if (!File.Exists(ffmpeg)) { reason = "ffmpeg.exe not found: " + ffmpeg; return false; }
            if (!File.Exists(grav1synth)) { reason = "grav1synth.exe not found: " + grav1synth; return false; }

            string tempRoot;
            if (!TryResolveTempRoot(input, state, out tempRoot, out reason)) return false;
            string job = "__FGS_AV1_COPY_" + Guid.NewGuid().ToString("N");
            string baseIvf = Path.Combine(tempRoot, job + "_base.ivf");
            string grainIvf = Path.Combine(tempRoot, job + "_grain.ivf");
            string verifyTable = Path.Combine(tempRoot, job + "_verify.txt");

            List<string> extract = new List<string> { "-hide_banner", "-stats", "-progress", "pipe:2", "-stats_period", "0.5", "-y", "-i", input, "-map", "0:v:0", "-c:v", "copy", "-f", "ivf", baseIvf };
            List<string> apply = new List<string> { "apply", baseIvf, "-o", grainIvf };
            apply.AddRange(grainArgs);
            apply.AddRange(new string[] { "--replace", "-y" });

            List<string> remux = new List<string> { "-hide_banner", "-stats", "-progress", "pipe:2", "-stats_period", "0.5", "-y", "-i", grainIvf, "-i", input, "-map", "0:v:0", "-map", "1:a?" };
            if (mkv) remux.AddRange(new string[] { "-map", "1:s?", "-map", "1:t?", "-map", "1:d?" });
            remux.AddRange(new string[] { "-map_metadata", "1", "-map_chapters", "1", "-c:v", "copy" });
            if (mkv) remux.AddRange(new string[] { "-c", "copy" });
            else remux.AddRange(new string[] { "-c:a", "aac", "-b:a", "256k", "-movflags", "+faststart" });
            remux.Add(output);

            List<string> inspect = new List<string> { "inspect", output, "-o", verifyTable, "-y" };
            double duration = ParseDuration(info.Duration);
            BridgeExecutionRequest request = new BridgeExecutionRequest();
            request.ToolPath = ffmpeg;
            request.WorkingDirectory = tempRoot;
            request.InputFiles = new List<string> { input };
            request.Environment = new Dictionary<string, string>(state, StringComparer.OrdinalIgnoreCase);
            request.DirectProcess = true;
            request.OutputFiles = new List<string> { output };
            request.TemporaryFiles = new List<string> { baseIvf, grainIvf, verifyTable };
            request.ProgressDurationSeconds = duration;
            request.DirectStages = new List<BridgeExecutionStage>
            {
                Stage(ffmpeg, tempRoot, extract, duration, 1, 4, "AV1 视频流复制到 IVF"),
                Stage(grav1synth, tempRoot, apply, duration, 2, 4, "grav1synth 添加/替换颗粒元数据"),
                Stage(ffmpeg, tempRoot, remux, duration, 3, 4, "保留源流并封装输出"),
                Stage(grav1synth, tempRoot, inspect, duration, 4, 4, "检查输出 AV1 颗粒元数据")
            };
            prepared = new BridgePreparedExecution(request, true, ffmpeg, new List<string> { input });
            return true;
        }

        private static bool TryBuildGrainArgs(string mode, IDictionary<string, string> state,
            List<string> grainArgs, out string tag, out string reason)
        {
            tag = "";
            reason = "";
            if (mode == "PRESET")
            {
                string[] formats = { "Classic35", "Modern35", "16mm", "Super8", "MaxMid" };
                string[] stocks = { "", "-1", "-2", "-3" };
                int f, s;
                if (!int.TryParse(Get(state, "FG_AV1_FORMAT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out f) || f < 1 || f > formats.Length)
                { reason = "invalid AV1 film format"; return false; }
                if (!int.TryParse(Get(state, "FG_AV1_STOCK"), NumberStyles.Integer, CultureInfo.InvariantCulture, out s) || s < 1 || s > stocks.Length)
                { reason = "invalid AV1 film stock"; return false; }
                string preset = formats[f - 1] + (f <= 3 ? stocks[s - 1] : "");
                grainArgs.AddRange(new string[] { "--preset", preset });
                tag = preset.Replace('-', '_');
                return true;
            }
            if (mode == "ISO")
            {
                int iso;
                if (!int.TryParse(Get(state, "FG_AV1_ISO"), NumberStyles.Integer, CultureInfo.InvariantCulture, out iso) || iso <= 0)
                { reason = "invalid AV1 Photon ISO"; return false; }
                grainArgs.AddRange(new string[] { "--iso", iso.ToString(CultureInfo.InvariantCulture) });
                if (Get(state, "FG_AV1_CHROMA") == "1") grainArgs.Add("--chroma");
                tag = "ISO" + iso.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            if (mode == "TABLE")
            {
                string table = Get(state, "FG_AV1_GRAIN_TABLE");
                if (string.IsNullOrWhiteSpace(table) || !File.Exists(table)) { reason = "selected AV1 Grain Table is missing: " + table; return false; }
                string stem = Regex.Replace(Path.GetFileNameWithoutExtension(table) ?? "TABLE", "[^A-Za-z0-9]+", "_").Trim('_');
                if (stem.Length == 0) stem = "TABLE";
                if (stem.Length > 48) stem = stem.Substring(0, 48);
                grainArgs.AddRange(new string[] { "--grain", table });
                tag = "TABLE_" + stem;
                return true;
            }
            reason = "unsupported AV1 no-reencode grain mode";
            return false;
        }

        private static string BuildOutputName(string baseName, string grainTag, string sourceAction, string extension)
        {
            string cleanBase = Regex.Replace(baseName, "(?:_AV1FG_.+?_(?:ADDED|REPLACED))+$", "", RegexOptions.IgnoreCase);
            if (string.IsNullOrWhiteSpace(cleanBase)) cleanBase = baseName;
            Match studio = Regex.Match(cleanBase, "^(?<root>.+)_AV1GS_.+_(?<speed>STD|FAST|UHQ)_(?<tail>\\d+k.*)$", RegexOptions.IgnoreCase);
            Match current = Regex.Match(cleanBase, "^(?<prefix>.+_AV1_(?:STD|FAST|UHQ)_\\d+k.*?)_GS_(?<grain>.+?)(?<tail>_LUT_.*|_SDR_.*|_SUB)?(?:_(?:ADDED|REPLACED))?$", RegexOptions.IgnoreCase);
            if (current.Success)
            {
                string tail = Regex.Replace(current.Groups["tail"].Value, "(?:_(?:ADDED|REPLACED))+$", "", RegexOptions.IgnoreCase);
                return current.Groups["prefix"].Value + "_GS_" + grainTag + tail + "_" + sourceAction + extension;
            }
            if (studio.Success)
            {
                string tail = Regex.Replace(studio.Groups["tail"].Value, "_(?:ADDED|REPLACED)$", "", RegexOptions.IgnoreCase);
                return studio.Groups["root"].Value + "_AV1GS_" + grainTag + "_" + studio.Groups["speed"].Value.ToUpperInvariant() + "_" + tail + "_" + sourceAction + extension;
            }
            return cleanBase + "_AV1FG_" + grainTag + "_" + sourceAction + extension;
        }

        private static BridgeExecutionStage Stage(string tool, string cwd, IList<string> args, double duration, int current, int total, string text)
        {
            StringBuilder command = new StringBuilder();
            for (int i = 0; i < args.Count; i++) { if (i > 0) command.Append(' '); command.Append(Quote(args[i])); }
            return new BridgeExecutionStage { ToolPath = tool, WorkingDirectory = cwd, DirectArguments = command.ToString(), ProgressDurationSeconds = duration, StageCurrent = current, StageTotal = total, StageText = text };
        }

        private static string Quote(string value)
        {
            StringBuilder result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value ?? "")
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '\"') { result.Append('\\', slashes * 2 + 1); result.Append('\"'); slashes = 0; continue; }
                result.Append('\\', slashes); slashes = 0; result.Append(c);
            }
            result.Append('\\', slashes * 2); result.Append('\"');
            return result.ToString();
        }

        private static bool TryResolveTempRoot(string input, IDictionary<string, string> state, out string root, out string reason)
        {
            reason = ""; root = Path.GetDirectoryName(input);
            string mode = Get(state, "FG_TEMP_MODE").ToUpperInvariant();
            if (mode == "CUSTOM")
            {
                string custom = Get(state, "FG_TEMP_CUSTOM_DIR");
                if (string.IsNullOrWhiteSpace(custom)) { reason = "custom temporary directory is not configured"; return false; }
                root = Path.Combine(custom, "FilmGrain_Studio");
            }
            else if (mode == "SYSTEM") root = Path.Combine(Path.GetTempPath(), "FilmGrain_Studio");
            if (string.IsNullOrWhiteSpace(root)) { reason = "temporary directory is empty"; return false; }
            try { Directory.CreateDirectory(root); }
            catch (Exception ex) { reason = "cannot create temporary directory: " + ex.Message; return false; }
            return true;
        }

        private static string ResolveOutputDirectory(string input, IDictionary<string, string> state)
        {
            string mode = Get(state, "FG_OUTPUT_MODE").ToUpperInvariant();
            if (mode == "CUSTOM")
            {
                string custom = Get(state, "FG_OUTPUT_CUSTOM_DIR");
                if (string.IsNullOrWhiteSpace(custom)) return "";
                return custom;
            }
            return Path.GetDirectoryName(input);
        }

        private static string ReadConfigValue(string appRoot, string key)
        {
            string config = Path.Combine(appRoot ?? "", "FilmGrain_Config.ini");
            if (!File.Exists(config)) config = Path.Combine(appRoot ?? "", "FilmGrain_Config.default.ini");
            if (!File.Exists(config)) return "";
            foreach (string line in File.ReadAllLines(config))
            {
                int equals = line.IndexOf('=');
                if (equals > 0 && string.Equals(line.Substring(0, equals).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(equals + 1).Trim().Trim('"');
            }
            return "";
        }

        private static string Get(IDictionary<string, string> state, string key) { string value; return state != null && state.TryGetValue(key, out value) ? value ?? "" : ""; }
        private static bool Eq(IDictionary<string, string> state, string key, string value) { return string.Equals(Get(state, key), value, StringComparison.OrdinalIgnoreCase); }
        private static double ParseDuration(string value) { double result; return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && result > 0.0 ? result : 0.0; }
    }
}
