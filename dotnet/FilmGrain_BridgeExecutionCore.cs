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
        public Func<string> BeforeRunPreparation { get; set; }
        internal Action PreparedObserver { get; set; }
        internal Func<Func<bool>, string> PrepareWithCancellation { get; set; }
        public Func<bool> ShouldRun { get; set; }
        public IList<string> FrucArguments { get; set; }
        public string FrucProbePath { get; set; }
        public string FrucTimelineReferenceInput { get; set; }
        public string PipeProducerPath { get; set; }
        public string PipeProducerArguments { get; set; }
        internal IList<string> OriginalArguments { get; set; }
        internal bool OrdinaryTimelineBound { get; set; }
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
        internal MediaProbeIdentity SourceIdentity;
        internal string SourceConfigRoot;
        internal TimeSpan PreparationElapsed;
        internal void ValidateSource()
        {
            if(SourceIdentity==null)return;
            var cfg=new FgsConfig(SourceConfigRoot,null);cfg.Load();
            if(!SourceIdentity.IsCurrent(SourceIdentity.Input,Path.Combine(cfg.Get("FFMPEG_DIR"),"ffprobe.exe")))
                throw new InvalidOperationException("Input or FFprobe changed; reselect and probe again: "+SourceIdentity.Input);
        }
        internal bool PixelGrainHdr { get; set; }
        internal IList<string> PixelGrainEncodeArguments { get; set; }
        internal string PixelGrainFullFilter { get; set; }
        internal string PixelGrainCleanFilter { get; set; }
        public string ExecutionNotice { get; set; }
        internal Func<Func<bool>, BridgeExecutionRequest> ContinueAfterStages { get; set; }
        internal IList<string> TemporaryDirectories { get; set; }
        public bool LogStageTimings { get; set; }
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
        public Action PostRunCommit { get; set; }
        public double ProgressDurationSeconds { get; set; }
    }

    internal sealed class BridgeExecutionResult
    {
        public int ExitCode { get; private set; }
        public bool Cancelled { get; private set; }
        public TimeSpan TotalElapsed { get; internal set; }
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

    internal static class NativeProcessPolicy
    {
        internal static void ValidateTool(string path)
        {
            if(string.IsNullOrWhiteSpace(path))throw new InvalidOperationException("Native execution tool path is empty.");
            string name=Path.GetFileName(path.Replace('\\','/'));
            if(!string.Equals(Path.GetExtension(name),".exe",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(".NET execution requires a Native executable; script tool rejected: "+path);
            foreach(string host in new[]{"cmd.exe","powershell.exe","powershell_ise.exe","pwsh.exe","wscript.exe","cscript.exe"})
                if(string.Equals(name,host,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(".NET execution does not start a script host: "+path);
        }
        internal static void ValidateRequest(BridgeExecutionRequest request)
        {
            if(request==null)throw new ArgumentNullException("request");
            if(!request.DirectProcess)throw new InvalidOperationException(".NET execution requires DirectProcess; Legacy requests are disabled.");
            ValidateTool(request.ToolPath);
            if(request.DirectStages!=null)foreach(var stage in request.DirectStages)
            {
                if(stage==null)throw new InvalidOperationException("Native stage is missing.");
                ValidateTool(stage.ToolPath);
                if(!string.IsNullOrWhiteSpace(stage.PipeProducerPath))ValidateTool(stage.PipeProducerPath);
                if(!string.IsNullOrWhiteSpace(stage.FrucProbePath))ValidateTool(stage.FrucProbePath);
            }
        }
    }

    internal sealed class BridgeExecutionCore : IDisposable
    {
        private readonly object sync = new object();
        private readonly object progressSync = new object();
        private Process activeProcess;
        private Process activePipeProducer;
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
            NativeProcessPolicy.ValidateRequest(request);
            request.ValidateSource();
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
            // The execution worker observes this flag while polling process exit.
            // Never wait for a GPU process or process-tree termination on the UI thread.
        }

        private void StopProcessTree(Process process)
        {
            if (process == null) return;
            try
            {
                if (process.HasExited) return;
                int pid = process.Id;
                RaiseLog(BridgeLogStream.System, "[Cancel] stopping process tree PID " + pid);
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    // vspipe.exe in a Python environment may be a launcher. Killing only
                    // the launcher leaves its child holding the redirected pipe open.
                    using (var killer = new Process())
                    {
                        killer.StartInfo = new ProcessStartInfo {
                            FileName=Path.Combine(Environment.SystemDirectory,"taskkill.exe"),
                            Arguments="/PID " + pid.ToString(CultureInfo.InvariantCulture) + " /T /F",
                            UseShellExecute=false, CreateNoWindow=true
                        };
                        if (killer.Start() && !killer.WaitForExit(5000))
                            try { killer.Kill(); } catch { }
                    }
                }
                if (!process.HasExited) process.Kill();
            }
            catch (Exception ex)
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
                RaiseLog(BridgeLogStream.System, "[Cancel] process stop: " + ex.Message);
            }
        }

        private void WaitForNativeExit(Process process, Process producer)
        {
            while (!process.WaitForExit(100))
            {
                if (!cancelRequested) continue;
                StopProcessTree(producer);
                StopProcessTree(process);
                if (!process.WaitForExit(5000))
                    throw new IOException("Cancelled process did not exit within 5 seconds.");
                return;
            }
        }

        private void DrainProcessOutput(Func<bool> closed)
        {
            var timer=Stopwatch.StartNew();
            while (!closed())
            {
                if (cancelRequested) return;
                if (timer.ElapsedMilliseconds > 5000)
                    throw new IOException("Process exited but redirected log streams did not close within 5 seconds.");
                Thread.Sleep(25);
            }
        }

        private void RunWorker(BridgeExecutionRequest request)
        {
            var totalTimer=Stopwatch.StartNew();
            TimeSpan preparationElapsed=request.PreparationElapsed;
            int exitCode = -1;
            Exception failure = null;
            List<BridgeExecutionRequest> previewOwners = new List<BridgeExecutionRequest>();
            var timelineScope=SharedInputTimelineCore.BeginScope();
            try
            {
            runPreparedRequest:
                request.ValidateSource();
                if (!string.IsNullOrEmpty(request.ExecutionNotice)) RaiseLog(BridgeLogStream.System,request.ExecutionNotice);
                if (request.DirectStages != null && request.DirectStages.Count > 0)
                {
                    foreach (BridgeExecutionStage stage in request.DirectStages)
                    {
                        if (cancelRequested) break;
                        request.ValidateSource();
                        if (stage.PrepareWithCancellation != null)
                        {
                            RaiseLog(BridgeLogStream.System, stage.PrepareWithCancellation(delegate { return cancelRequested; }));
                            if (cancelRequested) break;
                        }
                        if (stage.BeforeRunPreparation != null)
                        {
                            RaiseProgress(new BridgeProgressEventArgs(false, 0, false, "", "", "", true,
                                stage.StageCurrent, stage.StageTotal, stage.StageText + " / shared timeline preparation"));
                            RaiseLog(BridgeLogStream.System, stage.BeforeRunPreparation());
                            if (cancelRequested) break;
                        }
                        if (stage.ShouldRun != null && !stage.ShouldRun()) continue;
                        Stopwatch stageTimer = request.LogStageTimings ? Stopwatch.StartNew() : null;
                        if (stage.FrucArguments != null)
                        {
                            RaiseProgress(new BridgeProgressEventArgs(false, 0, false, "", "", "", true,
                                stage.StageCurrent, stage.StageTotal, "FRUC shared timestamp preparation"));
                            RaiseLog(BridgeLogStream.System, "[FRUC] preparing shared input timeline");
                            RaiseLog(BridgeLogStream.System, SharedFrucCore.PrepareStage(stage));
                            if (cancelRequested) break;
                        }
                        if(stage.PreparedObserver!=null)stage.PreparedObserver();
                        if(cancelRequested)break;
                        PrepareStageProgress(stage);
                        string stageMode;
                        string stagePrefix = request.Environment != null && request.Environment.TryGetValue("FG_MODE", out stageMode) ? stageMode + " step " : "Native step ";
                        RaiseLog(BridgeLogStream.System, stagePrefix + stage.StageCurrent.ToString(CultureInfo.InvariantCulture) + "/" + stage.StageTotal.ToString(CultureInfo.InvariantCulture) + " - " + (stage.StageText ?? ""));
                        request.ValidateSource();
                        exitCode = string.IsNullOrEmpty(stage.PipeProducerPath)
                            ? RunProcess(BuildStartInfo(request, stage), stage.ToolPath, out failure)
                            : RunPipedProcess(request, stage, out failure);
                        if (stageTimer != null) RaiseLog(BridgeLogStream.System, (request.Environment != null && request.Environment.ContainsKey("FG_MODE") && request.Environment["FG_MODE"] == "AV1BAKE" ? "[AV1 Bake]" : "[AV1 Copy]") + " stage_seconds=" + stageTimer.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " stage=" + stage.StageText);
                        if (failure != null || exitCode != 0 || cancelRequested) break;
                    }
                    if (cancelRequested && exitCode == 0) exitCode = -1;
                }
                else
                {
                    exitCode = RunProcess(BuildStartInfo(request, null), request.ToolPath, out failure);
                }
                if (!cancelRequested && exitCode == 0 && failure == null && request.ContinueAfterStages != null)
                {
                    request.ValidateSource();
                    previewOwners.Add(request);
                    BridgeExecutionRequest next = request.ContinueAfterStages(delegate { return cancelRequested; });
                    if (next == null) throw new InvalidOperationException("Preview preparation returned no request");
                    request = next;
                    NativeProcessPolicy.ValidateRequest(request);
                    lock (sync) { activeRequest = request; }
                    ResetProgressState(request.ProgressDurationSeconds);
                    if (!cancelRequested) goto runPreparedRequest;
                }
                if (!cancelRequested && exitCode == 0 && failure == null)
                    ValidateOutputFiles(request);
                if (!cancelRequested && exitCode == 0 && failure == null && request.PostRunValidation != null)
                {
                    RaiseProgress(new BridgeProgressEventArgs(false, 0, false, "", "", "", true,
                        currentStage > 0 ? currentStage : 1, currentStageTotal > 0 ? currentStageTotal : 1, "Post-run validation"));
                    string validationError = request.PostRunValidation();
                    if (!string.IsNullOrEmpty(validationError)) throw new InvalidDataException(validationError);
                    RaiseLog(BridgeLogStream.System, "Post-run validation passed");
                }
                if (!cancelRequested && exitCode == 0 && failure == null && request.PostRunCommit != null)
                {
                    request.PostRunCommit();
                    ValidateOutputFiles(request);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                RaiseLog(BridgeLogStream.System, "exception: " + ex.Message);
            }
            finally
            {
                RaiseLog(BridgeLogStream.System,timelineScope.Summary);
                timelineScope.Dispose();
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
                foreach (BridgeExecutionRequest owner in previewOwners) CleanupTemporaryFiles(owner);

                lock (sync)
                {
                    activeProcess = null;
                    activeRequest = null;
                    workerThread = null;
                }
                totalTimer.Stop();
                RaiseCompleted(new BridgeExecutionResult(exitCode, cancelRequested, failure) { TotalElapsed=preparationElapsed+totalTimer.Elapsed });
            }
        }

        private int RunProcess(ProcessStartInfo psi, string toolPath, out Exception failure)
        {
            failure = null;
            Process process = null;
            int exitCode = -1;
            int outputClosed=0, errorClosed=0;
            try
            {
                process = new Process();
                process.StartInfo = psi;
                process.EnableRaisingEvents = false;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) Interlocked.Exchange(ref outputClosed,1);
                    else
                    {
                        RaiseLog(BridgeLogStream.StandardOutput, e.Data);
                        HandleProcessLine(e.Data);
                    }
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) Interlocked.Exchange(ref errorClosed,1);
                    else
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

                RaiseLog(BridgeLogStream.System, "[Process] cwd=" + psi.WorkingDirectory);
                RaiseLog(BridgeLogStream.System, "[Process] args=" + psi.Arguments);
                RaiseLog(BridgeLogStream.System, "starting: " + toolPath);
                if (!process.Start()) throw new InvalidOperationException("Process.Start returned false.");
                RaiseLog(BridgeLogStream.System, "PID " + process.Id.ToString(CultureInfo.InvariantCulture));
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                WaitForNativeExit(process,null);
                DrainProcessOutput(delegate { return Thread.VolatileRead(ref outputClosed)!=0 && Thread.VolatileRead(ref errorClosed)!=0; });
                exitCode = process.ExitCode;
                RaiseLog(BridgeLogStream.System, "[Process] exit=" + exitCode.ToString(CultureInfo.InvariantCulture));
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
                    StopProcessTree(process);
                    try { process.WaitForExit(5000); } catch { }
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
            int producerErrorClosed=0,encoderOutputClosed=0,encoderErrorClosed=0;
            try
            {
                NativeProcessPolicy.ValidateTool(stage.PipeProducerPath);
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
                { if (e.Data == null) Interlocked.Exchange(ref producerErrorClosed,1); else RaiseLog(BridgeLogStream.StandardError, "[vspipe] " + e.Data); };
                encoder.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                { if (e.Data == null) Interlocked.Exchange(ref encoderOutputClosed,1); else { RaiseLog(BridgeLogStream.StandardOutput, e.Data); HandleProcessLine(e.Data); } };
                encoder.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                { if (e.Data == null) Interlocked.Exchange(ref encoderErrorClosed,1); else { RaiseLog(BridgeLogStream.StandardError, e.Data); HandleProcessLine(e.Data); } };
                lock (sync) { activePipeProducer = producer; activeProcess = encoder; }
                RaiseLog(BridgeLogStream.System, "[Process] producer_cwd=" + sourceInfo.WorkingDirectory + " args=" + sourceInfo.Arguments);
                RaiseLog(BridgeLogStream.System, "[Process] encoder_cwd=" + encoder.StartInfo.WorkingDirectory + " args=" + encoder.StartInfo.Arguments);
                RaiseLog(BridgeLogStream.System, "starting pipe: " + sourceInfo.FileName + " -> " + encoder.StartInfo.FileName);
                if (!encoder.Start()) throw new InvalidOperationException("FFmpeg did not start");
                encoder.BeginOutputReadLine(); encoder.BeginErrorReadLine();
                if (!producer.Start()) throw new InvalidOperationException("vspipe did not start");
                producer.BeginErrorReadLine();
                Process pipeSource = producer, pipeTarget = encoder;
                copyThread = new Thread(delegate()
                {
                    try { pipeSource.StandardOutput.BaseStream.CopyTo(pipeTarget.StandardInput.BaseStream); }
                    catch (Exception ex) { copyFailure = ex; }
                    finally { try { pipeTarget.StandardInput.Close(); } catch { } }
                });
                copyThread.IsBackground = true; copyThread.Start();
                WaitForNativeExit(encoder,producer);
                if (encoder.ExitCode != 0 || cancelRequested) StopProcessTree(producer);
                WaitForNativeExit(producer,null);
                if (!copyThread.Join(5000)) throw new IOException("vspipe copy thread did not stop within 5 seconds.");
                DrainProcessOutput(delegate { return Thread.VolatileRead(ref producerErrorClosed)!=0 && Thread.VolatileRead(ref encoderOutputClosed)!=0 && Thread.VolatileRead(ref encoderErrorClosed)!=0; });
                if (!cancelRequested && encoder.ExitCode == 0 && producer.ExitCode != 0)
                    throw new InvalidDataException("vspipe exited with code " + producer.ExitCode.ToString(CultureInfo.InvariantCulture));
                if (!cancelRequested && encoder.ExitCode == 0 && copyFailure != null)
                    throw new IOException("vspipe stream failed", copyFailure);
                RaiseLog(BridgeLogStream.System, "[Process] encoder_exit=" + encoder.ExitCode + " producer_exit=" + producer.ExitCode);
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
                StopProcessTree(producer);
                StopProcessTree(encoder);
                if (producer != null) { try { producer.WaitForExit(5000); } catch { } try { producer.CancelErrorRead(); } catch { } }
                if (encoder != null) { try { encoder.WaitForExit(5000); } catch { } try { encoder.CancelOutputRead(); } catch { } try { encoder.CancelErrorRead(); } catch { } }
                if (copyThread != null && copyThread.IsAlive && !copyThread.Join(5000))
                    RaiseLog(BridgeLogStream.System,"[Cancel] pipe copy still blocked after process termination");
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

        private static void ValidateOutputFiles(BridgeExecutionRequest request)
        {
            foreach (string output in request.OutputFiles ?? new string[0])
                if (string.IsNullOrWhiteSpace(output) || !File.Exists(output) || new FileInfo(output).Length == 0)
                    throw new InvalidDataException("Native output is missing or empty: " + output);
        }

        internal static void CleanupTemporaryFiles(BridgeExecutionRequest request)
        {
            if (request == null) return;
            foreach (string tempFile in request.TemporaryFiles ?? new string[0])
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(tempFile) && File.Exists(tempFile)) File.Delete(tempFile);
                }
                catch { }
            }
            if (request.TemporaryDirectories != null) foreach (string directory in request.TemporaryDirectories)
                try { if (Directory.Exists(directory)) Directory.Delete(directory, false); } catch { }
        }

        private static ProcessStartInfo BuildStartInfo(BridgeExecutionRequest request, BridgeExecutionStage stage)
        {
            // Recheck late-prepared stages immediately before Process.Start.
            NativeProcessPolicy.ValidateRequest(request);
            string toolPath = stage == null ? request.ToolPath : stage.ToolPath;
            string arguments = stage == null ? request.DirectArguments : stage.DirectArguments;
            string workingDirectory = stage == null ? request.WorkingDirectory : stage.WorkingDirectory;
            NativeProcessPolicy.ValidateTool(toolPath);
            ProcessStartInfo direct = new ProcessStartInfo();
            direct.FileName = toolPath;
            direct.Arguments = HardwareDecodeCore.ApplyArguments(toolPath,arguments ?? "",request.Environment);
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

        private static void SetEnvironmentVariable(StringDictionary environment, string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (environment.ContainsKey(key)) environment[key] = value;
            else environment.Add(key, value);
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
