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
    // A session log survives visible-log trimming/clearing and is flushed before UI updates.
    internal sealed class FullLogFileCore : IDisposable
    {
        private readonly object sync = new object();
        private StreamWriter writer;
        public string FilePath { get; private set; }
        public string Error { get; private set; }
        public FullLogFileCore(string appRoot, string kind = "Session", int entryLimit = 100)
        {
            string name = "FGS_" + kind + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N") + ".log";
            string[] dirs = new string[] { Path.Combine(appRoot, "Logs"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FilmGrainStudio", "Logs") };
            foreach (string dir in dirs)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    FilePath = Path.Combine(dir, name);
                    writer = new StreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(true));
                    writer.AutoFlush = true;
                    Error = "";
                    PruneOldLogs(dir, FilePath, entryLimit);
                    return;
                }
                catch (Exception ex) { Error = ex.Message; }
            }
        }
        private static void PruneOldLogs(string dir, string currentFile, int entryLimit)
        {
            if (entryLimit <= 0) return;
            try
            {
                FileInfo[] files = new DirectoryInfo(dir).GetFiles("FGS_*.log", SearchOption.TopDirectoryOnly);
                Array.Sort(files, delegate(FileInfo a, FileInfo b)
                {
                    int byTime = b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc);
                    return byTime != 0 ? byTime : string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase);
                });
                int kept = 0;
                foreach (FileInfo file in files)
                {
                    if (string.Equals(file.FullName, currentFile, StringComparison.OrdinalIgnoreCase)) { kept++; continue; }
                    if (kept < entryLimit) { kept++; continue; }
                    try { file.Delete(); } catch { }
                }
            }
            catch { }
        }

        public void Write(string text)
        {
            lock (sync)
            {
                if (writer == null) return;
                try { writer.Write(text); }
                catch (Exception ex) { Error = ex.Message; try { writer.Dispose(); } catch { } writer = null; }
            }
        }
        public void Dispose()
        {
            lock (sync) { if (writer != null) { try { writer.Dispose(); } catch { } writer = null; } }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                bool setupTest = Array.Exists(Environment.GetCommandLineArgs(), a => string.Equals(a, "--setup-test=missing", StringComparison.OrdinalIgnoreCase));
                Application.Run(new MainForm(setupTest));
            }
            catch (AppRootNotFoundException ex)
            {
                MessageBox.Show(
                    "找不到 Film Grain Studio 的完整程序目录。\n\n" +
                    "请将整个 dotnet\\build 文件夹放回 Film Grain Studio 目录，再运行其中的 EXE。\n" +
                    "该目录还需要包含 FilmGrain_Config.default.ini 和 Lang 文件夹。\n\n" +
                    "Cannot find the complete Film Grain Studio folder. Move dotnet\\build back into it and run the EXE there.\n\n" +
                    "当前 EXE 位置 / EXE location:\n" + ex.StartPath,
                    "Film Grain Studio - 启动位置不正确 / Incorrect location",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Film Grain Studio .NET Preview", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    internal sealed class AppRootNotFoundException : Exception
    {
        public string StartPath { get; private set; }

        public AppRootNotFoundException(string startPath)
        {
            StartPath = startPath;
        }
    }

    internal sealed class LutChoice
    {
        public string Display { get; private set; }
        public string PathValue { get; private set; }

        public LutChoice(string display, string pathValue)
        {
            Display = display ?? "";
            PathValue = pathValue ?? "";
        }

        public override string ToString()
        {
            return Display;
        }
    }

    internal sealed partial class MainForm : Form
    {
        private readonly Color ColorHeader = Color.FromArgb(45, 57, 72);
        private readonly Color ColorAccent = Color.FromArgb(47, 111, 173);
        private readonly Color ColorSubtle = Color.FromArgb(242, 244, 247);
        private readonly Color ColorMuted = Color.FromArgb(100, 107, 116);

        private readonly string appRoot;
        private readonly FgsConfig config;
        private readonly bool setupTestMode;
        private readonly LanguagePack lang;
        private readonly List<LanguageChoice> languages;
        private readonly Dictionary<string, MediaProbeInfo> mediaProbeCache = new Dictionary<string, MediaProbeInfo>(StringComparer.OrdinalIgnoreCase);
        private MediaProbeCore mediaProbeCore;
        private readonly Dictionary<string, string> av1GrainInspectCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Av1GrainInspectCore av1GrainInspectCore;

        private ListView listFiles;
        private Label lblMediaInfo;
        private Label lblHdrRoute;
        private Label lblStatus;
        private Label lblRunStage;
        private Label lblRunMetric;
        private TextBox log;
        private FullLogFileCore fullLog;
        private FullLogFileCore taskLog;
        private bool fullLogErrorReported;
        private ComboBox cmbCodec;
        private ComboBox cmbContainer;
        private ComboBox cmbSpeed;
        private CheckBox chkSfe;
        private ComboBox cmbBitrate;
        private CheckBox chkBitrateAuto;
        private CheckBox chkHighMotion;
        private ComboBox cmbFps;
        private CheckBox chkInterpolation;
        private ComboBox cmbInterpolationMode;
        private ComboBox cmbInterpolationFps;
        private ComboBox cmbDeint;
        private ComboBox cmbDeintMethod;
        private CheckBox chkCinematic;
        private ComboBox cmbFrameMode;
        private Label lblUploadX264Settings;
        private CheckBox chkUpload;
        private ComboBox cmbUploadBitrate;
        private CheckBox chkUploadBitrateAuto;
        private ComboBox cmbGrainMode;
        private Panel grainContent;
        private GroupBox grpGrain;
        private GroupBox grpLut;
        private Button btnSubtitle;
        private Label lblFrameHelp;
        private Button btnStart;
        private CheckBox chkNativeBackend;
        private Button btnCancelTask;
        private ProgressBar progressRun;
        private ComboBox cmbGpu;
        private ToolStripStatusLabel statusHardware;
        private BridgeTaskCoordinator bridgeTaskCoordinator;
        private HardwareCapabilityCore hardwareCapabilityCore;
        private HardwareCapabilitySnapshot hardwareCaps;
        private bool hardwareCapsReady;
        private bool hardwareDetectionInProgress;
        private bool changingCodecForHardware;
        private bool noReencodeUiActive;
        private bool activeRunNativeBackend;
        private string activeRunCodecName = "";
        private TrackBar trackFilmGrainStrength;
        private Label lblFilmGrainValue;
        private ComboBox cmbGrainFormat;
        private ComboBox cmbGrainStock;
        private NumericUpDown numGrainIso;
        private CheckBox chkGrainChroma;
        private ComboBox cmbGrainTable;
        private CheckBox chkShowAllAv1Tables;
        private ComboBox cmbGrainPlate;
        private readonly List<string> av1GrainTableFiles = new List<string>();
        private readonly List<string> displayedAv1GrainTableFiles = new List<string>();
        private readonly List<string> grainPlateFiles = new List<string>();
        private bool updatingGrainUi;
        private int av1GrainModeIndex = 0;
        private int pixelGrainModeIndex = 1;
        private int av1FormatIndex = 0;
        private int av1StockIndex = 0;
        private int av1IsoValue = 1600;
        private bool av1ChromaEnabled;
        private string selectedAv1GrainTable = "";
        private bool showAllAv1GrainTables;
        private int proceduralStrength = 55;
        private int fgsimPresetIndex = 1;
        private string selectedGrainPlatePath = "";
        private int scannedGrainStrengthIndex = 2;
        private CheckBox chkLut;
        private ComboBox cmbRecentLut;
        private ComboBox cmbFavoriteLut;
        private TrackBar trackLutStrength;
        private Label lblLutStrength;
        private Label lblLutStrengthTitle;
        private Label lblSelectedLut;
        private PictureBox picLutPreview;
        private Button btnLutGallery;
        private Button btnColorCorrection;
        private readonly ToolTip lutToolTip = new ToolTip();
        private readonly ToolTip sfeToolTip = new ToolTip();
        private ComboBox cmbLanguage;
        private Button btnFgsimTextureUpdate;
        private Label lblFgsimTextureStatus;

        private bool loadingLutLists;
        private bool updatingBitrateUi;
        private int lastCodecIndex = 0;
        private readonly string[] modeBitrate = new string[] { "5000", "6000", "7500" };
        private readonly bool[] modeBitrateAuto = new bool[] { true, true, true };
        private const string FgsimStandardItem = "Standard CQ27 / QP18-26";
        private const string FgsimHighItem = "High Quality CQ23 / QP18-24";
        private bool fgsimRcActive;
        private string fgsimRcChoice = "VBR";
        private bool uploadBitrateAuto = true;
        private string selectedLutPath = "";
        private string selectedLutSource = "None";
        private bool colorCorrectionEnabled;
        private double colorContrast = 1.0;
        private double colorBrightness = 0.0;
        private double colorSaturation = 1.0;
        private double colorGamma = 1.0;
        private bool colorBlackWhite;

        public MainForm(bool setupTest)
        {
            appRoot = FindAppRoot(AppDomain.CurrentDomain.BaseDirectory);
            setupTestMode = setupTest;
            string testConfig = setupTest ? Path.Combine(Path.GetTempPath(), "FGS_Setup_Test_" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + ".ini") : null;
            config = new FgsConfig(appRoot, testConfig);
            try { NetworkProxyCore.Apply(config.Get("NETWORK_PROXY_MODE"), config.Get("NETWORK_PROXY_URL")); }
            catch (Exception ex) { NetworkProxyCore.Apply(NetworkProxyCore.SystemMode, ""); MessageBox.Show("网络代理配置无效，已改用系统代理：\r\n" + ex.Message, "Film Grain Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            lang = new LanguagePack(appRoot);
            languages = lang.GetChoices();
            colorCorrectionEnabled = config.GetBool("COLOR_CORRECTION_ENABLED");
            colorContrast = config.GetDouble("COLOR_CONTRAST", 1.0);
            colorBrightness = config.GetDouble("COLOR_BRIGHTNESS", 0.0);
            colorSaturation = config.GetDouble("COLOR_SATURATION", 1.0);
            colorGamma = config.GetDouble("COLOR_GAMMA", 1.0);
            colorBlackWhite = config.GetBool("COLOR_BLACK_WHITE");
            BuildUi();
            if (setupTest) Text = "[设置测试模式] " + Text;
            UpdateSpeedChoices();
            bridgeTaskCoordinator = new BridgeTaskCoordinator();
            bridgeTaskCoordinator.StateChanged += OnBridgeTaskStateChanged;
            bridgeTaskCoordinator.LogLine += OnBridgeExecutionLogLine;
            bridgeTaskCoordinator.ProgressChanged += OnBridgeExecutionProgressChanged;
            bridgeTaskCoordinator.Completed += OnBridgeExecutionCompleted;
            hardwareCapabilityCore = new HardwareCapabilityCore();
            hardwareCapabilityCore.Completed += OnHardwareCapabilityCompleted;
            mediaProbeCore = new MediaProbeCore();
            mediaProbeCore.Completed += OnMediaProbeCompleted;
            av1GrainInspectCore = new Av1GrainInspectCore();
            av1GrainInspectCore.Completed += OnAv1GrainInspectCompleted;
            RefreshLutLists();
            UpdateLutUi();
            UpdateColorCorrectionUi();
            UpdateStatusCount();
            UpdateMediaDrivenUi(null);
            BeginHardwareDetection();
            Shown += delegate { SetupPhase1.ShowIfNeeded(this, appRoot, config, setupTest); };
            FormClosed += delegate
            {
                if (mediaProbeCore != null)
                {
                    mediaProbeCore.Completed -= OnMediaProbeCompleted;
                    mediaProbeCore.Dispose();
                    mediaProbeCore = null;
                }
                if (av1GrainInspectCore != null)
                {
                    av1GrainInspectCore.Completed -= OnAv1GrainInspectCompleted;
                    av1GrainInspectCore.Dispose();
                    av1GrainInspectCore = null;
                }
                if (bridgeTaskCoordinator != null)
                {
                    bridgeTaskCoordinator.StateChanged -= OnBridgeTaskStateChanged;
                    bridgeTaskCoordinator.LogLine -= OnBridgeExecutionLogLine;
                    bridgeTaskCoordinator.ProgressChanged -= OnBridgeExecutionProgressChanged;
                    bridgeTaskCoordinator.Completed -= OnBridgeExecutionCompleted;
                    bridgeTaskCoordinator.Dispose();
                    bridgeTaskCoordinator = null;
                }
                if (hardwareCapabilityCore != null)
                {
                    hardwareCapabilityCore.Completed -= OnHardwareCapabilityCompleted;
                    hardwareCapabilityCore.Dispose();
                    hardwareCapabilityCore = null;
                }
                if (fullLog != null) fullLog.Dispose();
                if (taskLog != null) taskLog.Dispose();
            };
        }

        private static string FindAppRoot(string start)
        {
            DirectoryInfo dir = new DirectoryInfo(Path.GetFullPath(start));
            for (int i = 0; i < 5 && dir != null; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "FilmGrain_Config.default.ini")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "Lang")))
                    return dir.FullName;
            }
            throw new AppRootNotFoundException(start);
        }

        private Font UiFont(float size, FontStyle style)
        {
            return new Font("Segoe UI", size, style, GraphicsUnit.Point);
        }

        private void BuildUi()
        {
            Text = "Film Grain Studio";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1320, 994);
            MinimumSize = new Size(1280, 984);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = UiFont(9f, FontStyle.Regular);
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;

            TryLoadIcon();

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Margin = Padding.Empty;
            root.Padding = Padding.Empty;
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 225));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            Controls.Add(root);

            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildMainArea(), 0, 1);
            root.Controls.Add(BuildLogArea(), 0, 2);
            root.Controls.Add(BuildFooter(), 0, 3);
            root.Controls.Add(BuildStatusStrip(), 0, 4);
        }

        private Control BuildHeader()
        {
            Panel header = new Panel();
            header.Dock = DockStyle.Fill;
            header.BackColor = ColorHeader;

            Label title = new Label();
            title.Text = "Film Grain Studio";
            title.ForeColor = Color.White;
            title.Font = UiFont(18f, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(20, 10);
            header.Controls.Add(title);

            Label subtitle = new Label();
            subtitle.Text = lang.T("app.subtitle");
            subtitle.ForeColor = Color.FromArgb(205, 214, 224);
            subtitle.Font = UiFont(9f, FontStyle.Regular);
            subtitle.AutoSize = true;
            subtitle.Location = new Point(22, 43);
            header.Controls.Add(subtitle);

            Button btnConfig = HeaderButton(lang.T("button.config"), 92);
            Button btnAdvanced = HeaderButton(lang.T("button.advanced"), 92);
            Button btnProxy = HeaderButton(UiText("网络代理", "Network"), 92);
            btnConfig.Click += delegate { ShowPathConfigurationDialog(); };
            btnAdvanced.Click += delegate { ShowAdvancedSettingsDialog(); };
            btnProxy.Click += delegate { ShowNetworkProxyDialog(); };
            header.Controls.Add(btnConfig);
            header.Controls.Add(btnAdvanced);
            header.Controls.Add(btnProxy);

            cmbLanguage = new ComboBox();
            cmbLanguage.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbLanguage.Size = new Size(116, 28);
            cmbLanguage.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            foreach (LanguageChoice item in languages) cmbLanguage.Items.Add(item);
            for (int i = 0; i < languages.Count; i++)
            {
                if (string.Equals(languages[i].Code, lang.Code, StringComparison.OrdinalIgnoreCase))
                {
                    cmbLanguage.SelectedIndex = i;
                    break;
                }
            }
            cmbLanguage.Enabled = true;
            ToolTip tip = new ToolTip();
            tip.SetToolTip(cmbLanguage, lang.T("language.tooltip"));
            cmbLanguage.SelectedIndexChanged += delegate
            {
                LanguageChoice selected = cmbLanguage.SelectedItem as LanguageChoice;
                if (selected == null) return;
                if (string.Equals(selected.Code, config.Get("LANGUAGE"), StringComparison.OrdinalIgnoreCase)) return;
                try
                {
                    config.SaveValue("LANGUAGE", selected.Code);
                    if (log != null) AppendLog("[Config] LANGUAGE=" + selected.Code + Environment.NewLine);
                    MessageBox.Show(this, lang.T("language.restart_required"), lang.T("language.title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, lang.T("language.title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            header.Controls.Add(cmbLanguage);

            Label baseline = new Label();
            baseline.Text = lang.T("app.core") + "  ·  .NET Preview";
            baseline.ForeColor = Color.FromArgb(205, 214, 224);
            baseline.AutoSize = true;
            baseline.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            header.Controls.Add(baseline);

            EventHandler place = delegate
            {
                btnConfig.Left = header.ClientSize.Width - btnConfig.Width - 20;
                btnConfig.Top = 19;
                btnAdvanced.Left = btnConfig.Left - btnAdvanced.Width - 8;
                btnAdvanced.Top = 19;
                btnProxy.Left = btnAdvanced.Left - btnProxy.Width - 8;
                btnProxy.Top = 19;
                cmbLanguage.Left = btnProxy.Left - cmbLanguage.Width - 8;
                cmbLanguage.Top = 20;
                baseline.Left = cmbLanguage.Left - baseline.Width - 18;
                baseline.Top = 27;
            };
            header.Resize += place;
            place(null, EventArgs.Empty);
            return header;
        }

        private Button HeaderButton(string text, int width)
        {
            Button b = new Button();
            b.Text = text;
            b.Size = new Size(width, 30);
            b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(110, 126, 145);
            b.ForeColor = Color.White;
            b.BackColor = Color.FromArgb(58, 72, 90);
            return b;
        }

        private Control BuildMainArea()
        {
            TableLayoutPanel main = new TableLayoutPanel();
            main.Dock = DockStyle.Fill;
            main.Padding = new Padding(10, 10, 10, 6);
            main.Margin = Padding.Empty;
            main.ColumnCount = 3;
            main.RowCount = 1;
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            main.Controls.Add(BuildInputGroup(), 0, 0);
            main.Controls.Add(BuildEncodeGroup(), 1, 0);
            main.Controls.Add(BuildRightArea(), 2, 0);
            return main;
        }

        private Control BuildInputGroup()
        {
            GroupBox box = new GroupBox();
            box.Text = lang.T("input.group");
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(0, 0, 6, 0);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(7, 6, 7, 7);
            layout.ColumnCount = 1;
            layout.RowCount = 4;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            box.Controls.Add(layout);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = false;
            Button add = new Button(); add.Text = lang.T("button.add"); add.Size = new Size(92, 29);
            Button remove = new Button(); remove.Text = lang.T("button.remove"); remove.Size = new Size(82, 29);
            Button clear = new Button(); clear.Text = lang.T("button.clear"); clear.Size = new Size(62, 29);
            add.Click += delegate { AddFilesWithDialog(); };
            remove.Click += delegate { RemoveSelected(); };
            clear.Click += delegate { listFiles.Items.Clear(); UpdateSelectedInfo(); UpdateStatusCount(); };
            buttons.Controls.Add(add); buttons.Controls.Add(remove); buttons.Controls.Add(clear);
            layout.Controls.Add(buttons, 0, 0);

            listFiles = new ListView();
            listFiles.Dock = DockStyle.Fill;
            listFiles.View = View.Details;
            listFiles.FullRowSelect = true;
            listFiles.GridLines = true;
            listFiles.HideSelection = false;
            listFiles.AllowDrop = true;
            listFiles.ShowItemToolTips = true;
            listFiles.Columns.Add(lang.T("input.column.filename"), 178);
            listFiles.Columns.Add(lang.T("input.column.size"), 72);
            listFiles.Columns.Add(lang.T("input.column.directory"), 260);
            listFiles.DragEnter += OnDragEnter;
            listFiles.DragDrop += OnDragDrop;
            listFiles.SelectedIndexChanged += delegate { UpdateSelectedInfo(); };
            layout.Controls.Add(listFiles, 0, 1);

            GroupBox info = new GroupBox();
            info.Text = lang.T("input.info_group");
            info.Dock = DockStyle.Fill;
            info.Margin = new Padding(0, 5, 0, 3);
            lblMediaInfo = new Label();
            lblMediaInfo.Dock = DockStyle.Fill;
            lblMediaInfo.Padding = new Padding(7, 4, 7, 3);
            lblMediaInfo.TextAlign = ContentAlignment.TopLeft;
            lblMediaInfo.ForeColor = ColorMuted;
            lblMediaInfo.Font = UiFont(8.5f, FontStyle.Regular);
            lblMediaInfo.Text = lang.T("input.info_prompt");
            info.Controls.Add(lblMediaInfo);
            layout.Controls.Add(info, 0, 2);

            TableLayoutPanel inputStatus = new TableLayoutPanel();
            inputStatus.Dock = DockStyle.Fill;
            inputStatus.ColumnCount = 1;
            inputStatus.RowCount = 2;
            inputStatus.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            inputStatus.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label note = new Label();
            note.Dock = DockStyle.Fill;
            note.ForeColor = ColorMuted;
            note.Padding = new Padding(6, 3, 4, 0);
            note.AutoEllipsis = true;
            note.Text = lang.T("input.note");
            inputStatus.Controls.Add(note, 0, 0);
            lblHdrRoute = new Label();
            lblHdrRoute.Dock = DockStyle.Fill;
            lblHdrRoute.ForeColor = ColorMuted;
            lblHdrRoute.Font = UiFont(8.5f, FontStyle.Bold);
            lblHdrRoute.Padding = new Padding(6, 0, 4, 0);
            lblHdrRoute.AutoEllipsis = true;
            lblHdrRoute.Text = lang.T("input.hdr_route_pending");
            inputStatus.Controls.Add(lblHdrRoute, 0, 1);
            layout.Controls.Add(inputStatus, 0, 3);
            return box;
        }

        private Control BuildEncodeGroup()
        {
            GroupBox box = new GroupBox();
            box.Text = lang.T("encode.group");
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(6, 0, 6, 0);

            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.Padding = new Padding(5, 7, 5, 5);
            t.ColumnCount = 2;
            t.RowCount = 17;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 16; i++) t.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            box.Controls.Add(t);

            cmbCodec = Combo(new string[] { lang.T("codec.av1_default"), lang.T("codec.hevc_scan"), lang.T("codec.x264_default") }, 0);
            cmbContainer = Combo(new string[] { lang.T("container.mp4"), lang.T("container.mkv") }, 0);
            cmbSpeed = Combo(new string[] { lang.T("speed.fast"), lang.T("speed.standard") }, 0);
            chkSfe = Check(lang.T("encode.sfe"), false); chkSfe.Enabled = false;
            sfeToolTip.SetToolTip(chkSfe, "NVENC: Split Frame Encoding (SFE)");

            TableLayoutPanel bitrate = new TableLayoutPanel();
            bitrate.Dock = DockStyle.Fill; bitrate.Margin = Padding.Empty; bitrate.ColumnCount = 3; bitrate.RowCount = 1;
            bitrate.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            bitrate.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
            bitrate.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
            cmbBitrate = ComboEditable(new string[] { "3000", "3500", "4000", "5000", "6000", "7000", "7500", "8000", "9000", "10000", "11000", "12000", "15000", "18000", "20000", "22000", "30000" }, "5000");
            cmbBitrate.DropDownWidth = 330;
            chkBitrateAuto = Check(lang.T("encode.auto"), true);
            chkHighMotion = Check(lang.T("encode.high_motion"), false);
            bitrate.Controls.Add(cmbBitrate, 0, 0); bitrate.Controls.Add(chkBitrateAuto, 1, 0); bitrate.Controls.Add(chkHighMotion, 2, 0);

            cmbFps = Combo(new string[] { lang.T("fps.auto_interlaced"), lang.T("fps.keep_source") }, 0);
            TableLayoutPanel interp = new TableLayoutPanel(); interp.Dock = DockStyle.Fill; interp.Margin = Padding.Empty;
            interp.ColumnCount = 3; interp.RowCount = 1;
            interp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            interp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            interp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            chkInterpolation = Check(lang.T("encode.enable"), false); chkInterpolation.Dock = DockStyle.Fill;
            cmbInterpolationMode = Combo(new string[] { "OpenSVP · " + lang.T("interp.smooth"), "OpenSVP · " + lang.T("interp.adaptive"), "FRUC Vulkan" }, 0);
            cmbInterpolationMode.Dock = DockStyle.Fill; cmbInterpolationMode.DropDownWidth = 220;
            cmbInterpolationFps = ComboEditable(new string[] { "60", "59.94", "60000/1001", "2x", "1.5x" }, config.Get("INTERPOLATION_TARGET_FPS"));
            cmbInterpolationFps.Dock = DockStyle.Fill; cmbInterpolationFps.DropDownWidth = 130;
            lutToolTip.SetToolTip(cmbInterpolationFps, UiText("目标帧率：绝对帧率、分数帧率或源帧率倍率；默认 60。", "Target rate: absolute FPS, a rational rate, or source-rate multiplier; default 60."));
            cmbInterpolationFps.Text = config.Get("INTERPOLATION_TARGET_FPS"); cmbInterpolationFps.Enabled = false;
            cmbInterpolationFps.TextChanged += delegate { if (config.Get("INTERPOLATION_TARGET_FPS") != cmbInterpolationFps.Text.Trim()) try { config.Save(new Dictionary<string,string> { { "INTERPOLATION_TARGET_FPS", cmbInterpolationFps.Text.Trim() } }); } catch { } UpdateMediaDrivenUi(GetSelectedMediaInfo()); UpdateBitrateDisplays(); };
            cmbInterpolationMode.SelectedIndexChanged += delegate {
                if (cmbInterpolationMode.SelectedIndex == 2 &&
                    (config.FrucUsesMainFfmpeg()
                        ? !NativeHevcCore.HasFrucFilter(Path.Combine(config.Get("FFMPEG_DIR"), "ffmpeg.exe"))
                        : string.IsNullOrWhiteSpace(config.Get("FRUC_FFMPEG_PATH"))))
                {
                    using (OpenFileDialog dialog = new OpenFileDialog())
                    {
                        dialog.Title = UiText("选择包含 fruc_vulkan 滤镜的 ffmpeg.exe", "Select ffmpeg.exe with fruc_vulkan support");
                        dialog.Filter = "ffmpeg.exe|ffmpeg.exe|Executable (*.exe)|*.exe";
                        if (dialog.ShowDialog(this) == DialogResult.OK && NativeHevcCore.HasFrucFilter(dialog.FileName))
                            config.Save(new Dictionary<string,string> { { "FRUC_FFMPEG_PATH", dialog.FileName }, { "FRUC_FFMPEG_SAME_AS_MAIN", "false" } });
                        else
                        {
                            cmbInterpolationMode.SelectedIndex = 0;
                            MessageBox.Show(this, UiText("未选中支持 fruc_vulkan 的 ffmpeg.exe。可在配置路径界面设置开发版目录。", "Select an ffmpeg.exe with fruc_vulkan support in Path Configuration."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                    }
                }
                if (chkInterpolation.Checked) UpdateMediaDrivenUi(GetSelectedMediaInfo());
            };
            chkInterpolation.CheckedChanged += delegate {
                if (chkInterpolation.Checked && cmbInterpolationMode.SelectedIndex != 2 && (setupTestMode || !File.Exists(Path.Combine(appRoot, "_OpenSVPFlow", ".venv", "Scripts", "vspipe.exe"))))
                {
                    chkInterpolation.Checked = false;
                    if (MessageBox.Show(this,
                        UiText("OpenSVPFlow 插帧运行环境缺失。现在打开首次运行设置安装吗？", "OpenSVPFlow is not installed. Open setup now?"),
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        SetupPhase1.ShowForInterpolation(this, appRoot, config, setupTestMode);
                    return;
                }
                cmbInterpolationMode.Enabled = true;
                UpdateMediaDrivenUi(GetSelectedMediaInfo());
                UpdateBitrateDisplays();
            };
            interp.Controls.Add(chkInterpolation, 0, 0); interp.Controls.Add(cmbInterpolationMode, 1, 0); interp.Controls.Add(cmbInterpolationFps, 2, 0);

            cmbDeint = Combo(new string[] { lang.T("deint.auto"), lang.T("deint.off") }, 0);
            cmbDeintMethod = Combo(new string[] { lang.T("deint.vulkan"), lang.T("deint.cuda"), lang.T("deint.w3fdif") }, 0);
            cmbDeint.SelectedIndexChanged += delegate { UpdateMediaDrivenUi(GetSelectedMediaInfo()); UpdateBitrateDisplays(); };
            cmbGpu = Combo(new string[] { lang.T("gpu.auto_retry") }, 0); cmbGpu.Enabled = false;
            chkCinematic = Check(lang.T("cinematic.enable"), true);
            cmbFrameMode = Combo(new string[] { lang.T("cinematic.letterbox"), lang.T("cinematic.crop") }, 0);

            TableLayoutPanel upload = new TableLayoutPanel(); upload.Dock = DockStyle.Fill; upload.Margin = Padding.Empty; upload.ColumnCount = 3; upload.RowCount = 1;
            upload.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52)); upload.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48)); upload.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
            chkUpload = Check(lang.T("encode.upload_h264"), false);
            cmbUploadBitrate = ComboEditable(new string[] { "3000", "3500", "4000", "5000", "6000", "7500", "9000", "11000", "15000", "22000", "30000" }, "7500"); cmbUploadBitrate.Enabled = false;
            chkUploadBitrateAuto = Check(lang.T("encode.auto"), true); chkUploadBitrateAuto.Enabled = false;
            chkUpload.CheckedChanged += delegate { cmbUploadBitrate.Enabled = chkUpload.Checked; chkUploadBitrateAuto.Enabled = chkUpload.Checked; UpdateBitrateDisplays(); UpdateHdrRouteStatus(GetSelectedMediaInfo()); };
            upload.Controls.Add(chkUpload, 0, 0); upload.Controls.Add(cmbUploadBitrate, 1, 0); upload.Controls.Add(chkUploadBitrateAuto, 2, 0);

            btnSubtitle = new Button(); btnSubtitle.Text = lang.T("button.subtitle"); btnSubtitle.Dock = DockStyle.Fill; btnSubtitle.Margin = new Padding(3, 4, 8, 4);
            btnSubtitle.Click += delegate { ShowSubtitleDialog(); };
            RefreshSubtitleButton();

            lblFrameHelp = new Label(); lblFrameHelp.Dock = DockStyle.Fill; lblFrameHelp.AutoEllipsis = true; lblFrameHelp.ForeColor = ColorMuted; lblFrameHelp.TextAlign = ContentAlignment.TopLeft; lblFrameHelp.Padding = new Padding(8, 6, 8, 0); lblFrameHelp.Text = lang.T("encode.frame_help");

            AddLabeledRow(t, 0, lang.T("encode.method"), cmbCodec);
            AddLabeledRow(t, 1, lang.T("encode.container"), cmbContainer);
            AddLabeledRow(t, 2, lang.T("encode.speed"), cmbSpeed);
            t.Controls.Add(chkSfe, 1, 3);
            AddLabeledRow(t, 4, lang.T("encode.bitrate"), bitrate);
            AddLabeledRow(t, 5, lang.T("encode.fps"), cmbFps);
            AddLabeledRow(t, 6, lang.T("encode.interpolation"), interp);
            AddLabeledRow(t, 7, lang.T("encode.deinterlace"), cmbDeint);
            AddLabeledRow(t, 8, lang.T("encode.deinterlace_method"), cmbDeintMethod);
            AddLabeledRow(t, 9, lang.T("encode.gpu"), cmbGpu);
            t.Controls.Add(chkCinematic, 0, 10); t.SetColumnSpan(chkCinematic, 2);
            AddLabeledRow(t, 11, lang.T("encode.framing"), cmbFrameMode);
            t.Controls.Add(upload, 0, 12); t.SetColumnSpan(upload, 2);
            lblUploadX264Settings = new Label(); lblUploadX264Settings.Dock = DockStyle.Fill;
            lblUploadX264Settings.ForeColor = ColorMuted; lblUploadX264Settings.TextAlign = ContentAlignment.MiddleLeft;
            lblUploadX264Settings.Padding = new Padding(3, 0, 0, 0);
            t.Controls.Add(lblUploadX264Settings, 0, 13); t.SetColumnSpan(lblUploadX264Settings, 2);
            t.Controls.Add(btnSubtitle, 0, 14); t.SetColumnSpan(btnSubtitle, 2);
            t.Controls.Add(lblFrameHelp, 0, 15); t.SetColumnSpan(lblFrameHelp, 2);

            cmbCodec.SelectedIndexChanged += delegate
            {
                if (GuardUnavailableCodecSelection()) return;
                if (!updatingBitrateUi && lastCodecIndex >= 0 && lastCodecIndex < modeBitrate.Length)
                {
                    long current;
                    if (long.TryParse(cmbBitrate.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out current) && current > 10)
                        modeBitrate[lastCodecIndex] = cmbBitrate.Text.Trim();
                    if (!(fgsimRcActive && lastCodecIndex == 1 && fgsimRcChoice != "VBR"))
                        modeBitrateAuto[lastCodecIndex] = chkBitrateAuto.Checked;
                }
                if (cmbCodec.SelectedIndex >= 0 && cmbCodec.SelectedIndex <= 2)
                    lastCodecIndex = cmbCodec.SelectedIndex;
                UpdateGrainForCodec();
                if (cmbCodec.SelectedIndex >= 0 && cmbCodec.SelectedIndex <= 2) LoadBitrateStateForCodec();
                UpdateNoReencodeUi();
                UpdateSpeedChoices();
                UpdateSfeUi();
                UpdateMediaDrivenUi(GetSelectedMediaInfo());
                UpdateBitrateDisplays();
                UpdateHardwareDependentControls();
            };
            cmbSpeed.SelectedIndexChanged += delegate { UpdateSfeUi(); };
            cmbBitrate.TextChanged += delegate
            {
                if (updatingBitrateUi || cmbCodec.SelectedIndex < 0 || cmbCodec.SelectedIndex > 2) return;
                if (IsHevcFgsim())
                {
                    if (cmbBitrate.Text == FgsimStandardItem) { fgsimRcChoice = "STANDARD"; UpdateFgsimBitrateControls(); return; }
                    if (cmbBitrate.Text == FgsimHighItem) { fgsimRcChoice = "HIGH"; UpdateFgsimBitrateControls(); return; }
                    fgsimRcChoice = "VBR";
                }
                modeBitrate[cmbCodec.SelectedIndex] = cmbBitrate.Text.Trim();
                modeBitrateAuto[cmbCodec.SelectedIndex] = false;
                updatingBitrateUi = true;
                try { chkBitrateAuto.Checked = false; }
                finally { updatingBitrateUi = false; }
                UpdateFgsimBitrateControls();
            };
            chkBitrateAuto.CheckedChanged += delegate
            {
                if (updatingBitrateUi || cmbCodec.SelectedIndex < 0 || cmbCodec.SelectedIndex > 2) return;
                modeBitrateAuto[cmbCodec.SelectedIndex] = chkBitrateAuto.Checked;
                UpdateBitrateDisplays();
            };
            chkHighMotion.CheckedChanged += delegate { UpdateBitrateDisplays(); };
            cmbFps.SelectedIndexChanged += delegate { UpdateBitrateDisplays(); };
            cmbInterpolationMode.SelectedIndexChanged += delegate { UpdateBitrateDisplays(); };
            cmbDeintMethod.SelectedIndexChanged += delegate { UpdateBitrateDisplays(); };
            cmbUploadBitrate.TextChanged += delegate
            {
                if (updatingBitrateUi) return;
                uploadBitrateAuto = false;
                updatingBitrateUi = true;
                try { chkUploadBitrateAuto.Checked = false; }
                finally { updatingBitrateUi = false; }
            };
            chkUploadBitrateAuto.CheckedChanged += delegate
            {
                if (updatingBitrateUi) return;
                uploadBitrateAuto = chkUploadBitrateAuto.Checked;
                UpdateBitrateDisplays();
            };
            LoadBitrateStateForCodec();
            UpdateBitrateDisplays();
            return box;
        }

        private Control BuildRightArea()
        {
            TableLayoutPanel right = new TableLayoutPanel();
            right.Dock = DockStyle.Fill;
            right.Margin = new Padding(6, 0, 0, 0);
            right.ColumnCount = 1;
            right.RowCount = 2;
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
            right.Controls.Add(BuildGrainGroup(), 0, 0);
            right.Controls.Add(BuildLutGroup(), 0, 1);
            return right;
        }

        private Control BuildGrainGroup()
        {
            grpGrain = new GroupBox();
            grpGrain.Text = lang.T("grain.av1_metadata");
            grpGrain.Dock = DockStyle.Fill;
            grpGrain.Margin = new Padding(0, 0, 0, 5);

            TableLayoutPanel shell = new TableLayoutPanel();
            shell.Dock = DockStyle.Fill; shell.Padding = new Padding(5); shell.ColumnCount = 1; shell.RowCount = 2;
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            cmbGrainMode = Combo(new string[] { lang.T("grain.mode.film"), lang.T("grain.mode.iso"), lang.T("grain.mode.table"), lang.T("grain.mode.digital"), lang.T("grain.mode.fgsim") }, 0);
            grainContent = new Panel(); grainContent.Dock = DockStyle.Fill;
            shell.Controls.Add(cmbGrainMode, 0, 0); shell.Controls.Add(grainContent, 0, 1);
            grpGrain.Controls.Add(shell);
            cmbGrainMode.SelectedIndexChanged += delegate
            {
                if (updatingGrainUi) return;
                if (cmbCodec != null && cmbCodec.SelectedIndex == 0) av1GrainModeIndex = Math.Max(0, cmbGrainMode.SelectedIndex);
                else pixelGrainModeIndex = Math.Max(0, cmbGrainMode.SelectedIndex);
                BuildGrainContent();
                UpdateHdrRouteStatus(GetSelectedMediaInfo());
            };
            RefreshGrainFileCaches();
            UpdateGrainForCodec();
            return grpGrain;
        }

        private void RefreshGrainFileCaches()
        {
            string oldTable = selectedAv1GrainTable;
            string oldPlate = selectedGrainPlatePath;
            av1GrainTableFiles.Clear();
            grainPlateFiles.Clear();

            av1GrainTableFiles.AddRange(GrainFileCatalogCore.ScanAv1Tables(Path.Combine(appRoot, "_AV1_Grain_Tables")));
            grainPlateFiles.AddRange(GrainFileCatalogCore.ScanGrainPlates(config.Get("GRAIN_ROOT")));

            if (!string.IsNullOrEmpty(oldTable) && av1GrainTableFiles.Exists(delegate(string p) { return string.Equals(p, oldTable, StringComparison.OrdinalIgnoreCase); }))
                selectedAv1GrainTable = oldTable;
            else if (av1GrainTableFiles.Count > 0)
                selectedAv1GrainTable = av1GrainTableFiles[0];
            else
                selectedAv1GrainTable = "";

            if (!string.IsNullOrEmpty(oldPlate) && grainPlateFiles.Exists(delegate(string p) { return string.Equals(p, oldPlate, StringComparison.OrdinalIgnoreCase); }))
                selectedGrainPlatePath = oldPlate;
            else if (grainPlateFiles.Count > 0)
                selectedGrainPlatePath = grainPlateFiles[0];
            else
                selectedGrainPlatePath = "";
        }

        private MediaProbeInfo GetAv1GrainSourceContext()
        {
            if (listFiles == null) return null;
            ListViewItem item = null;
            if (listFiles.SelectedItems.Count == 1) item = listFiles.SelectedItems[0];
            else if (listFiles.Items.Count > 0) item = listFiles.Items[0];
            if (item == null) return null;

            string path = item.Tag as string;
            if (string.IsNullOrEmpty(path)) return null;
            MediaProbeInfo info;
            return mediaProbeCache.TryGetValue(path, out info) ? info : null;
        }

        private List<string> GetFilteredAv1GrainTables(MediaProbeInfo source)
        {
            return Av1GrainTableSelectionCore.GetFilteredAv1GrainTables(av1GrainTableFiles,
                source != null ? source.Width : 0, source != null ? source.Height : 0, showAllAv1GrainTables);
        }

        private void RefreshAv1GrainTableForMedia()
        {
            if (cmbCodec == null || cmbGrainMode == null || grainContent == null) return;
            if ((cmbCodec.SelectedIndex == 0 || cmbCodec.SelectedIndex == 3) && cmbGrainMode.SelectedIndex == 2) BuildGrainContent();
        }

        private void BuildGrainContent()
        {
            if (grainContent == null || cmbGrainMode == null || cmbCodec == null) return;
            grainContent.Controls.Clear();
            cmbGrainFormat = null; cmbGrainStock = null; numGrainIso = null; chkGrainChroma = null; cmbGrainTable = null; chkShowAllAv1Tables = null; cmbGrainPlate = null;
            trackFilmGrainStrength = null; lblFilmGrainValue = null; btnFgsimTextureUpdate = null; lblFgsimTextureStatus = null;

            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill; t.ColumnCount = 2; t.RowCount = 4;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) t.RowStyles.Add(new RowStyle(SizeType.Percent, 25));

            bool av1 = cmbCodec.SelectedIndex == 0 || cmbCodec.SelectedIndex == 3;
            int mode = cmbGrainMode.SelectedIndex;
            if (mode < 0) mode = 0;

            if (av1 && mode == 0)
            {
                cmbGrainFormat = Combo(new string[] { "Classic35 · Super 35", "Modern35 · Full-frame", "16mm · Coarser", "Super8 · Heavy", "MaxMid · Synthetic" }, Math.Max(0, Math.Min(4, av1FormatIndex)));
                cmbGrainStock = Combo(new string[] { "Fujifilm Eterna 250D", "Fujifilm Eterna 500T", "Kodak Vision3 250D", "Kodak Vision3 200T" }, Math.Max(0, Math.Min(3, av1StockIndex)));
                cmbGrainStock.Enabled = cmbGrainFormat.SelectedIndex < 3;
                cmbGrainFormat.SelectedIndexChanged += delegate { av1FormatIndex = cmbGrainFormat.SelectedIndex; cmbGrainStock.Enabled = av1FormatIndex < 3; };
                cmbGrainStock.SelectedIndexChanged += delegate { av1StockIndex = cmbGrainStock.SelectedIndex; };
                AddLabeledRow(t, 0, lang.T("grain.format"), cmbGrainFormat);
                AddLabeledRow(t, 1, lang.T("grain.stock"), cmbGrainStock);
            }
            else if (av1 && mode == 1)
            {
                numGrainIso = new NumericUpDown(); numGrainIso.Minimum = 1; numGrainIso.Maximum = 1000000; numGrainIso.Value = Math.Max(1, Math.Min(1000000, av1IsoValue)); numGrainIso.Increment = 100; numGrainIso.ThousandsSeparator = true; numGrainIso.Dock = DockStyle.Fill; numGrainIso.Margin = new Padding(4, 5, 6, 5);
                chkGrainChroma = Check(lang.T("grain.chroma"), av1ChromaEnabled);
                numGrainIso.ValueChanged += delegate { av1IsoValue = (int)numGrainIso.Value; };
                chkGrainChroma.CheckedChanged += delegate { av1ChromaEnabled = chkGrainChroma.Checked; };
                AddLabeledRow(t, 0, lang.T("grain.iso"), numGrainIso);
                t.Controls.Add(chkGrainChroma, 1, 1);
            }
            else if (av1 && mode == 2)
            {
                MediaProbeInfo source = GetAv1GrainSourceContext();
                string preferredTier = Av1GrainTableSelectionCore.GetAv1GrainTierFromDimensions(source != null ? source.Width : 0, source != null ? source.Height : 0);
                displayedAv1GrainTableFiles.Clear();
                displayedAv1GrainTableFiles.AddRange(GetFilteredAv1GrainTables(source));

                TableLayoutPanel tablePanel = new TableLayoutPanel();
                tablePanel.Dock = DockStyle.Fill; tablePanel.Margin = Padding.Empty; tablePanel.ColumnCount = 3; tablePanel.RowCount = 1;
                tablePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                tablePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
                tablePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));

                cmbGrainTable = new ComboBox(); cmbGrainTable.DropDownStyle = ComboBoxStyle.DropDownList; cmbGrainTable.Dock = DockStyle.Fill; cmbGrainTable.Margin = new Padding(4, 5, 3, 5); cmbGrainTable.DropDownWidth = 560;
                int selectedIndex = -1;
                for (int i = 0; i < displayedAv1GrainTableFiles.Count; i++)
                {
                    string path = displayedAv1GrainTableFiles[i];
                    cmbGrainTable.Items.Add(GrainFileCatalogCore.RelativeDisplayPath(Path.Combine(appRoot, "_AV1_Grain_Tables"), path));
                    if (string.Equals(path, selectedAv1GrainTable, StringComparison.OrdinalIgnoreCase)) selectedIndex = i;
                }
                if (cmbGrainTable.Items.Count == 0)
                {
                    string emptyText;
                    if (!showAllAv1GrainTables && string.IsNullOrEmpty(preferredTier))
                        emptyText = UiText("等待视频分辨率以筛选颗粒表", "Waiting for video resolution to filter grain tables");
                    else if (!showAllAv1GrainTables)
                        emptyText = UiText(preferredTier + " 未发现可用颗粒表", "No usable " + preferredTier + " grain tables found");
                    else
                        emptyText = UiText("未发现 .tbl/.txt 颗粒表", "No .tbl/.txt grain tables found");
                    cmbGrainTable.Items.Add(emptyText);
                    cmbGrainTable.SelectedIndex = 0; cmbGrainTable.Enabled = false;
                    selectedAv1GrainTable = "";
                }
                else
                {
                    cmbGrainTable.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
                    selectedAv1GrainTable = displayedAv1GrainTableFiles[cmbGrainTable.SelectedIndex];
                    cmbGrainTable.SelectedIndexChanged += delegate
                    {
                        if (cmbGrainTable.SelectedIndex >= 0 && cmbGrainTable.SelectedIndex < displayedAv1GrainTableFiles.Count)
                            selectedAv1GrainTable = displayedAv1GrainTableFiles[cmbGrainTable.SelectedIndex];
                    };
                }

                Button refreshTable = new Button(); refreshTable.Text = "↻"; refreshTable.Dock = DockStyle.Fill; refreshTable.Margin = new Padding(0, 4, 0, 4);
                refreshTable.Click += delegate { RefreshGrainFileCaches(); BuildGrainContent(); };
                chkShowAllAv1Tables = new CheckBox(); chkShowAllAv1Tables.Text = ""; chkShowAllAv1Tables.Checked = showAllAv1GrainTables; chkShowAllAv1Tables.Dock = DockStyle.Fill; chkShowAllAv1Tables.Margin = new Padding(6, 4, 0, 4);
                string tableTip = string.IsNullOrEmpty(preferredTier)
                    ? UiText("默认按当前视频分辨率筛选；勾选后显示全部颗粒表。", "By default tables are filtered to the current video resolution; check to show all tables.")
                    : UiText("当前优先：" + preferredTier + "；勾选后显示全部颗粒表。", "Current preference: " + preferredTier + "; check to show all tables.");
                lutToolTip.SetToolTip(chkShowAllAv1Tables, tableTip);
                lutToolTip.SetToolTip(refreshTable, UiText("重新扫描颗粒表", "Rescan grain tables"));
                chkShowAllAv1Tables.CheckedChanged += delegate { showAllAv1GrainTables = chkShowAllAv1Tables.Checked; BuildGrainContent(); };

                tablePanel.Controls.Add(cmbGrainTable, 0, 0); tablePanel.Controls.Add(refreshTable, 1, 0); tablePanel.Controls.Add(chkShowAllAv1Tables, 2, 0);
                AddLabeledRow(t, 0, "Grain Table", tablePanel);
                Label note = MutedLabel(showAllAv1GrainTables
                    ? UiText("显示全部分辨率；最接近当前视频的颗粒表优先。", "Showing all resolutions; tables closest to the current video are listed first.")
                    : (string.IsNullOrEmpty(preferredTier)
                        ? UiText("选择并完成视频探测后自动按分辨率筛选。", "Select a video and complete probing to filter by resolution automatically.")
                        : UiText("当前按 " + preferredTier + " 自动筛选。", "Currently filtered automatically to " + preferredTier + ".")));
                t.Controls.Add(note, 0, 1); t.SetColumnSpan(note, 2);
            }
            else if (!av1 && mode == 1)
            {
                cmbGrainPlate = new ComboBox(); cmbGrainPlate.DropDownStyle = ComboBoxStyle.DropDownList; cmbGrainPlate.Dock = DockStyle.Fill; cmbGrainPlate.Margin = new Padding(4, 5, 6, 5); cmbGrainPlate.DropDownWidth = 520;
                string grainRoot = config.Get("GRAIN_ROOT");
                int selectedIndex = -1;
                for (int i = 0; i < grainPlateFiles.Count; i++)
                {
                    string path = grainPlateFiles[i];
                    cmbGrainPlate.Items.Add(GrainFileCatalogCore.RelativeDisplayPath(grainRoot, path));
                    if (string.Equals(path, selectedGrainPlatePath, StringComparison.OrdinalIgnoreCase)) selectedIndex = i;
                }
                if (cmbGrainPlate.Items.Count == 0)
                {
                    cmbGrainPlate.Items.Add(UiText("颗粒目录中未发现 .mov", "No .mov grain plates found"));
                    cmbGrainPlate.SelectedIndex = 0; cmbGrainPlate.Enabled = false;
                }
                else
                {
                    cmbGrainPlate.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
                    selectedGrainPlatePath = grainPlateFiles[cmbGrainPlate.SelectedIndex];
                    cmbGrainPlate.SelectedIndexChanged += delegate
                    {
                        if (cmbGrainPlate.SelectedIndex >= 0 && cmbGrainPlate.SelectedIndex < grainPlateFiles.Count)
                            selectedGrainPlatePath = grainPlateFiles[cmbGrainPlate.SelectedIndex];
                    };
                }
                AddLabeledRow(t, 0, UiText("颗粒文件", "Grain Plate"), cmbGrainPlate);
                BuildUnifiedGrainSlider(t, 1, "PLATE");
            }
            else if ((av1 && mode == 3) || (!av1 && mode == 0))
            {
                BuildUnifiedGrainSlider(t, 0, "DIGITAL");
                Label note = MutedLabel(lang.T("digital_grain.tip"));
                t.Controls.Add(note, 0, 1); t.SetColumnSpan(note, 2);
            }
            else
            {
                BuildUnifiedGrainSlider(t, 0, "FGSIM");
                if (cmbCodec.SelectedIndex == 1)
                {
                    Label note = MutedLabel(lang.T("grain.fgsim_hint"));
                    t.Controls.Add(note, 0, 1); t.SetColumnSpan(note, 2);
                }
                btnFgsimTextureUpdate = new Button();
                btnFgsimTextureUpdate.Text = UiText("在线更新纹理", "Update texture online");
                btnFgsimTextureUpdate.Dock = DockStyle.Fill;
                btnFgsimTextureUpdate.Margin = new Padding(4, 2, 6, 2);
                btnFgsimTextureUpdate.Click += delegate { StartFgsimTextureUpdate(); };
                t.Controls.Add(btnFgsimTextureUpdate, 0, 2); t.SetColumnSpan(btnFgsimTextureUpdate, 2);
                lblFgsimTextureStatus = MutedLabel(UiText("默认使用本地纹理", "Using local texture by default"));
                lblFgsimTextureStatus.AutoEllipsis = true;
                t.Controls.Add(lblFgsimTextureStatus, 0, 3); t.SetColumnSpan(lblFgsimTextureStatus, 2);
            }
            grainContent.Controls.Add(t);
            UpdateFgsimBitrateUi();
        }

        private void StartFgsimTextureUpdate()
        {
            if (bridgeTaskCoordinator != null && bridgeTaskCoordinator.IsActive)
            {
                MessageBox.Show(this, UiText("编码任务运行时不能更新纹理。", "Texture update is unavailable during encoding."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string ffmpeg = Path.Combine(config.Get("FFMPEG_DIR"), "ffmpeg.exe");
            if (!File.Exists(ffmpeg))
            {
                MessageBox.Show(this, UiText("找不到 ffmpeg.exe，请先在设置中配置 FFmpeg。", "ffmpeg.exe is missing. Configure FFmpeg first."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string proxyMode = config.Get("NETWORK_PROXY_MODE");
            string proxyUrl = config.Get("NETWORK_PROXY_URL");
            Button button = btnFgsimTextureUpdate;
            Label status = lblFgsimTextureStatus;
            if (button == null || status == null) return;
            button.Enabled = false;
            status.Text = UiText("正在通过当前网络代理下载纹理…", "Downloading texture using the configured proxy...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string detail;
                bool ok = FgsimTextureCore.TryUpdateOnline(appRoot, ffmpeg, proxyMode, proxyUrl, out detail);
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (button.IsDisposed || status.IsDisposed) return;
                        button.Enabled = true;
                        status.Text = detail;
                        status.ForeColor = ok ? Color.FromArgb(40, 135, 90) : Color.FromArgb(180, 70, 55);
                    });
                }
                catch (InvalidOperationException) { }
            });
        }

        private void BuildUnifiedGrainSlider(TableLayoutPanel t, int row, string kind)
        {
            trackFilmGrainStrength = new TrackBar(); trackFilmGrainStrength.AutoSize = false; trackFilmGrainStrength.Height = 30; trackFilmGrainStrength.Dock = DockStyle.Fill; trackFilmGrainStrength.Margin = new Padding(0, 2, 6, 2); trackFilmGrainStrength.TickStyle = TickStyle.BottomRight;
            if (kind == "DIGITAL") { trackFilmGrainStrength.Minimum = 10; trackFilmGrainStrength.Maximum = 100; trackFilmGrainStrength.Value = Math.Max(10, Math.Min(100, proceduralStrength)); trackFilmGrainStrength.TickFrequency = 10; }
            else if (kind == "FGSIM") { trackFilmGrainStrength.Minimum = 0; trackFilmGrainStrength.Maximum = 2; trackFilmGrainStrength.Value = Math.Max(0, Math.Min(2, fgsimPresetIndex)); trackFilmGrainStrength.TickFrequency = 1; }
            else { trackFilmGrainStrength.Minimum = 0; trackFilmGrainStrength.Maximum = 3; trackFilmGrainStrength.Value = Math.Max(0, Math.Min(3, scannedGrainStrengthIndex)); trackFilmGrainStrength.TickFrequency = 1; }
            lblFilmGrainValue = new Label(); lblFilmGrainValue.AutoSize = false; lblFilmGrainValue.Dock = DockStyle.Fill; lblFilmGrainValue.Margin = new Padding(4, 0, 0, 2); lblFilmGrainValue.TextAlign = ContentAlignment.MiddleLeft;
            TableLayoutPanel slider = new TableLayoutPanel(); slider.Dock = DockStyle.Fill; slider.Margin = Padding.Empty; slider.ColumnCount = 2; slider.RowCount = 1; slider.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); slider.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112)); slider.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            slider.Controls.Add(trackFilmGrainStrength, 0, 0); slider.Controls.Add(lblFilmGrainValue, 1, 0);
            AddLabeledRow(t, row, lang.T("grain.strength"), slider);
            trackFilmGrainStrength.ValueChanged += delegate
            {
                if (kind == "DIGITAL") proceduralStrength = trackFilmGrainStrength.Value;
                else if (kind == "FGSIM") fgsimPresetIndex = trackFilmGrainStrength.Value;
                else scannedGrainStrengthIndex = trackFilmGrainStrength.Value;
                UpdateGrainStrengthLabel(kind);
            };
            UpdateGrainStrengthLabel(kind);
        }

        private void UpdateGrainStrengthLabel(string kind)
        {
            if (trackFilmGrainStrength == null || lblFilmGrainValue == null) return;
            if (kind == "DIGITAL") lblFilmGrainValue.Text = (trackFilmGrainStrength.Value / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
            else if (kind == "FGSIM")
            {
                string[] names = new string[] { "Light / 0.10", "Medium / 0.20", "Heavy / 0.30" };
                lblFilmGrainValue.Text = names[Math.Max(0, Math.Min(2, trackFilmGrainStrength.Value))];
            }
            else
            {
                string[] names = new string[] { "Light · 65%", "Natural · 75%", "Strong · 85%", "Full · 100%" };
                lblFilmGrainValue.Text = names[Math.Max(0, Math.Min(3, trackFilmGrainStrength.Value))];
            }
        }

        private void UpdateGrainForCodec()
        {
            if (grpGrain == null || cmbCodec == null || cmbGrainMode == null) return;
            bool av1 = cmbCodec.SelectedIndex == 0 || cmbCodec.SelectedIndex == 3;
            updatingGrainUi = true;
            try
            {
                cmbGrainMode.Items.Clear();
                if (av1)
                {
                    cmbGrainMode.Items.AddRange(new object[] { lang.T("grain.mode.film"), lang.T("grain.mode.iso"), lang.T("grain.mode.table"), lang.T("grain.mode.digital"), lang.T("grain.mode.fgsim") });
                    cmbGrainMode.SelectedIndex = Math.Max(0, Math.Min(4, av1GrainModeIndex));
                    grpGrain.Text = cmbCodec.SelectedIndex == 3 ? lang.T("grain.av1_copy") : lang.T("grain.av1_metadata");
                }
                else
                {
                    cmbGrainMode.Items.AddRange(new object[] { lang.T("grain.mode.digital"), UiText("扫描颗粒文件", "Scanned Grain Plate"), lang.T("grain.mode.fgsim") });
                    if (grainPlateFiles.Count == 0 && pixelGrainModeIndex == 1) pixelGrainModeIndex = 0;
                    cmbGrainMode.SelectedIndex = Math.Max(0, Math.Min(2, pixelGrainModeIndex));
                    grpGrain.Text = cmbCodec.SelectedIndex == 1 ? lang.T("grain.hevc") : lang.T("grain.x264");
                }
            }
            finally { updatingGrainUi = false; }
            BuildGrainContent();
        }

        private Control BuildLutGroup()
        {
            GroupBox box = new GroupBox();
            grpLut = box;
            box.Text = lang.T("lut.group");
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(0, 5, 0, 0);

            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill; t.Padding = new Padding(6, 5, 6, 5); t.ColumnCount = 4; t.RowCount = 5;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            box.Controls.Add(t);

            chkLut = Check(lang.T("lut.enable"), false);
            btnLutGallery = new Button(); btnLutGallery.Text = lang.T("button.open_lut_gallery"); btnLutGallery.Dock = DockStyle.Fill; btnLutGallery.Margin = new Padding(3);
            btnColorCorrection = new Button(); btnColorCorrection.Text = lang.T("button.color_correction"); btnColorCorrection.Dock = DockStyle.Fill; btnColorCorrection.Margin = new Padding(3);
            Button clear = new Button(); clear.Text = lang.T("button.clear_lut"); clear.Dock = DockStyle.Fill; clear.Margin = new Padding(3);
            btnLutGallery.Click += delegate { OpenLutGallery(); };
            btnColorCorrection.Click += delegate { OpenColorCorrection(); };
            clear.Click += delegate { ClearSelectedLut(); };
            t.Controls.Add(chkLut, 0, 0); t.Controls.Add(btnLutGallery, 1, 0); t.Controls.Add(btnColorCorrection, 2, 0); t.Controls.Add(clear, 3, 0);

            Label recentLabel = MidLabel(lang.T("lut.recent"));
            cmbRecentLut = new ComboBox(); cmbRecentLut.DropDownStyle = ComboBoxStyle.DropDownList; cmbRecentLut.Dock = DockStyle.Fill; cmbRecentLut.Margin = new Padding(4, 5, 6, 5); cmbRecentLut.DropDownWidth = 440; cmbRecentLut.MaxDropDownItems = 25;
            cmbRecentLut.SelectedIndexChanged += delegate { SelectLutFromCombo(cmbRecentLut, "Recent"); };
            t.Controls.Add(recentLabel, 0, 1); t.Controls.Add(cmbRecentLut, 1, 1); t.SetColumnSpan(cmbRecentLut, 3);

            Label favoriteLabel = MidLabel(lang.T("lut.favorite"));
            cmbFavoriteLut = new ComboBox(); cmbFavoriteLut.DropDownStyle = ComboBoxStyle.DropDownList; cmbFavoriteLut.Dock = DockStyle.Fill; cmbFavoriteLut.Margin = new Padding(4, 5, 6, 5); cmbFavoriteLut.DropDownWidth = 440; cmbFavoriteLut.MaxDropDownItems = 25;
            cmbFavoriteLut.SelectedIndexChanged += delegate { SelectLutFromCombo(cmbFavoriteLut, "Favorite"); };
            t.Controls.Add(favoriteLabel, 0, 2); t.Controls.Add(cmbFavoriteLut, 1, 2); t.SetColumnSpan(cmbFavoriteLut, 3);

            TableLayoutPanel preview = new TableLayoutPanel(); preview.Dock = DockStyle.Fill; preview.Margin = new Padding(4, 3, 4, 2); preview.ColumnCount = 1; preview.RowCount = 2;
            preview.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); preview.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            picLutPreview = new PictureBox(); picLutPreview.Size = new Size(240, 135); picLutPreview.Anchor = AnchorStyles.None; picLutPreview.Margin = Padding.Empty; picLutPreview.BackColor = Color.Black; picLutPreview.BorderStyle = BorderStyle.None; picLutPreview.SizeMode = PictureBoxSizeMode.Zoom;
            lblSelectedLut = new Label(); lblSelectedLut.Text = lang.T("lut.none"); lblSelectedLut.Dock = DockStyle.Fill; lblSelectedLut.AutoEllipsis = true; lblSelectedLut.TextAlign = ContentAlignment.MiddleLeft; lblSelectedLut.ForeColor = ColorMuted; lblSelectedLut.Padding = new Padding(4, 0, 4, 0);
            preview.Controls.Add(picLutPreview, 0, 0); preview.Controls.Add(lblSelectedLut, 0, 1);
            t.Controls.Add(preview, 0, 3); t.SetColumnSpan(preview, 4);

            lblLutStrengthTitle = MidLabel(lang.T("lut.strength"));
            trackLutStrength = new TrackBar(); trackLutStrength.Minimum = 0; trackLutStrength.Maximum = 3; trackLutStrength.Value = 2; trackLutStrength.TickStyle = TickStyle.BottomRight; trackLutStrength.Dock = DockStyle.Fill; trackLutStrength.Margin = Padding.Empty; trackLutStrength.Enabled = false;
            lblLutStrength = new Label(); lblLutStrength.Text = "75%"; lblLutStrength.Dock = DockStyle.Fill; lblLutStrength.TextAlign = ContentAlignment.MiddleLeft; lblLutStrength.Enabled = false;
            chkLut.CheckedChanged += delegate { UpdateLutUi(); UpdateHdrRouteStatus(GetSelectedMediaInfo()); };
            trackLutStrength.ValueChanged += delegate
            {
                int[] values = new int[] { 25, 50, 75, 100 };
                lblLutStrength.Text = values[trackLutStrength.Value].ToString(CultureInfo.InvariantCulture) + "%";
            };
            t.Controls.Add(lblLutStrengthTitle, 0, 4); t.Controls.Add(trackLutStrength, 1, 4); t.SetColumnSpan(trackLutStrength, 2); t.Controls.Add(lblLutStrength, 3, 4);
            return box;
        }

        private Control BuildLogArea()
        {
            GroupBox box = new GroupBox();
            box.Text = lang.T("log.group");
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(10, 2, 10, 4);

            TableLayoutPanel layout = new TableLayoutPanel(); layout.Dock = DockStyle.Fill; layout.RowCount = 2; layout.ColumnCount = 1;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            box.Controls.Add(layout);

            TableLayoutPanel toolbar = new TableLayoutPanel(); toolbar.Dock = DockStyle.Fill; toolbar.ColumnCount = 6; toolbar.RowCount = 1; toolbar.Padding = new Padding(3, 0, 0, 0);
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            lblRunStage = new Label(); lblRunStage.Text = lang.T("log.waiting"); lblRunStage.Dock = DockStyle.Fill; lblRunStage.TextAlign = ContentAlignment.MiddleLeft; lblRunStage.AutoEllipsis = true;
            lblRunMetric = new Label(); lblRunMetric.Text = "fps: —   speed: —"; lblRunMetric.Dock = DockStyle.Fill; lblRunMetric.TextAlign = ContentAlignment.MiddleLeft; lblRunMetric.ForeColor = ColorMuted; lblRunMetric.AutoEllipsis = true;
            Button copy = new Button(); copy.Text = lang.T("button.copy_log"); copy.Size = new Size(78, 24); copy.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Button clear = new Button(); clear.Text = lang.T("button.clear_log"); clear.Size = new Size(78, 24); clear.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            copy.Click += delegate { if (log.TextLength > 0) Clipboard.SetText(log.Text); };
            clear.Click += delegate { log.Clear(); };
            toolbar.Controls.Add(lblRunStage, 0, 0); toolbar.Controls.Add(lblRunMetric, 1, 0); toolbar.Controls.Add(copy, 2, 0); toolbar.Controls.Add(clear, 3, 0);
            Button openLog = new Button(); openLog.Text = UiText("完整日志", "Full log"); openLog.Size = new Size(88, 24);
            Button openFolder = new Button(); openFolder.Text = UiText("日志目录", "Log folder"); openFolder.Size = new Size(88, 24);
            openLog.Click += delegate { OpenFullLog(false); };
            openFolder.Click += delegate { OpenFullLog(true); };
            ContextMenuStrip logFolderMenu = new ContextMenuStrip();
            ToolStripMenuItem clearLogFolder = new ToolStripMenuItem(UiText("清空日志目录", "Clear log folder"));
            clearLogFolder.Click += delegate { ClearLogFolder(); };
            logFolderMenu.Items.Add(clearLogFolder);
            openFolder.ContextMenuStrip = logFolderMenu;
            toolbar.Controls.Add(openLog, 4, 0); toolbar.Controls.Add(openFolder, 5, 0);
            layout.Controls.Add(toolbar, 0, 0);

            log = new TextBox(); log.Multiline = true; log.ScrollBars = ScrollBars.Both; log.MaxLength = 0; log.Dock = DockStyle.Fill; log.ReadOnly = true; log.WordWrap = false; log.BackColor = Color.FromArgb(28, 30, 34); log.ForeColor = Color.Gainsboro; log.Font = new Font("Consolas", 9f); log.BorderStyle = BorderStyle.FixedSingle;
            fullLog = new FullLogFileCore(appRoot, "Session", GetLogEntryLimit());
            AppendLog("Film Grain Studio .NET Preview NATIVE_P1" + Environment.NewLine);
            AppendLog("Baseline: v4.8.12 Stable / InspectLogFix TaskLog T9P3HZ" + Environment.NewLine);
            AppendLog("App root: " + appRoot + Environment.NewLine);
            AppendLog(".NET execution modules: Native AV1 grain inspection, Native codecs, and Legacy Bridge fallback." + Environment.NewLine);
            AppendLog("[Full log] " + fullLog.FilePath + Environment.NewLine);
            if (!string.IsNullOrEmpty(fullLog.Error)) AppendVisibleLog("[Full log ERROR] " + fullLog.Error + Environment.NewLine);
            layout.Controls.Add(log, 0, 1);
            return box;
        }

        private Control BuildFooter()
        {
            Panel footer = new Panel(); footer.Dock = DockStyle.Fill; footer.BackColor = ColorSubtle;
            lblStatus = new Label(); lblStatus.AutoSize = false; lblStatus.Size = new Size(560, 30); lblStatus.Location = new Point(14, 14); lblStatus.TextAlign = ContentAlignment.MiddleLeft; footer.Controls.Add(lblStatus);
            progressRun = new ProgressBar(); progressRun.Style = ProgressBarStyle.Blocks; progressRun.Minimum = 0; progressRun.Maximum = 1000; progressRun.Value = 0; progressRun.Size = new Size(220, 20); progressRun.Anchor = AnchorStyles.Top | AnchorStyles.Right; footer.Controls.Add(progressRun);
            btnCancelTask = new Button(); btnCancelTask.Text = lang.T("button.cancel_task"); btnCancelTask.Enabled = false; btnCancelTask.Size = new Size(94, 34); btnCancelTask.Anchor = AnchorStyles.Top | AnchorStyles.Right; footer.Controls.Add(btnCancelTask);
            chkNativeBackend = new CheckBox(); chkNativeBackend.Text = UiText("原生 .NET（实验）", "Native .NET (experimental)"); chkNativeBackend.AutoSize = true; chkNativeBackend.Checked = true; chkNativeBackend.Location = new Point(575, 20); footer.Controls.Add(chkNativeBackend);
            chkNativeBackend.CheckedChanged += delegate { UpdateHdrRouteStatus(GetSelectedMediaInfo()); };
            btnStart = new Button(); btnStart.Text = lang.T("button.start_encode"); btnStart.ForeColor = Color.White; btnStart.BackColor = ColorAccent; btnStart.FlatStyle = FlatStyle.Flat; btnStart.FlatAppearance.BorderSize = 0; btnStart.Size = new Size(126, 36); btnStart.Anchor = AnchorStyles.Top | AnchorStyles.Right; footer.Controls.Add(btnStart);
            btnStart.Click += delegate { StartBridgeExecution(); };
            btnCancelTask.Click += delegate { CancelBridgeExecution(); };
            footer.Resize += delegate
            {
                btnStart.Left = footer.ClientSize.Width - btnStart.Width - 14; btnStart.Top = 10;
                btnCancelTask.Left = btnStart.Left - btnCancelTask.Width - 9; btnCancelTask.Top = 11;
                progressRun.Left = btnCancelTask.Left - progressRun.Width - 12; progressRun.Top = 19;
            };
            footer.PerformLayout();
            return footer;
        }

        private Control BuildStatusStrip()
        {
            StatusStrip strip = new StatusStrip(); strip.Dock = DockStyle.Fill; strip.SizingGrip = false; strip.BackColor = ColorSubtle; strip.Padding = new Padding(8, 1, 8, 1);
            statusHardware = new ToolStripStatusLabel(); statusHardware.AutoSize = false; statusHardware.Width = 1; statusHardware.Spring = true; statusHardware.TextAlign = ContentAlignment.MiddleLeft; statusHardware.ForeColor = ColorMuted; statusHardware.Text = lang.T("hardware.detecting");
            ToolStripStatusLabel version = new ToolStripStatusLabel(); version.Spring = false; version.TextAlign = ContentAlignment.MiddleRight; version.ForeColor = ColorMuted; version.Text = "v4.8.11 .NET Preview"; version.Margin = new Padding(12, 0, 0, 0);
            strip.Items.Add(statusHardware); strip.Items.Add(version); return strip;
        }

        private ComboBox Combo(string[] items, int index)
        {
            ComboBox c = new ComboBox(); c.DropDownStyle = ComboBoxStyle.DropDownList; c.Dock = DockStyle.Fill; c.Margin = new Padding(4, 5, 6, 5);
            foreach (string item in items) c.Items.Add(item);
            if (c.Items.Count > 0) c.SelectedIndex = Math.Max(0, Math.Min(index, c.Items.Count - 1));
            return c;
        }

        private ComboBox ComboEditable(string[] items, string text)
        {
            ComboBox c = new ComboBox(); c.DropDownStyle = ComboBoxStyle.DropDown; c.Dock = DockStyle.Fill; c.Margin = new Padding(4, 5, 3, 5);
            foreach (string item in items) c.Items.Add(item); c.Text = text; return c;
        }

        private CheckBox Check(string text, bool value)
        {
            CheckBox c = new CheckBox(); c.Text = text; c.Checked = value; c.AutoSize = true; c.Dock = DockStyle.Fill; c.Margin = new Padding(4, 7, 3, 3); return c;
        }

        private Label MidLabel(string text)
        {
            Label l = new Label(); l.Text = text; l.Dock = DockStyle.Fill; l.TextAlign = ContentAlignment.MiddleLeft; l.Padding = new Padding(4, 0, 0, 0); return l;
        }

        private Label MutedLabel(string text)
        {
            Label l = new Label(); l.Text = text; l.Dock = DockStyle.Fill; l.TextAlign = ContentAlignment.TopLeft; l.ForeColor = ColorMuted; l.Padding = new Padding(4, 6, 4, 0); return l;
        }

        private void AddLabeledRow(TableLayoutPanel table, int row, string text, Control control)
        {
            Label l = MidLabel(text); table.Controls.Add(l, 0, row); table.Controls.Add(control, 1, row);
        }

        private void TryLoadIcon()
        {
            string path = Path.Combine(appRoot, "Utils", "FGS.ico");
            if (!File.Exists(path)) path = Path.Combine(appRoot, "images", "FGS.ico");
            if (File.Exists(path))
            {
                try { Icon = new Icon(path); } catch { }
            }
        }

        private void AddFilesWithDialog()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Multiselect = true;
                dlg.Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.m2ts;*.mts;*.ts;*.webm;*.mpg;*.mpeg;*.vob|All files|*.*";
                if (dlg.ShowDialog(this) == DialogResult.OK) AddFiles(dlg.FileNames);
            }
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null) AddFiles(files);
        }

        private void AddFiles(IEnumerable<string> paths)
        {
            HashSet<string> existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ListViewItem item in listFiles.Items) if (item.Tag is string) existing.Add((string)item.Tag);
            foreach (string path in paths)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || existing.Contains(path)) continue;
                FileInfo fi = new FileInfo(path);
                ListViewItem item = new ListViewItem(fi.Name);
                item.SubItems.Add(FormatSize(fi.Length));
                item.SubItems.Add(fi.DirectoryName ?? string.Empty);
                item.Tag = fi.FullName;
                item.ToolTipText = fi.FullName;
                listFiles.Items.Add(item);
                existing.Add(path);
            }
            UpdateStatusCount();
            if (listFiles.Items.Count > 0 && listFiles.SelectedItems.Count == 0) listFiles.Items[0].Selected = true;
            UpdateNoReencodeAvailability();
        }

        private void RemoveSelected()
        {
            while (listFiles.SelectedItems.Count > 0) listFiles.Items.Remove(listFiles.SelectedItems[0]);
            UpdateSelectedInfo();
            UpdateStatusCount();
        }

        private void UpdateSelectedInfo()
        {
            if (mediaProbeCore != null) mediaProbeCore.Cancel();
            StopAv1GrainInspect();
            RefreshAv1GrainTableForMedia();
            UpdateNoReencodeAvailability();

            if (listFiles.SelectedItems.Count == 0)
            {
                SetMediaInfoText(lang.T("input.info_prompt"), true);
                UpdateMediaDrivenUi(null);
                UpdateBitrateDisplays();
                return;
            }
            if (listFiles.SelectedItems.Count > 1)
            {
                SetMediaInfoText(LF("media.multi_selected", listFiles.SelectedItems.Count), true);
                UpdateMediaDrivenUi(null);
                UpdateBitrateDisplays();
                return;
            }

            string path = listFiles.SelectedItems[0].Tag as string;
            if (string.IsNullOrEmpty(path)) return;
            if (!File.Exists(path))
            {
                SetMediaInfoText(lang.T("media.file_missing"), true);
                UpdateMediaDrivenUi(null);
                UpdateBitrateDisplays();
                return;
            }

            MediaProbeInfo cached;
            if (mediaProbeCache.TryGetValue(path, out cached))
            {
                string summary = new MediaSummaryCore(lang).FormatMediaSummary(cached);
                SetMediaInfoText(summary, false);
                UpdateMediaDrivenUi(cached);
                RefreshAv1GrainTableForMedia();
                UpdateNoReencodeAvailability();
                UpdateBitrateDisplays();
                if (IsAv1Media(cached)) StartAv1GrainInspect(path, summary);
                return;
            }

            string ffprobe = Path.Combine(config.Get("FFMPEG_DIR"), "ffprobe.exe");
            if (!File.Exists(ffprobe))
            {
                SetMediaInfoText(LF("media.ffprobe_missing", ffprobe), true);
                UpdateMediaDrivenUi(null);
                UpdateBitrateDisplays();
                return;
            }

            SetMediaInfoText(lang.T("media.reading"), true);
            mediaProbeCore.Start(path, ffprobe);
        }

        private void OnMediaProbeCompleted(MediaProbeResult result)
        {
            if (result == null || IsDisposed || Disposing || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (result == null || IsDisposed || Disposing) return;
                    if (listFiles.SelectedItems.Count != 1) return;
                    string currentPath = listFiles.SelectedItems[0].Tag as string;
                    if (!string.Equals(currentPath, result.PathValue, StringComparison.OrdinalIgnoreCase)) return;

                    if (result.Info != null)
                    {
                        mediaProbeCache[result.PathValue] = result.Info;
                        string summary = new MediaSummaryCore(lang).FormatMediaSummary(result.Info);
                        SetMediaInfoText(summary, false);
                        UpdateMediaDrivenUi(result.Info);
                        RefreshAv1GrainTableForMedia();
                        UpdateNoReencodeAvailability();
                        UpdateBitrateDisplays();
                        if (IsAv1Media(result.Info)) StartAv1GrainInspect(result.PathValue, summary);
                        return;
                    }

                    string detail;
                    if (result.StartReturnedFalse)
                    {
                        detail = lang.T("media.ffprobe_start_failed");
                    }
                    else if (!string.IsNullOrEmpty(result.FailureMessage))
                    {
                        detail = result.FailureMessage;
                    }
                    else
                    {
                        detail = FirstLine(result.StandardError);
                        if (string.IsNullOrEmpty(detail)) detail = LF("media.ffprobe_code", result.ExitCode);
                    }
                    SetMediaInfoText(LF("media.read_failed", detail), true);
                    UpdateMediaDrivenUi(null);
                    UpdateBitrateDisplays();
                });
            }
            catch { }
        }

        private static bool IsAv1Media(MediaProbeInfo info)
        {
            return info != null && string.Equals(info.VideoCodec, "av1", StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateNoReencodeAvailability()
        {
            if (cmbCodec == null || listFiles == null) return;
            bool eligible = false;
            if (listFiles.Items.Count == 1)
            {
                string path = listFiles.Items[0].Tag as string;
                MediaProbeInfo info;
                if (!string.IsNullOrEmpty(path) && mediaProbeCache.TryGetValue(path, out info))
                    eligible = IsAv1Media(info);
            }

            string itemText = lang.T("codec.no_reencode");
            bool hasItem = cmbCodec.Items.Count >= 4 && string.Equals(Convert.ToString(cmbCodec.Items[3], CultureInfo.InvariantCulture), itemText, StringComparison.Ordinal);
            if (eligible && !hasItem)
            {
                cmbCodec.Items.Add(itemText);
            }
            else if (!eligible && hasItem)
            {
                if (cmbCodec.SelectedIndex == 3) cmbCodec.SelectedIndex = 0;
                cmbCodec.Items.RemoveAt(3);
            }
        }

        private string GetX264SettingsLabel()
        {
            string presetValue = config.Get("X264_PRESET");
            string presetLabel = string.Equals(presetValue, "medium", StringComparison.OrdinalIgnoreCase) ? "Medium" :
                (string.Equals(presetValue, "slow", StringComparison.OrdinalIgnoreCase) ? "Slow" : "Faster");
            string rateValue = config.Get("X264_RATE_MODE");
            string passLabel = string.Equals(rateValue, "3PASS", StringComparison.OrdinalIgnoreCase) ? lang.T("advanced.vbr3") :
                (string.Equals(rateValue, "2PASS", StringComparison.OrdinalIgnoreCase) ? lang.T("advanced.vbr2") : lang.T("speed.vbr1"));

            return "x264 · " + presetLabel + " / tune grain / " + passLabel;
        }

        private void UpdateSpeedChoices()
        {
            if (cmbCodec == null || cmbSpeed == null) return;
            if (lblUploadX264Settings != null)
            {
                lblUploadX264Settings.Text = GetX264SettingsLabel();
                lblUploadX264Settings.Visible = cmbCodec.SelectedIndex == 0 || cmbCodec.SelectedIndex == 1;
            }

            if (cmbCodec.SelectedIndex == 2)
            {
                cmbSpeed.BeginUpdate();
                try
                {
                    cmbSpeed.Items.Clear();
                    cmbSpeed.Items.Add(GetX264SettingsLabel());
                    cmbSpeed.SelectedIndex = 0;
                }
                finally { cmbSpeed.EndUpdate(); }
                cmbSpeed.Enabled = false;
                return;
            }

            string currentText = Convert.ToString(cmbSpeed.SelectedItem, CultureInfo.InvariantCulture);
            int selectedIndex = currentText.StartsWith("Standard", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            bool allowUhq = cmbCodec.SelectedIndex == 0 && hardwareCapsReady && hardwareCaps != null && hardwareCaps.Av1UhqAvailable;
            if (allowUhq && currentText.StartsWith("UHQ", StringComparison.OrdinalIgnoreCase)) selectedIndex = 2;

            cmbSpeed.BeginUpdate();
            try
            {
                cmbSpeed.Items.Clear();
                cmbSpeed.Items.Add(lang.T("speed.fast"));
                cmbSpeed.Items.Add(lang.T("speed.standard"));
                if (allowUhq) cmbSpeed.Items.Add(lang.T("speed.uhq"));
                cmbSpeed.SelectedIndex = Math.Max(0, Math.Min(cmbSpeed.Items.Count - 1, selectedIndex));
            }
            finally { cmbSpeed.EndUpdate(); }
            cmbSpeed.Enabled = cmbCodec.SelectedIndex != 3;
        }

        private void UpdateSfeUi()
        {
            if (chkSfe == null) return;

            bool ready = hardwareCapsReady && hardwareCaps != null;
            int maxEngines = ready ? Math.Max(1, hardwareCaps.Av1SplitEncodeMaxEngines) : 1;
            bool gravSfeCompatible = ready && hardwareCaps.Grav1synthSfeCompatible;
            string gravVersion = ready && !string.IsNullOrWhiteSpace(hardwareCaps.Grav1synthVersion)
                ? hardwareCaps.Grav1synthVersion
                : lang.T("hardware.not_detected");

            chkSfe.Text = LF("sfe.engines", maxEngines);

            bool isAv1 = cmbCodec != null && cmbCodec.SelectedIndex == 0;
            bool supportedSpeed = cmbSpeed != null && (cmbSpeed.SelectedIndex == 1 || cmbSpeed.SelectedIndex == 2);
            bool canUse = ready && maxEngines >= 2 && gravSfeCompatible && isAv1 && supportedSpeed;

            if (!canUse)
            {
                chkSfe.Checked = false;
                chkSfe.Enabled = false;
            }
            else
            {
                chkSfe.Enabled = true;
            }

            if (maxEngines < 2)
                sfeToolTip.SetToolTip(chkSfe, lang.T("tooltip.sfe_unavailable"));
            else if (!gravSfeCompatible)
                sfeToolTip.SetToolTip(chkSfe, LF("tooltip.sfe_version", gravVersion));
            else
                sfeToolTip.SetToolTip(chkSfe, LF("tooltip.sfe_ready", maxEngines, gravVersion));
        }

        private void UpdateNoReencodeUi()
        {
            if (cmbCodec == null) return;
            bool active = cmbCodec.SelectedIndex == 3;
            if (active)
            {
                noReencodeUiActive = true;
                if (cmbContainer != null && cmbContainer.Items.Count >= 2)
                {
                    cmbContainer.Items[0] = lang.T("container.mp4_compat");
                    cmbContainer.Items[1] = lang.T("container.mkv_recommended");
                }
                if (grpGrain != null) grpGrain.Text = lang.T("grain.av1_copy");
                if (cmbContainer != null) cmbContainer.Enabled = true;
                if (cmbSpeed != null) cmbSpeed.Enabled = false;
                if (cmbBitrate != null) cmbBitrate.Enabled = false;
                if (chkBitrateAuto != null) chkBitrateAuto.Enabled = false;
                if (chkHighMotion != null) chkHighMotion.Enabled = false;
                if (cmbFps != null) cmbFps.Enabled = false;
                if (chkInterpolation != null) { chkInterpolation.Checked = false; chkInterpolation.Enabled = false; }
                if (cmbInterpolationMode != null) cmbInterpolationMode.Enabled = false;
                if (cmbInterpolationFps != null) cmbInterpolationFps.Enabled = false;
                if (cmbDeint != null) cmbDeint.Enabled = false;
                if (cmbDeintMethod != null) cmbDeintMethod.Enabled = false;
                if (chkCinematic != null) chkCinematic.Enabled = false;
                if (cmbFrameMode != null) cmbFrameMode.Enabled = false;
                if (chkUpload != null) chkUpload.Enabled = false;
                if (cmbUploadBitrate != null) cmbUploadBitrate.Enabled = false;
                if (chkUploadBitrateAuto != null) chkUploadBitrateAuto.Enabled = false;
                if (btnSubtitle != null) btnSubtitle.Enabled = false;
                if (grpLut != null) grpLut.Enabled = false;
                if (lblFrameHelp != null) lblFrameHelp.Text = lang.T("cinematic.copy_help_full");
                if (btnStart != null) btnStart.Text = lang.T("button.start_process");
                return;
            }

            if (!noReencodeUiActive) return;
            noReencodeUiActive = false;
            if (cmbContainer != null && cmbContainer.Items.Count >= 2)
            {
                cmbContainer.Items[0] = lang.T("container.mp4");
                cmbContainer.Items[1] = lang.T("container.mkv");
            }
            if (cmbContainer != null) cmbContainer.Enabled = true;
            if (cmbSpeed != null) cmbSpeed.Enabled = true;
            if (cmbBitrate != null) cmbBitrate.Enabled = true;
            if (chkBitrateAuto != null) chkBitrateAuto.Enabled = true;
            if (chkHighMotion != null) chkHighMotion.Enabled = true;
            if (chkInterpolation != null) chkInterpolation.Enabled = true;
            if (cmbDeint != null) cmbDeint.Enabled = true;
            if (chkCinematic != null) chkCinematic.Enabled = true;
            if (cmbFrameMode != null) cmbFrameMode.Enabled = chkCinematic == null || chkCinematic.Checked;
            if (chkUpload != null) chkUpload.Enabled = cmbCodec.SelectedIndex != 2;
            if (cmbUploadBitrate != null) cmbUploadBitrate.Enabled = chkUpload != null && chkUpload.Enabled && chkUpload.Checked;
            if (chkUploadBitrateAuto != null) chkUploadBitrateAuto.Enabled = chkUpload != null && chkUpload.Enabled && chkUpload.Checked;
            if (btnSubtitle != null) btnSubtitle.Enabled = true;
            if (grpLut != null) grpLut.Enabled = true;
            if (lblFrameHelp != null) lblFrameHelp.Text = lang.T("encode.frame_help");
            if (btnStart != null) btnStart.Text = lang.T("button.start_encode");
        }

        private void StopAv1GrainInspect()
        {
            if (av1GrainInspectCore != null) av1GrainInspectCore.Cancel();
        }

        private void StartAv1GrainInspect(string path, string baseSummary)
        {
            StopAv1GrainInspect();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            string cached;
            if (av1GrainInspectCache.TryGetValue(path, out cached))
            {
                SetMediaInfoText(baseSummary + Environment.NewLine + GetAv1GrainDisplayText(cached), false);
                return;
            }

            string grav = config.Get("GRAV1SYNTH");
            string ffmpeg = Path.Combine(config.Get("FFMPEG_DIR"), "ffmpeg.exe");
            if (!File.Exists(ffmpeg))
            {
                SetMediaInfoText(baseSummary + Environment.NewLine + LF("media.ffmpeg_missing", ffmpeg), false);
                return;
            }

            SetMediaInfoText(baseSummary + Environment.NewLine + lang.T("av1grain.detecting"), false);
            if (av1GrainInspectCore != null) av1GrainInspectCore.Start(path, grav, ffmpeg);
        }

        private void OnAv1GrainInspectCompleted(Av1GrainInspectResult result)
        {
            if (result == null || IsDisposed || Disposing || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (result == null || IsDisposed || Disposing) return;
                    if (listFiles.SelectedItems.Count != 1) return;
                    string current = listFiles.SelectedItems[0].Tag as string;
                    if (!string.Equals(current, result.PathValue, StringComparison.OrdinalIgnoreCase)) return;

                    string state = result.State;
                    if (string.IsNullOrEmpty(state))
                    {
                        string detail = result.FailureMessage;
                        if (string.IsNullOrEmpty(detail)) detail = Av1GrainInspectCore.GetFailureDetail(result.StandardError);
                        if (string.IsNullOrEmpty(detail)) detail = LF("media.return_code", result.ExitCode);
                        state = "AV1 胶片颗粒：检测失败 · " + detail;
                        if (log != null)
                        {
                            AppendLog("[AV1 Inspect] path=" + result.PathValue + Environment.NewLine);
                            AppendLog("[AV1 Inspect] exit_code=" + result.ExitCode.ToString(CultureInfo.InvariantCulture) + " · " + detail + Environment.NewLine);
                            if (!string.IsNullOrWhiteSpace(result.StandardError))
                                AppendLog(result.StandardError.TrimEnd() + Environment.NewLine);
                            ScrollLogToEnd();
                        }
                    }

                    av1GrainInspectCache[result.PathValue] = state;
                    MediaProbeInfo info;
                    string summary = mediaProbeCache.TryGetValue(result.PathValue, out info)
                        ? new MediaSummaryCore(lang).FormatMediaSummary(info)
                        : Path.GetFileName(result.PathValue);
                    SetMediaInfoText(summary + Environment.NewLine + GetAv1GrainDisplayText(state), false);
                });
            }
            catch { }
        }

        private string GetAv1GrainDisplayText(string state)
        {
            if (state == "AV1 胶片颗粒：无") return lang.T("av1grain.none");
            if (state == "AV1 胶片颗粒：无法识别参数表") return lang.T("av1grain.table_invalid");
            if (state == "AV1 胶片颗粒：亮度") return lang.T("av1grain.luma");
            if (state == "AV1 胶片颗粒：亮度 + 色度") return lang.T("av1grain.luma_chroma");
            if (state == "AV1 胶片颗粒：参数表读取失败") return lang.T("av1grain.table_read_failed");
            const string prefix = "AV1 胶片颗粒：检测失败 · ";
            if (!string.IsNullOrEmpty(state) && state.StartsWith(prefix, StringComparison.Ordinal))
                return LF("av1grain.detect_failed_detail", state.Substring(prefix.Length));
            return state ?? lang.T("av1grain.detect_failed");
        }

        private MediaProbeInfo GetSelectedMediaInfo()
        {
            if (listFiles == null || listFiles.SelectedItems.Count != 1) return null;
            string path = listFiles.SelectedItems[0].Tag as string;
            if (string.IsNullOrEmpty(path)) return null;
            MediaProbeInfo info;
            return mediaProbeCache.TryGetValue(path, out info) ? info : null;
        }

        private void SetMediaInfoText(string text, bool muted)
        {
            if (lblMediaInfo == null) return;
            lblMediaInfo.Text = text ?? "";
            lblMediaInfo.ForeColor = muted ? ColorMuted : SystemColors.ControlText;
            try { lutToolTip.SetToolTip(lblMediaInfo, text ?? ""); } catch { }
        }

        private double GetRecommendedOutputFps(MediaProbeInfo info)
        {
            bool interpolate = chkInterpolation != null && chkInterpolation.Checked;
            if (interpolate)
            {
                InterpolationFrameRate parsed; string error;
                if (!InterpolationFrameRateCore.TryParse(cmbInterpolationFps == null ? config.Get("INTERPOLATION_TARGET_FPS") : cmbInterpolationFps.Text, info == null ? "" : info.AvgFrameRate, out parsed, out error)) return 0.0;
                return parsed.Fps;
            }
            return BitrateRecommendationCore.GetRecommendedOutputFps(info,
                cmbDeint != null && cmbDeint.SelectedIndex == 0,
                cmbFps != null && cmbFps.SelectedIndex == 1);
        }

        private MediaProbeInfo GetBitrateMediaContext()
        {
            MediaProbeInfo selected = GetSelectedMediaInfo();
            if (selected != null) return selected;
            if (listFiles == null || listFiles.Items.Count == 0) return null;
            string path = listFiles.Items[0].Tag as string;
            if (string.IsNullOrEmpty(path)) return null;
            MediaProbeInfo info;
            return mediaProbeCache.TryGetValue(path, out info) ? info : null;
        }

        private void LoadBitrateStateForCodec()
        {
            if (cmbCodec == null || cmbBitrate == null || chkBitrateAuto == null) return;
            int index = Math.Max(0, Math.Min(2, cmbCodec.SelectedIndex));
            updatingBitrateUi = true;
            try
            {
                chkBitrateAuto.Checked = modeBitrateAuto[index];
                cmbBitrate.Text = modeBitrate[index];
            }
            finally { updatingBitrateUi = false; }
        }

        private void UpdateBitrateDisplays()
        {
            if (cmbCodec == null || cmbBitrate == null || chkBitrateAuto == null) return;
            if (cmbCodec.SelectedIndex == 3) return;
            int codecIndex = Math.Max(0, Math.Min(2, cmbCodec.SelectedIndex));
            MediaProbeInfo info = GetBitrateMediaContext();
            int width = info != null ? info.Width : 1920;
            int height = info != null ? info.Height : 1080;
            double fps = GetRecommendedOutputFps(info);
            bool highMotion = chkHighMotion != null && chkHighMotion.Checked;

            if (fps > 0.0 && modeBitrateAuto[codecIndex])
            {
                int recommended = BitrateRecommendationCore.GetRecommendedBitrate(codecIndex, width, height, fps, highMotion);
                modeBitrate[codecIndex] = recommended.ToString(CultureInfo.InvariantCulture);
                updatingBitrateUi = true;
                try
                {
                    if (!IsHevcFgsim() || fgsimRcChoice == "VBR") cmbBitrate.Text = modeBitrate[codecIndex];
                    chkBitrateAuto.Checked = !IsHevcFgsim() || fgsimRcChoice == "VBR";
                }
                finally { updatingBitrateUi = false; }
            }

            if (fps > 0.0 && chkUpload != null && chkUpload.Checked && uploadBitrateAuto && cmbUploadBitrate != null && chkUploadBitrateAuto != null)
            {
                int recommendedUpload = BitrateRecommendationCore.GetRecommendedBitrate(2, width, height, fps, highMotion);
                updatingBitrateUi = true;
                try
                {
                    cmbUploadBitrate.Text = recommendedUpload.ToString(CultureInfo.InvariantCulture);
                    chkUploadBitrateAuto.Checked = true;
                }
                finally { updatingBitrateUi = false; }
            }
            UpdateFgsimBitrateUi();
        }

        private bool IsHevcFgsim()
        {
            return cmbCodec != null && cmbCodec.SelectedIndex == 1 && cmbGrainMode != null && cmbGrainMode.SelectedIndex == 2;
        }

        private void UpdateFgsimBitrateUi()
        {
            if (cmbBitrate == null || updatingBitrateUi) return;
            bool active = IsHevcFgsim();
            bool entering = active && !fgsimRcActive;
            bool leaving = !active && fgsimRcActive;
            if (entering) fgsimRcChoice = "VBR";
            fgsimRcActive = active;
            updatingBitrateUi = true;
            try
            {
                if (active)
                {
                    if (!cmbBitrate.Items.Contains(FgsimStandardItem))
                    {
                        cmbBitrate.Items.Insert(0, FgsimStandardItem);
                        cmbBitrate.Items.Insert(1, FgsimHighItem);
                    }
                    if (fgsimRcChoice == "STANDARD") cmbBitrate.SelectedItem = FgsimStandardItem;
                    else if (fgsimRcChoice == "HIGH") cmbBitrate.SelectedItem = FgsimHighItem;
                    lutToolTip.SetToolTip(cmbBitrate, lang.T("grain.fgsim_hint"));
                }
                else
                {
                    cmbBitrate.Items.Remove(FgsimStandardItem);
                    cmbBitrate.Items.Remove(FgsimHighItem);
                    if (leaving && cmbCodec != null && cmbCodec.SelectedIndex >= 0 && cmbCodec.SelectedIndex <= 2)
                        cmbBitrate.Text = modeBitrate[cmbCodec.SelectedIndex];
                    lutToolTip.SetToolTip(cmbBitrate, "");
                }
            }
            finally { updatingBitrateUi = false; }
            UpdateFgsimBitrateControls();
        }

        private void UpdateFgsimBitrateControls()
        {
            if (chkBitrateAuto == null || chkHighMotion == null) return;
            bool cq = IsHevcFgsim() && fgsimRcChoice != "VBR";
            updatingBitrateUi = true;
            try
            {
                chkBitrateAuto.Enabled = !cq;
                chkBitrateAuto.Checked = !cq && cmbCodec.SelectedIndex >= 0 && cmbCodec.SelectedIndex <= 2 && modeBitrateAuto[cmbCodec.SelectedIndex];
                chkHighMotion.Enabled = !cq;
            }
            finally { updatingBitrateUi = false; }
        }

        private string UiText(string zh, string en)
        {
            return string.Equals(lang.Code, "zh-CN", StringComparison.OrdinalIgnoreCase) ? zh : en;
        }

        private void BeginHardwareDetection()
        {
            if (hardwareCapabilityCore == null || hardwareCapabilityCore.IsRunning) return;
            string ffmpeg = Path.Combine(config.Get("FFMPEG_DIR"), "ffmpeg.exe");
            HardwareCapabilityRequest request = new HardwareCapabilityRequest();
            request.AppRoot = appRoot;
            request.FfmpegPath = ffmpeg;
            request.Grav1synthPath = config.Get("GRAV1SYNTH");

            hardwareDetectionInProgress = true;
            hardwareCapsReady = false;
            UpdateSfeUi();
            if (statusHardware != null) statusHardware.Text = lang.T("hardware.detecting");
            if (btnStart != null && (bridgeTaskCoordinator == null || !bridgeTaskCoordinator.IsActive)) btnStart.Enabled = false;
            try
            {
                hardwareCapabilityCore.Start(request);
            }
            catch (Exception ex)
            {
                hardwareDetectionInProgress = false;
                if (btnStart != null && (bridgeTaskCoordinator == null || !bridgeTaskCoordinator.IsActive)) btnStart.Enabled = true;
                if (statusHardware != null) statusHardware.Text = "FFmpeg " + lang.T("hardware.not_detected") + " · " + lang.T("hardware.pending");
                if (log != null) AppendLog("[Hardware] detection start failed: " + ex.Message + Environment.NewLine);
            }
        }

        private void OnHardwareCapabilityCompleted(object sender, HardwareCapabilityCompletedEventArgs e)
        {
            if (IsDisposed || Disposing) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    hardwareDetectionInProgress = false;
                    HardwareCapabilitySnapshot caps = e != null ? e.Snapshot : null;
                    if (caps != null && caps.Ready)
                    {
                        hardwareCaps = caps;
                        hardwareCapsReady = true;
                        ApplyHardwareCapabilities();
                        if (log != null)
                            AppendLog("[Hardware] " + caps.GpuName + " · AV1=" + (caps.Av1Available ? "1" : "0") + " · HEVC=" + (caps.HevcAvailable ? "1" : "0") + " · x264=" + (caps.X264Available ? "1" : "0") + Environment.NewLine);
                    }
                    else
                    {
                        hardwareCaps = caps;
                        hardwareCapsReady = false;
                        ResetHardwareUiToPending();
                        if (log != null && caps != null && !string.IsNullOrWhiteSpace(caps.Error))
                            AppendLog("[Hardware] detection failed: " + caps.Error + Environment.NewLine);
                    }
                    if (btnStart != null && (bridgeTaskCoordinator == null || !bridgeTaskCoordinator.IsActive)) btnStart.Enabled = true;
                    ScrollLogToEnd();
                });
            }
            catch (InvalidOperationException) { }
        }

        private void ApplyHardwareCapabilities()
        {
            if (!hardwareCapsReady || hardwareCaps == null) return;
            string available = lang.T("hardware.available");
            string unavailable = lang.T("hardware.unavailable");
            string cacheText = hardwareCaps.CacheState;
            if (string.Equals(cacheText, "Detected", StringComparison.OrdinalIgnoreCase)) cacheText = lang.T("hardware.cache_detected");
            else if (string.Equals(cacheText, "Cached", StringComparison.OrdinalIgnoreCase)) cacheText = lang.T("hardware.cache_cached");

            if (statusHardware != null)
            {
                statusHardware.Text = "GPU " + SafeHardwareText(hardwareCaps.GpuName) +
                    " · " + lang.T("hardware.driver") + " " + SafeHardwareText(hardwareCaps.DriverVersion) +
                    " · FFmpeg " + SafeHardwareText(hardwareCaps.FfmpegVersion) +
                    " · " + lang.T("hardware.profile") + " " + SafeHardwareText(cacheText) +
                    " · AV1 " + (hardwareCaps.Av1Available ? available : unavailable) +
                    " · UHQ " + (hardwareCaps.Av1UhqAvailable ? available : unavailable) +
                    " · HEVC/Vulkan " + (hardwareCaps.HevcAvailable ? available : unavailable) +
                    " · x264 Grain " + (hardwareCaps.X264Available ? available : unavailable) +
                    " · High10 " + (hardwareCaps.X264Available && hardwareCaps.X264High10Available ? available : unavailable) +
                    " · SVPFlow " + (hardwareCaps.OpenSvpGpuAvailable ? available : unavailable);
                statusHardware.ToolTipText = statusHardware.Text;
            }

            if (cmbGpu != null)
            {
                cmbGpu.Items.Clear();
                cmbGpu.Items.Add(SafeHardwareText(hardwareCaps.GpuName) + lang.T("gpu.auto_detected"));
                cmbGpu.SelectedIndex = 0;
            }

            if (cmbCodec != null && cmbCodec.Items.Count >= 3)
            {
                changingCodecForHardware = true;
                try
                {
                    cmbCodec.Items[0] = hardwareCaps.Av1Available ? lang.T("codec.av1_default") : lang.T("codec.av1_unavailable");
                    cmbCodec.Items[1] = hardwareCaps.HevcAvailable ? lang.T("codec.hevc_scan") : lang.T("codec.hevc_unavailable");
                    cmbCodec.Items[2] = hardwareCaps.X264Available ? lang.T("codec.x264_default") : lang.T("codec.x264_unavailable");
                    if (cmbCodec.SelectedIndex == 0 && !hardwareCaps.Av1Available)
                        cmbCodec.SelectedIndex = hardwareCaps.HevcAvailable ? 1 : (hardwareCaps.X264Available ? 2 : 1);
                    else if (cmbCodec.SelectedIndex == 1 && !hardwareCaps.HevcAvailable)
                    {
                        if (hardwareCaps.Av1Available) cmbCodec.SelectedIndex = 0;
                        else if (hardwareCaps.X264Available) cmbCodec.SelectedIndex = 2;
                    }
                    else if (cmbCodec.SelectedIndex == 2 && !hardwareCaps.X264Available)
                    {
                        if (hardwareCaps.HevcAvailable) cmbCodec.SelectedIndex = 1;
                        else if (hardwareCaps.Av1Available) cmbCodec.SelectedIndex = 0;
                    }
                }
                finally { changingCodecForHardware = false; }
            }
            UpdateSpeedChoices();
            UpdateSfeUi();
            UpdateHardwareDependentControls();
        }

        private void ResetHardwareUiToPending()
        {
            if (statusHardware != null)
            {
                string ffmpegVersion = hardwareCaps != null ? hardwareCaps.FfmpegVersion : "";
                if (string.IsNullOrWhiteSpace(ffmpegVersion)) ffmpegVersion = lang.T("hardware.not_detected");
                statusHardware.Text = "FFmpeg " + ffmpegVersion + " · " + lang.T("hardware.pending");
            }
            if (cmbGpu != null)
            {
                cmbGpu.Items.Clear();
                cmbGpu.Items.Add(lang.T("gpu.auto_retry"));
                cmbGpu.SelectedIndex = 0;
            }
            if (cmbCodec != null && cmbCodec.Items.Count >= 3)
            {
                cmbCodec.Items[0] = lang.T("codec.av1_default");
                cmbCodec.Items[1] = lang.T("codec.hevc_scan");
                cmbCodec.Items[2] = lang.T("codec.x264_default");
            }
            UpdateSpeedChoices();
            UpdateSfeUi();
            UpdateHardwareDependentControls();
        }

        private string SafeHardwareText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? lang.T("hardware.not_detected") : value;
        }

        private bool GuardUnavailableCodecSelection()
        {
            if (changingCodecForHardware || !hardwareCapsReady || hardwareCaps == null || cmbCodec == null) return false;
            int selected = cmbCodec.SelectedIndex;
            if (selected == 0 && !hardwareCaps.Av1Available)
            {
                int fallback = hardwareCaps.HevcAvailable ? 1 : (hardwareCaps.X264Available ? 2 : -1);
                if (fallback >= 0)
                {
                    changingCodecForHardware = true;
                    try { cmbCodec.SelectedIndex = fallback; }
                    finally { changingCodecForHardware = false; }
                }
                MessageBox.Show(this, fallback < 0 ? lang.T("error.av1_unavailable") : lang.T(fallback == 1 ? "message.av1_fallback_hevc" : "message.av1_fallback_x264"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            if (selected == 1 && !hardwareCaps.HevcAvailable)
            {
                int fallback = hardwareCaps.Av1Available ? 0 : (hardwareCaps.X264Available ? 2 : -1);
                if (fallback >= 0)
                {
                    changingCodecForHardware = true;
                    try { cmbCodec.SelectedIndex = fallback; }
                    finally { changingCodecForHardware = false; }
                }
                MessageBox.Show(this, lang.T("error.hevc_unavailable"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            if (selected == 2 && !hardwareCaps.X264Available)
            {
                int fallback = hardwareCaps.HevcAvailable ? 1 : (hardwareCaps.Av1Available ? 0 : -1);
                if (fallback >= 0)
                {
                    changingCodecForHardware = true;
                    try { cmbCodec.SelectedIndex = fallback; }
                    finally { changingCodecForHardware = false; }
                    MessageBox.Show(this, lang.T(fallback == 1 ? "message.x264_fallback_hevc" : "message.x264_fallback_av1"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(this, lang.T("error.x264_unavailable"), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return true;
            }
            return false;
        }

        private void UpdateHardwareDependentControls()
        {
            if (chkUpload != null && cmbCodec != null && cmbCodec.SelectedIndex != 3)
            {
                bool x264Ok = !hardwareCapsReady || hardwareCaps == null || hardwareCaps.X264Available;
                chkUpload.Enabled = cmbCodec.SelectedIndex != 2 && x264Ok;
                if (!chkUpload.Enabled)
                {
                    if (cmbUploadBitrate != null) cmbUploadBitrate.Enabled = false;
                    if (chkUploadBitrateAuto != null) chkUploadBitrateAuto.Enabled = false;
                }
            }
        }

        private bool ValidateHardwareForSelectedCodec()
        {
            if (!hardwareCapsReady || hardwareCaps == null || cmbCodec == null) return true;
            if (cmbCodec.SelectedIndex == 0 && !hardwareCaps.Av1Available)
            {
                MessageBox.Show(this, lang.T("error.av1_unavailable"), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            if (cmbCodec.SelectedIndex == 1 && !hardwareCaps.HevcAvailable)
            {
                MessageBox.Show(this, lang.T("error.hevc_unavailable"), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            if (cmbCodec.SelectedIndex == 2 && !hardwareCaps.X264Available)
            {
                MessageBox.Show(this, lang.T("error.x264_unavailable"), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            return true;
        }

        private void StartBridgeExecution()
        {
            if (bridgeTaskCoordinator == null || bridgeTaskCoordinator.IsActive) return;
            if (listFiles == null || listFiles.Items.Count == 0)
            {
                MessageBox.Show(this, lang.T("info.add_video"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (hardwareDetectionInProgress)
            {
                MessageBox.Show(this, lang.T("hardware.detecting"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!ValidateHardwareForSelectedCodec()) return;

            string error;
            SortedDictionary<string, string> state = BuildBridgeEnvironment(out error);
            if (state == null)
            {
                MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            bool noReencode = cmbCodec != null && cmbCodec.SelectedIndex == 3;
            List<string> inputs = new List<string>();
            foreach (ListViewItem item in listFiles.Items)
            {
                string path = item.Tag as string;
                if (!string.IsNullOrWhiteSpace(path)) inputs.Add(path);
            }

            if (taskLog != null) taskLog.Dispose();
            taskLog = new FullLogFileCore(appRoot, "Task", GetLogEntryLimit());
            fullLogErrorReported = false;
            AppendTaskLog("[Task log] " + taskLog.FilePath + Environment.NewLine);
            AppendTaskLog("[Task] started=" + DateTime.Now.ToString("o", CultureInfo.InvariantCulture) + " build=T9P3HZ" + Environment.NewLine);
            foreach (string input in inputs) AppendTaskLog("[Task input] " + input + Environment.NewLine);
            foreach (KeyValuePair<string, string> setting in state) AppendTaskLog(setting.Key + "=" + setting.Value + Environment.NewLine);

            bool nativeRequested = chkNativeBackend != null && chkNativeBackend.Checked && !noReencode;
            bool nativeAv1NoReencodeRequested = chkNativeBackend != null && chkNativeBackend.Checked && noReencode;
            bool hdrFgsimPreserveRequested = !noReencode &&
                (string.Equals(state["FG_MODE"], "HEVC", StringComparison.OrdinalIgnoreCase) || string.Equals(state["FG_MODE"], "AV1", StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(state["FG_GRAIN_ENGINE"], "FGSIM", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(state["FG_HDR_POLICY"], "PRESERVE", StringComparison.OrdinalIgnoreCase);
            bool hasHdrFgsimPreserveInput = false;
            if (hdrFgsimPreserveRequested)
            {
                foreach (string inputPath in inputs)
                {
                    MediaProbeInfo hdrInfo;
                    if (!mediaProbeCache.TryGetValue(inputPath, out hdrInfo))
                    {
                        MessageBox.Show(this, UiText("HDR 信息尚未检测完成，请稍后重试。", "HDR probe is not ready; please retry."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (!hdrInfo.IsHdr) continue;
                    hasHdrFgsimPreserveInput = true;
                    bool av1HdrFgsim = string.Equals(state["FG_MODE"], "AV1", StringComparison.OrdinalIgnoreCase);
                    if (!nativeRequested || (hdrInfo.IsInterlaced && !av1HdrFgsim))
                    {
                        string why = hdrInfo.IsInterlaced && !av1HdrFgsim
                            ? UiText("当前 Native HEVC FGSIM HDR Preserve 路径暂不支持隔行 HDR。", "The Native HEVC FGSIM HDR Preserve route does not support interlaced HDR.")
                            : UiText("Legacy FGSIM 会把 HDR 转为 SDR；此组合需要启用 Native .NET 才能保留 HDR。", "Legacy FGSIM converts HDR to SDR; enable Native .NET to preserve HDR for this combination.");
                        MessageBox.Show(this, why, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }
            }

            BridgePreparedExecution prepared = null;
            BridgeRequestPreparationFailure preparationFailure = null;
            bool frucRequested = state.ContainsKey("FG_SVP_INTERPOLATE") && state["FG_SVP_INTERPOLATE"] == "1" &&
                state.ContainsKey("FG_INTERPOLATION_ENGINE") && state["FG_INTERPOLATION_ENGINE"] == "FRUC";
            if (frucRequested && (!nativeRequested || inputs.Count != 1 || (state["FG_MODE"] != "HEVC" && state["FG_MODE"] != "X264")))
            {
                MessageBox.Show(this, UiText("FRUC Vulkan 当前仅支持单文件 HEVC/x264 Native。", "FRUC Vulkan currently requires single-file HEVC/x264 Native."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string nativeReason = "";
            bool nativePrepared = false;
            if (nativeAv1NoReencodeRequested && inputs.Count == 1)
            {
                MediaProbeInfo copyInfo;
                if (mediaProbeCache.TryGetValue(inputs[0], out copyInfo))
                {
                    nativePrepared = NativeAv1GrainReplaceCore.TryPrepare(appRoot, inputs[0], copyInfo, state, out prepared, out nativeReason);
                    if (nativePrepared) state["FG_MODE"] = "AV1";
                }
                else nativeReason = "media probe cache is not ready";
            }
            else if (nativeAv1NoReencodeRequested) nativeReason = "native AV1 no-reencode currently accepts one input at a time";
            if (!nativePrepared && nativeRequested && inputs.Count == 1)
            {
                MediaProbeInfo nativeInfo;
                if (mediaProbeCache.TryGetValue(inputs[0], out nativeInfo))
                {
                    if (string.Equals(state["FG_MODE"], "HEVC", StringComparison.OrdinalIgnoreCase))
                        nativePrepared = NativeHevcCore.TryPrepare(appRoot, inputs[0], nativeInfo, state, hardwareCaps, out prepared, out nativeReason);
                    else if (string.Equals(state["FG_MODE"], "AV1", StringComparison.OrdinalIgnoreCase))
                        nativePrepared = NativeAv1Core.TryPrepare(appRoot, inputs[0], nativeInfo, state, hardwareCaps, out prepared, out nativeReason);
                    else
                        nativePrepared = NativeX264DigitalGrainCore.TryPrepare(appRoot, inputs[0], nativeInfo, state, out prepared, out nativeReason);
                }
                else nativeReason = "media probe cache is not ready";
            }
            else if (!nativePrepared && nativeRequested) nativeReason = string.Equals(state["FG_MODE"], "HEVC", StringComparison.OrdinalIgnoreCase)
                ? "native HEVC currently accepts one input at a time"
                : string.Equals(state["FG_MODE"], "AV1", StringComparison.OrdinalIgnoreCase)
                    ? "native AV1 currently accepts one input at a time" : "native x264 currently accepts one input at a time";
            bool x264NativeInterpolation = nativeRequested && state["FG_MODE"] == "X264" && state["FG_SVP_INTERPOLATE"] == "1";
            if ((frucRequested || x264NativeInterpolation) && !nativePrepared)
            {
                MessageBox.Show(this, nativeReason, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (hasHdrFgsimPreserveInput && !nativePrepared)
            {
                string stopMessage = UiText("FGSIM HDR Preserve 的 Native 10-bit 路径未能准备，任务已停止；为避免 Legacy 把输出转成 SDR，不会回退。原因：", "The Native 10-bit FGSIM HDR Preserve route could not be prepared. The job was stopped to avoid Legacy converting the output to SDR; no fallback was started. Reason: ") + nativeReason;
                if (log != null) AppendTaskLog("[" + state["FG_MODE"] + "] HDR FGSIM Preserve stopped; Legacy fallback blocked: " + nativeReason + Environment.NewLine);
                MessageBox.Show(this, stopMessage, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if ((nativeRequested || nativeAv1NoReencodeRequested) && !nativePrepared && log != null)
                AppendTaskLog("[Native .NET] fallback to legacy Bridge: " + nativeReason + Environment.NewLine);
            if (!nativePrepared && !BridgeRequestPreparationCore.TryPrepareExecution(appRoot, inputs, state, noReencode, out prepared, out preparationFailure))
            {
                if (preparationFailure != null && preparationFailure.Kind == BridgeRequestPreparationFailureKind.MissingTool)
                {
                    MessageBox.Show(this, UiText("执行工具不存在：", "Execution tool was not found:") + Environment.NewLine + preparationFailure.Path, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else if (preparationFailure != null && preparationFailure.Kind == BridgeRequestPreparationFailureKind.MissingInput)
                {
                    if (log != null) AppendTaskLog("[Runner] input preflight: exists=0 path=\"" + preparationFailure.Path + "\"" + Environment.NewLine);
                    MessageBox.Show(this, UiText("输入文件不存在或当前无法访问：", "Input file does not exist or is not currently accessible:") + Environment.NewLine + preparationFailure.Path, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    ScrollLogToEnd();
                }
                else
                {
                    MessageBox.Show(this, lang.T("info.add_video"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }

            foreach (string inputPath in prepared.InputFiles)
            {
                if (log != null) AppendTaskLog("[Runner] input preflight: exists=1 path=\"" + inputPath + "\"" + Environment.NewLine);
            }

            WorkspacePreflightIssue workspaceIssue;
            if (!WorkspacePreflightCore.Check(prepared.InputFiles,
                noReencode ? "AV1" : (cmbCodec.SelectedIndex == 0 ? "AV1" : cmbCodec.SelectedIndex == 1 ? "HEVC" : "X264"),
                noReencode, config.Get("TEMP_MODE"), config.Get("TEMP_CUSTOM_DIR"),
                config.Get("OUTPUT_MODE"), config.Get("OUTPUT_CUSTOM_DIR"), mediaProbeCache,
                delegate(WorkspaceSpaceWarning warning)
                {
                    string message = LF(warning.IsOutput ? "warning.output_space" : "warning.temp_space",
                        warning.Path, FormatWorkspaceBytes(warning.Available), FormatWorkspaceBytes(warning.Required), FormatWorkspaceBytes(warning.Reserve));
                    return MessageBox.Show(this, message, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                }, out workspaceIssue))
            {
                if (workspaceIssue != null)
                    MessageBox.Show(this, LF(workspaceIssue.Key, workspaceIssue.Path, workspaceIssue.Detail), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                BridgeExecutionCore.CleanupTemporaryFiles(prepared.ExecutionRequest);
                return;
            }

            MediaProbeInfo info = GetBitrateMediaContext();
            double outputFps = GetRecommendedOutputFps(info);
            BridgeTaskRequest taskRequest = BridgeRequestPreparationCore.CreateTask(prepared, outputFps);
            taskRequest.NativeBackend = nativePrepared;
            activeRunNativeBackend = nativePrepared;
            string runCodecMode;
            if (!state.TryGetValue("FG_MODE", out runCodecMode))
                runCodecMode = noReencode ? "AV1" : "";
            activeRunCodecName = string.Equals(runCodecMode, "HEVC", StringComparison.OrdinalIgnoreCase) ? "HEVC" :
                string.Equals(runCodecMode, "AV1", StringComparison.OrdinalIgnoreCase) ? "AV1" : "x264";
            if (log != null)
            {
                AppendTaskLog(Environment.NewLine + "============================================================" + Environment.NewLine);
                AppendTaskLog(nativePrepared
                    ? (string.Equals(state["FG_MODE"], "AV1", StringComparison.OrdinalIgnoreCase)
                        ? UiText("[Native .NET AV1] AV1 原生执行", "[Native .NET AV1] Native AV1 execution")
                        : string.Equals(state["FG_MODE"], "HEVC", StringComparison.OrdinalIgnoreCase)
                        ? UiText("[Native .NET HEVC] HEVC 原生执行", "[Native .NET HEVC] Native HEVC execution")
                        : UiText("[Native .NET Phase 2C] x264 原生执行", "[Native .NET Phase 2C] Native x264 execution")) + Environment.NewLine
                    : (noReencode
                    ? UiText("[Phase 3.5.8 AV1 No-Reencode] 启动真实处理", "[Phase 3.5.8 AV1 No-Reencode] Starting real processing") + Environment.NewLine
                    : UiText("[Phase 3.5.8 Bridge] 启动真实编码", "[Phase 3.5.8 Bridge] Starting real encoding") + Environment.NewLine));
                AppendTaskLog(UiText("输入文件数：", "Input files: ") + prepared.InputFiles.Count.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);
                if (!noReencode) AppendTaskLog(UiText("推荐输出 FPS：", "Recommended output FPS: ") + outputFps.ToString("0.###", CultureInfo.InvariantCulture) + Environment.NewLine);
                AppendTaskLog((noReencode ? UiText("No-Reencode 工具：", "No-Reencode tool: ") : nativePrepared ? UiText("Native 执行工具：", "Native execution tool: ") : UiText("Bridge：", "Bridge: ")) + prepared.ToolPath + Environment.NewLine);
                foreach (KeyValuePair<string, string> pair in state)
                    AppendTaskLog(pair.Key + "=" + pair.Value + Environment.NewLine);
                AppendTaskLog("------------------------------------------------------------" + Environment.NewLine);
            }

            try
            {
                ScrollLogToEnd();
                bridgeTaskCoordinator.Start(taskRequest);
            }
            catch (Exception ex)
            {
                BridgeExecutionCore.CleanupTemporaryFiles(prepared.ExecutionRequest);
                if (progressRun != null)
                {
                    progressRun.MarqueeAnimationSpeed = 0;
                    progressRun.Style = ProgressBarStyle.Blocks;
                    progressRun.Value = 0;
                }
                AppendTaskLog("[Task] start failed: " + ex.ToString() + Environment.NewLine);
                if (lblRunStage != null) lblRunStage.Text = UiText("启动失败", "Start failed");
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void CancelBridgeExecution()
        {
            if (bridgeTaskCoordinator == null || !bridgeTaskCoordinator.IsActive) return;
            bridgeTaskCoordinator.Cancel();
        }

        private void OnBridgeTaskStateChanged(object sender, BridgeTaskStateChangedEventArgs e)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed || Disposing) return;

                    bool idle = e == null || e.State == BridgeTaskState.Idle;
                    if (btnStart != null) btnStart.Enabled = idle && !hardwareDetectionInProgress;
                    if (btnCancelTask != null) btnCancelTask.Enabled = e != null && e.State == BridgeTaskState.Running;

                    if (e == null) return;
                    if (e.State == BridgeTaskState.Running)
                    {
                        BridgeTaskRequest task = e.Task;
                        bool noReencode = task != null && task.NoReencode;
                        IDictionary<string, string> state = task != null && task.ExecutionRequest != null ? task.ExecutionRequest.Environment : null;
                        if (lblRunStage != null)
                        {
                            if (noReencode)
                                lblRunStage.Text = UiText("Phase 3.5.8 AV1 不重编码处理中", "Phase 3.5.8 AV1 no-reencode running");
                            else if (activeRunNativeBackend || (task != null && task.NativeBackend))
                            {
                                string codecName = string.IsNullOrWhiteSpace(activeRunCodecName) ? "x264" : activeRunCodecName;
                                lblRunStage.Text = UiText("Native .NET " + codecName + " 编码处理中", "Native .NET " + codecName + " encoding");
                            }
                            else
                                lblRunStage.Text = UiText("Phase 3.5.8 Bridge 编码处理中", "Phase 3.5.8 Bridge encoding running");
                        }
                        if (lblRunMetric != null)
                        {
                            string value;
                            if (noReencode)
                                lblRunMetric.Text = "grain: " + (state != null && state.TryGetValue("FG_AV1_GRAIN_MODE", out value) ? value : "—") + "   video: copy";
                            else
                                lblRunMetric.Text = (state != null && state.TryGetValue("FG_GRAIN_ENGINE", out value) ? "grain: " + value + "   " : "") +
                                    "fps: " + (task == null ? 0.0 : task.RecommendedOutputFps).ToString("0.###", CultureInfo.InvariantCulture);
                        }
                        if (progressRun != null)
                        {
                            progressRun.Value = 0;
                            progressRun.Style = ProgressBarStyle.Marquee;
                            progressRun.MarqueeAnimationSpeed = 25;
                        }
                        ScrollLogToEnd();
                    }
                    else if (e.State == BridgeTaskState.Cancelling)
                    {
                        if (lblRunStage != null) lblRunStage.Text = UiText("正在取消任务...", "Cancelling task...");
                        if (log != null)
                        {
                            AppendTaskLog(UiText("[Runner] 正在请求终止进程树...", "[Runner] Requesting process-tree termination...") + Environment.NewLine);
                            ScrollLogToEnd();
                        }
                    }
                });
            }
            catch (InvalidOperationException) { }
        }

        private void OnBridgeExecutionLogLine(object sender, BridgeLogLineEventArgs e)
        {
            string prefix = e.Stream == BridgeLogStream.StandardError ? "[stderr] " : (e.Stream == BridgeLogStream.StandardOutput ? "[stdout] " : "[runner] ");
            string line = prefix + e.Line + Environment.NewLine;
            FullLogFileCore currentLog = taskLog;
            if (currentLog != null) currentLog.Write(line);
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed || Disposing || log == null) return;
                    AppendVisibleLog(line);
                    ScrollLogToEnd();
                });
            }
            catch (InvalidOperationException) { }
        }

        private void OnBridgeExecutionProgressChanged(object sender, BridgeProgressEventArgs e)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed || Disposing) return;

                    if (e.HasStage && lblRunStage != null)
                    {
                        lblRunStage.Text = (activeRunNativeBackend ? "Native .NET " + (string.IsNullOrWhiteSpace(activeRunCodecName) ? "" : activeRunCodecName + " · ") : "") +
                            UiText("阶段 ", "Stage ") + e.StageCurrent.ToString(CultureInfo.InvariantCulture) + "/" + e.StageTotal.ToString(CultureInfo.InvariantCulture) + " · " + e.StageText;
                    }

                    if (e.HasMetrics && lblRunMetric != null)
                    {
                        string metricText = "fps: " + e.FpsText + "   speed: " + e.SpeedText + "x";
                        if (!string.IsNullOrWhiteSpace(e.EtaText)) metricText += "   ETA: " + e.EtaText;
                        lblRunMetric.Text = metricText;
                    }

                    if (e.HasProgress && progressRun != null)
                    {
                        progressRun.MarqueeAnimationSpeed = 0;
                        progressRun.Style = ProgressBarStyle.Continuous;
                        int value = Math.Max(progressRun.Minimum, Math.Min(progressRun.Maximum, e.ProgressPermille));
                        progressRun.Value = value;
                    }
                });
            }
            catch (InvalidOperationException) { }
        }

        private void AppendTaskLog(string text)
        {
            if (taskLog != null) taskLog.Write(text);
            AppendVisibleLog(text);
        }

        private void AppendLog(string text)
        {
            if (fullLog != null) fullLog.Write(text);
            AppendVisibleLog(text);
        }

        private void AppendVisibleLog(string text)
        {
            if (log == null || log.IsDisposed || log.Disposing) return;
            log.AppendText(text);
            FullLogFileCore failedLog = taskLog != null && !string.IsNullOrEmpty(taskLog.Error) ? taskLog : fullLog;
            if (failedLog != null && !string.IsNullOrEmpty(failedLog.Error) && !fullLogErrorReported)
            {
                fullLogErrorReported = true;
                log.AppendText("[Full log ERROR] " + failedLog.Error + Environment.NewLine);
            }
        }

        private int GetLogEntryLimit()
        {
            int value = config == null ? 100 : config.GetInt("LOG_ENTRY_LIMIT", 100);
            return Math.Max(0, Math.Min(10000, value));
        }

        private void ClearLogFolder()
        {
            string dir = Path.Combine(appRoot, "Logs");
            if (!Directory.Exists(dir))
            {
                MessageBox.Show(this, UiText("日志目录为空。", "The log folder is empty."), UiText("日志", "Log"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult confirm = MessageBox.Show(this,
                UiText("确定清空 FGS Logs 目录中的日志文件吗？\n\n当前正在使用的日志会保留。", "Clear log files from the FGS Logs folder?\n\nLogs currently in use will be kept."),
                UiText("清空日志目录", "Clear log folder"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) return;

            int deleted = 0, kept = 0;
            try
            {
                foreach (string file in Directory.GetFiles(dir, "FGS_*.log", SearchOption.TopDirectoryOnly))
                {
                    try { File.Delete(file); deleted++; }
                    catch { kept++; }
                }
                AppendLog("[Log] cleanup: deleted=" + deleted.ToString(CultureInfo.InvariantCulture) + " kept=" + kept.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);
                MessageBox.Show(this,
                    UiText("日志清理完成。删除：", "Log cleanup completed. Deleted: ") + deleted.ToString(CultureInfo.InvariantCulture) +
                    UiText("，保留：", ", kept: ") + kept.ToString(CultureInfo.InvariantCulture),
                    UiText("日志", "Log"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, UiText("日志", "Log"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenFullLog(bool folder)
        {
            FullLogFileCore selected = taskLog ?? fullLog;
            if (selected == null) return;
            try
            {
                string target = folder ? Path.GetDirectoryName(selected.FilePath) : selected.FilePath;
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, UiText("日志", "Log"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void ScrollLogToEnd()
        {
            if (log == null || log.IsDisposed || log.Disposing || !log.IsHandleCreated) return;
            // Use the standard EDIT control, not RichEdit (RichEd20.dll).
            // Keep long encoding runs from growing the visible log indefinitely.
            const int maxVisibleChars = 512 * 1024;
            if (log.TextLength > maxVisibleChars)
            {
                string text = log.Text;
                int cut = text.IndexOf('\n', text.Length - maxVisibleChars);
                if (cut < 0) cut = text.Length - maxVisibleChars;
                else cut++;
                log.Select(0, cut);
                log.SelectedText = "";
            }
            log.SelectionStart = log.TextLength;
            log.SelectionLength = 0;
            log.ScrollToCaret();
        }

        private void OnBridgeExecutionCompleted(object sender, BridgeExecutionCompletedEventArgs e)
        {
            string completion = (e.Result.Exception == null ? "" : "[runner] exception=" + e.Result.Exception.ToString() + Environment.NewLine) +
                "[runner] exit_code=" + e.Result.ExitCode.ToString(CultureInfo.InvariantCulture) + " cancelled=" + (e.Result.Cancelled ? "1" : "0") + Environment.NewLine +
                "[Task] finished=" + DateTime.Now.ToString("o", CultureInfo.InvariantCulture) + Environment.NewLine +
                "============================================================" + Environment.NewLine;
            FullLogFileCore completedLog = taskLog;
            if (completedLog != null) completedLog.Write(completion);
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed || Disposing) return;
                    if (progressRun != null)
                    {
                        progressRun.MarqueeAnimationSpeed = 0;
                        progressRun.Style = ProgressBarStyle.Blocks;
                        progressRun.Value = (!e.Result.Cancelled && e.Result.Exception == null && e.Result.ExitCode == 0) ? progressRun.Maximum : 0;
                    }

                    if (e.Result.Cancelled)
                    {
                        if (lblRunStage != null) lblRunStage.Text = UiText("任务已取消", "Task cancelled");
                    }
                    else if (e.Result.Exception != null)
                    {
                        if (lblRunStage != null) lblRunStage.Text = UiText("执行异常", "Execution error");
                    }
                    else if (e.Result.ExitCode == 0)
                    {
                        if (lblRunStage != null) lblRunStage.Text = UiText("处理完成", "Completed");
                    }
                    else
                    {
                        if (lblRunStage != null) lblRunStage.Text = UiText("处理失败", "Failed") + " (exit " + e.Result.ExitCode.ToString(CultureInfo.InvariantCulture) + ")";
                    }

                    if (log != null)
                    {
                        AppendVisibleLog(completion);
                        ScrollLogToEnd();
                    }
                });
            }
            catch (InvalidOperationException) { }
        }

        private void UpdateHdrRouteStatus(MediaProbeInfo info)
        {
            if (lblHdrRoute == null) return;
            string text;
            string detail;
            if (listFiles != null && listFiles.SelectedItems.Count > 1)
            {
                text = lang.T("input.hdr_route_multi");
                detail = text;
            }
            else if (info == null)
            {
                text = lang.T("input.hdr_route_pending");
                detail = text;
            }
            else if (!info.IsHdr)
            {
                text = lang.T("input.hdr_route_sdr");
                detail = text;
            }
            else
            {
                int codec = cmbCodec == null ? 0 : cmbCodec.SelectedIndex;
                bool noReencode = codec == 3;
                bool av1 = codec == 0;
                bool hevc = codec == 1;
                bool x264 = codec == 2;
                bool fgsim = (hevc && IsHevcFgsim()) ||
                    (av1 && cmbGrainMode != null && cmbGrainMode.SelectedIndex == 4);
                bool upload = chkUpload != null && chkUpload.Checked && !noReencode;
                bool color = colorCorrectionEnabled;
                bool lut = chkLut != null && chkLut.Checked && !string.IsNullOrWhiteSpace(selectedLutPath);
                bool interpolate = chkInterpolation != null && chkInterpolation.Checked && !noReencode;
                bool fruc = interpolate && cmbInterpolationMode != null && cmbInterpolationMode.SelectedIndex == 2;
                string policy = config.Get("HDR_POLICY").ToUpperInvariant();
                if (policy != "PRESERVE" && policy != "SDR") policy = "AUTO";
                string tone = config.Get("TONE_MAP_ALGO").ToLowerInvariant();
                if (tone != "mobius" && tone != "reinhard" && tone != "gamma" && tone != "linear" && tone != "clip") tone = "hable";
                string toneLabel = char.ToUpperInvariant(tone[0]) + tone.Substring(1);
                bool preserve = false;
                bool toSdr = false;
                bool unsupported = false;

                if (fruc && !hevc && !x264)
                {
                    unsupported = true;
                    detail = lang.T("input.hdr_route_detail_fruc");
                }
                else if (noReencode)
                {
                    preserve = true;
                    detail = UiText("AV1 不重编码只更换颗粒元数据，不执行色调映射。", "AV1 no-reencode only changes grain metadata; tone mapping is not run.");
                }
                else if (policy == "SDR")
                {
                    toSdr = true;
                    detail = LF("input.hdr_route_detail_sdr", toneLabel);
                }
                else if (policy == "PRESERVE")
                {
                    bool nativeSelected = chkNativeBackend != null && chkNativeBackend.Checked;
                    bool av1FgsimPreserveReady = av1 && nativeSelected &&
                        (!info.IsInterlaced || (cmbDeint != null && cmbDeint.SelectedIndex == 0));
                    if (x264 || (hevc && fgsim && (!nativeSelected || info.IsInterlaced)) ||
                        (av1 && fgsim && !av1FgsimPreserveReady) || fruc)
                    {
                        unsupported = true;
                        detail = x264
                            ? lang.T("input.hdr_route_detail_x264")
                            : (av1 && fgsim
                                ? UiText("AV1 FGSIM 的 HDR Preserve 需要启用 Native .NET；隔行素材还需启用 Auto 反交错。Legacy 会将其转为 SDR。", "AV1 FGSIM HDR Preserve requires Native .NET; interlaced sources also require Auto deinterlace. Legacy converts it to SDR.")
                                : (fgsim ? lang.T("input.hdr_route_detail_fgsim") : lang.T("input.hdr_route_detail_fruc")));
                    }
                    else if (fgsim && !hevc && !av1FgsimPreserveReady)
                    {
                        toSdr = true;
                        detail = lang.T("input.hdr_route_detail_fgsim_auto");
                    }
                    else
                    {
                        preserve = true;
                        detail = av1 && fgsim
                            ? UiText("AV1 Native 使用 10-bit FGSIM 亮度颗粒路径，保留 HDR 信号。", "AV1 Native uses the 10-bit FGSIM luma-grain path and preserves the HDR signal.")
                            : (hevc && fgsim
                                ? UiText("HEVC Native 将使用 10-bit FGSIM 亮度颗粒路径，保留 HDR 信号。", "HEVC Native will use the 10-bit FGSIM luma-grain path and preserve the HDR signal.")
                                : lang.T("input.hdr_route_detail_preserve"));
                        if (hevc && (lut || color))
                            detail += " " + UiText("BT.709 LUT 会跳过；启用的色彩纠正仍会应用。", "The BT.709 LUT is skipped; enabled color correction is still applied.");
                        if (upload || (interpolate && !fruc))
                            detail += " " + lang.T("input.hdr_route_detail_bypass");
                    }
                }
                else
                {
                    bool autoToSdr = x264 || lut || color || upload || (interpolate && !fruc) || fgsim;
                    if (fruc && !autoToSdr)
                    {
                        unsupported = true;
                        detail = lang.T("input.hdr_route_detail_fruc");
                    }
                    else if (autoToSdr)
                    {
                        toSdr = true;
                        detail = LF("input.hdr_route_detail_auto_sdr", toneLabel);
                    }
                    else
                    {
                        preserve = true;
                        detail = lang.T("input.hdr_route_detail_auto_preserve");
                    }
                }

                text = unsupported ? lang.T("input.hdr_route_unsupported") :
                    (toSdr ? LF("input.hdr_route_tosdr", toneLabel) : lang.T("input.hdr_route_passthrough"));
                bool hasPreserveBypass = hevc ? (lut || upload || interpolate) : (lut || color || upload || interpolate);
                if (preserve && !noReencode && hasPreserveBypass)
                    text += " " + lang.T("input.hdr_route_bypass_short");
            }
            lblHdrRoute.Text = text;
            lutToolTip.SetToolTip(lblHdrRoute, detail ?? text);
        }

        private void UpdateMediaDrivenUi(MediaProbeInfo info)
        {
            UpdateHdrRouteStatus(info);
            if (cmbFps == null || cmbDeint == null || cmbDeintMethod == null) return;
            if (cmbCodec != null && cmbCodec.SelectedIndex == 3)
            {
                cmbFps.Enabled = false;
                cmbDeint.Enabled = false;
                cmbDeintMethod.Enabled = false;
                if (cmbInterpolationMode != null) cmbInterpolationMode.Enabled = false;
                if (cmbInterpolationFps != null) cmbInterpolationFps.Enabled = false;
                return;
            }
            bool interpolating = chkInterpolation != null && chkInterpolation.Checked;
            if (cmbInterpolationFps != null) cmbInterpolationFps.Enabled = interpolating;

            if (interpolating)
            {
                if (cmbInterpolationMode != null) cmbInterpolationMode.Enabled = true;
                if (cmbDeint.SelectedIndex != 0) cmbDeint.SelectedIndex = 0;
                cmbDeint.Enabled = false;
                cmbDeintMethod.Enabled = true;
                if (cmbFps.Items.Count > 0)
                {
                    InterpolationFrameRate targetRate; string rateError;
                    string requestedRate = cmbInterpolationFps == null ? config.Get("INTERPOLATION_TARGET_FPS") : cmbInterpolationFps.Text;
                    if (InterpolationFrameRateCore.TryParse(requestedRate, info == null ? "" : info.AvgFrameRate, out targetRate, out rateError))
                    {
                        string engineLabel = cmbInterpolationMode != null && cmbInterpolationMode.SelectedIndex == 2 ? "FRUC Vulkan" : "OpenSVPFlow";
                        cmbFps.Items[0] = engineLabel + " · " + targetRate.Fps.ToString("0.###", CultureInfo.InvariantCulture) + " fps";
                    }
                    else cmbFps.Items[0] = UiText("插帧目标帧率无效", "Invalid interpolation target FPS");
                    cmbFps.SelectedIndex = 0;
                    cmbFps.Enabled = false;
                }
                return;
            }

            if (cmbInterpolationMode != null) cmbInterpolationMode.Enabled = true;
            if (cmbInterpolationFps != null) cmbInterpolationFps.Enabled = false;
            cmbDeint.Enabled = true;
            bool autoDeint = cmbDeint.SelectedIndex == 0;
            cmbDeintMethod.Enabled = autoDeint;

            if (cmbFps.Items.Count > 0)
            {
                if (autoDeint && info != null && info.IsInterlaced)
                {
                    string src = MediaSummaryCore.FormatMediaFps(info.AvgFrameRate);
                    string dst = MediaSummaryCore.DoubleFpsDisplay(info.AvgFrameRate);
                    cmbFps.Items[0] = !string.IsNullOrEmpty(dst) && src != "—" ? LF("fps.auto_detected", src, dst) : lang.T("fps.auto_interlaced");
                    cmbFps.SelectedIndex = 0;
                    cmbFps.Enabled = false;
                }
                else
                {
                    cmbFps.Items[0] = lang.T("fps.auto_film");
                    cmbFps.Enabled = true;
                }
            }
        }

        private static string QuoteProcessArg(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private static string FirstLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string[] lines = value.Trim().Split(new string[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 ? lines[0].Trim() : "";
        }

        private void UpdateStatusCount()
        {
            if (lblStatus == null || listFiles == null) return;
            if (listFiles.Items.Count == 0) lblStatus.Text = lang.T("log.ready");
            else lblStatus.Text = lang.T("status.ready_count").Replace("{0}", listFiles.Items.Count.ToString(CultureInfo.InvariantCulture));
        }

        private static string FormatSize(long bytes)
        {
            double value = bytes;
            string[] units = new string[] { "B", "KB", "MB", "GB", "TB" };
            int u = 0;
            while (value >= 1024.0 && u < units.Length - 1) { value /= 1024.0; u++; }
            return value.ToString(u == 0 ? "0" : "0.##", CultureInfo.InvariantCulture) + " " + units[u];
        }

        private static string FormatWorkspaceBytes(double bytes)
        {
            const double mb = 1024.0 * 1024.0;
            if (bytes >= 1024.0 * mb * 1024.0) return (bytes / (1024.0 * mb * 1024.0)).ToString("N2", CultureInfo.CurrentCulture) + " TB";
            if (bytes >= 1024.0 * mb) return (bytes / (1024.0 * mb)).ToString("N2", CultureInfo.CurrentCulture) + " GB";
            return (bytes / mb).ToString("N0", CultureInfo.CurrentCulture) + " MB";
        }


        private string LF(string key, params object[] args)
        {
            try { return string.Format(CultureInfo.CurrentCulture, lang.T(key), args); }
            catch { return lang.T(key); }
        }

        private string LutRoot
        {
            get { return config.Get("LUT_ROOT"); }
        }

        private string LutPreviewRoot
        {
            get { return Path.Combine(LutRoot, "_LUT_PREVIEWS"); }
        }

        private static string QuoteProcessArgument(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private static string PsLiteral(string value)
        {
            return "'" + (value ?? "").Replace("'", "''") + "'";
        }

        private static bool JsonBool(IDictionary<string, object> row, string key, bool fallback)
        {
            object value;
            if (!row.TryGetValue(key, out value) || value == null) return fallback;
            if (value is bool) return (bool)value;
            bool parsed;
            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }

        private static double JsonDouble(IDictionary<string, object> row, string key, double fallback)
        {
            object value;
            if (!row.TryGetValue(key, out value) || value == null) return fallback;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static int JsonInt(IDictionary<string, object> row, string key, int fallback)
        {
            object value;
            if (!row.TryGetValue(key, out value) || value == null) return fallback;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private List<LutChoice> ReadLutRecordList(string jsonPath)
        {
            try { return LutCatalogCore.ReadLutRecordList(jsonPath, LutRoot); }
            catch (Exception ex)
            {
                if (log != null) AppendLog("[LUT] Failed to read " + jsonPath + ": " + ex.Message + Environment.NewLine);
                return new List<LutChoice>();
            }
        }

        private string GetLutDisplayName(string path)
        {
            return LutCatalogCore.GetLutDisplayName(path, LutRoot, lang.T("lut.none"));
        }

        private void RefreshLutLists()
        {
            if (cmbRecentLut == null || cmbFavoriteLut == null) return;
            loadingLutLists = true;
            try
            {
                string previewRoot = LutPreviewRoot;
                PopulateLutCombo(cmbRecentLut, ReadLutRecordList(Path.Combine(previewRoot, "_LUT_GALLERY_RECENT.json")), lang.T("lut.recent_select"), lang.T("lut.recent_empty"), "Recent");
                PopulateLutCombo(cmbFavoriteLut, ReadLutRecordList(Path.Combine(previewRoot, "_LUT_GALLERY_FAVORITES.json")), lang.T("lut.favorite_select"), lang.T("lut.favorite_empty"), "Favorite");
            }
            finally
            {
                loadingLutLists = false;
            }
        }

        private void PopulateLutCombo(ComboBox combo, List<LutChoice> entries, string selectText, string emptyText, string source)
        {
            combo.BeginUpdate();
            try
            {
                combo.Items.Clear();
                if (entries.Count == 0)
                {
                    combo.Items.Add(new LutChoice(emptyText, ""));
                    combo.SelectedIndex = 0;
                    combo.Enabled = false;
                    return;
                }

                combo.Items.Add(new LutChoice(selectText, ""));
                int selectedIndex = 0;
                for (int i = 0; i < entries.Count; i++)
                {
                    combo.Items.Add(entries[i]);
                    if (string.Equals(selectedLutPath, entries[i].PathValue, StringComparison.OrdinalIgnoreCase))
                        selectedIndex = i + 1;
                }
                combo.SelectedIndex = selectedIndex;
                combo.Enabled = true;
            }
            finally { combo.EndUpdate(); }
        }

        private void SelectLutFromCombo(ComboBox combo, string source)
        {
            if (loadingLutLists || combo == null || combo.SelectedIndex <= 0) return;
            LutChoice choice = combo.SelectedItem as LutChoice;
            if (choice == null || string.IsNullOrEmpty(choice.PathValue)) return;
            selectedLutPath = choice.PathValue;
            selectedLutSource = source;
            chkLut.Checked = true;
            UpdateLutUi();
        }

        private void ClearSelectedLut()
        {
            selectedLutPath = "";
            selectedLutSource = "None";
            chkLut.Checked = false;
            colorCorrectionEnabled = false;
            try { config.SaveValue("COLOR_CORRECTION_ENABLED", "false"); }
            catch (Exception ex) { if (log != null) AppendLog("[Color] Failed to save disabled state: " + ex.Message + Environment.NewLine); }
            RefreshLutLists();
            UpdateLutUi();
            UpdateColorCorrectionUi();
        }

        private void UpdateLutUi()
        {
            if (lblSelectedLut == null || picLutPreview == null || chkLut == null) return;

            bool hasLut = !string.IsNullOrEmpty(selectedLutPath) && File.Exists(selectedLutPath);
            if (!hasLut && !string.IsNullOrEmpty(selectedLutPath))
            {
                selectedLutPath = "";
                selectedLutSource = "None";
            }

            if (hasLut)
            {
                lblSelectedLut.Text = GetLutDisplayName(selectedLutPath);
                lblSelectedLut.ForeColor = SystemColors.ControlText;
                lutToolTip.SetToolTip(lblSelectedLut, selectedLutPath);
                SetLutPreview(selectedLutPath);
            }
            else
            {
                lblSelectedLut.Text = lang.T("lut.none");
                lblSelectedLut.ForeColor = ColorMuted;
                lutToolTip.SetToolTip(lblSelectedLut, "");
                SetLutPreview("");
            }

            bool enabled = chkLut.Checked;
            trackLutStrength.Enabled = enabled;
            lblLutStrength.Enabled = enabled;
            if (lblLutStrengthTitle != null) lblLutStrengthTitle.Enabled = enabled;
            UpdateHdrRouteStatus(GetSelectedMediaInfo());
        }

        private void SetLutPreview(string lutPath)
        {
            Image old = picLutPreview.Image;
            picLutPreview.Image = null;
            if (old != null) old.Dispose();

            lutToolTip.SetToolTip(picLutPreview, "");
            if (string.IsNullOrEmpty(lutPath)) return;
            string preview = LutCatalogCore.GetLutPreviewPath(lutPath, LutRoot, LutPreviewRoot);
            if (string.IsNullOrEmpty(preview) || !File.Exists(preview)) return;
            try
            {
                byte[] bytes = File.ReadAllBytes(preview);
                using (MemoryStream ms = new MemoryStream(bytes))
                using (Image source = Image.FromStream(ms))
                {
                    picLutPreview.Image = new Bitmap(source);
                }
                lutToolTip.SetToolTip(picLutPreview, preview);
            }
            catch (Exception ex)
            {
                if (log != null) AppendLog("[LUT Preview] " + ex.Message + Environment.NewLine);
            }
        }

        private void OpenLutGallery()
        {
            string selector = Path.Combine(appRoot, "_LUT_Tools", "LUT_Gallery_Selector.ps1");
            string root = LutRoot;
            if (!File.Exists(selector))
            {
                MessageBox.Show(this, LF("error.lut_gallery_missing", selector), "Film Grain Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!Directory.Exists(root))
            {
                MessageBox.Show(this, LF("error.lut_root_missing", root), "Film Grain Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string pick = Path.Combine(Path.GetTempPath(), "FilmGrainStudio_NET_LUT_" + Guid.NewGuid().ToString("N") + ".txt");
            btnLutGallery.Enabled = false;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardError = true;
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -STA -File " + QuoteProcessArgument(selector) +
                                " -LutRoot " + QuoteProcessArgument(root) +
                                " -PreviewRoot " + QuoteProcessArgument(LutPreviewRoot) +
                                " -OutputFile " + QuoteProcessArgument(pick);
                using (Process proc = Process.Start(psi))
                {
                    string error = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();
                    int rc = proc.ExitCode;
                    if (rc == 0 && File.Exists(pick))
                    {
                        string selected = File.ReadAllText(pick, Encoding.UTF8).Trim();
                        if (selected.Length > 0)
                        {
                            selectedLutPath = selected;
                            selectedLutSource = "Gallery";
                            chkLut.Checked = true;
                        }
                    }
                    else if (rc == 10)
                    {
                        selectedLutPath = "";
                        selectedLutSource = "None";
                        chkLut.Checked = false;
                    }
                    else if (rc != 11)
                    {
                        string detail = (error ?? "").Trim();
                        if (detail.Length > 2000) detail = detail.Substring(0, 2000);
                        string message = detail.Length > 0 ? LF("error.lut_gallery_failed", rc, detail) : LF("error.lut_gallery_failed_no_detail", rc);
                        MessageBox.Show(this, message, "Film Grain Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, LF("error.lut_gallery_open_failed", ex.Message), "Film Grain Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnLutGallery.Enabled = true;
                try { if (File.Exists(pick)) File.Delete(pick); } catch { }
                RefreshLutLists();
                UpdateLutUi();
            }
        }

        private void UpdateColorCorrectionUi()
        {
            if (btnColorCorrection == null) return;
            string baseText = lang.T("button.color_correction");
            btnColorCorrection.Text = colorCorrectionEnabled ? baseText.TrimEnd('\u2026') + "  ✓" : baseText;
            UpdateHdrRouteStatus(GetSelectedMediaInfo());
        }

        private string GetPreviewVideoPath()
        {
            if (listFiles.SelectedItems.Count == 1) return listFiles.SelectedItems[0].Tag as string ?? "";
            if (listFiles.Items.Count == 1) return listFiles.Items[0].Tag as string ?? "";
            return "";
        }

        private void OpenColorCorrection()
        {
            string videoPath = GetPreviewVideoPath();
            if (string.IsNullOrEmpty(videoPath))
            {
                MessageBox.Show(this, lang.T("color.select_video"), "Film Grain Studio", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            config.Load();
            string ffmpeg = Path.Combine(config.Get("FFMPEG_DIR"), "ffmpeg.exe");
            string ffprobe = Path.Combine(config.Get("FFMPEG_DIR"), "ffprobe.exe");
            string selector = Path.Combine(appRoot, "_LUT_Tools", "LUT_Gallery_Selector.ps1");
            if (!File.Exists(ffmpeg) || !File.Exists(ffprobe))
            {
                MessageBox.Show(this, "FFmpeg / FFprobe not found:\r\n" + config.Get("FFMPEG_DIR"), "Film Grain Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int[] strengths = new int[] { 25, 50, 75, 100 };
            int lutStrength = strengths[Math.Max(0, Math.Min(3, trackLutStrength.Value))];
            btnColorCorrection.Enabled = false;
            if (log != null) AppendLog("[Preview] Opening native .NET color correction..." + Environment.NewLine);

            try
            {
                using (NativeColorCorrectionForm dlg = new NativeColorCorrectionForm(
                    videoPath,
                    ffmpeg,
                    ffprobe,
                    selector,
                    LutRoot,
                    LutPreviewRoot,
                    selectedLutPath,
                    selectedLutSource,
                    lutStrength,
                    colorCorrectionEnabled,
                    colorContrast,
                    colorBrightness,
                    colorSaturation,
                    colorGamma,
                    colorBlackWhite,
                    chkLut.Checked && !string.IsNullOrEmpty(selectedLutPath),
                    config.GetBool("COLOR_PREVIEW_LARGE_UI"),
                    lang.Code))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
                    {
                        ColorCorrectionResult result = dlg.Result;
                        colorCorrectionEnabled = result.Enabled;
                        colorContrast = result.Contrast;
                        colorBrightness = result.Brightness;
                        colorSaturation = result.Saturation;
                        colorGamma = result.Gamma;
                        colorBlackWhite = result.BlackWhite;
                        selectedLutPath = result.LutPath ?? "";
                        selectedLutSource = string.IsNullOrEmpty(selectedLutPath) ? "None" : (result.LutSource ?? "Gallery");
                        trackLutStrength.Value = result.LutStrength == 25 ? 0 : (result.LutStrength == 50 ? 1 : (result.LutStrength == 100 ? 3 : 2));
                        chkLut.Checked = result.UseLut && !string.IsNullOrEmpty(selectedLutPath);

                        Dictionary<string, string> updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        updates["COLOR_CORRECTION_ENABLED"] = colorCorrectionEnabled ? "true" : "false";
                        updates["COLOR_CONTRAST"] = colorContrast.ToString("0.00", CultureInfo.InvariantCulture);
                        updates["COLOR_BRIGHTNESS"] = colorBrightness.ToString("0.00", CultureInfo.InvariantCulture);
                        updates["COLOR_SATURATION"] = colorSaturation.ToString("0.00", CultureInfo.InvariantCulture);
                        updates["COLOR_GAMMA"] = colorGamma.ToString("0.00", CultureInfo.InvariantCulture);
                        updates["COLOR_BLACK_WHITE"] = colorBlackWhite ? "true" : "false";
                        config.Save(updates);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Film Grain Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnColorCorrection.Enabled = true;
                RefreshLutLists();
                UpdateLutUi();
                UpdateColorCorrectionUi();
                UpdateHdrRouteStatus(GetSelectedMediaInfo());
                if (log != null) AppendLog("[Preview] Native color correction closed." + Environment.NewLine);
            }
        }

        private void PreviewOnly(string message)
        {
            if (log != null) AppendLog("[Preview] " + message + Environment.NewLine);
            if (lblRunStage != null) lblRunStage.Text = ".NET Preview · no encoding";
            MessageBox.Show(this, message, "Film Grain Studio .NET Preview", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
