import os
import sys
import vapoursynth as vs

core = vs.core


def build_svp_parameters(speed="FGS", tuning="FGS", block_size="auto",
                         analyse_profile="ENCODEGUI", algo=13, scene_mode=0,
                         mask_area=100, target_num=60, target_den=1):
    # Speed/Tuning are front-end presets, not plugin API fields.
    # Mapping inspected from smoothie.z1x.us worker-oUtBbSR_.js on 2026-10-12.
    # Keep the existing FGS strings byte-for-byte at the default settings.
    if speed not in {"FGS", "medium", "fast", "faster", "fastest"}:
        raise ValueError(f"Invalid OpenSVPFlow Speed: {speed}")
    if tuning not in {"FGS", "weak", "film", "smooth", "animation"}:
        raise ValueError(f"Invalid OpenSVPFlow Tuning: {tuning}")
    if block_size not in {"auto", "8", "16", "32"}:
        raise ValueError(f"Invalid OpenSVPFlow Block Size: {block_size}")
    if analyse_profile not in {"ENCODEGUI", "BASE"}:
        raise ValueError(f"Unsupported Analyse profile: {analyse_profile}")
    if analyse_profile == "BASE" and (speed, tuning, block_size) != ("FGS", "FGS", "auto"):
        raise ValueError("BASE requires Speed=FGS, Tuning=FGS, Block Size=auto")
    if algo not in {1, 2, 11, 13, 21, 22, 23}:
        raise ValueError(f"Unsupported SmoothFps algo: {algo}")
    if scene_mode not in {0, 1, 2, 3} or not 0 <= mask_area <= 100:
        raise ValueError("Invalid scene mode or mask area")
    if target_num <= 0 or target_den <= 0:
        raise ValueError("Target frame rate must be positive")

    # full:true is intentional: the bundled nightly has a known full:false crash.
    pel = 2 if speed == "medium" else 1
    super_params = f"{{pel:{pel},gpu:1,full:true}}"
    if analyse_profile == "BASE":
        analyse_params = "{}"
    elif speed == "FGS" and tuning == "FGS":
        w = 16 if block_size == "auto" else int(block_size)
        analyse_params = (
            f"{{gpu:1,vectors:3,block:{{w:{w},h:{w},overlap:2}},"
            "main:{search:{distance:0,coarse:{distance:-10,bad:{sad:2000}}}},"
            "refine:[{thsad:250}]}"
        )
    else:
        animation = tuning == "animation"
        weak = tuning == "weak"
        w = (32 if animation or speed == "fastest" else
             16 if speed in {"FGS", "fast", "faster"} else 8)
        if block_size != "auto":
            w = int(block_size)
        overlap = 0 if animation or speed == "fastest" else 1 if speed == "faster" else 2
        analyse_params = f"{{block:{{w:{w},overlap:{overlap}}},main:{{search:{{"
        if animation:
            analyse_params += "coarse:{type:2,distance:-6,satd:false},distance:0,"
        else:
            if speed != "faster":
                analyse_params += "distance:0,"
            analyse_params += "coarse:{"
            analyse_params += "distance:-1,trymany:true," if weak else "distance:-10,"
        if animation:
            analyse_params += "bad:{sad:2000}}}}"
        elif speed in {"faster", "fastest"}:
            analyse_params += "bad:{sad:2000}}}}}"
        elif weak:
            analyse_params += "bad:{sad:2000}}}},refine:[{thsad:250,search:{distance:-1,satd:true}}]}"
        else:
            analyse_params += "bad:{sad:2000}}}},refine:[{thsad:250}]}"

    smooth_params = (
        f"{{gpuid:0,rate:{{num:{target_num},den:{target_den},abs:true}},"
        f"algo:{algo},cubic:1,linear:true,"
        f"mask:{{cover:80,area:{mask_area},area_sharp:1.2}},"
    )
    # Preserve FGS scene-mode selection, exact fractional FPS, cubic/linear and mask.
    # Weak only enables the upstream scene blend when scene mode is normal (0).
    if tuning == "weak" and scene_mode == 0:
        smooth_params += "scene:{blend:true,mode:0,limits:{blocks:50}}}"
    else:
        smooth_params += f"scene:{{blend:false,mode:{scene_mode}}}}}"
    return super_params, analyse_params, smooth_params


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
speed = str(arg("speed", "FGS"))
tuning = str(arg("tuning", "FGS"))
block_size = str(arg("block_size", "auto"))

try:
    super_params, analyse_params, smooth_params = build_svp_parameters(
        speed, tuning, block_size, analyse_profile, algo_value, scene_mode,
        mask_area, target_num, target_den)
except ValueError as exc:
    raise vs.Error(str(exc))
print(f"[OpenSVPFlow options] speed={speed} tuning={tuning} block_size={block_size} analyse={analyse_profile}", file=sys.stderr, flush=True)
print(f"[OpenSVPFlow params] Super={super_params} Analyse={analyse_params} SmoothFps={smooth_params}", file=sys.stderr, flush=True)

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
