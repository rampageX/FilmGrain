# Film Grain Studio v4.8.12

### 新增

* .NET Preview 增加 AV1、HEVC、x264 三条 Native 单文件编码执行路径；每条路线仅在已覆盖的组合中使用 Native，其他场景继续由现有 Legacy Bridge 处理。
* AV1 Native 支持 Film Preset、Photon ISO（亮度 / 色度）和 Grain Table 元数据颗粒，以及 Digital Grain、FGSIM 像素颗粒；保留 AV1 不重编码添加 / 替换 Film Grain。
* AV1 Native 已覆盖的单文件组合接入色彩校正、黑白、CUBE LUT、Cinematic 画幅、硬字幕、高动态配置、受硬件能力门控的 SFE、自动反交错与 Field-rate 输出、HDR Preserve / HDR→SDR，以及 SDR H.264 上传副本。
* x264 与 HEVC Native 接入各自已验证的单文件编码和颗粒处理路线；x264 包含多遍编码与已验证的插帧路线，HEVC 保留其已覆盖的 HDR、字幕与上传版处理。

### 改进与修复

* 修正 AV1 颗粒信息栏：显示实际选择的亮度 / 色度平面；修复 AV1 不重编码替换颗粒时启动报错。
* 修正 AV1 HDR Preserve + FGSIM 状态显示，避免信息栏错误显示“转 SDR - Hable”；当显式选择 HDR Preserve 而 Native FGSIM 条件不满足时阻止静默回退到会改变 HDR 的 Legacy 路线。
* AV1 Native 新增 HDR Digital Grain、隔行 HDR 与 HDR FGSIM 的已覆盖路径；HDR Preserve 下 H.264 上传副本继续按既有规则跳过。
* x264 Native、HEVC Native、AV1 Native 的对应 Windows 测试组已由用户完成并通过；测试结论只覆盖各测试说明中列出的素材、参数和组合。
* 本版不修改多文件队列、共享预处理、Phase 3.5.8 Bridge 编码处理中状态及 FRUC Vulkan 音画同步共享执行链路；这些仍由原有路径处理。
