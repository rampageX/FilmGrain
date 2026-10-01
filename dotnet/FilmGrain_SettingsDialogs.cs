using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace FilmGrainStudioPreview
{
    internal sealed partial class MainForm
    {
        private void ShowNetworkProxyDialog()
        {
            config.Load();
            using (Form dlg = new Form())
            {
                dlg.Text = UiText("网络代理设置", "Network proxy settings");
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false; dlg.MinimizeBox = false; dlg.ShowInTaskbar = false;
                dlg.ClientSize = new Size(540, 245); dlg.Font = UiFont(9f, FontStyle.Regular);
                Label modeLabel = new Label { Text = UiText("代理方式", "Proxy mode"), Left = 22, Top = 22, Width = 110, Height = 26, TextAlign = ContentAlignment.MiddleLeft };
                ComboBox mode = new ComboBox { Left = 140, Top = 20, Width = 370, DropDownStyle = ComboBoxStyle.DropDownList };
                mode.Items.Add(new ProxyModeItem("SYSTEM", UiText("系统代理 / 环境变量", "System proxy / environment")));
                mode.Items.Add(new ProxyModeItem("CUSTOM", UiText("自定义 HTTP(S) 代理", "Custom HTTP(S) proxy")));
                mode.Items.Add(new ProxyModeItem("DIRECT", UiText("直连", "Direct connection")));
                string savedMode = config.Get("NETWORK_PROXY_MODE").ToUpperInvariant();
                int selected = savedMode == "CUSTOM" ? 1 : (savedMode == "DIRECT" ? 2 : 0); mode.SelectedIndex = selected;
                Label urlLabel = new Label { Text = UiText("代理地址", "Proxy URL"), Left = 22, Top = 66, Width = 110, Height = 26, TextAlign = ContentAlignment.MiddleLeft };
                TextBox url = new TextBox { Left = 140, Top = 64, Width = 370, Text = config.Get("NETWORK_PROXY_URL") };
                Label help = new Label { Left = 140, Top = 96, Width = 370, Height = 48, ForeColor = ColorMuted, Text = UiText("例如 http://127.0.0.1:7890。设置会用于 FGS 下载、安装向导及其子进程。", "Example: http://127.0.0.1:7890. Applies to FGS downloads, setup, and child processes.") };
                Label status = new Label { Left = 140, Top = 144, Width = 370, Height = 24, ForeColor = ColorMuted, Text = "" };
                Button test = new Button { Left = 140, Top = 179, Width = 112, Height = 30, Text = UiText("测试代理", "Test proxy") };
                Button save = new Button { Left = 310, Top = 179, Width = 92, Height = 30, Text = UiText("保存", "Save"), DialogResult = DialogResult.None };
                Button cancel = new Button { Left = 410, Top = 179, Width = 100, Height = 30, Text = UiText("取消", "Cancel"), DialogResult = DialogResult.Cancel };
                dlg.Controls.Add(modeLabel); dlg.Controls.Add(mode); dlg.Controls.Add(urlLabel); dlg.Controls.Add(url); dlg.Controls.Add(help); dlg.Controls.Add(status); dlg.Controls.Add(test); dlg.Controls.Add(save); dlg.Controls.Add(cancel);
                dlg.CancelButton = cancel;
                EventHandler enabled = delegate { ProxyModeItem item = mode.SelectedItem as ProxyModeItem; url.Enabled = item != null && item.Value == NetworkProxyCore.CustomMode; test.Enabled = item != null; };
                mode.SelectedIndexChanged += enabled; enabled(null, EventArgs.Empty);
                test.Click += delegate {
                    ProxyModeItem item = mode.SelectedItem as ProxyModeItem;
                    string proxy = url.Text.Trim(), error = "";
                    if (item == null) return;
                    if (item.Value == NetworkProxyCore.CustomMode && !NetworkProxyCore.TryValidateProxy(proxy, out proxy, out error)) { MessageBox.Show(dlg, error, "Film Grain Studio"); return; }
                    test.Enabled = false; status.Text = UiText("正在测试…", "Testing...");
                    ThreadPool.QueueUserWorkItem(delegate {
                        string result; bool ok = NetworkProxyCore.TestProxy(item.Value, proxy, out result);
                        if (dlg.IsDisposed || !dlg.IsHandleCreated) return;
                        try { dlg.BeginInvoke((MethodInvoker)delegate { test.Enabled = true; status.Text = (ok ? "✓ " : "✗ ") + result; status.ForeColor = ok ? Color.DarkGreen : Color.Firebrick; }); } catch (InvalidOperationException) { }
                    });
                };
                save.Click += delegate {
                    ProxyModeItem item = mode.SelectedItem as ProxyModeItem;
                    if (item == null) return;
                    string proxy = url.Text.Trim(), error = "";
                    if (item.Value == NetworkProxyCore.CustomMode && !NetworkProxyCore.TryValidateProxy(proxy, out proxy, out error)) { MessageBox.Show(dlg, error, "Film Grain Studio"); return; }
                    try { config.Save(new Dictionary<string, string> { { "NETWORK_PROXY_MODE", item.Value }, { "NETWORK_PROXY_URL", item.Value == NetworkProxyCore.CustomMode ? proxy : "" } }); NetworkProxyCore.Apply(item.Value, proxy); dlg.DialogResult = DialogResult.OK; dlg.Close(); }
                    catch (Exception ex) { MessageBox.Show(dlg, ex.Message, "Film Grain Studio", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                };
                dlg.ShowDialog(this);
            }
        }

        private sealed class ProxyModeItem
        {
            internal readonly string Value; private readonly string label;
            internal ProxyModeItem(string value, string text) { Value = value; label = text; }
            public override string ToString() { return label; }
        }

        private Label ConfigLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Dock = DockStyle.Fill;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Margin = new Padding(4);
            return l;
        }

        private Control ConfigLabelWithReadme(string text, string fragment, Form owner)
        {
            TableLayoutPanel panel = new TableLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.Margin = Padding.Empty;
            panel.ColumnCount = 2;
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32));
            panel.Controls.Add(ConfigLabel(text), 0, 0);
            LinkLabel help = new LinkLabel();
            help.Text = "[?]"; help.AutoSize = false; help.Dock = DockStyle.Fill;
            help.TextAlign = ContentAlignment.MiddleCenter;
            help.LinkClicked += delegate { OpenReadmeLink(owner, fragment); };
            panel.Controls.Add(help, 1, 0);
            return panel;
        }

        private void OpenReadmeLink(Form owner, string fragment)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "https://github.com/rampageX/FilmGrain/blob/master/README.md#" + fragment;
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, ex.Message);
            }
        }

        private TextBox ConfigTextBox(string value)
        {
            TextBox t = new TextBox();
            t.Text = value;
            t.Dock = DockStyle.Fill;
            t.Margin = new Padding(4, 8, 4, 6);
            return t;
        }

        private Button ConfigButton(string text)
        {
            Button b = new Button();
            b.Text = text;
            b.Dock = DockStyle.Fill;
            b.Margin = new Padding(4, 5, 4, 5);
            return b;
        }

        private Button RefreshButton()
        {
            Button b = new Button();
            b.Text = "↻";
            b.Dock = DockStyle.Fill;
            b.Margin = new Padding(2, 5, 2, 5);
            return b;
        }

        private Label ConfigStatusLabel(string text)
        {
            Label l = new Label();
            l.Dock = DockStyle.Fill;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.ForeColor = ColorMuted;
            l.Margin = new Padding(4, 0, 4, 2);
            l.Text = text;
            return l;
        }

        private bool PickFolder(IWin32Window owner, TextBox target, string description, bool allowNewFolder)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = description;
                dlg.ShowNewFolderButton = allowNewFolder;
                try { if (Directory.Exists(target.Text)) dlg.SelectedPath = target.Text; } catch { }
                if (dlg.ShowDialog(owner) != DialogResult.OK) return false;
                target.Text = dlg.SelectedPath;
                return true;
            }
        }

        private bool PickExe(IWin32Window owner, TextBox target, string title, string fileName)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = title;
                dlg.Filter = lang.T("dialog.exe_filter");
                dlg.FileName = fileName;
                try { if (File.Exists(target.Text)) dlg.InitialDirectory = Path.GetDirectoryName(target.Text); } catch { }
                if (dlg.ShowDialog(owner) != DialogResult.OK) return false;
                target.Text = dlg.FileName;
                return true;
            }
        }

        private string GetToolVersion(string exePath, string arguments, out bool ok)
        {
            ok = false;
            if (!File.Exists(exePath)) return lang.T("config.version_not_found");
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exePath;
                psi.Arguments = arguments;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(5000))
                    {
                        try { p.Kill(); } catch { }
                        return lang.T("config.version_cannot_run");
                    }
                    ok = p.ExitCode == 0;
                    string text = stdout.Length > 0 ? stdout : stderr;
                    using (StringReader reader = new StringReader(text))
                    {
                        string first = reader.ReadLine();
                        if (!string.IsNullOrWhiteSpace(first))
                        {
                            string line = first.Trim();
                            string name = Path.GetFileName(exePath);
                            if (string.Equals(name, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                                return HardwareCapabilityCore.ShortFfmpegVersionLine(line);
                            return line;
                        }
                    }
                    return ok ? lang.T("config.version_unknown") : lang.T("config.version_cannot_run");
                }
            }
            catch { return lang.T("config.version_cannot_run"); }
        }

        private bool TestWritableDirectory(string path, out string error)
        {
            error = "";
            try
            {
                Directory.CreateDirectory(path);
                string test = Path.Combine(path, ".fgs_write_test_" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(test, "FGS");
                File.Delete(test);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private void ShowPathConfigurationDialog()
        {
            config.Load();
            using (Form dlg = new Form())
            {
                dlg.Text = lang.T("config.title");
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.ClientSize = new Size(850, 664);
                dlg.Font = UiFont(9f, FontStyle.Regular);

                TableLayoutPanel table = new TableLayoutPanel();
                table.Dock = DockStyle.Fill;
                table.Padding = new Padding(12, 12, 12, 10);
                table.ColumnCount = 4;
                table.RowCount = 15;
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
                float[] rowHeights = new float[] { 42, 50, 42, 36, 42, 36, 42, 36, 42, 36, 46, 46, 46 };
                foreach (float h in rowHeights) table.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
                table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
                dlg.Controls.Add(table);

                TextBox txtFfmpeg = ConfigTextBox(config.Get("FFMPEG_DIR"));
                string savedFrucExe = config.Get("FRUC_FFMPEG_PATH");
                string customFrucDir = "";
                try { if (!string.IsNullOrWhiteSpace(savedFrucExe)) customFrucDir = Path.GetDirectoryName(savedFrucExe) ?? ""; } catch { }
                CheckBox chkFrucSame = new CheckBox(); chkFrucSame.Text = UiText("与正式版使用相同 FFmpeg", "Use main FFmpeg");
                chkFrucSame.Checked = config.FrucUsesMainFfmpeg(); chkFrucSame.Dock = DockStyle.Fill; chkFrucSame.AutoSize = true;
                chkFrucSame.Margin = new Padding(4, 7, 3, 3);
                TextBox txtFruc = ConfigTextBox(chkFrucSame.Checked ? config.Get("FFMPEG_DIR") : customFrucDir);
                TableLayoutPanel frucPanel = new TableLayoutPanel(); frucPanel.Dock = DockStyle.Fill;
                frucPanel.Margin = Padding.Empty; frucPanel.ColumnCount = 2;
                frucPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                frucPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                frucPanel.Controls.Add(chkFrucSame, 0, 0); frucPanel.Controls.Add(txtFruc, 1, 0);
                TextBox txtGrav = ConfigTextBox(config.Get("GRAV1SYNTH"));
                TextBox txtGrain = ConfigTextBox(config.Get("GRAIN_ROOT"));
                TextBox txtLut = ConfigTextBox(config.Get("LUT_ROOT"));
                Button btnFfmpeg = ConfigButton(lang.T("config.browse"));
                Button btnFruc = ConfigButton(lang.T("config.browse"));
                Button btnGrav = ConfigButton(lang.T("config.browse"));
                Button btnGrain = ConfigButton(lang.T("config.browse"));
                Button btnLut = ConfigButton(lang.T("config.browse"));
                Button refreshFfmpeg = RefreshButton();
                Button refreshFruc = RefreshButton();
                Button refreshGrav = RefreshButton();
                Button refreshGrain = RefreshButton();
                Button refreshLut = RefreshButton();
                Label statusFfmpeg = ConfigStatusLabel(lang.T("config.not_checked"));
                Label statusFruc = ConfigStatusLabel(lang.T("config.not_checked"));
                Label statusGrav = ConfigStatusLabel(lang.T("config.not_checked"));
                Label statusGrain = ConfigStatusLabel(lang.T("config.not_checked"));
                Label statusLut = ConfigStatusLabel(lang.T("config.not_checked"));
                Button buildCache = ConfigButton(lang.T("config.build_cache"));
                Button buildThumbs = ConfigButton(lang.T("config.build_thumbs"));
                ToolTip configTip = new ToolTip();
                configTip.SetToolTip(buildCache, lang.T("config.tip.cache"));
                configTip.SetToolTip(buildThumbs, lang.T("config.tip.thumbs"));

                table.Controls.Add(ConfigLabel(lang.T("config.ffmpeg_dir")), 0, 0);
                table.Controls.Add(txtFfmpeg, 1, 0); table.Controls.Add(btnFfmpeg, 2, 0); table.Controls.Add(refreshFfmpeg, 3, 0);
                table.Controls.Add(statusFfmpeg, 1, 1); table.SetColumnSpan(statusFfmpeg, 3);
                table.Controls.Add(ConfigLabel("FRUC Vulkan FFmpeg"), 0, 2);
                table.Controls.Add(frucPanel, 1, 2); table.Controls.Add(btnFruc, 2, 2); table.Controls.Add(refreshFruc, 3, 2);
                table.Controls.Add(statusFruc, 1, 3); table.SetColumnSpan(statusFruc, 3);
                table.Controls.Add(ConfigLabel("grav1synth"), 0, 4);
                table.Controls.Add(txtGrav, 1, 4); table.Controls.Add(btnGrav, 2, 4); table.Controls.Add(refreshGrav, 3, 4);
                table.Controls.Add(statusGrav, 1, 5); table.SetColumnSpan(statusGrav, 3);
                table.Controls.Add(ConfigLabelWithReadme(lang.T("config.grain_root"), "hevc真实扫描-grain-plate", dlg), 0, 6);
                table.Controls.Add(txtGrain, 1, 6); table.Controls.Add(btnGrain, 2, 6); table.Controls.Add(refreshGrain, 3, 6);
                table.Controls.Add(statusGrain, 1, 7); table.Controls.Add(buildCache, 2, 7); table.SetColumnSpan(buildCache, 2);
                table.Controls.Add(ConfigLabelWithReadme(lang.T("config.lut_root"), "lut-gallery-与-film-look", dlg), 0, 8);
                table.Controls.Add(txtLut, 1, 8); table.Controls.Add(btnLut, 2, 8); table.Controls.Add(refreshLut, 3, 8);
                table.Controls.Add(statusLut, 1, 9); table.Controls.Add(buildThumbs, 2, 9); table.SetColumnSpan(buildThumbs, 2);

                TableLayoutPanel tempPanel = new TableLayoutPanel();
                tempPanel.Dock = DockStyle.Fill; tempPanel.ColumnCount = 2; tempPanel.Margin = Padding.Empty;
                tempPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); tempPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                ComboBox cmbTemp = new ComboBox(); cmbTemp.DropDownStyle = ComboBoxStyle.DropDownList; cmbTemp.Dock = DockStyle.Fill; cmbTemp.Margin = new Padding(4, 8, 4, 6);
                cmbTemp.Items.Add(lang.T("config.temp_video")); cmbTemp.Items.Add(lang.T("config.temp_system")); cmbTemp.Items.Add(lang.T("config.temp_custom"));
                cmbTemp.SelectedIndex = string.Equals(config.Get("TEMP_MODE"), "SYSTEM", StringComparison.OrdinalIgnoreCase) ? 1 : (string.Equals(config.Get("TEMP_MODE"), "CUSTOM", StringComparison.OrdinalIgnoreCase) ? 2 : 0);
                TextBox txtTemp = ConfigTextBox(config.Get("TEMP_CUSTOM_DIR"));
                tempPanel.Controls.Add(cmbTemp, 0, 0); tempPanel.Controls.Add(txtTemp, 1, 0);
                Button btnTemp = ConfigButton(lang.T("config.browse"));
                table.Controls.Add(ConfigLabel(lang.T("config.temp_dir")), 0, 10); table.Controls.Add(tempPanel, 1, 10); table.Controls.Add(btnTemp, 2, 10);

                TableLayoutPanel outputPanel = new TableLayoutPanel();
                outputPanel.Dock = DockStyle.Fill; outputPanel.ColumnCount = 2; outputPanel.Margin = Padding.Empty;
                outputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); outputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                ComboBox cmbOutput = new ComboBox(); cmbOutput.DropDownStyle = ComboBoxStyle.DropDownList; cmbOutput.Dock = DockStyle.Fill; cmbOutput.Margin = new Padding(4, 8, 4, 6);
                cmbOutput.Items.Add(lang.T("config.output_video")); cmbOutput.Items.Add(lang.T("config.output_custom"));
                cmbOutput.SelectedIndex = string.Equals(config.Get("OUTPUT_MODE"), "CUSTOM", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                TextBox txtOutput = ConfigTextBox(config.Get("OUTPUT_CUSTOM_DIR"));
                outputPanel.Controls.Add(cmbOutput, 0, 0); outputPanel.Controls.Add(txtOutput, 1, 0);
                Button btnOutput = ConfigButton(lang.T("config.browse"));
                table.Controls.Add(ConfigLabel(lang.T("config.output_dir")), 0, 11); table.Controls.Add(outputPanel, 1, 11); table.Controls.Add(btnOutput, 2, 11);

                NumericUpDown logEntryLimit = new NumericUpDown(); logEntryLimit.Minimum = 0; logEntryLimit.Maximum = 10000; logEntryLimit.Increment = 10; logEntryLimit.Dock = DockStyle.Left; logEntryLimit.Width = 120; logEntryLimit.Margin = new Padding(4, 8, 4, 6);
                logEntryLimit.Value = Math.Max(logEntryLimit.Minimum, Math.Min(logEntryLimit.Maximum, config.GetInt("LOG_ENTRY_LIMIT", 100)));
                Label logLimitInfo = new Label(); logLimitInfo.Text = UiText("0 = 不限制", "0 = unlimited"); logLimitInfo.Dock = DockStyle.Fill; logLimitInfo.TextAlign = ContentAlignment.MiddleLeft; logLimitInfo.ForeColor = ColorMuted;
                table.Controls.Add(ConfigLabel(UiText("日志条目限制", "Log entry limit")), 0, 12); table.Controls.Add(logEntryLimit, 1, 12); table.Controls.Add(logLimitInfo, 2, 12); table.SetColumnSpan(logLimitInfo, 2);

                Label note = new Label();
                note.Dock = DockStyle.Fill; note.ForeColor = ColorMuted; note.TextAlign = ContentAlignment.TopLeft; note.Padding = new Padding(4, 8, 4, 0);
                note.Text = LF("config.note", config.ConfigPath);
                table.Controls.Add(note, 0, 13); table.SetColumnSpan(note, 4);

                FlowLayoutPanel buttons = new FlowLayoutPanel();
                buttons.Dock = DockStyle.Fill; buttons.FlowDirection = FlowDirection.RightToLeft; buttons.WrapContents = false;
                Button save = new Button(); save.Text = lang.T("config.save"); save.Width = 82;
                Button cancel = new Button(); cancel.Text = lang.T("config.cancel"); cancel.Width = 82; cancel.DialogResult = DialogResult.Cancel;
                Button restore = new Button(); restore.Text = lang.T("config.defaults"); restore.Width = 104;
                buttons.Controls.Add(save); buttons.Controls.Add(cancel); buttons.Controls.Add(restore);
                table.Controls.Add(buttons, 0, 14); table.SetColumnSpan(buttons, 4);
                dlg.CancelButton = cancel;

                EventHandler updateStorage = delegate
                {
                    bool tempCustom = cmbTemp.SelectedIndex == 2;
                    txtTemp.Enabled = tempCustom; btnTemp.Enabled = tempCustom;
                    bool outputCustom = cmbOutput.SelectedIndex == 1;
                    txtOutput.Enabled = outputCustom; btnOutput.Enabled = outputCustom;
                };
                cmbTemp.SelectedIndexChanged += updateStorage;
                cmbOutput.SelectedIndexChanged += updateStorage;
                updateStorage(null, EventArgs.Empty);

                txtFruc.Enabled = !chkFrucSame.Checked; btnFruc.Enabled = !chkFrucSame.Checked;
                chkFrucSame.CheckedChanged += delegate
                {
                    if (chkFrucSame.Checked)
                    {
                        customFrucDir = txtFruc.Text.Trim();
                        txtFruc.Text = txtFfmpeg.Text;
                    }
                    else txtFruc.Text = customFrucDir;
                    txtFruc.Enabled = !chkFrucSame.Checked;
                    btnFruc.Enabled = !chkFrucSame.Checked;
                    statusFruc.Text = lang.T("config.path_changed");
                };
                txtFfmpeg.TextChanged += delegate { if (chkFrucSame.Checked) txtFruc.Text = txtFfmpeg.Text; };
                txtFruc.TextChanged += delegate
                {
                    if (!chkFrucSame.Checked) customFrucDir = txtFruc.Text;
                    statusFruc.Text = lang.T("config.path_changed");
                };

                EventHandler markFfmpeg = delegate { statusFfmpeg.Text = lang.T("config.path_changed"); };
                EventHandler markGrav = delegate { statusGrav.Text = lang.T("config.path_changed"); };
                EventHandler markGrain = delegate { statusGrain.Text = lang.T("config.path_changed"); buildCache.Enabled = false; };
                EventHandler markLut = delegate { statusLut.Text = lang.T("config.path_changed"); buildThumbs.Enabled = false; };
                txtFfmpeg.TextChanged += markFfmpeg; txtGrav.TextChanged += markGrav; txtGrain.TextChanged += markGrain; txtLut.TextChanged += markLut;

                Action updateFfmpeg = delegate
                {
                    string dir = txtFfmpeg.Text.Trim().TrimEnd('\\');
                    statusFfmpeg.Text = lang.T("config.detect.ffmpeg"); Application.DoEvents();
                    bool ffOk, fpOk;
                    string ff = GetToolVersion(Path.Combine(dir, "ffmpeg.exe"), "-version", out ffOk);
                    string fp = GetToolVersion(Path.Combine(dir, "ffprobe.exe"), "-version", out fpOk);
                    statusFfmpeg.Text = "ffmpeg.exe   " + (ffOk ? "✔ " : "✘ ") + ff + Environment.NewLine + "ffprobe.exe  " + (fpOk ? "✔ " : "✘ ") + fp;
                };
                Action updateFruc = delegate
                {
                    string ff = Path.Combine(txtFruc.Text.Trim().TrimEnd('\\'), "ffmpeg.exe");
                    statusFruc.Text = lang.T("config.detect.ffmpeg"); Application.DoEvents();
                    bool ok;
                    string version = GetToolVersion(ff, "-version", out ok);
                    bool filter = ok && NativeHevcCore.HasFrucFilter(ff);
                    statusFruc.Text = "ffmpeg.exe  " + (ok ? "✔ " : "✘ ") + version +
                        "    fruc_vulkan " + (filter ? "✔" : "✘");
                };
                Action updateGrav = delegate
                {
                    statusGrav.Text = lang.T("config.detect.grav"); Application.DoEvents();
                    bool ok; string version = GetToolVersion(txtGrav.Text.Trim(), "--version", out ok);
                    statusGrav.Text = "grav1synth.exe  " + (ok ? "✔ " : "✘ ") + version;
                };
                Action updateGrain = delegate
                {
                    string root = txtGrain.Text.Trim(); buildCache.Enabled = false;
                    if (!Directory.Exists(root)) { statusGrain.Text = lang.T("config.detect.grain_missing"); return; }
                    statusGrain.Text = lang.T("config.detect.grain_scan"); Application.DoEvents();
                    try
                    {
                        string[] movs = Directory.GetFiles(root, "*.mov", SearchOption.AllDirectories);
                        int full = 0, small = 0;
                        foreach (string mov in movs)
                        {
                            string dir = Path.GetDirectoryName(mov) ?? "";
                            string baseName = Path.GetFileNameWithoutExtension(mov);
                            if (File.Exists(Path.Combine(dir, baseName + "_HEVC_Lossless.mkv"))) full++;
                            if (File.Exists(Path.Combine(dir, baseName + "_1080p_HEVC_Lossless.mkv"))) small++;
                        }
                        statusGrain.Text = movs.Length == 0 ? lang.T("config.detect.grain_zero") : LF("config.detect.grain_count", movs.Length, full, small);
                        buildCache.Enabled = movs.Length > 0 && (full < movs.Length || small < movs.Length);
                    }
                    catch (Exception ex) { statusGrain.Text = LF("config.detect.grain_fail", ex.Message); }
                };
                Action updateLut = delegate
                {
                    string root = txtLut.Text.Trim(); buildThumbs.Enabled = false;
                    if (!Directory.Exists(root)) { statusLut.Text = lang.T("config.detect.lut_missing"); return; }
                    statusLut.Text = lang.T("config.detect.lut_scan"); Application.DoEvents();
                    try
                    {
                        string rootFull = Path.GetFullPath(root).TrimEnd('\\');
                        string previewRoot = Path.Combine(rootFull, "_LUT_PREVIEWS");
                        string prefix = rootFull + Path.DirectorySeparatorChar;
                        List<string> luts = new List<string>();
                        foreach (string file in Directory.GetFiles(rootFull, "*.cube", SearchOption.AllDirectories))
                        {
                            if (file.StartsWith(previewRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                            luts.Add(file);
                        }
                        int previews = 0;
                        foreach (string lut in luts)
                        {
                            string relative = lut.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? lut.Substring(prefix.Length) : Path.GetFileName(lut);
                            string relDir = Path.GetDirectoryName(relative) ?? "";
                            string dst = relDir.Length == 0 ? previewRoot : Path.Combine(previewRoot, relDir);
                            string jpg = Path.Combine(dst, Path.GetFileNameWithoutExtension(lut) + "_preview.jpg");
                            if (File.Exists(jpg)) previews++;
                        }
                        statusLut.Text = luts.Count == 0 ? lang.T("config.detect.lut_zero") : LF("config.detect.lut_count", luts.Count, previews);
                        buildThumbs.Enabled = luts.Count > previews;
                    }
                    catch (Exception ex) { statusLut.Text = LF("config.detect.lut_fail", ex.Message); }
                };

                refreshFfmpeg.Click += delegate { updateFfmpeg(); };
                refreshFruc.Click += delegate { updateFruc(); };
                refreshGrav.Click += delegate { updateGrav(); };
                refreshGrain.Click += delegate { updateGrain(); };
                refreshLut.Click += delegate { updateLut(); };
                btnFfmpeg.Click += delegate { if (PickFolder(dlg, txtFfmpeg, lang.T("dialog.ffmpeg_folder"), false)) updateFfmpeg(); };
                btnFruc.Click += delegate { if (PickFolder(dlg, txtFruc, lang.T("dialog.ffmpeg_folder"), false)) updateFruc(); };
                btnGrav.Click += delegate { if (PickExe(dlg, txtGrav, lang.T("dialog.grav_exe"), "grav1synth.exe")) updateGrav(); };
                btnGrain.Click += delegate { if (PickFolder(dlg, txtGrain, lang.T("dialog.grain_root"), false)) updateGrain(); };
                btnLut.Click += delegate { if (PickFolder(dlg, txtLut, lang.T("dialog.lut_root"), false)) updateLut(); };
                btnTemp.Click += delegate { PickFolder(dlg, txtTemp, lang.T("config.temp_browse"), true); };
                btnOutput.Click += delegate { PickFolder(dlg, txtOutput, lang.T("config.output_browse"), true); };

                buildCache.Click += delegate { RunGrainCacheUtility(dlg, txtGrain.Text.Trim(), txtFfmpeg.Text.Trim(), updateGrain); };
                buildThumbs.Click += delegate { RunLutPreviewUtility(dlg, txtLut.Text.Trim(), txtFfmpeg.Text.Trim(), updateLut); };

                restore.Click += delegate
                {
                    txtFfmpeg.Text = config.GetDefault("FFMPEG_DIR");
                    chkFrucSame.Checked = true; customFrucDir = ""; savedFrucExe = ""; txtFruc.Text = txtFfmpeg.Text;
                    txtGrav.Text = config.GetDefault("GRAV1SYNTH");
                    txtGrain.Text = config.GetDefault("GRAIN_ROOT");
                    txtLut.Text = config.GetDefault("LUT_ROOT");
                    cmbTemp.SelectedIndex = 0; txtTemp.Text = "";
                    cmbOutput.SelectedIndex = 0; txtOutput.Text = "";
                    logEntryLimit.Value = 100;
                };

                save.Click += delegate
                {
                    string ffdir = txtFfmpeg.Text.Trim().TrimEnd('\\');
                    if (!Directory.Exists(ffdir)) { MessageBox.Show(dlg, LF("config.ffmpeg_dir_missing", ffdir), lang.T("config.path_title"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    foreach (string exe in new string[] { "ffmpeg.exe", "ffprobe.exe" })
                    {
                        if (!File.Exists(Path.Combine(ffdir, exe))) { MessageBox.Show(dlg, LF("config.ffmpeg_exe_missing", exe, ffdir), lang.T("config.path_title"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    }
                    string frucExe = "";
                    if (!chkFrucSame.Checked)
                    {
                        string frucDir = txtFruc.Text.Trim().TrimEnd('\\');
                        frucExe = Path.Combine(frucDir, "ffmpeg.exe");
                        if (!Directory.Exists(frucDir) || !File.Exists(frucExe))
                        {
                            MessageBox.Show(dlg, LF("config.ffmpeg_exe_missing", "ffmpeg.exe", frucDir), lang.T("config.path_title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        if (!NativeHevcCore.HasFrucFilter(frucExe))
                        {
                            MessageBox.Show(dlg, UiText("选定的 ffmpeg.exe 不包含 fruc_vulkan 滤镜。", "The selected ffmpeg.exe does not provide fruc_vulkan."), lang.T("config.path_title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                    }
                    if (!File.Exists(txtGrav.Text.Trim())) { MessageBox.Show(dlg, LF("config.grav_missing", txtGrav.Text.Trim()), lang.T("config.path_title"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    if (!Directory.Exists(txtGrain.Text.Trim())) { MessageBox.Show(dlg, LF("config.item_missing", lang.T("config.item_grain_root"), txtGrain.Text.Trim()), lang.T("config.path_title"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    if (!Directory.Exists(txtLut.Text.Trim())) { MessageBox.Show(dlg, LF("config.item_missing", lang.T("config.item_lut_root"), txtLut.Text.Trim()), lang.T("config.path_title"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

                    string tempMode = cmbTemp.SelectedIndex == 1 ? "SYSTEM" : (cmbTemp.SelectedIndex == 2 ? "CUSTOM" : "VIDEO");
                    string tempCustom = txtTemp.Text.Trim().TrimEnd('\\');
                    string outputMode = cmbOutput.SelectedIndex == 1 ? "CUSTOM" : "VIDEO";
                    string outputCustom = txtOutput.Text.Trim().TrimEnd('\\');
                    if (tempMode == "CUSTOM")
                    {
                        if (tempCustom.Length == 0) { MessageBox.Show(dlg, LF("config.custom_empty", lang.T("config.temp_dir")), lang.T("config.path_title"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                        string err; if (!TestWritableDirectory(tempCustom, out err)) { MessageBox.Show(dlg, LF("error.temp_unavailable", tempCustom, err), lang.T("config.path_title"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    }
                    if (outputMode == "CUSTOM")
                    {
                        if (outputCustom.Length == 0) { MessageBox.Show(dlg, LF("config.custom_empty", lang.T("config.output_dir")), lang.T("config.path_title"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                        string err; if (!TestWritableDirectory(outputCustom, out err)) { MessageBox.Show(dlg, LF("error.output_unavailable", outputCustom, err), lang.T("config.path_title"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    }
                    try
                    {
                        Dictionary<string, string> updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        updates["FFMPEG_DIR"] = ffdir; updates["GRAV1SYNTH"] = txtGrav.Text.Trim(); updates["GRAIN_ROOT"] = txtGrain.Text.Trim(); updates["LUT_ROOT"] = txtLut.Text.Trim();
                        updates["FRUC_FFMPEG_SAME_AS_MAIN"] = chkFrucSame.Checked ? "true" : "false";
                        updates["FRUC_FFMPEG_PATH"] = chkFrucSame.Checked ?
                            (customFrucDir.Length > 0 ? Path.Combine(customFrucDir, "ffmpeg.exe") : savedFrucExe) : frucExe;
                        updates["TEMP_MODE"] = tempMode; updates["TEMP_CUSTOM_DIR"] = tempCustom; updates["OUTPUT_MODE"] = outputMode; updates["OUTPUT_CUSTOM_DIR"] = outputCustom;
                        updates["LOG_ENTRY_LIMIT"] = ((int)logEntryLimit.Value).ToString(CultureInfo.InvariantCulture);
                        config.Save(updates);
                        dlg.DialogResult = DialogResult.OK;
                        dlg.Close();
                    }
                    catch (Exception ex) { MessageBox.Show(dlg, LF("config.save_failed", ex.Message), lang.T("config.path_title"), MessageBoxButtons.OK, MessageBoxIcon.Error); }
                };

                DialogResult result = dlg.ShowDialog(this);
                configTip.Dispose();
                if (result == DialogResult.OK)
                {
                    config.Load();
                    if (!string.IsNullOrEmpty(selectedLutPath) && !File.Exists(selectedLutPath))
                    {
                        selectedLutPath = "";
                        selectedLutSource = "None";
                        chkLut.Checked = false;
                    }
                    RefreshLutLists();
                    UpdateLutUi();
                    RefreshGrainFileCaches();
                    UpdateGrainForCodec();
                    BeginHardwareDetection();
                    if (log != null) log.AppendText("[Config] Path configuration saved: " + config.ConfigPath + Environment.NewLine);
                    MessageBox.Show(this, lang.T("config.saved"), lang.T("config.path_title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void ShowAdvancedSettingsDialog()
        {
            config.Load();
            using (Form dlg = new Form())
            {
                dlg.Text = lang.T("advanced.title");
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.ClientSize = new Size(720, 460);
                dlg.Font = UiFont(9f, FontStyle.Regular);

                TabControl tabs = new TabControl(); tabs.Location = new Point(10, 10); tabs.Size = new Size(700, 382);
                TabPage enc = new TabPage(lang.T("advanced.tab.encode"));
                TabPage interp = new TabPage(lang.T("advanced.tab.interp"));
                TabPage hdr = new TabPage("HDR");
                TabPage other = new TabPage(lang.T("advanced.tab.other"));
                tabs.TabPages.Add(enc); tabs.TabPages.Add(interp); tabs.TabPages.Add(hdr); tabs.TabPages.Add(other); dlg.Controls.Add(tabs);

                CheckBox high10 = new CheckBox(); high10.Text = lang.T("advanced.high10"); high10.Checked = config.GetBool("H264_HIGH10") && (!hardwareCapsReady || hardwareCaps == null || (hardwareCaps.X264Available && hardwareCaps.X264High10Available)); high10.Enabled = !hardwareCapsReady || hardwareCaps == null || (hardwareCaps.X264Available && hardwareCaps.X264High10Available); high10.Location = new Point(28, 18); high10.Size = new Size(620, 26); enc.Controls.Add(high10);
                Label rateLabel = new Label(); rateLabel.Text = lang.T("advanced.x264_rate"); rateLabel.Location = new Point(28, 58); rateLabel.Size = new Size(150, 24); enc.Controls.Add(rateLabel);
                ComboBox rate = new ComboBox(); rate.DropDownStyle = ComboBoxStyle.DropDownList; rate.Location = new Point(190, 54); rate.Size = new Size(260, 26);
                rate.Items.Add(lang.T("advanced.vbr1")); rate.Items.Add(lang.T("advanced.vbr2")); rate.Items.Add(lang.T("advanced.vbr3"));
                rate.SelectedIndex = string.Equals(config.Get("X264_RATE_MODE"), "3PASS", StringComparison.OrdinalIgnoreCase) ? 2 : (string.Equals(config.Get("X264_RATE_MODE"), "2PASS", StringComparison.OrdinalIgnoreCase) ? 1 : 0); enc.Controls.Add(rate);
                Label presetLabel = new Label(); presetLabel.Text = "x264 Preset"; presetLabel.Location = new Point(28, 98); presetLabel.Size = new Size(150, 24); enc.Controls.Add(presetLabel);
                ComboBox preset = new ComboBox(); preset.DropDownStyle = ComboBoxStyle.DropDownList; preset.Location = new Point(190, 94); preset.Size = new Size(260, 26);
                preset.Items.Add(lang.T("advanced.preset_faster")); preset.Items.Add("Medium"); preset.Items.Add("Slow");
                preset.SelectedIndex = string.Equals(config.Get("X264_PRESET"), "medium", StringComparison.OrdinalIgnoreCase) ? 1 : (string.Equals(config.Get("X264_PRESET"), "slow", StringComparison.OrdinalIgnoreCase) ? 2 : 0); enc.Controls.Add(preset);
                Label aqLabel = new Label(); aqLabel.Text = lang.T("advanced.hevc_spatial_aq"); aqLabel.Location = new Point(28, 138); aqLabel.Size = new Size(150, 24); enc.Controls.Add(aqLabel);
                ComboBox aq = new ComboBox(); aq.DropDownStyle = ComboBoxStyle.DropDownList; aq.Location = new Point(190, 134); aq.Size = new Size(260, 26);
                aq.Items.Add(lang.T("advanced.hevc_spatial_off")); aq.Items.Add(lang.T("advanced.hevc_spatial_4")); aq.Items.Add(lang.T("advanced.hevc_spatial_8")); aq.Items.Add(lang.T("advanced.hevc_spatial_10")); aq.Items.Add(lang.T("advanced.hevc_spatial_12")); aq.Items.Add(lang.T("advanced.hevc_spatial_15"));
                int aqValue = config.GetInt("NVENC_SPATIAL_AQ", 8); aq.SelectedIndex = aqValue == 0 ? 0 : (aqValue == 4 ? 1 : (aqValue == 10 ? 3 : (aqValue == 12 ? 4 : (aqValue == 15 ? 5 : 2)))); aq.Enabled = !hardwareCapsReady || hardwareCaps == null || hardwareCaps.Av1Available || hardwareCaps.HevcAvailable; enc.Controls.Add(aq);
                CheckBox temporal = new CheckBox(); temporal.Text = lang.T("advanced.hevc_temporal_aq"); temporal.Checked = config.GetBool("NVENC_TEMPORAL_AQ"); temporal.Enabled = !hardwareCapsReady || hardwareCaps == null || hardwareCaps.Av1Available || hardwareCaps.HevcAvailable; temporal.Location = new Point(190, 170); temporal.Size = new Size(260, 26); enc.Controls.Add(temporal);
                Label encInfo = new Label(); encInfo.Location = new Point(28, 206); encInfo.Size = new Size(630, 48); encInfo.ForeColor = ColorMuted; encInfo.Text = lang.T("advanced.encode_info"); enc.Controls.Add(encInfo);
                Button encDefaults = new Button(); encDefaults.Text = lang.T("config.defaults"); encDefaults.Location = new Point(190, 262); encDefaults.Size = new Size(112, 30); enc.Controls.Add(encDefaults);

                Label algoLabel = new Label(); algoLabel.Text = "SmoothFps Algo"; algoLabel.Location = new Point(28, 34); algoLabel.Size = new Size(150, 24); interp.Controls.Add(algoLabel);
                ComboBox algo = new ComboBox(); algo.DropDownStyle = ComboBoxStyle.DropDownList; algo.Location = new Point(190, 30); algo.Size = new Size(220, 26);
                foreach (string item in new string[] { "1", "2", "11", "13", "21", "22", "23" }) algo.Items.Add(item);
                int ai = algo.Items.IndexOf(config.Get("SVP_ALGO")); algo.SelectedIndex = ai >= 0 ? ai : 3; interp.Controls.Add(algo);
                Label analyseLabel = new Label(); analyseLabel.Text = "Analyse Profile"; analyseLabel.Location = new Point(28, 82); analyseLabel.Size = new Size(150, 24); interp.Controls.Add(analyseLabel);
                ComboBox analyse = new ComboBox(); analyse.DropDownStyle = ComboBoxStyle.DropDownList; analyse.Location = new Point(190, 78); analyse.Size = new Size(360, 26);
                analyse.Items.Add(lang.T("advanced.analyse_recommended")); analyse.Items.Add(lang.T("advanced.analyse_baseline")); analyse.SelectedIndex = string.Equals(config.Get("SVP_ANALYSE"), "BASE", StringComparison.OrdinalIgnoreCase) ? 1 : 0; interp.Controls.Add(analyse);
                Label maskLabel = new Label(); maskLabel.Text = "Artifact Mask Area"; maskLabel.Location = new Point(28, 130); maskLabel.Size = new Size(150, 24); interp.Controls.Add(maskLabel);
                NumericUpDown mask = new NumericUpDown(); mask.Location = new Point(190, 126); mask.Size = new Size(120, 26); mask.Minimum = 0; mask.Maximum = 100; mask.Increment = 5; mask.Value = Math.Max(mask.Minimum, Math.Min(mask.Maximum, config.GetInt("SVP_MASK_AREA", 100))); interp.Controls.Add(mask);
                Label interpInfo = new Label(); interpInfo.Location = new Point(28, 238); interpInfo.Size = new Size(610, 44); interpInfo.ForeColor = ColorMuted; interpInfo.Text = lang.T("advanced.interp_info"); interp.Controls.Add(interpInfo);
                Label frucPerfLabel = new Label(); frucPerfLabel.Text = UiText("FRUC Vulkan 性能档位", "FRUC Vulkan perf"); frucPerfLabel.Location = new Point(28, 166); frucPerfLabel.Size = new Size(150, 24); interp.Controls.Add(frucPerfLabel);
                ComboBox frucPerf = new ComboBox(); frucPerf.DropDownStyle = ComboBoxStyle.DropDownList; frucPerf.Location = new Point(190, 162); frucPerf.Size = new Size(160, 26);
                frucPerf.Items.Add("slow"); frucPerf.Items.Add("medium"); frucPerf.Items.Add("fast"); string savedPerf = config.Get("FRUC_PERF").ToLowerInvariant(); frucPerf.SelectedIndex = savedPerf == "slow" ? 0 : (savedPerf == "fast" ? 2 : 1); interp.Controls.Add(frucPerf);
                Label frucGridLabel = new Label(); frucGridLabel.Text = UiText("FRUC Vulkan 光流网格", "FRUC Vulkan grid"); frucGridLabel.Location = new Point(28, 202); frucGridLabel.Size = new Size(150, 24); interp.Controls.Add(frucGridLabel);
                ComboBox frucGrid = new ComboBox(); frucGrid.DropDownStyle = ComboBoxStyle.DropDownList; frucGrid.Location = new Point(190, 198); frucGrid.Size = new Size(160, 26);
                frucGrid.Items.Add("auto"); frucGrid.Items.Add("1"); frucGrid.Items.Add("2"); frucGrid.Items.Add("4"); frucGrid.Items.Add("8"); int gridIndex = frucGrid.Items.IndexOf(config.Get("FRUC_GRID").ToLowerInvariant()); frucGrid.SelectedIndex = gridIndex >= 0 ? gridIndex : 0; interp.Controls.Add(frucGrid);
                Label frucInfo = new Label(); frucInfo.Location = new Point(370, 162); frucInfo.Size = new Size(280, 56); frucInfo.ForeColor = ColorMuted; frucInfo.Text = UiText("perf 控制质量/速度；grid 控制光流网格，auto 交由滤镜选择。", "perf controls quality/speed; grid selects the optical-flow grid."); interp.Controls.Add(frucInfo);
                Button interpDefaults = new Button(); interpDefaults.Text = lang.T("config.defaults"); interpDefaults.Location = new Point(190, 292); interpDefaults.Size = new Size(112, 30); interp.Controls.Add(interpDefaults);

                Label hdrLabel = new Label(); hdrLabel.Text = lang.T("advanced.hdr_policy"); hdrLabel.Location = new Point(28, 34); hdrLabel.Size = new Size(150, 24); hdr.Controls.Add(hdrLabel);
                ComboBox hdrPolicy = new ComboBox(); hdrPolicy.DropDownStyle = ComboBoxStyle.DropDownList; hdrPolicy.Location = new Point(190, 30); hdrPolicy.Size = new Size(330, 26);
                hdrPolicy.Items.Add(lang.T("advanced.hdr_auto")); hdrPolicy.Items.Add(lang.T("advanced.hdr_preserve")); hdrPolicy.Items.Add(lang.T("advanced.hdr_force_sdr")); hdrPolicy.SelectedIndex = string.Equals(config.Get("HDR_POLICY"), "PRESERVE", StringComparison.OrdinalIgnoreCase) ? 1 : (string.Equals(config.Get("HDR_POLICY"), "SDR", StringComparison.OrdinalIgnoreCase) ? 2 : 0); hdr.Controls.Add(hdrPolicy);
                Label toneLabel = new Label(); toneLabel.Text = "Tone Mapping"; toneLabel.Location = new Point(28, 82); toneLabel.Size = new Size(150, 24); hdr.Controls.Add(toneLabel);
                ComboBox tone = new ComboBox(); tone.DropDownStyle = ComboBoxStyle.DropDownList; tone.Location = new Point(190, 78); tone.Size = new Size(220, 26);
                tone.Items.Add(lang.T("advanced.hable")); tone.Items.Add("Mobius"); tone.Items.Add("Reinhard"); tone.Items.Add("Gamma"); tone.Items.Add("Linear"); tone.Items.Add("Clip");
                string ta = config.Get("TONE_MAP_ALGO").ToLowerInvariant(); tone.SelectedIndex = ta == "mobius" ? 1 : (ta == "reinhard" ? 2 : (ta == "gamma" ? 3 : (ta == "linear" ? 4 : (ta == "clip" ? 5 : 0)))); hdr.Controls.Add(tone);
                Label hdrInfo = new Label(); hdrInfo.Location = new Point(28, 132); hdrInfo.Size = new Size(620, 150); hdrInfo.ForeColor = ColorMuted; hdrInfo.Text = lang.T("advanced.hdr_info"); hdr.Controls.Add(hdrInfo);
                Button hdrDefaults = new Button(); hdrDefaults.Text = lang.T("config.defaults"); hdrDefaults.Location = new Point(190, 292); hdrDefaults.Size = new Size(112, 30); hdr.Controls.Add(hdrDefaults);
                EventHandler updateHdr = delegate { bool enabled = hdrPolicy.SelectedIndex != 1; tone.Enabled = enabled; toneLabel.Enabled = enabled; };
                hdrPolicy.SelectedIndexChanged += updateHdr; updateHdr(null, EventArgs.Empty);

                Label cropTitle = new Label(); cropTitle.Text = lang.T("advanced.crop_title"); cropTitle.Location = new Point(28, 30); cropTitle.Size = new Size(260, 26); other.Controls.Add(cropTitle);
                Label cropLabel = new Label(); cropLabel.Text = lang.T("advanced.crop_value"); cropLabel.Location = new Point(28, 82); cropLabel.Size = new Size(150, 24); other.Controls.Add(cropLabel);
                NumericUpDown crop = new NumericUpDown(); crop.Location = new Point(190, 78); crop.Size = new Size(120, 26); crop.Minimum = 0; crop.Maximum = 2000; crop.Increment = 1; crop.Value = Math.Max(crop.Minimum, Math.Min(crop.Maximum, config.GetInt("CINEMATIC_CROP_PER_SIDE", 0))); other.Controls.Add(crop);
                Label cropInfo = new Label(); cropInfo.Location = new Point(28, 128); cropInfo.Size = new Size(620, 118); cropInfo.ForeColor = ColorMuted; cropInfo.Text = lang.T("advanced.crop_info"); other.Controls.Add(cropInfo);
                CheckBox largeColor = new CheckBox(); largeColor.Text = lang.T("advanced.color_preview_large"); largeColor.Checked = config.GetBool("COLOR_PREVIEW_LARGE_UI"); largeColor.Location = new Point(28, 258); largeColor.Size = new Size(500, 26); other.Controls.Add(largeColor);
                Button otherDefaults = new Button(); otherDefaults.Text = lang.T("config.defaults"); otherDefaults.Location = new Point(190, 292); otherDefaults.Size = new Size(112, 30); other.Controls.Add(otherDefaults);

                Button ok = new Button(); ok.Text = lang.T("advanced.ok"); ok.Location = new Point(514, 406); ok.Size = new Size(88, 30); dlg.Controls.Add(ok);
                Button cancel = new Button(); cancel.Text = lang.T("advanced.cancel"); cancel.Location = new Point(610, 406); cancel.Size = new Size(88, 30); cancel.DialogResult = DialogResult.Cancel; dlg.Controls.Add(cancel);
                dlg.CancelButton = cancel;

                encDefaults.Click += delegate { high10.Checked = false; rate.SelectedIndex = 0; preset.SelectedIndex = 0; aq.SelectedIndex = 2; temporal.Checked = false; };
                interpDefaults.Click += delegate { algo.SelectedItem = "13"; analyse.SelectedIndex = 0; mask.Value = 100; frucPerf.SelectedIndex = 1; frucGrid.SelectedIndex = 0; };
                hdrDefaults.Click += delegate { hdrPolicy.SelectedIndex = 0; tone.SelectedIndex = 0; updateHdr(null, EventArgs.Empty); };
                otherDefaults.Click += delegate { crop.Value = 0; largeColor.Checked = false; };

                ok.Click += delegate
                {
                    Dictionary<string, string> updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    updates["H264_HIGH10"] = high10.Checked && (!hardwareCapsReady || hardwareCaps == null || (hardwareCaps.X264Available && hardwareCaps.X264High10Available)) ? "true" : "false";
                    updates["X264_RATE_MODE"] = rate.SelectedIndex == 2 ? "3PASS" : (rate.SelectedIndex == 1 ? "2PASS" : "VBR1");
                    updates["X264_PRESET"] = preset.SelectedIndex == 1 ? "medium" : (preset.SelectedIndex == 2 ? "slow" : "faster");
                    int[] aqValues = new int[] { 0, 4, 8, 10, 12, 15 }; updates["NVENC_SPATIAL_AQ"] = aqValues[Math.Max(0, Math.Min(aqValues.Length - 1, aq.SelectedIndex))].ToString(CultureInfo.InvariantCulture);
                    updates["NVENC_TEMPORAL_AQ"] = temporal.Checked ? "true" : "false";
                    updates["SVP_ALGO"] = Convert.ToString(algo.SelectedItem, CultureInfo.InvariantCulture);
                    updates["SVP_ANALYSE"] = analyse.SelectedIndex == 1 ? "BASE" : "ENCODEGUI";
                    updates["SVP_MASK_AREA"] = ((int)mask.Value).ToString(CultureInfo.InvariantCulture);
                    updates["FRUC_PERF"] = Convert.ToString(frucPerf.SelectedItem, CultureInfo.InvariantCulture);
                    updates["FRUC_GRID"] = Convert.ToString(frucGrid.SelectedItem, CultureInfo.InvariantCulture);
                    updates["HDR_POLICY"] = hdrPolicy.SelectedIndex == 1 ? "PRESERVE" : (hdrPolicy.SelectedIndex == 2 ? "SDR" : "AUTO");
                    string[] toneValues = new string[] { "hable", "mobius", "reinhard", "gamma", "linear", "clip" }; updates["TONE_MAP_ALGO"] = toneValues[Math.Max(0, Math.Min(toneValues.Length - 1, tone.SelectedIndex))];
                    updates["CINEMATIC_CROP_PER_SIDE"] = ((int)crop.Value).ToString(CultureInfo.InvariantCulture);
                    updates["COLOR_PREVIEW_LARGE_UI"] = largeColor.Checked ? "true" : "false";
                    try
                    {
                        config.Save(updates);
                        dlg.DialogResult = DialogResult.OK;
                        dlg.Close();
                    }
                    catch (Exception ex) { MessageBox.Show(dlg, ex.Message, "Film Grain Studio", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                };

                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    config.Load();
                    UpdateSpeedChoices();
                    UpdateHdrRouteStatus(GetSelectedMediaInfo());
                    if (log != null) log.AppendText("[Config] Advanced settings saved." + Environment.NewLine);
                }
            }
        }

    }
}
