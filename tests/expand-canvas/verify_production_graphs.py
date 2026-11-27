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
    cropped = graph.replace("[vout]","[canvas];[canvas]crop=320:180:64:64:exact=1[vout]")
    actual = frames(run(*inp,"-filter_complex",cropped,"-map","[vout]",*tail))
    if len(original)!=8 or actual!=original:
        raise RuntimeError(f"{file.stem}: original samples/PTS differ\n{original}\n{actual}")
    out = file.with_suffix(".mkv")
    codec = "libx265" if ten else "libx264"
    run(*inp,"-filter_complex",graph,"-map","[vout]","-an","-c:v",codec,"-threads","1","-preset","ultrafast","-pix_fmt",pix,"-fps_mode","passthrough","-enc_time_base","1/25","-color_range","tv","-color_primaries","bt2020" if hdr else "bt709","-color_trc",trc,"-colorspace","bt2020nc" if hdr else "bt709",out)
    info = json.loads(run("-v","error","-select_streams","v:0","-count_frames","-show_streams","-of","json",out,binary=probe))["streams"][0]
    expected = {"width":448,"height":308,"sample_aspect_ratio":"16:15","color_transfer":trc,"color_range":"tv","nb_read_frames":"8"}
    for key,val in expected.items():
        if info.get(key)!=val: raise RuntimeError(f"{file.stem}: {key} expected {val}, got {info.get(key)}")
    print("PASS original samples/PTS and encoded canvas:",file.stem,flush=True)
if len(list(work.glob("*.graph")))!=8: raise RuntimeError("Expected eight production graph cases")
