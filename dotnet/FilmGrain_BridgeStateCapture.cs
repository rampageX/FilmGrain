using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

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
            var state = BridgeEnvironmentCore.Build(snapshot);
            state["FG_AV1_COPY_FAST"]=config.Get("AV1_COPY_FAST");
            state["FG_AV1_COPY_FULL_VERIFY"]=config.Get("AV1_COPY_FULL_VERIFY");
            
            return state;
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
            if (cmbCodec != null && cmbCodec.SelectedIndex == 4) return CaptureNoReencodeEnvironmentSnapshot(out error);

            long bitrate = 0;
            string bitrateText = cmbBitrate == null ? "" : cmbBitrate.Text.Trim();
            if (!IsCqSelected() && (!long.TryParse(bitrateText, NumberStyles.Integer, CultureInfo.InvariantCulture, out bitrate) || bitrate <= 10 || bitrate > 500000000))
            {
                error = lang.T("error.bitrate_invalid");
                return null;
            }

            
            
            

            if (IsLutEffectEnabled())
            {
                if (string.IsNullOrWhiteSpace(selectedLutPath)) { error = lang.T("info.lut_not_selected"); return null; }
                if (!File.Exists(selectedLutPath)) { error = LF("error.selected_lut_missing", selectedLutPath); return null; }
            }

            string mode = cmbCodec.SelectedIndex == 2 ? CpuCodec() : cmbCodec.SelectedIndex == 0 ? "AV1" : (cmbCodec.SelectedIndex == 1 ? "HEVC" : cmbCodec.SelectedIndex == 3 ? "H264" : "X264");
            bool av1 = mode == "AV1";
            int grainMode = cmbGrainMode == null ? 0 : Math.Max(0, cmbGrainMode.SelectedIndex);
            if (IsGrainEnabled() && av1 && grainMode == 2)
            {
                if (string.IsNullOrWhiteSpace(selectedAv1GrainTable) || !File.Exists(selectedAv1GrainTable))
                {
                    error = UiText("当前选择了 AV1 Grain Table，但没有可用的 .tbl/.txt 文件。", "AV1 Grain Table is selected, but no usable .tbl/.txt file is available.");
                    return null;
                }
            }
            if (IsGrainEnabled() && !av1 && grainMode == 1)
            {
                if (string.IsNullOrWhiteSpace(selectedGrainPlatePath) || !File.Exists(selectedGrainPlatePath))
                {
                    error = UiText("当前选择了扫描颗粒文件，但颗粒文件不存在。", "Scanned Grain Plate is selected, but the grain plate file is missing.");
                    return null;
                }
            }

            string[] deintMethods = new string[] { "BWDIF_VULKAN", "BWDIF_CUDA", "W3FDIF" };
            int deintIndex = cmbDeintMethod == null ? 1 : Math.Max(0, Math.Min(2, cmbDeintMethod.SelectedIndex));
            int lutStrength = GetLutStrength();

            BridgeEnvironmentSnapshot snapshot = new BridgeEnvironmentSnapshot();
            snapshot.NoReencode = false;
            snapshot.GrainEnabled = IsGrainEnabled();
            CaptureCommonEnvironmentState(snapshot);
            snapshot.Mode = mode;
            snapshot.Container = cmbContainer != null && cmbContainer.SelectedIndex == 1 ? "MKV" : "MP4";
            snapshot.Speed = mode == "X264" ? "X264" : (cmbSpeed != null && cmbSpeed.SelectedIndex == 2 ? "UHQ" : (cmbSpeed != null && cmbSpeed.SelectedIndex == 1 ? "STANDARD" : "FAST"));
            snapshot.SfeEngines = 0;
            if (av1 && chkSfe != null && chkSfe.Checked && hardwareCapsReady && hardwareCaps != null &&
                hardwareCaps.Av1SplitEncodeMaxEngines >= 2 && (!snapshot.GrainEnabled || hardwareCaps.Grav1synthSfeCompatible) &&
                cmbSpeed != null && (cmbSpeed.SelectedIndex == 1 || cmbSpeed.SelectedIndex == 2))
            {
                snapshot.SfeEngines = hardwareCaps.Av1SplitEncodeMaxEngines;
            }
            if(mode=="HEVC"&&chkSfe!=null&&chkSfe.Checked&&hardwareCapsReady&&hardwareCaps!=null&&hardwareCaps.HevcSplitEncodeMaxEngines>=2)snapshot.SfeEngines=hardwareCaps.HevcSplitEncodeMaxEngines;
            snapshot.NvencRateControl = IsCqSelected() ? "CQ" : "VBR";
            snapshot.NvencCq = IsCqSelected() ? cmbBitrate.Text.Trim() : config.Get(RateControlKey("CQ"));
            snapshot.NvencCqUser=IsCqSelected()&&IsCqAutomatic()?"AUTO":snapshot.NvencCq;
            if(IsCqSelected()&&IsCqAutomatic())snapshot.NvencCq=RecommendedCq();
            snapshot.NvencCqLimitEnabled=config.GetBool(RateControlKey("CQ_LIMIT_ENABLED"));
            snapshot.NvencCqMaxrate=config.Get(RateControlKey("CQ_MAXRATE"));
            if(IsCqSelected()) { double quality; if(!NvencRateControlCore.TryQuality(mode=="SVT"?"AV1":mode,snapshot.NvencCq,out quality)) { error="CQ: 请输入至少 1 且不超过 "+(mode=="AV1"?"63":"51")+" 的数值 / Invalid CQ target";return null; } }
            snapshot.BitrateMode = modeBitrateAuto[Math.Max(0, Math.Min(3, cmbCodec.SelectedIndex))] ? "AUTO" : "MANUAL";
            snapshot.Bitrate = bitrate;
            snapshot.HighMotion = chkHighMotion != null && chkHighMotion.Checked;
            snapshot.FpsMode = cmbFps != null && cmbFps.SelectedIndex == 1 ? "SOURCE" : "AUTO";
            snapshot.SvpInterpolate = chkInterpolation != null && chkInterpolation.Checked;
            snapshot.InterpolationEngine = cmbInterpolationMode != null && cmbInterpolationMode.SelectedIndex == 2 ? "FRUC" : "SVP";
            snapshot.FrucUseMainFfmpeg = config.FrucUsesMainFfmpeg();
            snapshot.FrucFfmpegPath = snapshot.FrucUseMainFfmpeg ? "" : config.Get("FRUC_FFMPEG_PATH");
            snapshot.SvpAlgo = config.Get("SVP_ALGO");
            snapshot.SvpAnalyse = config.Get("SVP_ANALYSE");
            snapshot.SvpSceneMode = cmbInterpolationMode != null && cmbInterpolationMode.SelectedIndex == 1 ? "3" : "0";
            snapshot.SvpMaskArea = config.Get("SVP_MASK_AREA");
            snapshot.SvpSpeed = config.Get("SVP_SPEED");
            snapshot.SvpTuning = config.Get("SVP_TUNING");
            snapshot.SvpBlockSize = config.Get("SVP_BLOCK_SIZE");
            snapshot.SvpDedup = config.Get("SVP_DEDUP");
            snapshot.SvpStill = config.Get("SVP_STILL");
            snapshot.SvpStillLimit = config.Get("SVP_STILL_LIMIT");
            snapshot.SvpStillEdge = config.Get("SVP_STILL_EDGE");
            snapshot.SvpStillTolerance = config.Get("SVP_STILL_TOLERANCE");
            snapshot.SvpPeel = config.Get("SVP_PEEL");
            snapshot.SvpPeelLimit = config.Get("SVP_PEEL_LIMIT");

            if (snapshot.SvpInterpolate && snapshot.InterpolationEngine == "SVP" &&
                !SharedSvpCore.TryValidateOptions(snapshot.SvpSpeed, snapshot.SvpTuning, snapshot.SvpBlockSize, snapshot.SvpAnalyse, out error))
                return null;
            snapshot.InterpolationTargetFps = cmbInterpolationFps == null ? config.Get("INTERPOLATION_TARGET_FPS") : cmbInterpolationFps.Text.Trim();
            snapshot.FrucPerf = config.Get("FRUC_PERF");
            snapshot.FrucGrid = config.Get("FRUC_GRID");
            if (snapshot.SvpInterpolate)
            {
                if (listFiles != null)
                {
                    foreach (ListViewItem item in listFiles.Items)
                    {
                        string sourcePath = item.Tag as string;
                        if(captureInputPath!=null&&!string.Equals(sourcePath,captureInputPath,StringComparison.OrdinalIgnoreCase))continue;
                        MediaProbeInfo sourceInfo;
                        if (string.IsNullOrEmpty(sourcePath) || !mediaProbeCache.TryGetValue(sourcePath, out sourceInfo))
                        { error = UiText("无法读取源视频帧率，不能解析插帧目标帧率。", "Cannot read source frame rate to resolve interpolation target."); return null; }
                        InterpolationFrameRate parsedRate; string rateError;
                        if (!InterpolationFrameRateCore.TryParse(snapshot.InterpolationTargetFps, sourceInfo.AvgFrameRate, out parsedRate, out rateError))
                        { error = UiText("插帧目标帧率无效：", "Invalid interpolation target frame rate: ") + rateError; return null; }
                    }
                }
                else
                {
                    InterpolationFrameRate parsedRate; string rateError;
                    if (!InterpolationFrameRateCore.TryParse(snapshot.InterpolationTargetFps, "60", out parsedRate, out rateError))
                    { error = UiText("插帧目标帧率无效：", "Invalid interpolation target frame rate: ") + rateError; return null; }
                }
            }
            snapshot.Deinterlace = cmbDeint != null && cmbDeint.SelectedIndex == 1 ? "OFF" : "AUTO";
            snapshot.DeintMethod = deintMethods[deintIndex];
            snapshot.CinematicFrame = chkCinematic != null && chkCinematic.Checked;
            snapshot.FrameMode = cmbFrameMode != null && cmbFrameMode.SelectedIndex == 1 ? "CROP" : "LETTERBOX";
            snapshot.CropPerSide = config.Get("CINEMATIC_CROP_PER_SIDE");
            snapshot.CropHorizontal = config.Get("CINEMATIC_CROP_HORIZONTAL");
            snapshot.NoCrop = config.Get("CINEMATIC_NO_CROP");
            snapshot.H264High10 = config.GetBool("H264_HIGH10") && (!hardwareCapsReady || hardwareCaps == null || (hardwareCaps.X264Available && hardwareCaps.X264High10Available));
            snapshot.X264Preset = config.Get("X264_PRESET");
            snapshot.X264PassMode = config.Get("X264_RATE_MODE");
            snapshot.HevcSpatialAq = config.EncoderAq(mode, false);
            snapshot.HevcTemporalAq = string.Equals(config.EncoderAq(mode, true), "true", StringComparison.OrdinalIgnoreCase);
            snapshot.HdrPolicy = config.Get("HDR_POLICY");
            snapshot.TonemapAlgo = config.Get("TONE_MAP_ALGO");
            
            
            

            snapshot.ColorEnabled = colorCorrectionEnabled;
            snapshot.ColorContrast = colorContrast;
            snapshot.ColorBrightness = colorBrightness;
            snapshot.ColorSaturation = colorSaturation;
            snapshot.ColorGamma = colorGamma;
            snapshot.ColorBlackWhite = colorBlackWhite;
            snapshot.LutEnabled = IsLutEffectEnabled();
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
            snapshot.GrainRoot = config.Get("GRAIN_ROOT");
            snapshot.GrainPlatePath = selectedGrainPlatePath;
            snapshot.ScannedGrainStrengthPercent = scannedGrainStrengthPercent;
            return snapshot;
        }

        private BridgeEnvironmentSnapshot CaptureNoReencodeEnvironmentSnapshot(out string error)
        {
            error = "";
            if (listFiles == null || (captureInputPath==null && listFiles.Items.Count != 1))
            {
                error = lang.T("error.no_reencode_single");
                return null;
            }
            string path = captureInputPath ?? listFiles.Items[0].Tag as string;
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
                snapshot.SourceGrainAction = Av1GrainInspectCore.SourceGrainAction(grainState);
            }
            return snapshot;
        }

        private void CaptureCommonEnvironmentState(BridgeEnvironmentSnapshot snapshot)
        {
            snapshot.CpuSvtTuning=config.Get("CPU_SVT_TUNING");
            snapshot.CpuSvtGrainTune=config.Get("CPU_SVT_GRAIN_TUNE");snapshot.CpuX265GrainTune=config.Get("CPU_X265_GRAIN_TUNE");snapshot.X264GrainTune=config.Get("X264_GRAIN_TUNE");
            snapshot.CpuSvtPreset=config.Get("CPU_SVT_PRESET");snapshot.CpuX265Preset=config.Get("CPU_X265_PRESET");snapshot.CpuX265ThreadedMe=config.Get("CPU_X265_THREADED_ME");
            snapshot.AudioMode = config.Get("AUDIO_MODE");
            snapshot.AudioBitrate = config.Get("AUDIO_BITRATE");
            snapshot.Av1Multipass = config.Get("AV1_MULTIPASS");
            snapshot.HevcMultipass = config.Get("HEVC_MULTIPASS");
            snapshot.H264Multipass = config.Get("H264_MULTIPASS");
            snapshot.HevcBFrames = config.Get("HEVC_BFRAMES"); snapshot.H264BFrames = config.Get("H264_BFRAMES");
            snapshot.HevcBReference = config.Get("HEVC_BREF_MODE"); snapshot.H264BReference = config.Get("H264_BREF_MODE");
            snapshot.HevcLookahead = config.Get("HEVC_LOOKAHEAD"); snapshot.H264Lookahead = config.Get("H264_LOOKAHEAD");

            snapshot.Av1GrainStrength = Av1GrainStrengthPercent;
            snapshot.PixelGrainStrength = 100;
            snapshot.TempMode = config.Get("TEMP_MODE");
            snapshot.TempCustomDir = config.Get("TEMP_CUSTOM_DIR");
            snapshot.OutputMode = config.Get("OUTPUT_MODE");
            snapshot.OutputCustomDir = config.Get("OUTPUT_CUSTOM_DIR");
            snapshot.Subtitle = subtitleState.Copy();
        }
    }

    internal sealed partial class MainForm
    {
        // Editor-only memento. Runtime state is always produced by BuildBridgeEnvironment.
        // Explicit allowlists exclude preview images, media caches, capabilities and UI theme.
        private sealed class FileEditorSettings
        {
            internal Dictionary<string,string> Config;
            internal Dictionary<string,object> Fields=new Dictionary<string,object>();
            internal Dictionary<string,string> Controls=new Dictionary<string,string>();
            internal string[] Bitrates;
            internal bool[] Automatic;
            internal SubtitleState Subtitle;
            internal FileEditorSettings Copy()
            {
                return new FileEditorSettings { Config=new Dictionary<string,string>(Config,StringComparer.OrdinalIgnoreCase),
                    Fields=new Dictionary<string,object>(Fields), Controls=new Dictionary<string,string>(Controls),
                    Bitrates=(string[])Bitrates.Clone(),Automatic=(bool[])Automatic.Clone(),Subtitle=Subtitle.Copy() };
            }
        }
        private const string FileEditorFields="ordinaryGrainEnabled av1GrainModeIndex pixelGrainModeIndex av1FormatIndex av1StockIndex av1IsoValue av1ChromaEnabled selectedAv1GrainTable proceduralStrength fgsimPresetIndex selectedGrainPlatePath scannedGrainStrengthPercent selectedLutPath selectedLutSource colorCorrectionEnabled colorContrast colorBrightness colorSaturation colorGamma colorBlackWhite";
        private const string FileEditorControls="cmbCodec cmbContainer cmbSpeed chkSfe chkCq cmbBitrate chkBitrateAuto chkHighMotion cmbFps chkInterpolation cmbInterpolationMode cmbInterpolationFps cmbDeint cmbDeintMethod chkCinematic cmbFrameMode chkGrainEnabled chkLut trackLutStrength";
        private readonly Dictionary<string,FileEditorSettings> fileSettings=new Dictionary<string,FileEditorSettings>(StringComparer.OrdinalIgnoreCase);
        private FileEditorSettings sharedEditor;
        private string editingFile, captureInputPath;
        private bool independentFiles, restoringFileEditor, changingFileSelection, selectingQueueFile;
        private CheckBox fileSettingsMode;
        private ToolStripMenuItem fileCopyFastMenu,fileCopyVerifyMenu;
        private System.Reflection.FieldInfo EditorField(string name)
        {
            var field=typeof(MainForm).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            if(field==null)throw new InvalidOperationException("Missing task editor field: "+name);
            return field;
        }
        private FileEditorSettings CaptureFileEditor()
        {
            // Retain only the user's manual value. AUTO's displayed media recommendation is not a setting.
            if(cmbCodec.SelectedIndex>=0&&cmbCodec.SelectedIndex<4&&!IsCqSelected()&&!chkBitrateAuto.Checked) {
                long n;if(long.TryParse(cmbBitrate.Text,out n)&&n>10)modeBitrate[cmbCodec.SelectedIndex]=cmbBitrate.Text;
            }
            var result=new FileEditorSettings { Config=config.CaptureTaskValues(),Bitrates=(string[])modeBitrate.Clone(),Automatic=(bool[])modeBitrateAuto.Clone(),Subtitle=subtitleState.Copy() };
            foreach(string name in FileEditorFields.Split(' ')) {
                object value=EditorField(name).GetValue(this);
                if(value!=null&&!value.GetType().IsValueType&&!(value is string))throw new InvalidOperationException("Mutable editor field: "+name);
                result.Fields[name]=value;
            }
            foreach(string name in FileEditorControls.Split(' ')) {
                object control=EditorField(name).GetValue(this);
                var combo=control as ComboBox;var check=control as CheckBox;var track=control as TrackBar;var text=control as MaskedTextBox;
                if(combo!=null) result.Controls[name]=combo.DropDownStyle==ComboBoxStyle.DropDownList?combo.SelectedIndex.ToString(CultureInfo.InvariantCulture):combo.Text;
                else if(check!=null)result.Controls[name]=check.Checked?"1":"0";
                else if(track!=null)result.Controls[name]=track.Value.ToString(CultureInfo.InvariantCulture);
                else if(text!=null)result.Controls[name]=text.Text;
                else if(control!=null)throw new InvalidOperationException("Unsupported editor control: "+name);
            }
            string[] placeholders={"5000","6000","7500","7500"};
            for(int i=0;i<result.Automatic.Length;i++)if(result.Automatic[i])result.Bitrates[i]=placeholders[i];
            if(cmbCodec.SelectedIndex>=0&&cmbCodec.SelectedIndex<4&&!IsCqSelected()&&result.Automatic[cmbCodec.SelectedIndex])result.Controls["cmbBitrate"]=placeholders[cmbCodec.SelectedIndex];
            return result;
        }
        private void ApplyFileEditor(FileEditorSettings source, bool taskOverlay)
        {
            FileEditorSettings value=source.Copy();restoringFileEditor=true;SuspendLayout();
            try {
                config.UseTaskValues(taskOverlay?value.Config:null);
                if(fileCopyFastMenu!=null)fileCopyFastMenu.Checked=config.Get("AV1_COPY_FAST")!="false";
                if(fileCopyVerifyMenu!=null)fileCopyVerifyMenu.Checked=config.GetBool("AV1_COPY_FULL_VERIFY");
                foreach(var pair in value.Fields)EditorField(pair.Key).SetValue(this,pair.Value);
                Array.Copy(value.Bitrates,modeBitrate,modeBitrate.Length);Array.Copy(value.Automatic,modeBitrateAuto,modeBitrateAuto.Length);
                if(int.Parse(value.Controls["cmbCodec"],CultureInfo.InvariantCulture)==4&&cmbCodec.Items.Count<5)cmbCodec.Items.Add(lang.T("codec.no_reencode"));
                cmbCodec.SelectedIndex=int.Parse(value.Controls["cmbCodec"],CultureInfo.InvariantCulture);
                lastCodecIndex=Math.Max(0,Math.Min(3,cmbCodec.SelectedIndex));
                UpdateSpeedChoices();UpdateGrainForCodec();
                foreach(var pair in value.Controls) {
                    object control=EditorField(pair.Key).GetValue(this);
                    var combo=control as ComboBox;var check=control as CheckBox;var track=control as TrackBar;var text=control as MaskedTextBox;
                    if(combo!=null) {
                        if(combo.DropDownStyle==ComboBoxStyle.DropDownList) {int index=int.Parse(pair.Value,CultureInfo.InvariantCulture);if(index>=-1&&index<combo.Items.Count)combo.SelectedIndex=index;}
                        else combo.Text=pair.Value;
                    }
                    else if(check!=null)check.Checked=pair.Value=="1";
                    else if(track!=null)track.Value=int.Parse(pair.Value,CultureInfo.InvariantCulture);
                    else if(text!=null)text.Text=pair.Value;
                }
                foreach(var field in typeof(SubtitleState).GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic))field.SetValue(subtitleState,field.GetValue(value.Subtitle));
                // Media-derived text is refreshed below, never stored as a task configuration.
                UpdateNoReencodeUi();UpdateSfeUi();UpdateMediaDrivenUi(GetBitrateMediaContext());UpdateGrainEnableUi();UpdateCinematicCropLabels();RefreshSubtitleButton();UpdateColorCorrectionUi();UpdateLutUi();lblLutStrength.Text=GetLutStrength().ToString(CultureInfo.InvariantCulture)+"%";
            } finally {restoringFileEditor=false;ResumeLayout(true);}
            SyncCodecPicker();LoadBitrateStateForCodec();UpdateBitrateDisplays();
        }
        private readonly HashSet<string> initializedFileSettings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private void SaveEditingFile()
        {
            if(independentFiles&&editingFile!=null){fileSettings[editingFile]=CaptureFileEditor();initializedFileSettings.Add(editingFile);}
        }
        private void SelectFileEditor()
        {
            if(!independentFiles||restoringFileEditor||changingFileSelection||listFiles.SelectedItems.Count!=1)return;
            string path=listFiles.SelectedItems[0].Tag as string;
            if(path==null||path==editingFile)return;
            // Capture before switching: untouched files inherit the current processing flow.
            // Queue-driven selection only displays the already frozen per-file configuration.
            bool inherit=!selectingQueueFile&&!initializedFileSettings.Contains(path)&&(bridgeTaskCoordinator==null||!bridgeTaskCoordinator.IsActive);
            FileEditorSettings current=inherit?CaptureFileEditor():null;
            SaveEditingFile();editingFile=path;
            FileEditorSettings value;if(!fileSettings.TryGetValue(path,out value))fileSettings[path]=value=sharedEditor.Copy();
            if(inherit){current.Subtitle=value.Subtitle.Copy();fileSettings[path]=value=current;}
            initializedFileSettings.Add(path);
            ApplyFileEditor(value,true);UpdateFileSettingsLabels();
        }
        private void ChangeFileSettingsMode()
        {
            if(restoringFileEditor)return;
            bool enabled=fileSettingsMode.Checked;
            if(enabled==independentFiles)return;
            if(enabled) {
                sharedEditor=CaptureFileEditor();independentFiles=true;editingFile=null;
                foreach(ListViewItem item in listFiles.Items) {
                    string path=item.Tag as string;if(path!=null&&!fileSettings.ContainsKey(path))fileSettings[path]=sharedEditor.Copy();
                }
                changingFileSelection=true;
                try {listFiles.MultiSelect=true;}
                finally {changingFileSelection=false;}
                if(listFiles.SelectedItems.Count==0&&listFiles.Items.Count>0)listFiles.Items[0].Selected=true;
                SelectFileEditor();
                if(editingFile==null)config.UseTaskValues(sharedEditor.Config);
            } else {
                SaveEditingFile();independentFiles=false;editingFile=null;listFiles.MultiSelect=true;
                ApplyFileEditor(sharedEditor,false);
            }
            UpdateNoReencodeAvailability();UpdateFileSettingsLabels();
        }
        private Control BuildFileSettingsBar()
        {
            var panel=new Panel {Dock=DockStyle.Fill,Margin=Padding.Empty};
            fileSettingsMode=new ThemeCheckBox {Text=UiText("独立配置","Per-file"),AutoSize=true,Checked=false,Margin=Padding.Empty};
            fileSettingsMode.CheckedChanged+=delegate {ChangeFileSettingsMode();};
            panel.Controls.Add(fileSettingsMode);AddPresetControls(panel);
            bool aligning=false;FlowLayoutPanel above=null;
            Action align=delegate {
                if(aligning||above==null||above.IsDisposed||above.Controls.Count<4||panel.IsDisposed)return;
                aligning=true;
                try {
                    Control add=above.Controls[0],remove=above.Controls[1],clear=above.Controls[2],bake=above.Controls[3];
                    // Both rows share the same TableLayoutPanel. Include each row's
                    // actual offset, so margins, resizing and DPI do not drift.
                    int offset=above.Left-panel.Left;
                    System.Drawing.Size caption=fileSettingsMode.GetPreferredSize(System.Drawing.Size.Empty);
                    fileSettingsMode.SetBounds(offset+add.Left,Math.Max(0,(panel.ClientSize.Height-caption.Height)/2),caption.Width,caption.Height);
                    Control manager=panel.Controls[1];
                    manager.SetBounds(offset+remove.Left,Math.Max(0,(panel.ClientSize.Height-remove.Height)/2),remove.Width,remove.Height);
                    presetSelector.SetBounds(offset+clear.Left,Math.Max(0,(panel.ClientSize.Height-presetSelector.Height)/2),Math.Max(1,bake.Right-clear.Left),presetSelector.Height);
                } finally {aligning=false;}
            };
            LayoutEventHandler onAboveLayout=delegate{align();};
            panel.ParentChanged+=delegate {
                if(above!=null)above.Layout-=onAboveLayout;
                var rows=panel.Parent as TableLayoutPanel;above=rows==null?null:rows.GetControlFromPosition(0,0) as FlowLayoutPanel;
                if(above!=null)above.Layout+=onAboveLayout;
                align();
            };
            panel.Layout+=delegate{align();};
            panel.Disposed+=delegate{if(above!=null)above.Layout-=onAboveLayout;};
            return panel;
        }
        private void UpdateFileSettingsLabels()
        {
            foreach(ListViewItem item in listFiles.Items) {
                string path=item.Tag as string;if(path==null)continue;
                item.Text=(independentFiles?UiText("[独立] ","[File] "):"")+Path.GetFileName(path);
                item.ToolTipText=path+(!independentFiles&&fileSettings.ContainsKey(path)?UiText("\r\n已保留独立配置；统一模式暂不应用。","\r\nPer-file settings retained; inactive in shared mode."):"");
            }
        }
        // Membership is scoped to the current list; reset or remove/re-add requests a rerun.
        private void ResetSelectedQueueStatus()
        {
            if(bridgeTaskCoordinator!=null&&bridgeTaskCoordinator.IsActive)return;
            listFiles.BeginUpdate();
            try {
                foreach(ListViewItem item in listFiles.SelectedItems) {
                    string path=item.Tag as string;if(string.IsNullOrEmpty(path))continue;
                    completedQueueFiles.Remove(path);
                    submittedQueueFiles.Remove(path);
                    displayedQueueFiles.Remove(path);
                    item.SubItems[2].Text=UiText("等待","Waiting");
                }
            } finally {listFiles.EndUpdate();}
        }
        private readonly HashSet<string> completedQueueFiles=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> submittedQueueFiles=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool IsPendingQueueFile(string path){return !string.IsNullOrEmpty(path)&&!completedQueueFiles.Contains(path);}
        private List<string> PendingQueueInputs(){var paths=new List<string>();foreach(ListViewItem item in listFiles.Items){string path=item.Tag as string;if(IsPendingQueueFile(path))paths.Add(path);}return paths;}
        private readonly HashSet<string> displayedQueueFiles=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private ListViewItem QueueFileItem(BridgeTaskRequest task)
        {
            if(task==null||task.ExecutionRequest==null||task.ExecutionRequest.InputFiles==null||task.ExecutionRequest.InputFiles.Count==0)return null;
            string input=task.ExecutionRequest.InputFiles[0];
            foreach(ListViewItem item in listFiles.Items)if(string.Equals(item.Tag as string,input,StringComparison.OrdinalIgnoreCase))return item;
            return null;
        }
        private void UpdateQueueFileState(BridgeTaskStateChangedEventArgs e)
        {
            if(e.State==BridgeTaskState.Idle) {
                foreach(ListViewItem item in listFiles.Items)
                    if(displayedQueueFiles.Contains(item.Tag as string)&&item.SubItems[2].Text==UiText("等待","Waiting"))item.SubItems[2].Text=UiText("未执行","Not run");
                return;
            }
            var active=QueueFileItem(e.Task);if(active==null)return;
            if(e.State==BridgeTaskState.Cancelling){active.SubItems[2].Text=UiText("取消中","Cancelling");return;}
            if(e.Task.QueueIndex<=1) {
                displayedQueueFiles.Clear();
                foreach(ListViewItem item in listFiles.Items)if(submittedQueueFiles.Contains(item.Tag as string)) {
                    displayedQueueFiles.Add((string)item.Tag);item.SubItems[2].Text=UiText("等待","Waiting");
                }
            }
            active.SubItems[2].Text=UiText("处理中","Processing");
            changingFileSelection=true;
            try {foreach(ListViewItem item in listFiles.Items)item.Selected=false;active.Selected=true;active.Focused=true;active.EnsureVisible();}
            finally {changingFileSelection=false;}
            selectingQueueFile=true;try{SelectFileEditor();}finally{selectingQueueFile=false;}UpdateSelectedInfo();listFiles.Focus();
        }
        private void OnQueueItemCompleted(object sender,BridgeTaskItemCompletedEventArgs e)
        {
            if(IsDisposed||Disposing||!IsHandleCreated)return;
            try {BeginInvoke((MethodInvoker)delegate {
                if(IsDisposed||Disposing)return;
                var item=QueueFileItem(e.Task);if(item==null)return;
                string path=item.Tag as string;
                if(!e.Result.Cancelled&&e.Result.ExitCode==0&&e.Result.Exception==null)completedQueueFiles.Add(path);
                item.SubItems[2].Text=e.Result.Cancelled?UiText("已取消","Cancelled"):
                    e.Result.ExitCode==0&&e.Result.Exception==null?UiText("结束","Finished"):UiText("失败","Failed");
            });}catch(InvalidOperationException){}
        }
        private void StartIndependentQueue()
        {
            SaveEditingFile();
            var states=new Dictionary<string,SortedDictionary<string,string>>(StringComparer.OrdinalIgnoreCase);
            var inputs=new List<string>();
            FileEditorSettings restore=CaptureFileEditor();
            try {
                foreach(ListViewItem item in listFiles.Items) {
                    string input=item.Tag as string;if(!IsPendingQueueFile(input))continue;
                    FileEditorSettings editor;if(!fileSettings.TryGetValue(input,out editor))fileSettings[input]=editor=sharedEditor.Copy();
                    captureInputPath=input;ApplyFileEditor(editor,true);
                    if(!ValidateHardwareForSelectedCodec())return;
                    string error;var state=BuildBridgeEnvironment(out error);
                    if(state==null)throw new InvalidOperationException(input+" : "+error);
                    if(chkEncodePreview.Checked) {
                        double start;if(!numPreviewDuration.ValidateInput()||!SharedEncodePreviewCore.TryParseStart(txtPreviewStart.Text,out start))throw new InvalidOperationException("Invalid preview range: "+input);
                        state["FG_ENCODE_PREVIEW"]="1";state["FG_PREVIEW_START"]=txtPreviewStart.Text;state["FG_PREVIEW_SECONDS"]=numPreviewDuration.Value.ToString(CultureInfo.InvariantCulture);
                    }
                    MediaProbeInfo media;mediaProbeCache.TryGetValue(input,out media);
                    state["FG_QUEUE_OUTPUT_FPS"]=GetRecommendedOutputFps(media).ToString(CultureInfo.InvariantCulture);
                    state["FG_QUEUE_NO_REENCODE"]=cmbCodec.SelectedIndex==4?"1":"0";
                    states[input]=new SortedDictionary<string,string>(state,StringComparer.OrdinalIgnoreCase);inputs.Add(input);
                }
            } catch(Exception ex) {MessageBox.Show(this,ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}
            finally {captureInputPath=null;ApplyFileEditor(restore,true);}
            if(inputs.Count==0)return;
            if(taskLog!=null)taskLog.Dispose();taskLog=new FullLogFileCore(appRoot,"Task",GetLogEntryLimit());fullLogErrorReported=false;
            AppendTaskLog("[Queue] mode=per-file frozen="+inputs.Count+Environment.NewLine);
            StartSharedQueue(inputs,states[inputs[0]],states[inputs[0]]["FG_QUEUE_NO_REENCODE"]=="1",states);
        }
    }

}
