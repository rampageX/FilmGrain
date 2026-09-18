# Film Grain Studio v4.8.1

Film Grain Studio 是基于 **FFmpeg、NVIDIA NVENC、libx264 与 grav1synth** 的 Windows 视频胶片化工具包，同时提供 GUI 与 CLI。项目将 AV1 Film Grain metadata、真实扫描 Grain Plate、Digital Grain 与 GPU Film Grain (FGSIM) 整合到同一套工作流中，并提供 HDR、LUT、反交错、OpenSVPFlow 插帧、字幕、Cinematic Style、自动码率和多文件处理。

## 本版说明

v4.8.1 以 v4.8.0 为完整功能基线，重点整理项目文档与发布呈现方式，**不修改编码核心和已经验证的处理参数**。

README 现在只维护当前版本的功能说明、工作原理、设置方式、使用建议与已知限制；逐版本新增、修复和发布记录统一放在 CHANGELOG 中。这样 README 更接近持续更新的产品说明书，而 CHANGELOG 专门承担版本历史职责。

## 当前主编码路线

| 主编码路线 | Film Grain 重点 | 最终编码 |
|---|---|---|
| **AV1 Main10 + grav1synth** | Film Grain metadata、Film Preset、Photon ISO、现成 Grain Table；也可选择像素颗粒 | AV1 Main10 NVENC |
| **HEVC Main10** | 真实扫描 Grain Plate、Digital Grain、GPU Film Grain (FGSIM) | HEVC Main10 NVENC |
| **H.264 x264 Grain** | 真实扫描 Grain Plate、Digital Grain、GPU Film Grain (FGSIM) | libx264 + `tune grain` |

默认配置仍为 **AV1 Main10 + MP4 + AAC 256k**。

## 当前颗粒方式

- **AV1 Film Grain metadata**：颗粒模型写入 AV1 码流，由播放器解码时合成，适合重视码率效率的 AV1 工作流。
- **真实扫描 Grain Plate**：将真实胶片扫描素材直接合成进像素，主要用于 HEVC / x264 路线。
- **Digital Grain · Fast Noise**：不依赖外部 Grain Plate 的像素颗粒引擎，三条主线均可使用，强度连续可调。
- **GPU Film Grain (FGSIM)**：使用 GPU shader / Vulkan 路径生成并烘焙颗粒；HEVC 遇到少数色带素材时可按需尝试 CQ27 / CQ23 方案。

## 其它主要功能

- HDR Preserve 与 HDR→SDR 自动兼容；
- LUT Gallery、预览图、Recent / Favorites 与智能过滤；
- BWDIF Vulkan / CUDA、W3FDIF Complex 自动 Field-rate 反交错；
- OpenSVPFlow GPU 60 fps 插帧；
- Cinematic Style 加黑边 / 裁剪；
- AV1 UHQ 与 SFE 多引擎并行；
- 自动码率、高动态模式与可见的 VBV 计算；
- 独立字幕烧写与 H.264 上传母版；
- 简体中文 / English 多语言 GUI；
- GUI / CLI 共用 StudioBridge 编码核心。

完整功能、参数、工作原理与使用方法请查看仓库首页 **README.md**；历史版本变化请查看 **CHANGELOG.md**。

## 与 v4.8.0 的兼容性

v4.8.1 完整继承 v4.8.0 的程序功能与编码逻辑。本次没有调整 AV1 / HEVC / x264、FGSIM、Digital Grain、Grain Plate、HDR、LUT、OpenSVPFlow、反交错、码率、字幕、容器或 AAC 256k 参数。

正式包继续由 **Windows Server 2022 runner** 构建，并执行 PowerShell、语言文件、BAT / CMD / VBS、BOM、CRLF、续行与必需资源检查。
