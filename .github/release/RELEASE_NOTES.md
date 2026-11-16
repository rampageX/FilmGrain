# Film Grain Studio v5.0.74

- NVEncC AI HDR 插帧改为自动识别 NVEncC64.exe 是否包含专用的 10-bit VFG CUDA 路线，不再需要 .enable 标记文件。
- 「配置 → NVEncC」新增 HDR VFG 10-bit 支持状态（支持／不支持／未检测），右下角 NVEncC 悬停提示新增 AI HDR ✓／✗。
- 两处显示复用同一份能力检测与缓存，切换 NVEncC 路径后会重新识别，不反复做 GPU 测试。
- 已验证的 PQ/HLG TV 范围 10-bit VFG 路线及 SDR 插帧、FRUC、SVP 保持原有参数与行为；普通 NVEncC 不会误用 HDR VFG。
- 仍需要安装此前通过 RTX 4080 实测的 HDR 专用 NVEncC 构建；自动识别是程序特征检测，实际 GPU 运行与素材兼容性以编码结果为准。
