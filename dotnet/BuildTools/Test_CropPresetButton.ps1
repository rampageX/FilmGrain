$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $root 'FilmGrain_Studio.exe'))
$geom=$assembly.GetType('FilmGrainStudioPreview.CinematicCropGeometry',$true)
$flags=[Reflection.BindingFlags]'Static,Public,NonPublic'
$title=$geom.GetMethod('PresetShortName',$flags)
if(-not $title){throw 'Cinematic crop short-title helper is missing.'}
foreach($case in @(
    @{Index=0; Chinese=$true; Expected='自定义'}
    @{Index=1; Chinese=$true; Expected='原始画幅'}
    @{Index=2; Chinese=$true; Expected='宽银幕'}
    @{Index=3; Chinese=$true; Expected='IMAX 全尺寸'}
    @{Index=4; Chinese=$true; Expected='IMAX'}
    @{Index=5; Chinese=$true; Expected='现代高清'}
    @{Index=9; Chinese=$true; Expected='拍立得'}
    @{Index=4; Chinese=$false; Expected='IMAX'}
    @{Index=2; Chinese=$false; Expected='Widescreen'}
)){
    $args2=[object[]]::new(2)
    $args2[0]=[int]$case.Index
    $args2[1]=[bool]$case.Chinese
    $actual=[string]$title.Invoke($null,$args2)
    if($actual -cne [string]$case.Expected){
        throw "Wrong short preset title index=$($case.Index): $actual"
    }
}
$configType=$assembly.GetType('FilmGrainStudioPreview.FgsConfig',$true)
$ctor=$configType.GetConstructor([type[]]@([string],[string]))
if(-not $ctor){throw 'FgsConfig constructor missing.'}
$dir=Join-Path $env:RUNNER_TEMP ('FGS_CropPreset_'+[Guid]::NewGuid().ToString('N'))
$ini=Join-Path $dir 'Test.ini'
[IO.Directory]::CreateDirectory($dir) | Out-Null
try {
    $ctorArgs=[object[]]::new(2)
    $ctorArgs[0]=[string]::Copy([string]$dir)
    $ctorArgs[1]=[string]::Copy([string]$ini)
    $config=$ctor.Invoke($ctorArgs)
    if($config.Get('CINEMATIC_CROP_PRESET') -ne '0'){throw 'Legacy/clean crop must default to Custom.'}
    $config.SaveValue('CINEMATIC_CROP_PRESET','4')
    $config=$ctor.Invoke($ctorArgs)
    if($config.Get('CINEMATIC_CROP_PRESET') -ne '4'){throw 'Explicit preset selection was not persisted.'}
    $config.SaveValue('CINEMATIC_CROP_PRESET','0')
    $config=$ctor.Invoke($ctorArgs)
    if($config.Get('CINEMATIC_CROP_PRESET') -ne '0'){throw 'Custom edit did not clear saved preset.'}
} finally {
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
$source=[IO.File]::ReadAllText((Join-Path $root 'dotnet\FilmGrain_Preprocessing.cs'))
$main=[IO.File]::ReadAllText((Join-Path $root 'dotnet\FilmGrain_Studio_NET_Preview.cs'))
foreach($required in @(
    'selectedPresetIndex=presetIndex',
    'selectedPresetIndex=0',
    'values[CinematicCropGeometry.PresetKey] = selectedPresetIndex',
    'top.Layout += delegate',
    'visual.Height = Math.Max(mode.Height,preset.Height)',
    'CinematicCropGeometry.PresetShortName('
)){
    if(-not $source.Contains($required)){throw "Crop UI behavior missing: $required"}
}
if(-not $main.Contains('{CinematicCropGeometry.PresetKey,"0"}')){
    throw 'Direct visual crop must discard the preset identity when geometry changes.'
}
Write-Host 'PASS crop presets: explicit names, Custom semantics, persisted selection, matched row control sizing'
