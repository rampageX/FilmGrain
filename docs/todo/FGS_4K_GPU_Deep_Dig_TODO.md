# FGS · 4K GPU 流水线深度优化专项（Deep Dig）

- 立项：2026-11-28。
- 状态：G00 进行中；G01 首批聚焦 LUT + Grain Plate；G04 已随 v5.1.08 发布；G05 的 FGSIM 延后；G06a 独立 A/B 及缩放隔离完成；G06b 独立缩放验证完成；G06c 生产接入及颗粒选择提示待执行。
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
| G04 | P1（首项） | 已发布 v5.1.08（8d3f0ec5），见 §9.9 | LUT/色彩纠正融合：先审计现有 CPU LUT 与 GPU 预览/libplacebo 实现；验证插值、强度混合、顺序、HDR 保留，再决定是否需新内核。 |
| G05 | P2（FGSIM 延后） | 候选 | Digital Grain、Grain Plate、FGSIM 分开研究；优先避免 GPU 后下载及重复合成，保留随机性、时序、纹理、HDR luma delta 与边框/字幕顺序。 |
| G06 | P2 | G06a/G06b 独立验证完成；G06c 下一项 | HDR→SDR 色调映射与后续 GPU 滤镜衔接，保持现有曲线、参数、HLG/PQ、亮度、色域及预览一致性。 |
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

### 9.4 用户决策：Normalized GPU LUT 拟作为默认，修订 G04c 验收目标（2026-11-28）

- 本节取代 9.3 中“Normalized 不得替换生产 LUT”的阶段性决定，以及原 G04c 仅接入 CPU LUT + CUDA 的范围限制。历史实验数据不变。用户同意将创意 LUT 与无损传输分别验收：创意 LUT 不强求 CPU/GPU 逐像素一致，GPU Normalized 经真实素材视觉验收及兼容性验收后作为适用路线默认；原 CPU tetrahedral 保留为可选精确模式。显存传输优化仍须逐像素等价。
- 依据 V8L2：CPU_DL 36.6652、CPU_DIRECT 44.3094、NORM_DIRECT 80.2645 FPS；Normalized 相对原链 +118.91%，相对 CPU_DIRECT +81.15%；编码前综合 PSNR 53.763366 dB、V 48.588465 dB，H3N8/J4C7 平坦色块中心平均偏差不超过 1 个 10-bit 码值。50 dB 综合 PSNR 是建议的 FGS 初筛门槛而非行业标准或自动放行条件；低于门槛或局部/分量异常须专项分析，不能仅凭均值否决或接受。色度重采样为剩余偏差的主要待验证解释，不声明肉眼不可见。
- G04c 第一步：独立真实素材 A/B（先 LUT 单独，后 LUT+Plate），使用 FGS 实际 CUBE 与强度 0/25/75/100%，覆盖肤色、灰阶、高光暗部、天空、霓虹、高饱和边缘、渐变及细纹理；同帧无损输出、随机 A/B/滑动及 1:1 对照，保存 PSNR 分量、局部异常、范围/矩阵/标签、裁切、PTS；0% 应按既有 LUT bypass 语义处理。核对实际 LUT DOMAIN_MIN/MAX、越界、兼容 LUT 转换、预混合节点强度与预览，确认适用边界。只有用户确认视觉表现后接入默认路线。
- G04c 第二步：初期范围限已验证的普通 SDR BT.709 Limited progressive HEVC Main10 Grain Plate，含无 LUT/CPU 精确/GPU 高速；单独 LUT 的 GPU 路线也先独立验证，未通过不得扩大默认适用范围。GPU 用 FFmpeg libplacebo lut_type=normalized 与既有预混合节点技术；不采用已淘汰的 RGB16 桥接或 native YUV LUT。直接 CUDA 使用 disable_multiplane=1,disable_host_image_copy=1，严禁全局修改、删减或重排既有成功设备策略。GUI/CLI 同核心、预览与编码一致性需验收；不支持的 LUT、色彩、硬件或流程组合沿用原 CPU 路线，明确显示实际模式。HDR PQ/HLG、S4、FRUC、H264/AV1、FGSIM、复杂几何/字幕等不擅自扩展。4K30/60、1080p、音画同步、取消、特殊路径及旧路线回归通过后才发布。
- 交接时仅更新文档，无生产源码、分支或 PR 变动。下一窗先读取 AGENTS.md 及本总卡，核对 v5.1.07/72f498b2 与最新 master；先交付独立视觉验收包，未经明确授权不创建分支/PR/触发 Windows CI。测试素材优先复用既有全自动合成测试并提供真实片段选择机制，不强制下载依赖。

### 9.5 G04c M9K4 独立真实素材视觉验收（待实机）

- 已核对 v5.1.07/72f498b2 与 master/6ff4cbd3，仅 TODO 文档和索引变更；完整读取 AGENTS.md 和本卡。用户提供原 FGS_G04b_V8L2_Test(1).zip，继承其 CPU tetrahedral、GPU normalized、Plate blend、MH 双标志和 L8R2 启动器，不重新下载依赖。
- 交付 FGS_G04c_M9K4_Test.zip（SHA256 8a4306d3f43f5986d3cabde2db053c45d596830fb9b6e620a60c984482db8552）。选择多个真实 SDR BT.709 Limited progressive YUV420 8/10-bit 源、一个实际 FGS standalone 3D CUBE、可选真实 Plate（取消使用 V8L2 固定种子 Plate），每个源默认三个可指定的时间位置，每处连续四帧；0% 真 bypass，25/75/100% CPU blend 与 GPU 节点预混合。仅标准 [0,1] 输入域和有限 [0,1] 节点，非标准 LUT 不裁切或静默替换。单独 LUT 和 LUT+Plate 35% 分别测试；Plate CPU/GPU 各自比较默认 Vulkan 下载与 MH CUDA 直通后下载采样，要求 hash/PTS 等价。无损 FFV1 保留本地，第二帧统一 BT.709 YUV→RGB 16-bit PNG 供 localhost 浏览器盲测，屏幕实际显示精度另计；随机 A/B、滑动、单张、1:1、拖动及备注。回传仅日志、PSNR、帧 hash/PTS、局部 4×4 区域及 nominal range 统计、盲测答案和映射，不含实际媒体。50 dB 仅初筛。该包不进行生产 NVENC 编码或测速。
- 本地 Linux CPU 代理输出、10-bit 标签、帧 hash/PSNR、PNG、LUT/滤镜图矩阵、局部码值统计、特殊字符路径及 localhost API 验证通过；Python/JS 语法、BAT/PS1 BOM、ZIP 检查通过。Windows GPU、浏览器实际交互及 PowerShell 启动器待验收。G04c 第一阶段尚未关闭，等待 Results_G04c_*.zip。无生产源码变动，未创建分支或 PR；文档更新使用 skip ci。

### 9.6 M9K4 第一轮真实素材实测（2026-11-28）

- 证据 Results_G04c_20261128_163415_a08080.zip；FFmpeg SHA256 同 K6P3/V8L2。AV1_SDR.mp4，1920×1080/60fps/10-bit BT.709 Limited，缺 transfer/primaries 经用户确认 SDR，取 2/5/8 秒各4帧；Fujiflex-Crystal-Archive-New-Version.cube，33³；使用自动生成的固定 Plate，35%。66 个无损输出的格式/色彩标签均为 yuv420p10le/BT.709 Limited。
- 24/24 盲测答案 same（含6组零强度控制），45/45 PTS 一致；27/27 严格等价比较通过（108/108 帧 hash 一致，含21组显存传输）。18组非零强度 CPU/GPU 比较中17组综合 PSNR ≥50dB，范围49.901986–62.362243dB。单独 LUT 25/75/100% 最低分别58.622404/52.254194/49.901986dB；Plate 后最低58.968583/55.619903/53.545125dB。
- S02_LUT_100：综合49.901986dB，Y/U/V 56.186662/44.174551/48.695926dB；第二帧 Y/U/V 平均绝对误差1.045/2.651/1.498码值，最大55/84/61码值，U 最大误差区域平均8.062码值。该帧两边均无 nominal range 越界；盲测 same。全组累计 Y 越界计数两边均991340，U/V 均0；不将累计计数相同解释为逐像素裁切等价。保留低于初筛门槛的记录，不单凭0.1dB差距否决创意 LUT。首轮视觉验收通过，未覆盖不同真实源、真实扫描 Plate 和4K视觉验收；复用 M9K4 补测，无须开发新包。完成后进入生产最小接入，未改生产源码/分支/PR，文档提交使用 skip ci。

### 9.7 M9K4 第二轮真实素材验收：视觉阶段通过（2026-11-28）

- 证据 Results_G04c_20261128_165102_35e8cc.zip；FFmpeg SHA256 同 V8L2。真实 VP9 UHD SDR 3840×2160 24000/1001 8-bit BT.709 Limited，缺 transfer/primaries 经用户确认 SDR；取22.097/55.242/88.388秒各4帧。VEL_Warm_Film.cube 33³，35% Plate 仍为固定种子合成素材（make_plate.argv.json）。特殊字符路径 H:\\Tester & ( ) ^ % ! -=\\。
- 24组视觉对照，18组 same、6组 skip（用户说明全黑内容跳过），无可见差异报告；非零强度中14组 same、4组 skip。45/45 PTS 一致，27/27 严格等价比较108/108帧 hash一致。18组非零强度 CPU/GPU 综合PSNR 59.975215–69.046313dB，均超过50dB初筛线。第一轮用户补充：素材有重颗粒，使用屏幕放大镜仍看不出差异。
- 两轮独立真实素材视觉验收通过，结束 G04c 第一阶段，不再提高画质门槛或重启 RGB16 桥接。实际扫描 Plate、生产 NVENC 编码、4K30/60、1080p、预览一致性、音画同步、取消、特殊字符路径、GUI/CLI 核心及旧路线回归仍属于第二阶段验收。按9.4严格限定初期适用范围，保留CPU精确模式及两项 Vulkan 标志；未经授权不建分支、PR或触发Windows CI。生产源码尚未改动。

### 9.8 G04c Q8V6 生产候选（待 CI 和实机）

- 用户授权 test/g04c-gpu-lut 和 Windows CI。核对 v5.1.07 完整源码及后续文档提交。仅对 RTX 4080、HEVC Main10、BT.709 Limited progressive、原帧率、软件解码、无其它滤镜组合的普通路线放行。默认 GPU Normalized；右键 LUT 勾选框可选 CPU tetrahedral，LUT_PROCESSING_MODE / FG_LUT_PROCESSING_MODE。标准独立 CUBE 节点 [0,1]、2–65 阶，0% bypass；超出条件保留原 CPU 路线。无 LUT/CPU/GPU 的普通 Plate 直通均保留两标志；LUT 单独 GPU 下载。颗粒采样图保留 CPU 帧。LUT 四档预览使用原尺寸 FFV1 取帧和共同 Normalized 图；输出预览沿用生产计划；独立高级实时预览保留旧渲染器并提示差异。无版本变更，无正式发布；CI 和 RTX 4080 实机验收待执行。

### 9.9 G04c 正式版收尾（2026-11-28）

- M4P6（7708f88a）用户验收通过。4K 23.976 fps + Kodak Plate 的 GPU Normalized／CPU tetrahedral 均走 Vulkan → CUDA，任务耗时 10.824／14.029 秒。4K60 10-bit SDR 和 1080p VFR 独立 GPU LUT 成功，均输出 HEVC Main10 并复制 AAC 音轨。
- AV1 外框保护、Grain Table 默认显示全部、LUT G/C 显示和 BT.2390 大小写随 v5.1.08 收尾。日志覆盖特殊路径和音轨复制，未单独记录播放验收及取消清理。

- 正式版发布提交 `8d3f0ec5635b3690ca53216087cf68fd7acd6953`，[v5.1.08 Release](https://github.com/rampageX/FilmGrain/releases/tag/v5.1.08)，Windows CI `46142945244` 成功。Stable 与完整源码均核对两份 Dune2 表已移入 `2160p` 并带 `00_3840x2160_` 前缀，测试分支已删除。

## 10. G06 HDR→SDR GPU 滤镜衔接（2026-11-28）

- 基线 v5.1.08 / `8d3f0ec5635b3690ca53216087cf68fd7acd6953`，开工时 master 只领先一个 TODO 文档提交。SharedHdrPreprocessCore.cs:321 的 Vulkan 色调映射后下载，:427 经过 planar 10-bit CPU 格式，NativeHevcCore.cs:518–527 的 Plate 前上传及合成后下载是首批目标；:461 的 G04c 适用条件明确排除 HDR→SDR。现有合并编码已去除逐行 HDR 中间文件，保留现有时间轴和颜色处理。
- Q7M2 实测证据 Results_G06_20261128_201743_d5b43e.zip；FFmpeg SHA256 `2dcfa6d24b5076ab9f92b22655d241d76d549fdbe0e3ae7805cf5af9b5d29331`。4K PQ 30fps + Kodak Vision3 250D Plate 35%，原链/双标志/前端/末端/双端中位 FPS 30.2242/30.8700/41.9844/34.0144/51.3421，双端 +69.87%；无 Plate 58.5007→65.0736。1080p 三种 PQ/HLG 素材无 Plate 113.6344→123.2177、127.6848→147.9808、166.7067→198.4264 FPS，帧 hash/PTS 一致。1080p 使用 4K Plate 时，双标志本身导致与原链的像素差异；仅去除传输并非差异来源。
- R8K4 实测证据 Results_G06_20261128_210449_73e9a4.zip，同 FFmpeg SHA256。4K HLG 60fps、同尺寸 Kodak Plate、8 秒/480 帧，HEVC Main10 P5 HQ VBR25M/max50M/buf100M/B4/LA16/AQ8。Hable、Spline、BT.2390 各默认/非默认，7 路共42组32帧采样均 hash/PTS 一致（无 Plate 与对应无 Plate 基准比较）；21轮编码成功且独立解码480帧。原链/双标志/前端/末端/双端中位 FPS 34.8411/35.3435/51.5209/42.2713/68.3875，双端 +96.28%；无 Plate 68.2820→84.5738，+23.86%。同 Plate 的编码文件大小均25832667字节，不据此声明码流相同。抽查双端输出 BT.709 Limited 10-bit 60fps，8秒，AAC 音轨起点0；播放同步及取消仍需生产验收。
- R8K4 缩放隔离：仅对原4K ProRes HQ 10-bit BT.709 Limited Plate 执行相同 fps/format/上传/下载，默认、仅 disable_multiplane、仅 disable_host_image_copy、双标志四组原尺寸均8/8 hash相同；加入 scale_vulkan=1920x1080:scaler=bilinear 后分别8/8、0/8、8/8、0/8。仅 disable_multiplane 和双标志的缩放输出 hash 彼此相同，Y/U/V PSNR 28.4432/inf/inf，综合30.2041dB。已隔离到 disable_multiplane=1 与此缩放组合，尚未确认底层采样机理及哪个布局符合理论 bilinear。1080p HLG Hable 完整图仅保留默认设备或仅 host 标志并去掉前端往返，32/32 hash及PTS与原图一致，尚未测速且未验证其它曲线。
- 首批生产候选（待授权及源码审计）：RTX 4080、逐行、原帧率、软件解码、HEVC Main10、仅 Vulkan HDR→SDR（Hable/Spline/BT.2390），不附加 LUT/CC/几何/字幕/插帧/其它滤镜；无 Plate 或普通同尺寸 Plate 35% 等既有完整强度语义，复用 G04/S4 双标志及 CUDA 派生，保持色彩参数、metadata 清除和合并时间轴/EOF select。缩放 Plate 暂保留原链，默认设备前端优化另行验证后再放行。不全局改动两标志，不擅自改变已有 G04/S4；G04 及其它使用双标志且缩放 Plate 的路径需单独核对，不能从4K→1080结果直接推断所有缩放方向。保留 CPU 算法及既有 fallback。实际 GUI/CLI 入口、预览、独立 HDR→SDR 工具与 encoder pix_fmt 约束需继续审计；正式接入、Windows CI、真实播放、取消/临时文件和回归未执行。G06 尚未关闭，生产源码不变。

### 10.1 用户决策与 G06b/G06c 接力（2026-11-28）

- 用户已制作 2160p、1080p 两套扫描 Plate，不要求为每种电影宽高比另制颗粒。优先解决缩放异常，暂缓 G06c 正式接入；完全同尺寸只是当前双端直通的验证边界，不是永久产品限制。
- G06b：先核实 `SharedGrainCore.NeedsPlateScaling` 的完整定义及实际处理尺寸、Plate 尺寸探测、缩放顺序；NativeHevcCore.cs:240/295/503 已确认以 processingWidth/processingHeight 调用该 helper。以 R8K4 的独立 scale_vulkan 隔离为起点，复用已验证测试框架和同一 FFmpeg。追查实际 Stable 二进制对应的 FFmpeg 源码（不能假定 fork master 等于 Stable），重点比较 multiplane / separate-plane 的 P010 luma、chroma 格式、采样坐标及 shader。用可计算参考图（常量、斜坡、脉冲、棋盘）和真实 Plate 判定哪一条符合 bilinear；不先假定默认布局一定正确。覆盖 3840×2160→1920×1080、1920×1080→1920×800、3840×2160→3840×1600、反向放大及非等比例缩放，并保留原尺寸控制组。分别比较默认、单 multiplane、单 host、双标志，记录 Y/U/V 误差、帧 hash、PTS、色彩范围与标签；有候选修复后验证完整 HDR→SDR+Plate 双端直通及 SDR G04 缩放路线、性能和 S4 等受影响路径。不得以裁剪替代现有缩放、不得全局删除或改变两项 Vulkan 标志，不以用户新增 Plate 分辨率绕过问题。若需要 FFmpeg 修复，先定位并给出最小补丁及独立复现；未经明确授权不建分支、PR 或触发 Windows CI。
- G06c：待 G06b 得到可复现、正确的缩放方案及完整链验证后，再按第10节边界接入；缩放未解决的路线仍保留安全原路径，不静默宣称高速。颗粒选择界面新增实际尺寸与动态路线提示（匹配且当前编码组合支持直通 / 需要缩放但已验证支持直通 / 使用常规路线），由共享核心的真实处理尺寸和路线资格决定，而非只按宽度或文件名标签判断；参数变化应同步更新。复用既有控件，GUI/CLI 编码核心一致，提示不得承诺未经验证的性能。正式接入前核对预览、时间轴、音频、取消、临时文件、特殊字符路径和已验收旧路线。G06a 关闭独立诊断阶段；G06 总项仍未关闭。本次仅更新文档，不改生产源码。

### 10.2 G06b 首轮静态诊断（2026-11-28）

- 重读 AGENTS.md 及本卡；master 相对生产 8d3f0ec5 的比较仅列出本 TODO 变化。按实测版本字符串读取 FFmpeg Fork 47b92841e33b 的 `libavfilter/vf_scale_vulkan.c`、`libavfilter/vulkan/scale.comp.glsl`、`libavutil/vulkan.c`、`libavutil/hwcontext_vulkan.c`。此为版本标识指向的源码，8.2/8.3 记录的 host 选项源码与实测差异尚未解决，不宣称完整构建源码已核实。
- P010 默认候选为一张 G10X6/B10X6R10X6 双平面图像；disable_multiplane 路径使用独立 R16_UNORM / R16G16_UNORM 图像。视图创建根据实际图像数量选择 COLOR 或 PLANE aspect。scale_vulkan 使用归一化坐标、VK_FILTER_LINEAR 和 CLAMP_TO_EDGE；MODE_COPY 的 Y 采样使用 (pos+0.5)/imageSize(output) 经 crop_range/crop_off 调整，UV 另外按 chroma siting 和 textureSize(input) 计算。尚未获得实际 Vulkan 图像尺寸或采样结果，不能确认哪个布局正确，也不应直接修改公共 Shader。
- 生产 NativeHevcCore：processingWidth/Height 来自 ResizePlan 或输入尺寸，Plate 转 P010 后上传、按 grainScale 选择 scale_vulkan=bilinear，再 blend_vulkan。G04cDirect 的资格未排除 grainScale；此前 K6P3 合成 Plate 1080p→4K 等价，而 R8K4 真实 Plate 4K→1080p 不等价，需用同一确定性样本覆盖放大/缩小及单轴缩放，不能概括所有方向。SharedGrainCore 完整定义未定位（猜测文件名返回404），继续审计。
- 下一轮优先使用输入像素中心映射 x=(xo+0.5)*Wi/Wo-0.5、y=(yo+0.5)*Hi/Ho-0.5 的四邻域插值独立参考（边缘 clamp，P010 按有效10-bit比较）；用常量、斜坡、脉冲、棋盘及真实 Plate 区分几何采样误差与量化误差，记录四个设备选项和实际帧/视图尺寸证据；nearest 可作为定位采样器的辅助对照，不作为生产替换。候选修复后才进入完整链。
- 已在 Library 定位 FGS_G06_R8K4_Test.zip、FGS_G06_Q7M2_Test.zip 和对应 R8K4 Results ZIP，但原始字节 materialization 被拒绝；需要用户重新附上最新 R8K4 完整测试包及其结果，保持测试框架连续性。未编写替代测试包，未确认根因，未修改生产或 FFmpeg 源码，未建分支/PR，未触发 CI。

### 10.3 G06b N4V7 数学参考与缩放隔离（待实机）

- 用户补传 R8K4/Q7M2 完整测试 ZIP 和 R8K4 原始结果 ZIP，已恢复测试框架连续性。原始 config、Plate probe、隔离 argv/log/hash/PSNR 与第10节记录一致：4K ProRes HQ yuv422p10le 转 P010 后上传，原尺寸等价，加入 1920×1080 bilinear 后 M_ONLY/DUAL 的 Y 不等价。无新的 GPU 实测结论。
- 交付 FGS_G06b_N4V7_Test.zip，SHA256 `713ca9899d8e0339280d1eab444b67a73d48943f8711e3789db71b46655e6715`。复用 R8K4 的 Python 标准库、Windows BAT/PS1 启动器、设备参数、命令与日志保存；强制核对同一 Stable FFmpeg SHA256。选择 2160p 和 1080p 真实扫描 Plate，不需 HDR 输入。两套尺寸各四帧确定性图（常量/XY斜坡/脉冲网格/棋盘）及真实 Plate 两帧，原尺寸上传下载、显式原尺寸缩放、4K→1080、4K→3840×1600、1080→1920×800、1080→4K、1080→2560×1440，另含 nearest 定位控制；22组×4设备配置，共88次短渲染。
- 独立数学参考直接计算 Y 像素中心 bilinear、clamp-to-edge，固定网格、边缘和随机坐标采样，记录误差/异常位置，非全帧理论比较；全帧及Y/U/V哈希、范围、showinfo PTS/色彩标签、布局间差异PSNR。UV 暂不宣称独立 chroma siting 正确；nearest 半点舍入仅作诊断。计时不用于性能验收；实际 Vulkan 视图尺寸及根因仍未确认。
- Python 语法、参考算法、CPU P010 格式往返、12组 CPU 代理矩阵及特殊字符路径、BAT/PS1 BOM、ZIP 完整性检查通过；Windows GPU 和最终 PTS 正则比较尚待实机，CPU swscale 不作为数学参考。双击 Run_G06b_N4V7.bat，回传 Results_G06b ZIP。无生产/FFmpeg 源码变更、无新分支/PR、无 CI；完整 HDR→SDR 与 G04 缩放链留待正确候选路线验证。

### 10.4 G06b N4V7 实测：双标志布局符合 Y 数学参考；J8P3 完整链复验（2026-11-28）

- 证据 Results_G06b_20261128_213514_f447b5.zip；Stable FFmpeg SHA256 与 R8K4 一致，88/88 渲染成功。22组66帧的 M_ONLY/DUAL 全帧 hash 66/66 一致；DEFAULT/H_ONLY 对应帧一致。原尺寸仅上传下载全布局一致。1080p 原尺寸显式 scale_vulkan 和 4K→1080p 出现异常：DEFAULT/H_ONLY 的真实 Plate Y 最大采样误差分别151/196个10-bit码值，M_ONLY/DUAL 分别0/0.75。合成1080p identity 棋盘最大873、脉冲812码值；4K→1080p 合成脉冲最大402；双标志上述棋盘和脉冲采样符合数学参考。合成非恒定图在这两种输出1080p的情形中UV平面hash也不同，尚不能以旧真实Plate UV一致推广。4K→3840×1600、1080p→1920×800、1080p→4K/2560×1440 四布局完全相同，真实Plate Y最大采样误差1.15/1.08/0.94/0.98。修正“默认布局是正确参照”的隐含假设；目前双标志路线是更准确的Y采样候选，而不是异常来源。原始PTS布尔值33/66为false，系真实Plate showinfo额外处理第三帧而输出仅两帧导致长度判断错误；重新核对实际输出对应前N帧PTS，66/66一致。
- 最可能的底层解释为目标1080高度被按1088参与归一化坐标。N4V7 identity棋盘坐标(1090,135)理论940，实测67；以1088为分母的预测67.22。1080p斜坡(104,956)实测471、该模型471.97；4K→1080p脉冲(1024,306)实测530、该模型531.01。部分点仍有其他小量差异；实际图像尺寸/Shader编译来源和根因仍未确认。不能据此修改全局设备标志、公共Shader或宣称上游Bug已经完全定位。
- 交付 FGS_G06b_J8P3_Test.zip，SHA256 `6ae18dd0298ed4f0aae56bc64951cee51794f6d5444dcfe04a2d97264b930550`；复用N4V7和R8K4原脚本。第一阶段26次合成短渲染，默认/双标志、1072/1080/1082/1088/1090/1104及相邻宽度、控制组，比较真实尺寸和16/32对齐尺寸采样模型；新增UV中心色度位置假设下的独立参考及全平面hash，不宣称已确认实际色度位置。第二阶段选择真实逐行HDR/SDR与两套Plate，交叉非同尺寸；HDR Hable/Spline/BT.2390 默认/非默认六组，SDR普通无LUT的G04形态；A_BASE默认下载、B_FLAGS双标志下载、E_BOTH双端直通。用双标志下载32帧hash/PTS而非默认布局作为直通等价基准，同时记录相对默认的差异。通过质量门槛后进行8秒×3次HEVC Main10同参性能、独立解码、输出PTS及音频起点检查。
- 本地Python语法、Y/UV identity数学参考、18组HDR/SDR滤镜图构造、特殊字符路径、BAT/PS1编码、ZIP检查通过，未执行Windows GPU。等待Results_G06b_J8P3 ZIP；G04 GPU LUT组合、S4/FRUC回归、播放同步和正式接入仍待后续验证。不修改生产或FFmpeg源码、不创建分支/PR、不触发CI。

### 10.5 G06b J8P3 实测：缩放路线独立验收通过，进入 G06c（2026-11-28）

- 证据 Results_G06b_J8P3_20261128_214307_54d7b3.zip，Stable SHA256 与 Q7M2/R8K4/N4V7 相同。26/26 合成短渲染成功。默认布局的 1920×1080/1082/1090、1918×1080/1922×1080 最大 Y 采样误差分别873/867.95/853.94/875.54/865；双标志分别0/2.91/2.74/3.11/3.24。1072/1088/1104/800 与 3840×2160 控制组布局间4/4帧hash一致。4K→1080默认Y最大402、UV最大850，双标志Y 0.5、UV 0.75。UV 独立参考以 center siting 为条件；合成源标签 unspecified，不能推广所有色度位置。双标志13组最大Y误差4、UV 3.3，来自插值/量化参考差异，不宣称与数学浮点值逐像素完全相同。
- 默认布局1080/1082以1088作Y采样分母，1090以1104作分母，最大误差降至2.51/2.51/2.74；1918×1080按1920×1088预测最大2.51，1922×1080按1952×1088预测最大6.19。强证据支持默认多平面布局对齐尺寸参与归一化导致采样错误，但尚未直接读取 GPU imageSize/textureSize 或确认驱动与Shader的责任，不宣称底层根因已完全定位。不以裁剪、强制对齐输出或删除设备标志规避。独立上游最小复现/诊断可以后续单列，不阻塞正确双标志路线。
- 六个真实源，两套真实扫描Plate，9个非同尺寸组合；29组参数（4个HDR组合×6曲线参数、5个SDR组合×1），87/87采样成功；E_BOTH 对 B_FLAGS 29/29组、928/928帧hash/PTS一致。默认对双标志21/29组一致；3840×1608 SDR的两组Plate及1080p HLG六组曲线差异均为0/32 hash，不能以默认布局做正确性金标准。81/81编码及独立解码通过，27组三路线的完整输出逐帧PTS/duration列表一致，81组视频及音频起点0。HLG短源132帧，其余192或240帧，比较均使用实际帧数。未开展播放同步人工确认。
- A_BASE→E_BOTH 中位FPS：2560×1440 HDR/2160p Plate 42.70→56.17 (+31.5%)，/1080p 58.17→83.50 (+43.5%)；3840×1608 SDR/2160p 44.99→52.43 (+16.5%)，/1080p 62.40→74.77 (+19.8%)；2560×1072 SDR/2160p 62.16→66.55 (+7.1%)，/1080p 96.20→115.39 (+19.9%)；3840×2160 HDR/1080p 34.33→61.65 (+79.6%)；1920×1080 HLG/2160p 34.38→37.47 (+9.0%)；3840×2160 SDR/1080p 51.33→63.46 (+23.6%)。HDR完整链为Hable默认测速，其余曲线做32帧质量等价验证。
- G06b 独立缩放路线验收完成，G06c 可按第10/10.1节既有生产条件接入同尺寸与需要缩放的普通Plate及无Plate路线，保留两标志和现有算法、强度、时间轴；不得扩大为所有硬件/所有组合自动直通。G04 已有缩放候选保持不变；本轮SDR不含LUT，G04 GPU/CPU LUT组合、S4、FRUC、播放同步、取消清理、预览/GUI/CLI一致性及实际路线提示均列入生产候选回归，尚未通过。对默认多平面异常暂不全局修改；上游专项另列研究。G06总项仍未关闭。仅更新文档，未改生产/FFmpeg源码、未创建分支/PR或触发CI；G06c Windows CI及分支需用户明确授权。

### 10.6 G06c M9K6 生产候选（Windows CI 与实机待验）

- 从 v5.1.08 整合包核对正式 Release SHA256、生产源码及 master 文档提交。用户已授权 test/g06c-hdr-gpu-direct 和 Windows CI。仅共享 HEVC Main10 原帧率软件解码的 RTX 4080 普通路线、BT.2020 Limited PQ/HLG 10-bit、Hable/Spline/BT.2390；无 Plate 或已探测实际尺寸的偶数宽高 Plate，保留 bilinear。排除 LUT/CC/缩放裁剪/边框/插帧/其它滤镜组合，保留 G04/S4/FRUC 原路线。合并时间轴及参数生成后才启用双标志并移除传输，Hable 保留原 CPU 回退。颗粒区复用主题标签，异步探测实际尺寸，动态判断当前选中视频及完整状态。新增 G06c Windows 静态契约和受保护路线回归。未进行 GPU 实机或用户验收，未发布，G06 未关闭。
- M9K6 Windows CI：C# 编译及 Framework API 3053/3053 通过；新增 G06c 图契约发现 gamut_mode 前的 libplacebo 选项分隔符应为冒号，候选不交付。R7Q2 仅修正该边界及测试包后缀，重跑同一套契约。
- G06c R7Q2：Windows 编译、36 组 G06c 滤镜图契约以及 G04/R08/R07/R02/S4/R03/R04c/时间轴契约通过；旧 UI 启动测试仍以单参数反射调用已增加可选第二参数的 ShowTrueHdrDialog，触发 TargetParameterCountException。V4N8 仅修正测试调用及交付后缀，生产代码与 R7Q2 相同，重新运行 Windows CI；待实机验收，不关闭 G06。
