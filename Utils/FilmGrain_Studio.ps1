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

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$PackageRoot = Split-Path -Parent $ScriptRoot
$CoreBat = Join-Path $ScriptRoot 'FilmGrain_Universal_HEVC_AV1_StudioBridge.bat'
$Ffmpeg = 'E:\EnCoder\FFMpeg\13.0\bin\ffmpeg.exe'
$Ffprobe = 'E:\EnCoder\FFMpeg\13.0\bin\ffprobe.exe'
$HardwareCapsScript = Join-Path $ScriptRoot 'FilmGrain_Hardware_Caps.ps1'
$HardwareCapsCache = Join-Path $ScriptRoot '_HardwareCaps.json'
$LutRoot = 'E:\Adobe Portable\LUTs'
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
$script:ProbeProcess = $null
$script:ProbeOutputTask = $null
$script:ProbeErrorTask = $null
$script:ProbeTargetPath = ''
$script:ProbeCache = @{}
$script:ProbeVideoMeta = @{}
$script:RecentLuts = @()
$script:LoadingRecentLuts = $false
$script:FavoriteLuts = @()
$script:LoadingFavoriteLuts = $false
$script:CurrentLutPreviewImage = $null
$script:HardwareCaps = $null
$script:HardwareCapsReady = $false
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
    MarginV = 25
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
$subtitle.Text = 'AV1 grav1synth  ·  HEVC Scanned Grain  ·  LUT Gallery'
$subtitle.ForeColor = [System.Drawing.Color]::FromArgb(205, 214, 224)
$subtitle.Font = New-UiFont 9
$subtitle.AutoSize = $true
$subtitle.Location = New-Object System.Drawing.Point -ArgumentList 22, 43
[void]$header.Controls.Add($subtitle)

$baseline = New-Object System.Windows.Forms.Label
$baseline.Text = '核心：Universal HEVC / AV1'
$baseline.ForeColor = [System.Drawing.Color]::FromArgb(205, 214, 224)
$baseline.AutoSize = $true
$baseline.Anchor = 'Top,Right'
$baseline.Location = New-Object System.Drawing.Point -ArgumentList 1035, 25
$header.Add_Resize({ $baseline.Left = $header.ClientSize.Width - $baseline.Width - 20 })
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

$codecItems = @('AV1 Main10 + grav1synth（默认）', 'HEVC Main10 + Scanned Grain')
$initialCodecIndex = 0
if ($script:HardwareCapsReady -and -not $script:Av1Available) {
    $codecItems[0] = 'AV1 Main10 + grav1synth（当前硬件不可用）'
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
$btnUploadSubtitle.Enabled = $false
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
if ($script:HardwareCapsReady) {
    $yesNo = @('不可用', '可用')
    $av1Text = $yesNo[[int]$script:Av1Available]
    $av1UhqText = $yesNo[[int]$script:Av1UhqAvailable]
    $hevcText = $yesNo[[int]$script:HevcAvailable]
    $profileNote.Text = "驱动：$($script:HardwareCaps.gpu.driverVersion) · 配置：$($script:HardwareCaps.cacheState)`r`nAV1 Main10：$av1Text · UHQ：$av1UhqText；HEVC/Vulkan：$hevcText；其余参数按实测启用。"
} else {
    $profileNote.Text = "固定依赖：FFmpeg 13.0 / NVENC API 13.0`r`n硬件检测尚未完成，编码启动时会自动重试。"
}
[void]$encodeTable.Controls.Add($profileNote, 0, 13)
$encodeTable.SetColumnSpan($profileNote, 2)
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
$grpGrain.Text = 'Film Grain · AV1 metadata'
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
$pnlAv1.RowCount = 5
$av1LabelCol = New-Object System.Windows.Forms.ColumnStyle
$av1LabelCol.SizeType = [System.Windows.Forms.SizeType]::Absolute
$av1LabelCol.Width = 100
[void]$pnlAv1.ColumnStyles.Add($av1LabelCol)
$av1ValueCol = New-Object System.Windows.Forms.ColumnStyle
$av1ValueCol.SizeType = [System.Windows.Forms.SizeType]::Percent
$av1ValueCol.Width = 100
[void]$pnlAv1.ColumnStyles.Add($av1ValueCol)
for ($i = 0; $i -lt 5; $i++) { Add-RowPercent $pnlAv1 (100 / 5) }

$cmbAv1Method = New-ComboBox @('Film preset（推荐）', 'Photon ISO（高级）') 0
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
$chkChroma.Text = 'Luma + Chroma（默认仅 Luma）'
$chkChroma.Dock = 'Fill'
$chkChroma.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 7, 4, 3, 3

Add-LabeledRow $pnlAv1 0 'Grain 方式' $cmbAv1Method
Add-LabeledRow $pnlAv1 1 'Film 格式' $cmbAv1Format
Add-LabeledRow $pnlAv1 2 'Film stock' $cmbAv1Stock
Add-LabeledRow $pnlAv1 3 'Photon ISO' $numIso
[void]$pnlAv1.Controls.Add($chkChroma, 0, 4)
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
$txtGrainRoot.Text = 'D:\Film_Grain'
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

$cmbHevcPlate = New-ComboBox @('正在扫描 Grain 根目录…') 0
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
$cacheNote.Text = '将递归扫描根目录中的原始 MOV；Cache 自动匹配，缺失时回退 MOV。'
$cacheNote.ForeColor = $ColorMuted
$cacheNote.Dock = 'Fill'
$cacheNote.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$cacheNote.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 8, 0, 2, 0

Add-LabeledRow $pnlHevc 0 'Grain 根目录' $grainRootPanel
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
$toolTip.SetToolTip($btnGrainRoot, '选择 Grain 根目录')
$toolTip.SetToolTip($btnRefreshGrain, '重新扫描根目录中的 .mov 颗粒片')
$toolTip.SetToolTip($btnUploadSubtitle, '为 H.264 上传版烧写硬字幕；默认在下方黑边中距画面下沿 25px、水平居中；尺寸按 1080p 基准等比缩放。')
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
$folderDialog.Description = '选择 HEVC Scanned Grain 根目录'
$folderDialog.ShowNewFolderButton = $false

function Get-SubtitleProbeExe {
    $probeExe = $Ffprobe
    if (Test-Path -LiteralPath $probeExe -PathType Leaf) { return $probeExe }
    $probeCommand = Get-Command 'ffprobe.exe' -ErrorAction SilentlyContinue
    if ($probeCommand) { return $probeCommand.Source }
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
    $dlg.Text = 'H.264 上传版 · 硬字幕'
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
    $note.Text = "默认：huiwen-mincho / 69 / 白色 / 黑色边框与阴影 / Outline 1 / Shadow 1 / 在下方黑边中距画面下沿 25px、水平居中。字号、边距、描边和阴影均以 1920×1080 为基准，实际编码按输出宽度等比缩放。`r`n支持 SRT / ASS / SSA / WebVTT 等文本字幕；PGS/DVD 图形字幕暂不烧写。多文件任务建议使用各视频同名字幕。"
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
    $container = if ($format.format_name) { ([string]$format.format_name).Replace(',', ' / ') } else { '—' }
    $totalRate = Format-MediaBitrate $format.bit_rate
    $formatLine = "时长  $duration · 容器 $container · 总码率 $totalRate"
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
        Set-MediaInfoText ([string]$script:ProbeCache[$cacheKey])
        Update-DeinterlaceUi
        return
    }

    $probeExe = $Ffprobe
    if (-not (Test-Path -LiteralPath $probeExe -PathType Leaf)) {
        $probeCommand = Get-Command 'ffprobe.exe' -ErrorAction SilentlyContinue
        if ($probeCommand) { $probeExe = $probeCommand.Source }
    }
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

function Update-SelectedMediaInfo {
    $selected = @($listFiles.SelectedItems)
    if ($selected.Count -eq 0) {
        Stop-VideoProbe
        Set-MediaInfoText '选择一个视频，可查看编码、码率、分辨率与时长。' $true
    } elseif ($selected.Count -gt 1) {
        Stop-VideoProbe
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
    } catch {
        Stop-VideoProbe
        Set-MediaInfoText ('读取失败：' + $_.Exception.Message) $true
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
            [void]$cmbHevcPlate.Items.Add('Grain 根目录不存在')
            $cmbHevcPlate.SelectedIndex = 0
            $cacheNote.Text = '请选择有效的 Grain 根目录，然后点击 ↻ 刷新。'
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
        $cacheNote.Text = '已扫描到 ' + $script:HevcGrainFiles.Count + ' 个原始 MOV；Cache 自动匹配，缺失时回退 MOV。'
    } catch {
        $script:HevcGrainFiles = @()
        $cmbHevcPlate.Items.Clear()
        [void]$cmbHevcPlate.Items.Add('扫描 Grain 根目录失败')
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

function Update-Av1Controls {
    $presetMode = ($cmbAv1Method.SelectedIndex -eq 0)
    $cmbAv1Format.Enabled = $presetMode
    $cmbAv1Stock.Enabled = $presetMode -and ($cmbAv1Format.SelectedIndex -lt 3)
    $numIso.Enabled = -not $presetMode
    $chkChroma.Enabled = -not $presetMode
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
        $grpGrain.Text = 'Film Grain · AV1 metadata'
    } else {
        $pnlAv1.Visible = $false
        $pnlHevc.Visible = $true
        $pnlHevc.BringToFront()
        $grpGrain.Text = 'Film Grain · HEVC Scanned Grain'
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

function Start-Encoding {
    if (-not (Test-Path -LiteralPath $CoreBat)) {
        Show-Error "找不到 Studio 桥接核心：`r`n$CoreBat"
        return
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
    $selectedGrainPath = $null
    if ($mode -eq 'HEVC') {
        $grainRoot = $txtGrainRoot.Text.Trim()
        if (-not (Test-Path -LiteralPath $grainRoot -PathType Container)) {
            Show-Error "HEVC Grain 根目录不存在：`r`n$grainRoot"
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
    $inner = 'call ' + (Quote-CmdArgument $CoreBat) + ' ' + ($quotedInputs -join ' ') + ' 2>&1'

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $cmdPath
    $psi.Arguments = '/d /s /c "' + $inner + '"'
    $psi.WorkingDirectory = $ScriptRoot
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

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
    $envs['FG_UPLOAD_SUBTITLE'] = if ($chkUpload.Checked -and $script:UploadSubtitle.Enabled) { '1' } else { '0' }
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
        $envs['FG_AV1_GRAIN_MODE'] = if ($cmbAv1Method.SelectedIndex -eq 0) { 'PRESET' } else { 'ISO' }
        $envs['FG_AV1_FORMAT'] = [string]($cmbAv1Format.SelectedIndex + 1)
        $envs['FG_AV1_STOCK'] = [string]($cmbAv1Stock.SelectedIndex + 1)
        $envs['FG_AV1_ISO'] = [string][int]$numIso.Value
        $envs['FG_AV1_CHROMA'] = if ($chkChroma.Checked) { '1' } else { '0' }
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
        '取消 Film Grain 任务',
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

# Events
$btnAdd.Add_Click({
    if ($openDialog.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
        Add-InputFiles $openDialog.FileNames
    }
})

$btnRemove.Add_Click({
    $selected = @($listFiles.SelectedItems)
    foreach ($item in $selected) { $listFiles.Items.Remove($item) }
    Update-FileCount
})

$btnClear.Add_Click({
    $listFiles.Items.Clear()
    Update-FileCount
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
$listFiles.Add_SelectedIndexChanged({ Update-SelectedMediaInfo; Update-DeinterlaceUi })

$cmbCodec.Add_SelectedIndexChanged({ Update-CodecUi; Update-FramingUi })
$cmbDeint.Add_SelectedIndexChanged({ Update-DeinterlaceUi })
$chkCinematic.Add_CheckedChanged({ Update-FramingUi })
$cmbFrameMode.Add_SelectedIndexChanged({ Update-FramingUi })
$cmbBitrate.Add_TextChanged({
    if (-not $script:ChangingCodec -and $cmbCodec.SelectedIndex -ge 0) {
        $script:ModeBitrate[$cmbCodec.SelectedIndex] = $cmbBitrate.Text.Trim()
    }
})
$cmbAv1Method.Add_SelectedIndexChanged({ Update-Av1Controls })
$cmbAv1Format.Add_SelectedIndexChanged({ Update-Av1Controls })

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
    $btnUploadSubtitle.Enabled = $chkUpload.Checked
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
})

$form.Add_FormClosed({ Clear-StudioLutPreview })

Load-BitrateChoices $cmbCodec.SelectedIndex
Update-CodecUi
Update-Av1Controls
Update-DeinterlaceUi
Update-FramingUi
Refresh-RecentLuts
Refresh-FavoriteLuts
Set-LutUi

if ($InputFiles) { Add-InputFiles $InputFiles }

[void]$form.ShowDialog()
