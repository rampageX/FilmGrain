# FGS · 4K GPU 流水线深度优化专项（Deep Dig）

- 立项：2026-11-28。
- 状态：G00 进行中；G01 待执行；后续为候选研究任务，尚未确认缺陷或承诺实现。
- 初始生产基线：v5.1.07，master / Release tag 对应提交 `72f498b253443982ec144178a6a7570777f0476a`。
- 仓库：rampageX/FilmGrain；底层优先使用现有 FFmpeg / libplacebo / NVEncC，必要时研究 rampageX/FFmpeg。
- 本专项独立于此前 R 系列慢路径修复与 FGB 兼容性跟踪。每轮先读 AGENTS.md，核对最新已验收生产基线；文档更新不改变发行版本。

## 1. 目标与边界

主要改善 RTX 4080 的 3840×2160 复杂编码流水线实际吞吐；保持 1080p 性能、现有输出效果及功能。优先消除已证实无必要的主机往返、中间文件、重复格式转换和显存读写；不以降低质量、改变算法、缩减编码功能或偷换编码参数换速度。GUI/CLI 共用核心，不引入 Python 生产依赖；不重构已经验证的路线，不放开 FGB 现有保护条件。BAT/VBS 无 BOM，PS1 UTF-8 BOM，覆盖中文及特殊字符路径。测试以 Windows、RTX 4080 为主；T600 仅在涉及其特有能力边界时安排验证。未经明确授权不创建分支、PR 或发布。只有在基准和画质证据支持时才改生产源码。

## 2. 立项依据

vidpipe 上游提交 `d7d317fff55fa5acdbc130ffe73eb0b5f1508c58`，MIT。参考 [性能日志](https://github.com/venki-byte/vidpipe/blob/d7d317fff55fa5acdbc130ffe73eb0b5f1508c58/docs/perf-log.md) 与 [CUDA 内核](https://github.com/venki-byte/vidpipe/blob/d7d317fff55fa5acdbc130ffe73eb0b5f1508c58/src/vidpipe/color/kernel_src.py)。

2026-11-28 RTX 4080 实机结果，原始证据包：
- L8R2：`Results_20261128_141744_0c7879.zip`，21/21 视频测试通过；540p→4K SRVGG 46.12→54.20 FPS。
- N6V3：`Results_4K_20261128_142732_d49299.zip`，27/27 视频测试、16/16 内核测试通过。原生 4K identity D→D_kernel 稳态 128.08→132.78 FPS；非默认 stream 无明显收益，E / E_kernel 未超越对应串行路线。
- 4K NV12 FP16 Limited：YUV→RGB 3.096→0.081 ms；RGB→YUV 3.038→0.051 ms。
- 4K P010 FP16 Limited：YUV→RGB 3.128→0.119 ms；RGB→YUV 3.199→0.118 ms。BT.709 内核验证不等于 BT.2020/PQ/HLG 全链验证。
- 实验仅证明相对 vidpipe 自身多次 PyTorch 算子实现的优化。FFmpeg 参照编码参数与计时口径不同，不作为等价加速比。NVENC 调用耗时增加，背压仅为待验证假设。
- 原始 ZIP 保留于用户侧；本文件记录可追溯文件名与数据，不将测试产物、模型或大型依赖提交仓库。

## 3. 总任务卡

| ID | 优先级 | 状态 | 内容与关闭条件 |
| --- | --- | --- | --- |
| G00 | P0 | 进行中 | 源码审计：GUI/CLI 实际计划入口、解码、处理、插帧、颗粒、边框、编码、预览参考；按路径记录设备/像素格式/位深/颜色/PTS/进程和工作文件边界，区分已验证融合、必要边界、疑似冗余、未确认路线，产出路线矩阵和可复现命令依据。 |
| G01 | P0 | 待执行 | 建立真实 FGS 4K 性能基准；单项/组合及纯编码对照、4K 30/60、SDR/PQ/HLG；记录日志、实际滤镜图、全任务耗时与稳态 FPS、CPU/GPU 编解码和显存、输出及时间轴；按瓶颈重排 G02–G08。 |
| G02 | P1 | 候选 | 消除不必要 CPU/CUDA/Vulkan 主机帧传输及中间工作文件；先复用已有 S4、R01/R02/R07/R08 路线，证明画质与时间轴等价。不得把 GPU 间映射等同于零复制。 |
| G03 | P1 | 候选 | 消除重复像素格式、位深、色彩转换；核对自动插入转换、chroma siting、范围、矩阵、量化及抖动，保留必要精度。 |
| G04 | P1 | 候选 | LUT/色彩纠正融合：先审计现有 CPU LUT 与 GPU 预览/libplacebo 实现；验证插值、强度混合、顺序、HDR 保留，再决定是否需新内核。 |
| G05 | P1 | 候选 | Digital Grain、Grain Plate、FGSIM 分开研究；优先避免 GPU 后下载及重复合成，保留随机性、时序、纹理、HDR luma delta 与边框/字幕顺序。 |
| G06 | P2 | 候选 | HDR→SDR 色调映射与后续 GPU 滤镜衔接，保持现有曲线、参数、HLG/PQ、亮度、色域及预览一致性。 |
| G07 | P2 | 候选 | 跨进程插帧 GPU 显存直通可行性；验证进程/设备支持、资源生命周期、同步、PTS/VFR/音频、取消及尾帧；无安全等价机制则关闭。 |
| G08 | P2 | 候选 | NVENC 吞吐与背压：同编码器/预设/RC/AQ/B 帧/多 pass/位深、同源输入，分别计时提交、等待、flush；若实际瓶颈在编码则可提前。 |
| G09 | P0 | 待执行 | 综合性能、画质、兼容性与用户实机验收；通过后才允许按 AGENTS.md 走正式发布。 |

每项按“审计→独立 A/B→最小改动→用户验收→文档结项”进行；无收益或不可等价的任务标记关闭及原因，不强行实现。G00/G01 未完成前不得按表盲目开改 G02–G08。

## 4. G00 首轮源码证据（已开始）

下列行号均指初始生产提交 `72f498b253443982ec144178a6a7570777f0476a`；为静态路径发现，不是已测性能缺陷。

| 路线 | 证据（dotnet/） | 初步分类 |
| --- | --- | --- |
| HEVC/H.264 NVENC S4 Grain Plate + 边框 | FilmGrain_NativeHevcCore.cs:405–439, 483–505, 1970–2050 | 已有 Vulkan blend→libplacebo/边框→CUDA 路线；普通 Plate 仍显式下载，Expand 裁剪部分组合也存在下载/再上传。比较适用条件，不移除保护。 |
| HDR/SDR FGSIM | FilmGrain_NativeHevcCore.cs:521–555；FilmGrain_NativeAv1Core.cs:580–593 | Shader 后显式下载；HDR 保留还包含 extractplanes/lut2/mergeplanes 的 luma delta；候选高优先级，必须保证原色度及量化行为。 |
| LUT+颗粒 | FilmGrain_NativeHevcCore.cs:489–496, 977–983；FilmGrain_NativeAv1Core.cs:554–581；FilmGrain_NativeX264DigitalGrainCore.cs:409–436 | gbrp16le→split→lut3d→blend→YUV；CPU 前缀和独立 GPU 处理相邻，先比较已有调色/预览实现。 |
| HDR→SDR / BWDIF | FilmGrain_SharedHdrPreprocessCore.cs:315, 552–553；FilmGrain_NativeHevcCore.cs:678–745 | 有 GPU 后下载的构图；已有 mergedHdr/mergedFrucHdr/inlineVulkanPlate 路径，部分条件仍创建 workfile；需按具体组合核对。 |
| Vulkan 缩放 | FilmGrain_Preprocessing.cs:726–752 | 常规 Lanczos/EWA 路径显式上传、libplacebo、下载；HQ 路线涉及高色度格式，不能随意合并。 |
| FRUC / 外部插帧 | FilmGrain_InterpolationFrameRateCore.cs:420, 726–727, 941；FilmGrain_NativeAv1Core.cs:267, 314 | FRUC 片段显式下载；外部路线存在 Y4M/NUT pipe；需要核对所有实际合并路径和 PTS 保障。 |
| CPU 编码 x264/x265 | FilmGrain_NativeX264DigitalGrainCore.cs:403–456, 525–541 | GPU→CPU 编码边界及多 pass 本身可能是必要操作；不能按 NVENC 直通策略机械删除。 |
| AV1 粒子烘焙成功案例 | FilmGrain_NativeAv1Core.cs:1037–1072 | 已有 NVDEC→scale_cuda→NVENC 直接硬件帧传递；软件编码分支需要下载。作为 G02 参考，不等于主 AV1 流水线。 |

已有文档必须交叉核对：[S4 GPU 组合审计](../film-border/FGS_GPU_COMBINATION_FALLBACK_AUDIT.md)、[FGB-02](FGS_FGB02_Full_Range_TODO.md)、[FFmpeg 9.1 HDR 互操作](FGS_FFmpeg91_HDR_Border_GPU_Interop_TODO.md)。既有 TODO 的状态不在本次立项中擅自修改。

G00 下一步：逐条追踪入口、共享 helper、执行器和最终参数，补齐 AV1 metadata grain / SVT-AV1、数字颗粒、TrueHDR、预览复用、音视频时间轴与 FFmpeg 底层实现；提取真实执行命令，制作 G01 可自动重复的基准矩阵。当前只完成首轮静态定位，未完成全路线审计，不宣布 G00 关闭。

## 5. G01 / G09 验收规范

- 先记录当前生产依赖版本、驱动、GPU、源文件属性及全部编码/滤镜参数；每组同一素材与同一处理语义，至少三轮，报告中位数、离散程度及全任务耗时；预热与启动/flush 口径分离。
- 基线：无滤镜 NVENC、单项 Resize/BWDIF/FRUC、LUT/CC、三类颗粒、边框；再按允许组合覆盖复杂路线与 HDR→SDR；SDR 8/10bit、PQ/HLG 10bit、4K30/60，1080p 作为回归对照。
- 保存源/输出 ffprobe、完整命令、滤镜设备与实际执行日志、帧数/PTS、时长、音频同步、码率/大小、CPU/GPU/VRAM；诊断采样只在实验执行期间开启。不把不同编码参数或不同输出语义的 FPS 当收益。
- 对可逐像素等价的滤镜比较帧 hash；浮点/量化差异事先定义误差与精度门槛，再做高光、灰阶、饱和色、颗粒、边框及字幕人工检查；非确定颗粒采用固定种子或统计与视觉对照。
- 记录实际提升、画质影响和适用条件；以用户认可的可重复净收益作为放行条件。无明显收益关闭，1080p 无明显倒退，禁止静默降位深、HDR 转 SDR、改范围或改变帧率。
- 自动测试应可从当前目录运行，支持 Unicode 和 `H:\Tester & ( ) ^ % ! -=\`，不强制下载新依赖、不做常驻监控。测试脚本、原始报告和结果可追溯到基线与候选提交；正式发行保持现有规范。
