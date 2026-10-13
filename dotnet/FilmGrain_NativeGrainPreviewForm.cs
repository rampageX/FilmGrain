using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FilmGrainStudioPreview
{
    // Decode actual AV1 film grain via libdav1d. No approximate noise renderer,
    // no changes to encoder state, no additional VapourSynth/Python dependency.
    internal sealed class NativeGrainPreviewForm : Form
    {
        private readonly string ffmpeg, grav1synth, source;
        private readonly List<string> grainArgs;
        private readonly MediaProbeInfo info;
        private readonly string temporary = Path.Combine(Path.GetTempPath(), "FGS_GrainPreview_" + Guid.NewGuid().ToString("N"));
        private readonly object sync = new object();
        private Process active;
        private volatile bool closing;
        private readonly PictureBox clean = new PictureBox(), grain = new PictureBox();
        private readonly Label status = new Label();
        private readonly TrackBar frame = new TrackBar();
        private readonly List<string> cleanFrames = new List<string>(), grainFrames = new List<string>();
        private readonly System.Windows.Forms.Timer playback = new System.Windows.Forms.Timer();
        private readonly Button play = new Button();
        private readonly CheckBox actualSize = new CheckBox();
        private readonly CheckBox skipBlack = new CheckBox();
        private bool running;
        private readonly Button render = new Button();

        internal NativeGrainPreviewForm(string ffmpegPath, string gravPath, string input, List<string> args, MediaProbeInfo media)
        {
            info = media; ffmpeg = ffmpegPath; grav1synth = gravPath; source = input; grainArgs = new List<string>(args);
            Text = "AV1 颗粒真实预览 / Actual film grain preview";
            Size = new Size(1100, 720); MinimumSize = new Size(1020, 500); StartPosition = FormStartPosition.CenterParent;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(8) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            layout.Controls.Add(new Label { Text = "原图（关闭源颗粒） / Clean", Dock = DockStyle.Fill }, 0, 0);
            layout.Controls.Add(new Label { Text = "当前颗粒参数 / Selected grain", Dock = DockStyle.Fill }, 1, 0);
            foreach (PictureBox box in new PictureBox[] { clean, grain }) { box.Dock = DockStyle.Fill; box.SizeMode = PictureBoxSizeMode.Zoom; box.BackColor = Color.Black; }
            layout.Controls.Add(clean, 0, 1); layout.Controls.Add(grain, 1, 1);
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            render.Text = "重新生成 / Render again"; render.AutoSize = true;
            render.Click += delegate { if (!running) StartRender(); };
            play.Text = "播放 / Play"; play.AutoSize = true; play.Enabled = false;
            play.Click += delegate { playback.Enabled = !playback.Enabled; };
            frame.Width = 220; frame.Maximum = 0; frame.TickStyle = TickStyle.None; frame.Enabled = false;
            frame.ValueChanged += delegate { ShowFrame(); };
            actualSize.Text = "1:1（中心裁切） / Center crop"; actualSize.AutoSize = true;
            actualSize.CheckedChanged += delegate { clean.SizeMode = grain.SizeMode = actualSize.Checked ? PictureBoxSizeMode.CenterImage : PictureBoxSizeMode.Zoom; };
            skipBlack.Text = "跳过开头黑帧 / Skip opening black"; skipBlack.Checked = true; skipBlack.AutoSize = true;
            bar.Controls.Add(render); bar.Controls.Add(play); bar.Controls.Add(frame); bar.Controls.Add(actualSize); bar.Controls.Add(skipBlack);
            layout.Controls.Add(bar, 0, 2); layout.SetColumnSpan(bar, 2);
            status.Dock = DockStyle.Fill; status.Text = "预览开头短片段；仅颗粒，不包含 LUT、调色、插帧。HDR 为显示转换，需在正式输出中验收。\r\nShort opening sample; grain only. HDR display is converted for this window.";
            layout.Controls.Add(status, 0, 3); layout.SetColumnSpan(status, 2); Controls.Add(layout);
            Shown += delegate { if (!running && !closing) StartRender(); };
            playback.Interval = 150;
            playback.Tick += delegate { if (frame.Maximum > 0) frame.Value = (frame.Value + 1) % (frame.Maximum + 1); };
            FormClosing += delegate { closing = true; playback.Stop(); lock (sync) { try { if (active != null && !active.HasExited) active.Kill(); } catch { } } };
            FormClosed += delegate { playback.Dispose(); ReleaseImages(); if (!running) Cleanup(); };
        }

        private void StartRender()
        {
            if (running || closing) return;
            running = true; render.Enabled = false; playback.Stop(); play.Enabled = frame.Enabled = false;
            ReleaseImages(); cleanFrames.Clear(); grainFrames.Clear();
            status.Text = "正在生成实际颗粒对照，请稍候… / Rendering actual grain…";
            bool skipOpening = skipBlack.Checked;
            Thread worker = new Thread(new ThreadStart(delegate
            {
                string error = ""; int firstFrame = 0; bool allBlack = false;
                try
                {
                    Directory.CreateDirectory(temporary);
                    foreach (string path in Directory.GetFiles(temporary, "*.png")) File.Delete(path);
                    string baseIvf = Path.Combine(temporary, "base.ivf"), grainIvf = Path.Combine(temporary, "grain.ivf");
                    // Keep IVF normalization identical to the established ISO/preset/table path.
                    Run(ffmpeg, new List<string> { "-hide_banner", "-v", "error", "-y", "-i", source, "-map", "0:v:0", "-c:v", "copy", "-frames:v", "160", "-an", "-sn", "-dn", "-f", "ivf", baseIvf });
                    if (skipOpening)
                    {
                        string scan = Path.Combine(temporary, "scan.gray");
                        Run(ffmpeg, BuildScanArguments(baseIvf, scan));
                        firstFrame = FindPreviewStartFrame(File.ReadAllBytes(scan));
                        allBlack = firstFrame < 0;
                        if (allBlack) firstFrame = 0;
                    }
                    var apply = new List<string> { "apply", baseIvf, "-o", grainIvf }; apply.AddRange(grainArgs); apply.AddRange(new string[] { "--replace", "-y" });
                    Run(grav1synth, apply);
                    // Eight full-resolution images; sample every four decoded frames. Never re-encode AV1.
                    RenderFrames(baseIvf, "0", "clean", firstFrame); RenderFrames(grainIvf, "1", "grain", firstFrame);
                }
                catch (Exception ex) { error = ex.Message; }
                finally
                {
                    running = false;
                    if (closing) Cleanup();
                    else
                    {
                        string result = error;
                        try { BeginInvoke(new Action(delegate
                        {
                            if (closing) return;
                            render.Enabled = true;
                            cleanFrames.Clear(); grainFrames.Clear();
                            if (result.Length == 0)
                            {
                                cleanFrames.AddRange(Directory.GetFiles(temporary, "clean*.png")); cleanFrames.Sort(StringComparer.Ordinal);
                                grainFrames.AddRange(Directory.GetFiles(temporary, "grain*.png")); grainFrames.Sort(StringComparer.Ordinal);
                                if (cleanFrames.Count == 0 || cleanFrames.Count != grainFrames.Count) result = "Preview frame counts do not match.";
                            }
                            if (result.Length > 0) { status.Text = "预览失败 / Preview failed: " + result; return; }
                            try
                            {
                                frame.Maximum = cleanFrames.Count - 1; frame.Value = 0; ShowFrame();
                            }
                            catch (Exception ex) { ReleaseImages(); status.Text = "预览画面加载失败 / Image load failed: " + ex.Message; return; }
                            frame.Enabled = play.Enabled = true;
                            status.Text = "实际 dav1d 合成；已跳过 " + firstFrame + " 个开头解码帧，显示 " + cleanFrames.Count + " 个采样帧。1:1 可检查颗粒细节。\r\n" + (allBlack ? "扫描范围内均为近黑画面；可取消跳过黑帧后重新生成。 " : "") + "Grain only; HDR display conversion; no LUT/interpolation.";
                        })); } catch { Cleanup(); }
                    }
                }
            }));
            worker.IsBackground = true; worker.Start();
        }

        private void RenderFrames(string input, string filmgrain, string prefix, int firstFrame)
        {
            // libdav1d must expose filmgrain; if unsupported, fail explicitly instead of
            // accidentally displaying an AV1 decode with grain disabled.
            Run(ffmpeg, BuildFrameRenderArguments(input, filmgrain, Path.Combine(temporary, prefix + "%02d.png"), info, firstFrame));
        }

        internal static List<string> BuildFrameRenderArguments(string input, string filmgrain, string outputPattern, MediaProbeInfo info)
        {
            return BuildFrameRenderArguments(input, filmgrain, outputPattern, info, 0);
        }

        internal static List<string> BuildFrameRenderArguments(string input, string filmgrain, string outputPattern, MediaProbeInfo info, int firstFrame)
        {
            return new List<string> { "-hide_banner", "-v", "error", "-y", "-c:v", "libdav1d", "-filmgrain", filmgrain, "-i", input,
                "-map", "0:v:0", "-vf", PreviewFilter(info, firstFrame), "-fps_mode", "passthrough", "-frames:v", "8", "-threads", "1", "-pix_fmt", "rgb24", outputPattern };
        }

        internal static string PreviewFilter(MediaProbeInfo info)
        {
            return PreviewFilter(info, 0);
        }

        private static string PreviewFilter(MediaProbeInfo info, int firstFrame)
        {
            string start = Math.Max(0, firstFrame).ToString(CultureInfo.InvariantCulture);
            string select = firstFrame <= 0 ? "select=not(mod(n\\,4))" : "select=gte(n\\," + start + ")*not(mod(n-" + start + "\\,4))";
            if (!info.IsHdr) return select;
            string range = info.ColorRange == "pc" ? "full" : "limited";
            return select + ",zscale=transferin=" + info.ColorTransfer + ":primariesin=bt2020:matrixin=bt2020nc:rangein=" + range
                + ":t=linear:npl=100,format=gbrpf32le,tonemap=tonemap=hable:desat=0,zscale=p=bt709:t=bt709:m=bt709:r=full,format=rgb24";
        }

        // Scan only the clean stream: grain must not make a black frame look visible.
        // Both panes then use identical decoded frame indices; no seek/re-encode.
        internal static List<string> BuildScanArguments(string input, string output)
        {
            return new List<string> { "-hide_banner", "-v", "error", "-y", "-c:v", "libdav1d", "-filmgrain", "0", "-i", input,
                "-map", "0:v:0", "-vf", "scale=64:36,format=gray", "-fps_mode", "passthrough", "-frames:v", "160", "-threads", "1", "-f", "rawvideo", output };
        }

        internal static int FindPreviewStartFrame(byte[] pixels)
        {
            const int size = 64 * 36;
            if (pixels == null || pixels.Length == 0 || pixels.Length % size != 0)
                throw new InvalidDataException("Preview scan has incomplete frames.");
            for (int index = 0; index < pixels.Length / size; index++)
            {
                int visible = 0;
                for (int p = index * size; p < (index + 1) * size; p++) if (pixels[p] > 12) visible++;
                if (visible > size / 100) return index;
            }
            return -1; // Preserve black content if the entire scanned opening is black.
        }

        private void Run(string tool, List<string> arguments)
        {
            if (!File.Exists(tool)) throw new FileNotFoundException("Preview dependency missing", tool);
            using (var process = new Process())
            {
                var command = new StringBuilder(); foreach (string argument in arguments) { if (command.Length > 0) command.Append(' '); command.Append(Quote(argument)); }
                process.StartInfo = new ProcessStartInfo(tool, command.ToString()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                lock (sync) { if (closing) throw new OperationCanceledException(); process.Start(); active = process; }
                try
                {
                    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(120000)) { try { process.Kill(); } catch { } process.WaitForExit(); throw new TimeoutException("Preview process timed out."); }
                    stdout.Wait(); stderr.Wait();
                    if (closing) throw new OperationCanceledException();
                    if (process.ExitCode != 0) throw new InvalidOperationException(stderr.Result.Length > 0 ? stderr.Result : stdout.Result);
                }
                finally { lock (sync) { if (ReferenceEquals(active, process)) active = null; } }
            }
        }
        private static string Quote(string value)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value) { if (c == '\\') { slashes++; continue; } if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append(c); slashes = 0; continue; } result.Append('\\', slashes); slashes = 0; result.Append(c); }
            result.Append('\\', slashes * 2); result.Append('"'); return result.ToString();
        }
        private void ShowFrame()
        {
            if (cleanFrames.Count == 0 || frame.Value >= cleanFrames.Count) return;
            ReplaceImage(clean, cleanFrames[frame.Value]); ReplaceImage(grain, grainFrames[frame.Value]);
        }
        private static void ReplaceImage(PictureBox box, string path)
        {
            Image next; using (var stream = new MemoryStream(File.ReadAllBytes(path))) using (var image = Image.FromStream(stream)) next = new Bitmap(image);
            Image previous = box.Image; box.Image = next; if (previous != null) previous.Dispose();
        }
        private void ReleaseImages() { foreach (var box in new PictureBox[] { clean, grain }) { if (box.Image != null) { box.Image.Dispose(); box.Image = null; } } }
        private void Cleanup() { try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { } }
    }
}
