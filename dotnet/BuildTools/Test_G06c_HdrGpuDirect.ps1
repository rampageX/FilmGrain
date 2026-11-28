param([string]$Exe=(Join-Path $PSScriptRoot '..\..\FilmGrain_Studio.exe'))
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
public static class G06cAudit {
    const BindingFlags F=BindingFlags.NonPublic|BindingFlags.Static;
    static void Must(bool b,string s){if(!b)throw new Exception(s);}
    static void Set(object o,string n,object v){o.GetType().GetField(n).SetValue(o,v);}
    static void Prop(object o,string n,object v){o.GetType().GetProperty(n).SetValue(o,v,null);}
    public static void Run(string exe){
        var a=Assembly.LoadFrom(exe);const string ns="FilmGrainStudioPreview.";
        var t=a.GetType(ns+"G06cHdrCore",true);var h=a.GetType(ns+"SharedHdrPreprocessCore",true);
        var clock=a.GetType(ns+"SharedOrdinaryTimelineCore",true);
        object m=Activator.CreateInstance(a.GetType(ns+"MediaProbeInfo",true),true);
        Set(m,"HasVideo",true);Set(m,"Width",3840);Set(m,"Height",1608);Set(m,"FieldOrder","progressive");
        Set(m,"ColorSpace","bt2020nc");Set(m,"ColorRange","tv");Set(m,"ColorTransfer","smpte2084");Set(m,"ColorPrimaries","bt2020");Set(m,"PixelFormat","yuv420p10le");
        object c=Activator.CreateInstance(a.GetType(ns+"HardwareCapabilitySnapshot",true),true);
        Prop(c,"Ready",true);Prop(c,"HevcAvailable",true);Prop(c,"GpuName","NVIDIA GeForce RTX 4080");
        var s=new Dictionary<string,string>{{"FG_MODE","HEVC"},{"FG_GRAIN_ENABLED","1"},{"FG_GRAIN_ENGINE","NATIVE"},{"FG_FPS_MODE","SOURCE"},{"FG_HDR_POLICY","SDR"}};
        Func<bool> ok=delegate{return (bool)t.GetMethod("RouteEligible",F).Invoke(null,new object[]{m,c,s});};
        Must(ok(),"HDR Plate route rejected");
        foreach(var kv in new Dictionary<string,string>{{"FG_MODE","H264"},{"FG_HDR_POLICY","PRESERVE"},{"FG_SVP_INTERPOLATE","1"},{"FG_GPU_DECODE","1"},{"FG_HWD_BACKEND","cuda"},{"FG_FPS_MODE","AUTO"},{"FG_GRAIN_ENGINE","FGSIM"},{"FG_CINEMATIC_FRAME","1"},{"FG_SUBTITLE","1"},{"FG_FILM_BORDER_MODE","ON"},{"FG_COLOR_ENABLED","1"},{"FG_PRE_BILATERAL_ENABLED","true"},{"FG_RESIZE_MODE","1080"},{"FG_LUT_PATH","a.cube"},{"FG_TONEMAP_ALGO","mobius"}}){
            string old;bool had=s.TryGetValue(kv.Key,out old);s[kv.Key]=kv.Value;Must(!ok(),"Unverified route admitted: "+kv.Key);if(had)s[kv.Key]=old;else s.Remove(kv.Key);
        }
        Prop(c,"GpuName","NVIDIA T600");Must(!ok(),"Unverified GPU admitted");Prop(c,"GpuName","NVIDIA GeForce RTX 4080");
        Set(m,"FieldOrder","tt");Must(!ok(),"Interlaced admitted");Set(m,"FieldOrder","progressive");
        s["FG_GRAIN_ENABLED"]="0";Must(ok(),"No Plate rejected");
        string source=@"H:\Tester & ( ) ^ % ! -=\source.mkv";
        foreach(string transfer in new[]{"smpte2084","arib-std-b67"})
        foreach(string curve in new[]{"hable","spline","bt.2390"})
        foreach(bool tuned in new[]{false,true})
        foreach(int plate in new[]{0,1,2}){
            Set(m,"ColorTransfer",transfer);
            string options=!tuned?"":curve=="hable"?"100:200:0:8:120":curve=="spline"?"150:80:100:8:120":"150:100:8:120";
            var tone=(List<string>)h.GetMethod("BuildTunedToSdr",F).Invoke(null,new object[]{source,"unused.mkv",m,curve,false,"",options});
            if(curve=="hable")tone=(List<string>)h.GetMethod("BuildTunedGpuArguments",F).Invoke(null,new object[]{tone,options,curve});
            string hdr=tone[tone.IndexOf("-vf")+1];
            string g=plate==0?"[0:v:0]format=p010le,format=p010le[vout]":
                "[0:v:0]format=p010le,setpts=PTS-STARTPTS,hwupload[basevk];[1:v:0]fps=24000/1001,format=p010le,setpts=PTS-STARTPTS,hwupload"+
                (plate==2?",scale_vulkan=w=3840:h=1608:scaler=bilinear":"")+"[grainvk];[basevk][grainvk]blend_vulkan=all_mode=overlay:all_opacity=0.35,hwdownload,format=p010le,format=p010le[vout]";
            var args=new List<string>();if(plate!=0)args.AddRange(new[]{"-init_hw_device","vulkan=vk:NVIDIA","-filter_hw_device","vk"});
            args.AddRange(new[]{"-i",source,"-filter_complex",g,"-map","[vout]","-c:v","hevc_nvenc","-pix_fmt","p010le","-r","24000/1001","-fps_mode:v","cfr","output.mkv"});
            List<string> merged;
            if(plate==0){
                merged=(List<string>)h.GetMethod("MergeHevcArguments",F).Invoke(null,new object[]{args,tone});
                merged=(List<string>)clock.GetMethod("Build",F).Invoke(null,new object[]{merged,source,0.5d,0.32d});
            }else merged=(List<string>)h.GetMethod("MergeEncodeArguments",F).Invoke(null,new object[]{args,tone,false,0.5d,0.32d,"NVIDIA",true});
            Must((bool)t.GetMethod("TryApply",F).Invoke(null,new object[]{merged,plate!=0}),"Direct graph not applied");
            string actual=merged[merged.IndexOf("-filter_complex")+1];
            Must(!actual.Contains("hwdownload")&&!merged.Contains("-pix_fmt")&&!merged.Contains("-r"),"CPU format / clock boundary");
            Must(actual.Contains("hwupload=derive_device=cuda")&&actual.Contains("0.32/TB")&&merged.Contains("-0.5"),"CUDA or timeline missing");
            Must(actual.Contains("MASTERING_DISPLAY_METADATA")&&actual.Contains("CONTENT_LIGHT_LEVEL")&&actual.Contains("color_trc=bt709"),"SDR metadata cleanup missing");
            string expected=hdr.Substring(hdr.IndexOf("libplacebo="),hdr.IndexOf(",hwdownload",StringComparison.Ordinal)-hdr.IndexOf("libplacebo="));
            Must(actual.Contains(expected),"Tone options changed");
            Must(actual.Contains("scale_vulkan=")==(plate==2)&&actual.Contains("select='isnan(prev_selected_pts)+gt(pts,prev_selected_pts)'")==(plate!=0),"Scale/EOF selection changed");
            Must(merged.Count(x=>x=="-init_hw_device")==1&&merged[merged.IndexOf("-init_hw_device")+1].EndsWith(",disable_multiplane=1,disable_host_image_copy=1"),"Device flags missing");
        }
        var cpu=new List<string>{"-filter_complex","[0:v:0]format=p010le[vout]","-pix_fmt","p010le"};
        var before=string.Join(" ",cpu);Must(!(bool)t.GetMethod("TryApply",F).Invoke(null,new object[]{cpu,false})&&before==string.Join(" ",cpu),"CPU fallback modified");
        Console.WriteLine("PASS G06c 36 direct graph combinations, route guards, CUDA format, HDR metadata, shared clock, fallback.");
    }
}
'@
[G06cAudit]::Run((Resolve-Path -LiteralPath $Exe).Path)
