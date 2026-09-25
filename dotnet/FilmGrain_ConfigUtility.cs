using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace FilmGrainStudioPreview
{
    internal sealed partial class MainForm
    {
        private static string QuoteUtilityArgument(string value)
        {
            // Windows command-line quoting: escape runs of backslashes immediately before a quote.
            StringBuilder result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '\"')
                {
                    result.Append('\\', slashes * 2 + 1);
                    result.Append('\"');
                }
                else
                {
                    result.Append('\\', slashes);
                    result.Append(c);
                }
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            result.Append('\"');
            return result.ToString();
        }

        private void RunGrainCacheUtility(Form owner, string root, string ffdir, Action refresh)
        {
            refresh();
            if (!Directory.Exists(root)) return;
            int count = 0, full = 0, small = 0;
            try
            {
                foreach (string mov in Directory.GetFiles(root, "*.mov", SearchOption.AllDirectories))
                {
                    count++;
                    string prefix = Path.Combine(Path.GetDirectoryName(mov), Path.GetFileNameWithoutExtension(mov));
                    if (File.Exists(prefix + "_HEVC_Lossless.mkv")) full++;
                    if (File.Exists(prefix + "_1080p_HEVC_Lossless.mkv")) small++;
                }
            }
            catch (Exception ex) { MessageBox.Show(owner, LF("config.detect.grain_fail", ex.Message)); return; }
            if (count == 0 || full == count && small == count) return;
            string script = Path.Combine(appRoot, "Utils", "FilmGrain_MOV_to_HEVC_Lossless_Cache.bat");
            if (!File.Exists(script)) { MessageBox.Show(owner, LF("config.cache_tool_missing", script), lang.T("config.cache_title"), MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            if (MessageBox.Show(owner, LF("config.cache_confirm", count - full, count - small), lang.T("config.cache_title"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            string cmd = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrEmpty(cmd)) cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cmd.exe");
            // Same cmd /d /s /c call and four overrides as the stable PS1 implementation.
            string inner = "call \"" + script.Replace("\"", "\"\"") + "\" 3 2>&1";
            var vars = new Dictionary<string, string> {
                { "FG_CACHE_NO_PAUSE", "1" }, { "FG_GRAIN_ROOT_OVERRIDE", root },
                { "FG_FFMPEG_OVERRIDE", Path.Combine(ffdir.TrimEnd('\\'), "ffmpeg.exe") },
                { "FG_FFPROBE_OVERRIDE", Path.Combine(ffdir.TrimEnd('\\'), "ffprobe.exe") } };
            ShowUtilityProcess(owner, lang.T("config.utility_cache"), cmd, "/d /s /c \"" + inner + "\"", vars, Path.Combine(appRoot, "Utils"));
            refresh();
        }

        private void RunLutPreviewUtility(Form owner, string root, string ffdir, Action refresh)
        {
            refresh();
            if (!Directory.Exists(root)) return;
            int missing = 0;
            try
            {
                string rootFull = Path.GetFullPath(root).TrimEnd('\\');
                if (rootFull.Length == 2 && rootFull[1] == ':') rootFull += Path.DirectorySeparatorChar;
                string previewRoot = Path.Combine(rootFull, "_LUT_PREVIEWS");
                foreach (string lut in Directory.GetFiles(rootFull, "*.cube", SearchOption.AllDirectories))
                {
                    if (lut.StartsWith(previewRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    string relative = lut.Substring(rootFull.Length + (rootFull.EndsWith(Path.DirectorySeparatorChar.ToString()) ? 0 : 1));
                    string relDir = Path.GetDirectoryName(relative) ?? "";
                    string jpg = Path.Combine(previewRoot, relDir, Path.GetFileNameWithoutExtension(lut) + "_preview.jpg");
                    if (!File.Exists(jpg)) missing++;
                }
            }
            catch (Exception ex) { MessageBox.Show(owner, LF("config.detect.lut_fail", ex.Message)); return; }
            if (missing == 0) return;
            string tools = Path.Combine(appRoot, "_LUT_Tools");
            string generator = Path.Combine(tools, "LUT_Preview_Batch_Gallery.ps1");
            string current = Path.Combine(tools, "LUT_Reference_Current.jpg");
            string reference = File.Exists(current) ? current : Path.Combine(tools, "LUT_Reference_Default.jpg");
            if (!File.Exists(generator) || !File.Exists(reference)) { MessageBox.Show(owner, lang.T("config.lut_tool_missing"), lang.T("config.lut_thumb_title"), MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            string label = reference == current ? lang.T("config.reference_current") : lang.T("config.reference_default");
            if (MessageBox.Show(owner, LF("config.lut_confirm", label, missing, reference), lang.T("config.lut_create_title"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            string args = "-NoLogo -NoProfile -ExecutionPolicy Bypass -File " + QuoteUtilityArgument(generator) + " -LutRoot " + QuoteUtilityArgument(root) +
                " -ReferencePath " + QuoteUtilityArgument(reference) + " -OutputRoot " + QuoteUtilityArgument(Path.Combine(root, "_LUT_PREVIEWS")) +
                " -FFmpegPath " + QuoteUtilityArgument(Path.Combine(ffdir.TrimEnd('\\'), "ffmpeg.exe")) + " -NonInteractive -NoPause";
            ShowUtilityProcess(owner, lang.T("config.utility_lut"), "powershell.exe", args, null, appRoot);
            refresh();
        }

        private void ShowUtilityProcess(Form owner, string title, string fileName, string arguments, Dictionary<string, string> vars, string cwd)
        {
            using (Form dialog = new Form())
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer())
            {
                dialog.Text = title; dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Size = new Size(820, 520); dialog.MinimumSize = new Size(700, 420); dialog.ShowInTaskbar = false;
                dialog.Font = UiFont(9f, FontStyle.Regular);
                TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
                dialog.Controls.Add(layout);
                RichTextBox log = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false, Font = new Font("Consolas", 9) };
                layout.Controls.Add(log, 0, 0);
                FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
                Button action = new Button { Text = lang.T("common.cancel"), Width = 88, Height = 30, Margin = new Padding(6, 7, 10, 5) };
                buttons.Controls.Add(action); layout.Controls.Add(buttons, 0, 1);
                Process proc = null;
                bool finished = false, cancelled = false;
                System.Collections.Concurrent.ConcurrentQueue<string> lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
                Action cancelTask = delegate
                {
                    if (finished) { dialog.Close(); return; }
                    if (MessageBox.Show(dialog, lang.T("task.cancel_confirm"), lang.T("task.cancel_title"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    cancelled = true; action.Enabled = false;
                    try
                    {
                        ProcessStartInfo kill = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "taskkill.exe"), "/PID " + proc.Id + " /T /F") { UseShellExecute = false, CreateNoWindow = true };
                        using (Process kp = Process.Start(kill)) { kp.WaitForExit(5000); }
                    }
                    catch { try { proc.Kill(); } catch { } }
                };
                action.Click += delegate { cancelTask(); };
                dialog.FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!finished) { e.Cancel = true; cancelTask(); } };
                timer.Interval = 120;
                timer.Tick += delegate
                {
                    string line;
                    int n = 0;
                    while (n++ < 600 && lines.TryDequeue(out line)) log.AppendText(line + Environment.NewLine);
                    if (proc == null || finished || !proc.HasExited) return;
                    proc.WaitForExit();
                    while (lines.TryDequeue(out line)) log.AppendText(line + Environment.NewLine);
                    finished = true;
                    timer.Stop();
                    log.AppendText(Environment.NewLine + "=== " + (cancelled ? lang.T("common.cancel") : "Exit code " + proc.ExitCode) + " ===" + Environment.NewLine);
                    action.Enabled = true; action.Text = lang.T("common.close");
                };
                dialog.Shown += delegate
                {
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(fileName, arguments) { UseShellExecute = false, CreateNoWindow = true,
                            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd };
                        if (vars != null) foreach (var pair in vars) psi.EnvironmentVariables[pair.Key] = pair.Value;
                        proc = new Process { StartInfo = psi };
                        proc.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lines.Enqueue(e.Data); };
                        proc.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lines.Enqueue(e.Data); };
                        if (!proc.Start()) throw new InvalidOperationException("Unable to start utility process.");
                        proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
                        log.AppendText(title + Environment.NewLine + new string('=', 68) + Environment.NewLine);
                        timer.Start();
                    }
                    catch (Exception ex) { finished = true; log.AppendText(ex.Message + Environment.NewLine); action.Text = lang.T("common.close"); }
                };
                dialog.ShowDialog(owner);
                timer.Stop();
                if (proc != null) proc.Dispose();
            }
        }
    }
}
