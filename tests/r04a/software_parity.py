#!/usr/bin/env python3
"""R04a software-only verification of the OpenSVPFlow Y4M -> AV1 boundary.

Compares actual Y4M byte stream fed to a legacy FFV1/MKV intermediate and
fed directly to an encoder-side filter graph. No GPU/VapourSynth claims.
"""
import hashlib
import subprocess
import tempfile
from pathlib import Path

FFMPEG = 'ffmpeg'


def run(args, input_data=None):
    proc = subprocess.run([FFMPEG, '-hide_banner', '-loglevel', 'error', '-nostdin' if input_data is None else '-stdin', *args], input=input_data,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=80)
    if proc.returncode:
        raise RuntimeError('ffmpeg failed: '+ ' '.join(map(str,args))+'\n'+proc.stderr.decode('utf-8','replace'))
    return proc.stdout


def framemd5(source, fps, delay, is_pipe):
    args=['-copyts']
    if is_pipe:
        args+=['-f','yuv4mpegpipe','-i','pipe:0']
    else:
        args+=['-i',str(source)]
    graph=f'[0:v:0]format=p010le,settb=expr={fps[1]}/{fps[0]},setpts=N,fps=fps={fps[0]}/{fps[1]}:round=near:eof_action=pass[vout]'
    args+=['-filter_complex',graph,'-map','[vout]','-an','-sn','-dn','-fps_mode:v','passthrough','-f','framemd5','pipe:1']
    return run(args,source if is_pipe else None)


def main():
    passed=0
    with tempfile.TemporaryDirectory(prefix='fgs_r04a_') as d:
        for n,dn in ((50,1),(60000,1001)):
            fps=(n,dn)
            for depth in (8,10):
                y4m=run(['-f','lavfi','-i',f'testsrc2=size=128x72:rate={n}/{dn}',
                         '-frames:v','30','-vf',f'format=yuv420p{depth if depth==10 else ""}le' if depth==10 else 'format=yuv420p',
                         '-strict','-1','-f','yuv4mpegpipe','pipe:1'])
                for delay in (0,0.32):
                    old=Path(d)/f'legacy_{n}_{dn}_{depth}_{delay}.mkv'
                    oldargs=['-copyts','-itsoffset',str(delay),'-f','yuv4mpegpipe','-i','pipe:0','-map','0:v:0',
                             '-an','-sn','-dn','-c:v','ffv1','-level','3',
                             '-pix_fmt','yuv420p10le' if depth==10 else 'yuv420p',
                             '-enc_time_base:v',f'{dn}/{n}','-fps_mode','passthrough',str(old)]
                    run(oldargs,y4m)
                    expected=framemd5(old,fps,delay,False)
                    actual=framemd5(y4m,fps,delay,True)
                    if expected!=actual:
                        e=expected.decode(errors='replace').splitlines()
                        a=actual.decode(errors='replace').splitlines()
                        for ix,(x,y) in enumerate(zip(e,a)):
                            if x!=y:
                                raise AssertionError(f'failed fps={fps} depth={depth} delay={delay} line={ix} legacy={x!r} direct={y!r}')
                        raise AssertionError(f'frame/PTS stream length mismatch {len(e)}/{len(a)}')
                    frames=[line for line in actual.decode().splitlines() if not line.startswith('#')]
                    assert len(frames)==30,(fps,depth,delay,len(frames))
                    print(f'PASS {n}/{dn}  {depth}-bit  offset={delay:.2f}s  frames=30  sha256={hashlib.sha256(actual).hexdigest()[:16]}')
                    passed+=1
    assert passed==8
    print('PASS R04a 8/8 software frame MD5, ordering and PTS comparisons. GPU, HDR OBU, remux and vspipe remain untested.')

if __name__=='__main__': main()
