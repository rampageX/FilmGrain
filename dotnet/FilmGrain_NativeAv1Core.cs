using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FilmGrainStudioPreview
{
    // Native AV1 Main10 metadata-grain and pixel-grain encode paths.
    // Unported shared filter routes remain on the Legacy backend.
    internal static class NativeAv1Core
    {
        private sealed class FramePlan
        {
            internal string Filter = "";
            internal string FileSuffix = "";
            internal int ActiveWidth;
            internal int ActiveHeight;
        }

        internal static bool TryPrepare(string appRoot, string input, MediaProbeInfo info,
            IDictionary<string, string> state, HardwareCapabilitySnapshot caps,
            out BridgePreparedExecution prepared, out string reason)
        {
            prepared = null;
            reason = "";
            if (state == null || info == null) { reason = "missing state/media probe"; return false; }
            if (!Eq(state, "FG_MODE", "AV1")) { reason = "codec is not AV1"; return false; }
            if (caps == null || !caps.Ready || !caps.Av1Available) { reason = "AV1 hardware capability cache is unavailable"; return false; }
            string grainEngine = Get(state, "FG_GRAIN_ENGINE").ToUpperInvariant();
            bool proceduralGrain = grainEngine == "PROCEDURAL";
            bool fgsimGrain = grainEngine == "FGSIM";
            bool metadataGrain = grainEngine == "NATIVE";
            if (!proceduralGrain && !fgsimGrain && !metadataGrain) { reason = "unsupported AV1 grain engine"; return false; }

            // Keep every shared or unported transformation on the known Legacy route.
            if (Get(state, "FG_SVP_INTERPOLATE") == "1") { reason = "AV1 interpolation remains on the Legacy backend"; return false; }
            bool hdrToSdr = info.IsHdr && !Eq(state, "FG_HDR_POLICY", "PRESERVE") &&
                (Eq(state, "FG_HDR_POLICY", "SDR") || fgsimGrain ||
                 (Eq(state, "FG_HDR_POLICY", "AUTO") &&
                  (Get(state, "FG_COLOR_ENABLED") == "1" || !string.IsNullOrWhiteSpace(Get(state, "FG_LUT_PATH")))));
            bool hdrPreserve = info.IsHdr && !hdrToSdr;
            bool uploadRequested = Get(state, "FG_UPLOAD") == "1";
            bool uploadSkippedForHdrPreserve = uploadRequested && hdrPreserve;
            bool upload = uploadRequested && !hdrPreserve;
            string uploadPass = Get(state, "FG_X264_PASS_MODE").ToUpperInvariant();
            if (upload && !caps.X264Available) { reason = "x264 upload capability is unavailable"; return false; }
            if (upload && !Eq(state, "FG_UPLOAD_MODE", "X264")) { reason = "unsupported AV1 upload mode"; return false; }
            if (upload && uploadPass != "VBR1" && uploadPass != "2PASS" && uploadPass != "3PASS") { reason = "unsupported upload pass mode"; return false; }
            string tonemap = Get(state, "FG_TONEMAP_ALGO").ToLowerInvariant();
            if (tonemap != "mobius" && tonemap != "reinhard" && tonemap != "gamma" && tonemap != "linear" && tonemap != "clip") tonemap = "hable";
            string tonemapLabel = char.ToUpperInvariant(tonemap[0]) + tonemap.Substring(1);
            if (info.Width <= 0 || info.Height <= 0) { reason = "invalid dimensions"; return false; }
            bool deinterlace = info.IsInterlaced && Eq(state, "FG_DEINTERLACE", "AUTO");
            if ((hdrToSdr || hdrPreserve) && info.IsInterlaced && !deinterlace)
            { reason = "interlaced HDR Native requires automatic field-rate deinterlacing"; return false; }
            bool bwdifVulkan = deinterlace && Eq(state, "FG_DEINT_METHOD", "BWDIF_VULKAN");
            bool bwdifCuda = deinterlace && Eq(state, "FG_DEINT_METHOD", "BWDIF_CUDA");
            bool w3fdif = deinterlace && Eq(state, "FG_DEINT_METHOD", "W3FDIF");
            if (deinterlace && !bwdifVulkan && !bwdifCuda && !w3fdif)
            { reason = "unsupported AV1 deinterlace method"; return false; }
            string deinterlaceFilter = bwdifVulkan ? "format=p010le,hwupload,bwdif_vulkan=mode=send_field:parity=auto:deint=all,hwdownload,format=p010le," :
                bwdifCuda ? "format=p010le,hwupload_cuda=device=0,bwdif_cuda=mode=send_field:parity=auto:deint=all,hwdownload,format=p010le," :
                w3fdif ? "w3fdif=filter=complex:mode=field:parity=auto:deint=all," : "";
            string deinterlaceSuffix = bwdifVulkan ? "_DI_BWV" : bwdifCuda ? "_DI_BWC" : w3fdif ? "_DI_W3F" : "";
            FramePlan frame;
            if (!TryBuildFramePlan(info, state, out frame, out reason)) return false;
            string colorFilter, colorSuffix;
            if (!TryBuildColorFilter(state, out colorFilter, out colorSuffix, out reason)) return false;
            string lutNamingSuffix = hdrPreserve ? "" : BuildLutNamingSuffix(state);
            string subtitleSuffix = Get(state, "FG_SUBTITLE") == "1" ? "_SUB" : "";
            if (!Eq(state, "FG_SPEED", "STANDARD") && !Eq(state, "FG_SPEED", "FAST") && !Eq(state, "FG_SPEED", "UHQ"))
            { reason = "unsupported AV1 speed mode"; return false; }
            if (Eq(state, "FG_SPEED", "UHQ") && !caps.Av1UhqAvailable) { reason = "AV1 UHQ is unavailable"; return false; }
            int sfeEngines = 0;
            if (!string.IsNullOrWhiteSpace(Get(state, "FG_SFE_ENGINES")) &&
                !int.TryParse(Get(state, "FG_SFE_ENGINES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out sfeEngines))
            { reason = "invalid AV1 split-frame engine count"; return false; }
            if (sfeEngines < 0) { reason = "invalid AV1 split-frame engine count"; return false; }
            if (sfeEngines > 0 && (sfeEngines < 2 || sfeEngines > caps.Av1SplitEncodeMaxEngines || !caps.Grav1synthSfeCompatible ||
                (Eq(state, "FG_SPEED", "FAST") || (!Eq(state, "FG_SPEED", "STANDARD") && !Eq(state, "FG_SPEED", "UHQ")))))
            { reason = "requested AV1 SFE mode is not supported by current speed, GPU, or grav1synth capabilities"; return false; }

            string grainArgs = "", grainTag = "", grainLabel = "";
            if (metadataGrain)
            {
                string mode = Get(state, "FG_AV1_GRAIN_MODE").ToUpperInvariant();
                if (!TryBuildGrainArgs(mode, state, out grainArgs, out grainTag, out grainLabel, out reason)) return false;
            }
            else if (proceduralGrain)
            {
                int strength;
                if (!int.TryParse(Get(state, "FG_PROC_STRENGTH"), NumberStyles.Integer, CultureInfo.InvariantCulture, out strength))
                    strength = 55;
                strength = Math.Max(10, Math.Min(100, strength));
                grainTag = "DG" + strength.ToString(CultureInfo.InvariantCulture);
                grainLabel = "Digital Grain " + strength.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                string selectedFgsimPreset = Get(state, "FG_FGSIM_PRESET").ToUpperInvariant();
                if (selectedFgsimPreset != "LIGHT" && selectedFgsimPreset != "MEDIUM" && selectedFgsimPreset != "HEAVY") { reason = "unsupported FGSIM preset"; return false; }
                grainTag = "FGSIM_" + (selectedFgsimPreset == "LIGHT" ? "L" : selectedFgsimPreset == "HEAVY" ? "H" : "M");
                grainLabel = "FGSIM " + selectedFgsimPreset;
            }

            double sourceFps = BitrateRecommendationCore.ParseMediaFps(info);
            if (sourceFps <= 0.0) { reason = "invalid source FPS"; return false; }
            double outputFps = deinterlace ? sourceFps * 2.0 : (Eq(state, "FG_FPS_MODE", "SOURCE")
                ? sourceFps
                : BitrateRecommendationCore.GetRecommendedOutputFps(info, false, false));
            if (outputFps <= 0.0 || double.IsNaN(outputFps) || double.IsInfinity(outputFps)) { reason = "invalid output FPS"; return false; }
            string fpsText = outputFps.ToString("0.######", CultureInfo.InvariantCulture);
            if (deinterlace && !TryDoubleSourceFps(info.AvgFrameRate, out fpsText))
            { reason = "invalid field-rate FPS"; return false; }

            long bitrate;
            if (!long.TryParse(Get(state, "FG_BITRATE"), NumberStyles.Integer, CultureInfo.InvariantCulture, out bitrate) || bitrate <= 0)
            { reason = "invalid bitrate"; return false; }
            if (Eq(state, "FG_BITRATE_MODE", "AUTO"))
                bitrate = BitrateRecommendationCore.GetRecommendedBitrate(0, frame.ActiveWidth, frame.ActiveHeight, outputFps, Get(state, "FG_HIGH_MOTION") == "1");
            long maxRate = bitrate * 3L;
            long bufferSize = bitrate * 6L;
            long uploadBitrate = 0;
            if (upload)
            {
                if (Eq(state, "FG_UPLOAD_BITRATE_MODE", "MANUAL"))
                {
                    if (!long.TryParse(Get(state, "FG_UPLOAD_BITRATE"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uploadBitrate) || uploadBitrate <= 0)
                    { reason = "invalid manual upload bitrate"; return false; }
                }
                else if (Eq(state, "FG_UPLOAD_BITRATE_MODE", "AUTO"))
                    uploadBitrate = BitrateRecommendationCore.GetRecommendedBitrate(2, frame.ActiveWidth, frame.ActiveHeight, outputFps, Get(state, "FG_UPLOAD_HIGH_MOTION") == "1");
                else { reason = "unsupported upload bitrate mode"; return false; }
            }

            string container = Get(state, "FG_CONTAINER").ToUpperInvariant();
            if (container != "MP4" && container != "MKV") { reason = "unsupported AV1 container"; return false; }
            string extension = container == "MKV" ? ".mkv" : ".mp4";
            string outputDirectory = ResolveOutputDirectory(input, state);
            try { Directory.CreateDirectory(outputDirectory); }
            catch (Exception ex) { reason = "cannot create output directory: " + ex.Message; return false; }

            string speed = Eq(state, "FG_SPEED", "STANDARD") ? "STD" : Eq(state, "FG_SPEED", "UHQ") ? "UHQ" : "FAST";
            string fpsSuffix = "";
            if (Eq(state, "FG_FPS_MODE", "AUTO"))
            {
                if (Math.Abs(outputFps - (24000.0 / 1001.0)) <= 0.03) fpsSuffix = "_23976p";
                else if (Math.Abs(outputFps - 24.0) <= 0.03) fpsSuffix = "_24p";
            }
            string output = Path.Combine(outputDirectory,
                Path.GetFileNameWithoutExtension(input) + "_AV1_" + speed + "_" + bitrate.ToString(CultureInfo.InvariantCulture) + "k" + fpsSuffix + deinterlaceSuffix + frame.FileSuffix + "_GS_" + grainTag + colorSuffix + lutNamingSuffix + (hdrToSdr ? "_SDR_" + tonemapLabel : "") + subtitleSuffix + extension);
            if (File.Exists(output)) { reason = "native output already exists: " + output; return false; }
            string uploadPreset = Get(state, "FG_X264_PRESET").ToLowerInvariant();
            if (uploadPreset != "medium" && uploadPreset != "slow") uploadPreset = "faster";
            string uploadOutput = upload ? Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(input) + "_X264" + BuildX264FileSuffix(uploadPreset, uploadPass) + "_" + uploadBitrate.ToString(CultureInfo.InvariantCulture) + "k" + fpsSuffix + deinterlaceSuffix + frame.FileSuffix + "_GS_" + grainTag + colorSuffix + lutNamingSuffix + (hdrToSdr ? "_SDR_" + tonemapLabel : "") + "_UPLOAD_FROM_AV1_" + speed + "_" + bitrate.ToString(CultureInfo.InvariantCulture) + "k" + subtitleSuffix + ".mp4") : "";
            if (upload && File.Exists(uploadOutput)) { reason = "native upload output already exists: " + uploadOutput; return false; }

            string ffmpeg = Path.Combine(ReadConfigValue(appRoot, "FFMPEG_DIR"), "ffmpeg.exe");
            string grav1synth = ReadConfigValue(appRoot, "GRAV1SYNTH");
            string ffprobe = Path.Combine(ReadConfigValue(appRoot, "FFMPEG_DIR"), "ffprobe.exe");
            if (!File.Exists(ffmpeg)) { reason = "ffmpeg.exe not found: " + ffmpeg; return false; }
            if (metadataGrain && !File.Exists(grav1synth)) { reason = "grav1synth.exe not found: " + grav1synth; return false; }

            string aqStrength = Get(state, "FG_HEVC_SPATIAL_AQ");
            if (string.IsNullOrEmpty(aqStrength)) aqStrength = "8";
            if (aqStrength != "0" && aqStrength != "4" && aqStrength != "8" && aqStrength != "10" && aqStrength != "12" && aqStrength != "15")
            { reason = "unsupported AV1 spatial AQ strength"; return false; }

            string tempRoot;
            if (!TryResolveTempRoot(input, state, out tempRoot, out reason)) return false;
            string lutCompatPath = "", lutCompatName = "", lutOpacity = "";
            if (!hdrPreserve && !TryPrepareLut(ffmpeg, state, out lutCompatPath, out lutCompatName, out lutOpacity, out reason)) return false;
            string subtitleAss;
            if (!NativeSubtitlePrepareCore.TryPrepare(ffmpeg, ffprobe, input, state, frame.ActiveWidth, frame.ActiveHeight, out subtitleAss, out reason))
            {
                TryDelete(lutCompatPath);
                return false;
            }
            string encodeWorkingDirectory = string.IsNullOrEmpty(lutCompatPath) ? tempRoot : Path.GetDirectoryName(lutCompatPath);
            string subtitleFilterPath = "";
            if (!string.IsNullOrEmpty(subtitleAss))
            {
                subtitleFilterPath = Path.Combine(encodeWorkingDirectory, "__FGS_SUB_" + Guid.NewGuid().ToString("N") + ".ass");
                try { File.Copy(subtitleAss, subtitleFilterPath, false); }
                catch (Exception ex) { TryDelete(lutCompatPath); TryDelete(subtitleAss); TryDelete(subtitleFilterPath); reason = "could not stage subtitle for AV1 Native encoding: " + ex.Message; return false; }
            }
            string subtitleFilter = string.IsNullOrEmpty(subtitleFilterPath) ? "" : ",format=yuv420p10le,subtitles=filename='" + EscapeFilterPath(Path.GetFileName(subtitleFilterPath)) + "',format=p010le";
            string filterGraph = BuildFilterGraph(info, frame, state, deinterlaceFilter, colorFilter, lutCompatName, lutOpacity, subtitleFilter,
                proceduralGrain, fgsimGrain, metadataGrain, hdrPreserve);
            if (hdrPreserve && !string.IsNullOrWhiteSpace(filterGraph)) filterGraph = filterGraph.Replace("[vout]", BuildHdrFrameFilter(info) + "[vout]");
            string job = "__FGS_AV1_NATIVE_" + Guid.NewGuid().ToString("N");
            string cleanIvf = Path.Combine(tempRoot, job + "_clean.ivf");
            string grainIvf = Path.Combine(tempRoot, job + "_grain.ivf");
            string hdrWorkfile = hdrToSdr ? Path.Combine(tempRoot, job + "_hdr2sdr.mkv") : "";
            string encodeInput = hdrToSdr ? hdrWorkfile : input;

            string preset = Eq(state, "FG_SPEED", "STANDARD") ? "p7" : Eq(state, "FG_SPEED", "UHQ") ? "p4" : "p5";
            string tune = Eq(state, "FG_SPEED", "UHQ") ? "uhq" : "hq";
            bool highMotion = Get(state, "FG_HIGH_MOTION") == "1";
            bool highMotionEncoderProfile = highMotion && !Eq(state, "FG_SPEED", "UHQ");
            string fgsimHook = "";
            if (fgsimGrain)
            {
                string fgsimPreset = Get(state, "FG_FGSIM_PRESET").ToUpperInvariant();
                if (!HasLibplaceboFilter(ffmpeg)) { TryDelete(lutCompatPath); TryDelete(subtitleAss); TryDelete(subtitleFilterPath); reason = "selected FFmpeg lacks libplacebo filter for AV1 FGSIM"; return false; }
                if (!TryPrepareFgsimHook(appRoot, fgsimPreset, out fgsimHook, out reason)) { TryDelete(lutCompatPath); TryDelete(subtitleAss); TryDelete(subtitleFilterPath); return false; }
                if (hdrPreserve)
                {
                    if (!ValidateFgsimHdrHook(ffmpeg, fgsimHook, out reason)) { TryDelete(lutCompatPath); TryDelete(subtitleAss); TryDelete(subtitleFilterPath); return false; }
                    filterGraph = AppendHdrFgsimFilter(info, frame, deinterlaceFilter, colorFilter, subtitleFilter, fgsimHook);
                }
                else filterGraph = AppendFgsimFilter(filterGraph, fgsimHook);
            }
            else if (proceduralGrain)
            {
                int strength;
                if (!int.TryParse(Get(state, "FG_PROC_STRENGTH"), NumberStyles.Integer, CultureInfo.InvariantCulture, out strength)) strength = 55;
                strength = Math.Max(10, Math.Min(100, strength));
                filterGraph = hdrPreserve
                    ? AppendHdrProceduralFilter(filterGraph, frame.ActiveWidth, frame.ActiveHeight, strength, info)
                    : AppendProceduralFilter(filterGraph, frame.ActiveWidth, frame.ActiveHeight, strength);
            }

            List<string> encode = new List<string>();
            Add(encode, "-hide_banner", "-stats", "-progress", "pipe:2", "-stats_period", "0.5", "-y");
            if (fgsimGrain || bwdifVulkan) Add(encode, "-init_hw_device", "vulkan=vk:0", "-filter_hw_device", "vk");
            Add(encode, "-i", encodeInput);
            if (!string.IsNullOrWhiteSpace(filterGraph)) Add(encode, "-filter_complex", filterGraph, "-map", "[vout]");
            else Add(encode, "-map", "0:v:0");
            Add(encode, "-an", "-sn", "-dn", "-c:v", "av1_nvenc", "-gpu", "0", "-pix_fmt", "p010le",
                "-highbitdepth", "1", "-preset", preset, "-tune", tune, "-rc", "vbr", "-b:v", bitrate.ToString(CultureInfo.InvariantCulture) + "k",
                "-maxrate:v", maxRate.ToString(CultureInfo.InvariantCulture) + "k", "-bufsize:v", bufferSize.ToString(CultureInfo.InvariantCulture) + "k");
            if (highMotionEncoderProfile && caps.Av1MultipassFullres) Add(encode, "-multipass", "fullres");
            else if (Eq(state, "FG_SPEED", "FAST"))
            {
                if (caps.Av1MultipassQres) Add(encode, "-multipass", "qres");
            }
            else if (caps.Av1MultipassFullres) Add(encode, "-multipass", "fullres");
            else if (caps.Av1MultipassQres) Add(encode, "-multipass", "qres");
            if (caps.Av1SpatialAq && aqStrength != "0") Add(encode, "-spatial-aq", "1", "-aq-strength", aqStrength);
            if (!Eq(state, "FG_SPEED", "UHQ"))
            {
                if (caps.Av1BFrames) Add(encode, "-bf", "4");
                if (caps.Av1BFrames && caps.Av1BReference) Add(encode, "-b_ref_mode", "middle");
                if (caps.Av1TemporalAq && Get(state, "FG_HEVC_TEMPORAL_AQ") == "1") Add(encode, "-temporal-aq", "1");
                if (caps.Av1Lookahead) Add(encode, "-rc-lookahead", highMotionEncoderProfile ? "32" : Eq(state, "FG_SPEED", "FAST") ? "16" : "27");
                if (highMotionEncoderProfile && caps.Av1AdaptiveB) Add(encode, "-b_adapt", "1");
                if (highMotionEncoderProfile && caps.Av1SceneCut) Add(encode, "-no-scenecut", "0");
            }
            if (sfeEngines >= 2) Add(encode, "-split_encode_mode", sfeEngines.ToString(CultureInfo.InvariantCulture));
            Add(encode, "-r", fpsText, "-fps_mode:v", "cfr");
            if (hdrPreserve) Add(encode, "-color_range", HdrRange(info), "-color_primaries", HdrPrimaries(info), "-color_trc", info.ColorTransfer, "-colorspace", HdrSpace(info));
            else if (hdrToSdr) Add(encode, "-color_range", "tv", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709");
            Add(encode, "-f", "ivf", cleanIvf);

            List<string> apply = new List<string>();
            if (metadataGrain)
            {
                Add(apply, "apply", cleanIvf, "-o", grainIvf);
                if (!string.IsNullOrWhiteSpace(grainArgs)) Add(apply, grainArgs.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
                Add(apply, "--replace", "-y");
            }

            string remuxVideo = metadataGrain ? grainIvf : cleanIvf;
            List<string> remux = new List<string>();
            Add(remux, "-hide_banner", "-stats", "-progress", "pipe:2", "-stats_period", "0.5", "-y", "-i", remuxVideo, "-i", input,
                "-map", "0:v:0", "-map", "1:a?");
            if (container == "MKV") Add(remux, "-map", "1:s?", "-map", "1:t?", "-map", "1:d?");
            Add(remux, "-map_metadata", "1", "-map_chapters", "1", "-c:v", "copy");
            if (hdrPreserve) Add(remux, "-color_range", HdrRange(info), "-color_primaries", HdrPrimaries(info), "-color_trc", info.ColorTransfer, "-colorspace", HdrSpace(info));
            else if (hdrToSdr) Add(remux, "-color_range", "tv", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709");
            if (container == "MP4") Add(remux, "-c:a", "aac", "-b:a", "256k", "-movflags", "+faststart");
            else Add(remux, "-c", "copy");
            Add(remux, output);

            BridgeExecutionRequest request = new BridgeExecutionRequest();
            request.ToolPath = ffmpeg;
            request.WorkingDirectory = tempRoot;
            request.InputFiles = new List<string> { input };
            if (hdrToSdr) request.InputFiles.Add(hdrWorkfile);
            request.Environment = new Dictionary<string, string>(state, StringComparer.OrdinalIgnoreCase);
            request.DirectProcess = true;
            request.OutputFiles = new List<string> { output };
            if (upload) request.OutputFiles.Add(uploadOutput);
            request.TemporaryFiles = metadataGrain ? new List<string> { cleanIvf, grainIvf } : new List<string> { cleanIvf };
            if (hdrToSdr) request.TemporaryFiles.Add(hdrWorkfile);
            if (hdrPreserve) request.PostRunValidation = delegate { return VerifyHdrOutput(ffprobe, output, info); };
            if (!string.IsNullOrEmpty(lutCompatPath)) request.TemporaryFiles.Add(lutCompatPath);
            if (!string.IsNullOrEmpty(subtitleAss)) request.TemporaryFiles.Add(subtitleAss);
            if (!string.IsNullOrEmpty(subtitleFilterPath)) request.TemporaryFiles.Add(subtitleFilterPath);
            request.ProgressDurationSeconds = ParseDuration(info.Duration);
            double duration = request.ProgressDurationSeconds;
            int preStages = hdrToSdr ? 1 : 0;
            int encodeStages = metadataGrain ? 3 : 2;
            int uploadPassCount = upload ? (uploadPass == "VBR1" ? 1 : uploadPass == "3PASS" ? 3 : 2) : 0;
            int totalStages = preStages + encodeStages + uploadPassCount;
            List<BridgeExecutionStage> directStages = new List<BridgeExecutionStage>();
            if (hdrToSdr) directStages.Add(Stage(ffmpeg, tempRoot, BuildHdrToSdrArgs(input, hdrWorkfile, info, tonemap), duration, 1, totalStages, "HDR 转 SDR / " + tonemapLabel));
            if (metadataGrain)
            {
                directStages.Add(Stage(ffmpeg, encodeWorkingDirectory, encode, duration, preStages + 1, totalStages, "AV1 Main10 encode"));
                directStages.Add(Stage(grav1synth, tempRoot, apply, duration, preStages + 2, totalStages, "AV1 Film Grain metadata"));
                directStages.Add(Stage(ffmpeg, tempRoot, remux, duration, preStages + 3, totalStages, uploadSkippedForHdrPreserve ? "AV1 container remux (H.264 upload copy skipped by HDR Preserve)" : "AV1 container remux"));
            }
            else
            {
                string grainStage = fgsimGrain ? "AV1 FGSIM pixel-grain encode" : "AV1 Digital Grain pixel-grain encode";
                directStages.Add(Stage(ffmpeg, encodeWorkingDirectory, encode, duration, preStages + 1, totalStages, grainStage));
                directStages.Add(Stage(ffmpeg, tempRoot, remux, duration, preStages + 2, totalStages, uploadSkippedForHdrPreserve ? "AV1 container remux (H.264 upload copy skipped by HDR Preserve)" : "AV1 container remux"));
            }
            if (upload)
            {
                string passlog = "";
                if (uploadPassCount > 1)
                {
                    passlog = Path.Combine(tempRoot, "__FGS_AV1_UPLOAD_X264_" + Guid.NewGuid().ToString("N"));
                    AddPasslogTemps(request.TemporaryFiles, passlog);
                    directStages.Add(Stage(ffmpeg, tempRoot, BuildUploadArgs(output, uploadOutput, uploadPreset, uploadBitrate, Get(state, "FG_UPLOAD_HIGH_MOTION") == "1" && caps.X264BStrategy2, 1, passlog), duration, preStages + encodeStages + 1, totalStages, "H.264 upload pass 1: analysis"));
                    if (uploadPassCount == 3)
                        directStages.Add(Stage(ffmpeg, tempRoot, BuildUploadArgs(output, uploadOutput, uploadPreset, uploadBitrate, Get(state, "FG_UPLOAD_HIGH_MOTION") == "1" && caps.X264BStrategy2, 3, passlog), duration, totalStages - 1, totalStages, "H.264 upload pass 3: stats refinement"));
                }
                directStages.Add(Stage(ffmpeg, tempRoot, BuildUploadArgs(output, uploadOutput, uploadPreset, uploadBitrate, Get(state, "FG_UPLOAD_HIGH_MOTION") == "1" && caps.X264BStrategy2, uploadPassCount == 1 ? 0 : 2, passlog), duration, totalStages, totalStages, "H.264 upload final encode"));
            }
            request.DirectStages = directStages;
            prepared = new BridgePreparedExecution(request, false, ffmpeg, new List<string> { input });
            return true;
        }

        private static string BuildFilterGraph(MediaProbeInfo info, FramePlan frame, IDictionary<string,string> state,
            string deinterlaceFilter, string colorFilter, string lutName, string lutOpacity, string subtitleFilter,
            bool proceduralGrain, bool fgsimGrain, bool metadataGrain, bool hdrPreserve)
        {
            bool pixelGrain = proceduralGrain || fgsimGrain;
            string baseFormat = pixelGrain ? (hdrPreserve && proceduralGrain ? "yuv420p10le" : "yuv420p") : "p010le";
            string postSubtitle = pixelGrain && !string.IsNullOrEmpty(subtitleFilter) ? (hdrPreserve && proceduralGrain ? ",format=yuv420p10le" : ",format=yuv420p") : "";
            string baseFilters;
            if (!string.IsNullOrWhiteSpace(lutName))
            {
                baseFilters = "[0:v:0]" + deinterlaceFilter + colorFilter + "format=gbrp16le,split=2[lutorig][lutsrc];" +
                    "[lutsrc]lut3d=file='" + EscapeFilterPath(lutName) + "':interp=tetrahedral[lutgraded];" +
                    "[lutgraded][lutorig]blend=all_mode=normal:all_opacity=" + lutOpacity + ",format=" + baseFormat +
                    frame.Filter + subtitleFilter + postSubtitle;
            }
            else
            {
                baseFilters = "[0:v:0]" + deinterlaceFilter + colorFilter + "format=" + baseFormat + frame.Filter + subtitleFilter + postSubtitle;
            }
            baseFilters += "[baseout]";
            if (metadataGrain) return baseFilters.Replace("[baseout]", "[vout]");
            return baseFilters;
        }

        private static string AppendFgsimFilter(string baseGraph, string hook)
        {
            return baseGraph + ";[baseout]hwupload,libplacebo=format=yuv420p:custom_shader_path=" +
                EscapeFgsimShaderPath(hook) + ",hwdownload,format=yuv420p,format=p010le[vout]";
        }

        private static string AppendHdrFgsimFilter(MediaProbeInfo info, FramePlan frame, string deinterlaceFilter,
            string colorFilter, string subtitleFilter, string hook)
        {
            // Preserve the validated FGSIM shader. Run it on a 10-bit copy, then add only
            // the shader's luma delta to the untouched 10-bit source planes.
            return "[0:v:0]" + deinterlaceFilter + colorFilter + "format=yuv420p10le,split=2[hdrbase][fgsrc];" +
                "[hdrbase]extractplanes=planes=y+u+v[basey][baseu][basev];" +
                "[fgsrc]split=2[fgbase10][fgshader10];" +
                "[fgshader10]hwupload,libplacebo=format=yuv420p10le:custom_shader_path=" + EscapeFgsimShaderPath(hook) +
                    ",hwdownload,format=yuv420p10le[fgout10];" +
                "[fgbase10]extractplanes=planes=y[fgbasey];[fgout10]extractplanes=planes=y[fgouty];" +
                "[fgbasey][fgouty]lut2=c0='512+x-y':d=10[fgdelta];" +
                "[basey][fgdelta]lut2=c0='x+y-512':d=10[yout];" +
                "[yout][baseu][basev]mergeplanes=map0s=0:map0p=0:map1s=1:map1p=0:map2s=2:map2p=0:format=yuv420p10le" +
                frame.Filter + subtitleFilter + BuildHdrFrameFilter(info) + ",format=p010le[vout]";
        }

        private static bool ValidateFgsimHdrHook(string ffmpeg, string hook, out string reason)
        {
            reason = "";
            try
            {
                string filter = "hwupload,libplacebo=format=yuv420p10le:custom_shader_path=" + EscapeFgsimShaderPath(hook) +
                    ",hwdownload,format=yuv420p10le";
                List<string> args = new List<string> { "-hide_banner", "-loglevel", "warning", "-init_hw_device", "vulkan=vk:0",
                    "-filter_hw_device", "vk", "-f", "lavfi", "-i", "color=c=gray:s=32x32:d=0.04,format=yuv420p10le",
                    "-vf", filter, "-frames:v", "1", "-f", "null", "NUL" };
                ProcessStartInfo psi = new ProcessStartInfo(ffmpeg, JoinArgs(args));
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                using (Process process = new Process())
                {
                    StringBuilder stdout = new StringBuilder();
                    StringBuilder stderr = new StringBuilder();
                    process.StartInfo = psi;
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs eventArgs) { if (eventArgs.Data != null) stdout.AppendLine(eventArgs.Data); };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs eventArgs) { if (eventArgs.Data != null) stderr.AppendLine(eventArgs.Data); };
                    process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    if (!process.WaitForExit(15000)) { try { process.Kill(); } catch { } reason = "10-bit FGSIM shader preflight timed out"; return false; }
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        string detail = FirstLine(string.IsNullOrWhiteSpace(stderr.ToString()) ? stdout.ToString() : stderr.ToString());
                        reason = "10-bit FGSIM shader preflight failed" + (string.IsNullOrWhiteSpace(detail) ? "" : ": " + detail);
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex) { reason = "10-bit FGSIM shader preflight failed: " + ex.Message; return false; }
        }

        private static string AppendProceduralFilter(string baseGraph, int width, int height, int strength)
        {
            int procWidth = Even((int)(((long)width * 4L + 2L) / 3L));
            int procHeight = Even((int)(((long)height * 4L + 2L) / 3L));
            string mask = strength == 100 ? "1.00" : "0." + strength.ToString("00", CultureInfo.InvariantCulture);
            return baseGraph + ";[baseout]split=4[seed][masksrc][base][blacksrc];" +
                "[seed]scale=" + procWidth.ToString(CultureInfo.InvariantCulture) + ":" + procHeight.ToString(CultureInfo.InvariantCulture) +
                ",lutyuv=y=128:u=128:v=128,noise=c0s=100:c0f=t+u,deflate=threshold0=15,dilation=threshold0=10,eq=contrast=3,scale=" +
                width.ToString(CultureInfo.InvariantCulture) + ":" + height.ToString(CultureInfo.InvariantCulture) + "[n];" +
                "[masksrc]lutyuv=y='" + mask + "*(182-abs(75-val))':u=128:v=128[o];" +
                "[n][o]blend=c0_mode=multiply,negate[a];[base][a]alphamerge[c];" +
                "[blacksrc]drawbox=color=black:t=fill[black];[black][c]overlay=shortest=1,format=p010le[vout]";
        }

        private static string AppendHdrProceduralFilter(string baseGraph, int width, int height, int strength, MediaProbeInfo info)
        {
            int procWidth = Even((int)(((long)width * 4L + 2L) / 3L));
            int procHeight = Even((int)(((long)height * 4L + 2L) / 3L));
            string mask = strength == 100 ? "1.00" : "0." + strength.ToString("00", CultureInfo.InvariantCulture);
            string range = HdrRange(info);
            string black = range == "pc" ? "0" : "64";
            return baseGraph + ";[baseout]split=3[seedsrc][masksrc][base];" +
                "[seedsrc]format=yuv420p,scale=" + procWidth.ToString(CultureInfo.InvariantCulture) + ":" + procHeight.ToString(CultureInfo.InvariantCulture) +
                ",lutyuv=y=128:u=128:v=128,noise=c0s=100:c0f=t+u,deflate=threshold0=15,dilation=threshold0=10,eq=contrast=3,scale=" +
                width.ToString(CultureInfo.InvariantCulture) + ":" + height.ToString(CultureInfo.InvariantCulture) + "[n8];" +
                "[masksrc]scale=in_range=" + range + ":out_range=tv,format=yuv420p,lutyuv=y='" + mask + "*(182-abs(75-val))':u=128:v=128[o8];" +
                "[n8][o8]blend=c0_mode=multiply,negate,format=gray,format=gray10le[alpha10];" +
                "[base]extractplanes=planes=y+u+v[yb][ub][vb];" +
                "[yb][alpha10]lut2=c0='" + black + "+(x-" + black + ")*y/1023':d=10[yout];" +
                "[yout][ub][vb]mergeplanes=map0s=0:map0p=0:map1s=1:map1p=0:map2s=2:map2p=0:format=yuv420p10le,format=p010le[vout]";
        }

        private static bool TryPrepareFgsimHook(string appRoot, string preset, out string hookPath, out string reason)
        {
            hookPath = "";
            reason = "";
            string rawPath;
            if (!FgsimTextureCore.TryEnsureLocal(appRoot, out rawPath, out reason)) return false;
            string generated = Path.GetDirectoryName(rawPath);
            string root = Path.GetDirectoryName(generated);
            hookPath = Path.Combine(generated, "FilmGrainSimplified_" + preset + ".hook");
            if (File.Exists(hookPath) && new FileInfo(hookPath).Length >= 1048576) return true;
            string template = Path.Combine(root, "FilmGrainSimplified.hook.template");
            if (!File.Exists(template)) { reason = "FGSIM hook template is missing: " + template; return false; }
            string staging = hookPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] noise = File.ReadAllBytes(rawPath);
                if (noise.Length != 1048576) { reason = "FGSIM noise texture size is invalid"; return false; }
                string hex = BitConverter.ToString(noise).Replace("-", "").ToLowerInvariant();
                StringBuilder lines = new StringBuilder(hex.Length + hex.Length / 256 + 1);
                for (int i = 0; i < hex.Length; i += 256) lines.Append(hex, i, Math.Min(256, hex.Length - i)).Append('\n');
                string strength = preset == "LIGHT" ? "0.10" : preset == "HEAVY" ? "0.30" : "0.20";
                string body = File.ReadAllText(template).Replace("@@TILE_SIZE@@", "2").Replace("@@STRENGTH@@", strength)
                    .Replace("@@HL_START@@", "0.55").Replace("@@HL_END@@", "0.85").Replace("@@HL_REDUCE@@", "0.50")
                    .Replace("@@NOISE_HEX@@", lines.ToString());
                File.WriteAllText(staging, body, new UTF8Encoding(false));
                if (File.Exists(hookPath)) File.Delete(hookPath);
                File.Move(staging, hookPath);
                return true;
            }
            catch (Exception ex) { reason = "FGSIM hook preparation failed: " + ex.Message; return false; }
            finally { try { if (File.Exists(staging)) File.Delete(staging); } catch { } }
        }

        private static bool HasLibplaceboFilter(string ffmpeg)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ffmpeg, "-hide_banner -h filter=libplacebo");
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                using (Process p = new Process())
                {
                    StringBuilder output = new StringBuilder();
                    p.StartInfo = psi;
                    p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) output.AppendLine(e.Data); };
                    p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) output.AppendLine(e.Data); };
                    p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return false; }
                    p.WaitForExit();
                    return p.ExitCode == 0 && output.ToString().IndexOf("libplacebo", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        private static string EscapeFgsimShaderPath(string value)
        {
            string path = (value ?? "").Replace("\\", "/").Replace(":", "\\:").Replace(",", "\\,").Replace(";", "\\;").Replace("[", "\\[").Replace("]", "\\]");
            return "'" + path.Replace("'", "'" + new string('\\', 3) + "''") + "'";
        }

        private static int Even(int value) { return ((value + 1) / 2) * 2; }

        private static bool TryDoubleSourceFps(string source, out string doubled)
        {
            doubled = "";
            string[] parts = (source ?? "").Split('/');
            if (parts.Length < 1 || parts.Length > 2) return false;
            long numerator, denominator = 1;
            if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out numerator) || numerator <= 0 || numerator > long.MaxValue / 2) return false;
            if (parts.Length == 2 && (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out denominator) || denominator <= 0)) return false;
            doubled = (numerator * 2).ToString(CultureInfo.InvariantCulture);
            if (denominator != 1) doubled += "/" + denominator.ToString(CultureInfo.InvariantCulture);
            return true;
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

        private static int NormalizeLutStrength(string value) { int v; if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return 75; return v == 25 || v == 50 || v == 100 ? v : 75; }

        private static bool TryReadDouble(IDictionary<string,string> s, string k, out double v) { return double.TryParse(Get(s, k), NumberStyles.Float, CultureInfo.InvariantCulture, out v); }

        private static string F2(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }

        private static string EscapeFilterPath(string value) { return (value ?? "").Replace("\\", "/").Replace(":", "\\:").Replace("'", "\\'"); }

        private static void TryDelete(string path) { try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { } }

        private static string FirstLine(string value) { if (string.IsNullOrWhiteSpace(value)) return ""; using (StringReader r = new StringReader(value.Trim())) return r.ReadLine() ?? ""; }

        private static string JoinArgs(IEnumerable<string> args)
        {
            StringBuilder b = new StringBuilder();
            foreach (string x in args) { if (b.Length > 0) b.Append(' '); b.Append(Quote(x ?? "")); }
            return b.ToString();
        }


        private static bool TryBuildGrainArgs(string mode, IDictionary<string, string> state,
            out string args, out string tag, out string label, out string reason)
        {
            args = ""; tag = ""; label = ""; reason = "";
            if (mode == "PRESET")
            {
                string[] formats = { "Classic35", "Modern35", "16mm", "Super8", "MaxMid" };
                string[] stocks = { "", "-1", "-2", "-3" };
                int f, s;
                if (!int.TryParse(Get(state, "FG_AV1_FORMAT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out f) || f < 1 || f > formats.Length) f = 1;
                if (!int.TryParse(Get(state, "FG_AV1_STOCK"), NumberStyles.Integer, CultureInfo.InvariantCulture, out s) || s < 1 || s > stocks.Length) s = 1;
                string preset = formats[f - 1] + (f <= 3 ? stocks[s - 1] : "");
                args = "--preset\n" + preset;
                tag = preset.Replace('-', '_');
                label = preset;
                return true;
            }
            if (mode == "ISO")
            {
                int iso;
                if (!int.TryParse(Get(state, "FG_AV1_ISO"), NumberStyles.Integer, CultureInfo.InvariantCulture, out iso) || iso <= 0)
                { reason = "invalid AV1 Photon ISO"; return false; }
                args = "--iso\n" + iso.ToString(CultureInfo.InvariantCulture);
                if (Get(state, "FG_AV1_CHROMA") == "1") args += "\n--chroma";
                tag = "ISO" + iso.ToString(CultureInfo.InvariantCulture);
                label = "Photon ISO " + iso.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            if (mode == "TABLE")
            {
                string table = Get(state, "FG_AV1_GRAIN_TABLE");
                if (string.IsNullOrWhiteSpace(table) || !File.Exists(table)) { reason = "selected AV1 Grain Table is missing: " + table; return false; }
                string stem = Path.GetFileNameWithoutExtension(table) ?? "TABLE";
                stem = Regex.Replace(stem, "[^A-Za-z0-9]+", "_").Trim('_');
                if (stem.Length == 0) stem = "TABLE";
                if (stem.Length > 48) stem = stem.Substring(0, 48);
                args = "--grain\n" + table;
                tag = "TABLE_" + stem;
                label = "Grain Table / " + stem;
                return true;
            }
            reason = "unsupported AV1 grain mode";
            return false;
        }

        private static List<string> BuildHdrToSdrArgs(string input, string output, MediaProbeInfo info, string tonemap)
        {
            string range = string.Equals(info.ColorRange, "pc", StringComparison.OrdinalIgnoreCase) ? "pc" : "tv";
            string primaries = string.IsNullOrWhiteSpace(info.ColorPrimaries) || info.ColorPrimaries == "unknown" || info.ColorPrimaries == "reserved" ? "bt2020" : info.ColorPrimaries;
            string space = string.IsNullOrWhiteSpace(info.ColorSpace) || info.ColorSpace == "unknown" || info.ColorSpace == "reserved" ? "bt2020nc" : info.ColorSpace;
            string filter = "zscale=rin=" + range + ":pin=" + primaries + ":tin=" + info.ColorTransfer + ":min=" + space + ":t=linear:npl=100,format=gbrpf32le,tonemap=tonemap=" + tonemap + ":desat=2,zscale=p=bt709:t=bt709:m=bt709:r=tv,format=p010le,sidedata=mode=delete:type=MASTERING_DISPLAY_METADATA,sidedata=mode=delete:type=CONTENT_LIGHT_LEVEL,setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709";
            return new List<string> { "-hide_banner", "-stats", "-progress", "pipe:2", "-stats_period", "0.5", "-y", "-copyts", "-start_at_zero", "-i", input, "-map", "0:v:0", "-vf", filter, "-an", "-sn", "-dn", "-c:v", "hevc_nvenc", "-profile:v", "main10", "-pix_fmt", "p010le", "-preset", "p4", "-tune", "lossless", "-fps_mode", "passthrough", "-color_range", "tv", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", output };
        }

        private static string HdrRange(MediaProbeInfo info)
        {
            return string.Equals(info.ColorRange, "pc", StringComparison.OrdinalIgnoreCase) ? "pc" : "tv";
        }

        private static string HdrPrimaries(MediaProbeInfo info)
        {
            string value = info.ColorPrimaries ?? "";
            return value.Length == 0 || value == "unknown" || value == "reserved" ? "bt2020" : value;
        }

        private static string HdrSpace(MediaProbeInfo info)
        {
            string value = info.ColorSpace ?? "";
            return value.Length == 0 || value == "unknown" || value == "reserved" ? "bt2020nc" : value;
        }

        private static string BuildHdrFrameFilter(MediaProbeInfo info)
        {
            return ",setparams=range=" + HdrRange(info) + ":color_primaries=" + HdrPrimaries(info) + ":color_trc=" + info.ColorTransfer + ":colorspace=" + HdrSpace(info);
        }

        private static string VerifyHdrOutput(string ffprobe, string output, MediaProbeInfo info)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ffprobe, "-v error -select_streams v:0 -show_entries stream=color_range,color_primaries,color_transfer,color_space -of default=noprint_wrappers=1 " + Quote(output));
                psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true;
                using (Process process = new Process())
                {
                    process.StartInfo = psi; process.Start();
                    string data = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(8000)) { try { process.Kill(); } catch { } return "HDR signaling verification timed out"; }
                    if (process.ExitCode != 0) return "HDR signaling probe failed";
                    Dictionary<string, string> actual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string line in data.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    { int pos = line.IndexOf('='); if (pos > 0) actual[line.Substring(0, pos)] = line.Substring(pos + 1).Trim(); }
                    string value;
                    if (!actual.TryGetValue("color_range", out value) || value != HdrRange(info) || !actual.TryGetValue("color_primaries", out value) || value != HdrPrimaries(info) || !actual.TryGetValue("color_transfer", out value) || value != info.ColorTransfer || !actual.TryGetValue("color_space", out value) || value != HdrSpace(info))
                        return "HDR signaling mismatch: " + data.Trim().Replace(Environment.NewLine, " / ");
                    return "";
                }
            }
            catch (Exception ex) { return "HDR signaling probe failed: " + ex.Message; }
        }

        private static List<string> BuildUploadArgs(string mainOutput, string uploadOutput, string preset, long bitrate, bool highMotion, int pass, string passlog)
        {
            List<string> args = new List<string>();
            Add(args, "-hide_banner", "-stats", "-progress", "pipe:2", "-stats_period", "0.5", "-y", "-c:v", "libdav1d", "-i", mainOutput, "-map", "0:v:0");
            if (pass == 0 || pass == 2) Add(args, "-map", "0:a:0?", "-map_metadata", "0");
            Add(args, "-vf", "format=yuv420p", "-c:v", "libx264", "-profile:v", "high", "-pix_fmt", "yuv420p", "-preset", preset, "-tune", "grain");
            if (highMotion) Add(args, "-b_strategy", "2");
            Add(args, "-b:v", bitrate.ToString(CultureInfo.InvariantCulture) + "k", "-maxrate", (bitrate * 3L).ToString(CultureInfo.InvariantCulture) + "k", "-bufsize", (bitrate * 6L).ToString(CultureInfo.InvariantCulture) + "k");
            if (pass != 0) Add(args, "-pass", pass.ToString(CultureInfo.InvariantCulture), "-passlogfile", passlog);
            if (pass == 1 || pass == 3) Add(args, "-an", "-f", "null", "NUL");
            else Add(args, "-c:a", "aac", "-b:a", "256k", "-ac", "2", "-ar", "48000", "-movflags", "+faststart", uploadOutput);
            return args;
        }

        private static string BuildX264FileSuffix(string preset, string passMode)
        {
            string normalizedPreset = preset == "medium" || preset == "slow" ? preset : "faster";
            string normalizedPass = passMode == "2PASS" || passMode == "3PASS" ? passMode : "VBR1";
            string suffix = normalizedPreset == "faster" ? "" : "_" + normalizedPreset.ToUpperInvariant();
            if (normalizedPass == "2PASS" || normalizedPass == "3PASS") suffix += "_" + normalizedPass;
            return suffix;
        }

        private static void AddPasslogTemps(IList<string> files, string prefix)
        {
            if (files == null || string.IsNullOrEmpty(prefix)) return;
            files.Add(prefix + "-0.log");
            files.Add(prefix + "-0.log.mbtree");
            files.Add(prefix + "-0.mbtree");
        }

        private static BridgeExecutionStage Stage(string tool, string cwd, IList<string> args, double duration, int current, int total, string text)
        {
            StringBuilder command = new StringBuilder();
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) command.Append(' ');
                command.Append(Quote(args[i]));
            }
            return new BridgeExecutionStage { ToolPath = tool, WorkingDirectory = cwd, DirectArguments = command.ToString(), ProgressDurationSeconds = duration, StageCurrent = current, StageTotal = total, StageText = text };
        }

        private static void Add(List<string> target, params string[] values) { if (values != null) target.AddRange(values); }

        // Windows command-line argument quoting for ProcessStartInfo.Arguments.
        private static string Quote(string value)
        {
            StringBuilder result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value ?? "")
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append('"'); slashes = 0; continue; }
                result.Append('\\', slashes); slashes = 0; result.Append(c);
            }
            result.Append('\\', slashes * 2); result.Append('"');
            return result.ToString();
        }

        private static string Get(IDictionary<string, string> state, string key) { string value; return state != null && state.TryGetValue(key, out value) ? value ?? "" : ""; }
        private static bool Eq(IDictionary<string, string> state, string key, string value) { return string.Equals(Get(state, key), value, StringComparison.OrdinalIgnoreCase); }
        private static double ParseDuration(string value) { double result; return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && result > 0.0 ? result : 0.0; }

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
            if (mode == "CUSTOM" && !string.IsNullOrWhiteSpace(Get(state, "FG_OUTPUT_CUSTOM_DIR"))) return Get(state, "FG_OUTPUT_CUSTOM_DIR");
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
    }
}
