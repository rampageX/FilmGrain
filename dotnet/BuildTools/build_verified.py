"""Non-Windows prebuild: explicit Microsoft reference APIs + artifact audit gate."""
import argparse
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

TOOLS = Path(__file__).resolve().parent
ROOT = TOOLS.parent.parent
REFERENCES = ['mscorlib','System','System.Core','System.Drawing','System.Windows.Forms','System.Web.Extensions']


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--reference-dir',required=True,type=Path)
    parser.add_argument('--mono',required=True,type=Path)
    parser.add_argument('--compiler',required=True,type=Path)
    parser.add_argument('--mono-config',type=Path)
    args=parser.parse_args()
    ref=args.reference_dir.resolve()
    runtime=[str(args.mono.resolve())]
    if args.mono_config:
        runtime += ['--config',str(args.mono_config.resolve())]
    refs=[]
    for name in REFERENCES:
        path=ref/(name+'.dll')
        if not path.is_file():
            raise RuntimeError('Missing Microsoft Framework reference: '+str(path))
        refs.append('-r:'+str(path))
    compiler=runtime+[str(args.compiler.resolve()),'-langversion:5','-nostdlib','-optimize+']+refs
    # Keep candidates private until every audit succeeds; do not reuse old EXEs.
    with tempfile.TemporaryDirectory(prefix='FGS_Verified_') as temporary:
        stage=Path(temporary)
        auditor=stage/'FrameworkApiAudit.exe'
        shutil.copyfile(TOOLS/'Mono.Cecil.dll',stage/'Mono.Cecil.dll')
        subprocess.run(compiler+['-target:exe','-out:'+str(auditor),'-r:'+str(TOOLS/'Mono.Cecil.dll'),str(TOOLS/'FrameworkApiAudit.cs')],check=True)
        candidate=stage/'FilmGrain_Studio.exe'
        sources=[str(p) for p in sorted((ROOT/'dotnet').glob('*.cs'))]
        subprocess.run(compiler+['-target:winexe','-win32icon:'+str(ROOT/'Utils/FGS.ico'),'-out:'+str(candidate)]+sources,check=True)
        subprocess.run(runtime+[str(auditor),str(candidate),str(ref)],check=True)
        compatible=ROOT/'dotnet/build/FilmGrain_Studio_NET_Preview.exe'
        compatible.parent.mkdir(exist_ok=True)
        shutil.copyfile(candidate,compatible)
        shutil.copyfile(candidate,ROOT/'FilmGrain_Studio.exe')
        expected=hashlib.sha256(candidate.read_bytes()).hexdigest()
        for final in [compatible,ROOT/'FilmGrain_Studio.exe']:
            assert hashlib.sha256(final.read_bytes()).hexdigest()==expected
            subprocess.run(runtime+[str(auditor),str(final),str(ref)],check=True)
        print('VERIFIED: both EXEs match; SHA256='+expected)


if __name__=='__main__':
    main()
