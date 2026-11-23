# Film Grain Studio v5.0.88

- **R02 慢路径优化**：逐行 SDR 的 CPU Bilateral＋真实扫描 Grain Plate＋胶片边框，放行已验证的 GPU S4 Vulkan/libplacebo→CUDA→NVENC 合成通道，不再因 Bilateral 前处理直接回退到第二次 CPU 边框。
- **合同保持**：保留 Bilateral 原有 sigmaS/sigmaR/planes、10-bit 前处理→颗粒→Alpha 边框顺序、BT.709 色彩标签及音视频时间轴；HDR、非 Plate、其它前处理和 R01 已验收路径不扩大范围。Bilateral 本身仍为 CPU 计算，S4 所需的软件解码不改变。
- **验收**：Windows 2022 R02 CI #45121702403 通过（.NET Framework 4.8 编译、2996/2996 Framework API、R02 32 组选路和 R01/S4/HDR 回归）；T600 同源 10 秒带 AAC 素材，HEVC/H.264 NVENC × Bilateral 开/关 × 静/动态边框共 8/8 成功，用户确认画面正常。未进行 v5.0.87 同配置旧路性能对比，不宣称提速百分比。
