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
            if (Get(state,"FG_MODE") == "AV1BAKE") {
                string codec = Get(state,"FG_BAKE_ENCODER");
                bool cpu = codec == "X264" || codec == "X265";
                string quality = Get(state,"FG_BAKE_RATE_CONTROL") == "CQ"
                    ? (cpu ? "CRF " : "CQ ") + Get(state,"FG_BAKE_CQ")
                    : "VBR " + Get(state,"FG_BAKE_BITRATE") + " kbps";
                parts.Add("AV1 源内颗粒烘焙");
                if (media != null && media.IsHdr) parts.Add("HDR 保留（" + media.ColorTransfer + "）");
                parts.Add("解码 " + (Get(state,"FG_BAKE_GPU_DECODE") == "1" ? "NVDEC（实验）" : "libdav1d"));
                string preset = cpu ? Get(state,codec == "X265" ? "FG_CPU_X265_PRESET" : "FG_X264_PRESET") : Get(state,"FG_BAKE_PRESET");
                parts.Add("编码 " + codec + " / " + quality + (preset.Length > 0 ? " / Preset " + preset : "") +
                    " / 输出 " + task.RecommendedOutputFps.ToString("0.###", CultureInfo.InvariantCulture) + " fps");
                string engines = Get(state,"FG_BAKE_SFE");
                if (codec == "HEVC_NVENC" && engines != "" && engines != "0") parts.Add("HEVC 多引擎 " + engines);
                return string.Join(" · ", parts.ToArray());
            }
            if (task.NoReencode) parts.Add("视频流复制（不重编码）");
            else {
                if (media != null && media.IsInterlaced && Get(state,"FG_DEINTERLACE") == "AUTO") parts.Add("反交错 " + Get(state,"FG_DEINT_METHOD"));
                if (PreprocessingCore.Enabled(state)) parts.Add("Bilateral（sigmaS=" + Get(state,"FG_PRE_BILATERAL_SIGMA_S") + "，sigmaR=" + Get(state,"FG_PRE_BILATERAL_SIGMA_R") + "，planes=" + Get(state,"FG_PRE_BILATERAL_PLANES") + "）");
                CinematicFramePlan frame; ResizePlan resize; string reason;
                if (media != null && CinematicFrameCore.TryBuild(media, state, out frame, out reason)) {
                    if (Get(state,"FG_CINEMATIC_FRAME") == "1") parts.Add("画幅 " + Get(state,"FG_FRAME_MODE") + "（" + frame.ActiveWidth + "×" + frame.ActiveHeight + "）");
                    if (ResizeCore.Enabled(state) && ResizeCore.TryBuild(media, state, frame, out resize, out reason)) parts.Add("缩放 " + resize.Width + "×" + resize.Height + "（Vulkan / " + ResizeCore.AlgorithmName(resize.Algorithm) + "）");
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
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); butt