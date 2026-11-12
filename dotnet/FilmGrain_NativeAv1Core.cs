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
            if(!ResizeCore.TryBuild(info,state,frame,out resize,out reason))return false;
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
            if(resize.Enabled && !ResizeCore.TryCheckTool(ffmpeg,out reason))return false;
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
            if (hdrPreserve && !string.IsNullOrWhiteSpace(filterGraph)) filterGraph = filterGraph.Replace("[vout]", BuildHdrFrameFilter(info) + "[vout]");
            string job = "__FGS_AV1_NATIVE_" + Guid.NewGuid().ToString("N");
            string cleanIvf = Path.Combine(tempRoot, job + "_clean.ivf");
            string grainIvf = Path.Combine(tempRoot, job + "_grain.ivf");
            bool mergedHdr=SharedHdrPreprocessCore.CanMergeEncode(hdrToSdr,interpolate,info.IsInterlaced);
            // A separate FRUC-only tool must not acquire HDR filter dependencies from the main FFmpeg.
            bool mergedFrucHdr=hdrToSdr&&fruc&&!info.IsInterlaced&&
                string.Equals(Path.GetFullPath(frucFfmpeg),Path.GetFullPath(ffmpeg),StringComparison.OrdinalIgnoreCase);
            bool needsHdrWorkfile=hdrToSdr && !mergedHdr && !mergedFrucHdr;
            string hdrWorkfile = needsHdrWorkfile || separateDeint ? Path.Combine(tempRoot, job + (hdrToSdr ? "_hdr2sdr.mkv" : "_fields.mkv")) : "";
            string frucInput = needsHdrWorkfile || separateDeint ? hdrWorkfile : input;
            string frucWorkfile = interpolate ? Path.Combine(tempRoot, job + (fruc ? "_fruc.mkv" : "_svp.mkv")) : "";
            string encodeInput = interpolate && !directFruc ? frucWorkfile : frucInput;

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

            if(resizeBars.Length>0)filterGraph=filterGraph.Replace("[vout]",resizeBars+"[vout]");
            string pixelFull = grainEnabled && !metadataGrain ? filterGraph : null;
            string pixelClean = null;
            if (pixelFull != null)
            {
                pixelClean = BuildFilterGraph(info,frame,state,deinterlaceFilter,colorFilter,lutCompatName,lutOpacity,subtitleFilter,
                    proceduralGrain || hdrPreserve,fgsimGrain && !hdrPreserve,false,hdrPreserve).Replace("[baseout]",",format=p010le[vout]");
                if(hdrPreserve)pixelClean=pixelClean.Replace("[vout]",BuildHdrFrameFilter(info)+"[vout]");
                if(resizeBars.Length>0)pixelClean=pixelClean.Replace("[vout]",resizeBars+"[vout]");
                filterGraph=PixelGrainStrengthCore.Wrap(filterGraph,pixelClean,pixelPercent);
            }
            bool hdrStaticStream=hdrPreserve && NvencHdr10StreamCore.HasSourceHdr10(info);
            bool hdrOrdinaryStream=hdrPreserve && !interpolate && !deinterlace && !resize.Enabled && preprocessing.Length==0 && colorFilter.Length==0 && frame.Filter.Length==0 && string.IsNullOrEmpty(lutCompatName) && string.IsNullOrEmpty(subtitleAss) && (!grainEnabled||metadataGrain) && NvencHdr10StreamCore.Eligible(info,state);
            // grav1synth inspect derives model event times from nominal FPS; retain its old clock for the existing HDR ISO-strength fallback.
            bool hdrOrdinaryClock=hdrOrdinaryStream && !(metadataGrain && Eq(state,"FG_AV1_GRAIN_MODE","ISO") && Av1GrainStrengthCore.Read(state)!=100);
            List<string> encode = new List<string>();
            Add(encode, "-hide_banner", "-stats", "-progress", "pipe:2", "-stats_period", "0.5", "-y");
            if (directFruc || resize.Enabled || fgsimGrain || (bwdifVulkan && !fieldWorkfile && !separateDeint)) Add(encode, "-init_hw_device", VulkanDevicePolicy.Spec("vk"), "-filter_hw_device", "vk");
            if (interpolate) Add(encode, "-copyts");
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
            if(svp && !NvencInterpolationCore.Enabled(state))SharedInterpolationEncodeCore.NormalizeRenderedCadence(encode,fpsText);
            if (interpolate) Add(encode, "-enc_time_base:v",fpsText.Contains("/")?fpsText.Split('/')[1]+"/"+fpsText.Split('/')[0]:"1/"+fpsText,"-fps_mode:v", "passthrough");
            else if(hdrOrdinaryClock) Add(encode,"-enc_time_base:v","demux","-fps_mode:v","passthrough");
            else Add(encode, "-r", fpsText, "-fps_mode:v", "cfr");
            if (hdrPreserve) Add(encode, "-color_range", HdrRange(info), "-color_primaries", HdrPrimaries(info), "-color_trc", info.ColorTransfer, "-colorspace", HdrSpace(info));
            else if (hdrToSdr) Add(encode, "-color_range", "tv", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709");
            if (!interpolate) SharedOrdinaryTimelineCore.NormalizeVideoOnly(encode);
            NvencRateControlCore.Apply(encode,state);
            NvencMultipassCore.Apply(encode,state,"AV1",caps);
            Add(encode, "-f", "ivf", cleanIvf);

            List<string> apply = new List<string>();
            if (metadataGrain)
            {
                Add(apply, "apply", cleanIvf, "-o", grainIvf);
                if (!string.IsNullOrWhiteSpace(grainArgs)) Add(apply, grainArgs.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
                Add(apply, "--replace", "-y");
            }

            int grainPercent = metadataGrain ? Av1GrainStrengthCore.Read(state) : 100;
            bool directTableScale=metadataGrain && grainPercent!=100 && Eq(state,"FG_AV1_GRAIN_MODE","TABLE");
            bool directPresetScale=metadataGrain && grainPercent!=100 && Eq(state,"FG_AV1_GRAIN_MODE","PRESET");
            bool postScale=grainPercent!=100 && !directTableScale && !directPresetScale;
            string strengthIvf = grainIvf + ".strength.ivf";
            string remuxVideo = metadataGrain ? (postScale ? strengthIvf : grainIvf) : cleanIvf;
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
            AudioOutputCore.Apply(remux,state,info,container=="MKV",false,false);

            BridgeExecutionRequest request = new BridgeExecutionRequest();
            request.PixelGrainHdr = hdrPreserve;
            request.PixelGrainEncodeArguments = encode;
            request.PixelGrainFullFilter = pixelFull;
            request.PixelGrainCleanFilter = pixelClean;
            request.ToolPath = ffmpeg;
            request.WorkingDirectory = tempRoot;
            request.InputFiles = new List<string> { input };
            if (needsHdrWorkfile) request.InputFiles.Add(hdrWorkfile);
            request.Environment = new Dictionary<string, string>(state, StringComparer.OrdinalIgnoreCase);
            if (svp && File.Exists(Path.Combine(appRoot, "_OpenSVPFlow", "_UserConfig", "vapoursynth", "vapoursynth.toml")))
                request.Environment["APPDATA"] = Path.Combine(appRoot, "_OpenSVPFlow", "_UserConfig");
            request.DirectProcess = true;
            request.OutputFiles = new List<string> { output };
            
            request.TemporaryFiles = metadataGrain ? new List<string> { cleanIvf, grainIvf } : new List<string> { cleanIvf };
            if (needsHdrWorkfile || separateDeint) request.TemporaryFiles.Add(hdrWorkfile);
            if (interpolate && !directFruc) request.TemporaryFiles.Add(frucWorkfile);
            if (hdrPreserve) request.PostRunValidation = delegate { return VerifyHdrOutput(ffprobe, output, info); };
            if (!string.IsNullOrEmpty(lutCompatPath)) request.TemporaryFiles.Add(lutCompatPath);
            if (!string.IsNullOrEmpty(subtitleAss)) request.TemporaryFiles.Add(subtitleAss);
            if (!string.IsNullOrEmpty(subtitleFilterPath)&&subtitleFilterPath!=subtitleAss) request.TemporaryFiles.Add(subtitleFilterPath);
            request.ProgressDurationSeconds = ParseDuration(info.Duration);
            double duration = request.ProgressDurationSeconds;
            int preStages = (needsHdrWorkfile || separateDeint ? 1 : 0) + (interpolate && !directFruc ? 1 : 0);
            int encodeStages = metadataGrain ? 3 : 2;
            
            int totalStages = preStages + encodeStages;
            List<BridgeExecutionStage> directStages = new List<BridgeExecutionStage>();
            if (needsHdrWorkfile || separateDeint) directStages.Add(Stage(ffmpeg, tempRoot, hdrToSdr ? SharedHdrPreprocessCore.BuildToSdr(input,hdrWorkfile,info,tonemap,fieldWorkfile || hdrOffSdr,fieldWorkfile?fieldFilter:"") : SharedHdrPreprocessCore.BuildDeinterlaced(input,hdrWorkfile,info,fieldFilter), duration, 1, totalStages, hdrToSdr ? "HDR 转 SDR / " + tonemapLabel : "Shared field deinterlace / separate Vulkan device"));
            if(needsHdrWorkfile)SharedHdrPreprocessCore.AttachAutomatic(directStages[directStages.Count-1],tonemap);
            if (fruc && !directFruc)
            {
                BridgeExecutionStage preprocess = SharedFrucCore.CreateVideoPreprocess(frucFfmpeg, ffprobe, frucInput, frucWorkfile,
                    tempRoot, fpsText, frucPerf, frucGrid, duration, needsHdrWorkfile ? 2 : 1, totalStages, hdrPreserve);
                preprocess.FrucTimelineReferenceInput = input;
                if(hdrPreserve) request.ExecutionNotice = SharedFrucCore.HdrNotice;
                if(mergedFrucHdr)SharedHdrPreprocessCore.AttachMergedFruc(preprocess,input,info,tonemap,caps.GpuName);
                directStages.Add(preprocess);
            }
            if (svp)
            {
                BridgeExecutionStage svpStage=SharedSvpCore.CreateVideoPreprocess(appRoot,ffmpeg,ffprobe,frucInput,frucWorkfile,
                    tempRoot,fpsText,state,duration,hdrToSdr?2:1,totalStages);
                if (hdrToSdr) SharedHdrPreprocessCore.AttachValidation(svpStage,ffprobe,hdrWorkfile,info);
                directStages.Add(svpStage);
            }
            if (metadataGrain)
            {
                directStages.Add(Stage(ffmpeg, encodeWorkingDirectory, encode, duration, preStages + 1, totalStages, "AV1 Main10 encode"));
                var applyGrainStage=Stage(grav1synth, tempRoot, apply, duration, preStages + 2, totalStages, "AV1 Film Grain metadata");
                if(directTableScale)Av1GrainStrengthCore.AttachTableScale(applyGrainStage,apply,Get(state,"FG_AV1_GRAIN_TABLE"),grainIvf+".scaled.tbl",grainPercent,request.TemporaryFiles);
                if(directPresetScale)Av1GrainStrengthCore.AttachPresetScale(applyGrainStage,apply,grainIvf+".preset-scaled.tbl",grainPercent,request.TemporaryFiles);
                var strengthStages=postScale?Av1GrainStrengthCore.Stages(grav1synth,tempRoot,delegate { return grainIvf; },strengthIvf,grainPercent,request.TemporaryFiles):new List<BridgeExecutionStage>();
                if(postScale && Eq(state,"FG_AV1_GRAIN_MODE","ISO") && !info.IsHdr && !info.IsInterlaced && !interpolate && caps.Grav1synthVersion=="0.2.2" && Av1RouteOptimizationCore.Enabled())
                {
                    directStages[directStages.Count-1].StageText+=" / ISO: provisional stage total until model check";
                    Av1IsoStrengthCore.Attach(applyGrainStage,strengthStages,ffmpeg,grav1synth,tempRoot,cleanIvf,grainIvf,strengthIvf,grainArgs.Split(new char[]{'\n'},StringSplitOptions.RemoveEmptyEntries),grainPercent,request.TemporaryFiles,null,delegate {
                        int count=0;foreach(var active in directStages)if(!strengthStages.Contains(active))active.StageCurrent=++count;
                        foreach(var active in directStages)if(!strengthStages.Contains(active))active.StageTotal=count;
                    });
                }
                directStages.Add(applyGrainStage);
                directStages.AddRange(strengthStages);
                directStages.Add(Stage(ffmpeg, tempRoot, remux, duration, preStages + 3, totalStages, "AV1 container remux"));
            }
            else
            {
                string grainStage = !grainEnabled ? "AV1 Main10 encode / 颗粒：关闭" : fgsimGrain ? "AV1 FGSIM pixel-grain encode" : "AV1 Digital Grain pixel-grain encode";
                directStages.Add(Stage(ffmpeg, encodeWorkingDirectory, encode, duration, preStages + 1, totalStages, grainStage));
                directStages.Add(Stage(ffmpeg, tempRoot, remux, duration, preStages + 2, totalStages, "AV1 container remux"));
            }
            
            if(directOutput) {
                var direct=Av1RouteOptimizationCore.DirectContainer(encode,output,state,info,container=="MKV");
                var stage=Stage(ffmpeg,encodeWorkingDirectory,direct,duration,1,1,"AV1 direct container / no IVF");
                SharedOrdinaryTimelineCore.Attach(stage,ffprobe,input);
                directStages.Clear();directStages.Add(stage);
                request.PixelGrainEncodeArguments=direct;
                request.TemporaryFiles.Remove(cleanIvf);
                request.ExecutionNotice="[AV1 route] direct container; no clean IVF/remux; existing audio policy and shared source clock.";
            } else if(directFruc) {
                var stage=directStages[0];
                var direct=new List<string>(encode);int graph=direct.IndexOf("-filter_complex");
                // Reproduce the old SDR FFV1/MKV boundary: 8-bit 4:2:0 and millisecond time base.
                // This pilot does not change the existing FRUC precision policy.
                direct[graph+1]=direct[graph+1].Replace("[0:v:0]","[0:v:0]"+SharedFrucCore.InputFilter(fpsText,frucPerf,frucGrid,false)+"format=yuv420p,settb=expr=1/1000,");
                stage.OriginalArguments=new List<string>(direct);stage.DirectArguments=SharedFrucCore.JoinArguments(direct);
                stage.FrucArguments=new List<string>(direct);stage.FrucProbePath=ffprobe;
                stage.StageText+=" / FRUC direct, no FFV1";
                request.PixelGrainEncodeArguments=direct;
                request.ExecutionNotice="[AV1 route] FRUC direct to AV1; no FFV1; IVF/remux retained; pixel effects disabled on this route.";
            } else if(Av1RouteOptimizationCore.Enabled()) {
                request.ExecutionNotice="[AV1 encode route] IVF encode/container stages retained; direct-output/FRUC eligibility not met. Grain metadata optimization is selected independently.";
            }

            if(mergedHdr) {
                SharedHdrPreprocessCore.AttachMergedEncode(directStages[preStages],ffprobe,input,info,tonemap,caps.GpuName,true,Eq(state,"FG_FPS_MODE","SOURCE"));
                directStages[preStages].StageText+=" / HDR merged";
            }
            if(fieldWorkfile || separateDeint || hdrOffSdr)SharedHdrPreprocessCore.AttachFieldValidation(directStages[preStages],ffprobe,hdrWorkfile,info,hdrToSdr,!hdrOffSdr);
            // IVF and grain synthesis own a local video clock; always restore the original A/V clock once.
            foreach (BridgeExecutionStage stage in directStages)
                    if (stage.StageText.StartsWith("AV1 container remux", StringComparison.Ordinal))
                        SharedFrucCore.AttachAv1RemuxTimeline(stage, ffprobe, remuxVideo, input, remux, !interpolate ? "ordinary" : fruc ? "FRUC" : "OpenSVPFlow");
            if (postScale) Av1GrainStrengthCore.NumberStages(directStages);
            request.DirectStages = directStages;
            if(info.IsHdr && info.IsInterlaced && !deinterlace)request.ExecutionNotice="[Native .NET] 反交错 OFF：沿用不反交错的逐帧处理，不启用 bob/光流或双倍场率；不保证隔行编码标记。 / Deinterlace OFF: frame processing without bob/interpolation; no interlaced-encoding flags are added.";
            if (fgsimGrain) FgsimTextureCore.AttachVerifiedDevice(request, fgsimHook, hdrPreserve);
            if(hdrStaticStream)
                NvencHdr10StreamCore.Attach(request,input,info,remuxVideo,hdrOrdinaryClock);
            if(hdrPreserve&&!hdrStaticStream)request.ExecutionNotice=(request.ExecutionNotice??"")+Environment.NewLine+"[HDR metadata] 当前源格式未接入静态信息提取；现有输出校验仅核对颜色标记，不代表 mastering-display/CLLI 已保留。";
            if(hdrOrdinaryStream)request.ExecutionNotice+=Environment.NewLine+(hdrOrdinaryClock?"[NVENC HDR10 cadence] ordinary SOURCE/KEEP: demux time base and passthrough; original video-only origin normalization and final A/V clock binding retained.":"[NVENC HDR10 cadence] ISO non100% uses the existing nominal-FPS model-inspect fallback and retains legacy CFR; variable source cadence is not preserved by this combination. This combination is outside the R7 cadence fix.");
            HdrColorProcessingCore.Notice(request,info,state,hdrPreserve);
            prepared = new BridgePreparedExecution(request, false, ffmpeg, new List<string> { input });
            return true;
        }

        private static string BuildFilterGraph(MediaProbeInfo info, CinematicFramePlan frame, IDictionary<string,string> state,
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
            baseFilters=LutStrengthCore.OptimizeGraph(baseFilters);
            baseFilters += "[baseout]";
            if (!proceduralGrain && !fgsimGrain) return baseFilters.Replace("[baseout]", "[vout]");
            return baseFilters;
        }

        private static string AppendFgsimFilter(string baseGraph, string hook)
        {
            return baseGraph + ";[baseout]hwupload,libplacebo=format=yuv420p:custom_shader_path=" +
                EscapeFgsimShaderPath(hook) + ",hwdownload,format=yuv420p,format=p010le[vout]";
        }

        private static string AppendHdrFgsimFilter(MediaProbeInfo info, CinematicFramePlan frame, string deinterlaceFilter,
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
            return FgsimTextureCore.TryPrepareHook(appRoot, preset, out hookPath, out reason);
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
                int lutPercent=NormalizeLutStrength(Get(state,"FG_LUT_STRENGTH"));
                opacity=LutStrengthCore.BakeFile(compatPath,lutPercent)?"1.00":(lutPercent/100.0).ToString("0.00",CultureInfo.InvariantCulture);
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
            // Opacity was resolved when preparing the LUT; baked tables use full strength.
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

        private static int NormalizeLutStrength(string value) { int v; if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return 75; return Math.Max(0,Math.Min(100,v)); }

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
                ProcessStartInfo psi = new ProcessStartInfo(ffprobe, "-v error -select_streams v:0 -show_entries stream=pix_fmt,color_range,color_primaries,color_transfer,color_space -of default=noprint_wrappers=1 " + Quote(output));
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
                    if (!actual.TryGetValue("pix_fmt", out value) || (value != "yuv420p10le" && value != "p010le") || !actual.TryGetValue("color_range", out value) || value != HdrRange(info) || !actual.TryGetValue("color_primaries", out value) || value != HdrPrimaries(info) || !actual.TryGetValue("color_transfer", out value) || value != info.ColorTransfer || !actual.TryGetValue("color_space", out value) || value != HdrSpace(info))
                        return "HDR signaling mismatch: " + data.Trim().Replace(Environment.NewLine, " / ");
                    return "";
                }
            }
            catch (Exception ex) { return "HDR signaling probe failed: " + ex.Message; }
        }

        private static BridgeExecutionStage Stage(string tool, string cwd, IList<string> args, double duration, int current, int total, string text)
        {
            StringBuilder command = new StringBuilder();
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) command.Append(' ');
                command.Append(Quote(args[i]));
            }
            return new BridgeExecutionStage { ToolPath = tool, WorkingDirectory = cwd, OriginalArguments = new List<string>(args), DirectArguments = command.ToString(), ProgressDurationSeconds = duration, StageCurrent = current, StageTotal = total, StageText = text };
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

        internal static string ReadConfigValue(string appRoot, string key)
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

namespace FilmGrainStudioPreview
{
    // H.264 encoder parameters for the independent AV1 grain bake tool.
    internal static class SharedBakeH264Core
    {
        internal static void AddNvenc(IList<string> args)
        {
            foreach (string value in new[] { "-c:v","h264_nvenc","-profile:v","high","-preset","p7","-rc","vbr",
                "-multipass","fullres","-spatial-aq","1","-aq-strength","8","-temporal-aq","0","-bf","3","-rc-lookahead","27" }) args.Add(value);
        }
        internal static bool TryNvenc(string ffmpeg, out string reason)
        {
            reason=""; string output,error;
            var args=new List<string>{"-hide_banner","-v","error","-nostdin","-f","lavfi","-i","color=s=1920x1080:r=30",
                "-frames:v","40","-vf","format=nv12"};
            AddNvenc(args); args.AddRange(new[]{"-b:v","1000k","-f","null","NUL"});
            if(SharedFrucCore.Run(ffmpeg,SharedFrucCore.JoinArguments(args),15000,out output,out error))return true;
            reason="H.264 NVENC 预检失败；请改选 x264。不会静默改变编码器或参数。 / NVENC preflight failed: "+error;
            return false;
        }
    }

    internal static class SharedBakeHevcCore
    {
        internal static void AddEncoder(IList<string> args,bool bFrames)
        {
            foreach(string v in new[]{"-c:v","hevc_nvenc","-profile:v","main10","-pix_fmt","p010le","-preset","p7","-tune","hq",
                "-rc","vbr","-multipass","fullres","-spatial-aq","1","-aq-strength","8","-temporal-aq","0",
                "-rc-lookahead","27","-bf",bFrames?"4":"0","-tag:v","hvc1"})args.Add(v);
        }
        internal static bool TryPreflight(string ffmpeg,out bool bFrames,out string notice,out string reason)
        {
            bFrames=true;notice="";reason="";string output,error;
            foreach(bool useB in new[]{true,false})
            {
                var args=new List<string>{"-hide_banner","-v","error","-nostdin","-f","lavfi","-i","color=s=1920x1080:r=30",
                    "-frames:v","40","-vf","format=p010le"};
                AddEncoder(args,useB);args.AddRange(new[]{"-b:v","1000k","-f","null","NUL"});
                if(SharedFrucCore.Run(ffmpeg,SharedFrucCore.JoinArguments(args),15000,out output,out error))
                {
                    bFrames=useB;
                    notice=useB?"[AV1 Bake] HEVC Main10 preflight: B-frames=4 PASS":
                        "[AV1 Bake] HEVC B 帧参数组合预检未通过；bf=0 实际编码通过。 / B-frame preflight failed; verified bf=0.";
                    return true;
                }
                reason=error;
            }
            reason="HEVC NVENC Main10 预检失败，不改变编码器或位深。 / HEVC NVENC preflight failed: "+reason;return false;
        }
    }

    internal static class NativeAv1BakeCore
    {
        private static string Get(IDictionary<string,string> state,string key,string fallback="")
        {string value;return state!=null&&state.TryGetValue(key,out value)?value??"":fallback;}
        private static bool IsHevc(string encoder){return encoder=="HEVC_NVENC"||encoder=="X265";}
        private static bool IsNvenc(string encoder){return encoder=="HEVC_NVENC"||encoder=="H264_NVENC";}
        private static string NormalizeEncoder(string encoder){return encoder=="NVENC"?"H264_NVENC":encoder;}
        private static bool IsCq(IDictionary<string,string> state){return Get(state,"FG_BAKE_RATE_CONTROL","VBR")=="CQ";}
        private static string Quality(IDictionary<string,string> state,string encoder)
        {
            string value=Get(state,"FG_BAKE_CQ");double q;
            if(!NvencRateControlCore.TryQuality(IsHevc(encoder)?"HEVC":"H264",value,out q))
                throw new InvalidDataException("烘焙 CQ/CRF 必须为 1–51。 / Invalid bake CQ/CRF: "+value);
            return q.ToString("R",CultureInfo.InvariantCulture);
        }
        private static IDictionary<string,string> EncoderState(IDictionary<string,string> state,string encoder)
        {
            var copy=new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase);
            copy["FG_MODE"]=encoder=="HEVC_NVENC"?"HEVC":encoder=="H264_NVENC"?"H264":encoder;
            copy["FG_NVENC_RATE_CONTROL"]=Get(state,"FG_BAKE_RATE_CONTROL","VBR");
            copy["FG_NVENC_CQ"]=Get(state,"FG_BAKE_CQ","22");
            copy["FG_NVENC_CQ_LIMIT_ENABLED"]=Get(state,"FG_BAKE_CQ_LIMIT_ENABLED","false").ToUpperInvariant();
            copy["FG_NVENC_CQ_MAXRATE"]=Get(state,"FG_BAKE_CQ_MAXRATE","100");
            copy["FG_SFE_ENGINES"]=Get(state,"FG_BAKE_SFE","0");
            return copy;
        }
        internal static bool GpuDecodeCompatible(string encoder,MediaProbeInfo info)
        {
            encoder=NormalizeEncoder(encoder);
            if(info==null)return false;
            // HEVC NVENC already uses scale_cuda=format=p010le: NV12 -> P010
            // is converted on the GPU and proved by the two-frame NVENC preflight.
            // CPU x265 still downloads P010 directly, so it needs 10-bit input.
            return (IsHevc(encoder)&&info.PixelFormat=="yuv420p10le") ||
                (encoder=="HEVC_NVENC"&&info.PixelFormat=="yuv420p") ||
                (!IsHevc(encoder)&&info.PixelFormat=="yuv420p");
        }
        private static string DecodeFilter(string encoder,bool gpu)
        {
            if(!gpu)return encoder=="X265"?"format=yuv420p10le":IsHevc(encoder)?"format=p010le":"format=yuv420p";
            if(encoder=="HEVC_NVENC")return "scale_cuda=format=p010le";
            if(encoder=="H264_NVENC")return "scale_cuda=format=nv12";
            return encoder=="X265"?"hwdownload,format=p010le,format=yuv420p10le":"hwdownload,format=nv12,format=yuv420p";
        }
        private static List<string> BuildArguments(string input,string output,string encoder,long bitrate,IDictionary<string,string> state,MediaProbeInfo info,HardwareCapabilitySnapshot caps,bool useBFrames)
        {
            bool gpu=Get(state,"FG_BAKE_GPU_DECODE")=="1";
            var a=new List<string>{"-hide_banner","-stats","-progress","pipe:2","-stats_period","0.5","-n"};
            if(gpu)a.AddRange(new[]{"-hwaccel","cuda","-hwaccel_output_format","cuda"});
            else a.AddRange(new[]{"-c:v","libdav1d","-filmgrain","1"});
            a.AddRange(new[]{"-i",input,"-map","0:v:0","-map","0:a:0?","-map_metadata","0","-vf",DecodeFilter(encoder,gpu)});
            var codecState=EncoderState(state,encoder);
            if(encoder=="HEVC_NVENC"||encoder=="H264_NVENC")
            {
                if(encoder=="HEVC_NVENC"){
                    SharedBakeHevcCore.AddEncoder(a,useBFrames);
                    // NVDEC -> scale_cuda yields AV_PIX_FMT_CUDA hardware frames with
                    // P010 as the underlying surface. A software -pix_fmt p010le here
                    // makes FFmpeg insert auto_scale(cuda -> system memory), which
                    // cannot negotiate; NVENC reads the CUDA frames directly instead.
                    if(gpu)NvencMainOptionsCore.Set(a,"-pix_fmt",null);
                }
                else SharedBakeH264Core.AddNvenc(a);
                string preset=Get(state,"FG_BAKE_PRESET","p7");
                if(Array.IndexOf(new[]{"p1","p2","p3","p4","p5","p6","p7"},preset)<0)throw new InvalidDataException("Invalid NVENC preset: "+preset);
                NvencMainOptionsCore.Set(a,"-preset",preset);
                if(encoder=="HEVC_NVENC")NvencMainOptionsCore.Set(a,"-tune","hq");
                int aq;
                string family=encoder=="HEVC_NVENC"?"HEVC":"H264";
                if(!int.TryParse(Get(state,"FG_"+family+"_SPATIAL_AQ","8"),out aq)||Array.IndexOf(new[]{0,4,8,10,12,15},aq)<0)throw new InvalidDataException("Invalid spatial AQ");
                bool aqAvailable=caps!=null&&(family=="HEVC"?caps.HevcSpatialAq:caps.H264SpatialAq);
                bool temporal=Get(state,"FG_"+family+"_TEMPORAL_AQ","false")=="true";
                bool temporalAvailable=caps!=null&&(family=="HEVC"?caps.HevcTemporalAq:caps.H264TemporalAq);
                if(temporal&&!temporalAvailable)throw new InvalidDataException(family+" temporal AQ is not supported by capability cache");
                NvencMainOptionsCore.Set(a,"-spatial-aq",aq>0&&aqAvailable?"1":"0");
                NvencMainOptionsCore.Set(a,"-aq-strength",aq>0&&aqAvailable?aq.ToString(CultureInfo.InvariantCulture):null);
                NvencMainOptionsCore.Set(a,"-temporal-aq",temporal?"1":"0");
                if(IsCq(state))NvencRateControlCore.Apply(a,codecState);
                else a.AddRange(new[]{"-b:v",bitrate+"k","-maxrate",(bitrate*3)+"k","-bufsize",(bitrate*6)+"k"});
                NvencMultipassCore.Apply(a,codecState,family,caps);
                NvencMainOptionsCore.Apply(a,codecState,family,caps);
            }
            else if(encoder=="X265")
            {
                // The ordinary CPU x265 encoder supplies the same preset, tuning and CRF checks.
                CpuEncoderCore.AddEncoderArgs(a,codecState,true,bitrate,bitrate*3,bitrate*6,false,info.AvgFrameRate);
            }
            else
            {
                string preset=Get(state,"FG_X264_PRESET","faster");
                if(Array.IndexOf(new[]{"faster","medium","slow"},preset)<0)throw new InvalidDataException("Invalid x264 preset");
                a.AddRange(new[]{"-c:v","libx264","-profile:v","high","-pix_fmt","yuv420p","-preset",preset});
                SharedGrainCore.AddX264Tune(a,SharedGrainCore.GrainTune(state,"FG_X264_GRAIN_TUNE",true));
                if(IsCq(state))a.AddRange(new[]{"-crf",Quality(state,encoder)});
                else a.AddRange(new[]{"-b:v",bitrate+"k","-maxrate",(bitrate*3)+"k","-bufsize",(bitrate*6)+"k"});
            }
            if(encoder=="X265"&&!IsCq(state))a.AddRange(new[]{"-maxrate",(bitrate*3)+"k","-bufsize",(bitrate*6)+"k"});
            a.AddRange(new[]{"-c:a","aac","-b:a","256k","-ac","2","-ar","48000","-movflags","+faststart",output});
            AudioOutputCore.Apply(a,state,info,false,true,true);
            return SharedHdrPreprocessCore.BuildBakeTimeline(a,false);
        }
        private static bool TryGpuDecode(string ffmpeg,string input,string encoder,out string reason)
        {
            reason="";string output,error;
            var args=new List<string>{"-hide_banner","-nostdin","-loglevel","debug","-hwaccel","cuda","-hwaccel_output_format","cuda",
                "-i",input,"-map","0:v:0","-frames:v","2","-vf",DecodeFilter(encoder,true),"-an"};
            // Prove the CUDA -> HEVC NVENC frame handoff, not just the CUDA decoder.
            // In particular, do not add a software -pix_fmt p010le after scale_cuda.
            if(encoder=="HEVC_NVENC")args.AddRange(new[]{"-c:v","hevc_nvenc","-profile:v","main10","-preset","p4","-bf","0"});
            args.AddRange(new[]{"-f","null","-"});
            if(!SharedFrucCore.Run(ffmpeg,SharedFrucCore.JoinArguments(args),30000,out output,out error)){
                reason="AV1 NVDEC grain bake preflight failed: "+error;return false;
            }
            if(output.IndexOf("Format cuda chosen by get_format",StringComparison.OrdinalIgnoreCase)<0){
                reason="AV1 NVDEC did not prove CUDA hardware decoding; refusing an unmarked software fallback.";return false;
            }
            return true;
        }
        // Read only metadata that is actually present in the first AV1 access unit.
        // Do not manufacture mastering or light-level values when the source omits them.
        private static Dictionary<string,long> ReadAv1Hdr10(string ffmpeg,string input,MediaProbeInfo info)
        {
            if(info.ColorTransfer!="smpte2084"||info.ColorPrimaries!="bt2020"||info.ColorSpace!="bt2020nc")
                throw new InvalidDataException("HDR10 AV1 static metadata requires PQ/BT.2020/bt2020nc source signals");
            string trace=SvtHdr10Core.Trace(ffmpeg,input);
            if(SvtHdr10Core.Field(trace,"color_primaries")!=9 || SvtHdr10Core.Field(trace,"transfer_characteristics")!=16 ||
                SvtHdr10Core.Field(trace,"matrix_coefficients")!=9)
                throw new InvalidDataException("AV1 HDR10 source bitstream color signaling conflicts with FFprobe");
            var values=new Dictionary<string,long>();
            bool mastering=Regex.IsMatch(trace,@"(?m)^.*metadata_type\s+[01]+\s*=\s*2\s*$");
            bool light=Regex.IsMatch(trace,@"(?m)^.*metadata_type\s+[01]+\s*=\s*1\s*$");
            if(mastering){
                for(int i=0;i<3;i++)foreach(string axis in new[]{"x","y"}){
                    string key="primary_chromaticity_"+axis+"["+i+"]";
                    values[key]=SvtHdr10Core.Field(trace,key);
                }
                foreach(string key in new[]{"white_point_chromaticity_x","white_point_chromaticity_y","luminance_max","luminance_min"})
                    values[key]=SvtHdr10Core.Field(trace,key);
            }
            if(light){values["max_cll"]=SvtHdr10Core.Field(trace,"max_cll");values["max_fall"]=SvtHdr10Core.Field(trace,"max_fall");}
            return values;
        }
        private static long Rescale(long value,long from,long to)
        {return checked((long)Math.Round((decimal)value*to/from,0,MidpointRounding.AwayFromZero));}
        private static string X265Hdr10Parameters(Dictionary<string,long> values)
        {
            var parts=new List<string>{"hdr-opt=1","cll="+(values.ContainsKey("max_cll")?"1":"0")};
            if(values.ContainsKey("luminance_max")){
                Func<int,string> point=delegate(int i){return "("+Rescale(values["primary_chromaticity_x["+i+"]"],65536,50000)+","+Rescale(values["primary_chromaticity_y["+i+"]"],65536,50000)+")";};
                parts.Add("master-display=G"+point(1)+"B"+point(2)+"R"+point(0)+"WP("+
                    Rescale(values["white_point_chromaticity_x"],65536,50000)+","+Rescale(values["white_point_chromaticity_y"],65536,50000)+")L("+
                    Rescale(values["luminance_max"],256,10000)+","+Rescale(values["luminance_min"],16384,10000)+")");
            }
            if(values.ContainsKey("max_cll"))parts.Add("max-cll="+values["max_cll"]+","+values["max_fall"]);
            return string.Join(":",parts.ToArray());
        }
        private static string VerifyHdr10(string ffmpeg,string output,MediaProbeInfo info,Dictionary<string,long> expected)
        {
            if(expected==null)return "AV1 Bake HDR10 source proof was not executed.";
            try{
                var actual=SvtHdr10Core.ReadHevcAvailable(SvtHdr10Core.Trace(ffmpeg,output),info.ColorRange=="pc"?1:0);
                foreach(string group in new[]{"luminance_max","max_cll"})
                    if(!expected.ContainsKey(group)&&actual.ContainsKey(group))return "AV1 Bake HDR10 output invented "+group+" not present in source";
                foreach(var pair in expected){
                    long value;
                    if(!actual.TryGetValue(pair.Key,out value))return "AV1 Bake HDR10 output missing "+pair.Key;
                    // AV1 fixed-point -> HEVC integer units -> AV1 rounding can lose one unit.
                    if(Math.Abs(value-pair.Value)>1)return "AV1 Bake HDR10 output changed "+pair.Key+": "+pair.Value+" -> "+value;
                }
                return null;
            }catch(Exception ex){return "AV1 Bake HDR10 HEVC bitstream verification failed: "+ex.Message;}
        }
        internal static string OutputPath(string input,IDictionary<string,string> state,long bitrate,bool nvenc)
        {
            string mode,custom;state.TryGetValue("FG_OUTPUT_MODE",out mode);state.TryGetValue("FG_OUTPUT_CUSTOM_DIR",out custom);
            string dir=string.Equals(mode,"CUSTOM",StringComparison.OrdinalIgnoreCase)?custom:Path.GetDirectoryName(input);
            if(string.IsNullOrWhiteSpace(dir))throw new InvalidOperationException("烘焙输出目录未配置。 / Output directory is not configured.");
            string selected=NormalizeEncoder(Get(state,"FG_BAKE_ENCODER"));
            string family=IsHevc(selected)?"HEVC_MAIN10":"H264";
            string quality=IsCq(state)?"CQ"+Quality(state,selected):bitrate.ToString(CultureInfo.InvariantCulture)+"k";
            return Path.Combine(dir,Path.GetFileNameWithoutExtension(input)+"_BAKED_"+selected+(family=="HEVC_MAIN10"?"_Main10":"")+"_"+quality+(Get(state,"FG_BAKE_GPU_DECODE")=="1"?"_NVDEC":"")+".mp4");
        }
        internal static bool TryPrepare(string appRoot,string input,MediaProbeInfo info,IDictionary<string,string> state,HardwareCapabilitySnapshot caps,
            out BridgePreparedExecution prepared,out string reason)
        {
            prepared=null;reason="";
            try
            {
                if(info==null || !info.HasVideo || !string.Equals(info.VideoCodec,"av1",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("烘焙需要 AV1 输入。 / Baking requires AV1 input.");
                if(info.IsInterlaced || info.Width<=0 || info.Height<=0 || info.Width%2!=0 || info.Height%2!=0)
                    throw new InvalidOperationException("烘焙需要偶数尺寸逐行视频。 / Progressive even dimensions required.");
                if(info.PixelFormat!="yuv420p" && info.PixelFormat!="yuv420p10le")
                    throw new InvalidOperationException("烘焙仅支持 4:2:0 8/10-bit。 / 4:2:0 8/10-bit required.");
                string encoder=NormalizeEncoder(Get(state,"FG_BAKE_ENCODER"));
                bool hevc=IsHevc(encoder),nvenc=IsNvenc(encoder),h264=!hevc;
                if(!hevc&&!nvenc&&encoder!="X264")throw new InvalidOperationException("Unsupported bake encoder: "+encoder);
                if(info.IsHdr&&h264)throw new InvalidOperationException("HDR AV1 请使用 HEVC NVENC 或 x265（Main10），H.264 路线仅支持 SDR。 / HDR requires HEVC Main10.");
                if(info.IsHdr&&info.PixelFormat!="yuv420p10le")throw new InvalidOperationException("HDR AV1 烘焙需要 10-bit 4:2:0 输入。 / HDR requires 10-bit 4:2:0.");
                bool gpu=Get(state,"FG_BAKE_GPU_DECODE")=="1";
                if(gpu&&!GpuDecodeCompatible(encoder,info))throw new InvalidOperationException("NVDEC 烘焙暂不支持此输入/输出位深转换；请取消 GPU 硬解。 / Select software decode for bit-depth conversion.");
                if(gpu&&(caps==null||!caps.Ready))throw new InvalidOperationException("NVDEC 烘焙需要有效的能力缓存及逐文件硬解预检。 / NVDEC capability cache unavailable.");
                long bitrate;string value;state.TryGetValue("FG_BAKE_BITRATE",out value);
                if(!long.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out bitrate)||bitrate<100||bitrate>500000)
                    throw new InvalidOperationException("烘焙码率必须为 100–500000 kbps。 / Invalid bake bitrate.");
                if(Get(state,"FG_BAKE_RATE_CONTROL","VBR")!="VBR"&&Get(state,"FG_BAKE_RATE_CONTROL")!="CQ")throw new InvalidDataException("Unsupported bake rate control");
                if(IsCq(state))Quality(state,encoder);
                string ffmpeg=Path.Combine(NativeAv1Core.ReadConfigValue(appRoot,"FFMPEG_DIR"),"ffmpeg.exe");
                string ffprobe=Path.Combine(Path.GetDirectoryName(ffmpeg),"ffprobe.exe");
                if(!File.Exists(ffmpeg)||!File.Exists(ffprobe))throw new FileNotFoundException("需要 FFmpeg 和 FFprobe。 / FFmpeg and FFprobe required.");
                string help,error;
                if(!gpu&&(!SharedFrucCore.Run(ffmpeg,"-hide_banner -h decoder=libdav1d",5000,out help,out error)||help.IndexOf("filmgrain",StringComparison.OrdinalIgnoreCase)<0))
                    throw new InvalidOperationException("当前 libdav1d 未提供已验证的颗粒合成选项。 / Verified libdav1d filmgrain option unavailable.");
                if(nvenc&&(caps==null||!caps.Ready||(hevc?!caps.HevcAvailable:!caps.H264Available)))throw new InvalidOperationException("NVENC capability cache unavailable for selected bake encoder");
                if(encoder=="H264_NVENC"&&!SharedBakeH264Core.TryNvenc(ffmpeg,out error))throw new InvalidOperationException(error);
                bool hevcBFrames=false;string hevcNotice="";
                if(encoder=="HEVC_NVENC"&&!SharedBakeHevcCore.TryPreflight(ffmpeg,out hevcBFrames,out hevcNotice,out error))throw new InvalidOperationException(error);
                var codecState=EncoderState(state,encoder);
                if(nvenc&&IsCq(state))NvencRateControlCore.RequireSupport(ffmpeg,codecState);
                if(encoder=="X265"&&!CpuEncoderCore.TryPreflight(ffmpeg,codecState,out error))throw new InvalidOperationException(error);
                if(gpu&&!TryGpuDecode(ffmpeg,input,encoder,out error))throw new InvalidOperationException(error);
                string output=Path.GetFullPath(OutputPath(input,state,bitrate,nvenc));
                if(File.Exists(output))throw new IOException("烘焙输出已存在，不覆盖："+output);
                string dir=Path.GetDirectoryName(output);Directory.CreateDirectory(dir);
                // Encode to our own unique file; final rename refuses collisions. Cleanup never owns pre-existing output.
                string staging=Path.Combine(dir,"__FGS_BAKE_"+Guid.NewGuid().ToString("N")+".mp4");
                var args=BuildArguments(input,staging,encoder,bitrate,state,info,caps,hevcBFrames);
                int overwrite=args.IndexOf("-y");if(overwrite>=0)args[overwrite]="-n";
                Stamp(args,"-color_range",info.ColorRange);Stamp(args,"-colorspace",info.ColorSpace);
                Stamp(args,"-color_trc",info.ColorTransfer);Stamp(args,"-color_primaries",info.ColorPrimaries);
                // H.264 may omit video_signal_type_present_flag for default limited range
                // when no colour description is supplied. Make the known source range
                // explicit in SPS VUI, without changing pixels or adding another encode.
                if(h264 && (info.ColorRange=="tv" || info.ColorRange=="pc"))
                    args.InsertRange(args.Count-1,new[]{"-bsf:v","h264_metadata=video_full_range_flag="+(info.ColorRange=="pc"?"1":"0")});
                double duration;double.TryParse(info.Duration,NumberStyles.Float,CultureInfo.InvariantCulture,out duration);
                var req=new BridgeExecutionRequest{ToolPath=ffmpeg,WorkingDirectory=dir,DirectProcess=true,
                    InputFiles=new List<string>{input},OutputFiles=new List<string>(),TemporaryFiles=new List<string>{staging},
                    Environment=new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase),ProgressDurationSeconds=duration,LogStageTimings=true,
                    ExecutionNotice="[AV1 Bake] "+(info.IsHdr?"HDR preserve":"SDR")+" / "+(gpu?"NVDEC apply_grain (experimental)"+(encoder=="HEVC_NVENC"&&info.PixelFormat=="yuv420p"?" / CUDA NV12 -> P010":""):"libdav1d filmgrain=1")+" -> "+encoder+" / "+(IsCq(state)?"CQ/CRF "+Quality(state,encoder):"VBR "+bitrate+"k")+". "+hevcNotice+"\n只合成源内已有颗粒；不额外加颗粒、插帧、调色或字幕。 / Existing grain only; no transformations.\n[AV1 Bake] output="+output};
                var original=new FileInfo(input);long length=original.Length;DateTime modified=original.LastWriteTimeUtc;
                var stage=new BridgeExecutionStage{ToolPath=ffmpeg,WorkingDirectory=dir,OriginalArguments=args,
                    DirectArguments=SharedFrucCore.JoinArguments(args),ProgressDurationSeconds=duration,StageCurrent=1,StageTotal=1,
                    StageText="AV1 "+(info.IsHdr?"HDR":"SDR")+" grain bake / "+encoder};
                Dictionary<string,long> expectedHdr=null;
                stage.BeforeRunPreparation=delegate {
                    var current=new FileInfo(input);
                    if(!current.Exists||current.Length!=length||current.LastWriteTimeUtc!=modified)throw new IOException("烘焙输入已改变。 / Input changed.");
                    if(File.Exists(output)||File.Exists(staging))throw new IOException("烘焙输出发生冲突，不覆盖。 / Output collision.");
                    return "[AV1 Bake] "+(gpu?"NVDEC film grain synthesis (4080 visual comparison pending)":"libdav1d film grain synthesis")+"; independent single-stage encode";
                };
                if(info.IsHdr&&info.ColorTransfer=="smpte2084")SvtHdr10Core.Before(stage,delegate {
                    expectedHdr=ReadAv1Hdr10(ffmpeg,input,info);
                    if(encoder=="X265"){
                        var current=HardwareDecodeCore.SplitArguments(stage.DirectArguments);
                        string parameters=X265Hdr10Parameters(expectedHdr);
                        int option=current.IndexOf("-x265-params");
                        if(option>=0&&option+1<current.Count)current[option+1]+=(current[option+1].Length>0?":":"")+parameters;
                        else current.InsertRange(current.Count-1,new[]{"-x265-params",parameters});
                        stage.DirectArguments=SharedFrucCore.JoinArguments(current);
                    }
                    return "[AV1 Bake HDR10] AV1 source MDCV="+(expectedHdr.ContainsKey("luminance_max")?"present":"absent")+
                        ", CLLI="+(expectedHdr.ContainsKey("max_cll")?"present":"absent")+"; HEVC bitstream will be verified before commit.";
                });
                req.DirectStages=new List<BridgeExecutionStage>{stage};
                req.PostRunValidation=delegate {
                    string json,probeError;
                    if(!SharedFrucCore.Run(ffprobe,SharedFrucCore.JoinArguments(new[]{"-v","error","-show_streams","-show_format","-of","json",staging}),15000,out json,out probeError))return "Bake output probe failed: "+probeError;
                    var actual=MediaProbeInfo.Parse(staging,json);
                    if(!actual.HasVideo||actual.VideoCodec!=(hevc?"hevc":"h264")||actual.PixelFormat!=(hevc?"yuv420p10le":"yuv420p")||(hevc && actual.VideoProfile!="Main 10")||actual.Width!=info.Width||actual.Height!=info.Height||actual.IsHdr!=info.IsHdr||
                       (info.HasAudio&&!actual.HasAudio))return "Bake output format validation failed.";
                    if(!EqualKnown(info.ColorRange,actual.ColorRange)||!EqualKnown(info.ColorSpace,actual.ColorSpace)||
                       !EqualKnown(info.ColorTransfer,actual.ColorTransfer)||!EqualKnown(info.ColorPrimaries,actual.ColorPrimaries))return "Bake output color signaling changed: source {range="+info.ColorRange+", matrix="+info.ColorSpace+", transfer="+info.ColorTransfer+", primaries="+info.ColorPrimaries+"}; output {range="+actual.ColorRange+", matrix="+actual.ColorSpace+", transfer="+actual.ColorTransfer+", primaries="+actual.ColorPrimaries+"}.";
                    if(info.IsHdr&&info.ColorTransfer=="smpte2084")return VerifyHdr10(ffmpeg,staging,info,expectedHdr);
                    return null;
                };
                req.PostRunCommit=delegate {
                    // Runner checks cancellation after validation, before this same-directory atomic rename.
                    File.Move(staging,output);req.OutputFiles.Add(output);
                };
                prepared=new BridgePreparedExecution(req,false,ffmpeg,new List<string>{input});return true;
            }
            catch(Exception ex){reason="AV1 烘焙准备失败 / Bake preparation failed: "+ex.Message;return false;}
        }
        private static bool EqualKnown(string source,string actual)
        {return string.IsNullOrWhiteSpace(source)||source=="unknown"||source=="unspecified"||source==actual;}
        private static void Stamp(List<string> args,string option,string value)
        {if(!string.IsNullOrWhiteSpace(value)&&value!="unknown"&&value!="unspecified")args.InsertRange(args.Count-1,new[]{option,value});}
    }
}

namespace FilmGrainStudioPreview
{
    internal static class Av1RouteOptimizationCore
    {
        internal static bool Enabled()
        { return Environment.GetEnvironmentVariable("FGS_AV1_ROUTES_LEGACY") != "1"; }
        static string Get(IDictionary<string,string> s,string k) {string v;return s.TryGetValue(k,out v)?v:"";}
        internal static bool SimpleSource(string input,MediaProbeInfo info,IDictionary<string,string> state,int sfe)
        {
            if(!Enabled() || info==null || info.IsHdr || info.IsInterlaced || sfe!=0 ||
                Get(state,"FG_FPS_MODE")!="SOURCE" || PreprocessingCore.Enabled(state) || ResizeCore.Enabled(state) ||
                Get(state,"FG_COLOR_ENABLED")=="1" || !string.IsNullOrWhiteSpace(Get(state,"FG_LUT_PATH")) ||
                Get(state,"FG_CINEMATIC_FRAME")=="1" || Get(state,"FG_SUBTITLE")=="1")return false;
            // Reuse the bounded MOV timing-table proof. Average FPS alone cannot rule out VFR.
            // Other containers/edits/reordered timing retain the old route without extra probes.
            return MovFieldOrderCore.HasRegularTimeline(input,info.AvgFrameRate);
        }
        internal static string Clock(string rate)
        {
            string[] p=rate.Split('/');long n,d;
            if(p.Length!=2 || !long.TryParse(p[0],out n) || !long.TryParse(p[1],out d) || n<=0 || d<=0)
                throw new InvalidDataException("AV1 source rate is not rational");
            return d.ToString(CultureInfo.InvariantCulture)+"/"+n.ToString(CultureInfo.InvariantCulture);
        }
        internal static List<string> DirectContainer(IList<string> encode,string output,IDictionary<string,string> state,MediaProbeInfo info,bool mkv)
        {
            var args=new List<string>(encode);int f=args.LastIndexOf("-f");
            if(f!=args.Count-3 || args[f+1]!="ivf")throw new InvalidOperationException("AV1 output boundary changed");
            args.RemoveRange(f,3);
            args.Remove("-an");args.Remove("-sn");args.Remove("-dn");
            args.AddRange(new[]{"-map","0:a?"});
            if(mkv)args.AddRange(new[]{"-map","0:s?","-map","0:t?","-map","0:d?"});
            args.AddRange(new[]{"-map_metadata","0","-map_chapters","0"});
            // Never copy remux's global -c copy over av1_nvenc.
            if(mkv)args.AddRange(new[]{"-c:a","copy","-c:s","copy","-c:t","copy","-c:d","copy"});
            else args.AddRange(new[]{"-c:a","aac","-b:a","256k","-movflags","+faststart"});
            args.AddRange(new[]{"-enc_time_base:v",Clock(info.AvgFrameRate),output});
            AudioOutputCore.Apply(args,state,info,mkv,false,false);
            return args;
        }
    }
}

namespace FilmGrainStudioPreview
{
    // Adds source-proven static metadata while the existing final mux reads IVF.
    // No additional video encode or media workfile; compressed frame OBUs and IVF clocks are untouched.
    internal static class NvencHdr10StreamCore
    {
        private static readonly string[] Keys={"max_cll","max_fall","primary_chromaticity_x[0]","primary_chromaticity_y[0]","primary_chromaticity_x[1]","primary_chromaticity_y[1]","primary_chromaticity_x[2]","primary_chromaticity_y[2]","white_point_chromaticity_x","white_point_chromaticity_y","luminance_max","luminance_min"};
        internal static bool HasSourceHdr10(MediaProbeInfo i)
        {return i.VideoCodec=="hevc"&&i.ColorPrimaries=="bt2020"&&i.ColorTransfer=="smpte2084"&&i.ColorSpace=="bt2020nc"&&(i.ColorRange=="tv"||i.ColorRange=="pc");}
        internal static bool Eligible(MediaProbeInfo i,IDictionary<string,string> s)
        {
            string gpu,fps;s.TryGetValue("FG_GPU_DECODE",out gpu);s.TryGetValue("FG_FPS_MODE",out fps);
            return i.VideoCodec=="hevc"&&i.PixelFormat=="yuv420p10le"&&i.ColorPrimaries=="bt2020"&&i.ColorTransfer=="smpte2084"&&i.ColorSpace=="bt2020nc"&&(i.ColorRange=="tv"||i.ColorRange=="pc")&&!i.IsInterlaced&&i.FieldOrder=="progressive"&&gpu!="1"&&(fps=="SOURCE"||fps=="KEEP");
        }
        internal static void Attach(BridgeExecutionRequest request,string input,MediaProbeInfo info,string codedVideo,bool sourceClock)
        {
            if(request.DirectStages==null||request.DirectStages.Count<2)throw new InvalidDataException("NVENC HDR stream requires existing IVF/mux stages");
            Dictionary<string,long> expected=null;int range=info.ColorRange=="pc"?1:0;
            bool sourceMp4=sourceClock&&request.OutputFiles!=null&&request.OutputFiles.Count==1&&string.Equals(Path.GetExtension(request.OutputFiles[0]),".mp4",StringComparison.OrdinalIgnoreCase);
            string tailDuration=null;
            SvtHdr10Core.Before(request.DirectStages[0],delegate {
                expected=SvtHdr10Core.ReadHevcAvailable(SvtHdr10Core.Trace(request.ToolPath,input),range);
                if(sourceMp4)tailDuration=ReadSourceTailDuration(Path.Combine(Path.GetDirectoryName(request.ToolPath),"ffprobe.exe"),input,info.Duration);
                return "[NVENC HDR10] 已读取实际源静态信息：MDCV="+(expected.ContainsKey("luminance_max")?"有":"无")+" CLLI="+(expected.ContainsKey("max_cll")?"有":"无")+"；最终封装写入源中存在的项，源中没有的不编造。硬解、画面处理和颗粒继续使用原执行路线，无新增整片中间文件。"+(sourceMp4?Environment.NewLine+"[NVENC HDR10 MP4] bounded real source final packet duration="+tailDuration+"s; no nominal-FPS substitution.":"");
            });
            var mux=request.DirectStages[request.DirectStages.Count-1];
            if(!mux.StageText.StartsWith("AV1 container remux",StringComparison.Ordinal))throw new InvalidDataException("NVENC HDR stream mux boundary changed");
            mux.StageText+=" / source-proven HDR OBU stream";
            mux.PipeProducerPath=typeof(Program).Assembly.Location;
            SvtHdr10Core.Before(mux,delegate {
                if(expected==null)throw new InvalidDataException("NVENC HDR source proof is unavailable");
                var args=HardwareDecodeCore.SplitArguments(mux.DirectArguments);int found=0;
                for(int n=0;n<args.Count-1;n++)if(args[n]=="-i"&&args[n+1]==codedVideo){args[n+1]="pipe:0";found++;}
                if(found!=1)throw new InvalidDataException("NVENC HDR mux input boundary changed");
                if(sourceMp4) {
                    if(string.IsNullOrEmpty(tailDuration))throw new InvalidDataException("AV1 MP4 source tail duration is unavailable");
                    if(args.Contains("-bsf:v")||args.Contains("-bsf:v:0"))throw new InvalidDataException("AV1 MP4 duration filter boundary changed");
                    // Stream copy: preserve every packet PTS/DTS; next PTS supplies interval durations.
                    // IVF has no packet duration. Only the final sample uses the actual source tail duration.
                    args.InsertRange(args.Count-1,new[]{"-bsf:v","setts=duration=if(gt(NEXT_PTS\\,PTS)\\,NEXT_PTS-PTS\\,"+tailDuration+"/TB)"});
                }
                mux.DirectArguments=SharedFrucCore.JoinArguments(args);
                var values=new List<string>();foreach(string key in Keys)values.Add(expected.ContainsKey(key)?expected[key].ToString(CultureInfo.InvariantCulture):"-");
                mux.PipeProducerArguments=SharedFrucCore.JoinArguments(new[]{"--av1-hdr-ivf-stream",codedVideo,string.Join(",",values.ToArray())});
                return "[NVENC HDR10] existing coded IVF -> bounded packet metadata writer -> existing stream-copy mux; original compressed frame/grain OBUs, IVF PTS and original audio mapping retained.";
            });
            var previous=request.PostRunValidation;
            request.PostRunValidation=delegate {
                string error=previous==null?null:previous();if(!string.IsNullOrEmpty(error))return error;
                VerifyAvailable(request.ToolPath,request.OutputFiles[0],expected,range);return null;
            };
            if(sourceMp4)request.ExecutionNotice=(request.ExecutionNotice??"")+Environment.NewLine+"[NVENC HDR10 MP4] preserve packet PTS/DTS; stream-copy sample durations use next PTS and a bounded real-source tail packet duration. Missing/unreliable tail proof stops before encoding; no nominal-FPS duration substitution.";
            request.ExecutionNotice=(request.ExecutionNotice??"")+Environment.NewLine+"[NVENC HDR10] 静态信息保留已接入：读取源值 -> 原编码/颗粒流程 -> 最终封装写入并核验实际 AV1 OBU。硬解、遮黑边、缩放、插帧及颗粒选择不再绕过此步骤；不改变帧率路线。";
        }
        private static void VerifyAvailable(string ffmpeg,string output,Dictionary<string,long> expected,int range)
        {
            if(expected==null)throw new InvalidDataException("HDR source metadata was not read");
            string trace=SvtHdr10Core.Trace(ffmpeg,output);
            foreach(var kv in expected)if(SvtHdr10Core.Field(trace,kv.Key)!=kv.Value)
                throw new InvalidDataException("AV1 HDR metadata mismatch: "+kv.Key);
            foreach(var kv in new Dictionary<string,long>{{"seq_profile",0},{"high_bitdepth",1},{"mono_chrome",0},{"color_primaries",9},{"transfer_characteristics",16},{"matrix_coefficients",9},{"color_range",range}})
                if(SvtHdr10Core.Field(trace,kv.Key)!=kv.Value)throw new InvalidDataException("AV1 HDR output signaling changed: "+kv.Key);
        }
        internal static string ReadSourceTailDuration(string ffprobe,string input,string durationText)
        {
            double duration;
            if(!double.TryParse(durationText,NumberStyles.Float,CultureInfo.InvariantCulture,out duration)||double.IsNaN(duration)||double.IsInfinity(duration)||duration<=0)
                throw new InvalidDataException("AV1 MP4 source duration unavailable for bounded tail proof");
            // Seek near EOF. Long GOPs / inaccurate duration / unreadable tails must fail closed.
            // No full-file fallback: 8 seconds wall time and 2 MiB complete report limit.
            string start=Math.Max(0,duration-8).ToString("R",CultureInfo.InvariantCulture);
            string text;var captured=new StringBuilder();bool overflow=false;object sync=new object();
            using(var process=new Process()) {
                var args=new[]{"-v","error","-select_streams","v:0","-read_intervals",start+"%","-show_packets","-show_entries","packet=pts_time,duration_time","-of","compact=p=0:nk=0",input};
                process.StartInfo=new ProcessStartInfo(ffprobe,SharedFrucCore.JoinArguments(args)) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                DataReceivedEventHandler capture=delegate(object sender,DataReceivedEventArgs e) {
                    if(e.Data==null)return;lock(sync){if(captured.Length+e.Data.Length+2>2*1024*1024)overflow=true;else captured.AppendLine(e.Data);}
                };
                process.OutputDataReceived+=capture;process.ErrorDataReceived+=capture;
                process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();
                if(!process.WaitForExit(8000)){try{process.Kill();process.WaitForExit();}catch{}throw new InvalidDataException("AV1 MP4 source tail proof timed out; encoding stopped");}
                process.WaitForExit();
                if(process.ExitCode!=0||overflow)throw new InvalidDataException("AV1 MP4 source tail proof failed or exceeded report limit; encoding stopped");
                text=captured.ToString();
            }
            return ParseSourceTailDuration(text);
        }
        internal static string ParseSourceTailDuration(string text)
        {
            double last=double.NegativeInfinity,tail=0;bool found=false,conflict=false;
            foreach(string line in text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)) {
                if(!line.StartsWith("pts_time=",StringComparison.Ordinal))throw new InvalidDataException("AV1 MP4 unexpected source tail report");
                double pts=double.NaN,dur=double.NaN;
                foreach(string part in line.Split('|')) {
                    if(part.StartsWith("pts_time=",StringComparison.Ordinal)){if(!double.TryParse(part.Substring(9),NumberStyles.Float,CultureInfo.InvariantCulture,out pts))pts=double.NaN;}
                    else if(part.StartsWith("duration_time=",StringComparison.Ordinal)){if(!double.TryParse(part.Substring(14),NumberStyles.Float,CultureInfo.InvariantCulture,out dur))dur=double.NaN;}
                    else throw new InvalidDataException("AV1 MP4 unexpected source tail field");
                }
                if(double.IsNaN(pts)||double.IsInfinity(pts))throw new InvalidDataException("AV1 MP4 invalid source tail PTS");
                if(!found||pts>last){last=pts;tail=dur;found=true;conflict=false;}
                else if(pts==last&&dur!=tail)conflict=true;
            }
            if(!found||conflict||double.IsNaN(tail)||double.IsInfinity(tail)||tail<=0)
                throw new InvalidDataException("AV1 MP4 real source final packet duration missing or conflicting; encoding stopped");
            return tail.ToString("R",CultureInfo.InvariantCulture);
        }
        internal static int Run(string[] args)
        {
            try {
                if(args.Length!=3)throw new ArgumentException("Expected IVF input and twelve source-proven metadata integers");
                string[] parts=args[2].Split(',');if(parts.Length!=Keys.Length)throw new InvalidDataException("HDR metadata field count");
                var values=new Dictionary<string,long>();
                for(int n=0;n<Keys.Length;n++){
                    if(parts[n]=="-")continue;
                    long v;if(!long.TryParse(parts[n],NumberStyles.None,CultureInfo.InvariantCulture,out v)||v<0||v>(n>=10?uint.MaxValue:65535L))throw new InvalidDataException("HDR metadata integer out of range");values[Keys[n]]=v;
                }
                int cllCount=0,masterCount=0;for(int n=0;n<Keys.Length;n++)if(values.ContainsKey(Keys[n])){if(n<2)cllCount++;else masterCount++;}
                if((cllCount!=0&&cllCount!=2)||(masterCount!=0&&masterCount!=10))throw new InvalidDataException("Incomplete HDR metadata group");
                using(var source=File.OpenRead(args[1]))using(var target=Console.OpenStandardOutput())Copy(source,target,values);
                return 0;
            }catch(Exception ex){Console.Error.WriteLine("[NVENC HDR stream] "+ex.Message);return 2;}
        }
        private static byte[] Read(Stream s,int n)
        {
            var b=new byte[n];int at=0;while(at<n){int count=s.Read(b,at,n-at);if(count==0)throw new EndOfStreamException("Truncated IVF/OBU");at+=count;}return b;
        }
        private static uint U32(byte[] b,int p){return (uint)b[p]|((uint)b[p+1]<<8)|((uint)b[p+2]<<16)|((uint)b[p+3]<<24);}
        private static void Put32(byte[] b,uint n){for(int k=0;k<4;k++)b[k]=(byte)(n>>(8*k));}
        private static void Big(Stream s,long n,int bytes){for(int k=bytes-1;k>=0;k--)s.WriteByte((byte)(n>>(8*k)));}
        private static byte[] Body(int type,Dictionary<string,long> v)
        {
            using(var b=new MemoryStream()){
                b.WriteByte((byte)type);
                if(type==1){Big(b,v[Keys[0]],2);Big(b,v[Keys[1]],2);}
                else{for(int n=2;n<10;n++)Big(b,v[Keys[n]],2);Big(b,v[Keys[10]],4);Big(b,v[Keys[11]],4);}
                b.WriteByte(128);return b.ToArray();
            }
        }
        private static ulong Leb(byte[] b,ref int at,int end)
        {
            ulong value=0;for(int n=0;n<8;n++){if(at>=end)throw new InvalidDataException("Truncated OBU size/type");byte x=b[at++];value|=(ulong)(x&127)<<(n*7);if((x&128)==0)return value;}throw new InvalidDataException("Oversized OBU size/type");
        }
        private static int Obu(byte[] b,int length,ref int at,out int payload,out int type)
        {
            if(at>=length)throw new InvalidDataException("Missing OBU header");byte h=b[at++];if((h&129)!=0||(h&2)==0)throw new InvalidDataException("Unsupported OBU header/size form");
            type=(h>>3)&15;if((h&4)!=0){if(at>=length||(b[at++]&7)!=0)throw new InvalidDataException("Invalid OBU extension");}
            ulong size=Leb(b,ref at,length);payload=at;if(size>(ulong)(length-at))throw new InvalidDataException("OBU exceeds IVF packet");at+=checked((int)size);return at;
        }
        private static bool Match(byte[] data,int at,int end,byte[] expected)
        {if(end-at!=expected.Length)return false;for(int k=0;k<expected.Length;k++)if(data[at+k]!=expected[k])return false;return true;}
        internal static void Copy(Stream source,Stream target,Dictionary<string,long> values)
        {
            byte[] header=Read(source,32);
            if(Encoding.ASCII.GetString(header,0,4)!="DKIF"||header[4]!=0||header[5]!=0||Encoding.ASCII.GetString(header,8,4)!="AV01")throw new InvalidDataException("Not AV1 IVF version 0");
            int headerSize=header[6]|header[7]<<8;if(headerSize<32||headerSize>4096)throw new InvalidDataException("Unsupported IVF header size");
            byte[] extra=Read(source,headerSize-32);target.Write(header,0,header.Length);target.Write(extra,0,extra.Length);
            byte[] cll=values.ContainsKey(Keys[0])?Body(1,values):null,mdcv=values.ContainsKey(Keys[2])?Body(2,values):null,data=new byte[65536];long packets=0,injected=0;
            for(;;){
                int first=source.ReadByte();if(first<0)break;
                byte[] ph=new byte[12];ph[0]=(byte)first;byte[] rest=Read(source,11);Buffer.BlockCopy(rest,0,ph,1,11);
                uint size=U32(ph,0);if(size==0||size>128*1024*1024)throw new InvalidDataException("IVF packet outside bounded 1..128MiB support");
                int length=(int)size;if(data.Length<length)data=new byte[Math.Max(length,Math.Min(128*1024*1024,data.Length*2))];
                int filled=0;while(filled<length){int got=source.Read(data,filled,length-filled);if(got==0)throw new EndOfStreamException("Truncated IVF packet");filled+=got;}
                int at=0,sequences=0,count=0;bool haveCll=false,haveMdcv=false,seenFrame=false;
                while(at<length){
                    int payload,type;int end=Obu(data,length,ref at,out payload,out type);if(++count>100000)throw new InvalidDataException("Too many OBUs in one IVF packet");
                    if(type==1){if(sequences++!=0)throw new InvalidDataException("Multiple sequence headers in one IVF packet");}
                    if(type==3||type==6)seenFrame=true;
                    if(type==5){int p=payload;ulong mt=Leb(data,ref p,end);
                        if(mt==1||mt==2){
                            byte[] expected=mt==1?cll:mdcv;
                            if(expected==null)continue;
                            if(seenFrame)throw new InvalidDataException("Existing HDR metadata follows a frame in the same packet");
                            if(!Match(data,p,end,expected.SubArrayForHdr()))throw new InvalidDataException("Existing AV1 HDR metadata conflicts with proven source");
                            if(mt==1){if(haveCll)throw new InvalidDataException("Duplicate CLLI OBU");haveCll=true;}
                            else{if(haveMdcv)throw new InvalidDataException("Duplicate MDCV OBU");haveMdcv=true;}
                        }
                    }
                }
                if(packets==0&&sequences!=1)throw new InvalidDataException("First IVF packet has no unique sequence header");
                int add=sequences*((haveCll||cll==null?0:cll.Length+2)+(haveMdcv||mdcv==null?0:mdcv.Length+2));Put32(ph,checked(size+(uint)add));target.Write(ph,0,12);
                at=0;while(at<length){int start=at,payload,type;int end=Obu(data,length,ref at,out payload,out type);target.Write(data,start,end-start);
                    if(type==1){if(!haveCll&&cll!=null){target.WriteByte(42);target.WriteByte((byte)cll.Length);target.Write(cll,0,cll.Length);injected++;}if(!haveMdcv&&mdcv!=null){target.WriteByte(42);target.WriteByte((byte)mdcv.Length);target.Write(mdcv,0,mdcv.Length);injected++;}}
                }
                packets++;
            }
            if(packets==0)throw new InvalidDataException("IVF has no packets");target.Flush();
            Console.Error.WriteLine("[NVENC HDR stream] packets="+packets+" injected_metadata_obus="+injected+"; no media workfile; compressed frame OBUs and IVF clocks unchanged");
        }
        private static byte[] SubArrayForHdr(this byte[] data){var value=new byte[data.Length-1];Buffer.BlockCopy(data,1,value,0,value.Length);return value;}
    }
}
