param([string]$Exe = (Join-Path $PSScriptRoot '..\..\FilmGrain_Studio.exe'))
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Reflection;
public static class FgsTimelineStartAudit
{
    static string streams, frames;
    static bool frameSuccess;
    static int calls;
    static void Must(bool ok,string message) { if(!ok) throw new Exception(message); }
    public static bool Probe(string tool,string args,int timeout,out string output,out string reason)
    {
        ++calls;
        Must(args.Contains("\"C:\\Tester & ( ) ^ % ! -=\\中 文.mkv\""),"path quoting changed");
        reason="mock error";
        if(calls==1) { Must(args.Contains("stream=codec_type,start_time"),"normal probe changed");output=streams;return true; }
        Must(calls==2 && args.Contains("-select_streams v:0") && args.Contains("%+#64") && args.Contains("frame=best_effort_timestamp_time"),"unbounded or invalid fallback");
        output=frames;return frameSuccess;
    }
    static string Stream(string type,string time) { return "[STREAM]\ncodec_type="+type+"\nstart_time="+time+"\n[/STREAM]\n"; }
    static string Frame(string time) { return "[FRAME]\nbest_effort_timestamp_time="+time+"\n[/FRAME]\n"; }
    static void Case(MethodInfo method,Delegate probe,string name,string streamData,string frameData,bool success,int expectedCalls,double video,double common,string audio,bool fallback,bool probeSuccess=true)
    {
        calls=0;streams=streamData;frames=frameData;frameSuccess=probeSuccess;
        object result=null;
        try { result=method.Invoke(null,new object[]{"ffprobe.exe","C:\\Tester & ( ) ^ % ! -=\\中 文.mkv",probe}); }
        catch(TargetInvocationException e) { Must(!success && e.InnerException is InvalidOperationException && e.InnerException.Message.Contains("Interpolation"),name+": "+e.InnerException); }
        Must(calls==expectedCalls,name+": call count");
        if(success)
        {
            Must(result!=null,name+": missing timeline");
            var t=result.GetType();var f=BindingFlags.Instance|BindingFlags.NonPublic;
            Must(Math.Abs((double)t.GetField("VideoOrigin",f).GetValue(result)-video)<1e-9,name+": video origin");
            Must(Math.Abs((double)t.GetField("CommonOrigin",f).GetValue(result)-common)<1e-9,name+": common origin");
            Must((string)t.GetField("AudioOrigins",f).GetValue(result)==audio,name+": audio origin");
            Must((bool)t.GetField("VideoOriginFromFrame",f).GetValue(result)==fallback,name+": evidence flag");
        }
        else Must(result==null,name+": missing timing accepted");
        Console.WriteLine("PASS "+name);
    }
    public static void Run(string exe)
    {
        var asm=Assembly.LoadFrom(exe);var core=asm.GetType("FilmGrainStudioPreview.SharedFrucCore",true);
        var probe=Delegate.CreateDelegate(asm.GetType("FilmGrainStudioPreview.SharedFrucCore+ProbeRunner",true),typeof(FgsTimelineStartAudit).GetMethod("Probe"));
        var method=core.GetMethod("ProbeTimelineWithRunner",BindingFlags.Static|BindingFlags.NonPublic);
        Must(method!=null,"Missing timeline test entry");
        Case(method,probe,"known start unchanged",Stream("video","0.32")+Stream("audio","0.08"),"",true,1,.32,.08,"0.08",false);
        Case(method,probe,"video-only zero",Stream("video","N/A"),Frame("0.0"),true,2,0,0,"",true);
        Case(method,probe,"video-only nonzero",Stream("video","N/A"),Frame("1.44"),true,2,1.44,1.44,"",true);
        Case(method,probe,"preserve audio lead",Stream("video","N/A")+Stream("audio","0.08"),Frame("0.32"),true,2,.32,.08,"0.08",true);
        Case(method,probe,"preserve video lead",Stream("video","N/A")+Stream("audio","0.32"),Frame("-0.04"),true,2,-.04,-.04,"0.32",true);
        Case(method,probe,"first frame unknown",Stream("video","N/A"),Frame("N/A")+Frame("0.04"),false,2,0,0,"",false);
        Case(method,probe,"first frame missing field",Stream("video","N/A"),"[FRAME]\n[/FRAME]\n"+Frame("0.04"),false,2,0,0,"",false);
        Case(method,probe,"no frames",Stream("video","N/A"),"",false,2,0,0,"",false);
        Case(method,probe,"probe failed",Stream("video","N/A"),Frame("0.0"),false,2,0,0,"",false,false);
        Case(method,probe,"reject nonfinite",Stream("video","N/A"),Frame("Infinity"),false,2,0,0,"",false);
    }
}
'@
[FgsTimelineStartAudit]::Run((Resolve-Path -LiteralPath $Exe).Path)
