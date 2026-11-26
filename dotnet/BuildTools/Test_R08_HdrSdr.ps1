param([Parameter(Mandatory=$true)][string]$Exe)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

public static class R08HdrSdrAudit
{
    const BindingFlags Methods = BindingFlags.Static | BindingFlags.NonPublic;
    const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static void Require(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException(label);
    }

    public static void Run(string exe)
    {
        var asm = Assembly.LoadFrom(exe);
        const string ns = "FilmGrainStudioPreview.";
        var core = asm.GetType(ns + "SharedHdrPreprocessCore", true);
        var av1 = asm.GetType(ns + "NativeAv1Core", true);
        var media = asm.GetType(ns + "MediaProbeInfo", true);
        var stage = asm.GetType(ns + "BridgeExecutionStage", true);
        var nvenc = asm.GetType(ns + "NvencInterpolationCore", true);
        var svp = asm.GetType(ns + "SharedSvpCore", true);

        for (int flags = 0; flags < 16; flags++)
        {
            var arguments = Enumerable.Range(0, 4).Select(i => (object)((flags & (1 << i)) != 0)).ToArray();
            Require((bool)core.GetMethod("CanMergeFieldEncode", Methods).Invoke(null, arguments) == (flags == 1),
                "field route eligibility");
        }
        for (int flags = 0; flags < 32; flags++)
        {
            var arguments = Enumerable.Range(0, 5).Select(i => (object)((flags & (1 << i)) != 0)).ToArray();
            Require((bool)av1.GetMethod("CanPipeConvertedSdr", Methods).Invoke(null, arguments) == (flags == 3),
                "SDR producer route eligibility");
        }

        foreach (string transfer in new[]{"smpte2084","arib-std-b67"})
        foreach (string order in new[]{"tt","bb"})
        foreach (string method in new[]{"W3FDIF","BWDIF_CUDA","BWDIF_VULKAN"})
        {
            object info = Activator.CreateInstance(media, true);
            foreach (var field in new Dictionary<string,string>{
                {"ColorTransfer",transfer},{"ColorPrimaries","bt2020"},{"ColorSpace","bt2020nc"},{"ColorRange","tv"},
                {"FieldOrder",order},{"ContainerFieldOrder",order}})
                media.GetField(field.Key, Members).SetValue(info, field.Value);
            if (!((bool)media.GetProperty("IsInterlaced", Members).GetValue(info, null)))
                throw new InvalidOperationException("fake source must be interlaced");
            var deinterlace = core.GetMethod("DeinterlaceFilter", Methods, null, new[]{typeof(string),media}, null);
            string fieldFilter = (string)deinterlace.Invoke(null,new[]{method,info});
            string parity = order == "tt" ? "tff" : "bff";
            Require(fieldFilter.Contains("parity="+parity) && fieldFilter.Contains("setfield=prog"), "field parity");
            var cpu = (List<string>)core.GetMethod("BuildToSdr", Methods).Invoke(null,
                new object[]{"source.mkv","work.mkv",info,"hable",false,fieldFilter});
            var gpu = (List<string>)core.GetMethod("BuildGpuArguments", Methods).Invoke(null,new object[]{cpu,null});
            foreach (var hdr in new[]{cpu,gpu})
            foreach (bool videoOnly in new[]{false,true})
            {
                var original = new List<string>{"-i","source.mkv","-filter_complex","[0:v:0]format=p010le[vout]",
                    "-map","[vout]","-c:v",videoOnly?"av1_nvenc":"hevc_nvenc","-r","60000/1001","-fps_mode:v","cfr","output.mkv"};
                var merged = (List<string>)core.GetMethod("MergeEncodeArguments", Methods).Invoke(null,
                    new object[]{original,hdr,videoOnly,0.5d,0.32d,"",false});
                string graph = merged[merged.IndexOf("-filter_complex")+1];
                var nut = (List<string>)core.GetMethod("BuildSdrNutArguments", Methods).Invoke(null,new object[]{hdr});
                Require(nut[nut.IndexOf("-c:v")+1]=="rawvideo" && nut[nut.IndexOf("-pix_fmt")+1]=="yuv420p10le"
                    && nut[nut.Count-2]=="nut" && nut[nut.Count-1]=="pipe:1" && !nut.Contains("hevc_nvenc")
                    && nut[nut.IndexOf("-vf")+1]==hdr[hdr.IndexOf("-vf")+1], "lossless stdin feed");
                int tonemap = graph.IndexOf(hdr==gpu?"libplacebo=tonemapping=hable":"tonemap=tonemap=hable");
                Require(tonemap > graph.IndexOf(fieldFilter) && graph.IndexOf(fieldFilter) >= 0, "bob must precede tone mapping");
                Require(graph.Contains("color_trc=bt709") && graph.Contains("MASTERING_DISPLAY_METADATA"), "SDR color contract");
                Require(!merged.Contains("hevc_nvenc") || !videoOnly, "unexpected HEVC intermediary");
                Require(merged.Contains("-r")==videoOnly, "AV1 bob CFR / ordinary field passthrough");
                Require(videoOnly ? !merged.Contains("-itsoffset") : graph.Contains("0.32/TB") && merged.Contains("-0.5"),
                    "A/V origin handling");
                Require(merged.Count(a => a=="-filter_hw_device") <= 1, "duplicate Vulkan device");
            }
        }

        string pipeGraph = (string)av1.GetMethod("ConvertedSdrPipeGraph", Methods).Invoke(null,
            new object[]{"[0:v:0]format=p010le[vout]"});
        Require(pipeGraph.Contains("format=yuv420p,settb=expr=1/1000,") && pipeGraph.Contains("color_trc=bt709"),
            "SDR decoded FFV1 boundary not restored");
        foreach (string engine in new[]{"SVP","NVENC_AI","NVENC_FRUC"})
        {
            object encode = Activator.CreateInstance(stage,true);
            var args = new List<string>{"-f","yuv4mpegpipe","-i","pipe:0","-filter_complex",pipeGraph,"output.ivf"};
            stage.GetProperty("DirectArguments", Members).SetValue(encode,string.Join(" ",args),null);
            var state = new Dictionary<string,string>{{"FG_INTERPOLATION_ENGINE",engine},
                {"FG_NVENC_GPU_NAME","GPU"},{"FG_NVENC_DEVICES","DeviceId #0: GPU (PCI Bus ID:"},
                {"FG_NVENC_SOURCE_FPS","30000/1001"},{"FG_NVENC_DIR",@"C:\Tool"}};
            bool useNvenc = engine != "SVP";
            if (useNvenc)
            {
                nvenc.GetMethod("AttachProducer", Methods).Invoke(null,new object[]{encode,"converted.mkv",state,"60000/1001"});
                ((Action)stage.GetProperty("PreparedObserver", Members).GetValue(encode,null))();
                string consumer = (string)stage.GetProperty("DirectArguments", Members).GetValue(encode,null);
                Require(consumer.Contains("-f nut") && consumer.Contains("setpts=PTS-STARTPTS,format=yuv420p")
                    && consumer.Contains("settb=expr=1/1000,setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709"),"NUT clock/depth/color");
                string nvargs = (string)stage.GetProperty("PipeProducerArguments", Members).GetValue(encode,null);
                Require(nvargs.Contains(engine=="NVENC_FRUC"?"--vpp-fruc":"--vpp-nvvfx-framegen"),"wrong NVEncC producer");
                if (engine=="NVENC_FRUC") Require(nvargs.Contains("fps=60000/1001") && !nvargs.Contains("--vpp-nvvfx-framegen"),"FRUC target cadence");
            }
            else svp.GetMethod("AttachProducer", Methods).Invoke(null,
                new object[]{encode,"vspipe.exe","svp.vpy","plugins","converted.mkv",state,"60000/1001"});
            string producer = (string)stage.GetProperty("PipeProducerArguments", Members).GetValue(encode,null);
            Require(producer.Contains("converted.mkv") && (useNvenc?producer.Contains("\"--output-depth\" \"8\""):
                producer.Contains("target_num=60000") && producer.Contains("target_den=1001") && !producer.Contains("hdr10=1")), "producer SDR contract");
        }
        Console.WriteLine("PASS R08a 48 command combinations and 16 route guards; R08b 32 guards and all three producer contracts (SVP, NVEncC AI, NVEncC FRUC)");
    }
}
'@
[R08HdrSdrAudit]::Run((Resolve-Path -LiteralPath $Exe).Path)
