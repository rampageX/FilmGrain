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
    internal static class NvencAqUiPolicy
    {
        internal static bool IsEnabled(bool ready, HardwareCapabilitySnapshot caps, bool temporal)
        {
            if (!ready || caps == null) return true;
            return (caps.Av1Available && (temporal ? caps.Av1TemporalAq : caps.Av1SpatialAq)) ||
                (caps.HevcAvailable && (temporal ? caps.HevcTemporalAq : caps.HevcSpatialAq)) ||
                (caps.H264Available && (temporal ? caps.H264TemporalAq : caps.H264SpatialAq));
        }
        internal static bool IsEnabled(bool ready, HardwareCapabilitySnapshot caps, bool temporal, bool h264)
        {
            if (!ready || caps == null) return true;
            return h264 ? caps.H264Available && (temporal ? caps.H264TemporalAq : caps.H264SpatialAq) :
                caps.HevcAvailable && (temporal ? caps.HevcTemporalAq : caps.HevcSpatialAq);
        }
    }

    internal sealed partial class MainForm
    {
        

        

        private void ShowAv1BakeDialog()
        {
            if(bridgeTaskCoordinator==null||bridgeTaskCoordinator.IsActive)return;
            var inputs=new List<string>();
            foreach(ListViewItem item in listFiles.Items){string path=item.Tag as string;if(!string.IsNullOrWhiteSpace(path))inputs.Add(path);}
            if(inputs.Count==0){MessageBox.Show(this,lang.T("info.add_video"),Text);return;}
            foreach(string input in inputs){MediaProbeInfo info;if(!mediaProbeCache.TryGetValue(input,out info)){MessageBox.Show(this,UiText("请等待输入媒体探测完成。", "Wait for input media probing."),Text);return;}}
            using(var dlg=new Form())
            {
                dlg.Text=UiText("AV1 颗粒烘焙 · SDR", "AV1 grain bake · SDR");dlg.StartPosition=FormStartPosition.CenterParent;
                dlg.FormBorderStyle=FormBorderStyle.FixedDialog;dlg.MaximizeBox=false;dlg.MinimizeBox=false;dlg.ClientSize=new Size(470,240);
                var description=new ThemeLabel{Text=UiText("将输入列表中的已有 AV1 转为 H.264 或 HEVC / AAC。\n合成源内已有颗粒，不再添加颗粒；不应用主界面的插帧、调色或字幕设置。HDR 本阶段不支持。", "Convert the input AV1 list to H.264 or HEVC / AAC.\nSynthesize existing grain only. Main-window transformations do not apply. HDR is deferred."),Location=new Point(16,14),Size=new Size(438,66)};
                var encoder=new ThemeComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Location=new Point(126,88),Size=new Size(312,26)};
                encoder.Items.AddRange(new object[]{"x264 · VBR 1-Pass","H.264 NVENC · P7 / VBR","HEVC NVENC · Main10 / P7 / VBR"});encoder.SelectedIndex=0;
                var auto=new ThemeCheckBox{Text=UiText("自动码率（沿用所选编码线推荐）", "Automatic bitrate (selected FGS encoder recommendation)"),Checked=true,Location=new Point(16,123),Size=new Size(300,25)};
                var bitrate=new ThemeNumericUpDown{Minimum=100,Maximum=500000,Value=16000,Increment=1000,Location=new Point(326,123),Size=new Size(112,26),Enabled=false};auto.CheckedChanged+=delegate{bitrate.Enabled=!auto.Checked;};
                var start=new ThemeButton{Text=UiText("开始烘焙", "Start baking"),Location=new Point(230,191),Size=new Size(106,30),DialogResult=DialogResult.OK};
                var cancel=new ThemeButton{Text=lang.T("config.cancel"),Location=new Point(342,191),Size=new Size(96,30),DialogResult=DialogResult.Cancel};
                dlg.Controls.AddRange(new Control[]{description,new ThemeLabel{Text=UiText("视频编码器", "Video encoder"),Location=new Point(16,92),Size=new Size(105,23)},encoder,auto,bitrate,start,cancel});dlg.AcceptButton=start;dlg.CancelButton=cancel;
                AttachThemeWindow(dlg);
                if(dlg.ShowDialog(this)!=DialogResult.OK)return;
                var tasks=new List<BridgeTaskRequest>();var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach(string input in inputs)
                    {
                        var info=mediaProbeCache[input];double fps=BitrateRecommendationCore.ParseMediaFps(info);
                        long rate=auto.Checked?BitrateRecommendationCore.GetRecommendedBitrate(encoder.SelectedIndex==2?1:2,info.Width,info.Height,fps,false):(long)bitrate.Value;
                        var state=new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase){
                            {"FG_MODE","AV1BAKE"},{"FG_GRAIN_ENABLED","1"},{"FG_BAKE_ENCODER",encoder.SelectedIndex==2?"HEVC_NVENC":encoder.SelectedIndex==1?"NVENC":"X264"},
                            {"FG_BAKE_BITRATE",rate.ToString(CultureInfo.InvariantCulture)},
                            {"FG_AUDIO_MODE",config.Get("AUDIO_MODE")},{"FG_AUDIO_BITRATE",config.Get("AUDIO_BITRATE")},
                            {"FG_OUTPUT_MODE",config.Get("OUTPUT_MODE")},{"FG_OUTPUT_CUSTOM_DIR",config.Get("OUTPUT_CUSTOM_DIR")}};
                        string output=Path.GetFullPath(NativeAv1BakeCore.OutputPath(input,state,rate,encoder.SelectedIndex==1));
                        if(!paths.Add(output))throw new IOException("Bake queue output collision: "+output);
                        BridgePreparedExecution prepared;string reason;
                        if(!NativeExecutionPreparationCore.TryPrepare(appRoot,input,info,state,hardwareCaps,false,out prepared,out reason))throw new InvalidOperationException(reason);
                        tasks.Add(BridgeRequestPreparationCore.CreateTask(prepared,fps));
                    }
                    if(taskLog!=null)taskLog.Dispose();taskLog=new FullLogFileCore(appRoot,"Task",GetLogEntryLimit());fullLogErrorReported=false;
                    AppendTaskLog("[Task log] "+taskLog.FilePath+Environment.NewLine+"[AV1 Bake] build="+TestBuild+Environment.NewLine);
                    foreach(string input in inputs)AppendTaskLog("[AV1 Bake input] "+input+Environment.NewLine);
                    activeRunNativeBackend=true;activeRunCodecName="AV1 Bake";
                    bridgeTaskCoordinator.StartQueue(tasks);
                }
                catch(Exception ex){foreach(var task in tasks)BridgeExecutionCore.CleanupTemporaryFiles(task.ExecutionRequest);MessageBox.Show(this,ex.Message,dlg.Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            }
        }

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
                Label modeLabel = new ThemeLabel { Text = UiText("代理方式", "Proxy mode"), Left = 22, Top = 22, Width = 110, Height = 26, TextAlign = ContentAlignment.MiddleLeft };
                ComboBox mode = new ThemeComboBox { Left = 140, Top = 20, Width = 370, DropDownStyle = ComboBoxStyle.DropDownList };
                mode.Items.Add(new ProxyModeItem("SYSTEM", UiText("系统代理 / 环境变量", "System proxy / environment")));
                mode.Items.Add(new ProxyModeItem("CUSTOM", UiText("自定义 HTTP(S) 代理", "Custom HTTP(S) proxy")));
                mode.Items.Add(new ProxyModeItem("DIRECT", UiText("直连", "Direct connection")));
                string savedMode = config.Get("NETWORK_PROXY_MODE").ToUpperInvariant();
                int selected = savedMode == "CUSTOM" ? 1 : (savedMode == "DIRECT" ? 2 : 0); mode.SelectedIndex = selected;
                Label urlLabel = new ThemeLabel { Text = UiText("代理地址", "Proxy URL"), Left = 22, Top = 66, Width = 110, Height = 26, TextAlign = ContentAlignment.MiddleLeft };
                TextBox url = new ThemeTextBox { Left = 140, Top = 64, Width = 370, Text = config.Get("NETWORK_PROXY_URL") };
                Label help = new ThemeLabel { Left = 140, Top = 96, Width = 370, Height = 48, ForeColor = ColorMuted, Text = UiText("例如 http://127.0.0.1:7890。设置会用于 FGS 下载、安装向导及其子进程。", "Example: http://127.0.0.1:7890. Applies to FGS downloads, setup, and child processes.") };
                Label status = new ThemeLabel { Left = 140, Top = 144, Width = 370, Height = 24, ForeColor = ColorMuted, Text = "" };
                Button test = new ThemeButton { Left = 140, Top = 179, Width = 112, Height = 30, Text = UiText("测试代理", "Test proxy") };
                Button save = new ThemeButton { Left = 310, Top = 179, Width = 92, Height = 30, Text = UiText("保存", "Save"), DialogResult = DialogResult.None };
                Button cancel = new ThemeButton { Left = 410, Top = 179, Width = 100, Height = 30, Text = UiText("取消", "Cancel"), DialogResult = DialogResult.Cancel };
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
                AttachThemeWindow(dlg);
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
            Label l = new ThemeLabel();
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
            TextBox t = new ThemeTextBox();
            t.Text = value;
            t.Dock = DockStyle.Fill;
            t.Margin = new Padding(4, 8, 4, 6);
            return t;
        }

        private Button ConfigButton(string text)
        {
            Button b = new ThemeButton();
            b.Text = text;
            b.Dock = DockStyle.Fill;
            b.Margin = new Padding(4, 5, 4, 5);
            return b;
        }

        private Button RefreshButton()
        {
            Button b = new ThemeButton();
            b.Text = "↻";
            b.Dock = DockStyle.Fill;
            b.Margin = new Padding(2, 5, 2, 5);
            return b;
        }

        private Label ConfigStatusLabel(string text)
        {
            Label l = new ThemeLabel();
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
            string hardwareBefore = HardwareCapabilityCore.RequestKey(CreateHardwareRequest());
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
                CheckBox chkFrucSame = new ThemeCheckBox(); chkFrucSame.Text = UiText("与正式版使用相同 FFmpeg", "Use main FFmpeg");
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
                ToolTip configTip = new ThemeToolTip();
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
                ComboBox cmbTemp = new ThemeComboBox(); cmbTemp.DropDownStyle = ComboBoxStyle.DropDownList; cmbTemp.Dock = DockStyle.Fill; cmbTemp.Margin = new Padding(4, 8, 4, 6);
                cmbTemp.Items.Add(lang.T("config.temp_video")); cmbTemp.Items.Add(lang.T("config.temp_system")); cmbTemp.Items.Add(lang.T("config.temp_custom"));
                cmbTemp.SelectedIndex = string.Equals(config.Get("TEMP_MODE"), "SYSTEM", StringComparison.OrdinalIgnoreCase) ? 1 : (string.Equals(config.Get("TEMP_MODE"), "CUSTOM", StringComparison.OrdinalIgnoreCase) ? 2 : 0);
                TextBox txtTemp = ConfigTextBox(config.Get("TEMP_CUSTOM_DIR"));
                tempPanel.Controls.Add(cmbTemp, 0, 0); tempPanel.Controls.Add(txtTemp, 1, 0);
                Button btnTemp = ConfigButton(lang.T("config.browse"));
                table.Controls.Add(ConfigLabel(lang.T("config.temp_dir")), 0, 10); table.Controls.Add(tempPanel, 1, 10); table.Controls.Add(btnTemp, 2, 10);

                TableLayoutPanel outputPanel = new TableLayoutPanel();
                outputPanel.Dock = DockStyle.Fill; outputPanel.ColumnCount = 2; outputPanel.Margin = Padding.Empty;
                outputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); outputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                ComboBox cmbOutput = new ThemeComboBox(); cmbOutput.DropDownStyle = ComboBoxStyle.DropDownList; cmbOutput.Dock = DockStyle.Fill; cmbOutput.Margin = new Padding(4, 8, 4, 6);
                cmbOutput.Items.Add(lang.T("config.output_video")); cmbOutput.Items.Add(lang.T("config.output_custom"));
                cmbOutput.SelectedIndex = string.Equals(config.Get("OUTPUT_MODE"), "CUSTOM", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                TextBox txtOutput = ConfigTextBox(config.Get("OUTPUT_CUSTOM_DIR"));
                outputPanel.Controls.Add(cmbOutput, 0, 0); outputPanel.Controls.Add(txtOutput, 1, 0);
                Button btnOutput = ConfigButton(lang.T("config.browse"));
                table.Controls.Add(ConfigLabel(lang.T("config.output_dir")), 0, 11); table.Controls.Add(outputPanel, 1, 11); table.Controls.Add(btnOutput, 2, 11);

                NumericUpDown logEntryLimit = new ThemeNumericUpDown(); logEntryLimit.Minimum = 0; logEntryLimit.Maximum = 10000; logEntryLimit.Increment = 10; logEntryLimit.Dock = DockStyle.Left; logEntryLimit.Width = 120; logEntryLimit.Margin = new Padding(4, 8, 4, 6);
                logEntryLimit.Value = Math.Max(logEntryLimit.Minimum, Math.Min(logEntryLimit.Maximum, config.GetInt("LOG_ENTRY_LIMIT", 100)));
                Label logLimitInfo = new ThemeLabel(); logLimitInfo.Text = UiText("0 = 不限制", "0 = unlimited"); logLimitInfo.Dock = DockStyle.Fill; logLimitInfo.TextAlign = ContentAlignment.MiddleLeft; logLimitInfo.ForeColor = ColorMuted;
                table.Controls.Add(ConfigLabel(UiText("日志条目限制", "Log entry limit")), 0, 12); table.Controls.Add(logEntryLimit, 1, 12); table.Controls.Add(logLimitInfo, 2, 12); table.SetColumnSpan(logLimitInfo, 2);

                Label note = new ThemeLabel();
                note.Dock = DockStyle.Fill; note.ForeColor = ColorMuted; note.TextAlign = ContentAlignment.TopLeft; note.Padding = new Padding(4, 8, 4, 0);
                note.Text = LF("config.note", config.ConfigPath);
                table.Controls.Add(note, 0, 13); table.SetColumnSpan(note, 4);

                FlowLayoutPanel buttons = new FlowLayoutPanel();
                buttons.Dock = DockStyle.Fill; buttons.FlowDirection = FlowDirection.RightToLeft; buttons.WrapContents = false;
                Button save = new ThemeButton(); save.Text = lang.T("config.save"); save.Width = 82;
                Button cancel = new ThemeButton(); cancel.Text = lang.T("config.cancel"); cancel.Width = 82; cancel.DialogResult = DialogResult.Cancel;
                Button restore = new ThemeButton(); restore.Text = lang.T("config.defaults"); restore.Width = 104;
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

                AttachThemeWindow(dlg);
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
                    InvalidateChangedCatalogPaths();
                    UpdateLutUi();
                    UpdateGrainForCodec();
                    RefreshChangedHardware(hardwareBefore);
                    if (log != null) log.AppendText("[Config] Path configuration saved: " + config.ConfigPath + Environment.NewLine);
                    MessageBox.Show(this, lang.T("config.saved"), lang.T("config.path_title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void StartNativeSvpUpdate(Form owner)
        {
            if (setupTestMode) { MessageBox.Show(owner, "设置测试模式禁止真实更新。"); return; }
            if (bridgeTaskCoordinator != null && bridgeTaskCoordinator.IsActive) { MessageBox.Show(owner, "请等待编码队列结束后更新插件。"); return; }
            string mode = config.Get("NETWORK_PROXY_MODE"), proxy = config.Get("NETWORK_PROXY_URL");
            LibraryToolResult result = NativeLibraryToolDialog.Show(owner, "OpenSVPFlow 原生更新", delegate(CancellationToken token, Action<string> log) {
                var core = new NativeSetupCore(appRoot, false, token, log,
                    delegate(string url, string path, long limit, CancellationToken ct, Action<string> write) { NativeSetupCore.Download(url, path, limit, mode, proxy, ct, write); },
                    delegate(string exe, IList<string> args, string cwd, IDictionary<string,string> env, CancellationToken ct, Action<string> write) { return NativeLibraryTools.RunProcessEnvironment(exe,args,cwd,env,120000,ct,write); });
                return core.Install("OpenSVPFlowUpdate");
            });
            if (result.Success > 0 && !result.Cancelled && result.Failed == 0) {
                BeginHardwareDetection();
                MessageBox.Show(owner, "插件更新并完成自检，相关硬件能力已安排刷新。", "更新完成");
            }
        }

        private string NvencRecommendationTip(string key)
        {
            if (key.EndsWith("MULTIPASS", StringComparison.Ordinal))
                return UiText("FGS 推荐：快速用 QRes（四分之一分辨率两遍）；标准 / AV1 UHQ / 高动态优先 FullRes（完整分辨率两遍）。不支持 FullRes 时用 QRes；对应档位不可用时不强制 multipass。手动值仅覆盖所选编码线主输出；不支持则停止。这是逐帧分析，不是整片编码两遍。", "FGS recommended: Fast uses QRes (quarter-resolution 2-pass); Standard / AV1 UHQ / high motion prefer FullRes (full-resolution 2-pass), then QRes if unavailable. If the selected recommendation is unsupported, no multipass override is added. Manual overrides affect this main encoder only and stop if unsupported. This is per-frame analysis, not two whole-file encodes.");
            if (key.EndsWith("BFRAMES", StringComparison.Ordinal))
                return UiText("FGS 推荐：4 个 B 帧；编码器不支持时不强制设置 B 帧。手动设为 0 或 1 时不启用 B 参考帧；不支持的手动值会停止任务。", "FGS recommended: 4 B frames; no B-frame override if unsupported. Manual 0 or 1 disables B references; unsupported manual values stop the task.");
            if (key.EndsWith("BREF_MODE", StringComparison.Ordinal))
                return UiText("FGS 推荐：middle（中间 B 帧作参考），需要该编码器同时支持 B 帧和 B 参考。B 帧少于 2 时不启用 B 参考；不支持的手动值会停止任务。", "FGS recommended: middle (middle B frame as reference), when both B frames and B references are supported. Fewer than 2 B frames disable B references; unsupported manual values stop the task.");
            return UiText("FGS 推荐 Lookahead：快速 16 帧、标准 27 帧、高动态 32 帧（高动态优先）。编码器不支持时不强制设置。手动 0 表示关闭；数值是请求深度，不代表硬件已完整生效。NVIDIA SDK 13.1 普通 Lookahead 文档限制为 31 减 B 帧数（4 个 B 帧时为 27）；FFmpeg 还可能按缓冲/延迟裁剪。32 必须结合具体编码器、B 帧与实际日志验证，不是已确认的 4080 上限。x264 不使用这些 NVENC 设置。", "FGS recommended Lookahead: Fast 16 frames, Standard 27 frames, high motion 32 frames (takes priority). No override if unsupported. Manual 0 disables it. Values are requested depths, not guaranteed effective depths. NVIDIA SDK 13.1 documents ordinary lookahead as 31 minus B-frame count (27 with 4 B frames). FFmpeg may also clip for surfaces/delay. Validate 32 per encoder/B-frame combination and logs; it is not a verified RTX 4080 maximum. x264 does not use these NVENC controls.");
        }

        private void ShowAdvancedSettingsDialog()
        {
            ShowAdvancedSettingsDialog(cmbCodec.SelectedIndex==2||cmbCodec.SelectedIndex==3?1:0);
        }

        private void ShowAdvancedSettingsDialog(int initialTab)
        {
            config.Load();
            string hardwareBefore = HardwareCapabilityCore.RequestKey(CreateHardwareRequest());
            using (Form dlg = new Form())
            using (ToolTip interpTips = new ThemeToolTip())
            {
                dlg.Text = lang.T("advanced.title");
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.ClientSize = new Size(760, 520);
                dlg.Font = UiFont(9f, FontStyle.Regular);

                TabControl tabs = new ThemeTabControl(); tabs.Location = new Point(10, 10); tabs.Size = new Size(740, 442);
                TabPage enc = new TabPage("AV1 / HEVC");
                TabPage h264Page = new TabPage("H.264 / x264");
                TabPage interp = new TabPage(lang.T("advanced.tab.interp"));
                TabPage hdr = new TabPage("HDR");
                tabs.TabPages.Add(enc); tabs.TabPages.Add(h264Page); tabs.TabPages.Add(interp); tabs.TabPages.Add(hdr); dlg.Controls.Add(tabs);
                tabs.SelectedIndex = Math.Max(0, Math.Min(tabs.TabCount - 1, initialTab));

                CheckBox high10 = new ThemeCheckBox(); high10.Text = lang.T("advanced.high10"); high10.Checked = config.GetBool("H264_HIGH10") && (!hardwareCapsReady || hardwareCaps == null || (hardwareCaps.X264Available && hardwareCaps.X264High10Available)); high10.Enabled = !hardwareCapsReady || hardwareCaps == null || (hardwareCaps.X264Available && hardwareCaps.X264High10Available); high10.Location = new Point(28, 18); high10.Size = new Size(620, 26); enc.Controls.Add(high10);
                Label rateLabel = new ThemeLabel(); rateLabel.Text = lang.T("advanced.x264_rate"); rateLabel.Location = new Point(28, 58); rateLabel.Size = new Size(150, 24); enc.Controls.Add(rateLabel);
                ComboBox rate = new ThemeComboBox(); rate.DropDownStyle = ComboBoxStyle.DropDownList; rate.Location = new Point(190, 54); rate.Size = new Size(260, 26);
                rate.Items.Add(lang.T("advanced.vbr1")); rate.Items.Add(lang.T("advanced.vbr2")); rate.Items.Add(lang.T("advanced.vbr3"));
                rate.SelectedIndex = string.Equals(config.Get("X264_RATE_MODE"), "3PASS", StringComparison.OrdinalIgnoreCase) ? 2 : (string.Equals(config.Get("X264_RATE_MODE"), "2PASS", StringComparison.OrdinalIgnoreCase) ? 1 : 0); enc.Controls.Add(rate);
                Label presetLabel = new ThemeLabel(); presetLabel.Text = "x264 Preset"; presetLabel.Location = new Point(28, 98); presetLabel.Size = new Size(150, 24); enc.Controls.Add(presetLabel);
                ComboBox preset = new ThemeComboBox(); preset.DropDownStyle = ComboBoxStyle.DropDownList; preset.Location = new Point(190, 94); preset.Size = new Size(260, 26);
                preset.Items.Add(lang.T("advanced.preset_faster")); preset.Items.Add("Medium"); preset.Items.Add("Slow");
                preset.SelectedIndex = string.Equals(config.Get("X264_PRESET"), "medium", StringComparison.OrdinalIgnoreCase) ? 1 : (string.Equals(config.Get("X264_PRESET"), "slow", StringComparison.OrdinalIgnoreCase) ? 2 : 0); enc.Controls.Add(preset);
                Label aqLabel = new ThemeLabel(); aqLabel.Text = lang.T("advanced.hevc_spatial_aq"); aqLabel.Location = new Point(28, 138); aqLabel.Size = new Size(150, 24); enc.Controls.Add(aqLabel);
                ComboBox aq = new ThemeComboBox(); aq.DropDownStyle = ComboBoxStyle.DropDownList; aq.Location = new Point(190, 134); aq.Size = new Size(260, 26);
                aq.Items.Add(lang.T("advanced.hevc_spatial_off")); aq.Items.Add(lang.T("advanced.hevc_spatial_4")); aq.Items.Add(lang.T("advanced.hevc_spatial_8")); aq.Items.Add(lang.T("advanced.hevc_spatial_10")); aq.Items.Add(lang.T("advanced.hevc_spatial_12")); aq.Items.Add(lang.T("advanced.hevc_spatial_15"));
                int aqValue = config.GetInt("NVENC_SPATIAL_AQ", 8); aq.SelectedIndex = aqValue == 0 ? 0 : (aqValue == 4 ? 1 : (aqValue == 10 ? 3 : (aqValue == 12 ? 4 : (aqValue == 15 ? 5 : 2)))); aq.Enabled = NvencAqUiPolicy.IsEnabled(hardwareCapsReady, hardwareCaps, false);aq.Name="NVENC_SPATIAL_AQ"; enc.Controls.Add(aq);
                CheckBox temporal = new ThemeCheckBox(); temporal.Text = lang.T("advanced.hevc_temporal_aq"); temporal.Checked = config.GetBool("NVENC_TEMPORAL_AQ"); temporal.Enabled = NvencAqUiPolicy.IsEnabled(hardwareCapsReady, hardwareCaps, true);temporal.Name="NVENC_TEMPORAL_AQ"; temporal.Location = new Point(190, 170); temporal.Size = new Size(260, 26); enc.Controls.Add(temporal);
                var audioModes=new[]{"FGS","SMART","COPY","AAC"};
                var audioRates=new[]{"SOURCE","128","160","192","256","320"};
                var multipassValues=new[]{"FGS","DISABLED","QRES","FULLRES"};
                var audioMode=new ThemeComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Location=new Point(190,204),Size=new Size(440,26)};
                audioMode.Items.AddRange(new object[]{UiText("跟随容器（原有行为）","Follow container (existing behavior)"),UiText("智能保留：兼容音轨复制，否则 AAC","Smart: copy compatible tracks, otherwise AAC"),UiText("全部复制（不兼容则停止）","Copy all (stop if incompatible)"),UiText("全部转 AAC","Encode all to AAC")});
                audioMode.SelectedIndex=Math.Max(0,Array.IndexOf(audioModes,config.Get("AUDIO_MODE").ToUpperInvariant()));
                var audioRate=new ThemeComboBox{Name="AUDIO_BITRATE",DropDownStyle=ComboBoxStyle.DropDownList,Location=new Point(190,242),Size=new Size(260,26)};
                audioRate.Items.AddRange(new object[]{UiText("接近源音轨（估算/回退）","Near source (estimate/fallback)"),"128 kbps","160 kbps","192 kbps","256 kbps","320 kbps"});
                int rateIndex=Array.IndexOf(audioRates,config.Get("AUDIO_BITRATE").ToUpperInvariant());audioRate.SelectedIndex=rateIndex<0?4:rateIndex;
                audioMode.SelectedIndexChanged+=delegate{audioRate.Enabled=audioMode.SelectedIndex!=2;};audioRate.Enabled=audioMode.SelectedIndex!=2;
                var av1Multipass=new ThemeComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Name="AV1_MULTIPASS",Location=new Point(145,318),Size=new Size(180,26)};
                var hevcMultipass=new ThemeComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Name="HEVC_MULTIPASS",Location=new Point(450,318),Size=new Size(180,26)};
                object[] mpLabels={UiText("FGS 推荐","FGS recommended"),UiText("关闭","Disabled"),UiText("两遍 · 四分之一分辨率","2-pass: Quarter res"),UiText("两遍 · 完整分辨率","2-pass: Full res")};
                av1Multipass.Items.AddRange(mpLabels);hevcMultipass.Items.AddRange(mpLabels);
                av1Multipass.SelectedIndex=Math.Max(0,Array.IndexOf(multipassValues,config.Get("AV1_MULTIPASS").ToUpperInvariant()));hevcMultipass.SelectedIndex=Math.Max(0,Array.IndexOf(multipassValues,config.Get("HEVC_MULTIPASS").ToUpperInvariant()));
                enc.Controls.AddRange(new Control[]{new ThemeLabel{Text=UiText("音频处理","Audio policy"),Location=new Point(28,208),Size=new Size(155,24)},audioMode,new ThemeLabel{Text=UiText("AAC 音频码率","AAC bitrate"),Location=new Point(28,246),Size=new Size(155,24)},audioRate,new ThemeLabel{Text="AV1 Multipass",Location=new Point(28,322),Size=new Size(115,24)},av1Multipass,new ThemeLabel{Text="HEVC Multipass",Location=new Point(335,322),Size=new Size(115,24)},hevcMultipass});
                var encodingTip=new ThemeToolTip();dlg.Disposed+=delegate{encodingTip.Dispose();};

                encodingTip.SetToolTip(audioMode,UiText("与 MP4/MKV 独立。智能模式下，MP4 复制 AAC/MP3/AC3/EAC3，其它转 AAC；MKV 保留。独立烘焙仍仅第一音轨，且需要时转换为双声道。","Independent of MP4/MKV. Smart MP4 copies AAC/MP3/AC3/EAC3; other codecs become AAC. Smart MKV copies. Standalone bake retains its first-track/stereo scope."));
                encodingTip.SetToolTip(audioRate,UiText("仅对转 AAC 的音轨生效，复制不升码率。接近源：先用音轨码率，缺失时最多读取128个音频包估算；仍未知按声道回退（单声道64、双声道128、多声道384k）。不代表跨编码器同等音质。","Only encoded AAC tracks use this rate. Near-source uses per-track metadata or up to128 audio packets, then channel fallback (mono64/stereo128/multichannel384k). Equal bitrate is not equal quality."));
                encodingTip.InitialDelay=400;encodingTip.ReshowDelay=100;encodingTip.AutoPopDelay=25000;encodingTip.ShowAlways=true;
                foreach(ComboBox box in new ComboBox[]{av1Multipass,hevcMultipass}) {
                    string tip=NvencRecommendationTip(box.Name);box.AccessibleDescription=tip;encodingTip.SetToolTip(box,tip);
                }
                foreach(Control label in enc.Controls)
                    if(label is Label && (label.Text=="AV1 Multipass" || label.Text=="HEVC Multipass"))
                        encodingTip.SetToolTip(label,NvencRecommendationTip(label.Text.StartsWith("AV1",StringComparison.Ordinal)?"AV1_MULTIPASS":"HEVC_MULTIPASS"));
                enc.AutoScroll=false;
                var codecOptions=new Dictionary<string,ComboBox>();
                string[] optionKeys={"H264_MULTIPASS","HEVC_BFRAMES","H264_BFRAMES","HEVC_BREF_MODE","H264_BREF_MODE","HEVC_LOOKAHEAD","H264_LOOKAHEAD"};
                string[][] optionValues={multipassValues,new[]{"FGS","0","1","2","3","4"},new[]{"FGS","0","1","2","3","4"},new[]{"FGS","disabled","middle"},new[]{"FGS","disabled","middle"},new[]{"FGS","0","8","16","24","27","32"},new[]{"FGS","0","8","16","24","27","32"}};
                string[] optionLabels={"H.264 Multipass","HEVC B-frames","H.264 B-frames","HEVC B-reference","H.264 B-reference","HEVC Lookahead","H.264 Lookahead"};
                for(int oi=0;oi<optionKeys.Length;oi++)
                {
                    int x=oi==0?28:oi%2==1?28:335,y=oi==0?360:398+((oi-1)/2)*38;
                    var box=new ThemeComboBox{Name=optionKeys[oi],DropDownStyle=ComboBoxStyle.DropDownList,Location=new Point(x+117,y-4),Size=new Size(180,26)};
                    foreach(string choice in optionValues[oi])box.Items.Add(choice=="FGS"?UiText("FGS 推荐","FGS recommended"):choice);
                    box.SelectedIndex=Math.Max(0,Array.FindIndex(optionValues[oi],delegate(string v){return string.Equals(v,config.Get(optionKeys[oi]),StringComparison.OrdinalIgnoreCase);}));
                    codecOptions[optionKeys[oi]]=box;
                    var optionLabel=new ThemeLabel{Text=optionLabels[oi],Location=new Point(x,y),Size=new Size(117,24)};
                    enc.Controls.Add(optionLabel);enc.Controls.Add(box);
                    string tip=NvencRecommendationTip(optionKeys[oi]);box.AccessibleDescription=tip;
                    encodingTip.SetToolTip(box,tip);encodingTip.SetToolTip(optionLabel,tip);
                }
                Label encInfo = new ThemeLabel {Name="AUDIO_INFO",Location=new Point(28,278),Size=new Size(630,32),ForeColor=ColorMuted,Text=UiText("默认保持原有组合；复制音轨不使用 AAC 码率设置。","Defaults preserve existing combinations; copied tracks ignore AAC bitrate.")};enc.Controls.Add(encInfo);

                // Two compact encoder pages. Audio remains a single shared setting.
                Action<Control,TabPage,int,int,int> place = delegate(Control control, TabPage page, int x, int y, int width) {
                    page.Controls.Add(control); control.Location=new Point(x,y); control.Width=width;
                };
                Action<TabPage,string,int> heading = delegate(TabPage page,string text,int y) {
                    page.Controls.Add(new ThemeLabel { Text=text,Location=new Point(28,y),Size=new Size(400,24),Font=UiFont(9f,FontStyle.Bold) });
                };
                Action<TabPage,int> divider = delegate(TabPage page,int y) {
                    page.Controls.Add(new ThemeLabel { Name="ENCODER_DIVIDER",Location=new Point(28,y),Size=new Size(672,1),BorderStyle=BorderStyle.FixedSingle });
                };
                heading(enc,"AV1 · NVENC",14); divider(enc,190); heading(enc,"HEVC · NVENC",200);
                heading(h264Page,"H.264 · NVENC",14); divider(h264Page,230); heading(h264Page,"H.264 · x264",240);
                var layoutControls=new List<Control>();foreach(Control control in enc.Controls)layoutControls.Add(control);
                foreach(Control control in layoutControls) {
                    if(control is Label) {
                        if(control.Text=="AV1 Multipass") place(control,enc,28,50,155);
                        else if(control.Text=="HEVC Multipass") place(control,enc,28,232,155);
                        else if(control.Text==UiText("音频处理","Audio policy")) { control.Text=UiText("音频（四线共用）","Audio (all encoders)");place(control,enc,28,86,155); }
                        else if(control.Text==UiText("AAC 音频码率","AAC bitrate")) place(control,enc,28,122,155);
                        else if(control.Text=="H.264 Multipass") place(control,h264Page,28,50,155);
                        else if(control.Text=="HEVC B-frames") place(control,enc,28,268,155);
                        else if(control.Text=="HEVC B-reference") place(control,enc,28,304,155);
                        else if(control.Text=="HEVC Lookahead") place(control,enc,28,340,155);
                        else if(control.Text=="H.264 B-frames") place(control,h264Page,28,86,155);
                        else if(control.Text=="H.264 B-reference") place(control,h264Page,28,122,155);
                        else if(control.Text=="H.264 Lookahead") place(control,h264Page,28,158,155);
                    }
                }
                place(av1Multipass,enc,190,46,260);place(audioMode,enc,190,82,440);place(audioRate,enc,190,118,260);place(encInfo,enc,28,150,672);
                place(hevcMultipass,enc,190,228,260);
                place(codecOptions["HEVC_BFRAMES"],enc,190,264,260);place(codecOptions["HEVC_BREF_MODE"],enc,190,300,260);place(codecOptions["HEVC_LOOKAHEAD"],enc,190,336,260);
                place(codecOptions["H264_MULTIPASS"],h264Page,190,46,260);place(codecOptions["H264_BFRAMES"],h264Page,190,82,260);place(codecOptions["H264_BREF_MODE"],h264Page,190,118,260);place(codecOptions["H264_LOOKAHEAD"],h264Page,190,154,260);
                high10.Name="H264_HIGH10";rate.Name="X264_RATE_MODE";preset.Name="X264_PRESET";
                place(high10,h264Page,28,268,650);place(rateLabel,h264Page,28,308,155);place(rate,h264Page,190,304,260);place(presetLabel,h264Page,28,344,155);place(preset,h264Page,190,340,260);
                aqLabel.Text="Spatial AQ";place(aqLabel,enc,28,376,155);place(aq,enc,190,372,180);place(temporal,enc,390,372,280);
                aq.Name="HEVC_SPATIAL_AQ";temporal.Name="HEVC_TEMPORAL_AQ";temporal.Text="Temporal AQ";
                Func<string,int> aqIndex=delegate(string key) {int value=config.GetInt(key,8);return value==0?0:value==4?1:value==10?3:value==12?4:value==15?5:2;};
                aq.SelectedIndex=aqIndex("HEVC_SPATIAL_AQ");temporal.Checked=config.GetBool("HEVC_TEMPORAL_AQ");
                aq.Enabled=NvencAqUiPolicy.IsEnabled(hardwareCapsReady,hardwareCaps,false,false);temporal.Enabled=NvencAqUiPolicy.IsEnabled(hardwareCapsReady,hardwareCaps,true,false);
                var h264Aq=new ThemeComboBox {Name="H264_SPATIAL_AQ",DropDownStyle=ComboBoxStyle.DropDownList,Location=new Point(190,190),Size=new Size(180,26)};
                foreach(object item in aq.Items)h264Aq.Items.Add(item);h264Aq.SelectedIndex=aqIndex("H264_SPATIAL_AQ");h264Aq.Enabled=NvencAqUiPolicy.IsEnabled(hardwareCapsReady,hardwareCaps,false,true);
                var h264Temporal=new ThemeCheckBox {Name="H264_TEMPORAL_AQ",Text="Temporal AQ",Checked=config.GetBool("H264_TEMPORAL_AQ"),Location=new Point(390,190),Size=new Size(280,26),Enabled=NvencAqUiPolicy.IsEnabled(hardwareCapsReady,hardwareCaps,true,true)};
                var h264AqLabel=new ThemeLabel {Text="Spatial AQ",Location=new Point(28,194),Size=new Size(155,24)};h264Page.Controls.AddRange(new Control[]{h264AqLabel,h264Aq,h264Temporal});
                foreach(Control control in new Control[]{aq,aqLabel,temporal,h264Aq,h264AqLabel,h264Temporal}) {
                    bool isH264=control==h264Aq||control==h264AqLabel||control==h264Temporal;
                    string tip=(isH264?"H.264 NVENC: ":"HEVC NVENC: ")+UiText("Spatial AQ 默认8，0关闭；Temporal AQ 默认关闭。仅对本编码器支持的能力生效，独立保存，不影响另一编码器、AV1或x264。", "Spatial AQ defaults to 8; 0 disables it. Temporal AQ defaults off. Applied only when this encoder supports the capability. Saved independently; does not affect the other encoder, AV1 or x264.");
                    control.AccessibleDescription=tip;encodingTip.SetToolTip(control,tip);
                }


                var cqLimitChecks=new Dictionary<string,CheckBox>();
                var cqLimitValues=new Dictionary<string,TextBox>();
                foreach(string codec in new[]{"AV1","HEVC","H264"}) {
                    Control page=codec=="H264"?h264Page:enc;int y=codec=="HEVC"?200:14;
                    string prefix="NVENC_"+codec+"_";
                    var enabled=new ThemeCheckBox {Name=prefix+"CQ_LIMIT_ENABLED",Text=codec+UiText(" CQ 码率上限", " CQ bitrate limit"),Checked=config.GetBool(prefix+"CQ_LIMIT_ENABLED"),Location=new Point(470,y),Size=new Size(235,26)};
                    var value=new ThemeTextBox {Name=prefix+"CQ_MAXRATE",Text=config.Get(prefix+"CQ_MAXRATE"),AutoSize=false,Location=new Point(470,y+32),Size=new Size(110,26)};
                    var unit=new ThemeLabel {Text="Mbps",Location=new Point(590,y+36),Size=new Size(60,24)};
                    ComboBox alignedPeer=codec=="AV1"?av1Multipass:codec=="HEVC"?hevcMultipass:codecOptions["H264_MULTIPASS"];
                    dlg.Shown+=delegate {value.Top=alignedPeer.Top;value.Height=alignedPeer.Height;};
                    value.Enabled=enabled.Checked;enabled.CheckedChanged+=delegate {value.Enabled=enabled.Checked;};
                    string tip=UiText("仅 CQ 生效；默认 AV1 80 / HEVC 100 / H.264 120 Mbps，VBV 缓冲为上限的 2 秒。可取消限制；VBR 保留原策略。达到上限可能降低画质，不保证文件体积或所有设备兼容。", "CQ only. Defaults: AV1 80 / HEVC 100 / H.264 120 Mbps, with a two-second VBV buffer. Optional; VBR retains its policy. The cap may reduce quality; it does not guarantee file size or device compatibility.");
                    encodingTip.SetToolTip(enabled,tip);encodingTip.SetToolTip(value,tip);page.Controls.AddRange(new Control[]{enabled,value,unit});
                    cqLimitChecks[codec]=enabled;cqLimitValues[codec]=value;
                }
                Label algoLabel = new ThemeLabel(); algoLabel.Text = UiText("插帧算法", "SmoothFps Algo"); algoLabel.Location = new Point(18, 18); algoLabel.Size = new Size(150, 24); interp.Controls.Add(algoLabel);
                ComboBox algo = new ThemeComboBox(); algo.DropDownStyle = ComboBoxStyle.DropDownList; algo.Location = new Point(180, 14); algo.Size = new Size(150, 26);
                foreach (string item in new string[] { "1", "2", "11", "13", "21", "22", "23" }) algo.Items.Add(item);
                int ai = algo.Items.IndexOf(config.Get("SVP_ALGO")); algo.SelectedIndex = ai >= 0 ? ai : 3; interp.Controls.Add(algo);
                Label analyseLabel = new ThemeLabel(); analyseLabel.Text = UiText("分析方案", "Analyse Profile"); analyseLabel.Location = new Point(18, 54); analyseLabel.Size = new Size(150, 24); interp.Controls.Add(analyseLabel);
                ComboBox analyse = new ThemeComboBox(); analyse.DropDownStyle = ComboBoxStyle.DropDownList; analyse.Location = new Point(180, 50); analyse.Size = new Size(150, 26); analyse.DropDownWidth = 360;
                analyse.Items.Add(lang.T("advanced.analyse_recommended")); analyse.Items.Add(lang.T("advanced.analyse_baseline")); analyse.SelectedIndex = string.Equals(config.Get("SVP_ANALYSE"), "BASE", StringComparison.OrdinalIgnoreCase) ? 1 : 0; interp.Controls.Add(analyse);
                Label maskLabel = new ThemeLabel(); maskLabel.Text = UiText("伪影遮罩范围", "Artifact Mask Area"); maskLabel.Location = new Point(18, 90); maskLabel.Size = new Size(150, 24); interp.Controls.Add(maskLabel);
                NumericUpDown mask = new ThemeNumericUpDown(); mask.Location = new Point(180, 86); mask.Size = new Size(120, 26); mask.Minimum = 0; mask.Maximum = 100; mask.Increment = 5; mask.Value = Math.Max(mask.Minimum, Math.Min(mask.Maximum, config.GetInt("SVP_MASK_AREA", 100))); interp.Controls.Add(mask);
                Label speedLabel = new ThemeLabel { Text = UiText("OpenSVPFlow 速度档位", "OpenSVPFlow Speed"), Location = new Point(18, 126), Size = new Size(155, 24) }; interp.Controls.Add(speedLabel);
                ComboBox svpSpeed = new ThemeComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(180, 122), Size = new Size(150, 26) };
                svpSpeed.Items.AddRange(new object[] { "FGS", "medium", "fast", "faster", "fastest" });
                svpSpeed.SelectedItem = config.Get("SVP_SPEED"); if (svpSpeed.SelectedIndex < 0) svpSpeed.SelectedIndex = 0; interp.Controls.Add(svpSpeed);
                Label tuningLabel = new ThemeLabel { Text = UiText("OpenSVPFlow 调校预设", "OpenSVPFlow Tuning"), Location = new Point(18, 162), Size = new Size(155, 24) }; interp.Controls.Add(tuningLabel);
                ComboBox svpTuning = new ThemeComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(180, 158), Size = new Size(150, 26) };
                svpTuning.Items.AddRange(new object[] { "FGS", "weak", "film", "smooth", "animation" });
                svpTuning.SelectedItem = config.Get("SVP_TUNING"); if (svpTuning.SelectedIndex < 0) svpTuning.SelectedIndex = 0; interp.Controls.Add(svpTuning);
                Label blockLabel = new ThemeLabel { Text = UiText("OpenSVPFlow 分块大小", "OpenSVPFlow Block Size"), Location = new Point(18, 198), Size = new Size(160, 24) }; interp.Controls.Add(blockLabel);
                ComboBox svpBlock = new ThemeComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(180, 194), Size = new Size(150, 26) };
                svpBlock.Items.AddRange(new object[] { "auto", "8", "16", "32" });
                svpBlock.SelectedItem = config.Get("SVP_BLOCK_SIZE"); if (svpBlock.SelectedIndex < 0) svpBlock.SelectedIndex = 0; interp.Controls.Add(svpBlock);
                EventHandler updateSvpProfile = delegate {
                    bool baseline = svpSpeed.SelectedIndex == 0 && svpTuning.SelectedIndex == 0 && svpBlock.SelectedIndex == 0;
                    analyse.Enabled = baseline; if (!baseline) analyse.SelectedIndex = 0;
                };
                svpSpeed.SelectedIndexChanged += updateSvpProfile; svpTuning.SelectedIndexChanged += updateSvpProfile; svpBlock.SelectedIndexChanged += updateSvpProfile;
                updateSvpProfile(null, EventArgs.Empty);
                Label frucPerfLabel = new ThemeLabel(); frucPerfLabel.Text = UiText("FRUC Vulkan 性能档位", "FRUC Vulkan perf"); frucPerfLabel.Location = new Point(18, 238); frucPerfLabel.Size = new Size(150, 24); interp.Controls.Add(frucPerfLabel);
                ComboBox frucPerf = new ThemeComboBox(); frucPerf.DropDownStyle = ComboBoxStyle.DropDownList; frucPerf.Location = new Point(180, 234); frucPerf.Size = new Size(150, 26);
                frucPerf.Items.Add("slow"); frucPerf.Items.Add("medium"); frucPerf.Items.Add("fast"); string savedPerf = config.Get("FRUC_PERF").ToLowerInvariant(); frucPerf.SelectedIndex = savedPerf == "slow" ? 0 : (savedPerf == "fast" ? 2 : 1); interp.Controls.Add(frucPerf);
                Label frucGridLabel = new ThemeLabel(); frucGridLabel.Text = UiText("FRUC Vulkan 光流网格", "FRUC Vulkan grid"); frucGridLabel.Location = new Point(18, 274); frucGridLabel.Size = new Size(150, 24); interp.Controls.Add(frucGridLabel);
                ComboBox frucGrid = new ThemeComboBox(); frucGrid.DropDownStyle = ComboBoxStyle.DropDownList; frucGrid.Location = new Point(180, 270); frucGrid.Size = new Size(150, 26);
                string supportedFrucGrids=SharedFrucCore.SupportedGrids(hardwareCaps==null?"":hardwareCaps.FrucEvidence);
                frucGrid.Items.Add("auto");
                foreach(string size in new[]{"1","2","4","8"})
                    if(supportedFrucGrids.Length==0 || Array.IndexOf(supportedFrucGrids.Split(','),size)>=0 || config.Get("FRUC_GRID")==size)frucGrid.Items.Add(size);
                 int gridIndex = frucGrid.Items.IndexOf(config.Get("FRUC_GRID").ToLowerInvariant()); frucGrid.SelectedIndex = gridIndex >= 0 ? gridIndex : 0; interp.Controls.Add(frucGrid);
                // Localize only the displayed text. SelectedItem remains the original
                // upstream token, so config/GUI/CLI parameters never receive translations.
                var interpolationNames = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
                    {"weak","低伪影优先"},{"auto","自动"},{"slow","慢速"},{"medium","中等"},{"fast","快速"},
                    {"faster","更快"},{"fastest","最快"},{"film","电影"},{"smooth","流畅"},{"animation","动画"}
                };
                foreach(ComboBox choice in new ComboBox[]{svpSpeed,svpTuning,svpBlock,frucPerf,frucGrid}) {
                    choice.Format+=delegate(object sender,ListControlConvertEventArgs e) {
                        string translated;
                        if(object.ReferenceEquals(sender,frucGrid)) {
                            string token=Convert.ToString(e.ListItem,CultureInfo.InvariantCulture);
                            if(token=="auto")e.Value=UiText("自动：最大可用", "Auto: largest supported")+(supportedFrucGrids.Length==0?"":" ("+SharedFrucCore.LargestGrid("grid_supported="+supportedFrucGrids)+")");
                            else if(supportedFrucGrids.Length>0 && Array.IndexOf(supportedFrucGrids.Split(','),token)<0)e.Value=token+UiText("（不支持）"," (unsupported)");
                            return;
                        }
                        if(string.Equals(lang.Code,"zh-CN",StringComparison.OrdinalIgnoreCase) && interpolationNames.TryGetValue(Convert.ToString(e.ListItem,CultureInfo.InvariantCulture),out translated))e.Value=translated;
                    };
                    choice.FormattingEnabled=true;
                }
                Button updateSvp = new ThemeButton { Text = UiText("更新 OpenSVPFlow 插件", "Update OpenSVPFlow plugins"), Location = new Point(22, 586), Size = new Size(235, 30) };
                updateSvp.Click += delegate { StartNativeSvpUpdate(dlg); }; dlg.Controls.Add(updateSvp);

                Func<string,decimal,decimal,int,NumericUpDown> extraNumber = delegate(string key,decimal fallback,decimal maximum,int y) {
                    NumericUpDown field = new ThemeNumericUpDown { Location=new Point(560,y),Size=new Size(110,26),Minimum=0,Maximum=maximum,DecimalPlaces=2,Increment=0.1m };
                    decimal value; if(!decimal.TryParse(config.Get(key),NumberStyles.Float,CultureInfo.InvariantCulture,out value))value=fallback;
                    field.Value=Math.Max(0,Math.Min(maximum,value));interp.Controls.Add(field);return field;
                };
                Action<string,int> extraLabel = delegate(string text,int y) { interp.Controls.Add(new ThemeLabel { Text=text,Location=new Point(360,y+4),Size=new Size(195,24) }); };
                extraLabel(UiText("重复帧阈值（0 = 关闭）","Dedup threshold (0 = off)"),18);
                NumericUpDown dedup=extraNumber("SVP_DEDUP",0,10,18);
                CheckBox still = new ThemeCheckBox { Text=UiText("保护静止文字 / 标识","Protect still text / logos"),Checked=config.GetBool("SVP_STILL"),Location=new Point(360,58),Size=new Size(310,26) };interp.Controls.Add(still);
                extraLabel("Still limit",90);NumericUpDown stillLimit=extraNumber("SVP_STILL_LIMIT",4.5m,100,90);
                extraLabel("Still edge",122);NumericUpDown stillEdge=extraNumber("SVP_STILL_EDGE",5,100,122);
                extraLabel("Still tolerance",154);NumericUpDown stillTolerance=extraNumber("SVP_STILL_TOLERANCE",30,100,154);
                CheckBox peel = new ThemeCheckBox { Text=UiText("剥离 HUD 后重合成（Peel）","Peel HUD before warp, then restore"),Checked=config.GetBool("SVP_PEEL"),Location=new Point(360,194),Size=new Size(310,26) };interp.Controls.Add(peel);
                extraLabel("Peel limit",226);NumericUpDown peelLimit=extraNumber("SVP_PEEL_LIMIT",6,100,226);
                EventHandler refreshExtra = delegate { stillLimit.Enabled=stillEdge.Enabled=stillTolerance.Enabled=still.Checked;peelLimit.Enabled=peel.Checked; };
                still.CheckedChanged+=refreshExtra;peel.CheckedChanged+=refreshExtra;refreshExtra(null,EventArgs.Empty);
                Label extraInfo = new ThemeLabel { Text=UiText("默认全部关闭；dedup 建议从 0.5 开始。\nStill / Peel 仅 GPU，需支持这些选项的插件。\nHDR 直通、隔行旁路与 FRUC 不使用这些选项。", "All off by default; start dedup at 0.5.\nStill / Peel: GPU and compatible plugins required.\nNot used with HDR Preserve, interlaced bypass or FRUC."),Location=new Point(360,266),Size=new Size(310,80),ForeColor=ColorMuted };interp.Controls.Add(extraInfo);
                interpTips.SetToolTip(dedup,UiText("仅处理孤立重复帧；亮度采样平均差低于阈值才算重复，不是VFR或音画同步修复。", "Handles isolated repeats using sampled mean luma difference; not a VFR or audio sync repair."));
                interpTips.SetToolTip(still,UiText("上游 GPU 静止遮罩，帮助减少固定文字和标识的拖影。", "Upstream GPU still mask for stationary text and logos."));
                interpTips.SetToolTip(peel,UiText("上游 GPU HUD 剥离/重合成；先单独测试，再测试与Still的组合。", "Upstream GPU HUD peeling; test separately before combining with Still."));

                Label hdrLabel = new ThemeLabel(); hdrLabel.Text = lang.T("advanced.hdr_policy"); hdrLabel.Location = new Point(28, 34); hdrLabel.Size = new Size(150, 24); hdr.Controls.Add(hdrLabel);
                ComboBox hdrPolicy = new ThemeComboBox(); hdrPolicy.DropDownStyle = ComboBoxStyle.DropDownList; hdrPolicy.Location = new Point(190, 30); hdrPolicy.Size = new Size(330, 26);
                hdrPolicy.Items.Add(lang.T("advanced.hdr_auto")); hdrPolicy.Items.Add(lang.T("advanced.hdr_preserve")); hdrPolicy.Items.Add(lang.T("advanced.hdr_force_sdr")); hdrPolicy.SelectedIndex = string.Equals(config.Get("HDR_POLICY"), "PRESERVE", StringComparison.OrdinalIgnoreCase) ? 1 : (string.Equals(config.Get("HDR_POLICY"), "SDR", StringComparison.OrdinalIgnoreCase) ? 2 : 0); hdr.Controls.Add(hdrPolicy);
                Label toneLabel = new ThemeLabel(); toneLabel.Text = "Tone Mapping"; toneLabel.Location = new Point(28, 82); toneLabel.Size = new Size(150, 24); hdr.Controls.Add(toneLabel);
                ComboBox tone = new ThemeComboBox(); tone.DropDownStyle = ComboBoxStyle.DropDownList; tone.Location = new Point(190, 78); tone.Size = new Size(220, 26);
                tone.Items.Add(lang.T("advanced.hable")); tone.Items.Add("Mobius"); tone.Items.Add("Reinhard"); tone.Items.Add("Gamma"); tone.Items.Add("Linear"); tone.Items.Add("Clip");
                string ta = config.Get("TONE_MAP_ALGO").ToLowerInvariant(); tone.SelectedIndex = ta == "mobius" ? 1 : (ta == "reinhard" ? 2 : (ta == "gamma" ? 3 : (ta == "linear" ? 4 : (ta == "clip" ? 5 : 0)))); hdr.Controls.Add(tone);
                Label hdrInfo = new ThemeLabel(); hdrInfo.Location = new Point(28, 132); hdrInfo.Size = new Size(620, 150); hdrInfo.ForeColor = ColorMuted; hdrInfo.Text = lang.T("advanced.hdr_info"); hdr.Controls.Add(hdrInfo);
                EventHandler updateHdr = delegate { bool enabled = hdrPolicy.SelectedIndex != 1; tone.Enabled = enabled; toneLabel.Enabled = enabled; };
                hdrPolicy.SelectedIndexChanged += updateHdr; updateHdr(null, EventArgs.Empty);

                Button ok = new ThemeButton(); ok.Name="ADVANCED_OK";ok.Text = lang.T("advanced.ok"); ok.Location = new Point(554, 466); ok.Size = new Size(88, 30); dlg.Controls.Add(ok);
                Button cancel = new ThemeButton(); cancel.Text = lang.T("advanced.cancel"); cancel.Location = new Point(650, 466); cancel.Size = new Size(88, 30); cancel.DialogResult = DialogResult.Cancel; dlg.Controls.Add(cancel);
                dlg.CancelButton = cancel;

                Button pageDefaults = new ThemeButton { Name="PAGE_DEFAULTS",Text = lang.T("config.defaults"), Location = new Point(434, 466), Size = new Size(112, 30) }; dlg.Controls.Add(pageDefaults);
                updateSvp.Top=466;
                EventHandler updateAdvancedActions = delegate { updateSvp.Visible = tabs.SelectedTab == interp; };
                tabs.SelectedIndexChanged += updateAdvancedActions; updateAdvancedActions(null, EventArgs.Empty);
                pageDefaults.Click += delegate {
                    if (tabs.SelectedTab == enc) { foreach(string codec in new[]{"AV1","HEVC"}) {cqLimitChecks[codec].Checked=true;cqLimitValues[codec].Text=codec=="AV1"?"80":"100";} aq.SelectedIndex=2;temporal.Checked=false;audioMode.SelectedIndex=0;audioRate.SelectedIndex=4;av1Multipass.SelectedIndex=hevcMultipass.SelectedIndex=0;foreach(var item in codecOptions)if(item.Key.StartsWith("HEVC_",StringComparison.Ordinal))item.Value.SelectedIndex=0; }
                    else if(tabs.SelectedTab==h264Page) {cqLimitChecks["H264"].Checked=true;cqLimitValues["H264"].Text="120";high10.Checked=false;rate.SelectedIndex=preset.SelectedIndex=0;h264Aq.SelectedIndex=2;h264Temporal.Checked=false;foreach(var item in codecOptions)if(item.Key.StartsWith("H264_",StringComparison.Ordinal))item.Value.SelectedIndex=0;}
                    else if (tabs.SelectedTab == interp) { svpSpeed.SelectedIndex = 0; svpTuning.SelectedIndex = 0; svpBlock.SelectedIndex = 0; algo.SelectedItem = "13"; analyse.SelectedIndex = 0; mask.Value = 100; frucPerf.SelectedIndex = 1; frucGrid.SelectedIndex = 0; dedup.Value=0;still.Checked=false;stillLimit.Value=4.5m;stillEdge.Value=5;stillTolerance.Value=30;peel.Checked=false;peelLimit.Value=6; }
                    else if (tabs.SelectedTab == hdr) { hdrPolicy.SelectedIndex = 0; tone.SelectedIndex = 0; updateHdr(null, EventArgs.Empty); }
                };

                interpTips.InitialDelay = 500; interpTips.ReshowDelay = 100; interpTips.AutoPopDelay = 15000; interpTips.ShowAlways = true;
                Action<Control, Control, string> setInterpTip = delegate(Control field, Control label, string text) {
                    field.AccessibleDescription=text;interpTips.SetToolTip(field, text); interpTips.SetToolTip(label, text);
                };
                setInterpTip(algo, algoLabel, UiText("SmoothFps 插帧算法，默认 13。算法与 Speed/Tuning 独立生效。", "SmoothFps interpolation algorithm; default 13. Independent of Speed/Tuning."));
                setInterpTip(analyse, analyseLabel, UiText("EncodeGUI 为推荐分析配置。BASE 仅支持 Speed=FGS、Tuning=FGS、Block Size=auto；选择新预设时使用 EncodeGUI。", "EncodeGUI is the recommended analysis profile. BASE requires Speed=FGS, Tuning=FGS and Block Size=auto; new presets use EncodeGUI."));
                setInterpTip(mask, maskLabel, UiText("伪影遮罩面积，范围 0–100，默认 100。控制 SmoothFps 的 mask.area。", "Artifact mask area: 0–100, default 100. Sets SmoothFps mask.area."));
                setInterpTip(svpSpeed, speedLabel, UiText("FGS 推荐速度：pel=1；配合 FGS 调校和自动分块时为 16×16、overlap=2、coarse distance=-10、thsad=250。它是独立组合，不等同于中等档。中等、快速、更快、最快调整精度、重叠与细化。", "FGS recommended speed: pel=1; with FGS tuning and auto blocks: 16x16, overlap=2, coarse distance=-10, thsad=250. A distinct combination, not the medium preset. Other speeds adjust precision, overlap and refinement."));
                setInterpTip(svpTuning, tuningLabel, UiText("FGS 推荐调校：coarse distance=-10、bad sad=2000、refine thsad=250，场景 blend=false；与速度/分块选项共同生效。低伪影优先（weak）采用更保守的插帧调校，偏向减少运动补偿伪影；部分场景可能增加融合感或降低流畅度，实际效果取决于素材。动画使用动画搜索配置；电影与流畅当前同义。", "FGS recommended tuning: coarse distance=-10, bad sad=2000, refine thsad=250, scene blend=false; combined with Speed/Block choices. weak changes motion search and enables scene blending in normal scene mode; animation uses animation search; film/smooth are aliases."));
                setInterpTip(svpBlock, blockLabel, UiText("FGS 推荐 auto：FGS 速度/调校为 16×16；中等为 8×8，快速/更快为 16×16，最快或动画为 32×32。手动 8/16/32 固定块尺寸。", "FGS recommended auto: FGS speed/tuning 16x16; medium 8x8; fast/faster 16x16; fastest or animation 32x32. Manual 8/16/32 fixes the block size."));
                setInterpTip(frucPerf, frucPerfLabel, UiText("性能档位控制光流估计：慢速优先质量，中等平衡，快速优先速度。FGS 默认中等；上游默认可能不同。效果与耗时须用同一视频比较。", "perf trades motion-estimation quality for speed: slow / medium / fast. FGS defaults to medium; upstream defaults can differ. Compare using the same video."));
                setInterpTip(frucGrid, frucGridLabel, UiText("FGS 自动选择最大可用网格（速度优先），不是 FFmpeg 原生 auto 的最细网格。支持项：", "FGS Auto selects the largest supported grid (speed priority), unlike FFmpeg's finest-grid auto. Supported: ")+(supportedFrucGrids.Length==0?UiText("待硬件检测；执行前验证", "pending detection; checked before execution"):supportedFrucGrids));

                ok.Click += delegate
                {
                    Dictionary<string, string> updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    updates["H264_HIGH10"] = high10.Checked && (!hardwareCapsReady || hardwareCaps == null || (hardwareCaps.X264Available && hardwareCaps.X264High10Available)) ? "true" : "false";
                    updates["X264_RATE_MODE"] = rate.SelectedIndex == 2 ? "3PASS" : (rate.SelectedIndex == 1 ? "2PASS" : "VBR1");
                    updates["X264_PRESET"] = preset.SelectedIndex == 1 ? "medium" : (preset.SelectedIndex == 2 ? "slow" : "faster");
                    int[] aqValues = new int[] { 0, 4, 8, 10, 12, 15 }; updates["HEVC_SPATIAL_AQ"] = aqValues[Math.Max(0, Math.Min(aqValues.Length - 1, aq.SelectedIndex))].ToString(CultureInfo.InvariantCulture);
                    updates["HEVC_TEMPORAL_AQ"] = temporal.Checked ? "true" : "false";
                    updates["H264_SPATIAL_AQ"] = aqValues[Math.Max(0, Math.Min(aqValues.Length - 1, h264Aq.SelectedIndex))].ToString(CultureInfo.InvariantCulture);
                    updates["H264_TEMPORAL_AQ"] = h264Temporal.Checked ? "true" : "false";
                    foreach(string codec in new[]{"AV1","HEVC","H264"}) {
                        updates["NVENC_"+codec+"_CQ_LIMIT_ENABLED"]=cqLimitChecks[codec].Checked?"true":"false";
                        int limit;
                        if(cqLimitChecks[codec].Checked&&(!int.TryParse(cqLimitValues[codec].Text.Trim(),out limit)||limit<1||limit>2000)) {
                            MessageBox.Show(dlg,codec+UiText(" CQ 最高码率请输入 1–2000 Mbps 的整数。"," CQ maximum bitrate must be an integer from 1 to 2000 Mbps."));dlg.DialogResult=DialogResult.None;cqLimitValues[codec].Focus();return;
                        }
                        updates["NVENC_"+codec+"_CQ_MAXRATE"]=cqLimitValues[codec].Text.Trim();
                    }
                    updates["AUDIO_MODE"]=audioModes[audioMode.SelectedIndex];updates["AUDIO_BITRATE"]=audioRates[audioRate.SelectedIndex];
                    for(int oi=0;oi<optionKeys.Length;oi++)updates[optionKeys[oi]]=optionValues[oi][codecOptions[optionKeys[oi]].SelectedIndex];
                    updates["AV1_MULTIPASS"]=multipassValues[av1Multipass.SelectedIndex];updates["HEVC_MULTIPASS"]=multipassValues[hevcMultipass.SelectedIndex];
                    if(hardwareCapsReady&&hardwareCaps!=null) {
                        if((av1Multipass.SelectedIndex==2&&!hardwareCaps.Av1MultipassQres)||(av1Multipass.SelectedIndex==3&&!hardwareCaps.Av1MultipassFullres)||(hevcMultipass.SelectedIndex==2&&!hardwareCaps.HevcMultipassQres)||(hevcMultipass.SelectedIndex==3&&!hardwareCaps.HevcMultipassFullres)||(codecOptions["H264_MULTIPASS"].SelectedIndex==2&&!hardwareCaps.H264MultipassQres)||(codecOptions["H264_MULTIPASS"].SelectedIndex==3&&!hardwareCaps.H264MultipassFullres))
                        {dlg.DialogResult=DialogResult.None;MessageBox.Show(dlg,UiText("硬件检测不支持所选 Multipass；请选择 FGS 推荐或支持的档位。","Selected multipass is unsupported; use FGS or a supported choice."));return;}
                    }

                    updates["SVP_ALGO"] = Convert.ToString(algo.SelectedItem, CultureInfo.InvariantCulture);
                    updates["SVP_ANALYSE"] = analyse.SelectedIndex == 1 ? "BASE" : "ENCODEGUI";
                    updates["SVP_MASK_AREA"] = ((int)mask.Value).ToString(CultureInfo.InvariantCulture);
                    updates["SVP_SPEED"] = Convert.ToString(svpSpeed.SelectedItem, CultureInfo.InvariantCulture);
                    updates["SVP_TUNING"] = Convert.ToString(svpTuning.SelectedItem, CultureInfo.InvariantCulture);
                    updates["SVP_BLOCK_SIZE"] = Convert.ToString(svpBlock.SelectedItem, CultureInfo.InvariantCulture);
                    updates["SVP_DEDUP"] = dedup.Value.ToString(CultureInfo.InvariantCulture);
                    updates["SVP_STILL"] = still.Checked ? "true" : "false";
                    updates["SVP_STILL_LIMIT"] = stillLimit.Value.ToString(CultureInfo.InvariantCulture);
                    updates["SVP_STILL_EDGE"] = stillEdge.Value.ToString(CultureInfo.InvariantCulture);
                    updates["SVP_STILL_TOLERANCE"] = stillTolerance.Value.ToString(CultureInfo.InvariantCulture);
                    updates["SVP_PEEL"] = peel.Checked ? "true" : "false";
                    updates["SVP_PEEL_LIMIT"] = peelLimit.Value.ToString(CultureInfo.InvariantCulture);

                    updates["FRUC_PERF"] = Convert.ToString(frucPerf.SelectedItem, CultureInfo.InvariantCulture);
                    string selectedGrid=Convert.ToString(frucGrid.SelectedItem,CultureInfo.InvariantCulture);
                    if(supportedFrucGrids.Length>0 && selectedGrid!="auto" && Array.IndexOf(supportedFrucGrids.Split(','),selectedGrid)<0) {
                        MessageBox.Show(dlg,UiText("所选 FRUC 网格不受支持，请选择自动或支持项：", "Unsupported FRUC grid. Choose Auto or a supported size: ")+supportedFrucGrids);return;
                    }
                    updates["FRUC_GRID"] = Convert.ToString(frucGrid.SelectedItem, CultureInfo.InvariantCulture);
                    updates["HDR_POLICY"] = hdrPolicy.SelectedIndex == 1 ? "PRESERVE" : (hdrPolicy.SelectedIndex == 2 ? "SDR" : "AUTO");
                    string[] toneValues = new string[] { "hable", "mobius", "reinhard", "gamma", "linear", "clip" }; updates["TONE_MAP_ALGO"] = toneValues[Math.Max(0, Math.Min(toneValues.Length - 1, tone.SelectedIndex))];
                    try
                    {
                        config.Save(updates);
                        dlg.DialogResult = DialogResult.OK;
                        dlg.Close();
                    }
                    catch (Exception ex) { MessageBox.Show(dlg, ex.Message, "Film Grain Studio", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                };


                AttachThemeWindow(dlg);
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    config.Load();
                    RefreshChangedHardware(hardwareBefore);
                    UpdateSpeedChoices();
                    UpdateHdrRouteStatus(GetSelectedMediaInfo());
                    if (log != null) log.AppendText("[Config] Advanced settings saved." + Environment.NewLine);
                }
            }
        }

    }
}
