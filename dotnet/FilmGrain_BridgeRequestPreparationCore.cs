using System;
using System.Collections.Generic;
using System.IO;

namespace FilmGrainStudioPreview
{
    internal static class SharedInterpolationBypassCore
    {
        // Normalize only proven Legacy bypass rules. Do not mutate the user's captured state.
        internal static bool TryResolve(MediaProbeInfo info, IDictionary<string,string> state,
            out IDictionary<string,string> normalized, out string notice, out string reason)
        {
            normalized = null; notice = ""; reason = "";
            if (info == null || state == null) return true;
            string enabled, engine, policy, deint;
            state.TryGetValue("FG_SVP_INTERPOLATE",out enabled); state.TryGetValue("FG_INTERPOLATION_ENGINE",out engine);
            if (enabled != "1" || (!string.IsNullOrEmpty(engine) && !string.Equals(engine,"SVP",StringComparison.OrdinalIgnoreCase))) return true;
            state.TryGetValue("FG_HDR_POLICY",out policy); state.TryGetValue("FG_DEINTERLACE",out deint);
            if (info.IsHdr && !info.IsInterlaced && string.Equals(policy,"PRESERVE",StringComparison.OrdinalIgnoreCase))
            {
                normalized = new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase);
                normalized["FG_SVP_INTERPOLATE"] = "0"; normalized["FG_FPS_MODE"] = "SOURCE";
                notice = "[Native .NET] OpenSVPFlow 已绕过：HDR 保留使用原帧率与 10-bit 编码路径，不运行 8-bit 光流。 / OpenSVPFlow bypassed for HDR preservation; source FPS retained.";
                return true;
            }
            if (!info.IsInterlaced) return true;
            if (!string.Equals(deint,"AUTO",StringComparison.OrdinalIgnoreCase))
            { reason = "隔行源启用 OpenSVPFlow 时必须选择 AUTO 反交错；不会向光流传递交错帧。 / Interlaced OpenSVPFlow requires AUTO deinterlace."; return false; }
            normalized = new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase);
            normalized["FG_SVP_INTERPOLATE"] = "0"; normalized["FG_FPS_MODE"] = "AUTO";
            // Retain the original AUTO tonemap decision even though SVP is bypassed.
            if(info.IsHdr && !string.Equals(policy,"PRESERVE",StringComparison.OrdinalIgnoreCase))normalized["FG_HDR_POLICY"]="SDR";
            notice = "[Native .NET] OpenSVPFlow 已绕过：隔行输入使用所选反交错方式的 2x 场率输出，目标插帧 FPS 不参与。 / OpenSVPFlow bypassed for interlaced input; selected field-rate deinterlace used.";
            return true;
        }
        internal static void Attach(BridgePreparedExecution prepared,string notice)
        {
            if (prepared == null || string.IsNullOrEmpty(notice)) return;
            prepared.ExecutionRequest.ExecutionNotice = (prepared.ExecutionRequest.ExecutionNotice ?? "") + Environment.NewLine + notice;
            if (prepared.ExecutionRequest.DirectStages != null && prepared.ExecutionRequest.DirectStages.Count > 0)
                prepared.ExecutionRequest.DirectStages[0].StageText += " / OpenSVP 已绕过";
        }
    }

    internal static class NativeRuntimeBoundaryCore
    {
        // The .NET application never delegates preparation failures to scripts.
        internal static bool MustStop(string reason) { return true; }
        internal static string FailureReason(string reason)
        { return string.IsNullOrWhiteSpace(reason)?"Native preparation failed without a diagnostic; task stopped.":reason; }
    }

    internal static class NativeExecutionPreparationCore
    {
        // Shared by single-file UI, queue and the local test harness. No compatibility backend.
        internal static bool TryPrepare(string appRoot,string input,MediaProbeInfo info,
            IDictionary<string,string> state,HardwareCapabilitySnapshot caps,bool noReencode,
            out BridgePreparedExecution prepared,out string reason)
        {
            if(state==null){prepared=null;reason="missing execution state";return false;}
            var timer=System.Diagnostics.Stopwatch.StartNew();
            string previewFlag;state.TryGetValue("FG_ENCODE_PREVIEW",out previewFlag);
            string requested;state.TryGetValue("FG_GPU_DECODE",out requested);
            bool planning=requested=="1"&&!noReencode&&previewFlag!="1";
            var initialState=new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase);
            initialState.Remove("FG_HWD_PLAN");initialState.Remove("FG_HWD_BACKEND");initialState.Remove("FG_HWD_SOURCE");
            if(planning)initialState["FG_HWD_PLAN"]=Guid.NewGuid().ToString("N");
            bool ok=TryPrepareCore(appRoot,input,info,initialState,caps,noReencode,out prepared,out reason);
            string decodeNotice=noReencode?"[HWD] effective=not-applicable reason=video stream copy":"";
            if(ok && !noReencode && previewFlag!="1") {
                IDictionary<string,string> selected;
                bool hardware=HardwareDecodeCore.Select(appRoot,input,prepared.ExecutionRequest,initialState,out selected,out decodeNotice);
                if(planning) {
                    BridgeExecutionCore.CleanupTemporaryFiles(prepared.ExecutionRequest);
                    if(!hardware)selected=initialState;
                    selected.Remove("FG_HWD_PLAN");
                    ok=TryPrepareCore(appRoot,input,info,selected,caps,noReencode,out prepared,out reason);
                }
            }
            if(ok && prepared!=null && decodeNotice.Length>0)prepared.ExecutionRequest.ExecutionNotice=(prepared.ExecutionRequest.ExecutionNotice??"")+Environment.NewLine+decodeNotice;
            if(ok && prepared!=null && prepared.ExecutionRequest!=null)
            {
                try { SharedInputTimelineCore.Bind(prepared.ExecutionRequest,input,info.Identity.Probe,info,noReencode); }
                catch(Exception ex) {
                    BridgeExecutionCore.CleanupTemporaryFiles(prepared.ExecutionRequest);
                    prepared=null;reason="Shared input timeline binding failed: "+ex.Message;ok=false;
                }
            }
            timer.Stop();
            if(ok && prepared!=null && prepared.ExecutionRequest!=null)
                prepared.ExecutionRequest.PreparationElapsed=timer.Elapsed;
            return ok;
        }

        internal static bool TryPrepareCore(string appRoot,string input,MediaProbeInfo info,
            IDictionary<string,string> state,HardwareCapabilitySnapshot caps,bool noReencode,
            out BridgePreparedExecution prepared,out string reason)
        {
            prepared=null;reason="";
            if(string.IsNullOrWhiteSpace(input)||!File.Exists(input)){reason="input file does not exist or is inaccessible: "+input;return false;}
            if(info==null){reason="media probe cache is not ready: "+input;return false;}
            var probeConfig=new FgsConfig(appRoot,null);probeConfig.Load();
            string currentProbe=Path.Combine(probeConfig.Get("FFMPEG_DIR"),"ffprobe.exe");
            if(info.Identity==null || !info.Identity.IsCurrent(input,currentProbe)) { reason="Input or FFprobe changed; reselect and probe again: "+input;return false; }
            if(!info.HasVideo){reason="input has no usable video stream: "+input;return false;}
            if(state==null){reason="missing execution state";return false;}
            try { AudioOutputCore.EstimateMissing(input,currentProbe,info,state); }
            catch(Exception ex){reason=ex.Message;return false;}

            string preview;
            if (state.TryGetValue("FG_ENCODE_PREVIEW", out preview) && preview == "1")
            {
                bool ok=SharedEncodePreviewCore.TryPrepare(appRoot,input,info,state,caps,noReencode,out prepared,out reason);
                if(ok) { prepared.ExecutionRequest.SourceIdentity=info.Identity;prepared.ExecutionRequest.SourceConfigRoot=appRoot; }
                return ok;
            }
            string svpEnabled, svpEngine;
            state.TryGetValue("FG_SVP_INTERPOLATE", out svpEnabled); state.TryGetValue("FG_INTERPOLATION_ENGINE", out svpEngine);
            if (!noReencode && svpEnabled == "1" && (string.IsNullOrEmpty(svpEngine) || string.Equals(svpEngine, "SVP", StringComparison.OrdinalIgnoreCase)))
            {
                string speed, tuning, block, analyse;
                if (!state.TryGetValue("FG_SVP_SPEED", out speed)) speed = "FGS";
                if (!state.TryGetValue("FG_SVP_TUNING", out tuning)) tuning = "FGS";
                if (!state.TryGetValue("FG_SVP_BLOCK_SIZE", out block)) block = "auto";
                if (!state.TryGetValue("FG_SVP_ANALYSE", out analyse)) analyse = "ENCODEGUI";
                if (!SharedSvpCore.TryValidateOptions(speed, tuning, block, analyse, out reason)) return false;
            }
            try
            {
                bool success;
                if(noReencode && PreprocessingCore.Enabled(state)){reason="前处理需要重新编码，不能用于仅替换 AV1 颗粒。";return false;}
                if(noReencode)
                {
                    var copyState=new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase);copyState["FG_MODE"]="AV1";copyState["FG_GRAIN_ENABLED"]="1";
                    success=NativeAv1GrainReplaceCore.TryPrepare(appRoot,input,info,copyState,out prepared,out reason);
                }
                else
                {
                    string mode;state.TryGetValue("FG_MODE",out mode);
                    if(string.Equals(mode,"AV1BAKE",StringComparison.OrdinalIgnoreCase)&&PreprocessingCore.Enabled(state)){reason="独立 AV1 颗粒烘焙不提供前处理，请使用主编码流程。";return false;}
                    if(string.Equals(mode,"AV1BAKE",StringComparison.OrdinalIgnoreCase))success=NativeAv1BakeCore.TryPrepare(appRoot,input,info,state,out prepared,out reason);
                    else if(string.Equals(mode,"AV1",StringComparison.OrdinalIgnoreCase))success=NativeAv1Core.TryPrepare(appRoot,input,info,state,caps,out prepared,out reason);
                    else if((string.Equals(mode,"HEVC",StringComparison.OrdinalIgnoreCase)||string.Equals(mode,"H264",StringComparison.OrdinalIgnoreCase)))success=NativeHevcCore.TryPrepare(appRoot,input,info,state,caps,out prepared,out reason);
                    else if(string.Equals(mode,"X264",StringComparison.OrdinalIgnoreCase)||string.Equals(mode,"SVT",StringComparison.OrdinalIgnoreCase)||string.Equals(mode,"X265",StringComparison.OrdinalIgnoreCase))success=NativeX264DigitalGrainCore.TryPrepare(appRoot,input,info,state,out prepared,out reason,caps==null?"":caps.GpuName);
                    else {reason="unsupported codec: "+mode;return false;}
                }
                if(success && prepared!=null && prepared.ExecutionRequest!=null)
                { if(!noReencode && PreprocessingCore.Enabled(state))prepared.ExecutionRequest.ExecutionNotice=(prepared.ExecutionRequest.ExecutionNotice??"")+Environment.NewLine+"[Preprocessing] CPU bilateral / 10-bit software frames / before color, LUT and grain"; info.Identity.RequireCurrent(); string rateNotice=NvencRateControlCore.Notice(state);if(rateNotice.Length>0)prepared.ExecutionRequest.ExecutionNotice=(prepared.ExecutionRequest.ExecutionNotice??"")+Environment.NewLine+rateNotice; string audioNotice=AudioOutputCore.Notice(info,state);if(audioNotice.Length>0)prepared.ExecutionRequest.ExecutionNotice=(prepared.ExecutionRequest.ExecutionNotice??"")+Environment.NewLine+audioNotice; prepared.ExecutionRequest.SourceIdentity=info.Identity; prepared.ExecutionRequest.SourceConfigRoot=appRoot; NativeProcessPolicy.ValidateRequest(prepared.ExecutionRequest);return true; }
                if(success)reason="Native preparation returned no execution request";
            }
            catch(Exception ex){reason="Native preparation failed: "+ex.Message;}
            if(prepared!=null && prepared.ExecutionRequest!=null)BridgeExecutionCore.CleanupTemporaryFiles(prepared.ExecutionRequest);
            prepared=null;reason=NativeRuntimeBoundaryCore.FailureReason(reason);return false;
        }
    }

    // One stream-copy boundary for all native encoding paths. Disabled unless explicitly requested.
    internal static class SharedEncodePreviewCore
    {
        internal static bool TryParseStart(string text, out double seconds)
        {
            seconds = 0;
            if (text == null || !System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d{2}:[0-5]\d:[0-5]\d$")) return false;
            seconds = int.Parse(text.Substring(0, 2), System.Globalization.CultureInfo.InvariantCulture) * 3600
                + int.Parse(text.Substring(3, 2), System.Globalization.CultureInfo.InvariantCulture) * 60
                + int.Parse(text.Substring(6, 2), System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        private static string Get(IDictionary<string,string> state, string key)
        { string value; return state.TryGetValue(key, out value) ? value ?? "" : ""; }
        private static string Number(double value) { return value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture); }
        private static double ParseNumber(string value)
        { double n; return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n) ? n : 0; }
        internal static string Quote(string value)
        {
            // Windows CreateProcess argument quoting, never a command shell.
            System.Text.StringBuilder result = new System.Text.StringBuilder("\""); int slashes = 0;
            foreach (char c in value) {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1);
                else result.Append('\\', slashes);
                result.Append(c); slashes = 0;
            }
            result.Append('\\', slashes * 2); return result.Append('"').ToString();
        }
        internal static bool TryPrepare(string appRoot, string input, MediaProbeInfo info, IDictionary<string,string> state,
            HardwareCapabilitySnapshot caps, bool noReencode, out BridgePreparedExecution prepared, out string reason)
        {
            prepared = null; reason = "";
            double start; int length;
            if (!TryParseStart(Get(state,"FG_PREVIEW_START"), out start) ||
                !int.TryParse(Get(state,"FG_PREVIEW_SECONDS"), out length) || length < 1 || length > 86400)
            { reason = "预览时间无效 / Invalid preview range"; return false; }
            double sourceDuration = ParseNumber(info.Duration);
            if (sourceDuration > 0 && start >= sourceDuration)
            { reason = "预览起始时间已超出视频时长 / Preview starts beyond the source duration"; return false; }
            var config = new FgsConfig(appRoot, null); config.Load();
            string ffmpeg = Path.Combine(config.Get("FFMPEG_DIR"), "ffmpeg.exe");
            string ffprobe = Path.Combine(config.Get("FFMPEG_DIR"), "ffprobe.exe");
            if (!File.Exists(ffmpeg) || !File.Exists(ffprobe)) { reason = "Preview requires ffmpeg.exe and ffprobe.exe"; return false; }
            var captured = new Dictionary<string,string>(state, StringComparer.OrdinalIgnoreCase);
            captured["FG_ENCODE_PREVIEW"] = "0";
            string tempMode = Get(state,"FG_TEMP_MODE").ToUpperInvariant();
            string tempRoot = tempMode == "SYSTEM" ? Path.Combine(Path.GetTempPath(),"FilmGrain_Studio") :
                tempMode == "CUSTOM" ? Path.Combine(Get(state,"FG_TEMP_CUSTOM_DIR"),"FilmGrain_Studio") : Path.GetDirectoryName(input);
            if (tempMode == "CUSTOM" && string.IsNullOrWhiteSpace(Get(state,"FG_TEMP_CUSTOM_DIR")))
            { reason = "Custom temporary directory is empty"; return false; }
            string directory = Path.Combine(tempRoot, "__FGPREVIEW_" + Guid.NewGuid().ToString("N"));
            string stem = Path.GetFileNameWithoutExtension(input) + "_PREVIEW_" + Get(state,"FG_PREVIEW_START").Replace(":", "") + "_" + length + "s";
            string clip = Path.Combine(directory, stem + Path.GetExtension(input));
            string outputDirectory = Get(state,"FG_OUTPUT_MODE") == "CUSTOM" ? Get(state,"FG_OUTPUT_CUSTOM_DIR") : Path.GetDirectoryName(input);
            captured["FG_OUTPUT_MODE"] = "CUSTOM"; captured["FG_OUTPUT_CUSTOM_DIR"] = outputDirectory;
            // Resolve sidecars against the original source before its temporary name changes.
            if (Get(captured,"FG_SUBTITLE") == "1") {
                string mode = Get(captured,"FG_SUB_MODE").ToUpperInvariant();
                string external = mode == "EXTERNAL" ? Get(captured,"FG_SUB_PATH") : "";
                if ((mode == "AUTO" || mode == "EXTERNAL") && string.IsNullOrWhiteSpace(external)) external = SubtitleCore.FindSameName(input);
                if (!string.IsNullOrWhiteSpace(external)) { captured["FG_SUB_MODE"] = "EXTERNAL"; captured["FG_SUB_PATH"] = external; }
            }
            var request = new BridgeExecutionRequest {
                DirectProcess = true, ToolPath = ffmpeg, WorkingDirectory = appRoot,
                InputFiles = new List<string> { input }, Environment = captured,
                TemporaryFiles = new List<string> { clip }, TemporaryDirectories = new List<string> { directory },
                ProgressDurationSeconds = length,
                ExecutionNotice = "[Preview] 关键帧无损裁剪 / keyframe stream copy: start=" + Get(state,"FG_PREVIEW_START") + " requested_seconds=" + length + "; actual start/duration may differ."
            };
            double alignedStart = start;
            double sourceOrigin = 0;
            var cut = new BridgeExecutionStage {
                ToolPath = ffmpeg, WorkingDirectory = appRoot, StageCurrent = 1, StageTotal = 1,
                StageText = "预览：关键帧裁剪 / Preview: keyframe clip", ProgressDurationSeconds = length
            };
            // Resolve a real video key packet on the worker thread; no UI blocking and no exact-frame trimming.
            cut.PrepareWithCancellation = delegate(Func<bool> cancelled) {
                Directory.CreateDirectory(directory);
                string json = RunTool(ffprobe, "-v error -select_streams v:0 -read_intervals " + Quote(Number(start) + "%+#4096") +
                    " -show_packets -show_format -show_entries packet=pts_time,flags:format=start_time -of json " + Quote(input),
                    cancelled, 30000);
                var parser = new System.Web.Script.Serialization.JavaScriptSerializer(); parser.MaxJsonLength = 16777216;
                var root = parser.DeserializeObject(json) as Dictionary<string,object>;
                object raw; var packets = root != null && root.TryGetValue("packets",out raw) ? raw as object[] : null;
                Dictionary<string,object> packet = null;
                if (packets != null) foreach (object entry in packets) {
                    var candidate = entry as Dictionary<string,object>; object key;
                    if (candidate != null && candidate.TryGetValue("flags",out key) && Convert.ToString(key).IndexOf('K') >= 0) { packet = candidate; break; }
                }
                // Some TS files have no index and seeking near EOF lands after the last keyframe.
                // Search the preceding window in that case; never fall back to a non-key packet.
                if (packet == null && start > 0) {
                    string previous = RunTool(ffprobe, "-v error -select_streams v:0 -read_intervals " + Quote(Number(Math.Max(0,start-30)) + "%" + Number(start+1)) +
                        " -show_packets -show_entries packet=pts_time,flags -of json " + Quote(input), cancelled, 30000);
                    var earlier = parser.DeserializeObject(previous) as Dictionary<string,object>;
                    var previousPackets = earlier != null && earlier.TryGetValue("packets",out raw) ? raw as object[] : null;
                    if (previousPackets != null) foreach (object entry in previousPackets) {
                        var candidate = entry as Dictionary<string,object>; object key;
                        if (candidate != null && candidate.TryGetValue("flags",out key) && Convert.ToString(key).IndexOf('K') >= 0) packet = candidate;
                    }
                }
                object pts;
                if (packet == null || !packet.TryGetValue("pts_time",out pts))
                    throw new InvalidDataException("Could not locate a safe video keyframe for preview; no exact-frame fallback is used.");
                var format = root.TryGetValue("format",out raw) ? raw as Dictionary<string,object> : null;
                object begin; double origin = format != null && format.TryGetValue("start_time",out begin) ? ParseNumber(Convert.ToString(begin)) : 0;
                sourceOrigin = origin;
                alignedStart = Math.Max(0, ParseNumber(Convert.ToString(pts)) - origin);
                cut.DirectArguments = "-hide_banner -nostdin -n -noaccurate_seek -ss " + Number(alignedStart) + " -i " + Quote(input) +
                    " -t " + length + " -map 0 -map -0:d? -c copy -avoid_negative_ts make_zero " + Quote(clip);
                return "[Preview] aligned_start_seconds=" + Number(alignedStart) + "; video/audio stream copy; no video re-encode during clipping.";
            };
            request.DirectStages = new List<BridgeExecutionStage> { cut };
            request.ContinueAfterStages = delegate(Func<bool> cancelled) {
                MediaProbeInfo clipped = Probe(clip, ffprobe, cancelled);
                if (cancelled()) throw new OperationCanceledException();
                if (!clipped.HasVideo || ParseNumber(clipped.Duration) <= 0) throw new InvalidDataException("Preview clip has no usable duration/video.");
                string timingJson = RunTool(ffprobe, "-v error -show_entries format=start_time -of json " + Quote(clip), cancelled, 30000);
                var timingRoot = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(timingJson) as Dictionary<string,object>;
                object formatObject, startObject;
                var timingFormat = timingRoot != null && timingRoot.TryGetValue("format",out formatObject) ? formatObject as Dictionary<string,object> : null;
                double clipOrigin = timingFormat != null && timingFormat.TryGetValue("start_time",out startObject) ? ParseNumber(Convert.ToString(startObject)) : 0;
                double videoLead = ParseNumber(clipped.VideoStartTime) - clipOrigin;
                if (Get(captured,"FG_SUBTITLE") == "1" && Get(captured,"FG_SUB_MODE") == "EXTERNAL") {
                    // Demuxer cluster seeking can start earlier than the selected packet (notably MKV).
                    // Match the copied first key packet to the original instead of guessing a subtitle offset.
                    string firstJson = RunTool(ffprobe, "-v error -select_streams v:0 -read_intervals " + Quote("%+#1") +
                        " -show_packets -show_data_hash sha256 -show_entries packet=data_hash -of json " + Quote(clip), cancelled, 30000);
                    var hashes = new System.Web.Script.Serialization.JavaScriptSerializer(); hashes.MaxJsonLength = 16777216;
                    var firstRoot = hashes.DeserializeObject(firstJson) as Dictionary<string,object>; object values;
                    var firstPackets = firstRoot != null && firstRoot.TryGetValue("packets",out values) ? values as object[] : null;
                    var firstPacket = firstPackets != null && firstPackets.Length > 0 ? firstPackets[0] as Dictionary<string,object> : null;
                    object firstHash;
                    if (firstPacket == null || !firstPacket.TryGetValue("data_hash",out firstHash)) throw new InvalidDataException("Cannot resolve the preview subtitle timeline");
                    string originalJson = RunTool(ffprobe, "-v error -select_streams v:0 -read_intervals " + Quote(Number(Math.Max(0,sourceOrigin+alignedStart-30)) + "%" + Number(sourceOrigin+alignedStart+30)) +
                        " -show_packets -show_data_hash sha256 -show_entries packet=pts_time,data_hash -of json " + Quote(input), cancelled, 30000);
                    var originalRoot = hashes.DeserializeObject(originalJson) as Dictionary<string,object>;
                    var originalPackets = originalRoot != null && originalRoot.TryGetValue("packets",out values) ? values as object[] : null;
                    bool matched = false;
                    if (originalPackets != null) foreach (object value in originalPackets) {
                        var original = value as Dictionary<string,object>; object hash, timestamp;
                        if (original != null && original.TryGetValue("data_hash",out hash) && Convert.ToString(hash) == Convert.ToString(firstHash) && original.TryGetValue("pts_time",out timestamp)) {
                            captured["FG_PREVIEW_SUB_OFFSET"] = Number(Math.Max(0,ParseNumber(Convert.ToString(timestamp))-sourceOrigin-videoLead)); matched = true; break;
                        }
                    }
                    if (!matched) throw new InvalidDataException("Cannot match preview keyframe to the original subtitle timeline; stopped to avoid incorrect subtitle timing.");
                }
                BridgePreparedExecution next; string error;
                if (!NativeExecutionPreparationCore.TryPrepare(appRoot, clip, clipped, captured, caps, noReencode, out next, out error))
                    throw new InvalidOperationException("Preview encoding preparation failed: " + error);
                next.ExecutionRequest.ExecutionNotice = "[Preview] actual_clip_seconds=" + clipped.Duration + Environment.NewLine + (next.ExecutionRequest.ExecutionNotice ?? "");
                return next.ExecutionRequest;
            };
            prepared = new BridgePreparedExecution(request, noReencode, ffmpeg, new List<string> { input });
            return true;
        }
        internal static MediaProbeInfo Probe(string path, string ffprobe, Func<bool> cancelled)
        {
            MediaProbeResult result = null;
            using (var probe = new MediaProbeCore()) {
                probe.Completed += delegate(MediaProbeResult value) { System.Threading.Interlocked.Exchange(ref result, value); };
                probe.Start(path, ffprobe); var watch = System.Diagnostics.Stopwatch.StartNew();
                while (System.Threading.Interlocked.CompareExchange(ref result,null,null) == null) {
                    if (cancelled()) throw new OperationCanceledException();
                    if (watch.ElapsedMilliseconds > 30000) throw new TimeoutException("Preview FFprobe timed out");
                    System.Threading.Thread.Sleep(50);
                }
                if (result.Info == null || result.ExitCode != 0) throw new InvalidDataException("Preview probe failed: " + result.FailureMessage + " " + result.StandardError);
                return result.Info;
            }
        }
        private static string RunTool(string tool, string arguments, Func<bool> cancelled, int timeout)
        {
            var text = new System.Text.StringBuilder(); var errors = new System.Text.StringBuilder();
            using (var process = new System.Diagnostics.Process()) {
                process.StartInfo = new System.Diagnostics.ProcessStartInfo(tool, arguments) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                process.OutputDataReceived += delegate(object sender, System.Diagnostics.DataReceivedEventArgs e) { if (e.Data != null) lock(text) text.AppendLine(e.Data); };
                process.ErrorDataReceived += delegate(object sender, System.Diagnostics.DataReceivedEventArgs e) { if (e.Data != null) lock(errors) errors.AppendLine(e.Data); };
                try {
                    process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    while (!process.WaitForExit(50)) {
                        if (cancelled()) throw new OperationCanceledException();
                        if (watch.ElapsedMilliseconds > timeout) throw new TimeoutException("Preview keyframe probe timed out");
                    }
                    process.WaitForExit();
                    if (process.ExitCode != 0) throw new InvalidDataException("Preview keyframe probe failed: " + errors.ToString());
                    return text.ToString();
                } finally { try { if (!process.HasExited) { process.Kill(); process.WaitForExit(2000); } } catch { } }
            }
        }
        internal static void ShiftExternalAss(string path, IDictionary<string,string> state)
        {
            double offset = ParseNumber(Get(state,"FG_PREVIEW_SUB_OFFSET"));
            if (offset <= 0) return;
            var lines = new List<string>();
            foreach (string line in File.ReadAllLines(path)) {
                if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) { lines.Add(line); continue; }
                string[] fields = line.Split(new char[] { ',' }, 10);
                TimeSpan begin, end;
                if (fields.Length < 10 || !TimeSpan.TryParse(fields[1],System.Globalization.CultureInfo.InvariantCulture,out begin) ||
                    !TimeSpan.TryParse(fields[2],System.Globalization.CultureInfo.InvariantCulture,out end)) throw new InvalidDataException("Invalid ASS dialogue timestamp");
                double first = begin.TotalSeconds - offset, last = end.TotalSeconds - offset;
                if (last <= 0) continue;
                fields[1] = AssTime(Math.Max(0,first)); fields[2] = AssTime(last);
                lines.Add(string.Join(",",fields));
            }
            File.WriteAllLines(path,lines.ToArray(),new System.Text.UTF8Encoding(false));
        }
        private static string AssTime(double seconds)
        {
            long cs = (long)Math.Round(seconds * 100);
            return (cs/360000).ToString() + ":" + ((cs/6000)%60).ToString("00") + ":" + ((cs/100)%60).ToString("00") + "." + (cs%100).ToString("00");
        }
    }

    internal sealed class BridgePreparedExecution
    {
        public BridgeExecutionRequest ExecutionRequest { get; private set; }
        public bool NoReencode { get; private set; }
        public string ToolPath { get; private set; }
        public IList<string> InputFiles { get; private set; }

        public BridgePreparedExecution(BridgeExecutionRequest executionRequest, bool noReencode, string toolPath, IList<string> inputFiles)
        {
            ExecutionRequest = executionRequest;
            SharedGrainCore.Attach(executionRequest,noReencode);
            NoReencode = noReencode;
            ToolPath = toolPath ?? "";
            InputFiles = inputFiles;
        }
    }

    // Task construction for direct Native requests. Name retained for source/build compatibility.
    internal static class BridgeRequestPreparationCore
    {
        public static BridgeTaskRequest CreateTask(BridgePreparedExecution prepared, double recommendedOutputFps)
        {
            if (prepared == null) throw new ArgumentNullException("prepared");
            if (prepared.ExecutionRequest == null) throw new ArgumentException("ExecutionRequest is required.", "prepared");

            NativeProcessPolicy.ValidateRequest(prepared.ExecutionRequest);
            BridgeTaskRequest taskRequest = new BridgeTaskRequest();
            taskRequest.NativeBackend = true;
            taskRequest.ExecutionRequest = prepared.ExecutionRequest;
            taskRequest.NoReencode = prepared.NoReencode;
            taskRequest.RecommendedOutputFps = recommendedOutputFps;
            return taskRequest;
        }
    }
    // Hardware decoding returns normal CPU frames to preserve the existing filter graph.
    internal static class HardwareDecodeCore
    {
        private static string Get(IDictionary<string,string> s,string k) {string v;return s!=null&&s.TryGetValue(k,out v)?v??"":"";}
        internal static string FileSuffix(IDictionary<string,string> s) {return Get(s,"FG_HWD_PLAN").Length>0?"_HWD_PLAN_"+Get(s,"FG_HWD_PLAN"):Get(s,"FG_HWD_BACKEND").Length>0?"_HWD":"";}
        internal static string ProducerBackend(string input,IDictionary<string,string> s)
        {return Same(input,Get(s,"FG_HWD_SOURCE"))?Get(s,"FG_HWD_BACKEND"):"";}
        private static bool Same(string a,string b)
        {if(string.IsNullOrEmpty(a)||string.IsNullOrEmpty(b))return false;try{return string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);}catch{return false;}}
        internal static string Quote(string s)
        {
            var b=new System.Text.StringBuilder("\"");int slashes=0;
            foreach(char c in s){if(c=='\\'){slashes++;continue;}if(c=='"'){b.Append('\\',slashes*2+1);b.Append(c);}else {b.Append('\\',slashes);b.Append(c);}slashes=0;}
            b.Append('\\',slashes*2);return b.Append('"').ToString();
        }
        private sealed class Token {internal string Value;internal int Start;}
        private static List<Token> Tokens(string args)
        {
            var result=new List<Token>();int i=0;
            while(i<args.Length){while(i<args.Length&&char.IsWhiteSpace(args[i]))i++;if(i==args.Length)break;
                int start=i;bool quoted=false;var b=new System.Text.StringBuilder();
                while(i<args.Length&&(quoted||!char.IsWhiteSpace(args[i]))){int slashes=0;while(i<args.Length&&args[i]=='\\'){slashes++;i++;}
                    if(i<args.Length&&args[i]=='"'){b.Append('\\',slashes/2);if(slashes%2==1)b.Append('"');else quoted=!quoted;i++;}
                    else {b.Append('\\',slashes);if(i<args.Length&&(quoted||!char.IsWhiteSpace(args[i])))b.Append(args[i++]);}}
                result.Add(new Token{Value=b.ToString(),Start=start});}
            return result;
        }
        private static int SourceAt(string args,string source)
        {
            var t=Tokens(args);int inputIndex=0;
            // Source video is input zero in native FFmpeg stages. Later original inputs
            // supply audio/subtitles beside a pipe or processed workfile and must stay untouched.
            for(int i=0;i+1<t.Count;i++){
                if((t[i].Value=="-c:v"||t[i].Value=="-codec:v"||t[i].Value=="-c")&&t[i+1].Value=="copy")return -1;
            }
            for(int i=0;i+1<t.Count;i++)if(t[i].Value=="-i"){
                if(inputIndex==0&&Same(t[i+1].Value,source))return t[i].Start;inputIndex++;
            }
            return -1;
        }
        internal static string ApplyArguments(string tool,string args,IDictionary<string,string> state)
        {
            string backend=Get(state,"FG_HWD_BACKEND");
            if(backend.Length==0||!Path.GetFileName(tool).Equals("ffmpeg.exe",StringComparison.OrdinalIgnoreCase))return args;
            if(backend!="cuda"&&backend!="d3d11va")throw new InvalidOperationException("Invalid hardware decode backend");
            int at=SourceAt(args,Get(state,"FG_HWD_SOURCE"));
            return at<0?args:args.Insert(at,"-hwaccel "+backend+" ");
        }
        private static bool Run(string tool,string args,string appdata,out string detail)
        {
            detail="";var lines=new System.Text.StringBuilder();object gate=new object();
            try {using(var p=new System.Diagnostics.Process()){
                p.StartInfo=new System.Diagnostics.ProcessStartInfo(tool,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(tool)};
                if(!string.IsNullOrEmpty(appdata))p.StartInfo.EnvironmentVariables["APPDATA"]=appdata;
                var stdoutDone=new System.Threading.ManualResetEvent(false);var stderrDone=new System.Threading.ManualResetEvent(false);
                System.Diagnostics.DataReceivedEventHandler receive=delegate(object sender,System.Diagnostics.DataReceivedEventArgs e){if(e.Data!=null)lock(gate){if(lines.Length<262144)lines.AppendLine(e.Data);}};
                p.ErrorDataReceived+=delegate(object sender,System.Diagnostics.DataReceivedEventArgs e){if(e.Data==null)stderrDone.Set();else receive(sender,e);};
                p.OutputDataReceived+=delegate(object sender,System.Diagnostics.DataReceivedEventArgs e){if(e.Data==null)stdoutDone.Set();else receive(sender,e);};p.Start();p.BeginErrorReadLine();p.BeginOutputReadLine();
                // This probe owns no child processes. Bound indexing/driver stalls.
                if(!p.WaitForExit(20000)){try{p.Kill();}catch{}p.WaitForExit(2000);detail="probe timed out (20s)";return false;}
                // Async readers may still be draining after process exit; bounded joins only.
                stdoutDone.WaitOne(2000);stderrDone.WaitOne(2000);lock(gate)detail=lines.ToString();
                return p.ExitCode==0;
            }}catch(Exception ex){detail=ex.Message;return false;}
        }
        internal static bool Select(string root,string input,BridgeExecutionRequest request,IDictionary<string,string> state,
            out IDictionary<string,string> selected,out string notice)
        {
            selected=null;notice="[HWD] requested=off effective=software";
            if(Get(state,"FG_GPU_DECODE")!="1")return false;
            if(Get(state,"FG_MODE")=="AV1BAKE"){notice="[HWD] requested=on effective=software reason=AV1 bake requires libdav1d software film-grain synthesis";return false;}
            bool bestSource=false;var tools=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if(request.DirectStages!=null)foreach(var stage in request.DirectStages){
                if(!string.IsNullOrEmpty(stage.PipeProducerArguments)){
                    var a=Tokens(stage.PipeProducerArguments);foreach(var token in a)if(token.Value.StartsWith("input=",StringComparison.Ordinal)&&Same(token.Value.Substring(6),input))bestSource=true;
                }
                if(!string.IsNullOrEmpty(stage.DirectArguments)&&SourceAt(stage.DirectArguments,input)>=0&&Path.GetFileName(stage.ToolPath).Equals("ffmpeg.exe",StringComparison.OrdinalIgnoreCase))tools.Add(stage.ToolPath);
            }
            if(request.DirectStages==null||request.DirectStages.Count==0)if(SourceAt(request.DirectArguments??"",input)>=0&&Path.GetFileName(request.ToolPath).Equals("ffmpeg.exe",StringComparison.OrdinalIgnoreCase))tools.Add(request.ToolPath);
            if(!bestSource&&tools.Count==0){notice="[HWD] requested=on effective=software reason=no source-video decode stage";return false;}
            var errors=new List<string>();
            string sample="";
            try {
            if(bestSource){
                sample=Path.Combine(Path.GetTempPath(),"FGS_HWD_"+Guid.NewGuid().ToString("N")+".mkv");
                string ffmpeg=Path.Combine(new FgsConfig(root,null).Get("FFMPEG_DIR"),"ffmpeg.exe"), detail;
                if(!Run(ffmpeg,"-hide_banner -nostdin -v error -y -i "+Quote(input)+" -map 0:v:0 -t 2 -c copy -an -sn -dn "+Quote(sample),"",out detail)){
                    notice="[HWD] requested=on effective=software fallback=BestSource sample remux failed: "+detail;return false;
                }
            }
            foreach(string backend in new[]{"cuda","d3d11va"}) {
                bool ok=true;string detail="";
                foreach(string tool in tools){
                    bool decoded=Run(tool,"-hide_banner -nostdin -loglevel debug -hwaccel "+backend+" -i "+Quote(input)+" -map 0:v:0 -an -sn -dn -frames:v 1 -f null -","",out detail);
                    // Exit success alone can describe a software-only codec. Require FFmpeg's
                    // actual get_format hardware selection, not the requested command option.
                    string format=backend=="cuda"?"cuda":"d3d11";
                    if(!decoded||detail.IndexOf("Format "+format+" chosen by get_format",StringComparison.OrdinalIgnoreCase)<0){ok=false;break;}
                }
                if(ok&&bestSource){string dir=Path.Combine(root,"_OpenSVPFlow");
                    ok=Run(Path.Combine(dir,".venv","Scripts","vspipe.exe"),"--info --arg "+Quote("input="+sample)+" --arg "+Quote("plugin_dir="+Path.Combine(dir,"Plugins"))+" --arg hwd_probe=1 --arg hwdevice="+backend+" "+Quote(Path.Combine(dir,"FilmGrain_OpenSVPFlow.vpy"))+" -",File.Exists(Path.Combine(dir,"_UserConfig","vapoursynth","vapoursynth.toml"))?Path.Combine(dir,"_UserConfig"):"",out detail);
                    ok=ok&&detail.Contains("[HWD] effective=BestSource/"+backend);
                }
                if(ok){selected=new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase);selected["FG_HWD_BACKEND"]=backend;selected["FG_HWD_SOURCE"]=input;
                    notice="[HWD] requested=on effective="+(bestSource?"BestSource/":"FFmpeg/")+backend+" verified=first-frame; CPU-frame filter pipeline retained; output=_HWD";return true;}
                string flat=detail.Replace('\r',' ').Replace('\n',' ');if(flat.Length>1200)flat=flat.Substring(flat.Length-1200);
                errors.Add(backend+": "+flat);
            }
            notice="[HWD] requested=on effective=software fallback="+string.Join(" | ",errors.ToArray());return false;
            }catch(Exception ex){notice="[HWD] requested=on effective=software fallback=hardware probe failed: "+ex.Message;return false;}
            finally{if(sample.Length>0)try{File.Delete(sample);}catch{}}
        }
    }
}
