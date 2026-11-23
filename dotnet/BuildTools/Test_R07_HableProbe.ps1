param([Parameter(Mandatory=$true)][string]$Exe)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Reflection;

public static class R07HableProbeAudit
{
    const BindingFlags Methods = BindingFlags.Static | BindingFlags.NonPublic;
    const BindingFlags Properties = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static bool Vulkan, Hevc;
    static int VulkanCalls, HevcCalls;
    static bool Software;

    static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    public static bool Probe(string tool, string args, int timeout, out string output, out string reason)
    {
        output = "";
        if (args.Contains("hevc_nvenc"))
        {
            HevcCalls++;
            Check(args.Contains("main10") && args.Contains("lossless") && args.Contains("format=p010le")
                && !args.Contains("libplacebo"), "workfile probe mixes encoder and Vulkan capabilities");
            reason = "simulated HEVC failure";
            return Hevc;
        }
        VulkanCalls++;
        Check(args.Contains("wrapped_avframe") && args.Contains("libplacebo=tonemapping=hable")
            && args.Contains("\"-frames:v\" \"3\""), "GPU probe must consume the actual Hable graph");
        if (Software) output = "device name: llvmpipe";
        reason = "simulated Vulkan failure";
        return Vulkan;
    }

    public static void Run(string exe)
    {
        var asm = Assembly.LoadFrom(exe);
        string ns = "FilmGrainStudioPreview.";
        var core = asm.GetType(ns + "SharedHdrPreprocessCore", true);
        var media = asm.GetType(ns + "MediaProbeInfo", true);
        var stageType = asm.GetType(ns + "BridgeExecutionStage", true);
        var probe = Delegate.CreateDelegate(asm.GetType(ns + "SharedFrucCore+ProbeRunner", true),
            typeof(R07HableProbeAudit).GetMethod("Probe"));
        var select = core.GetMethod("SelectAutomatic", Methods);
        foreach (string transfer in new[]{"smpte2084", "arib-std-b67"})
        {
            object info = Activator.CreateInstance(media, true);
            media.GetField("ColorTransfer", Properties).SetValue(info, transfer);
            media.GetField("ColorPrimaries", Properties).SetValue(info, "bt2020");
            media.GetField("ColorSpace", Properties).SetValue(info, "bt2020nc");
            media.GetField("ColorRange", Properties).SetValue(info, "tv");
            var cpu = (List<string>)core.GetMethod("BuildToSdr", Methods).Invoke(null,
                new object[]{"input.mkv", "temp.mkv", info, "hable", true, ""});
            foreach (string codec in new[]{"av1_nvenc", "hevc_nvenc", "libx264", "libx265", "libsvtav1"})
            {
                foreach (bool available in new[]{true, false})
                {
                    Vulkan = available;
                    Hevc = false;
                    int before = HevcCalls;
                    object[] selection = {"ffmpeg.exe", cpu, "hable", null, null, probe};
                    var hdr = (List<string>)select.Invoke(null, selection);
                    var original = new List<string>{"-i","input.mkv","-filter_complex","[0:v:0]null[vout]",
                        "-map","[vout]","-c:v",codec,"output.mkv"};
                    bool plainHevc = codec == "hevc_nvenc";
                    var merged = (List<string>)core.GetMethod(plainHevc ? "MergeHevcArguments" : "MergeEncodeArguments", Methods)
                        .Invoke(null, plainHevc ? new object[]{original,hdr} :
                            new object[]{original,hdr,false,0d,0d,"",true});
                    string graph = merged[merged.IndexOf("-filter_complex")+1];
                    Check(HevcCalls == before && merged[merged.IndexOf("-c:v")+1] == codec, "target encoder changed");
                    Check(graph.Contains(available ? "peak_detect=1" : "format=gbrpf32le"), "wrong HDR graph");
                    Check(graph.Contains(transfer) && graph.Contains("color_trc=bt709")
                        && graph.Contains("color_primaries=bt709") && graph.Contains("colorspace=bt709")
                        && graph.Contains("MASTERING_DISPLAY_METADATA") && graph.Contains("CONTENT_LIGHT_LEVEL"), "color contract changed");
                    Check(merged[merged.IndexOf("-color_range")+1] == "tv" &&
                        merged[merged.IndexOf("-color_trc")+1] == "bt709", "output signaling changed");
                    Check(((string)selection[3]).Contains(available ? "Vulkan filter check passed" : "CPU fallback"), "wrong log");
                    Console.WriteLine("PASS {0} {1} {2}", transfer, codec, selection[3]);
                }
            }

            object stage = Activator.CreateInstance(stageType, true);
            stageType.GetProperty("OriginalArguments", Properties).SetValue(stage, cpu, null);
            stageType.GetProperty("ToolPath", Properties).SetValue(stage, "ffmpeg.exe", null);
            core.GetMethod("AttachAutomatic", Methods).Invoke(null, new object[]{stage,"hable",probe});
            var prepare = (Func<string>)stageType.GetProperty("BeforeRunPreparation", Properties).GetValue(stage,null);
            int gpuBefore = VulkanCalls;
            try { prepare(); throw new Exception("Missing workfile encoder was accepted"); }
            catch (InvalidOperationException e)
            {
                Check(e.Message.Contains("requires HEVC NVENC Main10 lossless") && e.Message.Contains("simulated HEVC failure")
                    && VulkanCalls == gpuBefore, "missing encoder must fail before filter selection");
            }
            Hevc = true;
            Vulkan = true;
            Check(prepare().Contains("workfile encoder check passed"), "missing encoder success log");
            Check(((string)stageType.GetProperty("DirectArguments", Properties).GetValue(stage,null)).Contains("hevc_nvenc"),
                "workfile lost required HEVC encoder");
            Software = true;
            Check(prepare().Contains("software Vulkan selected"), "software Vulkan must fall back");
            Software = false;
            int calls = VulkanCalls;
            object[] nonHable = {"ffmpeg.exe",cpu,"mobius",null,null,probe};
            select.Invoke(null, nonHable);
            Check(VulkanCalls == calls && ((string)nonHable[3]).Contains("selected algorithm=mobius"), "non-Hable was probed");
        }
    }
}
'@
[R07HableProbeAudit]::Run((Resolve-Path -LiteralPath $Exe).Path)
