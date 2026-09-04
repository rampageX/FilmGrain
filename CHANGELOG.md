# Film Grain Studio — CHANGELOG

本文件记录正式发布版本的主要变化。`README.md` 只维护当前版本功能与使用说明，不再重复版本更新摘要。

## v4.0.0 — 2026-09-05

- H.264 上传路线正式升级为 v4 核心功能：保留 NVENC P7 固定码率档，同时将 **libx264 / preset slow / tune grain / 2-pass** 作为高质量颗粒保留路线。
- NVENC 上传档精简为 `6000 / 8000 / 15000 kbps`，默认仍为 8000 kbps；NVENC 档不参与自动分辨率联动。
- x264 Grain 继续提供“推荐 / 高质量 / 极高”三档；码率算法由 v3.4.0 的 FPS 联动升级为 **FPS + 实际输出分辨率 + 动态系数三重联动**。
- x264 自动码率公式：`60p 档位基准 × FPS/60 × sqrt(输出像素数 / 1920×1080) × 动态系数`，最终按 500 kbps 步进取整。
- 新增 **“高动态视频”** 复选框，默认不勾选：普通动态使用完整自动码率的 0.5×；勾选后恢复 1.0× 高动态预算。1080p60 推荐档实测对应 7.5M / 15M。
- 分辨率联动采用像素面积平方根：720p≈0.67×、1080p=1×、1440p≈1.33×、4K=2×；4K 不再沿用 1080p 的同一总码率。
- Cinematic 裁剪后的**实际有效输出尺寸**参与 x264 码率计算，因此 1920×804、3840×1608 等电影画幅也可自动获得合理码率。
- x264 Grain 继续使用已验证的 VBV：`maxrate = average × 3`、`bufsize = average × 6`，在保持颗粒的同时限制极端高速镜头中的瞬时码率峰值。
- Studio UI 重新整理：H.264 上传质量行下方单独放置“高动态视频”和“字幕…”；画幅 / 驱动 / 能力等纯信息文字顺势下移，避免 Cinematic 与上传选项区域拥挤换行。
- H.264 硬字幕沿用 v3.4.0 已验证方案：内嵌字幕、同名外部字幕、浏览外部字幕、GB18030 回退、`huiwen-mincho`、1080p 基准字号 69、默认距有效画面下沿 25 px，并按输出宽度缩放。
- GUI / CLI 继续共用 `StudioBridge` 编码核心；CLI 的 x264 Grain 选择同样支持高动态开关与 FPS / 分辨率联动。
- 正式发布前重新执行 BAT/VBS BOM、PS1 BOM、BAT 标签引用、CMD `^` 续行、CRLF、Markdown/TXT 编码与 ZIP CRC 审计。

## v3.4.0 — 2026-09-05

- H.264 上传版新增 **硬字幕烧写**，仅作用于上传副本，不改变主 HEVC / AV1 成片。
- 字幕来源支持内嵌文本字幕下拉选择、同目录同名 `.srt / .ass / .ssa / .vtt` 自动匹配，以及浏览本地字幕文件；多文件任务支持逐文件自动匹配。
- 外部字幕新增字符集兼容：Unicode BOM / 严格 UTF-8 自动识别，非 UTF-8 中文字幕自动回退 **GB18030（兼容常见 GBK / ANSI）**。
- 字幕样式默认改为 `huiwen-mincho`、1080p 基准字号 `69`、白字、黑色 Outline 1 / Shadow 1；默认距有效画面下沿由测试阶段的 20 px 调整为 **25 px**。字号、边距、描边和阴影按输出宽度相对 1920 px 自动等比缩放。
- Studio 的“字幕…”按钮移至 **Cinematic Style 同一行**；H.264 上传版一行重新分配布局空间，避免“同时生成 H.264 上传版”文字换行。
- H.264 NVENC 上传档精简为 `6000 / 8000 / 15000 kbps`，统一升级为 **preset P7**；默认仍为 8000 kbps。
- 新增 CPU 高质量上传路线：**libx264 / preset slow / tune grain / 2-pass**，用于更高效地保留真实胶片颗粒。
- x264 Grain 提供“推荐 / 高质量 / 极高”三档，并按**实际输出 FPS 自动联动平均码率**：60p 基准分别为 15 / 20 / 25 Mbps，24p 约为 6 / 8 / 10 Mbps，最终值按 500 kbps 步进取整。
- x264 Grain 三档统一采用实测稳定的 VBV：`maxrate = average × 3`、`bufsize = average × 6`。60p 推荐档即 15M / 45M / 90M，解决无限制 2-pass 在极高动态镜头中瞬时码率异常飙升导致的播放色块问题。
- AV1 上传路线继续由 `libdav1d` 将 Film Grain metadata 合成为真实颗粒像素后再压制；HEVC 上传路线继续从原始视频重新走 Grain / LUT / 反交错 / Cinematic 处理链，避免从主成片二次转码。
- GUI / CLI 继续共用 `StudioBridge` 编码核心；正式发布前重新检查全部 BAT 标签引用、CMD 特殊字符、`^` 续行、文件编码与 ZIP CRC。

## v3.3.1 — 2026-09-04

- H.264 上传母版由 AV1 专属功能改为 **HEVC / AV1 共用**，GUI 与 CLI 同步开放。
- H.264 上传版新增码率选择：`6000 / 8000 / 10000 / 12000 / 15000 / 18000 / 20000 / 30000 kbps`；默认 8000 kbps，输出文件名包含所选码率，便于多档测试。
- HEVC 上传版直接从原始视频重新走同一套 Grain / LUT / 反交错 / Cinematic 处理链后编码 H.264，避免先经过主 HEVC 成片再二次转码。
- 合并 `FilmGrain_MOV_to_HEVC_Lossless_Cache.bat` 与旧 1080p Cache 工具；统一脚本可生成原始分辨率、1080p 或两种 Cache，旧 `FilmGrain_MOV_to_1080p_HEVC_Lossless_Cache.bat` 删除。
- Cache 校验改为 **实际 10-bit sample-exact**：参考 SHA-256 来自与 NVENC 相同的同一 P010 帧流，并规范化为 `yuv420p10le` 后比较，修复 T600 上因 P010 低 6 位填充差异导致的假校验失败；RTX 4080 与 T600 Laptop 均实测通过。
- 修复 `StudioBridge.bat` 被误保存为 UTF-8 BOM 后导致 `@echo off` 失效、GUI 日志窗口回显 BAT 调试命令的问题。
- GUI / Studio 模式不再显示单文件 `Actual commands` 调试输出；CLI 单文件模式继续保留，便于排错。
- 发布文件编码规则固化并在打包前检查：**BAT/VBS 使用无 BOM 的 ASCII 兼容编码；Windows PowerShell 5.1 的 PS1 使用 UTF-8 BOM；Markdown 使用 UTF-8；中文 TXT 使用 UTF-8 BOM。**

## v3.3 — 2026-09-04

- 新增自动反交错与 Field-rate 输出。
  - 默认：**BWDIF Vulkan**。
  - 备选：**BWDIF CUDA**。
  - 高质量对照：**W3FDIF Complex**。
  - 自动读取 FFprobe `field_order`；`tt / bb / tb / bt` 视为隔行，progressive / unknown 自动旁路。
  - 29.97i 自动输出 59.94p；25i 自动输出 50p。
- Film Grain Studio 的输出帧率提示会针对已识别的隔行素材显示类似 `自动（29.97i → 59.94p）`。
- HEVC 与 AV1 统一 Cinematic Style 画幅处理：
  - **加黑边 · 保留原分辨率**（默认，适合后期在黑边区域添加字幕）；
  - **裁剪 · 输出有效约 2.39:1 画面**。
- HEVC 扫描 Grain 路线的黑边继续在 Grain 合成之后添加，保持纯黑区域不叠加扫描颗粒。
- AV1 普通、AV1 + LUT 与 AV1 UHQ 共用相同的 Cinematic 画幅选择逻辑。
- CLI 正式入口改为直接调用 `Utils\FilmGrain_Universal_HEVC_AV1_StudioBridge.bat` 的交互模式；GUI 与 CLI 从此共用同一编码核心，避免核心功能两套脚本分别维护。
- CLI 同步加入反交错方式选择，并与 GUI 使用相同的 Field-rate、Cinematic、LUT、Grain 与编码参数逻辑。
- 修复 Cinematic 摘要中的 CMD `IF /I NOT` 语法顺序错误，避免 Letterbox / Crop 模式在开始编码前报“此时不应有 …”。

## v3.2 — 2026-09-03

- LUT Gallery 新增 **更换参考图**，可直接选择新图片并覆盖生成全部 LUT 预览。
- 生成期间显示独立进度窗口，Gallery 暂时锁定操作，完成后自动刷新缩略图。
- 当前页码由输入框改为只读下拉菜单，可显示当前页并直接选择任意页面。
- 保留上一页 / 下一页、PageUp / PageDown 首尾循环、搜索、文件夹筛选、Recent 与收藏等既有行为。

## v3.1 — 2026-09-03

- 新增 NVIDIA GPU、驱动与 FFmpeg 实际能力探测，不再依赖固定 GPU 型号写死参数。
- 自动探测 AV1、HEVC、H.264 NVENC、Main10、B-frame、B-reference、Spatial / Temporal AQ、Lookahead、Multipass、NVDEC CUDA 与 Vulkan，并只启用当前环境实际支持的参数。
- 新增 **AV1 UHQ**；只有微型编码测试通过的 GPU / 驱动 / FFmpeg 组合才显示。
- 新增 `Utils\_HardwareCaps.json` 能力缓存；环境未变化时直接读取缓存。
- H.264 社交平台上传母版改为按有效分辨率自动选择 6 / 8 / 10 / 12 Mbps。
- 修复 T600 / RTX 4080 能力探测与缓存状态显示相关问题。

## v3.0 — 2026-09-02

- 将 Film Grain 项目整理为正式稳定发布结构，统一 GUI / CLI 入口与 `Utils`、`_LUT_Tools` 目录。
- 清理各脚本文件名中历史遗留的独立版本号；组件固定文件名，项目版本号只体现在完整发布压缩包上。
- GUI Studio、CLI、HEVC 扫描 Grain、AV1 + grav1synth、LUT Gallery、Cinematic Style、自动电影帧率、MP4 / MKV、多文件处理等整合为统一工具包。
- `README.md` 作为 GitHub 项目的当前功能与使用说明基准。
