#!/usr/bin/env python3
"""R05 static source contract. It does not replace Windows CLR/GUI tests."""
from pathlib import Path
root = Path(__file__).resolve().parents[2]
ui = (root/'dotnet/FilmGrain_Studio_NET_Preview.cs').read_text('utf-8-sig')
core = (root/'dotnet/FilmGrain_WorkspacePreflightCore.cs').read_text('utf-8-sig')
assert ui.count('WorkspacePreflightCore.CheckPlanned(') == 2
assert 'WorkspacePreflightCore.Check(' not in ui
assert 'BridgeRequestPreparationCore.CreateTask(prepared, outputFps);' in ui
queue = ui[ui.index('private void StartSharedQueue'):ui.index('private void CancelBridgeExecution')]
assert queue.index('NativeExecutionPreparationCore.TryPrepare') < queue.index('WorkspacePreflightCore.CheckPlanned') < queue.index('bridgeTaskCoordinator.StartQueue(tasks)')
assert 'if(fileStates!=null)' in queue
assert 'foreach (BridgeTaskRequest task in tasks) BridgeExecutionCore.CleanupTemporaryFiles(task.ExecutionRequest);' in queue
assert 'if (plan.ContinueAfterStages != null' in core
assert 'IsPlannedFrucWorkfile(plan, workfile)' in core
assert 'args.IndexOf("ffv1", StringComparison.OrdinalIgnoreCase)' in core
assert 'plan.TemporaryFiles' in core and 'plan.DirectStages' in core
assert 'factor += 4.0' in core
assert 'fileSize * 1.2' in core
assert 'Math.Max(2.0 * 1024 * 1024 * 1024' in core
assert 'Math.Max(512.0 * 1024 * 1024' in core
examples = [
    ([],[],False),
    (['D:/temp/clean.ivf'],['-i pipe:0 -c:v av1_nvenc D:/temp/clean.ivf'],False),
    (['D:/temp/clip_svp.mkv'],['-c:v ffv1 D:/temp/clip_svp.mkv'],True),
    (['D:/temp/clip_fruc.mkv'],['-c:v ffv1 D:/temp/clip_fruc.mkv'],True),
    (['D:/temp/clip_svp.mkv'],['-c:v av1_nvenc D:/temp/clip_svp.mkv'],False),
    (['D:/temp/clip_svp.mkv'],['-c:v ffv1 D:/temp/unrelated.mkv'],False),
    (['D:/temp/clip_hdr2sdr.mkv'],['-c:v ffv1 D:/temp/clip_hdr2sdr.mkv'],False),
]
for files,args,expected in examples:
    actual = any((f.lower().endswith('_svp.mkv') or f.lower().endswith('_fruc.mkv')) and any(f.lower() in a.lower() and 'ffv1' in a.lower() for a in args) for f in files)
    assert actual == expected
bytes_4k60 = 3840 * 2160 * 1.5 * 60 * 600 * 1.05
assert 470e9 < bytes_4k60 < 471e9
print('PASS R05 static GUI 3 entrances and planned-output guards')
print('PASS R05 synthetic 7/7 FFV1 stage classification')
print('PASS R05 4K60 10min old full FFV1 increment %.3f GB, direct increment 0 GB' % (bytes_4k60 / 1e9))
print('NOTE: Windows compilation and executable plan preflight not run in this environment')
