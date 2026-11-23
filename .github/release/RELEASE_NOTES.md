# Film Grain Studio v5.0.91

- R04b：AV1 NVENC 对 NVEncC AI 插帧生成的 NUT 原始视频管道直接编码，移除原来整片 FFV1/MKV 中转，保留 IVF、可选 grav1synth、音轨 remux 和原 A/V 时间轴补偿。
- 保持 AI HDR 10-bit PQ/HLG 与现有色彩／元数据规则，包括源携带的 MDCV/CLLI；NVEncC FRUC (R04c)、R05 工作空间预检及其余旧路保护门槛不变。
- Windows 2022 CI #45142687854 success（.NET Framework 4.8、Framework API 2996/2996 以及 R01–R04b 回归）。RTX 4080 SDR/PQ/HLG 三项 exit=0，用户确认画面与声音正常，真实 HDR10 PQ 静态 MDCV/CLLI 注入 AV1 OBU，完整队列 11.261 秒。
- 没有与 v5.0.90 相同参数的整任务性能对照；CPU/GPU 与磁盘峰值、失败取消清理、R04b 本轮参数颗粒实片未单独测量，不报告提速百分比。R04 总任务需继续 R04c。
