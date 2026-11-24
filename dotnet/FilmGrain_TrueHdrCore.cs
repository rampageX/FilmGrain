using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace FilmGrainStudioPreview
{
    internal sealed class TrueHdrOptions
    {
        internal static readonly string[] Names = { "contrast", "saturation", "middlegray", "maxluminance" };
        internal static readonly int[] Defaults = { 125, 75, 44, 1000 };
        internal static readonly int[] Minimums = { 0, 0, 10, 400 };
        internal static readonly int[] Maximums = { 200, 200, 100, 2000 };
        public int[] Picture { get; set; }
        public string Codec { get; set; }
        public string Preset { get; set; }
        public bool Cq { get; set; }
        public bool Auto { get; set; }
        public bool HighMotion { get; set; }
        public string Target { get; set; }
        public string PreviewStart { get; set; }
        public int PreviewSeconds { get; set; }
        public bool Mp4 { get; set; }
        public Dictionary<string, string> Advanced { get; set; }

        public TrueHdrOptions()
        {
            Picture = (int[])Defaults.Clone();
            Codec = "HEVC";
            Preset = "p6";
            Auto = true;
            Target = "12000";
            Advanced = new Dictionary<string, string>();
        }
    }

    internal static class TrueHdrCore
    {
        internal static bool Eligible(MediaProbeInfo source)
        {
            return source != null && source.HasVideo && source.VideoStreamCount <= 1 && !source.IsHdr &&
                source.ColorTransfer != "smpte428" && !source.IsInterlaced &&
                source.Width > 0 && source.Height > 0 && source.Width % 2 == 0 && source.Height % 2 == 0;
        }

        internal static bool AdvertisesTrueHdr(string help)
        {
            return Regex.IsMatch(help ?? "", @"(?m)^\s*--vpp-ngx-truehdr(?:\s|$)");
        }

        internal static void RequireTrueHdr(string executable)
        {
            string help, error;
            if (!SharedFrucCore.RunWithLimit(executable, "--help", 10000, out help, out error, 1048576))
                throw new InvalidOperationException("TrueHDR could not read NVEncC help: " + executable + ". " + error);
            if (!AdvertisesTrueHdr(help))
                throw new InvalidOperationException("NVEncC does not list --vpp-ngx-truehdr: " + executable);
        }

        internal static string Number(int value) { return value.ToString(CultureInfo.InvariantCulture); }

        private static Dictionary<string, string> RateState(TrueHdrOptions options)
        {
            string prefix = "NVENC_" + options.Codec;
            return new Dictionary<string, string> {
                { "FG_MODE", options.Codec }, { "FG_NVENC_RATE_CONTROL", options.Cq ? "CQ" : "VBR" },
                { "FG_NVENC_CQ", options.Auto ? NvencRateControlCore.Recommend(options.Codec, options.HighMotion, false) : options.Target },
                { "FG_NVENC_CQ_LIMIT_ENABLED", Setting(options, prefix + "_CQ_LIMIT_ENABLED", "false") },
                { "FG_NVENC_CQ_MAXRATE", Setting(options, prefix + "_CQ_MAXRATE", "100") }
            };
        }

        private static string Setting(TrueHdrOptions options, string key, string fallback)
        {
            string value;
            return options.Advanced != null && options.Advanced.TryGetValue(key, out value) ? value : fallback;
        }

        internal static int Bitrate(TrueHdrOptions options, MediaProbeInfo source)
        {
            if (options.Cq) return 0;
            if (options.Auto) return BitrateRecommendationCore.GetRecommendedBitrate(options.Codec == "AV1" ? 0 : 1,
                source.Width, source.Height, BitrateRecommendationCore.ParseMediaFps(source), options.HighMotion);
            int bitrate;
            if (!int.TryParse(options.Target, NumberStyles.None, CultureInfo.InvariantCulture, out bitrate) || bitrate < 100 || bitrate > 500000)
                throw new InvalidDataException("TrueHDR: target bitrate must be 100–500000 kbps.");
            return bitrate;
        }

        internal static string Speed(string measured, double sourceFps)
        {
            double fps;
            return sourceFps > 0 && !double.IsInfinity(sourceFps) &&
                double.TryParse(measured, NumberStyles.Float, CultureInfo.InvariantCulture, out fps)
                ? (fps / sourceFps).ToString("0.##", CultureInfo.InvariantCulture) : "";
        }

        private static string PictureArgument(int[] values)
        {
            var picture = new string[TrueHdrOptions.Names.Length];
            if (values == null || values.Length != picture.Length) throw new InvalidDataException("Invalid TrueHDR parameters.");
            for (int i = 0; i < picture.Length; i++)
            {
                int value = values[i];
                if (value < TrueHdrOptions.Minimums[i] || value > TrueHdrOptions.Maximums[i])
                    throw new InvalidDataException("TrueHDR: " + TrueHdrOptions.Names[i] + " out of range.");
                picture[i] = TrueHdrOptions.Names[i] + "=" + Number(value);
            }
            return string.Join(",", picture);
        }

        internal static List<string> LiveFrameArguments(string input, string output, double seconds, int[] values)
        {
            return new List<string> { "--avsw", "--video-track", "1", "--seek",
                seconds.ToString("0.###", CultureInfo.InvariantCulture), "--frames", "1", "-i", input,
                "--codec", "raw", "--output-depth", "10", "--output-csp", "yuv420", "--y4m-timestamp",
                "--vpp-ngx-truehdr", PictureArgument(values), "--colormatrix", "bt2020nc",
                "--colorprim", "bt2020", "--transfer", "smpte2084", "--colorrange", "auto", "-o", output };
        }

        internal static Tuple<System.Drawing.Bitmap, System.Drawing.Bitmap> RenderLiveFrame(string nvenc, MediaProbeInfo source,
            double seconds, int[] values, CancellationToken token)
        {
            string directory = Path.Combine(Path.GetTempPath(), "FGS-TrueHDR-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string originalPath = Path.Combine(directory, "source.y4m");
                string filteredPath = Path.Combine(directory, "truehdr.y4m");
                var filtered = LiveFrameArguments(source.PathValue, filteredPath, seconds, values);
                var original = new List<string>(filtered);
                original[original.Count - 1] = originalPath;
                original.RemoveRange(original.IndexOf("--vpp-ngx-truehdr"), 2);
                original[original.IndexOf("--colormatrix") + 1] = "auto";
                original[original.IndexOf("--colorprim") + 1] = "auto";
                original[original.IndexOf("--transfer") + 1] = "auto";
                RunLiveFrameTool(nvenc, filtered, token);
                RunLiveFrameTool(nvenc, original, token);
                var before = TrueHdrPreviewFrames.Read(originalPath, source, false, values[3], token);
                try { return Tuple.Create(before, TrueHdrPreviewFrames.Read(filteredPath, source, true, values[3], token)); }
                catch { before.Dispose(); throw; }
            }
            finally { try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        private static void RunLiveFrameTool(string executable, IList<string> args, CancellationToken token)
        {
            if (!File.Exists(executable)) throw new FileNotFoundException("Preview tool missing", executable);
            var log = new System.Text.StringBuilder();
            int exit = NativeLibraryTools.RunProcessEnvironment(executable, args, Path.GetDirectoryName(executable),
                null, 20000, token, delegate(string line) { lock (log) if (log.Length < 16384) log.AppendLine(line); });
            if (exit != 0) throw new IOException(Path.GetFileName(executable) + " exit=" + Number(exit) + "\n" + log);
        }

        internal static List<string> Arguments(string input, string output, TrueHdrOptions options,
            MediaProbeInfo source, IDictionary<string, string> audio)
        {
            if (!Eligible(source)) throw new InvalidDataException("TrueHDR requires single-stream progressive SDR video with even dimensions.");
            bool hevc = options.Codec == "HEVC";
            if (!hevc && options.Codec != "AV1") throw new InvalidDataException("TrueHDR requires HEVC or AV1 NVENC.");
            if (Array.IndexOf(new[] { "p1", "p2", "p3", "p4", "p5", "p6", "p7" }, options.Preset) < 0)
                throw new InvalidDataException("Invalid NVEncC preset.");
            string picture = PictureArgument(options.Picture);
            // The avsw reader passes the source VUI to NGX. These color options signal the output.
            // Preserve the source range through NGX's internal colorspace conversion.
            var args = new List<string> { "--avsw", "--video-track", "1", "--avsync", "vfr", "--log-opt", "color=off", "-i", input,
                "--codec", hevc ? "hevc" : "av1", "--profile", hevc ? "main10" : "main",
                "--preset", options.Preset, "--output-depth", "10", "--output-csp", "yuv420",
                "--vpp-ngx-truehdr", picture, "--colorprim", "bt2020", "--transfer", "smpte2084",
                "--colormatrix", "bt2020nc", "--colorrange", "auto", "--master-display",
                "G(8500,39850)B(6550,2300)R(35400,14600)WP(15635,16450)L(" + Number(options.Picture[3] * 10000) + ",1)",
                "--vbr", Number(Bitrate(options, source)) };
            if (options.PreviewSeconds != 0)
            {
                double start;
                if (options.PreviewSeconds < 1 || options.PreviewSeconds > 86400 ||
                    !SharedEncodePreviewCore.TryParseStart(options.PreviewStart, out start))
                    throw new InvalidDataException("TrueHDR preview requires HH:MM:SS and a duration of 1–86400 seconds.");
                args.InsertRange(args.IndexOf("-i"), new[] {
                    "--seek", start.ToString(CultureInfo.InvariantCulture),
                    "--seekto", (start + options.PreviewSeconds).ToString(CultureInfo.InvariantCulture) });
            }
            if (options.Cq)
            {
                var rate = RateState(options);
                args.AddRange(new[] { "--vbr-quality", NvencRateControlCore.Quality(rate) });
                int limit = NvencRateControlCore.Limit(rate);
                if (limit > 0) args.AddRange(new[] { "--max-bitrate", Number(limit * 1000), "--vbv-bufsize", Number(limit * 2000) });
            }
            string multipass = Setting(options, options.Codec + "_MULTIPASS", "FGS");
            if (multipass != "FGS")
            {
                PresetCore.ValidateConfig(options.Codec + "_MULTIPASS", multipass);
                args.AddRange(new[] { "--multipass", multipass == "QRES" ? "2pass-quarter" : multipass == "FULLRES" ? "2pass-full" : "none" });
            }
            if (hevc)
            {
                foreach (var control in new[] { "BFRAMES", "BREF_MODE", "LOOKAHEAD", "SPATIAL_AQ", "TEMPORAL_AQ" })
                {
                    string key = "HEVC_" + control;
                    string value = Setting(options, key, "FGS");
                    if (value == "FGS") continue;
                    PresetCore.ValidateConfig(key, value);
                    switch (control)
                    {
                        case "BFRAMES": args.AddRange(new[] { "--bframes", value }); break;
                        case "BREF_MODE": args.AddRange(new[] { "--bref-mode", value }); break;
                        case "LOOKAHEAD": args.AddRange(new[] { "--lookahead", value }); break;
                        case "SPATIAL_AQ": args.AddRange(value == "0" ? new[] { "--no-aq" } : new[] { "--aq", "--aq-strength", value }); break;
                        case "TEMPORAL_AQ": args.Add(value == "true" ? "--aq-temporal" : "--no-aq-temporal"); break;
                    }
                }
            }
            AudioOutputCore.Validate(audio);
            string mode = AudioOutputCore.Get(audio, "FG_AUDIO_MODE", "FGS");
            var tracks = AudioOutputCore.Tracks(source);
            var copies = new List<string>();
            for (int i = 0; i < tracks.Count; i++)
            {
                string track = Number(i + 1);
                if (AudioOutputCore.Copy(tracks[i], mode, !options.Mp4, false)) copies.Add(track);
                else args.AddRange(new[] { "--audio-codec", track + "?aac", "--audio-bitrate", track + "?" + Number(AudioOutputCore.Rate(tracks[i], audio)) });
            }
            if (copies.Count != 0) args.AddRange(new[] { "--audio-copy", string.Join(",", copies.ToArray()) });
            args.AddRange(new[] { "--chapter-copy", "-o", output });
            return args;
        }

        private static MediaProbeInfo Probe(string executable, string path)
        {
            string json, error;
            if (!SharedFrucCore.Run(executable, SharedFrucCore.JoinArguments(new[] {
                "-v", "error", "-show_streams", "-show_format", "-of", "json", path }), 30000, out json, out error))
                throw new InvalidDataException("TrueHDR media probe failed: " + error);
            return MediaProbeInfo.Parse(path, json);
        }

        internal static BridgeExecutionRequest Prepare(FgsConfig config, string input, MediaProbeInfo source, TrueHdrOptions options)
        {
            input = Path.GetFullPath(input);
            string exe = NvencInterpolationCore.Exe(config.Get("NVENC_DIR"));
            string probe = Path.Combine(config.Get("FFMPEG_DIR"), "ffprobe.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("Configure NVEncC64.exe in FGS settings.", exe);
            if (!File.Exists(probe)) throw new FileNotFoundException("Configure ffprobe.exe in FGS settings.", probe);
            if (source == null)
            {
                source = Probe(probe, input);
                HevcVuiFallbackCore.TryRecover(source, input, probe);
            }
            if (source.Identity != null && !source.Identity.IsCurrent(input, probe))
                throw new IOException("TrueHDR source changed; reselect and probe it again: " + input);
            if (options.PreviewSeconds != 0)
            {
                double start, lengthSeconds;
                if (!SharedEncodePreviewCore.TryParseStart(options.PreviewStart, out start) ||
                    options.PreviewSeconds < 1 || options.PreviewSeconds > 86400 ||
                    (double.TryParse(source.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out lengthSeconds) &&
                        lengthSeconds > 0 && start >= lengthSeconds))
                    throw new InvalidDataException("Invalid TrueHDR preview start or duration.");
            }
            string directory = string.Equals(config.Get("OUTPUT_MODE"), "CUSTOM", StringComparison.OrdinalIgnoreCase) ? config.Get("OUTPUT_CUSTOM_DIR") : Path.GetDirectoryName(input);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
            var audio = new Dictionary<string, string> {
                { "FG_AUDIO_MODE", config.Get("AUDIO_MODE") }, { "FG_AUDIO_BITRATE", config.Get("AUDIO_BITRATE") }
            };
            string ext = options.Mp4 ? ".mp4" : ".mkv";
            string output = Path.Combine(directory, Path.GetFileNameWithoutExtension(input) + "_TrueHDR_" + options.Codec + "_" +
                NvencRateControlCore.FileToken(RateState(options), Bitrate(options, source)) + (options.PreviewSeconds > 0 ? "_Preview_" + (options.PreviewStart ?? "").Replace(":", "") +
                    "_" + Number(options.PreviewSeconds) + "s" : "") + ext);
            if (File.Exists(output)) throw new IOException("Output already exists: " + output);
            string staging = Path.Combine(directory, ".fgs-truehdr-" + Guid.NewGuid().ToString("N") + ext);
            var args = Arguments(input, staging, options, source, audio);
            bool faststart = options.Mp4 && config.GetBool("MP4_FASTSTART");
            double duration;
            double.TryParse(source.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            if (options.PreviewSeconds > 0) duration = options.PreviewSeconds;
            var stage = new BridgeExecutionStage {
                ToolPath = exe, WorkingDirectory = Path.GetDirectoryName(exe),
                DirectArguments = SharedFrucCore.JoinArguments(args), ProgressDurationSeconds = duration,
                StageCurrent = 1, StageTotal = 1, StageText = "SDR → TrueHDR → " + options.Codec + " HDR10",
                BeforeRunPreparation = delegate {
                    RequireTrueHdr(exe);
                    AudioOutputCore.EstimateMissing(input, probe, source, audio);
                    return "[TrueHDR] NGX and codec capability are validated by NVEncC initialization on the selected input.";
                }
            };
            var request = new BridgeExecutionRequest {
                ToolPath = exe, WorkingDirectory = stage.WorkingDirectory, DirectProcess = true,
                InputFiles = new[] { input }, DirectStages = new[] { stage }, SkipPreviewSpool = true,
                Environment = new Dictionary<string, string> { { "FG_MODE", "TrueHDR " + options.Codec } },
                OutputFiles = new List<string> { staging }, TemporaryFiles = new[] { staging },
                TrueHdrSourceFps = BitrateRecommendationCore.ParseMediaFps(source),
                ProgressDurationSeconds = duration, ExecutionNotice = "[TrueHDR] Output: " + output
            };
            var original = new FileInfo(input);
            long length = original.Length;
            DateTime modified = original.LastWriteTimeUtc;
            var preflight = stage.BeforeRunPreparation;
            stage.BeforeRunPreparation = delegate {
                var current = new FileInfo(input);
                if (!current.Exists || current.Length != length || current.LastWriteTimeUtc != modified)
                    throw new IOException("TrueHDR source changed: " + input);
                if (File.Exists(output)) throw new IOException("Output already exists: " + output);
                string notice = preflight();
                var prepared = Arguments(input, staging, options, source, audio);
                if (faststart) prepared.InsertRange(prepared.Count - 2, new[] { "--mux-option", "movflags:+faststart" });
                stage.DirectArguments = SharedFrucCore.JoinArguments(prepared);
                return notice;
            };
            request.PostRunValidation = delegate {
                var actual = Probe(probe, staging);
                if (actual.VideoCodec != options.Codec.ToLowerInvariant() || actual.PixelFormat != "yuv420p10le" ||
                    actual.ColorSpace != "bt2020nc" || actual.ColorTransfer != "smpte2084" || actual.ColorPrimaries != "bt2020" ||
                    actual.Width != source.Width || actual.Height != source.Height ||
                    AudioOutputCore.Tracks(actual).Count != AudioOutputCore.Tracks(source).Count)
                    return "TrueHDR output validation failed: video codec, size, HDR10 tags, or audio track count.";
                return null;
            };
            request.PostRunCommit = delegate { File.Move(staging, output); request.OutputFiles[0] = output; };
            return request;
        }

        internal static int RunCli(string[] args)
        {
            try
            {
                if (args.Length != 2 && args.Length != 3) throw new ArgumentException("Usage: --truehdr <input> [options.json]");
                var options = args.Length == 3 ? new JavaScriptSerializer().Deserialize<TrueHdrOptions>(File.ReadAllText(args[2])) : new TrueHdrOptions();
                var config = new FgsConfig(NativeStartupCore.FindAppRoot(AppDomain.CurrentDomain.BaseDirectory), null);
                config.Load();
                var request = Prepare(config, args[1], null, options);
                using (var task = new BridgeTaskCoordinator())
                using (var done = new ManualResetEvent(false))
                {
                    int code = 1;
                    task.LogLine += delegate(object sender, BridgeLogLineEventArgs e) { NativeStartupCore.WriteProtocolLine(e.Line); };
                    task.Completed += delegate(object sender, BridgeExecutionCompletedEventArgs e) {
                        if (e.Result.Exception != null) NativeStartupCore.WriteProtocolLine(e.Result.Exception.Message);
                        code = e.Result.Cancelled ? 2 : e.Result.ExitCode == 0 && e.Result.Exception == null ? 0 : 1;
                        done.Set();
                    };
                    ConsoleCancelEventHandler cancel = delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; task.Cancel(); };
                    Console.CancelKeyPress += cancel;
                    try { task.Start(new BridgeTaskRequest { ExecutionRequest = request }); done.WaitOne(); }
                    finally { Console.CancelKeyPress -= cancel; }
                    return code;
                }
            }
            catch (Exception error) { NativeStartupCore.WriteProtocolLine(error.Message); return 1; }
        }
    }
}
