[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Exe)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) {throw ('EXE missing: '+$Exe)}
$asm = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Exe).Path)
$core = $asm.GetType('FilmGrainStudioPreview.FilmBorderEncodingCore', $true)
$planType = $asm.GetType('FilmGrainStudioPreview.FilmBorderEncodingCore+Plan', $true)
$method = $core.GetMethod('AppendGpuFilter', [Reflection.BindingFlags]'Static, NonPublic')
$fields = [Reflection.BindingFlags]'Instance, NonPublic, Public'
function Graph([string]$trc,[int]$w,[int]$h,[bool]$legacy,[bool]$dynamic) {
    $plan = [Activator]::CreateInstance($planType, $true)
    $values = @{
        GpuRaw='texture.raw'; GpuWidth=$w; GpuHeight=$h; GpuContentWidth=$w;
        GpuContentHeight=$h; GpuInputIndex=1; GpuClockGated=$true;
        HdrTransfer=$trc; GpuUseLegacy4k=$legacy; Dynamic=$dynamic
    }
    foreach ($entry in $values.GetEnumerator()) {
        $planType.GetField($entry.Key, $fields).SetValue($plan,$entry.Value)
    }
    return [string]$method.Invoke($null,@('[0:v:0]hwupload[fgsfilm_gpu_video]',$plan))
}
function Assert([bool]$ok,[string]$msg) { if (-not $ok) {throw ('FAIL: ' + $msg)} }
foreach ($size in @(@(1920,1080,$false),@(3840,2160,$false),@(3840,2160,$true))) {
    $w=[int]$size[0]; $h=[int]$size[1];$legacy=[bool]$size[2]
    foreach ($dynamic in @($true,$false)) {
        $hlg = Graph 'arib-std-b67' $w $h $legacy $dynamic
        $pq  = Graph 'smpte2084' $w $h $legacy $dynamic
        $prefix = ('libplacebo=inputs=2:w=' + $w + ':h=' + $h + ':format=p010le:colorspace=bt2020nc:color_primaries=bt2020:color_trc=smpte2084:range=limited')
        $second = ('libplacebo=inputs=1:w=' + $w + ':h=' + $h + ':format=p010le:colorspace=bt2020nc:color_primaries=bt2020:color_trc=arib-std-b67:range=limited')
        Assert ($hlg.Contains($prefix)) 'HLG first GPU stage must output PQ'
        Assert ($hlg.Contains($second)) 'HLG second GPU stage must convert PQ to HLG'
        Assert ($hlg.IndexOf($second) -lt $hlg.IndexOf('hwupload=derive_device=cuda')) 'GPU HLG stage must precede CUDA transfer'
        Assert (-not $hlg.Contains('hwdownload') -and -not $hlg.Contains('zscale=')) 'HLG must not download frames or use zscale'
        Assert (-not $pq.Contains('libplacebo=inputs=1:')) 'PQ must remain one-stage GPU compositor'
        Assert (-not $pq.Contains('hwdownload')) 'PQ must remain GPU resident'
        Assert ($pq.Contains($prefix)) 'PQ compositor changed'
        Assert ($hlg.Contains('hwupload=derive_device=cuda')) 'CUDA direct path missing'
        if ($dynamic) {
            Assert ($hlg.Contains('floor(t*13)')) 'Dynamic movement missing'
        } else {
            Assert (-not $hlg.Contains('floor(t*')) 'Static border unexpectedly moves'
        }
        Write-Host ('PASS: ' + $w + 'x' + $h + ' legacy=' + $legacy + ' dynamic=' + $dynamic + ' (HLG two-stage / PQ one-stage)')
    }
}
Write-Host 'PASS: L4Q6 HLG GPU graphs and PQ regression assertions'
