# Film Grain Studio v4.8.11

* .NET 界面增加首次运行设置：检查 FFmpeg / ffprobe 和 grav1synth 路径；提供指定位置或下载并安装。FFmpeg 可由用户选择是否使用系统 PATH 中的版本。设置版本与快速路径检查共同决定是否再次提示；耗时硬件能力测试继续使用缓存。
* 新增 `--setup-test=missing` 启动参数：首次扫描模拟外部依赖缺失，指定路径后可真实验证；使用独立临时配置，不覆盖日常 `FilmGrain_Config.ini`，测试模式不执行真实安装。
* 可选 OpenSVPFlow 插帧安装采用 `_OpenSVPFlow\.venv`；Python 默认安装在 FGS 的 `_Dependencies` 中，只有用户主动勾选时才尝试系统 Python。插帧环境缺失且用户启用插帧时，.NET 界面提示进入设置。

### 改进与修复

* FFmpeg 使用 gyan.dev Full 构建，grav1synth 下载 Windows 修订版的完整运行目录；下载支持本次安装的 HTTP 代理，成功后更新配置中的工具路径。
* VapourSynth 配置保存在 FGS 内的 `_OpenSVPFlow\_UserConfig`，安装验证、硬件能力检测及插帧编码使用相同私有配置，避免写入用户个人 VapourSynth 配置。
* .NET 和正式 PS1 的路径配置窗口在 Grain Plate、LUT 标签旁增加 `[?]`，通过系统默认浏览器打开项目 README 相应章节；加宽标签列，修正中文标签换行。
* Grain Plate 和 LUT 继续作为用户自备素材目录；.NET、正式 PS1 GUI 与 CLI 沿用共享配置及原有编码后端。
