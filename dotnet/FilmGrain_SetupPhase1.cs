using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FilmGrainStudioPreview
{
    internal static class SetupPhase1
    {
        private const string Version = "2";
        private static bool Exists(string path) { try { return File.Exists(path); } catch { return false; } }
        private static bool Ffmpeg(FgsConfig c) { return Exists(Path.Combine(c.Get("FFMPEG_DIR"), "ffmpeg.exe")) && Exists(Path.Combine(c.Get("FFMPEG_DIR"), "ffprobe.exe")); }
        private static bool Grav(FgsConfig c) { return Exists(c.Get("GRAV1SYNTH")); }
        private static string FfmpegOnPath()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string item in path.Split(';'))
            {
                string dir = Environment.ExpandEnvironmentVariables(item.Trim().Trim('"'));
                try { if (Exists(Path.Combine(dir, "ffmpeg.exe")) && Exists(Path.Combine(dir, "ffprobe.exe"))) return dir; }
                catch { }
            }
            return "";
        }
        public static void ShowIfNeeded(Form owner, string root, FgsConfig c, bool test)
        {
            Show(owner, root, c, test, false);
        }
        public static void ShowForInterpolation(Form owner, string root, FgsConfig c, bool test)
        {
            Show(owner, root, c, test, true);
        }
        private static void Show(Form owner, string root, FgsConfig c, bool test, bool forced)
        {
            if (!forced && !test && c.Get("SETUP_VERSION") == Version && Ffmpeg(c) && Grav(c)) return;
            bool simulatedFf = test, simulatedGrav = test, simulatedSvp = test;
            using (Form form = new Form())
            {
                form.Text = test ? "设置测试模式 - 首次运行设置" : "Film Grain Studio - 首次运行设置";
                form.Width = 1020; form.Height = 495; form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog; form.MaximizeBox = false;
                Label header = new Label { Left = 20, Top = 15, Width = 975, Height = 52, Text = test ? "测试模式：初次扫描模拟缺失。指定路径或刷新后进行真实验证。\r\n测试模式禁止安装；配置只保存在本次运行的临时 INI。" : "优先复用现有工具；下载的新工具放在 FGS\\_Dependencies。60 fps 插帧可选。\r\n下载与安装请在复制出的完整 FGS 目录中测试；成功后窗口自动关闭。" };
                form.Controls.Add(header);
                Label ff = new Label { Left = 20, Top = 78, Width = 460, Height = 44 };
                Label grav = new Label { Left = 20, Top = 140, Width = 460, Height = 44 };
                Label svp = new Label { Left = 20, Top = 203, Width = 460, Height = 55 };
                form.Controls.Add(ff); form.Controls.Add(grav); form.Controls.Add(svp);
                string ffLocal = Path.Combine(root, "_Dependencies", "FFmpeg");
                string gravLocal = Path.Combine(root, "_Dependencies", "Grav1synth", "grav1synth.exe");
                string pipe = Path.Combine(root, "_OpenSVPFlow", ".venv", "Scripts", "vspipe.exe");
                Action refresh = delegate {
                    if (!test && Exists(Path.Combine(ffLocal, "ffmpeg.exe")) && Exists(Path.Combine(ffLocal, "ffprobe.exe")) && (!Ffmpeg(c) || simulatedFf)) { c.SaveValue("FFMPEG_DIR", ffLocal); simulatedFf = false; }
                    if (!test && Exists(gravLocal) && (!Grav(c) || simulatedGrav)) { c.SaveValue("GRAV1SYNTH", gravLocal); simulatedGrav = false; }
                    if (Exists(pipe)) simulatedSvp = false;
                    ff.Text = "FFmpeg + ffprobe（必需）：" + (simulatedFf ? "模拟缺失" : Ffmpeg(c) ? "可用" : "缺失") + "\r\n" + c.Get("FFMPEG_DIR");
                    grav.Text = "grav1synth（AV1 颗粒）：" + (simulatedGrav ? "模拟缺失" : Grav(c) ? "可用" : "缺失") + "\r\n" + c.Get("GRAV1SYNTH");
                    svp.Text = "OpenSVPFlow（可选 60 fps）：" + (simulatedSvp ? "模拟缺失" : Exists(pipe) ? "已检测到 vspipe；实际能力由硬件检测确认" : "未安装") + "\r\n默认使用 FGS 内的 Python；勾选下方选项才检测系统 Python。";
                };
                string ffPath = FfmpegOnPath();
                Label proxyLabel = new Label { Left = 20, Top = 312, Width = 470, Height = 22, Text = "下载代理（可选，HTTP/Mixed 端口；仅本次安装使用）：" };
                TextBox proxyInput = new TextBox { Left = 500, Top = 308, Width = 470, Text = Environment.GetEnvironmentVariable("FGS_SETUP_PROXY") ?? "" };
                CheckBox useSystemPython = new CheckBox { Left = 500, Top = 268, Width = 470, Height = 29, Text = "使用系统 Python（需 3.12+、能创建 venv；默认不使用）", Checked = false };
                form.Controls.Add(proxyLabel); form.Controls.Add(proxyInput); form.Controls.Add(useSystemPython);
                bool installing = false;
                form.FormClosing += delegate(object sender, FormClosingEventArgs e) { if (installing) { e.Cancel = true; MessageBox.Show(form, "请等待安装窗口结束，失败时先按回车关闭该窗口。", "安装进行中"); } };
                Action<string> install = delegate(string tool) {
                    if (test) { MessageBox.Show(form, "测试模式只验证界面和路径。真实安装请复制 FGS 目录后普通启动。", "设置测试模式"); return; }
                    if (installing) { MessageBox.Show(form, "当前安装尚未结束。"); return; }
                    if (MessageBox.Show(form, "将在当前 FGS 目录安装 " + tool + "。确认当前目录是测试副本？\r\n不会修改系统 PATH 或已有 Python。", "真实安装", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    string proxy = proxyInput.Text.Trim();
                    if (proxy.Length > 0) {
                        Uri parsed;
                        if (!Uri.TryCreate(proxy, UriKind.Absolute, out parsed) ||
                            (parsed.Scheme != "http" && parsed.Scheme != "https") ||
                            parsed.UserInfo.Length > 0 || proxy.IndexOf('"') >= 0) {
                            MessageBox.Show(form, "请输入不含账号密码的 HTTP 代理地址，例如 http://127.0.0.1:7890。SOCKS 专用端口不适用。");
                            return;
                        }
                    }
                    string script = Path.Combine(root, "dotnet", "Setup_Dependencies.ps1");
                    if (!Exists(script)) { MessageBox.Show(form, "找不到安装脚本：" + script); return; }
                    try {
                        ProcessStartInfo info = new ProcessStartInfo();
                        info.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
                        info.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -Tool " + tool +
                            (proxy.Length > 0 ? " -ProxyUrl \"" + proxy + "\"" : "") +
                            (tool == "OpenSVPFlow" && useSystemPython.Checked ? " -UseSystemPython" : "") + " -PauseOnFailure";
                        info.UseShellExecute = true;
                        Process process = Process.Start(info);
                        if (process == null) throw new InvalidOperationException("安装进程未能启动。");
                        installing = true;
                        header.Text = "正在安装 " + tool + "。成功后下载窗口会自动关闭并更新路径；失败时窗口会停留，供你复制错误。";
                        ThreadPool.QueueUserWorkItem(delegate(object state) {
                            int code;
                            try { process.WaitForExit(); code = process.ExitCode; }
                            catch { code = -1; }
                            finally { process.Dispose(); }
                            if (form.IsDisposed || !form.IsHandleCreated) return;
                            try { form.BeginInvoke((MethodInvoker)delegate {
                                installing = false;
                                if (code == 0) {
                                    if (tool == "FFmpeg" && Exists(Path.Combine(ffLocal, "ffmpeg.exe")) && Exists(Path.Combine(ffLocal, "ffprobe.exe"))) {
                                        c.SaveValue("FFMPEG_DIR", ffLocal); simulatedFf = false;
                                    }
                                    if (tool == "Grav1synth" && Exists(gravLocal)) { c.SaveValue("GRAV1SYNTH", gravLocal); simulatedGrav = false; }
                                    if (tool == "OpenSVPFlow" && Exists(pipe)) simulatedSvp = false;
                                    refresh();
                                    MessageBox.Show(form, tool + " 安装成功，路径已更新。硬件能力请重启 FGS 后重新检测。", "安装完成");
                                } else MessageBox.Show(form, tool + " 安装未完成。请复制独立安装窗口中的错误，再按回车关闭该窗口。", "安装失败");
                            }); } catch (InvalidOperationException) { }
                        });
                    } catch (Exception ex) { installing = false; MessageBox.Show(form, ex.Message, "无法启动安装器"); }
                };
                Button ffChoose = new Button { Left = 500, Top = 86, Width = 150, Text = "指定 FFmpeg 目录" };
                ffChoose.Click += delegate { using (FolderBrowserDialog dlg = new FolderBrowserDialog()) { if (dlg.ShowDialog(form) == DialogResult.OK) { simulatedFf = false; if (Exists(Path.Combine(dlg.SelectedPath, "ffmpeg.exe")) && Exists(Path.Combine(dlg.SelectedPath, "ffprobe.exe"))) c.SaveValue("FFMPEG_DIR", dlg.SelectedPath); else MessageBox.Show(form, "须同时包含 ffmpeg.exe 和 ffprobe.exe。"); refresh(); } } };
                Button ffPathChoice = new Button { Left = 660, Top = 86, Width = 150, Text = ffPath.Length > 0 ? "使用 PATH 中的" : "PATH 未发现", Enabled = ffPath.Length > 0 };
                ffPathChoice.Click += delegate { if (ffPath.Length > 0) { c.SaveValue("FFMPEG_DIR", ffPath); simulatedFf = false; refresh(); } };
                Button ffInstall = new Button { Left = 820, Top = 86, Width = 150, Text = "下载并安装 Full" };
                ffInstall.Click += delegate { install("FFmpeg"); };
                Button gravChoose = new Button { Left = 500, Top = 148, Width = 150, Text = "指定 grav1synth" };
                gravChoose.Click += delegate { using (OpenFileDialog dlg = new OpenFileDialog { Filter = "grav1synth.exe|grav1synth.exe", CheckFileExists = true }) { if (dlg.ShowDialog(form) == DialogResult.OK) { simulatedGrav = false; if (string.Equals(Path.GetFileName(dlg.FileName), "grav1synth.exe", StringComparison.OrdinalIgnoreCase)) c.SaveValue("GRAV1SYNTH", dlg.FileName); refresh(); } } };
                Button gravInstall = new Button { Left = 820, Top = 148, Width = 150, Text = "下载并安装" };
                gravInstall.Click += delegate { install("Grav1synth"); };
                Button svpInstall = new Button { Left = 820, Top = 209, Width = 150, Text = "安装可选插帧" };
                svpInstall.Click += delegate { install("OpenSVPFlow"); };
                Button refreshButton = new Button { Left = 660, Top = 209, Width = 150, Text = "刷新检测" };
                refreshButton.Click += delegate { simulatedFf = false; simulatedGrav = false; simulatedSvp = false; refresh(); };
                Button done = new Button { Left = 675, Top = 382, Width = 145, Text = "保存并继续" };
                done.Click += delegate { if (installing) { MessageBox.Show(form, "请等待安装结束。"); return; } if (!Ffmpeg(c)) MessageBox.Show(form, "FFmpeg / ffprobe 尚未就绪，编码功能暂不可用。"); else if (!test) c.SaveValue("SETUP_VERSION", Version); form.Close(); };
                Button later = new Button { Left = 830, Top = 382, Width = 140, Text = "稍后设置" };
                later.Click += delegate { form.Close(); };
                form.Controls.Add(ffChoose); form.Controls.Add(ffPathChoice); form.Controls.Add(ffInstall); form.Controls.Add(gravChoose); form.Controls.Add(gravInstall);
                form.Controls.Add(svpInstall); form.Controls.Add(refreshButton); form.Controls.Add(done); form.Controls.Add(later);
                refresh(); form.ShowDialog(owner);
            }
        }
    }
}
