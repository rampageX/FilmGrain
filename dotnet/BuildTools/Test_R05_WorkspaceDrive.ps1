param([string]$Exe = (Join-Path $PSScriptRoot '..\..\FilmGrain_Studio.exe'))
$ErrorActionPreference='Stop'
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw ('Missing R05 EXE: ' + $Exe) }
Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;

public static class R05DriveBudgetAudit
{
    static Assembly asm;
    static Type requestType, stageType, taskType, mediaType, warningType, issueType;
    static MethodInfo check;
    public static int Warnings;
    public static double LastRequired;
    public static bool Accept = true;

    static void Must(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    static PropertyInfo Prop(Type type, string name)
    {
        var p = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (p == null) throw new Exception("Missing property: " + type.Name + "." + name);
        return p;
    }
    static void Set(Type type, object obj, string name, object value)
    {
        Prop(type,name).SetValue(obj,value,null);
    }
    public static bool Record(object warning)
    {
        Warnings++;
        var f = warningType.GetField("Required", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        LastRequired = Math.Max(LastRequired, (double)f.GetValue(warning));
        return Accept;
    }
    static Delegate Callback()
    {
        var w = Expression.Parameter(warningType, "warning");
        var call = Expression.Call(typeof(R05DriveBudgetAudit).GetMethod("Record"), Expression.Convert(w, typeof(object)));
        return Expression.Lambda(typeof(Func<,>).MakeGenericType(warningType, typeof(bool)), call, w).Compile();
    }
    static object MakePlan(string input, string tempDir, string outputDir, bool ffv1)
    {
        var r = Activator.CreateInstance(requestType,true);
        var e = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        e["FG_MODE"] = "AV1";
        e["FG_TEMP_MODE"] = "CUSTOM";
        e["FG_TEMP_CUSTOM_DIR"] = tempDir;
        e["FG_OUTPUT_MODE"] = "CUSTOM";
        e["FG_OUTPUT_CUSTOM_DIR"] = outputDir;
        e["FG_SVP_INTERPOLATE"] = "1";
        e["FG_INTERPOLATION_TARGET_FPS"] = "60000/1001";
        e["FG_PREVIEW_SECONDS"] = "10";
        Set(requestType,r,"Environment",e);
        Set(requestType,r,"OutputFiles",new List<string>{Path.Combine(outputDir,"final.mkv")});
        string intermediate = Path.Combine(tempDir,"__FGS_TEST_svp.mkv");
        Set(requestType,r,"TemporaryFiles",ffv1 ? new List<string>{intermediate} : new List<string>());
        var stages=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(stageType));
        var stage=Activator.CreateInstance(stageType,true);
        Set(stageType,stage,"DirectArguments",ffv1 ? "-i input.mkv -c:v ffv1 \""+intermediate+"\"" :
            "-f nut -i pipe:0 -c:v av1_nvenc \""+Path.Combine(outputDir,"final.ivf")+"\"");
        stages.Add(stage);
        Set(requestType,r,"DirectStages",stages);
        return r;
    }
    static object MakeTask(object request)
    {
        var task=Activator.CreateInstance(taskType,true);
        Set(taskType,task,"ExecutionRequest",request);
        Set(taskType,task,"NoReencode",false);
        return task;
    }
    static bool Check(string input, object[] tasks, object info, out object issue)
    {
        var t=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(taskType));
        foreach (object task in tasks) t.Add(task);
        var cache=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string),mediaType));
        cache.Add(input,info);
        var names=new List<string>();
        foreach(object task in tasks) names.Add(input);
        var args=new object[]{names,t,cache,Callback(),null};
        bool valid=(bool)check.Invoke(null,args);
        issue=args[4];
        return valid;
    }
    public static void Run(string exe)
    {
        asm=Assembly.LoadFrom(exe);
        requestType=asm.GetType("FilmGrainStudioPreview.BridgeExecutionRequest",true);
        stageType=asm.GetType("FilmGrainStudioPreview.BridgeExecutionStage",true);
        taskType=asm.GetType("FilmGrainStudioPreview.BridgeTaskRequest",true);
        mediaType=asm.GetType("FilmGrainStudioPreview.MediaProbeInfo",true);
        warningType=asm.GetType("FilmGrainStudioPreview.WorkspaceSpaceWarning",true);
        issueType=asm.GetType("FilmGrainStudioPreview.WorkspacePreflightIssue",true);
        check=asm.GetType("FilmGrainStudioPreview.WorkspacePreflightCore",true)
            .GetMethod("CheckPlanned", BindingFlags.NonPublic | BindingFlags.Static);
        Must(check!=null, "Planned workspace method missing");
        string root=Path.Combine(Path.GetTempPath(),"FGS_R05_WinCI_"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string input=Path.Combine(root,"fake-source.mp4");
            File.WriteAllBytes(input,new byte[2048]);
            string temp=Path.Combine(root,"tmp"), output=Path.Combine(root,"output");
            var info=Activator.CreateInstance(mediaType,true);
            mediaType.GetField("Width").SetValue(info,3840);
            mediaType.GetField("Height").SetValue(info,2160);
            mediaType.GetField("Duration").SetValue(info,"600.0");
            mediaType.GetField("AvgFrameRate").SetValue(info,"30000/1001");

            Warnings=0;LastRequired=0;Accept=true;
            object unused;
            var direct=MakeTask(MakePlan(input,temp,output,false));
            Must(Check(input,new[]{direct},info,out unused), "Direct planned request rejected");
            Must(unused==null && Warnings==0,"Direct AV1 path produced a false space warning");
            Console.WriteLine("PASS R05 direct NUT/AV1 stage: no speculative FFV1 reserve");

            Warnings=0;LastRequired=0;
            var legacy=MakeTask(MakePlan(input,temp,output,true));
            Must(Check(input,new[]{legacy},info,out unused),"Legacy FFV1 plan incorrectly rejected");
            Must(unused==null && Warnings>0 && LastRequired>450e9,
                "Real disk availability check did not expose 4K60/10min FFV1 warning");
            double legacyRequired=LastRequired;
            Console.WriteLine("PASS R05 legacy FFV1 path: actual disk shortage detected, required="+(legacyRequired/1e9).ToString("F2")+" GB");

            Warnings=0;LastRequired=0;Accept=false;
            Must(!Check(input,new[]{legacy},info,out unused) && Warnings>0,
                "User-declined space warning did not stop execution");
            Console.WriteLine("PASS R05 insufficient-space decline stops planned execution");

            Warnings=0;LastRequired=0;Accept=true;
            Must(Check(input,new[]{direct,legacy},info,out unused),"Mixed queue plan rejected");
            Must(unused==null && Warnings>0 && Math.Abs(LastRequired-legacyRequired)<1,
                "Mixed queue should take maximum simultaneous temp budget, not sum or skip");
            Console.WriteLine("PASS R05 shared/independent queue frozen plan aggregation");

            var blocked=MakePlan(input,Path.Combine(input,"not-a-directory"),output,false);
            Warnings=0;LastRequired=0;
            Must(!Check(input,new[]{MakeTask(blocked)},info,out unused) && unused!=null,
                "Unwritable temporary workspace must hard-fail");
            var key=(string)issueType.GetField("Key",BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance).GetValue(unused);
            Must(key=="error.temp_unavailable","Unwritable path reported wrong error");
            Console.WriteLine("PASS R05 invalid temp directory fails before execution");

            Console.WriteLine("PASS R05 five dynamic workspace-boundary assertions on Windows");
        }
        finally
        {
            try { Directory.Delete(root,true); } catch {}
        }
    }
}
'@
[R05DriveBudgetAudit]::Run((Resolve-Path -LiteralPath $Exe).Path)
