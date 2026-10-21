Film Grain Studio - 文件与发布说明
================================

当前产品版本：v5.0.21；构建 Stable。

正式运行入口：FilmGrain_Studio.exe。
同源构建入口：dotnet\build\FilmGrain_Studio_NET_Preview.exe；两处成品必须一致。
源码在 dotnet；共享资源在 Lang、Utils、_LUT_Tools、_AV1_Grain_Tables、_OpenSVPFlow。
Legacy 为历史归档，不作为当前应用入口。保留实际依赖与共享资源原位置。

本机构建：dotnet\build_NET_Preview.bat。
正式发布：release.bat --dry-run <版本号>，通过后 release.bat <版本号>。
正式 Stable 使用 Windows runner 和统一发布核心构建，要求 Framework API 审计通过。
发布准备源码包不带旧版 EXE，正式发布流程会从此源码重新构建两处 EXE。
配置、缓存、日志、私有环境、下载、临时输出和测试目录不进入正式包。
准备包或 Dry Run 成功均不代表已提交、创建 Tag、推送或正式发布。

NVENC CQ：主界面勾选 CQ 模式，复用“自动”复选框显示推荐数值；也可直接填写 CQ。数值越低目标质量越高，码率和体积随内容变化。
高级页可分别设置 AV1 / HEVC / H.264 CQ 最高码率（默认 80 / 100 / 120 Mbps），可修改或关闭。VBR 与 CQ、各编码器及文件独立配置分别保留。
工具提示支持深浅主题与长文本换行。
