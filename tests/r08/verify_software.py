"""Software media comparison for the R08 field and producer boundaries."""
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile

def ff(*args):
    return subprocess.check_output([
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-filter_threads", "1",
        "-filter_complex_threads", "1", *map(str, args),
    ])

def probe(path):
    return json.loads(subprocess.check_output([
        "ffprobe", "-v", "error", "-show_streams", "-show_frames", "-of", "json", str(path)
    ]))

def frames(doc, media):
    return [float(f["best_effort_timestamp_time"]) for f in doc["frames"] if f["media_type"] == media]

def raw(path, pix_fmt):
    return ff("-i", path, "-map", "0:v:0", "-f", "rawvideo", "-pix_fmt", pix_fmt, "-")

def run_fields(root):
    for transfer in ("smpte2084", "arib-std-b67"):
        for bob_rate in ("50", "60000/1001"):
            for parity, interlace in (("tff", "interleave_top"), ("bff", "interleave_bottom")):
                source = root / "source.mkv"
                old = root / "old.mkv"
                direct = root / "direct.mkv"
                remux = root / "remux.mkv"
                # Video starts after the audio track.
                ff("-f", "lavfi", "-i", f"testsrc2=s=160x96:r={bob_rate}:d=1.2",
                   "-f", "lavfi", "-i", "sine=f=823:r=48000:d=1.6",
                   "-vf", f"tinterlace=mode={interlace},format=yuv420p10le,setpts=PTS+0.32/TB",
                   "-c:v", "ffv1", "-field_order", "tt" if parity == "tff" else "bb",
                   "-color_range", "tv", "-colorspace", "bt2020nc", "-color_primaries", "bt2020",
                   "-color_trc", transfer, "-c:a", "pcm_s16le", source)
                hdr = (f"w3fdif=filter=complex:mode=field:parity={parity}:deint=all,setfield=prog,"
                       f"zscale=rin=tv:pin=bt2020:tin={transfer}:min=bt2020nc:t=linear:p=bt2020:npl=100,"
                       "format=gbrpf32le,tonemap=tonemap=hable:desat=2,zscale=p=bt709:t=bt709:m=bt709:r=tv,"
                       "format=yuv420p10le,setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709")
                args = ("-c:v", "ffv1", "-pix_fmt", "yuv420p10le", "-fps_mode", "passthrough", "-c:a", "copy")
                ff("-copyts", "-start_at_zero", "-i", source, "-vf", hdr, *args, old)
                doc = probe(source)
                video_start = frames(doc, "video")[0]
                audio_start = frames(doc, "audio")[0]
                common = min(video_start, audio_start)
                delay = video_start - common
                ff("-copyts", "-itsoffset", -common, "-i", source, "-filter_complex",
                   f"[0:v:0]{hdr},setpts=PTS-STARTPTS,setpts=PTS-STARTPTS+{delay}/TB[vout]",
                   "-map", "[vout]", "-map", "0:a?", *args, direct)
                ff("-i", old, "-map", "0", "-c", "copy", remux)
                old_info, new_info = probe(remux), probe(direct)
                old_video, new_video = frames(old_info, "video"), frames(new_info, "video")
                assert len(new_video) == len(old_video) == 2 * len(frames(doc, "video"))
                assert all(a < b for a, b in zip(new_video, new_video[1:]))
                assert max(abs(a-b) for a,b in zip(old_video,new_video)) <= 0.0021
                assert abs(new_video[0] - frames(new_info,"audio")[0] - delay) <= 0.0021
                assert frames(new_info,"audio") == frames(old_info,"audio")
                expected = hashlib.sha256(raw(old,"yuv420p10le")).digest()
                assert expected == hashlib.sha256(raw(direct,"yuv420p10le")).digest()
                video = new_info["streams"][0]
                assert [video[k] for k in ("color_range","color_space","color_transfer","color_primaries")] == ["tv","bt709","bt709","bt709"]
                assert all(not f["interlaced_frame"] for f in new_info["frames"] if f["media_type"]=="video")
                print(f"PASS field {transfer} {parity} {bob_rate}: {len(new_video)} frames, exact pixel hashes and audio clock")

def run_producer(root):
    for rate in ("50","60000/1001"):
        for container, extension in (("yuv4mpegpipe","y4m"),("nut","nut")):
            pipe = root / f"producer.{extension}"
            baseline = root / "baseline.mkv"
            direct = root / "direct.mkv"
            ff("-f","lavfi","-i",f"testsrc2=s=160x96:r={rate}:d=1","-pix_fmt","yuv420p",
               "-f",container,pipe)
            n,d = (rate.split("/") if "/" in rate else (rate,"1"))
            ff("-i",pipe,"-c:v","ffv1","-enc_time_base",f"{d}/{n}","-fps_mode","passthrough",baseline)
            ff("-i",pipe,"-vf","format=yuv420p,settb=expr=1/1000,setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709",
               "-c:v","ffv1","-enc_time_base",f"{d}/{n}","-fps_mode","passthrough",direct)
            assert raw(baseline,"yuv420p") == raw(direct,"yuv420p")
            assert frames(probe(baseline),"video") == frames(probe(direct),"video")
            print(f"PASS {container} {rate}: decoded FFV1 pixels and clock match direct producer boundary")

with tempfile.TemporaryDirectory() as directory:
    root = Path(directory)
    run_fields(root)
    run_producer(root)
