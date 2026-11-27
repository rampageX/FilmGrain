# FGS 胶片边框 · 集成准备与延期任务

> 开发基准：`test/film-border-s4`，已验收 V4：`d73656f71e766e5537ccaa47a4206a66e2edcb6c`；导入规范版本：`9694621fbc2fb896266a14e2752734f0b617d2dc`。本文件记录集成边界，并不是正式发布授权。

## 当前集成范围（已完成开发与用户验收）

- 主界面“添加边框”按钮、独立暗色 GPU 边框设置/预览、样式下拉框，以及导入/删除/经典文字编辑。
- 原版 **Dark 35mm**、**FGS Classic Black**（含齿孔、可编辑文字）及两款各自的画窗几何。**不得调整既有视觉、颜色、运动默认值**。
- 静态/动态独立选择；幅度、速度、随机频率三个参数，默认均为 1；与编码设置一同保存。
- 已接入的正式编码路径保持原样；不扩大 AV1、HDR→SDR 或插帧的支持范围。
- 用户边框格式和机器校验见docs/film-border/ 目录 [FILM_BORDER_ASSET_SPEC.md](FILM_BORDER_ASSET_SPEC.md)；用户导入款采用 1920×1080 RGBA8、固定透明画窗 1680×748。

## 用户导入素材 v1.1：放宽画窗与 Alpha

- 自定义 PNG 画布继续固定 1920×1080，用户选定**居中画窗宽 1630–1730 / 高 698–798**；不再要求中心 Alpha=0，可用半透明图层叠加染色、静态颗粒和污渍。为防止遮蔽视频，完全不透明像素不能占选定画窗 95% 以上。
- 导入时以暗色数字控件编辑宽高，在 `.aperture` 文件中保存，与 UTF-8 `.label` 和 RGBA 原图一起存储；旧导入素材缺尺寸文件默认 1680×748。
- GPU 实时预览和正式合成都使用素材的宽高；保留内置 Dark/Classic 原本的画窗及性能路线。详情见 [FILM_BORDER_ASSET_SPEC.md](FILM_BORDER_ASSET_SPEC.md)。

## 用户导入款描述名

选择 PNG 后显示独立的暗色“边框名称”对话框，预填源文件名，允许用户输入 1–40 字的描述名（不允许空白或控制字符）；确认导入后保存为 GUID 对应的 UTF-8 `.label`。下拉菜单与动、静模式均显示用户名称，GUID 仅用于内部标识。导入命名时拒绝重名（忽略大小写，并检查 Windows 文件名转义后的冲突）。取消不写任何素材。原用户导入的 `.label` 继续兼容。

## 输出文件名：独立样式后缀

- Dark 35mm：静态 `_FILM35S`、动态 `_FILM35D`（保留旧版）。
- FGS Classic Black：静态 `_FILMCLASSIC35S`、动态 `_FILMCLASSIC35D`。
- 用户导入样式：`_FILMUSER_<边框名称>_S` / `_FILMUSER_<边框名称>_D`，不附加短 ID/GUID。导入名称必须唯一（不区分大小写，并检查文件名非法字符替换后的重名）。
- H.264 NVENC、HEVC NVENC、x264、x265 统一使用 `FilmBorderEncodingCore.Plan.FileSuffix`；未开启边框时不添加后缀。文件名后缀与边框画面逻辑无关。

### FGB-04 · SVT-AV1 胶片边框与 Film Grain 联合接入（TODO）

**状态：延期，未开放。** 待 SVT-AV1 的 Film Grain 能力进入可供 FGS 使用和验证的 Windows 构建后，再一起规划和测试 SVT-AV1 胶片边框。需要分别覆盖普通 SVT 编码、AV1 参数颗粒/像素颗粒、SDR/HDR、边框插入顺序、合成速度及音视频时间戳；现有 SVT 禁用边框的编码保护不得提前移除。

**当前交互：** NVENC_AV1、SVT-AV1、AV1 仅替换颗粒均禁用主界面“添加边框”，显示灰色“暂不可用”。AV1 编码的有效任务配置不携带之前保留的边框，切回 HEVC/H.264/x265/x264 后恢复用户原选择。此次不改任何实际编码滤镜。
## 明确延期（不要在这次整理中开启）

### FGB-01 · HDR→SDR GPU 预览统一修复

**状态：延期，未实现。** 输入 PQ/HLG，选择 H.264 NVENC / x264 等输出 SDR 的编码路线时，“GPU 高级预览”和“添加边框”的 HDR→SDR 预览均被前置限制；编码端也存在边框与 HDR→SDR 的限制。不是简单删掉判断：必须沿用实际编码所选的 Hable/libplacebo GPU 色调映射或非 Hable CPU 回退，预览以 BT.709 SDR 显示，并验证与成片一致。

同时处理 **HDR 画面拖动期间亮度下降/停止后恢复（约 <500ms）** 的旧问题：开启 1:1 并持续拖动画面时出现，停止后亮度恢复；v5.0.76 原版、关闭边框后仍可复现。不得归因于新增边框。两个问题合并作为一个独立 HDR 预览专题，不在本轮修改。

### FGB-02 · 边框与插帧组合

**状态：延期，未实现。** 当前 `FilmBorderEncodingCore.TryCreate` 明确阻止边框＋插帧组合。后续设计需分别验证 FRUC / OpenSVPFlow、SDR/HDR、静态/动态、时钟/时间戳与帧率对齐；不得直接移除限制而不验证完整输出。

### FGB-03 · 用户素材人工验收

**状态：待提供符合规范的真实用户 PNG。** `Test_FilmBorderImportSpec.ps1` 的自动化正反例测试已通过。尚未对用户自行制作的素材完成界面导入、修改、删除、GPU 预览与正式成片的整体验收。无样本时不追加人为测试，也不假定已通过。

## Dark 35mm 原版资源归档（2026-11-20 已处理）

用户提交并核验原版素材：`cinema35_dark_1920x1080.png` 和 `cinema35_dark_1920x1080_rgba.raw`。PNG 为 1920×1080 RGBA；RAW 为 8,294,400 字节，两者 Alpha 逐字节一致；非透明区域的 RGB 可能有少量舍入差异，**不能重新由 PNG 生成 RAW 替代用户原件**。

- PNG 已受 Git 管理，SHA256 `921d54afb402c4eb66aa85bdd9343e85455eab885c09fff3b26a5c15f2c830d0`。
- 原始 RAW 为减小源码归档体积，**无损压缩**为 `Utils/_FilmBorders/cinema35_dark_1920x1080_rgba.raw.gz` 并受 Git 管理，SHA256（展开原件）`d51d6f9496c939d6b8b8ab60e5cd96bddc4111497ba7e9d010669b479b781b66`。
- `dotnet/BuildTools/Restore_FilmBorderDarkAssets.ps1` 在 Windows 测试与正式构建 staging 时解压，三份文件均做 SHA256 校验。**最终 Stable ZIP / 测试整合包提供原版 PNG 和原版 RAW**，不会要求用户自行解压 gzip，也不会改变色彩。
- 对全新源码工作目录，在直接启动/调试 FGS 前可运行 `powershell -ExecutionPolicy Bypass -File dotnet/BuildTools/Restore_FilmBorderDarkAssets.ps1` 生成运行缓存。运行缓存不参与 Git 源码归档，gzip 归档源和 PNG 才是对应的源码版本。

## 发布前必须解决的资源缺口

**原版 Dark 两份资源已归档；此前缺失的资源问题已解决。** 正式打包脚本已将两者列入 `.github/release/build_release.ps1` 的 `RuntimeFiles` 强制清单，并校验尺寸／字节数；缺少原版文件将阻断 Stable 构建，不能静默发不完整版本。新的测试 ZIP 和 Stable 构建均会包含以下文件：

- `Utils/_FilmBorders/cinema35_dark_1920x1080.png`
- `Utils/_FilmBorders/cinema35_dark_1920x1080_rgba.raw`（1920×1080×4 字节，RGBA 顺序）

用户导入的 `User/`、自定义边印 `classic-text.txt`、Classic 运行时生成 PNG/RAW 以及 Dark 解压后的本地 RAW 缓存已加入 `.gitignore`；**原版 Dark PNG 与精确 RAW 的 gzip 归档均受 Git 管理**。

内置 Classic Black 继续按代码首次生成；Dark 使用用户提供、SHA256 锁定的原版素材。正式 Stable 的新目录验收尚需用户确认，**不因 CI 成功而自行发布**。

`dotnet/BuildTools/Test_FilmBorderIntegrationReadiness.ps1` 的常规模式验证源码/配置一致性并记录素材缺失；`-RequireDarkAssets` 为正式发布前的强制检查。

## 集成/发布步骤边界

1. **当前阶段：**只做去重、格式清理、文档归档、自动化构建；不改已验收效果。编译通过只代表代码和自动化检查通过。
2. 保持 `master`、正式 Release、旧 Stable 不变。当前 S3/S4 专用 GitHub Actions 属于测试分支的验证设施，正式合并前应评估是否排除或移入持续回归。
3. Dark 原版素材归档已经完成；完成完整新目录安装的集成候选验收后，未经明确确认不合并 master / 不创建 Release。
4. FGB-01、FGB-02、FGB-03 均为已记录的延期项，不应在本轮以未经验证的代码绕过限制。

## Expand Canvas artwork mapping

The catalog aperture remains the canvas geometry contract. The artwork renderer
uses the PNG alpha to find where stock intrudes into that nominal aperture.
Lumen Ice 320 declares a bottom split at source row 942, inside its complete
lower perforations. The alpha transition puts the split at row 904. A shared
three-scanline check across the middle of each edge also handles similar
asymmetry in other stocks.

Fit the recovered rails at a shared scale and extend the existing quiet seams
where the picture aspect needs extra width or height. Keep an even motion guard
on both sides of the artwork so the translated print remains outside the fixed
picture rectangle and inside the canvas. The preview and both encoding graphs
use the same guards. The Vulkan overlays retain the U4C8 AVTB compensation.
