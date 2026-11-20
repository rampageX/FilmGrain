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
# Reproduce a 2560x1072 source: 2.39:1 computes a full-frame crop,
# but a direct preset selection must remain selected instead of reverting to Full frame.
Add-Type -AssemblyName System.Windows.Forms
$presetCore=$assembly.GetType('FilmGrainStudioPreview.PresetCore',$true)
$validate=$presetCore.GetMethod('ValidateConfig',[Reflection.BindingFlags]'Static,NonPublic')
$includes=$presetCore.GetMethod('Includes',[Reflection.BindingFlags]'Static,NonPublic')
foreach($index in @(0,1,2,4,9)){
    $validationArgs=[object[]]::new(2)
    $validationArgs[0]=[string]'CINEMATIC_CROP_PRESET'
    $validationArgs[1]=[string]$index
    $validate.Invoke($null,$validationArgs) | Out-Null
}
$includedArgs=[object[]]::new(2)
$includedArgs[0]=[string]'Simple'
$includedArgs[1]=[string]'CINEMATIC_CROP_PRESET'
if(-not [bool]$includes.Invoke($null,$includedArgs)){
    throw 'Saved crop preset index cannot be included in a Simple preset.'
}
$mediaType=$assembly.GetType('FilmGrainStudioPreview.MediaProbeInfo',$true)
$cropType=$assembly.GetType('FilmGrainStudioPreview.PreprocessCropPage',$true)
$media=[Activator]::CreateInstance($mediaType,$true)
$fieldFlags=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$mediaType.GetField('Width',$fieldFlags).SetValue($media,[int]2560)
$mediaType.GetField('Height',$fieldFlags).SetValue($media,[int]1072)
$ctorTypeArgs=[type[]]@([System.Windows.Forms.TabPage],$configType,[int],$mediaType,
    [string],[Func[string,string,string]],[bool])
$cropCtor=$cropType.GetConstructor(
    [Reflection.BindingFlags]'Instance,NonPublic,Public',$null,$ctorTypeArgs,$null)
if(-not $cropCtor){throw 'Preprocess crop page constructor unavailable.'}
$tab=[System.Windows.Forms.TabPage]::new()
$caption=[Func[string,string,string]]{param([string]$zh,[string]$en) return $zh}
$cropArgs=[object[]]::new(7)
$cropArgs[0]=$tab
$cropArgs[1]=$config
$cropArgs[2]=[int]1
$cropArgs[3]=$media
$cropArgs[4]=[string]''
$cropArgs[5]=$caption
$cropArgs[6]=[bool]$true
try {
    $page=$cropCtor.Invoke($cropArgs)
    $combo=[System.Windows.Forms.ComboBox]$cropType.GetField('preset',$fieldFlags).GetValue($page)
    $onCommit=[System.Windows.Forms.ComboBox].GetMethod('OnSelectionChangeCommitted',
        [Reflection.BindingFlags]'Instance,NonPublic')
    if(-not $onCommit){throw 'ComboBox commit event unavailable.'}
    $eventArgs=[object[]]::new(1)
    $eventArgs[0]=[EventArgs]::Empty
    $combo.SelectedIndex=2
    $onCommit.Invoke($combo,$eventArgs) | Out-Null
    if($combo.SelectedIndex -ne 2){
        throw 'Selecting 2.39:1 on a near-2.39 input reverted to Full frame.'
    }
    $values=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $saveArgs=[object[]]::new(1)
    $saveArgs[0]=$values
    $cropType.GetMethod('SaveTo',$fieldFlags).Invoke($page,$saveArgs) | Out-Null
    if($values['CINEMATIC_CROP_PRESET'] -ne '2'){
        throw '2.39:1 direct-selection identity was not saved.'
    }
    $combo.SelectedIndex=4
    $onCommit.Invoke($combo,$eventArgs) | Out-Null
    if($combo.SelectedIndex -ne 4){
        throw 'IMAX 1.90:1 selected index did not remain stable.'
    }
    $setMargins=$cropType.GetMethod('SetMargins',$fieldFlags)
    $margs=[object[]]::new(5)
    $margs[0]=[int]0;$margs[1]=[int]36;$margs[2]=[int]0;$margs[3]=[int]36
    $margs[4]=[int]0
    $setMargins.Invoke($page,$margs) | Out-Null
    $values.Clear()
    $cropType.GetMethod('SaveTo',$fieldFlags).Invoke($page,$saveArgs) | Out-Null
    if($values['CINEMATIC_CROP_PRESET'] -ne '0'){
        throw 'Manual crop adjustment must mark the saved name as Custom.'
    }
} finally {
    $tab.Dispose()
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
Write-Host 'PASS crop presets: 2.39 and IMAX selection, manual Custom, config/preset validation, persistence and row sizing'
