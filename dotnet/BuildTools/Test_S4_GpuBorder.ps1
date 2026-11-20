$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot '..\..\FilmGrain_Studio.exe'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $exe).Path)
$core = $assembly.GetType('FilmGrainStudioPreview.FilmBorderEncodingCore', $true)
$planType = $assembly.GetType('FilmGrainStudioPreview.FilmBorderEncodingCore+Plan', $true)
$flags = [Reflection.BindingFlags]'Static, NonPublic'
$fields = [Reflection.BindingFlags]'Instance, NonPublic, Public'
$method = $core.GetMethod('AppendGpuFilter', $flags)
function Make-Graph([int]$width, [int]$height, [int]$x, [int]$y, [int]$cw, [int]$ch, [int]$idx, [bool]$legacy, [bool]$dynamic = $true) {
    $plan = [Activator]::CreateInstance($planType, $true)
    $values = @{ GpuRaw='texture.raw'; Dynamic=$dynamic; HdrTransfer='smpte2084'; GpuWidth=$width; GpuHeight=$height; GpuContentX=$x; GpuContentY=$y; GpuContentWidth=$cw; GpuContentHeight=$ch; GpuInputIndex=$idx; GpuClockGated=($idx -eq 1); GpuUseLegacy4k=$legacy }
    foreach ($entry in $values.GetEnumerator()) { $planType.GetField($entry.Key, $fields).SetValue($plan, $entry.Value) }
    return $method.Invoke($null, @('[0:v:0]hwupload[fgsfilm_gpu_video]', $plan))
}
function Assert-Contains([string]$actual,[string]$expected) {
    if (-not $actual.Contains($expected)) { throw "Expected graph text not found: $expected`nGraph: $actual" }
}
$g = Make-Graph 1920 1080 0 138 1920 804 1 $false
foreach ($s in @('[1:v:0]format=rgba', 'libplacebo=inputs=2:w=1920:h=1080:format=yuv420p10le', 'hwupload=derive_device=cuda,scale_cuda=format=p010le', 'w=1920:h=1080', "crop_y='if(eq(idx,0),138,0)'", "crop_h='if(eq(idx,0),804,ih)'", "pos_w='if(eq(idx,0),1680,1920)'", "pos_h='if(eq(idx,0),737,1080)'", "pos_y='if(eq(idx,0),171.5,0)", 'hwupload=derive_device=cuda', '[vout]')) { Assert-Contains $g $s }
if ($g.Contains('[2:v:0]')) { throw 'No-grain route references a non-existent grain input.' }
if ($g.Contains('libplacebo=inputs=2:w=1920:h=1080:format=p010le') -or $g.Contains('hwdownload')) {
    throw 'Nonlegacy GPU route must not map semi-planar P010 or round-trip through host memory.'
}
Assert-Contains $g '[fgsfilm_clock_src]scale=2:2,format=rgba[fgsfilm_clock]'
Assert-Contains $g 'overlay=x=main_w:shortest=1:format=rgb,format=rgba'
$g = Make-Graph 1920 1080 0 0 1920 1080 2 $false
foreach ($s in @('[2:v:0]format=rgba', "crop_y='if(eq(idx,0),0,0)'", "pos_h='if(eq(idx,0),990,1080)'")) { Assert-Contains $g $s }
if ($g.Contains('fgsfilm_clock')) { throw 'Verified Grain Plate route changed unexpectedly.' }
$g = Make-Graph 3840 2160 0 276 3840 1608 2 $true
foreach ($s in @('[2:v:0]format=rgba', 'w=3840:h=2160', 'libplacebo=inputs=2:w=3840:h=2160:format=p010le', "crop_y='if(eq(idx,0),276,0)'", "pos_h='if(eq(idx,0),1474,2160)'")) { Assert-Contains $g $s }
if ($g.Contains('scale_cuda=')) { throw 'Previously verified legacy 4K path must remain unchanged.' }
$g = Make-Graph 1280 720 0 92 1280 536 1 $false
foreach ($s in @('w=1280:h=720', "crop_y='if(eq(idx,0),92,0)'", "pos_w='if(eq(idx,0),1120,1280)'")) { Assert-Contains $g $s }
$static = Make-Graph 1920 1080 0 138 1920 804 1 $false $false
foreach ($s in @("pos_x='if(eq(idx,0),120,0)+(0)'", "pos_y='if(eq(idx,0),171.5,0)+(0)'")) { Assert-Contains $static $s }
if ($static.Contains('floor(t*')) { throw 'Static border unexpectedly moves.' }
$static4k = Make-Graph 3840 2160 0 276 3840 1608 2 $true $false
foreach ($s in @("pos_x='if(eq(idx,0),240,0)+(0)'", "pos_y='if(eq(idx,0),343,0)+(0)'")) { Assert-Contains $static4k $s }
if ($static4k.Contains('floor(t*')) { throw 'Legacy 4K static border unexpectedly moves.' }
$dynamic = Make-Graph 1920 1080 0 138 1920 804 1 $false $true
if (-not $dynamic.Contains('floor(t*13)')) { throw 'Dynamic border no longer moves.' }
# Exercise the actual encoder argument builder, including the finite end time.
$nativeType = $assembly.GetType('FilmGrainStudioPreview.NativeHevcCore', $true)
$buildArgs = $nativeType.GetMethod('BuildHevcArgs', $flags)
$capsType = $assembly.GetType('FilmGrainStudioPreview.HardwareCapabilitySnapshot', $true)
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Reflection;
public static class FgsS4ArgsAudit {
    public static string[] Build(MethodInfo method, Type capsType, Type planType, string graph) {
        object caps = Activator.CreateInstance(capsType, true);
        object plan = Activator.CreateInstance(planType, true);
        planType.GetField("GpuRaw", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SetValue(plan, "texture.raw");
        var state = new Dictionary<string,string>();
        state["FG_MODE"] = "HEVC";
        object[] call = { "source.mp4", "", graph, "out.mkv", "p5", false, 5000L, 10000L, 20000L,
            "24/1", "20", false, true, caps, state, null, false, null, plan };
        return ((List<string>)method.Invoke(null, call)).ToArray();
    }
}
'@
$list = [FgsS4ArgsAudit]::Build($buildArgs, $capsType, $planType, $g)
$sourceIndex = [array]::IndexOf($list, 'source.mp4')
$rawIndex = [array]::IndexOf($list, 'texture.raw')
$filterIndex = [array]::IndexOf($list, '-filter_complex')
$durationIndex = [array]::LastIndexOf($list, '-t')
if ($sourceIndex -lt 0 -or $rawIndex -le $sourceIndex -or $rawIndex -ge $filterIndex) { throw 'No-grain input order is wrong.' }
if ($durationIndex -lt $filterIndex -or $list[$durationIndex+1] -ne '20') { throw 'No-grain output has no finite duration.' }
if ([array]::IndexOf($list, '-pix_fmt') -ge 0) { throw 'GPU frames unexpectedly force a software pixel format.' }
# v1.1 flexible user aperture must map identically in CPU and Vulkan compositor.
$customPlan=[Activator]::CreateInstance($planType,$true)
$planType.GetField('GpuRaw',$fields).SetValue($customPlan,'texture.raw')
$planType.GetField('HdrTransfer',$fields).SetValue($customPlan,'smpte2084')
$planType.GetField('GpuWidth',$fields).SetValue($customPlan,1920)
$planType.GetField('GpuHeight',$fields).SetValue($customPlan,1080)
$planType.GetField('GpuContentWidth',$fields).SetValue($customPlan,1920)
$planType.GetField('GpuContentHeight',$fields).SetValue($customPlan,1080)
$planType.GetField('GpuInputIndex',$fields).SetValue($customPlan,1)
$planType.GetField('ApertureWidth1080',$fields).SetValue($customPlan,1730)
$planType.GetField('ApertureHeight1080',$fields).SetValue($customPlan,798)
$customGpu=[string]$method.Invoke($null,@('[0:v:0]hwupload[fgsfilm_gpu_video]',$customPlan))
Assert-Contains $customGpu "pos_w='if(eq(idx,0),1730,1920)'"
Assert-Contains $customGpu "pos_h='if(eq(idx,0),798,1080)'"
$planType.GetField('ApertureWidth1080',$fields).SetValue($customPlan,1630)
$planType.GetField('ApertureHeight1080',$fields).SetValue($customPlan,698)
$customGpu=[string]$method.Invoke($null,@('[0:v:0]hwupload[fgsfilm_gpu_video]',$customPlan))
Assert-Contains $customGpu "pos_w='if(eq(idx,0),1630,1920)'"
Assert-Contains $customGpu "pos_h='if(eq(idx,0),698,1080)'
# HLG import regression: custom 1650x720 gate hit the CUDA P010 interop error on RTX 4080.
$planType.GetField('ApertureWidth1080',$fields).SetValue($customPlan,1650)
$planType.GetField('ApertureHeight1080',$fields).SetValue($customPlan,720)
$planType.GetField('HdrTransfer',$fields).SetValue($customPlan,'arib-std-b67')
$hlgGpu=[string]$method.Invoke($null,@('[0:v:0]hwupload[fgsfilm_gpu_video]',$customPlan))
foreach($s in @('format=yuv420p10le', 'hwupload=derive_device=cuda,scale_cuda=format=p010le',
    "pos_w='if(eq(idx,0),1650,1920)'", "pos_h='if(eq(idx,0),720,1080)'", 'color_trc=arib-std-b67')) { Assert-Contains $hlgGpu $s }
if($hlgGpu.Contains('hwdownload') -or $hlgGpu.Contains('libplacebo=inputs=2:w=1920:h=1080:format=p010le')){
    throw 'HLG GPU border must use planar CUDA interop without CPU round-trip'
}
$planType.GetField('HdrTransfer',$fields).SetValue($customPlan,'')
$appendCpu=$core.GetMethod('AppendFilter',$flags)
$planType.GetField('Dynamic',$fields).SetValue($customPlan,$false)
$customCpu=[string]$appendCpu.Invoke($null,@('[0:v:0]format=yuv420p[vout]',$customPlan,1920,1080,1,'24/1',$null,'yuv420p'))
Assert-Contains $customCpu 'scale=1630:698'
Assert-Contains $customCpu 'pad=1920:1080:145:191'
Write-Host 'PASS flexible user film aperture: CPU encoder and Vulkan GPU geometry match'
Write-Host 'PASS: GPU border input index and 1080p/4K/720p geometry'
Write-Host 'PASS: static and dynamic GPU border movement'
Write-Host 'PASS: no-grain encoder input ordering and finite duration'
Write-Host 'PASS: no-grain texture ends with source clock; Grain Plate path unchanged'
