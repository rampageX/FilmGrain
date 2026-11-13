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

            if (interpolate && !fruc && !NvencInterpolationCore.Enabled(state) && !Eq(state, "FG_INTERPOLATION_ENGINE", "SVP") && !string.IsNullOrEmpty(Get(state, "FG_INTERPOLATION_ENGINE")))

            { reason = "unsupported interpolation engine"; return false; }

            string speed = Get(state, "FG_SPEED").ToUpperInvariant();

            if (speed != "FAST" && speed != "STANDARD") { reason = "unsupported HEVC speed"; return false; }

            bool hdrToSdr = HdrColorProcessingCore.NvencToSdr(info,state);
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

            if (svp && hdrPreserve && Get(state,"FG_SVP_HDR10")!="1") { reason = "OpenSVPFlow HDR preservation requires the validated HDR10 high-depth path."; return false; }

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

                fpsSuffix = (NvencInterpolationCore.Enabled(state) ? (Eq(state,"FG_INTERPOLATION_ENGINE","NVENC_AI")?"_NVAI":"_NVFRUC") : fruc ? "_FRUC" : "_SVP") + targetRate.Label + (svp && !fruc && !NvencInterpolationCore.Enabled(state) ? SharedSvpCore.BackendFileSuffix(state) : "");

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
            ResizePlan resize;
            if(!ResizeCore.TryBuild(info,state,frame,out resize,out reason,hdrPreserve))return false;
            int processingWidth=resize.Enabled?resize.Width:info.Width, processingHeight=resize.Enabled?resize.Height:info.Height;

            bool highMotion = Get(state, "FG_HIGH_MOTION") == "1";

            if (!NvencRateControlCore.IsCq(state) && Eq(state, "FG_BITRATE_MODE", "AUTO"))

            {

                double rateFps = ParseFps(fps);

                bitrate = BitrateRecommendationCore.GetRecommendedBitrate(1, frame.ActiveWidth, frame.ActiveHeight, rateFps, highMotion);

            }

            string colorFilter, colorSuffix;

            if (!TryBuildColorFilter(state, out colorFilter, out colorSuffix, out reason)) return false;
            if(hdrPreserve)colorFilter=HdrColorProcessingCore.Cc(state,info);
            string preprocessing;
            if(!PreprocessingCore.TryBuild(state,out preprocessing,out reason))return false;
            if(preprocessing.Length>0 && !PreprocessingCore.TryCheckTool(ffmpeg,out reason))return false;
            if(resize.Enabled && !ResizeCore.TryCheckTool(ffmpeg,resize,out reason))return false;
            colorFilter=preprocessing+resize.Prefix+colorFilter;
            if(preprocessing.Length>0)colorSuffix="_BIL"+colorSuffix;

            string lutNamingSuffix = BuildLutNamingSuffix(state);

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

                grainScale = SharedGrainCore.NeedsPlateScaling(ffprobe, plate, processingWidth, processingHeight);

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
                if (svp && !NvencInterpolationCore.Enabled(state) && (!File.Exists(vspipe)||!File.Exists(vpy)||!File.Exists(Path.Combine(svpPlugins,"svpflow1_vs.dll"))||!File.Exists(Path.Combine(svpPlugins,"svpflow2_vs.dll")))) { reason="OpenSVPFlow dependencies are missing";return false; }
                if (mpegTsSvp && !preserveSvpTransportTimeline && !TryGetMpegTsVideoOffset(ffprobe,input,out mpegTsVideoOffset,out reason))return false;
                if(fgsimMode) {
                    if(!TryPrepareFgsimHook(appRoot,ffmpeg,fgsimHook,Get(state,"FG_FGSIM_PRESET").ToUpperInvariant(),out reason))return false;
                }

            string lutCompatPath = "", lutCompatName = "", lutOpacity = "";

            if (!TryPrepareLut(ffmpeg, state, false, out lutCompatPath, out lutCompatName, out lutOpacity, out reason)) return false;
            if(hdrPreserve && !string.IsNullOrEmpty(lutCompatName)) {
                colorFilter+=Hdr