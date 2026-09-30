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

    internal sealed class BridgeExecutionStage
    {
        public string PipeProducerPath { get; set; }
        public string PipeProducerArguments { get; set; }
        public string ToolPath { get; set; }
        public string WorkingDirectory { get; set; }
        public string DirectArguments { get; set; }
        public double ProgressDurationSeconds { get; set; }
        public int StageCurrent { get; set; }
        public int StageTotal { get; set; }
        public string StageText { get; set; }
    }

    internal sealed class BridgeExecutionRequest
    {
        public string ToolPath { get; set; }
        public string WorkingDirectory { get; set; }
        public IList<string> InputFiles { get; set; }
        public IDictionary<string, string> Environment { get; set; }
        public bool DirectProcess { get; set; }
        public string DirectArguments { get; set; }
        public IList<BridgeExecutionStage> DirectStages { get; set; }
        public IList<string> OutputFiles { get; set; }
        public IList<string> TemporaryFiles { get; set; }
        public Func<string> PostRunValidation { get; set; }
        public double ProgressDurationSeconds { get; set; }
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
        private readonly ManualResetEventSlim legacyKillFinished = new ManualResetEventSlim(true);
        private Process activeProcess;
        private Process activePipeProducer;
        private bool activeDirectRequest;
        private string pendingLegacyX264Output;
        private BridgeExecutionRequest activeRequest;
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
            if (request.DirectStages != null && request.DirectStages.Count > 0)
            {
                foreach (BridgeExecutionStage stage in request.DirectStages)
                {
                    if (stage == null || string.IsNullOrWhiteSpace(stage.ToolPath)) throw new ArgumentException("Direct stage ToolPath is required.", "request");
                    if (!File.Exists(stage.ToolPath)) throw new FileNotFoundException("Direct stage execution tool was not found.", stage.ToolPath);
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(request.ToolPath)) throw new ArgumentException("ToolPath is required.", "request");
                if (!File.Exists(request.ToolPath)) throw new FileNotFoundException("Bridge execution tool was not found.", request.ToolPath);
            }
            if (request.InputFiles == null || request.InputFiles.Count == 0) throw new ArgumentException("At least one input file is required.", "request");

            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("BridgeExecutionCore");
                if (activeProcess != null || (workerThread != null && workerThread.IsAlive)) throw new InvalidOperationException("A Bridge process is already running.");
                cancelRequested = false;
                activeRequest = request;
                pendingLegacyX264Output = null;
                legacyKillFinished.Set();
                activeDirectRequest = request.DirectProcess || (request.DirectStages != null && request.DirectStages.Count > 0);
                ResetProgressState(request.ProgressDurationSeconds);
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
            bool direct = false;
            lock (sync)
            {
                process = activeProcess;
                direct = activeDirectRequest;
                if (activePipeProducer != null)
                    try { if (!activePipeProducer.HasExited) activePipeProducer.Kill(); } catch { }
            }
            if (process == null) return;

            int pid = 0;
            try { pid = process.Id; }
            catch { }

            // Native stages start the encoder directly: wait for that process to exit
            // before RunWorker removes its registered output files.
            if (direct)
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
                return;
            }

            if (pid > 0)
            {
                legacyKillFinished.Reset();
                RaiseLog(BridgeLogStream.System, "cancel requested; terminating process tree PID " + pid.ToString(CultureInfo.InvariantCulture));
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        TryTaskKillTree(pid);
                        if (!process.HasExited) process.Kill();
                    }
                    catch { }
                    finally { legacyKillFinished.Set(); }
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
            DateTime runStartedUtc = DateTime.UtcNow;
            try
            {
                if (request.DirectStages != null && request.DirectStages.Count > 0)
                {
                    foreach (BridgeExecutionStage stage in request.DirectStages)
                    {
                        if (cancelRequested) break;
                        PrepareStageProgress(stage);
                        string stageMode;
                        string stagePrefix = request.Environment != null && request.Environment.TryGetValue("FG_MODE", out stageMode) && string.Equals(stageMode, "HEVC", StringComparison.OrdinalIgnoreCase) ? "HEVC step " : "x264 step ";
                        RaiseLog(BridgeLogStream.System, stagePrefix + stage.StageCurrent.ToString(CultureInfo.InvariantCulture) + "/" + stage.StageTotal.ToString(CultureInfo.InvariantCulture) + " - " + (stage.StageText ?? ""));
                        exitCode = string.IsNullOrEmpty(stage.PipeProducerPath)
                            ? RunProcess(BuildStartInfo(request, stage), stage.ToolPath, out failure)
                            : RunPipedProcess(request, stage, out failure);
                        if (failure != null || exitCode != 0 || cancelRequested) break;
                    }
                    if (cancelRequested && exitCode == 0) exitCode = -1;
                }
                else
                {
                    exitCode = RunProcess(BuildStartInfo(request, null), request.ToolPath, out failure);
                }
                if (!cancelRequested && exitCode == 0 && failure == null && request.PostRunValidation != null)
                {
                    string validationError = request.PostRunValidation();
                    if (!string.IsNullOrEmpty(validationError)) throw new InvalidDataException(validationError);
                    RaiseLog(BridgeLogStream.System, "HDR signaling verification passed");
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                RaiseLog(BridgeLogStream.System, "exception: " + ex.Message);
            }
            finally
            {
                if (cancelRequested && !request.DirectProcess && (request.DirectStages == null || request.DirectStages.Count == 0))
                    if (!legacyKillFinished.Wait(6500)) RaiseLog(BridgeLogStream.System, "cancel cleanup warning: process-tree termination timed out");
                if (cancelRequested) CleanupCancelledX264TempFiles(request, runStartedUtc);
                if (cancelRequested) CleanupPendingLegacyX264Output();
                bool failedRun = cancelRequested || exitCode != 0 || failure != null;
                bool keepFailedOutputs = failedRun && !cancelRequested &&
                    string.Equals(Environment.GetEnvironmentVariable("FGS_KEEP_FAILED_OUTPUTS"), "1", StringComparison.OrdinalIgnoreCase);
                if (failedRun && request.OutputFiles != null)
                {
                    if (keepFailedOutputs)
                    {
                        foreach (string outputFile in request.OutputFiles)
                        {
                            if (!string.IsNullOrWhiteSpace(outputFile) && File.Exists(outputFile))
                                RaiseLog(BridgeLogStream.System, "diagnostic output retention enabled; preserved failed output: " + outputFile);
                        }
                    }
                    else foreach (string outputFile in request.OutputFiles)
                    {
                        if (string.IsNullOrWhiteSpace(outputFile)) continue;
                        for (int attempt = 0; attempt < 20 && File.Exists(outputFile); attempt++)
                        {
                            try { File.Delete(outputFile); }
                            catch (IOException) { if (attempt < 19) Thread.Sleep(100); }
                            catch (UnauthorizedAccessException) { break; }
                            catch (Exception ex) { RaiseLog(BridgeLogStream.System, "output cleanup error: " + ex.Message); break; }
                        }
                        if (File.Exists(outputFile)) RaiseLog(BridgeLogStream.System, "output cleanup warning: could not remove " + outputFile);
                    }
                }
                CleanupTemporaryFiles(request);

                lock (sync)
                {
                    activeProcess = null;
                    activeDirectRequest = false;
                    activeRequest = null;
                    workerThread = null;
                }
                RaiseCompleted(new BridgeExecutionResult(exitCode, cancelRequested, failure));
            }
        }

        private int RunProcess(ProcessStartInfo psi, string toolPath, out Exception failure)
        {
            failure = null;
            Process process = null;
            int exitCode = -1;
            try
            {
                process = new Process();
                process.StartInfo = psi;
                process.EnableRaisingEvents = false;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        TrackLegacyX264Output(e.Data);
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

                RaiseLog(BridgeLogStream.System, "starting: " + toolPath);
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
                    lock (sync)
                    {
                        if (object.ReferenceEquals(activeProcess, process)) activeProcess = null;
                    }
                    process.Dispose();
                }
            }
            return exitCode;
        }

        // Binary Y4M stream: never use cmd.exe or line-based stdout reading here.
        private int RunPipedProcess(BridgeExecutionRequest request, BridgeExecutionStage stage, out Exception failure)
        {
            failure = null;
            Process producer = null, encoder = null;
            Exception copyFailure = null;
            Thread copyThread = null;
            try
            {
                ProcessStartInfo sourceInfo = new ProcessStartInfo();
                sourceInfo.FileName = stage.PipeProducerPath;
                sourceInfo.Arguments = stage.PipeProducerArguments;
                sourceInfo.WorkingDirectory = stage.WorkingDirectory;
                sourceInfo.UseShellExecute = false;
                sourceInfo.CreateNoWindow = true;
                sourceInfo.RedirectStandardOutput = true;
                sourceInfo.RedirectStandardError = true;
                if (request.Environment != null)
                    foreach (KeyValuePair<string,string> pair in request.Environment)
                        SetEnvironmentVariable(sourceInfo.EnvironmentVariables, pair.Key, pair.Value ?? "");
                producer = new Process(); producer.StartInfo = sourceInfo;
                encoder = new Process(); encoder.StartInfo = BuildStartInfo(request, stage);
                encoder.StartInfo.RedirectStandardInput = true;
                producer.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                { if (e.Data != null) RaiseLog(BridgeLogStream.StandardError, "[vspipe] " + e.Data); };
                encoder.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                { if (e.Data != null) { RaiseLog(BridgeLogStream.StandardOutput, e.Data); HandleProcessLine(e.Data); } };
                encoder.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                { if (e.Data != null) { RaiseLog(BridgeLogStream.StandardError, e.Data); HandleProcessLine(e.Data); } };
                lock (sync) { activePipeProducer = producer; activeProcess = encoder; }
                RaiseLog(BridgeLogStream.System, "starting pipe: " + sourceInfo.FileName + " -> " + encoder.StartInfo.FileName);
                if (!encoder.Start()) throw new InvalidOperationException("FFmpeg did not start");
                encoder.BeginOutputReadLine(); encoder.BeginErrorReadLine();
                if (!producer.Start()) throw new InvalidOperationException("vspipe did not start");
                producer.BeginErrorReadLine();
                if (cancelRequested) Cancel();
                Process pipeSource = producer, pipeTarget = encoder;
                copyThread = new Thread(delegate()
                {
                    try { pipeSource.StandardOutput.BaseStream.CopyTo(pipeTarget.StandardInput.BaseStream); }
                    catch (Exception ex) { copyFailure = ex; }
                    finally { try { pipeTarget.StandardInput.Close(); } catch { } }
                });
                copyThread.IsBackground = true; copyThread.Start();
                encoder.WaitForExit();
                if (encoder.ExitCode != 0 || cancelRequested)
                    try { if (!producer.HasExited) producer.Kill(); } catch { }
                producer.WaitForExit();
                copyThread.Join();
                if (!cancelRequested && encoder.ExitCode == 0 && producer.ExitCode != 0)
                    throw new InvalidDataException("vspipe exited with code " + producer.ExitCode.ToString(CultureInfo.InvariantCulture));
                if (!cancelRequested && encoder.ExitCode == 0 && copyFailure != null)
                    throw new IOException("vspipe stream failed", copyFailure);
                return encoder.ExitCode;
            }
            catch (Exception ex)
            {
                failure = ex;
                RaiseLog(BridgeLogStream.System, "pipe exception: " + ex.Message);
                return -1;
            }
            finally
            {
                if (producer != null) try { if (!producer.HasExited) producer.Kill(); } catch { }
                if (encoder != null) try { if (!encoder.HasExited) encoder.Kill(); } catch { }
                if (copyThread != null && copyThread.IsAlive) copyThread.Join(5000);
                if (producer != null) { try { producer.WaitForExit(); } catch { } try { producer.CancelErrorRead(); } catch { } }
                if (encoder != null) { try { encoder.WaitForExit(); } catch { } try { encoder.CancelOutputRead(); } catch { } try { encoder.CancelErrorRead(); } catch { } }
                lock (sync) { if (ReferenceEquals(activePipeProducer, producer)) activePipeProducer = null; if (ReferenceEquals(activeProcess, encoder)) activeProcess = null; }
                if (producer != null) producer.Dispose();
                if (encoder != null) encoder.Dispose();
            }
        }

        private void PrepareStageProgress(BridgeExecutionStage stage)
        {
            int stageCurrent = stage == null ? 0 : stage.StageCurrent;
            int stageTotal = stage == null ? 0 : stage.StageTotal;
            double duration = stage == null ? 0.0 : stage.ProgressDurationSeconds;
            lock (progressSync)
            {
                currentDurationSeconds = duration > 0.0 ? duration : 0.0;
                currentDurationLocked = duration > 0.0;
                progressFpsText = "";
                progressOutTimeText = "";
                progressSpeedText = "";
                currentStage = stageCurrent;
                currentStageTotal = stageTotal;
            }
            if (stageTotal > 0 && stageCurrent > 0)
            {
                int stagePermille = Math.Max(0, Math.Min(1000, ((stageCurrent - 1) * 1000) / stageTotal));
                RaiseProgress(new BridgeProgressEventArgs(true, stagePermille, false, "", "", "", true, stageCurrent, stageTotal, stage == null ? "" : (stage.StageText ?? "")));
            }
        }

        private void ResetProgressState(double requestedDurationSeconds)
        {
            lock (progressSync)
            {
                currentDurationSeconds = requestedDurationSeconds > 0.0 ? requestedDurationSeconds : 0.0;
                currentDurationLocked = requestedDurationSeconds > 0.0;
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

        private void TrackLegacyX264Output(string line)
        {
            BridgeExecutionRequest current = activeRequest;
            if (current == null || current.DirectProcess ||
                !string.Equals(GetRequestEnvironment(current, "FG_MODE"), "X264", StringComparison.OrdinalIgnoreCase)) return;
            string value = (line ?? "").Trim();
            if (value == "DONE:")
            {
                lock (sync) pendingLegacyX264Output = null;
                return;
            }
            if (!value.StartsWith("Final file", StringComparison.OrdinalIgnoreCase)) return;
            int colon = value.IndexOf(':');
            if (colon < 0) return;
            string quoted = value.Substring(colon + 1).Trim();
            if (quoted.Length < 3 || quoted[0] != '"' || quoted[quoted.Length - 1] != '"') return;
            string candidate = quoted.Substring(1, quoted.Length - 2);
            try
            {
                candidate = Path.GetFullPath(candidate);
                string extension = Path.GetExtension(candidate);
                if (!string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".mkv", StringComparison.OrdinalIgnoreCase)) return;
                // The Bridge prints this line before starting FFmpeg. Only register a
                // new output in the selected destination with an input-based x264 name.
                if (File.Exists(candidate)) return;
                string outputDir = Path.GetDirectoryName(candidate);
                bool custom = string.Equals(GetRequestEnvironment(current, "FG_OUTPUT_MODE"), "CUSTOM", StringComparison.OrdinalIgnoreCase);
                string customDir = GetRequestEnvironment(current, "FG_OUTPUT_CUSTOM_DIR");
                bool belongsToInput = false;
                foreach (string input in current.InputFiles)
                {
                    string expectedDir = custom ? customDir : Path.GetDirectoryName(input);
                    if (string.IsNullOrWhiteSpace(expectedDir) ||
                        !string.Equals(outputDir.TrimEnd('\\'), Path.GetFullPath(expectedDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                    if (Path.GetFileName(candidate).StartsWith(Path.GetFileNameWithoutExtension(input) + "_X264", StringComparison.OrdinalIgnoreCase))
                    { belongsToInput = true; break; }
                }
                if (belongsToInput) lock (sync) pendingLegacyX264Output = candidate;
            }
            catch (Exception ex) { RaiseLog(BridgeLogStream.System, "legacy output tracking warning: " + ex.Message); }
        }

        private void CleanupPendingLegacyX264Output()
        {
            string candidate;
            lock (sync) { candidate = pendingLegacyX264Output; pendingLegacyX264Output = null; }
            if (string.IsNullOrEmpty(candidate)) return;
            for (int attempt = 0; attempt < 20 && File.Exists(candidate); attempt++)
            {
                try { File.Delete(candidate); }
                catch (IOException) { if (attempt < 19) Thread.Sleep(100); }
                catch (UnauthorizedAccessException) { break; }
                catch (Exception ex) { RaiseLog(BridgeLogStream.System, "legacy output cleanup error: " + ex.Message); break; }
            }
            if (File.Exists(candidate)) RaiseLog(BridgeLogStream.System, "legacy output cleanup warning: could not remove " + candidate);
            else RaiseLog(BridgeLogStream.System, "legacy cancel cleanup: checked " + candidate);
        }

        internal static void CleanupTemporaryFiles(BridgeExecutionRequest request)
        {
            if (request == null || request.TemporaryFiles == null) return;
            foreach (string tempFile in request.TemporaryFiles)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(tempFile) && File.Exists(tempFile)) File.Delete(tempFile);
                }
                catch { }
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

        private static ProcessStartInfo BuildStartInfo(BridgeExecutionRequest request, BridgeExecutionStage stage)
        {
            if (stage != null || request.DirectProcess)
            {
                string toolPath = stage == null ? request.ToolPath : stage.ToolPath;
                string arguments = stage == null ? request.DirectArguments : stage.DirectArguments;
                string workingDirectory = stage == null ? request.WorkingDirectory : stage.WorkingDirectory;
                ProcessStartInfo direct = new ProcessStartInfo();
                direct.FileName = toolPath;
                direct.Arguments = arguments ?? "";
                direct.UseShellExecute = false;
                direct.CreateNoWindow = true;
                direct.RedirectStandardOutput = true;
                direct.RedirectStandardError = true;
                direct.RedirectStandardInput = false;
                direct.WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Path.GetDirectoryName(toolPath) : workingDirectory;
                if (request.Environment != null)
                    foreach (KeyValuePair<string, string> pair in request.Environment)
                        SetEnvironmentVariable(direct.EnvironmentVariables, pair.Key, pair.Value ?? "");
                return direct;
            }

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
