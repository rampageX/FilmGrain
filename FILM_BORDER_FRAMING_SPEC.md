# FGS Film Border · 画面构图规范（S4 测试分支）

## 参数及默认值

- 配置键：`FILM_BORDER_FOCUS_X`、`FILM_BORDER_FOCUS_Y`（均为 0–1），`FILM_BORDER_ZOOM`（1–3）。
- 预览进程参数：`--film-focus-x`、`--film-focus-y`、`--film-zoom`。
- 编码状态键（GUI/CLI）：`FG_FILM_BORDER_FOCUS_X`、`FG_FILM_BORDER_FOCUS_Y`、`FG_FILM_BORDER_ZOOM`。
- 默认值：`(0.5, 0.5, 1.0)`；默认 FFmpeg CPU filter graph 保留 S4 原公式。
- `FocusX/Y` 表示希望位于画窗中心的源画面归一化位置；实际取景矩形会按源画幅边界钳制。

## 几何定义

输入帧 `W×H`，所选边框实际画窗 `A×B`，倍率 `Z`：

```text
cover = max(A/W, B/H)
scale = cover * Z
sourceWidth  = A / scale
sourceHeight = B / scale
left = clamp(FocusX*W - sourceWidth/2, 0, W-sourceWidth)
top  = clamp(FocusY*H - sourceHeight/2,0, H-sourceHeight)
```

图像只在画窗内采样；RGBA PNG 原坐标、文字和齿孔固定。动态 Gate Weave 由原运动模块在合成完成后叠加。CPU 分支沿用原 `scale(force_original_aspect_ratio=increase)` / `crop` / `pad`；非中性参数才启用新的动态裁剪表达式。HDR GPU libplacebo 分支沿用原内容裁剪和色彩元数据设置，只更新图片源裁剪及放置尺寸。HDR GPU 正式编码统一使用 Vulkan/libplacebo P010 → `hwdownload,format=p010le` → NVENC（编码器内部上传 CUDA），包括 1080p、4K 旧版及 VFR 非旧版画窗路线，避免 RTX 4080 已复现的 Vulkan→CUDA 外部内存映射失败。保持 10-bit、PQ/HLG 标签及原画窗/动态算法；需要每帧显存↔主存传输，待 RTX 4080 实测性能。

滚轮缩放时保留鼠标当前的原始采样坐标（除非触及边界）；拖动通过取景矩形尺寸换算源像素位移并钳制。缩放范围和焦点限制在本地与命令行解析处一致。切换样式、静态/动态不清空焦点。

## 操作与持久化

1. 在独立「添加边框」窗口点击「画面构图」启用鼠标编辑。此模式下拖动只移动边框透明画窗中的视频；双击不会切换原 1:1 检视。
2. 滚轮以鼠标位置为中心连续缩放；下方滑杆调节 1.00–3.00 倍，X/Y 文本框输入 0–1 坐标，Enter 或失去焦点确认。
3. 「恢复居中」重设为 `0.5/0.5/1.0`。「应用到 FGS」一次保存所有参数；关闭或取消不修改配置。
4. 已保存参数经队列状态快照传给 CPU / GPU 编码核心；高级 GPU 预览读取同一套配置。

## 自动检查与延期项

- Windows runner 构建原生 GPU 预览和 .NET 主程序，调用 D3D11 WARP 边框审计、`--audit-border-framing`、`Test_FilmBorderFraming.ps1` 以及原 S4 回归，并输出一个完整测试和源码包。
- 原生数学审计包含 1080p、UHD、自定义画窗上下限、1/1.5/2/3×、五种焦点坐标、边界钳制和鼠标锚点。
- 实机目视验证：RTX 4080 上 HDR10/HLG 的预览与正式输出及不同编码器同取景。
- FGB-01 HDR→SDR GPU 预览和拖动亮度呼吸、FGB-02 与插帧组合、FGB-03 自定义素材完整兼容验收均不在本次范围。
