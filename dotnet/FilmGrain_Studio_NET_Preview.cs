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
        internal const string ReleaseVersion = "5.0.68";
        internal const string FileReleaseVersion = "5.0.68.0";
        internal const string BuildLabel = "Stable";
        [STAThread]
        private static void Main(string[] args)
        {
            if(args.Length>0&&args[0]=="--av1-hdr-ivf-stream")
            { Environment.ExitCode=NvencHdr10StreamCore.Run(args);return; }
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
                    FgsMessageBox.Show("Film Grain Studio 已在运行，请切换到现有窗口。 / Film Grain Studio is already running.", "Film Grain Studio", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                FgsMessageBox.Show(
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
                FgsMessageBox.Show(ex.ToString(), "Film Grain Studio .NET", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        private LinkLabel lblInputNote;
        private Label lblExistingOutput;
        private Label lblHdrRoute;
        private Button btnHdrShortcut;
        private Button btnAv1Bake;
        private Label lblStatus;
        private Label lblRunStage;
        private Label lblQueueElapsed;
        private readonly object queueClockSync = new object();
        private readonly Stopwatch queueClock = new Stopwatch();
        private string queueClockStatus = "";
        private bool preparingQueue;
        private Label lblRunMetric;
        private TextBox log;
        private FullLogFileCore fullLog;
        private FullLogFileCore taskLog;
        private bool fullLogErrorReported;
        private ComboBox cmbCodec;
        private ComboBox cmbContainer;
        private ComboBox cmbSpeed;
        private CheckBox chkSfe;
        private CheckBox chkGpuDecode;
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
        
        
    