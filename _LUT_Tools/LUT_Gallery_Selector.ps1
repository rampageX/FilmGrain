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

# ------------------------------------------------------------
# Persistent Recent LUTs
# Keep one gallery page (25 entries), newest first.
# Stored beside the gallery index so AV1/HEVC/test harness all share it.
# ------------------------------------------------------------
$RecentLimit = 25
$recentPath = Join-Path $PreviewRoot '_LUT_GALLERY_RECENT.json'

function Get-LutKey([string]$path) {
    if (-not $path) { return '' }
    try { return ([IO.Path]::GetFullPath($path)).ToLowerInvariant() }
    catch { return $path.ToLowerInvariant() }
}

$allByPath = @{}
foreach ($entry in $items) {
    $allByPath[(Get-LutKey ([string]$entry.LutPath))] = $entry
}

function Read-RecentRecords {
    # Use plain PowerShell arrays for Windows PowerShell 5.1 compatibility.
    # Generic List[object] wrapped by @() can throw "Argument types do not match"
    # on some PS 5.1 builds.
    $result = @()
    if (-not (Test-Path -LiteralPath $recentPath)) { return $result }
    try {
        $raw = Get-Content -LiteralPath $recentPath -Raw -Encoding UTF8
        if (-not $raw.Trim()) { return $result }
        $parsed = $raw | ConvertFrom-Json
        foreach ($row in @($parsed)) {
            $p = [string]$row.LutPath
            if (-not $p) { continue }
            $key = Get-LutKey $p
            if ($allByPath.ContainsKey($key)) {
                $result += [pscustomobject]@{
                    LutPath  = [string]$allByPath[$key].LutPath
                    LastUsed = [string]$row.LastUsed
                }
            }
        }
    } catch {
        # A damaged/stale recent file must never prevent Gallery startup.
        $result = @()
    }
    return $result
}

function Get-RecentItems {
    $seen = @{}
    $result = @()
    foreach ($row in @(Read-RecentRecords)) {
        $key = Get-LutKey ([string]$row.LutPath)
        if ($seen.ContainsKey($key)) { continue }
        if ($allByPath.ContainsKey($key)) {
            $seen[$key] = $true
            $result += $allByPath[$key]
            if ($result.Count -ge $RecentLimit) { break }
        }
    }
    return $result
}

function Save-RecentUse([string]$lutPath) {
    if (-not $lutPath) { return }
    $key = Get-LutKey $lutPath
    $records = @(
        [pscustomobject]@{
            LutPath  = $lutPath
            LastUsed = [DateTime]::UtcNow.ToString('o')
        }
    )
    foreach ($row in @(Read-RecentRecords)) {
        $oldPath = [string]$row.LutPath
        if (-not $oldPath) { continue }
        if ((Get-LutKey $oldPath) -eq $key) { continue }
        $records += [pscustomobject]@{
            LutPath  = $oldPath
            LastUsed = [string]$row.LastUsed
        }
        if ($records.Count -ge $RecentLimit) { break }
    }
    try {
        # Force array JSON even when there is only one recent LUT.
        $json = ConvertTo-Json -InputObject @($records) -Depth 4
        [System.IO.File]::WriteAllText($recentPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    } catch {
        # Recent history is optional; never fail LUT selection because of it.
    }
}

# ------------------------------------------------------------
# Persistent Favorites
# Stored separately from Recent so both views remain independent.
# Plain PowerShell arrays are used for Windows PowerShell 5.1 safety.
# ------------------------------------------------------------
$favoritesPath = Join-Path $PreviewRoot '_LUT_GALLERY_FAVORITES.json'

function Read-FavoriteRecords {
    $result = @()
    if (-not (Test-Path -LiteralPath $favoritesPath)) { return $result }
    try {
        $raw = Get-Content -LiteralPath $favoritesPath -Raw -Encoding UTF8
        if (-not $raw.Trim()) { return $result }
        $parsed = $raw | ConvertFrom-Json
        foreach ($row in @($parsed)) {
            # Accept both the current object format and a plain-string format,
            # so future/older test files do not break Gallery startup.
            $p = if ($row -is [string]) { [string]$row } else { [string]$row.LutPath }
            if (-not $p) { continue }
            $key = Get-LutKey $p
            if ($allByPath.ContainsKey($key)) {
                $result += [pscustomobject]@{ LutPath = [string]$allByPath[$key].LutPath }
            }
        }
    } catch {
        $result = @()
    }
    return $result
}

function Get-FavoriteItems {
    $seen = @{}
    $result = @()
    foreach ($row in @(Read-FavoriteRecords)) {
        $key = Get-LutKey ([string]$row.LutPath)
        if ($seen.ContainsKey($key)) { continue }
        if ($allByPath.ContainsKey($key)) {
            $seen[$key] = $true
            $result += $allByPath[$key]
        }
    }
    return $result
}

function Test-IsFavorite([string]$lutPath) {
    $key = Get-LutKey $lutPath
    foreach ($row in @(Read-FavoriteRecords)) {
        if ((Get-LutKey ([string]$row.LutPath)) -eq $key) { return $true }
    }
    return $false
}

function Save-FavoriteRecords($records) {
    try {
        $json = ConvertTo-Json -InputObject @($records) -Depth 3
        [System.IO.File]::WriteAllText($favoritesPath, $json, (New-Object System.Text.UTF8Encoding($false)))
        return $true
    } catch {
        return $false
    }
}

function Toggle-Favorite([string]$lutPath) {
    if (-not $lutPath) { return $false }
    $key = Get-LutKey $lutPath
    $records = @()
    $found = $false
    $seen = @{}

    foreach ($row in @(Read-FavoriteRecords)) {
        $p = [string]$row.LutPath
        if (-not $p) { continue }
        $rowKey = Get-LutKey $p
        if ($seen.ContainsKey($rowKey)) { continue }
        $seen[$rowKey] = $true
        if ($rowKey -eq $key) {
            $found = $true
            continue
        }
        $records += [pscustomobject]@{ LutPath = $p }
    }

    if (-not $found) {
        $records += [pscustomobject]@{ LutPath = $lutPath }
    }

    [void](Save-FavoriteRecords $records)
    return (-not $found)
}

if ($items.Count -eq 0) {
    $msg = "No usable LUT preview entries were found.`r`n`r`nLUT root:`r`n$LutRoot`r`n`r`nPreview root:`r`n$PreviewRoot`r`n`r`nIndex rows read: $indexRows`r`nCUBE files found: $diskLuts`r`nCUBE files with matching previews: $diskMatched"
    if ($indexError) { $msg += "`r`n`r`nIndex error:`r`n$indexError" }
    [System.Windows.Forms.MessageBox]::Show($msg, 'LUT Gallery - diagnostics', 'OK', 'Warning') | Out-Null
    exit 3
}

# ------------------------------------------------------------
# Folder filter choices. Build relative folder paths from the actual
# gallery items. Parent folders are included so selecting a parent
# also includes LUTs from all of its subfolders.
# ------------------------------------------------------------
$folderSet = @{}
foreach ($entry in $items) {
    $rel = [string]$entry.Relative
    $dir = Split-Path $rel -Parent
    if (-not $dir -or $dir -eq '.') { continue }

    $parts = $dir -split '\\'
    $current = ''
    foreach ($part in $parts) {
        if (-not $part) { continue }
        if ($current) { $current = $current + '\' + $part }
        else { $current = $part }
        $folderSet[$current] = $true
    }
}
$folderOptions = @('All folders') + @($folderSet.Keys | Sort-Object)

$form = New-Object System.Windows.Forms.Form
$form.Text = "Film Look LUT Gallery - $($items.Count) LUTs"
$form.StartPosition = 'CenterScreen'
$form.Size = New-Object System.Drawing.Size -ArgumentList 1500,1000
$form.MinimumSize = New-Object System.Drawing.Size -ArgumentList 1200,780
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
$search.Size = New-Object System.Drawing.Size -ArgumentList 160,26
$top.Controls.Add($search)

$allView = New-Object System.Windows.Forms.Button
$allView.Text = 'All LUTs'; $allView.Size = New-Object System.Drawing.Size -ArgumentList 88,28
$allView.Location = New-Object System.Drawing.Point -ArgumentList 245,9
$top.Controls.Add($allView)

$recentView = New-Object System.Windows.Forms.Button
$recentView.Text = 'Recent (0)'; $recentView.Size = New-Object System.Drawing.Size -ArgumentList 105,28
$recentView.Location = New-Object System.Drawing.Point -ArgumentList 340,9
$top.Controls.Add($recentView)

$favoriteView = New-Object System.Windows.Forms.Button
$favoriteView.Text = 'Favorites (0)'; $favoriteView.Size = New-Object System.Drawing.Size -ArgumentList 112,28
$favoriteView.Location = New-Object System.Drawing.Point -ArgumentList 452,9
$top.Controls.Add($favoriteView)

$folderLabel = New-Object System.Windows.Forms.Label
$folderLabel.Text = 'Folder:'; $folderLabel.AutoSize = $true
$folderLabel.Location = New-Object System.Drawing.Point -ArgumentList 575,15
$top.Controls.Add($folderLabel)

$folderFilter = New-Object System.Windows.Forms.ComboBox
$folderFilter.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$folderFilter.Location = New-Object System.Drawing.Point -ArgumentList 622,10
$folderFilter.Size = New-Object System.Drawing.Size -ArgumentList 170,26
$folderFilter.DropDownWidth = 520
$folderFilter.MaxDropDownItems = 20
foreach ($folderName in $folderOptions) { [void]$folderFilter.Items.Add($folderName) }
$folderFilter.SelectedIndex = 0
$top.Controls.Add($folderFilter)

$status = New-Object System.Windows.Forms.Label
$status.AutoSize = $false
$status.Location = New-Object System.Drawing.Point -ArgumentList 805,13
$status.Size = New-Object System.Drawing.Size -ArgumentList 230,22
$status.AutoEllipsis = $true
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
$gallery.Padding = New-Object System.Windows.Forms.Padding -ArgumentList 10,52,10,52
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
$ViewMode = 'All'
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
$prev.Location = New-Object System.Drawing.Point -ArgumentList 1050,9
$top.Controls.Add($prev)

$pageLabel = New-Object System.Windows.Forms.Label
$pageLabel.AutoSize = $true; $pageLabel.Location = New-Object System.Drawing.Point -ArgumentList 1133,15
$top.Controls.Add($pageLabel)

$next = New-Object System.Windows.Forms.Button
$next.Text = 'Next >'; $next.Size = New-Object System.Drawing.Size -ArgumentList 74,28
$next.Location = New-Object System.Drawing.Point -ArgumentList 1220,9
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
    Save-RecentUse ([string]$entry.LutPath)
    [System.IO.File]::WriteAllText($OutputFile, [string]$entry.LutPath, (New-Object System.Text.UTF8Encoding($false)))
    $form.Tag = 'selected'; $form.Close()
}

function New-LutCard($entry) {
    $card = New-Object System.Windows.Forms.Panel
    $card.Size = New-Object System.Drawing.Size -ArgumentList 270,164
    $card.Margin = New-Object System.Windows.Forms.Padding -ArgumentList 6,2,6,2
    $card.BackColor = [System.Drawing.SystemColors]::Control
    $card.Tag = $entry

    $pic = New-Object System.Windows.Forms.PictureBox
    $pic.Location = New-Object System.Drawing.Point -ArgumentList 15,2
    $pic.Size = New-Object System.Drawing.Size -ArgumentList 240,135
    $pic.SizeMode = [System.Windows.Forms.PictureBoxSizeMode]::Normal
    $pic.Cursor = [System.Windows.Forms.Cursors]::Hand
    $pic.Tag = $entry
    $card.Controls.Add($pic)

    # Favorite toggle.  Use numeric Unicode code points so the PS1 remains
    # safe even when launched by Windows PowerShell 5.1 with legacy encoding.
    $fav = New-Object System.Windows.Forms.Button
    $fav.Size = New-Object System.Drawing.Size -ArgumentList 29,27
    $fav.Location = New-Object System.Drawing.Point -ArgumentList 222,5
    $fav.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $fav.FlatAppearance.BorderSize = 0
    $fav.BackColor = [System.Drawing.Color]::FromArgb(205,205,205)
    $fav.Text = if (Test-IsFavorite ([string]$entry.LutPath)) { [string][char]9733 } else { [string][char]9734 }
    $fav.Tag = $entry
    $fav.Cursor = [System.Windows.Forms.Cursors]::Hand
    $card.Controls.Add($fav)
    $fav.BringToFront()

    $name = New-Object System.Windows.Forms.Label
    $name.Location = New-Object System.Drawing.Point -ArgumentList 5,139
    $name.Size = New-Object System.Drawing.Size -ArgumentList 260,22
    $name.TextAlign = [System.Drawing.ContentAlignment]::TopCenter
    $name.AutoEllipsis = $true
    $name.Text = [string]$entry.Name
    $name.Tag = $entry
    $name.Cursor = [System.Windows.Forms.Cursors]::Hand
    $card.Controls.Add($name)

    $tip = New-Object System.Windows.Forms.ToolTip
    $tip.SetToolTip($pic,[string]$entry.Relative)
    $tip.SetToolTip($name,[string]$entry.Relative)
    $tip.SetToolTip($fav,'Add/remove favorite')

    # Right-click menu for the thumbnail only.
    # Keep the entry on each menu item so no shared/closure state is required.
    $ctx = New-Object System.Windows.Forms.ContextMenuStrip

    $openFolder = New-Object System.Windows.Forms.ToolStripMenuItem
    $openFolder.Text = 'Open source folder'
    $openFolder.Tag = $entry
    [void]$ctx.Items.Add($openFolder)
    $openFolder.Add_Click({
        $lutPath = [string]$this.Tag.LutPath
        if ($lutPath -and (Test-Path -LiteralPath $lutPath)) {
            Start-Process -FilePath 'explorer.exe' -ArgumentList ('/select,"{0}"' -f $lutPath)
        }
    })

    $openPreview = New-Object System.Windows.Forms.ToolStripMenuItem
    $openPreview.Text = 'Open preview image (full size)'
    $openPreview.Tag = $entry
    [void]$ctx.Items.Add($openPreview)
    $openPreview.Add_Click({
        $previewPath = [string]$this.Tag.PreviewPath
        if ($previewPath -and (Test-Path -LiteralPath $previewPath)) {
            Start-Process -FilePath $previewPath
        }
    })

    $pic.ContextMenuStrip = $ctx

    $fav.Add_Click({
        $isNowFavorite = Toggle-Favorite ([string]$this.Tag.LutPath)
        $this.Text = if ($isNowFavorite) { [string][char]9733 } else { [string][char]9734 }
        Update-ViewButtons
        if ($script:ViewMode -eq 'Favorites') { Apply-Filter }
    })

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
        $shown = 0; $imageErrors = 0; $cardErrors = 0; $script:lastImageError = $null; $script:lastCardError = $null

        if ($count -gt 0) {
            for ($i=$startIndex; $i -le $endIndex; $i++) {
                $entry = $filteredItems[$i]
                try {
                    $pair = New-LutCard $entry
                    $card = $pair[0]; $pic = $pair[1]
                } catch {
                    $cardErrors++
                    if (-not $script:lastCardError) { $script:lastCardError = "$($entry.LutPath) -> $($_.Exception.Message)" }
                    continue
                }
                try {
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
        if ($cardErrors -gt 0) { $status.Text += " / $cardErrors card errors" }
        if ($imageErrors -gt 0) { $status.Text += " / $imageErrors image errors" }
        $pageLabel.Text = "Page $($CurrentPage+1) / $pages"
        $prev.Enabled = ($CurrentPage -gt 0)
        $next.Enabled = ($CurrentPage -lt ($pages-1))
    } finally {
        $gallery.ResumeLayout($true)
    }
}

function Update-ViewButtons {
    $recentCount = @(Get-RecentItems).Count
    $favoriteCount = @(Get-FavoriteItems).Count
    $recentView.Text = "Recent ($recentCount)"
    $favoriteView.Text = "Favorites ($favoriteCount)"

    $allView.BackColor = [System.Drawing.SystemColors]::Control
    $recentView.BackColor = [System.Drawing.SystemColors]::Control
    $favoriteView.BackColor = [System.Drawing.SystemColors]::Control

    if ($ViewMode -eq 'Recent') {
        $recentView.BackColor = [System.Drawing.Color]::LightSteelBlue
    } elseif ($ViewMode -eq 'Favorites') {
        $favoriteView.BackColor = [System.Drawing.Color]::LightSteelBlue
    } else {
        $allView.BackColor = [System.Drawing.Color]::LightSteelBlue
    }
}

function Apply-Filter {
    if ($ViewMode -eq 'Recent') {
        $baseItems = @(Get-RecentItems)
    } elseif ($ViewMode -eq 'Favorites') {
        $baseItems = @(Get-FavoriteItems)
    } else {
        $baseItems = @($allItems)
    }

    # Folder filter is applied on top of All / Recent / Favorites.
    # Selecting a parent folder includes every subfolder below it.
    if ($folderFilter.SelectedIndex -gt 0) {
        $selectedFolder = [string]$folderFilter.SelectedItem
        $folderPrefix = $selectedFolder.TrimEnd('\') + '\'
        $baseItems = @($baseItems | Where-Object {
            $dir = Split-Path ([string]$_.Relative) -Parent
            ($dir -ieq $selectedFolder) -or $dir.StartsWith($folderPrefix,[StringComparison]::OrdinalIgnoreCase)
        })
    }

    $q = $search.Text.Trim()
    if (-not $q) {
        $script:filteredItems = @($baseItems)
    } else {
        $script:filteredItems = @($baseItems | Where-Object {
            (("{0} {1}" -f $_.Name,$_.Relative).IndexOf($q,[StringComparison]::OrdinalIgnoreCase) -ge 0)
        })
    }
    $script:CurrentPage = 0
    Update-ViewButtons
    Refresh-Page
}
$searchTimer = New-Object System.Windows.Forms.Timer
$searchTimer.Interval = 300
$searchTimer.Add_Tick({ $searchTimer.Stop(); Apply-Filter })

$allView.Add_Click({
    $script:ViewMode = 'All'
    Apply-Filter
})
$recentView.Add_Click({
    $script:ViewMode = 'Recent'
    Apply-Filter
})
$favoriteView.Add_Click({
    $script:ViewMode = 'Favorites'
    Apply-Filter
})
$search.Add_TextChanged({ $searchTimer.Stop(); $searchTimer.Start() })
$folderFilter.Add_SelectedIndexChanged({ Apply-Filter })
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
    elseif ($_.KeyCode -eq 'F12') { if ($script:lastCardError) { [System.Windows.Forms.MessageBox]::Show($script:lastCardError,'First card error') | Out-Null } elseif ($script:lastImageError) { [System.Windows.Forms.MessageBox]::Show($script:lastImageError,'First thumbnail error') | Out-Null } }
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
