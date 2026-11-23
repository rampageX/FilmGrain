param([string]$Exe = (Join-Path $PSScriptRoot '..\..\FilmGrain_Studio.exe'))
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw ('Missing verified EXE: ' + $Exe) }
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
public static class FgsR04aAudit
{
    static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Run(string exePath, string av1Source, string r03Source)
    {
        Assembly asm = Assembly.LoadFrom(exePath);
        BindingFlags flags = BindingFlags.Static|BindingFlags.NonPublic;
        Type svp = asm.GetType("FilmGrainStudioPreview.SharedSvpCore", true);
        Type stageType = asm.GetType("FilmGrainStudioPreview.BridgeExecutionStage", true);
        Type cadence = asm.GetType("FilmGrainStudioPreview.SharedInterpolationEncodeCore", true);
        MethodInfo producer = svp.GetMethod("AttachProducer", flags);
        MethodInfo normalize = cadence.GetMethod("NormalizeRenderedCadence", flags);
        Require(producer != null && normalize != null, "Shared producer/cadence methods unavailable");
        string source = File.ReadAllText(av1Source);
        Require(source.Contains("bool directSvp=svp && !NvencInterpolationCore.Enabled(state) && !hdrToSdr"), "OpenSVPFlow-only HDR-to-SDR guard missing");
        Require(source.Contains("hdrPreserve==(Get(state,\"FG_SVP_HDR10\")==\"1\")"), "Y4M 8/10-bit guard missing");
        Require(source.Contains("if (interpolate && !directFruc && !directSvp && !directNvencAi)"), "FFV1 probe bypass missing");
        Require(source.Contains("if (interpolate && !directFruc && !directSvp && !directNvencAi) request.TemporaryFiles.Add(frucWorkfile);"), "FFV1 workfile cleanup guard missing");
        Require(source.Contains("if (svp && !directSvp && !directNvencAi)"), "Legacy SVP preprocessor exclusion missing");
        Require(source.Contains("if (directSvp || directNvencAi) Add(encode, \"-f\", \"yuv4mpegpipe\");"), "AV1 Y4M demuxer missing");
        Require(source.Contains("string encodeInput = (directSvp || directNvencAi) ? \"pipe:0\""), "AV1 pipe input missing");
        Require(source.Contains("SharedSvpCore.AttachProducer(stage"), "Producer not attached to AV1 stage");
        Require(source.Contains("SharedFrucCore.AttachAv1RemuxTimeline"), "IVF/AV final clock removed");
        Require(source.Contains("Stage(grav1synth") && source.Contains("Av1GrainStrengthCore.Stages"), "AV1 metadata grain stages removed");
        Require(source.Contains("SharedFrucCore.TryPreflight10(frucFfmpeg"), "R03 P010 preflight changed");
        Require(!source.Contains("if (svp && !directSvp && NvencInterpolationCore.Enabled(state))"), "NVEncC producer was modified");
        Require(File.ReadAllText(r03Source).Contains("if (interpolate && !directFruc && !directSvp && !directNvencAi) request.TemporaryFiles.Add(frucWorkfile);"), "R03 regression assertion stale");
        object stage = Activator.CreateInstance(stageType, true);
        var state = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        state["FG_INTERPOLATION_ENGINE"] = "SVP";
        state["FG_SVP_HDR10"] = "0";
        producer.Invoke(null, new object[]{stage,"C:\\SVP\\vspipe.exe","C:\\SVP\\FGS.vpy","C:\\SVP\\Plugins","C:\\Media\\a & b.mp4",state,"60000/1001"});
        string args=(string)stageType.GetProperty("PipeProducerArguments").GetValue(stage,null);
        Require(args.Contains("-c y4m"), "Y4M producer output changed");
        Require(args.Contains("target_num=60000") && args.Contains("target_den=1001"), "Fractional producer cadence changed");
        Require(args.Contains("input="), "Producer source argument missing");
        var graph = new List<string>{"-i","pipe:0","-filter_complex","[0:v:0]format=p010le[vout]","-map","[vout]"};
        normalize.Invoke(null,new object[]{graph,"60000/1001"});
        Require(graph[3].Contains("settb=expr=1001/60000,setpts=N,fps=fps=60000/1001"),"Existing AV1 rendered-cadence contract changed");
        Console.WriteLine("PASS R04a OpenSVPFlow-only Y4M path, HDR guard, legacy fallback, FFV1 exclusion, IVF/remux/grain protection");
        Console.WriteLine("PASS R04a producer args and fractional cadence reflection test");
        Console.WriteLine("NOTE Windows code contract only. Actual AV1 NVENC/vspipe/PQ/HLG and A/V remux require RTX tests.");
    }
}
'@
$source = Join-Path $PSScriptRoot '..\FilmGrain_NativeAv1Core.cs'
$r03 = Join-Path $PSScriptRoot 'Test_R03_Av1Fruc.ps1'
[FgsR04aAudit]::Run((Resolve-Path -LiteralPath $Exe).Path, (Resolve-Path -LiteralPath $source).Path, (Resolve-Path -LiteralPath $r03).Path)
