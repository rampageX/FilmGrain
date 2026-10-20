using System;
using System.Collections.Generic;
using System.Globalization;
namespace FilmGrainStudioPreview
{
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
        // Single-input no-grain encodes retain the common audio/video clock.
        // No FRUC/SVP/piped input enters this adapter.
        internal static List<string> Build(IList<string> original, string source, double common, double delay)
        {
            if(double.IsNaN(common)||double.IsInfinity(common)||double.IsNaN(delay)||double.IsInfinity(delay)||delay<0)
                throw new InvalidOperationException("普通转码时间轴无效 / Invalid ordinary encode timeline");
            var args=new List<string>(original);int input=args.IndexOf("-i"),filter=args.IndexOf("-filter_complex");
            if(input<0 || input+1>=args.Count || args[input+1]!=source || filter<0 || filter+1>=args.Count || !args[filter+1].Contains("[vout]"))
                throw new InvalidOperationException("普通转码输入映射或输出滤镜已改变 / Ordinary encode input/filter changed");
            for(int i=input+2;i<args.Count;i++)if(args[i]=="-i")throw new InvalidOperationException("Ordinary encode requires one input");
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
            var frozen=new List<string>(stage.OriginalArguments);
            var previous=stage.BeforeRunPreparation;
            stage.BeforeRunPreparation=delegate {
                string before=previous==null?"":previous();
                var time=SharedFrucCore.ProbeTimeline(ffprobe,source);
                stage.DirectArguments=SharedFrucCore.JoinArguments(Build(frozen,source,time.CommonOrigin,time.VideoDelay));
                return before+Environment.NewLine+"[Native ordinary timeline] source_common="+Number(time.CommonOrigin)+" video_origin="+Number(time.VideoOrigin)+" video_delay="+Number(time.VideoDelay)+"; no interpolation";
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
