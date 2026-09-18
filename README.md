# Film Grain Studio

![](images/Film_Grain_Studio.jpg)

基于 **FFmpeg、NVIDIA NVENC、libx264 与 grav1synth** 的 Windows 视频胶片化工具包，同时提供图形界面和命令行入口。

Film Grain Studio 的目标不是只提供一种“加颗粒”方法，而是把几种不同思路的 Film Grain 处理方式统一到同一套工作流中：可以使用 AV1 Film Grain metadata，也可以把真实扫描 Grain Plate、数字颗粒或 GPU Film Grain 直接烘焙进像素；同时保留 HDR、LUT、反交错、插帧、字幕、Cinematic Style、自动码率和多文件处理。

当前提供三条同级主编码路线：

| 主编码路线 | Film Grain 重点 | 最终编码 |
|---|---|---|
| **AV1 Main10 + grav1synth** | AV1 Film Grain metadata、Film Preset、Photon ISO、现成 Grain Table；也可选择像素颗粒 | AV1 Main10 NVENC |
| **HEVC Main10** | 真实扫描 Grain Plate、Digital Grain、GPU Film Grain (FGSIM) | HEVC Main10 NVENC |
| **H.264 x264 Grain** | 真实扫描 Grain Plate、Digital Grain、GPU Film Grain (FGSIM) | libx264 + `tune grain` |

默认配置为 **AV1 Main10 + MP4 + AAC 256k**。

完整版本变化、Bug 修复与历史发布记录统一维护在 [`CHANGELOG.md`](CHANGELOG.md)。README 只介绍**当前版本的功能、工作原理、设置方式和使用建议**，不重复记录逐版本更新流水。

---

<table>
  <tr>
    <td width="50%" align="center"><a href="images/Original.jpg"><img src="images/Original.jpg" width="100%" alt="Original"></a><br><sub>Original Video</sub></td>
    <td width="50%" align="center"><a href="images/FG_CT35_V20FAST_HEVC_239LB_23976p.mkv_20260830_102847.766.jpg"><img src="images/FG_CT35_V20FAST_HEVC_239LB_23976p.mkv_20260830_102847.766.jpg" width="100%" alt="HEVC Real Grain"></a><br><sub>HEVC + Real Scanned Film Grain</sub></td>
  </tr>
</table>

---

## 快速开始

### GUI 图形界面

双击：

```text
FilmGrain_Universal_HEVC_AV1_GUI.bat
```

也可以将一个或多个视频直接拖到该 BAT。启动脚本会在交接参数后退出，Film Grain Studio 正常显示，不保留 CMD 或 Windows PowerShell 黑框。

基本流程：

1. 添加或拖入一个或多个视频。
2. 选择 AV1、HEVC 或 H.264 x264 Grain。
3. 选择 Film Grain 方式、输出容器、码率、输出帧率和反交错方式。
4. 按需启用 Cinematic Style、LUT、字幕、OpenSVPFlow、HDR 处理等功能。
5. 点击“开始编码”。
6. 在任务区查看当前阶段、进度、`fps`、`speed`、`ETA` 与完整日志。

GUI 输出默认保存在源视频所在目录；已有同名输出时会跳过，不直接覆盖。

### CLI 命令行

将一个或多个视频拖到：

```text
FilmGrain_Universal_HEVC_AV1_CLI.bat
```

按菜单选择处理方式；直接回车采用默认值。全部任务结束后会显示成功、失败和跳过数量。

GUI 与 CLI 共用：

```text
Utils\FilmGrain_Universal_HEVC_AV1_StudioBridge.bat
```

因此两种入口使用同一套编码核心，反交错、画幅、帧率、码率、LUT、Grain 与编码参数不会各维护一套。两种入口均考虑中文、空格以及 `&` 等 CMD 特殊字符路径。

---

## Film Grain 方式

FGS 当前把几种不同性质的颗粒方案放在同一项目中。它们的主要区别不是“谁更高级”，而是**颗粒最终存在哪里、如何生成、是否需要播放器参与以及适合什么场景**。

| Grain 方式 | 颗粒位置 | 主要特点 | 适用路线 |
|---|---|---|---|
| **AV1 Film Grain metadata** | AV1 码流参数，由播放器解码时合成 | 码率效率高；颗粒不直接编码进每个像素 | AV1 |
| **真实扫描 Grain Plate** | 直接烘焙进像素 | 使用真实胶片扫描素材，质感由 Grain Plate 决定 | HEVC / x264 |
| **Digital Grain · Fast Noise** | 直接烘焙进像素 | 不依赖外部 Grain Plate；速度较高；强度连续可调 | AV1 / HEVC / x264 |
| **GPU Film Grain (FGSIM)** | 直接烘焙进像素 | 使用 GPU shader / Vulkan 路径生成 Film Grain 效果 | AV1 / HEVC / x264 |

三条主编码路线尽量共用同一套画幅、字幕、LUT、反交错、OpenSVPFlow 与码率策略；真正不同的部分主要集中在 Film Grain 机制和最终编码器。

---

## AV1：grav1synth Film Grain

AV1 主线的核心思路是先编码相对干净的 Main10 视频，再由 grav1synth 将 Film Grain 参数写入 AV1 bitstream。

```text
原始视频
    ↓
AV1 Main10 NVENC
    ↓
IVF 临时视频流
    ↓
grav1synth 写入 Film Grain metadata
    ↓
恢复音频及容器内容
    ↓
grav1synth inspect 验证
    ↓
MP4 或 MKV
```

这里的 Grain 不会在编码阶段直接写进每一个像素。播放器解码 AV1 时，根据码流中的 Film Grain 参数实时生成颗粒。

### Film Preset

内置 Film 格式：

```text
Classic35
Modern35
16mm
Super8
MaxMid
```

Classic35、Modern35 和 16mm 还可以选择 Fujifilm Eterna 250D/500T、Kodak Vision3 250D/200T。

### Photon ISO

高级模式提供：

```text
ISO 400
ISO 800
ISO 1600
ISO 3200
ISO 6400
Custom ISO
```

并可选择 `Luma only` 或 `Luma + chroma`。

### 现成 Grain Table

AV1 可直接加载 `_AV1_Grain_Tables` 中的 `.tbl` / `.txt` Grain Table。目录推荐按以下分辨率分类：

```text
_AV1_Grain_Tables\
├─ 720p\
├─ 1080p\
├─ 1440p\
└─ 2160p\
```

GUI 会根据源视频分辨率自动匹配最接近的档位：

```text
≤ 1280  → 720p
≤ 1920  → 1080p
≤ 2560  → 1440p
> 2560  → 2160p
```

也可以勾选 Grain Table 列表旁的无文字复选框显示全部分辨率。

FGS 同时支持旧式 `1080p` 命名与 `3840x2160`、`3840x1600` 等实际宽高命名；同一档位中，带实际宽高的表会按与源视频尺寸的接近程度优先排序。电影画幅素材建议优先使用**与源或最终输出宽高完全一致**的 Grain Table，其次再按 2160p / 1440p / 1080p 档位近似匹配。

示例：

```text
LOTR FOTR Remastered · Light · 1080p · B/W · AOM
Star Trek TNG · Medium · 1080p · Color · SVT · P2
16mm · ISO 1000 · Medium · 1080p · Size8 · Photon
ISO 400 · 3840×2160 · sRGB · Photon
ISO 800 · 3840×2160 · BT.2020 · Photon
```

推荐来源：

- [Boulder08 / chunknorris](https://github.com/Boulder08/chunknorris) — `av1-graintables` 中包含多种影视来源、AOM / SVT 与 Photon Noise 表。
- [nekotrix / AV1-Photon-Noise-Tables](https://github.com/nekotrix/AV1-Photon-Noise-Tables) — 提供大量按实际分辨率生成的 Photon Noise 表，尤其适合 1440p / 4K。

项目正式包中整理的 Grain Table 继续按分辨率分类，具体上游来源见 `_AV1_Grain_Tables\README.txt`。第三方表文件仍受其原项目条款约束，FGS 只负责扫描、分类、选择和调用。

选择 Grain Table 后，Film 格式、Film stock、ISO 与 Chroma 参数自动禁用，因为颗粒参数已经由表文件本身决定。

### AV1 UHQ

当硬件能力探测确认当前 GPU、驱动与 FFmpeg 支持时，速度 / 质量中会出现 **UHQ**：

```text
preset：p4
tune：uhq
multipass：fullres
```

UHQ 由编码器自身进行更深入的时域分析和帧结构决策。能力探测未通过的环境不会显示该选项。

### AV1 SFE 多引擎并行

AV1 Standard / UHQ 可选 **“多引擎并行 ×N”**，对应 NVIDIA NVENC Split Frame Encoding (SFE)。

`×N` 由当前硬件实际编码引擎数量和能力检测决定，不按 GPU 型号硬编码。SFE 同时要求 grav1synth **0.2.2 或更高版本**；AV1 FAST、HEVC 与 x264 不使用该选项。

### AV1 Film Grain 最终验证

AV1 任务完成前会运行：

```text
grav1synth inspect
```

只有最终输出中的 Film Grain 信息通过检查，任务才计为成功。

相关项目：

- [rust-av / grav1synth](https://github.com/rust-av/grav1synth)
- [本项目使用的 Windows 修订版](https://github.com/rampageX/grav1synth)

---

## HEVC：真实扫描 Grain Plate

HEVC 主线适合将真实扫描 Grain Plate 直接合成到视频像素，再使用 Main10 NVENC 编码。

```text
原始视频 + 真实扫描 Grain Plate
            ↓
     Vulkan Overlay
            ↓
  P010 / HEVC Main10 NVENC
            ↓
        MP4 或 MKV
```

主要特点：

- 递归扫描 Grain 根目录中的 `.mov` Grain Plate；
- 支持真实 35mm、Super 35、16mm、Super 16、8mm 及自定义素材；
- Film Grain Strength 连续可调；
- 使用 Vulkan 路径完成 Grain 缩放与合成；
- 支持 LUT、Cinematic Style、反交错、自动电影帧率、字幕、OpenSVPFlow、MP4 / MKV 与多文件处理；
- HDR 输入可进入 Main10 HDR Preserve 工作流。

### Grain Cache

主脚本会自动查找与原始 Grain MOV 同目录、同名的缓存：

```text
*_1080p_HEVC_Lossless.mkv
*_HEVC_Lossless.mkv
```

选择规则：

```text
≤ 1920×1080 → 优先使用 1080p Cache
> 1920×1080 → 优先使用原分辨率 Cache
找不到适用 Cache → 回退到原始 Grain MOV
```

生成工具：

```text
Utils\FilmGrain_MOV_to_HEVC_Lossless_Cache.bat
```

可生成原始分辨率 Cache、1920×1080 Cache，或同时生成两种。GUI 的 **配置 → Grain 根目录** 也会检查 Cache 完整度，缺失时可以直接补建而不覆盖已有文件。

Cache 校验采用实际 10-bit sample-exact 思路：从与 NVENC 相同的 P010 帧流中分出参考路径，规范化为 `yuv420p10le` 后计算 SHA-256，再与 HEVC 解码结果比较，从而避免不同 GPU / 驱动对 P010 低位填充实现差异造成假失败。

免费 Grain Plate 素材：

- [TDCAT Free DCI 4K Film Grain Plates](https://tdcat.squarespace.com/downloads/filmgrain)
- [Cinema Tools 4K DCI 35mm Film Grain](https://www.cinematools.co/film-grain)

推荐目录结构：

```text
D:\Film_Grain\
├─ CinemaTools\
├─ TDCAT-Light\
└─ TDCAT-Heavy\
```

---

## H.264：x264 Grain

H.264 x264 Grain 与 HEVC 共用真实扫描 Grain、LUT、Cinematic Style、字幕、反交错、Field-rate、OpenSVPFlow 与 Grain Cache 等前处理，只在最终编码器边界切换为 libx264。

默认编码：

```text
libx264
preset faster
tune grain
VBR 单次
High Profile / yuv420p
```

高级设置可选择：

```text
Preset：Faster / Medium / Slow
码率模式：VBR 单次 / VBR 2-Pass
```

`2-Pass` 主要用于更精确地分配给定平均码率 / 文件大小，不是颗粒保留的必要条件。FGS 的 x264 Grain 路线始终把 `tune grain` 和足够码率作为核心。

普通 H.264 主线尽量维持 10-bit 前处理，到最终编码边界再转换为兼容性更好的 8-bit High Profile；能力探测通过时使用 error-diffusion dither。

高级设置提供 **H.264 High10（实验）**。启用并通过能力检测后，最终编码使用：

```text
yuv420p10le
High 10 Profile
```

High10 更适合本地测试和高位深链路验证；浏览器、电视、硬件解码和部分平台兼容性明显低于普通 H.264 High，因此不会用于附加 H.264 上传副本。

---

## Digital Grain · Fast Noise

Digital Grain 是 AV1 / HEVC / x264 三条主线都可调用的通用**像素颗粒引擎**。它不依赖外部 Grain Plate，也不是 AV1 Film Grain metadata；颗粒在编码前直接烘焙进像素。

强度使用连续滑杆：

```text
0.10 – 1.00
```

默认 `0.55`。当前实测参考：

| 强度 | 观感 |
|---:|---|
| 0.30 | 轻微 |
| 0.40 | 轻 |
| 0.55 | 中等 |
| 0.68 | 约接近 HEVC CT35 85% 的颗粒存在感 |
| 0.75+ | 明显 / 偏重 |

SDR 路径使用约 1.333× 分辨率的 Fast Noise 生成、形态学整形、亮度相关 Mask 后回缩到输出分辨率；相比早期 GEQ 随机颗粒参考实现，在 1080p / 1440p 实测具有明显的滤镜性能优势。

HDR Preserve 下，AV1 / HEVC 使用 10-bit 亮度颗粒路径，只修改 Y 平面，U/V 色度保持不变。HDR 与 SDR 的强度数值不要求严格视觉等效，仍建议以最终画面观感微调。

---

## GPU Film Grain (FGSIM)

GPU Film Grain (FGSIM) 是另一条**像素烘焙型** Film Grain 路线，使用已验证的 GPU shader / Vulkan / libplacebo 工作流生成颗粒。

主界面仍使用统一的 **Film Grain Strength** 控件；内部将强度映射到已经验证的 Light / Medium / Heavy 三档，而不是在界面上暴露三套独立算法。

FGSIM 与 BWDIF Vulkan 共用同一个 Vulkan filter device，避免重复创建 filter device。

### HEVC + FGSIM 码率

HEVC + FGSIM 默认仍使用正常的自动或手动 VBR。对于少数纯色渐变、天空等容易出现色带的素材，可在视频码率中主动尝试：

```text
Standard CQ27 / QP18-26
High Quality CQ23 / QP18-24
```

CQ 不是默认模式，而是针对色带问题的可选解决方案。界面提示保持为：

> HEVC+FGSIM 模式下, 若画面出现色带，请在视频码率中尝试 Standard CQ27 或 High Quality CQ23 方案。

---

## HDR Preserve / HDR→SDR

FGS 会根据 FFprobe 结果识别 HDR10/PQ 或 HLG。HEVC Main10 与 AV1 Main10 的 HDR Preserve 路线会尽量保持源视频已有的 HDR 信号，而不是人为伪造源中不存在的 mastering metadata。

当前保持与验证的项目包括：

- 实际 10-bit 视频路径；
- BT.2020 Color Primaries；
- PQ / SMPTE ST 2084，或 HLG / ARIB STD-B67；
- BT.2020 non-constant matrix；
- Limited / Full Range（按源信号）；
- 源文件本来存在的 Mastering Display、MaxCLL、MaxFALL 等 HDR10 静态元数据。

HEVC NVENC 会显式使用 `p010le`，避免只显示 Main 10 Profile、实际码流仍为 8-bit。AV1 Main10 继续使用 10-bit NVENC 路线。

主输出完成后会再次检查 HDR Primaries / Transfer / Matrix / Range；HDR 信号不匹配时不把结果作为正常完成输出。

FGS 的原则是：

> **保留源已有的 HDR 信息，不伪造源不存在的 HDR mastering metadata。**

因此某些网络 HDR 文件只有 BT.2020 + PQ + 10-bit、没有 Mastering Display / MaxCLL / MaxFALL 时，输出也不会人为补写虚构值。

### HDR 处理策略

高级设置 → HDR 提供：

```text
自动：仅不兼容流程转 SDR
保持 HDR
强制转换为 SDR
```

自动模式下，若当前任务需要 x264 主输出、BT.709 LUT、OpenSVPFlow 或 H.264 上传版等 SDR-only 流程，HDR 输入会先 Tone Mapping 为 BT.709 SDR，再进入后续处理。

Tone Mapping 默认使用 **Hable**，还可选择：

```text
Mobius
Reinhard
Gamma
Linear
Clip
```

HDR→SDR 会建立随机后缀的 10-bit HEVC Main10 无损工作文件，任务结束或失败后清理。工作文件仅作为内部中间层，不改变正式输出命名策略。

当前不处理 Dolby Vision RPU 或 HDR10+ 动态 metadata；特殊 HDR、非常规 mastering metadata 与 Dolby Vision 素材建议先使用短片验证。

---

## Cinematic Style

AV1 / HEVC / H.264 共用约 **2.39:1** 的 Cinematic Style。

两种方式：

- **加黑边 · 保留原分辨率（默认）**：例如 1920×1080 仍输出 1920×1080，将上下纯黑区域直接烘焙进视频；
- **裁剪 · 输出有效 2.39:1 画面**：例如 1920×1080 输出约 1920×804，只保留有效画面。

需要后期把字幕放在黑边区域时，推荐默认“加黑边”。需要减少无效像素时可选择“裁剪”。

对于真实扫描 Grain 路线，黑边在 Grain 合成之后添加，因此纯黑区域不会叠加扫描颗粒。

---

## 自动反交错与 Field-rate

GUI 默认启用自动反交错，并根据 FFprobe 的 `field_order` 判断输入是否为隔行素材。CLI 提供相同逻辑。

| 模式 | 定位 | Field-rate |
|---|---|---|
| BWDIF Vulkan | 默认 | `send_field` |
| BWDIF CUDA | 备选 | `send_field` |
| W3FDIF Complex | 高质量对照 | `mode=field` |
| 关闭 | 不进行反交错 | — |

当 `field_order` 为 `tt`、`bb`、`tb` 或 `bt` 时，自动反交错启用，并按“一场一帧”输出：

```text
29.97i → 59.94p
25i    → 50p
```

Field-rate 输出优先于普通电影帧率选择。progressive / unknown 素材保持正常逐行帧率逻辑。

---

## 自动电影帧率

对于逐行素材，自动模式会先把 FFprobe 返回的平均帧率分数换算为数值，再识别 VFR 和数学上等价的非标准分数，例如 `60/2` 或 `19001/317`。

| 源帧率族 | 输出 |
|---|---|
| 23.976 / 29.97 / 47.952 / 59.94 / 119.88 附近 | 23.976 CFR |
| 24 / 25 / 30 / 48 / 50 / 60 / 100 / 120 附近 | 24.000 CFR |
| 无法可靠归类的特殊帧率 | 保持源帧率 |

隔行素材启用自动反交错时直接采用 Field-rate ×2，不再走普通电影帧率转换。

特殊 VFR 素材仍建议检查音画同步。

---

## OpenSVPFlow GPU 60 fps 插帧

首次使用前运行：

```text
_OpenSVPFlow\00_Setup.bat
```

安装器会在 `_OpenSVPFlow` 内建立本地运行环境。安装完成并重新启动 Film Grain Studio 后，硬件能力检测会执行 CPU 与 GPU/OpenCL smoke test；只有检测通过时才允许启用插帧，不按 GPU 型号硬编码。

主界面提供：

| 模式 | SmoothFps scene.mode | 定位 |
|---|---:|---|
| 平滑 | 0 | 默认；Uniform |
| 自动平衡 | 3 | Adaptive |

当前默认插帧基线：

```text
60 fps
SmoothFps Algo 13
Super: pel=1 / gpu=1 / full=true
Analyse: EncodeGUI-inspired profile
Mask: cover=80 / area=100 / area_sharp=1.2
GPU/OpenCL
```

右上角 **高级 → 插帧** 可以调整 SmoothFps Algo、Analyse Profile 与 Artifact Mask Area，并恢复推荐值。

启用后，输出帧率由 OpenSVPFlow 接管为 60 fps；普通自动电影帧率 / 保持源帧率不再同时生效。

OpenSVPFlow 只处理逐行输入。隔行素材会自动旁路插帧并进入正常 Field-rate 反交错路线。处理链使用 **VSPipe Y4M → FFmpeg 管道**，不生成巨大的中间视频文件。

当前已验证的 OpenSVPFlow 内部工作格式为 **YUV420P8 SDR**。HDR 素材如果需要插帧，应由 HDR 自动兼容逻辑先 Tone Mapping 到 SDR，再进入 OpenSVPFlow，而不是把这条路径视为端到端 HDR / 10-bit 插帧。

`_OpenSVPFlow\01_Update_OpenSVPFlow.bat` 可主动更新上游最新版；更新前会备份当前插件，只有新版通过 CPU / GPU smoke test 后才保留，失败则回滚。

---

## LUT Gallery 与 Film Look

默认 LUT 根目录：

```text
E:\Adobe Portable\LUTs
```

可以使用：

```text
Utils\LUT_Preview_Batch_Gallery.bat
```

递归生成 LUT 缩略图与 Gallery 索引。

Gallery 支持：

- 全部 LUT、最近使用和我的最爱；
- 搜索、文件夹筛选与缩略图；
- 页码下拉、上一页 / 下一页、PageUp / PageDown；
- 右键菜单、收藏、双击选择、Enter 确认与 Esc 取消；
- 25% / 50% / 75% / 100% LUT 强度；
- Resolve CUBE 兼容处理与 tetrahedral 插值。

### 参考图

出厂参考图：

```text
_LUT_Tools\LUT_Reference_Default.jpg
```

在 Gallery 中选择“更换参考图”后，会生成：

```text
_LUT_Tools\LUT_Reference_Current.jpg
```

之后 Gallery 全量重建、GUI 配置页补建缺失缩略图以及独立预览生成器都优先使用当前参考图；不存在时才回退默认图。

`LUT_Reference_Current.jpg` 属于用户运行时状态，正式发布包默认不预置。

### 更新预览图

Gallery 可以直接同步预览：

- 只补建新增或缺失的 LUT 预览；
- 已存在的有效预览不重复生成；
- 对已经从 LUT 库中删除、且旧 Gallery 索引明确记录的项目执行保守清理；
- 不递归删除预览目录；
- 更新后立即刷新匹配数量和当前总数。

### 智能过滤

智能过滤的目标是从大型 LUT 库中**隐藏高置信度的纯技术转换 LUT**，而不是删除任何文件。

分类结果：

```text
Technical
Combined
Creative
Keep
```

只有 `Technical` 会被隐藏；`Combined / Creative / Keep` 继续显示。

判断会综合 `.cube` 文件头、文件名和目录上下文，例如：

```text
Input / Output Color Space
Utility / Technical / Transform / Conversion
CST / IDT / ODT
Log → Rec.709
Tone Map / Gamut Map
Full / Legal Range
Shaper
```

同时会跨文件识别 Creative Family：同一个创意 Look 如果同时有 S-Log3、V-Log、LogC、Rec.709 等多个输入适配版本，则优先判为 Combined 并保留，避免把内部包含技术转换的创意 LUT 当成纯 Utility LUT。

智能过滤：

- 不移动 LUT；
- 不删除 LUT；
- 不重命名 LUT；
- 只改变 Gallery 当前显示；
- 报告写入 `<LUT_ROOT>\_LUT_PREVIEWS\_LUT_SMART_FILTER_REPORT.csv`。

### 最近使用与我的最爱

LUT Gallery 是 Recent / Favorites 数据的主要维护界面。Studio 主界面的两个下拉列表读取这些数据并复用 Gallery 缩略图。

Recent 保持去重并限制数量；从 Gallery 正式选择 LUT 时更新 Recent，从 Studio 的 Favorites 选择 LUT 后则在实际开始编码时登记 Recent。

---

## 字幕

字幕功能独立于 H.264 上传副本，可以直接烧写进主 AV1、HEVC 或 H.264 输出。

处理逻辑：

- HEVC：字幕直接烧写进主 HEVC 处理链；
- AV1：字幕烧写进 Main10 基础画面，再注入 Film Grain metadata；
- H.264 x264 Grain：字幕在前处理阶段完成后进入最终 x264 编码；
- 同时生成 H.264 上传副本时，副本继承同一套字幕设置，不重复烧写；
- 带字幕的主输出文件名增加 `_SUB`。

字幕来源支持：

- 内嵌文本字幕；
- 同目录同名 `.srt / .ass / .ssa / .vtt`；
- 浏览外部字幕文件；
- 多文件任务自动匹配。

外部字幕会优先按 Unicode BOM / 严格 UTF-8 识别；不是合法 UTF-8 时自动回退 **GB18030**，兼容常见 GBK / ANSI 中文字幕。

默认样式：

```text
字体：huiwen-mincho
字号：69（1920×1080 基准）
颜色：白色
Outline / Shadow：黑色 1 / 1
距最终输出底部：5 px
对齐：水平居中
```

字号、边距、描边和阴影可在 GUI 中调整，并按输出宽度相对 1920 px 等比缩放。

字幕位置始终以**最终输出画面的底边**为基准。启用 Cinematic 黑边时，字幕自然落在下黑边；不启用黑边时则直接位于视频底部。

---

## 自动码率与高动态

AV1 / HEVC / H.264 共用可见的自动码率策略。主界面的“视频码率”直接显示计算后的 kbps，不是隐藏黑盒；编码开始后日志会再次输出分辨率档位、60 fps 基准、最终 FPS、FPS 系数、`b:v`、`maxrate` 与 `bufsize`。

### 60 fps 码率基准

| 输出档位 | AV1 普通 | HEVC 普通 | x264 普通 | AV1 高动态 | HEVC 高动态 | x264 高动态 |
|---|---:|---:|---:|---:|---:|---:|
| 720p | 3500k | 4000k | 5000k | 6000k | 7000k | 10000k |
| 1080p | 5000k | 6000k | 7500k | 9000k | 11000k | 15000k |
| 1440p | 7000k | 8000k | 10000k | 12000k | 15000k | 20000k |
| 2160p | 10000k | 12000k | 15000k | 18000k | 22000k | 30000k |

分辨率档位按最终输出画面长边选择：

```text
≤ 1280 → 720p
≤ 1920 → 1080p
≤ 2560 → 1440p
其余   → 2160p
```

最终码率再按最终输出 FPS 使用平滑系数：

```text
24 fps  ≈ 0.60
25 fps  ≈ 0.62
30 fps  ≈ 0.70
50 fps  ≈ 0.90
60 fps  = 1.00
120 fps ≈ 1.65
```

中间帧率线性插值，并按 500 kbps 步进取整。Field-rate 与 OpenSVPFlow 产生的最终 FPS 都会参与计算。

统一 VBV：

```text
maxrate = 平均码率 × 3
bufsize = 平均码率 × 6
```

### 高动态

“高动态”指高速运动 / 高复杂度画面，**不是 HDR**。

启用后除了提高自动码率，还会在能力探测允许时启用更适合复杂运动的编码器参数。取消“自动”后可以直接输入自定义 kbps；程序不会因为切换高动态而覆盖手动平均码率，但仍会按该值计算 3× / 6× VBV。

---

## 容器与音频

### MP4

默认输出容器。

主输出音频转换为：

```text
AAC 256 kbps
```

并启用 `faststart`。MP4 兼容模式不写入不兼容的字幕、附件和数据流。

### MKV

尽量复制并保留：

- 原始音频；
- 字幕；
- 附件；
- 数据流；
- 章节；
- metadata。

更适合完整归档。

---

## H.264 上传母版

视频平台通常会重新编码上传文件，因此 AV1 Film Grain metadata 很可能无法继续保留。FGS 可以额外生成一份将颗粒真正烘焙进像素的 H.264/AAC 上传母版。

### 主流程附加上传版

AV1 与 HEVC 主线都可启用 **“同时生成 H.264 上传版”**，OpenSVPFlow 60 fps 插帧开启时同样可用。

上传版默认：

```text
libx264 / High / yuv420p
preset faster
tune grain
VBR 单次
AAC 256 kbps / stereo / 48 kHz
```

上传版使用独立的视频码率框与“自动”开关，但自动码率策略与主线 x264 Grain 一致。Preset 和 VBR 单次 / 2-Pass 则与主线共享高级设置。

上传版始终保持标准 H.264 High 8-bit，以平台兼容性为优先；H.264 High10 只作用于 H.264 x264 Grain 主输出。

AV1 上传版会从最终 AV1 主成片读取，并通过 `libdav1d` 将 Film Grain metadata 合成为真实颗粒像素后再压制。

HEVC 未启用 OpenSVPFlow 时，从原始视频重新走 Grain / LUT / 反交错 / Cinematic 等处理链；启用 OpenSVPFlow 时则直接复用已经完成 60 fps 插帧和前处理的最终 HEVC 主成片，避免重复运行插帧。

### 独立转换工具

将一个或多个已经完成 Film Grain / 插帧等处理的 AV1 或 HEVC 成片拖到：

```text
Utils\AV1_FilmGrain_Bake_for_Social_Upload.bat
```

脚本先检测输入编码：

- AV1：使用 `libdav1d` 解码并把 AV1 Film Grain metadata 合成为真实像素颗粒；
- HEVC 等其它输入：由 FFmpeg 使用正常解码器。

随后统一使用：

```text
libx264
preset slow
tune grain
2-pass
AAC 256 kbps / stereo / 48 kHz
```

该工具适合对已经完成 Film Grain / 插帧等处理的成片单独补做 H.264 上传母版。

---

## 为现有 AV1 免重编码添加或替换 Film Grain

独立工具：

```text
Utils\AV1_Grav1synth_Add_Replace_FilmGrain_NoReencode.bat
```

处理流程：

```text
现有 AV1 视频
    ↓ stream copy
IVF
    ↓ grav1synth --replace
带新 Film Grain 的 AV1
    ↓ remux
MKV 或 MP4
    ↓
grav1synth inspect
```

特点：

- AV1 视频流不重新编码；
- 没有 Film Grain 时执行添加，已有时执行替换；
- 输出命名区分 `_ADDED` / `_REPLACED`；
- 连续替换时清理旧的 FGS Grain 标签，避免文件名无限增长；
- 默认 MKV 尽量保留原始流；
- MP4 模式将音频转换为 AAC 256 kbps；
- 失败时默认保留临时目录和日志；
- 非 AV1 视频会跳过。

该功能也已经接入 GUI。当输入列表中只有 **1 个 AV1 文件**且媒体探测完成后，可以选择：

```text
AV1 不重编码 · 添加/替换胶片颗粒
```

此模式会禁用码率、速度、反交错、LUT、Cinematic、上传版等需要重编码的功能，只保留 AV1 Film Grain 参数与容器选择。

> “No Re-encode”仅指视频流。选择 MP4 时，音频仍会转换为 AAC。

---

## 多语言界面

GUI 使用独立语言文件：

```text
Lang\zh-CN.ini
Lang\en-US.ini
Lang\FilmGrain_Language.ini
Utils\FilmGrain_Language.ps1
```

其中：

- `zh-CN.ini`：简体中文，同时作为缺失翻译的 fallback；
- `en-US.ini`：English；
- `FilmGrain_Language.ini`：保存当前语言选择；
- `FilmGrain_Language.ps1`：负责 UTF-8 语言文件读取、fallback、语言枚举与偏好保存。

右上角可以选择界面语言。保存后重新打开 Film Grain Studio 生效。

多语言只影响 GUI 显示文字，不改变：

- StudioBridge；
- BAT / CLI 参数；
- 编码器内部 ID；
- 码率模式；
- 配置协议；
- 输出命名。

简体中文保持原来的主窗口布局；English 使用更宽的默认 / 最小窗口宽度，以避免较长英文文本压缩右侧控件。未来新增语言时，也可以在语言文件中定义适合该语言的窗口宽度。

---

## 环境与统一路径配置

外部路径统一保存在根目录：

```text
FilmGrain_Config.ini
```

GUI 右上角的 **配置** 可以修改并保存。GUI、CLI、StudioBridge 与相关 Utils 工具读取同一份配置，不再分别维护硬编码路径。

默认值：

```text
GPU：NVIDIA GPU 自动检测
FFmpeg 目录：E:\EnCoder\FFMpeg\x64\bin
grav1synth：E:\EnCoder\FFMpeg\grav1synth\grav1synth.exe
HEVC Grain 库：D:\Film_Grain
LUT 根目录：E:\Adobe Portable\LUTs
```

配置界面中：

- FFmpeg / FFprobe 显示版本；
- grav1synth 显示版本；
- Grain 根目录统计原始 MOV、原分辨率 Cache 与 1080p Cache；
- LUT 根目录统计 `.cube` LUT 与 Gallery 预览；
- 浏览选择后立即检测；
- 手工输入后由用户点击 `↻` 再检测；
- 保存时不重新执行长时间扫描。

`FilmGrain_Config.ini` 使用 UTF-8 无 BOM。PS1 显式按 UTF-8 读写；BAT 通过统一配置读取层处理编码和 CMD 特殊字符。

### grav1synth 0.2.2+

AV1 Film Grain 与 AV1 SFE 路线推荐使用 **grav1synth 0.2.2 或更高版本**。0.2.2 修复 NVENC Split Frame Encoding 输出中 standalone `OBU_FRAME_HEADER / OBU_TILE_GROUP` 的兼容问题。

下载最新版：

[rampageX / grav1synth Releases](https://github.com/rampageX/grav1synth/releases/latest)

---

## 硬件能力自动探测

GUI 和 CLI 启动时调用：

```text
Utils\FilmGrain_Hardware_Caps.ps1
```

通过小型实际编码测试判断当前 GPU、NVIDIA 驱动与 FFmpeg 的真实能力，而不是根据 GPU 型号写死参数。

| 能力 | 自动处理 |
|---|---|
| AV1 / HEVC NVENC + x264 Grain | 实际验证编码路径；不可用时隐藏或回退 |
| Main10 / High10 | 验证 AV1 / HEVC 10-bit、x264 High10 与 10→8bit dither |
| B-frame / B-reference | 不支持时不传递相关参数 |
| Spatial AQ / Temporal AQ | 分别探测并按能力启用 |
| Lookahead / Multipass | 按 fullres / qres 实际支持情况选择 |
| NVDEC CUDA / Vulkan | 只在实际路径通过后启用 |
| AV1 UHQ | `-tune uhq` 微型编码成功时才显示 |
| AV1 SFE | 检测 NVENC 编码引擎数量并验证 `-split_encode_mode N` |
| OpenSVPFlow GPU/OpenCL | 本地 VSPipe、插件与 GPU/OpenCL smoke test 均通过后才启用 |

检测结果缓存到：

```text
Utils\_HardwareCaps.json
```

GPU、驱动、FFmpeg 文件或探测规则变化后会自动重新检测；环境未变时直接读取缓存。

GUI 底部状态栏集中显示 GPU、NVIDIA 驱动版本、FFmpeg 版本、能力缓存状态以及 AV1 / UHQ / HEVC-Vulkan / OpenSVPFlow 可用性。

---

## 当前默认设置

| 项目 | 默认值 |
|---|---|
| 编码与 Grain 方式 | AV1 Main10 + grav1synth |
| 输出容器 | MP4 |
| 音频 | AAC 256 kbps |
| 速度模式 | FAST：p5 / qres multipass / lookahead 16 |
| Cinematic Style | 开启；加黑边、保留原分辨率 |
| 反交错 | 自动；BWDIF Vulkan |
| 输出帧率 | 隔行 Field-rate ×2；逐行自动电影帧率 |
| OpenSVPFlow | 关闭；启用后 60 fps |
| GPU | 自动检测 |
| LUT | 关闭 |
| AV1 Grain | Film Preset |
| AV1 Film Preset | Classic35 / Fujifilm Eterna 250D |
| 视频码率 | 自动推荐并直接显示 |
| 高动态 | 关闭 |
| x264 Preset | Faster |
| x264 码率模式 | VBR 单次 |
| H.264 High10 | 关闭 |
| H.264 上传副本 | 关闭 |
| 界面语言 | 简体中文 |

---

## 工具包结构

```text
FilmGrain_Universal_HEVC_AV1_CLI.bat
FilmGrain_Universal_HEVC_AV1_GUI.bat
FilmGrain_Config.ini
README.md
CHANGELOG.md
README_FilmGrain_Studio.txt
README_Toolkit.txt
STABLE_BASELINE.txt

Lang\
    zh-CN.ini
    en-US.ini
    FilmGrain_Language.ini

Utils\
    FGS_Benchmark.cmd
    FGS_Benchmark.ps1
    FilmGrain_Language.ps1
    FilmGrain_Config.ps1
    FilmGrain_Config_Load.bat
    FilmGrain_Hardware_Caps.ps1
    FilmGrain_Studio.ps1
    FGS.ico
    FilmGrain_Studio_Launcher.vbs
    FilmGrain_Universal_HEVC_AV1_StudioBridge.bat
    FilmGrain_Subtitle_Prepare.ps1
    AV1_FilmGrain_Bake_for_Social_Upload.bat
    AV1_Grav1synth_Add_Replace_FilmGrain_NoReencode.bat
    LUT_Preview_Batch_Gallery.bat
    Collect_BT709_LUTs_Conservative.bat
    FilmGrain_MOV_to_HEVC_Lossless_Cache.bat

_LUT_Tools\
    LUT_Gallery_Selector.ps1
    LUT_Preview_Batch_Gallery.ps1
    LUT_Reference_Default.jpg
    LUT_Reference_Current.jpg

_AV1_Grain_Tables\
    README.txt
    720p\
    1080p\
    1440p\
    2160p\

_OpenSVPFlow\
    00_Setup.bat
    Setup_OpenSVPFlow.ps1
    01_Update_OpenSVPFlow.bat
    Update_OpenSVPFlow.ps1
    Check_OpenSVPFlow.vpy
    FilmGrain_OpenSVPFlow.vpy
    Plugins\
```

请保持两个入口 BAT、`Lang`、`Utils`、`_LUT_Tools`、`_AV1_Grain_Tables` 与 `_OpenSVPFlow` 的相对位置不变。

---

## FGS Benchmark 基准测试

将一个测试视频拖到 `Utils/FGS_Benchmark.cmd`；配套的 `FGS_Benchmark.ps1` 必须保持在同一目录。工具读取现有 FGS 路径配置，通过同一个 StudioBridge 核心执行 B01–B09 与 Q01–Q03 共 12 项测试，覆盖 HEVC / AV1 / x264 的颗粒路线，以及 HEVC FGSIM 的 VBR、CQ27、CQ23 对照。

1. 输入截图帧号，例如 `247,865,1420`；从 **0** 开始计数，且相对于当前测试片段。留空则不截图。
2. 输入 Grain Plate **原始 MOV 文件的完整路径（含文件名）**，例如 `D:\Film_Grain\CT 35mm Grain 4K DCI\CT 35mm Grain 4K DCI.mov`。StudioBridge 会按现有规则复用 HEVC 无损缓存；不要直接指定 `_HEVC_Lossless.mkv`。留空时 B03/B09 标记为 SKIPPED。
3. 结果保存在源视频目录下的 `FGS_Benchmark_随机数_随机数`，包含 `Benchmark.csv`、`Benchmark.md`、`Environment.txt`、`Source_Info.txt`、`Logs`、`Outputs` 和 `Screenshots`。隔离的 `Work` 目录用于暂存输入和失败中间文件。

统计 FPS 是实际输出帧数除以完整 Bridge 耗时，不是视频播放帧率或 FFmpeg 瞬时速度；截图、结果探测和文件搬移不计入该耗时。越界截图会记录问题，不会替换为其它帧。

两组实测报告与截图见 [Benchmark 文档与样本](https://github.com/rampageX/FilmGrain/tree/master/benchmark)：Taylor Swift - Look What You Made Me Do 片段第 **313** 帧，以及 LG OLED DAYDREAMS 测试片段第 **543** 帧。单帧用于观察特定场景，不代表整段平均画质；颗粒强度未做视觉等效标定，结果也不是严格的同码率编码器排名。

**`benchmark/` 仅保留在仓库，正式发布 ZIP 排除整个目录；两个 Benchmark 脚本随正式包提供。**

---

## 其他 Utils 工具

| 文件 | 用途 |
|---|---|
| `Collect_BT709_LUTs_Conservative.bat` | 保守筛选明确标注 BT.709 / Rec.709 输入的 CUBE LUT，并生成 CSV 报告 |
| `FilmGrain_MOV_to_HEVC_Lossless_Cache.bat` | 生成原分辨率、1080p 或两种 HEVC Main10 Lossless Grain Cache，并进行 10-bit sample-exact 校验 |
| `LUT_Preview_Batch_Gallery.bat` | 生成 LUT Gallery 缩略图和索引 |
| `AV1_Grav1synth_Add_Replace_FilmGrain_NoReencode.bat` | 为现有 AV1 添加或替换 Film Grain metadata，不重新编码视频流 |
| `AV1_FilmGrain_Bake_for_Social_Upload.bat` | 将 AV1 / HEVC 成片转换为 H.264 x264 Grain 上传母版 |

---

## FFmpeg 与 NVIDIA 驱动

当前已验证环境使用：

```text
NVIDIA Driver 616.86
FFmpeg 9.0.1
```

默认 FFmpeg / FFprobe 目录：

```text
E:\EnCoder\FFMpeg\x64\bin
```

项目不根据固定 GPU 型号或 NVENC API 版本直接决定功能开关，实际可用能力仍由启动时的小型编码测试决定。最低驱动要求取决于当前 FFmpeg 构建采用的 NVENC API / `nv-codec-headers`。

更换 GPU、升级 NVIDIA 驱动或替换 FFmpeg 后，能力缓存会自动失效并重新检测。

需要人工检查时可以使用：

```bat
ffmpeg -hide_banner -encoders | findstr /i "hevc_nvenc av1_nvenc"
ffmpeg -hide_banner -filters  | findstr /i "blend_vulkan scale_vulkan bwdif bwdif_vulkan bwdif_cuda w3fdif"
ffmpeg -hide_banner -hwaccels | findstr /i "cuda vulkan"
ffmpeg -hide_banner -h decoder=libdav1d
```

升级驱动或 FFmpeg 后建议至少重新测试：

- HEVC / AV1 Main10；
- 29.97i → 59.94p、25i → 50p；
- Vulkan Grain 合成；
- 23.976 / 24 fps 转换后的时长与音画同步；
- MP4 / MKV 音频、字幕、附件与章节行为；
- AV1 最终 `grav1synth inspect`；
- 实际编码速度。

不要把外部 FFmpeg 目录中的 DLL 覆盖到 grav1synth 目录，两者的运行时依赖应保持独立。

---

## 已知限制

- 仅面向 Windows BAT、Windows PowerShell 5.1 与 WinForms 工作流；
- 需要 NVIDIA GPU；AV1、UHQ、B-frame、AQ、NVDEC、Vulkan 等能力取决于 GPU、驱动和 FFmpeg 组合；
- HEVC / H.264 扫描 Grain 会增加编码压力和码率需求；
- H.264 x264 Grain 使用 CPU 编码，选择 Medium / Slow 或 2-Pass 会显著增加耗时；
- AV1 Film Grain 的显示依赖播放器和解码器正确实现 Film Grain Synthesis；
- 部分平台和转码软件会移除 AV1 Film Grain metadata；
- MP4 兼容模式不会保留字幕、附件和数据流；
- 启用 LUT 时，部分处理链会转为软件滤镜路径，速度可能下降；
- 自动反交错依赖 FFprobe `field_order`，异常标记素材需要人工确认；
- OpenSVPFlow 仅处理逐行输入并固定输出 60 fps；
- OpenSVPFlow 当前内部为 YUV420P8 SDR，不是端到端 HDR / 10-bit 插帧；
- H.264 High10 为实验功能，兼容性明显低于标准 H.264 High 8-bit；
- HDR Preserve 主要覆盖 AV1 Main10 / HEVC Main10；Dolby Vision RPU 与 HDR10+ 动态 metadata 不在当前处理范围；
- LUT 智能过滤属于保守启发式分类，不保证能从任意 `.cube` 数值表完全推断创作者意图；
- 特殊 HDR、VFR、多视频流和非常规容器建议先使用短片验证；
- 强制取消任务可能留下未完成输出或 `__AV1GS_TMP_*` 临时目录；
- 重要素材应保留原文件，并在归档前检查画面、音频、时长、流信息和 Film Grain 验证结果。

---

## 如何选择

**AV1 + grav1synth**  
适合重视低码率效率、批量处理，以及希望在较低码率下仍保留明显 Film Grain 的场景。Film Grain 由播放器解码时合成。

**HEVC + 真实扫描 Grain**  
适合重视真实 Grain Plate 质感、10-bit 主输出和 NVENC 编码速度的场景。

**H.264 x264 Grain**  
适合需要真实像素颗粒，同时更重视 H.264 平台兼容性和 `tune grain` 颗粒保留的场景。

**Digital Grain**  
适合不想准备 Grain Plate、希望直接使用连续强度控制和较高处理速度的场景。

**GPU Film Grain (FGSIM)**  
适合希望使用 GPU shader 路线生成颗粒的场景；HEVC 遇到少数色带素材时可尝试 CQ27 / CQ23 方案。

当前默认：

> **AV1 Main10 + grav1synth Film Grain，输出 MP4 / AAC 256k。**

需要上传到会二次转码的视频平台时，再额外生成一份将 Grain 烘焙到像素的 H.264 / AAC 上传母版。
