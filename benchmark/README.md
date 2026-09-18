# Film Grain Studio Benchmark

本目录保存 FGS Benchmark 的实测报告与同帧截图，用于比较不同编码器、颗粒路线及质量设置的处理速度、输出体积和画面表现。

测试通过 `Utils/FGS_Benchmark.cmd` 与配套 `FGS_Benchmark.ps1` 调用 Film Grain Studio 的 StudioBridge 编码核心完成。每组目录保留原始统计、环境信息、源视频信息和截图，便于复核。

## 测试素材

| 测试组 | 素材 | 截图帧号 | 报告 | 同帧截图 |
|---|---|---:|---|---|
| Taylor Swift | **Taylor Swift - Look What You Made Me Do** 截取片段 | 313 | [Benchmark.md](TaylorSwift_1440p/Benchmark.md) | [Frame_313](TaylorSwift_1440p/Screenshots/Frame_313/) |
| LG OLED | **LG OLED DAYDREAMS** 测试片截取片段 | 543 | [Benchmark.md](LG_Daydreams_1080p/Benchmark.md) | [Frame_543](LG_Daydreams_1080p/Screenshots/Frame_543/) |

帧号采用 FFmpeg 的 **0 起始计数**，相对于截取后的测试片段，而非原始完整视频。每组的 `SOURCE.png` 与 B/Q 编号截图均取自对应视频的同一帧号。

Taylor Swift 片段用于观察肤色、暗部渐变及颗粒与压缩伪影；LG OLED 片段的选定画面包含天空、云层和逆光渐变，可观察颗粒分布与渐变保留情况。

截图是特定场景的比较样本，尤其适合暴露瑕疵，**不代表整段视频的平均画质**。正常播放中的颗粒运动、闪烁和连续性，需要结合视频判断。

## 测试矩阵

| 编号 | 编码器 | 颗粒路线 / 质量设置 |
|---|---|---|
| B01 | HEVC | Digital Grain |
| B02 | HEVC | FGSIM / VBR |
| B03 | HEVC | Grain Plate |
| B04 | AV1 | Digital Grain |
| B05 | AV1 | FGSIM |
| B06 | AV1 | Native Film Grain |
| B07 | x264 | Digital Grain |
| B08 | x264 | FGSIM |
| B09 | x264 | Grain Plate |
| Q01 | HEVC | FGSIM / VBR |
| Q02 | HEVC | FGSIM / CQ27（STANDARD） |
| Q03 | HEVC | FGSIM / CQ23（HIGH） |

B02 与 Q01 使用相同设置、独立运行，用于观察重复测试的波动。Q02/Q03 使用 StudioBridge 对应的完整质量预设，包含 QP 限制，并非只改变 CQ 数值。

当前两组测试使用 FGS v4.8.1、Benchmark v1.0；完整硬件、驱动、FFmpeg 版本与 StudioBridge SHA256 见各组 `Environment.txt`。

公共设置为 FAST、源分辨率与源帧率、MP4、自动码率；Digital Grain 强度 55、FGSIM Medium、Grain Plate 强度 85%。x264 使用 faster + tune grain + 单次 VBR；AV1 Native 使用 Classic35 / Fujifilm Eterna 250D。关闭 LUT、字幕、插帧、反交错、黑边和上传副本。详细设置以各组报告为准。

## 结果文件

每个测试子目录保留：

- `Benchmark.md`：结果表、统计说明及截图链接。
- `Benchmark.csv`：可用于后续分析的逐项测试数据。
- `Environment.txt`：测试硬件、操作系统、驱动及软件版本。
- `Source_Info.txt`：源视频的 FFprobe 信息。
- `Screenshots/Frame_<帧号>/`：`SOURCE.png` 及 `B01.png` 至 `Q03.png`。

`Work`、`Logs`、`Outputs` 不纳入仓库；原始视频和编码后的视频也不随测试结果分发。报告或 CSV 中如保留本机输出路径，仅用于记录该次运行，不是可下载的视频链接。

## 统计口径

| 字段 | 含义 |
|---|---|
| Elapsed | 完整 StudioBridge 运行耗时，单位秒；包含探测、准备、编码及封装，不包含结果探测、文件搬移和截图 |
| Resolution | 实际输出分辨率 |
| FPS | 实际输出帧数 ÷ Elapsed，表示端到端处理速度，不是视频播放帧率 |
| Frames | 实际输出帧数 |
| MPix/s | 输出宽 × 高 × FPS ÷ 1,000,000 |
| Output Size | 输出文件大小，单位字节 |
| Bitrate | 实际文件平均码率，单位 kbit/s，包含音频和容器开销 |
| Speed | 输出视频时长 ÷ Elapsed，单位为实时倍速（x） |
| Status / Error | 测试状态及失败原因 |

测试为单轮、固定顺序、无预热。硬件探测、缓存状态和系统负载均可能影响耗时，小幅速度差异不应直接作为稳定排名。不同素材的分辨率、帧率、输入编码与内容不同，不能仅凭 FPS 跨组比较编码效率。

不同颗粒路线的强度未经过视觉等效标定；自动码率也可能随编码器和素材参数变化。因此，本矩阵反映的是指定设置下的实际工作流表现，不是同码率、同颗粒强度的严格编码器画质排名。

## 截图说明

截图保存为 RGB24 PNG；AV1 使用 libdav1d 并启用 film grain，以呈现原生 AV1 颗粒。PNG 压缩无损，但 YUV 到 RGB 的转换不等于无损保存原始视频像素与位深。

当前两组素材均为 SDR BT.709。比较截图时建议保持相同显示比例，优先在 100% 比例下观察，避免缩放影响细颗粒和纹理的观感。更密集的颗粒或更大的文件，并不自动代表更好的主观画质。

## 仓库与发布包

**`benchmark/` 仅保留在 GitHub 仓库，正式发布 ZIP 排除整个目录。**

`Utils/FGS_Benchmark.cmd` 和配套 `Utils/FGS_Benchmark.ps1` 随正式包提供，用户可使用自己的素材运行测试。

素材名称仅用于标识测试来源；相关视频及画面版权归各自权利人所有。
