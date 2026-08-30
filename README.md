# 用 FFmpeg + NVIDIA GPU 实现高效电影胶片颗粒：HEVC 真扫描 Grain 与 AV1 Film Grain Synthesis

在数码视频中加入 Film Grain（胶片颗粒）并不困难，真正困难的是同时做到：

- 颗粒自然，不是简单的随机 Noise；
- GPU 加速，批量处理速度足够快；
- 低码率下颗粒不会被编码器抹掉；
- 尽量保留原音轨、字幕、章节和附件；
- 可以方便地在 Windows 下拖放文件批量处理。

经过多轮测试和优化，最终形成了两套不同思路的方案：

1. **HEVC + 真实扫描 Film Grain Plate**
2. **AV1 NVENC + Film Grain Synthesis**

两者并不是谁取代谁，而是各有优势。

---

## 一、为什么普通“加噪点”不是理想的 Film Grain

真正的胶片颗粒具有明显的空间结构、亮度相关性以及随机变化特征，并不是简单地给画面叠加均匀白噪声。

比较理想的方法是使用真实胶片扫描得到的 **Grain Plate**，以 Overlay 等方式与数字影像合成。

但它存在一个编码上的天然问题：

> Film Grain 本质上是高度随机、时间相关性很低的信息，而现代视频编码器最擅长利用空间和时间上的重复性来压缩。

因此，一旦把真实颗粒直接烧进像素：

```text
原始视频
   +
扫描胶片颗粒
   ↓
带随机颗粒的视频
   ↓
HEVC / AV1 编码
```

编码器需要消耗大量码率来保存这些随机细节。

AOMedia 对 Film Grain 的描述也明确指出，胶片颗粒由于高度随机而难以压缩；AV1 因此专门设计了 Film Grain Synthesis，让编码器只保存干净画面以及少量颗粒模型参数，再由解码器重新生成颗粒。

参考：[AOMedia AV1 Tool Description](https://aomedia.org/docs/AV1_ToolDescription_v11-clean.pdf)

这正好对应本文的两条路线。

---

## 二、两种方案的核心区别

| 项目 | HEVC + 扫描 Grain | AV1 + Film Grain Synthesis |
|---|---|---|
| Grain 来源 | 真实胶片扫描 MOV | AV1 Grain 模型 |
| Grain 是否写进像素 | **是** | **否** |
| 编码器是否需要压缩颗粒 | 是 | 基本不需要 |
| 颗粒真实性 | ★★★★★ | ★★★★☆ |
| 低码率效率 | ★★★ | ★★★★★ |
| 编码速度 | 较快 | **非常快** |
| 播放兼容性 | **非常好** | 依赖 AV1/FGS 解码支持 |
| 适合用途 | 收藏、兼容性、真实 Grain | 高压缩率、高效率、批处理 |

简而言之：

> **HEVC 方案追求“真实扫描颗粒”。**  
> **AV1 方案追求“用极低码率重新合成非常像胶片的颗粒”。**

---

## 三、Film Grain 素材下载

本文 HEVC 方案使用的是公开提供的真实 Film Grain Plate。

### 1. TDCAT 4K DCI Film Grain

TDCAT 提供免费的 **4096×2160 ProRes 422 HQ** Grain Plate，包括：

- 35mm
- Super 35
- 16mm
- Super 16
- 8mm

每种又分为：

- Light
- Heavy

官方也推荐使用 **Overlay Blend Mode** 合成，并明确提醒 Film Grain 很难压缩。

下载：

[TDCAT Free DCI 4K Film Grain Plates](https://tdcat.squarespace.com/downloads/filmgrain)

如果不想一个个下载，可以直接在该页面选择 **All Five Light / All Five Heavy**。

---

### 2. Cinema Tools 35mm Film Grain

另一套很不错的免费素材：

```text
CT 35mm Grain 4K DCI.mov
4096 × 2160
ProRes 422 HQ
约 889 MB
```

Cinema Tools 同样建议把 Grain 放在原视频上方，以 **Overlay** 模式混合，并通过透明度控制强度。

下载：

[Cinema Tools 4K DCI 35mm Film Grain](https://www.cinematools.co/film-grain)

实际使用时可以统一整理为：

```text
D:\Film_Grain\
 ├─ CinemaTools\
 ├─ TDCAT-Light\
 └─ TDCAT-Heavy\
```

HEVC 脚本会递归搜索子目录。

---

## 四、方案一：HEVC + 真实扫描 Film Grain

最终脚本：

`FilmGrain_HEVC_NVENC_v20_FINAL_AutoCinemaFPS_FIX2.bat`

它的处理流程为：

```text
原视频
   ↓ NVDEC
主画面
                    Grain Plate
                         ↓
                HEVC Lossless Cache
                         ↓
主画面 ────────── Vulkan Overlay
                         ↓
                 P010 / Main10
                         ↓
                    HEVC NVENC
```

核心特点是：

**Grain 是真实扫描得到的颗粒，而且最终真正写入视频像素。**

---

## 五、HEVC 方案主要功能

### GPU 加速

整个主要处理链使用：

```text
NVDEC
Vulkan
NVENC
```

Grain 的缩放和 Overlay 合成都尽量交由 GPU 完成。

### Grain 强度

脚本提供：

```text
65%   Light
75%   Natural
85%   Strong   ← 推荐
100%  Full
```

实际测试中 **85%** 是一个比较自然且明显的强度。

### 编码速度模式

提供：

```text
Standard
p6 / fullres multipass / lookahead 32

FAST
p5 / qres multipass / lookahead 16
```

FAST 为默认模式。

### 自定义码率

内置：

```text
6000
7500
9000
12000 kbps
```

也可以直接输入：

```text
1500
2500
5000
...
```

脚本自动设置：

```text
MaxRate = 2 × Bitrate
BufSize = 4 × Bitrate
```

### 自动电影帧率

提供：

```text
[1] 保持源帧率
[2] 自动电影帧率
```

自动模式大致遵循：

```text
59.94 / 29.97 / 119.88
          ↓
       23.976

60 / 30 / 50 / 25 / 120
          ↓
        24.000
```

遇到无法可靠判断的特殊帧率则保留源帧率。

### 2.39:1 电影黑边

可自动将 16:9 画面处理为约 **2.39:1**。

例如：

```text
1920 × 1080
```

会在上下覆盖约：

```text
138 px
```

最终仍然保持：

```text
1920 × 1080
```

并且黑边是在 Grain 合成后添加，因此黑边本身保持纯黑。

---

## 六、为什么要制作 Grain Cache

原始 Grain 素材通常是：

```text
4096×2160
ProRes 422 HQ
```

如果每次处理视频都重新：

```text
ProRes Decode
→ P010
→ Scale
→ Vulkan
```

会产生不必要的重复计算。

因此另外提供两个转换脚本。

### 1080p Cache

`FilmGrain_MOV_to_1080p_HEVC_Lossless_Cache.bat`

把原始 4K Grain 一次性转换为：

```text
1920×1080
P010
HEVC Main10 Lossless
```

输出：

```text
xxx_1080p_HEVC_Lossless.mkv
```

脚本还会对转换前后的 **P010 像素流进行 SHA-256 校验**，确保 Cache 解码后的像素与预处理后的 Grain 完全一致。

这是 1080p 视频最推荐使用的缓存。

### 4K Cache

`FilmGrain_MOV_to_HEVC_Lossless_Cache.bat`

生成：

```text
xxx_HEVC_Lossless.mkv
```

用于 1440p、1600p、4K 等高于 1080p 的视频。

最终 HEVC 脚本会自动：

```text
≤1920×1080
→ 优先使用 1080p Cache

>1920×1080
→ 优先使用 4K Cache
```

不存在 Cache 时仍可直接使用原始 MOV。

---

## 七、方案二：AV1 NVENC + Film Grain Synthesis

最终脚本：

`AV1_NVENC_grav1synth_FilmGrain_FINAL.bat`

它走的是完全不同的路线：

```text
原始视频
    ↓
NVDEC
    ↓
AV1 Main10 NVENC
    ↓
干净的低码率 AV1
    ↓
grav1synth
    ↓
写入 AV1 Film Grain 参数
    ↓
恢复原音轨 / 字幕 / 附件
    ↓
grav1synth inspect 验证
```

这里最重要的一点是：

> **颗粒没有真正被编码进每一个视频像素。**

解码器首先解码相对干净的图像，然后根据码流中的 Grain 模型重新生成 Film Grain。

这正是 AV1 Film Grain Synthesis 的设计目的：用很少的模型参数替代大量随机噪声数据，从而提高压缩效率。

参考：[AOMedia AV1 Tool Description](https://aomedia.org/docs/AV1_ToolDescription_v11-clean.pdf)

---

## 八、grav1synth

AV1 方案使用开源项目 **grav1synth**。

它可以：

```text
inspect
apply
remove
diff
```

例如：

- `inspect`：读取 AV1 文件中的 Grain 参数；
- `apply`：将 Grain Table 或 ISO 模型应用到已有 AV1 视频；
- `remove`：移除 Film Grain；
- `diff`：比较原始与去噪视频来估算 Grain Table。

项目采用 MIT License。

项目地址：

[rust-av / grav1synth GitHub](https://github.com/rust-av/grav1synth)

Windows 官方目前没有稳定的预编译 EXE，因此本文附件提供：

`build-grav1synth-windows-v2-FIX.yml`

可以直接使用 **GitHub Actions / Windows 2022 Runner** 编译 Windows x64 版本。

本文实际使用：

```text
grav1synth 0.2.0
```

---

## 九、AV1 脚本中的 Grain 模式

脚本提供两种 Grain 来源。

### Film Preset

推荐日常使用。

包含：

```text
Classic35
Modern35
16mm
Super8
MaxMid
```

默认：

```text
Classic35
```

并提供不同 Film Stock 选择。

这是目前最接近“直接选择一种胶片质感然后使用”的模式。

### Photon ISO

高级模式，可以直接设置：

```text
ISO 200
ISO 400
ISO 800
ISO 1600
Custom ISO
```

还可以选择：

```text
Luma only
Luma + Chroma
```

grav1synth 也支持基于 `--iso` 的 photon-noise Grain，并可以通过 `--chroma` 将颗粒扩展到色度通道。

参考：

[grav1synth README](https://github.com/rust-av/grav1synth/blob/main/README.md)

---

## 十、为什么 AV1 版本使用裁切而不是“烧黑边”

HEVC 版可以这样做：

```text
1920×1080
↓
上下覆盖黑边
↓
仍为1920×1080
```

因为 HEVC 版的 Grain 已经在黑边之前完成合成。

AV1 不同。

AV1 Film Grain 是**解码后才合成的**。

如果把上下黑边本身编码进 AV1：

```text
1920×1080
黑边
画面
黑边
```

Film Grain 模型理论上仍可能作用于这些黑色像素。

所以 AV1 版本采用真正的：

```text
1920×1080
    ↓
1920×804
```

即 **2.39:1 Active Picture Crop**。

播放器在 16:9 屏幕全屏播放时自动补黑边。

这样有三个好处：

1. 黑边绝对纯黑；
2. 不浪费码率编码黑色区域；
3. Grain 只作用于真正的电影画面。

---

## 十一、AV1 方案为什么这么快

AV1 版本完全绕开了最费时的步骤：

```text
扫描 Grain Decode
→ Grain Scale
→ Vulkan Overlay
→ 编码随机噪声
```

实际只需要：

```text
AV1 NVENC
↓
grav1synth 修改 AV1 Grain 参数
↓
Stream Copy
```

grav1synth 本身并不会重新编码整个视频。

而 NVIDIA NVDEC / FFmpeg 已支持 AV1 8/10-bit 以及 Film Grain 相关解码路径。

参考：

[NVIDIA Video Codec SDK - FFmpeg with NVIDIA GPU](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.1/ffmpeg-with-nvidia-gpu/index.html)

---

## 十二、一次实际性能测试

测试素材：

```text
时长：12:07
输入：59.94 fps
输出：23.976 fps
GPU：RTX 4080
```

### HEVC + 真实 Grain

```text
frame=17435
fps=101
speed=4.22x
elapsed=2:52
```

### AV1 + grav1synth

AV1 主编码：

```text
frame=17435
fps=267
speed=11.1x
elapsed=1:05
```

最终 remux：

```text
约 0.18 秒
```

并通过：

```text
grav1synth inspect
```

验证：

```text
VERIFIED:
AV1 Film Grain headers are present
```

在这次测试中：

```text
纯 AV1 Video：
231514 KiB

加入 Film Grain 后：
234084 KiB
```

Film Grain 信息只增加约：

```text
2570 KiB
≈ 1.1%
```

这是 Film Grain Synthesis 最大的优势之一。

当然，这只是单个测试素材的结果，并不能作为不同 GPU、码率和片源之间的绝对性能基准。

---

## 十三、该选择哪一种？

### 选择 HEVC + 扫描 Grain，如果你：

- 最重视播放兼容性；
- 想使用真正扫描出来的 35mm / 16mm Grain；
- 希望无论什么播放器都能看到完全相同的颗粒；
- 不介意 Grain 占用更多码率。

### 选择 AV1 + grav1synth，如果你：

- 使用支持 AV1 的现代设备；
- 希望显著降低码率；
- 需要高速批量处理；
- 希望低码率下 Grain 仍然明显；
- 可以接受 Film Grain 是模型合成而非逐像素真实扫描。

对于大量视频转码，我更推荐：

> **AV1 + Film Grain Synthesis**

对于重要收藏、需要最高播放兼容性，或者特别喜欢某套真实 Grain Plate 的质感：

> **HEVC + Vulkan Overlay**

两者可以长期并存。

---

## 十四、使用前需要修改的路径

所有脚本顶部都保留了可编辑设置。

例如：

```bat
set "FFMPEG=E:\EnCoder\FFMpeg\13.0\bin\ffmpeg.exe"
set "FFPROBE=E:\EnCoder\FFMpeg\13.0\bin\ffprobe.exe"
```

HEVC 版本另外设置：

```bat
set "DEFAULT_GRAIN_ROOT=D:\Film_Grain"
```

AV1 版本另外设置：

```bat
set "GRAV1SYNTH=E:\EnCoder\FFMpeg\grav1synth\grav1synth.exe"
```

修改后直接：

> **把一个或多个视频文件拖到 BAT 上即可。**

---

## 十五、FFmpeg 与驱动注意事项

HEVC 方案需要 FFmpeg 包含：

```text
hevc_nvenc
blend_vulkan
scale_vulkan
```

AV1 方案需要：

```text
av1_nvenc
```

建议升级 FFmpeg 后先检查：

```bat
ffmpeg -encoders | findstr /i "hevc_nvenc av1_nvenc"
ffmpeg -filters  | findstr /i "blend_vulkan scale_vulkan"
```

对于较新的 FFmpeg / NVENC API，可能需要较新的 NVIDIA 驱动。

只要上述 Encoder / Filter 仍存在，脚本的核心逻辑通常无需修改。

---

## 十六、已知限制

AV1 Film Grain 的最终显示效果仍依赖播放端正确实现 Film Grain Synthesis。

另外，grav1synth 目前仍处于快速开发阶段，Issue Tracker 中仍存在 HDR10、部分特殊文件以及 metadata 等兼容性问题，因此重要素材建议保留原文件，并在正式归档前检查最终输出。

参考：

[grav1synth Issues](https://github.com/rust-av/grav1synth/issues)

本文脚本采用：

```text
grav1synth只处理视频流
+
FFmpeg重新从原片恢复
音频 / 字幕 / 附件 / metadata / chapters
```

来尽量避免中间工具造成其它流信息损失。

---

## 附件

本文对应的工具包包含：

- `FilmGrain_HEVC_NVENC_v20_FINAL_AutoCinemaFPS_FIX2.bat`
- `AV1_NVENC_grav1synth_FilmGrain_FINAL.bat`
- `FilmGrain_MOV_to_1080p_HEVC_Lossless_Cache.bat`
- `FilmGrain_MOV_to_HEVC_Lossless_Cache.bat`
- `build-grav1synth-windows-v2-FIX.yml`

推荐将附件和本文 Markdown 文件放在同一发布页面或同一个 GitHub Release 中。
