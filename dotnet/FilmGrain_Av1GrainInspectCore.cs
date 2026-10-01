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

        public void Start(string path, string grav1synthPath, string ffmpegPath)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Input path is empty.", "path");
            if (string.IsNullOrEmpty(ffmpegPath)) throw new ArgumentException("ffmpeg path is empty.", "ffmpegPath");

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

            Thread worker = new Thread(new ThreadStart(delegate { RunInspect(path, grav1synthPath, ffmpegPath, serial); }));
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

        private void RunInspect(string path, string grav1synthPath, string ffmpegPath, int serial)
        {
            string tempTable = Path.Combine(Path.GetTempPath(), "FilmGrainStudio_Inspect_" + Guid.NewGuid().ToString("N") + ".txt");
            Process process = null;
            int exitCode = -1;
            string errorText = "";
            string failureMessage = "";
            string state = "";

            try
            {
                bool? filmGrainPresent = ProbeFilmGrainMetadata(path, ffmpegPath, serial, out process, out exitCode, out errorText, out failureMessage);
                if (!IsCurrent(serial)) return;

                if (filmGrainPresent.HasValue && !filmGrainPresent.Value)
                {
                    state = "未发现 AV1 Film Grain metadata";
                    exitCode = 0;
                    errorText = "";
                    failureMessage = "";
                }
                else
                {
                    // If trace_headers cannot make a definite decision, retain the original
                    // grav1synth inspect behavior so real Film Grain files are not regressed.
                    if (string.IsNullOrWhiteSpace(grav1synthPath) || !File.Exists(grav1synthPath))
                    {
                        failureMessage = "grav1synth is missing.";
                    }
                    else
                    {
                        process = CreateProcess(grav1synthPath, "inspect " + QuoteArgument(path) + " -o " + QuoteArgument(tempTable) + " -y");
                        if (!SetCurrentProcess(process, serial))
                        {
                            process.Dispose();
                            return;
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
                }
            }
            catch (Exception ex)
            {
                failureMessage = ex.Message;
            }
            finally
            {
                ClearCurrentProcess(process);
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

        private bool? ProbeFilmGrainMetadata(string path, string ffmpegPath, int serial, out Process process, out int exitCode, out string errorText, out string failureMessage)
        {
            process = null;
            exitCode = -1;
            errorText = "";
            failureMessage = "";

            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                failureMessage = "ffmpeg is missing for AV1 Film Grain metadata precheck.";
                return null;
            }

            try
            {
                string arguments = "-hide_banner -loglevel trace -i " + QuoteArgument(path) + " -map 0:v:0 -c:v copy -bsf:v trace_headers -frames:v 1 -an -sn -dn -f null -";
                process = CreateProcess(ffmpegPath, arguments);
                if (!SetCurrentProcess(process, serial)) return null;

                if (!process.Start())
                {
                    failureMessage = "Unable to start FFmpeg AV1 Film Grain metadata precheck.";
                    return null;
                }

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                exitCode = process.ExitCode;
                outputTask.Wait();
                errorTask.Wait();
                errorText = errorTask.Result ?? "";

                bool foundZero = false;
                foreach (string line in errorText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.IndexOf("film_grain_params_present", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Match match = Regex.Match(line, @"film_grain_params_present.*=\s*([01])\s*$", RegexOptions.IgnoreCase);
                    if (!match.Success) continue;
                    if (match.Groups[1].Value == "1") return true;
                    foundZero = true;
                }

                if (foundZero && exitCode == 0) return false;
                return null;
            }
            catch (Exception ex)
            {
                failureMessage = ex.Message;
                return null;
            }
            finally
            {
                ClearCurrentProcess(process);
                DisposeProcess(process);
                process = null;
            }
        }

        private static Process CreateProcess(string fileName, string arguments)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = fileName;
            psi.Arguments = arguments;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            Process process = new Process();
            process.StartInfo = psi;
            return process;
        }

        private bool SetCurrentProcess(Process process, int serial)
        {
            lock (sync)
            {
                if (disposed || serial != requestSerial) return false;
                currentProcess = process;
                return true;
            }
        }

        private void ClearCurrentProcess(Process process)
        {
            lock (sync)
            {
                if (object.ReferenceEquals(currentProcess, process)) currentProcess = null;
            }
        }

        internal static string GetFailureDetail(string errorText)
        {
            string first = "";
            string last = "";
            foreach (string line in (errorText ?? "").Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string value = line.Trim();
                if (value.Length == 0) continue;
                if (first.Length == 0) first = value;
                last = value;
                if (value.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("error:", StringComparison.OrdinalIgnoreCase) ||
                    value.IndexOf("code: TagBits", StringComparison.OrdinalIgnoreCase) >= 0)
                    return value;
            }
            return last.Length > 0 ? last : first;
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

                    // p fields: ar_coeff_lag, ar_coeff_shift, grain_scale_shift, scaling_shift,
                    // chroma_scaling_from_luma, overlap_flag, then Cb/Cr multipliers.
                    // The chroma flag is parts[5]; parts[6] is overlap_flag.
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
