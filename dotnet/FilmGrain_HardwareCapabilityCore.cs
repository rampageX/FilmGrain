using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace FilmGrainStudioPreview
{
    internal sealed class HardwareCapabilityRequest
    {
        public string AppRoot { get; set; }
        public string FfmpegPath { get; set; }
        public string Grav1synthPath { get; set; }
        public string FrucFfmpegPath { get; set; }
        public string FrucPerf { get; set; }
        public string FrucGrid { get; set; }
        public bool ForceDetection { get; set; }
        public string NvidiaSmiPath { get; set; }
        public string NvencQueryPath { get; set; }
    }

    internal sealed class HardwareCapabilitySnapshot
    {
        public bool FrucReady { get; set; }
        public bool FrucAvailable { get; set; }
        public string FrucFailureKind { get; set; }
        public string FrucReason { get; set; }
        public string FrucFfmpegPath { get; set; }
        public string FrucPerf { get; set; }
        public string FrucGrid { get; set; }
        public string Diagnostics { get; set; }
        public bool Ready { get; set; }
        public string Error { get; set; }
        public string CacheState { get; set; }
        public string FfmpegVersion { get; set; }
        public string GpuName { get; set; }
        public string DriverVersion { get; set; }
        public bool Av1Available { get; set; }
        public bool Av1UhqAvailable { get; set; }
        public bool Av1BFrames { get; set; }
        public bool Av1BReference { get; set; }
        public bool Av1SpatialAq { get; set; }
        public bool Av1TemporalAq { get; set; }
        public bool Av1Lookahead { get; set; }
        public bool Av1AdaptiveB { get; set; }
        public bool Av1SceneCut { get; set; }
        public bool Av1MultipassQres { get; set; }
        public bool Av1MultipassFullres { get; set; }
        public int Av1SplitEncodeMaxEngines { get; set; }
        public bool Grav1synthSfeCompatible { get; set; }
        public string Grav1synthVersion { get; set; }
        public bool HevcAvailable { get; set; }
        public bool HevcBFrames { get; set; }
        public bool HevcBReference { get; set; }
        public bool HevcSpatialAq { get; set; }
        public bool HevcTemporalAq { get; set; }
        public bool HevcLookahead { get; set; }
        public bool HevcAdaptiveB { get; set; }
        public bool HevcSceneCut { get; set; }
        public bool HevcMultipassQres { get; set; }
        public bool HevcMultipassFullres { get; set; }
        public bool X264Available { get; set; }
        public bool X264BStrategy2 { get; set; }
        public bool X264High10Available { get; set; }
        public bool OpenSvpGpuAvailable { get; set; }
    }

    internal sealed class HardwareCapabilityCompletedEventArgs : EventArgs
    {
        public HardwareCapabilitySnapshot Snapshot { get; private set; }

        public HardwareCapabilityCompletedEventArgs(HardwareCapabilitySnapshot snapshot)
        {
            Snapshot = snapshot;
        }
    }

    internal sealed class HardwareCapabilityCore : IDisposable
    {
        private readonly object sync = new object();
        private Process activeProcess;
        private Thread workerThread;
        private bool disposed;
        private volatile bool cancelRequested;

        public event EventHandler<HardwareCapabilityCompletedEventArgs> Completed;

        public bool IsRunning
        {
            get
            {
                lock (sync)
                {
                    return activeProcess != null || (workerThread != null && workerThread.IsAlive);
                }
            }
        }

        public void Start(HardwareCapabilityRequest request)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (string.IsNullOrWhiteSpace(request.AppRoot)) throw new ArgumentException("AppRoot is required.", "request");
            if (string.IsNullOrWhiteSpace(request.FfmpegPath)) throw new ArgumentException("FfmpegPath is required.", "request");

            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("HardwareCapabilityCore");
                if (activeProcess != null || (workerThread != null && workerThread.IsAlive)) return;
                cancelRequested = false;
                workerThread = new Thread(new ThreadStart(delegate { RunWorker(request); }));
                workerThread.IsBackground = true;
                workerThread.Name = "FGS Hardware Capability Detection";
                workerThread.Start();
            }
        }

        public void Cancel()
        {
            cancelRequested = true;
            Process process = null;
            lock (sync) { process = activeProcess; }
            if (process == null) return;
            int pid = 0;
            try { pid = process.Id; } catch { }
            if (pid > 0) TryTaskKillTree(pid);
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch { }
        }

        private void RunWorker(HardwareCapabilityRequest request)
        {
            HardwareCapabilitySnapshot snapshot = new HardwareCapabilitySnapshot { Av1SplitEncodeMaxEngines = 1 };
            try
            {
                try { snapshot = NativeHardwareProbe.Detect(request, CaptureProbe); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { snapshot.Ready=false; snapshot.Error=ex.Message; }
                if (cancelRequested) throw new OperationCanceledException();
                if (!cancelRequested) FrucCapabilityDetection.Detect(request, snapshot, CaptureRawProbe);
            }
            catch (OperationCanceledException) { snapshot.Ready = false; snapshot.Error = "Hardware capability detection was cancelled."; }
            catch (Exception ex) { snapshot.Ready = false; snapshot.Error = ex.Message; }
            finally
            {
                lock (sync) { activeProcess = null; workerThread = null; }
                if (cancelRequested) { snapshot.Ready=false; snapshot.Error="Hardware capability detection was cancelled."; }
                if (!disposed) RaiseCompleted(snapshot);
            }
        }

        private NativeProbeResult CaptureProbe(string tool, IList<string> args, int timeout, string appData)
        { return Capture(tool, SharedFrucCore.JoinArguments(args), timeout, appData); }

        private bool CaptureRawProbe(string tool, string args, int timeout, out string output, out string reason)
        {
            NativeProbeResult result = Capture(tool, args, timeout, null);
            output = result.Output;
            reason = result.Success ? "" : result.Reason;
            return result.Success;
        }

        private NativeProbeResult Capture(string tool, string args, int timeout, string appData)
        {
            if (cancelRequested || disposed) throw new OperationCanceledException();
            if (!File.Exists(tool)) return new NativeProbeResult { Reason = "Executable is missing: " + tool };
            var text = new StringBuilder(); object textLock = new object();
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo { FileName=tool, Arguments=args, WorkingDirectory=Path.GetDirectoryName(tool),
                    UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
                if (!string.IsNullOrEmpty(appData)) process.StartInfo.EnvironmentVariables["APPDATA"] = appData;
                DataReceivedEventHandler capture = delegate(object sender, DataReceivedEventArgs e) {
                    if (e.Data != null) lock(textLock) { if (text.Length < 64000) text.AppendLine(e.Data); }
                };
                process.OutputDataReceived += capture; process.ErrorDataReceived += capture;
                try
                {
                    lock(sync) { if (cancelRequested || disposed) throw new OperationCanceledException(); process.Start(); activeProcess=process; }
                    process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    var clock = Stopwatch.StartNew();
                    while (!process.WaitForExit(100))
                    {
                        if (cancelRequested || disposed) { try { process.Kill(); } catch { } throw new OperationCanceledException(); }
                        if (clock.ElapsedMilliseconds >= timeout)
                        { try { process.Kill(); process.WaitForExit(); } catch { } return new NativeProbeResult { Reason="Probe timed out: " + Path.GetFileName(tool) }; }
                    }
                    process.WaitForExit();
                    if (cancelRequested || disposed) throw new OperationCanceledException();
                    string output; lock(textLock) { output=text.ToString(); }
                    return new NativeProbeResult { Success=process.ExitCode==0, Output=output,
                        Reason=process.ExitCode==0 ? "" : "exit=" + process.ExitCode + " " + output.Trim() };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { return new NativeProbeResult { Reason=ex.Message }; }
                finally { lock(sync) { if (object.ReferenceEquals(activeProcess,process)) activeProcess=null; } }
            }
        }

        internal static HardwareCapabilitySnapshot ParseCache(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Hardware capability cache was not generated.", path);
            string json = File.ReadAllText(path, Encoding.UTF8);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            object rootObject = serializer.DeserializeObject(json);
            return ParseProfile(AsMap(rootObject));
        }

        internal static HardwareCapabilitySnapshot ParseProfile(Dictionary<string,object> root)
        {
            Dictionary<string, object> ffmpeg = Child(root, "ffmpeg");
            Dictionary<string, object> gpu = Child(root, "gpu");
            Dictionary<string, object> grav = Child(root, "grav1synth");
            Dictionary<string, object> caps = Child(root, "caps");
            Dictionary<string, object> av1 = Child(caps, "av1");
            Dictionary<string, object> hevc = Child(caps, "hevc");
            Dictionary<string, object> x264 = Child(caps, "x264");
            Dictionary<string, object> svp = Child(caps, "openSvp");

            HardwareCapabilitySnapshot result = new HardwareCapabilitySnapshot();
            result.CacheState = GetString(root, "cacheState");
            result.FfmpegVersion = ParseFfmpegVersion(GetString(ffmpeg, "version"));
            result.GpuName = GetString(gpu, "name");
            result.DriverVersion = GetString(gpu, "driverVersion");
            result.Av1Available = GetBool(av1, "available");
            result.Av1UhqAvailable = GetBool(av1, "uhq");
            result.Av1BFrames = GetBool(av1, "bFrames");
            result.Av1BReference = GetBool(av1, "bReference");
            result.Av1SpatialAq = GetBool(av1, "spatialAQ");
            result.Av1TemporalAq = GetBool(av1, "temporalAQ");
            result.Av1Lookahead = GetBool(av1, "lookahead");
            result.Av1AdaptiveB = GetBool(av1, "adaptiveB");
            result.Av1SceneCut = GetBool(av1, "sceneCut");
            result.Av1MultipassQres = GetBool(av1, "multipassQres");
            result.Av1MultipassFullres = GetBool(av1, "multipassFullres");
            result.Av1SplitEncodeMaxEngines = Math.Max(1, GetInt(av1, "splitEncodeMaxEngines", 1));
            result.Grav1synthSfeCompatible = GetBool(grav, "sfeCompatible");
            result.Grav1synthVersion = GetString(grav, "version");
            result.HevcAvailable = GetBool(caps, "hevcPipeline");
            result.HevcBFrames = GetBool(hevc, "bFrames");
            result.HevcBReference = GetBool(hevc, "bReference");
            result.HevcSpatialAq = GetBool(hevc, "spatialAQ");
            result.HevcTemporalAq = GetBool(hevc, "temporalAQ");
            result.HevcLookahead = GetBool(hevc, "lookahead");
            result.HevcAdaptiveB = GetBool(hevc, "adaptiveB");
            result.HevcSceneCut = GetBool(hevc, "sceneCut");
            result.HevcMultipassQres = GetBool(hevc, "multipassQres");
            result.HevcMultipassFullres = GetBool(hevc, "multipassFullres");
            result.X264Available = GetBool(caps, "x264Pipeline");
            result.X264BStrategy2 = GetBool(x264, "bStrategy2");
            result.X264High10Available = GetBool(x264, "high10");
            result.OpenSvpGpuAvailable = GetBool(svp, "gpu");
            return result;
        }

        private static Dictionary<string, object> AsMap(object value)
        {
            Dictionary<string, object> map = value as Dictionary<string, object>;
            if (map == null) throw new InvalidDataException("Hardware capability JSON has an unexpected structure.");
            return map;
        }

        private static Dictionary<string, object> Child(Dictionary<string, object> map, string key)
        {
            object value;
            if (!map.TryGetValue(key, out value)) throw new InvalidDataException("Hardware capability JSON is missing: " + key);
            return AsMap(value);
        }

        private static string GetString(Dictionary<string, object> map, string key)
        {
            object value;
            if (!map.TryGetValue(key, out value) || value == null) return "";
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        private static bool GetBool(Dictionary<string, object> map, string key)
        {
            object value;
            if (!map.TryGetValue(key, out value) || value == null) return false;
            if (value is bool) return (bool)value;
            bool parsed;
            if (bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed)) return parsed;
            int number;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number != 0;
        }

        private static int GetInt(Dictionary<string, object> map, string key, int fallback)
        {
            object value;
            if (!map.TryGetValue(key, out value) || value == null) return fallback;
            int parsed;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static string ParseFfmpegVersion(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return "";
            Match token = Regex.Match(line, @"^ffmpeg version\s+([^\s]+)", RegexOptions.IgnoreCase);
            if (token.Success) return ShortFfmpegVersionToken(token.Groups[1].Value);
            return FirstToken(line);
        }

        internal static string ShortFfmpegVersionToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            Match git = Regex.Match(value, @"^(\d{4}-\d{2}-\d{2}-git)(?:-|$)", RegexOptions.IgnoreCase);
            return git.Success ? git.Groups[1].Value : value;
        }

        internal static string ShortFfmpegVersionLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return line;
            Match match = Regex.Match(line, @"^(ff(?:mpeg|probe) version\s+)([^\s]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value + ShortFfmpegVersionToken(match.Groups[2].Value) : line.Trim();
        }

        private static string FirstToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string trimmed = value.Trim();
            int space = trimmed.IndexOfAny(new char[] { ' ', '\t', '\r', '\n' });
            return space > 0 ? trimmed.Substring(0, space) : trimmed;
        }

        private static void TryTaskKillTree(int pid)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "taskkill.exe";
                psi.Arguments = "/PID " + pid.ToString(CultureInfo.InvariantCulture) + " /T /F";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process killer = Process.Start(psi))
                {
                    if (killer != null) killer.WaitForExit(5000);
                }
            }
            catch { }
        }

        private void RaiseCompleted(HardwareCapabilitySnapshot snapshot)
        {
            EventHandler<HardwareCapabilityCompletedEventArgs> handler = Completed;
            if (handler != null) handler(this, new HardwareCapabilityCompletedEventArgs(snapshot));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Cancel();
        }
    }
}

namespace FilmGrainStudioPreview
{
    internal static class FrucCapabilityDetection
    {
        internal static string Classify(string reason)
        {
            return SharedFrucCore.FailureKind(reason);
        }

        internal static void Detect(HardwareCapabilityRequest request, HardwareCapabilitySnapshot snapshot, SharedFrucCore.ProbeRunner runner = null)
        {
            if (runner == null) runner = SharedFrucCore.Run;
            SharedFrucCore.InvalidatePreflightCache();
            snapshot.FrucFfmpegPath = request.FrucFfmpegPath ?? "";
            snapshot.FrucPerf = request.FrucPerf ?? "medium";
            snapshot.FrucGrid = request.FrucGrid ?? "auto";
            snapshot.FrucReady = true;
            snapshot.FrucAvailable = false;
            snapshot.FrucFailureKind = "FFMPEG";
            string reason;
            try
            {
                if (!File.Exists(snapshot.FrucFfmpegPath)) { snapshot.FrucReason = "[dependency missing] FRUC FFmpeg executable: " + snapshot.FrucFfmpegPath; return; }
                SharedFrucCore.CapabilityReport report;
                snapshot.FrucAvailable = SharedFrucCore.TryInspect(snapshot.FrucFfmpegPath, snapshot.FrucPerf, snapshot.FrucGrid, out report, out reason, runner);
                snapshot.FrucFailureKind = snapshot.FrucAvailable ? "" : Classify(reason);
                snapshot.FrucReason = snapshot.FrucAvailable ? report.Summary(snapshot.FrucPerf, snapshot.FrucGrid) : reason;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { snapshot.FrucFailureKind = "RUNTIME"; snapshot.FrucReason = ex.Message; }
        }
    }
}

namespace FilmGrainStudioPreview
{
internal static class FilmGrainNvencCaps
{
    // NVENC API 13.0 is sufficient for Split Frame Encoding capability queries.
    private const uint NvencApiVersion = 13u;
    private const uint FunctionListVersion = 0x7002000Du;
    private const uint OpenSessionVersion = 0x7001000Du;
    private const uint CapsParamVersion = 0x7001000Du;

    // NV_ENC_CAPS_NUM_ENCODER_ENGINES
    private const int NumEncoderEnginesCap = 49;

    private static readonly Guid Av1Guid =
        new Guid("0a352289-0aa7-4759-862d-5d15cd16d254");

    [System.Runtime.InteropServices.DllImport("nvcuda.dll", EntryPoint = "cuInit",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Winapi)]
    private static extern int CuInit(uint flags);

    [System.Runtime.InteropServices.DllImport("nvcuda.dll", EntryPoint = "cuDeviceGet",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Winapi)]
    private static extern int CuDeviceGet(out int device, int ordinal);

    [System.Runtime.InteropServices.DllImport("nvcuda.dll", EntryPoint = "cuCtxCreate_v2",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Winapi)]
    private static extern int CuCtxCreate(out IntPtr context, uint flags, int device);

    [System.Runtime.InteropServices.DllImport("nvcuda.dll", EntryPoint = "cuCtxDestroy_v2",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.Winapi)]
    private static extern int CuCtxDestroy(IntPtr context);

    [System.Runtime.InteropServices.DllImport("nvEncodeAPI64.dll", EntryPoint = "NvEncodeAPIGetMaxSupportedVersion",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.StdCall)]
    private static extern int NvEncodeAPIGetMaxSupportedVersion(out uint version);

    [System.Runtime.InteropServices.DllImport("nvEncodeAPI64.dll", EntryPoint = "NvEncodeAPICreateInstance",
        CallingConvention = System.Runtime.InteropServices.CallingConvention.StdCall)]
    private static extern int NvEncodeAPICreateInstance(IntPtr functionList);

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate int GetEncodeCapsDelegate(
        IntPtr encoder, Guid encodeGuid, IntPtr capsParam, out int capsValue);

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate int DestroyEncoderDelegate(IntPtr encoder);

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate int OpenEncodeSessionExDelegate(
        IntPtr openParams, out IntPtr encoder);

    private static void ZeroMemory(IntPtr ptr, int size)
    {
        byte[] zero = new byte[size];
        System.Runtime.InteropServices.Marshal.Copy(zero, 0, ptr, size);
    }

    public static int GetAv1EncoderEngineCount(int gpuIndex)
    {
        if (IntPtr.Size != 8) return 1;
        IntPtr functionList = IntPtr.Zero;
        IntPtr openParams = IntPtr.Zero;
        IntPtr capsParam = IntPtr.Zero;
        IntPtr cudaContext = IntPtr.Zero;
        IntPtr encoder = IntPtr.Zero;
        DestroyEncoderDelegate destroyEncoder = null;

        try
        {
            uint maxSupported;
            if (NvEncodeAPIGetMaxSupportedVersion(out maxSupported) != 0)
                return 1;

            // NvEncodeAPIGetMaxSupportedVersion encodes major in the upper
            // bits and minor in the low 4 bits: 13.0 == 0xD0.
            if (maxSupported < (13u << 4))
                return 1;

            if (CuInit(0) != 0)
                return 1;

            int cudaDevice;
            if (CuDeviceGet(out cudaDevice, gpuIndex) != 0)
                return 1;

            if (CuCtxCreate(out cudaContext, 0, cudaDevice) != 0 ||
                cudaContext == IntPtr.Zero)
                return 1;

            // NV_ENCODE_API_FUNCTION_LIST starts with two uint32 values,
            // followed by function pointers. We only read three known ABI
            // positions from the official API 13.x function list.
            functionList = System.Runtime.InteropServices.Marshal.AllocHGlobal(4096);
            ZeroMemory(functionList, 4096);
            System.Runtime.InteropServices.Marshal.WriteInt32(functionList, 0, unchecked((int)FunctionListVersion));

            if (NvEncodeAPICreateInstance(functionList) != 0)
                return 1;

            int pointerSize = IntPtr.Size;
            IntPtr getCapsPtr = System.Runtime.InteropServices.Marshal.ReadIntPtr(functionList, 8 + (7 * pointerSize));
            IntPtr destroyPtr = System.Runtime.InteropServices.Marshal.ReadIntPtr(functionList, 8 + (27 * pointerSize));
            IntPtr openSessionPtr = System.Runtime.InteropServices.Marshal.ReadIntPtr(functionList, 8 + (29 * pointerSize));

            if (getCapsPtr == IntPtr.Zero ||
                destroyPtr == IntPtr.Zero ||
                openSessionPtr == IntPtr.Zero)
                return 1;

            GetEncodeCapsDelegate getCaps =
                (GetEncodeCapsDelegate)System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer(
                    getCapsPtr, typeof(GetEncodeCapsDelegate));
            destroyEncoder =
                (DestroyEncoderDelegate)System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer(
                    destroyPtr, typeof(DestroyEncoderDelegate));
            OpenEncodeSessionExDelegate openSession =
                (OpenEncodeSessionExDelegate)System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer(
                    openSessionPtr, typeof(OpenEncodeSessionExDelegate));

            // NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS on Win64:
            // version, deviceType, device, reserved, apiVersion,
            // reserved1[253], reserved2[64] = 1552 bytes.
            openParams = System.Runtime.InteropServices.Marshal.AllocHGlobal(1552);
            ZeroMemory(openParams, 1552);
            System.Runtime.InteropServices.Marshal.WriteInt32(openParams, 0, unchecked((int)OpenSessionVersion));
            System.Runtime.InteropServices.Marshal.WriteInt32(openParams, 4, 1); // NV_ENC_DEVICE_TYPE_CUDA
            System.Runtime.InteropServices.Marshal.WriteIntPtr(openParams, 8, cudaContext);
            System.Runtime.InteropServices.Marshal.WriteInt32(openParams, 24, unchecked((int)NvencApiVersion));

            if (openSession(openParams, out encoder) != 0 ||
                encoder == IntPtr.Zero)
                return 1;

            // NV_ENC_CAPS_PARAM = version + capsToQuery + reserved[62].
            capsParam = System.Runtime.InteropServices.Marshal.AllocHGlobal(256);
            ZeroMemory(capsParam, 256);
            System.Runtime.InteropServices.Marshal.WriteInt32(capsParam, 0, unchecked((int)CapsParamVersion));
            System.Runtime.InteropServices.Marshal.WriteInt32(capsParam, 4, NumEncoderEnginesCap);

            int engines;
            if (getCaps(encoder, Av1Guid, capsParam, out engines) != 0)
                return 1;

            if (engines < 1)
                return 1;

            // Current FFmpeg exposes explicit split modes up to four engines.
            if (engines > 4)
                engines = 4;

            return engines;
        }
        catch
        {
            return 1;
        }
        finally
        {
            if (encoder != IntPtr.Zero && destroyEncoder != null)
            {
                try { destroyEncoder(encoder); } catch {}
            }

            if (capsParam != IntPtr.Zero)
                System.Runtime.InteropServices.Marshal.FreeHGlobal(capsParam);
            if (openParams != IntPtr.Zero)
                System.Runtime.InteropServices.Marshal.FreeHGlobal(openParams);
            if (functionList != IntPtr.Zero)
                System.Runtime.InteropServices.Marshal.FreeHGlobal(functionList);

            if (cudaContext != IntPtr.Zero)
            {
                try { CuCtxDestroy(cudaContext); } catch {}
            }
        }
    }
}
}

namespace FilmGrainStudioPreview
{
    internal sealed class NativeProbeResult
    {
        internal bool Success;
        internal string Output = "";
        internal string Reason = "";
    }
    internal delegate NativeProbeResult NativeProbeRunner(string tool, IList<string> args, int timeout, string appData);

    internal sealed class NativeHardwareProbe
    {
        private readonly HardwareCapabilityRequest request;
        private readonly NativeProbeRunner runner;
        private readonly StringBuilder trace = new StringBuilder("Native .NET capability detection");
        private NativeHardwareProbe(HardwareCapabilityRequest request, NativeProbeRunner runner) { this.request=request; this.runner=runner; }
        private NativeProbeResult Run(string tool, IList<string> args, string appData = null, int timeout = 20000)
        {
            NativeProbeResult result = runner(tool,args,timeout,appData);
            trace.Append("\r\n").Append(Path.GetFileName(tool)).Append(' ').Append(SharedFrucCore.JoinArguments(args))
                .Append(" => ").Append(result.Success ? "PASS" : "FAIL " + Short(result.Reason));
            return result;
        }
        private static string Short(string s) { s=s??""; return s.Length>400?s.Substring(0,400):s; }
        private static Dictionary<string,object> Map(params object[] pairs)
        { var m=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)m[(string)pairs[i]]=pairs[i+1];return m; }
        private static string Tag(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "missing";
            var f=new FileInfo(path); return f.Exists ? f.FullName+"|"+f.Length+"|"+f.LastWriteTimeUtc.Ticks : path+"|missing";
        }
        private static string Hash(string text)
        { using(var sha=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
        private static string NullOutput { get { return Environment.OSVersion.Platform == PlatformID.Win32NT ? "NUL" : "/dev/null"; } }
        private List<string> EncoderBase(string encoder, string tune = "hq")
        {
            var a=new List<string>{"-hide_banner","-loglevel","error","-f","lavfi","-i","testsrc2=size=640x360:rate=30",
                "-frames:v","8","-an","-c:v",encoder,"-gpu","0","-preset","p5","-tune",tune,"-rc","vbr","-b:v","1M","-maxrate:v","2M","-bufsize:v","4M"};
            if(encoder=="av1_nvenc")a.AddRange(new string[]{"-pix_fmt","p010le","-highbitdepth","1"});
            else if(encoder=="hevc_nvenc")a.AddRange(new string[]{"-pix_fmt","p010le","-profile:v","main10"});
            else a.AddRange(new string[]{"-pix_fmt","yuv420p","-profile:v","high"});return a;
        }
        private bool FF(IList<string> a) { return Run(request.FfmpegPath,a).Success; }
        private bool EncoderTest(string encoder, string tune, params string[] extra)
        { var a=EncoderBase(encoder,tune);a.AddRange(extra);a.AddRange(new string[]{"-f","null",NullOutput});return FF(a); }
        private bool Option(string encoder, params string[] extra) { return EncoderTest(encoder,"hq",extra); }
        private Dictionary<string,object> Encoder(string encoder)
        {
            var caps=Map("available",false,"uhq",false,"bFrames",false,"bReference",false,"spatialAQ",false,"temporalAQ",false,
                "lookahead",false,"adaptiveB",false,"sceneCut",false,"multipassQres",false,"multipassFullres",false,"splitEncodeMaxEngines",1);
            bool available=Option(encoder);caps["available"]=available;if(!available)return caps;
            if(encoder=="av1_nvenc")
            {
                caps["uhq"]=EncoderTest(encoder,"uhq","-preset","p4","-multipass","fullres","-spatial-aq","1","-aq-strength","8");
                int engines=1;
                if(File.Exists(request.NvencQueryPath))
                {
                    var query=Run(request.NvencQueryPath,new string[]{"--hardware-nvenc-engines"},null,15000);
                    Match m=Regex.Match(query.Output??"",@"FGS_NVENC_ENGINES=([1-4])(?:\s|$)");
                    if(query.Success&&m.Success)engines=int.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture);
                }
                if(engines>=2&&FF(new string[]{"-hide_banner","-loglevel","error","-f","lavfi","-i","testsrc2=size=3840x2160:rate=30",
                    "-frames:v","4","-an","-c:v","av1_nvenc","-gpu","0","-pix_fmt","p010le","-highbitdepth","1","-preset","p6",
                    "-tune","hq","-rc","vbr","-b:v","12M","-maxrate:v","36M","-bufsize:v","72M","-multipass","fullres",
                    "-split_encode_mode",engines.ToString(CultureInfo.InvariantCulture),"-f","null",NullOutput}))caps["splitEncodeMaxEngines"]=engines;
            }
            if(Option(encoder,"-bf","4","-b_ref_mode","middle","-spatial-aq","1","-aq-strength","8","-temporal-aq","1","-rc-lookahead","32","-multipass","fullres"))
            {
                foreach(string key in new string[]{"bFrames","bReference","spatialAQ","temporalAQ","lookahead","multipassQres","multipassFullres"})caps[key]=true;
            }
            else
            {
                caps["bFrames"]=Option(encoder,"-bf","4");
                if((bool)caps["bFrames"])caps["bReference"]=Option(encoder,"-bf","4","-b_ref_mode","middle");
                caps["spatialAQ"]=Option(encoder,"-spatial-aq","1","-aq-strength","8");
                caps["temporalAQ"]=Option(encoder,"-temporal-aq","1");caps["lookahead"]=Option(encoder,"-rc-lookahead","32");
                caps["multipassQres"]=Option(encoder,"-multipass","qres");caps["multipassFullres"]=Option(encoder,"-multipass","fullres");
            }
            if((bool)caps["lookahead"]){caps["adaptiveB"]=Option(encoder,"-rc-lookahead","32","-b_adapt","1");caps["sceneCut"]=Option(encoder,"-rc-lookahead","32","-no-scenecut","0");}
            return caps;
        }
        private Dictionary<string,object> X264()
        {
            var caps=Map("available",false,"tuneGrain",false,"high10",false,"errorDiffusionDither",false,"bStrategy2",false);
            var baseArgs=new List<string>{"-hide_banner","-loglevel","error","-f","lavfi","-i","testsrc2=size=320x180:rate=30",
                "-frames:v","2","-an","-c:v","libx264","-preset","veryfast"};
            Func<string[],bool> check=delegate(string[] extra){var a=new List<string>(baseArgs);a.AddRange(extra);a.AddRange(new string[]{"-f","null",NullOutput});return FF(a);};
            caps["available"]=check(new string[]{"-pix_fmt","yuv420p","-profile:v","high"});if(!(bool)caps["available"])return caps;
            caps["tuneGrain"]=check(new string[]{"-tune","grain","-pix_fmt","yuv420p","-profile:v","high"});
            caps["bStrategy2"]=check(new string[]{"-tune","grain","-b_strategy","2","-pix_fmt","yuv420p","-profile:v","high"});
            caps["high10"]=FF(new string[]{"-hide_banner","-loglevel","error","-f","lavfi","-i","testsrc2=size=320x180:rate=30",
                "-frames:v","2","-an","-vf","format=yuv420p10le","-c:v","libx264","-preset","veryfast","-tune","grain",
                "-pix_fmt","yuv420p10le","-profile:v","high10","-f","null",NullOutput});
            caps["errorDiffusionDither"]=FF(new string[]{"-hide_banner","-loglevel","error","-f","lavfi","-i","testsrc2=size=320x180:rate=30",
                "-frames:v","1","-an","-vf","format=yuv420p10le,zscale=dither=error_diffusion,format=yuv420p","-f","null",NullOutput});return caps;
        }
        private static string FindSmi()
        {
            var paths=new List<string>();foreach(string dir in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator))
                if(!string.IsNullOrWhiteSpace(dir))try{paths.Add(Path.Combine(dir.Trim('"'),"nvidia-smi.exe"));}catch{}
            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"nvidia-smi.exe"));
            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"NVIDIA Corporation","NVSMI","nvidia-smi.exe"));
            foreach(string path in paths)if(File.Exists(path))return path;return "";
        }
        private Dictionary<string,object> Gpu()
        {
            var gpu=Map("index",0,"name","NVIDIA GPU","pciBusId","","driverVersion","","identity","unknown");
            string smi=string.IsNullOrEmpty(request.NvidiaSmiPath)?FindSmi():request.NvidiaSmiPath;
            if(File.Exists(smi))
            {
                var result=Run(smi,new string[]{"--query-gpu=index,name,pci.bus_id,driver_version","--format=csv,noheader,nounits"});
                if(result.Success)foreach(string line in result.Output.Split(new char[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries))
                {string[] parts=Regex.Split(line.Trim(),@"\s*,\s*");if(parts.Length>=4&&parts[0]=="0")
                    {gpu["name"]=parts[1];gpu["pciBusId"]=parts[2];gpu["driverVersion"]=parts[3];gpu["identity"]=string.Join("|",parts);return gpu;}}
            }
            // Isolate WMI as well: a stopped provider must not block the detector indefinitely.
            if(File.Exists(request.NvencQueryPath))
            {
                var identity=Run(request.NvencQueryPath,new string[]{"--hardware-gpu-identity"},null,15000);
                if(identity.Success)try {
                    var map=new JavaScriptSerializer().DeserializeObject(identity.Output.Trim()) as Dictionary<string,object>;
                    if(map!=null&&map.ContainsKey("name")&&map.ContainsKey("driverVersion")&&map.ContainsKey("identity"))return map;
                }catch { }
            }
            trace.Append("\r\nGPU identity unavailable; persistent cache reuse disabled.");
            return gpu;
        }
        internal static Dictionary<string,object> WmiGpuIdentity()
        {
            var gpu=Map("index",0,"name","NVIDIA GPU","pciBusId","","driverVersion","","identity","unknown");
            try
            {
                Type type=Type.GetType("System.Management.ManagementObjectSearcher, System.Management, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",true);
                using(IDisposable search=(IDisposable)Activator.CreateInstance(type,new object[]{"SELECT Name, DriverVersion FROM Win32_VideoController"}))
                using(IDisposable collection=(IDisposable)type.GetMethod("Get",Type.EmptyTypes).Invoke(search,null))
                foreach(object row in (System.Collections.IEnumerable)collection)
                {
                    using(IDisposable item=(IDisposable)row)
                    {
                        var property=row.GetType().GetProperty("Item",new Type[]{typeof(string)});
                        string name=Convert.ToString(property.GetValue(row,new object[]{"Name"}),CultureInfo.InvariantCulture);
                        if(name.IndexOf("NVIDIA",StringComparison.OrdinalIgnoreCase)<0)continue;
                        string driver=Convert.ToString(property.GetValue(row,new object[]{"DriverVersion"}),CultureInfo.InvariantCulture);
                        gpu["name"]=name;gpu["driverVersion"]=driver;gpu["identity"]="0|"+name+"|"+driver;return gpu;
                    }
                }
            }catch{ }
            return gpu;
        }
        private Dictionary<string,object> Grav()
        {
            var grav=Map("path",request.Grav1synthPath??"","available",false,"version","未检测","sfeCompatible",false);
            if(string.IsNullOrWhiteSpace(request.Grav1synthPath))return grav;
            if(!File.Exists(request.Grav1synthPath)){grav["version"]="未找到";return grav;}
            var result=Run(request.Grav1synthPath,new string[]{"--version"},null,15000);
            if(!result.Success){grav["version"]="检测失败";return grav;}grav["available"]=true;
            Match m=Regex.Match(result.Output,@"(\d+)\.(\d+)\.(\d+)");if(!m.Success){grav["version"]="未知";return grav;}
            grav["version"]=m.Value;Version version;if(Version.TryParse(m.Value,out version))grav["sfeCompatible"]=version>=new Version(0,2,2);return grav;
        }
        private Dictionary<string,object> Svp(string root)
        {
            var caps=Map("runtimeReady",false,"cpu",false,"gpu",false);
            string pipe=Path.Combine(root,".venv","Scripts","vspipe.exe"),check=Path.Combine(root,"Check_OpenSVPFlow.vpy"),plugins=Path.Combine(root,"Plugins");
            if(!File.Exists(pipe)||!File.Exists(check)||!File.Exists(Path.Combine(plugins,"svpflow1_vs.dll"))||!File.Exists(Path.Combine(plugins,"svpflow2_vs.dll")))return caps;
            caps["runtimeReady"]=true;string config=Path.Combine(root,"_UserConfig");
            string appData=File.Exists(Path.Combine(config,"vapoursynth","vapoursynth.toml"))?config:null;
            foreach(string gpu in new string[]{"0","1"})caps[gpu=="0"?"cpu":"gpu"]=Run(pipe,new string[]{"--arg","plugin_dir="+plugins,"--arg","gpu="+gpu,"--arg","algo=13","--start","0","--end","0",check,NullOutput},appData).Success;
            return caps;
        }
        private Dictionary<string,object> DetectProfile(string version, Dictionary<string,object> gpu, Dictionary<string,object> grav, string signature, string svpRoot)
        {
            var av1=Encoder("av1_nvenc");var hevc=Encoder("hevc_nvenc");var h264=Encoder("h264_nvenc");var x264=X264();
            bool vulkan=FF(new string[]{"-hide_banner","-loglevel","error","-init_hw_device","vulkan=vk:0","-filter_hw_device","vk",
                "-f","lavfi","-i","color=c=black:s=128x128:r=30","-vf","format=p010le,hwupload,scale_vulkan=w=64:h=64,hwdownload,format=p010le",
                "-frames:v","1","-f","null",NullOutput});
            bool grain=FF(new string[]{"-hide_banner","-loglevel","error","-init_hw_device","vulkan=vk:0","-filter_hw_device","vk",
                "-f","lavfi","-i","color=c=black:s=128x128:r=30","-f","lavfi","-i","color=c=gray:s=128x128:r=30",
                "-filter_complex","[0:v]format=p010le,hwupload[a];[1:v]format=p010le,hwupload[b];[a][b]blend_vulkan=all_mode=overlay:all_opacity=0.5,hwdownload,format=p010le",
                "-frames:v","1","-f","null",NullOutput});
            bool nvdec=false;string file=Path.Combine(Path.GetTempPath(),"FilmGrain_NVDEC_NET_"+Guid.NewGuid().ToString("N")+".mp4");
            try{if((bool)h264["available"]){var args=EncoderBase("h264_nvenc");args.AddRange(new string[]{"-movflags","+faststart","-y",file});
                if(FF(args))nvdec=FF(new string[]{"-hide_banner","-loglevel","error","-hwaccel","cuda","-hwaccel_device","0","-i",file,"-map","0:v:0","-frames:v","1","-f","null",NullOutput});}}
            finally{try{if(File.Exists(file))File.Delete(file);}catch{}}
            var info=new FileInfo(request.FfmpegPath);
            return Map("schemaVersion",9,"detector","Native.NET.H8R4WN","signature",signature,"generatedAt",DateTime.UtcNow.ToString("o"),"cacheState","Detected",
                "ffmpeg",Map("path",info.FullName,"version",version,"size",info.Length,"lastWriteTimeUtc",info.LastWriteTimeUtc.ToString("o")),"gpu",gpu,"grav1synth",grav,
                "caps",Map("av1",av1,"hevc",hevc,"h264",h264,"x264",x264,"nvdec",nvdec,"vulkan",vulkan,"vulkanGrain",grain,
                    "hevcPipeline",(bool)hevc["available"]&&vulkan,"x264Pipeline",(bool)x264["available"]&&(bool)x264["tuneGrain"]&&vulkan&&grain,"openSvp",Svp(svpRoot)));
        }
        private HardwareCapabilitySnapshot Detect()
        {
            if(!File.Exists(request.FfmpegPath))throw new FileNotFoundException("FFmpeg was not found.",request.FfmpegPath);
            var versionProbe=Run(request.FfmpegPath,new string[]{"-version"});if(!versionProbe.Success)throw new InvalidOperationException("FFmpeg version probe failed: "+versionProbe.Reason);
            string version="";
            foreach(string line in (versionProbe.Output??"").Split(new char[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries))
                if(line.StartsWith("ffmpeg version ",StringComparison.OrdinalIgnoreCase)){version=line;break;}
            if(version.Length==0)throw new InvalidOperationException("FFmpeg version probe returned no version line.");
            var gpu=Gpu();var grav=Grav();string svp=Path.Combine(request.AppRoot,"_OpenSVPFlow");
            var signature=new StringBuilder("schema=9|Native.NET.H8R4WN|gpu=0|cuda=0|vulkan=0|").Append(Tag(request.FfmpegPath)).Append('|').Append(version)
                .Append('|').Append(gpu["identity"]).Append('|').Append(Tag(request.Grav1synthPath)).Append('|').Append(grav["version"]).Append('|').Append(Tag(request.NvencQueryPath));
            foreach(string rel in new string[]{".venv/Scripts/vspipe.exe","Plugins/svpflow1_vs.dll","Plugins/svpflow2_vs.dll","Check_OpenSVPFlow.vpy",".venv/pyvenv.cfg","_UserConfig/vapoursynth/vapoursynth.toml"})signature.Append('|').Append(Tag(Path.Combine(svp,rel)));
            string hash=Hash(signature.ToString()),cache=Path.Combine(request.AppRoot,"Utils","_HardwareCaps_NET.json");
            var serializer=new JavaScriptSerializer();Dictionary<string,object> profile=null;
            if(!request.ForceDetection && (string)gpu["identity"]!="unknown" && File.Exists(cache))
            {
                try{var saved=serializer.DeserializeObject(File.ReadAllText(cache,Encoding.UTF8)) as Dictionary<string,object>;
                    if(saved!=null&&Convert.ToInt32(saved["schemaVersion"],CultureInfo.InvariantCulture)==9&&(string)saved["signature"]==hash)
                    {HardwareCapabilityCore.ParseProfile(saved);profile=saved;profile["cacheState"]="Cached";trace.Append("\r\nNative cache signature matched; FRUC remains independently rechecked.");}}
                catch(Exception ex){trace.Append("\r\nIgnoring invalid native cache: ").Append(Short(ex.Message));}
            }
            if(profile==null)profile=DetectProfile(version,gpu,grav,hash,svp);
            HardwareCapabilitySnapshot result=HardwareCapabilityCore.ParseProfile(profile);result.Ready=true;
            string temp=cache+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{Directory.CreateDirectory(Path.GetDirectoryName(cache));File.WriteAllText(temp,serializer.Serialize(profile)+"\r\n",new UTF8Encoding(false));
                if(File.Exists(cache))File.Replace(temp,cache,null);else File.Move(temp,cache);}
            catch(Exception ex){trace.Append("\r\nNative cache write skipped: ").Append(Short(ex.Message));}
            finally{try{if(File.Exists(temp))File.Delete(temp);}catch{}}
            result.Diagnostics=trace.ToString();return result;
        }
        internal static HardwareCapabilitySnapshot Detect(HardwareCapabilityRequest request, NativeProbeRunner runner)
        { return new NativeHardwareProbe(request,runner).Detect(); }
    }
}
