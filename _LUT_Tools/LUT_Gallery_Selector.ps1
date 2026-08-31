param(
    [Parameter(Mandatory=$true)][string]$LutRoot,
    [Parameter(Mandatory=$true)][string]$PreviewRoot,
    [Parameter(Mandatory=$true)][string]$OutputFile
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

function Get-RelativePath([string]$Base, [string]$Child) {
    $u = New-Object System.Uri(($Base.TrimEnd('\\') + '\\'))
    $v = New-Object System.Uri($Child)
    return [System.Uri]::UnescapeDataString($u.MakeRelativeUri($v).ToString()).Replace('/', '\\')
}

function Get-ExpectedPreview([System.IO.FileInfo]$Lut) {
    $rel = Get-RelativePath $LutRoot $Lut.FullName
    if ($rel.StartsWith('..\\')) {
        $rd = '_JUNCTIONS\\' + $Lut.Directory.Name
    } else {
        $rd = Split-Path $rel -Parent
    }
    $dst = if (-not $rd -or $rd -eq '.') { $PreviewRoot } else { Join-Path $PreviewRoot $rd }
    return Join-Path $dst ($Lut.BaseName + '_preview.jpg')
}

# ------------------------------------------------------------
# Build gallery entries from BOTH the JSON index and the actual
# LUT/preview files on disk.  The disk scan is deliberate: an old,
# sparse or stale index must never make the gallery appear empty.
# ------------------------------------------------------------
$byLut = @{}
$indexPath = Join-Path $PreviewRoot '_LUT_GALLERY_INDEX.json'
$indexRows = 0
$indexError = $null

if (Test-Path -LiteralPath $indexPath) {
    try {
        $raw = Get-Content -LiteralPath $indexPath -Raw -Encoding UTF8
        $parsed = $raw | ConvertFrom-Json
        $rows = @($parsed)
        foreach ($row in $rows) {
            $indexRows++
            $lutPath = [string]$row.LutPath
            $previewPath = [string]$row.PreviewPath
            $relative = [string]$row.Relative

            # Rebase old absolute paths when possible.
            if ((-not $lutPath -or -not (Test-Path -LiteralPath $lutPath)) -and $relative -and -not $relative.StartsWith('..\\')) {
                $candidate = Join-Path $LutRoot $relative
                if (Test-Path -LiteralPath $candidate) { $lutPath = (Get-Item -LiteralPath $candidate).FullName }
            }

            if ($lutPath -and (Test-Path -LiteralPath $lutPath) -and ([IO.Path]::GetExtension($lutPath) -ieq '.cube')) {
                $fi = Get-Item -LiteralPath $lutPath

                # IMPORTANT: never trust PreviewPath from an older index as the
                # authoritative mapping.  Recompute it from the actual LUT path.
                # This prevents many different LUTs from accidentally displaying
                # the same preview when a stale/bad index contains duplicate paths.
                $candidatePreview = Get-ExpectedPreview $fi
                if (Test-Path -LiteralPath $candidatePreview) {
                    $previewPath = $candidatePreview
                } elseif (-not $previewPath -or -not (Test-Path -LiteralPath $previewPath)) {
                    $previewPath = $null
                }

                if ($previewPath -and (Test-Path -LiteralPath $previewPath)) {
                    $key = $fi.FullName.ToLowerInvariant()
                    $byLut[$key] = [pscustomobject]@{
                        Name = $fi.BaseName
                        Relative = $(if ($relative) { $relative } else { Get-RelativePath $LutRoot $fi.FullName })
                        LutPath = $fi.FullName
                        PreviewPath = (Get-Item -LiteralPath $previewPath).FullName
                    }
                }
            }
        }
    } catch {
        $indexError = $_.Exception.Message
    }
}

# Filesystem fallback / repair pass.  This also discovers entries omitted
# by an older v2.2 index.  Exclude the preview tree itself.
$diskLuts = 0
$diskMatched = 0
try {
    Get-ChildItem -LiteralPath $LutRoot -Filter '*.cube' -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { -not $_.FullName.StartsWith($PreviewRoot, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object {
            $diskLuts++
            $preview = Get-ExpectedPreview $_
            if (Test-Path -LiteralPath $preview) {
                $diskMatched++
                $key = $_.FullName.ToLowerInvariant()
                if (-not $byLut.ContainsKey($key)) {
                    $byLut[$key] = [pscustomobject]@{
                        Name = $_.BaseName
                        Relative = Get-RelativePath $LutRoot $_.FullName
                        LutPath = $_.FullName
                        PreviewPath = (Get-Item -LiteralPath $preview).FullName
                    }
                }
            }
        }
} catch {}

$items = @($byLut.Values | Sort-Object Relative)
if ($items.Count -eq 0) {
    $msg = "No usable LUT preview entries were found.`r`n`r`nLUT root:`r`n$LutRoot`r`n`r`nPreview root:`r`n$PreviewRoot`r`n`r`nIndex rows read: $indexRows`r`nCUBE files found: $diskLuts`r`nCUBE files with matching previews: $diskMatched"
    if ($indexError) { $msg += "`r`n`r`nIndex error:`r`n$indexError" }
    [System.Windows.Forms.MessageBox]::Show($msg, 'LUT Gallery - diagnostics', 'OK', 'Warning') | Out-Null
    exit 3
}

$form = New-Object System.Windows.Forms.Form
$form.Text = "Film Look LUT Gallery - $($items.Count) LUTs"
$form.StartPosition = 'CenterScreen'
$form.Size = New-Object System.Drawing.Size -ArgumentList 1500,1000
$form.MinimumSize = New-Object System.Drawing.Size -ArgumentList 1200,800
$form.KeyPreview = $true

$top = New-Object System.Windows.Forms.Panel
$top.Dock = 'Top'; $top.Height = 46
$form.Controls.Add($top)

$label = New-Object System.Windows.Forms.Label
$label.Text = 'Search:'; $label.AutoSize = $true
$label.Location = New-Object System.Drawing.Point -ArgumentList 12,15
$top.Controls.Add($label)

$search = New-Object System.Windows.Forms.TextBox
$search.Location = New-Object System.Drawing.Point -ArgumentList 68,10
$search.Size = New-Object System.Drawing.Size -ArgumentList 420,26
$top.Controls.Add($search)

$status = New-Object System.Windows.Forms.Label
$status.AutoSize = $true
$status.Location = New-Object System.Drawing.Point -ArgumentList 505,15
$top.Controls.Add($status)

$none = New-Object System.Windows.Forms.Button
$none.Text = 'None / Disable LUT'; $none.Size = New-Object System.Drawing.Size -ArgumentList 150,28
$none.Anchor = 'Top,Right'; $none.Location = New-Object System.Drawing.Point -ArgumentList ($form.ClientSize.Width-170),9
$top.Controls.Add($none)

$gallery = New-Object System.Windows.Forms.FlowLayoutPanel
$gallery.Dock = 'Fill'
$gallery.AutoScroll = $true
$gallery.WrapContents = $true
$gallery.FlowDirection = [System.Windows.Forms.FlowDirection]::LeftToRight
$gallery.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 12,10,12,10
$form.Controls.Add($gallery)
$gallery.BringToFront(); $top.BringToFront()

$bottom = New-Object System.Windows.Forms.Panel
$bottom.Dock = 'Bottom'; $bottom.Height = 48
$form.Controls.Add($bottom); $bottom.BringToFront()

$selectedLabel = New-Object System.Windows.Forms.Label
$selectedLabel.Text = 'Double-click a thumbnail, or select one and click Use selected LUT.'
$selectedLabel.AutoEllipsis = $true
$selectedLabel.Location = New-Object System.Drawing.Point -ArgumentList 12,16
$selectedLabel.Size = New-Object System.Drawing.Size -ArgumentList 850,22
$selectedLabel.Anchor = 'Left,Right,Top'
$bottom.Controls.Add($selectedLabel)

$use = New-Object System.Windows.Forms.Button
$use.Text = 'Use selected LUT'; $use.Size = New-Object System.Drawing.Size -ArgumentList 160,30
$use.Anchor = 'Top,Right'; $use.Location = New-Object System.Drawing.Point -ArgumentList ($form.ClientSize.Width-180),9
$bottom.Controls.Add($use)

$allItems = @($items)
$filteredItems = @($allItems)
$lastImageError = $null
$PageSize = 25
$CurrentPage = 0
$SelectedEntry = $null
$SelectedCard = $null
$PageImages = New-Object System.Collections.Generic.List[System.Drawing.Image]

# Use a NEW cache namespace so no thumbnail generated by an older/test selector
# can be reused accidentally. Each file name is derived from the full preview path.
$thumbRoot = Join-Path $PreviewRoot '_GALLERY_THUMBS_v3_240x135'
New-Item -ItemType Directory -Force -Path $thumbRoot | Out-Null

function Get-ThumbCachePath([string]$previewPath) {
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($previewPath.ToLowerInvariant())
        $hash = -join ($sha256.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') })
    } finally { $sha256.Dispose() }
    return Join-Path $thumbRoot ($hash + '.jpg')
}

function New-ThumbFile([string]$srcPath,[string]$dstPath) {
    $fs = [System.IO.File]::Open($srcPath,[System.IO.FileMode]::Open,[System.IO.FileAccess]::Read,[System.IO.FileShare]::ReadWrite)
    try {
        $src = [System.Drawing.Image]::FromStream($fs,$true,$true)
        try {
            $bmp = New-Object System.Drawing.Bitmap -ArgumentList 240,135
            try {
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                try {
                    $g.Clear([System.Drawing.Color]::Black)
                    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $ratio = [Math]::Min(240.0/$src.Width,135.0/$src.Height)
                    $w=[int]($src.Width*$ratio); $h=[int]($src.Height*$ratio)
                    $x=[int]((240-$w)/2); $y=[int]((135-$h)/2)
                    $g.DrawImage($src,$x,$y,$w,$h)
                } finally { $g.Dispose() }
                $bmp.Save($dstPath,[System.Drawing.Imaging.ImageFormat]::Jpeg)
            } finally { $bmp.Dispose() }
        } finally { $src.Dispose() }
    } finally { $fs.Dispose() }
}

function Get-Thumb([string]$path) {
    $cachePath = Get-ThumbCachePath $path
    $needBuild = $true
    if (Test-Path -LiteralPath $cachePath) {
        try {
            if ((Get-Item -LiteralPath $cachePath).LastWriteTimeUtc -ge (Get-Item -LiteralPath $path).LastWriteTimeUtc) {
                $needBuild = $false
            }
        } catch {}
    }
    if ($needBuild) { New-ThumbFile $path $cachePath }

    # Return a fresh Bitmap instance for EVERY card. No ImageList, no shared image index.
    $fs = [System.IO.File]::Open($cachePath,[System.IO.FileMode]::Open,[System.IO.FileAccess]::Read,[System.IO.FileShare]::ReadWrite)
    try {
        $src = [System.Drawing.Image]::FromStream($fs,$true,$true)
        try { return New-Object System.Drawing.Bitmap $src }
        finally { $src.Dispose() }
    } finally { $fs.Dispose() }
}

$prev = New-Object System.Windows.Forms.Button
$prev.Text = '< Prev'; $prev.Size = New-Object System.Drawing.Size -ArgumentList 74,28
$prev.Location = New-Object System.Drawing.Point -ArgumentList 650,9
$top.Controls.Add($prev)

$pageLabel = New-Object System.Windows.Forms.Label
$pageLabel.AutoSize = $true; $pageLabel.Location = New-Object System.Drawing.Point -ArgumentList 733,15
$top.Controls.Add($pageLabel)

$next = New-Object System.Windows.Forms.Button
$next.Text = 'Next >'; $next.Size = New-Object System.Drawing.Size -ArgumentList 74,28
$next.Location = New-Object System.Drawing.Point -ArgumentList 805,9
$top.Controls.Add($next)

function Clear-Page {
    foreach ($img in @($PageImages)) { try { $img.Dispose() } catch {} }
    $PageImages.Clear()
    $gallery.Controls.Clear()
    $script:SelectedEntry = $null
    $script:SelectedCard = $null
}

function Select-Card($card, $entry) {
    if ($script:SelectedCard) {
        $script:SelectedCard.BackColor = [System.Drawing.SystemColors]::Control
    }
    $script:SelectedCard = $card
    $script:SelectedEntry = $entry
    $card.BackColor = [System.Drawing.Color]::LightSteelBlue
    $selectedLabel.Text = [string]$entry.Relative
}

function Commit-Entry($entry) {
    if (-not $entry) { return }
    [System.IO.File]::WriteAllText($OutputFile, [string]$entry.LutPath, (New-Object System.Text.UTF8Encoding($false)))
    $form.Tag = 'selected'; $form.Close()
}

function New-LutCard($entry) {
    $card = New-Object System.Windows.Forms.Panel
    $card.Size = New-Object System.Drawing.Size -ArgumentList 270,178
    $card.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 8,6,8,8
    $card.BackColor = [System.Drawing.SystemColors]::Control
    $card.Tag = $entry

    $pic = New-Object System.Windows.Forms.PictureBox
    $pic.Location = New-Object System.Drawing.Point -ArgumentList 15,4
    $pic.Size = New-Object System.Drawing.Size -ArgumentList 240,135
    $pic.SizeMode = [System.Windows.Forms.PictureBoxSizeMode]::Normal
    $pic.Cursor = [System.Windows.Forms.Cursors]::Hand
    $pic.Tag = $entry
    $card.Controls.Add($pic)

    $name = New-Object System.Windows.Forms.Label
    $name.Location = New-Object System.Drawing.Point -ArgumentList 5,143
    $name.Size = New-Object System.Drawing.Size -ArgumentList 260,30
    $name.TextAlign = [System.Drawing.ContentAlignment]::TopCenter
    $name.AutoEllipsis = $true
    $name.Text = [string]$entry.Name
    $name.Tag = $entry
    $name.Cursor = [System.Windows.Forms.Cursors]::Hand
    $card.Controls.Add($name)

    $tip = New-Object System.Windows.Forms.ToolTip
    $tip.SetToolTip($pic,[string]$entry.Relative)
    $tip.SetToolTip($name,[string]$entry.Relative)

    $click = { Select-Card $this.Parent $this.Tag }
    $dbl = { Commit-Entry $this.Tag }
    $pic.Add_Click($click); $name.Add_Click($click)
    $pic.Add_DoubleClick($dbl); $name.Add_DoubleClick($dbl)
    $card.Add_Click({ Select-Card $this $this.Tag })
    $card.Add_DoubleClick({ Commit-Entry $this.Tag })

    return $card, $pic
}

function Refresh-Page {
    $gallery.SuspendLayout()
    try {
        Clear-Page
        $count = $filteredItems.Count
        $pages = [Math]::Max(1,[int][Math]::Ceiling($count / [double]$PageSize))
        if ($CurrentPage -ge $pages) { $script:CurrentPage = $pages - 1 }
        if ($CurrentPage -lt 0) { $script:CurrentPage = 0 }
        $startIndex = $CurrentPage * $PageSize
        $endIndex = [Math]::Min($count-1,$startIndex+$PageSize-1)
        $shown = 0; $imageErrors = 0; $script:lastImageError = $null

        if ($count -gt 0) {
            for ($i=$startIndex; $i -le $endIndex; $i++) {
                $entry = $filteredItems[$i]
                try {
                    $pair = New-LutCard $entry
                    $card = $pair[0]; $pic = $pair[1]
                    $img = Get-Thumb $entry.PreviewPath
                    $PageImages.Add($img)
                    $pic.Image = $img
                    [void]$gallery.Controls.Add($card)
                    $shown++
                } catch {
                    $imageErrors++
                    if (-not $script:lastImageError) { $script:lastImageError = "$($entry.PreviewPath) -> $($_.Exception.Message)" }
                }
            }
        }
        $status.Text = "$count matched / $($allItems.Count) total"
        if ($imageErrors -gt 0) { $status.Text += " / $imageErrors image errors" }
        $pageLabel.Text = "Page $($CurrentPage+1) / $pages"
        $prev.Enabled = ($CurrentPage -gt 0)
        $next.Enabled = ($CurrentPage -lt ($pages-1))
    } finally {
        $gallery.ResumeLayout($true)
    }
}

function Apply-Filter {
    $q = $search.Text.Trim()
    if (-not $q) {
        $script:filteredItems = @($allItems)
    } else {
        $script:filteredItems = @($allItems | Where-Object {
            (("{0} {1}" -f $_.Name,$_.Relative).IndexOf($q,[StringComparison]::OrdinalIgnoreCase) -ge 0)
        })
    }
    $script:CurrentPage = 0
    Refresh-Page
}

$searchTimer = New-Object System.Windows.Forms.Timer
$searchTimer.Interval = 300
$searchTimer.Add_Tick({ $searchTimer.Stop(); Apply-Filter })

$search.Add_TextChanged({ $searchTimer.Stop(); $searchTimer.Start() })
$prev.Add_Click({ if ($CurrentPage -gt 0) { $script:CurrentPage--; Refresh-Page } })
$next.Add_Click({ if ($next.Enabled) { $script:CurrentPage++; Refresh-Page } })
$use.Add_Click({ Commit-Entry $script:SelectedEntry })
$none.Add_Click({ [System.IO.File]::WriteAllText($OutputFile, '', (New-Object System.Text.UTF8Encoding($false))); $form.Tag='none'; $form.Close() })
$form.Add_KeyDown({
    if ($_.KeyCode -eq 'Escape') { $form.Close() }
    elseif ($_.KeyCode -eq 'Enter' -and $script:SelectedEntry) { Commit-Entry $script:SelectedEntry }
    elseif ($_.KeyCode -eq 'F' -and $_.Control) { [void]$search.Focus(); $search.SelectAll() }
    elseif ($_.KeyCode -eq 'PageDown') { if ($next.Enabled) { $script:CurrentPage++; Refresh-Page } }
    elseif ($_.KeyCode -eq 'PageUp') { if ($prev.Enabled) { $script:CurrentPage--; Refresh-Page } }
    elseif ($_.KeyCode -eq 'F12' -and $script:lastImageError) { [System.Windows.Forms.MessageBox]::Show($script:lastImageError,'First thumbnail error') | Out-Null }
})
$form.Add_FormClosed({
    $searchTimer.Stop(); $searchTimer.Dispose()
    Clear-Page
})

Apply-Filter
[void]$search.Focus()
[void]$form.ShowDialog()

if ($form.Tag -eq 'selected') { exit 0 }
if ($form.Tag -eq 'none') { exit 10 }
exit 11
