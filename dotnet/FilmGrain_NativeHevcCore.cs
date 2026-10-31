using System;

using System.Collections.Generic;

using System.Diagnostics;

using System.Globalization;

using System.IO;
using System.Text;

namespace FilmGrainStudioPreview

{

    internal static class NativeHevcCore

    {

        internal static bool TryPrepare(string appRoot, string input, MediaProbeInfo info, IDictionary<string,string> state, HardwareCapabilitySnapshot caps, out BridgePreparedExecution prepared, out string reason)

        {

            prepared = null;

            reason = "";

            if (state == null || info == null) { reason = "missing state/media probe"; return false; }

            if (!Eq(state, "FG_MODE", "HEVC") && !Eq(state,"FG_MODE","H264")) { reason = "codec is not HEVC"; return false; }
            IDictionary<string,string> normalized; string bypassNotice;
            if (!SharedInterpolationBypassCore.TryResolve(info,state,out normalized,out bypassNotice,out reason)) return false;
            if (normalized != null)
            {
                bool success = TryPrepare(appRoot,input,info,normalized,caps,out prepared,out reason);
                if (success) SharedInterpolationBypassCore.Attach(prepared,bypassNotice);
                return success;
            }

            if (caps == null || !caps.Ready || !(Eq(state,"FG_MODE","H264")?caps.H264Available:caps.HevcAvailable)) { reason = "HEVC hardware capability cache is unavailable"; return false; }

            if (!SharedGrainCore.IsEnabled(state)) return TryPrepareEngine(appRoot, input, info, state, caps, false, out prepared, out reason);
            if (SharedGrainCore.Engine(state) == "PROCEDURAL") return TryPrepareEngine(appRoot, input, info, state, caps, false, out prepared, out reason);

            if (SharedGrainCore.Engine(state) == "NATIVE") return TryPrepareEngine(appRoot, input, info, state, caps, true, out prepared, out reason);

            if (SharedGrainCore.Engine(state) == "FGSIM") return TryPrepareEngine(appRoot, input, info, state, caps, false, out prepared, out reason);

            reason = "unsupported HEVC grain engine";

            return false;

        }

        private static bool TryPrepareEngine(string appRoot, string input, MediaProbeInfo info, IDictionary<string,string> state, HardwareCapabilitySnapshot caps, bool plateMode, out BridgePreparedExecution prepared, out string reason)

        {

            prepared = null;

            reason = "";

            bool grainEnabled = SharedGrainCore.IsEnabled(state);
            if (!SharedGrainCore.ValidateHdr(info,state,out reason)) return false;
            bool h264 = Eq(state,"FG_MODE","H264");
            bool fgsimMode = SharedGrainCore.Engine(state) == "FGSIM";

            bool interpolate = Get(state, "FG_SVP_INTERPOLATE") == "1";

            bool fruc = interpolate && Eq(state, "FG_INTERPOLATION_ENGINE", "FRUC");

            bool svp = interpolate && !fruc;

            if (interpolate && !fruc && !Eq(state, "FG_INTERPOLATION_ENGINE", "SVP") && !string.IsNullOrEmpty(Get(state, "FG_INTERPOLATION_ENGINE")))

            { reason = "unsupported interpolation engine"; return false; }

            string speed = Get(state, "FG_SPEED").ToUpperInvariant();

            if (speed != "FAST" && speed != "STANDARD") { reason = "unsupported HEVC speed"; return false; }

            bool hdrToSdr = info.IsHdr && !Eq(state, "FG_HDR_POLICY", "PRESERVE") && (Eq(state, "FG_HDR_POLICY", "SDR") ||

                fgsimMode || (Eq(state, "FG_HDR_POLICY", "AUTO") &&

                (Get(state, "FG_COLOR_ENABLED") == "1" || !string.IsNullOrWhiteSpace(Get(state, "FG_LUT_PATH")) || svp)));

            if(h264&&info.IsHdr&&!Eq(state,"FG_HDR_POLICY","PRESERVE"))hdrToSdr=true;
            bool hdrPreserve = info.IsHdr && !hdrToSdr;
            if(h264&&hdrPreserve){reason="H.264 NVENC 本线输出 8bit；HDR 源请选 HDR→SDR，不能保留 HDR10。 / H.264 8-bit output requires HDR to SDR.";return false;}

            string tonemap = Get(state, "FG_TONEMAP_ALGO").ToLowerInvariant();

            if (tonemap != "mobius" && tonemap != "reinhard" && tonemap != "gamma" && tonemap != "linear" && tonemap != "clip") tonemap = "hable";

            string tonemapLabel = char.ToUpperInvariant(tonemap[0]) + tonemap.Substring(1);

            bool mkv = Eq(state, "FG_CONTAINER", "MKV");

            if (!mkv && !Eq(state, "FG_CONTAINER", "MP4")) { reason = "unsupported HEVC container"; return false; }

            bool autoFps = Eq(state, "FG_FPS_MODE", "AUTO");

            if (!autoFps && !Eq(state, "FG_FPS_MODE", "SOURCE") && !Eq(state, "FG_FPS_MODE", "KEEP")) { reason = "unsupported HEVC FPS mode"; return false; }

            if (fruc && info.IsInterlaced) { reason = "interlaced FRUC is not supported"; return false; }

            if (svp && hdrPreserve) { reason = "OpenSVPFlow HDR preservation is not supported; choose FRUC for validated P010 interpolation."; return false; }

            bool mpegTsSvp = svp && !hdrToSdr && IsMpegTsInput(input);
            // All TS SVP routes keep the original audio lead on the shared common clock.
            bool preserveSvpTransportTimeline = mpegTsSvp;

            string mpegTsVideoOffset = "";



            string aqSetting = Get(state, "FG_HEVC_SPATIAL_AQ");

            if (aqSetting != "0" && aqSetting != "4" && aqSetting != "8" && aqSetting != "10" && aqSetting != "12" && aqSetting != "15") { reason = "unsupported spatial AQ strength"; return false; }

            // Match Legacy Bridge and Native x264: Off applies no bob filter or interlaced-encoding flags.

            bool deinterlace = info.IsInterlaced && Eq(state, "FG_DEINTERLACE", "AUTO");

            // OFF follows Legacy: no field filter or bob; it does not enable interlaced encoding.
            if(info.IsInterlaced && !deinterlace && !Eq(state,"FG_DEINTERLACE","OFF")) { reason="unsupported deinterlace policy"; return false; }
            bool hdrOffSdr=hdrToSdr && info.IsInterlaced && !deinterlace;

            bool bwdifVulkan = deinterlace && Eq(state, "FG_DEINT_METHOD", "BWDIF_VULKAN");

            bool bwdifCuda = deinterlace && Eq(state, "FG_DEINT_METHOD", "BWDIF_CUDA");

            if (deinterlace && (!Eq(state, "FG_DEINT_METHOD", "W3FDIF") && !bwdifVulkan && !bwdifCuda))

            { reason = "unsupported HEVC deinterlace method"; return false; }

            // Vulkan deinterlace with FGSIM / Grain Plate uses a separate workfile stage below.
            // CUDA avoids that stage unless HDR-to-SDR preprocessing already requires one.

            if (info.Width <= 0 || info.Height <= 0) { reason = "invalid dimensions"; return false; }

            long bitrate = 0;

            if (!NvencRateControlCore.IsCq(state) && (!long.TryParse(Get(state, "FG_BITRATE"), NumberStyles.Integer, CultureInfo.InvariantCulture, out bitrate) || bitrate <= 0)) { reason = "invalid bitrate"; return false; }

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

            string deinterlaceFilter = deinterlace ? SharedHdrPreprocessCore.DeinterlaceFilter(Get(state,"FG_DEINT_METHOD"),info) : "";

            string fieldFilter=deinterlaceFilter;

            bool fieldWorkfile=deinterlace && hdrToSdr;

            bool separateDeint=deinterlace && !hdrToSdr && bwdifVulkan && (plateMode || fgsimMode);

            if(fieldWorkfile || separateDeint || hdrOffSdr)deinterlaceFilter="";

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

            NvencRateControlCore.RequireSupport(ffmpeg,state);
            if (!File.Exists(ffmpeg)) { reason = "ffmpeg.exe not found: " + ffmpeg; return false; }

            string vspipe = Path.Combine(appRoot, "_OpenSVPFlow", ".venv", "Scripts", "vspipe.exe");

            string vpy = Path.Combine(appRoot, "_OpenSVPFlow", "FilmGrain_OpenSVPFlow.vpy");

            string svpPlugins = Path.Combine(appRoot, "_OpenSVPFlow", "Plugins");

            string fgsimHook = "", fgsimTag = "";

            if (fgsimMode)

            {

                string fgsimPreset = Get(state, "FG_FGSIM_PRESET").ToUpperInvariant();

                if (!GrainControlCore.IsFgsimPreset(fgsimPreset)) { reason = "unsupported FGSIM preset"; return false; }

                fgsimTag = GrainControlCore.FgsimTag(fgsimPreset);

                fgsimHook = Path.Combine(appRoot, "Utils", "_FilmGrainSimplified", "Generated", "FilmGrainSimplified_" + fgsimPreset + ".hook");

            }

            CinematicFramePlan frame;

            if (!CinematicFrameCore.TryBuild(info, state, out frame, out reason)) return false;

            bool highMotion = Get(state, "FG_HIGH_MOTION") == "1";

            if (!NvencRateControlCore.IsCq(state) && Eq(state, "FG_BITRATE_MODE", "AUTO"))

            {

                double rateFps = ParseFps(fps);

                bitrate = BitrateRecommendationCore.GetRecommendedBitrate(1, frame.ActiveWidth, frame.ActiveHeight, rateFps, highMotion);

            }

            string colorFilter, colorSuffix;

            if (!TryBuildColorFilter(state, out colorFilter, out colorSuffix, out reason)) return false;
            string preprocessing;
            if(!PreprocessingCore.TryBuild(state,out preprocessing,out reason))return false;
            if(preprocessing.Length>0 && !PreprocessingCore.TryCheckTool(ffmpeg,out reason))return false;
            colorFilter=preprocessing+colorFilter;
            if(preprocessing.Length>0)colorSuffix="_BIL"+colorSuffix;

            string lutNamingSuffix = hdrPreserve ? "" : BuildLutNamingSuffix(state);

            string preset = speed == "STANDARD" ? "p7" : "p5";

            string speedSuffix = speed == "STANDARD" ? "STD" : "FAST";


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

                grainScale = SharedGrainCore.NeedsPlateScaling(ffprobe, plate, info.Width, info.Height);

                int strengthSel;

                if (!int.TryParse(Get(state, "FG_HEVC_STRENGTH_SEL"), NumberStyles.Integer, CultureInfo.InvariantCulture, out strengthSel)) strengthSel = 3;
                plateStrengthPercent = GrainControlCore.PlatePercent(state);

                grainOpacity = (GrainControlCore.PlatePercent(state)/100.0).ToString("0.00",CultureInfo.InvariantCulture);

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

            int pixelPercent = grainEnabled ? PixelGrainStrengthCore.Read(state) : 100;
            string grainFileName = !grainEnabled ? "" : plateMode ? "_FG_" + grainTag + "_STR" + plateStrengthPercent.ToString(CultureInfo.InvariantCulture) : fgsimMode ? "_FG_FGSIM_" + fgsimTag : "_FG_DG" + procStrength.ToString(CultureInfo.InvariantCulture);

            if (grainEnabled && pixelPercent != 100) grainFileName += "_GS" + pixelPercent.ToString(CultureInfo.InvariantCulture);
            string output = Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + (h264?"_H264_NVENC_":"_HEVC_") + speedSuffix + "_" + NvencRateControlCore.FileToken(state,bitrate) + fpsSuffix + deinterlaceSuffix + frame.FileSuffix + grainFileName + colorSuffix + lutNamingSuffix + (hdrToSdr ? "_SDR_" + tonemapLabel : "") + subSuffix + HardwareDecodeCore.FileSuffix(state) + (mkv ? ".mkv" : ".mp4"));

            if (File.Exists(output)) { reason = "native output already exists: " + output; return false; }

                            if (plateMode && !File.Exists(Get(state,"FG_HEVC_GRAIN_PATH"))) { reason="selected Grain Plate is missing";return false; }
                if (fruc && !SharedFrucCore.TryPreflight(ffmpeg,Get(state,"FG_FRUC_PERF").ToLowerInvariant(),Get(state,"FG_FRUC_GRID").ToLowerInvariant(),out reason))return false;
                if(fruc && hdrPreserve && !SharedFrucCore.TryPreflight10(ffmpeg,Get(state,"FG_FRUC_PERF").ToLowerInvariant(),Get(state,"FG_FRUC_GRID").ToLowerInvariant(),out reason))return false;
                if (svp && (!File.Exists(vspipe)||!File.Exists(vpy)||!File.Exists(Path.Combine(svpPlugins,"svpflow1_vs.dll"))||!File.Exists(Path.Combine(svpPlugins,"svpflow2_vs.dll")))) { reason="OpenSVPFlow dependencies are missing";return false; }
                if (mpegTsSvp && !preserveSvpTransportTimeline && !TryGetMpegTsVideoOffset(ffprobe,input,out mpegTsVideoOffset,out reason))return false;
                if(fgsimMode) {
                    if(!TryPrepareFgsimHook(appRoot,ffmpeg,fgsimHook,Get(state,"FG_FGSIM_PRESET").ToUpperInvariant(),out reason))return false;
                }

            string lutCompatPath = "", lutCompatName = "", lutOpacity = "";

            if (!TryPrepareLut(ffmpeg, state, hdrPreserve, out lutCompatPath, out lutCompatName, out lutOpacity, out reason)) return false;

            string subtitleAss = "";

            if (!NativeSubtitlePrepareCore.TryPrepare(ffmpeg, ffprobe, input, state, frame.ActiveWidth, frame.ActiveHeight, out subtitleAss, out reason))

            {

                TryDelete(lutCompatPath);

                return false;

            }

            string workingDirectory=string.IsNullOrEmpty(lutCompatPath)?Path.GetDirectoryName(input):Path.GetDirectoryName(lutCompatPath);
            string subtitleFilterPath;
            if(!NativeSubtitlePrepareCore.TryStageForFilter(subtitleAss,workingDirectory,out subtitleFilterPath,out reason))
            {TryDelete(lutCompatPath);TryDelete(subtitleAss);return false;}
            List<string> temporaryFiles = new List<string>();

            if (!string.IsNullOrEmpty(lutCompatPath)) temporaryFiles.Add(lutCompatPath);

            if (!string.IsNullOrEmpty(subtitleAss)) temporaryFiles.Add(subtitleAss);
            if(!string.IsNullOrEmpty(subtitleFilterPath)&&subtitleFilterPath!=subtitleAss)temporaryFiles.Add(subtitleFilterPath);

            try

            {

                string subtitleFilter=NativeSubtitlePrepareCore.BurnInFilter(subtitleFilterPath);

                string filter;

                if (!grainEnabled)
                {
                    filter = SharedGrainCore.BuildWithoutGrain(deinterlaceFilter + fpsFilter + colorFilter,
                        EscapeFilterPath(lutCompatName), lutOpacity,
                        frame.Filter + subtitleFilter + (hdrPreserve ? BuildHdrFrameFilter(info) : "") + ",format=" + "p010le");
                }
                else if (plateMode)

                {

                    string grain = "[1:v:0]fps=" + fps + ",format=p010le,setpts=PTS-STARTPTS,hwupload" + (grainScale ? ",scale_vulkan=w=" + info.Width.ToString(CultureInfo.InvariantCulture) + ":h=" + info.Height.ToString(CultureInfo.InvariantCulture) + ":scaler=bilinear" : "") + "[grainvk]";

                    string baseFilter;

                    if (!string.IsNullOrEmpty(lutCompatName))

                        baseFilter = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=gbrp16le,setpts=PTS-STARTPTS,split=2[lutorig][lutsrc];[lutsrc]lut3d=file='" + EscapeFilterPath(lutCompatName) + "':interp=tetrahedral[lutgraded];[lutgraded][lutorig]blend=all_mode=normal:all_opacity=" + lutOpacity + ",format=p010le,hwupload[basevk]";

                    else

                        baseFilter = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=p010le,setpts=PTS-STARTPTS,hwupload[basevk]";

                    filter = baseFilter + ";" + grain + ";[basevk][grainvk]blend_vulkan=all_mode=overlay:all_opacity=" + grainOpacity + ",hwdownload,format=p010le" + frame.Filter + subtitleFilter + (hdrPreserve ? BuildHdrFrameFilter(info) : "") + ",format=p010le[vout]";

                }

                else if (fgsimMode)

                {

                    if (hdrPreserve)

                    {

                        // Keep the validated FGSIM shader unchanged. Run it on a 10-bit copy,

                        // then apply its luma delta to the original 10-bit Y plane only.

                        filter = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=yuv420p10le,setpts=PTS-STARTPTS,split=2[hdrbase][fgsrc];" +

                            "[hdrbase]extractplanes=planes=y+u+v[basey][baseu][basev];" +

                            "[fgsrc]split=2[fgbase10][fgshader10];" +

                            "[fgshader10]hwupload,libplacebo=format=yuv420p10le:custom_shader_path=" + EscapeFgsimShaderPath(fgsimHook) + ",hwdownload,format=yuv420p10le[fgout10];" +

                            "[fgbase10]extractplanes=planes=y[fgbasey];[fgout10]extractplanes=planes=y[fgouty];" +

                            "[fgbasey][fgouty]lut2=c0='512+x-y':d=10[fgdelta];" +

                            "[basey][fgdelta]lut2=c0='x+y-512':d=10[yout];" +

                            "[yout][baseu][basev]mergeplanes=map0s=0:map0p=0:map1s=1:map1p=0:map2s=2:map2p=0:format=yuv420p10le" +

                            frame.Filter + subtitleFilter + BuildHdrFrameFilter(info) + ",format=p010le[vout]";

                    }

                    else

                    {

                        string baseFilter;

                        if (!string.IsNullOrEmpty(lutCompatName))

                            baseFilter = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=gbrp16le,setpts=PTS-STARTPTS,split=2[lutorig][lutsrc];[lutsrc]lut3d=file='" + EscapeFilterPath(lutCompatName) + "':interp=tetrahedral[lutgraded];[lutgraded][lutorig]blend=all_mode=normal:all_opacity=" + lutOpacity + ",format=yuv420p[fgsimbase]";

                        else

                            baseFilter = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=yuv420p[fgsimbase]";

                        filter = baseFilter + ";[fgsimbase]hwupload,libplacebo=format=yuv420p:custom_shader_path=" + EscapeFgsimShaderPath(fgsimHook) + ",hwdownload,format=yuv420p" + frame.Filter + subtitleFilter + ",format=p010le[vout]";

                    }

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

                    filter = baseFilter + ";[procbase]split=4[seed][masksrc][base][blacksrc];[seed]scale=" + pw.ToString(CultureInfo.InvariantCulture) + ":" + ph.ToString(CultureInfo.InvariantCulture) + ",lutyuv=y=128:u=128:v=128,noise=c0s=100:c0f=t+u,deflate=threshold0=15,dilation=threshold0=10,eq=contrast=3,scale=" + info.Width.ToString(CultureInfo.InvariantCulture) + ":" + info.Height.ToString(CultureInfo.InvariantCulture) + "[n];[masksrc]lutyuv=y='" + procMask + "*(182-abs(75-val))':u=128:v=128[o];[n][o]blend=c0_mode=multiply,negate[a];[base][a]alphamerge[c];[blacksrc]drawbox=color=black:t=fill[black];[black][c]overlay=shortest=1" + frame.Filter + subtitleFilter + ",format=p010le[vout]";

                    if (hdrPreserve)

                    {

                        string range = HdrRange(info);

                        int black = range == "pc" ? 0 : 64;

                        string blackValue = black.ToString(CultureInfo.InvariantCulture);

                        string hdrBase = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=yuv420p10le[procbase]";

                        filter = hdrBase + ";[procbase]split=3[seedsrc][masksrc][base];[seedsrc]format=yuv420p,scale=" + pw.ToString(CultureInfo.InvariantCulture) + ":" + ph.ToString(CultureInfo.InvariantCulture) + ",lutyuv=y=128:u=128:v=128,noise=c0s=100:c0f=t+u,deflate=threshold0=15,dilation=threshold0=10,eq=contrast=3,scale=" + info.Width.ToString(CultureInfo.InvariantCulture) + ":" + info.Height.ToString(CultureInfo.InvariantCulture) + "[n8];[masksrc]scale=in_range=" + range + ":out_range=tv,format=yuv420p,lutyuv=y='" + procMask + "*(182-abs(75-val))':u=128:v=128[o8];[n8][o8]blend=c0_mode=multiply,negate,format=gray,format=gray10le[alpha10];[base]extractplanes=planes=y+u+v[yb][ub][vb];[yb][alpha10]lut2=c0='" + blackValue + "+(x-" + blackValue + ")*y/1023':d=10[yout];[yout][ub][vb]mergeplanes=map0s=0:map0p=0:map1s=1:map1p=0:map2s=2:map2p=0:format=yuv420p10le" + frame.Filter + subtitleFilter + BuildHdrFrameFilter(info) + ",format=p010le[vout]";

                    }

                }

                filter=LutStrengthCore.OptimizeGraph(filter);
                string pixelFull = grainEnabled ? filter : null;
                string pixelClean = grainEnabled ? SharedGrainCore.BuildWithoutGrain(deinterlaceFilter + fpsFilter + colorFilter,
                    EscapeFilterPath(lutCompatName), lutOpacity, frame.Filter + subtitleFilter + (hdrPreserve ? BuildHdrFrameFilter(info) : "") + ",format=p010le") : null;
                if (grainEnabled && (plateMode || (fgsimMode && hdrPreserve))) pixelClean=pixelClean.Replace("[0:v:0]","[0:v:0]setpts=PTS-STARTPTS,");
                pixelClean=LutStrengthCore.OptimizeGraph(pixelClean);
                if (grainEnabled) filter = PixelGrainStrengthCore.Wrap(filter,pixelClean,pixelPercent);

                if (fruc )

                {

                    string perf = Get(state, "FG_FRUC_PERF").ToLowerInvariant();

                    if (perf != "slow" && perf != "medium" && perf != "fast") { reason = "unsupported FRUC perf setting"; return false; }

                    string grid = Get(state, "FG_FRUC_GRID").ToLowerInvariant();

                    if (grid != "auto" && grid != "1" && grid != "2" && grid != "4" && grid != "8") { reason = "unsupported FRUC grid setting"; return false; }

                    filter = filter.Replace("[0:v:0]", "[0:v:0]" + SharedFrucCore.InputFilter(fps,perf,grid,hdrPreserve));


                }

                if (svp && plateMode) filter = filter.Replace("[1:v:0]", "[2:v:0]");

                long max = bitrate * 3L;

                long buf = bitrate * 6L;



                List<string> requestInputs = new List<string>(); requestInputs.Add(input); if (plateMode) requestInputs.Add(grainInput);

                BridgeExecutionRequest request = new BridgeExecutionRequest();
                if(fruc && hdrPreserve) request.ExecutionNotice = SharedFrucCore.HdrNotice;

                request.PixelGrainHdr = hdrPreserve;
                request.PixelGrainFullFilter = pixelFull;
                request.PixelGrainCleanFilter = pixelClean;
                request.ToolPath = ffmpeg;

                request.WorkingDirectory = workingDirectory;

                request.InputFiles = requestInputs;

                request.Environment = new Dictionary<string,string>(state, StringComparer.OrdinalIgnoreCase);

                if (svp && File.Exists(Path.Combine(appRoot, "_OpenSVPFlow", "_UserConfig", "vapoursynth", "vapoursynth.toml")))

                    request.Environment["APPDATA"] = Path.Combine(appRoot, "_OpenSVPFlow", "_UserConfig");

                request.DirectProcess = true;

                request.OutputFiles = new List<string>();

                request.OutputFiles.Add(output);

                request.ProgressDurationSeconds = durationSeconds > 0.0 ? durationSeconds : 0.0;

                if (hdrPreserve) request.PostRunValidation = delegate { return VerifyHdrOutput(ffprobe, output, info); };

                string encodeInput = input;

                bool mergedHdr = SharedHdrPreprocessCore.CanMergeEncode(hdrToSdr,interpolate,info.IsInterlaced);
                bool mergedFrucHdr=hdrToSdr&&fruc&&!info.IsInterlaced;
                bool needsHdrWorkfile = hdrToSdr && !mergedHdr && !mergedFrucHdr ;

                bool needsFieldWorkfile = separateDeint ;

                int totalStages = (needsHdrWorkfile || needsFieldWorkfile ? 1 : 0) + 1;

                List<BridgeExecutionStage> stages = new List<BridgeExecutionStage>();

                if (needsHdrWorkfile || needsFieldWorkfile)

                {

                    string tempRoot;

                    if (!TryResolveTempRoot(input, state, out tempRoot, out reason)) return false;

                    encodeInput = Path.Combine(tempRoot, (hdrToSdr ? "__FGS_HDR2SDR_" : "__FGS_FIELDS_") + Guid.NewGuid().ToString("N") + ".mkv");

                    temporaryFiles.Add(encodeInput);

                    stages.Add(MakeStage(ffmpeg, workingDirectory, hdrToSdr ? SharedHdrPreprocessCore.BuildToSdr(input,encodeInput,info,tonemap,true,fieldWorkfile?fieldFilter:"") : SharedHdrPreprocessCore.BuildDeinterlaced(input,encodeInput,info,fieldFilter), durationSeconds, 1, totalStages, hdrToSdr ? "HDR to SDR / " + tonemapLabel : "Shared field deinterlace / separate Vulkan device"));
                    if(hdrToSdr)SharedHdrPreprocessCore.AttachAutomatic(stages[stages.Count-1],tonemap);

                }

                    List<string> hevcArgs = BuildHevcArgs(encodeInput, grainInput, filter, output, preset, highMotion, bitrate, max, buf, fps, duration, plateMode, mkv, caps, state, hdrPreserve ? info : null, fruc, info);

                    if (svp) {
                        ConvertToPipeInput(hevcArgs, encodeInput, mpegTsSvp && !preserveSvpTransportTimeline ? mpegTsVideoOffset : null);
                        SharedInterpolationEncodeCore.NormalizeRenderedCadence(hevcArgs,fps);
                        hevcArgs.InsertRange(hevcArgs.Count-1,new[]{"-enc_time_base:v",fps.Contains("/")?fps.Split('/')[1]+"/"+fps.Split('/')[0]:"1/"+fps});
                    }

                        string mainStageText = h264?"H.264 NVENC final encode":"HEVC final encode";

                        BridgeExecutionStage mainStage = MakeStage(ffmpeg, workingDirectory, hevcArgs, durationSeconds, (needsHdrWorkfile || needsFieldWorkfile ? 2 : 1), totalStages, mainStageText);

                        if (svp) SharedSvpCore.AttachProducer(mainStage, vspipe, vpy, svpPlugins, encodeInput, state, fps);
                        if (svp && hdrToSdr) SharedHdrPreprocessCore.AttachPipeTimeline(mainStage,ffprobe,encodeInput,info,hevcArgs,input);
                        else if (preserveSvpTransportTimeline) SharedPipeTimelineCore.AttachTransportSource(mainStage,ffprobe,encodeInput,info,hevcArgs);

                        if(fieldWorkfile || separateDeint || hdrOffSdr)SharedHdrPreprocessCore.AttachFieldTimeline(mainStage,ffprobe,encodeInput,info,hdrToSdr,!hdrOffSdr);

                        if(mergedFrucHdr)
                            SharedHdrPreprocessCore.AttachMergedFruc(mainStage,input,info,tonemap,caps.GpuName);
                        else if (mergedHdr)
                        {
                            mainStage.StageText = (h264?"H.264":"HEVC")+"：HDR 合并 / 调色 / LUT / 颗粒 / 编码";
                            if(!grainEnabled)SharedHdrPreprocessCore.AttachMergedHevc(mainStage,ffprobe,input,info,tonemap,caps.GpuName);
                            else SharedHdrPreprocessCore.AttachMergedEncode(mainStage,ffprobe,input,info,tonemap,caps.GpuName);
                        }
                        else if(needsHdrWorkfile && !interpolate && !info.IsInterlaced)
                            SharedHdrPreprocessCore.AttachSharedWorkfileEncode(mainStage,ffprobe,encodeInput,caps.GpuName);

                        stages.Add(mainStage);

                    request.PixelGrainEncodeArguments = hevcArgs;

                request.DirectStages = stages;

                request.TemporaryFiles = temporaryFiles.Count == 0 ? null : temporaryFiles;

                if (fruc && request.DirectStages != null)
                    foreach (BridgeExecutionStage stage in request.DirectStages)
                        if (stage.FrucArguments != null) stage.FrucProbePath = ffprobe;
                if(info.IsHdr && info.IsInterlaced && !deinterlace)request.ExecutionNotice="[Native .NET] 反交错 OFF：沿用不反交错的逐帧处理，不启用 bob/光流或双倍场率；不保证隔行编码标记。 / Deinterlace OFF: frame processing without bob/interpolation; no interlaced-encoding flags are added.";
            if (fgsimMode) FgsimTextureCore.AttachVerifiedDevice(request, fgsimHook, hdrPreserve);
            prepared = new BridgePreparedExecution(request, false, ffmpeg, requestInputs);

                lutCompatPath = "";

                subtitleAss = "";
                subtitleFilterPath="";

                return true;

            }

            finally

            {

                if (!string.IsNullOrEmpty(lutCompatPath)) TryDelete(lutCompatPath);

                if (!string.IsNullOrEmpty(subtitleAss)) TryDelete(subtitleAss);
                if(!string.IsNullOrEmpty(subtitleFilterPath))TryDelete(subtitleFilterPath);

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

            return ",setparams=range=" + HdrRange(info) + ":color_primaries=" + HdrPrimaries(info) +

                ":color_trc=" + info.ColorTransfer + ":colorspace=" + HdrSpace(info);

        }

        private static string VerifyHdrOutput(string ffprobe, string output, MediaProbeInfo info)

        {

            try

            {

                ProcessStartInfo psi = new ProcessStartInfo(ffprobe, "-v error -select_streams v:0 -show_entries stream=pix_fmt,color_range,color_primaries,color_transfer,color_space -of default=noprint_wrappers=1 " + Quote(output));

                psi.UseShellExecute = false;

                psi.CreateNoWindow = true;

                psi.RedirectStandardOutput = true;

                using (Process process = new Process())

                {

                    process.StartInfo = psi;

                    process.Start();

                    string data = process.StandardOutput.ReadToEnd();

                    if (!process.WaitForExit(8000)) { try { process.Kill(); } catch { } return "HDR signaling verification timed out"; }

                    if (process.ExitCode != 0) return "HDR signaling probe failed";

                    Dictionary<string, string> actual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (string line in data.Split(new char[]{'\r','\n'}, StringSplitOptions.RemoveEmptyEntries))

                    {

                        int pos = line.IndexOf('=');

                        if (pos > 0) actual[line.Substring(0, pos)] = line.Substring(pos + 1).Trim();

                    }

                    string value;

                    if (!actual.TryGetValue("pix_fmt", out value) || (value != "yuv420p10le" && value != "p010le") || !actual.TryGetValue("color_range", out value) || value != HdrRange(info) ||

                        !actual.TryGetValue("color_primaries", out value) || value != HdrPrimaries(info) ||

                        !actual.TryGetValue("color_transfer", out value) || value != info.ColorTransfer ||

                        !actual.TryGetValue("color_space", out value) || value != HdrSpace(info))

                        return "HDR signaling mismatch: " + data.Trim().Replace(Environment.NewLine, " / ");

                    return "";

                }

            }

            catch (Exception ex) { return "HDR signaling probe failed: " + ex.Message; }

        }

        private static List<string> BuildHevcArgs(string input, string grainInput, string filter, string output, string preset, bool highMotion, long bitrate, long max, long buf, string fps, string duration, bool plateMode, bool mkv, HardwareCapabilitySnapshot caps, IDictionary<string,string> state, MediaProbeInfo hdrInfo, bool fruc, MediaProbeInfo audioInfo)

        {

            bool h264=Eq(state,"FG_MODE","H264");
            if(h264)filter=filter.Replace("[vout]",",format=yuv420p[vout]");
            List<string> a = new List<string>{"-hide_banner","-stats","-progress","pipe:2","-stats_period","0.5","-y"};

            if (plateMode || filter.Contains("libplacebo=") || filter.Contains("fruc_vulkan=")) { a.Add("-init_hw_device"); a.Add("vulkan=vk:0"); a.Add("-filter_hw_device"); a.Add("vk"); }

            else if (filter.Contains("bwdif_vulkan=")) { a.Add("-init_hw_device"); a.Add("vulkan=deintvk:0"); a.Add("-filter_hw_device"); a.Add("deintvk"); }

            a.Add("-i"); a.Add(input);

            if (plateMode) { a.Add("-stream_loop"); a.Add("-1"); a.Add("-t"); a.Add(duration); a.Add("-i"); a.Add(grainInput); }

            a.Add("-filter_complex"); a.Add(filter);

            a.Add("-map"); a.Add("[vout]"); a.Add("-map"); a.Add("0:a?");

            if (mkv) { a.Add("-map"); a.Add("0:s?"); a.Add("-map"); a.Add("0:t?"); }

            a.Add("-map_metadata"); a.Add("0"); a.Add("-map_chapters"); a.Add("0");

            a.Add("-c:v"); a.Add(h264?"h264_nvenc":"hevc_nvenc"); a.Add("-pix_fmt"); a.Add(h264?"yuv420p":"p010le"); a.Add("-gpu"); a.Add("0"); a.Add("-profile:v"); a.Add(h264?"high":"main10");

            a.Add("-preset"); a.Add(preset); a.Add("-tune"); a.Add("hq"); a.Add("-rc"); a.Add("vbr");



            a.Add("-b:v"); a.Add(bitrate.ToString(CultureInfo.InvariantCulture) + "k");

            a.Add("-maxrate:v"); a.Add(max.ToString(CultureInfo.InvariantCulture) + "k");

            a.Add("-bufsize:v"); a.Add(buf.ToString(CultureInfo.InvariantCulture) + "k");

            bool standard = preset == "p7";

            if ((h264?caps.H264MultipassFullres:caps.HevcMultipassFullres) && (standard || highMotion)) { a.Add("-multipass"); a.Add("fullres"); }

            else if ((h264?caps.H264MultipassQres:caps.HevcMultipassQres)) { a.Add("-multipass"); a.Add("qres"); }

            int lookahead = highMotion ? 32 : standard ? 27 : 16;

            if ((h264?caps.H264Lookahead:caps.HevcLookahead)) { a.Add("-rc-lookahead"); a.Add(lookahead.ToString(CultureInfo.InvariantCulture)); }

            if (highMotion && (h264?caps.H264Lookahead:caps.HevcLookahead) && (h264?caps.H264AdaptiveB:caps.HevcAdaptiveB)) { a.Add("-b_adapt"); a.Add("1"); }

            if (highMotion && (h264?caps.H264Lookahead:caps.HevcLookahead) && (h264?caps.H264SceneCut:caps.HevcSceneCut)) { a.Add("-no-scenecut"); a.Add("0"); }

            int aq;

            if (!int.TryParse(Get(state,"FG_HEVC_SPATIAL_AQ"), NumberStyles.Integer, CultureInfo.InvariantCulture, out aq)) aq = 8;

            if (aq != 0 && (h264?caps.H264SpatialAq:caps.HevcSpatialAq)) { a.Add("-spatial-aq"); a.Add("1"); a.Add("-aq-strength"); a.Add(aq.ToString(CultureInfo.InvariantCulture)); }

            if (Get(state,"FG_HEVC_TEMPORAL_AQ") == "1" && (h264?caps.H264TemporalAq:caps.HevcTemporalAq)) { a.Add("-temporal-aq"); a.Add("1"); }

            if ((h264?caps.H264BFrames:caps.HevcBFrames)) { a.Add("-bf"); a.Add("4"); if ((h264?caps.H264BReference:caps.HevcBReference)) { a.Add("-b_ref_mode"); a.Add("middle"); } }

            if (hdrInfo != null)

            {

                a.Add("-color_range"); a.Add(HdrRange(hdrInfo));

                a.Add("-color_primaries"); a.Add(HdrPrimaries(hdrInfo));

                a.Add("-color_trc"); a.Add(hdrInfo.ColorTransfer);

                a.Add("-colorspace"); a.Add(HdrSpace(hdrInfo));

            }

            // fruc_vulkan already emits frames with the requested target rate and timestamps.

            // Match Native x264: do not apply a second output-rate conversion after FRUC.

            if (!fruc) { a.Add("-r"); a.Add(fps); }

            a.Add("-fps_mode:v"); a.Add(fruc ? "passthrough" : "cfr");

            if (plateMode) { a.Add("-t"); a.Add(duration); }

            if (mkv) { a.Add("-c:a"); a.Add("copy"); a.Add("-c:s"); a.Add("copy"); a.Add("-c:t"); a.Add("copy"); }

            else { a.Add("-c:a"); a.Add("aac"); a.Add("-b:a"); a.Add("256k"); if(!h264){a.Add("-tag:v"); a.Add("hvc1");} a.Add("-movflags"); a.Add("+faststart"); }

            NvencRateControlCore.Apply(a,state);
            NvencMultipassCore.Apply(a,state,h264?"H264":"HEVC",caps);
            NvencMainOptionsCore.Apply(a,state,h264?"H264":"HEVC",caps);
            a.Add(output);

            AudioOutputCore.Apply(a,state,audioInfo,mkv,false,false);
            return a;

        }



        internal static bool HasFrucFilter(string ffmpeg)
        {
            string output, reason;
            return SharedFrucCore.Run(ffmpeg, "-hide_banner -h filter=fruc_vulkan", 5000, out output, out reason) &&
                output.Contains("Filter fruc_vulkan") && output.IndexOf("Unknown filter", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static bool IsMpegTsInput(string input)

        {

            string extension = Path.GetExtension(input);

            return extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||

                extension.Equals(".mts", StringComparison.OrdinalIgnoreCase) ||

                extension.Equals(".m2ts", StringComparison.OrdinalIgnoreCase);

        }

        private static bool TryGetMpegTsVideoOffset(string ffprobe, string input, out string sourceOffset, out string reason)

        {

            sourceOffset = "";

            reason = "";

            try

            {

                ProcessStartInfo psi = new ProcessStartInfo();

                psi.FileName = ffprobe;

                psi.Arguments = "-v error -select_streams v:0 -show_entries stream=start_time -of default=nokey=1:noprint_wrappers=1 " + Quote(input);

                psi.UseShellExecute = false;

                psi.CreateNoWindow = true;

                psi.RedirectStandardOutput = true;

                StringBuilder output = new StringBuilder();

                using (Process process = new Process())

                {

                    process.StartInfo = psi;

                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)

                    {

                        if (e.Data != null) output.AppendLine(e.Data);

                    };

                    process.Start();

                    process.BeginOutputReadLine();

                    if (!process.WaitForExit(15000))

                    {

                        try { process.Kill(); } catch { }

                        reason = "MPEG-TS OpenSVPFlow video timestamp probe timed out";

                        return false;

                    }

                    process.WaitForExit();

                    if (process.ExitCode != 0)

                    {

                        reason = "Could not read MPEG-TS video start timestamp for OpenSVPFlow sync";

                        return false;

                    }

                }

                string timestamp = FirstLine(output.ToString()).Trim();

                double seconds;

                if (string.IsNullOrWhiteSpace(timestamp) || timestamp.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||

                    !double.TryParse(timestamp, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) ||

                    double.IsNaN(seconds) || double.IsInfinity(seconds))

                {

                    reason = "MPEG-TS OpenSVPFlow video start timestamp is unavailable";

                    return false;

                }

                sourceOffset = seconds == 0.0 ? "0" : seconds < 0.0 ? timestamp.Substring(1) : "-" + timestamp.TrimStart('+');

                return true;

            }

            catch (Exception ex)

            {

                reason = "MPEG-TS OpenSVPFlow timestamp probe failed: " + ex.Message;

                return false;

            }

        }

        private static void ConvertToPipeInput(List<string> args, string input, string sourceOffset)

        {

            int index = args.IndexOf("-i");

            if (index < 0 || index + 1 >= args.Count || args[index + 1] != input)

                throw new InvalidOperationException("OpenSVPFlow HEVC input mapping changed");

            if (!string.IsNullOrEmpty(sourceOffset))

                args.InsertRange(index, new string[]{"-copyts", "-f", "yuv4mpegpipe"});

            else

                args.InsertRange(index, new string[]{"-f", "yuv4mpegpipe"});

            int pipeInputIndex = index + (string.IsNullOrEmpty(sourceOffset) ? 3 : 4);

            args[pipeInputIndex] = "pipe:0";

            if (!string.IsNullOrEmpty(sourceOffset))

                args.InsertRange(pipeInputIndex + 1, new string[]{"-itsoffset", sourceOffset, "-i", input});

            else

                args.InsertRange(pipeInputIndex + 1, new string[]{"-i", input});

            for (int i = 0; i < args.Count; i++)

            {

                if (args[i] == "0:a?") args[i] = "1:a?";

                if (args[i] == "0:s?") args[i] = "1:s?";

                if (args[i] == "0:t?") args[i] = "1:t?";

                if (args[i] == "-map_metadata" || args[i] == "-map_chapters")

                    if (i + 1 < args.Count && args[i + 1] == "0") args[i + 1] = "1";

            }

        }

        private static BridgeExecutionStage MakeStage(string ffmpeg, string workingDirectory, IList<string> args, double duration, int current, int total, string text)

        {

            BridgeExecutionStage stage = new BridgeExecutionStage();

            stage.ToolPath = ffmpeg;

            stage.WorkingDirectory = workingDirectory;

            stage.DirectArguments = JoinArgs(args);

            stage.OriginalArguments = new List<string>(args);
            int frucFilterIndex = args.IndexOf("-filter_complex");
            if (frucFilterIndex >= 0 && frucFilterIndex + 1 < args.Count && args[frucFilterIndex + 1].Contains("fruc_vulkan="))
                stage.FrucArguments = new List<string>(args);

            stage.ProgressDurationSeconds = duration > 0.0 ? duration : 0.0;

            stage.StageCurrent = current;

            stage.StageTotal = total;

            stage.StageText = text;

            return stage;

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

        private static bool TryPrepareLut(string ffmpeg, IDictionary<string,string> state, bool bypassLut, out string compatPath, out string compatName, out string opacity, out string reason)

        {

            compatPath = ""; compatName = ""; opacity = ""; reason = "";

            if (bypassLut) return true;

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

        private static bool TryPrepareFgsimHook(string appRoot, string ffmpeg, string hook, string preset, out string reason)
        {
            string prepared; return FgsimTextureCore.TryPrepareHook(appRoot, preset, out prepared, out reason);
        }

        private static string EscapeFgsimShaderPath(string value)

        {

            string path = (value ?? "").Replace("\\", "/").Replace(":", "\\:").Replace(",", "\\,").Replace(";", "\\;").Replace("[", "\\[").Replace("]", "\\]");

            return "'" + path.Replace("'", "'" + new string('\\', 3) + "''") + "'";

        }

        private static int Even(int v) { return ((v + 1) / 2) * 2; }

        private static string Get(IDictionary<string,string> s, string k) { string v; return s.TryGetValue(k, out v) ? (v ?? "") : ""; }

        private static bool Eq(IDictionary<string,string> s, string k, string v) { return string.Equals(Get(s, k), v, StringComparison.OrdinalIgnoreCase); }

        // Relative strength produces its own final blend; convert that completed result once.
        // Keep the established 100% subtitle/conversion order unchanged.

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

