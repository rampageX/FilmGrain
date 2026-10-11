using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
namespace FilmGrainStudioPreview
{
    internal static class SharedHdrPreprocessCore
    {
        internal static List<string> BuildToSdr(string input,string output,MediaProbeInfo info,string tonemap,bool retainStreams)
        {
            string range=string.Equals(info.ColorRange,"pc",StringComparison.OrdinalIgnoreCase)?"pc":"tv";
            string primaries=string.IsNullOrWhiteSpace(info.ColorPrimaries)||info.ColorPrimaries=="unknown"||info.ColorPrimaries=="reserved"?"bt2020":info.ColorPrimaries;
            string space=string.IsNullOrWhiteSpace(info.ColorSpace)||info.ColorSpace=="unknown"||info.ColorSpace=="reserved"?"bt2020nc":info.ColorSpace;
            string filter="zscale=rin="+range+":pin="+primaries+":tin="+info.ColorTransfer+":min="+space+":t=linear:npl=100,format=gbrpf32le,tonemap=tonemap="+tonemap+":desat=2,zscale=p=bt709:t=bt709:m=bt709:r=tv,format=p010le,sidedata=mode=delete:type=MASTERING_DISPLAY_METADATA,sidedata=mode=delete:type=CONTENT_LIGHT_LEVEL,setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709";
            var args=new List<string>{"-hide_banner","-stats","-progress","pipe:2","-stats_period","0.5","-y","-copyts","-start_at_zero","-i",input,"-map","0:v:0"};
            if(retainStreams)args.AddRange(new[]{"-map","0:a?","-map","0:s?","-map","0:t?","-map_metadata","0","-map_chapters","0"});
            args.AddRange(new[]{"-vf",filter});if(!retainStreams)args.AddRange(new[]{"-an","-sn","-dn"});
            args.AddRange(new[]{"-c:v","hevc_nvenc","-profile:v","main10","-pix_fmt","p010le","-preset","p4","-tune","lossless","-fps_mode","passthrough","-color_range","tv","-color_primaries","bt709","-color_trc","bt709","-colorspace","bt709"});
            if(retainStreams)args.AddRange(new[]{"-c:a","copy","-c:s","copy","-c:t","copy"});
            args.Add(output);return args;
        }
        internal static string ValidateWorkfile(string ffprobe,string path,MediaProbeInfo original)
        {
            if(!File.Exists(path)||new FileInfo(path).Length==0)throw new IOException("HDR 转 SDR 中间文件缺失或为空；不会交给 8-bit 插帧。");
            string json,error;
            if(!SharedFrucCore.Run(ffprobe,SharedFrucCore.JoinArguments(new[]{"-v","error","-select_streams","v:0","-show_streams","-of","json",path}),15000,out json,out error))throw new IOException("HDR 转 SDR 中间文件探测失败："+error);
            MediaProbeInfo actual=MediaProbeInfo.Parse(path,json);
            if(!actual.HasVideo||actual.IsHdr||actual.IsInterlaced||actual.Width!=original.Width||actual.Height!=original.Height||actual.ColorTransfer!="bt709"||actual.ColorPrimaries!="bt709"||actual.ColorSpace!="bt709"||actual.ColorRange!="tv"||(actual.PixelFormat!="yuv420p10le"&&actual.PixelFormat!="p010le"))
                throw new IOException("HDR 转 SDR 中间文件未满足原尺寸、逐行、BT.709 TV / 10-bit 条件；任务停止。");
            return "[HDR→SDR] 中间文件已验证：BT.709 TV / 10-bit / 逐行；后续光流读取此文件。";
        }
        internal static void AttachValidation(BridgeExecutionStage stage,string ffprobe,string workfile,MediaProbeInfo original)
        {
            Func<string> next=stage.BeforeRunPreparation;
            stage.BeforeRunPreparation=delegate {string result=ValidateWorkfile(ffprobe,workfile,original);return result+(next==null?"":"\n"+next());};
        }
        internal static void AttachPipeTimeline(BridgeExecutionStage stage,string ffprobe,string workfile,MediaProbeInfo original,IList<string> originalArguments)
        {
            var frozen=new List<string>(originalArguments);
            stage.BeforeRunPreparation=delegate {
                string checkedFile=ValidateWorkfile(ffprobe,workfile,original);
                SharedFrucCore.FrucTimeline time=SharedFrucCore.ProbeTimeline(ffprobe,workfile);
                stage.DirectArguments=SharedFrucCore.JoinArguments(BuildPipeTimeline(frozen,workfile,time.CommonOrigin,time.VideoDelay));
                return checkedFile+"\n[HDR→SDR→SVP timeline] source_common="+Number(time.CommonOrigin)+" video_delay="+Number(time.VideoDelay)+"; video from Y4M, audio/subtitles from SDR workfile";
            };
        }
        internal static List<string> BuildPipeTimeline(IList<string> original,string workfile,double common,double delay)
        {
            return SharedPipeTimelineCore.Build(original,workfile,common,delay,true);
        }
        internal static List<string> BuildUploadTimeline(IList<string> original)
        {
            var args=new List<string>(original);int input=args.IndexOf("-i");
            if(input<0||input+1>=args.Count)throw new InvalidOperationException("HDR SVP 上传输入映射已改变");
            for(int i=input+2;i+1<args.Count;i++)
                if(args[i]=="-r"){args.RemoveRange(i,2);i--;}
                else if(args[i]=="-fps_mode"||args[i]=="-fps_mode:v"){args.RemoveRange(i,2);i--;}
            args.InsertRange(0,new[]{"-copyts","-start_at_zero"});
            args.InsertRange(args.Count-1,new[]{"-fps_mode:v","passthrough","-color_range","tv","-color_primaries","bt709","-color_trc","bt709","-colorspace","bt709"});
            return args;
        }
        static string Number(double value){return value.ToString("0.#########",CultureInfo.InvariantCulture);}
    }
}
