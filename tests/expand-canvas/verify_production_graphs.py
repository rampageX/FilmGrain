from pathlib import Path
import subprocess
import sys
import json

ff = Path(sys.argv[1])
work = Path(sys.argv[2])
probe = ff.with_name("ffprobe.exe")
def run(*args, binary=ff):
    p = subprocess.run([str(binary), *map(str,args)], capture_output=True, text=True, encoding="utf-8", errors="replace")
    if p.returncode:
        raise RuntimeError(str(p.args)+"\n"+p.stderr)
    return p.stdout
def frames(s):
    return [tuple(x.strip() for x in l.split(",")) for l in s.splitlines() if l and not l.startswith("#")]
print(run("-version").splitlines()[0])
for file in sorted(work.glob("*.graph")):
    trc,pix,motion = file.stem.split("_")
    hdr = trc != "bt709"
    signal = f"setparams=range=limited:color_primaries={'bt2020' if hdr else 'bt709'}:color_trc={trc}:colorspace={'bt2020nc' if hdr else 'bt709'}"
    ten = pix == "yuv420p10le"
    lo, yr, cr = (64,877,897) if ten else (16,220,225)
    src = f"nullsrc=s=320x180:r=25:d=0.32,format={pix},geq=lum='{lo}+mod(X*11+Y*7+N*19,{yr})':cb='{lo}+mod(X*19+Y*13+N*23,{cr})':cr='{lo}+mod(X*5+Y*31+N*37,{cr})',{signal},setsar=16/15,setpts='N+floor(N/3)'"
    base = ["-hide_banner","-loglevel","error","-nostdin","-y","-filter_complex_threads","1","-f","lavfi","-i",src]
    tail = ["-an","-c:v","rawvideo","-threads:v","1","-pix_fmt",pix,"-fps_mode","passthrough","-enc_time_base","1/25","-f","framemd5","-"]
    original = frames(run(*base,*tail))
    graph = file.read_text(encoding="utf-8")
    inp = base+["-i",file.with_suffix(".png")]
    cropped = graph.replace("[vout]","[canvas];[canvas]crop=320:180:22:8:exact=1[vout]")
    actual = frames(run(*inp,"-filter_complex",cropped,"-map","[vout]",*tail))
    if len(original)!=8 or actual!=original:
        raise RuntimeError(f"{file.stem}: original samples/PTS differ\n{original}\n{actual}")
    out = file.with_suffix(".mkv")
    codec = "libx265" if ten else "libx264"
    run(*inp,"-filter_complex",graph,"-map","[vout]","-an","-c:v",codec,"-threads","1","-preset","ultrafast","-pix_fmt",pix,"-fps_mode","passthrough","-enc_time_base","1/25","-color_range","tv","-color_primaries","bt2020" if hdr else "bt709","-color_trc",trc,"-colorspace","bt2020nc" if hdr else "bt709",out)
    info = json.loads(run("-v","error","-select_streams","v:0","-count_frames","-show_streams","-of","json",out,binary=probe))["streams"][0]
    expected = {"width":364,"height":196,"sample_aspect_ratio":"16:15","color_transfer":trc,"color_range":"tv","nb_read_frames":"8"}
    expected.update(color_primaries="bt2020" if hdr else "bt709", color_space="bt2020nc" if hdr else "bt709")
    for key,val in expected.items():
        if info.get(key)!=val: raise RuntimeError(f"{file.stem}: {key} expected {val}, got {info.get(key)}")
    print("PASS original samples/PTS and encoded canvas:",file.stem,flush=True)
if len(list(work.glob("*.graph")))!=8: raise RuntimeError("Expected eight production graph cases")

# A hosted Windows runner may lack a Vulkan device. A Vulkan workstation can
# run the same matrix with FGS_TEST_VULKAN_DEVICE=0 (or the chosen device name).
import os
device = os.environ.get("FGS_TEST_VULKAN_DEVICE")
gpu_args = ["-init_hw_device", f"vulkan=vk:{device or '0'},disable_multiplane=1",
            "-filter_hw_device", "vk"]
try:
    run("-hide_banner", "-v", "error", *gpu_args, "-f", "lavfi", "-i", "testsrc2=s=64x64",
        "-vf", "format=p010le,hwupload,hwdownload,format=p010le", "-frames:v", "1", "-f", "null", "-")
    vulkan_available = True
except RuntimeError:
    if device is not None:
        raise
    vulkan_available = False
    print("SKIP Vulkan execution of Resize and NVENC compositor graphs: no usable Vulkan device.", flush=True)

def header(output, key, value):
    if f"#{key} 0: {value}" not in output.splitlines():
        raise RuntimeError(f"Expected {key} {value}\n{output}")

def verify_geometry(meta, art, route, pix, base):
    if route == "gpu":
        composite = meta["gpu"]
        split_label = "[fgsfilm_gpu_video]"
        split = (",split=2[fgsfilm_gpu_video][reference_hw];"
                 "[reference_hw]hwdownload,format=p010le,format=yuv420p10le[reference]")
    else:
        composite = meta[f"cpu_{pix}"]
        split_label = "[fgs_expand_input]"
        split = ",split=2[fgs_expand_input][reference]"
    # Split the same prepared frame. Separate Vulkan executions can dither
    # differently before either graph reaches the compositor.
    crop = f"crop={meta['width']}:{meta['height']}:{meta['left']}:{meta['top']}:exact=1"
    comparison = composite.replace(split_label, split, 1).replace(
        "[vout]", f"[canvas];[canvas]{crop}[vout]")
    tail = ["-an", "-c:v", "rawvideo", "-threads:v", "1", "-pix_fmt", pix,
            "-fps_mode", "passthrough", "-enc_time_base", "1/25", "-f", "framemd5"]
    reference_md5 = art.with_suffix(".reference.framemd5")
    actual = run(*base, "-i", art, "-filter_complex", comparison,
        "-map", "[reference]", *tail, reference_md5, "-map", "[vout]", *tail, "-")
    # The result over stdout is from the canvas aperture.
    expected = reference_md5.read_text(encoding="utf-8")
    reference_md5.unlink()
    if len(frames(expected)) != 4 or frames(actual) != frames(expected):
        raise RuntimeError(f"{art.stem} {route} {pix}: picture samples, PTS or frame count differ\n{expected}\n{actual}")
    for output in (expected, actual):
        header(output, "sar", f"{meta['sarNum']}/{meta['sarDen']}")
        header(output, "dimensions", f"{meta['width']}x{meta['height']}")
    # A lossless output checks signal tags from the graph without overriding
    # them with output color flags. Two frames suffice to probe the metadata.
    encoded = art.with_suffix(".geometry.mkv")
    run(*base, "-i", art, "-filter_complex", composite, "-map", "[vout]", "-an",
        "-frames:v", "2", "-c:v", "ffv1", "-threads:v", "1", "-pix_fmt", pix, encoded)
    stream = json.loads(run("-v", "error", "-select_streams", "v:0", "-show_streams",
                            "-of", "json", encoded, binary=probe))["streams"][0]
    hdr = meta["transfer"] != "bt709"
    tags = {"width": meta["canvasWidth"], "height": meta["canvasHeight"],
            "sample_aspect_ratio": f"{meta['sarNum']}:{meta['sarDen']}",
            "color_range": "tv", "color_transfer": meta["transfer"],
            "color_primaries": "bt2020" if hdr else "bt709", "color_space": "bt2020nc" if hdr else "bt709"}
    for key, value in tags.items():
        if stream.get(key) != value:
            raise RuntimeError(f"{art.stem} {route} {pix}: expected {key}={value}, got {stream.get(key)}")
    encoded.unlink()
    print("PASS processed picture samples, PTS, SAR and canvas tags:", art.stem, route, pix, flush=True)

geometry_files = sorted(work.glob("*.geometry.json"))
if len(geometry_files) != 48:
    raise RuntimeError("Expected 48 processed picture cases")
for case in geometry_files:
    meta = json.loads(case.read_text(encoding="utf-8"))
    if meta["resize"] and not vulkan_available:
        continue
    transfer = meta["transfer"]
    hdr = transfer != "bt709"
    signal = (f"setparams=range=limited:color_primaries={'bt2020' if hdr else 'bt709'}:"
              f"color_trc={transfer}:colorspace={'bt2020nc' if hdr else 'bt709'}")
    source = (f"testsrc2=s={meta['sourceWidth']}x{meta['sourceHeight']}:r=25:d=0.16,"
              f"format=yuv420p10le,{signal},setsar=16/15,setpts='N+floor(N/2)'")
    inp = ["-hide_banner", "-v", "error", "-nostdin", "-y", "-filter_complex_threads", "1"]
    source_args = ["-f", "lavfi", "-i", source]
    art = case.with_name(case.name.removesuffix(".geometry.json") + ".png")
    for pix in (["yuv420p10le"] if hdr else ["yuv420p", "yuv420p10le"]):
        verify_geometry(meta, art, "cpu", pix, inp + (gpu_args if meta["resize"] else []) + source_args)
    if vulkan_available:
        verify_geometry(meta, art, "gpu", "yuv420p10le", inp + gpu_args + source_args)
