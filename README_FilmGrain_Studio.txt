Film Grain Studio - 使用说明
============================

这是 Universal Film Grain Toolkit 的图形前端。
CLI 与 GUI Bridge 共享同一套编码逻辑，并分别提供交互菜单和图形界面。
所有脚本采用固定文件名；项目版本只体现在发布压缩包文件名中。

启动方式
--------
双击：FilmGrain_Universal_HEVC_AV1_GUI.bat

也可以把一个或多个视频直接拖到 FilmGrain_Universal_HEVC_AV1_GUI.bat，
Studio 打开后会自动加入文件列表。

初版已实现
----------
- 多视频添加、拖放、移除和清空。
- 选中单个输入视频时显示视频/音频编码、码率、分辨率、帧率与时长。
- AV1 Main10 + grav1synth（默认）与 HEVC Main10 + Scanned Grain。
- MP4（默认）与 MKV。
- FAST（默认）与 Standard。
- 自定义 kbps 码率，以及 AV1/HEVC 对应的常用码率列表。
- 自动电影帧率（默认）或保持源帧率。
- Cinematic Style（默认开启）：
  AV1 使用约 2.39:1 active-picture crop；HEVC 使用约 2.39:1 letterbox。
- RTX 4080（默认）与 RTX T600 Laptop 兼容配置。
- AV1 Film preset / Photon ISO、Film 格式、Film stock、Chroma Grain。
- AV1 可选 H.264 社交网站上传版。
- HEVC Grain 根目录递归扫描：界面仅显示电脑上实际存在的 .mov 颗粒片。
- Grain 列表支持选择目录后自动刷新，也可点击 ↻ 手动刷新。
- HEVC Grain 4 档强度，并自动匹配同名 1080p / 4K Lossless Cache。
- CLI 与 GUI 均支持输入文件名或目录名包含 & 等 CMD 特殊字符。
- 拖放视频启动时，CMD 与 PowerShell 控制台均不驻留任务栏。
- 主界面直接读取 Gallery 最近使用与我的最爱，各显示最多 25 个 LUT，并复用缩略图即时预览。
- 直接调用现有 LUT Gallery，支持 25% / 50% / 75% / 100% LUT 强度。
- 内置任务日志、当前阶段、当前编码阶段进度、fps / speed / ETA 与取消任务。
- Studio 模式使用 FFmpeg 结构化实时进度，并兼容原有 -stats 文本解析。

默认设置
--------
编码：AV1 Main10 + grav1synth
容器：MP4 / AAC 256k
速度：FAST（p5 / qres / lookahead 16）
码率：AV1 1500 kbps；HEVC 7500 kbps
画幅：Cinematic Style 开启
帧率：自动电影帧率
GPU：RTX 4080
AV1 Grain：Classic35 / Fujifilm Eterna 250D
LUT：关闭

固定依赖与路径
--------------
FFmpeg：E:\EnCoder\FFMpeg\13.0\bin\ffmpeg.exe
FFprobe：E:\EnCoder\FFMpeg\13.0\bin\ffprobe.exe
grav1synth：E:\EnCoder\FFMpeg\grav1synth\grav1synth.exe
HEVC Grain 库：D:\Film_Grain
LUT 根目录：E:\Adobe Portable\LUTs

文件关系
--------
FilmGrain_Universal_HEVC_AV1_GUI.bat
    GUI 入口；解决 PowerShell 执行策略与 STA 启动问题。

Utils\FilmGrain_Studio.ps1
    WinForms 图形界面。

Utils\FilmGrain_Studio_Launcher.vbs
    无控制台启动器；异步启动 WinForms 后立即退出，不保留 CMD 任务栏窗口。

Utils\FilmGrain_Universal_HEVC_AV1_StudioBridge.bat
    GUI 参数桥接版核心，仅供 Studio 调用。

FilmGrain_Universal_HEVC_AV1_CLI.bat
    CLI 入口，可独立拖放使用，并与 GUI 同步特殊路径、帧率与 MP4 音频修正。

请保持两个入口 BAT、Utils 与 _LUT_Tools 文件夹的相对位置不变。

v0.1.1 修正
------------
- 修正部分 Windows / FFmpeg 环境中转换时 fps、speed 和 ETA 不更新的问题。
- 移除 GUI 中写死的 11 项 Grain 列表，改为读取当前 Grain 根目录的真实文件。
- GUI 将所选 Grain 完整路径直接交给 Studio Bridge；CLI 菜单保持独立可用。

v0.1.2 修正
------------
- 移除 GUI 对临时日志文件的运行时轮询，避免 cmd.exe 重定向文件被占用时无法读取。
- GUI 改为通过异步输出管道直接接收 Studio Bridge、FFmpeg 的 stdout / stderr。
- 输出管道每 200 ms 排空一次；后台读取错误不再静默忽略，而会写入任务日志。

v0.1.3 修正
------------
- 修正 Studio 自定义视频码率可能回退到 HEVC 7500 / AV1 1500 默认值的问题。
- GUI 直接传递已验证的 bitrate、maxrate（2 倍）和 bufsize（4 倍）。
- Studio Bridge 不再让 GUI 码率经过旧 BAT 菜单校验链，并在日志中回显实际采用值。

v0.1.4 更新
------------
- 输入列表选中单个视频时，使用 FFprobe 异步读取并显示视频、音频与容器信息。
- 已探测文件在本次 Studio 会话中缓存；切换选择时不会阻塞界面。
- 主输出选择 MP4 时，HEVC 与 AV1 的默认音频码率由 AAC 320k 改为 AAC 256k。
- 可选的 H.264 社交网站上传版仍保持其独立 AAC 320k 设置。

v0.1.5 修正
------------
- 补强“自动电影帧率”对 VFR 与非标准等价分数的识别。
- FFprobe 帧率先转换为数值，再匹配最接近的 NTSC 或整数帧率族。
- 约 29.97 / 59.94 等 NTSC 帧率输出为 23.976 CFR；约 30 / 60 等整数帧率输出为 24 CFR。
- 日志的 FPS choice 会标注经过 Normalized/VFR 判断的源帧率分数。

v0.1.6 修正
------------
- 修复输入文件名或目录名包含 & 等 CMD 特殊字符时编码命令被拆断的问题。
- AV1、HEVC、Grain 注入、重封装与上传版均改为直接执行命令，避免二次解析。

v0.1.7 修正
------------
- 拖放视频启动时，BAT 转交任务后立即退出；CMD 不会残留在任务栏。
- 保持原有拖放参数传递方式，中文、空格与特殊字符路径兼容逻辑不变。

v0.1.8 修正
------------
- 修正 v0.1.7 仅将 CMD 窗口隐藏到任务栏、没有真正结束的问题。
- BAT 将拖放参数交给无控制台 VBS 启动器后立即退出；VBS 异步启动 WinForms。
- PowerShell 编码进程不再附着于启动 BAT 的控制台窗口。

v0.1.9 修正
------------
- 修正 v0.1.8 把 PowerShell 创建的 WinForms 窗口也继承为隐藏状态的问题。
- VBS 以正常窗口状态创建独立 PowerShell，再由 PowerShell 仅隐藏自己的控制台。
- BAT 仍会立即退出，WinForms 正常显示且不保留 CMD 任务栏窗口。

v0.1.10 修正
-------------
- 修正 v0.1.9 仍会显示独立 Windows PowerShell 黑框的问题。
- VBS 从创建时隐藏 PowerShell 控制台；WinForms 在 Shown 事件后单独强制显示。
- CMD 与 PowerShell 控制台均不驻留任务栏，主 WinForms 保持正常显示。

v0.1.11 更新
-------------
- Film Look 区新增“最近使用”下拉框，直接读取 Gallery 的 `_LUT_GALLERY_RECENT.json`。
- 最多显示最近 5 个有效 LUT；选择后自动启用 LUT，并写回同一份 Recent 记录。
- 主界面复用 `_LUT_GALLERY_INDEX.json` 的预览映射与 `_GALLERY_THUMBS_v3_240x135` 缩略图缓存。
- 打开完整 LUT Gallery 返回主界面后，最近列表与缩略图会自动刷新。

v0.1.12 更新
-------------
- 修复 Gallery 已有 Recent 记录在主界面显示为空的问题。
- 最近 LUT 只要对应的 .cube 文件仍存在就会显示，不再依赖预览图是否成功匹配。
- 缩略图匹配增加 LUT 相对路径回退，兼容旧索引或绝对路径发生变化的情况。

v0.1.13 更新
-------------
- Recent 读取改为逐条容错；单条旧记录或异常路径不再导致整个列表变空。
- 优先使用 Gallery 索引中的当前 LUT 路径，与 Gallery 自身的 Recent 解析方式保持一致。
- Recent 读取异常会写入任务日志，不再静默显示为“暂无记录”。
- Studio LUT 预览固定为与 Gallery 单图相同的 240 × 135 像素并居中显示。

v0.1.14 更新
-------------
- 根目录只保留 CLI 与 GUI 两个 BAT 入口，并采用统一命名。
- GUI 内部脚本、Bridge 与其他独立工具全部归入 Utils；_LUT_Tools 保持原位。
- 修正 GUI、Bridge 和 LUT 预览生成器移动后的相对调用路径。
- 加入 BT.709 LUT 收集器，以及同分辨率/1080p 两个 HEVC 无损 Grain Cache 生成器。

v0.1.15 修正
-------------
- `_LUT_GALLERY_RECENT.json` 改为单写者设计：仅 LUT Gallery 可以写入。
- Studio 主界面的最近列表只读取和选择，不再改写或重排 Gallery 历史。
- Gallery 读取旧历史失败时放弃本次更新，禁止用空记录覆盖原文件。
- Gallery 通过同目录临时文件原子替换 Recent JSON，避免读取到半写入内容。

v0.1.16 更新
-------------
- Studio 主界面的最近使用由 5 条增加到最多 25 条，与 LUT Gallery 保持一致。
- 在最近使用下方新增“我的最爱”下拉框，最多读取 Gallery 的 25 个收藏 LUT。
- 两个列表选择 LUT 后均复用 Gallery 的 240 × 135 缩略图进行即时预览。
- Studio 对 Recent 与 Favorites 数据库均保持只读；LUT Gallery 仍是唯一写入者。

v0.1.17 修正与更新
------------------
- 修正从 Studio 主界面选择 LUT 并编码后，Gallery 最近使用不更新的问题。
- Studio 在真正开始编码时调用 Gallery 的无界面登记入口；Recent JSON 仍只由 Gallery 写入。
- LUT Gallery 当前页改为可输入的页码框，输入有效页码后按 Enter 直接跳转。
- Gallery 的 Prev / Next 以及 PageUp / PageDown 支持首尾循环翻页。

v0.1.18 修正与更新
------------------
- 废除 v0.1.17 不可靠的隐藏 Gallery 进程登记方式，改用 Studio 与 Gallery 共用的唯一 Recent 存储模块。
- 在 Studio 的“最近使用”或“我的最爱”中选中 LUT 后立即登记、置顶并刷新，无需等待开始编码。
- Recent 存储增加跨进程互斥锁、去重、25 条上限及同目录原子替换，避免两套界面互相覆盖。
- LUT Gallery 的标题、按钮、状态、操作提示、右键菜单及诊断信息统一改为中文。

v0.1.19 修正
-------------
- 修正 v0.1.18 从 Studio 打开 LUT Gallery 时可能直接返回代码 1 的问题。
- Gallery 改为在实际登记 Recent 时才载入共享存储模块，存储组件不再影响窗口启动。
- 翻页控件恢复为经过验证的固定坐标初始化，避免启动阶段的动态位置计算异常。
- Studio 会捕获并显示 Gallery 返回的 PowerShell 错误详情，后续不再只显示返回代码。
- Studio 的 LUT 区域及 Gallery 窗口统一采用中文界面文字。

v0.1.20 修正
-------------
- 将 GUI Bridge 已验证的直接命令执行方式完整同步到 CLI；HEVC、AV1 编码、
  grav1synth、重封装与上传版不再通过命令变量二次展开。
- 修复 CLI 输入文件名或目录名包含 & 等 CMD 特殊字符时命令被拆断的问题。
- 将 VFR/等价分数归一化同步到 CLI；19001/317 等约 59.94 fps 的源视频
  会输出为 23.976 CFR，并在日志显示 Normalized/VFR source。
- CLI 主输出 MP4 音频同步为 AAC 256k；可选 H.264 上传版仍保持 AAC 320k。
- 恢复 LUT Gallery 对 Recent 与 Favorites 数据库的唯一写权限。Gallery
  双击/确认 LUT 时立即写入 Recent；Studio 的两个下拉列表平时保持只读。
- 只有从 Studio“我的最爱”选择 LUT 并点击“开始编码”时，Studio 才调用
  Gallery 的无界面入口，由 Gallery 登记一次 Recent。

v0.1.21 稳定基线
----------------
- 固化已经实机确认可用的 LUT Gallery Recent 增量写入版本。
- Gallery 双击或确认 LUT 时立即加入 Recent；Studio 的 Recent 与 Favorites
  下拉列表平时只读。
- Studio 从 Favorites 选择 LUT 后，仅在点击“开始编码”时调用 Gallery 的
  无界面入口登记一次 Recent。
- 修正 Windows PowerShell 5.1 下 File.Replace 使用空备份路径导致已有
  Recent 无法更新的问题，改用有效的临时备份路径并在完成后清理。
- Recent 写入不再清洗或删除旧路径；旧数据非空但无法完整解析时拒绝覆盖，
  并在替换前校验记录数量，避免列表被意外重写成单条记录。
- 完整保留 v0.1.20 的 CLI 特殊字符路径、VFR 帧率归一化及 MP4 AAC 256k 修正。

初版说明
--------
- 输出保存在每个源视频所在目录；已有同名输出时跳过。
- 进度条按当前 FFmpeg 编码阶段计算；当前阶段、fps、speed 与 ETA 会从后台
  日志中更新。AV1 的 grav1synth、封装和校验阶段时间较短，不单独计算百分比。
- 强制取消时可能留下未完成输出或 __AV1GS_TMP_* 临时目录，便于排查。
- GUI 与 CLI 可分别使用；两者的核心兼容性修正保持同步。
