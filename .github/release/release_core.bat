@echo off
setlocal EnableExtensions DisableDelayedExpansion
title Film Grain Studio One-Click Release

rem ============================================================
rem Film Grain Studio one-click release helper
rem
rem Usage:
rem   release.bat
rem   release.bat 4.8.6
rem   release.bat --dry-run
rem   release.bat --dry-run 4.8.9
rem   release.bat --test           (legacy alias of --dry-run)
rem
rem Workflow:
rem   1. Read or enter the release version.
rem   2. Update mechanical version fields and generate RELEASE_NOTES.md
rem      automatically from the matching CHANGELOG.md version section.
rem   3. Stage and commit only the approved FGS source/document paths.
rem   4. Call the shared release core to build and validate the Stable ZIP.
rem   5. Create an annotated local tag.
rem   6. Atomically push master and the tag together.
rem
rem Root release.bat is a stable launcher for this tracked script.
rem This script and GitHub Actions both call the same tracked
rem .github\release\build_release.ps1 build core.
rem
rem The release tools are tracked in Git but excluded from Stable packages.
rem No branch is created, switched, merged, rebased, or deleted.
rem
rem --dry-run mode does not modify the current repository:
rem   - clones the current local HEAD into an isolated temporary repository
rem   - overlays the current approved working-tree release files
rem   - synchronizes version fields and generates RELEASE_NOTES.md there
rem   - calls the same build_release.ps1 used by the formal release
rem   - never stages, commits, tags, pushes, or edits the real working tree
rem ============================================================

set "REMOTE_NAME=origin"
set "RELEASE_BRANCH=master"
set "VERSION_REL=.github\release\version.txt"
set "NOTES_REL=.github\release\RELEASE_NOTES.md"
set "BUILD_CORE_REL=.github\release\build_release.ps1"

set "REPO_ROOT="
set "PUSHD_DONE="
set "DETECTED_VERSION="
set "DEFAULT_VERSION="
set "VERSION="
set "TAG_NAME="
set "HEAD_SHA="
set "REMOTE_SHA="
set "LOCAL_TAG_SHA="
set "INDEX_TOUCHED=0"
set "COMMIT_CREATED=0"
set "TAG_CREATED=0"
set "DRY_RUN_MODE=0"
set "VERSION_ARG=%~1"
if /i "%~1"=="--dry-run" (
    set "DRY_RUN_MODE=1"
    set "VERSION_ARG=%~2"
)
if /i "%~1"=="--test" (
    set "DRY_RUN_MODE=1"
    set "VERSION_ARG=%~2"
)

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

if "%DRY_RUN_MODE%"=="1" goto :DRY_RUN_MODE

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

rem 本发布流程允许发布前已有经过人工核对的暂存内容。
rem 后续仍会按正式发布清单重新 git add，并在提交后检查暂存区必须为空。

echo [1/6] 正在读取远程状态...
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

set "VERSION=%VERSION_ARG%"
if defined VERSION goto :VERSION_ENTERED

if not defined DETECTED_VERSION goto :ASK_VERSION_WITHOUT_DEFAULT
call :RESOLVE_DEFAULT_VERSION
if errorlevel 1 goto :FAIL
echo [信息] %VERSION_REL% 当前版本：v%DETECTED_VERSION%
set /p "VERSION=请输入本次发布版本，直接回车使用 v%DEFAULT_VERSION%："
if not defined VERSION set "VERSION=%DEFAULT_VERSION%"
goto :VERSION_ENTERED

:ASK_VERSION_WITHOUT_DEFAULT
set /p "VERSION=请输入本次发布版本，例如 4.8.7："

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

if not exist "%REPO_ROOT%\%BUILD_CORE_REL%" (
    echo [错误] 缺少统一发布核心：%BUILD_CORE_REL%
    goto :FAIL
)

echo [2/6] 正在同步机械版本字段并调用统一发布核心预检...
call :SYNC_RELEASE_METADATA
if errorlevel 1 (
    echo [错误] 无法同步正式版本字段或生成 RELEASE_NOTES.md。未执行 Commit、Tag 或 Push。
    goto :FAIL
)
echo [信息] 已同步：%VERSION_REL%、STABLE_BASELINE.txt、GUI statusVersion.Text
echo [信息] 已从 CHANGELOG.md 的 %TAG_NAME% 章节自动生成：%NOTES_REL%

powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%REPO_ROOT%\%BUILD_CORE_REL%" -RepositoryRoot "%REPO_ROOT%" -Version "%VERSION%" -ValidateOnly
if errorlevel 1 (
    echo [错误] 统一发布核心预检失败。请按上方错误信息修正后重新运行。
    goto :FAIL
)

echo [3/6] 正在暂存本次 FGS 源码与发布文档...
git add -A -- "images" "Lang" "Utils" "_AV1_Grain_Tables" "_LUT_Tools" "_OpenSVPFlow" ".gitattributes" ".gitignore" "CHANGELOG.md" "FilmGrain_Config.default.ini" "FilmGrain_Universal_CLI.bat" "FilmGrain_Universal_GUI.bat" "LICENSE" "README.md" "README_FilmGrain_Studio.txt" "README_Toolkit.txt" "release.bat" "STABLE_BASELINE.txt" ".github/release/version.txt" ".github/release/RELEASE_NOTES.md" ".github/release/build_release.ps1" ".github/release/release_core.bat" ".github/workflows/release.yml"
if errorlevel 1 (
    echo [错误] Git 暂存正式发布文件失败。
    goto :FAIL
)
set "INDEX_TOUCHED=1"

rem Runtime and user-state files must never enter a source commit.
git reset -q HEAD -- "FilmGrain_Config.ini" "Utils/_HardwareCaps.json" "_LUT_Tools/LUT_Reference_Current.jpg" "_OpenSVPFlow/_PluginBackup" >nul 2>&1

set "HAVE_STAGED=0"
git diff --cached --quiet --ignore-submodules --
if errorlevel 1 set "HAVE_STAGED=1"
if "%HAVE_STAGED%"=="0" goto :USE_EXISTING_COMMITS

echo.
echo -------------------- 即将提交的内容 ------------------------
git diff --cached --stat
echo ------------------------------------------------------------
echo [信息] 正式 ZIP、benchmark 和其他未列入发布清单的文件不会提交。
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

echo [4/6] 正在调用统一发布核心构建并校验正式包...
powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%REPO_ROOT%\%BUILD_CORE_REL%" -RepositoryRoot "%REPO_ROOT%" -Version "%VERSION%" -OutputDirectory "%REPO_ROOT%"
if errorlevel 1 (
    echo [错误] 统一发布核心构建失败。Commit 已保留，但未创建 Tag 或远程写入。
    goto :FAIL
)

if not exist "%ARCHIVE_PATH%" (
    echo [错误] 统一发布核心未生成正式 ZIP："%ARCHIVE_PATH%"
    goto :FAIL
)
if not exist "%SHA_PATH%" (
    echo [错误] 统一发布核心未生成 SHA256："%SHA_PATH%"
    goto :FAIL
)

echo [5/6] 正在创建本地 Tag %TAG_NAME%...
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
echo [6/6] 正在进行发布前复核并原子推送 master 与 Tag...
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
echo 本地 release.bat 与 Windows runner 将调用同一份 build_release.ps1 构建逻辑。
echo ============================================================
echo.
pause
exit /b 0

:DRY_RUN_MODE
echo ============================================================
echo Film Grain Studio 发布系统 Dry Run
echo ============================================================
echo [安全] 不会修改当前工作区、暂存区、Commit、Tag、远程仓库或 Release。
echo [信息] 将在隔离临时仓库中预演当前工作区下一次正式发布的内容。
echo.

set "FGS_VERSION_FILE=%REPO_ROOT%\%VERSION_REL%"

if not exist "%FGS_VERSION_FILE%" (
    echo [错误] 缺少版本文件：%VERSION_REL%
    goto :DRY_RUN_FAIL
)

if not exist "%REPO_ROOT%\%BUILD_CORE_REL%" (
    echo [错误] 缺少统一发布核心：%BUILD_CORE_REL%
    goto :DRY_RUN_FAIL
)

set "DETECTED_VERSION="
for /f "usebackq delims=" %%V in (`powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$v=[IO.File]::ReadAllText($env:FGS_VERSION_FILE).Trim();if($v -match '^v?(\d+(?:\.\d+){2,3})$'){$Matches[1]}" 2^>nul`) do if not defined DETECTED_VERSION set "DETECTED_VERSION=%%V"

if not defined DETECTED_VERSION (
    echo [错误] %VERSION_REL% 中没有有效版本号。
    goto :DRY_RUN_FAIL
)

set "VERSION=%VERSION_ARG%"
if defined VERSION goto :DRY_RUN_VERSION_ENTERED
set "FGS_DETECTED_VERSION=%DETECTED_VERSION%"
for /f "usebackq delims=" %%V in (`powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$p=$env:FGS_DETECTED_VERSION.Split('.');$last=0;if($p.Length -lt 3 -or $p.Length -gt 4 -or -not [int]::TryParse($p[$p.Length-1],[ref]$last)){exit 1};$p[$p.Length-1]=($last+1).ToString();$p -join '.'" 2^>nul`) do if not defined VERSION set "VERSION=%%V"

:DRY_RUN_VERSION_ENTERED
if /i "%VERSION:~0,1%"=="v" set "VERSION=%VERSION:~1%"
set "FGS_RELEASE_VERSION=%VERSION%"
powershell.exe -NoLogo -NoProfile -NonInteractive -Command "if($env:FGS_RELEASE_VERSION -match '^\d+(?:\.\d+){2,3}$'){exit 0};exit 1" >nul 2>&1
if errorlevel 1 (
    echo [错误] Dry Run 版本号 "%VERSION%" 无效。只接受 x.x.x 或 x.x.x.x 格式。
    goto :DRY_RUN_FAIL
)

set "TAG_NAME=v%VERSION%"
set "FGS_RELEASE_TAG=%TAG_NAME%"
set "DRY_RUN_ROOT=%TEMP%\FGS_DryRun_Repo_v%VERSION%_%RANDOM%_%RANDOM%"
set "DRY_RUN_OUTPUT=%TEMP%\FGS_DryRun_Output_v%VERSION%_%RANDOM%_%RANDOM%"

echo [DRY RUN] Version : %TAG_NAME%
echo [DRY RUN] HEAD    :
git --no-pager log -1 --format="       %%H  %%s"
echo [DRY RUN] Output  : "%DRY_RUN_OUTPUT%"
echo.

echo [DRY RUN] 正在创建隔离临时仓库...
git clone --quiet --no-hardlinks -- "%REPO_ROOT%" "%DRY_RUN_ROOT%"
if errorlevel 1 goto :DRY_RUN_FAIL

set "FGS_DRY_SOURCE=%REPO_ROOT%"
set "FGS_DRY_TARGET=%DRY_RUN_ROOT%"
powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop';$src=$env:FGS_DRY_SOURCE;$dst=$env:FGS_DRY_TARGET;$items=@('images','Lang','Utils','_AV1_Grain_Tables','_LUT_Tools','_OpenSVPFlow','.gitattributes','.gitignore','CHANGELOG.md','FilmGrain_Config.default.ini','FilmGrain_Universal_CLI.bat','FilmGrain_Universal_GUI.bat','LICENSE','README.md','README_FilmGrain_Studio.txt','README_Toolkit.txt','release.bat','STABLE_BASELINE.txt','.github\release\version.txt','.github\release\RELEASE_NOTES.md','.github\release\build_release.ps1','.github\release\release_core.bat','.github\workflows\release.yml');foreach($rel in $items){$s=Join-Path $src $rel;$d=Join-Path $dst $rel;if(Test-Path -LiteralPath $d){Remove-Item -LiteralPath $d -Recurse -Force};if(Test-Path -LiteralPath $s){$parent=Split-Path -Parent $d;if(-not (Test-Path -LiteralPath $parent)){[void](New-Item -ItemType Directory -Path $parent -Force)};Copy-Item -LiteralPath $s -Destination $d -Recurse -Force}};$runtime=@('FilmGrain_Config.ini','Utils\_HardwareCaps.json','_LUT_Tools\LUT_Reference_Current.jpg','_OpenSVPFlow\_PluginBackup');foreach($rel in $runtime){$p=Join-Path $dst $rel;if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Recurse -Force}}"
if errorlevel 1 goto :DRY_RUN_FAIL

set "FGS_RELEASE_ROOT=%DRY_RUN_ROOT%"
set "FGS_VERSION_FILE=%DRY_RUN_ROOT%\%VERSION_REL%"
set "FGS_NOTES_FILE=%DRY_RUN_ROOT%\%NOTES_REL%"
call :SYNC_RELEASE_METADATA
if errorlevel 1 goto :DRY_RUN_FAIL

git -C "%DRY_RUN_ROOT%" add -A
if errorlevel 1 goto :DRY_RUN_FAIL
git -C "%DRY_RUN_ROOT%" -c user.name="FGS Dry Run" -c user.email="dry-run@localhost" commit --quiet -m "FGS Dry Run %TAG_NAME%"
if errorlevel 1 goto :DRY_RUN_FAIL

if not exist "%DRY_RUN_OUTPUT%" mkdir "%DRY_RUN_OUTPUT%" >nul 2>&1
if errorlevel 1 goto :DRY_RUN_FAIL
copy /y "%FGS_NOTES_FILE%" "%DRY_RUN_OUTPUT%\RELEASE_NOTES_%TAG_NAME%.md" >nul
if errorlevel 1 goto :DRY_RUN_FAIL

echo [DRY RUN] 已自动生成 Release Notes，并开始调用正式统一构建核心...
powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%DRY_RUN_ROOT%\%BUILD_CORE_REL%" -RepositoryRoot "%DRY_RUN_ROOT%" -Version "%VERSION%" -OutputDirectory "%DRY_RUN_OUTPUT%"
if errorlevel 1 goto :DRY_RUN_FAIL

set "FGS_DRY_CLEAN=%DRY_RUN_ROOT%"
powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$p=$env:FGS_DRY_CLEAN;$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath());$full=[IO.Path]::GetFullPath($p);if(-not $full.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)-or [IO.Path]::GetFileName($full) -notlike 'FGS_DryRun_Repo_*'){throw 'Unsafe Dry Run cleanup path'};if(Test-Path -LiteralPath $full){Remove-Item -LiteralPath $full -Recurse -Force}"
if errorlevel 1 (
    echo [警告] Dry Run 已通过，但隔离临时仓库清理失败："%DRY_RUN_ROOT%"
)

echo.
echo ============================================================
echo Dry Run 通过。
echo 当前工作区、暂存区、Commit、Tag 与远程仓库均未修改。
echo 测试 ZIP、SHA256 与 RELEASE_NOTES："%DRY_RUN_OUTPUT%"
echo ============================================================
echo.

if defined PUSHD_DONE popd >nul
set "PUSHD_DONE="
pause
exit /b 0

:DRY_RUN_FAIL
if defined DRY_RUN_ROOT if exist "%DRY_RUN_ROOT%" (
    set "FGS_DRY_CLEAN=%DRY_RUN_ROOT%"
    powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$p=$env:FGS_DRY_CLEAN;$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath());$full=[IO.Path]::GetFullPath($p);if($full.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)-and [IO.Path]::GetFileName($full) -like 'FGS_DryRun_Repo_*'){Remove-Item -LiteralPath $full -Recurse -Force}" >nul 2>&1
)
if defined PUSHD_DONE popd >nul
set "PUSHD_DONE="
echo.
echo [错误] Dry Run 失败。
echo 当前工作区、暂存区、Commit、Tag 与远程仓库均未修改。
echo.
pause
exit /b 1

:SYNC_RELEASE_METADATA
powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop';$r=$env:FGS_RELEASE_ROOT;$v=$env:FGS_RELEASE_VERSION;$utf8NoBom=New-Object System.Text.UTF8Encoding($false);$utf8Bom=New-Object System.Text.UTF8Encoding($true);[IO.File]::WriteAllText($env:FGS_VERSION_FILE,($v+[Environment]::NewLine),$utf8NoBom);$baselinePath=Join-Path $r 'STABLE_BASELINE.txt';if(-not (Test-Path -LiteralPath $baselinePath -PathType Leaf)){throw 'Missing STABLE_BASELINE.txt'};$baseline=[IO.File]::ReadAllText($baselinePath);$baselinePattern='(?m)^\s*v?\d+(?:\.\d+){2,3}\s*$';$baselineMatch=[regex]::Match($baseline,$baselinePattern);if(-not $baselineMatch.Success){throw 'STABLE_BASELINE.txt has no standalone version line'};$baseline=$baseline.Substring(0,$baselineMatch.Index)+('v'+$v)+$baseline.Substring($baselineMatch.Index+$baselineMatch.Length);[IO.File]::WriteAllText($baselinePath,$baseline,$utf8NoBom);$studioPath=Join-Path $r 'Utils\FilmGrain_Studio.ps1';if(-not (Test-Path -LiteralPath $studioPath -PathType Leaf)){throw 'Missing Utils\FilmGrain_Studio.ps1'};$studio=[IO.File]::ReadAllText($studioPath);$studioPattern='(?m)^(\s*\$?statusVersion\.Text\s*=\s*)''v\d+(?:\.\d+){2,3}''(\s*)$';$studioMatches=[regex]::Matches($studio,$studioPattern);if($studioMatches.Count -ne 1){throw 'GUI statusVersion.Text must contain exactly one clean release version line'};$studio=[regex]::Replace($studio,$studioPattern,{param($m)$m.Groups[1].Value+([char]39)+'v'+$v+([char]39)+$m.Groups[2].Value},1);[IO.File]::WriteAllText($studioPath,$studio,$utf8Bom);$changelogPath=Join-Path $r 'CHANGELOG.md';if(-not (Test-Path -LiteralPath $changelogPath -PathType Leaf)){throw 'Missing CHANGELOG.md'};$changelog=[IO.File]::ReadAllText($changelogPath);$escaped=[regex]::Escape($v);$notesPattern='(?ms)^##\s+v?'+$escaped+'(?:\s+[^\r\n]*)?\s*\r?\n(?<body>.*?)(?=^##\s+v?\d|\z)';$notesMatch=[regex]::Match($changelog,$notesPattern);if(-not $notesMatch.Success){throw ('CHANGELOG.md has no section for v'+$v)};$body=$notesMatch.Groups['body'].Value.Trim();if([string]::IsNullOrWhiteSpace($body)){throw ('CHANGELOG.md section v'+$v+' is empty')};$notes='# Film Grain Studio v'+$v+[Environment]::NewLine+[Environment]::NewLine+$body+[Environment]::NewLine;[IO.File]::WriteAllText($env:FGS_NOTES_FILE,$notes,$utf8NoBom)"
exit /b %ERRORLEVEL%

:RESOLVE_DEFAULT_VERSION
set "DEFAULT_VERSION=%DETECTED_VERSION%"
git ls-remote --exit-code --tags "%REMOTE_NAME%" "refs/tags/v%DETECTED_VERSION%" >nul 2>&1
set "DETECTED_TAG_RC=%ERRORLEVEL%"
if "%DETECTED_TAG_RC%"=="2" exit /b 0
if "%DETECTED_TAG_RC%"=="0" goto :RESOLVE_NEXT_PATCH
echo [错误] 无法确认远程 Tag "v%DETECTED_VERSION%" 状态。请检查网络或 GitHub 身份验证。
exit /b 1

:RESOLVE_NEXT_PATCH
set "FGS_DETECTED_VERSION=%DETECTED_VERSION%"
set "DEFAULT_VERSION="
for /f "usebackq delims=" %%V in (`powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$p=$env:FGS_DETECTED_VERSION.Split('.');if($p.Length -lt 3 -or $p.Length -gt 4){exit 1};$last=0;if(-not [int]::TryParse($p[$p.Length-1],[ref]$last)){exit 1};$p[$p.Length-1]=($last+1).ToString();$p -join '.'" 2^>nul`) do if not defined DEFAULT_VERSION set "DEFAULT_VERSION=%%V"
if not defined DEFAULT_VERSION (
    echo [错误] 无法根据 v%DETECTED_VERSION% 计算下一版本号。
    exit /b 1
)
echo [信息] 远程 Tag "v%DETECTED_VERSION%" 已存在，默认版本自动递增为 v%DEFAULT_VERSION%。
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

:CLEANUP
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
if "%COMMIT_CREATED%"=="1" echo [信息] 本次运行已经创建本地 Commit，但尚未创建远程写入。
echo 发布未完成，未执行新的远程写入。
echo.
pause
exit /b 1
