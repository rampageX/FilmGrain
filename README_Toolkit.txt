Film Grain Studio - 文件与发布说明
================================

当前产品版本：v5.0.23；构建 Stable。

正式运行入口：FilmGrain_Studio.exe。
Stable.zip 为精简便携运行包，包含默认配置、Lang、必要 Utils 资源、颗粒表、LUT 参考图和 OpenSVPFlow 脚本／插件及第三方声明。
首次运行按提示指定或安装 FFmpeg/FFprobe 与所需依赖；初始运行包不附带个人配置或完整私有环境。

Source.zip 为对应版本的完整源码交付，保留 dotnet、Legacy、images、发布脚本与工作流；不附带编译 EXE、benchmark 报告、本机配置、缓存、日志及已安装依赖。
Source 与 Stable 使用同一份已提交源码归档，SOURCE_BUILD_INFO.txt 记录版本、源码 commit 和对应运行 EXE 的 SHA256。
Legacy 为历史归档，不作为当前应用入口。运行资源保持原位置。

本机构建：dotnet\build_NET_Preview.bat。
构建后根 EXE 与 dotnet\build\FilmGrain_Studio_NET_Preview.exe 必须一致，Stable 只附带根 EXE。
正式发布：release.bat --dry-run <版本号>，通过后由维护者执行 release.bat <版本号>。
正式 release.bat 调用统一构建核心，在当前开发目录生成 Stable、Source 及各自 SHA256，再原子推送源码分支与 Tag。
Windows runner 重新构建双包，Release 页面仅自动上传 Stable 和对应 SHA256；Source 保留在工作流 Artifact 中。
仓库同步保留完整项目，运行包精简不用于删除仓库源码。
准备包或 Dry Run 成功不代表正式发布；提交、Tag 和远程发布由维护者执行。

开发目录与日常运行副本分开。新窗口开发交接使用当前磁盘导出的 Working_Source 包，以保留未提交修改和新文件；发布后没有修改时也可使用正式 Source 包。

NVENC CQ：主界面勾选 CQ 模式，复用“自动”复选框显示推荐数值；也可直接填写 CQ。数值越低目标质量越高，码率和体积随内容变化。
高级页可分别设置 AV1 / HEVC / H.264 CQ 最高码率（默认 80 / 100 / 120 Mbps），可修改或关闭。VBR 与 CQ、各编码器及文件独立配置分别保留。
工具提示支持深浅主题与长文本换行。
