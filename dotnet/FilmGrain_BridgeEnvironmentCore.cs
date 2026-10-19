using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FilmGrainStudioPreview
{
    // Phase 3.5.3 boundary:
    // This snapshot contains only normalized state needed to build the Bridge environment.
    // It intentionally has no WinForms, language-pack, config, or media-probe dependencies.
    internal sealed class BridgeEnvironmentSnapshot
    {
        public bool NoReencode;
        public bool GrainEnabled = true;
        public int Av1GrainStrength = 100;
        public int PixelGrainStrength = 100;

        public string TempMode = "";
        public string TempCustomDir = "";
        public string OutputMode = "";
        public string OutputCustomDir = "";
        public string Container = "MP4";

        public string Mode = "";
        public string Speed = "";
        public int SfeEngines;
        public string BitrateMode = "";
        public long Bitrate;
        public bool HighMotion;
        public string FpsMode = "";
        public bool SvpInterpolate;
        public string InterpolationEngine = "SVP";
        public string InterpolationTargetFps = "60";
        public string FrucPerf = "medium";
        public string FrucGrid = "auto";
        public string FrucFfmpegPath = "";
        public bool FrucUseMainFfmpeg = true;
        public string SvpAlgo = "";
        public string SvpAnalyse = "";
        public string SvpSceneMode = "";
        public string SvpMaskArea = "";
        public string SvpSpeed = "FGS";
        public string SvpTuning = "FGS";
        public string SvpBlockSize = "auto";
        public string SvpDedup = "0";
        public string SvpStill = "false";
        public string SvpStillLimit = "4.5";
        public string SvpStillEdge = "5";
        public string SvpStillTolerance = "30";
        public string SvpPeel = "false";
        public string SvpPeelLimit = "6";

        public string Deinterlace = "";
        public string DeintMethod = "";
        public bool CinematicFrame;
        public string FrameMode = "";
        public string CropPerSide = "";
        public string CropHorizontal = "";
        public string NoCrop = "";
        public bool H264High10;
        public string X264Preset = "";
        public string X264PassMode = "";
        public string AudioMode = "FGS", AudioBitrate = "256", Av1Multipass = "FGS", HevcMultipass = "FGS";
        public string HevcSpatialAq = "";
        public bool HevcTemporalAq;
        public string HdrPolicy = "";
        public string TonemapAlgo = "";

        public bool UploadEnabled;
        public string UploadBitrateMode = "";
        public long UploadBitrate;

        public bool ColorEnabled;
        public double ColorContrast;
        public double ColorBrightness;
        public double ColorSaturation;
        public double ColorGamma;
        public bool ColorBlackWhite;
        public bool LutEnabled;
        public string LutPath = "";
        public int LutStrength;

        public bool Av1;
        public int GrainMode;
        public int Av1FormatIndex;
        public int Av1StockIndex;
        public int Av1IsoValue;
        public bool Av1ChromaEnabled;
        public string Av1GrainTable = "";
        public int ProceduralStrength;
        public int FgsimPresetIndex;
        public string FgsimQuality = "VBR";
        public string GrainRoot = "";
        public string GrainPlatePath = "";
        public int ScannedGrainStrengthIndex;
        public int ScannedGrainStrengthPercent=80;

        public SubtitleState Subtitle = new SubtitleState();
        public string SourceGrainAction = "";
    }

    internal static class BridgeEnvironmentCore
    {
        internal static SortedDictionary<string, string> Build(BridgeEnvironmentSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            return snapshot.NoReencode ? BuildNoReencode(snapshot) : BuildEncode(snapshot);
        }

        private static SortedDictionary<string, string> BuildEncode(BridgeEnvironmentSnapshot snapshot)
        {
            long bitrate = snapshot.Bitrate;
            long uploadBitrate = snapshot.UploadBitrate;
            int grainMode = snapshot.GrainMode;
            bool av1 = snapshot.Av1;

            SortedDictionary<string, string> state = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            state["FG_STUDIO_MODE"] = "1";
            state["FG_AV1_GRAIN_STRENGTH"] = snapshot.Av1GrainStrength.ToString(CultureInfo.InvariantCulture);
            state["FG_PIXEL_GRAIN_STRENGTH"] = "100";
            state["FG_GRAIN_ENABLED"] = snapshot.NoReencode || snapshot.GrainEnabled ? "1" : "0";
            state["FG_TOOL_NO_PAUSE"] = "1";
            state["FG_TEMP_MODE"] = snapshot.TempMode;
            state["FG_TEMP_CUSTOM_DIR"] = snapshot.TempCustomDir;
            state["FG_TEMP_SPACE_CONFIRMED"] = "1";
            state["FG_OUTPUT_MODE"] = snapshot.OutputMode;
            state["FG_OUTPUT_CUSTOM_DIR"] = snapshot.OutputCustomDir;
            state["FG_OUTPUT_SPACE_CONFIRMED"] = "1";
            state["FG_KEEP_FAILED"] = "1";
            state["FG_MODE"] = snapshot.Mode;
            state["FG_CONTAINER"] = snapshot.Container;
            state["FG_AUDIO_MODE"] = snapshot.AudioMode;
            state["FG_AUDIO_BITRATE"] = snapshot.AudioBitrate;
            state["FG_AV1_MULTIPASS"] = snapshot.Av1Multipass;
            state["FG_HEVC_MULTIPASS"] = snapshot.HevcMultipass;
            state["FG_SPEED"] = snapshot.Speed;
            state["FG_SFE_ENGINES"] = Math.Max(0, snapshot.SfeEngines).ToString(CultureInfo.InvariantCulture);
            state["FG_BITRATE_MODE"] = snapshot.BitrateMode;
            state["FG_BITRATE"] = bitrate.ToString(CultureInfo.InvariantCulture);
            state["FG_MAXRATE"] = (bitrate * 3L).ToString(CultureInfo.InvariantCulture);
            state["FG_BUFSIZE"] = (bitrate * 6L).ToString(CultureInfo.InvariantCulture);
            state["FG_HIGH_MOTION"] = snapshot.HighMotion ? "1" : "0";
            state["FG_FPS_MODE"] = snapshot.FpsMode;
            state["FG_SVP_INTERPOLATE"] = snapshot.SvpInterpolate ? "1" : "0";
            state["FG_INTERPOLATION_ENGINE"] = snapshot.InterpolationEngine;
            state["FG_INTERPOLATION_TARGET_FPS"] = snapshot.InterpolationTargetFps;
            state["FG_FRUC_PERF"] = snapshot.FrucPerf;
            state["FG_FRUC_GRID"] = snapshot.FrucGrid;
            state["FG_FRUC_FFMPEG_PATH"] = snapshot.FrucFfmpegPath;
            state["FG_FRUC_USE_MAIN_FFMPEG"] = snapshot.FrucUseMainFfmpeg ? "1" : "0";
            state["FG_SVP_ALGO"] = snapshot.SvpAlgo;
            state["FG_SVP_ANALYSE"] = snapshot.SvpAnalyse;
            state["FG_SVP_SCENE_MODE"] = snapshot.SvpSceneMode;
            state["FG_SVP_MASK_AREA"] = snapshot.SvpMaskArea;
            state["FG_SVP_SPEED"] = snapshot.SvpSpeed;
            state["FG_SVP_TUNING"] = snapshot.SvpTuning;
            state["FG_SVP_BLOCK_SIZE"] = snapshot.SvpBlockSize;
            state["FG_SVP_DEDUP"] = snapshot.SvpDedup;
            state["FG_SVP_STILL"] = snapshot.SvpStill;
            state["FG_SVP_STILL_LIMIT"] = snapshot.SvpStillLimit;
            state["FG_SVP_STILL_EDGE"] = snapshot.SvpStillEdge;
            state["FG_SVP_STILL_TOLERANCE"] = snapshot.SvpStillTolerance;
            state["FG_SVP_PEEL"] = snapshot.SvpPeel;
            state["FG_SVP_PEEL_LIMIT"] = snapshot.SvpPeelLimit;

            state["FG_DEINTERLACE"] = snapshot.Deinterlace;
            state["FG_DEINT_METHOD"] = snapshot.DeintMethod;
            state["FG_CINEMATIC_FRAME"] = snapshot.CinematicFrame ? "1" : "0";
            state["FG_FRAME_MODE"] = snapshot.FrameMode;
            state["FG_CROP_PER_SIDE"] = snapshot.CropPerSide;
            state["FG_CROP_HORIZONTAL"] = snapshot.CropHorizontal;
            state["FG_NO_CROP"] = snapshot.NoCrop;
            state["FG_H264_HIGH10"] = snapshot.H264High10 ? "1" : "0";
            state["FG_X264_PRESET"] = snapshot.X264Preset;
            state["FG_X264_PASS_MODE"] = snapshot.X264PassMode;
            state["FG_HEVC_SPATIAL_AQ"] = snapshot.HevcSpatialAq;
            state["FG_HEVC_TEMPORAL_AQ"] = snapshot.HevcTemporalAq ? "1" : "0";
            state["FG_HDR_POLICY"] = snapshot.HdrPolicy;
            state["FG_TONEMAP_ALGO"] = snapshot.TonemapAlgo;
            state["FG_UPLOAD"] = snapshot.UploadEnabled ? "1" : "0";
            state["FG_UPLOAD_MODE"] = "X264";
            state["FG_UPLOAD_BITRATE_MODE"] = snapshot.UploadBitrateMode;
            state["FG_UPLOAD_BITRATE"] = uploadBitrate.ToString(CultureInfo.InvariantCulture);
            state["FG_UPLOAD_MAXRATE"] = (uploadBitrate * 3L).ToString(CultureInfo.InvariantCulture);
            state["FG_UPLOAD_BUFSIZE"] = (uploadBitrate * 6L).ToString(CultureInfo.InvariantCulture);
            state["FG_UPLOAD_HIGH_MOTION"] = snapshot.HighMotion ? "1" : "0";

            snapshot.Subtitle.WriteEnvironment(state);

            state["FG_COLOR_ENABLED"] = snapshot.ColorEnabled ? "1" : "0";
            state["FG_COLOR_CONTRAST"] = snapshot.ColorContrast.ToString("0.00", CultureInfo.InvariantCulture);
            state["FG_COLOR_BRIGHTNESS"] = snapshot.ColorBrightness.ToString("0.00", CultureInfo.InvariantCulture);
            state["FG_COLOR_SATURATION"] = snapshot.ColorSaturation.ToString("0.00", CultureInfo.InvariantCulture);
            state["FG_COLOR_GAMMA"] = snapshot.ColorGamma.ToString("0.00", CultureInfo.InvariantCulture);
            state["FG_COLOR_BLACK_WHITE"] = snapshot.ColorBlackWhite ? "1" : "0";
            if (snapshot.LutEnabled && snapshot.LutStrength>0)
            {
                state["FG_LUT_PATH"] = snapshot.LutPath;
                state["FG_LUT_STRENGTH"] = snapshot.LutStrength.ToString(CultureInfo.InvariantCulture);
            }

            string[] fgsimPresets = GrainControlCore.FgsimPresets;
            if (av1)
            {
                if (grainMode == 3)
                {
                    state["FG_GRAIN_ENGINE"] = "PROCEDURAL";
                    state["FG_PROC_STRENGTH"] = snapshot.ProceduralStrength.ToString(CultureInfo.InvariantCulture);
                }
                else if (grainMode == 4)
                {
                    state["FG_GRAIN_ENGINE"] = "FGSIM";
                    state["FG_FGSIM_PRESET"] = fgsimPresets[Math.Max(0, Math.Min(3, snapshot.FgsimPresetIndex))];
                }
                else
                {
                    string[] av1Modes = new string[] { "PRESET", "ISO", "TABLE" };
                    state["FG_GRAIN_ENGINE"] = "NATIVE";
                    state["FG_AV1_GRAIN_MODE"] = av1Modes[Math.Max(0, Math.Min(2, grainMode))];
                    state["FG_AV1_FORMAT"] = (snapshot.Av1FormatIndex + 1).ToString(CultureInfo.InvariantCulture);
                    state["FG_AV1_STOCK"] = (snapshot.Av1StockIndex + 1).ToString(CultureInfo.InvariantCulture);
                    state["FG_AV1_ISO"] = snapshot.Av1IsoValue.ToString(CultureInfo.InvariantCulture);
                    state["FG_AV1_CHROMA"] = snapshot.Av1ChromaEnabled ? "1" : "0";
                    if (grainMode == 2) state["FG_AV1_GRAIN_TABLE"] = snapshot.Av1GrainTable;
                }
            }
            else
            {
                if (grainMode == 0)
                {
                    state["FG_GRAIN_ENGINE"] = "PROCEDURAL";
                    state["FG_PROC_STRENGTH"] = snapshot.ProceduralStrength.ToString(CultureInfo.InvariantCulture);
                }
                else if (grainMode == 2)
                {
                    state["FG_GRAIN_ENGINE"] = "FGSIM";
                    state["FG_FGSIM_PRESET"] = fgsimPresets[Math.Max(0, Math.Min(3, snapshot.FgsimPresetIndex))];
                    state["FG_FGSIM_QUALITY"] = snapshot.FgsimQuality;
                }
                else
                {
                    state["FG_GRAIN_ENGINE"] = "NATIVE";
                    state["FG_GRAIN_ROOT"] = snapshot.GrainRoot;
                    state["FG_HEVC_GRAIN_PATH"] = snapshot.GrainPlatePath;
                    state["FG_HEVC_GRAIN_TAG"] = MakeGrainTag(snapshot.GrainPlatePath);
                    state["FG_HEVC_STRENGTH_SEL"] = (snapshot.ScannedGrainStrengthIndex + 1).ToString(CultureInfo.InvariantCulture);
                    state["FG_GRAIN_PLATE_STRENGTH"] = snapshot.ScannedGrainStrengthPercent.ToString(CultureInfo.InvariantCulture);
                }
            }
            if (!state.ContainsKey("FG_FGSIM_QUALITY")) state["FG_FGSIM_QUALITY"] = "VBR";
            return state;
        }

        private static SortedDictionary<string, string> BuildNoReencode(BridgeEnvironmentSnapshot snapshot)
        {
            int grainMode = snapshot.GrainMode;
            SortedDictionary<string, string> state = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            state["FG_STUDIO_MODE"] = "1";
            state["FG_AV1_GRAIN_STRENGTH"] = snapshot.Av1GrainStrength.ToString(CultureInfo.InvariantCulture);
            state["FG_PIXEL_GRAIN_STRENGTH"] = "100";
            state["FG_GRAIN_ENABLED"] = snapshot.NoReencode || snapshot.GrainEnabled ? "1" : "0";
            state["FG_TOOL_NO_PAUSE"] = "1";
            state["FG_TEMP_MODE"] = snapshot.TempMode;
            state["FG_TEMP_CUSTOM_DIR"] = snapshot.TempCustomDir;
            state["FG_TEMP_SPACE_CONFIRMED"] = "1";
            state["FG_OUTPUT_MODE"] = snapshot.OutputMode;
            state["FG_OUTPUT_CUSTOM_DIR"] = snapshot.OutputCustomDir;
            state["FG_OUTPUT_SPACE_CONFIRMED"] = "1";
            state["FG_CONTAINER"] = snapshot.Container;
            state["FG_AUDIO_MODE"] = snapshot.AudioMode;
            state["FG_AUDIO_BITRATE"] = snapshot.AudioBitrate;
            state["FG_AV1_MULTIPASS"] = snapshot.Av1Multipass;
            state["FG_HEVC_MULTIPASS"] = snapshot.HevcMultipass;
            string[] av1Modes = new string[] { "PRESET", "ISO", "TABLE" };
            state["FG_AV1_GRAIN_MODE"] = av1Modes[Math.Max(0, Math.Min(2, grainMode))];
            state["FG_AV1_FORMAT"] = (snapshot.Av1FormatIndex + 1).ToString(CultureInfo.InvariantCulture);
            state["FG_AV1_STOCK"] = (snapshot.Av1StockIndex + 1).ToString(CultureInfo.InvariantCulture);
            state["FG_AV1_ISO"] = snapshot.Av1IsoValue.ToString(CultureInfo.InvariantCulture);
            state["FG_AV1_CHROMA"] = snapshot.Av1ChromaEnabled ? "1" : "0";
            if (grainMode == 2) state["FG_AV1_GRAIN_TABLE"] = snapshot.Av1GrainTable;
            if (!string.IsNullOrEmpty(snapshot.SourceGrainAction)) state["FG_AV1_SOURCE_GRAIN_ACTION"] = snapshot.SourceGrainAction;
            return state;
        }

        private static string MakeGrainTag(string path)
        {
            string stem = Path.GetFileNameWithoutExtension(path) ?? "SCAN";
            StringBuilder sb = new StringBuilder(stem.Length);
            bool underscore = false;
            foreach (char c in stem)
            {
                if (char.IsLetterOrDigit(c)) { sb.Append(c); underscore = false; }
                else if (!underscore && sb.Length > 0) { sb.Append('_'); underscore = true; }
            }
            string tag = sb.ToString().Trim('_');
            if (tag.Length == 0) tag = "SCAN";
            if (tag.Length > 48) tag = tag.Substring(0, 48).TrimEnd('_');
            return tag;
        }
    }
    internal static class SharedGrainCore
    {
        // Missing setting means enabled, including state produced before this option existed.
        internal static bool IsEnabled(IDictionary<string,string> state)
        { string value; return state == null || !state.TryGetValue("FG_GRAIN_ENABLED",out value) || value != "0"; }
        internal static string Engine(IDictionary<string,string> state)
        { string value; return !IsEnabled(state) ? "NONE" : state != null && state.TryGetValue("FG_GRAIN_ENGINE",out value) ? value.ToUpperInvariant() : ""; }
        internal static void AddX264Tune(IList<string> args, bool enabled)
        { if(enabled) { args.Add("-tune"); args.Add("grain"); } }
        internal static string BuildWithoutGrain(string before, string lutEscaped, string opacity, string after)
        {
            if(string.IsNullOrEmpty(lutEscaped))return "[0:v:0]" + before + "format=p010le" + after + "[vout]";
            return LutStrengthCore.OptimizeGraph("[0:v:0]" + before + "format=gbrp16le,setpts=PTS-STARTPTS,split=2[lutorig][lutsrc];[lutsrc]lut3d=file='" +
                lutEscaped + "':interp=tetrahedral[lutgraded];[lutgraded][lutorig]blend=all_mode=normal:all_opacity=" +
                opacity + ",format=p010le" + after + "[vout]");
        }
        internal static bool ValidateHdr(MediaProbeInfo info, IDictionary<string,string> state, out string reason)
        {
            reason=""; string policy, color, lut;
            if(info==null || !info.IsHdr)return true;
            state.TryGetValue("FG_HDR_POLICY",out policy); state.TryGetValue("FG_COLOR_ENABLED",out color); state.TryGetValue("FG_LUT_PATH",out lut);
            if(string.Equals(policy,"PRESERVE",StringComparison.OrdinalIgnoreCase) && (color=="1" || !string.IsNullOrWhiteSpace(lut)))
            { reason="HDR 保留与当前 SDR 调色/LUT 不兼容；请选择 AUTO/转 SDR，或关闭调色/LUT。 / HDR Preserve conflicts with SDR color/LUT processing."; return false; }
            return true;
        }
        internal static void Attach(BridgeExecutionRequest request, bool noReencode)
        {
            if(request==null)return;
            bool enabled=noReencode || IsEnabled(request.Environment);
            request.ExecutionNotice=(request.ExecutionNotice ?? "") + Environment.NewLine + (enabled ? "颗粒：开启 / Grain: enabled" : "颗粒：关闭 / Grain: disabled (no grain removal)");
        }
    }

}

namespace FilmGrainStudioPreview
{
    internal sealed class AudioTrackInfo
    {
        public int StreamIndex;
        public string Codec = "";
        public long BitRate;
        public int Channels;
        public string SampleRate = "";
        public bool Estimated;
    }

    // Output audio policy is independent of the video encoder and container selector.
    internal static class AudioOutputCore
    {
        internal static string Get(IDictionary<string,string> state,string key,string fallback)
        { string v;return state!=null && state.TryGetValue(key,out v)&&!string.IsNullOrWhiteSpace(v)?v.ToUpperInvariant():fallback; }
        internal static List<AudioTrackInfo> Tracks(MediaProbeInfo info)
        {
            if(info==null)throw new InvalidOperationException("Audio source metadata is missing.");
            if(info.AudioTracks.Count>0)return info.AudioTracks;
            if(!info.HasAudio)return new List<AudioTrackInfo>();
            long rate;long.TryParse(info.AudioBitRate,out rate);
            return new List<AudioTrackInfo>{new AudioTrackInfo{Codec=info.AudioCodec,BitRate=rate,Channels=info.Channels,SampleRate=info.SampleRate}};
        }
        internal static bool Mp4Copy(string codec)
        {return codec=="aac"||codec=="mp3"||codec=="ac3"||codec=="eac3";}
        internal static void Validate(IDictionary<string,string> state)
        {
            string mode=Get(state,"FG_AUDIO_MODE","FGS"),rate=Get(state,"FG_AUDIO_BITRATE","256");
            if(mode!="FGS"&&mode!="SMART"&&mode!="COPY"&&mode!="AAC")throw new InvalidOperationException("Invalid audio mode: "+mode);
            if(rate!="SOURCE"&&rate!="128"&&rate!="160"&&rate!="192"&&rate!="256"&&rate!="320")throw new InvalidOperationException("Invalid AAC bitrate: "+rate);
        }
        internal static bool Copy(AudioTrackInfo track,string mode,bool mkv,bool stereo)
        {
            bool compatible=mkv||Mp4Copy(track.Codec);
            if(mode=="COPY") {
                if(!compatible)throw new InvalidOperationException("MP4 音频兼容策略不允许复制 "+track.Codec+"；请选择智能保留或 AAC。 / MP4 copy policy rejects this codec; choose Smart or AAC.");
                if(stereo&&track.Channels>2)throw new InvalidOperationException("上传/烘焙输出要求双声道，不能直接复制多声道音轨；请选择智能或 AAC。 / Stereo output requires audio conversion.");
                return true;
            }
            if(mode=="FGS")return mkv;
            return mode=="SMART"&&compatible&&(!stereo||track.Channels<=2);
        }
        internal static int Rate(AudioTrackInfo track,IDictionary<string,string> state)
        {
            string value=Get(state,"FG_AUDIO_BITRATE","256");if(value!="SOURCE")return int.Parse(value,CultureInfo.InvariantCulture);
            if(track.BitRate<=0)return track.Channels>2?384:track.Channels==1?64:128;
            int[] rates={32,40,48,56,64,80,96,112,128,160,192,224,256,320,384,448,512};
            double kb=track.BitRate/1000.0;int result=rates[0];foreach(int r in rates)if(Math.Abs(kb-r)<Math.Abs(kb-result))result=r;return result;
        }
        internal static void Apply(List<string> args,IDictionary<string,string> state,MediaProbeInfo source,bool mkv,bool stereo,bool firstOnly)
        {
            Validate(state);string mode=Get(state,"FG_AUDIO_MODE","FGS");
            if(mode=="FGS"&&Get(state,"FG_AUDIO_BITRATE","256")=="256")return; // Preserve every old argument by default.
            if(args.Contains("-an"))return;
            var tracks=Tracks(source);if(tracks.Count==0)return;
            for(int i=0;i<args.Count-1;i++)if(args[i]=="-c:a"||args[i]=="-b:a"||args[i]=="-ac"||args[i]=="-ar"){args.RemoveRange(i,2);i--;}
            var extra=new List<string>();int count=firstOnly?1:tracks.Count;
            for(int i=0;i<count;i++){
                var track=tracks[i];string spec=":a:"+i.ToString(CultureInfo.InvariantCulture);
                if(Copy(track,mode,mkv,stereo))extra.AddRange(new[]{"-c"+spec,"copy"});
                else {
                    extra.AddRange(new[]{"-c"+spec,"aac","-b"+spec,Rate(track,state).ToString(CultureInfo.InvariantCulture)+"k"});
                    if(stereo)extra.AddRange(new[]{"-ac"+spec,"2","-ar"+spec,"48000"});
                }
            }
            args.InsertRange(args.Count-1,extra);
        }
        internal static MediaProbeInfo Derived(MediaProbeInfo source,IDictionary<string,string> state,bool mkv)
        {
            var result=new MediaProbeInfo{HasAudio=source.HasAudio};string mode=Get(state,"FG_AUDIO_MODE","FGS");
            foreach(var t in Tracks(source))result.AudioTracks.Add(new AudioTrackInfo{StreamIndex=t.StreamIndex,Codec=Copy(t,mode,mkv,false)?t.Codec:"aac",BitRate=Copy(t,mode,mkv,false)?t.BitRate:Rate(t,state)*1000L,Channels=t.Channels,SampleRate=t.SampleRate,Estimated=t.Estimated});
            return result;
        }
        internal static void EstimateMissing(string path,string probe,MediaProbeInfo source,IDictionary<string,string> state)
        {
            Validate(state);if(Get(state,"FG_AUDIO_BITRATE","256")!="SOURCE"||Get(state,"FG_AUDIO_MODE","FGS")=="COPY")return;
            bool needed=false;foreach(var t in Tracks(source))if(t.BitRate<=0)needed=true;if(!needed)return;
            string output,error;var args=new List<string>{"-v","error","-select_streams","a","-read_intervals","%+#128","-show_packets","-show_entries","packet=stream_index,size,duration_time","-of","json",path};
            if(!SharedFrucCore.Run(probe,SharedFrucCore.JoinArguments(args),15000,out output,out error))return;
            try {
                var json=new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=4194304};var data=json.DeserializeObject(output) as Dictionary<string,object>;object raw;
                if(data==null||!data.TryGetValue("packets",out raw))return;
                var bytes=new Dictionary<int,double>();var seconds=new Dictionary<int,double>();
                foreach(object item in (System.Collections.IEnumerable)raw){var packet=item as Dictionary<string,object>;object index,size,duration;int n;double b,d;
                    if(packet==null||!packet.TryGetValue("stream_index",out index)||!packet.TryGetValue("size",out size)||!packet.TryGetValue("duration_time",out duration)||!int.TryParse(Convert.ToString(index),out n)||!double.TryParse(Convert.ToString(size),NumberStyles.Float,CultureInfo.InvariantCulture,out b)||!double.TryParse(Convert.ToString(duration),NumberStyles.Float,CultureInfo.InvariantCulture,out d)||d<=0)continue;
                    if(!bytes.ContainsKey(n)){bytes[n]=0;seconds[n]=0;}bytes[n]+=b;seconds[n]+=d;
                }
                foreach(var t in source.AudioTracks)if(t.BitRate<=0&&seconds.ContainsKey(t.StreamIndex)&&seconds[t.StreamIndex]>0){t.BitRate=(long)(bytes[t.StreamIndex]*8/seconds[t.StreamIndex]);t.Estimated=true;}
            }catch { } // Unknown rates use the documented channel-based fallback, never total file bitrate.
        }
        internal static string Notice(MediaProbeInfo source,IDictionary<string,string> state)
        {
            string mode=Get(state,"FG_AUDIO_MODE","FGS"),rate=Get(state,"FG_AUDIO_BITRATE","256");
            if(mode=="FGS"&&rate=="256")return "";
            var text=new StringBuilder("[Audio] mode="+mode+" AAC="+rate+"; copy keeps original bitrate; conservative MP4 copy codecs: AAC/MP3/AC3/EAC3.");int i=0;
            foreach(var t in Tracks(source)){text.Append(" a:"+(i++)+" "+t.Codec+" "+t.BitRate+"bps");if(rate=="SOURCE")text.Append(" -> AAC target="+Rate(t,state)+"k ("+(t.Estimated?"estimated from <=128 packets":t.BitRate>0?"source metadata":"unknown; channel-based fallback")+")");}
            return text.ToString();
        }
    }

    internal static class NvencMultipassCore
    {
        internal static void Apply(List<string> args,IDictionary<string,string> state,string codec,HardwareCapabilitySnapshot caps)
        {
            string choice=AudioOutputCore.Get(state,"FG_"+codec+"_MULTIPASS","FGS");if(choice=="FGS")return;
            if(choice!="DISABLED"&&choice!="QRES"&&choice!="FULLRES")throw new InvalidOperationException("Invalid NVENC multipass: "+choice);
            bool supported=choice=="DISABLED"||(caps!=null&&(codec=="AV1"?(choice=="QRES"?caps.Av1MultipassQres:caps.Av1MultipassFullres):(choice=="QRES"?caps.HevcMultipassQres:caps.HevcMultipassFullres)));
            if(!supported)throw new InvalidOperationException(codec+" NVENC 不支持所选 Multipass；请使用 FGS 推荐或支持的选项。 / Requested multipass unsupported.");
            for(int i=0;i<args.Count-1;i++)if(args[i]=="-multipass"){args.RemoveRange(i,2);i--;}
            args.AddRange(new[]{"-multipass",choice.ToLowerInvariant()});
        }
    }
}
