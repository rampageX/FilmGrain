import os
import sys
import math
import vapoursynth as vs

core = vs.core


def build_svp_parameters(speed="FGS", tuning="FGS", block_size="auto",
                         analyse_profile="ENCODEGUI", algo=13, scene_mode=0,
                         mask_area=100, target_num=60, target_den=1,
                         dedup=0.0, still=False, still_limit=4.5, still_edge=5.0,
                         still_tolerance=30.0, peel=False, peel_limit=6.0,
                         exact=True, lean=False):
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

    for name, value, maximum in [("dedup",dedup,10), ("still_limit",still_limit,100),
                                  ("still_edge",still_edge,100), ("still_tolerance",still_tolerance,100), ("peel_limit",peel_limit,100)]:
        if not math.isfinite(value) or value < 0 or value > maximum:
            raise ValueError(f"Invalid OpenSVPFlow {name}: expected finite 0..{maximum}")
    if not isinstance(still, bool) or not isinstance(peel, bool):
        raise ValueError("still and peel must be boolean")
    if not isinstance(exact, bool) or not isinstance(lean, bool):
        raise ValueError("exact and lean must be boolean")
    if lean and exact:
        raise ValueError("OpenSVPFlow lean=true requires exact=false")
    mask_extra = ""
    if still:
        mask_extra += f",still:{{limit:{still_limit:g},edge:{still_edge:g},tolerance:{still_tolerance:g}}}"
    if peel:
        mask_extra += f",peel:{{limit:{peel_limit:g}}}"
    dedup_extra = f"dedup:{dedup:g}," if dedup > 0 else ""

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

    # Never add new keys on the verified default path (including BASE).
    if not exact:
        prefix = "exact:false," if analyse_params.startswith("{gpu:1,") else "gpu:1,exact:false,"
        analyse_params = "{" + prefix + analyse_params[1:]
        analyse_params = analyse_params.replace(",}", "}")
    if lean:
        super_params = super_params[:-1] + ",lean:true}"

    smooth_params = (
        f"{{gpuid:0,rate:{{num:{target_num},den:{target_den},abs:true}},"
        f"algo:{algo},cubic:1,linear:true,{dedup_extra}"
        f"mask:{{cover:80,area:{mask_area},area_sharp:1.2{mask_extra}}},"
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
def option_bool(name, default="0"):
    value = str(arg(name, default)).lower()
    if value not in {"0", "1", "false", "true"}:
        raise vs.Error(f"Invalid {name}: expected true/false or 1/0")
    return value in {"1", "true"}
try:
    exact_value = option_bool("exact", "1")
    lean_value = option_bool("lean")
    dedup_value = float(arg("dedup", "0"))
    still_value = option_bool("still")
    peel_value = option_bool("peel")
    still_limit = float(arg("still_limit", "4.5"))
    still_edge = float(arg("still_edge", "5"))
    still_tolerance = float(arg("still_tolerance", "30"))
    peel_limit = float(arg("peel_limit", "6"))
except (ValueError, OverflowError) as exc:
    raise vs.Error(f"Invalid OpenSVPFlow extra option: {exc}")

try:
    super_params, analyse_params, smooth_params = build_svp_parameters(
        speed, tuning, block_size, analyse_profile, algo_value, scene_mode,
        mask_area, target_num, target_den, dedup_value, still_value, still_limit,
        still_edge, still_tolerance, peel_value, peel_limit, exact_value, lean_value)
except ValueError as exc:
    raise vs.Error(str(exc))
print(f"[OpenSVPFlow options] speed={speed} tuning={tuning} block_size={block_size} analyse={analyse_profile} exact={exact_value} lean={lean_value} dedup={dedup_value:g} still={still_value} peel={peel_value} effective=YUV420P8/OpenCL", file=sys.stderr, flush=True)
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

if not exact_value:
    # Feature probe: older plugins silently ignore unknown option keys. Lean Super
    # marks byte 5 bit 7 of its packed metadata; require the experimental pair's
    # feature generation without pinning a nightly or parsing a version file.
    try:
        probe_source = core.std.BlankClip(width=64, height=64, length=2, format=vs.YUV420P8)
        probe_super = core.svp1.Super(probe_source, "{pel:1,gpu:1,full:true,lean:true}")
        if not (int(probe_super["data"]) & (1 << 47)):
            raise ValueError("lean metadata flag missing (option ignored)")
    except (vs.Error, ValueError, KeyError, TypeError) as exc:
        raise vs.Error("OpenSVPFlow 实验性高速分析不可用 / Experimental exact/lean unsupported. "
                       "请成对更新 svpflow1_vs.dll 与 svpflow2_vs.dll，或关闭高速分析 / "
                       f"Update both plugins or disable fast analysis. Details: {exc}")
    del probe_super, probe_source

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
