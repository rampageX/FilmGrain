# Film Grain Studio v5.0.86

- **HDR 调色＋扫描颗粒＋胶片边框**：HEVC NVENC 的 BT.2020 TV Limited PQ／HLG 素材支持 LUT、色彩纠正及两者同时启用。调色沿用原高位深流程，颗粒和边框由 Vulkan/libplacebo 合成，经 CUDA 送入 NVENC。
- **SDR 缩放／剪裁＋扫描颗粒＋胶片边框**：HEVC 与 H.264 NVENC 支持 Resize、先剪后缩、先缩后剪以及偶数起点的单独剪裁。按最终画布合成静态或动态边框，H.264 在 CUDA 内转 NV12。
- **验收**：R6K4P9 两项组合均已在 RTX 4080 实机通过。字幕烧录组合沿用原处理链。
