$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot '..\..\FilmGrain_Studio.exe'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $exe).Path)
$core = $assembly.GetType('FilmGrainStudioPreview.FilmBorderEncodingCore', $true)
$planType = $assembly.GetType('FilmGrainStudioPreview.FilmBorderEncodingCore+Plan', $true)
$flags = [Reflection.BindingFlags]'Static, NonPublic'
$fields = [Reflection.BindingFlags]'Instance, NonPublic, Public'
$method = $core.GetMethod('AppendGpuFilter', $flags)
function Make-Graph([int]$width, [int]$height, [int]$x, [int]$y, [int]$cw, [int]$ch, [int]$idx, [bool]$legacy) {
    $plan = [Activator]::CreateInstance($planType, $true)
    $values = @{ GpuRaw='texture.raw'; Dynamic=$true; HdrTransfer='smpte2084'; GpuWidth=$width; GpuHeight=$height; GpuContentX=$x; GpuContentY=$y; GpuContentWidth=$cw; GpuContentHeight=$ch; GpuInputIndex=$idx; GpuUseLegacy4k=$legacy }
    foreach ($entry in $values.GetEnumerator()) { $planType.GetField($entry.Key, $fields).SetValue($plan, $entry.Value) }
    return $method.Invoke($null, @('[0:v:0]hwupload[fgsfilm_gpu_video]', $plan))
}
function Assert-Contains([string]$actual,[string]$expected) {
    if (-not $actual.Contains($expected)) { throw "Expected graph text not found: $expected`nGraph: $actual" }
}
$g = Make-Graph 1920 1080 0 138 1920 804 1 $false
foreach ($s in @('[1:v:0]format=rgba', 'w=1920:h=1080', "crop_y='if(eq(idx,0),138,0)'", "crop_h='if(eq(idx,0),804,ih)'", "pos_w='if(eq(idx,0),1680,1920)'", "pos_h='if(eq(idx,0),737,1080)'", "pos_y='if(eq(idx,0),171.5,0)", 'hwupload=derive_device=cuda', '[vout]')) { Assert-Contains $g $s }
if ($g.Contains('[2:v:0]')) { throw 'No-grain route references a non-existent grain input.' }
$g = Make-Graph 1920 1080 0 0 1920 1080 2 $false
foreach ($s in @('[2:v:0]format=rgba', "crop_y='if(eq(idx,0),0,0)'", "pos_h='if(eq(idx,0),990,1080)'")) { Assert-Contains $g $s }
$g = Make-Graph 3840 2160 0 276 3840 1608 2 $true
foreach ($s in @('[2:v:0]format=rgba', 'w=3840:h=2160', "crop_y='if(eq(idx,0),276,0)'", "pos_h='if(eq(idx,0),1474,2160)'")) { Assert-Contains $g $s }
$g = Make-Graph 1280 720 0 92 1280 536 1 $false
foreach ($s in @('w=1280:h=720', "crop_y='if(eq(idx,0),92,0)'", "pos_w='if(eq(idx,0),1120,1280)'")) { Assert-Contains $g $s }
# Exercise the actual encoder argument builder, including the finite end time.
$nativeType = $assembly.GetType('FilmGrainStudioPreview.NativeHevcCore', $true)
$buildArgs = $nativeType.GetMethod('BuildHevcArgs', $flags)
$capsType = $assembly.GetType('FilmGrainStudioPreview.HardwareCapabilitySnapshot', $true)
$caps = [Activator]::CreateInstance($capsType, $true)
$plan = [Activator]::CreateInstance($planType, $true)
$planType.GetField('GpuRaw', $fields).SetValue($plan, 'texture.raw')
$state = New-Object 'System.Collections.Generic.Dictionary[string,string]'
$state['FG_MODE'] = 'HEVC'
$call = New-Object 'object[]' 19
$call[0]='source.mp4'; $call[1]=''; $call[2]=$g; $call[3]='out.mkv'; $call[4]='p5'
$call[5]=$false; $call[6]=[long]5000; $call[7]=[long]10000; $call[8]=[long]20000
$call[9]='24/1'; $call[10]='20'; $call[11]=$false; $call[12]=$true; $call[13]=$caps
$call[14]=$state; $call[15]=$null; $call[16]=$false; $call[17]=$null; $call[18]=$plan
$list = [string[]]($buildArgs.Invoke($null, $call)).ToArray()
$sourceIndex = [array]::IndexOf($list, 'source.mp4')
$rawIndex = [array]::IndexOf($list, 'texture.raw')
$filterIndex = [array]::IndexOf($list, '-filter_complex')
$durationIndex = [array]::LastIndexOf($list, '-t')
if ($sourceIndex -lt 0 -or $rawIndex -le $sourceIndex -or $rawIndex -ge $filterIndex) { throw 'No-grain input order is wrong.' }
if ($durationIndex -lt $filterIndex -or $list[$durationIndex+1] -ne '20') { throw 'No-grain output has no finite duration.' }
if ([array]::IndexOf($list, '-pix_fmt') -ge 0) { throw 'GPU frames unexpectedly force a software pixel format.' }
Write-Host 'PASS: GPU border input index and 1080p/4K/720p geometry'
Write-Host 'PASS: no-grain encoder input ordering and finite duration'
