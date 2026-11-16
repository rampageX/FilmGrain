# Film Grain Studio v5.0.73

- NVEncC AI 视频插帧正式接入 10-bit HDR 保留：逐行 4:2:0、BT.2020 NCL、TV 范围的 HDR10(PQ) 与 HLG 素材，适用于 HEVC / AV1 NVENC 输出。
- 打通 VFG 10-bit NUT 传输与 AV1 的 10-bit FFV1 中间文件；保持 PQ/HLG 色彩标记，避免插帧中间环节被降成 8-bit。
- 保留 AI 插帧 Low / Medium / High 和整数 2～8 倍设置；未开放非整数倍目标帧率。
- PC(Full Range) HDR、真正的 12-bit / 4:4:4 HDR 暂不走 VFG HDR 路线，沿用现有 SDR 回退；现有 SDR、FRUC、SVP 不变。
- HDR10/HLG VFG 需用户配置已验证的实验版 NVEncC64.exe（对应 rampageX/NVEnc 6d387a31），并将随包的 FGS_VFG_HDR10_6d387a31.enable 放在该 EXE 同目录；普通 NVEncC 不自动启用此路线。
- RTX 4080 实测由用户确认通过；不同素材的 HDR10 静态 SEI、特殊音画时间轴及 HLG 绝对色度精度未作通用保证。
