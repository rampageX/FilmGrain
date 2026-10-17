[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$RepositoryRoot,
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+(?:\.\d+){2,3}$')][string]$Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$crlf = [char]13 + [char]10
function Read-ReleaseText([string]$Relative) {
    $path = Join-Path $RepositoryRoot $Relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Missing release metadata: ' + $Relative) }
    return [IO.File]::ReadAllText($path)
}
function Write-ReleaseText([string]$Relative, [string]$Text) {
    $path = Join-Path $RepositoryRoot $Relative
    $bytes = [IO.File]::ReadAllBytes($path)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $encoding = New-Object System.Text.UTF8Encoding($bom)
    [IO.File]::WriteAllText($path, [regex]::Replace($Text, '\r\n|\r|\n', $crlf), $encoding)
}
# Prepare and validate every new value before touching any file.
$native = Read-ReleaseText 'dotnet/FilmGrain_Studio_NET_Preview.cs'
$pattern = '(internal const string ReleaseVersion\s*=\s*)"([0-9.]+)"'
$m = [regex]::Matches($native, $pattern)
if ($m.Count -ne 1) { throw 'Native ReleaseVersion must occur exactly once.' }
$native = $native.Substring(0,$m[0].Index) + $m[0].Groups[1].Value + '"' + $Version + '"' + $native.Substring($m[0].Index+$m[0].Length)
$parsedVersion = [version]$Version
$fixedVersion = '{0}.{1}.{2}.{3}' -f $parsedVersion.Major,$parsedVersion.Minor,[Math]::Max(0,$parsedVersion.Build),[Math]::Max(0,$parsedVersion.Revision)
$filePattern = '(internal const string FileReleaseVersion\s*=\s*)"([0-9.]+)"'
$fileMatch = [regex]::Matches($native,$filePattern)
if ($fileMatch.Count -ne 1) { throw 'Native FileReleaseVersion must occur exactly once.' }
$native = $native.Substring(0,$fileMatch[0].Index) + $fileMatch[0].Groups[1].Value + '"' + $fixedVersion + '"' + $native.Substring($fileMatch[0].Index+$fileMatch[0].Length)
$buildPattern = '(internal const string BuildLabel\s*=\s*)"[^"\r\n]+"'
$m = [regex]::Matches($native,$buildPattern)
if ($m.Count -ne 1 -or $native -notmatch 'private const string TestBuild\s*=\s*Program\.BuildLabel\s*;') { throw 'Unexpected Native build-label declaration.' }
$native = $native.Substring(0,$m[0].Index) + $m[0].Groups[1].Value + '"Stable"' + $native.Substring($m[0].Index+$m[0].Length)
$previous = (Read-ReleaseText '.github/release/version.txt').Trim()
if ($previous -notmatch '^\d+(?:\.\d+){2,3}$') { throw 'Invalid previous version.txt.' }
# A failed release may already have written its target version in the working
# tree. Use the committed baseline for same-version retry/downgrade decisions.
# Non-Git standalone test directories keep the existing file-based behavior.
. (Join-Path $PSScriptRoot 'release_layout.ps1')
$committedVersion = ''
try { $committedVersion = (Invoke-ReleaseGitUtf8 -Root $RepositoryRoot -GitArguments 'show HEAD:.github/release/version.txt').Trim() } catch { }
if ($committedVersion) {
    if ($committedVersion -notmatch '^\d+(?:\.\d+){2,3}$') { throw 'Invalid committed release version.' }
    $previous = $committedVersion
}
$change = Read-ReleaseText 'CHANGELOG.md'
if ([version]$Version -lt [version]$previous -or ([version]$Version -eq [version]$previous -and $change -match '(?m)^##[ \t]+未发布累计修订')) { throw 'Pending changes require a newer version than the published baseline.' }
$section = '(?ms)^##[ \t]+v' + [regex]::Escape($Version) + '(?=[ \t\r\n]|$)[^\r\n]*\r?\n(?<body>.*?)(?=^##[ \t]+|\z)'
$m = [regex]::Match($change,$section)
if (-not $m.Success) {
    if ([version]$Version -le [version]$previous) { throw 'Published version cannot consume pending changes; choose a newer version.' }
    $pending = [regex]::Matches($change,'(?m)^##[ \t]+未发布累计修订[^\r\n]*')
    if ($pending.Count -ne 1) { throw ('CHANGELOG requires an explicit v' + $Version + ' section or one pending cumulative section.') }
    $change = $change.Substring(0,$pending[0].Index) + '## v' + $Version + ' — 完全 .NET 迁移' + $change.Substring($pending[0].Index+$pending[0].Length)
    $change = [regex]::Replace($change,'(?m)^产品版本暂保持[^\r\n]*','本版完成应用运行时 .NET 迁移。以下历史测试条目保留当时状态，当前验收结果以本条为准。')
    $m = [regex]::Match($change,$section)
}
if (-not $m.Success -or [string]::IsNullOrWhiteSpace($m.Groups['body'].Value)) { throw 'Release changelog section missing or empty.' }
$notes = '# Film Grain Studio v' + $Version + $crlf + $crlf + $m.Groups['body'].Value.Trim() + $crlf
$readme = Read-ReleaseText 'README.md'
# Keep the README body and layout intact; synchronize only its version marker.
$intro = [regex]::Match($readme,'(?m)^> \*\*Film Grain Studio v[0-9.]+')
if (-not $intro.Success) { throw 'Unexpected README version marker.' }
$marker = '> **Film Grain Studio v' + $Version
$readme = $readme.Substring(0,$intro.Index) + $marker + $readme.Substring($intro.Index+$intro.Length)
$readme = [regex]::Replace($readme,'产品版本仍为 4\.8\.14[^\r\n]*','产品版本与实际 EXE 显示为 v' + $Version + '，构建标签 Stable。')
$studio = Read-ReleaseText 'README_FilmGrain_Studio.txt'
$toolkit = Read-ReleaseText 'README_Toolkit.txt'
foreach ($name in @('studio','toolkit')) {
    $text = Get-Variable -Name $name -ValueOnly
    if ($text -notmatch '(?m)^当前产品版本：[^\r\n]*') { throw 'TXT product version line missing.' }
    $text = [regex]::Replace($text,'(?m)^当前产品版本：[^\r\n]*','当前产品版本：v' + $Version + '；构建 Stable。')
    $text = [regex]::Replace($text,'(?m)^Tests_Shared[^\r\n]*\r?\n','')
    Set-Variable -Name $name -Value $text
}
$baselineOld = Read-ReleaseText 'STABLE_BASELINE.txt'
$supersedes = $previous
if ($previous -eq $Version) {
    $old = [regex]::Match($baselineOld,'(?m)^Supersedes:\r?\n(v[0-9.]+)')
    $supersedes = if ($old.Success) { $old.Groups[1].Value.TrimStart('v') } else { '' }
}
$baseline = @(
    'Film Grain Studio formal stable baseline', '', 'Baseline:', ('v' + $Version), 'Supersedes:', ('v' + $supersedes), '',
    'Formal release:', ('FilmGrain_Studio_v' + $Version + '_Stable.zip'), '',
    'Application runtime: .NET Native-only. Legacy tools archived and independent.',
    'Acceptance applies only to explicitly tested combinations, not every container/timestamp/clip/hardware combination.',
    'Windows Full: 101 encodes + 6 boundaries. Next: 29 + 10 merged technical passes. Native-only: 6 + 1 merged technical passes.',
    'User manual checks including new ADDED picture/sync confirmed. Merged results are not single full reruns.',
    'AV1 grain-copy phase: Windows SDR 242 / HDR 241 technical checks passed; fast paths and preview/log-toolbar fixes confirmed within their actual scope.',
    $(if ([version]$Version -ge [version]'4.8.22') { 'v4.8.22 preview acceptance: 48 Windows shared-entry smoke checks passed; user manual checks confirmed. Smoke mode labels do not imply full encoder reruns.' }),
    $(if ([version]$Version -ge [version]'4.8.26') { 'v4.8.26: baseline measurement 42 cases x3=126 executions; 66 pre-encode hash/PTS cases passed; 42 reference/candidate real encodes completed. Two first-run output checks were test-path false negatives, resolved by read-only verification of 4 existing outputs; no new encodes.' }),
    $(if ([version]$Version -ge [version]'4.8.26') { 'v4.8.26: user manual picture/A-V sync spot checks passed; measured 4K SDR/50% wall time reduced approximately29-51% on RTX4080/FFmpeg9.0.2. Results do not cover every strength, hardware or filter combination.' }),
    $(if ([version]$Version -ge [version]'4.8.27') { 'v4.8.27:212 PASS/0 FAIL/0 SKIP, including12 real preview tasks and180 full-variant PNG comparisons across3 rounds (1440 matching PNG pairs). User manual SDR/HDR preview spot checks passed. Prior synthetic HDR input gap closed with22 PASS including8 real encodes; old failed reports retained.' }),
    $(if ([version]$Version -ge [version]'5.0.00') { 'v5.0.00: .NET native milestone based on published4.8.27. EXE ProductName/FileDescription=FilmGrain Studio; file/fixed numeric/assembly version5.0.0.0; product version string5.0.00. Build gate requires matching managed attributes and native VERSIONINFO. Video behavior remains the accepted4.8.27 implementation.' }),
    'Root and compatible executables are rebuilt from the same committed C# source with Stable label.',
    'Shared Lang, SVP vpy/plugins, FGSIM template/texture and original grain tables remain in their shared locations.',
    'Exclude local configuration, caches, private environments, downloads, generated hooks, logs, Tests_Shared and test-delivery markers.',
    'Local release and Windows runner use the same build_release.ps1 and release_layout.ps1.',
    'Commit, tag and remote publication are maintainer actions; successful Dry Run alone is not publication.'
) -join $crlf
# Ensure all output destinations exist before the first write.
[void](Read-ReleaseText '.github/release/RELEASE_NOTES.md')
Write-ReleaseText 'dotnet/FilmGrain_Studio_NET_Preview.cs' $native
Write-ReleaseText '.github/release/version.txt' ($Version + $crlf)
Write-ReleaseText 'CHANGELOG.md' $change
Write-ReleaseText '.github/release/RELEASE_NOTES.md' $notes
Write-ReleaseText 'README.md' $readme
Write-ReleaseText 'README_FilmGrain_Studio.txt' $studio
Write-ReleaseText 'README_Toolkit.txt' $toolkit
Write-ReleaseText 'STABLE_BASELINE.txt' ($baseline + $crlf)
Write-Host ('[FGS] Native source and release metadata synchronized for v' + $Version + ' / Stable. EXEs must be rebuilt from this source.')
