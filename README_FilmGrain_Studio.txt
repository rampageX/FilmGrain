Film Grain Studio - 使用说明
============================

当前正式稳定版：v4.6.2

Film Grain Studio 是 Universal Film Grain Toolkit 的图形前端。GUI 与 CLI 共用 Utils\FilmGrain_Universal_HEVC_AV1_StudioBridge.bat 编码核心。

启动：双击 FilmGrain_Universal_HEVC_AV1_GUI.bat，也可以把一个或多个视频直接拖到 GUI BAT 上。

主编码路线：AV1 Main10 + grav1synth、HEVC Main10 + 扫描 Grain、H.264 x264 Grain。默认 AV1 Main10 / MP4 / AAC 256k。

HDR Preserve（v4.6.2）
--------------------
支持 HEVC Main10 / AV1 Main10 HDR Preserve。检测并保持 BT.2020、PQ/ST2084、HLG/ARIB STD-B67、BT.2020 non-constant、Color Range 和 10-bit。HEVC 强制实际 P010 10-bit。源本来有 Mastering Display / MaxCLL / MaxFALL 时保留；源没有时不伪造。最终输出检查 Primaries / Transfer / Matrix / Range。

当前 HDR 安全限制：OpenSVPFlow 当前为 YUV420P8，HDR 自动旁路；H.264 x264 主输出不作为 HDR Preserve；H.264 上传版对 HDR 自动跳过；现有 LUT 路线对 HDR 自动旁路。暂不提供 HDR->SDR Tone Mapping、Dolby Vision RPU、HDR10+ 动态 metadata。

GUI 选中单个 HDR 文件并完成媒体探测后，会灰显当前不兼容的 OpenSVPFlow、H.264 上传版和 LUT 区域；切回 SDR 文件后恢复。

LUT Gallery 智能过滤（v4.6.2）
----------------------------
分类 Technical / Combined / Creative / Keep，只有 Technical 被隐藏。识别 Input/Output Color Space、Utility/Technical/Transform/Conversion、CST/IDT/ODT、Log->Rec.709、Tone/Gamut/Range、Shaper 和同一创意 Look 的多输入版本家族。Combined LUT 保留。智能过滤只改变图库显示，不移动、不删除、不改名原 LUT。报告写入 <LUT_ROOT>\_LUT_PREVIEWS\_LUT_SMART_FILTER_REPORT.csv。

AV1 Grain Table：按 720p / 1080p / 1440p / 2160p 分类。公开来源：
https://github.com/Boulder08/chunknorris
https://github.com/nekotrix/AV1-Photon-Noise-Tables

OpenSVPFlow 首次安装：_OpenSVPFlow\00_Setup.bat
主动更新：_OpenSVPFlow\01_Update_OpenSVPFlow.bat
正式包不预装用户本机更新后的 DLL，也不携带 _PluginBackup。

反交错默认 BWDIF Vulkan；29.97i -> 59.94p，25i -> 50p。隔行素材自动旁路 OpenSVPFlow。

x264 Grain 默认 libx264 / preset faster / tune grain / VBR single-pass / High 8-bit；高级设置支持 Medium / Slow / VBR 2-Pass / High10（实验）。

LUT Gallery 支持搜索、文件夹过滤、最近使用、我的最爱、分页、页码下拉、LUT 强度、更换参考图和智能过滤。LUT_Reference_Current.jpg 是用户运行状态，正式包默认不存在。

FilmGrain_Config.ini 统一管理 FFmpeg、grav1synth、Grain Root、LUT Root。

FilmGrain_Hardware_Caps.ps1 会实际探测 NVENC、Main10、AQ、Lookahead、Multipass、Vulkan、x264 Grain、AV1 UHQ、AV1 SFE、OpenSVPFlow GPU/OpenCL。运行时生成 Utils\_HardwareCaps.json，正式包默认不包含。

正式包已清理 _HardwareCaps.json、LUT_Reference_Current.jpg、OpenSVPFlow 本机 DLL/version state、_PluginBackup、临时 Patch/TEST/HOTFIX 文件和测试构建标记。

历史变更请查看 CHANGELOG.md。
