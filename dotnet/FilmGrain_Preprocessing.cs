using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace FilmGrainStudioPreview
{
    // A CPU-frame prefix shared by all six encoders and preview preparation.
    // No timestamp, range, transfer or output pixel-format changes belong here.
    internal static class PreprocessingCore
    {
        internal static readonly string[] Keys={"PRE_BILATERAL_ENABLED","PRE_BILATERAL_SIGMA_S","PRE_BILATERAL_SIGMA_R","PRE_BILATERAL_PLANES"};
        internal static readonly string[] Defaults={"false","3","0.05","1"};
        static readonly object Sync=new object();
        static readonly System.Collections.Generic.HashSet<string> Verified=new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        static string Read(System.Collections.Generic.IDictionary<string,string> state,string key,string fallback)
        {string value;return state.TryGetValue("FG_"+key,out value)?value:fallback;}
        internal static bool Enabled(System.Collections.Generic.IDictionary<string,string> state)
        {string value=Read(state,Keys[0],"false");return string.Equals(value,"true",StringComparison.OrdinalIgnoreCase)||value=="1";}
        internal static bool TryBuild(System.Collections.Generic.IDictionary<string,string> state,out string filter,out string reason)
        {
            filter="";reason="";if(!Enabled(state))return true;
            double spatial,range;int planes;
            if(!double.TryParse(Read(state,Keys[1],"3"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out spatial)||double.IsNaN(spatial)||double.IsInfinity(spatial)||spatial<0||spatial>512 ||
               !double.TryParse(Read(state,Keys[2],"0.05"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out range)||double.IsNaN(range)||double.IsInfinity(range)||range<0||range>1 ||
               !int.TryParse(Read(state,Keys[3],"1"),System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out planes)||planes<0||planes>15)
            {reason="Bilateral 参数无效：sigmaS 0–512，sigmaR 0–1，planes 0–15。";return false;}
            filter="format=yuv420p10le,bilateral=sigmaS="+spatial.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+":sigmaR="+range.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+":planes="+planes.ToString(System.Globalization.CultureInfo.InvariantCulture)+",";
            return true;
        }
        // Admit only the exact CPU Bilateral prefix already built by this core.
        // A future additional preprocessing filter must not inherit S4 eligibility.
        internal static bool IsOnlyBilateral(System.Collections.Generic.IDictionary<string,string> state,string filter)
        {
            if(!Enabled(state)||string.IsNullOrEmpty(filter))return false;
            string expected,reason;
            return TryBuild(state,out expected,out reason)&&string.Equals(filter,expected,StringComparison.Ordinal);
        }
        internal static bool TryCheckTool(string ffmpeg,out string reason)
        {
            reason="";
            try {
                var file=new System.IO.FileInfo(ffmpeg);
                string identity=file.FullName+"|"+file.Length+"|"+file.LastWriteTimeUtc.Ticks;
                lock(Sync)if(Verified.Contains(identity))return true;
                string output,detail;
                bool ok=SharedFrucCore.Run(ffmpeg,"-hide_banner -nostdin -v error -f lavfi -i color=s=32x32:d=0.04 -vf format=yuv420p10le,bilateral=sigmaS=3:sigmaR=0.05:planes=1 -frames:v 1 -f null -",10000,out output,out detail);
                if(!ok){reason="此 FFmpeg 未通过 CPU bilateral 10-bit 检查："+ffmpeg+" / "+detail+" / "+output;return false;}
                lock(Sync)Verified.Add(identity);return true;
            }catch(System.Exception ex){reason="CPU bilateral 检查失败："+ex.Message;return false;}
        }
    }
}

namespace FilmGrainStudioPreview
{
    internal sealed class PreprocessCropPage
    {
        private readonly Func<string,string,string> text;
        private readonly MediaProbeInfo media;
        private MediaProbeInfo resizedCanvas;
        private bool resizeEnabled;
        private string resizeError = "";
        private readonly bool chinese;
        private readonly NumericUpDown[] margins = new NumericUpDown[4];
        private readonly ComboBox mode = new ThemeComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        private readonly ComboBox preset = new ThemeComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 196 };
        private readonly CheckBox enabled = new ThemeCheckBox { AutoSize = true };
        private readonly CheckBox afterResize = new ThemeCheckBox { AutoSize = true };
        private readonly Label size = new ThemeLabel { AutoSize = true };
        private string marginText, resizedMargins, vertical, horizontal, noCrop;
        // Only a deliberate preset selection retains its name; edited geometry is Custom.
        private int selectedPresetIndex;
        private bool updating;
        internal event Action Changed;
        internal bool Enabled { get { return enabled.Checked; } }
        internal int Mode { get { return mode.SelectedIndex; } }
        private bool UseResizedCanvas { get { return afterResize.Checked && resizeEnabled; } }
        private MediaProbeInfo Canvas { get { return UseResizedCanvas ? resizedCanvas : media; } }
        private Size SourceSize { get { return new Size(Canvas.Width, Canvas.Height); } }
        private string CurrentMargins
        {
            get { return UseResizedCanvas ? resizedMargins : marginText; }
            set { if (UseResizedCanvas) resizedMargins = value; else marginText = value; }
        }

        internal PreprocessCropPage(TabPage page, FgsConfig config, int frameMode, MediaProbeInfo media, string input,
            Func<string,string,string> text, bool chinese)
        {
            this.text = text;
            this.media = media != null && media.Width >= 2 && media.Height >= 2 ? media : null;
            this.chinese = chinese;
            marginText = config.Get(CinematicFrameCore.MarginsKey);
            resizedMargins = config.Get(ResizeCore.CropMarginsKey);
            afterResize.Text = text("缩放后剪裁", "Crop after resize");
            afterResize.Checked = config.GetBool(ResizeCore.CropAfterKey);
            vertical = config.Get("CINEMATIC_CROP_PER_SIDE");
            horizontal = config.Get("CINEMATIC_CROP_HORIZONTAL");
            noCrop = config.Get("CINEMATIC_NO_CROP");
            if (!config.GetBool(CinematicFrameCore.CropEnabledKey) &&
                !CinematicFrameCore.HasLegacyCrop("LETTERBOX", marginText, vertical, horizontal, noCrop))
                marginText = "0,0,0,0";
            enabled.Text = text("启用剪裁", "Enable crop");
            enabled.Checked = config.GetBool(CinematicFrameCore.CropEnabledKey);
            mode.Items.AddRange(new object[] { text("加黑边", "Letterbox"), text("裁剪画面", "Crop") });
            mode.SelectedIndex = frameMode == 1 || config.Get(CinematicFrameCore.CropEnabledKey) == "" ? 1 : 0;
            preset.Items.AddRange(CinematicCropGeometry.PresetLabels(text));
            selectedPresetIndex = config.GetInt(CinematicCropGeometry.PresetKey,0);
            if(selectedPresetIndex<0 || selectedPresetIndex>=preset.Items.Count)selectedPresetIndex=0;
            preset.Enabled = this.media != null;
            var visual = new ThemeButton {
                Text = text("可视化裁剪…", "Visual Crop…"), Width = 158,
                FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, BackColor = Color.LightSteelBlue
            };
            visual.Click += delegate {
                if (this.media == null || string.IsNullOrEmpty(input)) {
                    FgsMessageBox.Show(page.FindForm(), text("请先选择并探测视频。", "Select and probe a video first."));
                    return;
                }
                if (Canvas == null) {
                    FgsMessageBox.Show(page.FindForm(), resizeError);
                    return;
                }
                var draft = CanvasState();
                draft["FG_FRAME_MODE"] = "CROP";
                using (var window = new NativeCinematicCropForm(System.IO.Path.Combine(config.Get("FFMPEG_DIR"),"ffmpeg.exe"),
                    input, this.media, draft, this.chinese, false, UseResizedCanvas ? Canvas : null))
                {
                    if (window.ShowDialog(page.FindForm()) != DialogResult.OK) return;
                    updating = true;
                    try {
                        mode.SelectedIndex = window.FrameMode;
                        CurrentMargins = window.CropMargins;
                        enabled.Checked = true;
                        if(window.Changed)selectedPresetIndex=0;
                    }
                    finally { updating = false; }
                    RefreshValues();
                    NotifyChanged();
                }
            };

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(14) };
            for (int i = 0; i < 4; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var top = new SettingsFlowPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false };
            // Use native combo heights for all three controls; center the checkbox in the same row.
            mode.Margin = preset.Margin = visual.Margin = new Padding(3,2,3,2);
            enabled.Margin = new Padding(3,2,8,2);
            enabled.Anchor = AnchorStyles.None;
            visual.Height = Math.Max(mode.Height,preset.Height);
            top.Layout += delegate {
                int height=Math.Max(mode.Height,preset.Height);
                if(visual.Height!=height)visual.Height=height;
            };
            top.Controls.Add(enabled); top.Controls.Add(mode); top.Controls.Add(preset); top.Controls.Add(visual);
            layout.Controls.Add(top, 0, 0);
            layout.Controls.Add(NativeCinematicCropForm.BuildMarginControls(margins, text, EditMargin), 0, 1);
            layout.Controls.Add(afterResize, 0, 2);
            layout.Controls.Add(size, 0, 3);
            page.Controls.Add(layout);

            afterResize.CheckedChanged += delegate { if (!updating) { selectedPresetIndex=0; RefreshValues(); if (Enabled) NotifyChanged(); } };
            enabled.CheckedChanged += delegate { if (!updating) NotifyChanged(); };
            mode.SelectedIndexChanged += delegate { if (!updating) { selectedPresetIndex=0; RefreshValues(); NotifyChanged(); } };
            preset.SelectionChangeCommitted += delegate {
                int selection = preset.SelectedIndex;
                if (selection == 0) { selectedPresetIndex=0; NotifyChanged(); return; }
                if (selection < 0 || Canvas == null) return;
                Rectangle region = selection == 1 ? new Rectangle(Point.Empty, SourceSize)
                    : CinematicCropGeometry.Fit(SourceSize, CinematicCropGeometry.PresetRatio(selection) / CinematicCropGeometry.SampleAspect(Canvas), Mode == 1);
                SetMargins(region.Left, region.Top, SourceSize.Width - region.Right, SourceSize.Height - region.Bottom, selection);
            };
            RefreshValues();
        }

        internal Dictionary<string,string> State(bool cinema, bool forceCropEnabled = false)
        {
            var state = new Dictionary<string,string>();
            CinematicFrameCore.WriteState(state, cinema, Enabled || forceCropEnabled, Mode, marginText, vertical, horizontal, noCrop);
            state["FG_"+ResizeCore.CropAfterKey] = !cinema && afterResize.Checked ? "true" : "false";
            state["FG_"+ResizeCore.CropMarginsKey] = resizedMargins;
            return state;
        }

        private Dictionary<string,string> CanvasState()
        {
            var state = State(false, true);
            state["FG_"+ResizeCore.CropAfterKey]="false";
            if(UseResizedCanvas)state[CinematicFrameCore.MarginsEnvironment]=resizedMargins;
            return state;
        }

        internal void SetResize(IDictionary<string,string> state)
        {
            resizeEnabled = ResizeCore.Enabled(state);
            resizedCanvas = null;
            resizeError = "";
            afterResize.Enabled = resizeEnabled;
            if (resizeEnabled && media != null)
            {
                var uncropped = new CinematicFramePlan { ActiveWidth=media.Width, ActiveHeight=media.Height };
                ResizePlan resize;
                if (ResizeCore.TryBuild(media,state,uncropped,out resize,out resizeError))
                    resizedCanvas = ResizeCore.CropCanvas(resize.CanvasWidth,resize.CanvasHeight);
            }
            RefreshValues();
        }

        internal void SaveTo(IDictionary<string,string> values)
        {
            values[CinematicFrameCore.CropEnabledKey] = Enabled ? "true" : "false";
            values[CinematicFrameCore.CropModeKey] = Mode == 1 ? "CROP" : "LETTERBOX";
            values[CinematicCropGeometry.PresetKey] = selectedPresetIndex.ToString(CultureInfo.InvariantCulture);
            values[CinematicFrameCore.MarginsKey] = marginText;
            values[ResizeCore.CropAfterKey] = afterResize.Checked ? "true" : "false";
            values[ResizeCore.CropMarginsKey] = resizedMargins;
            values["CINEMATIC_CROP_PER_SIDE"] = vertical;
            values["CINEMATIC_CROP_HORIZONTAL"] = horizontal;
            values["CINEMATIC_NO_CROP"] = noCrop;
        }

        internal void Reset()
        {
            updating = true;
            try { enabled.Checked = false; mode.SelectedIndex = 1; marginText = resizedMargins = "0,0,0,0"; afterResize.Checked = false; vertical = horizontal = noCrop = "0"; selectedPresetIndex=0; }
            finally { updating = false; }
            RefreshValues();
            NotifyChanged();
        }

        private void EditMargin(int edge)
        {
            if (updating) return;
            int[] current = Array.ConvertAll(margins, item => (int)item.Value);
            if (Canvas != null && Mode == 1)
            {
                int extent = (edge & 1) == 0 ? Canvas.Width : Canvas.Height;
                int available = extent - current[(edge + 2) % 4];
                if (((available - current[edge]) & 1) != 0)
                    current[edge] += current[edge] < available - 2 ? 1 : -1;
            }
            SetMargins(current[0], current[1], current[2], current[3]);
        }

        private void SetMargins(int left, int top, int right, int bottom, int presetIndex=0)
        {
            selectedPresetIndex=presetIndex;
            CurrentMargins = CinematicFrameCore.FormatMargins(left, top, right, bottom);
            updating = true;
            try { enabled.Checked = true; }
            finally { updating = false; }
            RefreshValues();
            NotifyChanged();
        }

        private void RefreshValues()
        {
            int[] edges;
            CinematicFrameCore.TryParseMargins(CurrentMargins, out edges);
            updating = true;
            try
            {
                if (Canvas != null)
                {
                    CinematicFramePlan plan;
                    string reason;
                    if (CinematicFrameCore.TryBuild(Canvas, CanvasState(), out plan, out reason))
                    {
                        edges = new[] { plan.ContentX, plan.ContentY, Canvas.Width - plan.ContentX - plan.ContentWidth, Canvas.Height - plan.ContentY - plan.ContentHeight };
                        if (CurrentMargins.Length != 0)
                            CurrentMargins = CinematicFrameCore.FormatMargins(edges[0], edges[1], edges[2], edges[3]);
                        size.Text = string.Format(CultureInfo.InvariantCulture,
                            text("有效区域 {0}×{1}   输出 {2}×{3}", "Active area {0}×{1}   Output {2}×{3}"),
                            plan.ContentWidth, plan.ContentHeight, plan.ActiveWidth, plan.ActiveHeight);
                        if(selectedPresetIndex>0) {
                            Rectangle expected=selectedPresetIndex==1 ? new Rectangle(Point.Empty,SourceSize)
                                : CinematicCropGeometry.Fit(SourceSize,CinematicCropGeometry.PresetRatio(selectedPresetIndex)/
                                    CinematicCropGeometry.SampleAspect(Canvas),Mode==1);
                            if(expected.X!=plan.ContentX || expected.Y!=plan.ContentY ||
                               expected.Width!=plan.ContentWidth || expected.Height!=plan.ContentHeight)
                                selectedPresetIndex=0;
                        }
                        // Geometry can match more than one preset (e.g. 2560x1072 fits 2.39:1
                        // without trimming pixels). Keep the choice that the user actually made.
                        preset.SelectedIndex=selectedPresetIndex;
                    }
                    else { size.Text = text("四边剪裁至少需要保留 2×2 像素。", reason); preset.SelectedIndex = 0; selectedPresetIndex=0; }
                }
                else
                {
                    if (!UseResizedCanvas && CurrentMargins.Length == 0 && noCrop != "1")
                    {
                        int x, y;
                        int.TryParse(horizontal, out x);
                        int.TryParse(vertical, out y);
                        edges = new[] { Math.Max(0,x), Math.Max(0,y), Math.Max(0,x), Math.Max(0,y) };
                    }
                    size.Text = resizeError;
                    preset.SelectedIndex = CurrentMargins == "0,0,0,0" || CurrentMargins.Length == 0 && noCrop == "1" ? 1 : 0;
                }
                if (UseResizedCanvas && Canvas != null)
                    size.Text = text("缩放画布 ", "Resized canvas ") + Canvas.Width + "×" + Canvas.Height + "   " + size.Text;
                else if (afterResize.Checked && !resizeEnabled)
                    size.Text += text("  缩放关闭，使用原图坐标。", "  Resize is off. Using source coordinates.");
                preset.Enabled = Canvas != null;
                for (int i = 0; i < margins.Length; i++)
                {
                    int limit = Canvas == null ? 100000 : ((i & 1) == 0 ? Canvas.Width : Canvas.Height) - 2 - edges[(i + 2) % 4];
                    margins[i].Maximum = Math.Max(edges[i], limit);
                    margins[i].Increment = Mode == 1 && Canvas != null ? 2 : 1;
                    margins[i].Value = edges[i];
                }
            }
            finally { updating = false; }
        }

        private void NotifyChanged()
        {
            if (Changed != null) Changed();
        }
    }

    internal sealed partial class MainForm
    {
        private Dictionary<string,string> PreprocessEnvironment()
        {var state=new Dictionary<string,string>();
            if(cmbCodec!=null) {
                int index=cmbCodec.SelectedIndex;
                if(index==0||index==1||index==3)state["FG_MODE"]=index==0?"AV1":index==1?"HEVC":"H264";
            }
            foreach(string key in PreprocessingCore.Keys)state["FG_"+key]=config.Get(key);
            foreach(string key in ResizeCore.Keys)state["FG_"+key]=config.Get(key);
            WriteCurrentFraming(state);
            if(chkCinematic!=null&&chkCinematic.Checked)state["FG_"+ResizeCore.CropAfterKey]="false";
            return state;
        }

        private void WriteCurrentFraming(IDictionary<string,string> state)
        {
            CinematicFrameCore.WriteState(state, chkCinematic != null && chkCinematic.Checked,
                config.GetBool(CinematicFrameCore.CropEnabledKey), cmbFrameMode == null ? 0 : cmbFrameMode.SelectedIndex,
                config.Get(CinematicFrameCore.MarginsKey), config.Get("CINEMATIC_CROP_PER_SIDE"),
                config.Get("CINEMATIC_CROP_HORIZONTAL"), config.Get("CINEMATIC_NO_CROP"));
        }
        private void ResizeDimensionsForUi(MediaProbeInfo info,ref int width,ref int height)
        {if(info==null||config.Get("RESIZE_MODE")=="OFF")return;var state=PreprocessEnvironment();CinematicFramePlan frame;ResizePlan plan;string error;if(CinematicFrameCore.TryBuild(info,state,out frame,out error)&&ResizeCore.TryBuild(info,state,frame,out plan,out error)){width=plan.Width;height=plan.Height;}}
        private void RefreshPreprocessButton()
        {
            if(btnPreprocess==null)return;
            string tags=config.GetBool("PRE_BILATERAL_ENABLED")?" [bilateral]":"";
            string mode=config.Get("RESIZE_MODE");
            if(mode!="OFF") {
                string target=mode;
                if(mode=="CUSTOM") {
                    target=config.Get("RESIZE_FACTOR");
                    if(target.Length==0)target=config.Get("RESIZE_WIDTH")+"x"+config.Get("RESIZE_HEIGHT");
                } else if(mode=="720"||mode=="1080"||mode=="1440"||mode=="2160")target+="p";
                tags+=" [Resize "+target+"]";
            }
            if (config.GetBool(CinematicFrameCore.CropEnabledKey) && (chkCinematic == null || !chkCinematic.Checked)) {
                string label=CinematicCropGeometry.PresetShortName(config.GetInt(CinematicCropGeometry.PresetKey,0),
                    string.Equals(lang.Code,"zh-CN",StringComparison.OrdinalIgnoreCase));
                tags += (cmbFrameMode != null && cmbFrameMode.SelectedIndex == 1 ? UiText(" [剪裁 ", " [Crop ") : UiText(" [黑边 ", " [Letterbox ")) + label + "]";
            }
            btnPreprocess.Text=UiText("前处理","Preprocessing")+(tags.Length==0?"...":tags);
            SetEffectButtonState(btnPreprocess,tags.Length>0);
        }
        private ContextMenuStrip BuildPreprocessMenu()
        {
            var menu=new ContextMenuStrip();
            var bilateral=menu.Items.Add(UiText("关闭 Bilateral","Turn off Bilateral"),null,delegate {ClearPreprocessing(true,false);});
            var resize=menu.Items.Add(UiText("关闭缩放","Turn off resize"),null,delegate {ClearPreprocessing(false,true);});
            var crop=menu.Items.Add(UiText("关闭剪裁","Turn off crop"),null,delegate {ClearPreprocessing(false,false,true);});
            menu.Items.Add(new ToolStripSeparator());
            var all=menu.Items.Add(UiText("全部清除","Clear all"),null,delegate {ClearPreprocessing(true,true,true);});
            menu.Opening+=delegate
            {
                bilateral.Enabled=config.GetBool("PRE_BILATERAL_ENABLED");
                resize.Enabled=config.Get("RESIZE_MODE")!="OFF";
                crop.Enabled=config.GetBool(CinematicFrameCore.CropEnabledKey);
                all.Enabled=bilateral.Enabled||resize.Enabled||crop.Enabled;
            };
            return menu;
        }

        private void ClearPreprocessing(bool bilateral,bool resize,bool crop=false)
        {
            var values=new Dictionary<string,string>();
            if(bilateral)values[PreprocessingCore.Keys[0]]=PreprocessingCore.Defaults[0];
            if(resize)values[ResizeCore.Keys[0]]=ResizeCore.Defaults[0];
            if(crop)values[CinematicFrameCore.CropEnabledKey]="false";
            try {ApplyPreprocessValues(values);}
            catch(Exception ex){FgsMessageBox.Show(this,ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        }

        private void ApplyPreprocessValues(Dictionary<string,string> values)
        {
            config.Save(values);
            RefreshPreprocessUi();
        }

        private void RefreshPreprocessUi()
        {
            UpdateCinematicCropLabels();
            RefreshPreprocessButton();
            UpdateFilmBorderButton();
            UpdateBitrateDisplays();
            RefreshAv1GrainTableForMedia();
            UpdateHdrRouteStatus(GetSelectedMediaInfo());
            UpdateFileSettingsLabels();
        }

        private void ShowPreprocessDialog()
        {
            using(var dialog=new SettingsDialogForm()) {
                dialog.Text=UiText("前处理","Preprocessing");dialog.Font=Font;
                dialog.AutoScaleMode=AutoScaleMode.Dpi;dialog.StartPosition=FormStartPosition.CenterParent;
                dialog.Size=new Size(820,450);dialog.MinimumSize=new Size(780,430);
                var shell=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(10),ColumnCount=1,RowCount=2};
                shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                shell.RowStyles.Add(new RowStyle(SizeType.Percent,100));shell.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
                var tabs=new ThemeTabControl {Name="PREPROCESS_TABS",Dock=DockStyle.Fill};
                var filterPage=new TabPage(UiText("滤波","Filtering")) {Name="PREPROCESS_FILTER_PAGE"};
                var resizePage=new TabPage(UiText("缩放","Resize")) {Name="PREPROCESS_RESIZE_PAGE"};
                var cropTab=new TabPage(UiText("剪裁","Crop")) {Name="PREPROCESS_CROP_PAGE"};
                tabs.TabPages.Add(filterPage);tabs.TabPages.Add(resizePage);tabs.TabPages.Add(cropTab);shell.Controls.Add(tabs,0,0);
                string previewInput=GetPreviewVideoPath();
                MediaProbeInfo previewMedia=null;
                if(!string.IsNullOrEmpty(previewInput))mediaProbeCache.TryGetValue(previewInput,out previewMedia);
                var cropPage=new PreprocessCropPage(cropTab,config,cmbFrameMode.SelectedIndex,previewMedia,previewInput,UiText,
                    string.Equals(lang.Code,"zh-CN",StringComparison.OrdinalIgnoreCase));
                bool cinemaDraft=chkCinematic.Checked;
                var grid=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(14),ColumnCount=2,RowCount=5};
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,68));grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,32));
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
                for(int i=0;i<3;i++)grid.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
                grid.RowStyles.Add(new RowStyle(SizeType.Percent,100));
                filterPage.Controls.Add(grid);
                var resizeGrid=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(14),ColumnCount=2,RowCount=7};
                resizeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,68));resizeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,32));
                for(int i=0;i<6;i++)resizeGrid.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
                resizeGrid.RowStyles.Add(new RowStyle(SizeType.Percent,100));resizePage.Controls.Add(resizeGrid);
                var enabled=new ThemeCheckBox {Name="BILATERAL_ENABLED",Text=UiText("启用 Bilateral（CPU 双边滤波）","Enable Bilateral (CPU)"),Dock=DockStyle.Fill,Checked=config.GetBool("PRE_BILATERAL_ENABLED")};
                grid.Controls.Add(enabled,0,0);grid.SetColumnSpan(enabled,2);
                var spatial=new ThemeTextBox {Name="BILATERAL_SIGMA_S",Dock=DockStyle.Fill,Text=config.Get("PRE_BILATERAL_SIGMA_S")};
                var range=new ThemeTextBox {Name="BILATERAL_SIGMA_R",Dock=DockStyle.Fill,Text=config.Get("PRE_BILATERAL_SIGMA_R")};
                var planes=new ThemeIntegerCombo(0,15,1,new int[]{1,2,4,6,7,15},false) {Name="BILATERAL_PLANES",Dock=DockStyle.Fill};
                int planeValue;if(int.TryParse(config.Get("PRE_BILATERAL_PLANES"),out planeValue)&&planeValue>=0&&planeValue<=15)planes.Value=planeValue;
                grid.Controls.Add(new ThemeLabel {Text=UiText("sigmaS · 空间平滑范围（0–512）","sigmaS · Spatial range (0–512)"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,1);grid.Controls.Add(spatial,1,1);
                grid.Controls.Add(new ThemeLabel {Text=UiText("sigmaR · 亮度差异容忍度（0–1）","sigmaR · Range tolerance (0–1)"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,2);grid.Controls.Add(range,1,2);
                grid.Controls.Add(new ThemeLabel {Text=UiText("planes · 通道位掩码（0–15）","planes · Plane bitmask (0–15)"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,3);grid.Controls.Add(planes,1,3);
                var help=new ThemeLabel {Name="PREPROCESS_FILTER_HELP",Dock=DockStyle.Fill,ForeColor=ColorMuted,Padding=new Padding(0,8,0,0),Text=UiText("默认 3 / 0.05 / 1：仅柔化亮度通道。sigmaS 越大影响范围越广；sigmaR 越大越容易抹平纹理。\nplanes：1=Y，2=U，4=V，6=UV，7=YUV；0=不处理。CPU 滤镜可能降低处理速度；GPU 解码仍按原设置运行。","Default 3 / 0.05 / 1: luma only. Larger sigmaS widens smoothing; larger sigmaR removes more texture.\nplanes: 1=Y, 2=U, 4=V, 6=UV, 7=YUV; 0=none. CPU filtering may reduce speed; GPU decode remains available.")};
                grid.Controls.Add(help,0,4);grid.SetColumnSpan(help,2);
                var resizeMode=new ThemeComboBox {Name="RESIZE_MODE",Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList};
                string[] modes={"OFF","720","1080","1440","2160","2x","4x","1/2","1/4","CUSTOM"};
                resizeMode.Items.AddRange(new object[]{UiText("关闭","Off"),"720p · 1280 x 720","1080p · 1920 x 1080","1440p · 2560 x 1440","2160p · 3840 x 2160","2x","4x","1/2","1/4",UiText("自定义","Custom")});
                resizeMode.SelectedIndex=Math.Max(0,Array.IndexOf(modes,config.Get("RESIZE_MODE")));
                var resizeWidth=new ThemeTextBox {Name="RESIZE_WIDTH",Dock=DockStyle.Fill,Text=config.Get("RESIZE_WIDTH")};
                var resizeHeight=new ThemeTextBox {Name="RESIZE_HEIGHT",Dock=DockStyle.Fill,Text=config.Get("RESIZE_HEIGHT")};
                var factorBox=new ThemeTextBox {Name="RESIZE_FACTOR",Dock=DockStyle.Fill,Text=config.Get("RESIZE_FACTOR")};
                var algorithm=new ThemeComboBox {Name="RESIZE_ALGORITHM",Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList};
                string[] algorithms={"LANCZOS","EWA_SHARP","EWA_SHARP_HQ"};
                foreach(string key in algorithms)algorithm.Items.Add(ResizeCore.AlgorithmName(key));
                algorithm.SelectedIndex=Math.Max(0,Array.IndexOf(algorithms,config.Get("RESIZE_ALGORITHM")));
                var keepAspect=new ThemeCheckBox {Name="RESIZE_KEEP_ASPECT",Text=UiText("保持显示宽高比（取消可能拉伸）","Keep display aspect ratio (uncheck to stretch)"),Dock=DockStyle.Fill,Checked=config.GetBool("RESIZE_KEEP_ASPECT")};
                resizeGrid.Controls.Add(new ThemeLabel {Text=UiText("画面缩放","Resize target"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,0);resizeGrid.Controls.Add(resizeMode,1,0);
                resizeGrid.Controls.Add(new ThemeLabel {Text=UiText("自定义宽度（2–16384）","Custom width (2–16384)"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,1);resizeGrid.Controls.Add(resizeWidth,1,1);
                resizeGrid.Controls.Add(new ThemeLabel {Text=UiText("自定义高度（2–16384）","Custom height (2–16384)"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,2);resizeGrid.Controls.Add(resizeHeight,1,2);
                resizeGrid.Controls.Add(new ThemeLabel {Text=UiText("自定义倍数（如 1.5x、3/2、1/3；留空使用宽高）","Custom scale (e.g. 1.5x, 3/2, 1/3; blank uses width and height)"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,3);resizeGrid.Controls.Add(factorBox,1,3);
                resizeGrid.Controls.Add(new ThemeLabel {Text=UiText("缩放算法 · Vulkan","Scaling algorithm · Vulkan"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,4);resizeGrid.Controls.Add(algorithm,1,4);
                resizeGrid.Controls.Add(keepAspect,0,5);resizeGrid.SetColumnSpan(keepAspect,2);
                var resizeSummary=new ThemeLabel {Name="RESIZE_SUMMARY",Dock=DockStyle.Fill,ForeColor=ColorMuted,Padding=new Padding(0,8,0,0)};resizeGrid.Controls.Add(resizeSummary,0,6);resizeGrid.SetColumnSpan(resizeSummary,2);
                Func<Dictionary<string,string>> resizeValues=delegate {return new Dictionary<string,string>{{"RESIZE_MODE",modes[resizeMode.SelectedIndex]},{"RESIZE_WIDTH",resizeWidth.Text.Trim()},{"RESIZE_HEIGHT",resizeHeight.Text.Trim()},{"RESIZE_KEEP_ASPECT",keepAspect.Checked?"true":"false"},{"RESIZE_FACTOR",factorBox.Text.Trim()},{"RESIZE_ALGORITHM",algorithms[algorithm.SelectedIndex]}};};
                Action updateResize=delegate {
                    var canvasState=PreprocessEnvironment();
                    canvasState["FG_"+ResizeCore.CropAfterKey]="false";
                    foreach(var pair in resizeValues())canvasState["FG_"+pair.Key]=pair.Value;
                    cropPage.SetResize(canvasState);
                    bool custom=modes[resizeMode.SelectedIndex]=="CUSTOM";
                    factorBox.Enabled=custom;
                    resizeWidth.Enabled=resizeHeight.Enabled=custom&&factorBox.Text.Trim().Length==0;
                    keepAspect.Enabled=algorithm.Enabled=resizeMode.SelectedIndex!=0;
                    string note=UiText("按边界框保持比例，偶数对齐；不自动补边。颗粒在缩放后添加。","Fit within bounds, even dimensions; no padding. Grain follows resize.");
                    MediaProbeInfo media=GetBitrateMediaContext();
                    if(media!=null&&resizeMode.SelectedIndex!=0) {
                        var state=PreprocessEnvironment();
                        foreach(var pair in cropPage.State(cinemaDraft))state[pair.Key]=pair.Value;
                        foreach(var pair in resizeValues())state["FG_"+pair.Key]=pair.Value;
                        CinematicFramePlan frame;ResizePlan plan;string error;
                        if(CinematicFrameCore.TryBuild(media,state,out frame,out error)) {
                            int cw=frame.ActiveWidth,ch=frame.ActiveHeight;
                            if(ResizeCore.TryBuild(media,state,frame,out plan,out error)) {
                                string original=media.Width+"x"+media.Height;
                                string middle=ResizeCore.CropAfter(state)?plan.CanvasWidth+"x"+plan.CanvasHeight:cw+"x"+ch;
                                note=original+" → "+middle+" → "+plan.Width+"x"+plan.Height+"\n"+note;
                            }
                        }
                        if(error.Length>0)note=error;
                    }
                    resizeSummary.Text=note;
                };
                cropPage.Changed+=delegate { if(cropPage.Enabled)cinemaDraft=false;updateResize(); };
                resizeMode.SelectedIndexChanged+=delegate {updateResize();};resizeWidth.TextChanged+=delegate {updateResize();};resizeHeight.TextChanged+=delegate {updateResize();};factorBox.TextChanged+=delegate {updateResize();};algorithm.SelectedIndexChanged+=delegate {updateResize();};keepAspect.CheckedChanged+=delegate {updateResize();};updateResize();
                var buttons=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false};
                var cancel=new ThemeButton {Name="PREPROCESS_CANCEL",Text=UiText("取消","Cancel"),DialogResult=DialogResult.Cancel,Width=88};
                var ok=new ThemeButton {Name="PREPROCESS_OK",Text=UiText("确定","OK"),Width=88};var reset=new ThemeButton {Name="PREPROCESS_DEFAULTS",Text=UiText("恢复默认值","Defaults"),Width=118};
                reset.Click+=delegate {if(tabs.SelectedTab==filterPage){enabled.Checked=false;spatial.Text="3";range.Text="0.05";planes.Value=1;}else if(tabs.SelectedTab==cropTab){cropPage.Reset();}else{resizeMode.SelectedIndex=0;resizeWidth.Text="1920";resizeHeight.Text="1080";factorBox.Text="";algorithm.SelectedIndex=0;keepAspect.Checked=true;}};
                enabled.CheckedChanged+=delegate {spatial.Enabled=range.Enabled=planes.Enabled=enabled.Checked;};
                spatial.Enabled=range.Enabled=planes.Enabled=enabled.Checked;
                ok.Click+=delegate {
                    if(!planes.ValidateInput())return;
                    var values=new Dictionary<string,string> { {"PRE_BILATERAL_ENABLED",enabled.Checked?"true":"false"},{"PRE_BILATERAL_SIGMA_S",spatial.Text.Trim()},{"PRE_BILATERAL_SIGMA_R",range.Text.Trim()},{"PRE_BILATERAL_PLANES",planes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)} };
                    try {
                        foreach(var pair in resizeValues())values[pair.Key]=pair.Value;
                        cropPage.SaveTo(values);
                        foreach(var pair in values)PresetCore.ValidateConfig(pair.Key,pair.Value);
                        MediaProbeInfo media=GetBitrateMediaContext();
                        if(media!=null) {
                            var state=PreprocessEnvironment();
                            foreach(var pair in resizeValues())state["FG_"+pair.Key]=pair.Value;
                            foreach(var pair in cropPage.State(cinemaDraft))state[pair.Key]=pair.Value;
                            CinematicFramePlan frame;ResizePlan plan;string error;
                            if(!CinematicFrameCore.TryBuild(media,state,out frame,out error)||!ResizeCore.TryBuild(media,state,frame,out plan,out error))
                                throw new InvalidOperationException(error);
                        }
                        config.Save(values);
                        cmbFrameMode.SelectedIndex=cropPage.Mode;
                        if(chkCinematic.Checked!=cinemaDraft)chkCinematic.Checked=cinemaDraft;
                        else RefreshPreprocessUi();
                        dialog.DialogResult=DialogResult.OK;dialog.Close();
                    }catch(Exception ex){FgsMessageBox.Show(dialog,ex.Message,dialog.Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);}
                };
                buttons.Controls.Add(cancel);buttons.Controls.Add(ok);buttons.Controls.Add(reset);shell.Controls.Add(buttons,0,1);
                dialog.Controls.Add(shell);dialog.AcceptButton=ok;dialog.CancelButton=cancel;
                AttachThemeWindow(dialog);AttachPreprocessWindowMemory(dialog);dialog.ShowDialog(this);
            }
        }
        private void AttachPreprocessWindowMemory(Form dialog)
        {
            bool ready=false;FormWindowState remembered=FormWindowState.Normal;
            dialog.Load+=delegate {
                if(!SuppressWindowMemory)try {
                    string[] parts=config.Get("PREPROCESS_WINDOW").Split(',');int x,y,w,h;
                    if(parts.Length==5&&int.TryParse(parts[0],out x)&&int.TryParse(parts[1],out y)&&int.TryParse(parts[2],out w)&&int.TryParse(parts[3],out h)&&w>0&&h>0) {
                        var areas=new List<Rectangle>();foreach(Screen screen in Screen.AllScreens)areas.Add(screen.WorkingArea);
                        dialog.StartPosition=FormStartPosition.Manual;dialog.Bounds=FitWindowBounds(new Rectangle(x,y,w,h),areas.ToArray(),dialog.MinimumSize);
                        remembered=parts[4]=="Maximized"?FormWindowState.Maximized:FormWindowState.Normal;dialog.WindowState=remembered;
                    }
                }catch(Exception ex){System.Diagnostics.Trace.WriteLine(ex.Message);}
                ready=true;
            };
            dialog.Resize+=delegate {if(ready&&dialog.WindowState!=FormWindowState.Minimized)remembered=dialog.WindowState;};
            dialog.FormClosed+=delegate {
                if(!ready||SuppressWindowMemory)return;
                try {
                    Rectangle b=dialog.WindowState==FormWindowState.Normal?dialog.Bounds:dialog.RestoreBounds;
                    config.SaveValue("PREPROCESS_WINDOW",string.Join(",",new[]{b.X.ToString(System.Globalization.CultureInfo.InvariantCulture),b.Y.ToString(System.Globalization.CultureInfo.InvariantCulture),b.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),b.Height.ToString(System.Globalization.CultureInfo.InvariantCulture),remembered==FormWindowState.Maximized?"Maximized":"Normal"}));
                }catch(Exception ex){System.Diagnostics.Trace.WriteLine(ex.Message);}
            };
        }
    }
}

namespace FilmGrainStudioPreview
{
    internal sealed class ResizePlan
    {
        internal bool Enabled;
        internal int Width,Height,CanvasWidth,CanvasHeight;
        internal string Prefix="";
        internal string Algorithm="LANCZOS";
        internal string HdrTransfer="";
    }
    // Geometry only: never rewrites source metadata, frame rates or timestamps.
    internal static class ResizeCore
    {
        internal const string CropAfterKey = "RESIZE_CROP_AFTER";
        internal const string CropMarginsKey = "RESIZE_CROP_MARGINS";
        internal static readonly string[] Keys={"RESIZE_MODE","RESIZE_WIDTH","RESIZE_HEIGHT","RESIZE_KEEP_ASPECT","RESIZE_ALGORITHM","RESIZE_FACTOR",CropAfterKey,CropMarginsKey};
        internal static readonly string[] Defaults={"OFF","1920","1080","true","LANCZOS","","false","0,0,0,0"};
        static readonly object Sync=new object();
        static readonly HashSet<string> Verified=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string Get(IDictionary<string,string> state,string key,string fallback)
        {string v;return state!=null&&state.TryGetValue("FG_"+key,out v)?v:fallback;}
        internal static bool Enabled(IDictionary<string,string> state){return Get(state,Keys[0],"OFF")!="OFF";}
        internal static bool CropAfter(IDictionary<string,string> state)
        {
            return Enabled(state) && Get(state,CropAfterKey,"false")=="true";
        }

        internal static MediaProbeInfo CropCanvas(int width,int height)
        {
            return new MediaProbeInfo { Width=width, Height=height, SampleAspectRatio="1:1" };
        }

        static int Even(double n){return Math.Max(2,(int)Math.Round(n/2,MidpointRounding.AwayFromZero)*2);}
        internal static string AlgorithmName(string algorithm)
        {
            return algorithm=="EWA_SHARP_HQ"?"EWA Lanczos SharpHQ":algorithm=="EWA_SHARP"?"EWA Lanczos Sharp":"Lanczos";
        }

        internal static bool TryParseFactor(string value,out double factor)
        {
            factor=0;
            string expression=value.Trim().ToLowerInvariant().Replace('×','x');
            if(expression.EndsWith("x",StringComparison.Ordinal)&&!expression.Contains("/"))
                expression=expression.Substring(0,expression.Length-1).TrimEnd();
            string[] terms=expression.Split('/');
            double numerator,denominator=1;
            if(terms.Length>2 || (terms.Length==2&&!double.TryParse(terms[1].Trim(),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out denominator)) ||
               !double.TryParse(terms[0].Trim(),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out numerator) ||
               denominator<=0 || numerator<=0)return false;
            factor=numerator/denominator;
            return factor>0&&!double.IsInfinity(factor);
        }

        internal static bool TryBuild(MediaProbeInfo info,IDictionary<string,string> state,CinematicFramePlan frame,out ResizePlan plan,out string reason,bool? preserveHdr=null)
        {
            plan=new ResizePlan {Width=frame.ActiveWidth,Height=frame.ActiveHeight};reason="";
            if(!Enabled(state))return true;
            string mode=Get(state,Keys[0],"OFF");int bw,bh;
            string factorText=mode=="CUSTOM"?Get(state,Keys[5],""):mode;
            bool relative=mode=="CUSTOM"?factorText.Length>0:mode=="2x"||mode=="4x"||mode=="1/2"||mode=="1/4";
            if(relative) {
                double factor;
                if(!TryParseFactor(factorText,out factor)){reason="无效缩放倍数。示例：1.5x、3/2、1/3。";return false;}
                double width=frame.ActiveWidth*factor,height=frame.ActiveHeight*factor;
                if(width<2||height<2||width>16384||height>16384){reason="缩放后的宽高必须为 2–16384。";return false;}
                bw=Even(width);bh=Even(height);
            } else if(mode=="CUSTOM") {
                if(!int.TryParse(Get(state,Keys[1],"1920"),out bw)||!int.TryParse(Get(state,Keys[2],"1080"),out bh)||bw<2||bh<2||bw>16384||bh>16384)
                {reason="Resize 自定义宽高必须为 2–16384。";return false;}
            } else {
                if(!int.TryParse(mode,out bh)||(bh!=720&&bh!=1080&&bh!=1440&&bh!=2160)){reason="无效 Resize 档位。";return false;}
                bw=bh==720?1280:bh==1080?1920:bh==1440?2560:3840;
            }
            string keep=Get(state,Keys[3],"true");
            if(keep!="true"&&keep!="false"){reason="无效 Resize 宽高比设置。";return false;}
            int inputW=frame.ActiveWidth,inputH=frame.ActiveHeight;
            if(inputW<2||inputH<2){reason="Resize 输入尺寸无效。";return false;}
            double sar=1;string[] parts=(info.SampleAspectRatio??"").Split(':');double sn,sd;
            if(parts.Length==2&&double.TryParse(parts[0],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out sn)&&double.TryParse(parts[1],System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out sd)&&sn>0&&sd>0)sar=sn/sd;
            int w=bw-bw%2,h=bh-bh%2;
            if(keep=="true") {
                double ratio=inputW*sar/inputH;
                if(relative){
                    double displayWidth=h*ratio;
                    if(displayWidth<2||displayWidth>16384){reason="缩放后的显示宽度必须为 2–16384。";return false;}
                    w=Even(displayWidth);
                }
                else if(ratio>=bw/(double)bh){w=bw-bw%2;h=Math.Min(bh-bh%2,Even(w/ratio));}
                else {h=bh-bh%2;w=Math.Min(bw-bw%2,Even(h*ratio));}
            }
            string codec=Get(state,"MODE","");int limit=codec=="H264"?4096:codec=="AV1"||codec=="HEVC"?8192:16384;
            if(w>limit||h>limit){reason="Resize 输出尺寸超过当前编码路线限制："+limit;return false;}
            string algorithm=Get(state,Keys[4],"LANCZOS");
            if(algorithm!="LANCZOS"&&algorithm!="EWA_SHARP"&&algorithm!="EWA_SHARP_HQ"){reason="无效缩放算法。";return false;}
            // Source-space crops precede scaling. Output-space crops follow scaling.
            string crop=frame.Filter.StartsWith(",crop=",StringComparison.Ordinal)?frame.Filter.Substring(1)+",":"";
            string format="yuv420p10le";
            if((info.PixelFormat??"").IndexOf("12",StringComparison.Ordinal)>=0||(info.PixelFormat??"").IndexOf("16",StringComparison.Ordinal)>=0)format="yuv420p16le";
            plan.Enabled=true;plan.Width=plan.CanvasWidth=w;plan.Height=plan.CanvasHeight=h;
            plan.Algorithm=algorithm;
            bool keepHdr=preserveHdr??info.IsHdr;
            if(keepHdr)plan.HdrTransfer=info.ColorTransfer;
            string scaleFilter=BuildFilters(algorithm,w,h,format,keepHdr?info:null)+",setsar=1,";
            plan.Prefix=crop+scaleFilter;
            string suffix="_RS"+w+"x"+h+(algorithm=="EWA_SHARP_HQ"?"_EWAHQ":algorithm=="EWA_SHARP"?"_EWA":"");

            if(CropAfter(state))
            {
                var outputState=new Dictionary<string,string>(state);
                outputState["FG_"+CropAfterKey]="false";
                outputState[CinematicFrameCore.MarginsEnvironment]=Get(state,CropMarginsKey,"0,0,0,0");
                CinematicFramePlan outputFrame;
                if(!CinematicFrameCore.TryBuild(CropCanvas(w,h),outputState,out outputFrame,out reason))return false;
                frame.ContentX=outputFrame.ContentX;frame.ContentY=outputFrame.ContentY;
                frame.ContentWidth=outputFrame.ContentWidth;frame.ContentHeight=outputFrame.ContentHeight;
                frame.ActiveWidth=plan.Width=outputFrame.ActiveWidth;
                frame.ActiveHeight=plan.Height=outputFrame.ActiveHeight;
                frame.Filter=outputFrame.Filter;
                frame.FileSuffix=suffix+outputFrame.FileSuffix;
                if(frame.Filter.StartsWith(",crop=",StringComparison.Ordinal))
                {
                    string exactCrop=frame.Filter.Substring(1);
                    if(((outputFrame.ContentX | outputFrame.ContentY) & 1) != 0)
                    {
                        // Exact odd offsets on 16-bit planar 4:2:0 can misalign chroma samples.
                        // Crop full-resolution chroma first and subsample the selected image.
                        plan.Prefix=BuildFilters(algorithm,w,h,format,keepHdr?info:null,exactCrop)+",setsar=1,";
                    }
                    else plan.Prefix+=exactCrop+",";
                    frame.Filter="";
                }
                return true;
            }

            bool bars=frame.Filter.StartsWith(",drawbox=",StringComparison.Ordinal);
            if (bars && frame.HasExplicitMargins)
            {
                int l = (int)Math.Round(frame.ContentX * (double)w / inputW);
                int t = (int)Math.Round(frame.ContentY * (double)h / inputH);
                int r = (int)Math.Round((frame.ContentX + frame.ContentWidth) * (double)w / inputW);
                int b = (int)Math.Round((frame.ContentY + frame.ContentHeight) * (double)h / inputH);
                l = Math.Min(l, w - 2);
                t = Math.Min(t, h - 2);
                CinematicFrameCore.SetBars(frame, w, h, l, t, w - Math.Max(l + 2, r), h - Math.Max(t + 2, b));
            }
            else if(bars) {
                int x=Even(frame.ContentX*w/(double)inputW),y=Even(frame.ContentY*h/(double)inputH);
                if(frame.ContentX==0)x=0;if(frame.ContentY==0)y=0;
                frame.Filter="";
                if(x>0)frame.Filter+=",drawbox=x=0:y=0:w="+x+":h=ih:color=black:t=fill,drawbox=x=iw-"+x+":y=0:w="+x+":h=ih:color=black:t=fill";
                if(y>0)frame.Filter+=",drawbox=x=0:y=0:w=iw:h="+y+":color=black:t=fill,drawbox=x=0:y=ih-"+y+":w=iw:h="+y+":color=black:t=fill";
            } else frame.Filter="";
            frame.ActiveWidth=w;frame.ActiveHeight=h;
            if (!bars || !frame.HasExplicitMargins)
            {
                frame.ContentX=bars&&frame.ContentX>0?Even(frame.ContentX*w/(double)inputW):0;frame.ContentY=bars&&frame.ContentY>0?Even(frame.ContentY*h/(double)inputH):0;
                frame.ContentWidth=bars?Even(frame.ContentWidth*w/(double)inputW):w;frame.ContentHeight=bars?Even(frame.ContentHeight*h/(double)inputH):h;
            }
            frame.FileSuffix+=suffix;
            return true;
        }
        static string BuildFilters(string algorithm,int w,int h,string format,MediaProbeInfo hdr,string fullChromaCrop=null)
        {
            const string options=":normalize_sar=0:peak_detect=0";
            string size="w="+w+":h="+h;
            string scaler=algorithm=="LANCZOS"?"lanczos":"ewa_lanczossharp";
            string downscaler=algorithm=="LANCZOS"?"lanczos":"ewa_lanczos";
            string yuv444=format=="yuv420p16le"?"yuv444p16le":"yuv444p10le";
            if(algorithm!="EWA_SHARP_HQ")
            {
                string filters="format="+format+",hwupload,libplacebo="+size+":upscaler="+scaler+":downscaler="+downscaler+options+":format="+format+",hwdownload,format="+format;
                if(fullChromaCrop!=null)filters+=",format="+yuv444+","+fullChromaCrop+",format="+format;
                return filters;
            }

            string colors="";
            if(hdr!=null) {
                string matrix=hdr.ColorSpace=="bt2020c"?"bt2020c":"bt2020nc";
                string range=hdr.ColorRange=="pc"?"pc":"tv";
                string transfer=hdr.ColorTransfer=="arib-std-b67"?"arib-std-b67":"smpte2084";
                string primaries=hdr.ColorPrimaries=="smpte432"?"smpte432":hdr.ColorPrimaries=="smpte431"?"smpte431":"bt2020";
                colors=":colorspace="+matrix+":range="+range+":color_primaries="+primaries+":color_trc="+transfer+
                       ":apply_dolbyvision=0:sigmoid=0";
            }
            return "libplacebo="+size+":upscaler="+scaler+":downscaler="+downscaler+
                   ":antiringing=0.6:dithering=blue"+options+":format="+yuv444+colors+
                   (fullChromaCrop==null?"":","+fullChromaCrop)+",format="+format;
        }

        internal static bool TryCheckTool(string ffmpeg,ResizePlan plan,out string reason)
        {
            reason="";
            try {
                var file=new System.IO.FileInfo(ffmpeg);string id=file.FullName+"|"+file.Length+"|"+file.LastWriteTimeUtc.Ticks+"|"+VulkanDevicePolicy.DeviceName+"|"+plan.Algorithm+"|"+plan.HdrTransfer;
                lock(Sync)if(Verified.Contains(id))return true;
                string output,detail;
                var hdr=plan.HdrTransfer.Length>0?new MediaProbeInfo{ColorTransfer=plan.HdrTransfer}:null;
                string filter=BuildFilters(plan.Algorithm,128,128,"yuv420p10le",hdr);
                if(hdr!=null)filter="format=yuv420p10le,setparams=range=tv:color_primaries=bt2020:color_trc="+hdr.ColorTransfer+":colorspace=bt2020nc,"+filter;
                string args="-hide_banner -nostdin -v error -init_hw_device "+SharedFrucCore.Quote(VulkanDevicePolicy.Spec("vk"))+
                    " -filter_hw_device vk -f lavfi -i testsrc2=s=64x64:d=0.04 -vf "+SharedFrucCore.Quote(filter)+" -frames:v 1 -f null -";
                if(!SharedFrucCore.Run(ffmpeg,args,15000,out output,out detail)){reason="Resize Vulkan/libplacebo "+AlgorithmName(plan.Algorithm)+" 检查失败（不自动替换算法）："+detail+" / "+output;return false;}
                lock(Sync)Verified.Add(id);return true;
            }catch(Exception ex){reason="Resize 检查失败："+ex.Message;return false;}
        }
    }
}
