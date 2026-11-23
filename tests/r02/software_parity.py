#!/usr/bin/env python3
"""R02 six-frame FFmpeg software analogue. Not a Vulkan/NVENC acceptance test."""
import argparse
import itertools
from pathlib import Path
import struct
import subprocess
import tempfile
import zlib


def make_png(path, w=160, h=90):
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        for x in range(w):
            d = min(x, w-1-x, y, h-1-y)
            raw.extend((20, 18, 16, int(224*(1-d/8)) if d < 8 else 0))
    def chunk(typ, data):
        return struct.pack('>I', len(data)) + typ + data + struct.pack('>I', zlib.crc32(typ + data) & 0xffffffff)
    path.write_bytes(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(bytes(raw), 9)) + chunk(b'IEND', b''))


def run(argv):
    p = subprocess.run(argv, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    if p.returncode:
        raise RuntimeError('FFmpeg failed: ' + ' '.join(map(str, argv)) + '\n' + p.stderr[-3000:])
    return p.stdout


def source_input(ffmpeg):
    return [ffmpeg, '-hide_banner', '-nostdin', '-loglevel', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=160x90:rate=6:duration=1']


def post_input(path, ffmpeg, staged):
    if staged:
        args = [ffmpeg, '-hide_banner', '-nostdin', '-loglevel', 'error', '-f', 'rawvideo', '-pixel_format', 'yuv420p10le', '-video_size', '160x90', '-framerate', '6', '-i', str(path)]
    else:
        args = source_input(ffmpeg)
    return args + ['-f', 'lavfi', '-i', 'smptebars=size=160x90:rate=6:duration=1']


def frame_lines(result):
    lines = [line.strip() for line in result.splitlines() if line.startswith('0,')]
    if len(lines) != 6:
        raise AssertionError('Expected 6 complete frames, got ' + str(len(lines)))
    return lines


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--ffmpeg', default='ffmpeg')
    args = ap.parse_args()
    ffmpeg = args.ffmpeg
    with tempfile.TemporaryDirectory(prefix='FGS_R02_') as td:
        td = Path(td)
        image = td / 'alpha.png'
        make_png(image)
        count = 0
        for bilateral, plate, border in itertools.product((False, True), repeat=3):
            first = 'format=yuv420p10le,' + ('bilateral=sigmaS=3:sigmaR=0.05:planes=1,' if bilateral else '') + 'format=yuv420p10le'
            pre = td / ('pre_' + str(int(bilateral)) + '.yuv')
            run(source_input(ffmpeg) + ['-vf', first, '-pix_fmt', 'yuv420p10le', '-frames:v', '6', '-f', 'rawvideo', '-y', str(pre)])
            rows=[]
            for staged in (False, True):
                params = post_input(pre, ffmpeg, staged)
                if border:
                    params += ['-loop', '1', '-framerate', '6', '-i', str(image)]
                graph = '[0:v]' + ('' if staged else first+',') + 'null[base]'
                last = '[base]'
                if plate:
                    graph += ';[1:v]format=yuv420p10le[plate];[base][plate]blend=all_mode=overlay:all_opacity=0.35,format=yuv420p10le[blend]'
                    last='[blend]'
                if border:
                    graph += ';' + last + 'format=rgba[video];[2:v]format=rgba[border];[video][border]overlay=shortest=1:format=auto,format=yuv420p10le[vout]'
                else:
                    graph += ';' + last + 'format=yuv420p10le[vout]'
                cmd = params + ['-filter_complex', graph, '-map', '[vout]', '-frames:v', '6', '-f', 'framemd5', '-']
                rows.append(frame_lines(run(cmd)))
            if rows[0] != rows[1]:
                raise AssertionError('Software prefix parity fails: bilateral=' + str(bilateral) + ' plate=' + str(plate) + ' border=' + str(border))
            count += 1
            print('PASS software comparison: Bilateral=%d Plate=%d Border=%d 6/6 frames' % (bilateral, plate, border))
    print('PASS %d/8 software configurations: direct prefix vs frame-accurate raw intermediate' % count)
    print('NOTE: CPU analogue only; does not verify CUDA/Vulkan S4, alpha geometry or whole-task throughput')


if __name__ == '__main__':
    main()
