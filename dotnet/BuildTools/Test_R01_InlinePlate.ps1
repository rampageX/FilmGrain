param([string]$Exe = (Join-Path $PSScriptRoot '..\..\FilmGrain_Studio.exe'))
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw ('Missing built EXE: ' + $Exe) }
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Reflection;

public static class FgsR01Audit
{
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static object Media(Type type, string order, string containerOrder)
    {
        var media = Activator.CreateInstance(type, true);
        type.GetField("FieldOrder").SetValue(media, order);
        type.GetField("ContainerFieldOrder").SetValue(media, containerOrder);
        return media;
    }
    public static void Run(string path)
    {
        var assembly = Assembly.LoadFrom(path);
        var core = assembly.GetType("FilmGrainStudioPreview.SharedHdrPreprocessCore", true);
        var mediaType = assembly.GetType("FilmGrainStudioPreview.MediaProbeInfo", true);
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var inline = core.GetMethod("InlineVulkanPlateFilter", flags);
        var timeline = core.GetMethod("BuildFieldTimeline", flags);
        var quantize = core.GetMethod("QuantizeInlineFieldDelay", flags);
        var software = core.GetMethod("InlineVulkanPlateSoftwareFilter", flags);
        Require(inline != null && timeline != null && quantize != null, "R01 shared methods missing");
        Require(Math.Abs((double)quantize.Invoke(null, new object[] {0.320, "60000/1001"}) - 0.317) < 0.0000001,
            "fractional field delay must match original FFV1/MKV clock");
        foreach (var order in new [] { "tt", "bb", "tb", "bt" })
        {
            var media = Media(mediaType, order, order);
            var graph = (string)inline.Invoke(null, new object[] {media, "60000/1001"});
            string parity = order == "tt" || order == "tb" ? "tff" : "bff";
            Require(graph.Contains("bwdif_vulkan=mode=send_field:parity=" + parity + ":deint=all"), "field parity changed for " + order);
            Require(graph.Contains("setfield=prog,"), "progressive field marker missing");
            Require(!graph.Contains("hwdownload") && !graph.Contains("hwupload_cuda"), "direct GPU path must retain Vulkan frames");
            Require(graph.Contains("hwupload,bwdif_vulkan"), "GPU filter order changed");
            var mismatched = Media(mediaType, order, order == "tt" || order == "tb" ? "bb" : "tt");
            var mismatchGraph = (string)inline.Invoke(null, new object[] {mismatched, "60000/1001"});
            Require(mismatchGraph.Contains("parity=auto"), "conflicting stream/container field tags must retain auto parity");
            Require(graph.Contains("settb=1001/60000,setpts=PTS-STARTPTS,settb=1/1000"),
                "legacy fractional field/mux clock quantization missing");
            Require(graph.EndsWith("settb=1/1000", StringComparison.Ordinal),
                "R01 GPU graph must have no dangling comma before [basevk]");
            Require(!graph.Contains(",[basevk]"), "R01 GPU output pad follows an empty filter");
            var softwareGraph = (string)software.Invoke(null, new object[] {media, "60000/1001"});
            Require(softwareGraph.EndsWith("settb=1/1000,", StringComparison.Ordinal),
                "CPU-prefix continuation requires the comma after the clock");
        }
        foreach (var rate in new [] { "50/1", "60000/1001" })
        {
            var media = Media(mediaType, "tt", "tt");
            var completeGraph = "[0:v:0]" + (string)inline.Invoke(null, new object[] {media, rate}) + "[basevk]";
            Require(!completeGraph.Contains(",[basevk]"), "R01 full graph has an empty filter: " + rate);
        }
        foreach (var rate in new [] { "50/1", "60000/1001" })
        foreach (double offset in new [] { -0.375, 0.0, 0.375 })
        {
            double common = Math.Max(0.0, -offset);
            double delay = Math.Max(0.0, offset);
            var arguments = new List<string> { "-hide_banner", "-i", "source.mkv", "-stream_loop", "-1", "-t", "2.0", "-i", "plate.mkv",
                "-filter_complex", "[0:v:0]setpts=PTS-STARTPTS,format=p010le,hwupload,bwdif_vulkan=mode=send_field:parity=tff:deint=all,setfield=prog,setpts=PTS-STARTPTS[basevk];[basevk]null[vout]",
                "-map", "[vout]", "-r", rate, "-fps_mode:v", "cfr", "-t", "2.0", "output.mkv"};
            var result = (List<string>)timeline.Invoke(null, new object[] {arguments, common, delay, false});
            var graph = result[result.IndexOf("-filter_complex")+1];
            Require(graph.Contains("setpts=PTS-STARTPTS,format=p010le"), "source PTS normalization not applied");
            if (delay > 0) Require(graph.Contains("setpts=PTS+"), "video delay missing");
            Require(result.IndexOf("-copyts") >= 0 && result.IndexOf("-itsoffset") >= 0, "original audio clock not retained");
            Require(result.IndexOf("-r") < 0, "2x field-rate forced a second output resample");
            Require(result[result.IndexOf("-fps_mode:v")+1] == "passthrough", "bob field PTS must pass through");
            Require(result[result.IndexOf("-itsoffset")+1] == ("-" + common.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)) || common==0.0, "common origin changed");
        }
        Console.WriteLine("PASS R01 graph/parity/PTS/audio-clock reflection checks (4 parity, 2 rates, 3 offsets, fractional delay)");
    }
}
'@
[FgsR01Audit]::Run((Resolve-Path -LiteralPath $Exe).Path)
