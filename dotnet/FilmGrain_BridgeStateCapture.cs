using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace FilmGrainStudioPreview
{
    // Phase 3.5.3 UI/config adapter:
    // Read and validate MainForm state here, then hand a control-free snapshot to BridgeEnvironmentCore.
    internal sealed partial class MainForm
    {
        private SortedDictionary<string, string> BuildBridgeEnvironment(out string error)
        {
            BridgeEnvironmentSnapshot snapshot = CaptureBridgeEnvironmentSnapshot(out error);
            if (snapshot == null) return null;
            return BridgeEnvironmentCore.Build(snapshot);
        }

        private SortedDictionary<string, string> BuildNoReencodeEnvironment(out string error)
        {
            BridgeEnvironmentSnapshot snapshot = CaptureNoReencodeEnvironmentSnapshot(out error);
            if (snapshot == null) return null;
            return BridgeEnvironmentCore.Build(snapshot);
        }

        private BridgeEnvironmentSnapshot CaptureBridgeEnvironmentSnapshot(out string error)
        {
            error = "";
            if (cmbCodec != null && cmbCodec.SelectedIndex == 3) return CaptureNoReencodeEnvironmentSnapshot(out error);

            long bitrate;
            string bitrateText = IsHevcFgsim() && fgsimRcChoice != "VBR" ? modeBitrate[1] : (cmbBitrate == null ? "" : cmbBitrate.Text.Trim());
            if (!long.TryParse(bitrateText, NumberStyles.Integer, CultureInfo.InvariantCulture, out bitrate) || bitrate <= 10 || bitrate > 500000000)
            {
                error = lang.T("error.bitrate_invalid");
                return null;
            }

            long uploadBitrate = 0;
            bool uploadEnabled = cmbCodec.SelectedIndex != 2 && chkUpload != null && chkUpload.Checked;
            if (uploadEnabled && (cmbUploadBitrate == null || !long.TryParse(cmbUploadBitrate.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uploadBitrate) || uploadBitrate <= 10 || uploadBitrate > 500000000))
            {
                error = lang.T("error.upload_bitrate_invalid");
                return null;
            }

            if (chkLut != null && chkLut.Checked)
            {
                if (string.IsNullOrWhiteSpace(selectedLutPath)) { error = lang.T("info.lut_not_selected"); return null; }
                if (!File.Exists(selectedLutPath)) { error = LF("error.selected_lut_missing", selectedLutPath); return null; }
            }

            string mode = cmbCodec.SelectedIndex == 0 ? "AV1" : (cmbCodec.SelectedIndex == 1 ? "HEVC" : "X264");
            bool av1 = mode == "AV1";
            int grainMode = cmbGrainMode == null ? 0 : Math.Max(0, cmbGrainMode.SelectedIndex);
            if (av1 && grainMode == 2)
            {
                if (string.IsNullOrWhiteSpace(selectedAv1GrainTable) || !File.Exists(selectedAv1GrainTable))
                {
                    error = UiText("当前选择了 AV1 Grain Table，但没有可用的 .tbl/.txt 文件。", "AV1 Grain Table is selected, but no usable .tbl/.txt file is available.");
                    return null;
                }
            }
            if (!av1 && grainMode == 1)
            {
                if (string.IsNullOrWhiteSpace(selectedGrainPlatePath) || !File.Exists(selectedGrainPlatePath))
                {
                    error = UiText("当前选择了扫描颗粒文件，但颗粒文件不存在。", "Scanned Grain Plate is selected, but the grain plate file is missing.");
                    return null;
                }
            }

            string[] deintMethods = new string[] { "BWDIF_VULKAN", "BWDIF_CUDA", "W3FDIF" };
            int deintIndex = cmbDeintMethod == null ? 0 : Math.Max(0, Math.Min(2, cmbDeintMethod.SelectedIndex));
            int[] lutStrengths = new int[] { 25, 50, 75, 100 };
            int lutStrength = trackLutStrength == null ? 75 : lutStrengths[Math.Max(0, Math.Min(3, trackLutStrength.Value))];

            BridgeEnvironmentSnapshot snapshot = new BridgeEnvironmentSnapshot();
            snapshot.NoReencode = false;
            CaptureCommonEnvironmentState(snapshot);
            snapshot.Mode = mode;
            snapshot.Container = cmbContainer != null && cmbContainer.SelectedIndex == 1 ? "MKV" : "MP4";
            snapshot.Speed = mode == "X264" ? "X264" : (cmbSpeed != null && cmbSpeed.SelectedIndex == 2 ? "UHQ" : (cmbSpeed != null && cmbSpeed.SelectedIndex == 1 ? "STANDARD" : "FAST"));
            snapshot.SfeEngines = 0;
            if (av1 && chkSfe != null && chkSfe.Checked && hardwareCapsReady && hardwareCaps != null &&
                hardwareCaps.Av1SplitEncodeMaxEngines >= 2 && hardwareCaps.Grav1synthSfeCompatible &&
                cmbSpeed != null && (cmbSpeed.SelectedIndex == 1 || cmbSpeed.SelectedIndex == 2))
            {
                snapshot.SfeEngines = hardwareCaps.Av1SplitEncodeMaxEngines;
            }
            snapshot.BitrateMode = modeBitrateAuto[Math.Max(0, Math.Min(2, cmbCodec.SelectedIndex))] ? "AUTO" : "MANUAL";
            snapshot.Bitrate = bitrate;
            snapshot.HighMotion = chkHighMotion != null && chkHighMotion.Checked;
            snapshot.FpsMode = cmbFps != null && cmbFps.SelectedIndex == 1 ? "SOURCE" : "AUTO";
            snapshot.SvpInterpolate = chkInterpolation != null && chkInterpolation.Checked;
            snapshot.SvpAlgo = config.Get("SVP_ALGO");
            snapshot.SvpAnalyse = config.Get("SVP_ANALYSE");
            snapshot.SvpSceneMode = cmbInterpolationMode != null && cmbInterpolationMode.SelectedIndex == 1 ? "3" : "0";
            snapshot.SvpMaskArea = config.Get("SVP_MASK_AREA");
            snapshot.Deinterlace = cmbDeint != null && cmbDeint.SelectedIndex == 1 ? "OFF" : "AUTO";
            snapshot.DeintMethod = deintMethods[deintIndex];
            snapshot.CinematicFrame = chkCinematic != null && chkCinematic.Checked;
            snapshot.FrameMode = cmbFrameMode != null && cmbFrameMode.SelectedIndex == 1 ? "CROP" : "LETTERBOX";
            snapshot.CropPerSide = config.Get("CINEMATIC_CROP_PER_SIDE");
            snapshot.H264High10 = config.GetBool("H264_HIGH10") && (!hardwareCapsReady || hardwareCaps == null || (hardwareCaps.X264Available && hardwareCaps.X264High10Available));
            snapshot.X264Preset = config.Get("X264_PRESET");
            snapshot.X264PassMode = config.Get("X264_RATE_MODE");
            snapshot.HevcSpatialAq = config.Get("NVENC_SPATIAL_AQ");
            snapshot.HevcTemporalAq = config.GetBool("NVENC_TEMPORAL_AQ");
            snapshot.HdrPolicy = config.Get("HDR_POLICY");
            snapshot.TonemapAlgo = config.Get("TONE_MAP_ALGO");
            snapshot.UploadEnabled = uploadEnabled;
            snapshot.UploadBitrateMode = uploadBitrateAuto ? "AUTO" : "MANUAL";
            snapshot.UploadBitrate = uploadBitrate;

            snapshot.ColorEnabled = colorCorrectionEnabled;
            snapshot.ColorContrast = colorContrast;
            snapshot.ColorBrightness = colorBrightness;
            snapshot.ColorSaturation = colorSaturation;
            snapshot.ColorGamma = colorGamma;
            snapshot.ColorBlackWhite = colorBlackWhite;
            snapshot.LutEnabled = chkLut != null && chkLut.Checked;
            snapshot.LutPath = selectedLutPath;
            snapshot.LutStrength = lutStrength;

            snapshot.Av1 = av1;
            snapshot.GrainMode = grainMode;
            snapshot.Av1FormatIndex = av1FormatIndex;
            snapshot.Av1StockIndex = av1StockIndex;
            snapshot.Av1IsoValue = av1IsoValue;
            snapshot.Av1ChromaEnabled = av1ChromaEnabled;
            snapshot.Av1GrainTable = selectedAv1GrainTable;
            snapshot.ProceduralStrength = proceduralStrength;
            snapshot.FgsimPresetIndex = fgsimPresetIndex;
            snapshot.FgsimQuality = IsHevcFgsim() ? fgsimRcChoice : "VBR";
            snapshot.GrainRoot = config.Get("GRAIN_ROOT");
            snapshot.GrainPlatePath = selectedGrainPlatePath;
            snapshot.ScannedGrainStrengthIndex = scannedGrainStrengthIndex;
            return snapshot;
        }

        private BridgeEnvironmentSnapshot CaptureNoReencodeEnvironmentSnapshot(out string error)
        {
            error = "";
            if (listFiles == null || listFiles.Items.Count != 1)
            {
                error = lang.T("error.no_reencode_single");
                return null;
            }
            string path = listFiles.Items[0].Tag as string;
            MediaProbeInfo info;
            if (string.IsNullOrEmpty(path) || !mediaProbeCache.TryGetValue(path, out info) || !IsAv1Media(info))
            {
                error = lang.T("error.not_confirmed_av1");
                return null;
            }
            string grav = config.Get("GRAV1SYNTH");
            if (string.IsNullOrWhiteSpace(grav) || !File.Exists(grav))
            {
                error = LF("error.grav_missing", grav);
                return null;
            }
            int grainMode = cmbGrainMode == null ? 0 : Math.Max(0, cmbGrainMode.SelectedIndex);
            if (grainMode >= 3)
            {
                error = lang.T("error.digital_requires_encode");
                return null;
            }
            if (grainMode == 2 && (string.IsNullOrWhiteSpace(selectedAv1GrainTable) || !File.Exists(selectedAv1GrainTable)))
            {
                error = lang.T("error.no_grain_table");
                return null;
            }

            BridgeEnvironmentSnapshot snapshot = new BridgeEnvironmentSnapshot();
            snapshot.NoReencode = true;
            CaptureCommonEnvironmentState(snapshot);
            snapshot.Container = cmbContainer != null && cmbContainer.SelectedIndex == 1 ? "MKV" : "MP4";
            snapshot.GrainMode = grainMode;
            snapshot.Av1FormatIndex = av1FormatIndex;
            snapshot.Av1StockIndex = av1StockIndex;
            snapshot.Av1IsoValue = av1IsoValue;
            snapshot.Av1ChromaEnabled = av1ChromaEnabled;
            snapshot.Av1GrainTable = selectedAv1GrainTable;

            string grainState;
            if (av1GrainInspectCache.TryGetValue(path, out grainState))
            {
                if (grainState == "AV1 胶片颗粒：无") snapshot.SourceGrainAction = "ADDED";
                else if (grainState == "AV1 胶片颗粒：亮度" || grainState == "AV1 胶片颗粒：亮度 + 色度") snapshot.SourceGrainAction = "REPLACED";
            }
            return snapshot;
        }

        private void CaptureCommonEnvironmentState(BridgeEnvironmentSnapshot snapshot)
        {
            snapshot.TempMode = config.Get("TEMP_MODE");
            snapshot.TempCustomDir = config.Get("TEMP_CUSTOM_DIR");
            snapshot.OutputMode = config.Get("OUTPUT_MODE");
            snapshot.OutputCustomDir = config.Get("OUTPUT_CUSTOM_DIR");
            snapshot.Subtitle = subtitleState;
        }
    }
}
