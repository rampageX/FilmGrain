# Film Grain Studio v5.0.63

- HDR AUTO 支持逐行 10-bit 4:2:0 BT.2020/PQ HDR10 与 HLG 使用 OpenSVPFlow GPU16 或 CPU10 高位深插帧，保留源色彩标签。隔行 HDR/HLG 使用 2x 场率反交错并绕过插帧器。不兼容的 HDR 组合按实际路线转 SDR。
- 修复 SVP-GPU 状态栏误报。点击可强制复检，悬停可查看失败原因，插件更新成功后自动刷新一次。HDR 插帧输出名以 C/G 区分 CPU10 与 GPU16。
- 输入区可打开配置的自定义输出目录，同名输出冲突时直接显示提示。
- 随包提供用户实测的 OpenSVPFlow `nightly-20261106-badc0de392ac` DLL 对及 SHA-256 清单。两只 DLL 均与上游同标签 Windows 发布资产逐字节一致。
