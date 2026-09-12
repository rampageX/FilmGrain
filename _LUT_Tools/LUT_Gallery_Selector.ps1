param(
    [Parameter(Mandatory=$true)][string]$LutRoot,
    [Parameter(Mandatory=$true)][string]$PreviewRoot,
    [string]$OutputFile,
    [string]$RecordRecentPath
)

$ErrorActionPreference = 'Stop'
$RecentLimit = 25
$recentPath = Join-Path $PreviewRoot '_LUT_GALLERY_RECENT.json'
$script:RecentWriteError = ''

function Get-LutKey([string]$path) {
    if (-not $path) { return '' }
    try { return ([IO.Path]::GetFullPath($path)).ToLowerInvariant() }
    catch { return $path.ToLowerInvariant() }
}

function Save-RecentUse([string]$lutPath) {
    $script:RecentWriteError = ''
    if (-not $lutPath -or -not (Test-Path -LiteralPath $lutPath -PathType Leaf)) {
        $script:RecentWriteError = "LUT 文件不存在：$lutPath"
        return $false
    }
    if ([IO.Path]::GetExtension($lutPath) -ine '.cube') {
        $script:RecentWriteError = "不是 CUBE LUT：$lutPath"
        return $false
    }
    if (-not (Test-Path -LiteralPath $PreviewRoot -PathType Container)) {
        $script:RecentWriteError = "预览根目录不存在：$PreviewRoot"
        return $false
    }

    $tempPath = Join-Path $PreviewRoot ('.LUT_GALLERY_RECENT_' + [guid]::NewGuid().ToString('N') + '.tmp')
    $backupPath = Join-Path $PreviewRoot ('.LUT_GALLERY_RECENT_' + [guid]::NewGuid().ToString('N') + '.bak')
    try {
        $fullLutPath = (Get-Item -LiteralPath $lutPath).FullName
        $newKey = Get-LutKey $fullLutPath
        $records = @(
            [pscustomobject]@{
                LutPath = $fullLutPath
                LastUsed = [DateTime]::UtcNow.ToString('o')
            }
        )
        $seen = @{}
        $seen[$newKey] = $true

        if (Test-Path -LiteralPath $recentPath -PathType Leaf) {
            $raw = Get-Content -LiteralPath $recentPath -Raw -Encoding UTF8
            if ($raw.Trim()) {
                $parsed = $raw | ConvertFrom-Json
                $oldRecordCount = 0
                foreach ($row in $parsed) {
                    $oldRecordCount++
                    $oldPath = if ($row -is [string]) { [string]$row } else { [string]$row.LutPath }
                    if (-not $oldPath) {
                        throw "Recent 中第 $oldRecordCount 条记录没有 LutPath；为保护原记录，本次拒绝覆盖。"
                    }
                    $oldKey = Get-LutKey $oldPath
                    if ($seen.ContainsKey($oldKey)) { continue }
                    $seen[$oldKey] = $true
                    $records += [pscustomobject]@{
                        # Never clean or validate old entries while writing.
                        # Readers may hide stale paths, but writers must preserve them.
                        LutPath = $oldPath
                        LastUsed = $(if ($row -is [string]) { '' } else { [string]$row.LastUsed })
                    }
                    if ($records.Count -ge $RecentLimit) { break }
                }
                if ($oldRecordCount -eq 0) {
                    throw 'Recent 文件非空，但没有解析出任何记录；为保护原记录，本次拒绝覆盖。'
                }
            }
        }

        $json = ConvertTo-Json -InputObject @($records) -Depth 4
        [IO.File]::WriteAllText($tempPath, $json, (New-Object System.Text.UTF8Encoding($false)))

        # Verify the complete replacement payload before touching the database.
        $verifyParsed = (Get-Content -LiteralPath $tempPath -Raw -Encoding UTF8) | ConvertFrom-Json
        $verifyCount = if ($verifyParsed -is [System.Array]) { $verifyParsed.Count } else { 1 }
        if ($verifyCount -ne $records.Count) {
            throw "Recent 临时文件校验失败：预期 $($records.Count) 条，实际 $verifyCount 条。"
        }
        if (Test-Path -LiteralPath $recentPath -PathType Leaf) {
            [IO.File]::Replace($tempPath, $recentPath, $backupPath, $true)
        } else {
            [IO.File]::Move($tempPath, $recentPath)
        }
        return $true
    } catch {
        $script:RecentWriteError = $_.Exception.ToString()
        return $false
    } finally {
        if (Test-Path -LiteralPath $tempPath) {
            Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $backupPath) {
            Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
        }
    }
}

# Studio can request one headless registration, but this Gallery script remains
# the sole owner and writer of the Recent database.
if ($RecordRecentPath) {
    if (Save-RecentUse $RecordRecentPath) { exit 0 }
    [Console]::Error.WriteLine($script:RecentWriteError)
    exit 12
}
if (-not $OutputFile) { exit 2 }

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
# Gallery is the sole writer. Studio only reads these files and can ask this
# script to perform one headless Recent registration at encoding start.
# ------------------------------------------------------------

$allByPath = @{}
foreach ($entry in $items) {
    $allByPath[(Get-LutKey ([string]$entry.LutPath))] = $entry
}

function Read-RecentRecords {
    param([object]$ReadStatus)

    if ($ReadStatus) {
        $ReadStatus.Success = $true
        $ReadStatus.ErrorMessage = ''
    }

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
            $p = if ($row -is [string]) { [string]$row } else { [string]$row.LutPath }
            if (-not $p) { continue }
            $key = Get-LutKey $p
            if ($allByPath.ContainsKey($key)) {
                $result += [pscustomobject]@{
                    LutPath  = [string]$allByPath[$key].LutPath
                    LastUsed = $(if ($row -is [string]) { '' } else { [string]$row.LastUsed })
                }
            }
        }
    } catch {
        # A damaged/stale recent file must never prevent Gallery startup.
        if ($ReadStatus) {
            $ReadStatus.Success = $false
            $ReadStatus.ErrorMessage = $_.Exception.Message
        }
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
    $msg = "没有找到可用的 LUT 预览记录。`r`n`r`nLUT 根目录：`r`n$LutRoot`r`n`r`n预览根目录：`r`n$PreviewRoot`r`n`r`n已读取索引记录：$indexRows`r`n找到的 CUBE 文件：$diskLuts`r`n具有匹配预览的 CUBE 文件：$diskMatched"
    if ($indexError) { $msg += "`r`n`r`n索引错误：`r`n$indexError" }
    [System.Windows.Forms.MessageBox]::Show($msg, 'LUT 图库诊断', 'OK', 'Warning') | Out-Null
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
$folderOptions = @('全部文件夹') + @($folderSet.Keys | Sort-Object)

$form = New-Object System.Windows.Forms.Form
$form.Text = "电影风格 LUT 图库 - $($items.Count) 个 LUT"
$form.StartPosition = 'CenterScreen'
$form.Size = New-Object System.Drawing.Size -ArgumentList 1500,1000
$form.MinimumSize = New-Object System.Drawing.Size -ArgumentList 1200,780
$form.KeyPreview = $true

$top = New-Object System.Windows.Forms.Panel
$top.Dock = 'Top'; $top.Height = 46
$form.Controls.Add($top)

$label = New-Object System.Windows.Forms.Label
$label.Text = '搜索：'; $label.AutoSize = $true
$label.Location = New-Object System.Drawing.Point -ArgumentList 12,15
$top.Controls.Add($label)

$search = New-Object System.Windows.Forms.TextBox
$search.Location = New-Object System.Drawing.Point -ArgumentList 68,10
$search.Size = New-Object System.Drawing.Size -ArgumentList 160,26
$top.Controls.Add($search)

$allView = New-Object System.Windows.Forms.Button
$allView.Text = '全部 LUT'; $allView.Size = New-Object System.Drawing.Size -ArgumentList 88,28
$allView.Location = New-Object System.Drawing.Point -ArgumentList 245,9
$top.Controls.Add($allView)

$recentView = New-Object System.Windows.Forms.Button
$recentView.Text = '最近使用 (0)'; $recentView.Size = New-Object System.Drawing.Size -ArgumentList 105,28
$recentView.Location = New-Object System.Drawing.Point -ArgumentList 340,9
$top.Controls.Add($recentView)

$favoriteView = New-Object System.Windows.Forms.Button
$favoriteView.Text = '我的最爱 (0)'; $favoriteView.Size = New-Object System.Drawing.Size -ArgumentList 112,28
$favoriteView.Location = New-Object System.Drawing.Point -ArgumentList 452,9
$top.Controls.Add($favoriteView)

$folderLabel = New-Object System.Windows.Forms.Label
$folderLabel.Text = '文件夹：'; $folderLabel.AutoSize = $true
$folderLabel.Location = New-Object System.Drawing.Point -ArgumentList 575,15
$top.Controls.Add($folderLabel)

$folderFilter = New-Object System.Windows.Forms.ComboBox
$folderFilter.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$folderFilter.Location = New-Object System.Drawing.Point -ArgumentList 634,10
$folderFilter.Size = New-Object System.Drawing.Size -ArgumentList 168,26
$folderFilter.DropDownWidth = 520
$folderFilter.MaxDropDownItems = 20
foreach ($folderName in $folderOptions) { [void]$folderFilter.Items.Add($folderName) }
$folderFilter.SelectedIndex = 0
$top.Controls.Add($folderFilter)

$status = New-Object System.Windows.Forms.Label
$status.AutoSize = $false
$status.Location = New-Object System.Drawing.Point -ArgumentList 805,13
$status.Size = New-Object System.Drawing.Size -ArgumentList 250,22
$status.AutoEllipsis = $true
$top.Controls.Add($status)

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
$selectedLabel.Text = '双击缩略图，或选中后点击【使用选中的 LUT】。'
$selectedLabel.AutoEllipsis = $true
$selectedLabel.Location = New-Object System.Drawing.Point -ArgumentList 12,16
$selectedLabel.Size = New-Object System.Drawing.Size -ArgumentList ($form.ClientSize.Width-730),22
$selectedLabel.Anchor = 'Left,Right,Top'
$bottom.Controls.Add($selectedLabel)

$smartFilter = New-Object System.Windows.Forms.Button
$smartFilter.Text = '智能过滤'; $smartFilter.Size = New-Object System.Drawing.Size -ArgumentList 130,30
$smartFilter.Anchor = 'Top,Right'; $smartFilter.Location = New-Object System.Drawing.Point -ArgumentList ($form.ClientSize.Width-705),9
$bottom.Controls.Add($smartFilter)

$changeReference = New-Object System.Windows.Forms.Button
$changeReference.Text = '更换参考图'; $changeReference.Size = New-Object System.Drawing.Size -ArgumentList 130,30
$changeReference.Anchor = 'Top,Right'; $changeReference.Location = New-Object System.Drawing.Point -ArgumentList ($form.ClientSize.Width-565),9
$bottom.Controls.Add($changeReference)

$updatePreview = New-Object System.Windows.Forms.Button
$updatePreview.Text = '更新预览图'; $updatePreview.Size = New-Object System.Drawing.Size -ArgumentList 130,30
$updatePreview.Anchor = 'Top,Right'; $updatePreview.Location = New-Object System.Drawing.Point -ArgumentList ($form.ClientSize.Width-425),9
$bottom.Controls.Add($updatePreview)

$use = New-Object System.Windows.Forms.Button
$use.Text = '使用选中的 LUT'; $use.Size = New-Object System.Drawing.Size -ArgumentList 160,30
$use.Anchor = 'Top,Right'; $use.Location = New-Object System.Drawing.Point -ArgumentList ($form.ClientSize.Width-285),9
$bottom.Controls.Add($use)

$none = New-Object System.Windows.Forms.Button
$none.Text = '禁用 LUT'; $none.Size = New-Object System.Drawing.Size -ArgumentList 95,30
$none.Anchor = 'Top,Right'; $none.Location = New-Object System.Drawing.Point -ArgumentList ($form.ClientSize.Width-115),9
$bottom.Controls.Add($none)

$smartTip = New-Object System.Windows.Forms.ToolTip
$smartTip.SetToolTip($smartFilter, '隐藏高置信度功能/技术转换型 LUT；再次点击恢复全部。只读取 CUBE 文件头，不修改 LUT 文件。')

$referenceTip = New-Object System.Windows.Forms.ToolTip
$referenceTip.SetToolTip($changeReference, '选择新参考图并覆盖生成全部 LUT 预览')

$updatePreviewTip = New-Object System.Windows.Forms.ToolTip
$updatePreviewTip.SetToolTip($updatePreview, '同步 LUT 预览：补建新增或缺失预览，并安全清理已删除 LUT 对应的旧预览')

$allItems = @($items)
$ViewMode = 'All'
$filteredItems = @($allItems)
$lastImageError = $null
$PageSize = 25
$CurrentPage = 0
$script:UpdatingPageMenu = $false
$SelectedEntry = $null
$SelectedCard = $null
$PageImages = New-Object System.Collections.Generic.List[System.Drawing.Image]
$previewGenerator = Join-Path $PSScriptRoot 'LUT_Preview_Batch_Gallery.ps1'
$currentReference = Join-Path $PSScriptRoot 'LUT_Reference_Current.jpg'
$script:PreviewBuildProcess = $null
$script:PreviewBuildMode = ''
$script:PreviewUpdateBeforeKeys = @{}
$script:PreviewBuildOriginalTitle = $form.Text

# V2 rule: only pure technical/utility LUTs are hidden.
# Combined LUTs that contain both an input transform and a creative look stay visible.
$script:SmartFilterEnabled = $false
$script:SmartScanComplete = $false
$script:SmartTechnicalCount = 0
$script:SmartCombinedCount = 0
$script:CurrentSmartHiddenCount = 0
$script:SmartLutCache = @{}
$script:SmartCreativeFamilies = @{}
$script:SmartFilterReport = Join-Path $PreviewRoot '_LUT_SMART_FILTER_REPORT.csv'

function Get-LutHeaderForSmartFilter([string]$lutPath) {
    $headerLines = @()
    try {
        foreach ($line in @(Get-Content -LiteralPath $lutPath -TotalCount 240 -ErrorAction Stop)) {
            $s = [string]$line
            if ($s -match '^\s*[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?\s+[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?\s+[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?(?:\s|$)') { break }
            $headerLines += $s
        }
    } catch {}
    return ($headerLines -join "`n")
}

function Get-SmartFamilyInfo($entry) {
    $base = [IO.Path]::GetFileNameWithoutExtension([string]$entry.LutPath)
    $relative = [string]$entry.Relative
    $dir = Split-Path $relative -Parent

    $inputToken = '(?:ACEScct|ACEScc|ACEScg|DWG|RWG|BMDFilmGen(?:4|5)|BolexLog|CanonLog(?:1|2|3)?|Cineon|DJIDLog|DragonColor2RLF|FLog2C|FLog2|FLog|KineLog3|LLog|LogC(?:3|4)?|NLog|ProTune|Rec709|BT[._-]?709|SLog3CineVenice|SLog3Cine|SLog3Venice|SLog3|SLog2|SLog1|VLog|ZLog2)'
    $outputToken = '(?:ACEScct|ACEScc|ACEScg|DWG|RWG|Rec709|BT[._-]?709)'
    $rx = '^(?<family>.+?)[._ -]+(?<input>' + $inputToken + ')[._ -]+to[._ -]+(?<output>' + $outputToken + ')(?:[._ -].*)?$'

    $m = [regex]::Match($base,$rx,[System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $m.Success) { return $null }

    $family = $m.Groups['family'].Value.Trim(' ','-','_','.')
    if (-not $family) { return $null }

    $key = (($dir + '|' + $family).ToLowerInvariant())
    return [pscustomobject]@{
        Key = $key
        Family = $family
        Input = $m.Groups['input'].Value
        Output = $m.Groups['output'].Value
    }
}

function Initialize-SmartCreativeFamilies {
    $groups = @{}

    foreach ($entry in @($allItems)) {
        $info = Get-SmartFamilyInfo $entry
        if (-not $info) { continue }

        if (-not $groups.ContainsKey($info.Key)) {
            $groups[$info.Key] = @{}
        }
        $variant = (($info.Input + '>' + $info.Output).ToLowerInvariant())
        $groups[$info.Key][$variant] = $true
    }

    $script:SmartCreativeFamilies = @{}
    foreach ($key in @($groups.Keys)) {
        $variantCount = @($groups[$key].Keys).Count
        if ($variantCount -ge 3) {
            $script:SmartCreativeFamilies[$key] = $variantCount
        }
    }
}

function Get-SmartLutClassification($entry) {
    $lutPath = [string]$entry.LutPath
    $key = Get-LutKey $lutPath
    if ($script:SmartLutCache.ContainsKey($key)) { return $script:SmartLutCache[$key] }

    $header = Get-LutHeaderForSmartFilter $lutPath
    $relative = [string]$entry.Relative
    $probe = $relative + "`n" + $header
    $lines = @($header -split "`r?`n")

    $spacePattern = '(?i)(?:\b(?:rec|bt|itu[\s._-]*r[\s._-]*bt)?[\s._-]*(?:709|2020)\b|\b(?:pq|st[\s._-]*2084|hlg|arib[\s._-]*std[\s._-]*b67)\b|\b(?:dci[\s._-]*p3|display[\s._-]*p3|p3)\b|\baces(?:cg|cc|cct)?\b|\b(?:logc(?:3|4)?|arri[\s._-]*logc?|s[\s._-]*log(?:1|2|3)?|slog(?:1|2|3)?|v[\s._-]*log|vlog|c[\s._-]*log(?:1|2|3)?|clog(?:1|2|3)?|f[\s._-]*log(?:2c|2)?|flog(?:2c|2)?|d[\s._-]*log|dlog|n[\s._-]*log|nlog|cineon|redlogfilm|log3g10|bmd[\s._-]*film|blackmagic[\s._-]*film)\b|\b(?:redwidegamut|davinci[\s._-]*wide[\s._-]*gamut|blackmagic[\s._-]*wide[\s._-]*gamut|s[\s._-]*gamut(?:3(?:[\s._-]*cine)?)?|v[\s._-]*gamut|v709|lc[\s._-]*709a)\b|\blinear\b)'
    $technicalWordPattern = '(?i)\b(?:utility|utilities|technical|conversion|convert|transform|color[\s._-]*space[\s._-]*transform|colour[\s._-]*space[\s._-]*transform|colorspace[\s._-]*transform|cst|idt|odt)\b'
    $operationPattern = '(?i)(?:tone[\s._-]*map|tonemap|gamut[\s._-]*map|range[\s._-]*conversion|full[\s._-]*(?:range[\s._-]*)?(?:to|[-=]+>)\s*legal|legal[\s._-]*(?:range[\s._-]*)?(?:to|[-=]+>)\s*full|video[\s._-]*levels?[\s._-]*(?:to|[-=]+>)\s*data[\s._-]*levels?|data[\s._-]*levels?[\s._-]*(?:to|[-=]+>)\s*video[\s._-]*levels?|\bshaper\b)'
    $creativePattern = '(?i)(?:\bcreative\b|\blook\b|\bcinematic\b|\bteal\b|\borange\b|\bvintage\b|\bretro\b|\bbleach\b|\bcross[\s._-]*process\b|\bwarm\b|\bcool\b|\bfilm[\s._-]*(?:look|tone|boost|print|emulation)\b|\bprint[\s._-]*film\b|\b(?:kodak[\s._-]*)?(?:2383|2393)\b|\beterna(?:[\s._-]*bb)?\b)'

    $inputLines = @($lines | Where-Object { $_ -match '(?i)^\s*#?\s*(?:input|input color ?space|input[_ -]?colorspace|source|source color ?space|source[_ -]?colorspace)\s*[:=]' })
    $outputLines = @($lines | Where-Object { $_ -match '(?i)^\s*#?\s*(?:output|output color ?space|output[_ -]?colorspace|target|target color ?space|destination|destination color ?space)\s*[:=]' })

    $score = 0
    $reasons = @()
    $strongEvidence = $false

    if ($inputLines.Count -gt 0 -and $outputLines.Count -gt 0) {
        $score += 10
        $strongEvidence = $true
        $reasons += 'Explicit Input/Output header fields'
        $inText = $inputLines -join ' '
        $outText = $outputLines -join ' '
        if (($inText -match $spacePattern) -and ($outText -match $spacePattern)) {
            $score += 2
            $reasons += 'Recognized source/target color spaces'
        }
    }

    $conversionDetected = $false
    foreach ($line in @($probe -split "`r?`n")) {
        if ($line -notmatch '(?i)(?:\bto\b|[-=]+>)') { continue }
        $parts = [regex]::Split($line,'(?i)(?:\bto\b|\s*[-=]+>\s*)')
        if ($parts.Count -lt 2) { continue }
        for ($i = 0; $i -lt ($parts.Count - 1); $i++) {
            if (($parts[$i] -match $spacePattern) -and ($parts[$i + 1] -match $spacePattern)) {
                $conversionDetected = $true
                break
            }
        }
        if ($conversionDetected) { break }
    }
    if ($conversionDetected) {
        $score += 9
        $strongEvidence = $true
        $reasons += 'Recognized color-space conversion pair'
    }

    if ($probe -match $technicalWordPattern) {
        $score += 6
        $reasons += 'Utility/technical/transform marker'
    }
    if ($probe -match $operationPattern) {
        $score += 7
        $strongEvidence = $true
        $reasons += 'Technical range/tone/gamut operation marker'
    }

    $familyInfo = Get-SmartFamilyInfo $entry
    $isCreativeFamily = $false
    if ($familyInfo -and $script:SmartCreativeFamilies.ContainsKey($familyInfo.Key)) {
        $isCreativeFamily = $true
        $reasons += ('Multi-input creative family: ' + $familyInfo.Family + ' / ' + $script:SmartCreativeFamilies[$familyInfo.Key] + ' variants')
    }

    $hasCreativeMarker = ($probe -match $creativePattern)
    if ($hasCreativeMarker) {
        $reasons += 'Creative/look marker'
    }

    $className = 'Keep'
    $isTechnical = $false

    if ($isCreativeFamily) {
        $className = 'Combined'
        $isTechnical = $false
    } elseif ($hasCreativeMarker -and $score -ge 6) {
        $className = 'Combined'
        $isTechnical = $false
    } elseif ($score -ge 6) {
        $className = 'Technical'
        $isTechnical = $true
    } elseif ($hasCreativeMarker) {
        $className = 'Creative'
        $isTechnical = $false
    }

    if ($reasons.Count -eq 0) { $reasons += 'No strong technical evidence' }

    $result = [pscustomobject]@{
        IsTechnical = [bool]$isTechnical
        Class = $className
        Score = [int]$score
        Reason = ($reasons -join '; ')
    }
    $script:SmartLutCache[$key] = $result
    return $result
}

function Build-SmartLutClassificationCache {
    $script:SmartLutCache = @{}
    Initialize-SmartCreativeFamilies

    $rows = @()
    $technical = 0
    $combined = 0

    foreach ($entry in @($allItems)) {
        $class = Get-SmartLutClassification $entry
        if ($class.IsTechnical) { $technical++ }
        if ($class.Class -eq 'Combined') { $combined++ }

        $rows += [pscustomobject]@{
            Type = [string]$class.Class
            Score = [int]$class.Score
            RelativePath = [string]$entry.Relative
            Reason = [string]$class.Reason
            LutPath = [string]$entry.LutPath
        }
    }

    $script:SmartTechnicalCount = $technical
    $script:SmartCombinedCount = $combined
    $script:SmartScanComplete = $true

    try {
        $rows | Sort-Object Type,RelativePath | Export-Csv -LiteralPath $script:SmartFilterReport -NoTypeInformation -Encoding UTF8
    } catch {}
}
function Save-CurrentLutReference([string]$sourcePath,[string]$destinationPath) {
    if (-not $sourcePath -or -not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "参考图不存在：$sourcePath"
    }

    $sourceFull = [System.IO.Path]::GetFullPath($sourcePath)
    $destinationFull = [System.IO.Path]::GetFullPath($destinationPath)
    if ([string]::Equals($sourceFull,$destinationFull,[System.StringComparison]::OrdinalIgnoreCase)) { return }

    $configScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'Utils\FilmGrain_Config.ps1'
    if (-not (Test-Path -LiteralPath $configScript -PathType Leaf)) { throw "配置 helper 不存在：$configScript" }
    . $configScript
    $pathConfig = Get-FilmGrainConfig
    $ffmpeg = [string]$pathConfig.FFMPEG
    if (-not (Test-Path -LiteralPath $ffmpeg -PathType Leaf)) { throw "FFmpeg 不存在：$ffmpeg" }

    $tempPath = $destinationFull + '.tmp_' + [guid]::NewGuid().ToString('N') + '.jpg'
    $process = $null
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $ffmpeg
        $psi.Arguments = '-hide_banner -loglevel error -y -i "' + $sourceFull + '" -frames:v 1 -q:v 2 "' + $tempPath + '"'
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true

        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $psi
        if (-not $process.Start()) { throw '无法启动 FFmpeg 保存当前参考图。' }
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $tempPath -PathType Leaf)) {
            $detail = $stderr.Trim()
            if (-not $detail) { $detail = $stdout.Trim() }
            if ($detail) { throw "无法保存当前参考图：$detail" }
            throw "无法保存当前参考图，FFmpeg 退出代码：$($process.ExitCode)"
        }
        [System.IO.File]::Copy($tempPath,$destinationFull,$true)
    } finally {
        if ($process) { try { $process.Dispose() } catch {} }
        if (Test-Path -LiteralPath $tempPath -PathType Leaf) { try { Remove-Item -LiteralPath $tempPath -Force } catch {} }
    }
}

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
$prev.Text = '< 上一页'; $prev.Size = New-Object System.Drawing.Size -ArgumentList 74,28
$prev.Anchor = 'Top,Right'
$prev.Location = New-Object System.Drawing.Point -ArgumentList ($form.ClientSize.Width-315),9
$top.Controls.Add($prev)

$pagePrefix = New-Object System.Windows.Forms.Label
$pagePrefix.Text = '页码'
$pagePrefix.AutoSize = $false; $pagePrefix.Size = New-Object System.Drawing.Size -ArgumentList 34,22
$pagePrefix.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$pagePrefix.Anchor = 'Top,Right'
$pagePrefix.Location = New-Object System.Drawing.Point -ArgumentList ($form.ClientSize.Width-233),12
$top.Controls.Add($pagePrefix)

$pageInput = New-Object System.Windows.Forms.ComboBox
$pageInput.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$pageInput.Size = New-Object System.Drawing.Size -ArgumentList 54,26
$pageInput.Anchor = 'Top,Right'
$pageInput.Location = New-Object System.Drawing.Point -ArgumentList ($form.ClientSize.Width-196),10
$pageInput.MaxDropDownItems = 20
$top.Controls.Add($pageInput)

$pageTotal = New-Object System.Windows.Forms.Label
$pageTotal.AutoSize = $false; $pageTotal.Size = New-Object System.Drawing.Size -ArgumentList 38,22
$pageTotal.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$pageTotal.Anchor = 'Top,Right'
$pageTotal.Location = New-Object System.Drawing.Point -ArgumentList ($form.ClientSize.Width-137),12
$top.Controls.Add($pageTotal)

$next = New-Object System.Windows.Forms.Button
$next.Text = '下一页 >'; $next.Size = New-Object System.Drawing.Size -ArgumentList 74,28
$next.Anchor = 'Top,Right'
$next.Location = New-Object System.Drawing.Point -ArgumentList ($form.ClientSize.Width-94),9
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
    $recentSaved = Save-RecentUse ([string]$entry.LutPath)
    if (-not $recentSaved) {
        $detail = [string]$script:RecentWriteError
        if (-not $detail) { $detail = '没有返回具体错误。' }
        [System.Windows.Forms.MessageBox]::Show("LUT 已选择，但无法更新【最近使用】：`r`n`r`n$detail", 'LUT 图库', 'OK', 'Warning') | Out-Null
    }
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
    $tip.SetToolTip($fav,'添加/移除我的最爱')

    # Right-click menu for the thumbnail only.
    # Keep the entry on each menu item so no shared/closure state is required.
    $ctx = New-Object System.Windows.Forms.ContextMenuStrip

    $openFolder = New-Object System.Windows.Forms.ToolStripMenuItem
    $openFolder.Text = '打开 LUT 所在文件夹'
    $openFolder.Tag = $entry
    [void]$ctx.Items.Add($openFolder)
    $openFolder.Add_Click({
        $lutPath = [string]$this.Tag.LutPath
        if ($lutPath -and (Test-Path -LiteralPath $lutPath)) {
            Start-Process -FilePath 'explorer.exe' -ArgumentList ('/select,"{0}"' -f $lutPath)
        }
    })

    $openPreview = New-Object System.Windows.Forms.ToolStripMenuItem
    $openPreview.Text = '打开完整尺寸预览图'
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
        $status.Text = "匹配 $count 个 / 共 $($allItems.Count) 个"
        if ($script:SmartFilterEnabled -and $script:CurrentSmartHiddenCount -gt 0) {
            $status.Text += " / 智能过滤 $($script:CurrentSmartHiddenCount)"
        }
        if ($cardErrors -gt 0) { $status.Text += " / $cardErrors 个卡片错误" }
        if ($imageErrors -gt 0) { $status.Text += " / $imageErrors 个图片错误" }
        $script:UpdatingPageMenu = $true
        try {
            if ($pageInput.Items.Count -ne $pages) {
                $pageInput.Items.Clear()
                for ($pageNumber = 1; $pageNumber -le $pages; $pageNumber++) {
                    [void]$pageInput.Items.Add([string]$pageNumber)
                }
            }
            $pageInput.SelectedIndex = $CurrentPage
        } finally {
            $script:UpdatingPageMenu = $false
        }
        $pageTotal.Text = "/ $pages"
        $prev.Enabled = ($pages -gt 1)
        $next.Enabled = ($pages -gt 1)
    } finally {
        $gallery.ResumeLayout($true)
    }
}

function Move-Page([int]$Delta) {
    $pages = [Math]::Max(1,[int][Math]::Ceiling($filteredItems.Count / [double]$PageSize))
    if ($pages -le 1) { return }
    $script:CurrentPage = ($CurrentPage + $Delta) % $pages
    if ($script:CurrentPage -lt 0) { $script:CurrentPage += $pages }
    Refresh-Page
}

function Update-ViewButtons {
    $recentCount = @(Get-RecentItems).Count
    $favoriteCount = @(Get-FavoriteItems).Count
    $recentView.Text = "最近使用 ($recentCount)"
    $favoriteView.Text = "我的最爱 ($favoriteCount)"

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
    if ($script:SmartFilterEnabled) {
        $beforeSmartCount = $script:filteredItems.Count
        $script:filteredItems = @($script:filteredItems | Where-Object {
            -not (Get-SmartLutClassification $_).IsTechnical
        })
        $script:CurrentSmartHiddenCount = $beforeSmartCount - $script:filteredItems.Count
    } else {
        $script:CurrentSmartHiddenCount = 0
    }

    $script:CurrentPage = 0
    Update-ViewButtons
    Refresh-Page
}

function Get-CurrentGalleryItems {
    $reloadByLut = @{}
    $reloadIndexPath = Join-Path $PreviewRoot '_LUT_GALLERY_INDEX.json'

    if (Test-Path -LiteralPath $reloadIndexPath) {
        try {
            $reloadRaw = Get-Content -LiteralPath $reloadIndexPath -Raw -Encoding UTF8
            $reloadParsed = $reloadRaw | ConvertFrom-Json
            foreach ($reloadRow in @($reloadParsed)) {
                $reloadLutPath = [string]$reloadRow.LutPath
                $reloadPreviewPath = [string]$reloadRow.PreviewPath
                $reloadRelative = [string]$reloadRow.Relative
                if ((-not $reloadLutPath -or -not (Test-Path -LiteralPath $reloadLutPath)) -and $reloadRelative -and -not $reloadRelative.StartsWith('..\\')) {
                    $reloadCandidate = Join-Path $LutRoot $reloadRelative
                    if (Test-Path -LiteralPath $reloadCandidate) { $reloadLutPath = (Get-Item -LiteralPath $reloadCandidate).FullName }
                }
                if ($reloadLutPath -and (Test-Path -LiteralPath $reloadLutPath) -and ([IO.Path]::GetExtension($reloadLutPath) -ieq '.cube')) {
                    $reloadFi = Get-Item -LiteralPath $reloadLutPath
                    $reloadCandidatePreview = Get-ExpectedPreview $reloadFi
                    if (Test-Path -LiteralPath $reloadCandidatePreview) {
                        $reloadPreviewPath = $reloadCandidatePreview
                    } elseif (-not $reloadPreviewPath -or -not (Test-Path -LiteralPath $reloadPreviewPath)) {
                        $reloadPreviewPath = $null
                    }
                    if ($reloadPreviewPath -and (Test-Path -LiteralPath $reloadPreviewPath)) {
                        $reloadKey = $reloadFi.FullName.ToLowerInvariant()
                        $reloadByLut[$reloadKey] = [pscustomobject]@{
                            Name = $reloadFi.BaseName
                            Relative = $(if ($reloadRelative) { $reloadRelative } else { Get-RelativePath $LutRoot $reloadFi.FullName })
                            LutPath = $reloadFi.FullName
                            PreviewPath = (Get-Item -LiteralPath $reloadPreviewPath).FullName
                        }
                    }
                }
            }
        } catch {}
    }

    try {
        Get-ChildItem -LiteralPath $LutRoot -Filter '*.cube' -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { -not $_.FullName.StartsWith($PreviewRoot, [StringComparison]::OrdinalIgnoreCase) } |
            ForEach-Object {
                $reloadPreview = Get-ExpectedPreview $_
                if (Test-Path -LiteralPath $reloadPreview) {
                    $reloadKey = $_.FullName.ToLowerInvariant()
                    if (-not $reloadByLut.ContainsKey($reloadKey)) {
                        $reloadByLut[$reloadKey] = [pscustomobject]@{
                            Name = $_.BaseName
                            Relative = Get-RelativePath $LutRoot $_.FullName
                            LutPath = $_.FullName
                            PreviewPath = (Get-Item -LiteralPath $reloadPreview).FullName
                        }
                    }
                }
            }
    } catch {}
    return @($reloadByLut.Values | Sort-Object Relative)
}

function Reload-GalleryAfterPreviewSync {
    $newItems = @(Get-CurrentGalleryItems)
    if ($newItems.Count -eq 0) { throw '同步完成后没有读取到任何有效 LUT 预览；当前图库内容保持不变。' }

    $script:allItems = @($newItems)
    $script:allByPath = @{}
    foreach ($entry in $script:allItems) { $script:allByPath[(Get-LutKey ([string]$entry.LutPath))] = $entry }

    $selectedFolder = if ($folderFilter.SelectedIndex -gt 0) { [string]$folderFilter.SelectedItem } else { '全部文件夹' }
    $reloadFolderSet = @{}
    foreach ($entry in $script:allItems) {
        $reloadRel = [string]$entry.Relative
        $reloadDir = Split-Path $reloadRel -Parent
        if (-not $reloadDir -or $reloadDir -eq '.') { continue }
        $reloadParts = $reloadDir -split '\\'
        $reloadCurrent = ''
        foreach ($reloadPart in $reloadParts) {
            if (-not $reloadPart) { continue }
            if ($reloadCurrent) { $reloadCurrent = $reloadCurrent + '\' + $reloadPart } else { $reloadCurrent = $reloadPart }
            $reloadFolderSet[$reloadCurrent] = $true
        }
    }

    $folderFilter.Items.Clear()
    [void]$folderFilter.Items.Add('全部文件夹')
    foreach ($reloadFolderName in @($reloadFolderSet.Keys | Sort-Object)) { [void]$folderFilter.Items.Add($reloadFolderName) }
    $restoreFolderIndex = $folderFilter.Items.IndexOf($selectedFolder)
    if ($restoreFolderIndex -ge 0) { $folderFilter.SelectedIndex = $restoreFolderIndex } else { $folderFilter.SelectedIndex = 0 }

    $script:SelectedEntry = $null
    $script:SelectedCard = $null
    $script:SmartScanComplete = $false
    $script:SmartTechnicalCount = 0
    $script:SmartCombinedCount = 0
    $script:CurrentSmartHiddenCount = 0
    $script:SmartLutCache = @{}
    $script:SmartCreativeFamilies = @{}
    $script:PreviewBuildOriginalTitle = "电影风格 LUT 图库 - $($script:allItems.Count) 个 LUT"
    $form.Text = $script:PreviewBuildOriginalTitle
    if ($script:SmartFilterEnabled) { Build-SmartLutClassificationCache }
    Apply-Filter
}

function Set-PreviewBuildState([bool]$running) {
    foreach ($control in @($search,$allView,$recentView,$favoriteView,$folderFilter,$prev,$pageInput,$next,$none,$use,$smartFilter,$gallery,$changeReference,$updatePreview)) {
        $control.Enabled = -not $running
    }
    if ($running) {
        if ($script:PreviewBuildMode -eq 'Update') {
            $form.Text = $script:PreviewBuildOriginalTitle + ' - 正在更新预览…'
            $selectedLabel.Text = '正在同步 LUT 预览，请查看进度窗口。'
        } else {
            $form.Text = $script:PreviewBuildOriginalTitle + ' - 正在重新生成预览…'
            $selectedLabel.Text = '正在使用新参考图重新生成全部 LUT 预览，请查看进度窗口。'
        }
    } else {
        $form.Text = $script:PreviewBuildOriginalTitle
    }
}

$previewBuildTimer = New-Object System.Windows.Forms.Timer
$previewBuildTimer.Interval = 500
$previewBuildTimer.Add_Tick({
    if (-not $script:PreviewBuildProcess) { return }
    try {
        if (-not $script:PreviewBuildProcess.HasExited) { return }
        $previewBuildTimer.Stop()
        $exitCode = $script:PreviewBuildProcess.ExitCode
        $script:PreviewBuildProcess.Dispose()
        $script:PreviewBuildProcess = $null
        Set-PreviewBuildState $false

        if ($exitCode -eq 0) {
            if ($script:PreviewBuildMode -eq 'Update') {
                Reload-GalleryAfterPreviewSync
                $afterKeys = @{}
                foreach ($entry in @($script:allItems)) {
                    $key = Get-LutKey ([string]$entry.LutPath)
                    if ($key) { $afterKeys[$key] = $true }
                }
                $addedCount = 0
                foreach ($key in $afterKeys.Keys) { if (-not $script:PreviewUpdateBeforeKeys.ContainsKey($key)) { $addedCount++ } }
                $deletedCount = 0
                foreach ($key in $script:PreviewUpdateBeforeKeys.Keys) { if (-not $afterKeys.ContainsKey($key)) { $deletedCount++ } }
                $selectedLabel.Text = "LUT 预览更新完成：新增 $addedCount 个 / 删除 $deletedCount 个 / 当前共 $($script:allItems.Count) 个。"
                [System.Windows.Forms.MessageBox]::Show("LUT 预览更新完成。`r`n`r`n新增：$addedCount 个`r`n删除：$deletedCount 个`r`n当前图库：$($script:allItems.Count) 个 LUT", 'LUT 图库', 'OK', 'Information') | Out-Null
                $script:PreviewUpdateBeforeKeys = @{}
            } else {
                Refresh-Page
                $selectedLabel.Text = '全部 LUT 预览已使用新参考图重新生成。'
                [System.Windows.Forms.MessageBox]::Show('全部 LUT 预览已重新生成，当前图库已刷新。', 'LUT 图库', 'OK', 'Information') | Out-Null
            }
        } else {
            Refresh-Page
            $selectedLabel.Text = '预览生成失败。'
            [System.Windows.Forms.MessageBox]::Show("预览生成脚本退出，代码：$exitCode`r`n`r`n请检查进度窗口或预览目录中的失败日志。", 'LUT 图库', 'OK', 'Warning') | Out-Null
        }
        $script:PreviewBuildMode = ''
    } catch {
        $previewBuildTimer.Stop()
        if ($script:PreviewBuildProcess) {
            try { $script:PreviewBuildProcess.Dispose() } catch {}
            $script:PreviewBuildProcess = $null
        }
        Set-PreviewBuildState $false
        Refresh-Page
        $script:PreviewBuildMode = ''
        [System.Windows.Forms.MessageBox]::Show("无法获取预览生成结果：`r`n`r`n$($_.Exception.Message)", 'LUT 图库', 'OK', 'Error') | Out-Null
    }
})

$changeReference.Add_Click({
    if ($script:PreviewBuildProcess) { return }
    if (-not (Test-Path -LiteralPath $previewGenerator -PathType Leaf)) {
        [System.Windows.Forms.MessageBox]::Show("预览生成脚本不存在：`r`n`r`n$previewGenerator", 'LUT 图库', 'OK', 'Error') | Out-Null
        return
    }

    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Title = '选择新的 LUT 预览参考图'
    $dialog.Filter = '图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.webp|所有文件|*.*'
    $dialog.Multiselect = $false
    $process = $null
    try {
        if ($dialog.ShowDialog($form) -ne [System.Windows.Forms.DialogResult]::OK) { return }
        $referencePath = $dialog.FileName
    } finally {
        $dialog.Dispose()
    }

    $confirm = [System.Windows.Forms.MessageBox]::Show(
        "将使用以下参考图覆盖生成全部 LUT 预览：`r`n`r`n$referencePath`r`n`r`n此过程可能需要一段时间，是否继续？",
        'LUT 图库',
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Question
    )
    if ($confirm -ne [System.Windows.Forms.DialogResult]::Yes) { return }

    try {
        Save-CurrentLutReference $referencePath $currentReference
        $referencePath = $currentReference

        $powerShellExe = Join-Path $PSHOME 'powershell.exe'
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $powerShellExe
        $psi.Arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + $previewGenerator + '" -LutRoot "' + $LutRoot + '" -ReferencePath "' + $referencePath + '" -OutputRoot "' + $PreviewRoot + '" -ForceOverwrite -NonInteractive -NoPause'
        $psi.UseShellExecute = $true
        $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Normal

        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $psi
        if (-not $process.Start()) { throw '无法启动预览生成进程。' }
        $script:PreviewBuildMode = 'Reference'
        $script:PreviewBuildProcess = $process
        Set-PreviewBuildState $true
        $previewBuildTimer.Start()
    } catch {
        if ($process) { try { $process.Dispose() } catch {} }
        $script:PreviewBuildProcess = $null
        Set-PreviewBuildState $false
        [System.Windows.Forms.MessageBox]::Show("无法启动预览生成：`r`n`r`n$($_.Exception.Message)", 'LUT 图库', 'OK', 'Error') | Out-Null
    }
})

$updatePreview.Add_Click({
    if ($script:PreviewBuildProcess) { return }
    if (-not (Test-Path -LiteralPath $previewGenerator -PathType Leaf)) {
        [System.Windows.Forms.MessageBox]::Show("预览生成脚本不存在：`r`n`r`n$previewGenerator", 'LUT 图库', 'OK', 'Error') | Out-Null
        return
    }
    $referencePath = if (Test-Path -LiteralPath $currentReference -PathType Leaf) { $currentReference } else { Join-Path $PSScriptRoot 'LUT_Reference_Default.jpg' }
    if (-not (Test-Path -LiteralPath $referencePath -PathType Leaf)) {
        [System.Windows.Forms.MessageBox]::Show("没有找到可用的 LUT 参考图：`r`n`r`n$referencePath", 'LUT 图库', 'OK', 'Error') | Out-Null
        return
    }

    $script:PreviewUpdateBeforeKeys = @{}
    foreach ($entry in @($script:allItems)) {
        $key = Get-LutKey ([string]$entry.LutPath)
        if ($key) { $script:PreviewUpdateBeforeKeys[$key] = $true }
    }

    $process = $null
    try {
        $powerShellExe = Join-Path $PSHOME 'powershell.exe'
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $powerShellExe
        $psi.Arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + $previewGenerator + '" -LutRoot "' + $LutRoot + '" -ReferencePath "' + $referencePath + '" -OutputRoot "' + $PreviewRoot + '" -SyncDeleted -NonInteractive -NoPause'
        $psi.UseShellExecute = $true
        $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Normal
        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $psi
        if (-not $process.Start()) { throw '无法启动预览更新进程。' }
        $script:PreviewBuildMode = 'Update'
        $script:PreviewBuildProcess = $process
        Set-PreviewBuildState $true
        $previewBuildTimer.Start()
    } catch {
        if ($process) { try { $process.Dispose() } catch {} }
        $script:PreviewBuildProcess = $null
        $script:PreviewBuildMode = ''
        $script:PreviewUpdateBeforeKeys = @{}
        Set-PreviewBuildState $false
        [System.Windows.Forms.MessageBox]::Show("无法启动预览更新：`r`n`r`n$($_.Exception.Message)", 'LUT 图库', 'OK', 'Error') | Out-Null
    }
})

$smartFilter.Add_Click({
    if (-not $script:SmartScanComplete) {
        $form.UseWaitCursor = $true
        $smartFilter.Enabled = $false
        $status.Text = '正在分析 LUT...'
        [System.Windows.Forms.Application]::DoEvents()
        try {
            Build-SmartLutClassificationCache
        } catch {
            [System.Windows.Forms.MessageBox]::Show("智能过滤扫描失败：`r`n`r`n$($_.Exception.Message)", 'LUT 图库', 'OK', 'Warning') | Out-Null
            return
        } finally {
            $form.UseWaitCursor = $false
            $smartFilter.Enabled = $true
        }
    }

    $script:SmartFilterEnabled = -not $script:SmartFilterEnabled
    if ($script:SmartFilterEnabled) {
        $smartFilter.Text = '智能过滤：开'
        $smartFilter.BackColor = [System.Drawing.Color]::LightSteelBlue
        $selectedLabel.Text = "智能过滤已开启：隐藏 $($script:SmartTechnicalCount) 个纯功能型 LUT；保留 $($script:SmartCombinedCount) 个 Combined LUT。"
    } else {
        $smartFilter.Text = '智能过滤'
        $smartFilter.BackColor = [System.Drawing.SystemColors]::Control
        $selectedLabel.Text = '智能过滤已关闭，显示全部 LUT。'
    }
    Apply-Filter
})

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
$prev.Add_Click({ Move-Page -1 })
$next.Add_Click({ Move-Page 1 })
$pageInput.Add_SelectedIndexChanged({
    if ($script:UpdatingPageMenu -or $pageInput.SelectedIndex -lt 0) { return }
    if ($pageInput.SelectedIndex -eq $CurrentPage) { return }
    $script:CurrentPage = $pageInput.SelectedIndex
    Refresh-Page
})
$use.Add_Click({ Commit-Entry $script:SelectedEntry })
$none.Add_Click({ [System.IO.File]::WriteAllText($OutputFile, '', (New-Object System.Text.UTF8Encoding($false))); $form.Tag='none'; $form.Close() })
$form.Add_KeyDown({
    if ($_.KeyCode -eq 'Escape') { $form.Close() }
    elseif ($_.KeyCode -eq 'Enter' -and $script:SelectedEntry -and -not $pageInput.Focused) { Commit-Entry $script:SelectedEntry }
    elseif ($_.KeyCode -eq 'F' -and $_.Control) { [void]$search.Focus(); $search.SelectAll() }
    elseif ($_.KeyCode -eq 'PageDown') { $_.Handled = $true; $_.SuppressKeyPress = $true; Move-Page 1 }
    elseif ($_.KeyCode -eq 'PageUp') { $_.Handled = $true; $_.SuppressKeyPress = $true; Move-Page -1 }
    elseif ($_.KeyCode -eq 'F12') { if ($script:lastCardError) { [System.Windows.Forms.MessageBox]::Show($script:lastCardError,'首个卡片错误') | Out-Null } elseif ($script:lastImageError) { [System.Windows.Forms.MessageBox]::Show($script:lastImageError,'首个缩略图错误') | Out-Null } }
})
$form.Add_FormClosed({
    $searchTimer.Stop(); $searchTimer.Dispose()
    $previewBuildTimer.Stop(); $previewBuildTimer.Dispose()
    if ($script:PreviewBuildProcess) {
        try { $script:PreviewBuildProcess.Dispose() } catch {}
        $script:PreviewBuildProcess = $null
    }
    $referenceTip.Dispose()
    $updatePreviewTip.Dispose()
    $smartTip.Dispose()
    Clear-Page
})

Apply-Filter
[void]$search.Focus()
[void]$form.ShowDialog()

if ($form.Tag -eq 'selected') { exit 0 }
if ($form.Tag -eq 'none') { exit 10 }
exit 11
