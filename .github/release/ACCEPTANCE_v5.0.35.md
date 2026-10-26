# v5.0.35 验证记录

- 基于本会话 v5.0.34 已验证源码及 R4_T7Q9VN 累计修改。
- 界面改动集中于 FilmGrain_SettingsDialogs.cs，共用 BuildEncoderOptions 贯通整合和独立窗口。
- 删除重复控件后保留内部配置值，未删除主界面自动质量、目标质量和码率模式能力；方案切换及恢复默认规则保留。
- Microsoft .NET Framework 参考程序集编译通过，API 审计2499项解析、0失败；两处测试EXE一致。
- 用户确认 R4 显示效果通过，授权发布新版。
- 正式EXE及发布ZIP由原Windows runner构建；本准备包不包含非Windows生成的EXE。
