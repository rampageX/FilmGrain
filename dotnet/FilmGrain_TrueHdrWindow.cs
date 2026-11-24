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
            if (source.IsHdr) ShowProcessingQuickSettings(true);
            else if (TrueHdrCore.Eligible(source) && bridgeTaskCoordinator != null && !bridgeTaskCoordinator.IsActive)
                ShowTrueHdrDialog(source);
        }

        private void ShowTrueHdrDialog(MediaProbeInfo source)
        {
            string settingsPath = Path.Combine(appRoot, "Utils", "TrueHDR.settings.json");
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

            using (var dlg = new SettingsDialogForm())
            using (var tips = new ThemeToolTip())
            using (var refresh = new System.Windows.Forms.Timer { Interval = 250 })
            {
                dlg.Text = "SDR → TrueHDR";
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
                codec.Items.AddRange(new object[] { "HEVC NVENC · HDR10 Main10", "AV1 NVENC · HDR10 10-bit" });
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
                Action updateRate = delegate {
                    target.Enabled = !auto.Checked;
                    if (!auto.Checked) return;
                    string family = codec.SelectedIndex == 1 ? "AV1" : "HEVC";
                    target.Text = rate.SelectedIndex == 1 ? NvencRateControlCore.Recommend(family, motion.Checked, false) :
                        TrueHdrCore.Number(BitrateRecommendationCore.GetRecommendedBitrate(codec.SelectedIndex == 1 ? 0 : 1,
                            source.Width, source.Height, BitrateRecommendationCore.ParseMediaFps(source), motion.Checked));
                };
                rate.SelectedIndexChanged += delegate { if (!auto.Checked) target.Text = rate.SelectedIndex == 1 ?
                    NvencRateControlCore.Recommend(codec.SelectedIndex == 1 ? "AV1" : "HEVC", motion.Checked, false) : "12000"; updateRate(); };
                auto.CheckedChanged += delegate { updateRate(); };
                motion.CheckedChanged += delegate { updateRate(); };
                codec.SelectedIndexChanged += delegate { updateRate(); };
                updateRate();

                var sliders = new TrackBar[TrueHdrOptions.Names.Length];
                string[] names = { UiText("对比度", "Contrast"), UiText("饱和度", "Saturation"),
                    UiText("中间调亮度", "Middle gray"), UiText("峰值亮度 (nit)", "Peak luminance (nit)") };
                for (int i = 0; i < sliders.Length; i++)
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
                tips.SetToolTip(preview, UiText("按关键帧定位后使用当前 TrueHDR 参数编码，实际起点和时长可能略有偏差。",
                    "Seek near a keyframe and encode using current TrueHDR settings. Start and duration may vary."));
                settings.Controls.AddRange(new Control[] { preview, start, seconds,
                    new ThemeLabel { Text = UiText("起始", "Start"), Location = new Point(154, 496), Size = new Size(53, 24) },
                    new ThemeLabel { Text = UiText("持续", "Length"), Location = new Point(327, 496), Size = new Size(65, 24) },
                    new ThemeLabel { Text = UiText("秒", "s"), Location = new Point(473, 496), Size = new Size(28, 24) } });
                var reset = new ThemeButton { Text = UiText("恢复画面默认值", "Reset picture"), Location = new Point(16, 533), Size = new Size(150, 31) };
                reset.Click += delegate { for (int i = 0; i < sliders.Length; i++) sliders[i].Value = TrueHdrOptions.Defaults[i]; };
                var begin = new ThemeButton { Text = UiText("开始编码", "Start encoding"), Name = "TRUEHDR_START",
                    Location = new Point(244, 533), Size = new Size(114, 31) };
                var cancel = new ThemeButton { Text = lang.T("config.cancel"), Location = new Point(369, 533), Size = new Size(104, 31), DialogResult = DialogResult.Cancel };

                var editors = new[] { BuildEncoderOptions("HEVC", false, tips), BuildEncoderOptions("AV1", false, tips) };
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
                Action showEditor = delegate { editors[0].Surface.Visible = codec.SelectedIndex == 0; editors[1].Surface.Visible = codec.SelectedIndex == 1; };
                codec.SelectedIndexChanged += delegate { showEditor(); };
                showEditor();

                var live = new ThemeCheckBox { Name = "TRUEHDR_LIVE", Checked = true, Text = UiText("实时画面", "Live preview"),
                    Location = new Point(16, 581), Size = new Size(140, 28) };
                var screen = new OutputComparisonCanvas(UiText("原片", "Original"), "TrueHDR") { Name = "TRUEHDR_LIVE_FRAME",
                    SingleOutput = true, SampleAspect = CinematicCropGeometry.SampleAspect(source),
                    Location = new Point(12, 12), Size = new Size(800, 500) };
                double mediaLength;
                double.TryParse(source.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out mediaLength);
                var seek = new NativeSeekStrip { Location = new Point(12, 522), Width = 493,
                    Duration = Math.Max(0, mediaLength - 1), Enabled = mediaLength > 1 };
                var displayStatus = new ThemeLabel { Location = new Point(12, 557), Size = new Size(800, 42), AutoEllipsis = true };
                var view = new ThemeComboBox { Location = new Point(513, 521), Size = new Size(150, 26),
                    DropDownStyle = ComboBoxStyle.DropDownList };
                view.Items.AddRange(new object[] { UiText("单张", "Single"), UiText("并排", "Side by side"), UiText("滑动比较", "Split") });
                view.SelectedIndex = 0;
                var zoom = new ThemeButton { Location = new Point(669, 521), Size = new Size(85, 26),
                    Text = UiText("适应窗口", "Fit") };
                var scale = new ThemeLabel { Location = new Point(758, 525), Size = new Size(54, 23) };
                Action scaleText = delegate { scale.Text = screen.HasPair ? screen.DisplayPercent + "%" : ""; };
                view.SelectedIndexChanged += delegate {
                    screen.SingleOutput = view.SelectedIndex == 0;
                    screen.SideBySide = view.SelectedIndex == 1;
                    screen.Invalidate();
                    scaleText();
                };
                Action toggleZoom = delegate {
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
                    for (int i = 0; i < values.Length; i++) values[i] = sliders[i].Value;
                    string nvenc = NvencInterpolationCore.Exe(config.Get("NVENC_DIR"));
                    double position = seek.Position;
                    displayStatus.Text = UiText("正在刷新画面…", "Refreshing preview…");
                    try {
                        var image = await Task.Run(() => TrueHdrCore.RenderLiveFrame(nvenc, source, position, values, job.Token));
                        if (dlg.IsDisposed || requested != revision) { image.Item1.Dispose(); image.Item2.Dispose(); return; }
                        screen.SetFrames(image.Item1, image.Item2);
                        scaleText();
                        string fps = BitrateRecommendationCore.ParseMediaFps(source).ToString("0.###", CultureInfo.InvariantCulture);
                        displayStatus.Text = NativeSeekStrip.FormatTime(position) + " · " + image.Item2.Width + "×" + image.Item2.Height +
                            " · " + fps + " fps · SAR " + source.SampleAspectRatio + Environment.NewLine +
                            "BT.2020 NCL · " + image.Item2.Tag + " · PQ → SDR · 203 nit";
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception error) {
                        if (!dlg.IsDisposed && requested == revision)
                        {
                            displayStatus.Text = UiText("实时预览失败", "Live preview failed") + ": " + error.Message.Split('\n')[0];
                            tips.SetToolTip(displayStatus, error.Message);
                        }
                    }
                    finally { render = null; job.Dispose(); }
                };
                live.CheckedChanged += delegate {
                    view.Enabled = zoom.Enabled = live.Checked;
                    seek.Enabled = live.Checked && mediaLength > 1;
                    if (!live.Checked) { screen.SetFrames(null, null); displayStatus.Text = ""; scaleText(); }
                    schedule();
                };
                seek.PositionChanged += delegate { schedule(); };
                foreach (TrackBar slider in sliders) slider.ValueChanged += delegate { schedule(); };
                dlg.FormClosed += delegate {
                    revision++;
                    refresh.Stop();
                    if (render != null) render.Cancel();
                    screen.SetFrames(null, null);
                };

                begin.Click += delegate {
                    bool tracking = false;
                    try
                    {
                        if (preview.Checked && !seconds.ValidateInput()) return;
                        if (render != null) {
                            live.Checked = false;
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
                        options.Target = target.Text.Trim();
                        options.Mp4 = container.SelectedIndex == 1;
                        for (int i = 0; i < sliders.Length; i++) options.Picture[i] = sliders[i].Value;
                        foreach (var setting in editors[codec.SelectedIndex].ReadValues()) options.Advanced[setting.Key] = setting.Value;
                        config.Load();
                        var request = TrueHdrCore.Prepare(config, source.PathValue, source, options);
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
                        AppendTaskLog("[Task log] " + taskLog.FilePath + Environment.NewLine + "[TrueHDR input] " + source.PathValue + Environment.NewLine);
                        queueHistory = null;
                        submittedQueueFiles.Clear();
                        lock (queueClockSync) { queueClock.Restart(); queueClockStatus = ""; }
                        tracking = true;
                        bridgeTaskCoordinator.Start(new BridgeTaskRequest { ExecutionRequest = request,
                            RecommendedOutputFps = BitrateRecommendationCore.ParseMediaFps(source) });
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
                dlg.CancelButton = cancel;
                AttachThemeWindow(dlg);
                dlg.ResumeLayout(true);
                dlg.Shown += delegate { dlg.Refresh(); dlg.Opacity = 1; schedule(); };
                dlg.ShowDialog(this);
            }
        }
    }
}
