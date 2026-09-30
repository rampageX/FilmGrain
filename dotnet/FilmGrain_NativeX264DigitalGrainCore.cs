using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace FilmGrainStudioPreview
{
    internal static class NativeX264DigitalGrainCore
    {
        private sealed class FramePlan
        {
            internal string Filter = "";
            internal string FileSuffix = "";
            internal int ActiveWidth;
            internal int ActiveHeight;
        }

        internal static bool TryPrepare(string appRoot, string input, MediaProbeInfo info, IDictionary<string,string> state, out BridgePreparedExecution prepared, out string reason)
        {
            prepared = null;
            reason = "";
            if (state == null || info == null) { reason = "missing state/media probe"; return false; }
            if (!Eq(state, "FG_MODE", "X264")) { reason = "codec is not x264"; return false; }
            if (Eq(state, "FG_GRAIN_ENGINE", "PROCEDURAL")) return TryPrepareEngine(appRoot, input, info, state, false, out prepared, out reason);
            if (Eq(state, "FG_GRAIN_ENGINE", "NATIVE")) return TryPrepareEngine(appRoot, input, info, state, true, out prepared, out reason);
            if (Eq(state, "FG_GRAIN_ENGINE", "FGSIM")) return TryPrepareEngine(appRoot, input, info, state, false, out prepared, out reason);
            reason = "grain engine is not migrated for native x264";
            return false;
        }

        private static bool TryPrepareEngine(string appRoot, string input, MediaProbeInfo info, IDictionary<string,string> state, bool plateMode, out BridgePreparedExecution prepared, out string reason)
        {
            prepared = null;
            reason = "";
            bool fgsimMode = Eq(state, "FG_GRAIN_ENGINE", "FGSIM");
            string passMode = Get(state, "FG_X264_PASS_MODE").ToUpperInvariant();
            if (passMode != "VBR1" && passMode != "2PASS" && passMode != "3PASS") { reason = "unsupported x264 pass mode"; return false; }
            bool mkv = Eq(state, "FG_CONTAINER", "MKV");
            if (!mkv && !Eq(state, "FG_CONTAINER", "MP4")) { reason = "unsupported native x264 container"; return false; }
            bool autoFps = Eq(state, "FG_FPS_MODE", "AUTO");
            if (!autoFps && !Eq(state, "FG_FPS_MODE", "SOURCE") && !Eq(state, "FG_FPS_MODE", "KEEP")) { reason = "unsupported native x264 FPS mode"; return false; }
            bool interpolate = Get(state, "FG_SVP_INTERPOLATE") == "1";
            bool fruc = interpolate && Eq(state, "FG_INTERPOLATION_ENGINE", "FRUC");
            bool svp = interpolate && !fruc;
            if (interpolate && !fruc && !Eq(state, "FG_INTERPOLATION_ENGINE", "SVP") && !string.IsNullOrEmpty(Get(state, "FG_INTERPOLATION_ENGINE")))
            { reason = "unsupported interpolation engine"; return false; }
            if (interpolate && info.IsInterlaced) { reason = "Native x264 interpolation requires progressive input / Native x264 插帧需要逐行源视频"; return false; }
            if (svp && (Path.GetExtension(input).Equals(".ts", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(input).Equals(".m2ts", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(input).Equals(".mts", StringComparison.OrdinalIgnoreCase)))
            { reason = "OpenSVPFlow Native requires MP4/MKV input; remux transport streams first / 请先将 TS/M2TS/MTS 无损封装为 MP4/MKV"; return false; }
            if (Get(state, "FG_UPLOAD") == "1") { reason = "upload copy is not migrated"; return false; }
            bool hdrToSdr = info.IsHdr && !Eq(state, "FG_HDR_POLICY", "PRESERVE");
            if (info.IsHdr && !hdrToSdr) { reason = "HDR preservation is unavailable for x264"; return false; }
            if (hdrToSdr && info.IsInterlaced) { reason = "interlaced HDR requires legacy intermediate probe"; return false; }
            string tonemap = Get(state, "FG_TONEMAP_ALGO").ToLowerInvariant();
            if (tonemap != "mobius" && tonemap != "reinhard" && tonemap != "gamma" && tonemap != "linear" && tonemap != "clip") tonemap = "hable";
            string tonemapLabel = char.ToUpperInvariant(tonemap[0]) + tonemap.Substring(1);
            // Match Legacy Bridge: interlaced input only enters the deinterlace route when UI selects AUTO.
            // "OFF" keeps the original field-encoded source path without BWDIF/W3FDIF conversion.
            bool deinterlace = info.IsInterlaced && Eq(state, "FG_DEINTERLACE", "AUTO");
            bool bwdifCuda = deinterlace && Eq(state, "FG_DEINT_METHOD", "BWDIF_CUDA");
            bool bwdifVulkan = deinterlace && Eq(state, "FG_DEINT_METHOD", "BWDIF_VULKAN");
            if (info.IsInterlaced && Eq(state, "FG_DEINTERLACE", "AUTO") && (!Eq(state, "FG_DEINT_METHOD", "W3FDIF") && !bwdifCuda && !bwdifVulkan)) { reason = "selected interlaced route remains on legacy backend"; return false; }
            bool high10 = Get(state, "FG_H264_HIGH10") == "1";
            if (info.Width <= 0 || info.Height <= 0) { reason = "invalid dimensions"; return false; }

            long bitrate;
            if (!long.TryParse(Get(state, "FG_BITRATE"), NumberStyles.Integer, CultureInfo.InvariantCulture, out bitrate) || bitrate <= 0) { reason = "invalid bitrate"; return false; }
            string fps = info.AvgFrameRate;
            if (string.IsNullOrWhiteSpace(fps) || fps == "0/0") { reason = "invalid source FPS"; return false; }
            string fpsFilter = "", fpsSuffix = "";
            if (deinterlace)
            {
                if (!TryDoubleSourceFps(fps, out fps)) { reason = "invalid field-rate FPS"; return false; }
            }
            else if (interpolate)
            {
                InterpolationFrameRate targetRate; string rateError;
                if (!InterpolationFrameRateCore.TryParse(Get(state, "FG_INTERPOLATION_TARGET_FPS"), info.AvgFrameRate, out targetRate, out rateError))
                { reason = "invalid interpolation target FPS: " + rateError; return false; }
                fps = targetRate.Rational;
                fpsSuffix = (fruc ? "_FRUC" : "_SVP") + targetRate.Label;
            }
            else if (autoFps && !TryResolveAutoFps(fps, out fps, out fpsFilter, out fpsSuffix)) { reason = "invalid automatic FPS"; return false; }
            if (interpolate) autoFps = true;
            string deinterlaceFilter = bwdifVulkan ? "format=p010le,hwupload,bwdif_vulkan=mode=send_field:parity=auto:deint=all,hwdownload,format=p010le," : bwdifCuda ? "format=p010le,hwupload_cuda=device=0,bwdif_cuda=mode=send_field:parity=auto:deint=all,hwdownload,format=p010le," : deinterlace ? "w3fdif=filter=complex:mode=field:parity=auto:deint=all," : "";
            string deinterlaceSuffix = bwdifVulkan ? "_DI_BWV" : bwdifCuda ? "_DI_BWC" : deinterlace ? "_DI_W3F" : "";
            double durationSeconds = 0.0;
            double.TryParse(info.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out durationSeconds);
            if (plateMode && durationSeconds <= 0.0) { reason = "invalid source duration for Grain Plate loop bound"; return false; }
            string duration = durationSeconds > 0.0 ? durationSeconds.ToString("0.######", CultureInfo.InvariantCulture) : "";

            string ffmpeg = Path.Combine(GetFfmpegDir(appRoot), "ffmpeg.exe");
            string ffprobe = Path.Combine(GetFfmpegDir(appRoot), "ffprobe.exe");
            if (fruc && Get(state, "FG_FRUC_USE_MAIN_FFMPEG") == "0" && string.IsNullOrWhiteSpace(Get(state, "FG_FRUC_FFMPEG_PATH")))
            { reason = "custom FRUC FFmpeg directory is empty"; return false; }
            if (fruc && !string.IsNullOrWhiteSpace(Get(state, "FG_FRUC_FFMPEG_PATH"))) ffmpeg = Get(state, "FG_FRUC_FFMPEG_PATH");
            if (!File.Exists(ffmpeg)) { reason = "ffmpeg.exe not found: " + ffmpeg; return false; }
            if (fruc && !NativeHevcCore.HasFrucFilter(ffmpeg)) { reason = "selected FFmpeg lacks fruc_vulkan: " + ffmpeg; return false; }
            string vspipe = Path.Combine(appRoot, "_OpenSVPFlow", ".venv", "Scripts", "vspipe.exe");
            string vpy = Path.Combine(appRoot, "_OpenSVPFlow", "FilmGrain_OpenSVPFlow.vpy");
            string svpPlugins = Path.Combine(appRoot, "_OpenSVPFlow", "Plugins");
            if (svp && (!File.Exists(vspipe) || !File.Exists(vpy) || !File.Exists(Path.Combine(svpPlugins, "svpflow1_vs.dll")) || !File.Exists(Path.Combine(svpPlugins, "svpflow2_vs.dll"))))
            { reason = "OpenSVPFlow dependencies are missing"; return false; }
            string fgsimHook = "", fgsimTag = "";
            if (fgsimMode)
            {
                string fgsimPreset = Get(state, "FG_FGSIM_PRESET").ToUpperInvariant();
                if (fgsimPreset != "LIGHT" && fgsimPreset != "MEDIUM" && fgsimPreset != "HEAVY") { reason = "unsupported FGSIM preset"; return false; }
                fgsimTag = fgsimPreset == "LIGHT" ? "L" : fgsimPreset == "HEAVY" ? "H" : "M";
                fgsimHook = Path.Combine(appRoot, "Utils", "_FilmGrainSimplified", "Generated", "FilmGrainSimplified_" + fgsimPreset + ".hook");
                if (!TryPrepareFgsimHook(fgsimHook, fgsimPreset, out reason)) return false;
            }
            if (high10 && !SupportsHigh10(ffmpeg)) { reason = "this libx264 build does not advertise yuv420p10le"; return false; }

            FramePlan frame;
            if (!TryBuildFramePlan(info, state, out frame, out reason)) return false;
            bool highMotion = Get(state, "FG_HIGH_MOTION") == "1";
            if (Eq(state, "FG_BITRATE_MODE", "AUTO"))
            {
                double rateFps = ParseFps(fps);
                bitrate = BitrateRecommendationCore.GetRecommendedBitrate(2, frame.ActiveWidth, frame.ActiveHeight, rateFps, highMotion);
            }

            string colorFilter, colorSuffix;
            if (!TryBuildColorFilter(state, out colorFilter, out colorSuffix, out reason)) return false;
            string lutNamingSuffix = BuildLutNamingSuffix(state);
            string preset = Get(state, "FG_X264_PRESET").ToLowerInvariant();
            if (preset != "medium" && preset != "slow") preset = "faster";
            string x264Suffix = BuildX264FileSuffix(preset, passMode);

            string grainInput = "";
            bool grainScale = false;
            string grainTag = "";
            string grainOpacity = "";
            int plateStrengthPercent = 85;
            int procStrength = 55;
            string procMask = "0.55";
            if (plateMode)
            {
                string plate = Get(state, "FG_HEVC_GRAIN_PATH");
                if (string.IsNullOrWhiteSpace(plate) || !File.Exists(plate)) { reason = "selected Grain Plate is missing: " + plate; return false; }
                grainInput = plate;
                grainScale = true;
                string stem = Path.Combine(Path.GetDirectoryName(plate), Path.GetFileNameWithoutExtension(plate));
                string cache1080 = stem + "_1080p_HEVC_Lossless.mkv";
                string cache4k = stem + "_HEVC_Lossless.mkv";
                if (info.Width <= 1920 && info.Height <= 1080 && File.Exists(cache1080)) { grainInput = cache1080; grainScale = !(info.Width == 1920 && info.Height == 1080); }
                else if (File.Exists(cache4k)) { grainInput = cache4k; grainScale = true; }
                int strengthSel;
                if (!int.TryParse(Get(state, "FG_HEVC_STRENGTH_SEL"), NumberStyles.Integer, CultureInfo.InvariantCulture, out strengthSel)) strengthSel = 3;
                plateStrengthPercent = strengthSel == 1 ? 65 : strengthSel == 2 ? 75 : strengthSel == 4 ? 100 : 85;
                grainOpacity = strengthSel == 1 ? "0.65" : strengthSel == 2 ? "0.75" : strengthSel == 4 ? "1.00" : "0.85";
                grainTag = ResolvePlateTag(plate, Get(state, "FG_HEVC_GRAIN_TAG"));
            }
            else
            {
                if (!int.TryParse(Get(state, "FG_PROC_STRENGTH"), NumberStyles.Integer, CultureInfo.InvariantCulture, out procStrength)) procStrength = 55;
                procStrength = Math.Max(10, Math.Min(100, procStrength));
                procMask = procStrength == 100 ? "1.00" : "0." + procStrength.ToString("00", CultureInfo.InvariantCulture);
            }

            string outDir = ResolveOutputDirectory(input, state);
            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex) { reason = "cannot create output directory: " + ex.Message; return false; }

            string subSuffix = Get(state, "FG_SUBTITLE") == "1" ? "_SUB" : "";
            string grainFileName = plateMode ? "_FG_" + grainTag + "_STR" + plateStrengthPercent.ToString(CultureInfo.InvariantCulture) : fgsimMode ? "_FG_FGSIM_" + fgsimTag : "_FG_DG" + procStrength.ToString(CultureInfo.InvariantCulture);
            string output = Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + "_X264" + x264Suffix + "_" + bitrate.ToString(CultureInfo.InvariantCulture) + "k" + (high10 ? "_HIGH10" : "") + fpsSuffix + deinterlaceSuffix + frame.FileSuffix + grainFileName + colorSuffix + lutNamingSuffix + (hdrToSdr ? "_SDR_" + tonemapLabel : "") + subSuffix + (mkv ? ".mkv" : ".mp4"));
            if (File.Exists(output)) { reason = "native output already exists: " + output; return false; }

            string lutCompatPath = "", lutCompatName = "", lutOpacity = "";
            if (!TryPrepareLut(ffmpeg, state, out lutCompatPath, out lutCompatName, out lutOpacity, out reason)) return false;
            string subtitleAss = "";
            if (!NativeSubtitlePrepareCore.TryPrepare(ffmpeg, ffprobe, input, state, frame.ActiveWidth, frame.ActiveHeight, out subtitleAss, out reason))
            {
                TryDelete(lutCompatPath);
                return false;
            }

            string passlog = "";
            List<string> temporaryFiles = new List<string>();
            if (!string.IsNullOrEmpty(lutCompatPath)) temporaryFiles.Add(lutCompatPath);
            if (!string.IsNullOrEmpty(subtitleAss)) temporaryFiles.Add(subtitleAss);
            try
            {
                string subtitleFilter = string.IsNullOrEmpty(subtitleAss) ? "" : ",format=yuv420p10le,subtitles=filename='" + EscapeFilterPath(subtitleAss) + "',format=p010le";
                string filter;
                if (plateMode)
                {
                    string grain = "[1:v:0]fps=" + fps + ",format=p010le,setpts=PTS-STARTPTS,hwupload" + (grainScale ? ",scale_vulkan=w=" + info.Width.ToString(CultureInfo.InvariantCulture) + ":h=" + info.Height.ToString(CultureInfo.InvariantCulture) + ":scaler=bilinear" : "") + "[grainvk]";
                    string baseFilter;
                    if (!string.IsNullOrEmpty(lutCompatName))
                        baseFilter = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=gbrp16le,setpts=PTS-STARTPTS,split=2[lutorig][lutsrc];[lutsrc]lut3d=file='" + EscapeFilterPath(lutCompatName) + "':interp=tetrahedral[lutgraded];[lutgraded][lutorig]blend=all_mode=normal:all_opacity=" + lutOpacity + ",format=p010le,hwupload[basevk]";
                    else
                        baseFilter = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=p010le,setpts=PTS-STARTPTS,hwupload[basevk]";
                    filter = baseFilter + ";" + grain + ";[basevk][grainvk]blend_vulkan=all_mode=overlay:all_opacity=" + grainOpacity + ",hwdownload,format=p010le" + frame.Filter + subtitleFilter + ",format=" + (high10 ? "yuv420p10le" : "yuv420p") + "[vout]";
                }
                else if (fgsimMode)
                {
                    string baseFilter;
                    if (!string.IsNullOrEmpty(lutCompatName))
                        baseFilter = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=gbrp16le,setpts=PTS-STARTPTS,split=2[lutorig][lutsrc];[lutsrc]lut3d=file='" + EscapeFilterPath(lutCompatName) + "':interp=tetrahedral[lutgraded];[lutgraded][lutorig]blend=all_mode=normal:all_opacity=" + lutOpacity + ",format=yuv420p[fgsimbase]";
                    else
                        baseFilter = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=yuv420p[fgsimbase]";
                    filter = baseFilter + ";[fgsimbase]hwupload,libplacebo=format=yuv420p:custom_shader_path=" + EscapeFgsimShaderPath(fgsimHook) + ",hwdownload,format=yuv420p" + frame.Filter + subtitleFilter + ",format=" + (high10 ? "yuv420p10le" : "yuv420p") + "[vout]";
                }
                else
                {
                    int pw = Even((info.Width * 4 + 2) / 3);
                    int ph = Even((info.Height * 4 + 2) / 3);
                    string baseFilter;
                    if (!string.IsNullOrEmpty(lutCompatName))
                        baseFilter = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=gbrp16le,setpts=PTS-STARTPTS,split=2[lutorig][lutsrc];[lutsrc]lut3d=file='" + EscapeFilterPath(lutCompatName) + "':interp=tetrahedral[lutgraded];[lutgraded][lutorig]blend=all_mode=normal:all_opacity=" + lutOpacity + ",format=yuv420p[procbase]";
                    else
                        baseFilter = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=yuv420p[procbase]";
                    filter = baseFilter + ";[procbase]split=4[seed][masksrc][base][blacksrc];[seed]scale=" + pw.ToString(CultureInfo.InvariantCulture) + ":" + ph.ToString(CultureInfo.InvariantCulture) + ",lutyuv=y=128:u=128:v=128,noise=c0s=100:c0f=t+u,deflate=threshold0=15,dilation=threshold0=10,eq=contrast=3,scale=" + info.Width.ToString(CultureInfo.InvariantCulture) + ":" + info.Height.ToString(CultureInfo.InvariantCulture) + "[n];[masksrc]lutyuv=y='" + procMask + "*(182-abs(75-val))':u=128:v=128[o];[n][o]blend=c0_mode=multiply,negate[a];[base][a]alphamerge[c];[blacksrc]drawbox=color=black:t=fill[black];[black][c]overlay=shortest=1" + frame.Filter + subtitleFilter + ",format=" + (high10 ? "yuv420p10le" : "yuv420p") + "[vout]";
                }

                if (fruc)
                {
                    string perf = Get(state, "FG_FRUC_PERF").ToLowerInvariant();
                    if (perf != "slow" && perf != "medium" && perf != "fast") { reason = "unsupported FRUC perf setting"; return false; }
                    string grid = Get(state, "FG_FRUC_GRID").ToLowerInvariant();
                    if (grid != "auto" && grid != "1" && grid != "2" && grid != "4" && grid != "8") { reason = "unsupported FRUC grid setting"; return false; }
                    filter = filter.Replace("[0:v:0]", "[0:v:0]format=nv12,hwupload,fruc_vulkan=fps=" + fps + ":perf=" + perf + ":grid=" + grid + ",hwdownload,format=nv12,");
                }
                if (svp && plateMode) filter = filter.Replace("[1:v:0]", "[2:v:0]");

                long max = bitrate * 3L;
                long buf = bitrate * 6L;
                string workingDirectory = string.IsNullOrEmpty(lutCompatPath) ? Path.GetDirectoryName(input) : Path.GetDirectoryName(lutCompatPath);
                List<string> requestInputs = new List<string>(); requestInputs.Add(input); if (plateMode) requestInputs.Add(grainInput);
                BridgeExecutionRequest request = new BridgeExecutionRequest();
                request.ToolPath = ffmpeg;
                request.WorkingDirectory = workingDirectory;
                request.InputFiles = requestInputs;
                request.Environment = new Dictionary<string,string>(state, StringComparer.OrdinalIgnoreCase);
                if (svp && File.Exists(Path.Combine(appRoot, "_OpenSVPFlow", "_UserConfig", "vapoursynth", "vapoursynth.toml")))
                    request.Environment["APPDATA"] = Path.Combine(appRoot, "_OpenSVPFlow", "_UserConfig");
                request.DirectProcess = true;
                request.OutputFiles = new List<string>{ output };
                request.ProgressDurationSeconds = durationSeconds > 0.0 ? durationSeconds : 0.0;

                string encodeInput = input;
                List<BridgeExecutionStage> stages = new List<BridgeExecutionStage>();
                int stageOffset = hdrToSdr ? 1 : 0;
                int stageTotal = stageOffset + (passMode == "VBR1" ? 1 : passMode == "3PASS" ? 3 : 2);
                if (hdrToSdr)
                {
                    string tempRoot;
                    if (!TryResolveTempRoot(input, state, out tempRoot, out reason)) return false;
                    encodeInput = Path.Combine(tempRoot, "__FGS_HDR2SDR_" + Guid.NewGuid().ToString("N") + ".mkv");
                    temporaryFiles.Add(encodeInput);
                    stages.Add(MakeStage(ffmpeg, workingDirectory, BuildHdrToSdrArgs(input, encodeInput, info, tonemap), durationSeconds, 1, stageTotal, "HDR to SDR / " + tonemapLabel));
                }
                if (passMode == "VBR1")
                {
                    List<string> args = BuildEncodeArgs(encodeInput, grainInput, filter, output, preset, highMotion, bitrate, max, buf, fps, duration, plateMode, autoFps, high10, mkv, deinterlace, 0, "", true, svp);
                    if (hdrToSdr || svp)
                    {
                        stages.Add(MakeStage(ffmpeg, workingDirectory, args, durationSeconds, stageOffset + 1, stageTotal, "x264 final encode"));
                        request.DirectStages = stages;
                    }
                    else request.DirectArguments = JoinArgs(args);
                }
                else
                {
                    string tempRoot;
                    if (!TryResolveTempRoot(input, state, out tempRoot, out reason)) return false;
                    passlog = Path.Combine(tempRoot, (plateMode ? "__FGS_X264_" : fgsimMode ? "__FGS_X264_FGSIM_" : "__FGS_X264_PROC_") + Guid.NewGuid().ToString("N"));
                    AddPasslogTemps(temporaryFiles, passlog);
                    stages.Add(MakeStage(ffmpeg, workingDirectory, BuildEncodeArgs(encodeInput, grainInput, filter, output, preset, highMotion, bitrate, max, buf, fps, duration, plateMode, autoFps, high10, mkv, deinterlace, 1, passlog, false, svp), durationSeconds, stageOffset + 1, stageTotal, plateMode ? "pass 1: Grain Plate analysis" : fgsimMode ? "pass 1: FGSIM analysis" : "pass 1: Digital Grain analysis"));
                    if (passMode == "3PASS")
                        stages.Add(MakeStage(ffmpeg, workingDirectory, BuildEncodeArgs(encodeInput, grainInput, filter, output, preset, highMotion, bitrate, max, buf, fps, duration, plateMode, autoFps, high10, mkv, deinterlace, 3, passlog, false, svp), durationSeconds, stageOffset + 2, stageTotal, "pass 3: stats refinement"));
                    stages.Add(MakeStage(ffmpeg, workingDirectory, BuildEncodeArgs(encodeInput, grainInput, filter, output, preset, highMotion, bitrate, max, buf, fps, duration, plateMode, autoFps, high10, mkv, deinterlace, 2, passlog, true, svp), durationSeconds, stageTotal, stageTotal, plateMode ? "pass 2: Grain Plate final encode" : fgsimMode ? "pass 2: FGSIM final encode" : "pass 2: Digital Grain final encode"));
                    request.DirectStages = stages;
                }

                if (svp)
                    for (int i = stageOffset; i < stages.Count; i++)
                        AttachSvpProducer(stages[i], vspipe, vpy, svpPlugins, encodeInput, state, fps);

                request.TemporaryFiles = temporaryFiles.Count == 0 ? null : temporaryFiles;
                prepared = new BridgePreparedExecution(request, false, ffmpeg, requestInputs);
                lutCompatPath = "";
                subtitleAss = "";
                return true;
            }
            finally
            {
                if (!string.IsNullOrEmpty(lutCompatPath)) TryDelete(lutCompatPath);
                if (!string.IsNullOrEmpty(subtitleAss)) TryDelete(subtitleAss);
            }
        }

        // StudioBridge :DOUBLE_SOURCE_FPS: preserve the ffprobe denominator.
        private static bool TryDoubleSourceFps(string source, out string doubled)
        {
            doubled = "";
            string[] parts = source.Split('/');
            if (parts.Length < 1 || parts.Length > 2) return false;
            long numerator, denominator = 1;
            if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out numerator) || numerator <= 0 || numerator > long.MaxValue / 2) return false;
            if (parts.Length == 2 && (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out denominator) || denominator <= 0)) return false;
            doubled = (numerator * 2).ToString(CultureInfo.InvariantCulture);
            if (denominator != 1) doubled += "/" + denominator.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool SupportsHigh10(string ffmpeg)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ffmpeg, "-hide_banner -h encoder=libx264");
                psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                using (Process process = new Process())
                {
                    process.StartInfo = psi; process.Start();
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(5000)) { try { process.Kill(); } catch { } return false; }
                    return process.ExitCode == 0 && (stdout + stderr).IndexOf("yuv420p10le", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        private static double ParseFps(string value)
        {
            string[] parts = value.Split('/');
            double n, d = 1.0;
            if (parts.Length < 1 || parts.Length > 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return 0.0;
            if (parts.Length == 2 && !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return 0.0;
            return d > 0.0 && n > 0.0 ? n / d : 0.0;
        }

        // Matches StudioBridge :AUTO_CINEMA_FPS, including its exact-rate cases and 0.25 tolerance.
        private static bool TryResolveAutoFps(string source, out string target, out string filter, out string suffix)
        {
            target = source; filter = ""; suffix = "";
            double rate = ParseFps(source);
            if (rate <= 0.0 || double.IsNaN(rate) || double.IsInfinity(rate)) return false;
            bool same23976 = source == "24000/1001";
            bool same24 = source == "24/1" || source == "24";
            bool family23976 = same23976 || source == "30000/1001" || source == "48000/1001" || source == "60000/1001" || source == "120000/1001";
            bool family24 = same24 || source == "25/1" || source == "25" || source == "30/1" || source == "30" || source == "48/1" || source == "48" || source == "50/1" || source == "50" || source == "60/1" || source == "60" || source == "100/1" || source == "100" || source == "120/1" || source == "120";
            if (!family23976 && !family24)
            {
                double best = double.MaxValue;
                foreach (double known in new double[]{23.976023976,29.970029970,47.952047952,59.940059940,119.880119880})
                { double distance = Math.Abs(rate - known); if (distance < best) { best = distance; family23976 = true; family24 = false; } }
                foreach (double known in new double[]{24,25,30,48,50,60,100,120})
                { double distance = Math.Abs(rate - known); if (distance < best) { best = distance; family23976 = false; family24 = true; } }
                if (best > 0.25) { family23976 = false; family24 = false; }
            }
            if (family23976) { target = "24000/1001"; suffix = "_23976p"; if (!same23976) filter = "fps=24000/1001,"; }
            else if (family24) { target = "24"; suffix = "_24p"; if (!same24) filter = "fps=24,"; }
            return true;
        }

        private static List<string> BuildHdrToSdrArgs(string input, string output, MediaProbeInfo info, string tonemap)
        {
            string range = string.Equals(info.ColorRange, "pc", StringComparison.OrdinalIgnoreCase) ? "pc" : "tv";
            string primaries = string.IsNullOrWhiteSpace(info.ColorPrimaries) || info.ColorPrimaries == "unknown" || info.ColorPrimaries == "reserved" ? "bt2020" : info.ColorPrimaries;
            string space = string.IsNullOrWhiteSpace(info.ColorSpace) || info.ColorSpace == "unknown" || info.ColorSpace == "reserved" ? "bt2020nc" : info.ColorSpace;
            string filter = "zscale=rin=" + range + ":pin=" + primaries + ":tin=" + info.ColorTransfer + ":min=" + space + ":t=linear:npl=100,format=gbrpf32le,tonemap=tonemap=" + tonemap + ":desat=2,zscale=p=bt709:t=bt709:m=bt709:r=tv,format=p010le,sidedata=mode=delete:type=MASTERING_DISPLAY_METADATA,sidedata=mode=delete:type=CONTENT_LIGHT_LEVEL,setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709";
            return new List<string>{"-hide_banner","-stats","-progress","pipe:2","-stats_period","0.5","-y","-copyts","-start_at_zero","-i",input,"-map","0:v:0","-map","0:a?","-map","0:s?","-map","0:t?","-map_metadata","0","-map_chapters","0","-vf",filter,"-c:v","hevc_nvenc","-profile:v","main10","-pix_fmt","p010le","-preset","p4","-tune","lossless","-fps_mode","passthrough","-color_range","tv","-color_primaries","bt709","-color_trc","bt709","-colorspace","bt709","-c:a","copy","-c:s","copy","-c:t","copy",output};
        }

        private static List<string> BuildEncodeArgs(string input, string grainInput, string filter, string output, string preset, bool highMotion, long bitrate, long max, long buf, string fps, string duration, bool plateMode, bool autoFps, bool high10, bool mkv, bool deinterlace, int passNumber, string passlog, bool finalOutput, bool svp)
        {
            List<string> a = new List<string>{"-hide_banner","-stats","-progress","pipe:2","-stats_period","0.5","-y"};
            if (plateMode || filter.Contains("libplacebo=") || filter.Contains("fruc_vulkan=")) { a.Add("-init_hw_device"); a.Add("vulkan=vk:0"); a.Add("-filter_hw_device"); a.Add("vk"); }
            else if (filter.Contains("bwdif_vulkan=")) { a.Add("-init_hw_device"); a.Add("vulkan=deintvk:0"); a.Add("-filter_hw_device"); a.Add("deintvk"); }
            a.Add("-i"); a.Add(input);
            if (plateMode)
            {
                a.Add("-stream_loop"); a.Add("-1");
                a.Add("-t"); a.Add(duration);
                a.Add("-i"); a.Add(grainInput);
            }
            a.Add("-filter_complex"); a.Add(filter);
            a.Add("-map"); a.Add("[vout]");
            if (finalOutput)
            {
                a.Add("-map"); a.Add("0:a?");
                if (mkv) { a.Add("-map"); a.Add("0:s?"); a.Add("-map"); a.Add("0:t?"); }
                a.Add("-map_metadata"); a.Add("0"); a.Add("-map_chapters"); a.Add("0");
            }
            else a.Add("-an");
            a.Add("-c:v"); a.Add("libx264"); a.Add("-profile:v"); a.Add(high10 ? "high10" : "high"); a.Add("-pix_fmt"); a.Add(high10 ? "yuv420p10le" : "yuv420p");
            a.Add("-preset"); a.Add(preset); a.Add("-tune"); a.Add("grain");
            if (highMotion) { a.Add("-b_strategy"); a.Add("2"); }
            a.Add("-b:v"); a.Add(bitrate.ToString(CultureInfo.InvariantCulture) + "k");
            a.Add("-maxrate"); a.Add(max.ToString(CultureInfo.InvariantCulture) + "k");
            a.Add("-bufsize"); a.Add(buf.ToString(CultureInfo.InvariantCulture) + "k");
            if (passNumber > 0)
            {
                a.Add("-pass"); a.Add(passNumber.ToString(CultureInfo.InvariantCulture));
                a.Add("-passlogfile"); a.Add(passlog);
            }
            if (autoFps || deinterlace || plateMode || passNumber > 0) { a.Add("-r"); a.Add(fps); }
            a.Add("-fps_mode:v"); a.Add("cfr");
            if (plateMode && !string.IsNullOrEmpty(duration)) { a.Add("-t"); a.Add(duration); }
            else if (passNumber > 0 && !string.IsNullOrEmpty(duration)) { a.Add("-t"); a.Add(duration); }
            if (finalOutput)
            {
                if (mkv) { a.Add("-c:a"); a.Add("copy"); a.Add("-c:s"); a.Add("copy"); a.Add("-c:t"); a.Add("copy"); }
                else { a.Add("-c:a"); a.Add("aac"); a.Add("-b:a"); a.Add("256k"); a.Add("-movflags"); a.Add("+faststart"); }
                a.Add(output);
            }
            else { a.Add("-f"); a.Add("null"); a.Add("NUL"); }
            if (svp) ConvertToPipeInput(a, input);
            return a;
        }

        private static void ConvertToPipeInput(List<string> args, string input)
        {
            int index = args.IndexOf("-i");
            if (index < 0 || index + 1 >= args.Count || args[index + 1] != input)
                throw new InvalidOperationException("OpenSVPFlow x264 input mapping changed");
            args.InsertRange(index, new string[]{"-f", "yuv4mpegpipe"});
            args[index + 3] = "pipe:0";
            args.InsertRange(index + 4, new string[]{"-i", input});
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i] == "0:a?") args[i] = "1:a?";
                if (args[i] == "0:s?") args[i] = "1:s?";
                if (args[i] == "0:t?") args[i] = "1:t?";
                if (args[i] == "-map_metadata" || args[i] == "-map_chapters")
                    if (i + 1 < args.Count && args[i + 1] == "0") args[i + 1] = "1";
            }
        }

        private static void AttachSvpProducer(BridgeExecutionStage stage, string vspipe, string vpy, string plugins, string input, IDictionary<string,string> state, string outputFps)
        {
            string algo = Get(state, "FG_SVP_ALGO"), analyse = Get(state, "FG_SVP_ANALYSE");
            string scene = Get(state, "FG_SVP_SCENE_MODE"), mask = Get(state, "FG_SVP_MASK_AREA");
            int n;
            if (!int.TryParse(algo, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ||
                (n != 1 && n != 2 && n != 11 && n != 13 && n != 21 && n != 22 && n != 23)) algo = "13";
            if (analyse != "BASE" && analyse != "ENCODEGUI") analyse = "ENCODEGUI";
            if (scene != "0" && scene != "3") scene = "0";
            if (!int.TryParse(mask, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < 0 || n > 100) mask = "100";
            stage.PipeProducerPath = vspipe;
            stage.PipeProducerArguments = JoinArgs(new string[]{"--progress", "-c", "y4m", "--arg", "input=" + input,
                "--arg", "plugin_dir=" + plugins, "--arg", "target_num=" + outputFps.Split('/')[0], "--arg", "target_den=" + (outputFps.Contains("/") ? outputFps.Split('/')[1] : "1"),
                "--arg", "algo=" + algo, "--arg", "analyse_profile=" + analyse,
                "--arg", "scene_mode=" + scene, "--arg", "mask_area=" + mask, vpy, "-"});
        }

        private static BridgeExecutionStage MakeStage(string ffmpeg, string workingDirectory, IList<string> args, double duration, int current, int total, string text)
        {
            BridgeExecutionStage stage = new BridgeExecutionStage();
            stage.ToolPath = ffmpeg;
            stage.WorkingDirectory = workingDirectory;
            stage.DirectArguments = JoinArgs(args);
            stage.ProgressDurationSeconds = duration > 0.0 ? duration : 0.0;
            stage.StageCurrent = current;
            stage.StageTotal = total;
            stage.StageText = text;
            return stage;
        }

        private static bool TryBuildFramePlan(MediaProbeInfo info, IDictionary<string,string> state, out FramePlan plan, out string reason)
        {
            plan = new FramePlan();
            plan.ActiveWidth = info.Width;
            plan.ActiveHeight = info.Height;
            reason = "";
            if (Get(state, "FG_CINEMATIC_FRAME") != "1") return true;
            int custom = 0;
            int.TryParse(Get(state, "FG_CROP_PER_SIDE"), NumberStyles.Integer, CultureInfo.InvariantCulture, out custom);
            if (custom < 0) custom = 0;
            string mode = Get(state, "FG_FRAME_MODE").ToUpperInvariant();
            if (mode == "CROP")
            {
                plan.FileSuffix = custom > 0 ? "_CROP" + custom.ToString(CultureInfo.InvariantCulture) : "_239";
                if (custom > 0)
                {
                    int target = info.Height - custom * 2;
                    if (target <= 1) { reason = "custom crop is too large for input height"; return false; }
                    plan.ActiveHeight = target;
                    plan.Filter = ",crop=w=iw:h=" + target.ToString(CultureInfo.InvariantCulture) + ":x=0:y=" + custom.ToString(CultureInfo.InvariantCulture) + ":exact=1";
                    return true;
                }
                long aspectLeft = (long)info.Width * 100L;
                long aspectRight = (long)info.Height * 239L;
                if (aspectLeft >= aspectRight) return true;
                int targetH = (int)(((long)info.Width * 100L + 239L) / 478L) * 2;
                if (targetH >= info.Height || targetH <= 0) return true;
                int cropY = (info.Height - targetH) / 2; cropY = (cropY / 2) * 2;
                plan.ActiveHeight = targetH;
                plan.Filter = ",crop=w=iw:h=" + targetH.ToString(CultureInfo.InvariantCulture) + ":x=0:y=" + cropY.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            plan.FileSuffix = custom > 0 ? "_BARS" + custom.ToString(CultureInfo.InvariantCulture) : "_239LB";
            if (custom > 0)
            {
                int content = info.Height - custom * 2;
                if (content <= 1) { reason = "custom black bars are too large for input height"; return false; }
                plan.Filter = ",drawbox=x=0:y=0:w=iw:h=" + custom.ToString(CultureInfo.InvariantCulture) + ":color=black:t=fill,drawbox=x=0:y=ih-" + custom.ToString(CultureInfo.InvariantCulture) + ":w=iw:h=" + custom.ToString(CultureInfo.InvariantCulture) + ":color=black:t=fill";
                return true;
            }
            if ((long)info.Width * 100L >= (long)info.Height * 239L) return true;
            int barRaw = (int)(((long)info.Height * 239L - (long)info.Width * 100L) / 478L);
            int barH = (barRaw / 2) * 2;
            if (barH <= 0) return true;
            plan.Filter = ",drawbox=x=0:y=0:w=iw:h=" + barH.ToString(CultureInfo.InvariantCulture) + ":color=black:t=fill,drawbox=x=0:y=ih-" + barH.ToString(CultureInfo.InvariantCulture) + ":w=iw:h=" + barH.ToString(CultureInfo.InvariantCulture) + ":color=black:t=fill";
            return true;
        }

        private static string BuildX264FileSuffix(string preset, string passMode)
        {
            string suffix = preset == "medium" ? "_MEDIUM" : preset == "slow" ? "_SLOW" : "";
            if (passMode == "2PASS") suffix += "_2PASS";
            else if (passMode == "3PASS") suffix += "_3PASS";
            return suffix;
        }

        private static bool TryResolveTempRoot(string input, IDictionary<string,string> state, out string root, out string reason)
        {
            root = "";
            reason = "";
            string mode = Get(state, "FG_TEMP_MODE").ToUpperInvariant();
            if (mode == "SYSTEM") root = Path.Combine(Path.GetTempPath(), "FilmGrain_Studio");
            else if (mode == "CUSTOM")
            {
                string custom = Get(state, "FG_TEMP_CUSTOM_DIR");
                if (string.IsNullOrWhiteSpace(custom)) { reason = "custom temporary directory is not configured"; return false; }
                root = Path.Combine(custom, "FilmGrain_Studio");
            }
            else root = Path.GetDirectoryName(input);
            try { Directory.CreateDirectory(root); }
            catch (Exception ex) { reason = "could not create temporary directory: " + ex.Message; return false; }
            return true;
        }

        private static void AddPasslogTemps(List<string> files, string prefix)
        {
            string[] suffixes = new string[]{"-0.log","-0.log.mbtree","-0.mbtree","-0.log.temp","-0.log.mbtree.temp","-0.mbtree.temp",".log",".log.mbtree",".mbtree",".log.temp",".log.mbtree.temp",".mbtree.temp"};
            foreach (string suffix in suffixes) files.Add(prefix + suffix);
        }

        private static bool TryBuildColorFilter(IDictionary<string,string> state, out string filter, out string suffix, out string reason)
        {
            filter = ""; suffix = ""; reason = "";
            if (Get(state, "FG_COLOR_ENABLED") != "1") return true;
            double contrast, brightness, saturation, gamma;
            if (!TryReadDouble(state, "FG_COLOR_CONTRAST", out contrast) || !TryReadDouble(state, "FG_COLOR_BRIGHTNESS", out brightness) || !TryReadDouble(state, "FG_COLOR_SATURATION", out saturation) || !TryReadDouble(state, "FG_COLOR_GAMMA", out gamma))
            { reason = "invalid color correction value"; return false; }
            filter = "eq=contrast=" + F2(contrast) + ":brightness=" + F2(brightness) + ":saturation=" + F2(saturation) + ":gamma=" + F2(gamma) + ",";
            if (Get(state, "FG_COLOR_BLACK_WHITE") == "1") filter += "hue=s=0,";
            suffix = "_CC";
            return true;
        }

        private static string BuildLutNamingSuffix(IDictionary<string,string> state)
        {
            string path = Get(state, "FG_LUT_PATH");
            if (string.IsNullOrWhiteSpace(path)) return "";
            int strength = NormalizeLutStrength(Get(state, "FG_LUT_STRENGTH"));
            return "_LUT_" + Path.GetFileNameWithoutExtension(path) + "_" + strength.ToString(CultureInfo.InvariantCulture);
        }

        private static bool TryPrepareLut(string ffmpeg, IDictionary<string,string> state, out string compatPath, out string compatName, out string opacity, out string reason)
        {
            compatPath = ""; compatName = ""; opacity = ""; reason = "";
            string path = Get(state, "FG_LUT_PATH");
            if (string.IsNullOrWhiteSpace(path)) return true;
            if (!File.Exists(path)) { reason = "selected LUT is missing: " + path; return false; }
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex) { reason = "cannot read selected LUT: " + ex.Message; return false; }
            bool has3d = false;
            List<string> output = new List<string>(lines.Length + 2);
            foreach (string line in lines)
            {
                if (line.TrimStart().StartsWith("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase)) has3d = true;
                string trimmed = line.Trim();
                string[] parts = trimmed.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && string.Equals(parts[0], "LUT_3D_INPUT_RANGE", StringComparison.OrdinalIgnoreCase))
                {
                    output.Add("DOMAIN_MIN " + parts[1] + " " + parts[1] + " " + parts[1]);
                    output.Add("DOMAIN_MAX " + parts[2] + " " + parts[2] + " " + parts[2]);
                }
                else output.Add(line);
            }
            if (!has3d) { reason = "selected CUBE does not contain LUT_3D_SIZE"; return false; }
            string tempDir = Path.Combine(Path.GetTempPath(), "FilmGrain_Studio", "NativeLut");
            try
            {
                Directory.CreateDirectory(tempDir);
                compatName = "__FGS_LUT_" + Guid.NewGuid().ToString("N") + ".cube";
                compatPath = Path.Combine(tempDir, compatName);
                File.WriteAllLines(compatPath, output.ToArray(), Encoding.ASCII);
            }
            catch (Exception ex)
            {
                TryDelete(compatPath); compatPath = ""; compatName = "";
                reason = "could not prepare FFmpeg-compatible LUT: " + ex.Message;
                return false;
            }
            string validationError;
            if (!ValidateLut(ffmpeg, tempDir, compatName, out validationError))
            {
                TryDelete(compatPath); compatPath = ""; compatName = "";
                reason = "FFmpeg cannot initialize selected LUT" + (string.IsNullOrWhiteSpace(validationError) ? "" : ": " + validationError);
                return false;
            }
            int strength = NormalizeLutStrength(Get(state, "FG_LUT_STRENGTH"));
            opacity = (strength / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
            return true;
        }

        private static bool ValidateLut(string ffmpeg, string workingDirectory, string compatName, out string error)
        {
            error = "";
            try
            {
                string filter = "format=gbrp16le,lut3d=file='" + EscapeFilterPath(compatName) + "':interp=tetrahedral";
                List<string> a = new List<string>{"-hide_banner","-v","error","-f","lavfi","-i","color=c=gray:s=32x32:d=0.04","-vf",filter,"-frames:v","1","-f","null","NUL"};
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = ffmpeg; psi.Arguments = JoinArgs(a); psi.WorkingDirectory = workingDirectory;
                psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardError = true; psi.RedirectStandardOutput = true;
                using (Process p = new Process())
                {
                    p.StartInfo = psi; p.Start();
                    if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } error = "validation timed out"; return false; }
                    string stdOut = p.StandardOutput.ReadToEnd(); string stdErr = p.StandardError.ReadToEnd();
                    if (p.ExitCode != 0) { error = FirstLine(string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr); return false; }
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static string ResolvePlateTag(string plate, string fallback)
        {
            string n = Path.GetFileName(plate);
            if (string.Equals(n, "CT 35mm Grain 4K DCI.mov", StringComparison.OrdinalIgnoreCase)) return "CT35";
            if (string.Equals(n, "Filmgrain_4KDCI_35mm_24fps.mov", StringComparison.OrdinalIgnoreCase)) return "35L";
            if (string.Equals(n, "Filmgrain_4KDCI_Super_35mm_24fps.mov", StringComparison.OrdinalIgnoreCase)) return "S35L";
            if (string.Equals(n, "Filmgrain_4KDCI_16mm_24fps.mov", StringComparison.OrdinalIgnoreCase)) return "16L";
            if (string.Equals(n, "Filmgrain_4KDCI_Super_16mm_24fps.mov", StringComparison.OrdinalIgnoreCase)) return "S16L";
            if (string.Equals(n, "Filmgrain_4KDCI_8mm_24fps.mov", StringComparison.OrdinalIgnoreCase)) return "8L";
            if (string.Equals(n, "Filmgrain_4KDCI_35mm_24fps_Heavy.mov", StringComparison.OrdinalIgnoreCase)) return "35H";
            if (string.Equals(n, "Filmgrain_4KDCI_Super_35mm_24fps_Heavy.mov", StringComparison.OrdinalIgnoreCase)) return "S35H";
            if (string.Equals(n, "Filmgrain_4KDCI_16mm_24fps_Heavy.mov", StringComparison.OrdinalIgnoreCase)) return "16H";
            if (string.Equals(n, "Filmgrain_4KDCI_Super_16mm_24fps_Heavy.mov", StringComparison.OrdinalIgnoreCase)) return "S16H";
            if (string.Equals(n, "Filmgrain_4KDCI_8mm_24fps_Heavy.mov", StringComparison.OrdinalIgnoreCase)) return "8H";
            return string.IsNullOrWhiteSpace(fallback) ? "SCAN" : fallback;
        }

        private static int NormalizeLutStrength(string value) { int v; if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return 75; return v == 25 || v == 50 || v == 100 ? v : 75; }
        private static bool TryReadDouble(IDictionary<string,string> s, string k, out double v) { return double.TryParse(Get(s, k), NumberStyles.Float, CultureInfo.InvariantCulture, out v); }
        private static string F2(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }
        private static string EscapeFilterPath(string value) { return (value ?? "").Replace("\\", "/").Replace(":", "\\:").Replace("'", "\\'"); }
        // Match StudioBridge's two-level escaping for libplacebo custom_shader_path.
        private static bool TryPrepareFgsimHook(string hook, string preset, out string reason)
        {
            reason = "";
            if (File.Exists(hook) && new FileInfo(hook).Length >= 1048576) return true;
            string generated = Path.GetDirectoryName(hook);
            string root = Path.GetDirectoryName(generated);
            string raw = Path.Combine(generated, "Noise_512x512_RGBA8.rgba");
            string template = Path.Combine(root, "FilmGrainSimplified.hook.template");
            if (!File.Exists(raw) || new FileInfo(raw).Length != 1048576 || !File.Exists(template))
            {
                reason = "FGSIM noise texture needs initial Legacy preparation";
                return false;
            }
            string staging = hook + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] noise = File.ReadAllBytes(raw);
                if (noise.Length != 1048576) { reason = "FGSIM noise texture changed during preparation"; return false; }
                string hex = BitConverter.ToString(noise).Replace("-", "").ToLowerInvariant();
                StringBuilder lines = new StringBuilder(hex.Length + hex.Length / 256 + 1);
                for (int i = 0; i < hex.Length; i += 256) lines.Append(hex, i, Math.Min(256, hex.Length - i)).Append('\n');
                string strength = preset == "LIGHT" ? "0.10" : preset == "HEAVY" ? "0.30" : "0.20";
                string body = File.ReadAllText(template).Replace("@@TILE_SIZE@@", "2").Replace("@@STRENGTH@@", strength)
                    .Replace("@@HL_START@@", "0.55").Replace("@@HL_END@@", "0.85").Replace("@@HL_REDUCE@@", "0.50")
                    .Replace("@@NOISE_HEX@@", lines.ToString());
                File.WriteAllText(staging, body, new UTF8Encoding(false));
                if (File.Exists(hook)) File.Delete(hook);
                File.Move(staging, hook);
                return true;
            }
            catch (Exception ex) { reason = "FGSIM hook preparation failed: " + ex.Message; return false; }
            finally { try { if (File.Exists(staging)) File.Delete(staging); } catch { } }
        }

        private static string EscapeFgsimShaderPath(string value)
        {
            string path = (value ?? "").Replace("\\", "/").Replace(":", "\\:").Replace(",", "\\,").Replace(";", "\\;").Replace("[", "\\[").Replace("]", "\\]");
            return "'" + path.Replace("'", "'" + new string('\\', 3) + "''") + "'";
        }
        private static int Even(int v) { return ((v + 1) / 2) * 2; }
        private static string Get(IDictionary<string,string> s, string k) { string v; return s.TryGetValue(k, out v) ? (v ?? "") : ""; }
        private static bool Eq(IDictionary<string,string> s, string k, string v) { return string.Equals(Get(s, k), v, StringComparison.OrdinalIgnoreCase); }
        private static string GetFfmpegDir(string root)
        {
            string cfg = Path.Combine(root, "FilmGrain_Config.ini"); if (!File.Exists(cfg)) cfg = Path.Combine(root, "FilmGrain_Config.default.ini");
            foreach (string line in File.ReadAllLines(cfg)) { int p = line.IndexOf('='); if (p > 0 && line.Substring(0, p).Trim().Equals("FFMPEG_DIR", StringComparison.OrdinalIgnoreCase)) return line.Substring(p + 1).Trim().Trim('"'); }
            return "";
        }
        private static string ResolveOutputDirectory(string input, IDictionary<string,string> s) { if (Eq(s, "FG_OUTPUT_MODE", "CUSTOM") && !string.IsNullOrWhiteSpace(Get(s, "FG_OUTPUT_CUSTOM_DIR"))) return Get(s, "FG_OUTPUT_CUSTOM_DIR"); return Path.GetDirectoryName(input); }
        private static string JoinArgs(IEnumerable<string> args) { StringBuilder b = new StringBuilder(); foreach (string x in args) { if (b.Length > 0) b.Append(' '); b.Append(Quote(x ?? "")); } return b.ToString(); }
        private static string Quote(string s)
        {
            if (s.Length > 0 && s.IndexOfAny(new char[]{' ','\t','\n','\v','"'}) < 0) return s;
            StringBuilder b = new StringBuilder(); b.Append('"'); int bs = 0;
            foreach (char c in s) { if (c == '\\') { bs++; continue; } if (c == '"') { b.Append('\\', bs * 2 + 1); b.Append('"'); bs = 0; continue; } if (bs > 0) { b.Append('\\', bs); bs = 0; } b.Append(c); }
            if (bs > 0) b.Append('\\', bs * 2); b.Append('"'); return b.ToString();
        }
        private static void TryDelete(string path) { try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { } }
        private static string FirstLine(string value) { if (string.IsNullOrWhiteSpace(value)) return ""; using (StringReader r = new StringReader(value.Trim())) return r.ReadLine() ?? ""; }
    }
}
