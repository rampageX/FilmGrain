import os
import vapoursynth as vs

core = vs.core
ALLOWED_ALGOS = {1, 2, 11, 13, 21, 22, 23}

plugin_dir = os.path.abspath(globals().get("plugin_dir", ""))
use_gpu = int(globals().get("gpu", "1")) != 0
algo_value = int(globals().get("algo", "13"))

if algo_value not in ALLOWED_ALGOS:
    raise vs.Error(f"Unsupported SmoothFps algo: {algo_value}")

svp1_path = os.path.join(plugin_dir, "svpflow1_vs.dll")
svp2_path = os.path.join(plugin_dir, "svpflow2_vs.dll")

core.std.LoadPlugin(svp1_path)
core.std.LoadPlugin(svp2_path)

clip = core.std.BlankClip(width=640, height=360, format=vs.YUV420P8, length=8, fpsnum=24, fpsden=1)

super_params = "{pel:1,gpu:1,full:true}" if use_gpu else "{pel:1,gpu:0,full:true}"
smooth_params = (
    f"{{gpuid:0,rate:{{num:60,den:1,abs:true}},algo:{algo_value},cubic:1,linear:true,"
    "mask:{cover:80,area:100,area_sharp:1.2},scene:{blend:false,mode:0}}}"
    if use_gpu else
    f"{{rate:{{num:60,den:1,abs:true}},algo:{algo_value},"
    "mask:{cover:80,area:100,area_sharp:1.2},scene:{blend:false,mode:0}}}"
)

super_clip = core.svp1.Super(clip, super_params)
vectors = core.svp1.Analyse(super_clip["clip"], super_clip["data"], clip, "{}")
smooth = core.svp2.SmoothFps(
    clip,
    super_clip["clip"],
    super_clip["data"],
    vectors["clip"],
    vectors["data"],
    smooth_params
)
smooth = core.std.AssumeFPS(smooth, fpsnum=60, fpsden=1)
smooth.set_output()
