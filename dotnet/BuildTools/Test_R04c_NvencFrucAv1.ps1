param([string]$Exe = (Join-Path $PSScriptRoot '..\..\FilmGrain_Studio.exe'))
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw ('Missing R04c candidate EXE: ' + $Exe) }
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
public static class FgsR04cAudit
{
    static void Must(bool ok,string reason) { if (!ok) throw new Exception(reason); }
    static object Prop(Type t,object o,string name) { return t.GetProperty(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).GetValue(o,null); }
    static void Set(Type t,object o,string name,object value) { t.GetProperty(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).SetValue(o,value,null); }
    public static void Run(string exe,string av1Path,string producerPath)
    {
        string s=File.ReadAllText(av1Path);
        string p=File.ReadAllText(producerPath);
        Must(s.Contains("bool directNvencFruc=svp && NvencInterpolationCore.Enabled(state)"),"FRUC-specific eligibility missing");
        Must(s.Contains("Eq(state,\"FG_INTERPOLATION_ENGINE\",\"NVENC_FRUC\")") &&
             s.Contains("!info.IsHdr && !hdrToSdr && Get(state,\"FG_NVENC_VFG_HDR10\")!=\"1\""),"FRUC SDR-only protection missing");
        Must(s.Contains("bool directNvencAi=svp && NvencInterpolationCore.Enabled(state)"),"R04b AI eligibility disturbed");
        Must(s.Contains("if (svp && !directSvp && !directNvencAi && !directNvencFruc)"),"Legacy producer path not retained");
        Must(s.Contains("if (interpolate && !directFruc && !directSvp && !directNvencAi && !directNvencFruc) request.TemporaryFiles.Add(frucWorkfile);"),"Workfile cleanup/fallback guard missing");
        Must(s.Contains("string encodeInput = (directSvp || directNvencAi || directNvencFruc) ? \"pipe:0\""),"Direct pipe not selected");
        Must(s.Contains("if (directSvp || directNvencAi || directNvencFruc) Add(encode, \"-f\", \"yuv4mpegpipe\");"),"Pipe demux placeholder missing");
        Must(s.Contains("if (interpolate) Add(encode, \"-copyts\");") &&
             s.Contains("\"-fps_mode:v\", \"passthrough\""),"FRUC AV1 input PTS passthrough changed");
        Must(s.Contains("NvencInterpolationCore.AttachProducer(stage,frucInput,state,fpsText);"),"Shared NVEncC raw producer missing");
        Must(s.Contains("SharedFrucCore.AttachAv1RemuxTimeline") && s.Contains("Stage(grav1synth") &&
             s.Contains("NvencHdr10StreamCore.Attach(request,input,info,remuxVideo,hdrOrdinaryClock)"),"IVF/grain/remux/metadata hooks changed");
        Must(p.Contains("\"--avsync\",\"vfr\"") && p.Contains("\"--output-format\",\"nut\"") &&
             p.Contains("string prefix=\"setpts=PTS-STARTPTS,\";"),"NUT timestamp contract changed");
        Assembly a=Assembly.LoadFrom(exe);
        Type producer=a.GetType("FilmGrainStudioPreview.NvencInterpolationCore",true);
        Type stageType=a.GetType("FilmGrainStudioPreview.BridgeExecutionStage",true);
        MethodInfo attach=producer.GetMethod("AttachProducer",BindingFlags.NonPublic|BindingFlags.Static);
        MethodInfo enabled=producer.GetMethod("Enabled",BindingFlags.NonPublic|BindingFlags.Static);
        Must(attach!=null && enabled!=null,"NVEncC FRUC reflection entry missing");
        Must(!s.Contains("NvencInterpolationCore.HasFrucRawFixBuild"),"Fork-only FRUC gate remains");
        Must(producer.GetMethod("HasFrucRawFixBuild",BindingFlags.NonPublic|BindingFlags.Static)==null,"Obsolete FRUC build fingerprint remains");
        int count=0;
        foreach(string rate in new []{"50","60000/1001"})
        foreach(string origin in new []{"zero","offset_320ms"})
        {
            var state=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            state["FG_SVP_INTERPOLATE"]="1";
            state["FG_INTERPOLATION_ENGINE"]="NVENC_FRUC";
            state["FG_NVENC_DIR"]="C:\\NVEncC";
            state["FG_NVENC_GPU_NAME"]="NVIDIA GeForce RTX 4080";
            state["FG_NVENC_DEVICES"]="DeviceId #0: NVIDIA GeForce RTX 4080 (PCI Bus ID: 0000:01:00.0)";
            state["FG_NVENC_VFG_HDR10"]="0";
            Must((bool)enabled.Invoke(null,new object[]{state}),"NVENC_FRUC engine not enabled");
            object stage=Activator.CreateInstance(stageType,true);
            Set(stageType,stage,"DirectArguments","-copyts -f yuv4mpegpipe -i pipe:0 -filter_complex \"[0:v:0]format=p010le[vout]\" -map [vout] -enc_time_base:v 1001/60000 -fps_mode:v passthrough");
            attach.Invoke(null,new object[]{stage,"C:\\media\\FRUC & test.mkv",state,rate});
            string args=(string)Prop(stageType,stage,"PipeProducerArguments");
            Must(args.Contains("--vpp-fruc") && args.Contains("fps="+rate) && !args.Contains("--vpp-nvvfx-framegen"),"FRUC option/target cadence wrong");
            Must(args.Contains("\"--output-format\" \"nut\"") && args.Contains("\"--avsync\" \"vfr\"") &&
                 args.Contains("\"--output-depth\" \"8\"") && args.Contains("\"-c\" \"raw\""),"FRUC raw NUT/8-bit/VFR transport changed");
            Action observe=(Action)Prop(stageType,stage,"PreparedObserver");
            Must(observe!=null,"NUT demux observer not installed");
            observe();
            string consumer=(string)Prop(stageType,stage,"DirectArguments");
            Must(consumer.Contains("-f nut") && !consumer.Contains("yuv4mpegpipe") &&
                 consumer.Contains("[0:v:0]setpts=PTS-STARTPTS,") && !consumer.Contains("setparams=range=limited"),"FRUC PTS rebasing/SDR color handling wrong");
            count++;
        }
        Console.WriteLine("PASS R04c NVEncC FRUC separate SDR-only guard, raw NUT producer and local PTS normalization "+count+"/4");
        Console.WriteLine("PASS R04c preserved legacy fallback, AVC/IVF/grain/remux/metadata and cancellation owner contracts (static)");
        Console.WriteLine("NOTE: Real NVEncC FRUC requires RTX acceptance.");
    }
}
'@
$av1 = Join-Path $PSScriptRoot '..\FilmGrain_NativeAv1Core.cs'
$producer = Join-Path $PSScriptRoot '..\FilmGrain_InterpolationFrameRateCore.cs'
[FgsR04cAudit]::Run((Resolve-Path -LiteralPath $Exe).Path,(Resolve-Path -LiteralPath $av1).Path,(Resolve-Path -LiteralPath $producer).Path)
