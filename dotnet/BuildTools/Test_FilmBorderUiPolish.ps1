$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $root 'FilmGrain_Studio.exe'))
$type=$assembly.GetType('FilmGrainStudioPreview.FgsConfig',$true)
$testIni=Join-Path $env:RUNNER_TEMP ('FGS_Cinematic_'+[Guid]::NewGuid().ToString('N')+'.ini')
try {
    $settings=[Activator]::CreateInstance($type,[object[]]@($root,$testIni))
    if($settings.GetBool('CINEMATIC_STYLE_ENABLED')){throw 'Cinema style must default OFF on fresh setup.'}
    $settings.SaveValue('CINEMATIC_STYLE_ENABLED','true')
    $settings=[Activator]::CreateInstance($type,[object[]]@($root,$testIni))
    if(-not $settings.GetBool('CINEMATIC_STYLE_ENABLED')){throw 'Cinema style ON did not persist.'}
    $settings.SaveValue('CINEMATIC_STYLE_ENABLED','false')
    $settings=[Activator]::CreateInstance($type,[object[]]@($root,$testIni))
    if($settings.GetBool('CINEMATIC_STYLE_ENABLED')){throw 'Cinema style OFF did not persist.'}
} finally {
    if(Test-Path -LiteralPath $testIni){Remove-Item -LiteralPath $testIni -Force}
}
$main=[IO.File]::ReadAllText((Join-Path $root 'dotnet\FilmGrain_Studio_NET_Preview.cs'))
$gpu=[IO.File]::ReadAllText((Join-Path $root 'dotnet\FilmGrain_GpuAdvancedPreview.cs'))
$prep=[IO.File]::ReadAllText((Join-Path $root 'dotnet\FilmGrain_Preprocessing.cs'))
foreach($required in @(
    'config.GetBool("CINEMATIC_STYLE_ENABLED")',
    'config.SaveValue("CINEMATIC_STYLE_ENABLED",chkCinematic.Checked?"true":"false")',
    'btnFilmBorder.ContextMenuStrip=borderMenu',
    'config.SaveValue("FILM_BORDER_MODE",FilmBorderCatalogCore.Off)',
    'clearBorder.Enabled=!string.Equals(config.Get("FILM_BORDER_MODE"),FilmBorderCatalogCore.Off'
)){
    if(-not $main.Contains($required)){throw "Main UI integration missing: $required"}
}
if(-not $gpu.Contains('SetEffectButtonState(btnFilmBorder,active);')){throw 'Film border button does not reuse effect highlight.'}
if(-not $prep.Contains('SetEffectButtonState(btnPreprocess,tags.Length>0);')){throw 'Preprocess reference styling changed.'}
Write-Host 'PASS cinema default OFF and persisted ON/OFF; film border reuses preprocess highlight and right-click clear'
