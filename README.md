# Film Grain Studio
![](images/Film_Grain_Studio.jpg)

Film Grain Studio（FGS）是一个面向 Windows / NVIDIA GPU 的视频电影化工具包，核心基于 **FFmpeg、NVIDIA NVENC、libx264、Vulkan、grav1synth 与 OpenSVPFlow**。GUI 与 CLI 共用同一套 `StudioBridge` 编码核心，重点提供真实扫描胶片颗粒、AV1 Film Grain、LUT、Cinematic 画幅、反交错、插帧、字幕和批量编码工作流。

当前正式稳定版：**v4.6.2**

正式发布包：

```text
FilmGrain_Studio_v4.6.2_Stable.zip
```

升级建议：完整替换旧版本目录，不要把多个版本的 `Utils` / `_LUT_Tools` / `_OpenSVPFlow` 混用。

---

## v4.6.2 重点变化

### HDR Preserve

v4.6.2 正式加入 **HDR Preserve**，当前覆盖：

- **HEVC Main10 + 扫描 Grain**
- **AV1 Main10 + grav1synth Film Grain**

FGS 会检测源视频 HDR 色彩信号，并在主编码链中保持：

- 10-bit
- BT.2020 Color Primaries
- PQ / SMPTE ST 2084，或 HLG / ARIB STD-B67
- BT.2020 non-constant matrix
- Limited / Full range（按源信号）
- 源文件本来存在的 HDR10 静态元数据，包括 Mastering Display、MaxCLL、MaxFALL

HEVC 路线显式强制 `p010le`，避免只显示 Main10 Profile 但实际码流仍为 8-bit。最终输出还会使用 FFprobe 校验 HDR Primaries / Transfer / Matrix / Range；验证失败时不会把错误输出当作成功成片。

原则：源有什么 HDR 信息就尽量保留什么；源没有的 Mastering Display / MaxCLL / MaxFALL 不会凭空伪造。

YouTube 等来源的 HDR 视频可能只有 BT.2020 / PQ / 10-bit，而没有 Mastering Display 或 MaxCLL / MaxFALL，这属于正常输入。

### HDR 当前兼容边界

为了避免 HDR 误走进 8-bit / SDR 处理链，当前 HDR Preserve 对以下功能采用安全限制：

- **OpenSVPFlow 60 fps**：当前 FGS 集成使用 YUV420P8，因此 HDR 输入自动旁路插帧并保持源帧率处理。
- **H.264 x264 主输出**：当前不作为 HDR Preserve 主线；HDR 主输出请使用 HEVC Main10 或 AV1 Main10。
- **H.264 上传版**：当前 8-bit 上传链没有 HDR→SDR Tone Mapping，因此 HDR 输入自动跳过。
- **现有 LUT 工作流**：当前 LUT 体系主要建立在 SDR / BT.709 与 Log→709 用法上，HDR 输入由编码核心安全旁路。后续如确认 HDR→HDR LUT 的输入/输出色彩空间，再单独扩展。

本版没有加入 Dolby Vision RPU、HDR10+ 动态元数据、HDR→SDR Tone Mapping 或 HDR OpenSVPFlow 10-bit 插帧。

HEVC HDR10/PQ 与 AV1 HDR 均已完成用户侧实际测试；BT.2020 / PQ / 10-bit、画面色彩和源 HDR10 静态元数据保持正常。

GUI 在选中单个 HDR 文件并完成媒体探测后，会把当前不兼容的 OpenSVPFlow、H.264 上传版和 LUT 区域灰显；切回 SDR 文件后恢复。混合批量仍由编码核心按文件分别判断，不会因为当前选中项而改写其它文件的安全路由。

### LUT Gallery 智能过滤

LUT Gallery 新增 **智能过滤** 按钮，用于大型 LUT 库的功能型 LUT 初筛。

它只影响图库显示：不移动、不删除、不重命名 LUT，再次点击即可恢复全部。

分类结果：

```text
Technical
Combined
Creative
Keep
```

只有高置信度 **Technical** 会被隐藏。

判断会读取 `.cube` 文件头及文件/目录上下文，识别 Input / Output Color Space、Utility / Technical / Transform / Conversion、CST / IDT / ODT、Log→Rec.709、Tone Map / Gamut Map、Full / Legal Range、Shaper 等技术证据。

v4.6.2 同时加入跨文件家族识别。像同一个创意 Look 同时存在 S-Log3 / V-Log / LogC / Rec.709 等多个输入适配版本时，会优先判断为 **Combined** 并保留，避免把创意 LUT 因为内部包含技术转换而误删。

扫描报告：

```text
<LUT_ROOT>\_LUT_PREVIEWS\_LUT_SMART_FILTER_REPORT.csv
```

图库顶部 UI 也做了小调整：`无 / 禁用 LUT` 缩短为 `禁用 LUT`，状态区域加宽，使“匹配 / 共计 / 隐藏功能型数量”更容易完整显示。

---

## 三条主编码路线

| 路线 | Film Grain 方式 | 默认位深 | 主要用途 |
|---|---|---:|---|
| AV1 Main10 + grav1synth | Film Grain metadata / Grain Table | 10-bit | 高压缩效率、AV1 Film Grain |
| HEVC Main10 + 扫描 Grain | Grain Plate 实际合成进像素 | 10-bit | 收藏、真实扫描颗粒、GPU 高速编码 |
| H.264 x264 Grain | Grain Plate 实际合成进像素 | 8-bit High，High10 可选 | 平台兼容、上传、CPU x264 Grain |

默认输出：AV1 Main10 / MP4 / AAC 256k。

GUI / CLI 共用 `Utils\FilmGrain_Universal_HEVC_AV1_StudioBridge.bat`。

---

## AV1 Film Grain 与现成 Grain Table

AV1 支持 Film Preset、Photon ISO、现成 `.tbl / .txt`、grav1synth 添加/替换 Film Grain、最终 Header 验证，以及硬件和 grav1synth 条件满足时的 NVENC Split Frame Encoding / SFE。

`_AV1_Grain_Tables` 按 720p / 1080p / 1440p / 2160p 分类。

v4.6.2 正式包保留当前整理的一组公开可获取 Grain Table，主要来源：

- Boulder08 / chunknorris — https://github.com/Boulder08/chunknorris
- nekotrix / AV1-Photon-Noise-Tables — https://github.com/nekotrix/AV1-Photon-Noise-Tables

`Photon-Noise-*` 主要来自 Photon Noise Tables；影视命名表及部分分辨率预设来自公开 Grain Table 集合。第三方表文件仍受其各自上游项目条款约束；如需二次分发，请同时查看原项目说明。

---

## OpenSVPFlow

首次安装：`_OpenSVPFlow\00_Setup.bat`

主动更新：`_OpenSVPFlow\01_Update_OpenSVPFlow.bat`

更新器支持下载、校验、CPU / GPU smoke test、安装前备份、安装后验证和失败回滚。正式包不包含用户本机安装后的 DLL、版本状态文件或 `_PluginBackup`。

---

## AV1 SFE / 多引擎并行

AV1 Standard / UHQ 在硬件允许时支持 NVENC Split Frame Encoding，要求 `grav1synth >= 0.2.2`。

下载：https://github.com/rampageX/grav1synth/releases/latest

FGS 不随包分发 grav1synth。

---

## 反交错与帧率

自动反交错支持 BWDIF Vulkan、BWDIF CUDA、W3FDIF Complex。典型 Field-rate：29.97i → 59.94p、25i → 50p。

逐行素材可使用自动电影帧率、保持源帧率，或在 SDR 路线中使用 OpenSVPFlow 60 fps。隔行素材检测到后按文件自动旁路 OpenSVPFlow。

---

## x264 Grain

默认 `libx264 + preset faster + tune grain + VBR single-pass + High 8-bit`。高级设置可选 Medium / Slow / VBR 2-Pass / High10（实验）。

自动码率综合编码器、输出分辨率、最终 FPS 与高动态状态。VBV 统一为 `maxrate = 平均码率 x 3`、`bufsize = 平均码率 x 6`。H.264 上传版与主线共用 x264 Preset / Pass 选择，AAC 统一为 256k。

---

## MPEG-TS / M2TS 时间同步

对 `.ts / .mts / .m2ts + OpenSVPFlow` 使用针对性时间戳保持/重基准逻辑，避免 VSPipe 重建视频时间轴后造成音视频逐渐错位。其它普通文件不走该特殊路径。

---

## LUT Gallery

支持全部 LUT、最近使用、我的最爱、文件夹过滤、搜索、分页、页码下拉、25/50/75/100% 强度、更换参考图和智能过滤。

`_LUT_Tools\LUT_Reference_Current.jpg` 属于用户运行状态，正式发布包默认不包含。

---

## 配置与硬件探测

统一配置文件为 `FilmGrain_Config.ini`，可在 GUI 右上角“配置…”修改。

硬件能力由 `Utils\FilmGrain_Hardware_Caps.ps1` 自动探测，运行时生成 `Utils\_HardwareCaps.json`。该缓存与具体电脑、驱动、FFmpeg 绑定，因此正式包不包含用户生成的 `_HardwareCaps.json`。

---

## 正式包清理

v4.6.2 正式包不包含 `_HardwareCaps.json`、`LUT_Reference_Current.jpg`、LUT Recent/Favorites 状态、Smart Filter CSV、OpenSVPFlow 用户 DLL/version state、`_PluginBackup`、临时 Patch/HOTFIX/TEST 文件和测试构建标记。

历史版本变更请参阅 `CHANGELOG.md`；正式稳定基线见 `STABLE_BASELINE.txt`。
