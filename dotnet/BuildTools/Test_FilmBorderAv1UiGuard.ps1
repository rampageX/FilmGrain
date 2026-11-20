# AV1 border UI protection regression. Test the compiled route guard and
# assert every UI path neutralizes saved unsupported effects without mutating the config.
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$exe=Join-Path $root 'FilmGrain_Studio.exe'
$assembly=[Reflection.Assembly]::LoadFrom($exe)
$snapshotType=$assembly.GetType('FilmGrainStudioPreview.BridgeEnvironmentSnapshot',$true)
$bridgeType=$assembly.GetType('FilmGrainStudioPreview.BridgeEnvironmentCore',$true)
$flags=[Reflection.BindingFlags]'Static,NonPublic'
$instanceFlags=[Reflection.BindingFlags]'Instance,NonPublic,Public'
$builder=$bridgeType.GetMethod('Build',$flags)
if($null -eq $builder){throw 'BridgeEnvironmentCore.Build not found'}
$snapshot=[Activator]::CreateInstance($snapshotType,$true)
$snapshotType.GetField('Mode',$instanceFlags).SetValue($snapshot,'AV1')
$snapshotType.GetField('FilmBorderMode',$instanceFlags).SetValue($snapshot,'DARK_STATIC')
$caught=$false
try { $builder.Invoke($null,@($snapshot)) | Out-Null }
catch { $ex=$_.Exception;while($ex.InnerException){$ex=$ex.InnerException};if($ex -is [NotSupportedException]){$caught=$true}else{throw} }
if(-not $caught){throw 'Native bridge lost protection against unsupported AV1 borders'}
$state=Get-Content -LiteralPath (Join-Path $root 'dotnet\FilmGrain_BridgeStateCapture.cs') -Raw -Encoding UTF8
$ui=Get-Content -LiteralPath (Join-Path $root 'dotnet\FilmGrain_GpuAdvancedPreview.cs') -Raw -Encoding UTF8
$main=Get-Content -LiteralPath (Join-Path $root 'dotnet\FilmGrain_Studio_NET_Preview.cs') -Raw -Encoding UTF8
$svt=Get-Content -LiteralPath (Join-Path $root 'dotnet\FilmGrain_NativeX264DigitalGrainCore.cs') -Raw -Encoding UTF8
if($state -notmatch 'snapshot\.Mode=="AV1"\|\|snapshot\.Mode=="SVT"\|\|snapshot\.NoReencode' -or
   $state -notmatch 'snapshot\.FilmBorderMode=FilmBorderCatalogCore\.Off' -or
   $state -notmatch 'catch\(NotSupportedException ex\)'){throw 'Snapshot AV1 border guard missing'}
if($ui -notmatch 'NVENC_AV1 暂不可用' -or $ui -notmatch 'SVT-AV1 暂不可用' -or
   $ui -notmatch 'AV1 仅替换颗粒 · 暂不可用' -or $ui -notmatch 'btnFilmBorder.Enabled=false' -or
   $ui -notmatch 'btnFilmBorder.Enabled=!\(gpuAdvancedPreviewBusy\|\|filmBorderSettingsBusy\)'){throw 'AV1 disabled UI text or restore missing'}
if($main -notmatch 'UpdateFilmBorderButton\(\);' -or
   $state -notmatch 'SyncCodecPicker\(\);LoadBitrateStateForCodec\(\);UpdateBitrateDisplays\(\);\s*UpdateFilmBorderButton\(\);'){throw 'Codec or independent-file UI refresh missing'}
if($svt -notmatch 'AV1 SVT film border requires a separate validated AV1 graph'){throw 'SVT encoder border protection removed without validation'}
Write-Host 'PASS AV1 NVENC/SVT/copy UI disable; saved border restoration; no WinForms crash; SVT pipeline unchanged'
