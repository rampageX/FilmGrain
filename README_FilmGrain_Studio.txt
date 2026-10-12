Film Grain Studio - 使用说明
============================

当前产品版本：v4.8.16；构建 Stable。

运行：双击根目录 FilmGrain_Studio.exe，或将视频拖入EXE。
同源构建位置：dotnet\build\FilmGrain_Studio_NET_Preview.exe；两处EXE内容必须一致。
运行时为.NET；准备失败报告实际原因，不调用归档脚本。
保留共享资源和实际算法依赖：FFmpeg/ffprobe、grav1synth、Python/VapourSynth、SVP插件/.vpy、FGSIM模板/纹理。

本版新增：OpenSVPFlow Speed/Tuning/Block Size；默认FGS/FGS/auto保持既有参数。
FRUC核对实际构建能力及设备，默认medium/auto保持；悬停提示简短，完整错误在任务日志。
高级设置恢复默认统一在确定左侧；插帧说明使用tooltip，插件更新在底部左侧。
本地/正式构建均对实际EXE执行Framework API审计，失败停止；开发构建BAT/PS1仍可保留。

用户配置：FilmGrain_Config.ini，使用时生成；测试/发布包不携带个人配置。
源码：dotnet；共享资源：Lang、Utils、_LUT_Tools、_AV1_Grain_Tables、_OpenSVPFlow。
Legacy仅为历史归档，不作为当前产品运行入口，也不维护兼容参数接口。
日志：按任务记录完整诊断；失败请保留日志及实际参数。

已验收：OpenSVPFlow本轮人工项目、设置布局、FRUCtooltip；RTX4080指定FFmpeg下1080p/4K/剪切TS共18个FRUC观察案例，TS同步正常。
结论仅覆盖实际样本和路径，未测硬件/HDR/NVENC/字幕/LUT/颗粒等组合不登记通过。
版本变更与详细边界见README.md、CHANGELOG.md和.github/release/RELEASE_NOTES.md。

发布：由维护者指定目标版号运行release.bat；正式Stable由Windows流程重新构建。
此工作包为发布准备，不代表已提交、创建Tag、推送或正式发布。Dry Run不等于发布。
