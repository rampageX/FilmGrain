param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$InputFiles
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class FilmGrainNativeWindow
{
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);
}
'@
[System.Windows.Forms.Application]::EnableVisualStyles()

# Startup safety net: if an unexpected terminating error occurs before the
# GUI becomes usable, show it instead of silently disappearing behind WScript.
trap {
    try {
        $where = ''
        if ($_.InvocationInfo -and $_.InvocationInfo.PositionMessage) { $where = "`r`n`r`n" + $_.InvocationInfo.PositionMessage }
        [void][System.Windows.Forms.MessageBox]::Show(
            ('Film Grain Studio 发生未处理错误：' + "`r`n`r`n" + $_.Exception.Message + $where),
            'Film Grain Studio',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error
        )
    } catch {}
    exit 1
}

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$PackageRoot = Split-Path -Parent $ScriptRoot
$CoreBat = Join-Path $ScriptRoot 'FilmGrain_Universal_HEVC_AV1_StudioBridge.bat'
$NoReencodeBat = Join-Path $ScriptRoot 'AV1_Grav1synth_Add_Replace_FilmGrain_NoReencode.bat'
$GrainCacheBat = Join-Path $ScriptRoot 'FilmGrain_MOV_to_HEVC_Lossless_Cache.bat'
$LutPreviewGenerator = Join-Path $PackageRoot '_LUT_Tools\LUT_Preview_Batch_Gallery.ps1'
$LutPreviewDefaultReference = Join-Path $PackageRoot '_LUT_Tools\LUT_Reference_Default.jpg'
$LutPreviewCurrentReference = Join-Path $PackageRoot '_LUT_Tools\LUT_Reference_Current.jpg'
$ConfigScript = Join-Path $ScriptRoot 'FilmGrain_Config.ps1'
if (-not (Test-Path -LiteralPath $ConfigScript -PathType Leaf)) { throw "Film Grain configuration helper not found: $ConfigScript" }
. $ConfigScript
$script:PathConfig = Get-FilmGrainConfig
$Ffmpeg = [string]$script:PathConfig.FFMPEG
$Ffprobe = [string]$script:PathConfig.FFPROBE
$Grav1synth = [string]$script:PathConfig.GRAV1SYNTH
$DefaultGrainRoot = [string]$script:PathConfig.GRAIN_ROOT
$DefaultAv1GrainTableRoot = Join-Path $PackageRoot '_AV1_Grain_Tables'
$HardwareCapsScript = Join-Path $ScriptRoot 'FilmGrain_Hardware_Caps.ps1'
$HardwareCapsCache = Join-Path $ScriptRoot '_HardwareCaps.json'
$LutRoot = [string]$script:PathConfig.LUT_ROOT
$LutPreviewRoot = Join-Path $LutRoot '_LUT_PREVIEWS'
$LutSelector = Join-Path $PackageRoot '_LUT_Tools\LUT_Gallery_Selector.ps1'
$LutGalleryIndex = Join-Path $LutPreviewRoot '_LUT_GALLERY_INDEX.json'
$LutGalleryRecent = Join-Path $LutPreviewRoot '_LUT_GALLERY_RECENT.json'
$LutGalleryFavorites = Join-Path $LutPreviewRoot '_LUT_GALLERY_FAVORITES.json'
$LutGalleryThumbRoot = Join-Path $LutPreviewRoot '_GALLERY_THUMBS_v3_240x135'

$script:SelectedLutPath = $null
$script:SelectedLutSource = 'None'
$script:LastLutRecentRegisterError = ''
$script:RunningProcess = $null
$script:OutputReadTask = $null
$script:ErrorReadTask = $null
$script:OutputStreamClosed = $true
$script:ErrorStreamClosed = $true
$script:LogParseTail = ''
$script:CurrentInputPath = ''
$script:CurrentDurationSeconds = 0.0
$script:RunCompletionHandled = $false
$script:RunWasCancelled = $false
$script:LastCodecIndex = 0
$script:ChangingCodec = $false
$script:ModeBitrate = @{ 0 = '1500'; 1 = '7500' }
$script:HevcGrainFiles = @()
$script:LastScannedGrainRoot = ''
$script:Av1GrainTableFiles = @()
$script:LastScannedAv1GrainTableRoot = ''
$script:ProbeProcess = $null
$script:ProbeOutputTask = $null
$script:ProbeErrorTask = $null
$script:ProbeTargetPath = ''
$script:ProbeCache = @{}
$script:ProbeVideoMeta = @{}
$script:Av1GrainCache = @{}
$script:Av1InspectProcess = $null
$script:Av1InspectOutputTask = $null
$script:Av1InspectErrorTask = $null
$script:Av1InspectTargetPath = ''
$script:Av1InspectTempTable = ''
$script:NoReencodeItemText = 'AV1 不重编码 · 添加/替换胶片颗粒'
$script:NoReencodeUiActive = $false
$script:RecentLuts = @()
$script:LoadingRecentLuts = $false
$script:FavoriteLuts = @()
$script:LoadingFavoriteLuts = $false
$script:CurrentLutPreviewImage = $null
$script:HardwareCaps = $null
$script:HardwareCapsReady = $false
$script:FFmpegVersionOverride = ''
$script:Av1Available = $true
$script:Av1UhqAvailable = $false
$script:HevcAvailable = $true
$script:UploadSubtitle = [ordered]@{
    Enabled = $false
    Mode = 'OFF'
    EmbeddedIndex = 0
    ExternalPath = ''
    FontName = 'huiwen-mincho'
    FontSize = 69
    PrimaryHex = 'FFFFFF'
    BorderHex = '000000'
    Outline = 1.0
    Shadow = 1.0
    MarginV = 5
    Label = '字幕：关闭'
}
$ColorHeader = [System.Drawing.Color]::FromArgb(45, 57, 72)
$ColorAccent = [System.Drawing.Color]::FromArgb(47, 111, 173)
$ColorSubtle = [System.Drawing.Color]::FromArgb(242, 244, 247)
$ColorMuted = [System.Drawing.Color]::FromArgb(100, 107, 116)
$ColorSuccess = [System.Drawing.Color]::FromArgb(34, 139, 94)
$ColorError = [System.Drawing.Color]::FromArgb(190, 60, 55)

function New-UiFont {
    param([float]$Size = 9.0, [System.Drawing.FontStyle]$Style = [System.Drawing.FontStyle]::Regular)
    return New-Object System.Drawing.Font -ArgumentList 'Segoe UI', $Size, $Style
}

function Add-ColumnPercent {
    param([System.Windows.Forms.TableLayoutPanel]$Table, [float]$Percent)
    $style = New-Object System.Windows.Forms.ColumnStyle
    $style.SizeType = [System.Windows.Forms.SizeType]::Percent
    $style.Width = $Percent
    [void]$Table.ColumnStyles.Add($style)
}

function Add-RowAbsolute {
    param([System.Windows.Forms.TableLayoutPanel]$Table, [float]$Height)
    $style = New-Object System.Windows.Forms.RowStyle
    $style.SizeType = [System.Windows.Forms.SizeType]::Absolute
    $style.Height = $Height
    [void]$Table.RowStyles.Add($style)
}

function Add-RowPercent {
    param([System.Windows.Forms.TableLayoutPanel]$Table, [float]$Percent)
    $style = New-Object System.Windows.Forms.RowStyle
    $style.SizeType = [System.Windows.Forms.SizeType]::Percent
    $style.Height = $Percent
    [void]$Table.RowStyles.Add($style)
}

function New-ComboBox {
    param([string[]]$Items, [int]$SelectedIndex = 0)
    $combo = New-Object System.Windows.Forms.ComboBox
    $combo.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
    $combo.Dock = 'Fill'
    $combo.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 4, 5, 6, 5
    foreach ($item in $Items) { [void]$combo.Items.Add($item) }
    if ($combo.Items.Count -gt 0) { $combo.SelectedIndex = $SelectedIndex }
    return $combo
}

function Add-LabeledRow {
    param(
        [System.Windows.Forms.TableLayoutPanel]$Table,
        [int]$Row,
        [string]$Text,
        [System.Windows.Forms.Control]$Control
    )
    $label = New-Object System.Windows.Forms.Label
    $label.Text = $Text
    $label.Dock = 'Fill'
    $label.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
    $label.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 8, 3, 3, 3
    $label.Font = New-UiFont 9
    $Control.Dock = 'Fill'
    [void]$Table.Controls.Add($label, 0, $Row)
    [void]$Table.Controls.Add($Control, 1, $Row)
}

function Show-Error {
    param([string]$Message, [string]$Title = 'Film Grain Studio')
    [void][System.Windows.Forms.MessageBox]::Show(
        $form,
        $Message,
        $Title,
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error
    )
}

function Show-Info {
    param([string]$Message, [string]$Title = 'Film Grain Studio')
    [void][System.Windows.Forms.MessageBox]::Show(
        $form,
        $Message,
        $Title,
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Information
    )
}

function Initialize-HardwareCaps {
    $script:HardwareCaps = $null
    $script:HardwareCapsReady = $false
    $script:Av1Available = $true
    $script:Av1UhqAvailable = $false
    $script:HevcAvailable = $true

    if (-not (Test-Path -LiteralPath $HardwareCapsScript -PathType Leaf)) { return }
    if (-not (Test-Path -LiteralPath $Ffmpeg -PathType Leaf)) { return }

    try {
        $powerShellExe = Join-Path $PSHOME 'powershell.exe'
        & $powerShellExe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $HardwareCapsScript `
            -FFmpeg $Ffmpeg -GpuIndex 0 -CudaDevice 0 -VulkanDevice 0 `
            -CachePath $HardwareCapsCache -Quiet 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $HardwareCapsCache -PathType Leaf)) { return }

        $caps = Get-Content -LiteralPath $HardwareCapsCache -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $caps.caps -or -not $caps.caps.av1 -or -not $caps.caps.hevc) { return }
        $script:HardwareCaps = $caps
        $script:HardwareCapsReady = $true
        $script:Av1Available = [bool]$caps.caps.av1.available
        $script:Av1UhqAvailable = [bool]$caps.caps.av1.uhq
        $script:HevcAvailable = [bool]$caps.caps.hevcPipeline
    } catch {
        $script:HardwareCaps = $null
        $script:HardwareCapsReady = $false
        $script:Av1Available = $true
        $script:Av1UhqAvailable = $false
        $script:HevcAvailable = $true
    }
}

function Get-FFmpegVersionLabel {
    if ($script:HardwareCapsReady -and $script:HardwareCaps.ffmpeg -and $script:HardwareCaps.ffmpeg.version) {
        $line = [string]$script:HardwareCaps.ffmpeg.version
        if ($line -match '^ffmpeg version\s+([0-9]+(?:\.[0-9]+){1,3})') { return $matches[1] }
        if ($line -match '^ffmpeg version\s+([^\s]+)') { return $matches[1] }
    }
    if ($script:FFmpegVersionOverride) { return [string]$script:FFmpegVersionOverride }
    if (Test-Path -LiteralPath $Ffmpeg -PathType Leaf) {
        try {
            $line = (& $Ffmpeg -version 2>$null | Select-Object -First 1)
            if ($line -match '^ffmpeg version\s+([0-9]+(?:\.[0-9]+){1,3})') { return $matches[1] }
            if ($line -match '^ffmpeg version\s+([^\s]+)') { return $matches[1] }
        } catch {}
    }
    return '未检测'
}

function Update-HardwareProfileUi {
    if (-not $profileNote -or -not $cmbGpu) { return }
    $ffmpegVersion = Get-FFmpegVersionLabel
    if ($script:HardwareCapsReady) {
        $yesNo = @('不可用', '可用')
        $av1Text = $yesNo[[int]$script:Av1Available]
        $av1UhqText = $yesNo[[int]$script:Av1UhqAvailable]
        $hevcText = $yesNo[[int]$script:HevcAvailable]
        $cacheStateText = switch ([string]$script:HardwareCaps.cacheState) {
            'Detected' { '已适配' }
            'Cached'   { '已缓存' }
            default    { [string]$script:HardwareCaps.cacheState }
        }
        $profileNote.Text = "驱动：$($script:HardwareCaps.gpu.driverVersion) · FFmpeg：$ffmpegVersion · 配置：$cacheStateText`r`nAV1 Main10：$av1Text · UHQ：$av1UhqText；HEVC/Vulkan：$hevcText；其余参数按实测启用。"
        $cmbGpu.Items.Clear()
        [void]$cmbGpu.Items.Add(([string]$script:HardwareCaps.gpu.name + '（自动检测）'))
        $cmbGpu.SelectedIndex = 0
    } else {
        $profileNote.Text = "FFmpeg：$ffmpegVersion`r`n硬件检测尚未完成；请检查配置路径，编码启动时会自动重试。"
        $cmbGpu.Items.Clear()
        [void]$cmbGpu.Items.Add('自动检测（编码启动时再次校验）')
        $cmbGpu.SelectedIndex = 0
    }
}

Initialize-HardwareCaps

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Film Grain Studio'
$form.StartPosition = 'CenterScreen'
$form.Size = New-Object System.Drawing.Size -ArgumentList 1260, 950
$form.MinimumSize = New-Object System.Drawing.Size -ArgumentList 1120, 950
$form.AutoScaleMode = [System.Windows.Forms.AutoScaleMode]::Dpi
$form.Font = New-UiFont 9
$form.AllowDrop = $true

$root = New-Object System.Windows.Forms.TableLayoutPanel
$root.Dock = 'Fill'
$root.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0
$root.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 0
$root.RowCount = 4
$root.ColumnCount = 1
Add-RowAbsolute $root 68
Add-RowPercent $root 100
Add-RowAbsolute $root 225
Add-RowAbsolute $root 58
[void]$form.Controls.Add($root)

# Header
$header = New-Object System.Windows.Forms.Panel
$header.Dock = 'Fill'
$header.BackColor = $ColorHeader

$title = New-Object System.Windows.Forms.Label
$title.Text = 'Film Grain Studio'
$title.ForeColor = [System.Drawing.Color]::White
$title.Font = New-UiFont 18 ([System.Drawing.FontStyle]::Bold)
$title.AutoSize = $true
$title.Location = New-Object System.Drawing.Point -ArgumentList 20, 10
[void]$header.Controls.Add($title)

$subtitle = New-Object System.Windows.Forms.Label
$subtitle.Text = 'AV1 grav1synth  ·  HEVC 扫描胶片颗粒  ·  LUT 图库'
$subtitle.ForeColor = [System.Drawing.Color]::FromArgb(205, 214, 224)
$subtitle.Font = New-UiFont 9
$subtitle.AutoSize = $true
$subtitle.Location = New-Object System.Drawing.Point -ArgumentList 22, 43
[void]$header.Controls.Add($subtitle)

$btnConfig = New-Object System.Windows.Forms.Button
$btnConfig.Text = '配置…'
$btnConfig.Size = New-Object System.Drawing.Size -ArgumentList 76, 30
$btnConfig.Anchor = 'Top,Right'
$btnConfig.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$btnConfig.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(110, 126, 145)
$btnConfig.ForeColor = [System.Drawing.Color]::White
$btnConfig.BackColor = [System.Drawing.Color]::FromArgb(58, 72, 90)
$btnConfig.Location = New-Object System.Drawing.Point -ArgumentList 1158, 19
[void]$header.Controls.Add($btnConfig)

$baseline = New-Object System.Windows.Forms.Label
$baseline.Text = '核心：Universal HEVC / AV1'
$baseline.ForeColor = [System.Drawing.Color]::FromArgb(205, 214, 224)
$baseline.AutoSize = $true
$baseline.Anchor = 'Top,Right'
$baseline.Location = New-Object System.Drawing.Point -ArgumentList 955, 27
$header.Add_Resize({
    $btnConfig.Left = $header.ClientSize.Width - $btnConfig.Width - 20
    $baseline.Left = $btnConfig.Left - $baseline.Width - 18
})
[void]$header.Controls.Add($baseline)
[void]$root.Controls.Add($header, 0, 0)

# Main three-column area
$main = New-Object System.Windows.Forms.TableLayoutPanel
$main.Dock = 'Fill'
$main.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 10, 10, 10, 6
$main.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0
$main.ColumnCount = 3
$main.RowCount = 1
Add-ColumnPercent $main 34
Add-ColumnPercent $main 31
Add-ColumnPercent $main 35
[void]$root.Controls.Add($main, 0, 1)

# Input group
$grpInput = New-Object System.Windows.Forms.GroupBox
$grpInput.Text = '输入视频'
$grpInput.Dock = 'Fill'
$grpInput.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0, 0, 6, 0

$inputLayout = New-Object System.Windows.Forms.TableLayoutPanel
$inputLayout.Dock = 'Fill'
$inputLayout.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 7, 6, 7, 7
$inputLayout.RowCount = 4
$inputLayout.ColumnCount = 1
Add-RowAbsolute $inputLayout 39
Add-RowPercent $inputLayout 100
Add-RowAbsolute $inputLayout 112
Add-RowAbsolute $inputLayout 44
[void]$grpInput.Controls.Add($inputLayout)

$inputButtons = New-Object System.Windows.Forms.FlowLayoutPanel
$inputButtons.Dock = 'Fill'
$inputButtons.FlowDirection = 'LeftToRight'
$inputButtons.WrapContents = $false

$btnAdd = New-Object System.Windows.Forms.Button
$btnAdd.Text = '添加文件…'
$btnAdd.Size = New-Object System.Drawing.Size -ArgumentList 92, 29
$btnRemove = New-Object System.Windows.Forms.Button
$btnRemove.Text = '移除所选'
$btnRemove.Size = New-Object System.Drawing.Size -ArgumentList 82, 29
$btnClear = New-Object System.Windows.Forms.Button
$btnClear.Text = '清空'
$btnClear.Size = New-Object System.Drawing.Size -ArgumentList 62, 29
[void]$inputButtons.Controls.Add($btnAdd)
[void]$inputButtons.Controls.Add($btnRemove)
[void]$inputButtons.Controls.Add($btnClear)
[void]$inputLayout.Controls.Add($inputButtons, 0, 0)

$listFiles = New-Object System.Windows.Forms.ListView
$listFiles.Dock = 'Fill'
$listFiles.View = [System.Windows.Forms.View]::Details
$listFiles.FullRowSelect = $true
$listFiles.GridLines = $true
$listFiles.HideSelection = $false
$listFiles.AllowDrop = $true
[void]$listFiles.Columns.Add('文件名', 178)
[void]$listFiles.Columns.Add('大小', 72)
[void]$listFiles.Columns.Add('目录', 260)
[void]$inputLayout.Controls.Add($listFiles, 0, 1)

$grpMediaInfo = New-Object System.Windows.Forms.GroupBox
$grpMediaInfo.Text = '所选视频信息'
$grpMediaInfo.Dock = 'Fill'
$grpMediaInfo.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0, 5, 0, 3

$lblMediaInfo = New-Object System.Windows.Forms.Label
$lblMediaInfo.Dock = 'Fill'
$lblMediaInfo.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 7, 4, 7, 3
$lblMediaInfo.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$lblMediaInfo.ForeColor = $ColorMuted
$lblMediaInfo.Font = New-UiFont 8.5
$lblMediaInfo.AutoEllipsis = $true
$lblMediaInfo.Text = "选择一个视频，可查看编码、码率、分辨率与时长。"
[void]$grpMediaInfo.Controls.Add($lblMediaInfo)
[void]$inputLayout.Controls.Add($grpMediaInfo, 0, 2)

$inputNote = New-Object System.Windows.Forms.Label
$inputNote.Dock = 'Fill'
$inputNote.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$inputNote.ForeColor = $ColorMuted
$inputNote.Text = "可拖入多个视频。输出位于源目录；已有输出会跳过。"
[void]$inputLayout.Controls.Add($inputNote, 0, 3)
[void]$main.Controls.Add($grpInput, 0, 0)

# Shared encoding group
$grpEncode = New-Object System.Windows.Forms.GroupBox
$grpEncode.Text = '编码与画幅'
$grpEncode.Dock = 'Fill'
$grpEncode.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 6, 0, 6, 0

$encodeTable = New-Object System.Windows.Forms.TableLayoutPanel
$encodeTable.Dock = 'Fill'
$encodeTable.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 5, 7, 5, 5
$encodeTable.ColumnCount = 2
$encodeTable.RowCount = 14
$encodeTable.ColumnStyles.Clear()
$labelColumn = New-Object System.Windows.Forms.ColumnStyle
$labelColumn.SizeType = [System.Windows.Forms.SizeType]::Absolute
$labelColumn.Width = 112
[void]$encodeTable.ColumnStyles.Add($labelColumn)
$valueColumn = New-Object System.Windows.Forms.ColumnStyle
$valueColumn.SizeType = [System.Windows.Forms.SizeType]::Percent
$valueColumn.Width = 100
[void]$encodeTable.ColumnStyles.Add($valueColumn)
for ($i = 0; $i -lt 13; $i++) { Add-RowAbsolute $encodeTable 34 }
Add-RowPercent $encodeTable 100
[void]$grpEncode.Controls.Add($encodeTable)

$codecItems = @('AV1 · grav1synth 胶片颗粒（默认）', 'HEVC · 扫描胶片颗粒')
$initialCodecIndex = 0
if ($script:HardwareCapsReady -and -not $script:Av1Available) {
    $codecItems[0] = 'AV1 · grav1synth 胶片颗粒（当前硬件不可用）'
    $initialCodecIndex = 1
}
$cmbCodec = New-ComboBox $codecItems $initialCodecIndex
$script:LastCodecIndex = $initialCodecIndex
$cmbContainer = New-ComboBox @('MP4 · AAC 256k（默认）', 'MKV · 保留原始流') 0
$cmbSpeed = New-ComboBox @('FAST · p5 / qres（默认）', 'Standard · p6 / fullres') 0

$cmbBitrate = New-Object System.Windows.Forms.ComboBox
$cmbBitrate.Dock = 'Fill'
$cmbBitrate.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDown
$cmbBitrate.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 4, 5, 6, 5

$cmbFps = New-ComboBox @('自动（隔行→双帧率，如 29.97i → 59.94p）', '保持源帧率') 0

$cmbDeint = New-ComboBox @('自动（仅对隔行素材启用）', '关闭') 0
$cmbDeintMethod = New-ComboBox @('BWDIF Vulkan（默认）', 'BWDIF CUDA（备选）', 'W3FDIF Complex（高质量对照）') 0

$gpuDisplay = '自动检测（编码启动时再次校验）'
if ($script:HardwareCapsReady) { $gpuDisplay = [string]$script:HardwareCaps.gpu.name + '（自动检测）' }
$cmbGpu = New-ComboBox @($gpuDisplay) 0
$cmbGpu.Enabled = $false

$chkCinematic = New-Object System.Windows.Forms.CheckBox
$chkCinematic.Text = '启用 Cinematic Style（约 2.39:1）'
$chkCinematic.Checked = $true
$chkCinematic.Dock = 'Fill'
$chkCinematic.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 7, 7, 3, 3

$cmbFrameMode = New-ComboBox @(
    '加黑边 · 保留原分辨率（推荐后期字幕）',
    '裁剪 · 输出有效 2.39:1 画面'
) 0

$cinematicPanel = New-Object System.Windows.Forms.TableLayoutPanel
$cinematicPanel.Dock = 'Fill'
$cinematicPanel.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0, 0, 0, 0
$cinematicPanel.ColumnCount = 1
$cinematicPanel.RowCount = 1
$cinematicCheckCol = New-Object System.Windows.Forms.ColumnStyle
$cinematicCheckCol.SizeType = [System.Windows.Forms.SizeType]::Percent
$cinematicCheckCol.Width = 100
[void]$cinematicPanel.ColumnStyles.Add($cinematicCheckCol)
[void]$cinematicPanel.Controls.Add($chkCinematic, 0, 0)

$chkUploadHighMotion = New-Object System.Windows.Forms.CheckBox
$chkUploadHighMotion.Text = '高动态视频'
$chkUploadHighMotion.Checked = $false
$chkUploadHighMotion.Dock = 'Fill'
$chkUploadHighMotion.Enabled = $false
$chkUploadHighMotion.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 8, 7, 3, 3

$btnUploadSubtitle = New-Object System.Windows.Forms.Button
$btnUploadSubtitle.Text = '字幕…'
$btnUploadSubtitle.Dock = 'Fill'
$btnUploadSubtitle.Enabled = $true
$btnUploadSubtitle.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 3, 4, 8, 4

$uploadExtraPanel = New-Object System.Windows.Forms.TableLayoutPanel
$uploadExtraPanel.Dock = 'Fill'
$uploadExtraPanel.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0, 0, 0, 0
$uploadExtraPanel.ColumnCount = 2
$uploadExtraPanel.RowCount = 1

$uploadExtraMotionCol = New-Object System.Windows.Forms.ColumnStyle
$uploadExtraMotionCol.SizeType = [System.Windows.Forms.SizeType]::Percent
$uploadExtraMotionCol.Width = 68
[void]$uploadExtraPanel.ColumnStyles.Add($uploadExtraMotionCol)

$uploadExtraSubCol = New-Object System.Windows.Forms.ColumnStyle
$uploadExtraSubCol.SizeType = [System.Windows.Forms.SizeType]::Percent
$uploadExtraSubCol.Width = 32
[void]$uploadExtraPanel.ColumnStyles.Add($uploadExtraSubCol)

[void]$uploadExtraPanel.Controls.Add($chkUploadHighMotion, 0, 0)
[void]$uploadExtraPanel.Controls.Add($btnUploadSubtitle, 1, 0)

$uploadPanel = New-Object System.Windows.Forms.TableLayoutPanel
$uploadPanel.Dock = 'Fill'
$uploadPanel.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0, 0, 0, 0
$uploadPanel.ColumnCount = 2
$uploadPanel.RowCount = 1
$uploadCheckCol = New-Object System.Windows.Forms.ColumnStyle
$uploadCheckCol.SizeType = [System.Windows.Forms.SizeType]::Percent
$uploadCheckCol.Width = 52
[void]$uploadPanel.ColumnStyles.Add($uploadCheckCol)
$uploadRateCol = New-Object System.Windows.Forms.ColumnStyle
$uploadRateCol.SizeType = [System.Windows.Forms.SizeType]::Percent
$uploadRateCol.Width = 48
[void]$uploadPanel.ColumnStyles.Add($uploadRateCol)

$chkUpload = New-Object System.Windows.Forms.CheckBox
$chkUpload.Text = '同时生成 H.264 上传版'
$chkUpload.Dock = 'Fill'
$chkUpload.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 7, 4, 3, 3

$cmbUploadBitrate = New-ComboBox @(
    '6000 kbps · NVENC',
    '8000 kbps · NVENC',
    '15000 kbps · NVENC',
    'x264 Grain 推荐 · FPS联动',
    'x264 Grain 高质量 · FPS联动',
    'x264 Grain 极高 · FPS联动'
) 1
$cmbUploadBitrate.Enabled = $false
$cmbUploadBitrate.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 3, 5, 6, 5

[void]$uploadPanel.Controls.Add($chkUpload, 0, 0)
[void]$uploadPanel.Controls.Add($cmbUploadBitrate, 1, 0)

$frameHelp = New-Object System.Windows.Forms.Label
$frameHelp.Dock = 'Fill'
$frameHelp.AutoEllipsis = $true
$frameHelp.ForeColor = $ColorMuted
$frameHelp.TextAlign = [System.Drawing.ContentAlignment]::TopLeft
$frameHelp.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 8, 6, 8, 0
$frameHelp.Text = 'HEVC / AV1 均可选择烘焙黑边或裁剪有效画面。'

Add-LabeledRow $encodeTable 0 '编码方式' $cmbCodec
Add-LabeledRow $encodeTable 1 '输出容器' $cmbContainer
Add-LabeledRow $encodeTable 2 '速度 / 质量' $cmbSpeed
Add-LabeledRow $encodeTable 3 '视频码率 (kbps)' $cmbBitrate
Add-LabeledRow $encodeTable 4 '输出帧率' $cmbFps
Add-LabeledRow $encodeTable 5 '反交错' $cmbDeint
Add-LabeledRow $encodeTable 6 '自动方式' $cmbDeintMethod
Add-LabeledRow $encodeTable 7 'GPU 配置' $cmbGpu
[void]$encodeTable.Controls.Add($cinematicPanel, 0, 8)
$encodeTable.SetColumnSpan($cinematicPanel, 2)
Add-LabeledRow $encodeTable 9 '画幅处理' $cmbFrameMode
[void]$encodeTable.Controls.Add($uploadPanel, 0, 10)
$encodeTable.SetColumnSpan($uploadPanel, 2)
[void]$encodeTable.Controls.Add($uploadExtraPanel, 0, 11)
$encodeTable.SetColumnSpan($uploadExtraPanel, 2)
[void]$encodeTable.Controls.Add($frameHelp, 0, 12)
$encodeTable.SetColumnSpan($frameHelp, 2)

$profileNote = New-Object System.Windows.Forms.Label
$profileNote.Dock = 'Fill'
$profileNote.ForeColor = $ColorMuted
$profileNote.TextAlign = [System.Drawing.ContentAlignment]::TopLeft
$profileNote.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 8, 3, 8, 0
[void]$encodeTable.Controls.Add($profileNote, 0, 13)
$encodeTable.SetColumnSpan($profileNote, 2)
Update-HardwareProfileUi
[void]$main.Controls.Add($grpEncode, 1, 0)

# Right side: mode-specific Grain and LUT
$rightLayout = New-Object System.Windows.Forms.TableLayoutPanel
$rightLayout.Dock = 'Fill'
$rightLayout.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 6, 0, 0, 0
$rightLayout.RowCount = 2
$rightLayout.ColumnCount = 1
Add-RowPercent $rightLayout 38
Add-RowPercent $rightLayout 62
[void]$main.Controls.Add($rightLayout, 2, 0)

$grpGrain = New-Object System.Windows.Forms.GroupBox
$grpGrain.Text = 'AV1 · 胶片颗粒元数据'
$grpGrain.Dock = 'Fill'
$grpGrain.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0, 0, 0, 5
[void]$rightLayout.Controls.Add($grpGrain, 0, 0)

$grainHost = New-Object System.Windows.Forms.Panel
$grainHost.Dock = 'Fill'
$grainHost.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 5, 5, 5, 5
[void]$grpGrain.Controls.Add($grainHost)

# AV1 Grain panel
$pnlAv1 = New-Object System.Windows.Forms.TableLayoutPanel
$pnlAv1.Dock = 'Fill'
$pnlAv1.ColumnCount = 2
$pnlAv1.RowCount = 6
$av1LabelCol = New-Object System.Windows.Forms.ColumnStyle
$av1LabelCol.SizeType = [System.Windows.Forms.SizeType]::Absolute
$av1LabelCol.Width = 100
[void]$pnlAv1.ColumnStyles.Add($av1LabelCol)
$av1ValueCol = New-Object System.Windows.Forms.ColumnStyle
$av1ValueCol.SizeType = [System.Windows.Forms.SizeType]::Percent
$av1ValueCol.Width = 100
[void]$pnlAv1.ColumnStyles.Add($av1ValueCol)
for ($i = 0; $i -lt 6; $i++) { Add-RowPercent $pnlAv1 (100 / 6) }

$cmbAv1Method = New-ComboBox @('胶片预设（推荐）', '感光度 ISO（高级）', '现成 Grain Table（影视 / Photon）') 0
$cmbAv1Format = New-ComboBox @('Classic35 · Super 35', 'Modern35 · Full-frame', '16mm · Coarser', 'Super8 · Heavy', 'MaxMid · Synthetic') 0
$cmbAv1Stock = New-ComboBox @('Fujifilm Eterna 250D', 'Fujifilm Eterna 500T', 'Kodak Vision3 250D', 'Kodak Vision3 200T') 0

$numIso = New-Object System.Windows.Forms.NumericUpDown
$numIso.Minimum = 1
$numIso.Maximum = 1000000
$numIso.Value = 1600
$numIso.Increment = 100
$numIso.ThousandsSeparator = $true
$numIso.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 4, 5, 6, 5

$chkChroma = New-Object System.Windows.Forms.CheckBox
$chkChroma.Text = '亮度 + 色度（默认仅亮度）'
$chkChroma.Dock = 'Fill'
$chkChroma.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 7, 4, 3, 3

$av1TablePanel = New-Object System.Windows.Forms.TableLayoutPanel
$av1TablePanel.Dock = 'Fill'
$av1TablePanel.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0
$av1TablePanel.ColumnCount = 3
$av1TablePanel.RowCount = 1
$av1TableComboCol = New-Object System.Windows.Forms.ColumnStyle
$av1TableComboCol.SizeType = [System.Windows.Forms.SizeType]::Percent
$av1TableComboCol.Width = 100
[void]$av1TablePanel.ColumnStyles.Add($av1TableComboCol)
$av1TableRefreshCol = New-Object System.Windows.Forms.ColumnStyle
$av1TableRefreshCol.SizeType = [System.Windows.Forms.SizeType]::Absolute
$av1TableRefreshCol.Width = 42
[void]$av1TablePanel.ColumnStyles.Add($av1TableRefreshCol)
$av1TableAllCol = New-Object System.Windows.Forms.ColumnStyle
$av1TableAllCol.SizeType = [System.Windows.Forms.SizeType]::Absolute
$av1TableAllCol.Width = 28
[void]$av1TablePanel.ColumnStyles.Add($av1TableAllCol)

$cmbAv1GrainTable = New-ComboBox @('正在扫描 Grain Table…') 0
$cmbAv1GrainTable.DropDownWidth = 560
$cmbAv1GrainTable.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 4, 5, 3, 5
$btnRefreshAv1Table = New-Object System.Windows.Forms.Button
$btnRefreshAv1Table.Text = '↻'
$btnRefreshAv1Table.Dock = 'Fill'
$btnRefreshAv1Table.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0, 4, 0, 4
$chkShowAllAv1Tables = New-Object System.Windows.Forms.CheckBox
$chkShowAllAv1Tables.Text = ''
$chkShowAllAv1Tables.Checked = $false
$chkShowAllAv1Tables.Dock = 'Fill'
$chkShowAllAv1Tables.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 6, 4, 0, 4
[void]$av1TablePanel.Controls.Add($cmbAv1GrainTable, 0, 0)
[void]$av1TablePanel.Controls.Add($btnRefreshAv1Table, 1, 0)
[void]$av1TablePanel.Controls.Add($chkShowAllAv1Tables, 2, 0)

Add-LabeledRow $pnlAv1 0 '颗粒方式' $cmbAv1Method
Add-LabeledRow $pnlAv1 1 '胶片格式' $cmbAv1Format
Add-LabeledRow $pnlAv1 2 '胶片型号' $cmbAv1Stock
Add-LabeledRow $pnlAv1 3 '感光度 ISO' $numIso
Add-LabeledRow $pnlAv1 4 'Grain Table' $av1TablePanel
[void]$pnlAv1.Controls.Add($chkChroma, 0, 5)
$pnlAv1.SetColumnSpan($chkChroma, 2)
[void]$grainHost.Controls.Add($pnlAv1)

# HEVC Grain panel
$pnlHevc = New-Object System.Windows.Forms.TableLayoutPanel
$pnlHevc.Dock = 'Fill'
$pnlHevc.ColumnCount = 2
$pnlHevc.RowCount = 5
$pnlHevc.Visible = $false
$hevcLabelCol = New-Object System.Windows.Forms.ColumnStyle
$hevcLabelCol.SizeType = [System.Windows.Forms.SizeType]::Absolute
$hevcLabelCol.Width = 100
[void]$pnlHevc.ColumnStyles.Add($hevcLabelCol)
$hevcValueCol = New-Object System.Windows.Forms.ColumnStyle
$hevcValueCol.SizeType = [System.Windows.Forms.SizeType]::Percent
$hevcValueCol.Width = 100
[void]$pnlHevc.ColumnStyles.Add($hevcValueCol)
for ($i = 0; $i -lt 4; $i++) { Add-RowPercent $pnlHevc 21 }
Add-RowPercent $pnlHevc 16

$grainRootPanel = New-Object System.Windows.Forms.TableLayoutPanel
$grainRootPanel.Dock = 'Fill'
$grainRootPanel.ColumnCount = 3
$grainRootPanel.RowCount = 1
$grainRootTextCol = New-Object System.Windows.Forms.ColumnStyle
$grainRootTextCol.SizeType = [System.Windows.Forms.SizeType]::Percent
$grainRootTextCol.Width = 100
[void]$grainRootPanel.ColumnStyles.Add($grainRootTextCol)
$grainRootButtonCol = New-Object System.Windows.Forms.ColumnStyle
$grainRootButtonCol.SizeType = [System.Windows.Forms.SizeType]::Absolute
$grainRootButtonCol.Width = 42
[void]$grainRootPanel.ColumnStyles.Add($grainRootButtonCol)
$grainRootRefreshCol = New-Object System.Windows.Forms.ColumnStyle
$grainRootRefreshCol.SizeType = [System.Windows.Forms.SizeType]::Absolute
$grainRootRefreshCol.Width = 42
[void]$grainRootPanel.ColumnStyles.Add($grainRootRefreshCol)

$txtGrainRoot = New-Object System.Windows.Forms.TextBox
$txtGrainRoot.Text = $DefaultGrainRoot
$txtGrainRoot.Dock = 'Fill'
$txtGrainRoot.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0, 5, 3, 5
$btnGrainRoot = New-Object System.Windows.Forms.Button
$btnGrainRoot.Text = '…'
$btnGrainRoot.Dock = 'Fill'
$btnGrainRoot.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0, 4, 0, 4
$btnRefreshGrain = New-Object System.Windows.Forms.Button
$btnRefreshGrain.Text = '↻'
$btnRefreshGrain.Dock = 'Fill'
$btnRefreshGrain.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 3, 4, 0, 4
[void]$grainRootPanel.Controls.Add($txtGrainRoot, 0, 0)
[void]$grainRootPanel.Controls.Add($btnGrainRoot, 1, 0)
[void]$grainRootPanel.Controls.Add($btnRefreshGrain, 2, 0)

$cmbHevcPlate = New-ComboBox @('正在扫描颗粒根目录…') 0
$cmbHevcPlate.Enabled = $false
$cmbHevcPlate.DropDownWidth = 420

$hevcStrengthPanel = New-Object System.Windows.Forms.TableLayoutPanel
$hevcStrengthPanel.Dock = 'Fill'
$hevcStrengthPanel.ColumnCount = 2
$hevcStrengthPanel.RowCount = 1
$strengthTrackCol = New-Object System.Windows.Forms.ColumnStyle
$strengthTrackCol.SizeType = [System.Windows.Forms.SizeType]::Percent
$strengthTrackCol.Width = 100
[void]$hevcStrengthPanel.ColumnStyles.Add($strengthTrackCol)
$strengthLabelCol = New-Object System.Windows.Forms.ColumnStyle
$strengthLabelCol.SizeType = [System.Windows.Forms.SizeType]::Absolute
$strengthLabelCol.Width = 92
[void]$hevcStrengthPanel.ColumnStyles.Add($strengthLabelCol)

$trackHevcStrength = New-Object System.Windows.Forms.TrackBar
$trackHevcStrength.Minimum = 0
$trackHevcStrength.Maximum = 3
$trackHevcStrength.Value = 2
$trackHevcStrength.TickStyle = [System.Windows.Forms.TickStyle]::BottomRight
$trackHevcStrength.Dock = 'Fill'
$trackHevcStrength.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0, 1, 0, 0
$lblHevcStrength = New-Object System.Windows.Forms.Label
$lblHevcStrength.Text = 'Strong · 85%'
$lblHevcStrength.Dock = 'Fill'
$lblHevcStrength.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
[void]$hevcStrengthPanel.Controls.Add($trackHevcStrength, 0, 0)
[void]$hevcStrengthPanel.Controls.Add($lblHevcStrength, 1, 0)

$cacheNote = New-Object System.Windows.Forms.Label
$cacheNote.Text = '将递归扫描根目录中的原始 MOV；缓存自动匹配，缺失时回退 MOV。'
$cacheNote.ForeColor = $ColorMuted
$cacheNote.Dock = 'Fill'
$cacheNote.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$cacheNote.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 8, 0, 2, 0

Add-LabeledRow $pnlHevc 0 '颗粒根目录' $grainRootPanel
Add-LabeledRow $pnlHevc 1 '扫描颗粒片' $cmbHevcPlate
Add-LabeledRow $pnlHevc 2 '颗粒强度' $hevcStrengthPanel
[void]$pnlHevc.Controls.Add($cacheNote, 0, 3)
$pnlHevc.SetColumnSpan($cacheNote, 2)
[void]$grainHost.Controls.Add($pnlHevc)

# LUT group
$grpLut = New-Object System.Windows.Forms.GroupBox
$grpLut.Text = '电影风格 / LUT 图库'
$grpLut.Dock = 'Fill'
$grpLut.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0, 5, 0, 0
[void]$rightLayout.Controls.Add($grpLut, 0, 1)

$lutTable = New-Object System.Windows.Forms.TableLayoutPanel
$lutTable.Dock = 'Fill'
$lutTable.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 6, 5, 6, 5
$lutTable.ColumnCount = 3
$lutTable.RowCount = 5
$lutCol1 = New-Object System.Windows.Forms.ColumnStyle
$lutCol1.SizeType = [System.Windows.Forms.SizeType]::Absolute
$lutCol1.Width = 88
[void]$lutTable.ColumnStyles.Add($lutCol1)
$lutCol2 = New-Object System.Windows.Forms.ColumnStyle
$lutCol2.SizeType = [System.Windows.Forms.SizeType]::Percent
$lutCol2.Width = 100
[void]$lutTable.ColumnStyles.Add($lutCol2)
$lutCol3 = New-Object System.Windows.Forms.ColumnStyle
$lutCol3.SizeType = [System.Windows.Forms.SizeType]::Absolute
$lutCol3.Width = 106
[void]$lutTable.ColumnStyles.Add($lutCol3)
Add-RowAbsolute $lutTable 34
Add-RowAbsolute $lutTable 34
Add-RowAbsolute $lutTable 34
Add-RowPercent $lutTable 100
Add-RowAbsolute $lutTable 42
[void]$grpLut.Controls.Add($lutTable)

$chkLut = New-Object System.Windows.Forms.CheckBox
$chkLut.Text = '启用 LUT'
$chkLut.Dock = 'Fill'
$chkLut.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 4, 3, 3, 3
$btnLutGallery = New-Object System.Windows.Forms.Button
$btnLutGallery.Text = '打开 LUT 图库…'
$btnLutGallery.Dock = 'Fill'
$btnLutGallery.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 3
$btnLutClear = New-Object System.Windows.Forms.Button
$btnLutClear.Text = '清除'
$btnLutClear.Dock = 'Fill'
$btnLutClear.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 3
[void]$lutTable.Controls.Add($chkLut, 0, 0)
[void]$lutTable.Controls.Add($btnLutGallery, 1, 0)
[void]$lutTable.Controls.Add($btnLutClear, 2, 0)

$lblRecentLut = New-Object System.Windows.Forms.Label
$lblRecentLut.Text = '最近使用'
$lblRecentLut.Dock = 'Fill'
$lblRecentLut.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$lblRecentLut.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 4, 0, 0, 0

$cmbRecentLut = New-Object System.Windows.Forms.ComboBox
$cmbRecentLut.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbRecentLut.Dock = 'Fill'
$cmbRecentLut.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 3, 4, 3, 3
$cmbRecentLut.DropDownWidth = 440
$cmbRecentLut.MaxDropDownItems = 25
[void]$lutTable.Controls.Add($lblRecentLut, 0, 1)
[void]$lutTable.Controls.Add($cmbRecentLut, 1, 1)
$lutTable.SetColumnSpan($cmbRecentLut, 2)

$lblFavoriteLut = New-Object System.Windows.Forms.Label
$lblFavoriteLut.Text = '我的最爱'
$lblFavoriteLut.Dock = 'Fill'
$lblFavoriteLut.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$lblFavoriteLut.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 4, 0, 0, 0

$cmbFavoriteLut = New-Object System.Windows.Forms.ComboBox
$cmbFavoriteLut.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbFavoriteLut.Dock = 'Fill'
$cmbFavoriteLut.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 3, 4, 3, 3
$cmbFavoriteLut.DropDownWidth = 440
$cmbFavoriteLut.MaxDropDownItems = 25
[void]$lutTable.Controls.Add($lblFavoriteLut, 0, 2)
[void]$lutTable.Controls.Add($cmbFavoriteLut, 1, 2)
$lutTable.SetColumnSpan($cmbFavoriteLut, 2)

$lutPreviewPanel = New-Object System.Windows.Forms.TableLayoutPanel
$lutPreviewPanel.Dock = 'Fill'
$lutPreviewPanel.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 4, 3, 4, 2
$lutPreviewPanel.ColumnCount = 1
$lutPreviewPanel.RowCount = 2
Add-RowPercent $lutPreviewPanel 100
Add-RowAbsolute $lutPreviewPanel 22

$picLutPreview = New-Object System.Windows.Forms.PictureBox
$picLutPreview.Size = New-Object System.Drawing.Size -ArgumentList 240, 135
$picLutPreview.Anchor = [System.Windows.Forms.AnchorStyles]::None
$picLutPreview.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0
$picLutPreview.BackColor = [System.Drawing.Color]::Black
$picLutPreview.BorderStyle = [System.Windows.Forms.BorderStyle]::None
$picLutPreview.SizeMode = [System.Windows.Forms.PictureBoxSizeMode]::Normal
$picLutPreview.TabStop = $false
[void]$lutPreviewPanel.Controls.Add($picLutPreview, 0, 0)

$lblSelectedLut = New-Object System.Windows.Forms.Label
$lblSelectedLut.Text = '未选择 LUT'
$lblSelectedLut.Dock = 'Fill'
$lblSelectedLut.AutoEllipsis = $true
$lblSelectedLut.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$lblSelectedLut.ForeColor = $ColorMuted
$lblSelectedLut.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 4, 0, 4, 0
[void]$lutPreviewPanel.Controls.Add($lblSelectedLut, 0, 1)
[void]$lutTable.Controls.Add($lutPreviewPanel, 0, 3)
$lutTable.SetColumnSpan($lutPreviewPanel, 3)

$lblLutStrengthTitle = New-Object System.Windows.Forms.Label
$lblLutStrengthTitle.Text = 'LUT 强度'
$lblLutStrengthTitle.Dock = 'Fill'
$lblLutStrengthTitle.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft

$trackLutStrength = New-Object System.Windows.Forms.TrackBar
$trackLutStrength.Minimum = 0
$trackLutStrength.Maximum = 3
$trackLutStrength.Value = 2
$trackLutStrength.TickStyle = [System.Windows.Forms.TickStyle]::BottomRight
$trackLutStrength.Dock = 'Fill'
$trackLutStrength.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0
$trackLutStrength.Enabled = $false

$lblLutStrength = New-Object System.Windows.Forms.Label
$lblLutStrength.Text = '75%'
$lblLutStrength.Dock = 'Fill'
$lblLutStrength.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$lblLutStrength.Enabled = $false
[void]$lutTable.Controls.Add($lblLutStrengthTitle, 0, 4)
[void]$lutTable.Controls.Add($trackLutStrength, 1, 4)
[void]$lutTable.Controls.Add($lblLutStrength, 2, 4)

$toolTip = New-Object System.Windows.Forms.ToolTip
$toolTip.SetToolTip($btnGrainRoot, '选择颗粒根目录')
$toolTip.SetToolTip($btnRefreshGrain, '重新扫描根目录中的 .mov 颗粒片')
$toolTip.SetToolTip($cmbAv1GrainTable, '默认仅显示与源视频分辨率最接近的 Grain Table 档位。')
$toolTip.SetToolTip($btnRefreshAv1Table, '重新扫描 _AV1_Grain_Tables。')
$toolTip.SetToolTip($chkShowAllAv1Tables, '显示全部分辨率 Grain Table；默认仅显示与源视频最接近的分辨率档位。')
$toolTip.SetToolTip($btnUploadSubtitle, '硬字幕独立于 H.264 上传版；启用后烧写到主输出，若同时生成 H.264 上传版则副本也包含同一字幕。默认距最终输出画面下沿 5px、水平居中。')
$toolTip.SetToolTip($cmbUploadBitrate, 'NVENC：固定 6M / 8M / 15M。x264 Grain：按实际输出 FPS 与分辨率自动换算；分辨率按相对 1080p 像素面积平方根缩放。默认普通动态再乘 0.5，高动态视频勾选后使用完整码率。x264 使用 Slow + tune grain + 2-pass，VBV Max=3×、Buf=6×。')
$toolTip.SetToolTip($chkUploadHighMotion, '仅影响 x264 Grain FPS联动模式。默认不勾选：自动计算码率减半；勾选：使用完整高动态码率。NVENC 不受影响。')

# Log area
$grpLog = New-Object System.Windows.Forms.GroupBox
$grpLog.Text = '任务日志'
$grpLog.Dock = 'Fill'
$grpLog.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 10, 2, 10, 4

$logLayout = New-Object System.Windows.Forms.TableLayoutPanel
$logLayout.Dock = 'Fill'
$logLayout.RowCount = 2
$logLayout.ColumnCount = 1
Add-RowAbsolute $logLayout 28
Add-RowPercent $logLayout 100
[void]$grpLog.Controls.Add($logLayout)

$logToolbar = New-Object System.Windows.Forms.TableLayoutPanel
$logToolbar.Dock = 'Fill'
$logToolbar.ColumnCount = 4
$logToolbar.RowCount = 1
$logToolbar.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 3, 0, 0, 0

$logStageColumn = New-Object System.Windows.Forms.ColumnStyle
$logStageColumn.SizeType = [System.Windows.Forms.SizeType]::Absolute
$logStageColumn.Width = 360
[void]$logToolbar.ColumnStyles.Add($logStageColumn)
$logMetricColumn = New-Object System.Windows.Forms.ColumnStyle
$logMetricColumn.SizeType = [System.Windows.Forms.SizeType]::Percent
$logMetricColumn.Width = 100
[void]$logToolbar.ColumnStyles.Add($logMetricColumn)
$logCopyColumn = New-Object System.Windows.Forms.ColumnStyle
$logCopyColumn.SizeType = [System.Windows.Forms.SizeType]::Absolute
$logCopyColumn.Width = 84
[void]$logToolbar.ColumnStyles.Add($logCopyColumn)
$logClearColumn = New-Object System.Windows.Forms.ColumnStyle
$logClearColumn.SizeType = [System.Windows.Forms.SizeType]::Absolute
$logClearColumn.Width = 84
[void]$logToolbar.ColumnStyles.Add($logClearColumn)

$lblRunStage = New-Object System.Windows.Forms.Label
$lblRunStage.Text = '等待任务'
$lblRunStage.AutoSize = $false
$lblRunStage.Dock = 'Fill'
$lblRunStage.Height = 23
$lblRunStage.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$lblRunMetric = New-Object System.Windows.Forms.Label
$lblRunMetric.Text = 'fps: —   speed: —'
$lblRunMetric.AutoSize = $false
$lblRunMetric.Dock = 'Fill'
$lblRunMetric.Height = 23
$lblRunMetric.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$lblRunMetric.ForeColor = $ColorMuted
$lblRunMetric.AutoEllipsis = $true
$btnCopyLog = New-Object System.Windows.Forms.Button
$btnCopyLog.Text = '复制日志'
$btnCopyLog.Size = New-Object System.Drawing.Size -ArgumentList 78, 24
$btnCopyLog.Anchor = 'Top,Right'
$btnClearLog = New-Object System.Windows.Forms.Button
$btnClearLog.Text = '清空日志'
$btnClearLog.Size = New-Object System.Drawing.Size -ArgumentList 78, 24
$btnClearLog.Anchor = 'Top,Right'
[void]$logToolbar.Controls.Add($lblRunStage, 0, 0)
[void]$logToolbar.Controls.Add($lblRunMetric, 1, 0)
[void]$logToolbar.Controls.Add($btnCopyLog, 2, 0)
[void]$logToolbar.Controls.Add($btnClearLog, 3, 0)
[void]$logLayout.Controls.Add($logToolbar, 0, 0)

$rtbLog = New-Object System.Windows.Forms.RichTextBox
$rtbLog.Dock = 'Fill'
$rtbLog.ReadOnly = $true
$rtbLog.WordWrap = $false
$rtbLog.DetectUrls = $false
$rtbLog.BackColor = [System.Drawing.Color]::FromArgb(28, 30, 34)
$rtbLog.ForeColor = [System.Drawing.Color]::Gainsboro
$rtbLog.Font = New-Object System.Drawing.Font -ArgumentList 'Consolas', 9
$rtbLog.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
[void]$logLayout.Controls.Add($rtbLog, 0, 1)
[void]$root.Controls.Add($grpLog, 0, 2)

# Footer
$footer = New-Object System.Windows.Forms.Panel
$footer.Dock = 'Fill'
$footer.BackColor = $ColorSubtle
[void]$root.Controls.Add($footer, 0, 3)

$lblStatus = New-Object System.Windows.Forms.Label
$lblStatus.Text = '就绪 · 请添加视频'
$lblStatus.AutoSize = $false
$lblStatus.Size = New-Object System.Drawing.Size -ArgumentList 560, 30
$lblStatus.Location = New-Object System.Drawing.Point -ArgumentList 14, 14
$lblStatus.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
[void]$footer.Controls.Add($lblStatus)

$progress = New-Object System.Windows.Forms.ProgressBar
$progress.Style = [System.Windows.Forms.ProgressBarStyle]::Blocks
$progress.Size = New-Object System.Drawing.Size -ArgumentList 220, 20
$progress.Anchor = 'Top,Right'
$progress.Location = New-Object System.Drawing.Point -ArgumentList 765, 19
[void]$footer.Controls.Add($progress)

$btnCancel = New-Object System.Windows.Forms.Button
$btnCancel.Text = '取消任务'
$btnCancel.Enabled = $false
$btnCancel.Size = New-Object System.Drawing.Size -ArgumentList 94, 34
$btnCancel.Anchor = 'Top,Right'
$btnCancel.Location = New-Object System.Drawing.Point -ArgumentList 995, 11
[void]$footer.Controls.Add($btnCancel)

$btnStart = New-Object System.Windows.Forms.Button
$btnStart.Text = '开始编码'
$btnStart.ForeColor = [System.Drawing.Color]::White
$btnStart.BackColor = $ColorAccent
$btnStart.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$btnStart.FlatAppearance.BorderSize = 0
$btnStart.Size = New-Object System.Drawing.Size -ArgumentList 126, 36
$btnStart.Anchor = 'Top,Right'
$btnStart.Location = New-Object System.Drawing.Point -ArgumentList 1098, 10
[void]$footer.Controls.Add($btnStart)

$footer.Add_Resize({
    $btnStart.Left = $footer.ClientSize.Width - $btnStart.Width - 14
    $btnCancel.Left = $btnStart.Left - $btnCancel.Width - 9
    $progress.Left = $btnCancel.Left - $progress.Width - 12
})

# Dialogs
$openDialog = New-Object System.Windows.Forms.OpenFileDialog
$openDialog.Title = '选择一个或多个视频文件'
$openDialog.Multiselect = $true
$openDialog.Filter = '视频文件|*.mp4;*.mkv;*.mov;*.mxf;*.avi;*.webm;*.ts;*.m2ts;*.mts|所有文件|*.*'

$folderDialog = New-Object System.Windows.Forms.FolderBrowserDialog
$folderDialog.Description = '选择 HEVC 扫描胶片颗粒根目录'
$folderDialog.ShowNewFolderButton = $false

function Get-SubtitleProbeExe {
    $probeExe = $Ffprobe
    if (Test-Path -LiteralPath $probeExe -PathType Leaf) { return $probeExe }
    return $null
}

function Get-TextSubtitleTracks {
    param([string]$Path)
    $result = @()
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $result }
    $probeExe = Get-SubtitleProbeExe
    if (-not $probeExe) { return $result }
    try {
        $json = (& $probeExe -v error -select_streams s -show_entries 'stream=index,codec_name:stream_tags=language,title' -of json $Path 2>$null | Out-String)
        if (-not $json) { return $result }
        $data = $json | ConvertFrom-Json
        $ordinal = 0
        $textCodecs = @('subrip','ass','ssa','webvtt','mov_text','text','sami','microdvd','jacosub','realtext','subviewer','subviewer1','vplayer')
        foreach ($stream in @($data.streams)) {
            $codec = ([string]$stream.codec_name).ToLowerInvariant()
            if ($codec -in $textCodecs) {
                $lang = if ($stream.tags -and $stream.tags.language) { [string]$stream.tags.language } else { 'und' }
                $title = if ($stream.tags -and $stream.tags.title) { [string]$stream.tags.title } else { '' }
                $label = "内嵌 #$($ordinal + 1) · $lang · $codec"
                if ($title) { $label += " · $title" }
                $result += [pscustomobject]@{ Ordinal = $ordinal; Label = $label; Codec = $codec }
            }
            $ordinal++
        }
    } catch {}
    return $result
}

function Find-SameNameSubtitleFile {
    param([string]$VideoPath)
    if (-not $VideoPath) { return $null }
    $dir = [System.IO.Path]::GetDirectoryName($VideoPath)
    $base = [System.IO.Path]::GetFileNameWithoutExtension($VideoPath)
    foreach ($ext in @('.ass','.srt','.ssa','.vtt')) {
        $candidate = Join-Path $dir ($base + $ext)
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    return $null
}

function Set-SubtitleColorButton {
    param([System.Windows.Forms.Button]$Button, [string]$Hex)
    $hexValue = ([string]$Hex).Trim().TrimStart('#')
    if ($hexValue -notmatch '^[0-9A-Fa-f]{6}$') { $hexValue = 'FFFFFF' }
    $Button.Text = '#' + $hexValue.ToUpperInvariant()
    $Button.BackColor = [System.Drawing.ColorTranslator]::FromHtml('#' + $hexValue)
    $brightness = ($Button.BackColor.R * 299 + $Button.BackColor.G * 587 + $Button.BackColor.B * 114) / 1000
    $Button.ForeColor = if ($brightness -lt 128) { [System.Drawing.Color]::White } else { [System.Drawing.Color]::Black }
}

function Show-UploadSubtitleDialog {
    $targetPath = $null
    $selected = @($listFiles.SelectedItems)
    if ($selected.Count -eq 1) { $targetPath = [string]$selected[0].Tag }
    elseif ($listFiles.Items.Count -eq 1) { $targetPath = [string]$listFiles.Items[0].Tag }

    $tracks = @()
    $sameName = $null
    if ($targetPath) {
        $tracks = @(Get-TextSubtitleTracks $targetPath)
        $sameName = Find-SameNameSubtitleFile $targetPath
    }

    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = '硬字幕'
    $dlg.StartPosition = 'CenterParent'
    $dlg.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $dlg.MaximizeBox = $false
    $dlg.MinimizeBox = $false
    $dlg.ShowInTaskbar = $false
    $dlg.ClientSize = New-Object System.Drawing.Size -ArgumentList 610, 425
    $dlg.Font = New-UiFont 9

    $table = New-Object System.Windows.Forms.TableLayoutPanel
    $table.Dock = 'Fill'
    $table.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 12
    $table.ColumnCount = 3
    $table.RowCount = 10
    $c0 = New-Object System.Windows.Forms.ColumnStyle
    $c0.SizeType = [System.Windows.Forms.SizeType]::Absolute
    $c0.Width = 118
    [void]$table.ColumnStyles.Add($c0)
    $c1 = New-Object System.Windows.Forms.ColumnStyle
    $c1.SizeType = [System.Windows.Forms.SizeType]::Percent
    $c1.Width = 100
    [void]$table.ColumnStyles.Add($c1)
    $c2 = New-Object System.Windows.Forms.ColumnStyle
    $c2.SizeType = [System.Windows.Forms.SizeType]::Absolute
    $c2.Width = 92
    [void]$table.ColumnStyles.Add($c2)
    for ($i=0; $i -lt 8; $i++) { Add-RowAbsolute $table 36 }
    Add-RowPercent $table 100
    Add-RowAbsolute $table 42
    [void]$dlg.Controls.Add($table)

    $cmbSource = New-ComboBox @() 0
    $cmbSource.DropDownWidth = 500
    $sourceDefs = New-Object System.Collections.ArrayList
    [void]$cmbSource.Items.Add('关闭 · 不烧写字幕')
    [void]$sourceDefs.Add([pscustomobject]@{ Mode='OFF'; Index=0; Path=''; Label='字幕：关闭' })
    [void]$cmbSource.Items.Add('自动匹配 · 同名外部字幕优先，否则内嵌 #1')
    [void]$sourceDefs.Add([pscustomobject]@{ Mode='AUTO'; Index=0; Path=''; Label='字幕：自动匹配' })

    foreach ($track in $tracks) {
        [void]$cmbSource.Items.Add([string]$track.Label)
        [void]$sourceDefs.Add([pscustomobject]@{ Mode='EMBEDDED'; Index=[int]$track.Ordinal; Path=''; Label=('字幕：内嵌 #' + ([int]$track.Ordinal + 1)) })
    }
    if ($sameName) {
        [void]$cmbSource.Items.Add('同名外部 · ' + [System.IO.Path]::GetFileName($sameName))
        [void]$sourceDefs.Add([pscustomobject]@{ Mode='EXTERNAL'; Index=0; Path=$sameName; Label=('字幕：' + [System.IO.Path]::GetFileName($sameName)) })
    }
    [void]$cmbSource.Items.Add('浏览外部字幕文件…')
    [void]$sourceDefs.Add([pscustomobject]@{ Mode='BROWSE'; Index=0; Path=''; Label='字幕：外部文件' })

    $desired = 0
    if ($script:UploadSubtitle.Enabled) {
        for ($i=0; $i -lt $sourceDefs.Count; $i++) {
            $d=$sourceDefs[$i]
            if ($script:UploadSubtitle.Mode -eq 'AUTO' -and $d.Mode -eq 'AUTO') { $desired=$i; break }
            if ($script:UploadSubtitle.Mode -eq 'EMBEDDED' -and $d.Mode -eq 'EMBEDDED' -and [int]$d.Index -eq [int]$script:UploadSubtitle.EmbeddedIndex) { $desired=$i; break }
            if ($script:UploadSubtitle.Mode -eq 'EXTERNAL' -and $d.Mode -eq 'EXTERNAL' -and $script:UploadSubtitle.ExternalPath -and $d.Path -eq $script:UploadSubtitle.ExternalPath) { $desired=$i; break }
        }
    } elseif ($tracks.Count -gt 0) {
        $desired = 2
    } elseif ($sameName) {
        $desired = $sourceDefs.Count - 2
    } else {
        $desired = 1
    }
    if ($cmbSource.Items.Count -gt 0) { $cmbSource.SelectedIndex = $desired }

    $btnBrowseSub = New-Object System.Windows.Forms.Button
    $btnBrowseSub.Text = '浏览…'
    $btnBrowseSub.Dock = 'Fill'
    $btnBrowseSub.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 3,4,3,4

    $txtFont = New-Object System.Windows.Forms.TextBox
    $txtFont.Text = [string]$script:UploadSubtitle.FontName
    $txtFont.Dock = 'Fill'
    $txtFont.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 4,6,3,5

    $numSize = New-Object System.Windows.Forms.NumericUpDown
    $numSize.Minimum = 6; $numSize.Maximum = 200; $numSize.Value = [decimal]$script:UploadSubtitle.FontSize
    $numSize.Dock = 'Fill'; $numSize.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 4,5,3,5

    $btnPrimary = New-Object System.Windows.Forms.Button
    $btnPrimary.Dock='Fill'; $btnPrimary.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 4,4,3,4
    Set-SubtitleColorButton $btnPrimary ([string]$script:UploadSubtitle.PrimaryHex)
    $btnBorder = New-Object System.Windows.Forms.Button
    $btnBorder.Dock='Fill'; $btnBorder.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 4,4,3,4
    Set-SubtitleColorButton $btnBorder ([string]$script:UploadSubtitle.BorderHex)

    $numOutline = New-Object System.Windows.Forms.NumericUpDown
    $numOutline.DecimalPlaces=1; $numOutline.Increment=[decimal]0.5; $numOutline.Minimum=0; $numOutline.Maximum=10; $numOutline.Value=[decimal]$script:UploadSubtitle.Outline
    $numOutline.Dock='Fill'; $numOutline.Margin=New-Object System.Windows.Forms.Padding -ArgumentList 4,5,3,5
    $numShadow = New-Object System.Windows.Forms.NumericUpDown
    $numShadow.DecimalPlaces=1; $numShadow.Increment=[decimal]0.5; $numShadow.Minimum=0; $numShadow.Maximum=10; $numShadow.Value=[decimal]$script:UploadSubtitle.Shadow
    $numShadow.Dock='Fill'; $numShadow.Margin=New-Object System.Windows.Forms.Padding -ArgumentList 4,5,3,5
    $numMargin = New-Object System.Windows.Forms.NumericUpDown
    $numMargin.Minimum=0; $numMargin.Maximum=300; $numMargin.Value=[decimal]$script:UploadSubtitle.MarginV
    $numMargin.Dock='Fill'; $numMargin.Margin=New-Object System.Windows.Forms.Padding -ArgumentList 4,5,3,5

    function Add-SubDialogLabel([int]$row,[string]$text,[System.Windows.Forms.Control]$control,[int]$span=1) {
        $lbl=New-Object System.Windows.Forms.Label
        $lbl.Text=$text; $lbl.Dock='Fill'; $lbl.TextAlign=[System.Drawing.ContentAlignment]::MiddleLeft
        $lbl.Margin=New-Object System.Windows.Forms.Padding -ArgumentList 4,3,3,3
        [void]$table.Controls.Add($lbl,0,$row)
        [void]$table.Controls.Add($control,1,$row)
        if ($span -gt 1) { $table.SetColumnSpan($control,$span) }
    }

    Add-SubDialogLabel 0 '字幕来源' $cmbSource 1
    [void]$table.Controls.Add($btnBrowseSub,2,0)
    Add-SubDialogLabel 1 '字体' $txtFont 2
    Add-SubDialogLabel 2 '字号' $numSize 2
    Add-SubDialogLabel 3 '字体颜色' $btnPrimary 2
    Add-SubDialogLabel 4 '边框/阴影颜色' $btnBorder 2
    Add-SubDialogLabel 5 '边框宽度' $numOutline 2
    Add-SubDialogLabel 6 '阴影' $numShadow 2
    Add-SubDialogLabel 7 '距画面下沿' $numMargin 2

    $note = New-Object System.Windows.Forms.Label
    $note.Dock='Fill'; $note.ForeColor=$ColorMuted; $note.Padding=New-Object System.Windows.Forms.Padding -ArgumentList 4,6,4,0
    $note.Text = "默认：huiwen-mincho / 69 / 白色 / 黑色边框与阴影 / Outline 1 / Shadow 1 / 距最终输出画面下沿 5px、水平居中。启用添加黑边时，黑边属于最终输出画面，字幕自然位于下方黑边内；不添加黑边时，字幕位于视频画面底部并覆盖少量内容。字号、边距、描边和阴影均以 1920×1080 为基准，实际编码按输出宽度等比缩放。`r`n支持 SRT / ASS / SSA / WebVTT 等文本字幕；PGS/DVD 图形字幕暂不烧写。多文件任务建议使用各视频同名字幕。"
    [void]$table.Controls.Add($note,0,8); $table.SetColumnSpan($note,3)

    $buttons = New-Object System.Windows.Forms.FlowLayoutPanel
    $buttons.Dock='Fill'; $buttons.FlowDirection='RightToLeft'; $buttons.WrapContents=$false
    $ok = New-Object System.Windows.Forms.Button; $ok.Text='确定'; $ok.Width=82; $ok.DialogResult=[System.Windows.Forms.DialogResult]::OK
    $cancel = New-Object System.Windows.Forms.Button; $cancel.Text='取消'; $cancel.Width=82; $cancel.DialogResult=[System.Windows.Forms.DialogResult]::Cancel
    [void]$buttons.Controls.Add($ok); [void]$buttons.Controls.Add($cancel)
    [void]$table.Controls.Add($buttons,0,9); $table.SetColumnSpan($buttons,3)
    $dlg.AcceptButton=$ok; $dlg.CancelButton=$cancel

    $colorDialog = New-Object System.Windows.Forms.ColorDialog
    $btnPrimary.Add_Click({ $colorDialog.Color=$btnPrimary.BackColor; if ($colorDialog.ShowDialog($dlg) -eq [System.Windows.Forms.DialogResult]::OK) { $hex = ('{0:X2}{1:X2}{2:X2}' -f $colorDialog.Color.R,$colorDialog.Color.G,$colorDialog.Color.B); Set-SubtitleColorButton $btnPrimary $hex } })
    $btnBorder.Add_Click({ $colorDialog.Color=$btnBorder.BackColor; if ($colorDialog.ShowDialog($dlg) -eq [System.Windows.Forms.DialogResult]::OK) { $hex = ('{0:X2}{1:X2}{2:X2}' -f $colorDialog.Color.R,$colorDialog.Color.G,$colorDialog.Color.B); Set-SubtitleColorButton $btnBorder $hex } })

    $browseDialog = New-Object System.Windows.Forms.OpenFileDialog
    $browseDialog.Title='选择外部字幕文件'
    $browseDialog.Filter='字幕文件|*.srt;*.ass;*.ssa;*.vtt|所有文件|*.*'
    if ($targetPath) { $browseDialog.InitialDirectory=[System.IO.Path]::GetDirectoryName($targetPath) }
    $chosenBrowsePath = ''
    $btnBrowseSub.Add_Click({
        if ($browseDialog.ShowDialog($dlg) -eq [System.Windows.Forms.DialogResult]::OK) {
            $chosenBrowsePath=$browseDialog.FileName
            $browseIndex=$sourceDefs.Count-1
            $sourceDefs[$browseIndex].Path=$chosenBrowsePath
            $sourceDefs[$browseIndex].Label='字幕：' + [System.IO.Path]::GetFileName($chosenBrowsePath)
            $cmbSource.Items[$browseIndex]='外部 · ' + [System.IO.Path]::GetFileName($chosenBrowsePath)
            $cmbSource.SelectedIndex=$browseIndex
        }
    })

    $result=$dlg.ShowDialog($form)
    if ($result -ne [System.Windows.Forms.DialogResult]::OK) { $dlg.Dispose(); return }
    $def=$sourceDefs[$cmbSource.SelectedIndex]
    if ($def.Mode -eq 'BROWSE' -and -not $def.Path) {
        Show-Info '尚未选择外部字幕文件。'
        $dlg.Dispose(); return
    }

    $script:UploadSubtitle.Enabled = ($def.Mode -ne 'OFF')
    $script:UploadSubtitle.Mode = if ($def.Mode -eq 'BROWSE') { 'EXTERNAL' } else { [string]$def.Mode }
    $script:UploadSubtitle.EmbeddedIndex = [int]$def.Index
    $script:UploadSubtitle.ExternalPath = [string]$def.Path
    $script:UploadSubtitle.FontName = $txtFont.Text.Trim()
    if (-not $script:UploadSubtitle.FontName) { $script:UploadSubtitle.FontName='huiwen-mincho' }
    $script:UploadSubtitle.FontSize = [int]$numSize.Value
    $script:UploadSubtitle.PrimaryHex = $btnPrimary.Text.TrimStart('#')
    $script:UploadSubtitle.BorderHex = $btnBorder.Text.TrimStart('#')
    $script:UploadSubtitle.Outline = [double]$numOutline.Value
    $script:UploadSubtitle.Shadow = [double]$numShadow.Value
    $script:UploadSubtitle.MarginV = [int]$numMargin.Value
    $script:UploadSubtitle.Label = if ($script:UploadSubtitle.Enabled) { [string]$def.Label } else { '字幕：关闭' }
    $btnUploadSubtitle.Text = if ($script:UploadSubtitle.Enabled) { '字幕 ✓' } else { '字幕…' }
    $toolTip.SetToolTip($btnUploadSubtitle, [string]$script:UploadSubtitle.Label)
    $dlg.Dispose()
}

function Update-UploadHighMotionUi {
    $isX264 = ($cmbUploadBitrate.SelectedIndex -ge 3)
    $chkUploadHighMotion.Enabled = ($chkUpload.Checked -and $isX264)
}

function Set-MediaInfoText {
    param([string]$Text, [bool]$IsMuted = $false)
    $lblMediaInfo.Text = $Text
    $lblMediaInfo.ForeColor = if ($IsMuted) { $ColorMuted } else { [System.Drawing.SystemColors]::ControlText }
    if ($toolTip) { $toolTip.SetToolTip($lblMediaInfo, $Text) }
}

function Format-MediaBitrate {
    param($Value)
    $rate = 0L
    if ($null -eq $Value -or -not [long]::TryParse([string]$Value, [ref]$rate) -or $rate -le 0) { return '—' }
    if ($rate -ge 1000000) { return ('{0:0.##} Mb/s' -f ($rate / 1000000.0)) }
    return ('{0:0} kb/s' -f ($rate / 1000.0))
}

function Format-MediaDuration {
    param($Value)
    $seconds = 0.0
    if ($null -eq $Value -or -not [double]::TryParse([string]$Value, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$seconds) -or $seconds -lt 0) {
        return '—'
    }
    $hours = [Math]::Floor($seconds / 3600)
    $minutes = [Math]::Floor(($seconds % 3600) / 60)
    $remaining = $seconds % 60
    return ('{0:00}:{1:00}:{2:00.000}' -f $hours, $minutes, $remaining)
}

function Format-MediaFps {
    param($Value)
    if (-not $Value) { return '—' }
    $parts = ([string]$Value).Split('/')
    $numerator = 0.0
    $denominator = 1.0
    if ($parts.Count -eq 2 -and
        [double]::TryParse($parts[0], [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$numerator) -and
        [double]::TryParse($parts[1], [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$denominator) -and
        $denominator -ne 0) {
        return ('{0:0.###}' -f ($numerator / $denominator))
    }
    return [string]$Value
}

function Format-CodecName {
    param($Stream)
    if ($null -eq $Stream) { return '—' }
    $codec = if ($Stream.codec_name) { ([string]$Stream.codec_name).ToUpperInvariant() } else { '未知' }
    $profile = [string]$Stream.profile
    if ($profile -and $profile -ne 'unknown' -and $profile -notmatch '^N/A$') { return "$codec · $profile" }
    return $codec
}

function Format-ProbeResult {
    param($Data)
    $streams = @($Data.streams)
    $video = $streams | Where-Object { $_.codec_type -eq 'video' } | Select-Object -First 1
    $audio = $streams | Where-Object { $_.codec_type -eq 'audio' } | Select-Object -First 1

    if ($video) {
        $resolution = if ($video.width -and $video.height) { "$($video.width)×$($video.height)" } else { '—' }
        $fps = Format-MediaFps $video.avg_frame_rate
        $fieldOrder = ([string]$video.field_order).ToLowerInvariant()
        $scanText = if ($fieldOrder -in @('tt', 'bb', 'tb', 'bt')) { "隔行 $fieldOrder" } elseif ($fieldOrder -eq 'progressive') { '逐行' } elseif ($fieldOrder) { "扫描标记 $fieldOrder" } else { '扫描标记 —' }
        $videoLine = '视频  ' + (Format-CodecName $video) + " · $resolution · $fps fps · $scanText · " + (Format-MediaBitrate $video.bit_rate)
    } else {
        $videoLine = '视频  未找到视频流'
    }

    if ($audio) {
        $channelText = if ($audio.channel_layout) { [string]$audio.channel_layout } elseif ($audio.channels) { "$($audio.channels) 声道" } else { '声道 —' }
        $sampleText = '—'
        $sampleRate = 0.0
        if ($audio.sample_rate -and [double]::TryParse([string]$audio.sample_rate, [ref]$sampleRate) -and $sampleRate -gt 0) {
            $sampleText = ('{0:0.###} kHz' -f ($sampleRate / 1000.0))
        }
        $audioLine = '音频  ' + (Format-CodecName $audio) + " · $channelText · $sampleText · " + (Format-MediaBitrate $audio.bit_rate)
    } else {
        $audioLine = '音频  无音频流'
    }

    $format = $Data.format
    $duration = Format-MediaDuration $format.duration
    $totalRate = Format-MediaBitrate $format.bit_rate
    $formatLine = "时长  $duration · 总码率 $totalRate"
    return $videoLine + "`r`n" + $audioLine + "`r`n" + $formatLine
}

function Stop-VideoProbe {
    if ($probeTimer) { $probeTimer.Stop() }
    if ($script:ProbeProcess) {
        try {
            if (-not $script:ProbeProcess.HasExited) {
                $script:ProbeProcess.Kill()
                [void]$script:ProbeProcess.WaitForExit(500)
            }
        } catch {}
        try { $script:ProbeProcess.Dispose() } catch {}
    }
    $script:ProbeProcess = $null
    $script:ProbeOutputTask = $null
    $script:ProbeErrorTask = $null
    $script:ProbeTargetPath = ''
}

function Start-VideoProbe {
    param([string]$Path)
    Stop-VideoProbe
    if (-not $Path -or -not [System.IO.File]::Exists($Path)) {
        Set-MediaInfoText '文件不存在或已经被移动。' $true
        return
    }

    $cacheKey = $Path.ToLowerInvariant()
    if ($script:ProbeCache.ContainsKey($cacheKey)) {
        $summary = [string]$script:ProbeCache[$cacheKey]
        Set-MediaInfoText $summary
        Update-DeinterlaceUi
        Update-NoReencodeAvailability
        if ($script:ProbeVideoMeta.ContainsKey($cacheKey) -and ([string]$script:ProbeVideoMeta[$cacheKey].codec_name).ToLowerInvariant() -eq 'av1') {
            Start-Av1GrainInspect $Path $summary
        } else {
            Stop-Av1GrainInspect
        }
        if ($cmbAv1Method.SelectedIndex -eq 2 -and -not $chkShowAllAv1Tables.Checked) { Refresh-Av1GrainTables }
        return
    }

    $probeExe = $Ffprobe
    if (-not $probeExe -or (-not (Test-Path -LiteralPath $probeExe -PathType Leaf))) {
        Set-MediaInfoText "找不到 FFprobe：$Ffprobe" $true
        return
    }

    Set-MediaInfoText '正在读取媒体信息…' $true
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $probeExe
        $psi.Arguments = '-v error -show_entries "format=format_name,duration,bit_rate:stream=codec_type,codec_name,profile,width,height,avg_frame_rate,field_order,bit_rate,channels,channel_layout,sample_rate" -of json "' + $Path + '"'
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true

        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $psi
        if (-not $process.Start()) { throw '无法启动 FFprobe。' }
        $script:ProbeProcess = $process
        $script:ProbeOutputTask = $process.StandardOutput.ReadToEndAsync()
        $script:ProbeErrorTask = $process.StandardError.ReadToEndAsync()
        $script:ProbeTargetPath = $Path
        $probeTimer.Start()
    } catch {
        if ($process) { try { $process.Dispose() } catch {} }
        $script:ProbeProcess = $null
        Set-MediaInfoText ('读取失败：' + $_.Exception.Message) $true
    }
}

function Stop-Av1GrainInspect {
    if ($av1InspectTimer) { $av1InspectTimer.Stop() }
    if ($script:Av1InspectProcess) {
        try {
            if (-not $script:Av1InspectProcess.HasExited) {
                $script:Av1InspectProcess.Kill()
                [void]$script:Av1InspectProcess.WaitForExit(500)
            }
        } catch {}
        try { $script:Av1InspectProcess.Dispose() } catch {}
    }
    if ($script:Av1InspectTempTable) {
        Remove-Item -LiteralPath $script:Av1InspectTempTable -Force -ErrorAction SilentlyContinue
    }
    $script:Av1InspectProcess = $null
    $script:Av1InspectOutputTask = $null
    $script:Av1InspectErrorTask = $null
    $script:Av1InspectTargetPath = ''
    $script:Av1InspectTempTable = ''
}

function Get-Av1GrainTableSummary {
    param([string]$TablePath)
    if (-not $TablePath -or -not (Test-Path -LiteralPath $TablePath -PathType Leaf)) {
        return 'AV1 胶片颗粒：无'
    }
    try {
        $lines = @(Get-Content -LiteralPath $TablePath -Encoding UTF8 -ErrorAction Stop)
        if ($lines.Count -eq 0 -or ([string]$lines[0]).Trim() -ne 'filmgrn1') {
            return 'AV1 胶片颗粒：无法识别参数表'
        }
        $hasChroma = $false
        foreach ($line in $lines) {
            $t = ([string]$line).Trim()
            if ($t -match '^sCb\s+([1-9][0-9]*)\b' -or $t -match '^sCr\s+([1-9][0-9]*)\b') {
                $hasChroma = $true
                break
            }
        }
        $plane = if ($hasChroma) { '亮度 + 色度' } else { '亮度' }
        return "AV1 胶片颗粒：$plane"
    } catch {
        return 'AV1 胶片颗粒：参数表读取失败'
    }
}

function Update-NoReencodeAvailability {
    $eligible = $false
    if ($listFiles.Items.Count -eq 1) {
        $path = [string]$listFiles.Items[0].Tag
        if ($path) {
            $key = $path.ToLowerInvariant()
            if ($script:ProbeVideoMeta.ContainsKey($key)) {
                $meta = $script:ProbeVideoMeta[$key]
                $eligible = (([string]$meta.codec_name).ToLowerInvariant() -eq 'av1')
            }
        }
    }

    $hasItem = ($cmbCodec.Items.Count -ge 3 -and [string]$cmbCodec.Items[2] -eq $script:NoReencodeItemText)
    if ($eligible -and -not $hasItem) {
        [void]$cmbCodec.Items.Add($script:NoReencodeItemText)
    } elseif (-not $eligible -and $hasItem) {
        if ($cmbCodec.SelectedIndex -eq 2) {
            $fallback = if ($script:HardwareCapsReady -and -not $script:Av1Available) { 1 } else { 0 }
            $cmbCodec.SelectedIndex = $fallback
        }
        $cmbCodec.Items.RemoveAt(2)
    }
}

function Start-Av1GrainInspect {
    param([string]$Path,[string]$BaseSummary)
    Stop-Av1GrainInspect
    if (-not $Path -or -not [System.IO.File]::Exists($Path)) { return }

    $key = $Path.ToLowerInvariant()
    if ($script:Av1GrainCache.ContainsKey($key)) {
        Set-MediaInfoText ($BaseSummary + "`r`n" + [string]$script:Av1GrainCache[$key])
        return
    }
    if (-not $Grav1synth -or -not (Test-Path -LiteralPath $Grav1synth -PathType Leaf)) {
        Set-MediaInfoText ($BaseSummary + "`r`nAV1 胶片颗粒：grav1synth 未找到")
        return
    }

    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('FilmGrainStudio_Inspect_' + [guid]::NewGuid().ToString('N') + '.txt')
    Set-MediaInfoText ($BaseSummary + "`r`nAV1 胶片颗粒：正在检测…")
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $Grav1synth
        $psi.Arguments = 'inspect "' + $Path + '" -o "' + $tmp + '" -y'
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $proc = New-Object System.Diagnostics.Process
        $proc.StartInfo = $psi
        if (-not $proc.Start()) { throw '无法启动 grav1synth inspect。' }
        $script:Av1InspectProcess = $proc
        $script:Av1InspectOutputTask = $proc.StandardOutput.ReadToEndAsync()
        $script:Av1InspectErrorTask = $proc.StandardError.ReadToEndAsync()
        $script:Av1InspectTargetPath = $Path
        $script:Av1InspectTempTable = $tmp
        $av1InspectTimer.Start()
    } catch {
        if ($proc) { try { $proc.Dispose() } catch {} }
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        $script:Av1InspectProcess = $null
        Set-MediaInfoText ($BaseSummary + "`r`nAV1 胶片颗粒：检测失败")
    }
}

function Update-SelectedMediaInfo {
    $selected = @($listFiles.SelectedItems)
    if ($selected.Count -eq 0) {
        Stop-VideoProbe
        Stop-Av1GrainInspect
        Set-MediaInfoText '选择一个视频，可查看编码、码率、分辨率、时长与 AV1 胶片颗粒状态。' $true
    } elseif ($selected.Count -gt 1) {
        Stop-VideoProbe
        Stop-Av1GrainInspect
        Set-MediaInfoText "已选择 $($selected.Count) 个视频；请选择单个视频查看信息。" $true
    } else {
        Start-VideoProbe ([string]$selected[0].Tag)
    }
}

$probeTimer = New-Object System.Windows.Forms.Timer
$probeTimer.Interval = 120
$probeTimer.Add_Tick({
    if (-not $script:ProbeProcess) {
        $probeTimer.Stop()
        return
    }
    try {
        if (-not $script:ProbeProcess.HasExited -or -not $script:ProbeOutputTask.IsCompleted -or -not $script:ProbeErrorTask.IsCompleted) { return }
        $probeTimer.Stop()
        $targetPath = $script:ProbeTargetPath
        $exitCode = $script:ProbeProcess.ExitCode
        $json = $script:ProbeOutputTask.Result
        $errorText = $script:ProbeErrorTask.Result
        try { $script:ProbeProcess.Dispose() } catch {}
        $script:ProbeProcess = $null
        $script:ProbeOutputTask = $null
        $script:ProbeErrorTask = $null
        $script:ProbeTargetPath = ''

        if ($exitCode -ne 0 -or -not $json) {
            $detail = if ($errorText) { ([System.Text.RegularExpressions.Regex]::Split(([string]$errorText).Trim(), '\r?\n'))[0] } else { "FFprobe 返回代码 $exitCode" }
            Set-MediaInfoText ('读取失败：' + $detail) $true
            return
        }
        $data = $json | ConvertFrom-Json
        $summary = Format-ProbeResult $data
        $cacheKey = $targetPath.ToLowerInvariant()
        $script:ProbeCache[$cacheKey] = $summary
        $videoMeta = @($data.streams | Where-Object { $_.codec_type -eq 'video' } | Select-Object -First 1)
        if ($videoMeta.Count -gt 0) { $script:ProbeVideoMeta[$cacheKey] = $videoMeta[0] }
        Set-MediaInfoText $summary
        Update-DeinterlaceUi
        Update-NoReencodeAvailability
        if ($cmbAv1Method.SelectedIndex -eq 2 -and -not $chkShowAllAv1Tables.Checked) { Refresh-Av1GrainTables }
        if ($videoMeta.Count -gt 0 -and ([string]$videoMeta[0].codec_name).ToLowerInvariant() -eq 'av1') {
            Start-Av1GrainInspect $targetPath $summary
        } else {
            Stop-Av1GrainInspect
        }
    } catch {
        Stop-VideoProbe
        Set-MediaInfoText ('读取失败：' + $_.Exception.Message) $true
    }
})

$av1InspectTimer = New-Object System.Windows.Forms.Timer
$av1InspectTimer.Interval = 140
$av1InspectTimer.Add_Tick({
    if (-not $script:Av1InspectProcess) {
        $av1InspectTimer.Stop()
        return
    }
    try {
        if (-not $script:Av1InspectProcess.HasExited -or -not $script:Av1InspectOutputTask.IsCompleted -or -not $script:Av1InspectErrorTask.IsCompleted) { return }
        $av1InspectTimer.Stop()
        $targetPath = $script:Av1InspectTargetPath
        $tmp = $script:Av1InspectTempTable
        $exitCode = $script:Av1InspectProcess.ExitCode
        $errorText = [string]$script:Av1InspectErrorTask.Result
        try { $script:Av1InspectProcess.Dispose() } catch {}
        $script:Av1InspectProcess = $null
        $script:Av1InspectOutputTask = $null
        $script:Av1InspectErrorTask = $null
        $script:Av1InspectTargetPath = ''
        $script:Av1InspectTempTable = ''

        if ($exitCode -eq 0) {
            $grainSummary = Get-Av1GrainTableSummary $tmp
        } else {
            $detail = ([System.Text.RegularExpressions.Regex]::Split($errorText.Trim(), '\r?\n') | Where-Object { $_ } | Select-Object -First 1)
            if (-not $detail) { $detail = "返回代码 $exitCode" }
            $grainSummary = "AV1 胶片颗粒：检测失败 · $detail"
        }
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        $key = $targetPath.ToLowerInvariant()
        $script:Av1GrainCache[$key] = $grainSummary

        $selected = @($listFiles.SelectedItems)
        if ($selected.Count -eq 1 -and [string]::Equals([string]$selected[0].Tag,$targetPath,[System.StringComparison]::OrdinalIgnoreCase)) {
            $base = if ($script:ProbeCache.ContainsKey($key)) { [string]$script:ProbeCache[$key] } else { '' }
            if ($base) { Set-MediaInfoText ($base + "`r`n" + $grainSummary) }
        }
    } catch {
        Stop-Av1GrainInspect
    }
})

function Update-FileCount {
    $count = $listFiles.Items.Count
    if ($count -eq 0) {
        $lblStatus.Text = '就绪 · 请添加视频'
    } elseif (-not $script:RunningProcess) {
        $lblStatus.Text = "就绪 · 已添加 $count 个视频"
    }
}

function Add-InputFiles {
    param([string[]]$Paths)
    if (-not $Paths) { return }

    $known = @{}
    foreach ($item in $listFiles.Items) {
        $known[[string]$item.Tag.ToLowerInvariant()] = $true
    }

    $listFiles.BeginUpdate()
    try {
        foreach ($path in $Paths) {
            if (-not $path) { continue }
            try { $fullPath = [System.IO.Path]::GetFullPath($path) } catch { continue }
            if (-not [System.IO.File]::Exists($fullPath)) { continue }
            $key = $fullPath.ToLowerInvariant()
            if ($known.ContainsKey($key)) { continue }

            $fi = New-Object System.IO.FileInfo -ArgumentList $fullPath
            $size = if ($fi.Length -ge 1GB) {
                '{0:N2} GB' -f ($fi.Length / 1GB)
            } elseif ($fi.Length -ge 1MB) {
                '{0:N1} MB' -f ($fi.Length / 1MB)
            } else {
                '{0:N0} KB' -f ($fi.Length / 1KB)
            }

            $row = New-Object System.Windows.Forms.ListViewItem -ArgumentList $fi.Name
            [void]$row.SubItems.Add($size)
            [void]$row.SubItems.Add($fi.DirectoryName)
            $row.Tag = $fi.FullName
            [void]$listFiles.Items.Add($row)
            $known[$key] = $true
        }
    } finally {
        $listFiles.EndUpdate()
    }
    Update-FileCount
    Update-NoReencodeAvailability
    if ($listFiles.Items.Count -eq 1 -and $listFiles.SelectedItems.Count -eq 0) {
        $listFiles.Items[0].Selected = $true
        $listFiles.Items[0].Focused = $true
    }
}

function Get-InputPaths {
    $paths = @()
    foreach ($item in $listFiles.Items) { $paths += [string]$item.Tag }
    return $paths
}

function Refresh-HevcGrainPlates {
    $rootText = $txtGrainRoot.Text.Trim()
    $previousPath = $null
    if ($cmbHevcPlate.SelectedIndex -ge 0 -and $cmbHevcPlate.SelectedIndex -lt $script:HevcGrainFiles.Count) {
        $previousPath = [string]$script:HevcGrainFiles[$cmbHevcPlate.SelectedIndex]
    }

    $script:HevcGrainFiles = @()
    $script:LastScannedGrainRoot = $rootText
    $cmbHevcPlate.BeginUpdate()
    try {
        $cmbHevcPlate.Items.Clear()
        $cmbHevcPlate.Enabled = $false

        if (-not $rootText -or -not (Test-Path -LiteralPath $rootText -PathType Container)) {
            [void]$cmbHevcPlate.Items.Add('颗粒根目录不存在')
            $cmbHevcPlate.SelectedIndex = 0
            $cacheNote.Text = '请选择有效的颗粒根目录，然后点击 ↻ 刷新。'
            return
        }

        $rootFull = [System.IO.Path]::GetFullPath($rootText).TrimEnd('\')
        $prefix = $rootFull + '\'
        $files = @(
            Get-ChildItem -LiteralPath $rootFull -Filter '*.mov' -File -Recurse -ErrorAction SilentlyContinue |
                Sort-Object FullName
        )

        foreach ($file in $files) {
            $fullPath = [string]$file.FullName
            $relative = [string]$file.Name
            if ($fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                $relative = $fullPath.Substring($prefix.Length)
            }
            $script:HevcGrainFiles += $fullPath
            [void]$cmbHevcPlate.Items.Add($relative)
        }

        if ($script:HevcGrainFiles.Count -eq 0) {
            [void]$cmbHevcPlate.Items.Add('未找到 .mov 扫描颗粒片')
            $cmbHevcPlate.SelectedIndex = 0
            $cacheNote.Text = '当前根目录及其子目录中没有找到原始 .mov 颗粒片。'
            return
        }

        $selectedIndex = 0
        if ($previousPath) {
            for ($i = 0; $i -lt $script:HevcGrainFiles.Count; $i++) {
                if ([string]::Equals([string]$script:HevcGrainFiles[$i], $previousPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $selectedIndex = $i
                    break
                }
            }
        }
        $cmbHevcPlate.Enabled = $true
        $cmbHevcPlate.SelectedIndex = $selectedIndex
        $cacheNote.Text = '已扫描到 ' + $script:HevcGrainFiles.Count + ' 个原始 MOV；缓存自动匹配，缺失时回退 MOV。'
    } catch {
        $script:HevcGrainFiles = @()
        $cmbHevcPlate.Items.Clear()
        [void]$cmbHevcPlate.Items.Add('扫描颗粒根目录失败')
        $cmbHevcPlate.SelectedIndex = 0
        $cacheNote.Text = '扫描失败：' + $_.Exception.Message
    } finally {
        $cmbHevcPlate.EndUpdate()
    }
}

function Load-BitrateChoices {
    param([int]$CodecIndex)
    $cmbBitrate.BeginUpdate()
    try {
        $cmbBitrate.Items.Clear()
        $values = if ($CodecIndex -eq 0) { @('1000', '1500', '2500', '4000') } else { @('6000', '7500', '9000', '12000') }
        foreach ($value in $values) { [void]$cmbBitrate.Items.Add($value) }
        $cmbBitrate.Text = [string]$script:ModeBitrate[$CodecIndex]
    } finally {
        $cmbBitrate.EndUpdate()
    }
}

function Get-Av1GrainTierFromDimensions {
    param([int]$Width, [int]$Height)
    if ($Width -le 0) { return '' }
    if ($Width -le 1280) { return '720p' }
    if ($Width -le 1920) { return '1080p' }
    if ($Width -le 2560) { return '1440p' }
    return '2160p'
}

function Get-Av1GrainTableTier {
    param([string]$Path)
    if (-not $Path) { return '' }
    $pathLower = $Path.ToLowerInvariant()
    if ($pathLower -match '[\\/](720p|1080p|1440p|2160p)([\\/]|$)') {
        return $matches[1].ToLowerInvariant()
    }

    $stem = [System.IO.Path]::GetFileNameWithoutExtension($Path)
    if ($stem -match '(?i)([0-9]{3,4})x([0-9]{3,4})') {
        return (Get-Av1GrainTierFromDimensions ([int]$matches[1]) ([int]$matches[2]))
    }
    if ($stem -match '(?i)(^|[_-])(720p|1080p|1440p|2160p)([_-]|$)') {
        return $matches[2].ToLowerInvariant()
    }
    return ''
}

function Get-Av1GrainTableDimensions {
    param([string]$Path)
    $width = 0
    $height = 0
    if ($Path) {
        $stem = [System.IO.Path]::GetFileNameWithoutExtension($Path)
        if ($stem -match '(?i)([0-9]{3,4})x([0-9]{3,4})') {
            $width = [int]$matches[1]
            $height = [int]$matches[2]
        }
    }
    return [pscustomobject]@{ Width = $width; Height = $height }
}

function Get-Av1GrainSourceContext {
    $item = $null
    $selected = @($listFiles.SelectedItems)
    if ($selected.Count -eq 1) {
        $item = $selected[0]
    } elseif ($listFiles.Items.Count -gt 0) {
        $item = $listFiles.Items[0]
    }
    if ($null -eq $item -or -not $item.Tag) {
        return [pscustomobject]@{ Width = 0; Height = 0; Tier = ''; Path = '' }
    }

    $path = [string]$item.Tag
    $key = $path.ToLowerInvariant()
    if (-not $script:ProbeVideoMeta.ContainsKey($key)) {
        return [pscustomobject]@{ Width = 0; Height = 0; Tier = ''; Path = $path }
    }

    $meta = $script:ProbeVideoMeta[$key]
    $width = 0
    $height = 0
    if ($meta.width) { $width = [int]$meta.width }
    if ($meta.height) { $height = [int]$meta.height }
    return [pscustomobject]@{
        Width = $width
        Height = $height
        Tier = (Get-Av1GrainTierFromDimensions $width $height)
        Path = $path
    }
}

function Get-Av1GrainTableSortDistance {
    param([string]$Path, [int]$SourceWidth, [int]$SourceHeight)
    $dims = Get-Av1GrainTableDimensions $Path
    if ($dims.Width -gt 0 -and $dims.Height -gt 0 -and $SourceWidth -gt 0 -and $SourceHeight -gt 0) {
        return ([Math]::Abs($dims.Width - $SourceWidth) + [Math]::Abs($dims.Height - $SourceHeight))
    }
    return 2147483647
}

function Get-Av1GrainTableDisplayName {
    param([string]$Path)

    $stem = [System.IO.Path]::GetFileNameWithoutExtension($Path)
    $pathLower = $Path.ToLowerInvariant()
    $folder = ([System.IO.Path]::GetFileName([System.IO.Path]::GetDirectoryName($Path))).ToLowerInvariant()

    # Source folders may now sit below resolution folders. Detect from the
    # whole path so 1080p\AOM\... and imported legacy trees both work.
    $source = 'Table'
    if ($pathLower -match '[\\/](aomenc-created|aom)([\\/]|$)') { $source = 'AOM' }
    elseif ($pathLower -match '[\\/](svt-created|svt)([\\/]|$)') { $source = 'SVT' }
    elseif ($pathLower -match 'photon') { $source = 'Photon' }

    $resolution = ''
    $exactResolution = ''
    if ($stem -match '(?i)([0-9]{3,4})x([0-9]{3,4})') {
        $exactResolution = $matches[1] + [char]0x00D7 + $matches[2]
        $w = [int]$matches[1]; $h = [int]$matches[2]
        if ($w -ge 3800 -or $h -ge 2000) { $resolution = '2160p' }
        elseif ($w -ge 2500 -or $h -ge 1400) { $resolution = '1440p' }
        elseif ($w -ge 1900 -or $h -ge 1000) { $resolution = '1080p' }
        elseif ($w -ge 1200 -or $h -ge 700) { $resolution = '720p' }
    } elseif ($stem -match '(?i)(^|[_-])(720p|1080p|1440p|2160p)([_-]|$)') {
        $resolution = $matches[2].ToLowerInvariant()
    } elseif ($pathLower -match '[\\/](720p|1080p|1440p|2160p)([\\/]|$)') {
        $resolution = $matches[1].ToLowerInvariant()
    }

    $strength = ''
    if ($stem -match '(?i)(^|_)(ultra_high|ultrahigh|heavy|medium|med|light|low)_grain(_|$)') {
        switch ($matches[2].ToLowerInvariant()) {
            'ultra_high' { $strength = 'Ultra High' }
            'ultrahigh'  { $strength = 'Ultra High' }
            'heavy'      { $strength = 'Heavy' }
            'medium'     { $strength = 'Medium' }
            'med'        { $strength = 'Medium' }
            'light'      { $strength = 'Light' }
            'low'        { $strength = 'Low' }
        }
    }

    $isBw = ($stem -match '(?i)(^|_)BW(_|$)')
    $pass = ''
    if ($stem -match '(?i)_P([0-9]+)$') { $pass = 'P' + $matches[1] }

    if ($pathLower -match 'photon' -or $stem -match '(?i)(^|[_-])iso[0-9]+([_-]|$)') {
        $film = ''
        if ($stem -match '(?i)(^|[_-])(8mm|16mm|35mm)([_-]|$)') { $film = $matches[2].ToLowerInvariant() }
        $iso = ''
        if ($stem -match '(?i)(^|[_-])iso([0-9]+)([_-]|$)') { $iso = 'ISO ' + $matches[2] }
        $size = ''
        if ($stem -match '(?i)(^|[_-])size([0-9]+)([_-]|$)') { $size = 'Size' + $matches[2] }
        $colorSpace = ''
        if ($stem -match '(?i)(^|[_-])BT2020([_-]|$)') { $colorSpace = 'BT.2020' }
        elseif ($stem -match '(?i)(^|[_-])SRGB([_-]|$)') { $colorSpace = 'sRGB' }
        $resLabel = if ($exactResolution) { $exactResolution } else { $resolution }
        $parts = @($film, $iso, $strength, $resLabel, $colorSpace, $size, $source) | Where-Object { $_ }
        return ($parts -join ' · ')
    }

    $title = $stem
    $title = [System.Text.RegularExpressions.Regex]::Replace($title, '(?i)_P[0-9]+$', '')
    $title = [System.Text.RegularExpressions.Regex]::Replace($title, '(?i)(^|_)BW(?=_|$)', '$1')
    $title = [System.Text.RegularExpressions.Regex]::Replace($title, '(?i)(^|_)(720p|1080p|1440p|2160p)(?=_|$)', '$1')
    $title = [System.Text.RegularExpressions.Regex]::Replace($title, '(?i)(^|_)(ultra_high|ultrahigh|heavy|medium|med|light|low)_grain(?=_|$)', '$1')
    $title = [System.Text.RegularExpressions.Regex]::Replace($title, '_+', ' ').Trim()

    $plane = if ($isBw) { 'B/W' } else { 'Color' }
    $resLabel = if ($exactResolution) { $exactResolution } else { $resolution }
    $parts = @($title, $strength, $resLabel, $plane, $source, $pass) | Where-Object { $_ }
    return ($parts -join ' · ')
}

function Refresh-Av1GrainTables {
    $previousPath = ''
    if ($cmbAv1GrainTable.SelectedIndex -ge 0 -and $cmbAv1GrainTable.SelectedIndex -lt $script:Av1GrainTableFiles.Count) {
        $previousPath = [string]$script:Av1GrainTableFiles[$cmbAv1GrainTable.SelectedIndex]
    }

    $script:Av1GrainTableFiles = @()
    $script:LastScannedAv1GrainTableRoot = $DefaultAv1GrainTableRoot
    $source = Get-Av1GrainSourceContext
    $showAll = $chkShowAllAv1Tables.Checked
    $preferredTier = [string]$source.Tier

    if ($toolTip) {
        $tip = if ($preferredTier) {
            "显示全部分辨率 Grain Table；当前源视频自动匹配：$preferredTier。"
        } else {
            '显示全部分辨率 Grain Table；读取到源视频分辨率后会自动匹配档位。'
        }
        $toolTip.SetToolTip($chkShowAllAv1Tables, $tip)
    }

    $cmbAv1GrainTable.BeginUpdate()
    try {
        $cmbAv1GrainTable.Items.Clear()
        if (-not (Test-Path -LiteralPath $DefaultAv1GrainTableRoot -PathType Container)) {
            [void]$cmbAv1GrainTable.Items.Add('未找到 _AV1_Grain_Tables 目录')
            $cmbAv1GrainTable.SelectedIndex = 0
            return
        }

        $files = @(Get-ChildItem -LiteralPath $DefaultAv1GrainTableRoot -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -ieq '.tbl' -or $_.Extension -ieq '.txt' } |
            Where-Object { $_.Name -notmatch '(?i)^README(?:_|\.|$)' })

        if (-not $showAll) {
            if ($preferredTier) {
                $files = @($files | Where-Object { (Get-Av1GrainTableTier $_.FullName) -eq $preferredTier })
            } else {
                $files = @()
            }
        }

        if ($source.Width -gt 0 -and $source.Height -gt 0) {
            $tierOrder = @{ '720p' = 0; '1080p' = 1; '1440p' = 2; '2160p' = 3; '' = 9 }
            $preferredOrder = if ($tierOrder.ContainsKey($preferredTier)) { [int]$tierOrder[$preferredTier] } else { 9 }
            $sortProps = @(
                @{ Expression = {
                    $tier = Get-Av1GrainTableTier $_.FullName
                    if ($showAll -and $tierOrder.ContainsKey($tier)) { [Math]::Abs([int]$tierOrder[$tier] - $preferredOrder) } else { 0 }
                } }
                @{ Expression = { Get-Av1GrainTableSortDistance $_.FullName $source.Width $source.Height } }
                @{ Expression = { $_.FullName } }
            )
            $files = @($files | Sort-Object -Property $sortProps)
        } else {
            $files = @($files | Sort-Object FullName)
        }

        foreach ($file in $files) {
            $script:Av1GrainTableFiles += $file.FullName
            [void]$cmbAv1GrainTable.Items.Add((Get-Av1GrainTableDisplayName $file.FullName))
        }

        if ($script:Av1GrainTableFiles.Count -eq 0) {
            if (-not $showAll -and $preferredTier) {
                [void]$cmbAv1GrainTable.Items.Add("$preferredTier 目录未扫描到 Grain Table；勾选右侧方框可显示全部")
            } elseif (-not $showAll -and -not $preferredTier) {
                [void]$cmbAv1GrainTable.Items.Add('正在等待源视频分辨率；可勾选右侧方框显示全部')
            } else {
                [void]$cmbAv1GrainTable.Items.Add('未扫描到 .tbl / .txt')
            }
            $cmbAv1GrainTable.SelectedIndex = 0
            return
        }

        $selectIndex = 0
        if ($previousPath) {
            for ($i = 0; $i -lt $script:Av1GrainTableFiles.Count; $i++) {
                if ([string]::Equals([string]$script:Av1GrainTableFiles[$i], $previousPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $selectIndex = $i
                    break
                }
            }
        }
        $cmbAv1GrainTable.SelectedIndex = $selectIndex
    } finally {
        $cmbAv1GrainTable.EndUpdate()
        Update-Av1Controls
    }
}

function Update-Av1Controls {
    $presetMode = ($cmbAv1Method.SelectedIndex -eq 0)
    $isoMode = ($cmbAv1Method.SelectedIndex -eq 1)
    $tableMode = ($cmbAv1Method.SelectedIndex -eq 2)
    $cmbAv1Format.Enabled = $presetMode
    $cmbAv1Stock.Enabled = $presetMode -and ($cmbAv1Format.SelectedIndex -lt 3)
    $numIso.Enabled = $isoMode
    $chkChroma.Enabled = $isoMode
    $cmbAv1GrainTable.Enabled = $tableMode -and ($script:Av1GrainTableFiles.Count -gt 0)
    $btnRefreshAv1Table.Enabled = $tableMode
    $chkShowAllAv1Tables.Enabled = $tableMode
}

function Update-SpeedChoices {
    $allowUhq = ($cmbCodec.SelectedIndex -eq 0 -and $script:HardwareCapsReady -and $script:Av1UhqAvailable)
    $currentText = [string]$cmbSpeed.SelectedItem
    $selectedIndex = if ($currentText -like 'Standard*') { 1 } else { 0 }
    if ($allowUhq -and $currentText -like 'UHQ*') { $selectedIndex = 2 }

    $cmbSpeed.BeginUpdate()
    try {
        $cmbSpeed.Items.Clear()
        [void]$cmbSpeed.Items.Add('FAST · p5 / qres（默认）')
        [void]$cmbSpeed.Items.Add('Standard · p6 / fullres')
        if ($allowUhq) { [void]$cmbSpeed.Items.Add('UHQ · p4 / fullres（AV1 专用）') }
        $cmbSpeed.SelectedIndex = $selectedIndex
    } finally {
        $cmbSpeed.EndUpdate()
    }
}

function Get-DoubleFpsDisplay {
    param([string]$Value)
    if (-not $Value) { return $null }
    try {
        $parts = $Value.Split('/')
        if ($parts.Count -eq 2) {
            $num = [double]::Parse($parts[0], [System.Globalization.CultureInfo]::InvariantCulture)
            $den = [double]::Parse($parts[1], [System.Globalization.CultureInfo]::InvariantCulture)
            if ($den -eq 0) { return $null }
            return ('{0:0.###}' -f (($num * 2.0) / $den))
        }
        $fps = [double]::Parse($Value, [System.Globalization.CultureInfo]::InvariantCulture)
        return ('{0:0.###}' -f ($fps * 2.0))
    } catch {
        return $null
    }
}

function Update-DeinterlaceUi {
    if ($cmbCodec.SelectedIndex -eq 2) {
        $cmbDeintMethod.Enabled = $false
        $cmbFps.Enabled = $false
        return
    }

    $auto = ($cmbDeint.SelectedIndex -eq 0)
    $cmbDeintMethod.Enabled = $auto

    $autoText = '自动（隔行→双帧率，如 29.97i → 59.94p）'
    if ($auto) {
        $selected = @($listFiles.SelectedItems)
        if ($selected.Count -eq 1) {
            $key = ([string]$selected[0].Tag).ToLowerInvariant()
            if ($script:ProbeVideoMeta.ContainsKey($key)) {
                $meta = $script:ProbeVideoMeta[$key]
                $fieldOrder = ([string]$meta.field_order).ToLowerInvariant()
                if ($fieldOrder -in @('tt', 'bb', 'tb', 'bt')) {
                    $srcFps = Format-MediaFps $meta.avg_frame_rate
                    $dstFps = Get-DoubleFpsDisplay ([string]$meta.avg_frame_rate)
                    if ($srcFps -and $srcFps -ne '—' -and $dstFps) {
                        $autoText = "自动（${srcFps}i → ${dstFps}p）"
                    }
                }
            }
        }
        if ($cmbFps.Items.Count -gt 0) { $cmbFps.Items[0] = $autoText }
        $cmbFps.SelectedIndex = 0
        $cmbFps.Enabled = $false
    } else {
        if ($cmbFps.Items.Count -gt 0) { $cmbFps.Items[0] = '自动电影帧率 · VFR 兼容（默认）' }
        $cmbFps.Enabled = $true
    }
}

function Update-FramingUi {
    if ($cmbCodec.SelectedIndex -eq 2) {
        $cmbFrameMode.Enabled = $false
        $frameHelp.Text = 'AV1 视频流不重编码，仅添加/替换胶片颗粒元数据；画幅处理已禁用。'
        return
    }

    $enabled = $chkCinematic.Checked
    $cmbFrameMode.Enabled = $enabled
    if (-not $enabled) {
        $frameHelp.Text = 'Cinematic Style 已关闭：HEVC / AV1 均保持原始画幅。'
        return
    }
    if ($cmbFrameMode.SelectedIndex -eq 0) {
        $frameHelp.Text = 'HEVC / AV1：保留原分辨率，将约 2.39:1 纯黑上下黑边烘焙进画面，适合后期字幕。'
    } else {
        $frameHelp.Text = 'HEVC / AV1：裁剪为约 2.39:1 有效画面；例如 1920×1080 → 1920×804。'
    }
}

function Update-CodecUi {
    $newIndex = $cmbCodec.SelectedIndex
    if ($newIndex -lt 0) { return }

    if ($newIndex -eq 2) {
        $script:NoReencodeUiActive = $true
        if ($cmbContainer.Items.Count -ge 2) {
            $cmbContainer.Items[0] = 'MP4 · AAC 256k（兼容模式）'
            $cmbContainer.Items[1] = 'MKV · 保留原始流（推荐）'
        }
        $pnlHevc.Visible = $false
        $pnlAv1.Visible = $true
        $pnlAv1.BringToFront()
        $grpGrain.Text = 'AV1 · 胶片颗粒元数据 · 不重编码'
        Update-Av1Controls

        $cmbContainer.Enabled = $true
        $cmbSpeed.Enabled = $false
        $cmbBitrate.Enabled = $false
        $cmbFps.Enabled = $false
        $cmbDeint.Enabled = $false
        $cmbDeintMethod.Enabled = $false
        $chkCinematic.Enabled = $false
        $cmbFrameMode.Enabled = $false
        $chkUpload.Enabled = $false
        $cmbUploadBitrate.Enabled = $false
        $chkUploadHighMotion.Enabled = $false
        $btnUploadSubtitle.Enabled = $false
        $grpLut.Enabled = $false
        $frameHelp.Text = 'AV1 视频流不重编码，仅添加/替换胶片颗粒元数据；反交错、码率、LUT、画幅处理和上传版等重编码功能已禁用。'
        $btnStart.Text = '开始处理'
        return
    }

    # Keep the normal AV1 / HEVC startup path identical to the v4.2.1 stable baseline.
    # Only restore controls when we are actually leaving the no-reencode mode.
    if ($script:NoReencodeUiActive) {
        $script:NoReencodeUiActive = $false
        if ($cmbContainer.Items.Count -ge 2) {
            $cmbContainer.Items[0] = 'MP4 · AAC 256k（默认）'
            $cmbContainer.Items[1] = 'MKV · 保留原始流'
        }
        $cmbContainer.Enabled = $true
        $cmbSpeed.Enabled = $true
        $cmbBitrate.Enabled = $true
        $cmbDeint.Enabled = $true
        $chkCinematic.Enabled = $true
        $chkUpload.Enabled = $true
        $cmbUploadBitrate.Enabled = $chkUpload.Checked
        $btnUploadSubtitle.Enabled = $true
        $grpLut.Enabled = $true
        $btnStart.Text = '开始编码'
        Update-DeinterlaceUi
        Update-FramingUi
        Update-UploadHighMotionUi
        Set-LutUi
    }

    if (-not $script:ChangingCodec -and $newIndex -eq 0 -and $script:HardwareCapsReady -and -not $script:Av1Available) {
        $script:ChangingCodec = $true
        try { $cmbCodec.SelectedIndex = 1 }
        finally { $script:ChangingCodec = $false }
        Show-Info '当前 GPU / 驱动不支持 AV1 Main10 NVENC，已自动切换到 HEVC。'
        return
    }

    if (-not $script:ChangingCodec) {
        $script:ChangingCodec = $true
        try {
            if ($script:LastCodecIndex -ne $newIndex) {
                if ($cmbBitrate.Text) { $script:ModeBitrate[$script:LastCodecIndex] = $cmbBitrate.Text.Trim() }
                Load-BitrateChoices $newIndex
                $script:LastCodecIndex = $newIndex
            }
        } finally {
            $script:ChangingCodec = $false
        }
    }

    Update-SpeedChoices

    if ($newIndex -eq 0) {
        $pnlHevc.Visible = $false
        $pnlAv1.Visible = $true
        $pnlAv1.BringToFront()
        $grpGrain.Text = 'AV1 · 胶片颗粒元数据'
    } else {
        $pnlAv1.Visible = $false
        $pnlHevc.Visible = $true
        $pnlHevc.BringToFront()
        $grpGrain.Text = 'HEVC · 扫描胶片颗粒'
        if ($script:LastScannedGrainRoot -ne $txtGrainRoot.Text.Trim() -or $script:HevcGrainFiles.Count -eq 0) {
            Refresh-HevcGrainPlates
        }
    }
}

function Get-LutPathKey {
    param([string]$Path)
    if (-not $Path) { return '' }
    try { return ([System.IO.Path]::GetFullPath($Path)).ToLowerInvariant() }
    catch { return $Path.ToLowerInvariant() }
}

function Get-StudioLutRelativePath {
    param([string]$Path)
    if (-not $Path) { return '' }
    try {
        $baseUri = New-Object System.Uri(($LutRoot.TrimEnd('\') + '\'))
        $pathUri = New-Object System.Uri($Path)
        return [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
    } catch {
        return [System.IO.Path]::GetFileName($Path)
    }
}

function Get-StudioExpectedLutPreview {
    param([string]$LutPath)
    if (-not $LutPath) { return $null }
    try {
        $fi = Get-Item -LiteralPath $LutPath -ErrorAction Stop
        $relative = Get-StudioLutRelativePath $fi.FullName
        if ($relative.StartsWith('..\')) {
            $relativeDirectory = '_JUNCTIONS\' + $fi.Directory.Name
        } else {
            $relativeDirectory = Split-Path $relative -Parent
        }
        $previewDirectory = if (-not $relativeDirectory -or $relativeDirectory -eq '.') {
            $LutPreviewRoot
        } else {
            Join-Path $LutPreviewRoot $relativeDirectory
        }
        return Join-Path $previewDirectory ($fi.BaseName + '_preview.jpg')
    } catch {
        return $null
    }
}

function Read-StudioLutIndex {
    $result = @{}
    if (-not (Test-Path -LiteralPath $LutGalleryIndex)) { return $result }
    try {
        $raw = Get-Content -LiteralPath $LutGalleryIndex -Raw -Encoding UTF8
        if (-not $raw.Trim()) { return $result }
        foreach ($row in @($raw | ConvertFrom-Json)) {
            $path = [string]$row.LutPath
            if ($path) {
                $result[(Get-LutPathKey $path)] = $row
            }
            $relative = [string]$row.Relative
            if ($relative) {
                $relativeKey = 'REL|' + $relative.Replace('/', '\').ToLowerInvariant()
                $result[$relativeKey] = $row
            }
        }
    } catch {}
    return $result
}

function Get-StudioLutIndexEntry {
    param(
        [string]$LutPath,
        [hashtable]$IndexByPath
    )
    if (-not $LutPath -or -not $IndexByPath) { return $null }

    $key = Get-LutPathKey $LutPath
    if ($IndexByPath.ContainsKey($key)) {
        return $IndexByPath[$key]
    }

    $relative = Get-StudioLutRelativePath $LutPath
    if ($relative) {
        $relativeKey = 'REL|' + $relative.Replace('/', '\').ToLowerInvariant()
        if ($IndexByPath.ContainsKey($relativeKey)) {
            return $IndexByPath[$relativeKey]
        }
    }
    return $null
}

function Get-StudioLutPreviewPath {
    param(
        [string]$LutPath,
        [hashtable]$IndexByPath
    )
    if (-not $LutPath) { return $null }

    $expected = Get-StudioExpectedLutPreview $LutPath
    if ($expected -and (Test-Path -LiteralPath $expected -PathType Leaf)) {
        return (Get-Item -LiteralPath $expected).FullName
    }

    $indexEntry = Get-StudioLutIndexEntry $LutPath $IndexByPath
    if ($indexEntry) {
        $indexed = [string]$indexEntry.PreviewPath
        if ($indexed -and (Test-Path -LiteralPath $indexed -PathType Leaf)) {
            return (Get-Item -LiteralPath $indexed).FullName
        }
    }
    return $null
}

function Get-StudioLutThumbPath {
    param([string]$PreviewPath)
    if (-not $PreviewPath) { return $null }
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($PreviewPath.ToLowerInvariant())
        $hash = -join ($sha256.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') })
    } finally {
        $sha256.Dispose()
    }
    return Join-Path $LutGalleryThumbRoot ($hash + '.jpg')
}

function New-StudioLutThumbFile {
    param(
        [string]$SourcePath,
        [string]$DestinationPath
    )
    if (-not (Test-Path -LiteralPath $LutGalleryThumbRoot)) {
        [void](New-Item -ItemType Directory -Force -Path $LutGalleryThumbRoot)
    }
    $stream = [System.IO.File]::Open($SourcePath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $source = [System.Drawing.Image]::FromStream($stream, $true, $true)
        try {
            $bitmap = New-Object System.Drawing.Bitmap -ArgumentList 240, 135
            try {
                $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
                try {
                    $graphics.Clear([System.Drawing.Color]::Black)
                    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $ratio = [Math]::Min(240.0 / $source.Width, 135.0 / $source.Height)
                    $width = [int]($source.Width * $ratio)
                    $height = [int]($source.Height * $ratio)
                    $x = [int]((240 - $width) / 2)
                    $y = [int]((135 - $height) / 2)
                    $graphics.DrawImage($source, $x, $y, $width, $height)
                } finally {
                    $graphics.Dispose()
                }
                $bitmap.Save($DestinationPath, [System.Drawing.Imaging.ImageFormat]::Jpeg)
            } finally {
                $bitmap.Dispose()
            }
        } finally {
            $source.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
}

function Clear-StudioLutPreview {
    $picLutPreview.Image = $null
    if ($script:CurrentLutPreviewImage) {
        try { $script:CurrentLutPreviewImage.Dispose() } catch {}
        $script:CurrentLutPreviewImage = $null
    }
    $toolTip.SetToolTip($picLutPreview, '')
}

function Set-StudioLutPreview {
    param([string]$PreviewPath)
    Clear-StudioLutPreview
    if (-not $PreviewPath -or -not (Test-Path -LiteralPath $PreviewPath -PathType Leaf)) { return }
    try {
        $thumbPath = Get-StudioLutThumbPath $PreviewPath
        $rebuild = $true
        if ($thumbPath -and (Test-Path -LiteralPath $thumbPath -PathType Leaf)) {
            try {
                if ((Get-Item -LiteralPath $thumbPath).LastWriteTimeUtc -ge (Get-Item -LiteralPath $PreviewPath).LastWriteTimeUtc) {
                    $rebuild = $false
                }
            } catch {}
        }
        if ($rebuild) { New-StudioLutThumbFile $PreviewPath $thumbPath }

        $stream = [System.IO.File]::Open($thumbPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            $source = [System.Drawing.Image]::FromStream($stream, $true, $true)
            try { $script:CurrentLutPreviewImage = New-Object System.Drawing.Bitmap $source }
            finally { $source.Dispose() }
        } finally {
            $stream.Dispose()
        }
        $picLutPreview.Image = $script:CurrentLutPreviewImage
        $toolTip.SetToolTip($picLutPreview, $PreviewPath)
    } catch {
        Clear-StudioLutPreview
    }
}

function Refresh-RecentLuts {
    $script:LoadingRecentLuts = $true
    $cmbRecentLut.BeginUpdate()
    try {
        $script:RecentLuts = @()
        $indexByPath = Read-StudioLutIndex
        $seen = @{}
        $recentRows = @()
        $recentReadError = ''
        $recentSkippedErrors = 0
        if (Test-Path -LiteralPath $LutGalleryRecent) {
            try {
                $raw = Get-Content -LiteralPath $LutGalleryRecent -Raw -Encoding UTF8
                if ($raw.Trim()) {
                    $parsed = $raw | ConvertFrom-Json
                    $recentRows = @($parsed)
                    foreach ($row in $recentRows) {
                        try {
                        $lutPath = [string]$row.LutPath
                        if (-not $lutPath) { continue }

                        # Match Gallery behavior: prefer the current path from its
                        # index, then fall back to the path stored in Recent.
                        $indexEntry = Get-StudioLutIndexEntry $lutPath $indexByPath
                        if ($indexEntry -and [string]$indexEntry.LutPath) {
                            $indexedLutPath = [string]$indexEntry.LutPath
                            if (Test-Path -LiteralPath $indexedLutPath -PathType Leaf) {
                                $lutPath = (Get-Item -LiteralPath $indexedLutPath).FullName
                            }
                        }

                        if (-not (Test-Path -LiteralPath $lutPath -PathType Leaf)) { continue }
                        if ([System.IO.Path]::GetExtension($lutPath) -ine '.cube') { continue }
                        $key = Get-LutPathKey $lutPath
                        if ($seen.ContainsKey($key)) { continue }
                        $previewPath = Get-StudioLutPreviewPath $lutPath $indexByPath

                        $relative = Get-StudioLutRelativePath $lutPath
                        $indexEntry = Get-StudioLutIndexEntry $lutPath $indexByPath
                        if ($indexEntry -and [string]$indexEntry.Relative) {
                            $relative = [string]$indexEntry.Relative
                        }
                        $seen[$key] = $true
                        $script:RecentLuts += [pscustomobject]@{
                            DisplayName = $relative
                            LutPath = (Get-Item -LiteralPath $lutPath).FullName
                            PreviewPath = $previewPath
                        }
                        if ($script:RecentLuts.Count -ge 25) { break }
                        } catch {
                            $recentSkippedErrors++
                        }
                    }
                }
            } catch {
                $recentReadError = $_.Exception.Message
            }
        }

        $cmbRecentLut.Items.Clear()
        if ($script:RecentLuts.Count -gt 0) {
            [void]$cmbRecentLut.Items.Add('— 选择最近使用的 LUT —')
            foreach ($entry in $script:RecentLuts) { [void]$cmbRecentLut.Items.Add([string]$entry.DisplayName) }
            $cmbRecentLut.Enabled = $true
        } else {
            [void]$cmbRecentLut.Items.Add('暂无 LUT 图库最近使用记录')
            $cmbRecentLut.Enabled = $false
        }

        $selectedIndex = 0
        if ($script:SelectedLutPath) {
            $selectedKey = Get-LutPathKey $script:SelectedLutPath
            for ($i = 0; $i -lt $script:RecentLuts.Count; $i++) {
                if ((Get-LutPathKey ([string]$script:RecentLuts[$i].LutPath)) -eq $selectedKey) {
                    $selectedIndex = $i + 1
                    break
                }
            }
        }
        $cmbRecentLut.SelectedIndex = $selectedIndex

        if ($recentReadError) {
            Append-LogText ("[LUT Recent] 读取失败：$recentReadError`r`n文件：$LutGalleryRecent`r`n")
        } elseif ($recentSkippedErrors -gt 0) {
            Append-LogText ("[LUT Recent] 已跳过 $recentSkippedErrors 条异常记录，其余记录仍正常载入。`r`n")
        } elseif ($recentRows.Count -gt 0 -and $script:RecentLuts.Count -eq 0) {
            Append-LogText ("[LUT Recent] 已读取 $($recentRows.Count) 条记录，但没有找到仍存在的 .cube 文件。`r`n文件：$LutGalleryRecent`r`n")
        }
    } finally {
        $cmbRecentLut.EndUpdate()
        $script:LoadingRecentLuts = $false
    }
}

function Refresh-FavoriteLuts {
    $script:LoadingFavoriteLuts = $true
    $cmbFavoriteLut.BeginUpdate()
    try {
        $script:FavoriteLuts = @()
        $indexByPath = Read-StudioLutIndex
        $seen = @{}
        $favoriteRows = @()
        $favoriteReadError = ''
        $favoriteSkippedErrors = 0
        if (Test-Path -LiteralPath $LutGalleryFavorites) {
            try {
                $raw = Get-Content -LiteralPath $LutGalleryFavorites -Raw -Encoding UTF8
                if ($raw.Trim()) {
                    $parsed = $raw | ConvertFrom-Json
                    $favoriteRows = @($parsed)
                    foreach ($row in $favoriteRows) {
                        try {
                            $lutPath = if ($row -is [string]) { [string]$row } else { [string]$row.LutPath }
                            if (-not $lutPath) { continue }

                            # Match Gallery behavior and support both its current
                            # object records and older plain-string favorites.
                            $indexEntry = Get-StudioLutIndexEntry $lutPath $indexByPath
                            if ($indexEntry -and [string]$indexEntry.LutPath) {
                                $indexedLutPath = [string]$indexEntry.LutPath
                                if (Test-Path -LiteralPath $indexedLutPath -PathType Leaf) {
                                    $lutPath = (Get-Item -LiteralPath $indexedLutPath).FullName
                                }
                            }

                            if (-not (Test-Path -LiteralPath $lutPath -PathType Leaf)) { continue }
                            if ([System.IO.Path]::GetExtension($lutPath) -ine '.cube') { continue }
                            $key = Get-LutPathKey $lutPath
                            if ($seen.ContainsKey($key)) { continue }
                            $previewPath = Get-StudioLutPreviewPath $lutPath $indexByPath

                            $relative = Get-StudioLutRelativePath $lutPath
                            $indexEntry = Get-StudioLutIndexEntry $lutPath $indexByPath
                            if ($indexEntry -and [string]$indexEntry.Relative) {
                                $relative = [string]$indexEntry.Relative
                            }
                            $seen[$key] = $true
                            $script:FavoriteLuts += [pscustomobject]@{
                                DisplayName = $relative
                                LutPath = (Get-Item -LiteralPath $lutPath).FullName
                                PreviewPath = $previewPath
                            }
                            if ($script:FavoriteLuts.Count -ge 25) { break }
                        } catch {
                            $favoriteSkippedErrors++
                        }
                    }
                }
            } catch {
                $favoriteReadError = $_.Exception.Message
            }
        }

        $cmbFavoriteLut.Items.Clear()
        if ($script:FavoriteLuts.Count -gt 0) {
            [void]$cmbFavoriteLut.Items.Add('— 选择我的最爱 LUT —')
            foreach ($entry in $script:FavoriteLuts) { [void]$cmbFavoriteLut.Items.Add([string]$entry.DisplayName) }
            $cmbFavoriteLut.Enabled = $true
        } else {
            [void]$cmbFavoriteLut.Items.Add('暂无 LUT 图库我的最爱')
            $cmbFavoriteLut.Enabled = $false
        }

        $selectedIndex = 0
        if ($script:SelectedLutPath) {
            $selectedKey = Get-LutPathKey $script:SelectedLutPath
            for ($i = 0; $i -lt $script:FavoriteLuts.Count; $i++) {
                if ((Get-LutPathKey ([string]$script:FavoriteLuts[$i].LutPath)) -eq $selectedKey) {
                    $selectedIndex = $i + 1
                    break
                }
            }
        }
        $cmbFavoriteLut.SelectedIndex = $selectedIndex

        if ($favoriteReadError) {
            Append-LogText ("[LUT Favorites] 读取失败：$favoriteReadError`r`n文件：$LutGalleryFavorites`r`n")
        } elseif ($favoriteSkippedErrors -gt 0) {
            Append-LogText ("[LUT Favorites] 已跳过 $favoriteSkippedErrors 条异常记录，其余记录仍正常载入。`r`n")
        } elseif ($favoriteRows.Count -gt 0 -and $script:FavoriteLuts.Count -eq 0) {
            Append-LogText ("[LUT Favorites] 已读取 $($favoriteRows.Count) 条记录，但没有找到仍存在的 .cube 文件。`r`n文件：$LutGalleryFavorites`r`n")
        }
    } finally {
        $cmbFavoriteLut.EndUpdate()
        $script:LoadingFavoriteLuts = $false
    }
}

function Set-LutUi {
    if ($script:SelectedLutPath) {
        $relative = [System.IO.Path]::GetFileName($script:SelectedLutPath)
        try {
            $rootFull = [System.IO.Path]::GetFullPath($LutRoot).TrimEnd('\') + '\'
            $pathFull = [System.IO.Path]::GetFullPath($script:SelectedLutPath)
            if ($pathFull.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
                $relative = $pathFull.Substring($rootFull.Length)
            }
        } catch {}
        $lblSelectedLut.Text = $relative
        $lblSelectedLut.ForeColor = [System.Drawing.SystemColors]::ControlText
        $toolTip.SetToolTip($lblSelectedLut, $script:SelectedLutPath)
        $indexByPath = Read-StudioLutIndex
        Set-StudioLutPreview (Get-StudioLutPreviewPath $script:SelectedLutPath $indexByPath)
    } else {
        $lblSelectedLut.Text = '未选择 LUT'
        $lblSelectedLut.ForeColor = $ColorMuted
        $toolTip.SetToolTip($lblSelectedLut, '')
        Clear-StudioLutPreview
    }
    $trackLutStrength.Enabled = $chkLut.Checked
    $lblLutStrength.Enabled = $chkLut.Checked
    $lblLutStrengthTitle.Enabled = $chkLut.Checked
}

function Register-FavoriteLutRecentUse {
    param([string]$LutPath)
    $script:LastLutRecentRegisterError = ''
    if (-not $LutPath -or -not (Test-Path -LiteralPath $LutPath -PathType Leaf)) {
        $script:LastLutRecentRegisterError = "LUT 文件不存在：$LutPath"
        return $false
    }
    if (-not (Test-Path -LiteralPath $LutSelector -PathType Leaf)) {
        $script:LastLutRecentRegisterError = "找不到 LUT Gallery：$LutSelector"
        return $false
    }

    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'powershell.exe'
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardError = $true
        $psi.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $LutSelector + '" -LutRoot "' + $LutRoot + '" -PreviewRoot "' + $LutPreviewRoot + '" -RecordRecentPath "' + $LutPath + '"'
        $proc = [System.Diagnostics.Process]::Start($psi)
        $galleryError = $proc.StandardError.ReadToEnd()
        $proc.WaitForExit()
        $rc = $proc.ExitCode
        $proc.Dispose()
        if ($rc -eq 0) { return $true }
        $script:LastLutRecentRegisterError = $galleryError.Trim()
        if (-not $script:LastLutRecentRegisterError) {
            $script:LastLutRecentRegisterError = "Gallery 无界面登记返回代码：$rc"
        }
        return $false
    } catch {
        $script:LastLutRecentRegisterError = $_.Exception.ToString()
        return $false
    }
}

function Open-LutGallery {
    if (-not (Test-Path -LiteralPath $LutSelector)) {
        Show-Error "找不到 LUT 图库：`r`n$LutSelector`r`n`r`n请保持 Utils 与 _LUT_Tools 文件夹的相对位置不变。"
        return
    }
    if (-not (Test-Path -LiteralPath $LutRoot)) {
        Show-Error "LUT 根目录不存在：`r`n$LutRoot"
        return
    }

    $pick = Join-Path ([System.IO.Path]::GetTempPath()) ('FilmGrainStudio_LUT_' + [guid]::NewGuid().ToString('N') + '.txt')
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'powershell.exe'
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardError = $true
        $psi.Arguments = '-NoProfile -ExecutionPolicy Bypass -STA -File "' + $LutSelector + '" -LutRoot "' + $LutRoot + '" -PreviewRoot "' + $LutPreviewRoot + '" -OutputFile "' + $pick + '"'
        $proc = [System.Diagnostics.Process]::Start($psi)
        $galleryError = $proc.StandardError.ReadToEnd()
        $proc.WaitForExit()
        $rc = $proc.ExitCode
        $proc.Dispose()

        if ($rc -eq 0 -and (Test-Path -LiteralPath $pick)) {
            $selected = [System.IO.File]::ReadAllText($pick, [System.Text.Encoding]::UTF8).Trim()
            if ($selected) {
                $script:SelectedLutPath = $selected
                $script:SelectedLutSource = 'Gallery'
                $chkLut.Checked = $true
            }
        } elseif ($rc -eq 10) {
            $script:SelectedLutPath = $null
            $script:SelectedLutSource = 'None'
            $chkLut.Checked = $false
        } elseif ($rc -ne 11) {
            $detail = $galleryError.Trim()
            if ($detail.Length -gt 2000) { $detail = $detail.Substring(0, 2000) }
            if ($detail) { Show-Error "LUT 图库启动失败，返回代码：$rc`r`n`r`n$detail" }
            else { Show-Error "LUT 图库启动失败，返回代码：$rc；未返回错误详情。" }
        }
    } catch {
        Show-Error ("打开 LUT 图库失败：`r`n" + $_.Exception.Message)
    } finally {
        if (Test-Path -LiteralPath $pick) { Remove-Item -LiteralPath $pick -Force -ErrorAction SilentlyContinue }
        Refresh-RecentLuts
        Refresh-FavoriteLuts
        Set-LutUi
    }
}

function Set-RunMetrics {
    param(
        [string]$FpsText,
        [string]$SpeedText,
        [double]$ElapsedSeconds
    )

    $speedValue = 0.0
    if (-not [double]::TryParse($SpeedText, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$speedValue)) {
        return
    }

    $metricText = 'fps: ' + $FpsText + '   speed: ' + $SpeedText + 'x'
    if ($script:CurrentDurationSeconds -gt 0) {
        $ratio = [Math]::Max(0.0, [Math]::Min(1.0, $ElapsedSeconds / $script:CurrentDurationSeconds))
        $progress.MarqueeAnimationSpeed = 0
        $progress.Style = [System.Windows.Forms.ProgressBarStyle]::Continuous
        $progress.Maximum = 1000
        $progress.Value = [int]($ratio * 1000)
        if ($speedValue -gt 0.0) {
            $etaSeconds = [Math]::Max(0.0, ($script:CurrentDurationSeconds - $ElapsedSeconds) / $speedValue)
            $etaText = [TimeSpan]::FromSeconds($etaSeconds).ToString('hh\:mm\:ss')
            $metricText += '   ETA: ' + $etaText
        }
    }
    $lblRunMetric.Text = $metricText
}

function Append-LogText {
    param([string]$Text)
    if (-not $Text) { return }
    $display = [System.Text.RegularExpressions.Regex]::Replace($Text, "`r(?!`n)", "`r`n")
    $rtbLog.AppendText($display)
    if ($rtbLog.TextLength -gt 2000000) {
        $rtbLog.Select(0, 500000)
        $rtbLog.SelectedText = ''
    }
    $rtbLog.SelectionStart = $rtbLog.TextLength
    $rtbLog.ScrollToCaret()

    $parseText = $script:LogParseTail + $display
    $currentSegment = $parseText
    $inputs = [System.Text.RegularExpressions.Regex]::Matches($parseText, 'Input\s*:\s*"([^"]+)"')
    if ($inputs.Count -gt 0) {
        $lastInput = $inputs[$inputs.Count - 1]
        $currentSegment = $parseText.Substring($lastInput.Index)
        $inputPath = $lastInput.Groups[1].Value
        if ($inputPath -ne $script:CurrentInputPath) {
            $script:CurrentInputPath = $inputPath
            $script:CurrentDurationSeconds = 0.0
            if ($script:RunningProcess) {
                $progress.Style = [System.Windows.Forms.ProgressBarStyle]::Marquee
                $progress.MarqueeAnimationSpeed = 25
            }
        }
        $currentName = [System.IO.Path]::GetFileName($inputPath)
        $lblStatus.Text = '正在处理 · ' + $currentName
    }

    $durations = [System.Text.RegularExpressions.Regex]::Matches($currentSegment, 'Duration\s*:\s*([0-9.]+)\s*sec')
    if ($durations.Count -gt 0) {
        $durationValue = 0.0
        $durationText = $durations[$durations.Count - 1].Groups[1].Value
        if ([double]::TryParse($durationText, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$durationValue)) {
            $script:CurrentDurationSeconds = $durationValue
        }
    }

    # Studio mode asks FFmpeg for newline-delimited -progress records. These
    # remain parseable even when the normal -stats line uses carriage-return
    # updates or is buffered by CMD redirection. Keep the legacy parser below
    # as a fallback for older bridge scripts.
    $structuredMetricApplied = $false
    $progressBlocks = [System.Text.RegularExpressions.Regex]::Matches(
        $currentSegment,
        '(?ms)^frame=.*?^progress=(?:continue|end)\r?$'
    )
    for ($i = $progressBlocks.Count - 1; $i -ge 0; $i--) {
        $block = $progressBlocks[$i].Value
        $fpsMatch = [System.Text.RegularExpressions.Regex]::Match($block, '(?m)^fps=([0-9.]+)\r?$')
        $timeMatch = [System.Text.RegularExpressions.Regex]::Match($block, '(?m)^out_time=([0-9]+):([0-9]{2}):([0-9]{2}(?:\.[0-9]+)?)\r?$')
        $speedMatch = [System.Text.RegularExpressions.Regex]::Match($block, '(?m)^speed=\s*([0-9.]+)x\r?$')
        if ($fpsMatch.Success -and $timeMatch.Success -and $speedMatch.Success) {
            $secondsValue = 0.0
            [void][double]::TryParse($timeMatch.Groups[3].Value, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$secondsValue)
            $elapsed = ([int]$timeMatch.Groups[1].Value * 3600) + ([int]$timeMatch.Groups[2].Value * 60) + $secondsValue
            Set-RunMetrics $fpsMatch.Groups[1].Value $speedMatch.Groups[1].Value $elapsed
            $structuredMetricApplied = $true
            break
        }
    }

    if (-not $structuredMetricApplied) {
        $metrics = [System.Text.RegularExpressions.Regex]::Matches($currentSegment, 'fps=\s*([0-9.]+).*?time=\s*([0-9]{2}):([0-9]{2}):([0-9]{2}(?:\.[0-9]+)?).*?speed=\s*([0-9.]+)x')
        if ($metrics.Count -gt 0) {
            $last = $metrics[$metrics.Count - 1]
            $secondsValue = 0.0
            [void][double]::TryParse($last.Groups[4].Value, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$secondsValue)
            $elapsed = ([int]$last.Groups[2].Value * 3600) + ([int]$last.Groups[3].Value * 60) + $secondsValue
            Set-RunMetrics $last.Groups[1].Value $last.Groups[5].Value $elapsed
        }
    }
    $stages = [System.Text.RegularExpressions.Regex]::Matches($parseText, '\[(\d+)/(\d+)\]\s*([^\r\n]+)')
    if ($stages.Count -gt 0) {
        $lastStage = $stages[$stages.Count - 1]
        $lblRunStage.Text = '阶段 ' + $lastStage.Groups[1].Value + '/' + $lastStage.Groups[2].Value + ' · ' + $lastStage.Groups[3].Value.Trim()
    }
    if ($parseText.Length -gt 4096) {
        $script:LogParseTail = $parseText.Substring($parseText.Length - 4096)
    } else {
        $script:LogParseTail = $parseText
    }
}

function Read-ProcessOutput {
    if (-not $script:RunningProcess) { return }

    # ReadLineAsync keeps both redirected pipes draining without blocking the
    # WinForms thread. FFmpeg -stats (CR) and -progress (LF) are both emitted
    # as complete lines by StreamReader, so metrics arrive during encoding.
    $stdoutText = New-Object System.Text.StringBuilder
    $stdoutLines = 0
    while ($script:OutputReadTask -and $script:OutputReadTask.IsCompleted -and $stdoutLines -lt 500) {
        try {
            $line = $script:OutputReadTask.Result
        } catch {
            $line = $null
            [void]$stdoutText.Append('[Studio] 读取后台标准输出失败：' + $_.Exception.Message + "`r`n")
        }

        if ($null -eq $line) {
            $script:OutputReadTask = $null
            $script:OutputStreamClosed = $true
            break
        }

        [void]$stdoutText.Append($line)
        [void]$stdoutText.Append("`r`n")
        $stdoutLines++
        try {
            $script:OutputReadTask = $script:RunningProcess.StandardOutput.ReadLineAsync()
        } catch {
            $script:OutputReadTask = $null
            $script:OutputStreamClosed = $true
            [void]$stdoutText.Append('[Studio] 无法继续读取后台标准输出：' + $_.Exception.Message + "`r`n")
        }
    }
    if ($stdoutText.Length -gt 0) { Append-LogText ($stdoutText.ToString()) }

    $stderrText = New-Object System.Text.StringBuilder
    $stderrLines = 0
    while ($script:ErrorReadTask -and $script:ErrorReadTask.IsCompleted -and $stderrLines -lt 500) {
        try {
            $line = $script:ErrorReadTask.Result
        } catch {
            $line = $null
            [void]$stderrText.Append('[Studio] 读取后台错误输出失败：' + $_.Exception.Message + "`r`n")
        }

        if ($null -eq $line) {
            $script:ErrorReadTask = $null
            $script:ErrorStreamClosed = $true
            break
        }

        [void]$stderrText.Append($line)
        [void]$stderrText.Append("`r`n")
        $stderrLines++
        try {
            $script:ErrorReadTask = $script:RunningProcess.StandardError.ReadLineAsync()
        } catch {
            $script:ErrorReadTask = $null
            $script:ErrorStreamClosed = $true
            [void]$stderrText.Append('[Studio] 无法继续读取后台错误输出：' + $_.Exception.Message + "`r`n")
        }
    }
    if ($stderrText.Length -gt 0) { Append-LogText ($stderrText.ToString()) }
}

function Set-RunningState {
    param([bool]$Running)
    $main.Enabled = -not $Running
    $btnStart.Enabled = -not $Running
    $btnCancel.Enabled = $Running
    $btnClearLog.Enabled = -not $Running
    $btnConfig.Enabled = -not $Running
    if ($Running) {
        $progress.Style = [System.Windows.Forms.ProgressBarStyle]::Marquee
        $progress.MarqueeAnimationSpeed = 25
    } else {
        $progress.MarqueeAnimationSpeed = 0
        $progress.Style = [System.Windows.Forms.ProgressBarStyle]::Blocks
        $progress.Maximum = 1000
        $progress.Value = 0
    }
}

function Complete-Run {
    param([int]$ExitCode)
    if ($script:RunCompletionHandled) { return }
    $script:RunCompletionHandled = $true
    Read-ProcessOutput
    Set-RunningState $false

    if ($script:RunWasCancelled) {
        $lblStatus.Text = '任务已取消'
        $lblStatus.ForeColor = $ColorError
        $lblRunStage.Text = '已取消 · 可能保留部分输出或 AV1 临时目录'
    } elseif ($ExitCode -eq 0) {
        $lblStatus.Text = '全部任务已完成'
        $lblStatus.ForeColor = $ColorSuccess
        $lblRunStage.Text = '完成 · 请查看源文件目录中的输出'
        $progress.Value = $progress.Maximum
    } else {
        $lblStatus.Text = "任务结束，但存在错误（代码 $ExitCode）"
        $lblStatus.ForeColor = $ColorError
        $lblRunStage.Text = '完成但有错误 · 请查看下方日志'
    }

    if ($script:RunningProcess) {
        try { $script:RunningProcess.Dispose() } catch {}
    }
    $script:RunningProcess = $null
    $script:OutputReadTask = $null
    $script:ErrorReadTask = $null
    $script:OutputStreamClosed = $true
    $script:ErrorStreamClosed = $true
}

function Quote-CmdArgument {
    param([string]$Value)
    return '"' + $Value.Replace('"', '""') + '"'
}

function Start-NoReencodeProcessing {
    $paths = @(Get-InputPaths)
    if ($paths.Count -ne 1) {
        Show-Error '“AV1 不重编码 · 添加/替换胶片颗粒”当前仅支持单个 AV1 输入文件。'
        return
    }
    $path = [string]$paths[0]
    $key = $path.ToLowerInvariant()
    if (-not $script:ProbeVideoMeta.ContainsKey($key) -or ([string]$script:ProbeVideoMeta[$key].codec_name).ToLowerInvariant() -ne 'av1') {
        Show-Error '当前文件未确认是 AV1，请重新选择文件并等待媒体信息读取完成。'
        return
    }
    if (-not (Test-Path -LiteralPath $NoReencodeBat -PathType Leaf)) {
        Show-Error "找不到 AV1 无重编码工具：`r`n$NoReencodeBat"
        return
    }
    if (-not (Test-Path -LiteralPath $Grav1synth -PathType Leaf)) {
        Show-Error "找不到 grav1synth：`r`n$Grav1synth"
        return
    }

    $selectedAv1GrainTable = ''
    if ($cmbAv1Method.SelectedIndex -eq 2) {
        if ($script:Av1GrainTableFiles.Count -eq 0 -or $cmbAv1GrainTable.SelectedIndex -lt 0 -or $cmbAv1GrainTable.SelectedIndex -ge $script:Av1GrainTableFiles.Count) {
            Show-Error '当前没有可用的 Grain Table。请将 .tbl / .txt Grain Table 放入项目根目录 _AV1_Grain_Tables 的对应分辨率目录后点击刷新。'
            return
        }
        $selectedAv1GrainTable = [string]$script:Av1GrainTableFiles[$cmbAv1GrainTable.SelectedIndex]
        if (-not (Test-Path -LiteralPath $selectedAv1GrainTable -PathType Leaf)) {
            Refresh-Av1GrainTables
            Show-Error '所选 Grain Table 已不存在，请刷新后重新选择。'
            return
        }
    }

    $script:RunWasCancelled = $false
    $script:RunCompletionHandled = $false
    $script:OutputReadTask = $null
    $script:ErrorReadTask = $null
    $script:OutputStreamClosed = $true
    $script:ErrorStreamClosed = $true
    $script:LogParseTail = ''
    $script:CurrentInputPath = ''
    $script:CurrentDurationSeconds = 0.0
    $rtbLog.Clear()
    $lblStatus.ForeColor = [System.Drawing.SystemColors]::ControlText
    $lblRunStage.Text = '正在启动 AV1 胶片颗粒无重编码处理…'
    $lblRunMetric.Text = '视频编码：NONE · stream copy'
    Append-LogText ("Film Grain Studio`r`n" + ('=' * 68) + "`r`n")
    Append-LogText ("任务文件数：1  ·  模式：AV1 胶片颗粒添加/替换 · 视频不重编码`r`n`r`n")

    $cmdPath = $env:ComSpec
    if (-not $cmdPath) { $cmdPath = Join-Path $env:SystemRoot 'System32\cmd.exe' }
    # The Studio log is read through redirected pipes. Force CMD and the
    # .NET StreamReaders to the same UTF-8 code page so Chinese file names
    # and status text cannot fall back to the system OEM code page.
    $inner = 'chcp 65001 >nul & call ' + (Quote-CmdArgument $NoReencodeBat) + ' ' + (Quote-CmdArgument $path) + ' 2>&1'

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $cmdPath
    $psi.Arguments = '/d /s /c "' + $inner + '"'
    $psi.WorkingDirectory = $ScriptRoot
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $utf8Log = New-Object System.Text.UTF8Encoding($false)
    $psi.StandardOutputEncoding = $utf8Log
    $psi.StandardErrorEncoding = $utf8Log

    $envs = $psi.EnvironmentVariables
    $envs['FG_STUDIO_MODE'] = '1'
    $envs['FG_TOOL_NO_PAUSE'] = '1'
    $envs['FG_CONTAINER'] = if ($cmbContainer.SelectedIndex -eq 0) { 'MP4' } else { 'MKV' }
    $av1Modes = @('PRESET', 'ISO', 'TABLE')
    $envs['FG_AV1_GRAIN_MODE'] = $av1Modes[$cmbAv1Method.SelectedIndex]
    $envs['FG_AV1_FORMAT'] = [string]($cmbAv1Format.SelectedIndex + 1)
    $envs['FG_AV1_STOCK'] = [string]($cmbAv1Stock.SelectedIndex + 1)
    $envs['FG_AV1_ISO'] = [string][int]$numIso.Value
    $envs['FG_AV1_CHROMA'] = if ($chkChroma.Checked) { '1' } else { '0' }
    if ($selectedAv1GrainTable) { $envs['FG_AV1_GRAIN_TABLE'] = $selectedAv1GrainTable }
    else { [void]$envs.Remove('FG_AV1_GRAIN_TABLE') }

    if ($script:Av1GrainCache.ContainsKey($key)) {
        $grainState = [string]$script:Av1GrainCache[$key]
        if ($grainState -eq 'AV1 胶片颗粒：无') {
            $envs['FG_AV1_SOURCE_GRAIN_ACTION'] = 'ADDED'
        } elseif ($grainState -eq 'AV1 胶片颗粒：亮度' -or $grainState -eq 'AV1 胶片颗粒：亮度 + 色度') {
            $envs['FG_AV1_SOURCE_GRAIN_ACTION'] = 'REPLACED'
        }
    }

    try {
        $proc = New-Object System.Diagnostics.Process
        $proc.StartInfo = $psi
        if (-not $proc.Start()) { throw '无法启动 AV1 无重编码处理。' }
        $script:RunningProcess = $proc
        $script:OutputStreamClosed = $false
        $script:ErrorStreamClosed = $false
        $script:OutputReadTask = $proc.StandardOutput.ReadLineAsync()
        $script:ErrorReadTask = $proc.StandardError.ReadLineAsync()
        Set-RunningState $true
        $lblStatus.Text = 'AV1 胶片颗粒无重编码任务正在运行'
        $pollTimer.Start()
    } catch {
        Set-RunningState $false
        if ($proc) { try { $proc.Dispose() } catch {} }
        $script:RunningProcess = $null
        $script:OutputReadTask = $null
        $script:ErrorReadTask = $null
        $script:OutputStreamClosed = $true
        $script:ErrorStreamClosed = $true
        Show-Error ("启动 AV1 无重编码任务失败：`r`n" + $_.Exception.Message)
    }
}

function Start-Encoding {
    if ($cmbCodec.SelectedIndex -eq 2) {
        Start-NoReencodeProcessing
        return
    }

    if (-not (Test-Path -LiteralPath $CoreBat)) {
        Show-Error "找不到 Studio 桥接核心：`r`n$CoreBat"
        return
    }

    if (-not $script:HardwareCapsReady) {
        Initialize-HardwareCaps
        $script:FFmpegVersionOverride = ''
        Update-HardwareProfileUi
        Update-CodecUi
    }

    $paths = @(Get-InputPaths)
    if ($paths.Count -eq 0) {
        Show-Info '请先添加至少一个视频文件。'
        return
    }

    $bitrate = 0L
    if (-not [long]::TryParse($cmbBitrate.Text.Trim(), [ref]$bitrate) -or $bitrate -le 10 -or $bitrate -gt 500000000) {
        Show-Error '视频码率必须是大于 10 且不超过 500000000 的整数（单位 kbps）。'
        return
    }
    $maxrate = [long]($bitrate * 2)
    $bufsize = [long]($bitrate * 4)

    if ($chkLut.Checked) {
        if (-not $script:SelectedLutPath) {
            Show-Info '已启用 LUT，但尚未选择 LUT。请先打开 LUT Gallery。'
            return
        }
        if (-not (Test-Path -LiteralPath $script:SelectedLutPath)) {
            Show-Error "所选 LUT 已不存在：`r`n$script:SelectedLutPath"
            return
        }
    }

    $mode = if ($cmbCodec.SelectedIndex -eq 0) { 'AV1' } else { 'HEVC' }
    if ($script:HardwareCapsReady -and $mode -eq 'AV1' -and -not $script:Av1Available) {
        Show-Error '当前 GPU / 驱动不支持 AV1 Main10 NVENC。请使用自动选择的 HEVC 模式。'
        return
    }
    if ($script:HardwareCapsReady -and $mode -eq 'HEVC' -and -not $script:HevcAvailable) {
        Show-Error '当前 GPU / 驱动不支持本项目所需的 HEVC Main10 NVENC + Vulkan 路径。'
        return
    }
    if ($cmbSpeed.SelectedIndex -eq 2 -and ($mode -ne 'AV1' -or -not $script:Av1UhqAvailable)) {
        Show-Error '当前 GPU / 驱动 / FFmpeg 不支持 AV1 UHQ，请选择 FAST 或 Standard。'
        return
    }
    $selectedAv1GrainTable = ''
    if ($mode -eq 'AV1' -and $cmbAv1Method.SelectedIndex -eq 2) {
        if ($script:Av1GrainTableFiles.Count -eq 0 -or $cmbAv1GrainTable.SelectedIndex -lt 0 -or $cmbAv1GrainTable.SelectedIndex -ge $script:Av1GrainTableFiles.Count) {
            Show-Error '当前没有可用的 Grain Table。请将 .tbl / .txt Grain Table 放入项目根目录 _AV1_Grain_Tables 的对应分辨率目录后点击刷新。'
            return
        }
        $selectedAv1GrainTable = [string]$script:Av1GrainTableFiles[$cmbAv1GrainTable.SelectedIndex]
        if (-not (Test-Path -LiteralPath $selectedAv1GrainTable -PathType Leaf)) {
            Refresh-Av1GrainTables
            Show-Error '所选 Grain Table 已不存在，请刷新后重新选择。'
            return
        }
    }

    $selectedGrainPath = $null
    if ($mode -eq 'HEVC') {
        $grainRoot = $txtGrainRoot.Text.Trim()
        if (-not (Test-Path -LiteralPath $grainRoot -PathType Container)) {
            Show-Error "HEVC 颗粒根目录不存在：`r`n$grainRoot"
            return
        }
        if ($script:LastScannedGrainRoot -ne $grainRoot -or $script:HevcGrainFiles.Count -eq 0) {
            Refresh-HevcGrainPlates
        }
        if ($script:HevcGrainFiles.Count -eq 0 -or $cmbHevcPlate.SelectedIndex -lt 0 -or $cmbHevcPlate.SelectedIndex -ge $script:HevcGrainFiles.Count) {
            Show-Error "所选根目录中没有可用的 .mov 扫描颗粒片：`r`n$grainRoot"
            return
        }
        $selectedGrainPath = [string]$script:HevcGrainFiles[$cmbHevcPlate.SelectedIndex]
        if (-not (Test-Path -LiteralPath $selectedGrainPath -PathType Leaf)) {
            Refresh-HevcGrainPlates
            Show-Error "所选扫描颗粒片已不存在，请刷新后重新选择。"
            return
        }
    }

    $script:RunWasCancelled = $false
    $script:RunCompletionHandled = $false
    $script:OutputReadTask = $null
    $script:ErrorReadTask = $null
    $script:OutputStreamClosed = $true
    $script:ErrorStreamClosed = $true
    $script:LogParseTail = ''
    $script:CurrentInputPath = ''
    $script:CurrentDurationSeconds = 0.0
    $rtbLog.Clear()
    $lblStatus.ForeColor = [System.Drawing.SystemColors]::ControlText
    $lblRunStage.Text = '正在启动 Studio Bridge…'
    $lblRunMetric.Text = 'fps: —   speed: —'
    Append-LogText ("Film Grain Studio`r`n" + ('=' * 68) + "`r`n")
    Append-LogText ("任务文件数：$($paths.Count)  ·  模式：$mode  ·  码率：$bitrate kbps`r`n")
    Append-LogText ("GUI 码率请求：b:v ${bitrate}k  ·  maxrate ${maxrate}k  ·  bufsize ${bufsize}k`r`n`r`n")

    # Studio normally treats Recent and Favorites as read-only. Only a LUT
    # selected from Favorites is registered once, through Gallery itself,
    # when the user actually clicks Start Encoding.
    if ($chkLut.Checked -and $script:SelectedLutSource -eq 'Favorite') {
        if (Register-FavoriteLutRecentUse $script:SelectedLutPath) {
            Append-LogText "[LUT Recent] 已由 LUT Gallery 登记本次【我的最爱】选择。`r`n"
            $script:SelectedLutSource = 'Recent'
            Refresh-RecentLuts
        } else {
            $detail = $script:LastLutRecentRegisterError
            if (-not $detail) { $detail = '没有返回具体错误。' }
            Append-LogText ("[LUT Recent] Gallery 登记失败：$detail`r`n")
        }
        Append-LogText "`r`n"
    }

    $cmdPath = $env:ComSpec
    if (-not $cmdPath) { $cmdPath = Join-Path $env:SystemRoot 'System32\cmd.exe' }

    $quotedInputs = @()
    foreach ($path in $paths) { $quotedInputs += (Quote-CmdArgument $path) }
    # Keep the hidden CMD code page and .NET redirected readers aligned.
    # This fixes mojibake in the GUI log for Chinese paths/status messages,
    # including the existing Grain Table branch.
    $inner = 'chcp 65001 >nul & call ' + (Quote-CmdArgument $CoreBat) + ' ' + ($quotedInputs -join ' ') + ' 2>&1'

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $cmdPath
    $psi.Arguments = '/d /s /c "' + $inner + '"'
    $psi.WorkingDirectory = $ScriptRoot
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $utf8Log = New-Object System.Text.UTF8Encoding($false)
    $psi.StandardOutputEncoding = $utf8Log
    $psi.StandardErrorEncoding = $utf8Log

    $envs = $psi.EnvironmentVariables
    $envs['FG_STUDIO_MODE'] = '1'
    $envs['FG_MODE'] = $mode
    $envs['FG_CONTAINER'] = if ($cmbContainer.SelectedIndex -eq 0) { 'MP4' } else { 'MKV' }
    $speedModes = @('FAST', 'STANDARD', 'UHQ')
    $envs['FG_SPEED'] = $speedModes[$cmbSpeed.SelectedIndex]
    $envs['FG_BITRATE'] = [string]$bitrate
    $envs['FG_MAXRATE'] = [string]$maxrate
    $envs['FG_BUFSIZE'] = [string]$bufsize
    $envs['FG_FPS_MODE'] = if ($cmbFps.SelectedIndex -eq 0) { 'AUTO' } else { 'SOURCE' }
    $envs['FG_DEINTERLACE'] = if ($cmbDeint.SelectedIndex -eq 0) { 'AUTO' } else { 'OFF' }
    $deintMethods = @('BWDIF_VULKAN', 'BWDIF_CUDA', 'W3FDIF')
    $envs['FG_DEINT_METHOD'] = $deintMethods[$cmbDeintMethod.SelectedIndex]
    $envs['FG_CINEMATIC_FRAME'] = if ($chkCinematic.Checked) { '1' } else { '0' }
    $envs['FG_FRAME_MODE'] = if ($cmbFrameMode.SelectedIndex -eq 0) { 'LETTERBOX' } else { 'CROP' }
    $envs['FG_KEEP_FAILED'] = '1'
    $uploadBitrates = @(6000, 8000, 15000)
    $envs['FG_UPLOAD'] = if ($chkUpload.Checked) { '1' } else { '0' }
    $envs['FG_UPLOAD_HIGH_MOTION'] = if ($chkUpload.Checked -and $cmbUploadBitrate.SelectedIndex -ge 3 -and $chkUploadHighMotion.Checked) { '1' } else { '0' }
    if ($cmbUploadBitrate.SelectedIndex -le 2) {
        $envs['FG_UPLOAD_MODE'] = 'VBR'
        $envs['FG_UPLOAD_BITRATE'] = [string]$uploadBitrates[$cmbUploadBitrate.SelectedIndex]
        $envs['FG_UPLOAD_X264_TIER'] = ''
        $envs['FG_UPLOAD_QP'] = ''
    } else {
        $envs['FG_UPLOAD_MODE'] = 'X264'
        $envs['FG_UPLOAD_BITRATE'] = ''
        $envs['FG_UPLOAD_X264_TIER'] = [string]($cmbUploadBitrate.SelectedIndex - 2)
        $envs['FG_UPLOAD_QP'] = ''
    }
    $envs['FG_SUBTITLE'] = if ($script:UploadSubtitle.Enabled) { '1' } else { '0' }
    $envs['FG_UPLOAD_SUBTITLE'] = $envs['FG_SUBTITLE']
    $envs['FG_SUB_MODE'] = [string]$script:UploadSubtitle.Mode
    $envs['FG_SUB_INDEX'] = [string]$script:UploadSubtitle.EmbeddedIndex
    $envs['FG_SUB_PATH'] = [string]$script:UploadSubtitle.ExternalPath
    $envs['FG_SUB_FONT'] = [string]$script:UploadSubtitle.FontName
    $envs['FG_SUB_FONT_SIZE'] = [string]$script:UploadSubtitle.FontSize
    $envs['FG_SUB_PRIMARY_HEX'] = [string]$script:UploadSubtitle.PrimaryHex
    $envs['FG_SUB_BORDER_HEX'] = [string]$script:UploadSubtitle.BorderHex
    $envs['FG_SUB_OUTLINE'] = [string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:0.##}', [double]$script:UploadSubtitle.Outline)
    $envs['FG_SUB_SHADOW'] = [string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:0.##}', [double]$script:UploadSubtitle.Shadow)
    $envs['FG_SUB_MARGINV'] = [string]$script:UploadSubtitle.MarginV

    if ($chkLut.Checked) {
        $envs['FG_LUT_PATH'] = $script:SelectedLutPath
        $lutStrengths = @(25, 50, 75, 100)
        $envs['FG_LUT_STRENGTH'] = [string]$lutStrengths[$trackLutStrength.Value]
    } else {
        [void]$envs.Remove('FG_LUT_PATH')
        [void]$envs.Remove('FG_LUT_STRENGTH')
    }

    if ($mode -eq 'AV1') {
        $av1Modes = @('PRESET', 'ISO', 'TABLE')
        $envs['FG_AV1_GRAIN_MODE'] = $av1Modes[$cmbAv1Method.SelectedIndex]
        $envs['FG_AV1_FORMAT'] = [string]($cmbAv1Format.SelectedIndex + 1)
        $envs['FG_AV1_STOCK'] = [string]($cmbAv1Stock.SelectedIndex + 1)
        $envs['FG_AV1_ISO'] = [string][int]$numIso.Value
        $envs['FG_AV1_CHROMA'] = if ($chkChroma.Checked) { '1' } else { '0' }
        if ($selectedAv1GrainTable) { $envs['FG_AV1_GRAIN_TABLE'] = $selectedAv1GrainTable }
        else { [void]$envs.Remove('FG_AV1_GRAIN_TABLE') }
    } else {
        $envs['FG_GRAIN_ROOT'] = $txtGrainRoot.Text.Trim()
        $envs['FG_HEVC_GRAIN_PATH'] = $selectedGrainPath
        $grainTag = [System.IO.Path]::GetFileNameWithoutExtension($selectedGrainPath)
        $grainTag = [System.Text.RegularExpressions.Regex]::Replace($grainTag, '[^\p{L}\p{Nd}]+', '_').Trim('_')
        if (-not $grainTag) { $grainTag = 'SCAN' }
        if ($grainTag.Length -gt 48) { $grainTag = $grainTag.Substring(0, 48).TrimEnd('_') }
        $envs['FG_HEVC_GRAIN_TAG'] = $grainTag
        $envs['FG_HEVC_STRENGTH_SEL'] = [string]($trackHevcStrength.Value + 1)
    }

    try {
        $proc = New-Object System.Diagnostics.Process
        $proc.StartInfo = $psi
        if (-not $proc.Start()) { throw '无法启动 CMD 进程。' }
        $script:RunningProcess = $proc
        $script:OutputStreamClosed = $false
        $script:ErrorStreamClosed = $false
        $script:OutputReadTask = $proc.StandardOutput.ReadLineAsync()
        $script:ErrorReadTask = $proc.StandardError.ReadLineAsync()
        Set-RunningState $true
        $lblStatus.Text = '任务正在运行'
        $pollTimer.Start()
    } catch {
        Set-RunningState $false
        if ($proc) { try { $proc.Dispose() } catch {} }
        $script:RunningProcess = $null
        $script:OutputReadTask = $null
        $script:ErrorReadTask = $null
        $script:OutputStreamClosed = $true
        $script:ErrorStreamClosed = $true
        Show-Error ("启动编码任务失败：`r`n" + $_.Exception.Message)
    }
}

function Stop-Encoding {
    if (-not $script:RunningProcess) { return }
    try {
        if ($script:RunningProcess.HasExited) { return }
    } catch { return }

    $answer = [System.Windows.Forms.MessageBox]::Show(
        $form,
        "确定取消当前任务？`r`n`r`n强制停止后，可能保留未完成输出或 AV1 临时目录。",
        '取消胶片颗粒任务',
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Warning
    )
    if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }

    $script:RunWasCancelled = $true
    $lblStatus.Text = '正在取消任务…'
    $btnCancel.Enabled = $false
    try {
        $pidValue = $script:RunningProcess.Id
        $taskkill = Join-Path $env:SystemRoot 'System32\taskkill.exe'
        $kill = New-Object System.Diagnostics.ProcessStartInfo
        $kill.FileName = $taskkill
        $kill.Arguments = '/PID ' + $pidValue + ' /T /F'
        $kill.UseShellExecute = $false
        $kill.CreateNoWindow = $true
        $killer = [System.Diagnostics.Process]::Start($kill)
        $killer.WaitForExit(5000) | Out-Null
        $killer.Dispose()
    } catch {
        try { $script:RunningProcess.Kill() } catch {}
    }
}

$pollTimer = New-Object System.Windows.Forms.Timer
$pollTimer.Interval = 200
$pollTimer.Add_Tick({
    Read-ProcessOutput
    if ($script:RunningProcess) {
        try {
            if ($script:RunningProcess.HasExited) {
                $exitCode = $script:RunningProcess.ExitCode
                Read-ProcessOutput
                if ($script:OutputStreamClosed -and $script:ErrorStreamClosed) {
                    $pollTimer.Stop()
                    Complete-Run $exitCode
                }
            }
        } catch {}
    }
})

function Show-UtilityProcessDialog {
    param(
        [System.Windows.Forms.IWin32Window]$Owner,
        [string]$Title,
        [string]$FileName,
        [string]$Arguments,
        [hashtable]$Environment,
        [string]$WorkingDirectory
    )

    $runDlg = New-Object System.Windows.Forms.Form
    $runDlg.Text = $Title
    $runDlg.StartPosition = 'CenterParent'
    $runDlg.Size = New-Object System.Drawing.Size -ArgumentList 820, 520
    $runDlg.MinimumSize = New-Object System.Drawing.Size -ArgumentList 700, 420
    $runDlg.Font = New-UiFont 9
    $runDlg.ShowInTaskbar = $false

    $runTable = New-Object System.Windows.Forms.TableLayoutPanel
    $runTable.Dock = 'Fill'; $runTable.RowCount = 2; $runTable.ColumnCount = 1
    Add-RowPercent $runTable 100
    Add-RowAbsolute $runTable 48
    [void]$runDlg.Controls.Add($runTable)

    $runLog = New-Object System.Windows.Forms.RichTextBox
    $runLog.Dock = 'Fill'; $runLog.ReadOnly = $true; $runLog.WordWrap = $false
    $runLog.Font = New-Object System.Drawing.Font -ArgumentList 'Consolas', 9
    [void]$runTable.Controls.Add($runLog,0,0)

    $runButtons = New-Object System.Windows.Forms.FlowLayoutPanel
    $runButtons.Dock = 'Fill'; $runButtons.FlowDirection='RightToLeft'; $runButtons.WrapContents=$false
    $runButton = New-Object System.Windows.Forms.Button
    $runButton.Text = '取消'; $runButton.Width = 88; $runButton.Height = 30; $runButton.Margin=New-Object System.Windows.Forms.Padding -ArgumentList 6,7,10,5
    [void]$runButtons.Controls.Add($runButton)
    [void]$runTable.Controls.Add($runButtons,0,1)

    $state = @{ Process=$null; OutTask=$null; ErrTask=$null; OutClosed=$true; ErrClosed=$true; ExitCode=$null; Cancelled=$false; Finished=$false }
    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 120

    $append = {
        param([string]$Text)
        if (-not $Text) { return }
        $runLog.AppendText($Text)
        $runLog.SelectionStart = $runLog.TextLength
        $runLog.ScrollToCaret()
    }
    $drain = {
        if (-not $state.Process) { return }
        $loops=0
        while ($state.OutTask -and $state.OutTask.IsCompleted -and $loops -lt 300) {
            try { $line=$state.OutTask.Result } catch { $line=$null }
            if ($null -eq $line) { $state.OutTask=$null; $state.OutClosed=$true; break }
            & $append ($line + "`r`n")
            $loops++
            try { $state.OutTask=$state.Process.StandardOutput.ReadLineAsync() } catch { $state.OutTask=$null; $state.OutClosed=$true }
        }
        $loops=0
        while ($state.ErrTask -and $state.ErrTask.IsCompleted -and $loops -lt 300) {
            try { $line=$state.ErrTask.Result } catch { $line=$null }
            if ($null -eq $line) { $state.ErrTask=$null; $state.ErrClosed=$true; break }
            & $append ($line + "`r`n")
            $loops++
            try { $state.ErrTask=$state.Process.StandardError.ReadLineAsync() } catch { $state.ErrTask=$null; $state.ErrClosed=$true }
        }
    }

    $timer.Add_Tick({
        & $drain
        if ($state.Process -and -not $state.Finished) {
            try {
                if ($state.Process.HasExited) {
                    $state.ExitCode=$state.Process.ExitCode
                    & $drain
                    if ($state.OutClosed -and $state.ErrClosed) {
                        $state.Finished=$true
                        $timer.Stop()
                        & $append ("`r`n=== " + $(if ($state.Cancelled) { '已取消' } elseif ($state.ExitCode -eq 0) { '完成' } else { "结束，代码 $($state.ExitCode)" }) + " ===`r`n")
                        $runButton.Enabled=$true
                        $runButton.Text='关闭'
                    }
                }
            } catch {}
        }
    })

    $runButton.Add_Click({
        if ($state.Process -and -not $state.Finished) {
            $answer=[System.Windows.Forms.MessageBox]::Show($runDlg,'确定取消当前工具任务？','取消任务',[System.Windows.Forms.MessageBoxButtons]::YesNo,[System.Windows.Forms.MessageBoxIcon]::Warning)
            if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }
            $state.Cancelled=$true
            try {
                $taskkill=Join-Path $env:SystemRoot 'System32\taskkill.exe'
                $kpsi=New-Object System.Diagnostics.ProcessStartInfo
                $kpsi.FileName=$taskkill; $kpsi.Arguments='/PID ' + $state.Process.Id + ' /T /F'; $kpsi.UseShellExecute=$false; $kpsi.CreateNoWindow=$true
                $kp=[System.Diagnostics.Process]::Start($kpsi); [void]$kp.WaitForExit(5000); $kp.Dispose()
            } catch { try { $state.Process.Kill() } catch {} }
            $runButton.Enabled=$false
        } else {
            $runDlg.Close()
        }
    })

    $runDlg.Add_FormClosing({
        param($sender,$e)
        if ($state.Process -and -not $state.Finished) {
            $e.Cancel=$true
            $runButton.PerformClick()
        }
    })

    $runDlg.Add_Shown({
        try {
            $psi=New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName=$FileName; $psi.Arguments=$Arguments
            $psi.UseShellExecute=$false; $psi.CreateNoWindow=$true
            $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
            if ($WorkingDirectory) { $psi.WorkingDirectory=$WorkingDirectory }
            if ($Environment) { foreach ($k in $Environment.Keys) { $psi.EnvironmentVariables[[string]$k]=[string]$Environment[$k] } }
            $proc=New-Object System.Diagnostics.Process; $proc.StartInfo=$psi
            if (-not $proc.Start()) { throw '无法启动工具进程。' }
            $state.Process=$proc; $state.OutClosed=$false; $state.ErrClosed=$false
            $state.OutTask=$proc.StandardOutput.ReadLineAsync(); $state.ErrTask=$proc.StandardError.ReadLineAsync()
            & $append ("$Title`r`n" + ('=' * 68) + "`r`n")
            $timer.Start()
        } catch {
            $state.ExitCode=-1; $state.Finished=$true
            & $append ("启动失败：" + $_.Exception.Message + "`r`n")
            $runButton.Text='关闭'
        }
    })

    [void]$runDlg.ShowDialog($Owner)
    $timer.Stop(); $timer.Dispose()
    if ($state.Process) { try { $state.Process.Dispose() } catch {} }
    $runDlg.Dispose()
    return [pscustomobject]@{ ExitCode=$state.ExitCode; Cancelled=$state.Cancelled }
}

function Show-PathConfigurationDialog {
    $cfg = Get-FilmGrainConfig
    $oldFfmpegDir = [string]$cfg.FFMPEG_DIR
    $oldGrainRoot = [string]$cfg.GRAIN_ROOT
    $oldLutRoot = [string]$cfg.LUT_ROOT

    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = 'Film Grain Studio · 路径配置'
    $dlg.StartPosition = 'CenterParent'
    $dlg.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $dlg.MaximizeBox = $false
    $dlg.MinimizeBox = $false
    $dlg.ShowInTaskbar = $false
    $dlg.ClientSize = New-Object System.Drawing.Size -ArgumentList 850, 486
    $dlg.Font = New-UiFont 9

    $table = New-Object System.Windows.Forms.TableLayoutPanel
    $table.Dock = 'Fill'
    $table.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 12, 12, 12, 10
    $table.ColumnCount = 4
    $table.RowCount = 10
    $c0 = New-Object System.Windows.Forms.ColumnStyle; $c0.SizeType='Absolute'; $c0.Width=108; [void]$table.ColumnStyles.Add($c0)
    $c1 = New-Object System.Windows.Forms.ColumnStyle; $c1.SizeType='Percent'; $c1.Width=100; [void]$table.ColumnStyles.Add($c1)
    $c2 = New-Object System.Windows.Forms.ColumnStyle; $c2.SizeType='Absolute'; $c2.Width=94; [void]$table.ColumnStyles.Add($c2)
    $c3 = New-Object System.Windows.Forms.ColumnStyle; $c3.SizeType='Absolute'; $c3.Width=38; [void]$table.ColumnStyles.Add($c3)
    Add-RowAbsolute $table 42
    Add-RowAbsolute $table 50
    Add-RowAbsolute $table 42
    Add-RowAbsolute $table 36
    Add-RowAbsolute $table 42
    Add-RowAbsolute $table 36
    Add-RowAbsolute $table 42
    Add-RowAbsolute $table 36
    Add-RowPercent $table 100
    Add-RowAbsolute $table 42
    [void]$dlg.Controls.Add($table)

    function Add-ConfigLabel([int]$Row,[string]$Text) {
        $l=New-Object System.Windows.Forms.Label
        $l.Text=$Text; $l.Dock='Fill'; $l.TextAlign='MiddleLeft'; $l.Margin=New-Object System.Windows.Forms.Padding -ArgumentList 4,4,4,4
        [void]$table.Controls.Add($l,0,$Row)
    }
    function New-ConfigTextBox([string]$Value) {
        $t=New-Object System.Windows.Forms.TextBox
        $t.Text=$Value; $t.Dock='Fill'; $t.Margin=New-Object System.Windows.Forms.Padding -ArgumentList 4,8,4,6
        return $t
    }
    function New-ConfigBrowseButton {
        $b=New-Object System.Windows.Forms.Button
        $b.Text='浏览…'; $b.Dock='Fill'; $b.Margin=New-Object System.Windows.Forms.Padding -ArgumentList 4,5,4,5
        return $b
    }
    function New-ConfigRefreshButton {
        $b=New-Object System.Windows.Forms.Button
        $b.Text=[string][char]8635; $b.Dock='Fill'; $b.Margin=New-Object System.Windows.Forms.Padding -ArgumentList 2,5,2,5
        return $b
    }
    function New-ConfigStatusLabel([string]$Text) {
        $l=New-Object System.Windows.Forms.Label
        $l.Dock='Fill'; $l.TextAlign='MiddleLeft'; $l.ForeColor=$ColorMuted
        $l.Margin=New-Object System.Windows.Forms.Padding -ArgumentList 4,0,4,2
        $l.Text=$Text
        return $l
    }

    $txtCfgFfmpegDir = New-ConfigTextBox ([string]$cfg.FFMPEG_DIR)
    $txtCfgGrav = New-ConfigTextBox ([string]$cfg.GRAV1SYNTH)
    $txtCfgGrain = New-ConfigTextBox ([string]$cfg.GRAIN_ROOT)
    $txtCfgLut = New-ConfigTextBox ([string]$cfg.LUT_ROOT)
    $btnCfgFfmpegDir = New-ConfigBrowseButton
    $btnCfgGrav = New-ConfigBrowseButton
    $btnCfgGrain = New-ConfigBrowseButton
    $btnCfgLut = New-ConfigBrowseButton
    $btnRefreshFfmpeg = New-ConfigRefreshButton
    $btnRefreshGrav = New-ConfigRefreshButton
    $btnRefreshGrain = New-ConfigRefreshButton
    $btnRefreshLut = New-ConfigRefreshButton
    $btnBuildGrainCache = New-Object System.Windows.Forms.Button
    $btnBuildGrainCache.Text='生成高速缓存'; $btnBuildGrainCache.Dock='Fill'; $btnBuildGrainCache.Enabled=$false
    $btnBuildGrainCache.Margin=New-Object System.Windows.Forms.Padding -ArgumentList 4,3,2,3
    $btnBuildLutPreviews = New-Object System.Windows.Forms.Button
    $btnBuildLutPreviews.Text='创建缩略图'; $btnBuildLutPreviews.Dock='Fill'; $btnBuildLutPreviews.Enabled=$false
    $btnBuildLutPreviews.Margin=New-Object System.Windows.Forms.Padding -ArgumentList 4,3,2,3

    Add-ConfigLabel 0 'FFmpeg 目录'
    [void]$table.Controls.Add($txtCfgFfmpegDir,1,0)
    [void]$table.Controls.Add($btnCfgFfmpegDir,2,0)
    [void]$table.Controls.Add($btnRefreshFfmpeg,3,0)
    $lblFfmpegDetect = New-ConfigStatusLabel '尚未检测，点击 ↻。'
    [void]$table.Controls.Add($lblFfmpegDetect,1,1); $table.SetColumnSpan($lblFfmpegDetect,3)

    Add-ConfigLabel 2 'grav1synth'
    [void]$table.Controls.Add($txtCfgGrav,1,2)
    [void]$table.Controls.Add($btnCfgGrav,2,2)
    [void]$table.Controls.Add($btnRefreshGrav,3,2)
    $lblGravDetect = New-ConfigStatusLabel '尚未检测，点击 ↻。'
    [void]$table.Controls.Add($lblGravDetect,1,3); $table.SetColumnSpan($lblGravDetect,3)

    Add-ConfigLabel 4 '颗粒根目录'
    [void]$table.Controls.Add($txtCfgGrain,1,4)
    [void]$table.Controls.Add($btnCfgGrain,2,4)
    [void]$table.Controls.Add($btnRefreshGrain,3,4)
    $lblGrainDetect = New-ConfigStatusLabel '尚未检测，点击 ↻。'
    [void]$table.Controls.Add($lblGrainDetect,1,5)
    [void]$table.Controls.Add($btnBuildGrainCache,2,5); $table.SetColumnSpan($btnBuildGrainCache,2)

    Add-ConfigLabel 6 'LUT 根目录'
    [void]$table.Controls.Add($txtCfgLut,1,6)
    [void]$table.Controls.Add($btnCfgLut,2,6)
    [void]$table.Controls.Add($btnRefreshLut,3,6)
    $lblLutDetect = New-ConfigStatusLabel '尚未检测，点击 ↻。'
    [void]$table.Controls.Add($lblLutDetect,1,7)
    [void]$table.Controls.Add($btnBuildLutPreviews,2,7); $table.SetColumnSpan($btnBuildLutPreviews,2)

    $note=New-Object System.Windows.Forms.Label
    $note.Dock='Fill'; $note.ForeColor=$ColorMuted; $note.TextAlign='TopLeft'; $note.Padding=New-Object System.Windows.Forms.Padding -ArgumentList 4,8,4,0
    $note.Text="保存到：$($cfg.ConfigPath)`r`n浏览选择后会立即检测；手工修改路径后请点击右侧 ↻。Grain / LUT 检测会同时检查 Cache / Gallery 预览图，缺失时可直接在此生成。保存本身不会重新执行扫描。"
    [void]$table.Controls.Add($note,0,8); $table.SetColumnSpan($note,4)

    $buttonPanel=New-Object System.Windows.Forms.FlowLayoutPanel
    $buttonPanel.Dock='Fill'; $buttonPanel.FlowDirection='RightToLeft'; $buttonPanel.WrapContents=$false
    $btnOk=New-Object System.Windows.Forms.Button; $btnOk.Text='保存'; $btnOk.Width=82
    $btnCancelCfg=New-Object System.Windows.Forms.Button; $btnCancelCfg.Text='取消'; $btnCancelCfg.Width=82; $btnCancelCfg.DialogResult=[System.Windows.Forms.DialogResult]::Cancel
    $btnDefaults=New-Object System.Windows.Forms.Button; $btnDefaults.Text='恢复默认'; $btnDefaults.Width=92
    [void]$buttonPanel.Controls.Add($btnOk); [void]$buttonPanel.Controls.Add($btnCancelCfg); [void]$buttonPanel.Controls.Add($btnDefaults)
    [void]$table.Controls.Add($buttonPanel,0,9); $table.SetColumnSpan($buttonPanel,4)
    $dlg.CancelButton=$btnCancelCfg

    $cfgTip=New-Object System.Windows.Forms.ToolTip
    $cfgTip.SetToolTip($btnRefreshFfmpeg,'检测 ffmpeg.exe / ffprobe.exe 及版本')
    $cfgTip.SetToolTip($btnRefreshGrav,'检测 grav1synth.exe 及版本')
    $cfgTip.SetToolTip($btnRefreshGrain,'递归统计原始 .mov Grain 文件')
    $cfgTip.SetToolTip($btnRefreshLut,'递归统计可用于 LUT Gallery 的 .cube 文件及预览图')
    $cfgTip.SetToolTip($btnBuildGrainCache,'为缺失的 Grain MOV 创建原分辨率 + 1080p HEVC Main10 Lossless Cache；已有文件不会覆盖')
    $cfgTip.SetToolTip($btnBuildLutPreviews,'使用当前参考图为缺失的 LUT 创建 Gallery 预览图；尚未更换参考图时使用项目默认图')

    $ffmpegState = @{
        LastDir = ''
        Valid = $false
        FfmpegVersion = ''
        FfprobeVersion = ''
    }
    $grainCacheState = @{ MovCount=0; FullCount=0; Cache1080Count=0; Missing=0 }
    $lutPreviewState = @{ LutCount=0; PreviewCount=0; Missing=0 }

    function Get-ConfigToolVersion([string]$ExePath,[string]$ToolName,[string]$Arguments) {
        if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
            return [pscustomobject]@{ Ok=$false; Version='未找到' }
        }
        try {
            $psi=New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName=$ExePath
            $psi.Arguments=$Arguments
            $psi.UseShellExecute=$false
            $psi.CreateNoWindow=$true
            $psi.RedirectStandardOutput=$true
            $psi.RedirectStandardError=$true
            $p=New-Object System.Diagnostics.Process
            $p.StartInfo=$psi
            if (-not $p.Start()) { return [pscustomobject]@{ Ok=$false; Version='无法运行' } }
            $stdout=$p.StandardOutput.ReadToEnd()
            $stderr=$p.StandardError.ReadToEnd()
            $p.WaitForExit()
            $exitCode=$p.ExitCode
            $p.Dispose()
            if ($exitCode -ne 0) { return [pscustomobject]@{ Ok=$false; Version='无法运行' } }
            $allText=($stdout + "`n" + $stderr)
            $lines=@($allText -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 8)
            foreach ($line in $lines) {
                $text=[string]$line
                if ($ToolName -eq 'ffmpeg' -or $ToolName -eq 'ffprobe') {
                    if ($text -match ('^' + [regex]::Escape($ToolName) + '\s+version\s+([0-9]+(?:\.[0-9]+){1,3})')) {
                        return [pscustomobject]@{ Ok=$true; Version=$matches[1] }
                    }
                    if ($text -match ('^' + [regex]::Escape($ToolName) + '\s+version\s+([^\s]+)')) {
                        return [pscustomobject]@{ Ok=$true; Version=$matches[1] }
                    }
                } elseif ($text -match '(?i)\b(?:version\s*)?v?([0-9]+(?:\.[0-9]+){1,3}(?:[-+][^\s]+)?)') {
                    return [pscustomobject]@{ Ok=$true; Version=$matches[1] }
                }
            }
            return [pscustomobject]@{ Ok=$true; Version='版本未知' }
        } catch {
            return [pscustomobject]@{ Ok=$false; Version='无法运行' }
        }
    }

    function Update-FfmpegDirectoryStatus {
        $dir=$txtCfgFfmpegDir.Text.Trim().TrimEnd('\')
        $ffmpegState.LastDir=$dir
        $ffmpegState.Valid=$false
        $lblFfmpegDetect.Text='正在检测 ffmpeg.exe / ffprobe.exe…'
        [System.Windows.Forms.Application]::DoEvents()

        if (-not $dir -or -not (Test-Path -LiteralPath $dir -PathType Container)) {
            $lblFfmpegDetect.Text="ffmpeg.exe   ✘ 未找到`r`nffprobe.exe  ✘ 未找到"
            return
        }

        $ffmpegResult=Get-ConfigToolVersion (Join-Path $dir 'ffmpeg.exe') 'ffmpeg' '-version'
        $ffprobeResult=Get-ConfigToolVersion (Join-Path $dir 'ffprobe.exe') 'ffprobe' '-version'
        $ffmpegState.FfmpegVersion=[string]$ffmpegResult.Version
        $ffmpegState.FfprobeVersion=[string]$ffprobeResult.Version
        $ffmpegMark=if ($ffmpegResult.Ok) { '✔' } else { '✘' }
        $ffprobeMark=if ($ffprobeResult.Ok) { '✔' } else { '✘' }
        $lblFfmpegDetect.Text="ffmpeg.exe   $ffmpegMark $($ffmpegResult.Version)`r`nffprobe.exe  $ffprobeMark $($ffprobeResult.Version)"
        $ffmpegState.Valid=([bool]$ffmpegResult.Ok -and [bool]$ffprobeResult.Ok)
    }

    function Update-GravStatus {
        $path=$txtCfgGrav.Text.Trim()
        $lblGravDetect.Text='正在检测 grav1synth.exe…'
        [System.Windows.Forms.Application]::DoEvents()
        $result=Get-ConfigToolVersion $path 'grav1synth' '--version'
        $mark=if ($result.Ok) { '✔' } else { '✘' }
        $lblGravDetect.Text="grav1synth.exe  $mark $($result.Version)"
    }

    function Update-GrainStatus {
        $root=$txtCfgGrain.Text.Trim()
        $btnBuildGrainCache.Enabled=$false
        $grainCacheState.MovCount=0; $grainCacheState.FullCount=0; $grainCacheState.Cache1080Count=0; $grainCacheState.Missing=0
        if (-not $root -or -not (Test-Path -LiteralPath $root -PathType Container)) {
            $lblGrainDetect.Text='✘ 颗粒根目录不存在'
            return
        }
        $lblGrainDetect.Text='正在扫描 MOV / Cache…'
        [System.Windows.Forms.Application]::DoEvents()
        try {
            $movs=@(Get-ChildItem -LiteralPath $root -Filter '*.mov' -File -Recurse -ErrorAction SilentlyContinue)
            $full=0; $small=0
            foreach ($mov in $movs) {
                $base=Join-Path $mov.DirectoryName $mov.BaseName
                if (Test-Path -LiteralPath ($base + '_HEVC_Lossless.mkv') -PathType Leaf) { $full++ }
                if (Test-Path -LiteralPath ($base + '_1080p_HEVC_Lossless.mkv') -PathType Leaf) { $small++ }
            }
            $count=$movs.Count
            $grainCacheState.MovCount=$count; $grainCacheState.FullCount=$full; $grainCacheState.Cache1080Count=$small
            $grainCacheState.Missing=($count-$full)+($count-$small)
            if ($count -eq 0) {
                $lblGrainDetect.Text='✔ 原始 MOV 0 · 无可生成 Cache'
            } else {
                $lblGrainDetect.Text="✔ MOV $count · Cache 原尺寸 $full/$count · 1080p $small/$count"
                $btnBuildGrainCache.Enabled=($grainCacheState.Missing -gt 0)
            }
        } catch {
            $lblGrainDetect.Text='✘ 扫描失败：' + $_.Exception.Message
        }
    }

    function Update-LutStatus {
        $root=$txtCfgLut.Text.Trim()
        $btnBuildLutPreviews.Enabled=$false
        $lutPreviewState.LutCount=0; $lutPreviewState.PreviewCount=0; $lutPreviewState.Missing=0
        if (-not $root -or -not (Test-Path -LiteralPath $root -PathType Container)) {
            $lblLutDetect.Text='✘ LUT 根目录不存在'
            return
        }
        $lblLutDetect.Text='正在扫描 LUT / 预览图…'
        [System.Windows.Forms.Application]::DoEvents()
        try {
            $rootFull=[System.IO.Path]::GetFullPath($root).TrimEnd('\')
            $previewRoot=Join-Path $rootFull '_LUT_PREVIEWS'
            $prefix=$rootFull+'\'
            $luts=@(Get-ChildItem -LiteralPath $rootFull -Filter '*.cube' -File -Recurse -ErrorAction SilentlyContinue |
                Where-Object { -not $_.FullName.StartsWith($previewRoot.TrimEnd('\')+'\', [System.StringComparison]::OrdinalIgnoreCase) })
            $previewCount=0
            foreach ($lut in $luts) {
                $relative=$lut.Name
                if ($lut.FullName.StartsWith($prefix,[System.StringComparison]::OrdinalIgnoreCase)) { $relative=$lut.FullName.Substring($prefix.Length) }
                $relDir=Split-Path $relative -Parent
                $dst=if (-not $relDir -or $relDir -eq '.') { $previewRoot } else { Join-Path $previewRoot $relDir }
                $jpg=Join-Path $dst ($lut.BaseName + '_preview.jpg')
                if (Test-Path -LiteralPath $jpg -PathType Leaf) { $previewCount++ }
            }
            $count=$luts.Count
            $lutPreviewState.LutCount=$count; $lutPreviewState.PreviewCount=$previewCount; $lutPreviewState.Missing=$count-$previewCount
            if ($count -eq 0) {
                $lblLutDetect.Text='✔ .cube LUT 0 · 无可创建预览图'
            } else {
                $lblLutDetect.Text="✔ LUT $count · Gallery 预览图 $previewCount/$count"
                $btnBuildLutPreviews.Enabled=($lutPreviewState.Missing -gt 0)
            }
        } catch {
            $lblLutDetect.Text='✘ 扫描失败：' + $_.Exception.Message
        }
    }

    $pickExe = {
        param($target,$title,$fileName)
        $ofd=New-Object System.Windows.Forms.OpenFileDialog
        $ofd.Title=$title; $ofd.Filter='可执行文件 (*.exe)|*.exe|所有文件|*.*'; $ofd.FileName=$fileName
        try { if (Test-Path -LiteralPath $target.Text -PathType Leaf) { $ofd.InitialDirectory=Split-Path -Parent $target.Text } } catch {}
        $changed=$false
        if ($ofd.ShowDialog($dlg) -eq [System.Windows.Forms.DialogResult]::OK) { $target.Text=$ofd.FileName; $changed=$true }
        $ofd.Dispose()
        return $changed
    }
    $pickFolder = {
        param($target,$description)
        $fbd=New-Object System.Windows.Forms.FolderBrowserDialog
        $fbd.Description=$description; $fbd.ShowNewFolderButton=$false
        try { if (Test-Path -LiteralPath $target.Text -PathType Container) { $fbd.SelectedPath=$target.Text } } catch {}
        $changed=$false
        if ($fbd.ShowDialog($dlg) -eq [System.Windows.Forms.DialogResult]::OK) { $target.Text=$fbd.SelectedPath; $changed=$true }
        $fbd.Dispose()
        return $changed
    }

    $txtCfgFfmpegDir.Add_TextChanged({
        $ffmpegState.LastDir=''; $ffmpegState.Valid=$false; $ffmpegState.FfmpegVersion=''; $ffmpegState.FfprobeVersion=''
        $lblFfmpegDetect.Text='路径已修改，点击 ↻ 检测。'
    })
    $txtCfgGrav.Add_TextChanged({ $lblGravDetect.Text='路径已修改，点击 ↻ 检测。' })
    $txtCfgGrain.Add_TextChanged({ $lblGrainDetect.Text='路径已修改，点击 ↻ 检测。'; $btnBuildGrainCache.Enabled=$false })
    $txtCfgLut.Add_TextChanged({ $lblLutDetect.Text='路径已修改，点击 ↻ 检测。'; $btnBuildLutPreviews.Enabled=$false })

    $btnRefreshFfmpeg.Add_Click({ Update-FfmpegDirectoryStatus })
    $btnRefreshGrav.Add_Click({ Update-GravStatus })
    $btnRefreshGrain.Add_Click({ Update-GrainStatus })
    $btnRefreshLut.Add_Click({ Update-LutStatus })

    $btnCfgFfmpegDir.Add_Click({
        $fbd=New-Object System.Windows.Forms.FolderBrowserDialog
        $fbd.Description='选择同时包含 ffmpeg.exe 与 ffprobe.exe 的目录'
        $fbd.ShowNewFolderButton=$false
        try { if (Test-Path -LiteralPath $txtCfgFfmpegDir.Text -PathType Container) { $fbd.SelectedPath=$txtCfgFfmpegDir.Text } } catch {}
        if ($fbd.ShowDialog($dlg) -eq [System.Windows.Forms.DialogResult]::OK) {
            $txtCfgFfmpegDir.Text=$fbd.SelectedPath
            Update-FfmpegDirectoryStatus
        }
        $fbd.Dispose()
    })
    $btnCfgGrav.Add_Click({ if (& $pickExe $txtCfgGrav '选择 grav1synth.exe' 'grav1synth.exe') { Update-GravStatus } })
    $btnCfgGrain.Add_Click({ if (& $pickFolder $txtCfgGrain '选择胶片颗粒根目录') { Update-GrainStatus } })
    $btnCfgLut.Add_Click({ if (& $pickFolder $txtCfgLut '选择 LUT 根目录') { Update-LutStatus } })

    $btnBuildGrainCache.Add_Click({
        Update-GrainStatus
        if ($grainCacheState.MovCount -le 0 -or $grainCacheState.Missing -le 0) { return }
        if (-not (Test-Path -LiteralPath $GrainCacheBat -PathType Leaf)) {
            [void][System.Windows.Forms.MessageBox]::Show($dlg,"找不到 Cache 工具：`r`n$GrainCacheBat",'Grain Cache',[System.Windows.Forms.MessageBoxButtons]::OK,[System.Windows.Forms.MessageBoxIcon]::Error)
            return
        }
        $missingFull=$grainCacheState.MovCount-$grainCacheState.FullCount
        $missing1080=$grainCacheState.MovCount-$grainCacheState.Cache1080Count
        $answer=[System.Windows.Forms.MessageBox]::Show($dlg,"将为缺失项创建 HEVC Main10 Lossless Cache：`r`n`r`n原分辨率：缺 $missingFull`r`n1080p：缺 $missing1080`r`n`r`n已有 Cache 不会覆盖。继续？",'生成高速缓存',[System.Windows.Forms.MessageBoxButtons]::YesNo,[System.Windows.Forms.MessageBoxIcon]::Question)
        if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }
        $cmdPath=$env:ComSpec; if (-not $cmdPath) { $cmdPath=Join-Path $env:SystemRoot 'System32\cmd.exe' }
        $inner='call ' + (Quote-CmdArgument $GrainCacheBat) + ' 3 2>&1'
        $ffdir=$txtCfgFfmpegDir.Text.Trim().TrimEnd('\')
        $envMap=@{
            FG_CACHE_NO_PAUSE='1'; FG_GRAIN_ROOT_OVERRIDE=$txtCfgGrain.Text.Trim();
            FG_FFMPEG_OVERRIDE=(Join-Path $ffdir 'ffmpeg.exe'); FG_FFPROBE_OVERRIDE=(Join-Path $ffdir 'ffprobe.exe')
        }
        [void](Show-UtilityProcessDialog $dlg '生成 Grain 高速缓存' $cmdPath ('/d /s /c "'+$inner+'"') $envMap $ScriptRoot)
        Update-GrainStatus
    })

    $btnBuildLutPreviews.Add_Click({
        Update-LutStatus
        if ($lutPreviewState.LutCount -le 0 -or $lutPreviewState.Missing -le 0) { return }
        $lutReference = if (Test-Path -LiteralPath $LutPreviewCurrentReference -PathType Leaf) { $LutPreviewCurrentReference } else { $LutPreviewDefaultReference }
        $lutReferenceLabel = if ([string]::Equals($lutReference,$LutPreviewCurrentReference,[System.StringComparison]::OrdinalIgnoreCase)) { '当前参考图' } else { '项目默认参考图' }
        if (-not (Test-Path -LiteralPath $LutPreviewGenerator -PathType Leaf) -or -not (Test-Path -LiteralPath $lutReference -PathType Leaf)) {
            [void][System.Windows.Forms.MessageBox]::Show($dlg,'找不到 LUT 预览生成脚本或可用参考图。','LUT 缩略图',[System.Windows.Forms.MessageBoxButtons]::OK,[System.Windows.Forms.MessageBoxIcon]::Error)
            return
        }
        $answer=[System.Windows.Forms.MessageBox]::Show($dlg,"将使用$lutReferenceLabel，为缺失的 $($lutPreviewState.Missing) 个 LUT 创建 Gallery 预览图。`r`n`r`n参考图：`r`n$lutReference`r`n`r`n已有预览不会覆盖；如需更换参考图并全部重建，仍请使用 LUT Gallery 的「更换参考图」。继续？",'创建 LUT 缩略图',[System.Windows.Forms.MessageBoxButtons]::YesNo,[System.Windows.Forms.MessageBoxIcon]::Question)
        if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }
        $ffmpegPath=Join-Path $txtCfgFfmpegDir.Text.Trim().TrimEnd('\') 'ffmpeg.exe'
        $outRoot=Join-Path $txtCfgLut.Text.Trim() '_LUT_PREVIEWS'
        $args='-NoLogo -NoProfile -ExecutionPolicy Bypass -File "'+$LutPreviewGenerator+'" -LutRoot "'+$txtCfgLut.Text.Trim()+'" -ReferencePath "'+$lutReference+'" -OutputRoot "'+$outRoot+'" -FFmpegPath "'+$ffmpegPath+'" -NonInteractive -NoPause'
        [void](Show-UtilityProcessDialog $dlg '创建 LUT Gallery 缩略图' 'powershell.exe' $args @{} $PackageRoot)
        Update-LutStatus
    })

    $btnDefaults.Add_Click({
        $txtCfgFfmpegDir.Text=[string]$script:FilmGrainConfigDefaults.FFMPEG_DIR
        $txtCfgGrav.Text=[string]$script:FilmGrainConfigDefaults.GRAV1SYNTH
        $txtCfgGrain.Text=[string]$script:FilmGrainConfigDefaults.GRAIN_ROOT
        $txtCfgLut.Text=[string]$script:FilmGrainConfigDefaults.LUT_ROOT
    })

    $btnOk.Add_Click({
        $ffmpegDir=$txtCfgFfmpegDir.Text.Trim().TrimEnd('\')
        if (-not (Test-Path -LiteralPath $ffmpegDir -PathType Container)) {
            [void][System.Windows.Forms.MessageBox]::Show($dlg,"FFmpeg 目录不存在：`r`n$ffmpegDir",'路径配置',[System.Windows.Forms.MessageBoxButtons]::OK,[System.Windows.Forms.MessageBoxIcon]::Warning)
            return
        }
        foreach ($exeName in @('ffmpeg.exe','ffprobe.exe')) {
            $exePath=Join-Path $ffmpegDir $exeName
            if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
                [void][System.Windows.Forms.MessageBox]::Show($dlg,"FFmpeg 目录中缺少：$exeName`r`n`r`n$ffmpegDir",'路径配置',[System.Windows.Forms.MessageBoxButtons]::OK,[System.Windows.Forms.MessageBoxIcon]::Warning)
                return
            }
        }
        if (-not (Test-Path -LiteralPath $txtCfgGrav.Text.Trim() -PathType Leaf)) {
            [void][System.Windows.Forms.MessageBox]::Show($dlg,"grav1synth 路径不存在：`r`n$($txtCfgGrav.Text.Trim())",'路径配置',[System.Windows.Forms.MessageBoxButtons]::OK,[System.Windows.Forms.MessageBoxIcon]::Warning)
            return
        }
        foreach ($item in @(
            @('颗粒根目录',$txtCfgGrain.Text.Trim()),
            @('LUT 根目录',$txtCfgLut.Text.Trim())
        )) {
            if (-not (Test-Path -LiteralPath $item[1] -PathType Container)) {
                [void][System.Windows.Forms.MessageBox]::Show($dlg,"$($item[0])不存在：`r`n$($item[1])",'路径配置',[System.Windows.Forms.MessageBoxButtons]::OK,[System.Windows.Forms.MessageBoxIcon]::Warning)
                return
            }
        }

        try {
            Save-FilmGrainConfig -Values @{
                FFMPEG_DIR=$ffmpegDir; GRAV1SYNTH=$txtCfgGrav.Text.Trim();
                GRAIN_ROOT=$txtCfgGrain.Text.Trim(); LUT_ROOT=$txtCfgLut.Text.Trim()
            }
        } catch {
            [void][System.Windows.Forms.MessageBox]::Show($dlg,"保存配置失败：`r`n$($_.Exception.Message)",'路径配置',[System.Windows.Forms.MessageBoxButtons]::OK,[System.Windows.Forms.MessageBoxIcon]::Error)
            return
        }
        $dlg.DialogResult=[System.Windows.Forms.DialogResult]::OK
        $dlg.Close()
    })

    $dialogResult=$dlg.ShowDialog($form)
    $cfgTip.Dispose()
    if ($dialogResult -ne [System.Windows.Forms.DialogResult]::OK) { $dlg.Dispose(); return }
    $dlg.Dispose()

    $script:PathConfig = Get-FilmGrainConfig
    $script:Ffmpeg = [string]$script:PathConfig.FFMPEG
    $script:Ffprobe = [string]$script:PathConfig.FFPROBE
    $script:Grav1synth = [string]$script:PathConfig.GRAV1SYNTH
    $script:DefaultGrainRoot = [string]$script:PathConfig.GRAIN_ROOT
    $script:LutRoot = [string]$script:PathConfig.LUT_ROOT
    $script:LutPreviewRoot = Join-Path $script:LutRoot '_LUT_PREVIEWS'
    $script:LutGalleryIndex = Join-Path $script:LutPreviewRoot '_LUT_GALLERY_INDEX.json'
    $script:LutGalleryRecent = Join-Path $script:LutPreviewRoot '_LUT_GALLERY_RECENT.json'
    $script:LutGalleryFavorites = Join-Path $script:LutPreviewRoot '_LUT_GALLERY_FAVORITES.json'
    $script:LutGalleryThumbRoot = Join-Path $script:LutPreviewRoot '_GALLERY_THUMBS_v3_240x135'

    $grainRootChanged = -not [string]::Equals($oldGrainRoot, [string]$script:PathConfig.GRAIN_ROOT, [System.StringComparison]::OrdinalIgnoreCase)
    $lutRootChanged = -not [string]::Equals($oldLutRoot, [string]$script:PathConfig.LUT_ROOT, [System.StringComparison]::OrdinalIgnoreCase)

    $txtGrainRoot.Text = $script:DefaultGrainRoot
    $script:SelectedLutPath = $null
    $script:SelectedLutSource = 'None'
    $chkLut.Checked = $false
    if ($oldFfmpegDir -ne [string]$script:PathConfig.FFMPEG_DIR) {
        $script:HardwareCaps = $null
        $script:HardwareCapsReady = $false
        $script:Av1Available = $true
        $script:Av1UhqAvailable = $false
        $script:HevcAvailable = $true
        if ($ffmpegState.Valid -and $ffmpegState.LastDir -eq [string]$script:PathConfig.FFMPEG_DIR) {
            $script:FFmpegVersionOverride = [string]$ffmpegState.FfmpegVersion
        } else {
            $script:FFmpegVersionOverride = '未检测'
        }
    }
    Update-HardwareProfileUi
    Update-NoReencodeAvailability
    if ($cmbCodec.Items.Count -ge 2) {
        $cmbCodec.Items[0] = if ($script:HardwareCapsReady -and -not $script:Av1Available) { 'AV1 · grav1synth 胶片颗粒（当前硬件不可用）' } else { 'AV1 · grav1synth 胶片颗粒（默认）' }
    }
    Update-CodecUi
    if ($grainRootChanged) {
        $script:HevcGrainFiles = @()
        $script:LastScannedGrainRoot = ''
        $cmbHevcPlate.BeginUpdate()
        try {
            $cmbHevcPlate.Items.Clear()
            [void]$cmbHevcPlate.Items.Add('颗粒根目录已更新，请点击 ↻ 刷新')
            $cmbHevcPlate.SelectedIndex = 0
            $cmbHevcPlate.Enabled = $false
            $cacheNote.Text = '配置已保存；点击 Grain 区域的 ↻ 后再扫描原始 MOV。'
        } finally {
            $cmbHevcPlate.EndUpdate()
        }
    }
    if ($lutRootChanged) {
        Refresh-RecentLuts
        Refresh-FavoriteLuts
    }
    Set-LutUi
    Show-Info '路径配置已保存并重新载入。' '路径配置'
}
# Events
$btnConfig.Add_Click({ Show-PathConfigurationDialog })

$btnAdd.Add_Click({
    if ($openDialog.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
        Add-InputFiles $openDialog.FileNames
    }
})

$btnRemove.Add_Click({
    $selected = @($listFiles.SelectedItems)
    foreach ($item in $selected) { $listFiles.Items.Remove($item) }
    Update-FileCount
    Update-NoReencodeAvailability
    Update-SelectedMediaInfo
})

$btnClear.Add_Click({
    $listFiles.Items.Clear()
    Stop-Av1GrainInspect
    Update-FileCount
    Update-NoReencodeAvailability
    Update-SelectedMediaInfo
})

$dragEnterHandler = {
    param($sender, $e)
    if ($e.Data.GetDataPresent([System.Windows.Forms.DataFormats]::FileDrop)) {
        $e.Effect = [System.Windows.Forms.DragDropEffects]::Copy
    } else {
        $e.Effect = [System.Windows.Forms.DragDropEffects]::None
    }
}
$dragDropHandler = {
    param($sender, $e)
    $dropped = [string[]]$e.Data.GetData([System.Windows.Forms.DataFormats]::FileDrop)
    Add-InputFiles $dropped
}
$form.Add_DragEnter($dragEnterHandler)
$form.Add_DragDrop($dragDropHandler)
$listFiles.Add_DragEnter($dragEnterHandler)
$listFiles.Add_DragDrop($dragDropHandler)
$listFiles.Add_SelectedIndexChanged({ Update-SelectedMediaInfo; Update-DeinterlaceUi; Update-NoReencodeAvailability })

$cmbCodec.Add_SelectedIndexChanged({ Update-CodecUi; Update-FramingUi })
$cmbDeint.Add_SelectedIndexChanged({ Update-DeinterlaceUi })
$chkCinematic.Add_CheckedChanged({ Update-FramingUi })
$cmbFrameMode.Add_SelectedIndexChanged({ Update-FramingUi })
$cmbBitrate.Add_TextChanged({
    if (-not $script:ChangingCodec -and $cmbCodec.SelectedIndex -ge 0 -and $cmbCodec.SelectedIndex -le 1) {
        $script:ModeBitrate[$cmbCodec.SelectedIndex] = $cmbBitrate.Text.Trim()
    }
})
$cmbAv1Method.Add_SelectedIndexChanged({
    Update-Av1Controls
    if ($cmbAv1Method.SelectedIndex -eq 2) { Refresh-Av1GrainTables }
})
$cmbAv1Format.Add_SelectedIndexChanged({ Update-Av1Controls })
$btnRefreshAv1Table.Add_Click({ Refresh-Av1GrainTables })
$chkShowAllAv1Tables.Add_CheckedChanged({ Refresh-Av1GrainTables })

$trackHevcStrength.Add_ValueChanged({
    $names = @('Light · 65%', 'Natural · 75%', 'Strong · 85%', 'Full · 100%')
    $lblHevcStrength.Text = $names[$trackHevcStrength.Value]
})

$trackLutStrength.Add_ValueChanged({
    $strengths = @(25, 50, 75, 100)
    $lblLutStrength.Text = [string]$strengths[$trackLutStrength.Value] + '%'
})

$chkUpload.Add_CheckedChanged({
    $cmbUploadBitrate.Enabled = $chkUpload.Checked
    Update-UploadHighMotionUi
})
$cmbUploadBitrate.Add_SelectedIndexChanged({ Update-UploadHighMotionUi })
$btnUploadSubtitle.Add_Click({ Show-UploadSubtitleDialog })

$chkLut.Add_CheckedChanged({ Set-LutUi })
$btnLutGallery.Add_Click({ Open-LutGallery })
$cmbRecentLut.Add_SelectedIndexChanged({
    if ($script:LoadingRecentLuts) { return }
    $recentIndex = $cmbRecentLut.SelectedIndex - 1
    if ($recentIndex -lt 0 -or $recentIndex -ge $script:RecentLuts.Count) { return }
    $entry = $script:RecentLuts[$recentIndex]
    if (-not (Test-Path -LiteralPath ([string]$entry.LutPath) -PathType Leaf)) {
        Refresh-RecentLuts
        return
    }
    $script:SelectedLutPath = [string]$entry.LutPath
    $script:SelectedLutSource = 'Recent'
    $chkLut.Checked = $true
    $script:LoadingFavoriteLuts = $true
    try {
        if ($cmbFavoriteLut.Items.Count -gt 0) { $cmbFavoriteLut.SelectedIndex = 0 }
    } finally {
        $script:LoadingFavoriteLuts = $false
    }
    Set-LutUi
})
$cmbFavoriteLut.Add_SelectedIndexChanged({
    if ($script:LoadingFavoriteLuts) { return }
    $favoriteIndex = $cmbFavoriteLut.SelectedIndex - 1
    if ($favoriteIndex -lt 0 -or $favoriteIndex -ge $script:FavoriteLuts.Count) { return }
    $entry = $script:FavoriteLuts[$favoriteIndex]
    if (-not (Test-Path -LiteralPath ([string]$entry.LutPath) -PathType Leaf)) {
        Refresh-FavoriteLuts
        return
    }
    $script:SelectedLutPath = [string]$entry.LutPath
    $script:SelectedLutSource = 'Favorite'
    $chkLut.Checked = $true
    Set-LutUi
})
$btnLutClear.Add_Click({
    $script:SelectedLutPath = $null
    $script:SelectedLutSource = 'None'
    $chkLut.Checked = $false
    $script:LoadingRecentLuts = $true
    try {
        if ($cmbRecentLut.Items.Count -gt 0) { $cmbRecentLut.SelectedIndex = 0 }
    } finally {
        $script:LoadingRecentLuts = $false
    }
    $script:LoadingFavoriteLuts = $true
    try {
        if ($cmbFavoriteLut.Items.Count -gt 0) { $cmbFavoriteLut.SelectedIndex = 0 }
    } finally {
        $script:LoadingFavoriteLuts = $false
    }
    Set-LutUi
})

$btnGrainRoot.Add_Click({
    if (Test-Path -LiteralPath $txtGrainRoot.Text) { $folderDialog.SelectedPath = $txtGrainRoot.Text }
    if ($folderDialog.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
        $txtGrainRoot.Text = $folderDialog.SelectedPath
        Refresh-HevcGrainPlates
    }
})
$btnRefreshGrain.Add_Click({ Refresh-HevcGrainPlates })
$txtGrainRoot.Add_Leave({
    if ($script:LastScannedGrainRoot -ne $txtGrainRoot.Text.Trim()) {
        Refresh-HevcGrainPlates
    }
})

$btnCopyLog.Add_Click({
    if ($rtbLog.TextLength -gt 0) {
        try { [System.Windows.Forms.Clipboard]::SetText($rtbLog.Text) } catch {}
    }
})
$btnClearLog.Add_Click({ $rtbLog.Clear() })
$btnStart.Add_Click({ Start-Encoding })
$btnCancel.Add_Click({ Stop-Encoding })

$form.Add_Shown({
    # WScript starts PowerShell with SW_HIDE so no console is ever shown.
    # Force the WinForms top-level window visible after that startup state has
    # already been consumed by the form's first native ShowWindow call.
    [void]$form.BeginInvoke([System.Action]{
        $form.WindowState = [System.Windows.Forms.FormWindowState]::Normal
        [void][FilmGrainNativeWindow]::ShowWindow($form.Handle, 5)
        [void][FilmGrainNativeWindow]::SetWindowPos(
            $form.Handle,
            [IntPtr]::Zero,
            0,
            0,
            0,
            0,
            [uint32]0x0047
        )
        [void]$form.Activate()
    })
})

$form.Add_FormClosing({
    param($sender, $e)
    if ($script:RunningProcess) {
        try {
            if (-not $script:RunningProcess.HasExited) {
                $answer = [System.Windows.Forms.MessageBox]::Show(
                    $form,
                    '编码任务仍在运行。关闭 Studio 将强制停止当前任务，是否继续？',
                    '关闭 Film Grain Studio',
                    [System.Windows.Forms.MessageBoxButtons]::YesNo,
                    [System.Windows.Forms.MessageBoxIcon]::Warning
                )
                if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) {
                    $e.Cancel = $true
                    return
                }
                $script:RunWasCancelled = $true
                try {
                    $pidValue = $script:RunningProcess.Id
                    & (Join-Path $env:SystemRoot 'System32\taskkill.exe') /PID $pidValue /T /F 2>$null | Out-Null
                } catch {}
            }
        } catch {}
    }
    $pollTimer.Stop()
    Stop-VideoProbe
    Stop-Av1GrainInspect
})

$form.Add_FormClosed({ Clear-StudioLutPreview })

Load-BitrateChoices $cmbCodec.SelectedIndex
Update-CodecUi
Update-Av1Controls
Refresh-Av1GrainTables
Update-DeinterlaceUi
Update-FramingUi
Refresh-RecentLuts
Refresh-FavoriteLuts
Set-LutUi

if ($InputFiles) { Add-InputFiles $InputFiles }

[void]$form.ShowDialog()
