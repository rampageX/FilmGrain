#!/usr/bin/env python3
"""R04b NUT -> FFV1/MKV vs direct NUT software frame/time contract.

No NVEncC/NVENC claim: generated NUT raw producer substitutes for the actual RTX AI.
Actual production uses NvencInterpolationCore.AttachProducer and AV1 encoder.
"""
import argparse
import subprocess
import tempfile
from pathlib import Path


def run(args, stdin=None):
    r = subprocess.run(args, input=stdin, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if r.returncode:
        raise RuntimeError(' '.join(map(str,args))+'\n'+r.stderr.decode('utf-8','replace')[-4000:])
    return r.stdout


def frame_digest(text):
    timebase=None
    out=[]
    for line in text.decode().splitlines():
        if line.startswith('#tb 0: '):
            timebase=line.split(': ',1)[1].strip()
        if line.startswith('#') or not line.strip():
            continue
        cols=[c.strip() for c in line.split(',')]
        if len(cols)<6:raise AssertionError('Unrecognized framemd5: '+line)
        out.append((int(cols[0]),int(cols[1]),int(cols[2]),int(cols[3]),cols[-1]))
    if not timebase:raise AssertionError('No framemd5 timebase')
    num,den=map(int,timebase.split('/'))
    return out,num/den


def seq_equal(a,b,variant):
    (aa,atb),(bb,btb)=a,b
    if len(aa)!=len(bb) or len(aa)!=20:
        raise AssertionError(f'{variant}: frame count differs: {len(aa)}/{len(bb)}')
    if [x[-1] for x in aa]!=[x[-1] for x in bb]:
        mismatch=next(i for i,(x,y) in enumerate(zip(aa,bb)) if x[-1]!=y[-1])
        raise AssertionError(f'{variant}: pixel MD5 differs at frame {mismatch}')
    def rel(rows,tb):
        return [(x[2]-rows[0][2])*tb for x in rows]
    ar,br=rel(aa,atb),rel(bb,btb)
    max_drift=max(abs(a-b) for a,b in zip(ar,br))
    if max_drift>0.0015:raise AssertionError(f'{variant}: relative PTS drift {max_drift:.6f} >1.5ms')
    for vals in (ar,br):
        if any(b<=a for a,b in zip(vals,vals[1:])):
            raise AssertionError(f'{variant}: nonmonotonic PTS')
    return max_drift,aa[0][2]*atb,bb[0][2]*btb


def test_case(ffmpeg,probe,work,fps,pix,hdr,offset,vfr):
    t='sdr' if not hdr else ('pq' if hdr=='smpte2084' else 'hlg')
    variant=f'{fps}_{pix}_{t}_offset{offset}_{"vfr" if vfr else "cfr"}'
    folder=work/variant.replace('/','-');folder.mkdir(parents=True)
    nut=folder/'producer.nut';legacy=folder/'old.mkv'
    fps_number=eval_fps(fps)
    pts='PTS+%s/TB'%offset if not vfr else 'PTS+floor(N/5)*0.04/TB+%s/TB'%offset
    color=[]
    if hdr:
        color=['-color_range','tv','-color_primaries','bt2020','-color_trc',hdr,'-colorspace','bt2020nc']
    source=f'testsrc2=size=160x96:rate={fps}:duration={20/fps_number},format={pix},setpts={pts}'
    run([ffmpeg,'-hide_banner','-loglevel','error','-y','-f','lavfi','-i',source,'-frames:v','20',
         '-an','-fps_mode','passthrough','-c:v','rawvideo','-pix_fmt',pix,*color,'-f','nut',str(nut)])
    legacyfilter=f'setparams=range=limited:color_primaries=bt2020:color_trc={hdr}:colorspace=bt2020nc' if hdr else 'null'
    run([ffmpeg,'-hide_banner','-loglevel','error','-y','-copyts','-itsoffset',str(offset),'-f','nut','-i',str(nut),
         '-vf',legacyfilter,'-an','-c:v','ffv1','-level','3','-pix_fmt',pix,'-enc_time_base:v','1/'+fps,
         '-fps_mode','passthrough',*color,str(legacy)])
    # The direct leg applies exactly the existing NVEncC producer adapter's
    # setpts=PTS-STARTPTS and HDR setparams before the common AV1 pixel format.
    directfilter='[0:v:0]setpts=PTS-STARTPTS,'
    if hdr:directfilter+=f'setparams=range=limited:color_primaries=bt2020:color_trc={hdr}:colorspace=bt2020nc,'
    directfilter+='format=p010le[vout]'
    # Compare the actual *video frames entering the AV1 encoder*, never write
    # another FFV1 file on the direct side. Framemd5 keeps timebase and PTS.
    decoded=[]
    variants=[(str(legacy),'matroska','[0:v:0]format=p010le[vout]'),
              (str(nut),'nut',directfilter)]
    timebase='1/'+fps if '/' not in fps else fps.split('/')[1]+'/'+fps.split('/')[0]
    for name,demux,filters in variants:
        stdin=nut.read_bytes() if name==str(nut) else None
        source='pipe:0' if stdin is not None else name
        out=run([ffmpeg,'-hide_banner','-loglevel','error','-copyts','-f',demux,'-i',source,
                 '-filter_complex',filters,'-map','[vout]','-an',
                 '-enc_time_base:v',timebase,'-fps_mode:v','passthrough','-f','framemd5','-'],stdin=stdin)
        decoded.append(frame_digest(out))
    drift,oldstart,newstart=seq_equal(decoded[0],decoded[1],variant)
    print(f'PASS {variant}: 20/20 pixel hashes, relative PTS drift {drift*1000:.2f}ms, start old={oldstart:.3f} new={newstart:.3f}')


def eval_fps(s):
    if '/' in s:
        a,b=s.split('/');return float(a)/float(b)
    return float(s)


def main():
    p=argparse.ArgumentParser();p.add_argument('--ffmpeg',default='ffmpeg');p.add_argument('--ffprobe',default='ffprobe');args=p.parse_args()
    for binary in (args.ffmpeg,args.ffprobe):
        run([binary,'-version'])
    with tempfile.TemporaryDirectory(prefix='fgs-r04b-') as d:
        cases=[('50','yuv420p',None,0,False),('60000/1001','yuv420p',None,.32,True),
               ('50','yuv420p10le','smpte2084',0,False),('60000/1001','yuv420p10le','smpte2084',.32,True),
               ('50','yuv420p10le','arib-std-b67',0,True),('60000/1001','yuv420p10le','arib-std-b67',.32,False)]
        for f,pix,hdr,offset,vfr in cases:test_case(args.ffmpeg,args.ffprobe,Path(d),f,pix,hdr,offset,vfr)
    print(f'PASS R04b software NUT/FFV1 parity {len(cases)}/{len(cases)} variants, not a real NVIDIA encode')

if __name__=='__main__':main()
