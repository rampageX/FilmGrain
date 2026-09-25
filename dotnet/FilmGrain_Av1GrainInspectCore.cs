using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace FilmGrainStudioPreview
{
    internal sealed class Av1GrainInspectResult
    {
        public string PathValue;
        public string State;
        public int ExitCode;
        public string StandardError;
        public string FailureMessage;
    }

    internal sealed class Av1GrainInspectCore : IDisposable
    {
        private readonly object sync = new object();
        private Process currentProcess;
        private int requestSerial;
        private bool disposed;

        public event Action<Av1GrainInspectResult> Completed;

        public void Start(string path, string grav1synthPath)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Input path is empty.", "path");
            if (string.IsNullOrEmpty(grav1synthPath)) throw new ArgumentException("grav1synth path is empty.", "grav1synthPath");

            int serial;
            Process oldProcess;
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("Av1GrainInspectCore");
                serial = Interlocked.Increment(ref requestSerial);
                oldProcess = currentProcess;
                currentProcess = null;
            }
            KillProcessAsync(oldProcess);

            Thread worker = new Thread(new ThreadStart(delegate { RunInspect(path, grav1synthPath, serial); }));
            worker.IsBackground = true;
            worker.Name = "FGS AV1 Grain Inspect Core";
            worker.Start();
        }

        public void Cancel()
        {
            Process process;
            lock (sync)
            {
                Interlocked.Increment(ref requestSerial);
                process = currentProcess;
                currentProcess = null;
            }
            KillProcessAsync(process);
        }

        private void RunInspect(string path, string grav1synthPath, int serial)
        {
            string tempTable = Path.Combine(Path.GetTempPath(), "FilmGrainStudio_Inspect_" + Guid.NewGuid().ToString("N") + ".txt");
            Process process = null;
            int exitCode = -1;
            string errorText = "";
            string failureMessage = "";
            string state = "";

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = grav1synthPath;
                psi.Arguments = "inspect " + QuoteArgument(path) + " -o " + QuoteArgument(tempTable) + " -y";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                process = new Process();
                process.StartInfo = psi;
                lock (sync)
                {
                    if (disposed || serial != requestSerial)
                    {
                        process.Dispose();
                        return;
                    }
                    currentProcess = process;
                }

                if (!process.Start())
                {
                    failureMessage = "Unable to start grav1synth inspect.";
                }
                else
                {
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();
                    process.WaitForExit();
                    exitCode = process.ExitCode;
                    outputTask.Wait();
                    errorTask.Wait();
                    errorText = errorTask.Result ?? "";
                    if (exitCode == 0) state = ReadTableSummary(tempTable);
                }
            }
            catch (Exception ex)
            {
                failureMessage = ex.Message;
            }
            finally
            {
                lock (sync)
                {
                    if (object.ReferenceEquals(currentProcess, process)) currentProcess = null;
                }
                DisposeProcess(process);
                try { if (File.Exists(tempTable)) File.Delete(tempTable); } catch { }
            }

            if (!IsCurrent(serial)) return;

            Av1GrainInspectResult result = new Av1GrainInspectResult();
            result.PathValue = path;
            result.State = state ?? "";
            result.ExitCode = exitCode;
            result.StandardError = errorText ?? "";
            result.FailureMessage = failureMessage ?? "";

            Action<Av1GrainInspectResult> handler = Completed;
            if (handler != null)
            {
                try { handler(result); } catch { }
            }
        }

        private static string ReadTableSummary(string tablePath)
        {
            if (string.IsNullOrEmpty(tablePath) || !File.Exists(tablePath)) return "AV1 胶片颗粒：无";
            try
            {
                string[] lines = File.ReadAllLines(tablePath, Encoding.UTF8);
                if (lines.Length == 0 || !string.Equals(lines[0].Trim(), "filmgrn1", StringComparison.Ordinal))
                    return "AV1 胶片颗粒：无法识别参数表";
                Regex chroma = new Regex(@"^s(?:Cb|Cr)\s+([1-9][0-9]*)\b", RegexOptions.IgnoreCase);
                foreach (string line in lines)
                {
                    string t = (line ?? "").Trim();
                    if (chroma.IsMatch(t)) return "AV1 胶片颗粒：亮度 + 色度";

                    // Photon ISO --chroma uses AV1 chroma_scaling_from_luma=1.
                    // In the filmgrn1 table this is the 5th numeric field on the p line,
                    // while sCb/sCr legitimately remain 0.
                    if (t.StartsWith("p ", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "p", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = t.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 5 && string.Equals(parts[0], "p", StringComparison.OrdinalIgnoreCase) && parts[5] == "1")
                            return "AV1 胶片颗粒：亮度 + 色度";
                    }
                }
                return "AV1 胶片颗粒：亮度";
            }
            catch { return "AV1 胶片颗粒：参数表读取失败"; }
        }

        private bool IsCurrent(int serial)
        {
            lock (sync)
            {
                return !disposed && serial == requestSerial;
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private static void KillProcessAsync(Process process)
        {
            if (process == null) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(500);
                    }
                }
                catch { }
                DisposeProcess(process);
            });
        }

        private static void DisposeProcess(Process process)
        {
            if (process == null) return;
            try { process.Dispose(); } catch { }
        }

        public void Dispose()
        {
            Process process;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                Interlocked.Increment(ref requestSerial);
                process = currentProcess;
                currentProcess = null;
                Completed = null;
            }
            KillProcessAsync(process);
        }
    }
}
