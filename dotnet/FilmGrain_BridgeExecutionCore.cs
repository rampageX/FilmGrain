using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace FilmGrainStudioPreview
{
    internal enum BridgeLogStream
    {
        StandardOutput,
        StandardError,
        System
    }

    internal sealed class BridgeLogLineEventArgs : EventArgs
    {
        public BridgeLogStream Stream { get; private set; }
        public string Line { get; private set; }

        public BridgeLogLineEventArgs(BridgeLogStream stream, string line)
        {
            Stream = stream;
            Line = line ?? "";
        }
    }

    internal sealed class BridgeProgressEventArgs : EventArgs
    {
        public bool HasProgress { get; private set; }
        public int ProgressPermille { get; private set; }
        public bool HasMetrics { get; private set; }
        public string FpsText { get; private set; }
        public string SpeedText { get; private set; }
        public string EtaText { get; private set; }
        public bool HasStage { get; private set; }
        public int StageCurrent { get; private set; }
        public int StageTotal { get; private set; }
        public string StageText { get; private set; }

        public BridgeProgressEventArgs(bool hasProgress, int progressPermille, bool hasMetrics, string fpsText, string speedText, string etaText, bool hasStage, int stageCurrent, int stageTotal, string stageText)
        {
            HasProgress = hasProgress;
            ProgressPermille = progressPermille;
            HasMetrics = hasMetrics;
            FpsText = fpsText ?? "";
            SpeedText = speedText ?? "";
            EtaText = etaText ?? "";
            HasStage = hasStage;
            StageCurrent = stageCurrent;
            StageTotal = stageTotal;
            StageText = stageText ?? "";
        }
    }

    internal sealed class BridgeExecutionRequest
    {
        public string ToolPath { get; set; }
        public string WorkingDirectory { get; set; }
        public IList<string> InputFiles { get; set; }
        public IDictionary<string, string> Environment { get; set; }
    }

    internal sealed class BridgeExecutionResult
    {
        public int ExitCode { get; private set; }
        public bool Cancelled { get; private set; }
        public Exception Exception { get; private set; }

        public BridgeExecutionResult(int exitCode, bool cancelled, Exception exception)
        {
            ExitCode = exitCode;
            Cancelled = cancelled;
            Exception = exception;
        }
    }

    internal sealed class BridgeExecutionCompletedEventArgs : EventArgs
    {
        public BridgeExecutionResult Result { get; private set; }

        public BridgeExecutionCompletedEventArgs(BridgeExecutionResult result)
        {
            Result = result;
        }
    }

    internal sealed class BridgeExecutionCore : IDisposable
    {
        private readonly object sync = new object();
        private readonly object progressSync = new object();
        private Process activeProcess;
        private Thread workerThread;
        private volatile bool cancelRequested;
        private bool disposed;
        private double currentDurationSeconds;
        private bool currentDurationLocked;
        private string progressFpsText = "";
        private string progressOutTimeText = "";
        private string progressSpeedText = "";
        private int currentStage;
        private int currentStageTotal;

        public event EventHandler<BridgeLogLineEventArgs> LogLine;
        public event EventHandler<BridgeProgressEventArgs> ProgressChanged;
        public event EventHandler<BridgeExecutionCompletedEventArgs> Completed;

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

        public void Start(BridgeExecutionRequest request)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (string.IsNullOrWhiteSpace(request.ToolPath)) throw new ArgumentException("ToolPath is required.", "request");
            if (!File.Exists(request.ToolPath)) throw new FileNotFoundException("Bridge execution tool was not found.", request.ToolPath);
            if (request.InputFiles == null || request.InputFiles.Count == 0) throw new ArgumentException("At least one input file is required.", "request");

            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("BridgeExecutionCore");
                if (activeProcess != null || (workerThread != null && workerThread.IsAlive)) throw new InvalidOperationException("A Bridge process is already running.");
                cancelRequested = false;
                ResetProgressState();
                workerThread = new Thread(new ThreadStart(delegate { RunWorker(request); }));
                workerThread.IsBackground = true;
                workerThread.Name = "FGS Bridge Execution";
                workerThread.Start();
            }
        }

        public void Cancel()
        {
            cancelRequested = true;
            Process process = null;
            lock (sync)
            {
                process = activeProcess;
            }
            if (process == null) return;

            int pid = 0;
            try { pid = process.Id; }
            catch { }

            if (pid > 0)
            {
                RaiseLog(BridgeLogStream.System, "cancel requested; terminating process tree PID " + pid.ToString(CultureInfo.InvariantCulture));
                ThreadPool.QueueUserWorkItem(delegate
                {
                    TryTaskKillTree(pid);
                    try
                    {
                        if (!process.HasExited) process.Kill();
                    }
                    catch { }
                });
                return;
            }

            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch { }
        }

        private void RunWorker(BridgeExecutionRequest request)
        {
            int exitCode = -1;
            Exception failure = null;
            Process process = null;
            DateTime runStartedUtc = DateTime.UtcNow;
            try
            {
                ProcessStartInfo psi = BuildStartInfo(request);
                process = new Process();
                process.StartInfo = psi;
                process.EnableRaisingEvents = false;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        RaiseLog(BridgeLogStream.StandardOutput, e.Data);
                        HandleProcessLine(e.Data);
                    }
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        RaiseLog(BridgeLogStream.StandardError, e.Data);
                        HandleProcessLine(e.Data);
                    }
                };

                lock (sync)
                {
                    if (disposed) throw new ObjectDisposedException("BridgeExecutionCore");
                    activeProcess = process;
                }

                RaiseLog(BridgeLogStream.System, "starting: " + request.ToolPath);
                if (!process.Start()) throw new InvalidOperationException("Process.Start returned false.");
                RaiseLog(BridgeLogStream.System, "PID " + process.Id.ToString(CultureInfo.InvariantCulture));
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (cancelRequested) Cancel();

                process.WaitForExit();
                exitCode = process.ExitCode;
            }
            catch (Exception ex)
            {
                failure = ex;
                RaiseLog(BridgeLogStream.System, "exception: " + ex.Message);
            }
            finally
            {
                if (process != null)
                {
                    try { process.CancelOutputRead(); } catch { }
                    try { process.CancelErrorRead(); } catch { }
                }

                if (cancelRequested) CleanupCancelledX264TempFiles(request, runStartedUtc);

                lock (sync)
                {
                    if (object.ReferenceEquals(activeProcess, process)) activeProcess = null;
                    workerThread = null;
                }

                if (process != null) process.Dispose();
                RaiseCompleted(new BridgeExecutionResult(exitCode, cancelRequested, failure));
            }
        }

        private void ResetProgressState()
        {
            lock (progressSync)
            {
                currentDurationSeconds = 0.0;
                currentDurationLocked = false;
                progressFpsText = "";
                progressOutTimeText = "";
                progressSpeedText = "";
                currentStage = 0;
                currentStageTotal = 0;
            }
        }

        private void HandleProcessLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;

            BridgeProgressEventArgs eventArgs = null;
            lock (progressSync)
            {
                Match inputMatch = Regex.Match(line, @"Input\s*:\s*""([^""]+)""");
                if (!inputMatch.Success)
                    inputMatch = Regex.Match(line, @"^\s*\[\d+\]\s*""([^""]+)""\s*$");
                if (inputMatch.Success)
                {
                    currentDurationSeconds = 0.0;
                    currentDurationLocked = false;
                    progressFpsText = "";
                    progressOutTimeText = "";
                    progressSpeedText = "";
                    currentStage = 0;
                    currentStageTotal = 0;
                }

                Match durationMatch = Regex.Match(line, @"Duration\s*:\s*([0-9.]+)\s*sec", RegexOptions.IgnoreCase);
                if (durationMatch.Success)
                {
                    double durationValue;
                    if (double.TryParse(durationMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out durationValue) && durationValue > 0.0)
                    {
                        currentDurationSeconds = durationValue;
                        currentDurationLocked = true;
                    }
                }
                else if (!currentDurationLocked)
                {
                    Match ffmpegDuration = Regex.Match(line, @"Duration:\s*([0-9]{2}):([0-9]{2}):([0-9]{2}(?:\.[0-9]+)?)", RegexOptions.IgnoreCase);
                    if (ffmpegDuration.Success)
                    {
                        double durationValue;
                        if (TryParseClock(ffmpegDuration.Groups[1].Value, ffmpegDuration.Groups[2].Value, ffmpegDuration.Groups[3].Value, out durationValue) && durationValue > 0.0)
                            currentDurationSeconds = durationValue;
                    }
                }

                Match stageMatch = Regex.Match(line, @"\[(\d+)/(\d+)\]\s*(.+)$");
                if (!stageMatch.Success)
                    stageMatch = Regex.Match(line, @"x264\s+step\s+(\d+)/(\d+)\s*[-:]\s*(.+)$", RegexOptions.IgnoreCase);
                if (stageMatch.Success)
                {
                    int stageCurrent;
                    int stageTotal;
                    if (int.TryParse(stageMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out stageCurrent) &&
                        int.TryParse(stageMatch.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out stageTotal) && stageTotal > 0)
                    {
                        currentStage = Math.Max(1, Math.Min(stageTotal, stageCurrent));
                        currentStageTotal = stageTotal;
                        int stagePermille = Math.Max(0, Math.Min(1000, ((currentStage - 1) * 1000) / currentStageTotal));
                        eventArgs = new BridgeProgressEventArgs(true, stagePermille, false, "", "", "", true, currentStage, currentStageTotal, stageMatch.Groups[3].Value.Trim());
                    }
                }

                int equalsIndex = line.IndexOf('=');
                if (equalsIndex > 0)
                {
                    string key = line.Substring(0, equalsIndex).Trim();
                    string value = line.Substring(equalsIndex + 1).Trim();
                    if (string.Equals(key, "fps", StringComparison.OrdinalIgnoreCase)) progressFpsText = value;
                    else if (string.Equals(key, "out_time", StringComparison.OrdinalIgnoreCase)) progressOutTimeText = value;
                    else if (string.Equals(key, "speed", StringComparison.OrdinalIgnoreCase)) progressSpeedText = value.EndsWith("x", StringComparison.OrdinalIgnoreCase) ? value.Substring(0, value.Length - 1).Trim() : value;
                    else if (string.Equals(key, "progress", StringComparison.OrdinalIgnoreCase))
                    {
                        BridgeProgressEventArgs metricEvent = BuildMetricProgressEventNoLock();
                        if (metricEvent != null) eventArgs = metricEvent;
                    }
                }

                Match statsMatch = Regex.Match(line, @"fps=\s*([0-9.]+).*?time=\s*([0-9]{2}):([0-9]{2}):([0-9]{2}(?:\.[0-9]+)?).*?speed=\s*([0-9.]+)x", RegexOptions.IgnoreCase);
                if (statsMatch.Success)
                {
                    double elapsed;
                    if (TryParseClock(statsMatch.Groups[2].Value, statsMatch.Groups[3].Value, statsMatch.Groups[4].Value, out elapsed))
                        eventArgs = BuildMetricProgressEventNoLock(statsMatch.Groups[1].Value, statsMatch.Groups[5].Value, elapsed);
                }
            }

            if (eventArgs != null) RaiseProgress(eventArgs);
        }

        private BridgeProgressEventArgs BuildMetricProgressEventNoLock()
        {
            double elapsed;
            if (string.IsNullOrWhiteSpace(progressFpsText) || string.IsNullOrWhiteSpace(progressOutTimeText) || string.IsNullOrWhiteSpace(progressSpeedText)) return null;
            if (!TryParseClock(progressOutTimeText, out elapsed)) return null;
            return BuildMetricProgressEventNoLock(progressFpsText, progressSpeedText, elapsed);
        }

        private BridgeProgressEventArgs BuildMetricProgressEventNoLock(string fpsText, string speedText, double elapsedSeconds)
        {
            double speedValue;
            if (!double.TryParse(speedText, NumberStyles.Float, CultureInfo.InvariantCulture, out speedValue)) return null;

            bool hasProgress = currentDurationSeconds > 0.0;
            int permille = 0;
            string etaText = "";
            if (hasProgress)
            {
                double ratio = Math.Max(0.0, Math.Min(1.0, elapsedSeconds / currentDurationSeconds));
                double overallRatio = ratio;
                if (currentStageTotal > 0 && currentStage > 0)
                    overallRatio = ((currentStage - 1) + ratio) / currentStageTotal;
                permille = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, overallRatio)) * 1000.0, MidpointRounding.AwayFromZero);
                if (speedValue > 0.0)
                {
                    double etaSeconds = Math.Max(0.0, (currentDurationSeconds - elapsedSeconds) / speedValue);
                    etaText = FormatElapsed(etaSeconds);
                }
            }
            return new BridgeProgressEventArgs(hasProgress, permille, true, fpsText, speedText, etaText, false, 0, 0, "");
        }

        private static bool TryParseClock(string value, out double seconds)
        {
            seconds = 0.0;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string[] parts = value.Trim().Split(':');
            if (parts.Length != 3) return false;
            return TryParseClock(parts[0], parts[1], parts[2], out seconds);
        }

        private static bool TryParseClock(string hoursText, string minutesText, string secondsText, out double seconds)
        {
            seconds = 0.0;
            int hours;
            int minutes;
            double secondsPart;
            if (!int.TryParse(hoursText, NumberStyles.Integer, CultureInfo.InvariantCulture, out hours)) return false;
            if (!int.TryParse(minutesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes)) return false;
            if (!double.TryParse(secondsText, NumberStyles.Float, CultureInfo.InvariantCulture, out secondsPart)) return false;
            seconds = (hours * 3600.0) + (minutes * 60.0) + secondsPart;
            return true;
        }

        private static string FormatElapsed(double totalSeconds)
        {
            if (totalSeconds < 0.0) totalSeconds = 0.0;
            long wholeSeconds = (long)Math.Floor(totalSeconds);
            long hours = wholeSeconds / 3600;
            long minutes = (wholeSeconds % 3600) / 60;
            long seconds = wholeSeconds % 60;
            return hours.ToString("00", CultureInfo.InvariantCulture) + ":" + minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        private void CleanupCancelledX264TempFiles(BridgeExecutionRequest request, DateTime runStartedUtc)
        {
            try
            {
                HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string tempMode = GetRequestEnvironment(request, "FG_TEMP_MODE");
                string customTemp = GetRequestEnvironment(request, "FG_TEMP_CUSTOM_DIR");

                if (string.Equals(tempMode, "SYSTEM", StringComparison.OrdinalIgnoreCase))
                {
                    roots.Add(Path.Combine(Path.GetTempPath(), "FilmGrain_Studio"));
                }
                else if (string.Equals(tempMode, "CUSTOM", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(customTemp))
                {
                    roots.Add(Path.Combine(customTemp, "FilmGrain_Studio"));
                }
                else if (request.InputFiles != null)
                {
                    foreach (string input in request.InputFiles)
                    {
                        if (string.IsNullOrWhiteSpace(input)) continue;
                        string directory = Path.GetDirectoryName(input);
                        if (!string.IsNullOrWhiteSpace(directory)) roots.Add(directory);
                    }
                }

                DateTime earliestWriteUtc = runStartedUtc.AddSeconds(-2.0);
                HashSet<string> removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    foreach (string root in roots)
                    {
                        if (!Directory.Exists(root)) continue;
                        CleanupCancelledX264Pattern(root, "__FGS_X264_*.log", earliestWriteUtc, removed);
                        CleanupCancelledX264Pattern(root, "__FGS_X264_*.log.mbtree", earliestWriteUtc, removed);
                        CleanupCancelledX264Pattern(root, "__FGS_X264_*.mbtree", earliestWriteUtc, removed);
                        CleanupCancelledX264Pattern(root, "__FGS_X264_*.log.temp", earliestWriteUtc, removed);
                        CleanupCancelledX264Pattern(root, "__FGS_X264_*.log.mbtree.temp", earliestWriteUtc, removed);
                        CleanupCancelledX264Pattern(root, "__FGS_X264_*.mbtree.temp", earliestWriteUtc, removed);
                        CleanupCancelledX264Pattern(root, "__FGS_UPLOAD_X264_*.log", earliestWriteUtc, removed);
                        CleanupCancelledX264Pattern(root, "__FGS_UPLOAD_X264_*.log.mbtree", earliestWriteUtc, removed);
                        CleanupCancelledX264Pattern(root, "__FGS_UPLOAD_X264_*.mbtree", earliestWriteUtc, removed);
                        CleanupCancelledX264Pattern(root, "__FGS_UPLOAD_X264_*.log.temp", earliestWriteUtc, removed);
                        CleanupCancelledX264Pattern(root, "__FGS_UPLOAD_X264_*.log.mbtree.temp", earliestWriteUtc, removed);
                        CleanupCancelledX264Pattern(root, "__FGS_UPLOAD_X264_*.mbtree.temp", earliestWriteUtc, removed);
                    }
                    if (attempt < 4) Thread.Sleep(120);
                }

                if (removed.Count > 0)
                    RaiseLog(BridgeLogStream.System, "cancel cleanup: removed " + removed.Count.ToString(CultureInfo.InvariantCulture) + " x264 passlog artifact(s)");
            }
            catch (Exception ex)
            {
                RaiseLog(BridgeLogStream.System, "cancel cleanup warning: " + ex.Message);
            }
        }

        private static string GetRequestEnvironment(BridgeExecutionRequest request, string key)
        {
            if (request == null || request.Environment == null || string.IsNullOrEmpty(key)) return "";
            string value;
            return request.Environment.TryGetValue(key, out value) ? (value ?? "") : "";
        }

        private static void CleanupCancelledX264Pattern(string root, string pattern, DateTime earliestWriteUtc, HashSet<string> removed)
        {
            string[] files;
            try { files = Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly); }
            catch { return; }

            foreach (string file in files)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < earliestWriteUtc) continue;
                    File.Delete(file);
                    removed.Add(file);
                }
                catch { }
            }
        }

        private static ProcessStartInfo BuildStartInfo(BridgeExecutionRequest request)
        {
            string comSpec = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(comSpec))
                comSpec = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = comSpec;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = false;
            psi.WorkingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory) ? Path.GetDirectoryName(request.ToolPath) : request.WorkingDirectory;

            if (request.Environment != null)
            {
                foreach (KeyValuePair<string, string> pair in request.Environment)
                    SetEnvironmentVariable(psi.EnvironmentVariables, pair.Key, pair.Value ?? "");
            }

            const string toolVariable = "FG_NET_TOOL";
            SetEnvironmentVariable(psi.EnvironmentVariables, toolVariable, request.ToolPath);

            string command = "\"%" + toolVariable + "%\"";
            for (int i = 0; i < request.InputFiles.Count; i++)
            {
                string variable = "FG_NET_INPUT_" + (i + 1).ToString("000", CultureInfo.InvariantCulture);
                SetEnvironmentVariable(psi.EnvironmentVariables, variable, request.InputFiles[i] ?? "");
                command += " \"%" + variable + "%\"";
            }

            // /S /C requires one outer quote pair around a command whose executable is quoted.
            // File paths themselves never appear literally in the command string; they arrive through
            // environment expansion, which avoids injecting raw CMD metacharacters into ProcessStartInfo.Arguments.
            psi.Arguments = "/D /Q /V:OFF /S /C \"" + command + "\"";
            return psi;
        }

        private static void SetEnvironmentVariable(StringDictionary environment, string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (environment.ContainsKey(key)) environment[key] = value;
            else environment.Add(key, value);
        }

        private static void TryTaskKillTree(int pid)
        {
            try
            {
                string taskKill = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = taskKill;
                psi.Arguments = "/PID " + pid.ToString(CultureInfo.InvariantCulture) + " /T /F";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process killer = Process.Start(psi))
                {
                    if (killer != null) killer.WaitForExit(5000);
                }
            }
            catch { }
        }

        private void RaiseLog(BridgeLogStream stream, string line)
        {
            EventHandler<BridgeLogLineEventArgs> handler = LogLine;
            if (handler != null) handler(this, new BridgeLogLineEventArgs(stream, line));
        }

        private void RaiseProgress(BridgeProgressEventArgs e)
        {
            EventHandler<BridgeProgressEventArgs> handler = ProgressChanged;
            if (handler != null) handler(this, e);
        }

        private void RaiseCompleted(BridgeExecutionResult result)
        {
            EventHandler<BridgeExecutionCompletedEventArgs> handler = Completed;
            if (handler != null) handler(this, new BridgeExecutionCompletedEventArgs(result));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Cancel();
        }
    }
}
