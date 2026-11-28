param([string]$Exe=(Join-Path $PSScriptRoot '..\..\FilmGrain_Studio.exe'))
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
public static class G04cAudit {
    static void Must(bool v,string msg){if(!v)throw new Exception(msg);}
    static void Set(object o,string n,object v){o.GetType().GetField(n).SetValue(o,v);}
    static void Prop(object o,string n,object v){o.GetType().GetProperty(n).SetValue(o,v,null);}
    public static void Run(string exe){
        Assembly a=Assembly.LoadFrom(exe);
        Type t=a.GetType("FilmGrainStudioPreview.G04cLutCore",true);
        BindingFlags f=BindingFlags.NonPublic|BindingFlags.Static;
        MethodInfo route=t.GetMethod("RouteEligible",f),cube=t.GetMethod("TryCanonicalCube",f),graph=t.GetMethod("NormalizedFilter",f),direct=t.GetMethod("PlateDirectGraph",f);
        object m=Activator.CreateInstance(a.GetType("FilmGrainStudioPreview.MediaProbeInfo",true),true);
        Set(m,"HasVideo",true);Set(m,"Width",3840);Set(m,"Height",2160);Set(m,"FieldOrder","progressive");
        Set(m,"ColorSpace","bt709");Set(m,"ColorRange","tv");Set(m,"ColorTransfer","bt709");Set(m,"ColorPrimaries","bt709");Set(m,"PixelFormat","yuv420p10le");
        object c=Activator.CreateInstance(a.GetType("FilmGrainStudioPreview.HardwareCapabilitySnapshot",true),true);
        Prop(c,"Ready",true);Prop(c,"HevcAvailable",true);Prop(c,"GpuName","NVIDIA GeForce RTX 4080");
        var s=new Dictionary<string,string>{{"FG_MODE","HEVC"},{"FG_GRAIN_ENABLED","1"},{"FG_GRAIN_ENGINE","NATIVE"},{"FG_FPS_MODE","SOURCE"}};
        Func<bool> ok=delegate{return (bool)route.Invoke(null,new object[]{m,c,s});};Must(ok(),"Valid Plate excluded");
        foreach(var kv in new Dictionary<string,string>{{"FG_MODE","H264"},{"FG_SVP_INTERPOLATE","1"},{"FG_GPU_DECODE","1"},{"FG_FPS_MODE","AUTO"},{"FG_GRAIN_ENGINE","FGSIM"},{"FG_CINEMATIC_FRAME","1"},{"FG_SUBTITLE","1"},{"FG_FILM_BORDER_MODE","ON"},{"FG_COLOR_ENABLED","1"},{"FG_PRE_BILATERAL_ENABLED","true"},{"FG_RESIZE_MODE","WIDTH"}}){
            string old;bool had=s.TryGetValue(kv.Key,out old);s[kv.Key]=kv.Value;Must(!ok(),"Protected route admitted: "+kv.Key);if(had)s[kv.Key]=old;else s.Remove(kv.Key);
        }
        Set(m,"ColorTransfer","smpte2084");Must(!ok(),"HDR admitted");Set(m,"ColorTransfer","bt709");
        Set(m,"ColorRange","pc");Must(!ok(),"Full range admitted");Set(m,"ColorRange","tv");
        Prop(c,"GpuName","NVIDIA T600");Must(!ok(),"Unverified GPU admitted");Prop(c,"GpuName","NVIDIA GeForce RTX 4080");
        s["FG_GRAIN_ENABLED"]="0";Must(ok(),"LUT only excluded");
        MethodInfo wants=t.GetMethod("WantsGpu",f);Must((bool)wants.Invoke(null,new object[]{s}),"GPU not default");
        s["FG_LUT_PROCESSING_MODE"]="CPU";Must(!(bool)wants.Invoke(null,new object[]{s}),"CPU override ignored");
        string path=Path.Combine(Path.GetTempPath(),"G04c & (test) ^ % ! "+Guid.NewGuid().ToString("N")+".cube");
        try{
            string data="LUT_3D_SIZE 2\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n";
            File.WriteAllText(path,data);object[] args={path,25,""};Must((bool)cube.Invoke(null,args),"Standard LUT rejected");
            Must(((string)args[2]).Contains("0.7500000000 0.7500000000 0.7500000000"),"Node strength mismatch");
            File.WriteAllText(path,data.Replace("0 0 0","1.1 0 0"));Must(!(bool)cube.Invoke(null,args),"Out-of-range nodes admitted");
            File.WriteAllText(path,"LUT_1D_SIZE 2\n"+data);Must(!(bool)cube.Invoke(null,args),"Compound LUT admitted");
            File.WriteAllText(path,"DOMAIN_MIN -1 0 0\n"+data);Must(!(bool)cube.Invoke(null,args),"Nonstandard domain admitted");
        }finally{File.Delete(path);}
        string g=(string)graph.Invoke(null,new object[]{"stage.cube"});Must(g.Contains("lut_type=normalized:format=p010le")&&g.EndsWith("peak_detect=0:dithering=none"),"GPU graph changed");
        string d=(string)direct.Invoke(null,new object[]{"[basevk][grainvk]blend_vulkan=all_mode=overlay,hwdownload,format=p010le,format=p010le[vout]"});
        Must(d.EndsWith(",hwupload=derive_device=cuda[vout]")&&!d.Contains("hwdownload"),"Plate direct tail changed");
        Must((string)t.GetField("DirectDeviceFlags",f).GetRawConstantValue()==",disable_multiplane=1,disable_host_image_copy=1","Vulkan flags changed");
        Console.WriteLine("PASS G04c route, standard CUBE, strength, graph and guard contracts; GPU acceptance pending.");
    }
}
'@
[G04cAudit]::Run((Resolve-Path -LiteralPath $Exe).Path)
