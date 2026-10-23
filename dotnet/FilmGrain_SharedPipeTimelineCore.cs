using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
namespace FilmGrainStudioPreview
{
    // One binding point for ordinary SDR encodes; no media is rendered or remuxed here.
    internal static class SharedInputTimelineCore
    {
        [ThreadStatic] internal static TimelineProbeScope CurrentScope;
        internal static TimelineProbeScope BeginScope() { return new TimelineProbeScope(); }
        internal static void Bind(BridgeExecutionRequest request,string source,string ffprobe,MediaProbeInfo info,bool noReencode)
        {
            if(request==null || noReencode || info==null || info.IsHdr || request.Environment==null)return;
            string mode,interpolate;
            request.Environment.TryGetValue("FG_MODE",out mode);
            if(mode!="SVT" && mode!="X265" && mode!="X264" && mode!="HEVC" && mode!="H264")return;
            request.Environment.TryGetValue("FG_SVP_INTERPOLATE",out interpolate);
            if(interpolate=="1")return; // FRUC/SVP retain their established clock adapters.
            if(request.DirectStages==null || request.DirectStages.Count==0)
                throw new InvalidOperationException("普通编码缺少执行阶段，无法绑定共享时间轴；任务停止 / Missing ordinary encode stage");
            int bound=0;
            foreach(var stage in request.DirectStages)
            {
                var args=stage.OriginalArguments;
                if(args==null || stage.FrucArguments!=null || !string.IsNullOrEmpty(stage.PipeProducerPath))continue;
                int input=args.IndexOf("-i"),graph=args.IndexOf("-filter_complex");
                // Preprocess/workfile stages own their existing timeline; never attach again.
                if(input<0 || input+1>=args.Count || !string.Equals(args[input+1],source,StringComparison.OrdinalIgnoreCase) ||
                    graph<0 || graph+1>=args.Count || !args[graph+1].Contains("[vout]"))continue;
                if(stage.OrdinaryTimelineBound)continue;
                SharedOrdinaryTimelineCore.Attach(stage,ffprobe,source);
                bound++;
            }
            if(bound>0)request.ExecutionNotice=(request.ExecutionNotice??"")+Environment.NewLine+
                "[Input timeline] ordinary SDR stages="+bound+"; one shared binding; no added media preprocess; source="+source;
        }
    }
    internal sealed class TimelineProbeScope : IDisposable
    {
        private readonly TimelineProbeScope previous;
        private readonly Dictionary<string,SharedFrucCore.FrucTimeline> cache=new Dictionary<string,SharedFrucCore.FrucTimeline>(StringComparer.OrdinalIgnoreCase);
        private int probes,hits;
        internal TimelineProbeScope() { previous=SharedInputTimelineCore.CurrentScope;SharedInputTimelineCore.CurrentScope=this; }
        internal SharedFrucCore.FrucTimeline Probe(string ffprobe,string source)
        {
            var identity=MediaProbeIdentity.Capture(source,ffprobe);
            string key=identity.Input+"|"+identity.Value;
            SharedFrucCore.FrucTimeline result;
            if(cache.TryGetValue(key,out result)){hits++;return result;}
            result=SharedFrucCore.ProbeTimelineUncached(ffprobe,source);
            identity.RequireCurrent();
            cache.Add(key,result);probes++;return result;
        }
        internal string Summary { get { return "[Timeline probes] unique="+probes+" reused="+hits+"; task-scoped; no media preprocessing added"; } }
        public void Dispose() { cache.Clear();SharedInputTimelineCore.CurrentScope=previous; }
    }

    internal static class SharedPipeTimelineCore
    {
        // Y4M cannot retain source PTS. Restore relative video delay after filters that reset PTS.
        internal static List<string> Build(IList<string> original,string source,double common,double delay,bool stampBt709)
        {
            if(double.IsNaN(common)||double.IsInfinity(common)||double.IsNaN(delay)||double.IsInfinity(delay)||delay<0)throw new InvalidOperationException("SVP pipe 时间轴无效");
            var args=new List<string>(original);int first=args.IndexOf("-i"),second=-1,filter=args.IndexOf("-filter_complex");
            for(int i=first+2;i<args.Count;i++)if(args[i]=="-i"){second=i;break;}
            if(first<0||first+1>=args.Count||args[first+1]!="pipe:0"||second<0||second+1>=args.Count||args[second+1]!=source||filter<0||filter+1>=args.Count||!args[filter+1].Contains("[vout]"))throw new InvalidOperationException("SVP pipe 输入映射或滤镜输出已改变");
            string tail=stampBt709?",setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709":"";
            if(delay>0.000000001)tail+=",setpts=PTS+round("+Number(delay)+"/TB)";
            args[filter+1]=args[filter+1].Replace("[vout]",tail+"[vout]");
            // Only output timing options are changed; the looping grain input keeps its own -t.
            for(int i=filter+2;i+1<args.Count;i++) {
                if(args[i]=="-r"){args.RemoveRange(i,2);i--;continue;}
                if(args[i]=="-fps_mode"||args[i]=="-fps_mode:v")args[i+1]="passthrough";
                if(args[i]=="-t"){double duration;if(double.TryParse(args[i+1],NumberStyles.Float,CultureInfo.InvariantCulture,out duration))args[i+1]=Number(duration+delay);}
            }
            args.InsertRange(second,new[]{"-itsoffset",Number(-common)});args.Insert(0,"-copyts");
            if(stampBt709)args.InsertRange(args.Count-1,new[]{"-color_range","tv","-color_primaries","bt709","-color_trc","bt709","-colorspace","bt709"});
            return args;
        }
        internal static void AttachTransportSource(BridgeExecutionStage stage,string ffprobe,string source,MediaProbeInfo info,IList<string> original)
        {
            var frozen=new List<string>(original);
            string range=info.ColorRange,primaries=info.ColorPrimaries,transfer=info.ColorTransfer,space=info.ColorSpace;
            stage.BeforeRunPreparation=delegate {
                SharedFrucCore.FrucTimeline time=SharedFrucCore.ProbeTimeline(ffprobe,source);
                var args=Build(frozen,source,time.CommonOrigin,time.VideoDelay,false);
                AddColor(args,"-color_range",range);
                AddColor(args,"-color_primaries",primaries);
                AddColor(args,"-color_trc",transfer);
                AddColor(args,"-colorspace",space);
                stage.DirectArguments=SharedFrucCore.JoinArguments(args);
                return "[Native SVP transport timeline] source_common="+Number(time.CommonOrigin)+" video_delay="+Number(time.VideoDelay)+"; Y4M video / original audio and subtitles; no remux required";
            };
        }
        private static void AddColor(List<string> args,string name,string value)
        {
            if(string.IsNullOrWhiteSpace(value)||value=="unknown"||value=="reserved"||value=="unspecified")return;
            args.InsertRange(args.Count-1,new[]{name,value});
        }
        private static string Number(double value){return value.ToString("0.#########",CultureInfo.InvariantCulture);}
    }
    internal static class SharedOrdinaryTimelineCore
    {
        // Ordinary encodes share one A/V clock, including grain and LUT filters.
        // No FRUC/SVP/piped input enters this adapter.
        internal static List<string> Build(IList<string> original, string source, double common, double delay)
        {
            if(double.IsNaN(common)||double.IsInfinity(common)||double.IsNaN(delay)||double.IsInfinity(delay)||delay<0)
                throw new InvalidOperationException("普通转码时间轴无效 / Invalid ordinary encode timeline");
            var args=new List<string>(original);int input=args.IndexOf("-i"),filter=args.IndexOf("-filter_complex");
            if(input<0 || input+1>=args.Count || args[input+1]!=source || filter<0 || filter+1>=args.Count || !args[filter+1].Contains("[vout]"))
                throw new InvalidOperationException("普通转码输入映射或输出滤镜已改变 / Ordinary encode input/filter changed");
            // Additional inputs are looping grain plates; only input zero owns audio.
            // Preserve relative PTS within the video, restoring its delay after LUT's local zero.
            args[filter+1]=args[filter+1].Replace("[vout]",",setpts=PTS-STARTPTS+"+Number(delay)+"/TB[vout]");
            for(int i=filter+2;i+1<args.Count;i++)
            {
                // Frame selection/field filters already establish the requested cadence.
                // -r with passthrough is contradictory and would pad the initial video gap.
                if(args[i]=="-r") {args.RemoveRange(i,2);i--;continue;}
                if(args[i]=="-fps_mode" || args[i]=="-fps_mode:v")args[i+1]="passthrough";
            }
            args.InsertRange(input,new[]{"-itsoffset",Number(-common)});
            args.Insert(0,"-copyts");
            return args;
        }
        internal static void Attach(BridgeExecutionStage stage, string ffprobe, string source)
        {
            if(stage==null || stage.OriginalArguments==null)throw new InvalidOperationException("Missing ordinary encode arguments");
            if(stage.OrdinaryTimelineBound)return;
            stage.OrdinaryTimelineBound=true;
            var frozen=new List<string>(stage.OriginalArguments);
            var previous=stage.BeforeRunPreparation;
            stage.BeforeRunPreparation=delegate {
                var time=SharedFrucCore.ProbeTimeline(ffprobe,source);
                stage.DirectArguments=SharedFrucCore.JoinArguments(Build(frozen,source,time.CommonOrigin,time.VideoDelay));
                // Device selection must patch the final timeline command, not be overwritten by it.
                string before=previous==null?"":previous();
                return before+Environment.NewLine+"[Native ordinary timeline] source_common="+Number(time.CommonOrigin)+" video_origin="+Number(time.VideoOrigin)+" video_delay="+Number(time.VideoDelay)+" audio_origins="+time.AudioOrigins+"; compensation=video_delay; audio_shift="+Number(-time.CommonOrigin)+"; source=probed input; no repair/remux";
            };
        }
        internal static void NormalizeVideoOnly(IList<string> args)
        {
            int filter=args.IndexOf("-filter_complex");
            if(filter<0 || filter+1>=args.Count || !args[filter+1].Contains("[vout]"))throw new InvalidOperationException("Missing video-only encode filter");
            args[filter+1]=args[filter+1].Replace("[vout]",",setpts=PTS-STARTPTS[vout]");
        }
        private static string Number(double value){return value.ToString("0.#########",CultureInfo.InvariantCulture);}
    }

}
