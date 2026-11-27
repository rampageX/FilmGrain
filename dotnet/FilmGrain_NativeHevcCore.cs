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
            FilmBorderEncodingCore.Plan filmBorder;
            // FGB-02 opt-in is confined to HEVC NVENC SVP/NVEncC; all other encoder routes keep their guard.
            if (!FilmBorderEncodingCore.TryCreate(appRoot,info,state,interpolate,hdrPreserve,out filmBorder,out reason,interpolate && svp && !h264 &&
                (!NvencInterpolationCore.Enabled(state) || Eq(state,"FG_INTERPOLATION_ENGINE","NVENC_AI")))) return false;
            if(h264&&hdrPreserve){reason="H.264 NVENC 本线输出 8bit；HDR 源请选 HDR→SDR，不能保留 HDR10。 / H.264 8-bit output requires HDR to SDR.";return false;}

            string tonemap = Get(state, "FG_TONEMAP_ALGO").ToLowerInvariant();

            if (tonemap != "mobius" && tonemap != "reinhard" && tonemap != "gamma" && tonemap != "linear" && tonemap != "clip") tonemap = "hable";

            string toneSettings = Get(state, "FG_TONEMAP_OPTIONS");
            string tonemapLabel = char.ToUpperInvariant(tonemap[0]) + tonemap.Substring(1);

            bool mkv = Eq(state, "FG_CONTAINER", "MKV");

            if (!mkv && !Eq(state, "FG_CONTAINER", "MP4")) { reason = "unsupported HEVC container"; return false; }

            bool autoFps = Eq(state, "FG_FPS_MODE", "AUTO");

            if (!autoFps && !Eq(state, "FG_FPS_MODE", "SOURCE") && !Eq(state, "FG_FPS_MODE", "KEEP")) { reason = "unsupported HEVC FPS mode"; return false; }

            if (fruc && info.IsInterlaced) { reason = "interlaced FRUC is not supported"; return false; }

            if (svp && hdrPreserve && !NvencInterpolationCore.Enabled(state) && Get(state,"FG_SVP_HDR10")!="1") { reason = "OpenSVPFlow HDR preservation requires the validated HDR10 high-depth path."; return false; }

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

            // R01: Plate can run after Vulkan field bob in the same encode graph.
            // FGSIM and interpolated Plate keep their original intermediate-file protection.

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

            bool inlineVulkanPlate=deinterlace && !hdrToSdr && bwdifVulkan && plateMode && !interpolate;
            bool separateDeint=deinterlace && !hdrToSdr && bwdifVulkan && (fgsimMode || (plateMode && interpolate));

            if(fieldWorkfile || separateDeint || hdrOffSdr)deinterlaceFilter="";
            else if(inlineVulkanPlate)deinterlaceFilter=SharedHdrPreprocessCore.InlineVulkanPlateSoftwareFilter(info,fps);

            string deinterlaceSuffix = bwdifVulkan ? "_DI_BWV" : bwdifCuda ? "_DI_BWC" : deinterlace ? "_DI_W3F" : "";

            double durationSeconds = 0.0;

            double.TryParse(info.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out durationSeconds);

            if (plateMode && durationSeconds <= 0.0) { reason = "invalid source duration for Grain Plate loop bound"; return false; }

            string duration = durationSeconds > 0.0 ? durationSeconds.ToString("0.######", CultureInfo.InvariantCulture) : "";

            string ffmpeg = Path.Combine(GetFfmpegDir(appRoot), "ffmpeg.exe");

            string ffprobe = Path.Combine(GetFfmpegDir(appRoot), "ffprobe.exe");

            NvencRateControlCore.RequireSupport(ffmpeg,state);
            if (!File.Exists(ffmpeg)) { reason = "ffmpeg.exe not found: " + ffmpeg; return false; }
            string zscaleHelp;
            if (filmBorder != null && filmBorder.HdrTransfer != "" &&
                (!SharedFrucCore.Run(ffmpeg, "-hide_banner -h filter=zscale", 5000, out zscaleHelp, out reason) ||
                 !zscaleHelp.Contains("Filter zscale")))
            { reason = "HDR film border requires FFmpeg with the zscale filter."; return false; }

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
            if(!FilmBorderEncodingCore.ConfigureExpand(filmBorder,frame,resize,info,h264?"H264":"HEVC",out reason))return false;
            int processingWidth=resize.Enabled?resize.Width:info.Width, processingHeight=resize.Enabled?resize.Height:info.Height;

            bool highMotion = Get(state, "FG_HIGH_MOTION") == "1";

            if (!NvencRateControlCore.IsCq(state) && Eq(state, "FG_BITRATE_MODE", "AUTO"))

            {

                double rateFps = ParseFps(fps);

                bitrate = BitrateRecommendationCore.GetRecommendedBitrate(1, FilmBorderEncodingCore.FinalWidth(filmBorder,frame.ActiveWidth), FilmBorderEncodingCore.FinalHeight(filmBorder,frame.ActiveHeight), rateFps, highMotion);

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
            string borderSuffix = filmBorder == null ? "" : filmBorder.FileSuffix;
            string output = Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + (h264?"_H264_NVENC_":"_HEVC_") + speedSuffix + "_" + NvencRateControlCore.FileToken(state,bitrate) + fpsSuffix + deinterlaceSuffix + frame.FileSuffix + grainFileName + colorSuffix + lutNamingSuffix + (hdrToSdr ? "_SDR_" + tonemapLabel : "") + subSuffix + borderSuffix + HardwareDecodeCore.FileSuffix(state) + (mkv ? ".mkv" : ".mp4"));

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
                colorFilter+=HdrColorProcessingCore.Lut(info,EscapeFilterPath(lutCompatName),lutOpacity);
                lutCompatName=""; // Already applied in the shared HDR prefix; keep the LUT file for execution.
            }

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
                bool expand=filmBorder!=null && filmBorder.ExpandGeometry!=null;
                if(expand)
                {
                    string root;if(!TryResolveTempRoot(input,state,out root,out reason))return false;
                    FilmBorderEncodingCore.StageExpandTexture(filmBorder,root,temporaryFiles);
                }

                // Experimental route is deliberately narrower than the UI's general border mode.
                // No match means the validated CPU graph is used unchanged.
                bool s4GpuRequested = Environment.GetEnvironmentVariable("FGS_S4_GPU_BORDER") != "0";
                bool fgb02 = interpolate && svp && !h264 && filmBorder != null &&
                    (!NvencInterpolationCore.Enabled(state) || Eq(state,"FG_INTERPOLATION_ENGINE","NVENC_AI"));
                // The FGB-02 SDR GPU path is BT.709 limited range only; never relabel explicit BT.601/PC material.
                if (fgb02 && !info.IsHdr &&
                    ((!string.IsNullOrEmpty(info.ColorPrimaries) && info.ColorPrimaries != "bt709" && info.ColorPrimaries != "unknown") ||
                     (!string.IsNullOrEmpty(info.ColorTransfer) && info.ColorTransfer != "bt709" && info.ColorTransfer != "unknown") ||
                     (!string.IsNullOrEmpty(info.ColorSpace) && info.ColorSpace != "bt709" && info.ColorSpace != "unknown") ||
                     info.ColorRange == "pc"))
                { reason = "FGB-02 SDR GPU film border currently requires BT.709 limited-range input."; return false; }
                bool s4Gpu4k30 = info.Width == 3840 && info.Height == 2160 &&
                    frame.ActiveWidth == 3840 && frame.ActiveHeight == 2160 &&
                    frame.ContentX == 0 && frame.ContentY == 276 && frame.ContentWidth == 3840 && frame.ContentHeight == 1608 &&
                    Math.Abs(ParseFps(fps) - 30.0) < 0.001;
                bool s4SdrPlate = plateMode && !interpolate && !info.IsHdr && !hdrToSdr &&
                    (string.IsNullOrEmpty(info.ColorPrimaries) || info.ColorPrimaries == "bt709" || info.ColorPrimaries == "unknown") &&
                    (string.IsNullOrEmpty(info.ColorTransfer) || info.ColorTransfer == "bt709" || info.ColorTransfer == "unknown") &&
                    (string.IsNullOrEmpty(info.ColorSpace) || info.ColorSpace == "bt709" || info.ColorSpace == "unknown") &&
                    (string.IsNullOrEmpty(info.ColorRange) || info.ColorRange == "tv" || info.ColorRange == "unknown");
                // Resize applies its crop before grain. With crop alone, the plate is
                // still blended at source size and the border compositor applies the crop.
                bool cropAfterGrain = frame.Filter.StartsWith(",crop=", StringComparison.Ordinal);
                bool s4PlateGeometry = s4SdrPlate &&
                    (resize.Enabled || (cropAfterGrain && ((frame.ContentX | frame.ContentY) & 1) == 0)) &&
                    FilmBorderEncodingCore.ValidDimensions(frame.ActiveWidth, frame.ActiveHeight);
                bool s4HdrGrade = hdrPreserve && plateMode && !interpolate && !string.IsNullOrEmpty(colorFilter);
                bool expandGeometryChanged=expand && (resize.Enabled || frame.Filter.Length!=0);
                bool expandPlainSdr=expand && !info.IsHdr && !grainEnabled;
                bool s4GpuBorder = s4GpuRequested && filmBorder != null &&
                    ((hdrPreserve || (fgb02 && !info.IsHdr) || s4SdrPlate) || expandPlainSdr) &&
                    ((!h264 || s4SdrPlate) || expandPlainSdr) && (!interpolate || fgb02) &&
                    (!grainEnabled || plateMode) && durationSeconds > 0.0 && !info.IsInterlaced &&
                    (expandGeometryChanged || s4PlateGeometry || (!resize.Enabled && !cropAfterGrain &&
                        frame.ActiveWidth == info.Width && frame.ActiveHeight == info.Height)) &&
                    (expand && !grainEnabled || FilmBorderEncodingCore.ValidDimensions(info.Width, info.Height)) && ParseFps(fps) > 0 &&
                    (string.IsNullOrEmpty(preprocessing) || (s4SdrPlate && PreprocessingCore.IsOnlyBilateral(state,preprocessing))) &&
                    (string.IsNullOrEmpty(colorFilter) || (expand && colorFilter==resize.Prefix) || s4SdrPlate || s4HdrGrade) &&
                    (string.IsNullOrEmpty(lutCompatName) || s4SdrPlate) && string.IsNullOrEmpty(subtitleFilter) &&
                    pixelPercent == 100;
                if(expand && (hdrPreserve || expandGeometryChanged) && !s4GpuBorder)
                { reason="Expand Canvas with HDR or Crop/Resize on NVENC requires S4 Vulkan/CUDA, supported color and dimensions, no subtitles or partial pixel-grain strength, and either no grain or a supported Grain Plate combination.";return false; }
                if(expand)filmBorder.ExpandGpu=s4GpuBorder;
                // Never silently use the CPU border when the explicitly requested combined GPU path is unavailable.
                if (fgb02 && !s4GpuBorder)
                { reason = "FGB-02 requires S4 Vulkan/CUDA GPU compositing: progressive HEVC, no resize/LUT/CC/subtitles/preprocess/FGSIM, and no grain or Grain Plate only."; return false; }
                int filmInputIndex = plateMode ? (svp ? 3 : 2) : (svp ? 2 : 1);
                string s4Signal = (fgb02 || s4SdrPlate || (s4GpuBorder && (s4HdrGrade || expand))) ? "setparams=range=limited:color_primaries=" + (hdrPreserve ? "bt2020" : "bt709") +
                    ":color_trc=" + (hdrPreserve ? info.ColorTransfer : "bt709") +
                    ":colorspace=" + (hdrPreserve ? "bt2020nc" : "bt709") + "," : "";
                if (s4GpuBorder)
                {
                    if(!expand)
                    {
                        string raw = filmBorder.GpuRaw;
                        if (!File.Exists(raw) || new FileInfo(raw).Length != 1920L*1080*4)
                        { reason = "S4 GPU film border requires verified 1920x1080 RGBA texture: " + raw; return false; }
                        filmBorder.GpuRaw = raw;
                    }
                    filmBorder.GpuInputIndex = filmInputIndex;
                    filmBorder.GpuClockGated = !expand && !grainEnabled;
                    filmBorder.GpuSdr = (fgb02 && !hdrPreserve) || s4SdrPlate || expandPlainSdr;
                    filmBorder.GpuH264 = (s4SdrPlate || expandPlainSdr) && h264;
                    filmBorder.GpuUseLegacy4k = !expand && hdrPreserve && s4Gpu4k30 && !interpolate && !filmBorder.User &&
                        FilmBorderFramingCore.Neutral(filmBorder.FocusX,filmBorder.FocusY,filmBorder.Zoom);
                    FilmBorderEncodingCore.ConfigureGpuGeometry(filmBorder, frame, s4PlateGeometry, cropAfterGrain);
                }
                // The independent R3M8P4 Vulkan->CUDA pass used this FFmpeg binary.
                // Do not silently switch back to a different FFmpeg build for the GPU experiment.
                string s4GpuFfmpeg = s4GpuBorder ? (Environment.GetEnvironmentVariable("FGS_S4_GPU_FFMPEG") ?? "").Trim().Trim('"') : "";
                if (s4GpuBorder && s4GpuFfmpeg.Length == 0) s4GpuFfmpeg = ffmpeg;
                if (s4GpuBorder && (s4GpuFfmpeg.Length == 0 || !File.Exists(s4GpuFfmpeg)))
                { reason = "S4 GPU route requires FGS_S4_GPU_FFMPEG pointing to the verified FFmpeg build: " + s4GpuFfmpeg; return false; }

                string filter;

                if (!grainEnabled)
                {
                    if (s4GpuBorder)
                        filter = FilmBorderEncodingCore.AppendGpuFilter(
                            expand ? FilmBorderEncodingCore.ExpandGpuPicture(deinterlaceFilter+fpsFilter+colorFilter,frame,s4Signal) :
                            "[0:v:0]" + deinterlaceFilter + fpsFilter + (s4Gpu4k30 ? "fps=30," : "fps=" + fps + ",") +
                            "format=p010le," + s4Signal + "setpts=PTS-STARTPTS,split[fgsfilm_gpu_src][fgsfilm_clock_src];" +
                            "[fgsfilm_gpu_src]hwupload[fgsfilm_gpu_video]", filmBorder);
                    else
                        filter = SharedGrainCore.BuildWithoutGrain(deinterlaceFilter + fpsFilter + colorFilter,
                            EscapeFilterPath(lutCompatName), lutOpacity,
                            frame.Filter + subtitleFilter + (hdrPreserve ? BuildHdrFrameFilter(info) : "") + ",format=" + "p010le");
                }
                else if (plateMode)

                {

                    string grain = "[" + (fgb02 ? "2" : "1") + ":v:0]fps=" + fps + ",format=p010le,setpts=PTS-STARTPTS,hwupload" + (grainScale ? ",scale_vulkan=w=" + processingWidth.ToString(CultureInfo.InvariantCulture) + ":h=" + processingHeight.ToString(CultureInfo.InvariantCulture) + ":scaler=bilinear" : "") + "[grainvk]";

                    string baseFilter;

                    if (!string.IsNullOrEmpty(lutCompatName))

                        baseFilter = s4GpuBorder && s4SdrPlate
                            ? BuildSdrPlateLutGpuBaseFilter(deinterlaceFilter, fpsFilter, colorFilter, lutCompatName, lutOpacity, fps, s4Signal)
                            : "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=gbrp16le,setpts=PTS-STARTPTS,split=2[lutorig][lutsrc];[lutsrc]lut3d=file='" + EscapeFilterPath(lutCompatName) + "':interp=tetrahedral[lutgraded];[lutgraded][lutorig]blend=all_mode=normal:all_opacity=" + lutOpacity + ",format=p010le,hwupload[basevk]";

                    else if (inlineVulkanPlate && string.IsNullOrEmpty(colorFilter))
                        baseFilter = "[0:v:0]" + SharedHdrPreprocessCore.InlineVulkanPlateFilter(info,fps) + "[basevk]";
                    else
                        baseFilter = "[0:v:0]" + deinterlaceFilter + fpsFilter + (s4GpuBorder && !expand ? (s4Gpu4k30 && !interpolate ? "fps=30," : "fps=" + fps + ",") : "") + colorFilter + "format=p010le," + s4Signal + "setpts=PTS-STARTPTS,hwupload[basevk]";

                    if (s4GpuBorder)
                        filter = FilmBorderEncodingCore.AppendGpuFilter(
                            baseFilter + ";" + grain + ";[basevk][grainvk]blend_vulkan=all_mode=overlay:all_opacity=" + grainOpacity +
                            (expand && frame.Filter.Length!=0 ? ",hwdownload,format=p010le" + frame.Filter + ",hwupload" : "") +
                            "[fgsfilm_gpu_video]",
                            filmBorder);
                    else
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

                    int pw = Even((processingWidth * 4 + 2) / 3);

                    int ph = Even((processingHeight * 4 + 2) / 3);

                    string baseFilter;

                    if (!string.IsNullOrEmpty(lutCompatName))

                        baseFilter = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=gbrp16le,setpts=PTS-STARTPTS,split=2[lutorig][lutsrc];[lutsrc]lut3d=file='" + EscapeFilterPath(lutCompatName) + "':interp=tetrahedral[lutgraded];[lutgraded][lutorig]blend=all_mode=normal:all_opacity=" + lutOpacity + ",format=yuv420p[procbase]";

                    else

                        baseFilter = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=yuv420p[procbase]";

                    filter = baseFilter + ";[procbase]split=4[seed][masksrc][base][blacksrc];[seed]scale=" + pw.ToString(CultureInfo.InvariantCulture) + ":" + ph.ToString(CultureInfo.InvariantCulture) + ",lutyuv=y=128:u=128:v=128,noise=c0s=100:c0f=t+u,deflate=threshold0=15,dilation=threshold0=10,eq=contrast=3,scale=" + processingWidth.ToString(CultureInfo.InvariantCulture) + ":" + processingHeight.ToString(CultureInfo.InvariantCulture) + "[n];[masksrc]lutyuv=y='" + procMask + "*(182-abs(75-val))':u=128:v=128[o];[n][o]blend=c0_mode=multiply,negate[a];[base][a]alphamerge[c];[blacksrc]drawbox=color=black:t=fill[black];[black][c]overlay=shortest=1" + frame.Filter + subtitleFilter + ",format=p010le[vout]";

                    if (hdrPreserve)

                    {

                        string range = HdrRange(info);

                        int black = range == "pc" ? 0 : 64;

                        string blackValue = black.ToString(CultureInfo.InvariantCulture);

                        string hdrBase = "[0:v:0]" + deinterlaceFilter + fpsFilter + colorFilter + "format=yuv420p10le[procbase]";

                        filter = hdrBase + ";[procbase]split=3[seedsrc][masksrc][base];[seedsrc]format=yuv420p,scale=" + pw.ToString(CultureInfo.InvariantCulture) + ":" + ph.ToString(CultureInfo.InvariantCulture) + ",lutyuv=y=128:u=128:v=128,noise=c0s=100:c0f=t+u,deflate=threshold0=15,dilation=threshold0=10,eq=contrast=3,scale=" + processingWidth.ToString(CultureInfo.InvariantCulture) + ":" + processingHeight.ToString(CultureInfo.InvariantCulture) + "[n8];[masksrc]scale=in_range=" + range + ":out_range=tv,format=yuv420p,lutyuv=y='" + procMask + "*(182-abs(75-val))':u=128:v=128[o8];[n8][o8]blend=c0_mode=multiply,negate,format=gray,format=gray10le[alpha10];[base]extractplanes=planes=y+u+v[yb][ub][vb];[yb][alpha10]lut2=c0='" + blackValue + "+(x-" + blackValue + ")*y/1023':d=10[yout];[yout][ub][vb]mergeplanes=map0s=0:map0p=0:map1s=1:map1p=0:map2s=2:map2p=0:format=yuv420p10le" + frame.Filter + subtitleFilter + BuildHdrFrameFilter(info) + ",format=p010le[vout]";

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
                if (s4GpuBorder) request.Environment["FG_S4_GPU_BORDER_ACTIVE"] = "1";
                if(expand) request.ExecutionNotice=FilmBorderEncodingCore.ExpandNotice(filmBorder);

                if (svp && File.Exists(Path.Combine(appRoot, "_OpenSVPFlow", "_UserConfig", "vapoursynth", "vapoursynth.toml")))

                    request.Environment["APPDATA"] = Path.Combine(appRoot, "_OpenSVPFlow", "_UserConfig");

                request.DirectProcess = true;

                request.OutputFiles = new List<string>();

                request.OutputFiles.Add(output);

                request.ProgressDurationSeconds = durationSeconds > 0.0 ? durationSeconds : 0.0;

                if (hdrPreserve) request.PostRunValidation = delegate { return VerifyHdrOutput(ffprobe, output, info); };

                string encodeInput = input;

                bool mergedFields = SharedHdrPreprocessCore.CanMergeFieldEncode(fieldWorkfile,interpolate,grainEnabled,filmBorder!=null);
                bool mergedHdr = mergedFields || SharedHdrPreprocessCore.CanMergeEncode(hdrToSdr,interpolate,info.IsInterlaced);
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

                    stages.Add(MakeStage(ffmpeg, workingDirectory, hdrToSdr ? SharedHdrPreprocessCore.BuildTunedToSdr(input,encodeInput,info,tonemap,true,fieldWorkfile?fieldFilter:"",toneSettings) : SharedHdrPreprocessCore.BuildDeinterlaced(input,encodeInput,info,fieldFilter), durationSeconds, 1, totalStages, hdrToSdr ? "HDR to SDR / " + tonemapLabel : "Shared field deinterlace / separate Vulkan device"));
                    if(hdrToSdr)SharedHdrPreprocessCore.AttachTunedAutomatic(stages[stages.Count-1],tonemap,settings:toneSettings);

                }

                    if (filmBorder != null && !s4GpuBorder)
                        filter = FilmBorderEncodingCore.AppendFilter(filter,filmBorder,frame.ActiveWidth,frame.ActiveHeight,filmInputIndex,fps,info);
                    List<string> hevcArgs = BuildHevcArgs(encodeInput, grainInput, filter, output, preset, highMotion, bitrate, max, buf, fps, duration, plateMode, mkv, caps, state, hdrPreserve ? info : null, fruc, info, filmBorder);
                    // Only patched FFmpeg builds accept the host-copy workaround; keep old binaries unchanged.
                    if (s4GpuBorder && SupportsHostImageCopyOption(s4GpuFfmpeg))
                    {
                        int device = hevcArgs.IndexOf("-init_hw_device");
                        if (device < 0 || device + 1 >= hevcArgs.Count)
                            throw new InvalidOperationException("S4 GPU Vulkan device argument is missing.");
                        hevcArgs[device + 1] += ",disable_host_image_copy=1";
                    }

                    if (svp) {
                        ConvertToPipeInput(hevcArgs, encodeInput, mpegTsSvp && !preserveSvpTransportTimeline ? mpegTsVideoOffset : null);
                        // FGB-02 clocks the interpolated frames before Vulkan upload; do not append software fps after CUDA.
                        if(!NvencInterpolationCore.Enabled(state) && !s4GpuBorder)SharedInterpolationEncodeCore.NormalizeRenderedCadence(hevcArgs,fps);
                        hevcArgs.InsertRange(hevcArgs.Count-1,new[]{"-enc_time_base:v",fps.Contains("/")?fps.Split('/')[1]+"/"+fps.Split('/')[0]:"1/"+fps});
                    }

                        string mainStageText = h264?"H.264 NVENC final encode":"HEVC final encode";

                        BridgeExecutionStage mainStage = MakeStage(s4GpuBorder ? s4GpuFfmpeg : ffmpeg, workingDirectory, hevcArgs, durationSeconds, (needsHdrWorkfile || needsFieldWorkfile ? 2 : 1), totalStages, mainStageText);
                        if (s4GpuBorder) mainStage.StageText += (fgb02 ? " / FGB-02 interpolation then " : " / ") + (expand?"S4 Expand Canvas Vulkan-overlay-CUDA":"S4 Vulkan-libplacebo-CUDA GPU film border") + " / software decode";

                        if (svp) SharedSvpCore.AttachProducer(mainStage, vspipe, vpy, svpPlugins, encodeInput, state, fps);
                        if (svp && hdrToSdr) SharedHdrPreprocessCore.AttachPipeTimeline(mainStage,ffprobe,encodeInput,info,hevcArgs,input);
                        else if (preserveSvpTransportTimeline || NvencInterpolationCore.Enabled(state)) SharedPipeTimelineCore.AttachTransportSource(mainStage,ffprobe,encodeInput,info,hevcArgs);

                        if((fieldWorkfile && !mergedFields) || separateDeint || hdrOffSdr)SharedHdrPreprocessCore.AttachFieldTimeline(mainStage,ffprobe,encodeInput,info,hdrToSdr,!hdrOffSdr);
                        if(inlineVulkanPlate) {
                            SharedHdrPreprocessCore.AttachInlineFieldTimeline(mainStage,ffprobe,input,fps);
                            mainStage.StageText += " / inline Vulkan BWDIF + Plate (no FFV1)";
                        }

                        if(mergedFrucHdr)
                            SharedHdrPreprocessCore.AttachMergedFruc(mainStage,input,info,tonemap,caps.GpuName,settings:toneSettings);
                        else if (mergedHdr)
                        {
                            mainStage.StageText = (h264?"H.264":"HEVC")+"：HDR 合并 / 调色 / LUT / 颗粒 / 编码";
                            if(mergedFields)SharedHdrPreprocessCore.AttachMergedEncode(mainStage,ffprobe,input,info,tonemap,caps.GpuName,fieldFilter:fieldFilter,settings:toneSettings);
                            else if(!grainEnabled && !resize.Enabled)SharedHdrPreprocessCore.AttachMergedHevc(mainStage,ffprobe,input,info,tonemap,caps.GpuName,settings:toneSettings);
                            else SharedHdrPreprocessCore.AttachMergedEncode(mainStage,ffprobe,input,info,tonemap,caps.GpuName,settings:toneSettings);
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
            HdrColorProcessingCore.Notice(request,info,state,hdrPreserve);
            if (s4GpuRequested && filmBorder != null && !s4GpuBorder && !expand)
                request.ExecutionNotice = (request.ExecutionNotice ?? "") +
                    " [S4 GPU border unavailable for this configuration: validated CPU border retained.]";
            prepared = new BridgePreparedExecution(request, false, ffmpeg, requestInputs);

                lutCompatPath = "";

                subtitleAss = "";
                subtitleFilterPath="";
                if(filmBorder!=null)filmBorder.ExpandTexture=null;

                return true;

            }

            finally

            {

                if (!string.IsNullOrEmpty(lutCompatPath)) TryDelete(lutCompatPath);

                if (!string.IsNullOrEmpty(subtitleAss)) TryDelete(subtitleAss);
                if(!string.IsNullOrEmpty(subtitleFilterPath))TryDelete(subtitleFilterPath);
                if(filmBorder!=null)TryDelete(filmBorder.ExpandTexture);

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

        // The existing CPU LUT applies before Vulkan Grain Plate blending. Keep the
        // same LUT algorithm/opacity but normalize cadence and SDR tags before upload
        // so S4 can complete the film border without downloading the blended frame.
        internal static string BuildSdrPlateLutGpuBaseFilter(string deinterlaceFilter, string fpsFilter,
            string colorFilter, string lutName, string opacity, string fps, string colorSignal)
        {
            return "[0:v:0]" + deinterlaceFilter + fpsFilter + "fps=" + fps + "," + colorFilter +
                "format=gbrp16le,setpts=PTS-STARTPTS,split=2[lutorig][lutsrc];[lutsrc]lut3d=file='" +
                EscapeFilterPath(lutName) + "':interp=tetrahedral[lutgraded];[lutgraded][lutorig]" +
                "blend=all_mode=normal:all_opacity=" + opacity + ",format=p010le," +
                colorSignal + "hwupload[basevk]";
        }

        private static List<string> BuildHevcArgs(string input, string grainInput, string filter, string output, string preset, bool highMotion, long bitrate, long max, long buf, string fps, string duration, bool plateMode, bool mkv, HardwareCapabilitySnapshot caps, IDictionary<string,string> state, MediaProbeInfo hdrInfo, bool fruc, MediaProbeInfo audioInfo, FilmBorderEncodingCore.Plan filmBorder)

        {

            bool h264=Eq(state,"FG_MODE","H264");
            bool expand=filmBorder!=null && filmBorder.ExpandGeometry!=null;
            if(h264 && (filmBorder == null || !filmBorder.GpuH264))filter=filter.Replace("[vout]",",format=yuv420p[vout]");
            List<string> a = new List<string>{"-hide_banner","-stats","-progress","pipe:2","-stats_period","0.5","-y"};

            if (plateMode || filter.Contains("libplacebo=") || filter.Contains("fruc_vulkan=") || (expand && filmBorder.ExpandGpu)) { a.Add("-init_hw_device"); a.Add(VulkanDevicePolicy.Spec("vk") + (filmBorder != null && (filmBorder.GpuRaw != null || filmBorder.ExpandGpu) ? ",disable_multiplane=1" : "")); a.Add("-filter_hw_device"); a.Add("vk"); }

            else if (filter.Contains("bwdif_vulkan=")) { a.Add("-init_hw_device"); a.Add(VulkanDevicePolicy.Spec("deintvk")); a.Add("-filter_hw_device"); a.Add("deintvk"); }

            a.Add("-i"); a.Add(input);

            if (plateMode) { a.Add("-stream_loop"); a.Add("-1"); a.Add("-t"); a.Add(duration); a.Add("-i"); a.Add(grainInput); }

            if (filmBorder != null)
            {
                if(expand)
                {
                    a.Add("-framerate"); a.Add(fps); a.Add("-i"); a.Add(filmBorder.ExpandTexture);
                }
                else if (filmBorder.GpuRaw != null)
                {
                    a.Add("-stream_loop"); a.Add("-1"); a.Add("-f"); a.Add("rawvideo");
                    a.Add("-pixel_format"); a.Add("rgba"); a.Add("-video_size"); a.Add("1920x1080");
                    a.Add("-framerate"); a.Add(fps); a.Add("-i"); a.Add(filmBorder.GpuRaw);
                }
                else
                {
                    if (filmBorder.HdrTransfer == "") { a.Add("-loop"); a.Add("1"); }
                    a.Add("-framerate"); a.Add(fps); a.Add("-i"); a.Add(filmBorder.Path);
                }
            }

            a.Add("-filter_complex"); a.Add(filter);

            a.Add("-map"); a.Add("[vout]"); a.Add("-map"); a.Add("0:a?");

            if (mkv) { a.Add("-map"); a.Add("0:s?"); a.Add("-map"); a.Add("0:t?"); }

            a.Add("-map_metadata"); a.Add("0"); a.Add("-map_chapters"); a.Add("0");

            a.Add("-c:v"); a.Add(h264?"h264_nvenc":"hevc_nvenc");
            // CUDA hardware frames preserve the verified Vulkan->CUDA path; no CPU pix_fmt conversion.
            if (filmBorder == null || (filmBorder.GpuRaw == null && !filmBorder.ExpandGpu))
            { a.Add("-pix_fmt"); a.Add(h264?"yuv420p":"p010le"); }
            a.Add("-gpu"); a.Add("0"); a.Add("-profile:v"); a.Add(h264?"high":"main10");

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

            // FGB-02 SDR frames leave Vulkan as P010 Main10; signal BT.709 explicitly.
            if (filmBorder != null && filmBorder.GpuSdr)
            { a.Add("-color_range"); a.Add("tv"); a.Add("-color_primaries"); a.Add("bt709");
              a.Add("-color_trc"); a.Add("bt709"); a.Add("-colorspace"); a.Add("bt709"); }

            // fruc_vulkan already emits frames with the requested target rate and timestamps.

            // Match Native x264: do not apply a second output-rate conversion after FRUC.

            if (!fruc && !expand) { a.Add("-r"); a.Add(fps); }

            a.Add("-fps_mode:v"); a.Add(fruc || expand ? "passthrough" : "cfr");
            if(expand)a.AddRange(new[]{"-enc_time_base:v","filter"});

            if (plateMode || (filmBorder != null && filmBorder.GpuRaw != null)) { a.Add("-t"); a.Add(duration); }

            if (mkv) { a.Add("-c:a"); a.Add("copy"); a.Add("-c:s"); a.Add("copy"); a.Add("-c:t"); a.Add("copy"); }

            else { a.Add("-c:a"); a.Add("aac"); a.Add("-b:a"); a.Add("256k"); if(!h264){a.Add("-tag:v"); a.Add("hvc1");} Mp4FaststartCore.Add(a,state); }

            NvencRateControlCore.Apply(a,state);
            NvencMultipassCore.Apply(a,state,h264?"H264":"HEVC",caps);
            NvencMainOptionsCore.Apply(a,state,h264?"H264":"HEVC",caps);
            a.Add(output);

            AudioOutputCore.Apply(a,state,audioInfo,mkv,false,false);
            return a;

        }



        private static readonly Dictionary<string,bool> hostImageCopyOptionCache =
            new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);

        private static bool SupportsHostImageCopyOption(string ffmpeg)
        {
            // Run only for S4 GPU borders; cache by executable stamp and selected GPU.
            string key = HardwareCapabilityCore.ToolStamp(ffmpeg) + "|" + VulkanDevicePolicy.DeviceName;
            bool supported;
            lock (hostImageCopyOptionCache)
                if (hostImageCopyOptionCache.TryGetValue(key, out supported)) return supported;

            string output, reason;
            string device = VulkanDevicePolicy.Spec("vk") + ",disable_multiplane=1,disable_host_image_copy=1";
            supported = SharedFrucCore.Run(ffmpeg,
                "-hide_banner -nostdin -loglevel error -init_hw_device " + Quote(device) +
                " -f lavfi -i color=c=black:s=16x16:r=1 -frames:v 1 -f null NUL",
                10000, out output, out reason);
            lock (hostImageCopyOptionCache) hostImageCopyOptionCache[key] = supported;
            return supported;
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

            if (state.ContainsKey(ColorGradeCore.OriginalLutKey)) return ColorGradeCore.OutputSuffix(state[ColorGrade.StateKey]);
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

        private static int NormalizeLutStrength(string value) { int v; if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return 75; return Math.Max(0,Math.Min(100,v)); }

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



namespace FilmGrainStudioPreview
{
    // S2: common FFmpeg filter-graph integration for all native HEVC/H264 invocations.
    // No UI rendering or separate video baking. OFF leaves the existing graph unchanged.
    // Normalized focus/zoom shared by the GUI, CLI and all FFmpeg encoding routes.
    internal static class FilmBorderFramingCore
    {
        internal const string FocusXKey="FILM_BORDER_FOCUS_X",FocusYKey="FILM_BORDER_FOCUS_Y",ZoomKey="FILM_BORDER_ZOOM";
        internal static double Parse(string input,double fallback,double min,double max)
        {
            double value;
            if(!double.TryParse(input,NumberStyles.Float,CultureInfo.InvariantCulture,out value) ||
               double.IsNaN(value) || double.IsInfinity(value)) return fallback;
            return Math.Max(min,Math.Min(max,value));
        }
        internal static string N(double value) {return value.ToString("0.##########",CultureInfo.InvariantCulture);}
        internal static bool Neutral(double x,double y,double zoom)
        {return Math.Abs(x-0.5)<1e-9 && Math.Abs(y-0.5)<1e-9 && Math.Abs(zoom-1)<1e-9;}
        internal static string CpuScaleCrop(int apertureW,int apertureH,double x,double y,double zoom)
        {
            string w=apertureW.ToString(CultureInfo.InvariantCulture),h=apertureH.ToString(CultureInfo.InvariantCulture);
            // The original S4 filter graph is preserved verbatim for neutral framing.
            if(Neutral(x,y,zoom))
                return "scale="+w+":"+h+":force_original_aspect_ratio=increase:flags=lanczos,crop="+w+":"+h;
            int scaledW=(int)Math.Ceiling(apertureW*zoom/2.0)*2;
            int scaledH=(int)Math.Ceiling(apertureH*zoom/2.0)*2;
            return "scale="+scaledW+":"+scaledH+":force_original_aspect_ratio=increase:flags=lanczos"+
                ",crop="+w+":"+h+":x='max(0,min(iw-ow,"+N(x)+"*iw-ow/2))'"+
                ":y='max(0,min(ih-oh,"+N(y)+"*ih-oh/2))'";
        }
    }

    internal static class FilmBorderEncodingCore
    {
        internal sealed class Plan
        {
            internal string Path;
            internal double FocusX=0.5,FocusY=0.5,Zoom=1;
            internal bool Dynamic;
            internal bool Classic;
            internal bool User;
            internal int ApertureWidth1080=1680,ApertureHeight1080;
            internal string FileSuffix = "";
            internal string MoveX=FilmBorderMotionCore.MoveX,MoveY=FilmBorderMotionCore.MoveY;
            internal string HdrTransfer = "";
            internal bool GpuSdr;
            internal bool GpuH264;
            internal string GpuRaw;
            internal bool GpuUseLegacy4k;
            internal bool GpuClockGated;
            internal int GpuInputIndex;
            internal int GpuWidth, GpuHeight, GpuContentX, GpuContentY, GpuContentWidth, GpuContentHeight;
            internal int GpuCropX, GpuCropY;
            internal bool GpuSquareOutput;
            internal int ExpandThickness,ExpandSarNum=1,ExpandSarDen=1;
            internal FilmBorderExpandGeometry ExpandGeometry;
            internal bool ExpandGpu;
            internal string ExpandTexture;
        }

        private static string Read(IDictionary<string,string> state,string key)
        {
            string value;
            return state != null && state.TryGetValue(key,out value) ? (value ?? "") : "";
        }

        // Imported names are readable; GUIDs remain internal identifiers.
        internal static string OutputFileSuffix(string styleId,bool dynamic,string userName)
        {
            if (string.IsNullOrEmpty(styleId) || styleId=="OFF") return "";
            string motion=dynamic?"D":"S";
            if (styleId=="DARK") return "_FILM35"+motion;
            if (styleId=="CLASSIC") return "_FILMCLASSIC35"+motion;
            if (styleId.StartsWith("USER_",StringComparison.Ordinal) && styleId.Length==37 &&
                System.Text.RegularExpressions.Regex.IsMatch(styleId.Substring(5),@"\A[0-9A-F]{32}\z"))
            {
                string token=FilmBorderCatalogCore.SafeUserFileToken(userName);
                if(token.Length==0)
                    throw new InvalidDataException("自定义边框缺少有效名称，请重新导入。");
                return "_FILMUSER_"+token+"_"+motion;
            }
            throw new InvalidDataException("Unknown film border filename style: "+styleId);
        }

        internal static bool TryCreate(string appRoot,MediaProbeInfo media,IDictionary<string,string> state,
            bool interpolating,bool hdrPreserve,out Plan plan,out string reason,bool allowInterpolatedGpu=false)
        {
            plan=null;reason="";
            string value=Read(state,"FG_FILM_BORDER_MODE").Trim().ToUpperInvariant();
            if(value.Length==0 || value=="OFF") return true;
            FilmBorderChoice chosen;
            if(!FilmBorderCatalogCore.TryResolve(appRoot,value,out chosen,out reason))return false;
            if(chosen.User)
            {
                try {FilmBorderCatalogCore.ValidateUniqueDisplayName(appRoot,chosen.UserName,chosen.StyleId);}
                catch(InvalidDataException ex) {reason=ex.Message;return false;}
            }
            if (media.IsHdr && !hdrPreserve)
            { reason="HDR film border currently requires keeping HDR in HEVC NVENC."; return false; }
            if (hdrPreserve &&
                ((media.ColorTransfer!="smpte2084" && media.ColorTransfer!="arib-std-b67") ||
                 media.ColorPrimaries!="bt2020" || media.ColorSpace!="bt2020nc" || media.ColorRange!="tv"))
            { reason="HDR film border requires limited-range BT.2020 PQ or HLG."; return false; }
            if(interpolating && !allowInterpolatedGpu)
            {reason="S2 film border with interpolation is not yet validated (S3/S4).";return false;}
            double amp=FilmBorderMotionCore.Parse(Read(state,"FG_FILM_BORDER_AMP"),0,3);
            double speed=FilmBorderMotionCore.Parse(Read(state,"FG_FILM_BORDER_SPEED"),0.25,3);
            double frequency=FilmBorderMotionCore.Parse(Read(state,"FG_FILM_BORDER_FREQUENCY"),0.25,3);
            plan=new Plan{Path=chosen.PngPath,Dynamic=chosen.Weave,Classic=chosen.StyleId=="CLASSIC" || chosen.User,User=chosen.User,
                ApertureWidth1080=chosen.ApertureWidth1080,ApertureHeight1080=chosen.ApertureHeight1080,
                FileSuffix=OutputFileSuffix(chosen.StyleId,chosen.Weave,chosen.UserName),HdrTransfer=hdrPreserve?media.ColorTransfer:"",
                FocusX=FilmBorderFramingCore.Parse(Read(state,"FG_"+FilmBorderFramingCore.FocusXKey),0.5,0,1),
                FocusY=FilmBorderFramingCore.Parse(Read(state,"FG_"+FilmBorderFramingCore.FocusYKey),0.5,0,1),
                Zoom=FilmBorderFramingCore.Parse(Read(state,"FG_"+FilmBorderFramingCore.ZoomKey),1,1,3),
                GpuRaw=chosen.RawPath,
                MoveX=FilmBorderMotionCore.Expression(true,amp,speed,frequency),
                MoveY=FilmBorderMotionCore.Expression(false,amp,speed,frequency)};
            int thickness;
            if(!FilmBorderExpandCanvasCore.TryParseThickness(Read(state,"FG_"+FilmBorderExpandCanvasCore.ThicknessKey),out thickness))
            { plan=null;reason="Expand Canvas border thickness must be an even number from 2 to 512 px (0 = Overlay).";return false; }
            plan.ExpandThickness=thickness;
            if(thickness>0)
            {
                // Initial scope is deliberate. Existing Overlay routes are untouched.
                if(interpolating || media.IsInterlaced)
                { plan=null;reason="Expand Canvas candidate requires progressive video without interpolation.";return false; }
                if(!hdrPreserve && ((!String.IsNullOrEmpty(media.ColorSpace) && media.ColorSpace!="unknown" && media.ColorSpace!="bt709") ||
                    (!String.IsNullOrEmpty(media.ColorTransfer) && media.ColorTransfer!="unknown" && media.ColorTransfer!="bt709") ||
                    (!String.IsNullOrEmpty(media.ColorPrimaries) && media.ColorPrimaries!="unknown" && media.ColorPrimaries!="bt709") ||
                    (!String.IsNullOrEmpty(media.ColorRange) && media.ColorRange!="unknown" && media.ColorRange!="tv")))
                { plan=null;reason="Expand Canvas candidate requires BT.709 limited SDR or existing PQ/HLG HDR preserve.";return false; }
                plan.FileSuffix+="_EXPNAT";
                plan.GpuRaw=null;
            }
            return true;
        }

        internal static bool ConfigureExpand(Plan plan,CinematicFramePlan frame,ResizePlan resize,MediaProbeInfo media,string codec,out string reason)
        {
            reason="";
            if(plan==null || plan.ExpandThickness==0)return true;
            if(frame.CinematicBars)
            { reason="Expand Canvas does not support the separate Cinematic bars option.";return false; }
            if(!ValidDimensions(frame.ActiveWidth,frame.ActiveHeight))
            { reason="Expand Canvas requires even picture dimensions in the range 32..16384.";return false; }
            try
            {
                plan.ExpandGeometry=FilmBorderExpandCanvasCore.NaturalGeometry(frame.ActiveWidth,frame.ActiveHeight,
                    ApertureWidth(plan),ApertureHeight(plan));
                int max=codec=="H264"?4096:codec=="HEVC"?8192:16384;
                if(plan.ExpandGeometry.CanvasWidth>max || plan.ExpandGeometry.CanvasHeight>max)
                { reason="Expand Canvas exceeds codec dimension limit "+N(max)+".";return false; }
                ExpandPictureSar(media,resize,out plan.ExpandSarNum,out plan.ExpandSarDen);
                return true;
            }
            catch(ArgumentException e){reason=e.Message;return false;}
        }

        internal static void ExpandPictureSar(MediaProbeInfo media,ResizePlan resize,out int numerator,out int denominator)
        {
            numerator=denominator=1;
            if(resize.Enabled)return; // ResizeCore explicitly outputs square pixels.
            string[] sar=(media.SampleAspectRatio??"").Split(':');
            int a,b;
            if(sar.Length==2 && Int32.TryParse(sar[0],out a) && Int32.TryParse(sar[1],out b) && a>0 && b>0)
            { numerator=a;denominator=b; }
        }

        internal static string ExpandNotice(Plan plan)
        {
            var geometry=plan.ExpandGeometry;
            return "[Expand Canvas] "+geometry.Width+"x"+geometry.Height+
                " -> "+geometry.CanvasWidth+"x"+geometry.CanvasHeight+
                " SAR="+plan.ExpandSarNum+":"+plan.ExpandSarDen+
                " offset="+geometry.Left+","+geometry.Top+
                (plan.ExpandGpu?" / Vulkan region copy -> CUDA":" / CPU region copy");
        }

        internal static void StageExpandTexture(Plan plan,string directory,List<string> temporaryFiles)
        {
            if(plan==null || plan.ExpandGeometry==null)return;
            plan.ExpandTexture=FilmBorderExpandCanvasCore.WriteTexture(plan.Path,ApertureWidth(plan),ApertureHeight(plan),plan.ExpandGeometry,directory);
            temporaryFiles.Add(plan.ExpandTexture);
        }

        internal static int FinalWidth(Plan plan,int width)
        {return plan!=null && plan.ExpandGeometry!=null?plan.ExpandGeometry.CanvasWidth:width;}
        internal static int FinalHeight(Plan plan,int height)
        {return plan!=null && plan.ExpandGeometry!=null?plan.ExpandGeometry.CanvasHeight:height;}

        internal static string ExpandSignal(Plan plan)
        {return String.IsNullOrEmpty(plan.HdrTransfer)?"bt709":plan.HdrTransfer;}

        // The generated black canvas is the main overlay input and has no source color tags.
        private static string RestoreColorTags(MediaProbeInfo media)
        {
            var tags = new List<string>();
            AddColorTag(tags, "color_primaries", media.ColorPrimaries);
            AddColorTag(tags, "color_trc", media.ColorTransfer);
            AddColorTag(tags, "colorspace", media.ColorSpace);
            return tags.Count == 0 ? "" : ",setparams=" + string.Join(":", tags);
        }

        private static void AddColorTag(List<string> tags, string key, string value)
        {
            if (string.IsNullOrEmpty(value) || value == "unknown" || value == "unspecified") return;
            if (System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[a-z0-9-]+\z"))
                tags.Add(key + "=" + value);
        }

        private static int EvenDimension(long value)
        {
            if(value<2)value=2;
            if(value>32766)value=32766;
            return (int)(value/2*2);
        }
        private static string N(int n) {return n.ToString(CultureInfo.InvariantCulture);}

        // R3 accepted discrete gate registration: hash changes at integer time slots,
        // not smooth sinusoids. The sin() calls are deterministic hash functions only.
        private const string MoveX = FilmBorderMotionCore.MoveX;
        private const string MoveY = FilmBorderMotionCore.MoveY;

        private static int ApertureWidth(Plan plan)
        {return plan.ApertureWidth1080>0?plan.ApertureWidth1080:1680;}
        private static int ApertureHeight(Plan plan)
        {return plan.ApertureHeight1080>0?plan.ApertureHeight1080:
            (plan.Classic?FilmBorderCatalogCore.ClassicApertureHeight1080:990);}

        internal static bool ValidDimensions(int width,int height)
        {
            return width>=32 && height>=32 && width<=16384 && height<=16384 && ((width|height)&1)==0;
        }

        internal static void ConfigureGpuGeometry(Plan plan,CinematicFramePlan frame,bool geometryChanged,bool cropAfterGrain)
        {
            plan.GpuWidth=frame.ActiveWidth;
            plan.GpuHeight=frame.ActiveHeight;
            plan.GpuSquareOutput=geometryChanged;
            if (geometryChanged && !frame.Filter.StartsWith(",drawbox=",StringComparison.Ordinal))
            {
                plan.GpuCropX=cropAfterGrain?frame.ContentX:0;
                plan.GpuCropY=cropAfterGrain?frame.ContentY:0;
                plan.GpuContentX=plan.GpuContentY=0;
                plan.GpuContentWidth=frame.ActiveWidth;
                plan.GpuContentHeight=frame.ActiveHeight;
                return;
            }
            plan.GpuContentX=frame.ContentX;
            plan.GpuContentY=frame.ContentY;
            plan.GpuContentWidth=frame.ContentWidth;
            plan.GpuContentHeight=frame.ContentHeight;
        }

        // Final filter stage: existing color, frame, LUT, subtitles and grain complete first.
        // Dimensions are normalized from the R3 1920x1080 aperture; SAR remains square.
        internal static string AppendFilter(string original,Plan plan,int width,int height,int filmInputIndex,string fps,MediaProbeInfo media,string outputFormat="p010le")
        {
            if(plan==null) return original;
            if(!ValidDimensions(width,height))
                throw new InvalidOperationException("Film border requires even output dimensions in the range 32..16384.");
            int at=original.LastIndexOf("[vout]",StringComparison.Ordinal);
            if(at<0) throw new InvalidOperationException("Existing filter graph has no final [vout] label.");
            if(plan.ExpandGeometry!=null)
            {
                string expandGraph=original.Substring(0,at)+"[fgs_expand_input]"+original.Substring(at+6);
                bool eight=outputFormat=="yuv420p";
                string pixel=eight?"yuv420p":"yuv420p10le";
                string transfer=ExpandSignal(plan);
                string texture=FilmBorderExpandCanvasCore.TextureFilter(transfer,false,false);
                string cpu=FilmBorderExpandCanvasCore.CpuGraph(plan.ExpandGeometry,plan.ExpandSarNum,plan.ExpandSarDen,transfer,false,
                    plan.Dynamic?plan.MoveX:"",plan.Dynamic?plan.MoveY:"");
                if(eight){texture=texture.Replace("yuv420p10le","yuv420p");
                    cpu=cpu.Replace("yuv420p10le","yuv420p").Replace("format=yuv420p10,","format=yuv420,");}
                return expandGraph+";[fgs_expand_input]format="+pixel+"[fgsfilm_src];["+N(filmInputIndex)+":v:0]"+
                    texture+"[fgstexture];"+cpu.Replace("[vout]",",format="+outputFormat+"[vout]");
            }
            int apertureWidth=EvenDimension(((long)width*ApertureWidth(plan)+960)/1920);
            int apertureHeight=EvenDimension(((long)height*ApertureHeight(plan)+540)/1080);
            if(apertureWidth>width)apertureWidth=width;
            if(apertureHeight>height)apertureHeight=height;
            int x=(width-apertureWidth)/2,y=(height-apertureHeight)/2;
            string graph=original.Substring(0,at)+"[fgsfilm_src]"+original.Substring(at+6);
            graph+=";[fgsfilm_src]"+FilmBorderFramingCore.CpuScaleCrop(apertureWidth,apertureHeight,
                 plan.FocusX,plan.FocusY,plan.Zoom)+
                   ",setsar=1,pad="+N(width)+":"+N(height)+":"+N(x)+":"+N(y)+":color=black[fgsfilm_canvas]";
            if (plan.HdrTransfer != "")
                return AppendHdrTexture(graph,plan,filmInputIndex,width,height,outputFormat);
            graph+=";["+N(filmInputIndex)+":v:0]format=rgba,scale="+N(width)+":"+N(height)+
                   ":flags=lanczos[fgsfilm_png]";
            graph+=";[fgsfilm_canvas][fgsfilm_png]overlay=x=0:y=0:shortest=1:format=auto[fgsfilm_joined]";
            if(plan.Dynamic)
            {
                // R3 moved the entire composed frame over a black projection gate.
                // The overlay on generated black must end when the actual clip ends.
                graph+=";color=c=black:s="+N(width)+"x"+N(height)+":r="+fps+"[fgsfilm_black]";
                graph+=";[fgsfilm_black][fgsfilm_joined]overlay=x='"+plan.MoveX+"':y='"+plan.MoveY+
                       "':shortest=1:eval=frame:format=auto,setsar=1,format="+outputFormat + RestoreColorTags(media) + "[vout]";
            }
            else graph+=";[fgsfilm_joined]setsar=1,format="+outputFormat+"[vout]";
            return graph;
        }

        internal static string ExpandGpuPicture(string prefix,CinematicFramePlan frame,string signal)
        {
            return "[0:v:0]"+prefix+"format=p010le"+frame.Filter+","+signal+"hwupload[fgsfilm_gpu_video]";
        }

        internal static string AppendGpuFilter(string graph,Plan plan)
        {
            if(plan!=null && plan.ExpandGeometry!=null)
            {
                string transfer=ExpandSignal(plan);
                string expandTexture=FilmBorderExpandCanvasCore.TextureFilter(transfer,false,true);
                string expandKernel=FilmBorderExpandCanvasCore.GpuGraph(plan.ExpandGeometry,plan.ExpandSarNum,plan.ExpandSarDen,transfer,false,
                    plan.Dynamic?plan.MoveX:"",plan.Dynamic?plan.MoveY:"",true);
                if(plan.GpuH264)expandKernel=expandKernel.Replace("[vout]",",scale_cuda=format=nv12[vout]");
                return graph+";["+N(plan.GpuInputIndex)+":v:0]"+expandTexture+"[fgstexture];"+expandKernel;
            }
            if (plan == null || plan.GpuRaw == null || (plan.HdrTransfer == "" && !plan.GpuSdr))
                throw new InvalidOperationException("S4 GPU border called outside its validated route.");
            string moveX = plan.Dynamic ? plan.MoveX : "0", moveY = plan.Dynamic ? plan.MoveY : "0";
            // HLG GPU output: compose into PQ first, then let a second GPU libplacebo
            // convert the actual pixels back to HLG before CUDA/NVENC. PQ is unchanged.
            bool splitHlg = plan.HdrTransfer == "arib-std-b67";
            string gpuComposeTransfer = plan.GpuSdr ? "bt709" : splitHlg ? "smpte2084" : plan.HdrTransfer;
            string gpuOutputTransfer = plan.GpuSdr ? "bt709" : plan.HdrTransfer;
            string gpuColorSpace = plan.GpuSdr ? "bt709" : "bt2020nc";
            string gpuPrimaries = plan.GpuSdr ? "bt709" : "bt2020";
            string gpuCudaFormat = plan.GpuH264 ? ",scale_cuda=format=nv12" : "";
            string gpuHlgFinish = splitHlg
                ? ",libplacebo=inputs=1:w="+N(plan.GpuWidth)+":h="+N(plan.GpuHeight)+
                  ":format=p010le:colorspace=bt2020nc:color_primaries=bt2020:"+
                  "color_trc=arib-std-b67:range=limited:tonemapping=clip:peak_detect=false:dithering=none"
                : "";
            // For no-grain graphs, clock the still texture with the video.  The
            // off-screen overlay keeps every RGBA pixel intact and ends at video EOF.
            string texture = "["+N(plan.GpuInputIndex)+":v:0]format=rgba";
            string input = plan.GpuClockGated
                ? "[fgsfilm_clock_src]scale=2:2,format=rgba[fgsfilm_clock];"+texture+
                  "[fgsfilm_texture];[fgsfilm_texture][fgsfilm_clock]overlay=x=main_w:shortest=1:format=rgb,format=rgba"
                : texture;
            input += ",setparams=range=full:color_primaries=bt709:"+
                     "color_trc=iec61966-2-1:colorspace=gbr,hwupload[fgsfilm_gpu_png]";
            if (!plan.GpuUseLegacy4k)
            {
                int w=plan.GpuWidth, h=plan.GpuHeight;
                int apertureW=EvenDimension(((long)w*ApertureWidth(plan)+960)/1920);
                int apertureH=EvenDimension(((long)h*ApertureHeight(plan)+540)/1080);
                apertureW=Math.Min(apertureW,w); apertureH=Math.Min(apertureH,h);
                double scale=Math.Max((double)apertureW/w,(double)apertureH/h)*plan.Zoom;
                 double cropWidth=apertureW/scale,cropHeight=apertureH/scale;
                 double cropLeft=Math.Max(0,Math.Min(w-cropWidth,plan.FocusX*w-cropWidth/2.0));
                 double cropTop=Math.Max(0,Math.Min(h-cropHeight,plan.FocusY*h-cropHeight/2.0));
                double left=Math.Max(cropLeft,plan.GpuContentX), top=Math.Max(cropTop,plan.GpuContentY);
                double right=Math.Min(cropLeft+apertureW/scale,plan.GpuContentX+plan.GpuContentWidth);
                double bottom=Math.Min(cropTop+apertureH/scale,plan.GpuContentY+plan.GpuContentHeight);
                if (right<=left || bottom<=top) throw new InvalidOperationException("GPU film border has no visible picture area.");
                Func<double,string> f=delegate(double value) { return value.ToString("0.##########",CultureInfo.InvariantCulture); };
                string kernelDynamic = "libplacebo=inputs=2:w="+N(w)+":h="+N(h)+":format=p010le:"+
                    "colorspace="+gpuColorSpace+":color_primaries="+gpuPrimaries+":color_trc="+gpuComposeTransfer+":range=limited"+
                    ":crop_x='if(eq(idx,0),"+f(left+plan.GpuCropX)+",0)'"+
                    ":crop_y='if(eq(idx,0),"+f(top+plan.GpuCropY)+",0)'"+
                    ":crop_w='if(eq(idx,0),"+f(right-left)+",iw)'"+
                    ":crop_h='if(eq(idx,0),"+f(bottom-top)+",ih)'"+
                    ":pos_w='if(eq(idx,0),"+f((right-left)*scale)+","+N(w)+")'"+
                    ":pos_h='if(eq(idx,0),"+f((bottom-top)*scale)+","+N(h)+")'"+
                    ":pos_x='if(eq(idx,0),"+f((w-apertureW)/2+(left-cropLeft)*scale)+",0)+("+moveX+")'"+
                    ":pos_y='if(eq(idx,0),"+f((h-apertureH)/2+(top-cropTop)*scale)+",0)+("+moveY+")'"+
                    ":upscaler=ewa_lanczos:downscaler=ewa_lanczos:tonemapping=clip:peak_detect=false:dithering=none:fillcolor=black";
                return graph+";"+input+";[fgsfilm_gpu_video][fgsfilm_gpu_png]"+kernelDynamic+gpuHlgFinish+
                    (plan.GpuSquareOutput?",setsar=1":"")+
                    ",hwupload=derive_device=cuda"+gpuCudaFormat+",setparams=range=limited:color_primaries="+gpuPrimaries+":"+
                    "color_trc="+gpuOutputTransfer+":colorspace="+gpuColorSpace+"[vout]";
            }
            string kernel = "libplacebo=inputs=2:w=3840:h=2160:format=p010le:"+
                            "colorspace=bt2020nc:color_primaries=bt2020:color_trc=" + gpuComposeTransfer + ":range=limited";
            kernel += ":crop_x='if(eq(idx,0),87.2727272727,0)'"+
                      ":crop_y='if(eq(idx,0),276,0)'"+
                      ":crop_w='if(eq(idx,0),3665.4545454545,iw)'"+
                      ":crop_h='if(eq(idx,0),1608,ih)'"+
                      ":pos_w='if(eq(idx,0),3360,3840)'"+
                      ":pos_h='if(eq(idx,0),1474,2160)'"+
                      ":pos_x='if(eq(idx,0),240,0)+(" + moveX + ")'"+
                      ":pos_y='if(eq(idx,0),343,0)+(" + moveY + ")'"+
                      ":upscaler=ewa_lanczos:downscaler=ewa_lanczos:"+
                      "tonemapping=clip:peak_detect=false:dithering=none:fillcolor=black";
            return graph + ";" + input + ";[fgsfilm_gpu_video][fgsfilm_gpu_png]" + kernel + gpuHlgFinish +
                ",hwupload=derive_device=cuda,setparams=range=limited:color_primaries=bt2020:"+
                "color_trc=" + plan.HdrTransfer + ":colorspace=bt2020nc[vout]";
        }

        private static string AppendHdrTexture(string graph,Plan plan,int input,int width,int height,string outputFormat)
        {
            // Resize and convert the PNG once. Framesync holds it until the source ends.
            graph+=";["+N(input)+":v:0]format=gbrap16le,zscale=w="+N(width)+":h="+N(height)+
                   ":matrixin=gbr:primariesin=709:transferin=iec61966-2-1:rangein=full"+
                   ":matrix=2020_ncl:primaries=2020:transfer="+plan.HdrTransfer+
                   ":range=limited:npl=100:threads=1,format=yuva420p10le[fgsfilm_png]";
            graph+=";[fgsfilm_canvas][fgsfilm_png]overlay=eof_action=repeat:format=yuv420p10";
            if (plan.Dynamic)
            {
                // Keep the source-derived frame as the main frame so HDR10 side data survives.
                graph+=",pad="+N(width+8)+":"+N(height+8)+":4:4:black,crop="+N(width)+":"+N(height)+
                       ":x='4-("+plan.MoveX+")':y='4-("+plan.MoveY+")'";
            }
            return graph+",setsar=1,format="+outputFormat+",setparams=range=limited:color_primaries=bt2020"+
                   ":color_trc="+plan.HdrTransfer+":colorspace=bt2020nc[vout]";
        }
    }
}
