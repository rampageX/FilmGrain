# Film Grain Studio v4.8.6

本版主要完成用户配置统一与正式发布流程整理。

- 所有用户自定义配置统一保存到根目录 `FilmGrain_Config.ini`，包括 GUI 语言、LUT Gallery 智能过滤状态和高级设置四页参数。
- 高级设置“编码 / 插帧 / HDR / 其他”分别支持独立恢复默认，不影响其它页面。
- 旧版 `Lang\FilmGrain_Language.ini` 仅用于一次性迁移，正式包不再携带。
- 正式包随附当前已验证的 OpenSVPFlow 插件 DLL 与版本状态文件；`.env`、`_PluginBackup` 和 `_HardwareCaps.json` 等本机运行状态继续排除。
- `release.bat` 调整为本地发布工具，不再由 Git 跟踪，也不进入 Stable ZIP。
- AV1 / HEVC / x264 编码核心及既有 Grain、HDR、LUT、反交错、OpenSVPFlow、码率和 AAC 256k 策略保持不变。

完整功能说明见 `README.md`，版本历史见 `CHANGELOG.md`。
