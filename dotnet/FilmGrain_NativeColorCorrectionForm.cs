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
using System.Windows.Forms.Integration;
using System.Runtime.InteropServices;
using WpfCanvas = System.Windows.Controls.Canvas;
using WpfImage = System.Windows.Controls.Image;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfStretch = System.Windows.Media.Stretch;
using WpfRenderCapability = System.Windows.Media.RenderCapability;
using WpfBitmapSource = System.Windows.Media.Imaging.BitmapSource;
using WpfImaging = System.Windows.Interop.Imaging;
using WpfKeyboard = System.Windows.Input.Keyboard;
using WpfKey = System.Windows.Input.Key;
using WpfBitmapSizeOptions = System.Windows.Media.Imaging.BitmapSizeOptions;
using WpfMouseButtonState = System.Windows.Input.MouseButtonState;
using WpfCursors = System.Windows.Input.Cursors;
using WpfRenderOptions = System.Windows.Media.RenderOptions;
using WpfBitmapScalingMode = System.Windows.Media.BitmapScalingMode;

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
        public byte[] VideoReference;
        public FilmLutSettings FilmSettings;
    }

    internal sealed class NativeColorCorrectionForm : Form, IThemeSurface
    {
        public bool DarkTheme { get; set; }
        protected override void OnLoad(EventArgs e) { MainForm.AttachOwnedTheme(this, Owner); base.OnLoad(e); }
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
            public bool Zoom;
            public int PanX;
            public int PanY;
            public bool Enabled;
            public double Contrast;
            public double Brightness;
            public double Saturation;
            public double Gamma;
            public bool BlackWhite;
            public bool UseLut;
            public string CompatLutPath;
            public int LutStrength;
            public FilmLutSettings FilmSettings;
        }

        // Captures both key-down and key-up before focused WinForms controls
        // consume Space as a button click or a checkbox toggle.
        private sealed class OriginalSpaceFilter : IMessageFilter
        {
            private readonly NativeColorCorrectionForm owner;
            internal OriginalSpaceFilter(NativeColorCorrectionForm owner) { this.owner=owner; }
            public bool PreFilterMessage(ref Message message)
            {
                if(message.Msg!=0x0100 && message.Msg!=0x0101)return false;
                if(message.WParam.ToInt64()!=(long)Keys.Space)return false;
                if(owner.closing||owner.IsDisposed||(!owner.ContainsFocus&&Form.ActiveForm!=owner))return false;
                owner.spaceOriginalHeld=message.Msg==0x0100;
                owner.UpdateOriginalHold();
                return true;
            }
        }

        private readonly MediaProbeInfo hdrPreviewInfo;
        private readonly bool preserveHdrColor;
        private readonly string previewTonemap;
        private readonly string videoPath;
        private MediaProbeIdentity sourceIdentity;
        private readonly string ffmpegPath;
        private readonly string ffprobePath;
        private readonly string galleryAppRoot;
        private readonly string lutRoot;
        private readonly string lutPreviewRoot;
        private readonly bool isZh;
        private readonly FilmLutSettings initialFilmSettings;
        private readonly string tempRoot;
        private readonly string compatLutPath;
        private readonly int previewWidth;
        private readonly int previewHeight;
        private readonly object processLock = new object();

        private PictureBox pic;
        private ElementHost gpuView;
        private WpfCanvas gpuCanvas;
        private WpfImage gpuImage;
        private WpfBitmapSource normalGpuSource,zoomGpuSource,originalGpuSource;
        private Bitmap zoomFullImage,zoomOriginalImage;
        private double zoomFullPosition=double.NaN,zoomOriginalPosition=double.NaN;
        private bool zoomGradedDirty=true;
        private bool gpuReady;
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr handle);
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
        private GroupBox adjustmentGroup;
        private Panel filmPanel;
        private Button filmTabButton,colorTabButton;
        private CheckBox checkFilm;
        private ComboBox filmStock;
        private readonly TrackBar[] filmTracks=new TrackBar[3];
        private readonly Label[] filmAmounts=new Label[3];
        private readonly List<Control> colorPageControls=new List<Control>();
        private readonly Dictionary<string,string> filmPreviewCubes=new Dictionary<string,string>(StringComparer.Ordinal);
        private bool filmUiSync,filmModeActive;
        private readonly Dictionary<string, AdjustmentControl> adjustments = new Dictionary<string, AdjustmentControl>(StringComparer.OrdinalIgnoreCase);
        private System.Windows.Forms.Timer renderTimer;

        private double duration = 1.0;
        private double fps = 24.0;
        private int sourceWidth,sourceHeight;
        private int timelineValue = 1000;
        private bool timelineDragging;
        private long timelineLastClickTicks;
        private int timelineLastClickX = -1000;
        private bool syncing;
        private bool originalHeld;
        private bool spaceOriginalHeld,mouseOriginalHeld;
        private bool zoomPreview,panDragging;
        private Point panDragStart;
        private int panX,panY;
        private readonly OriginalSpaceFilter originalSpaceFilter;
        private bool renderWorkerRunning;
        private bool renderPending;
        private bool renderDue;
        private int renderSerial;
        private Process activeRenderProcess;
        private bool closing;
        // One clean reference per selected time; parameter drags reuse it.
        // Only the rendering worker writes this cache. Publish after a current render succeeds.
        private byte[] cachedVideoReference, displayedVideoReference;
        private double cachedReferencePosition = double.NaN;
        private string lutPath;
        private string lutSource;
        private string activeCompatLutPath = "";
        private int lutStrength;

        public ColorCorrectionResult Result { get; private set; }

        public NativeColorCorrectionForm(
            string videoPath,
            string ffmpegPath,
            string ffprobePath,
            string galleryAppRoot,
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
            string languageCode, MediaProbeInfo mediaInfo = null, bool preserveHdr = false, string tonemap = "hable",FilmLutSettings filmSettings=null)
        {
            this.videoPath = videoPath;
            hdrPreviewInfo=mediaInfo!=null&&mediaInfo.IsHdr?mediaInfo:null;
            preserveHdrColor=preserveHdr;previewTonemap=tonemap;
            this.ffmpegPath = ffmpegPath;
            this.ffprobePath = ffprobePath;
            this.galleryAppRoot = galleryAppRoot;
            this.lutRoot = lutRoot;
            this.lutPreviewRoot = lutPreviewRoot;
            this.lutPath = lutPath ?? "";
            this.lutSource = string.IsNullOrEmpty(lutSource) ? "None" : lutSource;
            this.lutStrength = NormalizeStrength(lutStrength);
            this.isZh = !string.Equals(languageCode, "en-US", StringComparison.OrdinalIgnoreCase);
            originalSpaceFilter=new OriginalSpaceFilter(this);
            initialFilmSettings=filmSettings==null?null:filmSettings.Copy();
            this.previewWidth = largeUi ? 960 : 720;
            this.previewHeight = largeUi ? 540 : 480;
            tempRoot = Path.Combine(Path.GetTempPath(), "FGS_NET_ColorPreview_" + Guid.NewGuid().ToString("N"));
            compatLutPath = Path.Combine(tempRoot, "preview_lut.cube");
            Directory.CreateDirectory(tempRoot);

            sourceIdentity=MediaProbeIdentity.Capture(videoPath,ffprobePath);
            ProbeVideo();
            sourceIdentity.RequireCurrent();
            PrepareLut(this.lutPath);
            BuildUi(largeUi, enabled, contrast, brightness, saturation, gamma, blackWhite, useLut);
            if(initialFilmSettings!=null) {
                SetFilmSettings(initialFilmSettings);
                SwitchAdjustmentPage(true);
            }
            if(hdrPreviewInfo!=null)Text+=S(" · SDR显示预览（非HDR亮度校准）"," · SDR display preview (not HDR luminance calibration)");
        }

        private string S(string zh, string en)
        {
            return isZh ? zh : en;
        }

        private static int NormalizeStrength(int value)
        {
            return Math.Max(0, Math.Min(100, value));
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

            string[] dimensions=ProbeText("-v error -select_streams v:0 -show_entries stream=width,height -of csv=s=x:p=0 " + QuoteArg(videoPath)).Split('x');
            if(dimensions.Length==2)
            {
                int w,h;
                if(int.TryParse(dimensions[0],out w)&&int.TryParse(dimensions[1],out h)&&w>0&&h>0)
                {sourceWidth=w;sourceHeight=h;}
            }

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
            Text = S("LUT 工坊与色彩纠正", "LUT Studio & Color Correction");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(245, 246, 248);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            int dialogWidth = largeUi ? 1280 : 1040;
            // The enlarged frame also needs room for its LUT slider and status.
            int dialogHeight = largeUi ? 770 : 690;
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
            pic.MouseDoubleClick += PictureMouseDoubleClick;
            pic.MouseDown += PictureMouseDown;
            pic.MouseMove += PictureMouseMove;
            pic.MouseUp += PictureMouseUp;
            pic.MouseCaptureChanged += delegate {if(!pic.Capture)panDragging=false;};
            pic.Paint += PicturePaint;
            new ToolTip().SetToolTip(pic,S("双击切换 1:1 原像素预览；1:1 时按住左键拖动画面",
                "Double-click for 1:1 native pixels; drag to pan at 1:1"));
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

            timeLabel = new ThemeLabel();
            timeLabel.Location = new Point(14, timeLabelY);
            timeLabel.Size = new Size(previewWidth, 24);
            timeLabel.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(timeLabel);

            colorTabButton=new ThemeButton{Text=S("色彩纠正","Color correction"),Location=new Point(rightX,14),Size=new Size(135,29)};
            filmTabButton=new ThemeButton{Text=S("经典胶片","Classic film"),Location=new Point(rightX+143,14),Size=new Size(135,29)};
            Controls.Add(colorTabButton);Controls.Add(filmTabButton);
            colorTabButton.Click+=delegate{SwitchAdjustmentPage(false);};
            filmTabButton.Click+=delegate{
                SwitchAdjustmentPage(true);
                if(!filmModeActive){filmModeActive=true;checkFilm.Checked=true;checkLut.Checked=true;lutStrengthTrack.Value=100;UpdateLutUi();RequestRender(true);}
            };

            GroupBox group = new ThemeGroupBox();
            adjustmentGroup=group;
            group.Text = S("调整参数", "Adjustments");
            group.Location = new Point(rightX, 51);
            group.Size = new Size(278, 443);
            Controls.Add(group);

            checkEnable = new ThemeCheckBox();
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

            checkBlackWhite = new ThemeCheckBox();
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

            Button reset = new ThemeButton();
            reset.Text = S("恢复默认", "Reset");
            reset.Location = new Point(18, 401);
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

            foreach(Control colorControl in group.Controls)colorPageControls.Add(colorControl);
            BuildFilmPanel(group);
            SwitchAdjustmentPage(false);

            // Shared with both adjustment pages; keyboard and mouse holds combine.
            originalButton = new ThemeButton();
            originalButton.Text = S("按住查看原图（空格）", "Hold for original (Space)");
            originalButton.Location = new Point(rightX+44, group.Bottom+8);
            originalButton.Size = new Size(190, 32);
            originalButton.MouseDown += delegate(object sender, MouseEventArgs e)
            { if(e.Button==MouseButtons.Left){mouseOriginalHeld=true;UpdateOriginalHold();} };
            originalButton.MouseUp += delegate(object sender, MouseEventArgs e)
            { if(e.Button==MouseButtons.Left){mouseOriginalHeld=false;UpdateOriginalHold();} };
            originalButton.MouseLeave += delegate
            { if(Control.MouseButtons!=MouseButtons.Left){mouseOriginalHeld=false;UpdateOriginalHold();} };
            originalButton.MouseCaptureChanged += delegate
            { if(Control.MouseButtons!=MouseButtons.Left){mouseOriginalHeld=false;UpdateOriginalHold();} };
            Controls.Add(originalButton);

            openLutGallery = new ThemeButton();
            openLutGallery.Text = S("打开 LUT 图库…", "Open LUT Gallery…");
            openLutGallery.Location = new Point(14, lutRowY);
            openLutGallery.Size = new Size(120, 26);
            openLutGallery.Click += delegate { OpenLutGalleryAsync(); };
            Controls.Add(openLutGallery);

            checkLut = new ThemeCheckBox();
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

            lutLabel = new ThemeLabel();
            lutLabel.Location = new Point(286, lutRowY);
            lutLabel.Size = new Size(previewWidth - 272, 24);
            lutLabel.AutoEllipsis = true;
            lutLabel.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(lutLabel);

            int strengthY = lutRowY + 25;
            lutStrengthTitle = new ThemeLabel();
            lutStrengthTitle.Text = S("LUT 强度", "LUT Strength");
            int strengthLabelWidth=isZh?66:100;
            lutStrengthTitle.Location = new Point(14, strengthY+2);
            lutStrengthTitle.Size = new Size(strengthLabelWidth, 27);
            lutStrengthTitle.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(lutStrengthTitle);

            lutStrengthTrack = new ThemeTrackBar();
            lutStrengthTrack.Minimum = 0;
            lutStrengthTrack.Maximum = 100;
            lutStrengthTrack.SmallChange = 1;
            lutStrengthTrack.LargeChange = 5;
            lutStrengthTrack.Value = lutStrength;
            lutStrengthTrack.AutoSize = false;
            lutStrengthTrack.TickStyle = TickStyle.None;
            lutStrengthTrack.Location = new Point(strengthLabelWidth+14, strengthY+2);
            lutStrengthTrack.Size = new Size(previewWidth-strengthLabelWidth-86, 28);
            lutStrengthTrack.ValueChanged += delegate { UpdateLutUi(); RequestRender(false); };
            Controls.Add(lutStrengthTrack);

            lutStrengthLabel = new ThemeLabel();
            lutStrengthLabel.Location = new Point(previewWidth - 56, strengthY+2);
            lutStrengthLabel.Size = new Size(68, 27);
            lutStrengthLabel.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(lutStrengthLabel);

            status = new ThemeLabel();
            status.Location = new Point(14, dialogHeight-35);
            status.Size = new Size(previewWidth, 27);
            status.AutoEllipsis = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            var statusTip=new ToolTip();
            status.TextChanged+=delegate{statusTip.SetToolTip(status,status.Text);};
            Controls.Add(status);

            Button ok = new ThemeButton();
            ok.Text = S("应用", "Apply");
            ok.Location = new Point(dialogWidth - 214, buttonY);
            ok.Size = new Size(96, 32);
            ok.Click += delegate
            {
                string appliedLut=lutPath;
                FilmLutSettings filmSettings=null;
                if(filmModeActive && checkLut.Checked) {
                    try {
                        Cursor=Cursors.WaitCursor;
                        filmSettings=CaptureFilmSettings();
                        appliedLut=FilmLutCore.WriteSessionCube(galleryAppRoot,filmSettings);
                    }
                    catch(Exception ex){FgsMessageBox.Show(this,S("胶片 LUT 应用失败：","Film LUT apply failed: ")+ex.Message);return;}
                    finally{Cursor=Cursors.Default;}
                }
                Result = new ColorCorrectionResult();
                Result.Enabled = checkEnable.Checked;
                Result.Contrast = (double)adjustments["Contrast"].Number.Value;
                Result.Brightness = (double)adjustments["Brightness"].Number.Value;
                Result.Saturation = (double)adjustments["Saturation"].Number.Value;
                Result.Gamma = (double)adjustments["Gamma"].Number.Value;
                Result.BlackWhite = checkBlackWhite.Checked;
                Result.UseLut = checkLut.Checked;
                Result.LutPath = appliedLut;
                Result.LutSource = filmSettings!=null?"FilmLut":(string.IsNullOrEmpty(appliedLut)?"None":lutSource);
                Result.LutStrength = lutStrength;
                Result.VideoReference = displayedVideoReference;
                Result.FilmSettings=filmSettings;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(ok);

            Button cancel = new ThemeButton();
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
            Shown += delegate {Application.AddMessageFilter(originalSpaceFilter);InitializeGpuView();RequestRender(true);};
            Deactivate += delegate {spaceOriginalHeld=false;mouseOriginalHeld=false;UpdateOriginalHold();};
            FormClosing += delegate { closing = true; CancelActiveRender(); };
            FormClosed += delegate
            {
                Application.RemoveMessageFilter(originalSpaceFilter);
                try { renderTimer.Stop(); renderTimer.Dispose(); } catch { }
                Image old = pic.Image; pic.Image = null; if (old != null) old.Dispose();
                if(zoomFullImage!=null){zoomFullImage.Dispose();zoomFullImage=null;}
                if(zoomOriginalImage!=null){zoomOriginalImage.Dispose();zoomOriginalImage=null;}
                normalGpuSource=zoomGpuSource=originalGpuSource=null;
                if(gpuImage!=null)gpuImage.Source=null;
                if(gpuCanvas!=null)gpuCanvas.Children.Clear();
                if(gpuView!=null){gpuView.Child=null;gpuView.Dispose();gpuView=null;}
                gpuImage=null;gpuCanvas=null;
                try { Directory.Delete(tempRoot, true); } catch { }
            };
        }

        private void AddAdjustment(Control parent, string key, string caption, int min, int max, double value, double defaultValue, int y)
        {
            Label label = new ThemeLabel();
            label.Text = caption;
            label.Location = new Point(14, y);
            label.Size = new Size(80, 22);
            label.TextAlign = ContentAlignment.MiddleLeft;
            parent.Controls.Add(label);

            TrackBar track = new ThemeTrackBar();
            track.Minimum = min;
            track.Maximum = max;
            track.Value = Math.Max(min, Math.Min(max, (int)Math.Round(value * 100.0)));
            track.TickStyle = TickStyle.None;
            track.SmallChange = 1;
            track.LargeChange = 10;
            track.Location = new Point(92, y - 5);
            track.Size = new Size(120, 35);
            parent.Controls.Add(track);

            NumericUpDown number = new ThemeNumericUpDown();
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

        private void BuildFilmPanel(Control parent)
        {
            filmPanel=new Panel{Location=new Point(8,27),Size=new Size(262,405)};
            parent.Controls.Add(filmPanel);
            filmPanel.Controls.Add(new ThemeLabel{Text=S("胶片组合","Film combination"),Location=new Point(7,2),Size=new Size(248,23)});
            filmStock=new ThemeComboBox{Location=new Point(7,27),Size=new Size(248,26),DropDownStyle=ComboBoxStyle.DropDownList};
            foreach(var profile in FilmLutCore.Profiles)filmStock.Items.Add(isZh?profile.Chinese:profile.English);
            filmPanel.Controls.Add(filmStock);
            checkFilm=new ThemeCheckBox{Text=S("启用经典胶片 LUT","Enable classic film LUT"),Location=new Point(7,63),Size=new Size(246,26)};
            filmPanel.Controls.Add(checkFilm);
            filmPanel.Controls.Add(new ThemeLabel{Text=S("真实光谱基准 · 下方原有滑杆调整 LUT 强度","Spectral film base · LUT strength below preview"),
                Location=new Point(7,94),Size=new Size(248,29)});
            AddFilmAdjustment(0,S("曝光","Exposure"),-10,10,0,126);
            AddFilmAdjustment(1,S("冷暖","Warmth"),-40,40,0,202);
            AddFilmAdjustment(2,S("饱和度","Saturation"),70,130,100,278);
            var reset=new ThemeButton{Text=S("恢复默认","Reset"),Location=new Point(5,357),Size=new Size(76,31)};
            var load=new ThemeButton{Text=S("载入配方","Load recipe"),Location=new Point(88,357),Size=new Size(80,31)};
            var save=new ThemeButton{Text=S("保存 LUT…","Save LUT…"),Location=new Point(175,357),Size=new Size(80,31)};
            filmPanel.Controls.Add(reset);filmPanel.Controls.Add(load);filmPanel.Controls.Add(save);
            filmStock.SelectedIndexChanged+=delegate{if(!filmUiSync&&filmStock.SelectedIndex>=0){filmModeActive=true;checkFilm.Checked=true;checkLut.Checked=true;RequestRender(false);}};
            checkFilm.CheckedChanged+=delegate{if(!filmUiSync){filmModeActive=checkFilm.Checked;if(filmModeActive)checkLut.Checked=true;UpdateLutUi();RequestRender(false);}};
            reset.Click+=delegate{SetFilmSettings(FilmLutSettings.Default(FilmLutCore.Profiles[filmStock.SelectedIndex].Id));};
            load.Click+=delegate{LoadFilmRecipe();};
            save.Click+=delegate{SaveFilmLut();};
            filmUiSync=true;
            filmStock.SelectedIndex=1;
            filmUiSync=false;
        }

        private void AddFilmAdjustment(int index,string caption,int min,int max,int value,int y)
        {
            var title=new ThemeLabel{Text=caption,Location=new Point(7,y),Size=new Size(161,22)};
            var amount=new ThemeLabel{Location=new Point(177,y),Size=new Size(76,22),TextAlign=ContentAlignment.MiddleRight};
            // Native TrackBar.AutoSize otherwise silently increases its height
            // and covers the following label (three labels were clipped in R7Q4VX).
            var slider=new ThemeTrackBar{AutoSize=false,Minimum=min,Maximum=max,Value=value,
                TickStyle=TickStyle.None,SmallChange=1,LargeChange=5,
                Location=new Point(5,y+26),Size=new Size(250,29)};
            filmTracks[index]=slider;filmAmounts[index]=amount;
            filmPanel.Controls.Add(title);filmPanel.Controls.Add(amount);filmPanel.Controls.Add(slider);
            slider.ValueChanged+=delegate{UpdateFilmLabels();if(!filmUiSync){filmModeActive=true;checkFilm.Checked=true;RequestRender(false);}};
            UpdateFilmLabels();
        }

        private void UpdateFilmLabels()
        {
            for(int i=0;i<3;i++) {
                if(filmTracks[i]==null||filmAmounts[i]==null)continue;
                int n=filmTracks[i].Value;
                filmAmounts[i].Text=i==0?(n/10.0).ToString("+0.0;-0.0;0.0",CultureInfo.CurrentCulture)+" EV":
                    i==1?(n>0?"+":"")+n.ToString(CultureInfo.CurrentCulture):n.ToString(CultureInfo.CurrentCulture)+"%";
            }
        }

        private void SwitchAdjustmentPage(bool showFilm)
        {
            foreach(Control control in colorPageControls)control.Visible=!showFilm;
            if(filmPanel!=null)filmPanel.Visible=showFilm;
            if(adjustmentGroup!=null)adjustmentGroup.Text=showFilm?(preserveHdrColor ? S("经典胶片 LUT · HDR","Classic Film LUT · HDR") : S("经典胶片 LUT","Classic Film LUT")):S("色彩纠正","Color Correction");
            if(colorTabButton!=null)colorTabButton.Text=(showFilm?"":"● ")+S("色彩纠正","Color correction");
            if(filmTabButton!=null)filmTabButton.Text=(showFilm?"● ":"")+S("经典胶片","Classic film");
        }

        private FilmLutSettings CaptureFilmSettings()
        {
            if(filmStock==null||filmStock.SelectedIndex<0)throw new InvalidOperationException("No film stock selected.");
            var settings=new FilmLutSettings{ProfileId=FilmLutCore.Profiles[filmStock.SelectedIndex].Id,
                ExposureTenths=filmTracks[0].Value,Warmth=filmTracks[1].Value,
                Saturation=filmTracks[2].Value,Strength=lutStrengthTrack.Value};
            settings.Validate();return settings;
        }

        private void SetFilmSettings(FilmLutSettings settings)
        {
            settings.Validate();
            filmUiSync=true;
            try {
                for(int i=0;i<FilmLutCore.Profiles.Length;i++)if(FilmLutCore.Profiles[i].Id==settings.ProfileId){filmStock.SelectedIndex=i;break;}
                filmTracks[0].Value=settings.ExposureTenths;
                filmTracks[1].Value=settings.Warmth;
                filmTracks[2].Value=settings.Saturation;
                lutStrengthTrack.Value=settings.Strength;
                checkFilm.Checked=true;filmModeActive=true;
                checkLut.Checked=true;
            }finally{filmUiSync=false;}
            UpdateFilmLabels();SwitchAdjustmentPage(true);UpdateLutUi();RequestRender(true);
        }

        private void LoadFilmRecipe()
        {
            using(var dialog=new OpenFileDialog{Title=S("载入胶片配方","Load film recipe"),
                Filter="FGS Film LUT recipe (*.film.json)|*.film.json|JSON (*.json)|*.json",
                InitialDirectory=Path.Combine(lutRoot,"FGS_FilmLUT")}) {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                try {FilmLutSettings settings;FilmLutCore.ReadRecipe(dialog.FileName,out settings);SetFilmSettings(settings);}
                catch(Exception ex){FgsMessageBox.Show(this,S("配方读取失败：","Could not read recipe: ")+ex.Message);}
            }
        }

        private void SaveFilmLut()
        {
            FilmLutSettings settings;
            try{settings=CaptureFilmSettings();}catch(Exception ex){FgsMessageBox.Show(this,ex.Message);return;}
            string folder=Path.Combine(lutRoot,"FGS_FilmLUT");
            try{Directory.CreateDirectory(folder);}catch(Exception ex){FgsMessageBox.Show(this,ex.Message);return;}
            using(var dialog=new SaveFileDialog{Title=S("保存胶片 LUT（不自动应用）","Save film LUT (without applying)"),
                Filter="3D LUT (*.cube)|*.cube",DefaultExt="cube",AddExtension=true,OverwritePrompt=true,
                InitialDirectory=folder,FileName=FilmLutCore.NewCubeFileName(settings.ProfileId)}) {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                try {
                    Cursor=Cursors.WaitCursor;
                    FilmLutSettings full=settings.Copy();full.Strength=100;
                    FilmLutCore.WriteCube(dialog.FileName,FilmLutCube.Read(FilmLutCore.ProfilePath(galleryAppRoot,settings.ProfileId)),full);
                    FilmLutCore.WriteRecipe(Path.ChangeExtension(dialog.FileName,".film.json"),settings);
                    status.Text=S("已保存（未应用）：","Saved (not applied): ")+Path.GetFileName(dialog.FileName);
                }catch(Exception ex){FgsMessageBox.Show(this,S("LUT 保存失败：","Could not save LUT: ")+ex.Message);}
                finally{Cursor=Cursors.Default;}
            }
        }

        private void UpdateLutUi()
        {
            lutStrength = Math.Max(0,Math.Min(100,lutStrengthTrack.Value));
            lutStrengthLabel.Text = lutStrength.ToString(CultureInfo.InvariantCulture) + "%";
            bool available = filmModeActive || (!string.IsNullOrEmpty(activeCompatLutPath) && File.Exists(activeCompatLutPath));
            checkLut.Enabled = available;
            if (!available && checkLut.Checked) checkLut.Checked = false;
            lutStrengthTrack.Enabled = available && checkLut.Checked;
            lutStrengthTitle.Enabled = lutStrengthTrack.Enabled;
            lutStrengthLabel.Enabled = lutStrengthTrack.Enabled;
            lutLabel.Text = filmModeActive&&filmStock.SelectedIndex>=0?
                (isZh?FilmLutCore.Profiles[filmStock.SelectedIndex].Chinese:FilmLutCore.Profiles[filmStock.SelectedIndex].English):
                (available ? Path.GetFileName(lutPath) : S("未选择 LUT", "No LUT selected"));
        }

        private void UpdateTimeLabel()
        {
            TimeSpan pos = TimeSpan.FromSeconds(CurrentTime());
            TimeSpan total = TimeSpan.FromSeconds(duration);
            timeLabel.Text = pos.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture) + " / " + total.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture) +
                (zoomPreview?" · 1:1":"");
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
            using (Pen rail = new Pen(DarkTheme ? UiTheme.Border : Color.FromArgb(175, 180, 188), 4F))
            using (Pen fill = new Pen(DarkTheme ? UiTheme.Accent : Color.FromArgb(35, 120, 220), 4F))
            using (SolidBrush brush = new SolidBrush(DarkTheme ? UiTheme.Accent : Color.FromArgb(35, 120, 220)))
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

        // WinForms hosts the controls; WPF's D3D compositor owns only the preview
        // rectangle. Rendering remains the same FFmpeg filter graph as encoding.
        // The full-resolution frame is uploaded ONCE; pan only updates coordinates.
        private void InitializeGpuView()
        {
            if(gpuReady || closing)return;
            try
            {
                if(WpfRenderCapability.Tier<0x20000)return;
                gpuCanvas=new WpfCanvas { Background=WpfBrushes.Black, ClipToBounds=true, Focusable=true };
                gpuImage=new WpfImage { Stretch=WpfStretch.Fill, IsHitTestVisible=false };
                gpuCanvas.Children.Add(gpuImage);
                gpuView=new ElementHost { Location=pic.Location, Size=pic.Size, BackColor=Color.Black, Child=gpuCanvas };
                gpuCanvas.MouseLeftButtonDown+=delegate(object sender,System.Windows.Input.MouseButtonEventArgs e)
                {
                    var p=e.GetPosition(gpuCanvas);
                    int x=(int)Math.Round(p.X*gpuView.ClientSize.Width/Math.Max(1,gpuCanvas.ActualWidth));
                    int y=(int)Math.Round(p.Y*gpuView.ClientSize.Height/Math.Max(1,gpuCanvas.ActualHeight));
                    if(e.ClickCount>=2)PictureMouseDoubleClick(sender,new MouseEventArgs(MouseButtons.Left,2,x,y,0));
                    else
                    {
                        PictureMouseDown(sender,new MouseEventArgs(MouseButtons.Left,1,x,y,0));
                        if(panDragging)gpuCanvas.CaptureMouse();
                    }
                    gpuCanvas.Focus();
                    e.Handled=true;
                };
                gpuCanvas.MouseMove+=delegate(object sender,System.Windows.Input.MouseEventArgs e)
                {
                    if(!panDragging || e.LeftButton!=WpfMouseButtonState.Pressed)return;
                    var p=e.GetPosition(gpuCanvas);
                    int x=(int)Math.Round(p.X*gpuView.ClientSize.Width/Math.Max(1,gpuCanvas.ActualWidth));
                    int y=(int)Math.Round(p.Y*gpuView.ClientSize.Height/Math.Max(1,gpuCanvas.ActualHeight));
                    PictureMouseMove(sender,new MouseEventArgs(MouseButtons.Left,1,x,y,0));
                    e.Handled=true;
                };
                gpuCanvas.MouseLeftButtonUp+=delegate(object sender,System.Windows.Input.MouseButtonEventArgs e)
                {
                    PictureMouseUp(sender,new MouseEventArgs(MouseButtons.Left,1,0,0,0));
                    if(gpuCanvas.IsMouseCaptured)gpuCanvas.ReleaseMouseCapture();
                    e.Handled=true;
                };
                gpuCanvas.LostMouseCapture+=delegate{panDragging=false;};
                gpuCanvas.PreviewKeyDown+=delegate(object sender,System.Windows.Input.KeyEventArgs e)
                {if(e.Key==WpfKey.Space){spaceOriginalHeld=true;UpdateOriginalHold();e.Handled=true;}};
                gpuCanvas.PreviewKeyUp+=delegate(object sender,System.Windows.Input.KeyEventArgs e)
                {if(e.Key==WpfKey.Space){spaceOriginalHeld=false;UpdateOriginalHold();e.Handled=true;}};
                gpuCanvas.SizeChanged+=delegate{UpdateGpuDisplay();};
                Controls.Add(gpuView);
                gpuView.BringToFront();
                pic.Visible=false;
                gpuReady=true;
                UpdateGpuDisplay();
            }
            catch { DisableGpuView(); }
        }

        private void DisableGpuView()
        {
            gpuReady=false;
            normalGpuSource=zoomGpuSource=originalGpuSource=null;
            try
            {
                if(gpuImage!=null)gpuImage.Source=null;
                if(gpuCanvas!=null)gpuCanvas.Children.Clear();
            }
            catch { }
            if(gpuView!=null)
            {
                try { gpuView.Child=null;gpuView.Dispose(); } catch { }
                gpuView=null;
            }
            gpuCanvas=null;gpuImage=null;
            if(pic!=null && !pic.IsDisposed){pic.Visible=true;pic.Invalidate();}
        }

        private static WpfBitmapSource UploadGpuFrame(Bitmap bitmap)
        {
            if(bitmap==null)return null;
            IntPtr handle=bitmap.GetHbitmap();
            try
            {
                var source=WpfImaging.CreateBitmapSourceFromHBitmap(handle,IntPtr.Zero,
                    System.Windows.Int32Rect.Empty,WpfBitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally {DeleteObject(handle);}
        }

        private void UpdateGpuDisplay()
        {
            if(!gpuReady || gpuCanvas==null || gpuImage==null)return;
            try
            {
                bool zoom=zoomPreview;
                var source=zoom?(originalHeld?originalGpuSource:zoomGpuSource):normalGpuSource;
                if(source==null) {gpuImage.Source=null;return;}
                if(!object.ReferenceEquals(gpuImage.Source,source))gpuImage.Source=source;
                WpfRenderOptions.SetBitmapScalingMode(gpuImage,zoom?WpfBitmapScalingMode.NearestNeighbor:WpfBitmapScalingMode.HighQuality);
                double w=Math.Max(1,gpuCanvas.ActualWidth),h=Math.Max(1,gpuCanvas.ActualHeight);
                if(zoom)
                {
                    double sx=w/Math.Max(1,gpuView.ClientSize.Width);
                    double sy=h/Math.Max(1,gpuView.ClientSize.Height);
                    gpuImage.Width=source.PixelWidth*sx;
                    gpuImage.Height=source.PixelHeight*sy;
                    WpfCanvas.SetLeft(gpuImage,(w-gpuImage.Width)/2-panX*sx);
                    WpfCanvas.SetTop(gpuImage,(h-gpuImage.Height)/2-panY*sy);
                }
                else
                {
                    gpuImage.Width=w;gpuImage.Height=h;
                    WpfCanvas.SetLeft(gpuImage,0);
                    WpfCanvas.SetTop(gpuImage,0);
                }
            }
            catch {DisableGpuView();}
        }

        // Fallback also draws FROM A CACHED full frame without invoking FFmpeg.
        private void PicturePaint(object sender,PaintEventArgs e)
        {
            if(!zoomPreview || gpuReady)return;
            Bitmap bitmap=originalHeld?zoomOriginalImage:zoomFullImage;
            if(bitmap==null)return;
            e.Graphics.Clear(Color.Black);
            e.Graphics.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode=System.Drawing.Drawing2D.PixelOffsetMode.Half;
            e.Graphics.DrawImage(bitmap,(pic.ClientSize.Width-bitmap.Width)/2-panX,
                (pic.ClientSize.Height-bitmap.Height)/2-panY,bitmap.Width,bitmap.Height);
        }

        private void UpdateOriginalHold()
        {
            bool next=spaceOriginalHeld||mouseOriginalHeld;
            if(originalHeld==next)return;
            originalHeld=next;
            if(zoomPreview && (originalHeld || !zoomGradedDirty) && (originalHeld?zoomOriginalImage:zoomFullImage)!=null &&
                (originalHeld?zoomOriginalPosition:zoomFullPosition)==CurrentTime())
            {
                if(gpuReady)UpdateGpuDisplay();else pic.Invalidate();
                return;
            }
            RequestRender(true);
        }

        private void PictureMouseDoubleClick(object sender,MouseEventArgs e)
        {
            if(e.Button!=MouseButtons.Left)return;
            zoomPreview=!zoomPreview;
            panDragging=false;
            panX=0;panY=0;
            pic.Cursor=zoomPreview?Cursors.SizeAll:Cursors.Default;
            if(gpuReady)gpuCanvas.Cursor=zoomPreview?WpfCursors.SizeAll:WpfCursors.Arrow;
            pic.SizeMode=zoomPreview?PictureBoxSizeMode.Normal:PictureBoxSizeMode.Zoom;
            if(zoomPreview){Image old=pic.Image;pic.Image=null;if(old!=null)old.Dispose();}
            UpdateTimeLabel();
            RequestRender(true);
        }

        private void PictureMouseDown(object sender,MouseEventArgs e)
        {
            if(!zoomPreview||e.Button!=MouseButtons.Left)return;
            panDragging=true;
            panDragStart=e.Location;
            if(!gpuReady)pic.Capture=true;
        }

        private void PictureMouseMove(object sender,MouseEventArgs e)
        {
            if(!panDragging||!zoomPreview||e.Button!=MouseButtons.Left)return;
            int dx=e.X-panDragStart.X,dy=e.Y-panDragStart.Y;
            panDragStart=e.Location;
            Bitmap zoomImage=originalHeld?zoomOriginalImage:zoomFullImage;
            int imageWidth=zoomImage!=null?zoomImage.Width:sourceWidth;
            int imageHeight=zoomImage!=null?zoomImage.Height:sourceHeight;
            int limitX=Math.Max(0,(imageWidth-pic.ClientSize.Width)/2);
            int limitY=Math.Max(0,(imageHeight-pic.ClientSize.Height)/2);
            int x=Math.Max(-limitX,Math.Min(limitX,panX-dx));
            int y=Math.Max(-limitY,Math.Min(limitY,panY-dy));
            if(x==panX&&y==panY)return;
            panX=x;panY=y;
            if(gpuReady)UpdateGpuDisplay();else pic.Invalidate();
        }

        private void PictureMouseUp(object sender,MouseEventArgs e)
        {
            if(e.Button!=MouseButtons.Left||!panDragging)return;
            panDragging=false;
        }

        private void RequestRender(bool immediate)
        {
            if (closing || IsDisposed) return;
            zoomGradedDirty=true;
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
                Bitmap fullOriginal = null;
                string error = "";
                Stopwatch watch = Stopwatch.StartNew();
                byte[] reference = null;
                try
                {
                    if(cachedVideoReference==null || cachedReferencePosition!=state.Position)
                    {
                        // Keep the whole source frame for the main window's LUT
                        // reference even if this editor is showing a 1:1 crop.
                        var originalState=new RenderState {Serial=-state.Serial-1,Position=state.Position,Original=true};
                        using(var clean=RenderPreview(originalState))
                        using(var stream=new MemoryStream())
                        {
                            clean.Save(stream,System.Drawing.Imaging.ImageFormat.Png);
                            cachedVideoReference=stream.ToArray();cachedReferencePosition=state.Position;
                        }
                    }
                    reference=cachedVideoReference;
                    // A new time position needs one native original for instant
                    // Space bypass. Never regenerate it for panning or grading.
                    if(state.Zoom && zoomOriginalPosition!=state.Position)
                    {
                        var cleanState=new RenderState { Serial=-state.Serial-2,
                            Position=state.Position, Original=true, Zoom=true };
                        fullOriginal=RenderPreview(cleanState);
                    }
                    if(state.Original)
                    {
                        if(state.Zoom)
                        {
                            if(fullOriginal!=null){image=fullOriginal;fullOriginal=null;}
                            else image=RenderPreview(state);
                        }
                        else using(var stream=new MemoryStream(reference))
                            using(var clean=Image.FromStream(stream))image=new Bitmap(clean);
                    }
                    else image=RenderPreview(state);
                }
                catch (Exception ex) { error = ex.Message; }
                watch.Stop();
                long elapsedMs = watch.ElapsedMilliseconds;

                if (closing || IsDisposed)
                {
                    if (image != null) image.Dispose();
                    if (fullOriginal != null) fullOriginal.Dispose();
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
                                if (fullOriginal != null) fullOriginal.Dispose();
                                return;
                            }

                            if(!sourceIdentity.IsCurrent(videoPath,ffprobePath)) { if(image!=null){image.Dispose();image=null;}error="Input or FFprobe changed; close and re-probe."; }
                            if (state.Serial == renderSerial && image != null)
                            {
                                displayedVideoReference=reference;
                                if(state.Zoom)
                                {
                                    if(fullOriginal!=null)
                                    {
                                        if(zoomOriginalImage!=null)zoomOriginalImage.Dispose();
                                        zoomOriginalImage=fullOriginal;
                                        zoomOriginalPosition=state.Position;
                                        fullOriginal=null;
                                        if(gpuReady)try{originalGpuSource=UploadGpuFrame(zoomOriginalImage);}catch{DisableGpuView();}
                                    }
                                    if(state.Original)
                                    {
                                        if(zoomOriginalImage!=null)zoomOriginalImage.Dispose();
                                        zoomOriginalImage=image;
                                        zoomOriginalPosition=state.Position;
                                        image=null;
                                        if(gpuReady)try{originalGpuSource=UploadGpuFrame(zoomOriginalImage);}catch{DisableGpuView();}
                                    }
                                    else
                                    {
                                        if(zoomFullImage!=null)zoomFullImage.Dispose();
                                        zoomFullImage=image;
                                        zoomFullPosition=state.Position;
                                        image=null;
                                        zoomGradedDirty=false;
                                        if(gpuReady)try{zoomGpuSource=UploadGpuFrame(zoomFullImage);}catch{DisableGpuView();}
                                    }
                                    Image oldImage=pic.Image;pic.Image=null;
                                    if(oldImage!=null)oldImage.Dispose();
                                    pic.Invalidate();
                                }
                                else
                                {
                                    Image oldImage=pic.Image;
                                    pic.Image=image;
                                    if(gpuReady)try{normalGpuSource=UploadGpuFrame(image);}catch{DisableGpuView();}
                                    image=null;
                                    if(oldImage!=null)oldImage.Dispose();
                                }
                                UpdateGpuDisplay();
                                status.Text = S("预览已更新 · ", "Preview updated · ") + elapsedMs.ToString(CultureInfo.InvariantCulture) +
                                    " ms · " + (gpuReady ? "GPU" : S("缓存显示", "Cached display"));
                            }
                            else if (state.Serial == renderSerial && !string.IsNullOrEmpty(error))
                            {
                                status.Text = S("预览失败：", "Preview failed: ") + error;
                            }

                            if (image != null) image.Dispose();
                            if (fullOriginal != null) fullOriginal.Dispose();

                            // If the debounce timer already elapsed while FFmpeg was busy,
                            // render the newest state immediately. Otherwise the active timer
                            // keeps the normal debounce delay and coalesces intermediate input.
                            if (renderPending && renderDue) StartRenderIfReady();
                        }
                        catch
                        {
                            renderWorkerRunning = false;
                            if (image != null) image.Dispose();
                            if (fullOriginal != null) fullOriginal.Dispose();
                        }
                    });
                }
                catch
                {
                    if (image != null) image.Dispose();
                    if (fullOriginal != null) fullOriginal.Dispose();
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
            state.Zoom=zoomPreview;
            state.PanX=panX;state.PanY=panY;
            state.Enabled = checkEnable.Checked;
            state.Contrast = (double)adjustments["Contrast"].Number.Value;
            state.Brightness = (double)adjustments["Brightness"].Number.Value;
            state.Saturation = (double)adjustments["Saturation"].Number.Value;
            state.Gamma = (double)adjustments["Gamma"].Number.Value;
            state.BlackWhite = checkBlackWhite.Checked;
            state.UseLut = checkLut.Checked && lutStrength>0 && (filmModeActive || !string.IsNullOrEmpty(activeCompatLutPath));
            state.CompatLutPath = activeCompatLutPath;
            state.LutStrength = lutStrength;
            if(filmModeActive)state.FilmSettings=CaptureFilmSettings();
            return state;
        }

        private string FilmCubeForPreview(FilmLutSettings settings)
        {
            string key=settings.ProfileId+"_"+settings.ExposureTenths.ToString(CultureInfo.InvariantCulture)+"_"+
                settings.Warmth.ToString(CultureInfo.InvariantCulture)+"_"+settings.Saturation.ToString(CultureInfo.InvariantCulture);
            string file;
            if(filmPreviewCubes.TryGetValue(key,out file)&&File.Exists(file))return file;
            file=Path.Combine(tempRoot,"film_"+filmPreviewCubes.Count.ToString(CultureInfo.InvariantCulture)+".cube");
            var full=settings.Copy();full.Strength=100;
            FilmLutCore.WriteCube(file,FilmLutCube.Read(FilmLutCore.ProfilePath(galleryAppRoot,full.ProfileId)),full);
            filmPreviewCubes[key]=file;
            return file;
        }

        private Bitmap RenderPreview(RenderState state)
        {
            string output = Path.Combine(tempRoot, "preview_" + state.Serial.ToString(CultureInfo.InvariantCulture) + ".jpg");
            string preparedStrengthLut="";
            try { if (File.Exists(output)) File.Delete(output); } catch { }

            List<string> prefix = new List<string>();
            // At 1:1 keep the ENTIRE native frame, preserving exactly the
            // encoder's FFmpeg grading filters. WPF/GDI pans this cached image
            // without FFmpeg, LUT recomputation, decoding, or texture uploads.
            if(state.Zoom)prefix.Add("null");
            else prefix.Add("scale=" + previewWidth.ToString(CultureInfo.InvariantCulture) + ":" + previewHeight.ToString(CultureInfo.InvariantCulture) + ":force_original_aspect_ratio=decrease:force_divisible_by=2:flags=bilinear");
            bool gradeInHdr = hdrPreviewInfo!=null && preserveHdrColor;
            if(hdrPreviewInfo!=null && !gradeInHdr)prefix.Add(HdrColorProcessingCore.DisplayToSdr(hdrPreviewInfo,previewTonemap));
            if (!state.Original && state.Enabled && gradeInHdr)
            {
                var cc=new Dictionary<string,string>();cc["FG_COLOR_ENABLED"]="1";
                cc["FG_COLOR_CONTRAST"]=F(state.Contrast);cc["FG_COLOR_BRIGHTNESS"]=F(state.Brightness);
                cc["FG_COLOR_SATURATION"]=F(state.Saturation);cc["FG_COLOR_GAMMA"]=F(state.Gamma);
                cc["FG_COLOR_BLACK_WHITE"]=state.BlackWhite?"1":"0";
                string high=HdrColorProcessingCore.Cc(cc,hdrPreviewInfo);if(high.Length>0)prefix.Add(high.TrimEnd(','));
            }
            else if (!state.Original && state.Enabled)
            {
                prefix.Add("eq=contrast=" + F(state.Contrast) + ":brightness=" + F(state.Brightness) + ":saturation=" + F(state.Saturation) + ":gamma=" + F(state.Gamma));
                if (state.BlackWhite) prefix.Add("hue=s=0");
            }

            string filter = string.Join(",", prefix.ToArray());
            if (!state.Original && state.UseLut)
            {
                string lutForPreview=state.FilmSettings!=null?FilmCubeForPreview(state.FilmSettings):state.CompatLutPath;
                string resolvedOpacity="1.00";
                if(state.LutStrength<100) {
                    string strengthSource=lutForPreview;
                    lutForPreview=Path.Combine(tempRoot,"lut_strength_"+state.Serial.ToString(CultureInfo.InvariantCulture)+".cube");
                    resolvedOpacity=LutStrengthCore.CreateFile(strengthSource,lutForPreview,state.LutStrength);preparedStrengthLut=lutForPreview;
                }
                string escaped = EscapeFilterPath(lutForPreview);
                if(gradeInHdr)
                    filter+=","+HdrColorProcessingCore.Lut(hdrPreviewInfo,escaped,resolvedOpacity).TrimEnd(',');
                else if (resolvedOpacity=="1.00")
                    filter += ",format=gbrp16le,lut3d=file='" + escaped + "':interp=tetrahedral";
                else
                {
                    string opacity = resolvedOpacity;
                    filter += ",format=gbrp16le,split=2[lutorig][lutsrc];[lutsrc]lut3d=file='" + escaped + "':interp=tetrahedral[lutgraded];[lutgraded][lutorig]blend=all_mode=normal:all_opacity=" + opacity;
                }
            }
            if(gradeInHdr)filter+=","+HdrColorProcessingCore.DisplayToSdr(hdrPreviewInfo,previewTonemap);
            if(!state.Zoom)filter += ",pad=" + previewWidth.ToString(CultureInfo.InvariantCulture) + ":" + previewHeight.ToString(CultureInfo.InvariantCulture) + ":(ow-iw)/2:(oh-ih)/2:black";

            sourceIdentity.RequireCurrent();
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

            try{if(preparedStrengthLut.Length>0)File.Delete(preparedStrengthLut);}catch{}
            sourceIdentity.RequireCurrent();
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
            if(!Directory.Exists(lutRoot)){FgsMessageBox.Show(this,S("找不到 LUT 根目录：\r\n","LUT root not found:\r\n")+lutRoot);return;}
            openLutGallery.Enabled=false;
            CancelActiveRender();
            try {
                using(var gallery=new NativeLutGalleryForm(galleryAppRoot,ffmpegPath,lutRoot,lutPreviewRoot)) {
                    if(gallery.ShowDialog(this)==DialogResult.OK) {
                        filmUiSync=true;
                        try {filmModeActive=false;checkFilm.Checked=false;}finally{filmUiSync=false;}
                        if(gallery.DisabledLut){PrepareLut("");lutSource="None";}
                        else {PrepareLut(gallery.SelectedLut);lutSource="Gallery";}
                        syncing=true;
                        try{checkLut.Checked=!gallery.DisabledLut&&!string.IsNullOrEmpty(activeCompatLutPath);}finally{syncing=false;}
                        SwitchAdjustmentPage(false);
                    }
                }
                UpdateLutUi();RequestRender(true);
            }catch(Exception ex){FgsMessageBox.Show(this,ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Error);}
            finally{openLutGallery.Enabled=true;}
        }

    }

}
