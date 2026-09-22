function Show-FilmGrainColorCorrectionDialog {
    param(
        [Parameter(Mandatory=$true)][System.Windows.Forms.IWin32Window]$Owner,
        [Parameter(Mandatory=$true)][string]$VideoPath,
        [Parameter(Mandatory=$true)][string]$FfmpegPath,
        [Parameter(Mandatory=$true)][string]$FfprobePath,
        [Parameter(Mandatory=$true)][string]$LutSelectorPath,
        [Parameter(Mandatory=$true)][string]$LutRoot,
        [Parameter(Mandatory=$true)][string]$LutPreviewRoot,
        [string]$LutPath = '',
        [string]$LutSource = 'None',
        [int]$LutStrength = 75,
        [bool]$Enabled = $false,
        [double]$Contrast = 1.0,
        [double]$Brightness = 0.0,
        [double]$Saturation = 1.0,
        [double]$Gamma = 1.0,
        [bool]$BlackWhite = $false,
        [bool]$UseLut = $true,
        [bool]$LargeUi = $false,
        [string]$Language = 'zh-CN'
    )

    $isZh = ($Language -ne 'en-US')
    $ci = [Globalization.CultureInfo]::InvariantCulture
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('FGS_ColorPreview_' + [guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Force -Path $tempRoot)
    $previewPath = Join-Path $tempRoot 'preview.jpg'
    $compatLutPath = Join-Path $tempRoot 'preview_lut.cube'
    $lutState = [pscustomobject]@{ Path = $LutPath; Source = $LutSource; CompatPath = ''; Strength = $LutStrength }

    function Q([string]$Text) { return '"' + $Text.Replace('"','\"') + '"' }
    function F([double]$Value) { return $Value.ToString('0.00', $ci) }
    function Load-UnlockedImage([string]$Path) {
        $bytes = [IO.File]::ReadAllBytes($Path)
        $ms = New-Object IO.MemoryStream(,$bytes)
        try {
            $source = [Drawing.Image]::FromStream($ms)
            try { return New-Object Drawing.Bitmap $source }
            finally { $source.Dispose() }
        } finally { $ms.Dispose() }
    }

    $duration = 0.0
    try {
        $probePsi = New-Object Diagnostics.ProcessStartInfo
        $probePsi.FileName = $FfprobePath
        $probePsi.Arguments = '-v error -show_entries format=duration -of default=nw=1:nk=1 ' + (Q $VideoPath)
        $probePsi.UseShellExecute = $false
        $probePsi.CreateNoWindow = $true
        $probePsi.RedirectStandardOutput = $true
        $p = [Diagnostics.Process]::Start($probePsi)
        $text = $p.StandardOutput.ReadToEnd().Trim()
        $p.WaitForExit()
        $p.Dispose()
        [void][double]::TryParse($text, [Globalization.NumberStyles]::Float, $ci, [ref]$duration)
    } catch {}
    if ($duration -le 0) { $duration = 1.0 }
    $fps = 24.0
    try {
        $fpsPsi = New-Object Diagnostics.ProcessStartInfo
        $fpsPsi.FileName = $FfprobePath
        $fpsPsi.Arguments = '-v error -select_streams v:0 -show_entries stream=avg_frame_rate -of default=nw=1:nk=1 ' + (Q $VideoPath)
        $fpsPsi.UseShellExecute = $false; $fpsPsi.CreateNoWindow = $true; $fpsPsi.RedirectStandardOutput = $true
        $fp = [Diagnostics.Process]::Start($fpsPsi); $fpsText = $fp.StandardOutput.ReadToEnd().Trim(); $fp.WaitForExit(); $fp.Dispose()
        $parts=$fpsText.Split('/')
        if($parts.Count -eq 2 -and [double]$parts[1] -ne 0){$fps=[double]$parts[0]/[double]$parts[1]}elseif($fpsText){$fps=[double]$fpsText}
    } catch { $fps=24.0 }
    if($fps -le 0){$fps=24.0}

    $prepareLut = {
        param([string]$path)
        $lutState.Path = $path
        $lutState.CompatPath = ''
        Remove-Item -LiteralPath $compatLutPath -Force -ErrorAction SilentlyContinue
        if (-not $path -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { return }
        try {
            $out = foreach ($line in [IO.File]::ReadAllLines($path)) {
                if ($line -match '^\s*LUT_3D_INPUT_RANGE\s+([^\s]+)\s+([^\s]+)\s*$') {
                    'DOMAIN_MIN {0} {0} {0}' -f $matches[1]
                    'DOMAIN_MAX {0} {0} {0}' -f $matches[2]
                } else { $line }
            }
            [IO.File]::WriteAllLines($compatLutPath, [string[]]$out, [Text.Encoding]::ASCII)
            $lutState.CompatPath = $compatLutPath
        } catch { $lutState.CompatPath = '' }
    }.GetNewClosure()
    & $prepareLut $LutPath

    $dlg = New-Object Windows.Forms.Form
    $dlg.Text = if ($isZh) { '色彩纠正与 LUT 实时预览' } else { 'Color Correction and LUT Preview' }
    $dlg.StartPosition = 'CenterParent'
    $dlg.FormBorderStyle = 'FixedDialog'
    $dlg.MaximizeBox = $false
    $dlg.MinimizeBox = $false
    $previewWidth = if ($LargeUi) { 960 } else { 720 }
    $previewHeight = if ($LargeUi) { 540 } else { 480 }
    $dialogWidth = if ($LargeUi) { 1280 } else { 1040 }
    $dialogHeight = if ($LargeUi) { 700 } else { 690 }
    $timelineY = 14 + $previewHeight + 11
    $timeLabelY = $timelineY + 40
    $lutRowY = $timeLabelY + 29
    $buttonY = $dialogHeight - 60
    $rightX = 28 + $previewWidth
    $dlg.ClientSize = New-Object Drawing.Size -ArgumentList $dialogWidth,$dialogHeight
    $dlg.BackColor = [Drawing.Color]::FromArgb(245,246,248)

    $pic = New-Object Windows.Forms.PictureBox
    $pic.Location = New-Object Drawing.Point -ArgumentList 14,14
    $pic.Size = New-Object Drawing.Size -ArgumentList $previewWidth,$previewHeight
    $pic.BackColor = [Drawing.Color]::Black
    $pic.BorderStyle = 'FixedSingle'
    $pic.SizeMode = 'Zoom'
    [void]$dlg.Controls.Add($pic)

    $timelineState = [pscustomobject]@{ Value = 1000; Dragging = $false; LastClickAt = 0L; LastClickX = -1000 }
    $timelinePanel = New-Object Windows.Forms.Panel
    $timelinePanel.Location = New-Object Drawing.Point -ArgumentList 14,$timelineY
    $timelinePanel.Size = New-Object Drawing.Size -ArgumentList $previewWidth,34
    $timelinePanel.BackColor = [Drawing.Color]::FromArgb(245,246,248)
    $timelinePanel.Cursor = [Windows.Forms.Cursors]::Hand
    [void]$dlg.Controls.Add($timelinePanel)
    $timeLabel = New-Object Windows.Forms.Label
    $timeLabel.Location = New-Object Drawing.Point -ArgumentList 14,$timeLabelY
    $timeLabel.Size = New-Object Drawing.Size -ArgumentList $previewWidth,24
    $timeLabel.TextAlign = 'MiddleCenter'
    [void]$dlg.Controls.Add($timeLabel)

    $group = New-Object Windows.Forms.GroupBox
    $group.Text = if ($isZh) { '调整参数' } else { 'Adjustments' }
    $group.Location = New-Object Drawing.Point -ArgumentList $rightX,14
    $group.Size = New-Object Drawing.Size -ArgumentList 278,480
    [void]$dlg.Controls.Add($group)

    $checkEnable = New-Object Windows.Forms.CheckBox
    $checkEnable.Text = if ($isZh) { '启用色彩纠正' } else { 'Enable color correction' }
    $checkEnable.Location = New-Object Drawing.Point -ArgumentList 18,30
    $checkEnable.Size = New-Object Drawing.Size -ArgumentList 230,24
    $checkEnable.Checked = $Enabled
    [void]$group.Controls.Add($checkEnable)

    $controls = @{}
    $defs = @(
        @('Contrast',    $(if ($isZh) {'对比度'} else {'Contrast'}),    -200,200,[int][Math]::Round($Contrast*100),1.0),
        @('Brightness',  $(if ($isZh) {'亮度'} else {'Brightness'}),    -100,100,[int][Math]::Round($Brightness*100),0.0),
        @('Saturation',  $(if ($isZh) {'饱和度'} else {'Saturation'}),      0,300,[int][Math]::Round($Saturation*100),1.0),
        @('Gamma',       'Gamma',                                         1,300,[int][Math]::Round($Gamma*100),1.0)
    )
    $y = 72
    foreach ($d in $defs) {
        $label = New-Object Windows.Forms.Label
        $label.Text = [string]$d[1]
        $label.Location = New-Object Drawing.Point -ArgumentList 14,$y
        $label.Size = New-Object Drawing.Size -ArgumentList 80,22
        $label.TextAlign = 'MiddleLeft'
        [void]$group.Controls.Add($label)
        $track = New-Object Windows.Forms.TrackBar
        $track.Minimum=[int]$d[2]; $track.Maximum=[int]$d[3]; $track.Value=[Math]::Max([int]$d[2],[Math]::Min([int]$d[3],[int]$d[4]))
        $track.TickStyle='None'; $track.SmallChange=1; $track.LargeChange=10
        $track.Location = New-Object Drawing.Point -ArgumentList 92,($y-5)
        $track.Size = New-Object Drawing.Size -ArgumentList 120,35
        [void]$group.Controls.Add($track)
        $num = New-Object Windows.Forms.NumericUpDown
        $num.DecimalPlaces=2; $num.Increment=[decimal]0.01
        $num.Minimum=[decimal]([double]$d[2]/100.0); $num.Maximum=[decimal]([double]$d[3]/100.0)
        $num.Value=[decimal]([double]$track.Value/100.0)
        $num.Location = New-Object Drawing.Point -ArgumentList 212,$y
        $num.Size = New-Object Drawing.Size -ArgumentList 58,24
        [void]$group.Controls.Add($num)
        $controls[[string]$d[0]] = [pscustomobject]@{ Track=$track; Number=$num; Default=[double]$d[5] }
        $y += 70
    }

    $checkLut = New-Object Windows.Forms.CheckBox
    $checkLut.Text = if ($isZh) { '预览当前 LUT' } else { 'Preview current LUT' }
    $checkBlackWhite = New-Object Windows.Forms.CheckBox
    $checkBlackWhite.Text = if ($isZh) { '黑白模式' } else { 'Black and white mode' }
    $checkBlackWhite.Location = New-Object Drawing.Point -ArgumentList 18,344
    $checkBlackWhite.Size = New-Object Drawing.Size -ArgumentList 230,24
    $checkBlackWhite.Checked = $BlackWhite
    [void]$group.Controls.Add($checkBlackWhite)

    $openLutGallery = New-Object Windows.Forms.Button
    $openLutGallery.Text = if ($isZh) { '打开 LUT 图库…' } else { 'Open LUT Gallery…' }
    $openLutGallery.Location = New-Object Drawing.Point -ArgumentList 14,$lutRowY
    $openLutGallery.Size = New-Object Drawing.Size -ArgumentList 120,26
    [void]$dlg.Controls.Add($openLutGallery)

    $checkLut.Location = New-Object Drawing.Point -ArgumentList 142,$lutRowY
    $checkLut.Size = New-Object Drawing.Size -ArgumentList 140,24
    $checkLut.Checked = ($UseLut -and [bool]$lutState.CompatPath)
    $checkLut.Enabled = [bool]$lutState.CompatPath
    [void]$dlg.Controls.Add($checkLut)
    $lutLabel = New-Object Windows.Forms.Label
    $lutLabel.Location = New-Object Drawing.Point -ArgumentList 286,$lutRowY
    $lutLabel.Size = New-Object Drawing.Size -ArgumentList ($previewWidth-272),24
    $lutLabel.AutoEllipsis = $true
    $lutLabel.Text = if ($lutState.CompatPath) { [IO.Path]::GetFileName($lutState.Path) } else { if ($isZh) {'未选择 LUT'} else {'No LUT selected'} }
    $lutLabel.TextAlign = 'MiddleLeft'
    [void]$dlg.Controls.Add($lutLabel)

    $strengthY = $lutRowY + 25
    $lutStrengthTitle = New-Object Windows.Forms.Label
    $lutStrengthTitle.Text = if ($isZh) { 'LUT 强度' } else { 'LUT Strength' }
    $lutStrengthTitle.Location = New-Object Drawing.Point -ArgumentList 14,$strengthY
    $lutStrengthTitle.Size = New-Object Drawing.Size -ArgumentList 82,34
    $lutStrengthTitle.TextAlign = 'MiddleLeft'
    [void]$dlg.Controls.Add($lutStrengthTitle)
    $lutStrengthTrack = New-Object Windows.Forms.TrackBar
    $lutStrengthTrack.Minimum=0; $lutStrengthTrack.Maximum=3
    $lutStrengthTrack.Value = switch ($LutStrength) { 25 {0} 50 {1} 100 {3} default {2} }
    $lutStrengthTrack.TickStyle='BottomRight'
    $lutStrengthTrack.Location = New-Object Drawing.Point -ArgumentList 96,$strengthY
    $lutStrengthTrack.Size = New-Object Drawing.Size -ArgumentList ($previewWidth-164),40
    [void]$dlg.Controls.Add($lutStrengthTrack)
    $lutStrengthLabel = New-Object Windows.Forms.Label
    $lutStrengthLabel.Location = New-Object Drawing.Point -ArgumentList ($previewWidth-54),$strengthY
    $lutStrengthLabel.Size = New-Object Drawing.Size -ArgumentList 68,34
    $lutStrengthLabel.TextAlign = 'MiddleCenter'
    [void]$dlg.Controls.Add($lutStrengthLabel)
    $reset = New-Object Windows.Forms.Button
    $reset.Text = if ($isZh) { '恢复默认' } else { 'Reset' }
    $reset.Location = New-Object Drawing.Point -ArgumentList 18,438
    $reset.Size = New-Object Drawing.Size -ArgumentList 100,28
    [void]$group.Controls.Add($reset)
    $original = New-Object Windows.Forms.CheckBox
    $original.Appearance = 'Button'
    $original.Text = if ($isZh) { '按住查看原图' } else { 'Hold for original' }
    $original.TextAlign = 'MiddleCenter'
    $original.Location = New-Object Drawing.Point -ArgumentList 130,438
    $original.Size = New-Object Drawing.Size -ArgumentList 130,28
    [void]$group.Controls.Add($original)

    $status = New-Object Windows.Forms.Label
    $status.Location = New-Object Drawing.Point -ArgumentList $rightX,510
    $status.Size = New-Object Drawing.Size -ArgumentList 278,110
    $status.TextAlign = 'TopLeft'
    [void]$dlg.Controls.Add($status)
    $ok = New-Object Windows.Forms.Button
    $ok.Text = if ($isZh) { '确定' } else { 'OK' }; $ok.Location=New-Object Drawing.Point -ArgumentList ($dialogWidth-214),$buttonY; $ok.Size=New-Object Drawing.Size -ArgumentList 96,32
    $cancel = New-Object Windows.Forms.Button
    $cancel.Text = if ($isZh) { '取消' } else { 'Cancel' }; $cancel.Location=New-Object Drawing.Point -ArgumentList ($dialogWidth-110),$buttonY; $cancel.Size=New-Object Drawing.Size -ArgumentList 96,32
    [void]$dlg.Controls.Add($ok); [void]$dlg.Controls.Add($cancel)
    $dlg.AcceptButton=$ok; $dlg.CancelButton=$cancel

    $timer = New-Object Windows.Forms.Timer
    $timer.Interval = 140
    $syncing = $false
    $rendering = $false
    $pending = $true

    $currentTime = {
        return $duration * ([double]$timelineState.Value / 10000.0)
    }.GetNewClosure()
    $updateTimeLabel = {
        $pos=[TimeSpan]::FromSeconds((& $currentTime)); $total=[TimeSpan]::FromSeconds($duration)
        $timeLabel.Text=('{0:hh\:mm\:ss\.fff} / {1:hh\:mm\:ss\.fff}' -f $pos,$total)
    }.GetNewClosure()
    $requestRender = {
        $timer.Stop()
        $timer.Start()
    }.GetNewClosure()
    $lutStrengthValues = @(25,50,75,100)
    $updateLutUi = {
        $lutState.Strength = $lutStrengthValues[$lutStrengthTrack.Value]
        $lutStrengthLabel.Text = [string]$lutState.Strength + '%'
        $available = [bool]$lutState.CompatPath
        $checkLut.Enabled = $available
        $lutStrengthTrack.Enabled = ($available -and $checkLut.Checked)
        $lutStrengthTitle.Enabled = $lutStrengthTrack.Enabled
        $lutStrengthLabel.Enabled = $lutStrengthTrack.Enabled
        $lutLabel.Text = if ($available) { [IO.Path]::GetFileName($lutState.Path) } else { if ($isZh) {'未选择 LUT'} else {'No LUT selected'} }
    }.GetNewClosure()
    $setTimelineValue = {
        param([int]$value)
        $timelineState.Value = [Math]::Max(0,[Math]::Min(10000,$value))
        $timelinePanel.Invalidate()
        & $updateTimeLabel
        & $requestRender
    }.GetNewClosure()
    $renderPreview = {
        if ($rendering) { $pending=$true; return }
        $rendering=$true; $pending=$false
        try {
            $status.Text = if ($isZh) { '正在生成预览...' } else { 'Rendering preview...' }
            $dlg.Refresh()
            $filter = New-Object Collections.Generic.List[string]
            if (-not $original.Checked -and $checkEnable.Checked) {
                $contrast=([double]$controls.Contrast.Number.Value).ToString('0.00',$ci)
                $brightness=([double]$controls.Brightness.Number.Value).ToString('0.00',$ci)
                $saturation=([double]$controls.Saturation.Number.Value).ToString('0.00',$ci)
                $gamma=([double]$controls.Gamma.Number.Value).ToString('0.00',$ci)
                [void]$filter.Add("eq=contrast=$contrast`:brightness=$brightness`:saturation=$saturation`:gamma=$gamma")
                if($checkBlackWhite.Checked){[void]$filter.Add('hue=s=0')}
            }
            if (-not $original.Checked -and $checkLut.Checked -and $lutState.CompatPath) {
                $escaped=$lutState.CompatPath.Replace('\','/').Replace(':','\:').Replace("'","'\\\''")
                if ($lutState.Strength -ge 100) { [void]$filter.Add("format=gbrp16le,lut3d=file='$escaped':interp=tetrahedral") }
                else {
                    $opacity=([double]$lutState.Strength/100.0).ToString('0.00',$ci)
                    [void]$filter.Add("format=gbrp16le,split=2[lutorig][lutsrc];[lutsrc]lut3d=file='$escaped':interp=tetrahedral[lutgraded];[lutgraded][lutorig]blend=all_mode=normal:all_opacity=$opacity")
                }
            }
            [void]$filter.Add("scale=$previewWidth`:$previewHeight`:force_original_aspect_ratio=decrease,pad=$previewWidth`:$previewHeight`:(ow-iw)/2:(oh-ih)/2:black")
            $position=([double](& $currentTime)).ToString('0.00',$ci)
            $quotedVideo='"' + $VideoPath.Replace('"','\"') + '"'
            $quotedFilter='"' + ($filter -join ',').Replace('"','\"') + '"'
            $quotedPreview='"' + $previewPath.Replace('"','\"') + '"'
            $args='-hide_banner -loglevel error -y -ss ' + $position + ' -i ' + $quotedVideo + ' -frames:v 1 -vf ' + $quotedFilter + ' -q:v 2 ' + $quotedPreview
            $psi=New-Object Diagnostics.ProcessStartInfo
            $psi.FileName=$FfmpegPath; $psi.Arguments=$args; $psi.UseShellExecute=$false; $psi.CreateNoWindow=$true; $psi.RedirectStandardError=$true
            $p=[Diagnostics.Process]::Start($psi); $err=$p.StandardError.ReadToEnd(); $p.WaitForExit(); $rc=$p.ExitCode; $p.Dispose()
            if ($rc -ne 0 -or -not (Test-Path -LiteralPath $previewPath)) { throw $err.Trim() }
            $bytes=[IO.File]::ReadAllBytes($previewPath)
            $ms=New-Object IO.MemoryStream(,$bytes)
            try {
                $source=[Drawing.Image]::FromStream($ms)
                try { $img=New-Object Drawing.Bitmap $source } finally { $source.Dispose() }
            } finally { $ms.Dispose() }
            $old=$pic.Image; $pic.Image=$img; if ($old) {$old.Dispose()}
            $status.Text = if ($isZh) { '预览已更新' } else { 'Preview updated' }
        } catch {
            $prefix = if ($isZh) { '预览失败：' } else { 'Preview failed: ' }
            $status.Text = $prefix + $_.Exception.Message
        } finally {
            $rendering=$false
            if ($pending) { $timer.Stop(); $timer.Start() }
        }
    }.GetNewClosure()
    $timer.Add_Tick({$timer.Stop(); & $renderPreview}.GetNewClosure())
    & $updateLutUi
    $timelinePanel.Add_Paint({
        param($sender,$e)
        $left=9; $right=[Math]::Max($left+1,$sender.ClientSize.Width-10); $y=[int]($sender.ClientSize.Height/2)
        $thumbX=$left+[int][Math]::Round(($right-$left)*([double]$timelineState.Value/10000.0))
        $rail=New-Object Drawing.Pen ([Drawing.Color]::FromArgb(175,180,188)),4
        $fill=New-Object Drawing.Pen ([Drawing.Color]::FromArgb(35,120,220)),4
        $brush=New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(35,120,220))
        try {
            $rail.StartCap='Round'; $rail.EndCap='Round'; $fill.StartCap='Round'; $fill.EndCap='Round'
            $e.Graphics.SmoothingMode='AntiAlias'
            $e.Graphics.DrawLine($rail,$left,$y,$right,$y)
            $e.Graphics.DrawLine($fill,$left,$y,$thumbX,$y)
            $e.Graphics.FillEllipse($brush,$thumbX-7,$y-7,14,14)
        } finally { $rail.Dispose(); $fill.Dispose(); $brush.Dispose() }
    }.GetNewClosure())
    & $updateTimeLabel
    $timelinePanel.Add_MouseDown({
        param($sender,$e)
        if($e.Button -ne [Windows.Forms.MouseButtons]::Left){return}
        $now=[DateTime]::UtcNow.Ticks
        $elapsed=if($timelineState.LastClickAt -eq 0L){[int]::MaxValue}else{[int](($now-$timelineState.LastClickAt)/[TimeSpan]::TicksPerMillisecond)}
        $isDouble=($elapsed -le [Windows.Forms.SystemInformation]::DoubleClickTime -and [Math]::Abs($e.X-$timelineState.LastClickX) -le [Windows.Forms.SystemInformation]::DoubleClickSize.Width)
        $timelineState.LastClickAt=$now; $timelineState.LastClickX=$e.X
        $left=9; $right=[Math]::Max($left+1,$sender.ClientSize.Width-10)
        $target=[Math]::Max(0,[Math]::Min(10000,[int][Math]::Round((($e.X-$left)/[double]($right-$left))*10000.0)))
        $thumbX=$left+[int][Math]::Round(($right-$left)*([double]$timelineState.Value/10000.0))
        $frameStep=[Math]::Max(1,[int][Math]::Round(10000.0/($duration*$fps)))
        if($isDouble){
            $timelineState.Dragging=$false
            $timelineState.LastClickAt=0L
            & $setTimelineValue $target
        } elseif([Math]::Abs($e.X-$thumbX) -le 10) {
            $timelineState.Dragging=$true
        } elseif($target -gt $timelineState.Value) {
            & $setTimelineValue ([int]$timelineState.Value+$frameStep)
        } elseif($target -lt $timelineState.Value) {
            & $setTimelineValue ([int]$timelineState.Value-$frameStep)
        }
    }.GetNewClosure())
    $timelinePanel.Add_MouseMove({
        param($sender,$e)
        if(-not $timelineState.Dragging -or $e.Button -ne [Windows.Forms.MouseButtons]::Left){return}
        $left=9; $right=[Math]::Max($left+1,$sender.ClientSize.Width-10)
        $target=[Math]::Max(0,[Math]::Min(10000,[int][Math]::Round((($e.X-$left)/[double]($right-$left))*10000.0)))
        & $setTimelineValue $target
    }.GetNewClosure())
    $timelinePanel.Add_MouseUp({$timelineState.Dragging=$false}.GetNewClosure())
    $timelinePanel.Add_MouseLeave({if([Windows.Forms.Control]::MouseButtons -eq [Windows.Forms.MouseButtons]::None){$timelineState.Dragging=$false}}.GetNewClosure())
    foreach ($entry in $controls.Values) {
        $t=$entry.Track; $n=$entry.Number
        $localTrack=$t; $localNum=$n
        $t.Add_ValueChanged({
            if(-not $syncing){$syncing=$true; $localNum.Value=[decimal]([double]$localTrack.Value/100.0); $syncing=$false}
            & $requestRender
        }.GetNewClosure())
        $n.Add_ValueChanged({
            if(-not $syncing){$syncing=$true; $localTrack.Value=[Math]::Max($localTrack.Minimum,[Math]::Min($localTrack.Maximum,[int][Math]::Round([double]$localNum.Value*100))); $syncing=$false}
            & $requestRender
        }.GetNewClosure())
        $defaultValue=[decimal]$entry.Default
        $doubleClickState=[pscustomobject]@{LastAt=0L; LastX=-1000; LastY=-1000}
        $t.Add_MouseDown({
            param($sender,$e)
            if($e.Button -ne [Windows.Forms.MouseButtons]::Left){return}
            $now=[DateTime]::UtcNow.Ticks
            $elapsed=if($doubleClickState.LastAt -eq 0L){[int]::MaxValue}else{[int](($now-$doubleClickState.LastAt)/[TimeSpan]::TicksPerMillisecond)}
            $near=([Math]::Abs($e.X-$doubleClickState.LastX) -le [Windows.Forms.SystemInformation]::DoubleClickSize.Width -and [Math]::Abs($e.Y-$doubleClickState.LastY) -le [Windows.Forms.SystemInformation]::DoubleClickSize.Height)
            if($elapsed -le [Windows.Forms.SystemInformation]::DoubleClickTime -and $near){
                $doubleClickState.LastAt=0L
                $resetNumber=$localNum; $resetValue=$defaultValue
                $resetAction={$resetNumber.Value=$resetValue}.GetNewClosure()
                [void]$dlg.BeginInvoke([Action]$resetAction)
            } else {
                $doubleClickState.LastAt=$now; $doubleClickState.LastX=$e.X; $doubleClickState.LastY=$e.Y
            }
        }.GetNewClosure())
    }
    $checkEnable.Add_CheckedChanged({& $requestRender}.GetNewClosure())
    $checkBlackWhite.Add_CheckedChanged({if($checkBlackWhite.Checked){$checkEnable.Checked=$true}; & $requestRender}.GetNewClosure())
    $checkLut.Add_CheckedChanged({& $updateLutUi; & $requestRender}.GetNewClosure())
    $lutStrengthTrack.Add_ValueChanged({& $updateLutUi; & $requestRender}.GetNewClosure())
    $openLutGallery.Add_Click({
        if(-not (Test-Path -LiteralPath $LutSelectorPath -PathType Leaf)){
            [void][Windows.Forms.MessageBox]::Show($dlg,$(if($isZh){"找不到 LUT 图库：`n$LutSelectorPath"}else{"LUT Gallery not found:`n$LutSelectorPath"}),$dlg.Text,'OK','Error')
            return
        }
        if(-not (Test-Path -LiteralPath $LutRoot -PathType Container)){
            [void][Windows.Forms.MessageBox]::Show($dlg,$(if($isZh){"找不到 LUT 根目录：`n$LutRoot"}else{"LUT root not found:`n$LutRoot"}),$dlg.Text,'OK','Error')
            return
        }
        $pick=Join-Path ([IO.Path]::GetTempPath()) ('FilmGrainStudio_Color_LUT_'+[guid]::NewGuid().ToString('N')+'.txt')
        $openLutGallery.Enabled=$false
        try {
            $psi=New-Object Diagnostics.ProcessStartInfo
            $psi.FileName='powershell.exe'; $psi.UseShellExecute=$false; $psi.CreateNoWindow=$true; $psi.RedirectStandardError=$true
            $psi.Arguments='-NoProfile -ExecutionPolicy Bypass -STA -File "'+$LutSelectorPath+'" -LutRoot "'+$LutRoot+'" -PreviewRoot "'+$LutPreviewRoot+'" -OutputFile "'+$pick+'"'
            $proc=[Diagnostics.Process]::Start($psi); $galleryError=$proc.StandardError.ReadToEnd(); $proc.WaitForExit(); $rc=$proc.ExitCode; $proc.Dispose()
            if($rc -eq 0 -and (Test-Path -LiteralPath $pick)){
                $selected=[IO.File]::ReadAllText($pick,[Text.Encoding]::UTF8).Trim()
                if($selected){& $prepareLut $selected; $lutState.Source='Gallery'; $checkLut.Checked=[bool]$lutState.CompatPath}
            } elseif($rc -eq 10) {
                & $prepareLut ''
                $lutState.Source='None'
                $checkLut.Checked=$false
            } elseif($rc -ne 11) {
                $detail=$galleryError.Trim(); if($detail.Length -gt 2000){$detail=$detail.Substring(0,2000)}
                [void][Windows.Forms.MessageBox]::Show($dlg,$(if($isZh){"LUT 图库返回错误 $rc。`n$detail"}else{"LUT Gallery returned error $rc.`n$detail"}),$dlg.Text,'OK','Error')
            }
            & $updateLutUi
            & $requestRender
        } catch {
            [void][Windows.Forms.MessageBox]::Show($dlg,$_.Exception.Message,$dlg.Text,'OK','Error')
        } finally {
            $openLutGallery.Enabled=$true
            Remove-Item -LiteralPath $pick -Force -ErrorAction SilentlyContinue
        }
    }.GetNewClosure())
    $original.Add_CheckedChanged({& $requestRender}.GetNewClosure())
    $reset.Add_Click({
        $checkEnable.Checked=$false
        $checkBlackWhite.Checked=$false
        foreach($key in $controls.Keys){$controls[$key].Number.Value=[decimal]$controls[$key].Default}
        & $requestRender
    }.GetNewClosure())
    $ok.Add_Click({$dlg.DialogResult='OK'; $dlg.Close()})
    $cancel.Add_Click({$dlg.DialogResult='Cancel'; $dlg.Close()})
    $dlg.Add_Shown({& $requestRender}.GetNewClosure())
    $result=$dlg.ShowDialog($Owner)
    $answer=$null
    if($result -eq [Windows.Forms.DialogResult]::OK){
        $answer=[pscustomobject]@{
            Enabled=[bool]$checkEnable.Checked
            Contrast=[double]$controls.Contrast.Number.Value
            Brightness=[double]$controls.Brightness.Number.Value
            Saturation=[double]$controls.Saturation.Number.Value
            Gamma=[double]$controls.Gamma.Number.Value
            BlackWhite=[bool]$checkBlackWhite.Checked
            UseLut=[bool]$checkLut.Checked
            LutPath=[string]$lutState.Path
            LutSource=[string]$lutState.Source
            LutStrength=[int]$lutState.Strength
        }
    }
    $timer.Stop(); $timer.Dispose()
    if($pic.Image){$pic.Image.Dispose()}
    $dlg.Dispose()
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    return $answer
}
