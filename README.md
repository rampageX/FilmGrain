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

>
> Film Grain 本质上是高度随机、时间相关性很低的信息，而现代视频编码器最擅长利用空间和时间上的重复性来压缩。
>

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


| 项目                            | HEVC + 扫描 Grain               | AV1 + Film Grain Synthesis           |
|-----------------------------------|-----------------------------------|--------------------------------------|
| Grain 来源                      | 真实胶片扫描 MOV            | AV1 Grain 模型                     |
| Grain 是否写进像素          | **是**                           | **否**                              |
| 编码器是否需要压缩颗粒 | 是                               | 基本不需要                      |
| 颗粒真实性                   | ★★★★★                   | ★★★★☆                      |
| 低码率效率                   | ★★★                         | ★★★★★                      |
| 编码速度                      | 较快                            | **非常快**                        |
| 播放兼容性                   | **非常好**                     | 依赖 AV1/FGS 解码支持          |
| 适合用途                      | 收藏、兼容性、真实 Grain | 高压缩率、高效率、批处理 |


简而言之：

> 
> **HEVC 方案追求“真实扫描颗粒”。**  

> **AV1 方案追求“用极低码率重新合成非常像胶片的颗粒”。**
> 

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

Cinema Tools 同样建议把 Grain 放在原视频上方，以 **Overlay**
模式混合，并通过透明度控制强度。

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

>
> **颗粒没有真正被编码进每一个视频像素。**
>

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

本文实际使用：[此仓库编译](https://github.com/rampageX/build_gs)

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

grav1synth 也支持基于 `--iso` 的 photon-noise Grain，并可以通过 `--chroma`
将颗粒扩展到色度通道。

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

> 
> **AV1 + Film Grain Synthesis**
> 

对于重要收藏、需要最高播放兼容性，或者特别喜欢某套真实 Grain Plate 的质感：

> 
> **HEVC + Vulkan Overlay**
> 

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

>
> **把一个或多个视频文件拖到 BAT 上即可。**
>

---

## 十五、本文为什么固定使用一个较旧的 FFmpeg 构建

本文测试环境中的 NVIDIA 驱动为：

```text
NVIDIA Driver 596.49
```

这版驱动能够正常使用 **NVENC API 13.0**，但无法满足当前部分新 FFmpeg 二进制构建所使用的 **NVENC API 13.1** 要求。

NVIDIA/FFmpeg 的 NVENC 支持并不是单纯由“FFmpeg 主版本号”决定的，而与编译该 FFmpeg 二进制时使用的 `nv-codec-headers` 版本直接相关。

例如：

```text
nv-codec-headers 13.0.19  
→ Video Codec SDK 13.0  
→ Windows 最低驱动 570.0  

nv-codec-headers 13.1.15  
→ Video Codec SDK 13.1  
→ Windows 最低驱动 610.0
```

官方说明：

- [nv-codec-headers 13.0.19 README](https://raw.githubusercontent.com/FFmpeg/nv-codec-headers/n13.0.19.0/README)
- [当前 nv-codec-headers README（13.1.15）](https://github.com/FFmpeg/nv-codec-headers/blob/master/README)

因此要特别注意：

>
> **不是“FFmpeg 9.x 本身必须使用 610+ 驱动”。**  

> 真正决定最低 NVIDIA 驱动版本的是该 FFmpeg 构建所链接/编译使用的 NVENC API / `nv-codec-headers` 版本。
>

### 本文实际使用的特殊 FFmpeg 版本

为了继续使用 596.49 驱动，同时获得：

```text
hevc_nvenc  
av1_nvenc  
blend_vulkan  
scale_vulkan  
CUDA / NVDEC  
P010 / Main10
```

本文固定使用 BtbN 在 2026-04-30 发布的 Windows x64 GPL 构建：

```text
BtbN FFmpeg Auto-Build  
Date    : 2026-04-30 13:44  
Version : N-124278-gcc3ca17127  
Target  : Windows x86_64  
Variant : GPL static
```

本文脚本中的路径示例：

```bat
set "FFMPEG=E:\EnCoder\FFMpeg\13.0\bin\ffmpeg.exe"  
set "FFPROBE=E:\EnCoder\FFMpeg\13.0\bin\ffprobe.exe"
```

这里目录名 `13.0` 是为了表示当前使用的是 **NVENC API 13.0 兼容构建**，并不是 FFmpeg 的正式版本号。

### 下载地址

BtbN 对应 Release 页面：

[BtbN FFmpeg Auto-Build 2026-04-30 13:44](https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-04-30-13-44)

本文推荐的 Windows x64 GPL Static：

[ffmpeg-N-124278-gcc3ca17127-win64-gpl.zip](https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-04-30-13-44/ffmpeg-N-124278-gcc3ca17127-win64-gpl.zip)

如果需要 Shared 版本，则为：

[ffmpeg-N-124278-gcc3ca17127-win64-gpl-shared.zip](https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-04-30-13-44/ffmpeg-N-124278-gcc3ca17127-win64-gpl-shared.zip)

对于本文 BAT 脚本，**Static GPL 版本最省事**，不需要额外维护 `avcodec / avfilter / avformat` 等 DLL。

BtbN 项目地址：

[BtbN / FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)

---

## 十六、升级到 610+ 驱动和新 FFmpeg 后需要验证什么

如果以后 NVIDIA 驱动升级到 **610 或更高版本**，就可以考虑切换到采用 NVENC API 13.1 的新 BtbN / Gyan FFmpeg 构建，或者 FFmpeg 9.x 的新版本。

两套脚本的核心设计原则通常不需要重写，但**不要直接覆盖旧环境后就批量处理重要素材**。建议保留当前已经验证稳定的 FFmpeg 目录，同时新建一个目录进行 A/B 测试。

例如：

```text
E:\EnCoder\FFMpeg\13.0\   ← 当前稳定版本  
E:\EnCoder\FFMpeg\9.x\    ← 新版本测试
```

然后只修改 BAT 顶部：

```bat
set "FFMPEG=E:\EnCoder\FFMpeg\9.x\bin\ffmpeg.exe"  
set "FFPROBE=E:\EnCoder\FFMpeg\9.x\bin\ffprobe.exe"
```

### 1. 首先确认 NVIDIA Encoder 和 Vulkan Filter 仍然存在

运行：

```bat
ffmpeg -hide_banner -encoders | findstr /i "hevc_nvenc av1_nvenc"  
ffmpeg -hide_banner -filters  | findstr /i "blend_vulkan scale_vulkan"  
ffmpeg -hide_banner -hwaccels | findstr /i "cuda vulkan"
```

HEVC 脚本至少需要：

```text
hevc_nvenc  
blend_vulkan  
scale_vulkan  
CUDA/NVDEC  
Vulkan
```

AV1 脚本至少需要：

```text
av1_nvenc  
CUDA/NVDEC
```

如果其中任何一个缺失，通常说明所下载的 FFmpeg 构建功能不完整，而不是 BAT 本身的问题。

### 2. 再检查 NVENC 参数名称有没有变化

建议分别运行：

```bat
ffmpeg -hide_banner -h encoder=hevc_nvenc  
ffmpeg -hide_banner -h encoder=av1_nvenc
```

重点确认本文使用的参数仍然存在：

```text
preset p5 / p6  
tune hq  
rc vbr  
multipass qres / fullres  
rc-lookahead  
spatial-aq  
temporal-aq  
aq-strength  
bf  
b_ref_mode
```

AV1 另外确认：

```text
highbitdepth  
p010le / 10-bit
```

如果新版 FFmpeg 对某个参数改名、废弃或者改变默认行为，应先调整脚本，再进行性能对比。

### 3. HEVC + Vulkan 真扫描 Grain 是升级后最需要重新验证的部分

HEVC 脚本依赖较长的硬件滤镜链：

```text
NVDEC  
→ P010  
→ hwupload  
→ scale_vulkan  
→ blend_vulkan  
→ hwdownload  
→ HEVC NVENC
```

新版 FFmpeg 可能修改：

- Vulkan Framesync 行为；
- Pixel Format 协商；
- `hwupload / hwdownload` 行为；
- P010 与 Vulkan Surface 的转换方式；
- `blend_vulkan` / `scale_vulkan` 的实现效率。

因此升级后至少使用一段固定测试片验证：

```text
画面是否正常  
Grain 方向是否正确  
亮度是否发生变化  
是否出现灰雾  
是否发生丢帧/重复帧  
最终时长是否完全一致
```

性能比较不要只看：

```text
fps=
```

而应优先比较：

```text
speed=  
elapsed=
```

特别是在 59.94 → 23.976 或 60 → 24fps 转换后，输出 FPS 本身已经变化，直接比较 `fps=` 数字没有意义。

### 4. 现有 Grain Cache 通常不需要重新生成

已经生成的：

```text
*_1080p_HEVC_Lossless.mkv  
*_HEVC_Lossless.mkv
```

本质上只是普通的 HEVC Main10 Lossless 文件。

升级 FFmpeg 或 NVIDIA 驱动后，正常情况下可以继续使用，不需要重新制作。

如果希望做严格验证，可以任选一个 Cache 再执行一次原脚本中的 P010 SHA-256 校验，确认新版解码器输出保持一致即可。

### 5. AV1 + grav1synth 需要重新验证最终 Grain 是否仍被保留

AV1 方案的核心链路是：

```text
av1_nvenc  
→ grav1synth apply  
→ FFmpeg stream-copy remux  
→ grav1synth inspect
```

升级 FFmpeg 后重点验证最终阶段：

```text
[4/4] Verifying Film Grain headers in FINAL file...
```

必须仍然得到：

```text
VERIFIED: AV1 Film Grain headers are present.
```

这样才能确认新 FFmpeg 的 Matroska remux 没有影响 AV1 Film Grain 信息。

`grav1synth.exe` 本身不需要因为外部 FFmpeg 升级而替换。

它的目录中包含自己的运行时 FFmpeg DLL 依赖，因此：

> 
> **不要把新 FFmpeg 9.x 目录里的 DLL 复制到 grav1synth 目录覆盖原 DLL。**
> 

外部 `ffmpeg.exe` 与 grav1synth 的内部依赖应该保持相互独立。

### 6. 自动电影帧率需要检查一次 CFR 行为

本文脚本使用：

```text
fps=24000/1001  
fps=24  
-fps_mode:v cfr
```

升级后建议确认：

```text
59.94 → 23.976  
60.00 → 24.000
```

最终：

- 总时长不变；
- 音频不变速；
- 无异常 A/V Sync；
- 无大量 `dup` / `drop`。

少量边界帧的 `drop` 属于正常 CFR 转换现象。

### 7. 升级后值得重新测试 CUDA ↔ Vulkan Zero-Copy

旧环境：

```text
596.49  
+  
N-124278-gcc3ca17127
```

曾尝试：

```text
NVDEC CUDA  
→ hwmap  
→ Vulkan
```

直接映射，但 Windows 下返回：

```text
Failed to map frame: -40  
Function not implemented
```

因此最终 HEVC 版本使用了已经验证稳定的：

```text
NVDEC  
→ System/P010  
→ Vulkan Upload
```

当升级到：

```text
610+ Driver  
+  
新 FFmpeg 9.x  
+  
新版 CUDA / Vulkan interop
```

后，可以重新测试 CUDA ↔ Vulkan `hwmap` / zero-copy。

如果新版能够稳定实现：

```text
NVDEC CUDA Surface  
        ↓ zero-copy  
Vulkan  
        ↓  
blend_vulkan  
        ↓  
NVENC
```

理论上可以进一步减少 CPU 和显存/系统内存之间的数据搬运。

但这一优化必须满足两个条件才值得合并进正式脚本：

1. **输出像素和时长完全正确；**
2. **实际 `speed=` / `elapsed=` 明显优于当前稳定版。**


如果只是 CPU 使用率下降、实际速度没有提高，就没有必要为了“零拷贝”牺牲稳定性。

### 8. 最推荐的升级验证方式

保留一段固定 Benchmark 视频，每次升级只测试这一段。

本文已经有一套可重复对比的参考数据：

```text
测试时长：12:07  
输入帧率：59.94  
输出帧率：23.976  
GPU：RTX 4080
```

当前参考成绩：

```text
HEVC + Real Grain  
speed ≈ 4.22x  
elapsed ≈ 2:52  
  
AV1 + grav1synth  
AV1 Encode speed ≈ 11.1x  
elapsed ≈ 1:05
```

升级后同时比较：

```text
画质  
Grain 外观  
最终码率  
最终文件大小  
speed=  
elapsed=  
CPU 占用  
GPU Video Decode  
GPU Video Encode
```

只有确认新版本在**稳定性、画质和性能上至少不退步**，才建议把它替换为新的正式环境。

---

## 十七、已知限制

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

  


 
