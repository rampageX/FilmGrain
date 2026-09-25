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
    internal sealed class ColorCorrectionResult
    {
        public bool Enabled;
        public double Contrast;
        public double Brightness;
        public double Saturation;
        public double Gamma;
        public bool BlackWhite;
        public bool UseLut;
        public string LutPath = "";
        public string LutSource = "None";
        public int LutStrength = 75;
    }

    internal sealed class NativeColorCorrectionForm : Form
    {
        private sealed class AdjustmentControl
        {
            public TrackBar Track;
            public NumericUpDown Number;
            public decimal DefaultValue;
        }

        private sealed class RenderState
        {
            public int Serial;
            public double Position;
            public bool Original;
            public bool Enabled;
            public double Contrast;
            public double Brightness;
            public double Saturation;
            public double Gamma;
            public bool BlackWhite;
            public bool UseLut;
            public string CompatLutPath;
            public int LutStrength;
        }

        private readonly string videoPath;
        private readonly string ffmpegPath;
        private readonly string ffprobePath;
        private readonly string lutSelectorPath;
        private readonly string lutRoot;
        private readonly string lutPreviewRoot;
        private readonly bool isZh;
        private readonly string tempRoot;
        private readonly string compatLutPath;
        private readonly int previewWidth;
        private readonly int previewHeight;
        private readonly object processLock = new object();

        private PictureBox pic;
        private Panel timelinePanel;
        private Label timeLabel;
        private Label status;
        private Label lutLabel;
        private Label lutStrengthLabel;
        private Label lutStrengthTitle;
        private CheckBox checkEnable;
        private CheckBox checkBlackWhite;
        private CheckBox checkLut;
        private TrackBar lutStrengthTrack;
        private Button openLutGallery;
        private Button originalButton;
        private readonly Dictionary<string, AdjustmentControl> adjustments = new Dictionary<string, AdjustmentControl>(StringComparer.OrdinalIgnoreCase);
        private System.Windows.Forms.Timer renderTimer;

        private double duration = 1.0;
        private double fps = 24.0;
        private int timelineValue = 1000;
        private bool timelineDragging;
        private long timelineLastClickTicks;
        private int timelineLastClickX = -1000;
        private bool syncing;
        private bool originalHeld;
        private bool renderWorkerRunning;
        private bool renderPending;
        private bool renderDue;
        private int renderSerial;
        private Process activeRenderProcess;
        private bool closing;
        private string lutPath;
        private string lutSource;
        private string activeCompatLutPath = "";
        private int lutStrength;

        public ColorCorrectionResult Result { get; private set; }

        public NativeColorCorrectionForm(
            string videoPath,
            string ffmpegPath,
            string ffprobePath,
            string lutSelectorPath,
            string lutRoot,
            string lutPreviewRoot,
            string lutPath,
            string lutSource,
            int lutStrength,
            bool enabled,
            double contrast,
            double brightness,
            double saturation,
            double gamma,
            bool blackWhite,
            bool useLut,
            bool largeUi,
            string languageCode)
        {
            this.videoPath = videoPath;
            this.ffmpegPath = ffmpegPath;
            this.ffprobePath = ffprobePath;
            this.lutSelectorPath = lutSelectorPath;
            this.lutRoot = lutRoot;
            this.lutPreviewRoot = lutPreviewRoot;
            this.lutPath = lutPath ?? "";
            this.lutSource = string.IsNullOrEmpty(lutSource) ? "None" : lutSource;
            this.lutStrength = NormalizeStrength(lutStrength);
            this.isZh = !string.Equals(languageCode, "en-US", StringComparison.OrdinalIgnoreCase);
            this.previewWidth = largeUi ? 960 : 720;
            this.previewHeight = largeUi ? 540 : 480;
            tempRoot = Path.Combine(Path.GetTempPath(), "FGS_NET_ColorPreview_" + Guid.NewGuid().ToString("N"));
            compatLutPath = Path.Combine(tempRoot, "preview_lut.cube");
            Directory.CreateDirectory(tempRoot);

            ProbeVideo();
            PrepareLut(this.lutPath);
            BuildUi(largeUi, enabled, contrast, brightness, saturation, gamma, blackWhite, useLut);
        }

        private string S(string zh, string en)
        {
            return isZh ? zh : en;
        }

        private static int NormalizeStrength(int value)
        {
            if (value == 25 || value == 50 || value == 75 || value == 100) return value;
            return 75;
        }

        private static string QuoteArg(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private static string EscapeFilterPath(string value)
        {
            return (value ?? "").Replace("\\", "/").Replace(":", "\\:").Replace("'", "'\\''");
        }

        private static string F(double value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private void ProbeVideo()
        {
            duration = ProbeDouble("-v error -show_entries format=duration -of default=nw=1:nk=1 " + QuoteArg(videoPath), 1.0);
            if (duration <= 0) duration = 1.0;

            string rate = ProbeText("-v error -select_streams v:0 -show_entries stream=avg_frame_rate -of default=nw=1:nk=1 " + QuoteArg(videoPath));
            double parsed = 24.0;
            if (!string.IsNullOrWhiteSpace(rate))
            {
                string[] parts = rate.Trim().Split('/');
                double a, b;
                if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b) && b != 0)
                    parsed = a / b;
                else if (!double.TryParse(rate.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    parsed = 24.0;
            }
            fps = parsed > 0 ? parsed : 24.0;
        }

        private double ProbeDouble(string arguments, double fallback)
        {
            double value;
            string text = ProbeText(arguments);
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private string ProbeText(string arguments)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = ffprobePath;
                psi.Arguments = arguments;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    return output.Trim();
                }
            }
            catch { return ""; }
        }

        private void PrepareLut(string path)
        {
            lutPath = path ?? "";
            activeCompatLutPath = "";
            try { if (File.Exists(compatLutPath)) File.Delete(compatLutPath); } catch { }
            if (string.IsNullOrEmpty(lutPath) || !File.Exists(lutPath)) return;

            try
            {
                List<string> output = new List<string>();
                string[] lines = File.ReadAllLines(lutPath);
                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    string[] parts = trimmed.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 3 && string.Equals(parts[0], "LUT_3D_INPUT_RANGE", StringComparison.OrdinalIgnoreCase))
                    {
                        output.Add("DOMAIN_MIN " + parts[1] + " " + parts[1] + " " + parts[1]);
                        output.Add("DOMAIN_MAX " + parts[2] + " " + parts[2] + " " + parts[2]);
                    }
                    else output.Add(line);
                }
                File.WriteAllLines(compatLutPath, output.ToArray(), Encoding.ASCII);
                activeCompatLutPath = compatLutPath;
            }
            catch
            {
                activeCompatLutPath = "";
            }
        }

        private void BuildUi(bool largeUi, bool enabled, double contrast, double brightness, double saturation, double gamma, bool blackWhite, bool useLut)
        {
            Text = S("色彩纠正与 LUT 实时预览", "Color Correction and LUT Preview");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(245, 246, 248);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            int dialogWidth = largeUi ? 1280 : 1040;
            int dialogHeight = largeUi ? 700 : 690;
            int timelineY = 14 + previewHeight + 11;
            int timeLabelY = timelineY + 40;
            int lutRowY = timeLabelY + 29;
            int buttonY = dialogHeight - 60;
            int rightX = 28 + previewWidth;
            ClientSize = new Size(dialogWidth, dialogHeight);

            pic = new PictureBox();
            pic.Location = new Point(14, 14);
            pic.Size = new Size(previewWidth, previewHeight);
            pic.BackColor = Color.Black;
            pic.BorderStyle = BorderStyle.FixedSingle;
            pic.SizeMode = PictureBoxSizeMode.Zoom;
            Controls.Add(pic);

            timelinePanel = new Panel();
            timelinePanel.Location = new Point(14, timelineY);
            timelinePanel.Size = new Size(previewWidth, 34);
            timelinePanel.BackColor = BackColor;
            timelinePanel.Cursor = Cursors.Hand;
            timelinePanel.Paint += TimelinePaint;
            timelinePanel.MouseDown += TimelineMouseDown;
            timelinePanel.MouseMove += TimelineMouseMove;
            timelinePanel.MouseUp += TimelineMouseUp;
            timelinePanel.MouseLeave += TimelineMouseLeave;
            Controls.Add(timelinePanel);

            timeLabel = new Label();
            timeLabel.Location = new Point(14, timeLabelY);
            timeLabel.Size = new Size(previewWidth, 24);
            timeLabel.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(timeLabel);

            GroupBox group = new GroupBox();
            group.Text = S("调整参数", "Adjustments");
            group.Location = new Point(rightX, 14);
            group.Size = new Size(278, 480);
            Controls.Add(group);

            checkEnable = new CheckBox();
            checkEnable.Text = S("启用色彩纠正", "Enable color correction");
            checkEnable.Location = new Point(18, 30);
            checkEnable.Size = new Size(230, 24);
            checkEnable.Checked = enabled;
            checkEnable.CheckedChanged += delegate
            {
                if (!syncing) RequestRender(false);
            };
            group.Controls.Add(checkEnable);

            int y = 72;
            AddAdjustment(group, "Contrast", S("对比度", "Contrast"), -200, 200, contrast, 1.0, y); y += 70;
            AddAdjustment(group, "Brightness", S("亮度", "Brightness"), -100, 100, brightness, 0.0, y); y += 70;
            AddAdjustment(group, "Saturation", S("饱和度", "Saturation"), 0, 300, saturation, 1.0, y); y += 70;
            AddAdjustment(group, "Gamma", "Gamma", 1, 300, gamma, 1.0, y);

            checkBlackWhite = new CheckBox();
            checkBlackWhite.Text = S("黑白模式", "Black and white mode");
            checkBlackWhite.Location = new Point(18, 344);
            checkBlackWhite.Size = new Size(230, 24);
            checkBlackWhite.Checked = blackWhite;
            checkBlackWhite.CheckedChanged += delegate
            {
                if (syncing) return;
                if (checkBlackWhite.Checked && !checkEnable.Checked)
                {
                    checkEnable.Checked = true;
                    return;
                }
                RequestRender(false);
            };
            group.Controls.Add(checkBlackWhite);

            Button reset = new Button();
            reset.Text = S("恢复默认", "Reset");
            reset.Location = new Point(18, 438);
            reset.Size = new Size(100, 28);
            reset.Click += delegate
            {
                syncing = true;
                try
                {
                    checkEnable.Checked = false;
                    checkBlackWhite.Checked = false;
                    foreach (AdjustmentControl item in adjustments.Values)
                    {
                        item.Number.Value = item.DefaultValue;
                        item.Track.Value = Math.Max(item.Track.Minimum, Math.Min(item.Track.Maximum, (int)Math.Round((double)item.DefaultValue * 100.0)));
                    }
                }
                finally { syncing = false; }
                RequestRender(true);
            };
            group.Controls.Add(reset);

            originalButton = new Button();
            originalButton.Text = S("按住查看原图", "Hold for original");
            originalButton.Location = new Point(130, 438);
            originalButton.Size = new Size(130, 28);
            originalButton.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                originalHeld = true;
                RequestRender(true);
            };
            originalButton.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (!originalHeld) return;
                originalHeld = false;
                RequestRender(true);
            };
            originalButton.MouseLeave += delegate
            {
                if (!originalHeld || Control.MouseButtons == MouseButtons.Left) return;
                originalHeld = false;
                RequestRender(true);
            };
            group.Controls.Add(originalButton);

            openLutGallery = new Button();
            openLutGallery.Text = S("打开 LUT 图库…", "Open LUT Gallery…");
            openLutGallery.Location = new Point(14, lutRowY);
            openLutGallery.Size = new Size(120, 26);
            openLutGallery.Click += delegate { OpenLutGalleryAsync(); };
            Controls.Add(openLutGallery);

            checkLut = new CheckBox();
            checkLut.Text = S("预览当前 LUT", "Preview current LUT");
            checkLut.Location = new Point(142, lutRowY);
            checkLut.Size = new Size(140, 24);
            checkLut.Checked = useLut && !string.IsNullOrEmpty(activeCompatLutPath);
            checkLut.CheckedChanged += delegate
            {
                UpdateLutUi();
                if (!syncing) RequestRender(false);
            };
            Controls.Add(checkLut);

            lutLabel = new Label();
            lutLabel.Location = new Point(286, lutRowY);
            lutLabel.Size = new Size(previewWidth - 272, 24);
            lutLabel.AutoEllipsis = true;
            lutLabel.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(lutLabel);

            int strengthY = lutRowY + 25;
            lutStrengthTitle = new Label();
            lutStrengthTitle.Text = S("LUT 强度", "LUT Strength");
            lutStrengthTitle.Location = new Point(14, strengthY);
            lutStrengthTitle.Size = new Size(82, 34);
            lutStrengthTitle.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(lutStrengthTitle);

            lutStrengthTrack = new TrackBar();
            lutStrengthTrack.Minimum = 0;
            lutStrengthTrack.Maximum = 3;
            lutStrengthTrack.Value = lutStrength == 25 ? 0 : (lutStrength == 50 ? 1 : (lutStrength == 100 ? 3 : 2));
            lutStrengthTrack.TickStyle = TickStyle.BottomRight;
            lutStrengthTrack.Location = new Point(96, strengthY);
            lutStrengthTrack.Size = new Size(previewWidth - 164, 40);
            lutStrengthTrack.ValueChanged += delegate { UpdateLutUi(); RequestRender(false); };
            Controls.Add(lutStrengthTrack);

            lutStrengthLabel = new Label();
            lutStrengthLabel.Location = new Point(previewWidth - 54, strengthY);
            lutStrengthLabel.Size = new Size(68, 34);
            lutStrengthLabel.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(lutStrengthLabel);

            status = new Label();
            status.Location = new Point(rightX, 510);
            status.Size = new Size(278, 110);
            status.TextAlign = ContentAlignment.TopLeft;
            Controls.Add(status);

            Button ok = new Button();
            ok.Text = S("确定", "OK");
            ok.Location = new Point(dialogWidth - 214, buttonY);
            ok.Size = new Size(96, 32);
            ok.Click += delegate
            {
                Result = new ColorCorrectionResult();
                Result.Enabled = checkEnable.Checked;
                Result.Contrast = (double)adjustments["Contrast"].Number.Value;
                Result.Brightness = (double)adjustments["Brightness"].Number.Value;
                Result.Saturation = (double)adjustments["Saturation"].Number.Value;
                Result.Gamma = (double)adjustments["Gamma"].Number.Value;
                Result.BlackWhite = checkBlackWhite.Checked;
                Result.UseLut = checkLut.Checked;
                Result.LutPath = lutPath;
                Result.LutSource = string.IsNullOrEmpty(lutPath) ? "None" : lutSource;
                Result.LutStrength = lutStrength;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = S("取消", "Cancel");
            cancel.Location = new Point(dialogWidth - 110, buttonY);
            cancel.Size = new Size(96, 32);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            renderTimer = new System.Windows.Forms.Timer();
            renderTimer.Interval = 120;
            renderTimer.Tick += delegate
            {
                renderTimer.Stop();
                renderDue = true;
                StartRenderIfReady();
            };

            UpdateLutUi();
            UpdateTimeLabel();
            Shown += delegate { RequestRender(true); };
            FormClosing += delegate { closing = true; CancelActiveRender(); };
            FormClosed += delegate
            {
                try { renderTimer.Stop(); renderTimer.Dispose(); } catch { }
                Image old = pic.Image; pic.Image = null; if (old != null) old.Dispose();
                try { Directory.Delete(tempRoot, true); } catch { }
            };
        }

        private void AddAdjustment(Control parent, string key, string caption, int min, int max, double value, double defaultValue, int y)
        {
            Label label = new Label();
            label.Text = caption;
            label.Location = new Point(14, y);
            label.Size = new Size(80, 22);
            label.TextAlign = ContentAlignment.MiddleLeft;
            parent.Controls.Add(label);

            TrackBar track = new TrackBar();
            track.Minimum = min;
            track.Maximum = max;
            track.Value = Math.Max(min, Math.Min(max, (int)Math.Round(value * 100.0)));
            track.TickStyle = TickStyle.None;
            track.SmallChange = 1;
            track.LargeChange = 10;
            track.Location = new Point(92, y - 5);
            track.Size = new Size(120, 35);
            parent.Controls.Add(track);

            NumericUpDown number = new NumericUpDown();
            number.DecimalPlaces = 2;
            number.Increment = 0.01M;
            number.Minimum = (decimal)min / 100M;
            number.Maximum = (decimal)max / 100M;
            number.Value = (decimal)track.Value / 100M;
            number.Location = new Point(212, y);
            number.Size = new Size(58, 24);
            parent.Controls.Add(number);

            AdjustmentControl item = new AdjustmentControl();
            item.Track = track;
            item.Number = number;
            item.DefaultValue = (decimal)defaultValue;
            adjustments[key] = item;

            track.ValueChanged += delegate
            {
                if (syncing) return;
                syncing = true;
                try { number.Value = (decimal)track.Value / 100M; }
                finally { syncing = false; }
                RequestRender(false);
            };
            number.ValueChanged += delegate
            {
                if (syncing) return;
                syncing = true;
                try
                {
                    int next = (int)Math.Round((double)number.Value * 100.0);
                    track.Value = Math.Max(track.Minimum, Math.Min(track.Maximum, next));
                }
                finally { syncing = false; }
                RequestRender(false);
            };

            long lastDown = 0;
            int lastX = -1000;
            int lastY = -1000;
            track.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                long now = DateTime.UtcNow.Ticks;
                int elapsed = lastDown == 0 ? int.MaxValue : (int)((now - lastDown) / TimeSpan.TicksPerMillisecond);
                bool near = Math.Abs(e.X - lastX) <= SystemInformation.DoubleClickSize.Width && Math.Abs(e.Y - lastY) <= SystemInformation.DoubleClickSize.Height;
                if (elapsed <= SystemInformation.DoubleClickTime && near)
                {
                    lastDown = 0;
                    BeginInvoke((MethodInvoker)delegate { number.Value = item.DefaultValue; });
                }
                else
                {
                    lastDown = now;
                    lastX = e.X;
                    lastY = e.Y;
                }
            };
        }

        private void UpdateLutUi()
        {
            int[] values = new int[] { 25, 50, 75, 100 };
            lutStrength = values[Math.Max(0, Math.Min(3, lutStrengthTrack.Value))];
            lutStrengthLabel.Text = lutStrength.ToString(CultureInfo.InvariantCulture) + "%";
            bool available = !string.IsNullOrEmpty(activeCompatLutPath) && File.Exists(activeCompatLutPath);
            checkLut.Enabled = available;
            if (!available && checkLut.Checked) checkLut.Checked = false;
            lutStrengthTrack.Enabled = available && checkLut.Checked;
            lutStrengthTitle.Enabled = lutStrengthTrack.Enabled;
            lutStrengthLabel.Enabled = lutStrengthTrack.Enabled;
            lutLabel.Text = available ? Path.GetFileName(lutPath) : S("未选择 LUT", "No LUT selected");
        }

        private void UpdateTimeLabel()
        {
            TimeSpan pos = TimeSpan.FromSeconds(CurrentTime());
            TimeSpan total = TimeSpan.FromSeconds(duration);
            timeLabel.Text = pos.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture) + " / " + total.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
        }

        private double CurrentTime()
        {
            return duration * ((double)timelineValue / 10000.0);
        }

        private void SetTimelineValue(int value, bool immediate)
        {
            timelineValue = Math.Max(0, Math.Min(10000, value));
            timelinePanel.Invalidate();
            UpdateTimeLabel();
            RequestRender(immediate);
        }

        private void TimelinePaint(object sender, PaintEventArgs e)
        {
            int left = 9;
            int right = Math.Max(left + 1, timelinePanel.ClientSize.Width - 10);
            int y = timelinePanel.ClientSize.Height / 2;
            int thumbX = left + (int)Math.Round((right - left) * ((double)timelineValue / 10000.0));
            using (Pen rail = new Pen(Color.FromArgb(175, 180, 188), 4F))
            using (Pen fill = new Pen(Color.FromArgb(35, 120, 220), 4F))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(35, 120, 220)))
            {
                rail.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                rail.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                fill.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                fill.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.DrawLine(rail, left, y, right, y);
                e.Graphics.DrawLine(fill, left, y, thumbX, y);
                e.Graphics.FillEllipse(brush, thumbX - 7, y - 7, 14, 14);
            }
        }

        private void TimelineMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            long now = DateTime.UtcNow.Ticks;
            int elapsed = timelineLastClickTicks == 0 ? int.MaxValue : (int)((now - timelineLastClickTicks) / TimeSpan.TicksPerMillisecond);
            bool isDouble = elapsed <= SystemInformation.DoubleClickTime && Math.Abs(e.X - timelineLastClickX) <= SystemInformation.DoubleClickSize.Width;
            timelineLastClickTicks = now;
            timelineLastClickX = e.X;

            int left = 9;
            int right = Math.Max(left + 1, timelinePanel.ClientSize.Width - 10);
            int target = Math.Max(0, Math.Min(10000, (int)Math.Round(((e.X - left) / (double)(right - left)) * 10000.0)));
            int thumbX = left + (int)Math.Round((right - left) * ((double)timelineValue / 10000.0));
            int frameStep = Math.Max(1, (int)Math.Round(10000.0 / (duration * fps)));

            if (isDouble)
            {
                timelineDragging = false;
                timelineLastClickTicks = 0;
                SetTimelineValue(target, true);
            }
            else if (Math.Abs(e.X - thumbX) <= 10)
            {
                timelineDragging = true;
            }
            else if (target > timelineValue) SetTimelineValue(timelineValue + frameStep, true);
            else if (target < timelineValue) SetTimelineValue(timelineValue - frameStep, true);
        }

        private void TimelineMouseMove(object sender, MouseEventArgs e)
        {
            if (!timelineDragging || e.Button != MouseButtons.Left) return;
            int left = 9;
            int right = Math.Max(left + 1, timelinePanel.ClientSize.Width - 10);
            int target = Math.Max(0, Math.Min(10000, (int)Math.Round(((e.X - left) / (double)(right - left)) * 10000.0)));
            SetTimelineValue(target, false);
        }

        private void TimelineMouseUp(object sender, MouseEventArgs e)
        {
            if (!timelineDragging) return;
            timelineDragging = false;
            RequestRender(true);
        }

        private void TimelineMouseLeave(object sender, EventArgs e)
        {
            if (Control.MouseButtons == MouseButtons.None) timelineDragging = false;
        }

        private void RequestRender(bool immediate)
        {
            if (closing || IsDisposed) return;
            renderSerial++;
            renderPending = true;
            renderDue = false;
            renderTimer.Stop();
            renderTimer.Interval = immediate ? 1 : 90;
            renderTimer.Start();

            // Do not synchronously kill FFmpeg while the user is dragging controls.
            // For an explicit final action (mouse-up / click / original view), cancel
            // a stale render on a worker thread so the UI thread always stays responsive.
            if (immediate && renderWorkerRunning)
            {
                ThreadPool.QueueUserWorkItem(delegate { CancelActiveRender(); });
            }
        }

        private void StartRenderIfReady()
        {
            if (closing || renderWorkerRunning || !renderPending || !renderDue) return;
            renderPending = false;
            renderDue = false;
            renderWorkerRunning = true;
            RenderState state = CaptureRenderState();
            status.Text = S("正在生成预览...", "Rendering preview...");

            Thread worker = new Thread(delegate()
            {
                Bitmap image = null;
                string error = "";
                Stopwatch watch = Stopwatch.StartNew();
                try { image = RenderPreview(state); }
                catch (Exception ex) { error = ex.Message; }
                watch.Stop();
                long elapsedMs = watch.ElapsedMilliseconds;

                if (closing || IsDisposed)
                {
                    if (image != null) image.Dispose();
                    return;
                }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        try
                        {
                            renderWorkerRunning = false;
                            if (closing || IsDisposed)
                            {
                                if (image != null) image.Dispose();
                                return;
                            }

                            if (state.Serial == renderSerial && image != null)
                            {
                                Image oldImage = pic.Image;
                                pic.Image = image;
                                if (oldImage != null) oldImage.Dispose();
                                image = null;
                                status.Text = S("预览已更新 · ", "Preview updated · ") + elapsedMs.ToString(CultureInfo.InvariantCulture) + " ms";
                            }
                            else if (state.Serial == renderSerial && !string.IsNullOrEmpty(error))
                            {
                                status.Text = S("预览失败：", "Preview failed: ") + error;
                            }

                            if (image != null) image.Dispose();

                            // If the debounce timer already elapsed while FFmpeg was busy,
                            // render the newest state immediately. Otherwise the active timer
                            // keeps the normal debounce delay and coalesces intermediate input.
                            if (renderPending && renderDue) StartRenderIfReady();
                        }
                        catch
                        {
                            renderWorkerRunning = false;
                            if (image != null) image.Dispose();
                        }
                    });
                }
                catch
                {
                    if (image != null) image.Dispose();
                }
            });
            worker.IsBackground = true;
            worker.Name = "FGS Native Color Preview";
            worker.Start();
        }

        private RenderState CaptureRenderState()
        {
            RenderState state = new RenderState();
            state.Serial = renderSerial;
            state.Position = CurrentTime();
            state.Original = originalHeld;
            state.Enabled = checkEnable.Checked;
            state.Contrast = (double)adjustments["Contrast"].Number.Value;
            state.Brightness = (double)adjustments["Brightness"].Number.Value;
            state.Saturation = (double)adjustments["Saturation"].Number.Value;
            state.Gamma = (double)adjustments["Gamma"].Number.Value;
            state.BlackWhite = checkBlackWhite.Checked;
            state.UseLut = checkLut.Checked && !string.IsNullOrEmpty(activeCompatLutPath);
            state.CompatLutPath = activeCompatLutPath;
            state.LutStrength = lutStrength;
            return state;
        }

        private Bitmap RenderPreview(RenderState state)
        {
            string output = Path.Combine(tempRoot, "preview_" + state.Serial.ToString(CultureInfo.InvariantCulture) + ".jpg");
            try { if (File.Exists(output)) File.Delete(output); } catch { }

            List<string> prefix = new List<string>();
            prefix.Add("scale=" + previewWidth.ToString(CultureInfo.InvariantCulture) + ":" + previewHeight.ToString(CultureInfo.InvariantCulture) + ":force_original_aspect_ratio=decrease:flags=bilinear");
            if (!state.Original && state.Enabled)
            {
                prefix.Add("eq=contrast=" + F(state.Contrast) + ":brightness=" + F(state.Brightness) + ":saturation=" + F(state.Saturation) + ":gamma=" + F(state.Gamma));
                if (state.BlackWhite) prefix.Add("hue=s=0");
            }

            string filter = string.Join(",", prefix.ToArray());
            if (!state.Original && state.UseLut && !string.IsNullOrEmpty(state.CompatLutPath))
            {
                string escaped = EscapeFilterPath(state.CompatLutPath);
                if (state.LutStrength >= 100)
                    filter += ",format=gbrp16le,lut3d=file='" + escaped + "':interp=tetrahedral";
                else
                {
                    string opacity = ((double)state.LutStrength / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
                    filter += ",format=gbrp16le,split=2[lutorig][lutsrc];[lutsrc]lut3d=file='" + escaped + "':interp=tetrahedral[lutgraded];[lutgraded][lutorig]blend=all_mode=normal:all_opacity=" + opacity;
                }
            }
            filter += ",pad=" + previewWidth.ToString(CultureInfo.InvariantCulture) + ":" + previewHeight.ToString(CultureInfo.InvariantCulture) + ":(ow-iw)/2:(oh-ih)/2:black";

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = ffmpegPath;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.Arguments = "-hide_banner -loglevel error -y -ss " + state.Position.ToString("0.000", CultureInfo.InvariantCulture) +
                            " -i " + QuoteArg(videoPath) + " -an -sn -dn -frames:v 1 -vf " + QuoteArg(filter) + " -q:v 2 " + QuoteArg(output);

            string error;
            int rc;
            using (Process process = new Process())
            {
                process.StartInfo = psi;
                lock (processLock) activeRenderProcess = process;
                try
                {
                    process.Start();
                    error = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    rc = process.ExitCode;
                }
                finally
                {
                    lock (processLock)
                    {
                        if (object.ReferenceEquals(activeRenderProcess, process)) activeRenderProcess = null;
                    }
                }
            }

            if (rc != 0 || !File.Exists(output))
            {
                string detail = (error ?? "").Trim();
                if (detail.Length > 1200) detail = detail.Substring(detail.Length - 1200);
                throw new InvalidOperationException(string.IsNullOrEmpty(detail) ? "FFmpeg preview failed." : detail);
            }

            byte[] bytes = File.ReadAllBytes(output);
            try { File.Delete(output); } catch { }
            using (MemoryStream stream = new MemoryStream(bytes))
            using (Image source = Image.FromStream(stream))
            {
                return new Bitmap(source);
            }
        }

        private void CancelActiveRender()
        {
            lock (processLock)
            {
                if (activeRenderProcess == null) return;
                try { if (!activeRenderProcess.HasExited) activeRenderProcess.Kill(); } catch { }
            }
        }

        private void OpenLutGalleryAsync()
        {
            if (!File.Exists(lutSelectorPath))
            {
                MessageBox.Show(this, S("找不到 LUT 图库：\r\n", "LUT Gallery not found:\r\n") + lutSelectorPath, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!Directory.Exists(lutRoot))
            {
                MessageBox.Show(this, S("找不到 LUT 根目录：\r\n", "LUT root not found:\r\n") + lutRoot, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string pick = Path.Combine(Path.GetTempPath(), "FilmGrainStudio_NET_Color_LUT_" + Guid.NewGuid().ToString("N") + ".txt");
            openLutGallery.Enabled = false;
            Thread worker = new Thread(delegate()
            {
                int rc = -1;
                string error = "";
                Exception failure = null;
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = "powershell.exe";
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardError = true;
                    psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -STA -File " + QuoteArg(lutSelectorPath) +
                                    " -LutRoot " + QuoteArg(lutRoot) +
                                    " -PreviewRoot " + QuoteArg(lutPreviewRoot) +
                                    " -OutputFile " + QuoteArg(pick);
                    using (Process process = Process.Start(psi))
                    {
                        error = process.StandardError.ReadToEnd();
                        process.WaitForExit();
                        rc = process.ExitCode;
                    }
                }
                catch (Exception ex) { failure = ex; }

                if (closing || IsDisposed)
                {
                    try { if (File.Exists(pick)) File.Delete(pick); } catch { }
                    return;
                }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        try
                        {
                            if (failure != null) throw failure;
                            CancelActiveRender();
                            if (rc == 0 && File.Exists(pick))
                            {
                                string selected = File.ReadAllText(pick, Encoding.UTF8).Trim();
                                if (!string.IsNullOrEmpty(selected))
                                {
                                    PrepareLut(selected);
                                    lutSource = "Gallery";
                                    syncing = true;
                                    try { checkLut.Checked = !string.IsNullOrEmpty(activeCompatLutPath); }
                                    finally { syncing = false; }
                                }
                            }
                            else if (rc == 10)
                            {
                                PrepareLut("");
                                lutSource = "None";
                                syncing = true;
                                try { checkLut.Checked = false; }
                                finally { syncing = false; }
                            }
                            else if (rc != 11)
                            {
                                string detail = (error ?? "").Trim();
                                if (detail.Length > 2000) detail = detail.Substring(0, 2000);
                                MessageBox.Show(this, S("LUT 图库返回错误 ", "LUT Gallery returned error ") + rc.ToString(CultureInfo.InvariantCulture) + ".\r\n" + detail, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                            UpdateLutUi();
                            RequestRender(true);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        finally
                        {
                            openLutGallery.Enabled = true;
                            try { if (File.Exists(pick)) File.Delete(pick); } catch { }
                        }
                    });
                }
                catch { try { if (File.Exists(pick)) File.Delete(pick); } catch { } }
            });
            worker.IsBackground = true;
            worker.Name = "FGS LUT Gallery";
            worker.Start();
        }
    }

}
