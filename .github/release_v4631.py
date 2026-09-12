from pathlib import Path
import hashlib
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile

ROOT = Path.cwd()
VERSION = 'v4.6.3.1'
ZIP_NAME = 'FilmGrain_Studio_v4.6.3.1_Stable.zip'
SHA_NAME = 'FilmGrain_Studio_v4.6.3.1_Stable.sha256'
SCRIPT_EXTS = {'.bat', '.vbs', '.cmd', '.ps1'}


def run(*args, cwd=None):
    print('+', ' '.join(str(x) for x in args), flush=True)
    subprocess.run([str(x) for x in args], cwd=cwd, check=True)


def read_text(path):
    return Path(path).read_text(encoding='utf-8-sig')


def write_text(path, text, bom=False):
    enc = 'utf-8-sig' if bom else 'utf-8'
    with open(path, 'w', encoding=enc, newline='\r\n') as f:
        f.write(text.replace('\r\n', '\n').replace('\r', '\n'))


def line_counts(data: bytes):
    lf = data.count(b'\n')
    crlf = data.count(b'\r\n')
    return lf, crlf


def has_utf8_bom(data: bytes):
    return data.startswith(b'\xef\xbb\xbf')


def audit_script_tree(root: Path, final_gate=False):
    issues = []
    files = sorted(p for p in root.rglob('*') if p.is_file() and p.suffix.lower() in SCRIPT_EXTS)
    for p in files:
        rel = p.relative_to(root).as_posix()
        data = p.read_bytes()
        ext = p.suffix.lower()
        lf, crlf = line_counts(data)
        bom = has_utf8_bom(data)
        text = data.decode('utf-8-sig', errors='strict')

        if ext in {'.bat', '.vbs', '.cmd'}:
            if bom:
                issues.append(f'BOM not allowed: {rel}')
            if lf != crlf:
                issues.append(f'Non-CRLF: {rel} LF={lf} CRLF={crlf}')
        elif ext == '.ps1':
            if not bom:
                issues.append(f'PS1 BOM missing: {rel}')
            if lf != crlf:
                issues.append(f'PS1 non-CRLF: {rel} LF={lf} CRLF={crlf}')
            if any(ch in text for ch in '\u201c\u201d\u2018\u2019'):
                issues.append(f'PS1 curly quote: {rel}')

        if final_gate and ext == '.bat':
            lines = text.splitlines()
            first = next((x.strip().lower() for x in lines if x.strip()), '')
            if first != '@echo off':
                issues.append(f'BAT first non-empty line is not @echo off: {rel}')
            for idx, line in enumerate(lines, 1):
                if re.search(r'\^[ \t]+$', line):
                    issues.append(f'BAT caret trailing whitespace: {rel} line {idx}')

            labels = {}
            for line in lines:
                m = re.match(r'^:([A-Za-z0-9_]+)\s*$', line)
                if m:
                    labels[m.group(1).upper()] = True

            for line in lines:
                m = re.match(r'^\s*goto\s+:?([A-Za-z0-9_]+)\s*$', line, re.I)
                if m:
                    target = m.group(1).upper()
                    if target != 'EOF' and target not in labels:
                        issues.append(f'Missing GOTO label {target}: {rel}')
                m = re.match(r'^\s*call\s+:([A-Za-z0-9_]+)(?:\s|$)', line, re.I)
                if m:
                    target = m.group(1).upper()
                    if target not in labels:
                        issues.append(f'Missing CALL label {target}: {rel}')
    return files, issues


def normalize_stage_scripts(stage: Path):
    for p in stage.rglob('*'):
        if not p.is_file():
            continue
        ext = p.suffix.lower()
        if ext not in SCRIPT_EXTS:
            continue
        text = p.read_text(encoding='utf-8-sig')
        text = text.replace('\r\n', '\n').replace('\r', '\n')
        if ext == '.ps1':
            with open(p, 'w', encoding='utf-8-sig', newline='\r\n') as f:
                f.write(text)
        else:
            with open(p, 'w', encoding='utf-8', newline='\r\n') as f:
                f.write(text)


def update_metadata():
    studio_path = ROOT / 'Utils' / 'FilmGrain_Studio.ps1'
    studio = read_text(studio_path)
    old = "statusVersion.Text = 'v4.6.3'"
    new = "statusVersion.Text = 'v4.6.3.1'"
    if old not in studio:
        raise RuntimeError('Expected v4.6.3 Studio version marker not found')
    studio = studio.replace(old, new, 1)
    write_text(studio_path, studio, bom=True)

    readme_path = ROOT / 'README.md'
    readme = read_text(readme_path)
    if '![](images/Film_Grain_Studio.jpg)' not in readme:
        raise RuntimeError('README GUI screenshot line missing')
    readme = readme.replace('当前正式稳定版为 **v4.6.3**', '当前正式稳定版为 **v4.6.3.1**', 1)
    readme = readme.replace('FilmGrain_Studio_v4.6.3_Stable.zip', 'FilmGrain_Studio_v4.6.3.1_Stable.zip', 1)
    note = ('\n\nv4.6.3.1 为 Windows 打包兼容性修正版：v4.6.3 功能与编码参数完全不变；正式发布固定使用 Windows runner。'
            '发布包中的 BAT/VBS/CMD 统一为 CRLF + 无 BOM，PS1 统一为 UTF-8 BOM + CRLF，并在最终 ZIP 解压后再次全量验证。'
            '该修复解决 Linux runner 打包后 Windows CMD 可能出现 BAT 标签实际存在却无法 `goto/call` 的问题。\n')
    marker = '\n默认配置仍为'
    if marker not in readme:
        raise RuntimeError('README insertion marker missing')
    readme = readme.replace(marker, note + marker, 1)
    write_text(readme_path, readme, bom=False)

    cl_path = ROOT / 'CHANGELOG.md'
    cl = read_text(cl_path)
    header = '# Film Grain Studio — CHANGELOG\n\n'
    if not cl.startswith(header):
        raise RuntimeError('CHANGELOG header mismatch')
    entry = (
        '## v4.6.3.1 — 2026-09-12\n\n'
        '- 修复 v4.6.3 正式 ZIP 由 Linux/Ubuntu runner 打包后，Windows 脚本换行格式不稳定的问题；该问题可导致 CMD 出现标签明明存在却提示 `The system cannot find the batch label specified`。\n'
        '- v4.6.3.1 不修改 AV1 / HEVC / x264、LUT Gallery、HDR Preserve、Grain、OpenSVPFlow、字幕、码率、AAC 256k 或其它编码参数；功能内容与已验证的 v4.6.3 完全一致。\n'
        '- 正式 FGS Release 固定使用 Windows Server runner；最终 ZIP 内 BAT/VBS/CMD 强制 CRLF + 无 BOM，PS1 强制 UTF-8 BOM + CRLF。\n'
        '- 发布门禁对最终 ZIP 解压后的全部 BAT/VBS/CMD/PS1 检查换行、BOM、PS1 中文弯引号、BAT `^` 行尾空格，以及静态 `goto/call :label` 目标。\n'
        '- AV1 + LUT 路线已使用 Windows CRLF 测试包 M8Q4X 完成用户侧实际验证。\n\n'
    )
    cl = header + entry + cl[len(header):]
    write_text(cl_path, cl, bom=False)

    for name in ('README_FilmGrain_Studio.txt', 'README_Toolkit.txt'):
        p = ROOT / name
        text = read_text(p)
        text = text.replace('当前正式稳定版：v4.6.3', '当前正式稳定版：v4.6.3.1', 1)
        write_text(p, text, bom=True)

    stable = (
        'Film Grain Studio formal stable baseline\n\n'
        'Baseline:\nv4.6.3.1\n\n'
        'Supersedes:\nv4.6.3\n\n'
        'Formal release:\nFilmGrain_Studio_v4.6.3.1_Stable.zip\n\n'
        'v4.6.3.1 packaging compatibility fix:\n'
        '- Functional source and encoding behavior are unchanged from user-validated v4.6.3.\n'
        '- Formal FGS release packaging is fixed to a Windows runner.\n'
        '- BAT / VBS / CMD are normalized to CRLF with no BOM in the release package.\n'
        '- PS1 is normalized to UTF-8 BOM + CRLF in the release package.\n'
        '- Final ZIP is unpacked and revalidated before release creation.\n'
        '- AV1 + LUT path was user-tested successfully with Windows CRLF package M8Q4X.\n\n'
        'Inherited v4.6.3:\n'
        '- LUT Gallery Update Preview synchronization and safe stale-preview cleanup.\n'
        '- Added / deleted / current LUT counts refresh immediately.\n'
        '- Final R6T2B Gallery layout and Smart Filter count display.\n'
        '- v4.6.2.1 H.264 upload manual bitrate fix.\n'
        '- v4.6.2 HDR Preserve and LUT Smart Filter.\n'
        '- v4.6.1 FGS icon and OpenSVPFlow updater.\n'
        '- v4.6.0 AV1 SFE and interlaced/OpenSVPFlow routing.\n\n'
        'Release cleanup:\n'
        '- No temporary patch / hotfix / test files.\n'
        '- No Utils\\_HardwareCaps.json.\n'
        '- No _LUT_Tools\\LUT_Reference_Current.jpg.\n'
        '- No user OpenSVPFlow DLL/version state or _PluginBackup.\n'
        '- No Recent / Favorites / Smart Filter CSV user state.\n'
    )
    write_text(ROOT / 'STABLE_BASELINE.txt', stable, bom=True)


def copy_stage(stage: Path):
    def ignore(directory, names):
        ignored = set()
        if Path(directory) == ROOT:
            ignored.update({'.git', '.github'})
        return ignored
    for item in ROOT.iterdir():
        if item.name in {'.git', '.github', ZIP_NAME, SHA_NAME}:
            continue
        dst = stage / item.name
        if item.is_dir():
            shutil.copytree(item, dst)
        else:
            shutil.copy2(item, dst)

    cleanup = [
        stage / 'Utils' / '_HardwareCaps.json',
        stage / '_LUT_Tools' / 'LUT_Reference_Current.jpg',
        stage / '_OpenSVPFlow' / 'Plugins' / 'open-svpflow-version.txt',
    ]
    for p in cleanup:
        if p.exists():
            p.unlink()
    backup = stage / '_OpenSVPFlow' / '_PluginBackup'
    if backup.exists():
        shutil.rmtree(backup)
    for name in ('_LUT_GALLERY_RECENT.json', '_LUT_GALLERY_FAVORITES.json', '_LUT_SMART_FILTER_REPORT.csv'):
        for p in stage.rglob(name):
            p.unlink(missing_ok=True)


def make_zip(stage: Path, out_zip: Path):
    with zipfile.ZipFile(out_zip, 'w', compression=zipfile.ZIP_DEFLATED) as z:
        for p in sorted(stage.rglob('*')):
            if p.is_file():
                z.write(p, p.relative_to(stage))


def main():
    if os.name != 'nt':
        raise RuntimeError('Formal v4.6.3.1 release must run on Windows')

    # Audit the already-published v4.6.3 asset to understand scope.
    audit_dir = Path(tempfile.mkdtemp(prefix='fgs_audit_v463_'))
    run('gh', 'release', 'download', 'v4.6.3', '--pattern', 'FilmGrain_Studio_v4.6.3_Stable.zip', '-D', str(audit_dir))
    bad_zip = audit_dir / 'FilmGrain_Studio_v4.6.3_Stable.zip'
    bad_unpack = audit_dir / 'unpack'
    with zipfile.ZipFile(bad_zip) as z:
        z.extractall(bad_unpack)
    bad_files, bad_issues = audit_script_tree(bad_unpack, final_gate=False)
    print(f'v4.6.3 published package scripts audited: {len(bad_files)}')
    print(f'v4.6.3 packaging issues found: {len(bad_issues)}')
    for issue in bad_issues:
        print('  ISSUE:', issue)

    # Prepare source metadata only; functional scripts remain v4.6.3 behavior.
    update_metadata()

    run('git', 'config', 'user.name', 'github-actions[bot]')
    run('git', 'config', 'user.email', '41898282+github-actions[bot]@users.noreply.github.com')
    run('git', 'add', 'Utils/FilmGrain_Studio.ps1', 'README.md', 'CHANGELOG.md', 'README_FilmGrain_Studio.txt', 'README_Toolkit.txt', 'STABLE_BASELINE.txt')
    run('git', 'commit', '-m', 'release: Film Grain Studio v4.6.3.1')
    run('git', 'push', 'origin', 'HEAD:master')
    run('git', 'tag', VERSION)
    run('git', 'push', 'origin', VERSION)

    # Stage/package strictly on Windows and normalize every Windows script.
    stage = Path(tempfile.mkdtemp(prefix='fgs_v4631_stage_'))
    copy_stage(stage)
    normalize_stage_scripts(stage)

    out_zip = ROOT / ZIP_NAME
    make_zip(stage, out_zip)
    sha = hashlib.sha256(out_zip.read_bytes()).hexdigest()
    (ROOT / SHA_NAME).write_text(f'{sha}  {ZIP_NAME}\n', encoding='ascii')
    print('RELEASE_SHA256=' + sha)

    # Validate the actual final ZIP, not just the staging tree.
    verify = Path(tempfile.mkdtemp(prefix='fgs_v4631_verify_'))
    with zipfile.ZipFile(out_zip) as z:
        z.extractall(verify)
    files, issues = audit_script_tree(verify, final_gate=True)
    print(f'Final ZIP scripts checked: {len(files)}')
    if issues:
        for issue in issues:
            print('FINAL ZIP ERROR:', issue)
        raise RuntimeError(f'Final ZIP validation failed with {len(issues)} issue(s)')
    print('FINAL ZIP SCRIPT VALIDATION: PASS')

    # Extra critical label guard for the regression that triggered this patch.
    bridge = (verify / 'Utils' / 'FilmGrain_Universal_HEVC_AV1_StudioBridge.bat').read_text(encoding='utf-8')
    if not re.search(r'(?m)^:RUN_LUT_AV1_DONE\s*$', bridge):
        raise RuntimeError('RUN_LUT_AV1_DONE label missing in final ZIP')
    if not re.search(r'(?m)^goto RUN_LUT_AV1_DONE\s*$', bridge):
        raise RuntimeError('goto RUN_LUT_AV1_DONE missing in final ZIP')

    body = '''# Film Grain Studio v4.6.3.1

Windows packaging compatibility release. Functional behavior is unchanged from v4.6.3.

## Fixed
- Formal FGS release packaging is now fixed to a Windows Server runner.
- BAT / VBS / CMD files in the final ZIP are normalized to CRLF with no BOM.
- PS1 files in the final ZIP are normalized to UTF-8 BOM + CRLF.
- Fixes Windows CMD cases where a BAT label exists in source but `goto/call` can still report `The system cannot find the batch label specified` after Linux-built packaging.

## Release validation
- Every BAT / VBS / CMD / PS1 in the actual final ZIP is checked after re-extraction.
- BAT static `goto` and `call :label` targets are verified.
- BAT caret-continuation trailing whitespace is rejected.
- PS1 Chinese curly quotes are rejected.
- AV1 + LUT was user-tested successfully with Windows CRLF TEST M8Q4X before this release.

No AV1 / HEVC / x264 encoding parameters, LUT Gallery behavior, HDR, Grain, OpenSVPFlow, subtitles, bitrate logic or AAC settings changed from v4.6.3.
'''
    run('gh', 'release', 'create', VERSION, ZIP_NAME, SHA_NAME, '--title', 'Film Grain Studio v4.6.3.1', '--notes', body)


if __name__ == '__main__':
    main()
