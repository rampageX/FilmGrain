# v5.0.21 发布验收与交接

v5.0.20 已由维护者发布。本版仅修复通用工具提示的位置，不重新宣称 CQ 为新增功能。

## 变更与验证

- H.264 / HEVC Lookahead 和 OpenSVPFlow 调教预设的长提示按指针所在显示器工作区定位，靠边时调整位置；识别原生及 WindowsForms10.tooltips_class32.app.* 提示类名。
- Windows 返回 Return_Hint_Q7M3VN_R2_H6P8TC_20261022_071235_538.zip：354 项通过、0 失败；32 个实际悬停场景，32 张截图齐全。四个控件各 8 组，覆盖双显示器、深浅主题、普通/靠边位置，验证指针命中、实际窗口位置、屏幕边界、换行和主题像素。
- R1 的窗口漏识别失败未算作通过。R2 完整测到 H.264 Lookahead、HEVC Lookahead、OpenSVPFlow 调教预设及 H.264 B 帧对照。
- 本轮版本修正仅修改版本常量和文档；重建后执行 Framework API 审计。候选程序校验见下方构建记录。
- 编码与队列代码未改，不重复视频编码。既有 CQ 实测和 FRUC/SVP/驱动重试边界见 CQ_AUDIT_v5.0.20.md。实际 Windows 混合 DPI 未穷举，本地几何检查覆盖 96/144/192 DPI。

## 发布步骤

1. 关闭 FGS，覆盖完整源码或相对最初上传 v5.0.19 的累计补丁；包内不携带个人 FilmGrain_Config.ini。
2. 运行 release.bat --dry-run 5.0.21。
3. 成功后由维护者运行 release.bat 5.0.21；正式包 FilmGrain_Studio_v5.0.21_Stable.zip。

本环境未执行正式 Windows Dry Run，未 commit/tag/push/发布。不清理测试文件，不新增清理脚本；测试文件均留在 Tests_Tooltip_Q7M3VN 内，该目录已从正式发布布局排除。.gitignore、用户配置和原有图片资源保持不变。

## 构建记录

产品版本 5.0.21，文件/程序集版本 5.0.21.0；Microsoft Framework API 审计 2304 项解析、0 失败；根目录与 dotnet/build 两份 EXE 字节一致，SHA256：`6c5b91a52da5fc8690255eba29f7c2f76c6e622a739de5621440ba5fe64b3226`。
