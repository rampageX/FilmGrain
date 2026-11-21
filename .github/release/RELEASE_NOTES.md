# Film Grain Studio v5.0.82

- **FFmpeg Stable 9.1 适配**：支持 rampageX/FFmpeg 官方源码静态构建及经 RTX 4080 验证的 `disable_host_image_copy=1` Vulkan/CUDA 兼容修复；旧版 FFmpeg 不传入新参数。
- **FRUC Vulkan 正式归主版**：滤镜检测、能力刷新和 AV1／HEVC／H.264／CPU 路线统一使用主 FFmpeg。特殊版 FFmpeg 与 SVT-AV1-HDR 实验入口继续保留，SVT-HDR 单进程与独立主版 FRUC 不支持组合。
- **grav1synth 版本兼容**：SVT 颗粒与 AV1 ISO 优化统一接受 `grav1synth >= 0.2.2`，包括修复 Full Range 的 0.2.3；不再使用精确版本白名单。
- 已通过 Windows 正式包构建与现有 GPU/HDR/边框回归；用户已实测通过 Stable 9.1 下的 SDR/PQ/HLG、SVP/AI 与 SVT/AV1 路线。
- 依赖 FFmpeg 与 grav1synth 仍需自行配置。本版保留预装 15 款边框和既有功能范围。
