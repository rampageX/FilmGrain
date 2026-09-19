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

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class FilmGrainTaskbarIdentity
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;

        public PROPERTYKEY(Guid formatId, uint propertyId)
        {
            fmtid = formatId;
            pid = propertyId;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct PROPVARIANT
    {
        [FieldOffset(0)]
        public ushort vt;

        [FieldOffset(8)]
        public IntPtr pointerValue;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        uint GetCount(out uint cProps);
        uint GetAt(uint iProp, out PROPERTYKEY pkey);
        uint GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        uint SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        uint Commit();
    }

    [DllImport("shell32.dll", SetLastError = true)]
    public static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appID);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd,
        ref Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    private static PROPVARIANT FromString(string value)
    {
        PROPVARIANT pv = new PROPVARIANT();
        pv.vt = 31;
        pv.pointerValue = Marshal.StringToCoTaskMemUni(value);
        return pv;
    }

    private static void SetString(IPropertyStore store, PROPERTYKEY key, string value)
    {
        PROPVARIANT pv = FromString(value);
        try
        {
            uint hr = store.SetValue(ref key, ref pv);
            if (hr != 0)
                Marshal.ThrowExceptionForHR(unchecked((int)hr));
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }

    public static void ApplyWindowIdentity(IntPtr hwnd, string appId, string iconPath)
    {
        Guid iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
        IPropertyStore store;
        int hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out store);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);

        try
        {
            PROPERTYKEY relaunchIcon = new PROPERTYKEY(
                new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 3);
            PROPERTYKEY appUserModelId = new PROPERTYKEY(
                new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

            SetString(store, relaunchIcon, iconPath + ",0");
            SetString(store, appUserModelId, appId);

            uint commitHr = store.Commit();
            if (commitHr != 0)
                Marshal.ThrowExceptionForHR(unchecked((int)commitHr));
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }
}
'@

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
$AppIconPath = Join-Path $ScriptRoot 'FGS.ico'
$AppUserModelId = 'FilmGrainStudio.FGS'
$CoreBat = Join-Path $ScriptRoot 'FilmGrain_Universal_HEVC_AV1_StudioBridge.bat'
$NoReencodeBat = Join-Path $ScriptRoot 'AV1_Grav1synth_Add_Replace_FilmGrain_NoReencode.bat'
$GrainCacheBat = Join-Path $ScriptRoot 'FilmGrain_MOV_to_HEVC_Lossless_Cache.bat'
$LutPreviewGenerator = Join-Path $PackageRoot '_LUT_Tools\LUT_Preview_Batch_Gallery.ps1'
$LutPreviewDefaultReference = Join-Path $PackageRoot '_LUT_Tools\LUT_Reference_Default.jpg'
$LutPreviewCurrentReference = Join-Path $PackageRoot '_LUT_Tools\LUT_Reference_Current.jpg'
$ConfigScript = Join-Path $ScriptRoot 'FilmGrain_Config.ps1'
if (-not (Test-Path -LiteralPath $ConfigScript -PathType Leaf)) { throw "Film Grain configuration helper not found: $ConfigScript" }
. $ConfigScript
$LanguageScript = Join-Path $ScriptRoot 'FilmGrain_Language.ps1'
if (-not (Test-Path -LiteralPath $LanguageScript -PathType Leaf)) { throw "Film Grain language helper not found: $LanguageScript" }
. $LanguageScript
$script:PathConfig = Get-FilmGrainConfig
$Ffmpeg = [string]$script:PathConfig.FFMPEG
$Ffprobe = [string]$script:PathConfig.FFPROBE
$Grav1synth = [string]$script:PathConfig.GRAV1SYNTH
$DefaultGrainRoot = [string]$script:PathConfig.GRAIN_ROOT
$DefaultAv1GrainTableRoot = Join-Path $PackageRoot '_AV1_Grain_Tables'
$HardwareCapsScript = Join-Path $ScriptRoot 'FilmGrain_Hardware_Caps.ps1'
$HardwareCapsCache = Join-Path $ScriptRoot '_HardwareCaps.json'
$OpenSvpRoot = Join-Path $PackageRoot '_OpenSVPFlow'
$OpenSvpSetupBat = Join-Path $OpenSvpRoot '00_Setup.bat'
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
$script:ModeBitrate = @{ 0 = '5000'; 1 = '6000'; 2 = '7500' }
$script:ModeBitrateAuto = @{ 0 = $true; 1 = $true; 2 = $true }
$script:UpdatingBitrateUi = $false
$script:UploadBitrate = '7500'
$script:UploadBitrateAuto = $true
$script:UpdatingUploadBitrateUi = $false
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
$script:NoReencodeItemText = L 'codec.no_reencode'
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
$script:SfeMaxEngines = 1
$script:Grav1synthVersion = L 'hardware.not_detected'
$script:Grav1synthSfeCompatible = $false
$script:UpdatingSpeedChoices = $false
$script:HevcAvailable = $true
$script:X264Available = $true
$script:H264High10Available = $true
$script:OpenSvpAvailable = $false
$script:SvpAlgo = 13
$script:SvpAnalyse = 'ENCODEGUI'
$script:SvpMaskArea = 100
$script:CinematicCropPerSide = 0
$script:H264High10 = $false
$script:X264RateMode = 'VBR1'
$script:X264Preset = 'faster'
$script:HdrPolicy = 'AUTO'
$script:ToneMapAlgo = 'hable'
$script:UpdatingFramingUi = $false
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
    Label = L 'subtitle.label_off'
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

function Show-AdvancedSettingsDialog {
    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = L 'advanced.title'
    $dlg.StartPosition = 'CenterParent'
    $dlg.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $dlg.MaximizeBox = $false
    $dlg.MinimizeBox = $false
    $dlg.ShowInTaskbar = $false
    $dlg.ClientSize = New-Object System.Drawing.Size -ArgumentList 720, 460
    $dlg.Font = New-UiFont 9

    $tabs = New-Object System.Windows.Forms.TabControl
    $tabs.Dock = 'None'
    $tabs.Location = New-Object System.Drawing.Point -ArgumentList 10, 10
    $tabs.Size = New-Object System.Drawing.Size -ArgumentList 700, 382
    [void]$dlg.Controls.Add($tabs)

    $tabEncode = New-Object System.Windows.Forms.TabPage
    $tabEncode.Text = L 'advanced.tab.encode'
    [void]$tabs.TabPages.Add($tabEncode)

    $tabInterp = New-Object System.Windows.Forms.TabPage
    $tabInterp.Text = L 'advanced.tab.interp'
    [void]$tabs.TabPages.Add($tabInterp)

    $tabHdr = New-Object System.Windows.Forms.TabPage
    $tabHdr.Text = 'HDR'
    [void]$tabs.TabPages.Add($tabHdr)

    $tabOther = New-Object System.Windows.Forms.TabPage
    $tabOther.Text = L 'advanced.tab.other'
    [void]$tabs.TabPages.Add($tabOther)

    $chkAdvH264High10 = New-Object System.Windows.Forms.CheckBox
    $chkAdvH264High10.Text = L 'advanced.high10'
    $chkAdvH264High10.Checked = ($script:H264High10 -and $script:H264High10Available)
    $chkAdvH264High10.Enabled = (-not $script:HardwareCapsReady -or $script:H264High10Available)
    $chkAdvH264High10.Location = New-Object System.Drawing.Point -ArgumentList 28, 32
    $chkAdvH264High10.Size = New-Object System.Drawing.Size -ArgumentList 260, 28
    [void]$tabEncode.Controls.Add($chkAdvH264High10)

    $lblX264RateMode = New-Object System.Windows.Forms.Label
    $lblX264RateMode.Text = L 'advanced.x264_rate'
    $lblX264RateMode.Location = New-Object System.Drawing.Point -ArgumentList 28, 78
    $lblX264RateMode.Size = New-Object System.Drawing.Size -ArgumentList 150, 24
    [void]$tabEncode.Controls.Add($lblX264RateMode)

    $cmbAdvX264RateMode = New-Object System.Windows.Forms.ComboBox
    $cmbAdvX264RateMode.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
    $cmbAdvX264RateMode.Location = New-Object System.Drawing.Point -ArgumentList 190, 74
    $cmbAdvX264RateMode.Size = New-Object System.Drawing.Size -ArgumentList 260, 26
    [void]$cmbAdvX264RateMode.Items.Add((L 'advanced.vbr1'))
    [void]$cmbAdvX264RateMode.Items.Add((L 'advanced.vbr2'))
    [void]$cmbAdvX264RateMode.Items.Add((L 'advanced.vbr3'))
    $cmbAdvX264RateMode.SelectedIndex = if ($script:X264RateMode -eq '3PASS') { 2 } elseif ($script:X264RateMode -eq '2PASS') { 1 } else { 0 }
    [void]$tabEncode.Controls.Add($cmbAdvX264RateMode)

    $lblX264Preset = New-Object System.Windows.Forms.Label
    $lblX264Preset.Text = 'x264 Preset'
    $lblX264Preset.Location = New-Object System.Drawing.Point -ArgumentList 28, 122
    $lblX264Preset.Size = New-Object System.Drawing.Size -ArgumentList 150, 24
    [void]$tabEncode.Controls.Add($lblX264Preset)

    $cmbAdvX264Preset = New-Object System.Windows.Forms.ComboBox
    $cmbAdvX264Preset.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
    $cmbAdvX264Preset.Location = New-Object System.Drawing.Point -ArgumentList 190, 118
    $cmbAdvX264Preset.Size = New-Object System.Drawing.Size -ArgumentList 260, 26
    [void]$cmbAdvX264Preset.Items.Add((L 'advanced.preset_faster'))
    [void]$cmbAdvX264Preset.Items.Add('Medium')
    [void]$cmbAdvX264Preset.Items.Add('Slow')
    $presetIndex = switch ($script:X264Preset) { 'medium' { 1 } 'slow' { 2 } default { 0 } }
    $cmbAdvX264Preset.SelectedIndex = $presetIndex
    [void]$tabEncode.Controls.Add($cmbAdvX264Preset)

    $lblEncodeInfo = New-Object System.Windows.Forms.Label
    $lblEncodeInfo.AutoSize = $false
    $lblEncodeInfo.Location = New-Object System.Drawing.Point -ArgumentList 28, 166
    $lblEncodeInfo.Size = New-Object System.Drawing.Size -ArgumentList 630, 150
    $lblEncodeInfo.ForeColor = $ColorMuted
    $lblEncodeInfo.Text = L 'advanced.encode_info'
    [void]$tabEncode.Controls.Add($lblEncodeInfo)

    $lblAlgo = New-Object System.Windows.Forms.Label
    $lblAlgo.Text = 'SmoothFps Algo'
    $lblAlgo.Location = New-Object System.Drawing.Point -ArgumentList 28, 34
    $lblAlgo.Size = New-Object System.Drawing.Size -ArgumentList 150, 24
    [void]$tabInterp.Controls.Add($lblAlgo)

    $cmbAdvAlgo = New-Object System.Windows.Forms.ComboBox
    $cmbAdvAlgo.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
    $cmbAdvAlgo.Location = New-Object System.Drawing.Point -ArgumentList 190, 30
    $cmbAdvAlgo.Size = New-Object System.Drawing.Size -ArgumentList 220, 26
    foreach ($item in @('1','2','11','13','21','22','23')) { [void]$cmbAdvAlgo.Items.Add($item) }
    $algoIndex = $cmbAdvAlgo.Items.IndexOf([string]$script:SvpAlgo)
    if ($algoIndex -lt 0) { $algoIndex = 3 }
    $cmbAdvAlgo.SelectedIndex = $algoIndex
    [void]$tabInterp.Controls.Add($cmbAdvAlgo)

    $lblAnalyse = New-Object System.Windows.Forms.Label
    $lblAnalyse.Text = 'Analyse Profile'
    $lblAnalyse.Location = New-Object System.Drawing.Point -ArgumentList 28, 82
    $lblAnalyse.Size = New-Object System.Drawing.Size -ArgumentList 150, 24
    [void]$tabInterp.Controls.Add($lblAnalyse)

    $cmbAdvAnalyse = New-Object System.Windows.Forms.ComboBox
    $cmbAdvAnalyse.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
    $cmbAdvAnalyse.Location = New-Object System.Drawing.Point -ArgumentList 190, 78
    $cmbAdvAnalyse.Size = New-Object System.Drawing.Size -ArgumentList 360, 26
    [void]$cmbAdvAnalyse.Items.Add((L 'advanced.analyse_recommended'))
    [void]$cmbAdvAnalyse.Items.Add((L 'advanced.analyse_baseline'))
    if ($script:SvpAnalyse -eq 'BASE') {
        $cmbAdvAnalyse.SelectedIndex = 1
    } else {
        $cmbAdvAnalyse.SelectedIndex = 0
    }
    [void]$tabInterp.Controls.Add($cmbAdvAnalyse)

    $lblMask = New-Object System.Windows.Forms.Label
    $lblMask.Text = 'Artifact Mask Area'
    $lblMask.Location = New-Object System.Drawing.Point -ArgumentList 28, 130
    $lblMask.Size = New-Object System.Drawing.Size -ArgumentList 150, 24
    [void]$tabInterp.Controls.Add($lblMask)

    $numAdvMask = New-Object System.Windows.Forms.NumericUpDown
    $numAdvMask.Location = New-Object System.Drawing.Point -ArgumentList 190, 126
    $numAdvMask.Size = New-Object System.Drawing.Size -ArgumentList 120, 26
    $numAdvMask.Minimum = 0
    $numAdvMask.Maximum = 100
    $numAdvMask.Increment = 5
    $numAdvMask.Value = [decimal]$script:SvpMaskArea
    [void]$tabInterp.Controls.Add($numAdvMask)

    $lblInterpInfo = New-Object System.Windows.Forms.Label
    $lblInterpInfo.AutoSize = $false
    $lblInterpInfo.Location = New-Object System.Drawing.Point -ArgumentList 28, 182
    $lblInterpInfo.Size = New-Object System.Drawing.Size -ArgumentList 610, 92
    $lblInterpInfo.ForeColor = $ColorMuted
    $lblInterpInfo.Text = L 'advanced.interp_info'
    [void]$tabInterp.Controls.Add($lblInterpInfo)

    $btnRecommended = New-Object System.Windows.Forms.Button
    $btnRecommended.Text = L 'advanced.restore_recommended'
    $btnRecommended.Location = New-Object System.Drawing.Point -ArgumentList 190, 292
    $btnRecommended.Size = New-Object System.Drawing.Size -ArgumentList 112, 30
    [void]$tabInterp.Controls.Add($btnRecommended)

    $lblHdrPolicy = New-Object System.Windows.Forms.Label
    $lblHdrPolicy.Text = L 'advanced.hdr_policy'
    $lblHdrPolicy.Location = New-Object System.Drawing.Point -ArgumentList 28, 34
    $lblHdrPolicy.Size = New-Object System.Drawing.Size -ArgumentList 150, 24
    [void]$tabHdr.Controls.Add($lblHdrPolicy)

    $cmbAdvHdrPolicy = New-Object System.Windows.Forms.ComboBox
    $cmbAdvHdrPolicy.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
    $cmbAdvHdrPolicy.Location = New-Object System.Drawing.Point -ArgumentList 190, 30
    $cmbAdvHdrPolicy.Size = New-Object System.Drawing.Size -ArgumentList 330, 26
    [void]$cmbAdvHdrPolicy.Items.Add((L 'advanced.hdr_auto'))
    [void]$cmbAdvHdrPolicy.Items.Add((L 'advanced.hdr_preserve'))
    [void]$cmbAdvHdrPolicy.Items.Add((L 'advanced.hdr_force_sdr'))
    $cmbAdvHdrPolicy.SelectedIndex = switch ($script:HdrPolicy) { 'PRESERVE' { 1 } 'SDR' { 2 } default { 0 } }
    [void]$tabHdr.Controls.Add($cmbAdvHdrPolicy)

    $lblToneMap = New-Object System.Windows.Forms.Label
    $lblToneMap.Text = 'Tone Mapping'
    $lblToneMap.Location = New-Object System.Drawing.Point -ArgumentList 28, 82
    $lblToneMap.Size = New-Object System.Drawing.Size -ArgumentList 150, 24
    [void]$tabHdr.Controls.Add($lblToneMap)

    $cmbAdvToneMap = New-Object System.Windows.Forms.ComboBox
    $cmbAdvToneMap.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
    $cmbAdvToneMap.Location = New-Object System.Drawing.Point -ArgumentList 190, 78
    $cmbAdvToneMap.Size = New-Object System.Drawing.Size -ArgumentList 220, 26
    foreach ($item in @((L 'advanced.hable'),'Mobius','Reinhard','Gamma','Linear','Clip')) { [void]$cmbAdvToneMap.Items.Add($item) }
    $toneIndex = switch ($script:ToneMapAlgo) { 'mobius' { 1 } 'reinhard' { 2 } 'gamma' { 3 } 'linear' { 4 } 'clip' { 5 } default { 0 } }
    $cmbAdvToneMap.SelectedIndex = $toneIndex
    [void]$tabHdr.Controls.Add($cmbAdvToneMap)

    $lblHdrInfo = New-Object System.Windows.Forms.Label
    $lblHdrInfo.AutoSize = $false
    $lblHdrInfo.Location = New-Object System.Drawing.Point -ArgumentList 28, 132
    $lblHdrInfo.Size = New-Object System.Drawing.Size -ArgumentList 620, 150
    $lblHdrInfo.ForeColor = $ColorMuted
    $lblHdrInfo.Text = L 'advanced.hdr_info'
    [void]$tabHdr.Controls.Add($lblHdrInfo)

    $updateHdrAdvancedUi = {
        $enabled = ($cmbAdvHdrPolicy.SelectedIndex -ne 1)
        $cmbAdvToneMap.Enabled = $enabled
        $lblToneMap.Enabled = $enabled
    }
    $cmbAdvHdrPolicy.Add_SelectedIndexChanged($updateHdrAdvancedUi)
    & $updateHdrAdvancedUi

    $lblCropTitle = New-Object System.Windows.Forms.Label
    $lblCropTitle.Text = L 'advanced.crop_title'
    $lblCropTitle.Location = New-Object System.Drawing.Point -ArgumentList 28, 30
    $lblCropTitle.Size = New-Object System.Drawing.Size -ArgumentList 260, 26
    $lblCropTitle.Font = New-UiFont 9 ([System.Drawing.FontStyle]::Bold)
    [void]$tabOther.Controls.Add($lblCropTitle)

    $lblCropValue = New-Object System.Windows.Forms.Label
    $lblCropValue.Text = L 'advanced.crop_value'
    $lblCropValue.Location = New-Object System.Drawing.Point -ArgumentList 28, 82
    $lblCropValue.Size = New-Object System.Drawing.Size -ArgumentList 150, 24
    [void]$tabOther.Controls.Add($lblCropValue)

    $numAdvCrop = New-Object System.Windows.Forms.NumericUpDown
    $numAdvCrop.Location = New-Object System.Drawing.Point -ArgumentList 190, 78
    $numAdvCrop.Size = New-Object System.Drawing.Size -ArgumentList 120, 26
    $numAdvCrop.Minimum = 0
    $numAdvCrop.Maximum = 2000
    $numAdvCrop.Increment = 1
    $numAdvCrop.Value = [decimal]$script:CinematicCropPerSide
    [void]$tabOther.Controls.Add($numAdvCrop)

    $lblCropInfo = New-Object System.Windows.Forms.Label
    $lblCropInfo.AutoSize = $false
    $lblCropInfo.Location = New-Object System.Drawing.Point -ArgumentList 28, 128
    $lblCropInfo.Size = New-Object System.Drawing.Size -ArgumentList 620, 118
    $lblCropInfo.ForeColor = $ColorMuted
    $lblCropInfo.Text = L 'advanced.crop_info'
    [void]$tabOther.Controls.Add($lblCropInfo)

    $btnOk = New-Object System.Windows.Forms.Button
    $btnOk.Text = L 'advanced.ok'
    $btnOk.Location = New-Object System.Drawing.Point -ArgumentList 514, 406
    $btnOk.Size = New-Object System.Drawing.Size -ArgumentList 88, 30
    $btnOk.DialogResult = [System.Windows.Forms.DialogResult]::OK
    [void]$dlg.Controls.Add($btnOk)

    $btnCancelAdv = New-Object System.Windows.Forms.Button
    $btnCancelAdv.Text = L 'advanced.cancel'
    $btnCancelAdv.Location = New-Object System.Drawing.Point -ArgumentList 610, 406
    $btnCancelAdv.Size = New-Object System.Drawing.Size -ArgumentList 88, 30
    $btnCancelAdv.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    [void]$dlg.Controls.Add($btnCancelAdv)

    $btnOk.BringToFront()
    $btnCancelAdv.BringToFront()
    $dlg.AcceptButton = $btnOk
    $dlg.CancelButton = $btnCancelAdv

    $btnRecommended.Add_Click({
        $cmbAdvAlgo.SelectedItem = '13'
        $cmbAdvAnalyse.SelectedIndex = 0
        $numAdvMask.Value = 100
    })

    $result = $dlg.ShowDialog($form)
    if ($result -eq [System.Windows.Forms.DialogResult]::OK) {
        $script:SvpAlgo = [int]([string]$cmbAdvAlgo.SelectedItem)
        if ($cmbAdvAnalyse.SelectedIndex -eq 1) {
            $script:SvpAnalyse = 'BASE'
        } else {
            $script:SvpAnalyse = 'ENCODEGUI'
        }
        $script:SvpMaskArea = [int]$numAdvMask.Value
        $script:CinematicCropPerSide = [int]$numAdvCrop.Value
        $script:H264High10 = ([bool]$chkAdvH264High10.Checked -and $script:H264High10Available)
        $script:X264RateMode = if ($cmbAdvX264RateMode.SelectedIndex -eq 2) { '3PASS' } elseif ($cmbAdvX264RateMode.SelectedIndex -eq 1) { '2PASS' } else { 'VBR1' }
        $script:X264Preset = switch ($cmbAdvX264Preset.SelectedIndex) { 1 { 'medium' } 2 { 'slow' } default { 'faster' } }
        $script:HdrPolicy = switch ($cmbAdvHdrPolicy.SelectedIndex) { 1 { 'PRESERVE' } 2 { 'SDR' } default { 'AUTO' } }
        $script:ToneMapAlgo = switch ($cmbAdvToneMap.SelectedIndex) { 1 { 'mobius' } 2 { 'reinhard' } 3 { 'gamma' } 4 { 'linear' } 5 { 'clip' } default { 'hable' } }
        Update-HdrCompatibilityUi
        Update-InterpolationUi
        Update-SpeedChoices
        Update-FramingUi
        Update-BitrateDisplays
    }

    $dlg.Dispose()
}

function Initialize-HardwareCaps {
    $script:HardwareCaps = $null
    $script:HardwareCapsReady = $false
    $script:Av1Available = $true
    $script:Av1UhqAvailable = $false
    $script:SfeMaxEngines = 1
    $script:Grav1synthVersion = L 'hardware.not_detected'
    $script:Grav1synthSfeCompatible = $false
    $script:HevcAvailable = $true
    $script:X264Available = $true
    $script:H264High10Available = $true
    $script:OpenSvpAvailable = $false

    if (-not (Test-Path -LiteralPath $HardwareCapsScript -PathType Leaf)) { return }
    if (-not (Test-Path -LiteralPath $Ffmpeg -PathType Leaf)) { return }

    try {
        $powerShellExe = Join-Path $PSHOME 'powershell.exe'
        & $powerShellExe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $HardwareCapsScript `
            -FFmpeg $Ffmpeg -GpuIndex 0 -CudaDevice 0 -VulkanDevice 0 `
            -Grav1synth $Grav1synth -CachePath $HardwareCapsCache -Quiet 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $HardwareCapsCache -PathType Leaf)) { return }

        $caps = Get-Content -LiteralPath $HardwareCapsCache -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $caps.caps -or -not $caps.caps.av1 -or -not $caps.caps.hevc) { return }
        $script:HardwareCaps = $caps
        $script:HardwareCapsReady = $true
        $script:Av1Available = [bool]$caps.caps.av1.available
        $script:Av1UhqAvailable = [bool]$caps.caps.av1.uhq
        $script:SfeMaxEngines = 1
        if ($caps.caps.av1.PSObject.Properties.Name -contains 'splitEncodeMaxEngines') {
            $detectedSfeMax = 1
            if ([int]::TryParse([string]$caps.caps.av1.splitEncodeMaxEngines, [ref]$detectedSfeMax) -and $detectedSfeMax -ge 1) {
                $script:SfeMaxEngines = $detectedSfeMax
            }
        }
        if ($caps.grav1synth) {
            if ($caps.grav1synth.version) {
                $script:Grav1synthVersion = [string]$caps.grav1synth.version
            }
            $script:Grav1synthSfeCompatible = [bool]$caps.grav1synth.sfeCompatible
        }
        $script:HevcAvailable = [bool]$caps.caps.hevcPipeline
        $script:X264Available = [bool]$caps.caps.x264Pipeline
        $script:H264High10Available = [bool]$caps.caps.x264.high10
        if (-not $script:H264High10Available) { $script:H264High10 = $false }
        if ($caps.caps.openSvp) { $script:OpenSvpAvailable = [bool]$caps.caps.openSvp.gpu }
    } catch {
        $script:HardwareCaps = $null
        $script:HardwareCapsReady = $false
        $script:Av1Available = $true
        $script:Av1UhqAvailable = $false
        $script:SfeMaxEngines = 1
        $script:Grav1synthVersion = L 'hardware.not_detected'
        $script:Grav1synthSfeCompatible = $false
        $script:HevcAvailable = $true
        $script:X264Available = $true
        $script:H264High10Available = $true
        $script:OpenSvpAvailable = $false
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
    return (L 'hardware.not_detected')
}

function Update-HardwareProfileUi {
    if (-not $statusHardware -or -not $cmbGpu) { return }
    $ffmpegVersion = Get-FFmpegVersionLabel
    if ($script:HardwareCapsReady) {
        $yesNo = @((L 'hardware.unavailable'), (L 'hardware.available'))
        $av1Text = $yesNo[[int]$script:Av1Available]
        $av1UhqText = $yesNo[[int]$script:Av1UhqAvailable]
        $hevcText = $yesNo[[int]$script:HevcAvailable]
        $x264Text = $yesNo[[int]$script:X264Available]
        $x264High10Text = $yesNo[[int]$script:H264High10Available]
        $svpText = $yesNo[[int]$script:OpenSvpAvailable]
        $cacheStateText = switch ([string]$script:HardwareCaps.cacheState) {
            'Detected' { L 'hardware.cache_detected' }
            'Cached'   { L 'hardware.cache_cached' }
            default    { [string]$script:HardwareCaps.cacheState }
        }
        $statusHardware.Text = "GPU $($script:HardwareCaps.gpu.name) · $(L 'hardware.driver') $($script:HardwareCaps.gpu.driverVersion) · FFmpeg $ffmpegVersion · $(L 'hardware.profile') $cacheStateText · AV1 $av1Text · UHQ $av1UhqText · HEVC/Vulkan $hevcText · x264 Grain $x264Text · High10 $x264High10Text · SVPFlow $svpText"
        $cmbGpu.Items.Clear()
        [void]$cmbGpu.Items.Add(([string]$script:HardwareCaps.gpu.name + (L 'gpu.auto_detected')))
        $cmbGpu.SelectedIndex = 0
    } else {
        $statusHardware.Text = "FFmpeg $ffmpegVersion · $(L 'hardware.pending')"
        $cmbGpu.Items.Clear()
        [void]$cmbGpu.Items.Add((L 'gpu.auto_retry'))
        $cmbGpu.SelectedIndex = 0
    }
}

Initialize-HardwareCaps

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Film Grain Studio'
$form.StartPosition = 'CenterScreen'

$initialClientWidth = 1320
$initialMinimumWidth = 1280
try { $initialClientWidth = [int](L 'layout.window_width') } catch {}
try { $initialMinimumWidth = [int](L 'layout.minimum_width') } catch {}
if ($initialClientWidth -lt 1280) { $initialClientWidth = 1280 }
if ($initialMinimumWidth -lt 1200) { $initialMinimumWidth = 1200 }
if ($initialMinimumWidth -gt $initialClientWidth) { $initialMinimumWidth = $initialClientWidth }

$form.ClientSize = New-Object System.Drawing.Size -ArgumentList $initialClientWidth, 960
$form.MinimumSize = New-Object System.Drawing.Size -ArgumentList $initialMinimumWidth, 950
$form.AutoScaleMode = [System.Windows.Forms.AutoScaleMode]::Dpi
$form.Font = New-UiFont 9
$form.AllowDrop = $true

if (Test-Path -LiteralPath $AppIconPath -PathType Leaf) {
    try {
        [void][FilmGrainTaskbarIdentity]::SetCurrentProcessExplicitAppUserModelID($AppUserModelId)
        $form.Icon = New-Object System.Drawing.Icon -ArgumentList $AppIconPath
        [void]$form.Handle
        [FilmGrainTaskbarIdentity]::ApplyWindowIdentity($form.Handle, $AppUserModelId, $AppIconPath)
    } catch {}
}

$root = New-Object System.Windows.Forms.TableLayoutPanel
$root.Dock = 'Fill'
$root.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0
$root.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 0
$root.RowCount = 5
$root.ColumnCount = 1
Add-RowAbsolute $root 68
Add-RowPercent $root 100
Add-RowAbsolute $root 225
Add-RowAbsolute $root 58
Add-RowAbsolute $root 24
[void]$form.Controls.Add($root)

# Bottom application status bar
$statusStrip = New-Object System.Windows.Forms.StatusStrip
$statusStrip.Dock = 'Fill'
$statusStrip.SizingGrip = $false
$statusStrip.BackColor = $ColorSubtle
$statusStrip.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 8, 1, 8, 1
$statusHardware = New-Object System.Windows.Forms.ToolStripStatusLabel
$statusHardware.Spring = $true
$statusHardware.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$statusHardware.ForeColor = $ColorMuted
$statusHardware.Text = L 'hardware.detecting'
[void]$statusStrip.Items.Add($statusHardware)

$statusVersion = New-Object System.Windows.Forms.ToolStripStatusLabel
$statusVersion.Spring = $false
$statusVersion.TextAlign = [System.Drawing.ContentAlignment]::MiddleRight
$statusVersion.ForeColor = $ColorMuted
$statusVersion.Text = 'v4.8.3'
$statusVersion.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 12, 0, 0, 0
[void]$statusStrip.Items.Add($statusVersion)

[void]$root.Controls.Add($statusStrip, 0, 4)

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
$subtitle.Text = L 'app.subtitle'
$subtitle.ForeColor = [System.Drawing.Color]::FromArgb(205, 214, 224)
$subtitle.Font = New-UiFont 9
$subtitle.AutoSize = $true
$subtitle.Location = New-Object System.Drawing.Point -ArgumentList 22, 43
[void]$header.Controls.Add($subtitle)

$script:LanguageChoices = @(Get-FgAvailableLanguages)
$cmbLanguage = New-Object System.Windows.Forms.ComboBox
$cmbLanguage.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbLanguage.Size = New-Object System.Drawing.Size -ArgumentList 116, 28
$cmbLanguage.Anchor = 'Top,Right'
$cmbLanguage.Location = New-Object System.Drawing.Point -ArgumentList 950, 20
foreach ($choice in $script:LanguageChoices) { [void]$cmbLanguage.Items.Add([string]$choice.DisplayName) }
$languageIndex = 0
for ($i = 0; $i -lt $script:LanguageChoices.Count; $i++) {
    if ([string]$script:LanguageChoices[$i].Code -eq [string]$script:FgLanguageCode) { $languageIndex = $i; break }
}
if ($cmbLanguage.Items.Count -gt 0) { $cmbLanguage.SelectedIndex = $languageIndex }
$languageTip = New-Object System.Windows.Forms.ToolTip
$languageTip.SetToolTip($cmbLanguage, (L 'language.tooltip'))
$cmbLanguage.Add_SelectedIndexChanged({
    if ($cmbLanguage.SelectedIndex -lt 0 -or $cmbLanguage.SelectedIndex -ge $script:LanguageChoices.Count) { return }
    $newLanguage = [string]$script:LanguageChoices[$cmbLanguage.SelectedIndex].Code
    if ($newLanguage -eq [string]$script:FgLanguageCode) { return }
    try {
        Set-FgLanguagePreference -Language $newLanguage
        [void][System.Windows.Forms.MessageBox]::Show(
            $form,
            (L 'language.restart_required'),
            (L 'language.title'),
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information
        )
    } catch {
        Show-Error $_.Exception.Message (L 'language.title')
    }
})
[void]$header.Controls.Add($cmbLanguage)

$btnConfig = New-Object System.Windows.Forms.Button
$btnConfig.Text = L 'button.config'
$btnConfig.Size = New-Object System.Drawing.Size -ArgumentList 92, 30
$btnConfig.Anchor = 'Top,Right'
$btnConfig.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$btnConfig.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(110, 126, 145)
$btnConfig.ForeColor = [System.Drawing.Color]::White
$btnConfig.BackColor = [System.Drawing.Color]::FromArgb(58, 72, 90)
$btnConfig.Location = New-Object System.Drawing.Point -ArgumentList 1158, 19
[void]$header.Controls.Add($btnConfig)

$btnAdvanced = New-Object System.Windows.Forms.Button
$btnAdvanced.Text = L 'button.advanced'
$btnAdvanced.Size = New-Object System.Drawing.Size -ArgumentList 92, 30
$btnAdvanced.Anchor = 'Top,Right'
$btnAdvanced.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$btnAdvanced.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(110, 126, 145)
$btnAdvanced.ForeColor = [System.Drawing.Color]::White
$btnAdvanced.BackColor = [System.Drawing.Color]::FromArgb(58, 72, 90)
$btnAdvanced.Location = New-Object System.Drawing.Point -ArgumentList 1074, 19
[void]$header.Controls.Add($btnAdvanced)

$baseline = New-Object System.Windows.Forms.Label
$baseline.Text = L 'app.core'
$baseline.ForeColor = [System.Drawing.Color]::FromArgb(205, 214, 224)
$baseline.AutoSize = $true
$baseline.Anchor = 'Top,Right'
$baseline.Location = New-Object System.Drawing.Point -ArgumentList 955, 27
$header.Add_Resize({
    $btnConfig.Left = $header.ClientSize.Width - $btnConfig.Width - 20
    $btnAdvanced.Left = $btnConfig.Left - $btnAdvanced.Width - 8
    $cmbLanguage.Left = $btnAdvanced.Left - $cmbLanguage.Width - 8
    $baseline.Left = $cmbLanguage.Left - $baseline.Width - 18
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
Add-ColumnPercent $main 32
Add-ColumnPercent $main 35
Add-ColumnPercent $main 33
[void]$root.Controls.Add($main, 0, 1)

# Input group
$grpInput = New-Object System.Windows.Forms.GroupBox
$grpInput.Text = L 'input.group'
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
$btnAdd.Text = L 'button.add'
$btnAdd.Size = New-Object System.Drawing.Size -ArgumentList 92, 29
$btnRemove = New-Object System.Windows.Forms.Button
$btnRemove.Text = L 'button.remove'
$btnRemove.Size = New-Object System.Drawing.Size -ArgumentList 82, 29
$btnClear = New-Object System.Windows.Forms.Button
$btnClear.Text = L 'button.clear'
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
$listFiles.ShowItemToolTips = $true
[void]$listFiles.Columns.Add((L 'input.column.filename'), 178)
[void]$listFiles.Columns.Add((L 'input.column.size'), 72)
[void]$listFiles.Columns.Add((L 'input.column.directory'), 260)
[void]$inputLayout.Controls.Add($listFiles, 0, 1)

$grpMediaInfo = New-Object System.Windows.Forms.GroupBox
$grpMediaInfo.Text = L 'input.info_group'
$grpMediaInfo.Dock = 'Fill'
$grpMediaInfo.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0, 5, 0, 3

$lblMediaInfo = New-Object System.Windows.Forms.Label
$lblMediaInfo.Dock = 'Fill'
$lblMediaInfo.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 7, 4, 7, 3
$lblMediaInfo.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$lblMediaInfo.ForeColor = $ColorMuted
$lblMediaInfo.Font = New-UiFont 8.5
$lblMediaInfo.AutoEllipsis = $true
$lblMediaInfo.Text = L 'input.info_prompt'
[void]$grpMediaInfo.Controls.Add($lblMediaInfo)
[void]$inputLayout.Controls.Add($grpMediaInfo, 0, 2)

$inputNote = New-Object System.Windows.Forms.Label
$inputNote.Dock = 'Fill'
$inputNote.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$inputNote.ForeColor = $ColorMuted
$inputNote.Text = L 'input.note'
[void]$inputLayout.Controls.Add($inputNote, 0, 3)
[void]$main.Controls.Add($grpInput, 0, 0)

# Shared encoding group
$grpEncode = New-Object System.Windows.Forms.GroupBox
$grpEncode.Text = L 'encode.group'
$grpEncode.Dock = 'Fill'
$grpEncode.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 6, 0, 6, 0

$encodeTable = New-Object System.Windows.Forms.TableLayoutPanel
$encodeTable.Dock = 'Fill'
$encodeTable.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 5, 7, 5, 5
$encodeTable.ColumnCount = 2
$encodeTable.RowCount = 16
$encodeTable.ColumnStyles.Clear()
$labelColumn = New-Object System.Windows.Forms.ColumnStyle
$labelColumn.SizeType = [System.Windows.Forms.SizeType]::Absolute
$labelColumn.Width = 112
[void]$encodeTable.ColumnStyles.Add($labelColumn)
$valueColumn = New-Object System.Windows.Forms.ColumnStyle
$valueColumn.SizeType = [System.Windows.Forms.SizeType]::Percent
$valueColumn.Width = 100
[void]$encodeTable.ColumnStyles.Add($valueColumn)
for ($i = 0; $i -lt 15; $i++) { Add-RowAbsolute $encodeTable 34 }
Add-RowPercent $encodeTable 100
[void]$grpEncode.Controls.Add($encodeTable)

$codecItems = @((L 'codec.av1_default'), (L 'codec.hevc_scan'), (L 'codec.x264_default'))
$initialCodecIndex = 0
if ($script:HardwareCapsReady -and -not $script:Av1Available) {
    $codecItems[0] = L 'codec.av1_unavailable'
    if ($script:HevcAvailable) { $initialCodecIndex = 1 } else { $initialCodecIndex = 2 }
}
if ($script:HardwareCapsReady -and -not $script:X264Available) {
    $codecItems[2] = L 'codec.x264_unavailable'
}
$cmbCodec = New-ComboBox $codecItems $initialCodecIndex
$script:LastCodecIndex = $initialCodecIndex
$cmbContainer = New-ComboBox @((L 'container.mp4'), (L 'container.mkv')) 0
$cmbSpeed = New-ComboBox @((L 'speed.fast'), (L 'speed.standard')) 0

$chkSfe = New-Object System.Windows.Forms.CheckBox
$chkSfe.Text = L 'encode.sfe'
$chkSfe.Checked = $false
$chkSfe.Enabled = $false
$chkSfe.AutoSize = $true
$chkSfe.Dock = 'Fill'
$chkSfe.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 4, 7, 3, 3

$sfeToolTip = New-Object System.Windows.Forms.ToolTip
$sfeToolTip.SetToolTip($chkSfe, 'NVENC: Split Frame Encoding (SFE)')

$cmbBitrate = New-Object System.Windows.Forms.ComboBox
$cmbBitrate.Dock = 'Fill'
$cmbBitrate.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDown
$cmbBitrate.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 4, 5, 3, 5

$chkBitrateAuto = New-Object System.Windows.Forms.CheckBox
$chkBitrateAuto.Text = L 'encode.auto'
$chkBitrateAuto.Checked = $true
$chkBitrateAuto.AutoSize = $true
$chkBitrateAuto.Dock = 'Fill'
$chkBitrateAuto.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 3, 7, 3, 3

$chkUploadHighMotion = New-Object System.Windows.Forms.CheckBox
$chkUploadHighMotion.Text = L 'encode.high_motion'
$chkUploadHighMotion.Checked = $false
$chkUploadHighMotion.AutoSize = $false
$chkUploadHighMotion.Dock = 'Fill'
$chkUploadHighMotion.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 3, 7, 3, 3

$bitratePanel = New-Object System.Windows.Forms.TableLayoutPanel
$bitratePanel.Dock = 'Fill'
$bitratePanel.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0
$bitratePanel.ColumnCount = 3
$bitratePanel.RowCount = 1
$bitrateColValue = New-Object System.Windows.Forms.ColumnStyle
$bitrateColValue.SizeType = [System.Windows.Forms.SizeType]::Percent
$bitrateColValue.Width = 62
[void]$bitratePanel.ColumnStyles.Add($bitrateColValue)
$bitrateColAuto = New-Object System.Windows.Forms.ColumnStyle
$bitrateColAuto.SizeType = [System.Windows.Forms.SizeType]::Absolute
$bitrateColAuto.Width = 62
[void]$bitratePanel.ColumnStyles.Add($bitrateColAuto)
$bitrateColMotion = New-Object System.Windows.Forms.ColumnStyle
$bitrateColMotion.SizeType = [System.Windows.Forms.SizeType]::Absolute
$bitrateColMotion.Width = 106
[void]$bitratePanel.ColumnStyles.Add($bitrateColMotion)
[void]$bitratePanel.Controls.Add($cmbBitrate, 0, 0)
[void]$bitratePanel.Controls.Add($chkBitrateAuto, 1, 0)
[void]$bitratePanel.Controls.Add($chkUploadHighMotion, 2, 0)

$cmbFps = New-ComboBox @((L 'fps.auto_interlaced'), (L 'fps.keep_source')) 0

$chkInterpolation = New-Object System.Windows.Forms.CheckBox
$chkInterpolation.Text = L 'encode.enable'
$chkInterpolation.Checked = $false
$chkInterpolation.AutoSize = $true
$chkInterpolation.Dock = 'None'
$chkInterpolation.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 4, 6, 8, 3

$cmbInterpolationMode = New-Object System.Windows.Forms.ComboBox
$cmbInterpolationMode.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbInterpolationMode.Items.Add((L 'interp.smooth')) | Out-Null
$cmbInterpolationMode.Items.Add((L 'interp.adaptive')) | Out-Null
$cmbInterpolationMode.SelectedIndex = 0
$cmbInterpolationMode.Width = 150
$cmbInterpolationMode.Enabled = $false
$cmbInterpolationMode.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 4, 3, 3, 3

$interpolationPanel = New-Object System.Windows.Forms.FlowLayoutPanel
$interpolationPanel.Dock = 'Fill'
$interpolationPanel.FlowDirection = 'LeftToRight'
$interpolationPanel.WrapContents = $false
$interpolationPanel.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0
$interpolationPanel.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 0
[void]$interpolationPanel.Controls.Add($chkInterpolation)
[void]$interpolationPanel.Controls.Add($cmbInterpolationMode)

$cmbDeint = New-ComboBox @((L 'deint.auto'), (L 'deint.off')) 0
$cmbDeintMethod = New-ComboBox @((L 'deint.vulkan'), (L 'deint.cuda'), (L 'deint.w3fdif')) 0

$gpuDisplay = L 'gpu.auto_retry'
if ($script:HardwareCapsReady) { $gpuDisplay = [string]$script:HardwareCaps.gpu.name + (L 'gpu.auto_detected') }
$cmbGpu = New-ComboBox @($gpuDisplay) 0
$cmbGpu.Enabled = $false

$chkCinematic = New-Object System.Windows.Forms.CheckBox
$chkCinematic.Text = L 'cinematic.enable'
$chkCinematic.Checked = $true
$chkCinematic.Dock = 'Fill'
$chkCinematic.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 7, 7, 3, 3

$cmbFrameMode = New-ComboBox @(
    (L 'cinematic.letterbox'),
    (L 'cinematic.crop')
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

$btnUploadSubtitle = New-Object System.Windows.Forms.Button
$btnUploadSubtitle.Text = L 'button.subtitle'
$btnUploadSubtitle.Dock = 'Fill'
$btnUploadSubtitle.Enabled = $true
$btnUploadSubtitle.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 3, 4, 8, 4

$uploadExtraPanel = New-Object System.Windows.Forms.TableLayoutPanel
$uploadExtraPanel.Dock = 'Fill'
$uploadExtraPanel.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0, 0, 0, 0
$uploadExtraPanel.ColumnCount = 1
$uploadExtraPanel.RowCount = 1
$uploadExtraSubCol = New-Object System.Windows.Forms.ColumnStyle
$uploadExtraSubCol.SizeType = [System.Windows.Forms.SizeType]::Percent
$uploadExtraSubCol.Width = 100
[void]$uploadExtraPanel.ColumnStyles.Add($uploadExtraSubCol)
[void]$uploadExtraPanel.Controls.Add($btnUploadSubtitle, 0, 0)

$uploadPanel = New-Object System.Windows.Forms.TableLayoutPanel
$uploadPanel.Dock = 'Fill'
$uploadPanel.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0, 0, 0, 0
$uploadPanel.ColumnCount = 3
$uploadPanel.RowCount = 1
$uploadCheckCol = New-Object System.Windows.Forms.ColumnStyle
$uploadCheckCol.SizeType = [System.Windows.Forms.SizeType]::Percent
$uploadCheckCol.Width = 52
[void]$uploadPanel.ColumnStyles.Add($uploadCheckCol)
$uploadRateCol = New-Object System.Windows.Forms.ColumnStyle
$uploadRateCol.SizeType = [System.Windows.Forms.SizeType]::Percent
$uploadRateCol.Width = 48
[void]$uploadPanel.ColumnStyles.Add($uploadRateCol)
$uploadAutoCol = New-Object System.Windows.Forms.ColumnStyle
$uploadAutoCol.SizeType = [System.Windows.Forms.SizeType]::Absolute
$uploadAutoCol.Width = 62
[void]$uploadPanel.ColumnStyles.Add($uploadAutoCol)

$chkUpload = New-Object System.Windows.Forms.CheckBox
$chkUpload.Text = L 'encode.upload_h264'
$chkUpload.Dock = 'Fill'
$chkUpload.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 7, 4, 3, 3

$cmbUploadBitrate = New-Object System.Windows.Forms.ComboBox
$cmbUploadBitrate.Dock = 'Fill'
$cmbUploadBitrate.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDown
$cmbUploadBitrate.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 3, 5, 3, 5
foreach ($value in @('3000','3500','4000','5000','6000','7000','7500','8000','9000','10000','11000','12000','15000','18000','20000','22000','30000')) { [void]$cmbUploadBitrate.Items.Add($value) }
$cmbUploadBitrate.Text = $script:UploadBitrate
$cmbUploadBitrate.Enabled = $false

$chkUploadBitrateAuto = New-Object System.Windows.Forms.CheckBox
$chkUploadBitrateAuto.Text = L 'encode.auto'
$chkUploadBitrateAuto.Checked = $true
$chkUploadBitrateAuto.AutoSize = $true
$chkUploadBitrateAuto.Dock = 'Fill'
$chkUploadBitrateAuto.Enabled = $false
$chkUploadBitrateAuto.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 3, 7, 3, 3

[void]$uploadPanel.Controls.Add($chkUpload, 0, 0)
[void]$uploadPanel.Controls.Add($cmbUploadBitrate, 1, 0)
[void]$uploadPanel.Controls.Add($chkUploadBitrateAuto, 2, 0)

$frameHelp = New-Object System.Windows.Forms.Label
$frameHelp.Dock = 'Fill'
$frameHelp.AutoEllipsis = $true
$frameHelp.ForeColor = $ColorMuted
$frameHelp.TextAlign = [System.Drawing.ContentAlignment]::TopLeft
$frameHelp.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 8, 6, 8, 0
$frameHelp.Text = L 'encode.frame_help'

Add-LabeledRow $encodeTable 0 (L 'encode.method') $cmbCodec
Add-LabeledRow $encodeTable 1 (L 'encode.container') $cmbContainer
Add-LabeledRow $encodeTable 2 (L 'encode.speed') $cmbSpeed
[void]$encodeTable.Controls.Add($chkSfe, 1, 3)
Add-LabeledRow $encodeTable 4 (L 'encode.bitrate') $bitratePanel
Add-LabeledRow $encodeTable 5 (L 'encode.fps') $cmbFps
Add-LabeledRow $encodeTable 6 (L 'encode.interpolation') $interpolationPanel
Add-LabeledRow $encodeTable 7 (L 'encode.deinterlace') $cmbDeint
Add-LabeledRow $encodeTable 8 (L 'encode.deinterlace_method') $cmbDeintMethod
Add-LabeledRow $encodeTable 9 (L 'encode.gpu') $cmbGpu
[void]$encodeTable.Controls.Add($cinematicPanel, 0, 10)
$encodeTable.SetColumnSpan($cinematicPanel, 2)
Add-LabeledRow $encodeTable 11 (L 'encode.framing') $cmbFrameMode
[void]$encodeTable.Controls.Add($uploadPanel, 0, 12)
$encodeTable.SetColumnSpan($uploadPanel, 2)
[void]$encodeTable.Controls.Add($uploadExtraPanel, 0, 13)
$encodeTable.SetColumnSpan($uploadExtraPanel, 2)
[void]$encodeTable.Controls.Add($frameHelp, 0, 14)
$encodeTable.SetColumnSpan($frameHelp, 2)

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
$grpGrain.Text = L 'grain.av1_metadata'
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

$cmbAv1Method = New-ComboBox @((L 'grain.method.film'), (L 'grain.method.iso'), (L 'grain.method.table'), (L 'grain.method.digital30'), (L 'grain.method.digital55'), (L 'grain.method.fgsim_light'), (L 'grain.method.fgsim_medium'), (L 'grain.method.fgsim_heavy')) 0
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
$chkChroma.Text = L 'grain.chroma'
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

$cmbAv1GrainTable = New-ComboBox @((L 'grain.scanning_table')) 0
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

$av1ProcStrengthPanel = New-Object System.Windows.Forms.TableLayoutPanel
$av1ProcStrengthPanel.Dock = 'Fill'
$av1ProcStrengthPanel.ColumnCount = 2
$av1ProcStrengthPanel.RowCount = 1
$av1ProcStrengthPanel.Visible = $false
$av1ProcTrackCol = New-Object System.Windows.Forms.ColumnStyle
$av1ProcTrackCol.SizeType = [System.Windows.Forms.SizeType]::Percent
$av1ProcTrackCol.Width = 100
[void]$av1ProcStrengthPanel.ColumnStyles.Add($av1ProcTrackCol)
$av1ProcLabelCol = New-Object System.Windows.Forms.ColumnStyle
$av1ProcLabelCol.SizeType = [System.Windows.Forms.SizeType]::Absolute
$av1ProcLabelCol.Width = 72
[void]$av1ProcStrengthPanel.ColumnStyles.Add($av1ProcLabelCol)

$trackAv1ProcStrength = New-Object System.Windows.Forms.TrackBar
$trackAv1ProcStrength.Minimum = 10
$trackAv1ProcStrength.Maximum = 100
$trackAv1ProcStrength.Value = 55
$trackAv1ProcStrength.TickFrequency = 10
$trackAv1ProcStrength.SmallChange = 1
$trackAv1ProcStrength.LargeChange = 5
$trackAv1ProcStrength.TickStyle = [System.Windows.Forms.TickStyle]::BottomRight
$trackAv1ProcStrength.Dock = 'Fill'
$trackAv1ProcStrength.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0, 1, 0, 0
$lblAv1ProcStrength = New-Object System.Windows.Forms.Label
$lblAv1ProcStrength.Text = '0.55'
$lblAv1ProcStrength.Dock = 'Fill'
$lblAv1ProcStrength.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
[void]$av1ProcStrengthPanel.Controls.Add($trackAv1ProcStrength, 0, 0)
[void]$av1ProcStrengthPanel.Controls.Add($lblAv1ProcStrength, 1, 0)

Add-LabeledRow $pnlAv1 0 (L 'grain.method') $cmbAv1Method
Add-LabeledRow $pnlAv1 1 (L 'grain.format') $cmbAv1Format
Add-LabeledRow $pnlAv1 2 (L 'grain.stock') $cmbAv1Stock
Add-LabeledRow $pnlAv1 3 (L 'grain.iso') $numIso
Add-LabeledRow $pnlAv1 4 'Grain Table' $av1TablePanel
[void]$pnlAv1.Controls.Add($chkChroma, 0, 5)
$pnlAv1.SetColumnSpan($chkChroma, 2)
[void]$pnlAv1.Controls.Add($av1ProcStrengthPanel, 0, 5)
$pnlAv1.SetColumnSpan($av1ProcStrengthPanel, 2)
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

$cmbHevcPlate = New-ComboBox @((L 'grain.scanning_root')) 0
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

$hevcProcStrengthPanel = New-Object System.Windows.Forms.TableLayoutPanel
$hevcProcStrengthPanel.Dock = 'Fill'
$hevcProcStrengthPanel.ColumnCount = 2
$hevcProcStrengthPanel.RowCount = 1
$hevcProcStrengthPanel.Visible = $false
$hevcProcTrackCol = New-Object System.Windows.Forms.ColumnStyle
$hevcProcTrackCol.SizeType = [System.Windows.Forms.SizeType]::Percent
$hevcProcTrackCol.Width = 100
[void]$hevcProcStrengthPanel.ColumnStyles.Add($hevcProcTrackCol)
$hevcProcLabelCol = New-Object System.Windows.Forms.ColumnStyle
$hevcProcLabelCol.SizeType = [System.Windows.Forms.SizeType]::Absolute
$hevcProcLabelCol.Width = 72
[void]$hevcProcStrengthPanel.ColumnStyles.Add($hevcProcLabelCol)

$trackHevcProcStrength = New-Object System.Windows.Forms.TrackBar
$trackHevcProcStrength.Minimum = 10
$trackHevcProcStrength.Maximum = 100
$trackHevcProcStrength.Value = 55
$trackHevcProcStrength.TickFrequency = 10
$trackHevcProcStrength.SmallChange = 1
$trackHevcProcStrength.LargeChange = 5
$trackHevcProcStrength.TickStyle = [System.Windows.Forms.TickStyle]::BottomRight
$trackHevcProcStrength.Dock = 'Fill'
$trackHevcProcStrength.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0, 1, 0, 0
$lblHevcProcStrength = New-Object System.Windows.Forms.Label
$lblHevcProcStrength.Text = '0.55'
$lblHevcProcStrength.Dock = 'Fill'
$lblHevcProcStrength.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
[void]$hevcProcStrengthPanel.Controls.Add($trackHevcProcStrength, 0, 0)
[void]$hevcProcStrengthPanel.Controls.Add($lblHevcProcStrength, 1, 0)

$cacheNote = New-Object System.Windows.Forms.Label
$cacheNote.Text = L 'grain.root_note'
$cacheNote.ForeColor = $ColorMuted
$cacheNote.Dock = 'Fill'
$cacheNote.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$cacheNote.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 8, 0, 2, 0

Add-LabeledRow $pnlHevc 0 (L 'grain.root') $grainRootPanel
Add-LabeledRow $pnlHevc 1 (L 'grain.method') $cmbHevcPlate
Add-LabeledRow $pnlHevc 2 (L 'grain.strength') $hevcStrengthPanel
[void]$pnlHevc.Controls.Add($hevcProcStrengthPanel, 1, 2)
[void]$pnlHevc.Controls.Add($cacheNote, 0, 3)
$pnlHevc.SetColumnSpan($cacheNote, 2)
[void]$grainHost.Controls.Add($pnlHevc)


# Unified presentation; existing controls remain the parameter model used by the encoder.
$script:UpdatingGrainUi = $false
$script:UnifiedFgsimIndex = 1
$script:GrainFileSignature = ''
$grainHost.Controls.Clear()
$grainUi = New-Object System.Windows.Forms.TableLayoutPanel
$grainUi.Dock = 'Fill'
$grainUi.ColumnCount = 1
$grainUi.RowCount = 2
Add-RowAbsolute $grainUi 34
Add-RowPercent $grainUi 100
[void]$grainHost.Controls.Add($grainUi)
$cmbGrainMode = New-ComboBox @((L 'grain.mode.film'),(L 'grain.mode.iso'),(L 'grain.mode.table'),(L 'grain.mode.digital'),(L 'grain.mode.fgsim')) 0
$cmbGrainMode.DropDownWidth = 390
[void]$grainUi.Controls.Add($cmbGrainMode,0,0)
$grainContent = New-Object System.Windows.Forms.Panel
$grainContent.Dock = 'Fill'
[void]$grainUi.Controls.Add($grainContent,0,1)
# Preserve the native AV1 controls and only suppress their duplicate method selector.
$pnlAv1.RowStyles[0].SizeType = [System.Windows.Forms.SizeType]::Absolute
$pnlAv1.RowStyles[0].Height = 0
foreach ($control in $pnlAv1.Controls) {
    if ($pnlAv1.GetRow($control) -eq 0) { $control.Visible = $false }
}
[void]$grainContent.Controls.Add($pnlAv1)
$pixelScroll = New-Object System.Windows.Forms.Panel
$pixelScroll.Dock = 'Fill'
$pixelScroll.AutoScroll = $true
[void]$grainContent.Controls.Add($pixelScroll)
$pixelOptions = New-Object System.Windows.Forms.TableLayoutPanel
$pixelOptions.Dock = 'Top'
$pixelOptions.AutoSize = $true
$pixelOptions.AutoSizeMode = 'GrowAndShrink'
$pixelOptions.ColumnCount = 1
$pixelOptions.RowCount = 4
for ($i=0; $i -lt 4; $i++) {
    [void]$pixelOptions.RowStyles.Add((New-Object System.Windows.Forms.RowStyle -ArgumentList ([System.Windows.Forms.SizeType]::AutoSize)))
}
[void]$pixelScroll.Controls.Add($pixelOptions)
$plateOptions = New-Object System.Windows.Forms.TableLayoutPanel
$plateOptions.Dock = 'Top'
$plateOptions.Height = 68
$plateOptions.ColumnCount = 1
$plateOptions.RowCount = 2
Add-RowAbsolute $plateOptions 34
Add-RowAbsolute $plateOptions 34
[void]$plateOptions.Controls.Add($grainRootPanel,0,0)
$cmbGrainPlateFile = New-ComboBox @((L 'grain.plate_missing')) 0
$cmbGrainPlateFile.DropDownWidth = 560
[void]$plateOptions.Controls.Add($cmbGrainPlateFile,0,1)
[void]$pixelOptions.Controls.Add($plateOptions,0,0)
$strengthUi = New-Object System.Windows.Forms.TableLayoutPanel
$strengthUi.Dock = 'Top'
$strengthUi.Height = 58
$strengthUi.ColumnCount = 2
$strengthUi.RowCount = 2
Add-ColumnPercent $strengthUi 65
Add-ColumnPercent $strengthUi 35
Add-RowAbsolute $strengthUi 22
Add-RowAbsolute $strengthUi 36
$lblFilmGrainStrength = New-Object System.Windows.Forms.Label
$lblFilmGrainStrength.Text = 'Film Grain Strength'
$lblFilmGrainStrength.Dock = 'Fill'
[void]$strengthUi.Controls.Add($lblFilmGrainStrength,0,0)
$strengthUi.SetColumnSpan($lblFilmGrainStrength,2)
$trackFilmGrainStrength = New-Object System.Windows.Forms.TrackBar
$trackFilmGrainStrength.Minimum = 0
$trackFilmGrainStrength.Maximum = 100
$trackFilmGrainStrength.Dock = 'Fill'
$trackFilmGrainStrength.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 0
$trackFilmGrainStrength.SmallChange = 1
$lblFilmGrainValue = New-Object System.Windows.Forms.Label
$lblFilmGrainValue.Dock = 'Fill'
$lblFilmGrainValue.TextAlign = 'MiddleLeft'
[void]$strengthUi.Controls.Add($trackFilmGrainStrength,0,1)
[void]$strengthUi.Controls.Add($lblFilmGrainValue,1,1)
[void]$pixelOptions.Controls.Add($strengthUi,0,1)
$lblHevcGrainHint = New-Object System.Windows.Forms.Label
$lblHevcGrainHint.Dock = 'Top'
$lblHevcGrainHint.Height = 62
$lblHevcGrainHint.ForeColor = $ColorMuted
$lblHevcGrainHint.Text = L 'grain.fgsim_hint'
[void]$pixelOptions.Controls.Add($lblHevcGrainHint,0,3)

function Get-VisibleGrainKind {
    if ($cmbCodec.SelectedIndex -eq 0 -or $cmbCodec.SelectedIndex -eq 3) {
        if ($cmbAv1Method.SelectedIndex -lt 3) { return 'NATIVE' }
        if ($cmbAv1Method.SelectedIndex -lt 5) { return 'DIGITAL' }
        return 'FGSIM'
    }
    if ($cmbHevcPlate.SelectedIndex -lt 2) { return 'DIGITAL' }
    if ($cmbHevcPlate.SelectedIndex -lt 5) { return 'FGSIM' }
    return 'PLATE'
}
function Update-FgsimControls {
    if ($script:UpdatingGrainUi) { return }
    $script:UpdatingGrainUi = $true
    try {
        $av1 = ($cmbCodec.SelectedIndex -eq 0 -or $cmbCodec.SelectedIndex -eq 3)
        $kind = Get-VisibleGrainKind
        $modeItems = if ($av1) { @((L 'grain.mode.film'),(L 'grain.mode.iso'),(L 'grain.mode.table'),(L 'grain.mode.digital'),(L 'grain.mode.fgsim')) } else { @((L 'grain.mode.digital'),(L 'grain.mode.plate'),(L 'grain.mode.fgsim')) }
        if (($cmbGrainMode.Items -join '|') -ne ($modeItems -join '|')) {
            $cmbGrainMode.Items.Clear()
            $cmbGrainMode.Items.AddRange([object[]]$modeItems)
        }
        $cmbGrainMode.SelectedIndex = if ($av1) {
            if ($kind -eq 'NATIVE') { $cmbAv1Method.SelectedIndex } elseif ($kind -eq 'DIGITAL') { 3 } else { 4 }
        } else { if ($kind -eq 'DIGITAL') { 0 } elseif ($kind -eq 'PLATE') { 1 } else { 2 } }
        $pnlAv1.Visible = ($kind -eq 'NATIVE')
        $pixelScroll.Visible = ($kind -ne 'NATIVE')
        $plateOptions.Visible = ($kind -eq 'PLATE')
        $hevcFgsim = ($cmbCodec.SelectedIndex -eq 1 -and $kind -eq 'FGSIM')
        Update-FgsimBitrateUi
        $lblHevcGrainHint.Visible = $hevcFgsim
        $rightLayout.RowStyles[0].Height = 38
        $rightLayout.RowStyles[1].Height = 62
        if ($kind -eq 'NATIVE') { $pnlAv1.BringToFront(); return }
        $pixelScroll.BringToFront()
        # Temporarily expand the range before setting the mode-specific range/value.
        $trackFilmGrainStrength.Minimum = 0
        $trackFilmGrainStrength.Maximum = 100
        if ($kind -eq 'DIGITAL') {
            $trackFilmGrainStrength.Value = $trackHevcProcStrength.Value
            $trackFilmGrainStrength.Minimum = 10
            $trackFilmGrainStrength.TickFrequency = 10
            $trackFilmGrainStrength.LargeChange = 5
            $lblFilmGrainValue.Text = [string]::Format([Globalization.CultureInfo]::InvariantCulture,'{0:0.00}',($trackHevcProcStrength.Value/100.0))
        } elseif ($kind -eq 'FGSIM') {
            $script:UnifiedFgsimIndex = if ($av1) { $cmbAv1Method.SelectedIndex - 5 } else { $cmbHevcPlate.SelectedIndex - 2 }
            $trackFilmGrainStrength.Value = $script:UnifiedFgsimIndex
            $trackFilmGrainStrength.Maximum = 2
            $trackFilmGrainStrength.TickFrequency = 1
            $trackFilmGrainStrength.LargeChange = 1
            $lblFilmGrainValue.Text = @('Light / 0.10','Medium / 0.20','Heavy / 0.30')[$script:UnifiedFgsimIndex]
        } else {
            $trackFilmGrainStrength.Value = $trackHevcStrength.Value
            $trackFilmGrainStrength.Maximum = 3
            $trackFilmGrainStrength.TickFrequency = 1
            $trackFilmGrainStrength.LargeChange = 1
            $lblFilmGrainValue.Text = @('65%','75%','85%','100%')[$trackHevcStrength.Value]
            $signature = $script:HevcGrainFiles -join '|'
            if ($script:GrainFileSignature -ne $signature) {
                $cmbGrainPlateFile.Items.Clear()
                for ($i=5; $i -lt $script:HevcGrainFiles.Count; $i++) {
                    [void]$cmbGrainPlateFile.Items.Add([string]$cmbHevcPlate.Items[$i])
                }
                $script:GrainFileSignature = $signature
            }
            $cmbGrainPlateFile.SelectedIndex = $cmbHevcPlate.SelectedIndex - 5
        }
    } finally { $script:UpdatingGrainUi = $false }
}
$cmbGrainMode.Add_SelectedIndexChanged({
    if ($script:UpdatingGrainUi) { return }
    $av1 = ($cmbCodec.SelectedIndex -eq 0 -or $cmbCodec.SelectedIndex -eq 3)
    $selection = $cmbGrainMode.SelectedIndex
    # Changing the visible mode must not reset an already adjusted Fast Noise strength.
    $script:UpdatingProcStrengthUi = $true
    try {
        if ($av1) {
            $cmbAv1Method.SelectedIndex = if ($selection -lt 3) { $selection } elseif ($selection -eq 3) { 4 } else { 5 + $script:UnifiedFgsimIndex }
        } elseif ($selection -eq 0) { $cmbHevcPlate.SelectedIndex = 1 }
        elseif ($selection -eq 2) { $cmbHevcPlate.SelectedIndex = 2 + $script:UnifiedFgsimIndex }
        elseif ($selection -eq 1) {
            if ($script:HevcGrainFiles.Count -gt 5) {
                $plateIndex = if ($cmbGrainPlateFile.SelectedIndex -ge 0) { $cmbGrainPlateFile.SelectedIndex + 5 } else { 5 }
                if ($plateIndex -ge $script:HevcGrainFiles.Count) { $plateIndex = 5 }
                $cmbHevcPlate.SelectedIndex = $plateIndex
            } else { Show-Error (L 'error.grain_plate_missing') }
        }
    } finally { $script:UpdatingProcStrengthUi = $false }
    Update-FgsimControls
})
$trackFilmGrainStrength.Add_ValueChanged({
    if ($script:UpdatingGrainUi) { return }
    $value = $trackFilmGrainStrength.Value
    switch (Get-VisibleGrainKind) {
        'DIGITAL' { Set-ProceduralStrength $value 'Unified' }
        'PLATE' { $trackHevcStrength.Value = $value }
        'FGSIM' {
            $script:UnifiedFgsimIndex = $value
            if ($cmbCodec.SelectedIndex -eq 0) { $cmbAv1Method.SelectedIndex = 5 + $value }
            else { $cmbHevcPlate.SelectedIndex = 2 + $value }
        }
    }
    Update-FgsimControls
})
$cmbGrainPlateFile.Add_SelectedIndexChanged({
    if (-not $script:UpdatingGrainUi -and $cmbGrainPlateFile.SelectedIndex -ge 0) {
        $cmbHevcPlate.SelectedIndex = 5 + $cmbGrainPlateFile.SelectedIndex
    }
})

# LUT group
$grpLut = New-Object System.Windows.Forms.GroupBox
$grpLut.Text = L 'lut.group'
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
$lutCol1.Width = 108
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
$chkLut.Text = L 'lut.enable'
$chkLut.Dock = 'Fill'
$chkLut.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 4, 3, 3, 3
$btnLutGallery = New-Object System.Windows.Forms.Button
$btnLutGallery.Text = L 'button.open_lut_gallery'
$btnLutGallery.Dock = 'Fill'
$btnLutGallery.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 3
$btnLutClear = New-Object System.Windows.Forms.Button
$btnLutClear.Text = L 'button.clear_lut'
$btnLutClear.Dock = 'Fill'
$btnLutClear.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 3
[void]$lutTable.Controls.Add($chkLut, 0, 0)
[void]$lutTable.Controls.Add($btnLutGallery, 1, 0)
[void]$lutTable.Controls.Add($btnLutClear, 2, 0)

$lblRecentLut = New-Object System.Windows.Forms.Label
$lblRecentLut.Text = L 'lut.recent'
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
$lblFavoriteLut.Text = L 'lut.favorite'
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
$lblSelectedLut.Text = L 'lut.none'
$lblSelectedLut.Dock = 'Fill'
$lblSelectedLut.AutoEllipsis = $true
$lblSelectedLut.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$lblSelectedLut.ForeColor = $ColorMuted
$lblSelectedLut.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 4, 0, 4, 0
[void]$lutPreviewPanel.Controls.Add($lblSelectedLut, 0, 1)
[void]$lutTable.Controls.Add($lutPreviewPanel, 0, 3)
$lutTable.SetColumnSpan($lutPreviewPanel, 3)

$lblLutStrengthTitle = New-Object System.Windows.Forms.Label
$lblLutStrengthTitle.Text = L 'lut.strength'
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
$digitalGrainTip = L 'digital_grain.tip'
$toolTip.SetToolTip($trackAv1ProcStrength, $digitalGrainTip)
$toolTip.SetToolTip($trackHevcProcStrength, $digitalGrainTip)
$toolTip.SetToolTip($btnGrainRoot, (L 'tooltip.grain_root'))
$toolTip.SetToolTip($btnRefreshGrain, (L 'tooltip.grain_refresh'))
$toolTip.SetToolTip($cmbAv1GrainTable, (L 'tooltip.table_default'))
$toolTip.SetToolTip($btnRefreshAv1Table, (L 'tooltip.table_refresh'))
$toolTip.SetToolTip($chkShowAllAv1Tables, (L 'tooltip.table_all'))
$toolTip.SetToolTip($btnUploadSubtitle, (L 'tooltip.subtitle'))
$toolTip.SetToolTip($chkInterpolation, (L 'tooltip.interpolation'))
$toolTip.SetToolTip($chkUpload, (L 'tooltip.upload'))
$toolTip.SetToolTip($cmbUploadBitrate, (L 'tooltip.upload_bitrate'))
$toolTip.SetToolTip($chkUploadBitrateAuto, (L 'tooltip.upload_auto'))
$toolTip.SetToolTip($chkUploadHighMotion, (L 'tooltip.high_motion'))
$toolTip.SetToolTip($chkBitrateAuto, (L 'tooltip.bitrate_auto'))
$toolTip.SetToolTip($cmbBitrate, (L 'tooltip.bitrate'))

# Log area
$grpLog = New-Object System.Windows.Forms.GroupBox
$grpLog.Text = L 'log.group'
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
$logStageColumn.SizeType = [System.Windows.Forms.SizeType]::Percent
$logStageColumn.Width = 100
[void]$logToolbar.ColumnStyles.Add($logStageColumn)
$logMetricColumn = New-Object System.Windows.Forms.ColumnStyle
$logMetricColumn.SizeType = [System.Windows.Forms.SizeType]::Absolute
$logMetricColumn.Width = 320
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
$lblRunStage.Text = L 'log.waiting'
$lblRunStage.AutoSize = $false
$lblRunStage.Dock = 'Fill'
$lblRunStage.Height = 23
$lblRunStage.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$lblRunStage.AutoEllipsis = $true
$lblRunMetric = New-Object System.Windows.Forms.Label
$lblRunMetric.Text = 'fps: —   speed: —'
$lblRunMetric.AutoSize = $false
$lblRunMetric.Dock = 'Fill'
$lblRunMetric.Height = 23
$lblRunMetric.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$lblRunMetric.ForeColor = $ColorMuted
$lblRunMetric.AutoEllipsis = $true
$btnCopyLog = New-Object System.Windows.Forms.Button
$btnCopyLog.Text = L 'button.copy_log'
$btnCopyLog.Size = New-Object System.Drawing.Size -ArgumentList 78, 24
$btnCopyLog.Anchor = 'Top,Right'
$btnClearLog = New-Object System.Windows.Forms.Button
$btnClearLog.Text = L 'button.clear_log'
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
$lblStatus.Text = L 'log.ready'
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
$btnCancel.Text = L 'button.cancel_task'
$btnCancel.Enabled = $false
$btnCancel.Size = New-Object System.Drawing.Size -ArgumentList 94, 34
$btnCancel.Anchor = 'Top,Right'
$btnCancel.Location = New-Object System.Drawing.Point -ArgumentList 995, 11
[void]$footer.Controls.Add($btnCancel)

$btnStart = New-Object System.Windows.Forms.Button
$btnStart.Text = L 'button.start_encode'
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
$openDialog.Title = L 'dialog.video_title'
$openDialog.Multiselect = $true
$openDialog.Filter = L 'dialog.video_filter'

$folderDialog = New-Object System.Windows.Forms.FolderBrowserDialog
$folderDialog.Description = L 'dialog.grain_root'
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
                $label = ((L 'subtitle.embedded_item') -f ($ordinal + 1),$lang,$codec)
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
    $dlg.Text = L 'subtitle.title'
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
    [void]$cmbSource.Items.Add((L 'subtitle.off'))
    [void]$sourceDefs.Add([pscustomobject]@{ Mode='OFF'; Index=0; Path=''; Label=(L 'subtitle.label_off') })
    [void]$cmbSource.Items.Add((L 'subtitle.auto'))
    [void]$sourceDefs.Add([pscustomobject]@{ Mode='AUTO'; Index=0; Path=''; Label=(L 'subtitle.label_auto') })

    foreach ($track in $tracks) {
        [void]$cmbSource.Items.Add([string]$track.Label)
        [void]$sourceDefs.Add([pscustomobject]@{ Mode='EMBEDDED'; Index=[int]$track.Ordinal; Path=''; Label=((L 'subtitle.label_embedded') -f ([int]$track.Ordinal + 1)) })
    }
    if ($sameName) {
        [void]$cmbSource.Items.Add((L 'subtitle.same_name') + [System.IO.Path]::GetFileName($sameName))
        [void]$sourceDefs.Add([pscustomobject]@{ Mode='EXTERNAL'; Index=0; Path=$sameName; Label=((L 'subtitle.label_external') -f [System.IO.Path]::GetFileName($sameName)) })
    }
    [void]$cmbSource.Items.Add((L 'subtitle.browse_external'))
    [void]$sourceDefs.Add([pscustomobject]@{ Mode='BROWSE'; Index=0; Path=''; Label=(L 'subtitle.label_external_file') })

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
    $btnBrowseSub.Text = L 'config.browse'
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

    Add-SubDialogLabel 0 (L 'subtitle.source') $cmbSource 1
    [void]$table.Controls.Add($btnBrowseSub,2,0)
    Add-SubDialogLabel 1 (L 'subtitle.font') $txtFont 2
    Add-SubDialogLabel 2 (L 'subtitle.size') $numSize 2
    Add-SubDialogLabel 3 (L 'subtitle.primary_color') $btnPrimary 2
    Add-SubDialogLabel 4 (L 'subtitle.border_color') $btnBorder 2
    Add-SubDialogLabel 5 (L 'subtitle.outline') $numOutline 2
    Add-SubDialogLabel 6 (L 'subtitle.shadow') $numShadow 2
    Add-SubDialogLabel 7 (L 'subtitle.margin_bottom') $numMargin 2

    $note = New-Object System.Windows.Forms.Label
    $note.Dock='Fill'; $note.ForeColor=$ColorMuted; $note.Padding=New-Object System.Windows.Forms.Padding -ArgumentList 4,6,4,0
    $note.Text = L 'subtitle.note'
    [void]$table.Controls.Add($note,0,8); $table.SetColumnSpan($note,3)

    $buttons = New-Object System.Windows.Forms.FlowLayoutPanel
    $buttons.Dock='Fill'; $buttons.FlowDirection='RightToLeft'; $buttons.WrapContents=$false
    $ok = New-Object System.Windows.Forms.Button; $ok.Text=(L 'advanced.ok'); $ok.Width=82; $ok.DialogResult=[System.Windows.Forms.DialogResult]::OK
    $cancel = New-Object System.Windows.Forms.Button; $cancel.Text=(L 'advanced.cancel'); $cancel.Width=82; $cancel.DialogResult=[System.Windows.Forms.DialogResult]::Cancel
    [void]$buttons.Controls.Add($ok); [void]$buttons.Controls.Add($cancel)
    [void]$table.Controls.Add($buttons,0,9); $table.SetColumnSpan($buttons,3)
    $dlg.AcceptButton=$ok; $dlg.CancelButton=$cancel

    $colorDialog = New-Object System.Windows.Forms.ColorDialog
    $btnPrimary.Add_Click({ $colorDialog.Color=$btnPrimary.BackColor; if ($colorDialog.ShowDialog($dlg) -eq [System.Windows.Forms.DialogResult]::OK) { $hex = ('{0:X2}{1:X2}{2:X2}' -f $colorDialog.Color.R,$colorDialog.Color.G,$colorDialog.Color.B); Set-SubtitleColorButton $btnPrimary $hex } })
    $btnBorder.Add_Click({ $colorDialog.Color=$btnBorder.BackColor; if ($colorDialog.ShowDialog($dlg) -eq [System.Windows.Forms.DialogResult]::OK) { $hex = ('{0:X2}{1:X2}{2:X2}' -f $colorDialog.Color.R,$colorDialog.Color.G,$colorDialog.Color.B); Set-SubtitleColorButton $btnBorder $hex } })

    $browseDialog = New-Object System.Windows.Forms.OpenFileDialog
    $browseDialog.Title=(L 'subtitle.select_external')
    $browseDialog.Filter=(L 'subtitle.filter')
    if ($targetPath) { $browseDialog.InitialDirectory=[System.IO.Path]::GetDirectoryName($targetPath) }
    $chosenBrowsePath = ''
    $btnBrowseSub.Add_Click({
        if ($browseDialog.ShowDialog($dlg) -eq [System.Windows.Forms.DialogResult]::OK) {
            $chosenBrowsePath=$browseDialog.FileName
            $browseIndex=$sourceDefs.Count-1
            $sourceDefs[$browseIndex].Path=$chosenBrowsePath
            $sourceDefs[$browseIndex].Label=((L 'subtitle.label_external') -f [System.IO.Path]::GetFileName($chosenBrowsePath))
            $cmbSource.Items[$browseIndex]=((L 'subtitle.external_item') -f [System.IO.Path]::GetFileName($chosenBrowsePath))
            $cmbSource.SelectedIndex=$browseIndex
        }
    })

    $result=$dlg.ShowDialog($form)
    if ($result -ne [System.Windows.Forms.DialogResult]::OK) { $dlg.Dispose(); return }
    $def=$sourceDefs[$cmbSource.SelectedIndex]
    if ($def.Mode -eq 'BROWSE' -and -not $def.Path) {
        Show-Info (L 'subtitle.not_selected')
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
    $script:UploadSubtitle.Label = if ($script:UploadSubtitle.Enabled) { [string]$def.Label } else { L 'subtitle.label_off' }
    $btnUploadSubtitle.Text = if ($script:UploadSubtitle.Enabled) { L 'button.subtitle_on' } else { L 'button.subtitle' }
    $toolTip.SetToolTip($btnUploadSubtitle, [string]$script:UploadSubtitle.Label)
    $dlg.Dispose()
}

function Update-UploadHighMotionUi {
    $chkUploadHighMotion.Enabled = ($cmbCodec.SelectedIndex -ge 0 -and $cmbCodec.SelectedIndex -le 2)
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
    $codec = if ($Stream.codec_name) { ([string]$Stream.codec_name).ToUpperInvariant() } else { L 'media.unknown' }
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
        $scanText = if ($fieldOrder -in @('tt', 'bb', 'tb', 'bt')) { ((L 'media.scan_interlaced') -f $fieldOrder) } elseif ($fieldOrder -eq 'progressive') { L 'media.scan_progressive' } elseif ($fieldOrder) { ((L 'media.scan_flag') -f $fieldOrder) } else { L 'media.scan_unknown' }
        $videoLine = (L 'media.video_prefix') + '  ' + (Format-CodecName $video) + " · $resolution · $fps fps · $scanText · " + (Format-MediaBitrate $video.bit_rate)
        $transfer = ([string]$video.color_transfer).ToLowerInvariant()
        $isHdr = ($transfer -eq 'smpte2084' -or $transfer -eq 'arib-std-b67')
        if ($isHdr) {
            $hdrName = if ($transfer -eq 'smpte2084') { 'HDR10 / PQ' } else { 'HLG' }
            $bitDepth = '10-bit'
            if ($video.bits_per_raw_sample -and ([string]$video.bits_per_raw_sample) -match '^\d+$') {
                $bitDepth = ([string]$video.bits_per_raw_sample) + '-bit'
            } elseif (([string]$video.pix_fmt) -match '10') {
                $bitDepth = '10-bit'
            }
            $prim = if ($video.color_primaries) { [string]$video.color_primaries } else { 'primaries ?' }
            $matrix = if ($video.color_space) { [string]$video.color_space } else { 'matrix ?' }
            $videoLine += "`r`nHDR   $hdrName · $bitDepth · $prim · $matrix"
        }
    } else {
        $videoLine = L 'media.video_missing'
    }

    if ($audio) {
        $channelText = if ($audio.channel_layout) { [string]$audio.channel_layout } elseif ($audio.channels) { ((L 'media.channel_count') -f $audio.channels) } else { L 'media.channel_unknown' }
        $sampleText = '—'
        $sampleRate = 0.0
        if ($audio.sample_rate -and [double]::TryParse([string]$audio.sample_rate, [ref]$sampleRate) -and $sampleRate -gt 0) {
            $sampleText = ('{0:0.###} kHz' -f ($sampleRate / 1000.0))
        }
        $audioLine = (L 'media.audio_prefix') + '  ' + (Format-CodecName $audio) + " · $channelText · $sampleText · " + (Format-MediaBitrate $audio.bit_rate)
    } else {
        $audioLine = L 'media.audio_missing'
    }

    $format = $Data.format
    $duration = Format-MediaDuration $format.duration
    $totalRate = Format-MediaBitrate $format.bit_rate
    $formatLine = ((L 'media.duration_line') -f $duration,$totalRate)
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
        Set-MediaInfoText (L 'media.file_missing') $true
        return
    }

    $cacheKey = $Path.ToLowerInvariant()
    if ($script:ProbeCache.ContainsKey($cacheKey)) {
        $summary = [string]$script:ProbeCache[$cacheKey]
        Set-MediaInfoText $summary
        Update-DeinterlaceUi
        Update-NoReencodeAvailability
        Update-BitrateDisplays
        Update-HdrCompatibilityUi
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
        Set-MediaInfoText ((L 'media.ffprobe_missing') -f $Ffprobe) $true
        return
    }

    Set-MediaInfoText (L 'media.reading') $true
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $probeExe
        $psi.Arguments = '-v error -show_entries "format=format_name,duration,bit_rate:stream=codec_type,codec_name,profile,width,height,avg_frame_rate,field_order,bit_rate,channels,channel_layout,sample_rate,pix_fmt,bits_per_raw_sample,color_range,color_space,color_transfer,color_primaries" -of json "' + $Path + '"'
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true

        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $psi
        if (-not $process.Start()) { throw (L 'media.ffprobe_start_failed') }
        $script:ProbeProcess = $process
        $script:ProbeOutputTask = $process.StandardOutput.ReadToEndAsync()
        $script:ProbeErrorTask = $process.StandardError.ReadToEndAsync()
        $script:ProbeTargetPath = $Path
        $probeTimer.Start()
    } catch {
        if ($process) { try { $process.Dispose() } catch {} }
        $script:ProbeProcess = $null
        Set-MediaInfoText ((L 'media.read_failed') -f $_.Exception.Message) $true
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

function Get-Av1GrainDisplayText {
    param([string]$State)
    switch ($State) {
        'AV1 胶片颗粒：无' { return (L 'av1grain.none') }
        'AV1 胶片颗粒：无法识别参数表' { return (L 'av1grain.table_invalid') }
        'AV1 胶片颗粒：亮度' { return (L 'av1grain.luma') }
        'AV1 胶片颗粒：亮度 + 色度' { return (L 'av1grain.luma_chroma') }
        'AV1 胶片颗粒：参数表读取失败' { return (L 'av1grain.table_read_failed') }
        default {
            if ($State -like 'AV1 胶片颗粒：检测失败 · *') {
                $detail = $State.Substring('AV1 胶片颗粒：检测失败 · '.Length)
                return ((L 'av1grain.detect_failed_detail') -f $detail)
            }
            return $State
        }
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

    $hasItem = ($cmbCodec.Items.Count -ge 4 -and [string]$cmbCodec.Items[3] -eq $script:NoReencodeItemText)
    if ($eligible -and -not $hasItem) {
        [void]$cmbCodec.Items.Add($script:NoReencodeItemText)
    } elseif (-not $eligible -and $hasItem) {
        if ($cmbCodec.SelectedIndex -eq 3) {
            $fallback = if ($script:HardwareCapsReady -and -not $script:Av1Available) { if ($script:HevcAvailable) { 1 } else { 2 } } else { 0 }
            $cmbCodec.SelectedIndex = $fallback
        }
        $cmbCodec.Items.RemoveAt(3)
    }
}

function Start-Av1GrainInspect {
    param([string]$Path,[string]$BaseSummary)
    Stop-Av1GrainInspect
    if (-not $Path -or -not [System.IO.File]::Exists($Path)) { return }

    $key = $Path.ToLowerInvariant()
    if ($script:Av1GrainCache.ContainsKey($key)) {
        Set-MediaInfoText ($BaseSummary + "`r`n" + (Get-Av1GrainDisplayText ([string]$script:Av1GrainCache[$key])))
        return
    }
    if (-not $Grav1synth -or -not (Test-Path -LiteralPath $Grav1synth -PathType Leaf)) {
        Set-MediaInfoText ($BaseSummary + "`r`n" + (L 'av1grain.grav_missing'))
        return
    }

    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('FilmGrainStudio_Inspect_' + [guid]::NewGuid().ToString('N') + '.txt')
    Set-MediaInfoText ($BaseSummary + "`r`n" + (L 'av1grain.detecting'))
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
        Set-MediaInfoText ($BaseSummary + "`r`n" + (L 'av1grain.detect_failed'))
    }
}

function Update-SelectedMediaInfo {
    $selected = @($listFiles.SelectedItems)
    if ($selected.Count -eq 0) {
        Stop-VideoProbe
        Stop-Av1GrainInspect
        Set-MediaInfoText (L 'media.select_prompt') $true
    } elseif ($selected.Count -gt 1) {
        Stop-VideoProbe
        Stop-Av1GrainInspect
        Set-MediaInfoText ((L 'media.multi_selected') -f $selected.Count) $true
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
            $detail = if ($errorText) { ([System.Text.RegularExpressions.Regex]::Split(([string]$errorText).Trim(), '\r?\n'))[0] } else { ((L 'media.ffprobe_code') -f $exitCode) }
            Set-MediaInfoText ((L 'media.read_failed') -f $detail) $true
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
        Update-BitrateDisplays
        Update-HdrCompatibilityUi
        if ($cmbAv1Method.SelectedIndex -eq 2 -and -not $chkShowAllAv1Tables.Checked) { Refresh-Av1GrainTables }
        if ($videoMeta.Count -gt 0 -and ([string]$videoMeta[0].codec_name).ToLowerInvariant() -eq 'av1') {
            Start-Av1GrainInspect $targetPath $summary
        } else {
            Stop-Av1GrainInspect
        }
    } catch {
        Stop-VideoProbe
        Set-MediaInfoText ((L 'media.read_failed') -f $_.Exception.Message) $true
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
            if (-not $detail) { $detail = ((L 'media.return_code') -f $exitCode) }
            $grainSummary = "AV1 胶片颗粒：检测失败 · $detail"
        }
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        $key = $targetPath.ToLowerInvariant()
        $script:Av1GrainCache[$key] = $grainSummary

        $selected = @($listFiles.SelectedItems)
        if ($selected.Count -eq 1 -and [string]::Equals([string]$selected[0].Tag,$targetPath,[System.StringComparison]::OrdinalIgnoreCase)) {
            $base = if ($script:ProbeCache.ContainsKey($key)) { [string]$script:ProbeCache[$key] } else { '' }
            if ($base) { Set-MediaInfoText ($base + "`r`n" + (Get-Av1GrainDisplayText $grainSummary)) }
        }
    } catch {
        Stop-Av1GrainInspect
    }
})

function Update-FileCount {
    $count = $listFiles.Items.Count
    if ($count -eq 0) {
        $lblStatus.Text = L 'log.ready'
    } elseif (-not $script:RunningProcess) {
        $lblStatus.Text = ((L 'status.ready_count') -f $count)
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
            $row.ToolTipText = $fi.Name
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

$script:UpdatingProcStrengthUi = $false

function Set-ProceduralStrength {
    param([int]$Value, [string]$Source = '')
    if ($Value -lt 10) { $Value = 10 }
    if ($Value -gt 100) { $Value = 100 }
    if ($script:UpdatingProcStrengthUi) { return }
    $script:UpdatingProcStrengthUi = $true
    try {
        if ($trackAv1ProcStrength.Value -ne $Value) { $trackAv1ProcStrength.Value = $Value }
        if ($trackHevcProcStrength.Value -ne $Value) { $trackHevcProcStrength.Value = $Value }
        $display = [string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:0.00}', ($Value / 100.0))
        $lblAv1ProcStrength.Text = $display
        $lblHevcProcStrength.Text = $display
    } finally {
        $script:UpdatingProcStrengthUi = $false
    }
}

function Refresh-HevcGrainPlates {
    $rootText = $txtGrainRoot.Text.Trim()
    $previousPath = $null
    if ($cmbHevcPlate.SelectedIndex -ge 0 -and $cmbHevcPlate.SelectedIndex -lt $script:HevcGrainFiles.Count) {
        $previousPath = [string]$script:HevcGrainFiles[$cmbHevcPlate.SelectedIndex]
    }

    $proc30 = '::PROC30::'
    $proc55 = '::PROC55::'
    $fgsimLight = '::FGSIM_LIGHT::'
    $fgsimMedium = '::FGSIM_MEDIUM::'
    $fgsimHeavy = '::FGSIM_HEAVY::'
    $script:HevcGrainFiles = @($proc30, $proc55, $fgsimLight, $fgsimMedium, $fgsimHeavy)
    $script:LastScannedGrainRoot = $rootText
    $cmbHevcPlate.BeginUpdate()
    try {
        $cmbHevcPlate.Items.Clear()
        [void]$cmbHevcPlate.Items.Add((L 'grain.method.digital30'))
        [void]$cmbHevcPlate.Items.Add((L 'grain.method.digital55'))
        [void]$cmbHevcPlate.Items.Add((L 'grain.method.fgsim_light'))
        [void]$cmbHevcPlate.Items.Add((L 'grain.method.fgsim_medium'))
        [void]$cmbHevcPlate.Items.Add((L 'grain.method.fgsim_heavy'))
        $cmbHevcPlate.Enabled = $true

        if (-not $rootText -or -not (Test-Path -LiteralPath $rootText -PathType Container)) {
            $selectIndex = if ($previousPath -eq $proc30) { 0 } else { 1 }
            $cmbHevcPlate.SelectedIndex = $selectIndex
            $cacheNote.Text = L 'grain.root_missing'
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

        $selectedIndex = if ($files.Count -gt 0) { 5 } else { 1 }
        if ($previousPath) {
            for ($i = 0; $i -lt $script:HevcGrainFiles.Count; $i++) {
                if ([string]::Equals([string]$script:HevcGrainFiles[$i], $previousPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $selectedIndex = $i
                    break
                }
            }
        }
        $cmbHevcPlate.SelectedIndex = $selectedIndex
        if ($files.Count -gt 0) {
            $cacheNote.Text = ((L 'grain.root_count') -f $files.Count)
        } else {
            $cacheNote.Text = L 'grain.root_none'
        }
    } catch {
        $script:HevcGrainFiles = @($proc30, $proc55, $fgsimLight, $fgsimMedium, $fgsimHeavy)
        $cmbHevcPlate.Items.Clear()
        [void]$cmbHevcPlate.Items.Add((L 'grain.method.digital30'))
        [void]$cmbHevcPlate.Items.Add((L 'grain.method.digital55'))
        [void]$cmbHevcPlate.Items.Add('GPU Film Grain (FGSIM) · Light · 0.10 + HL50')
        [void]$cmbHevcPlate.Items.Add((L 'grain.method.fgsim_medium'))
        [void]$cmbHevcPlate.Items.Add('GPU Film Grain (FGSIM) · Heavy · 0.30 + HL50')
        $cmbHevcPlate.SelectedIndex = 1
        $cmbHevcPlate.Enabled = $true
        $cacheNote.Text = (L 'grain.root_failed') + ' ' + $_.Exception.Message
    } finally {
        $cmbHevcPlate.EndUpdate()
        Update-HevcGrainControls
    }
}

function Update-HevcGrainControls {
    $selected = ''
    if ($cmbHevcPlate.SelectedIndex -ge 0 -and $cmbHevcPlate.SelectedIndex -lt $script:HevcGrainFiles.Count) {
        $selected = [string]$script:HevcGrainFiles[$cmbHevcPlate.SelectedIndex]
    }
    $isProc = ($selected -eq '::PROC30::' -or $selected -eq '::PROC55::')
    $isFgsim = $selected.StartsWith('::FGSIM_')
    $trackHevcStrength.Enabled = (-not $isProc -and -not $isFgsim)
    $hevcStrengthPanel.Visible = (-not $isProc -and -not $isFgsim)
    $hevcProcStrengthPanel.Visible = $isProc
    $hevcProcStrengthPanel.Enabled = $isProc
    if ($isProc) {
        $hevcProcStrengthPanel.BringToFront()
    } elseif ($isFgsim) {
        $hevcProcStrengthPanel.Visible = $false
        $hevcStrengthPanel.Visible = $false
    } else {
        $hevcStrengthPanel.BringToFront()
        $names = @('Light · 65%', 'Natural · 75%', 'Strong · 85%', 'Full · 100%')
        $lblHevcStrength.Text = $names[$trackHevcStrength.Value]
    }
    Update-FgsimControls
}

function Get-FpsNumber {
    param([string]$Value)
    if (-not $Value) { return 0.0 }
    try {
        $parts = $Value.Split('/')
        if ($parts.Count -eq 2) {
            $den = [double]$parts[1]
            if ($den -eq 0) { return 0.0 }
            return ([double]$parts[0] / $den)
        }
        return [double]$Value
    } catch { return 0.0 }
}

function Get-RecommendedOutputFps {
    param($Meta)
    if ($chkInterpolation.Checked) { return 60.0 }
    if ($null -eq $Meta) { return 60.0 }
    $sourceFps = Get-FpsNumber ([string]$Meta.avg_frame_rate)
    if ($sourceFps -le 0) { return 60.0 }
    $fieldOrder = ([string]$Meta.field_order).ToLowerInvariant()
    $isInterlaced = ($fieldOrder -in @('tt','bb','tb','bt'))
    if ($cmbDeint.SelectedIndex -eq 0 -and $isInterlaced) { return ($sourceFps * 2.0) }
    if ($cmbFps.SelectedIndex -eq 1) { return $sourceFps }

    $targets23976 = @(23.976023976,29.970029970,47.952047952,59.940059940,119.880119880)
    $targets24 = @(24.0,25.0,30.0,48.0,50.0,60.0,100.0,120.0)
    $best = [double]::MaxValue
    $family = ''
    foreach ($t in $targets23976) {
        $d = [Math]::Abs($sourceFps - $t)
        if ($d -lt $best) { $best = $d; $family = '23976' }
    }
    foreach ($t in $targets24) {
        $d = [Math]::Abs($sourceFps - $t)
        if ($d -lt $best) { $best = $d; $family = '24' }
    }
    if ($best -le 0.25) {
        if ($family -eq '23976') { return (24000.0 / 1001.0) }
        return 24.0
    }
    return $sourceFps
}

function Get-BitrateFpsFactor {
    param([double]$Fps)
    if ($Fps -le 0) { return 1.0 }
    $points = @(
        @(24.0, 0.60),
        @(25.0, 0.62),
        @(30.0, 0.70),
        @(50.0, 0.90),
        @(60.0, 1.00),
        @(120.0, 1.65)
    )
    if ($Fps -le 24.0) { return [Math]::Max(0.40, 0.60 * ($Fps / 24.0)) }
    for ($i = 1; $i -lt $points.Count; $i++) {
        $x1 = [double]$points[$i - 1][0]
        $y1 = [double]$points[$i - 1][1]
        $x2 = [double]$points[$i][0]
        $y2 = [double]$points[$i][1]
        if ($Fps -le $x2) {
            $t = ($Fps - $x1) / ($x2 - $x1)
            return ($y1 + (($y2 - $y1) * $t))
        }
    }
    return (1.65 * [Math]::Pow(($Fps / 120.0), 0.75))
}

function Get-BitrateRecommendation {
    param([int]$CodecIndex, [int]$Width, [int]$Height, [double]$Fps, [bool]$HighMotion)
    if ($Width -le 0) { $Width = 1920 }
    if ($Height -le 0) { $Height = 1080 }
    if ($Fps -le 0) { $Fps = 60.0 }

    $longEdge = [Math]::Max($Width, $Height)
    $tier = if ($longEdge -le 1280) { '720p' } elseif ($longEdge -le 1920) { '1080p' } elseif ($longEdge -le 2560) { '1440p' } else { '2160p' }
    $normal = @{
        0 = @{ '720p'=3500; '1080p'=5000; '1440p'=7000; '2160p'=10000 }
        1 = @{ '720p'=4000; '1080p'=6000; '1440p'=8000; '2160p'=12000 }
        2 = @{ '720p'=5000; '1080p'=7500; '1440p'=10000; '2160p'=15000 }
    }
    $motion = @{
        0 = @{ '720p'=6000; '1080p'=9000; '1440p'=12000; '2160p'=18000 }
        1 = @{ '720p'=7000; '1080p'=11000; '1440p'=15000; '2160p'=22000 }
        2 = @{ '720p'=10000; '1080p'=15000; '1440p'=20000; '2160p'=30000 }
    }
    $table = if ($HighMotion) { $motion } else { $normal }
    $base = [double]$table[$CodecIndex][$tier]
    $factor = Get-BitrateFpsFactor $Fps
    $raw = $base * $factor
    $rate = [int]([Math]::Floor(($raw + 250.0) / 500.0) * 500.0)
    if ($rate -lt 1000) { $rate = 1000 }
    return [pscustomobject]@{ Bitrate=$rate; Tier=$tier; Base=[int]$base; Fps=$Fps; FpsFactor=$factor; Width=$Width; Height=$Height }
}

function Get-BitrateSourceContext {
    $item = $null
    $selected = @($listFiles.SelectedItems)
    if ($selected.Count -eq 1) { $item = $selected[0] }
    elseif ($listFiles.Items.Count -gt 0) { $item = $listFiles.Items[0] }
    if ($null -eq $item -or -not $item.Tag) { return [pscustomobject]@{ Meta=$null; Path='' } }
    $path = [string]$item.Tag
    $key = $path.ToLowerInvariant()
    $meta = if ($script:ProbeVideoMeta.ContainsKey($key)) { $script:ProbeVideoMeta[$key] } else { $null }
    return [pscustomobject]@{ Meta=$meta; Path=$path }
}

$script:FgsimRcActive = $false
$script:FgsimRcChoice = 'VBR'
$script:FgsimStandardItem = 'Standard CQ27 / QP18-26'
$script:FgsimHighItem = 'High Quality CQ23 / QP18-24'
$cmbBitrate.DropDownWidth = 330

function Test-HevcFgsim {
    return ($cmbCodec.SelectedIndex -eq 1 -and (Get-VisibleGrainKind) -eq 'FGSIM')
}
function Test-FgsimCqSelected {
    return ((Test-HevcFgsim) -and $script:FgsimRcActive -and $script:FgsimRcChoice -ne 'VBR')
}
function Get-EncodingBitrateText {
    if (Test-FgsimCqSelected) { return [string]$script:ModeBitrate[1] }
    return $cmbBitrate.Text.Trim()
}
function Update-FgsimBitrateUi {
    if (-not $cmbBitrate -or $script:UpdatingBitrateUi) { return }
    $active = Test-HevcFgsim
    $entering = ($active -and -not $script:FgsimRcActive)
    $leaving = (-not $active -and $script:FgsimRcActive)
    if ($entering) { $script:FgsimRcChoice = 'VBR' }
    $script:FgsimRcActive = $active
    $script:UpdatingBitrateUi = $true
    try {
        if ($active) {
            if (-not $cmbBitrate.Items.Contains($script:FgsimStandardItem)) {
                $cmbBitrate.Items.Insert(0,$script:FgsimStandardItem)
                $cmbBitrate.Items.Insert(1,$script:FgsimHighItem)
            }
            if ($script:FgsimRcChoice -eq 'STANDARD') { $cmbBitrate.SelectedItem = $script:FgsimStandardItem }
            elseif ($script:FgsimRcChoice -eq 'HIGH') { $cmbBitrate.SelectedItem = $script:FgsimHighItem }
            if ($toolTip) {
                $toolTip.SetToolTip($cmbBitrate, $lblHevcGrainHint.Text)
            }
        } else {
            $cmbBitrate.Items.Remove($script:FgsimStandardItem)
            $cmbBitrate.Items.Remove($script:FgsimHighItem)
            if ($leaving -and $cmbCodec.SelectedIndex -ge 0 -and $cmbCodec.SelectedIndex -le 2) {
                $cmbBitrate.Text = [string]$script:ModeBitrate[$cmbCodec.SelectedIndex]
            }
        }
    } finally { $script:UpdatingBitrateUi = $false }
}

function Update-AutoBitrateDisplay {
    if (-not $cmbBitrate -or -not $chkBitrateAuto -or $cmbCodec.SelectedIndex -lt 0 -or $cmbCodec.SelectedIndex -gt 2) { return }
    $codecIndex = $cmbCodec.SelectedIndex
    if (-not [bool]$script:ModeBitrateAuto[$codecIndex]) { return }
    $ctx = Get-BitrateSourceContext
    $meta = $ctx.Meta
    $width = if ($meta -and $meta.width) { [int]$meta.width } else { 1920 }
    $height = if ($meta -and $meta.height) { [int]$meta.height } else { 1080 }
    $fps = Get-RecommendedOutputFps $meta
    $rec = Get-BitrateRecommendation $codecIndex $width $height $fps $chkUploadHighMotion.Checked
    $script:UpdatingBitrateUi = $true
    try {
        if (-not (Test-FgsimCqSelected)) { $cmbBitrate.Text = [string]$rec.Bitrate }
        $script:ModeBitrate[$codecIndex] = [string]$rec.Bitrate
        $chkBitrateAuto.Checked = $true
    } finally { $script:UpdatingBitrateUi = $false }
    if ($toolTip) {
        $motionText = if ($chkUploadHighMotion.Checked) { L 'motion.high' } else { L 'motion.normal' }
        $toolTip.SetToolTip($cmbBitrate, ((L 'tooltip.bitrate_recommend') -f $rec.Bitrate,$rec.Tier,$rec.Base,$rec.Fps,$rec.FpsFactor,$motionText))
    }
}

function Update-UploadAutoBitrateDisplay {
    if (-not $cmbUploadBitrate -or -not $chkUploadBitrateAuto) { return }
    if (-not [bool]$script:UploadBitrateAuto) { return }
    $ctx = Get-BitrateSourceContext
    $meta = $ctx.Meta
    $width = if ($meta -and $meta.width) { [int]$meta.width } else { 1920 }
    $height = if ($meta -and $meta.height) { [int]$meta.height } else { 1080 }
    $fps = Get-RecommendedOutputFps $meta
    $rec = Get-BitrateRecommendation 2 $width $height $fps $chkUploadHighMotion.Checked
    $script:UpdatingUploadBitrateUi = $true
    try {
        $cmbUploadBitrate.Text = [string]$rec.Bitrate
        $script:UploadBitrate = [string]$rec.Bitrate
        $chkUploadBitrateAuto.Checked = $true
    } finally { $script:UpdatingUploadBitrateUi = $false }
    if ($toolTip) {
        $motionText = if ($chkUploadHighMotion.Checked) { L 'motion.high' } else { L 'motion.normal' }
        $toolTip.SetToolTip($cmbUploadBitrate, ((L 'tooltip.upload_recommend') -f $rec.Bitrate,$rec.Tier,$rec.Base,$rec.Fps,$rec.FpsFactor,$motionText))
    }
}

function Update-BitrateDisplays {
    Update-AutoBitrateDisplay
    Update-UploadAutoBitrateDisplay
    Update-FgsimBitrateUi
}

function Load-BitrateChoices {
    param([int]$CodecIndex)
    $script:UpdatingBitrateUi = $true
    $cmbBitrate.BeginUpdate()
    try {
        $cmbBitrate.Items.Clear()
        foreach ($value in @('3000','3500','4000','5000','6000','7000','7500','8000','9000','10000','11000','12000','15000','18000','20000','22000','30000')) { [void]$cmbBitrate.Items.Add($value) }
    } finally { $cmbBitrate.EndUpdate(); $script:UpdatingBitrateUi = $false }
    $script:UpdatingBitrateUi = $true
    try { $chkBitrateAuto.Checked = [bool]$script:ModeBitrateAuto[$CodecIndex] } finally { $script:UpdatingBitrateUi = $false }
    if ([bool]$script:ModeBitrateAuto[$CodecIndex]) { Update-AutoBitrateDisplay }
    else {
        $script:UpdatingBitrateUi = $true
        try { $cmbBitrate.Text = [string]$script:ModeBitrate[$CodecIndex] } finally { $script:UpdatingBitrateUi = $false }
    }
    Update-FgsimBitrateUi
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
            ((L 'tooltip.table_current') -f $preferredTier)
        } else {
            (L 'tooltip.table_wait')
        }
        $toolTip.SetToolTip($chkShowAllAv1Tables, $tip)
    }

    $cmbAv1GrainTable.BeginUpdate()
    try {
        $cmbAv1GrainTable.Items.Clear()
        if (-not (Test-Path -LiteralPath $DefaultAv1GrainTableRoot -PathType Container)) {
            [void]$cmbAv1GrainTable.Items.Add((L 'grain.table_dir_missing'))
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
                [void]$cmbAv1GrainTable.Items.Add(((L 'grain.table_tier_empty') -f $preferredTier))
            } elseif (-not $showAll -and -not $preferredTier) {
                [void]$cmbAv1GrainTable.Items.Add((L 'grain.table_wait_resolution'))
            } else {
                [void]$cmbAv1GrainTable.Items.Add((L 'grain.table_empty'))
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
    $procMode = ($cmbAv1Method.SelectedIndex -eq 3 -or $cmbAv1Method.SelectedIndex -eq 4)
    $fgsimMode = ($cmbAv1Method.SelectedIndex -ge 5 -and $cmbAv1Method.SelectedIndex -le 7)
    $cmbAv1Format.Enabled = $presetMode
    $cmbAv1Stock.Enabled = $presetMode -and ($cmbAv1Format.SelectedIndex -lt 3)
    $numIso.Enabled = $isoMode
    $chkChroma.Enabled = $isoMode
    $chkChroma.Visible = -not $procMode
    $av1ProcStrengthPanel.Visible = $procMode
    $av1ProcStrengthPanel.Enabled = $procMode
    if ($procMode) { $av1ProcStrengthPanel.BringToFront() } else { $chkChroma.BringToFront() }
    $cmbAv1GrainTable.Enabled = $tableMode -and ($script:Av1GrainTableFiles.Count -gt 0)
    $btnRefreshAv1Table.Enabled = $tableMode
    $chkShowAllAv1Tables.Enabled = $tableMode
    if ($cmbCodec.SelectedIndex -eq 0) {
        if ($procMode) { $grpGrain.Text = L 'grain.av1_digital' }
        elseif ($fgsimMode) { $grpGrain.Text = L 'grain.av1_fgsim' }
        else { $grpGrain.Text = L 'grain.av1_metadata' }
    }
    Update-FgsimControls
}

function Update-SfeUi {
    if (-not $chkSfe) { return }

    $maxEngines = [int]$script:SfeMaxEngines
    if ($maxEngines -lt 1) { $maxEngines = 1 }
    $chkSfe.Text = ((L 'sfe.engines') -f $maxEngines)

    $isAv1 = ($cmbCodec.SelectedIndex -eq 0)
    $isSupportedSpeed = ($cmbSpeed.SelectedIndex -eq 1 -or $cmbSpeed.SelectedIndex -eq 2)
    $canUse = ($script:HardwareCapsReady -and $maxEngines -ge 2 -and $script:Grav1synthSfeCompatible -and $isAv1 -and $isSupportedSpeed)

    if (-not $canUse) {
        $chkSfe.Checked = $false
        $chkSfe.Enabled = $false
    } else {
        $chkSfe.Enabled = $true
    }

    if ($maxEngines -lt 2) {
        $sfeToolTip.SetToolTip($chkSfe, (L 'tooltip.sfe_unavailable'))
    } elseif (-not $script:Grav1synthSfeCompatible) {
        $sfeToolTip.SetToolTip($chkSfe, ((L 'tooltip.sfe_version') -f $script:Grav1synthVersion))
    } else {
        $sfeToolTip.SetToolTip($chkSfe, ((L 'tooltip.sfe_ready') -f $maxEngines,$script:Grav1synthVersion))
    }
}

function Update-SpeedChoices {
    $script:UpdatingSpeedChoices = $true
    try {
        if ($cmbCodec.SelectedIndex -eq 2) {
            $presetLabel = switch ($script:X264Preset) { 'medium' { 'Medium' } 'slow' { 'Slow' } default { 'Faster' } }
            $passLabel = if ($script:X264RateMode -eq '3PASS') { L 'advanced.vbr3' } elseif ($script:X264RateMode -eq '2PASS') { L 'advanced.vbr2' } else { L 'speed.vbr1' }
            $cmbSpeed.BeginUpdate()
            try {
                $cmbSpeed.Items.Clear()
                [void]$cmbSpeed.Items.Add("x264 · $presetLabel / tune grain / $passLabel")
                $cmbSpeed.SelectedIndex = 0
            } finally { $cmbSpeed.EndUpdate() }
            $cmbSpeed.Enabled = $false
            return
        }
        $cmbSpeed.Enabled = $true
        $allowUhq = ($cmbCodec.SelectedIndex -eq 0 -and $script:HardwareCapsReady -and $script:Av1UhqAvailable)
        $currentText = [string]$cmbSpeed.SelectedItem
        $selectedIndex = if ($currentText -like 'Standard*') { 1 } else { 0 }
        if ($allowUhq -and $currentText -like 'UHQ*') { $selectedIndex = 2 }

        $cmbSpeed.BeginUpdate()
        try {
            $cmbSpeed.Items.Clear()
            [void]$cmbSpeed.Items.Add((L 'speed.fast'))
            [void]$cmbSpeed.Items.Add('Standard · p6 / fullres')
            if ($allowUhq) { [void]$cmbSpeed.Items.Add((L 'speed.uhq')) }
            $cmbSpeed.SelectedIndex = $selectedIndex
        } finally {
            $cmbSpeed.EndUpdate()
        }
    } finally {
        $script:UpdatingSpeedChoices = $false
        Update-SfeUi
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
    if ($chkInterpolation -and $chkInterpolation.Checked) {
        $cmbDeintMethod.Enabled = $false
        $cmbFps.Enabled = $false
        return
    }

    if ($cmbCodec.SelectedIndex -eq 3) {
        $cmbDeintMethod.Enabled = $false
        $cmbFps.Enabled = $false
        return
    }

    $auto = ($cmbDeint.SelectedIndex -eq 0)
    $cmbDeintMethod.Enabled = $auto

    $autoText = L 'fps.auto_interlaced'
    if ($auto) {
        $isConfirmedInterlaced = $false
        $selected = @($listFiles.SelectedItems)
        if ($selected.Count -eq 1) {
            $key = ([string]$selected[0].Tag).ToLowerInvariant()
            if ($script:ProbeVideoMeta.ContainsKey($key)) {
                $meta = $script:ProbeVideoMeta[$key]
                $fieldOrder = ([string]$meta.field_order).ToLowerInvariant()
                if ($fieldOrder -in @('tt', 'bb', 'tb', 'bt')) {
                    $isConfirmedInterlaced = $true
                    $srcFps = Format-MediaFps $meta.avg_frame_rate
                    $dstFps = Get-DoubleFpsDisplay ([string]$meta.avg_frame_rate)
                    if ($srcFps -and $srcFps -ne '—' -and $dstFps) {
                        $autoText = ((L 'fps.auto_detected') -f $srcFps,$dstFps)
                    }
                }
            }
        }

        if ($isConfirmedInterlaced) {
            if ($cmbFps.Items.Count -gt 0) { $cmbFps.Items[0] = $autoText }
            $cmbFps.SelectedIndex = 0
            $cmbFps.Enabled = $false
        } else {
            if ($cmbFps.Items.Count -gt 0) { $cmbFps.Items[0] = L 'fps.auto_film' }
            $cmbFps.Enabled = $true
        }
    } else {
        if ($cmbFps.Items.Count -gt 0) { $cmbFps.Items[0] = L 'fps.auto_film' }
        $cmbFps.Enabled = $true
    }
}

function Test-SelectedMediaIsHdr {
    $selected = @($listFiles.SelectedItems)
    if ($selected.Count -ne 1) { return $false }
    $path = [string]$selected[0].Tag
    if (-not $path) { return $false }
    $key = $path.ToLowerInvariant()
    if (-not $script:ProbeVideoMeta.ContainsKey($key)) { return $false }
    $transfer = ([string]$script:ProbeVideoMeta[$key].color_transfer).ToLowerInvariant()
    return ($transfer -eq 'smpte2084' -or $transfer -eq 'arib-std-b67')
}

function Update-HdrCompatibilityUi {
    if (-not $chkInterpolation -or -not $chkUpload -or -not $grpLut) { return }
    $isHdr = Test-SelectedMediaIsHdr
    if ($cmbCodec.SelectedIndex -eq 3) { return }

    if ($isHdr -and $script:HdrPolicy -eq 'PRESERVE') {
        $chkInterpolation.Enabled = $false
        $cmbInterpolationMode.Enabled = $false
        $chkUpload.Enabled = $false
        $cmbUploadBitrate.Enabled = $false
        $chkUploadBitrateAuto.Enabled = $false
        $grpLut.Enabled = $false
        $toolTip.SetToolTip($chkInterpolation, (L 'tooltip.hdr_interp_preserve'))
        $toolTip.SetToolTip($chkUpload, (L 'tooltip.hdr_upload_preserve'))
        $toolTip.SetToolTip($grpLut, (L 'tooltip.hdr_lut_preserve'))
        if ($cmbCodec.SelectedIndex -eq 2) {
            $btnStart.Enabled = $false
            $toolTip.SetToolTip($cmbCodec, (L 'tooltip.hdr_codec_preserve'))
        } else {
            $btnStart.Enabled = $true
        }
        return
    }

    $grpLut.Enabled = $true
    if ($cmbCodec.SelectedIndex -ne 2) {
        $chkUpload.Enabled = (-not $script:HardwareCapsReady -or $script:X264Available)
        $cmbUploadBitrate.Enabled = ($chkUpload.Enabled -and $chkUpload.Checked)
        $chkUploadBitrateAuto.Enabled = ($chkUpload.Enabled -and $chkUpload.Checked)
    }
    $chkInterpolation.Enabled = $true
    $btnStart.Enabled = $true
    if ($isHdr -and $script:HdrPolicy -ne 'PRESERVE') {
        $toneLabel = switch ($script:ToneMapAlgo) { 'mobius' { 'Mobius' } 'reinhard' { 'Reinhard' } 'gamma' { 'Gamma' } 'linear' { 'Linear' } 'clip' { 'Clip' } default { 'Hable' } }
        $toolTip.SetToolTip($chkInterpolation, ((L 'tooltip.hdr_interp_sdr') -f $toneLabel))
        $toolTip.SetToolTip($chkUpload, ((L 'tooltip.hdr_upload_sdr') -f $toneLabel))
        $toolTip.SetToolTip($grpLut, ((L 'tooltip.hdr_lut_sdr') -f $toneLabel))
    } else {
        $toolTip.SetToolTip($chkUpload, (L 'tooltip.upload'))
    }
}

function Update-InterpolationUi {
    if (-not $chkInterpolation) { return }

    if ($cmbCodec.SelectedIndex -eq 3) {
        $chkInterpolation.Checked = $false
        $chkInterpolation.Enabled = $false
        $cmbInterpolationMode.Enabled = $false
        return
    }

    $chkInterpolation.Enabled = $true
    if ($chkInterpolation.Checked) {
        $cmbInterpolationMode.Enabled = $true
        if ($cmbDeint.SelectedIndex -ne 0) { $cmbDeint.SelectedIndex = 0 }
        $cmbDeint.Enabled = $false
        $cmbDeintMethod.Enabled = $true
        if ($cmbFps.Items.Count -gt 0) { $cmbFps.Items[0] = L 'fps.interpolation' }
        $cmbFps.SelectedIndex = 0
        $cmbFps.Enabled = $false

        $chkUpload.Enabled = ($cmbCodec.SelectedIndex -ne 2 -and (-not $script:HardwareCapsReady -or $script:X264Available))
        $cmbUploadBitrate.Enabled = ($chkUpload.Enabled -and $chkUpload.Checked)
        $chkUploadBitrateAuto.Enabled = ($chkUpload.Enabled -and $chkUpload.Checked)
        $btnUploadSubtitle.Enabled = $true
        Update-UploadHighMotionUi
        return
    }

    $cmbInterpolationMode.Enabled = $false
    $cmbDeint.Enabled = $true
    $chkUpload.Enabled = ($cmbCodec.SelectedIndex -ne 2 -and (-not $script:HardwareCapsReady -or $script:X264Available))
    $cmbUploadBitrate.Enabled = ($chkUpload.Enabled -and $chkUpload.Checked)
    $chkUploadBitrateAuto.Enabled = ($chkUpload.Enabled -and $chkUpload.Checked)
    Update-DeinterlaceUi
    Update-UploadHighMotionUi
}

function Update-FramingUi {
    if ($script:UpdatingFramingUi) { return }
    $script:UpdatingFramingUi = $true
    try {
        $customCrop = [int]$script:CinematicCropPerSide
        $selectedFrameIndex = $cmbFrameMode.SelectedIndex

        if ($customCrop -gt 0) {
            $chkCinematic.Text = ((L 'cinematic.custom') -f $customCrop)
            $letterboxText = ((L 'cinematic.letterbox_custom') -f $customCrop)
            $cropText = ((L 'cinematic.crop_custom') -f $customCrop)
        } else {
            $chkCinematic.Text = L 'cinematic.enable'
            $letterboxText = L 'cinematic.letterbox'
            $cropText = L 'cinematic.crop'
        }

        if ($cmbFrameMode.Items.Count -gt 0 -and [string]$cmbFrameMode.Items[0] -ne $letterboxText) {
            $cmbFrameMode.Items[0] = $letterboxText
        }
        if ($cmbFrameMode.Items.Count -gt 1 -and [string]$cmbFrameMode.Items[1] -ne $cropText) {
            $cmbFrameMode.Items[1] = $cropText
        }
        if ($selectedFrameIndex -ge 0 -and $cmbFrameMode.SelectedIndex -ne $selectedFrameIndex) {
            $cmbFrameMode.SelectedIndex = $selectedFrameIndex
        }

        if ($cmbCodec.SelectedIndex -eq 3) {
            $cmbFrameMode.Enabled = $false
            $frameHelp.Text = L 'cinematic.copy_help'
            return
        }

        $enabled = $chkCinematic.Checked
        $cmbFrameMode.Enabled = $enabled
        if (-not $enabled) {
            $frameHelp.Text = L 'cinematic.off_help'
            return
        }

        if ($cmbFrameMode.SelectedIndex -eq 0) {
            if ($customCrop -gt 0) {
                $frameHelp.Text = ((L 'cinematic.custom_letterbox') -f $customCrop)
            } else {
                $frameHelp.Text = L 'cinematic.auto_letterbox'
            }
        } else {
            if ($customCrop -gt 0) {
                $totalCrop = $customCrop * 2
                $frameHelp.Text = ((L 'cinematic.custom_crop') -f $customCrop,$totalCrop)
            } else {
                $frameHelp.Text = L 'cinematic.auto_crop'
            }
        }
    } finally {
        $script:UpdatingFramingUi = $false
    }
}

function Update-CodecUi {
    $newIndex = $cmbCodec.SelectedIndex
    if ($newIndex -lt 0) { return }

    if ($newIndex -eq 3) {
        $script:NoReencodeUiActive = $true
        if ($cmbContainer.Items.Count -ge 2) {
            $cmbContainer.Items[0] = L 'container.mp4_compat'
            $cmbContainer.Items[1] = L 'container.mkv_recommended'
        }
        $pnlHevc.Visible = $false
        $pnlAv1.Visible = $true
        $pnlAv1.BringToFront()
        $grpGrain.Text = L 'grain.av1_copy'
        Update-Av1Controls

        $cmbContainer.Enabled = $true
        $cmbSpeed.Enabled = $false
        $cmbBitrate.Enabled = $false
        $cmbFps.Enabled = $false
        $chkInterpolation.Checked = $false
        $chkInterpolation.Enabled = $false
        $cmbInterpolationMode.Enabled = $false
        $cmbDeint.Enabled = $false
        $cmbDeintMethod.Enabled = $false
        $chkCinematic.Enabled = $false
        $cmbFrameMode.Enabled = $false
        $chkUpload.Enabled = $false
        $cmbUploadBitrate.Enabled = $false
        $chkUploadBitrateAuto.Enabled = $false
        $chkUploadHighMotion.Enabled = $false
        $chkBitrateAuto.Enabled = $false
        $btnUploadSubtitle.Enabled = $false
        $grpLut.Enabled = $false
        $frameHelp.Text = L 'cinematic.copy_help_full'
        $btnStart.Text = L 'button.start_process'
        Update-SfeUi
        return
    }

    # Keep the normal AV1 / HEVC startup path identical to the v4.2.1 stable baseline.
    # Only restore controls when we are actually leaving the no-reencode mode.
    if ($script:NoReencodeUiActive) {
        $script:NoReencodeUiActive = $false
        if ($cmbContainer.Items.Count -ge 2) {
            $cmbContainer.Items[0] = L 'container.mp4'
            $cmbContainer.Items[1] = L 'container.mkv'
        }
        $cmbContainer.Enabled = $true
        $cmbSpeed.Enabled = $true
        $cmbBitrate.Enabled = $true
        $chkBitrateAuto.Enabled = $true
        $chkInterpolation.Enabled = $true
        $cmbDeint.Enabled = $true
        $chkCinematic.Enabled = $true
        $chkUpload.Enabled = $true
        $cmbUploadBitrate.Enabled = $chkUpload.Checked
        $chkUploadBitrateAuto.Enabled = $chkUpload.Checked
        $btnUploadSubtitle.Enabled = $true
        $grpLut.Enabled = $true
        $btnStart.Text = L 'button.start_encode'
        Update-DeinterlaceUi
        Update-FramingUi
        Update-UploadHighMotionUi
        Set-LutUi
        Update-InterpolationUi
    }

    if (-not $script:ChangingCodec -and $newIndex -eq 0 -and $script:HardwareCapsReady -and -not $script:Av1Available) {
        $fallback = if ($script:HevcAvailable) { 1 } else { 2 }
        $script:ChangingCodec = $true
        try { $cmbCodec.SelectedIndex = $fallback }
        finally { $script:ChangingCodec = $false }
        $fallbackMessage = if ($fallback -eq 1) { L 'message.av1_fallback_hevc' } else { L 'message.av1_fallback_x264' }
        Show-Info $fallbackMessage
        return
    }

    if (-not $script:ChangingCodec -and $newIndex -eq 2 -and $script:HardwareCapsReady -and -not $script:X264Available) {
        $fallback = if ($script:HevcAvailable) { 1 } elseif ($script:Av1Available) { 0 } else { -1 }
        if ($fallback -ge 0) {
            $script:ChangingCodec = $true
            try { $cmbCodec.SelectedIndex = $fallback }
            finally { $script:ChangingCodec = $false }
            $fallbackMessage = if ($fallback -eq 1) { L 'message.x264_fallback_hevc' } else { L 'message.x264_fallback_av1' }
            Show-Info $fallbackMessage
            return
        }
    }

    if (-not $script:ChangingCodec) {
        $script:ChangingCodec = $true
        try {
            if ($script:LastCodecIndex -ne $newIndex) {
                if ($cmbBitrate.Text -match '^\d+$' -and $script:LastCodecIndex -ge 0 -and $script:LastCodecIndex -le 2) { $script:ModeBitrate[$script:LastCodecIndex] = $cmbBitrate.Text.Trim() }
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
        Update-Av1Controls
        $chkUpload.Enabled = (-not $script:HardwareCapsReady -or $script:X264Available)
    } else {
        $pnlAv1.Visible = $false
        $pnlHevc.Visible = $true
        $pnlHevc.BringToFront()
        if ($newIndex -eq 2) {
            $grpGrain.Text = L 'grain.x264'
            $chkUpload.Enabled = $false
        } else {
            $grpGrain.Text = L 'grain.hevc'
            $chkUpload.Enabled = (-not $script:HardwareCapsReady -or $script:X264Available)
        }
        if ($script:LastScannedGrainRoot -ne $txtGrainRoot.Text.Trim() -or $script:HevcGrainFiles.Count -eq 0) {
            Refresh-HevcGrainPlates
        }
    }
    $cmbUploadBitrate.Enabled = ($chkUpload.Enabled -and $chkUpload.Checked)
    $chkUploadBitrateAuto.Enabled = ($chkUpload.Enabled -and $chkUpload.Checked)
    Update-UploadHighMotionUi
    Update-BitrateDisplays
    Update-FgsimControls
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
            [void]$cmbRecentLut.Items.Add((L 'lut.recent_select'))
            foreach ($entry in $script:RecentLuts) { [void]$cmbRecentLut.Items.Add([string]$entry.DisplayName) }
            $cmbRecentLut.Enabled = $true
        } else {
            [void]$cmbRecentLut.Items.Add((L 'lut.recent_empty'))
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
            [void]$cmbFavoriteLut.Items.Add((L 'lut.favorite_select'))
            foreach ($entry in $script:FavoriteLuts) { [void]$cmbFavoriteLut.Items.Add([string]$entry.DisplayName) }
            $cmbFavoriteLut.Enabled = $true
        } else {
            [void]$cmbFavoriteLut.Items.Add((L 'lut.favorite_empty'))
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
        $lblSelectedLut.Text = L 'lut.none'
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
        Show-Error ((L 'error.lut_gallery_missing') -f $LutSelector)
        return
    }
    if (-not (Test-Path -LiteralPath $LutRoot)) {
        Show-Error ((L 'error.lut_root_missing') -f $LutRoot)
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
            if ($detail) { Show-Error ((L 'error.lut_gallery_failed') -f $rc,$detail) }
            else { Show-Error ((L 'error.lut_gallery_failed_no_detail') -f $rc) }
        }
    } catch {
        Show-Error ((L 'error.lut_gallery_open_failed') -f $_.Exception.Message)
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
        $lblStatus.Text = (L 'status.processing') + $currentName
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
        $lblRunStage.Text = (L 'status.stage') + $lastStage.Groups[1].Value + '/' + $lastStage.Groups[2].Value + ' · ' + $lastStage.Groups[3].Value.Trim()
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
        $lblStatus.Text = L 'status.cancelled'
        $lblStatus.ForeColor = $ColorError
        $lblRunStage.Text = L 'status.cancelled_detail'
    } elseif ($ExitCode -eq 0) {
        $lblStatus.Text = L 'status.completed'
        $lblStatus.ForeColor = $ColorSuccess
        $lblRunStage.Text = L 'status.completed_detail'
        $progress.Value = $progress.Maximum
    } else {
        $lblStatus.Text = ((L 'status.error') -f $ExitCode)
        $lblStatus.ForeColor = $ColorError
        $lblRunStage.Text = L 'status.error_detail'
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
    if ($cmbAv1Method.SelectedIndex -ge 3) {
        Show-Error (L 'error.digital_requires_encode')
        return
    }

    $paths = @(Get-InputPaths)
    if ($paths.Count -ne 1) {
        Show-Error (L 'error.no_reencode_single')
        return
    }
    $path = [string]$paths[0]
    $key = $path.ToLowerInvariant()
    if (-not $script:ProbeVideoMeta.ContainsKey($key) -or ([string]$script:ProbeVideoMeta[$key].codec_name).ToLowerInvariant() -ne 'av1') {
        Show-Error (L 'error.not_confirmed_av1')
        return
    }
    if (-not (Test-Path -LiteralPath $NoReencodeBat -PathType Leaf)) {
        Show-Error ((L 'error.no_reencode_tool_missing') -f $NoReencodeBat)
        return
    }
    if (-not (Test-Path -LiteralPath $Grav1synth -PathType Leaf)) {
        Show-Error ((L 'error.grav_missing') -f $Grav1synth)
        return
    }

    $selectedAv1GrainTable = ''
    if ($cmbAv1Method.SelectedIndex -eq 2) {
        if ($script:Av1GrainTableFiles.Count -eq 0 -or $cmbAv1GrainTable.SelectedIndex -lt 0 -or $cmbAv1GrainTable.SelectedIndex -ge $script:Av1GrainTableFiles.Count) {
            Show-Error (L 'error.no_grain_table')
            return
        }
        $selectedAv1GrainTable = [string]$script:Av1GrainTableFiles[$cmbAv1GrainTable.SelectedIndex]
        if (-not (Test-Path -LiteralPath $selectedAv1GrainTable -PathType Leaf)) {
            Refresh-Av1GrainTables
            Show-Error (L 'error.grain_table_missing')
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
    $lblRunStage.Text = L 'status.start_copy'
    $lblRunMetric.Text = L 'status.copy_metric'
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
        $lblStatus.Text = L 'status.copy_running'
        $pollTimer.Start()
    } catch {
        Set-RunningState $false
        if ($proc) { try { $proc.Dispose() } catch {} }
        $script:RunningProcess = $null
        $script:OutputReadTask = $null
        $script:ErrorReadTask = $null
        $script:OutputStreamClosed = $true
        $script:ErrorStreamClosed = $true
        Show-Error ((L 'error.no_reencode_start_failed') -f $_.Exception.Message)
    }
}

function Start-Encoding {
    if ($cmbCodec.SelectedIndex -eq 3) {
        Start-NoReencodeProcessing
        return
    }

    if (-not (Test-Path -LiteralPath $CoreBat)) {
        Show-Error ((L 'error.bridge_missing') -f $CoreBat)
        return
    }

    if (-not $script:HardwareCapsReady) {
        Initialize-HardwareCaps
        $script:FFmpegVersionOverride = ''
        Update-HardwareProfileUi
        Update-CodecUi
        Update-SfeUi
    }

    if ($chkInterpolation.Checked -and -not $script:OpenSvpAvailable) {
        Initialize-HardwareCaps
        Update-HardwareProfileUi
        Update-SfeUi
        if (-not $script:OpenSvpAvailable) {
            Show-Error ((L 'error.svp_runtime') -f $OpenSvpSetupBat)
            return
        }
    }

    $paths = @(Get-InputPaths)
    if ($paths.Count -eq 0) {
        Show-Info (L 'info.add_video')
        return
    }

    $bitrate = 0L
    if (-not [long]::TryParse((Get-EncodingBitrateText), [ref]$bitrate) -or $bitrate -le 10 -or $bitrate -gt 500000000) {
        Show-Error (L 'error.bitrate_invalid')
        return
    }
    $bitrateAuto = [bool]$script:ModeBitrateAuto[$cmbCodec.SelectedIndex]
    $maxrate = [long]($bitrate * 3)
    $bufsize = [long]($bitrate * 6)

    $uploadBitrate = 0L
    $uploadBitrateAuto = [bool]$script:UploadBitrateAuto
    if ($cmbCodec.SelectedIndex -ne 2 -and $chkUpload.Checked) {
        if (-not [long]::TryParse($cmbUploadBitrate.Text.Trim(), [ref]$uploadBitrate) -or $uploadBitrate -le 10 -or $uploadBitrate -gt 500000000) {
            Show-Error (L 'error.upload_bitrate_invalid')
            return
        }
    }

    if ($chkLut.Checked) {
        if (-not $script:SelectedLutPath) {
            Show-Info (L 'info.lut_not_selected')
            return
        }
        if (-not (Test-Path -LiteralPath $script:SelectedLutPath)) {
            Show-Error ((L 'error.selected_lut_missing') -f $script:SelectedLutPath)
            return
        }
    }

    $mode = if ($cmbCodec.SelectedIndex -eq 0) { 'AV1' } elseif ($cmbCodec.SelectedIndex -eq 1) { 'HEVC' } else { 'X264' }
    if ($script:HardwareCapsReady -and $mode -eq 'AV1' -and -not $script:Av1Available) {
        Show-Error (L 'error.av1_unavailable')
        return
    }
    if ($script:HardwareCapsReady -and $mode -eq 'HEVC' -and -not $script:HevcAvailable) {
        Show-Error (L 'error.hevc_unavailable')
        return
    }
    if ($script:HardwareCapsReady -and $mode -eq 'X264' -and -not $script:X264Available) {
        Show-Error (L 'error.x264_unavailable')
        return
    }
    if ($mode -ne 'X264' -and $cmbSpeed.SelectedIndex -eq 2 -and ($mode -ne 'AV1' -or -not $script:Av1UhqAvailable)) {
        Show-Error (L 'error.uhq_unavailable')
        return
    }
    $selectedAv1GrainTable = ''
    if ($mode -eq 'AV1' -and $cmbAv1Method.SelectedIndex -eq 2) {
        if ($script:Av1GrainTableFiles.Count -eq 0 -or $cmbAv1GrainTable.SelectedIndex -lt 0 -or $cmbAv1GrainTable.SelectedIndex -ge $script:Av1GrainTableFiles.Count) {
            Show-Error (L 'error.no_grain_table')
            return
        }
        $selectedAv1GrainTable = [string]$script:Av1GrainTableFiles[$cmbAv1GrainTable.SelectedIndex]
        if (-not (Test-Path -LiteralPath $selectedAv1GrainTable -PathType Leaf)) {
            Refresh-Av1GrainTables
            Show-Error (L 'error.grain_table_missing')
            return
        }
    }

    $selectedGrainPath = $null
    $selectedProcStrength = ''
    $selectedFgsimPreset = ''
    if ($mode -eq 'HEVC' -or $mode -eq 'X264') {
        $grainRoot = $txtGrainRoot.Text.Trim()
        if ($script:LastScannedGrainRoot -ne $grainRoot -or $script:HevcGrainFiles.Count -lt 2) {
            Refresh-HevcGrainPlates
        }
        if ($script:HevcGrainFiles.Count -lt 2 -or $cmbHevcPlate.SelectedIndex -lt 0 -or $cmbHevcPlate.SelectedIndex -ge $script:HevcGrainFiles.Count) {
            Show-Error (L 'error.no_grain_option')
            return
        }
        $selectedGrainPath = [string]$script:HevcGrainFiles[$cmbHevcPlate.SelectedIndex]
        if ($selectedGrainPath -eq '::PROC30::' -or $selectedGrainPath -eq '::PROC55::') {
            $selectedProcStrength = [string]$trackHevcProcStrength.Value
        } elseif ($selectedGrainPath -eq '::FGSIM_LIGHT::') {
            $selectedFgsimPreset = 'LIGHT'
        } elseif ($selectedGrainPath -eq '::FGSIM_MEDIUM::') {
            $selectedFgsimPreset = 'MEDIUM'
        } elseif ($selectedGrainPath -eq '::FGSIM_HEAVY::') {
            $selectedFgsimPreset = 'HEAVY'
        } else {
            if (-not (Test-Path -LiteralPath $selectedGrainPath -PathType Leaf)) {
                Refresh-HevcGrainPlates
                Show-Error (L 'error.selected_plate_missing')
                return
            }
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
    $lblRunStage.Text = L 'status.start_bridge'
    $lblRunMetric.Text = 'fps: —   speed: —'
    Append-LogText ("Film Grain Studio`r`n" + ('=' * 68) + "`r`n")
    $rateModeLabel = if ($bitrateAuto) { '自动推荐' } else { '手动' }
    $motionLabel = if ($chkUploadHighMotion.Checked) { '高动态' } else { '普通动态' }
    Append-LogText ("任务文件数：$($paths.Count)  ·  模式：$mode  ·  码率：$rateModeLabel / 当前显示 $bitrate kbps  ·  $motionLabel`r`n")
    if ($mode -eq 'AV1') {
        if ($chkSfe.Checked -and $script:SfeMaxEngines -ge 2 -and ($cmbSpeed.SelectedIndex -eq 1 -or $cmbSpeed.SelectedIndex -eq 2)) {
            Append-LogText ("多引擎并行：ON ×$($script:SfeMaxEngines) · NVENC Split Frame Encoding`r`n")
        } else {
            Append-LogText "多引擎并行：OFF`r`n"
        }
    }
    if ($chkInterpolation.Checked) {
        $sceneLabel = 'Uniform'
        if ($cmbInterpolationMode.SelectedIndex -eq 1) { $sceneLabel = 'Adaptive' }
        $analyseLabel = 'EncodeGUI Analyse'
        if ($script:SvpAnalyse -eq 'BASE') { $analyseLabel = 'Baseline Analyse' }
        Append-LogText "OpenSVPFlow：60 fps · $sceneLabel · Algo $($script:SvpAlgo) · $analyseLabel · Mask $($script:SvpMaskArea) · GPU/OpenCL`r`n"
    }
    if ($bitrateAuto) {
        Append-LogText ("GUI 自动推荐当前显示：b:v ${bitrate}k  ·  maxrate ${maxrate}k  ·  bufsize ${bufsize}k；Bridge 将按每个文件实际输出重新计算。`r`n")
    } else {
        Append-LogText ("GUI 手动码率：b:v ${bitrate}k  ·  maxrate ${maxrate}k  ·  bufsize ${bufsize}k`r`n")
    }
    $toneLabel = switch ($script:ToneMapAlgo) { 'mobius' { 'Mobius' } 'reinhard' { 'Reinhard' } 'gamma' { 'Gamma' } 'linear' { 'Linear' } 'clip' { 'Clip' } default { 'Hable' } }
    if ($script:HdrPolicy -eq 'SDR') {
        Append-LogText ("HDR 输入：强制 Tone Mapping 到 BT.709 SDR · $toneLabel · 10-bit 无损工作文件`r`n")
    } elseif ($script:HdrPolicy -eq 'PRESERVE') {
        Append-LogText "HDR 输入：保持 HDR Preserve；不兼容的 SDR 功能继续按现有规则旁路`r`n"
    } else {
        Append-LogText ("HDR 输入：自动兼容 · 可保持时 HDR Preserve；遇到 x264 / LUT / OpenSVPFlow / H.264 上传版时自动 Tone Mapping 到 SDR · $toneLabel`r`n")
    }

    $x264PresetLabel = switch ($script:X264Preset) { 'medium' { 'Medium' } 'slow' { 'Slow' } default { 'Faster' } }
    $x264PassLabel = if ($script:X264RateMode -eq '3PASS') { 'VBR 3-Pass' } elseif ($script:X264RateMode -eq '2PASS') { 'VBR 2-Pass' } else { 'VBR 单次' }
    if ($mode -eq 'X264') {
        Append-LogText ("x264 Grain：$x264PresetLabel + tune grain + $x264PassLabel`r`n")
    }
    if ($mode -ne 'X264' -and $chkUpload.Checked) {
        $uploadRateModeLabel = if ($uploadBitrateAuto) { '自动推荐' } else { '手动' }
        Append-LogText ("H.264 上传版：x264 $x264PresetLabel + tune grain + $x264PassLabel · $uploadRateModeLabel / 当前显示 ${uploadBitrate} kbps · 与全局 $motionLabel 同步；Bridge 将按每个文件实际输出确认。`r`n")
    }
    if ($mode -eq 'AV1' -and ($cmbAv1Method.SelectedIndex -eq 3 -or $cmbAv1Method.SelectedIndex -eq 4)) {
        $procUiStrength = [string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:0.00}', ($trackAv1ProcStrength.Value / 100.0))
        Append-LogText ("数字颗粒：Fast Noise · 滑杆 $procUiStrength · SDR/HDR 自适应路径 · HDR 使用 10-bit 亮度颗粒 · 直接烘焙到像素`r`n")
    } elseif (($mode -eq 'HEVC' -or $mode -eq 'X264') -and $selectedProcStrength) {
        $procUiStrength = [string]::Format([System.Globalization.CultureInfo]::InvariantCulture, '{0:0.00}', ([int]$selectedProcStrength / 100.0))
        Append-LogText ("数字颗粒：Fast Noise · 滑杆 $procUiStrength · SDR/HDR 自适应路径 · HDR 使用 10-bit 亮度颗粒 · 直接烘焙到像素`r`n")
    } elseif ($mode -eq 'AV1' -and $cmbAv1Method.SelectedIndex -ge 5) {
        $fgsimNames = @('LIGHT','MEDIUM','HEAVY')
        $fgsimPresetLog = $fgsimNames[$cmbAv1Method.SelectedIndex - 5]
        Append-LogText ("GPU Film Grain (FGSIM)：$fgsimPresetLog · Tile2 · Highlight Protect 50% · SDR 像素烘焙`r`n")
    } elseif (($mode -eq 'HEVC' -or $mode -eq 'X264') -and $selectedFgsimPreset) {
        Append-LogText ("GPU Film Grain (FGSIM)：$selectedFgsimPreset · Tile2 · Highlight Protect 50% · SDR 像素烘焙`r`n")
    }
    Append-LogText "`r`n"

    # Studio normally treats Recent and Favorites as read-only. Only a LUT
    # selected from Favorites is registered once, through Gallery itself,
    # when the user actually clicks Start Encoding.
    if ($chkLut.Checked -and $script:SelectedLutSource -eq 'Favorite') {
        if (Register-FavoriteLutRecentUse $script:SelectedLutPath) {
            Append-LogText "[LUT Recent] 已由 LUT Gallery 登记本次我的最爱选择。`r`n"
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
    $envs['FG_HDR_POLICY'] = [string]$script:HdrPolicy
    $envs['FG_TONEMAP_ALGO'] = [string]$script:ToneMapAlgo
    $speedModes = @('FAST', 'STANDARD', 'UHQ')
    $envs['FG_FGSIM_QUALITY'] = if ($mode -eq 'HEVC' -and $selectedFgsimPreset) { $script:FgsimRcChoice } else { 'STANDARD' }
    $envs['FG_SPEED'] = if ($mode -eq 'X264') { 'X264' } else { $speedModes[$cmbSpeed.SelectedIndex] }
    $sfeEngines = 0
    if ($mode -eq 'AV1' -and $chkSfe.Checked -and $script:SfeMaxEngines -ge 2 -and $script:Grav1synthSfeCompatible -and ($cmbSpeed.SelectedIndex -eq 1 -or $cmbSpeed.SelectedIndex -eq 2)) {
        $sfeEngines = [int]$script:SfeMaxEngines
    }
    $envs['FG_SFE_ENGINES'] = [string]$sfeEngines
    $envs['FG_BITRATE_MODE'] = if ($bitrateAuto) { 'AUTO' } else { 'MANUAL' }
    $envs['FG_BITRATE'] = [string]$bitrate
    $envs['FG_MAXRATE'] = [string]$maxrate
    $envs['FG_BUFSIZE'] = [string]$bufsize
    $envs['FG_HIGH_MOTION'] = if ($chkUploadHighMotion.Checked) { '1' } else { '0' }
    $envs['FG_H264_HIGH10'] = if ($script:H264High10) { '1' } else { '0' }
    $envs['FG_X264_PRESET'] = [string]$script:X264Preset
    $envs['FG_X264_PASS_MODE'] = [string]$script:X264RateMode
    $envs['FG_FPS_MODE'] = if ($cmbFps.SelectedIndex -eq 0) { 'AUTO' } else { 'SOURCE' }
    $envs['FG_SVP_INTERPOLATE'] = if ($chkInterpolation.Checked) { '1' } else { '0' }
    $envs['FG_SVP_ALGO'] = [string]$script:SvpAlgo
    $envs['FG_SVP_ANALYSE'] = [string]$script:SvpAnalyse
    if ($cmbInterpolationMode.SelectedIndex -eq 1) {
        $envs['FG_SVP_SCENE_MODE'] = '3'
    } else {
        $envs['FG_SVP_SCENE_MODE'] = '0'
    }
    $envs['FG_SVP_MASK_AREA'] = [string]$script:SvpMaskArea
    $envs['FG_DEINTERLACE'] = if ($cmbDeint.SelectedIndex -eq 0) { 'AUTO' } else { 'OFF' }
    $deintMethods = @('BWDIF_VULKAN', 'BWDIF_CUDA', 'W3FDIF')
    $envs['FG_DEINT_METHOD'] = $deintMethods[$cmbDeintMethod.SelectedIndex]
    $envs['FG_CINEMATIC_FRAME'] = if ($chkCinematic.Checked) { '1' } else { '0' }
    $envs['FG_FRAME_MODE'] = if ($cmbFrameMode.SelectedIndex -eq 0) { 'LETTERBOX' } else { 'CROP' }
    $envs['FG_CROP_PER_SIDE'] = [string]$script:CinematicCropPerSide
    $envs['FG_KEEP_FAILED'] = '1'
    $envs['FG_UPLOAD'] = if ($mode -ne 'X264' -and $chkUpload.Checked) { '1' } else { '0' }
    $envs['FG_UPLOAD_MODE'] = 'X264'
    $envs['FG_UPLOAD_BITRATE_MODE'] = if ($uploadBitrateAuto) { 'AUTO' } else { 'MANUAL' }
    $envs['FG_UPLOAD_BITRATE'] = [string]$uploadBitrate
    $envs['FG_UPLOAD_MAXRATE'] = [string]([long]($uploadBitrate * 3))
    $envs['FG_UPLOAD_BUFSIZE'] = [string]([long]($uploadBitrate * 6))
    $envs['FG_UPLOAD_HIGH_MOTION'] = if ($chkUploadHighMotion.Checked) { '1' } else { '0' }
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
        if ($cmbAv1Method.SelectedIndex -eq 3 -or $cmbAv1Method.SelectedIndex -eq 4) {
            $envs['FG_GRAIN_ENGINE'] = 'PROCEDURAL'
            $envs['FG_PROC_STRENGTH'] = [string]$trackAv1ProcStrength.Value
            [void]$envs.Remove('FG_AV1_GRAIN_TABLE')
        } elseif ($cmbAv1Method.SelectedIndex -ge 5 -and $cmbAv1Method.SelectedIndex -le 7) {
            $envs['FG_GRAIN_ENGINE'] = 'FGSIM'
            $fgsimPresets = @('LIGHT','MEDIUM','HEAVY')
            $envs['FG_FGSIM_PRESET'] = $fgsimPresets[$cmbAv1Method.SelectedIndex - 5]
            [void]$envs.Remove('FG_AV1_GRAIN_TABLE')
        } else {
            $envs['FG_GRAIN_ENGINE'] = 'NATIVE'
            $av1Modes = @('PRESET', 'ISO', 'TABLE')
            $envs['FG_AV1_GRAIN_MODE'] = $av1Modes[$cmbAv1Method.SelectedIndex]
            $envs['FG_AV1_FORMAT'] = [string]($cmbAv1Format.SelectedIndex + 1)
            $envs['FG_AV1_STOCK'] = [string]($cmbAv1Stock.SelectedIndex + 1)
            $envs['FG_AV1_ISO'] = [string][int]$numIso.Value
            $envs['FG_AV1_CHROMA'] = if ($chkChroma.Checked) { '1' } else { '0' }
            if ($selectedAv1GrainTable) { $envs['FG_AV1_GRAIN_TABLE'] = $selectedAv1GrainTable }
            else { [void]$envs.Remove('FG_AV1_GRAIN_TABLE') }
        }
    } else {
        if ($selectedProcStrength) {
            $envs['FG_GRAIN_ENGINE'] = 'PROCEDURAL'
            $envs['FG_PROC_STRENGTH'] = $selectedProcStrength
            [void]$envs.Remove('FG_HEVC_GRAIN_PATH')
            [void]$envs.Remove('FG_HEVC_GRAIN_TAG')
        } elseif ($selectedFgsimPreset) {
            $envs['FG_GRAIN_ENGINE'] = 'FGSIM'
            $envs['FG_FGSIM_PRESET'] = $selectedFgsimPreset
            [void]$envs.Remove('FG_HEVC_GRAIN_PATH')
            [void]$envs.Remove('FG_HEVC_GRAIN_TAG')
        } else {
            $envs['FG_GRAIN_ENGINE'] = 'NATIVE'
            $envs['FG_GRAIN_ROOT'] = $txtGrainRoot.Text.Trim()
            $envs['FG_HEVC_GRAIN_PATH'] = $selectedGrainPath
            $grainTag = [System.IO.Path]::GetFileNameWithoutExtension($selectedGrainPath)
            $grainTag = [System.Text.RegularExpressions.Regex]::Replace($grainTag, '[^\p{L}\p{Nd}]+', '_').Trim('_')
            if (-not $grainTag) { $grainTag = 'SCAN' }
            if ($grainTag.Length -gt 48) { $grainTag = $grainTag.Substring(0, 48).TrimEnd('_') }
            $envs['FG_HEVC_GRAIN_TAG'] = $grainTag
            $envs['FG_HEVC_STRENGTH_SEL'] = [string]($trackHevcStrength.Value + 1)
        }
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
        $lblStatus.Text = L 'status.running'
        $pollTimer.Start()
    } catch {
        Set-RunningState $false
        if ($proc) { try { $proc.Dispose() } catch {} }
        $script:RunningProcess = $null
        $script:OutputReadTask = $null
        $script:ErrorReadTask = $null
        $script:OutputStreamClosed = $true
        $script:ErrorStreamClosed = $true
        Show-Error ((L 'error.encode_start_failed') -f $_.Exception.Message)
    }
}

function Stop-Encoding {
    if (-not $script:RunningProcess) { return }
    try {
        if ($script:RunningProcess.HasExited) { return }
    } catch { return }

    $answer = [System.Windows.Forms.MessageBox]::Show(
        $form,
        (L 'cancel.encode_confirm'),
        (L 'cancel.encode_title'),
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Warning
    )
    if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }

    $script:RunWasCancelled = $true
    $lblStatus.Text = L 'status.cancelling'
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
    $runButton.Text = L 'common.cancel'; $runButton.Width = 88; $runButton.Height = 30; $runButton.Margin=New-Object System.Windows.Forms.Padding -ArgumentList 6,7,10,5
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
                        $runButton.Text=(L 'common.close')
                    }
                }
            } catch {}
        }
    })

    $runButton.Add_Click({
        if ($state.Process -and -not $state.Finished) {
            $answer=[System.Windows.Forms.MessageBox]::Show($runDlg,(L 'task.cancel_confirm'),(L 'task.cancel_title'),[System.Windows.Forms.MessageBoxButtons]::YesNo,[System.Windows.Forms.MessageBoxIcon]::Warning)
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
            $runButton.Text=(L 'common.close')
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
    $oldGravPath = [string]$cfg.GRAV1SYNTH
    $oldGrainRoot = [string]$cfg.GRAIN_ROOT
    $oldLutRoot = [string]$cfg.LUT_ROOT

    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = L 'config.title'
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
        $b.Text=(L 'config.browse'); $b.Dock='Fill'; $b.Margin=New-Object System.Windows.Forms.Padding -ArgumentList 4,5,4,5
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
    $btnBuildGrainCache.Text=(L 'config.build_cache'); $btnBuildGrainCache.Dock='Fill'; $btnBuildGrainCache.Enabled=$false
    $btnBuildGrainCache.Margin=New-Object System.Windows.Forms.Padding -ArgumentList 4,3,2,3
    $btnBuildLutPreviews = New-Object System.Windows.Forms.Button
    $btnBuildLutPreviews.Text=(L 'config.build_thumbs'); $btnBuildLutPreviews.Dock='Fill'; $btnBuildLutPreviews.Enabled=$false
    $btnBuildLutPreviews.Margin=New-Object System.Windows.Forms.Padding -ArgumentList 4,3,2,3

    Add-ConfigLabel 0 (L 'config.ffmpeg_dir')
    [void]$table.Controls.Add($txtCfgFfmpegDir,1,0)
    [void]$table.Controls.Add($btnCfgFfmpegDir,2,0)
    [void]$table.Controls.Add($btnRefreshFfmpeg,3,0)
    $lblFfmpegDetect = New-ConfigStatusLabel (L 'config.not_checked')
    [void]$table.Controls.Add($lblFfmpegDetect,1,1); $table.SetColumnSpan($lblFfmpegDetect,3)

    Add-ConfigLabel 2 'grav1synth'
    [void]$table.Controls.Add($txtCfgGrav,1,2)
    [void]$table.Controls.Add($btnCfgGrav,2,2)
    [void]$table.Controls.Add($btnRefreshGrav,3,2)
    $lblGravDetect = New-ConfigStatusLabel (L 'config.not_checked')
    [void]$table.Controls.Add($lblGravDetect,1,3); $table.SetColumnSpan($lblGravDetect,3)

    Add-ConfigLabel 4 (L 'config.grain_root')
    [void]$table.Controls.Add($txtCfgGrain,1,4)
    [void]$table.Controls.Add($btnCfgGrain,2,4)
    [void]$table.Controls.Add($btnRefreshGrain,3,4)
    $lblGrainDetect = New-ConfigStatusLabel (L 'config.not_checked')
    [void]$table.Controls.Add($lblGrainDetect,1,5)
    [void]$table.Controls.Add($btnBuildGrainCache,2,5); $table.SetColumnSpan($btnBuildGrainCache,2)

    Add-ConfigLabel 6 (L 'config.lut_root')
    [void]$table.Controls.Add($txtCfgLut,1,6)
    [void]$table.Controls.Add($btnCfgLut,2,6)
    [void]$table.Controls.Add($btnRefreshLut,3,6)
    $lblLutDetect = New-ConfigStatusLabel (L 'config.not_checked')
    [void]$table.Controls.Add($lblLutDetect,1,7)
    [void]$table.Controls.Add($btnBuildLutPreviews,2,7); $table.SetColumnSpan($btnBuildLutPreviews,2)

    $note=New-Object System.Windows.Forms.Label
    $note.Dock='Fill'; $note.ForeColor=$ColorMuted; $note.TextAlign='TopLeft'; $note.Padding=New-Object System.Windows.Forms.Padding -ArgumentList 4,8,4,0
    $note.Text=((L 'config.note') -f $cfg.ConfigPath)
    [void]$table.Controls.Add($note,0,8); $table.SetColumnSpan($note,4)

    $buttonPanel=New-Object System.Windows.Forms.FlowLayoutPanel
    $buttonPanel.Dock='Fill'; $buttonPanel.FlowDirection='RightToLeft'; $buttonPanel.WrapContents=$false
    $btnOk=New-Object System.Windows.Forms.Button; $btnOk.Text=(L 'config.save'); $btnOk.Width=82
    $btnCancelCfg=New-Object System.Windows.Forms.Button; $btnCancelCfg.Text=(L 'config.cancel'); $btnCancelCfg.Width=82; $btnCancelCfg.DialogResult=[System.Windows.Forms.DialogResult]::Cancel
    $btnDefaults=New-Object System.Windows.Forms.Button; $btnDefaults.Text=(L 'config.defaults'); $btnDefaults.Width=104
    [void]$buttonPanel.Controls.Add($btnOk); [void]$buttonPanel.Controls.Add($btnCancelCfg); [void]$buttonPanel.Controls.Add($btnDefaults)
    [void]$table.Controls.Add($buttonPanel,0,9); $table.SetColumnSpan($buttonPanel,4)
    $dlg.CancelButton=$btnCancelCfg

    $cfgTip=New-Object System.Windows.Forms.ToolTip
    $cfgTip.SetToolTip($btnRefreshFfmpeg,(L 'config.tip.ffmpeg'))
    $cfgTip.SetToolTip($btnRefreshGrav,(L 'config.tip.grav'))
    $cfgTip.SetToolTip($btnRefreshGrain,(L 'config.tip.grain'))
    $cfgTip.SetToolTip($btnRefreshLut,(L 'config.tip.lut'))
    $cfgTip.SetToolTip($btnBuildGrainCache,(L 'config.tip.cache'))
    $cfgTip.SetToolTip($btnBuildLutPreviews,(L 'config.tip.thumbs'))

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
            return [pscustomobject]@{ Ok=$false; Version=(L 'config.version_not_found') }
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
            if (-not $p.Start()) { return [pscustomobject]@{ Ok=$false; Version=(L 'config.version_cannot_run') } }
            $stdout=$p.StandardOutput.ReadToEnd()
            $stderr=$p.StandardError.ReadToEnd()
            $p.WaitForExit()
            $exitCode=$p.ExitCode
            $p.Dispose()
            if ($exitCode -ne 0) { return [pscustomobject]@{ Ok=$false; Version=(L 'config.version_cannot_run') } }
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
            return [pscustomobject]@{ Ok=$true; Version=(L 'config.version_unknown') }
        } catch {
            return [pscustomobject]@{ Ok=$false; Version=(L 'config.version_cannot_run') }
        }
    }

    function Update-FfmpegDirectoryStatus {
        $dir=$txtCfgFfmpegDir.Text.Trim().TrimEnd('\')
        $ffmpegState.LastDir=$dir
        $ffmpegState.Valid=$false
        $lblFfmpegDetect.Text=(L 'config.detect.ffmpeg')
        [System.Windows.Forms.Application]::DoEvents()

        if (-not $dir -or -not (Test-Path -LiteralPath $dir -PathType Container)) {
            $lblFfmpegDetect.Text=(L 'config.detect.ffmpeg_missing')
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
        $lblGravDetect.Text=(L 'config.detect.grav')
        [System.Windows.Forms.Application]::DoEvents()
        $result=Get-ConfigToolVersion $path 'grav1synth' '--version'
        $mark=if ($result.Ok) { '✔' } else { '✘' }
        $sfeText=''
        if ($result.Ok -and [string]$result.Version -match '(\d+)\.(\d+)\.(\d+)') {
            try {
                $v=[version]("$($matches[1]).$($matches[2]).$($matches[3])")
                $sfeText=if ($v -ge [version]'0.2.2') { L 'config.sfe_compatible' } else { L 'config.sfe_requires' }
            } catch {}
        }
        $lblGravDetect.Text="grav1synth.exe  $mark $($result.Version)$sfeText"
    }

    function Update-GrainStatus {
        $root=$txtCfgGrain.Text.Trim()
        $btnBuildGrainCache.Enabled=$false
        $grainCacheState.MovCount=0; $grainCacheState.FullCount=0; $grainCacheState.Cache1080Count=0; $grainCacheState.Missing=0
        if (-not $root -or -not (Test-Path -LiteralPath $root -PathType Container)) {
            $lblGrainDetect.Text=(L 'config.detect.grain_missing')
            return
        }
        $lblGrainDetect.Text=(L 'config.detect.grain_scan')
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
                $lblGrainDetect.Text=(L 'config.detect.grain_zero')
            } else {
                $lblGrainDetect.Text=((L 'config.detect.grain_count') -f $count,$full,$small)
                $btnBuildGrainCache.Enabled=($grainCacheState.Missing -gt 0)
            }
        } catch {
            $lblGrainDetect.Text=((L 'config.detect.grain_fail') -f $_.Exception.Message)
        }
    }

    function Update-LutStatus {
        $root=$txtCfgLut.Text.Trim()
        $btnBuildLutPreviews.Enabled=$false
        $lutPreviewState.LutCount=0; $lutPreviewState.PreviewCount=0; $lutPreviewState.Missing=0
        if (-not $root -or -not (Test-Path -LiteralPath $root -PathType Container)) {
            $lblLutDetect.Text=(L 'config.detect.lut_missing')
            return
        }
        $lblLutDetect.Text=(L 'config.detect.lut_scan')
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
                $lblLutDetect.Text=(L 'config.detect.lut_zero')
            } else {
                $lblLutDetect.Text=((L 'config.detect.lut_count') -f $count,$previewCount)
                $btnBuildLutPreviews.Enabled=($lutPreviewState.Missing -gt 0)
            }
        } catch {
            $lblLutDetect.Text=((L 'config.detect.lut_fail') -f $_.Exception.Message)
        }
    }

    $pickExe = {
        param($target,$title,$fileName)
        $ofd=New-Object System.Windows.Forms.OpenFileDialog
        $ofd.Title=$title; $ofd.Filter=(L 'dialog.exe_filter'); $ofd.FileName=$fileName
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
        $lblFfmpegDetect.Text=(L 'config.path_changed')
    })
    $txtCfgGrav.Add_TextChanged({ $lblGravDetect.Text=(L 'config.path_changed') })
    $txtCfgGrain.Add_TextChanged({ $lblGrainDetect.Text=(L 'config.path_changed'); $btnBuildGrainCache.Enabled=$false })
    $txtCfgLut.Add_TextChanged({ $lblLutDetect.Text=(L 'config.path_changed'); $btnBuildLutPreviews.Enabled=$false })

    $btnRefreshFfmpeg.Add_Click({ Update-FfmpegDirectoryStatus })
    $btnRefreshGrav.Add_Click({ Update-GravStatus })
    $btnRefreshGrain.Add_Click({ Update-GrainStatus })
    $btnRefreshLut.Add_Click({ Update-LutStatus })

    $btnCfgFfmpegDir.Add_Click({
        $fbd=New-Object System.Windows.Forms.FolderBrowserDialog
        $fbd.Description=(L 'dialog.ffmpeg_folder')
        $fbd.ShowNewFolderButton=$false
        try { if (Test-Path -LiteralPath $txtCfgFfmpegDir.Text -PathType Container) { $fbd.SelectedPath=$txtCfgFfmpegDir.Text } } catch {}
        if ($fbd.ShowDialog($dlg) -eq [System.Windows.Forms.DialogResult]::OK) {
            $txtCfgFfmpegDir.Text=$fbd.SelectedPath
            Update-FfmpegDirectoryStatus
        }
        $fbd.Dispose()
    })
    $btnCfgGrav.Add_Click({ if (& $pickExe $txtCfgGrav (L 'dialog.grav_exe') 'grav1synth.exe') { Update-GravStatus } })
    $btnCfgGrain.Add_Click({ if (& $pickFolder $txtCfgGrain (L 'dialog.grain_root')) { Update-GrainStatus } })
    $btnCfgLut.Add_Click({ if (& $pickFolder $txtCfgLut (L 'dialog.lut_root')) { Update-LutStatus } })

    $btnBuildGrainCache.Add_Click({
        Update-GrainStatus
        if ($grainCacheState.MovCount -le 0 -or $grainCacheState.Missing -le 0) { return }
        if (-not (Test-Path -LiteralPath $GrainCacheBat -PathType Leaf)) {
            [void][System.Windows.Forms.MessageBox]::Show($dlg,((L 'config.cache_tool_missing') -f $GrainCacheBat),'Grain Cache',[System.Windows.Forms.MessageBoxButtons]::OK,[System.Windows.Forms.MessageBoxIcon]::Error)
            return
        }
        $missingFull=$grainCacheState.MovCount-$grainCacheState.FullCount
        $missing1080=$grainCacheState.MovCount-$grainCacheState.Cache1080Count
        $answer=[System.Windows.Forms.MessageBox]::Show($dlg,((L 'config.cache_confirm') -f $missingFull,$missing1080),(L 'config.cache_title'),[System.Windows.Forms.MessageBoxButtons]::YesNo,[System.Windows.Forms.MessageBoxIcon]::Question)
        if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }
        $cmdPath=$env:ComSpec; if (-not $cmdPath) { $cmdPath=Join-Path $env:SystemRoot 'System32\cmd.exe' }
        $inner='call ' + (Quote-CmdArgument $GrainCacheBat) + ' 3 2>&1'
        $ffdir=$txtCfgFfmpegDir.Text.Trim().TrimEnd('\')
        $envMap=@{
            FG_CACHE_NO_PAUSE='1'; FG_GRAIN_ROOT_OVERRIDE=$txtCfgGrain.Text.Trim();
            FG_FFMPEG_OVERRIDE=(Join-Path $ffdir 'ffmpeg.exe'); FG_FFPROBE_OVERRIDE=(Join-Path $ffdir 'ffprobe.exe')
        }
        [void](Show-UtilityProcessDialog $dlg (L 'config.utility_cache') $cmdPath ('/d /s /c "'+$inner+'"') $envMap $ScriptRoot)
        Update-GrainStatus
    })

    $btnBuildLutPreviews.Add_Click({
        Update-LutStatus
        if ($lutPreviewState.LutCount -le 0 -or $lutPreviewState.Missing -le 0) { return }
        $lutReference = if (Test-Path -LiteralPath $LutPreviewCurrentReference -PathType Leaf) { $LutPreviewCurrentReference } else { $LutPreviewDefaultReference }
        $lutReferenceLabel = if ([string]::Equals($lutReference,$LutPreviewCurrentReference,[System.StringComparison]::OrdinalIgnoreCase)) { L 'config.reference_current' } else { L 'config.reference_default' }
        if (-not (Test-Path -LiteralPath $LutPreviewGenerator -PathType Leaf) -or -not (Test-Path -LiteralPath $lutReference -PathType Leaf)) {
            [void][System.Windows.Forms.MessageBox]::Show($dlg,(L 'config.lut_tool_missing'),(L 'config.lut_thumb_title'),[System.Windows.Forms.MessageBoxButtons]::OK,[System.Windows.Forms.MessageBoxIcon]::Error)
            return
        }
        $answer=[System.Windows.Forms.MessageBox]::Show($dlg,((L 'config.lut_confirm') -f $lutReferenceLabel,$lutPreviewState.Missing,$lutReference),(L 'config.lut_create_title'),[System.Windows.Forms.MessageBoxButtons]::YesNo,[System.Windows.Forms.MessageBoxIcon]::Question)
        if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }
        $ffmpegPath=Join-Path $txtCfgFfmpegDir.Text.Trim().TrimEnd('\') 'ffmpeg.exe'
        $outRoot=Join-Path $txtCfgLut.Text.Trim() '_LUT_PREVIEWS'
        $args='-NoLogo -NoProfile -ExecutionPolicy Bypass -File "'+$LutPreviewGenerator+'" -LutRoot "'+$txtCfgLut.Text.Trim()+'" -ReferencePath "'+$lutReference+'" -OutputRoot "'+$outRoot+'" -FFmpegPath "'+$ffmpegPath+'" -NonInteractive -NoPause'
        [void](Show-UtilityProcessDialog $dlg (L 'config.utility_lut') 'powershell.exe' $args @{} $PackageRoot)
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
            [void][System.Windows.Forms.MessageBox]::Show($dlg,((L 'config.ffmpeg_dir_missing') -f $ffmpegDir),(L 'config.path_title'),[System.Windows.Forms.MessageBoxButtons]::OK,[System.Windows.Forms.MessageBoxIcon]::Warning)
            return
        }
        foreach ($exeName in @('ffmpeg.exe','ffprobe.exe')) {
            $exePath=Join-Path $ffmpegDir $exeName
            if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
                [void][System.Windows.Forms.MessageBox]::Show($dlg,((L 'config.ffmpeg_exe_missing') -f $exeName,$ffmpegDir),(L 'config.path_title'),[System.Windows.Forms.MessageBoxButtons]::OK,[System.Windows.Forms.MessageBoxIcon]::Warning)
                return
            }
        }
        if (-not (Test-Path -LiteralPath $txtCfgGrav.Text.Trim() -PathType Leaf)) {
            [void][System.Windows.Forms.MessageBox]::Show($dlg,((L 'config.grav_missing') -f $txtCfgGrav.Text.Trim()),(L 'config.path_title'),[System.Windows.Forms.MessageBoxButtons]::OK,[System.Windows.Forms.MessageBoxIcon]::Warning)
            return
        }
        foreach ($item in @(
            @((L 'config.item_grain_root'),$txtCfgGrain.Text.Trim()),
            @((L 'config.item_lut_root'),$txtCfgLut.Text.Trim())
        )) {
            if (-not (Test-Path -LiteralPath $item[1] -PathType Container)) {
                [void][System.Windows.Forms.MessageBox]::Show($dlg,((L 'config.item_missing') -f $item[0],$item[1]),(L 'config.path_title'),[System.Windows.Forms.MessageBoxButtons]::OK,[System.Windows.Forms.MessageBoxIcon]::Warning)
                return
            }
        }

        try {
            Save-FilmGrainConfig -Values @{
                FFMPEG_DIR=$ffmpegDir; GRAV1SYNTH=$txtCfgGrav.Text.Trim();
                GRAIN_ROOT=$txtCfgGrain.Text.Trim(); LUT_ROOT=$txtCfgLut.Text.Trim()
            }
        } catch {
            [void][System.Windows.Forms.MessageBox]::Show($dlg,((L 'config.save_failed') -f $_.Exception.Message),(L 'config.path_title'),[System.Windows.Forms.MessageBoxButtons]::OK,[System.Windows.Forms.MessageBoxIcon]::Error)
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

    $gravPathChanged = -not [string]::Equals($oldGravPath, [string]$script:PathConfig.GRAV1SYNTH, [System.StringComparison]::OrdinalIgnoreCase)
    $grainRootChanged = -not [string]::Equals($oldGrainRoot, [string]$script:PathConfig.GRAIN_ROOT, [System.StringComparison]::OrdinalIgnoreCase)
    $lutRootChanged = -not [string]::Equals($oldLutRoot, [string]$script:PathConfig.LUT_ROOT, [System.StringComparison]::OrdinalIgnoreCase)

    $txtGrainRoot.Text = $script:DefaultGrainRoot
    $script:SelectedLutPath = $null
    $script:SelectedLutSource = 'None'
    $chkLut.Checked = $false
    if ($oldFfmpegDir -ne [string]$script:PathConfig.FFMPEG_DIR -or $gravPathChanged) {
        $script:HardwareCaps = $null
        $script:HardwareCapsReady = $false
        $script:Av1Available = $true
        $script:Av1UhqAvailable = $false
        $script:SfeMaxEngines = 1
        $script:Grav1synthVersion = L 'hardware.not_detected'
        $script:Grav1synthSfeCompatible = $false
        $script:HevcAvailable = $true
        if ($ffmpegState.Valid -and $ffmpegState.LastDir -eq [string]$script:PathConfig.FFMPEG_DIR) {
            $script:FFmpegVersionOverride = [string]$ffmpegState.FfmpegVersion
        } else {
            $script:FFmpegVersionOverride = L 'hardware.not_detected'
        }
    }
    if ($gravPathChanged -and $oldFfmpegDir -eq [string]$script:PathConfig.FFMPEG_DIR) {
        Initialize-HardwareCaps
    }
    Update-HardwareProfileUi
    Update-SfeUi
    Update-NoReencodeAvailability
    if ($cmbCodec.Items.Count -ge 3) {
        $cmbCodec.Items[0] = if ($script:HardwareCapsReady -and -not $script:Av1Available) { L 'codec.av1_unavailable' } else { L 'codec.av1_default' }
        $cmbCodec.Items[2] = if ($script:HardwareCapsReady -and -not $script:X264Available) { L 'codec.x264_unavailable' } else { L 'codec.x264_default' }
    }
    Update-CodecUi
    if ($grainRootChanged) {
        $script:HevcGrainFiles = @()
        $script:LastScannedGrainRoot = ''
        $cmbHevcPlate.BeginUpdate()
        try {
            $cmbHevcPlate.Items.Clear()
            [void]$cmbHevcPlate.Items.Add((L 'grain.root_updated'))
            $cmbHevcPlate.SelectedIndex = 0
            $cmbHevcPlate.Enabled = $false
            $cacheNote.Text = L 'grain.config_saved'
        } finally {
            $cmbHevcPlate.EndUpdate()
        }
    }
    if ($lutRootChanged) {
        Refresh-RecentLuts
        Refresh-FavoriteLuts
    }
    Set-LutUi
    Show-Info (L 'config.saved') (L 'config.path_title')
}
# Events
$btnConfig.Add_Click({ Show-PathConfigurationDialog })
$btnAdvanced.Add_Click({ Show-AdvancedSettingsDialog })

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
$listFiles.Add_SelectedIndexChanged({ Update-SelectedMediaInfo; Update-DeinterlaceUi; Update-NoReencodeAvailability; Update-BitrateDisplays; Update-HdrCompatibilityUi })

$cmbCodec.Add_SelectedIndexChanged({ Update-CodecUi; Update-FgsimControls; Update-FramingUi; Update-BitrateDisplays; Update-HdrCompatibilityUi })
$cmbSpeed.Add_SelectedIndexChanged({ if (-not $script:UpdatingSpeedChoices) { Update-SfeUi } })
$cmbDeint.Add_SelectedIndexChanged({ Update-DeinterlaceUi; Update-BitrateDisplays })
$chkInterpolation.Add_CheckedChanged({ Update-InterpolationUi; Update-BitrateDisplays })
$chkCinematic.Add_CheckedChanged({ Update-FramingUi; Update-BitrateDisplays })
$cmbFrameMode.Add_SelectedIndexChanged({ Update-FramingUi; Update-BitrateDisplays })
$cmbBitrate.Add_TextChanged({
    if (-not $script:ChangingCodec -and -not $script:UpdatingBitrateUi -and $cmbCodec.SelectedIndex -ge 0 -and $cmbCodec.SelectedIndex -le 2) {
        if (Test-HevcFgsim) {
            if ($cmbBitrate.Text -eq $script:FgsimStandardItem) { $script:FgsimRcChoice = 'STANDARD'; return }
            if ($cmbBitrate.Text -eq $script:FgsimHighItem) { $script:FgsimRcChoice = 'HIGH'; return }
            $script:FgsimRcChoice = 'VBR'
        }
        $script:ModeBitrateAuto[$cmbCodec.SelectedIndex] = $false
        $script:ModeBitrate[$cmbCodec.SelectedIndex] = $cmbBitrate.Text.Trim()
        $script:UpdatingBitrateUi = $true
        try { $chkBitrateAuto.Checked = $false } finally { $script:UpdatingBitrateUi = $false }
    }
})
$chkBitrateAuto.Add_CheckedChanged({
    if ($script:UpdatingBitrateUi -or $cmbCodec.SelectedIndex -lt 0 -or $cmbCodec.SelectedIndex -gt 2) { return }
    $script:ModeBitrateAuto[$cmbCodec.SelectedIndex] = [bool]$chkBitrateAuto.Checked
    if ($chkBitrateAuto.Checked) { Update-AutoBitrateDisplay }
    Update-FgsimBitrateUi
})
$chkUploadHighMotion.Add_CheckedChanged({ Update-BitrateDisplays })
$cmbFps.Add_SelectedIndexChanged({ Update-BitrateDisplays })
$cmbAv1Method.Add_SelectedIndexChanged({
    if (-not $script:UpdatingProcStrengthUi) {
        if ($cmbAv1Method.SelectedIndex -eq 3) { Set-ProceduralStrength 30 'AV1' }
        elseif ($cmbAv1Method.SelectedIndex -eq 4) { Set-ProceduralStrength 55 'AV1' }
    }
    Update-Av1Controls
    if ($cmbAv1Method.SelectedIndex -eq 2) { Refresh-Av1GrainTables }
})
$cmbAv1Format.Add_SelectedIndexChanged({ Update-Av1Controls })
$btnRefreshAv1Table.Add_Click({ Refresh-Av1GrainTables })
$chkShowAllAv1Tables.Add_CheckedChanged({ Refresh-Av1GrainTables })

$cmbHevcPlate.Add_SelectedIndexChanged({
    if (-not $script:UpdatingProcStrengthUi) {
        if ($cmbHevcPlate.SelectedIndex -ge 0 -and $cmbHevcPlate.SelectedIndex -lt $script:HevcGrainFiles.Count) {
            $procSel = [string]$script:HevcGrainFiles[$cmbHevcPlate.SelectedIndex]
            if ($procSel -eq '::PROC30::') { Set-ProceduralStrength 30 'HEVC' }
            elseif ($procSel -eq '::PROC55::') { Set-ProceduralStrength 55 'HEVC' }
        }
    }
    Update-HevcGrainControls
})
$trackHevcStrength.Add_ValueChanged({ Update-HevcGrainControls })
$trackAv1ProcStrength.Add_ValueChanged({
    if (-not $script:UpdatingProcStrengthUi) { Set-ProceduralStrength $trackAv1ProcStrength.Value 'AV1' }
})
$trackHevcProcStrength.Add_ValueChanged({
    if (-not $script:UpdatingProcStrengthUi) { Set-ProceduralStrength $trackHevcProcStrength.Value 'HEVC'; Update-HevcGrainControls }
})

$trackLutStrength.Add_ValueChanged({
    $strengths = @(25, 50, 75, 100)
    $lblLutStrength.Text = [string]$strengths[$trackLutStrength.Value] + '%'
})

$chkUpload.Add_CheckedChanged({
    $enabled = ($chkUpload.Enabled -and $chkUpload.Checked)
    $cmbUploadBitrate.Enabled = $enabled
    $chkUploadBitrateAuto.Enabled = $enabled
    if ($enabled -and $script:UploadBitrateAuto) { Update-UploadAutoBitrateDisplay }
    Update-UploadHighMotionUi
})
$cmbUploadBitrate.Add_TextChanged({
    if (-not $script:UpdatingUploadBitrateUi) {
        $script:UploadBitrateAuto = $false
        $script:UploadBitrate = $cmbUploadBitrate.Text.Trim()
        $script:UpdatingUploadBitrateUi = $true
        try { $chkUploadBitrateAuto.Checked = $false } finally { $script:UpdatingUploadBitrateUi = $false }
    }
})
$chkUploadBitrateAuto.Add_CheckedChanged({
    if ($script:UpdatingUploadBitrateUi) { return }
    $script:UploadBitrateAuto = [bool]$chkUploadBitrateAuto.Checked
    if ($chkUploadBitrateAuto.Checked) { Update-UploadAutoBitrateDisplay }
})
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
                    (L 'close.running_confirm'),
                    (L 'close.running_title'),
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
Update-BitrateDisplays
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
