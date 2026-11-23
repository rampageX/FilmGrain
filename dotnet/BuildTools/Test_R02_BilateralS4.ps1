param([string]$Exe = (Join-Path $PSScriptRoot '..\..\FilmGrain_Studio.exe'))
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw ('Missing verified EXE: ' + $Exe) }
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
public static class FgsR02Audit
{
    static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Run(string exePath, string sourcePath)
    {
        Assembly a = Assembly.LoadFrom(exePath);
        Type preprocess = a.GetType("FilmGrainStudioPreview.PreprocessingCore", true);
        Type native = a.GetType("FilmGrainStudioPreview.NativeHevcCore", true);
        BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        MethodInfo build = preprocess.GetMethod("TryBuild", flags);
        MethodInfo only = preprocess.GetMethod("IsOnlyBilateral", flags);
        MethodInfo lutBase = native.GetMethod("BuildSdrPlateLutGpuBaseFilter", flags);
        Require(build != null && only != null && lutBase != null, "R02 expected shared methods missing");
        string nativeText = File.ReadAllText(sourcePath);
        Require(nativeText.Contains("(string.IsNullOrEmpty(preprocessing) || (s4SdrPlate && PreprocessingCore.IsOnlyBilateral(state,preprocessing)))"),
            "R02 conditional GPU border gate missing");
        Require(nativeText.Contains("bool s4SdrPlate = plateMode && !interpolate && !info.IsHdr && !hdrToSdr"),
            "SDR and no-interpolation guard changed");
        Require(nativeText.Contains("(hdrPreserve || (fgb02 && !info.IsHdr) || s4SdrPlate)"),
            "HDR and FGB02 branch protection changed");
        Require(nativeText.Contains("string.IsNullOrEmpty(subtitleFilter)"), "Subtitle protection changed");
        Require(nativeText.Contains("(!grainEnabled || plateMode)"), "Non-Plate grain protection changed");
        Require(nativeText.Contains("colorFilter=preprocessing+resize.Prefix+colorFilter;"),
            "CPU Bilateral must execute before Resize, LUT, Grain Plate");
        Require(nativeText.Contains("+ colorFilter + \"format=p010le,\""),
            "CPU Bilateral prefix was lost from direct GPU Plate base graph");
        var state = new Dictionary<string,string>();
        object[] args = new object[] { state, "", "" };
        Require((bool)build.Invoke(null,args) && (string)args[1] == "", "Disabled Bilateral changed");
        state["FG_PRE_BILATERAL_ENABLED"] = "true";
        state["FG_PRE_BILATERAL_SIGMA_S"] = "3";
        state["FG_PRE_BILATERAL_SIGMA_R"] = "0.05";
        state["FG_PRE_BILATERAL_PLANES"] = "1";
        args = new object[] { state, "", "" };
        Require((bool)build.Invoke(null,args), "Original Bilateral builder failed");
        string prefix = (string)args[1];
        Require(prefix == "format=yuv420p10le,bilateral=sigmaS=3:sigmaR=0.05:planes=1,",
            "Original Bilateral pixel format or sigma/planes contract changed");
        Require((bool)only.Invoke(null,new object[]{state,prefix}), "Exact Bilateral rejected");
        Require(!(bool)only.Invoke(null,new object[]{state,prefix+"eq=contrast=1.1,"}),
            "Additional unapproved preprocessing admitted");
        Require(!(bool)only.Invoke(null,new object[]{state,"format=yuv420p10le,eq=contrast=1.1,"}),
            "Unapproved preprocessing admitted");
        state["FG_PRE_BILATERAL_ENABLED"] = "false";
        Require(!(bool)only.Invoke(null,new object[]{state,prefix}), "Disabled Bilateral admitted");
        state["FG_PRE_BILATERAL_ENABLED"] = "true";
        state["FG_PRE_BILATERAL_SIGMA_R"] = "NaN";
        Require(!(bool)only.Invoke(null,new object[]{state,prefix}), "Invalid Bilateral params admitted");
        state["FG_PRE_BILATERAL_SIGMA_R"] = "0.05";
        string signal="setparams=range=limited:color_primaries=bt709:color_trc=bt709:colorspace=bt709,";
        string graph = (string)lutBase.Invoke(null,new object[]{"", "", prefix,"test.cube","0.50","24000/1001",signal});
        Require(graph.IndexOf("bilateral=",StringComparison.Ordinal)<graph.IndexOf("lut3d=",StringComparison.Ordinal),
            "Bilateral must precede LUT");
        Require(graph.IndexOf("lut3d=",StringComparison.Ordinal)<graph.IndexOf("hwupload[basevk]",StringComparison.Ordinal),
            "LUT must precede Vulkan upload");
        Require(graph.Contains(signal) && !graph.Contains("hwdownload"),"SDR tags or direct upload changed");
        int scenarios = 0;
        foreach (bool bilateral in new bool[]{false,true})
        foreach (bool plate in new bool[]{false,true})
        foreach (bool border in new bool[]{false,true})
        foreach (bool hdr in new bool[]{false,true})
        foreach (bool h264 in new bool[]{false,true})
        {
            state["FG_PRE_BILATERAL_ENABLED"] = bilateral ? "true" : "false";
            args = new object[] { state, "", "" };
            Require((bool)build.Invoke(null,args), "Matrix Bilateral builder failed");
            string candidate = (string)args[1];
            bool passedPreprocess = candidate.Length==0 || (plate && !hdr && (bool)only.Invoke(null,new object[]{state,candidate}));
            bool expectedPreprocess = !bilateral || (plate && !hdr);
            Require(passedPreprocess==expectedPreprocess,"Bilateral/Plate/HDR conditional gate mismatch");
            bool eligibleNewS4 = plate && border && !hdr && passedPreprocess;
            bool eligibleExpected = plate && border && !hdr;
            Require(eligibleNewS4 == eligibleExpected, "Unexpected GPU S4 gate in R02 candidate");
            Require(!h264 || nativeText.Contains("(!h264 || s4SdrPlate)"), "H264 S4 protection changed");
            scenarios++;
        }
        Console.WriteLine("PASS R02 32 gate scenarios; strict Bilateral-only validation, HDR guards, LUT-before-Plate graph");
        Console.WriteLine("NOTE reflection/source contracts only; real Vulkan/CUDA and NVENC require GPU acceptance");
    }
}
'@
$sourceFile = Join-Path $PSScriptRoot '..\FilmGrain_NativeHevcCore.cs'
[FgsR02Audit]::Run((Resolve-Path -LiteralPath $Exe).Path, (Resolve-Path -LiteralPath $sourceFile).Path)
