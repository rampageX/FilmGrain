using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
namespace FilmGrainStudioPreview
{
    internal static class SharedHdrPreprocessCore
    {
        internal static List<string> BuildToSdr(string input,string output,MediaProbeInfo info,string tonemap,bool retainStreams,string deinterlaceFilter = "")
        {
            string range=string.Equals(info.ColorRange,"pc",StringComparison.OrdinalIgnoreCase)?"pc":"tv";
            string primaries=string.IsNullOrWhiteSpace(info.ColorPrimaries)||info.ColorPrimaries=="unknown"||info.ColorPrimaries=="reserved"?"bt2020":info.ColorPrimaries;
            string space=string.IsNullOrWhiteSpace(info.ColorSpace)||info.ColorSpace=="unknown"||info.ColorSpace=="reserved"?"bt2020nc":info.ColorSpace;
            deinterlaceFilter=ResolveDeinterlaceParity(deinterlaceFilter,info);
            string filter=deinterlaceFilter+"zscale=rin="+range+":pin="+primaries+":tin="+info.ColorTransfer+":min="+space+":t=linear:npl=100,format=gbrpf32le,tonemap=tonemap="+tonemap+":desat=2,zscale=p=bt709:t=bt709:m=bt709:r=tv,format=p010le,sidedata=mode=delete:type=MASTERING_DISPLAY_METADATA,sidedata=mode=delete:type=CONTENT_LIGHT_LEVEL,setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709";
            var args=new List<string>{"-hide_banner","-stats","-progress","pipe:2","-stats_period","0.5","-y","-copyts","-start_at_zero","-i",input,"-map","0:v:0"};
            if(retainStreams)args.AddRange(new[]{"-map","0:a?","-map","0:s?","-map","0:t?","-map_metadata","0","-map_chapters","0"});
            AddDeinterlaceDevice(args,deinterlaceFilter);
            args.AddRange(new[]{"-vf",filter});if(!retainStreams)args.AddRange(new[]{"-an","-sn","-dn"});
            args.AddRange(new[]{"-c:v","hevc_nvenc","-profile:v","main10","-pix_fmt","p010le","-preset","p4","-tune","lossless","-fps_mode","passthrough","-color_range","tv","-color_primaries","bt709","-color_trc","bt709","-colorspace","bt709"});
            if(retainStreams)args.AddRange(new[]{"-c:a","copy","-c:s","copy","-c:t","copy"});
            args.Add(output);return args;
        }

        // GPU default is resolved immediately before execution, never while merely planning.
        // The original CPU command remains intact for unsupported devices/builds/algorithms.
        internal static void AttachAutomatic(BridgeExecutionStage stage,string tonemap)
        {
            if(stage==null)throw new ArgumentNullException("stage");
            var cpu=new List<string>(stage.OriginalArguments);
            Func<string> previous=stage.BeforeRunPreparation;
            stage.BeforeRunPreparation=delegate {
                string prior=previous==null?"":previous();
                string notice;
                var selected=SelectAutomatic(stage.ToolPath,cpu,tonemap,out notice);
                stage.DirectArguments=SharedFrucCore.JoinArguments(selected);
                return (string.IsNullOrEmpty(prior)?"":prior+"\n")+notice;
            };
        }
        internal static List<string> BuildGpuArguments(IList<string> cpu)
        {
            var args=new List<string>(cpu);
            int vf=args.IndexOf("-vf"),input=args.IndexOf("-i");
            if(vf<0||vf+1>=args.Count||input<0)throw new InvalidOperationException("HDR command layout changed");
            string filter=args[vf+1];int linear=filter.IndexOf("zscale=rin=",StringComparison.Ordinal);
            int clean=filter.IndexOf("sidedata=mode=delete:type=MASTERING_DISPLAY_METADATA",StringComparison.Ordinal);
            if(linear<0||clean<linear)throw new InvalidOperationException("HDR CPU filter layout changed");
            string deint=filter.Substring(0,linear);
            var source=System.Text.RegularExpressions.Regex.Match(filter.Substring(linear),@"^zscale=rin=([^:]+):pin=([^:]+):tin=([^:]+):min=([^:]+):");
            if(!source.Success)throw new InvalidOperationException("HDR source color layout changed");
            string sourceTags="setparams=range="+source.Groups[1].Value+":color_primaries="+source.Groups[2].Value+":color_trc="+source.Groups[3].Value+":colorspace="+source.Groups[4].Value+",";
            args[vf+1]=sourceTags+deint+"format=p010le,hwupload,libplacebo=tonemapping=hable:peak_detect=1:colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv:format=p010le:contrast_recovery=0:gamut_mode=clip,hwdownload,format=p010le,"+filter.Substring(clean);
            // A Vulkan deinterlace stage already supplies a filter device; reuse it.
            if(!args.Contains("-filter_hw_device"))args.InsertRange(input,new[]{"-init_hw_device","vulkan=hdrvk:0","-filter_hw_device","hdrvk"});
            return args;
        }
        internal static List<string> SelectAutomatic(string ffmpeg,IList<string> cpu,string tonemap,out string notice)
        {
            if(!string.Equals(tonemap,"hable",StringComparison.OrdinalIgnoreCase))
            {
                notice="[HDR preprocess] CPU fallback: selected algorithm="+tonemap+"; GPU Hable does not override the selected algorithm.";
                return new List<string>(cpu);
            }
            // Command construction errors are programming errors, not missing GPU support.
            var gpu=BuildGpuArguments(cpu);
            var probe=new List<string>{"-hide_banner","-nostdin","-loglevel","verbose"};
            foreach(string option in new[]{"-init_hw_device","-filter_hw_device"})
                for(int i=0;i+1<gpu.Count;i++)if(gpu[i]==option)probe.AddRange(new[]{option,gpu[i+1]});
            probe.AddRange(new[]{"-i",gpu[gpu.IndexOf("-i")+1],"-map","0:v:0","-vf",gpu[gpu.IndexOf("-vf")+1],
                "-frames:v","3","-an","-sn","-dn","-c:v","hevc_nvenc","-profile:v","main10","-pix_fmt","p010le",
                "-preset","p4","-tune","lossless","-fps_mode","passthrough","-f","null","-"});
            string output,reason;
            bool supported=SharedFrucCore.Run(ffmpeg,SharedFrucCore.JoinArguments(probe),30000,out output,out reason);
            bool software=false;
            foreach(string line in output.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries))
            {
                string lower=line.ToLowerInvariant();
                if((lower.Contains("device name:")||lower.Contains("using device:")||lower.Contains(" selected:")) &&
                    (lower.Contains("llvmpipe")||lower.Contains("lavapipe")||lower.Contains("swiftshader")||lower.Contains("software")))software=true;
            }
            if(supported&&!software)
            {
                notice="[HDR preprocess] GPU Vulkan/libplacebo Hable / peak_detect=1 / gamut_mode=clip / contrast_recovery=0; actual source/filter/NVENC capability check passed.";
                return gpu;
            }
            string detail=software?"software Vulkan selected":reason;
            // Include the diagnostic output, bounded by the existing process helper (64 KiB).
            notice="[HDR preprocess] CPU fallback: GPU capability check unavailable or failed.\n"+detail;
            return new List<string>(cpu);
        }

        // A dedicated process owns the deinterlace device; the grain process uploads anew.
        internal static string DeinterlaceFilter(string method,MediaProbeInfo info)
        {
            return ResolveDeinterlaceParity(DeinterlaceFilter(method),info);
        }
        internal static string ResolveDeinterlaceParity(string filter,MediaProbeInfo info)
        {
            if(info==null || !info.IsInterlaced || info.ContainerFieldOrder!=info.FieldOrder)return filter;
            string parity=(info.FieldOrder=="tt" || info.FieldOrder=="tb")?"tff":"bff";
            return filter.Replace("parity=auto","parity="+parity);
        }
        internal static string DeinterlaceFilter(string method)
        {
            if(string.Equals(method,"BWDIF_VULKAN",StringComparison.OrdinalIgnoreCase))return "format=p010le,hwupload,bwdif_vulkan=mode=send_field:parity=auto:deint=all,hwdownload,format=p010le,setfield=prog,";
            if(string.Equals(method,"BWDIF_CUDA",StringComparison.OrdinalIgnoreCase))return "format=p010le,hwupload_cuda=device=0,bwdif_cuda=mode=send_field:parity=auto:deint=all,hwdownload,format=p010le,setfield=prog,";
            if(string.Equals(method,"W3FDIF",StringComparison.OrdinalIgnoreCase))return "w3fdif=filter=complex:mode=field:parity=auto:deint=all,setfield=prog,";
            throw new ArgumentException("Unsupported deinterlace method: "+method);
        }
        static void AddDeinterlaceDevice(List<string> args,string filter)
        {
            if(filter.Contains("bwdif_vulkan="))args.InsertRange(args.IndexOf("-i"),new[]{"-init_hw_device","vulkan=deintvk:0","-filter_hw_device","deintvk"});
        }
        internal static List<string> BuildDeinterlaced(string input,string output,MediaProbeInfo info,string filter)
        {
            filter=ResolveDeinterlaceParity(filter,info);
            var args=new List<string>{"-hide_banner","-stats","-progress","pipe:2","-stats_period","0.5","-y","-copyts","-start_at_zero","-i",input,
                "-map","0:v:0","-map","0:a?","-map","0:s?","-map","0:t?","-map_metadata","0","-map_chapters","0",
                "-vf",filter+"format=yuv420p10le","-c:v","ffv1","-level","3","-pix_fmt","yuv420p10le","-fps_mode","passthrough","-c:a","copy","-c:s","copy","-c:t","copy"};
            AddDeinterlaceDevice(args,filter);
            if(info.IsHdr)args.AddRange(new[]{"-color_range",info.ColorRange=="pc"?"pc":"tv","-color_primaries",Known(info.ColorPrimaries,"bt2020"),"-color_trc",info.ColorTransfer,"-colorspace",Known(info.ColorSpace,"bt2020nc")});
            args.Add(output);return args;
        }
        static string Known(string value,string fallback){return string.IsNullOrWhiteSpace(value)||value=="unknown"||value=="reserved"?fallback:value;}
        internal static string ValidateFieldWorkfile(string ffprobe,string path,MediaProbeInfo original,bool toSdr,bool doubleFields = true)
        {
            bool requireProgressive=doubleFields||!original.IsInterlaced;
            if(toSdr)ValidateWorkfile(ffprobe,path,original,requireProgressive);
            if(!File.Exists(path)||new FileInfo(path).Length==0)throw new IOException("反交错中间文件缺失或为空。");
            string json,error;
            if(!SharedFrucCore.Run(ffprobe,SharedFrucCore.JoinArguments(new[]{"-v","error","-show_streams","-show_format","-of","json",path}),15000,out json,out error))throw new IOException("反交错中间文件探测失败："+error);
            MediaProbeInfo actual=MediaProbeInfo.Parse(path,json);
            return ValidateFieldProperties(actual,original,toSdr,doubleFields);
        }
        // OFF preserves source cadence and may retain field flags; AUTO still requires progressive bob output.
        internal static string ValidateFieldProperties(MediaProbeInfo actual,MediaProbeInfo original,bool toSdr,bool doubleFields)
        {
            bool requireProgressive=doubleFields||!original.IsInterlaced;
            if(toSdr)ValidateSdrProperties(actual,original,requireProgressive);
            double expected=BitrateRecommendationCore.ParseMediaFps(original)*(doubleFields?2.0:1.0),rate=BitrateRecommendationCore.ParseMediaFps(actual);
            if(!actual.HasVideo||(requireProgressive&&actual.IsInterlaced)||actual.Width!=original.Width||actual.Height!=original.Height||rate<=0||Math.Abs(rate-expected)>0.01||
                (actual.PixelFormat!="yuv420p10le"&&actual.PixelFormat!="p010le")||original.HasAudio&&!actual.HasAudio||
                (!toSdr&&original.IsHdr&&(actual.ColorTransfer!=original.ColorTransfer||actual.ColorPrimaries!=Known(original.ColorPrimaries,"bt2020")||actual.ColorSpace!=Known(original.ColorSpace,"bt2020nc"))))
                throw new IOException("视频中间文件未满足原尺寸、10-bit、"+(requireProgressive?"逐行、":"OFF 源帧率、")+"所请求帧率、音频/HDR 标记条件；任务停止。");
            return "[Native field preprocess] validated "+(requireProgressive?"progressive ":"OFF field flags="+actual.FieldOrder+" / ")+"10-bit / "+(doubleFields?"2x field rate=":"OFF source frame rate=")+Number(rate)+(toSdr?" / BT.709 SDR":" / source HDR preserved");
        }
        internal static void AttachFieldValidation(BridgeExecutionStage stage,string ffprobe,string path,MediaProbeInfo original,bool toSdr,bool doubleFields = true)
        {
            Func<string> next=stage.BeforeRunPreparation;
            stage.BeforeRunPreparation=delegate {string notice=ValidateFieldWorkfile(ffprobe,path,original,toSdr,doubleFields);return notice+(next==null?"":"\n"+next());};
        }
        internal static void AttachFieldTimeline(BridgeExecutionStage stage,string ffprobe,string path,MediaProbeInfo original,bool toSdr,bool doubleFields = true)
        {
            var frozen=new List<string>(stage.OriginalArguments);
            stage.BeforeRunPreparation=delegate {
                string notice=ValidateFieldWorkfile(ffprobe,path,original,toSdr,doubleFields);
                var time=SharedFrucCore.ProbeTimeline(ffprobe,path);
                var args=BuildFieldTimeline(frozen,time.CommonOrigin,time.VideoDelay,toSdr);
                stage.DirectArguments=SharedFrucCore.JoinArguments(args);
                return notice+"\n[Native field timeline] common="+Number(time.CommonOrigin)+" video_delay="+Number(time.VideoDelay);
            };
        }
        internal static List<string> BuildFieldTimeline(IList<string> original,double common,double delay,bool toSdr)
        {
            if(double.IsNaN(common)||double.IsInfinity(common)||double.IsNaN(delay)||double.IsInfinity(delay)||delay<0)throw new InvalidOperationException("Invalid field timeline");
            var args=new List<string>(original);int filter=args.IndexOf("-filter_complex"),input=args.IndexOf("-i");
            if(filter<0||input<0||!args[filter+1].Contains("[vout]"))throw new InvalidOperationException("Field preprocess input/filter mapping changed");
            string graph=args[filter+1].Replace("[0:v:0]","[0:v:0]setpts=PTS-STARTPTS,");
            string tail=toSdr?",setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709":"";
            if(delay>0.000000001)tail+=",setpts=PTS+"+Number(delay)+"/TB";
            args[filter+1]=graph.Replace("[vout]",tail+"[vout]");
            for(int i=filter+2;i+1<args.Count;i++) {
                if(args[i]=="-r"){args.RemoveRange(i,2);i--;continue;}
                if(args[i]=="-fps_mode"||args[i]=="-fps_mode:v")args[i+1]="passthrough";
                if(args[i]=="-t"){double duration;if(double.TryParse(args[i+1],NumberStyles.Float,CultureInfo.InvariantCulture,out duration))args[i+1]=Number(duration+delay);}
            }
            args.InsertRange(input,new[]{"-copyts","-itsoffset",Number(-common)});
            if(toSdr)args.InsertRange(args.Count-1,new[]{"-color_range","tv","-color_primaries","bt709","-color_trc","bt709","-colorspace","bt709"});
            return args;
        }
        internal static string ValidateWorkfile(string ffprobe,string path,MediaProbeInfo original,bool requireProgressive = true)
        {
            if(!File.Exists(path)||new FileInfo(path).Length==0)throw new IOException("HDR 转 SDR 中间文件缺失或为空；不会交给 8-bit 插帧。");
            string json,error;
            if(!SharedFrucCore.Run(ffprobe,SharedFrucCore.JoinArguments(new[]{"-v","error","-select_streams","v:0","-show_streams","-of","json",path}),15000,out json,out error))throw new IOException("HDR 转 SDR 中间文件探测失败："+error);
            MediaProbeInfo actual=MediaProbeInfo.Parse(path,json);
            return ValidateSdrProperties(actual,original,requireProgressive);
        }
        internal static string ValidateSdrProperties(MediaProbeInfo actual,MediaProbeInfo original,bool requireProgressive)
        {
            if(!actual.HasVideo||actual.IsHdr||(requireProgressive&&actual.IsInterlaced)||actual.Width!=original.Width||actual.Height!=original.Height||actual.ColorTransfer!="bt709"||actual.ColorPrimaries!="bt709"||actual.ColorSpace!="bt709"||actual.ColorRange!="tv"||(actual.PixelFormat!="yuv420p10le"&&actual.PixelFormat!="p010le"))
                throw new IOException("HDR 转 SDR 中间文件未满足原尺寸、"+(requireProgressive?"逐行、":"")+"BT.709 TV / 10-bit 条件；任务停止。");
            return "[HDR→SDR] 中间文件已验证：BT.709 TV / 10-bit / "+(requireProgressive?"逐行":"反交错 OFF，允许保留场标记")+"。";
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
        internal static List<string> BuildUploadTimeline(IList<string> original,bool stampBt709 = true)
        {
            var args=new List<string>(original);int input=args.IndexOf("-i");
            if(input<0||input+1>=args.Count)throw new InvalidOperationException("HDR SVP 上传输入映射已改变");
            for(int i=input+2;i+1<args.Count;i++)
                if(args[i]=="-r"){args.RemoveRange(i,2);i--;}
                else if(args[i]=="-fps_mode"||args[i]=="-fps_mode:v"){args.RemoveRange(i,2);i--;}
            args.InsertRange(0,new[]{"-copyts","-start_at_zero"});
            args.InsertRange(args.Count-1,new[]{"-fps_mode:v","passthrough"});
            if(stampBt709)args.InsertRange(args.Count-1,new[]{"-color_range","tv","-color_primaries","bt709","-color_trc","bt709","-colorspace","bt709"});
            return args;
        }
        static string Number(double value){return value.ToString("0.#########",CultureInfo.InvariantCulture);}
    }
}

namespace FilmGrainStudioPreview
{
    // Reuse a completed main file without placing it in the output-cleanup list.
    internal static class SharedExistingUploadCore
    {
        internal static MediaProbeInfo ValidateMain(string ffprobe,string path,string codec,double expectedFps,MediaProbeInfo source)
        {
            if(!File.Exists(path)||new FileInfo(path).Length==0)throw new IOException("existing main output is missing or empty: "+path);
            string json,error;
            if(!SharedFrucCore.Run(ffprobe,SharedFrucCore.JoinArguments(new[]{"-v","error","-show_streams","-show_format","-of","json",path}),15000,out json,out error))
                throw new IOException("existing main output probe failed: "+error);
            var actual=MediaProbeInfo.Parse(path,json);
            double rate=BitrateRecommendationCore.ParseMediaFps(actual),a,b;
            if(!actual.HasVideo || actual.VideoCodec!=codec || actual.IsHdr || actual.IsInterlaced || actual.Width<=0 || actual.Height<=0 ||
                source.HasAudio&&!actual.HasAudio || rate<=0 || Math.Abs(rate-expectedFps)>0.03 ||
                double.TryParse(source.Duration,NumberStyles.Float,CultureInfo.InvariantCulture,out a)&&double.TryParse(actual.Duration,NumberStyles.Float,CultureInfo.InvariantCulture,out b)&&Math.Abs(a-b)>0.5)
                throw new IOException("existing main output does not match the requested SDR upload route: "+path);
            return actual;
        }
        static double ParseDuration(string text){double value;return double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out value)?value:0;}
        internal static bool TryPrepare(string ffmpeg,string ffprobe,string originalInput,string main,string output,string tempRoot,
            IDictionary<string,string> state,string passMode,string passlog,MediaProbeInfo source,double fps,
            Func<int,string,List<string>> buildArgs,out BridgePreparedExecution prepared,out string reason)
        {
            prepared=null;reason="";
            try
            {
                if(!File.Exists(ffmpeg)||!File.Exists(ffprobe))throw new IOException("existing main upload requires ffmpeg.exe and ffprobe.exe");
                var actual=ValidateMain(ffprobe,main,"av1",fps,source);
                var existing=new FileInfo(main);long length=existing.Length;DateTime modified=existing.LastWriteTimeUtc;
                int count=passMode=="3PASS"?3:passMode=="2PASS"?2:1;
                var req=new BridgeExecutionRequest{ToolPath=ffmpeg,WorkingDirectory=tempRoot,DirectProcess=true,
                    InputFiles=new List<string>{main},OutputFiles=new List<string>{output},TemporaryFiles=new List<string>(),
                    Environment=new Dictionary<string,string>(state,StringComparer.OrdinalIgnoreCase),
                    ProgressDurationSeconds=ParseDuration(actual.Duration),
                    ExecutionNotice="[Native .NET] 已有 AV1 主输出已验证，跳过主编码/颗粒/封装，仅补生成上传副本。 / Existing AV1 main output reused; upload only."};
                if(count>1)foreach(string suffix in new[]{"-0.log","-0.log.mbtree","-0.mbtree","-0.log.temp","-0.log.mbtree.temp"})req.TemporaryFiles.Add(passlog+suffix);
                var stages=new List<BridgeExecutionStage>();
                var passes=count==1?new[]{0}:count==2?new[]{1,2}:new[]{1,3,2};
                foreach(int pass in passes)
                {
                    var args=buildArgs(pass,passlog);
                    // The output is new; never overwrite a file that appeared after planning.
                    int overwrite=args.IndexOf("-y");if(overwrite>=0)args[overwrite]="-n";
                    var stage=new BridgeExecutionStage{ToolPath=ffmpeg,WorkingDirectory=tempRoot,OriginalArguments=args,
                        DirectArguments=SharedFrucCore.JoinArguments(args),ProgressDurationSeconds=req.ProgressDurationSeconds,
                        StageCurrent=stages.Count+1,StageTotal=count,StageText=pass==0||pass==2?"H.264 upload final encode (existing AV1 main)":"H.264 upload pass "+pass+" (existing AV1 main)"};
                    stage.BeforeRunPreparation=delegate {
                        var current=new FileInfo(main);
                        if(!current.Exists || current.Length!=length || current.LastWriteTimeUtc!=modified)throw new IOException("existing main output changed after upload preparation: "+main);
                        return "[Native reuse] existing main output unchanged; upload stage only";
                    };
                    stages.Add(stage);
                }
                req.DirectStages=stages;prepared=new BridgePreparedExecution(req,false,ffmpeg,new List<string>{originalInput});return true;
            }
            catch(Exception ex){reason="existing main upload preparation failed: "+ex.Message;return false;}
        }
    }
}

namespace FilmGrainStudioPreview
{
    // Kept in an existing compiled core file so GUI, CLI and release builds share it.
    internal sealed class CinematicFramePlan
    {
        internal string Filter = "";
        internal string FileSuffix = "";
        internal int ActiveWidth, ActiveHeight;
        internal int ContentX, ContentY, ContentWidth, ContentHeight;
    }
    internal static class CinematicFrameCore
    {
        private static string Get(IDictionary<string,string> state,string key)
        { string value;return state!=null && state.TryGetValue(key,out value)?value??"":""; }
        internal static bool TryBuild(MediaProbeInfo info, IDictionary<string,string> state, out CinematicFramePlan plan, out string reason)
        {
            plan = new CinematicFramePlan();
            plan.ActiveWidth = info.Width;
            plan.ActiveHeight = info.Height;
            plan.ContentWidth = info.Width; plan.ContentHeight = info.Height;
            reason = "";
            if (Get(state, "FG_CINEMATIC_FRAME") != "1" || Get(state,"FG_NO_CROP")=="1") return true;
            int horizontal;
            int.TryParse(Get(state,"FG_CROP_HORIZONTAL"),NumberStyles.Integer,CultureInfo.InvariantCulture,out horizontal);
            if(horizontal>0)
            {
                int vertical;int.TryParse(Get(state,"FG_CROP_PER_SIDE"),NumberStyles.Integer,CultureInfo.InvariantCulture,out vertical);
                vertical=Math.Max(0,vertical);
                if(horizontal>(info.Width-2)/2||vertical>(info.Height-2)/2){reason="custom crop is too large for input dimensions";return false;}
                bool cutting=Get(state,"FG_FRAME_MODE").ToUpperInvariant()=="CROP";
                int w=info.Width-horizontal*2,h=info.Height-vertical*2;
                plan.ContentX=horizontal;plan.ContentY=vertical;plan.ContentWidth=w;plan.ContentHeight=h;
                string x=horizontal.ToString(CultureInfo.InvariantCulture),y=vertical.ToString(CultureInfo.InvariantCulture);
                plan.FileSuffix=(cutting?"_CROP":"_BARS")+"V"+y+"H"+x;
                if(cutting){plan.ActiveWidth=w;plan.ActiveHeight=h;plan.Filter=",crop=w="+w.ToString(CultureInfo.InvariantCulture)+":h="+h.ToString(CultureInfo.InvariantCulture)+":x="+x+":y="+y+":exact=1";}
                else {
                    plan.Filter=",drawbox=x=0:y=0:w="+x+":h=ih:color=black:t=fill,drawbox=x=iw-"+x+":y=0:w="+x+":h=ih:color=black:t=fill";
                    if(vertical>0)plan.Filter+=",drawbox=x=0:y=0:w=iw:h="+y+":color=black:t=fill,drawbox=x=0:y=ih-"+y+":w=iw:h="+y+":color=black:t=fill";
                }
                return true;
            }

            int custom = 0;
            int.TryParse(Get(state, "FG_CROP_PER_SIDE"), NumberStyles.Integer, CultureInfo.InvariantCulture, out custom);
            if (custom < 0) custom = 0;
            string mode = Get(state, "FG_FRAME_MODE").ToUpperInvariant();
            if (mode == "CROP")
            {
                plan.FileSuffix = custom > 0 ? "_CROP" + custom.ToString(CultureInfo.InvariantCulture) : "_239";
                if (custom > 0)
                {
                    int target = info.Height - custom * 2;
                    if (target <= 1) { reason = "custom crop is too large for input height"; return false; }
                    plan.ActiveHeight = target;
                    plan.ContentY=custom;plan.ContentHeight=target;
                    plan.Filter = ",crop=w=iw:h=" + target.ToString(CultureInfo.InvariantCulture) + ":x=0:y=" + custom.ToString(CultureInfo.InvariantCulture) + ":exact=1";
                    return true;
                }
                long aspectLeft = (long)info.Width * 100L;
                long aspectRight = (long)info.Height * 239L;
                if (aspectLeft >= aspectRight) return true;
                int targetH = (int)(((long)info.Width * 100L + 239L) / 478L) * 2;
                if (targetH >= info.Height || targetH <= 0) return true;
                int cropY = (info.Height - targetH) / 2; cropY = (cropY / 2) * 2;
                plan.ActiveHeight = targetH;
                plan.ContentY=cropY;plan.ContentHeight=targetH;
                plan.Filter = ",crop=w=iw:h=" + targetH.ToString(CultureInfo.InvariantCulture) + ":x=0:y=" + cropY.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            plan.FileSuffix = custom > 0 ? "_BARS" + custom.ToString(CultureInfo.InvariantCulture) : "_239LB";
            if (custom > 0)
            {
                int content = info.Height - custom * 2;
                if (content <= 1) { reason = "custom black bars are too large for input height"; return false; }
                plan.ContentY=custom;plan.ContentHeight=content;
                plan.Filter = ",drawbox=x=0:y=0:w=iw:h=" + custom.ToString(CultureInfo.InvariantCulture) + ":color=black:t=fill,drawbox=x=0:y=ih-" + custom.ToString(CultureInfo.InvariantCulture) + ":w=iw:h=" + custom.ToString(CultureInfo.InvariantCulture) + ":color=black:t=fill";
                return true;
            }
            if ((long)info.Width * 100L >= (long)info.Height * 239L) return true;
            int barRaw = (int)(((long)info.Height * 239L - (long)info.Width * 100L) / 478L);
            int barH = (barRaw / 2) * 2;
            if (barH <= 0) return true;
            plan.ContentY=barH;plan.ContentHeight=info.Height-2*barH;
            plan.Filter = ",drawbox=x=0:y=0:w=iw:h=" + barH.ToString(CultureInfo.InvariantCulture) + ":color=black:t=fill,drawbox=x=0:y=ih-" + barH.ToString(CultureInfo.InvariantCulture) + ":w=iw:h=" + barH.ToString(CultureInfo.InvariantCulture) + ":color=black:t=fill";
            return true;
        }
    }
}
