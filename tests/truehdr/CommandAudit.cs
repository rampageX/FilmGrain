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
        var invoke = new object[] { input, output, options, source, new Dictionary<string, string>() };
        var command = (IList<string>)build.Invoke(null, invoke);
        Require(Option(command, "--vpp-ngx-truehdr") == "contrast=125,saturation=75,middlegray=44,maxluminance=1000", "Picture defaults");
        Require(command.Count(v => v.StartsWith("--vpp", StringComparison.Ordinal)) == 1 && !command.Contains("--max-cll"), "Only TrueHDR and no fabricated content light levels");
        Require(Option(command, "--colormatrix") == "bt2020nc" && Option(command, "--colorprim") == "bt2020" &&
            Option(command, "--transfer") == "smpte2084" && Option(command, "--output-depth") == "10", "HDR10 output");
        Require(Option(command, "-i") == input && Option(command, "-o") == output && Option(command, "--audio-copy") == "1", "Paths and audio");
        Require((string)source.GetType().GetField("ColorTransfer").GetValue(source) == "bt709" &&
            (string)source.GetType().GetField("ColorSpace").GetValue(source) == "smpte170m", "SDR source unchanged");
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
