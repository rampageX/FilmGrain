# Film Grain Studio v5.0.90

- R04a：OpenSVPFlow Y4M 生产者直连 AV1 NVENC 编码阶段，避免原本整片 FFV1/MKV 临时视频的写入与再次解码；保留 AV1 IVF、中间参数颗粒处理、最终音轨 remux 与视频/音频起点补偿。
- 仅在 OpenSVPFlow 源输出位深符合现有 HDR Preserve 色彩合同、且未选择 HDR→SDR 时启用新直连；NVEncC AI/FRUC、其它不匹配组合维持原有保护路线。R04b、R04c 与 R05 不在本次范围内。
- Windows 2022 R04a CI [#45136177066](https://github.com/rampageX/FilmGrain/actions/runs/45136177066) success：.NET Framework 4.8、Framework API 2996/2996、R04a 与 R01–R03/HDR-PQ/HLG/回调回归全部通过。RTX 4080 实际 SDR 1080p、PQ HDR10 4K、HLG 4K＋grav1synth 参数颗粒三段任务 3/3 exit=0；用户确认画面和音画同步正常。
- 用户实测三项队列总耗时 64.884 秒；未进行旧版同参数整任务对照、CPU/GPU 与磁盘峰值测量，不计算性能提升百分比。HDR10 静态 MDCV/CLLI 元数据未获独立实测验证。
