using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FilmGrainStudioPreview
{
    // Phase 3.5.3 boundary:
    // This snapshot contains only normalized state needed to build the Bridge environment.
    // It intentionally has no WinForms, language-pack, config, or media-probe dependencies.
    internal sealed class BridgeEnvironmentSnapshot
    {
        public bool NoReencode;

        public string TempMode = "";
        public string TempCustomDir = "";
        public string OutputMode = "";
        public string OutputCustomDir = "";
        public string Container = "MP4";

        public string Mode = "";
        public string Speed = "";
        public int SfeEngines;
        public string BitrateMode = "";
        public long Bitrate;
        public bool HighMotion;
        public string FpsMode = "";
        public bool SvpInterpolate;
        public string SvpAlgo = "";
        public string SvpAnalyse = "";
        public string SvpSceneMode = "";
        public string SvpMaskArea = "";
        public string Deinterlace = "";
        public string DeintMethod = "";
        public bool CinematicFrame;
        public string FrameMode = "";
        public string CropPerSide = "";
        public bool H264High10;
        public string X264Preset = "";
        public string X264PassMode = "";
        public string HevcSpatialAq = "";
        public bool HevcTemporalAq;
        public string HdrPolicy = "";
        public string TonemapAlgo = "";

        public bool UploadEnabled;
        public string UploadBitrateMode = "";
        public long UploadBitrate;

        public bool ColorEnabled;
        public double ColorContrast;
        public double ColorBrightness;
        public double ColorSaturation;
        public double ColorGamma;
        public bool ColorBlackWhite;
        public bool LutEnabled;
        public string LutPath = "";
        public int LutStrength;

        public bool Av1;
        public int GrainMode;
        public int Av1FormatIndex;
        public int Av1StockIndex;
        public int Av1IsoValue;
        public bool Av1ChromaEnabled;
        public string Av1GrainTable = "";
        public int ProceduralStrength;
        public int FgsimPresetIndex;
        public string FgsimQuality = "VBR";
        public string GrainRoot = "";
        public string GrainPlatePath = "";
        public int ScannedGrainStrengthIndex;

        public SubtitleState Subtitle = new SubtitleState();
        public string SourceGrainAction = "";
    }

    internal static class BridgeEnvironmentCore
    {
        internal static SortedDictionary<string, string> Build(BridgeEnvironmentSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            return snapshot.NoReencode ? BuildNoReencode(snapshot) : BuildEncode(snapshot);
        }

        private static SortedDictionary<string, string> BuildEncode(BridgeEnvironmentSnapshot snapshot)
        {
            long bitrate = snapshot.Bitrate;
            long uploadBitrate = snapshot.UploadBitrate;
            int grainMode = snapshot.GrainMode;
            bool av1 = snapshot.Av1;

            SortedDictionary<string, string> state = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            state["FG_STUDIO_MODE"] = "1";
            state["FG_TOOL_NO_PAUSE"] = "1";
            state["FG_TEMP_MODE"] = snapshot.TempMode;
            state["FG_TEMP_CUSTOM_DIR"] = snapshot.TempCustomDir;
            state["FG_TEMP_SPACE_CONFIRMED"] = "1";
            state["FG_OUTPUT_MODE"] = snapshot.OutputMode;
            state["FG_OUTPUT_CUSTOM_DIR"] = snapshot.OutputCustomDir;
            state["FG_OUTPUT_SPACE_CONFIRMED"] = "1";
            state["FG_KEEP_FAILED"] = "1";
            state["FG_MODE"] = snapshot.Mode;
            state["FG_CONTAINER"] = snapshot.Container;
            state["FG_SPEED"] = snapshot.Speed;
            state["FG_SFE_ENGINES"] = Math.Max(0, snapshot.SfeEngines).ToString(CultureInfo.InvariantCulture);
            state["FG_BITRATE_MODE"] = snapshot.BitrateMode;
            state["FG_BITRATE"] = bitrate.ToString(CultureInfo.InvariantCulture);
            state["FG_MAXRATE"] = (bitrate * 3L).ToString(CultureInfo.InvariantCulture);
            state["FG_BUFSIZE"] = (bitrate * 6L).ToString(CultureInfo.InvariantCulture);
            state["FG_HIGH_MOTION"] = snapshot.HighMotion ? "1" : "0";
            state["FG_FPS_MODE"] = snapshot.FpsMode;
            state["FG_SVP_INTERPOLATE"] = snapshot.SvpInterpolate ? "1" : "0";
            state["FG_SVP_ALGO"] = snapshot.SvpAlgo;
            state["FG_SVP_ANALYSE"] = snapshot.SvpAnalyse;
            state["FG_SVP_SCENE_MODE"] = snapshot.SvpSceneMode;
            state["FG_SVP_MASK_AREA"] = snapshot.SvpMaskArea;
            state["FG_DEINTERLACE"] = snapshot.Deinterlace;
            state["FG_DEINT_METHOD"] = snapshot.DeintMethod;
            state["FG_CINEMATIC_FRAME"] = snapshot.CinematicFrame ? "1" : "0";
            state["FG_FRAME_MODE"] = snapshot.FrameMode;
            state["FG_CROP_PER_SIDE"] = snapshot.CropPerSide;
            state["FG_H264_HIGH10"] = snapshot.H264High10 ? "1" : "0";
            state["FG_X264_PRESET"] = snapshot.X264Preset;
            state["FG_X264_PASS_MODE"] = snapshot.X264PassMode;
            state["FG_HEVC_SPATIAL_AQ"] = snapshot.HevcSpatialAq;
            state["FG_HEVC_TEMPORAL_AQ"] = snapshot.HevcTemporalAq ? "1" : "0";
            state["FG_HDR_POLICY"] = snapshot.HdrPolicy;
            state["FG_TONEMAP_ALGO"] = snapshot.TonemapAlgo;
            state["FG_UPLOAD"] = snapshot.UploadEnabled ? "1" : "0";
            state["FG_UPLOAD_MODE"] = "X264";
            state["FG_UPLOAD_BITRATE_MODE"] = snapshot.UploadBitrateMode;
            state["FG_UPLOAD_BITRATE"] = uploadBitrate.ToString(CultureInfo.InvariantCulture);
            state["FG_UPLOAD_MAXRATE"] = (uploadBitrate * 3L).ToString(CultureInfo.InvariantCulture);
            state["FG_UPLOAD_BUFSIZE"] = (uploadBitrate * 6L).ToString(CultureInfo.InvariantCulture);
            state["FG_UPLOAD_HIGH_MOTION"] = snapshot.HighMotion ? "1" : "0";

            snapshot.Subtitle.WriteEnvironment(state);

            state["FG_COLOR_ENABLED"] = snapshot.ColorEnabled ? "1" : "0";
            state["FG_COLOR_CONTRAST"] = snapshot.ColorContrast.ToString("0.00", CultureInfo.InvariantCulture);
            state["FG_COLOR_BRIGHTNESS"] = snapshot.ColorBrightness.ToString("0.00", CultureInfo.InvariantCulture);
            state["FG_COLOR_SATURATION"] = snapshot.ColorSaturation.ToString("0.00", CultureInfo.InvariantCulture);
            state["FG_COLOR_GAMMA"] = snapshot.ColorGamma.ToString("0.00", CultureInfo.InvariantCulture);
            state["FG_COLOR_BLACK_WHITE"] = snapshot.ColorBlackWhite ? "1" : "0";
            if (snapshot.LutEnabled)
            {
                state["FG_LUT_PATH"] = snapshot.LutPath;
                state["FG_LUT_STRENGTH"] = snapshot.LutStrength.ToString(CultureInfo.InvariantCulture);
            }

            string[] fgsimPresets = new string[] { "LIGHT", "MEDIUM", "HEAVY" };
            if (av1)
            {
                if (grainMode == 3)
                {
                    state["FG_GRAIN_ENGINE"] = "PROCEDURAL";
                    state["FG_PROC_STRENGTH"] = snapshot.ProceduralStrength.ToString(CultureInfo.InvariantCulture);
                }
                else if (grainMode == 4)
                {
                    state["FG_GRAIN_ENGINE"] = "FGSIM";
                    state["FG_FGSIM_PRESET"] = fgsimPresets[Math.Max(0, Math.Min(2, snapshot.FgsimPresetIndex))];
                }
                else
                {
                    string[] av1Modes = new string[] { "PRESET", "ISO", "TABLE" };
                    state["FG_GRAIN_ENGINE"] = "NATIVE";
                    state["FG_AV1_GRAIN_MODE"] = av1Modes[Math.Max(0, Math.Min(2, grainMode))];
                    state["FG_AV1_FORMAT"] = (snapshot.Av1FormatIndex + 1).ToString(CultureInfo.InvariantCulture);
                    state["FG_AV1_STOCK"] = (snapshot.Av1StockIndex + 1).ToString(CultureInfo.InvariantCulture);
                    state["FG_AV1_ISO"] = snapshot.Av1IsoValue.ToString(CultureInfo.InvariantCulture);
                    state["FG_AV1_CHROMA"] = snapshot.Av1ChromaEnabled ? "1" : "0";
                    if (grainMode == 2) state["FG_AV1_GRAIN_TABLE"] = snapshot.Av1GrainTable;
                }
            }
            else
            {
                if (grainMode == 0)
                {
                    state["FG_GRAIN_ENGINE"] = "PROCEDURAL";
                    state["FG_PROC_STRENGTH"] = snapshot.ProceduralStrength.ToString(CultureInfo.InvariantCulture);
                }
                else if (grainMode == 2)
                {
                    state["FG_GRAIN_ENGINE"] = "FGSIM";
                    state["FG_FGSIM_PRESET"] = fgsimPresets[Math.Max(0, Math.Min(2, snapshot.FgsimPresetIndex))];
                    state["FG_FGSIM_QUALITY"] = snapshot.FgsimQuality;
                }
                else
                {
                    state["FG_GRAIN_ENGINE"] = "NATIVE";
                    state["FG_GRAIN_ROOT"] = snapshot.GrainRoot;
                    state["FG_HEVC_GRAIN_PATH"] = snapshot.GrainPlatePath;
                    state["FG_HEVC_GRAIN_TAG"] = MakeGrainTag(snapshot.GrainPlatePath);
                    state["FG_HEVC_STRENGTH_SEL"] = (snapshot.ScannedGrainStrengthIndex + 1).ToString(CultureInfo.InvariantCulture);
                }
            }
            if (!state.ContainsKey("FG_FGSIM_QUALITY")) state["FG_FGSIM_QUALITY"] = "VBR";
            return state;
        }

        private static SortedDictionary<string, string> BuildNoReencode(BridgeEnvironmentSnapshot snapshot)
        {
            int grainMode = snapshot.GrainMode;
            SortedDictionary<string, string> state = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            state["FG_STUDIO_MODE"] = "1";
            state["FG_TOOL_NO_PAUSE"] = "1";
            state["FG_TEMP_MODE"] = snapshot.TempMode;
            state["FG_TEMP_CUSTOM_DIR"] = snapshot.TempCustomDir;
            state["FG_TEMP_SPACE_CONFIRMED"] = "1";
            state["FG_OUTPUT_MODE"] = snapshot.OutputMode;
            state["FG_OUTPUT_CUSTOM_DIR"] = snapshot.OutputCustomDir;
            state["FG_OUTPUT_SPACE_CONFIRMED"] = "1";
            state["FG_CONTAINER"] = snapshot.Container;
            string[] av1Modes = new string[] { "PRESET", "ISO", "TABLE" };
            state["FG_AV1_GRAIN_MODE"] = av1Modes[Math.Max(0, Math.Min(2, grainMode))];
            state["FG_AV1_FORMAT"] = (snapshot.Av1FormatIndex + 1).ToString(CultureInfo.InvariantCulture);
            state["FG_AV1_STOCK"] = (snapshot.Av1StockIndex + 1).ToString(CultureInfo.InvariantCulture);
            state["FG_AV1_ISO"] = snapshot.Av1IsoValue.ToString(CultureInfo.InvariantCulture);
            state["FG_AV1_CHROMA"] = snapshot.Av1ChromaEnabled ? "1" : "0";
            if (grainMode == 2) state["FG_AV1_GRAIN_TABLE"] = snapshot.Av1GrainTable;
            if (!string.IsNullOrEmpty(snapshot.SourceGrainAction)) state["FG_AV1_SOURCE_GRAIN_ACTION"] = snapshot.SourceGrainAction;
            return state;
        }

        private static string MakeGrainTag(string path)
        {
            string stem = Path.GetFileNameWithoutExtension(path) ?? "SCAN";
            StringBuilder sb = new StringBuilder(stem.Length);
            bool underscore = false;
            foreach (char c in stem)
            {
                if (char.IsLetterOrDigit(c)) { sb.Append(c); underscore = false; }
                else if (!underscore && sb.Length > 0) { sb.Append('_'); underscore = true; }
            }
            string tag = sb.ToString().Trim('_');
            if (tag.Length == 0) tag = "SCAN";
            if (tag.Length > 48) tag = tag.Substring(0, 48).TrimEnd('_');
            return tag;
        }
    }
}
