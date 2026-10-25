# v5.0.32 验收记录

基线为用户上传 Working_Source_20261025_121125_985，包含已验收 T9M4XC；在此基础上加入 K8V3QN 任务栏进度。用户分别确认两项功能可以，并要求发布新版。

- T9M4XC：队列重置单选/多选、统一/独立模式；状态为“等待”，仅重置所选队列状态，运行中禁用。
- K8V3QN：复用现有 coordinator 进度，不改编码核心；未知进度动画、确定进度同步、完成/失败/取消/启动失败清除，窗口与任务栏重建恢复。
- Microsoft .NET Framework 4.8 参考程序集编译及 Framework API 审计通过；该验证不等同于 Windows runner 正式打包。
- 用户人工确认通过；未收到逐场景日志，不将 Explorer 重启等所有边界记为已实测。

版本同步为 5.0.32 / 5.0.32.0 / Stable。正式 C# 与 K8V3QN 相比仅变更两处版本常量。README 写用法，CHANGELOG/RELEASE_NOTES 写变化。发布白名单改为本版验收记录；排除本轮临时交付包和说明，不要求新增测试清理脚本。

正式包须由现有 Windows runner 构建。当前环境未执行 Windows release Dry Run、推送、打标签或 GitHub 发布。
