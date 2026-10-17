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
    // Read only structured MOV/MP4 boxes; never scan compressed payload for a fourcc.
    internal static class MovFieldOrderCore
    {
        private sealed class Box { public string Type; public long Data; public long End; }
        private sealed class Reader
        {
            internal readonly Stream Input;
            private int remaining = 4096;
            internal Reader(Stream input) { Input = input; }
            internal uint U32() { uint v=0; for(int i=0;i<4;i++){int b=Input.ReadByte();if(b<0)throw new EndOfStreamException();v=(v<<8)|(uint)b;}return v; }
            internal List<Box> Boxes(long start,long end)
            {
                var boxes=new List<Box>();
                while(start<end) {
                    if(--remaining<0 || end-start<8)throw new InvalidDataException("Invalid MOV box list");
                    Input.Position=start;ulong size=U32();uint type=U32();long header=8;
                    if(size==1){size=((ulong)U32()<<32)|U32();header=16;}
                    if(size==0)size=(ulong)(end-start);
                    if(size<(ulong)header || size>(ulong)(end-start))throw new InvalidDataException("Invalid MOV box size");
                    boxes.Add(new Box{Type=new string(new[]{(char)(type>>24),(char)((type>>16)&255),(char)((type>>8)&255),(char)(type&255)}),Data=start+header,End=start+(long)size});
                    start+=(long)size;
                }
                return boxes;
            }
            internal Box Child(Box parent,string type) {foreach(var b in Boxes(parent.Data,parent.End))if(b.Type==type)return b;return null;}
            internal string VideoOrder(Box track,int width,int height,out bool video)
            {
                video=false;var media=Child(track,"mdia");if(media==null)return "";
                var handler=Child(media,"hdlr");if(handler==null || handler.End-handler.Data<12)return "";
                Input.Position=handler.Data+8;if(U32()!=0x76696465)return "";video=true;
                var minf=Child(media,"minf");if(minf==null)return "";
                var table=Child(minf,"stbl");if(table==null)return "";
                var desc=Child(table,"stsd");if(desc==null || desc.End-desc.Data<8)return "";
                Input.Position=desc.Data+4;if(U32()!=1)return ""; // Ambiguous sample descriptions do not override FFprobe.
                var entries=Boxes(desc.Data+8,desc.End);if(entries.Count!=1)return "";var entry=entries[0];
                if(entry.End-entry.Data<78)return "";
                Input.Position=entry.Data+24;uint dimensions=U32();
                if((dimensions>>16)!=(uint)width || (dimensions&65535)!=(uint)height)return "";
                foreach(var field in Boxes(entry.Data+78,entry.End)) {
                    if(field.Type!="fiel")continue;if(field.End-field.Data<2)return "";
                    Input.Position=field.Data;int count=Input.ReadByte(),order=Input.ReadByte();
                    if(count==1)return "progressive";if(count!=2)return "";
                    switch(order){case 1:return "tt";case 6:return "bb";case 9:return "tb";case 14:return "bt";default:return "";}
                }
                return "";
            }
        }
        // Fast eligibility reads only MOV sample-table metadata, never compressed video.
        // A single stts run and no nonzero composition offsets prove regular packet PTS.
        internal static bool HasRegularTimeline(string path, string averageRate)
        {
            try
            {
                string[] rate = (averageRate ?? "").Split('/');
                ulong rateNum, rateDen;
                if (rate.Length != 2 || !ulong.TryParse(rate[0], out rateNum) || !ulong.TryParse(rate[1], out rateDen) || rateNum == 0 || rateDen == 0) return false;
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var reader = new Reader(file);
                    foreach (var root in reader.Boxes(0, file.Length)) if (root.Type == "moov")
                        foreach (var track in reader.Boxes(root.Data, root.End)) if (track.Type == "trak")
                        {
                            var media = reader.Child(track, "mdia"); if (media == null) continue;
                            var handler = reader.Child(media, "hdlr"); if (handler == null || handler.End - handler.Data < 12) return false;
                            file.Position = handler.Data + 8; if (reader.U32() != 0x76696465) continue;
                            var edits = reader.Child(track, "edts");
                            if (edits != null)
                            {
                                var list = reader.Child(edits, "elst"); if (list == null || list.End - list.Data < 20) return false;
                                file.Position = list.Data; uint editVersion = reader.U32() >> 24;
                                if (editVersion > 1 || reader.U32() != 1) return false;
                                if (list.End - list.Data != (editVersion == 1 ? 28 : 20)) return false;
                                file.Position = list.Data + (editVersion == 1 ? 16 : 12);
                                if (reader.U32() != 0 || editVersion == 1 && reader.U32() != 0 || reader.U32() != 65536) return false;
                            }
                            var header = reader.Child(media, "mdhd"); if (header == null || header.End - header.Data < 24) return false;
                            file.Position = header.Data; uint version = reader.U32() >> 24;
                            if (version > 1) return false;
                            long scaleOffset = header.Data + (version == 1 ? 20 : 12);
                            if (header.End - scaleOffset < 4) return false;
                            file.Position = scaleOffset; uint scale = reader.U32();
                            var minf = reader.Child(media, "minf"); if (minf == null) return false;
                            var table = reader.Child(minf, "stbl"); if (table == null) return false;
                            var timing = reader.Child(table, "stts"); if (timing == null || timing.End - timing.Data != 16) return false;
                            file.Position = timing.Data + 4;
                            if (reader.U32() != 1 || reader.U32() == 0) return false;
                            uint delta = reader.U32(); if (delta == 0 || scale == 0 || (decimal)scale * rateDen != (decimal)delta * rateNum) return false;
                            var offsets = reader.Child(table, "ctts");
                            if (offsets != null)
                            {
                                if (offsets.End - offsets.Data < 8) return false;
                                file.Position = offsets.Data + 4; uint count = reader.U32();
                                if (count > 4096 || offsets.End - offsets.Data != 8L + count * 8L) return false;
                                for (uint i = 0; i < count; i++) if (reader.U32() == 0 || reader.U32() != 0) return false;
                            }
                            return true;
                        }
                }
            }
            catch { }
            return false;
        }

        internal static string Read(Stream input,int width,int height)
        {
            var reader=new Reader(input);
            foreach(var root in reader.Boxes(0,input.Length))if(root.Type=="moov")
                foreach(var track in reader.Boxes(root.Data,root.End))if(track.Type=="trak") {
                    bool video;string order=reader.VideoOrder(track,width,height,out video);
                    if(video)return order; // Same first video track selected by MediaProbeInfo.Parse.
                }
            return "";
        }
        internal static void Apply(MediaProbeInfo info)
        {
            if(!info.HasVideo || (info.FormatName??"").IndexOf("mov",StringComparison.OrdinalIgnoreCase)<0)return;
            try {
                using(var file=new FileStream(info.PathValue,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                    string order=Read(file,info.Width,info.Height);
                    info.ContainerFieldOrder=order;
                    if(!info.IsInterlaced && (order=="tt" || order=="bb" || order=="tb" || order=="bt")) {
                        info.FieldOrder=order;info.FieldOrderSource="mov-fiel";
                    }
                }
            }
            catch(InvalidDataException) { }
            catch(IOException) { }
            catch(UnauthorizedAccessException) { }
            catch(ArgumentException) { }
            catch(NotSupportedException) { }
        }
    }

    // Metadata-only identity: no media payload is read for cache validation.
    internal sealed class MediaProbeIdentity
    {
        internal readonly string Input, Probe, Value;
        private MediaProbeIdentity(string input, string probe, string value) { Input=input; Probe=probe; Value=value; }
        internal static string Normalize(string path) { return Path.GetFullPath(path); }
        private static string Stamp(string path)
        {
            var f=new FileInfo(path); f.Refresh();
            if(!f.Exists) throw new FileNotFoundException("Media/probe file is missing",path);
            return f.Length.ToString(CultureInfo.InvariantCulture)+":"+f.LastWriteTimeUtc.Ticks+":"+f.CreationTimeUtc.Ticks;
        }
        internal static MediaProbeIdentity Capture(string input,string probe)
        {
            input=Normalize(input); probe=Normalize(probe);
            return new MediaProbeIdentity(input,probe,Stamp(input)+"|"+probe.ToUpperInvariant()+"|"+Stamp(probe));
        }
        internal bool IsCurrent(string input,string probe)
        {
            try { var next=Capture(input,probe); return string.Equals(Input,next.Input,StringComparison.OrdinalIgnoreCase) && Value==next.Value; }
            catch { return false; }
        }
        internal void RequireCurrent()
        {
            if(!IsCurrent(Input,Probe)) throw new InvalidOperationException("输入或 FFprobe 已变化，请重新探测并准备任务 / Input or FFprobe changed; re-probe and prepare again: "+Input);
        }
    }

    internal sealed class MediaProbeCache : IDictionary<string,MediaProbeInfo>
    {
        private readonly Dictionary<string,MediaProbeInfo> entries=new Dictionary<string,MediaProbeInfo>(StringComparer.OrdinalIgnoreCase);
        internal Func<string> ProbePath;
        public bool TryGetValue(string path,out MediaProbeInfo info)
        {
            info=null;
            try {
                string key=MediaProbeIdentity.Normalize(path); MediaProbeInfo found;
                if(entries.TryGetValue(key,out found) && found!=null && found.Identity!=null && found.Identity.IsCurrent(path,ProbePath())) { info=found;return true; }
                entries.Remove(key);
            } catch { }
            return false;
        }
        public MediaProbeInfo this[string path] {
            get { MediaProbeInfo info; if(!TryGetValue(path,out info))throw new InvalidOperationException("Media changed or probe is not ready; reselect and probe: "+path);return info; }
            set { if(value!=null && value.Identity!=null && value.Identity.IsCurrent(path,ProbePath())) entries[MediaProbeIdentity.Normalize(path)]=value; }
        }
        public bool ContainsKey(string path) { MediaProbeInfo info;return TryGetValue(path,out info); }
        public void Add(string path,MediaProbeInfo info) { this[path]=info; }
        public bool Remove(string path) { return entries.Remove(MediaProbeIdentity.Normalize(path)); }
        public void Clear() { entries.Clear(); }
        public ICollection<string> Keys { get { var keys=new List<string>();foreach(var key in new List<string>(entries.Keys))if(ContainsKey(key))keys.Add(key);return keys; } }
        public ICollection<MediaProbeInfo> Values { get { var values=new List<MediaProbeInfo>();foreach(var key in Keys)values.Add(this[key]);return values; } }
        public int Count { get { return Keys.Count; } }
        public bool IsReadOnly { get { return false; } }
        public void Add(KeyValuePair<string,MediaProbeInfo> item) { Add(item.Key,item.Value); }
        public bool Contains(KeyValuePair<string,MediaProbeInfo> item) { MediaProbeInfo info;return TryGetValue(item.Key,out info)&&object.ReferenceEquals(info,item.Value); }
        public bool Remove(KeyValuePair<string,MediaProbeInfo> item) { return Contains(item)&&Remove(item.Key); }
        public void CopyTo(KeyValuePair<string,MediaProbeInfo>[] array,int index) { foreach(var item in this)array[index++]=item; }
        public IEnumerator<KeyValuePair<string,MediaProbeInfo>> GetEnumerator() { foreach(var key in Keys)yield return new KeyValuePair<string,MediaProbeInfo>(key,this[key]); }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }

    internal sealed class MediaProbeInfo
    {
        internal MediaProbeIdentity Identity;
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
        public string VideoStartTime = "";
        public string FieldOrder = "";
        public string FieldOrderSource = "ffprobe-stream";
        public string ContainerFieldOrder = "";
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
                            info.VideoStartTime = GetString(stream, "start_time");
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

            MovFieldOrderCore.Apply(info);
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
        internal int Serial;
        internal MediaProbeIdentity Identity;
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

            MediaProbeIdentity identity = null;
            try { identity=MediaProbeIdentity.Capture(path,ffprobePath); } catch { }
            Thread worker = new Thread(new ThreadStart(delegate { RunProbe(path, ffprobePath, serial, identity); }));
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

        internal bool IsCurrentResult(MediaProbeResult result) { return result!=null && IsCurrent(result.Serial); }

        private void RunProbe(string path, string ffprobePath, int serial, MediaProbeIdentity identity)
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
                psi.Arguments = "-v error -show_entries format=format_name,duration,bit_rate:stream=codec_type,codec_name,profile,width,height,avg_frame_rate,start_time,field_order,bit_rate,channels,channel_layout,sample_rate,pix_fmt,bits_per_raw_sample,color_range,color_space,color_transfer,color_primaries -of json " + QuoteArgument(path);

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
            if (info != null && info.HasVideo && NeedsFrameFieldOrder(info.FieldOrder))
            {
                string frameOrder = ProbeFrameFieldOrder(path, ffprobePath, serial);
                if (!IsCurrent(serial)) return;
                if (!string.IsNullOrEmpty(frameOrder)) { info.FieldOrder = frameOrder; info.FieldOrderSource = "ffprobe-frames"; }
            }

            if(info!=null) {
                if(identity==null || !identity.IsCurrent(path,ffprobePath)) { info=null;failureMessage="Input or FFprobe changed during probing; reselect and probe again."; }
                else info.Identity=identity;
            }
            MediaProbeResult result = new MediaProbeResult();
            result.Serial = serial;
            result.Identity = identity;
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

        private static bool NeedsFrameFieldOrder(string value)
        {
            return string.IsNullOrWhiteSpace(value) || string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase);
        }

        private string ProbeFrameFieldOrder(string path, string ffprobePath, int serial)
        {
            Process process = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = ffprobePath;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.Arguments = "-v error -select_streams v:0 -read_intervals \"%+#16\" -show_frames -show_entries frame=interlaced_frame,top_field_first -of csv=p=0 " + QuoteArgument(path);
                process = new Process();
                process.StartInfo = psi;
                lock (sync)
                {
                    if (disposed || serial != requestSerial) return "";
                    currentProcess = process;
                }
                if (!process.Start()) return "";
                string frames = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) return "";
                int total = 0, interlaced = 0, topFirst = 0;
                foreach (string raw in frames.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] flags = raw.Trim().Split(',');
                    if (flags.Length < 2 || (flags[0] != "0" && flags[0] != "1") || (flags[1] != "0" && flags[1] != "1")) continue;
                    total++;
                    if (flags[0] == "1") { interlaced++; if (flags[1] == "1") topFirst++; }
                }
                if (total >= 3 && interlaced * 4 >= total * 3)
                    return topFirst * 2 >= interlaced ? "tt" : "bb";
            }
            catch { }
            finally
            {
                lock (sync) { if (object.ReferenceEquals(currentProcess, process)) currentProcess = null; }
                DisposeProcess(process);
            }
            return "";
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
