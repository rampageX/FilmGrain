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

// Explorer VERSIONINFO and managed assembly versions follow the same product constant.
[assembly: System.Reflection.AssemblyTitle("FilmGrain Studio")]
[assembly: System.Reflection.AssemblyProduct("FilmGrain Studio")]
[assembly: System.Reflection.AssemblyDescription("FilmGrain Studio")]
[assembly: System.Reflection.AssemblyVersion(FilmGrainStudioPreview.Program.FileReleaseVersion)]
[assembly: System.Reflection.AssemblyFileVersion(FilmGrainStudioPreview.Program.FileReleaseVersion)]
[assembly: System.Reflection.AssemblyInformationalVersion(FilmGrainStudioPreview.Program.ReleaseVersion)]

namespace FilmGrainStudioPreview
{
    // A session log survives visible-log trimming/clearing and is flushed before UI updates.
    // Display policy only: process parsing and complete file logs remain unchanged.
    internal static class VisibleLogCore
    {
        internal static bool ShowProcessLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            string value = line.Trim();
            if (Regex.IsMatch(value, @"^(frame|fps|stream_\d+_\d+_q|bitrate|total_size|out_time_us|out_time_ms|out_time|dup_frames|drop_frames|speed|progress)=\s*(?:[-+0-9.:/]+(?:kbits/s|x)?|N/A|continue|end)\s*$", RegexOptions.IgnoreCase))
                return false;
            if (Regex.IsMatch(value, @"^frame=\s*\d+.*\bfps=\s*[0-9.]+.*\btime=\s*[0-9.:]+.*\bspeed=\s*[0-9.]+x(?:\s+elapsed=[0-9:.]+)?\s*$", RegexOptions.IgnoreCase))
                return false;
            if (Regex.IsMatch(value, @"^\[vspipe\] Frame:\s*\d+/\d+(?:\s*\([0-9.]+ fps\))?\s*$", RegexOptions.IgnoreCase)) return false;
            if (value.StartsWith("ffmpeg version ", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("built with ", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("configuration: ", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(value, @"^lib(?:avutil|avcodec|avformat|avdevice|avfilter|swscale|swresample)\s+\d+\.", RegexOptions.IgnoreCase))
                return false;
            return true;
        }
    }

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
        internal const string ReleaseVersion = "5.0.35";
        internal const string FileReleaseVersion = "5.0.35.0";
        internal const string BuildLabel = "Stable";
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--hardware-gpu-identity")
            { NativeStartupCore.WriteProtocolLine(new JavaScriptSerializer().Serialize(NativeHardwareProbe.WmiGpuIdentity())); return; }
            if (args.Length == 1 && args[0] == "--hardware-nvenc-engines")
            { NativeStartupCore.WriteProtocolLine("FGS_NVENC_ENGINES=" + FilmGrainNvencCaps.GetAv1EncoderEngineCount(0)); return; }
            if(args.Length==1&&args[0]=="--hardware-hevc-engines")
            {NativeStartupCore.WriteProtocolLine("FGS_NVENC_ENGINES="+FilmGrainNvencCaps.GetHevcEncoderEngineCount(0));return;}
            StartupTiming.Mark("entry", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            NativeStartupOptions startup = NativeStartupCore.Parse(args, Environment.CurrentDirectory);
            if (startup.CheckOnly)
            {
                try
                {
                    string root = NativeStartupCore.FindAppRoot(AppDomain.CurrentDomain.BaseDirectory);
                    NativeStartupCore.WriteProtocolLine(new JavaScriptSerializer().Serialize(new { app_root = root, inputs = startup.Inputs, notices = startup.Notices, build = BuildLabel, version = ReleaseVersion, startup = "Native .NET" }));
                    Environment.ExitCode = startup.Notices.Count == 0 ? 0 : 2;
                }
                catch (AppRootNotFoundException ex)
                {
                    NativeStartupCore.WriteProtocolLine(new JavaScriptSerializer().Serialize(new { error = ex.Message, exe_directory = ex.StartPath }));
                    Environment.ExitCode = 1;
                }
                return;
            }
            bool ownsInstance;
            using (Mutex instance = new Mutex(true, @"Local\FilmGrainStudio.MainWindow", out ownsInstance))
            {
                if (!ownsInstance)
                {
                    MessageBox.Show("Film Grain Studio 已在运行，请切换到现有窗口。 / Film Grain Studio is already running.", "Film Grain Studio", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try
                {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                MainForm form = new MainForm(startup.SetupTest);
                form.startupInputs = startup;
                Application.Run(form);
            }
            catch (AppRootNotFoundException ex)
            {
                MessageBox.Show(
                    "找不到 Film Grain Studio 的完整程序目录。\n\n" +
                    "请将 EXE 放回完整 Film Grain Studio 根目录，或原 dotnet\\build 目录。\n" +
                    "该目录还需要包含 FilmGrain_Config.default.ini 和 Lang 文件夹。\n\n" +
                    "Cannot find the complete Film Grain Studio folder. Restore the EXE to the application root or dotnet\\build.\n\n" +
                    "当前 EXE 位置 / EXE location:\n" + ex.StartPath,
                    "Film Grain Studio - 启动位置不正确 / Incorrect location",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Film Grain Studio .NET", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
                }
                finally { instance.ReleaseMutex(); }
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
        // Compose the first client frame, then restore normal independent child painting.
        private bool composeStartupFrame=true;
        protected override CreateParams CreateParams
        {
            get {var cp=base.CreateParams;if(composeStartupFrame)cp.ExStyle|=0x02000000;return cp;}
        }


        // Shell progress is a UI-only mirror of the existing coordinator progress.
        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
        [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarProgress
        {
            void HrInit();
            void AddTab(IntPtr window);
            void DeleteTab(IntPtr window);
            void ActivateTab(IntPtr window);
            void SetActiveAlt(IntPtr window);
            void MarkFullscreenWindow(IntPtr window, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool fullscreen);
            void SetProgressValue(IntPtr window, ulong completed, ulong total);
            void SetProgressState(IntPtr window, uint state);
        }
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string message);
        private uint taskbarCreatedMessage;
        private ITaskbarProgress taskbarProgress;
        private bool taskbarReady;
        private uint taskbarProgressState;
        private int taskbarProgressValue;
        protected override void OnHandleCreated(EventArgs e)
        {
            taskbarCreatedMessage=RegisterWindowMessage("TaskbarButtonCreated");
            base.OnHandleCreated(e);
        }
        protected override void WndProc(ref Message m)
        {
            if(m.Msg==0x0014&&m.WParam!=IntPtr.Zero) {
                using(var graphics=Graphics.FromHdc(m.WParam))
                using(var brush=new SolidBrush(BackColor))graphics.FillRectangle(brush,ClientRectangle);
                m.Result=new IntPtr(1);return;
            }
            base.WndProc(ref m);
            if(taskbarCreatedMessage!=0&&(uint)m.Msg==taskbarCreatedMessage) {
                ReleaseTaskbarProgress();
                taskbarReady=true;
                ApplyTaskbarProgress();
            }
        }
        protected override void OnHandleDestroyed(EventArgs e)
        {
            taskbarReady=false;
            ReleaseTaskbarProgress();
            base.OnHandleDestroyed(e);
        }
        private void ReleaseTaskbarProgress()
        {
            if(taskbarProgress==null)return;
            try {System.Runtime.InteropServices.Marshal.FinalReleaseComObject(taskbarProgress);}
            catch(System.Runtime.InteropServices.COMException) { }
            finally {taskbarProgress=null;}
        }
        private void SetTaskbarProgress(uint state,int value)
        {
            if(taskbarProgressState==state&&taskbarProgressValue==Math.Max(0,Math.Min(1000,value)))return;
            taskbarProgressState=state;
            taskbarProgressValue=Math.Max(0,Math.Min(1000,value));
            ApplyTaskbarProgress();
        }
        private void ApplyTaskbarProgress()
        {
            if(!taskbarReady||!IsHandleCreated||IsDisposed||Disposing)return;
            try {
                if(taskbarProgress==null) {
                    taskbarProgress=(ITaskbarProgress)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11D0-958A-006097C9A090")));
                    taskbarProgress.HrInit();
                }
                taskbarProgress.SetProgressState(Handle,taskbarProgressState);
                if(taskbarProgressState==2)taskbarProgress.SetProgressValue(Handle,(ulong)taskbarProgressValue,1000);
            } catch(System.Runtime.InteropServices.COMException) {ReleaseTaskbarProgress();}
              catch(NotSupportedException) {ReleaseTaskbarProgress();}
            // Shell failures must never interrupt media processing.
        }

        private const string TestBuild = Program.BuildLabel;
        private readonly Color ColorHeader = Color.FromArgb(45, 57, 72);
        private readonly Color ColorAccent = Color.FromArgb(47, 111, 173);
        private readonly Color ColorSubtle = Color.FromArgb(242, 244, 247);
        private readonly Color ColorMuted = Color.FromArgb(100, 107, 116);

        private readonly string appRoot;
        private readonly FgsConfig config;
        private readonly bool setupTestMode;
        private readonly LanguagePack lang;
        private readonly List<LanguageChoice> languages;
        private readonly MediaProbeCache mediaProbeCache = new MediaProbeCache();
        private MediaProbeCore mediaProbeCore;
        private readonly Dictionary<string, string> av1GrainInspectCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Av1GrainInspectCore av1GrainInspectCore;

        private ListView listFiles;
        private Label lblMediaInfo;
        private Label lblHdrRoute;
        private Button btnHdrShortcut;
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
        
        
        
        
        private ComboBox cmbGrainMode;
        private CheckBox chkGrainEnabled;
        private bool ordinaryGrainEnabled = true;
        private bool updatingGrainEnableUi;
        private Panel grainContent;
        private GroupBox grpGrain;
        private GroupBox grpLut;
        private Button btnSubtitle;
        private Label lblFrameHelp;
        private Button btnStart;
        private CheckBox chkEncodePreview;
        private MaskedTextBox txtPreviewStart;
        private ThemeIntegerCombo numPreviewDuration;
        private Panel encodePreviewPanel;
        private Button btnCancelTask;
        private ProgressBar progressRun;
        private ToolStripStatusLabel statusHardware;
        private ToolStripStatusLabel statusFfmpeg;
        private readonly Dictionary<string, ToolStripStatusLabel> statusCapabilities = new Dictionary<string, ToolStripStatusLabel>();
        private bool updatingFrucUi;
        private bool hardwareRefreshQueued;
        private string hardwareActiveKey, hardwareAppliedKey;
        private bool hardwareForceQueued;
        private int previousSvpSelection;
        private BridgeTaskCoordinator bridgeTaskCoordinator;
        private HardwareCapabilityCore hardwareCapabilityCore;
        private HardwareCapabilitySnapshot hardwareCaps;
        private bool hardwareCapsReady;
        private bool hardwareDetectionInProgress;
        private string hardwareNativeAppliedKey, hardwareActiveNativeKey, hardwareFrucAppliedKey, hardwareActiveFrucKey;
        private bool changingCodecForHardware;
        private bool noReencodeUiActive;
        private bool activeRunNativeBackend;
        private string activeRunCodecName = "";
        private TrackBar trackFilmGrainStrength;
        private TrackBar av1StrengthControl;
        private int Av1GrainStrengthPercent { get { int v; return int.TryParse(config.Get("AV1_GRAIN_STRENGTH"),out v)?GrainControlCore.SnapTen(v):100; } }
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
        private int fgsimPresetIndex = 2;
        private string selectedGrainPlatePath = "";
        private int scannedGrainStrengthPercent = 80;
        private CheckBox chkLut;
        private ComboBox cmbRecentLut;
        private ComboBox cmbFavoriteLut;
        private TrackBar trackLutStrength;
        private LutThumbnailRenderer lutThumbnail;
        private readonly LutSessionReference lutSessionReference=new LutSessionReference();
        private Label lblLutStrength;
        private Label lblLutCompoundHint;
        private RowStyle lutCompoundHintRow;
        private string lutTypeSignature="";
        private bool selectedLutIsCompound;
        private Label lblSelectedLut;
        private PictureBox picLutPreview;
        private Button btnLutGallery;
        private Button btnColorCorrection;
        private readonly ToolTip lutToolTip = new ThemeToolTip();
        private readonly ToolTip sfeToolTip = new ThemeToolTip();
        private ComboBox cmbLanguage;
        private Button btnFgsimTextureUpdate;
        private Label lblFgsimTextureStatus;

        private bool loadingLutLists;
        private bool updatingBitrateUi;
        private int lastCodecIndex = 0;
        private CheckBox chkCq;
        private Control rateControlLabel;
        private readonly string[] bitrateChoices = new string[] { "3000", "3500", "4000", "5000", "6000", "7000", "7500", "8000", "9000", "10000", "11000", "12000", "15000", "18000", "20000", "22000", "30000" };
        private readonly string[] modeBitrate = new string[] { "5000", "6000", "7500", "7500" };
        private readonly bool[] modeBitrateAuto = new bool[] { true, true, true, true };
        
        private string selectedLutPath = "";
        private string selectedLutSource = "None";
        private bool colorCorrectionEnabled;
        private double colorContrast = 1.0;
        private double colorBrightness = 0.0;
        private double colorSaturation = 1.0;
        private double colorGamma = 1.0;
        private bool colorBlackWhite;

        // One atomic config value: x,y,width,height,Normal|Maximized.
        private FormWindowState rememberedWindowState = FormWindowState.Normal;
        private bool windowMemoryReady;
        private bool SuppressWindowMemory {
            get { return setupTestMode || Environment.GetEnvironmentVariable("FGS_AUTOMATION") == "1"
                || Environment.GetEnvironmentVariable("FGS_NO_WINDOW_STATE") == "1"; }
        }
        internal static Rectangle FitWindowBounds(Rectangle saved, Rectangle[] areas, Size minimum)
        {
            if (areas == null || areas.Length == 0) throw new ArgumentException("No working areas");
            Rectangle target = areas[0]; long best = -1;
            foreach (Rectangle area in areas) {
                Rectangle overlap = Rectangle.Intersect(saved, area);
                long score = (long)Math.Max(0, overlap.Width) * Math.Max(0, overlap.Height);
                if (score > best) { best = score; target = area; }
            }
            int width = Math.Max(minimum.Width, Math.Min(saved.Width, target.Width));
            int height = Math.Max(minimum.Height, Math.Min(saved.Height, target.Height));
            return new Rectangle(Math.Max(target.Left, Math.Min(saved.X, target.Right - width)),
                Math.Max(target.Top, Math.Min(saved.Y, target.Bottom - height)), width, height);
        }
        private void InitializeWindowMemory()
        {
            if (SuppressWindowMemory) return;
            Load += delegate {
                try {
                    string[] parts = config.Get("MAIN_WINDOW").Split(',');
                    int x,y,w,h;
                    if (parts.Length == 5 && int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y)
                        && int.TryParse(parts[2], out w) && int.TryParse(parts[3], out h)
                        && Math.Abs((long)x) <= 100000 && Math.Abs((long)y) <= 100000
                        && w >= MinimumSize.Width && h >= MinimumSize.Height && w <= 32767 && h <= 32767
                        && (parts[4] == "Normal" || parts[4] == "Maximized")) {
                        var workingAreas = new List<Rectangle>();
                        foreach (Screen display in Screen.AllScreens) workingAreas.Add(display.WorkingArea);
                        Rectangle bounds = FitWindowBounds(new Rectangle(x,y,w,h),
                            workingAreas.ToArray(), MinimumSize);
                        StartPosition = FormStartPosition.Manual; Bounds = bounds;
                        rememberedWindowState = parts[4] == "Maximized" ? FormWindowState.Maximized : FormWindowState.Normal;
                        WindowState = rememberedWindowState;
                    }
                } catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Window restore: " + ex.Message); }
                windowMemoryReady = true;
            };
            Resize += delegate {
                if (windowMemoryReady && WindowState != FormWindowState.Minimized) rememberedWindowState = WindowState;
            };
            FormClosed += delegate {
                if (!windowMemoryReady || SuppressWindowMemory) return;
                try {
                    Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                    if (b.Width < MinimumSize.Width || b.Height < MinimumSize.Height) return;
                    config.SaveValue("MAIN_WINDOW", string.Join(",", new string[] {
                        b.X.ToString(CultureInfo.InvariantCulture), b.Y.ToString(CultureInfo.InvariantCulture),
                        b.Width.ToString(CultureInfo.InvariantCulture), b.Height.ToString(CultureInfo.InvariantCulture),
                        rememberedWindowState == FormWindowState.Maximized ? "Maximized" : "Normal" }));
                } catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Window save: " + ex.Message); }
            };
        }

        public MainForm(bool setupTest) : this(setupTest, null) {}

        internal MainForm(bool setupTest, HardwareCapabilityCore injectedHardware)
        {
            StartupTiming.Mark("constructor.begin");
            appRoot = FindAppRoot(AppDomain.CurrentDomain.BaseDirectory);
            setupTestMode = setupTest;
            string testConfig = setupTest ? Path.Combine(Path.GetTempPath(), "FGS_Setup_Test_" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + ".ini") : null;
            config = new FgsConfig(appRoot, testConfig);
            try { NetworkProxyCore.Apply(config.Get("NETWORK_PROXY_MODE"), config.Get("NETWORK_PROXY_URL")); }
            catch (Exception ex) { NetworkProxyCore.Apply(NetworkProxyCore.SystemMode, ""); MessageBox.Show("网络代理配置无效，已改用系统代理：\r\n" + ex.Message, "Film Grain Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            lang = new LanguagePack(appRoot);
            languages = lang.GetChoices();
            ordinaryGrainEnabled = config.GetBool("GRAIN_ENABLED");
            colorCorrectionEnabled = config.GetBool("COLOR_CORRECTION_ENABLED");
            colorContrast = config.GetDouble("COLOR_CONTRAST", 1.0);
            colorBrightness = config.GetDouble("COLOR_BRIGHTNESS", 0.0);
            colorSaturation = config.GetDouble("COLOR_SATURATION", 1.0);
            colorGamma = config.GetDouble("COLOR_GAMMA", 1.0);
            colorBlackWhite = config.GetBool("COLOR_BLACK_WHITE");
            StartupTiming.Mark("config.language.proxy.end");
            BuildUi();
            StartupTiming.Mark("controls.end");
            if (setupTest) Text = "[设置测试模式] " + Text;
            UpdateSpeedChoices();
            bridgeTaskCoordinator = new BridgeTaskCoordinator();
            InitializeRunUiUpdates();
            bridgeTaskCoordinator.StateChanged += OnBridgeTaskStateChanged;
            bridgeTaskCoordinator.ItemCompleted += OnQueueItemCompleted;
            bridgeTaskCoordinator.LogLine += OnBridgeExecutionLogLine;
            bridgeTaskCoordinator.ProgressChanged += OnBridgeExecutionProgressChanged;
            bridgeTaskCoordinator.Completed += OnBridgeExecutionCompleted;
            hardwareCapabilityCore = injectedHardware ?? new HardwareCapabilityCore();
            hardwareCapabilityCore.Completed += OnHardwareCapabilityCompleted;
            mediaProbeCache.ProbePath = delegate { return Path.Combine(config.Get("FFMPEG_DIR"), "ffprobe.exe"); };
            mediaProbeCore = new MediaProbeCore();
            mediaProbeCore.Completed += OnMediaProbeCompleted;
            av1GrainInspectCore = new Av1GrainInspectCore();
            av1GrainInspectCore.Completed += OnAv1GrainInspectCompleted;
            SetLutListPlaceholder(false);
            UpdateLutUi();
            UpdateColorCorrectionUi();
            UpdateCinematicCropLabels();
            UpdateStatusCount();
            UpdateMediaDrivenUi(null);
            // Restore final capability-dependent UI before theme/first paint. No detector process here.
            bool fullStartupCache = false;
            HardwareCapabilitySnapshot startupCaps = null;
            HardwareCapabilityRequest startupRequest = null;
            try {
                if(injectedHardware == null && !setupTestMode) {
                    startupRequest = CreateHardwareRequest();
                    fullStartupCache = hardwareCapabilityCore.TryRestorePersistent(startupRequest,out startupCaps);
                }
            } catch(Exception ex) { Trace.WriteLine("Startup capability cache: " + ex.Message); }
            if(startupCaps != null && startupCaps.Ready) {
                hardwareCaps = startupCaps; hardwareCapsReady = true;
                hardwareDetectionInProgress = !fullStartupCache;
                hardwareNativeAppliedKey = hardwareCapabilityCore.LastLookupNativeKey;
                hardwareActiveNativeKey = hardwareNativeAppliedKey;
                hardwareFrucAppliedKey = startupCaps.FrucReady ? hardwareCapabilityCore.LastLookupFrucKey : null;
                hardwareActiveFrucKey = hardwareCapabilityCore.LastLookupFrucKey;
                hardwareAppliedKey = hardwareCapabilityCore.LastLookupRequestKey;
                hardwareActiveKey = hardwareAppliedKey;
                ApplyHardwareCapabilities();
                AppendLog("[Hardware] " + startupCaps.GpuName + " · AV1=" + (startupCaps.Av1Available ? "1" : "0") + " · HEVC=" + (startupCaps.HevcAvailable ? "1" : "0") + " · x264=" + (startupCaps.X264Available ? "1" : "0") + Environment.NewLine);
                btnStart.Enabled = true;
                StartupTiming.Mark("hardware.cache.restore", "full=" + fullStartupCache + " native=0 fruc=0 children=0");
            }
            StartupTiming.Mark("initial.ui.end");
            InitializeTheme();
            StartupTiming.Mark("theme.end");
            InitializeWindowMemory();
            if(!hardwareCapsReady) {
                hardwareDetectionInProgress = true;
                btnStart.Enabled = false;
                SetCapabilityStatusPending();
                statusHardware.Text = UiText("正在初始化资源…", "Initializing resources...");
            }
            FormClosed += delegate { startupClosing = true; Application.Idle -= OnStartupIdle; };
            StartupTiming.Attach(this);
            Application.Idle += OnStartupIdle;
            StartupTiming.Mark("constructor.end");
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
                    bridgeTaskCoordinator.ItemCompleted -= OnQueueItemCompleted;
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
            return NativeStartupCore.FindAppRoot(start);
        }

        private Font UiFont(float size, FontStyle style)
        {
            return new Font("Segoe UI", size, style, GraphicsUnit.Point);
        }

        private void BuildUi()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
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

            Label title = new ThemeLabel();
            title.Text = "Film Grain Studio";
            title.ForeColor = Color.White;
            title.Font = UiFont(18f, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(20, 10);
            header.Controls.Add(title);

            Label subtitle = new ThemeLabel();
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

            cmbLanguage = new ThemeComboBox();
            cmbLanguage.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbLanguage.Size = new Size(116, 28);
            cmbLanguage.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            foreach (LanguageChoice item in languages) cmbLanguage.Items.Add(item);
            for (int i = 0; i < languages.Count; i++)
            {
                if (string.Equals(languages[i].Code, lang.Preference, StringComparison.OrdinalIgnoreCase))
                {
                    cmbLanguage.SelectedIndex = i;
                    break;
                }
            }
            cmbLanguage.Enabled = true;
            ToolTip tip = new ThemeToolTip();
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

            Label baseline = new ThemeLabel();
            baseline.Text = lang.T("app.core") + "  ·  .NET";
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
            Button b = new ThemeButton();
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
            GroupBox box = new ThemeGroupBox();
            box.Text = lang.T("input.group");
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(0, 0, 6, 0);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(7, 6, 7, 7);
            layout.ColumnCount = 1;
            layout.RowCount = 4;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            box.Controls.Add(layout);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = false;
            Button add = new ThemeButton(); add.Text = lang.T("button.add"); add.Size = new Size(92, 29);
            Button remove = new ThemeButton(); remove.Text = lang.T("button.remove"); remove.Size = new Size(82, 29);
            Button clear = new ThemeButton(); clear.Text = lang.T("button.clear"); clear.Size = new Size(62, 29);
            add.Click += delegate { AddFilesWithDialog(); };
            remove.Click += delegate { RemoveSelected(); };
            clear.Click += delegate { ClearInputFiles(); };
            buttons.Controls.Add(add); buttons.Controls.Add(remove); buttons.Controls.Add(clear);
            var bake = new ThemeButton { Text = UiText("AV1 烘焙...", "AV1 Bake..."), Size = new Size(106,29) };
            bake.Click += delegate { ShowAv1BakeDialog(); }; buttons.Controls.Add(bake);
            var inputBars=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=new Padding(0) };
            inputBars.RowStyles.Add(new RowStyle(SizeType.Absolute,36));inputBars.RowStyles.Add(new RowStyle(SizeType.Absolute,34));
            inputBars.Controls.Add(buttons,0,0);inputBars.Controls.Add(BuildFileSettingsBar(),0,1);
            layout.Controls.Add(inputBars, 0, 0);

            listFiles = new ThemeListView();
            listFiles.Dock = DockStyle.Fill;
            listFiles.View = View.Details;
            listFiles.FullRowSelect = true;
            listFiles.GridLines = true;
            listFiles.HideSelection = false;
            listFiles.AllowDrop = true;
            listFiles.ShowItemToolTips = true;
            listFiles.Columns.Add(lang.T("input.column.filename"), 178);
            listFiles.Columns.Add(lang.T("input.column.size"), 72);
            listFiles.Columns.Add(UiText("状态","Status"), 90);
            listFiles.ClientSizeChanged += delegate { FitFileColumns(); };
            listFiles.ColumnWidthChanged += delegate {
                int total=0;foreach(ColumnHeader column in listFiles.Columns)total+=column.Width;
                if(!fittingFileColumns && total>Math.Max(0,listFiles.ClientSize.Width-4))FitFileColumns();
            };
            listFiles.ColumnWidthChanging += delegate(object sender, ColumnWidthChangingEventArgs e) {
                int total=0; for(int i=0;i<listFiles.Columns.Count;i++) total += i==e.ColumnIndex ? e.NewWidth : listFiles.Columns[i].Width;
                if(total>Math.Max(0,listFiles.ClientSize.Width-4)) { e.Cancel=true; e.NewWidth=listFiles.Columns[e.ColumnIndex].Width; }
            };
            FitFileColumns();
            listFiles.DragEnter += OnDragEnter;
            listFiles.DragDrop += OnDragDrop;
            listFiles.SelectedIndexChanged += delegate { SelectFileEditor(); UpdateSelectedInfo(); };
            layout.Controls.Add(listFiles, 0, 1);

            GroupBox info = new ThemeGroupBox();
            info.Text = lang.T("input.info_group");
            info.Dock = DockStyle.Fill;
            info.Margin = new Padding(0, 5, 0, 3);
            lblMediaInfo = new ThemeLabel();
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
            Label note = new ThemeLabel();
            note.Dock = DockStyle.Fill;
            note.ForeColor = ColorMuted;
            note.Padding = new Padding(6, 3, 4, 0);
            note.AutoEllipsis = true;
            note.Text = lang.T("input.note");
            inputStatus.Controls.Add(note, 0, 0);
            lblHdrRoute = new ThemeLabel();
            lblHdrRoute.Dock = DockStyle.Fill;
            lblHdrRoute.ForeColor = ColorMuted;
            lblHdrRoute.Font = UiFont(8.5f, FontStyle.Bold);
            lblHdrRoute.Padding = new Padding(6, 0, 4, 0);
            lblHdrRoute.AutoEllipsis = true;
            lblHdrRoute.Text = lang.T("input.hdr_route_pending");
            inputStatus.Controls.Add(WithAdvancedShortcut(lblHdrRoute, 2, "HDR", out btnHdrShortcut), 0, 1);
            btnHdrShortcut.Visible = false;
            layout.Controls.Add(inputStatus, 0, 3);
            return box;
        }

        private Control WithAdvancedShortcut(Control content, int tabIndex, string page, out Button shortcut)
        {
            TableLayoutPanel row = new TableLayoutPanel();
            row.Dock = DockStyle.Fill; row.Margin = Padding.Empty;
            row.ColumnCount = 2; row.RowCount = 1;
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
            content.Dock = DockStyle.Fill;
            shortcut = new ThemeButton();
            shortcut.Text = ""; shortcut.Dock = DockStyle.Fill;
            shortcut.Margin = Padding.Empty;
            shortcut.Padding = Padding.Empty;
            shortcut.FlatStyle = FlatStyle.Flat; shortcut.FlatAppearance.BorderSize = 0;
            shortcut.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 235, 250);
            shortcut.ForeColor = Color.FromArgb(45, 110, 170);
            // Draw the arrow in the available client area, including short HDR rows.
            shortcut.Paint += delegate(object sender, PaintEventArgs e) {
                Button button = (Button)sender;
                float scale = e.Graphics.DpiX / 96f;
                float size = Math.Min(8f * scale, Math.Min(button.ClientSize.Width, button.ClientSize.Height) - 4f);
                if (size < 3f) return;
                float x = (button.ClientSize.Width - size) / 2f;
                float y = (button.ClientSize.Height - size) / 2f;
                using (System.Drawing.Pen pen = new System.Drawing.Pen(button.Enabled ? button.ForeColor : SystemColors.GrayText, 1.3f * scale)) {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    e.Graphics.DrawLine(pen, x, y + size, x + size, y);
                    e.Graphics.DrawLine(pen, x + size * 0.3f, y, x + size, y);
                    e.Graphics.DrawLine(pen, x + size, y, x + size, y + size * 0.7f);
                }
            };
            shortcut.Cursor = Cursors.Hand;
            if(tabIndex==0)shortcut.Name="ENCODER_SPEED_SHORTCUT";
            shortcut.AccessibleName = UiText("打开高级设置：", "Open advanced settings: ") + page;
            lutToolTip.SetToolTip(shortcut, shortcut.AccessibleName);
            shortcut.Click += delegate { if(tabIndex==0)ShowEncoderQuickSettings();else if(tabIndex<0)OpenCinematicCrop();else ShowAdvancedSettingsDialog(tabIndex+2); };
            row.Controls.Add(content, 0, 0); row.Controls.Add(shortcut, 1, 0);
            return row;
        }

        private Control BuildEncodeGroup()
        {
            GroupBox box = new ThemeGroupBox();
            box.Text = lang.T("encode.group");
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(6, 0, 6, 0);

            TableLayoutPanel t = new TableLayoutPanel();
            t.SuspendLayout();
            try {
            t.Dock = DockStyle.Fill;
            t.Padding = new Padding(5, 7, 5, 5);
            t.ColumnCount = 2;
            t.RowCount = 14;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 13; i++) t.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            box.Controls.Add(t);

            cmbCodec = Combo(new string[] { lang.T("codec.av1_default"), lang.T("codec.hevc_scan"), lang.T("codec.x264_default"), UiText("H.264 · NVENC", "H.264 · NVENC") }, 0);
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
            interp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            interp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            chkInterpolation = Check(lang.T("encode.enable"), false); chkInterpolation.Dock = DockStyle.Fill;
            cmbInterpolationMode = Combo(new string[] { "OpenSVP · " + lang.T("interp.smooth"), "OpenSVP · " + lang.T("interp.adaptive"), "FRUC Vulkan" }, 0);
            cmbInterpolationMode.Dock = DockStyle.Fill; cmbInterpolationMode.DropDownWidth = 340;
            cmbInterpolationMode.DrawMode = DrawMode.OwnerDrawFixed;
            cmbInterpolationMode.DrawItem += delegate(object sender, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                e.DrawBackground();
                bool disabled = e.Index == 2 && ((hardwareDetectionInProgress && !hardwareCapsReady) || hardwareCaps == null || !hardwareCaps.FrucReady || !hardwareCaps.FrucAvailable);
                TextRenderer.DrawText(e.Graphics, Convert.ToString(cmbInterpolationMode.Items[e.Index]), e.Font,
                    e.Bounds, disabled ? SystemColors.GrayText : e.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                e.DrawFocusRectangle();
            };
            cmbInterpolationFps = ComboEditable(new string[] { "60", "59.94", "60000/1001", "2x", "1.5x" }, config.Get("INTERPOLATION_TARGET_FPS"));
            cmbInterpolationFps.Dock = DockStyle.Fill; cmbInterpolationFps.DropDownWidth = 130;
            lutToolTip.SetToolTip(cmbInterpolationFps, UiText("目标帧率：绝对帧率、分数帧率或源帧率倍率；默认 60。", "Target rate: absolute FPS, a rational rate, or source-rate multiplier; default 60."));
            cmbInterpolationFps.Text = config.Get("INTERPOLATION_TARGET_FPS"); cmbInterpolationFps.Enabled = false;
            cmbInterpolationFps.TextChanged += delegate { if(restoringFileEditor)return; if (config.Get("INTERPOLATION_TARGET_FPS") != cmbInterpolationFps.Text.Trim()) try { config.Save(new Dictionary<string,string> { { "INTERPOLATION_TARGET_FPS", cmbInterpolationFps.Text.Trim() } }); } catch { } UpdateMediaDrivenUi(GetSelectedMediaInfo()); UpdateBitrateDisplays(); };
            cmbInterpolationMode.SelectedIndexChanged += delegate { if(restoringFileEditor)return;
                if (updatingFrucUi) return;
                if (cmbInterpolationMode.SelectedIndex == 2 &&
                    ((hardwareDetectionInProgress && !hardwareCapsReady) || hardwareCaps == null || !hardwareCaps.FrucReady || !hardwareCaps.FrucAvailable))
                {
                    MessageBox.Show(this, FrucUiReason(), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    cmbInterpolationMode.SelectedIndex = previousSvpSelection;
                    return;
                }
                if (cmbInterpolationMode.SelectedIndex >= 0 && cmbInterpolationMode.SelectedIndex < 2)
                    previousSvpSelection = cmbInterpolationMode.SelectedIndex;
                if (chkInterpolation.Checked) UpdateMediaDrivenUi(GetSelectedMediaInfo());
            };
            chkInterpolation.CheckedChanged += delegate { if(restoringFileEditor)return;
                if (chkInterpolation.Checked && cmbDeint != null && cmbDeint.SelectedIndex != 0) cmbDeint.SelectedIndex = 0;
                if (chkInterpolation.Checked && cmbInterpolationMode.SelectedIndex != 2 && (setupTestMode || (!File.Exists(Path.Combine(appRoot, "_OpenSVPFlow", ".venv", "Scripts", "vspipe.exe")) && !AllInputsBypassOpenSvp())))
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
            cmbDeintMethod = Combo(new string[] { lang.T("deint.vulkan"), lang.T("deint.cuda"), lang.T("deint.w3fdif") }, 1);
            cmbDeint.SelectedIndexChanged += delegate { if(restoringFileEditor)return; UpdateMediaDrivenUi(GetSelectedMediaInfo()); UpdateBitrateDisplays(); };
            chkCinematic = Check(lang.T("cinematic.enable"), true);
            cmbFrameMode = Combo(new string[] { lang.T("cinematic.letterbox"), lang.T("cinematic.crop") }, 0);

            btnSubtitle = new ThemeButton(); btnSubtitle.Text = lang.T("button.subtitle"); btnSubtitle.Dock = DockStyle.Fill; btnSubtitle.Margin = new Padding(3, 4, 8, 4);
            btnSubtitle.Click += delegate { ShowSubtitleDialog(); };
            RefreshSubtitleButton();

            lblFrameHelp = new ThemeLabel(); lblFrameHelp.Dock = DockStyle.Fill; lblFrameHelp.AutoEllipsis = true; lblFrameHelp.ForeColor = ColorMuted; lblFrameHelp.TextAlign = ContentAlignment.TopLeft; lblFrameHelp.Padding = new Padding(8, 6, 8, 0); lblFrameHelp.Text = lang.T("encode.frame_help");

            AddLabeledRow(t, 0, lang.T("encode.method"), BuildCodecPicker());
            AddLabeledRow(t, 1, lang.T("encode.container"), cmbContainer);
            Button speedShortcut;
            AddLabeledRow(t, 2, lang.T("encode.speed"), WithAdvancedShortcut(cmbSpeed, 0, lang.T("advanced.tab.encode"), out speedShortcut));
            speedShortcut.Visible = cmbCodec.SelectedIndex >= 0 && cmbCodec.SelectedIndex <= 3;
            cmbCodec.SelectedIndexChanged += delegate { speedShortcut.Visible = cmbCodec.SelectedIndex >= 0 && cmbCodec.SelectedIndex <= 3; };
            var rateModes = new FlowLayoutPanel { Dock=DockStyle.Fill, Margin=Padding.Empty, WrapContents=false };
            chkSfe.Dock=DockStyle.None;chkSfe.AutoSize=true;
            chkCq=Check(UiText("CQ 模式", "CQ mode"),false);chkCq.AutoSize=true;
            rateModes.Controls.Add(chkSfe);rateModes.Controls.Add(chkCq);t.Controls.Add(rateModes,1,3);
            chkCq.CheckedChanged += delegate {
                if(restoringFileEditor||updatingBitrateUi)return;
                int index=cmbCodec.SelectedIndex;if(index!=0&&index!=1&&index!=3&&!NewCpuSelected())return;
                config.SaveValue(RateControlKey("RATE_CONTROL"),chkCq.Checked?"CQ":"VBR");
                LoadBitrateStateForCodec();UpdateBitrateDisplays();
            };
            AddLabeledRow(t, 4, lang.T("encode.bitrate"), bitrate);
            rateControlLabel=t.GetControlFromPosition(0,4);
            AddLabeledRow(t, 5, lang.T("encode.fps"), cmbFps);
            Button interpShortcut;
            AddLabeledRow(t, 6, lang.T("encode.interpolation"), WithAdvancedShortcut(interp, 1, lang.T("advanced.tab.interp"), out interpShortcut));
            AddLabeledRow(t, 7, lang.T("encode.deinterlace"), cmbDeint);
            AddLabeledRow(t, 8, lang.T("encode.deinterlace_method"), cmbDeintMethod);
            Button cinematicShortcut;
            Control cinematicRow = WithAdvancedShortcut(chkCinematic, -1, UiText("可视化裁剪", "Visual crop"), out cinematicShortcut);
            cinematicShortcut.Enabled = chkCinematic.Checked;
            chkCinematic.CheckedChanged += delegate { cinematicShortcut.Enabled = chkCinematic.Checked; };
            t.Controls.Add(cinematicRow, 0, 9); t.SetColumnSpan(cinematicRow, 2);
            AddLabeledRow(t, 10, lang.T("encode.framing"), cmbFrameMode);
            t.Controls.Add(btnSubtitle, 0, 11); t.SetColumnSpan(btnSubtitle, 2);
            var resourceMonitor = new ResourceMonitorPanel(UiText("内存", "RAM"), UiText("显存", "VRAM"));
            resourceMonitor.Font = UiFont(8f, FontStyle.Regular);
            t.Controls.Add(resourceMonitor, 0, 12); t.SetColumnSpan(resourceMonitor, 2); t.SetRowSpan(resourceMonitor, 2);

            cmbCodec.SelectedIndexChanged += delegate
            { if(restoringFileEditor)return;
                if (GuardUnavailableCodecSelection()) return;
                if (cmbCodec.SelectedIndex >= 0 && cmbCodec.SelectedIndex <= 3)
                    lastCodecIndex = cmbCodec.SelectedIndex;
                UpdateGrainForCodec();
                if (cmbCodec.SelectedIndex >= 0 && cmbCodec.SelectedIndex <= 3) LoadBitrateStateForCodec();
                UpdateNoReencodeUi();
                UpdateSpeedChoices();
                UpdateSfeUi();
                UpdateMediaDrivenUi(GetSelectedMediaInfo());
                UpdateBitrateDisplays();
                UpdateHardwareDependentControls();LoadBitrateStateForCodec();UpdateBitrateDisplays();
            };
            cmbSpeed.SelectedIndexChanged += delegate { if(restoringFileEditor)return; UpdateSfeUi(); };
            cmbBitrate.TextChanged += delegate
            { if(restoringFileEditor)return;
                if (updatingBitrateUi || cmbCodec.SelectedIndex < 0 || cmbCodec.SelectedIndex > 3) return;
                if(IsCqSelected()) {SaveManualCq();return;}
                modeBitrate[cmbCodec.SelectedIndex] = cmbBitrate.Text.Trim();
                modeBitrateAuto[cmbCodec.SelectedIndex] = false;
                updatingBitrateUi = true;
                try { chkBitrateAuto.Checked = false; }
                finally { updatingBitrateUi = false; }
            };
            cmbBitrate.SelectionChangeCommitted += delegate { SaveManualCq(); };
            cmbBitrate.SelectedIndexChanged += delegate { SaveManualCq(); };
            chkBitrateAuto.CheckedChanged += delegate
            { if(restoringFileEditor)return;
                if (updatingBitrateUi || cmbCodec.SelectedIndex < 0 || cmbCodec.SelectedIndex > 3) return;
                if(IsCqSelected()) {config.Save(new Dictionary<string,string>{{RateControlKey("CQ_AUTO"),chkBitrateAuto.Checked?"true":"false"},{RateControlKey("CQ"),ManualCq()}});LoadBitrateStateForCodec();return;}
                modeBitrateAuto[cmbCodec.SelectedIndex] = chkBitrateAuto.Checked;
                UpdateBitrateDisplays();
            };
            chkHighMotion.CheckedChanged += delegate { if(restoringFileEditor)return; UpdateBitrateDisplays(); };
            cmbFps.SelectedIndexChanged += delegate { if(restoringFileEditor)return; UpdateBitrateDisplays(); };
            cmbInterpolationMode.SelectedIndexChanged += delegate { if(restoringFileEditor)return; UpdateBitrateDisplays(); };
            cmbDeintMethod.SelectedIndexChanged += delegate { if(restoringFileEditor)return; UpdateBitrateDisplays(); };
            
            
            LoadBitrateStateForCodec();
            UpdateBitrateDisplays();
            }
            finally { t.ResumeLayout(true); }
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
            grpGrain = new ThemeGroupBox();
            grpGrain.Text = lang.T("grain.av1_metadata");
            grpGrain.Dock = DockStyle.Fill;
            grpGrain.Margin = new Padding(0, 0, 0, 5);

            TableLayoutPanel shell = new TableLayoutPanel();
            shell.Dock = DockStyle.Fill; shell.Padding = new Padding(5); shell.ColumnCount = 1; shell.RowCount = 3;
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            cmbGrainMode = Combo(new string[] { lang.T("grain.mode.film"), lang.T("grain.mode.iso"), lang.T("grain.mode.table"), lang.T("grain.mode.digital"), lang.T("grain.mode.fgsim") }, 0);
            grainContent = new Panel(); grainContent.Dock = DockStyle.Fill;
            chkGrainEnabled = Check(UiText("启用颗粒", "Enable grain"), ordinaryGrainEnabled);
            chkGrainEnabled.CheckedChanged += delegate
            { if(restoringFileEditor)return;
                if (updatingGrainEnableUi) return;
                ordinaryGrainEnabled = chkGrainEnabled.Checked;
                EnsureCurrentGrainCatalog(false);
                try { config.SaveValue("GRAIN_ENABLED", ordinaryGrainEnabled ? "true" : "false"); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, UiText("保存设置失败", "Could not save settings")); }
                UpdateGrainEnableUi();
                UpdateHdrRouteStatus(GetSelectedMediaInfo());
                UpdateBitrateDisplays();
                UpdateSfeUi();
            };
            var grainHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            grainHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); grainHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grainHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            chkGrainEnabled.Margin = new Padding(4, 0, 3, 0);
            var previewButton = new ThemeButton { Text = UiText("预览(右键切换模式)", "Preview (right-click for modes)"), AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(6, 0, 6, 0) };
            previewButton.Click += delegate { OpenGrainPreview(); };
            var grainMenu = new ContextMenuStrip();
            var fastCopy = fileCopyFastMenu = new ToolStripMenuItem(UiText("不重编码：自动快速路径", "No-reencode: automatic fast path")) { CheckOnClick = true, Checked = config.Get("AV1_COPY_FAST") != "false" };
            var fullVerify = fileCopyVerifyMenu = new ToolStripMenuItem(UiText("不重编码：完整颗粒检查", "No-reencode: full grain verification")) { CheckOnClick = true, Checked = config.GetBool("AV1_COPY_FULL_VERIFY") };
            fastCopy.CheckedChanged += delegate { if(restoringFileEditor)return;try { config.SaveValue("AV1_COPY_FAST", fastCopy.Checked ? "true" : "false"); } catch (Exception ex) { MessageBox.Show(this, ex.Message, UiText("保存设置失败", "Could not save settings")); } };
            fullVerify.CheckedChanged += delegate { if(restoringFileEditor)return;try { config.SaveValue("AV1_COPY_FULL_VERIFY", fullVerify.Checked ? "true" : "false"); } catch (Exception ex) { MessageBox.Show(this, ex.Message, UiText("保存设置失败", "Could not save settings")); } };
            grainMenu.Items.Add(fastCopy); grainMenu.Items.Add(fullVerify); previewButton.ContextMenuStrip = grainMenu;
            grainHeader.Controls.Add(chkGrainEnabled, 0, 0); grainHeader.Controls.Add(previewButton, 1, 0);
            shell.Controls.Add(grainHeader, 0, 0); shell.Controls.Add(cmbGrainMode, 0, 1); shell.Controls.Add(grainContent, 0, 2);
            grpGrain.Controls.Add(shell);
            cmbGrainMode.SelectedIndexChanged += delegate
            { if(restoringFileEditor)return;
                if (updatingGrainUi) return;
                if (cmbCodec != null && (cmbCodec.SelectedIndex == 0 || cmbCodec.SelectedIndex == 4)) av1GrainModeIndex = Math.Max(0, cmbGrainMode.SelectedIndex);
                else pixelGrainModeIndex = Math.Max(0, cmbGrainMode.SelectedIndex);
                EnsureCurrentGrainCatalog(false);
                BuildGrainContent();
                UpdateSfeUi();
                UpdateHdrRouteStatus(GetSelectedMediaInfo());
            };
            UpdateGrainForCodec();
            return grpGrain;
        }

        internal NativeStartupOptions startupInputs;
        private volatile bool startupClosing;
        private bool startupIdleStarted, startupCatalogReady;
        private readonly DemandCatalog<List<string>> tableCatalog = new DemandCatalog<List<string>>();
        private readonly DemandCatalog<List<string>> plateCatalog = new DemandCatalog<List<string>>();
        private readonly DemandCatalog<LutRecordPair> lutCatalog = new DemandCatalog<LutRecordPair>();
        private bool catalogRunning;
        private sealed class LutRecordPair {
            internal List<LutChoice> Recent, Favorite;
        }

        private void OnStartupIdle(object sender, EventArgs e)
        {
            if (startupClosing || startupIdleStarted || !Visible || StartupTiming.PaintPending(this)) return;
            if(composeStartupFrame) {
                composeStartupFrame=false;
                UpdateStyles();
                StartupTiming.Mark("first.frame.composition.end");
            }
            startupIdleStarted = true;
            Application.Idle -= OnStartupIdle;
            StartupTiming.Mark("deferred.begin");
            CheckStartupDependencies();
        }

        private void CheckStartupDependencies()
        {
            if (startupClosing || IsDisposed) return;
            string version = config.Get("SETUP_VERSION"), ffmpeg = config.Get("FFMPEG_DIR"), grav = config.Get("GRAV1SYNTH");
            StartupTiming.Mark("dependencies.begin");
            ThreadPool.QueueUserWorkItem(delegate {
                bool needed = true;
                try {
                    needed = setupTestMode || version != SetupPhase1.Version || !File.Exists(Path.Combine(ffmpeg, "ffmpeg.exe"))
                        || !File.Exists(Path.Combine(ffmpeg, "ffprobe.exe")) || !File.Exists(grav);
                } catch { needed = true; }
                StartupTiming.Mark("dependencies.end", "wizard=" + needed);
                if (startupClosing) return;
                try { BeginInvoke((MethodInvoker)delegate {
                    if (startupClosing || IsDisposed) return;
                    if (version != config.Get("SETUP_VERSION") || ffmpeg != config.Get("FFMPEG_DIR") || grav != config.Get("GRAV1SYNTH")) {
                        CheckStartupDependencies(); return;
                    }
                    foreach (Form open in Application.OpenForms) {
                        if (open != this && open.Visible && open.Modal) {
                            open.FormClosed += delegate { if (!startupClosing) CheckStartupDependencies(); };
                            return;
                        }
                    }
                    // Only the dialog itself is UI work. Its startup file checks ran on the worker.
                    if (needed) SetupPhase1.ShowAfterStartupCheck(this, appRoot, config, setupTestMode);
                    if (startupClosing || IsDisposed) return;
                    if (startupInputs != null) LoadStartupInputs(startupInputs);
                    startupCatalogReady = true;
                    // This flag now means startup dependency checks, not optional catalog readiness.
                    BeginHardwareDetection();
                    EnsureCurrentGrainCatalog(false);
                    UpdateGrainEnableUi();
                    StartupTiming.Mark("startup.background.scheduled");
                }); } catch (InvalidOperationException) { }
            });
        }

        private int RequiredGrainCatalog()
        {
            if (!IsGrainEnabled() || cmbCodec == null || cmbGrainMode == null) return 0;
            bool av1 = cmbCodec.SelectedIndex == 0 || cmbCodec.SelectedIndex == 4;
            return av1 && cmbGrainMode.SelectedIndex == 2 ? 1 : (!av1 && cmbGrainMode.SelectedIndex == 1 ? 2 : 0);
        }

        private bool RequiredGrainCatalogReady()
        {
            int kind = RequiredGrainCatalog();
            return kind == 0 || (kind == 1 ? tableCatalog.Ready : plateCatalog.Ready && string.Equals(plateCatalog.Key, config.Get("GRAIN_ROOT"), StringComparison.OrdinalIgnoreCase));
        }

        private void EnsureCurrentGrainCatalog(bool force)
        {
            if (!startupCatalogReady || startupClosing) return;
            int kind = RequiredGrainCatalog();
            if (kind == 1) tableCatalog.Request(Path.Combine(appRoot, "_AV1_Grain_Tables"), force);
            else if (kind == 2) plateCatalog.Request(config.Get("GRAIN_ROOT"), force);
            ScheduleCatalogWork();
        }

        private void RefreshGrainFileCaches() { EnsureCurrentGrainCatalog(true); }

        private void InvalidateChangedCatalogPaths()
        {
            if (plateCatalog.SetKey(config.Get("GRAIN_ROOT"))) grainPlateFiles.Clear();
            if (lutCatalog.SetKey(LutRoot)) SetLutListPlaceholder(false);
            // Inactive resources stay invalid but are not scanned.
            EnsureCurrentGrainCatalog(false);
        }

        private void EnsureLutRecords(bool force)
        {
            if (startupClosing) return;
            lutCatalog.Request(LutRoot, force);
            if (!lutCatalog.Ready) SetLutListPlaceholder(true);
            ScheduleCatalogWork();
        }

        private void ScheduleCatalogWork()
        {
            if (catalogRunning || startupClosing || !startupCatalogReady || !IsHandleCreated) return;
            string key; int version;
            if (tableCatalog.TryStart(out key, out version)) {
                RunCatalogJob<List<string>>(tableCatalog, key, version, "tables",
                    delegate { StartupTiming.Mark("catalog.tables.scan"); return GrainFileCatalogCore.ScanAv1Tables(key, true); },
                    delegate(List<string> value) {
                        bool changed = !SameCatalog(av1GrainTableFiles, value);
                        av1GrainTableFiles.Clear(); av1GrainTableFiles.AddRange(value);
                        if ((!independentFiles || string.IsNullOrEmpty(selectedAv1GrainTable)) && !value.Exists(delegate(string path) { return string.Equals(path, selectedAv1GrainTable, StringComparison.OrdinalIgnoreCase); }))
                            selectedAv1GrainTable = value.Count > 0 ? value[0] : "";
                        return changed;
                    });
            } else if (plateCatalog.TryStart(out key, out version)) {
                RunCatalogJob<List<string>>(plateCatalog, key, version, "plates",
                    delegate { StartupTiming.Mark("catalog.plates.scan"); return GrainFileCatalogCore.ScanGrainPlates(key, true); },
                    delegate(List<string> value) {
                        bool changed = !SameCatalog(grainPlateFiles, value);
                        grainPlateFiles.Clear(); grainPlateFiles.AddRange(value);
                        if ((!independentFiles || string.IsNullOrEmpty(selectedGrainPlatePath)) && !value.Exists(delegate(string path) { return string.Equals(path, selectedGrainPlatePath, StringComparison.OrdinalIgnoreCase); }))
                            selectedGrainPlatePath = value.Count > 0 ? value[0] : "";
                        return changed;
                    });
            } else if (lutCatalog.TryStart(out key, out version)) {
                RunCatalogJob<LutRecordPair>(lutCatalog, key, version, "lut.records",
                    delegate {
                        StartupTiming.Mark("catalog.lut.record.read", "recent");
                        var recent = LutCatalogCore.ReadLutRecordList(Path.Combine(key, "_LUT_PREVIEWS", "_LUT_GALLERY_RECENT.json"), key);
                        StartupTiming.Mark("catalog.lut.record.read", "favorite");
                        var favorite = LutCatalogCore.ReadLutRecordList(Path.Combine(key, "_LUT_PREVIEWS", "_LUT_GALLERY_FAVORITES.json"), key);
                        return new LutRecordPair { Recent = recent, Favorite = favorite };
                    },
                    delegate(LutRecordPair value) { ApplyLutRecords(value); return true; });
            }
        }

        private void RunCatalogJob<T>(DemandCatalog<T> cache, string key, int version, string kind,
            Func<T> load, Func<T, bool> apply)
        {
            catalogRunning = true;
            StartupTiming.Mark("catalog.scan.begin", "kind=" + kind + " run=" + cache.Runs);
            ThreadPool.QueueUserWorkItem(delegate {
                T value = default(T); Exception error = null;
                try { value = load(); } catch (Exception ex) { error = ex; }
                StartupTiming.Mark("catalog.scan.end", "kind=" + kind + " success=" + (error == null));
                if (startupClosing) return;
                try { BeginInvoke((MethodInvoker)delegate {
                    if (startupClosing || IsDisposed || Disposing) return;
                    catalogRunning = false;
                    // Configuration may have changed while a settings dialog was open.
                    if (kind == "plates") cache.SetKey(config.Get("GRAIN_ROOT"));
                    else if (kind == "lut.records") cache.SetKey(LutRoot);
                    bool wasReady = cache.Ready;
                    bool accepted = cache.Complete(key, version, value, error);
                    if (accepted) {
                        bool changed = error == null && apply(value);
                        if (error != null && log != null) AppendLog("[Catalog " + kind + "] " + error.Message + Environment.NewLine);
                        int required = RequiredGrainCatalog();
                        if ((!wasReady || changed || error != null) && ((kind == "tables" && required == 1) || (kind == "plates" && required == 2))) {
                            grainContent.SuspendLayout();
                            try { BuildGrainContent(); }
                            finally { grainContent.ResumeLayout(true); }
                        }
                        if (kind == "lut.records" && error != null) SetLutListPlaceholder(false);
                        UpdateGrainEnableUi();
                        StartupTiming.Mark("catalog.applied", "kind=" + kind + " success=" + (error == null));
                    } else StartupTiming.Mark("catalog.stale.skipped", "kind=" + kind);
                    ScheduleCatalogWork();
                }); } catch (InvalidOperationException) { }
            });
        }

        private static bool SameCatalog(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; ++i)
                if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return false;
            return true;
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
            if ((cmbCodec.SelectedIndex == 0 || cmbCodec.SelectedIndex == 4) && cmbGrainMode.SelectedIndex == 2) BuildGrainContent();
        }

        private void BuildGrainContent()
        {
            if (grainContent == null || cmbGrainMode == null || cmbCodec == null) return;
            grainContent.Controls.Clear();
            cmbGrainFormat = null; cmbGrainStock = null; numGrainIso = null; chkGrainChroma = null; cmbGrainTable = null; chkShowAllAv1Tables = null; cmbGrainPlate = null;
            av1StrengthControl=null;
            trackFilmGrainStrength = null; lblFilmGrainValue = null; btnFgsimTextureUpdate = null; lblFgsimTextureStatus = null;

            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill; t.ColumnCount = 2; t.RowCount = 4;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) t.RowStyles.Add(new RowStyle(SizeType.Percent, 25));

            bool av1 = cmbCodec.SelectedIndex == 0 || cmbCodec.SelectedIndex == 4;
            int mode = cmbGrainMode.SelectedIndex;
            if (mode < 0) mode = 0;

            if (av1 && mode == 0)
            {
                cmbGrainFormat = Combo(new string[] { "Classic35 · Super 35", "Modern35 · Full-frame", "16mm · Coarser", "Super8 · Heavy", "MaxMid · Synthetic" }, Math.Max(0, Math.Min(4, av1FormatIndex)));
                cmbGrainStock = Combo(new string[] { "Fujifilm Eterna 250D", "Fujifilm Eterna 500T", "Kodak Vision3 250D", "Kodak Vision3 200T" }, Math.Max(0, Math.Min(3, av1StockIndex)));
                cmbGrainStock.Enabled = cmbGrainFormat.SelectedIndex < 3;
                cmbGrainFormat.SelectedIndexChanged += delegate { if(restoringFileEditor)return; av1FormatIndex = cmbGrainFormat.SelectedIndex; cmbGrainStock.Enabled = av1FormatIndex < 3; };
                cmbGrainStock.SelectedIndexChanged += delegate { if(restoringFileEditor)return; av1StockIndex = cmbGrainStock.SelectedIndex; };
                AddLabeledRow(t, 0, lang.T("grain.format"), cmbGrainFormat);
                AddLabeledRow(t, 1, lang.T("grain.stock"), cmbGrainStock);
            }
            else if (av1 && mode == 1)
            {
                numGrainIso = new ThemeNumericUpDown(); numGrainIso.Minimum = 1; numGrainIso.Maximum = 1000000; numGrainIso.Value = Math.Max(1, Math.Min(1000000, av1IsoValue)); numGrainIso.Increment = 100; numGrainIso.ThousandsSeparator = true; numGrainIso.Dock = DockStyle.Fill; numGrainIso.Margin = new Padding(4, 5, 6, 5);
                chkGrainChroma = Check(lang.T("grain.chroma"), av1ChromaEnabled);
                numGrainIso.ValueChanged += delegate { if(restoringFileEditor)return; av1IsoValue = (int)numGrainIso.Value; };
                chkGrainChroma.CheckedChanged += delegate { if(restoringFileEditor)return; av1ChromaEnabled = chkGrainChroma.Checked; };
                AddLabeledRow(t, 0, lang.T("grain.iso"), numGrainIso);
                t.Controls.Add(chkGrainChroma, 1, 1);
            }
            else if (av1 && mode == 2)
            {
                MediaProbeInfo source = GetAv1GrainSourceContext();
                string preferredTier = Av1GrainTableSelectionCore.GetAv1GrainTierFromDimensions(source != null ? source.Width : 0, source != null ? source.Height : 0);
                displayedAv1GrainTableFiles.Clear();
                if (tableCatalog.Ready) displayedAv1GrainTableFiles.AddRange(GetFilteredAv1GrainTables(source));

                TableLayoutPanel tablePanel = new TableLayoutPanel();
                tablePanel.Dock = DockStyle.Fill; tablePanel.Margin = Padding.Empty; tablePanel.ColumnCount = 3; tablePanel.RowCount = 1;
                tablePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                tablePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
                tablePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));

                cmbGrainTable = new ThemeComboBox(); cmbGrainTable.DropDownStyle = ComboBoxStyle.DropDownList; cmbGrainTable.Dock = DockStyle.Fill; cmbGrainTable.Margin = new Padding(4, 5, 3, 5); cmbGrainTable.DropDownWidth = 560;
                cmbGrainTable.DropDown += delegate { if(restoringFileEditor)return; EnsureCurrentGrainCatalog(false); };
                int selectedIndex = -1;
                for (int i = 0; i < displayedAv1GrainTableFiles.Count; i++)
                {
                    string path = displayedAv1GrainTableFiles[i];
                    cmbGrainTable.Items.Add(GrainFileCatalogCore.RelativeDisplayPath(Path.Combine(appRoot, "_AV1_Grain_Tables"), path));
                    if (string.Equals(path, selectedAv1GrainTable, StringComparison.OrdinalIgnoreCase)) selectedIndex = i;
                }
                if(independentFiles && selectedIndex<0 && !string.IsNullOrEmpty(selectedAv1GrainTable)) {selectedIndex=cmbGrainTable.Items.Count;cmbGrainTable.Items.Add(UiText("已保存：","Saved: ")+selectedAv1GrainTable);}
                if (cmbGrainTable.Items.Count == 0)
                {
                    string emptyText;
                    if (!showAllAv1GrainTables && string.IsNullOrEmpty(preferredTier))
                        emptyText = UiText("等待视频分辨率以筛选颗粒表", "Waiting for video resolution to filter grain tables");
                    else if (!showAllAv1GrainTables)
                        emptyText = UiText(preferredTier + " 未发现可用颗粒表", "No usable " + preferredTier + " grain tables found");
                    else
                        emptyText = UiText("未发现 .tbl/.txt 颗粒表", "No .tbl/.txt grain tables found");
                    cmbGrainTable.Items.Add(!tableCatalog.Ready ? CatalogPendingText(tableCatalog.Error, tableCatalog.Running || tableCatalog.Requested) : emptyText);
                    cmbGrainTable.SelectedIndex = 0; cmbGrainTable.Enabled = !tableCatalog.Ready;
                    if (tableCatalog.Ready && !independentFiles) selectedAv1GrainTable = "";
                }
                else
                {
                    cmbGrainTable.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
                    if(cmbGrainTable.SelectedIndex<displayedAv1GrainTableFiles.Count)selectedAv1GrainTable = displayedAv1GrainTableFiles[cmbGrainTable.SelectedIndex];
                    cmbGrainTable.SelectedIndexChanged += delegate
                    { if(restoringFileEditor)return;
                        if (cmbGrainTable.SelectedIndex >= 0 && cmbGrainTable.SelectedIndex < displayedAv1GrainTableFiles.Count)
                            selectedAv1GrainTable = displayedAv1GrainTableFiles[cmbGrainTable.SelectedIndex];
                    };
                }

                Button refreshTable = new ThemeButton(); refreshTable.Text = "↻"; refreshTable.Dock = DockStyle.Fill; refreshTable.Margin = new Padding(0, 4, 0, 4);
                refreshTable.Click += delegate { bool ready = tableCatalog.Ready; RefreshGrainFileCaches(); if (!ready) BuildGrainContent(); };
                chkShowAllAv1Tables = new ThemeCheckBox(); chkShowAllAv1Tables.Text = ""; chkShowAllAv1Tables.Checked = showAllAv1GrainTables; chkShowAllAv1Tables.Dock = DockStyle.Fill; chkShowAllAv1Tables.Margin = new Padding(6, 4, 0, 4);
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
                cmbGrainPlate = new ThemeComboBox(); cmbGrainPlate.DropDownStyle = ComboBoxStyle.DropDownList; cmbGrainPlate.Dock = DockStyle.Fill; cmbGrainPlate.Margin = new Padding(4, 5, 6, 5); cmbGrainPlate.DropDownWidth = 520;
                cmbGrainPlate.DropDown += delegate { if(restoringFileEditor)return; EnsureCurrentGrainCatalog(false); };
                string grainRoot = config.Get("GRAIN_ROOT");
                int selectedIndex = -1;
                for (int i = 0; plateCatalog.Ready && i < grainPlateFiles.Count; i++)
                {
                    string path = grainPlateFiles[i];
                    cmbGrainPlate.Items.Add(GrainFileCatalogCore.RelativeDisplayPath(grainRoot, path));
                    if (string.Equals(path, selectedGrainPlatePath, StringComparison.OrdinalIgnoreCase)) selectedIndex = i;
                }
                if(independentFiles && selectedIndex<0 && !string.IsNullOrEmpty(selectedGrainPlatePath)) {selectedIndex=cmbGrainPlate.Items.Count;cmbGrainPlate.Items.Add(UiText("已保存：","Saved: ")+selectedGrainPlatePath);}
                if (cmbGrainPlate.Items.Count == 0)
                {
                    cmbGrainPlate.Items.Add(!plateCatalog.Ready ? CatalogPendingText(plateCatalog.Error, plateCatalog.Running || plateCatalog.Requested) : UiText("颗粒目录中未发现 .mov", "No .mov grain plates found"));
                    cmbGrainPlate.SelectedIndex = 0; cmbGrainPlate.Enabled = !plateCatalog.Ready;
                }
                else
                {
                    cmbGrainPlate.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
                    if(cmbGrainPlate.SelectedIndex<grainPlateFiles.Count)selectedGrainPlatePath = grainPlateFiles[cmbGrainPlate.SelectedIndex];
                    cmbGrainPlate.SelectedIndexChanged += delegate
                    { if(restoringFileEditor)return;
                        if (cmbGrainPlate.SelectedIndex >= 0 && cmbGrainPlate.SelectedIndex < grainPlateFiles.Count)
                            selectedGrainPlatePath = grainPlateFiles[cmbGrainPlate.SelectedIndex];
                    };
                }
                var platePanel = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 1 };
                platePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                platePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
                var refreshPlate = new ThemeButton { Text = "↻", Dock = DockStyle.Fill, Margin = new Padding(0, 4, 3, 4) };
                refreshPlate.Click += delegate { bool ready = plateCatalog.Ready; EnsureCurrentGrainCatalog(true); if (!ready) BuildGrainContent(); };
                platePanel.Controls.Add(cmbGrainPlate, 0, 0); platePanel.Controls.Add(refreshPlate, 1, 0);
                AddLabeledRow(t, 0, UiText("颗粒文件", "Grain Plate"), platePanel);
                BuildUnifiedGrainSlider(t, 1, "PLATE");
            }
            else if ((av1 && mode == 3) || (!av1 && mode == 0))
            {
                BuildUnifiedGrainSlider(t, 0, "DIGITAL");
                Label note = MutedLabel(lang.T("digital_grain.tip"));
                t.Controls.Add(note, 0, 1); t.SetColumnSpan(note, 2); t.SetRowSpan(note, 3);
            }
            else
            {
                BuildUnifiedGrainSlider(t, 0, "FGSIM");
                btnFgsimTextureUpdate = new ThemeButton();
                btnFgsimTextureUpdate.Text = UiText("在线更新纹理", "Update texture online");
                btnFgsimTextureUpdate.Dock = DockStyle.Fill;
                btnFgsimTextureUpdate.Margin = new Padding(4, 2, 6, 2);
                btnFgsimTextureUpdate.Click += delegate { StartFgsimTextureUpdate(); };
                t.Controls.Add(btnFgsimTextureUpdate, 0, 2); t.SetColumnSpan(btnFgsimTextureUpdate, 2);
                lblFgsimTextureStatus = MutedLabel(UiText("默认使用本地纹理", "Using local texture by default"));
                lblFgsimTextureStatus.AutoEllipsis = true;
                t.Controls.Add(lblFgsimTextureStatus, 0, 3); t.SetColumnSpan(lblFgsimTextureStatus, 2);
            }
            if (av1 && mode <= 2)
            {
                av1StrengthControl=new ThemeTrackBar {Minimum=1,Maximum=10,Value=Av1GrainStrengthPercent/10,TickFrequency=1,SmallChange=1,LargeChange=1,AutoSize=false,Height=30,Dock=DockStyle.Fill,Margin=new Padding(0,2,6,2)};
                var strengthRow=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
                strengthRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));strengthRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,62));
                var strengthValue=new ThemeLabel {Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft};
                strengthRow.Controls.Add(av1StrengthControl,0,0);strengthRow.Controls.Add(strengthValue,1,0);
                AddLabeledRow(t,2,UiText("颗粒强度 %","Grain strength %"),strengthRow);
                var performanceNote=MutedLabel("");performanceNote.AutoEllipsis=true;
                t.Controls.Add(performanceNote,0,3);t.SetColumnSpan(performanceNote,2);
                Action updateStrength=delegate {
                    int percent=av1StrengthControl.Value*10;strengthValue.Text=percent+"%";
                    performanceNote.Text=percent==100?"":UiText("选择非100%强度可能会导致性能损失。","Selecting a strength below 100% may reduce performance.");
                };
                av1StrengthControl.ValueChanged+=delegate { if(restoringFileEditor)return;updateStrength();try{config.SaveValue("AV1_GRAIN_STRENGTH",(av1StrengthControl.Value*10).ToString(CultureInfo.InvariantCulture));}catch(Exception ex){MessageBox.Show(this,ex.Message);}};
                updateStrength();
                lutToolTip.SetToolTip(av1StrengthControl,UiText("相对当前预设/ISO/颗粒表的强度；100%保持原效果。","Relative to the selected preset/ISO/table; 100% preserves the original."));
            }
            grainContent.Controls.Add(t);
            UpdateGrainEnableUi();
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
            trackFilmGrainStrength = new ThemeTrackBar(); trackFilmGrainStrength.AutoSize = false; trackFilmGrainStrength.Height = 30; trackFilmGrainStrength.Dock = DockStyle.Fill; trackFilmGrainStrength.Margin = new Padding(0, 2, 6, 2); trackFilmGrainStrength.TickStyle = TickStyle.BottomRight;
            if (kind == "DIGITAL") { trackFilmGrainStrength.Minimum = 10; trackFilmGrainStrength.Maximum = 100; trackFilmGrainStrength.Value = Math.Max(10, Math.Min(100, proceduralStrength)); trackFilmGrainStrength.TickFrequency = 10; }
            else if (kind == "FGSIM") { trackFilmGrainStrength.Minimum = 0; trackFilmGrainStrength.Maximum = 3; trackFilmGrainStrength.Value = Math.Max(0, Math.Min(3, fgsimPresetIndex)); trackFilmGrainStrength.TickFrequency = 1; }
            else { trackFilmGrainStrength.Minimum = 1; trackFilmGrainStrength.Maximum = 10; trackFilmGrainStrength.Value = GrainControlCore.SnapTen(scannedGrainStrengthPercent)/10; trackFilmGrainStrength.TickFrequency = 1; }
            trackFilmGrainStrength.SmallChange=1;trackFilmGrainStrength.LargeChange=1;
            lblFilmGrainValue = new ThemeLabel(); lblFilmGrainValue.AutoSize = false; lblFilmGrainValue.Dock = DockStyle.Fill; lblFilmGrainValue.Margin = new Padding(4, 0, 0, 2); lblFilmGrainValue.TextAlign = ContentAlignment.MiddleLeft;
            TableLayoutPanel slider = new TableLayoutPanel(); slider.Dock = DockStyle.Fill; slider.Margin = Padding.Empty; slider.ColumnCount = 2; slider.RowCount = 1; slider.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); slider.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112)); slider.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            slider.Controls.Add(trackFilmGrainStrength, 0, 0); slider.Controls.Add(lblFilmGrainValue, 1, 0);
            AddLabeledRow(t, row, lang.T("grain.strength"), slider);
            trackFilmGrainStrength.ValueChanged += delegate
            { if(restoringFileEditor)return;
                if (kind == "DIGITAL") proceduralStrength = trackFilmGrainStrength.Value;
                else if (kind == "FGSIM") fgsimPresetIndex = trackFilmGrainStrength.Value;
                else scannedGrainStrengthPercent = trackFilmGrainStrength.Value*10;
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
                string[] names = new string[] { "Light / 0.10", "Normal / 0.15", "Medium / 0.20", "Heavy / 0.30" };
                lblFilmGrainValue.Text = names[Math.Max(0, Math.Min(3, trackFilmGrainStrength.Value))];
            }
            else
            {
                lblFilmGrainValue.Text=(trackFilmGrainStrength.Value*10).ToString(CultureInfo.InvariantCulture)+"%";
            }
        }

        private void UpdateGrainForCodec()
        {
            if (grpGrain == null || cmbCodec == null || cmbGrainMode == null) return;
            bool av1 = cmbCodec.SelectedIndex == 0 || cmbCodec.SelectedIndex == 4;
            updatingGrainUi = true;
            try
            {
                cmbGrainMode.Items.Clear();
                if (av1)
                {
                    cmbGrainMode.Items.AddRange(new object[] { lang.T("grain.mode.film"), lang.T("grain.mode.iso"), lang.T("grain.mode.table"), lang.T("grain.mode.digital"), lang.T("grain.mode.fgsim") });
                    cmbGrainMode.SelectedIndex = Math.Max(0, Math.Min(4, av1GrainModeIndex));
                    grpGrain.Text = cmbCodec.SelectedIndex == 4 ? lang.T("grain.av1_copy") : lang.T("grain.av1_metadata");
                }
                else
                {
                    cmbGrainMode.Items.AddRange(new object[] { lang.T("grain.mode.digital"), UiText("扫描颗粒文件", "Scanned Grain Plate"), lang.T("grain.mode.fgsim") });
                    cmbGrainMode.SelectedIndex = Math.Max(0, Math.Min(2, pixelGrainModeIndex));
                    grpGrain.Text = NewCpuSelected()?CpuCodec()+" 像素颗粒":cmbCodec.SelectedIndex == 1 ? lang.T("grain.hevc") : cmbCodec.SelectedIndex == 3 ? UiText("H.264 像素颗粒", "H.264 pixel grain") : lang.T("grain.x264");
                }
            }
            finally { updatingGrainUi = false; }
            if (!changingCodecForHardware) EnsureCurrentGrainCatalog(false);
            BuildGrainContent();
            UpdateGrainEnableUi();
        }

        private Control BuildLutGroup()
        {
            GroupBox box = new ThemeGroupBox();
            grpLut = box;
            box.Text = lang.T("lut.group");
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(0, 5, 0, 0);

            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill; t.Padding = new Padding(6, 5, 6, 5); t.ColumnCount = 4; t.RowCount = 6;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            lutCompoundHintRow=new RowStyle(SizeType.Absolute,0);t.RowStyles.Add(lutCompoundHintRow);
            box.Controls.Add(t);

            chkLut = Check(lang.T("lut.enable"), false);
            btnLutGallery = new ThemeButton(); btnLutGallery.Text = lang.T("button.open_lut_gallery"); btnLutGallery.Dock = DockStyle.Fill; btnLutGallery.Margin = new Padding(3);
            btnColorCorrection = new ThemeButton(); btnColorCorrection.Text = lang.T("button.color_correction"); btnColorCorrection.Dock = DockStyle.Fill; btnColorCorrection.Margin = new Padding(3);
            Button clear = new ThemeButton(); clear.Text = lang.T("button.clear_lut"); clear.Dock = DockStyle.Fill; clear.Margin = new Padding(3);
            btnLutGallery.Click += delegate { OpenLutGallery(); };
            btnColorCorrection.Click += delegate { OpenColorCorrection(); };
            ContextMenuStrip colorSizeMenu = new ContextMenuStrip();
            ToolStripMenuItem largeColorItem = new ToolStripMenuItem(UiText("大界面", "Large interface"));
            colorSizeMenu.Items.Add(largeColorItem);
            colorSizeMenu.Opening += delegate { largeColorItem.Checked = config.GetBool("COLOR_PREVIEW_LARGE_UI"); };
            largeColorItem.Click += delegate {
                try { config.Save(new Dictionary<string, string> { { "COLOR_PREVIEW_LARGE_UI", config.GetBool("COLOR_PREVIEW_LARGE_UI") ? "false" : "true" } }); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            btnColorCorrection.ContextMenuStrip = colorSizeMenu;
            clear.Click += delegate { ClearSelectedLut(); };
            t.Controls.Add(chkLut, 0, 0); t.Controls.Add(btnLutGallery, 1, 0); t.Controls.Add(btnColorCorrection, 2, 0); t.Controls.Add(clear, 3, 0);

            Label recentLabel = MidLabel(lang.T("lut.recent"));
            cmbRecentLut = new ThemeComboBox(); cmbRecentLut.DropDownStyle = ComboBoxStyle.DropDownList; cmbRecentLut.Dock = DockStyle.Fill; cmbRecentLut.Margin = new Padding(4, 5, 6, 5); cmbRecentLut.DropDownWidth = 440; cmbRecentLut.MaxDropDownItems = 25;
            cmbRecentLut.DropDown += delegate { EnsureLutRecords(false); };
            cmbRecentLut.SelectedIndexChanged += delegate { SelectLutFromCombo(cmbRecentLut, "Recent"); };
            t.Controls.Add(recentLabel, 0, 1); t.Controls.Add(cmbRecentLut, 1, 1); t.SetColumnSpan(cmbRecentLut, 3);

            Label favoriteLabel = MidLabel(lang.T("lut.favorite"));
            cmbFavoriteLut = new ThemeComboBox(); cmbFavoriteLut.DropDownStyle = ComboBoxStyle.DropDownList; cmbFavoriteLut.Dock = DockStyle.Fill; cmbFavoriteLut.Margin = new Padding(4, 5, 6, 5); cmbFavoriteLut.DropDownWidth = 440; cmbFavoriteLut.MaxDropDownItems = 25;
            cmbFavoriteLut.DropDown += delegate { EnsureLutRecords(false); };
            cmbFavoriteLut.SelectedIndexChanged += delegate { SelectLutFromCombo(cmbFavoriteLut, "Favorite"); };
            t.Controls.Add(favoriteLabel, 0, 2); t.Controls.Add(cmbFavoriteLut, 1, 2); t.SetColumnSpan(cmbFavoriteLut, 3);
            var recordsMenu = new ContextMenuStrip();
            var refreshRecords = new ToolStripMenuItem(UiText("刷新最近／收藏列表", "Refresh recent/favorite lists"));
            refreshRecords.Click += delegate { EnsureLutRecords(true); };
            recordsMenu.Items.Add(refreshRecords);
            cmbRecentLut.ContextMenuStrip = recordsMenu; cmbFavoriteLut.ContextMenuStrip = recordsMenu;

            TableLayoutPanel preview = new TableLayoutPanel(); preview.Dock = DockStyle.Fill; preview.Margin = new Padding(4, 3, 4, 2); preview.ColumnCount = 1; preview.RowCount = 2;
            preview.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); preview.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            picLutPreview = new PictureBox(); picLutPreview.Size = new Size(240, 135); picLutPreview.Anchor = AnchorStyles.None; picLutPreview.Margin = Padding.Empty; picLutPreview.BackColor = Color.Black; picLutPreview.BorderStyle = BorderStyle.None; picLutPreview.SizeMode = PictureBoxSizeMode.Zoom;
            picLutPreview.Paint+=delegate(object sender,PaintEventArgs e){using(var border=new Pen(Color.FromArgb(125,125,125),1))e.Graphics.DrawRectangle(border,0,0,picLutPreview.Width-1,picLutPreview.Height-1);};
            lutThumbnail=new LutThumbnailRenderer(picLutPreview,AppendLog);
            FormClosed+=delegate {lutThumbnail.Dispose();lutSessionReference.Dispose();};
            picLutPreview.Cursor=Cursors.Hand;picLutPreview.Click+=delegate {OpenLutStrengthPreview();};
            lblSelectedLut = new ThemeLabel(); lblSelectedLut.Text = lang.T("lut.none"); lblSelectedLut.Dock = DockStyle.Fill; lblSelectedLut.AutoEllipsis = true; lblSelectedLut.TextAlign = ContentAlignment.MiddleCenter; lblSelectedLut.ForeColor = ColorMuted; lblSelectedLut.Padding = new Padding(4, 0, 4, 0);
            preview.Controls.Add(picLutPreview, 0, 0); preview.Controls.Add(lblSelectedLut, 0, 1);
            t.Controls.Add(preview, 0, 3); t.SetColumnSpan(preview, 4);

            trackLutStrength = new ThemeTrackBar(); trackLutStrength.Minimum = 0; trackLutStrength.Maximum = 4; trackLutStrength.Value = 3; trackLutStrength.TickStyle = TickStyle.BottomRight; trackLutStrength.Dock = DockStyle.Fill; trackLutStrength.Margin = Padding.Empty; trackLutStrength.Enabled = false;
            lblLutStrength = new ThemeLabel(); lblLutStrength.Text = "75%"; lblLutStrength.Dock = DockStyle.Fill; lblLutStrength.TextAlign = ContentAlignment.MiddleLeft; lblLutStrength.Enabled = false;
            chkLut.CheckedChanged += delegate { if(restoringFileEditor)return; UpdateLutUi(); UpdateHdrRouteStatus(GetSelectedMediaInfo()); };
            trackLutStrength.ValueChanged += delegate
            { if(restoringFileEditor)return;
                lblLutStrength.Text = GetLutStrength().ToString(CultureInfo.InvariantCulture) + "%";
                SetLutPreview(selectedLutPath);
                UpdateHdrRouteStatus(GetSelectedMediaInfo());
            };
            var strengthGroup=new TableLayoutPanel {Size=new Size(290,40),Anchor=AnchorStyles.None,Margin=Padding.Empty,ColumnCount=2,RowCount=1};
            strengthGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));strengthGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,50));
            strengthGroup.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            strengthGroup.Controls.Add(trackLutStrength,0,0);strengthGroup.Controls.Add(lblLutStrength,1,0);
            t.Controls.Add(strengthGroup,0,4);t.SetColumnSpan(strengthGroup,4);

            lblLutCompoundHint=new ThemeLabel {Dock=DockStyle.Fill,AutoEllipsis=true,ForeColor=ColorMuted,Padding=new Padding(4,0,4,0),Visible=false};
            lblLutCompoundHint.Text=UiText("1D＋3D 组合 LUT\r\n非100%强度仍采用原混合流程，可能影响性能。","Combined 1D + 3D LUT\r\nBelow 100%, the original blend remains and may affect performance.");
            lutToolTip.SetToolTip(lblLutCompoundHint,lblLutCompoundHint.Text);
            t.Controls.Add(lblLutCompoundHint,0,5);t.SetColumnSpan(lblLutCompoundHint,4);
            return box;
        }

        private Control BuildLogArea()
        {
            GroupBox box = new ThemeGroupBox();
            box.Text = lang.T("log.group");
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(10, 2, 10, 4);

            TableLayoutPanel layout = new TableLayoutPanel(); layout.Dock = DockStyle.Fill; layout.RowCount = 2; layout.ColumnCount = 1;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            box.Controls.Add(layout);

            TableLayoutPanel toolbar = new TableLayoutPanel(); toolbar.Dock = DockStyle.Fill; toolbar.ColumnCount = 6; toolbar.RowCount = 1; toolbar.Margin = Padding.Empty; toolbar.Padding = new Padding(3, 0, 0, 0);
            toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
            lblRunStage = new ThemeLabel(); lblRunStage.Text = lang.T("log.waiting"); lblRunStage.Dock = DockStyle.Fill; lblRunStage.TextAlign = ContentAlignment.MiddleLeft; lblRunStage.AutoEllipsis = true;
            lblRunMetric = new ThemeLabel(); lblRunMetric.Text = "fps: —   speed: —"; lblRunMetric.AutoSize = true; lblRunMetric.MinimumSize = new Size(360, 0); lblRunMetric.Anchor = AnchorStyles.Left; lblRunMetric.TextAlign = ContentAlignment.MiddleLeft; lblRunMetric.ForeColor = ColorMuted; lblRunMetric.AutoEllipsis = false;
            Button copy = new ThemeButton(); copy.Text = lang.T("button.copy_log"); copy.Size = new Size(78, 24); copy.Anchor = AnchorStyles.Right;
            Button clear = new ThemeButton(); clear.Text = lang.T("button.clear_log"); clear.Size = new Size(78, 24); clear.Anchor = AnchorStyles.Right;
            copy.Click += delegate { if (log.TextLength > 0) Clipboard.SetText(log.Text); };
            clear.Click += delegate { log.Clear(); };
            toolbar.Controls.Add(lblRunStage, 0, 0); toolbar.Controls.Add(lblRunMetric, 1, 0); toolbar.Controls.Add(copy, 2, 0); toolbar.Controls.Add(clear, 3, 0);
            Button openLog = new ThemeButton(); openLog.Text = UiText("完整日志", "Full log"); openLog.Size = new Size(88, 24);
            Button openFolder = new ThemeButton(); openFolder.Text = UiText("日志目录", "Log folder"); openFolder.Size = new Size(88, 24);
            foreach (Button button in new Button[] { copy, clear, openLog, openFolder })
            {
                button.MinimumSize = new Size(button.Width, 24);
                button.Margin = new Padding(3);
                button.Anchor = AnchorStyles.Right;
            }
            openLog.Click += delegate { OpenFullLog(false); };
            openFolder.Click += delegate { OpenFullLog(true); };
            ContextMenuStrip logFolderMenu = new ContextMenuStrip();
            ToolStripMenuItem clearLogFolder = new ToolStripMenuItem(UiText("清空日志目录", "Clear log folder"));
            clearLogFolder.Click += delegate { ClearLogFolder(); };
            logFolderMenu.Items.Add(clearLogFolder);
            openFolder.ContextMenuStrip = logFolderMenu;
            toolbar.Controls.Add(openLog, 4, 0); toolbar.Controls.Add(openFolder, 5, 0);
            layout.Controls.Add(toolbar, 0, 0);

            log = new TextBox(); log.Multiline = true; log.ScrollBars = ScrollBars.Vertical; log.MaxLength = 0; log.Dock = DockStyle.Fill; log.ReadOnly = true; log.WordWrap = true; log.BackColor = Color.FromArgb(28, 30, 34); log.ForeColor = Color.Gainsboro; log.Font = new Font(LanguagePack.LogFontName(lang.Code), 9f); log.BorderStyle = BorderStyle.FixedSingle;
            fullLog = new FullLogFileCore(appRoot, "Session", GetLogEntryLimit());
            AppendLog("Film Grain Studio v" + Program.ReleaseVersion + " · .NET · " + TestBuild + Environment.NewLine);
            AppendDiagnosticLog("Validated migration baseline: Q9AOL2 / release " + TestBuild + Environment.NewLine);
            AppendDiagnosticLog("App root: " + appRoot + Environment.NewLine);
            AppendDiagnosticLog(".NET execution modules: Native AV1 grain inspection and Native-only codecs; preparation failures stop the task." + Environment.NewLine);
            AppendLog("[Full log] " + fullLog.FilePath + Environment.NewLine);
            if (!string.IsNullOrEmpty(fullLog.Error)) AppendVisibleLog("[Full log ERROR] " + fullLog.Error + Environment.NewLine);
            layout.Controls.Add(log, 0, 1);
            return box;
        }

        private Control BuildFooter()
        {
            Panel footer = new Panel(); footer.Dock = DockStyle.Fill; footer.BackColor = ColorSubtle;
            lblStatus = new ThemeLabel(); lblStatus.AutoSize = false; lblStatus.Size = new Size(560, 30); lblStatus.Location = new Point(14, 14); lblStatus.TextAlign = ContentAlignment.MiddleLeft; footer.Controls.Add(lblStatus);
            progressRun = new ThemeProgressBar(); progressRun.Style = ProgressBarStyle.Blocks; progressRun.Minimum = 0; progressRun.Maximum = 1000; progressRun.Value = 0; progressRun.Size = new Size(220, 20); progressRun.Anchor = AnchorStyles.Top | AnchorStyles.Right; footer.Controls.Add(progressRun);
            btnCancelTask = new ThemeButton(); btnCancelTask.Text = lang.T("button.cancel_task"); btnCancelTask.Enabled = false; btnCancelTask.Size = new Size(94, 34); btnCancelTask.Anchor = AnchorStyles.Top | AnchorStyles.Right; footer.Controls.Add(btnCancelTask);
            btnStart = new ThemeButton(); btnStart.Text = lang.T("button.start_encode"); btnStart.ForeColor = Color.White; btnStart.BackColor = ColorAccent; btnStart.FlatStyle = FlatStyle.Flat; btnStart.FlatAppearance.BorderSize = 0; btnStart.Size = new Size(126, 36); btnStart.Anchor = AnchorStyles.Top | AnchorStyles.Right; footer.Controls.Add(btnStart);
            encodePreviewPanel = new Panel(); encodePreviewPanel.Size = new Size(392, 30); footer.Controls.Add(encodePreviewPanel);
            chkEncodePreview = new ThemeCheckBox { Text = UiText("预览编码", "Preview"), AutoSize = true, Location = new Point(0, 6) };
            txtPreviewStart = new MaskedTextBox("00:00:00") { Text = "000000", Size = new Size(76, 24), Location = new Point(94, 3), Enabled = false, TextMaskFormat = MaskFormat.IncludeLiterals };
            Label previewStartLabel = new ThemeLabel { Text = UiText("起始", "Start"), AutoSize = true, Location = new Point(58, 7) };
            // Keep labels and editors aligned without covering the status area.
            chkEncodePreview.AutoSize = false; chkEncodePreview.Size = new Size(86, 24); chkEncodePreview.Location = new Point(0, 2);
            previewStartLabel.Location = new Point(88, 7); txtPreviewStart.Left = 122;
            Label durationLabel = new ThemeLabel { Text = UiText("持续", "Length"), AutoSize = true, Location = new Point(206, 7) };
            numPreviewDuration = new ThemeIntegerCombo(1,86400,30,new int[]{30,60,120},false) { Size = new Size(66,24), Location = new Point(248,3), Enabled = false };
            Label secondsLabel = new ThemeLabel { Text = UiText("秒", "s"), AutoSize = true, Location = new Point(320, 7) };
            encodePreviewPanel.Width = 350;
            encodePreviewPanel.Controls.AddRange(new Control[] { chkEncodePreview, previewStartLabel, txtPreviewStart, durationLabel, numPreviewDuration, secondsLabel });
            chkEncodePreview.CheckedChanged += delegate { txtPreviewStart.Enabled = numPreviewDuration.Enabled = chkEncodePreview.Checked; };
            lutToolTip.SetToolTip(chkEncodePreview, UiText("先按关键帧无损裁剪，再用当前设置编码预览。实际起点和时长允许偏差。", "Stream-copy a keyframe clip, then encode with current settings. Start and duration may differ."));
            btnStart.Click += delegate { StartBridgeExecution(); };
            btnCancelTask.Click += delegate { CancelBridgeExecution(); };
            footer.Resize += delegate
            {
                btnStart.Left = footer.ClientSize.Width - btnStart.Width - 14; btnStart.Top = 10;
                btnCancelTask.Left = btnStart.Left - btnCancelTask.Width - 9; btnCancelTask.Top = 11;
                progressRun.Left = btnCancelTask.Left - progressRun.Width - 12; progressRun.Top = 19;
                encodePreviewPanel.Left = progressRun.Left - encodePreviewPanel.Width - 10; encodePreviewPanel.Top = 14;
                lblStatus.Width = Math.Max(0, encodePreviewPanel.Left - lblStatus.Left - 8);
            };
            footer.PerformLayout();
            return footer;
        }

        private Control BuildStatusStrip()
        {
            StatusStrip strip = new StatusStrip(); strip.Dock = DockStyle.Fill; strip.SizingGrip = false; strip.BackColor = ColorSubtle; strip.Padding = new Padding(8, 1, 8, 1);
            statusHardware = new ToolStripStatusLabel(); statusHardware.AutoSize = false; statusHardware.Width = 1; statusHardware.Spring = true; statusHardware.TextAlign = ContentAlignment.MiddleLeft; statusHardware.ForeColor = ColorMuted; statusHardware.Text = lang.T("hardware.detecting");
            ToolStripStatusLabel version = CreateProjectVersionLink(this);
            strip.ShowItemToolTips = true;
            strip.Items.Add(statusHardware);
            statusFfmpeg = new ToolStripStatusLabel("FFmpeg ?"); statusFfmpeg.ForeColor = ColorMuted;
            strip.Items.Add(statusFfmpeg);
            foreach (string name in new string[]{"AV1", "UHQ", "HEVC", "H.264", "x264", "High10", "SVT-HDR", "SVP", "FRUC"})
            {
                ToolStripStatusLabel item = new ToolStripStatusLabel(name + " ?");
                item.ForeColor = ColorMuted; item.Margin = new Padding(3, 0, 3, 0);
                statusCapabilities[name] = item; strip.Items.Add(item);
            }
            statusCapabilities["FRUC"].Click += delegate { BeginHardwareDetection(true); };
            statusCapabilities["SVT-HDR"].Click += delegate { BeginHardwareDetection(true); };
            BuildThemeMenu(strip);
            strip.Items.Add(version); return strip;
        }

        private bool fittingFileColumns;
        private void FitFileColumns()
        {
            if(fittingFileColumns || listFiles==null || listFiles.Columns.Count!=3 || listFiles.ClientSize.Width<20)return;
            fittingFileColumns=true;listFiles.BeginUpdate();
            try {
                int width=Math.Max(3,listFiles.ClientSize.Width-4);
                int name=Math.Max(1,(int)(width*0.62));
                int size=Math.Max(1,(int)(width*0.19));
                int[] targets=new int[]{name,size,Math.Max(1,width-name-size)};
                // Shrink before growing; keep the sum within the available client width.
                for(int i=0;i<3;i++)if(listFiles.Columns[i].Width>targets[i])listFiles.Columns[i].Width=targets[i];
                for(int i=0;i<3;i++)if(listFiles.Columns[i].Width!=targets[i])listFiles.Columns[i].Width=targets[i];
            } finally {listFiles.EndUpdate();fittingFileColumns=false;}
        }

        private ComboBox Combo(string[] items, int index)
        {
            ComboBox c = new ThemeComboBox(); c.DropDownStyle = ComboBoxStyle.DropDownList; c.Dock = DockStyle.Fill; c.Margin = new Padding(4, 5, 6, 5);
            foreach (string item in items) c.Items.Add(item);
            if (c.Items.Count > 0) c.SelectedIndex = Math.Max(0, Math.Min(index, c.Items.Count - 1));
            return c;
        }

        private ComboBox ComboEditable(string[] items, string text)
        {
            ComboBox c = new ThemeComboBox(); c.DropDownStyle = ComboBoxStyle.DropDown; c.Dock = DockStyle.Fill; c.Margin = new Padding(4, 5, 3, 5);
            foreach (string item in items) c.Items.Add(item); c.Text = text; return c;
        }

        private CheckBox Check(string text, bool value)
        {
            CheckBox c = new ThemeCheckBox(); c.Text = text; c.Checked = value; c.AutoSize = true; c.Dock = DockStyle.Fill; c.Margin = new Padding(4, 7, 3, 3); return c;
        }

        private Label MidLabel(string text)
        {
            Label l = new ThemeLabel(); l.Text = text; l.Dock = DockStyle.Fill; l.TextAlign = ContentAlignment.MiddleLeft; l.Padding = new Padding(4, 0, 0, 0); return l;
        }

        private Label MutedLabel(string text)
        {
            Label l = new ThemeLabel(); l.Text = text; l.Dock = DockStyle.Fill; l.TextAlign = ContentAlignment.TopLeft; l.ForeColor = ColorMuted; l.Padding = new Padding(4, 6, 4, 0); return l;
        }

        private void AddLabeledRow(TableLayoutPanel table, int row, string text, Control control)
        {
            Label l = MidLabel(text); table.Controls.Add(l, 0, row); table.Controls.Add(control, 1, row);
        }

        private void TryLoadIcon()
        {
            UiWindowIcon.Apply(this, appRoot);
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

        internal void LoadStartupInputs(NativeStartupOptions startup)
        {
            StartupTiming.Mark("startup.inputs", "count=" + startup.Inputs.Count);
            foreach (string notice in startup.Notices) AppendLog("[Startup] " + notice + Environment.NewLine);
            if (startup.Inputs.Count > 0)
            {
                AddFiles(startup.Inputs);
                AppendLog("[Startup] 已添加输入文件 / Added input files: " + startup.Inputs.Count.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);
            }
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
                item.SubItems.Add(UiText("等待","Waiting"));
                item.Tag = fi.FullName;
                item.ToolTipText = fi.FullName;
                if(independentFiles&&sharedEditor!=null)fileSettings[fi.FullName]=sharedEditor.Copy();
                listFiles.Items.Add(item);
                existing.Add(path);
            }
            QueueMissingMediaProbes(true);
            UpdateStatusCount();
            if (listFiles.Items.Count > 0 && listFiles.SelectedItems.Count == 0) listFiles.Items[0].Selected = true;
            UpdateNoReencodeAvailability();
            UpdateFileSettingsLabels();
        }

        private void ClearInputFiles()
        {
            editingFile=null;fileSettings.Clear();initializedFileSettings.Clear();completedQueueFiles.Clear();submittedQueueFiles.Clear();
            listFiles.Items.Clear();UpdateSelectedInfo();UpdateStatusCount();
        }

        private void RemoveSelected()
        {
            changingFileSelection=true;
            try {while(listFiles.SelectedItems.Count>0) {var item=listFiles.SelectedItems[0];string path=item.Tag as string;if(path!=null){fileSettings.Remove(path);initializedFileSettings.Remove(path);completedQueueFiles.Remove(path);submittedQueueFiles.Remove(path);mediaProbeFailures.Remove(path);}if(path==editingFile)editingFile=null;listFiles.Items.Remove(item);}}
            finally {changingFileSelection=false;}
            UpdateSelectedInfo();
            UpdateStatusCount();
        }

        private void UpdateSelectedInfo()
        {
            QueueMissingMediaProbes();
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
            string failure;
            if(TryGetMediaProbeFailure(path,out failure))SetMediaInfoText(LF("media.read_failed",failure),true);
        }

        private readonly MediaProbeQueuePlan mediaProbeQueue=new MediaProbeQueuePlan();
        private bool waitingForQueueMedia;
        private readonly Dictionary<string,MediaProbeResult> mediaProbeFailures=new Dictionary<string,MediaProbeResult>(StringComparer.OrdinalIgnoreCase);
        private bool IsInputListed(string path)
        {
            foreach(ListViewItem item in listFiles.Items)if(string.Equals(item.Tag as string,path,StringComparison.OrdinalIgnoreCase))return true;
            return false;
        }
        private bool TryGetMediaProbeFailure(string path,out string detail)
        {
            detail="";MediaProbeResult result;
            if(!mediaProbeFailures.TryGetValue(path,out result))return false;
            string probe=Path.Combine(config.Get("FFMPEG_DIR"),"ffprobe.exe");
            if(result.Identity!=null && !result.Identity.IsCurrent(path,probe)){mediaProbeFailures.Remove(path);return false;}
            detail=result.FailureMessage;
            if(string.IsNullOrEmpty(detail))detail=FirstLine(result.StandardError);
            if(string.IsNullOrEmpty(detail))detail=LF("media.ffprobe_code",result.ExitCode);
            return true;
        }
        private void QueueMissingMediaProbes(bool allInputs = false)
        {
            if(mediaProbeCore==null || IsDisposed || Disposing)return;
            if(listFiles.Items.Count==0) {
                mediaProbeCore.Cancel();mediaProbeQueue.Reset();mediaProbeFailures.Clear();
                if(waitingForQueueMedia && lblRunStage!=null)lblRunStage.Text=UiText("队列已清空","Queue cleared");
                waitingForQueueMedia=false;return;
            }
            foreach(ListViewItem item in listFiles.Items) {
                if(!allInputs && !item.Selected)continue;
                string path=item.Tag as string;MediaProbeInfo info;string failure;
                if(!string.IsNullOrEmpty(path) && !mediaProbeCache.TryGetValue(path,out info) && !TryGetMediaProbeFailure(path,out failure))mediaProbeQueue.Enqueue(path);
            }
            PumpMediaProbeQueue();
        }
        private void PumpMediaProbeQueue()
        {
            if(mediaProbeQueue.Active!=null)return;
            string path;
            while((path=mediaProbeQueue.TakeNext())!=null) {
                MediaProbeInfo cached;string failure;
                if(!IsInputListed(path) || mediaProbeCache.TryGetValue(path,out cached) || TryGetMediaProbeFailure(path,out failure)) {mediaProbeQueue.Complete(path);continue;}
                string probe=Path.Combine(config.Get("FFMPEG_DIR"),"ffprobe.exe");
                if(!File.Exists(probe)) {mediaProbeQueue.Reset();SetMediaInfoText(LF("media.ffprobe_missing",probe),true);return;}
                AppendLog("[Media probe] start remaining="+mediaProbeQueue.Count+" input="+path+Environment.NewLine);
                mediaProbeCore.Start(path,probe);return;
            }
        }
        private bool QueueMediaReadyForStart()
        {
            QueueMissingMediaProbes(true);
            int missing=0;var failures=new List<string>();
            foreach(ListViewItem item in listFiles.Items) {
                string path=item.Tag as string;MediaProbeInfo info;string detail;
                if(!IsPendingQueueFile(path) || mediaProbeCache.TryGetValue(path,out info))continue;
                missing++;
                if(TryGetMediaProbeFailure(path,out detail))failures.Add(Path.GetFileName(path)+": "+detail);
            }
            if(missing==0){waitingForQueueMedia=false;return true;}
            if(failures.Count>0)MessageBox.Show(this,UiText("以下文件的媒体信息读取失败，队列尚未启动：","Media probing failed; queue has not started:")+Environment.NewLine+string.Join(Environment.NewLine,failures.ToArray()),Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);
            else {
                waitingForQueueMedia=true;
                string message=UiText("正在读取队列媒体信息，剩余 ","Reading queue metadata; remaining ")+missing+UiText(" 个。完成后请再次点击开始。",". Click Start again when ready.");
                if(lblRunStage!=null)lblRunStage.Text=message;
                SetMediaInfoText(message,true);
            }
            return false;
        }

        private void OnMediaProbeCompleted(MediaProbeResult result)
        {
            if (result == null || IsDisposed || Disposing || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (result == null || IsDisposed || Disposing || !mediaProbeCore.IsCurrentResult(result)) return;
                    if(!mediaProbeQueue.Complete(result.PathValue))return;
                    bool listed=IsInputListed(result.PathValue);
                    bool current=result.Identity!=null && result.Identity.IsCurrent(result.PathValue,mediaProbeCache.ProbePath());
                    if(listed && (current || result.Identity==null)) {
                        if(result.Info!=null && result.Info.Identity!=null) {mediaProbeCache[result.PathValue]=result.Info;mediaProbeFailures.Remove(result.PathValue);}
                        else mediaProbeFailures[result.PathValue]=result;
                    }
                    AppendLog("[Media probe] complete valid="+current+" success="+(result.Info!=null)+" input="+result.PathValue+Environment.NewLine);
                    // Cache every completed input, even after selection changes or multi-selection.
                    QueueMissingMediaProbes();
                    if(waitingForQueueMedia && lblRunStage!=null) {
                        if(mediaProbeQueue.Count>0)lblRunStage.Text=UiText("正在读取队列媒体信息，剩余 ","Reading queue metadata; remaining ")+mediaProbeQueue.Count;
                        else {waitingForQueueMedia=false;lblRunStage.Text=mediaProbeFailures.Count==0?UiText("媒体信息已就绪，请点击开始。","Queue metadata ready; click Start."):UiText("部分文件读取失败，请检查文件后再开始。","Some inputs failed probing; check them before starting.");}
                    }
                    if(listFiles.SelectedItems.Count!=1)return;
                    string currentPath=listFiles.SelectedItems[0].Tag as string;
                    if(!string.Equals(currentPath,result.PathValue,StringComparison.OrdinalIgnoreCase))return;
                    if(!current && result.Identity!=null){SetMediaInfoText("Input or FFprobe changed; probing current input again.",true);return;}

                    if (result.Info != null)
                    {
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
            bool eligible = independentFiles;
            if (!independentFiles && listFiles.Items.Count == 1)
            {
                string path = listFiles.Items[0].Tag as string;
                MediaProbeInfo info;
                if (!string.IsNullOrEmpty(path) && mediaProbeCache.TryGetValue(path, out info))
                    eligible = IsAv1Media(info);
            }

            string itemText = lang.T("codec.no_reencode");
            bool hasItem = cmbCodec.Items.Count >= 5 && string.Equals(Convert.ToString(cmbCodec.Items[4], CultureInfo.InvariantCulture), itemText, StringComparison.Ordinal);
            if (eligible && !hasItem)
            {
                cmbCodec.Items.Add(itemText);
            }
            else if (!eligible && hasItem)
            {
                if (cmbCodec.SelectedIndex == 4) cmbCodec.SelectedIndex = 0;
                cmbCodec.Items.RemoveAt(4);
            }
        }

        private bool GrainTuneSelected()
        {
            string key=CpuCodec()=="SVT"?"CPU_SVT_GRAIN_TUNE":CpuCodec()=="X265"?"CPU_X265_GRAIN_TUNE":"X264_GRAIN_TUNE";
            string value=config.Get(key);return value=="ON"||(value=="AUTO"&&IsGrainEnabled());
        }

        private string GetX264SettingsLabel()
        {
            if(NewCpuSelected())return CpuCodec()+" · preset "+config.Get(CpuCodec()=="SVT"?"CPU_SVT_PRESET":"CPU_X265_PRESET")+(GrainTuneSelected()?(CpuCodec()=="SVT"?" / tune 6":" / tune grain"):"");
            string presetValue = config.Get("X264_PRESET");
            string presetLabel = string.Equals(presetValue, "medium", StringComparison.OrdinalIgnoreCase) ? "Medium" :
                (string.Equals(presetValue, "slow", StringComparison.OrdinalIgnoreCase) ? "Slow" : "Faster");
            string rateValue = config.Get("X264_RATE_MODE");
            string passLabel = string.Equals(rateValue, "3PASS", StringComparison.OrdinalIgnoreCase) ? lang.T("advanced.vbr3") :
                (string.Equals(rateValue, "2PASS", StringComparison.OrdinalIgnoreCase) ? lang.T("advanced.vbr2") : lang.T("speed.vbr1"));

            return "x264 · " + presetLabel + (GrainTuneSelected() ? " / tune grain / " : " / tune：关闭 / ") + passLabel;
        }

        private void UpdateSpeedChoices()
        {
            if (cmbCodec == null || cmbSpeed == null) return;
            

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
            int selectedIndex = independentFiles ? Math.Max(0,cmbSpeed.SelectedIndex) : currentText.StartsWith("Standard", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            bool allowUhq = cmbCodec.SelectedIndex == 0 && (independentFiles || hardwareCapsReady && hardwareCaps != null && hardwareCaps.Av1UhqAvailable);
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
            cmbSpeed.Enabled = cmbCodec.SelectedIndex != 4;
        }

        private void UpdateSfeUi()
        {
            if(chkSfe==null)return;
            bool ready=hardwareCapsReady&&hardwareCaps!=null;
            bool hevc=cmbCodec!=null&&cmbCodec.SelectedIndex==1,av1=cmbCodec!=null&&cmbCodec.SelectedIndex==0;
            int max=ready?(hevc?hardwareCaps.HevcSplitEncodeMaxEngines:av1?hardwareCaps.Av1SplitEncodeMaxEngines:1):1;
            bool grainCompatible=ready&&(!av1||!IsGrainEnabled()||hardwareCaps.Grav1synthSfeCompatible);
            bool speedCompatible=hevc||av1&&cmbSpeed!=null&&cmbSpeed.SelectedIndex>=1;
            chkSfe.Text=LF("sfe.engines",Math.Max(1,max));
            chkSfe.Enabled=ready&&(av1||hevc)&&max>=2&&grainCompatible&&speedCompatible;
            if(!chkSfe.Enabled&&!independentFiles)chkSfe.Checked=false;
            string gravVersion = ready ? (hardwareCaps.Grav1synthVersion ?? "") : lang.T("hardware.not_detected");
            sfeToolTip.SetToolTip(chkSfe,hevc?UiText("HEVC 分帧并行：需要多个 NVENC 引擎及实测支持。提高单任务速度，但可能降低压缩效率。关闭时禁用隐式 SFE。","HEVC split-frame encoding requires multiple NVENC engines and a successful capability probe. Faster single-task encoding may reduce compression efficiency. Off explicitly disables implicit SFE."):!av1?UiText("H.264 / x264 不支持 NVENC SFE。","H.264 / x264 do not support NVENC SFE."):!ready?lang.T("tooltip.sfe_unavailable"):!grainCompatible?LF("tooltip.sfe_version",gravVersion):!chkSfe.Enabled?lang.T("tooltip.sfe_unavailable"):LF("tooltip.sfe_ready",max,gravVersion));
        }

        private void UpdateNoReencodeUi()
        {
            if (cmbCodec == null) return;
            
            bool active = cmbCodec.SelectedIndex == 4;
            UpdateGrainEnableUi();
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
                                AppendDiagnosticLog(result.StandardError.TrimEnd() + Environment.NewLine);
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

        private bool AllInputsBypassOpenSvp()
        {
            if (listFiles == null || listFiles.Items.Count == 0) return false;
            foreach (ListViewItem item in listFiles.Items)
            {
                MediaProbeInfo info; string path = item.Tag as string;
                if (path == null || !mediaProbeCache.TryGetValue(path,out info) || !HasNativeInterpolationBypass(info)) return false;
            }
            return true;
        }

        private bool HasNativeInterpolationBypass(MediaProbeInfo info)
        {
            if (chkInterpolation == null || !chkInterpolation.Checked || info == null) return false;
            var current = new Dictionary<string,string> {
                {"FG_SVP_INTERPOLATE","1"}, {"FG_INTERPOLATION_ENGINE",cmbInterpolationMode != null && cmbInterpolationMode.SelectedIndex == 2 ? "FRUC" : "SVP"},
                {"FG_HDR_POLICY",config.Get("HDR_POLICY")}, {"FG_DEINTERLACE",cmbDeint == null || cmbDeint.SelectedIndex == 0 ? "AUTO" : "OFF"}
            };
            IDictionary<string,string> normalized; string notice,reason;
            return SharedInterpolationBypassCore.TryResolve(info,current,out normalized,out notice,out reason) && normalized != null;
        }

        private double GetRecommendedOutputFps(MediaProbeInfo info)
        {
            bool interpolate = chkInterpolation != null && chkInterpolation.Checked;
            if (interpolate)
            {
                if (HasNativeInterpolationBypass(info)) return BitrateRecommendationCore.ParseMediaFps(info) * (info.IsInterlaced ? 2.0 : 1.0);
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
            if(captureInputPath!=null) {MediaProbeInfo capture;return mediaProbeCache.TryGetValue(captureInputPath,out capture)?capture:null;}
            MediaProbeInfo selected = GetSelectedMediaInfo();
            if (selected != null) return selected;
            if (listFiles == null || listFiles.Items.Count == 0) return null;
            string path = listFiles.Items[0].Tag as string;
            if (string.IsNullOrEmpty(path)) return null;
            MediaProbeInfo info;
            return mediaProbeCache.TryGetValue(path, out info) ? info : null;
        }

        private string RateControlKey(string option)
        { return NewCpuSelected()?"CPU_"+CpuCodec()+"_"+option:"NVENC_"+(cmbCodec.SelectedIndex==0?"AV1":cmbCodec.SelectedIndex==3?"H264":"HEVC")+"_"+option; }
        private bool IsCqSelected()
        { return cmbCodec!=null&&(cmbCodec.SelectedIndex==0||cmbCodec.SelectedIndex==1||cmbCodec.SelectedIndex==3||NewCpuSelected())&&config.Get(RateControlKey("RATE_CONTROL"))=="CQ"; }

        private bool IsCqAutomatic()
        {
            string saved=config.Get(RateControlKey("CQ_AUTO"));
            return saved.Length==0?config.Get(RateControlKey("CQ"))=="AUTO":saved.Equals("true",StringComparison.OrdinalIgnoreCase);
        }
        private string ManualCq()
        {double value;string text=config.Get(RateControlKey("CQ"));return NvencRateControlCore.TryQuality((cmbCodec.SelectedIndex==0||NewCpuSelected()&&CpuCodec()=="SVT")?"AV1":"HEVC",text,out value)?text:"23";}
        private string RecommendedCq()
        {if(NewCpuSelected())return CpuCodec()=="SVT"?"30":"22";return NvencRateControlCore.Recommend(cmbCodec.SelectedIndex==0?"AV1":cmbCodec.SelectedIndex==3?"H264":"HEVC",chkHighMotion.Checked,IsGrainEnabled());}
        private void SaveManualCq()
        {
            if(restoringFileEditor||updatingBitrateUi||!IsCqSelected())return;
            config.Save(new Dictionary<string,string>{{RateControlKey("CQ"),cmbBitrate.Text.Trim()},{RateControlKey("CQ_AUTO"),"false"}});
            updatingBitrateUi=true;try {chkBitrateAuto.Checked=false;}finally {updatingBitrateUi=false;}
        }
        private void LoadBitrateStateForCodec()
        {
            if (cmbCodec == null || cmbBitrate == null || chkBitrateAuto == null) return;
            int index = Math.Max(0, Math.Min(3, cmbCodec.SelectedIndex));
            updatingBitrateUi = true;
            try
            {
                bool cq=IsCqSelected();
                chkCq.Text=NewCpuSelected()?"CRF 模式":UiText("CQ 模式","CQ mode");
                chkCq.Enabled=cmbCodec.SelectedIndex==0||cmbCodec.SelectedIndex==1||cmbCodec.SelectedIndex==3||NewCpuSelected();chkCq.Checked=cq;
                cmbBitrate.Items.Clear();
                if(cq)cmbBitrate.Items.AddRange(NewCpuSelected()?(CpuCodec()=="SVT"?new object[]{"20","25","30","35","40"}:new object[]{"18","20","22","24","26"}):index==0?new object[]{"20","22","24","25","26","28","30"}:index==1?new object[]{"18","20","21","22","23","24","26"}:new object[]{"16","18","19","20","21","22","24"});
                else cmbBitrate.Items.AddRange(bitrateChoices);
                chkBitrateAuto.Enabled=cmbCodec.SelectedIndex!=4;
                chkBitrateAuto.Checked=cq?IsCqAutomatic():modeBitrateAuto[index];
                cmbBitrate.Text=cq?(IsCqAutomatic()?RecommendedCq():ManualCq()):modeBitrate[index];
                if(rateControlLabel!=null)rateControlLabel.Text=cq?(NewCpuSelected()?"目标质量 CRF":UiText("目标质量 CQ", "Target quality CQ")):lang.T("encode.bitrate");
                string help=UiText("CQ 数值越小质量越高，输出码率和体积不固定。勾选自动时使用 FGS 推荐：AV1 25 / HEVC 22 / H.264 20；高动态、启用颗粒各降低 1。手动数值不受影响。预置值由高画质到小体积，仅供起点参考，不保证视觉无损。0 是驱动自动，不能等同 FGS 自动推荐。", "Lower CQ targets higher quality; bitrate and size vary. Automatic: FGS starting targets AV1 25 / HEVC 22 / H.264 20; subtract 1 each for high motion and enabled grain. Manual values stay unchanged. Presets span quality to size, without a visual-lossless guarantee. Zero means driver automatic, not FGS AUTO.");
                if(NewCpuSelected())help="CPU CRF：数值越小质量越高，体积不固定。SVT 默认 30、x265 默认 22；数值不能与 NVENC CQ 横向比较。Grain 调优可在高级设置独立选择。";
                lutToolTip.SetToolTip(chkCq,help);lutToolTip.SetToolTip(cmbBitrate,cq?help:UiText("视频码率，单位 kbps。", "Video bitrate in kbps."));
            }
            finally { updatingBitrateUi = false; }
        }

        private void UpdateBitrateDisplays()
        {
            if (cmbCodec == null || cmbBitrate == null || chkBitrateAuto == null) return;
            if(chkCq!=null)chkCq.Text=NewCpuSelected()?"CRF 模式":UiText("CQ 模式","CQ mode");
                chkCq.Enabled=cmbCodec.SelectedIndex==0||cmbCodec.SelectedIndex==1||cmbCodec.SelectedIndex==3||NewCpuSelected();
            if (cmbCodec.SelectedIndex == 4) return;
            if(IsCqSelected()) {
                if(IsCqAutomatic()) {updatingBitrateUi=true;try {cmbBitrate.Text=RecommendedCq();chkBitrateAuto.Checked=true;}finally {updatingBitrateUi=false;}}
                return;
            }
            int codecIndex = Math.Max(0, Math.Min(3, cmbCodec.SelectedIndex));
            MediaProbeInfo info = GetBitrateMediaContext();
            int width = info != null ? info.Width : 1920;
            int height = info != null ? info.Height : 1080;
            double fps = GetRecommendedOutputFps(info);
            bool highMotion = chkHighMotion != null && chkHighMotion.Checked;

            if (fps > 0.0 && modeBitrateAuto[codecIndex])
            {
                int recommended = BitrateRecommendationCore.GetRecommendedBitrate(NewCpuSelected()?(CpuCodec()=="SVT"?0:1):codecIndex, width, height, fps, highMotion);
                modeBitrate[codecIndex] = recommended.ToString(CultureInfo.InvariantCulture);
                updatingBitrateUi = true;
                try
                {
                    cmbBitrate.Text = modeBitrate[codecIndex];
                    chkBitrateAuto.Checked = true;
                }
                finally { updatingBitrateUi = false; }
            }

            
        }

        private bool IsGrainEnabled()
        { return (cmbCodec != null && cmbCodec.SelectedIndex == 4) || ordinaryGrainEnabled; }

        private void UpdateGrainEnableUi()
        {
            if(chkGrainEnabled==null)return;
            bool dedicated=cmbCodec!=null && cmbCodec.SelectedIndex==4;
            updatingGrainEnableUi=true;
            try { chkGrainEnabled.Checked=IsGrainEnabled(); chkGrainEnabled.Enabled=!dedicated; }
            finally { updatingGrainEnableUi=false; }
            if(cmbGrainMode!=null)cmbGrainMode.Enabled=IsGrainEnabled();
            if(grainContent!=null)grainContent.Enabled=IsGrainEnabled();
            lutToolTip.SetToolTip(chkGrainEnabled,dedicated ? UiText("此模式用于添加/更换 AV1 颗粒元数据，颗粒必须开启。", "This mode changes AV1 grain metadata; grain must stay enabled.") : UiText("关闭后不添加 FGS 颗粒；不会消除输入已有颗粒。", "Disabling skips FGS grain addition; existing input grain is not removed."));
        }

        private bool IsNvencFgsim()
        {
            return IsGrainEnabled() && cmbCodec != null && (cmbCodec.SelectedIndex == 1 || cmbCodec.SelectedIndex == 3) && cmbGrainMode != null && cmbGrainMode.SelectedIndex == 2;
        }

        private string UiText(string zh, string en)
        {
            return string.Equals(lang.Code, "zh-CN", StringComparison.OrdinalIgnoreCase) ? zh : en;
        }

        private HardwareCapabilityRequest CreateHardwareRequest()
        {
            string ffmpeg = Path.Combine(config.Get("FFMPEG_DIR"), "ffmpeg.exe");
            HardwareCapabilityRequest request = new HardwareCapabilityRequest();
            request.AppRoot = appRoot;
            request.FfmpegPath = ffmpeg;
            request.Grav1synthPath = config.Get("GRAV1SYNTH");
            request.NvencQueryPath = Application.ExecutablePath;
            request.ForceDetection = false;
            request.FrucFfmpegPath = config.FrucUsesMainFfmpeg() ? ffmpeg : config.Get("FRUC_FFMPEG_PATH");
            request.FrucPerf = config.Get("FRUC_PERF").ToLowerInvariant();
            request.FrucGrid = config.Get("FRUC_GRID").ToLowerInvariant();

            return request;
        }

        private void RefreshChangedHardware(string before)
        {
            if (before != HardwareCapabilityCore.RequestKey(CreateHardwareRequest())) BeginHardwareDetection();
        }

        private void BeginHardwareDetection(bool force = false)
        {
            if (hardwareCapabilityCore == null) return;
            if (!startupCatalogReady) { hardwareForceQueued |= force; return; }
            StartupTiming.Mark("hardware.request.begin");
            HardwareCapabilityRequest request = CreateHardwareRequest();
            request.ForceDetection = force || hardwareForceQueued;
            string requestedNativeKey = HardwareCapabilityCore.NativeKey(request);
            string requestedFrucKey = HardwareCapabilityCore.FrucKey(request);
            string key = requestedNativeKey + "|FRUC|" + requestedFrucKey;
            if (hardwareCapabilityCore.IsRunning) {
                // Identical requests join the active pass, including repeated refresh clicks.
                hardwareRefreshQueued = key != hardwareActiveKey;
                return;
            }
            if (bridgeTaskCoordinator != null && bridgeTaskCoordinator.IsActive) {
                hardwareRefreshQueued = true; hardwareForceQueued |= force; return;
            }
            hardwareRefreshQueued = false; hardwareForceQueued = false;
            if (!request.ForceDetection && key == hardwareAppliedKey && hardwareCaps != null && hardwareCaps.Ready && HardwareCapabilityCore.CanCacheFruc(hardwareCaps)) return;
            hardwareActiveKey = key;
            hardwareActiveNativeKey = requestedNativeKey;
            hardwareActiveFrucKey = requestedFrucKey;
            hardwareDetectionInProgress = true;
            // Keep previous codec choices while refreshing; task start remains blocked until detection finishes.
            hardwareCapsReady = hardwareCaps != null && hardwareCaps.Ready && hardwareNativeAppliedKey == hardwareActiveNativeKey;
            if(hardwareCaps!=null && (hardwareFrucAppliedKey!=requestedFrucKey || !HardwareCapabilityCore.CanCacheFruc(hardwareCaps))) {
                hardwareCaps=hardwareCaps.Clone();hardwareCaps.FrucReady=false;hardwareCaps.FrucAvailable=false;
                if(statusCapabilities.ContainsKey("FRUC")) {statusCapabilities["FRUC"].Text="FRUC ?";statusCapabilities["FRUC"].ForeColor=ColorMuted;}
                if(statusCapabilities.ContainsKey("SVT-HDR")) {statusCapabilities["SVT-HDR"].Text="SVT-HDR ?";statusCapabilities["SVT-HDR"].ForeColor=ColorMuted;}
            }
            UpdateFrucAvailabilityUi();
            if (!hardwareCapsReady) SetCapabilityStatusPending();
            UpdateSfeUi();
            if (statusHardware != null && !hardwareCapsReady) statusHardware.Text = lang.T("hardware.detecting");
            if (btnStart != null && (bridgeTaskCoordinator == null || !bridgeTaskCoordinator.IsActive)) btnStart.Enabled = hardwareCapsReady;
            try
            {
                StartupTiming.Mark("hardware.request");
                hardwareCapabilityCore.Start(request);
            }
            catch (Exception ex)
            {
                hardwareDetectionInProgress = false;
                if (btnStart != null && (bridgeTaskCoordinator == null || !bridgeTaskCoordinator.IsActive)) btnStart.Enabled = hardwareCapsReady;
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
                    if (e == null || hardwareCapabilityCore == null || e.Generation != hardwareCapabilityCore.Generation) return;
                    hardwareDetectionInProgress = false;
                    if (hardwareRefreshQueued) { BeginHardwareDetection(); return; }
                    HardwareCapabilitySnapshot caps = e != null ? e.Snapshot : null;
                    if (caps != null && caps.Ready) hardwareAppliedKey = hardwareActiveKey;
                    if (caps != null && caps.Ready)
                    {
                        bool nativeChanged = !hardwareCapsReady || !HardwareCapabilityCore.SameNativeCapabilities(hardwareCaps,caps);
                        hardwareCaps = caps;
                        hardwareCapsReady = true;
                        hardwareNativeAppliedKey = hardwareActiveNativeKey;
                        hardwareFrucAppliedKey = caps.FrucReady ? hardwareActiveFrucKey : null;
                        if(nativeChanged) ApplyHardwareCapabilities();
                        if (log != null)
                            AppendDiagnosticLog("[Hardware .NET] " + caps.Diagnostics + Environment.NewLine);
                        if (log != null && nativeChanged)
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
                    UpdateFrucAvailabilityUi();
                    UpdateFrucStatus();
                    if(caps!=null)AppendDiagnosticLog("[Hardware SVT-HDR] available="+caps.SvtHdrAvailable+" path="+caps.FrucFfmpegPath+" reason="+caps.SvtHdrReason+Environment.NewLine);
                    if (caps != null) AppendDiagnosticLog("[Hardware FRUC] available=" + caps.FrucAvailable + " kind=" + caps.FrucFailureKind + " path=" + caps.FrucFfmpegPath + " reason=" + caps.FrucReason + Environment.NewLine);
                    if (btnStart != null && (bridgeTaskCoordinator == null || !bridgeTaskCoordinator.IsActive)) btnStart.Enabled = hardwareCapsReady;
                    StartupTiming.Mark("hardware.applied", "ready=" + hardwareCapsReady + " native=" + hardwareCapabilityCore.NativeRuns + " fruc=" + hardwareCapabilityCore.FrucRuns + " children=" + hardwareCapabilityCore.ChildProcesses);
                    ScrollLogToEnd();
                });
            }
            catch (InvalidOperationException) { }
        }

        private void ApplyHardwareCapabilities()
        {
            if (!hardwareCapsReady || hardwareCaps == null) return;
            string cacheText = hardwareCaps.CacheState;
            if (string.Equals(cacheText, "Detected", StringComparison.OrdinalIgnoreCase)) cacheText = lang.T("hardware.cache_detected");
            else if (string.Equals(cacheText, "Cached", StringComparison.OrdinalIgnoreCase)) cacheText = lang.T("hardware.cache_cached");

            if (statusHardware != null)
            {
                statusHardware.Text = SafeHardwareText(hardwareCaps.GpuName);
                string details = "GPU " + SafeHardwareText(hardwareCaps.GpuName) + " · Driver " + SafeHardwareText(hardwareCaps.DriverVersion) +
                    " · FFmpeg " + SafeHardwareText(hardwareCaps.FfmpegVersion) + " · " + SafeHardwareText(cacheText);
                statusHardware.ToolTipText = details;
                statusFfmpeg.ToolTipText = details;
                statusFfmpeg.Text = "FFmpeg " + SafeHardwareText(hardwareCaps.FfmpegVersion);
            }
            SetCapabilityStatus("AV1", hardwareCaps.Av1Available);
            SetCapabilityStatus("UHQ", hardwareCaps.Av1UhqAvailable);
            SetCapabilityStatus("HEVC", hardwareCaps.HevcAvailable);
            SetCapabilityStatus("H.264",hardwareCaps.H264Available);
            SetCapabilityStatus("x264", hardwareCaps.X264Available);
            SetCapabilityStatus("High10", hardwareCaps.X264Available && hardwareCaps.X264High10Available);
            SetCapabilityStatus("SVT-HDR",hardwareCaps.SvtHdrAvailable);
            if(statusCapabilities.ContainsKey("SVT-HDR"))statusCapabilities["SVT-HDR"].ToolTipText="SVT-AV1-HDR · "+hardwareCaps.FrucFfmpegPath+"\r\n"+hardwareCaps.SvtHdrReason;
            SetCapabilityStatus("SVP", hardwareCaps.OpenSvpGpuAvailable);
            UpdateFrucAvailabilityUi(); UpdateFrucStatus();


            if (cmbCodec != null && cmbCodec.Items.Count >= 4)
            {
                changingCodecForHardware = true;
                try
                {
                    cmbCodec.Items[0] = hardwareCaps.Av1Available ? lang.T("codec.av1_default") : lang.T("codec.av1_unavailable");
                    cmbCodec.Items[1] = hardwareCaps.HevcAvailable ? lang.T("codec.hevc_scan") : lang.T("codec.hevc_unavailable");
                    cmbCodec.Items[2] = hardwareCaps.X264Available ? lang.T("codec.x264_default") : lang.T("codec.x264_unavailable");
                    cmbCodec.Items[3] = hardwareCaps.H264Available ? "H.264 · NVENC" : UiText("H.264 · 不可用", "H.264 · unavailable");
                    if (!independentFiles && cmbCodec.SelectedIndex == 0 && !hardwareCaps.Av1Available)
                        {if(!hardwareCaps.HevcAvailable&&hardwareCaps.X264Available)config.SaveValue("CPU_CODEC","X264");cmbCodec.SelectedIndex = hardwareCaps.HevcAvailable ? 1 : (hardwareCaps.X264Available ? 2 : 1);}
                    else if (!independentFiles && cmbCodec.SelectedIndex == 1 && !hardwareCaps.HevcAvailable)
                    {
                        if (hardwareCaps.Av1Available) cmbCodec.SelectedIndex = 0;
                        else if (hardwareCaps.X264Available) {config.SaveValue("CPU_CODEC","X264");cmbCodec.SelectedIndex = 2;}
                    }
                    else if (!independentFiles && cmbCodec.SelectedIndex == 2 && !NewCpuSelected() && !hardwareCaps.X264Available)
                    {
                        if (hardwareCaps.HevcAvailable) cmbCodec.SelectedIndex = 1;
                        else if (hardwareCaps.Av1Available) cmbCodec.SelectedIndex = 0;
                    }
                }
                finally { changingCodecForHardware = false; }
            }
            SyncCodecPicker();UpdateSpeedChoices();
            UpdateSfeUi();
            UpdateHardwareDependentControls();
        }

        private void SetCapabilityStatusPending()
        {
            foreach (KeyValuePair<string,ToolStripStatusLabel> pair in statusCapabilities)
            { pair.Value.Text = pair.Key + " ?"; pair.Value.ForeColor = ColorMuted; }
        }

        private void SetCapabilityStatus(string name, bool available)
        {
            ToolStripStatusLabel item;
            if (!statusCapabilities.TryGetValue(name, out item)) return;
            item.Text = name + (available ? " ✓" : " ✗");
            item.ForeColor = available ? Color.FromArgb(25, 135, 65) : Color.FromArgb(190, 50, 45);
            item.ToolTipText = name + " · " + lang.T(available ? "hardware.available" : "hardware.unavailable");
        }

        private string FrucUiSummary()
        {
            if ((hardwareDetectionInProgress && !hardwareCapsReady) || hardwareCaps == null || !hardwareCaps.FrucReady)
                return UiText("FRUC Vulkan 正在检测，请稍后重试。", "FRUC Vulkan detection is pending.");
            string kind = hardwareCaps.FrucFailureKind;
            string summary = kind == "FFMPEG" ? UiText("FFmpeg 缺少 FRUC 滤镜或路径不可用", "FFmpeg filter/path unavailable") :
                kind == "DEVICE" ? UiText("Vulkan 设备 0 / 驱动不支持 FRUC", "Vulkan device 0 / driver limitation") :
                kind == "CONFIG" ? UiText("FRUC 参数无效", "Invalid FRUC configuration") :
                kind == "UNSUPPORTED" ? UiText("当前构建或设备不支持此 FRUC 组合", "Unsupported FRUC build/device combination") :
                UiText("FRUC 运行预检失败", "FRUC runtime preflight failed");
            return summary;
        }

        private string FrucUiReason()
        {
            // Normal UI uses the concise reason. Full probe output stays in the diagnostic log.
            return FrucToolTipText();
        }

        private string FrucToolTipText()
        {
            if ((hardwareDetectionInProgress && !hardwareCapsReady) || hardwareCaps == null || !hardwareCaps.FrucReady) return FrucUiSummary();
            if (hardwareCaps.FrucAvailable)
                return UiText("FRUC Vulkan 小样本预检通过。实际输入和组合仍需验证。", "FRUC Vulkan small-sample probe passed. Actual inputs/combinations still require testing.");
            return FrucUiSummary() + Environment.NewLine + UiText("详细原因请查看任务日志。", "See the task log for details.");
        }

        private void UpdateFrucAvailabilityUi()
        {
            if (cmbInterpolationMode == null || cmbInterpolationMode.Items.Count < 3) return;
            string label = "FRUC Vulkan";
            if ((hardwareDetectionInProgress && !hardwareCapsReady) || hardwareCaps == null || !hardwareCaps.FrucReady)
                label += UiText(" · 检测中", " · pending");
            else if (!hardwareCaps.FrucAvailable)
                label += hardwareCaps.FrucFailureKind == "FFMPEG" ? UiText(" · 不可用：FFmpeg", " · unavailable: FFmpeg") :
                    hardwareCaps.FrucFailureKind == "DEVICE" ? UiText(" · 不可用：设备/驱动", " · unavailable: device/driver") :
                    hardwareCaps.FrucFailureKind == "UNSUPPORTED" ? UiText(" · 不可用：组合不支持", " · unavailable: unsupported combination") :
                    hardwareCaps.FrucFailureKind == "CONFIG" ? UiText(" · 不可用：参数无效", " · unavailable: invalid parameters") :
                    UiText(" · 不可用：预检失败", " · unavailable: preflight");
            updatingFrucUi = true;
            try { cmbInterpolationMode.Items[2] = label; }
            finally { updatingFrucUi = false; }
            cmbInterpolationMode.Invalidate();
            if (lutToolTip != null) lutToolTip.SetToolTip(cmbInterpolationMode, FrucToolTipText());
        }

        private void UpdateFrucStatus()
        {
            if (hardwareCaps == null || !hardwareCaps.FrucReady || hardwareDetectionInProgress) return;
            SetCapabilityStatus("FRUC", hardwareCaps.FrucAvailable);
            if (statusCapabilities.ContainsKey("FRUC"))
                statusCapabilities["FRUC"].ToolTipText = FrucToolTipText();
        }

        private void ResetHardwareUiToPending()
        {
            SetCapabilityStatusPending();
            if (statusHardware != null)
            {
                string ffmpegVersion = hardwareCaps != null ? hardwareCaps.FfmpegVersion : "";
                if (string.IsNullOrWhiteSpace(ffmpegVersion)) ffmpegVersion = lang.T("hardware.not_detected");
                statusHardware.Text = "FFmpeg " + ffmpegVersion + " · " + lang.T("hardware.pending");
            }
            if (cmbCodec != null && cmbCodec.Items.Count >= 4)
            {
                cmbCodec.Items[0] = lang.T("codec.av1_default");
                cmbCodec.Items[1] = lang.T("codec.hevc_scan");
                cmbCodec.Items[2] = lang.T("codec.x264_default");
                cmbCodec.Items[3] = "H.264 · NVENC";
            }
            SyncCodecPicker();UpdateSpeedChoices();
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
                    try { if(fallback==2)config.SaveValue("CPU_CODEC","X264");cmbCodec.SelectedIndex = fallback; }
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
                    try { if(fallback==2)config.SaveValue("CPU_CODEC","X264");cmbCodec.SelectedIndex = fallback; }
                    finally { changingCodecForHardware = false; }
                }
                MessageBox.Show(this, lang.T("error.hevc_unavailable"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            if(selected==3&&!hardwareCaps.H264Available) { MessageBox.Show(this,UiText("当前 GPU/FFmpeg 不支持 H.264 NVENC。","H.264 NVENC is unavailable."),Text);changingCodecForHardware=true;try{cmbCodec.SelectedIndex=hardwareCaps.HevcAvailable?1:hardwareCaps.X264Available?2:0;}finally{changingCodecForHardware=false;}return true;}
            if (selected == 2 && !NewCpuSelected() && !hardwareCaps.X264Available)
            {
                int fallback = hardwareCaps.HevcAvailable ? 1 : (hardwareCaps.Av1Available ? 0 : -1);
                if (fallback >= 0)
                {
                    changingCodecForHardware = true;
                    try { if(fallback==2)config.SaveValue("CPU_CODEC","X264");cmbCodec.SelectedIndex = fallback; }
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
            if(cmbCodec.SelectedIndex==3&&!hardwareCaps.H264Available){MessageBox.Show(this,UiText("H.264 NVENC 不可用。","H.264 NVENC is unavailable."),Text);return false;}
            if (cmbCodec.SelectedIndex == 2 && !NewCpuSelected() && !hardwareCaps.X264Available)
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
            if (!startupCatalogReady || (hardwareDetectionInProgress && !hardwareCapsReady))
            {
                MessageBox.Show(this, lang.T("hardware.detecting"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!RequiredGrainCatalogReady()) {
                EnsureCurrentGrainCatalog(false);
                MessageBox.Show(this, UiText("颗粒列表正在加载；完成后请重试。", "Loading the grain catalog; retry when ready."));
                return;
            }
            if(PendingQueueInputs().Count==0){MessageBox.Show(this,UiText("当前列表已全部完成；如需重新处理，请移除后重新添加文件。","All listed files are complete. Remove and re-add a file to process it again."),Text);return;}
            if(!QueueMediaReadyForStart())return;
            if(independentFiles) {StartIndependentQueue();return;}
            if (!ValidateHardwareForSelectedCodec()) return;

            if (chkEncodePreview.Checked && !numPreviewDuration.ValidateInput()) return;
            string error;
            SortedDictionary<string, string> state = BuildBridgeEnvironment(out error);
            if (state == null)
            {
                MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (chkEncodePreview != null && chkEncodePreview.Checked)
            {
                double startSeconds;
                if (!SharedEncodePreviewCore.TryParseStart(txtPreviewStart.Text, out startSeconds))
                { MessageBox.Show(this, UiText("预览开始时间须为 00:00:00，分钟和秒须小于 60。", "Use HH:MM:SS; minutes and seconds must be below 60."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                state["FG_ENCODE_PREVIEW"] = "1";
                state["FG_PREVIEW_START"] = txtPreviewStart.Text;
                state["FG_PREVIEW_SECONDS"] = numPreviewDuration.Value.ToString(CultureInfo.InvariantCulture);
            }

            bool noReencode = cmbCodec != null && cmbCodec.SelectedIndex == 4;
            if (!noReencode && chkInterpolation.Checked && cmbInterpolationMode.SelectedIndex == 2 &&
                (hardwareCaps == null || !hardwareCaps.FrucReady || !hardwareCaps.FrucAvailable))
            { MessageBox.Show(this, FrucUiReason(), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            List<string> inputs = new List<string>();
            foreach (ListViewItem item in listFiles.Items)
            {
                string path = item.Tag as string;
                if (IsPendingQueueFile(path)) inputs.Add(path);
            }

            if(inputs.Count==0){MessageBox.Show(this,lang.T("info.add_video"),Text,MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
            if (taskLog != null) taskLog.Dispose();
            taskLog = new FullLogFileCore(appRoot, "Task", GetLogEntryLimit());
            fullLogErrorReported = false;
            AppendTaskLog("[Task log] " + taskLog.FilePath + Environment.NewLine);
            AppendTaskLog("[Task] started=" + DateTime.Now.ToString("o", CultureInfo.InvariantCulture) + " build=" + TestBuild + Environment.NewLine);
            foreach (string input in inputs) AppendTaskLog("[Task input] " + input + Environment.NewLine);
            foreach (KeyValuePair<string, string> setting in state) AppendTaskDiagnosticLog(setting.Key + "=" + setting.Value + Environment.NewLine);

            if (inputs.Count > 1)
            {
                StartSharedQueue(inputs, state, noReencode);
                return;
            }
            BridgePreparedExecution prepared;
            MediaProbeInfo nativeInfo;mediaProbeCache.TryGetValue(inputs[0],out nativeInfo);
            string nativeReason;
            if(!NativeExecutionPreparationCore.TryPrepare(appRoot,inputs[0],nativeInfo,state,hardwareCaps,noReencode,out prepared,out nativeReason))
            {
                AppendTaskLog("[Native boundary] stopped: "+nativeReason+Environment.NewLine);
                MessageBox.Show(this,nativeReason,Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return;
            }
            if(noReencode)state["FG_MODE"]="AV1";

            foreach (string inputPath in prepared.InputFiles)
            {
                if (log != null) AppendTaskLog("[Runner] input preflight: exists=1 path=\"" + inputPath + "\"" + Environment.NewLine);
            }

            WorkspacePreflightIssue workspaceIssue;
            if (!WorkspacePreflightCore.Check(prepared.InputFiles,
                noReencode ? "AV1" : (cmbCodec.SelectedIndex == 0 ? "AV1" : cmbCodec.SelectedIndex == 1 ? "HEVC" : cmbCodec.SelectedIndex == 3 ? "H264" : "X264"),
                noReencode, config.Get("TEMP_MODE"), config.Get("TEMP_CUSTOM_DIR"),
                config.Get("OUTPUT_MODE"), config.Get("OUTPUT_CUSTOM_DIR"), mediaProbeCache,
                delegate(WorkspaceSpaceWarning warning)
                {
                    string message = LF(warning.IsOutput ? "warning.output_space" : "warning.temp_space",
                        warning.Path, FormatWorkspaceBytes(warning.Available), FormatWorkspaceBytes(warning.Required), FormatWorkspaceBytes(warning.Reserve));
                    return MessageBox.Show(this, message, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                }, !noReencode && state["FG_MODE"] == "AV1" && prepared.ExecutionRequest.Environment["FG_SVP_INTERPOLATE"] == "1", !noReencode ? state["FG_INTERPOLATION_TARGET_FPS"] : "", out workspaceIssue))
            {
                if (workspaceIssue != null)
                    MessageBox.Show(this, LF(workspaceIssue.Key, workspaceIssue.Path, workspaceIssue.Detail), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                BridgeExecutionCore.CleanupTemporaryFiles(prepared.ExecutionRequest);
                return;
            }

            MediaProbeInfo info = GetBitrateMediaContext();
            double outputFps = GetRecommendedOutputFps(info);
            BridgeTaskRequest taskRequest = BridgeRequestPreparationCore.CreateTask(prepared, outputFps);
            taskRequest.NativeBackend = true;
            activeRunNativeBackend = true;
            string runCodecMode;
            if (!state.TryGetValue("FG_MODE", out runCodecMode))
                runCodecMode = noReencode ? "AV1" : "";
            activeRunCodecName = CpuEncoderCore.IsNewCpu(state)?runCodecMode:string.Equals(runCodecMode, "HEVC", StringComparison.OrdinalIgnoreCase) ? "HEVC" :
                string.Equals(runCodecMode, "AV1", StringComparison.OrdinalIgnoreCase) ? "AV1" : "x264";
            if (log != null)
            {
                AppendTaskLog(Environment.NewLine + "============================================================" + Environment.NewLine);
                AppendTaskLog((noReencode
                    ? UiText("[Native .NET AV1] AV1 不重编码原生执行", "[Native .NET AV1] Native AV1 no-reencode execution")
                    : string.Equals(state["FG_MODE"],"AV1",StringComparison.OrdinalIgnoreCase)
                    ? UiText("[Native .NET AV1] AV1 原生执行", "[Native .NET AV1] Native AV1 execution")
                    : string.Equals(state["FG_MODE"],"HEVC",StringComparison.OrdinalIgnoreCase)
                    ? UiText("[Native .NET HEVC] HEVC 原生执行", "[Native .NET HEVC] Native HEVC execution")
                    : CpuEncoderCore.IsNewCpu(state)?"[Native .NET CPU] "+state["FG_MODE"]+" CPU 原生执行": UiText("[Native .NET x264] x264 原生执行", "[Native .NET x264] Native x264 execution"))+Environment.NewLine);
                AppendTaskLog(UiText("输入文件数：", "Input files: ") + prepared.InputFiles.Count.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);
                if (!noReencode) AppendTaskLog(UiText("推荐输出 FPS：", "Recommended output FPS: ") + outputFps.ToString("0.###", CultureInfo.InvariantCulture) + Environment.NewLine);
                AppendTaskLog(UiText("Native 执行工具：", "Native execution tool: ") + prepared.ToolPath + Environment.NewLine);
                foreach (KeyValuePair<string, string> pair in state)
                    AppendTaskDiagnosticLog(pair.Key + "=" + pair.Value + Environment.NewLine);
                AppendTaskLog("------------------------------------------------------------" + Environment.NewLine);
            }

            try
            {
                ScrollLogToEnd();
                submittedQueueFiles.Clear();foreach(string input in inputs)submittedQueueFiles.Add(input);
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
                SetTaskbarProgress(0,0);
                AppendTaskLog("[Task] start failed: " + ex.ToString() + Environment.NewLine);
                if (lblRunStage != null) lblRunStage.Text = UiText("启动失败", "Start failed");
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StartSharedQueue(List<string> inputs, SortedDictionary<string, string> capturedState, bool noReencode, IDictionary<string,SortedDictionary<string,string>> fileStates = null)
        {
            List<BridgeTaskRequest> tasks = new List<BridgeTaskRequest>();
            HashSet<string> outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> inputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            BridgePreparedExecution prepared = null;
            try
            {
                WorkspacePreflightIssue issue;
                if (fileStates == null && !WorkspacePreflightCore.Check(inputs, noReencode ? "AV1" : capturedState["FG_MODE"], noReencode,
                    config.Get("TEMP_MODE"), config.Get("TEMP_CUSTOM_DIR"), config.Get("OUTPUT_MODE"), config.Get("OUTPUT_CUSTOM_DIR"),
                    mediaProbeCache, delegate(WorkspaceSpaceWarning warning)
                    {
                        return MessageBox.Show(this, LF(warning.IsOutput ? "warning.output_space" : "warning.temp_space",
                            warning.Path, FormatWorkspaceBytes(warning.Available), FormatWorkspaceBytes(warning.Required), FormatWorkspaceBytes(warning.Reserve)),
                            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                    }, !noReencode && capturedState["FG_MODE"] == "AV1" && capturedState["FG_SVP_INTERPOLATE"] == "1",
                    !noReencode ? capturedState["FG_INTERPOLATION_TARGET_FPS"] : "", delegate(string input) {
                        MediaProbeInfo probe; IDictionary<string,string> normalized; string notice,reason;
                        return mediaProbeCache.TryGetValue(input,out probe) && SharedInterpolationBypassCore.TryResolve(probe,capturedState,out normalized,out notice,out reason) && normalized != null;
                    }, out issue))
                    throw new InvalidOperationException(issue == null ? "Queue workspace check was declined." : LF(issue.Key, issue.Path, issue.Detail));
                foreach (string input in inputs)
                {
                    prepared = null;
                    if(fileStates!=null) {
                        capturedState=new SortedDictionary<string,string>(fileStates[input],StringComparer.OrdinalIgnoreCase);
                        noReencode=capturedState["FG_QUEUE_NO_REENCODE"]=="1";
                        if(!WorkspacePreflightCore.Check(new List<string>{input},noReencode?"AV1":capturedState["FG_MODE"],noReencode,
                            config.Get("TEMP_MODE"),config.Get("TEMP_CUSTOM_DIR"),config.Get("OUTPUT_MODE"),config.Get("OUTPUT_CUSTOM_DIR"),mediaProbeCache,
                            delegate(WorkspaceSpaceWarning warning) {return MessageBox.Show(this,LF(warning.IsOutput?"warning.output_space":"warning.temp_space",warning.Path,FormatWorkspaceBytes(warning.Available),FormatWorkspaceBytes(warning.Required),FormatWorkspaceBytes(warning.Reserve)),Text,MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)==DialogResult.Yes;},
                            !noReencode&&capturedState["FG_MODE"]=="AV1"&&capturedState["FG_SVP_INTERPOLATE"]=="1",!noReencode?capturedState["FG_INTERPOLATION_TARGET_FPS"]:"",out issue))throw new InvalidOperationException(issue==null?"Workspace check declined":LF(issue.Key,issue.Path,issue.Detail));
                    }
                    foreach(var pair in capturedState)AppendTaskDiagnosticLog("[Queue snapshot] input="+input+" "+pair.Key+"="+pair.Value+Environment.NewLine);
                    string targetDir = capturedState["FG_OUTPUT_MODE"] == "CUSTOM" ? capturedState["FG_OUTPUT_CUSTOM_DIR"] : Path.GetDirectoryName(input);
                    string collisionKey = Path.Combine(Path.GetFullPath(targetDir), Path.GetFileNameWithoutExtension(input));
                    if (!inputNames.Add(collisionKey)) throw new InvalidOperationException("Queue has duplicate output stems: " + input);
                    MediaProbeInfo info;
                    if (!File.Exists(input)) throw new FileNotFoundException("Queue input is unavailable.", input);
                    if (!mediaProbeCache.TryGetValue(input, out info)) throw new InvalidOperationException("Media probe is not ready: " + input);
                    SortedDictionary<string,string> itemState = new SortedDictionary<string,string>(capturedState, StringComparer.OrdinalIgnoreCase);
                    if (noReencode) itemState["FG_MODE"] = "AV1";
                    string reason;
                    if(!NativeExecutionPreparationCore.TryPrepare(appRoot,input,info,itemState,hardwareCaps==null?null:hardwareCaps.Clone(),noReencode,out prepared,out reason))
                        throw new InvalidOperationException("Native queue preparation stopped: "+input+" : "+reason);
                    if (prepared.ExecutionRequest.OutputFiles != null)
                        foreach (string output in prepared.ExecutionRequest.OutputFiles)
                            if (!outputs.Add(Path.GetFullPath(output))) throw new InvalidOperationException("Queue output collision: " + output);
                    BridgeTaskRequest task = BridgeRequestPreparationCore.CreateTask(prepared, fileStates==null?GetRecommendedOutputFps(info):double.Parse(capturedState["FG_QUEUE_OUTPUT_FPS"],CultureInfo.InvariantCulture));
                    task.NativeBackend = true;
                    tasks.Add(task);
                    prepared = null;
                    AppendTaskLog("[Queue] prepared " + tasks.Count + "/" + inputs.Count + " backend=Native" + " input=" + input + Environment.NewLine);
                }
                submittedQueueFiles.Clear();foreach(string input in inputs)submittedQueueFiles.Add(input);
                bridgeTaskCoordinator.SkipFailedFiles=config.GetBool("QUEUE_SKIP_FAILED");
                AppendTaskLog("[Queue] skip_failed="+bridgeTaskCoordinator.SkipFailedFiles+" (frozen at start)"+Environment.NewLine);
                bridgeTaskCoordinator.StartQueue(tasks);
            }
            catch (Exception ex)
            {
                if (prepared != null) BridgeExecutionCore.CleanupTemporaryFiles(prepared.ExecutionRequest);
                foreach (BridgeTaskRequest task in tasks) BridgeExecutionCore.CleanupTemporaryFiles(task.ExecutionRequest);
                SetTaskbarProgress(0,0);
                AppendTaskLog("[Queue] preparation/start failed: " + ex + Environment.NewLine);
                if (lblRunStage != null) lblRunStage.Text = UiText("队列未启动", "Queue not started");
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                    if (btnStart != null) btnStart.Enabled = idle && (!hardwareDetectionInProgress || hardwareCapsReady);
                    if (encodePreviewPanel != null) encodePreviewPanel.Enabled = idle;
                    if (btnCancelTask != null) btnCancelTask.Enabled = e != null && e.State == BridgeTaskState.Running;

                    if (e == null) return;
                    ClearPendingRunProgress();
                    UpdateQueueFileState(e);
                    if (e.State == BridgeTaskState.Running)
                    {
                        BridgeTaskRequest task = e.Task;
                        bool noReencode = task != null && task.NoReencode;
                        IDictionary<string, string> state = task != null && task.ExecutionRequest != null ? task.ExecutionRequest.Environment : null;
                        activeRunNativeBackend = task != null && task.NativeBackend;
                        string codec;
                        activeRunCodecName = state != null && state.TryGetValue("FG_MODE", out codec) ? codec : "AV1";
                        if (lblRunStage != null)
                        {
                            if (noReencode)
                                lblRunStage.Text = UiText("AV1 不重编码处理中", "AV1 no-reencode running");
                            else
                            {
                                string codecName = string.IsNullOrWhiteSpace(activeRunCodecName) ? "x264" : activeRunCodecName;
                                lblRunStage.Text = UiText("Native .NET " + codecName + " 编码处理中", "Native .NET " + codecName + " encoding");
                            }
                        }
                        if (task != null && task.QueueTotal > 1 && lblRunStage != null)
                            lblRunStage.Text = "[" + task.QueueIndex + "/" + task.QueueTotal + "] " + lblRunStage.Text;
                        if (lblRunMetric != null)
                        {
                            string value;
                            if (noReencode)
                                lblRunMetric.Text = "grain: " + (state != null && state.TryGetValue("FG_AV1_GRAIN_MODE", out value) ? value : "—") + "   video: copy";
                            else
                                lblRunMetric.Text = (state != null && state.TryGetValue("FG_GRAIN_ENGINE", out value) ? (SharedGrainCore.IsEnabled(state) ? "grain: " + value : "颗粒：关闭") + "   " : "") +
                                    "fps: " + (task == null ? 0.0 : task.RecommendedOutputFps).ToString("0.###", CultureInfo.InvariantCulture);
                        }
                        if (progressRun != null)
                        {
                            progressRun.Value = task != null && task.QueueTotal > 1 ? ((task.QueueIndex - 1) * 1000 / task.QueueTotal) : 0;
                            progressRun.Style = ProgressBarStyle.Marquee;
                            progressRun.MarqueeAnimationSpeed = 25;
                        }
                        SetTaskbarProgress(1,0);
                        ScrollLogToEnd();
                    }
                    else if (e.State == BridgeTaskState.Cancelling)
                    {
                        SetTaskbarProgress(0,0);
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

        private readonly object runUiSync=new object();
        private readonly StringBuilder pendingRunLog=new StringBuilder();
        private BridgeProgressEventArgs pendingRunStage,pendingRunMetrics,pendingRunProgress;
        private System.Windows.Forms.Timer runUiTimer;
        private void InitializeRunUiUpdates()
        {
            runUiTimer=new System.Windows.Forms.Timer {Interval=100};
            runUiTimer.Tick+=delegate {FlushRunUiUpdates();};runUiTimer.Start();
            FormClosed+=delegate {runUiTimer.Stop();runUiTimer.Dispose();lock(runUiSync){pendingRunLog.Clear();pendingRunStage=pendingRunMetrics=pendingRunProgress=null;}};
        }
        private void OnBridgeExecutionLogLine(object sender, BridgeLogLineEventArgs e)
        {
            string prefix=e.Stream==BridgeLogStream.StandardError?"[stderr] ":e.Stream==BridgeLogStream.StandardOutput?"[stdout] ":"[runner] ";
            string line=prefix+e.Line+Environment.NewLine;
            FullLogFileCore currentLog=taskLog;if(currentLog!=null)currentLog.Write(line);
            if(!VisibleLogCore.ShowProcessLine(e.Line)||IsDisposed||Disposing)return;
            lock(runUiSync) {
                // Only the visible tail is bounded. The disk log above retains every line.
                const int limit=128*1024;
                if(pendingRunLog.Length+line.Length>limit){pendingRunLog.Clear();pendingRunLog.Append("[UI] 显示缓冲已截短，完整内容见任务日志。"+Environment.NewLine);}
                pendingRunLog.Append(line.Length>limit?line.Substring(line.Length-limit):line);
            }
        }
        private void OnBridgeExecutionProgressChanged(object sender, BridgeProgressEventArgs e)
        {
            if(IsDisposed||Disposing||e==null)return;
            lock(runUiSync) {
                if(e.HasStage)pendingRunStage=e;
                if(e.HasMetrics)pendingRunMetrics=e;
                if(e.HasProgress)pendingRunProgress=e;
            }
        }
        private void ClearPendingRunProgress()
        {
            lock(runUiSync){pendingRunStage=pendingRunMetrics=pendingRunProgress=null;}
        }
        private void FlushRunUiUpdates()
        {
            if(IsDisposed||Disposing)return;
            string text;BridgeProgressEventArgs stage,metrics,progress;
            lock(runUiSync) {
                text=pendingRunLog.ToString();pendingRunLog.Clear();
                stage=pendingRunStage;metrics=pendingRunMetrics;progress=pendingRunProgress;
                pendingRunStage=pendingRunMetrics=pendingRunProgress=null;
            }
            if(text.Length>0){AppendVisibleLog(text);ScrollLogToEnd();}
            if(bridgeTaskCoordinator==null||bridgeTaskCoordinator.State!=BridgeTaskState.Running)return;
                    if (stage != null && stage.HasStage && lblRunStage != null)
                    {
                        lblRunStage.Text = (activeRunNativeBackend ? "Native .NET " + (string.IsNullOrWhiteSpace(activeRunCodecName) ? "" : activeRunCodecName + " · ") : "") +
                            UiText("阶段 ", "Stage ") + stage.StageCurrent.ToString(CultureInfo.InvariantCulture) + "/" + stage.StageTotal.ToString(CultureInfo.InvariantCulture) + " · " + stage.StageText;
                    }

                    if (metrics != null && metrics.HasMetrics && lblRunMetric != null)
                    {
                        string metricText = "fps: " + metrics.FpsText + "   speed: " + metrics.SpeedText + "x";
                        if (!string.IsNullOrWhiteSpace(metrics.EtaText)) metricText += "   ETA: " + metrics.EtaText;
                        lblRunMetric.Text = metricText;
                    }

                    if (progress != null && progress.HasProgress && progressRun != null)
                    {
                        progressRun.MarqueeAnimationSpeed = 0;
                        progressRun.Style = ProgressBarStyle.Continuous;
                        int value = Math.Max(progressRun.Minimum, Math.Min(progressRun.Maximum, progress.ProgressPermille));
                        progressRun.Value = value;
                        SetTaskbarProgress(2,value);
                    }
        }

        private void AppendDiagnosticLog(string text)
        {
            if (fullLog != null) fullLog.Write(text);
        }

        private void AppendTaskDiagnosticLog(string text)
        {
            if (taskLog != null) taskLog.Write(text);
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
                int cut = text.IndexOf('\n', text.Length - 384 * 1024);
                if (cut < 0) cut = text.Length - 384 * 1024;
                else cut++;
                log.Select(0, cut);
                log.SelectedText = "";
            }
            log.SelectionStart = log.TextLength;
            log.SelectionLength = 0;
            log.ScrollToCaret();
        }

        private static string FormatTaskElapsed(TimeSpan elapsed)
        {
            long hours=elapsed.Ticks/TimeSpan.TicksPerHour;
            return hours.ToString("00",CultureInfo.InvariantCulture)+":"+
                elapsed.Minutes.ToString("00",CultureInfo.InvariantCulture)+":"+
                elapsed.Seconds.ToString("00",CultureInfo.InvariantCulture)+"."+
                elapsed.Milliseconds.ToString("000",CultureInfo.InvariantCulture);
        }

        private void OnBridgeExecutionCompleted(object sender, BridgeExecutionCompletedEventArgs e)
        {
            string completion = (e.Result.Exception == null ? "" : "[runner] exception=" + e.Result.Exception.ToString() + Environment.NewLine) +
                "[runner] exit_code=" + e.Result.ExitCode.ToString(CultureInfo.InvariantCulture) + " cancelled=" + (e.Result.Cancelled ? "1" : "0") + Environment.NewLine +
                "[Task] " + UiText("总耗时：", "Total elapsed: ") + FormatTaskElapsed(e.Result.TotalElapsed) + Environment.NewLine +
                "============================================================" + Environment.NewLine;
            FullLogFileCore completedLog = taskLog;
            if (completedLog != null) completedLog.Write(completion);
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed || Disposing) return;
                    ClearPendingRunProgress();
                    FlushRunUiUpdates();
                    SetTaskbarProgress(0,0);
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

                    if (hardwareRefreshQueued) BeginHardwareDetection();
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
            if (btnHdrShortcut != null) btnHdrShortcut.Visible = info != null && info.IsHdr && (listFiles == null || listFiles.SelectedItems.Count <= 1);
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
                bool noReencode = codec == 4;
                bool av1 = codec == 0;
                bool hevc = codec == 1;
                bool x264 = codec == 2 || codec == 3;
                bool fgsim = IsGrainEnabled() && ((hevc && IsNvencFgsim()) ||
                    (av1 && cmbGrainMode != null && cmbGrainMode.SelectedIndex == 4));
                
                bool color = colorCorrectionEnabled;
                bool lut = IsLutEffectEnabled() && !string.IsNullOrWhiteSpace(selectedLutPath);
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

                if (fruc && !hevc && !x264 && !av1)
                {
                    unsupported = true;
                    detail = lang.T("input.hdr_route_detail_fruc");
                }
                else if (noReencode)
                {
                    preserve = true;
                    detail = UiText("AV1 不重编码只更换颗粒元数据，不执行色调映射。", "AV1 no-reencode only changes grain metadata; tone mapping is not run.");
                }
                else if (policy == "PRESERVE" && (color || lut))
                {
                    unsupported = true;
                    detail = UiText("HDR 保留与当前 SDR 调色/LUT 不兼容；请关闭这些处理或选择 AUTO/转 SDR。", "HDR Preserve conflicts with SDR color/LUT processing; disable them or choose AUTO/SDR.");
                }
                else if (policy == "SDR")
                {
                    toSdr = true;
                    detail = LF("input.hdr_route_detail_sdr", toneLabel);
                }
                else if (policy == "PRESERVE")
                {
                    bool av1FgsimPreserveReady = av1;
                    if (x264)
                    {
                        unsupported = true;
                        detail = x264
                            ? (codec==3?UiText("H.264 NVENC 本线输出8bit，不能保留HDR10；请选择AUTO或转SDR。","This H.264 NVENC path outputs 8-bit and cannot preserve HDR10; select AUTO or SDR."):(NewCpuSelected()?"当前 CPU 路线支持 SDR / HDR 转 SDR；HDR 保留尚未开放，显式保留会停止。":lang.T("input.hdr_route_detail_x264")))
: lang.T("input.hdr_route_detail_fruc");
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
                        if ((interpolate && !fruc))
                            detail += " " + lang.T("input.hdr_route_detail_bypass");
                    }
                }
                else
                {
                    bool autoToSdr = x264 || lut || color || (interpolate && !fruc) || fgsim;
                    if (autoToSdr)
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

                if(preserve && fruc)
                    detail += " " + UiText("FRUC 使用 P010 10-bit 路线；执行前验证当前工具及设备的高位深能力，不降为 8bit。", "FRUC uses the P010 10-bit path; tool/device precision is validated before execution, without 8-bit fallback.");
                if(fruc && info.IsInterlaced)
                {
                    unsupported = true;
                    detail = UiText("隔行输入当前不支持 FRUC 插帧。", "FRUC interpolation currently requires progressive input.");
                }
                text = unsupported ? lang.T("input.hdr_route_unsupported") :
                    (toSdr ? LF("input.hdr_route_tosdr", toneLabel) : lang.T("input.hdr_route_passthrough"));
                bool hasPreserveBypass = hevc ? (lut || (interpolate && !fruc)) : (lut || color || (interpolate && !fruc));
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
            if (cmbCodec != null && cmbCodec.SelectedIndex == 4)
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
                    if (HasNativeInterpolationBypass(info))
                    {
                        cmbFps.Items[0] = UiText("OpenSVP 已绕过 · ", "OpenSVP bypassed · ") + GetRecommendedOutputFps(info).ToString("0.###",CultureInfo.InvariantCulture) + " fps";
                        if (cmbInterpolationFps != null) cmbInterpolationFps.Enabled = false;
                    }
                    else if (InterpolationFrameRateCore.TryParse(requestedRate, info == null ? "" : info.AvgFrameRate, out targetRate, out rateError))
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

        private string GetLutDisplayName(string path)
        {
            return LutCatalogCore.GetLutDisplayName(path, LutRoot, lang.T("lut.none"));
        }

        private string CatalogPendingText(string error, bool pending = true)
        {
            if (!string.IsNullOrEmpty(error)) return UiText("加载失败，请刷新重试", "Load failed; refresh to retry");
            return pending ? UiText("正在加载…", "Loading...") : UiText("展开或刷新时加载", "Open or refresh to load");
        }

        private void SetLutListPlaceholder(bool loading)
        {
            if (cmbRecentLut == null || cmbFavoriteLut == null) return;
            string text = loading ? CatalogPendingText(null) : (!string.IsNullOrEmpty(lutCatalog.Error)
                ? UiText("加载失败，展开可重试", "Load failed; open to retry") : UiText("展开时加载", "Open to load"));
            loadingLutLists = true;
            try {
                foreach (ComboBox combo in new ComboBox[] { cmbRecentLut, cmbFavoriteLut }) {
                    LutChoice existing = combo.Items.Count == 1 ? combo.Items[0] as LutChoice : null;
                    if (existing != null && existing.Display == text && existing.PathValue.Length == 0) continue;
                    combo.BeginUpdate();
                    try { combo.Items.Clear(); combo.Items.Add(new LutChoice(text, "")); combo.SelectedIndex = 0; combo.Enabled = true; }
                    finally { combo.EndUpdate(); }
                }
            } finally { loadingLutLists = false; }
        }

        private void ApplyLutRecords(LutRecordPair value)
        {
            loadingLutLists = true;
            try {
                PopulateLutCombo(cmbRecentLut, value.Recent, lang.T("lut.recent_select"), lang.T("lut.recent_empty"), "Recent");
                PopulateLutCombo(cmbFavoriteLut, value.Favorite, lang.T("lut.favorite_select"), lang.T("lut.favorite_empty"), "Favorite");
            } finally { loadingLutLists = false; }
        }

        private void RefreshLutLists()
        {
            // Called after gallery/color operations: invalidate; next actual use reads once.
            lutCatalog.Invalidate();
            SetLutListPlaceholder(false);
        }

        private bool LutComboMatches(ComboBox combo, List<LutChoice> entries, string selectText, string emptyText)
        {
            if (combo.Items.Count != Math.Max(1, entries.Count + (entries.Count > 0 ? 1 : 0))) return false;
            LutChoice first = combo.Items[0] as LutChoice;
            if (first == null || first.PathValue.Length != 0 || first.Display != (entries.Count == 0 ? emptyText : selectText)) return false;
            int selected = 0;
            for (int i = 0; i < entries.Count; ++i) {
                LutChoice old = combo.Items[i + 1] as LutChoice;
                if (old == null || old.Display != entries[i].Display || old.PathValue != entries[i].PathValue) return false;
                if (string.Equals(selectedLutPath, entries[i].PathValue, StringComparison.OrdinalIgnoreCase)) selected = i + 1;
            }
            return combo.SelectedIndex == selected && combo.Enabled;
        }

        private void PopulateLutCombo(ComboBox combo, List<LutChoice> entries, string selectText, string emptyText, string source)
        {
            if (LutComboMatches(combo, entries, selectText, emptyText)) return;
            combo.BeginUpdate();
            try
            {
                combo.Items.Clear();
                if (entries.Count == 0)
                {
                    combo.Items.Add(new LutChoice(emptyText, ""));
                    combo.SelectedIndex = 0;
                    combo.Enabled = true;
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
            if (lutCatalog.Ready) ApplyLutRecords(lutCatalog.Value);
            UpdateLutUi();
            UpdateColorCorrectionUi();
        }

        private void UpdateLutUi()
        {
            if (lblSelectedLut == null || picLutPreview == null || chkLut == null) return;

            bool hasLut = !string.IsNullOrEmpty(selectedLutPath) && File.Exists(selectedLutPath);
            if (!hasLut && !string.IsNullOrEmpty(selectedLutPath) && !independentFiles)
            {
                selectedLutPath = "";
                selectedLutSource = "None";
            }

            if (hasLut)
            {
                lblSelectedLut.Text = LutStrengthCore.Caption(GetLutDisplayName(selectedLutPath));
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

            UpdateLutCompoundHint(hasLut);
            bool enabled = chkLut.Checked;
            trackLutStrength.Enabled = enabled;
            lblLutStrength.Enabled = enabled;
            UpdateHdrRouteStatus(GetSelectedMediaInfo());
        }

        private void UpdateLutCompoundHint(bool hasLut)
        {
            if(lblLutCompoundHint==null||lutCompoundHintRow==null)return;
            if(!hasLut){lutTypeSignature="";selectedLutIsCompound=false;}
            else try {
                var file=new FileInfo(selectedLutPath);
                string signature=file.FullName+"|"+file.Length+"|"+file.LastWriteTimeUtc.Ticks;
                if(signature!=lutTypeSignature) {
                    selectedLutIsCompound=LutStrengthCore.IsCompoundFile(selectedLutPath);
                    lutTypeSignature=signature;
                }
            }catch {lutTypeSignature="";selectedLutIsCompound=false;}
            lblLutCompoundHint.Visible=hasLut&&selectedLutIsCompound;
            float height=hasLut&&selectedLutIsCompound?40:0;
            if(lutCompoundHintRow.Height!=height)lutCompoundHintRow.Height=height;
        }

        private void OpenLutStrengthPreview()
        {
            if(string.IsNullOrEmpty(selectedLutPath)||!File.Exists(selectedLutPath)){MessageBox.Show(this,UiText("请先选择 LUT。","Select a LUT first."));return;}
            if(bridgeTaskCoordinator!=null&&bridgeTaskCoordinator.IsActive){MessageBox.Show(this,UiText("请等待当前任务结束。","Wait for the active task to finish."));return;}
            string video=GetPreviewVideoPath();MediaProbeInfo media=null;
            if(!string.IsNullOrEmpty(video))mediaProbeCache.TryGetValue(video,out media);
            if(string.IsNullOrEmpty(video)||media==null){MessageBox.Show(this,UiText("请选择一个已完成探测的视频。","Select one fully probed video."));return;}
            using(var window=new NativeLutStrengthPreviewForm(Path.Combine(config.Get("FFMPEG_DIR"),"ffmpeg.exe"),selectedLutPath,video,lang.Code,media)) {
                AttachOwnedTheme(window,this,log);window.ShowDialog(this);
                if(window.SelectedStrength>0){
                    try{lutSessionReference.Set(window.SelectedReference);}
                    catch(Exception ex){MessageBox.Show(this,UiText("无法保存临时参考图：","Could not save the temporary reference: ")+ex.Message);return;}
                    trackLutStrength.Value=window.SelectedStrength/25;chkLut.Checked=true;UpdateLutUi();
                }
            }
        }

        private int GetLutStrength(){return trackLutStrength==null?75:Math.Max(0,Math.Min(4,trackLutStrength.Value))*25;}
        private bool IsLutEffectEnabled(){return chkLut!=null&&chkLut.Checked&&GetLutStrength()>0;}

        private void SetLutPreview(string lutPath)
        {
            if(lutThumbnail==null||picLutPreview==null)return;
            if(string.IsNullOrEmpty(lutPath)){lutThumbnail.Queue("","","",100);lutToolTip.SetToolTip(picLutPreview,"");return;}
            string tools=Path.Combine(appRoot,"_LUT_Tools"),reference=Path.Combine(tools,"LUT_Reference_Current.jpg");
            if(!File.Exists(reference))reference=Path.Combine(tools,"LUT_Reference_Default.jpg");
            if(!string.IsNullOrEmpty(lutSessionReference.PathValue))reference=lutSessionReference.PathValue;
            int percent=GetLutStrength();
            lutThumbnail.Queue(Path.Combine(config.Get("FFMPEG_DIR"),"ffmpeg.exe"),lutPath,reference,percent);
            lutToolTip.SetToolTip(picLutPreview,UiText("点击从实际视频生成 LUT 四档对照","Click to compare four LUT strengths on a video frame")+"\n"+percent+"% · "+lutPath);
        }

        private void OpenLutGallery()
        {
            if(!Directory.Exists(LutRoot)) {MessageBox.Show(this,LF("error.lut_root_missing",LutRoot));return;}
            btnLutGallery.Enabled=false;
            try {
                using(var gallery=new NativeLutGalleryForm(appRoot,Path.Combine(config.Get("FFMPEG_DIR"),"ffmpeg.exe"),LutRoot,LutPreviewRoot)) {
                    gallery.PrepareTheme(this);
                    if(gallery.ShowDialog(this)==DialogResult.OK) {
                        if(gallery.DisabledLut){selectedLutPath="";selectedLutSource="None";chkLut.Checked=false;}
                        else {selectedLutPath=gallery.SelectedLut;selectedLutSource="Gallery";chkLut.Checked=true;}
                    }
                }
            }catch(Exception ex){MessageBox.Show(this,LF("error.lut_gallery_open_failed",ex.Message));}
            finally{btnLutGallery.Enabled=true;RefreshLutLists();UpdateLutUi();}
        }

        private void UpdateColorCorrectionUi()
        {
            if (btnColorCorrection == null) return;
            string baseText = lang.T("button.color_correction");
            btnColorCorrection.Text = colorCorrectionEnabled ? UiText("调色已启用", "Color: ON") : baseText;
            btnColorCorrection.UseVisualStyleBackColor = !colorCorrectionEnabled;
            btnColorCorrection.BackColor = colorCorrectionEnabled ? Color.FromArgb(185,65,20) : SystemColors.Control;
            btnColorCorrection.ForeColor = colorCorrectionEnabled ? Color.White : SystemColors.ControlText;
            btnColorCorrection.FlatStyle = colorCorrectionEnabled ? FlatStyle.Flat : FlatStyle.Standard;
            btnColorCorrection.FlatAppearance.BorderColor = colorCorrectionEnabled ? Color.FromArgb(135,40,10) : SystemColors.ControlDark;
            if (uiTheme != null) uiTheme.CorrectionState(btnColorCorrection, colorCorrectionEnabled);
            UpdateHdrRouteStatus(GetSelectedMediaInfo());
        }

        private string GetPreviewVideoPath()
        {
            if (listFiles.SelectedItems.Count == 1) return listFiles.SelectedItems[0].Tag as string ?? "";
            if (listFiles.Items.Count == 1) return listFiles.Items[0].Tag as string ?? "";
            return "";
        }

        private void OpenGrainPreview()
        {
            if (!RequiredGrainCatalogReady()) {
                EnsureCurrentGrainCatalog(false);
                MessageBox.Show(this, UiText("颗粒列表正在加载；完成后请重试。", "Loading the grain catalog; retry when ready."));
                return;
            }
            if (bridgeTaskCoordinator != null && bridgeTaskCoordinator.IsActive)
            { MessageBox.Show(this, UiText("请等待当前任务结束后再生成颗粒预览。", "Wait for the current task to finish before rendering a grain preview.")); return; }
            string input = GetPreviewVideoPath();
            MediaProbeInfo info = null;
            if (!string.IsNullOrEmpty(input)) mediaProbeCache.TryGetValue(input, out info);
            bool metadata = (cmbCodec.SelectedIndex == 0 || cmbCodec.SelectedIndex == 4) && cmbGrainMode.SelectedIndex <= 2;
            if (string.IsNullOrEmpty(input) || info == null)
            { MessageBox.Show(this, UiText("请选择一个已完成探测的视频。", "Select one fully probed video.")); return; }
            if (!metadata)
            {
                if (!IsGrainEnabled()) {MessageBox.Show(this,UiText("请先启用颗粒。","Enable grain first."));return;}
                string error;var pixelState=BuildBridgeEnvironment(out error);
                if(pixelState==null){MessageBox.Show(this,error);return;}
                using(var window=new NativeGrainPreviewForm(Path.Combine(config.Get("FFMPEG_DIR"),"ffmpeg.exe"),appRoot,input,pixelState,hardwareCaps,info,lang.Code))
                {window.ShowDialog(this);if(window.SelectedStrength>0 && trackFilmGrainStrength!=null) {
                    if(pixelState["FG_GRAIN_ENGINE"]=="FGSIM")trackFilmGrainStrength.Value=window.SelectedStrength-1;
                    else if(pixelState["FG_GRAIN_ENGINE"]=="NATIVE")trackFilmGrainStrength.Value=window.SelectedStrength/10;
                    else trackFilmGrainStrength.Value=window.SelectedStrength;
                }}
                return;
            }
            if (info.VideoCodec != "av1") {MessageBox.Show(this,UiText("AV1 元数据颗粒预览需要 AV1 输入；其它编码的源可使用下方预览编码。","AV1 metadata preview requires AV1 input; use preview encoding for other source codecs."));return;}
            if (!IsGrainEnabled())
            { MessageBox.Show(this, UiText("请启用颗粒并选择 AV1 的预设、ISO 或颗粒表模式。", "Enable grain and select an AV1 preset, ISO or table mode.")); return; }
            var state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            state["FG_AV1_FORMAT"] = (av1FormatIndex + 1).ToString(CultureInfo.InvariantCulture);
            state["FG_AV1_STOCK"] = (av1StockIndex + 1).ToString(CultureInfo.InvariantCulture);
            state["FG_AV1_ISO"] = av1IsoValue.ToString(CultureInfo.InvariantCulture);
            state["FG_AV1_CHROMA"] = av1ChromaEnabled ? "1" : "0";
            state["FG_AV1_GRAIN_TABLE"] = selectedAv1GrainTable;
            var args = new List<string>(); string tag, reason;
            string mode = new string[] { "PRESET", "ISO", "TABLE" }[cmbGrainMode.SelectedIndex];
            if (!NativeAv1GrainReplaceCore.TryBuildGrainArgs(mode, state, args, out tag, out reason)) { MessageBox.Show(this, reason); return; }
            using (var window = new NativeGrainPreviewForm(Path.Combine(config.Get("FFMPEG_DIR"), "ffmpeg.exe"), config.Get("GRAV1SYNTH"), input, args, info, lang.Code))
            {
                window.ShowDialog(this);
                if (window.SelectedStrength > 0 && av1StrengthControl != null) av1StrengthControl.Value = window.SelectedStrength/10;
            }
        }

        private void UpdateCinematicCropLabels()
        {
            if(cmbFrameMode==null)return;int side=config.GetInt("CINEMATIC_CROP_PER_SIDE",0);
            int horizontal=config.GetInt("CINEMATIC_CROP_HORIZONTAL",0);
            if(config.Get("CINEMATIC_NO_CROP")=="1"){cmbFrameMode.Items[0]="加黑边 · 原始画幅 / Bars · Original";cmbFrameMode.Items[1]="裁剪 · 原始画幅 / Crop · Original";return;}
            if(horizontal>0){cmbFrameMode.Items[0]="加黑边 / Bars · V"+side+" H"+horizontal;cmbFrameMode.Items[1]="裁剪 / Crop · V"+side+" H"+horizontal;return;}
            cmbFrameMode.Items[0]=side>0?UiText("加黑边 · 上下各 "+side+" px","Letterbox · "+side+" px per side"):lang.T("cinematic.letterbox");
            cmbFrameMode.Items[1]=side>0?UiText("裁剪 · 上下各 "+side+" px","Crop · "+side+" px per side"):lang.T("cinematic.crop");
        }

        private void OpenCinematicCrop()
        {
            string input=GetPreviewVideoPath();MediaProbeInfo media;
            if(string.IsNullOrEmpty(input)||!mediaProbeCache.TryGetValue(input,out media)) {MessageBox.Show(this,UiText("请先选择并探测视频。","Select and probe a video first."));return;}
            if(media.Width<2||media.Height<2){MessageBox.Show(this,UiText("视频尺寸无效。","Invalid video dimensions."));return;}
            if(bridgeTaskCoordinator!=null && bridgeTaskCoordinator.IsActive)return;
            using(var window=new NativeCinematicCropForm(Path.Combine(config.Get("FFMPEG_DIR"),"ffmpeg.exe"),input,media,
                config.GetInt("CINEMATIC_CROP_PER_SIDE",0),cmbFrameMode.SelectedIndex,config.GetInt("CINEMATIC_CROP_HORIZONTAL",0),config.Get("CINEMATIC_NO_CROP")=="1"))
            {
                if(window.ShowDialog(this)!=DialogResult.OK)return;
                try {config.SaveValue("CINEMATIC_CROP_PER_SIDE",window.CropPerSide.ToString(CultureInfo.InvariantCulture));config.SaveValue("CINEMATIC_CROP_HORIZONTAL",window.CropHorizontal.ToString(CultureInfo.InvariantCulture));config.SaveValue("CINEMATIC_NO_CROP",window.NoCrop?"1":"0");}
                catch(Exception ex){MessageBox.Show(this,ex.Message);return;}
                cmbFrameMode.SelectedIndex=window.FrameMode;UpdateCinematicCropLabels();UpdateBitrateDisplays();
            }
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
            string galleryRoot = appRoot;
            if (!File.Exists(ffmpeg) || !File.Exists(ffprobe))
            {
                MessageBox.Show(this, "FFmpeg / FFprobe not found:\r\n" + config.Get("FFMPEG_DIR"), "Film Grain Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int lutStrength = GetLutStrength();
            btnColorCorrection.Enabled = false;
            if (log != null) AppendLog("[Preview] Opening native .NET color correction..." + Environment.NewLine);

            try
            {
                using (NativeColorCorrectionForm dlg = new NativeColorCorrectionForm(
                    videoPath,
                    ffmpeg,
                    ffprobe,
                    galleryRoot,
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
                        trackLutStrength.Value = Math.Max(0,Math.Min(4,result.LutStrength/25));
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
            MessageBox.Show(this, message, "Film Grain Studio .NET", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // Optional diagnostics; no disk I/O on the UI thread until the window closes.
    internal static class StartupTiming
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly object Sync = new object();
        private static readonly List<string> Rows = new List<string>();
        private static readonly string Output = Environment.GetEnvironmentVariable("FGS_STARTUP_TRACE");
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetUpdateRect(IntPtr window, IntPtr rect, bool erase);
        internal static void Mark(string name) { Mark(name, ""); }
        internal static void Mark(string name, string detail)
        {
            if (string.IsNullOrEmpty(Output)) return;
            lock (Sync) Rows.Add(Clock.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture) + "," + name + "," + detail.Replace(',', ';').Replace('\r',' ').Replace('\n',' '));
        }
        internal static bool PaintPending(Control control)
        {
            if (!control.Visible) return false;
            if (control.IsHandleCreated && GetUpdateRect(control.Handle, IntPtr.Zero, false)) return true;
            foreach (Control child in control.Controls) if (PaintPending(child)) return true;
            return false;
        }
        internal static void Attach(Form form)
        {
            if (string.IsNullOrEmpty(Output)) return;
            bool complete = false;
            EventHandler idle = null;
            idle = delegate {
                if (complete || form.IsDisposed || !form.Visible || PaintPending(form)) return;
                complete = true; Mark("paint.queue.clear"); Application.Idle -= idle;
            };
            form.Load += delegate { Mark("load"); };
            form.Shown += delegate { Mark("shown"); };
            form.Paint += delegate { Mark("form.paint"); };
            Application.Idle += idle;
            form.FormClosed += delegate {
                Application.Idle -= idle; Mark("closed");
                try { lock (Sync) { var lines = new List<string>(); lines.Add("ms,event,detail"); lines.AddRange(Rows); File.WriteAllLines(Output, lines.ToArray(), new System.Text.UTF8Encoding(true)); } }
                catch (Exception ex) { Trace.WriteLine(ex.Message); }
            };
        }
    }
    // State is owned by the UI thread. Workers receive captured key/version and data only.
    internal sealed class DemandCatalog<T>
    {
        internal string Key, Error;
        internal T Value;
        internal bool Ready, Running, Requested;
        internal int Version, Runs, CacheHits, Joins;
        private int runningVersion;
        internal bool SetKey(string key)
        {
            key = key ?? "";
            if (string.Equals(Key, key, StringComparison.OrdinalIgnoreCase)) return false;
            Key = key; Invalidate(); return true;
        }
        internal void Invalidate()
        {
            ++Version; Ready = false; Requested = false; Value = default(T); Error = null;
        }
        internal void Request(string key, bool force)
        {
            SetKey(key);
            if (force) { ++Version; Error = null; Requested = true; return; }
            if (Ready) { ++CacheHits; StartupTiming.Mark("catalog.cache.hit"); return; }
            if (Running && runningVersion == Version) { ++Joins; return; }
            Requested = true;
        }
        internal bool TryStart(out string key, out int version)
        {
            key = Key; version = Version;
            if (!Requested || Running) return false;
            Requested = false; Running = true; runningVersion = Version; ++Runs; return true;
        }
        internal bool Complete(string key, int version, T value, Exception error)
        {
            Running = false;
            if (version != Version || !string.Equals(Key, key, StringComparison.OrdinalIgnoreCase)) return false;
            Requested = false; Error = error == null ? null : error.Message;
            Ready = error == null;
            if (Ready) Value = value;
            return true;
        }
    }
}


namespace FilmGrainStudioPreview
{
    internal sealed partial class MainForm
    {
        // Keep established preset control IDs and codec slot numbers stable.
        // Slot 2 owns the existing CPU pixel editor; CPU_CODEC selects its final encoder.
        private ComboBox cmbCodecFamily;
        private CheckBox chkCpuEncoder;
        private bool syncingCodecPicker;
        private string CpuCodec() {string c=config.Get("CPU_CODEC");return c=="SVT"||c=="X265"?c:"X264";}
        private bool NewCpuSelected() {return cmbCodec!=null&&cmbCodec.SelectedIndex==2&&CpuCodec()!="X264";}
        private Control BuildCodecPicker()
        {
            var panel=new TableLayoutPanel{Dock=DockStyle.Fill,Margin=Padding.Empty,ColumnCount=2,RowCount=1};
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,120));
            cmbCodecFamily=Combo(new[]{"AV1","HEVC","H264"},0);chkCpuEncoder=Check("CPU · SVT",false);
            panel.Controls.Add(cmbCodecFamily,0,0);panel.Controls.Add(chkCpuEncoder,1,0);
            cmbCodecFamily.SelectionChangeCommitted+=delegate{CommitCodecPicker();};
            chkCpuEncoder.CheckedChanged+=delegate{if(!syncingCodecPicker)CommitCodecPicker();};
            cmbCodec.SelectedIndexChanged+=delegate{SyncCodecPicker();};SyncCodecPicker();return panel;
        }
        private void CommitCodecPicker()
        {
            if(syncingCodecPicker||restoringFileEditor)return;
            int family=Math.Max(0,cmbCodecFamily.SelectedIndex);bool cpu=chkCpuEncoder.Checked;
            if(cpu)config.SaveValue("CPU_CODEC",family==0?"SVT":family==1?"X265":"X264");
            cmbCodec.SelectedIndex=cpu?2:family==0?0:family==1?1:3;
            SyncCodecPicker();UpdateSpeedChoices();UpdateGrainForCodec();LoadBitrateStateForCodec();UpdateBitrateDisplays();UpdateSfeUi();UpdateMediaDrivenUi(GetSelectedMediaInfo());
        }
        private void SyncCodecPicker()
        {
            if(cmbCodecFamily==null||chkCpuEncoder==null)return;syncingCodecPicker=true;
            try {bool cpu=cmbCodec.SelectedIndex==2;string codec=CpuCodec();
                cmbCodecFamily.SelectedIndex=cpu?(codec=="SVT"?0:codec=="X265"?1:2):cmbCodec.SelectedIndex==1?1:cmbCodec.SelectedIndex==3?2:0;
                chkCpuEncoder.Checked=cpu;chkCpuEncoder.Text="CPU · "+(cmbCodecFamily.SelectedIndex==0?"SVT":cmbCodecFamily.SelectedIndex==1?"x265":"x264");
                cmbCodecFamily.Enabled=cmbCodec.SelectedIndex!=4;chkCpuEncoder.Enabled=cmbCodec.SelectedIndex!=4;
            }finally{syncingCodecPicker=false;}
        }

    }
}
