param([string]$Exe = (Join-Path $PSScriptRoot '..\..\FilmGrain_Studio.exe'))
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw ('R05 candidate EXE not found: ' + $Exe) }
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\FilmGrain_WorkspacePreflightCore.cs') -Raw
$gui = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\FilmGrain_Studio_NET_Preview.cs') -Raw
if (($gui.Split(@('WorkspacePreflightCore.CheckPlanned('),[System.StringSplitOptions]::None).Count - 1) -ne 2) { throw 'R05 GUI single/shared/independent queue did not converge on planned preflight' }
if ($gui.Contains('WorkspacePreflightCore.Check(')) { throw 'R05 old speculative GUI preflight remains' }
if (-not $source.Contains('plan.ContinueAfterStages != null')) { throw 'R05 deferred preview safety guard missing' }
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
public static class R05WorkspaceCheck {
  static void Must(bool b, string message) { if (!b) throw new Exception(message); }
  public static void Run(string exe) {
    Assembly a=Assembly.LoadFrom(exe);
    Type r=a.GetType("FilmGrainStudioPreview.BridgeExecutionRequest", true);
    Type st=a.GetType("FilmGrainStudioPreview.BridgeExecutionStage", true);
    Type workspace=a.GetType("FilmGrainStudioPreview.WorkspacePreflightCore", true);
    MethodInfo test=workspace.GetMethod("IsPlannedFrucWorkfile",BindingFlags.Static|BindingFlags.NonPublic);
    Must(test!=null,"R05 planned workfile entry missing");
    string file=Path.Combine(Path.GetTempPath(),"FGS_R05_test_svp.mkv");
    string[] cases={
      "-f yuv4mpegpipe -i pipe:0 -c:v ffv1 \""+file+"\"",
      "-f nut -i pipe:0 -c:v av1_nvenc \""+file+"\"",
      "-i input.mp4 -c:v hevc_nvenc \""+file+"\""
    };
    for(int i=0;i<cases.Length;i++) {
      object req=Activator.CreateInstance(r,true);
      object stage=Activator.CreateInstance(st,true);
      st.GetProperty("DirectArguments").SetValue(stage,cases[i],null);
      Type listType=typeof(List<>).MakeGenericType(st);
      IList stages=(IList)Activator.CreateInstance(listType);
      stages.Add(stage);
      r.GetProperty("DirectStages").SetValue(req,stages,null);
      bool got=(bool)test.Invoke(null,new object[]{req,file});
      Must(got==(i==0),"R05 actual-FFV1 stage detector failed at case "+i);
    }
    Console.WriteLine("PASS R05 3/3 stage-aware FFV1 detection, includes direct and non-FFV1 guards");
  }
}
'@
[R05WorkspaceCheck]::Run((Resolve-Path -LiteralPath $Exe).Path)
Write-Host 'PASS R05 GUI three entrances use planned requests; preview deferred safety guard present'
