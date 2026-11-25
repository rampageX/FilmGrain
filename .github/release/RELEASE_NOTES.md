# Film Grain Studio v5.1.01

- R04c：识别含 NVEncC #813 修复的构建后，逐行 SDR NVEncC FRUC → AV1 NVENC 使用 raw NUT 管道直连，取消整片 FFV1 中转；未知或旧构建保留原路线。保留 IVF、grav1synth 参数颗粒、封装和音画补偿；不自动替换 NVEncC。
- RTX 4080 实机：8 秒 60fps（486 帧）、10 秒 47.952fps＋Classic35（487 帧）、完整 215 秒 60fps（12,896 帧），均正常完成，用户确认音画同步。完整任务 96.824 秒；特殊字符路径通过。
- Windows 测试 CI #45569568503 通过，Framework API 3037/3037，R01～R04c 等回归通过。上游 PR #814 待审核，NUT 管道既有告警仍保留；未独立确认下游逐帧完整性。
