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
    }

    internal sealed class HardwareCapabilitySnapshot
    {
        public bool Ready { get; set; }
        public string Error { get; set; }
        public string CacheState { get; set; }
        public string FfmpegVersion { get; set; }
        public string GpuName { get; set; }
        public string DriverVersion { get; set; }
        public bool Av1Available { get; set; }
        public bool Av1UhqAvailable { get; set; }
        public int Av1SplitEncodeMaxEngines { get; set; }
        public bool Grav1synthSfeCompatible { get; set; }
        public string Grav1synthVersion { get; set; }
        public bool HevcAvailable { get; set; }
        public bool X264Available { get; set; }
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
            HardwareCapabilitySnapshot snapshot = new HardwareCapabilitySnapshot();
            snapshot.Av1SplitEncodeMaxEngines = 1;
            Process process = null;
            try
            {
                string script = Path.Combine(request.AppRoot, "Utils", "FilmGrain_Hardware_Caps.ps1");
                string cache = Path.Combine(request.AppRoot, "Utils", "_HardwareCaps.json");
                string openSvpRoot = Path.Combine(request.AppRoot, "_OpenSVPFlow");
                if (!File.Exists(script)) throw new FileNotFoundException("Hardware capability script was not found.", script);
                if (!File.Exists(request.FfmpegPath)) throw new FileNotFoundException("FFmpeg was not found.", request.FfmpegPath);

                string powerShell = GetWindowsPowerShellPath();
                if (!File.Exists(powerShell)) throw new FileNotFoundException("Windows PowerShell was not found.", powerShell);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = powerShell;
                psi.Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -File " + QuoteArg(script) +
                    " -FFmpeg " + QuoteArg(request.FfmpegPath) +
                    " -GpuIndex 0 -CudaDevice 0 -VulkanDevice 0" +
                    " -CachePath " + QuoteArg(cache) +
                    " -OpenSvpRoot " + QuoteArg(openSvpRoot) +
                    " -Grav1synth " + QuoteArg(request.Grav1synthPath ?? "") +
                    " -Quiet";
                psi.WorkingDirectory = request.AppRoot;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                StringBuilder stderr = new StringBuilder();
                object errorLock = new object();
                process = new Process();
                process.StartInfo = psi;
                process.OutputDataReceived += delegate { };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (errorLock)
                    {
                        if (stderr.Length < 12000) stderr.AppendLine(e.Data);
                    }
                };

                lock (sync)
                {
                    if (disposed) throw new ObjectDisposedException("HardwareCapabilityCore");
                    activeProcess = process;
                }

                if (!process.Start()) throw new InvalidOperationException("Hardware capability detection could not start.");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (cancelRequested) Cancel();
                process.WaitForExit();

                string errorText;
                lock (errorLock) { errorText = stderr.ToString().Trim(); }
                if (cancelRequested)
                {
                    snapshot.Error = "Hardware capability detection was cancelled.";
                }
                else if (process.ExitCode != 0)
                {
                    snapshot.Error = errorText.Length > 0 ? errorText : "Hardware capability detection exited with code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + ".";
                }
                else
                {
                    snapshot = ParseCache(cache);
                    snapshot.Ready = true;
                }
            }
            catch (Exception ex)
            {
                snapshot.Ready = false;
                snapshot.Error = ex.Message;
            }
            finally
            {
                lock (sync)
                {
                    if (object.ReferenceEquals(activeProcess, process)) activeProcess = null;
                    workerThread = null;
                }
                if (process != null) process.Dispose();
                if (!cancelRequested) RaiseCompleted(snapshot);
            }
        }

        private static HardwareCapabilitySnapshot ParseCache(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Hardware capability cache was not generated.", path);
            string json = File.ReadAllText(path, Encoding.UTF8);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            object rootObject = serializer.DeserializeObject(json);
            Dictionary<string, object> root = AsMap(rootObject);
            Dictionary<string, object> ffmpeg = Child(root, "ffmpeg");
            Dictionary<string, object> gpu = Child(root, "gpu");
            Dictionary<string, object> grav = Child(root, "grav1synth");
            Dictionary<string, object> caps = Child(root, "caps");
            Dictionary<string, object> av1 = Child(caps, "av1");
            Dictionary<string, object> x264 = Child(caps, "x264");
            Dictionary<string, object> svp = Child(caps, "openSvp");

            HardwareCapabilitySnapshot result = new HardwareCapabilitySnapshot();
            result.CacheState = GetString(root, "cacheState");
            result.FfmpegVersion = ParseFfmpegVersion(GetString(ffmpeg, "version"));
            result.GpuName = GetString(gpu, "name");
            result.DriverVersion = GetString(gpu, "driverVersion");
            result.Av1Available = GetBool(av1, "available");
            result.Av1UhqAvailable = GetBool(av1, "uhq");
            result.Av1SplitEncodeMaxEngines = Math.Max(1, GetInt(av1, "splitEncodeMaxEngines", 1));
            result.Grav1synthSfeCompatible = GetBool(grav, "sfeCompatible");
            result.Grav1synthVersion = GetString(grav, "version");
            result.HevcAvailable = GetBool(caps, "hevcPipeline");
            result.X264Available = GetBool(caps, "x264Pipeline");
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
            Match numeric = Regex.Match(line, @"^ffmpeg version\s+([0-9]+(?:\.[0-9]+){1,3})", RegexOptions.IgnoreCase);
            if (numeric.Success) return numeric.Groups[1].Value;
            Match token = Regex.Match(line, @"^ffmpeg version\s+([^\s]+)", RegexOptions.IgnoreCase);
            if (token.Success) return token.Groups[1].Value;
            return FirstToken(line);
        }

        private static string FirstToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string trimmed = value.Trim();
            int space = trimmed.IndexOfAny(new char[] { ' ', '\t', '\r', '\n' });
            return space > 0 ? trimmed.Substring(0, space) : trimmed;
        }

        private static string GetWindowsPowerShellPath()
        {
            string windows = Environment.GetEnvironmentVariable("WINDIR") ?? "";
            if (string.IsNullOrWhiteSpace(windows)) windows = @"C:\Windows";
            return Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        }

        private static string QuoteArg(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
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
