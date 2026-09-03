# Film Grain Studio

![](images/Film_Grain_Studio_v3.1.jpg)

基于 **FFmpeg、NVIDIA NVENC 与 grav1synth** 的 Windows 视频胶片化工具包，同时提供图形界面和命令行入口。

项目包含两条可切换的 Film Grain 处理路线：

- **HEVC Main10 + 真实扫描 Grain Plate**：将真实胶片颗粒合成到视频像素中。
- **AV1 Main10 + grav1synth Film Grain**：将颗粒模型写入 AV1 Film Grain metadata，由播放器在解码时合成。

当前正式稳定版为 **v3.2**，发布包名称：

```text
FilmGrain_Studio_v3.2_Stable.zip
```

所有独立脚本使用固定文件名，不再包含组件版本号；版本号只体现在整个项目的发布压缩包上。升级时建议完整替换工具包，避免新旧脚本混用。

默认配置为 **AV1 Main10 + MP4 + AAC 256k**，并集成 LUT Gallery、自动电影帧率、Cinematic Style、多文件处理、NVENC 硬件能力自动探测、AV1 UHQ 及 AV1 Film Grain 最终验证。

## v3.2 更新摘要

- LUT Gallery 新增 **更换参考图**，可直接选择新图片并覆盖生成全部 LUT 预览；
- 生成期间显示独立进度窗口，Gallery 暂时锁定操作，完成后自动刷新缩略图；
- 将当前页码输入框改为只读下拉菜单，显示当前页并可直接选择任意页面；
- 保留上一页/下一页、PageUp/PageDown 首尾循环、搜索、文件夹筛选、Recent 与收藏的原有行为。

## v3.1 更新摘要

- 新增 NVIDIA GPU、驱动与 FFmpeg 实际能力探测，不再依赖 RTX 4080 / T600 固定型号配置；
- 自动检测 AV1、HEVC、H.264 NVENC、Main10、B-frame、B-reference、Spatial/Temporal AQ、Lookahead、Multipass、NVDEC CUDA 与 Vulkan，并只启用当前环境实际支持的参数；
- 新增 **AV1 UHQ** 模式；只有微型编码测试通过的 GPU / 驱动 / FFmpeg 组合才会显示；
- 新增 `_HardwareCaps.json` 能力缓存；首次探测显示 `Detected`，后续命中缓存显示 `Cached`；
- H.264 社交平台上传母版改为按分辨率自动使用 **6 / 8 / 10 / 12 Mbps**；
- 修复 T600 / RTX 4080 能力探测与缓存状态显示问题。

---

<table>
  <tr>
    <td width="50%" align="center"><a href="images/Original.jpg"><img src="images/Original.jpg" width="100%" alt="Original"></a><br><sub>Original Video</sub></td>
    <td width="50%" align="center"><a href="images/FG_CT35_V20FAST_HEVC_239LB_23976p.mkv_20260830_102847.766.jpg"><img src="images/FG_CT35_V20FAST_HEVC_239LB_23976p.mkv_20260830_102847.766.jpg" width="100%" alt="HEVC Real Grain"></a><br><sub>HEVC + Real Scanned Film Grain</sub></td>
  </tr>
</table>

---

## 两个正式入口

项目根目录只保留两个 BAT 入口：

| 文件 | 用途 |
|---|---|
| `FilmGrain_Universal_HEVC_AV1_GUI.bat` | 启动 Film Grain Studio 图形界面，推荐日常使用 |
| `FilmGrain_Universal_HEVC_AV1_CLI.bat` | 独立命令行版本，保留完整交互菜单与多文件拖放 |

GUI 与 CLI 使用一致的核心编码参数，并同步支持中文、空格以及 `&` 等 CMD 特殊字符路径。

---

## 两种 Film Grain 路线

真实胶片颗粒具有随机性、亮度相关性和持续变化的空间结构。将 Grain Plate 合成进像素，可以获得稳定、真实且不依赖播放器的效果，但也会增加编码压力和所需码率。

AV1 Film Grain Synthesis 采用另一种方式：编码相对干净的画面，并在码流中保存颗粒模型参数，播放时由解码器生成颗粒，因此更适合低码率和高速批量处理。

参考：[AOMedia AV1 Tool Description](https://aomedia.org/docs/AV1_ToolDescription_v11-clean.pdf)

| 项目 | HEVC + 扫描 Grain | AV1 + grav1synth |
|---|---|---|
| Grain 来源 | 真实胶片扫描素材 | Film Preset 或 Photon ISO 模型 |
| 是否写进像素 | 是 | 否，由解码器合成 |
| 低码率效率 | 较低 | 很高 |
| 播放兼容性 | 较好 | 依赖播放器正确支持 AV1 Film Grain |
| 画面一致性 | 不同播放器效果一致 | 可能受解码器实现影响 |
| 典型用途 | 收藏、真实扫描颗粒 | 高效率压缩、批量转码 |

两种方案各有用途，并不存在绝对替代关系。

---

## 工具包结构

```text
FilmGrain_Universal_HEVC_AV1_CLI.bat
FilmGrain_Universal_HEVC_AV1_GUI.bat
README.md
README_FilmGrain_Studio.txt
README_Toolkit.txt
Utils\
    FilmGrain_Hardware_Caps.ps1
    FilmGrain_Studio.ps1
    FilmGrain_Studio_Launcher.vbs
    FilmGrain_Universal_HEVC_AV1_StudioBridge.bat
    AV1_FilmGrain_Bake_for_Social_Upload.bat
    AV1_Grav1synth_Add_Replace_FilmGrain_NoReencode.bat
    LUT_Preview_Batch_Gallery.bat
    Collect_BT709_LUTs_Conservative.bat
    FilmGrain_MOV_to_HEVC_Lossless_Cache.bat
    FilmGrain_MOV_to_1080p_HEVC_Lossless_Cache.bat
_LUT_Tools\
    LUT_Gallery_Selector.ps1
    LUT_Preview_Batch_Gallery.ps1
    LUT_Reference_Default.jpg
```

请保持两个入口 BAT、`Utils` 与 `_LUT_Tools` 的相对位置不变。

---

## 环境与固定路径

当前项目按以下 Windows 环境配置：

```text
GPU：NVIDIA GPU（自动检测，已验证 RTX 4080 与 T600 Laptop）
FFmpeg：E:\EnCoder\FFMpeg\13.0\bin\ffmpeg.exe
FFprobe：E:\EnCoder\FFMpeg\13.0\bin\ffprobe.exe
grav1synth：E:\EnCoder\FFMpeg\grav1synth\grav1synth.exe
HEVC Grain 库：D:\Film_Grain
LUT 根目录：E:\Adobe Portable\LUTs
```

> `E:\EnCoder\FFMpeg\13.0` 中的 `13.0` 表示项目锁定使用的 NVENC API 13.0 兼容构建目录，不代表 FFmpeg 的正式主版本号。

### 硬件能力自动探测

GUI 和 CLI 启动时会调用 `Utils\FilmGrain_Hardware_Caps.ps1`，对当前 GPU、NVIDIA 驱动与 FFmpeg 执行小型实际编码测试。检测结果用于动态构造编码参数：

| 能力 | 自动处理 |
|---|---|
| AV1 / HEVC / H.264 NVENC | 仅使用实际可用的编码器；AV1 不可用时回退到 HEVC |
| Main10 | 验证 AV1 / HEVC 10-bit 实际编码 |
| B-frame / B-reference | 不支持时不传递相关参数 |
| Spatial AQ / Temporal AQ | 分别探测并按能力启用 |
| Lookahead / Multipass | 按 fullres、qres 的实际支持情况选择 |
| NVDEC CUDA / Vulkan | 只在通过实际路径测试后启用 |
| AV1 UHQ | 只在 `-tune uhq` 微型编码成功时显示 |

能力结果会写入 `Utils\_HardwareCaps.json`。GPU、驱动、FFmpeg 文件或探测规则变化后会自动重新检测；环境未变时直接读取缓存。新包首次启动显示 `Detected` 属于正常现象，关闭并再次启动后应显示 `Cached`。

RTX 4080 可自动启用 AV1、B-frame、AQ、Lookahead 及其他已通过测试的功能；T600 Laptop 会自动回退 HEVC，并移除不支持的 B-frame、Temporal AQ 等参数，无需手动切换硬件配置。

---

## 快速开始

### GUI 图形界面

双击：

```text
FilmGrain_Universal_HEVC_AV1_GUI.bat
```

也可以将一个或多个视频直接拖到该 BAT。启动脚本会在交接参数后退出，GUI 正常显示，同时不保留 CMD 或 Windows PowerShell 黑框。

基本流程：

1. 添加或拖入一个或多个视频。
2. 选择 AV1 或 HEVC、输出容器、码率、帧率和 GPU 配置。
3. 按需启用 Cinematic Style、Film Grain 与 LUT。
4. 点击“开始编码”。
5. 在任务区查看当前阶段、进度、`fps`、`speed`、`ETA` 与完整日志。

### CLI 命令行

将一个或多个视频拖到：

```text
FilmGrain_Universal_HEVC_AV1_CLI.bat
```

按菜单选择处理方式；直接回车采用默认值。全部任务结束后会显示成功、失败和跳过数量。

GUI 与 CLI 的输出均保存在源视频所在目录。已有同名输出时会跳过，不会直接覆盖。

---

## GUI 主要功能

- 多视频添加、拖放、移除与清空；
- 选中单个视频时异步显示视频/音频编码、码率、分辨率、帧率、声道、采样率、时长、容器与总码率；
- AV1 Main10 + grav1synth 与 HEVC Main10 + Scanned Grain；
- MP4 与 MKV 输出；
- FAST、Standard，以及能力探测通过后可选的 AV1 UHQ 编码模式；
- 常用码率及自定义 kbps 码率；
- 自动电影帧率或保持源帧率；
- NVIDIA GPU / 驱动 / FFmpeg 能力自动探测与缓存；
- AV1 Film Preset、Photon ISO、Film 格式、Film stock 与 Chroma Grain；
- HEVC Grain 根目录递归扫描，只显示电脑上实际存在的 `.mov` Grain Plate；
- 自动匹配 1080p 或原分辨率 HEVC Lossless Grain Cache；
- LUT Gallery、最近使用、我的最爱、缩略图预览、参考图更换及 LUT 强度；
- 结构化实时进度、`fps`、`speed`、`ETA`、日志复制/清空与任务取消。

---

## 当前默认值

| 项目 | 默认值 |
|---|---|
| 编码与 Grain 方式 | AV1 Main10 + grav1synth |
| 输出容器 | MP4 |
| 音频 | AAC 256 kbps |
| 速度模式 | FAST：p5 / qres multipass / lookahead 16 |
| Cinematic Style | 开启，约 2.39:1 |
| 输出帧率 | 自动电影帧率 |
| GPU | 自动检测 |
| LUT | 关闭 |
| AV1 Grain 方式 | Film Preset |
| AV1 Film Preset | Classic35 / Fujifilm Eterna 250D |
| AV1 平均码率 | 1500 kbps |
| HEVC 平均码率 | 7500 kbps |
| 社交平台上传副本 | 关闭 |

### 容器行为

**MP4（默认）**：主输出音频转换为 AAC 256 kbps，启用 `faststart`，不写入不兼容的字幕、附件和数据流。

**MKV**：尽量复制并保留原始音频、字幕、附件、数据流、章节与 metadata，更适合完整归档。

可选的 H.264 社交平台上传版使用独立的 AAC 320 kbps 设置。

---

## HEVC：真实扫描 Film Grain

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

- 递归扫描 `D:\Film_Grain` 下的 `.mov` Grain Plate；
- 支持真实 35mm、Super 35、16mm、Super 16、8mm 及自定义素材；
- 支持四档 Grain 强度；
- 使用 `scale_vulkan` 与 `blend_vulkan` 完成 GPU 缩放和 Overlay；
- 支持 LUT、Cinematic Style、自动电影帧率、MP4/MKV 与多文件处理。

### Cinematic Style

HEVC 路线会保持原始输出分辨率，并添加约 **2.39:1** 上下黑边。

例如 1920×1080 输入仍输出 1920×1080。黑边在 Grain 合成后添加，因此黑色区域不会叠加颗粒。

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

生成工具位于：

```text
Utils\FilmGrain_MOV_to_1080p_HEVC_Lossless_Cache.bat
Utils\FilmGrain_MOV_to_HEVC_Lossless_Cache.bat
```

两个工具都会生成 HEVC Main10 Lossless Cache，并对源/预处理后的 P010 像素流与缓存解码结果执行 SHA-256 校验。

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

## AV1：grav1synth Film Grain

```text
原始视频
    ↓
AV1 Main10 NVENC
    ↓
IVF 临时视频流
    ↓
grav1synth 写入 Film Grain metadata
    ↓
恢复原片音频及容器内容
    ↓
grav1synth inspect 验证
    ↓
MP4 或 MKV
```

这里的 Grain 不会在编码阶段直接写进每一个像素。播放器解码 AV1 时，根据码流中的 Film Grain 参数实时生成颗粒。

### AV1 UHQ

当硬件探测确认当前 GPU、驱动与 FFmpeg 支持时，GUI 和 CLI 会增加 **UHQ** 速度/质量模式：

```text
preset：p4
tune：uhq
multipass：fullres
```

UHQ 模式不再额外强制 B-frame、Temporal AQ 和 Lookahead，由 UHQ 自身进行时域分析与帧结构决策。不支持 UHQ 的环境不会显示该选项，FAST 与 Standard 保持原有 HQ 逻辑。

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

高级模式提供 ISO 400、800、1600、3200、6400 和 Custom ISO，并可选择 `Luma only` 或 `Luma + chroma`。

### AV1 的 2.39:1 画幅

AV1 路线采用 **Active Picture Crop**，而不是将黑边编码进视频：

```text
1920×1080 → 约 1920×804
2560×1440 → 约 2560×1072
```

这样可以避免 Film Grain 模型影响黑边，同时减少对黑色区域的无效编码。全屏播放时由播放器或显示设备补充黑边。

### 最终验证

AV1 任务完成前会运行 `grav1synth inspect`。只有最终输出中的 Film Grain 信息通过检查，任务才会计为成功。

相关项目：

- [rust-av / grav1synth](https://github.com/rust-av/grav1synth)
- [本项目使用的 Windows 修订版](https://github.com/rampageX/grav1synth)

---

## 自动电影帧率

自动模式会先将 FFprobe 返回的平均帧率分数换算为数值，再识别 VFR 和数学上等价的非标准分数，例如 `60/2` 或 `19001/317`。

| 源帧率族 | 输出 |
|---|---|
| 23.976 / 29.97 / 47.952 / 59.94 / 119.88 附近 | 23.976 CFR |
| 24 / 25 / 30 / 48 / 50 / 60 / 100 / 120 附近 | 24.000 CFR |
| 无法可靠归类的特殊帧率 | 保持源帧率 |

转换使用 CFR 输出并保持正常视频时长。特殊 VFR 素材仍建议检查音画同步。

---

## LUT Gallery 与 Film Look

默认 LUT 根目录：

```text
E:\Adobe Portable\LUTs
```

运行以下工具可递归生成 LUT 缩略图与 Gallery 索引：

```text
Utils\LUT_Preview_Batch_Gallery.bat
```

缩略图生成器支持默认或自定义参考素材、Junction/Symlink、防循环、1920 宽预览及 Resolve CUBE 兼容处理。Gallery 中的“更换参考图”可在选择新图片后覆盖生成所有 LUT 预览，完成后自动刷新当前图库。

中文 LUT Gallery 支持：

- 全部 LUT、最近使用和我的最爱；
- 搜索、文件夹筛选与缩略图显示；
- 页码下拉菜单显示当前页，并可直接选择任意页面；
- 上一页/下一页及 PageUp/PageDown 首尾循环；
- 右键菜单、收藏、双击选择、Enter 确认与 Esc 取消。

选定 LUT 后，编码链使用 tetrahedral 插值，并支持 25%、50%、75% 和 100% 强度。

### 最近使用与我的最爱

- LUT Gallery 是“最近使用”和“我的最爱”数据库的唯一写入者；
- 在 Gallery 中双击或确认 LUT 时，会立即去重并加入“最近使用”；
- “最近使用”最多保留 25 条；
- Studio 主界面的两个下拉列表平时只读，并复用 Gallery 的 240×135 缩略图预览；
- 从 Studio 的“我的最爱”选择 LUT 后，仅在点击“开始编码”时调用 Gallery 的无界面登记入口，将该 LUT 加入“最近使用”；
- Recent 写入采用单写者、数量校验与同目录原子替换，避免两个界面互相覆盖或清空历史。

---

## 社交平台上传母版

视频平台通常会重新编码上传文件，原始 AV1 Film Grain metadata 很可能无法继续保留。项目提供两种生成上传母版的方式。

### 在主流程中生成

AV1 模式可以启用 H.264 社交网站上传版。主任务完成后会额外生成一份已将 Grain 烘焙到像素的 H.264/AAC MP4。

### 独立转换工具

将一个或多个已带 AV1 Film Grain 的文件拖到：

```text
Utils\AV1_FilmGrain_Bake_for_Social_Upload.bat
```

```text
AV1 + Film Grain metadata
    ↓ libdav1d 解码并合成颗粒像素
H.264 NVENC
    ↓
AAC 320k / MP4 / faststart
```

输出文件名带 `_UPLOAD_H264_GRAIN.mp4`，适合作为视频平台上传母版。

H.264 视频码率根据有效画面分辨率自动选择，主流程内的附加上传版与独立转换工具使用相同规则：

| 有效分辨率 | 平均码率 | Maxrate | Bufsize |
|---|---:|---:|---:|
| ≤ 720p | 6 Mbps | 9 Mbps | 12 Mbps |
| ≤ 1080p | 8 Mbps | 12 Mbps | 16 Mbps |
| ≤ 1440p | 10 Mbps | 15 Mbps | 20 Mbps |
| > 1440p（含 4K） | 12 Mbps | 18 Mbps | 24 Mbps |

---

## 为现有 AV1 免重编码添加或替换 Film Grain

将一个或多个已有 AV1 视频拖到：

```text
Utils\AV1_Grav1synth_Add_Replace_FilmGrain_NoReencode.bat
```

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
- 默认输出 MKV 并尽量保留原始流；
- MP4 模式将音频转换为 AAC 320 kbps，并省略字幕、附件和数据流；
- 失败时默认保留临时目录和日志；
- 非 AV1 视频会被跳过。

> “No Re-encode”仅指视频流。选择 MP4 时，音频仍会转换为 AAC。

---

## 其他 Utils 工具

| 文件 | 用途 |
|---|---|
| `Collect_BT709_LUTs_Conservative.bat` | 保守筛选明确标注 BT.709/Rec.709 输入的 CUBE LUT，复制到 LUT 根目录的 `BT.709` 子目录并生成 CSV 报告 |
| `FilmGrain_MOV_to_HEVC_Lossless_Cache.bat` | 递归扫描 Grain 库，生成原分辨率 HEVC Main10 Lossless Cache 并验证像素哈希 |
| `FilmGrain_MOV_to_1080p_HEVC_Lossless_Cache.bat` | 将 Grain Plate 缩放至 1920×1080，生成 HEVC Main10 Lossless Cache 并验证像素哈希 |
| `LUT_Preview_Batch_Gallery.bat` | 生成 LUT Gallery 缩略图和索引 |

---

## FFmpeg 与 NVIDIA 驱动

当前稳定测试环境使用 NVIDIA Driver 596.49，以及采用 NVENC API 13.0 headers 构建的 FFmpeg：

```text
BtbN FFmpeg Auto-Build
Date    : 2026-04-30 13:44
Version : N-124278-gcc3ca17127
Target  : Windows x86_64
Variant : GPL static
```

- [对应的 BtbN Release](https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-04-30-13-44)
- [BtbN / FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)

最低驱动要求取决于 FFmpeg 构建采用的 NVENC API / `nv-codec-headers`，不能只根据 FFmpeg 主版本号判断。

v3.1 起会在启动时自动校验当前环境。更换 GPU、升级 NVIDIA 驱动或替换 FFmpeg 后，原能力缓存会自动失效并重新检测。

升级驱动或 FFmpeg 后，建议至少确认：

```bat
ffmpeg -hide_banner -encoders | findstr /i "hevc_nvenc av1_nvenc"
ffmpeg -hide_banner -filters  | findstr /i "blend_vulkan scale_vulkan"
ffmpeg -hide_banner -hwaccels | findstr /i "cuda vulkan"
ffmpeg -hide_banner -h decoder=libdav1d
```

并重新测试：

- HEVC/AV1 Main10 输出；
- Vulkan Grain 合成的亮度、格式与帧同步；
- 23.976/24 fps 转换后的时长与音画同步；
- MP4/MKV 的音频、字幕、附件与章节行为；
- AV1 最终文件能否通过 `grav1synth inspect`；
- 新环境的实际编码速度。

不要将外部 FFmpeg 目录中的 DLL 覆盖到 grav1synth 目录，两者的运行时依赖应保持独立。

---

## 已知限制

- 仅面向 Windows BAT、Windows PowerShell 5.1 与 WinForms 工作流；
- 需要 NVIDIA GPU；AV1、UHQ、B-frame、AQ、NVDEC 和 Vulkan 的可用性由当前 GPU、驱动与 FFmpeg 组合决定；
- HEVC 扫描 Grain 会增加编码压力和所需码率；
- AV1 Film Grain 的显示依赖播放器和解码器正确实现 Film Grain Synthesis；
- 部分平台和转码软件会移除 AV1 Film Grain metadata；
- MP4 兼容模式不会保留字幕、附件和数据流；
- 启用 LUT 时，部分处理链会转为软件滤镜路径，速度可能下降；
- 特殊 HDR、VFR、多视频流或非常规容器建议先使用短片测试；
- 强制取消任务可能留下未完成输出或 `__AV1GS_TMP_*` 临时目录；
- 重要素材应保留原文件，并在归档前检查画面、音频、时长、流信息及 Film Grain 验证结果。

---

## 如何选择

选择 **HEVC + 真实扫描 Grain**，如果你更重视真实 Grain Plate 的具体质感、不依赖播放器生成颗粒，以及更广泛的播放兼容性。

选择 **AV1 + grav1synth**，如果你更重视较低码率、快速批量处理，以及低码率下仍能保留明显颗粒。

在硬件检测通过的设备上，如果更重视 AV1 编码质量而不是最高速度，可选择 **UHQ**。

当前默认推荐：

> **AV1 Main10 + grav1synth Film Grain，输出 MP4 / AAC 256k。**

需要向社交或视频平台上传时，再生成一份将 Grain 烘焙到像素的 H.264/AAC MP4 上传母版。
