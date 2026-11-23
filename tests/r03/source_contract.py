#!/usr/bin/env python3
"""R03 offline exact-baseline diff and route contract check.

Usage: python tests/r03/source_contract.py --base /path/to/v5.0.88_Source --candidate .
"""
import argparse
import hashlib
from pathlib import Path


ALLOWED_EXISTING = {'dotnet/FilmGrain_NativeAv1Core.cs'}
ALLOWED_NEW = {
    'dotnet/BuildTools/Test_R03_Av1Fruc.ps1',
    '.github/workflows/r03-av1-fruc-windows.yml',
    'tests/r03/software_parity.py',
    'tests/r03/source_contract.py',
    'R03_CANDIDATE_INFO.txt',
}


def files(root):
    return {p.relative_to(root).as_posix(): p.read_bytes() for p in root.rglob('*') if p.is_file()}


def check(condition, explanation):
    if not condition:
        raise AssertionError(explanation)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--base', required=True)
    parser.add_argument('--candidate', required=True)
    opts = parser.parse_args()
    base, candidate = files(Path(opts.base)), files(Path(opts.candidate))
    modified = {name for name in base.keys() & candidate.keys() if base[name] != candidate[name]}
    added = set(candidate) - set(base)
    removed = set(base) - set(candidate)
    check(modified == ALLOWED_EXISTING, f'Unexpected modified files: {modified}')
    check(added == ALLOWED_NEW, f'Unexpected added files: {added}')
    check(not removed, f'Original files removed: {removed}')
    path='dotnet/FilmGrain_NativeAv1Core.cs'
    original, revised = base[path].decode('utf-8-sig'), candidate[path].decode('utf-8-sig')
    check(base[path].startswith(b'\xef\xbb\xbf') and candidate[path].startswith(b'\xef\xbb\xbf'), 'Original UTF-8 BOM changed')
    check(revised.count('\r\n') == original.count('\r\n')+16, 'Unexpected C# newline normalization or line-count drift')
    begin='        internal static bool SimpleSource('
    end='        internal static string Clock('
    check(original.split(begin,1)[1].split(end,1)[0] == revised.split(begin,1)[1].split(end,1)[0],
          'SimpleSource MOV proof/eligibility changed')
    required = (
        'bool simpleRoute=!interpolate && Av1RouteOptimizationCore.SimpleSource',
        'bool directOutput=simpleRoute && !interpolate && !grainEnabled;',
        'bool directFruc=fruc && Av1RouteOptimizationCore.FrucEncodeSource',
        'grainEnabled && !metadataGrain)',
        'if (fruc && hdrPreserve && !SharedFrucCore.TryPreflight10',
        'string frucFormat=hdrPreserve?"yuv420p10le":"yuv420p";',
        'SharedFrucCore.InputFilter(fpsText,frucPerf,frucGrid,hdrPreserve)',
        'format="+frucFormat+",settb=expr=1/1000,',
        'if (interpolate && !directFruc) request.TemporaryFiles.Add(frucWorkfile);',
        'if (fruc && !directFruc)',
        'SharedFrucCore.AttachAv1RemuxTimeline',
        'Stage(grav1synth',
        'Av1GrainStrengthCore.Stages',
        'SharedFrucCore.HdrNotice',
        'PreprocessingCore.Enabled(state) || ResizeCore.Enabled(state)',
        'Get(state,"FG_COLOR_ENABLED")=="1"',
        'Get(state,"FG_CINEMATIC_FRAME")=="1" || Get(state,"FG_SUBTITLE")=="1"',
        'info.IsInterlaced || sfe!=0 || hdrToSdr || pixelGrain',
    )
    for x in required:
        check(x in revised, f'Missing R03/protection contract {x}')
    check(b'\xef\xbb\xbf' == candidate['dotnet/BuildTools/Test_R03_Av1Fruc.ps1'][:3], 'PS1 UTF-8 BOM missing')
    check(not any(q in candidate['dotnet/BuildTools/Test_R03_Av1Fruc.ps1'].decode('utf-8-sig') for q in '“”‘’'),
          'PS1 includes forbidden curly quotes')
    check(b'runs-on: windows-2022' in candidate['.github/workflows/r03-av1-fruc-windows.yml'],
          'R03 Windows CI runner missing')
    check(b'branches:\n      - test/r03-av1-fruc' in candidate['.github/workflows/r03-av1-fruc-windows.yml'],
          'Unexpected CI branch trigger')
    print('PASS R03 source changes: one AV1 core only; four new test/CI files; no baseline removals')
    print('PASS SimpleSource body unchanged; HDR 10-bit/IVF/remux/workfile and protective gates retained')
    print('PASS PS1 BOM/quote policy; Windows 2022 CI file (not executed)')


if __name__ == '__main__':
    main()
