# Film Grain Studio v5.0.57

- 新增独立的高级 GPU 单帧预览：AV1／SVT 内置胶片预设和 Grain Table、HEVC／H.264／x264／SVT 的扫描 Grain Plate，以及 3D LUT 支持同窗拖动即时预览。应用后回传原有主界面强度，取消不修改参数；正式编码核心与原四档预览不变。
- AV1 参数颗粒与 LUT 可按 1% 调整，Grain Plate 按 5% 档位调整；窗口记忆位置／大小，随机选取初始帧，提供精确时间跳转与重新随机选帧。仅支持有限范围逐行 SDR BT.709；ISO／Digital／FGSIM、HDR 和连续播放仍由原有功能处理。
- 优化 ProRes Grain Plate 在循环素材中的定位及并行抽帧，避免每次从长视频起点线性解码。RTX 4080 用户测试颗粒抽帧由约 3–4.5 秒降到约 0.28 秒，初次视频抽帧约 0.7 秒；滑杆调整无需重新抽帧，画面效果与正式输出接近但不保证逐像素一致。
- 正式 Windows 发布链新增 MSYS2 UCRT64 构建、辅助 EXE 与递归 DLL 依赖收集、libplacebo 许可及包内容校验；Stable 携带独立运行目录，Source 包含 C 源码、构建脚本与测试。不改变既有编码工作流及 Legacy 隔离。
