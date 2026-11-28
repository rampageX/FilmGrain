# Film Grain Studio v5.1.08

- RTX 4080 上，符合条件的 SDR BT.709 Limited HEVC Main10 默认使用 GPU Normalized LUT，可切换 CPU tetrahedral。复选框显示所选 G/C，输出名称按实际路线添加 _G/_C。
- 普通扫描 Grain Plate 支持 Vulkan → CUDA 直通。实测通过 GPU／CPU LUT 加 Kodak Plate，以及 4K60 10-bit SDR 和 1080p 独立 GPU LUT。
- AV1 Grain Table 默认显示全部，勾选才按分辨率筛选。两份 Dune2 表原样移入 2160p，新增 00_3840x2160_ 前缀。
- 修复 AV1 被已保存的 HEVC 外框参数拦截的问题，统一 BT.2390 显示名称。
