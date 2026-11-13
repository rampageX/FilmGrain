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
        internal static List<string> BuildGpuArguments(IList<string> cpu,string vulkanDevice = null)
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
            if(!args.Contains("-filter_hw_device"))args.InsertRange(input,new[]{"-init_hw_device",VulkanDevicePolicy.Spec("hdrvk"),"-filter_hw_device","hdrvk"});
            return args;
        }
        internal static List<string> SelectAutomatic(string ffmpeg,IList<string> cpu,string tonemap,out string notice,string vulkanDevice = null,SharedFrucCore.ProbeRunner runner = null)
        {
            if(!string.Equals(tonemap,"hable",StringComparison.OrdinalIgnoreCase))
            {
                notice="[HDR preprocess] CPU fallback: selected algorithm="+tonemap+"; GPU Hable does not override the selected algorithm.";
                return new List<string>(cpu);
            }
            // Command construction errors are programming errors, not missing GPU support.
            var gpu=BuildGpuArguments(cpu,vulkanDevice);
            var probe=new List<string>{"-hide_banner","-nostdin","-loglevel","verbose"};
            foreach(string option in new[]{"-init_hw_device","-filter_hw_device"})
                for(int i=0;i+1<gpu.Count;i++)if(gpu[i]==option)probe.AddRange(new[]{option,gpu[i+1]});
            probe.AddRange(new[]{"-i",gpu[gpu.IndexOf("-i")+1],"-map","0:v:0","-vf",gpu[gpu.IndexOf("-vf")+1],
                "-frames:v","3","-an","-sn","-dn","-c:v","hevc_nvenc","-profile:v","main10","-pix_fmt","p010le",
                "-preset","p4","-tune","lossless","-fps_mode","passthrough","-f","null","-"});
            string output,reason;
            if(runner==null)runner=SharedFrucCore.Run;
            bool supported=runner(ffmpeg,SharedFrucCore.JoinArguments(probe),30000,out output,out reason);
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

        // A single-input progressive HEVC encode can consume HDR directly. Color/LUT,
        // framing and subtitles remain downstream of the exact shared SDR conversion.

        internal static List<string> MergeHevcArguments(IList<string> original,IList<string> hdr)
        {
            var args=new List<string>(original);
            int graph=args.IndexOf("-filter_complex"),vf=IndexOf(hdr,"-vf"),input=args.IndexOf("-i");
            if(graph<0 || graph+1>=args.Count || vf<0 || vf+1>=hdr.Count || input<0 ||
                !args[graph+1].StartsWith("[0:v:0]",StringComparison.Ordinal))
                throw new InvalidOperationException("HDR merge input/filter layout changed");
            if(args.Contains("-filter_hw_device"))throw new InvalidOperationException("HDR merge already has a filter device");
            args[graph+1]="[0:v:0]"+hdr[vf+1]+","+args[graph+1].Substring("[0:v:0]".Length);
            var devices=new List<string>();
            foreach(string option in new[]{"-init_hw_device","-filter_hw_device"})
                for(int i=0;i+1<hdr.Count;i++)if(hdr[i]==option)devices.AddRange(new[]{option,hdr[i+1]});
            if(devices.Count>0)args.InsertRange(input,devices);
            args.InsertRange(args.Count-1,new[]{"-color_range","tv","-color_primaries","bt709","-color_trc","bt709","-colorspace","bt709"});
            return args;
        }
        static int IndexOf(IList<string> args,string value){for(int i=0;i<args.Count;i++)if(args[i]==value)return i;return -1;}

        internal static void AttachMergedHevc(BridgeExecutionStage stage,string ffprobe,string input,MediaProbeInfo info,string tonemap,string gpuName)
        {
            if(stage.BeforeRunPreparation!=null)throw new InvalidOperationException("HDR merge preparation already attached");
            var frozen=new List<string>(stage.OriginalArguments);
            // No workfile is created: reuse the shared filter builders and their worker-side probe/fallback.
            var cpu=BuildToSdr(input,"unused-inline-hdr.mkv",info,tonemap,false);
            string device=VulkanDevicePolicy.DeviceName;
            stage.BeforeRunPreparation=delegate {
                string notice;
                var selected=SelectAutomatic(stage.ToolPath,cpu,tonemap,out notice,device);
                var merged=MergeHevcArguments(frozen,selected);
                var time=SharedFrucCore.ProbeTimeline(ffprobe,input);
                merged=SharedOrdinaryTimelineCore.Build(merged,input,time.CommonOrigin,time.VideoDelay);
                stage.DirectArguments=SharedFrucCore.JoinArguments(merged);
                return "[HDR merged HEVC] 单次 HDR 转 SDR / 调色 / LUT / 编码；不生成 HDR 中间文件。\n"+notice+
                    "\n[Native ordinary timeline] source_common="+Number(time.CommonOrigin)+" video_delay="+Number(time.VideoDelay);
            };
        }

        internal static bool CanMergeEncode(bool hdrToSdr,bool interpolation,bool interlaced)
        { return hdrToSdr && !interpolation && !interlaced; }

        internal static List<string> MergeEncodeArguments(IList<string> original,IList<string> hdr,
            bool videoOnly,double common,double delay,string gpuName,bool sourceCadence=true)
        {
            if(double.IsNaN(common)||double.IsInfinity(common)||double.IsNaN(delay)||double.IsInfinity(delay)||delay<0)
                throw new InvalidOperationException("Invalid merged encode timeline");
            var args=new List<string>(original);
            int graph=args.IndexOf("-filter_complex"),vf=IndexOf(hdr,"-vf"),input=args.IndexOf("-i");
            if(graph<0||vf<0||input<0||!args[graph+1].StartsWith("[0:v:0]",StringComparison.Ordinal))
                throw new InvalidOperationException("Merged encode input/filter layout changed");
            // Match the old workfile clock before shaders and procedural noise.
            args[graph+1]="[0:v:0]"+hdr[vf+1]+",format=yuv420p10le,setpts=PTS-STARTPTS,"+args[graph+1].Substring(7);
            if(args.Contains("-filter_hw_device")) {
                int init=args.IndexOf("-init_hw_device");
                if(init<0||!args[init+1].StartsWith("vulkan=",StringComparison.Ordinal))
                    throw new InvalidOperationException("Merged HDR requires an existing Vulkan filter device");
                int colon=args[init+1].IndexOf(':');
                string name=colon<0?args[init+1]:args[init+1].Substring(0,colon);
                args[init+1]=name+":"+(VulkanDevicePolicy.DeviceName);
            } else {
                var devices=new List<string>();
                foreach(string option in new[]{"-init_hw_device","-filter_hw_device"})
                    for(int j=0;j+1<hdr.Count;j++)if(hdr[j]==option)devices.AddRange(new[]{option,hdr[j+1]});
                args.InsertRange(input,devices);
            }
            graph=args.IndexOf("-filter_complex");input=args.IndexOf("-i");
            // The former Matroska workfile supplied millisecond frame timestamps to FGSIM.
            // Match that link time base before libplacebo's shader/frame queue.
            if(args[graph+1].Contains("FilmGrainSimplified_"))
                args[graph+1]=args[graph+1].Replace("format=yuv420p10le,setpts=PTS-STARTPTS,","format=yuv420p10le,settb=expr=1/1000,setpts=PTS-STARTPTS,");
            // blend_vulkan can repeat the last main frame while a looping plate ends.
            // Keep the main video's strictly increasing timestamps, without an extra copy/blend.
            if(args[graph+1].Contains("blend_vulkan="))
                args[graph+1]=args[graph+1].Replace("[vout]",",select='isnan(prev_selected_pts)+gt(pts,prev_selected_pts)'[vout]");
            string tail=videoOnly?",setpts=PTS-STARTPTS":",setpts=PTS-STARTPTS+"+Number(delay)+"/TB";
            args[graph+1]=args[graph+1].Replace("[vout]",tail+"[vout]");
            if(!videoOnly || sourceCadence) {
                for(int j=graph+2;j+1<args.Count;j++) {
                    if(args[j]=="-r"){args.RemoveRange(j,2);j--;continue;}
                    if(args[j]=="-fps_mode"||args[j]=="-fps_mode:v")args[j+1]="passthrough";
                }
            }
            if(!videoOnly) {
                // Offset only the main input; the looping plate keeps its local clock.
                args.InsertRange(input,new[]{"-copyts","-itsoffset",Number(-common)});
            }
            args.InsertRange(args.Count-1,new[]{"-color_range","tv","-color_primaries","bt709","-color_trc","bt709","-colorspace","bt709"});
            return args;
        }
        internal static List<string> MergeFrucArguments(IList<string> original,IList<string> hdr,string gpuName,string encoderRate = null)
        {
            var args=MergeEncodeArguments(original,hdr,true,0,0,gpuName);
            int graph=args.IndexOf("-filter_complex");
            if(!args[graph+1].Contains("fruc_vulkan="))throw new InvalidOperationException("FRUC HDR merge requires FRUC filter");
            args[graph+1]=args[graph+1].Replace("format=yuv420p10le,setpts=PTS-STARTPTS,","format=yuv420p10le,settb=expr=1/1000,setpts=PTS-STARTPTS,");
            if(!string.IsNullOrEmpty(encoderRate)) {
                if(!System.Text.RegularExpressions.Regex.IsMatch(encoderRate,@"^[1-9][0-9]*/[1-9][0-9]*$"))throw new InvalidOperationException("Invalid HDR workfile encoder clock");
                string[] rate=encoderRate.Split('/');
                // Old HDR output rounded PTS to the encoder clock before Matroska rounded
                // again to milliseconds. Preserve both quantizations before FRUC resets PTS.
                args[graph+1]=args[graph+1].Replace("format=yuv420p10le,settb=expr=1/1000,","format=yuv420p10le,settb=expr="+rate[1]+"/"+rate[0]+",settb=expr=1/1000,");
            }
            return args;
        }
        internal static string ProbeWorkfileFrameRate(string ffmpeg,string input)
        {
            string output,error;
            var args=new[]{"-hide_banner","-nostdin","-loglevel","info","-i",input,"-map","0:v:0","-an","-sn","-dn","-vf","showinfo","-frames:v","1","-f","null","-"};
            if(!SharedFrucCore.Run(ffmpeg,SharedFrucCore.JoinArguments(args),15000,out output,out error))throw new InvalidOperationException("HDR workfile clock probe failed: "+error);
            var match=System.Text.RegularExpressions.Regex.Match(output??"",@"config in time_base:\s*[0-9]+/[0-9]+,\s*frame_rate:\s*([1-9][0-9]*/[1-9][0-9]*)");
            if(!match.Success)throw new InvalidOperationException("HDR workfile clock probe did not report the decoder filter frame rate");
            return match.Groups[1].Value;
        }
        internal static void AttachMergedFruc(BridgeExecutionStage stage,string input,MediaProbeInfo info,string tonemap,string gpuName)
        {
            if(stage.BeforeRunPreparation!=null||stage.FrucArguments==null)throw new InvalidOperationException("FRUC merge preparation layout changed");
            var original=new List<string>(stage.FrucArguments);
            var cpu=BuildToSdr(input,"unused-fruc-hdr.mkv",info,tonemap,false);
          