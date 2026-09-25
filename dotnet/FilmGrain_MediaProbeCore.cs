using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;

namespace FilmGrainStudioPreview
{
    internal sealed class MediaProbeInfo
    {
        public string PathValue = "";
        public string FormatName = "";
        public string Duration = "";
        public string TotalBitRate = "";

        public bool HasVideo;
        public string VideoCodec = "";
        public string VideoProfile = "";
        public int Width;
        public int Height;
        public string AvgFrameRate = "";
        public string FieldOrder = "";
        public string VideoBitRate = "";
        public string PixelFormat = "";
        public string BitsPerRawSample = "";
        public string ColorRange = "";
        public string ColorSpace = "";
        public string ColorTransfer = "";
        public string ColorPrimaries = "";

        public bool HasAudio;
        public string AudioCodec = "";
        public string AudioProfile = "";
        public string AudioBitRate = "";
        public int Channels;
        public string ChannelLayout = "";
        public string SampleRate = "";

        public bool IsInterlaced
        {
            get
            {
                string value = (FieldOrder ?? "").ToLowerInvariant();
                return value == "tt" || value == "bb" || value == "tb" || value == "bt";
            }
        }

        public bool IsHdr
        {
            get
            {
                string value = (ColorTransfer ?? "").ToLowerInvariant();
                return value == "smpte2084" || value == "arib-std-b67";
            }
        }

        public static MediaProbeInfo Parse(string path, string json)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("FFprobe JSON root is invalid.");

            MediaProbeInfo info = new MediaProbeInfo();
            info.PathValue = path ?? "";

            Dictionary<string, object> format = GetDictionary(root, "format");
            if (format != null)
            {
                info.FormatName = GetString(format, "format_name");
                info.Duration = GetString(format, "duration");
                info.TotalBitRate = GetString(format, "bit_rate");
            }

            object streamValue;
            if (root.TryGetValue("streams", out streamValue) && streamValue != null)
            {
                IEnumerable streams = streamValue as IEnumerable;
                if (streams != null)
                {
                    foreach (object entry in streams)
                    {
                        Dictionary<string, object> stream = entry as Dictionary<string, object>;
                        if (stream == null) continue;
                        string type = GetString(stream, "codec_type").ToLowerInvariant();
                        if (type == "video" && !info.HasVideo)
                        {
                            info.HasVideo = true;
                            info.VideoCodec = GetString(stream, "codec_name");
                            info.VideoProfile = GetString(stream, "profile");
                            info.Width = GetInt(stream, "width");
                            info.Height = GetInt(stream, "height");
                            info.AvgFrameRate = GetString(stream, "avg_frame_rate");
                            info.FieldOrder = GetString(stream, "field_order");
                            info.VideoBitRate = GetString(stream, "bit_rate");
                            info.PixelFormat = GetString(stream, "pix_fmt");
                            info.BitsPerRawSample = GetString(stream, "bits_per_raw_sample");
                            info.ColorRange = GetString(stream, "color_range");
                            info.ColorSpace = GetString(stream, "color_space");
                            info.ColorTransfer = GetString(stream, "color_transfer");
                            info.ColorPrimaries = GetString(stream, "color_primaries");
                        }
                        else if (type == "audio" && !info.HasAudio)
                        {
                            info.HasAudio = true;
                            info.AudioCodec = GetString(stream, "codec_name");
                            info.AudioProfile = GetString(stream, "profile");
                            info.AudioBitRate = GetString(stream, "bit_rate");
                            info.Channels = GetInt(stream, "channels");
                            info.ChannelLayout = GetString(stream, "channel_layout");
                            info.SampleRate = GetString(stream, "sample_rate");
                        }
                    }
                }
            }

            return info;
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> source, string key)
        {
            object value;
            if (source != null && source.TryGetValue(key, out value)) return value as Dictionary<string, object>;
            return null;
        }

        private static string GetString(Dictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return "";
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        private static int GetInt(Dictionary<string, object> source, string key)
        {
            string value = GetString(source, key);
            int parsed;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }
    }

    internal sealed class MediaProbeResult
    {
        public string PathValue;
        public MediaProbeInfo Info;
        public int ExitCode;
        public string StandardError;
        public string FailureMessage;
        public bool StartReturnedFalse;
    }

    internal sealed class MediaProbeCore : IDisposable
    {
        private readonly object sync = new object();
        private Process currentProcess;
        private int requestSerial;
        private bool disposed;

        public event Action<MediaProbeResult> Completed;

        public void Start(string path, string ffprobePath)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Input path is empty.", "path");
            if (string.IsNullOrEmpty(ffprobePath)) throw new ArgumentException("FFprobe path is empty.", "ffprobePath");

            int serial;
            Process oldProcess;
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("MediaProbeCore");
                serial = Interlocked.Increment(ref requestSerial);
                oldProcess = currentProcess;
                currentProcess = null;
            }
            KillProcessAsync(oldProcess);

            Thread worker = new Thread(new ThreadStart(delegate { RunProbe(path, ffprobePath, serial); }));
            worker.IsBackground = true;
            worker.Name = "FGS FFprobe Core";
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

        private void RunProbe(string path, string ffprobePath, int serial)
        {
            Process process = null;
            string json = "";
            string error = "";
            int exitCode = -1;
            string failureMessage = "";
            bool startReturnedFalse = false;
            MediaProbeInfo info = null;

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = ffprobePath;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.Arguments = "-v error -show_entries format=format_name,duration,bit_rate:stream=codec_type,codec_name,profile,width,height,avg_frame_rate,field_order,bit_rate,channels,channel_layout,sample_rate,pix_fmt,bits_per_raw_sample,color_range,color_space,color_transfer,color_primaries -of json " + QuoteArgument(path);

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
                    startReturnedFalse = true;
                }
                else
                {
                    json = process.StandardOutput.ReadToEnd();
                    error = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    exitCode = process.ExitCode;
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
            }

            if (!IsCurrent(serial))
            {
                DisposeProcess(process);
                return;
            }

            if (!startReturnedFalse && string.IsNullOrEmpty(failureMessage) && exitCode == 0 && !string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    info = MediaProbeInfo.Parse(path, json);
                }
                catch (Exception ex)
                {
                    failureMessage = ex.Message;
                }
            }

            DisposeProcess(process);
            if (!IsCurrent(serial)) return;

            MediaProbeResult result = new MediaProbeResult();
            result.PathValue = path;
            result.Info = info;
            result.ExitCode = exitCode;
            result.StandardError = error ?? "";
            result.FailureMessage = failureMessage ?? "";
            result.StartReturnedFalse = startReturnedFalse;

            Action<MediaProbeResult> handler = Completed;
            if (handler != null)
            {
                try { handler(result); } catch { }
            }
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
