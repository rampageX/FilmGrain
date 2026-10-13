using System;
using System.Globalization;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

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

namespace FilmGrainStudioPreview
{
    // Shared FRUC preflight and timestamp policy; encoder-specific filters stay in their cores.
    internal static class SharedFrucCore
    {
        internal sealed class CapabilityReport
        {
            internal string Version = "unknown";
            internal string RuntimeOutput = "";
            internal string HelpOutput = "";
            internal string Evidence = "device/grid not reported by this FFmpeg build";
            internal string Summary(string perf, string grid)
            {
                return "Vulkan device index=0; perf=" + perf + "; requested_grid=" + grid + "; " + Evidence +
                    "; " + Version + "; probe=128x128 NV12 30->60 fps (not an input/encoder compatibility guarantee)";
            }
        }
        private static readonly Dictionary<string, CapabilityReport> runtimeCache = new Dictionary<string, CapabilityReport>();

        internal static void InvalidatePreflightCache()
        { lock (runtimeCache) { runtimeCache.Clear(); } }

        internal delegate bool ProbeRunner(string tool, string args, int timeout, out string output, out string reason);
        internal static bool TryPreflight(string ffmpeg, string perf, string grid, out string reason, ProbeRunner runner = null)
        {
            CapabilityReport report;
            return TryInspect(ffmpeg, perf, grid, out report, out reason, runner);
        }

        internal static bool HasOption(string help, string option)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(help ?? "",
                @"(?m)^\s*" + System.Text.RegularExpressions.Regex.Escape(option) + @"\s+<[^>]+>");
        }

        internal static string FailureKind(string reason)
        {
            string text = (reason ?? "").ToLowerInvariant();
            if (text.StartsWith("[input error]")) return "CONFIG";
            if (text.StartsWith("[dependency missing]")) return "FFMPEG";
            if (text.StartsWith("[unsupported combination]")) return "UNSUPPORTED";
            if (text.StartsWith("[unsupported device]") || text.Contains("vk_nv_optical_flow") || text.Contains("does not support") ||
                text.Contains("no device") || text.Contains("failed to create vulkan") || text.Contains("cannot load vulkan") ||
                text.Contains("no optical flow queues") || text.Contains("no compute queues") || text.Contains("created without the") ||
                text.Contains("families cannot exchange") || text.Contains("vector format unavailable") || text.Contains("r8 input format unavailable")) return "DEVICE";
            return "RUNTIME";
        }

        internal static bool TryInspect(string ffmpeg, string perf, string grid, out CapabilityReport report,
            out string reason, ProbeRunner runner = null)
        {
            report = new CapabilityReport(); reason = "";
            if (perf != "slow" && perf != "medium" && perf != "fast")
            { reason = "[input error] invalid FRUC performance level: " + perf; return false; }
            if (grid != "auto" && grid != "1" && grid != "2" && grid != "4" && grid != "8")
            { reason = "[input error] invalid FRUC grid: " + grid; return false; }
            if (runner == null)
            {
                if (!File.Exists(ffmpeg)) { reason = "[dependency missing] FRUC FFmpeg executable: " + ffmpeg; return false; }
                runner = Run;
            }
            string output;
            if (!runner(ffmpeg, "-hide_banner -h filter=fruc_vulkan", 5000, out output, out reason))
            { reason = "[program/runtime error] FRUC help probe failed: " + reason; return false; }
            report.HelpOutput = output;
            if (!output.Contains("Filter fruc_vulkan") || output.IndexOf("Unknown filter", StringComparison.OrdinalIgnoreCase) >= 0)
            { reason = "[dependency missing] FFmpeg does not expose the fruc_vulkan filter"; return false; }
            foreach (string option in new string[] { "fps", "perf", "grid" })
                if (!HasOption(output, option))
                { reason = "[unsupported combination] Selected fruc_vulkan build does not advertise option: " + option; return false; }
            // The help must advertise the selected enum as well, not merely a matching substring.
            if (!System.Text.RegularExpressions.Regex.IsMatch(output,
                @"(?m)^\s*" + perf + @"\s+-?\d+\s+"))
            { reason = "[unsupported combination] Selected fruc_vulkan build does not advertise perf=" + perf; return false; }
            string key = Path.GetFullPath(ffmpeg) + "|" + File.GetLastWriteTimeUtc(ffmpeg).Ticks + "|" + perf + "|" + grid;
            lock (runtimeCache) { if (runtimeCache.TryGetValue(key, out report)) return true; }
            report = new CapabilityReport { HelpOutput = output };
            string versionReason;
            if (runner(ffmpeg, "-hide_banner -version", 5000, out output, out versionReason))
                foreach (string line in output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    if (line.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase)) { report.Version = line.Trim(); break; }
            string args = "-hide_banner -v verbose -nostdin -init_hw_device vulkan=vk:0 -filter_hw_device vk " +
                "-f lavfi -i testsrc2=size=128x128:rate=30:duration=0.2 " +
                "-vf format=nv12,hwupload,fruc_vulkan=fps=60:perf=" + perf + ":grid=" + grid +
                ",hwdownload,format=nv12 -frames:v 4 -f null -";
            bool ok = runner(ffmpeg, args, 15000, out output, out reason);
            report.RuntimeOutput = output;
            StringBuilder evidence = new StringBuilder();
            foreach (string raw in output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.IndexOf("optical flow: perf", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("Using device", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("Device name:", StringComparison.OrdinalIgnoreCase) >= 0)
                { if (evidence.Length > 0) evidence.Append(" | "); evidence.Append(line); }
            }
            if (evidence.Length > 0) report.Evidence = evidence.ToString();
            if (!ok)
            {
                string detail = string.IsNullOrEmpty(reason) ? output.Trim() : reason;
                string lower = detail.ToLowerInvariant();
                bool unsupported = lower.Contains("requested optical flow grid") || lower.Contains("outside the range") ||
                    lower.Contains("unsupported pixel format") || lower.Contains("not supported by the device");
                reason = (unsupported ? "[unsupported combination] " : FailureKind(detail) == "DEVICE" ? "[unsupported device] " : "[program/runtime error] ") +
                    "FRUC Vulkan device 0 preflight failed: " + detail;
                return false;
            }
            // Only explicit refresh invalidates a success; never cache failures or infer other input formats.
            lock (runtimeCache) { runtimeCache[key] = report; }
            return true;
        }

        internal static bool Run(string tool, string args, int timeout, out string output, out string reason)
        {
            output = ""; reason = "";
            StringBuilder text = new StringBuilder();
            try
            {
                using (Process p = new Process())
                {
                    p.StartInfo = new ProcessStartInfo(tool, args) { UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true };
                    DataReceivedEventHandler capture = delegate(object sender, DataReceivedEventArgs e)
                    { if (e.Data != null) lock (text) { if (text.Length < 65536) text.AppendLine(e.Data); } };
                    p.OutputDataReceived += capture; p.ErrorDataReceived += capture;
                    p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeout))
                    {
                        try { p.Kill(); p.WaitForExit(); } catch { }
                        output = text.ToString(); reason = "Timed out: " + Path.GetFileName(tool) + " " + output.Trim(); return false;
                    }
                    p.WaitForExit(); output = text.ToString();
                    if (p.ExitCode != 0) { reason = "exit=" + p.ExitCode + " " + output.Trim(); return false; }
                    return true;
                }
            }
            catch (Exception ex) { reason = ex.Message; return false; }
        }

        internal static string PrepareStage(BridgeExecutionStage stage)
        {
            List<string> args = new List<string>(stage.FrucArguments);
            int inputIndex = args.IndexOf("-i");
            if (inputIndex < 0 || inputIndex + 1 >= args.Count) throw new InvalidOperationException("FRUC source is missing.");
            string source = args[inputIndex + 1];
            FrucTimeline timeline = ProbeTimeline(stage.FrucProbePath, source);
            double videoOrigin = timeline.VideoOrigin, commonOrigin = timeline.CommonOrigin;
            double videoDelay = videoOrigin - commonOrigin;
            if (!string.IsNullOrEmpty(stage.FrucTimelineReferenceInput) && !string.Equals(stage.FrucTimelineReferenceInput, source, StringComparison.OrdinalIgnoreCase))
                videoDelay = ProbeTimeline(stage.FrucProbePath, stage.FrucTimelineReferenceInput).VideoDelay;
            // Move every input stream by the same amount. Restore video delay after FRUC's local zero-based clock.
            args.InsertRange(inputIndex, new string[]{"-copyts", "-itsoffset", (-commonOrigin).ToString("0.#########", CultureInfo.InvariantCulture)});
            int filterIndex = args.IndexOf("-filter_complex");
            if (filterIndex < 0 || !args[filterIndex + 1].Contains("[vout]")) throw new InvalidOperationException("FRUC output filter is missing.");
            if (videoDelay > 0.000000001)
                args[filterIndex + 1] = args[filterIndex + 1].Replace("[vout]", ",setpts=PTS+" + videoDelay.ToString("0.#########", CultureInfo.InvariantCulture) + "/TB[vout]");
            // Output duration limits are on the common timeline, including any initial video delay.
            for (int i = filterIndex + 2; i + 1 < args.Count; i++)
                if (args[i] == "-t")
                {
                    double length;
                    if (double.TryParse(args[i+1], NumberStyles.Float, CultureInfo.InvariantCulture, out length))
                        args[i+1] = (length + videoDelay).ToString("0.#########", CultureInfo.InvariantCulture);
                }
            StringBuilder command = new StringBuilder();
            foreach (string arg in args) { if (command.Length > 0) command.Append(' '); command.Append(Quote(arg)); }
            stage.DirectArguments = command.ToString();
            return "[FRUC timeline] video_origin=" + videoOrigin.ToString("0.#########", CultureInfo.InvariantCulture) +
                " common_origin=" + commonOrigin.ToString("0.#########", CultureInfo.InvariantCulture) +
                " video_delay=" + videoDelay.ToString("0.#########", CultureInfo.InvariantCulture) + " input=" + source;
        }

        internal sealed class FrucTimeline
        {
            internal double VideoOrigin;
            internal double CommonOrigin;
            internal double VideoDelay { get { return VideoOrigin - CommonOrigin; } }
        }

        internal static FrucTimeline ProbeTimeline(string ffprobe, string source)
        {
            string output, reason;
            if (!Run(ffprobe, "-v error -show_entries stream=codec_type,start_time -of default=noprint_wrappers=0 " + Quote(source),
                15000, out output, out reason)) throw new InvalidOperationException("Interpolation timestamp probe failed: " + reason);
            double videoOrigin = double.NaN, commonOrigin = double.PositiveInfinity;
            string type = "";
            foreach (string raw in output.Split(new char[]{'\r','\n'}, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.StartsWith("codec_type=", StringComparison.Ordinal)) type = line.Substring(11);
                else if (line.StartsWith("start_time=", StringComparison.Ordinal))
                {
                    double time;
                    if (!double.TryParse(line.Substring(11), NumberStyles.Float, CultureInfo.InvariantCulture, out time) || double.IsNaN(time) || double.IsInfinity(time)) continue;
                    if (type == "video" && double.IsNaN(videoOrigin)) videoOrigin = time;
                    if (type == "video" || type == "audio") commonOrigin = Math.Min(commonOrigin, time);
                }
            }
            if (double.IsNaN(videoOrigin) || double.IsInfinity(commonOrigin))
                throw new InvalidOperationException("Interpolation requires a known video start_time: " + source);
            return new FrucTimeline { VideoOrigin = videoOrigin, CommonOrigin = commonOrigin };
        }

        internal static string JoinArguments(IEnumerable<string> args)
        {
            StringBuilder command = new StringBuilder();
            foreach (string arg in args) { if (command.Length > 0) command.Append(' '); command.Append(Quote(arg)); }
            return command.ToString();
        }

        internal static BridgeExecutionStage CreateVideoPreprocess(string ffmpeg, string ffprobe, string source,
            string output, string cwd, string fps, string perf, string grid, double duration, int index, int total)
        {
            List<string> args = new List<string>{"-hide_banner", "-stats", "-progress", "pipe:2", "-stats_period", "0.5", "-y",
                "-init_hw_device", "vulkan=vk:0", "-filter_hw_device", "vk", "-i", source,
                "-filter_complex", "[0:v:0]setpts=PTS-STARTPTS,format=nv12,hwupload,fruc_vulkan=fps=" + fps +
                    ":perf=" + perf + ":grid=" + grid + ",hwdownload,format=nv12,format=yuv420p[vout]",
                "-map", "[vout]", "-an", "-sn", "-dn", "-map_metadata", "0", "-c:v", "ffv1", "-level", "3",
                "-pix_fmt", "yuv420p", "-fps_mode", "passthrough", output};
            return new BridgeExecutionStage { ToolPath = ffmpeg, WorkingDirectory = cwd, DirectArguments = JoinArguments(args),
                FrucArguments = args, FrucProbePath = ffprobe, ProgressDurationSeconds = duration,
                StageCurrent = index, StageTotal = total, StageText = "FRUC Vulkan shared video preprocessing" };
        }

        internal static void AttachAv1RemuxTimeline(BridgeExecutionStage stage, string ffprobe,
            string video, string original, IList<string> originalArguments, string engine = "FRUC")
        {
            // IVF / grav1synth may rebase timestamps. Probe the actual final IVF immediately before mux.
            stage.BeforeRunPreparation = delegate
            {
                FrucTimeline source = ProbeTimeline(ffprobe, original);
                FrucTimeline coded = ProbeTimeline(ffprobe, video);
                List<string> args = new List<string>(originalArguments);
                int first = args.IndexOf("-i"), second = -1;
                for (int i = first + 2; i < args.Count; i++) if (args[i] == "-i") { second = i; break; }
                if (first < 0 || second < 0) throw new InvalidOperationException("AV1 FRUC mux inputs are missing.");
                args.InsertRange(second, new string[]{"-itsoffset", (-source.CommonOrigin).ToString("0.#########", CultureInfo.InvariantCulture)});
                args.InsertRange(first, new string[]{"-itsoffset", (source.VideoDelay - coded.VideoOrigin).ToString("0.#########", CultureInfo.InvariantCulture)});
                args.Insert(0, "-copyts");
                stage.DirectArguments = JoinArguments(args);
                return "[AV1 " + engine + " remux timeline] source_common=" + source.CommonOrigin.ToString("0.#########", CultureInfo.InvariantCulture) +
                    " source_video_delay=" + source.VideoDelay.ToString("0.#########", CultureInfo.InvariantCulture) +
                    " coded_video_origin=" + coded.VideoOrigin.ToString("0.#########", CultureInfo.InvariantCulture);
            };
        }

        internal static string Quote(string value)
        {
            // ProcessStartInfo arguments use Windows command-line quoting, never CMD escaping.
            StringBuilder b = new StringBuilder(); b.Append('"'); int slash = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slash++; continue; }
                if (c == '"') { b.Append('\\', slash * 2 + 1); b.Append('"'); slash = 0; continue; }
                if (slash > 0) { b.Append('\\', slash); slash = 0; } b.Append(c);
            }
            if (slash > 0) b.Append('\\', slash * 2); b.Append('"'); return b.ToString();
        }
    }

    // Shared .NET process preparation; VapourSynth remains the interpolation engine.
    internal static class SharedSvpCore
    {
        internal static bool TryValidateOptions(string speed, string tuning, string block, string analyse, out string reason)
        {
            reason = "";
            if (Array.IndexOf(new string[] { "FGS", "medium", "fast", "faster", "fastest" }, speed) < 0)
            { reason = "OpenSVPFlow input error: invalid Speed: " + speed; return false; }
            if (Array.IndexOf(new string[] { "FGS", "weak", "film", "smooth", "animation" }, tuning) < 0)
            { reason = "OpenSVPFlow input error: invalid Tuning: " + tuning; return false; }
            if (Array.IndexOf(new string[] { "auto", "8", "16", "32" }, block) < 0)
            { reason = "OpenSVPFlow input error: invalid Block Size: " + block; return false; }
            if (analyse != "BASE" && analyse != "ENCODEGUI")
            { reason = "OpenSVPFlow input error: invalid Analyse Profile: " + analyse; return false; }
            if (analyse == "BASE" && (speed != "FGS" || tuning != "FGS" || block != "auto"))
            { reason = "OpenSVPFlow unsupported combination: BASE requires Speed=FGS, Tuning=FGS, Block Size=auto."; return false; }
            return true;
        }

        // D8P6WK: fixed upstream release, checked by bytes rather than a mutable marker.
        internal const string OptionsPlugin1Hash = "36258dfa4f1c8f4a3b88a61c0327c2271ec3fc6a107ce0c1e0cb859b23f7fa9d";
        internal const string OptionsPlugin2Hash = "da82451364b65241b6547b703739e761422d922c45b8f111fcd98f805faf58eb";

        internal static string[] ExtraArguments(IDictionary<string,string> state)
        {
            string dedup = ExtraNumber(state,"FG_SVP_DEDUP","0",10);
            string still = ExtraSwitch(state,"FG_SVP_STILL");
            string peel = ExtraSwitch(state,"FG_SVP_PEEL");
            string limit = ExtraNumber(state,"FG_SVP_STILL_LIMIT","4.5",100);
            string edge = ExtraNumber(state,"FG_SVP_STILL_EDGE","5",100);
            string tolerance = ExtraNumber(state,"FG_SVP_STILL_TOLERANCE","30",100);
            string peelLimit = ExtraNumber(state,"FG_SVP_PEEL_LIMIT","6",100);
            if(dedup=="0" && still=="0" && peel=="0") return new string[0];
            return new string[]{"--arg","dedup="+dedup,"--arg","still="+still,
                "--arg","still_limit="+limit,"--arg","still_edge="+edge,
                "--arg","still_tolerance="+tolerance,"--arg","peel="+peel,"--arg","peel_limit="+peelLimit};
        }

        private static string ExtraNumber(IDictionary<string,string> state,string key,string fallback,double maximum)
        {
            string text=Option(state,key,fallback);double value;
            if(!double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out value) ||
                double.IsNaN(value)||double.IsInfinity(value)||value<0||value>maximum)
                throw new ArgumentException("OpenSVPFlow input error: "+key+" must be finite and within 0.."+maximum.ToString(CultureInfo.InvariantCulture));
            return value.ToString("0.#########",CultureInfo.InvariantCulture);
        }

        private static string ExtraSwitch(IDictionary<string,string> state,string key)
        {
            string value=Option(state,key,"false");
            if(value=="1" || string.Equals(value,"true",StringComparison.OrdinalIgnoreCase))return "1";
            if(value=="0" || string.Equals(value,"false",StringComparison.OrdinalIgnoreCase))return "0";
            throw new ArgumentException("OpenSVPFlow input error: "+key+" must be true/false or 1/0");
        }

        internal static void ValidateExtraPlugins(string plugins)
        {
            string[] files={"svpflow1_vs.dll","svpflow2_vs.dll"};
            string[] hashes={OptionsPlugin1Hash,OptionsPlugin2Hash};
            for(int i=0;i<files.Length;i++) {
                string path=Path.Combine(plugins,files[i]);
                if(!File.Exists(path))throw new IOException("OpenSVPFlow new options require nightly-20261011-deadbadae0d9: "+path);
                using(var sha=System.Security.Cryptography.SHA256.Create())
                using(var stream=File.OpenRead(path)) {
                    string actual=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
                    if(actual!=hashes[i])throw new IOException("OpenSVPFlow 新选项需要本版配套固定插件 nightly-20261011-deadbadae0d9；当前 DLL 未通过能力门禁。 / New options require the pinned plugin: "+files[i]);
                }
            }
        }

        private static string Option(IDictionary<string,string> state, string key, string defaultValue)
        { string value; return state.TryGetValue(key, out value) ? value ?? "" : defaultValue; }

        internal static void AttachProducer(BridgeExecutionStage stage, string vspipe, string vpy, string plugins, string input, IDictionary<string,string> state, string outputFps)
        {
            string algo = Get(state, "FG_SVP_ALGO"), analyse = Get(state, "FG_SVP_ANALYSE");
            string scene = Get(state, "FG_SVP_SCENE_MODE"), mask = Get(state, "FG_SVP_MASK_AREA");
            int n;
            if (!int.TryParse(algo, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ||
                (n != 1 && n != 2 && n != 11 && n != 13 && n != 21 && n != 22 && n != 23)) algo = "13";
            if (analyse != "BASE" && analyse != "ENCODEGUI") analyse = "ENCODEGUI";
            if (scene != "0" && scene != "3") scene = "0";
            if (!int.TryParse(mask, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < 0 || n > 100) mask = "100";
            string speed = Option(state, "FG_SVP_SPEED", "FGS"), tuning = Option(state, "FG_SVP_TUNING", "FGS"), block = Option(state, "FG_SVP_BLOCK_SIZE", "auto");
            string optionError;
            if (!TryValidateOptions(speed, tuning, block, analyse, out optionError)) throw new ArgumentException(optionError);
            string[] extra = ExtraArguments(state);
            if(extra.Length>0) ValidateExtraPlugins(plugins);
            stage.PipeProducerPath = vspipe;
            var producerArgs = new List<string>(new string[]{"--progress", "-c", "y4m", "--arg", "input=" + input,
                "--arg", "plugin_dir=" + plugins, "--arg", "target_num=" + outputFps.Split('/')[0], "--arg", "target_den=" + (outputFps.Contains("/") ? outputFps.Split('/')[1] : "1"),
                "--arg", "algo=" + algo, "--arg", "analyse_profile=" + analyse,
                "--arg", "scene_mode=" + scene, "--arg", "mask_area=" + mask,
                "--arg", "speed=" + speed, "--arg", "tuning=" + tuning, "--arg", "block_size=" + block});
            producerArgs.AddRange(extra);producerArgs.Add(vpy);producerArgs.Add("-");
            stage.PipeProducerArguments = JoinArgs(producerArgs);
        }


        private static string JoinArgs(IEnumerable<string> args) { StringBuilder b = new StringBuilder(); foreach (string x in args) { if (b.Length > 0) b.Append(' '); b.Append(Quote(x ?? "")); } return b.ToString(); }
        private static string Quote(string s)
        {
            if (s.Length > 0 && s.IndexOfAny(new char[]{' ','\t','\n','\v','"'}) < 0) return s;
            StringBuilder b = new StringBuilder(); b.Append('"'); int bs = 0;
            foreach (char c in s) { if (c == '\\') { bs++; continue; } if (c == '"') { b.Append('\\', bs * 2 + 1); b.Append('"'); bs = 0; continue; } if (bs > 0) { b.Append('\\', bs); bs = 0; } b.Append(c); }
            if (bs > 0) b.Append('\\', bs * 2); b.Append('"'); return b.ToString();
        }

        private static string Get(IDictionary<string,string> state, string key)
        { string value; return state.TryGetValue(key, out value) ? value ?? "" : ""; }

        internal static bool TryDependencies(string root, out string reason)
        {
            string dir = Path.Combine(root, "_OpenSVPFlow");
            foreach (string file in new string[]{Path.Combine(dir, ".venv", "Scripts", "vspipe.exe"),
                Path.Combine(dir, "FilmGrain_OpenSVPFlow.vpy"), Path.Combine(dir, "Plugins", "svpflow1_vs.dll"),
                Path.Combine(dir, "Plugins", "svpflow2_vs.dll")})
                if (!File.Exists(file)) { reason = "OpenSVPFlow dependency is missing: " + file; return false; }
            reason = ""; return true;
        }

        internal static BridgeExecutionStage CreateVideoPreprocess(string root, string ffmpeg, string ffprobe,
            string input, string output, string cwd, string fps, IDictionary<string,string> state, double duration, int index, int total)
        {
            var originalArgs = new List<string>{"-hide_banner", "-stats", "-progress", "pipe:2", "-stats_period", "0.5", "-y",
                "-f", "yuv4mpegpipe", "-i", "pipe:0", "-map", "0:v:0", "-an", "-sn", "-dn",
                "-c:v", "ffv1", "-level", "3", "-pix_fmt", "yuv420p", "-fps_mode", "passthrough", output};
            var stage = new BridgeExecutionStage{ToolPath=ffmpeg, WorkingDirectory=cwd,
                DirectArguments=JoinArgs(originalArgs), ProgressDurationSeconds=duration,
                StageCurrent=index, StageTotal=total, StageText="OpenSVPFlow shared video preprocessing"};
            string dir = Path.Combine(root, "_OpenSVPFlow");
            AttachProducer(stage, Path.Combine(dir, ".venv", "Scripts", "vspipe.exe"),
                Path.Combine(dir, "FilmGrain_OpenSVPFlow.vpy"), Path.Combine(dir, "Plugins"), input, state, fps);
            stage.BeforeRunPreparation = delegate {
                SharedFrucCore.FrucTimeline timeline = SharedFrucCore.ProbeTimeline(ffprobe, input);
                var args = new List<string>(originalArgs);
                int at = args.IndexOf("-i");
                args.InsertRange(at, new string[]{"-copyts", "-itsoffset", timeline.VideoDelay.ToString("0.#########", CultureInfo.InvariantCulture)});
                stage.DirectArguments=JoinArgs(args);
                return "[OpenSVPFlow timeline] source_common=" + timeline.CommonOrigin.ToString("0.#########", CultureInfo.InvariantCulture) +
                    " video_delay=" + timeline.VideoDelay.ToString("0.#########", CultureInfo.InvariantCulture);
            };
            return stage;
        }
    }
}
