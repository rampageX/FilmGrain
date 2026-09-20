@echo off
setlocal EnableExtensions DisableDelayedExpansion
title Film Grain Studio One-Click Release

rem ============================================================
rem Film Grain Studio one-click release helper
rem
rem Usage:
rem   release.bat
rem   release.bat 4.8.6
rem
rem Workflow:
rem   1. Read or enter the release version.
rem   2. Update .github\release\version.txt.
rem   3. Stage and commit only the approved FGS source/document paths.
rem   4. Build and validate a local Stable ZIP from the committed HEAD.
rem   5. Create an annotated local tag.
rem   6. Atomically push master and the tag together.
rem   7. The existing Windows Server 2022 GitHub workflow creates the
rem      official Release ZIP and SHA256 assets from the same commit.
rem
rem release.bat itself is never staged or packaged by this script.
rem No branch is created, switched, merged, rebased, or deleted.
rem ============================================================

set "REMOTE_NAME=origin"
set "RELEASE_BRANCH=master"
set "VERSION_REL=.github\release\version.txt"
set "NOTES_REL=.github\release\RELEASE_NOTES.md"

set "REPO_ROOT="
set "PUSHD_DONE="
set "STAGE_DIR="
set "TEMP_ARCHIVE="
set "DETECTED_VERSION="
set "VERSION="
set "TAG_NAME="
set "HEAD_SHA="
set "REMOTE_SHA="
set "LOCAL_TAG_SHA="
set "INDEX_TOUCHED=0"
set "COMMIT_CREATED=0"
set "TAG_CREATED=0"

echo ============================================================
echo Film Grain Studio 一键正式发布
echo ============================================================
echo.

where git.exe >nul 2>&1
if errorlevel 1 (
    echo [错误] 未找到 Git。请先安装 Git for Windows，并确认 git.exe 已加入 PATH。
    goto :FAIL
)

where powershell.exe >nul 2>&1
if errorlevel 1 (
    echo [错误] 未找到 Windows PowerShell。
    goto :FAIL
)

for /f "usebackq delims=" %%I in (`git -C "%~dp0." rev-parse --show-toplevel 2^>nul`) do if not defined REPO_ROOT set "REPO_ROOT=%%I"
if not defined REPO_ROOT (
    echo [错误] release.bat 必须放在 FGS Git 工作区内运行。
    goto :FAIL
)

pushd "%REPO_ROOT%" >nul
if errorlevel 1 (
    echo [错误] 无法进入 Git 仓库目录："%REPO_ROOT%"
    goto :FAIL
)
set "PUSHD_DONE=1"

git remote get-url "%REMOTE_NAME%" >nul 2>&1
if errorlevel 1 (
    echo [错误] Git 远程仓库 "%REMOTE_NAME%" 不存在。
    goto :FAIL
)

set "CURRENT_BRANCH="
for /f "usebackq delims=" %%B in (`git branch --show-current 2^>nul`) do if not defined CURRENT_BRANCH set "CURRENT_BRANCH=%%B"
if not defined CURRENT_BRANCH (
    echo [错误] 当前处于 detached HEAD，不能执行正式发布。
    goto :FAIL
)
if /i not "%CURRENT_BRANCH%"=="%RELEASE_BRANCH%" (
    echo [错误] 当前分支是 "%CURRENT_BRANCH%"，正式发布必须在 "%RELEASE_BRANCH%" 分支执行。
    goto :FAIL
)

git ls-files --error-unmatch -- "release.bat" >nul 2>&1
if not errorlevel 1 (
    echo [错误] release.bat 当前已被 Git 跟踪。
    echo        现有 GitHub 工作流会把所有受跟踪的根目录文件打进正式包，
    echo        因此请让 release.bat 保持为本地工具，不要提交到仓库。
    goto :FAIL
)

git rev-parse -q --verify MERGE_HEAD >nul 2>&1
if not errorlevel 1 (
    echo [错误] 当前仓库正在执行 Merge。请先完成或取消后再发布。
    goto :FAIL
)
git rev-parse -q --verify REBASE_HEAD >nul 2>&1
if not errorlevel 1 (
    echo [错误] 当前仓库正在执行 Rebase。请先完成或取消后再发布。
    goto :FAIL
)
git rev-parse -q --verify CHERRY_PICK_HEAD >nul 2>&1
if not errorlevel 1 (
    echo [错误] 当前仓库正在执行 Cherry-pick。请先完成或取消后再发布。
    goto :FAIL
)

set "UNMERGED_FILE="
for /f "usebackq delims=" %%U in (`git diff --name-only --diff-filter^=U`) do if not defined UNMERGED_FILE set "UNMERGED_FILE=%%U"
if defined UNMERGED_FILE (
    echo [错误] 存在未解决的冲突文件："%UNMERGED_FILE%"
    goto :FAIL
)

git diff --cached --quiet --ignore-submodules --
if errorlevel 1 (
    echo [错误] 暂存区已有内容。为避免误提交，请先提交或取消暂存，再运行 release.bat。
    goto :FAIL
)

echo [1/8] 正在读取远程状态...
git fetch --quiet --prune "%REMOTE_NAME%"
if errorlevel 1 (
    echo [错误] 无法从 "%REMOTE_NAME%" 获取最新状态。请检查网络或 GitHub 身份验证。
    goto :FAIL
)

git rev-parse --verify "refs/remotes/%REMOTE_NAME%/%RELEASE_BRANCH%" >nul 2>&1
if errorlevel 1 (
    echo [错误] 未找到远程分支 "%REMOTE_NAME%/%RELEASE_BRANCH%"。
    goto :FAIL
)

git merge-base --is-ancestor "refs/remotes/%REMOTE_NAME%/%RELEASE_BRANCH%" HEAD
if errorlevel 1 (
    echo [错误] 本地 master 落后于远程或已经分叉。
    echo        release.bat 不会自动 Pull、Merge 或 Rebase，请先人工同步仓库。
    goto :FAIL
)

set "FGS_RELEASE_ROOT=%REPO_ROOT%"
set "FGS_VERSION_FILE=%REPO_ROOT%\%VERSION_REL%"
set "FGS_NOTES_FILE=%REPO_ROOT%\%NOTES_REL%"

if exist "%FGS_VERSION_FILE%" (
    for /f "usebackq delims=" %%V in (`powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$v=[IO.File]::ReadAllText($env:FGS_VERSION_FILE).Trim();if($v -match '^v?(\d+(?:\.\d+){2,3})$'){$Matches[1]}" 2^>nul`) do if not defined DETECTED_VERSION set "DETECTED_VERSION=%%V"
)

set "VERSION=%~1"
if defined VERSION goto :VERSION_ENTERED

if defined DETECTED_VERSION (
    echo [信息] %VERSION_REL% 当前版本：v%DETECTED_VERSION%
    set /p "VERSION=请输入本次发布版本，直接回车使用 v%DETECTED_VERSION%："
    if not defined VERSION set "VERSION=%DETECTED_VERSION%"
) else (
    set /p "VERSION=请输入本次发布版本，例如 4.8.6："
)

:VERSION_ENTERED
if /i "%VERSION:~0,1%"=="v" set "VERSION=%VERSION:~1%"
set "FGS_RELEASE_VERSION=%VERSION%"
powershell.exe -NoLogo -NoProfile -NonInteractive -Command "if($env:FGS_RELEASE_VERSION -match '^\d+(?:\.\d+){2,3}$'){exit 0};exit 1" >nul 2>&1
if errorlevel 1 (
    echo [错误] 版本号 "%VERSION%" 无效。只接受 x.x.x 或 x.x.x.x 格式。
    goto :FAIL
)

set "TAG_NAME=v%VERSION%"
set "FGS_RELEASE_TAG=%TAG_NAME%"
set "ARCHIVE_NAME=FilmGrain_Studio_v%VERSION%_Stable.zip"
set "ARCHIVE_PATH=%REPO_ROOT%\%ARCHIVE_NAME%"
set "SHA_PATH=%ARCHIVE_PATH%.sha256"

call :CHECK_REMOTE_TAG
if errorlevel 1 goto :FAIL

if not exist "%REPO_ROOT%\.github\release\" (
    echo [错误] 缺少 .github\release 目录，无法触发现有 Release 工作流。
    goto :FAIL
)

echo [2/8] 正在写入发布版本并检查版本资料...
powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$e=New-Object System.Text.UTF8Encoding($false);[IO.File]::WriteAllText($env:FGS_VERSION_FILE,($env:FGS_RELEASE_VERSION+'`r`n'),$e)"
if errorlevel 1 (
    echo [错误] 无法写入 %VERSION_REL%。
    goto :FAIL
)

for %%P in (
    "README.md"
    "CHANGELOG.md"
    "STABLE_BASELINE.txt"
    "Utils\FilmGrain_Studio.ps1"
    "%NOTES_REL%"
) do (
    if not exist "%REPO_ROOT%\%%~P" (
        echo [错误] 缺少正式发布所需文件：%%~P
        goto :FAIL
    )
)

powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop';$r=$env:FGS_RELEASE_ROOT;$v=$env:FGS_RELEASE_VERSION;$q=[regex]::Escape($v);$baseline=[IO.File]::ReadAllText((Join-Path $r 'STABLE_BASELINE.txt'));$m=[regex]::Match($baseline,'(?m)^\s*v?(\d+(?:\.\d+){2,3})\s*$');if(-not $m.Success -or $m.Groups[1].Value -ne $v){throw 'STABLE_BASELINE.txt version does not match v'+$v};$change=[IO.File]::ReadAllText((Join-Path $r 'CHANGELOG.md'));if($change -notmatch ('(?m)^##\s+v'+$q+'\b')){throw 'CHANGELOG.md has no top-level entry for v'+$v};$notes=[IO.File]::ReadAllText($env:FGS_NOTES_FILE);if($notes -notmatch ('(?m)^#\s+Film Grain Studio v'+$q+'\s*$')){throw 'RELEASE_NOTES.md title does not match v'+$v};$studio=[IO.File]::ReadAllText((Join-Path $r 'Utils\FilmGrain_Studio.ps1'));if($studio -notmatch ('(?m)^\s*statusVersion\.Text\s*=\s*''v'+$q+'''\s*$')){throw 'GUI status version does not match v'+$v};if($studio -match 'statusVersion\.Text[^\r\n]*TEST'){throw 'GUI still contains a TEST version marker'}"
if errorlevel 1 (
    echo [错误] 版本资料不一致。请只修正上面指出的文件后重新运行。
    goto :FAIL
)

echo [3/8] 正在暂存本次 FGS 源码与发布文档...
git add -A -- "images" "Lang" "Utils" "_AV1_Grain_Tables" "_LUT_Tools" "_OpenSVPFlow" ".gitattributes" "CHANGELOG.md" "FilmGrain_Config.ini" "FilmGrain_Universal_HEVC_AV1_CLI.bat" "FilmGrain_Universal_HEVC_AV1_GUI.bat" "LICENSE" "README.md" "README_FilmGrain_Studio.txt" "README_Toolkit.txt" "STABLE_BASELINE.txt" ".github/release/version.txt" ".github/release/RELEASE_NOTES.md"
if errorlevel 1 (
    echo [错误] Git 暂存正式发布文件失败。
    goto :FAIL
)
set "INDEX_TOUCHED=1"

rem Runtime and user-state files must never enter a source commit.
git reset -q HEAD -- "Utils/_HardwareCaps.json" "_LUT_Tools/LUT_Reference_Current.jpg" "_OpenSVPFlow/Plugins/open-svpflow-version.txt" "_OpenSVPFlow/_PluginBackup" >nul 2>&1

set "HAVE_STAGED=0"
git diff --cached --quiet --ignore-submodules --
if errorlevel 1 set "HAVE_STAGED=1"
if "%HAVE_STAGED%"=="0" goto :USE_EXISTING_COMMITS

echo.
echo -------------------- 即将提交的内容 ------------------------
git diff --cached --stat
echo ------------------------------------------------------------
echo [信息] release.bat、正式 ZIP、benchmark 和其他未列入发布清单的文件不会提交。
echo [信息] 下方 Git 状态仅供核对；未暂存项目将保持原样。
git status --short
echo.
echo 版本：%TAG_NAME%
echo 本地压缩包："%ARCHIVE_PATH%"
choice /C YN /N /M "确认提交、打包并原子推送 master 与 Tag 吗？[Y/N]："
if errorlevel 2 goto :CANCEL_BEFORE_COMMIT

git commit -m "Release Film Grain Studio %TAG_NAME%"
if errorlevel 1 (
    echo [错误] Git Commit 失败。未执行 Tag 或 Push。
    goto :FAIL
)
set "INDEX_TOUCHED=0"
set "COMMIT_CREATED=1"
goto :COMMIT_READY

:USE_EXISTING_COMMITS
set "INDEX_TOUCHED=0"
set "AHEAD_COUNT="
for /f "usebackq delims=" %%N in (`git rev-list --count "refs/remotes/%REMOTE_NAME%/%RELEASE_BRANCH%..HEAD"`) do if not defined AHEAD_COUNT set "AHEAD_COUNT=%%N"
if "%AHEAD_COUNT%"=="0" (
    echo [错误] 当前没有可提交或可推送的新修改，无法触发 Release 工作流。
    echo        请先放入测试通过的脚本，并更新 README、CHANGELOG 和发布资料。
    goto :FAIL
)
echo.
echo [信息] 工作区没有新的发布修改，但本地 master 有 %AHEAD_COUNT% 个尚未推送的提交。
echo 版本：%TAG_NAME%
echo 本地压缩包："%ARCHIVE_PATH%"
choice /C YN /N /M "使用这些现有提交继续打包并原子推送吗？[Y/N]："
if errorlevel 2 goto :CANCEL

:COMMIT_READY
git diff --cached --quiet --ignore-submodules --
if errorlevel 1 (
    echo [错误] Commit 后暂存区仍有内容，已停止发布。
    goto :FAIL
)

set "HEAD_SHA="
for /f "usebackq delims=" %%H in (`git rev-parse HEAD`) do if not defined HEAD_SHA set "HEAD_SHA=%%H"

git diff --quiet "refs/remotes/%REMOTE_NAME%/%RELEASE_BRANCH%..HEAD" -- ".github/release/version.txt"
set "VERSION_DIFF_RC=%ERRORLEVEL%"
if "%VERSION_DIFF_RC%"=="0" (
    echo [错误] 待推送提交中没有 %VERSION_REL% 的变化，因此不会触发 Release 工作流。
    goto :FAIL
)
if not "%VERSION_DIFF_RC%"=="1" (
    echo [错误] 无法确认版本触发文件的 Git 差异。
    goto :FAIL
)

echo [4/8] 正在建立正式包暂存目录...
call :CREATE_STAGE_DIR
if errorlevel 1 (
    echo [错误] 无法建立发布暂存目录。
    goto :FAIL
)
set "TEMP_ARCHIVE=%STAGE_DIR%.zip"
set "FGS_RELEASE_STAGE=%STAGE_DIR%"
set "FGS_RELEASE_TEMP_ZIP=%TEMP_ARCHIVE%"

for %%P in (
    "images"
    "Lang"
    "Utils"
    "_AV1_Grain_Tables"
    "_LUT_Tools"
    "_OpenSVPFlow"
    ".gitattributes"
    "CHANGELOG.md"
    "FilmGrain_Config.ini"
    "FilmGrain_Universal_HEVC_AV1_CLI.bat"
    "FilmGrain_Universal_HEVC_AV1_GUI.bat"
    "LICENSE"
    "README.md"
    "README_FilmGrain_Studio.txt"
    "README_Toolkit.txt"
    "STABLE_BASELINE.txt"
) do (
    git cat-file -e "HEAD:%%~P" 2>nul
    if errorlevel 1 (
        echo [错误] 当前提交缺少正式包项目：%%~P
        goto :FAIL
    )
)

rem Export only committed release paths. release.bat is deliberately excluded.
git ls-files -z -- "images" "Lang" "Utils" "_AV1_Grain_Tables" "_LUT_Tools" "_OpenSVPFlow" ".gitattributes" "CHANGELOG.md" "FilmGrain_Config.ini" "FilmGrain_Universal_HEVC_AV1_CLI.bat" "FilmGrain_Universal_HEVC_AV1_GUI.bat" "LICENSE" "README.md" "README_FilmGrain_Studio.txt" "README_Toolkit.txt" "STABLE_BASELINE.txt" | git checkout-index --force --prefix="%STAGE_DIR%/" -z --stdin
if errorlevel 1 (
    echo [错误] 从已提交版本导出正式包文件失败。
    goto :FAIL
)

echo [5/8] 正在规范化并检查正式包...
powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop';$s=$env:FGS_RELEASE_STAGE;$bom=New-Object System.Text.UTF8Encoding($true);$noBom=New-Object System.Text.UTF8Encoding($false);Get-ChildItem -LiteralPath $s -Recurse -Filter '*.ps1' -File|ForEach-Object{$b=[IO.File]::ReadAllBytes($_.FullName);$o=0;if($b.Length -ge 3 -and $b[0] -eq 239 -and $b[1] -eq 187 -and $b[2] -eq 191){$o=3};$t=[System.Text.Encoding]::UTF8.GetString($b,$o,$b.Length-$o)-replace '\r?\n','`r`n';[IO.File]::WriteAllText($_.FullName,$t,$bom)};Get-ChildItem -LiteralPath (Join-Path $s 'Lang') -Filter '*.ini' -File|ForEach-Object{$b=[IO.File]::ReadAllBytes($_.FullName);$o=0;if($b.Length -ge 3 -and $b[0] -eq 239 -and $b[1] -eq 187 -and $b[2] -eq 191){$o=3};$t=[System.Text.Encoding]::UTF8.GetString($b,$o,$b.Length-$o)-replace '\r?\n','`r`n';[IO.File]::WriteAllText($_.FullName,$t,$noBom)}"
if errorlevel 1 (
    echo [错误] PS1 或语言资源规范化失败。
    goto :FAIL
)

powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop';$s=$env:FGS_RELEASE_STAGE;$required=@('FilmGrain_Universal_HEVC_AV1_GUI.bat','FilmGrain_Universal_HEVC_AV1_CLI.bat','Utils\FilmGrain_Studio.ps1','Utils\FGS_Benchmark.cmd','Utils\FGS_Benchmark.ps1','Utils\FilmGrain_Language.ps1','Lang\zh-CN.ini','Lang\en-US.ini','Lang\FilmGrain_Language.ini','Utils\_FilmGrainSimplified\NoiseFallback_512x512_RGBA8.rgba');foreach($r in $required){if(-not(Test-Path -LiteralPath (Join-Path $s $r)-PathType Leaf)){throw 'Required file missing: '+$r}};. (Join-Path $s 'Utils\FilmGrain_Language.ps1');$codes=@(Get-FgAvailableLanguages|ForEach-Object{[string]$_.Code});if($codes -notcontains 'zh-CN' -or $codes -notcontains 'en-US'){throw 'Required language missing'};$studio=[IO.File]::ReadAllText((Join-Path $s 'Utils\FilmGrain_Studio.ps1'));$used=@([regex]::Matches($studio,'\bL\s+''([^'']+)''')|ForEach-Object{$_.Groups[1].Value}|Sort-Object -Unique);function Keys([string]$p){$h=@{};foreach($line in [IO.File]::ReadAllLines($p,[System.Text.Encoding]::UTF8)){$x=$line.Trim();if(-not $x -or $x.StartsWith('#') -or $x.StartsWith(';')){continue};$i=$line.IndexOf('=');if($i -gt 0){$h[$line.Substring(0,$i).Trim()]=$true}};$h};$zh=Keys (Join-Path $s 'Lang\zh-CN.ini');$en=Keys (Join-Path $s 'Lang\en-US.ini');$mz=@($used|Where-Object{-not $zh.ContainsKey($_)});$me=@($used|Where-Object{-not $en.ContainsKey($_)});if($mz.Count){throw 'Missing zh-CN keys: '+($mz -join ', ')};if($me.Count){throw 'Missing en-US keys: '+($me -join ', ')};Get-ChildItem -LiteralPath $s -Recurse -Filter '*.ps1' -File|ForEach-Object{$tokens=$null;$errors=$null;[void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName,[ref]$tokens,[ref]$errors);if($errors.Count){throw 'PowerShell parse failed: '+$_.FullName};$t=[IO.File]::ReadAllText($_.FullName);if($t -match '[“”‘’]'){throw 'Curly quote found: '+$_.FullName}}"
if errorlevel 1 (
    echo [错误] PowerShell 或多语言完整性检查失败。
    goto :FAIL
)

powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop';$bad=@();Get-ChildItem -LiteralPath $env:FGS_RELEASE_STAGE -Recurse -File|Where-Object{$_.Extension.ToLowerInvariant() -in '.bat','.cmd','.vbs'}|ForEach-Object{$b=[IO.File]::ReadAllBytes($_.FullName);$bom=$b.Length -ge 3 -and $b[0] -eq 239 -and $b[1] -eq 187 -and $b[2] -eq 191;if($bom){$bad+=('UTF-8 BOM: '+$_.FullName)};$t=[Text.Encoding]::Default.GetString($b);if($t -match '(?<!\r)\n'){$bad+=('LF-only newline: '+$_.FullName)};if($_.Extension.ToLowerInvariant() -in '.bat','.cmd'){if($t -match '(?m)\^[ \t]+\r?$'){$bad+=('Caret trailing whitespace: '+$_.FullName)};$first=($t -split '\r\n'|Where-Object{$_.Trim()}|Select-Object -First 1);if($null -eq $first -or $first.Trim().ToLowerInvariant() -ne '@echo off'){$bad+=('Missing @echo off: '+$_.FullName)}}};if($bad.Count){$bad|ForEach-Object{[Console]::Error.WriteLine($_)};exit 1}"
if errorlevel 1 (
    echo [错误] BAT、CMD 或 VBS 格式检查失败。
    goto :FAIL
)

echo [6/8] 正在生成 %ARCHIVE_NAME%...
powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop';Add-Type -AssemblyName System.IO.Compression.FileSystem;if(Test-Path -LiteralPath $env:FGS_RELEASE_TEMP_ZIP){Remove-Item -LiteralPath $env:FGS_RELEASE_TEMP_ZIP -Force};[IO.Compression.ZipFile]::CreateFromDirectory($env:FGS_RELEASE_STAGE,$env:FGS_RELEASE_TEMP_ZIP,[IO.Compression.CompressionLevel]::Optimal,$false);$z=[IO.Compression.ZipFile]::OpenRead($env:FGS_RELEASE_TEMP_ZIP);try{$n=@($z.Entries|ForEach-Object{$_.FullName.Replace('\','/')});foreach($r in @('.gitattributes','CHANGELOG.md','FilmGrain_Universal_HEVC_AV1_CLI.bat','FilmGrain_Universal_HEVC_AV1_GUI.bat','LICENSE','README.md','STABLE_BASELINE.txt')){if($n -notcontains $r){throw 'Missing ZIP entry: '+$r}};foreach($d in @('images/','Lang/','Utils/','_AV1_Grain_Tables/','_LUT_Tools/','_OpenSVPFlow/')){if(-not($n|Where-Object{$_ -like ($d+'*')})){throw 'Missing ZIP directory: '+$d}};if($n -contains 'release.bat'){throw 'release.bat must not be packaged'};if($n|Where-Object{$_ -like '.git/*' -or $_ -like '.github/*' -or $_ -like 'benchmark/*'}){throw 'Development files found in ZIP'}}finally{$z.Dispose()}"
if errorlevel 1 (
    echo [错误] ZIP 创建或内容验证失败。
    goto :FAIL
)

copy /b /y "%TEMP_ARCHIVE%" "%ARCHIVE_PATH%" >nul
if errorlevel 1 (
    echo [错误] 无法把正式压缩包写入仓库根目录。
    goto :FAIL
)

set "FGS_RELEASE_ZIP=%ARCHIVE_PATH%"
set "FGS_RELEASE_SHA=%SHA_PATH%"
powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$e=New-Object System.Text.UTF8Encoding($false);$h=(Get-FileHash -LiteralPath $env:FGS_RELEASE_ZIP -Algorithm SHA256).Hash.ToLowerInvariant();[IO.File]::WriteAllText($env:FGS_RELEASE_SHA,($h+'  '+[IO.Path]::GetFileName($env:FGS_RELEASE_ZIP)+'`r`n'),$e)"
if errorlevel 1 (
    echo [错误] 生成 SHA256 文件失败。
    goto :FAIL
)

echo [7/8] 正在创建本地 Tag %TAG_NAME%...
git show-ref --verify --quiet "refs/tags/%TAG_NAME%"
if errorlevel 1 goto :CREATE_TAG

set "LOCAL_TAG_SHA="
for /f "usebackq delims=" %%H in (`git rev-list -n 1 "%TAG_NAME%"`) do if not defined LOCAL_TAG_SHA set "LOCAL_TAG_SHA=%%H"
if /i "%LOCAL_TAG_SHA%"=="%HEAD_SHA%" (
    echo [信息] 本地 Tag 已指向当前发布提交，将直接复用。
    goto :TAG_READY
)

echo [警告] 本地 Tag %TAG_NAME% 指向旧提交 %LOCAL_TAG_SHA%。
choice /C YN /N /M "远程尚无此 Tag，是否删除并重建本地 Tag？[Y/N]："
if errorlevel 2 goto :CANCEL
git tag -d "%TAG_NAME%" >nul
if errorlevel 1 (
    echo [错误] 无法删除旧的本地 Tag。
    goto :FAIL
)

:CREATE_TAG
git tag -a "%TAG_NAME%" -m "Film Grain Studio %TAG_NAME% Stable" "%HEAD_SHA%"
if errorlevel 1 (
    echo [错误] 创建本地 Tag 失败。Commit 与本地 ZIP 已保留。
    goto :FAIL
)
set "TAG_CREATED=1"

:TAG_READY
echo [8/8] 正在进行发布前复核并原子推送 master 与 Tag...
git fetch --quiet --prune "%REMOTE_NAME%"
if errorlevel 1 (
    echo [错误] 推送前无法刷新远程状态。Commit、Tag 与本地 ZIP 已保留。
    goto :FAIL
)

git merge-base --is-ancestor "refs/remotes/%REMOTE_NAME%/%RELEASE_BRANCH%" HEAD
if errorlevel 1 (
    echo [错误] 远程 master 已发生变化。为避免覆盖，未执行 Push。
    echo        请人工同步后重新运行；本地 Commit、Tag 与 ZIP 均已保留。
    goto :FAIL
)

call :CHECK_REMOTE_TAG
if errorlevel 1 goto :FAIL

git diff --quiet "refs/remotes/%REMOTE_NAME%/%RELEASE_BRANCH%..HEAD" -- ".github/release/version.txt"
set "VERSION_DIFF_RC=%ERRORLEVEL%"
if not "%VERSION_DIFF_RC%"=="1" (
    echo [错误] 远程差异中已找不到版本触发文件，未执行 Push。
    goto :FAIL
)

git push --atomic "%REMOTE_NAME%" "HEAD:refs/heads/%RELEASE_BRANCH%" "refs/tags/%TAG_NAME%:refs/tags/%TAG_NAME%"
if errorlevel 1 goto :PUSH_FAILED

call :CLEANUP
if defined PUSHD_DONE popd >nul
set "PUSHD_DONE="
echo.
echo ============================================================
echo 一键发布已提交。
echo Commit：%HEAD_SHA%
echo Tag：%TAG_NAME%
echo 本地 ZIP："%ARCHIVE_PATH%"
echo SHA256："%SHA_PATH%"
echo.
echo master 与 Tag 已在同一次原子 Push 中上传。
echo %VERSION_REL% 的变化将触发 Windows Server 2022 Release 工作流；
echo 工作流会从同一提交重新构建正式 ZIP，并上传 ZIP 与 SHA256 到 GitHub Release。
echo ============================================================
echo.
pause
exit /b 0

:CHECK_REMOTE_TAG
git ls-remote --exit-code --tags "%REMOTE_NAME%" "refs/tags/%TAG_NAME%" >nul 2>&1
set "REMOTE_TAG_RC=%ERRORLEVEL%"
if "%REMOTE_TAG_RC%"=="2" exit /b 0
if "%REMOTE_TAG_RC%"=="0" (
    echo [错误] 远程 Tag "%TAG_NAME%" 已存在，不能重复发布同一版本。
    exit /b 1
)
echo [错误] 无法确认远程 Tag 状态。请检查网络或 GitHub 身份验证。
exit /b 1

:CREATE_STAGE_DIR
set "STAGE_DIR=%TEMP%\FGS_Release_%RANDOM%_%RANDOM%_%RANDOM%"
if exist "%STAGE_DIR%\" goto :CREATE_STAGE_DIR
mkdir "%STAGE_DIR%" >nul 2>&1
if errorlevel 1 exit /b 1
exit /b 0

:CLEANUP
if defined STAGE_DIR if exist "%STAGE_DIR%\" rmdir /s /q "%STAGE_DIR%" >nul 2>&1
if defined TEMP_ARCHIVE if exist "%TEMP_ARCHIVE%" del /f /q "%TEMP_ARCHIVE%" >nul 2>&1
exit /b 0

:CANCEL_BEFORE_COMMIT
if "%INDEX_TOUCHED%"=="1" git reset -q >nul 2>&1
set "INDEX_TOUCHED=0"
goto :CANCEL

:PUSH_FAILED
call :CLEANUP
echo.
echo [错误] 原子 Push 失败，因此远程 master 与远程 Tag 都不会只更新一半。
echo        本地 Commit、Tag、ZIP 与 SHA256 均已保留，排除网络或权限问题后可重新运行。
if defined PUSHD_DONE popd >nul
set "PUSHD_DONE="
echo.
pause
exit /b 1

:CANCEL
call :CLEANUP
if defined PUSHD_DONE popd >nul
set "PUSHD_DONE="
echo.
echo 操作已取消。未执行远程 Push；已经产生的本地 Commit、Tag 或 ZIP 将保留。
echo.
pause
exit /b 2

:FAIL
if "%INDEX_TOUCHED%"=="1" git reset -q >nul 2>&1
set "INDEX_TOUCHED=0"
call :CLEANUP
if defined PUSHD_DONE popd >nul
set "PUSHD_DONE="
echo.
echo 发布未完成，未执行新的远程写入。
echo.
pause
exit /b 1
