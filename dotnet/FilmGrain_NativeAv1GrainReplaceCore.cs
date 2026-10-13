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
        private static readonly object isoCacheSync = new object();
        private static readonly HashSet<string> isoContextCache = new HashSet<string>(StringComparer.Ordinal);
        internal static bool TryPrepare(string appRoot, string input, MediaProbeInfo info,
            IDictionary<string, string> state, out BridgePreparedExecution prepared, out string reason)
        {
            prepared = null;
            reason = "";
            if (state == null || info == null) { reason = "missing state/media probe"; return false; }
            if (!SharedGrainCore.IsEnabled(state)) { reason = "AV1 不重编码颗粒模式必须启用颗粒。 / Grain must be enabled in AV1 no-reencode mode."; return false; }
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
            string directReason;
            bool eligible = CanUseDirectInput(mode, info, state, out directReason);
            bool fastEnabled = !string.Equals(ReadConfigValue(appRoot, "AV1_COPY_FAST"), "false", StringComparison.OrdinalIgnoreCase);
            bool fast = fastEnabled && eligible;
            if (!fastEnabled) directReason = "你已关闭快速处理，按原来的方式运行 / automatic fast path disabled";
            string ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg), "ffprobe.exe");
            string isoCacheKey = mode == "ISO" ? BuildIsoCacheKey(input, ffmpeg, ffprobe, info) : "";
            bool cachedIso = false;
            if (isoCacheKey.Length > 0) lock (isoCacheSync) { cachedIso = isoContextCache.Contains(isoCacheKey); }
            bool isoGuard = fast && mode == "ISO" && !cachedIso;
            if (fast && mode == "ISO" && !File.Exists(ffprobe)) { fast = false; isoGuard = false; directReason = "缺少 FFprobe，无法核对 ISO 色彩条件，保留原流程 / ISO context probe unavailable"; }
            string job = "__FGS_AV1_COPY_" + Guid.NewGuid().ToString("N");
            string baseIvf = Path.Combine(tempRoot, job + "_base.ivf");
            string grainIvf = Path.Combine(tempRoot, job + (fast ? ((info.FormatName ?? "").IndexOf("mov", StringComparison.OrdinalIgnoreCase) >= 0 ? "_grain.mp4" : "_grain.mkv") : "_grain.ivf"));
            string verifyTable = Path.Combine(tempRoot, job + "_verify.txt");

            List<string> extract = new List<string> { "-hide_banner", "-stats", "-progress", "pipe:2", "-stats_period", "0.5", "-y", "-i", input, "-map", "0:v:0", "-c:v", "copy", "-f", "ivf", baseIvf };
            List<string> apply = new List<string> { "apply", fast ? input : baseIvf, "-o", grainIvf };
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
            if (isoGuard) request.TemporaryFiles.Add(Path.Combine(tempRoot, job + "_grain.ivf"));
            request.LogStageTimings = true;
            request.ProgressDurationSeconds = duration;
            bool fullVerify = mode == "TABLE" && !IsStaticTable(info, state) || string.Equals(ReadConfigValue(appRoot, "AV1_COPY_FULL_VERIFY"), "true", StringComparison.OrdinalIgnoreCase);
            request.ExecutionNotice = isoGuard ? "[AV1 Copy] ISO: 核对原 IVF 色彩条件后选择处理路径 / verifying original IVF color context"
                : fast ? (cachedIso ? "[AV1 Copy] fast: ISO 已核对的色彩条件仍有效，省略重复核对 / cached ISO context valid; direct input" : "[AV1 Copy] fast: direct container input; identical grain arguments; original stream mapping retained.")
                : "[AV1 Copy] compatibility: " + directReason + ". 保留原处理流程 / using original processing route.";
            request.DirectStages = new List<BridgeExecutionStage>();
            int total = (isoGuard ? 4 : fast ? 2 : 3) + (fullVerify ? 1 : 0);
            int step = 0;
            BridgeExecutionStage fullExtract = null;
            if (isoGuard)
            {
                var probeExtract = new List<string>(extract);
                probeExtract.InsertRange(probeExtract.Count - 1, new string[] { "-frames:v", "4" });
                request.DirectStages.Add(Stage(ffmpeg, tempRoot, probeExtract, duration, ++step, total, "快速核对 ISO 色彩条件（仅开头四个包）"));
                fullExtract = Stage(ffmpeg, tempRoot, extract, duration, ++step, total, "提取原视频（不重新编码）");
                request.DirectStages.Add(fullExtract);
            }
            else if (!fast) request.DirectStages.Add(Stage(ffmpeg, tempRoot, extract, duration, ++step, total, "提取原视频（不重新编码）"));
            var applyStage = Stage(grav1synth, tempRoot, apply, duration, ++step, total, "添加或更换颗粒效果");
            var remuxStage = Stage(ffmpeg, tempRoot, remux, duration, ++step, total, "保留源流并封装输出");
            request.DirectStages.Add(applyStage); request.DirectStages.Add(remuxStage);
            if (fullVerify) request.DirectStages.Add(Stage(grav1synth, tempRoot, inspect, duration, ++step, total, "完整检查输出 AV1 颗粒元数据"));
            if (isoGuard)
            {
                fullExtract.ShouldRun = delegate { return !fast; };
                fullExtract.BeforeRunPreparation = delegate
                {
                    string contextReason;
                    fast = IsoContextMatches(ffprobe, baseIvf, info, out contextReason);
                    if (!fast)
                    {
                        grainIvf = Path.Combine(tempRoot, job + "_grain.ivf");
                        apply[1] = baseIvf; apply[3] = grainIvf;
                        remux[remux.IndexOf("-i") + 1] = grainIvf;
                        applyStage.DirectArguments = Stage(grav1synth, tempRoot, apply, duration, 1, 1, "").DirectArguments;
                        remuxStage.DirectArguments = Stage(ffmpeg, tempRoot, remux, duration, 1, 1, "").DirectArguments;
                    }
                    else
                    {
                        if (isoCacheKey.Length > 0 && isoCacheKey == BuildIsoCacheKey(input, ffmpeg, ffprobe, info))
                            lock (isoCacheSync) { if (isoContextCache.Count >= 64) isoContextCache.Clear(); isoContextCache.Add(isoCacheKey); }
                        int n = 0, count = request.DirectStages.Count - 1;
                        foreach (var item in request.DirectStages) if (!object.ReferenceEquals(item, fullExtract)) { item.StageCurrent = ++n; item.StageTotal = count; }
                    }
                    return fast ? "[AV1 Copy] fast: ISO 的尺寸、传递函数及色彩范围与原 IVF 一致，直接处理 / ISO context matches original IVF; direct input"
                        : "[AV1 Copy] compatibility: " + contextReason + ". 保留原处理流程 / using original processing route.";
                };
            }
            request.PostRunValidation = delegate
            {
                if (!File.Exists(output) || new FileInfo(output).Length == 0) return "AV1 output is missing or empty.";
                if (fullVerify)
                {
                    if (!File.Exists(verifyTable) || new FileInfo(verifyTable).Length == 0) return "Output grain table is missing.";
                    return "";
                }
                return ValidateGrainHeader(ffmpeg, output);
            };
            prepared = new BridgePreparedExecution(request, true, ffmpeg, new List<string> { input });
            return true;
        }

        internal static bool TryBuildGrainArgs(string mode, IDictionary<string, string> state,
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

        // Regular zero-origin MP4 permits direct preset timing. ISO also verifies actual IVF color context;
        // tables still require a single full-duration event.
        internal static bool CanUseDirectInput(string mode, MediaProbeInfo info, IDictionary<string, string> state)
        {
            string reason;
            return CanUseDirectInput(mode, info, state, out reason);
        }

        internal static bool CanUseDirectInput(string mode, MediaProbeInfo info, IDictionary<string, string> state, out string reason)
        {
            reason = "";
            if (mode != "TABLE" && mode != "PRESET" && mode != "ISO") { reason = "当前选项需要按原来的方式处理 / unsupported direct grain mode"; return false; }
            string format = info.FormatName ?? "";
            if (format.IndexOf("mov", StringComparison.OrdinalIgnoreCase) < 0
                || !string.Equals(Path.GetExtension(info.PathValue), ".mp4", StringComparison.OrdinalIgnoreCase))
            { reason = "这个视频不是 MP4，按原来的方式处理 / direct path requires MP4 input"; return false; }
            double start;
            if (!double.TryParse(info.VideoStartTime, NumberStyles.Float, CultureInfo.InvariantCulture, out start) || double.IsNaN(start) || double.IsInfinity(start))
            { reason = "无法确认视频的起点，按原来的方式处理，以免影响音画同步 / unknown video start_time"; return false; }
            if (Math.Abs(start) > 0.000001)
            { reason = "视频并非从零开始，按原来的方式处理，以免影响音画同步 / nonzero video start_time=" + info.VideoStartTime; return false; }
            if (mode == "TABLE" && !IsStaticTable(info, state))
            { reason = "这份颗粒文件不符合整段使用同一组参数的条件，按原来的方式处理 / table must be one enabled event covering the entire video"; return false; }
            if (!MovFieldOrderCore.HasRegularTimeline(info.PathValue, info.AvgFrameRate))
            { reason = "这个视频的帧时间安排不符合快速处理条件，按原来的方式处理，以免改变画面或音画同步 / MP4 timeline requires original processing route"; return false; }
            return true;
        }

        internal static bool IsStaticTable(MediaProbeInfo info, IDictionary<string, string> state)
        {
            try
            {
                int events = 0;
                foreach (string raw in File.ReadLines(Get(state, "FG_AV1_GRAIN_TABLE")))
                {
                    string line = raw.Trim();
                    if (!line.StartsWith("E ", StringComparison.Ordinal)) continue;
                    string[] fields = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    ulong begin, end;
                    if (fields.Length < 3 || !ulong.TryParse(fields[1], out begin) || !ulong.TryParse(fields[2], out end)
                        || fields.Length < 6 || fields[3] != "1" || fields[5] != "1"
                        || begin != 0 || ParseDuration(info.Duration) <= 0 || end < ParseDuration(info.Duration) * 10000000.0) return false;
                    events++;
                }
                return events == 1;
            }
            catch { return false; }
        }

        // ISO generation uses width/height, PQ-vs-SDR transfer and full-vs-limited range.
        // Compare actual demuxed IVF context instead of assuming container tags are identical.
        // Session-only cache, bounded and invalidated by file/tool identity and probed context.
        // No settings or source files are modified; restarting FGS performs the check again.
        internal static string BuildIsoCacheKey(string input, string ffmpeg, string ffprobe, MediaProbeInfo info)
        {
            try
            {
                var key = new StringBuilder();
                foreach (string path in new string[] { input, ffmpeg, ffprobe })
                {
                    var file = new FileInfo(path); if (!file.Exists) return "";
                    key.Append(Path.GetFullPath(path)).Append('|').Append(file.Length).Append('|').Append(file.LastWriteTimeUtc.Ticks).Append('|');
                }
                key.Append(info.Width).Append('|').Append(info.Height).Append('|').Append(info.ColorTransfer).Append('|').Append(info.ColorRange);
                return key.ToString();
            }
            catch { return ""; }
        }

        internal static bool IsoContextEquivalent(MediaProbeInfo source, MediaProbeInfo normalized)
        {
            return source != null && normalized != null && source.Width > 0 && source.Height > 0
                && source.Width == normalized.Width && source.Height == normalized.Height
                && string.Equals(source.ColorTransfer ?? "", normalized.ColorTransfer ?? "", StringComparison.OrdinalIgnoreCase)
                && string.Equals(source.ColorRange ?? "", normalized.ColorRange ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsoContextMatches(string ffprobe, string sample, MediaProbeInfo source, out string reason)
        {
            reason = "ISO 输入色彩信息与原 IVF 不一致，保留原颗粒效果 / ISO color context differs";
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo(ffprobe, "-v error -show_entries stream=codec_type,width,height,color_transfer,color_range -of json " + Quote(sample))
                        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(5000)) { try { process.Kill(); } catch { } process.WaitForExit(); reason = "ISO 色彩核对超时，保留原流程 / ISO context check timed out"; return false; }
                    stdout.Wait(); stderr.Wait();
                    if (process.ExitCode != 0) { reason = "ISO 色彩核对失败，保留原流程 / ISO context probe failed"; return false; }
                    return IsoContextEquivalent(source, MediaProbeInfo.Parse(sample, stdout.Result));
                }
            }
            catch { reason = "ISO 色彩核对不可用，保留原流程 / ISO context check unavailable"; return false; }
        }

        internal static string ValidateGrainHeader(string ffmpeg, string output)
        {
            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo(ffmpeg,
                        "-hide_banner -loglevel trace -i " + Quote(output) + " -map 0:v:0 -c:v copy -bsf:v trace_headers -frames:v 1 -an -sn -dn -f null -");
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.Start();
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(15000)) { try { process.Kill(); } catch { } process.WaitForExit(); return "AV1 header verification timed out."; }
                    stdout.Wait(); stderr.Wait();
                    if (process.ExitCode != 0) return "AV1 header verification failed: " + Av1GrainInspectCore.GetFailureDetail(stderr.Result);
                    return Regex.IsMatch(stderr.Result, @"film_grain_params_present.*=\s*1\s*(?:\r?\n|$)")
                        ? "" : "AV1 output has no confirmed film grain header. Enable full verification for sparse/dynamic tables.";
                }
            }
            catch (Exception ex) { return "AV1 header verification: " + ex.Message; }
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
