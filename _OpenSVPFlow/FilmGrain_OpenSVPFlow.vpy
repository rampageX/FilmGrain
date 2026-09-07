import os
import vapoursynth as vs

core = vs.core


def arg(name: str, default=None):
    value = globals().get(name, default)
    if value is None:
        raise vs.Error(f"Missing vspipe argument: {name}")
    return value


input_path = os.path.abspath(arg("input"))
plugin_dir = os.path.abspath(arg("plugin_dir"))
target_num = int(arg("target_num", "60"))
target_den = int(arg("target_den", "1"))
algo_value = int(arg("algo", "13"))
analyse_profile = str(arg("analyse_profile", "ENCODEGUI")).upper()
scene_mode = int(arg("scene_mode", "0"))
mask_area = int(arg("mask_area", "100"))

if algo_value not in {1, 2, 11, 13, 21, 22, 23}:
    raise vs.Error(f"Unsupported SmoothFps algo: {algo_value}")
if analyse_profile not in {"ENCODEGUI", "BASE"}:
    raise vs.Error(f"Unsupported Analyse profile: {analyse_profile}")
if scene_mode not in {0, 1, 2, 3}:
    raise vs.Error(f"Unsupported scene mode: {scene_mode}")
if mask_area < 0 or mask_area > 100:
    raise vs.Error(f"Artifact mask area must be 0..100: {mask_area}")

svp1_path = os.path.join(plugin_dir, "svpflow1_vs.dll")
svp2_path = os.path.join(plugin_dir, "svpflow2_vs.dll")

if not os.path.isfile(input_path):
    raise vs.Error(f"Input file not found: {input_path}")
if not os.path.isfile(svp1_path):
    raise vs.Error(f"Plugin not found: {svp1_path}")
if not os.path.isfile(svp2_path):
    raise vs.Error(f"Plugin not found: {svp2_path}")

core.std.LoadPlugin(svp1_path)
core.std.LoadPlugin(svp2_path)

clip = core.bs.VideoSource(source=input_path, cachemode=1)
if clip.format is None:
    raise vs.Error("Variable-format input is not supported by the current OpenSVPFlow integration.")

# Current verified open-svpflow baseline is YUV420P8. Film Grain Studio converts
# the interpolated result back to its normal P010/Main10 processing chain.
if clip.format.id != vs.YUV420P8:
    if clip.format.color_family == vs.RGB:
        clip = core.resize.Bicubic(clip, format=vs.YUV420P8, matrix_s="709")
    else:
        clip = core.resize.Bicubic(clip, format=vs.YUV420P8)

# Verified open-svpflow nightly baseline. Keep full:true: full:false can access
# violate in the currently verified nightly.
super_params = "{pel:1,gpu:1,full:true}"

# EncodeGUI Analyse is the recommended baseline; BASE is retained as a comparison.
if analyse_profile == "BASE":
    analyse_params = "{}"
else:
    analyse_params = (
        "{gpu:1,vectors:3,"
        "block:{w:16,h:16,overlap:2},"
        "main:{search:{distance:0,coarse:{distance:-10,bad:{sad:2000}}}},"
        "refine:[{thsad:250}]}"
    )

smooth_params = (
    f"{{gpuid:0,rate:{{num:{target_num},den:{target_den},abs:true}},"
    f"algo:{algo_value},cubic:1,linear:true,"
    f"mask:{{cover:80,area:{mask_area},area_sharp:1.2}},"
    f"scene:{{blend:false,mode:{scene_mode}}}}}"
)

super_clip = core.svp1.Super(clip, super_params)
vectors = core.svp1.Analyse(
    super_clip["clip"],
    super_clip["data"],
    clip,
    analyse_params
)

smooth = core.svp2.SmoothFps(
    clip,
    super_clip["clip"],
    super_clip["data"],
    vectors["clip"],
    vectors["data"],
    smooth_params
)

smooth = core.std.AssumeFPS(smooth, fpsnum=target_num, fpsden=target_den)
smooth.set_output()
