from pathlib import Path
import hashlib
import os
import re
import subprocess
import sys
import zipfile

ROOT = Path.cwd()
VERSION = 'v4.6.3'
ZIP_NAME = 'FilmGrain_Studio_v4.6.3_Stable.zip'
SHA_NAME = 'FilmGrain_Studio_v4.6.3_Stable.sha256'


def run(*args):
    subprocess.run(list(args), check=True)


def read_ps1(path):
    return path.read_text(encoding='utf-8-sig')


def write_ps1(path, text):
    with open(path, 'w', encoding='utf-8-sig', newline='\r\n') as f:
        f.write(text)


def replace_once(text, old, new, label):
    if old not in text:
        raise RuntimeError('missing target: ' + label)
    return text.replace(old, new, 1)

# ------------------------------------------------------------------
# LUT Gallery Selector: exact user-validated R6T2B delta from v4.6.2.1.
# ------------------------------------------------------------------
sel_path = ROOT / '_LUT_Tools' / 'LUT_Gallery_Selector.ps1'
sel = read_ps1(sel_path)

sel = replace_once(sel,
"$folderFilter.Location = New-Object System.Drawing.Point -ArgumentList 622,10\n$folderFilter.Size = New-Object System.Drawing.Size -ArgumentList 170,26",
"$folderFilter.Location = New-Object System.Drawing.Point -ArgumentList 634,10\n$folderFilter.Size = New-Object System.Drawing.Size -ArgumentList 168,26",
'folder dropdown')

sel = replace_once(sel,
"$none = New-Object System.Windows.Forms.Button\n$none.Text = '禁用 LUT'; $none.Size = New-Object System.Drawing.Size -ArgumentList 95,28\n$none.Anchor = 'Top,Right'; $none.Location = New-Object System.Drawing.Point -ArgumentList ($form.ClientSize.Width-115),9\n$top.Controls.Add($none)\n\n",
'', 'remove top disable button')

start = sel.index('$selectedLabel = New-Object System.Windows.Forms.Label')
end = sel.index('$allItems = @($items)', start)
sel = sel[:start] + """$selectedLabel = New-Object System.Windows.Forms.Label
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

""" + sel[end:]

sel = replace_once(sel,
"$script:PreviewBuildProcess = $null\n$script:PreviewBuildOriginalTitle = $form.Text",
"$script:PreviewBuildProcess = $null\n$script:PreviewBuildMode = ''\n$script:PreviewUpdateBeforeKeys = @{}\n$script:PreviewBuildOriginalTitle = $form.Text",
'preview state')

# Right-align pagination controls.
repls = {
"$prev.Location = New-Object System.Drawing.Point -ArgumentList 1065,9": "$prev.Anchor = 'Top,Right'\n$prev.Location = New-Object System.Drawing.Point -ArgumentList ($form.ClientSize.Width-315),9",
"$pagePrefix.Location = New-Object System.Drawing.Point -ArgumentList 1147,12": "$pagePrefix.Anchor = 'Top,Right'\n$pagePrefix.Location = New-Object System.Drawing.Point -ArgumentList ($form.ClientSize.Width-233),12",
"$pageInput.Location = New-Object System.Drawing.Point -ArgumentList 1184,10": "$pageInput.Anchor = 'Top,Right'\n$pageInput.Location = New-Object System.Drawing.Point -ArgumentList ($form.ClientSize.Width-196),10",
"$pageTotal.Location = New-Object System.Drawing.Point -ArgumentList 1243,12": "$pageTotal.Anchor = 'Top,Right'\n$pageTotal.Location = New-Object System.Drawing.Point -ArgumentList ($form.ClientSize.Width-137),12",
"$next.Location = New-Object System.Drawing.Point -ArgumentList 1286,9": "$next.Anchor = 'Top,Right'\n$next.Location = New-Object System.Drawing.Point -ArgumentList ($form.ClientSize.Width-94),9",
}
for old, new in repls.items():
    sel = replace_once(sel, old, new, 'pagination')

sel = replace_once(sel,
'$status.Text += " / 隐藏功能型 $($script:CurrentSmartHiddenCount)"',
'$status.Text += " / 智能过滤 $($script:CurrentSmartHiddenCount)"',
'smart filter status')

insert_at = sel.index('function Set-PreviewBuildState([bool]$running) {')
reload_code = r'''function Get-CurrentGalleryItems {
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

'''
sel = sel[:insert_at] + reload_code + sel[insert_at:]

old_state = '''function Set-PreviewBuildState([bool]$running) {
    foreach ($control in @($search,$allView,$recentView,$favoriteView,$folderFilter,$prev,$pageInput,$next,$none,$use,$smartFilter,$gallery)) {
        $control.Enabled = -not $running
    }
    $changeReference.Enabled = -not $running
    if ($running) {
        $form.Text = $script:PreviewBuildOriginalTitle + ' - 正在重新生成预览…'
        $selectedLabel.Text = '正在使用新参考图重新生成全部 LUT 预览，请查看进度窗口。'
    } else {
        $form.Text = $script:PreviewBuildOriginalTitle
    }
}
'''
new_state = '''function Set-PreviewBuildState([bool]$running) {
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
'''
sel = replace_once(sel, old_state, new_state, 'build state')

old_timer = '''$previewBuildTimer.Add_Tick({
    if (-not $script:PreviewBuildProcess) { return }
    try {
        if (-not $script:PreviewBuildProcess.HasExited) { return }
        $previewBuildTimer.Stop()
        $exitCode = $script:PreviewBuildProcess.ExitCode
        $script:PreviewBuildProcess.Dispose()
        $script:PreviewBuildProcess = $null
        Set-PreviewBuildState $false
        Refresh-Page

        if ($exitCode -eq 0) {
            $selectedLabel.Text = '全部 LUT 预览已使用新参考图重新生成。'
            [System.Windows.Forms.MessageBox]::Show('全部 LUT 预览已重新生成，当前图库已刷新。', 'LUT 图库', 'OK', 'Information') | Out-Null
        } else {
            $selectedLabel.Text = '预览重新生成失败。'
            [System.Windows.Forms.MessageBox]::Show("预览生成脚本退出，代码：$exitCode`r`n`r`n请检查进度窗口或预览目录中的失败日志。", 'LUT 图库', 'OK', 'Warning') | Out-Null
        }
    } catch {
        $previewBuildTimer.Stop()
        if ($script:PreviewBuildProcess) {
            try { $script:PreviewBuildProcess.Dispose() } catch {}
            $script:PreviewBuildProcess = $null
        }
        Set-PreviewBuildState $false
        Refresh-Page
        [System.Windows.Forms.MessageBox]::Show("无法获取预览生成结果：`r`n`r`n$($_.Exception.Message)", 'LUT 图库', 'OK', 'Error') | Out-Null
    }
})
'''
new_timer = '''$previewBuildTimer.Add_Tick({
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
'''
sel = replace_once(sel, old_timer, new_timer, 'preview timer')

sel = replace_once(sel,
"        $script:PreviewBuildProcess = $process\n        Set-PreviewBuildState $true\n        $previewBuildTimer.Start()",
"        $script:PreviewBuildMode = 'Reference'\n        $script:PreviewBuildProcess = $process\n        Set-PreviewBuildState $true\n        $previewBuildTimer.Start()",
'change reference mode')

at = sel.index('$smartFilter.Add_Click({')
update_handler = r'''$updatePreview.Add_Click({
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

'''
sel = sel[:at] + update_handler + sel[at:]
sel = replace_once(sel,
"    $referenceTip.Dispose()\n    $smartTip.Dispose()",
"    $referenceTip.Dispose()\n    $updatePreviewTip.Dispose()\n    $smartTip.Dispose()",
'tooltip dispose')

# ------------------------------------------------------------------
# Preview generator: safe user-validated SyncDeleted behavior.
# ------------------------------------------------------------------
gen_path = ROOT / '_LUT_Tools' / 'LUT_Preview_Batch_Gallery.ps1'
gen = read_ps1(gen_path)
gen = replace_once(gen,
"    [switch]$ForceOverwrite,\n    [switch]$NonInteractive,",
"    [switch]$ForceOverwrite,\n    [switch]$SyncDeleted,\n    [switch]$NonInteractive,",
'generator param')

gen = replace_once(gen,
'''New-Item -ItemType Directory -Force -Path $out | Out-Null
$out = (Resolve-Path $out).Path
$overwrite = if ($NonInteractive) { [bool]$ForceOverwrite } else { (Read-Host "Overwrite existing previews? [y/N]") -match '^(y|yes)$' }
''',
'''New-Item -ItemType Directory -Force -Path $out | Out-Null
$out = (Resolve-Path $out).Path
$overwrite = if ($NonInteractive) { [bool]$ForceOverwrite } else { (Read-Host "Overwrite existing previews? [y/N]") -match '^(y|yes)$' }

# SyncDeleted safety model:
# - read only the previous gallery index;
# - delete only an exact generated *_preview.jpg whose indexed source LUT no longer exists;
# - delete only the exact hashed gallery thumbnail for that preview;
# - remove parent directories only when already empty, never recursively.
$oldGalleryRows = @()
$galleryIndex = Join-Path $out "_LUT_GALLERY_INDEX.json"
if ($SyncDeleted -and (Test-Path -LiteralPath $galleryIndex -PathType Leaf)) {
    try {
        $oldIndexRaw = Get-Content -LiteralPath $galleryIndex -Raw -Encoding UTF8
        if ($oldIndexRaw.Trim()) { $oldGalleryRows = @($oldIndexRaw | ConvertFrom-Json) }
    } catch {
        Write-Host "WARNING: Existing gallery index could not be read; stale-preview cleanup will be skipped." -ForegroundColor Yellow
        $oldGalleryRows = @()
    }
}
''', 'generator sync setup')

footer_old = '''# Write a gallery index that maps each preview image to its source LUT.
# This is consumed by LUT_Gallery_Selector.ps1 and the AV1 pipeline.
$galleryIndex = Join-Path $out "_LUT_GALLERY_INDEX.json"
$galleryRows | Sort-Object Relative | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $galleryIndex -Encoding UTF8

Write-Host "`nGallery entries: $($galleryRows.Count)"
Write-Host "Gallery index: $galleryIndex"
Write-Host "`nSuccess: $ok  Skipped: $skip  Unsupported: $unsupported  Failed: $fail"
Write-Host "Resolve CUBE files converted temporarily: $convertedCount"
Write-Host "Output: $out"
if (Test-Path -LiteralPath $log) { Write-Host "Log: $log" }
if (-not $NoPause) { Read-Host "Press Enter to exit" }
'''
footer_new = r'''# Synchronize only stale preview files explicitly present in the previous index
# and whose source LUT no longer exists.
$removedPreviewCount = 0
$removedThumbCount = 0
$removedEmptyDirCount = 0

if ($SyncDeleted -and $oldGalleryRows.Count -gt 0) {
    $outFull = [IO.Path]::GetFullPath($out).TrimEnd('\')
    $outPrefix = $outFull + '\'
    $thumbRoot = Join-Path $out '_GALLERY_THUMBS_v3_240x135'
    $thumbRootFull = [IO.Path]::GetFullPath($thumbRoot).TrimEnd('\')

    foreach ($oldRow in $oldGalleryRows) {
        $oldLutPath = [string]$oldRow.LutPath
        $oldRelative = [string]$oldRow.Relative
        $oldPreviewPath = [string]$oldRow.PreviewPath
        if (-not $oldPreviewPath) { continue }

        $sourceStillExists = $false
        if ($oldLutPath -and (Test-Path -LiteralPath $oldLutPath -PathType Leaf)) {
            $sourceStillExists = $true
        } elseif ($oldRelative -and -not $oldRelative.StartsWith('..\')) {
            $rebasedLut = Join-Path $root $oldRelative
            if (Test-Path -LiteralPath $rebasedLut -PathType Leaf) { $sourceStillExists = $true }
        }
        if ($sourceStillExists) { continue }

        try { $oldPreviewFull = [IO.Path]::GetFullPath($oldPreviewPath) } catch { continue }
        if (-not $oldPreviewFull.StartsWith($outPrefix,[StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($oldPreviewFull.StartsWith($thumbRootFull + '\',[StringComparison]::OrdinalIgnoreCase)) { continue }
        if (-not $oldPreviewFull.EndsWith('_preview.jpg',[StringComparison]::OrdinalIgnoreCase)) { continue }

        if (Test-Path -LiteralPath $oldPreviewFull -PathType Leaf) {
            try {
                Remove-Item -LiteralPath $oldPreviewFull -Force -ErrorAction Stop
                $removedPreviewCount++
                Write-Host "  REMOVE stale preview: $oldPreviewFull" -ForegroundColor DarkYellow
            } catch {
                Write-Host "  WARNING: Could not remove stale preview: $oldPreviewFull" -ForegroundColor Yellow
                continue
            }
        }

        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hashBytes = [System.Text.Encoding]::UTF8.GetBytes($oldPreviewFull.ToLowerInvariant())
            $thumbHash = -join ($sha256.ComputeHash($hashBytes) | ForEach-Object { $_.ToString('x2') })
        } finally { $sha256.Dispose() }
        $oldThumb = Join-Path $thumbRoot ($thumbHash + '.jpg')
        if (Test-Path -LiteralPath $oldThumb -PathType Leaf) {
            try { Remove-Item -LiteralPath $oldThumb -Force -ErrorAction Stop; $removedThumbCount++ }
            catch { Write-Host "  WARNING: Could not remove stale thumbnail: $oldThumb" -ForegroundColor Yellow }
        }

        $parent = Split-Path $oldPreviewFull -Parent
        while ($parent) {
            try { $parentFull = [IO.Path]::GetFullPath($parent).TrimEnd('\') } catch { break }
            if ($parentFull.Equals($outFull,[StringComparison]::OrdinalIgnoreCase)) { break }
            if ($parentFull.Equals($thumbRootFull,[StringComparison]::OrdinalIgnoreCase)) { break }
            if (-not $parentFull.StartsWith($outPrefix,[StringComparison]::OrdinalIgnoreCase)) { break }
            try {
                [IO.Directory]::Delete($parentFull,$false)
                $removedEmptyDirCount++
                Write-Host "  REMOVE empty preview folder: $parentFull" -ForegroundColor DarkYellow
            } catch { break }
            $parent = Split-Path $parentFull -Parent
        }
    }
}

# Write a gallery index that maps each preview image to its source LUT.
# This is consumed by LUT_Gallery_Selector.ps1 and the AV1 pipeline.
$galleryRows | Sort-Object Relative | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $galleryIndex -Encoding UTF8

Write-Host "`nGallery entries: $($galleryRows.Count)"
Write-Host "Gallery index: $galleryIndex"
Write-Host "`nSuccess: $ok  Skipped: $skip  Unsupported: $unsupported  Failed: $fail"
if ($SyncDeleted) { Write-Host "Sync cleanup: previews=$removedPreviewCount  thumbs=$removedThumbCount  empty folders=$removedEmptyDirCount" }
Write-Host "Resolve CUBE files converted temporarily: $convertedCount"
Write-Host "Output: $out"
if (Test-Path -LiteralPath $log) { Write-Host "Log: $log" }
if (-not $NoPause) { Read-Host "Press Enter to exit" }
'''
gen = replace_once(gen, footer_old, footer_new, 'generator footer')

# Safety/static validation.
for name, txt in [('selector', sel), ('generator', gen)]:
    for ch in '\u201c\u201d\u2018\u2019':
        if ch in txt:
            raise RuntimeError(name + ': forbidden curly quote')
    if re.search(r'Remove-Item[^\r\n]*-Recurse', txt, re.I):
        raise RuntimeError(name + ': recursive Remove-Item forbidden')
if '[IO.Directory]::Delete($parentFull,$true)' in gen:
    raise RuntimeError('recursive Directory.Delete forbidden')
if '[IO.Directory]::Delete($parentFull,$false)' not in gen:
    raise RuntimeError('safe empty-folder delete missing')
if '$top.Controls.Add($none)' in sel:
    raise RuntimeError('disable button still top')
if '$bottom.Controls.Add($updatePreview)' not in sel:
    raise RuntimeError('update preview button missing')

write_ps1(sel_path, sel)
write_ps1(gen_path, gen)

# ------------------------------------------------------------------
# Version and documentation.
# ------------------------------------------------------------------
studio_path = ROOT / 'Utils' / 'FilmGrain_Studio.ps1'
studio = read_ps1(studio_path)
studio = replace_once(studio, "$statusVersion.Text = 'v4.6.2.1'", "$statusVersion.Text = 'v4.6.3'", 'Studio version')
write_ps1(studio_path, studio)

readme_path = ROOT / 'README.md'
readme = readme_path.read_text(encoding='utf-8')
readme = replace_once(readme, '当前正式稳定版为 **v4.6.2.1**', '当前正式稳定版为 **v4.6.3**', 'README version')
readme = replace_once(readme, 'FilmGrain_Studio_v4.6.2.1_Stable.zip', ZIP_NAME, 'README package')
marker = '\n默认配置仍为 **AV1 Main10 + MP4 + AAC 256k**'
paragraph = ('\nv4.6.3 在 v4.6.2.1 稳定基线上完善 **LUT Gallery 预览同步**：图库新增“更新预览图”，可补建新增/缺失 LUT 预览，并仅对旧索引中明确记录且源 LUT 已不存在的单个 `*_preview.jpg` 与对应缩略图执行安全清理；目录仅在已经为空时使用非递归删除。更新完成后图库立即刷新匹配/总数，并显示新增、删除与当前总数。Gallery 同时优化底部按钮布局、分页右对齐、文件夹下拉框间距，以及智能过滤隐藏数量显示。现有“更换参考图”全量重建流程与 AV1 / HEVC / x264 编码核心参数均保持不变。\n')
if marker not in readme:
    raise RuntimeError('README insertion marker missing')
readme = readme.replace(marker, paragraph + marker, 1)
readme_path.write_text(readme, encoding='utf-8', newline='\n')

ch_path = ROOT / 'CHANGELOG.md'
ch = ch_path.read_text(encoding='utf-8')
entry = '''## v4.6.3 — 2026-09-12

- LUT Gallery 新增 **“更新预览图”**，直接在图库中同步 LUT 预览，不再需要手动进入 Utils 运行批处理工具。
- 更新模式只补建新增或缺失的 LUT 预览；现有“更换参考图”继续保持选择新参考图并全量重建的原有行为。
- 删除同步采用保守安全策略：仅删除旧 Gallery 索引中明确记录、且源 LUT 已确认不存在的单个 `*_preview.jpg`；对应 Gallery 缩略图按完整预览路径哈希精确删除。
- 禁止递归删除预览目录；父目录仅在确认已经为空时使用非递归 `Directory.Delete(path, false)` 删除，`_LUT_PREVIEWS` 根目录与缩略图缓存树均受保护。
- 更新完成后 Gallery 立即重新加载，顶部 `匹配 / 共` 数量无需重新打开即可刷新；左下角显示本次 `新增 X / 删除 Y / 当前共 Z`。
- LUT Gallery UI 调整：`禁用 LUT` 移至底部并放在“使用选中的 LUT”之后；“更新预览图”位于“更换参考图”之后；分页控件整体右对齐；文件夹下拉框间距修正；智能过滤状态简化为 `智能过滤 X`。
- 本次功能经新增 LUT、删除 LUT 与 UI 刷新实际测试通过；AV1 / HEVC / x264 编码参数、HDR Preserve、Grain、OpenSVPFlow、字幕、AAC 256k 与 H.264 上传版逻辑均未修改。

'''
ch = replace_once(ch, '# Film Grain Studio — CHANGELOG\n\n', '# Film Grain Studio — CHANGELOG\n\n' + entry, 'CHANGELOG header')
ch_path.write_text(ch, encoding='utf-8', newline='\n')

for doc_name in ['README_FilmGrain_Studio.txt', 'README_Toolkit.txt']:
    p = ROOT / doc_name
    t = p.read_text(encoding='utf-8-sig')
    t = replace_once(t, '当前正式稳定版：v4.6.2.1', '当前正式稳定版：v4.6.3', doc_name)
    with open(p, 'w', encoding='utf-8-sig', newline='\r\n') as f:
        f.write(t)

baseline = '''Film Grain Studio formal stable baseline

Baseline:
v4.6.3

Supersedes:
v4.6.2.1

Formal release:
FilmGrain_Studio_v4.6.3_Stable.zip

v4.6.3 changes:
- Adds LUT Gallery Update Preview directly in the Gallery UI.
- Missing/new LUT previews are generated without ForceOverwrite.
- Deleted LUT cleanup is index-driven and conservative: only exact stale *_preview.jpg files whose source LUT is confirmed missing are eligible.
- Matching Gallery thumbnails are deleted by exact path hash.
- Recursive preview-directory deletion is forbidden; only already-empty parent directories may be removed non-recursively.
- Gallery reloads immediately after update and reports added / deleted / current counts.
- Moves Disable LUT to the bottom action row, places Update Preview after Change Reference, right-aligns pagination, fixes folder dropdown spacing, and shows Smart Filter hidden count as Smart Filter X.
- User validation completed for add, delete, refresh and final UI layout through R6T2B.

Inherited baseline:
- v4.6.2.1 H.264 upload Manual bitrate fix remains unchanged.
- v4.6.2 HDR Preserve and LUT Smart Filter remain unchanged.
- v4.6.1 FGS icon and OpenSVPFlow updater remain unchanged.
- v4.6.0 AV1 SFE and interlaced/OpenSVPFlow routing remain unchanged.
- GUI / CLI continue sharing StudioBridge; AV1 / HEVC / x264 encoding parameters are unchanged in v4.6.3.

Release cleanup:
- No TEST / HOTFIX files.
- No Utils\\_HardwareCaps.json.
- No _LUT_Tools\\LUT_Reference_Current.jpg.
- No LUT Recent / Favorites / Smart Filter report state.
- No OpenSVPFlow user DLL/version backup state.
'''
(ROOT / 'STABLE_BASELINE.txt').write_text(baseline, encoding='utf-8', newline='\r\n')

# README screenshot must remain.
if '![](images/Film_Grain_Studio.jpg)' not in readme:
    raise RuntimeError('README GUI screenshot reference missing')

# Validate PS1 BOM and curly quote rule across tracked PS1 files.
for p in ROOT.rglob('*.ps1'):
    if '.git' in p.parts or '.github' in p.parts:
        continue
    b = p.read_bytes()
    if not b.startswith(b'\xef\xbb\xbf'):
        raise RuntimeError('PS1 missing UTF-8 BOM: ' + str(p))
    txt = b[3:].decode('utf-8')
    for ch in '\u201c\u201d\u2018\u2019':
        if ch in txt:
            raise RuntimeError('curly quote in PS1: ' + str(p))

# BAT/VBS must not have UTF-8 BOM.
for ext in ('*.bat', '*.vbs'):
    for p in ROOT.rglob(ext):
        if '.git' in p.parts or '.github' in p.parts:
            continue
        if p.read_bytes().startswith(b'\xef\xbb\xbf'):
            raise RuntimeError('BOM forbidden: ' + str(p))

# Commit formal source.
run('git', 'config', 'user.name', 'github-actions[bot]')
run('git', 'config', 'user.email', '41898282+github-actions[bot]@users.noreply.github.com')
run('git', 'add', '_LUT_Tools/LUT_Gallery_Selector.ps1', '_LUT_Tools/LUT_Preview_Batch_Gallery.ps1', 'Utils/FilmGrain_Studio.ps1', 'README.md', 'CHANGELOG.md', 'README_FilmGrain_Studio.txt', 'README_Toolkit.txt', 'STABLE_BASELINE.txt')
run('git', 'commit', '-m', 'release: Film Grain Studio v4.6.3')
run('git', 'push', 'origin', 'HEAD:master')

# Tag exact formal source commit.
run('git', 'tag', '-a', VERSION, '-m', 'Film Grain Studio v4.6.3')
run('git', 'push', 'origin', VERSION)

# Build clean formal ZIP from tracked files only.
tracked = subprocess.check_output(['git', 'ls-files'], text=True).splitlines()
exclude_prefixes = ('.github/',)
exclude_exact = {
    'Utils/_HardwareCaps.json',
    '_LUT_Tools/LUT_Reference_Current.jpg',
    '_LUT_PREVIEWS/_LUT_GALLERY_RECENT.json',
    '_LUT_PREVIEWS/_LUT_GALLERY_FAVORITES.json',
    '_LUT_PREVIEWS/_LUT_SMART_FILTER_REPORT.csv',
}
exclude_contains = ('/_PluginBackup/',)
files = []
for rel in tracked:
    rp = rel.replace('\\', '/')
    if rp.startswith(exclude_prefixes):
        continue
    if rp in exclude_exact:
        continue
    if any(x in '/' + rp for x in exclude_contains):
        continue
    if rp.startswith('_OpenSVPFlow/Plugins/') and (rp.lower().endswith('.dll') or rp.lower().endswith('open-svpflow-version.txt')):
        continue
    p = ROOT / rel
    if p.is_file():
        files.append((rel, p))

zip_path = ROOT / ZIP_NAME
with zipfile.ZipFile(zip_path, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for rel, p in files:
        z.write(p, rel)

sha = hashlib.sha256(zip_path.read_bytes()).hexdigest()
(ROOT / SHA_NAME).write_text(f'{sha}  *{ZIP_NAME}\n', encoding='ascii')

notes = '''# Film Grain Studio v4.6.3

LUT Gallery usability and preview-sync release, based on the user-validated v4.6.2.1 stable line.

## Added / Improved

- Adds **Update Preview** directly to LUT Gallery. New or missing LUT previews can now be generated without leaving the Gallery.
- Deleted LUTs are synchronized conservatively: only an exact preview recorded by the old index is eligible, and only after its source LUT is confirmed missing.
- Matching Gallery thumbnails are removed by exact path hash; preview directories are never recursively deleted and may only be removed when already empty.
- Gallery counters refresh immediately after update and the status reports **Added / Deleted / Current** counts.
- Moves **Disable LUT** to the bottom action row after **Use Selected LUT**.
- Places **Update Preview** immediately after **Change Reference**.
- Right-aligns pagination controls, fixes the folder dropdown spacing, and shortens Smart Filter status to show the actual hidden count.

## Unchanged

- Existing Change Reference full preview-regeneration behavior.
- AV1 / HEVC / x264 encoding parameters and shared StudioBridge core.
- HDR Preserve, Grain, OpenSVPFlow, subtitles, AAC 256k, Batch Summary and H.264 upload behavior.

The final Gallery layout and add/delete preview synchronization were user-tested through `R6T2B` before release.
'''
notes_path = ROOT / 'RELEASE_NOTES_v4.6.3.md'
notes_path.write_text(notes, encoding='utf-8')

# Create GitHub Release with clean assets.
run('gh', 'release', 'create', VERSION, ZIP_NAME, SHA_NAME, '--title', 'Film Grain Studio v4.6.3', '--notes-file', str(notes_path), '--verify-tag')

print('RELEASE_SHA256=' + sha)
print('RELEASE_ZIP=' + ZIP_NAME)
