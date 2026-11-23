#!/usr/bin/env python3
"""FGS R01 lossless filtergraph clock/pixel comparison (not a full FGS task benchmark).

CPU mode is a timing-only surrogate. Vulkan mode requires the actual GPU/FFmpeg.
"""
import argparse
import itertools
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time


def command(exe, args, log):
    start = time.perf_counter()
    p = subprocess.run([str(exe)] + [str(x) for x in args], stdout=subprocess.PIPE,
                       stderr=subprocess.STDOUT, text=True, errors="replace")
    elapsed = time.perf_counter() - start
    with open(log, "a", encoding="utf-8") as f:
        f.write("$ " + json.dumps([str(exe)] + list(map(str, args)), ensure_ascii=False) + "\n")
        f.write(p.stdout + f"\nEXIT={p.returncode} SECONDS={elapsed:.6f}\n\n")
    if p.returncode:
        raise RuntimeError(f"FFmpeg returned {p.returncode}; see {log}")
    return elapsed, p.stdout


def probe(ffprobe, path):
    p = subprocess.run([str(ffprobe), "-v", "error", "-show_streams", "-show_format",
                        "-of", "json", str(path)], capture_output=True, text=True)
    if p.returncode:
        raise RuntimeError("ffprobe failed for " + str(path) + ": " + p.stderr)
    return json.loads(p.stdout)


def signature(ffmpeg, path, log):
    _, s = command(ffmpeg, ["-hide_banner", "-v", "error", "-i", path,
                            "-map", "0:v:0", "-pix_fmt", "yuv420p10le", "-f", "framemd5", "-"], log)
    rows = [line.split(",") for line in s.splitlines() if line and not line.startswith("#")]
    return [x[-1].strip() for x in rows], [int(x[2].strip()) for x in rows]


def streams(report):
    def start(stream):
        try:
            return float(stream.get("start_time", "0"))
        except ValueError:
            return 0.0
    video = next(s for s in report["streams"] if s["codec_type"] == "video")
    audio = next((s for s in report["streams"] if s["codec_type"] == "audio"), None)
    return dict(video_start=start(video), audio_start=start(audio) if audio else None,
                frame_rate=video.get("avg_frame_rate"), field_order=video.get("field_order"),
                transfer=video.get("color_transfer"), primaries=video.get("color_primaries"),
                matrix=video.get("color_space"))


def clock(report):
    s = streams(report)
    a = s["audio_start"]
    v = s["video_start"]
    origin = min(v, a) if a is not None else v
    return origin, v - origin


def graph(engine, inline, fps, parity, delay):
    n, d = (int(x) for x in fps.split('/'))
    clock = f"settb={d}/{n},setpts=PTS-STARTPTS,settb=1/1000,"
    if engine == "vulkan":
        bob = "format=p010le,hwupload,bwdif_vulkan=mode=send_field:parity=" + parity + ":deint=all,setfield=prog," + clock
        base = (bob if inline else "format=p010le,hwupload,") + "setpts=PTS-STARTPTS[basevk]"
        plate = "format=p010le,setpts=PTS-STARTPTS,hwupload[grainvk]"
        blend = "[basevk][grainvk]blend_vulkan=all_mode=overlay:all_opacity=0.80,hwdownload,format=p010le,format=yuv420p10le"
    else:
        bob = "format=yuv420p10le,bwdif=mode=send_field:parity=" + parity + ":deint=all,setfield=prog," + clock
        base = (bob if inline else "format=yuv420p10le,") + "setpts=PTS-STARTPTS[basevk]"
        plate = "format=yuv420p10le,setpts=PTS-STARTPTS[grainvk]"
        blend = "[basevk][grainvk]blend=all_mode=overlay:all_opacity=0.80,format=yuv420p10le"
    tail = ",setpts=PTS+{:.9f}/TB".format(delay) if delay > 1e-9 else ""
    return ("[0:v:0]setpts=PTS-STARTPTS," + base + ";[1:v:0]fps=" + fps + "," + plate + ";" + blend + tail + "[vout]")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--ffprobe", default="ffprobe")
    parser.add_argument("--engine", choices=["cpu", "vulkan"], required=True)
    parser.add_argument("--cases", type=int, default=24)
    parser.add_argument("--output", default="R01_Results")
    args = parser.parse_args()
    outdir = Path(args.output).resolve()
    outdir.mkdir(parents=True, exist_ok=True)
    log = outdir / "commands.log"
    if log.exists():
        log.unlink()
    results = []
    all_cases = list(itertools.product(["tff", "bff"], ["25/1", "30000/1001"],
                                       ["sdr", "pq", "hlg"], [-0.32, 0.32]))
    try:
        for idx, (parity, fps, signal, audio_offset) in enumerate(all_cases[:args.cases], 1):
            with tempfile.TemporaryDirectory(prefix="fgs_r01_", dir=str(outdir)) as workstr:
                work = Path(workstr)
                source, plate, intermediate = (work / x for x in ["source.mkv", "plate.mkv", "__FGS_FIELDS_old.mkv"])
                old, new = work / "old.mkv", work / "inline.mkv"
                rate = fps if fps != "25/1" else "25"
                double = "50/1" if fps == "25/1" else "60000/1001"
                tags = (["-color_range", "tv", "-color_primaries", "bt2020", "-color_trc",
                         "smpte2084" if signal == "pq" else "arib-std-b67", "-colorspace", "bt2020nc"]
                        if signal != "sdr" else ["-color_range", "tv", "-color_primaries", "bt709",
                         "-color_trc", "bt709", "-colorspace", "bt709"])
                order = "tt" if parity == "tff" else "bb"
                command(args.ffmpeg, ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i",
                         f"testsrc2=size=128x72:rate={rate}:duration=0.48", "-itsoffset", str(audio_offset),
                         "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=0.70",
                         "-map", "0:v:0", "-map", "1:a:0", "-vf", "format=yuv420p10le,setfield=" + parity,
                         "-c:v", "ffv1", "-level", "3", "-pix_fmt", "yuv420p10le", "-field_order", order,
                         *tags, "-c:a", "pcm_s16le", source], log)
                command(args.ffmpeg, ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i",
                         f"testsrc2=size=128x72:rate={double}:duration=1.20", "-vf", "format=yuv420p10le",
                         "-c:v", "ffv1", "-level", "3", plate], log)
                deint = ("format=p010le,hwupload,bwdif_vulkan=mode=send_field:parity=" + parity + ":deint=all,hwdownload,format=p010le,setfield=prog,"
                         if args.engine == "vulkan" else "format=yuv420p10le,bwdif=mode=send_field:parity=" + parity + ":deint=all,setfield=prog,")
                vulkan_args = (["-init_hw_device", "vulkan=vk", "-filter_hw_device", "vk"]
                               if args.engine == "vulkan" else [])
                legacy_start, _ = command(args.ffmpeg, ["-hide_banner", "-v", "error", "-y", *vulkan_args,
                                     "-copyts", "-start_at_zero", "-i", source, "-map", "0:v:0", "-map", "0:a?",
                                     "-map_metadata", "0", "-vf", deint + "format=yuv420p10le", "-c:v", "ffv1",
                                     "-level", "3", "-pix_fmt", "yuv420p10le", "-fps_mode", "passthrough",
                                     "-c:a", "copy", *tags, intermediate], log)
                source_meta = streams(probe(args.ffprobe, source))
                old_meta = streams(probe(args.ffprobe, intermediate))
                old_origin, old_delay = clock(probe(args.ffprobe, intermediate))
                new_origin, new_delay = clock(probe(args.ffprobe, source))
                ticks = int(new_delay * (int(double.split('/')[0]) / int(double.split('/')[1])) + 0.5)
                new_delay = round(ticks * int(double.split('/')[1]) * 1000.0 / int(double.split('/')[0])) / 1000.0
                def render(inp, dst, direct, origin, delay):
                    d = graph(args.engine, direct, double, parity, delay)
                    cmd = ["-hide_banner", "-v", "error", "-y", *vulkan_args, "-copyts", "-itsoffset", f"{-origin:.9f}",
                           "-i", inp, "-stream_loop", "-1", "-t", "0.9", "-i", plate,
                           "-filter_complex", d, "-map", "[vout]", "-map", "0:a?", "-map_metadata", "0",
                           "-c:v", "ffv1", "-level", "3", "-pix_fmt", "yuv420p10le", "-fps_mode:v", "passthrough",
                           "-c:a", "copy", *tags, "-t", f"{0.9+delay:.6f}", dst]
                    return command(args.ffmpeg, cmd, log)[0]
                legacy_end = render(intermediate, old, False, old_origin, old_delay)
                direct_time = render(source, new, True, new_origin, new_delay)
                hashes_old, pts_old = signature(args.ffmpeg, old, log)
                hashes_new, pts_new = signature(args.ffmpeg, new, log)
                out_old, out_new = streams(probe(args.ffprobe, old)), streams(probe(args.ffprobe, new))
                passed = (hashes_old == hashes_new and pts_old == pts_new and
                          abs((out_old["video_start"] - (out_old["audio_start"] or 0)) -
                              (out_new["video_start"] - (out_new["audio_start"] or 0))) < 0.003 and
                          out_old["transfer"] == out_new["transfer"] and out_old["matrix"] == out_new["matrix"])
                row = dict(case=idx, parity=parity, source_fps=fps, output_fps=double, signal=signal,
                           audio_offset=audio_offset, source=source_meta, field_workfile=old_meta,
                           old=out_old, new=out_new, frames_old=len(hashes_old), frames_new=len(hashes_new),
                           pixel_equal=hashes_old == hashes_new, pixel_prefix_equal=hashes_old == hashes_new[:len(hashes_old)],
                           first_pixel_difference=next((j for j,(a,b) in enumerate(zip(hashes_old,hashes_new)) if a!=b),-1),
                           pts_equal=pts_old == pts_new, pts_prefix_equal=pts_old == pts_new[:len(pts_old)],
                           workfile_bytes=intermediate.stat().st_size, old_final_bytes=old.stat().st_size,
                           new_final_bytes=new.stat().st_size, old_seconds=round(legacy_start+legacy_end, 4),
                           inline_seconds=round(direct_time, 4), passed=passed)
                results.append(row)
                print(f"{'PASS' if passed else 'FAIL'} {idx}/{min(args.cases,len(all_cases))}: {parity} {fps} {signal} audio_offset={audio_offset} frames={len(hashes_new)}", flush=True)
                (outdir / "results.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
        if not all(r["passed"] for r in results):
            raise RuntimeError("One or more PTS/pixel/metadata cases failed; see results.json and commands.log")
    except Exception:
        print(f"Diagnostic files: {outdir}", flush=True)
        raise
    print("All synthetic comparison cases passed. Full FGS job and RTX 4080 throughput still require separate acceptance.")


if __name__ == "__main__":
    main()
