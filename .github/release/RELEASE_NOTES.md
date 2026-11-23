# Film Grain Studio v5.0.89

- R03：AV1 NVENC＋FRUC Vulkan 的编码直连资格与 `SimpleSource` 最终容器直出资格分离；符合保护条件时，MP4（含 B 帧）与 MKV 输入无需生成整片 FFV1 中转，保持原 IVF 编码及最终 remux／参数颗粒路径。
- 沿用原有视频／音频起点补偿、SOURCE／AUTO 时钟、SDR 与 P010 HDR10 PQ／HLG 色彩标签；隔行、SFE、HDR→SDR、数字／FGSIM 像素颗粒、LUT、Resize、字幕、边框等原有保护条件不放开。
- Windows 2022 R03 CI #45126794637 通过：.NET 4.8 构建、Framework API 2996/2996、R03 12 组选路与保护门槛、R02／R01／HLG-PQ／回调回归。RTX 4080 实际 AV1 NVENC＋FRUC Vulkan 25→50 fps 六段编码均 exit=0，用户确认画面、声音与音画同步正常；六项队列共 9.895 秒。
- 本次合成 PQ10 样本不携带 MDCV／CLLI，未实测真实 HDR10 静态元数据源、参数颗粒 GPU 成品或旧版同参完整任务性能对照；不宣称旧路提速百分比。
