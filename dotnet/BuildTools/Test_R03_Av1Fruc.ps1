param([string]$Exe = (Join-Path $PSScriptRoot '..\..\FilmGrain_Studio.exe'))
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw ('Missing verified EXE: ' + $Exe) }
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
public static class FgsR03Audit
{
    static void Require(bool pass,string message) { if(!pass) throw new Exception(message); }
    public static void Run(string exePath,string sourceFile)
    {
        Assembly a=Assembly.LoadFrom(exePath);
        BindingFlags b=BindingFlags.Static|BindingFlags.NonPublic;
        Type route=a.GetType("FilmGrainStudioPreview.Av1RouteOptimizationCore",true);
        Type media=a.GetType("FilmGrainStudioPreview.MediaProbeInfo",true);
        Type fruc=a.GetType("FilmGrainStudioPreview.SharedFrucCore",true);
        var eligible=route.GetMethod("FrucEncodeSource",b);
        var inputFilter=fruc.GetMethod("InputFilter",b);
        Require(eligible!=null && inputFilter!=null,"R03 shared route/input methods unavailable");
        object info=Activator.CreateInstance(media,true);
        media.GetField("AvgFrameRate").SetValue(info,"25/1");
        var state=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        state["FG_FPS_MODE"]="SOURCE";
        string source=File.ReadAllText(sourceFile);
        Require(source.Contains("bool simpleRoute=!interpolate && Av1RouteOptimizationCore.SimpleSource"),"Direct-output gate no longer independent");
        Require(source.Contains("bool directFruc=fruc && Av1RouteOptimizationCore.FrucEncodeSource"),"Direct FRUC gate missing");
        Require(source.Contains("SharedFrucCore.InputFilter(fpsText,frucPerf,frucGrid,hdrPreserve)"),"10-bit FRUC filter selection missing");
        Require(source.Contains("format=\"+frucFormat+\",settb=expr=1/1000,"),"Old FFV1 clock missing");
        Require(source.Contains("if (interpolate && !directFruc && !directSvp && !directNvencAi && !directNvencFruc) request.TemporaryFiles.Add(frucWorkfile);"),"Workfile staging gate changed");
        Require(source.Contains("SharedFrucCore.AttachAv1RemuxTimeline"),"Final remux timeline removed");
        Require(source.Contains("Av1GrainStrengthCore.Stages") && source.Contains("Stage(grav1synth"),"Metadata grain stages removed");
        Require(source.Contains("SharedFrucCore.TryPreflight10(frucFfmpeg"),"P010 preflight removed");
        string sd=(string)inputFilter.Invoke(null,new object[]{"50/1","medium","auto",false});
        string hd=(string)inputFilter.Invoke(null,new object[]{"50/1","medium","auto",true});
        Require(sd.Contains("format=nv12,hwupload,fruc_vulkan") && !sd.Contains("p010le"),"SDR FRUC format changed");
        Require(hd.Contains("format=p010le,hwupload,fruc_vulkan") && !hd.Contains("nv12"),"HDR FRUC 10-bit format changed");
        int good=0;
        foreach(string fmt in new []{"mov,mp4,m4a,3gp,3g2,mj2","matroska,webm"})
        foreach(string fps in new []{"SOURCE","AUTO"})
        foreach(string transfer in new []{"bt709","smpte2084","arib-std-b67"})
        {
            media.GetField("FormatName").SetValue(info,fmt);
            media.GetField("ColorTransfer").SetValue(info,transfer);
            state["FG_FPS_MODE"]=fps;
            Require((bool)eligible.Invoke(null,new object[]{info,state,0,false,false}),"FRUC encoder disallowed by container/FPS/HDR");
            good++;
        }
        Require(good==12,"Expected twelve safe-route scenarios");
        state["FG_FPS_MODE"]="SOURCE";
        media.GetField("FieldOrder").SetValue(info,"tt");
        Require(!(bool)eligible.Invoke(null,new object[]{info,state,0,false,false}),"Interlaced input admitted");
        media.GetField("FieldOrder").SetValue(info,"");
        Require(!(bool)eligible.Invoke(null,new object[]{info,state,2,false,false}),"SFE admitted");
        Require(!(bool)eligible.Invoke(null,new object[]{info,state,0,true,false}),"HDR to SDR admitted");
        Require(!(bool)eligible.Invoke(null,new object[]{info,state,0,false,true}),"Pixel grain admitted");
        foreach(var guard in new [] {
            new KeyValuePair<string,string>("FG_PRE_BILATERAL_ENABLED","true"),
            new KeyValuePair<string,string>("FG_RESIZE_MODE","MANUAL"),
            new KeyValuePair<string,string>("FG_COLOR_ENABLED","1"),
            new KeyValuePair<string,string>("FG_LUT_PATH","C:\\film.cube"),
            new KeyValuePair<string,string>("FG_CINEMATIC_FRAME","1"),
            new KeyValuePair<string,string>("FG_SUBTITLE","1") })
        {
            state[guard.Key]=guard.Value;
            Require(!(bool)eligible.Invoke(null,new object[]{info,state,0,false,false}),"Protection unexpectedly admitted: "+guard.Key);
            state.Remove(guard.Key);
        }
        string old=Environment.GetEnvironmentVariable("FGS_AV1_ROUTES_LEGACY");
        try {
            Environment.SetEnvironmentVariable("FGS_AV1_ROUTES_LEGACY","1");
            Require(!(bool)eligible.Invoke(null,new object[]{info,state,0,false,false}),"Legacy escape hatch changed");
        } finally { Environment.SetEnvironmentVariable("FGS_AV1_ROUTES_LEGACY",old); }
        Console.WriteLine("PASS R03 12 MP4/MKV SOURCE/AUTO SDR/PQ/HLG routes, eight safety gates and legacy escape hatch");
        Console.WriteLine("PASS R03 8-bit/P010 filter prefix, IVF/remux/grain/clock/P010 static contracts");
        Console.WriteLine("NOTE Windows reflection contract; actual FRUC Vulkan AV1 NVENC output requires GPU acceptance");
    }
}
'@
$sourceFile = Join-Path $PSScriptRoot '..\FilmGrain_NativeAv1Core.cs'
[FgsR03Audit]::Run((Resolve-Path -LiteralPath $Exe).Path, (Resolve-Path -LiteralPath $sourceFile).Path)
