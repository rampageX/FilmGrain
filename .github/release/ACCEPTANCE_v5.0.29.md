# v5.0.29 验收记录

日期：2026-10-23。唯一工作基线为 Working_Source_20261023_105141_632(1).zip，包内 v5.0.28；保留上传时尚未提交的代码。未经远程覆盖，未提交、推送、打Tag或发布。

## 问题与最终实现

真实TS样本音频首PTS/DTS=1.400000，视频首PTS=3.142822、DTS=3.122822，time_base均1/90000。音频867包、视频920包，无DTS倒退，首视频可解码关键帧；约1.742822秒音频先行不能单独视为损坏。未收到SolveigMM另存版本，不宣称完成与该版本逐包对照。

普通CPU单遍开启颗粒时曾直接执行request.DirectArguments，绕过阶段时间轴回调；该路径现统一建立Stage。普通SDR的CPU/NVENC补偿仅在共同NativeExecutionPreparationCore入口绑定一次，OrdinaryTimelineBound防重复。FGSIM设备选择在最终时间轴命令生成后执行。AV1所有最终封装恢复原始音画关系；HEVC/H264的TS OpenSVPFlow颗粒路线使用已有共享管道补偿。HDR/反交错/插帧保留原有专用处理，不叠加普通补偿。

每次运行独立TimelineProbeScope，完成/失败/取消均清除。时间轴探测按MediaProbeIdentity复用（文件与FFprobe路径、大小、写入及创建时间），身份变化则重新探测；不跨任务共享。不新增视频预处理或临时媒体。生产中间文件仍由原TemporaryFiles/TemporaryDirectories清理。GUI/CLI继续共用任务准备入口，未改变预设、队列、冻结快照、共用预览参数、硬件缓存或UI。

## 自动与人工验收

- Windows R1回传 Return_Timeline_T7M4QX_R1_N6P8VX_20261023_112857.zip：反射转换自检通过；4类输入×4编码器×3共享适配路线，共48项通过，最大事件误差19ms。编码器为libx264/h264_nvenc/hevc_nvenc/av1_nvenc。这是共享方法级测试，不等于每一项GUI完整流程。
- Windows R2回传 Return_ProductionTimeline_R2_Q4N8VK_20261023_115949.zip：从共同NativeExecutionPreparationCore准备真实任务，SVT/x264＋Plate blend_vulkan＋50→24fps，正常/剪切/合法音频提前×MKV/MP4，共12项通过；最大事件误差28ms，小于24fps一帧。MP4实际AAC转码、MKV复制音频。重复绑定检查通过，每项时间轴探测unique=1、reused=2。测试执行生产准备及最终命令，未走全部GUI事件或BridgeExecutionCore所有运行分支。
- P确认真实素材SVT与FRUC人工实测通过；随后补测FGSIM通过并同意准备发布。人工结果不扩展为所有容器/编码器/高级参数组合。
- Linux已测独立FFmpeg同步标记算术；本环境无Windows/.NET及Vulkan驱动，正式Windows证据以上述P回传为准。不能据此新增全部HDR元数据保留或T600兼容承诺。

## 发布准备

本版仅整理版本与文档、清理临时测试；除Program版本常量外，发布候选生产C#与R2验收包逐字节一致。版本5.0.29 / 5.0.29.0 / Stable同步，README写用法、CHANGELOG与Release Notes写变化。
移除Tests_Timeline_T7M4QX、根测试BAT、回传结果和零散审计文件；本记录是本轮唯一新增正式验收文档。发布白名单仅依赖本版验收记录及正式构建文件，不依赖测试或清理工具。
正式打包仍使用既有Windows构建流程。GitHub Release只上传Stable及其SHA256；本地发布生成Stable/Source及各自SHA256。当前环境未运行最终Windows release Dry Run，交由维护者执行既有release.bat dry-run 5.0.29后正式发布。

上传快照未包含images目录，无法在当前离线快照完成该目录的布局验证。准备包按覆盖现有仓库使用，保留维护者本地最新images；不从旧包或远程补图、不取消RequiredPackageDirectories中的images校验。维护者Windows Dry Run将验证实际完整仓库。
