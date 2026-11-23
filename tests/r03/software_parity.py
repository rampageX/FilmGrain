#!/usr/bin/env python3
"""R03 software boundary surrogate, not a FRUC Vulkan or AV1 NVENC test.

Exercises FFV1/MKV versus inline 1/1000 time-base boundaries on real CFR MP4,
B-frame MP4, same-bitstream MKV, offset audio/video and P010-tagged HDR input.
All generated samples and reports live in an isolated output directory.
"""
import argparse
import json
import subprocess
from pathlib import Path


def run(args):
    p = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    if p.returncode:
        raise RuntimeError(' '.join(map(str, args)) + '\n' + p.stderr[-5000:])
    return p.stdout


def ffmpeg(*args):
    return run(['ffmpeg', '-hide_banner', '-nostdin', '-v', 'error', '-threads', '2', '-y', *map(str, args)])


def ffprobe(path):
    return json.loads(run(['ffprobe', '-v', 'error', '-show_streams', '-show_format', '-of', 'json', str(path)]))


def streams(path):
    return {x['codec_type']: x for x in ffprobe(path)['streams'] if x['codec_type'] in ('video', 'audio')}


def mux_source(path, bf, video_offset=0, audio_offset=0):
    args = []
    if video_offset:
        args += ['-itsoffset', str(video_offset)]
    args += ['-f', 'lavfi', '-i', 'testsrc2=size=160x90:rate=25:duration=2']
    if audio_offset:
        args += ['-itsoffset', str(audio_offset)]
    args += ['-f', 'lavfi', '-i', 'sine=frequency=725:sample_rate=48000:duration=2',
             '-map', '0:v:0', '-map', '1:a:0', '-c:v', 'libx264', '-preset', 'ultrafast',
             '-g', '50', '-bf', str(bf), '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '96k', path]
    ffmpeg(*args)


def make_hdr(path):
    ffmpeg('-f', 'lavfi', '-i', 'testsrc2=size=160x90:rate=25:duration=2',
           '-f', 'lavfi', '-i', 'sine=frequency=600:sample_rate=48000:duration=2',
           '-map', '0:v:0', '-map', '1:a:0', '-vf',
           'format=yuv420p10le,setparams=range=limited:color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc',
           '-c:v', 'ffv1', '-level', '3', '-pix_fmt', 'yuv420p10le',
           '-color_primaries', 'bt2020', '-color_trc', 'smpte2084',
           '-colorspace', 'bt2020nc', '-c:a', 'flac', path)


def video_hash(path):
    return run(['ffmpeg', '-hide_banner', '-v', 'error', '-i', str(path), '-map', '0:v:0',
                '-c:v', 'copy', '-f', 'streamhash', '-hash', 'SHA256', '-'])


def frame_hash(path, pixel):
    return run(['ffmpeg', '-hide_banner', '-v', 'error', '-i', str(path), '-map', '0:v:0',
                '-vf', 'format=p010le',
                '-fps_mode', 'passthrough', '-f', 'framemd5', '-'])


def normalize_hash(md5):
    # Frame content is compared; output muxer time bases are inspected separately.
    return [line.split(',', 5)[-1].strip() for line in md5.splitlines() if line and not line.startswith('#')]


def frames(path):
    data = json.loads(run(['ffprobe', '-v', 'error', '-select_streams', 'v:0',
                           '-show_frames', '-show_entries', 'frame=pts_time,pkt_duration_time',
                           '-of', 'json', str(path)]))
    return [float(f['pts_time']) for f in data['frames'] if 'pts_time' in f]


def audio_pts(path):
    data=json.loads(run(['ffprobe','-v','error','-select_streams','a:0',
                         '-show_packets','-show_entries','packet=pts_time',
                         '-of','json',str(path)]))
    return [float(f['pts_time']) for f in data['packets'] if 'pts_time' in f]


def compare(case, src, work, rate, bitdepth):
    pix = 'yuv420p10le' if bitdepth == 10 else 'yuv420p'
    # The software fps operation is deliberately used as a surrogate for the
    # GPU optical flow stage, but both sides run exactly the same upstream work.
    common = f'setpts=PTS-STARTPTS,fps=fps={rate}:round=near:eof_action=pass,format={pix}'
    ffmpeg('-i', src, '-vf', common, '-map', '0:v:0', '-an', '-c:v', 'ffv1',
           '-level', '3', '-pix_fmt', pix, '-fps_mode', 'passthrough', work / 'old.mkv')
    ffmpeg('-i', src, '-vf', common + ',settb=expr=1/1000', '-map', '0:v:0',
           '-an', '-c:v', 'ffv1', '-level', '3', '-pix_fmt', pix, '-fps_mode', 'passthrough', work / 'direct.mkv')
    old = work / 'old.mkv'
    direct = work / 'direct.mkv'
    a = normalize_hash(frame_hash(old, pix))
    b = normalize_hash(frame_hash(direct, pix))
    if a != b:
        raise AssertionError(f'{case}: frame pixels differ, old={len(a)}, direct={len(b)}')
    at, bt = frames(old), frames(direct)
    if len(at) != len(bt) or max([abs(x-y) for x, y in zip(at, bt)], default=0) > 0.00101:
        raise AssertionError(f'{case}: frame PTS mismatch, {at[:5]} vs {bt[:5]}')
    fmt = streams(src)
    return dict(case=case, original_video_start=fmt['video'].get('start_time'),
                original_audio_start=fmt.get('audio', {}).get('start_time'),
                frames=len(a), source_last_pts=frames(src)[-1],
                old_last_pts=at[-1], direct_last_pts=bt[-1],
                max_pts_diff=round(max((abs(x-y) for x, y in zip(at, bt)), default=0), 6),
                format=fmt['video']['pix_fmt'], old_pixfmt=streams(old)['video']['pix_fmt'],
                direct_pixfmt=streams(direct)['video']['pix_fmt'])


def main():
    ap=argparse.ArgumentParser()
    ap.add_argument('--output', default='r03_software_evidence')
    ns=ap.parse_args()
    root=Path(ns.output).resolve();root.mkdir(parents=True,exist_ok=True)
    a=root/'cfr_no_b.mp4';b=root/'cfr_b.mp4';c=root/'cfr_b.mkv';v=root/'video_offset.mkv';aud=root/'audio_offset.mp4';h=root/'hdr10_p010_contract.mkv'
    mux_source(a,0)
    mux_source(b,3)
    ffmpeg('-i',b,'-map','0','-c','copy',c)
    assert video_hash(b)==video_hash(c),'MP4 vs MKV coded video payload changed'
    mux_source(v,3,video_offset=0.32)
    mux_source(aud,3,audio_offset=0.32)
    make_hdr(h)
    assert streams(h)['video']['pix_fmt']=='yuv420p10le'
    assert streams(h)['video'].get('color_transfer')=='smpte2084', 'HDR signal not present in fixture'
    probes={p.name:streams(p) for p in (a,b,c,v,aud,h)}
    assert float(probes[v.name]['video']['start_time']) > float(probes[v.name]['audio']['start_time']) + 0.1
    assert float(probes[aud.name]['audio']['start_time']) > float(probes[aud.name]['video']['start_time']) + 0.1
    results=[]
    for label,path,rate,depth in [('no_b_mp4',a,'50',8),('b_mp4',b,'50',8),('same_stream_mkv',c,'50',8),
                                  ('video_origin_mkv',v,'50',8),('audio_origin_mp4',aud,'60000/1001',8),
                                  ('pq10_ffv1',h,'50',10)]:
        wd=root/label;wd.mkdir(exist_ok=True)
        item=compare(label,path,wd,rate,depth)
        results.append(item)
        print('PASS',label,item['frames'],'frames',rate,'fps','PTS delta <=',item['max_pts_diff'])
    # Exercise the original two-input IVF/remux clock independently of route selection.
    # Use libaom solely to generate an actual AV1 IVF stream on this Linux host.
    mux_results=[]
    for label,path,ext in [('video_origin_mkv',v,'mkv'),('audio_origin_mp4',aud,'mp4'),('same_stream_mkv',c,'mkv')]:
        wd=root/label
        ivf=wd/'av1_clean.ivf'
        final=wd/('av1_remux.'+ext)
        ffmpeg('-i',wd/'old.mkv','-an','-c:v','libaom-av1','-cpu-used','8','-threads','4',
               '-row-mt','1','-b:v','0','-crf','42','-f','ivf',ivf)
        source_starts={k:float(x.get('start_time','0')) for k,x in streams(path).items()}
        coded_starts={k:float(x.get('start_time','0')) for k,x in streams(ivf).items()}
        common=min(source_starts.values())
        video_delay=source_starts['video']-common
        args=['-copyts','-itsoffset',str(video_delay-coded_starts['video']),'-i',ivf,
              '-itsoffset',str(-common),'-i',path,'-map','0:v:0','-map','1:a?',
              '-map_metadata','1','-map_chapters','1','-c:v','copy',
              '-c:a','copy' if ext=='mkv' else 'aac']
        if ext=='mp4': args += ['-b:a','128k']
        ffmpeg(*args,final)
        output_starts={k:float(x.get('start_time','0')) for k,x in streams(final).items()}
        for k in ('video','audio'):
            expected=source_starts[k]-common
            if abs(output_starts[k]-expected) > 0.045:
                raise AssertionError(f'{label} {k} origin expected {expected} got {output_starts[k]}')
        if streams(final)['video']['codec_name']!='av1':
            raise AssertionError('Final remux is not AV1')
        coded_video_pts=frames(ivf)
        mux_video_pts=frames(final)
        if len(coded_video_pts)!=len(mux_video_pts) or abs((coded_video_pts[-1]-coded_video_pts[0])-(mux_video_pts[-1]-mux_video_pts[0]))>0.035:
            raise AssertionError(f'{label} video frame count or last-frame duration changed during remux')
        source_audio_pts=audio_pts(path)
        output_audio_pts=audio_pts(final)
        if not source_audio_pts or not output_audio_pts or abs((source_audio_pts[-1]-common)-output_audio_pts[-1])>0.045:
            raise AssertionError(f'{label} audio last packet moved during remux')
        mux_results.append({'case':label,'source':source_starts,'remux':output_starts,'video_codec':'av1',
                            'video_frames':len(mux_video_pts), 'video_tail':mux_video_pts[-1],
                            'audio_tail':output_audio_pts[-1]})
        print('PASS IVF/remux',label,'origins',output_starts,'last_video',mux_video_pts[-1],'last_audio',output_audio_pts[-1])
    # Actual software AV1 Main10 IVF + final MKV mux checks only color tags and bit depth.
    # NVIDIA static metadata OBU injection remains outside this test's capability.
    pq_dir=root/'pq10_ffv1'
    pq_ivf=pq_dir/'pq10_av1.ivf'
    pq_mux=pq_dir/'pq10_remux.mkv'
    ffmpeg('-i',pq_dir/'old.mkv','-an','-c:v','libaom-av1','-cpu-used','8',
           '-threads','4','-row-mt','1','-b:v','0','-crf','42',
           '-pix_fmt','yuv420p10le','-color_primaries','bt2020',
           '-color_trc','smpte2084','-colorspace','bt2020nc','-f','ivf',pq_ivf)
    ffmpeg('-copyts','-itsoffset','0','-i',pq_ivf,'-itsoffset','0','-i',h,
           '-map','0:v:0','-map','1:a?','-map_metadata','1','-map_chapters','1',
           '-c:v','copy','-c:a','copy','-color_primaries','bt2020',
           '-color_trc','smpte2084','-colorspace','bt2020nc',pq_mux)
    pq=streams(pq_mux)['video']
    assert pq['codec_name']=='av1' and pq['pix_fmt']=='yuv420p10le'
    assert (pq['color_primaries'],pq['color_transfer'],pq['color_space'])==('bt2020','smpte2084','bt2020nc')
    assert 'audio' in streams(pq_mux)
    mux_results.append({'case':'pq10_ivf_remux','signals':[pq['color_primaries'],pq['color_transfer'],pq['color_space']],
                        'bit_depth':10,'audio_kept':True})
    print('PASS P010/AV1 IVF/MKV bit-depth, PQ/BT2020 signaling and audio presence')
    (root/'report.json').write_text(json.dumps({'software_parity':results,'av1_remux':mux_results},indent=2,ensure_ascii=False),encoding='utf-8')
    print('PASS 6 software pixel/timebase comparisons; 3 A/V-origin AV1 remux cases; 1 PQ10 AV1 IVF/remux')
    print('NOTE does not test GPU FRUC, NVENC, grav1synth or NVIDIA source HDR10 OBU restoration')

if __name__=='__main__': main()
