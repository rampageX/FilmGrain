Universal Film Grain Toolkit
=============================

正式稳定版：v4.6.2

所有主脚本使用固定正式文件名。版本号只体现在项目发布包和 GUI 底部版本标识中，升级时建议完整替换整个工具包。

GUI：FilmGrain_Universal_HEVC_AV1_GUI.bat
CLI：FilmGrain_Universal_HEVC_AV1_CLI.bat
共用核心：Utils\FilmGrain_Universal_HEVC_AV1_StudioBridge.bat

主线：AV1 Main10 + grav1synth、HEVC Main10 + 扫描 Grain、H.264 x264 Grain。AAC 标准码率 256k。

HDR Preserve：v4.6.2 支持 HEVC/AV1 HDR，保持 10-bit、BT.2020、PQ/HLG、BT.2020 non-constant 和 color range；源本来有 HDR10 Mastering Display / MaxCLL / MaxFALL 时保持，源本来没有时不创建虚构值。HEVC 显式强制 p010le，输出完成后校验 HDR 色彩信号。HDR 当前自动旁路 OpenSVPFlow 8-bit 插帧、H.264 x264 HDR 主输出、H.264 上传版和现有 SDR/BT.709 LUT 处理。

AV1 Film Grain 支持 Film Preset、Photon ISO、现成 .tbl/.txt、grav1synth 添加/替换、最终 Film Grain 验证、SFE；SFE 要求 grav1synth 0.2.2+。

现成 Grain Table：_AV1_Grain_Tables\720p / 1080p / 1440p / 2160p。公开来源：
https://github.com/Boulder08/chunknorris
https://github.com/nekotrix/AV1-Photon-Noise-Tables

HEVC 扫描 Grain 使用真实 Grain Plate，经 Vulkan 合成到像素，再由 HEVC Main10 NVENC 编码。

H.264 x264 Grain 默认 preset faster / tune grain / VBR single-pass / High 8-bit；可选 Medium / Slow / VBR 2-Pass / High10（实验）。

LUT Gallery 支持 Recent、Favorites、文件夹、搜索、分页、页码下拉、强度、更换参考图和智能过滤。智能过滤分类 Technical / Combined / Creative / Keep，只隐藏 Technical；报告为 <LUT_ROOT>\_LUT_PREVIEWS\_LUT_SMART_FILTER_REPORT.csv。

OpenSVPFlow 首次安装 _OpenSVPFlow\00_Setup.bat，主动更新 _OpenSVPFlow\01_Update_OpenSVPFlow.bat。正式包只保留安装/更新/检查脚本与 Plugins 占位文件。

反交错默认 BWDIF Vulkan；29.97i -> 59.94p，25i -> 50p；隔行素材自动旁路 OpenSVPFlow。

AV1/HEVC 可选同时生成 H.264 上传版；默认 x264 Faster + tune grain + VBR 单次，AAC 256k。HDR 输入当前不生成上传版。

AV1 免重编码工具：Utils\AV1_Grav1synth_Add_Replace_FilmGrain_NoReencode.bat
社交网站烘焙工具：Utils\AV1_FilmGrain_Bake_for_Social_Upload.bat
LUT 缩略图：Utils\LUT_Preview_Batch_Gallery.bat

FilmGrain_Config.ini 统一管理 FFmpeg、grav1synth、Grain Root、LUT Root。Utils\FilmGrain_Hardware_Caps.ps1 运行后产生 Utils\_HardwareCaps.json，正式包不包含该机器相关缓存。

正式包不包含测试 Patch/HOTFIX/TEST 构建文件、_HardwareCaps.json、LUT_Reference_Current.jpg、OpenSVPFlow 用户 DLL/_PluginBackup、Recent/Favorites/Smart Filter Report 等用户状态。

完整说明请查看 README.md，版本变更请查看 CHANGELOG.md。
