param([string]$Exe = (Join-Path $PSScriptRoot '..\..\FilmGrain_Studio.exe'))
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw ('Missing candidate EXE: ' + $Exe) }
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
public static class FgsR04bAudit
{
    static void Must(bool pass,string reason) { if (!pass) throw new Exception(reason); }
    static object Prop(Type type,object obj,string name) { return type.GetProperty(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).GetValue(obj,null); }
    static void Set(Type type,object obj,string name,object value) { type.GetProperty(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).SetValue(obj,value,null); }
    public static void Run(string exe,string sourcePath,string hdrPath)
    {
        string source=File.ReadAllText(sourcePath);
        string hdrSource=File.ReadAllText(hdrPath);
        Must(source.Contains("bool directNvencAi=svp && NvencInterpolationCore.Enabled(state)"),"AI-only gate missing");
        Must(source.Contains("Eq(state,\"FG_INTERPOLATION_ENGINE\",\"NVENC_AI\") && !hdrToSdr"),"AI engine/HDR-to-SDR protection missing");
        Must(source.Contains("NvencInterpolationCore.Hdr10Eligible(info,state)"),"Existing 10-bit eligibility not enforced");
        Must(source.Contains("!info.IsHdr && Get(state,\"FG_NVENC_VFG_HDR10\")!=\"1\""),"SDR depth protection missing");
        Must(source.Contains("if (svp && !directSvp && !directNvencAi && !directNvencFruc)"),"Legacy NVEncC FRUC workfile route changed");
        Must(source.Contains("if (interpolate && !directFruc && !directSvp && !directNvencAi && !directNvencFruc) request.TemporaryFiles.Add(frucWorkfile);"),"Workfile staging changed");
        Must(source.Contains("if (directSvp || directNvencAi || directNvencFruc) Add(encode, \"-f\", \"yuv4mpegpipe\");"),"Direct demuxer setup missing");
        Must(source.Contains("NvencInterpolationCore.AttachProducer(stage,frucInput,state,fpsText)"),"Existing NVEncC producer not reused");
        Must(source.Contains("SharedFrucCore.AttachAv1RemuxTimeline") && source.Contains("Stage(grav1synth"),"IVF/grain/remux removed");
        Must(source.Contains("NvencHdr10StreamCore.Attach(request,input,info,remuxVideo,hdrOrdinaryClock)"),"AV1 MDCV/CLLI injection hook changed");
        Must(hdrSource.Contains("setparams=range=limited:color_primaries=bt2020:color_trc="),"HDR producer color-label contract changed");
        Assembly asm=Assembly.LoadFrom(exe);
        Type cls=asm.GetType("FilmGrainStudioPreview.NvencInterpolationCore",true);
        Type stageType=asm.GetType("FilmGrainStudioPreview.BridgeExecutionStage",true);
        MethodInfo attach=cls.GetMethod("AttachProducer",BindingFlags.Static|BindingFlags.NonPublic);
        Must(attach!=null,"NVEncC producer adapter missing");
        int tested=0;
        foreach(string transfer in new []{"","smpte2084","arib-std-b67"})
        {
            object stage=Activator.CreateInstance(stageType,true);
            Set(stageType,stage,"DirectArguments","-copyts -f yuv4mpegpipe -i pipe:0 -filter_complex \"[0:v:0]format=p010le[vout]\" -map [vout]");
            var state=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            state["FG_NVENC_DIR"]="C:\\NVEncC";
            state["FG_INTERPOLATION_ENGINE"]="NVENC_AI";
            state["FG_NVENC_GPU_NAME"]="NVIDIA GeForce RTX 4080";
            state["FG_NVENC_DEVICES"]="DeviceId #0: NVIDIA GeForce RTX 4080 (PCI Bus ID: 0000:01:00.0)";
            state["FG_NVENC_AI_MODE"]="medium";
            state["FG_NVENC_SOURCE_FPS"]="30000/1001";
            state["FG_NVENC_VFG_HDR10"]=transfer.Length>0?"1":"0";
            state["FG_NVENC_VFG_TRANSFER"]=transfer;
            attach.Invoke(null,new object[]{stage,"C:\\Input\\clip & test.mkv",state,"60000/1001"});
            string producer=(string)Prop(stageType,stage,"PipeProducerArguments");
            Must(producer.Contains("--vpp-nvvfx-framegen") && producer.Contains("multiplier=2"),"AI factor changed");
            Must(producer.Contains("\"--output-format\" \"nut\"") && producer.Contains("\"-c\" \"raw\"") && producer.Contains("\"--avsync\" \"vfr\""),"NUT raw/VFR producer contract changed");
            Must(producer.Contains("\"--output-depth\" \""+(transfer.Length>0?"10":"8")+"\""),"Producer HDR depth changed");
            Action callback=(Action)Prop(stageType,stage,"PreparedObserver");
            Must(callback!=null,"NVEncC NUT demux callback missing");
            callback();
            string args=(string)Prop(stageType,stage,"DirectArguments");
            Must(args.Contains("-f nut") && !args.Contains("yuv4mpegpipe"),"NUT demux not selected");
            Must(args.Contains("[0:v:0]setpts=PTS-STARTPTS,"),"NUT local origin handling changed");
            if (transfer.Length>0) Must(args.Contains("setparams=range=limited:color_primaries=bt2020:color_trc="+transfer+":colorspace=bt2020nc,"),"PQ/HLG color labeling missing");
            else Must(!args.Contains("setparams=range=limited"),"SDR incorrectly stamped as HDR");
            tested++;
        }
        Console.WriteLine("PASS R04b AI-only/legacy protection and NUT producer 3/3 SDR/PQ/HLG; IVF/grain/remux and MDCV/CLLI hooks unchanged");
        Console.WriteLine("NOTE No NVEncC GPU executed in this reflection test.");
    }
}
'@
$av1 = Join-Path $PSScriptRoot '..\FilmGrain_NativeAv1Core.cs'
$interpolation = Join-Path $PSScriptRoot '..\FilmGrain_InterpolationFrameRateCore.cs'
[FgsR04bAudit]::Run((Resolve-Path -LiteralPath $Exe).Path, (Resolve-Path -LiteralPath $av1).Path, (Resolve-Path -LiteralPath $interpolation).Path)
