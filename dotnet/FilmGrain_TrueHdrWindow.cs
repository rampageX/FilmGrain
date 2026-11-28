using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace FilmGrainStudioPreview
{
    internal sealed partial class MainForm
    {
        private void ShowHdrSourceShortcut()
        {
            MediaProbeInfo source = GetSelectedMediaInfo();
            if (source == null || !source.HasVideo) return;
            if (source.IsHdr) ShowTrueHdrDialog(source, true);
            else if (TrueHdrCore.Eligible(source) && bridgeTaskCoordinator != null && !bridgeTaskCoordinator.IsActive)
                ShowTrueHdrDialog(source);
        }

        private void ShowTrueHdrDialog(MediaProbeInfo source, bool toSdr = false)
        {
            string settingsPath = Path.Combine(appRoot, "Utils", toSdr ? "HdrToSdr.settings.json" : "TrueHDR.settings.json");
            var serializer = new JavaScriptSerializer();
            var options = new TrueHdrOptions();
            try
            {
                if (File.Exists(settingsPath))
                    options = serializer.Deserialize<TrueHdrOptions>(File.ReadAllText(settingsPath)) ?? options;
            }
            catch (IOException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            if (options.Picture == null || options.Picture.Length != TrueHdrOptions.Names.Length)
                options.Picture = (int[])TrueHdrOptions.Defaults.Clone();
            if (options.Advanced == null) options.Advanced = new Dictionary<string, string>();

            if (toSdr) config.Load();

            using (var dlg = new SettingsDialogForm())
            using (var tips = new ThemeToolTip())
            using (var refresh = new System.Windows.Forms.Timer { Interval = 250 })
            {
                dlg.Text = toSdr ? "HDR → SDR" : "SDR → TrueHDR";
                dlg.BackColor = selectedDarkTheme ? UiTheme.Surface : SystemColors.Control;
                dlg.ForeColor = selectedDarkTheme ? UiTheme.TextColor : SystemColors.ControlText;
                dlg.Font = UiFont(9f, FontStyle.Regular);
                dlg.AutoScaleMode = AutoScaleMode.Font;
                dlg.ClientSize = new Size(1328, 614);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = dlg.MinimizeBox = dlg.ShowInTaskbar = false;
                dlg.Opacity = 0;
                dlg.SuspendLayout();

                var settings = new Panel { Location = new Point(830, 0), Size = new Size(498, 614) };
                dlg.Controls.Add(settings);
                settings.Controls.Add(new ThemeLabel { Text = Path.GetFileName(source.PathValue), AutoEllipsis = true,
                    Location = new Point(16, 14), Size = new Size(465, 24) });
                var codec = new ThemeComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(16, 44), Size = new Size(238, 26) };
                codec.Items.AddRange(toSdr ? new object[] { "HEVC NVENC · SDR Main10", "AV1 NVENC · SDR 10-bit" } :
                    new object[] { "HEVC NVENC · HDR10 Main10", "AV1 NVENC · HDR10 10-bit" });
                codec.SelectedIndex = options.Codec == "AV1" ? 1 : 0;
                var preset = new ThemeComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(266, 44), Size = new Size(95, 26) };
                preset.Items.AddRange(new object[] { "p1", "p2", "p3", "p4", "p5", "p6", "p7" });
                int presetIndex = preset.Items.IndexOf(options.Preset);
                preset.SelectedIndex = presetIndex < 0 ? 5 : presetIndex;
                var container = new ThemeComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(373, 44), Size = new Size(96, 26) };
                container.Items.AddRange(new object[] { "MKV", "MP4" });
                container.SelectedIndex = options.Mp4 ? 1 : 0;
                var rate = new ThemeComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(16, 80), Size = new Size(148, 26) };
                rate.Items.AddRange(new object[] { "VBR (kbps)", "CQ" });
                rate.SelectedIndex = options.Cq ? 1 : 0;
                var target = new ThemeTextBox { Text = options.Target, Location = new Point(176, 80), Size = new Size(94, 26), AutoSize = false };
                var auto = new ThemeCheckBox { Text = lang.T("encode.auto"), Checked = options.Auto, Location = new Point(281, 80), Size = new Size(78, 26) };
                var motion = new ThemeCheckBox { Text = UiText("高动态", "High motion"), Checked = options.HighMotion, Location = new Point(373, 80), Size = new Size(78, 26) };
                var pending = PendingQueueInputs(toSdr);
                bool hasQueue = listFiles.Items.Count > 1;
                var ai = new ThemeCheckBox { Name = "TRUEHDR_AI", Text = UiText("AI 插帧 2×", "AI interpolation 2×"),
                    Location = new Point(125, 581), Size = new Size(140, 28), Enabled = TrueHdrCore.CanInterpolate(source) };
                ai.Checked = ai.Enabled && options.AiInterpolation;
                tips.SetToolTip(ai, UiText("逐行 8/10-bit 4:2:0 SDR，最低 640×360。10-bit 先降至 8-bit。成片和预览编码启用插帧；实时画面用于检查颜色。",
                    "Progressive 8/10-bit 4:2:0 SDR, at least 640×360. 10-bit is reduced to 8-bit. Applies to encoding and clip preview; live stills are for color."));
                var route = new ThemeLabel { Name = "TRUEHDR_ROUTE", Location = new Point(269, 581), Size = new Size(216, 28), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
                settings.Controls.AddRange(new Control[] { ai, route });
                Action updateRate = delegate {
                    bool batchRoute = hasQueue;
                    bool useAi = !toSdr && TrueHdrCore.CanInterpolate(source) && (batchRoute || ai.Checked);
                    ai.Enabled = !batchRoute && TrueHdrCore.CanInterpolate(source);
                    string processing = source.IsInterlaced ? UiText("反交错 · CUDA Bob 2×", "Deinterlace · CUDA Bob 2×") :
                        useAi ? UiText("插帧 · NVEncC AI 2×", "NVEncC AI 2×") : UiText("直接转换", "Direct conversion");
                    route.Text = processing + (toSdr ? " → SDR" : " → HDR");
                    tips.SetToolTip(route, route.Text);
                    target.Enabled = !auto.Checked;
                    if (!auto.Checked) return;
                    string family = codec.SelectedIndex == 1 ? "AV1" : "HEVC";
                    target.Text = rate.SelectedIndex == 1 ? NvencRateControlCore.Recommend(family, motion.Checked, false) :
                        TrueHdrCore.Number(BitrateRecommendationCore.GetRecommendedBitrate(codec.SelectedIndex == 1 ? 0 : 1,
                            source.Width, source.Height, TrueHdrCore.OutputFps(source, useAi), motion.Checked));
                };
                rate.SelectedIndexChanged += delegate { if (!auto.Checked) target.Text = rate.SelectedIndex == 1 ?
                    NvencRateControlCore.Recommend(codec.SelectedIndex == 1 ? "AV1" : "HEVC", motion.Checked, false) : "12000"; updateRate(); };
                ai.CheckedChanged += delegate { updateRate(); };
                auto.CheckedChanged += delegate { updateRate(); };
                motion.CheckedChanged += delegate { updateRate(); };
                codec.SelectedIndexChanged += delegate { updateRate(); };
                updateRate();

                var tone = new ThemeComboBox { Name = "TONE_MAP_ALGO", DropDownStyle = ComboBoxStyle.DropDownList,
                    Location = new Point(198, 125), Size = new Size(271, 26) };
                var toneValues = HdrToneSettings.Algorithms;
                var toneSliders = new TrackBar[toneValues.Length][];
                var tonePanels = new Panel[toneValues.Length];
                var sliders = new TrackBar[TrueHdrOptions.Names.Length];
                Func<string> algorithm = () => toneValues[tone.SelectedIndex];
                Func<string> toneOptions = () => HdrToneSettings.Serialize(algorithm(),
                    Array.ConvertAll(toneSliders[tone.SelectedIndex], slider => slider.Value));
                ThemeCheckBox cpuReference = null;
                if (toSdr)
                {
                    cpuReference = new ThemeCheckBox { Name = "HDR_TO_SDR_CPU_REFERENCE",
                        Text = UiText("CPU Hable 参照", "CPU Hable reference"),
                        Location = new Point(16, 343), Size = new Size(250, 28) };
                    settings.Controls.Add(cpuReference);
                    tips.SetToolTip(cpuReference, UiText("左图使用 CPU Hable 默认参数，右图仍显示当前处理方式和参数。",
                        "Use default CPU Hable for the left image. The right image shows the current processing and settings."));
                    ai.Visible = false;
                    route.Left = 128;
                    settings.Controls.Add(new ThemeLabel { Text = UiText("色调映射", "Tone mapping"),
                        Location = new Point(16, 129), Size = new Size(175, 24) });
                    tone.Items.AddRange(new object[] { lang.T("advanced.hable"), "Mobius", "Reinhard", "Gamma", "Linear", "Clip" });
                    settings.Controls.Add(tone);
                    for (int i = 0; i < toneValues.Length; i++)
                    {
                        var panel = new Panel { Location = new Point(16, 164), Size = new Size(470, 185), Visible = false };
                        tonePanels[i] = panel;
                        var fields = HdrToneSettings.For(toneValues[i]);
                        var initial = HdrToneSettings.Read(toneValues[i], config.Get(HdrToneSettings.Key(toneValues[i])));
                        toneSliders[i] = new TrackBar[fields.Length];
                        for (int row = 0; row < fields.Length; row++)
                        {
                            var field = fields[row];
                            int y = row * 34;
                            string caption = UiText(field.Chinese, field.English);
                            var slider = new ThemeTrackBar { AccessibleName = caption, AutoSize = false, TickStyle = TickStyle.None,
                                Minimum = field.Minimum, Maximum = field.Maximum, Value = initial[row],
                                Location = new Point(182, y), Size = new Size(206, 28) };
                            var value = new ThemeLabel { Text = field.Format(slider.Value), Location = new Point(400, y + 4), Size = new Size(64, 24) };
                            slider.ValueChanged += delegate { value.Text = field.Format(slider.Value); };
                            toneSliders[i][row] = slider;
                            panel.Controls.AddRange(new Control[] { new ThemeLabel { Text = caption,
                                Location = new Point(0, y + 4), Size = new Size(180, 24) }, slider, value });
                        }
                        settings.Controls.Add(panel);
                    }
                    tone.SelectedIndexChanged += delegate {
                        for (int i = 0; i < tonePanels.Length; i++) tonePanels[i].Visible = i == tone.SelectedIndex;
                        cpuReference.Visible = algorithm() == "hable";
                    };
                    tone.SelectedIndex = Math.Max(0, Array.IndexOf(toneValues, config.Get("TONE_MAP_ALGO").ToLowerInvariant()));
                }
                string[] names = { UiText("对比度", "Contrast"), UiText("饱和度", "Saturation"),
                    UiText("中间调亮度", "Middle gray"), UiText("峰值亮度 (nit)", "Peak luminance (nit)") };
                for (int i = 0; !toSdr && i < sliders.Length; i++)
                {
                    int y = 125 + i * 37;
                    var label = new ThemeLabel { Text = names[i], Location = new Point(16, y + 4), Size = new Size(145, 25) };
                    var slider = new ThemeTrackBar { AccessibleName = names[i], Name = "TRUEHDR_" + TrueHdrOptions.Names[i],
                        AutoSize = false, TickStyle = TickStyle.None, Location = new Point(164, y), Size = new Size(252, 28),
                        Minimum = TrueHdrOptions.Minimums[i], Maximum = TrueHdrOptions.Maximums[i] };
                    slider.Value = Math.Max(slider.Minimum, Math.Min(slider.Maximum, options.Picture[i]));
                    var value = new ThemeLabel { Text = TrueHdrCore.Number(slider.Value), Location = new Point(427, y + 4), Size = new Size(55, 25) };
                    slider.ValueChanged += delegate { value.Text = TrueHdrCore.Number(slider.Value); };
                    sliders[i] = slider;
                    settings.Controls.AddRange(new Control[] { label, slider, value });
                }
                var preview = new ThemeCheckBox { Text = UiText("预览编码", "Preview encode"),
                    Name = "TRUEHDR_PREVIEW", Checked = chkEncodePreview.Checked,
                    Location = new Point(16, 493), Size = new Size(135, 26) };
                var start = new MaskedTextBox("00:00:00") { Text = txtPreviewStart.Text, TextMaskFormat = MaskFormat.IncludeLiterals,
                    Location = new Point(210, 493), Size = new Size(90, 26) };
                var seconds = new ThemeIntegerCombo(1, 86400, (int)numPreviewDuration.Value, new[] { 30, 60, 120 }, false) {
                    Location = new Point(394, 493), Size = new Size(75, 26) };
                Action updatePreview = delegate { start.Enabled = seconds.Enabled = preview.Checked; };
                preview.CheckedChanged += delegate { updatePreview(); };
                updatePreview();
                if (toSdr) tips.SetToolTip(preview, UiText("短于截取时长的素材处理整段。起点越界时，该素材从片头截取。",
                    "Encode shorter sources in full. If the start is outside a source, clip from its beginning."));
                else tips.SetToolTip(preview, UiText("按关键帧定位后使用当前 TrueHDR 参数编码，实际起点和时长可能略有偏差。",
                    "Seek near a keyframe and encode using current TrueHDR settings. Start and duration may vary."));
                settings.Controls.AddRange(new Control[] { preview, start, seconds,
                    new ThemeLabel { Text = UiText("起始", "Start"), Location = new Point(154, 496), Size = new Size(53, 24) },
                    new ThemeLabel { Text = UiText("持续", "Length"), Location = new Point(327, 496), Size = new Size(65, 24) },
                    new ThemeLabel { Text = UiText("秒", "s"), Location = new Point(473, 496), Size = new Size(28, 24) } });
                var reset = new ThemeButton { Text = UiText("恢复画面默认值", "Reset picture"), Location = new Point(16, 533), Size = new Size(150, 31) };
                reset.Click += delegate {
                    if (toSdr)
                    {
                        var fields = HdrToneSettings.For(algorithm());
                        for (int i = 0; i < fields.Length; i++) toneSliders[tone.SelectedIndex][i].Value = fields[i].Default;
                    }
                    else for (int i = 0; i < sliders.Length; i++) sliders[i].Value = TrueHdrOptions.Defaults[i];
                };
                var begin = new ThemeButton { Text = UiText("开始编码", "Start encoding"), Name = "TRUEHDR_START",
                    Location = new Point(244, 533), Size = new Size(114, 31) };
                var cancel = new ThemeButton { Text = lang.T("config.cancel"), Location = new Point(369, 533), Size = new Size(104, 31), DialogResult = DialogResult.Cancel };

                var editors = toSdr ? new EncoderOptionsEditor[0] : new[] { BuildEncoderOptions("HEVC", false, tips), BuildEncoderOptions("AV1", false, tips) };
                foreach (var editor in editors)
                {
                    editor.Surface.Location = new Point(4, 273);
                    editor.ValidateHardware = null; // NVEncC validates its own capabilities on initialization.
                    foreach (string key in editor.Fields.Keys)
                    {
                        string stored;
                        editor.SetValue(key, options.Advanced.TryGetValue(key, out stored) ? stored : config.GetDefault(key));
                        if (key.EndsWith("_SPATIAL_AQ", StringComparison.Ordinal) || key.EndsWith("_TEMPORAL_AQ", StringComparison.Ordinal))
                            editor.Fields[key].Enabled = true;
                    }
                    editor.Surface.Width = 480;
                    foreach (Control field in editor.Surface.Controls)
                    {
                        if (field.Left == 190) { field.Left = 155; field.Width = Math.Min(field.Width, 155); }
                        else if (field.Left == 470) { field.Left = 322; field.Width = field is CheckBox ? 150 : 70; }
                        else if (field.Left == 590) field.Left = 401;
                        else if (field.Left == 28) { field.Left = 12; field.Width = Math.Min(field.Width, 135); }
                    }
                    settings.Controls.Add(editor.Surface);
                }
                Action showEditor = delegate { if (toSdr) return; editors[0].Surface.Visible = codec.SelectedIndex == 0; editors[1].Surface.Visible = codec.SelectedIndex == 1; };
                codec.SelectedIndexChanged += delegate { showEditor(); };
                showEditor();

                var live = new ThemeCheckBox { Name = "TRUEHDR_LIVE", Checked = true, Text = UiText("实时画面", "Live preview"),
                    Location = new Point(16, 581), Size = new Size(108, 28) };
                var screen = new OutputComparisonCanvas(toSdr ? UiText("源片 · 默认 Hable", "Source · default Hable") : UiText("原片", "Original"), toSdr ? "HDR → SDR" : "TrueHDR") { Name = "TRUEHDR_LIVE_FRAME",
                    SampleAspect = CinematicCropGeometry.SampleAspect(source),
                    Location = new Point(12, 12), Size = new Size(800, 500) };
                double mediaLength;
                double.TryParse(source.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out mediaLength);
                var seek = new NativeSeekStrip { Location = new Point(12, 522), Width = 493,
                    Duration = Math.Max(0, mediaLength - 1), Enabled = mediaLength > 1 };
                var displayStatus = new ThemeLabel { Location = new Point(12, 557), Size = new Size(800, 36), AutoEllipsis = true };
                string[] previewInfo = null;
                string sourceDepth = TrueHdrCore.DownconvertsForAi(source) ? "10-bit" : MediaSummaryCore.FormatVideoBitDepth(source);
                displayStatus.Paint += delegate(object sender, PaintEventArgs e) {
                    if (previewInfo == null) return;
                    bool highlight = sourceDepth == "10-bit" && previewInfo[3] == "8-bit";
                    Color accent = selectedDarkTheme ? Color.Orange : Color.FromArgb(168, 79, 0);
                    const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
                    int x = 0;
                    int y = Math.Max(0, (displayStatus.Height - 2 * displayStatus.Font.Height) / 2);
                    Action<string, bool> draw = delegate(string value, bool depth) {
                        TextRenderer.DrawText(e.Graphics, value, displayStatus.Font, new Point(x, y),
                            highlight && depth ? accent : displayStatus.ForeColor, flags);
                        x += TextRenderer.MeasureText(e.Graphics, value, displayStatus.Font, Size.Empty, flags).Width;
                    };
                    draw(previewInfo[0], false);
                    if (sourceDepth != "8-bit" || previewInfo[3] != "8-bit")
                    {
                        draw(" · ", false);
                        draw(sourceDepth, true);
                    }
                    draw(previewInfo[1], false);
                    x = 0;
                    y += displayStatus.Font.Height;
                    draw("BT.2020 NCL · " + previewInfo[2], false);
                    draw(previewInfo[3], true);
                    draw(" · PQ → SDR · 203 nit", false);
                };
                var view = new ThemeComboBox { Location = new Point(513, 521), Size = new Size(150, 26),
                    DropDownStyle = ComboBoxStyle.DropDownList };
                view.Items.AddRange(new object[] { UiText("单张", "Single"), UiText("并排", "Side by side"), UiText("滑动比较", "Split") });
                view.SelectedIndex = 2;
                var zoom = new ThemeButton { Location = new Point(669, 521), Size = new Size(85, 26),
                    Text = UiText("适应窗口", "Fit") };
                var scale = new ThemeLabel { Location = new Point(758, 525), Size = new Size(54, 23) };
                Action scaleText = delegate { scale.Text = screen.HasPair ? screen.DisplayPercent + "%" : ""; };
                view.SelectedIndexChanged += delegate {
                    screen.ResetPan();
                    screen.SingleOutput = view.SelectedIndex == 0;
                    screen.SideBySide = view.SelectedIndex == 1;
                    screen.Invalidate();
                    scaleText();
                };
                Action toggleZoom = delegate {
                    screen.ResetPan();
                    screen.ActualSize = !screen.ActualSize;
                    zoom.Text = screen.ActualSize ? "1:1" : UiText("适应窗口", "Fit");
                    screen.Invalidate();
                    scaleText();
                };
                zoom.Click += delegate { toggleZoom(); };
                screen.DoubleClick += delegate { toggleZoom(); };
                settings.Controls.Add(live);
                dlg.Controls.AddRange(new Control[] { screen, seek, displayStatus, view, zoom, scale });
                CancellationTokenSource render = null;
                bool startAfterRender = false;
                int revision = 0;
                Action schedule = delegate {
                    revision++;
                    if (render != null) render.Cancel();
                    refresh.Stop();
                    if (live.Checked) refresh.Start();
                };
                refresh.Tick += async delegate {
                    refresh.Stop();
                    if (render != null) { refresh.Start(); return; }
                    int requested = revision;
                    var job = new CancellationTokenSource();
                    render = job;
                    var values = new int[sliders.Length];
                    if (!toSdr) for (int i = 0; i < values.Length; i++) values[i] = sliders[i].Value;
                    string selectedAlgorithm = toSdr ? algorithm() : null;
                    string tuning = toSdr ? toneOptions() : null;
                    bool useCpuReference = selectedAlgorithm == "hable" && cpuReference.Checked;
                    string ffmpeg = Path.Combine(config.Get("FFMPEG_DIR"), "ffmpeg.exe");
                    string nvenc = NvencInterpolationCore.Exe(config.Get("NVENC_DIR"));
                    double position = seek.Position;
                    previewInfo = null;
                    displayStatus.Text = UiText("正在刷新画面…", "Refreshing preview…");
                    try {
                        var image = await Task.Run(() => toSdr
                            ? HdrToSdrCore.RenderLiveFrame(ffmpeg, source, position, selectedAlgorithm, tuning, useCpuReference, job.Token)
                            : TrueHdrCore.RenderLiveFrame(nvenc, source, position, values, job.Token));
                        if (dlg.IsDisposed || requested != revision) { image.Item1.Dispose(); image.Item2.Dispose(); return; }
                        if (toSdr) screen.OriginalCaption = image.Item1.Tag + UiText(" · 默认", " · default");
                        screen.SetFrames(image.Item1, image.Item2);
                        scaleText();
                        if (toSdr)
                        {
                            displayStatus.Text = NativeSeekStrip.FormatTime(position) + " · " + image.Item2.Width + "×" +
                                image.Item2.Height + " · BT.709 SDR · " + image.Item2.Tag;
                            return;
                        }
                        string fps = TrueHdrCore.OutputFps(source).ToString("0.###", CultureInfo.InvariantCulture);
                        string output = (string)image.Item2.Tag;
                        int depthStart = output.LastIndexOf(' ') + 1;
                        previewInfo = new[] {
                            NativeSeekStrip.FormatTime(position) + " · " + image.Item2.Width + "×" + image.Item2.Height,
                            " · " + fps + " fps · SAR " + source.SampleAspectRatio,
                            output.Substring(0, depthStart), output.Substring(depthStart)
                        };
                        displayStatus.Text = "";
                        displayStatus.Invalidate();
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception error) {
                        if (!dlg.IsDisposed && requested == revision)
                        {
                            displayStatus.Text = UiText("实时预览失败", "Live preview failed") + ": " + error.Message.Split('\n')[0];
                            tips.SetToolTip(displayStatus, error.Message);
                        }
                    }
                    finally
                    {
                        render = null;
                        job.Dispose();
                        if (startAfterRender && !dlg.IsDisposed && dlg.Visible)
                        {
                            startAfterRender = false;
                            settings.Enabled = true;
                            begin.PerformClick();
                        }
                    }
                };
                live.CheckedChanged += delegate {
                    view.Enabled = zoom.Enabled = live.Checked;
                    seek.Enabled = live.Checked && mediaLength > 1;
                    if (!live.Checked) { screen.SetFrames(null, null); previewInfo = null; displayStatus.Invalidate(); displayStatus.Text = ""; scaleText(); }
                    schedule();
                };
                seek.PositionChanged += delegate { schedule(); };
                if (toSdr)
                {
                    tone.SelectedIndexChanged += delegate { schedule(); };
                    cpuReference.CheckedChanged += delegate { schedule(); };
                    foreach (var row in toneSliders)
                        foreach (var slider in row) slider.ValueChanged += delegate { schedule(); };
                }
                else foreach (TrackBar slider in sliders) slider.ValueChanged += delegate { schedule(); };
                dlg.FormClosed += delegate {
                    startAfterRender = false;
                    revision++;
                    refresh.Stop();
                    if (render != null) render.Cancel();
                    screen.SetFrames(null, null);
                };

                Action updateStartLabel = delegate {
                    begin.Text = hasQueue ? (preview.Checked ? UiText("预览编码队列", "Preview queue") : UiText("开始队列", "Start queue")) :
                        UiText("开始编码", "Start encoding");
                };
                preview.CheckedChanged += delegate { updateStartLabel(); };
                updateStartLabel();
                if (toSdr && hasQueue) tips.SetToolTip(begin, UiText("队列中的 HDR 素材使用当前统一参数，跳过 SDR。",
                    "Apply these settings to pending HDR sources and skip SDR."));
                if (!toSdr && hasQueue) tips.SetToolTip(begin, UiText("队列使用当前统一配置。HDR 跳过，隔行 Bob，符合条件的逐行 SDR 用 AI 2×，其余可转换 SDR 直接转 HDR。预览编码对每个待处理素材使用同一截取范围。",
                    "Queue uses these shared settings. Skip HDR, Bob interlaced SDR, AI 2× for eligible progressive SDR, direct conversion for other supported SDR. Clip preview applies the same range to every pending source."));

                begin.Click += delegate {
                    bool tracking = false;
                    try
                    {
                        if (preview.Checked && !seconds.ValidateInput()) return;
                        if (render != null) {
                            live.Checked = false;
                            if (toSdr)
                            {
                                startAfterRender = true;
                                settings.Enabled = false;
                                return;
                            }
                            FgsMessageBox.Show(dlg, UiText("正在停止实时预览，请稍后开始编码。", "Stopping live preview. Start encoding once it stops."), dlg.Text);
                            return;
                        }
                        options.PreviewStart = preview.Checked ? start.Text : null;
                        options.PreviewSeconds = preview.Checked ? (int)seconds.Value : 0;
                        options.Codec = codec.SelectedIndex == 1 ? "AV1" : "HEVC";
                        options.Preset = (string)preset.SelectedItem;
                        options.Cq = rate.SelectedIndex == 1;
                        options.Auto = auto.Checked;
                        options.HighMotion = motion.Checked;
                        options.AiInterpolation = !toSdr && ai.Checked;
                        options.Target = target.Text.Trim();
                        options.Mp4 = container.SelectedIndex == 1;
                        if (!toSdr)
                        {
                            for (int i = 0; i < sliders.Length; i++) options.Picture[i] = sliders[i].Value;
                            foreach (var setting in editors[codec.SelectedIndex].ReadValues()) options.Advanced[setting.Key] = setting.Value;
                        }
                        string selectedAlgorithm = toSdr ? algorithm() : null;
                        string tuning = toSdr ? toneOptions() : null;
                        config.Load();
                        bool batch = hasQueue;
                        var inputs = batch ? pending : new List<string> { source.PathValue };
                        var tasks = new List<BridgeTaskRequest>();
                        var skipped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        var finalOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (string input in inputs)
                        {
                            MediaProbeInfo media = source;
                            if (batch && !mediaProbeCache.TryGetValue(input, out media))
                                throw new InvalidOperationException(UiText("队列媒体信息未就绪：", "Queue media info is unavailable: ") + input);
                            var itemOptions = toSdr ? (HdrToSdrCore.Eligible(media) ? options : null) :
                                batch ? options.ForQueueItem(media) : options;
                            if (itemOptions == null)
                            {
                                skipped[input] = toSdr && !media.IsHdr ? UiText("跳过 SDR", "Skipped SDR") : !toSdr && media.IsHdr ? UiText("跳过 HDR", "Skipped HDR") : UiText("跳过 · 素材不符合条件", "Skipped · unsupported source");
                                continue;
                            }
                            string output;
                            var request = toSdr
                                ? HdrToSdrCore.Prepare(config, input, media, itemOptions, selectedAlgorithm, tuning, out output)
                                : TrueHdrCore.Prepare(config, input, media, itemOptions, out output);
                            if (!finalOutputs.Add(Path.GetFullPath(output)))
                                throw new InvalidOperationException(UiText("队列输出路径重复：", "Duplicate queue output: ") + output);
                            tasks.Add(new BridgeTaskRequest { ExecutionRequest = request,
                                RecommendedOutputFps = TrueHdrCore.OutputFps(media, itemOptions.AiInterpolation) });
                        }
                        if (tasks.Count == 0)
                            throw new InvalidOperationException(toSdr
                                ? UiText("队列里没有符合条件的 HDR 素材。", "No eligible HDR sources in the queue.")
                                : UiText("队列里没有符合条件的 SDR 素材。", "No eligible SDR sources in the queue."));
                        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                        string temporary = settingsPath + "." + Guid.NewGuid().ToString("N");
                        try {
                            var persisted = serializer.Deserialize<TrueHdrOptions>(serializer.Serialize(options));
                            persisted.PreviewStart = null;
                            persisted.PreviewSeconds = 0;
                            File.WriteAllText(temporary, serializer.Serialize(persisted));
                            if (File.Exists(settingsPath)) File.Replace(temporary, settingsPath, null);
                            else File.Move(temporary, settingsPath);
                        }
                        finally { File.Delete(temporary); }

                        if (taskLog != null) taskLog.Dispose();
                        taskLog = new FullLogFileCore(appRoot, "Task", GetLogEntryLimit());
                        fullLogErrorReported = false;
                        AppendTaskLog("[Task log] " + taskLog.FilePath + Environment.NewLine);
                        string logLabel = toSdr ? "HDR-to-SDR" : "TrueHDR";
                        foreach (var skip in skipped) AppendTaskLog("[" + logLabel + " queue] " + skip.Value + " " + skip.Key + Environment.NewLine);
                        foreach (ListViewItem item in listFiles.Items)
                        {
                            string reason;
                            if (item.Tag is string && skipped.TryGetValue((string)item.Tag, out reason)) item.SubItems[2].Text = reason;
                        }
                        queueHistory = batch ? new QueueCompletionHistory(chkQueueHistory != null && chkQueueHistory.Checked) : null;
                        submittedQueueFiles.Clear();
                        foreach (var task in tasks)
                        {
                            string input = task.ExecutionRequest.InputFiles[0];
                            submittedQueueFiles.Add(input);
                            AppendTaskLog("[" + logLabel + " input] " + input + Environment.NewLine);
                        }
                        lock (queueClockSync) { queueClock.Restart(); queueClockStatus = ""; }
                        tracking = true;
                        if (batch)
                        {
                            bridgeTaskCoordinator.SkipFailedFiles = config.GetBool("QUEUE_SKIP_FAILED");
                            RegisterQueueHistory(tasks);
                            bridgeTaskCoordinator.StartQueue(tasks);
                        }
                        else bridgeTaskCoordinator.Start(tasks[0]);
                        dlg.DialogResult = DialogResult.OK;
                    }
                    catch (Exception error)
                    {
                        if (tracking && !bridgeTaskCoordinator.IsActive)
                            lock (queueClockSync) { queueClock.Stop(); }
                        FgsMessageBox.Show(dlg, error.Message, dlg.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };
                settings.Controls.AddRange(new Control[] { codec, preset, container, rate, target, auto, motion, reset, begin, cancel });
                if (toSdr)
                {
                    var confirm = new ThemeButton { Name = "HDR_TO_SDR_OK", Text = lang.T("advanced.ok"),
                        Location = new Point(174, 533), Size = new Size(78, 31) };
                    begin.Left = 258;
                    begin.Width = 111;
                    cancel.Left = 375;
                    cancel.Width = 98;
                    begin.Enabled = bridgeTaskCoordinator != null && !bridgeTaskCoordinator.IsActive;
                    confirm.Click += delegate {
                        try
                        {
                            var updates = new Dictionary<string, string> { { "TONE_MAP_ALGO", algorithm() } };
                            for (int i = 0; i < toneValues.Length; i++)
                                updates[HdrToneSettings.Key(toneValues[i])] = HdrToneSettings.Serialize(toneValues[i],
                                    Array.ConvertAll(toneSliders[i], slider => slider.Value));
                            config.Save(updates);
                            UpdateHdrRouteStatus(GetSelectedMediaInfo());
                            dlg.DialogResult = DialogResult.OK;
                        }
                        catch (Exception error)
                        {
                            FgsMessageBox.Show(dlg, error.Message, dlg.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    };
                    settings.Controls.Add(confirm);
                }
                dlg.CancelButton = cancel;
                AttachThemeWindow(dlg);
                dlg.ResumeLayout(true);
                dlg.Shown += delegate { dlg.Refresh(); dlg.Opacity = 1; schedule(); };
                dlg.ShowDialog(this);
            }
        }
    }
}
