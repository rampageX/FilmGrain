Film Grain Studio - 使用说明
============================

当前产品版本：v4.8.17；构建 Stable。

运行：双击根目录 FilmGrain_Studio.exe，或将视频拖入EXE。
同源构建位置：dotnet\build\FilmGrain_Studio_NET_Preview.exe；两处EXE内容必须一致。
运行时为.NET；准备失败报告实际原因，不调用归档脚本。
保留共享资源和实际算法依赖：FFmpeg/ffprobe、grav1synth、Python/VapourSynth、SVP插件/.vpy、FGSIM模板/纹理。

本版新增默认开启的“启用颗粒”；关闭后保留参数，不主动添加FGS颗粒，插帧/调色/LUT/字幕等继续执行。
开关保存并重启恢复，旧INI缺项默认开启；关闭时跳过实际颗粒处理、依赖及仅供颗粒的资源。
x264主输出及实际libx264上传副本关闭自动tune grain；无颗粒命名不带本次颗粒标记。
HDR依据实际处理判定，未启用的FGSIM不自动使任务转SDR；其它处理限制及明确HDR策略保持。
AV1不重编码专用模式固定颗粒开启；进入/离开恢复普通模式偏好。检测/查看独立。
关闭不是降噪，不消除已有像素颗粒，不承诺普通AV1重编码原样保留输入Film Grain metadata。
普通无颗粒剪切TS及HEVC/SVP的共同起点、前导音频和上传副本时间轴修复已纳入。
保留已验收OpenSVPFlow高级参数、FRUC诊断/tooltip和Framework强制成品API审计。

用户配置：FilmGrain_Config.ini，使用时生成；测试/发布包不携带个人配置。
源码：dotnet；共享资源：Lang、Utils、_LUT_Tools、_AV1_Grain_Tables、_OpenSVPFlow。
Legacy仅为历史归档，不作为当前产品运行入口，也不维护兼容参数接口。
日志：按任务记录完整诊断；失败请保留日志及实际参数。

已验收：用户确认本轮G7N2VX及累计补测通过，包含普通剪切TS、FRUC及V3K8MS的HEVC/AV1＋SVP。
结果仅覆盖既定实际测试范围；软件检查、Windows执行和人工观察分别记录，未测组合不登记通过。
本轮未重复此前整套FRUC观察矩阵；后续技术上可共用的三线处理优先统一实现。
版本变更与详细边界见README.md、CHANGELOG.md和.github/release/RELEASE_NOTES.md。

发布：由维护者指定目标版号运行release.bat；正式Stable由Windows流程重新构建。
此工作包为发布准备，不代表已提交、创建Tag、推送或正式发布。Dry Run不等于发布。
