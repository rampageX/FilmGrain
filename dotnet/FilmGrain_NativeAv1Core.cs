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
        internal static bool TryPrepare(string appRoot, string input, MediaProbeInfo info,
            IDictionary<string, string> state, HardwareCapabilitySnapshot caps,
            out BridgePreparedExecution prepared, out string reason)
        {
            prepared = null;
            reason = "";
            if (state == null || info == null) { reason = "missing state/media probe"; return false; }
            if (!Eq(state, "FG_MODE", "AV1")) { reason = "codec is not AV1"; return false; }
            IDictionary<string,string> normalized; string bypassNotice;
            if (!SharedInterpolationBypassCore.TryResolve(info,state,out normalized,out bypassNotice,out reason)) return false;
            if (normalized != null)
            {
                bool success = TryPrepare(appRoot,input,info,normalized,caps,out prepared,out reason);
                if (success) SharedInterpolationBypassCore.Attach(prepared,bypassNotice);
                return success;
            }
            bool grainEnabled = SharedGrainCore.IsEnabled(state);
            if (!SharedGrainCore.ValidateHdr(info,state,out reason)) return false;
            string grainEngine = SharedGrainCore.Engine(state);
            bool proceduralGrain = grainEngine == "PROCEDURAL";
            bool fgsimGrain = grainEngine == "FGSIM";
            bool metadataGrain = grainEngine == "NATIVE";
            if (grainEnabled && !proceduralGrain && !fgsimGrain && !metadataGrain) { reason = "unsupported AV1 grain engine"; return false; }

            // Keep every shared or unported transformation on the known Legacy route.
            bool interpolate = Get(state, "FG_SVP_INTERPOLATE") == "1";
            bool fruc = interpolate && Eq(state, "FG_INTERPOLATION_ENGINE", "FRUC");
            bool svp = interpolate && !fruc;
            if (svp && !NvencInterpolationCore.Enabled(state) && !Eq(state, "FG_INTERPOLATION_ENGINE", "SVP") && !string.IsNullOrEmpty(Get(state, "FG_INTERPOLATION_ENGINE")))
            { reason = "unsupported AV1 interpolation engine"; return false; }
            if (svp && info.IsInterlaced) { reason = "interlaced OpenSVPFlow follows Legacy field-rate handling"; return false; }
            if (fruc && info.IsInterlaced) { reason = "AV1 FRUC requires progressive input; interlaced FRUC is not supported"; return false; }
            bool hdrToSdr = HdrColorProcessingCore.NvencToSdr(info,state);
            bool hdrPreserve = info.IsHdr && !hdrToSdr;


            string tonemap = Get(state, "FG_TONEMAP_ALGO").ToLowerInvariant();
            if (tonemap != "mobius" && tonemap != "reinhard" && tonemap != "gamma" && tonemap != "linear" && tonemap != "clip") tonemap = "hable";
            string tonemapLabel = char.ToUpperInvariant(tonemap[0]) + tonemap.Substring(1);
            if (info.Width <= 0 || info.Height <= 0) { reason = "invalid dimensions"; return false; }
            bool deinterlace = info.IsInterlaced && Eq(state, "FG_DEINTERLACE", "AUTO");
            // OFF follows Legacy: no field filter or bob; it does not enable interlaced encoding.
            if(info.IsInterlaced && !deinterlace && !Eq(state,"FG_DEINTERLACE","OFF")) { reason="unsupported deinterlace policy"; return false; }
            bool hdrOffSdr=hdrToSdr && info.IsInterlaced && !deinterlace;
            bool bwdifVulkan = deinterlace && Eq(state, "FG_DEINT_METHOD", "BWDIF_VULKAN");
            bool bwdifCuda = deinterlace && Eq(state, "FG_DEINT_METHOD", "BWDIF_CUDA");
            bool w3fdif = deinterlace && Eq(state, "FG_DEINT_METHOD", "W3FDIF");
            if (deinterlace && !bwdifVulkan && !bwdifCuda && !w3fdif)
            { reason = "unsupported AV1 deinterlace method"; return false; }
            string deinterlaceFilter=deinterlace?SharedHdrPreprocessCore.DeinterlaceFilter(Get(state,"FG_DEINT_METHOD"),info):"";
            string fieldFilter=deinterlaceFilter;
            bool fieldWorkfile=deinterlace && hdrToSdr;
            bool separateDeint=deinterlace && !hdrToSdr && bwdifVulkan && fgsimGrain;
            if(fieldWorkfile || separateDeint)deinterlaceFilter="";
            string deinterlaceSuffix = bwdifVulkan ? "_DI_BWV" : bwdifCuda ? "_DI_BWC" : w3fdif ? "_DI_W3F" : "";
            CinematicFramePlan frame;
            if (!CinematicFrameCore.TryBuild(info, state, out frame, out reason)) return false;
            ResizePlan resize;
            if(!ResizeCore.TryBuild(info,state,frame,out resize,out reason,hdrPreserve))return false;
            string resizeBars=resize.Enabled?frame.Filter:"";if(resize.Enabled)frame.Filter="";
            string colorFilter, colorSuffix;
            if (!TryBuildColorFilter(state, out colorFilter, out colorSuffix, out reason)) return false;
            if(hdrPreserve)colorFilter=HdrColorProcessingCore.Cc(state,info);
            string preprocessing;
            if(!PreprocessingCore.TryBuild(state,out preprocessing,out reason))return false;
            colorFilter=preprocessing+resize.Prefix+colorFilter;
            if(preprocessing.Length>0)colorSuffix="_BIL"+colorSuffix;
            string lutNamingSuffix = BuildLutNamingSuffix(state);
            string subtitleSuffix = Get(state, "FG_SUBTITLE") == "1" ? "_SUB" : "";
            if (!Eq(state, "FG_SPEED", "STANDARD") && !Eq(state, "FG_SPEED", "FAST") && !Eq(state, "FG_SPEED", "UHQ"))
            { reason = "unsupported AV1 speed mode"; return false; }
            int sfeEngines = 0;
            if (!string.IsNullOrWhiteSpace(Get(state, "FG_SFE_ENGINES")) &&
                !int.TryParse(Get(state, "FG_SFE_ENGINES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out sfeEngines))
            { reason = "invalid AV1 split-frame engine count"; return false; }
            if (sfeEngines < 0) { reason = "invalid AV1 split-frame engine count"; return false; }

            string grainArgs = "", grainTag = "", grainLabel = "";
            if (metadataGrain)
            {
                string mode = Get(state, "FG_AV1_GRAIN_MODE").ToUpperInvariant();
                if (!TryBuildGrainArgs(mode, state, out grainArgs, out grainTag, out grainLabel, out reason)) return false;
                if (Av1GrainStrengthCore.Read(state) != 100) grainTag += "_GS" + Av1GrainStrengthCore.Read(state).ToString(CultureInfo.InvariantCulture);
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
            else if (fgsimGrain)
            {
                string selectedFgsimPreset = Get(state, "FG_FGSIM_PRESET").ToUpperInvariant();
                if (!GrainControlCore.IsFgsimPreset(selectedFgsimPreset)) { reason = "unsupported FGSIM preset"; return false; }
                grainTag = "FGSIM_" + (GrainControlCore.FgsimTag(selectedFgsimPreset));
                grainLabel = "FGSIM " + selectedFgsimPreset;
            }

            int pixelPercent = grainEnabled && !metadataGrain ? PixelGrainStrengthCore.Read(state) : 100;
            if (pixelPercent != 100) grainTag += "_GS" + pixelPercent.ToString(CultureInfo.InvariantCulture);
            double sourceFps = BitrateRecommendationCore.ParseMediaFps(info);
            if (sourceFps <= 0.0) { reason = "invalid source FPS"; return false; }
            double outputFps = deinterlace ? sourceFps * 2.0 : (Eq(state, "FG_FPS_MODE", "SOURCE")
                ? sourceFps
                : BitrateRecommendationCore.GetRecommendedOutputFps(info, false, false));
            if (outputFps <= 0.0 || double.IsNaN(outputFps) || double.IsInfinity(outputFps)) { reason = "invalid output FPS"; return false; }
            string fpsText = outputFps.ToString("0.######", CultureInfo.InvariantCulture);
            if (deinterlace && !TryDoubleSourceFps(info.AvgFrameRate, out fpsText))
            { reason = "invalid field-rate FPS"; return false; }

            InterpolationFrameRate frucRate = null;
            if (interpolate)
            {
                if (!InterpolationFrameRateCore.TryParse(Get(state, "FG_INTERPOLATION_TARGET_FPS"), info.AvgFrameRate, out frucRate, out reason)) return false;
                outputFps = frucRate.Fps; fpsText = frucRate.Rational;
                if (ParseDuration(info.Duration) <= 0.0) { reason = "AV1 interpolation requires a known positive source duration"; return false; }
            }

            long bitrate = 0;
            if (!NvencRateControlCore.IsCq(state) && (!long.TryParse(Get(state, "FG_BITRATE"), NumberStyles.Integer, CultureInfo.InvariantCulture, out bitrate) || bitrate <= 0))
            { reason = "invalid bitrate"; return false; }
            if (!NvencRateControlCore.IsCq(state) && Eq(state, "FG_BITRATE_MODE", "AUTO"))
                bitrate = BitrateRecommendationCore.GetRecommendedBitrate(0, frame.ActiveWidth, frame.ActiveHeight, outputFps, Get(state, "FG_HIGH_MOTION") == "1");
            long maxRate = bitrate * 3L;
            long bufferSize = bitrate * 6L;

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
            if (interpolate) fpsSuffix = (NvencInterpolationCore.Enabled(state) ? (Eq(state,"FG_INTERPOLATION_ENGINE","NVENC_AI")?"_NVAI":"_NVFRUC") : fruc ? "_FRUC" : "_SVP") + frucRate.Label + (svp && !fruc && !NvencInterpolationCore.Enabled(state) ? SharedSvpCore.BackendFileSuffix(state) : "");
            string output = Path.Combine(outputDirectory,
                Path.GetFileNameWithoutExtension(input) + "_AV1_" + speed + "_" + NvencRateControlCore.FileToken(state,bitrate) + fpsSuffix + deinterlaceSuffix + frame.FileSuffix + (grainEnabled ? "_GS_" + grainTag : "") + colorSuffix + lutNamingSuffix + (hdrToSdr ? "_SDR_" + tonemapLabel : "") + subtitleSuffix + HardwareDecodeCore.FileSuffix(state) + extension);
            if (File.Exists(output)) { reason = "native output already exists: " + output; return false; }

            string ffmpeg = Path.Combine(ReadConfigValue(appRoot, "FFMPEG_DIR"), "ffmpeg.exe");
            
            NvencRateControlCore.RequireSupport(ffmpeg,state);
            string grav1synth = ReadConfigValue(appRoot, "GRAV1SYNTH");
            string ffprobe = Path.Combine(ReadConfigValue(appRoot, "FFMPEG_DIR"), "ffprobe.exe");
            
            if (caps == null || !caps.Ready || !caps.Av1Available) { reason = "AV1 hardware capability cache is unavailable"; return false; }
            if (Eq(state,"FG_SPEED","UHQ") && !caps.Av1UhqAvailable) { reason="AV1 UHQ is unavailable"; return false; }
            if (sfeEngines > 0 && (sfeEngines < 2 || sfeEngines > caps.Av1SplitEncodeMaxEngines || (grainEnabled && !caps.Grav1synthSfeCompatible) ||
                (Eq(state, "FG_SPEED", "FAST") || (!Eq(state, "FG_SPEED", "STANDARD") && !Eq(state, "FG_SPEED", "UHQ")))))
            { reason = "requested AV1 SFE mode is not supported by current speed, GPU, or grav1synth capabilities"; return false; }
            if (svp && !NvencInterpolationCore.Enabled(state) && !SharedSvpCore.TryDependencies(appRoot, out reason)) return false;
            string frucFfmpeg = Get(state, "FG_FRUC_USE_MAIN_FFMPEG") == "0" ? Get(state, "FG_FRUC_FFMPEG_PATH") : ffmpeg;
            if(preprocessing.Length>0 && !PreprocessingCore.TryCheckTool(ffmpeg,out reason))return false;
            if(resize.Enabled && !ResizeCore.TryCheckTool(ffmpeg,resize,out reason))return false;
            string frucPerf = Get(state, "FG_FRUC_PERF").ToLowerInvariant();
            string frucGrid = Get(state, "FG_FRUC_GRID").ToLowerInvariant();
            if (fruc && !SharedFrucCore.TryPreflight(frucFfmpeg, frucPerf, frucGrid, out reason)) return false;
            if (fruc && hdrPreserve && !SharedFrucCore.TryPreflight10(frucFfmpeg, frucPerf, frucGrid, out reason)) return false;
            bool simpleRoute=Av1RouteOptimizationCore.SimpleSource(input,info,state,sfeEngines);
            bool directOutput=simpleRoute && !interpolate && !grainEnabled;
            bool directFruc=simpleRoute && fruc && !grainEnabled &&
                string.Equals(Path.GetFullPath(frucFfmpeg),Path.GetFullPath(ffmpeg),StringComparison.OrdinalIgnoreCase);
            if (interpolate && !directFruc)
            {
                string encoderHelp;
                if (!SharedFrucCore.Run(fruc ? frucFfmpeg : ffmpeg, "-hide_banner -h encoder=ffv1", 5000, out encoderHelp, out reason)) return false;
                if (!encoderHelp.Contains("Encoder ffv1")) { reason = "Interpolation FFmpeg lacks FFV1 lossless intermediate encoder"; return false; }
            }
            if (!File.Exists(ffmpeg)) { reason = "ffmpeg.exe not found: " + ffmpeg; return false; }
            if (metadataGrain && !File.Exists(grav1synth)) { reason = "grav1synth.exe not found: " + grav1synth; return false; }

            string aqStrength = Get(state, "FG_HEVC_SPATIAL_AQ");
            if (string.IsNullOrEmpty(aqStrength)) aqStrength = "8";
            if (aqStrength != "0" && aqStrength != "4" && aqStrength != "8" && aqStrength != "10" && aqStrength != "12" && aqStrength != "15")
            { reason = "unsupported AV1 spatial AQ strength"; return false; }

            string tempRoot;
            if (!TryResolveTempRoot(input, state, out tempRoot, out reason)) return false;
            string lutCompatPath = "", lutCompatName = "", lutOpacity = "";
            if (!TryPrepareLut(ffmpeg, state, out lutCompatPath, out lutCompatName, out lutOpacity, out reason)) return false;
            if(hdrPreserve && !string.IsNullOrEmpty(lutCompatName)) {
                colorFilter+=HdrColorProcessingCore.Lut(info,EscapeFilterPath(lutCompatName),lutOpacity);
                lutCompatName=""; // Already applied in the shared HDR prefix; keep the LUT file for execution.
            }
            string subtitleAss;
            if (!NativeSubtitlePrepareCore.TryPrepare(ffmpeg, ffprobe, input, state, frame.ActiveWidth, frame.ActiveHeight, out subtitleAss, out reason))
            {
                TryDelete(lutCompatPath);
                return false;
            }
            string encodeWorkingDirectory = string.IsNullOrEmpty(lutCompatPath) ? tempRoot : Path.GetDirectoryName(lutCompatPath);
            string subtitleFilterPath;
            if(!NativeSubtitlePrepareCore.TryStageForFilter(subtitleAss,encodeWorkingDirectory,out subtitleFilterPath,out reason))
            {TryDelete(lutCompatPath);TryDelete(subtitleAss);return false;}
            string subtitleFilter=NativeSubtitlePrepareCore.BurnInFilter(subtitleFilterPath);
            string filterGraph = BuildFilterGraph(info, frame, state, deinterlaceFilter, colorFilter, lutCompatName, lutOpacity, subtitleFilter,
                proceduralGrain, fgsimGrain, metadataGrain, hdrPreserve);
            if (hdrPreserve && !string.IsNullOrWhiteSpace(filterGraph)) filterGraph =