# FGS GPU 单帧实时预览——FGS 独立调用与正式构建 R12

FGS 基线：`master` / `f2ab7dd`（v5.0.56）。

**验证状态（2026-11-08）：** R4 已在老P 的 Windows 11 + RTX 4080 上通过原生 10-bit、双滑块及 1% LUT 实测。《沙丘2》强颗粒可明显观察，拖动无闪烁。60 次颗粒调整平均 4.545 ms、P95 6.191 ms；60 次 LUT 调整平均 3.966 ms、P95 6.069 ms。**R5–R10 的交互和 FGS 回传已通过验收；R12 的 ProRes Grain Plate 定位优化已通过 Windows 实测，颗粒换帧抽取约 0.28 秒。**正式 v5.0.57 构建验证待 Windows runner 完成；不要把实验生成文件直接覆盖进已发布 Stable。

## 一、原型能做什么

- 读取 FFmpeg 抽出的 SDR BT.709、有限范围、YUV420P 8/10-bit 原始帧。每次切换视频帧才重新解码并上传 Y/Cb/Cr；同一帧内调颗粒／LUT 不重复上传。
- 解析 FGS `filmgrn1` Grain Table，包括完整多事件时间轴与 `update=0` 模型继承；可按源视频时间点选择颗粒模型。R8 对表中的初始种子按选中事件的标称帧序号逐帧推进，避免 `seed=0` 时 GPU 的亮度颗粒退化为常量。
- `presets/` 内的 14 套 Film Preset 均**直接从当前 FGS C# 内嵌 GZip 模型导出**（并与 grav1synth v0.2.2 官方同名文件逐字节核对一致），不是手工近似替代品。
- 强度调整复用 FGS `ScaleTable()` 的整数缩放规则，保留随机种子和 AR 系数。
- 可选加载 3D `.cube` LUT，保留已验证的 25%、50%、75%、100% 预计算路径；其余百分比使用动态 LUT。鼠标直接拖动两条绿色滑块，颗粒、LUT 都可从 0 调到 100%。R5 起支持通过 FGS 按钮打开独立窗口，点击“应用到 FGS”才写回参数，取消则不改。R6 的 FGS AV1 颗粒主滑块为 1–100% / 1% 步进，0% 对应关闭颗粒。
- 使用 libplacebo + D3D11 实时重绘。调节时不重复解码、不启动 FFmpeg、不生成 PNG、不重新上传原始画面。
- R7 画面双击切换 **适应窗口 ↔ 1:1 原始预览帧像素**，在 1:1 时可拖动图片平移。由 FGS 打开时，新增视频时间轴、时间输入框、跳转及随机一帧，只有确定切换帧才执行一次 FFmpeg 抽帧；时间点变化会同步重选 Grain Table 模型。
- R9 将时间输入框精简到容纳完整毫秒时间码的大小，并放在“随机一帧”“跳转”之前，三控件底边对齐。关闭独立窗口会保存位置、大小与最大化状态，下次打开恢复；显示器断开时退回可见的默认位置。R9a 起每次打开使用随机的安全时间点；很短的片段会回退到开头。
- **R10 Grain Plate GPU 实时路径**：HEVC/H.264/x264（以及 SVT 像素扫描模式）的 `.mov` Grain Plate，打开预览时只解码一次视频／颗粒帧并分别上传 GPU；拖动 15–100% 颗粒强度（5% 一档）通过 libplacebo 的原生 YUV Overlay Shader 实时重绘。选择其他时间点时才分别抽取新的视频帧及循环颗粒帧；“应用到 FGS”按原有主界面档位回传。**RTX 4080 实机验收通过，但不宣称与正式编码逐像素一致。**
- **R11 并行抽帧优化**：打开 GPU 窗口时，FGS 不再串行等待原视频与 Grain Plate 两次 FFmpeg 抽帧；窗口内切换时间点时也先启动两路 FFmpeg，再等待两路成功完成后更新纹理。所有颗粒／LUT 实时 GPU 调整代码保持不变。新增初次抽帧及每次切帧的**视频／颗粒／总耗时**，关闭 GPU 窗口后显示在 FGS 日志中。R10 的 2–3 秒选帧等待来自串行解码；并行后的实际缩短幅度待测。
- **R12 ProRes 循环定位**：针对可核对帧数／时长的 ProRes Grain Plate，按素材真实帧周期循环取模，避免对短 MOV 做漫长的 `-stream_loop -1` 定位。用户实测单次颗粒抽帧降到约 275–310 ms，换帧总耗时约 300–573 ms；效果及正式编码回传此前已通过人工确认。
- 时间点不被表覆盖、停用事件、缺失模型、1D/复合/过大 LUT 会明确报错，不擅自渲染。

当前新增的 LUT 是**接近效果的交互预览**：FGS 正式编码在颗粒注入前执行 FFmpeg `lut3d`，而原型通过 libplacebo 渲染参数在颜色映射阶段执行 LUT，**两条链的先后顺序与色彩/插值细节不同**，不能当成逐像素一致的最终输出。R10 Grain Plate 的 Overlay 算法按正式编码公式实现，但 GPU 采样、取帧时点和与 LUT 叠加的顺序必须通过 Windows 实测对比。原型不支持 HDR、动态 `--iso` 自动生成、Digital Grain、FGSIM 和连续视频。**现有 1080p Photon-Noise ISOxxx `.tbl` 可直接按 Grain Table 路线预览**，但不等于从用户选择的 ISO 值实时生成模型。

### R10 已验收：Grain Plate

1. 在 FGS 选择 HEVC 或 H.264 → 扫描颗粒文件 → 一个已有 `.mov` 素材，关闭 LUT；点击“高级预览调整…”。10-bit Grain Plate 帧由 FFmpeg 抽取，窗口里强度应落在 **15、20、…、100%**，不能出现 1% 之类仅适用于 AV1 的档位。
2. 比较 15% 与 100%：必须看到明显的实时变化；按 `G` 关闭／恢复颗粒，双击画面 1:1 后拖动平移，确认颗粒与原画没有错位。滑块变化不应触发 FFmpeg；切时间点才触发两路抽帧。
3. “应用到 FGS”应同步主界面扫描颗粒强度和开关；“取消”不改变设置。分别对 HEVC/H.264 做小片段实际编码，并用相同时间点对比原四格预览／成品。
4. 开启 LUT 测试交互流畅与组合观感；**R10 不能承诺 LUT + Grain Plate 与正式编码逐像素相同**。如果画面全白、全黑、颗粒倒置、1:1 平移错位或 Shader 禁用，把 GPU 控制台日志及截图交回分析。

### R11 新增验收：并行抽帧

1. 在同一份 HEVC/H.264 + Grain Plate 输入上，对比 R10 与 R11 **打开高级预览**、**随机选帧**和**输入时间跳转**的等待时间。R11 关闭 GPU 窗口后，在 FGS 日志搜索 `[GPU Preview] 初次抽帧` 以及 `切换帧抽取`，可见视频／颗粒／并行总耗时（毫秒）。
2. 仍应同时显示对应时间点的正确画面和 Grain Plate；一条 FFmpeg 解码失败时仍停在上一帧，不显示半旧半新的组合。拖动颗粒/LUT 强度依旧立即 GPU 重绘。
3. 两条解码同时运行可能争抢磁盘和解码资源，因此**不预设能缩短到几毫秒或固定倍率**。请回传日志中的时间数据，确认实际是否有收益。

独立调用（`--plate-frame` 必须先用 FFmpeg 抽出与主帧相同尺寸的 `yuv420p10le` 原始单帧）：

```text
fgs_gpu_preview.exe video.yuv 1920 1080 - 10 0 15 --plate grain.mov --plate-frame plate.yuv --result result.json --grain 80 --grain-on 1 --lut 0 --lut-on 0 --fps 30/1 --video video.mp4 --ffmpeg ffmpeg.exe --duration 120
```

只有 `--plate` 与 `--plate-frame` **同时存在**才启用 Grain Plate；AV1 Grain Table 的 CLI 不变。每次启动都要传入已抽取的两张 YUV，不能传 PNG 或 MOV 冒充原始帧。

## 二、Windows 11 编译

打开 **MSYS2 UCRT64**，先完整升级（如提示关闭终端，重开后再执行一次）：

```bash
pacman -Syu
```

再装依赖，进入本目录编译：

```bash
pacman -S --needed mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-pkgconf mingw-w64-ucrt-x86_64-libplacebo
./build_ucrt64.sh
```

构建脚本会顺便测试 14 套预设的事件起点、终点及继承行为，并把随附的 `FGS.ico` 编入原型 EXE，不依赖运行目录中的图标文件；原型通过 `libplacebo` 动态库运行。正式发布由 Windows runner 同时重编译辅助程序，并收集 UCRT64 DLL 和许可证至 `GpuAdvancedPreview/`；**不要手动把实验目录里的编译产物覆盖到 FGS Stable**。

如遇 GCC 15 的 `gcc-libs` / `gcc-libgfortran` 与 GCC 16 拆分运行库冲突，先完成包迁移，**不得用 `pacman -Rdd` 强拆**。编译后从 UCRT64 运行 `fgs_gpu_preview.exe`，以便加载 `/ucrt64/bin` 的运行时 DLL；原型不往 FGS 主目录复制文件。

## 三、准备输入帧

选 SDR BT.709、有限范围视频，在 PowerShell 里用现有 FFmpeg **抽取一次** 1920×1080 原始帧：

```powershell
& 'E:\EnCoder\FFMpeg\x64\bin\ffmpeg.exe' -hide_banner -y -ss 00:00:15 -i 'H:\Tester\Benchmark\AV1 1080p SDR 30 30s.mp4' -map '0:v:0' -frames:v 1 -vf format=yuv420p -f rawvideo 'H:\Tester\Benchmark\one_frame.yuv'
```

重要：第一轮 R2 用上面的 `format=yuv420p` 转成了 8-bit。R3 已用下面的原生 10-bit 路线成功实测；后续优先沿用 10-bit。

想测试 10-bit，只把滤镜及输出文件改为：

```powershell
& 'E:\EnCoder\FFMpeg\x64\bin\ffmpeg.exe' -hide_banner -y -ss 00:00:15 -i 'H:\Tester\Benchmark\AV1 1080p SDR 30 30s.mp4' -map '0:v:0' -frames:v 1 -vf format=yuv420p10le -f rawvideo 'H:\Tester\Benchmark\one_frame_10bit.yuv'
```

`.yuv` 没有分辨率及颜色元数据，宽高、位深必须匹配抽帧输出。

## 四、运行和操作

在 UCRT64 中执行（路径用引号包住，支持中文）：

```bash
./fgs_gpu_preview.exe 'H:\Tester\Benchmark\one_frame.yuv' 1920 1080 'E:\EnCoder\FilmGrain_Studio\_AV1_Grain_Tables\1080p\Star_Trek_TNG_medium_grain_1080p.tbl' 8 60
```

10-bit 测试使用 `one_frame_10bit.yuv`，并把参数 `8` 改成 `10`。

**R4 双滑块实测（R9 保留相同位置参数）：**

```bash
./fgs_gpu_preview.exe 'H:\Tester\Benchmark\one_frame_10bit.yuv' 1920 1080 'E:\EnCoder\FilmGrain_Studio\_AV1_Grain_Tables\Netflix_FGS_Dune2_MOD_E3.txt' 10 60 15 'D:\LUTs\Preset.Curator.Cinematic.and.allround.LUTS\Summer Vibe\4E_Beach Style 33.cube'
```

如果想脱离 FGS 目录单独测，也可以用包内模型和 LUT：

```bash
./fgs_gpu_preview.exe 'H:\Tester\Benchmark\one_frame_10bit.yuv' 1920 1080 'presets/16mm.tbl' 10 60 15 'demo_warm.cube'
```

参数位置固定为：`输入YUV 宽 高 表路径 8|10 [测速次数] [模型时间秒] [LUT路径]`，R8 起另外支持可选标志 `--fps 24000/1001`（或 `--fps 23.976`）。FGS 会自动传入视频探测得到的标称帧率；独立命令不传时默认用 30fps 估算。VFR 或中间插帧后，预览颗粒纹理不保证逐像素与成品一致。

- `presets/16mm.tbl`：FGS 的原始 16mm 预设；还可换成 `modern35.tbl`、`classic35.tbl`、`super8.tbl` 等 14 套预设。
- `15`：从预设的**第 15 秒**查找 AV1 颗粒事件，适配抽帧用的 `-ss 00:00:15`。省略则默认时间 0；对多事件模型，时间对不上可能看到不同颗粒。
- `demo_warm.cube`：附带的简单暖调 3D LUT，用来检查切换是否工作；可换成 FGS LUT 图库里的独立 3D `.cube`（大小 2–65，暂不支持纯 1D、1D+3D 复合 LUT）。使用该 LUT **并不代表 FGS 最终编码的 LUT 结果完全一样**。
- 如果只想检验预设的 AV1 颗粒，可在命令末尾删掉 `15 'demo_warm.cube'`，仅使用 `10 60`；此时选择预设第 0 秒模型。

**鼠标拖动：**窗口下方两条绿色滑块分别对应颗粒和 LUT，单击轨道定位、按住滑块拖动。**AV1 颗粒与 LUT 为 1% 精度；Grain Plate 仍按 FGS 的 5% 档位。**显示区和滑块区是两个独立 HWND，避免 GDI 绘制覆盖 D3D11 交换链。如果 LUT 未加载，第二条滑块为禁用状态。

**R7 观察细颗粒：**双击视频画面进入 1:1，查看未经窗口缩小的像素，按住画面左键拖动即可观察不同位置；再双击恢复适应窗口。由 FGS 启动后，可拖动第三条「选择视频帧」时间轴，松开鼠标后抽取所选时间点。也可在底部输入 `00:00:15.000`，点击「跳转」或按 Enter；「随机一帧」可寻找天空、暗部或肤色等适合观察颗粒的画面。切换帧时会短暂停顿等待 FFmpeg；调节颗粒／LUT 保持实时。

| 按键 | 功能 |
|---|---|
| ← / →、↑ / ↓ | AV1 颗粒每次 1%，Grain Plate 每次 5% |
| `G` | 有颗粒／原图切换 |
| `Home` / `End` | 设为 0% / 100% |
| `J` / `K` | LUT 每次减少／增加 25%（保留 R3 操作） |
| `L` | LUT 开关切换；未加载 LUT 时无操作 |
| 画面双击 / `1` | 1:1 与适应窗口互相切换 |
| `Esc` | 退出 |

由 FGS 启动时，底部会出现**“应用到 FGS”与“取消”**按钮；在画面区域按 `Enter` 应用、`Esc` 取消；时间输入框的 Enter 用于跳转。直接执行旧 CLI 命令不带回传／原视频参数，不会出现操作按钮或可用的时间轴，但仍支持双击 1:1。R7 独立窗口沿用 FGS 图标；滑块标题改为靠右贴近轨道，所有标签、百分比、按钮统一使用与 FGS 主界面一致的 **Segoe UI 9pt**（中文由系统字体回退），并保持垂直居中。

R9 底部视频操作顺序为**时间输入 → 随机一帧 → 跳转**，输入框按时间码实际宽度紧凑排列，三个控件底边对齐。独立 GPU 窗口记忆当前用户上次关闭时的位置、大小、最大化状态，保存在 `HKCU\Software\FilmGrainStudio\GpuAdvancedPreview`；屏幕配置变化造成旧位置不可见时使用默认位置。

## 五、在 FGS 中测试 R12

**只把本测试包里的 `dotnet/` 修改文件覆盖到与你当前 `f2ab7dd` 基线完全一致的 FGS 源码副本**。不确定本地源码版本时不要覆盖，先核实。不要直接替换 Stable 安装目录里的正式 EXE。

1. 用 MSYS2 UCRT64 执行 `./build_ucrt64.sh`，生成 R12 的 `fgs_gpu_preview.exe`。确认 R9 以来的两条 libplacebo `const` 类型警告消失。如果依赖已经装好，不用再安装一遍。需要随附的 `preview_icon.rc` 和 `FGS.ico`，构建脚本通过 `windres` 嵌入 FGS 图标。
2. 将实验生成的 `fgs_gpu_preview.exe` 放到 FGS 测试运行目录下的 `GpuAdvancedPreview/` 子目录。依赖 MSYS2 DLL 从现有 `D:\msys64\ucrt64\bin` 查找（若在别的位置，可设置 `MSYS2_UCRT64_BIN` 环境变量），不会修改系统 PATH。也可以设置 `FGS_GPU_PREVIEW_EXE` 为程序的绝对路径，不复制 EXE。
3. 用**本包修改后的** `dotnet/build_NET_Preview.bat` 在 Windows 编译，确认退出代码为 `0`。运行编译出的测试版 FGS，选择已完成探测的 SDR BT.709 视频。
4. 在右侧“颗粒”与“LUT”之间点击**高级预览调整…**。确认时间输入框在最左侧，比完整时间码稍宽、比文字稍高，并与“随机一帧”“跳转”底边对齐；实际输入时间，测试跳转及随机抽帧。选择 MaxMid 的 65.588 秒片段，核对 0%／100% 颗粒调节和 1:1 双击功能保持正常。
5. 回到 FGS，把颗粒、LUT 分别调成 **37%、63%、99%**，确认 1% 强度正常回传、取消不改参数；分别实际编码短样本检查正式编码链。**R7 对 4K 输入保留原始 4K 抽帧尺寸，可能比之前 1920 上限消耗更多时间与显存，须单独测量。**
6. 拖动独立窗口并调整大小，关闭后重开确认位置／尺寸恢复；最大化关闭再打开确认保持最大化。若更改了显示器布局，应确认窗口仍可见，不会留在已断开的屏幕上。

**首轮支持**：AV1 胶片预设／Grain Table、HEVC/H.264 像素 Grain Plate、独立 3D CUBE LUT、SDR BT.709 有限范围逐行视频。Digital Grain/FGSIM 等其他颗粒类型仅在已有可选 LUT 时进行 **LUT-only** 预览，并明示不代表完整效果。HDR、AV1 不重编码、复合 LUT、ISO 实时生成、前处理、调色、插帧均不纳入本轮。现有正式编码和原有预览均保留。ISO 正式编码静态核查没有发现同类 seed=0 问题；其原本就是比 MaxMid 弱很多的光子噪声模型。

**特别注意**：R6 的 GPU 颗粒滑块和 FGS AV1 主滑块均为 **1% 精度**（FGS 主滑块 1–100%，关闭颗粒用总开关），应用时不舍入；正式编码共用的 AV1 表强度缩放核心原本就支持 1–100%。其它颗粒类型的旧滑块档位不变。LUT 仍在主界面、调色窗口、预设和三条编码核心保留 **1%**。

**单独检查参数回传**（仍可从 UCRT64 测试，不必先运行 FGS）：

```bash
./fgs_gpu_preview.exe 'H:\Tester\Benchmark\one_frame_10bit.yuv' 1920 1080 'presets/16mm.tbl' 10 0 15 'demo_warm.cube' --result 'H:\Tester\Benchmark\gpu_result.json' --grain 75 --lut 63 --grain-on 1 --lut-on 1
```

- 点“应用到 FGS”：生成 `gpu_result.json`，含版本号、颗粒/LUT 强度与开关；取消或按 `Esc`：**不生成文件**。
- 若 FGS 报找不到 GPU 程序、DLL 或抽帧失败，请提供 FGS 日志与构建输出；无需重复上传大视频。

## 六、RTX 4080 已实测成绩（R2 / R3 / R4）

末尾 `60` 表示自动做 60 次调节测速，省略也可交互。输出列：`phase,strength,elapsed_ms,working_mb,cpu_total_ms`。

- `SETUP`：读文件、初始化 GPU、首次上传耗时；`FIRST`：首帧 Shader 编译、渲染及显示耗时。
- `BENCH_MEAN/P95/MAX`：颗粒连续调节平均／95 分位／最大延迟。
- **`LUT_BENCH_MEAN/P95/MAX`（R4）**：有加载 LUT 时，额外自动执行 60 次 1% 精度 LUT 更新并测速；包括 CPU 更新 LUT 数据、libplacebo 上传/着色及 GPU 完成时间，因此与颗粒测试不能直接按相同工作量比较。
- `ADJUST`：拖动或按键触发的实际重绘（窗口重绘也可能输出同一强度）。第一下切换新强度可能包含 Shader/纹理初始化开销。
- 测量包含 GPU 完成与交换链显示，可能受 VSync 影响，不等于纯 Shader 时间。
- `working_mb` 是进程工作集（非 GPU 显存）；`cpu_total_ms` 是累计 CPU 时间（非 CPU 百分比）。

| 指标 | R2（8-bit） | R3（10-bit+LUT） | R4（10-bit+LUT，1%） |
|---|---:|---:|---:|
| 初始化 | 140.851 ms | 139.045 ms | 145.861 ms |
| 首帧 | 169.663 ms | 180.882 ms | 180.827 ms |
| 60 次颗粒调节均值 | 4.922 ms | 4.709 ms | 4.545 ms |
| 颗粒 P95 | 6.529 ms | 6.538 ms | 6.191 ms |
| LUT 调节均值 / P95 | — | — | 3.966 / 6.069 ms |
| 工作集 | 约 85–88 MB | 约 86 MB | 约 82 MB |
| 整卡 GPU 使用率（抽样） | 0–16% | 0–24% | 0–27% |
| 整卡显存占用（非程序独占） | 1911 MiB | 1959 MiB | 1813 MiB |

R4 人工结果：强颗粒实时可辨，调整无闪烁；LUT 1% 色彩变化明显、平滑。R5 支持的 AV1 模式与 R6 的 1% 颗粒输出、参数回传均已获用户验收。**R6 的颗粒基准测试改为覆盖 1% 强度点，与 R4 的 5% 采样序列不可直接横向比较。R7 的双击 1:1／选帧已试用；R8 的 seed 修复已通过用户视觉验收。R9 UI／窗口记忆待实测。**

200 ms 间隔的整卡 GPU 采样不能精确测出每次重绘负载。需要采样时在另一 PowerShell 窗口运行：

```powershell
nvidia-smi --query-gpu=timestamp,name,utilization.gpu,memory.used --format=csv -lms 200
```

## 七、当前两条预览流程

现有 FGS：`模式与强度 → Grain Table / 模型 → grav1synth → AV1 临时片段 → libdav1d → PNG → WinForms`。

GPU 原型：`FFmpeg 一次抽帧 → CPU 内存 → GPU 纹理常驻`，同时 `FGS 预设/Grain Table → 选中时间点模型 → 强度滑块 → libplacebo Shader`；可选 `3D CUBE → 预计算四档 + 按需生成 1% LUT → GPU LUT`，经 D3D11 子窗口显示。

首帧仍有一次视频帧 CPU→GPU 上传。**1% LUT 滑动会改写 CPU 中的 LUT 表，并可能重新上传较小的 LUT 纹理**；不会重新传输原始 YUV 视频三张纹理，因此不是“整个过程零拷贝”。完整参数映射参见 `TECHNICAL_NOTES.md`。

## 八、后续边界

1. 本轮请反馈：**R12 对你两份 ProRes HQ Plate 是否出现 `ProRes 快速循环定位`，并用 65.607 秒附近比较 R11 的 3–4.5 秒与 R12 的抽帧耗时。**快速复验 Grain Plate 15%／100%、AV1 MaxMid/LUT、随机初始帧、窗口记忆和回传。抽帧仍由 CPU 解码 ProRes，GPU 占用低是预期；优化目标是少解码重复循环。如果自动回退 `普通循环定位`，请贴 FGS 日志及 FFprobe 输出。
2. 对比截图前先确认视频抽出的帧是否已经包含 AV1 颗粒，避免“自带颗粒 + GPU 又合成一次”造成误判。
3. 后续可做同帧 1:1 差分图、PSNR/SSIM；ISO 的**实时生成**需要照顾分辨率、传递函数和色彩范围，仍须单独验证。
4. 本轮采用独立 EXE 与 FGS 的一次性参数交接，**不嵌入 WinForms GPU 画布**；连续视频仍留待后续。现有正式预览与编码链保留原样。
