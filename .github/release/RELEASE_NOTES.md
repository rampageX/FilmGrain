# Film Grain Studio v5.0.58

- 扩展「AV1 烘焙...」为 AV1 成片快捷转码：HEVC NVENC、H.264 NVENC、CPU x265、CPU x264 四种输出，各自显示适用的高级编码设置；CQ/CRF、VBR、Preset、Lookahead、Multipass、HEVC SFE 与硬解控制复用现有参数校验，保留编码器切换记忆。原 AV1 颗粒合成逻辑和主编码处理路线不变。
- HDR10 AV1 可烘焙到 HEVC NVENC Main10 或 x265 Main10，保留 PQ/BT.2020 和源中实际存在的 Mastering Display、MaxCLL/MaxFALL；按实际码流核对后再提交输出，不强制转 SDR。H.264 8-bit 仍明确拒绝 HDR 保留，不伪造源缺失的静态信息。
- 新增可选的 AV1 NVDEC 颗粒合成实验路径，逐文件实际预检 CUDA 硬解；HEVC NVENC 进一步允许 8-bit AV1 经 `scale_cuda` 在显存内转换 NV12→P010 后编码 Main10。10-bit 直通路线保留；不兼容组合按编码器能力禁用，预检失败不静默回退。
- AV1 烘焙接入共用队列计时与详细完成记录，支持单／多文件、实际编码参数与逐项结果；启动准备失败时正确结束计时。队列记录开关继续按原设置生效。
- 「所选视频信息」统一显示 SDR/HDR 实际位深，扫描标记仅在存在明确值时显示并放至行尾；未知状态不再显示「扫描标记 —」。无法可靠探测的视频流码率仍显示未知，不使用陈旧的 MKV 统计标签伪造数值。
- AV1 烘焙入口仅在选中视频和待处理队列均已确认是 AV1 时启用；非 AV1、混合队列及待探测文件直接将按钮置灰，添加／删除／切换文件、探测完成和队列状态变化时自动刷新。执行前的 AV1 校验仍保留。
- 用户在 RTX 4080 上确认 AV1 烘焙主要转码路线、R3 队列功能、R4 8-bit→HEVC Main10 NVDEC 和 R5 信息栏调整通过。此反馈不等同于所有 NVDEC Film Grain 与 libdav1d 逐像素一致、所有 HDR 源或所有编码参数组合已验证；正式运行包由 Windows runner 构建与审计。
