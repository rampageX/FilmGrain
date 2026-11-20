# Film Border import v1.1: real RGBA8 pixel validation, user window geometry,
# semi-transparent center, metadata persistence, legacy fallback and bad input rejection.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$assembly=[Reflection.Assembly]::LoadFrom((Resolve-Path 'FilmGrain_Studio.exe').Path)
$type=$assembly.GetType('FilmGrainStudioPreview.FilmBorderCatalogCore',$true)
$flags=[Reflection.BindingFlags]'Static,NonPublic'
$validate=$type.GetMethod('ValidatePng',$flags)
$validateNew=$type.GetMethod('ValidatePngWithAperture',$flags)
$import=$type.GetMethod('Import',$flags,$null,[type[]]@([string],[string],[string],[int],[int]),$null)
$checkName=$type.GetMethod('ValidateDisplayName',$flags)
$checkSize=$type.GetMethod('ValidateAperture',$flags)
$list=$type.GetMethod('List',$flags)
$choiceType=$assembly.GetType('FilmGrainStudioPreview.FilmBorderChoice',$true)
$fieldFlags=[Reflection.BindingFlags]'Instance,NonPublic'
$labelField=$choiceType.GetField('Label',$fieldFlags)
$pngField=$choiceType.GetField('PngPath',$fieldFlags)
$widthField=$choiceType.GetField('ApertureWidth1080',$fieldFlags)
$heightField=$choiceType.GetField('ApertureHeight1080',$fieldFlags)
if(-not $validate -or -not $validateNew -or -not $import -or -not $checkName -or -not $checkSize -or
   -not $list -or -not $labelField -or -not $pngField -or -not $widthField -or -not $heightField){
    throw 'Film Border import v1.1 API/metadata fields unavailable'
}
$dir=Join-Path $env:RUNNER_TEMP ('FGS_ImportV11_'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir -Force | Out-Null
function New-Png([string]$name,[string]$mode,[int]$apW=1680,[int]$apH=748){
    $w=1920;$h=1080
    if($mode -eq 'wrong-size'){$w=1280;$h=720}
    $pixel=if($mode -eq 'rgb'){[Drawing.Imaging.PixelFormat]::Format24bppRgb}
        else {[Drawing.Imaging.PixelFormat]::Format32bppArgb}
    $image=[Drawing.Bitmap]::new($w,$h,$pixel)
    try {
        $g=[Drawing.Graphics]::FromImage($image)
        try {
            $g.Clear([Drawing.Color]::Transparent)
            if($mode -ne 'blank'){
                $x=[int][Math]::Floor(($w-$apW)/2)
                $y=[int][Math]::Floor(($h-$apH)/2)
                $brush=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255,6,7,9))
                try {
                    $g.FillRectangle($brush,0,0,$w,$y)
                    $g.FillRectangle($brush,0,$y,$x,$apH)
                    $g.FillRectangle($brush,$x+$apW,$y,$w-($x+$apW),$apH)
                    $g.FillRectangle($brush,0,$y+$apH,$w,$h-$y-$apH)
                } finally {$brush.Dispose()}
                if($mode -eq 'tint' -or $mode -eq 'opaque-center' -or $mode -eq 'small-mark'){
                    $alpha=if($mode -eq 'opaque-center'){255}elseif($mode -eq 'small-mark'){1}else{96}
                    $overlay=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb($alpha,170,110,66))
                    try {
                        if($mode -eq 'small-mark'){$g.FillRectangle($overlay,$x+20,$y+30,4,4)}
                        else {$g.FillRectangle($overlay,$x,$y,$apW,$apH)}
                    } finally {$overlay.Dispose()}
                }
            }
        } finally {$g.Dispose()}
        $file=Join-Path $dir ($name+'.png')
        $image.Save($file,[Drawing.Imaging.ImageFormat]::Png)
        return $file
    } finally {$image.Dispose()}
}
function Assert-Rejected([scriptblock]$action,[string]$expected){
    $msg=$null
    try {& $action | Out-Null}
    catch {
        $err=$_.Exception
        while($err.InnerException){$err=$err.InnerException}
        $msg=$err.Message
    }
    if($null -eq $msg -or -not $msg.Contains($expected)){
        throw "Wrong/missing rejection. Expected '$expected', got '$msg'"
    }
    Write-Host "PASS rejected: $expected"
}
function Assert-Imported([string]$path,[string]$name,[int]$apW,[int]$apH){
    $mode=[string]$import.Invoke($null,@([string]$dir,[string]$path,[string]$name,[int]$apW,[int]$apH))
    if(-not $mode.StartsWith('USER_')){throw 'User import mode missing'}
    $id=$mode.Substring(5,32)
    $folder=Join-Path $dir 'Utils\_FilmBorders\User'
    $png=Join-Path $folder ($id+'.png')
    $raw=Join-Path $folder ($id+'.raw')
    $label=Join-Path $folder ($id+'.label')
    $aperture=Join-Path $folder ($id+'.aperture')
    if((Get-Item -LiteralPath $raw).Length -ne (1920*1080*4)){throw 'RGBA cache length incorrect'}
    if([IO.File]::ReadAllText($label,[Text.Encoding]::UTF8) -cne $name){throw 'User label not saved'}
    $record=[IO.File]::ReadAllText($aperture,[Text.Encoding]::UTF8).Trim()
    if($record -ne ($apW.ToString()+"`t"+$apH.ToString())){throw 'Aperture metadata incorrect: '+$record}
    $styles=$list.Invoke($null,@([string]$dir))
    $items=@($styles | Where-Object {
        [string]$pngField.GetValue($_) -eq $png
    })
    if($items.Count -ne 2){throw 'Imported static and dynamic catalog entries missing'}
    foreach($style in $items){
        if([int]$widthField.GetValue($style) -ne $apW -or [int]$heightField.GetValue($style) -ne $apH){
            throw 'Imported aperture geometry differs from saved value'
        }
    }
    $labels=@($items | ForEach-Object {[string]$labelField.GetValue($_)})
    if($labels -notcontains ('自定义 · '+$name+' · 静态') -or
       $labels -notcontains ('自定义 · '+$name+' · 动态')){
        throw 'User dropdown descriptions not retained'
    }
    return $png
}
try {
    $default=New-Png 'default' 'small-mark'
    $validate.Invoke($null,@([string]$default)) | Out-Null
    $original=Assert-Imported $default '柔和暗角 · 默认' 1680 748
    $originalAp=[IO.Path]::ChangeExtension($original,'.aperture')
    Remove-Item -LiteralPath $originalAp
    $styles=$list.Invoke($null,@([string]$dir))
    foreach($item in @($styles | Where-Object {[string]$pngField.GetValue($_) -eq $original})){
        if([int]$widthField.GetValue($item) -ne 1680 -or [int]$heightField.GetValue($item) -ne 748){
            throw 'Old v1.0 import without metadata changed geometry'
        }
    }
    Write-Host 'PASS: original imported assets keep default aperture if sidecar is absent'

    $min=New-Png 'minimum-window' 'tint' 1630 698
    $max=New-Png 'maximum-window' 'tint' 1730 798
    $validateNew.Invoke($null,@([string]$min,1630,698)) | Out-Null
    $validateNew.Invoke($null,@([string]$max,1730,798)) | Out-Null
    $importMin=Assert-Imported $min '旧片仓 · 半透明' 1630 698
    $importMax=Assert-Imported $max '青色颗粒覆盖' 1730 798
    $bmp=[Drawing.Bitmap]::new($importMax)
    try {
        if($bmp.GetPixel(960,540).A -ne 96){throw 'Center tint Alpha was rewritten during import'}
    } finally {$bmp.Dispose()}
    Write-Host 'PASS: min/max aperture, translucent full-center RGBA and saved user sizes'

    Assert-Rejected { $checkSize.Invoke($null,@(1629,748)) } '1630'
    Assert-Rejected { $checkSize.Invoke($null,@(1731,748)) } '1630'
    Assert-Rejected { $checkSize.Invoke($null,@(1680,697)) } '698'
    Assert-Rejected { $checkSize.Invoke($null,@(1680,799)) } '698'
    Assert-Rejected { $validateNew.Invoke($null,@([string](New-Png 'opaque' 'opaque-center'),1680,748)) } '95%'
    Assert-Rejected { $validate.Invoke($null,@([string](New-Png 'blank' 'blank'))) } '可见像素不足'
    Assert-Rejected { $validate.Invoke($null,@([string](New-Png 'wrong-size' 'wrong-size'))) } '1920'
    Assert-Rejected { $validate.Invoke($null,@([string](New-Png 'rgb' 'rgb'))) } 'RGBA'
    foreach($bad in @('', '   ', ('X'*41), "bad`tname", "bad`nname")){
        Assert-Rejected { $checkName.Invoke($null,@([string]$bad)) } '边框名称'
    }

    $fake=Join-Path $dir 'fake.png'
    [IO.File]::WriteAllText($fake,'not a PNG')
    Assert-Rejected { $validate.Invoke($null,@([string]$fake)) } 'PNG'
    $apng=Join-Path $dir 'animated.png'
    $bytes=[IO.File]::ReadAllBytes($default)
    $chunk=[byte[]](0,0,0,8,97,99,84,76,0,0,0,2,0,0,0,0,0,0,0,0)
    $out=[byte[]]::new($bytes.Length+$chunk.Length)
    [Array]::Copy($bytes,0,$out,0,33)
    [Array]::Copy($chunk,0,$out,33,$chunk.Length)
    [IO.File]::WriteAllBytes($apng,$out)
    Assert-Rejected { $validate.Invoke($null,@([string]$apng)) } 'APNG'
    $tooLarge=Join-Path $dir 'oversize.png'
    $stream=[IO.File]::Open($tooLarge,[IO.FileMode]::Create)
    try {$stream.SetLength(33MB)} finally {$stream.Dispose()}
    Assert-Rejected { $validate.Invoke($null,@([string]$tooLarge)) } '32 MiB'
    Write-Host 'PASS Film Border user import v1.1 - geometry, Alpha, description and rollback rules'
} finally {
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
