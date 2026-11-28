# FGS · 4K GPU 流水线深度优化专项（Deep Dig）

- 立项：2026-11-28。
- 状态：G00 进行中；G01 首批聚焦 LUT + Grain Plate；G04 为首个研究方向，G05 的 FGSIM 延后。后续为候选任务。
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
| G04 | P1（首项） | G04a/G04b 独立验证完成；G04c 待接入 | LUT/色彩纠正融合：先审计现有 CPU LUT 与 GPU 预览/libplacebo 实现；验证插值、强度混合、顺序、HDR 保留，再决定是否需新内核。 |
| G05 | P2（FGSIM 延后） | 候选 | Digital Grain、Grain Plate、FGSIM 分开研究；优先避免 GPU 后下载及重复合成，保留随机性、时序、纹理、HDR luma delta 与边框/字幕顺序。 |
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
| HDR/SDR FGSIM | FilmGrain_NativeHevcCore.cs:521–555；FilmGrain_NativeAv1Core.cs:580–593 | Shader 后显式下载；HDR 保留还包含 extractplanes/lut2/mergeplanes 的 luma delta；按用户决定延后，必须保证原色度及量化行为。 |
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

## 6. 2026-11-28 优先级调整：LUT + Grain Plate

用户决定先做 LUT＋颗粒 CPU/GPU 混合链，FGSIM 延后。先完成该范围的 G00/G01 切片，不等待其余路线全量审计；未获 A/B 证据前仍不修改生产逻辑。首批 SDR BT.709 Limited、HEVC NVENC Main10、普通 Grain Plate（扫描颗粒），不加 FGSIM、边框、插帧、字幕、CC 或缩放；随后再验证 CC、HDR PQ/HLG 和其他组合。

- 生产基线：`FilmGrain_NativeHevcCore.cs:483–505, 976–983, 991–1040`。主视频 CPU 转 gbrp16le→split→lut3d tetrahedral→blend 强度→P010→Vulkan；颗粒独立上传→blend_vulkan；普通 Plate 最后 hwdownload→NVENC。S4 已有 Vulkan→CUDA 的成功路径。
- 现有预览：`experiments/GpuSingleFrame/preview_win.c:616–678, 1894–1909` 已用 pl_lut_parse_cube、PL_LUT_NATIVE 及预混合 identity LUT 节点实现 GPU 调色强度，且有 Plate hook。预览并不自动证明与正式 CPU tetrahedral 等价。
- FFmpeg Fork master `8687110e9275d07e0eb5f244108b886938905d2c` 的 `libavfilter/vf_libplacebo.c:1848–1853` 支持 lut 与 lut_type=native/normalized/conversion；Stable 9.1 的实际二进制选项需由用户侧 -h filter=libplacebo 核对，当前仓库无 release/9.1 分支。先用现有滤镜，不预设需改 FFmpeg。
- 首批 A/B：A 现有 CPU LUT+Vulkan Plate+下载；B libplacebo GPU LUT+相同 Plate+下载；C 在 B 基础上复用 Vulkan→CUDA 直通；D 无 LUT 的 Plate 控制组。可另加 CPU LUT+直通以隔离两个变量。全部保持相同编码参数、输出格式、颗粒输入、强度、PTS/帧数。
- GPU LUT 的插值、色度重采样、范围、强度混合、DOMAIN_MIN/MAX 和越界裁切可能不同；先用标准 [0,1] standalone 3D CUBE 及预混合节点，强度 25/75/100，独立检查编码前画面。实际比较若不满足精度，不以速度替代画质；研究 tetrahedral shader 或保留原实现。HDR 的 LUT 前缀另行验证，禁止直接套 SDR native LUT。
- 先使用无新依赖的自动测试脚本收集滤镜选项、同源性能及编码前差异。测试方向不等于生产实现，FGSIM 不进入首批矩阵。

## 7. G04 T4K8 首轮实机记录（2026-11-28）

- 证据：`Results_G04_20261128_144443_e2b9ff.zip`；FFmpeg `9.1.git-FGS-47b92841e33b-20261122`。
- 15 轮中 9 成功、6 失败；成功组均通过 360 帧独立解码检查。端到端 FPS 中位数：A CPU LUT+Plate+下载 4.9535；B GPU LUT+Plate+下载 5.0844（+2.64%）；D 无 LUT+Plate+下载 5.3594。C/E 直通均 0 帧，`cuExternalMemoryGetMappedMipmappedArray` / `CUDA_ERROR_INVALID_VALUE`。
- 编码前 FFV1 8 帧对照：LUT PSNR 31.557318 dB / SSIM 0.988887（V 28.829117 dB）；Plate 后 31.587256 dB / 0.988786。GPU LUT 当前不满足等价替换要求。
- **实验设计偏差**：T4K8 给全部组强制 `vulkan=vk:0,disable_multiplane=1`，而生产普通 Plate 使用 VulkanDevicePolicy.Spec，不加该标志；S4 按适用条件添加 disable_multiplane、探测 disable_host_image_copy，并在 blend_vulkan 后经过 libplacebo/边框输出才派生 CUDA。因此 5 FPS 原因未明，T4K8 不能作为生产性能基线。不能直接认定互操作底层补丁失效。
- 下一轮 G04a：使用明确的 NVIDIA 设备名并保存 verbose 设备日志；先以默认 Vulkan 配置复现生产普通 Plate，短片探针比较 disable_multiplane、支持时 disable_host_image_copy、blend_vulkan 后直接派生 vs libplacebo P010 中继（S4 参考），再跑成功路线。纯 NVENC、上传/下载、Plate 原尺寸/缩放、blend、完整编码分开计时，查清异常低速。对比 10-bit 编码前帧的 hash/像素差异；中继不得改变颗粒/色彩。失败路线不重复长测。
- 下一轮 G04b：使用 identity LUT、0/75/100% 和灰阶/色条，分别比较 CPU 与 GPU 的矩阵/范围、4:2:0 重采样、LUT 插值、量化及裁切；先验证无 LUT/identity 对照，避免直接将差异归咎于插值。G04a 优先，FGSIM 仍延期。没有生产源码修改。

## 8. G04a Q7M2 独立诊断包（2026-11-28，待实机）

- 核对 tag v5.1.07 与文档提交 b56af154：3 个后续提交仅涉及 TODO 文档及索引，生产源码仍为 72f498b2。已重读 AGENTS.md、普通 Plate/S4 图和设备参数、S4 组合审计与 FFmpeg 9.1 互操作 TODO。
- 新发现：FFmpeg Fork 构建对应提交 [47b92841e33b 的 hwcontext_vulkan.c](https://github.com/rampageX/FFmpeg/blob/47b92841e33b/libavutil/hwcontext_vulkan.c) 读取 disable_multiplane（1945–1952 附近），但未读取 disable_host_image_copy。其 HOST_IMAGE_COPY 扩展选择受 ReBAR 判断影响（1012 附近）；初始化成功并不证明未知字典选项有效。Q7M2 分别探测 D/M/H/MH 并检查实际 extension verbose 日志；H/MH 若无实际禁用证据，跳过重复长测。不能据此宣称已定位低速根因，也不在本轮修改生产检测。
- 测试包 FGS_G04a_Q7M2_Test.zip：现有 Python 标准库 + 同一 FFmpeg 9.1 构建；指定原视频与原 Plate，不下载依赖。P00/P01 源解码/纯 HEVC NVENC，P02 上传下载，P03 原 Plate 解码/上传下载，P04 实际需要时的 Vulkan bilinear 缩放，P05 blend GPU/null 与下载/null，P06 普通 Plate；各设备配置分别探测下载、直接 CUDA、libplacebo P010 单输入中继 CUDA。8 帧探针，120 帧 × 3 轮默认性能测试（可选 360），成功编码独立解码计数；预编码 10-bit FFV1 hash/PTS/PSNR，色彩标签单独记录。GPU-only null sink 仅作诊断，不保证逐帧同步。原 T4K8 脚本和参数未能检索，默认选统一 FAST-style Main10 p5 VBR 20M/qres/LA16/AQ8/B4，85% opacity；实际参数全部记录，不能直接声称与 T4K8 严格同参数。
- 已完成 Python 语法和模拟流程检查（12 个模拟性能组、6 组模拟画质对照、失败直通及无效 host 标志长测跳过、特殊字符路径、ZIP 校验）；模拟数值不作实测数据。Windows GPU 实机未执行。等待 Results_G04a_*.zip，G04a 未关闭，G04b 和 FGSIM 延后，无生产源码改动。

### 8.1 R9V4 替代 Q7M2

用户补充原 `FGS_DeepDig_G04_T4K8(1).zip`，核实其为全自动合成素材测试。Q7M2 的手动输入和不同编码参数属于交付偏差，废弃。R9V4 完整复用原 L8R2 Python 查找逻辑、FFmpeg 查找顺序、testsrc2 4K30 360 帧及 1080p30 60 帧 noise seed=12345 的 H.264 NVENC 素材生成命令，保持 35% Plate、HEVC Main10 P5 HQ VBR 25M/max50M/buf100M/B4/LA16/AQ8、MP4 输出，360 帧×3；G04a 不生成 LUT。明确 RTX 4080 设备名，保留各组合短探针，H/MH 无扩展禁用证据时跳过长测。记录生成命令及素材 hash。BAT/PS1 编码及 ZIP 检查、Python 语法检查通过；模拟子进程测试因环境耗时超时，未完成全矩阵模拟；已走通自动定位、生成及部分互操作探针，Windows GPU 实测待回传。无生产源码变动。交付 `FGS_G04a_R9V4_Test.zip`，双击 `Run_G04a_R9V4.bat`，仅回传 Results_G04a ZIP。

### 8.2 R9V4 实测与 K6P3 直通复验（2026-11-28）

- 原始证据：Results_G04a_20261128_151025_96024c.zip；RTX 4080 / 617.14，FFmpeg 9.1.git-FGS-47b92841e33b-20261122，SHA256 2dcfa6d24b5076ab9f92b22655d241d76d549fdbe0e3ae7805cf5af9b5d29331。30/30 性能轮通过，实际编码均独立解码 360 帧；P00/P01/P02/P03 decode/P03 native/P04/P05 GPU/P05 DL/P06/M DL 中位 FPS 分别 271.5641/92.9026/110.0127/377.6385/247.5803/155.436/118.0341/86.1834/67.2234/5.1012。
- P06 与 I_M_DL 的实际命令除输出路径和 disable_multiplane=1 外一致，67.22→5.10 FPS（13.18 倍差距）。上轮约 5 FPS 是实验设备参数导致，不是普通 Plate 生产路线。底层具体耗时机制仍未知。P05 GPU null sink 与 NVENC 口径不同，不相减。
- D/H 直通和中继均失败，日志为一张多平面图像对应两平面不能映射 CUDA；M 两种直通均 CUDA_ERROR_INVALID_VALUE / cuExternalMemoryGetMappedMipmappedArray；MH 两者均 8/8 帧编码及独立解码通过，日志明确 Transferred Vulkan image to CUDA! / Using input frames context (format cuda)。不能将 transfer 等同严格零复制。
- 所有配置仍宣告 VK_EXT_host_image_copy；MH 成功与静态源代码未读取选项存在待核实差异，不能断言 host flag 无效或已禁用该 feature。R9V4 以扩展是否消失作为 H/MH 长测门槛，误跳过成功的 MH 路线。M/D 下载 8/8 hash 与 PTS 一致；单输入 libplacebo 中继 0/8 hash 相同，PTS 一致，Y/U/V/avg PSNR 70.652452/42.769296/38.265134/44.721546 dB，不选为普通 Plate 等价路线。
- K6P3 独立复验包 FGS_G04a_K6P3_Test.zip：直接复用 R9V4 全自动素材/编码/启动器；三轮交替 8 帧 M、M0（host=0）、MH（host=1）直接 CUDA 探针，记录实际成功而不以扩展日志筛选；成功过的路线及 D/MH 下载基准进入 360 帧×3 长测，连续两次失败停止重试；32 帧 FFV1 编码前 hash/PTS/PSNR（CUDA 直通额外下载仅用于质量采样），完整命令与独立输出解码计数。已验证 Python 语法及 ZIP/BOM，待 Windows 实机。G04a 未关闭，G04b 继续延后，不改生产源码，不创建分支或 PR。

### 8.3 K6P3 实机复验：G04a 独立诊断通过（2026-11-28）

- 证据：Results_G04a_20261128_152031_339544.zip；FFmpeg 9.1.git-FGS-47b92841e33b-20261122，SHA256 2dcfa6d24b5076ab9f92b22655d241d76d549fdbe0e3ae7805cf5af9b5d29331。4K30 SDR BT.709 Limited，1080p Plate，35%，HEVC Main10 P5，360帧×3轮。
- M、M0、MH 交替短探针分别 0/3、0/3、3/3 成功；M/M0 为 cuExternalMemoryGetMappedMipmappedArray / CUDA_ERROR_INVALID_VALUE；MH 日志记录 Transferred Vulkan image to CUDA!，NVENC 输入 cuda frames context。9/9 长测成功，全部独立解码 360 帧。P06 67.1103 FPS（5.3643s），MH 下载 66.7993 FPS（5.3893s），MH 直通 82.7985 FPS（4.3479s），对 P06 吞吐 +23.38%。两标志同时启用消除 M 单标志的异常低速；不宣称已证实底层耗时机制或严格零复制。
- MH 下载及 MH 直通编码前采样均为 32/32 hash 相同、PTS 相同、Y/U/V PSNR inf；FFprobe 一致为 10-bit BT.709 Limited，长测 3840×2160、30fps、12s。三条路线首轮输出文件均 41,207,971 字节（未保存输出文件 hash，不声明码流完全相同）。不使用 libplacebo 中继。
- 修正先前对 disable_host_image_copy 可能无效的推测：三个交替探针明确显示参数值影响实测结果，即使设备日志均宣告 VK_EXT_host_image_copy；静态源代码与二进制行为差异尚待追溯，不据此移除任何标志。用户特别要求保留此前耗费大量实机验证的两个参数及既有成功路线。禁止全局修改 Vulkan 设备策略、S4、HDR PQ/HLG、FRUC 等已有验收路径。
- G04a 独立诊断目标完成；仅登记候选路线：普通 SDR HEVC Main10 Plate blend_vulkan 后直接 hwupload=derive_device=cuda，显式同时启用两标志，省去 CPU 下载；尚未接入生产。G04b 优先验证 LUT 色彩等价性；生产接入前需真实素材、有音轨、4K60/1080p、组合及完整时间轴回归。保持旧路线不变，不擅自扩展 H264/HDR/AV1。无生产源码改动、无新分支/PR。

## 9. G04b H3N8 LUT 色彩等价性诊断（2026-11-28，待实机）

- 开工读取 AGENTS.md 和总卡，生产仍为 v5.1.07/72f498b2；重读预览 preview_win.c 的 LUT 节点预混合及 PL_LUT_NATIVE 设置，并对照 T4K8 原脚本、K6P3 运行环境及实际 libplacebo 选项。上游 renderer.h 区分 renderer LUT 的 native gamma RGB、normalized linear RGB；不将 frame LUT 的 raw image 语义混同。
- 独立包 FGS_G04b_H3N8_Test.zip，复用 K6P3 自动定位、Python 和 T4K8 固定合成素材；生成 576×288 10-bit BT.709 Limited 色块、灰阶、斜坡、色度棋盘格，另测原 4K30 前4帧。CPU gbrp16le tetrahedral、0/25/75/100% 逐像素混合、identity 和预混合25/75%节点；GPU libplacebo native/normalized、无 LUT、identity、默认及 bilinear/disable_linear/sigmoid=0 组合，显式 BT.709 CPU 矩阵/范围往返、RGB16 桥接；RGB16 输入输出隔离和 5^3 非线性交叉项 LUT 的三线性/四面体对照。
- FFV1 无损输出，逐帧 SHA256/PTS、分量 PSNR、色块中心码值、ffprobe 标签/帧数、日志与实际命令；失败组合保留日志，不影响其余组。OK 仅是命令成功，非画质放行。先定位差异，之后明确候选的画质标准再长测，不以单一 PSNR 放行。
- 本地 Linux FFmpeg CPU 色块对照、显式矩阵、RGB16 插值、hash/PTS/PSNR 与色块采样检查通过；Python 语法、BAT 无 BOM/PS1 BOM、ZIP 校验通过。Windows GPU 未执行。双击 Run_G04b_H3N8.bat，回传 Results_G04b_*.zip。无生产源码或全局设备策略改动，无新分支/PR；G04a 双标志直通路线不参与本轮颜色诊断，FGSIM 仍延后。

### 9.1 H3N8 实测及 J4C7 复验（2026-11-28）

- 证据 Results_G04b_20261128_152858_72e0f2.zip；FFmpeg SHA256 同 K6P3。60/60 render OK；52/52 comparison OK（命令成功，不等价于像素验收），PTS 一致。4K source 75% native 31.557272 dB，native tuned 31.411917 dB，normalized 53.810407 dB；色块最大中心平均码值偏差分别 82、82、1（10-bit）。CPU 0% 等于 CPU RGB 往返；显式 BT.709 与 CPU 自动矩阵往返 hash 相同。无 LUT GPU 往返也有色度差异，不能归因于 LUT 插值。
- RGB16 stress 对照：GPU vs CPU tetrahedral 75.930247 dB、GPU vs CPU trilinear 47.503555 dB、CPU tetrahedral vs trilinear 47.413919 dB（chart）。支持 GPU 插值近似 tetrahedral，不据此声称逐像素完全一致。CPU 节点预混合与逐像素 blend 仍有微小量化差异。
- 复查 FFmpeg vf_libplacebo.c：LUT 绑定 pl_frame；FGS preview_win.c 绑定 pl_render_params，native 语义不同。参考 https://github.com/haasn/libplacebo/blob/master/src/include/libplacebo/renderer.h 。normalized 才是当前 FFmpeg RGB CUBE 的候选。H3N8 RGB bridge 日志显示 gbrp16le GBR/full -> P010 GBR/full，随后 setparams 仅重标记为 BT.709 limited，属于实验设计错误，不能据此否定 RGB 桥接。J4C7 显式 scale=out_color_matrix=bt709:in_range=pc:out_range=tv,format=p010le 后设置标签。
- FGS_G04b_J4C7_Test.zip 复用原 H3N8 自动输入、环境、日志及 launcher；52 renders / 40 comparisons。normalized identity/25/75/100/stress，修正 RGB bridge 对照，混合 RGB/YUV 路线及独立 chroma_location、up/downscaler、clip、disable_linear/sigmoid 参数。仍为颜色诊断，不做长时间性能测、不接 Plate/CUDA、不改生产及既有两个 Vulkan 标志。Python 语法、矩阵 dry-run、CPU 显式转换代理、BAT/PS1 编码及 ZIP 校验通过，GPU 实机待回传。双击 Run_G04b_J4C7.bat，回传 Results_G04b ZIP。G04b 未关闭。

### 9.2 J4C7 实测及 V8L2 完整链吞吐验证（2026-11-28）

- 证据 Results_G04b_20261128_153637_94290f.zip；FFmpeg SHA256 同 K6P3。52/52 renders、40/40 comparisons 命令成功，PTS 全一致，非像素完全等价。RGB16 bridge source PSNR：plain 75.257197、identity 75.257488、25% 78.938025、75% 66.395049、100% 68.106521、stress 70.590058 dB；chart 对应 74.589130/74.589130/67.955319/72.019538/66.412938/73.892060 dB；平坦色块中心平均最大码值偏差均为 1。显式 RGB full→BT.709 limited 转换解决 H3N8 bridge 错误。Normalized 75% 53.810407 dB（source），bilinear 44.609221、bicubic 43.289199、nearest 44.218037、center 51.019501；clip/left/nolinear 与默认一致，暂不继续调这些参数。混合 RGB/YUV 路线仍不及完整 RGB bridge。不能用 PSNR 或色块均值替代全部像素验收。
- V8L2 独立包 FGS_G04b_V8L2_Test.zip：复用 J4C7 CPU LUT 与显式 RGB16 bridge、K6P3 Plate/编码/自动输入。CPU/RGB bridge/normalized 三种 LUT × 默认 Vulkan 下载或 MH 双标志 CUDA 直通；75% LUT、35% Plate，4K30 360帧×3轮，先8帧编码及独立解码探针，成功路线进行32帧 Plate 后编码前 FFV1 hash/PTS/PSNR 及长测；连续两次失败停止。CPU_DIRECT 是判断 LUT 增量收益的关键参照。Normalized 仅诊断，不放行画质。RGB bridge 有额外 RGB16 上传/下载，必须先证实完整链净收益；否则保留 CPU LUT。沿用现有 T4K8 生成器、L8R2 环境和编码参数，不下载新依赖。Python 语法、6组图构造、模拟失败跳过/轮转、BAT/PS1 编码与 ZIP 检查通过；未执行 Windows GPU。双击 Run_G04b_V8L2.bat，回传 Results_G04b ZIP。G04b 尚未关闭，不改生产及既有 S4/HDR/FRUC 设备标志，不新建分支或 PR。

### 9.3 V8L2 实测：当前 GPU LUT 方案不放行，CPU LUT + CUDA 直通通过独立验证（2026-11-28）

- 证据 Results_G04b_20261128_154355_c783c7.zip；config.json 确认 FFmpeg SHA256 同 K6P3、RTX 4080、4K30/360 帧×3、LUT75%/Plate35%、HEVC Main10 P5 HQ VBR25M/max50M/buf100M/B4/LA16/AQ8。6/6 短探针、6/6 质量采样、18/18 性能轮均成功，全部长测独立解码 360 帧，BT.709 Limited 10-bit 标签一致。
- 中位 FPS（wall，包含启动和 flush、不含独立解码）：CPU_DL 36.6652、CPU_DIRECT 44.3094、RGB_DL 31.6647、RGB_DIRECT 36.1858、NORM_DL 62.7364、NORM_DIRECT 80.2645。CPU_DIRECT 对 CPU_DL +20.85%；RGB_DIRECT 对 CPU_DIRECT -18.33%。RGB bridge 不具备净性能收益，关闭此实现。Normalized 保留后续研究价值，但不得替换生产 LUT。
- quality.json：CPU_DIRECT 对 CPU_DL 编码前 32/32 hash 相同、Y/U/V PSNR inf；RGB_DL/DIRECT 均 0/32，平均 PSNR 66.373389 dB；NORM_DL/DIRECT 均 0/32，平均 53.763366 dB（V 48.588465 dB）；全部 PTS 相同。未开展真实素材的主观画质验收，不放行两种 GPU LUT。G04b 现有 libplacebo 候选评估完成，后续融合颜色转换/重采样与 LUT 的等价 GPU 实现另列研究，不阻塞 G04a。
- 下一阶段 G04c：仅准备 G04a 已验证路线的生产最小接入及实机验收，普通 SDR BT.709 Limited progressive HEVC Main10 Grain Plate（无 LUT/原 CPU LUT），双标志同时启用，blend_vulkan 后直接派生 CUDA，不加 libplacebo 中继；严格检查末端 CPU 滤镜、像素颗粒强度、帧率、输入范围等边界，不适用则保持原图与参数。旧 S4/HDR/FRUC、H264/AV1、字幕、复杂几何、其它颗粒、既有设备策略不动；真实素材及音轨、4K30/60、1080p、PTS/帧数/色彩、取消及特殊路径回归后再考虑发布。GUI/CLI 共用核心。生产源码本轮未改，未建分支/PR；后续 Windows CI 或分支须明确授权。
