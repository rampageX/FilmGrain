using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class CommandAudit
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
    private static void Set(object value, string name, object item) { value.GetType().GetProperty(name, Flags).SetValue(value, item, null); }
    private static void Field(object value, string name, object item) { value.GetType().GetField(name, Flags).SetValue(value, item); }
    private static string Option(IList<string> args, string name) { return args[args.IndexOf(name) + 1]; }

    private static int Main(string[] args)
    {
        var assembly = Assembly.LoadFrom(args[0]);
        var core = assembly.GetType("FilmGrainStudioPreview.TrueHdrCore", true);
        var options = Activator.CreateInstance(assembly.GetType("FilmGrainStudioPreview.TrueHdrOptions", true));
        var source = Activator.CreateInstance(assembly.GetType("FilmGrainStudioPreview.MediaProbeInfo", true));
        var build = core.GetMethod("Arguments", Flags);
        var retry = core.GetMethod("LiveFrameRetryBudget", Flags);
        Require((int)retry.Invoke(null, new object[] { "avsw: all frames in the trim range are before the first keyframe and cannot be decoded (offset 119).", 1 }) == 120, "NVEncC keyframe retry");
        Require((int)retry.Invoke(null, new object[] { "failed to initialize file reader(s).", 1 }) == 0, "Unrelated NVEncC errors are not retried");
        var frames = assembly.GetType("FilmGrainStudioPreview.TrueHdrPreviewFrames", true);
        double white = (double)frames.GetMethod("PqToNits", Flags).Invoke(null, new object[] { 1.0 });
        Require(Math.Abs(white - 10000) < 0.001, "ST 2084 white level");
        Require((string)core.GetMethod("Speed", Flags).Invoke(null, new object[] { "60", 24000.0 / 1001 }) == "2.5", "TrueHDR speed multiplier");
        var live = (IList<string>)core.GetMethod("LiveFrameArguments", Flags).Invoke(null, new object[] {
            "test.mkv", "frame.y4m", 2.5, new[] { 125, 75, 44, 1000 }, false });
        Require(Option(live, "--codec") == "raw" && Option(live, "--frames") == "1" &&
            Option(live, "--seek") == "2.5" && !live.Contains("--output-depth") && Option(live, "--output-format") == "y4m" &&
            Option(live, "--vpp-ngx-truehdr") == "contrast=125,saturation=75,middlegray=44,maxluminance=1000", "Live TrueHDR frame");
        var interlacedLive = (IList<string>)core.GetMethod("LiveFrameArguments", Flags).Invoke(null, new object[] {
            "test.mkv", "frame.y4m", 2.5, new[] { 125, 75, 44, 1000 }, true });
        Require(Option(interlacedLive, "--frames") == "2" && Option(interlacedLive, "--vpp-yadif") == "mode=auto", "YADIF preview and lookahead");
        Require((int)retry.Invoke(null, new object[] { "all frames in the trim range are before the first keyframe and cannot be decoded (offset 119)", 2 }) == 121, "YADIF preview retry keeps lookahead");
        if (args.Length > 2) core.GetMethod("RequireTrueHdr", Flags).Invoke(null, new object[] { args[2] });
        var help = core.GetMethod("AdvertisesTrueHdr", Flags);
        Require((bool)help.Invoke(null, new object[] { "  --vpp-ngx-truehdr [<param1>=<value>]" }), "Optional help syntax");
        Require(!(bool)help.Invoke(null, new object[] { "  --vpp-ngx-truehdr-experimental" }), "Help option boundary");
        if (args.Length > 1) Require((bool)help.Invoke(null, new object[] { File.ReadAllText(args[1]) }), "Release help syntax");
        Field(source, "HasVideo", true);
        Field(source, "Width", 1920);
        Field(source, "Height", 1080);
        Field(source, "AvgFrameRate", "24000/1001");
        Field(source, "ColorTransfer", "bt709");
        Field(source, "ColorSpace", "smpte170m");
        Field(source, "ColorRange", "pc");
        Field(source, "HasAudio", true);
        Field(source, "AudioCodec", "aac");
        string input = @"I:\-=Converter=-\素材 & (1).mkv";
        string output = @"I:\-=Converter=-\成片.mkv";
        Field(source, "Width", 2);
        Field(source, "Height", 2);
        var read = frames.GetMethods(Flags).Single(m => m.Name == "Read" && m.GetParameters()[0].ParameterType == typeof(Stream));
        foreach (bool hdr in new[] { false, true })
        foreach (bool tenBit in new[] { false, true })
        {
            string header = "YUV4MPEG2 W2 H2 F24:1 Ip A1:1 C" + (tenBit ? "420p10" : "420mpeg2") + " XCOLORRANGE=LIMITED\nFRAME\n";
            byte[] raw = System.Text.Encoding.ASCII.GetBytes(header).Concat(tenBit ? new byte[] { 64, 0, 64, 0, 172, 3, 172, 3, 0, 2, 0, 2 } : new byte[] { 16, 16, 235, 235, 128, 128 }).ToArray();
            using (var stream = new MemoryStream(raw))
            using (var bmp = (IDisposable)read.Invoke(null, new object[] { stream, source, hdr, 1000, System.Threading.CancellationToken.None }))
            {
                var getPixel = bmp.GetType().GetMethod("GetPixel");
                object black = getPixel.Invoke(bmp, new object[] { 0, 0 });
                object whitePixel = getPixel.Invoke(bmp, new object[] { 0, 1 });
                var red = black.GetType().GetProperty("R");
                Require((byte)red.GetValue(black, null) == 0 && (byte)red.GetValue(whitePixel, null) == 255, "Y4M preview black and white");
            }
        }
        Field(source, "Width", 1920);
        Field(source, "Height", 1080);
        var invoke = new object[] { input, output, options, source, new Dictionary<string, string>() };
        var command = (IList<string>)build.Invoke(null, invoke);
        Require(Option(command, "--vpp-ngx-truehdr") == "contrast=125,saturation=75,middlegray=44,maxluminance=1000", "Picture defaults");
        Require(command.Count(v => v.StartsWith("--vpp", StringComparison.Ordinal)) == 1 && !command.Contains("--max-cll"), "Only TrueHDR and no fabricated content light levels");
        Require(Option(command, "--colormatrix") == "bt2020nc" && Option(command, "--colorprim") == "bt2020" &&
            Option(command, "--transfer") == "smpte2084" && Option(command, "--output-depth") == "10", "HDR10 output");
        Require(Option(command, "-i") == input && Option(command, "-o") == output && Option(command, "--audio-copy") == "1", "Paths and audio");
        Require((string)source.GetType().GetField("ColorTransfer").GetValue(source) == "bt709" &&
            (string)source.GetType().GetField("ColorSpace").GetValue(source) == "smpte170m", "SDR source unchanged");
        Require(!command.Contains("--seek") && !command.Contains("--seekto"), "Full encode timeline");
        Field(source, "FieldOrder", "tt");
        var interlaced = (IList<string>)build.Invoke(null, invoke);
        Require(Option(interlaced, "--vpp-yadif") == "mode=auto" &&
            interlaced.IndexOf("--vpp-yadif") < interlaced.IndexOf("--vpp-ngx-truehdr"), "Interlaced SDR YADIF before TrueHDR");
        Field(source, "FieldOrder", "progressive");
        Set(options, "PreviewStart", "00:01:05");
        Set(options, "PreviewSeconds", 30);
        command = (IList<string>)build.Invoke(null, invoke);
        Require(Option(command, "--seek") == "65" && Option(command, "--seekto") == "95", "Preview timeline");
        Set(options, "PreviewStart", "bad");
        RequireRejected(build, invoke, "Invalid preview timestamp");
        Set(options, "PreviewSeconds", 0);
        Set(options, "Codec", "AV1");
        Set(options, "Cq", true);
        Set(options, "Auto", false);
        Set(options, "Target", "25.5");
        Set(options, "Picture", new[] { 0, 200, 10, 2000 });
        command = (IList<string>)build.Invoke(null, invoke);
        Require(Option(command, "--codec") == "av1" && Option(command, "--profile") == "main" &&
            Option(command, "--vbr") == "0" && Option(command, "--vbr-quality") == "25.5", "AV1 CQ output");
        Require(Option(command, "--master-display").Contains("L(20000000,1)"), "Mastering luminance follows target");
        Set(options, "Picture", new[] { -1, 200, 10, 2000 });
        RequireRejected(build, invoke, "Picture range");
        Set(options, "Picture", new[] { 0, 200, 10, 2000 });
        Field(source, "ColorTransfer", "smpte2084");
        RequireRejected(build, invoke, "HDR input rejected");
        Field(source, "ColorTransfer", "bt709");
        Field(source, "VideoStreamCount", 2);
        RequireRejected(build, invoke, "NVEncC track selection ambiguity rejected");
        Console.WriteLine("PASS TrueHDR command, input/output state, codec, rate, bounds and effect isolation");
        return 0;
    }

    private static void RequireRejected(MethodInfo method, object[] args, string reason)
    {
        try { method.Invoke(null, args); }
        catch (TargetInvocationException error) { if (error.InnerException is InvalidDataException) return; throw; }
        throw new InvalidDataException(reason);
    }
}
