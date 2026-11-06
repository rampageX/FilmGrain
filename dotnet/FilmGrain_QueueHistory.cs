using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace FilmGrainStudioPreview
{
    // One immutable submission order per Start; observations arrive on the worker thread.
    internal sealed class QueueCompletionHistory
    {
        internal sealed class Entry
        {
            internal BridgeTaskRequest Task;
            internal BridgeExecutionRequest Request;
            internal string Input, Settings;
            internal MediaProbeInfo Media;
            internal readonly List<string> Stages = new List<string>();
            internal DateTime? Finished;
            internal BridgeExecutionResult Result;
        }
        readonly object gate = new object();
        readonly List<Entry> entries = new List<Entry>();
        Entry active;
        internal readonly bool Enabled;
        readonly bool independent;
        readonly DateTime started = DateTime.Now;
        internal QueueCompletionHistory(bool enabled, bool perFile = false) { Enabled = enabled; independent = perFile; }
        internal void Register(BridgeTaskRequest task, MediaProbeInfo media)
        {
            if (!Enabled) return;
            var entry = new Entry { Task = task, Request = task.ExecutionRequest, Input = Join(task.ExecutionRequest.InputFiles), Settings = Describe(task, media), Media = media };
            ObserveContinuation(entry, task.ExecutionRequest);
            lock (gate) entries.Add(entry);
        }
        void ObserveContinuation(Entry entry, BridgeExecutionRequest request)
        {
            var continuation = request.ContinueAfterStages;
            if (continuation == null) return;
            request.ContinueAfterStages = delegate(Func<bool> cancelled) {
                BridgeExecutionRequest next = continuation(cancelled);
                if (next != null) {
                    ObserveContinuation(entry, next);
                    lock (gate) entry.Request = next;
                }
                return next;
            };
        }
        internal void Running(BridgeTaskRequest task)
        { lock (gate) active = entries.Find(delegate(Entry e) { return e.Task == task; }); }
        internal void Log(string line)
        {
            if (!Enabled || string.IsNullOrEmpty(line)) return;
            Match match = Regex.Match(line, @"^\w+ step \d+/\d+ - (.+)$");
            if (!match.Success) return;
            lock (gate) if (active != null) active.Stages.Add(match.Groups[1].Value);
        }
        internal void Completed(BridgeTaskRequest task, BridgeExecutionResult result)
        {
            lock (gate) {
                Entry entry = entries.Find(delegate(Entry e) { return e.Task == task; });
                if (entry == null) return;
                entry.Finished = DateTime.Now; entry.Result = result;
                entry.Settings = Describe(task, entry.Media, entry.Request);
            }
        }
        internal string Finish(TimeSpan elapsed, BridgeExecutionResult terminal)
        {
            lock (gate) {
                if (entries.Count == 0) return "";
                var text = new StringBuilder("队列任务完成记录\r\n\r\n");
                text.AppendLine("开始时间：" + started.ToString("yyyy-MM-dd HH:mm:ss"));
                text.AppendLine("结束时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                text.AppendLine();
                int success = 0, failed = 0, cancelled = 0, pending = 0;
                long bytes = 0;
                for (int i = 0; i < entries.Count; i++) {
                    Entry entry = entries[i]; BridgeExecutionResult result = entry.Result;
                    bool ok = result != null && !result.Cancelled && result.Exception == null && result.ExitCode == 0;
                    string status;
                    if (result == null) { pending++; status = "未执行"; }
                    else if (result.Cancelled) { cancelled++; status = "已取消"; }
                    else if (ok) { success++; status = "完成"; }
                    else { failed++; status = "失败（exit " + result.ExitCode + "）"; }
                    text.AppendLine("队列序号 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " / " + entries.Count);
                    text.AppendLine("状态：" + status);
                    text.AppendLine("输入文件：" + entry.Input);
                    string outputs = Join(entry.Request.OutputFiles);
                    text.AppendLine((ok ? "输出文件：" : "输出目标（未确认完成）：") + (outputs.Length == 0 ? "未生成" : outputs));
                    if (independent) text.AppendLine(Flow(entry));
                    string notice = entry.Request.ExecutionNotice;
                    if (!string.IsNullOrWhiteSpace(notice) && notice.Contains("OpenSVPFlow 已绕过")) text.AppendLine("执行说明：OpenSVPFlow 已按素材兼容规则绕过，未执行插帧。");
                    if (result != null && result.Exception != null) text.AppendLine("错误：" + result.Exception.Message);
                    text.AppendLine("完成时间：" + (entry.Finished.HasValue ? entry.Finished.Value.ToString("yyyy-MM-dd HH:mm:ss") : "—"));
                    if (result != null) text.AppendLine("单项耗时：" + Duration(result.TotalElapsed));
                    if (ok && entry.Request.OutputFiles != null)
                        foreach (string path in entry.Request.OutputFiles)
                            try { if (File.Exists(path)) bytes += new FileInfo(path).Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    text.AppendLine(); text.AppendLine("────────────────────────────────────────"); text.AppendLine();
                }
                if (!independent) {
                    text.AppendLine("统一模式 · 流程记录");
                    var flows = new List<string>();
                    var groups = new List<List<string>>();
                    for (int i = 0; i < entries.Count; i++) {
                        string flow = Flow(entries[i]); int index = flows.IndexOf(flow);
                        if (index < 0) { index = flows.Count; flows.Add(flow); groups.Add(new List<string>()); }
                        groups[index].Add((i + 1).ToString(CultureInfo.InvariantCulture));
                    }
                    for (int i = 0; i < flows.Count; i++) {
                        if (flows.Count > 1) text.AppendLine("适用队列序号：" + string.Join("、", groups[i].ToArray()));
                        text.AppendLine(flows[i]); text.AppendLine();
                    }
                    text.AppendLine("────────────────────────────────────────"); text.AppendLine();
                }
                text.AppendLine("队列状态：" + (terminal.Cancelled ? "已取消" : terminal.Exception != null || terminal.ExitCode != 0 ? "结束（存在失败）" : "完成"));
                text.AppendLine("队列完成总时间：" + Duration(elapsed));
                text.AppendLine("文件统计：共 " + entries.Count + "，成功 " + success + "，失败 " + failed + "，取消 " + cancelled + "，未执行 " + pending);
                text.AppendLine("成功输出总大小：" + (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MiB");
                return text.ToString();
            }
        }
        static string Flow(Entry entry)
        { return "处理项目：" + entry.Settings + Environment.NewLine + "执行流程：" + (entry.Stages.Count == 0 ? "未进入执行阶段" : string.Join(" → ", entry.Stages.ToArray())); }
        internal static string Duration(TimeSpan value)
        { return ((long)value.TotalHours).ToString("00", CultureInfo.InvariantCulture) + ":" + value.Minutes.ToString("00") + ":" + value.Seconds.ToString("00") + "." + value.Milliseconds.ToString("000"); }
        static string Join(IList<string> values) { return values == null ? "" : string.Join("\r\n    ", new List<string>(values).ToArray()); }
        static string Get(IDictionary<string,string> state, string key, string fallback = "")
        { string value; return state != null && state.TryGetValue(key, out value) ? value : fallback; }
        static string Describe(BridgeTaskRequest task, MediaProbeInfo media, BridgeExecutionRequest effective = null)
        {
            BridgeExecutionRequest request = effective ?? task.ExecutionRequest;
            IDictionary<string,string> state = request.Environment;
            var parts = new List<string>();
            if (task.NoReencode) parts.Add("视频流复制（不重编码）");
            else {
                if (media != null && media.IsInterlaced && Get(state,"FG_DEINTERLACE") == "AUTO") parts.Add("反交错 " + Get(state,"FG_DEINT_METHOD"));
                if (PreprocessingCore.Enabled(state)) parts.Add("Bilateral（sigmaS=" + Get(state,"FG_PRE_BILATERAL_SIGMA_S") + "，sigmaR=" + Get(state,"FG_PRE_BILATERAL_SIGMA_R") + "，planes=" + Get(state,"FG_PRE_BILATERAL_PLANES") + "）");
                CinematicFramePlan frame; ResizePlan resize; string reason;
                if (media != null && CinematicFrameCore.TryBuild(media, state, out frame, out reason)) {
                    if (Get(state,"FG_CINEMATIC_FRAME") == "1") parts.Add("画幅 " + Get(state,"FG_FRAME_MODE") + "（" + frame.ActiveWidth + "×" + frame.ActiveHeight + "）");
                    if (ResizeCore.Enabled(state) && ResizeCore.TryBuild(media, state, frame, out resize, out reason)) parts.Add("缩放 " + resize.Width + "×" + resize.Height + "（Vulkan / Lanczos）");
                }
                if (Get(state,"FG_SVP_INTERPOLATE") == "1") parts.Add("插帧 " + Get(state,"FG_INTERPOLATION_ENGINE","SVP") + " / " + Get(state,"FG_INTERPOLATION_TARGET_FPS") + " fps");
                // These are effective prepared settings; exact stage order is recorded separately.
                if (media != null && media.IsHdr) {
                    string arguments = request.DirectArguments ?? "";
                    if (request.DirectStages != null) foreach (BridgeExecutionStage stage in request.DirectStages) arguments += " " + stage.DirectArguments;
                    bool toSdr = arguments.Contains("tonemapping=") || arguments.Contains("tonemap=");
                    parts.Add(toSdr ? "HDR → SDR / " + Get(state,"FG_TONEMAP_ALGO") : "HDR 保留（" + media.ColorTransfer + "）");
                }
                if (Get(state,"FG_COLOR_ENABLED") == "1") parts.Add("色彩校正（对比度 " + Get(state,"FG_COLOR_CONTRAST") + "，亮度 " + Get(state,"FG_COLOR_BRIGHTNESS") + "，饱和度 " + Get(state,"FG_COLOR_SATURATION") + "，Gamma " + Get(state,"FG_COLOR_GAMMA") + "）");
                if (Get(state,"FG_LUT_PATH").Length > 0) parts.Add("LUT " + Path.GetFileName(Get(state,"FG_LUT_PATH")) + " / " + Get(state,"FG_LUT_STRENGTH","100") + "%（受 HDR 策略约束）");
                if (Get(state,"FG_SUBTITLE") == "1") parts.Add("字幕 " + Get(state,"FG_SUB_MODE"));
            }
            if (SharedGrainCore.IsEnabled(state)) {
                string engine = Get(state,"FG_GRAIN_ENGINE","NATIVE");
                parts.Add("颗粒 " + (engine == "PROCEDURAL" ? "数字 / 强度 " + Get(state,"FG_PROC_STRENGTH") : engine == "FGSIM" ? "FGSIM / " + Get(state,"FG_FGSIM_PRESET") : engine) + (Get(state,"FG_MODE") == "AV1" && engine == "NATIVE" ? " / " + Get(state,"FG_AV1_GRAIN_MODE") + " / " + Get(state,"FG_AV1_GRAIN_STRENGTH","100") + "%" : engine == "NATIVE" ? " / " + Path.GetFileName(Get(state,"FG_HEVC_GRAIN_PATH")) + " / " + Get(state,"FG_GRAIN_PLATE_STRENGTH","100") + "%" : ""));
            }
            if (!task.NoReencode) {
                string rate = Get(state,"FG_NVENC_RATE_CONTROL","VBR");
                string quality = rate == "CQ" ? "CQ " + Get(state,"FG_NVENC_CQ") : "VBR " + Get(state,"FG_BITRATE") + " kbps";
                if (CpuEncoderCore.IsNewCpu(state)) quality = CpuEncoderCore.IsCrf(state) ? "CRF " + Get(state,"FG_NVENC_CQ") : "VBR " + Get(state,"FG_BITRATE") + " kbps";
                parts.Add("编码 " + Get(state,"FG_MODE") + " / " + quality + " / 输出 " + task.RecommendedOutputFps.ToString("0.###", CultureInfo.InvariantCulture) + " fps");
            }
            return string.Join(" · ", parts.ToArray());
        }
    }

    internal sealed partial class MainForm
    {
        private CheckBox chkQueueHistory;
        private QueueCompletionHistory queueHistory;
        private void RegisterQueueHistory(IList<BridgeTaskRequest> tasks)
        {
            foreach (BridgeTaskRequest task in tasks) {
                MediaProbeInfo media = null;
                if (task.ExecutionRequest.InputFiles != null && task.ExecutionRequest.InputFiles.Count > 0) mediaProbeCache.TryGetValue(task.ExecutionRequest.InputFiles[0], out media);
                queueHistory.Register(task, media);
            }
        }
        private void ShowQueueCompletionRecord(string record)
        {
            var dialog = new SettingsDialogForm(false) { Name = "QUEUE_COMPLETION_RECORD", Text = UiText("队列任务完成记录", "Queue completion record"), Font = Font,
                StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, MinimizeBox = false, AutoScaleMode = AutoScaleMode.Dpi,
                Size = new Size(1120, 630), MinimumSize = new Size(640, 360) };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 14, 22, 12), ColumnCount = 1, RowCount = 3 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            var note = new ThemeLabel { Text = UiText("只读记录 · 滚轮或 Page Up / Page Down 浏览 · Ctrl+A / Ctrl+C 复制", "Read only · Wheel / Page Up / Page Down to browse · Ctrl+A / Ctrl+C to copy"), Dock = DockStyle.Fill, ForeColor = ColorMuted };
            var text = new QueueRecordText(record) { Name = "QUEUE_RECORD_TEXT", Dock = DockStyle.Fill, Font = Font, Margin = Padding.Empty };
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 12, 18, 12), Margin = Padding.Empty, BorderStyle = BorderStyle.None };
            body.Controls.Add(text);
            var wheel = new QueueRecordWheel(text);
            text.Disposed += delegate { wheel.Dispose(); };
            var buttons = new SettingsTablePanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Padding = new Padding(0, 10, 0, 0) };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var copy = new ThemeButton { Text = UiText("复制记录", "Copy record"), Dock = DockStyle.Fill };
            var close = new ThemeButton { Text = UiText("关闭", "Close"), Dock = DockStyle.Fill, DialogResult = DialogResult.Cancel };
            copy.Click += delegate { try { Clipboard.SetText(text.Text); } catch (System.Runtime.InteropServices.ExternalException) { FgsMessageBox.Show(dialog, UiText("剪贴板正忙，请重试。", "Clipboard is busy; retry.")); } };
            close.Click += delegate { dialog.Close(); };
            buttons.Controls.Add(copy, 1, 0); buttons.Controls.Add(close, 2, 0);
            layout.Controls.Add(note, 0, 0); layout.Controls.Add(body, 0, 1); layout.Controls.Add(buttons, 0, 2);
            dialog.Controls.Add(layout); dialog.CancelButton = close; AttachThemeWindow(dialog);
            dialog.Load += delegate {
                // Fit the outer window at 16:9 after WinForms DPI scaling has completed.
                Rectangle area = Screen.FromControl(this).WorkingArea;
                float dpi; using (Graphics graphics = dialog.CreateGraphics()) dpi = graphics.DpiX / 96f;
                int width = Math.Min((int)(1120 * dpi), Math.Min(Math.Max(320, area.Width - 32), Math.Max(180, area.Height - 32) * 16 / 9));
                dialog.MinimumSize = new Size(Math.Min((int)(640 * dpi), width), Math.Min((int)(360 * dpi), width * 9 / 16));
                dialog.Size = new Size(width, width * 9 / 16);
                dialog.Location = new Point(Math.Max(area.Left, Math.Min(area.Right - dialog.Width, Left + (Width - dialog.Width) / 2)),
                    Math.Max(area.Top, Math.Min(area.Bottom - dialog.Height, Top + (Height - dialog.Height) / 2)));
            };
            dialog.Shown += delegate { text.Select(0, 0); text.ScrollToCaret(); text.Focus(); };
            dialog.FormClosed += delegate { dialog.Dispose(); };
            dialog.Show(this);
        }
    }
    // Borderless RichEdit supplies selectable text without the native edit focus frame.
    internal sealed class QueueRecordText : RichTextBox, IThemeSurface
    {
        readonly string record;
        bool dark;
        public bool DarkTheme {
            get { return dark; }
            set {
                dark = value;
                int start = SelectionStart, length = SelectionLength;
                Rtf = QueueRecordPresentation.BuildRtf(record, Font.FontFamily.Name, dark);
                BorderStyle = BorderStyle.None;
                BackColor = dark ? UiTheme.Surface : SystemColors.Window;
                Select(Math.Min(start, TextLength), Math.Min(length, Math.Max(0, TextLength - start)));
            }
        }
        internal QueueRecordText(string content)
        {
            record = content;
            BorderStyle = BorderStyle.None; ReadOnly = true; WordWrap = true;
            ScrollBars = RichTextBoxScrollBars.None; DetectUrls = false; HideSelection = false;
            // Keep selection and copy; prevent any RichEdit URL or drag/drop interaction.
            EnableAutoDragDrop = false;
        }
    }

    internal static class QueueRecordPresentation
    {
        // Central palette: accent titles/output, neutral input, blue-grey processing, muted timings.
        // All paragraphs retain their own spacing when long filenames wrap.
        internal static string BuildRtf(string record, string font, bool dark)
        {
            string colors = dark
                ? @"\red235\green237\blue240;\red162\green167\blue174;\red91\green200\blue132;\red186\green206\blue220;\red136\green165\blue184;\red224\green173\blue100;\red238\green135\blue135;\red75\green81\blue87;"
                : @"\red35\green38\blue41;\red94\green102\blue110;\red0\green116\blue85;\red51\green83\blue107;\red69\green97\blue118;\red137\green88\blue19;\red176\green48\blue48;\red190\green195\blue200;";
            var rtf = new StringBuilder(@"{\rtf1\ansi\deff0\uc1{\fonttbl{\f0 ");
            Escape(rtf, font); rtf.Append(@";}}{\colortbl;").Append(colors).Append(@"}\viewkind4\f0 ");
            bool summary = false;
            foreach (string raw in record.Replace("\r\n", "\n").Split('\n')) {
                string line = raw.TrimEnd();
                if (line.Length == 0) continue;
                if (line.StartsWith("─", StringComparison.Ordinal)) {
                    // Blank separation replaces the old long, bright text rules.
                    Paragraph(rtf, "", 2, 19, false, 0, 140, 0); continue;
                }
                if (line == "队列任务完成记录") { Paragraph(rtf, line, 1, 28, true, 0, 0, 160); continue; }
                if (line.StartsWith("队列序号 ", StringComparison.Ordinal) || line == "统一模式 · 流程记录") {
                    Paragraph(rtf, line, 3, 23, true, 0, 150, 90); continue;
                }
                if (line.StartsWith("队列状态：", StringComparison.Ordinal) && !summary) {
                    summary = true; Paragraph(rtf, "队列统计", 3, 23, true, 0, 80, 90);
                }
                int colon = line.IndexOf('：');
                string label = colon >= 0 ? line.Substring(0, colon) : "";
                string value = colon >= 0 ? line.Substring(colon + 1).Trim() : line;
                if (label == "输入文件" || label == "输出文件" || label.StartsWith("输出目标", StringComparison.Ordinal)) {
                    Paragraph(rtf, label, 2, 19, false, 0, 70, 30);
                    Paragraph(rtf, value, label == "输出文件" ? 3 : 1, 21, false, 180, 0, 80);
                } else if (label == "处理项目") {
                    Paragraph(rtf, label, 4, 21, true, 0, 100, 60);
                    foreach (string item in value.Split(new string[] { " · " }, StringSplitOptions.RemoveEmptyEntries))
                        Paragraph(rtf, "•  " + item, 4, 20, false, 180, 0, 45);
                } else if (label == "执行流程") {
                    Paragraph(rtf, label, 5, 21, true, 0, 100, 60);
                    string[] stages = value.Split(new string[] { " → " }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < stages.Length; i++)
                        Paragraph(rtf, (i + 1).ToString(CultureInfo.InvariantCulture) + ".  " + stages[i], 5, 20, false, 180, 0, 45);
                } else if (label == "状态" || label == "队列状态") {
                    int color = value.Contains("失败") ? 7 : value.Contains("取消") || value == "未执行" ? 6 : 3;
                    Paragraph(rtf, line, color, 20, false, 0, 0, 60);
                } else if (label == "错误") Paragraph(rtf, line, 7, 20, false, 180, 60, 60);
                else if (label == "队列完成总时间") Paragraph(rtf, line, 3, 23, true, 0, 60, 100);
                else if (label == "适用队列序号") Paragraph(rtf, line, 2, 20, false, 0, 100, 60);
                else Paragraph(rtf, line, 2, 19, false, line.StartsWith("    ", StringComparison.Ordinal) ? 180 : 0, 20, 40);
            }
            rtf.Append('}'); return rtf.ToString();
        }
        static void Paragraph(StringBuilder rtf, string text, int color, int halfPoints, bool bold, int indent, int before, int after)
        {
            rtf.Append(@"\pard\ql\li").Append(indent).Append(@"\ri0\sb").Append(before).Append(@"\sa").Append(after)
                .Append(@"\sl280\slmult1\cf").Append(color).Append(@"\fs").Append(halfPoints).Append(bold ? @"\b " : @"\b0 ");
            Escape(rtf, text); rtf.Append(@"\par ");
        }
        static void Escape(StringBuilder target, string text)
        {
            foreach (char c in text) {
                if (c == '\\' || c == '{' || c == '}') target.Append('\\').Append(c);
                else if (c > 127) target.Append(@"\u").Append(((short)c).ToString(CultureInfo.InvariantCulture)).Append('?');
                else if (c >= ' ') target.Append(c);
            }
        }
    }

    // Explicit wheel support with no visible scrollbar; retain native read-only text selection.
    internal sealed class QueueRecordWheel : NativeWindow, IDisposable
    {
        readonly TextBoxBase text; int remainder;
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        internal QueueRecordWheel(TextBoxBase control)
        {
            text = control; text.HandleCreated += Created; text.HandleDestroyed += Destroyed;
            if (text.IsHandleCreated) AssignHandle(text.Handle);
        }
        void Created(object sender, EventArgs e) { AssignHandle(text.Handle); }
        void Destroyed(object sender, EventArgs e) { ReleaseHandle(); }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x020A) {
                remainder += (short)((message.WParam.ToInt64() >> 16) & 0xffff);
                int steps = remainder / 120; remainder %= 120;
                int lines = SystemInformation.MouseWheelScrollLines;
                if (lines == -1) lines = Math.Max(1, text.ClientSize.Height / text.Font.Height - 1);
                if (steps != 0 && lines > 0) SendMessage(text.Handle, 0x00B6, IntPtr.Zero, new IntPtr(-steps * lines));
                message.Result = IntPtr.Zero; return;
            }
            base.WndProc(ref message);
        }
        public void Dispose() { text.HandleCreated -= Created; text.HandleDestroyed -= Destroyed; ReleaseHandle(); }
    }
}
