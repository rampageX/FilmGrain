# Film Grain Studio v5.0.84

- **扫描颗粒＋胶片边框 GPU 加速**：SDR BT.709 TV Limited 的 HEVC/H.264 NVENC 与 Grain Plate、静态或动态边框组合，复用 Vulkan 颗粒合成、libplacebo GPU 边框及 CUDA/NVENC 直通，避免旧 CPU 边框路径中的 GPU→CPU 下载与软件叠加。H.264 仍输出 NV12 8-bit，PQ/HLG HEVC 的既有 S4 GPU 合成路径保持不变。
- **实机性能验证**：RTX 4080、1080p 扫描颗粒＋动态边框组合，由约 15 fps 恢复至约 142 fps；单独 SDR 边框仍约 150+ fps。
- **任务阶段性能提示**：现有阶段栏对经滤镜图确认的高开销路线使用橙色提示：Vulkan/FGSIM 颗粒＋CPU 边框、数字颗粒＋CPU 边框、CPU Bilateral、HDR 软件色调映射和 OpenSVPFlow HDR CPU10。单独 SDR 边框、正常 CPU 编码、普通软解、普通 hwdownload 不误报严重性能回退。
- **MP4 Faststart 可选**：右键“开始编码”切换网页播放优化，默认关闭并记忆。开启后仅对 MP4 成品追加 `-movflags +faststart`；关闭时避免大文件无谓的 moov 前移，MKV 和中间文件不受影响。
- **验收与构建**：用户已确认 P6R2W4 运行效果；Windows 构建、PQ/HLG GPU 图、S4 边框、CPU 性能警示、Native UI 构图及缩略图回归测试通过。维持现有 FFmpeg/FRUC、音画时序与颗粒算法。
