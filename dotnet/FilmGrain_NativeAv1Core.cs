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
        internal static List<string> BuildBakeArgs(string input,string output,string preset,long bitrate,bool nvenc,IDictionary<string,string> state,MediaProbeInfo info)
        {
            var args=new List<string>();
            Add(args,"-hide_banner","-stats","-progress","pipe:2","-stats_period","0.5","-y","-c:v","libdav1d","-filmgrain","1","-i",input,"-map","0:v:0","-map","0:a:0?","-map_metadata","0");
            if(nvenc){Add(args,"-vf","format=nv12");SharedBakeH264Core.AddNvenc(args);}
            else{Add(args,"-vf","format=yuv420p","-c:v","libx264","-profile:v","high","-pix_fmt","yuv420p","-preset",preset);SharedGrainCore.AddX264Tune(args,SharedGrainCore.GrainTune(state,"FG_X264_GRAIN_TUNE",true));}
            Add(args,"-b:v",bitrate.ToString(CultureInfo.InvariantCulture)+"k","-maxrate",(bitrate*3L).ToString(CultureInfo.InvariantCulture)+"k","-bufsize",(bitrate*6L).ToString(CultureInfo.InvariantCulture)+"k","-c:a","aac","-b:a","256k","-ac","2","-ar","48000","-movflags","+faststart",output);
            AudioOutputCore.Apply(args,state,info,false,true,true);
            return SharedHdrPreprocessCore.BuildBakeTimeline(args,false);
        }

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
            if (svp && !Eq(state, "FG_INTERPOLATION_ENGINE", "SVP") && !string.IsNullOrEmpty(Get(state, "FG_INTERPOLATION_ENGINE")))
            { reason = "unsupported AV1 interpolation engine"; return false; }
            if (svp && info.IsInterlaced) { reason = "interlaced OpenSVPFlow follows Legacy field-rate handling"; return false; }
            if (fruc && info.IsInterlaced) { reason = "AV1 FRUC requires progressive input; interlaced FRUC is not supported"; return false; }
            bool hdrToSdr = info.IsHdr && !Eq(state, "FG_HDR_POLICY", "PRESERVE") &&
                (Eq(state, "FG_HDR_POLICY", "SDR") || fgsimGrain ||
                 (Eq(state, "FG_HDR_POLICY", "AUTO") &&
                  (Get(state, "FG_COLOR_ENABLED") == "1" || !string.IsNullOrWhiteSpace(Get(state, "FG_LUT_PATH")) || svp)));
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
            string colorFilter, colorSuffix;
            if (!TryBuildColorFilter(state, out colorFilter, out colorSuffix, out reason)) return false;
            string lutNamingSuffix = hdrPreserve ? "" : BuildLutNamingSuffix(state);
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
            if (interpolate) fpsSuffix = (fruc ? "_FRUC" : "_SVP") + frucRate.Label;
            string output = Path.Combine(outputDirectory,
                Path.GetFileNameWithoutExtension(input) + "_AV1_" + speed + "_" + NvencRateControlCore.FileToken(state,bitrate) + fpsSuffix + deinterlaceSuffix + frame.FileSuffix + (grainEnabled ? "_GS_" + grainTag : "") + colorSuffix + lutNamingSuffix + (hdrToSdr ? "_SDR_" + tonemapLabel : "") + subtitleSuffix + extension);
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
            if (svp && !SharedSvpCore.TryDependencies(appRoot, out reason)) return false;
            string frucFfmpeg = Get(state, "FG_FRUC_USE_MAIN_FFMPEG") == "0" ? Get(state, "FG_FRUC_FFMPEG_PATH") : ffmpeg;
            string frucPerf = Get(state, "FG_FRUC_PERF").ToLowerInvariant();
            string frucGrid = Get(state, "FG_FRUC_GRID").ToLowerInvariant();
            if (fruc && !SharedFrucCore.TryPreflight(frucFfmpeg, frucPerf, frucGrid, out reason)) return false;
            if (fruc && hdrPreserve && !SharedFrucCore.TryPreflight10(frucFfmpeg, frucPerf, frucGrid, out reason)) return false;
            if (interpolate)
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
            if (!hdrPreserve && !TryPrepareLut(ffmpeg, state, out lutCompatPath, out lutCompatName, out lutOpacity, out reason)) return false;
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
            string encodeInput = interpolate ? frucWorkfile : frucInput;

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

            string pixelFull = grainEnabled && !metadataGrain ? filterGraph : null;
            string pixelClean = null;
            if (pixelFull != null)
            {
                pixelClean = BuildFilterGraph(info,frame,state,deinterlaceFilter,colorFilter,lutCompatName,lutOpacity,subtitleFilter,
                    proceduralGrain || hdrPreserve,fgsimGrain && !hdrPreserve,false,hdrPreserve).Replace("[baseout]",",format=p010le[vout]");
                if(hdrPreserve)pixelClean=pixelClean.Replace("[vout]",BuildHdrFrameFilter(info)+"[vout]");
                filterGraph=PixelGrainStrengthCore.Wrap(filterGraph,pixelClean,pixelPercent);
            }
            List<string> encode = new List<string>();
            Add(encode, "-hide_banner", "-stats", "-progress", "pipe:2", "-stats_period", "0.5", "-y");
            if (fgsimGrain || (bwdifVulkan && !fieldWorkfile && !separateDeint)) Add(encode, "-init_hw_device", "vulkan=vk:0", "-filter_hw_device", "vk");
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
            if(svp)SharedInterpolationEncodeCore.NormalizeRenderedCadence(encode,fpsText);
            if (interpolate) Add(encode, "-enc_time_base:v",fpsText.Contains("/")?fpsText.Split('/')[1]+"/"+fpsText.Split('/')[0]:"1/"+fpsText,"-fps_mode:v", "passthrough");
            else Add(encode, "-r", fpsText, "-fps_mode:v", "cfr");
            if (hdrPreserve) Add(encode, "-color_range", HdrRange(info), "-color_primaries", HdrPrimaries(info), "-color_trc", info.ColorTransfer, "-colorspace", HdrSpace(info));
            else if (hdrToSdr) Add(encode, "-color_range", "tv", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709");
            if (!grainEnabled && !interpolate) SharedOrdinaryTimelineCore.NormalizeVideoOnly(encode);
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
            string strengthIvf = grainIvf + ".strength.ivf";
            string remuxVideo = metadataGrain ? (grainPercent == 100 ? grainIvf : strengthIvf) : cleanIvf;
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
            if (interpolate) request.TemporaryFiles.Add(frucWorkfile);
            if (hdrPreserve) request.PostRunValidation = delegate { return VerifyHdrOutput(ffprobe, output, info); };
            if (!string.IsNullOrEmpty(lutCompatPath)) request.TemporaryFiles.Add(lutCompatPath);
            if (!string.IsNullOrEmpty(subtitleAss)) request.TemporaryFiles.Add(subtitleAss);
            if (!string.IsNullOrEmpty(subtitleFilterPath)&&subtitleFilterPath!=subtitleAss) request.TemporaryFiles.Add(subtitleFilterPath);
            request.ProgressDurationSeconds = ParseDuration(info.Duration);
            double duration = request.ProgressDurationSeconds;
            int preStages = (needsHdrWorkfile || separateDeint ? 1 : 0) + (interpolate ? 1 : 0);
            int encodeStages = metadataGrain ? 3 : 2;
            
            int totalStages = preStages + encodeStages;
            List<BridgeExecutionStage> directStages = new List<BridgeExecutionStage>();
            if (needsHdrWorkfile || separateDeint) directStages.Add(Stage(ffmpeg, tempRoot, hdrToSdr ? SharedHdrPreprocessCore.BuildToSdr(input,hdrWorkfile,info,tonemap,fieldWorkfile || hdrOffSdr,fieldWorkfile?fieldFilter:"") : SharedHdrPreprocessCore.BuildDeinterlaced(input,hdrWorkfile,info,fieldFilter), duration, 1, totalStages, hdrToSdr ? "HDR 转 SDR / " + tonemapLabel : "Shared field deinterlace / separate Vulkan device"));
            if(needsHdrWorkfile)SharedHdrPreprocessCore.AttachAutomatic(directStages[directStages.Count-1],tonemap);
            if (fruc)
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
                directStages.Add(Stage(grav1synth, tempRoot, apply, duration, preStages + 2, totalStages, "AV1 Film Grain metadata"));
                if (grainPercent != 100) directStages.AddRange(Av1GrainStrengthCore.Stages(grav1synth,tempRoot,delegate { return grainIvf; },strengthIvf,grainPercent,request.TemporaryFiles));
                directStages.Add(Stage(ffmpeg, tempRoot, remux, duration, preStages + 3, totalStages, "AV1 container remux"));
            }
            else
            {
                string grainStage = !grainEnabled ? "AV1 Main10 encode / 颗粒：关闭" : fgsimGrain ? "AV1 FGSIM pixel-grain encode" : "AV1 Digital Grain pixel-grain encode";
                directStages.Add(Stage(ffmpeg, encodeWorkingDirectory, encode, duration, preStages + 1, totalStages, grainStage));
                directStages.Add(Stage(ffmpeg, tempRoot, remux, duration, preStages + 2, totalStages, "AV1 container remux"));
            }
            
            if(mergedHdr) {
                SharedHdrPreprocessCore.AttachMergedEncode(directStages[preStages],ffprobe,input,info,tonemap,caps.GpuName,true,Eq(state,"FG_FPS_MODE","SOURCE"));
                directStages[preStages].StageText+=" / HDR merged";
            }
            if(fieldWorkfile || separateDeint || hdrOffSdr)SharedHdrPreprocessCore.AttachFieldValidation(directStages[preStages],ffprobe,hdrWorkfile,info,hdrToSdr,!hdrOffSdr);
            if (mergedHdr || interpolate || fieldWorkfile || separateDeint || hdrOffSdr || !grainEnabled)
                foreach (BridgeExecutionStage stage in directStages)
                    if (stage.StageText.StartsWith("AV1 container remux", StringComparison.Ordinal))
                        SharedFrucCore.AttachAv1RemuxTimeline(stage, ffprobe, remuxVideo, input, remux, !grainEnabled && !interpolate ? "ordinary no-grain" : fruc ? "FRUC" : "OpenSVPFlow");
            if (grainPercent != 100) Av1GrainStrengthCore.NumberStages(directStages);
            request.DirectStages = directStages;
            if(info.IsHdr && info.IsInterlaced && !deinterlace)request.ExecutionNotice="[Native .NET] 反交错 OFF：沿用不反交错的逐帧处理，不启用 bob/光流或双倍场率；不保证隔行编码标记。 / Deinterlace OFF: frame processing without bob/interpolation; no interlaced-encoding flags are added.";
            if (fgsimGrain) FgsimTextureCore.AttachVerifiedDevice(request, fgsimHook, hdrPreserve);
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
        internal static List<string> BuildArgs(string input,string output,long bitrate,bool bFrames)
        {
            var args=new List<string>{"-hide_banner","-stats","-progress","pipe:2","-stats_period","0.5","-n",
                "-c:v","libdav1d","-filmgrain","1","-i",input,"-map","0:v:0","-map","0:a:0?","-map_metadata","0",
                "-vf","format=p010le"};
            AddEncoder(args,bFrames);
            args.AddRange(new[]{"-b:v",bitrate.ToString(CultureInfo.InvariantCulture)+"k","-maxrate",(bitrate*3).ToString(CultureInfo.InvariantCulture)+"k",
                "-bufsize",(bitrate*6).ToString(CultureInfo.InvariantCulture)+"k","-c:a","aac","-b:a","256k","-ac","2","-ar","48000","-movflags","+faststart",output});
            return SharedHdrPreprocessCore.BuildBakeTimeline(args,false);
        }
    }

    internal static class NativeAv1BakeCore
    {
        internal static string OutputPath(string input,IDictionary<string,string> state,long bitrate,bool nvenc)
        {
            string mode,custom;state.TryGetValue("FG_OUTPUT_MODE",out mode);state.TryGetValue("FG_OUTPUT_CUSTOM_DIR",out custom);
            string dir=string.Equals(mode,"CUSTOM",StringComparison.OrdinalIgnoreCase)?custom:Path.GetDirectoryName(input);
            if(string.IsNullOrWhiteSpace(dir))throw new InvalidOperationException("烘焙输出目录未配置。 / Output directory is not configured.");
            string selected;state.TryGetValue("FG_BAKE_ENCODER",out selected);
            return Path.Combine(dir,Path.GetFileNameWithoutExtension(input)+(string.Equals(selected,"HEVC_NVENC",StringComparison.OrdinalIgnoreCase)?"_BAKED_HEVC_MAIN10_NVENC":"_BAKED_H264_"+(nvenc?"NVENC":"X264"))+"_"+bitrate.ToString(CultureInfo.InvariantCulture)+"k.mp4");
        }
        internal static bool TryPrepare(string appRoot,string input,MediaProbeInfo info,IDictionary<string,string> state,
            out BridgePreparedExecution prepared,out string reason)
        {
            prepared=null;reason="";
            try
            {
                if(info==null || !info.HasVideo || !string.Equals(info.VideoCodec,"av1",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("烘焙需要 AV1 输入。 / Baking requires AV1 input.");
                if(info.IsHdr || string.Equals(info.ColorPrimaries,"bt2020",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("本阶段只支持普通 SDR；HDR/BT.2020 烘焙放到最后阶段。 / SDR only in this stage.");
                if(info.IsInterlaced || info.Width<=0 || info.Height<=0 || info.Width%2!=0 || info.Height%2!=0)
                    throw new InvalidOperationException("烘焙需要偶数尺寸逐行视频。 / Progressive even dimensions required.");
                if(info.PixelFormat!="yuv420p" && info.PixelFormat!="yuv420p10le")
                    throw new InvalidOperationException("本阶段仅支持 4:2:0 8/10-bit SDR。 / 4:2:0 SDR 8/10-bit required.");
                string encoder;state.TryGetValue("FG_BAKE_ENCODER",out encoder);
                bool hevc=string.Equals(encoder,"HEVC_NVENC",StringComparison.OrdinalIgnoreCase);
                bool nvenc=string.Equals(encoder,"NVENC",StringComparison.OrdinalIgnoreCase);
                if(!hevc && !nvenc && !string.Equals(encoder,"X264",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("请选择 x264、H.264 NVENC 或 HEVC NVENC。 / Unknown bake encoder.");
                long bitrate;string value;state.TryGetValue("FG_BAKE_BITRATE",out value);
                if(!long.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out bitrate)||bitrate<100||bitrate>500000)
                    throw new InvalidOperationException("烘焙码率必须为 100–500000 kbps。 / Invalid bake bitrate.");
                string ffmpeg=Path.Combine(NativeAv1Core.ReadConfigValue(appRoot,"FFMPEG_DIR"),"ffmpeg.exe");
                string ffprobe=Path.Combine(Path.GetDirectoryName(ffmpeg),"ffprobe.exe");
                if(!File.Exists(ffmpeg)||!File.Exists(ffprobe))throw new FileNotFoundException("需要 FFmpeg 和 FFprobe。 / FFmpeg and FFprobe required.");
                string help,error;
                if(!SharedFrucCore.Run(ffmpeg,"-hide_banner -h decoder=libdav1d",5000,out help,out error)||help.IndexOf("filmgrain",StringComparison.OrdinalIgnoreCase)<0)
                    throw new InvalidOperationException("当前 libdav1d 未提供已验证的颗粒合成选项。 / Verified libdav1d filmgrain option unavailable.");
                if(nvenc && !SharedBakeH264Core.TryNvenc(ffmpeg,out error))throw new InvalidOperationException(error);
                bool hevcBFrames=false;string hevcNotice="";
                if(hevc && !SharedBakeHevcCore.TryPreflight(ffmpeg,out hevcBFrames,out hevcNotice,out error))throw new InvalidOperationException(error);
                string preset=NativeAv1Core.ReadConfigValue(appRoot,"X264_PRESET").ToLowerInvariant();
                if(preset!="medium"&&preset!="slow")preset="faster";
                string output=Path.GetFullPath(OutputPath(input,state,bitrate,nvenc));
                if(File.Exists(output))throw new IOException("烘焙输出已存在，不覆盖："+output);
                string dir=Path.GetDirectoryName(output);Directory.CreateDirectory(dir);
                // Encode to our own unique file; final rename refuses collisions. Cleanup never owns pre-existing output.
                string staging=Path.Combine(dir,"__FGS_BAKE_"+Guid.NewGuid().ToString("N")+".mp4");
                var args=hevc?SharedBakeHevcCore.BuildArgs(input,staging,bitrate,hevcBFrames):NativeAv1Core.BuildBakeArgs(input,staging,preset,bitrate,nvenc,state,info);
                if(hevc)AudioOutputCore.Apply(args,state,info,false,true,true);
                int overwrite=args.IndexOf("-y");if(overwrite>=0)args[overwrite]="-n";
                Stamp(args,"-color_range",info.ColorRange);Stamp(args,"-colorspace",info.ColorSpace);
                Stamp(args,"-color_trc",info.ColorTransfer);Stamp(args,"-color_primaries",info.ColorPrimaries);
                // H.264 may omit video_signal_type_present_flag for default limited range
                // when no colour description is supplied. Make the known source range
                // explicit in SPS VUI, without changing pixels or adding another encode.
                if(!hevc && (info.ColorRange=="tv" || info.ColorRange=="pc"))
                    args.InsertRange(args.Count-1,new[]{"-bsf:v","h264_metadata=video_full_range_flag="+(info.ColorRange=="pc"?"1":"0")});
                double duration;double.TryParse(info.Duration,NumberStyles.Float,CultureInfo.InvariantCulture,out duration);
                var req=new BridgeExecutionRequest{ToolPath=ffmpeg,WorkingDirectory=dir,DirectProcess=true,
                    InputFiles=new List<string>{input},OutputFiles=new List<string>(),TemporaryFiles=new List<string>{staging},
                    Environment=new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase),ProgressDurationSeconds=duration,LogStageTimings=true,
                    ExecutionNotice="[AV1 Bake] SDR / libdav1d filmgrain=1 -> "+(hevc?"HEVC NVENC Main10 P7":nvenc?"H.264 NVENC P7":"x264 "+preset)+" / AAC. "+hevcNotice+"\n仅合成源内已有颗粒，不新增颗粒、不运行插帧或调色；无 metadata 时正常转码。 / Existing grain only; no extra grain or transformations.\n[AV1 Bake] output="+output};
                var original=new FileInfo(input);long length=original.Length;DateTime modified=original.LastWriteTimeUtc;
                var stage=new BridgeExecutionStage{ToolPath=ffmpeg,WorkingDirectory=dir,OriginalArguments=args,
                    DirectArguments=SharedFrucCore.JoinArguments(args),ProgressDurationSeconds=duration,StageCurrent=1,StageTotal=1,
                    StageText="AV1 SDR grain bake / "+(hevc?"HEVC NVENC Main10":nvenc?"H.264 NVENC":"x264")};
                stage.BeforeRunPreparation=delegate {
                    var current=new FileInfo(input);
                    if(!current.Exists||current.Length!=length||current.LastWriteTimeUtc!=modified)throw new IOException("烘焙输入已改变。 / Input changed.");
                    if(File.Exists(output)||File.Exists(staging))throw new IOException("烘焙输出发生冲突，不覆盖。 / Output collision.");
                    return "[AV1 Bake] software grain synthesis enabled; independent SDR bake";
                };
                req.DirectStages=new List<BridgeExecutionStage>{stage};
                req.PostRunValidation=delegate {
                    string json,probeError;
                    if(!SharedFrucCore.Run(ffprobe,SharedFrucCore.JoinArguments(new[]{"-v","error","-show_streams","-show_format","-of","json",staging}),15000,out json,out probeError))return "Bake output probe failed: "+probeError;
                    var actual=MediaProbeInfo.Parse(staging,json);
                    if(!actual.HasVideo||actual.VideoCodec!=(hevc?"hevc":"h264")||actual.PixelFormat!=(hevc?"yuv420p10le":"yuv420p")||(hevc && actual.VideoProfile!="Main 10")||actual.Width!=info.Width||actual.Height!=info.Height||actual.IsHdr||
                       (info.HasAudio && (!actual.HasAudio||actual.AudioCodec!="aac")))return "Bake output format validation failed.";
                    if(!EqualKnown(info.ColorRange,actual.ColorRange)||!EqualKnown(info.ColorSpace,actual.ColorSpace)||
                       !EqualKnown(info.ColorTransfer,actual.ColorTransfer)||!EqualKnown(info.ColorPrimaries,actual.ColorPrimaries))return "Bake output color signaling changed: source {range="+info.ColorRange+", matrix="+info.ColorSpace+", transfer="+info.ColorTransfer+", primaries="+info.ColorPrimaries+"}; output {range="+actual.ColorRange+", matrix="+actual.ColorSpace+", transfer="+actual.ColorTransfer+", primaries="+actual.ColorPrimaries+"}.";
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
